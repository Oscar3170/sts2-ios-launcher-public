using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

internal static partial class PortraitSubmenuLayout
{
    public const string CompendiumTypeName = Ns + "MainMenu.NCompendiumSubmenu";

    /// <summary>
    /// The compendium's card row is laid out on the menu's own 1680 unit canvas, so its cards take exactly the singleplayer
    /// mode cards' rule (two lines of two, sized for a full line of three) and the same scale and spacing.
    /// </summary>
    public const float CompendiumDesignWidth = 1680f;

    private const string CompendiumCardsPath = "MarginContainer/VBoxContainer/TopRow";
    private const string CompendiumButtonsPath = "MarginContainer/VBoxContainer/BottomRow";
    // The bottom buttons' native separation; read from the row when it still carries its scene value.
    private const float CompendiumButtonGap = 24f;

    /// <summary>
    /// One compendium screen's card row, its bottom-button row, the sort callbacks that keep our child positions after the
    /// game's own containers sort, and the focus plan's last visible set.
    /// </summary>
    private sealed class CompendiumState
    {
        public Rect2 Safe;
        public Action<Control>? Capture;
        public BoxContainer? Cards;
        public BoxContainer? Buttons;
        public Action? CardsSort;
        public Action? ButtonsSort;
        public int FocusCards = -1;
        public int FocusButtons = -1;
    }

    private static readonly Dictionary<ulong, CompendiumState> CompendiumStates = new();
    private static readonly Dictionary<ulong, (NodePath? Left, NodePath? Right, NodePath? Top, NodePath? Bottom)> CompendiumFocus = new();

    private static CompendiumState CompendiumStateFor(Control screen)
    {
        ulong id = screen.GetInstanceId();
        if (!CompendiumStates.TryGetValue(id, out CompendiumState? state)) CompendiumStates[id] = state = new CompendiumState();
        return state;
    }

    /// <summary>
    /// The compendium: Card Library, Relic Collection, Potion Lab and Bestiary in two lines of two, each card as wide as a
    /// singleplayer mode card and spaced from its drawn art, and the two small bottom buttons (Character Stats, Run History)
    /// centred under the last line at the cards' scale, far larger than a finger's 44 point target. The two rows leave the
    /// desktop's VBox (so its column cannot place or squeeze them) and keep their native buttons, clicks, hover tweens and
    /// sort callbacks; the focus plan follows the new geometry, including the cards' cross-row neighbours and the buttons'
    /// own row. <see cref="MoveKept"/> and the screen's snapshots give landscape the desktop layout back.
    /// </summary>
    private static void LayoutCompendium(Control screen, float w, float h, Action<Control> capture)
    {
        if (w < 100f || h < 100f) return;
        CompendiumState state = CompendiumStateFor(screen);
        state.Safe = new Rect2(0f, 0f, w, h);
        state.Capture = capture;
        BoxContainer? cards = CompendiumRow(screen, state, cards: true);
        BoxContainer? buttons = CompendiumRow(screen, state, cards: false);
        if (cards == null || buttons == null) return;
        capture(cards);
        capture(buttons);
        if (state.CardsSort == null || state.ButtonsSort == null || state.Cards != cards || state.Buttons != buttons)
            HookRows(screen, state, cards, buttons);
        MoveKept(cards, screen);
        MoveKept(buttons, screen);
        ArrangeCompendium(screen, state);
    }

    private static BoxContainer? CompendiumRow(Control screen, CompendiumState state, bool cards)
    {
        BoxContainer? cached = cards ? state.Cards : state.Buttons;
        if (cached != null && GodotObject.IsInstanceValid(cached)) return cached;
        BoxContainer? row = screen.GetNodeOrNull<BoxContainer>(cards ? CompendiumCardsPath : CompendiumButtonsPath);
        // After the first frame the row lives under the screen, not in the desktop column: find it by name then.
        return row ?? screen.FindChild(cards ? "TopRow" : "BottomRow", true, false) as BoxContainer;
    }

    private static void HookRows(Control screen, CompendiumState state, BoxContainer cards, BoxContainer buttons)
    {
        UnhookRows(state);
        state.Cards = cards;
        state.Buttons = buttons;
        Action cardsSort = () =>
        {
            if (GodotObject.IsInstanceValid(screen) && GodotObject.IsInstanceValid(cards)) ArrangeCompendium(screen, state);
        };
        Action buttonsSort = () =>
        {
            if (GodotObject.IsInstanceValid(screen) && GodotObject.IsInstanceValid(buttons)) ArrangeCompendium(screen, state);
        };
        state.CardsSort = cardsSort;
        state.ButtonsSort = buttonsSort;
        cards.SortChildren += cardsSort;
        buttons.SortChildren += buttonsSort;
    }

