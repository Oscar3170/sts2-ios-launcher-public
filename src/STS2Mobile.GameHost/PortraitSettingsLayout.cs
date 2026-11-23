using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// The settings screen in portrait, its dropdowns, and the feedback screen settings opens.
/// <para>The tabs sit under the notch (under the top bar, potions and relics in a run, which stay in view: the run-bar
/// tweens are taken over in PortraitLayout) and the rows scroll below them, never under them. Each panel but Input is
/// drawn <see cref="Scale"/> times larger at a width that fits the screen, so its rows, text, tickboxes, page arrows, sliders
/// and the language list grow together; every row and widget is at least 44 points tall as drawn. Input, the keyboard and
/// controller table, is drawn at 0.909 (its four columns need 1100 units) with the game's own table size kept: its labels
/// wrap inside their columns and are raised to at least 11 points, and its rows are taller than the rest for them.
/// A vertical drag on the rows scrolls them before the GUI: the press under the finger is let go (see
/// <see cref="PhoneSwipeRelease"/>), so a scroll never toggles a setting, and a drag on a slider or the scrollbar is left to
/// the game.</para>
/// <para>The language dropdown is a 320 x 64 unit button over a 600 unit list inside the scaled panel: the button and every
/// list row get a 44 point target, the list is never taller than the rows area it opens in, and its dismisser covers the whole
/// screen (the desktop one is 3000 x 2000 around the button, which leaves the bottom of a tall screen uncovered). The feedback
/// screen is 368 x 305 in the middle of a desktop screen; in portrait it is drawn <see cref="FeedbackScale"/> times larger,
/// which makes its panel, emoji buttons, dropdown and text phone sized. Every edit is captured first, and all of it is put
/// back when Portrait Mode is turned off.</para>
/// </summary>
internal sealed class PortraitSettingsLayout
{
    private const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private const string Ns = "MegaCrit.Sts2.Core.Nodes.";
    public const string FeedbackScreenTypeName = Ns + "Screens.FeedbackScreen.NSendFeedbackScreen";
    private const string DropdownTypeName = Ns + "GodotExtensions.NDropdown";
    private const string DropdownListTypeName = Ns + "CommonUi.NDropdownContainer";
    private const string DropdownItemTypeName = Ns + "CommonUi.NDropdownItem";
    private const string SettingsSliderTypeName = Ns + "Screens.Settings.NSettingsSlider";
    private const string ScrollContainerTypeName = Ns + "GodotExtensions.NScrollableContainer";

    /// <summary>The feedback screen is only ever laid out by this class; watching it makes Settings' own screen follow.</summary>
    public static readonly HashSet<string> WatchedTypeNames = new(StringComparer.Ordinal) { FeedbackScreenTypeName };

    // The panels drawn 1.55 times larger, so a row of 64 units with its divider and gaps (82) is 127 units (44 points) from
    // the next and text of 28 units reads at 15 points; 1000 units wide as drawn, from 24 units in, with the scrollbar beside
    // them. The tabs are 110 units tall with 44 point touch areas and 13 point text.
    private const float Scale = 1.55f;
    private const float RowsLeft = 24f;
    private const float RowsWidth = 1000f;
    private const float TabHeight = 110f;
    private const int TabFont = 38;
    // The rows end this far above the canvas's lower edge, clear of the back button in the lower left corner.
    private const float BottomGap = 380f;
    private const string ClipperPath = "ScrollContainer/Mask/Clipper";
    private const string SpacerName = "PortraitScaleSpacer";
    private const string InputPanelName = "NInputSettingsPanel";
    // 36 units drawn at Input's fit (its 1100 unit table drawn 1000 wide, 0.909) are 11.4 points: long command and key
    // names wrap at a size a phone can read instead of silently widening the four column table. A row that holds three lines
    // of them (the longest key names) is 145 units, 132 drawn at that fit (45.8 points), which is also more than the 127
    // unit touch target.
    private const int InputFont = 36;
    private const float InputRowHeight = 145f;
    private static readonly string[] RichFontKeys =
        ["normal_font_size", "bold_font_size", "italics_font_size", "bold_italics_font_size", "mono_font_size"];

    // An open dropdown list is at most 386 drawn units tall (three 44 point rows) and never taller than the rows area it
    // opens in.
    private const float DropdownHeight = 386f;
    private const float DropdownMargin = 8f;

    // A drag starts after 28 units (about 10 points), up or down, wherever it began in the rows; <see cref="SwipeMath"/>
    // holds the rule.
    private const float DragDistance = 28f;
    /// <summary>
    /// Safety cap on the dismiss tap's swallow, only for a release that never arrives (a lost touch); the swallow itself ends
    /// when every press kind that arrived has been released, so a held finger or a quick next tap behaves.
    /// </summary>
    private const ulong SwallowCapMs = 1500;

    // The feedback screen's panel is 368 x 305 with 80 x 80 emoji buttons and a 57 unit dropdown: drawn twice as large, the
    // panel is 736 x 610 (inside the canvas), the emoji buttons 160 units and the dropdown's list rows 88, and its 22-24
    // unit text reads at 15-17 points. Its decorations (sun, cupcake, flower) reach 530 units from an edge, which twice over
    // stays inside 1080.
    // 720 rather than 540: the seven 80 unit emoji buttons (with their gaps) are wider than 540, and only fit when the
    // virtual canvas is as wide as that row.
    private const float FeedbackDesignWidth = 720f;
    private const float FeedbackBottomMargin = 40f;

    /// <summary>One settings screen: the rows area, its scroll, and the dropdowns and sliders in its panels.</summary>
    private sealed class ScreenState
    {
        public ulong Clip;
        public ulong Scroll;
        public ulong Scrollbar;
        public readonly List<ulong> Dropdowns = new();
        public readonly List<ulong> Sliders = new();
    }

    /// <summary>One dropdown: its open list, its dismisser, and where the list may be drawn.</summary>
    private sealed class DropdownState
    {
        public ulong List;
        public ulong Dismisser;
        /// <summary>The settings rows area the list is kept inside; 0 for the whole canvas (the feedback screen).</summary>
        public ulong Area;
        public Vector2 Button;
        public bool Covers;
    }

    /// <summary>A label's own wrap mode and font sizes, put back in landscape.</summary>
    private sealed class TextState
    {
        public TextServer.AutowrapMode Wrap;
        public bool HadFont;
        public int Font;
        public int[]? RichFonts;
        public bool[]? RichHad;
        public (int Min, int Max)? Mega;
    }

