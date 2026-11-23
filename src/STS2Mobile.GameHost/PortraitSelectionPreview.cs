using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Portrait layout for the deck's card selection screens: upgrade, enchant, transform and the generic deck select. The
/// before/after comparison and the multi-card preview rows are fitted into the canvas, the prompts (and the enchant legend)
/// stand under them above the grid's footer, the comparison's Confirm and Cancel are drawn at 44 points at the right and left
/// above the footer, and the picking prompt and View Upgrades box hide while a comparison is open. The transform preview's
/// composition is fitted once the game has built it (its scale is put back to 1 whenever it closes, so the game's own
/// initialisation never reads it). Every edit is captured first so turning Portrait Mode off restores the desktop layout live.
/// <para>All numbers are canvas units on the 1080-wide portrait canvas: <see cref="PortraitPreviewMath"/> converts the
/// reference design's 1680-canvas values.</para>
/// </summary>
internal sealed class PortraitSelectionPreview
{
    public const string UpgradeTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckUpgradeSelectScreen";
    public const string EnchantTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckEnchantSelectScreen";
    public const string TransformTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckTransformSelectScreen";
    public const string DeckSelectTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckCardSelectScreen";

    public static readonly HashSet<string> WatchedTypeNames = new(StringComparer.Ordinal)
    {
        UpgradeTypeName, EnchantTypeName, TransformTypeName, DeckSelectTypeName,
    };

    private const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string CardTypeName = "MegaCrit.Sts2.Core.Nodes.Cards.NCard";

    private enum Kind
    {
        Upgrade, Enchant, Transform, Select,
    }

    /// <summary>One comparison panel: its container, the pair or row inside it and its two buttons.</summary>
    private sealed class Panel
    {
        public Control Container = null!;
        public Control? Pair;
        public Control? Before;
        public Control? After;
        public Control? Arrows;
        public Control? Row;
        public Control? Transform;
        public Button? Confirm;
        public Button? Cancel;
        public bool Shown;
        public bool TransformFitted;
        public int TransformTries;
        public Vector2 TransformCanvas;
        public int RowCount = -1;
        public Vector2 PairFit;
        public float RowScale;
    }

    /// <summary>A preview or Back button: where the game shows it, how it was grown, and its own move tween.</summary>
    private sealed class Button
    {
        public Control Control = null!;
        public bool IsBack;
        public PortraitControlSnapshot Snapshot = null!;
        public readonly List<Descendant> Descendants = new();
        public Vector2 HomeShow, HomeHide;
        public bool HomesKnown;
        public bool Grown;
        public float Applied;
        public FieldInfo? ShowField, HideField, MoveTweenField;
        public PropertyInfo? EnabledProperty;
        public Vector2 Target = new(float.NaN, float.NaN);
    }

    private readonly record struct Descendant(
        Control Node,
        float AnchorLeft, float AnchorTop, float AnchorRight, float AnchorBottom,
        float OffsetLeft, float OffsetTop, float OffsetRight, float OffsetBottom);

    private sealed class ScreenState
    {
        public Kind Kind;
        public Control Screen = null!;
        public Control? Prompt;
        public Control? Toggle;
        public Control? Legend;
        public ColorRect? LegendBacking;
        public Color LegendColor;
        public Button? Close;
        public Button? ScreenConfirm;
        public readonly List<Panel> Panels = new();
        public bool FooterHidden;
        public bool PromptHome, ToggleHome;
        public bool HomesKnown;
        public bool Failure;
        public bool Logged;
        public float CapHeight;
        public Vector2 Canvas;
    }

    private readonly Dictionary<ulong, ScreenState> _screens = new();
    private readonly Dictionary<ulong, Button> _buttons = new();
    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private static FieldInfo? _cardDefaultSize;
    private static Type? _cardType;

    /// <summary>Whether this control belongs to a deck selection screen laid out here (the overlay keeps its hands off).</summary>
    internal static bool Owns(Control control)
    {
        for (Node? node = control; node != null; node = node.GetParent())
            if (node is Control owner && WatchedTypeNames.Contains(owner.GetType().FullName ?? string.Empty)) return true;
        return false;
    }

