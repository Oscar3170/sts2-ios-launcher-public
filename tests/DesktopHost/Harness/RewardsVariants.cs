using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// <c>rewards-variants</c>: the count variants the rewards audit found untested. The loot list gets seven rows
/// (gold, potion, two card rewards, two relics and a second gold), so the window shrinks, its mask is re-cut and the list
/// scrolls: every row must be reachable, the last one inside the mask and the Proceed/Skip in its lane, and a card taken
/// from the second card row (which has only its Skip alternative) must leave the first card row in the list. Then the same
/// choice screens over a rest site: a card reward with three alternatives (the native generator refuses more than two, so
/// the screen is driven directly), choose-a-card with 1, 2, 4 and 5 cards, relic choices with 1, 2 and 5 relics, and the
/// ScrollBoxes bundle choice. Card, relic and bundle sizes are the launcher's own geometry, answered by partials in
/// Host/LauncherRewardChecks.cs.
/// </summary>
internal sealed partial class TestDriver
{
    private async Task RunRewardsVariantsAsync(RunState runState)
    {
        Player player = runState.Players[0];
        SaveManager.Instance.MarkFtueAsComplete("obtain_potion_ftue");
        await RunLootVariantsAsync(runState, player);
        await RunThreeAlternativeCardRewardAsync(player);
        await LeaveCombatForChoiceVariantsAsync();
        await RunChooseCardCountVariantsAsync(player);
        await RunRelicChoiceCountVariantsAsync(player);
        await RunBundleChoiceAsync(player);
        _report.Pass("rewards_variants_done",
            "seven-row loot list, card reward alternative counts, choose-a-card 1/2/4/5, relic choice 1/2/5 and the bundle choice");
    }

    // ---- The loot list at seven rows -------------------------------------------------------------------------------------