    private enum DragKind : byte { None, Scroll, List }

    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private readonly Dictionary<ulong, ScreenState> _screens = new();
    private readonly Dictionary<ulong, DropdownState> _dropdowns = new();
    private readonly HashSet<ulong> _panels = new();
    private readonly HashSet<ulong> _feedback = new();
    private readonly HashSet<ulong> _prepared = new();
    private readonly Dictionary<ulong, (bool Had, int Size)> _fonts = new();
    private readonly Dictionary<ulong, TextState> _texts = new();

    private readonly Type? _dropdownType, _listType, _itemType, _sliderType, _scrollType;
    private readonly FieldInfo? _dropdownList, _dropdownItems, _dropdownDismisser;
    private readonly FieldInfo? _listMaxHeight, _listDrag, _listLimit, _listDragging, _scrollDragY, _scrollDragging;
    private readonly PropertyInfo? _scrollLimit;
    private readonly PropertyInfo? _megaMin, _megaMax, _richMin, _richMax;
    private readonly bool _ready;

    private SceneTree? _tree;
    /// <summary>The dismiss tap being swallowed, the press kinds it has seen, and when the safety cap gives up.</summary>
    private bool _swallowing;
    private bool _swallowTouchPress;
    private bool _swallowMousePress;
    private bool _swallowTouchRelease;
    private bool _swallowMouseRelease;
    private ulong _swallowDeadline;
    private bool _down;
    private bool _dragging;
    private DragKind _kind;
    private ulong _target;
    private ulong _surface;
    /// <summary>Whether a settings screen, its rows clip or an open dropdown list is visible this frame.</summary>
    private bool _surfaceVisible;
    private float _startY;
    private float _initial;
    private float _limit;

    public PortraitSettingsLayout()
    {
        try
        {
            Assembly game = typeof(NGame).Assembly;
            _dropdownType = game.GetType(DropdownTypeName);
            _listType = game.GetType(DropdownListTypeName);
            _itemType = game.GetType(DropdownItemTypeName);
            _sliderType = game.GetType(SettingsSliderTypeName);
            _scrollType = game.GetType(ScrollContainerTypeName);
            _dropdownList = _dropdownType?.GetField("_dropdownContainer", InstAll);
            _dropdownItems = _dropdownType?.GetField("_dropdownItems", InstAll);
            _dropdownDismisser = _dropdownType?.GetField("_dismisser", InstAll);
            _listMaxHeight = _listType?.GetField("_maxHeight", InstAll);
            _listDrag = _listType?.GetField("_targetDragPos", InstAll);
            _listLimit = _listType?.GetField("_scrollLimitBottom", InstAll);
            _listDragging = _listType?.GetField("_isDragging", InstAll);
            _megaMin = typeof(MegaCrit.Sts2.addons.mega_text.MegaLabel).GetProperty("MinFontSize", InstAll);
            _megaMax = typeof(MegaCrit.Sts2.addons.mega_text.MegaLabel).GetProperty("MaxFontSize", InstAll);
            _richMin = typeof(MegaCrit.Sts2.addons.mega_text.MegaRichTextLabel).GetProperty("MinFontSize", InstAll);
            _richMax = typeof(MegaCrit.Sts2.addons.mega_text.MegaRichTextLabel).GetProperty("MaxFontSize", InstAll);
            if (_scrollType != null)
            {
                _scrollDragY = _scrollType.GetField("_targetDragPosY", InstAll);
                _scrollDragging = _scrollType.GetField("_isDragging", InstAll);
                _scrollLimit = _scrollType.GetProperty("ScrollLimitBottom", InstAll);
            }
            _ready = _dropdownType != null && _listType != null && _itemType != null && _scrollType != null &&
                     _dropdownList != null && _dropdownItems != null && _listMaxHeight != null && _listDrag != null &&
                     _listLimit != null && _scrollDragY != null && _scrollDragging != null && _scrollLimit != null &&
                     _megaMin != null && _megaMax != null;
            if (!_ready)
                GD.PrintErr("Portrait settings: the game's dropdowns or scroll view no longer have the expected members; " +
                            "the drag and the dropdown sizing are off.");
        }
        catch (Exception error)
        {
            GD.PrintErr("Portrait settings: off: ", error.Message);
        }
    }

    public void Attach(SceneTree tree)
    {
        if (_tree == tree) return;
        Detach();
        _tree = tree;
        tree.Root.WindowInput += OnWindowInput;
        GD.Print("Portrait settings: window input attached.");
    }

    public void Detach()
    {
        if (_tree != null && GodotObject.IsInstanceValid(_tree.Root)) _tree.Root.WindowInput -= OnWindowInput;
        _tree = null;
        Reset();
    }

    // ---- settings ---------------------------------------------------------------------------------------------------------

    public void ApplyNode(NSettingsScreen settings, float capHeight)
    {
        LayoutSettings(settings, capHeight);
        if (!_screens.ContainsKey(settings.GetInstanceId())) Register(settings);
    }

