using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// <c>screens</c> on both hosts: the native choose-a-card and card reward screens over a rest site, their cards, Skip and
/// Peek controls, and the hover-scale behaviour of a card holder. The cards' and Skip's phone sizes are each host's own
/// layout: those checks are partials implemented by the TouchSpire host (HostRewardChecks.cs) and dropped on the launcher,
/// where the choice-screen control geometry is the launcher's own.
/// </summary>
internal sealed partial class TestDriver
{
    private async Task RunScreensScenarioAsync(RunState runState)
    {
        await WaitFramesAsync(4);
        await RunManager.Instance.EnterRoomDebug(RoomType.RestSite, showTransition: false);
        await WaitUntilAsync(
            () => NRestSiteRoom.Instance is { } room
                  && GodotObject.IsInstanceValid(room)
                  && room.IsInsideTree(),
            "rest site room",
            MediumWait);
        await WaitFramesAsync(6);
        await CaptureAsync("screens_01_rest_site");
        await RunCardScreenTestsAsync(runState);
    }

    private async Task RunCardScreenTestsAsync(RunState runState)
    {
        Player player = runState.Players[0];
        List<CardModel> deckCards = player.Deck.Cards.Take(3).ToList();
        if (deckCards.Count < 3)
        {
            _report.Skip("card_screens", "deck does not have three cards");
            return;
        }

        NOverlayStack.Instance?.Clear();
        await WaitFramesAsync(4);

        var choose = NChooseACardSelectionScreen.ShowScreen(deckCards, canSkip: true);
        if (choose is null)
        {
            _report.Fail("choose_a_card_opens", "ShowScreen returned null");
            return;
        }

        await WaitUntilAsync(() => choose.IsInsideTree() && choose.Visible, "choose-a-card screen", MediumWait);
        await WaitFramesAsync(12);
        await CaptureAsync("screens_02_choose_a_card");

        List<NGridCardHolder> chooseHolders = FindAll<NGridCardHolder>(choose);
        _report.Check("choose_a_card_has_cards", chooseHolders.Count == deckCards.Count, $"cards={chooseHolders.Count}");
        // Read the native completion up front: the screen requires it before a card may be selected, and it is the direct
        // proof that a gesture did not select one.
        Task<IEnumerable<CardModel>> chooseSelection = choose.CardsSelected();

        NChoiceSelectionSkipButton? skip = FindFirst<NChoiceSelectionSkipButton>(choose);
        _report.Check("choose_a_card_has_skip", skip is not null, "skip button found");
        CheckCardScreensLayout("choose_a_card", choose, chooseHolders, skip);

        await TestCardHoverScaleAsync("choose_a_card", chooseHolders);
        // T4: a drag that starts on a card must not select it: the choice screen stays open and nothing is taken. The finger
        // leaves the cards at their top edge, so the release is outside every card.
        Vector2 chooseCentre = CenterOf(chooseHolders[0].Hitbox);
        Rect2 chooseBlock = chooseHolders.Select(holder => holder.GetGlobalRect()).Aggregate((a, b) => a.Merge(b));
        await _pointer.DragAsync(chooseCentre, new Vector2(chooseBlock.GetCenter().X, chooseBlock.Position.Y - 40f), 18);
        await WaitSecondsAsync(0.6);
        _report.Check("choose_a_card_swipe_from_card_does_not_select", !chooseSelection.IsCompleted && ChoiceSelectionIdle(chooseHolders[0])
            && choose.IsVisibleInTree()
            && chooseHolders.Count(holder => GodotObject.IsInstanceValid(holder) && holder.IsVisibleInTree()) == deckCards.Count,
            $"selected={chooseSelection.IsCompleted} armed={!ChoiceSelectionIdle(chooseHolders[0])} open={choose.IsVisibleInTree()} " +
            $"cards={chooseHolders.Count(holder => GodotObject.IsInstanceValid(holder))}");
        if (IsPortraitHost())
            CheckCombatChoiceControls("choose_a_card", choose, chooseHolders);

        NOverlayStack.Instance?.Clear();
        await WaitFramesAsync(6);

        List<CardCreationResult> options = deckCards
            .Select(card => new CardCreationResult(card))
            .ToList();
        var reward = NCardRewardSelectionScreen.ShowScreen(options, Array.Empty<CardRewardAlternative>());
        if (reward is null)
        {
            _report.Fail("card_reward_opens", "ShowScreen returned null");
            return;
        }

        await WaitUntilAsync(() => reward.IsInsideTree() && reward.Visible, "card reward screen", MediumWait);
        await WaitFramesAsync(12);
        await CaptureAsync("screens_03_card_reward");

        List<NGridCardHolder> rewardHolders = FindAll<NGridCardHolder>(reward);
        _report.Check("card_reward_has_cards", rewardHolders.Count == options.Count, $"cards={rewardHolders.Count}");
        CheckCardRewardScreenLayout("card_reward", reward, rewardHolders);

        await TestCardHoverScaleAsync("card_reward", rewardHolders);
    }

    /// <summary>
    /// A card holder keeps its grown size while it holds focus and after it lets go (the game's enlarge is driven by hover,
    /// the phone's by the tap): host-neutral, and also used by the upgrade and rewards fixtures.
    /// </summary>
    private async Task TestCardHoverScaleAsync(string prefix, List<NGridCardHolder> holders)
    {
        if (holders.Count == 0)
        {
            _report.Skip($"{prefix}_hover_scale", "no card holders");
            return;
        }

        NGridCardHolder holder = holders[0];
        float enlarged = holder.Scale.X;

        holder.GrabFocus();
        await WaitSecondsAsync(0.4);
        _report.Check(
            $"{prefix}_focus_keeps_scale",
            holder.Scale.X >= enlarged * 0.95f,
            $"enlarged={enlarged:F2} focused={holder.Scale.X:F2} hasFocus={holder.HasFocus()}");

        holder.ReleaseFocus();
        await WaitSecondsAsync(0.8);
        _report.Check(
            $"{prefix}_unfocus_keeps_scale",
            holder.Scale.X >= enlarged * 0.95f,
            $"enlarged={enlarged:F2} unfocused={holder.Scale.X:F2}");
    }
}
