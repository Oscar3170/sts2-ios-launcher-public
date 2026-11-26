using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;

namespace STS2Mobile.GameHost;

/// <summary>
/// Character select, Blitz and Dread in portrait: the character roster laid out four over three with cross-row focus, the
/// ascension panel under it, the info panel in the upper half, and the confirm button at the safe band's lower right corner,
/// on the 1680 unit menu canvas the screen is fitted to. Blitz and Dread also shift their badge and info button under the
/// notch and grow them for a finger; Dread places its name, score, win streak and rotation tickbox between the header and the
/// roster. The art behind the roster is framed separately by <see cref="PortraitCharacterArtLayout"/>; the mode cards of the
/// singleplayer submenus are <see cref="PortraitModeCardsLayout"/>. The per-screen roster, panel and focus state and the
/// desktop values this owner writes live here and go back in landscape.
/// </summary>
internal sealed class PortraitCharacterSelectLayout
{
    private const string Ns = "MegaCrit.Sts2.Core.Nodes.Screens.";
    public const string CharacterSelectTypeName = Ns + "CharacterSelect.NCharacterSelectScreen";
    public const string BlitzTypeName = Ns + "Blitz.NBlitzScreen";
    public const string DreadTypeName = Ns + "Dread.NDreadScreen";

    // The roster: seven character buttons in two rows (four over three), at most 2.52 times their desktop size, the lower
    // row ending 282 units above the safe band's lower edge so the ascension panel and the confirm button keep their room.
    private const float GridGapX = 23f;
    private const float GridGapY = 14f;
    private const float GridMaxScale = 2.52f;
    private const float GridSideRoom = 64f;
    private const float GridBottomRoom = 282f;

    // The ascension panel: 1.8 times its desktop size, its flame under the roster, the level number centred on the opaque
    // body of the flame rather than its tips.
    private const float AscensionScale = 1.8f;
    private const float AscensionGap = 32f;

    // The confirm button: 2.1 times its desktop size, its show/hide places from the safe band's lower right corner.
    private const float ConfirmScale = 2.1f;
    private const float ConfirmRight = 40f;
    private const float ConfirmBottom = 20f;

    // The info panel: 526 units wide, 404 tall (434 on Blitz), drawn at most 2.35 times, centred on the safe band's middle
    // or above the ascension panel, whichever is higher.
    private const float InfoWidth = 526f;
    private const float InfoHeight = 404f;
    private const float InfoBlitzHeight = 434f;
    private const float InfoMaxScale = 2.35f;
    private const float InfoSideRoom = 44f;
    private const float InfoTopFraction = 0.445f;
    private const float InfoAscensionGap = 64f;

    // Dread keeps the character-select roster and confirm button; its name, score, win streak and rotation tickbox take the
    // places measured from the desktop layout: the name under the top edge, the score and streak above the roster and the
    // tickbox between them, each grown for a phone.
    private const float DreadNameTop = 260f;
    private const float DreadNameScale = 1.7f;
    private const float DreadScoreScale = 1.65f;
    private const float DreadStreakScale = 1.3f;
    private const float DreadTickboxScale = 1.8f;

    // Room between a right-aligned header icon and the safe edge, so its pad can reach 127 units on both sides.
    private const float HeaderRightMargin = 32f;

    /// <summary>One character-select screen's resolved nodes, roster/focus plans and portrait-only flags.</summary>
    private sealed class SelectState
    {
        public float Scale = 1f;
        public Rect2 Safe;

        // Resolved scene nodes and the roster/focus plans, so the per-frame pass does no path lookups or LINQ.
        public readonly Dictionary<string, Control?> Named = new(StringComparer.Ordinal);
        public Control? GridControl;
        public readonly List<Control> Roster = new();
        public int RosterChildren = -1;
        public Vector2 RosterCell;
        public bool FocusDirty = true;
        public readonly Dictionary<ulong, NodePath[]> FocusPaths = new();
        public readonly HashSet<ulong> ScaledHeaders = new();
        public bool ConfirmScaled;
        public bool ConfirmPlaced;
        public bool InfoPlaced;
        public Container? Grid;
        public Action? GridSort;