    private void LayoutSettings(NSettingsScreen settings, float capHeight)
    {
        bool inRun = settings.GetType().GetField("_isInRun", InstAll)?.GetValue(settings) is true;
        float tabsTop = inRun ? capHeight + PortraitBands.RunContentTop + 12f : capHeight + 24f;
        if (settings.GetNodeOrNull<Control>("SettingsTabManager") is { } tabManager)
        {
            Capture(tabManager, captureVisible: false);
            tabManager.AnchorLeft = 0.5f;
            tabManager.AnchorRight = 0.5f;
            tabManager.OffsetLeft = -504f;
            tabManager.OffsetRight = 504f;
            tabManager.OffsetTop = tabsTop;
            tabManager.OffsetBottom = tabsTop + TabHeight;

            foreach (string iconName in new[] { "%LeftTabIcon", "%RightTabIcon" })
            {
                if (settings.GetNodeOrNull<Control>(iconName) is { } icon)
                {
                    Capture(icon, captureVisible: true);
                    icon.Visible = false;
                }
            }

            foreach (string tabName in new[] { "General", "Graphics", "Sound", "Input" })
            {
                if (tabManager.GetNodeOrNull<Control>(tabName) is not { } tab) continue;
                Capture(tab, captureVisible: false);
                tab.CustomMinimumSize = new Vector2(244f, TabHeight);
                float pad = (PortraitBands.MinTarget - TabHeight) / 2f;
                PhoneHitArea.Grow(tab, 0f, pad, 0f, pad);
                if (tab.GetNodeOrNull<Control>("Label") is { } tabLabel) SetFont(tabLabel, TabFont);
            }
        }

        float rowsTop = tabsTop + TabHeight + 16f;
        if (settings.GetNodeOrNull<Control>(ClipperPath) is { } clipper)
        {
            // The scroll area: its top edge is where the rows start, its bottom edge stays clear of the back button.
            Capture(clipper, captureVisible: false);
            clipper.OffsetTop = rowsTop;
            clipper.OffsetBottom = -BottomGap;

            float panelWidth = RowsWidth / Scale;
            foreach (Node child in clipper.GetChildren())
            {
                if (child is not NSettingsPanel panel) continue;
                _panels.Add(panel.GetInstanceId());
                bool input = IsInputPanel(panel);
                // Input is wider than the screen (a 1100 unit table): drawn a little smaller so it fits.
                float inputWidth = input ? Math.Max(panel.Size.X, 1100f) : 0f;
                float drawnScale = input ? RowsWidth / inputWidth : Scale;
                Capture(panel, captureVisible: false);
                panel.AnchorLeft = 0.5f;
                panel.AnchorRight = 0.5f;
                panel.OffsetLeft = RowsLeft - 540f;
                panel.OffsetRight = RowsLeft - 540f + (input ? inputWidth : panelWidth);
                panel.PivotOffset = Vector2.Zero;
                panel.Scale = new Vector2(drawnScale, drawnScale);
                if (panel.GetNodeOrNull<Control>("VBoxContainer") is { } content)
                {
                    if (!input)
                    {
                        Capture(content, captureVisible: false);
                        content.CustomMinimumSize = new Vector2(panelWidth, 0f);
                    }
                    float rowMinimum = input
                        ? Math.Max(PortraitBands.MinTarget / drawnScale, InputRowHeight)
                        : PortraitBands.MinTarget / drawnScale;
                    GrowRows(content, rowMinimum);
                    if (input) RaiseInputText(content);
                }
                PadTargets(panel, drawnScale);
            }
        }

        // The mask fades the rows out at the tabs' lower edge, measured from where the tabs are when it is resized.
        if (settings.GetNodeOrNull<Control>("ScrollContainer/Mask") is { } mask && mask.HasMethod("OnResized"))
            mask.Call("OnResized");

        if (settings.GetNodeOrNull<Control>("ScrollContainer/Scrollbar") is { } scrollbar)
        {
            Capture(scrollbar, captureVisible: false);
            scrollbar.AnchorLeft = 0.5f;
            scrollbar.AnchorRight = 0.5f;
            scrollbar.AnchorTop = 0f;
            scrollbar.AnchorBottom = 1f;
            scrollbar.OffsetLeft = RowsLeft + RowsWidth - 540f + 4f;
            scrollbar.OffsetRight = scrollbar.OffsetLeft + 48f;
            scrollbar.OffsetTop = rowsTop;
            scrollbar.OffsetBottom = -BottomGap;
        }

        UpdateScrollRoom(settings);
    }

    /// <summary>
    /// Every row and every widget in a row is at least <paramref name="minimum"/> units tall where its panel is drawn
    /// <see cref="Scale"/> times larger, so each is 44 points to a finger: the game's own rows are 64 units (34 points at that
    /// scale), and the tickbox or paginator inside a row can be smaller still.
    /// </summary>
    private void GrowRows(Control content, float minimum)
    {
        foreach (Node child in content.GetChildren())
        {
            if (child is not Control row || row is ColorRect || !IsRow(row)) continue;
            GrowHeight(row, minimum);
            foreach (Node inside in row.GetChildren())
            {
                if (inside is not Control widget || widget is Label or RichTextLabel or ColorRect) continue;
                GrowHeight(widget, minimum);
            }
        }
    }

    private static bool IsRow(Control control)
    {
        string name = control.Name.ToString();
        return !name.Contains("Divider", StringComparison.Ordinal) && !name.StartsWith("Padding", StringComparison.Ordinal);
    }

    /// <summary>
    /// Input's labels wrap inside their fixed columns, and are raised to at least 11 points: on the desktop a long key name
    /// widens its column, which would narrow the whole table on the phone.
    /// </summary>
    private void RaiseInputText(Control content)
    {
        foreach (Node node in Traverse(content))
        {
            if (node is not Control control) continue;
            if (control is not (Label or RichTextLabel)) continue;
            if (control is Label label && label.Text.Trim().Length == 0) continue;
            if (control is RichTextLabel rich && rich.GetParsedText().Trim().Length == 0) continue;
            FloorText(control, InputFont);
        }
    }

    /// <summary>
    /// The game scrolls a panel by its own height, not its drawn one: a spacer at the end of a scaled panel's rows makes the
    /// panel as tall to the scroll as its rows are drawn, so the last row can be scrolled into view.
    /// </summary>
    private void UpdateScrollRoom(NSettingsScreen settings)
    {
        if (settings.GetNodeOrNull<Control>(ClipperPath) is not { } clipper) return;
        foreach (Node child in clipper.GetChildren())
        {
            if (child is not NSettingsPanel panel || IsInputPanel(panel) || !panel.IsNodeReady()) continue;
            if (panel.GetNodeOrNull<VBoxContainer>("VBoxContainer") is not { } content) continue;
            Control? spacer = content.GetNodeOrNull<Control>(SpacerName);
            if (spacer == null)
            {
                spacer = new Control { Name = SpacerName, MouseFilter = Control.MouseFilterEnum.Ignore, FocusMode = Control.FocusModeEnum.None };
                content.AddChild(spacer);
            }
            content.MoveChild(spacer, content.GetChildCount() - 1);
            spacer.Visible = true;
            float rows = 0f;
            int count = 0;
            foreach (Node row in content.GetChildren())
            {
                if (row == spacer || row is not Control control || !control.Visible) continue;
                rows += control.GetCombinedMinimumSize().Y;
                count++;
            }
            rows += Math.Max(0, count - 1) * content.GetThemeConstant("separation");
            float room = MathF.Ceiling(rows * (Scale - 1f));
            if (Math.Abs(spacer.CustomMinimumSize.Y - room) > 0.5f) spacer.CustomMinimumSize = new Vector2(0f, room);
            panel.Call(NSettingsPanel.MethodName.RefreshSize);
        }
    }

    /// <summary>Gives each touch target of a panel drawn <paramref name="drawnScale"/> times larger a touch area of 44 points.</summary>
    private void PadTargets(Control root, float drawnScale)
    {
        float minimum = PortraitBands.MinTarget / Math.Max(drawnScale, 0.01f);
        foreach (Node node in Traverse(root))
        {
            if (node is not Control control || !PortraitAudit.IsTouchTarget(control) || PhoneHitArea.IsContainer(control)) continue;
            Vector2 size = control.Size;
            Vector2 combined = control.GetCombinedMinimumSize();
            size = new Vector2(Math.Max(size.X, combined.X), Math.Max(size.Y, combined.Y));
            if (size.X <= 0f || size.Y <= 0f) continue;
            float padX = Math.Max(0f, (minimum - size.X) / 2f);
            float padY = Math.Max(0f, (minimum - size.Y) / 2f);
            if (padX > 0f || padY > 0f) PhoneHitArea.Grow(control, padX, padY, padX, padY);
        }
    }

