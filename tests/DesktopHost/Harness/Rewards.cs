using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// <c>rewards</c>: the loot list over a real fight, the gold row, the native card reward's Skip and reroll, the return to
/// the list and taking a card. The rows' fonts and sizes are the host's own layout: those checks are partials implemented
/// by the launcher (LauncherRewardChecks.cs). Taking the card goes through the host partial
/// <see cref="TakeRewardCardAsync"/>: the launcher selects first and takes it on the second tap.
/// </summary>
internal sealed partial class TestDriver
{
    private async Task RunRewardsScenarioAsync(RunState runState)
    {
        // Show the rewards screen over a real combat room, like the normal
        // post-combat flow, rather than over the map screen.
        await EnterCombatAsync();
        Player player = runState.Players[0];

        var rewards = new RewardsSet(player);
        if (runState.CurrentRoom is { } room)
        {
            rewards.EmptyForRoom(room);
        }

        Log.Info("[TEST] rewards: adding gold, relic, and card rows");
        var relicReward = new RelicReward(player);
        relicReward.Populate();
        var cardOptions = CardCreationOptions.ForRoom(player, RoomType.Monster);
        var cardReward = new CardReward(cardOptions, 3, player) { CanReroll = true };
        cardReward.Populate();
        rewards.WithCustomRewards(new List<Reward>
        {
            new GoldReward(120, player),
            relicReward,
            cardReward,
        });

        Log.Info("[TEST] rewards: clearing overlay stack");
        NOverlayStack.Instance?.Clear();
        await WaitFramesAsync(4);
        Log.Info("[TEST] rewards: showing screen");
        _ = RunManager.Instance.RewardsSetSynchronizer.BeginRewardsSet(rewards);
        NRewardsScreen? screen = NRewardsScreen.ShowScreen(rewards, isTerminal: true, runState);
        Log.Info($"[TEST] rewards: screen returned valid={screen is not null}");
        if (screen?.GetNodeOrNull<Control>("ProceedButton") is { } ribbon)
            await CheckSlidesAlongItsLineAsync(ribbon, "rewards_skip_enters_on_its_lane", 60);
        if (screen is null)
        {
            _report.Fail("rewards_screen_opens", "ShowScreen returned null");
            return;
        }

        await WaitUntilAsync(() => screen.IsInsideTree() && screen.Visible, "rewards screen", MediumWait);
        await WaitSecondsAsync(1.0);

        List<NRewardButton> buttons = FindAll<NRewardButton>(screen);
        _report.Check("rewards_screen_loaded", screen.IsInsideTree(), "rewards screen is present");
        _report.Check("rewards_has_rows", buttons.Count >= 3, $"rows={buttons.Count}");
        CheckRewardsRowsLayout("rewards", screen, buttons);

        await CaptureAsync("rewards_01_rows");
        int goldBefore = player.Gold;
        // T4: a drag that starts on a loot row must not claim it. The finger leaves the rows at the panel's top edge, so the
        // release is outside every row.
        Vector2 goldRowCentre = CenterOf(buttons[0]);
        Rect2 rowBlock = buttons.Select(row => row.GetGlobalRect()).Aggregate((a, b) => a.Merge(b));
        await _pointer.DragAsync(goldRowCentre, new Vector2(rowBlock.GetCenter().X, rowBlock.Position.Y - 40f), 18);
        await WaitSecondsAsync(0.6);
        _report.Check("rewards_swipe_from_row_does_not_claim", player.Gold == goldBefore
            && screen.IsVisibleInTree() && buttons.All(button => GodotObject.IsInstanceValid(button) && button.IsVisibleInTree()),
            $"gold={goldBefore}->{player.Gold} rows={buttons.Count}");
        Control lootWindow = screen.GetNode<Control>("Rewards");
        Rect2 windowBeforePick = lootWindow.GetGlobalRect();
        await _pointer.TapAsync(CenterOf(buttons[0]));
        await WaitSecondsAsync(0.6);
        _report.Check("rewards_gold_collection", player.Gold == goldBefore + 120, $"gold={goldBefore}->{player.Gold}");
        // The panel is the screen's frame: taking a reward closes its row up inside the panel, the panel itself stays put.
        _report.Check("rewards_loot_window_keeps_its_frame_after_a_pick", SameRect(windowBeforePick, lootWindow.GetGlobalRect()),
            $"before={windowBeforePick} after={lootWindow.GetGlobalRect()}");
        var lootSkip = screen.GetNode<Control>("ProceedButton");

        async Task<NCardRewardSelectionScreen> OpenReward(bool afterSkip = false)
        {
            bool open() => FindAll<NCardRewardSelectionScreen>(_tree.Root).Any(s => s.IsVisibleInTree());
            if (afterSkip)
            {
                // The loot list is still returning from the card Skip; a press delivered during that transition reaches the
                // row's hover but not its press. Wait for the row and retry once (logged with diagnostics); the card reward
                // must still open.
                await TapMenuUntilAsync(buttons[2], "reward_card_reopen", open, "native card reward selection", ShortWait);
            }
            else
            {
                await _pointer.TapAsync(CenterOf(buttons[2]));
                await WaitUntilAsync(open, "native card reward selection", ShortWait);
            }
            await WaitSecondsAsync(0.8);
            return FindAll<NCardRewardSelectionScreen>(_tree.Root).Last(s => s.IsVisibleInTree());
        }

        NCardRewardSelectionScreen selection = await OpenReward();
        var alternatives = FindAll<NCardRewardAlternativeButton>(selection);
        _report.Check("reward_native_skip_and_reroll", alternatives.Count == 2, $"alternatives={alternatives.Count}");
        if (alternatives.Count == 2)
        {
            CheckRewardAlternativesLayout("rewards", selection, alternatives);
            await CaptureAsync("rewards_02_skip_and_reroll");
            await _pointer.TapAsync(CenterOf(alternatives[1]));
            await WaitUntilAsync(() => !cardReward.CanReroll && FindAll<NCardRewardAlternativeButton>(selection).Count == 1,
                "native reward rerolled in place", ShortWait);
            await WaitSecondsAsync(0.6);
        }

        var cardSkip = FindAll<NCardRewardAlternativeButton>(selection).First();
        CheckRewardSkipLayout("rewards", selection, cardSkip, lootSkip);
        await CaptureAsync("rewards_02_skip_slot");
        int countBeforeSkip = player.Deck.Cards.Count;
        // A live combat room stays behind this fixture. In landscape the
        // compact hand overlaps the Skip button's Y range.
        BeforeRewardSkipTap();
        Rect2 windowBeforeSkip = lootWindow.GetGlobalRect();
        var windowFrames = new List<Rect2>();
        var skipFrames = new List<string>();
        Action sampleDrawn = () =>
        {
            if (!GodotObject.IsInstanceValid(lootWindow) || !lootWindow.IsVisibleInTree()) return;
            windowFrames.Add(lootWindow.GetGlobalRect());
            skipFrames.Add($"{lootSkip.GetGlobalRect().Position.X:F0},{lootSkip.GetGlobalRect().Position.Y:F0}/{lootSkip.Visible}");
        };
        RenderingServer.FramePostDraw += sampleDrawn;
        try
        {
            await _pointer.TapAsync(CenterOf(cardSkip));
            CheckRewardSkipHandUntouched("rewards");
            await WaitUntilAsync(() => !GodotObject.IsInstanceValid(selection) || !selection.IsInsideTree(),
                "returns from card Skip", ShortWait);
            await WaitSecondsAsync(0.7);
        }
        finally
        {
            RenderingServer.FramePostDraw -= sampleDrawn;
        }
        // Leaving the card reward only fades the loot panel back in: every frame it is drawn, it is where it was. Landscape
        // keeps the game's own return, which bounces the panel up from 100 units below.
        List<Rect2> moved = windowFrames.Where(rect => !SameRect(rect, windowBeforeSkip)).ToList();
        if (IsPortraitHost())
            _report.Check("rewards_loot_window_still_after_card_skip", windowFrames.Count > 0 && moved.Count == 0,
            $"before={windowBeforeSkip} frames={windowFrames.Count} moved={moved.Count} first={(moved.Count > 0 ? moved[0] : default)} " +
            $"skip=[{string.Join(" ", skipFrames.Distinct())}]");
        _report.Check("reward_card_skip_does_not_skip_loot", screen.IsVisibleInTree() && lootSkip.IsVisibleInTree()
            && player.Deck.Cards.Count == countBeforeSkip, "native card Skip returns to the loot list without granting a card");
        selection = await OpenReward(afterSkip: true);
        List<NGridCardHolder> offered = FindAll<NGridCardHolder>(selection).Where(h => !h.IsQueuedForDeletion()).ToList();
        _report.Check("reward_card_count", offered.Count == 3, $"cards={offered.Count}");
        CheckRewardCardsLayout("rewards", selection, offered);
        await CaptureAsync("rewards_02_cards");
        await TestCardHoverScaleAsync("reward_cards", offered);
        int deckBefore = player.Deck.Cards.Count;
        Log.Info($"[TEST] reward tap hitbox={offered[0].Hitbox.GetGlobalRect()} holder={offered[0].GetGlobalRect()}");
        Rect2 windowBeforeCard = lootWindow.GetGlobalRect();
        await TakeRewardCardAsync(offered[0]);
        await WaitUntilAsync(() => player.Deck.Cards.Count > deckBefore, "selected reward added to deck", ShortWait);
        _report.Check("reward_card_collection", player.Deck.Cards.Count == deckBefore + 1, $"deck={deckBefore}->{player.Deck.Cards.Count}");
        await WaitSecondsAsync(0.7);
        if (GodotObject.IsInstanceValid(lootWindow) && lootWindow.IsVisibleInTree())
            _report.Check("rewards_loot_window_keeps_its_frame_after_a_card", SameRect(windowBeforeCard, lootWindow.GetGlobalRect()),
                $"before={windowBeforeCard} after={lootWindow.GetGlobalRect()}");

        // The final boss offers no rewards: the game still opens a terminal rewards screen, fades its empty window out and
        // leaves only Proceed. No loot window may show.
        var empty = new RewardsSet(player);
        if (runState.CurrentRoom is { } emptyRoom) empty.EmptyForRoom(emptyRoom);
        NOverlayStack.Instance?.Clear();
        await WaitFramesAsync(4);
        _ = RunManager.Instance.RewardsSetSynchronizer.BeginRewardsSet(empty);
        NRewardsScreen? emptyScreen = NRewardsScreen.ShowScreen(empty, isTerminal: true, runState);
        if (emptyScreen is null)
        {
            _report.Fail("rewards_empty_set_hides_loot", "ShowScreen returned null for the empty set");
            return;
        }
        await WaitUntilAsync(() => emptyScreen.IsInsideTree() && emptyScreen.Visible, "empty rewards screen", MediumWait);
        await WaitSecondsAsync(1.0);
        Control emptyWindow = emptyScreen.GetNode<Control>("Rewards");
        await CaptureAsync("rewards_03_empty_terminal");
        _report.Check("rewards_empty_set_hides_loot",
            emptyWindow.Modulate.A <= 0.05f && emptyScreen.GetNode<Control>("ProceedButton").IsVisibleInTree(),
            $"windowAlpha={emptyWindow.Modulate.A:F2} rows={FindAll<NRewardButton>(emptyScreen).Count} complete={emptyScreen.IsComplete}");
    }