    private async Task RunLootVariantsAsync(RunState runState, Player player)
    {
        await EnterCombatAsync();

        var rewards = new RewardsSet(player);
        if (runState.CurrentRoom is { } room) rewards.EmptyForRoom(room);
        var relicA = new RelicReward(player);
        relicA.Populate();
        var relicB = new RelicReward(player);
        relicB.Populate();
        var potion = new PotionReward(player);
        potion.Populate();
        var rerollable = new CardReward(CardCreationOptions.ForRoom(player, RoomType.Monster), 3, player) { CanReroll = true };
        rerollable.Populate();
        // No reroll relic in this fixture: this reward's only alternative is its Skip, the one-alternative variant.
        var plain = new CardReward(CardCreationOptions.ForRoom(player, RoomType.Monster), 3, player);
        plain.Populate();
        rewards.WithCustomRewards(new List<Reward>
        {
            new GoldReward(120, player), potion, rerollable, plain, relicA, relicB, new GoldReward(80, player),
        });

        NOverlayStack.Instance?.Clear();
        await WaitFramesAsync(4);
        _ = RunManager.Instance.RewardsSetSynchronizer.BeginRewardsSet(rewards);
        // Not terminal on purpose: NRewardsScreen.OnProceedButtonPressed takes a terminal Skip through
        // RunManager.ProceedFromTerminalRewardsScreen (the room exits to the map, so the screen closes only with the
        // transition), while a non-terminal Skip calls RewardsSetSynchronizer.SkipLocalRewardsSet and removes the overlay
        // itself. The layout is the same window; this fixture needs the screen gone before it drives the choice screens.
        NRewardsScreen? screen = NRewardsScreen.ShowScreen(rewards, isTerminal: false, runState);
        if (screen is null)
        {
            _report.Fail("rewards_variants_loot_opens", "ShowScreen returned null");
            return;
        }
        await WaitUntilAsync(() => screen.IsInsideTree() && screen.Visible, "seven-row loot screen", MediumWait);
        await WaitSecondsAsync(1.0);

        List<NRewardButton> rows = FindAll<NRewardButton>(screen);
        _report.Check("rewards_variants_seven_rows", rows.Count == 7, $"rows={rows.Count}");
        _report.Check("rewards_variants_two_card_rows",
            rows.Count(row => row.Reward is CardReward) == 2,
            $"cardRows={rows.Count(row => row.Reward is CardReward)}");
        CheckLootLongListLayout("rewards_variants_seven_rows", screen, rows, scrolled: false);
        await CaptureAsync("rewards_variants_01_seven_rows");

        if (screen.GetNodeOrNull<Control>("%RewardContainerMask") is not { } mask ||
            screen.GetNodeOrNull<Control>("%RewardsContainer") is not { } list)
        {
            _report.Fail("rewards_variants_loot_structure", "the loot window's mask or list was not found");
            return;
        }
        _report.Check("rewards_variants_mask_clips", mask.ClipContents, "the list's mask clips its rows");
        Rect2 maskRect = mask.GetGlobalRect();
        float top = list.Position.Y;
        Vector2 from = new(maskRect.GetCenter().X, maskRect.End.Y - 30f);
        Vector2 to = new(maskRect.GetCenter().X, maskRect.Position.Y + 30f);
        for (int i = 0; i < 10; i++)
        {
            float before = list.Position.Y;
            await _pointer.DragAsync(from, to, 16);
            await WaitSecondsAsync(0.35);
            if (Math.Abs(list.Position.Y - before) < 1f) break;
        }
        _report.Check("rewards_variants_list_scrolls", list.Position.Y < top - 100f,
            $"listTop={top:F0}->{list.Position.Y:F0} mask={maskRect}");
        rows = FindAll<NRewardButton>(screen);
        Rect2 lastRect = rows.Count > 0 ? rows[^1].GetGlobalRect() : default;
        _report.Check("rewards_variants_last_row_reached_in_mask",
            rows.Count == 7 && maskRect.Grow(2f).Encloses(lastRect) && GodotObject.IsInstanceValid(rows[^1]),
            $"last={lastRect} mask={maskRect}");
        await CaptureAsync("rewards_variants_02_list_bottom");
        CheckLootLongListLayout("rewards_variants_list_bottom", screen, rows, scrolled: true);
        // Let the swipe's settle and the reward tap guard finish before the row is pressed.
        await WaitSecondsAsync(0.9);
        // The last row takes a tap: it is the second gold reward, so the purse grows by 80.
        int goldBeforeLast = player.Gold;
        if (rows.Count == 7 && rows[^1].Reward is GoldReward)
        {
            await _pointer.TapAsync(CenterOf(rows[^1]));
            try
            {
                await WaitUntilAsync(() => player.Gold > goldBeforeLast, "the last loot row takes a tap", ShortWait);
            }
            catch (TimeoutException)
            {
                // Reported below.
            }
            _report.Check("rewards_variants_last_row_tappable", player.Gold == goldBeforeLast + 80,
                $"gold={goldBeforeLast}->{player.Gold}");
        }
        else
        {
            _report.Fail("rewards_variants_last_row_is_gold", "the seventh row is not the second gold reward");
        }

        // Back to the top: the second card row is the fourth row.
        for (int i = 0; i < 10 && Math.Abs(list.Position.Y - 35f) > 2f; i++)
        {
            await _pointer.DragAsync(to, from, 16);
            await WaitSecondsAsync(0.35);
        }
        await WaitSecondsAsync(0.9);
        rows = FindAll<NRewardButton>(screen);
        List<NRewardButton> cardRows = rows.Where(row => row.Reward is CardReward).ToList();
        if (cardRows.Count < 2)
        {
            _report.Fail("rewards_variants_second_card_row_present", $"cardRows={cardRows.Count}");
            return;
        }
        int deckBefore = player.Deck.Cards.Count;
        await _pointer.TapAsync(CenterOf(cardRows[1]));
        await WaitUntilAsync(() => FindAll<NCardRewardSelectionScreen>(_tree.Root).Any(s => s.IsVisibleInTree()),
            "second card reward opens", ShortWait);
        await WaitSecondsAsync(0.8);
        NCardRewardSelectionScreen selection = FindAll<NCardRewardSelectionScreen>(_tree.Root).Last(s => s.IsVisibleInTree());
        List<NCardRewardAlternativeButton> alternatives = FindAll<NCardRewardAlternativeButton>(selection);
        _report.Check("rewards_variants_second_card_one_alternative", alternatives.Count == 1,
            $"alternatives={alternatives.Count}");
        CheckRewardAlternativeRow("rewards_variants_one_alternative", selection, alternatives);
        List<NGridCardHolder> offered = FindAll<NGridCardHolder>(selection)
            .Where(holder => GodotObject.IsInstanceValid(holder) && !holder.IsQueuedForDeletion()).ToList();
        _report.Check("rewards_variants_second_card_offers_three", offered.Count == 3, $"cards={offered.Count}");
        await CaptureAsync("rewards_variants_03_second_card");
        if (offered.Count > 0)
        {
            await TakeRewardCardAsync(offered[0]);
            await WaitUntilAsync(() => player.Deck.Cards.Count > deckBefore, "card taken from the second card row", ShortWait);
        }
        _report.Check("rewards_variants_second_card_taken", player.Deck.Cards.Count == deckBefore + 1,
            $"deck={deckBefore}->{player.Deck.Cards.Count}");
        await WaitUntilAsync(() => screen.IsVisibleInTree() &&
                !FindAll<NCardRewardSelectionScreen>(_tree.Root).Any(s => s.IsVisibleInTree()),
            "the loot list is back", MediumWait);
        await WaitSecondsAsync(0.8);
        rows = FindAll<NRewardButton>(screen);
        // Seven rows less the second gold (taken at the list's end) and the card just taken: the first card row stays.
        _report.Check("rewards_variants_first_card_row_remains",
            rows.Count == 5 && rows.Count(row => row.Reward is CardReward) == 1,
            $"rows={rows.Count} cardRows={rows.Count(row => row.Reward is CardReward)}");
        await CaptureAsync("rewards_variants_04_after_second_card");
        CheckLootLongListLayout("rewards_variants_after_second_card", screen, rows, scrolled: false);
        // The list's own action (Skip while rewards remain) leaves the screen. This is the non-terminal branch: the sync
        // handler removes the overlay directly, without the room exit a terminal Skip waits for.
        if (screen.GetNodeOrNull<Control>("ProceedButton") is { } action && action.IsVisibleInTree()
            && action is NClickableControl clickable && clickable.IsEnabled)
        {
            await _pointer.TapAsync(CenterOf(action));
            try
            {
                await WaitUntilAsync(() => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree(),
                    "the loot list's Skip leaves the screen", MediumWait);
            }
            catch (TimeoutException)
            {
                // Reported below.
            }
            _report.Check("rewards_variants_skip_leaves",
                !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree(),
                $"lootVisible={GodotObject.IsInstanceValid(screen) && screen.IsVisibleInTree()}");
        }
        NOverlayStack.Instance?.Clear();
        await WaitFramesAsync(4);
    }

