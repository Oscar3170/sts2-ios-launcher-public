using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The launcher host's answers for the shop and Blitz scenarios (Batch B): the cloth, the Cards/Items pages and their tabs,
/// the rows' touch targets and prices, the framed native room, select-then-confirm purchases (first press only selects and
/// shows the native tip, the second tap confirms on release, hold inspects, a swipe cancels, a page change, a close and a
/// Courier restock clear the selection), the card-removal preview, and Blitz's stock and action lane.
/// <para>
/// Everything here is measured against the launcher's own contract: <see cref="SafeAreaCanvas"/>, <see cref="PortraitBands"/>
/// and <c>PortraitShopLayout.ShopPage</c>, with the merchant inventory's cloth named <c>PhoneShopCloth</c>, its tabs
/// <c>PhoneShopCardsTab</c>/<c>PhoneShopItemsTab</c>, and the selection published by <c>PhoneSelectConfirm</c>.
/// </para>
/// </summary>
internal sealed partial class TestDriver
{
    private const string LauncherShopClothNode = "PhoneShopCloth";
    private const string LauncherShopCardsTabNode = "PhoneShopCardsTab";
    private const string LauncherShopItemsTabNode = "PhoneShopItemsTab";
    private const string LauncherSelectedReticleNode = "PhoneSelectedReticle";
    // PortraitShopLayout.ShopPriceUnits: the price number's on-screen height the launcher's portrait shop keeps.
    private const float LauncherShopPriceUnits = 32f;
    // The measured opaque backdrop of the native merchant room, in BgContainer space (the original reference layout's
    // measurements of the same spine attachments; the launcher keeps the native room, so the same rectangle applies).
    private static readonly Rect2 NativeShopArtRect = new(-320f, -60f, 2560f, 1200f);
    private static readonly Rect2 WeirdShopArtRect = new(-575.64f, -179.23f, 2433f, 1440f);
    // Numbered page switch, so repeated switches report distinct steps.
    private int _shopPageSwitches;

    /// <summary>A scenario-prefixed step so both shop scenarios' results stay distinguishable.</summary>
    private void ShopCheck(string name, bool passed, string detail) =>
        _report.Check(_options.Scenario + "_" + name, passed, detail);

    /// <summary>The bottom of the run header: the first line the shop may use.</summary>
    private float LauncherShopHeaderBottom() => PortraitLayout.ComputeCapHeight(_tree.Root) + PortraitBands.RunContentTop;

    private partial string ShopClothNodeName() => LauncherShopClothNode;

    private partial string ShopCardsTabNodeName() => LauncherShopCardsTabNode;

    private partial string ShopItemsTabNodeName() => LauncherShopItemsTabNode;

    private partial int ShopCommits() => PhoneSelectConfirm.Confirms;

    private partial int ShopClears() => PhoneSelectConfirm.Clears;

    private partial Vector2 ShopDismissPoint() =>
        new(20f, Mathf.Max(40f, PortraitLayout.ComputeCapHeight(_tree.Root) * 0.5f));

