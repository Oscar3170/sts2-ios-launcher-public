using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace STS2Mobile.GameHost;

/// <summary>
/// The singleplayer and multiplayer-host mode cards in portrait: the native buttons stay in their own row (their input
/// connections, hotkeys and hover tweens stay intact), the row is scaled, and the cards are placed up to three to a line,
/// the lines balanced (5 = 3 + 2, 4 = 2 + 2) and centred, with their interiors at the reference card's measured places. The
/// cards are sized for a full line of three, so hiding a mode does not resize the rest. The row is laid out from each card's
/// DRAWN bounds, not its control rectangle: the native background art is taller than the control, and spacing the controls
/// made the art of one row cover the next. The row's sort callback, the cards' cross-row focus plan and the desktop values
/// are per submenu and live here; the menu coordinator fits the screen and passes the safe band.
/// </summary>
internal sealed class PortraitModeCardsLayout
{
    private const string Ns = "MegaCrit.Sts2.Core.Nodes.Screens.";
    public const string SingleplayerTypeName = Ns + "MainMenu.NSingleplayerSubmenu";
    public const string MultiplayerHostTypeName = Ns + "MainMenu.NMultiplayerHostSubmenu";

    private const float ModeCardWidth = 362f;
    private const float ModeCardHeight = 545f;
    private const float ModeGap = 18f;
    private const int ModeLineMax = 3;
    private const float ModeTop = 100f;
    // Space kept under the cards for the mode screen's own title and the back button.
    private const float ModeReserved = 430f;

    /// <summary>One mode submenu's resolved row, sort callback, focus plan and the safe band last passed in.</summary>
    private sealed class RowState
    {
        public float Scale = 1f;
        public Rect2 Safe;
        public readonly Dictionary<string, Control?> Named = new(StringComparer.Ordinal);
        public BoxContainer? Row;
        public Action? RowSort;
        public int FocusCount = -1;

        /// <summary>Forgets what only holds while portrait is on; the snapshots put the desktop layout back.</summary>
        public void ResetPortrait()
        {
            if (RowSort != null && GodotObject.IsInstanceValid(Row)) Row!.SortChildren -= RowSort;
            RowSort = null;
            Row = null;
            FocusCount = -1;
            Named.Clear();
        }
    }

    private readonly Dictionary<ulong, RowState> _screens = new();
    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private readonly List<(ulong Id, NodePath? Left, NodePath? Right, NodePath? Top, NodePath? Bottom)> _focus = new();
    private readonly Dictionary<ulong, Rect2> _modeDrawnLogged = new();

    private RowState GetState(Control screen)
    {
        ulong id = screen.GetInstanceId();
        if (!_screens.TryGetValue(id, out RowState? state)) _screens[id] = state = new RowState();
        return state;
    }

    private PortraitControlSnapshot Capture(Control control)
    {
        ulong id = control.GetInstanceId();
        if (!_snapshots.TryGetValue(id, out PortraitControlSnapshot? snap))
            _snapshots[id] = snap = new PortraitControlSnapshot(control, captureVisible: false);
        return snap;
    }

    /// <summary>
    /// Lays out the screen's mode cards on the safe band the menu coordinator computed. The screen is already fitted to the
    /// 1680 unit menu canvas; the row keeps its own children and native input.
    /// </summary>
    public void Apply(Control screen, Rect2 safe, float scale)
    {
        RowState state = GetState(screen);
        state.Safe = safe;
        state.Scale = scale;
        if (Node(screen, state, "ButtonContainer") is not BoxContainer row) return;
        EnsureBackOnTop(screen, state);
        Capture(row);
        row.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        if (state.Row != row)
        {
            if (state.RowSort != null && GodotObject.IsInstanceValid(state.Row)) state.Row!.SortChildren -= state.RowSort;
            state.Row = row;
            state.RowSort = () =>
            {
                if (GodotObject.IsInstanceValid(row) && state.Scale > 0f) Arrange(state, screen, row);
            };
            row.SortChildren += state.RowSort;
        }
        Arrange(state, screen, row);

        List<Control> cards = row.GetChildren().OfType<Control>().Where(card => card.Visible).ToList();
        if (state.FocusCount != cards.Count)
        {
            int[] starts = LineStarts(cards.Count);
            for (int i = 0; i < cards.Count; i++)
            {
                (int line, int col) = Cell(starts, i);
                SaveFocus(cards[i]);
                cards[i].FocusNeighborLeft = cards[starts[line]].GetPath();
                cards[i].FocusNeighborRight = cards[starts[line + 1] - 1].GetPath();
                cards[i].FocusNeighborTop = cards[line == 0 ? i : NearestInLine(starts, line - 1, col)].GetPath();
                cards[i].FocusNeighborBottom =
                    cards[line == starts.Length - 2 ? i : NearestInLine(starts, line + 1, col)].GetPath();
            }
            state.FocusCount = cards.Count;
        }
    }

