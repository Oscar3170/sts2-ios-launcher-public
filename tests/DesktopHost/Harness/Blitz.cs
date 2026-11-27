using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Nodes.TreasureRooms;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The Blitz run: Neow's three-card blessing, the native Blitz chest (two relics for two rounds, so every relic is taken on
/// the game's own single tap), the native Blitz merchant room and its stock, the map marker, the fight and its loot, the
/// card reward and Proceed. The chest runs before the merchant and the fight so a random fight relic cannot change its
/// fixture (Silver Crucible would). The header band, the merchant room's framing, the stock inside the cloth, the action
/// lane and the loot panel are host partials (Host/LauncherShopChecks.cs), and a card reward is taken through
/// <see cref="TakeBlitzRewardCardAsync"/> because the launcher confirms on a second tap.
/// </summary>
internal sealed partial class TestDriver
{
    private async Task RunBlitzAsync(RunState state)
    {
        _report.Check("blitz_native_run", state.GameMode == GameMode.Blitz, $"mode={state.GameMode}");
        await WaitUntilAsync(() => NEventRoom.Instance?.Layout is NAncientEventLayout a && a.OptionButtons.Any(), "Blitz Neow", MediumWait);
        var ancient = (NAncientEventLayout)NEventRoom.Instance!.Layout!;
        await AdvanceBlitzDialogueAsync(ancient);
        CheckBlitzMap("blitz_neow");
        await CaptureAsync("blitz_01_neow");
        await _pointer.TapAsync(CenterOf(ancient.OptionButtons.Single()));
        await WaitUntilAsync(() => FindAll<NSimpleCardSelectScreen>(_tree.Root).Any(s => s.IsVisibleInTree()),
            "Blitz Neow choose-three grid", MediumWait);
        await WaitSecondsAsync(0.65);
        var picker = FindAll<NSimpleCardSelectScreen>(_tree.Root).Single(s => s.IsVisibleInTree());
        var grid = picker.GetNode<NCardGrid>("%CardGrid");
        var cards = grid.CurrentlyDisplayedCardHolders.Where(h => h.IsVisibleInTree()).Take(3).ToArray();
        if (IsPortraitHost()) CheckBlitzChooseThreeLayout(grid, cards);
        await CaptureAsync("blitz_02_choose_three");
        var deck = state.Players[0].Deck;
        int before = deck.Cards.Count;
        foreach (var card in cards) await _pointer.TapAsync(CenterOf(card.Hitbox));
        await ConfirmBlitzBlessingAsync(picker);
        // The native blessing completes on the third pick (or on its Confirm button).
        await WaitUntilAsync(() => !GodotObject.IsInstanceValid(picker) || !picker.IsInsideTree(), "Blitz choice complete", ShortWait);
        await WaitUntilAsync(() => deck.Cards.Count == before + 3, "Blitz adds three chosen cards", ShortWait);
        await WaitSecondsAsync(1.0);

        // The chest runs before the merchant and the fight so the Blitz fixture is the run's own, not one altered by a
        // random fight relic: Silver Crucible makes ShouldGenerateTreasure false and would leave this chest with one relic.
        // Its two relics cannot disturb the checks after it (the merchant and loot layouts read geometry, not the pouch).
        await CheckBlitzTreasureChestAsync(state);

        SaveManager.Instance.MarkFtueAsComplete("merchant_ftue");
        await RunManager.Instance.EnterRoomDebug(RoomType.Shop, showTransition: false);
        await WaitUntilAsync(() => NMerchantRoom.Instance is NBlitzMerchantRoom m && m.IsInsideTree(), "native Blitz merchant", MediumWait);
        await WaitSecondsAsync(0.8);
        var room = (NBlitzMerchantRoom)NMerchantRoom.Instance!;
        CheckBlitzMap("blitz_merchant");
        if (IsPortraitHost()) CheckBlitzMerchantLayout(room);
        await CaptureAsync("blitz_03_merchant_room");
        await _pointer.TapAsync(CenterOf(room.MerchantButton));
        var inventory = room.Inventory;
        await WaitUntilAsync(() => inventory.IsOpen, "Blitz inventory open", ShortWait);
        await WaitSecondsAsync(0.85);
        var slots = inventory.GetAllSlots().OfType<NBlitzMerchantSlot>().Where(s => s.IsVisibleInTree()).ToArray();
        _report.Check("blitz_inventory_has_native_slots", slots.Length > 0, $"slots={slots.Length}");
        if (IsPortraitHost()) CheckBlitzInventoryLayout(inventory, slots);
        foreach (var slot in slots.Take(3))
        {
            Vector2 resting = slot.Scale;
            await _pointer.MoveAsync(CenterOf(slot.Hitbox), pressed: false);
            await WaitSecondsAsync(0.25);
            Vector2 hovered = slot.Scale;
            await _pointer.MoveAsync(new Vector2(16f, GetViewportSize().Y * 0.58f), pressed: false);
            await WaitSecondsAsync(0.6);
            _report.Check("blitz_stock_returns_to_base_" + slot.GetIndex(), hovered.X > resting.X
                && slot.Scale.IsEqualApprox(resting), $"before={resting}, hovered={hovered}, after={slot.Scale}");
        }
        await CaptureAsync("blitz_04_merchant_stock_after_hover");
        await _pointer.TapAsync(CenterOf(inventory.GetNode<Control>("%BackButton")));
        await WaitUntilAsync(() => !inventory.IsOpen && room.ProceedButton.IsEnabled, "Blitz native shop Back", ShortWait);
        await WaitSecondsAsync(0.8);
        var map = NRun.Instance!.GlobalUi.TopBar.BlitzMap;
        var marker = map.GetNode<Control>("%BlitzMapMarker");
        float beforeX = marker.GlobalPosition.X;
        int visited = state.VisitedMapCoords.Count;
        await _pointer.TapAsync(CenterOf(room.ProceedButton));
        await WaitUntilAsync(() => state.VisitedMapCoords.Count > visited && NMerchantRoom.Instance != room,
            "Blitz Proceed travels to the next room", MediumWait);
        await WaitSecondsAsync(1.1);
        _report.Check("blitz_native_marker_advances", marker.GlobalPosition.X > beforeX + 20f,
            $"marker={beforeX:F1}->{marker.GlobalPosition.X:F1}");
        CheckBlitzMap("blitz_after_proceed");
        await CaptureAsync("blitz_05_after_proceed");
        await CheckBlitzFightRewardsAsync(state);
    }

