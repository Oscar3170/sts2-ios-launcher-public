using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// <c>shop-states</c>: the merchant's exhausted and sold-out states. Three native states were never reached
/// by the shop scenarios: a Blitz merchant after all its picks are spent (used slots with their art gone, the rest
/// dimmed and untappable, the choices-remaining line changing and, on the launcher, still fitting the cloth), the
/// <c>TwoMoreChoices</c> entry that raises the count again, a normal shop with every card, relic and potion sold and the
/// removal used, and a shop entered in a later act.
/// <para>
/// Every layout answer is a host partial: the footer's fit in <c>CheckBlitzFooterLayout</c> and the leftover selection
/// reticle in <c>ShopHighlightCount</c> live in Host/LauncherShopChecks.cs. Purchases go through
/// <see cref="ConfirmShopSlotAsync"/>, the launcher's first-press-select / second-tap-confirm flow.
/// </para>
/// <para>
/// The Blitz stock is seeded from a fixture: the merchant's six random entries can open card, relic or potion screens
/// mid-count, so five slots are replaced by native entry types whose select completes without a blocking screen
/// (<c>RandomCardUpgrades</c>, <c>FullHealAndMaxHp</c> and <c>TwoMoreChoices</c>). The sixth slot keeps its native entry
/// and is the one the exhausted merchant leaves disabled. The inventory, the slots, the purchase path and the native
/// status transitions are the game's own.
/// </para>
/// </summary>
internal sealed partial class TestDriver
{
    private const string ShopStatesPrefix = "shop_states";
    private static readonly BindingFlags ShopStatesInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    // Numbered so the footer's repeated checks keep distinct step names.
    private int _shopStatesFooterChecks;

    private async Task RunShopStatesAsync(RunState state)
    {
        Player player = state.Players[0];
        SaveManager.Instance.MarkFtueAsComplete("merchant_ftue");
        // Ascension 10 starts one potion slot short, which would refuse the shop's third potion; the sold-out fixture
        // needs room for every one of them (the potion-belt relics raise the same count).
        if (player.MaxPotionCount < 3)
            await PlayerCmd.GainMaxPotionCount(3 - player.MaxPotionCount, player);
        await RunBlitzShopStatesAsync(state, player);
        await RunSoldOutShopStatesAsync(player);
        await RunActShopStatesAsync(state);
        _report.Skip("shop_states_multiplayer_variants",
            "a party of 2-4 characters in the merchant room and a second player's Blitz picks need a real multiplayer session");
        _report.Pass("shop_states_completed", "Blitz exhaustion, sold-out shop and act 2/3 shops visited");
    }

    // ---- Gap 10: an exhausted Blitz merchant and the TwoMoreChoices entry ---------------------------------------------