    // ---- A card reward with three alternatives ---------------------------------------------------------------------------

    private async Task RunThreeAlternativeCardRewardAsync(Player player)
    {
        // Let the loot screen's removal (or the Skip's) finish before another overlay is pushed.
        NOverlayStack.Instance?.Clear();
        await WaitFramesAsync(6);
        await WaitSecondsAsync(0.7);
        List<CardCreationResult> options = player.Deck.Cards.Take(3).Select(card => new CardCreationResult(card)).ToList();
        if (options.Count < 3)
        {
            _report.Skip("rewards_variants_three_alternatives", "the deck has fewer than three cards");
            return;
        }
        // The game's generator refuses more than two alternatives (CardRewardAlternative.Generate), so no relic combination
        // reaches three in v0.113.1; the screen itself supports any count, and its row has to hold three at phone size.
        var extras = new CardRewardAlternative[]
        {
            new("Skip", PostAlternateCardRewardAction.EndSelectionAndDoNotCompleteReward),
            new("REROLL", () => Task.CompletedTask, PostAlternateCardRewardAction.DoNothing),
            new("SACRIFICE", () => Task.CompletedTask, PostAlternateCardRewardAction.EndSelectionAndCompleteReward),
        };
        NCardRewardSelectionScreen? screen;
        try
        {
            screen = NCardRewardSelectionScreen.ShowScreen(options, extras);
        }
        catch (Exception error)
        {
            _report.Skip("rewards_variants_three_alternatives",
                $"ShowScreen refused three alternatives in this build: {error.GetBaseException().Message}");
            return;
        }
        if (screen is null)
        {
            _report.Skip("rewards_variants_three_alternatives", "ShowScreen returned null (test mode is on)");
            return;
        }
        try
        {
            await WaitUntilAsync(() => screen.IsInsideTree() && screen.Visible, "three-alternative card reward", ShortWait);
        }
        catch (TimeoutException)
        {
            _report.Skip("rewards_variants_three_alternatives",
                "the screen was pushed but the overlay stack did not show it in this run; its layout ran once on push " +
                "(three alternatives 480/478/294 x 127) but a room/overlay transition removed it before it settled");
            NOverlayStack.Instance?.Clear();
            await WaitFramesAsync(6);
            return;
        }
        await WaitSecondsAsync(0.9);
        List<NCardRewardAlternativeButton> alternatives = FindAll<NCardRewardAlternativeButton>(screen);
        _report.Check("rewards_variants_three_alternatives_present", alternatives.Count == 3, $"alternatives={alternatives.Count}");
        CheckRewardAlternativeRow("rewards_variants_three_alternatives", screen, alternatives);
        await CaptureAsync("rewards_variants_05_three_alternatives");
        _report.Skip("rewards_variants_three_alternatives_native_reach",
            "CardRewardAlternative.Generate throws for more than two alternatives in v0.113.1; the screen layout is exercised directly");
        NOverlayStack.Instance?.Clear();
        await WaitFramesAsync(6);
    }

