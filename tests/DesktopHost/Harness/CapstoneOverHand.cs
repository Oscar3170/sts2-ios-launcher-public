using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The regression for the reported device bug: a screen opened over the combat hand (the deck view, the draw pile or the pause
/// menu) must take every gesture that lands over the hand's former positions. Each gesture — a vertical swipe, a tap, a
/// 0.6 second hold, an upward drag onto an enemy — must leave the hand, the energy and the card-play/targeting state exactly
/// as they were. Afterwards the screens are closed and one card is played by drag, proving the hand works again.
/// <para>
/// The screens are native: <see cref="NDeckViewScreen.ShowScreen"/> and the draw pile and pause menu buttons the game draws,
/// all opened in the run's capstone container (or its submenu stack), which is what a host's hand input gate must respect.
/// Step names: <c>capstone_&lt;screen&gt;_&lt;gesture&gt;_hand_untouched</c>.
/// </para>
/// </summary>
internal sealed partial class TestDriver
{
    private async Task RunCapstoneOverHandAsync(RunState state)
    {
        Player player = state.Players[0];
        NCombatRoom room = await EnterCombatAsync();
        NPlayerHand hand = room.Ui.Hand;
        await WaitSecondsAsync(1.0);
        if ((player.PlayerCombatState?.Energy ?? 0) < 1) await PlayerCmd.SetEnergy(1m, player);
        await WaitUntilAsync(() => hand.ActiveHolders.Count >= 4, "full hand in combat", MediumWait);
        _report.Pass("capstone_fixture_ready",
            $"hand={hand.ActiveHolders.Count} energy={player.PlayerCombatState?.Energy}/{player.PlayerCombatState?.MaxEnergy}");

        // Guarded: a gesture that the game acts on can free the hand (a played card, a menu that tears the run down), and the
        // check must then report the change instead of throwing on a freed node.
        (int Count, int Energy, bool InPlay, string Focus) Snapshot()
        {
            if (!GodotObject.IsInstanceValid(hand)) return (-1, -1, true, "hand freed");
            var holders = hand.ActiveHolders.Where(GodotObject.IsInstanceValid).ToArray();
            return (holders.Length, player.PlayerCombatState?.Energy ?? -1, hand.InCardPlay,
                hand.FocusedHolder is { } focused && GodotObject.IsInstanceValid(focused) ? focused.Name.ToString() : "none");
        }

        void CheckUntouched(string screen, string gesture, (int Count, int Energy, bool InPlay, string Focus) before)
        {
            var now = Snapshot();
            _report.Check($"capstone_{screen}_{gesture}_hand_untouched",
                now.Count == before.Count && now.Energy == before.Energy && !before.InPlay && !now.InPlay
                    && now.Focus == "none",
                $"gesture={gesture} hand={now.Count}/{before.Count} energy={now.Energy}/{before.Energy} " +
                $"inPlay={before.InPlay}->{now.InPlay} focus={now.Focus}");
        }

        Vector2[] handPoints = hand.ActiveHolders.Select(CenterOf).ToArray();
        Vector2 anchor = handPoints[handPoints.Length / 2];
        NCreature enemy = room.CreatureNodes.FirstOrDefault(c => c.Entity.IsMonster && c.Entity.IsAlive)
            ?? throw new InvalidOperationException("no living enemy to drag a card onto");

        // A gesture that lands on a deck card opens the native card inspection; that is the game's own behaviour, not a
        // hand leak, so it is closed again before the next gesture and only logged.
        async Task CloseNativeInspectionAsync(string screen)
        {
            if (NGame.Instance?.InspectCardScreen is not { } inspect || !GodotObject.IsInstanceValid(inspect)
                || !inspect.IsVisibleInTree()) return;
            inspect.Close();
            Log.Info($"[TEST] capstone {screen}: the gesture opened the native card inspection; closed it natively");
            // Its backstop fades out; let it finish before the next gesture so it cannot swallow one.
            await WaitSecondsAsync(0.35);
        }

        async Task GesturesOverHandAsync(string screen, Vector2[] points)
        {
            if (points.Length == 0)
            {
                _report.Skip($"capstone_{screen}_gestures", "no hand-card position clear of the screen's own controls");
                return;
            }
            Log.Info($"[TEST] capstone {screen}: gesture points={string.Join(", ", points.Select(p => p.ToString()))}");

            var swipe = Snapshot();
            // A scroll: down the list, starting on a hand card's former position.
            await _pointer.DragAsync(points[0] + new Vector2(0f, -140f), points[0] + new Vector2(0f, 140f), steps: 12);
            await WaitSecondsAsync(0.4);
            CheckUntouched(screen, "swipe", swipe);
            await CloseNativeInspectionAsync(screen);

            if (points.Length < 2) _report.Skip($"capstone_{screen}_tap_hand_untouched", "no second clear hand position");
            else
            {
                var tap = Snapshot();
                await _pointer.TapAsync(points[1]);
                await WaitSecondsAsync(0.4);
                CheckUntouched(screen, "tap", tap);
                await CloseNativeInspectionAsync(screen);
            }

            if (points.Length < 2) _report.Skip($"capstone_{screen}_hold_hand_untouched", "no second clear hand position");
            else
            {
                var hold = Snapshot();
                await _pointer.LongPressAsync(points[Math.Min(1, points.Length - 1)], frames: 36); // ~0.6 s
                await WaitSecondsAsync(0.4);
                CheckUntouched(screen, "hold", hold);
                await CloseNativeInspectionAsync(screen);
            }

            if (points.Length < 3) _report.Skip($"capstone_{screen}_drag_to_enemy_hand_untouched", "no third clear hand position");
            else
            {
                var drag = Snapshot();
                await _pointer.DragAsync(points[2], CenterOf(enemy), steps: 14);
                await WaitSecondsAsync(0.6);
                CheckUntouched(screen, "drag_to_enemy", drag);
                await CloseNativeInspectionAsync(screen);
            }
        }

        // The draw pile tapped on its own, in a fresh state: if this fails too, the pile button is not tappable at all; if it
        // passes here and fails only after the deck view, the deck's close path is what leaves something over the button.
        bool PileOpen() => FindAll<NCardPileScreen>(_tree.Root).Any(s => s.IsVisibleInTree() && s.Pile.Type == PileType.Draw);
        async Task<bool> TapDrawPileAsync(string suffix)
        {
            Control pile = room.Ui.DrawPile;
            Vector2 point = CenterOf(pile);
            // Diagnostic first: the hovered control, the pile's own rect/filter/pads, at the point about to be tapped.
            await LogTapDiagnosticsAsync("draw_pile_" + suffix, pile);
            await _pointer.TapAsync(point);
            bool opened = await WaitOrFalseAsync(PileOpen, TimeSpan.FromSeconds(3));
            if (!opened)
            {
                await LogTapDiagnosticsAsync("draw_pile_" + suffix + "_timeout", pile);
                await _pointer.TapAsync(point);
                opened = await WaitOrFalseAsync(PileOpen, TimeSpan.FromSeconds(8));
            }
            _report.Check($"capstone_draw_pile_opens_by_tap_{suffix}", opened,
                $"point={point} pile={pile.GetGlobalRect()} target={HostTouchTargetBox(pile)} filter={pile.MouseFilter}");
            return opened;
        }

        bool freshOpened = await TapDrawPileAsync("fresh");
        if (freshOpened)
        {
            await WaitSecondsAsync(0.5);
            CloseCapstoneScreen();
            await WaitUntilAsync(() => !PileOpen() && (NCapstoneContainer.Instance is null || !NCapstoneContainer.Instance.InUse),
                "fresh draw pile closes", ShortWait);
            await WaitSecondsAsync(0.6);
        }

        // 1. The deck view, scrolled with the finger over the hand's former positions.
        NDeckViewScreen? deck = NDeckViewScreen.ShowScreen(player);
        await WaitUntilAsync(() => deck is not null && GodotObject.IsInstanceValid(deck) && deck.IsVisibleInTree(),
            "native deck view over the hand", MediumWait);
        await WaitSecondsAsync(0.7);
        await GesturesOverHandAsync("deck", handPoints);
        await CaptureAsync("capstone_deck_scrolled");
        CloseCapstoneScreen();
        // Settle the close: the capstone must be out of use *and* the deck screen hidden/freed, not just "closing".
        await WaitUntilAsync(() => (NCapstoneContainer.Instance is null || !NCapstoneContainer.Instance.InUse)
            && (deck is null || !GodotObject.IsInstanceValid(deck) || !deck.IsVisibleInTree()),
            "deck view closed and settled", ShortWait);
        await WaitSecondsAsync(0.6);

        // 2. The draw pile again, after the deck view closed: distinguishes a close-timing problem from a general tap failure.
        if (await TapDrawPileAsync("after_deck"))
        {
            await WaitSecondsAsync(0.5);
            await GesturesOverHandAsync("draw_pile", handPoints);
            await CaptureAsync("capstone_draw_pile_gestures");
            CloseCapstoneScreen();
            await WaitUntilAsync(() => !PileOpen(), "draw pile closes", ShortWait);
            await WaitSecondsAsync(0.5);
        }

        // 3. The in-run pause menu.
        await _pointer.TapAsync(CenterOf(NRun.Instance!.GlobalUi.TopBar.Pause));
        await WaitUntilAsync(() => RunManager.Instance.IsPaused && VisibleScreen(nameof(NPauseMenu)) is not null,
            "pause menu over the hand", ShortWait);
        await WaitSecondsAsync(0.6);
        // Hand-card positions that do not fall inside the pause menu's own buttons (Save & Quit / Give Up must never be hit by
        // a gesture meant for the hand under it). The buttons and the chosen points are logged; gestures with no clear point
        // are skipped with an explicit step rather than retargeted.
        Control pauseScreenForPoints = VisibleScreen(nameof(NPauseMenu))!;
        var pauseButtons = FindAll<NPauseMenuButton>(pauseScreenForPoints)
            .Where(button => button.IsVisibleInTree())
            .Select(button => (Name: button.Name.ToString(), Rect: button.GetGlobalRect()))
            .ToArray();
        Vector2[] pausePoints = handPoints
            .Where(point => pauseButtons.All(button => !button.Rect.Grow(3f).HasPoint(point)))
            .ToArray();
        Log.Info($"[TEST] capstone pause: buttons=[{string.Join(", ", pauseButtons.Select(b => $"{b.Name} {b.Rect}"))}] " +
                 $"clearPoints={pausePoints.Length}/{handPoints.Length}");
        await GesturesOverHandAsync("pause", pausePoints);
        await CaptureAsync("capstone_pause_gestures");
        var pause = (NPauseMenu)VisibleScreen(nameof(NPauseMenu))!;
        await _pointer.TapAsync(CenterOf(pause.GetNode<Control>("%BackButton")));
        await WaitUntilAsync(() => !RunManager.Instance.IsPaused && VisibleScreen(nameof(NPauseMenu)) is null,
            "pause menu closes", ShortWait);
        await WaitSecondsAsync(0.5);

        // 4. The hand works again: play one card by dragging it out of the hand. A card that needs no enemy target goes into the
        // play zone above the hand; a card that needs a target is released on a living enemy's hitbox. (Dragging an Attack
        // straight up releases it off every target, and the game correctly puts it back.)
        int countBefore = hand.ActiveHolders.Count;
        int energyBefore = player.PlayerCombatState?.Energy ?? -1;
        var playable = hand.ActiveHolders.Where(h => h.IsVisibleInTree() && h.CardModel is not null).ToArray();
        NHandCardHolder? untargeted = playable.FirstOrDefault(h => h.CardModel!.TargetType
            is TargetType.None or TargetType.Self or TargetType.AllEnemies or TargetType.RandomEnemy);
        var attempts = new List<(NHandCardHolder Holder, Vector2 Release, string Why)>();
        if (untargeted is not null)
        {
            Vector2 from = CenterOf(untargeted);
            attempts.Add((untargeted, from + new Vector2(0f, -420f), "untargeted card into the play zone"));
        }
        foreach (NHandCardHolder targeted in playable.Where(h => h.CardModel!.TargetType == TargetType.AnyEnemy).Take(2))
            attempts.Add((targeted, CenterOf(enemy.Hitbox), "targeted card onto the enemy hitbox"));
        bool played = false;
        string playedDetail = "no playable card in hand";
        foreach ((NHandCardHolder holder, Vector2 release, string why) in attempts)
        {
            if (!GodotObject.IsInstanceValid(holder) || !hand.ActiveHolders.Contains(holder)) continue;
            Vector2 from = CenterOf(holder);
            playedDetail = $"card={holder.CardModel?.Id} TargetType={holder.CardModel?.TargetType} release={release} via={why}";
            Log.Info($"[TEST] capstone play-after-screens: {playedDetail} from={from}");
            await _pointer.DragAsync(from, release, steps: 14);
            played = await WaitOrFalseAsync(() => hand.ActiveHolders.Count < countBefore
                || (player.PlayerCombatState?.Energy ?? -1) < energyBefore, TimeSpan.FromSeconds(5));
            if (played) break;
            await _pointer.TapAsync(new Vector2(20f, GetViewportSize().Y * 0.3f));
            await WaitSecondsAsync(0.5);
        }
        _report.Check("capstone_hand_plays_after_screens", played && NRun.Instance?.CombatRoom == room,
            $"{playedDetail} hand={hand.ActiveHolders.Count}/{countBefore} " +
            $"energy={player.PlayerCombatState?.Energy}/{energyBefore} played={played} sameRoom={NRun.Instance?.CombatRoom == room}");
    }

    private static void CloseCapstoneScreen()
    {
        if (NCapstoneContainer.Instance is { InUse: true } container) container.Close();
    }
}
