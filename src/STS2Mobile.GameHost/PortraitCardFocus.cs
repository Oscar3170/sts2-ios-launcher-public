using System;
using System.Collections.Generic;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// The focus neighbours of a card laid in balanced lines, shared by the mode cards and the compendium: left and right stay
/// in the line, top and bottom go to the nearest card in the line above and below, and the first and last lines point at
/// themselves (there is nothing above or below to reach). The caller saves each card's desktop neighbours before the first
/// write so landscape can have them back.
/// </summary>
internal static class PortraitCardFocus
{
    public static void LinkLines(IReadOnlyList<Control> cards, int[] starts, Action<Control> save)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            (int line, int col) = PortraitMenuCardsMath.Cell(starts, i);
            save(cards[i]);
            cards[i].FocusNeighborLeft = cards[starts[line]].GetPath();
            cards[i].FocusNeighborRight = cards[starts[line + 1] - 1].GetPath();
            cards[i].FocusNeighborTop = cards[line == 0 ? i : PortraitMenuCardsMath.NearestInLine(starts, line - 1, col)].GetPath();
            cards[i].FocusNeighborBottom =
                cards[line == starts.Length - 2 ? i : PortraitMenuCardsMath.NearestInLine(starts, line + 1, col)].GetPath();
        }
    }
}
