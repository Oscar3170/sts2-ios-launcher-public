using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.DesktopHost.Harness;

internal sealed partial class TestDriver
{
    private async Task RunPotionRewardAsync(RunState state)
    {
        var player = state.Players[0];
        SaveManager.Instance.MarkFtueAsComplete("obtain_potion_ftue");
        var room = await EnterCombatAsync();
        var belt = NRun.Instance!.GlobalUi.TopBar.PotionContainer.Holders;
        for (int pass = 0; pass < 2; pass++)
        {
            var attack = ModelDb.Potion<AttackPotion>().ToMutable();
            player.AddPotionInternal(attack, 0);
            await WaitSecondsAsync(0.65);
            var popup = await TapPotionAsync(belt[0], $"potion_reward_{pass}_use");
            await _pointer.TapAsync(CenterOf(Action(popup, "Use")));
            await WaitUntilAsync(() => FindAll<NChooseACardSelectionScreen>(_tree.Root).Any(s => s.IsVisibleInTree()),
                "Attack Potion native card picker", ShortWait);
            await WaitSecondsAsync(0.65);
            var screen = FindAll<NChooseACardSelectionScreen>(_tree.Root).Single(s => s.IsVisibleInTree());
            var holders = FindAll<NGridCardHolder>(screen);
            _report.Check("potion_reward_offers_three_cards_" + pass, holders.Count == 3, $"holders={holders.Count}");
            CheckCombatChoiceControls($"potion_reward_{pass}", screen, holders);
            var peek = screen.GetNode<NPeekButton>("%PeekButton");
            var skip = screen.GetNode<NChoiceSelectionSkipButton>("SkipButton");
            await CaptureAsync($"potion_reward_{pass}_picker");
            if (pass == 0)
            {
                _report.Check("potion_reward_native_peek_available", peek.IsEnabled, "combat picker has native Peek");
                if (peek.IsEnabled)
                {
                    await _pointer.TapAsync(CenterOf(peek));
                    await WaitUntilAsync(() => peek.IsPeeking && !screen.GetNode<Control>("CardRow").Visible, "peek hides potion cards", ShortWait);
                    await _pointer.TapAsync(CenterOf(peek));
                    await WaitUntilAsync(() => !peek.IsPeeking && skip.IsEnabled, "peek shows potion cards again", ShortWait);
                }
            }
            var hand = room.Ui.Hand;
            int before = hand.ActiveHolders.Count;
            if (pass == 0) await _pointer.TapAsync(CenterOf(skip));
            else await TakeChoiceCardAsync(holders[0]);
            await WaitUntilAsync(() => !GodotObject.IsInstanceValid(screen) && IsPlayerPlayPhase(), "returns to combat", ShortWait);
            int expected = before + pass;
            await WaitUntilAsync(() => hand.ActiveHolders.Count == expected && !player.Potions.Contains(attack), "native potion resolves", ShortWait);
            _report.Check(pass == 0 ? "potion_reward_skip_does_not_add_card" : "potion_reward_selection_adds_card",
                hand.ActiveHolders.Count == expected, $"before={before} after={hand.ActiveHolders.Count}");
            await WaitSecondsAsync(0.65);
        }
    }

}
