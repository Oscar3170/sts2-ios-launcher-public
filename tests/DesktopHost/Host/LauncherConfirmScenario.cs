using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Nodes.TreasureRooms;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// <c>select-confirm</c> on the launcher host: the shop's press-selects/second-tap-confirms interaction on every screen that
/// lost its Choose button. Each phase is a real native screen with its real commit path:
/// <list type="number">
/// <item>Card reward (from a loot row): first tap selects with the native hover tip and takes nothing; another item moves
/// the selection; a press elsewhere clears it; a hold opens the native inspection and takes nothing; a swipe takes nothing;
/// the second tap answers the reward with exactly the intended card. Reroll clears the selection and Skip still works.</item>
/// <item>Choose-a-card (Attack Potion): the same select/move/clear/hold/swipe checks, Peek still works, and the second tap
/// takes exactly one card.</item>
/// <item>Relic choice (<c>NChooseARelicSelection.ShowScreen</c>, the fixture v0.113.1 has no caller for): the selection
/// moves and clears, the hold opens the native relic inspection, the second tap answers with exactly the intended relic.</item>
/// <item>Treasure: a tap during the chest's entrance does nothing; the normal one-relic chest takes on the game's own
/// single tap with no selection or reticle, and a three-relic one-round chest (the shared fixture's reward-count path)
/// selects on the first tap, shows the reticle and tip, and takes on the second.</item>
/// <item>Portrait → landscape with a selection open: the selection and reticle go, a landscape tap takes natively, and back
/// in portrait the two taps work again.</item>
/// </list>
/// The live switch is the launcher's <c>PortraitLayout.Apply</c> (<see cref="LauncherHostWiring.ApplyOrientation"/>), the
/// same call the Graphics row makes; the host window does not rotate, the canvas changes instead.
/// </summary>
internal sealed partial class TestDriver
{
    private const string SelectConfirmReticle = "PhoneSelectedReticle";

    /// <summary>
    /// The launcher's selection reticle on an item: a child of the item (cards, relic choice, chest) or of its hitbox (shop
    /// slots). Only the selected reticle counts: a deselected one fades out for 0.2 s before it is freed.
    /// </summary>
    private static Control? ReticleNode(Control item, bool activeOnly = true)
    {
        if (!GodotObject.IsInstanceValid(item)) return null;
        Control? hitbox = item.GetNodeOrNull<Control>("%Hitbox");
        foreach (Control owner in ReferenceEquals(hitbox, null) ? new[] { item } : new[] { item, hitbox })
            foreach (Node child in owner.GetChildren())
                if (child is Control reticle && reticle.Name.ToString() == SelectConfirmReticle &&
                    (!activeOnly || reticle.Get("IsSelected").AsBool()))
                    return reticle;
        return null;
    }

    /// <summary>Whether any selected reticle hangs under <paramref name="root"/> (the harness's "gone" checks).</summary>
    private static bool ReticleSelectedUnder(Node root) => GodotObject.IsInstanceValid(root) && FindAll<Control>(root)
        .Any(node => node.Name.ToString() == SelectConfirmReticle && node.Get("IsSelected").AsBool());

    /// <summary>
    /// The native reticle, 0.6 s after the selection (its 0.5 s scale-in is over), must be opaque and cover the item's drawn
    /// rectangle: the same rectangle the game's reward buttons put it around, with the corners 10 units outside it.
    /// </summary>
    private async Task CheckReticleAsync(Control item, Control drawn, string step)
    {
        await WaitSecondsAsync(0.6);
        Control? reticle = ReticleNode(item);
        Rect2 itemRect = drawn.GetGlobalRect();
        bool shown = reticle is not null && GodotObject.IsInstanceValid(reticle) && reticle.IsVisibleInTree() &&
                     reticle.Modulate.A >= 0.95f;
        Rect2 reticleRect = shown ? reticle!.GetGlobalRect() : new Rect2();
        bool fits = shown && reticleRect.Grow(2f).Encloses(itemRect) && itemRect.Grow(2f).Encloses(reticleRect);
        _report.Check(step + "_reticle_visible", shown,
            $"alpha={(shown ? reticle!.Modulate.A : 0f):F2} reticle={(shown ? reticleRect.ToString() : "none")}");
        _report.Check(step + "_reticle_fits_item", fits, $"reticle={reticleRect} item={itemRect}");
    }

    /// <summary>Waits for a deselected reticle's 0.2 s fade and its free; false when one is still there.</summary>
    private async Task<bool> AwaitReticleGoneAsync(Control item, string what)
    {
        try
        {
            await WaitUntilAsync(() => ReticleNode(item, activeOnly: false) is null, what, ShortWait);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static readonly FieldInfo? TipOwnerField =
        typeof(NHoverTipSet).GetField("_owner", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? TipTextField =
        typeof(NHoverTipSet).GetField("_textHoverTipContainer", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? TipCardsField =
        typeof(NHoverTipSet).GetField("_cardHoverTipContainer", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>Called by <c>LauncherHostRunner</c> for <c>--sts2mobile-test=select-confirm</c>.</summary>
    internal static async Task RunLauncherSelectConfirmAsync(TestOptions options, TestReport report,
        ScreenshotService screenshots, VirtualPointer pointer)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Log.Error("Launcher select-confirm requires a SceneTree.");
            return;
        }
        await new TestDriver(tree, options, report, screenshots, pointer).RunLauncherSelectConfirmInternalAsync();
    }

    private async Task RunLauncherSelectConfirmInternalAsync()
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            _report.Fail("select-confirm_portrait", $"select-confirm needs a portrait window; window={DisplayServer.WindowGetSize()} " +
                                                    $"canvas={GetViewportSize()}");
            return;
        }
        if (!PhoneSelectConfirm.Installed)
        {
            _report.Fail("select-confirm_installed", "PhoneSelectConfirm is not installed (its reflection over the game failed)");
            return;
        }

        await WaitForMainMenuPreloadAsync();
        RunState state = await StartIroncladRunAsync();
        Player player = LocalContext.GetMe(state) ?? throw new InvalidOperationException("No local player in the run.");
        player.Creature.SetMaxHpInternal(9999);
        player.Creature.SetCurrentHpInternal(9999);
        _report.Pass("select-confirm_wiring", $"PhoneSelectConfirm installed; portrait={SafeAreaCanvas.IsPortrait}");

        await RunSelectConfirmPhaseAsync("card_reward", () => CheckCardRewardSelectConfirmAsync(state, player));
        await RunSelectConfirmPhaseAsync("choose_card", () => CheckChooseACardSelectConfirmAsync(state, player));
        await RunSelectConfirmPhaseAsync("relic_choice", CheckRelicChoiceSelectConfirmAsync);
        await RunSelectConfirmPhaseAsync("treasure", () => CheckTreasureSelectConfirmAsync(player));
        await RunSelectConfirmPhaseAsync("orientation", () => CheckOrientationSelectConfirmAsync(player));
        _report.Pass("select-confirm_complete",
            "card reward, choose-a-card, relic choice, treasure and the portrait/landscape round trip ran");
    }

    /// <summary>
    /// Runs one phase; a phase that fails (or times out) is reported as its own <c>select-confirm_…_exception</c> step and
    /// the next one still runs from portrait with nothing on top. The portrait-leave phase wrapper cannot be reused: it
    /// names its failures for that scenario.
    /// </summary>
    private async Task RunSelectConfirmPhaseAsync(string phase, Func<Task> run)
    {
        try
        {
            if (!SafeAreaCanvas.IsPortrait) SetPortraitMode(true);
            ClearOverlays();
            await WaitFramesAsync(2);
            await run();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            _report.Fail($"select-confirm_{phase}_exception", error.GetBaseException().Message);
        }
    }

    // ---- 1. Card reward -----------------------------------------------------------------------------------------------

    private async Task CheckCardRewardSelectConfirmAsync(RunState state, Player player)
    {
        await EnterTouchCombatAsync(state, "select-confirm");
        var rewards = new RewardsSet(player);
        if (state.CurrentRoom is { } room) rewards.EmptyForRoom(room);
        var cardOptions = CardCreationOptions.ForRoom(player, RoomType.Monster);
        var cardReward = new CardReward(cardOptions, 3, player) { CanReroll = true };
        cardReward.Populate();
        rewards.WithCustomRewards(new List<Reward> { new GoldReward(120, player), cardReward });

        NOverlayStack.Instance?.Clear();
        await WaitFramesAsync(4);
        // The set outlives this phase (its gold row is never taken), and clearing the overlay before it ends cancels the
        // card reward's pending answer; observe the fault so a deliberate fixture teardown is not reported as an
        // unobserved task exception by the crash guard.
        ObserveFixtureTask(RunManager.Instance.RewardsSetSynchronizer.BeginRewardsSet(rewards), "card reward loot set");
        NRewardsScreen? loot = NRewardsScreen.ShowScreen(rewards, isTerminal: true, state);
        if (loot is null)
        {
            _report.Fail("select-confirm_card_reward_loot", "NRewardsScreen.ShowScreen returned null");
            return;
        }
        await WaitUntilAsync(() => loot.IsInsideTree() && loot.Visible, "loot list", MediumWait);
        await WaitSecondsAsync(1.0);

        List<NRewardButton> rows = FindAll<NRewardButton>(loot);
        bool hasCardRow = rows.Any(b => b.Reward is CardReward);
        _report.Check("select-confirm_card_reward_row", rows.Count >= 2 && hasCardRow,
            $"rows={rows.Count} cardRewardRow={hasCardRow}");
        if (rows.Count < 2 || !hasCardRow)
        {
            _report.Fail("select-confirm_card_reward_row", $"the loot list has no card reward row ({rows.Count} rows)");
            return;
        }

        NCardRewardSelectionScreen? screen = await OpenCardRewardAsync(loot);
        if (screen is null) return;
        // The loot reward's own loop already awaits the screen's OptionSelected(); asking again would install a second
        // source and abandon the game's, so the take is observed through the deck and the closing screen instead.
        await WaitSecondsAsync(0.7);
        List<NGridCardHolder> holders = FindAll<NGridCardHolder>(screen)
            .Where(h => GodotObject.IsInstanceValid(h) && h.IsVisibleInTree() && !h.IsQueuedForDeletion()).ToList();
        _report.Check("select-confirm_card_reward_opens", holders.Count == 3, $"cards={holders.Count}");
        if (holders.Count < 3) return;

        int deckBefore = player.Deck.Cards.Count;
        int confirms = PhoneSelectConfirm.Confirms;
        int clears = PhoneSelectConfirm.Clears;
        _report.Check("select-confirm_card_reward_no_choose_button",
            screen.GetNodeOrNull<Control>("PhoneChooseButton") is null,
            "the card reward screen carries no Choose button");

        // First press: selects, tips, takes nothing.
        await _pointer.TapAsync(CenterOf(holders[0].Hitbox));
        await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, holders[0]),
            "card reward first tap selects", ShortWait);
        _report.Check("select-confirm_card_reward_first_tap_only_selects",
            PlayerDeck(player) == deckBefore && screen.IsVisibleInTree() &&
            ReticleNode(holders[0]) is not null &&
            PhoneSelectConfirm.SelectedName == holders[0].Name.ToString(),
            $"deck={PlayerDeck(player)}/{deckBefore} screen={screen.IsVisibleInTree()} " +
            $"selected={PhoneSelectConfirm.SelectedName ?? "none"}");
        await CheckReticleAsync(holders[0], holders[0].Hitbox, "select-confirm_card_reward");
        _report.Check("select-confirm_card_reward_tip_on_select", TipShownUnder(holders[0]),
            "the selected card shows its native keyword tips");
        await CaptureAsync("select-confirm_card_reward_selected");

