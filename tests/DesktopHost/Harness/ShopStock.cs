using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The shop scenarios, shared by both hosts: the fixture (merchant room, weird event shop, Courier restock), the room and
/// inventory geometry, the Cards/Items pages, hold inspection, swipes, purchases, card removal and the return to the map.
/// <para>
/// The stock's shape is TouchSpireUI's (rotated rug, 190-unit tabs, staggered Power) or the launcher's (cloth, two
/// <c>NSettingsTab</c> pages, rows) depending on the host, so every assertion about it is a host partial; the TSUI
/// implementations live in HostShopChecks.cs, the launcher's in TouchSpire.LauncherHost/src/LauncherShopChecks.cs. The
/// interaction is the same on both: <see cref="ConfirmShopSlotAsync"/> is one tap on TouchSpireUI and first-press-select
/// plus second-tap-confirm on the launcher.
/// </para>
/// </summary>
internal sealed partial class TestDriver
{
    private static Rect2 ShopSlotBounds(NMerchantSlot slot)
    {
        Rect2 bounds = slot.Hitbox.GetGlobalRect();
        if (slot.GetNodeOrNull<Control>("Cost") is not { Visible: true } cost) return bounds;
        // The HBox spans the whole native slot; only its coin icon and label draw.
        foreach (Control child in cost.GetChildren().OfType<Control>().Where(c => c.Visible))
            bounds = bounds.Merge(child.GetGlobalRect());
        return bounds;
    }

    private async Task RunShopScenarioAsync(RunState runState)
    {
        Player player = runState.Players[0];
        bool weirdShop = _options.Scenario == "weirdshop";
        bool courier = _options.Scenario == "shop-courier";
        if (courier) await PrepareShopCourierAsync(player);
        NMerchantInventory inventory;
        NMerchantButton merchantButton;
        if (weirdShop)
        {
            await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: ModelDb.Event<FakeMerchant>(), showTransition: false);
            await WaitUntilAsync(() => NEventRoom.Instance is { } eventRoom
                && FindFirst<NFakeMerchant>(eventRoom) is { Inventory: not null }, "weird merchant", MediumWait);
            var weird = FindFirst<NFakeMerchant>(NEventRoom.Instance!)!;
            inventory = weird.Inventory;
            merchantButton = weird.MerchantButton;
        }
        else
        {
            await RunManager.Instance.EnterRoomDebug(RoomType.Shop, showTransition: false);
            await WaitUntilAsync(() => NMerchantRoom.Instance is { } room && room.IsInsideTree(), "merchant room", MediumWait);
            inventory = NMerchantRoom.Instance!.Inventory;
            merchantButton = NMerchantRoom.Instance.MerchantButton;
        }
        await WaitSecondsAsync(1.0);
        _report.Pass("shop_room_loaded");
        if (IsPortraitHost()) CheckShopRoomFraming(merchantButton, weirdShop);
        var roomFraming = ReadShopFraming(merchantButton);
        await CaptureAsync("shop_01_room");
        await _pointer.TapAsync(CenterOf(merchantButton));
        if (IsPortraitHost()) await CheckShopBackEntranceAsync(inventory, "open");
        await WaitUntilAsync(() => inventory.IsOpen, "shop inventory", MediumWait);
        await WaitSecondsAsync(0.8);
        _report.Check("shop_inventory_visible", inventory.IsVisibleInTree(), "merchant inventory is visible");
        await CaptureAsync("shop_02_inventory");
        if (inventory.GetNodeOrNull<Control>("%SlotsContainer") is not { } stock
            || inventory.GetNodeOrNull<Control>("%BackButton") is not { } landscapeBack)
        {
            _report.Fail("shop_stock_below_header", "the inventory has no stock container or Back button");
            return;
        }
        if (!IsPortraitHost())
        {
            if (!weirdShop && !courier) await CheckShopRemovalCancelAsync(inventory, player);
            await _pointer.TapAsync(CenterOf(landscapeBack));
            await WaitUntilAsync(() => !inventory.IsOpen, "landscape shop closes", ShortWait);
            await WaitSecondsAsync(0.8);
            _report.Check("shop_landscape_keeps_native_inventory", Mathf.Abs(stock.Position.Y + 1000f) < 1f
                && inventory.GetNodeOrNull<Control>(ShopCardsTabNodeName()) is null, $"stock Y={stock.Position.Y}");
            return;
        }
        Rect2 clip = stock.GetGlobalRect();
        CheckShopStockGeometry(inventory, stock);
        var cards = inventory.GetAllSlots().OfType<NMerchantCard>().ToList();
        var card = inventory.GetAllSlots().FirstOrDefault(c => clip.Encloses(c.Hitbox.GetGlobalRect()))
            ?? throw new InvalidOperationException("No fully visible shop target: " + string.Join("; ", inventory.GetAllSlots().Select(s => $"{s.GetType().Name} {s.Hitbox.GetGlobalRect()}")));
        _report.Check("shop_card_art_survives_layout", cards.All(c => FindAll<NCard>(c).Count() == 1),
            $"cards={cards.Count} art={cards.Sum(c => FindAll<NCard>(c).Count())}");
        if (!weirdShop) await CaptureAsync("shop_02a_cards_page");