        /// <summary>Forgets what only holds while portrait is on; the snapshots put the desktop layout back.</summary>
        public void ResetPortrait()
        {
            if (GridSort != null && GodotObject.IsInstanceValid(Grid)) Grid.SortChildren -= GridSort;
            GridSort = null;
            Grid = null;
            ConfirmScaled = false;
            ConfirmPlaced = false;
            InfoPlaced = false;
            GridControl = null;
            Named.Clear();
            Roster.Clear();
            RosterChildren = -1;
            RosterCell = Vector2.Zero;
            FocusDirty = true;
            FocusPaths.Clear();
            ScaledHeaders.Clear();
        }
    }

    private readonly Dictionary<ulong, SelectState> _screens = new();
    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private readonly List<(ulong Id, NodePath? Left, NodePath? Right, NodePath? Top, NodePath? Bottom)> _focus = new();

    private SelectState GetState(Control screen)
    {
        ulong id = screen.GetInstanceId();
        if (!_screens.TryGetValue(id, out SelectState? state)) _screens[id] = state = new SelectState();
        return state;
    }

    private PortraitControlSnapshot Capture(Control control)
    {
        ulong id = control.GetInstanceId();
        if (!_snapshots.TryGetValue(id, out PortraitControlSnapshot? snap))
            _snapshots[id] = snap = new PortraitControlSnapshot(control, captureVisible: false);
        return snap;
    }

    /// <summary>Character select and Blitz: the roster, ascension, info and confirm places, plus Blitz's header row.</summary>
    public void Apply(Control screen, Rect2 safe, float scale, bool blitz)
    {
        SelectState state = GetState(screen);
        state.Safe = safe;
        state.Scale = scale;
        LayoutCharacterSelect(screen, state, blitz);
    }

    /// <summary>Dread: the character-select roster and confirm button, its own name/score/streak/tickbox, and its header row.</summary>
    public void ApplyDread(Control screen, Rect2 safe, float scale)
    {
        SelectState state = GetState(screen);
        state.Safe = safe;
        state.Scale = scale;
        LayoutDread(screen, state);
    }

    private void LayoutCharacterSelect(Control screen, SelectState state, bool blitz)
    {
        Rect2 safe = state.Safe;
        state.GridControl ??= Roster(screen, state);
        float gridTop = ArrangeCharacterButtons(state, screen, state.GridControl, safe);
        PrepareConfirm(state, screen, safe);

        // Blitz's title, its badge and its info button sit along the top edge, under the notch.
        foreach (string name in new[] { "BlitzPlaceholder", "%UnderConstruction", "%BlitzInfoButton" })
            ShiftTopControl(screen, state, name, safe.Position.Y);
        // The badge's icon and label scale together until the label reads 11 points; the icon-only info button gets a
        // matching visual size. Both are placed inside the safe band's top edge, clear of the centred title.
        ScaleHeaderLabel(screen, state, "%UnderConstruction", safe, minPoints: 11f, right: false);
        ScaleHeaderLabel(screen, state, "%BlitzInfoButton", safe, minPoints: 0f, right: true);
        GrowHeaderTarget(screen, "%BlitzInfoButton");

        float ascensionTop = PrepareAscension(screen, state, safe, gridTop);
        PrepareInfo(screen, state, blitz, safe, ascensionTop);
        // Last, so the ascension panel (moved last for its arrows) cannot cover the back's input.
        EnsureBackOnTop(screen, state);
    }

