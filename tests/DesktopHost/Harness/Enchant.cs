using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

internal sealed partial class TestDriver
{
    private async Task RunEnchantAsync(RunState state)
    {
        await RunManager.Instance.EnterRoomDebug(RoomType.RestSite, showTransition: false);
        var pile = state.Players[0].Deck;
        foreach (int count in new[] { 1, 2 })
        {
            EnchantmentModel model = count == 1 ? ModelDb.Enchantment<Swift>() : ModelDb.Enchantment<Momentum>();
            if (count == 1 && IsPortraitHost())
            {
                var original = pile.Cards.Where(model.CanEnchant).ToArray();
                for (int i = original.Length; i < 28 && original.Length > 0; i++)
                    pile.AddInternal(state.CloneCard(original[i % original.Length]), silent: true);
            }
            var deck = pile.Cards.Where(c => c.Enchantment is null && model.CanEnchant(c)).ToArray();
            string path = count == 1 ? "%EnchantSinglePreviewContainer" : "%EnchantMultiPreviewContainer";
            var prefs = new CardSelectorPrefs(CardSelectorPrefs.EnchantSelectionPrompt, count) { Cancelable = true };
            int amount = count == 1 ? 2 : 1;
            var screen = NDeckEnchantSelectScreen.ShowScreen(deck, model, amount, prefs);
            var selection = screen.CardsSelected();
            var preview = screen.GetNode<Control>(path);
            var confirm = preview.GetNode<NConfirmButton>("Confirm");
            await WaitSecondsAsync(0.65);
            var grid = screen.GetNode<NCardGrid>("%CardGrid");
            if (count == 1 && IsPortraitHost())
            {
                var prompt = screen.GetNode<Control>("%BottomText");
                CheckEnchantLegend(prompt, screen.GetNode<Control>("%EnchantmentDescriptionContainer"));
                await CaptureAsync("enchant_1_grid_top");
                await CheckDeckSelectionScrollAsync("enchant", grid, prompt, preview, screen.GetNode<Control>("%EnchantMultiPreviewContainer"));
            }
            var holders = grid.SortedCards.Take(count).Select(c => grid.GetCardHolder(c)!).ToArray();
            foreach (var holder in holders) await _pointer.TapAsync(CenterOf(holder.Hitbox));
            await WaitUntilAsync(() => preview.Visible && confirm.IsEnabled, "native enchant preview", ShortWait);
            await WaitSecondsAsync(0.55);
            if (IsPortraitHost()) CheckScreenBacks($"enchant_{count}_preview", screen);
            var cards = FindAll<NCard>(preview).Where(c => c.IsVisibleInTree())
                .Select(c => new Rect2(c.GlobalPosition - NCard.defaultSize * c.GetGlobalTransform().Scale * 0.5f,
                    NCard.defaultSize * c.GetGlobalTransform().Scale)).ToArray();
            _report.Check($"enchant_{count}_visible_preview_cards", cards.Length == 2, $"cards={string.Join(", ", cards)}");
            if (IsPortraitHost())
                CheckEnchantPreviewFit(count, cards, confirm);
            await CaptureAsync($"enchant_{count}_preview");
            if (count == 1)
            {
                await _pointer.TapAsync(CenterOf(preview.GetNode<Control>("Cancel")));
                await WaitUntilAsync(() => !preview.Visible, "native enchant preview cancellation", ShortWait);
                _report.Check("enchant_cancel_preserves_deck", deck.All(c => c.Enchantment is null), "no preview touched the deck");
                await WaitSecondsAsync(0.5);
                await _pointer.TapAsync(CenterOf(holders[0].Hitbox));
                await WaitUntilAsync(() => preview.Visible && confirm.IsEnabled, "reopened enchant preview", ShortWait);
                await WaitSecondsAsync(0.55);
            }
            await _pointer.TapAsync(CenterOf(confirm));
            await WaitUntilAsync(() => selection.IsCompleted, "native enchant confirmation", ShortWait);
            var selected = (await selection).ToArray();
            _report.Check($"enchant_{count}_confirm_returns_selection", selected.Length == count, $"selected={selected.Length}");
            if (count == 1)
            {
                CardCmd.Enchant(model.ToMutable(), selected.Single(), amount);
                _report.Check("enchant_native_application", selected[0].Enchantment is Swift, "returned selection can be enchanted by the native command");
            }
            await WaitFramesAsync(4);
        }
    }
}
