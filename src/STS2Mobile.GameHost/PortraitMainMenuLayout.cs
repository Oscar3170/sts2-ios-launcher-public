using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// The main menu in portrait. The native live background keeps the game's zoom and its counter-scaled animated logo; the text
/// buttons become a wide column of 44 point rows drawn in the game's own lettering, outline and hover carets (measured around
/// the real text instead of the desktop rect); the continue button's run details are drawn above it at phone size; and the
/// profile, release notes, version and mod status controls move into the safe band at readable sizes. Every control edited here
/// is captured first, so turning Portrait Mode off restores the game's layout live.
/// </summary>
internal sealed class PortraitMainMenuLayout
{
    private const string ButtonsName = "MainMenuTextButtons";
    private const string BgPath = "%MainMenuBg/BgContainer";
    private const string LogoHolderName = "Control";
    private const string RunInfoPath = "%ContinueRunInfo";
    private const string ReticleLeftPath = "%ButtonReticleLeft";
    private const string ReticleRightPath = "%ButtonReticleRight";
    private const string ProfileName = "ChangeProfileButton";
    private const string ProfileRowName = "HBoxContainer";
    private const string ProfileTitlePath = "MarginContainer/VBoxContainer/Title";
    private const string ProfileDescriptionPath = "MarginContainer/VBoxContainer/Description";
    private const string NotesName = "PatchNotesButton";
    private const string NotesIconName = "Icon";
    private const string ReleasePath = "%ReleaseInfo";
    private const string ModStatusPath = "%ModdedWarning";
    private const string ModDetailsPath = "%ModWarningContainer";
    private const string ModLabelPath = "%ModWarningLabel";
    private const string ReticleTweenField = "_reticleTween";
    private const string FocusedProperty = "IsFocused";
    private const string InitPositionField = "_initPosition";
    private const string VisTweenField = "_visTween";
    private const string IsShownField = "_isShown";
    // The game's own menu background size in portrait: the live Spines are zoomed about the screen's middle and the logo is
    // counter-scaled so it keeps its desktop size where the game animates it.
    private const float BgScale = 2.05f;
    private const float LogoInsetX = 98.5f;
    private const float LogoOffsetY = -226f;
    // The game's reticles: a 40 unit caret 6 units off the text, travelling 28 units out, on the 32 unit desktop label.
    private const float NativeCaretSize = 40f;
    private const float NativeCaretGap = 6f;
    private const float NativeCaretTravel = 28f;
    // The mod status's details sit above it by a gap and are drawn at this width; the box's own margin stays native.
    private const float ModDetailsWidth = 682f;
    private const float ModDetailsGap = 9f;
    private const float ModDetailsPadding = 24f;
    private const float ModDetailsFontSize = 25f;
    private static readonly string[] ModDetailsFontNames =
        ["normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size"];
    private static readonly Color Gold = new("EFC851");
    private static readonly Color TransparentWhite = new(1f, 1f, 1f, 0f);
    private static readonly Color NativeOutlineColor = new(0f, 0f, 0f, 0.501961f);

    /// <summary>One main menu's layout state: the column it was laid out at, and what the per-frame pass watches for changes.</summary>
    private sealed class MenuState
    {
        public ulong ColumnId;
        public int VisibleCount = -1;
        public Vector2 Canvas;
        public float CapHeight = -1f;
        public bool ProfileContentChanged;
        public int ModStatusOriginalIndex = -1;
        public bool Logged;
    }

    /// <summary>A label's own theme values, captured before the first portrait edit so landscape puts them back.</summary>
    private readonly struct LabelStyle
    {
        public readonly bool FontOverride;
        public readonly int FontSize;
        public readonly bool OutlineOverride;
        public readonly int Outline;
        public readonly bool ColorOverride;
        public readonly Color Color;

        public LabelStyle(Label label)
        {
            FontOverride = label.HasThemeFontSizeOverride("font_size");
            FontSize = label.GetThemeFontSize("font_size");
            OutlineOverride = label.HasThemeConstantOverride("outline_size");
            Outline = label.GetThemeConstant("outline_size");
            ColorOverride = label.HasThemeColorOverride("font_outline_color");
            Color = label.GetThemeColor("font_outline_color");
        }
    }