    private async Task RunBlitzShopStatesAsync(RunState state, Player player)
    {
        PropertyInfo? gameMode = typeof(RunState).GetProperty("GameMode", ShopStatesInstance);
        if (gameMode?.SetMethod is null)
        {
            _report.Fail("shop_states_blitz_mode_fixture", "RunState.GameMode has no settable property");
            return;
        }
        // The room and its inventory are created from RunState.GameMode in MerchantRoom.EnterInternal and
        // MerchantInventory.Setup; the fixture flips it for the single room entry so the ordinary run can also cover the
        // normal shop below.
        gameMode.SetValue(state, GameMode.Blitz);
        NBlitzMerchantRoom? room = null;
        try
        {
            await RunManager.Instance.EnterRoomDebug(RoomType.Shop, showTransition: false);
            await WaitUntilAsync(() => NMerchantRoom.Instance is NBlitzMerchantRoom m && m.IsInsideTree(),
                "Blitz merchant room", MediumWait);
            room = (NBlitzMerchantRoom)NMerchantRoom.Instance!;
        }
        finally
        {
            gameMode.SetValue(state, GameMode.Standard);
        }
        if (room is null)
        {
            _report.Fail("shop_states_blitz_room_native", "the Blitz merchant room did not appear");
            return;
        }
        await WaitSecondsAsync(1.0);
        NMerchantInventory inventory = room.Inventory;
        MerchantInventory? model = inventory.Inventory;
        _report.Check("shop_states_blitz_room_native", model?.IsBlitzShop == true,
            $"inventory={model?.GetType().Name} entries={model?.BlitzEntries.Count}");
        if (model is null)
        {
            _report.Fail("shop_states_blitz_inventory", "the Blitz room has no merchant inventory");
            return;
        }
        if (IsPortraitHost()) CheckBlitzMerchantLayout(room);
        await CaptureAsync(ShopStatesPrefix + "_01_blitz_room");

        await _pointer.TapAsync(CenterOf(room.MerchantButton));
        if (!await WaitForAsync(() => inventory.IsOpen, "Blitz inventory open", MediumWait))
        {
            _report.Fail("shop_states_blitz_inventory_open", "the Blitz merchant's inventory did not open");
            return;
        }
        await WaitSecondsAsync(0.85);
        NBlitzMerchantSlot[] slots = inventory.GetAllSlots().OfType<NBlitzMerchantSlot>()
            .Where(slot => slot.IsVisibleInTree()).ToArray();
        _report.Check("shop_states_blitz_six_slots_three_picks", slots.Length == 6 && model.BlitzChoicesRemaining == 3,
            $"slots={slots.Length} remaining={model.BlitzChoicesRemaining}");
        if (IsPortraitHost()) CheckBlitzInventoryLayout(inventory, slots);
        Control? footer = inventory.GetNodeOrNull<Control>("%BlitzSelectionText");
        CheckBlitzFooter(inventory, footer, model.BlitzChoicesRemaining, "initial");
        await CaptureAsync(ShopStatesPrefix + "_02_blitz_stock");

        List<BlitzMerchantEntry> picks = InstallBlitzPurchaseFixture(inventory, room, player);
        if (picks.Count != 5)
        {
            _report.Fail("shop_states_blitz_fixture", $"the fixture installed {picks.Count} of 5 entries");
            return;
        }
        await WaitSecondsAsync(0.45);
        _report.Check("shop_states_blitz_fixture_leaves_a_native_slot",
            slots.Length == 6 && slots[5].Entry is BlitzMerchantEntry { IsCursed: true }
                && picks.All(pick => slots.Any(slot => ReferenceEquals(slot.Entry, pick))),
            string.Join("; ", slots.Select(slot => slot.Entry.GetType().Name)));
        await CaptureAsync(ShopStatesPrefix + "_02a_blitz_fixture_stock");

        // Two ordinary picks, the count-raising entry, then the two picks that spend the raised count: the line reads
        // 3, 2, 1, 2 (the entry adds two and costs one), 1, none, and the slot left over turns disabled.
        int[] expected = { 2, 1, 2, 1, 0 };
        string[] names = { "random_upgrades_one", "full_heal_one", "two_more_choices", "random_upgrades_two", "full_heal_two" };
        for (int i = 0; i < picks.Count; i++)
        {
            var slot = inventory.GetAllSlots().OfType<NBlitzMerchantSlot>()
                .FirstOrDefault(candidate => ReferenceEquals(candidate.Entry, picks[i]));
            if (slot is null)
            {
                _report.Fail("shop_states_blitz_pick_" + names[i], "the fixture's slot is missing from the inventory");
                return;
            }
            await ConfirmShopSlotAsync(slot);
            bool counted = await WaitForAsync(() => model.BlitzChoicesRemaining == expected[i],
                $"Blitz choices remaining {expected[i]}", MediumWait);
            bool used = await WaitForAsync(() => !slot.Entry.IsStocked, "Blitz entry marked used", MediumWait);
            bool footerText = await WaitForAsync(() => inventory.GetNodeOrNull<RichTextLabel>("%BlitzSelectionText")?.Text
                    == BlitzFooterExpectedText(expected[i]), "Blitz footer text", ShortWait);
            await WaitSecondsAsync(0.4);
            _report.Check("shop_states_blitz_pick_" + names[i], counted && used && footerText
                    && slot.Entry is BlitzMerchantEntry { Status: BlitzMerchantEntry.BlitzEntryStatus.Used }
                    && !BlitzSlotIconVisible(slot) && slot.Hitbox.MouseFilter == Control.MouseFilterEnum.Ignore,
                $"remaining={model.BlitzChoicesRemaining}/{expected[i]} used={used} icon={BlitzSlotIconVisible(slot)} " +
                $"filter={slot.Hitbox.MouseFilter} entry={slot.Entry.GetType().Name}");
            RefreshBlitzTokens(room.MerchantButton as NBlitzMerchantButton);
            CheckBlitzFooter(inventory, footer, expected[i], names[i]);
            if (names[i] == "two_more_choices")
            {
                // Raising the count must leave the other untouched entries available again, not disabled.
                var stillAvailable = inventory.GetAllSlots().OfType<NBlitzMerchantSlot>()
                    .Count(candidate => candidate.Entry is BlitzMerchantEntry { IsStocked: true });
                _report.Check("shop_states_blitz_two_more_reopens_picks", model.BlitzChoicesRemaining == 2
                        && stillAvailable == 6 - (i + 1),
                    $"remaining=2 available={stillAvailable} picked={i + 1}");
            }
            await CaptureAsync($"{ShopStatesPrefix}_03_blitz_pick_{i + 1}_{names[i]}");
        }

        await _pointer.MoveAsync(ShopDismissPoint(), pressed: false);
        await WaitSecondsAsync(0.5);
        NBlitzMerchantSlot[] remaining = inventory.GetAllSlots().OfType<NBlitzMerchantSlot>().ToArray();
        var disabled = remaining.Where(slot => slot.Entry is BlitzMerchantEntry
            { Status: BlitzMerchantEntry.BlitzEntryStatus.Disabled }).ToArray();
        var spent = remaining.Where(slot => slot.Entry is BlitzMerchantEntry
            { Status: BlitzMerchantEntry.BlitzEntryStatus.Used }).ToArray();
        _report.Check("shop_states_blitz_exhausted_disables_the_rest",
            model.BlitzChoicesRemaining == 0 && disabled.Length == 1 && spent.Length == 5,
            $"remaining={model.BlitzChoicesRemaining} disabled={disabled.Length} used={spent.Length}");
        foreach (NBlitzMerchantSlot slot in disabled)
        {
            _report.Check("shop_states_blitz_disabled_slot_" + slot.GetIndex(),
                slot.Modulate.IsEqualApprox(Colors.DarkGray) && BlitzSlotAlreadyDisabled(slot)
                && slot.Hitbox.MouseFilter == Control.MouseFilterEnum.Ignore
                && slot.FocusMode == Control.FocusModeEnum.None,
                $"entry={slot.Entry.GetType().Name} status={((BlitzMerchantEntry)slot.Entry).Status} " +
                $"modulate={slot.Modulate} filter={slot.Hitbox.MouseFilter} focus={slot.FocusMode}");
        }
        foreach (NBlitzMerchantSlot slot in spent)
        {
            _report.Check("shop_states_blitz_used_slot_" + slot.GetIndex(),
                BlitzSlotAlreadyDisabled(slot) && !BlitzSlotIconVisible(slot)
                && slot.Hitbox.MouseFilter == Control.MouseFilterEnum.Ignore,
                $"entry={slot.Entry.GetType().Name} icon={BlitzSlotIconVisible(slot)} filter={slot.Hitbox.MouseFilter}");
        }
        CheckBlitzFooter(inventory, footer, 0, "exhausted");
        await CaptureAsync(ShopStatesPrefix + "_04_blitz_exhausted");

        if (disabled.Length > 0)
        {
            NBlitzMerchantSlot disabledSlot = disabled[0];
            int commits = ShopCommits();
            int deck = player.Deck.Cards.Count;
            await _pointer.TapAsync(CenterOf(disabledSlot.Hitbox));
            await WaitSecondsAsync(0.45);
            _report.Check("shop_states_blitz_disabled_tap_does_nothing",
                !ShopSelected() && ShopCommits() == commits && model.BlitzChoicesRemaining == 0
                && disabledSlot.Entry is BlitzMerchantEntry { Status: BlitzMerchantEntry.BlitzEntryStatus.Disabled }
                && player.Deck.Cards.Count == deck,
                $"selected={(ShopSelected() ? "yes" : "none")} commits={ShopCommits()}/{commits} " +
                $"remaining={model.BlitzChoicesRemaining} deck={player.Deck.Cards.Count}/{deck}");
            // The host's enlarged touch pad must not revive the dimmed slot's hover either: the native disabled slot has
            // MouseFilter Ignore, so a pointer over it leaves the art at its resting scale.
            await _pointer.MoveAsync(ShopDismissPoint(), pressed: false);
            await WaitSecondsAsync(0.3);
            Vector2 restingScale = disabledSlot.Scale;
            await _pointer.MoveAsync(CenterOf(disabledSlot.Hitbox), pressed: false);
            await WaitSecondsAsync(0.35);
            _report.Check("shop_states_blitz_disabled_hover_keeps_rest",
                disabledSlot.Scale.IsEqualApprox(restingScale),
                $"rest={restingScale} hovered={disabledSlot.Scale}");
            await _pointer.MoveAsync(ShopDismissPoint(), pressed: false);
            await WaitSecondsAsync(0.25);
        }

        await _pointer.TapAsync(ShopDismissPoint());
        await CloseShopAndCheckAsync(inventory, "blitz_exhausted");
        await CaptureAsync(ShopStatesPrefix + "_05_blitz_room_closed");
        _report.Check("shop_states_blitz_proceed_enabled", room.ProceedButton.IsEnabled,
            $"proceedEnabled={room.ProceedButton.IsEnabled}");
        if (!room.ProceedButton.IsEnabled) return;
        bool mapBefore = NMapScreen.Instance?.IsOpen == true;
        await _pointer.TapAsync(CenterOf(room.ProceedButton));
        // The room was entered with the run in Standard mode (the fixture's one-room Blitz flip), so Proceed is the native
        // non-Blitz path: it opens the map and keeps the room under it. The Blitz advance needs a Blitz run's map.
        bool opened = await WaitForAsync(() => NMapScreen.Instance?.IsOpen == true,
            "Blitz shop Proceed opens the map", MediumWait);
        await WaitSecondsAsync(0.5);
        _report.Check("shop_states_blitz_proceed_opens_map", opened && !mapBefore
                && ReferenceEquals(NMerchantRoom.Instance, room),
            $"map={NMapScreen.Instance?.IsOpen} before={mapBefore} " +
            $"room={(NMerchantRoom.Instance is { } still ? still.GetType().Name : "none")}");
    }