        int gold = player.Gold;
        int commits = ShopCommits(), clears = ShopClears();
        await _pointer.LongPressAsync(CenterOf(card.Hitbox), frames: 60);
        await WaitSecondsAsync(0.5);
        Control inspector = cards.Count > 0 ? NGame.Instance!.GetInspectCardScreen() : NGame.Instance!.GetInspectRelicScreen();
        _report.Check("shop_hold_inspects_without_buying", inspector.IsVisibleInTree() && player.Gold == gold,
            $"gold={player.Gold}/{gold}");
        await CaptureAsync("shop_03_inspect");
        Vector2 dismiss = ShopDismissPoint();
        await _pointer.TapAsync(dismiss);
        await WaitUntilAsync(() => !inspector.IsVisibleInTree(), "shop inspector dismissed", ShortWait);
        await WaitSecondsAsync(0.4);
        CheckShopHoldHost(gold, commits, clears);

        Vector2 slotPosition = card.Position;
        Vector2 start = CenterOf(card.Hitbox);
        int swipeCommits = ShopCommits(), swipeClears = ShopClears();
        await _pointer.DragAsync(start, start + new Vector2(110f, 0f), 15);
        await WaitSecondsAsync(0.4);
        _report.Check("shop_drag_keeps_stock_and_does_not_buy", card.IsVisibleInTree() && card.Entry.IsStocked
            && slotPosition.DistanceTo(card.Position) < 1f && player.Gold == gold,
            $"gold={player.Gold}/{gold}, slot={card.Position}/{slotPosition}");
        await CheckShopSwipeHostAsync(card, gold, swipeCommits, swipeClears);

