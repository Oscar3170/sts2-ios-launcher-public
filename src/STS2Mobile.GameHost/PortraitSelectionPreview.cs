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
        public Vector2 TransformHome;
        public bool TransformHomeKnown;
        public int RowCount = -1;
        public Vector2 PairFit;
        public float RowScale;
        public float RowCentreY;
        public int RowSettle;
    }

    /// <summary>
    /// A preview button: how it was grown and, for the composed Confirm, where the game shows it. A Back (the panels' Cancel
    /// and the screen's Close) is grown here but its place belongs to PortraitNavigationPads, the single writer of every
    /// NBackButton's slide fields.
    /// </summary>
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

    /// <summary>
    /// A preview panel's button: "Confirm"/"Cancel" in the upgrade, enchant and transform panels, and the deck select's
    /// scene-unique "%PreviewConfirm"/"%PreviewCancel".
    /// </summary>
    private static Control? PreviewButton(Control container, string name, string uniqueName) =>
        container.GetNodeOrNull<Control>(name) ?? container.GetNodeOrNull<Control>("%" + uniqueName) ??
        container.GetNodeOrNull<Control>(uniqueName);

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
        panel.Confirm = TrackButton(PreviewButton(container, "Confirm", "PreviewConfirm"));
        panel.Cancel = TrackButton(PreviewButton(container, "Cancel", "PreviewCancel"));
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
        if (panel.Row != null)
        {
            FitRow(panel, width, PortraitPreviewMath.PreviewCenterY(
                state.CapHeight, height, PortraitPreviewMath.RowCenterFraction));
            // The cards are added in the same call that shows the panel and are placed by their row's deferred sort, so fit
            // once more after that sort (and after the game's own deferred pivot/scale on the deck select) before the draw.
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
            // Put the game's own scale and place back before its next Initialize reads the composition's position.
            if (GodotObject.IsInstanceValid(panel.Transform))
            {
                panel.Transform.Scale = _snapshots.TryGetValue(panel.Transform.GetInstanceId(),
                    out PortraitControlSnapshot? snap) ? snap.InitialScale : Vector2.One;
                if (panel.TransformHomeKnown) panel.Transform.Position = panel.TransformHome;
            }
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
                panel.Row.PivotOffset != Vector2.Zero || Math.Abs(panel.RowCentreY - centre) > 0.5f ||
                Math.Abs(panel.Row.Size.X - PortraitPreviewMath.RowLocalWidth(width, count)) > 0.5f)
                FitRow(panel, width, centre);
            else if (panel.RowSettle > 0)
            {
                // The row's own sort may land after the frame it opened on: keep centring from the cards for a few frames.
                panel.RowSettle--;
                CenterRow(panel.Row, width, centre, 0);
            }
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
        PlaceTopLeft(row, ToLocal(row, new Vector2(PortraitPreviewMath.RowLeft(width, count), centerY)),
            fit.Scale, fit.LocalWidth);
        if (row.GetThemeConstant("separation") != (int)PortraitPreviewMath.RowSeparation)
            row.AddThemeConstantOverride("separation", (int)PortraitPreviewMath.RowSeparation);
        CenterRow(row, width, centerY, 0);
        panel.RowCount = count;
        panel.RowScale = row.Scale.X;
        panel.RowCentreY = centerY;
        panel.RowSettle = 2;
    }

    /// <summary>
    /// Centres a preview row from the cards it actually draws: the game's rows place their holders around their own rect
    /// (a container that centres its children, and a preview container whose origin is not the canvas origin), so the
    /// fitted row is measured and moved so the cards' union is the canvas middle. A union wider than the design margin is
    /// scaled down and measured again; both are one-shot per open (or count change), never per frame.
    /// </summary>
    private void CenterRow(Control row, float width, float centerY, int attempt)
    {
        if (CardDefaultSize() is not { } cardSize) return;
        if (!CardUnion(row, cardSize, out Rect2 union)) return;
        // Only a laid-out row is measurable: the cards a row was just handed still stand where the previous layout left them
        // until the row's own deferred sort runs, and their drawn width is then the one the fit asks for.
        float expected = (PortraitPreviewMath.RowCardWidth +
            PortraitPreviewMath.RowPitch * Math.Max(0, RowCards(row) - 1)) * row.Scale.X;
        if (Math.Abs(union.Size.X - expected) > 2f) return;
        float room = width - PortraitPreviewMath.RowMargin;
        if (union.Size.X > room + 0.5f)
        {
            if (attempt >= 2) return;
            float shrink = room / union.Size.X;
            row.Scale = new Vector2(row.Scale.X * shrink, row.Scale.Y * shrink);
            CenterRow(row, width, centerY, attempt + 1);
            return;
        }
        float dx = width * 0.5f - union.GetCenter().X;
        float dy = centerY - union.GetCenter().Y;
        if (Math.Abs(dx) < 0.25f && Math.Abs(dy) < 0.25f) return;
        // Move the row by exactly this much on the canvas: convert the new canvas point of its own origin through its
        // parent's transform, so no assumption about that transform's scale is needed.
        Vector2 moved = row.GlobalPosition + new Vector2(dx, dy);
        if (row.GetParent() is Control parent && parent.IsInsideTree())
            row.Position = parent.GetGlobalTransform().AffineInverse() * moved;
        else
            row.Position += new Vector2(dx, dy);
        if (_logged.Add(row.GetInstanceId()))
            GD.Print($"Portrait selection preview: {RowCards(row)}-card row {union.Size.X:F0}x{union.Size.Y:F0} at " +
                     $"({union.GetCenter().X:F0}, {union.GetCenter().Y:F0}) moved {dx:F0},{dy:F0}.");
    }

    /// <summary>The cards' drawn rectangle in canvas coordinates, as the harness measures them (a card is centred on its node).</summary>
    private static bool CardUnion(Node node, Vector2 cardSize, out Rect2 union)
    {
        union = default;
        bool any = false;
        CollectCardBounds(node, cardSize, ref union, ref any);
        return any;
    }

    private static void CollectCardBounds(Node node, Vector2 cardSize, ref Rect2 union, ref bool any)
    {
        for (int i = 0; i < node.GetChildCount(); i++)
        {
            Node child = node.GetChild(i);
            if (child.GetType().FullName == CardTypeName && child is Control card)
            {
                Vector2 size = cardSize * card.GetGlobalTransform().Scale.Abs();
                var rect = new Rect2(card.GlobalPosition - size * 0.5f, size);
                union = any ? union.Merge(rect) : rect;
                any = true;
            }
            CollectCardBounds(child, cardSize, ref union, ref any);
        }
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
        PortraitControlSnapshot snap = Capture(preview);
        if (!panel.TransformHomeKnown)
        {
            panel.TransformHome = preview.Position;
            panel.TransformHomeKnown = true;
        }
        // Measure the game's own composition: never the scale a previous fit left behind.
        if (preview.Scale != snap.InitialScale) preview.Scale = snap.InitialScale;
        if (!CardUnion(preview, cardSize, out Rect2 union)) return;
        TransformPreviewFit fit = PortraitPreviewMath.TransformFit(
            state.Canvas.X, state.Canvas.Y, state.CapHeight, PortraitPreviewMath.TransformPromptTop(state.Canvas.Y),
            union.GetCenter().X, union.GetCenter().Y, union.Size.X, union.Size.Y);
        preview.Scale = Vector2.One * fit.Scale;
        panel.TransformFitted = true;
        panel.TransformCanvas = state.Canvas;
        // The drawn composition need not be symmetric about the preview's own pivot: measure it where it is now and move the
        // whole thing so its middle is the canvas's middle (and its height stays in the band above the prompt).
        if (!CardUnion(preview, cardSize, out Rect2 drawn))
        {
            if (_logged.Add(preview.GetInstanceId()))
                GD.Print($"Portrait selection preview: transform composition fitted at {fit.Scale:F3}, no cards to centre.");
            return;
        }
        float dx = fit.CenterX - drawn.GetCenter().X;
        float dy = fit.CenterY - drawn.GetCenter().Y;
        if (Math.Abs(dx) > 0.25f || Math.Abs(dy) > 0.25f)
        {
            Vector2 parentScale = preview.GetParent() is Control parent && parent.IsInsideTree()
                ? parent.GetGlobalTransform().Scale.Abs()
                : Vector2.One;
            preview.Position += new Vector2(dx / Math.Max(parentScale.X, 0.01f), dy / Math.Max(parentScale.Y, 0.01f));
        }
        if (_logged.Add(preview.GetInstanceId()))
            GD.Print($"Portrait selection preview: transform composition {union.Size.X:F0}x{union.Size.Y:F0} fitted at " +
                     $"{fit.Scale:F3}, drawn {drawn.Size.X:F0}x{drawn.Size.Y:F0}, moved {dx:F0},{dy:F0}.");
    }

    private readonly HashSet<ulong> _logged = new();

    private static Vector2? CardDefaultSize()
    {
        if (_cardDefaultSize != null) return _cardDefaultSize.GetValue(null) is Vector2 size ? size : null;
        _cardType ??= typeof(MegaCrit.Sts2.Core.Nodes.NGame).Assembly.GetType(CardTypeName);
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
        // A Back's place (and its parking spot off the canvas) belongs to PortraitNavigationPads, the single writer of every
        // NBackButton's cached slide positions. A second writer here would drag the Back to this footer point for a frame and
        // would overwrite the pad pass's hide point, leaving a disabled Back's art on screen.
        if (button.IsBack) return;
        var target = new Vector2(PortraitPreviewMath.ConfirmLeft(width, control.Size.X), top);
        if (button.ShowField == null) return;
        target = ToLocal(control, target);
        if (!float.IsNaN(button.Target.X) && button.Target.DistanceSquaredTo(target) < 0.25f) return;
        button.Target = target;
        if (!button.HomesKnown && button.ShowField.GetValue(control) is Vector2 show)
        {
            button.HomesKnown = true;
            button.HomeShow = show;
            button.HomeHide = button.HideField?.GetValue(control) is Vector2 hide ? hide : show;
        }
        button.ShowField.SetValue(control, target);
        Vector2 hidden = target + new Vector2(control.Size.X + 100f * PortraitPreviewMath.Unit, 0f);
        button.HideField?.SetValue(control, hidden);
        // A shown button slides to its portrait point (the game's own entrance feel, and no jump if it was already on its way
        // out; a hidden one is parked clear of the screen at once, since it cannot be seen).
        if (button.EnabledProperty?.GetValue(control) is true) Move(button, control, target);
        else
        {
            if (button.MoveTweenField?.GetValue(control) is Tween moving && GodotObject.IsInstanceValid(moving)) moving.Kill();
            control.Position = hidden;
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
        tween.TweenProperty(control, "position", point, 0.35)
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
            // A Back's show and hide places belong to PortraitNavigationPads, which restores the game's own values on the
            // same pass; only the composed Confirm buttons are put back from here.
            if (button.HomesKnown && !button.IsBack)
            {
                button.ShowField?.SetValue(control, button.HomeShow);
                button.HideField?.SetValue(control, button.HomeHide);
                if (button.MoveTweenField?.GetValue(control) is Tween tween && GodotObject.IsInstanceValid(tween)) tween.Kill();
                bool enabled = button.EnabledProperty?.GetValue(control) is true;
                control.Position = enabled ? button.HomeShow : button.HomeHide;
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