    private static bool IsInputPanel(Control panel) => panel.GetType().Name == InputPanelName;

    /// <summary>The settings screen's own parts, for the drag and the dropdowns: the rows area, its scroll and scrollbar.</summary>
    private void Register(NSettingsScreen screen)
    {
        var state = new ScreenState();
        if (screen.GetNodeOrNull<Control>(ClipperPath) is { } clip) state.Clip = clip.GetInstanceId();
        if (screen.GetNodeOrNull<Control>("ScrollContainer") is { } scroll) state.Scroll = scroll.GetInstanceId();
        if (screen.GetNodeOrNull<Control>("ScrollContainer/Scrollbar") is { } bar) state.Scrollbar = bar.GetInstanceId();
        foreach (Node node in Traverse(screen))
        {
            if (_dropdownType != null && _dropdownType.IsInstanceOfType(node))
            {
                state.Dropdowns.Add(node.GetInstanceId());
                RegisterDropdown((Control)node, state.Clip);
            }
            else if (_sliderType != null && _sliderType.IsInstanceOfType(node))
            {
                state.Sliders.Add(node.GetInstanceId());
            }
        }
        _screens[screen.GetInstanceId()] = state;
    }

    /// <summary>
    /// The press under a swipe is let go, so a scroll never toggles a tickbox, picks a language or moves a slider: the one
    /// place that knows how the game records a press is <see cref="PhoneSwipeRelease"/>. A drag on a slider or the scrollbar
    /// is the game's own and is left alone, and so is the game's own drag on the rows area's background.
    /// </summary>
    private void OnWindowInput(InputEvent input)
    {
        if (_tree == null || !GodotObject.IsInstanceValid(_tree.Root)) { Reset(); return; }
        // Outside Settings this handler must not see the game's input at all: a Settings screen that was closed can stay
        // registered in the tree, and its stale state must never inspect or consume presses on another screen (the event
        // options found this as a dead tap). The visibility flag is refreshed once per frame, so the per-event path is one
        // bool and nothing else; a gesture left when Settings closed is cleared by the next Begin.
        if (!SafeAreaCanvas.IsPortrait) { Reset(); return; }
        if (!_surfaceVisible) return;
        try
        {
            // The tap that dismissed an open list is the list's: neither it nor its emulated mouse twin (nor their
            // releases) may reach the GUI, or it lands on whatever is under it and closes the settings screen.
            if (Swallowing(input))
            {
                _tree.Root.SetInputAsHandled();
                return;
            }
            switch (input)
            {
                case InputEventScreenTouch touch when touch.Index == 0:
                    if (touch.Pressed) Begin(input, ToCanvas(touch.Position));
                    else End();
                    return;
                case InputEventScreenDrag drag when drag.Index == 0:
                    Move(ToCanvas(drag.Position));
                    return;
                // The Linux host and the desktop editor send a mouse; the phone sends touches (and, with the game's
                // emulate-mouse-from-touch setting, both). Absolute movement keeps the pair from scrolling twice.
                case InputEventMouseButton button when button.ButtonIndex == MouseButton.Left:
                    if (button.Pressed) Begin(input, ToCanvas(button.Position));
                    else End();
                    return;
                case InputEventMouseMotion motion:
                    if (_down) Move(ToCanvas(motion.Position));
                    return;
            }
        }
        catch (Exception error)
        {
            GD.PrintErr("Portrait settings drag: ", error.Message);
            Reset();
        }
    }

    private Vector2 ToCanvas(Vector2 point) => _tree == null ? point : _tree.Root.GetFinalTransform().AffineInverse() * point;