    /// <summary>The alignment, wrapping and rich-text font sizes a label had before portrait changed them.</summary>
    private sealed class TextStyle
    {
        public readonly VerticalAlignment Alignment;
        public readonly TextServer.AutowrapMode Autowrap;
        public readonly bool FitContent;
        public readonly bool ScrollActive;
        public readonly bool[] FontOverride = new bool[ModDetailsFontNames.Length];
        public readonly int[] FontSizes = new int[ModDetailsFontNames.Length];

        public TextStyle(Control control)
        {
            if (control is Label label)
            {
                Alignment = label.VerticalAlignment;
                Autowrap = label.AutowrapMode;
            }
            if (control is not RichTextLabel rich) return;
            FitContent = rich.FitContent;
            ScrollActive = rich.ScrollActive;
            for (int i = 0; i < ModDetailsFontNames.Length; i++)
            {
                FontOverride[i] = rich.HasThemeFontSizeOverride(ModDetailsFontNames[i]);
                FontSizes[i] = rich.GetThemeFontSize(ModDetailsFontNames[i]);
            }
        }
    }

    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private readonly Dictionary<ulong, MenuState> _menus = new();
    private readonly Dictionary<ulong, LabelStyle> _labelStyles = new();
    private readonly Dictionary<ulong, TextStyle> _textStyles = new();
    private readonly HashSet<ulong> _wiredButtons = new();
    private readonly HashSet<ulong> _wiredMenuLabels = new();
    private readonly HashSet<ulong> _wiredProfileRows = new();

    public void Apply(NMainMenu menu, float capHeight, Vector2 canvas)
    {
        ulong id = menu.GetInstanceId();
        if (_menus.TryGetValue(id, out MenuState? state) && state.ColumnId != 0 && state.CapHeight == capHeight &&
            state.Canvas.IsEqualApprox(canvas) && ColumnStillMatches(state) && !state.ProfileContentChanged)
            return;
        Layout(menu, capHeight, canvas);
    }

    /// <summary>
    /// The cheap per-frame pass: the menu's rows appear and disappear as a run is saved, cleared or discovered epochs arrive,
    /// so the column is re-fitted when its visible count changes, and the profile row when its text does. Nothing else runs.
    /// </summary>
    public void OnProcessFrame(Window root, float capHeight)
    {
        if (_menus.Count == 0 || !SafeAreaCanvas.IsPortrait) return;
        Vector2 size = root.GetVisibleRect().Size;
        Vector2 canvas = new(size.X, Math.Max(size.Y, SafeAreaCanvas.MinPortraitHeight));
        foreach ((ulong id, MenuState state) in _menus)
        {
            if (GodotObject.InstanceFromId(id) is not NMainMenu menu || !GodotObject.IsInstanceValid(menu) || !menu.IsVisibleInTree())
                continue;
            if (state.CapHeight != capHeight || !state.Canvas.IsEqualApprox(canvas) || !ColumnStillMatches(state) ||
                state.ProfileContentChanged)
                Layout(menu, capHeight, canvas);
        }
    }