    /// <summary>
    /// Replaces five of the six Blitz entries with native types whose select never opens a blocking screen and keeps
    /// each entry's inventory id, so the native purchase lookup, the count update and the footer refresh still run. The
    /// sixth entry is untouched and is the one the exhausted merchant leaves disabled. Returns the installed entries in
    /// purchase order.
    /// </summary>
    private List<BlitzMerchantEntry> InstallBlitzPurchaseFixture(NMerchantInventory inventory,
        NBlitzMerchantRoom room, Player player)
    {
        MerchantInventory? model = inventory.Inventory;
        if (model is null || model.BlitzEntries.Count < 6) return new List<BlitzMerchantEntry>();
        var entries = (List<BlitzMerchantEntry>)model.BlitzEntries;
        var button = room.MerchantButton as NBlitzMerchantButton;
        MethodInfo? decrement = EntryMethod(typeof(MerchantInventory), "UpdateEntries");
        MethodInfo? refresh = EntryMethod(typeof(NMerchantInventory), "OnPurchaseCompleted");
        if (decrement is null || refresh is null)
        {
            _report.Fail("shop_states_blitz_fixture_hooks", "the native entry-update handlers were not found");
            return new List<BlitzMerchantEntry>();
        }
        var picks = new List<BlitzMerchantEntry>
        {
            new BlitzMerchantRandomCardUpgradesEntry(player),
            new BlitzMerchantFullHealAndMaxHpEntry(player),
            new BlitzMerchantTwoMoreChoicesEntry(player),
            new BlitzMerchantRandomCardUpgradesEntry(player),
            new BlitzMerchantFullHealAndMaxHpEntry(player),
        };
        for (int i = 0; i < picks.Count; i++)
        {
            BlitzMerchantEntry replacement = picks[i];
            BlitzMerchantEntry native = entries[i];
            replacement.Id = native.Id;
            // The inventory's count/decrement and the footer refresh are subscribed per entry when the game sets the
            // stock up; a fixture entry has to join both, in the game's order (decrement first, then the footer).
            replacement.PurchaseCompleted += (status, entry) =>
            {
                decrement.Invoke(model, new object[] { status, entry });
                refresh.Invoke(inventory, new object[] { status, entry });
            };
            entries[i] = replacement;
            foreach (NBlitzMerchantSlot slot in inventory.GetAllSlots().OfType<NBlitzMerchantSlot>())
                if (ReferenceEquals(slot.Entry, native))
                    slot.FillSlot(replacement);
        }
        RefreshBlitzTokens(button);
        return picks;
    }