    private void LayoutDread(Control screen, SelectState state)
    {
        Rect2 safe = state.Safe;
        float gridTop = ArrangeCharacterButtons(state, screen, state.GridControl ??= Roster(screen, state), safe);
        PrepareConfirm(state, screen, safe);

        // Dread's badge and info button sit on the top edge on the desktop, under the notch here: shift them with the safe
        // band like Blitz's header.
        foreach (string name in new[] { "%UnderConstruction", "%DreadInfoButton" })
            ShiftTopControl(screen, state, name, safe.Position.Y);
        ScaleHeaderLabel(screen, state, "%UnderConstruction", safe, minPoints: 11f, right: false);
        ScaleHeaderLabel(screen, state, "%DreadInfoButton", safe, minPoints: 0f, right: true);
        GrowHeaderTarget(screen, "%DreadInfoButton");

        // A finger's target is 127 canvas units, 197.5 of this screen's units; the tickbox and the win streak above it are
        // short wide blocks, so they are stacked with the room between them that the pad needs to reach that size.
        float tickHeight = DreadHeight(screen, state, "RotatingTickbox", DreadTickboxScale);
        float streakHeight = DreadHeight(screen, state, "DreadWinStreak", DreadStreakScale);
        float scoreHeight = DreadHeight(screen, state, "ScoreContainer", DreadScoreScale);
        float tickTop = gridTop - 150f;
        float gap = Math.Max(24f, 197.5f - (tickHeight + streakHeight) / 2f);
        float streakTop = tickTop - gap - streakHeight;
        float scoreTop = streakTop - 32f - scoreHeight;

        PlaceDreadControl(screen, state, "DreadNameText", safe.Position.Y + DreadNameTop, DreadNameScale, safe);
        PlaceDreadControl(screen, state, "ScoreContainer", scoreTop, DreadScoreScale, safe);
        PlaceDreadControl(screen, state, "DreadWinStreak", streakTop, DreadStreakScale, safe);
        PlaceDreadControl(screen, state, "RotatingTickbox", tickTop, DreadTickboxScale, safe);
        EnsureBackOnTop(screen, state);
    }

    /// <summary>The icon-only header buttons are drawn smaller than a finger: pad them to 127 canvas units in place.</summary>
    private static void GrowHeaderTarget(Control screen, string name)
    {
        if (screen.GetNodeOrNull<Control>(name) is not { } button) return;
        Vector2 scale = button.GetGlobalTransformWithCanvas().Scale.Abs();
        if (scale.X < 0.01f || scale.Y < 0.01f) return;
        float padX = Mathf.Max(0f, (PortraitBands.MinTarget - button.Size.X * scale.X) * 0.5f) / scale.X;
        float padY = Mathf.Max(0f, (PortraitBands.MinTarget - button.Size.Y * scale.Y) * 0.5f) / scale.Y;
        if (padX > 0f || padY > 0f) PhoneHitArea.Grow(button, padX, padY, padX, padY);
    }

    private static Control? Roster(Control screen, SelectState state) =>
        Node(screen, state, "CharSelectButtons/ButtonContainer") ?? Node(screen, state, "%ButtonContainer");