    /// <summary>
    /// The Blitz run's native chest. A single-player Blitz chest rolls two relics for two rounds (v0.114's
    /// <c>BeginRelicPicking</c> adds one per player and keeps <c>_rounds = 2</c>; v0.113.1's adds one to its one and keeps
    /// <c>_roundsLeft = 2</c>), so no relic is a choice and every relic must keep the game's own single tap. The fixture is
    /// read first and the phase stops if it is not that exact shape: a different chest (a normal one, or a build that made
    /// the Blitz relics a choice) must never be tapped as if it were the all-claimable fixture. Round one takes relic A and
    /// the collection retires its holder; round two re-reads the remaining holder and round, takes relic B, and the native
    /// award animation then fades the collection out and turns Proceed on.
    /// </summary>
    private async Task CheckBlitzTreasureChestAsync(RunState state)
    {
        SaveManager.Instance.MarkFtueAsComplete("obtain_relic_ftue");
        Player player = state.Players[0];
        int relicsBefore = player.Relics.Count;

        await RunManager.Instance.EnterRoomDebug(RoomType.Treasure, showTransition: false);
        await WaitUntilAsync(() => FindFirst<NTreasureRoom>(_tree.Root) is { } r && r.IsInsideTree(),
            "Blitz treasure room", MediumWait);
        NTreasureRoom room = FindFirst<NTreasureRoom>(_tree.Root)!;
        NTreasureRoomRelicCollection collection = room.GetNode<NTreasureRoomRelicCollection>("%RelicCollection");
        await WaitSecondsAsync(0.5);
        await _pointer.TapAsync(CenterOf(room.GetNode<NTreasureButton>("%Chest")));
        await WaitUntilAsync(() => collection.IsVisibleInTree() && collection.HoldersInUse.Count > 0,
            "Blitz chest opens", ShortWait);
        await WaitUntilAsync(() => collection.HoldersInUse.All(h => h.MouseFilter != Control.MouseFilterEnum.Ignore),
            "Blitz chest relics accept touches", ShortWait);
        await WaitSecondsAsync(0.4);

        TreasureChestContract fixture = ReadTreasureChestContract(collection);
        bool expected = state.GameMode == GameMode.Blitz && fixture.Players == 1 && fixture.Holders == 2 &&
                        fixture.PicksLeft == 2 && !fixture.NeedsConfirm;
        _report.Check("treasure_blitz_fixture", expected, $"mode={state.GameMode}; {fixture.Detail}");
        if (!expected) return;
        await CaptureAsync("blitz_treasure_01_fixture");

        NTreasureRoomRelicHolder first = collection.HoldersInUse[0];
        var firstRelic = first.Relic.Model.Id;
        await CheckTreasurePressAwardAsync("treasure_blitz_round_1", first, player);
        _report.Check("treasure_blitz_round_1_awarded",
            player.Relics.Count == relicsBefore + 1 && player.Relics.Any(r => r.Id == firstRelic),
            $"relics={relicsBefore}->{player.Relics.Count} relic={firstRelic}");

        // Round two is a new native round: the award animation removes the taken holder and re-enables the one left, and
        // the synchronizer counts one round fewer. Read both again before the second tap.
        bool roundTwoReady = await WaitForAsync(() => collection.HoldersInUse.Count == 1,
            "Blitz round 1 retires the taken relic", ShortWait);
        if (roundTwoReady)
            roundTwoReady = await WaitForAsync(
                () => collection.HoldersInUse[0].MouseFilter != Control.MouseFilterEnum.Ignore,
                "Blitz round 2's relic accepts touches", ShortWait);
        _report.Check("treasure_blitz_round_2_ready", roundTwoReady,
            $"holders={collection.HoldersInUse.Count} " +
            $"mouseFilter={(collection.HoldersInUse.Count > 0 ? collection.HoldersInUse[0].MouseFilter.ToString() : "none")}");
        if (!roundTwoReady) return;
        await WaitSecondsAsync(0.4);

        TreasureChestContract round2 = ReadTreasureChestContract(collection);
        _report.Check("treasure_blitz_round_2_fixture",
            round2.Players == 1 && round2.Holders == 1 && round2.PicksLeft == 1 && !round2.NeedsConfirm,
            round2.Detail);
        await CaptureAsync("blitz_treasure_02_round_1_taken");

        NTreasureRoomRelicHolder last = collection.HoldersInUse[0];
        var lastRelic = last.Relic.Model.Id;
        await CheckTreasurePressAwardAsync("treasure_blitz_round_2", last, player);

        bool hidden = await WaitForAsync(() => !collection.Visible && !collection.IsVisibleInTree(),
            "Blitz chest collection hides after the last relic", ShortWait);
        await WaitSecondsAsync(0.2);
        _report.Check("treasure_blitz_two_relics_awarded",
            player.Relics.Count == relicsBefore + 2 &&
            player.Relics.Any(r => r.Id == firstRelic) &&
            player.Relics.Any(r => r.Id == lastRelic),
            $"relics={relicsBefore}->{player.Relics.Count} relics={firstRelic},{lastRelic}");
        _report.Check("treasure_blitz_award_animation_end",
            hidden && !collection.Visible && !collection.IsVisibleInTree() &&
            RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics is null,
            $"collection visible={collection.Visible} inTree={collection.IsVisibleInTree()} " +
            $"currentRelics={(RunManager.Instance.TreasureRoomRelicSynchronizer.CurrentRelics is null ? "none" : "still set")}");
        _report.Check("treasure_blitz_proceed_ready",
            room.ProceedButton.IsEnabled && !room.ProceedButton.IsSkip,
            $"enabled={room.ProceedButton.IsEnabled} skip={room.ProceedButton.IsSkip}");
        await CaptureAsync("blitz_treasure_03_claimed");
    }