    /// <summary>Blitz slot members the game keeps private: the icon's visibility and the one-time disable flag.</summary>
    private static bool BlitzSlotIconVisible(NBlitzMerchantSlot slot) =>
        slot.GetNodeOrNull<TextureRect>("%Icon") is { Visible: true };

    private static bool BlitzSlotAlreadyDisabled(NBlitzMerchantSlot slot) =>
        typeof(NBlitzMerchantSlot).GetField("_alreadyDisabled", ShopStatesInstance)?.GetValue(slot) is true;

    /// <summary>Refreshes the merchant's six pick tokens after the fixture changes the stock.</summary>
    private static void RefreshBlitzTokens(NBlitzMerchantButton? button)
    {
        if (button is null) return;
        typeof(NBlitzMerchantButton)
            .GetMethod("UpdateTokens", ShopStatesInstance, null, Type.EmptyTypes, null)?.Invoke(button, null);
    }

    /// <summary>The native <c>UpdateEntries</c> / <c>OnPurchaseCompleted</c> handler, found by its event signature.</summary>
    private static MethodInfo? EntryMethod(Type type, string method) =>
        type.GetMethod(method, ShopStatesInstance, null, new[] { typeof(PurchaseStatus), typeof(MerchantEntry) }, null);

    /// <summary>The native footer sentence for a remaining-picks count, exactly as the game formats it.</summary>
    private static string BlitzFooterExpectedText(int remaining)
    {
        var line = new LocString("merchant_room",
            remaining > 0 ? "MERCHANT.blitzSelection.remaining" : "MERCHANT.blitzSelection.none");
        if (remaining > 0) line.Add("Amount", (decimal)remaining);
        return line.GetFormattedText();
    }

    /// <summary>
    /// The choices-remaining line's text, plus the host's answer about where it sits and whether it fits.
    /// </summary>
    private void CheckBlitzFooter(NMerchantInventory inventory, Control? footer, int remaining, string when)
    {
        string textStep = "shop_states_blitz_footer_text_" + when;
        if (footer is not RichTextLabel label)
        {
            _report.Fail(textStep, "the Blitz inventory has no choices-remaining line");
            return;
        }
        string expected = BlitzFooterExpectedText(remaining);
        _report.Check(textStep, label.IsVisibleInTree() && label.Text == expected,
            $"text='{label.Text}' expected='{expected}' visible={label.IsVisibleInTree()}");
        CheckBlitzFooterLayout(inventory, footer, remaining);
    }

    // ---- Gap 14: a normal shop with no stock left ----------------------------------------------------------------