    public void RestoreLandscape()
    {
        foreach ((ulong id, MenuState state) in _menus)
            if (GodotObject.InstanceFromId(id) is NMainMenu menu && GodotObject.IsInstanceValid(menu))
            {
                KillReticleTween(menu);
                if (menu.GetNodeOrNull<Control>(ReticleLeftPath) is { } left) left.Modulate = TransparentWhite;
                if (menu.GetNodeOrNull<Control>(ReticleRightPath) is { } right) right.Modulate = TransparentWhite;
                if (state.ModStatusOriginalIndex >= 0 && menu.GetNodeOrNull<Label>(ModStatusPath) is { } modded &&
                    GodotObject.IsInstanceValid(modded))
                    menu.MoveChild(modded, Math.Min(state.ModStatusOriginalIndex, menu.GetChildCount() - 1));
            }
        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
        foreach ((ulong id, LabelStyle style) in _labelStyles)
        {
            if (GodotObject.InstanceFromId(id) is not Label label || !GodotObject.IsInstanceValid(label)) continue;
            if (style.FontOverride) label.AddThemeFontSizeOverride("font_size", style.FontSize);
            else label.RemoveThemeFontSizeOverride("font_size");
            if (style.OutlineOverride) label.AddThemeConstantOverride("outline_size", style.Outline);
            else label.RemoveThemeConstantOverride("outline_size");
            if (style.ColorOverride) label.AddThemeColorOverride("font_outline_color", style.Color);
            else label.RemoveThemeColorOverride("font_outline_color");
        }
        foreach ((ulong id, TextStyle style) in _textStyles)
        {
            if (GodotObject.InstanceFromId(id) is not Control control || !GodotObject.IsInstanceValid(control)) continue;
            if (control is Label label)
            {
                label.VerticalAlignment = style.Alignment;
                label.AutowrapMode = style.Autowrap;
            }
            if (control is not RichTextLabel rich) continue;
            for (int i = 0; i < ModDetailsFontNames.Length; i++)
            {
                if (style.FontOverride[i]) rich.AddThemeFontSizeOverride(ModDetailsFontNames[i], style.FontSizes[i]);
                else rich.RemoveThemeFontSizeOverride(ModDetailsFontNames[i]);
            }
            rich.FitContent = style.FitContent;
            rich.ScrollActive = style.ScrollActive;
        }
        _snapshots.Clear();
        _labelStyles.Clear();
        _textStyles.Clear();
        _menus.Clear();
    }

    public void PruneDead()
    {
        foreach (ulong id in new List<ulong>(_snapshots.Keys))
            if (!GodotObject.IsInstanceIdValid(id)) _snapshots.Remove(id);
        foreach (ulong id in new List<ulong>(_labelStyles.Keys))
            if (!GodotObject.IsInstanceIdValid(id)) _labelStyles.Remove(id);
        foreach (ulong id in new List<ulong>(_textStyles.Keys))
            if (!GodotObject.IsInstanceIdValid(id)) _textStyles.Remove(id);
        foreach (ulong id in new List<ulong>(_menus.Keys))
            if (!GodotObject.IsInstanceIdValid(id)) _menus.Remove(id);
        _wiredButtons.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _wiredMenuLabels.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _wiredProfileRows.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
    }

    private void Layout(NMainMenu menu, float capHeight, Vector2 canvas)
    {
        ulong id = menu.GetInstanceId();
        if (!_menus.TryGetValue(id, out MenuState? state)) _menus[id] = state = new MenuState();
        state.CapHeight = capHeight;
        state.Canvas = canvas;
        LayoutBackground(menu);
        state.ColumnId = LayoutButtons(menu, capHeight, canvas);
        state.VisibleCount = VisibleButtonCount(state.ColumnId);
        LayoutRunInfo(menu, state.ColumnId);
        LayoutUtilities(menu, capHeight, canvas, state);
        state.ProfileContentChanged = false;
        if (!state.Logged)
        {
            state.Logged = true;
            GD.Print($"Portrait main menu: canvas {canvas.X:F0}x{canvas.Y:F0} cap={capHeight:F0} buttons={state.VisibleCount} " +
                     $"portrait={SafeAreaCanvas.IsPortrait}");
        }
    }

    // ---- background -------------------------------------------------------------------------------------------------------