        // A press on another card moves the selection.
        await _pointer.TapAsync(CenterOf(holders[2].Hitbox));
        await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, holders[2]),
            "card reward selection moves", ShortWait);
        _report.Check("select-confirm_card_reward_moves",
            PhoneSelectConfirm.SelectedName == holders[2].Name.ToString() && PlayerDeck(player) == deckBefore
            && PhoneSelectConfirm.Confirms == confirms && PhoneSelectConfirm.Clears == clears + 1,
            $"selected={PhoneSelectConfirm.SelectedName ?? "none"} deck={PlayerDeck(player)} confirms={PhoneSelectConfirm.Confirms} " +
            $"clears={PhoneSelectConfirm.Clears}");
        bool oldCardFreed = await AwaitReticleGoneAsync(holders[0], "the first card's reticle fades and is freed");
        _report.Check("select-confirm_card_reward_reticle_follows",
            oldCardFreed && ReticleNode(holders[2]) is not null,
            $"oldCardReticle={ReticleNode(holders[0], activeOnly: false)?.Name.ToString() ?? "none"} " +
            $"newCardReticle={ReticleNode(holders[2])?.Name.ToString() ?? "none"}");
        await CaptureAsync("select-confirm_card_reward_moved");

        // A press elsewhere lets it go.
        await _pointer.TapAsync(EmptyPoint());
        await WaitUntilAsync(() => PhoneSelectConfirm.SelectedOwner is null, "card reward press elsewhere clears", ShortWait);
        _report.Check("select-confirm_card_reward_clears",
            screen.IsVisibleInTree() && PhoneSelectConfirm.SelectedName is null && PlayerDeck(player) == deckBefore,
            $"selected={PhoneSelectConfirm.SelectedName ?? "none"} deck={PlayerDeck(player)} confirms={PhoneSelectConfirm.Confirms}");
        await CaptureAsync("select-confirm_card_reward_cleared");

        // A hold opens the native inspection and never confirms.
        await _pointer.TapAsync(CenterOf(holders[0].Hitbox));
        await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, holders[0]),
            "card reward selected for the hold", ShortWait);
        int holds = PhoneSelectConfirm.HoldInspects;
        var (holdInspects, cardInspected, _) = await HoldAsync(CenterOf(holders[0].Hitbox),
            "select-confirm_card_reward_hold_inspect");
        _report.Check("select-confirm_card_reward_hold_inspects",
            holdInspects == holds + 1 && cardInspected && PhoneSelectConfirm.Confirms == confirms
            && PlayerDeck(player) == deckBefore,
            $"holdInspects={holds}->{holdInspects} inspectScreen={cardInspected} " +
            $"confirms={PhoneSelectConfirm.Confirms} deck={PlayerDeck(player)}");
        await CloseInspectScreensAsync();
        _report.Check("select-confirm_card_reward_hold_takes_nothing",
            PhoneSelectConfirm.Confirms == confirms && PlayerDeck(player) == deckBefore && screen.IsVisibleInTree(),
            "the closed inspection returns to the still-open reward with nothing taken");

        // A swipe from the selected item takes nothing. The hold left the card selected; let it go first, or the next tap
        // on the same card would be the second tap.
        await _pointer.TapAsync(EmptyPoint());
        await WaitUntilAsync(() => PhoneSelectConfirm.SelectedOwner is null, "the held card's selection lets go", ShortWait);
        await _pointer.TapAsync(CenterOf(holders[0].Hitbox));
        await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, holders[0]),
            "card reward selected for the swipe", ShortWait);
        Rect2 card = holders[0].Hitbox.GetGlobalRect();
        await _pointer.DragAsync(CenterOf(holders[0].Hitbox), card.End + new Vector2(card.Size.X, 40f), 14);
        await WaitFramesAsync(6);
        _report.Check("select-confirm_card_reward_swipe_takes_nothing",
            PhoneSelectConfirm.Confirms == confirms && PlayerDeck(player) == deckBefore && screen.IsVisibleInTree(),
            $"confirms={PhoneSelectConfirm.Confirms} deck={PlayerDeck(player)}");
        await CaptureAsync("select-confirm_card_reward_swiped");
        await _pointer.TapAsync(EmptyPoint());
        await WaitFramesAsync(3);

        // Reroll rebuilds the cards and lets the old selection go; Skip still answers the reward.
        List<NCardRewardAlternativeButton> alternatives = FindAll<NCardRewardAlternativeButton>(screen);
        if (alternatives.Count != 2)
        {
            _report.Fail("select-confirm_card_reward_reroll_available", $"alternatives={alternatives.Count} (expected Skip and Reroll)");
            return;
        }
        await _pointer.TapAsync(CenterOf(alternatives[1]));
        await WaitUntilAsync(() => !cardReward.CanReroll && FindAll<NCardRewardAlternativeButton>(screen).Count == 1,
            "native reward rerolls in place", ShortWait);
        await WaitSecondsAsync(0.5);
        _report.Check("select-confirm_card_reward_reroll_clears",
            PhoneSelectConfirm.SelectedName is null && PhoneSelectConfirm.Confirms == confirms && PlayerDeck(player) == deckBefore
            && !ReticleSelectedUnder(screen),
            $"selected={PhoneSelectConfirm.SelectedName ?? "none"} confirms={PhoneSelectConfirm.Confirms} deck={PlayerDeck(player)}");
        await CaptureAsync("select-confirm_card_reward_rerolled");

        NCardRewardAlternativeButton skip = FindAll<NCardRewardAlternativeButton>(screen).First();
        await _pointer.TapAsync(CenterOf(skip));
        await WaitUntilAsync(() => !ScreenAlive(screen) || !screen.IsVisibleInTree(),
            "card reward Skip closes the screen", ShortWait);
        // Evidence that the list a player sees is tappable again: how long the guard keeps the visible rows closed.
        (double sinceReturn, double sinceRowsStopped, bool reopened, string rowLayout) = await MeasureRewardListReopenAsync(loot);
        _report.Check("select-confirm_reward_list_reopens_quickly", reopened && sinceRowsStopped <= 1000,
            $"the loot rows took touches again {(reopened ? "yes" : "never")} {sinceReturn:F0} ms after the card screen closed and " +
            $"{sinceRowsStopped:F0} ms after they stopped moving (limit 1000 ms); rows={rowLayout}");
        await WaitSecondsAsync(0.2);
        _report.Check("select-confirm_card_reward_skip",
            !ScreenAlive(screen) && PlayerDeck(player) == deckBefore && loot.IsVisibleInTree(),
            $"Skip takes no card (deck {PlayerDeck(player)}) and returns to the loot list " +
            $"(screen alive={ScreenAlive(screen)}, loot visible={loot.IsVisibleInTree()})");
        await CaptureAsync("select-confirm_card_reward_skipped");

        // The second tap takes exactly the intended card, once.
        screen = await OpenCardRewardAsync(loot);
        if (screen is null) return;
        await WaitSecondsAsync(0.7);
        holders = FindAll<NGridCardHolder>(screen)
            .Where(h => GodotObject.IsInstanceValid(h) && h.IsVisibleInTree() && !h.IsQueuedForDeletion()).ToList();
        if (holders.Count < 2) return;
        NGridCardHolder target = holders[1];
        CardModel expected = target.CardModel ?? throw new InvalidOperationException("reward holder has no card model");
        string expectedId = expected.Id.ToString();
        int deckSecond = player.Deck.Cards.Count;
        int sameBefore = player.Deck.Cards.Count(c => c.Id.ToString() == expectedId);
        int confirmsSecond = PhoneSelectConfirm.Confirms;
        await _pointer.TapAsync(CenterOf(target.Hitbox));
        await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, target), "reward card selected", ShortWait);
        await CaptureAsync("select-confirm_card_reward_second_pick_selected");
        await _pointer.TapAsync(CenterOf(target.Hitbox));
        try
        {
            await WaitUntilAsync(() => PlayerDeck(player) > deckSecond, "the taken card is added to the deck", ShortWait);
        }
        catch (TimeoutException)
        {
            // Reported below with the observed deck size and selection state.
        }
        await WaitFramesAsync(8);
        bool closed = !ScreenAlive(screen) || !screen.IsVisibleInTree();
        int sameAfter = player.Deck.Cards.Count(c => c.Id.ToString() == expectedId);
        _report.Check("select-confirm_card_reward_second_tap_takes",
            PlayerDeck(player) == deckSecond + 1 && sameAfter == sameBefore + 1 && closed &&
            PhoneSelectConfirm.Confirms == confirmsSecond + 1,
            $"deck={deckSecond}->{PlayerDeck(player)} added={expectedId} count={sameBefore}->{sameAfter} " +
            $"screenClosed={closed} confirms={confirmsSecond}->{PhoneSelectConfirm.Confirms}");
        await CaptureAsync("select-confirm_card_reward_taken");
        await WaitSecondsAsync(0.8);
        _report.Check("select-confirm_card_reward_takes_once", PlayerDeck(player) == deckSecond + 1,
            $"deck={PlayerDeck(player)} after the reward settled");

        _report.Pass("select-confirm_card_reward_done",
            $"selected/moved/cleared, held, swiped, rerolled, skipped and took {expected.Id} with a second tap");
    }

    /// <summary>
    /// Taps the loot list's card row and waits for the reward screen. The tap guard takes touches away from the rows while
    /// they fade in or move (it did so for longer than the fixed settle after the card screen returned, which is exactly
    /// the row it protects), so the row is only tapped once the guard says it is open, and a tap that still raced with a
    /// layout change is retried once. The row is looked up by its reward each attempt: the list may rebuild its rows.
    /// </summary>
    private async Task<NCardRewardSelectionScreen?> OpenCardRewardAsync(NRewardsScreen loot)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                // IsOpen is exactly "tracked and no row is held back" in PhoneRewardTapGuard (Blocked.Count == 0); the
                // row's own MouseFilter is the same state read directly, in case the guard is not tracking this screen.
                await WaitUntilAsync(() => PhoneRewardTapGuard.IsOpen(loot) || CardRewardRowAcceptsTouches(loot),
                    "the loot rows take touches", ShortWait);
            }
            catch (TimeoutException)
            {
                _report.Fail("select-confirm_card_reward_opens",
                    $"the loot rows stayed guarded (guard open={PhoneRewardTapGuard.IsOpen(loot)}, " +
                    $"rowFilter={CardRewardRowFilter(loot)})");
                return null;
            }
            NRewardButton? row = FindAll<NRewardButton>(loot).FirstOrDefault(b => b.Reward is CardReward);
            if (row is null || !GodotObject.IsInstanceValid(row) || !row.IsVisibleInTree())
            {
                _report.Fail("select-confirm_card_reward_opens", "the loot list has no visible card reward row");
                return null;
            }
            await _pointer.TapAsync(CenterOf(row));
            try
            {
                await WaitUntilAsync(() => FindAll<NCardRewardSelectionScreen>(_tree.Root).Any(s => s.IsVisibleInTree()),
                    "card reward selection", ShortWait);
                await WaitSecondsAsync(0.8);
                // Done: the screen is up. Do not loop again — the next attempt would wait on the loot rows, which are
                // hidden under the reward screen, and its guard keeps them closed for as long as that screen is open.
                return FindAll<NCardRewardSelectionScreen>(_tree.Root).Last(s => s.IsVisibleInTree());
            }
            catch (TimeoutException)
            {
                if (attempt == 2)
                {
                    NRewardButton? last = FindAll<NRewardButton>(loot).FirstOrDefault(b => b.Reward is CardReward);
                    _report.Fail("select-confirm_card_reward_opens",
                        "tapping the card row did not open the reward screen " +
                        $"(guard open={PhoneRewardTapGuard.IsOpen(loot)}, " +
                        $"row={(last is null ? "gone" : CenterOf(last).ToString())}, " +
                        $"rowVisible={(last is not null && last.IsVisibleInTree())})");
                    return null;
                }
                await WaitSecondsAsync(0.4);
            }
        }
        return null;
    }

    // ---- 2. Choose a card (Attack Potion) ------------------------------------------------------------------------------

    private async Task CheckChooseACardSelectConfirmAsync(RunState state, Player player)
    {
        SaveManager.Instance.MarkFtueAsComplete("obtain_potion_ftue");
        await EnterTouchCombatAsync(state, "select-confirm");
        var attack = ModelDb.Potion<AttackPotion>().ToMutable();
        player.AddPotionInternal(attack, 0);
        await WaitSecondsAsync(0.65);
        var belt = NRun.Instance!.GlobalUi.TopBar.PotionContainer.Holders;
        NCombatRoom room = NCombatRoom.Instance ?? throw new InvalidOperationException("Combat room disappeared.");
        var popup = await TapPotionAsync(belt[0], "select-confirm_choose_card");
        await _pointer.TapAsync(CenterOf(Action(popup, "Use")));
        await WaitUntilAsync(() => FindAll<NChooseACardSelectionScreen>(_tree.Root).Any(s => s.IsVisibleInTree()),
            "Attack Potion card picker", ShortWait);
        await WaitSecondsAsync(0.8);
        NChooseACardSelectionScreen screen = FindAll<NChooseACardSelectionScreen>(_tree.Root).Single(s => s.IsVisibleInTree());
        List<NGridCardHolder> holders = FindAll<NGridCardHolder>(screen)
            .Where(h => GodotObject.IsInstanceValid(h) && h.IsVisibleInTree() && !h.IsQueuedForDeletion()).ToList();
        _report.Check("select-confirm_choose_card_opens", holders.Count == 3, $"cards={holders.Count}");
        if (holders.Count < 3) return;

        int handBefore = room.Ui.Hand.ActiveHolders.Count;
        int confirms = PhoneSelectConfirm.Confirms;
        int clears = PhoneSelectConfirm.Clears;
        _report.Check("select-confirm_choose_card_no_choose_button",
            screen.GetNodeOrNull<Control>("PhoneChooseButton") is null, "the picker carries no Choose button");

        await _pointer.TapAsync(CenterOf(holders[0].Hitbox));
        await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, holders[0]),
            "choose-a-card first tap selects", ShortWait);
        _report.Check("select-confirm_choose_card_first_tap_only_selects",
            room.Ui.Hand.ActiveHolders.Count == handBefore && screen.IsVisibleInTree() &&
            ReticleNode(holders[0]) is not null && PhoneSelectConfirm.Confirms == confirms,
            $"hand={room.Ui.Hand.ActiveHolders.Count}/{handBefore} selected={PhoneSelectConfirm.SelectedName ?? "none"} " +
            $"confirms={PhoneSelectConfirm.Confirms}");
        await CheckReticleAsync(holders[0], holders[0].Hitbox, "select-confirm_choose_card");
        _report.Check("select-confirm_choose_card_tip_on_select", TipShownUnder(holders[0]),
            "the selected card shows its native keyword tips");
        await CaptureAsync("select-confirm_choose_card_selected");

        // Peek still works; the hidden row lets the selection go (the item is no longer shown) and comes back. Its eye is
        // reachable off the piles, and while peeking the picker's own copy of the three piles must stay on the portrait rail
        // instead of reappearing at its desktop corners.
        var peek = screen.GetNode<NPeekButton>("%PeekButton");
        Rect2 eyeRect = peek.GetGlobalRect();
        _report.Check("select-confirm_choose_card_peek_shown",
            peek.IsVisibleInTree() && peek.IsEnabled && GetViewportRect().Encloses(eyeRect) &&
            eyeRect.Size.X >= PortraitBands.MinTarget && eyeRect.Size.Y >= PortraitBands.MinTarget,
            $"peek={eyeRect} visible={peek.IsVisibleInTree()} enabled={peek.IsEnabled} canvas={GetViewportRect()}");
        _report.Check("select-confirm_choose_card_peek_clear_of_piles", EyeClearOfPiles(screen, eyeRect),
            EyePileDetail(screen, eyeRect));
        if (peek.IsEnabled)
        {
            await _pointer.TapAsync(CenterOf(peek));
            await WaitUntilAsync(() => peek.IsPeeking && !screen.GetNode<Control>("CardRow").Visible, "Peek hides the cards", ShortWait);
            await WaitFramesAsync(5);
            bool railPiles = ChoicePilesOnRail(screen, out string pileDetail);
            _report.Check("select-confirm_choose_card_peek_piles_portrait", railPiles, pileDetail);
            eyeRect = peek.GetGlobalRect();
            _report.Check("select-confirm_choose_card_peek_eye_clear_while_peeking", EyeClearOfPiles(screen, eyeRect),
                EyePileDetail(screen, eyeRect));
            await CaptureAsync("select-confirm_choose_card_peek_hidden");
            await _pointer.TapAsync(CenterOf(peek));
            await WaitUntilAsync(() => !peek.IsPeeking && screen.GetNode<Control>("CardRow").Visible, "Peek shows the cards again", ShortWait);
            _report.Pass("select-confirm_choose_card_peek", "Peek hides and restores the card row; nothing is taken");
        }
        else
        {
            _report.Skip("select-confirm_choose_card_peek", "Peek is disabled on this picker");
        }
        await WaitFramesAsync(4);

        // Press elsewhere clears.
        await _pointer.TapAsync(CenterOf(holders[0].Hitbox));
        await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, holders[0]), "picker card selected", ShortWait);
        await _pointer.TapAsync(EmptyPoint());
        await WaitUntilAsync(() => PhoneSelectConfirm.SelectedOwner is null, "picker press elsewhere clears", ShortWait);
        _report.Check("select-confirm_choose_card_clears",
            PhoneSelectConfirm.SelectedName is null && PhoneSelectConfirm.Confirms == confirms
            && room.Ui.Hand.ActiveHolders.Count == handBefore && screen.IsVisibleInTree(),
            $"selected={PhoneSelectConfirm.SelectedName ?? "none"} hand={room.Ui.Hand.ActiveHolders.Count}");
        await CaptureAsync("select-confirm_choose_card_cleared");

        // Hold opens the native inspection, not the pick.
        await _pointer.TapAsync(CenterOf(holders[0].Hitbox));
        await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, holders[0]), "picker card selected", ShortWait);
        int holds = PhoneSelectConfirm.HoldInspects;
        var (holdInspects, cardInspected, _) = await HoldAsync(CenterOf(holders[0].Hitbox),
            "select-confirm_choose_card_hold_inspect");
        _report.Check("select-confirm_choose_card_hold_inspects",
            holdInspects == holds + 1 && cardInspected && PhoneSelectConfirm.Confirms == confirms
            && room.Ui.Hand.ActiveHolders.Count == handBefore,
            $"holdInspects={holds}->{holdInspects} inspectScreen={cardInspected} " +
            $"hand={room.Ui.Hand.ActiveHolders.Count}");
        await CloseInspectScreensAsync();

        // A swipe takes nothing. The hold left the card selected; let it go first, or the next tap would be the second tap.
        await _pointer.TapAsync(EmptyPoint());
        await WaitUntilAsync(() => PhoneSelectConfirm.SelectedOwner is null, "the held card's selection lets go", ShortWait);
        await _pointer.TapAsync(CenterOf(holders[0].Hitbox));
        await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, holders[0]), "picker card selected", ShortWait);
        Rect2 rect = holders[0].Hitbox.GetGlobalRect();
        await _pointer.DragAsync(CenterOf(holders[0].Hitbox), rect.End + new Vector2(rect.Size.X, 60f), 14);
        await WaitFramesAsync(6);
        _report.Check("select-confirm_choose_card_swipe_takes_nothing",
            PhoneSelectConfirm.Confirms == confirms && room.Ui.Hand.ActiveHolders.Count == handBefore && screen.IsVisibleInTree(),
            $"confirms={PhoneSelectConfirm.Confirms} hand={room.Ui.Hand.ActiveHolders.Count}");
        await CaptureAsync("select-confirm_choose_card_swiped");
        await _pointer.TapAsync(EmptyPoint());
        await WaitFramesAsync(3);

        // The second tap takes exactly one card.
        CardModel expected = holders[0].CardModel ?? throw new InvalidOperationException("picker holder has no card model");
        await _pointer.TapAsync(CenterOf(holders[0].Hitbox));
        await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, holders[0]), "picker card selected", ShortWait);
        await _pointer.TapAsync(CenterOf(holders[0].Hitbox));
        await WaitUntilAsync(() => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree(),
            "picker second tap takes the card", ShortWait);
        await WaitUntilAsync(() => room.Ui.Hand.ActiveHolders.Count == handBefore + 1, "picked card arrives in hand", ShortWait);
        _report.Check("select-confirm_choose_card_second_tap_takes",
            room.Ui.Hand.ActiveHolders.Count == handBefore + 1 && PhoneSelectConfirm.Confirms == confirms + 1 &&
            !player.Potions.Contains(attack),
            $"hand={handBefore}->{room.Ui.Hand.ActiveHolders.Count} confirms={PhoneSelectConfirm.Confirms} card={expected.Id}");
        await CaptureAsync("select-confirm_choose_card_taken");
        await WaitSecondsAsync(0.6);
        _report.Check("select-confirm_choose_card_takes_once", room.Ui.Hand.ActiveHolders.Count == handBefore + 1,
            $"hand={room.Ui.Hand.ActiveHolders.Count} after the pick settled");
        _report.Pass("select-confirm_choose_card_done", $"took {expected.Id} with a second tap; Peek, hold and swipe safe");

        // The inspect-card screen is shared by the in-hand hold and by the decks, grids and rewards. Its keyword tips and
        // card previews must never cover the inspected card, and its View Upgrades box must sit where the deck places it
        // whichever path opened it.
        await CheckHandInspectUpgradeAsync(room);
        await CheckInspectTipsAsync();
        await CheckCombatPileSelectPeekAsync(player);
    }

    // ---- Inspect-card screen (tips, previews, View Upgrades) ----------------------------------------------------------

    /// <summary>
    /// The in-hand long press opens the same inspect screen as the deck. The View Upgrades box must be the deck/grid
    /// placement — centred under the card, 18 canvas units below it, 44 points tall — and not the old left-of-card spot
    /// over the card's cost badge.
    /// </summary>
    private async Task CheckHandInspectUpgradeAsync(NCombatRoom room)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        try
        {
            await WaitUntilStableAsync(() => IsPlayerPlayPhase() ? CurrentHandSettledSignature() : null,
                "select-confirm inspect hand settled", ShortWait);
        }
        catch (TimeoutException)
        {
            // The hold itself checks whether the inspector opened; a moving hand is reported there.
        }
        NPlayerHand hand = room.Ui.Hand;
        NHandCardHolder? holder = hand.ActiveHolders.FirstOrDefault(h =>
            GodotObject.IsInstanceValid(h) && h.IsVisibleInTree() && h.CardModel != null);
        if (holder is null)
        {
            _report.Skip("select-confirm_hand_inspect_upgrade_under_card", "the hand is empty");
            return;
        }
        int before = PhoneTouchCards.InspectsOpened;
        Vector2 point = CenterOf(holder.Hitbox);
        await _pointer.PressAsync(point);
        bool openedWhileDown = true;
        try
        {
            await WaitUntilAsync(() => PhoneTouchCards.InspectsOpened > before,
                "the hand hold opens the inspector", MediumWait);
        }
        catch (TimeoutException)
        {
            openedWhileDown = false;
        }
        await _pointer.ReleaseAsync(point);
        NInspectCardScreen? screen = NGame.Instance?.GetInspectCardScreen();
        bool open = openedWhileDown && screen is not null && screen.IsVisibleInTree();
        _report.Check("select-confirm_hand_inspect_opens", open,
            $"inspected={PhoneTouchCards.InspectsOpened - before} screen={(screen is null ? "none" : screen.IsVisibleInTree().ToString())}");
        if (!open || screen is null)
        {
            await CloseInspectScreensAsync();
            return;
        }
        await WaitForInspectorCardAsync(screen);
        await WaitFramesAsync(4);
        (Rect2 cardRect, Rect2 upgradeRect) = InspectCardAndUpgrade(screen);
        CheckInspectUpgradePlacement("select-confirm_hand_inspect_upgrade_under_card", cardRect, upgradeRect);
        await CaptureAsync("select-confirm_hand_inspect_portrait");
        await CloseInspectScreensAsync();
        await WaitSecondsAsync(0.4);

        // The same card opened straight from the override (the deck/grid path) must place its box at the same offset:
        // that is what "like the deck inspection" means here, not a different combat-only rule.
        NInspectCardScreen direct = NGame.Instance!.GetInspectCardScreen();
        direct.Open(new List<CardModel> { holder.CardModel! }, 0);
        try
        {
            await WaitUntilAsync(() => direct.IsVisibleInTree(), "the direct inspection opens", ShortWait);
            await WaitForInspectorCardAsync(direct);
            await WaitFramesAsync(4);
            (Rect2 directCard, Rect2 directUpgrade) = InspectCardAndUpgrade(direct);
            bool matches = Math.Abs((directUpgrade.Position.Y - directCard.End.Y) -
                                    (upgradeRect.Position.Y - cardRect.End.Y)) <= 1.5f &&
                           Math.Abs(directUpgrade.GetCenter().X - directCard.GetCenter().X -
                                    (upgradeRect.GetCenter().X - cardRect.GetCenter().X)) <= 1.5f;
            _report.Check("select-confirm_hand_inspect_matches_deck_layout", matches,
                $"hand offset=({upgradeRect.Position.Y - cardRect.End.Y:F1},{upgradeRect.GetCenter().X - cardRect.GetCenter().X:F1}) " +
                $"deck offset=({directUpgrade.Position.Y - directCard.End.Y:F1},{directUpgrade.GetCenter().X - directCard.GetCenter().X:F1})");
        }
        finally
        {
            direct.Close();
        }
        await CloseInspectScreensAsync();
        await WaitFramesAsync(4);
    }

    private void CheckInspectUpgradePlacement(string name, Rect2 cardRect, Rect2 upgradeRect)
    {
        bool centred = Math.Abs(upgradeRect.GetCenter().X - cardRect.GetCenter().X) <= 2f;
        bool below = upgradeRect.Position.Y >= cardRect.End.Y - 0.5f && upgradeRect.Position.Y <= cardRect.End.Y + 40f;
        bool tall = upgradeRect.Size.Y >= PortraitBands.MinTarget - 1f;
        bool clear = !upgradeRect.Intersects(cardRect);
        _report.Check(name, centred && below && tall && clear,
            $"card={cardRect} upgrade={upgradeRect} centred={centred} below={below} tall={tall} clear={clear}");
    }

    private static (Rect2 Card, Rect2 Upgrade) InspectCardAndUpgrade(NInspectCardScreen screen)
    {
        NCard card = screen.GetNode<NCard>("Card");
        Vector2 size = card.GetCurrentSize();
        var cardRect = new Rect2(card.GlobalPosition - size * 0.5f, size);
        Control upgrade = screen.GetNode<Control>("%Upgrade");
        return (cardRect, upgrade.GetGlobalRect());
    }

    /// <summary>
    /// Opens the inspect screen on a card with many keyword tips plus a card preview (Growth) and on one with only many
    /// keyword tips (Big Bang), and checks no tip or preview block intersects the inspected card.
    /// </summary>
    private async Task CheckInspectTipsAsync()
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        CardModel? growth = ModelDb.AllCards.FirstOrDefault(c => c.GetType().Name == "Growth");
        CardModel? bigBang = ModelDb.AllCards.FirstOrDefault(c => c.GetType().Name == "BigBang");
        if (growth is null || bigBang is null)
        {
            _report.Fail("select-confirm_inspect_tips_models", "Growth/BigBang are missing from ModelDb.AllCards");
            return;
        }
        NInspectCardScreen screen = NGame.Instance!.GetInspectCardScreen();
        var cards = new List<CardModel> { growth.ToMutable(), bigBang.ToMutable() };
        screen.Open(cards, 0);
        await WaitUntilAsync(() => screen.IsVisibleInTree(), "the Growth inspection opens", ShortWait);
        await WaitSecondsAsync(0.5);
        for (int frame = 0; frame < 60; frame++)
        {
            (Control? textColumn, Control? cardColumn) = TipColumns(screen);
            if (textColumn != null && cardColumn is { } previews &&
                previews.GetChildren().OfType<Control>().Any(c => c.IsVisibleInTree()))
                break;
            await WaitFramesAsync(1);
        }
        await WaitFramesAsync(2);
        CheckInspectTipsCase(screen, "select-confirm_inspect_growth", expectPreview: true, minTextTips: 1);
        await CaptureAsync("select-confirm_inspect_growth");

        screen.Open(cards, 1);
        await WaitUntilAsync(() => screen.IsVisibleInTree(), "the Big Bang inspection opens", ShortWait);
        await WaitSecondsAsync(0.4);
        await WaitFramesAsync(3);
        // Big Bang's Forge tip carries a card preview too (the forged Blade), so both blocks are expected; its four text
        // tips are the case the old vertical column made taller than the space above the card.
        CheckInspectTipsCase(screen, "select-confirm_inspect_bigbang", expectPreview: true, minTextTips: 3);
        await CaptureAsync("select-confirm_inspect_bigbang");
        screen.Close();
        await CloseInspectScreensAsync();
        await WaitFramesAsync(4);
    }

    private void CheckInspectTipsCase(NInspectCardScreen screen, string name, bool expectPreview, int minTextTips)
    {
        NCard card = screen.GetNode<NCard>("Card");
        Vector2 size = card.GetCurrentSize();
        var cardRect = new Rect2(card.GlobalPosition - size * 0.5f, size);
        (Control? textColumn, Control? cardColumn) = TipColumns(screen);
        Rect2? text = TipUnion(textColumn);
        Rect2? preview = TipUnion(cardColumn);
        int textTips = textColumn?.GetChildren().OfType<Control>().Count(c => c.IsVisibleInTree()) ?? 0;
        int previews = cardColumn?.GetChildren().OfType<Control>().Count(c => c.IsVisibleInTree()) ?? 0;
        bool textClear = text is not { } t || !t.Intersects(cardRect);
        bool previewClear = preview is not { } p || !p.Intersects(cardRect);
        bool apart = text is not { } a || preview is not { } b || !a.Intersects(b);
        Rect2 canvas = GetViewportRect();
        bool inside = (text is not { } tt || canvas.Encloses(tt)) && (preview is not { } pp || canvas.Encloses(pp));
        _report.Check(name + "_tips_clear_of_card", textClear && previewClear && apart,
            $"card={cardRect} text={text} preview={preview} textClear={textClear} previewClear={previewClear} apart={apart}");
        _report.Check(name + "_tips_inside_canvas", inside,
            $"canvas={canvas} text={text} preview={preview}");
        _report.Check(name + "_tips_present", textTips >= minTextTips && (previews > 0) == expectPreview,
            $"textTips={textTips} previewCards={previews} expectedPreview={expectPreview}");
    }

    private static (Control? Text, Control? Cards) TipColumns(Control owner)
    {
        if (NGame.Instance?.HoverTipsContainer is not Node container) return (null, null);
        foreach (Node child in container.GetChildren())
        {
            if (child is not NHoverTipSet set || !ReferenceEquals(TipOwnerField?.GetValue(set), owner)) continue;
            return (TipTextField?.GetValue(set) as Control, TipCardsField?.GetValue(set) as Control);
        }
        return (null, null);
    }

    private static Rect2? TipUnion(Control? column)
    {
        if (column is null || !GodotObject.IsInstanceValid(column)) return null;
        Rect2? union = null;
        foreach (Node child in column.GetChildren())
        {
            if (child is not Control tip || !tip.IsVisibleInTree()) continue;
            Rect2 rect = tip.GetGlobalRect();
            if (rect.Size.X < 1f || rect.Size.Y < 1f) continue;
            union = union is { } present ? present.Merge(rect) : rect;
        }
        return union;
    }

    // ---- A choice screen's own combat piles while the eye is pressed ------------------------------------------------------

    /// <summary>
    /// The combat-pile grid from a fight: its eye shows, its eye is clear of the piles, and pressing it puts the screen's
    /// own draw/discard/exhaust copies on the portrait rail instead of their desktop corners.
    /// </summary>
    private async Task CheckCombatPileSelectPeekAsync(Player player)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        var prefs = new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, 1);
        NCombatPileCardSelectScreen? screen = NCombatPileCardSelectScreen.Create(PileType.Draw.GetPile(player), prefs, null);
        if (screen is null)
        {
            _report.Fail("select-confirm_pile_select_peek_shown", "NCombatPileCardSelectScreen.Create returned null");
            return;
        }
        NOverlayStack.Instance!.Push(screen);
        Task ignored = screen.CardsSelected();
        try
        {
            await WaitUntilAsync(() => screen.IsVisibleInTree(), "the combat pile grid opens", ShortWait);
            await WaitSecondsAsync(0.7);
            NPeekButton peek = screen.GetNode<NPeekButton>("%PeekButton");
            Rect2 eye = peek.GetGlobalRect();
            bool shown = peek.IsVisibleInTree() && peek.IsEnabled && GetViewportRect().Encloses(eye) &&
                         eye.Size.X >= PortraitBands.MinTarget && eye.Size.Y >= PortraitBands.MinTarget;
            _report.Check("select-confirm_pile_select_peek_shown", shown,
                $"peek={eye} visible={peek.IsVisibleInTree()} enabled={peek.IsEnabled} canvas={GetViewportRect()}");
            _report.Check("select-confirm_pile_select_peek_clear_of_piles", EyeClearOfPiles(screen, eye),
                EyePileDetail(screen, eye));
            if (!shown) return;
            await _pointer.TapAsync(CenterOf(peek));
            await WaitUntilAsync(() => peek.IsPeeking, "the pile grid peeks", ShortWait);
            await WaitFramesAsync(5);
            _report.Check("select-confirm_pile_select_peek_piles_portrait", ChoicePilesOnRail(screen, out string detail), detail);
            eye = peek.GetGlobalRect();
            _report.Check("select-confirm_pile_select_peek_eye_clear_while_peeking", EyeClearOfPiles(screen, eye),
                EyePileDetail(screen, eye));
            await CaptureAsync("select-confirm_pile_select_peek_hidden");
            await _pointer.TapAsync(CenterOf(peek));
            await WaitUntilAsync(() => !peek.IsPeeking, "the pile grid stops peeking", ShortWait);
        }
        catch (TimeoutException)
        {
            _report.Fail("select-confirm_pile_select_peek", "the combat pile grid did not settle");
        }
        finally
        {
            _ = ignored;
            NOverlayStack.Instance?.Clear();
            await WaitFramesAsync(4);
        }
    }

    private static Control? ChoicePile(Control screen, string name) =>
        (screen.GetNodeOrNull<Control>("%CombatPiles"))?.GetNodeOrNull<Control>("%" + name);

    /// <summary>The picker's own piles must stand exactly where the fight's own piles stand on the portrait rail.</summary>
    private bool ChoicePilesOnRail(Control screen, out string detail)
    {
        Vector2 canvas = GetViewportSize();
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvas.Y, 0f);
        PortraitHudLayout hud = PortraitCombatMath.ComputeHudLayout(canvas.Y, bands);
        var parts = new List<string>();
        bool ok = true;
        foreach ((string name, float x, float y) in new[]
                 {
                     ("DrawPile", hud.DrawShowX, hud.DrawShowY),
                     ("DiscardPile", hud.DiscardShowX, hud.DiscardShowY),
                     ("ExhaustPile", hud.ExhaustShowX, hud.ExhaustShowY),
                 })
        {
            Control? pile = ChoicePile(screen, name);
            if (pile is null)
            {
                ok = false;
                parts.Add($"{name}=none");
                continue;
            }
            Rect2 rect = pile.GetGlobalRect();
            if (!pile.IsVisibleInTree())
            {
                // The native exhaust pile is hidden while no card has been exhausted.
                parts.Add($"{name}=hidden");
                continue;
            }
            // The rail places an 80-unit pile at its unscaled show point with pivot (40,40) and PileScale; its drawn
            // rect is inset by pivot * (scale - 1) and is 80 * PileScale square. This is the same rect the fight's own
            // pile shows, which is what "at its portrait place" means here.
            float inset = 40f * (hud.PileScale - 1f);
            float side = 80f * hud.PileScale;
            bool placed = Math.Abs(rect.Position.X - (x - inset)) <= 3f &&
                          Math.Abs(rect.Position.Y - (y - inset)) <= 3f &&
                          Math.Abs(rect.Size.X - side) <= 3f && Math.Abs(rect.Size.Y - side) <= 3f;
            ok &= placed;
            parts.Add($"{name}={(placed ? "rail" : "off-rail")} {rect}");
        }
        detail = $"railTop={bands.RailTopY:F0} pileScale={hud.PileScale:F2} " + string.Join("; ", parts);
        return ok;
    }

    /// <summary>Whether the eye is clear of every draw/discard/exhaust pile drawn under it, the fight's and the picker's.</summary>
    private static bool EyeClearOfPiles(Control screen, Rect2 eye)
    {
        foreach (Rect2 rect in PileRects(screen)) if (eye.Intersects(rect)) return false;
        return true;
    }

    private static string EyePileDetail(Control screen, Rect2 eye)
    {
        var parts = new List<string> { $"eye={eye}" };
        foreach (Rect2 rect in PileRects(screen)) parts.Add(rect.ToString());
        return string.Join("; ", parts);
    }

    private static IEnumerable<Rect2> PileRects(Control screen)
    {
        if (NCombatRoom.Instance is { } room)
        {
            foreach (Control pile in new Control[] { room.Ui.DrawPile, room.Ui.DiscardPile, room.Ui.ExhaustPile })
                if (GodotObject.IsInstanceValid(pile) && pile.IsVisibleInTree())
                    yield return pile.GetGlobalRect();
        }
        foreach (string name in new[] { "DrawPile", "DiscardPile", "ExhaustPile" })
            if (ChoicePile(screen, name) is { } pile && GodotObject.IsInstanceValid(pile) && pile.IsVisibleInTree())
                yield return pile.GetGlobalRect();
    }

    private Rect2 GetViewportRect() => new(Vector2.Zero, GetViewportSize());

    // ---- 3. Relic choice ----------------------------------------------------------------------------------------------

    private async Task CheckRelicChoiceSelectConfirmAsync()
    {
        var relics = ModelDb.AllRelics.Skip(10).Take(3).Select(r => r.ToMutable()).ToList();
        Type screenType = typeof(NGame).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Screens.NChooseARelicSelection")
            ?? throw new InvalidOperationException("NChooseARelicSelection is gone.");
        MethodInfo? show = screenType.GetMethod("ShowScreen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        object? screen = show?.Invoke(null, [relics]);
        if (screen is not Control relicScreen)
        {
            _report.Fail("select-confirm_relic_choice_opens", "NChooseARelicSelection.ShowScreen returned null");
            return;
        }
        MethodInfo? selected = screenType.GetMethod("RelicsSelected", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Task answer = (Task?)selected?.Invoke(screen, null) ?? Task.CompletedTask;
        try
        {
            await WaitUntilAsync(() => ScreenAlive(relicScreen) && relicScreen.IsInsideTree() && relicScreen.IsVisibleInTree(),
                "relic choice screen", MediumWait);
        }
        catch (TimeoutException)
        {
            _report.Fail("select-confirm_relic_choice_opens", "the relic choice screen never became visible");
            return;
        }
        if (!ScreenAlive(relicScreen) || !relicScreen.IsVisibleInTree())
        {
            _report.Fail("select-confirm_relic_choice_opens", "the relic choice screen closed before it was used");
            return;
        }
        await WaitSecondsAsync(0.8);
        List<Control> items = relicScreen.GetNodeOrNull<Control>("RelicRow")?.GetChildren().OfType<Control>().ToList() ?? [];
        _report.Check("select-confirm_relic_choice_row", items.Count == 3, $"relics={items.Count}");
        if (items.Count < 3) return;

        int confirms = PhoneSelectConfirm.Confirms;
        int clears = PhoneSelectConfirm.Clears;

        await _pointer.TapAsync(CenterOf(items[0]));
        await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, items[0]), "relic first tap selects", ShortWait);
        _report.Check("select-confirm_relic_choice_first_tap_only_selects",
            !answer.IsCompleted && ReticleNode(items[0]) is not null &&
            PhoneSelectConfirm.SelectedName == items[0].Name.ToString() && PhoneSelectConfirm.Confirms == confirms,
            $"selected={PhoneSelectConfirm.SelectedName ?? "none"} answer={answer.IsCompleted} confirms={PhoneSelectConfirm.Confirms}");
        await CheckReticleAsync(items[0], items[0], "select-confirm_relic_choice");
        _report.Check("select-confirm_relic_choice_tip_on_select", TipShownUnder(items[0]),
            "the selected relic shows its native tip");
        await CaptureAsync("select-confirm_relic_choice_selected");

        await _pointer.TapAsync(CenterOf(items[2]));
        await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, items[2]), "relic selection moves", ShortWait);
        _report.Check("select-confirm_relic_choice_moves",
            PhoneSelectConfirm.SelectedName == items[2].Name.ToString() && !answer.IsCompleted &&
            PhoneSelectConfirm.Clears == clears + 1,
            $"selected={PhoneSelectConfirm.SelectedName ?? "none"} clears={PhoneSelectConfirm.Clears}");
        await CaptureAsync("select-confirm_relic_choice_moved");

        await _pointer.TapAsync(EmptyPoint());
        await WaitUntilAsync(() => PhoneSelectConfirm.SelectedOwner is null, "relic press elsewhere clears", ShortWait);
        _report.Check("select-confirm_relic_choice_clears", !answer.IsCompleted && PhoneSelectConfirm.SelectedName is null,
            $"selected={PhoneSelectConfirm.SelectedName ?? "none"} answer={answer.IsCompleted}");
        await CaptureAsync("select-confirm_relic_choice_cleared");

        await _pointer.TapAsync(CenterOf(items[0]));
        await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, items[0]), "relic selected for the hold", ShortWait);
        int holds = PhoneSelectConfirm.HoldInspects;
        var (holdInspects, _, relicInspected) = await HoldAsync(CenterOf(items[0]),
            "select-confirm_relic_choice_hold_inspect");
        _report.Check("select-confirm_relic_choice_hold_inspects",
            holdInspects == holds + 1 && relicInspected && PhoneSelectConfirm.Confirms == confirms && !answer.IsCompleted,
            $"holdInspects={holds}->{holdInspects} inspectScreen={relicInspected}");
        await CloseInspectScreensAsync();
        await _pointer.TapAsync(EmptyPoint());
        await WaitFramesAsync(3);

        // The second tap answers with exactly the intended relic.
        await _pointer.TapAsync(CenterOf(items[1]));
        await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, items[1]), "relic selected", ShortWait);
        await CaptureAsync("select-confirm_relic_choice_second_pick_selected");
        await _pointer.TapAsync(CenterOf(items[1]));
        try
        {
            await WaitUntilAsync(() => answer.IsCompleted, "relic choice answered", ShortWait);
        }
        catch (TimeoutException)
        {
            _report.Fail("select-confirm_relic_choice_second_tap_takes", "the second tap did not answer the relic choice");
            return;
        }
        await WaitFramesAsync(6);
        object? result = answer.GetType().IsGenericType ? answer.GetType().GetProperty("Result")?.GetValue(answer) : null;
        bool modelExposed = result is RelicModel;
        string picked = result is RelicModel relic ? relic.Id.ToString() : "n/a";
        bool tookIntended = !modelExposed || ((RelicModel)result!).Id == relics[1].Id;
        // RelicsSelected removes the overlay, so the fixture's screen is already freed here: never touch it unguarded.
        bool screenGone = !ScreenAlive(relicScreen) || !relicScreen.IsVisibleInTree();
        _report.Check("select-confirm_relic_choice_second_tap_takes",
            answer.IsCompleted && tookIntended && PhoneSelectConfirm.Confirms == confirms + 1 && screenGone,
            $"picked={picked} expected={relics[1].Id} modelExposed={modelExposed} screenGone={screenGone} " +
            $"confirms={confirms}->{PhoneSelectConfirm.Confirms}");
        await CaptureAsync("select-confirm_relic_choice_taken");
        _report.Pass("select-confirm_relic_choice_done",
            modelExposed ? $"answered {picked} with a second tap" : "answered the relic choice with a second tap");
    }

    // ---- 4. Treasure --------------------------------------------------------------------------------------------------

    private async Task CheckTreasureSelectConfirmAsync(Player player)
    {
        SaveManager.Instance.MarkFtueAsComplete("obtain_relic_ftue");

        // 4a. A normal chest (one relic, one round): every visible relic can be taken, so the chest keeps the game's own
        // single tap. The entrance tap still does nothing, and the take leaves no selection, reticle or confirm behind.
        (NTreasureRoom room, NTreasureRoomRelicCollection collection) = await EnterTreasureRoomAsync(relics: 1);
        int relicsBefore = player.Relics.Count;
        int confirms = PhoneSelectConfirm.Confirms;
        await _pointer.TapAsync(CenterOf(room.GetNode<NTreasureButton>("%Chest")));
        await WaitUntilAsync(() => collection.IsVisibleInTree(), "chest opens", ShortWait);
        await WaitUntilAsync(() => collection.HoldersInUse.Count > 0, "treasure relic holders appear", ShortWait);
        await CaptureAsync("select-confirm_treasure_entrance");

        // A tap during the entrance: the holders ignore touches, (or the collection ignores picks), so nothing is taken
        // and nothing is selected for later.
        NTreasureRoomRelicHolder first = collection.HoldersInUse[0];
        bool ignoring = first.MouseFilter == Control.MouseFilterEnum.Ignore;
        await _pointer.TapAsync(CenterOf(first));
        await WaitFramesAsync(4);
        _report.Check("select-confirm_treasure_entrance_tap_harmless",
            player.Relics.Count == relicsBefore && PhoneSelectConfirm.Confirms == confirms,
            $"entrance tap: mouseFilter={first.MouseFilter} ignoring={ignoring} relics={player.Relics.Count}/" +
            $"{relicsBefore} confirms={PhoneSelectConfirm.Confirms}");
        if (!GodotObject.IsInstanceValid(first) || player.Relics.Count != relicsBefore)
        {
            _report.Fail("select-confirm_treasure_entrance_holder",
                "the entrance tap took the chest's relic or freed its holder; the single-tap check cannot run");
            return;
        }
        if (PhoneSelectConfirm.SelectedOwner is not null) PhoneSelectConfirm.Clear("select-confirm fixture");

        await WaitUntilAsync(() => collection.HoldersInUse.All(h => h.MouseFilter != Control.MouseFilterEnum.Ignore),
            "treasure relics accept touches", ShortWait);
        await WaitSecondsAsync(0.4);
        TreasureChestContract single = ReadTreasureChestContract(collection);
        _report.Check("select-confirm_treasure_single_relic_all_claimable",
            !single.NeedsConfirm && single.Holders == 1 && single.Players == 1 && single.PicksLeft == 1,
            single.Detail);
        await CheckTreasurePressAwardAsync("select-confirm_treasure", first, player);
        try
        {
            await WaitUntilAsync(() => player.Relics.Count == relicsBefore + 1, "one tap takes the single relic", ShortWait);
            await WaitUntilAsync(() => RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics is null,
                "the single-relic chest finishes", ShortWait);
        }
        catch (TimeoutException)
        {
            _report.Fail("select-confirm_treasure_single_tap_room",
                "the single-relic chest did not finish; the choice chest still runs next");
        }
        await CaptureAsync("select-confirm_treasure_single_tap_taken");

        // 4b. A chest that offers a choice: the shared treasure fixture's reward-count path gives this single player three
        // relics for its one round, so one relic must be chosen and the select-then-confirm pass owns the chest.
        (room, collection) = await EnterTreasureRoomAsync(relics: 3);
        relicsBefore = player.Relics.Count;
        confirms = PhoneSelectConfirm.Confirms;
        await _pointer.TapAsync(CenterOf(room.GetNode<NTreasureButton>("%Chest")));
        await WaitUntilAsync(() => collection.IsVisibleInTree() && collection.HoldersInUse.Count > 0,
            "choice chest opens", ShortWait);
        await WaitUntilAsync(() => collection.HoldersInUse.All(h => h.MouseFilter != Control.MouseFilterEnum.Ignore),
            "choice chest relics accept touches", ShortWait);
        await WaitSecondsAsync(0.4);
        List<NTreasureRoomRelicHolder> holders = collection.HoldersInUse.ToList();
        _report.Check("select-confirm_treasure_choice_relics", holders.Count == 3, $"holders={holders.Count}");
        TreasureChestContract choice = ReadTreasureChestContract(collection);
        _report.Check("select-confirm_treasure_choice_contract",
            choice.NeedsConfirm && choice.Players == 1 && choice.Holders == 3 && choice.PicksLeft == 1,
            choice.Detail);
        await CaptureAsync("select-confirm_treasure_choice_entrance");
        if (holders.Count < 2)
        {
            _report.Fail("select-confirm_treasure_choice_target", "the choice chest shows fewer than two relics");
            return;
        }
        NTreasureRoomRelicHolder target = holders[^1];
        string relic = target.Relic.Model.Id.ToString();
        await _pointer.TapAsync(CenterOf(target));
        try
        {
            await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, target), "treasure relic selects", ShortWait);
        }
        catch (TimeoutException)
        {
            _report.Fail("select-confirm_treasure_first_tap_only_selects",
                "the tap did not leave the relic selected (the launcher let the selection go, or the relic was taken)");
            return;
        }
        _report.Check("select-confirm_treasure_first_tap_only_selects",
            player.Relics.Count == relicsBefore && ReticleNode(target) is not null &&
            PhoneSelectConfirm.Confirms == confirms,
            $"selected={PhoneSelectConfirm.SelectedName ?? "none"} relics={player.Relics.Count}/{relicsBefore}");
        await CheckReticleAsync(target, target, "select-confirm_treasure");
        _report.Check("select-confirm_treasure_tip_on_select", TipShownUnder(target),
            "the selected relic shows its native tip");
        await CaptureAsync("select-confirm_treasure_selected");

        await _pointer.TapAsync(CenterOf(target));
        await WaitUntilAsync(() => player.Relics.Count > relicsBefore, "second tap picks the treasure relic", ShortWait);
        await WaitFramesAsync(8);
        _report.Check("select-confirm_treasure_second_tap_takes",
            player.Relics.Count == relicsBefore + 1 && PhoneSelectConfirm.Confirms == confirms + 1,
            $"relics={relicsBefore}->{player.Relics.Count} confirms={confirms}->{PhoneSelectConfirm.Confirms} relic={relic}");
        await CaptureAsync("select-confirm_treasure_taken");
        await WaitSecondsAsync(0.6);
        _report.Check("select-confirm_treasure_takes_once", player.Relics.Count == relicsBefore + 1,
            $"relics={player.Relics.Count} after the chest settled");
        // The selection's tip is anchored to the relic drawn inside the holder, so a tip cleared by the holder alone would
        // stay over the Proceed ribbon after the chest closes.
        _report.Check("select-confirm_treasure_tip_clears_on_pick",
            !GodotObject.IsInstanceValid(target) || !TipShownUnder(target),
            $"tip={(GodotObject.IsInstanceValid(target) && TipShownUnder(target) ? "still shown" : "cleared")} " +
            $"relic={relic}");
        // The chest's pick is a multiplayer vote (TreasureRoomRelicSynchronizer.PickRelicLocally). The commit path is
        // unchanged native code; a multiplayer run has no harness fixture, so its vote behaviour is not checked here.
        _report.Skip("select-confirm_treasure_multiplayer_untested",
            "single player only: PickRelicLocally was committed through the native path; the multiplayer vote is untested");
        _report.Pass("select-confirm_treasure_done",
            "one tap took the normal chest's relic; the three-relic one-round chest selected and took one on a second tap");
    }

    /// <summary>
    /// Enters a native treasure room and, for a fixture chest, gives the synchronizer's current rewards the requested count
    /// through the shared reward-count path (extra common relics, no networking and no UI replacement). A normal chest asks
    /// for one relic and keeps the room's own roll.
    /// </summary>
    private async Task<(NTreasureRoom Room, NTreasureRoomRelicCollection Collection)> EnterTreasureRoomAsync(int relics)
    {
        await RunManager.Instance.EnterRoomDebug(RoomType.Treasure, showTransition: false);
        await WaitUntilAsync(() => FindFirst<NTreasureRoom>(_tree.Root) is { } r && r.IsInsideTree(),
            "treasure room", MediumWait);
        NTreasureRoom room = FindFirst<NTreasureRoom>(_tree.Root)!;
        if (relics != 1) SetTreasureRewardRelics(relics);
        await WaitSecondsAsync(0.5);
        return (room, room.GetNode<NTreasureRoomRelicCollection>("%RelicCollection"));
    }

    // ---- 5. Portrait -> landscape with a selection open --------------------------------------------------------------

    private async Task CheckOrientationSelectConfirmAsync(Player player)
    {
        NOverlayStack.Instance?.Clear();
        await WaitFramesAsync(4);
        List<CardModel> cards = player.Deck.Cards.Take(3).ToList();
        if (cards.Count < 3)
        {
            _report.Fail("select-confirm_orientation_cards", $"deck has {cards.Count} cards");
            return;
        }

        var screen = NChooseACardSelectionScreen.ShowScreen(cards, canSkip: true);
        if (screen is null)
        {
            _report.Fail("select-confirm_orientation_open", "ShowScreen returned null");
            return;
        }
        Task<IEnumerable<CardModel>> answer = screen.CardsSelected();
        await WaitUntilAsync(() => screen.IsInsideTree() && screen.Visible, "picker in portrait", MediumWait);
        await WaitSecondsAsync(0.8);
        List<NGridCardHolder> holders = FindAll<NGridCardHolder>(screen)
            .Where(h => GodotObject.IsInstanceValid(h) && h.IsVisibleInTree() && !h.IsQueuedForDeletion()).ToList();
        if (holders.Count < 2)
        {
            _report.Fail("select-confirm_orientation_holders", $"cards={holders.Count}");
            return;
        }

        await _pointer.TapAsync(CenterOf(holders[0].Hitbox));
        await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, holders[0]),
            "portrait selection for the switch", ShortWait);
        _report.Check("select-confirm_orientation_selected_in_portrait",
            ReticleNode(holders[0]) is not null,
            $"selected={PhoneSelectConfirm.SelectedName ?? "none"}");
        await CheckReticleAsync(holders[0], holders[0].Hitbox, "select-confirm_orientation");
        await CaptureAsync("select-confirm_orientation_portrait_selected");
        int clears = PhoneSelectConfirm.Clears;

        // The launcher's live Portrait Mode switch (PortraitLayout.Apply, the Graphics row's call).
        SetPortraitMode(false);
        await WaitFramesAsync(6);
        _report.Check("select-confirm_orientation_landscape_canvas", !SafeAreaCanvas.IsPortrait,
            $"portrait={SafeAreaCanvas.IsPortrait} canvas={_tree.Root.ContentScaleSize} visible={GetViewportSize()}");
        bool reticleFreed = await AwaitReticleGoneAsync(holders[0], "landscape frees the selection reticle after its fade");
        _report.Check("select-confirm_orientation_selection_cleared",
            PhoneSelectConfirm.SelectedOwner is null && PhoneSelectConfirm.Clears == clears + 1 &&
            !ReticleSelectedUnder(screen) && reticleFreed,
            $"selected={PhoneSelectConfirm.SelectedName ?? "none"} clears={clears}->{PhoneSelectConfirm.Clears} " +
            $"reticleFreed={reticleFreed}");
        await CaptureAsync("select-confirm_orientation_landscape_open");

        // Landscape plays as the game does: one tap takes the card natively.
        await WaitSecondsAsync(0.4);
        CardModel expected = holders[1].CardModel ?? throw new InvalidOperationException("picker holder has no card model");
        await _pointer.TapAsync(CenterOf(holders[1].Hitbox));
        try
        {
            await WaitUntilAsync(() => answer.IsCompleted, "landscape single tap takes natively", ShortWait);
        }
        catch (TimeoutException)
        {
            _report.Fail("select-confirm_orientation_landscape_takes", "the landscape tap did not answer the native picker");
            SetPortraitMode(true);
            return;
        }
        await WaitFramesAsync(6);
        IEnumerable<CardModel>? picked = answer.IsCompletedSuccessfully ? answer.Result : null;
        _report.Check("select-confirm_orientation_landscape_takes",
            picked is not null && picked.Count() == 1 && picked.First().Id == expected.Id,
            $"picked={string.Join(",", picked?.Select(c => c.Id.ToString()) ?? [])} expected={expected.Id}");
        await CaptureAsync("select-confirm_orientation_landscape_taken");

        // Back in portrait, a freshly opened picker uses the two taps again.
        SetPortraitMode(true);
        await WaitFramesAsync(6);
        _report.Check("select-confirm_orientation_portrait_returned", SafeAreaCanvas.IsPortrait,
            $"portrait={SafeAreaCanvas.IsPortrait} canvas={_tree.Root.ContentScaleSize} visible={GetViewportSize()}");
        NOverlayStack.Instance?.Clear();
        await WaitFramesAsync(4);
        var again = NChooseACardSelectionScreen.ShowScreen(cards, canSkip: true);
        if (again is null)
        {
            _report.Fail("select-confirm_orientation_reopen", "ShowScreen returned null in portrait");
            return;
        }
        Task<IEnumerable<CardModel>> answerAgain = again.CardsSelected();
        await WaitUntilAsync(() => again.IsInsideTree() && again.Visible, "picker back in portrait", MediumWait);
        await WaitSecondsAsync(0.8);
        List<NGridCardHolder> againHolders = FindAll<NGridCardHolder>(again)
            .Where(h => GodotObject.IsInstanceValid(h) && h.IsVisibleInTree() && !h.IsQueuedForDeletion()).ToList();
        if (againHolders.Count == 0)
        {
            _report.Fail("select-confirm_orientation_reopen_holders", "the reopened picker has no cards");
            return;
        }
        await _pointer.TapAsync(CenterOf(againHolders[0].Hitbox));
        await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, againHolders[0]),
            "portrait selection after the return", ShortWait);
        _report.Check("select-confirm_orientation_portrait_selects_again",
            !answerAgain.IsCompleted && ReticleNode(againHolders[0]) is not null,
            $"selected={PhoneSelectConfirm.SelectedName ?? "none"} answer={answerAgain.IsCompleted}");
        await CaptureAsync("select-confirm_orientation_portrait_again");
        PhoneSelectConfirm.Clear("select-confirm fixture");
        NOverlayStack.Instance?.Clear();
        await WaitFramesAsync(2);
        _report.Pass("select-confirm_orientation_done",
            "the selection and reticle go in landscape, a landscape tap takes natively, and portrait keeps the two taps");
    }

    // ---- Shared helpers -----------------------------------------------------------------------------------------------

    private static int PlayerDeck(Player player) => player.Deck.Cards.Count;

    /// <summary>
    /// Holds a fixture task that is expected to end cancelled (a loot set the phase tears down before its last row is
    /// taken), so its cancellation is observed here instead of surfacing as an unobserved task exception.
    /// </summary>
    private static void ObserveFixtureTask(Task task, string what) =>
        task.ContinueWith(t => Log.Warn($"[TEST] select-confirm: {what} ended: " +
                                       $"{t.Exception?.GetBaseException().Message ?? t.Status.ToString()}"),
            TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>Whether the card reward row accepts a touch now (the guard holds it with <c>MouseFilter=Ignore</c>).</summary>
    private static bool CardRewardRowAcceptsTouches(NRewardsScreen loot) =>
        FindAll<NRewardButton>(loot).Any(b => b.Reward is CardReward && b.MouseFilter != Control.MouseFilterEnum.Ignore);

    /// <summary>The card reward row's touch filter, for failure details.</summary>
    private static string CardRewardRowFilter(NRewardsScreen loot)
    {
        NRewardButton? row = FindAll<NRewardButton>(loot).FirstOrDefault(b => b.Reward is CardReward);
        return row is null || !GodotObject.IsInstanceValid(row) ? "noRow" : row.MouseFilter.ToString();
    }

    /// <summary>Whether a screen is still a live Godot object (a native screen removes and frees itself when it answers).</summary>
    private static bool ScreenAlive(Control? screen) => screen != null && GodotObject.IsInstanceValid(screen);

    /// <summary>A point on the canvas that holds no choice item (the banner band above every choice row).</summary>
    private Vector2 EmptyPoint() => new(GetViewportSize().X * 0.5f, 16f);

    /// <summary>
    /// How long <see cref="PhoneRewardTapGuard"/> kept the loot rows closed after the card screen returned: the time until
    /// <c>IsOpen</c>, and the time from the rows' last movement (their instance and coarse Y, as the guard tracks them) to
    /// that moment. A guard that stays shut long after the rows have settled would be a product defect, not a test wait.
    /// </summary>
    private async Task<(double SinceReturn, double SinceRowsStopped, bool Reopened, string Rows)> MeasureRewardListReopenAsync(
        NRewardsScreen loot)
    {
        ulong start = Time.GetTicksMsec();
        ulong lastMove = start;
        ulong opened = 0;
        bool reopened = false;
        string previous = "";
        for (int frame = 0; frame < 600; frame++)
        {
            string rows = RewardRowSignature(loot);
            if (rows != previous)
            {
                previous = rows;
                lastMove = Time.GetTicksMsec();
            }
            if (PhoneRewardTapGuard.IsOpen(loot))
            {
                opened = Time.GetTicksMsec();
                reopened = true;
                break;
            }
            await WaitFramesAsync(1);
        }
        if (opened == 0) opened = Time.GetTicksMsec();
        double sinceReturn = opened > start ? (double)(opened - start) : 0;
        double sinceRowsStopped = opened > lastMove ? (double)(opened - lastMove) : 0;
        Log.Info($"[TEST] select-confirm: loot rows {(reopened ? "took touches again" : "never took touches again")} " +
                 $"{sinceReturn:F0} ms after the return, {sinceRowsStopped:F0} ms after their last movement; rows={previous}");
        return (sinceReturn, sinceRowsStopped, reopened, previous);
    }

    /// <summary>The loot rows as the tap guard sees them: instance id and Y in eighths.</summary>
    private static string RewardRowSignature(NRewardsScreen loot)
    {
        var parts = new List<string>();
        foreach (NRewardButton row in FindAll<NRewardButton>(loot))
        {
            if (!GodotObject.IsInstanceValid(row) || row.IsQueuedForDeletion()) continue;
            parts.Add($"{row.GetInstanceId()}:{MathF.Round(row.GlobalPosition.Y / 8f)}");
        }
        return string.Join(";", parts);
    }

    /// <summary>
    /// Presses, stays past the hold threshold (capturing the open inspection) and releases in place. Returns the hold
    /// counter and which native inspection was seen while the press was still down.
    /// </summary>
    private async Task<(int Holds, bool CardScreen, bool RelicScreen)> HoldAsync(Vector2 point, string capture)
    {
        int before = PhoneSelectConfirm.HoldInspects;
        await _pointer.PressAsync(point);
        try
        {
            await WaitUntilAsync(() => PhoneSelectConfirm.HoldInspects > before,
                "hold opens the item's native inspection", TimeSpan.FromSeconds(4));
        }
        catch (TimeoutException)
        {
            // The step that follows reports the missing inspection; the release still has to happen.
        }
        bool cardScreen = FindAll<NInspectCardScreen>(_tree.Root).Any(s => s.IsVisibleInTree());
        bool relicScreen = FindAll<NInspectRelicScreen>(_tree.Root).Any(s => s.IsVisibleInTree());
        await CaptureAsync(capture);
        await _pointer.ReleaseAsync(point);
        await WaitFramesAsync(4);
        return (PhoneSelectConfirm.HoldInspects, cardScreen, relicScreen);
    }

    /// <summary>Whether a native hover tip is shown for this item or for a node inside it (a relic's own node).</summary>
    private bool TipShownUnder(Control item)
    {
        if (TipOwnerField is null)
            return FindAll<NHoverTipSet>(_tree.Root).Any(t => t.IsVisibleInTree());
        return FindAll<NHoverTipSet>(_tree.Root).Any(tip =>
            tip.IsVisibleInTree() && TipOwnerField.GetValue(tip) is Control owner && GodotObject.IsInstanceValid(owner) &&
            (ReferenceEquals(owner, item) || item.IsAncestorOf(owner)));
    }

    /// <summary>
    /// Closes the native inspection screens a hold may have opened and waits until the game has taken them off the overlay
    /// stack. The screen's <c>Close</c> fades for a quarter second and only then turns itself invisible; until it does, the
    /// stack keeps input from the choice screen below, and a tap sent into that window selects nothing.
    /// </summary>
    private async Task CloseInspectScreensAsync()
    {
        try
        {
            if (FindAll<NInspectCardScreen>(_tree.Root).Any(s => s.IsVisibleInTree()))
                NGame.Instance?.GetInspectCardScreen()?.Close();
        }
        catch (Exception error)
        {
            Log.Warn($"[TEST] select-confirm: the card inspection did not close: {error.GetBaseException().Message}");
        }
        try
        {
            if (FindAll<NInspectRelicScreen>(_tree.Root).Any(s => s.IsVisibleInTree()))
                NGame.Instance?.GetInspectRelicScreen()?.Close();
        }
        catch (Exception error)
        {
            Log.Warn($"[TEST] select-confirm: the relic inspection did not close: {error.GetBaseException().Message}");
        }
        try
        {
            await WaitUntilAsync(() =>
                    !FindAll<NInspectCardScreen>(_tree.Root).Any(s => s.IsVisibleInTree() && !s.IsQueuedForDeletion()) &&
                    !FindAll<NInspectRelicScreen>(_tree.Root).Any(s => s.IsVisibleInTree() && !s.IsQueuedForDeletion()),
                "the native inspection leaves the overlay stack", ShortWait);
        }
        catch (TimeoutException)
        {
            Log.Warn("[TEST] select-confirm: the native inspection stayed open; the next taps may land on it");
        }
        await WaitFramesAsync(2);
    }
}
