using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

internal sealed partial class TestDriver
{
    private async Task RunTransformAsync(RunState state)
    {
        await RunManager.Instance.EnterRoomDebug(RoomType.RestSite, showTransition: false);
        var pile = state.Players[0].Deck;
        var original = pile.Cards.Where(c => c.IsTransformable).ToArray();
        bool portrait = IsPortraitHost();
        if (portrait)
            for (int i = original.Length; i < 28; i++)
                pile.AddInternal(state.CloneCard(original[i % original.Length]), silent: true);

        var deck = pile.Cards.ToArray();
        var eligible = deck.Where(c => c.IsTransformable).ToArray();
        var replacements = original.DistinctBy(c => c.Id).Take(2).Select(state.CloneCard).ToArray();
        foreach (int count in new[] { 1, 2 })
        {
            var prefs = new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt, count) { Cancelable = true };
            var screen = NDeckTransformSelectScreen.ShowScreen(eligible,
                card => count == 1 ? new CardTransformation(card, replacements) : new CardTransformation(card, replacements[1]), prefs);
            var selection = screen.CardsSelected();
            var grid = screen.GetNode<NCardGrid>("%CardGrid");
            var preview = screen.GetNode<Control>("%PreviewContainer");
            var pair = preview.GetNode<NTransformPreview>("TransformPreview");
            var confirm = preview.GetNode<NConfirmButton>("Confirm");
            await WaitSecondsAsync(0.65);
            if (count == 1 && portrait)
            {
                await CaptureAsync("transform_grid_top");
                await CheckDeckSelectionScrollAsync("transform", grid, screen.GetNode<Control>("%BottomText"), preview);
            }
            var picked = grid.SortedCards.Take(count).ToArray();
            async Task OpenPreviewAsync()
            {
                foreach (var card in picked) await _pointer.TapAsync(CenterOf(grid.GetCardHolder(card)!.Hitbox));
                await WaitUntilAsync(() => preview.Visible && confirm.IsEnabled, "native transform preview", ShortWait);
                await WaitSecondsAsync(0.55);
                if (portrait) CheckScreenBacks($"transform_{count}_preview", screen);
            }

            await OpenPreviewAsync();
            Rect2[] bounds = FindAll<NCard>(pair).Where(c => c.IsVisibleInTree()).Select(c =>
            {
                Vector2 size = NCard.defaultSize * c.GetGlobalTransform().Scale;
                return new Rect2(c.GlobalPosition - size * 0.5f, size);
            }).OrderBy(r => r.Position.X).ToArray();
            _report.Check($"transform_{count}_shows_both_sides", bounds.Length == count * 2, $"cards={bounds.Length}");
            Vector2 previewScale = pair.Scale;
            if (portrait) CheckTransformPreviewFit(count, bounds, confirm, previewScale);
            else _report.Check($"transform_{count}_landscape_native_scale", previewScale == Vector2.One, $"scale={previewScale}");
            await CaptureAsync($"transform_{count}_preview");

            if (count == 1)
            {
                var seen = new HashSet<string>();
                var after = pair.GetNode<Control>("After");
                for (int i = 0; i < 18 && seen.Count < 2; i++)
                {
                    seen.Add(FindAll<NCard>(after).First().Model!.Id.ToString());
                    await WaitSecondsAsync(0.12);
                }
                _report.Check("transform_random_preview_keeps_cycling", seen.Count == 2, $"options={string.Join(", ", seen)}");
                await _pointer.TapAsync(CenterOf(preview.GetNode<Control>("Cancel")));
                await WaitUntilAsync(() => !preview.Visible, "native transform preview cancellation", ShortWait);
                _report.Check("transform_cancel_preserves_deck", !selection.IsCompleted && deck.SequenceEqual(pile.Cards), "preview did not replace a card");
                await WaitSecondsAsync(0.5);
                await OpenPreviewAsync();
                _report.Check("transform_reopened_preview_keeps_scale", pair.Scale.IsEqualApprox(previewScale), $"scale={previewScale}->{pair.Scale}");
            }

            await _pointer.TapAsync(CenterOf(confirm));
            await WaitUntilAsync(() => selection.IsCompleted, "native transform confirmation", ShortWait);
            var selected = (await selection).ToHashSet();
            _report.Check($"transform_{count}_confirm_returns_selection", selected.SetEquals(picked), $"selected={selected.Count}");
            await WaitFramesAsync(4);
        }
    }
}