    /// <summary>
    /// Lays the character buttons out four over three, each at the largest cell the roster's minimum sizes need and the width
    /// allows, with the rows' focus crossing between them. Returns the roster's top in the screen's units.
    /// </summary>
    private float ArrangeCharacterButtons(SelectState state, Control screen, Control? grid, Rect2 safe)
    {
        float fallback = safe.End.Y - GridBottomRoom;
        if (grid == null) return fallback;
        // Re-scan the row only when its children change or a sort says the game rebuilt something.
        if (state.RosterChildren != grid.GetChildCount() || state.FocusDirty)
        {
            state.Roster.Clear();
            Vector2 scan = Vector2.Zero;
            foreach (Node child in grid.GetChildren())
            {
                if (child is not Control control || !control.Visible || control.GetType().Name != "NCharacterSelectButton") continue;
                state.Roster.Add(control);
                scan = scan.Max(control.GetCombinedMinimumSize());
            }
            state.RosterChildren = grid.GetChildCount();
            state.RosterCell = scan;
            state.FocusDirty = false;
            state.FocusPaths.Clear();
        }
        List<Control> buttons = state.Roster;
        if (buttons.Count == 0) return fallback;

        int columns = (buttons.Count + 1) / 2;
        Vector2 cell = state.RosterCell;
        if (cell.X < 1f || cell.Y < 1f)
        {
            state.FocusDirty = true;
            return fallback;
        }

        float scale = Math.Min(GridMaxScale, (safe.Size.X - GridSideRoom) / (columns * (cell.X + GridGapX) - GridGapX));
        if (scale < 0.3f) scale = 0.3f;
        float width = safe.Size.X / scale;
        float height = buttons.Count > 1 ? 2f * cell.Y + GridGapY : cell.Y;
        float top = safe.End.Y - GridBottomRoom - height * scale;

        Capture(grid);
        if (grid.AnchorLeft != 0f || grid.AnchorTop != 0f || grid.AnchorRight != 0f || grid.AnchorBottom != 0f)
            grid.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        // A row container must not take a touch itself; its buttons still receive theirs.
        if (grid.MouseFilter != Control.MouseFilterEnum.Ignore) grid.MouseFilter = Control.MouseFilterEnum.Ignore;
        if (grid.GetParent() is Control gridHolder && gridHolder.MouseFilter != Control.MouseFilterEnum.Ignore)
            gridHolder.MouseFilter = Control.MouseFilterEnum.Ignore;
        SetScale(grid, new Vector2(scale, scale), Vector2.Zero);
        Vector2 target = screen.GetGlobalTransformWithCanvas() * new Vector2(safe.Position.X, top);
        if (grid.GlobalPosition.DistanceSquaredTo(target) > 0.25f) grid.GlobalPosition = target;

        // The game sorts the row again when a character unlocks or a random button appears; lay out again right after it.
        if (grid is Container container && state.Grid != container)
        {
            if (state.GridSort != null && GodotObject.IsInstanceValid(state.Grid)) state.Grid!.SortChildren -= state.GridSort;
            state.Grid = container;
            state.GridSort = () =>
            {
                state.FocusDirty = true;
                state.RosterChildren = -1;
                if (GodotObject.IsInstanceValid(container) && state.Scale > 0f)
                    ArrangeCharacterButtons(state, screen, container, state.Safe);
            };
            container.SortChildren += state.GridSort;
        }

        void PositionRow(int start, int count, float y)
        {
            float left = (width - count * (cell.X + GridGapX) + GridGapX) * 0.5f;
            for (int i = 0; i < count; i++)
            {
                Control button = buttons[start + i];
                if (button.Size.DistanceSquaredTo(cell) > 0.25f) button.Size = cell;
                var place = new Vector2(left + i * (cell.X + GridGapX), y);
                if (button.Position.DistanceSquaredTo(place) > 0.25f) button.Position = place;
            }
        }
        PositionRow(0, columns, 0f);
        if (columns < buttons.Count) PositionRow(columns, buttons.Count - columns, cell.Y + GridGapY);

        // The desired focus grid is built once per roster; the game writes its own one-row neighbours on open/pick, so each
        // frame only compares the four paths and rewrites the plan when a path changed (no GetPath per frame).
        if (state.FocusPaths.Count != buttons.Count)
        {
            state.FocusPaths.Clear();
            for (int i = 0; i < buttons.Count; i++)
            {
                Control button = buttons[i];
                int row = i < columns ? 0 : 1;
                int rowStart = row == 0 ? 0 : columns;
                int rowCount = row == 0 ? columns : buttons.Count - columns;
                int inRow = i - rowStart;
                bool upper = row == 0;
                Control other = (upper ? buttons.Skip(columns) : buttons.Take(columns))
                    .MinBy(candidate => MathF.Abs(candidate.Position.X - button.Position.X)) ?? button;
                state.FocusPaths[button.GetInstanceId()] = new[]
                {
                    buttons[rowStart + (inRow + rowCount - 1) % rowCount].GetPath(),
                    buttons[rowStart + (inRow + 1) % rowCount].GetPath(),
                    (upper ? button : other).GetPath(),
                    (upper ? other : button).GetPath(),
                };
            }
        }
        for (int i = 0; i < buttons.Count; i++)
        {
            Control button = buttons[i];
            SaveFocus(button);
            if (!state.FocusPaths.TryGetValue(button.GetInstanceId(), out NodePath[]? plan)) continue;
            if (button.FocusNeighborLeft == plan[0] && button.FocusNeighborRight == plan[1] &&
                button.FocusNeighborTop == plan[2] && button.FocusNeighborBottom == plan[3]) continue;
            button.FocusNeighborLeft = plan[0];
            button.FocusNeighborRight = plan[1];
            button.FocusNeighborTop = plan[2];
            button.FocusNeighborBottom = plan[3];
        }
        return top;
    }