    /// <summary>Called for whichever of the four screens enters the tree, on every layout pass.</summary>
    public void ApplyNode(Control screen, string typeName, float capHeight, Vector2 canvas)
    {
        Kind kind = KindOf(typeName);
        ulong id = screen.GetInstanceId();
        if (!_screens.TryGetValue(id, out ScreenState? state) || state.Screen != screen)
        {
            _screens[id] = state = Build(screen, kind);
            state.CapHeight = capHeight;
            state.Canvas = canvas;
        }
        state.CapHeight = capHeight;
        state.Canvas = canvas;
        Update(state);
    }

    /// <summary>
    /// Per frame: a comparison opens, closes or fills with cards after the game's own input and deferred calls, so the panel
    /// that became visible is fitted before it is drawn and the prompt/toggle follow it. Everything else early-outs.
    /// </summary>
    public void OnProcessFrame(Window root, float capHeight)
    {
        Vector2 visible = root.GetVisibleRect().Size;
        var canvas = new Vector2(visible.X, Math.Max(visible.Y, SafeAreaCanvas.MinPortraitHeight));
        foreach (ScreenState state in _screens.Values)
        {
            if (!GodotObject.IsInstanceValid(state.Screen) || !state.Screen.IsVisibleInTree()) continue;
            state.CapHeight = capHeight;
            state.Canvas = canvas;
            Update(state);
        }
    }

    private static Kind KindOf(string typeName) => typeName switch
    {
        UpgradeTypeName => Kind.Upgrade,
        EnchantTypeName => Kind.Enchant,
        TransformTypeName => Kind.Transform,
        _ => Kind.Select,
    };

    private ScreenState Build(Control screen, Kind kind)
    {
        var state = new ScreenState
        {
            Kind = kind,
            Screen = screen,
            Prompt = screen.GetNodeOrNull<Control>("%BottomText") ?? PromptLabelParent(screen),
            Toggle = screen.GetNodeOrNull<Control>("ViewUpgrades"),
        };
        switch (kind)
        {
            case Kind.Upgrade:
                AddPanel(state, "%UpgradeSinglePreviewContainer", "UpgradePreview", null);
                AddPanel(state, "%UpgradeMultiPreviewContainer", null, "Cards");
                break;
            case Kind.Enchant:
                AddPanel(state, "%EnchantSinglePreviewContainer", "EnchantPreview", null);
                AddPanel(state, "%EnchantMultiPreviewContainer", null, "Cards");
                state.Legend = screen.GetNodeOrNull<Control>("%EnchantmentDescriptionContainer");
                break;
            case Kind.Transform:
                AddPanel(state, "%PreviewContainer", null, null, "TransformPreview");
                state.ScreenConfirm = TrackButton(ConfirmOf(screen));
                break;
            case Kind.Select:
                AddPanel(state, "%PreviewContainer", null, "Cards");
                state.ScreenConfirm = TrackButton(ConfirmOf(screen));
                break;
        }
        state.Close = TrackButton(screen.GetNodeOrNull<Control>("%Close"));
        if (state.Legend?.GetNodeOrNull<ColorRect>("ColorRect") is { } backing)
        {
            state.LegendBacking = backing;
            state.LegendColor = backing.Color;
        }
        return state;
    }

    /// <summary>The prompt label's box, for a screen whose prompt is not wrapped in its own container.</summary>
    private static Control? PromptLabelParent(Control screen)
    {
        if (screen.GetNodeOrNull<Control>("%BottomLabel") is not { } label) return null;
        return label.GetParent() is Control parent && parent != screen ? parent : label;
    }

    private static Control? ConfirmOf(Control screen) =>
        screen.GetNodeOrNull<Control>("%Confirm") ?? screen.GetNodeOrNull<Control>("Confirm");