    private async Task RunSoldOutShopStatesAsync(Player player)
    {
        await PlayerCmd.GainGold(9999, player);
        // Three potions are off the shelf; an occupied belt would refuse the third purchase.
        MethodInfo? removePotion = typeof(Player).GetMethod("RemovePotionInternal", ShopStatesInstance);
        if (removePotion is null) _report.Fail("shop_states_potion_belt_fixture", "Player.RemovePotionInternal not found");
        else foreach (var potion in player.Potions.ToArray()) removePotion.Invoke(player, new object[] { potion });
        await WaitSecondsAsync(0.35);

        await RunManager.Instance.EnterRoomDebug(RoomType.Shop, showTransition: false);
        await WaitUntilAsync(() => NMerchantRoom.Instance is NStandardMerchantRoom r && r.IsInsideTree(),
            "standard merchant room for the sold-out fixture", MediumWait);
        await WaitSecondsAsync(1.0);
        var room = (NStandardMerchantRoom)NMerchantRoom.Instance!;
        NMerchantInventory inventory = room.Inventory;
        MerchantInventory? model = inventory.Inventory;
        _report.Check("shop_states_sold_out_room_native", model is { IsBlitzShop: false },
            $"inventory={model?.GetType().Name}");
        if (model is null)
        {
            _report.Fail("shop_states_sold_out_inventory", "the shop has no merchant inventory");
            return;
        }
        if (IsPortraitHost()) CheckShopRoomFraming(room.MerchantButton, weird: false);
        await CaptureAsync(ShopStatesPrefix + "_06_shop_room");

        await _pointer.TapAsync(CenterOf(room.MerchantButton));
        if (!await WaitForAsync(() => inventory.IsOpen, "shop open for the sold-out fixture", MediumWait))
        {
            _report.Fail("shop_states_sold_out_open", "the shop's inventory did not open");
            return;
        }
        await WaitSecondsAsync(0.85);
        if (inventory.GetNodeOrNull<Control>("%SlotsContainer") is not { } stock)
        {
            _report.Fail("shop_states_sold_out_stock", "the shop has no stock container");
            return;
        }
        if (IsPortraitHost()) CheckShopStockGeometry(inventory, stock);
        await CaptureAsync(ShopStatesPrefix + "_07_shop_stock");

        Dictionary<NMerchantSlot, Vector2> baseline = SnapshotShopPositions(stock);
        Dictionary<string, Vector2> groupScales = SnapshotShopGroupScales(inventory);
        Rect2 baselineStock = stock.GetGlobalRect();

        var cards = inventory.GetAllSlots().OfType<NMerchantCard>().ToArray();
        int bought = 0;
        foreach (NMerchantCard card in cards)
        {
            if (!card.Entry.IsStocked) continue;
            int deck = player.Deck.Cards.Count;
            int gold = player.Gold;
            int price = card.Entry.Cost;
            await ConfirmShopSlotAsync(card);
            bool sold = await WaitForAsync(() => !card.Entry.IsStocked && player.Deck.Cards.Count == deck + 1,
                "shop card sold", MediumWait);
            _report.Check($"shop_states_sold_card_{bought++}", sold && player.Gold == gold - price,
                $"stocked={card.Entry.IsStocked} deck={player.Deck.Cards.Count}/{deck} gold={player.Gold}/{gold} price={price}");
        }
        _report.Check("shop_states_every_card_sold", cards.Length == 7 && cards.All(card => !card.Entry.IsStocked),
            $"cards={cards.Length} stocked={cards.Count(card => card.Entry.IsStocked)}");
        await _pointer.MoveAsync(ShopDismissPoint(), pressed: false);
        await WaitSecondsAsync(0.5);
        await CaptureAsync(ShopStatesPrefix + "_08_cards_sold_out");

        if (IsPortraitHost()) await SelectShopPageAsync(inventory, items: true);
        await WaitSecondsAsync(0.4);

        var relics = inventory.GetAllSlots().OfType<NMerchantRelic>().ToArray();
        int relicCount = 0;
        foreach (NMerchantRelic relic in relics)
        {
            if (!relic.Entry.IsStocked) continue;
            int gold = player.Gold;
            int price = relic.Entry.Cost;
            await ConfirmShopSlotAsync(relic);
            bool sold = await WaitForAsync(() => !relic.Entry.IsStocked, "shop relic sold", MediumWait);
            _report.Check($"shop_states_sold_relic_{relicCount++}", sold && player.Gold == gold - price,
                $"stocked={relic.Entry.IsStocked} gold={player.Gold}/{gold} price={price}");
        }
        var potions = inventory.GetAllSlots().OfType<NMerchantPotion>().ToArray();
        int potionCount = 0;
        foreach (NMerchantPotion potion in potions)
        {
            if (!potion.Entry.IsStocked) continue;
            int gold = player.Gold;
            int price = potion.Entry.Cost;
            await ConfirmShopSlotAsync(potion);
            bool sold = await WaitForAsync(() => !potion.Entry.IsStocked, "shop potion sold", MediumWait);
            _report.Check($"shop_states_sold_potion_{potionCount++}", sold && player.Gold == gold - price,
                $"stocked={potion.Entry.IsStocked} gold={player.Gold}/{gold} price={price}");
        }
        _report.Check("shop_states_every_item_sold",
            relics.Length == 3 && relics.All(relic => !relic.Entry.IsStocked)
            && potions.Length == 3 && potions.All(potion => !potion.Entry.IsStocked),
            $"relics={relics.Count(relic => relic.Entry.IsStocked)}/{relics.Length} " +
            $"potions={potions.Count(potion => potion.Entry.IsStocked)}/{potions.Length}");

        var removal = inventory.GetAllSlots().OfType<NMerchantCardRemoval>().FirstOrDefault();
        if (removal is null) _report.Fail("shop_states_removal_completes_and_charges", "the shop has no card removal");
        else await CompleteShopRemovalAsync(removal, player);
        await _pointer.MoveAsync(ShopDismissPoint(), pressed: false);
        await WaitSecondsAsync(0.55);

        var entries = AllShopSlots(stock).Select(slot => slot.Entry).ToArray();
        _report.Check("shop_states_all_stock_sold_out",
            entries.All(entry => entry is not null && !entry.IsStocked)
            && entries.OfType<MerchantCardRemovalEntry>().All(entry => entry.Used),
            string.Join("; ", entries.Select(entry => entry is null
                ? "unfilled"
                : $"{entry.GetType().Name}:stocked={entry.IsStocked}")));
        await CaptureAsync(ShopStatesPrefix + "_09_items_sold_out");

        // Revisit both pages with nothing purchasable: the host's layout must not move, keep a highlight or a target.
        if (IsPortraitHost())
        {
            await SelectShopPageAsync(inventory, items: false);
            await _pointer.MoveAsync(ShopDismissPoint(), pressed: false);
            await WaitSecondsAsync(0.45);
            await CaptureAsync(ShopStatesPrefix + "_10_cards_sold_out_empty");
            await CheckShopSoldOutLayoutAsync("shop_states_cards_sold_out", inventory, baseline, groupScales, baselineStock, player);
            await SelectShopPageAsync(inventory, items: true);
            await WaitSecondsAsync(0.45);
            await CaptureAsync(ShopStatesPrefix + "_11_items_sold_out_empty");
            await CheckShopSoldOutLayoutAsync("shop_states_items_sold_out", inventory, baseline, groupScales, baselineStock, player);
        }
        else
        {
            await CheckShopSoldOutLayoutAsync("shop_states_sold_out", inventory, baseline, groupScales, baselineStock, player);
        }

        await CloseShopAndCheckAsync(inventory, "sold_out");
        await CaptureAsync(ShopStatesPrefix + "_12_sold_out_room");
        CheckShopProceedTarget(room.ProceedButton);
        await _pointer.TapAsync(CenterOf(room.ProceedButton));
        await WaitUntilAsync(() => NMapScreen.Instance?.IsOpen == true, "sold-out shop Proceed opens the map", ShortWait);
        _report.Pass("shop_states_sold_out_proceed_opens_map", "the empty shop's Proceed still reaches the map");
    }