    private void LayoutBackground(NMainMenu menu)
    {
        if (menu.GetNodeOrNull<Control>(BgPath) is not { } bgContainer) return;
        Capture(bgContainer, captureVisible: false);
        bgContainer.Scale = new Vector2(BgScale, BgScale);
        if (bgContainer.GetNodeOrNull<Control>(LogoHolderName) is not { } logoHolder) return;
        Capture(logoHolder, captureVisible: false);
        logoHolder.Scale = new Vector2(1f / BgScale, 1f / BgScale);
        logoHolder.OffsetLeft = LogoInsetX;
        logoHolder.OffsetRight = LogoInsetX;
        logoHolder.OffsetTop = LogoOffsetY;
        logoHolder.OffsetBottom = LogoOffsetY;
    }

    // ---- the button column ------------------------------------------------------------------------------------------------

    private ulong LayoutButtons(NMainMenu menu, float capHeight, Vector2 canvas)
    {
        if (menu.GetNodeOrNull<Control>(ButtonsName) is not { } column) return 0;
        MainMenuColumn layout = PortraitMainMenuMath.Column(VisibleButtonCount(column), canvas.Y, capHeight);
        Capture(column, captureVisible: false);
        column.AnchorLeft = 0.5f;
        column.AnchorRight = 0.5f;
        column.AnchorTop = 0f;
        column.AnchorBottom = 0f;
        column.OffsetLeft = -PortraitMainMenuMath.ButtonWidth / 2f;
        column.OffsetRight = PortraitMainMenuMath.ButtonWidth / 2f;
        column.OffsetTop = layout.Top;
        column.OffsetBottom = layout.Top + layout.Height;
        column.AddThemeConstantOverride("separation", (int)layout.Separation);
        if (menu.GetNodeOrNull<Control>(ReticleLeftPath) is { } reticleLeft) Capture(reticleLeft, captureVisible: false);
        if (menu.GetNodeOrNull<Control>(ReticleRightPath) is { } reticleRight) Capture(reticleRight, captureVisible: false);
        for (int i = 0; i < column.GetChildCount(); i++)
        {
            if (column.GetChild(i) is not Control button) continue;
            Capture(button, captureVisible: false);
            button.CustomMinimumSize = new Vector2(PortraitMainMenuMath.ButtonWidth, layout.ButtonHeight);
            if (button.GetChildCount() > 0 && button.GetChild(0) is Label label) StyleMenuLabel(label);
            WireCarets(menu, button);
        }
        return column.GetInstanceId();
    }

    /// <summary>
    /// The game's lettering at the portrait size: its own outline constant and colour, grown with the font. Both are derived
    /// from the label as it is drawn now, so a copy BaseLib makes of the settings entry (which can happen after this pass, or
    /// already carry it) comes out the same instead of being scaled twice.
    /// </summary>
    private void StyleMenuLabel(Label label)
    {
        NativeStyle(label);
        int outline = label.GetThemeConstant("outline_size");
        if (outline <= 0) outline = PortraitMainMenuMath.NativeOutlineSize;
        outline = PortraitMainMenuMath.Outline(outline, label.GetThemeFontSize("font_size"), PortraitMainMenuMath.FontSize);
        Color color = label.GetThemeColor("font_outline_color");
        label.AddThemeFontSizeOverride("font_size", PortraitMainMenuMath.FontSize);
        label.AddThemeConstantOverride("outline_size", outline);
        label.AddThemeColorOverride("font_outline_color", color.A > 0f ? color : NativeOutlineColor);
        // The game turns a hovered label around its centre with a pivot set once, for the desktop font: keep it the centre as
        // the label grows (the same value the game gives it, so landscape is unchanged).
        if (_wiredMenuLabels.Add(label.GetInstanceId()))
            label.Resized += () =>
            {
                if (GodotObject.IsInstanceValid(label)) label.PivotOffset = label.Size * 0.5f;
            };
        label.PivotOffset = label.Size * 0.5f;
    }

