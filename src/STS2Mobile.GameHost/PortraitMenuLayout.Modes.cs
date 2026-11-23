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
    private readonly Dictionary<ulong, Rect2> _modeDrawnLogged = new();

    /// <summary>
    /// The singleplayer and multiplayer-host mode cards: the native buttons stay in their own row (their input connections,
    /// hotkeys and hover tweens stay intact), the row is scaled, and the cards are placed two to a line with the last card
    /// centred. Their interior is the reference card (545 units tall). The rows are laid out from each card's DRAWN bounds,
    /// not its control rectangle: the native background art is taller than the control, and spacing the controls made the
    /// art of one row cover the next. The row scale is whatever fits 2 columns plus the centred Custom card in the band.
    /// </summary>
    private void LayoutModeCards(Control screen, ScreenState state)
    {
        if (Node(screen, state, "ButtonContainer") is not BoxContainer row) return;
        EnsureBackOnTop(screen, state);
        Capture(row);
        row.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        if (state.ModeRow != row)
        {
            if (state.ModeSort != null && GodotObject.IsInstanceValid(state.ModeRow)) state.ModeRow!.SortChildren -= state.ModeSort;
            state.ModeRow = row;
            state.ModeSort = () =>
            {
                if (GodotObject.IsInstanceValid(row) && state.Scale > 0f) ArrangeModeCards(state, screen, row);
            };
            row.SortChildren += state.ModeSort;
        }
        ArrangeModeCards(state, screen, row);

        List<Control> cards = row.GetChildren().OfType<Control>().Where(card => card.Visible).ToList();
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
    /// Lays out the visible cards from their drawn bounds: interiors first, then the union of their texture/plate children,
    /// then the row scale and each card's position so one card's drawn art never reaches the next.
    /// </summary>
    private void ArrangeModeCards(ScreenState state, Control screen, BoxContainer row)
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

        int rows = Math.Max(1, (cards.Count + 1) / 2);
        float rowWidth = 2f * cellWidth + ModeGap;
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
            int line = i / 2, col = i % 2, inLine = Math.Min(2, cards.Count - line * 2);
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