    /// <summary>
    /// The blessing's own Confirm button. <c>NSimpleCardSelectScreen</c> completes on the third pick unless the profile
    /// requires manual confirmation, and then it waits with "%Confirm" enabled; pressing it is the native commit path and
    /// a no-op once the screen has already closed itself.
    /// </summary>
    private async Task ConfirmBlitzBlessingAsync(NSimpleCardSelectScreen picker)
    {
        if (!GodotObject.IsInstanceValid(picker) || !picker.IsInsideTree()) return;
        if (picker.GetNodeOrNull<NConfirmButton>("%Confirm") is not { Visible: true } confirm) return;
        bool ready = await WaitForAsync(() => !GodotObject.IsInstanceValid(picker) || !picker.IsInsideTree() || confirm.IsEnabled,
            "Blitz blessing confirm enabled", ShortWait);
        if (!ready || !GodotObject.IsInstanceValid(picker) || !picker.IsInsideTree()) return;
        await _pointer.TapAsync(CenterOf(confirm));
    }

    /// <summary>
    /// The fixture's Neow talks before its options appear. This is the same tap-the-hitbox loop as the ancient scenarios'
    /// helper, kept here because this scenario also compiles into the launcher host, which does not link AncientOptions.cs.
    /// </summary>
    private async Task AdvanceBlitzDialogueAsync(NAncientEventLayout layout)
    {
        Control hitbox = layout.GetNode<Control>("%DialogueHitbox");
        for (int i = 0; i < 12 && hitbox.IsVisibleInTree(); i++)
        {
            await _pointer.TapAsync(CenterOf(hitbox));
            await WaitSecondsAsync(0.25);
        }
        await WaitUntilAsync(() => !hitbox.IsVisibleInTree(), "ancient dialogue reveals rewards", ShortWait);
        await WaitSecondsAsync(1.15);
    }