    /// <summary>
    /// Whether any settings surface is on screen: a registered settings screen or its rows clip, or an open dropdown list
    /// (the test host can report the screen itself as not visible while the list it opened is clearly drawn).
    /// </summary>
    private bool SettingsSurfaceVisible()
    {
        foreach ((ulong id, ScreenState state) in _screens)
        {
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen) &&
                screen.IsInsideTree() && screen.IsVisibleInTree()) return true;
            if (state.Clip != 0 && GodotObject.InstanceFromId(state.Clip) is Control clip &&
                GodotObject.IsInstanceValid(clip) && clip.IsInsideTree() && clip.IsVisibleInTree()) return true;
        }
        foreach ((ulong _, DropdownState state) in _dropdowns)
            if (GodotObject.InstanceFromId(state.List) is Control list && GodotObject.IsInstanceValid(list) &&
                list.IsInsideTree() && list.IsVisibleInTree()) return true;
        return false;
    }

    /// <summary>Whether the feedback screen settings opens holds the view; while it does, a press is not the settings'.</summary>
    private bool FeedbackHolds()
    {
        foreach (ulong id in _feedback)
            if (GodotObject.InstanceFromId(id) is Control feedback && GodotObject.IsInstanceValid(feedback) &&
                feedback.IsVisibleInTree())
                return true;
        return false;
    }

    /// <summary>
    /// What the press lands on: an open dropdown's list, else a settings screen's rows area. The screen's own
    /// <c>IsVisibleInTree</c> says nothing here: with the language list open the test host reports the screen as not visible
    /// while its dropdown (drawn over the rows) plainly is, which kept the list's drag from ever starting. The hit test is
    /// the geometry of the list and the rows area instead.
    /// </summary>
    private void Begin(InputEvent input, Vector2 point)
    {
        // Clear a previous gesture without consuming this new press. If a drag's release was lost (the input shield, a
        // screen change, the window losing focus), End() would mark this press handled and the option under it would
        // never see its MousePressed — a dead tap. The game-over summary drag learned the same lesson.
        End(handleInput: false);
        if (!_ready) return;
        if (FeedbackHolds()) { Note("the feedback screen holds the view", point); return; }
        // An open dropdown's list is drawn over the rows and scrolls itself; its items are what the finger went down on.
        // A press anywhere else dismisses it: the native dismisser is a child of the clipped panel, so a tap below the rows
        // area (or beside it) never reaches it, and the list would stay open.
        foreach (ulong id in _dropdowns.Keys.ToList())
        {
            if (!_dropdowns.TryGetValue(id, out DropdownState? drop) || Resolve(id, drop) is not { } list ||
                !list.IsVisibleInTree()) continue;
            Rect2 listRect = list.GetGlobalRect();
            if (!listRect.HasPoint(point))
            {
                CloseList(id);
                // This tap belongs to the dismisser: swallow it, its emulated mouse twin and every event between and after
                // them until each press kind that arrived has been released, so nothing under it acts — the native
                // dismisser would have consumed the press.
                ArmSwallow(input);
                _tree?.Root.SetInputAsHandled();
                Note($"outside the open list {listRect}: dismissed it", point);
                return;
            }
            if (_listDrag?.GetValue(list) is not Vector2 listDrag) { Note("list has no drag target", point); return; }
            // A list too short to scroll still owns the press: a swipe on it is not a language change.
            float listLimit = _listLimit?.GetValue(list) is float room ? Math.Min(room, 0f) : 0f;
            Start(DragKind.List, list, list, point, listDrag.Y, listLimit);
            return;
        }
        foreach ((ulong id, ScreenState state) in _screens)
        {
            if (state.Clip == 0 || GodotObject.InstanceFromId(state.Clip) is not Control clip ||
                !GodotObject.IsInstanceValid(clip) || !clip.IsVisibleInTree() || !clip.GetGlobalRect().HasPoint(point)) continue;
            if (state.Scrollbar != 0 && GodotObject.InstanceFromId(state.Scrollbar) is Control bar &&
                GodotObject.IsInstanceValid(bar) && bar.GetGlobalRect().HasPoint(point)) { Note("on the scrollbar", point); return; }
            foreach (ulong sliderId in state.Sliders)
                if (GodotObject.InstanceFromId(sliderId) is Control slider && GodotObject.IsInstanceValid(slider) &&
                    slider.IsVisibleInTree() && slider.GetGlobalRect().HasPoint(point)) { Note("on a slider", point); return; }
            if (state.Scroll == 0 || GodotObject.InstanceFromId(state.Scroll) is not Control scroll ||
                !GodotObject.IsInstanceValid(scroll)) { Note("no scroll view", point); return; }
            if (_scrollLimit?.GetValue(scroll) is not float limit || limit > -0.5f) { Note("the rows fit", point); return; }
            if (_scrollDragY?.GetValue(scroll) is not float initial) { Note("no scroll target", point); return; }
            Start(DragKind.Scroll, scroll, clip, point, initial, limit);
            return;
        }
        Note("no open list and no rows area at the press", point);
    }

    /// <summary>
    /// Starts swallowing the dismiss tap: the press that dismissed the list, its emulated mouse twin, everything between and
    /// after them. Each press kind that arrives must be released before swallowing stops (so a held finger leaks nothing and
    /// a quick next tap is not eaten); <see cref="SwallowCapMs"/> only recovers if a release never comes.
    /// </summary>
    private void ArmSwallow(InputEvent input)
    {
        _swallowing = true;
        _swallowDeadline = Time.GetTicksMsec() + SwallowCapMs;
        _swallowTouchPress = input is InputEventScreenTouch touch && touch.Index == 0 && touch.Pressed;
        _swallowMousePress = input is InputEventMouseButton button && button.ButtonIndex == MouseButton.Left && button.Pressed;
        _swallowTouchRelease = false;
        _swallowMouseRelease = false;
    }

    /// <summary>Whether this event belongs to a dismiss tap; also notes the releases that end it.</summary>
    private bool Swallowing(InputEvent input)
    {
        if (!_swallowing) return false;
        if (Time.GetTicksMsec() > _swallowDeadline)
        {
            _swallowing = false;
            return false;
        }
        if (input is InputEventScreenTouch touch && touch.Index == 0)
        {
            if (touch.Pressed) _swallowTouchPress = true;
            else _swallowTouchRelease = true;
        }
        else if (input is InputEventMouseButton button && button.ButtonIndex == MouseButton.Left)
        {
            if (button.Pressed) _swallowMousePress = true;
            else _swallowMouseRelease = true;
        }
        // Over once every press kind that arrived has been released; this last release is swallowed with the rest.
        if ((!_swallowTouchPress || _swallowTouchRelease) && (!_swallowMousePress || _swallowMouseRelease) &&
            (_swallowTouchPress || _swallowMousePress))
            _swallowing = false;
        return true;
    }

    /// <summary>
    /// Closes a dropdown the way its own dismisser does (<c>NDropdown.CloseDropdown</c> hides the list and its dismisser and
    /// takes the focus back). The native dismisser cannot do it from outside the panel's clipping ancestor.
    /// </summary>
    private void CloseList(ulong id)
    {
        if (GodotObject.InstanceFromId(id) is Node dropdown && GodotObject.IsInstanceValid(dropdown) &&
            dropdown.HasMethod("CloseDropdown"))
            dropdown.Call("CloseDropdown");
        if (_dropdowns.TryGetValue(id, out DropdownState? state))
        {
            state.Button = Vector2.Inf;
            state.Covers = false;
        }
        End();
    }

    /// <summary>Why a press started no swipe; printed once per press while settings is open (a handful per run).</summary>
    private static void Note(string why, Vector2 point) =>
        GD.Print($"Portrait settings: press at ({point.X:F0},{point.Y:F0}) starts no swipe: {why}.");

    private void Start(DragKind kind, Control target, Control surface, Vector2 point, float initial, float limit)
    {
        _down = true;
        _dragging = false;
        _kind = kind;
        _target = target.GetInstanceId();
        _surface = surface.GetInstanceId();
        _startY = point.Y;
        _initial = initial;
        _limit = limit;
    }

    private void Move(Vector2 point)
    {
        if (!_down || _target == 0) return;
        if (GodotObject.InstanceFromId(_target) is not Control target || !GodotObject.IsInstanceValid(target) ||
            !target.IsVisibleInTree())
        {
            End();
            return;
        }
        float moved = point.Y - _startY;
        if (!_dragging)
        {
            if (!SwipeMath.Vertical(moved, DragDistance)) return;
            // A list's native drag flag is set by the emulated mouse press, but its motion path does not scroll a touch drag
            // inside the list: clear the flag and drive the list's own drag target from here instead of deferring to it.
            if (_kind == DragKind.Scroll && _scrollDragging?.GetValue(target) is true)
            {
                Note("the game's own rows drag owns this gesture", point);
                Reset();
                return;
            }
            if (_kind == DragKind.List) _listDragging?.SetValue(target, false);
            _dragging = true;
            int released = Release();
            if (_kind == DragKind.Scroll) _scrollDragging?.SetValue(target, true);
            GD.Print($"Portrait settings: {_kind} swipe from y={_startY:F0} by {moved:F0} (limit {_limit:F0}), " +
                     $"released {released} press(es) under {target.Name}.");
        }
        float scale = Math.Max(0.01f, Math.Abs(target.GetGlobalTransform().Scale.Y));
        float value = Math.Clamp(_initial + moved / scale, Math.Min(0f, _limit), 0f);
        if (_kind == DragKind.Scroll) _scrollDragY?.SetValue(target, value);
        else if (_listDrag != null) _listDrag.SetValue(target, new Vector2(0f, value));
        _tree?.Root.SetInputAsHandled();
    }

    /// <summary>Lets go of the press under the swipe: the language item or tickbox the finger went down on.</summary>
    private int Release()
    {
        if (GodotObject.InstanceFromId(_surface) is Control screen && GodotObject.IsInstanceValid(screen))
            return PhoneSwipeRelease.Shared.Release(screen);
        return 0;
    }

    private void End(bool handleInput = true)
    {
        if (_dragging)
        {
            if (_target != 0 && GodotObject.InstanceFromId(_target) is Control target && GodotObject.IsInstanceValid(target))
            {
                if (_kind == DragKind.Scroll) _scrollDragging?.SetValue(target, false);
                else if (_kind == DragKind.List) _listDragging?.SetValue(target, false);
            }
            Release();
            // The finger lift is the swipe's, not a tap: the list item or tickbox it ends over must not act on it, even
            // when press, drag and release arrive in one frame. A stale drag cleared by the next press must not take
            // that press, so Begin() passes handleInput: false.
            if (handleInput) _tree?.Root.SetInputAsHandled();
        }
        _down = false;
        _dragging = false;
        _kind = DragKind.None;
        _target = 0;
        _surface = 0;
    }

    /// <summary>Abandons a gesture without marking the current event handled (Reset runs on unrelated input and teardown).</summary>
    private void Reset() => End(handleInput: false);

    // ---- dropdowns --------------------------------------------------------------------------------------------------------

    private void RegisterDropdown(Control dropdown, ulong area)
    {
        ulong id = dropdown.GetInstanceId();
        if (!_dropdowns.TryGetValue(id, out DropdownState? state))
            _dropdowns[id] = state = new DropdownState();
        state.Area = area;
        Resolve(id, state);
    }

    /// <summary>The dropdown's list, resolved once from the game's own fields (both scenes lay it out differently).</summary>
    private Control? Resolve(ulong id, DropdownState state)
    {
        // A rebuilt or freed list (a language change rebuilds it) is looked up again rather than read through a stale node.
        if (state.List != 0 && !GodotObject.IsInstanceIdValid(state.List)) state.List = 0;
        if (state.Dismisser != 0 && !GodotObject.IsInstanceIdValid(state.Dismisser)) state.Dismisser = 0;
        if (state.List == 0 && GodotObject.InstanceFromId(id) is Control dropdown)
        {
            if (_dropdownList?.GetValue(dropdown) is Control list && GodotObject.IsInstanceValid(list))
                state.List = list.GetInstanceId();
            if (_dropdownDismisser?.GetValue(dropdown) is Control dismisser && GodotObject.IsInstanceValid(dismisser))
                state.Dismisser = dismisser.GetInstanceId();
        }
        return state.List != 0 && GodotObject.InstanceFromId(state.List) is Control found && GodotObject.IsInstanceValid(found)
            ? found
            : null;
    }

    /// <summary>
    /// Keeps an open list phone sized: every row a 44 point target, the list never taller than the area it opens in, below the
    /// button when it fits there and above it otherwise, and the dismisser over the whole screen. Runs only while a list is
    /// shown, and only when the button or its rows have changed.
    /// </summary>
    public void OnProcessFrame(Window root, float capHeight)
    {
        _surfaceVisible = SettingsSurfaceVisible();
        if (_feedback.Count > 0) HoldFeedback(root, capHeight);
        if (_dropdowns.Count == 0 || !SafeAreaCanvas.IsPortrait) return;
        Vector2 canvas = root.GetVisibleRect().Size;
        foreach ((ulong id, DropdownState state) in _dropdowns)
        {
            Control? list = Resolve(id, state);
            if (list == null || !list.IsVisibleInTree()) { state.Button = Vector2.Inf; state.Covers = false; continue; }
            if (GodotObject.InstanceFromId(id) is not Control dropdown || !GodotObject.IsInstanceValid(dropdown) ||
                !dropdown.IsVisibleInTree()) continue;
            Vector2 button = dropdown.GetGlobalRect().Position;
            var bounds = new Rect2(Vector2.Zero, canvas);
            if (state.Area != 0 && GodotObject.InstanceFromId(state.Area) is Control area && GodotObject.IsInstanceValid(area))
                bounds = area.GetGlobalRect();
            if (state.Button == button) continue; // no movement since the last pass
            state.Button = button;
            KeepOpen(dropdown, list, state, bounds, canvas);
        }
    }

    private void KeepOpen(Control dropdown, Control list, DropdownState state, Rect2 bounds, Vector2 canvas)
    {
        float scale = Math.Max(0.01f, Math.Abs(list.GetGlobalTransform().Scale.Y));
        if (_dropdownItems?.GetValue(dropdown) is Control items)
        {
            float row = PortraitBands.MinTarget / scale;
            foreach (Node child in items.GetChildren())
            {
                if (child is not Control item || !_itemType!.IsInstanceOfType(item)) continue;
                GrowHeight(item, row);
                // The list sizes itself from its rows' Size (NDropdownContainer.RefreshLayout sums Size.Y), and a container
                // only applies its children's new minimums on its next sort. Write the grown height now, or the list is
                // sized for the native one-row height while the rows are already 44 points tall: the first row is clipped
                // and a longer list cannot be scrolled to its last entry.
                if (item.Size.Y + 0.5f < row) item.Size = new Vector2(item.Size.X, row);
            }
        }
        Rect2 buttonRect = dropdown.GetGlobalRect();
        Rect2 button = buttonRect;
        float below = bounds.End.Y - button.End.Y;
        float above = button.Position.Y - bounds.Position.Y;
        // 386 drawn units: three 44 point rows.
        bool useBelow = below >= DropdownHeight || below >= above;
        float room = Math.Max(useBelow ? below : above, 0f);
        float maxHeight = MathF.Min(DropdownHeight / scale, room / scale);
        if (_listMaxHeight?.GetValue(list) is not float current || Math.Abs(current - maxHeight) > 0.5f)
        {
            Capture(list, captureVisible: false);
            _listMaxHeight?.SetValue(list, maxHeight);
            if (list.HasMethod("RefreshLayout")) list.Call("RefreshLayout");
        }
        // The list is a child of the scaled panel: its size is in local units, the bounds and the button's rect are drawn.
        Vector2 drawn = list.GetGlobalRect().Size;
        var position = new Vector2(
            Math.Clamp(buttonRect.GetCenter().X - drawn.X / 2f, bounds.Position.X + DropdownMargin,
                Math.Max(bounds.Position.X + DropdownMargin, bounds.End.X - drawn.X - DropdownMargin)),
            useBelow ? button.End.Y : button.Position.Y - drawn.Y);
        if (list.GlobalPosition.DistanceTo(position) > 0.5f)
        {
            Capture(list, captureVisible: false);
            list.GlobalPosition = position;
        }
        CoverScreen(state, canvas);
    }

    /// <summary>
    /// The desktop dismisser is 3000 x 2000 units around the button, which twice overdrawn still leaves the bottom of a tall
    /// screen uncovered when the dropdown sits near the top. In portrait it is the whole screen instead.
    /// </summary>
    private void CoverScreen(DropdownState state, Vector2 canvas)
    {
        if (state.Covers || state.Dismisser == 0) return;
        if (GodotObject.InstanceFromId(state.Dismisser) is not Control dismisser || !GodotObject.IsInstanceValid(dismisser)) return;
        if (dismisser.GetGlobalRect().Encloses(new Rect2(Vector2.Zero, canvas))) { state.Covers = true; return; }
        Capture(dismisser, captureVisible: false);
        dismisser.AnchorLeft = dismisser.AnchorTop = dismisser.AnchorRight = dismisser.AnchorBottom = 0f;
        dismisser.CustomMinimumSize = Vector2.Zero;
        dismisser.Size = canvas / dismisser.GetGlobalTransform().Scale.Abs();
        dismisser.GlobalPosition = Vector2.Zero;
        state.Covers = true;
    }

    // ---- the feedback screen ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Settings' feedback screen: a 368 x 305 unit panel with 80 unit emoji buttons, drawn twice as large on the virtual canvas
    /// its decorations fit (540 wide), centred under the notch. Its back button is moved flush with the left edge, where the
    /// desktop's 40 unit overhang would be twice as far out, and every control is given a 44 point target.
    /// </summary>
    public void ApplyFeedback(Control screen, float capHeight)
    {
        if (_tree == null || !GodotObject.IsInstanceValid(_tree.Root)) return;
        FitFeedback(screen, capHeight, _tree.Root.GetVisibleRect().Size);
        _feedback.Add(screen.GetInstanceId());
    }

    /// <summary>
    /// The touch areas, once the screen is shown and its own containers have laid it out: the screen is created hidden, and a
    /// hidden container gives its children no size to measure. Its back button is placed by <see cref="PortraitNavigationPads"/>,
    /// which owns every back button.
    /// </summary>
    private void PrepareFeedback(Control screen, float scale, float capHeight)
    {
        foreach (Node node in Traverse(screen))
            if (_dropdownType != null && _dropdownType.IsInstanceOfType(node)) RegisterDropdown((Control)node, area: 0);
        PadTargets(screen, scale);
        GD.Print($"Portrait settings: feedback screen drawn {scale:F2}x under cap {capHeight:F0}.");
    }

    /// <summary>Holds the feedback screen's fit; returns the scale it is drawn at.</summary>
    private float FitFeedback(Control screen, float capHeight, Vector2 canvas)
    {
        VirtualCanvas fit = PortraitFitMath.Fit(canvas.X, capHeight, canvas.Y - FeedbackBottomMargin, FeedbackDesignWidth, allowEnlarge: true);
        Vector2 parentOrigin = screen.GetParent() is Control parent ? parent.GetGlobalTransformWithCanvas().Origin : Vector2.Zero;
        var position = new Vector2(fit.Left, fit.Top) - parentOrigin;
        var size = new Vector2(fit.Width, fit.Height);
        var scale = new Vector2(fit.Scale, fit.Scale);
        if (!_snapshots.ContainsKey(screen.GetInstanceId())) Capture(screen, captureVisible: false);
        if (screen.AnchorLeft != 0f || screen.AnchorTop != 0f || screen.AnchorRight != 0f || screen.AnchorBottom != 0f)
        {
            screen.AnchorLeft = 0f;
            screen.AnchorTop = 0f;
            screen.AnchorRight = 0f;
            screen.AnchorBottom = 0f;
        }
        if (screen.PivotOffset != Vector2.Zero) screen.PivotOffset = Vector2.Zero;
        if (screen.Scale != scale) screen.Scale = scale;
        if (screen.Position.DistanceSquaredTo(position) > 0.25f) screen.Position = position;
        if (screen.Size.DistanceSquaredTo(size) > 0.25f) screen.Size = size;
        return fit.Scale;
    }

    private void HoldFeedback(Window root, float capHeight)
    {
        Vector2 canvas = root.GetVisibleRect().Size;
        foreach (ulong id in _feedback)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen) && screen.IsVisibleInTree())
            {
                float scale = FitFeedback(screen, capHeight, canvas);
                if (_prepared.Add(id)) PrepareFeedback(screen, scale, capHeight);
            }
    }

    // ---- shared -----------------------------------------------------------------------------------------------------------

    /// <summary>Puts everything back for landscape: the desktop layout, the game's fonts and text, and its own dropdowns.</summary>
    public void Restore()
    {
        foreach (ulong id in _screens.Keys)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen))
                RemoveTouchAreas(screen);
        foreach (ulong id in _feedback)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen))
                RemoveTouchAreas(screen);
        RefreshPanels();
        foreach ((ulong id, (bool had, int size)) in _fonts)
        {
            if (GodotObject.InstanceFromId(id) is not Control label || !GodotObject.IsInstanceValid(label)) continue;
            if (had) label.AddThemeFontSizeOverride("font_size", size);
            else label.RemoveThemeFontSizeOverride("font_size");
        }
        _fonts.Clear();
        foreach ((ulong id, TextState state) in _texts)
        {
            if (GodotObject.InstanceFromId(id) is not Control control || !GodotObject.IsInstanceValid(control)) continue;
            if (control is Label label) label.AutowrapMode = state.Wrap;
            if (control is RichTextLabel rich)
            {
                rich.AutowrapMode = state.Wrap;
                if (state.RichFonts != null && state.RichHad != null)
                    for (int i = 0; i < RichFontKeys.Length; i++)
                    {
                        if (state.RichHad[i]) rich.AddThemeFontSizeOverride(RichFontKeys[i], state.RichFonts[i]);
                        else rich.RemoveThemeFontSizeOverride(RichFontKeys[i]);
                    }
            }
            if (control is Label ownLabel)
            {
                if (state.HadFont) ownLabel.AddThemeFontSizeOverride("font_size", state.Font);
                else ownLabel.RemoveThemeFontSizeOverride("font_size");
            }
            if (state.Mega is { } mega) SetMegaSize(control, mega.Min, mega.Max);
        }
        _texts.Clear();
        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
        _snapshots.Clear();
        _screens.Clear();
        _dropdowns.Clear();
        _panels.Clear();
        _feedback.Clear();
        _prepared.Clear();
        Reset();
    }

    private void RefreshPanels()
    {
        foreach (ulong id in _panels)
        {
            if (GodotObject.InstanceFromId(id) is not NSettingsPanel panel || !GodotObject.IsInstanceValid(panel)) continue;
            if (panel.GetNodeOrNull<Control>("VBoxContainer/" + SpacerName) is { } spacer) spacer.Visible = false;
            if (panel.IsNodeReady()) panel.Call(NSettingsPanel.MethodName.RefreshSize);
        }
        foreach (ulong id in _screens.Keys)
        {
            if (GodotObject.InstanceFromId(id) is not Control screen || !GodotObject.IsInstanceValid(screen)) continue;
            if (screen.GetNodeOrNull<Control>("ScrollContainer/Mask") is { } mask && mask.HasMethod("OnResized"))
                mask.Call("OnResized");
        }
    }

    private static void RemoveTouchAreas(Node root)
    {
        foreach (Node node in Traverse(root))
            if (node is Control control && control.Name == PhoneHitArea.NodeName && control.GetParent() is Control owner)
                PhoneHitArea.Remove(owner);
    }

    public void PruneDead()
    {
        foreach (ulong id in _snapshots.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _snapshots.Remove(id);
        foreach (ulong id in _fonts.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _fonts.Remove(id);
        foreach (ulong id in _texts.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _texts.Remove(id);
        foreach (ulong id in _panels.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _panels.Remove(id);
        foreach (ulong id in _feedback.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _feedback.Remove(id);
        foreach (ulong id in _prepared.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _prepared.Remove(id);
        foreach (ulong id in _dropdowns.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _dropdowns.Remove(id);
        foreach ((ulong id, ScreenState state) in _screens)
        {
            state.Sliders.RemoveAll(k => !GodotObject.IsInstanceIdValid(k));
            state.Dropdowns.RemoveAll(k => !GodotObject.IsInstanceIdValid(k));
        }
        // A settings screen that left the tree is no longer a surface for this handler. Hidden-but-in-tree screens stay
        // registered (their layout is reused on reopen); SettingsSurfaceVisible keeps the handler off while none is shown.
        foreach (ulong id in _screens.Keys.ToList())
            if (GodotObject.InstanceFromId(id) is not Control screen || !GodotObject.IsInstanceValid(screen) ||
                !screen.IsInsideTree())
                _screens.Remove(id);
        if (_target != 0 && !GodotObject.IsInstanceIdValid(_target)) Reset();
    }

    private PortraitControlSnapshot Capture(Control control, bool captureVisible)
    {
        if (!_snapshots.TryGetValue(control.GetInstanceId(), out PortraitControlSnapshot? snap))
            _snapshots[control.GetInstanceId()] = snap = new PortraitControlSnapshot(control, captureVisible);
        return snap;
    }

    /// <summary>Grows a control's height to <paramref name="minimum"/> units, capturing its own size the first time.</summary>
    private void GrowHeight(Control control, float minimum)
    {
        if (control.CustomMinimumSize.Y >= minimum - 0.5f) return;
        Capture(control, captureVisible: false);
        control.CustomMinimumSize = new Vector2(control.CustomMinimumSize.X, MathF.Ceiling(minimum));
    }

    /// <summary>Sets a label's font size in portrait, keeping the game's own to put back in landscape.</summary>
    private void SetFont(Control label, int size)
    {
        ulong id = label.GetInstanceId();
        if (!_fonts.ContainsKey(id))
        {
            bool had = label.HasThemeFontSizeOverride("font_size");
            _fonts[id] = (had, label.GetThemeFontSize("font_size"));
        }
        label.AddThemeFontSizeOverride("font_size", size);
    }

    /// <summary>Wraps one label inside its column and raises it to <paramref name="font"/>, keeping the game's own sizes.</summary>
    private void FloorText(Control control, int font)
    {
        ulong id = control.GetInstanceId();
        if (!_texts.TryGetValue(id, out TextState? state))
        {
            state = new TextState
            {
                Wrap = control is Label label ? label.AutowrapMode
                    : control is RichTextLabel rich ? rich.AutowrapMode
                    : TextServer.AutowrapMode.Off,
            };
            if (control is Label own)
            {
                state.HadFont = own.HasThemeFontSizeOverride("font_size");
                state.Font = own.GetThemeFontSize("font_size");
            }
            if (control is RichTextLabel text)
            {
                state.RichFonts = new int[RichFontKeys.Length];
                state.RichHad = new bool[RichFontKeys.Length];
                for (int i = 0; i < RichFontKeys.Length; i++)
                {
                    state.RichHad[i] = text.HasThemeFontSizeOverride(RichFontKeys[i]);
                    state.RichFonts[i] = text.GetThemeFontSize(RichFontKeys[i]);
                }
            }
            state.Mega = MegaSize(control);
            _texts[id] = state;
        }
        switch (control)
        {
            case Label label:
                label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                label.AddThemeFontSizeOverride("font_size", font);
                break;
            case RichTextLabel rich:
                rich.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                foreach (string key in RichFontKeys) rich.AddThemeFontSizeOverride(key, font);
                break;
        }
        SetMegaSize(control, font, font);
    }

    /// <summary>A Mega label's own font limits (they auto-size between them, so the theme size alone is not enough).</summary>
    private (int Min, int Max)? MegaSize(Control control)
    {
        if (Limits(control) is not { } pair) return null;
        if (pair.Min.GetValue(control) is not int min || pair.Max.GetValue(control) is not int max) return null;
        return (min, max);
    }

    private void SetMegaSize(Control control, int min, int max)
    {
        if (Limits(control) is not { } pair) return;
        pair.Min.SetValue(control, min);
        pair.Max.SetValue(control, max);
    }

    /// <summary>The Min/Max pair of a plain or rich Mega label, or null for any other control.</summary>
    private (PropertyInfo Min, PropertyInfo Max)? Limits(Control control)
    {
        if (_megaMin?.DeclaringType is { } plain && plain.IsInstanceOfType(control) && _megaMax != null)
            return (_megaMin, _megaMax);
        if (_richMin?.DeclaringType is { } rich && rich.IsInstanceOfType(control) && _richMax != null)
            return (_richMin, _richMax);
        return null;
    }

    private static IEnumerable<Node> Traverse(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Node current = stack.Pop();
            yield return current;
            int count = current.GetChildCount();
            for (int i = count - 1; i >= 0; i--)
                stack.Push(current.GetChild(i));
        }
    }
}