    private void AddPanel(ScreenState state, string containerName, string? pairName, string? rowName, string? transformName = null)
    {
        if (state.Screen.GetNodeOrNull<Control>(containerName) is not { } container) return;
        var panel = new Panel { Container = container };
        if (pairName != null && container.GetNodeOrNull<Control>(pairName) is { } pair)
        {
            panel.Pair = pair;
            panel.Before = pair.GetNodeOrNull<Control>("Before");
            panel.After = pair.GetNodeOrNull<Control>("After");
            panel.Arrows = pair.GetNodeOrNull<Control>("Arrows");
        }
        if (rowName != null) panel.Row = container.GetNodeOrNull<Control>(rowName);
        if (transformName != null) panel.Transform = container.GetNodeOrNull<Control>(transformName);
        panel.Confirm = TrackButton(container.GetNodeOrNull<Control>("Confirm"));
        panel.Cancel = TrackButton(container.GetNodeOrNull<Control>("Cancel"));
        state.Panels.Add(panel);
    }

    private Button? TrackButton(Control? control)
    {
        if (control == null || !GodotObject.IsInstanceValid(control)) return null;
        ulong id = control.GetInstanceId();
        if (_buttons.TryGetValue(id, out Button? existing)) return existing;
        var button = new Button
        {
            Control = control,
            IsBack = control.GetType().Name.Contains("BackButton", StringComparison.Ordinal),
            Snapshot = Capture(control, captureVisible: false),
            ShowField = FindField(control.GetType(), "_showPos"),
            HideField = FindField(control.GetType(), "_hidePos"),
            MoveTweenField = FindField(control.GetType(), "_moveTween"),
            EnabledProperty = control.GetType().GetProperty("IsEnabled", InstAll),
        };
        _buttons[id] = button;
        return button;
    }

    private static FieldInfo? FindField(Type type, string name)
    {
        for (Type? t = type; t != null && t != typeof(object); t = t.BaseType)
            if (t.GetField(name, InstAll) is { } field) return field;
        return null;
    }

    // ---- One screen, one frame ---------------------------------------------------------------------------------------------

    private void Update(ScreenState state)
    {
        if (state.Failure) return;
        try
        {
            float width = state.Canvas.X, height = state.Canvas.Y;
            bool anyShown = false;
            foreach (Panel panel in state.Panels)
            {
                if (!GodotObject.IsInstanceValid(panel.Container)) continue;
                bool visible = panel.Container.Visible;
                if (visible)
                {
                    anyShown = true;
                    if (!panel.Shown) Open(state, panel);
                    else Maintain(state, panel);
                }
                else if (panel.Shown)
                {
                    Close(state, panel);
                }
            }

            if (anyShown) HideFooter(state);
            else ShowFooter(state);

            PlaceFooter(state);
            EnsureButtons(state);
            if (!state.Logged && state.Panels.Count > 0)
            {
                state.Logged = true;
                GD.Print($"Portrait selection preview: {state.Screen.GetType().Name} prompt top " +
                         $"{PromptTop(state, height):F0}, {state.Panels.Count} comparison panel(s), canvas {width:F0}x{height:F0}.");
            }
        }
        catch (Exception error)
        {
            state.Failure = true;
            GD.PrintErr($"Portrait selection preview: {state.Screen.GetType().Name} left as the game lays it out after an error: ", error);
            try
            {
                ShowFooter(state);
            }
            catch (Exception)
            {
                // Its own layout stays.
            }
        }
    }

    private void Open(ScreenState state, Panel panel)
    {
        panel.Shown = true;
        float width = state.Canvas.X, height = state.Canvas.Y;
        if (panel.Pair != null)
        {
            FitPair(panel, width, PortraitPreviewMath.PreviewCenterY(
                state.CapHeight, height, state.Kind == Kind.Enchant
                    ? PortraitPreviewMath.EnchantCenterFraction
                    : PortraitPreviewMath.UpgradeCenterFraction));
        }
        if (panel.Row != null) FitRow(panel, width, PortraitPreviewMath.PreviewCenterY(
            state.CapHeight, height, PortraitPreviewMath.RowCenterFraction));
        if (panel.Row != null && state.Kind == Kind.Select)
        {
            // The generic deck select sets its row's pivot and its count-based scale deferred, after its own input: fit once
            // more after that call, so the first frame it is drawn already carries the portrait fit.
            Callable.From(() =>
            {
                if (panel.Shown && GodotObject.IsInstanceValid(panel.Container) && panel.Container.Visible)
                    FitRow(panel, width, PortraitPreviewMath.PreviewCenterY(
                        state.CapHeight, height, PortraitPreviewMath.RowCenterFraction));
            }).CallDeferred();
        }
        if (panel.Transform != null)
        {
            panel.TransformFitted = false;
            panel.TransformTries = 0;
            FitTransform(state, panel);
        }
    }