    /// <summary>
    /// Where each line starts, plus the count as the end: ceil(count / 3) lines, the earlier lines taking the extra card.
    /// </summary>
    private static int[] LineStarts(int count)
    {
        int lines = Math.Max(1, (count + ModeLineMax - 1) / ModeLineMax);
        var starts = new int[lines + 1];
        for (int line = 0; line < lines; line++)
            starts[line + 1] = starts[line] + count / lines + (line < count % lines ? 1 : 0);
        return starts;
    }

    private static (int Line, int Col) Cell(int[] starts, int index)
    {
        int line = 0;
        while (index >= starts[line + 1]) line++;
        return (line, index - starts[line]);
    }

    private static int NearestInLine(int[] starts, int line, int col) =>
        Math.Min(starts[line] + col, starts[line + 1] - 1);

    /// <summary>
    /// Lays out the visible cards from their drawn bounds: interiors first, then the union of their texture/plate children,
    /// then the row scale and each card's position so one card's drawn art never reaches the next.
    /// </summary>
    private void Arrange(RowState state, Control screen, BoxContainer row)
    {
        List<Control> cards = row.GetChildren().OfType<Control>().Where(card => card.Visible).ToList();
        if (cards.Count == 0) return;
        for (int i = 0; i < cards.Count; i++)
        {
            Control card = cards[i];
            Capture(card);
            SetMinSize(card, new Vector2(ModeCardWidth, ModeCardHeight));
            if (card.Size.DistanceSquaredTo(new Vector2(ModeCardWidth, ModeCardHeight)) > 0.25f)
                card.Size = new Vector2(ModeCardWidth, ModeCardHeight);
            var pivot = new Vector2(ModeCardWidth / 2f, ModeCardHeight / 2f);
            if (card.PivotOffset.DistanceSquaredTo(pivot) > 0.25f) card.PivotOffset = pivot;

            Place(card.GetNodeOrNull<Control>("BgPanel"), 11f, 0f, 340f, ModeCardHeight);
            Place(card.GetNodeOrNull<Control>("Title"), 28f, 30f, 306f, 61f);
            // The reference card's own interior, scaled with the card: art, frame, title and description keep their native
            // proportions, so nothing spills out of the card.
            Place(card.GetNodeOrNull<Control>("SubmenuPortrait"), 42f, 108f, 252f, 192f);
            if (card.GetNodeOrNull<RichTextLabel>("Description") is { } description)
            {
                if (description.AutowrapMode != TextServer.AutowrapMode.WordSmart)
                    description.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                Place(description, 43f, 331f, 276f, ModeCardHeight - 381f);
            }
        }

        var drawn = new Rect2[cards.Count];
        float cellWidth = 1f, cellHeight = 1f;
        for (int i = 0; i < cards.Count; i++)
        {
            drawn[i] = CardDrawnBounds(cards[i]);
            cellWidth = Math.Max(cellWidth, drawn[i].Size.X);
            cellHeight = Math.Max(cellHeight, drawn[i].Size.Y);
            ulong id = cards[i].GetInstanceId();
            if (!_modeDrawnLogged.TryGetValue(id, out Rect2 seen) || !seen.IsEqualApprox(drawn[i]))
            {
                _modeDrawnLogged[id] = drawn[i];
                GD.Print($"Portrait mode card: {cards[i].Name} control={cards[i].Size.X:F0}x{cards[i].Size.Y:F0} " +
                         $"drawn={drawn[i].Position.X:F0},{drawn[i].Position.Y:F0} {drawn[i].Size.X:F0}x{drawn[i].Size.Y:F0}");
            }
        }

        int[] starts = LineStarts(cards.Count);
        int rows = starts.Length - 1;
        float rowWidth = ModeLineMax * cellWidth + (ModeLineMax - 1) * ModeGap;
        float height = rows * cellHeight + (rows - 1) * ModeGap;
        Rect2 safe = state.Safe;
        float available = Math.Max(200f, safe.Size.Y - ModeReserved);
        float scale = Math.Min((safe.Size.X - 80f) / rowWidth, available / height);
        scale = Math.Max(0.3f, scale);
        float top = safe.Position.Y + ModeTop + Math.Max(0f, available - height * scale) / 2f;

        SetScale(row, new Vector2(scale, scale), Vector2.Zero);
        Vector2 target = screen.GetGlobalTransformWithCanvas() * new Vector2(safe.GetCenter().X, top);
        if (row.GlobalPosition.DistanceSquaredTo(target) > 0.25f) row.GlobalPosition = target;

        for (int i = 0; i < cards.Count; i++)
        {
            (int line, int col) = Cell(starts, i);
            int inLine = starts[line + 1] - starts[line];
            float cellLeft = col * (cellWidth + ModeGap) - (inLine * (cellWidth + ModeGap) - ModeGap) * 0.5f;
            var place = new Vector2(
                cellLeft + (cellWidth - drawn[i].Size.X) * 0.5f - drawn[i].Position.X,
                line * (cellHeight + ModeGap) + (cellHeight - drawn[i].Size.Y) * 0.5f - drawn[i].Position.Y);
            if (cards[i].Position.DistanceSquaredTo(place) > 0.25f) cards[i].Position = place;
        }
    }

