using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace STS2Mobile.GameHost;

internal sealed partial class PortraitMenuLayout
{
    private const float ModeCardWidth = 362f;
    private const float ModeCardHeight = 545f;
    private const float ModeGap = 18f;
    private const float ModeTop = 100f;
    // Space kept under the cards for the mode screen's own title and the back button.
    private const float ModeReserved = 430f;
    // Room between a right-aligned header icon and the safe edge, so its pad can reach 127 units on both sides.
    private const float HeaderRightMargin = 32f;

    /// <summary>
    /// The singleplayer and multiplayer-host mode cards: the native buttons stay in their own row (their input connections,
    /// hotkeys and hover tweens stay intact and nothing leaves the tree); the row is drawn larger and its children are put two
    /// to a line, an odd last card centred, re-applied right after any native sort. Their interior is the measured card
    /// layout: a 340 wide plate, the title, the mode's portrait and the description under it, in a card 545 units tall
    /// instead of the desktop's 705.
    /// </summary>
    private void LayoutModeCards(Control screen, ScreenState state)
    {
        if (Node(screen, state, "ButtonContainer") is not BoxContainer row) return;
        List<Control> cards = row.GetChildren().OfType<Control>().Where(card => card.Visible).ToList();
        EnsureBackOnTop(screen, state);
        if (cards.Count == 0) return;

        int rows = Math.Max(1, (cards.Count + 1) / 2);
        float rowWidth = 2f * ModeCardWidth + ModeGap;
        float height = rows * (ModeCardHeight + ModeGap) - ModeGap;
        Rect2 safe = state.Safe;
        float available = Math.Max(200f, safe.Size.Y - ModeReserved);
        float scale = Math.Min((safe.Size.X - 80f) / rowWidth, available / height);
        scale = Math.Max(0.3f, scale);
        float top = safe.Position.Y + ModeTop + Math.Max(0f, available - height * scale) / 2f;

        Capture(row);
        row.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        SetScale(row, new Vector2(scale, scale), Vector2.Zero);
        Vector2 target = screen.GetGlobalTransformWithCanvas() * new Vector2(safe.GetCenter().X, top);
        if (row.GlobalPosition.DistanceSquaredTo(target) > 0.25f) row.GlobalPosition = target;

        if (state.ModeRow != row)
        {
            if (state.ModeSort != null && GodotObject.IsInstanceValid(state.ModeRow)) state.ModeRow!.SortChildren -= state.ModeSort;
            state.ModeRow = row;
            state.ModeSort = () =>
            {
                if (GodotObject.IsInstanceValid(row) && state.Scale > 0f) ArrangeModeCards(state, row);
            };
            row.SortChildren += state.ModeSort;
        }
        ArrangeModeCards(state, row);

        if (state.FocusCount != cards.Count)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                int line = i / 2, inLine = Math.Min(2, cards.Count - line * 2);
                SaveFocus(cards[i]);
                cards[i].FocusNeighborLeft = cards[line * 2].GetPath();
                cards[i].FocusNeighborRight = cards[line * 2 + inLine - 1].GetPath();
                cards[i].FocusNeighborTop = cards[Math.Max(0, i - 2)].GetPath();
                cards[i].FocusNeighborBottom = cards[Math.Min(cards.Count - 1, i + 2)].GetPath();
            }
            state.FocusCount = cards.Count;
        }
    }

    /// <summary>
    /// Positions the visible cards in the row's own units: two per line around the row's origin (the middle of the screen
    /// width), each 362 by 545 with its measured interior.
    /// </summary>
    private void ArrangeModeCards(ScreenState state, BoxContainer row)
    {
        List<Control> cards = row.GetChildren().OfType<Control>().Where(card => card.Visible).ToList();
        if (cards.Count == 0) return;
        for (int i = 0; i < cards.Count; i++)
        {
            Control card = cards[i];
            Capture(card);
            int line = i / 2, col = i % 2, inLine = Math.Min(2, cards.Count - line * 2);
            SetMinSize(card, new Vector2(ModeCardWidth, ModeCardHeight));
            if (card.Size.DistanceSquaredTo(new Vector2(ModeCardWidth, ModeCardHeight)) > 0.25f)
                card.Size = new Vector2(ModeCardWidth, ModeCardHeight);
            var pivot = new Vector2(ModeCardWidth / 2f, ModeCardHeight / 2f);
            if (card.PivotOffset.DistanceSquaredTo(pivot) > 0.25f) card.PivotOffset = pivot;

            var place = new Vector2(
                col * (ModeCardWidth + ModeGap) - (inLine * (ModeCardWidth + ModeGap) - ModeGap) * 0.5f,
                line * (ModeCardHeight + ModeGap));
            if (card.Position.DistanceSquaredTo(place) > 0.25f) card.Position = place;

            Place(card.GetNodeOrNull<Control>("BgPanel"), 11f, 0f, 340f, ModeCardHeight);
            Place(card.GetNodeOrNull<Control>("Title"), 28f, 30f, 306f, 61f);
            // The reference card's own interior, scaled with the card: the art, its frame, the title and the description
            // keep their native proportions, so nothing spills out of the card and the rows need no extra gap.
            Place(card.GetNodeOrNull<Control>("SubmenuPortrait"), 42f, 108f, 252f, 192f);
            if (card.GetNodeOrNull<RichTextLabel>("Description") is { } description)
            {
                if (description.AutowrapMode != TextServer.AutowrapMode.WordSmart)
                    description.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                Place(description, 43f, 331f, 276f, ModeCardHeight - 381f);
            }
        }
    }

    /// <summary>
    /// Scales one of the top-edge header controls (icon and label together) until its smallest label reads the given points
    /// (the gold badge), or by a fixed visual bump for an icon-only button; then places it inside the safe band's top edge.
    /// The title is left alone and the control's right/left edge is kept clear of it.
    /// </summary>
    private void ScaleHeaderLabel(Control screen, ScreenState state, string name, Rect2 safe, float minPoints, bool right)
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

    /// <summary>
    /// The credits modal's plain engine buttons at a finger's height. The screen keeps its own layout; only the button boxes
    /// grow (their art and text follow the box).
    /// </summary>
    private void GrowCreditsTargets(Control screen)
    {
        void Walk(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is not Control control || !GodotObject.IsInstanceValid(control)) continue;
                if (control is BaseButton && control.IsVisibleInTree() &&
                    control.CustomMinimumSize.Y < PortraitBands.MinTarget)
                {
                    Capture(control);
                    control.CustomMinimumSize = new Vector2(control.CustomMinimumSize.X, PortraitBands.MinTarget);
                }
                Walk(control);
            }
        }
        Walk(screen);
    }

    /// <summary>Moves one of the screen's top-edge controls down with the safe band, keeping its own size and anchors.</summary>
    private void ShiftTopControl(Control screen, ScreenState state, string name, float safeTop)
    {
        if (Node(screen, state, name) is not { } control) return;
        PortraitControlSnapshot snap = Capture(control);
        float top = snap.InitialOffsetTop + safeTop;
        if (MathF.Abs(control.OffsetTop - top) > 0.5f) control.OffsetTop = top;
        float bottom = snap.InitialOffsetBottom + safeTop;
        if (MathF.Abs(control.OffsetBottom - bottom) > 0.5f) control.OffsetBottom = bottom;
    }
}