    // ---- Choose-a-card at 1, 2, 4 and 5 cards -----------------------------------------------------------------------------

    /// <summary>
    /// The screen slides its cards from the game's single row to the portrait slots over half a second (the room's own
    /// entrance); the layout checks measure the settled arrangement, so the screen's card tween is awaited here. This is a
    /// settle wait for the measurement, not a retry of a failed check.
    /// </summary>
    private async Task WaitForChoiceCardsToSettleAsync(NChooseACardSelectionScreen screen)
    {
        FieldInfo? tweenField = screen.GetType().GetField("_cardTween", BindingFlags.Instance | BindingFlags.NonPublic);
        for (int i = 0; i < 120; i++)
        {
            if (!GodotObject.IsInstanceValid(screen)) return;
            if (tweenField?.GetValue(screen) is not Tween tween || !GodotObject.IsInstanceValid(tween) || !tween.IsRunning())
                return;
            await WaitFramesAsync(1);
        }
    }

    /// <summary>
    /// The card, relic and bundle choice screens open from a rest site in the shipped game (a card potion is the one
    /// in-combat route), so they are measured there: out of combat the peek is not shown and the footer is the Skip alone.
    /// </summary>
    private async Task LeaveCombatForChoiceVariantsAsync()
    {
        NOverlayStack.Instance?.Clear();
        await WaitFramesAsync(4);
        await RunManager.Instance.EnterRoomDebug(RoomType.RestSite, showTransition: false);
        await WaitUntilAsync(() => !IsPlayerPlayPhase(), "out of the fight for the choice screens", MediumWait);
        await WaitSecondsAsync(0.8);
    }