    private static void UnhookRows(CompendiumState state)
    {
        Action? cardsSort = state.CardsSort;
        BoxContainer? cards = state.Cards;
        if (cardsSort != null && cards != null && GodotObject.IsInstanceValid(cards))
            cards.SortChildren -= cardsSort;
        Action? buttonsSort = state.ButtonsSort;
        BoxContainer? buttons = state.Buttons;
        if (buttonsSort != null && buttons != null && GodotObject.IsInstanceValid(buttons))
            buttons.SortChildren -= buttonsSort;
        state.CardsSort = null;
        state.ButtonsSort = null;
        state.FocusCards = -1;
        state.FocusButtons = -1;
    }

    /// <summary>
    /// Places the compendium's cards and bottom buttons from their drawn bounds. The cards get the mode-card plan (four cards
    /// are two lines of two, sized for a full line of three); the buttons are a centred row under the last line, scaled by
    /// the same plan so they stay finger-sized.
    /// </summary>
    private static void ArrangeCompendium(Control screen, CompendiumState state)
    {
        Action<Control>? captureAction = state.Capture;
        BoxContainer? cachedCards = state.Cards;
        BoxContainer? cachedButtons = state.Buttons;
        if (captureAction == null || cachedCards == null || cachedButtons == null) return;
        if (!GodotObject.IsInstanceValid(cachedCards) || !GodotObject.IsInstanceValid(cachedButtons)) return;
        Action<Control> capture = captureAction;
        BoxContainer cardsRow = cachedCards;
        BoxContainer buttonsRow = cachedButtons;

        List<Control> cards = cardsRow.GetChildren().OfType<Control>()
            .Where(card => card is BaseButton && card.Visible && GodotObject.IsInstanceValid(card)).ToList();
        if (cards.Count == 0) return;
        var drawn = new Rect2[cards.Count];
        float cellWidth = 1f, cellHeight = 1f;
        for (int i = 0; i < cards.Count; i++)
        {
            capture(cards[i]);
            // The desktop row gives each short card its own 368 x 490 box; keep that box if the container has not sized the
            // card yet (the row is moved out before it can always have laid its children out).
            Vector2 minimum = cards[i].GetCombinedMinimumSize();
            if (minimum.X >= 1f && minimum.Y >= 1f && cards[i].Size.DistanceSquaredTo(minimum) > 0.25f)
                cards[i].Size = minimum;
            drawn[i] = PortraitDrawnBounds.Of(cards[i]);
            cellWidth = Math.Max(cellWidth, Math.Max(minimum.X, drawn[i].Size.X));
            cellHeight = Math.Max(cellHeight, Math.Max(minimum.Y, drawn[i].Size.Y));
        }

        List<Control> buttons = buttonsRow.GetChildren().OfType<Control>()
            .Where(button => button is BaseButton && button.Visible && GodotObject.IsInstanceValid(button)).ToList();
        float buttonHeight = 0f;
        foreach (Control button in buttons)
        {
            capture(button);
            Vector2 size = ButtonSize(button);
            if (size.X >= 1f && size.Y >= 1f && button.Size.DistanceSquaredTo(size) > 0.25f) button.Size = size;
            buttonHeight = Math.Max(buttonHeight, size.Y);
        }

        MenuCardLines plan = PortraitMenuCardsMath.Plan(
            cards.Count, state.Safe.Size.X, state.Safe.Position.Y, state.Safe.Size.Y, cellWidth, cellHeight,
            buttons.Count > 0 ? buttonHeight : 0f);
        float cardsHeight = plan.Lines * cellHeight + (plan.Lines - 1) * PortraitMenuCardsMath.Gap;
        float buttonsTop = plan.Top + (cardsHeight + PortraitMenuCardsMath.Gap) * plan.Scale;

        PlaceRow(cardsRow, screen, plan.Scale, new Vector2(state.Safe.GetCenter().X, plan.Top));
        for (int i = 0; i < cards.Count; i++)
        {
            (float x, float y) = PortraitMenuCardsMath.CellOffset(
                plan.Starts, i, cellWidth, cellHeight, PortraitMenuCardsMath.Gap,
                drawn[i].Size.X, drawn[i].Size.Y, drawn[i].Position.X, drawn[i].Position.Y);
            var place = new Vector2(x, y);
            if (cards[i].Position.DistanceSquaredTo(place) > 0.25f) cards[i].Position = place;
        }

        PlaceRow(buttonsRow, screen, plan.Scale, new Vector2(state.Safe.GetCenter().X, buttonsTop));
        if (buttons.Count > 0)
        {
            float gap = buttonsRow.GetThemeConstant("separation");
            if (gap <= 0f) gap = CompendiumButtonGap;
            var widths = buttons.Select(button => ButtonSize(button).X).ToArray();
            float total = widths.Sum() + gap * (buttons.Count - 1);
            float x = -total * 0.5f;
            for (int i = 0; i < buttons.Count; i++)
            {
                var place = new Vector2(x, 0f);
                if (buttons[i].Position.DistanceSquaredTo(place) > 0.25f) buttons[i].Position = place;
                x += widths[i] + gap;
            }
        }

        UpdateCompendiumFocus(cards, buttons, plan.Starts, state);
    }

