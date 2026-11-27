using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// <c>reward-guard</c> on the launcher host, separate from select-then-confirm: <see cref="PhoneRewardTapGuard"/> must keep
/// every unread loot row safe while the list fades in, while the rows move after a claim, and for a finger already down
/// when they settle, while a settled row still takes a new tap normally. The fixture is a loot list with two gold rows (one
/// of several) and a card reward, so a claim removes a row and the rest slide up under the finger.
/// </summary>
internal sealed partial class TestDriver
{
    /// <summary>Called by <c>LauncherHostRunner</c> for <c>--touchspire-test=reward-guard</c>.</summary>
    internal static async Task RunLauncherRewardGuardAsync(TestOptions options, TestReport report,
        ScreenshotService screenshots, VirtualPointer pointer)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Log.Error("Launcher reward-guard requires a SceneTree.");
            return;
        }
        await new TestDriver(tree, options, report, screenshots, pointer).RunLauncherRewardGuardInternalAsync();
    }

    private async Task RunLauncherRewardGuardInternalAsync()
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            _report.Fail("reward-guard_portrait", $"reward-guard needs a portrait window; window={DisplayServer.WindowGetSize()} " +
                                                  $"canvas={GetViewportSize()}");
            return;
        }
        if (PhoneRewardTapGuard.Instance is null)
        {
            _report.Fail("reward-guard_installed", "PhoneRewardTapGuard is not installed");
            return;
        }

        await WaitForMainMenuPreloadAsync();
        RunState state = await StartIroncladRunAsync();
        Player player = LocalContext.GetMe(state) ?? throw new InvalidOperationException("No local player in the run.");
        player.Creature.SetMaxHpInternal(9999);
        player.Creature.SetCurrentHpInternal(9999);

        await RunRewardGuardPhaseAsync("loot", () => CheckRewardGuardAsync(state, player));
        _report.Pass("reward-guard_complete",
            "fade-in, a claim, the row movement under a held press and a settled row all behaved");
    }

    /// <summary>
    /// Runs the reward-guard fixture; a failure is reported as <c>reward-guard_…_exception</c> and the scenario still ends
    /// with a complete step. The portrait-leave wrapper cannot be reused: it names its failures for that scenario.
    /// </summary>
    private async Task RunRewardGuardPhaseAsync(string phase, Func<Task> run)
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
            _report.Fail($"reward-guard_{phase}_exception", error.GetBaseException().Message);
        }
    }

    private async Task CheckRewardGuardAsync(RunState state, Player player)
    {
        await EnterTouchCombatAsync(state, "reward-guard");
        var rewards = new RewardsSet(player);
        if (state.CurrentRoom is { } room) rewards.EmptyForRoom(room);
        var cardOptions = CardCreationOptions.ForRoom(player, RoomType.Monster);
        var cardReward = new CardReward(cardOptions, 3, player);
        cardReward.Populate();
        rewards.WithCustomRewards(new List<Reward>
        {
            new GoldReward(120, player),
            new GoldReward(50, player),
            cardReward,
        });

        NOverlayStack.Instance?.Clear();
        await WaitFramesAsync(4);
        // The fixture ends with the card row still open and is torn down by the next phase: keep its cancellation observed.
        ObserveFixtureTask(RunManager.Instance.RewardsSetSynchronizer.BeginRewardsSet(rewards), "reward-guard loot set");
        NRewardsScreen? loot = NRewardsScreen.ShowScreen(rewards, isTerminal: true, state);
        if (loot is null)
        {
            _report.Fail("reward-guard_list", "NRewardsScreen.ShowScreen returned null");
            return;
        }
        await WaitUntilAsync(() => loot.IsInsideTree() && loot.Visible && FindAll<NRewardButton>(loot).Count >= 3,
            "loot list with three rows", MediumWait);

        int goldBefore = player.Gold;
        List<NRewardButton> rows = FindAll<NRewardButton>(loot);
        _report.Check("reward-guard_has_several_rows", rows.Count >= 3, $"rows={rows.Count}");

        // 1. Taps while the list fades in collect nothing. The second tap is repeated only while the guard still blocks
        //    (a slow frame could settle the rows between the two on a very slow host).
        await WaitFramesAsync(2);
        bool blockedAtOpen = !PhoneRewardTapGuard.IsOpen(loot);
        Vector2 first = CenterOf(rows[0]);
        await WaitFramesAsync(2);
        await _pointer.TapAsync(first);
        await WaitFramesAsync(2);
        bool stillBlocked = !PhoneRewardTapGuard.IsOpen(loot);
        if (stillBlocked) await _pointer.TapAsync(first);
        await WaitFramesAsync(4);
        _report.Check("reward-guard_fadein_blocks",
            blockedAtOpen && stillBlocked && player.Gold == goldBefore &&
            !FindAll<NCardRewardSelectionScreen>(_tree.Root).Any(s => s.IsVisibleInTree()),
            $"guardedAtOpen={blockedAtOpen} guardedAfterFirstTap={stillBlocked} gold={goldBefore}->{player.Gold} " +
            $"cardScreen={FindAll<NCardRewardSelectionScreen>(_tree.Root).Any(s => s.IsVisibleInTree())}");
        await CaptureAsync("reward-guard_fadein");

        // 2. A settled row takes one tap.
        await WaitUntilAsync(() => PhoneRewardTapGuard.IsOpen(loot), "loot rows settle", ShortWait);
        rows = FindAll<NRewardButton>(loot);
        int goldBeforeClaim = player.Gold;
        await _pointer.TapAsync(CenterOf(rows[0]));
        await WaitUntilAsync(() => player.Gold == goldBeforeClaim + 120, "the settled gold row is claimed by one tap", ShortWait);
        _report.Check("reward-guard_settled_row_claims",
            player.Gold == goldBeforeClaim + 120, $"gold={goldBeforeClaim}->{player.Gold} (one tap, no confirmation)");
        await CaptureAsync("reward-guard_gold_claimed");

        // 3. The claim removes a row and the rest move up. The list is guarded again: a press that spans that movement
        //    (down while the rows are still blocked, up after they settle) must not collect the row that slid under it.
        await WaitUntilAsync(() => FindAll<NRewardButton>(loot).Count == 2, "the claimed row is gone", ShortWait);
        await WaitFramesAsync(2);
        rows = FindAll<NRewardButton>(loot);
        bool blockedAfterClaim = !PhoneRewardTapGuard.IsOpen(loot);
        Vector2 movedUnder = CenterOf(rows[0]);
        int goldBeforeSpanning = player.Gold;
        await _pointer.PressAsync(movedUnder);
        await WaitSecondsAsync(0.55);
        bool openAtRelease = PhoneRewardTapGuard.IsOpen(loot);
        await _pointer.ReleaseAsync(movedUnder);
        await WaitFramesAsync(6);
        _report.Check("reward-guard_press_spanning_settle_takes_nothing",
            blockedAfterClaim && openAtRelease && player.Gold == goldBeforeSpanning,
            $"blockedAfterClaim={blockedAfterClaim} openAtRelease={openAtRelease} " +
            $"gold={goldBeforeSpanning}->{player.Gold}");
        await CaptureAsync("reward-guard_spanned_press");

        // 4. The same row accepts a new tap once it has settled.
        await WaitUntilAsync(() => PhoneRewardTapGuard.IsOpen(loot), "loot rows settle again", ShortWait);
        rows = FindAll<NRewardButton>(loot);
        int goldBeforeSecond = player.Gold;
        await _pointer.TapAsync(CenterOf(rows[0]));
        await WaitUntilAsync(() => player.Gold == goldBeforeSecond + 50, "the settled row accepts a new tap", ShortWait);
        _report.Check("reward-guard_settled_row_accepts_new_tap",
            player.Gold == goldBeforeSecond + 50, $"gold={goldBeforeSecond}->{player.Gold}");
        await CaptureAsync("reward-guard_second_gold");

        // 5. Return from a card reward choice to the list, and the list still works.
        await WaitUntilAsync(() => FindAll<NRewardButton>(loot).Count == 1, "only the card reward row is left", ShortWait);
        rows = FindAll<NRewardButton>(loot);
        NRewardButton cardRow = rows[0];
        await WaitUntilAsync(() => PhoneRewardTapGuard.IsOpen(loot), "loot rows settle before the card reward", ShortWait);
        await _pointer.TapAsync(CenterOf(cardRow));
        await WaitUntilAsync(() => FindAll<NCardRewardSelectionScreen>(_tree.Root).Any(s => s.IsVisibleInTree()),
            "card reward selection", ShortWait);
        await WaitSecondsAsync(0.8);
        NCardRewardSelectionScreen screen = FindAll<NCardRewardSelectionScreen>(_tree.Root).Last(s => s.IsVisibleInTree());
        List<NCardRewardAlternativeButton> alternatives = FindAll<NCardRewardAlternativeButton>(screen);
        _report.Check("reward-guard_card_reward_opens", alternatives.Count >= 1, $"alternatives={alternatives.Count}");
        if (alternatives.Count == 0) return;
        await CaptureAsync("reward-guard_card_reward");
        await _pointer.TapAsync(CenterOf(alternatives[0]));
        await WaitUntilAsync(() => !GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree(),
            "card reward Skip returns to the loot list", ShortWait);
        await WaitSecondsAsync(0.6);
        _report.Check("reward-guard_returns_to_list", loot.IsVisibleInTree() && FindAll<NRewardButton>(loot).Count == 1,
            $"loot visible={loot.IsVisibleInTree()} rows={FindAll<NRewardButton>(loot).Count}");
        await CaptureAsync("reward-guard_returned");

        // The settled row still opens the reward after the return (one tap, no confirmation).
        await WaitUntilAsync(() => PhoneRewardTapGuard.IsOpen(loot), "the returned list settles", ShortWait);
        rows = FindAll<NRewardButton>(loot);
        await _pointer.TapAsync(CenterOf(rows[0]));
        await WaitUntilAsync(() => FindAll<NCardRewardSelectionScreen>(_tree.Root).Any(s => s.IsVisibleInTree()),
            "the returned list's card row opens again", ShortWait);
        _report.Pass("reward-guard_after_return_tap_works", "one tap on the settled row opened the reward again");
        await CaptureAsync("reward-guard_returned_open");
        await WaitSecondsAsync(0.6);
        NCardRewardSelectionScreen reopened = FindAll<NCardRewardSelectionScreen>(_tree.Root).Last(s => s.IsVisibleInTree());
        List<NCardRewardAlternativeButton> again = FindAll<NCardRewardAlternativeButton>(reopened);
        if (again.Count > 0) await _pointer.TapAsync(CenterOf(again[0]));
    }
}
