using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

internal sealed partial class TestDriver
{
    /// <summary>
    /// Checks the native draw, discard and exhaust piles in a fight: empty exhaust hidden, exhaust appearing with its native
    /// count and view, and the rail staying where it is across a turn. Where the rail is belongs to the host, so its
    /// placement, touch targets and End Turn stability are host partials.
    /// </summary>
    private async Task RunCombatPilesAsync(RunState state)
    {
        // Both probes arm before EnterRoomDebug: the rail from the room's first frames, the opening hand from its draw.
        BeginCombatRailProbe();
        BeginHandDrawProbe("first_turn");
        try
        {
            var room = await EnterCombatAsync();
            MarkCombatRailProbeTurnStarted();
            await FinishHandDrawProbeAsync("first_turn");
            var ui = room.Ui;
            var exhaust = ui.ExhaustPile;
            _report.Check("empty_exhaust_pile_starts_hidden", !exhaust.IsVisibleInTree(), "native visibility before first exhaust");
            Rect2[] before = [ui.DrawPile.GetGlobalRect(), ui.EndTurnButton.GetGlobalRect(), ui.DiscardPile.GetGlobalRect()];
            var card = ui.Hand.ActiveHolders.First().CardModel!;
            await CardCmd.Exhaust(new ThrowingPlayerChoiceContext(), card);
            await WaitSecondsAsync(0.75);
            // Runs to 1.5 s after the first player turn, covering the rail's arrival and the exhaust pile appearing.
            await FinishCombatRailProbeAsync();
            Rect2 box = exhaust.GetGlobalRect();
            _report.Check("exhaust_pile_appears_with_native_count", exhaust.IsVisibleInTree() && exhaust.IsEnabled
                && exhaust.GetNode<Label>("CountContainer/Count").Text == "1", $"bounds={box}");
            await CheckCombatPilesRailAsync(room, before);
            await CaptureAsync("combat_piles_rail");
            await _pointer.TapAsync(CenterOf(exhaust));
            await WaitUntilAsync(() => FindAll<NCardPileScreen>(_tree.Root).Any(s => s.IsVisibleInTree() && s.Pile.Type == PileType.Exhaust),
                "native Exhaust pile screen opens", ShortWait);
            var screen = FindAll<NCardPileScreen>(_tree.Root).Single(s => s.IsVisibleInTree() && s.Pile.Type == PileType.Exhaust);
            _report.Check("native_exhaust_view_has_exhausted_card", screen.Pile.Cards.Contains(card), $"cards={screen.Pile.Cards.Count}");
            await CaptureAsync("combat_piles_exhaust_view");
            await _pointer.TapAsync(CenterOf(screen.GetNode<Control>("BackButton")));
            await WaitUntilAsync(() => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree(), "close the native Exhaust pile", ShortWait);
            _report.Pass("native_exhaust_view_closes");
            await WaitForCombatPilesEndTurnAsync(ui.EndTurnButton);
            // The turn-start draw is sampled from before End Turn through the next player turn.
            BeginHandDrawProbe("turn_two");
            // The enemy's turn, from the End Turn tap to the next player phase: what the host's own passes cost while
            // intents, damage numbers, statuses and bars change (reported, never gated).
            BeginEnemyTurnCostProbe();
            await _pointer.TapAsync(CenterOf(ui.EndTurnButton));
            await WaitUntilAsync(() => CombatManager.Instance.PlayerActionsDisabled, "End Turn advances combat", ShortWait);
            await WaitUntilAsync(IsPlayerPlayPhase, "next player turn", MediumWait);
            await FinishHandDrawProbeAsync("turn_two");
            await FinishEnemyTurnCostProbeAsync("enemy_turn");
            await CheckEndTurnAfterNextTurnAsync(room);
            // A modified turn draw: MachineLearningPower adds 2 to the native 5-card draw, so the next hand arrives as 7.
            // The first 5 are already in the hand when the 6th/7th arrive; the probe's no-retarget check proves the extra
            // cards do not move them. The power is a real game model applied through the native command, test-only.
            var player = state.Players[0];
            await PowerCmd.Apply<MachineLearningPower>(new ThrowingPlayerChoiceContext(), player.Creature, 2m,
                null, null, silent: true);
            _report.Pass("modified_draw_modifier", "MachineLearningPower x2: next turn draws 7 (5 + 2)");
            await WaitForCombatPilesEndTurnAsync(ui.EndTurnButton);
            BeginHandDrawProbe("modified_turn", 2);
            await _pointer.TapAsync(CenterOf(ui.EndTurnButton));
            await WaitUntilAsync(() => CombatManager.Instance.PlayerActionsDisabled, "End Turn advances combat", ShortWait);
            await WaitUntilAsync(IsPlayerPlayPhase, "modified draw turn", MediumWait);
            await FinishHandDrawProbeAsync("modified_turn");
            // One more turn draw on a simulated slow device (30 fps plus a 120ms hitch after every card arrival); the
            // probe's arrival_still/straight checks run under hand_draw_slow_turn_*.
            await CheckSlowTurnDrawAsync(room);
            // A reduced turn-1 draw: BigMushroom's native ModifyHandDraw subtracts 2 on turn 1 (5 -> 3). The first combat
            // is already running, so the harness enters a second short combat with the same encounter while the relic is
            // owned. The probe then records its turn-1 draw; every card's layout target must be locked for the final
            // count from the moment it arrives, so releasing the lock does not re-centre the hand.
            double reducedStart = Time.GetTicksMsec();
            RelicModel? mushroom = null;
            try
            {
                // The live combat's encounter is already mutable; use the canonical model by id (its id is copied by
                // MemberwiseClone). If it cannot be resolved, fail and skip: never silently switch to a different fight.
                EncounterModel? liveEncounter = CombatManager.Instance.DebugOnlyGetState()?.Encounter;
                EncounterModel? canonicalEncounter = liveEncounter is null
                    ? null
                    : ModelDb.GetByIdOrNull<EncounterModel>(liveEncounter.Id);
                if (canonicalEncounter is null)
                {
                    _report.Fail("reduced_first_turn_completed",
                        liveEncounter is null
                            ? "no live combat encounter to reuse for the reduced first draw"
                            : $"canonical encounter {liveEncounter.Id} not found in ModelDb; refusing to enter a different fight");
                    return;
                }
                mushroom = await RelicCmd.Obtain<BigMushroom>(player);
                _report.Pass("reduced_draw_modifier", "BigMushroom owned: native turn-1 draw 5 - 2 = 3");
                // The native turn-1 draw is max(5 - 2, Innate cards): the shuffle moves Innate to the top and the
                // draw count is raised to cover them. Count them in the deck before the new combat starts.
                int innate = player.Deck.Cards.Count(card => card.Keywords.Contains(CardKeyword.Innate));
                int expectedHand = Math.Max(3, innate);
                BeginHandDrawProbe("reduced_first_turn", allCardsStill: true);
                NCombatRoom reducedRoom = await EnterCombatAsync(canonicalEncounter);
                await FinishHandDrawProbeAsync("reduced_first_turn");
                _report.Check("reduced_first_turn_hand_count",
                    reducedRoom.Ui.Hand.ActiveHolders.Count == expectedHand,
                    $"hand={reducedRoom.Ui.Hand.ActiveHolders.Count} expected={expectedHand} " +
                    $"(BigMushroom 5 - 2 = 3; {innate} Innate card(s) raise it, normal first turn drew 5)");
                _report.Pass("reduced_first_turn_completed",
                    $"same encounter {canonicalEncounter.Id} entered with BigMushroom owned");
            }
            catch (Exception error)
            {
                // Keep the failure here and let the scenario finish so the earlier and post-run steps are still recorded.
                _report.Fail("reduced_first_turn_completed", error.Message);
            }
            finally
            {
                if (mushroom is not null)
                {
                    try
                    {
                        await RelicCmd.Remove(mushroom);
                        _report.Pass("reduced_first_turn_relic_removed", "BigMushroom removed");
                    }
                    catch (Exception error)
                    {
                        _report.Fail("reduced_first_turn_relic_removed", error.Message);
                    }
                }
                _report.Pass("reduced_first_turn_cost",
                    $"{(Time.GetTicksMsec() - reducedStart) / 1000.0:F1}s (second short combat + turn-1 draw)");
            }
        }
        finally
        {
            CancelHandDrawProbe();
            CancelCombatRailProbe();
        }
    }

    private Task WaitForCombatPilesEndTurnAsync(NEndTurnButton button) =>
        WaitUntilAsync(() => IsPlayerPlayPhase() && button.IsEnabled && button.IsVisibleInTree() &&
            (button.Get("_positionTween").AsGodotObject() is not Tween tween || !tween.IsRunning()),
            "End Turn enabled and its entrance tween finished", ShortWait);
}