    /// <summary>
    /// Finishes the removal the shop scenarios only cancel: tap a deck card, confirm the native preview, then check the
    /// entry is used, the card left the deck and the gold was spent.
    /// </summary>
    private async Task CompleteShopRemovalAsync(NMerchantCardRemoval removal, Player player)
    {
        int deck = player.Deck.Cards.Count;
        int gold = player.Gold;
        await ConfirmShopSlotAsync(removal);
        if (!await WaitForAsync(() => FindFirst<NDeckCardSelectScreen>(_tree.Root)?.IsVisibleInTree() == true,
                "sold-out removal deck select", MediumWait))
        {
            _report.Fail("shop_states_removal_completes_and_charges", "the removal did not open the deck select");
            return;
        }
        var screen = FindFirst<NDeckCardSelectScreen>(_tree.Root);
        if (screen?.GetNodeOrNull<NCardGrid>("%CardGrid") is not { } grid
            || !await WaitForAsync(() => grid.CurrentlyDisplayedCardHolders.Any(), "removal cards displayed", MediumWait))
        {
            _report.Fail("shop_states_removal_completes_and_charges", "the removal deck select showed no cards");
            return;
        }
        var holder = grid.CurrentlyDisplayedCardHolders.FirstOrDefault(card => card.IsVisibleInTree()
            && grid.GetGlobalRect().Encloses(card.Hitbox.GetGlobalRect()));
        if (holder is null)
        {
            _report.Fail("shop_states_removal_completes_and_charges", "no removal card sits inside the grid");
            return;
        }
        await _pointer.TapAsync(CenterOf(holder.Hitbox));
        if (screen.GetNodeOrNull<Control>("%PreviewContainer") is not { } preview
            || preview.GetNodeOrNull<NConfirmButton>("%PreviewConfirm") is not { } confirm
            || !await WaitForAsync(() => preview.IsVisibleInTree() && confirm.IsEnabled, "removal preview", MediumWait))
        {
            _report.Fail("shop_states_removal_completes_and_charges", "the removal preview did not open");
            return;
        }
        await WaitSecondsAsync(0.45);
        await CaptureAsync(ShopStatesPrefix + "_09a_removal_preview");
        await _pointer.TapAsync(CenterOf(confirm));
        bool done = await WaitForAsync(() => removal.Entry is MerchantCardRemovalEntry { Used: true }
                && player.Deck.Cards.Count == deck - 1
                && (!GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree()),
            "removal completes", MediumWait);
        _report.Check("shop_states_removal_completes_and_charges", done && player.Gold < gold,
            $"used={(removal.Entry as MerchantCardRemovalEntry)?.Used} gold={player.Gold}/{gold} " +
            $"deck={player.Deck.Cards.Count}/{deck}");
        await WaitSecondsAsync(0.45);
        Control? cost = removal.GetNodeOrNull<Control>("Cost");
        AnimationPlayer? animator = removal.GetNodeOrNull<AnimationPlayer>("%Animation");
        _report.Check("shop_states_used_removal_visual",
            removal.Entry is MerchantCardRemovalEntry { Used: true }
            && removal.Hitbox.MouseFilter == Control.MouseFilterEnum.Ignore
            && cost is { Visible: false },
            $"filter={removal.Hitbox.MouseFilter} cost={cost?.Visible} " +
            $"animation={animator?.CurrentAnimation}");
    }