    private void Close(ScreenState state, Panel panel)
    {
        panel.Shown = false;
        panel.RowCount = -1;
        if (panel.Transform != null && panel.TransformFitted)
        {
            panel.TransformFitted = false;
            // Put the game's own scale back before its next Initialize reads the composition's position.
            if (_snapshots.TryGetValue(panel.Transform.GetInstanceId(), out PortraitControlSnapshot? snap))
                panel.Transform.Scale = snap.InitialScale;
            else
                panel.Transform.Scale = Vector2.One;
        }
    }

    private void Maintain(ScreenState state, Panel panel)
    {
        float width = state.Canvas.X, height = state.Canvas.Y;
        if (panel.Pair != null && GodotObject.IsInstanceValid(panel.Pair))
        {
            float scale = PortraitPreviewMath.PairScale(width);
            float centre = PortraitPreviewMath.PreviewCenterY(state.CapHeight, height,
                state.Kind == Kind.Enchant ? PortraitPreviewMath.EnchantCenterFraction : PortraitPreviewMath.UpgradeCenterFraction);
            if (Math.Abs(panel.PairFit.X - scale) > 0.0005f || Math.Abs(panel.PairFit.Y - centre) > 0.5f)
                FitPair(panel, width, centre);
        }
        if (panel.Row != null && GodotObject.IsInstanceValid(panel.Row))
        {
            int count = RowCards(panel.Row);
            float centre = PortraitPreviewMath.PreviewCenterY(state.CapHeight, height, PortraitPreviewMath.RowCenterFraction);
            if (count != panel.RowCount || Math.Abs(panel.Row.Scale.X - panel.RowScale) > 0.0005f ||
                panel.Row.PivotOffset != Vector2.Zero ||
                Math.Abs(panel.Row.Size.X - PortraitPreviewMath.RowLocalWidth(width, count)) > 0.5f)
                FitRow(panel, width, centre);
        }
        if (panel.Transform != null)
        {
            if (panel.TransformFitted && panel.TransformCanvas != state.Canvas)
            {
                // The canvas changed under an open composition: put its own scale back, measure it again and fit.
                panel.Transform.Scale = _snapshots.TryGetValue(panel.Transform.GetInstanceId(), out PortraitControlSnapshot? snap)
                    ? snap.InitialScale
                    : Vector2.One;
                panel.TransformFitted = false;
                panel.TransformTries = 0;
            }
            if (!panel.TransformFitted && ++panel.TransformTries < 30) FitTransform(state, panel);
        }
    }

    // ---- The comparison's fits ---------------------------------------------------------------------------------------------

    private void FitPair(Panel panel, float width, float centerY)
    {
        Control pair = panel.Pair!;
        float scale = PortraitPreviewMath.PairScale(width);
        Capture(pair);
        PlaceTopLeft(pair, ToLocal(pair, new Vector2(width * 0.5f, centerY)), scale);
        if (panel.Before != null)
        {
            Capture(panel.Before);
            panel.Before.Position = new Vector2(-PortraitPreviewMath.PairCardOffset, panel.Before.Position.Y);
        }
        if (panel.After != null)
        {
            Capture(panel.After);
            panel.After.Position = new Vector2(PortraitPreviewMath.PairCardOffset, panel.After.Position.Y);
        }
        if (panel.Arrows != null)
        {
            Capture(panel.Arrows);
            panel.Arrows.Scale = Vector2.One * PortraitPreviewMath.PairArrowScale;
        }
        panel.PairFit = new Vector2(scale, centerY);
    }

    private static readonly string[] BeforeAfter = ["Before", "After"];

