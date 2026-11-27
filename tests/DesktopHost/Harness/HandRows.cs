using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

internal sealed partial class TestDriver
{
    /// <summary>
    /// Draws the hand one card at a time and checks that every count lands in the upper row first and leaves the rest of the
    /// fight alone. The host supplies its row policy: the launcher's permanent two-row hand with up to ten cards
    /// (LauncherHandChecks.cs).
    /// </summary>
    private async Task RunHandRowsAsync(RunState state)
    {
        await EnterCombatAsync();
        NPlayerHand hand = NPlayerHand.Instance!;
        // The old 2 s settle covered the hand's entry presentation; the rows scenario reads the hand, not the room's health bars.
        await WaitUntilStableAsync(() => IsPlayerPlayPhase() ? HandSettledSignature(hand) : null,
            "hand-rows intro", TimeSpan.FromSeconds(2));
        var player = state.Players[0];
        var initial = hand.ActiveHolders.Select(h => h.CardModel).OfType<CardModel>().ToList();
        await CardPileCmd.Add(initial, PileType.Draw);
        await WaitUntilAsync(() => hand.ActiveHolders.Count == 0, "empty fixture hand", ShortWait);
        await WaitSecondsAsync(0.6);
        NCombatRoom room = NCombatRoom.Instance!;
        float? upperY = null;
        for (int count = 1; count <= HandRowsCardLimit(); count++)
        {
            await CardPileCmd.Draw(new ThrowingPlayerChoiceContext(), player);
            await WaitUntilAsync(() => hand.ActiveHolders.Count == count, $"hand has {count} cards", ShortWait);
            // The native draw and the hand's reflow: holders ease to their targets with per-frame Lerps and snap at <1
            // unit, so give the wait room past the old 0.8 s fixed wait; it returns as soon as every holder has snapped.
            await WaitUntilStableAsync(() => HandSettledSignature(hand),
                $"hand_rows_{count} layout", TimeSpan.FromSeconds(1.2));
            NHandCardHolder[] cards = hand.ActiveHolders.ToArray();
            float y = HandRowsRowY(cards[0]);
            upperY ??= y;
            _report.Check($"hand_{count}_upper_row_stable", Math.Abs(y - upperY.Value) < HandRowsRowTolerance(),
                $"first card Y={y:F1}, one-card Y={upperY:F1}");
            int upperCount = HandRowsUpperCount(count);
            float lowerLine = upperCount < cards.Length ? HandRowsRowY(cards[upperCount]) : y;
            for (int i = 0; i < cards.Length; i++)
            {
                float cardY = HandRowsRowY(cards[i]);
                bool upper = i < upperCount;
                bool rowOk = upper
                    ? Math.Abs(cardY - y) < HandRowsRowTolerance()
                    : cardY > y + 100f && Math.Abs(cardY - lowerLine) < HandRowsLowerRowTolerance();
                _report.Check($"hand_{count}_card_{i}_row", rowOk, $"Y={cardY:F1} upper={upper}");
            }
            CheckHandRowsLayout(count, cards, room);
            await CheckHandRowsModesAsync(count, cards, room);
            await CaptureAsync($"hand_rows_{count}");
        }
        ResetFlatHandMode();
    }
}
