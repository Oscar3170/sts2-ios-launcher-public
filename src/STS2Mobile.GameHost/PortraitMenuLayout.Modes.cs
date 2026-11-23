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

    /// <summary>
    /// The singleplayer and multiplayer-host mode cards: the native buttons stay in their own row (their input connections,
    /// hotkeys and hover tweens stay intact and nothing leaves the tree); the row is drawn larger and its children are put two
    /// to a line, an odd last card centred, re-applied right after any native sort. Their interior is the measured card
    /// layout: a 340 wide plate, the title, the mode's portrait and the description under it, in a card 545 units tall
    /// instead of the desktop's 705.
    /// </summary>
    private void LayoutModeCards(Control screen, ScreenState state)
    {
        if (screen.GetNodeOrNull<BoxContainer>("ButtonContainer") is not { } row) return;
        List<Control> cards = row.GetChildren().OfType<Control>().Where(card => card.Visible).ToList();
        PlaceBackButton(screen, state);
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
        PortraitMenuText.Apply(screen);
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
            Place(card.GetNodeOrNull<Control>("SubmenuPortrait"), 42f, 108f, 252f, 192f);
            if (card.GetNodeOrNull<RichTextLabel>("Description") is { } description)
            {
                if (description.AutowrapMode != TextServer.AutowrapMode.WordSmart)
                    description.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                Place(description, 43f, 331f, 276f, ModeCardHeight - 381f);
            }
        }
    }

    /// <summary>Moves one of the screen's top-edge controls down with the safe band, keeping its own size and anchors.</summary>
    private void ShiftTopControl(Control screen, string name, float safeTop)
    {
        if (screen.GetNodeOrNull<Control>(name) is not { } control) return;
        PortraitControlSnapshot snap = Capture(control);
        float top = snap.InitialOffsetTop + safeTop;
        if (MathF.Abs(control.OffsetTop - top) > 0.5f) control.OffsetTop = top;
        float bottom = snap.InitialOffsetBottom + safeTop;
        if (MathF.Abs(control.OffsetBottom - bottom) > 0.5f) control.OffsetBottom = bottom;
    }
}
