using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Runs;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The launcher host's answers for the shared scenarios' host-specific input. The declarations live in HarnessCore.cs, which
/// this project compiles; the TouchSpire host implements them in HostRewardChecks.cs. In portrait on this host a first press
/// only selects the item (its native reticle, with its native hover tip) and the second tap on the same instance confirms
/// through the screen's own native commit path (card reward option, choose-a-card SelectHolder, treasure PickRelic); there is
/// no Choose button any more. A treasure chest whose every relic can be taken (a single player with no more visible relics
/// than picks left: a normal one-relic chest, or the Blitz chest's two relics for two rounds) keeps the game's own single
/// tap instead: its release takes the relic and no selection or reticle appears (<see cref="ReadTreasureChestContract"/>).
/// Landscape is the game's own single tap, so these
/// take the item with one press/release and check that no selection or reticle appears (the select-then-confirm pass is
/// portrait-only).
/// </summary>
internal sealed partial class TestDriver
{
    // The removed Choose button: no screen may still carry it.
    private const string ChooseButtonName = "PhoneChooseButton";

    private partial async Task TakeChoiceCardAsync(NGridCardHolder holder)
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            NChooseACardSelectionScreen landscapeScreen = FindAll<NChooseACardSelectionScreen>(_tree.Root)
                .First(s => s.IsVisibleInTree() && !s.IsQueuedForDeletion());
            await LandscapeTapAsync(holder.Hitbox, holder, "potion_reward_choice");
            await WaitUntilAsync(() => !ScreenAlive(landscapeScreen) || !landscapeScreen.IsVisibleInTree(),
                "landscape single tap takes the picker's card", ShortWait);
            _report.Pass("potion_reward_choice_landscape_single_tap_takes",
                "one tap answered the native picker; no selection was made");
            return;
        }
        int handBefore = GetHand().ActiveHolders.Count;
        NChooseACardSelectionScreen screen = FindAll<NChooseACardSelectionScreen>(_tree.Root)
            .First(s => s.IsVisibleInTree() && !s.IsQueuedForDeletion());
        int confirmsBefore = PhoneSelectConfirm.Confirms;
        await _pointer.TapAsync(CenterOf(holder.Hitbox));
        try
        {
            await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, holder),
                "launcher selects the tapped choice card", ShortWait);
        }
        catch (TimeoutException)
        {
            _report.Fail("potion_reward_choice_card_selected_not_taken",
                "the tap did not leave the card selected (it may have been taken, or the selection was let go)");
            return;
        }

        string selected = PhoneSelectConfirm.SelectedName ?? "none";
        _report.Check("potion_reward_choice_card_selected_not_taken",
            selected == holder.Name.ToString() && screen.IsVisibleInTree() && GetHand().ActiveHolders.Count == handBefore
            && ReticleNode(holder) is not null && TipShownUnder(holder)
            && screen.GetNodeOrNull<Control>(ChooseButtonName) is null,
            $"PhoneSelectConfirm selected={selected} holder={holder.Name}; the screen is still open with the card's tips, " +
            $"the card is not taken (hand {handBefore}) and there is no {ChooseButtonName}");
        await CaptureAsync("potion_reward_choice_selected");

        await _pointer.TapAsync(CenterOf(holder.Hitbox));
        await WaitUntilAsync(() => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree(),
            "launcher second tap takes the selected card and closes the picker", ShortWait);
        _report.Check("potion_reward_choice_second_tap_takes",
            PhoneSelectConfirm.Confirms == confirmsBefore + 1 && PhoneSelectConfirm.SelectedName is null,
            $"confirms={confirmsBefore}->{PhoneSelectConfirm.Confirms} selected={PhoneSelectConfirm.SelectedName ?? "none"}");
        await CaptureAsync("potion_reward_choice_taken");
    }

    private partial async Task TakeRewardCardAsync(NGridCardHolder holder)
    {
        NCardRewardSelectionScreen screen = FindAll<NCardRewardSelectionScreen>(_tree.Root)
            .Last(s => s.IsVisibleInTree() && !s.IsQueuedForDeletion());
        if (!SafeAreaCanvas.IsPortrait)
        {
            await LandscapeTapAsync(holder.Hitbox, holder, "reward_card");
            await WaitUntilAsync(() => !ScreenAlive(screen) || !screen.IsVisibleInTree(),
                "landscape single tap accepts the reward card", ShortWait);
            _report.Pass("reward_card_landscape_single_tap_takes",
                "one tap answered the native reward; no selection was made");
            return;
        }
        await _pointer.TapAsync(CenterOf(holder.Hitbox));
        try
        {
            await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, holder),
                "launcher selects the tapped reward card", ShortWait);
        }
        catch (TimeoutException)
        {
            _report.Fail("reward_card_first_tap_selects",
                "the tap did not leave the card selected (it may have been taken, or the selection was let go)");
            return;
        }
        _report.Check("reward_card_first_tap_selects",
            PhoneSelectConfirm.SelectedName == holder.Name.ToString() && screen.IsVisibleInTree()
            && ReticleNode(holder) is not null,
            $"selected={PhoneSelectConfirm.SelectedName ?? "none"} holder={holder.Name}; the reward screen is still open");
        await CaptureAsync("rewards_02_card_selected");
        await _pointer.TapAsync(CenterOf(holder.Hitbox));
        await WaitUntilAsync(() => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree(),
            "launcher second tap takes the selected reward card", ShortWait);
        _report.Check("reward_card_second_tap_takes", PhoneSelectConfirm.SelectedName is null,
            $"confirms={PhoneSelectConfirm.Confirms} selected={PhoneSelectConfirm.SelectedName ?? "none"}");
    }

    private partial async Task TakeTreasureRelicAsync(NTreasureRoomRelicHolder holder)
    {
        string holderName = holder.Name.ToString();
        if (!SafeAreaCanvas.IsPortrait)
        {
            await LandscapeTapAsync(holder, holder, "treasure_relic");
            _report.Pass("treasure_relic_landscape_single_tap_takes",
                $"one tap on {holderName} took the native relic; no selection was made");
            return;
        }
        NTreasureRoomRelicCollection? collection = TreasureCollectionOf(holder);
        if (collection is null)
        {
            _report.Fail("treasure_relic_collection",
                $"the relic holder {holderName} is not inside an open treasure chest");
            return;
        }
        TreasureChestContract contract = ReadTreasureChestContract(collection);
        if (!contract.NeedsConfirm)
        {
            Player? player = RunManager.Instance.DebugOnlyGetState() is { } state
                ? LocalContext.GetMe(state)
                : null;
            if (player is null)
            {
                _report.Fail("treasure_relic_single_tap_player", "no local player to award the chest's relic to");
                return;
            }
            await CheckTreasureSingleTapAsync("treasure_relic", holder, player);
            return;
        }
        _report.Check("treasure_relic_choice_fixture",
            contract.Players == 1 && contract.PicksLeft is int picksLeft && contract.Holders > picksLeft,
            $"{contract.Detail}; the shared treasure fixture must be a single player with more relics than picks left");
        await _pointer.TapAsync(CenterOf(holder));
        try
        {
            await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, holder),
                "launcher selects the tapped treasure relic", ShortWait);
        }
        catch (TimeoutException)
        {
            _report.Fail("treasure_relic_first_tap_selects",
                "the tap did not leave the relic selected (it may have been taken, or the selection was let go)");
            return;
        }
        _report.Check("treasure_relic_first_tap_selects",
            ScreenAlive(holder) && PhoneSelectConfirm.SelectedName == holderName &&
            ReticleNode(holder) is not null,
            $"selected={PhoneSelectConfirm.SelectedName ?? "none"} holder={holderName}");
        await CaptureAsync("treasure_relic_selected");
        if (!ScreenAlive(holder))
        {
            _report.Fail("treasure_relic_second_tap_takes", "the relic holder is gone before the second tap");
            return;
        }
        await _pointer.TapAsync(CenterOf(holder));
    }

    private partial async Task CheckTreasurePressAwardAsync(string name, NTreasureRoomRelicHolder holder, Player player)
    {
        int relicsBefore = player.Relics.Count;
        Vector2 point = CenterOf(holder);
        if (!SafeAreaCanvas.IsPortrait)
        {
            // Landscape is the game's own single tap: the release of the press takes the relic, and no selection or
            // reticle may appear on the way.
            int confirms = PhoneSelectConfirm.Confirms;
            await _pointer.PressAsync(point);
            await WaitSecondsAsync(0.38);
            _report.Check(name + "_landscape_no_selection",
                PhoneSelectConfirm.SelectedOwner is null && player.Relics.Count == relicsBefore &&
                PhoneSelectConfirm.Confirms == confirms,
                $"selected={PhoneSelectConfirm.SelectedName ?? "none"} relics={relicsBefore}->{player.Relics.Count}; " +
                "the portrait selection never runs in landscape");
            await CaptureAsync(name + "_inspection");
            await _pointer.ReleaseAsync(point);
            await WaitUntilAsync(() => player.Relics.Count > relicsBefore, "landscape release takes the relic", ShortWait);
            _report.Check(name + "_landscape_release_takes", player.Relics.Count == relicsBefore + 1,
                $"relics={relicsBefore}->{player.Relics.Count}");
            await CaptureAsync(name + "_picked");
            return;
        }
        NTreasureRoomRelicCollection? collection = TreasureCollectionOf(holder);
        if (collection is null)
        {
            _report.Fail(name + "_collection", $"the relic holder {holder.Name} is not inside an open treasure chest");
            return;
        }
        TreasureChestContract contract = ReadTreasureChestContract(collection);
        if (!contract.NeedsConfirm)
        {
            await CheckTreasureSingleTapAsync(name, holder, player);
            return;
        }
        await _pointer.PressAsync(point);
        bool selected = true;
        try
        {
            await WaitUntilAsync(() => ReferenceEquals(PhoneSelectConfirm.SelectedOwner, holder),
                "launcher selects the pressed treasure relic", ShortWait);
        }
        catch (TimeoutException)
        {
            selected = false;
        }
        await WaitSecondsAsync(0.38);
        _report.Check(name + "_first_press_only_selects",
            selected && ScreenAlive(holder) && player.Relics.Count == relicsBefore &&
            ReticleNode(holder) is not null,
            $"selectedOnPress={selected} relics={relicsBefore}->{player.Relics.Count}; nothing may be awarded before the release");
        await CaptureAsync(name + "_inspection");
        // The release is harmless only while the selection is still held: dropping it before the release restores the
        // game's own take, which then fires on this release (the launcher's selection identity failing was the observed
        // cause of this step failing). Record the state so the detail says which of the two happened.
        bool selectedAtRelease = ReferenceEquals(PhoneSelectConfirm.SelectedOwner, holder);
        await _pointer.ReleaseAsync(point);
        await WaitFramesAsync(4);
        _report.Check(name + "_release_does_not_take", player.Relics.Count == relicsBefore,
            $"relics={relicsBefore}->{player.Relics.Count}; selectionHeldAtRelease={selectedAtRelease} " +
            "(the selecting press must stay suppressed until the release)");
        if (!ScreenAlive(holder))
        {
            _report.Fail(name + "_second_tap_takes_one",
                "the relic holder is gone: the release took the relic instead of the second tap");
            return;
        }
        await _pointer.TapAsync(point);
        await WaitUntilAsync(() => player.Relics.Count > relicsBefore, "second tap picks the treasure relic", ShortWait);
        _report.Check(name + "_second_tap_takes_one", player.Relics.Count == relicsBefore + 1,
            $"relics={relicsBefore}->{player.Relics.Count}");
        await CaptureAsync(name + "_picked");
    }

    /// <summary>
    /// The game's own single tap on a chest whose every relic can be taken: the press selects nothing and shows no reticle,
    /// and its release takes exactly one relic. Used for a single player's normal one-relic chest and for any chest that
    /// offers no choice (a Blitz chest with as many relics as rounds).
    /// </summary>
    private async Task CheckTreasureSingleTapAsync(string name, NTreasureRoomRelicHolder holder, Player player)
    {
        int relicsBefore = player.Relics.Count;
        int confirms = PhoneSelectConfirm.Confirms;
        Vector2 point = CenterOf(holder);
        await _pointer.PressAsync(point);
        await WaitSecondsAsync(0.38);
        bool selectedOnPress = PhoneSelectConfirm.SelectedOwner is not null;
        bool reticleOnPress = ReticleNode(holder) is not null;
        _report.Check(name + "_single_tap_no_selection",
            !selectedOnPress && !reticleOnPress && player.Relics.Count == relicsBefore &&
            PhoneSelectConfirm.Confirms == confirms,
            $"selected={PhoneSelectConfirm.SelectedName ?? "none"} reticle={reticleOnPress} " +
            $"relics={relicsBefore}->{player.Relics.Count} confirms={confirms}->{PhoneSelectConfirm.Confirms}; " +
            "every relic is claimable, so the game's own release takes it and nothing is selected");
        await CaptureAsync(name + "_inspection");
        await _pointer.ReleaseAsync(point);
        try
        {
            await WaitUntilAsync(() => player.Relics.Count > relicsBefore, "native chest release takes the relic", ShortWait);
        }
        catch (TimeoutException)
        {
            // Reported by the check below with the counts.
        }
        _report.Check(name + "_single_tap_takes_when_all_claimable",
            player.Relics.Count == relicsBefore + 1 && PhoneSelectConfirm.SelectedOwner is null &&
            PhoneSelectConfirm.Confirms == confirms && ReticleNode(holder) is null,
            $"relics={relicsBefore}->{player.Relics.Count} selected={PhoneSelectConfirm.SelectedName ?? "none"} " +
            $"confirms={confirms}->{PhoneSelectConfirm.Confirms} reticle={ReticleNode(holder) is not null}");
        await CaptureAsync(name + "_picked");
    }

    /// <summary>The open chest a relic holder belongs to (the collection is its holder layout's parent).</summary>
    private static NTreasureRoomRelicCollection? TreasureCollectionOf(Node holder)
    {
        for (Node? node = holder; node is not null; node = node.GetParent())
            if (node is NTreasureRoomRelicCollection collection) return collection;
        return null;
    }

    /// <summary>
    /// Landscape plays as the desktop game: the game's own release takes the item. A press must not select or show the
    /// reticle — the whole select-then-confirm pass is portrait-only — so the state is read while the finger is still down.
    /// </summary>
    private async Task LandscapeTapAsync(Control target, Control? reticleOwner, string step)
    {
        Control? selectedBefore = PhoneSelectConfirm.SelectedOwner;
        int confirms = PhoneSelectConfirm.Confirms;
        Vector2 point = CenterOf(target);
        await _pointer.PressAsync(point);
        await WaitFramesAsync(2);
        bool selectedOnPress = !ReferenceEquals(PhoneSelectConfirm.SelectedOwner, selectedBefore);
        bool hasReticle = reticleOwner is not null && ScreenAlive(reticleOwner) && ReticleNode(reticleOwner) is not null;
        await _pointer.ReleaseAsync(point);
        await WaitFramesAsync(2);
        _report.Check(step + "_landscape_no_selection",
            !selectedOnPress && !hasReticle && PhoneSelectConfirm.Confirms == confirms,
            $"selectedOnPress={selectedOnPress} reticle={hasReticle} " +
            $"confirms={confirms}->{PhoneSelectConfirm.Confirms}; landscape takes on the native release");
    }

    private partial Vector2 PointerParkingPosition() => new(20f, GetViewportSize().Y - 10f);
}