    private async Task CheckBlitzFightRewardsAsync(RunState state)
    {
        await WaitUntilAsync(() => NCombatRoom.Instance is not null && IsPlayerPlayPhase(), "Blitz fight after merchant", MediumWait);
        var fight = NCombatRoom.Instance!;
        await CreatureCmd.Kill(fight.CreatureNodes.Select(c => c.Entity).Where(c => c.IsMonster && !c.IsPet && c.IsAlive).ToArray(), force: true);
        // The fixture kills outside a card action; evaluate the same native
        // win condition that the battle loop checks after an action.
        _report.Check("blitz_native_victory", await CombatManager.Instance.CheckWinCondition(), "defeated the native encounter");
        await WaitUntilAsync(() => FindAll<NRewardsScreen>(_tree.Root).Any(s => s.IsVisibleInTree()), "native Blitz post-fight rewards", MediumWait);
        await WaitSecondsAsync(0.8);
        var loot = FindAll<NRewardsScreen>(_tree.Root).Single(s => s.IsVisibleInTree());
        var action = loot.GetNode<NProceedButton>("ProceedButton");
        var panel = loot.GetNode<Control>("Rewards");
        _report.Check("blitz_loot_shows_skip", action.IsSkip && action.IsEnabled, $"skip={action.IsSkip} enabled={action.IsEnabled}");
        CheckBlitzLootAction("blitz_fight_skip", action, panel.GetGlobalRect());
        var skipCenter = CenterOf(action);
        if (IsPortraitHost()) CheckBlitzLootLayout(loot, panel);
        await CaptureAsync("blitz_06_fight_loot_skip");

        while (!loot.IsComplete)
        {
            var reward = FindAll<NRewardButton>(loot).First(b => b.IsVisibleInTree() && !b.IsQueuedForDeletion());
            bool isCardReward = reward.Reward is CardReward;
            await _pointer.TapAsync(CenterOf(reward));
            if (isCardReward)
            {
                await WaitUntilAsync(() => FindAll<NCardRewardSelectionScreen>(_tree.Root).Any(s => s.IsVisibleInTree()), "Blitz earned card choice", ShortWait);
                await WaitSecondsAsync(0.55);
                var cards = FindAll<NCardRewardSelectionScreen>(_tree.Root).Single(s => s.IsVisibleInTree());
                await TakeBlitzRewardCardAsync(cards, FindAll<NGridCardHolder>(cards).First());
            }
            await WaitUntilAsync(() => !GodotObject.IsInstanceValid(reward) || !reward.IsInsideTree(), "Blitz reward claimed", ShortWait);
            await WaitSecondsAsync(0.55);
        }
        _report.Check("blitz_loot_shows_proceed", !action.IsSkip && action.IsEnabled, $"skip={action.IsSkip} enabled={action.IsEnabled}");
        CheckBlitzLootAction("blitz_fight_proceed", action, null);
        if (IsPortraitHost()) CheckBlitzSkipProceedPosition(skipCenter, CenterOf(action));
        await CaptureAsync("blitz_07_fight_proceed");
        int visited = state.VisitedMapCoords.Count;
        await _pointer.TapAsync(CenterOf(action));
        await WaitUntilAsync(() => state.VisitedMapCoords.Count > visited && NCombatRoom.Instance != fight,
            "Blitz post-fight Proceed advances to the next room", MediumWait);
        _report.Pass("blitz_postfight_proceed_travels");
    }

    private void CheckBlitzMap(string name)
    {
        var topBar = NRun.Instance!.GlobalUi.TopBar;
        NBlitzTopBarMap map = topBar.BlitzMap;
        _report.Check(name + "_native_map_points", map.MapPoints.Count > 1, $"points={map.MapPoints.Count}");
        if (!IsPortraitHost()) return;
        CheckBlitzHeaderLayout(name, topBar);
    }
}