    private static bool SameRect(Rect2 a, Rect2 b) =>
        a.Position.DistanceTo(b.Position) <= 1f && Mathf.Abs(a.Size.X - b.Size.X) <= 1f && Mathf.Abs(a.Size.Y - b.Size.Y) <= 1f;

    /// <summary>
    /// A button sliding into view (the Skip/Proceed ribbon, as the game's own horizontal slide) is never seen off its final
    /// line: every frame it shows on the canvas, its top is where it settles. Sampled for <paramref name="frames"/> frames.
    /// </summary>
    private async Task CheckSlidesAlongItsLineAsync(Control button, string id, int frames)
    {
        var tops = new List<Rect2>();
        Vector2 canvas = GetViewportSize();
        for (int i = 0; i < frames; i++)
        {
            await WaitFramesAsync(1);
            if (!GodotObject.IsInstanceValid(button) || !button.IsVisibleInTree()) continue;
            Rect2 rect = button.GetGlobalRect();
            if (rect.End.X > 0f && rect.Position.X < canvas.X && rect.End.Y > 0f && rect.Position.Y < canvas.Y) tops.Add(rect);
        }
        if (tops.Count == 0)
        {
            _report.Fail(id, "the button never showed on the canvas");
            return;
        }
        float settled = tops[^1].Position.Y;
        var off = tops.Where(r => Mathf.Abs(r.Position.Y - settled) > 2f).ToList();
        _report.Check(id, off.Count == 0, $"settled={tops[^1]} frames={tops.Count} offLine={off.Count} first={(off.Count > 0 ? off[0] : tops[0])}");
    }
}