    /// <summary>A bottom button's native box; its container may not have sized it yet on the screen's first frame.</summary>
    private static Vector2 ButtonSize(Control button) =>
        button.Size.X >= 1f && button.Size.Y >= 1f ? button.Size : button.GetCombinedMinimumSize();

    /// <summary>Puts a row at a canvas point under the screen's transform and scales it about its top left corner.</summary>
    private static void PlaceRow(Control row, Control screen, float scale, Vector2 localPoint)
    {
        row.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        if (row.PivotOffset != Vector2.Zero) row.PivotOffset = Vector2.Zero;
        var rowScale = new Vector2(scale, scale);
        if (row.Scale.DistanceSquaredTo(rowScale) > 0.000001f) row.Scale = rowScale;
        Vector2 target = screen.GetGlobalTransformWithCanvas() * localPoint;
        if (row.GlobalPosition.DistanceSquaredTo(target) > 0.25f) row.GlobalPosition = target;
    }

    /// <summary>
    /// The compendium's focus after the reflow: each card reaches the cards beside it and the nearest card on the line below
    /// or above, the last line reaches down to the bottom buttons, and the buttons reach along their row and up to the cards.
    /// Rebuilt only when the visible set changes; the desktop neighbours are kept for landscape.
    /// </summary>
    private static void UpdateCompendiumFocus(
        IReadOnlyList<Control> cards, IReadOnlyList<Control> buttons, int[] starts, CompendiumState state)
    {
        if (state.FocusCards == cards.Count && state.FocusButtons == buttons.Count) return;
        PortraitCardFocus.LinkLines(cards, starts, SaveCompendiumFocus);
        int lastLine = starts.Length - 2;
        for (int i = starts[lastLine]; i < cards.Count; i++)
        {
            Control? button = Nearest(buttons, cards[i]);
            cards[i].FocusNeighborBottom = (button ?? cards[i]).GetPath();
        }
        for (int i = 0; i < buttons.Count; i++)
        {
            SaveCompendiumFocus(buttons[i]);
            buttons[i].FocusNeighborLeft = buttons[i > 0 ? i - 1 : i].GetPath();
            buttons[i].FocusNeighborRight = buttons[i < buttons.Count - 1 ? i + 1 : i].GetPath();
            buttons[i].FocusNeighborTop = (Nearest(cards.Skip(starts[lastLine]), buttons[i]) ?? buttons[i]).GetPath();
            buttons[i].FocusNeighborBottom = buttons[i].GetPath();
        }
        state.FocusCards = cards.Count;
        state.FocusButtons = buttons.Count;
    }

    /// <summary>The visible control whose drawn centre is nearest the target's, or null when there is none.</summary>
    private static Control? Nearest(IEnumerable<Control> candidates, Control target)
    {
        float targetX = target.GetGlobalRect().GetCenter().X;
        Control? nearest = null;
        float best = float.MaxValue;
        foreach (Control candidate in candidates)
        {
            if (!GodotObject.IsInstanceValid(candidate)) continue;
            float distance = MathF.Abs(candidate.GetGlobalRect().GetCenter().X - targetX);
            if (distance >= best) continue;
            best = distance;
            nearest = candidate;
        }
        return nearest;
    }

    private static void SaveCompendiumFocus(Control control)
    {
        ulong id = control.GetInstanceId();
        if (CompendiumFocus.ContainsKey(id)) return;
        CompendiumFocus[id] = (control.FocusNeighborLeft, control.FocusNeighborRight,
            control.FocusNeighborTop, control.FocusNeighborBottom);
    }

    /// <summary>Puts the desktop focus links back and forgets the portrait plans (the rows' parents are restored separately).</summary>
    private static void RestoreCompendiumPortrait()
    {
        foreach (CompendiumState state in CompendiumStates.Values) UnhookRows(state);
        CompendiumStates.Clear();
        foreach (var entry in CompendiumFocus)
        {
            ulong id = entry.Key;
            (NodePath? left, NodePath? right, NodePath? top, NodePath? bottom) = entry.Value;
            if (GodotObject.InstanceFromId(id) is not Control control || !GodotObject.IsInstanceValid(control)) continue;
            if (left is { } l) control.FocusNeighborLeft = l;
            if (right is { } r) control.FocusNeighborRight = r;
            if (top is { } t) control.FocusNeighborTop = t;
            if (bottom is { } b) control.FocusNeighborBottom = b;
        }
        CompendiumFocus.Clear();
    }

    private static void PruneCompendiumDead()
    {
        foreach (ulong id in CompendiumStates.Keys.Where(key => !GodotObject.IsInstanceIdValid(key)).ToList())
            CompendiumStates.Remove(id);
        foreach (ulong id in CompendiumFocus.Keys.Where(key => !GodotObject.IsInstanceIdValid(key)).ToList())
            CompendiumFocus.Remove(id);
    }
}
