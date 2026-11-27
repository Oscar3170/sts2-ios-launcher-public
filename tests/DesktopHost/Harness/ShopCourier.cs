using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The Courier restock fixture. Each purchase goes through <see cref="ConfirmShopSlotAsync"/>: first-press-select then
/// second-tap-confirm on the launcher. The slot node and its merchant entry survive the restock; only the
/// card/relic/potion model changes, which is exactly the identity the launcher's selection must key on, so every
/// purchase also checks that the replacement did not inherit the confirmation.
/// </summary>
internal sealed partial class TestDriver
{
    private static async Task PrepareShopCourierAsync(Player player)
    {
        await RelicCmd.Obtain<TheCourier>(player);
        await PlayerCmd.GainGold(1500, player);
    }

    private async Task BuyCourierStockAsync(NMerchantInventory inventory, Player player, Rect2 clip)
    {
        _report.Check("shop_courier_equipped", player.Relics.Any(relic => relic is TheCourier), "native Courier relic on the player");
        var card = inventory.GetAllSlots().OfType<NMerchantCard>().First(s => s.Entry.IsStocked);
        var cardEntry = (MerchantCardEntry)card.Entry;
        await BuyAndCheckRestockAsync("card", card, player, clip, () => cardEntry.CreationResult?.Card,
            model => player.Deck.Cards.Count(c => c.Id == model.Id));
        await CaptureAsync("shop_courier_cards_restocked");
        await SelectShopPageAsync(inventory, items: true);
        var potion = inventory.GetAllSlots().OfType<NMerchantPotion>().First(s => s.Entry.IsStocked);
        var potionEntry = (MerchantPotionEntry)potion.Entry;
        await BuyAndCheckRestockAsync("potion", potion, player, clip, () => potionEntry.Model,
            model => player.Potions.Count(p => p.Id == model.Id));
        var relic = inventory.GetAllSlots().OfType<NMerchantRelic>().First(s => s.Entry.IsStocked);
        var relicEntry = (MerchantRelicEntry)relic.Entry;
        await BuyAndCheckRestockAsync("relic", relic, player, clip, () => relicEntry.Model,
            model => player.Relics.Count(r => r.Id == model.Id));
        await CaptureAsync("shop_courier_items_restocked");

        AbstractModel? nextCard = cardEntry.CreationResult?.Card;
        AbstractModel? nextPotion = potionEntry.Model;
        AbstractModel? nextRelic = relicEntry.Model;
        await SelectShopPageAsync(inventory, items: false);
        _report.Check("shop_courier_card_replacement_survives_page_switch", card.IsVisibleInTree() && !relic.IsVisibleInTree()
            && card.Entry.IsStocked && ReferenceEquals(cardEntry.CreationResult?.Card, nextCard) && clip.Encloses(ShopSlotBounds(card)),
            $"restocked {nextCard?.Id}");
        await SelectShopPageAsync(inventory, items: true);
        _report.Check("shop_courier_item_replacements_survive_page_switch", !card.IsVisibleInTree() && potion.IsVisibleInTree()
            && relic.IsVisibleInTree() && potion.Entry.IsStocked && relic.Entry.IsStocked
            && ReferenceEquals(potionEntry.Model, nextPotion) && ReferenceEquals(relicEntry.Model, nextRelic)
            && clip.Encloses(ShopSlotBounds(potion)) && clip.Encloses(ShopSlotBounds(relic)),
            $"restocked {nextPotion?.Id} and {nextRelic?.Id}");
    }

    private async Task BuyAndCheckRestockAsync(string kind, NMerchantSlot slot, Player player, Rect2 clip,
        Func<AbstractModel?> modelInSlot, Func<AbstractModel, int> ownedQuantity)
    {
        var before = modelInSlot() ?? throw new InvalidOperationException($"Courier fixture lacks a {kind}");
        int gold = player.Gold;
        int price = slot.Entry.Cost;
        int owned = ownedQuantity(before);
        Vector2 position = slot.Position;
        await BuyShopSlotAsync(slot);
        await WaitUntilAsync(() => modelInSlot() is { } replacement && !ReferenceEquals(replacement, before),
            $"Courier restocks {kind} after purchase", MediumWait);
        // The finger is still on the slot and it is at the native hover scale (0.8 against the resting 0.65), which grows
        // its hitbox by ~12 % around its centre: on the first row that pokes a few units past the stock's clip. Park the
        // pointer and let the hover tween settle, so the check measures the resting stock (positions, clip containment)
        // rather than the hover.
        await _pointer.MoveAsync(ShopDismissPoint(), pressed: false);
        await WaitSecondsAsync(0.5);
        Rect2 bounds = ShopSlotBounds(slot);
        int ownedNow = ownedQuantity(before);
        _report.Check($"shop_courier_buys_and_restocks_{kind}",
            ownedNow == owned + 1 && player.Gold == gold - price && slot.Entry.IsStocked
            && slot.Position.IsEqualApprox(position) && clip.Encloses(bounds),
            $"{before.Id} → {modelInSlot()?.Id}, gold={player.Gold}/{gold}, price={price}, " +
            $"owned={owned}->{ownedNow}, pos={position}->{slot.Position}, bounds={bounds}, clip={clip}");
        await CheckShopRestockSelectionClearedAsync(slot, player);
    }
}