    private void WireCarets(NMainMenu menu, Control button)
    {
        if (!_wiredButtons.Add(button.GetInstanceId())) return;
        ulong menuId = menu.GetInstanceId();
        ulong buttonId = button.GetInstanceId();
        // The game defers its own caret placement for a focused button. Defer ours too: the queue is first in, first out, so
        // this runs after the game's handler and replaces the positions it computed from the desktop label's rect.
        button.Connect("Focused", Callable.From<NClickableControl>(_ =>
            Callable.From(() => FocusCarets(menuId, buttonId)).CallDeferred()));
    }

    /// <summary>
    /// The game's own carets, on the measured text: the desktop code places them from the label's 15 unit rect, which no
    /// longer matches the drawn words once the label is 50 units. Same gold, fade and Back/Out movement as the game's.
    /// </summary>
    private void FocusCarets(ulong menuId, ulong buttonId)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        if (GodotObject.InstanceFromId(menuId) is not NMainMenu menu || !GodotObject.IsInstanceValid(menu)) return;
        if (GodotObject.InstanceFromId(buttonId) is not Control button || !GodotObject.IsInstanceValid(button)) return;
        // A hover can end before this deferred pass runs. The game's own flag says whether the button still has the focus
        // the carets belong to (hover or controller focus); only skip when it is there and false.
        if (button.GetType().GetProperty(FocusedProperty, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(button) is false)
            return;
        if (button.GetChildCount() == 0 || button.GetChild(0) is not Label label || string.IsNullOrWhiteSpace(label.Text)) return;
        if (menu.GetNodeOrNull<Control>(ReticleLeftPath) is not { } left || menu.GetNodeOrNull<Control>(ReticleRightPath) is not { } right)
            return;
        float ratio = PortraitMainMenuMath.FontSize / (float)PortraitMainMenuMath.NativeFontSize;
        float size = NativeCaretSize * ratio;
        float gap = NativeCaretGap * ratio;
        float travel = NativeCaretTravel * ratio;
        float textWidth = TextWidth(label);
        float centreX = label.GlobalPosition.X + label.Size.X * 0.5f;
        float y = label.GlobalPosition.Y + label.Size.Y * 0.5f - size * 0.5f;
        float startLeft = centreX - textWidth * 0.5f - size - gap;
        float startRight = centreX + textWidth * 0.5f + gap;
        KillReticleTween(menu);
        foreach (Control accent in new[] { left, right })
        {
            accent.Scale = Vector2.One;
            accent.CustomMinimumSize = new Vector2(size, size);
            accent.Size = new Vector2(size, size);
            accent.PivotOffset = new Vector2(size * 0.5f, size * 0.5f);
        }
        left.GlobalPosition = new Vector2(startLeft, y);
        right.GlobalPosition = new Vector2(startRight, y);
        Tween tween = menu.CreateTween().SetParallel();
        tween.TweenProperty(left, "global_position:x", startLeft - travel, 0.25)
            .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back).From(startLeft);
        tween.TweenProperty(left, "modulate", Gold, 0.05).From(TransparentWhite);
        tween.TweenProperty(right, "global_position:x", startRight + travel, 0.25)
            .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back).From(startRight);
        tween.TweenProperty(right, "modulate", Gold, 0.05).From(TransparentWhite);
        // The game's unfocus still kills this very tween and fades the carets out.
        SetField(menu, ReticleTweenField, tween);
    }

    private static float TextWidth(Label label)
    {
        Font? font = label.GetThemeFont("font");
        float measured = font?.GetStringSize(label.Text, HorizontalAlignment.Left, -1f, label.GetThemeFontSize("font_size")).X ?? 0f;
        return measured > 0f ? measured : label.Size.X;
    }

    // ---- the continue button's run details --------------------------------------------------------------------------------

    private void LayoutRunInfo(NMainMenu menu, ulong columnId)
    {
        Control? column = columnId != 0 ? GodotObject.InstanceFromId(columnId) as Control : null;
        if (column == null || !GodotObject.IsInstanceValid(column)) return;
        if (menu.GetNodeOrNull<Control>(RunInfoPath) is not { } runInfo) return;
        PortraitControlSnapshot snapshot = Capture(runInfo, captureVisible: false);
        float width = snapshot.InitialOffsetRight - snapshot.InitialOffsetLeft;
        float height = snapshot.InitialOffsetBottom - snapshot.InitialOffsetTop;
        if (width <= 0f || height <= 0f) return;
        float scale = PortraitMainMenuMath.ContinueInfoScale(width, menu.Size.X);
        // The packed scene anchors the details to its parent's middle, beside the button. Portrait puts them above the first
        // row, by their own top left, exactly as wide as the column.
        runInfo.AnchorLeft = 0f;
        runInfo.AnchorTop = 0f;
        runInfo.AnchorRight = 0f;
        runInfo.AnchorBottom = 0f;
        runInfo.Scale = new Vector2(scale, scale);
        runInfo.PivotOffset = Vector2.Zero;
        SetRunInfoHome(runInfo, new Vector2((PortraitMainMenuMath.ButtonWidth - width * scale) * 0.5f,
            -height * scale - PortraitMainMenuMath.ContinueGap), width, height);
    }

    /// <summary>
    /// Shows the details at the portrait row and keeps their copy of the animation's home position in step, so the game's
    /// show and hide still slide from where they now live.
    /// </summary>
    private static void SetRunInfoHome(Control runInfo, Vector2 position, float width, float height)
    {
        if (GetField(runInfo, VisTweenField) is Tween tween && tween.IsValid()) tween.Kill();
        SetField(runInfo, InitPositionField, position);
        runInfo.OffsetLeft = position.X;
        runInfo.OffsetTop = position.Y;
        runInfo.OffsetRight = position.X + width;
        runInfo.OffsetBottom = position.Y + height;
        runInfo.Position = position;
        if (GetField(runInfo, IsShownField) is true) runInfo.Position = position + new Vector2(0f, -20f);
    }

    // ---- utilities --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The profile row, the release-notes button, the version label and the mod status, moved under the safe cap. The two
    /// buttons become 44 point touch targets; the profile's own name and description are drawn at 11 points, and the version
    /// and mod status are fine print at 8 points.
    /// </summary>
    private void LayoutUtilities(NMainMenu menu, float capHeight, Vector2 canvas, MenuState state)
    {
        float top = capHeight + PortraitMainMenuMath.UtilityTopGap;
        float target = PortraitBands.MinTarget;
        float inset = PortraitMainMenuMath.UtilityInset;
        float notesLeft = canvas.X - inset - target;
        if (menu.GetNodeOrNull<Control>(NotesName) is { } notes)
        {
            Capture(notes, captureVisible: false);
            Place(notes, new Vector2(notesLeft, top), new Vector2(target, target));
            notes.PivotOffset = new Vector2(target, target) * 0.5f;
            if (notes.GetNodeOrNull<Control>(NotesIconName) is { } icon)
            {
                Capture(icon, captureVisible: false);
                icon.PivotOffset = new Vector2(target, target) * 0.5f;
            }
        }
        if (menu.GetNodeOrNull<Control>(ProfileName) is { } profile)
        {
            Capture(profile, captureVisible: false);
            var content = Vector2.Zero;
            if (profile.GetNodeOrNull<HBoxContainer>(ProfileRowName) is { } row)
            {
                Capture(row, captureVisible: false);
                WireProfileRow(row, menu.GetInstanceId());
                // The row keeps the native 1x shape (and its 1.02 hover tween); the name and description carry the size.
                if (row.GetNodeOrNull<Label>(ProfileTitlePath) is { } title)
                    SetLabelFont(title, PortraitMainMenuMath.ProfileFontSize);
                if (row.GetNodeOrNull<Label>(ProfileDescriptionPath) is { } description)
                    SetLabelFont(description, PortraitMainMenuMath.ProfileFontSize);
                content = row.GetCombinedMinimumSize();
                row.Size = content;
                row.Position = new Vector2(PortraitMainMenuMath.ProfilePadding,
                    (target - content.Y) * 0.5f);
            }
            float width = Math.Max(target, content.X + 2f * PortraitMainMenuMath.ProfilePadding);
            Place(profile, new Vector2(inset, top), new Vector2(width, target));
            profile.PivotOffset = new Vector2(width, target) * 0.5f;
        }
        if (menu.GetNodeOrNull<Label>(ReleasePath) is { } release)
        {
            Capture(release, captureVisible: false);
            TextStyleFor(release);
            SetLabelFont(release, PortraitMainMenuMath.ReleaseFontSize);
            float width = PortraitMainMenuMath.ReleaseWidth;
            Place(release, new Vector2(notesLeft - width - PortraitMainMenuMath.UtilityGap, top), new Vector2(width, target));
            release.VerticalAlignment = VerticalAlignment.Center;
        }
        if (menu.GetNodeOrNull<Label>(ModStatusPath) is { } modded)
        {
            Capture(modded, captureVisible: false);
            TextStyleFor(modded);
            SetLabelFont(modded, PortraitMainMenuMath.ModStatusFontSize);
            modded.AutowrapMode = TextServer.AutowrapMode.Off;
            float maxWidth = canvas.X * 0.38f;
            Font? font = modded.GetThemeFont("font");
            float width = MathF.Ceiling(font?.GetStringSize(modded.Text, HorizontalAlignment.Left, -1f,
                PortraitMainMenuMath.ModStatusFontSize).X ?? 0f) + 8f;
            if (width > maxWidth)
            {
                width = maxWidth;
                modded.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            }
            float height = Math.Max(PortraitBands.MinTarget, (font?.GetHeight(PortraitMainMenuMath.ModStatusFontSize) ?? 0f) + 8f);
            var box = new Vector2(canvas.X - inset - width, canvas.Y - PortraitMainMenuMath.ModStatusBottom - height);
            Place(modded, box, new Vector2(width, height));
            modded.VerticalAlignment = VerticalAlignment.Center;
            // The status is a corner footnote, not another menu row: where its box meets the buttons, they keep the touch.
            // Godot gives input to the later sibling first, so the status goes behind the column while it is in portrait.
            if (menu.GetNodeOrNull<Control>(ButtonsName) is { } column && modded.GetIndex() > column.GetIndex())
            {
                if (state.ModStatusOriginalIndex < 0) state.ModStatusOriginalIndex = modded.GetIndex();
                menu.MoveChild(modded, column.GetIndex());
            }
            LayoutModDetails(menu, capHeight, canvas, box.Y);
        }
    }

    /// <summary>Anchors a control to the canvas's top left and gives it a position and size in canvas units.</summary>
    private static void Place(Control control, Vector2 position, Vector2 size)
    {
        control.AnchorLeft = control.AnchorTop = control.AnchorRight = control.AnchorBottom = 0f;
        control.OffsetLeft = position.X;
        control.OffsetTop = position.Y;
        control.OffsetRight = position.X + size.X;
        control.OffsetBottom = position.Y + size.Y;
    }

    private void LayoutModDetails(NMainMenu menu, float capHeight, Vector2 canvas, float statusBottom)
    {
        if (menu.GetNodeOrNull<Control>(ModDetailsPath) is not { } details) return;
        if (menu.GetNodeOrNull<RichTextLabel>(ModLabelPath) is not { } text) return;
        Capture(details, captureVisible: false);
        Capture(text, captureVisible: false);
        TextStyleFor(text);
        foreach (string name in ModDetailsFontNames) text.AddThemeFontSizeOverride(name, (int)ModDetailsFontSize);
        float right = canvas.X - PortraitMainMenuMath.UtilityInset;
        float bottom = statusBottom - ModDetailsGap;
        float maxHeight = Math.Max(1f, bottom - capHeight - ModDetailsPadding - 16f);
        // A hidden container has not allocated its label yet: measure at the final width, not the old or zero one.
        text.FitContent = false;
        text.ScrollActive = false;
        text.Size = new Vector2(ModDetailsWidth - ModDetailsPadding, text.Size.Y);
        float required = text.GetContentHeight() + 8f;
        text.ScrollActive = required > maxHeight;
        float textHeight = Math.Min(required, maxHeight);
        text.CustomMinimumSize = new Vector2(0f, textHeight);
        Rect2 menuRect = menu.GetGlobalRect();
        details.AnchorLeft = details.AnchorTop = details.AnchorRight = details.AnchorBottom = 1f;
        details.OffsetLeft = right - ModDetailsWidth - menuRect.End.X;
        details.OffsetRight = right - menuRect.End.X;
        details.OffsetBottom = bottom - menuRect.End.Y;
        details.OffsetTop = details.OffsetBottom - textHeight - ModDetailsPadding;
    }

    /// <summary>Marks this menu due a new layout when the profile row's own text or icon changes size.</summary>
    private void WireProfileRow(Control row, ulong menuId)
    {
        if (!_wiredProfileRows.Add(row.GetInstanceId())) return;
        row.Connect(Control.SignalName.MinimumSizeChanged, Callable.From(() =>
        {
            if (_menus.TryGetValue(menuId, out MenuState? state)) state.ProfileContentChanged = true;
        }));
    }

    private void SetLabelFont(Label label, int size)
    {
        NativeStyle(label);
        label.AddThemeFontSizeOverride("font_size", size);
    }

    private LabelStyle NativeStyle(Label label)
    {
        ulong id = label.GetInstanceId();
        if (_labelStyles.TryGetValue(id, out LabelStyle style)) return style;
        style = new LabelStyle(label);
        _labelStyles[id] = style;
        return style;
    }

    private TextStyle TextStyleFor(Control control)
    {
        ulong id = control.GetInstanceId();
        if (_textStyles.TryGetValue(id, out TextStyle? style)) return style;
        style = new TextStyle(control);
        _textStyles[id] = style;
        return style;
    }

    // ---- per-frame change detection ---------------------------------------------------------------------------------------

    private static int VisibleButtonCount(ulong columnId)
    {
        Control? column = columnId != 0 ? GodotObject.InstanceFromId(columnId) as Control : null;
        return column != null && GodotObject.IsInstanceValid(column) ? VisibleButtonCount(column) : 0;
    }

    private static int VisibleButtonCount(Control column)
    {
        int count = 0;
        for (int i = 0; i < column.GetChildCount(); i++)
            if (column.GetChild(i) is Control { Visible: true }) count++;
        return count;
    }

    private bool ColumnStillMatches(MenuState state)
    {
        Control? column = state.ColumnId != 0 ? GodotObject.InstanceFromId(state.ColumnId) as Control : null;
        return column != null && GodotObject.IsInstanceValid(column) && VisibleButtonCount(column) == state.VisibleCount;
    }

    // ---- shared -----------------------------------------------------------------------------------------------------------

    private PortraitControlSnapshot Capture(Control control, bool captureVisible)
    {
        if (!_snapshots.TryGetValue(control.GetInstanceId(), out PortraitControlSnapshot? snapshot))
            _snapshots[control.GetInstanceId()] = snapshot = new PortraitControlSnapshot(control, captureVisible);
        return snapshot;
    }

    private static void KillReticleTween(NMainMenu menu)
    {
        if (GetField(menu, ReticleTweenField) is Tween tween && tween.IsValid()) tween.Kill();
        SetField(menu, ReticleTweenField, null);
    }

    private static object? GetField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target);

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(target, value);
}