    /// <summary>
    /// Every stock slot regardless of which page currently shows its group: the game's own <c>GetAllSlots</c> leaves out
    /// the hidden page's groups, and a sold-out shop has to be compared across both pages.
    /// </summary>
    private static List<NMerchantSlot> AllShopSlots(Control stock)
    {
        var slots = new List<NMerchantSlot>();
        foreach (string group in new[] { "CharacterCards", "ColorlessCards", "Relics", "Potions" })
            if (stock.GetNodeOrNull<Control>(group) is { } container)
                slots.AddRange(container.GetChildren().OfType<NMerchantSlot>());
        if (stock.GetNodeOrNull<NMerchantSlot>("MerchantCardRemoval") is { } removal) slots.Add(removal);
        return slots;
    }

    private static Dictionary<NMerchantSlot, Vector2> SnapshotShopPositions(Control stock)
    {
        var positions = new Dictionary<NMerchantSlot, Vector2>();
        foreach (NMerchantSlot slot in AllShopSlots(stock)) positions[slot] = slot.Position;
        return positions;
    }

    private static Dictionary<string, Vector2> SnapshotShopGroupScales(NMerchantInventory inventory)
    {
        var scales = new Dictionary<string, Vector2>();
        foreach (string group in new[] { "CharacterCards", "ColorlessCards", "Relics", "Potions", "BlitzSlots" })
            if (inventory.GetNodeOrNull<Control>("%" + group) is { } node)
                scales[group] = node.Scale;
        return scales;
    }

