using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

internal sealed partial class TestDriver
{
    /// <summary>
    /// Drives the game's own in-hand card selection (Armaments' upgrade choice, Burning Pact's exhaust choice and a whole-hand
    /// selection) and checks select, replace, deselect and native Confirm. The selection flow is the game's; the host only
    /// supplies the neutral pointer resting point and its own geometry assertions for the enlarged previews.
    /// </summary>
    private async Task RunHandSelectionAsync(RunState state)
    {
        var room = await EnterCombatAsync();
        NPlayerHand hand = room.Ui.Hand;
        var player = state.Players[0];
        var combat = player.Creature.CombatState!;
        var confirm = hand.GetNode<NButton>("%SelectModeConfirmButton");
        var preview = hand.GetNode<NUpgradePreview>("%UpgradePreview");
        var selected = hand.GetNode<NSelectedHandCardContainer>("%SelectedHandCardContainer");
        var prompt = hand.GetNode<RichTextLabel>("%SelectionHeader");
        var mouse = new VirtualPointer(_tree, PointerMode.Mouse);
        Vector2 neutral = HandSelectionNeutralPoint();

        NPreviewCardHolder PreviewHolder(string side) => preview.GetNode<Control>(side).GetChild<NPreviewCardHolder>(0);

        void CheckPreview(string name, Node container, params NCardHolder[] shown) =>
            CheckHandSelectionPreview(name, hand, confirm, prompt, container, shown);

        // The native selection reflows after every tap (backstop fade, selected row, preview holders, Confirm). This is the
        // same state the old 0.45-0.65 s sleeps waited for; the per-tap 0.6 s SelectionMotionProbe window is untouched.
        // Pending while the hand, a selected/preview holder or the backstop is still animating; the rest is quantized so
        // idle float drift (the same sub-pixel noise seen on creature roots) cannot keep the signature changing.
        string? SelectionSettledSignature()
        {
            if (!GodotObject.IsInstanceValid(hand) || !hand.IsInCardSelection) return null;
            string? handSample = HandSettledSignature(hand);
            if (handSample is null || IsPending(handSample)) return handSample;
            Control? backstop = hand.GetNodeOrNull<Control>("%SelectModeBackstop");
            if (backstop is { Visible: true } && backstop.SelfModulate.A < 0.999f) return Pending("sel.backstopFade");
            var builder = new StringBuilder(handSample);
            builder.Append("sel.backstop=").Append(backstop is null ? "-" : backstop.Visible.ToString()).Append('|')
                .Append("sel.mode=").Append(hand.CurrentMode).Append('|')
                .Append("sel.confirm=").Append(Q(confirm.GetGlobalRect())).Append('|')
                .Append("sel.confirmEnabled=").Append(confirm.IsEnabled).Append('|')
                .Append("sel.prompt=").Append(Q(prompt.GetGlobalRect())).Append('|')
                .Append("sel.selected=").Append(Q(selected.GetGlobalRect())).Append('|')
                .Append("sel.selectedScale=").Append(Q(selected.Scale, 3)).Append('|');
            foreach (NCardHolder holder in selected.Holders)
            {
                if (!GodotObject.IsInstanceValid(holder) || holder.IsQueuedForDeletion()) continue;
                string id = holder.GetInstanceId().ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (TweenRunning(holder, SelectedHolderTweenField)) return Pending($"sel.{id}.tween");
                if (TweenRunning(holder, HolderHoverTweenField)) return Pending($"sel.{id}.hoverTween");
                builder.Append("sel.").Append(id).Append(".rect=").Append(Q(holder.Hitbox.GetGlobalRect())).Append('|');
            }
            foreach (string side in new[] { "Before", "After" })
            {
                Control? container = preview.GetNodeOrNull<Control>(side);
                NPreviewCardHolder? holder = container is { } box && box.GetChildCount() > 0
                    ? box.GetChild(0) as NPreviewCardHolder
                    : null;
                if (holder is null || !GodotObject.IsInstanceValid(holder) || holder.IsQueuedForDeletion()) continue;
                string id = holder.GetInstanceId().ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (TweenRunning(holder, HolderHoverTweenField)) return Pending($"sel.preview.{id}.hoverTween");
                builder.Append("sel.preview.").Append(id).Append(".rect=")
                    .Append(Q(holder.Hitbox.GetGlobalRect())).Append('|');
            }
            return builder.Append("sel.preview.rect=").Append(Q(preview.GetGlobalRect())).Append('|')
                .Append("sel.preview.scale=").Append(Q(preview.Scale, 3))
                .ToString();
        }

        async Task MoveHandToDrawAsync()
        {
            var cards = PileType.Hand.GetPile(player).Cards.ToArray();
            if (cards.Length > 0) await CardPileCmd.Add(cards, PileType.Draw);
            await WaitUntilAsync(() => hand.ActiveHolders.Count == 0, "fixture hand emptied", ShortWait);
        }

        async Task<CardModel> AddAsync<T>() where T : CardModel
        {
            var card = combat.CreateCard<T>(player);
            await CardPileCmd.AddGeneratedCardToCombat(card, PileType.Hand, player);
            return card;
        }

        NHandCardHolder Holder(CardModel card) => hand.ActiveHolders.First(h => h.CardModel == card);

        async Task PlayAsync(CardModel card, int handCount)
        {
            await WaitUntilAsync(() => hand.ActiveHolders.Count == handCount, $"{handCount}-card fixture hand", ShortWait);
            await WaitUntilStableAsync(() => HandSettledSignature(hand), $"{handCount}-card fixture layout",
                TimeSpan.FromSeconds(1.2));
            var target = GetViewportSize() * new Vector2(0.5f, 0.35f);
            await _pointer.PressAsync(CenterOf(Holder(card).Hitbox));
            await _pointer.MoveAsync(target, pressed: true);
            await WaitUntilAsync(() => hand.InCardPlay, $"native {card.Id} drag started", ShortWait);
            await _pointer.ReleaseAsync(target);
        }

        await WaitUntilAsync(() => !FindAll<NCombatStartBanner>(room).Any() && !FindAll<NPlayerTurnBanner>(room).Any(),
            "initial combat banners clear", ShortWait);
        await MoveHandToDrawAsync();
        var armaments = await AddAsync<Armaments>();
        var first = await AddAsync<DefendIronclad>();
        var second = await AddAsync<StrikeIronclad>();
        await PlayAsync(armaments, 3);
        await WaitUntilAsync(() => hand.CurrentMode == NPlayerHand.Mode.UpgradeSelect, "Armaments opens native hand upgrade", ShortWait);
        await WaitUntilStableAsync(SelectionSettledSignature, "Armaments selection layout", TimeSpan.FromSeconds(1.2));
        _report.Check("hand_select_confirm_requires_choice", !confirm.IsEnabled, "native confirm is disabled before selecting");
        await _pointer.TapAsync(CenterOf(room.Ui.EndTurnButton));
        _report.Check("hand_select_blocks_end_turn", hand.IsInCardSelection && IsPlayerPlayPhase(), "end turn behind the backstop did not activate");
        await TapSelectingAsync(hand, _pointer, CenterOf(Holder(first).Hitbox), "hand_select_armaments_tap",
            () => preview.Card == first && confirm.IsEnabled);
        await WaitUntilAsync(() => preview.Card == first && confirm.IsEnabled, "stationary tap selects the first upgrade preview", ShortWait);
        await mouse.MoveAsync(neutral, pressed: false);
        await WaitUntilStableAsync(SelectionSettledSignature, "selection preview layout", TimeSpan.FromSeconds(1.2));
        CheckPreview("hand_select_armaments_preview", preview, PreviewHolder("Before"), PreviewHolder("After"));
        await CaptureAsync("hand_select_armaments_touch_preview");
        if (IsPortraitHost())
        {
            await CheckArmamentsPreviewInteractionsAsync(hand, preview, confirm, PreviewHolder("Before"), PreviewHolder("After"), first);
        }
        else
        {
            _report.Check("hand_select_armaments_landscape_native_scale", preview.Scale.IsEqualApprox(Vector2.One * 0.9f),
                $"upgrade preview scale={preview.Scale}");
        }
        await TapSelectingAsync(hand, mouse, CenterOf(Holder(second).Hitbox), "hand_select_armaments_replace",
            () => preview.Card == second && confirm.IsEnabled);
        await WaitUntilAsync(() => preview.Card == second && confirm.IsEnabled, "mouse click replaces the selected preview", ShortWait);
        await mouse.MoveAsync(neutral, pressed: false);
        await WaitUntilStableAsync(SelectionSettledSignature, "selection preview layout", TimeSpan.FromSeconds(1.2));
        CheckPreview("hand_select_armaments_replacement", preview, PreviewHolder("Before"), PreviewHolder("After"));
        await CaptureAsync("hand_select_armaments_mouse_preview");
        await _pointer.TapAsync(CenterOf(confirm));
        await WaitUntilAsync(() => second.IsUpgraded && !hand.IsInCardSelection, "native Confirm upgrades the selected card", ShortWait);
        _report.Check("hand_select_armaments_upgrades_only_chosen", second.IsUpgraded && !first.IsUpgraded && IsPlayerPlayPhase(),
            "both touch and mouse selected previews, Confirm upgraded only the final choice");

        var pact = await AddAsync<BurningPact>();
        CardModel? sacrifice = null;
        // Two cards remain after Armaments; add Pact and seven more for a full hand.
        for (int i = 0; i < 7; i++) sacrifice = await AddAsync<DefendIronclad>();
        await PlayAsync(pact, 10);
        await WaitUntilAsync(() => hand.CurrentMode == NPlayerHand.Mode.SimpleSelect, "Burning Pact opens native exhaust selection", ShortWait);
        await WaitUntilStableAsync(SelectionSettledSignature, "Burning Pact selection layout", TimeSpan.FromSeconds(1.2));
        var victim = sacrifice!;
        await TapSelectingAsync(hand, _pointer, CenterOf(Holder(victim).Hitbox), "hand_select_burning_pact_tap",
            () => confirm.IsEnabled);
        await WaitUntilAsync(() => confirm.IsEnabled, "full hand allows tapping the exhaust target", ShortWait);
        await mouse.MoveAsync(neutral, pressed: false);
        await WaitUntilStableAsync(SelectionSettledSignature, "selection preview layout", TimeSpan.FromSeconds(1.2));
        CheckPreview("hand_select_burning_pact_preview", selected, selected.Holders.Single());
        await CaptureAsync("hand_select_burning_pact");
        if (IsPortraitHost())
        {
            await CheckBurningPactPreviewInteractionsAsync(hand, selected, confirm, victim);
        }
        else
        {
            _report.Check("hand_select_burning_pact_landscape_native_scale", selected.Scale.IsEqualApprox(Vector2.One),
                $"selected row scale={selected.Scale}");
        }
        await _pointer.TapAsync(CenterOf(confirm));
        await WaitUntilAsync(() => victim.Pile?.Type == PileType.Exhaust && !hand.IsInCardSelection, "Burning Pact exhausts selected card", ShortWait);
        _report.Pass("hand_select_burning_pact_exhausts_chosen_from_full_hand");
        await WaitUntilAsync(() => pact.Pile?.Type == PileType.Discard && hand.ActiveHolders.Count == 10,
            "Burning Pact finishes drawing before reshaping the fixture hand", ShortWait);

        await MoveHandToDrawAsync();
        var lastArmaments = await AddAsync<Armaments>();
        var onlyChoice = await AddAsync<DefendIronclad>();
        await PlayAsync(lastArmaments, 2);
        await WaitUntilAsync(() => onlyChoice.IsUpgraded, "Armaments upgrades a lone eligible card without a selection", ShortWait);
        _report.Check("hand_select_one_candidate_auto_upgrade", !hand.IsInCardSelection && IsPlayerPlayPhase(), "native single-candidate shortcut returns to play");
        await WaitUntilAsync(() => lastArmaments.Pile?.Type == PileType.Discard, "second Armaments finishes", ShortWait);

        async Task<CardModel[]> FillAsync(int count)
        {
            var cards = new CardModel[count];
            for (int i = 0; i < cards.Length; i++)
                cards[i] = i % 2 == 0 ? await AddAsync<DefendIronclad>() : await AddAsync<StrikeIronclad>();
            return cards;
        }

        async Task PickAsync(CardModel card, int count, string prefix = "hand_select")
        {
            await TapSelectingAsync(hand, _pointer, CenterOf(Holder(card).Hitbox), $"{prefix}_pick_{card.Id}_{count}",
                () => selected.Holders.Count == count && selected.Holders.Any(h => h.CardModel == card));
            await WaitUntilAsync(() => selected.Holders.Count == count && selected.Holders.Any(h => h.CardModel == card),
                $"native selection contains {count} cards including {card.Id}", ShortWait);
            await WaitUntilStableAsync(SelectionSettledSignature, $"native selection of {count} cards",
                TimeSpan.FromSeconds(1.2));
        }

        await MoveHandToDrawAsync();
        var purity = await AddAsync<Purity>();
        CardCmd.Upgrade(purity);
        var candidates = await FillAsync(9);
        await PlayAsync(purity, 10);
        await WaitUntilAsync(() => hand.CurrentMode == NPlayerHand.Mode.SimpleSelect, "Purity+ opens the native multi-select", ShortWait);
        await WaitUntilStableAsync(SelectionSettledSignature, "Purity+ selection layout", TimeSpan.FromSeconds(1.2));
        for (int i = 0; i < 5; i++) await PickAsync(candidates[i], i + 1);
        // Native Purity+ replaces the fifth choice if a sixth card is tapped.
        await PickAsync(candidates[5], 5);
        var chosen = candidates.Take(4).Append(candidates[5]).ToArray();
        _report.Check("hand_select_purity_plus_caps_at_five", chosen.All(c => selected.Holders.Any(h => h.CardModel == c)),
            "the sixth tap replaced the last selection without discarding the first four");
        await mouse.MoveAsync(neutral, pressed: false);
        await WaitUntilStableAsync(SelectionSettledSignature, "selection preview layout", TimeSpan.FromSeconds(1.2));
        CheckPreview("hand_select_purity_plus_preview", selected, selected.Holders.ToArray());
        await CaptureAsync("hand_select_purity_plus_five");
        await _pointer.TapAsync(CenterOf(confirm));
        await WaitUntilAsync(() => purity.Pile?.Type == PileType.Exhaust && chosen.All(c => c.Pile?.Type == PileType.Exhaust)
            && !hand.IsInCardSelection && selected.Holders.Count == 0, "Purity+ finishes exhausting the chosen five", ShortWait);
        _report.Check("hand_select_purity_plus_exhausts_only_chosen", candidates.Except(chosen).All(c => c.Pile?.Type == PileType.Hand),
            "the other four remain in hand");

        await MoveHandToDrawAsync();
        var wholeHand = await FillAsync(10);
        await WaitUntilAsync(() => hand.ActiveHolders.Count == wholeHand.Length, "full fixture hand", ShortWait);
        await WaitUntilStableAsync(() => HandSettledSignature(hand), "full fixture hand layout",
            TimeSpan.FromSeconds(1.2));
        // Purity is capped at five. Ask the same native UI for exactly ten in
        // an isolated fixture, without adding a card or effect to the game.
        var all = hand.SelectCards(new CardSelectorPrefs(CardSelectorPrefs.ExhaustSelectionPrompt, 10, 10), null, null);
        await WaitUntilStableAsync(SelectionSettledSignature, "whole-hand selection layout",
            TimeSpan.FromSeconds(1.2));
        for (int i = 0; i < wholeHand.Length; i++) await PickAsync(wholeHand[i], i + 1);
        _report.Check("hand_select_whole_hand_ready", hand.ActiveHolders.Count == 0 && confirm.IsEnabled,
            "all ten cards selected and native Confirm enabled");
        await mouse.MoveAsync(neutral, pressed: false);
        await WaitUntilStableAsync(SelectionSettledSignature, "selection preview layout", TimeSpan.FromSeconds(1.2));
        CheckPreview("hand_select_whole_hand_preview", selected, selected.Holders.ToArray());
        if (IsPortraitHost())
        {
            await CheckWholeHandPreviewInteractionsAsync(hand, selected, confirm, wholeHand[^1]);
        }
        await _pointer.TapAsync(CenterOf(confirm));
        await WaitUntilAsync(() => all.IsCompleted && !hand.IsInCardSelection, "full hand Confirm returns the native selection", ShortWait);
        _report.Check("hand_select_whole_hand_confirms_exactly_ten", wholeHand.ToHashSet().SetEquals(await all),
            "native result contains all ten selected cards");

        // One representative pass in the launcher's Flat Hand mode: Armaments' upgrade choice and the whole-hand ten.
        if (FlatHandSupported())
        {
            try
            {
                await WaitUntilAsync(() => !hand.IsInCardSelection && selected.Holders.Count == 0 && IsPlayerPlayPhase(),
                    "selection state clears before the flat pass", ShortWait);
                await PlayerCmd.SetEnergy(3m, player);
                await WaitUntilAsync(() => player.PlayerCombatState!.Energy >= 3, "flat pass energy restored", ShortWait);
                SetFlatHandMode(true);
                await WaitUntilStableAsync(SelectionSettledSignature, "flat hand selection layout", TimeSpan.FromSeconds(1.2));

                await MoveHandToDrawAsync();
                var flatArmaments = await AddAsync<Armaments>();
                var flatFirst = await AddAsync<DefendIronclad>();
                await AddAsync<StrikeIronclad>();
                _report.Check("hand_select_flat_armaments_in_hand",
                    hand.ActiveHolders.Any(h => h.CardModel == flatArmaments),
                    $"holders={string.Join(",", hand.ActiveHolders.Select(h => h.CardModel?.Id.ToString() ?? "?"))}");
                string DumpFlatArmamentsState()
                {
                    string holders = string.Join(",", hand.ActiveHolders
                        .Select(h => h.CardModel?.Id.ToString() ?? "?"));
                    return $"flat Armaments timeout: energy={player.PlayerCombatState?.Energy}/{player.PlayerCombatState?.MaxEnergy} " +
                           $"playPhase={IsPlayerPlayPhase()} inSelection={hand.IsInCardSelection} mode={hand.CurrentMode} " +
                           $"inCardPlay={hand.InCardPlay} holders=[{holders}] selected={selected.Holders.Count} " +
                           $"preview={preview.Card?.Id.ToString() ?? "none"} confirm={confirm.IsEnabled} " +
                           $"armamentsInHand={hand.ActiveHolders.Any(h => h.CardModel == flatArmaments)} " +
                           $"armamentsPile={flatArmaments.Pile?.Type.ToString() ?? "none"}";
                }
                await PlayAsync(flatArmaments, 3);
                try
                {
                    await WaitUntilAsync(() => hand.CurrentMode == NPlayerHand.Mode.UpgradeSelect,
                        "flat Armaments opens native hand upgrade", ShortWait);
                }
                catch (TimeoutException)
                {
                    string dump = DumpFlatArmamentsState();
                    GD.Print("[TEST] " + dump);
                    _report.Check("hand_select_flat_armaments_timeout", false, dump);
                    await CaptureAsync("hand_selection_flat_armaments_timeout");
                    throw;
                }
                await WaitUntilStableAsync(SelectionSettledSignature, "flat Armaments layout", TimeSpan.FromSeconds(1.2));
                await TapSelectingAsync(hand, _pointer, CenterOf(Holder(flatFirst).Hitbox), "hand_select_flat_armaments_tap",
                    () => preview.Card == flatFirst && confirm.IsEnabled);
                await WaitUntilAsync(() => preview.Card == flatFirst && confirm.IsEnabled,
                    "flat Armaments preview selected", ShortWait);
                await mouse.MoveAsync(neutral, pressed: false);
                await WaitUntilStableAsync(SelectionSettledSignature, "flat upgrade preview layout", TimeSpan.FromSeconds(1.2));
                CheckPreview("hand_select_flat_armaments_preview", preview, PreviewHolder("Before"), PreviewHolder("After"));
                await CaptureAsync("hand_selection_flat_armaments");
                await _pointer.TapAsync(CenterOf(confirm));
                await WaitUntilAsync(() => !hand.IsInCardSelection, "flat Armaments confirms", ShortWait);

                await MoveHandToDrawAsync();
                var flatHand = await FillAsync(10);
                await WaitUntilAsync(() => hand.ActiveHolders.Count == flatHand.Length, "flat full fixture hand", ShortWait);
                await WaitUntilStableAsync(() => HandSettledSignature(hand), "flat full hand layout", TimeSpan.FromSeconds(1.2));
                var flatAll = hand.SelectCards(new CardSelectorPrefs(CardSelectorPrefs.ExhaustSelectionPrompt, 10, 10), null, null);
                await WaitUntilStableAsync(SelectionSettledSignature, "flat whole-hand selection layout", TimeSpan.FromSeconds(1.2));
                for (int i = 0; i < flatHand.Length; i++) await PickAsync(flatHand[i], i + 1, "hand_select_flat");
                _report.Check("hand_select_flat_whole_hand_ready", hand.ActiveHolders.Count == 0 && confirm.IsEnabled,
                    "all ten cards selected in flat mode and native Confirm enabled");
                await mouse.MoveAsync(neutral, pressed: false);
                await WaitUntilStableAsync(SelectionSettledSignature, "flat selection preview layout", TimeSpan.FromSeconds(1.2));
                CheckPreview("hand_select_flat_whole_hand_preview", selected, selected.Holders.ToArray());
                await CaptureAsync("hand_selection_flat_whole_hand_ten");
                await _pointer.TapAsync(CenterOf(confirm));
                await WaitUntilAsync(() => flatAll.IsCompleted && !hand.IsInCardSelection,
                    "flat whole hand Confirm returns the native selection", ShortWait);
                _report.Check("hand_select_flat_whole_hand_confirms_exactly_ten",
                    flatHand.ToHashSet().SetEquals(await flatAll), "native result contains all ten flat-mode selected cards");
            }
            finally
            {
                SetFlatHandMode(false);
            }
        }
        ResetFlatHandMode();
    }
}