    /// <summary>
    /// Places a control from its top left, grown to this scale, without touching anything the game would not change again
    /// (an unchanged placement is skipped so a per-frame pass writes nothing in the steady state).
    /// </summary>
    private static void PlaceTopLeft(Control control, Vector2 point, float scale, float width = float.NaN)
    {
        bool resize = !float.IsNaN(width) && Math.Abs(control.Size.X - width) > 0.5f;
        if (!resize && Math.Abs(control.Scale.X - scale) < 0.0005f && control.AnchorLeft == 0f && control.AnchorTop == 0f &&
            control.AnchorRight == 0f && control.AnchorBottom == 0f && control.PivotOffset == Vector2.Zero &&
            Math.Abs(control.Position.X - point.X) < 0.25f && Math.Abs(control.Position.Y - point.Y) < 0.25f)
            return;
        control.AnchorLeft = control.AnchorTop = control.AnchorRight = control.AnchorBottom = 0f;
        control.PivotOffset = Vector2.Zero;
        control.Scale = new Vector2(scale, scale);
        control.Position = point;
        if (resize) control.Size = new Vector2(width, control.Size.Y);
    }

    private void FitRow(Panel panel, float width, float centerY)
    {
        Control row = panel.Row!;
        int count = RowCards(row);
        if (count <= 0) return;
        PreviewRowFit fit = PortraitPreviewMath.PreviewRow(width, count, centerY);
        Capture(row);
        PlaceTopLeft(row, ToLocal(row, new Vector2(fit.FirstCenterX, centerY)), fit.Scale, fit.LocalWidth);
        if (row.GetThemeConstant("separation") != (int)PortraitPreviewMath.RowSeparation)
            row.AddThemeConstantOverride("separation", (int)PortraitPreviewMath.RowSeparation);
        panel.RowCount = count;
        panel.RowScale = fit.Scale;
    }

    /// <summary>The cards a preview row shows, ignoring ones the game has queued for freeing.</summary>
    private static int RowCards(Control row)
    {
        int count = 0;
        for (int i = 0; i < row.GetChildCount(); i++)
            if (row.GetChild(i) is Control card && !card.IsQueuedForDeletion()) count++;
        return count;
    }

    private void FitTransform(ScreenState state, Panel panel)
    {
        Control preview = panel.Transform!;
        if (!GodotObject.IsInstanceValid(preview) || !preview.IsInsideTree()) return;
        if (CardDefaultSize() is not { } cardSize) return;
        Transform2D inverse = preview.GetGlobalTransform().AffineInverse();
        Vector2 extent = Vector2.Zero;
        foreach (string side in BeforeAfter)
        {
            if (preview.GetNodeOrNull<Control>(side) is not { } holder) continue;
            AddCardExtent(holder, inverse, cardSize, ref extent);
        }
        if (extent.X <= 0.5f || extent.Y <= 0.5f) return;
        float scale = PortraitPreviewMath.TransformScale(
            preview.GlobalPosition.X, preview.GlobalPosition.Y,
            0f, state.CapHeight, state.Canvas.X, state.Canvas.Y, extent.X, extent.Y);
        Capture(preview);
        preview.Scale = Vector2.One * scale;
        panel.TransformFitted = true;
        panel.TransformCanvas = state.Canvas;
        if (_logged.Add(preview.GetInstanceId()))
            GD.Print($"Portrait selection preview: transform composition {extent.X:F0}x{extent.Y:F0} fitted at {scale:F2}.");
    }

    private readonly HashSet<ulong> _logged = new();