    /// <summary>
    /// A sold-out page: every slot stays where it was, the groups keep their scale, the host left no selection reticle,
    /// the sold slots are hidden with no live hitbox and the used removal keeps no target. Then one tap on a sold card and
    /// one on the used removal prove they buy nothing.
    /// </summary>
    private async Task CheckShopSoldOutLayoutAsync(string prefix, NMerchantInventory inventory,
        Dictionary<NMerchantSlot, Vector2> baseline, Dictionary<string, Vector2> groupScales, Rect2 baselineStock,
        Player player)
    {
        if (inventory.GetNodeOrNull<Control>("%SlotsContainer") is not { } stock)
        {
            _report.Fail(prefix + "_stock_present", "the shop has no stock container");
            return;
        }
        NMerchantSlot[] slots = AllShopSlots(stock).ToArray();
        var moved = slots.Where(slot => baseline.TryGetValue(slot, out Vector2 position)
                && !slot.Position.IsEqualApprox(position))
            .Select(slot => $"{slot.GetType().Name} {baseline[slot]}->{slot.Position}").ToArray();
        _report.Check(prefix + "_stock_positions_unchanged",
            moved.Length == 0 && slots.Length == baseline.Count && stock.GetGlobalRect().IsEqualApprox(baselineStock),
            $"moved={string.Join("; ", moved)} stock={stock.GetGlobalRect()}/{baselineStock}");
        var scaled = groupScales.Where(pair => inventory.GetNodeOrNull<Control>("%" + pair.Key) is { } node
                && !node.Scale.IsEqualApprox(pair.Value))
            .Select(pair => $"{pair.Key} {pair.Value}->{inventory.GetNode<Control>("%" + pair.Key).Scale}").ToArray();
        _report.Check(prefix + "_group_scales_unchanged", scaled.Length == 0, string.Join("; ", scaled));
        _report.Check(prefix + "_no_stale_selection", !ShopSelected() && ShopHighlightCount(inventory) == 0,
            $"selected={(ShopSelected() ? "yes" : "none")} reticles={ShopHighlightCount(inventory)}");

        var cards = slots.OfType<NMerchantCard>().ToArray();
        var relics = slots.OfType<NMerchantRelic>().ToArray();
        var potions = slots.OfType<NMerchantPotion>().ToArray();
        var removal = slots.OfType<NMerchantCardRemoval>().FirstOrDefault();
        bool NoTarget(NMerchantSlot slot) => !slot.IsVisibleInTree() && (slot.GetParent() is not Control { Visible: true }
            || slot.MouseFilter == Control.MouseFilterEnum.Ignore);
        _report.Check(prefix + "_sold_slots_keep_no_targets",
            cards.All(NoTarget) && relics.All(NoTarget) && potions.All(NoTarget),
            $"cards={cards.Count(card => card.IsVisibleInTree())} relics={relics.Count(relic => relic.IsVisibleInTree())} " +
            $"potions={potions.Count(potion => potion.IsVisibleInTree())}");
        Control? cost = removal?.GetNodeOrNull<Control>("Cost");
        _report.Check(prefix + "_used_removal_keeps_no_target", removal is null
                || (removal.Entry is MerchantCardRemovalEntry { Used: true }
                    && removal.Hitbox.MouseFilter == Control.MouseFilterEnum.Ignore
                    && cost is { Visible: false }),
            removal is null ? "no removal in the stock"
                : $"filter={removal.Hitbox.MouseFilter} cost={cost?.Visible}");

        if (cards.Length > 0)
        {
            int gold = player.Gold, deck = player.Deck.Cards.Count, commits = ShopCommits();
            await _pointer.TapAsync(CenterOf(cards[0].Hitbox));
            await WaitSecondsAsync(0.4);
            _report.Check(prefix + "_sold_card_tap_does_nothing",
                player.Gold == gold && player.Deck.Cards.Count == deck && ShopCommits() == commits && !ShopSelected(),
                $"gold={player.Gold}/{gold} deck={player.Deck.Cards.Count}/{deck} commits={ShopCommits()}/{commits}");
        }
        if (removal is not null)
        {
            int gold = player.Gold, deck = player.Deck.Cards.Count, commits = ShopCommits();
            await _pointer.TapAsync(CenterOf(removal.Hitbox));
            await WaitSecondsAsync(0.4);
            _report.Check(prefix + "_used_removal_tap_does_nothing",
                player.Gold == gold && player.Deck.Cards.Count == deck && ShopCommits() == commits
                && !ShopSelected() && !FindAll<NDeckCardSelectScreen>(_tree.Root).Any(s => s.IsVisibleInTree()),
                $"gold={player.Gold}/{gold} deck={player.Deck.Cards.Count}/{deck} commits={ShopCommits()}/{commits}");
        }
    }

    // ---- Gap 14: shops in later acts -------------------------------------------------------------------------------

    private async Task RunActShopStatesAsync(RunState state)
    {
        foreach ((string act, string tag) in new[] { ("hive", "act2"), ("glory", "act3") })
        {
            try
            {
                await ConfigureTourActAsync(state, act);
            }
            catch (Exception error)
            {
                _report.Fail($"shop_states_{tag}_act_fixture", error.Message);
                continue;
            }
            await WaitSecondsAsync(0.8);
            NMerchantRoom? previous = NMerchantRoom.Instance;
            await RunManager.Instance.EnterRoomDebug(RoomType.Shop, showTransition: false);
            if (!await WaitForAsync(() => NMerchantRoom.Instance is NStandardMerchantRoom r && r != previous && r.IsInsideTree(),
                    $"{tag} merchant room", MediumWait))
            {
                _report.Fail($"shop_states_{tag}_merchant_room_native", "the shop did not appear in this act");
                continue;
            }
            await WaitSecondsAsync(1.0);
            var room = (NStandardMerchantRoom)NMerchantRoom.Instance!;
            NMerchantInventory inventory = room.Inventory;
            _report.Check($"shop_states_{tag}_merchant_room_native",
                inventory.Inventory is { IsBlitzShop: false },
                $"inventory={inventory.Inventory?.GetType().Name}");
            if (IsPortraitHost()) CheckShopRoomFraming(room.MerchantButton, weird: false);
            await CaptureAsync($"{ShopStatesPrefix}_13_{tag}_room");

            await _pointer.TapAsync(CenterOf(room.MerchantButton));
            if (!await WaitForAsync(() => inventory.IsOpen, $"{tag} shop inventory", MediumWait))
            {
                _report.Fail($"shop_states_{tag}_inventory", "the shop's inventory did not open");
                continue;
            }
            await WaitSecondsAsync(0.8);
            Control? actStock = inventory.GetNodeOrNull<Control>("%SlotsContainer");
            if (actStock is not null && IsPortraitHost()) CheckShopStockGeometry(inventory, actStock);
            await CaptureAsync($"{ShopStatesPrefix}_14_{tag}_stock");
            await CloseShopAndCheckAsync(inventory, tag);
            CheckShopProceedTarget(room.ProceedButton);
            await _pointer.TapAsync(CenterOf(room.ProceedButton));
            await WaitUntilAsync(() => NMapScreen.Instance?.IsOpen == true, $"{tag} shop Proceed opens the map", ShortWait);
            _report.Pass($"shop_states_{tag}_proceed_opens_map", "the act's shop Proceed still reaches the map");
        }
    }
}