    /// <summary>The union of a card's visible texture and nine-patch children, in the card's own units (its drawn box).</summary>
    private static Rect2 CardDrawnBounds(Control card)
    {
        Transform2D inverse = card.GetGlobalTransformWithCanvas().AffineInverse();
        Rect2? union = null;
        void Walk(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is not Control control || !GodotObject.IsInstanceValid(control) || !control.IsVisibleInTree()) continue;
                if (control is TextureRect or NinePatchRect)
                {
                    Rect2 rect = inverse * control.GetGlobalRect();
                    union = union is { } current ? current.Merge(rect) : rect;
                }
                Walk(control);
            }
        }
        Walk(card);
        return union is { } bounds && bounds.Size.X >= 1f && bounds.Size.Y >= 1f
            ? bounds
            : new Rect2(Vector2.Zero, card.Size);
    }

    /// <summary>Puts a control at a rectangle of its parent with the top left anchor, changing only what differs.</summary>
    private void Place(Control? control, float x, float y, float w, float h)
    {
        if (control == null) return;
        Capture(control);
        if (control.AnchorLeft != 0f || control.AnchorTop != 0f || control.AnchorRight != 0f || control.AnchorBottom != 0f)
        {
            control.AnchorLeft = 0f;
            control.AnchorTop = 0f;
            control.AnchorRight = 0f;
            control.AnchorBottom = 0f;
        }
        if (MathF.Abs(control.OffsetLeft - x) > 0.5f) control.OffsetLeft = x;
        if (MathF.Abs(control.OffsetTop - y) > 0.5f) control.OffsetTop = y;
        if (MathF.Abs(control.OffsetRight - (x + w)) > 0.5f) control.OffsetRight = x + w;
        if (MathF.Abs(control.OffsetBottom - (y + h)) > 0.5f) control.OffsetBottom = y + h;
    }

    /// <summary>Sets a control's scale and pivot, changing only what differs (a native hover tween may own the scale).</summary>
    private static void SetScale(Control control, Vector2 scale, Vector2 pivot)
    {
        if (control.PivotOffset.DistanceSquaredTo(pivot) > 0.25f) control.PivotOffset = pivot;
        if (control.Scale.DistanceSquaredTo(scale) > 0.000001f) control.Scale = scale;
    }

    private static void SetMinSize(Control control, Vector2 size)
    {
        if (control.CustomMinimumSize.DistanceSquaredTo(size) > 0.25f) control.CustomMinimumSize = size;
    }

    /// <summary>
    /// Keeps the back button's input on top: it becomes the last child of its own parent, which the GUI hit test asks first.
    /// Moving a node within its own parent does not exit the tree, so its connections and hotkeys stay. The position belongs
    /// to the navigation pad pass (the single writer for back buttons).
    /// </summary>
    private static void EnsureBackOnTop(Control screen, RowState state)
    {
        Control? back = Node(screen, state, "BackButton") ?? Node(screen, state, "%BackButton");
        if (back?.GetParent() is Control parent && !PhoneHitArea.IsContainer(parent) &&
            back.GetIndex() != parent.GetChildCount() - 1)
            parent.MoveChild(back, parent.GetChildCount() - 1);
    }

    /// <summary>A scene node under this screen, resolved once (a null is final for these scenes; ResetPortrait clears it).</summary>
    private static Control? Node(Control screen, RowState state, string path)
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

    /// <summary>Forgets the row plan and disconnects its sort callback (the snapshots go back later).</summary>
    public void ResetPortrait()
    {
        foreach (RowState state in _screens.Values) state.ResetPortrait();
    }

    public void RestoreControls()
    {
        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
        _snapshots.Clear();
    }

    public void PruneDead()
    {
        foreach (ulong id in _snapshots.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _snapshots.Remove(id);
        foreach (ulong id in _screens.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _screens.Remove(id);
        foreach (ulong id in _modeDrawnLogged.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _modeDrawnLogged.Remove(id);
        _focus.RemoveAll(entry => !GodotObject.IsInstanceIdValid(entry.Id));
    }
}