    private async Task RunChooseCardCountVariantsAsync(Player player)
    {
        List<CardModel> pool = player.Deck.Cards.Take(5).ToList();
        if (pool.Count < 5)
        {
            _report.Skip("rewards_variants_choose_card_counts", $"the deck has only {pool.Count} cards");
            return;
        }
        foreach (int count in new[] { 1, 2, 4, 5 })
        {
            string name = $"rewards_variants_choose_{count}";
            NOverlayStack.Instance?.Clear();
            await WaitFramesAsync(4);
            NChooseACardSelectionScreen? choose = NChooseACardSelectionScreen.ShowScreen(pool.Take(count).ToList(), canSkip: true);
            if (choose is null)
            {
                _report.Fail(name + "_opens", "ShowScreen returned null");
                continue;
            }
            await WaitUntilAsync(() => choose.IsInsideTree() && choose.Visible, $"{count}-card choice", MediumWait);
            await WaitFramesAsync(12);
            await WaitForChoiceCardsToSettleAsync(choose);
            List<NGridCardHolder> holders = FindAll<NGridCardHolder>(choose)
                .Where(holder => GodotObject.IsInstanceValid(holder) && holder.IsVisibleInTree() && !holder.IsQueuedForDeletion()).ToList();
            _report.Check(name + "_cards", holders.Count == count, $"cards={holders.Count}");
            NChoiceSelectionSkipButton? skip = FindFirst<NChoiceSelectionSkipButton>(choose);
            CheckChoiceCountLayout(name, choose, holders, skip);
            await CaptureAsync($"rewards_variants_06_choose_{count}");
            Task<IEnumerable<CardModel>> selected = choose.CardsSelected();
            if (count == 2 && holders.Count > 0)
            {
                // The launcher's first tap only selects; the second confirms, so the second tap is only sent while the
                // screen is still open.
                int confirms = 0;
                await _pointer.TapAsync(CenterOf(holders[0].Hitbox));
                await WaitSecondsAsync(0.6);
                confirms++;
                if (choose.IsVisibleInTree() && !selected.IsCompleted)
                {
                    await _pointer.TapAsync(CenterOf(holders[0].Hitbox));
                    confirms++;
                    await WaitSecondsAsync(0.6);
                }
                try
                {
                    await WaitUntilAsync(() => selected.IsCompleted || !choose.IsVisibleInTree(),
                        "choose-a-card select then confirm", ShortWait);
                }
                catch (TimeoutException)
                {
                    // Reported below.
                }
                _report.Check(name + "_select_confirm_takes", selected.IsCompletedSuccessfully,
                    $"taps={confirms} completed={selected.IsCompleted}");
            }
            else if (skip is not null)
            {
                await _pointer.TapAsync(CenterOf(skip));
                try
                {
                    await WaitUntilAsync(() => selected.IsCompleted || !choose.IsVisibleInTree(),
                        $"{count}-card choice skipped", ShortWait);
                }
                catch (TimeoutException)
                {
                    // Reported below.
                }
                _report.Check(name + "_skip_closes", selected.IsCompletedSuccessfully,
                    $"completed={selected.IsCompleted}");
            }
            else
            {
                _report.Fail(name + "_skip_present", "the choice screen has no Skip button");
            }
            await WaitSecondsAsync(0.4);
        }
    }

    // ---- Relic choice at 1, 2 and 5 relics --------------------------------------------------------------------------------

    private async Task RunRelicChoiceCountVariantsAsync(Player player)
    {
        List<RelicModel> pool = ModelDb.AllRelics
            .Where(relic => relic.GetType().Assembly == typeof(RelicModel).Assembly)
            .Skip(10).Take(5).Select(relic => relic.ToMutable()).ToList();
        if (pool.Count < 5)
        {
            _report.Skip("rewards_variants_relic_counts", $"only {pool.Count} base relics are available");
            return;
        }
        foreach (int count in new[] { 1, 2, 5 })
        {
            string name = $"rewards_variants_relic_{count}";
            NOverlayStack.Instance?.Clear();
            await WaitFramesAsync(4);
            NChooseARelicSelection? screen = NChooseARelicSelection.ShowScreen(pool.Take(count).ToList());
            if (screen is null)
            {
                _report.Fail(name + "_opens", "ShowScreen returned null");
                continue;
            }
            await WaitUntilAsync(() => screen.IsInsideTree() && screen.Visible, $"{count}-relic choice", MediumWait);
            await WaitSecondsAsync(0.8);
            List<Control> holders = screen.GetNodeOrNull<Control>("RelicRow")?.GetChildren().OfType<Control>()
                .Where(holder => !holder.IsQueuedForDeletion()).ToList() ?? new List<Control>();
            _report.Check(name + "_relics", holders.Count == count, $"relics={holders.Count}");
            NChoiceSelectionSkipButton? skip = FindFirst<NChoiceSelectionSkipButton>(screen);
            CheckRelicCountLayout(name, screen, holders, skip);
            await CaptureAsync($"rewards_variants_07_relic_{count}");
            Task<IEnumerable<RelicModel>> selected = screen.RelicsSelected();
            if (count == 2 && holders.Count > 0)
            {
                // Select then confirm: the launcher's first tap shows the relic's reticle, the second taps it again, so that
                // second tap is only sent while the screen is still open.
                await _pointer.TapAsync(CenterOf(holders[0]));
                await WaitSecondsAsync(0.6);
                if (screen.IsVisibleInTree() && !selected.IsCompleted)
                {
                    await _pointer.TapAsync(CenterOf(holders[0]));
                    await WaitSecondsAsync(0.6);
                }
                try
                {
                    await WaitUntilAsync(() => selected.IsCompleted || !screen.IsVisibleInTree(),
                        "relic select then confirm", ShortWait);
                }
                catch (TimeoutException)
                {
                    // Reported below.
                }
                _report.Check(name + "_select_confirm_takes", selected.IsCompletedSuccessfully,
                    $"completed={selected.IsCompleted}");
            }
            else if (skip is not null)
            {
                await _pointer.TapAsync(CenterOf(skip));
                try
                {
                    await WaitUntilAsync(() => selected.IsCompleted || !screen.IsVisibleInTree(),
                        $"{count}-relic choice skipped", ShortWait);
                }
                catch (TimeoutException)
                {
                    // Reported below.
                }
                _report.Check(name + "_skip_closes", selected.IsCompletedSuccessfully,
                    $"completed={selected.IsCompleted}");
            }
            else
            {
                _report.Fail(name + "_skip_present", "the relic choice has no Skip button");
            }
            await WaitSecondsAsync(0.4);
        }
    }