    private static void AddCardExtent(Node node, Transform2D inverse, Vector2 cardSize, ref Vector2 extent)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child.GetType().FullName == CardTypeName && child is Control card)
            {
                Vector2 centre = inverse * card.GlobalPosition;
                Vector2 edge = centre.Abs() + cardSize * card.GetGlobalTransform().Scale.Abs() * 0.5f;
                extent = new Vector2(MathF.Max(extent.X, edge.X), MathF.Max(extent.Y, edge.Y));
            }
            AddCardExtent(child, inverse, cardSize, ref extent);
        }
    }

    private static Vector2? CardDefaultSize()
    {
        if (_cardDefaultSize != null) return _cardDefaultSize.GetValue(null) is Vector2 size ? size : null;
        _cardType ??= typeof(PortraitSelectionPreview).Assembly.GetType(CardTypeName);
        _cardDefaultSize = _cardType?.GetField("defaultSize", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        return _cardDefaultSize?.GetValue(null) is Vector2 found ? found : null;
    }

    /// <summary>A canvas point in the control's parent's own coordinates (the preview containers are not always at the origin).</summary>
    private static Vector2 ToLocal(Control control, Vector2 canvas)
    {
        if (control.GetParent() is not Control parent || !parent.IsInsideTree()) return canvas;
        return parent.GetGlobalTransform().AffineInverse() * canvas;
    }

    // ---- The footer: prompt, legend and the hidden toggle ------------------------------------------------------------------

    private float PromptTop(ScreenState state, float height) => state.Kind switch
    {
        Kind.Enchant when state.Legend is { } legend =>
            PortraitPreviewMath.PromptTopAboveLegend(
                PortraitPreviewMath.LegendTop(height, legend.Size.Y),
                state.Prompt?.Size.Y ?? 0f,
                PortraitPreviewMath.FooterPromptScale(state.Canvas.X, state.Prompt?.Size.X ?? 0f)),
        Kind.Transform => PortraitPreviewMath.TransformPromptTop(height),
        Kind.Select => PortraitPreviewMath.SelectPromptTop(height),
        _ => PortraitPreviewMath.UpgradePromptTop(height),
    };

    private void PlaceFooter(ScreenState state)
    {
        float width = state.Canvas.X, height = state.Canvas.Y;
        if (state.Kind == Kind.Enchant)
        {
            if (state.Legend is not { } legend || legend.Size.Y < 1f) return;
            Capture(legend);
            if (state.LegendBacking != null && state.LegendBacking.Color.A < 1f)
                state.LegendBacking.Color = new Color(state.LegendBacking.Color, 1f);
            PlaceTopLeft(legend, ToLocal(legend, new Vector2(PortraitPreviewMath.LegendX,
                PortraitPreviewMath.LegendTop(height, legend.Size.Y))), PortraitPreviewMath.LegendScale,
                PortraitPreviewMath.LegendLocalWidth(width));
            if (legend.GetNodeOrNull<Control>("MarginContainer/HBoxContainer/VBoxContainer") is { } column &&
                column.SizeFlagsHorizontal != Control.SizeFlags.ExpandFill)
                column.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        }
        if (state.Prompt is not { } prompt || prompt.Size.X < 1f || prompt.Size.Y < 1f) return;
        Capture(prompt);
        // The enchant prompt packs under its legend with its own cap; the others grow to 2.2 of their size.
        float promptScale = state.Kind == Kind.Enchant
            ? PortraitPreviewMath.FooterPromptScale(width, prompt.Size.X)
            : PortraitPreviewMath.PromptScale(width, prompt.Size.X);
        PlaceTopLeft(prompt, ToLocal(prompt,
            new Vector2(PortraitPreviewMath.CenteredX(width, prompt.Size.X, promptScale), PromptTop(state, height))), promptScale);
    }

    /// <summary>
    /// The picking prompt and View Upgrades box hide while a comparison is open (they describe the grid behind it); the
    /// enchant legend stays, as it belongs to the comparison.
    /// </summary>
    private void HideFooter(ScreenState state)
    {
        if (state.FooterHidden) return;
        state.FooterHidden = true;
        if (!state.HomesKnown)
        {
            state.HomesKnown = true;
            state.PromptHome = state.Prompt?.Visible ?? false;
            state.ToggleHome = state.Toggle?.Visible ?? false;
        }
        if (state.Prompt != null && GodotObject.IsInstanceValid(state.Prompt)) state.Prompt.Visible = false;
        if (state.Toggle != null && GodotObject.IsInstanceValid(state.Toggle)) state.Toggle.Visible = false;
    }

    private void ShowFooter(ScreenState state)
    {
        if (!state.FooterHidden) return;
        state.FooterHidden = false;
        if (state.Prompt != null && GodotObject.IsInstanceValid(state.Prompt)) state.Prompt.Visible = state.PromptHome;
        if (state.Toggle != null && GodotObject.IsInstanceValid(state.Toggle)) state.Toggle.Visible = state.ToggleHome;
    }

    // ---- Confirm, Cancel and the upgrade screen's Close -------------------------------------------------------------------

    private void EnsureButtons(ScreenState state)
    {
        float width = state.Canvas.X, height = state.Canvas.Y;
        foreach (Panel panel in state.Panels)
        {
            Place(panel.Confirm, width, PortraitPreviewMath.ButtonTop(height));
            Place(panel.Cancel, width, PortraitPreviewMath.ButtonTop(height));
        }
        Place(state.ScreenConfirm, width, PortraitPreviewMath.ButtonTop(height));
        Place(state.Close, width, state.Kind == Kind.Upgrade
            ? PortraitPreviewMath.CloseTop(height)
            : PortraitPreviewMath.ButtonTop(height));
    }

    private void Place(Button? button, float width, float top)
    {
        if (button == null || !GodotObject.IsInstanceValid(button.Control)) return;
        Control control = button.Control;
        if (control.Size.Y < 1f) return;
        Grow(button);
        var target = new Vector2(
            button.IsBack ? 0f : PortraitPreviewMath.ConfirmLeft(width, control.Size.X),
            top);
        if (button.ShowField == null) return;
        if (!button.IsBack) target = ToLocal(control, target);
        if (!float.IsNaN(button.Target.X) && button.Target.DistanceSquaredTo(target) < 0.25f) return;
        button.Target = target;
        if (!button.HomesKnown && button.ShowField.GetValue(control) is Vector2 show)
        {
            button.HomesKnown = true;
            button.HomeShow = show;
            button.HomeHide = button.HideField?.GetValue(control) is Vector2 hide ? hide : show;
        }
        button.ShowField.SetValue(control, target);
        Vector2 hidden = target + (button.IsBack
            ? new Vector2(-(control.Size.X + 80f * PortraitPreviewMath.Unit), 0f)
            : new Vector2(control.Size.X + 100f * PortraitPreviewMath.Unit, 0f));
        button.HideField?.SetValue(control, hidden);
        // A shown button slides to its portrait point (the game's own entrance feel, and no jump if it was already on its way
        // out; a hidden one is parked clear of the screen at once, since it cannot be seen).
        if (button.EnabledProperty?.GetValue(control) is true) Move(button, control, target);
        else
        {
            if (button.MoveTweenField?.GetValue(control) is Tween moving && GodotObject.IsInstanceValid(moving)) moving.Kill();
            if (button.IsBack) control.GlobalPosition = hidden;
            else control.Position = hidden;
        }
        if (_logged.Add(control.GetInstanceId()))
            GD.Print($"Portrait selection preview: {control.GetType().Name} of {OwnerName(control)} at " +
                     $"({target.X:F0}, {target.Y:F0}), {control.Size.X:F0}x{control.Size.Y:F0}.");
    }

    /// <summary>Slides a composed button to its portrait point with the game's own entrance curve, owning its move tween.</summary>
    private static void Move(Button button, Control control, Vector2 point)
    {
        if (button.MoveTweenField?.GetValue(control) is Tween old && GodotObject.IsInstanceValid(old)) old.Kill();
        Tween tween = control.CreateTween();
        tween.TweenProperty(control, button.IsBack ? "global_position" : "position", point, 0.35)
            .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
        if (button.MoveTweenField != null && button.MoveTweenField.FieldType.IsAssignableFrom(typeof(Tween)))
            button.MoveTweenField.SetValue(control, tween);
    }

    private static string OwnerName(Control control) => control.GetParent()?.GetType().Name ?? control.GetParent()?.Name ?? "?";

    /// <summary>
    /// Grows a button's box (and everything in it) to the reference's size for touch, from its top left, keeping its picture
    /// in proportion. The game animates the button's scale, so the box is grown rather than the node.
    /// </summary>
    private void Grow(Button button)
    {
        if (button.Grown) return;
        Control control = button.Control;
        float factor = PortraitPreviewMath.ButtonScale(control.Size.Y, button.IsBack);
        if (Math.Abs(factor - button.Applied) < 0.0005f) return;
        button.Grown = true;
        button.Applied = factor;
        if (factor <= 1.0005f) return;
        Vector2 position = control.Position, size = control.Size;
        Collect(control, button.Descendants);
        control.AnchorLeft = control.AnchorTop = control.AnchorRight = control.AnchorBottom = 0f;
        control.Position = position;
        control.Size = size * factor;
        control.PivotOffset *= factor;
        foreach (Descendant child in button.Descendants)
        {
            Control node = child.Node;
            node.OffsetLeft = child.OffsetLeft * factor;
            node.OffsetTop = child.OffsetTop * factor;
            node.OffsetRight = child.OffsetRight * factor;
            node.OffsetBottom = child.OffsetBottom * factor;
        }
    }

    private static void Collect(Control control, List<Descendant> into)
    {
        foreach (Node child in control.GetChildren())
        {
            if (child is not Control controlChild) continue;
            into.Add(new Descendant(controlChild,
                controlChild.AnchorLeft, controlChild.AnchorTop, controlChild.AnchorRight, controlChild.AnchorBottom,
                controlChild.OffsetLeft, controlChild.OffsetTop, controlChild.OffsetRight, controlChild.OffsetBottom));
            Collect(controlChild, into);
        }
    }

    // ---- Landscape ----------------------------------------------------------------------------------------------------------

    public void RestoreLandscape()
    {
        foreach (Button button in _buttons.Values)
        {
            if (!GodotObject.IsInstanceValid(button.Control)) continue;
            PortraitControlSnapshot snapshot = button.Snapshot;
            Control control = button.Control;
            foreach (Descendant child in button.Descendants)
            {
                if (!GodotObject.IsInstanceValid(child.Node)) continue;
                child.Node.AnchorLeft = child.AnchorLeft;
                child.Node.AnchorTop = child.AnchorTop;
                child.Node.AnchorRight = child.AnchorRight;
                child.Node.AnchorBottom = child.AnchorBottom;
                child.Node.OffsetLeft = child.OffsetLeft;
                child.Node.OffsetTop = child.OffsetTop;
                child.Node.OffsetRight = child.OffsetRight;
                child.Node.OffsetBottom = child.OffsetBottom;
            }
            button.Descendants.Clear();
            snapshot.Restore(control);
            if (button.HomesKnown)
            {
                button.ShowField?.SetValue(control, button.HomeShow);
                button.HideField?.SetValue(control, button.HomeHide);
                if (button.MoveTweenField?.GetValue(control) is Tween tween && GodotObject.IsInstanceValid(tween)) tween.Kill();
                bool enabled = button.EnabledProperty?.GetValue(control) is true;
                Vector2 point = enabled ? button.HomeShow : button.HomeHide;
                if (button.IsBack) control.GlobalPosition = point;
                else control.Position = point;
            }
        }
        foreach ((ulong id, ScreenState state) in _screens)
        {
            if (GodotObject.IsInstanceValid(state.Screen)) ShowFooter(state);
            if (state.LegendBacking != null && GodotObject.IsInstanceValid(state.LegendBacking))
                state.LegendBacking.Color = state.LegendColor;
        }
        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
        _screens.Clear();
        _buttons.Clear();
        _snapshots.Clear();
        _logged.Clear();
    }

    public void PruneDead()
    {
        static bool Dead(ulong id) => !GodotObject.IsInstanceIdValid(id);
        foreach (ulong id in _screens.Keys.Where(Dead).ToList()) _screens.Remove(id);
        foreach (ulong id in _buttons.Keys.Where(Dead).ToList()) _buttons.Remove(id);
        foreach (ulong id in _snapshots.Keys.Where(static id => !GodotObject.IsInstanceIdValid(id)).ToList())
            _snapshots.Remove(id);
        _logged.RemoveWhere(static id => !GodotObject.IsInstanceIdValid(id));
    }

    private PortraitControlSnapshot Capture(Control control, bool captureVisible = false)
    {
        ulong id = control.GetInstanceId();
        if (_snapshots.TryGetValue(id, out PortraitControlSnapshot? existing))
        {
            if (captureVisible && !existing.CaptureVisible) existing.RecordInitialVisibility(control.Visible);
            return existing;
        }
        var created = new PortraitControlSnapshot(control, captureVisible);
        _snapshots[id] = created;
        return created;
    }
}