    private partial void CheckShopStockGeometry(NMerchantInventory inventory, Control stock)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Vector2 canvas = GetViewportSize();
        Rect2 canvasRect = new(Vector2.Zero, canvas);
        float header = LauncherShopHeaderBottom();
        bool weird = inventory is NFakeMerchantInventory;
        Control? cloth = inventory.GetNodeOrNull<Control>(LauncherShopClothNode);
        Rect2 clip = stock.GetGlobalRect();
        Rect2 clothRect = cloth?.GetGlobalRect() ?? clip;
        ShopCheck("cloth_inside_canvas_below_header", cloth is { } c && c.IsVisibleInTree()
            && clothRect.Position.Y >= header - 1f && canvasRect.Grow(1f).Encloses(clothRect),
            $"cloth={clothRect} header={header:F0} canvas={canvasRect}");
        ShopCheck("cloth_encloses_stock", clothRect.Grow(1f).Encloses(clip), $"cloth={clothRect} clip={clip}");
        var entrySlots = inventory.GetAllSlots().Where(slot => slot.Entry.IsStocked).ToArray();
        if (weird)
        {
            ShopCheck("all_six_relics_fit_without_scrolling", entrySlots.Length == 6
                && entrySlots.All(slot => slot.IsVisibleInTree() && clip.Encloses(ShopSlotBounds(slot)))
                && inventory.GetNodeOrNull<Control>(LauncherShopItemsTabNode) is null,
                string.Join("; ", entrySlots.Select(slot => ShopSlotBounds(slot))));
            ShopCheck("has_no_pages", PortraitShopLayout.ShopPage == -1, $"ShopPage={PortraitShopLayout.ShopPage}");
            CheckLauncherShopTargets("stock", entrySlots, clip, clothRect);
            return;
        }
        var cards = entrySlots.OfType<NMerchantCard>().ToArray();
        ShopCheck("cards_page_shows_all_seven_cards_and_prices", entrySlots.Length == 7 && cards.Length == 7
            && cards.All(card => card.IsVisibleInTree() && clip.Encloses(ShopSlotBounds(card))),
            string.Join("; ", cards.Select(ShopSlotBounds)));
        Control? cardsTab = inventory.GetNodeOrNull<Control>(LauncherShopCardsTabNode);
        Control? itemsTab = inventory.GetNodeOrNull<Control>(LauncherShopItemsTabNode);
        Rect2[] tabRects = new[] { cardsTab, itemsTab }.OfType<Control>().Select(tab => tab.GetGlobalRect()).ToArray();
        ShopCheck("page_tabs_are_native_touch_targets", cardsTab is NSettingsTab && itemsTab is NSettingsTab
            && tabRects.Length == 2 && tabRects.All(tab => canvasRect.Grow(1f).Encloses(tab)
                && tab.Size.X >= PortraitBands.MinTarget && tab.Size.Y >= PortraitBands.MinTarget)
            && !tabRects[0].Intersects(tabRects[1]),
            string.Join("; ", tabRects));
        // The stock's rect ends where the tab row is reserved; the Back and both tabs belong below it. The tabs are placed
        // from the Back, so a Back left on the generic footer line lifted them into the last card row.
        ShopCheck("page_tabs_below_stock", tabRects.Length == 2
            && tabRects.All(tab => tab.Position.Y >= clip.End.Y - 2f),
            $"stock={clip} tabs={string.Join("; ", tabRects)}");
        ShopCheck("cards_page_is_selected", PortraitShopLayout.ShopPage == 0
            && GroupVisible(inventory, "CharacterCards") && GroupVisible(inventory, "ColorlessCards")
            && !GroupVisible(inventory, "Relics") && !GroupVisible(inventory, "Potions") && !GroupVisible(inventory, "MerchantCardRemoval"),
            $"ShopPage={PortraitShopLayout.ShopPage}");
        Control? back = inventory.GetNodeOrNull<Control>("%BackButton");
        // The launcher pads a native back button to 44 points with a PhoneHitArea child; that pad is the touch target.
        Rect2 backBox = back is null ? default : (PhoneHitArea.Of(back) ?? back).GetGlobalRect();
        ShopCheck("back_target_reachable", back is NClickableControl { IsEnabled: true }
            && backBox.Size.X >= PortraitBands.MinTarget && backBox.Size.Y >= PortraitBands.MinTarget
            && canvasRect.HasPoint(backBox.GetCenter()),
            $"back={back?.GetGlobalRect()} target={backBox} canvas={canvasRect}");
        CheckLauncherShopTargets("cards", cards, clip, clothRect);
    }

    /// <summary>Every visible slot is inside the cloth and either a 44-point target or limited by its row's pitch.</summary>
    private void CheckLauncherShopTargets(string name, IReadOnlyList<NMerchantSlot> slots, Rect2 clip, Rect2 clothRect)
    {
        var shown = slots.Where(slot => slot.IsVisibleInTree()).ToArray();
        Rect2[] hits = shown.Select(slot => slot.Hitbox.GetGlobalRect()).ToArray();
        ShopCheck(name + "_targets_inside_cloth", shown.Length > 0
            && hits.All(hit => clothRect.Grow(1f).Encloses(hit) && clip.Grow(1f).Encloses(hit)),
            $"cloth={clothRect}; targets={string.Join("; ", hits)}");
        bool sized = hits.All(hit => hit.Size.Y >= PortraitBands.MinTarget - 0.5f
            && (hit.Size.X >= PortraitBands.MinTarget - 0.5f || RowPitch(hit, hits) <= PortraitBands.MinTarget + 2f));
        ShopCheck(name + "_targets_are_touch_sized", sized,
            $"min={PortraitBands.MinTarget}; targets={string.Join("; ", hits.Select(hit => $"{hit.Size} pitch={RowPitch(hit, hits):F0}"))}");
        float[] prices = shown.Select(PriceHeight).OfType<float>().ToArray();
        ShopCheck(name + "_prices_readable", prices.Length > 0 && prices.All(height => height >= LauncherShopPriceUnits - 0.5f),
            $"prices={string.Join("; ", prices.Select(height => height.ToString("F0")))} floor={LauncherShopPriceUnits}");
    }

    /// <summary>The centre distance to the nearest neighbour in the same row; the most a target could grow before overlap.</summary>
    private static float RowPitch(Rect2 hit, Rect2[] hits) => hits
        .Where(other => other != hit && Mathf.Abs(other.GetCenter().Y - hit.GetCenter().Y) <= hit.Size.Y * 0.5f + 1f)
        .Select(other => Mathf.Abs(other.GetCenter().X - hit.GetCenter().X))
        .DefaultIfEmpty(float.MaxValue)
        .Min();

    /// <summary>A price's rendered number height in canvas units, or null when the slot has no price row.</summary>
    private static float? PriceHeight(NMerchantSlot slot)
    {
        if (slot.GetNodeOrNull<Control>("Cost") is not { } cost) return null;
        if (cost.GetNodeOrNull<Label>("CostLabel") is not { } label || !label.IsVisibleInTree()) return null;
        return label.GetThemeFontSize("font_size") * label.GetGlobalTransformWithCanvas().Scale.Y;
    }

    private static bool GroupVisible(NMerchantInventory inventory, string group) =>
        inventory.GetNodeOrNull<Control>("%" + group) is { } node && node.Visible;

    private partial void CheckShopItemsLayout(NMerchantInventory inventory, Rect2 clip)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        var items = inventory.GetAllSlots().Where(slot => slot.Entry.IsStocked).ToArray();
        Control? cloth = inventory.GetNodeOrNull<Control>(LauncherShopClothNode);
        Rect2 clothRect = cloth?.GetGlobalRect() ?? clip;
        ShopCheck("items_page_is_selected", PortraitShopLayout.ShopPage == 1
            && GroupVisible(inventory, "Relics") && GroupVisible(inventory, "Potions") && GroupVisible(inventory, "MerchantCardRemoval")
            && !GroupVisible(inventory, "CharacterCards") && !GroupVisible(inventory, "ColorlessCards"),
            $"ShopPage={PortraitShopLayout.ShopPage}");
        CheckLauncherShopTargets("items", items, clip, clothRect);
    }

    private partial async Task SelectShopPageAsync(NMerchantInventory inventory, bool items)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        if (inventory.GetNodeOrNull<Control>(items ? LauncherShopItemsTabNode : LauncherShopCardsTabNode) is not { } tab)
        {
            // Before the pages exist (or if the product regresses), fail one step and leave the rest of the shop scenario to
            // report instead of throwing out of a Godot signal callback.
            ShopCheck("page_tab_missing", false,
                $"the inventory has no {(items ? LauncherShopItemsTabNode : LauncherShopCardsTabNode)}");
            return;
        }
        int page = items ? 1 : 0;
        int clears = PhoneSelectConfirm.Clears;
        // Prove the page change drops a live selection: press one stocked item first (a first press only selects).
        await ArmShopSelectionAsync(inventory);
        bool armed = ShopSelected();
        await _pointer.TapAsync(CenterOf(tab));
        if (!await WaitForAsync(() => PortraitShopLayout.ShopPage == page, $"shop page becomes {(items ? "items" : "cards")}", ShortWait))
        {
            ShopCheck(items ? "items_page_selected" : "cards_page_selected", false,
                $"ShopPage={PortraitShopLayout.ShopPage} after tapping the tab");
            return;
        }
        await WaitSecondsAsync(0.3);
        string turn = (++_shopPageSwitches).ToString();
        ShopCheck((items ? "items" : "cards") + "_page_selected_" + turn, PortraitShopLayout.ShopPage == page
            && GroupVisible(inventory, items ? "Relics" : "CharacterCards")
            && !GroupVisible(inventory, items ? "CharacterCards" : "Relics"),
            $"ShopPage={PortraitShopLayout.ShopPage}");
        if (armed)
            ShopCheck("page_change_clears_selection_" + turn, PhoneSelectConfirm.SelectedOwner is null && PhoneSelectConfirm.Clears > clears,
                $"selected={(PhoneSelectConfirm.SelectedOwner is null ? "none" : PhoneSelectConfirm.SelectedName)} clears={PhoneSelectConfirm.Clears}/{clears}");
    }

    private partial void CheckShopRoomFraming(NMerchantButton merchant, bool weird)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Control scene = merchant.GetParent<Control>();
        Vector2 canvas = GetViewportSize();
        Rect2 canvasRect = new(Vector2.Zero, canvas);
        float header = LauncherShopHeaderBottom();
        ShopFraming framing = ReadShopFraming(merchant);
        Rect2 art = framing.Art * (weird ? WeirdShopArtRect : NativeShopArtRect);
        Rect2 band = new(0f, header, canvas.X, canvas.Y - header);
        ShopCheck("room_art_covers_canvas_below_header", art.Grow(1f).Encloses(band), $"art={art} band={band}");
        Rect2 merchantBox = merchant.GetGlobalRect();
        ShopCheck("merchant_target_inside_canvas", merchantBox.Size.Y >= PortraitBands.MinTarget
            && canvasRect.Grow(24f).Encloses(merchantBox), $"merchant={merchantBox}");
        if (scene.GetNodeOrNull<Control>("CharacterContainer") is { } party)
            ShopCheck("party_on_canvas", canvasRect.Grow(1f).Encloses(party.GetGlobalRect()), $"party={party.GetGlobalRect()}");
    }

    private partial async Task ConfirmShopSlotAsync(NMerchantSlot slot)
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            // Landscape keeps every native screen's single tap.
            await _pointer.TapAsync(CenterOf(slot.Hitbox));
            return;
        }
        if (ShopPlayer() is not { } player)
        {
            ShopCheck(ShopKind(slot) + "_first_press_only_selects", false, "the run state disappeared");
            return;
        }
        int gold = player.Gold;
        int commits = PhoneSelectConfirm.Confirms;
        Vector2 point = CenterOf(slot.Hitbox);
        string kind = ShopKind(slot);
        var resting = RestingStock(slot);
        await _pointer.TapAsync(point);
        await SampleTipNeverDropsAsync(slot, kind + "_selection_tip_never_drops");
        await WaitUntilAsync(() => PhoneSelectConfirm.SelectedOwner is not null, "shop press selects a slot", ShortWait);
        Control? reticle = ReticleNode(slot);
        ShopCheck(kind + "_first_press_only_selects", reticle is not null
            && ReferenceEquals(PhoneSelectConfirm.SelectedOwner, slot)
            && !string.IsNullOrEmpty(PhoneSelectConfirm.SelectedName)
            && player.Gold == gold && PhoneSelectConfirm.Confirms == commits,
            $"selected={PhoneSelectConfirm.SelectedName ?? "none"} reticle={reticle?.GetGlobalRect()} gold={player.Gold}/{gold} confirms={PhoneSelectConfirm.Confirms}/{commits}");
        await CheckReticleAsync(slot, slot.Hitbox, kind + "_first_press");
        NHoverTipSet? tip = null;
        // The selecting press creates the slot's tip; the launcher keeps it up while the item stays selected, so it is still
        // there a frame later (the per-frame sample above already proved it never dropped).
        bool tipShown = await WaitForAsync(() => (tip = VisibleTipOwner(slot)) is not null,
            "shop selection shows a native tip", ShortWait);
        ShopCheck(kind + "_selection_shows_native_tip", tipShown,
            $"tip={tip?.Name ?? "none"} for {slot.GetType().Name}");
        await WaitSecondsAsync(0.15);
        await CaptureAsync(_options.Scenario + "_" + kind + "_selected_with_tip");
        await _pointer.TapAsync(point);
        await WaitUntilAsync(() => PhoneSelectConfirm.Confirms == commits + 1, "shop second tap confirms", MediumWait);
        await WaitSecondsAsync(0.4);
        ShopCheck(kind + "_second_tap_confirms_once", PhoneSelectConfirm.Confirms == commits + 1,
            $"confirms={PhoneSelectConfirm.Confirms}/{commits + 1}");
        bool reticleGone = await AwaitReticleGoneAsync(slot, "the purchased slot's reticle fades and is freed");
        ShopCheck(kind + "_reticle_gone_after_confirm", reticleGone,
            $"reticle={(ReticleNode(slot, activeOnly: false) is { } left ? left.GetGlobalRect().ToString() : "none")}");
        bool stable = resting.All(entry => !GodotObject.IsInstanceValid(entry.Slot) || entry.Slot == slot
            || entry.Slot.Position.IsEqualApprox(entry.Position));
        ShopCheck(kind + "_stock_positions_unchanged_after_purchase", stable,
            string.Join("; ", resting.Where(entry => entry.Slot != slot && GodotObject.IsInstanceValid(entry.Slot))
                .Select(entry => $"{entry.Slot.GetType().Name} {entry.Position}->{entry.Slot.Position}")));
    }

    /// <summary>The local player, or null when the run state has gone: the check reports, never throws mid-scenario.</summary>
    private static Player? ShopPlayer() => RunManager.Instance.DebugOnlyGetState()?.Players.FirstOrDefault();

    /// <summary>A slot's kind, used to keep each purchase's step names distinct.</summary>
    private static string ShopKind(NMerchantSlot slot) => slot.GetType().Name switch
    {
        "NMerchantCard" => "card",
        "NMerchantRelic" => "relic",
        "NMerchantPotion" => "potion",
        "NMerchantCardRemoval" => "removal",
        _ => "slot",
    };

    /// <summary>Every slot's resting position in the stock container around <paramref name="slot"/>.</summary>
    private static (NMerchantSlot Slot, Vector2 Position)[] RestingStock(NMerchantSlot slot)
    {
        if (slot.GetParent()?.GetParent() is not Control stock) return Array.Empty<(NMerchantSlot, Vector2)>();
        var resting = new List<(NMerchantSlot, Vector2)>();
        foreach (string group in new[] { "CharacterCards", "ColorlessCards", "Relics", "Potions", "BlitzSlots" })
            if (stock.GetNodeOrNull<Control>(group) is { } container)
                foreach (NMerchantSlot item in container.GetChildren().OfType<NMerchantSlot>())
                    resting.Add((item, item.Position));
        if (stock.GetNodeOrNull<NMerchantSlot>("MerchantCardRemoval") is { } removal) resting.Add((removal, removal.Position));
        return resting.ToArray();
    }

    /// <summary>The visible native hover tip owned by <paramref name="owner"/>, if any. The owner field is private.</summary>
    private static NHoverTipSet? VisibleTipOwner(Control owner)
    {
        if (NGame.Instance?.HoverTipsContainer is not { } container) return null;
        FieldInfo? ownerField = typeof(NHoverTipSet).GetField("_owner", BindingFlags.Instance | BindingFlags.NonPublic);
        if (ownerField is null) return null;
        foreach (NHoverTipSet tip in FindStatic<NHoverTipSet>(container))
            if (tip.IsVisibleInTree() && ReferenceEquals(ownerField.GetValue(tip), owner)) return tip;
        return null;
    }

    /// <summary>
    /// Samples the selected item's native tip every frame for 0.5 s from the selecting release. A shop slot's tip belongs to
    /// its hover state, so the release can take it down; the launcher must put it back for the item that stays selected, with
    /// no frame in between where the description is gone.
    /// </summary>
    private async Task<bool> SampleTipNeverDropsAsync(Control slot, string step)
    {
        ulong start = Time.GetTicksMsec();
        int frames = 0;
        while (Time.GetTicksMsec() - start < 500)
        {
            if (VisibleTipOwner(slot) is null)
            {
                ShopCheck(step, false,
                    $"tip missing {Time.GetTicksMsec() - start} ms after the selecting release ({frames} frame(s) sampled)");
                return false;
            }
            frames++;
            await WaitFramesAsync(1);
        }
        ShopCheck(step, true, $"tip up on all {frames} sampled frame(s) over {Time.GetTicksMsec() - start} ms");
        return true;
    }

    private static IEnumerable<T> FindStatic<T>(Node root) where T : Node
    {
        if (root is T match) yield return match;
        foreach (Node child in root.GetChildren())
            foreach (T found in FindStatic<T>(child))
                yield return found;
    }

    private partial async Task BuyShopSlotAsync(NMerchantSlot slot)
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            await _pointer.TapAsync(CenterOf(slot.Hitbox));
            return;
        }
        if (ShopPlayer() is not { } player)
        {
            ShopCheck(ShopKind(slot) + "_purchase_pays_the_listed_price", false, "the run state disappeared");
            return;
        }
        int price = slot.Entry.Cost;
        int gold = player.Gold;
        await ConfirmShopSlotAsync(slot);
        await WaitSecondsAsync(0.35);
        ShopCheck(ShopKind(slot) + "_purchase_pays_the_listed_price", player.Gold == gold - price,
            $"gold={player.Gold}/{gold} price={price}");
    }

    private partial async Task CheckShopRestockSelectionClearedAsync(NMerchantSlot slot, Player player)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        string kind = ShopKind(slot);
        ShopCheck(kind + "_restock_clears_selection", PhoneSelectConfirm.SelectedOwner is null,
            $"selected={(PhoneSelectConfirm.SelectedOwner is null ? "none" : PhoneSelectConfirm.SelectedName)}");
        int gold = player.Gold;
        int commits = PhoneSelectConfirm.Confirms;
        await _pointer.TapAsync(CenterOf(slot.Hitbox));
        await WaitSecondsAsync(0.4);
        ShopCheck(kind + "_restocked_slot_needs_a_new_selection", player.Gold == gold && PhoneSelectConfirm.Confirms == commits
            && PhoneSelectConfirm.SelectedOwner is not null,
            $"gold={player.Gold}/{gold} confirms={PhoneSelectConfirm.Confirms}/{commits} selected={PhoneSelectConfirm.SelectedName ?? "none"}");
        await _pointer.TapAsync(ShopDismissPoint());
        await WaitSecondsAsync(0.2);
    }

    private partial void CheckShopHoldHost(int goldBefore, int commitsBefore, int clearsBefore)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        if (ShopPlayer() is not { } player)
        {
            ShopCheck("hold_inspects_without_confirming", false, "the run state disappeared");
            return;
        }
        ShopCheck("hold_inspects_without_confirming", PhoneSelectConfirm.HoldInspects > 0
            && PhoneSelectConfirm.Confirms == commitsBefore && player.Gold == goldBefore,
            $"holds={PhoneSelectConfirm.HoldInspects} confirms={PhoneSelectConfirm.Confirms}/{commitsBefore} gold={player.Gold}/{goldBefore}");
        ShopCheck("press_elsewhere_clears_selection", PhoneSelectConfirm.SelectedOwner is null
            && PhoneSelectConfirm.Clears > clearsBefore,
            $"selected={(PhoneSelectConfirm.SelectedOwner is null ? "none" : PhoneSelectConfirm.SelectedName)} clears={PhoneSelectConfirm.Clears}/{clearsBefore}");
    }

    private partial async Task CheckShopSwipeHostAsync(NMerchantSlot slot, int goldBefore, int commitsBefore, int clearsBefore)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        if (ShopPlayer() is not { } player)
        {
            ShopCheck("swipe_does_not_buy", false, "the run state disappeared");
            return;
        }
        bool selected = PhoneSelectConfirm.SelectedOwner is { } owner && ReferenceEquals(owner, slot);
        ShopCheck("swipe_does_not_buy", PhoneSelectConfirm.Confirms == commitsBefore && player.Gold == goldBefore,
            $"confirms={PhoneSelectConfirm.Confirms}/{commitsBefore} gold={player.Gold}/{goldBefore}");
        // The swipe cancels the confirmation: the item is either let go or still selected, but never left with a half-armed
        // release, and no other item took its place.
        ShopCheck("swipe_leaves_no_confirmation", PhoneSelectConfirm.SelectedOwner is null || selected,
            $"selected={(PhoneSelectConfirm.SelectedOwner is null ? "none" : PhoneSelectConfirm.SelectedName)} clears={PhoneSelectConfirm.Clears}/{clearsBefore}");
        await _pointer.TapAsync(ShopDismissPoint());
        await WaitSecondsAsync(0.2);
    }

    private partial void CheckShopCleared(string reason, bool hadSelection, int commitsBefore, int clearsBefore)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        ShopCheck("close_clears_selection_" + reason,
            PhoneSelectConfirm.SelectedOwner is null && PhoneSelectConfirm.SelectedName is null
            && PhoneSelectConfirm.Confirms == commitsBefore && (!hadSelection || PhoneSelectConfirm.Clears > clearsBefore),
            $"selected={(PhoneSelectConfirm.SelectedOwner is null ? "none" : PhoneSelectConfirm.SelectedName)} " +
            $"clears={PhoneSelectConfirm.Clears}/{clearsBefore} confirms={PhoneSelectConfirm.Confirms}/{commitsBefore} " +
            $"hadSelection={hadSelection}");
    }

    private partial async Task ArmShopSelectionAsync(NMerchantInventory inventory)
    {
        if (!SafeAreaCanvas.IsPortrait || !PhoneSelectConfirm.Installed || PhoneSelectConfirm.SelectedOwner is not null) return;
        var slot = inventory.GetAllSlots().FirstOrDefault(candidate => candidate.Entry.IsStocked
            && candidate.GetNodeOrNull<Control>("%Hitbox") is { } hitbox && hitbox.IsVisibleInTree());
        if (slot is null) return;
        await _pointer.TapAsync(CenterOf(slot.Hitbox));
        await WaitSecondsAsync(0.2);
    }

    private partial bool ShopSelected() => PhoneSelectConfirm.SelectedOwner is not null;

    private partial void CheckShopProceedTarget(Control proceed)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Rect2 box = proceed.GetGlobalRect();
        Vector2 canvas = GetViewportSize();
        bool enabled = proceed is not NClickableControl clickable || clickable.IsEnabled;
        ShopCheck("proceed_target_reachable", new Rect2(Vector2.Zero, canvas).Grow(1f).Encloses(box)
            && box.Size.X >= PortraitBands.MinTarget && box.Size.Y >= PortraitBands.MinTarget && enabled,
            $"proceed={box} canvas={canvas} enabled={enabled}");
    }

    private partial void CheckShopRemovalPreview(NCard previewCard, NConfirmButton confirm)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Rect2 button = confirm.GetGlobalRect();
        Vector2 size = NCard.defaultSize * previewCard.GetGlobalTransform().Scale;
        Rect2 bounds = new(previewCard.GlobalPosition - size * 0.5f, size);
        Vector2 canvas = GetViewportSize();
        Rect2 canvasRect = new(Vector2.Zero, canvas);
        ShopCheck("removal_preview_confirm_is_a_phone_target", canvasRect.Grow(1f).Encloses(button)
            && button.Size.Y >= PortraitBands.MinTarget && canvasRect.Grow(1f).Encloses(bounds),
            $"card={bounds} confirm={button}");
    }

    // ---- Blitz ----------------------------------------------------------------------------------------------------------

    private partial void CheckBlitzHeaderLayout(string name, NTopBar topBar)
    {
        Vector2 canvas = GetViewportSize();
        Rect2 canvasRect = new(Vector2.Zero, canvas);
        NBlitzTopBarMap map = topBar.BlitzMap;
        Rect2[] points = map.MapPoints.Select(point => point.GetGlobalRect()).ToArray();
        _report.Check(name + "_map_on_canvas", points.Length > 1 && points.All(point => canvasRect.Grow(1f).Encloses(point)),
            $"canvas={canvas}, points={string.Join("; ", points)}");
    }

    private partial void CheckBlitzMerchantLayout(NBlitzMerchantRoom room)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Vector2 canvas = GetViewportSize();
        Rect2 merchant = room.MerchantButton.GetGlobalRect();
        _report.Check("blitz_merchant_reachable", new Rect2(Vector2.Zero, canvas).Grow(1f).Encloses(merchant)
            && merchant.Size.Y >= PortraitBands.MinTarget, $"merchant={merchant} canvas={canvas}");
        CheckBlitzActionLane("blitz_merchant_proceed", room.ProceedButton, merchant);
        Control? party = room.GetNodeOrNull<Control>("%CharacterContainer");
        Rect2 canvasRect = new(Vector2.Zero, canvas);
        bool partyOnCanvas = party is not null && room.PlayerVisuals.Count > 0
            && room.PlayerVisuals.All(v => v.IsVisibleInTree()
                && canvasRect.Grow(1f).HasPoint(v.GetGlobalTransformWithCanvas().Origin));
        _report.Check("blitz_party_on_canvas", partyOnCanvas,
            $"party={party?.GetGlobalRect()} visuals={string.Join("; ", room.PlayerVisuals.Select(v =>
                $"{v.GetType().Name} vis={v.IsVisibleInTree()} pos={v.GetGlobalTransformWithCanvas().Origin}"))} canvas={canvas}");
    }

    private partial void CheckBlitzInventoryLayout(NMerchantInventory inventory, NBlitzMerchantSlot[] slots)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Vector2 canvas = GetViewportSize();
        Rect2 canvasRect = new(Vector2.Zero, canvas);
        Control? cloth = inventory.GetNodeOrNull<Control>(LauncherShopClothNode);
        Rect2 frame = cloth?.GetGlobalRect() ?? inventory.GetNode<Control>("%SlotsContainer").GetGlobalRect();
        Rect2[] hits = slots.Select(slot => slot.Hitbox.GetGlobalRect()).ToArray();
        _report.Check("blitz_inventory_targets_inside_cloth", cloth is { } c && c.IsVisibleInTree()
            && canvasRect.Grow(1f).Encloses(frame) && hits.All(hit => frame.Grow(1f).Encloses(hit)),
            $"cloth={frame}; slots={string.Join("; ", hits)}");
        Control? footer = inventory.GetNodeOrNull<Control>("%BlitzSelectionText");
        _report.Check("blitz_footer_inside_cloth", footer is null || !footer.IsVisibleInTree()
            || frame.Grow(1f).Encloses(footer.GetGlobalRect()), $"footer={footer?.GetGlobalRect()} cloth={frame}");
    }

    /// <summary>
    /// The native line is re-laid out by the launcher at a fixed phone font size and the page's width. After every
    /// purchase its text changes, so this checks that the new sentence still fits the cloth without clipping.
    /// </summary>
    private partial void CheckBlitzFooterLayout(NMerchantInventory inventory, Control footer, int remaining)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        string step = "blitz_footer_fit_" + (++_shopStatesFooterChecks);
        Control? cloth = inventory.GetNodeOrNull<Control>(LauncherShopClothNode);
        Rect2 frame = cloth?.GetGlobalRect() ?? inventory.GetNode<Control>("%SlotsContainer").GetGlobalRect();
        Rect2 box = footer.GetGlobalRect();
        if (footer is not RichTextLabel label)
        {
            ShopCheck(step, false, $"the footer is {footer.GetType().Name}, not a RichTextLabel");
            return;
        }
        Font font = label.GetThemeFont("normal_font");
        int size = label.GetThemeFontSize("normal_font_size");
        string text = label.GetParsedText();
        Vector2 measured = font.GetStringSize(text, HorizontalAlignment.Left, -1, size);
        float scale = footer.GetGlobalTransformWithCanvas().Scale.X;
        float width = measured.X * scale;
        float height = measured.Y * scale;
        float content = label.GetContentHeight();
        bool fits = footer.IsVisibleInTree() && frame.Grow(1f).Encloses(box)
            && width <= box.Size.X + 1f && height <= box.Size.Y + 1f && content <= box.Size.Y + 1f
            && size >= PortraitShopMath.BlitzFooterFontSize - 1;
        ShopCheck(step, fits, $"text='{text}' width={width:F0}/{box.Size.X:F0} height={height:F0} " +
            $"content={content:F0}/{box.Size.Y:F0} font={size} box={box} frame={frame} remaining={remaining}");
    }

    /// <summary>
    /// Selected reticles are children of the slot's hitbox; only a selected one counts (a deselected reticle fades out for
    /// 0.2 s before it is freed), and none may survive a purchase or a page change.
    /// </summary>
    private partial int ShopHighlightCount(NMerchantInventory inventory) =>
        FindAll<Control>(inventory).Count(node => node.Name.ToString() == LauncherSelectedReticleNode &&
                                                   node.Get("IsSelected").AsBool());

    private partial void CheckBlitzChooseThreeLayout(NCardGrid grid, NGridCardHolder[] cards)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        // The blessing's grid is laid out from the top bar (cap + TopBarHeight + 8): this overlay keeps the potion row but
        // not the relic strip, so the relic band must not be used as its floor.
        float header = PortraitLayout.ComputeCapHeight(_tree.Root) + PortraitBands.TopBarHeight;
        _report.Check("blitz_choose_three_first_row_clear", cards.Length == 3 && cards.All(holder =>
            holder.Hitbox.GetGlobalRect().Position.Y > header && grid.GetGlobalRect().Encloses(holder.Hitbox.GetGlobalRect())),
            $"header={header:F0}, cards={string.Join("; ", cards.Select(holder => holder.Hitbox.GetGlobalRect()))}");
    }

    private partial void CheckBlitzActionLane(string name, NProceedButton action, Rect2? content)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Rect2 box = action.GetGlobalRect();
        Vector2 canvas = GetViewportSize();
        _report.Check(name, new Rect2(Vector2.Zero, canvas).Grow(1f).Encloses(box)
            && box.Size.X >= PortraitBands.MinTarget && box.Size.Y >= PortraitBands.MinTarget
            && !(content?.Intersects(box) ?? false),
            $"action={box} canvas={canvas} content={content}");
    }

    private partial void CheckBlitzSkipProceedPosition(Vector2 skipCenter, Vector2 proceedCenter)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        // One owned vertical centre for the loot lane: the native re-enable tween may not move Skip and Proceed apart.
        _report.Check("blitz_skip_and_proceed_share_position", skipCenter.DistanceTo(proceedCenter) < 2f,
            $"skip={skipCenter} proceed={proceedCenter} delta={skipCenter.DistanceTo(proceedCenter):F1}");
    }

    private partial void CheckBlitzLootAction(string name, NProceedButton action, Rect2? content)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Rect2 box = action.GetGlobalRect();
        Vector2 canvas = GetViewportSize();
        float laneCentre = canvas.Y * LauncherLaneCentreFactor;
        Rect2 overlap = content is { } panel ? box.Intersection(panel) : default;
        bool onCanvas = LauncherOnCanvas(box, canvas) && box.Size.X >= PortraitBands.MinTarget - 0.5f
            && box.Size.Y >= PortraitBands.MinTarget - 0.5f && !(content?.Intersects(box) ?? false);
        bool inLane = Mathf.Abs(box.End.X - LauncherLaneRightEdge(canvas.X)) < 25f
            && Mathf.Abs(box.GetCenter().Y - laneCentre) < 20f;
        _report.Check(name, onCanvas && inLane,
            $"action={box} canvas={canvas} lane=({LauncherLaneRightEdge(canvas.X):F0},{laneCentre:F0}) " +
            $"content={content} overlap={overlap}");
    }

    private partial void CheckBlitzLootLayout(NRewardsScreen loot, Control panel)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Rect2 box = panel.GetGlobalRect();
        Vector2 canvas = GetViewportSize();
        // The loot overlay keeps the top bar and potion row but not the relic strip, so its floor is the top-bar band.
        float header = PortraitLayout.ComputeCapHeight(_tree.Root) + PortraitBands.TopBarHeight;
        _report.Check("blitz_loot_panel_on_canvas", LauncherOnCanvas(box, canvas) && box.Position.Y >= header - 1f,
            $"panel={box} canvas={canvas} header={header:F0}");
        _report.Check("blitz_loot_panel_phone_width", box.Size.X >= canvas.X * 0.74f - 0.5f,
            $"panel={box} width={box.Size.X:F0} floor={canvas.X * 0.74f:F0}");
        // The skip/proceed action's lane: its centre at 5/8 of the height, its grown rectangle 127 tall.
        var action = loot.GetNode<NProceedButton>("ProceedButton");
        float laneTop = canvas.Y * LauncherLaneCentreFactor - action.GetGlobalRect().Size.Y / 2f;
        _report.Check("blitz_loot_panel_clear_of_lane", box.End.Y <= laneTop - 8f,
            $"panelEnd={box.End.Y:F1} laneTop={laneTop:F1} action={action.GetGlobalRect()}");
        NRewardButton[] rows = FindAll<NRewardButton>(loot)
            .Where(row => row.IsVisibleInTree() && !row.IsQueuedForDeletion()).ToArray();
        Rect2? firstRow = rows.Select(row => row.GetGlobalRect())
            .OrderBy(rect => rect.Position.Y).Cast<Rect2?>().FirstOrDefault();
        Rect2 mask = panel.GetNode<Control>("%RewardContainerMask").GetGlobalRect();
        _report.Check("blitz_loot_rows_phone_size",
            firstRow is { } row && row.Size.Y >= PortraitBands.MinTarget - 0.5f
            && mask.Intersects(row) && mask.Size.Y >= row.Size.Y * 2f,
            $"row={firstRow} mask={mask} rows={rows.Length}");
    }

    private partial async Task TakeBlitzRewardCardAsync(NCardRewardSelectionScreen screen, NGridCardHolder holder)
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            await _pointer.TapAsync(CenterOf(holder.Hitbox));
            return;
        }
        int commits = PhoneSelectConfirm.Confirms;
        Vector2 point = CenterOf(holder.Hitbox);
        await _pointer.TapAsync(point);
        await WaitUntilAsync(() => ReticleNode(holder) is not null,
            "blitz reward card reticle", ShortWait);
        await CheckReticleAsync(holder, holder.Hitbox, "blitz_reward_card");
        _report.Check("blitz_reward_card_selected_not_taken", screen.IsVisibleInTree()
            && !string.IsNullOrEmpty(PhoneSelectConfirm.SelectedName) && PhoneSelectConfirm.Confirms == commits,
            $"selected={PhoneSelectConfirm.SelectedName ?? "none"} confirms={PhoneSelectConfirm.Confirms}/{commits}");
        await _pointer.TapAsync(point);
        await WaitUntilAsync(() => PhoneSelectConfirm.Confirms == commits + 1, "blitz reward card confirmed", MediumWait);
        await WaitSecondsAsync(0.3);
        _report.Check("blitz_reward_card_taken_by_second_tap", PhoneSelectConfirm.Confirms == commits + 1,
            $"confirms={PhoneSelectConfirm.Confirms}/{commits + 1}");
    }
}