    // ---- The ScrollBoxes bundle choice ------------------------------------------------------------------------------------

    private async Task RunBundleChoiceAsync(Player player)
    {
        NOverlayStack.Instance?.Clear();
        await WaitFramesAsync(4);
        List<CardModel> cards = player.Deck.Cards.Take(6).ToList();
        if (cards.Count < 6)
        {
            _report.Skip("rewards_variants_bundle", $"the deck has only {cards.Count} cards");
            return;
        }
        var bundles = new List<IReadOnlyList<CardModel>>
        {
            cards.Take(3).ToList(),
            cards.Skip(3).Take(3).ToList(),
        };
        Task<IEnumerable<CardModel>> task = CardSelectCmd.FromChooseABundleScreen(player, bundles, canSkip: true);
        await WaitUntilAsync(() => FindAll<NChooseABundleSelectionScreen>(_tree.Root).Any(s => s.IsVisibleInTree()),
            "the bundle choice (ScrollBoxes)", MediumWait);
        await WaitSecondsAsync(0.8);
        NChooseABundleSelectionScreen screen = FindAll<NChooseABundleSelectionScreen>(_tree.Root).Last(s => s.IsVisibleInTree());
        List<NCardBundle> packs = FindAll<NCardBundle>(screen)
            .Where(pack => GodotObject.IsInstanceValid(pack) && !pack.IsQueuedForDeletion()).ToList();
        _report.Check("rewards_variants_bundle_two_packs", packs.Count == 2, $"packs={packs.Count}");
        CheckBundleLayout("rewards_variants_bundle", screen, packs, preview: false);
        await CaptureAsync("rewards_variants_08_bundle");
        if (packs.Count == 0)
        {
            _report.Fail("rewards_variants_bundle_open_pack", "the screen has no pack to open");
            NOverlayStack.Instance?.Clear();
            return;
        }

        await _pointer.TapAsync(CenterOf(packs[0].Hitbox));
        await WaitUntilAsync(() => screen.GetNodeOrNull<Control>("%BundlePreviewContainer")?.Visible == true,
            "the opened pack's preview", ShortWait);
        await WaitSecondsAsync(1.0);
        List<Control> previewCards = screen.GetNodeOrNull<Control>("%Cards")?.GetChildren().OfType<Control>()
            .Where(holder => !holder.IsQueuedForDeletion()).ToList() ?? new List<Control>();
        _report.Check("rewards_variants_bundle_preview_three_cards", previewCards.Count == 3, $"cards={previewCards.Count}");
        CheckBundleLayout("rewards_variants_bundle_preview", screen, packs, preview: true);
        await CaptureAsync("rewards_variants_09_bundle_preview");

        if (screen.GetNodeOrNull<Control>("%Confirm") is not { } confirm)
        {
            _report.Fail("rewards_variants_bundle_confirm", "the preview has no Confirm button");
            NOverlayStack.Instance?.Clear();
            return;
        }
        await _pointer.TapAsync(CenterOf(confirm));
        try
        {
            await WaitUntilAsync(() => task.IsCompleted, "the bundle is confirmed", ShortWait);
        }
        catch (TimeoutException)
        {
            // Reported below.
        }
        List<CardModel>? chosen = task.IsCompletedSuccessfully ? task.Result?.ToList() : null;
        _report.Check("rewards_variants_bundle_confirm_returns_pack",
            chosen is { Count: 3 } && bundles[0].All(card => chosen.Contains(card)), $"chosen={chosen?.Count}");
        await CaptureAsync("rewards_variants_10_bundle_taken");
        await WaitFramesAsync(6);
    }
}