    /// <summary>Scales the ascension panel, centers its number on the flame and places it under the roster; returns its top.</summary>
    private float PrepareAscension(Control screen, SelectState state, Rect2 safe, float gridTop)
    {
        if (Node(screen, state, "%AscensionPanel") is not { } ascension) return gridTop;
        Capture(ascension);
        if (ascension.MouseFilter != Control.MouseFilterEnum.Ignore) ascension.MouseFilter = Control.MouseFilterEnum.Ignore;
        Vector2 size = ascension.Size;
        ascension.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft, Control.LayoutPresetMode.KeepSize);
        ascension.Size = size;
        SetScale(ascension, new Vector2(AscensionScale, AscensionScale), Vector2.Zero);

        Control? art = Node(ascension, state, "Background", "asc.background");
        Rect2 artRect = art?.GetRect() ?? new Rect2(Vector2.Zero, size);
        Vector2 place = new(
            (safe.Size.X - size.X * AscensionScale) * 0.5f,
            gridTop - artRect.End.Y * AscensionScale - AscensionGap);
        if (ascension.Position.DistanceSquaredTo(place) > 0.25f) ascension.Position = place;
        float top = place.Y + artRect.Position.Y * AscensionScale;

        if (Node(ascension, state, "HBoxContainer/AscensionContainer/AscensionLevel", "asc.number") is { } number)
        {
            if (number.OffsetLeft != 0f) number.OffsetLeft = 0f;
            if (number.OffsetRight != 0f) number.OffsetRight = 0f;
            if (number.OffsetTop != 11f) number.OffsetTop = 11f;
            if (number.OffsetBottom != 11f) number.OffsetBottom = 11f;
        }
        // The info panel is the screen's later sibling and swallows taps on anything of its box: keep the ascension row (and
        // its arrows) last among the screen's children. Reordering within the parent does not exit the tree.
        if (ascension.GetParent() is Control parent && !PhoneHitArea.IsContainer(parent) &&
            ascension.GetIndex() != parent.GetChildCount() - 1)
            parent.MoveChild(ascension, parent.GetChildCount() - 1);
        return top;
    }

    /// <summary>
    /// Places the info panel centred in the safe band's upper half, above the ascension panel once it is there. While the
    /// game's own slide runs the panel is left to the tween; the slide's resting place is kept at the new spot so the next
    /// pick starts from it.
    /// </summary>
    private void PrepareInfo(Control screen, SelectState state, bool blitz, Rect2 safe, float ascensionTop)
    {
        if (Node(screen, state, "InfoPanel") is not { } info) return;
        Control plate = Node(info, state, "NinePatchRect", "info.plate") ?? info;
        Capture(info);
        if (info.MouseFilter != Control.MouseFilterEnum.Ignore) info.MouseFilter = Control.MouseFilterEnum.Ignore;
        info.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft, Control.LayoutPresetMode.KeepSize);
        info.PivotOffset = Vector2.Zero;
        var size = new Vector2(InfoWidth, blitz ? InfoBlitzHeight : InfoHeight);
        if (info.Size.DistanceSquaredTo(size) > 0.25f) info.Size = size;
        float scale = Math.Min(InfoMaxScale, (safe.Size.X - InfoSideRoom) / Math.Max(1f, plate.Size.X));
        if (scale < 0.2f) scale = 0.2f;
        var drawScale = new Vector2(scale, scale);
        if (info.Scale.DistanceSquaredTo(drawScale) > 0.000001f) info.Scale = drawScale;

        Rect2 plateRect = plate.GetRect();
        var place = new Vector2(
            safe.GetCenter().X - plateRect.GetCenter().X * scale,
            Math.Min(safe.Position.Y + safe.Size.Y * InfoTopFraction, ascensionTop - plateRect.End.Y * scale - InfoAscensionGap));
        bool first = !state.InfoPlaced;
        if (first && TweenRunning(screen, "_infoPanelTween"))
        {
            // The screen's opening slide was aimed at the desktop place; stop it and stand the panel at its own.
            (GetField(screen, "_infoPanelTween") as Tween)?.Kill();
        }
        if ((first || !TweenRunning(screen, "_infoPanelTween")) && info.Position.DistanceSquaredTo(place) > 0.5f)
            info.Position = place;
        if (GetField(screen, "_infoPanelPosFinalVal") is not Vector2 final || final.DistanceSquaredTo(place) > 0.5f)
            SetField(screen, "_infoPanelPosFinalVal", place);
        state.InfoPlaced = true;
    }

    /// <summary>
    /// Enlarges the confirm button once per portrait session (its picture, outline and label together, pivoting like the
    /// native button), gives it the safe band's lower right corner as its shown place and keeps it there when the game's
    /// show/hide slide is not running.
    /// </summary>
    private void PrepareConfirm(SelectState state, Control screen, Rect2 safe)
    {
        if (Node(screen, state, "ConfirmButton") is not { } confirm) return;
        if (!state.ConfirmScaled)
        {
            // The screen can be laid out before the game has given the button its size; scaling a 0x0 box would lock it in.
            if (confirm.Size.X < 1f || confirm.Size.Y < 1f) return;
            state.ConfirmScaled = true;
            var rectangles = new List<(Control Node, Rect2 Rect, Vector2 Pivot)>();
            Collect(confirm, rectangles);
            Capture(confirm);
            confirm.Size *= ConfirmScale;
            confirm.PivotOffset *= ConfirmScale;
            foreach ((Control node, Rect2 rect, Vector2 pivot) in rectangles)
            {
                Capture(node);
                node.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft, Control.LayoutPresetMode.KeepSize);
                node.Position = rect.Position * ConfirmScale;
                node.Size = rect.Size * ConfirmScale;
                node.PivotOffset = pivot * ConfirmScale;
            }
        }
        // KeepSize: the default preset mode shrinks the box to its minimum size, which is 0x0 for this button.
        confirm.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft, Control.LayoutPresetMode.KeepSize);
        Vector2 show = safe.End - confirm.Size - new Vector2(ConfirmRight, ConfirmBottom);
        var hide = new Vector2(screen.Size.X + 80f, show.Y);
        if (GetField(confirm, "_showPos") is not Vector2 shown || shown.DistanceSquaredTo(show) > 0.5f)
            SetField(confirm, "_showPos", show);
        if (GetField(confirm, "_hidePos") is not Vector2 hidden || hidden.DistanceSquaredTo(hide) > 0.5f)
            SetField(confirm, "_hidePos", hide);
        bool first = !state.ConfirmPlaced;
        if (TweenRunning(confirm, "_moveTween"))
        {
            if (!first) return;
            (GetField(confirm, "_moveTween") as Tween)?.Kill();
        }
        Vector2 target = GetField(confirm, "_isEnabled") is true ? show : hide;
        if (confirm.Position.DistanceSquaredTo(target) > 1f) confirm.Position = target;
        state.ConfirmPlaced = true;
    }

    private static void Collect(Control node, List<(Control Node, Rect2 Rect, Vector2 Pivot)> into)
    {
        foreach (Control child in node.GetChildren().OfType<Control>())
        {
            into.Add((child, child.GetRect(), child.PivotOffset));
            Collect(child, into);
        }
    }

    private static float DreadHeight(Control screen, SelectState state, string name, float scale)
    {
        Control? control = Node(screen, state, name) ?? Node(screen, state, "%" + name);
        return control == null ? 0f : Math.Max(0f, control.Size.Y * scale);
    }

    /// <summary>
    /// Places one of Dread's own blocks centred in the width at a height of the screen's units, drawn at the measured scale
    /// (smaller when its picture would not fit the width). Its own size is kept, so the native clock and chain animations
    /// keep their local layout.
    /// </summary>
    private void PlaceDreadControl(Control screen, SelectState state, string name, float y, float scale, Rect2 safe)
    {
        Control? control = Node(screen, state, name) ?? Node(screen, state, "%" + name);
        if (control == null) return;
        Capture(control);
        Vector2 size = control.Size;
        if (size.X < 1f || size.Y < 1f) return;
        scale = Math.Min(scale, (safe.Size.X - 80f) / size.X);
        if (scale < 0.2f) scale = 0.2f;
        control.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        if (control.Size.DistanceSquaredTo(size) > 0.25f) control.Size = size;
        SetScale(control, new Vector2(scale, scale), Vector2.Zero);
        var place = new Vector2(safe.GetCenter().X - size.X * scale * 0.5f, y);
        if (control.Position.DistanceSquaredTo(place) > 0.25f) control.Position = place;
    }

    /// <summary>
    /// Scales one of the top-edge header controls (icon and label together) until its smallest label reads the given points
    /// (the gold badge), or by a fixed visual bump for an icon-only button; then places it inside the safe band's top edge.
    /// The title is left alone and the control's right/left edge is kept clear of it.
    /// </summary>
    private void ScaleHeaderLabel(Control screen, SelectState state, string name, Rect2 safe, float minPoints, bool right)
    {
        if (Node(screen, state, name) is not { } control || control.Size.X < 1f || control.Size.Y < 1f) return;
        ulong id = control.GetInstanceId();
        bool scaled = state.ScaledHeaders.Contains(id);
        float factor = scaled ? 1f : LabelFactor(control, minPoints, 1.6f);
        if (factor > 1.001f)
        {
            Capture(control);
            control.PivotOffset = Vector2.Zero;
            control.Scale *= factor;
            state.ScaledHeaders.Add(id);
        }
        Transform2D xf = screen.GetGlobalTransformWithCanvas();
        Vector2 safeTop = xf * new Vector2(0f, safe.Position.Y);
        float safeRight = (xf * new Vector2(safe.Size.X, 0f)).X;
        Rect2 rect = control.GetGlobalRect();
        // A right-aligned icon keeps a wide inset: its touch pad has to grow towards the edge, and at 8 units the edge
        // capped the pad before the target reached 127. The centred title cannot be reached from here.
        Vector2 target = right
            ? new Vector2(safeRight - rect.Size.X - HeaderRightMargin, safeTop.Y + 8f)
            : new Vector2(8f, safeTop.Y + 8f);
        Vector2 delta = target - rect.Position;
        if (delta.LengthSquared() > 0.5f) control.GlobalPosition += delta;
    }

    /// <summary>The factor that takes the control's smallest label to the wanted points (capped), or the fixed factor.</summary>
    private static float LabelFactor(Control control, float minPoints, float fixedFactor)
    {
        if (minPoints <= 0f) return fixedFactor;
        float smallest = float.MaxValue;
        void Walk(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is not Control control2) continue;
                int font = control2 is RichTextLabel rich
                    ? rich.GetThemeFontSize("normal_font_size")
                    : control2 is Label label ? label.GetThemeFontSize("font_size") : 0;
                if (font > 0)
                {
                    float points = font * Math.Abs(control2.GetGlobalTransformWithCanvas().Scale.Y) /
                                   PortraitBands.UnitsPerPoint;
                    if (points > 0.01f) smallest = Math.Min(smallest, points);
                }
                Walk(control2);
            }
        }
        Walk(control);
        // With no label (or one already large enough) keep the control as it is; the icon-only fixed factor is applied once
        // by the caller and it no longer asks for a label factor.
        return smallest == float.MaxValue || smallest >= minPoints
            ? 1f
            : Math.Min(3f, minPoints / smallest);
    }

    /// <summary>Moves one of the screen's top-edge controls down with the safe band, keeping its own size and anchors.</summary>
    private void ShiftTopControl(Control screen, SelectState state, string name, float safeTop)
    {
        if (Node(screen, state, name) is not { } control) return;
        PortraitControlSnapshot snap = Capture(control);
        float top = snap.InitialOffsetTop + safeTop;
        if (MathF.Abs(control.OffsetTop - top) > 0.5f) control.OffsetTop = top;
        float bottom = snap.InitialOffsetBottom + safeTop;
        if (MathF.Abs(control.OffsetBottom - bottom) > 0.5f) control.OffsetBottom = bottom;
    }

    /// <summary>
    /// Keeps the back button's input on top: it becomes the last child of its own parent, which the GUI hit test asks first.
    /// Moving a node within its own parent does not exit the tree, so its connections and hotkeys stay. The position belongs
    /// to the navigation pad pass (the single writer for back buttons).
    /// </summary>
    private static void EnsureBackOnTop(Control screen, SelectState state)
    {
        Control? back = Node(screen, state, "BackButton") ?? Node(screen, state, "%BackButton");
        if (back?.GetParent() is Control parent && !PhoneHitArea.IsContainer(parent) &&
            back.GetIndex() != parent.GetChildCount() - 1)
            parent.MoveChild(back, parent.GetChildCount() - 1);
    }

    /// <summary>A scene node under this screen, resolved once (a null is final for these scenes; ResetPortrait clears it).</summary>
    private static Control? Node(Control screen, SelectState state, string path)
    {
        if (state.Named.TryGetValue(path, out Control? cached))
        {
            if (cached == null || GodotObject.IsInstanceValid(cached)) return cached;
            state.Named.Remove(path);
        }
        Control? found = screen.GetNodeOrNull<Control>(path);
        state.Named[path] = found;
        return found;
    }

    /// <summary>A node below a cached parent, resolved once and kept in the state's name cache.</summary>
    private static Control? Node(Control parent, SelectState state, string path, string cacheKey)
    {
        if (state.Named.TryGetValue(cacheKey, out Control? cached))
        {
            if (cached == null || GodotObject.IsInstanceValid(cached)) return cached;
            state.Named.Remove(cacheKey);
        }
        Control? found = parent.GetNodeOrNull<Control>(path);
        state.Named[cacheKey] = found;
        return found;
    }

    /// <summary>Sets a control's scale and pivot, changing only what differs (a native hover tween may own the scale).</summary>
    private static void SetScale(Control control, Vector2 scale, Vector2 pivot)
    {
        if (control.PivotOffset.DistanceSquaredTo(pivot) > 0.25f) control.PivotOffset = pivot;
        if (control.Scale.DistanceSquaredTo(scale) > 0.000001f) control.Scale = scale;
    }

    // ---- focus neighbours -------------------------------------------------------------------------------------------------

    private void SaveFocus(Control button)
    {
        ulong id = button.GetInstanceId();
        for (int i = 0; i < _focus.Count; i++)
            if (_focus[i].Id == id) return;
        _focus.Add((id, button.FocusNeighborLeft, button.FocusNeighborRight, button.FocusNeighborTop, button.FocusNeighborBottom));
    }

    public void RestoreFocus()
    {
        foreach ((ulong id, NodePath? left, NodePath? right, NodePath? top, NodePath? bottom) in _focus)
        {
            if (GodotObject.InstanceFromId(id) is not Control button || !GodotObject.IsInstanceValid(button)) continue;
            if (left is { } l) button.FocusNeighborLeft = l;
            if (right is { } r) button.FocusNeighborRight = r;
            if (top is { } t) button.FocusNeighborTop = t;
            if (bottom is { } b) button.FocusNeighborBottom = b;
        }
        _focus.Clear();
    }

    // ---- shared reflection ------------------------------------------------------------------------------------------------

    private static readonly Dictionary<(Type, string), FieldInfo?> Fields = new();

    private static FieldInfo? Field(object target, string name)
    {
        Type type = target.GetType();
        if (!Fields.TryGetValue((type, name), out FieldInfo? field))
            Fields[(type, name)] = field = FindField(type, name);
        return field;
    }

    private static object? GetField(object target, string name) => Field(target, name)?.GetValue(target);

    private static void SetField(object target, string name, object? value) => Field(target, name)?.SetValue(target, value);

    private static FieldInfo? FindField(Type? type, string name)
    {
        for (; type != null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } field)
                return field;
        return null;
    }

    private static bool TweenRunning(object owner, string field) =>
        GetField(owner, field) is Tween tween && tween.IsValid() && tween.IsRunning();

    /// <summary>Forgets the portrait-only plans and disconnects the roster's sort callback (the snapshots go back later).</summary>
    public void ResetPortrait()
    {
        foreach (SelectState state in _screens.Values) state.ResetPortrait();
    }

    public void RestoreControls()
    {
        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
        _snapshots.Clear();
    }

    /// <summary>
    /// The info panel may slide again from wherever it is: give the game's own resting place back. Runs after the snapshots
    /// have put the panel where the desktop had it.
    /// </summary>
    public void RestoreRestingPlaces()
    {
        foreach (ulong id in _screens.Keys)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen) &&
                screen.GetNodeOrNull<Control>("InfoPanel") is { } info)
                SetField(screen, "_infoPanelPosFinalVal", info.Position);
    }

    public void PruneDead()
    {
        foreach (ulong id in _snapshots.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _snapshots.Remove(id);
        foreach (ulong id in _screens.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _screens.Remove(id);
        _focus.RemoveAll(entry => !GodotObject.IsInstanceIdValid(entry.Id));
    }
}