        if (courier) await BuyCourierStockAsync(inventory, player, clip);
        else if (!weirdShop)
        {
            await SelectShopPageAsync(inventory, items: true);
            CheckShopItemsPage(inventory, clip);
            await CaptureAsync("shop_05_items_page");
            await CheckShopRemovalCancelAsync(inventory, player);
            await SelectShopPageAsync(inventory, items: false);
            var purchase = cards.FirstOrDefault(c => c.Entry.IsStocked && c.Entry.EnoughGold && clip.Encloses(c.Hitbox.GetGlobalRect()));
            if (purchase is not null)
            {
                int deckCount = player.Deck.Cards.Count;
                // The removal-cancel fixture can top up gold; measure this transaction from the current balance.
                int goldBeforePurchase = player.Gold;
                int price = purchase.Entry.Cost;
                await BuyShopSlotAsync(purchase);
                await WaitUntilAsync(() => player.Deck.Cards.Count == deckCount + 1, "shop purchase added to deck", MediumWait);
                _report.Check("shop_tap_buys_one_card", player.Gold == goldBeforePurchase - price && !purchase.Entry.IsStocked,
                    $"gold={player.Gold}/{goldBeforePurchase} price={price} deck={player.Deck.Cards.Count}/{deckCount}");
                await CaptureAsync("shop_06_purchased");
            }
            else _report.Skip("shop_tap_buys_one_card", "no affordable card at top of fixture stock");
            await SelectShopPageAsync(inventory, items: true);
            await SelectShopPageAsync(inventory, items: false);
            _report.Check("shop_purchase_stays_sold_after_page_switch", purchase is null || !purchase.Entry.IsStocked,
                "switching pages preserves native stock state");
            await SelectShopPageAsync(inventory, items: true);
        }
        else
        {
            await CaptureAsync("weirdshop_01_stock");
            var lastRelic = inventory.GetAllSlots().Last();
            await _pointer.LongPressAsync(CenterOf(lastRelic.Hitbox), frames: 60);
            await WaitSecondsAsync(0.4);
            _report.Check("weirdshop_last_relic_inspectable_without_scroll", inspector.IsVisibleInTree()
                && player.Gold == gold, $"gold={player.Gold}/{gold}");
            await _pointer.TapAsync(dismiss);
            await WaitUntilAsync(() => !inspector.IsVisibleInTree(), "last relic inspector dismissed", ShortWait);
            await WaitSecondsAsync(0.4);
        }
        await CloseShopAndCheckAsync(inventory, "after_interaction");
        _report.Check("shop_framing_stable_after_inventory", ReadShopFraming(merchantButton) == roomFraming,
            "backdrop, party and merchant keep their room positions after inventory interactions");
        await CaptureAsync("shop_07_room_return");
        await _pointer.TapAsync(CenterOf(merchantButton));
        await WaitUntilAsync(() => inventory.IsOpen, "merchant reopens after Back", ShortWait);
        await WaitSecondsAsync(0.8);
        bool firstPage = weirdShop || (inventory.GetNodeOrNull<Control>("%CharacterCards")?.Visible == true
            && inventory.GetNodeOrNull<Control>("%Relics")?.Visible == false);
        _report.Check("shop_reopens_on_first_page", stock.IsVisibleInTree() && firstPage,
            $"stock={stock.GetGlobalRect()}");
        await CloseShopAndCheckAsync(inventory, "after_reopen");
        await _pointer.TapAsync(CenterOf(merchantButton));
        await WaitUntilAsync(() => inventory.IsOpen, "open for mid-slide Back", ShortWait);
        await CloseShopAndCheckAsync(inventory, "mid_slide", duringOpen: true);
        if (merchantButton.GetParent().GetParent()?.GetNodeOrNull<Control>("%ProceedButton") is not { } proceed)
        {
            _report.Fail("shop_proceed_target_reachable", "the room has no Proceed button");
            return;
        }
        CheckShopProceedTarget(proceed);
        await _pointer.TapAsync(CenterOf(proceed));
        await WaitUntilAsync(() => NMapScreen.Instance?.IsOpen == true, "shop Proceed opens the map", ShortWait);
        _report.Pass("shop_proceed_opens_map");
    }

    private void CheckShopItemsPage(NMerchantInventory inventory, Rect2 clip)
    {
        var items = inventory.GetAllSlots().Where(slot => slot.Entry.IsStocked).ToArray();
        _report.Check("shop_items_page_shows_all_stock_and_prices", items.OfType<NMerchantRelic>().Count() == 3
            && items.OfType<NMerchantPotion>().Count() == 3 && items.OfType<NMerchantCardRemoval>().Count() == 1
            && items.All(item => item.IsVisibleInTree() && clip.Encloses(ShopSlotBounds(item))),
            string.Join("; ", items.Select(item => $"{item.GetType().Name}: {ShopSlotBounds(item)}")));
        if (inventory.GetNodeOrNull<Control>("%CharacterCards") is not { } cards || inventory.GetNodeOrNull<Control>("%ColorlessCards") is not { } colorless)
        {
            _report.Fail("shop_cards_page_is_hidden_while_viewing_items", "the card groups are missing from the inventory");
            return;
        }
        _report.Check("shop_cards_page_is_hidden_while_viewing_items", !cards.Visible && !colorless.Visible,
            "card groups are not interactive on Items");
        CheckShopItemsLayout(inventory, clip);
    }

    private async Task CheckShopRemovalCancelAsync(NMerchantInventory inventory, Player player)
    {
        var removal = inventory.GetAllSlots().OfType<NMerchantCardRemoval>().FirstOrDefault(slot => slot.IsVisibleInTree());
        if (removal is null)
        {
            // A shop whose Items page is not shown has no reachable removal; report it instead of throwing out of the run.
            _report.Fail("shop_removal_slot_visible", "the shop shows no visible card-removal slot to open");
            return;
        }
        // An ascension run can start with less gold than the removal costs (10 gives 99 against 100); the cancel path
        // needs the purchase to open the deck select, and it still checks that cancelling spends nothing.
        if (player.Gold < removal.Entry.Cost)
            await PlayerCmd.GainGold(removal.Entry.Cost - player.Gold + 200, player);
        int gold = player.Gold;
        int deckSize = player.Deck.Cards.Count;
        await ConfirmShopSlotAsync(removal);
        if (!await WaitForAsync(() => FindFirst<NDeckCardSelectScreen>(_tree.Root)?.IsVisibleInTree() == true,
                "shop card removal selection", ShortWait))
        {
            _report.Fail("shop_removal_opens_deck_select", "the native deck select did not open from the removal slot");
            return;
        }
        var screen = FindFirst<NDeckCardSelectScreen>(_tree.Root);
        if (screen?.GetNodeOrNull<NCardGrid>("%CardGrid") is not { } grid)
        {
            _report.Fail("shop_removal_opens_deck_select", "the deck select has no card grid");
            return;
        }
        if (!await WaitForAsync(() => grid.CurrentlyDisplayedCardHolders.Any(), "shop removal cards displayed", ShortWait))
        {
            _report.Fail("shop_removal_cards_displayed", "the deck select showed no cards");
            return;
        }
        await CaptureAsync("shop_05b_removal_prompt");
        var holder = grid.CurrentlyDisplayedCardHolders.FirstOrDefault(card => card.IsVisibleInTree()
            && grid.GetGlobalRect().Encloses(card.Hitbox.GetGlobalRect()));
        if (holder is null)
        {
            _report.Fail("shop_removal_cards_displayed", "no deck-select card sits inside the grid");
            return;
        }
        await _pointer.TapAsync(CenterOf(holder.Hitbox));
        if (screen.GetNodeOrNull<Control>("%PreviewContainer") is not { } preview
            || preview.GetNodeOrNull<NConfirmButton>("%PreviewConfirm") is not { } confirm)
        {
            _report.Fail("shop_removal_preview_opens", "the deck select has no preview");
            return;
        }
        if (!await WaitForAsync(() => preview.IsVisibleInTree() && confirm.IsEnabled, "native shop removal preview", ShortWait))
        {
            _report.Fail("shop_removal_preview_opens", "the native card preview did not open");
            return;
        }
        await WaitSecondsAsync(0.55);
        if (preview.GetNodeOrNull<HBoxContainer>("%Cards") is not { } previewCards
            || FindAll<NCard>(previewCards).FirstOrDefault(card => card.IsVisibleInTree()) is not { } previewCard)
        {
            _report.Fail("shop_removal_previews_selected_card", "the preview shows no card");
            return;
        }
        _report.Check("shop_removal_previews_selected_card", previewCard.Model == holder.CardModel, $"card={previewCard.Model?.Id}");
        if (IsPortraitHost()) CheckShopRemovalPreview(previewCard, confirm);
        else _report.Check("shop_removal_landscape_keeps_native_scale", previewCards.Scale == Vector2.One, $"scale={previewCards.Scale}");
        await CaptureAsync("shop_05c_removal_preview");
        if (preview.GetNodeOrNull<Control>("%PreviewCancel") is not { } cancel)
        {
            _report.Fail("shop_removal_preview_back_keeps_selection_open", "the preview has no back button");
            return;
        }
        await _pointer.TapAsync(CenterOf(cancel));
        await WaitForAsync(() => !preview.Visible, "cancel shop removal preview", ShortWait);
        _report.Check("shop_removal_preview_back_keeps_selection_open", screen.IsVisibleInTree()
            && player.Gold == gold && player.Deck.Cards.Count == deckSize, $"gold={player.Gold}/{gold} deck={player.Deck.Cards.Count}/{deckSize}");
        var back = screen.GetNodeOrNull<NBackButton>("%Close");
        if (back is null)
        {
            _report.Fail("shop_removal_cancel_returns_to_items_without_charge", "the deck select has no close button");
            return;
        }
        await WaitSecondsAsync(0.5);
        await _pointer.TapAsync(CenterOf(back));
        await WaitForAsync(() => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree(), "cancel shop card removal", ShortWait);
        await WaitSecondsAsync(0.45);
        _report.Check("shop_removal_cancel_returns_to_items_without_charge", inventory.IsOpen && removal.IsVisibleInTree()
            && removal.Entry.IsStocked && player.Gold == gold && player.Deck.Cards.Count == deckSize,
            $"gold={player.Gold}/{gold}, deck={player.Deck.Cards.Count}/{deckSize}");
    }

    private async Task CloseShopAndCheckAsync(NMerchantInventory inventory, string suffix, bool duringOpen = false)
    {
        if (inventory.GetNodeOrNull<Control>("%SlotsContainer") is not { } stock
            || inventory.GetNodeOrNull<NBackButton>("%BackButton") is not { } back)
        {
            _report.Fail("shop_back_removes_entire_overlay_" + suffix, "the inventory has no stock container or Back button");
            return;
        }
        if (!await WaitForAsync(() => back.IsEnabled, "shop Back enabled", ShortWait))
        {
            _report.Fail("shop_back_removes_entire_overlay_" + suffix, "the shop Back button never enabled");
            return;
        }
        if (duringOpen)
            _report.Check("shop_back_during_opening_slide", stock.Position.Y < 79f, $"stock Y={stock.Position.Y:F1}");
        int commits = ShopCommits(), clears = ShopClears();
        await ArmShopSelectionAsync(inventory);
        bool hadSelection = ShopSelected();
        // A Back sliding in from the left edge is only tappable where it is on screen: wait for a finger-wide part of it,
        // then tap the middle of what is visible (a settled Back is wholly visible, so this is its centre).
        Rect2 screen = new(Vector2.Zero, GetViewportSize());
        const float fingerWidth = 64f;
        await WaitForAsync(() => back.GetGlobalRect().Intersection(screen).Size.X >= fingerWidth,
            "shop Back on screen", ShortWait);
        await _pointer.TapAsync(back.GetGlobalRect().Intersection(screen).GetCenter());
        await WaitForAsync(() => !inventory.IsOpen, "shop Back closes inventory", ShortWait);
        // The row leaves with the Back press, not after the stock's close slide. Two frames after the inventory reports
        // closed, a close-time tween must not still be keeping the tabs on screen.
        await WaitFramesAsync(2);
        string cardsTabName = ShopCardsTabNodeName();
        Control? cardsTab = cardsTabName.Length > 0 ? inventory.GetNodeOrNull<Control>(cardsTabName) : null;
        if (IsPortraitHost() && cardsTab is not null)
            _report.Check("shop_tabs_hide_at_close_start_" + suffix, cardsTab.Visible != true
                    && inventory.GetNodeOrNull<Control>(ShopItemsTabNodeName())?.Visible != true,
                $"cardsTab={cardsTab.Visible} itemsTab={inventory.GetNodeOrNull<Control>(ShopItemsTabNodeName())?.Visible}");
        await WaitSecondsAsync(0.9);
        Control? cloth = inventory.GetNodeOrNull<Control>(ShopClothNodeName());
        ColorRect? backstop = inventory.GetNodeOrNull<ColorRect>("Backstop");
        // The contract is that the closed shop leaves nothing on screen: hidden, or carried fully above the top edge (the
        // cloth is a child of the stock, so both leave together).
        bool stockGone = !stock.IsVisibleInTree() || stock.GetGlobalRect().End.Y <= 0f;
        bool clothGone = cloth is null || !cloth.IsVisibleInTree() || cloth.GetGlobalRect().End.Y <= 0f;
        _report.Check("shop_back_removes_entire_overlay_" + suffix,
            stockGone && clothGone && (backstop?.Modulate.A ?? 0f) < 0.01f
            && inventory.GetNodeOrNull<Control>(ShopItemsTabNodeName())?.Visible != true,
            $"cloth={cloth?.GetGlobalRect()} visible={cloth?.IsVisibleInTree()} stock={stock.GetGlobalRect()} " +
            $"visible={stock.IsVisibleInTree()} fade={backstop?.Modulate.A:F2}");
        CheckShopCleared(suffix, hadSelection, commits, clears);
    }

    /// <summary>
    /// Waits for a condition without throwing: a missing screen fails the scenario's next step instead of aborting the run
    /// inside a Godot signal callback (where an escaping exception kills the process before RESULTS.json is written).
    /// </summary>
    private async Task<bool> WaitForAsync(Func<bool> condition, string description, TimeSpan timeout)
    {
        try
        {
            await WaitUntilAsync(condition, description, timeout);
            return true;
        }
        catch (TimeoutException)
        {
            Log.Warn($"[TEST] timed out waiting for {description}");
            return false;
        }
    }

    /// <summary>
    /// The shop's Back enters along its own row from beyond the left edge, and the Cards/Items tabs beside it stand still:
    /// sampled every frame from the opening tap until both have settled. A Back seen anywhere else on the canvas (the game's
    /// own place is mid-screen in portrait) or a tab that moves fails.
    /// </summary>
    private async Task CheckShopBackEntranceAsync(NMerchantInventory inventory, string tag)
    {
        var samples = new List<(Rect2 Back, Rect2? Tab)>();
        Vector2 canvas = GetViewportSize();
        for (int i = 0; i < 70; i++)
        {
            await WaitFramesAsync(1);
            if (inventory.GetNodeOrNull<Control>("%BackButton") is not { } back || !back.IsVisibleInTree()) continue;
            string tabName = ShopCardsTabNodeName();
            Control? tab = tabName.Length > 0 ? inventory.GetNodeOrNull<Control>(tabName) : null;
            samples.Add((back.GetGlobalRect(), tab is not null && tab.IsVisibleInTree() ? tab.GetGlobalRect() : null));
        }
        if (samples.Count == 0)
        {
            _report.Fail($"shop_back_enters_on_its_row_{tag}", "the Back never showed");
            return;
        }
        Rect2 settled = samples[^1].Back;
        Rect2? settledTab = samples[^1].Tab;
        bool OnCanvas(Rect2 r) => r.End.X > 0f && r.Position.X < canvas.X && r.End.Y > 0f && r.Position.Y < canvas.Y;
        var strays = samples.Where(s => OnCanvas(s.Back) && Mathf.Abs(s.Back.Position.Y - settled.Position.Y) > 2f).ToList();
        _report.Check($"shop_back_enters_on_its_row_{tag}", strays.Count == 0,
            $"settled={settled} frames={samples.Count} strays={strays.Count} first={(strays.Count > 0 ? strays[0].Back : samples[0].Back)}");
        var tabMoves = settledTab is { } still
            ? samples.Where(s => s.Tab is { } t && t.Position.DistanceTo(still.Position) > 2f).ToList()
            : new List<(Rect2 Back, Rect2? Tab)>();
        _report.Check($"shop_tabs_hold_still_{tag}", tabMoves.Count == 0,
            $"settledTab={settledTab?.ToString() ?? "none"} moved={tabMoves.Count} first={(tabMoves.Count > 0 ? tabMoves[0].Tab : null)}");
    }
}
