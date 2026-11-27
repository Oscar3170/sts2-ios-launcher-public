using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;
using GameLog = MegaCrit.Sts2.Core.Logging.Log;
using LogLevel = MegaCrit.Sts2.Core.Logging.LogLevel;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>Monotonic wall clock shared by the perf probe and the combat log watcher.</summary>
internal static class PerfClock
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    internal static long NowMs => Clock.ElapsedMilliseconds;
}

/// <summary>
/// <c>perf-combat</c>: what one fixed fight costs per frame, on the launcher host only. The scenario plays the
/// NibbitsNormal-in-Overgrowth fight that <c>touch-combat</c> uses (same act, same encounter, same portrait window) with
/// real injected finger input: the opening draw, two more turn draws, four card drags onto enemies and above the play
/// zone, an aim cancel, and two enemy turns. Cards are only ever played through the launcher's touch path, so the measured
/// frames are the ones a player causes.
/// <para>
/// The probe samples every drawn frame: the wall delta between drawn frames as the frame time (the figure the device
/// review read as 60 -> 30 fps), the nine Godot monitors the device review called out (TIME_PROCESS among them), the CPU
/// frame setup time and the root viewport's measured CPU/GPU render times (<see cref="RenderingServer"/>), Engine.MaxFps,
/// every bucket of <see cref="PortraitFrameTiming"/> (14 passes), and the main thread's GC collection counts and
/// allocated bytes. It folds them into one step per second
/// (<c>perf_second_N_summary</c>), one per draw window (<c>perf_draw_turnN_summary</c>, from the first frame a new hand
/// holder appears until the hand settles), and a whole-fight <c>perf_combat_summary</c>. Steps are prefixed <c>perf_</c>.
/// </para>
/// <para>
/// Gates: <c>perf_no_uncached_assets_in_combat</c> (no "Asset not cached" warning after the fight's first second; this is
/// the regression check for combat asset preloading), <c>perf_our_passes_budget</c> (menu of the top-level portrait
/// passes per draw-window frame ≤ 1.0 ms and worst non-GC frame ≤ 8 ms on Linux; frames with a collection inside a pass
/// are excluded from the worst and reported), and
/// <c>perf_reduce_effects_inflight_nodes</c> (with <see cref="PortraitHandLayout.ReduceEffects"/> on, only cards
/// <b>arriving</b> from a draw lose their highlight quad and shadow; a card that was already in the hand and only
/// re-spaces keeps them; every hidden card comes back on one and the same frame; the cross-turn draw-call aggregate is
/// reported only because different turns differ by more than the ~10 calls the setting removes) and
/// <c>perf_reduce_effects_respace_nodes</c> (the third play draws nothing, so the played card leaving and the rest
/// re-spacing must keep both nodes visible), and
/// <c>perf_no_idle_cap_during_animation</c> (Engine.MaxFps must
/// not be the launcher's idle 30 during a turn-start draw or an enemy turn; the probe drives the same
/// <see cref="FrameRateGovernor"/> loop PhoneGraphics runs, with Lower Idle Frame Rate on, and a companion
/// <c>perf_idle_cap_engages_when_idle</c> proves the fixture can cap at all). <c>perf_draw_allocations</c> is reported
/// but not gated: the figure is the whole main thread's allocation (the game's own work dominates) and has no stable
/// cross-run baseline yet.
/// </para>
/// <para>
/// <see cref="PortraitHandLayout.ReduceEffects"/> only hides the effects of cards arriving from a draw and restores the
/// whole hand together on one frame; a card that only re-spaces keeps its effects, and a resting hand looks the same as
/// with the setting off. The draw-call gate compares the two real draw windows, and a resting-hand control reports that
/// the settled hand is unchanged.
/// </para>
/// <para>
/// A second, test-only pass channels Frost, Lightning and Dark orbs onto this Ironclad fight
/// (<see cref="OrbCmd.Channel{T}"/> adds the first slot itself) to exercise the orbs bucket and the orb VFX assets. It is
/// not a Defect run: a real Defect variant would need a second run start, which the two-minute budget does not allow, and
/// its asset loads are reported separately instead of gated so a fixture-only load cannot fail the preload gate.
/// </para>
/// </summary>
internal sealed partial class TestDriver
{
    /// <summary>Called by <c>LauncherHostRunner</c> for <c>--touchspire-test=perf-combat</c>.</summary>
    internal static async Task RunLauncherPerfCombatAsync(TestOptions options, TestReport report, ScreenshotService screenshots,
        VirtualPointer pointer)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Log.Error("Launcher perf-combat requires a SceneTree.");
            return;
        }
        await new TestDriver(tree, options, report, screenshots, pointer).RunLauncherPerfCombatInternalAsync();
    }

    private async Task RunLauncherPerfCombatInternalAsync()
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            _report.Fail("perf-combat_portrait", $"perf-combat needs a portrait window; window={DisplayServer.WindowGetSize()} " +
                                                 $"canvas={GetViewportSize()}");
            return;
        }

        // Test-only fixture: the ReduceEffects setting is a launcher static, and this host never runs PhoneGraphics (the
        // battery row that assigns it). Start from off so the baseline draws are measured without it, and leave it off.
        PortraitHandLayout.ReduceEffects = false;
        _report.Pass("perf_reduce_effects_off_start", "PortraitHandLayout.ReduceEffects=false for the baseline turns (test-only fixture)");

        var assets = new CombatAssetWatcher();
        assets.Attach();
        PerfProbe? probe = null;
        long combatStartedMs = 0;
        int missedBefore = 0;
        try
        {
            await WaitForMainMenuPreloadAsync();
            RunState state = await StartIroncladRunAsync();
            Player player = LocalContext.GetMe(state) ?? throw new InvalidOperationException("No local player in the run.");
            // Keep the turn loop alive for the duration of the measurement; the fight itself is unchanged.
            player.Creature.SetMaxHpInternal(9999);
            player.Creature.SetCurrentHpInternal(9999);

            probe = new PerfProbe(_tree, _report);
            _report.Pass("perf_idle_governor_fixture",
                "test-only PhoneFrameCap path driven by the probe (SetInputs + Apply, as PhoneGraphics does): Lower Idle " +
                "Frame Rate=true, game limit forced to 0, touching=false, secondsSinceTouch=100, thermal=unknown; " +
                "Engine.MaxFps is restored when the probe stops");
            // The opening hand is a real turn draw. Arm before EnterRoomDebug so its first arrival opens the window.
            probe.ArmDrawWindow("draw_turn1");
            probe.Start();
            _report.Pass("perf_render_measure", probe.RenderMeasureEnabled
                ? "root viewport measured render times enabled (render_cpu_ms/render_gpu_ms are reported, not gated)"
                : "root viewport render measurement unavailable; render_cpu_ms/render_gpu_ms report 0");
            combatStartedMs = PerfClock.NowMs;
            missedBefore = PreloadManager.Cache.MissedCacheAssetCount;
            NCombatRoom room = await EnterTouchCombatAsync(state, "perf-combat");
            _report.Phase("perf_combat", $"act={state.Act.GetType().Name} encounter=NibbitsNormal " +
                                         $"enemies={LivingEnemies(room).Count}");

            // The intro settle inside EnterTouchCombatAsync includes the hand's arrival; close the opening draw window.
            await WaitUntilStableAsync(CurrentHandSettledSignature, "perf-combat opening hand", TimeSpan.FromSeconds(3));
            probe.EndDrawWindow("draw_turn1");

            // A few plays cannot end a scripted fight. Both enemies get a large HP pool; the damage/HP text still runs.
            foreach (NCreature enemy in LivingEnemies(room)) await RaiseHpAsync(enemy, 500);

            // ---- turn 1: two plays and an aim cancel ----
            await SetEnergyAsync(player, 9m);
            await PerfPlayCardAsync(room, player, "perf_turn1_play1", probe);
            await SetEnergyAsync(player, 9m);
            await PerfPlayCardAsync(room, player, "perf_turn1_play2", probe);
            await PerfCancelAimAsync(room, player, "perf_turn1_cancel", probe);

            // ---- idle control: the cap must actually engage when nothing animates, or the animation gate is vacuous.
            // A settled hand on the player's own turn (actions enabled) is the state where CombatAnimating is false by
            // design; an enemy turn or a moving card would suppress the idle cap, so the wait is explicit. ----
            await WaitUntilStableAsync(CurrentHandSettledSignature, "perf-combat idle control hand", TimeSpan.FromSeconds(2));
            bool actionsBack = await TryWaitAsync(() => !CombatManager.Instance.PlayerActionsDisabled,
                "player actions enabled before the idle control", ShortWait);
            if (!actionsBack)
            {
                _report.Fail("perf_idle_cap_engages_when_idle",
                    "the game still had player actions disabled after the cancel; the idle control cannot be measured");
            }
            else
            {
                await WaitFramesAsync(20);
                probe.BeginSegment("idle_control");
                await WaitFramesAsync(24);
                var idleControl = probe.EndSegment("idle_control");
                if (idleControl is not null)
                    _report.Check("perf_idle_cap_engages_when_idle",
                        idleControl.IdleCapFrames > 0 && idleControl.AnimatingFrames == 0
                        && idleControl.MonitorMax(9) <= FrameRateGovernor.IdleFrameRate,
                        $"Engine.MaxFps while resting (settled hand, actions enabled): min/mean/max=" +
                        $"{Num(idleControl.MonitorMin(9), 0)}/{Num(idleControl.MonitorMean(9), 1)}/{Num(idleControl.MonitorMax(9), 0)}, " +
                        $"frames at the idle cap={idleControl.IdleCapFrames}/{idleControl.Frames}, " +
                        $"animating frames={idleControl.AnimatingFrames} (must be 0: the control only proves the cap " +
                        "engages when nothing is animating)");
            }

            // ---- turn 2 draw with ReduceEffects off (the mechanism watch proves the nodes stay visible) ----
            PortraitHandLayout.ReduceEffects = false;
            probe.BeginReduceWatch(false);
            probe.ArmDrawWindow("draw_turn2");
            if (!await PerfEndTurnAsync(room, "turn1", probe))
            {
                probe.EndReduceWatch("off", checkRest: false);
                return;
            }
            await WaitUntilStableAsync(CurrentHandSettledSignature, "perf-combat turn 2 hand", TimeSpan.FromSeconds(3));
            var turn2Off = probe.EndDrawWindow("draw_turn2");
            var offNodes = probe.EndReduceWatch("off", checkRest: false);

            await SetEnergyAsync(player, 9m);
            await PerfPlayCardAsync(room, player, "perf_turn2_play1", probe);

            // ---- turn 3 draw with ReduceEffects on ----
            PortraitHandLayout.ReduceEffects = true;
            _report.Pass("perf_reduce_effects_on", "PortraitHandLayout.ReduceEffects=true for the third draw window");
            probe.BeginReduceWatch(true);
            probe.ArmDrawWindow("draw_turn3");
            if (!await PerfEndTurnAsync(room, "turn2", probe))
            {
                probe.EndReduceWatch("on", checkRest: false);
                return;
            }
            await WaitUntilStableAsync(CurrentHandSettledSignature, "perf-combat turn 3 hand", TimeSpan.FromSeconds(3));
            var turn3On = probe.EndDrawWindow("draw_turn3");
            // The launcher closes its hand-effects window on its own settle rule and restores every hidden card on one
            // frame. Poll for that frame rather than sleeping a fixed number of frames, and record when each card came
            // back so the whole-hand restore can be checked.
            await probe.WaitForRestoreAsync(TimeSpan.FromSeconds(1.5));
            var onNodes = probe.EndReduceWatch("on", checkRest: true);
            if (onNodes is not null)
                _report.Pass("perf_reduce_effects_rest_timing",
                    $"rest check at draw+{onNodes.DrawStartDelta}f, {onNodes.MotionDelta}f after the hand's last observed " +
                    $"motion, after polling up to 1.5 s; restore spread={onNodes.RestoreSpread} frame(s) over " +
                    $"{onNodes.RestoreFrameDetails.Count}/{onNodes.ReducedCards} hidden card(s), frames: " +
                    $"{DescribeList(onNodes.RestoreFrameDetails)}; ReduceEffects={PortraitHandLayout.ReduceEffects} " +
                    "(set true before draw_turn3 and first set false later, in the resting-hand control); the next action " +
                    "after this check is perf_turn3_play1");

            // A play only re-spaces the retained cards; it draws nothing, so every holder that moves must keep its glow
            // and shadow. This is the play half of the new rule.
            probe.BeginReduceWatch(true);
            await SetEnergyAsync(player, 9m);
            bool turn3Played = await PerfPlayCardAsync(room, player, "perf_turn3_play1", probe);
            var playNodes = probe.EndReduceWatch("play", checkRest: false);

            // ---- the preload regression gate, before the test-only orb fixture ----
            long gateEndMs = PerfClock.NowMs;
            ReportCombatAssetGate(assets, combatStartedMs, missedBefore, gateEndMs);

            // ---- the resting-hand control for ReduceEffects (a settled hand must draw the same either way) ----
            await RunReduceEffectRestControlAsync(probe);

            // ---- test-only orb pass (the orbs bucket and the orb VFX assets) ----
            await RunPerfOrbPassAsync(player, probe, assets, gateEndMs);

            // ---- gates ----
            ReportOurPassBudget(probe);
            ReportDrawAllocations(probe);
            ReportReduceEffects(probe, turn2Off, turn3On, offNodes, onNodes, playNodes, turn3Played);
            ReportIdleCapGate(probe);

            _report.Pass("perf-combat_complete", "perf-combat steps ran: draws, plays, cancel, enemy turns, orb pass, gates");
        }
        finally
        {
            PortraitHandLayout.ReduceEffects = false;
            assets.Detach();
            probe?.Stop();
        }
    }

    // ---- the measured turns -------------------------------------------------------------------------------------------

    /// <summary>
    /// The enemy turn after an End Turn tap and the next player's draw: waits for the play phase and a non-empty hand.
    /// The draw window has already been armed, so the probe opens it on the frame a new holder appears.
    /// </summary>
    private async Task<bool> PerfEndTurnAsync(NCombatRoom room, string label, PerfProbe probe)
    {
        NEndTurnButton? endTurn = room.Ui.EndTurnButton;
        bool ready = endTurn is not null && GodotObject.IsInstanceValid(endTurn) && endTurn.IsEnabled;
        _report.Check($"perf_{label}_end_turn_ready", ready,
            $"button={(endTurn is null ? "none" : endTurn.GetGlobalRect().ToString())} enabled={endTurn?.IsEnabled}");
        if (!ready) return false;

        // The enemy turn is a second interval for the frame-cap check, independent of the armed draw window.
        probe.BeginCapWatch($"cap_enemy_turn_{label}");
        await _pointer.TapAsync(CenterOf(endTurn!));
        bool started = await TryWaitAsync(() => CombatManager.Instance.PlayerActionsDisabled, $"enemy turn ({label})", ShortWait);
        _report.Check($"perf_{label}_enemy_turn", started,
            $"actionsDisabled={CombatManager.Instance.PlayerActionsDisabled}");
        if (!started)
        {
            probe.EndCapWatch();
            return false;
        }

        bool returned = await TryWaitAsync(
            () => !CombatManager.Instance.PlayerActionsDisabled || !CombatManager.Instance.IsInProgress,
            $"next player turn ({label})", TimeSpan.FromSeconds(30));
        probe.EndCapWatch();
        if (!returned || !CombatManager.Instance.IsInProgress)
        {
            _report.Fail($"perf_{label}_next_turn",
                $"the fight did not return control within 30 s: combatInProgress={CombatManager.Instance.IsInProgress} " +
                $"actionsDisabled={CombatManager.Instance.PlayerActionsDisabled} hand={HandCountSafe()}");
            return false;
        }
        bool hand = await TryWaitAsync(() => HandCountSafe() > 0, "next turn hand", TimeSpan.FromSeconds(8));
        _report.Check($"perf_{label}_next_turn", hand, $"next turn hand={HandCountSafe()}");
        return hand;
    }

    /// <summary>
    /// Plays one card by finger drag: a targeted card is released on the front enemy, everything else above the play
    /// zone. Reports the play and waits for the hand to settle again (the reflow is part of the measured session).
    /// </summary>
    private async Task<bool> PerfPlayCardAsync(NCombatRoom room, Player player, string step, PerfProbe probe)
    {
        NPlayerHand hand = GetHand();
        await ParkPointerAsync();
        NHandCardHolder? card = hand.ActiveHolders
            .Where(h => h.CardModel is { } model && model.CanPlay())
            .OrderByDescending(h => IsTargetedPlayable(h))
            .ThenByDescending(h => h.Hitbox.GetGlobalRect().GetCenter().X)
            .FirstOrDefault();
        if (card is null)
        {
            _report.Skip(step, $"no playable card in the hand ({HandCountSafe()} holder(s))");
            return false;
        }

        string cardName = card.CardModel?.Id.Entry ?? "unknown";
        bool targeted = IsTargetedPlayable(card);
        Vector2 start = PointOnHolder(card, 0.5f, 0.5f);
        int handBefore = hand.ActiveHolders.Count;
        int energyBefore = player.PlayerCombatState?.Energy ?? 0;
        Vector2 release;
        if (targeted)
        {
            NCreature? enemy = LivingEnemies(room).FirstOrDefault();
            if (enemy is null)
            {
                _report.Skip(step, $"no living enemy for targeted card {cardName}");
                return false;
            }
            release = CenterOf(enemy.Hitbox);
        }
        else
        {
            release = new Vector2(start.X, Mathf.Max(140f, start.Y - 520f));
        }

        bool dragged = await DragFromToAsync(start, release);
        bool played = await TryWaitAsync(
            () => HandCountSafe() < handBefore || (player.PlayerCombatState?.Energy ?? energyBefore) < energyBefore,
            step + " card play", TimeSpan.FromSeconds(6));
        await WaitFramesAsync(4);
        int handAfter = HandCountSafe();
        int energyAfter = player.PlayerCombatState?.Energy ?? -1;
        if (played) probe.NotePlayedCard(card.CardModel);
        _report.Check(step, dragged && played,
            $"card={cardName} targeted={targeted} release={release} hand={handAfter}/{handBefore} " +
            $"energy={energyAfter}/{energyBefore}");
        if (played) await WaitUntilStableAsync(CurrentHandSettledSignature, step + " hand settled", TimeSpan.FromSeconds(1.5));
        else await ParkPointerAsync();
        return dragged && played;
    }

    /// <summary>
    /// Drags a targeted card far from every enemy and releases: the aim is cancelled, the card returns to the hand and no
    /// energy is spent. A Strike is added as a fixture only when the drawn hand has no targeted card at all.
    /// </summary>
    private async Task PerfCancelAimAsync(NCombatRoom room, Player player, string step, PerfProbe probe)
    {
        NPlayerHand hand = GetHand();
        await ParkPointerAsync();
        List<NCreature> enemies = LivingEnemies(room);
        if (enemies.Count == 0)
        {
            _report.Skip(step, "no living enemy to aim at");
            return;
        }
        if (!hand.ActiveHolders.Any(IsTargetedPlayable))
        {
            _report.Pass(step + "_fixture", $"no targeted card in the hand; added {StrikeId} as a test-only fixture for the cancel");
            await AddCardAsync(player, StrikeId);
            await WaitUntilStableAsync(CurrentHandSettledSignature, "perf-combat card for the cancel", TimeSpan.FromSeconds(1.5));
        }

        NHandCardHolder? card = hand.ActiveHolders.Where(IsTargetedPlayable)
            .OrderByDescending(h => h.Hitbox.GetGlobalRect().GetCenter().X)
            .FirstOrDefault();
        if (card is null)
        {
            _report.Skip(step, "still no targeted card after the fixture");
            return;
        }
        string cardName = card.CardModel?.Id.Entry ?? "unknown";
        Vector2 far = FarFromEnemies(enemies, GetViewportSize());
        int handBefore = hand.ActiveHolders.Count;
        int energyBefore = player.PlayerCombatState?.Energy ?? 0;
        bool dragged = await DragFromToAsync(PointOnHolder(card, 0.5f, 0.5f), far);
        await WaitSecondsAsync(0.4);
        probe.NoteCancelledCard(card.CardModel);
        bool kept = dragged && !hand.InCardPlay && hand.ActiveHolders.Count == handBefore
                    && (player.PlayerCombatState?.Energy ?? energyBefore) == energyBefore && !IsTargeting();
        _report.Check(step, kept,
            $"card={cardName} far={far} dragged={dragged} cardPlay={hand.InCardPlay} " +
            $"hand={HandCountSafe()}/{handBefore} energy={player.PlayerCombatState?.Energy}/{energyBefore} targeting={IsTargeting()}");
        if (!kept) await ParkPointerAsync();
    }

    // ---- test-only passes ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Channels three orbs onto the fight to make the orbs bucket and the orb VFX run, samples 45 frames of the orb arc,
    /// and reports the assets loaded during the fixture separately (never gated: this is not a Defect run).
    /// </summary>
    private async Task RunPerfOrbPassAsync(Player player, PerfProbe probe, CombatAssetWatcher assets, long gateEndMs)
    {
        var context = new ThrowingPlayerChoiceContext();
        await OrbCmd.AddSlots(player, 3);
        await OrbCmd.Channel<FrostOrb>(context, player);
        await OrbCmd.Channel<LightningOrb>(context, player);
        await OrbCmd.Channel<DarkOrb>(context, player);
        await WaitUntilStableAsync(() => OrbSettledSignature(3), "perf-combat orbs settled", TimeSpan.FromSeconds(2.5));
        await WaitSecondsAsync(0.5);
        int orbs = FindAll<NOrb>(_tree.Root).Count(o => GodotObject.IsInstanceValid(o) && !o.IsQueuedForDeletion());
        _report.Check("perf_orbs_channeled", orbs == 3,
            $"orb nodes={orbs} (Frost, Lightning, Dark; OrbCmd.Channel adds the Ironclad's first slot as a test-only fixture)");

        probe.BeginSegment("orb_pass");
        await WaitFramesAsync(45);
        var stats = probe.EndSegment("orb_pass");
        if (stats is not null)
            _report.Check("perf_orb_pass_orbs_bucket", stats.PassRuns[PortraitFrameTiming.Orbs] > 0,
                $"orbs bucket ran in {stats.PassRuns[PortraitFrameTiming.Orbs]}/{stats.Frames} sampled frames, " +
                $"mean={Num(stats.PassMean(PortraitFrameTiming.Orbs), 3)}ms worst={Num(stats.PassWorst(PortraitFrameTiming.Orbs), 3)}ms");

        // The fixture is not a Defect run, so the game does not preload orb VFX for this fight; its loads are reported but
        // never gated (the preload gate above covers the fight itself).
        (int count, List<string> unique) = assets.Between(gateEndMs + 1, PerfClock.NowMs);
        string list = unique.Count == 0
            ? "none"
            : string.Join(", ", unique.Take(20)) + (unique.Count > 20 ? $" (+{unique.Count - 20} more)" : "");
        _report.Pass("perf_orb_pass_uncached_assets",
            $"test-only orb fixture: {count} 'Asset not cached' warning(s) after the fixture started, assets: {list} " +
            "(reported, not gated: an Ironclad fight is not expected to preload orb VFX)");
    }

    /// <summary>
    /// Control for <see cref="PortraitHandLayout.ReduceEffects"/>: a resting hand draws the same with the setting on or
    /// off, because it only hides the effects of cards arriving from a draw and restores the whole hand together once the
    /// draw settles. The gate is the draw/play watches; this control reports that the settled picture is not changed.
    /// </summary>
    private async Task RunReduceEffectRestControlAsync(PerfProbe probe)
    {
        await ParkPointerAsync();
        await WaitUntilStableAsync(CurrentHandSettledSignature, "perf-combat rest before the effect comparison", TimeSpan.FromSeconds(2));

        PortraitHandLayout.ReduceEffects = true;
        await WaitFramesAsync(8);
        probe.BeginSegment("reduce_effects_on");
        await WaitFramesAsync(40);
        var on = probe.EndSegment("reduce_effects_on");

        PortraitHandLayout.ReduceEffects = false;
        await WaitFramesAsync(8);
        probe.BeginSegment("reduce_effects_off");
        await WaitFramesAsync(40);
        var off = probe.EndSegment("reduce_effects_off");

        if (on is null || off is null)
        {
            _report.Skip("perf_reduce_effects_rest_draw_calls", "one of the resting control windows did not sample any frame");
            return;
        }
        _report.Pass("perf_reduce_effects_rest_draw_calls",
            $"resting-hand control: mean draw calls on={Num(on.MeanDrawCalls, 1)} off={Num(off.MeanDrawCalls, 1)} " +
            $"equalWithinOneCall={Math.Abs(on.MeanDrawCalls - off.MeanDrawCalls) <= 1.0} over {on.Frames}+{off.Frames} frames " +
            "(info, not gated: the setting restores the highlight and shadow at rest, so no difference is expected here; " +
            "the gate uses the two real draw windows)");
    }

    // ---- gates --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>perf_no_uncached_assets_in_combat</c>: no "Asset not cached" warning may be logged from one second after the room
    /// is entered until the end of the measured fight. The first second is ignored because the room and its hand are
    /// expected to pull their own assets on entry.
    /// </summary>
    private void ReportCombatAssetGate(CombatAssetWatcher assets, long combatStartedMs, int missedBefore, long gateEndMs)
    {
        long since = combatStartedMs + 1000;
        int before = assets.Count(long.MinValue, combatStartedMs - 1);
        int firstSecond = assets.Count(combatStartedMs, since - 1);
        (int count, List<string> unique) = assets.Between(since, gateEndMs);
        int missedNow = PreloadManager.Cache.MissedCacheAssetCount;
        string list = unique.Count == 0
            ? "none"
            : string.Join(", ", unique.Take(40)) + (unique.Count > 40 ? $" (+{unique.Count - 40} more)" : "");
        _report.Check("perf_no_uncached_assets_in_combat", count == 0,
            $"{count} 'Asset not cached' warning(s) after the fight's first second " +
            $"({firstSecond} in the fight's first second and {before} before combat ignored); " +
            $"missed-cache assets {missedBefore}->{missedNow} (delta {missedNow - missedBefore}); assets: {list}; " +
            "the test-only orb fixture that runs after this gate is reported by perf_orb_pass_uncached_assets, not here");
    }

    /// <summary>
    /// <c>perf_our_passes_budget</c>: the per-frame sum of the top-level portrait passes over every draw window (the real
    /// turn draws) must average ≤ 1.0 ms over all frames and never exceed 8 ms on a non-GC frame on Linux. A frame whose
    /// pass contained a collection is GC pressure, not layout cost (Perf2's rule): it is excluded from the worst and its
    /// count/worst/collection count are reported. Stage/selection/orbs/hpText are nested inside
    /// combat/preDraw/labels already and are not added a second time.
    /// </summary>
    private void ReportOurPassBudget(PerfProbe probe)
    {
        long frames = probe.DrawFrames;
        if (frames == 0)
        {
            _report.Fail("perf_our_passes_budget", "no draw-window frame was sampled; the budget cannot be checked");
            return;
        }
        double mean = probe.DrawMeanTopMs;
        double worstNonGc = probe.DrawWorstNonGcTopMs;
        long nonGc = probe.DrawNonGcFrames;
        long gc = probe.DrawGcFrames;
        string windows = string.Join("; ", probe.DrawWindows
            .Where(w => w.Frames > 0)
            .Select(w => $"{w.Label}={Num(w.MeanTopMs, 3)}/{Num(w.WorstNonGcTop, 3)}ms over {w.Frames}f " +
                         $"(gc_frames={w.GcFrames} worst_gc={Num(w.WorstGcTop, 2)})"));
        _report.Check("perf_our_passes_budget", mean <= 1.0 && worstNonGc <= 8.0 && nonGc > 0,
            $"mean={Num(mean, 3)}ms worst_nongc={Num(worstNonGc, 3)}ms over {frames} frames in " +
            $"{probe.DrawWindows.Count} draw window(s) ({nonGc} non-GC); budget on Linux: mean <= 1.000 ms over all " +
            "frames, worst frame <= 8.000 ms over non-GC frames only (a collection inside a pass is GC pressure, not " +
            "layout cost; Perf2's rule). Excluded: gc_frames=" + gc + " worst_gc=" + Num(probe.DrawWorstGcTopMs, 3) +
            "ms gc_collections=" + probe.DrawGcCollections + " max_per_frame=" + probe.DrawGcCollectionsMax +
            "; sum = combat+ftue+preDraw+hover+touch, with stage/selection inside combat, orbs inside preDraw and " +
            $"hpText inside labels; windows: {windows}");
    }

    /// <summary>
    /// <c>perf_draw_allocations</c>: reported, not gated (see the scenario doc). The figure is GC's current-thread
    /// allocation delta over each draw window, plus the collection counts, so a regression can be read off the run.
    /// </summary>
    private void ReportDrawAllocations(PerfProbe probe)
    {
        var windows = probe.DrawWindows.Where(w => w.Frames > 0).ToList();
        if (windows.Count == 0)
        {
            _report.Fail("perf_draw_allocations", "no draw-window frame was sampled; allocations cannot be reported");
            return;
        }
        string detail = string.Join("; ", windows.Select(w =>
            $"{w.Label}: frames={w.Frames} alloc_kb={Num(w.AllocKilobytes, 1)} " +
            $"alloc_per_frame={Num(w.AllocBytesPerFrame, 0)}B gc={w.Gc0}/{w.Gc1}/{w.Gc2}"));
        _report.Pass("perf_draw_allocations",
            "reported, not gated: whole main-thread allocation includes the game's own work and has no stable cross-run " +
            "baseline yet; " + detail);
    }

    /// <summary>
    /// ReduceEffects. The aggregate draw-call comparison across two turns is reported but not gated: two off draws
    /// already differ by more calls than the two hidden items per arriving card, and the third draw's scene is heavier
    /// (Perf2/ReduceEffects measured this in-runs), so it cannot resolve the setting.
    /// <para>
    /// The gates follow the hand-level rule: only cards <b>arriving</b> from a draw lose <c>%Highlight</c> and
    /// <c>%CardContainer/Shadow</c>; a seeded card that only re-spaces keeps them. On the draw window every arrival must
    /// be hidden with no violation, every re-spacer visible, and every hidden card must come back on one and the same
    /// frame (restore spread 0) — the launcher restores the whole hand together. The off draw must show every in-flight
    /// holder's nodes visible. A second watch around <c>perf_turn3_play1</c> checks the play half: nothing is drawn, so
    /// every holder that moves (the played card leaving, the rest re-spacing) must keep both nodes visible. Violation
    /// records carry holder index, card id, draw/motion frame deltas, each node's visibility, the highlight width and
    /// colour, the setting, playability and the card's role (last played/cancelled or drawn).
    /// </para>
    /// </summary>
    private void ReportReduceEffects(PerfProbe probe, PerfProbe.Stats? off, PerfProbe.Stats? on,
        PerfProbe.ReduceWatchResult? offNodes, PerfProbe.ReduceWatchResult? onNodes,
        PerfProbe.ReduceWatchResult? playNodes, bool playRan)
    {
        var firstOff = probe.DrawWindows.FirstOrDefault(w => w.Label == "draw_turn1");
        string calls = off is null || on is null
            ? "draw windows missing"
            : $"draw calls: draw_turn1(off)={Num(firstOff?.MeanDrawCalls ?? 0, 1)} " +
              $"draw_turn2(off)={Num(off.MeanDrawCalls, 1)} draw_turn3(on)={Num(on.MeanDrawCalls, 1)} " +
              $"(on<off={on.MeanDrawCalls < off.MeanDrawCalls}; the two off draws' own spread is " +
              $"{Num(Math.Abs((firstOff?.MeanDrawCalls ?? 0) - off.MeanDrawCalls), 1)} calls)";
        _report.Pass("perf_reduce_effects_draw_calls",
            "reported, not gated: different turns are not comparable (scene/card content differ by more than the ~10 " +
            "calls the setting hides); the gate is perf_reduce_effects_inflight_nodes; " + calls);

        bool ok = onNodes is { FramesWithInflight: > 0, FramesWithHiddenPair: > 0, Violations: 0,
                               RespaceViolations: 0, RestViolations: 0, RestoreSpread: 0 }
                  && onNodes.RestoreFrameDetails.Count == onNodes.ReducedCards
                  && offNodes is { FramesWithInflight: > 0, Violations: 0 };
        _report.Check("perf_reduce_effects_inflight_nodes", ok,
            $"off draw: {DescribeWatch(offNodes)}; on draw: {DescribeWatch(onNodes)}; " +
            $"restore frames: {DescribeList(onNodes?.RestoreFrameDetails)}; " +
            $"inflight violations: {DescribeList(onNodes?.ViolationDetails)}; " +
            $"rest violations: {DescribeList(onNodes?.RestDetails)}; " +
            $"rest hidden but never reduced by the setting (informational): " +
            $"{DescribeList(onNodes?.UntouchedHiddenDetails)}");

        if (!playRan)
        {
            _report.Skip("perf_reduce_effects_respace_nodes",
                "the turn-3 play did not run, so the re-space half could not be measured");
            return;
        }
        _report.Check("perf_reduce_effects_respace_nodes",
            playNodes is { RespaceChecked: > 0, RespaceViolations: 0, Violations: 0 },
            $"play window (no draw, only re-spacing): {DescribeWatch(playNodes)}; " +
            $"violations: {DescribeList(playNodes?.ViolationDetails)}; " +
            "the played card leaving and every other holder moving must keep their glow and shadow");
    }

    private static string DescribeList(List<string>? lines) =>
        lines is null || lines.Count == 0 ? "none" : string.Join(" | ", lines);

    private static string DescribeWatch(PerfProbe.ReduceWatchResult? watch) => watch is null
        ? "missing"
        : $"mode={watch.Mode} frames={watch.Frames} inflight_frames={watch.FramesWithInflight} " +
          $"inflight_holders={watch.InflightHolders} hidden_pair_frames={watch.FramesWithHiddenPair} " +
          $"violations={watch.Violations} respace_checked={watch.RespaceChecked} " +
          $"respace_violations={watch.RespaceViolations} reduced={watch.ReducedCards} " +
          $"restore_spread={watch.RestoreSpread} rest_checked={watch.RestChecked} " +
          $"rest_violations={watch.RestViolations} rest_untouched_hidden={watch.RestUntouchedHidden} " +
          $"draw_start_delta={watch.DrawStartDelta} motion_delta={watch.MotionDelta}";

    /// <summary>
    /// <c>perf_no_idle_cap_during_animation</c>: during every turn-start draw window and every enemy turn, no sampled frame
    /// may have Engine.MaxFps at the launcher's idle cap (30) or lower. The probe publishes <c>PhoneFrameCap</c>'s inputs
    /// and applies it every frame with Lower Idle Frame Rate on — the same two calls PhoneGraphics makes — so this runs
    /// the app's own governor path, activity input included, rather than a measurement of an untouched cap.
    /// </summary>
    private void ReportIdleCapGate(PerfProbe probe)
    {
        var windows = probe.DrawWindows.Concat(probe.CapWatches).Where(w => w.Frames > 0).ToList();
        if (windows.Count == 0)
        {
            _report.Fail("perf_no_idle_cap_during_animation", "no draw or enemy-turn window sampled a frame");
            return;
        }
        int cappedWindows = windows.Count(w => w.MaxFpsIdleFrames > 0);
        int shortWindows = windows.Count(w => w.AnimatingFrames < 10);
        string perWindow = string.Join("; ", windows.Select(w =>
            $"{w.Label}: anim={w.AnimatingFrames}/{w.Frames}f " +
            $"maxfps={Num(w.MonitorMin(9), 0)}/{Num(w.MonitorMean(9), 1)}/{Num(w.MonitorMax(9), 0)} " +
            $"cap30_anim={w.MaxFpsIdleFrames} cap30_all={w.IdleCapFrames}"));
        _report.Check("perf_no_idle_cap_during_animation", cappedWindows == 0 && shortWindows == 0,
            $"{cappedWindows}/{windows.Count} animated window(s) had Engine.MaxFps at the idle cap " +
            $"({FrameRateGovernor.IdleFrameRate}) or lower on a frame the probe classified as animating " +
            $"(actions disabled or a hand card moving); {shortWindows} window(s) had fewer than 10 animating frames " +
            $"(the classifier must see the animation); governor driven: yes, test-only PhoneFrameCap.SetInputs+Apply " +
            $"(Lower Idle Frame Rate=true, game limit forced 0, no touch, thermal unknown); windows: {perWindow}");
    }

    // ---- the per-frame probe -------------------------------------------------------------------------------------------

    /// <summary>
    /// Samples every drawn frame from <see cref="RenderingServer.FramePostDraw"/> (the pass timings are complete by then):
    /// the wall frame delta, the nine game monitors, all 14 <see cref="PortraitFrameTiming"/> buckets, and the main
    /// thread's GC counters. Three accumulators run at once: the whole fight, the current second, and the current
    /// marked window (draw window or named segment). Nothing is formatted per frame; a step is written at each boundary.
    /// </summary>
    private sealed class PerfProbe
    {
        // The nine Performance monitors, then four per-frame numbers sampled directly: Engine.MaxFps and the CPU setup /
        // measured render times (RenderingServer), all reported only.
        private const int PerformanceMonitorCount = 9;
        private const int MonitorCount = 13;

        // The non-nested passes. Stage/selection are inside combat, orbs inside preDraw, hpText inside labels.
        private static readonly int[] TopLevelPasses =
        [
            PortraitFrameTiming.Combat, PortraitFrameTiming.Ftue, PortraitFrameTiming.PreDraw,
            PortraitFrameTiming.Hover, PortraitFrameTiming.Touch,
        ];

        private static readonly Performance.Monitor[] Monitors =
        [
            Performance.Monitor.TimeProcess,
            Performance.Monitor.TimePhysicsProcess,
            Performance.Monitor.RenderTotalDrawCallsInFrame,
            Performance.Monitor.RenderTotalObjectsInFrame,
            Performance.Monitor.RenderTotalPrimitivesInFrame,
            Performance.Monitor.ObjectCount,
            Performance.Monitor.ObjectNodeCount,
            Performance.Monitor.ObjectResourceCount,
            Performance.Monitor.MemoryStatic,
        ];

        private static readonly string[] MonitorNames =
        [
            "time_process_ms", "time_physics_ms", "draw_calls", "render_objects", "render_primitives",
            "objects", "object_nodes", "object_resources", "memory_static_mb",
            "engine_max_fps", "frame_setup_ms", "render_cpu_ms", "render_gpu_ms",
        ];

        // Test-only: the idle-cap fixture the launcher's PhoneGraphics would build. The host does not link PhoneGraphics,
        // so the probe publishes PhoneFrameCap's inputs and applies it, the same two calls PhoneGraphics makes: Lower Idle
        // Frame Rate on, no game limit (so 30 can only come from the idle cap), no touch and a long time since the last one.
        private static readonly PhoneGraphicsSettings IdleFixture =
            PhoneGraphicsSettings.Default with { LowerIdleFrameRate = true, CoolDownWhenHot = false };

        private readonly SceneTree _tree;
        private readonly TestReport _report;
        private readonly double[] _monitorScratch = new double[MonitorCount];
        private readonly HashSet<ulong> _baselineIds = new();
        private readonly List<Stats> _drawWindows = new();
        private readonly List<Stats> _capWatches = new();
        // Frame-to-frame holder positions for the activity classifier and the ReduceEffects mechanism watch. Two
        // dictionaries swap each frame, so a steady hand allocates nothing.
        private Dictionary<ulong, Vector2> _prevHolderPos = new();
        private Dictionary<ulong, Vector2> _nextHolderPos = new();
        private Dictionary<ulong, Vector2> _rwPrev = new();
        private Dictionary<ulong, Vector2> _rwNext = new();
        private readonly Dictionary<ulong, long> _rwLastMove = new();
        private readonly HashSet<ulong> _rwReducedCards = new();
        private readonly HashSet<ulong> _rwSeededIds = new();
        private readonly Dictionary<ulong, ulong> _rwRestoreFrames = new();
        private long _rwFrame;
        private long _rwRestoreSpread = -1;
        private ulong _rwDrawStartFrame;
        private ulong _rwLastMotionFrame;
        private CardModel? _lastPlayedCard;
        private CardModel? _lastCancelledCard;
        private bool _reduceWatchActive;
        private bool _reduceWatchOn;
        private long _rwFrames, _rwFramesWithInflight, _rwFramesWithHiddenPair, _rwViolations, _rwInflightHolders;
        private long _rwRespaceChecked, _rwRespaceViolations;
        private readonly List<string> _rwViolationDetails = new();

        private readonly Stats _total;
        private Stats _second;
        private Stats? _window;
        private bool _windowIsDraw;
        private Stats? _capWatch;
        private string? _armedDraw;
        private Rid _rootViewport;
        private bool _renderMeasure;
        private int _maxFpsBefore;
        private Control? _cardContainer;
        private ulong _cardContainerHand;
        private ulong _lastFrame = ulong.MaxValue;
        private ulong _lastWallUsec;
        private int _secondIndex = 1;
        private bool _running;

        public PerfProbe(SceneTree tree, TestReport report)
        {
            _tree = tree;
            _report = report;
            _total = new Stats("combat");
            _second = new Stats("second_1");
        }

        public IReadOnlyList<Stats> DrawWindows => _drawWindows;
        public IReadOnlyList<Stats> CapWatches => _capWatches;
        public bool RenderMeasureEnabled => _renderMeasure;

        public long DrawFrames
        {
            get
            {
                long frames = 0;
                foreach (Stats window in _drawWindows) frames += window.Frames;
                return frames;
            }
        }

        public double DrawMeanTopMs
        {
            get
            {
                double sum = 0;
                long frames = 0;
                foreach (Stats window in _drawWindows)
                {
                    sum += window.TopSum;
                    frames += window.Frames;
                }
                return frames > 0 ? sum / frames : 0.0;
            }
        }

        public double DrawWorstTopMs
        {
            get
            {
                double worst = 0;
                foreach (Stats window in _drawWindows)
                    if (window.TopMax > worst) worst = window.TopMax;
                return worst;
            }
        }

        public double DrawWorstNonGcTopMs
        {
            get
            {
                double worst = 0;
                foreach (Stats window in _drawWindows)
                    if (window.WorstNonGcTop > worst) worst = window.WorstNonGcTop;
                return worst;
            }
        }

        public double DrawWorstGcTopMs
        {
            get
            {
                double worst = 0;
                foreach (Stats window in _drawWindows)
                    if (window.WorstGcTop > worst) worst = window.WorstGcTop;
                return worst;
            }
        }

        public long DrawNonGcFrames
        {
            get
            {
                long frames = 0;
                foreach (Stats window in _drawWindows) frames += window.NonGcFrames;
                return frames;
            }
        }

        public long DrawGcFrames
        {
            get
            {
                long frames = 0;
                foreach (Stats window in _drawWindows) frames += window.GcFrames;
                return frames;
            }
        }

        public long DrawGcCollections
        {
            get
            {
                long count = 0;
                foreach (Stats window in _drawWindows) count += window.GcCollections;
                return count;
            }
        }

        public int DrawGcCollectionsMax
        {
            get
            {
                int max = 0;
                foreach (Stats window in _drawWindows)
                    if (window.GcCollectionsMax > max) max = window.GcCollectionsMax;
                return max;
            }
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            long now = PerfClock.NowMs;
            _maxFpsBefore = Engine.MaxFps;
            // The viewport's measured render times are opt-in in Godot; the root viewport is the one the player sees.
            _rootViewport = _tree.Root.GetViewportRid();
            if (_rootViewport.IsValid)
            {
                RenderingServer.ViewportSetMeasureRenderTime(_rootViewport, true);
                _renderMeasure = true;
            }
            Open(_total, now);
            Open(_second, now);
            RenderingServer.FramePostDraw += OnFramePostDraw;
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            RenderingServer.FramePostDraw -= OnFramePostDraw;
            if (_renderMeasure)
            {
                RenderingServer.ViewportSetMeasureRenderTime(_rootViewport, false);
                _renderMeasure = false;
            }
            // Leave the launcher path sane: publish the cap the host had, with the idle choice off, so the hand pass
            // cannot re-apply the fixture cap during teardown.
            Engine.MaxFps = _maxFpsBefore;
            PhoneFrameCap.SetInputs(_maxFpsBefore, IdleFixture with { LowerIdleFrameRate = false }, touching: true,
                secondsSinceTouch: 0.0, DevicePower.UnknownThermalState);
            PhoneFrameCap.Apply();
            long now = PerfClock.NowMs;
            if (_armedDraw is not null)
            {
                _report.Fail($"perf_{_armedDraw}_summary", "the scenario ended before the draw window opened");
                _armedDraw = null;
            }
            if (_window is not null)
            {
                Stats partial = Close(_window, now);
                _window = null;
                if (_windowIsDraw) _drawWindows.Add(partial);
                _windowIsDraw = false;
                _report.Pass($"perf_{partial.Label}_summary", Describe(partial, partial: true));
            }
            if (_capWatch is not null) EndCapWatch();
            _reduceWatchActive = false;
            if (_second.Frames > 0)
            {
                Stats second = Close(_second, now);
                _report.Pass($"perf_{second.Label}_summary", Describe(second, partial: true));
            }
            Stats total = Close(_total, now);
            _report.Pass("perf_combat_summary", Describe(total, partial: false));
        }

        /// <summary>Arms the draw-window detector: the current hand's holders are the baseline, and the first holder not in it
        /// opens the window (the hand may be empty, and End Turn's discard must not count as a draw).</summary>
        public void ArmDrawWindow(string label)
        {
            _armedDraw = label;
            _baselineIds.Clear();
            if (!TryContainer(out Control? container)) return;
            int children = container!.GetChildCount();
            for (int i = 0; i < children; i++)
            {
                if (container.GetChild(i, false) is not NHandCardHolder holder) continue;
                if (!GodotObject.IsInstanceValid(holder) || !holder.IsVisibleInTree()) continue;
                _baselineIds.Add(holder.GetInstanceId());
            }
        }

        public Stats? EndDrawWindow(string label)
        {
            _armedDraw = null;
            if (_window is null)
            {
                _report.Fail($"perf_{label}_summary",
                    "the draw window never opened: no new hand holder appeared before the hand was ended");
                return null;
            }
            Stats closed = Close(_window, PerfClock.NowMs);
            _window = null;
            _windowIsDraw = false;
            _drawWindows.Add(closed);
            _report.Pass($"perf_{closed.Label}_summary", Describe(closed, partial: false));
            return closed;
        }

        public void BeginSegment(string label)
        {
            if (_window is not null)
            {
                Stats previous = Close(_window, PerfClock.NowMs);
                if (_windowIsDraw) _drawWindows.Add(previous);
                _report.Pass($"perf_{previous.Label}_summary", Describe(previous, partial: true));
            }
            _window = new Stats(label);
            _windowIsDraw = false;
            Open(_window, PerfClock.NowMs);
        }

        public Stats? EndSegment(string label)
        {
            if (_window is null)
            {
                _report.Fail($"perf_{label}_summary", "the segment was never started");
                return null;
            }
            Stats closed = Close(_window, PerfClock.NowMs);
            _window = null;
            _windowIsDraw = false;
            _report.Pass($"perf_{closed.Label}_summary", Describe(closed, partial: false));
            return closed;
        }

        private void OnFramePostDraw()
        {
            if (!_running) return;
            ulong frame = Engine.GetProcessFrames();
            if (frame == _lastFrame) return;
            _lastFrame = frame;

            long now = PerfClock.NowMs;
            // The frame delta the player sees: the wall interval between two drawn frames, in microseconds (the same figure
            // the device review read as 60/30 fps). TIME_PROCESS is reported separately as Godot's own process time.
            ulong wallUsec = Time.GetTicksUsec();
            double frameMs = _lastWallUsec == 0 ? 0.0 : (wallUsec - _lastWallUsec) / 1000.0;
            _lastWallUsec = wallUsec;
            // Black-box activity for the idle-cap gate: the game resolving something (enemy turn, card, turn-start draw)
            // or a hand card that moved since the previous sample. Boundary frames of a window are therefore never
            // counted as animated unless something really was moving.
            bool animating = FrameAnimating();
            bool gcInPass = PortraitFrameTiming.LastFrameGcInPass;
            int collections = PortraitFrameTiming.LastFrameCollections;
            DriveIdleGovernor();
            SampleMonitors();
            SampleReduceWatch();
            ReadOnlySpan<double> passes = PortraitFrameTiming.LastFrameMs();

            if (_armedDraw is not null && HasNewHolder())
            {
                _window = new Stats(_armedDraw);
                _windowIsDraw = true;
                _armedDraw = null;
                Open(_window, now);
                if (_reduceWatchActive) _rwDrawStartFrame = frame;
            }
            if (_window is not null) _window.Add(frameMs, passes, _monitorScratch, animating, gcInPass, collections);
            if (_capWatch is not null) _capWatch.Add(frameMs, passes, _monitorScratch, animating, gcInPass, collections);
            _second.Add(frameMs, passes, _monitorScratch, animating, gcInPass, collections);
            _total.Add(frameMs, passes, _monitorScratch, animating, gcInPass, collections);

            if (now - _second.StartMs >= 1000)
            {
                Stats closed = Close(_second, now);
                _report.Pass($"perf_{closed.Label}_summary", Describe(closed, partial: false));
                _secondIndex++;
                _second = new Stats($"second_{_secondIndex}");
                Open(_second, now);
            }
        }

        /// <summary>
        /// Starts a second, independent accumulator for a named interval (an enemy turn) so the frame cap can be checked
        /// there too while the draw-window accumulator stays armed. Its summary is reported like any other window.
        /// </summary>
        public void BeginCapWatch(string label)
        {
            if (_capWatch is not null) EndCapWatch();
            _capWatch = new Stats(label);
            Open(_capWatch, PerfClock.NowMs);
        }

        public Stats? EndCapWatch()
        {
            if (_capWatch is null) return null;
            Stats closed = Close(_capWatch, PerfClock.NowMs);
            _capWatch = null;
            _capWatches.Add(closed);
            _report.Pass($"perf_{closed.Label}_summary", Describe(closed, partial: false));
            return closed;
        }

        /// <summary>
        /// The test-only stand-in for <c>PhoneGraphics._Process</c>: publishes <c>PhoneFrameCap</c>'s inputs and applies
        /// the cap, the same two calls the launcher makes, every sampled frame. It runs before
        /// <see cref="SampleMonitors"/> so the sample is the cap the frame is drawn under, and the hand pass's own
        /// <c>PhoneFrameCap.Apply()</c> covers a draw's first frame exactly as it does on the phone.
        /// </summary>
        private void DriveIdleGovernor()
        {
            PhoneFrameCap.SetInputs(0, IdleFixture, touching: false, secondsSinceTouch: 100.0,
                DevicePower.UnknownThermalState);
            PhoneFrameCap.Apply();
        }

        /// <summary>
        /// The nine Performance monitors plus the four numbers sampled directly: Engine.MaxFps (the launcher governor's
        /// output), the CPU frame setup time, and the root viewport's measured CPU/GPU render times. Reported only.
        /// </summary>
        private void SampleMonitors()
        {
            for (int i = 0; i < PerformanceMonitorCount; i++) _monitorScratch[i] = Performance.GetMonitor(Monitors[i]);
            _monitorScratch[9] = Engine.MaxFps;
            _monitorScratch[10] = RenderingServer.GetFrameSetupTimeCpu() * 1000.0;
            if (_renderMeasure)
            {
                _monitorScratch[11] = RenderingServer.ViewportGetMeasuredRenderTimeCpu(_rootViewport) * 1000.0;
                _monitorScratch[12] = RenderingServer.ViewportGetMeasuredRenderTimeGpu(_rootViewport) * 1000.0;
            }
            else
            {
                _monitorScratch[11] = 0.0;
                _monitorScratch[12] = 0.0;
            }
        }

        private void Open(Stats stats, long nowMs)
        {
            stats.Open(nowMs, GC.GetAllocatedBytesForCurrentThread(), GC.CollectionCount(0),
                GC.CollectionCount(1), GC.CollectionCount(2), VisibleCards());
        }

        private Stats Close(Stats stats, long nowMs)
        {
            stats.Close(nowMs, GC.GetAllocatedBytesForCurrentThread(), GC.CollectionCount(0),
                GC.CollectionCount(1), GC.CollectionCount(2), VisibleCards());
            return stats;
        }

        private int VisibleCards()
        {
            if (!TryContainer(out Control? container)) return 0;
            int count = 0;
            int children = container!.GetChildCount();
            for (int i = 0; i < children; i++)
            {
                if (container.GetChild(i, false) is NHandCardHolder holder
                    && GodotObject.IsInstanceValid(holder) && holder.IsVisibleInTree())
                    count++;
            }
            return count;
        }

        private bool HasNewHolder()
        {
            if (!TryContainer(out Control? container)) return false;
            int children = container!.GetChildCount();
            for (int i = 0; i < children; i++)
            {
                if (container.GetChild(i, false) is not NHandCardHolder holder) continue;
                if (!GodotObject.IsInstanceValid(holder) || !holder.IsVisibleInTree()) continue;
                if (!_baselineIds.Contains(holder.GetInstanceId())) return true;
            }
            return false;
        }

        /// <summary>
        /// Whether this frame is animating, observed without asking the governor: the game is resolving something
        /// (<see cref="CombatManager"/> has player actions disabled) or a visible hand holder moved since the previous
        /// sample (a card in flight; a holder that just appeared counts as moving).
        /// </summary>
        private bool FrameAnimating()
        {
            bool actionsDisabled;
            try
            {
                actionsDisabled = CombatManager.Instance.PlayerActionsDisabled;
            }
            catch (Exception)
            {
                actionsDisabled = false;
            }
            return actionsDisabled || UpdateHolderMotion();
        }

        private bool UpdateHolderMotion()
        {
            Dictionary<ulong, Vector2> previous = _prevHolderPos;
            Dictionary<ulong, Vector2> next = _nextHolderPos;
            next.Clear();
            bool moving = false;
            if (TryContainer(out Control? container))
            {
                int children = container!.GetChildCount();
                for (int i = 0; i < children; i++)
                {
                    if (container.GetChild(i, false) is not NHandCardHolder holder) continue;
                    if (!GodotObject.IsInstanceValid(holder) || !holder.IsVisibleInTree()) continue;
                    ulong id = holder.GetInstanceId();
                    Vector2 position = holder.GetGlobalPosition();
                    next[id] = position;
                    if (!previous.TryGetValue(id, out Vector2 before)
                        || position.DistanceSquaredTo(before) > 0.25f)
                        moving = true;
                }
            }
            _prevHolderPos = next;
            _nextHolderPos = previous;
            return moving;
        }

        /// <summary>
        /// Starts/stops a ReduceEffects mechanism watch. Every frame, each visible hand holder that is in flight (moved
        /// within the last two samples; the settled hand's held-off-slot positions are not motion) is classified:
        /// <list type="bullet">
        /// <item>a <b>seeded</b> holder (present when the watch began) that moves is a re-space, and the card must keep
        /// its <c>%Highlight</c> and <c>%CardContainer/Shadow</c> visible — only cards arriving from a draw lose them;</item>
        /// <item>a <b>new</b> holder is an arrival, and with the setting on both nodes must be hidden on that frame; with
        /// the setting off they must be visible.</item>
        /// </list>
        /// The watch keeps the reduced cards so the closing check can poll for their restore and verify it happened on
        /// one frame for the whole hand.
        /// </summary>
        public void BeginReduceWatch(bool reduceOn)
        {
            _reduceWatchActive = true;
            _reduceWatchOn = reduceOn;
            _rwFrames = _rwFramesWithInflight = _rwFramesWithHiddenPair = _rwViolations = _rwInflightHolders = 0;
            _rwRespaceChecked = _rwRespaceViolations = 0;
            _rwFrame = 0;
            _rwDrawStartFrame = 0;
            _rwLastMotionFrame = 0;
            _rwRestoreSpread = -1;
            _rwRestoreFrames.Clear();
            _rwReducedCards.Clear();
            _rwSeededIds.Clear();
            _rwViolationDetails.Clear();
            _rwLastMove.Clear();
            _rwNext.Clear();
            _rwPrev.Clear();
            // Seed the previous positions and the ids of the resting hand: a seeded holder that later moves is a re-space,
            // not a pile arrival, so it must keep its effects.
            if (TryContainer(out Control? container))
            {
                int children = container!.GetChildCount();
                for (int i = 0; i < children; i++)
                {
                    if (container.GetChild(i, false) is not NHandCardHolder holder) continue;
                    if (!GodotObject.IsInstanceValid(holder) || !holder.IsVisibleInTree()) continue;
                    ulong id = holder.GetInstanceId();
                    _rwPrev[id] = holder.GetGlobalPosition();
                    _rwSeededIds.Add(id);
                }
            }
        }

        public sealed class ReduceWatchResult
        {
            public string Mode = "";
            public long Frames, FramesWithInflight, FramesWithHiddenPair, InflightHolders, Violations;
            public long RespaceChecked, RespaceViolations;
            public long RestChecked, RestViolations, RestUntouchedHidden;
            public long ReducedCards, RestoreSpread = -1;
            public long DrawStartDelta = -1, MotionDelta = -1;
            public readonly List<string> ViolationDetails = new();
            public readonly List<string> RestDetails = new();
            public readonly List<string> UntouchedHiddenDetails = new();
            public readonly List<string> RestoreFrameDetails = new();
        }

        /// <summary>Remembers the card a scenario action just played or cancelled, for the violation detail's role field.</summary>
        public void NotePlayedCard(CardModel? card) => _lastPlayedCard = card;

        public void NoteCancelledCard(CardModel? card) => _lastCancelledCard = card;

        public ReduceWatchResult? EndReduceWatch(string mode, bool checkRest)
        {
            if (!_reduceWatchActive) return null;
            _reduceWatchActive = false;
            var result = new ReduceWatchResult
            {
                Mode = mode,
                Frames = _rwFrames,
                FramesWithInflight = _rwFramesWithInflight,
                FramesWithHiddenPair = _rwFramesWithHiddenPair,
                InflightHolders = _rwInflightHolders,
                Violations = _rwViolations,
                RespaceChecked = _rwRespaceChecked,
                RespaceViolations = _rwRespaceViolations,
                ReducedCards = _rwReducedCards.Count,
                RestoreSpread = _rwRestoreSpread,
            };
            result.ViolationDetails.AddRange(_rwViolationDetails);
            foreach ((ulong id, ulong frame) in _rwRestoreFrames)
                result.RestoreFrameDetails.Add($"node={id} frame={frame}");
            ulong now = Engine.GetProcessFrames();
            result.DrawStartDelta = _rwDrawStartFrame == 0 ? -1 : (long)now - (long)_rwDrawStartFrame;
            result.MotionDelta = _rwLastMotionFrame == 0 ? -1 : (long)now - (long)_rwLastMotionFrame;
            if (checkRest) CheckRestVisuals(result, now);
            return result;
        }

        /// <summary>
        /// Polls until every card the setting hid during the watch is visible again (highlight and shadow), and records the
        /// frame each one first showed both. The spread of those frames is the same-frame check: the launcher restores the
        /// whole hand on one frame, so a spread other than 0 is a bug. Call it immediately after the draw window closes,
        /// before the next game action; it returns at the timeout with -1 if some card never came back.
        /// </summary>
        public async Task WaitForRestoreAsync(TimeSpan timeout)
        {
            _rwRestoreFrames.Clear();
            _rwRestoreSpread = -1;
            if (_rwReducedCards.Count == 0)
            {
                _rwRestoreSpread = 0;
                return;
            }
            ulong start = Time.GetTicksMsec();
            while (true)
            {
                foreach (ulong id in _rwReducedCards)
                {
                    if (_rwRestoreFrames.ContainsKey(id)) continue;
                    if (!TryReducedCardVisible(id, out bool visible) || !visible) continue;
                    _rwRestoreFrames[id] = Engine.GetProcessFrames();
                }
                if (_rwRestoreFrames.Count == _rwReducedCards.Count) break;
                if (Time.GetTicksMsec() - start > (ulong)timeout.TotalMilliseconds) break;
                await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
            }
            if (_rwRestoreFrames.Count == _rwReducedCards.Count)
            {
                ulong min = ulong.MaxValue;
                ulong max = 0;
                foreach (ulong frame in _rwRestoreFrames.Values)
                {
                    if (frame < min) min = frame;
                    if (frame > max) max = frame;
                }
                _rwRestoreSpread = (long)(max - min);
            }
        }

        private static bool TryReducedCardVisible(ulong cardNodeId, out bool visible)
        {
            visible = false;
            if (GodotObject.InstanceFromId(cardNodeId) is not NCard card || !GodotObject.IsInstanceValid(card))
                return false;
            if (!GodotObject.IsInstanceValid(card.CardHighlight) || !card.CardHighlight.Visible) return true;
            CanvasItem? shadow = card.GetNodeOrNull<CanvasItem>("CardContainer/Shadow");
            visible = shadow is not null && shadow.Visible;
            return true;
        }

        private void SampleReduceWatch()
        {
            if (!_reduceWatchActive) return;
            _rwFrames++;
            _rwFrame++;
            ulong nowFrame = Engine.GetProcessFrames();
            bool anyInflight = false;
            bool anyHiddenPair = false;
            bool violation = false;
            if (TryContainer(out Control? container))
            {
                int children = container!.GetChildCount();
                for (int i = 0; i < children; i++)
                {
                    if (container.GetChild(i, false) is not NHandCardHolder holder) continue;
                    if (!GodotObject.IsInstanceValid(holder) || !holder.IsVisibleInTree()) continue;
                    ulong id = holder.GetInstanceId();
                    Vector2 position = holder.GetGlobalPosition();
                    _rwNext[id] = position;
                    bool moved = !_rwPrev.TryGetValue(id, out Vector2 before)
                                 || position.DistanceSquaredTo(before) > 0.25f;
                    if (moved)
                    {
                        _rwLastMove[id] = _rwFrame;
                        _rwLastMotionFrame = nowFrame;
                    }
                    // Two frames of carry, like the product's own HandMoving: a glide can pause for a frame at an ease
                    // plateau, and the settled hand's held-off-slot positions must not be mistaken for motion.
                    if (!_rwLastMove.TryGetValue(id, out long lastMove) || _rwFrame - lastMove > 2) continue;
                    _rwInflightHolders++;
                    anyInflight = true;
                    NCard? card = holder.CardNode;
                    if (card is null) continue;
                    bool highlightHidden = GodotObject.IsInstanceValid(card.CardHighlight) && !card.CardHighlight.Visible;
                    CanvasItem? shadow = card.GetNodeOrNull<CanvasItem>("CardContainer/Shadow");
                    bool shadowHidden = shadow is not null && !shadow.Visible;
                    if (_reduceWatchOn && _rwSeededIds.Contains(id))
                    {
                        // A card that was already in the hand and only re-spaced: it kept its glow tween, so both nodes
                        // must still be visible.
                        _rwRespaceChecked++;
                        if (highlightHidden || shadowHidden)
                        {
                            _rwRespaceViolations++;
                            if (_rwViolationDetails.Count < 12)
                                _rwViolationDetails.Add(DescribeHolder(i, holder, card, !highlightHidden, !shadowHidden,
                                    "respace(hidden)"));
                        }
                    }
                    else if (_reduceWatchOn)
                    {
                        // A new holder is a pile arrival: with the setting on both nodes must be hidden.
                        if (highlightHidden)
                        {
                            _rwReducedCards.Add(card.GetInstanceId());
                            if (shadowHidden) anyHiddenPair = true;
                        }
                        if (!(highlightHidden && shadowHidden))
                        {
                            violation = true;
                            if (_rwViolationDetails.Count < 12)
                                _rwViolationDetails.Add(DescribeHolder(i, holder, card, !highlightHidden, !shadowHidden,
                                    "inflight(arrival)"));
                        }
                    }
                    else if (highlightHidden || shadowHidden)
                    {
                        violation = true;
                        if (_rwViolationDetails.Count < 12)
                            _rwViolationDetails.Add(DescribeHolder(i, holder, card, !highlightHidden, !shadowHidden,
                                "inflight(off)"));
                    }
                }
            }
            // A card can come back before the draw window closes, so record the first frame each hidden card shows both
            // nodes again right here, while the watch is live. The spread over those frames is the same-frame check.
            foreach (ulong id in _rwReducedCards)
            {
                if (_rwRestoreFrames.ContainsKey(id)) continue;
                if (TryReducedCardVisible(id, out bool visible) && visible)
                    _rwRestoreFrames[id] = nowFrame;
            }
            var swap = _rwPrev;
            _rwPrev = _rwNext;
            _rwNext = swap;
            _rwNext.Clear();
            if (anyInflight) _rwFramesWithInflight++;
            if (anyHiddenPair) _rwFramesWithHiddenPair++;
            if (violation) _rwViolations++;
        }

        /// <summary>
        /// After the On draw: only the cards the setting actually hid during the watch must show both nodes again. A
        /// resting card the watch never saw hidden is the game's own highlight state and is reported separately, not
        /// counted as a restore violation.
        /// </summary>
        private void CheckRestVisuals(ReduceWatchResult result, ulong now)
        {
            if (!TryContainer(out Control? container)) return;
            int children = container!.GetChildCount();
            for (int i = 0; i < children; i++)
            {
                if (container.GetChild(i, false) is not NHandCardHolder holder) continue;
                if (!GodotObject.IsInstanceValid(holder) || !holder.IsVisibleInTree()) continue;
                NCard? card = holder.CardNode;
                if (card is null) continue;
                bool highlightVisible = GodotObject.IsInstanceValid(card.CardHighlight) && card.CardHighlight.Visible;
                CanvasItem? shadow = card.GetNodeOrNull<CanvasItem>("CardContainer/Shadow");
                bool shadowVisible = shadow is not null && shadow.Visible;
                bool reduced = _rwReducedCards.Contains(card.GetInstanceId());
                if (reduced)
                {
                    result.RestChecked++;
                    if (!highlightVisible || !shadowVisible)
                    {
                        result.RestViolations++;
                        if (result.RestDetails.Count < 12)
                            result.RestDetails.Add(DescribeHolder(i, holder, card, highlightVisible, shadowVisible,
                                "rest(reduced)"));
                    }
                }
                else if (!highlightVisible || !shadowVisible)
                {
                    result.RestUntouchedHidden++;
                    if (result.UntouchedHiddenDetails.Count < 12)
                        result.UntouchedHiddenDetails.Add(DescribeHolder(i, holder, card, highlightVisible, shadowVisible,
                            "rest(untouched)"));
                }
            }
        }

        /// <summary>
        /// One violation's evidence: holder position in the container, card id/node, frames since the draw window opened
        /// and since the watch last saw motion, each node's visibility, the highlight's shader width and colour, the
        /// setting's value now, whether the game says the card can be played, and whether it is the card an action just
        /// played/cancelled or a drawn card.
        /// </summary>
        private string DescribeHolder(int index, NHandCardHolder holder, NCard? card, bool highlightVisible,
            bool shadowVisible, string context)
        {
            string cardId = holder.CardModel?.Id.Entry ?? card?.Model?.Id.Entry ?? "?";
            string width = "none";
            string modulate = "none";
            if (card is not null && GodotObject.IsInstanceValid(card.CardHighlight))
            {
                modulate = card.CardHighlight.Modulate.ToHtml();
                try
                {
                    if (card.CardHighlight.Material is ShaderMaterial mat)
                        width = Num(mat.GetShaderParameter("width").AsSingle(), 3);
                }
                catch (Exception)
                {
                    width = "error";
                }
            }
            string playable;
            try
            {
                playable = holder.CardModel?.CanPlay() == true ? "yes" : "no";
            }
            catch (Exception)
            {
                playable = "error";
            }
            string role = ReferenceEquals(holder.CardModel, _lastPlayedCard) ? "last-played"
                : ReferenceEquals(holder.CardModel, _lastCancelledCard) ? "last-cancelled" : "drawn/new";
            ulong now = Engine.GetProcessFrames();
            long drawDelta = _rwDrawStartFrame == 0 ? -1 : (long)now - (long)_rwDrawStartFrame;
            long motionDelta = _rwLastMotionFrame == 0 ? -1 : (long)now - (long)_rwLastMotionFrame;
            return $"{context} holder#{index} card={cardId} node={(card is null ? 0 : card.GetInstanceId())} " +
                   $"draw+{drawDelta}f motion_ago={motionDelta}f highlight.Visible={highlightVisible} " +
                   $"shadow.Visible={shadowVisible} width={width} modulate={modulate} " +
                   $"reduce={PortraitHandLayout.ReduceEffects} playable={playable} role={role}";
        }

        /// <summary>The hand's holder container, cached per hand node: looking the node path up every frame would allocate.</summary>
        private bool TryContainer(out Control? container)
        {
            container = _cardContainer;
            if (container is not null && GodotObject.IsInstanceValid(container)) return true;
            _cardContainer = null;
            NCombatRoom? room = NCombatRoom.Instance;
            if (room is null || !GodotObject.IsInstanceValid(room)) return false;
            NPlayerHand? hand = room.Ui?.Hand;
            if (hand is null || !GodotObject.IsInstanceValid(hand) || !hand.IsInsideTree()) return false;
            ulong handId = hand.GetInstanceId();
            if (_cardContainerHand != handId)
            {
                _cardContainerHand = handId;
                _cardContainer = hand.GetNodeOrNull<Control>("%CardHolderContainer")
                                 ?? hand.GetNodeOrNull<Control>("CardHolderContainer");
            }
            container = _cardContainer;
            return container is not null && GodotObject.IsInstanceValid(container);
        }

        private string Describe(Stats stats, bool partial)
        {
            var text = new StringBuilder(768);
            text.Append("frames=").Append(stats.Frames)
                .Append(" duration_ms=").Append(stats.DurationMs)
                .Append(" cards=").Append(stats.FirstCards).Append("->").Append(stats.LastCards);
            if (partial) text.Append(" partial=1");
            text.Append(" frame_ms=").Append(Num(stats.MeanFrameMs, 2)).Append('/').Append(Num(stats.FrameMax, 2))
                .Append("(wall delta)");
            if (stats.MeanFrameMs > 0.001) text.Append(" fps=").Append(Num(1000.0 / stats.MeanFrameMs, 1));
            text.Append(" our_sum_ms=").Append(Num(stats.MeanTopMs, 3)).Append('/').Append(Num(stats.TopMax, 3));

            text.Append(" mon=");
            for (int i = 0; i < MonitorCount; i++)
            {
                if (i > 0) text.Append(' ');
                text.Append(MonitorNames[i]).Append('=');
                switch (i)
                {
                    case 0:
                    case 1:
                        text.Append(Num(stats.MonitorMean(i) * 1000.0, 2)).Append('/')
                            .Append(Num(stats.MonitorMax(i) * 1000.0, 2));
                        break;
                    case 8:
                        text.Append(Num(stats.MonitorMean(i) / (1024.0 * 1024.0), 1)).Append('/')
                            .Append(Num(stats.MonitorMax(i) / (1024.0 * 1024.0), 1));
                        break;
                    case 9:
                        // Engine.MaxFps: min / mean / max as applied over the window.
                        text.Append(Num(stats.MonitorMin(i), 0)).Append('/').Append(Num(stats.MonitorMean(i), 1))
                            .Append('/').Append(Num(stats.MonitorMax(i), 0));
                        break;
                    case 10:
                    case 11:
                    case 12:
                        text.Append(Num(stats.MonitorMean(i), 2)).Append('/').Append(Num(stats.MonitorMax(i), 2));
                        break;
                    default:
                        text.Append(Num(stats.MonitorMean(i), 1)).Append('/').Append(Num(stats.MonitorMax(i), 1));
                        break;
                }
            }

            for (int pass = 0; pass < PortraitFrameTiming.PassCount; pass++)
            {
                if (stats.PassRuns[pass] == 0) continue;
                text.Append(' ').Append(PortraitFrameTiming.Names[pass]).Append('=')
                    .Append(Num(stats.PassMean(pass), 2)).Append('/').Append(Num(stats.PassWorst(pass), 2))
                    .Append('@').Append(stats.PassRuns[pass]);
            }
            text.Append(" anim_frames=").Append(stats.AnimatingFrames)
                .Append(" cap30_anim=").Append(stats.MaxFpsIdleFrames)
                .Append(" cap30_all=").Append(stats.IdleCapFrames)
                .Append(" gc_frames=").Append(stats.GcFrames)
                .Append(" worst_gc_ms=").Append(Num(stats.WorstGcTop, 2))
                .Append(" worst_nongc_ms=").Append(Num(stats.WorstNonGcTop, 2))
                .Append(" gc_collections=").Append(stats.GcCollections)
                .Append(" gc_collections_max=").Append(stats.GcCollectionsMax)
                .Append(" gc=").Append(stats.Gc0).Append('/').Append(stats.Gc1).Append('/').Append(stats.Gc2)
                .Append(" alloc_kb=").Append(Num(stats.AllocKilobytes, 1))
                .Append(" alloc_b_per_frame=").Append(Num(stats.AllocBytesPerFrame, 0));
            return text.ToString();
        }

        /// <summary>One window's numbers. Arrays are allocated with the window, never per frame.</summary>
        public sealed class Stats
        {
            public readonly double[] PassSum = new double[PortraitFrameTiming.PassCount];
            public readonly double[] PassMax = new double[PortraitFrameTiming.PassCount];
            public readonly int[] PassRuns = new int[PortraitFrameTiming.PassCount];
            private readonly double[] _monitorSum = new double[MonitorCount];
            private readonly double[] _monitorMax = new double[MonitorCount];
            private readonly double[] _monitorMin = new double[MonitorCount];

            public Stats(string label) => Label = label;

            public string Label { get; }
            public long StartMs { get; private set; }
            public long DurationMs { get; private set; }
            public long Frames { get; private set; }
            public double FrameSum { get; private set; }
            public double FrameMax { get; private set; }
            public double TopSum { get; private set; }
            public double TopMax { get; private set; }
            public int FirstCards { get; private set; }
            public int LastCards { get; private set; }
            public long AllocBytes { get; private set; }
            public int Gc0 { get; private set; }
            public int Gc1 { get; private set; }
            public int Gc2 { get; private set; }
            /// <summary>Frames whose Engine.MaxFps was the idle cap (or lower), animating or not.</summary>
            public long IdleCapFrames { get; private set; }
            /// <summary>Frames the probe classified as animating (actions disabled, or a hand card moved since the last frame).</summary>
            public long AnimatingFrames { get; private set; }
            /// <summary>Animating frames whose Engine.MaxFps was the idle cap: the gate's count.</summary>
            public long MaxFpsIdleFrames { get; private set; }
            /// <summary>Frames where a GC ran inside one of our passes (excluded from the worst-frame budget).</summary>
            public long GcFrames { get; private set; }
            public long NonGcFrames { get; private set; }
            public double WorstNonGcTop { get; private set; }
            public double WorstGcTop { get; private set; }
            public long GcCollections { get; private set; }
            public int GcCollectionsMax { get; private set; }
            private long _allocStart;
            private int _gc0Start, _gc1Start, _gc2Start;

            public double MeanFrameMs => Frames > 0 ? FrameSum / Frames : 0.0;
            public double MeanTopMs => Frames > 0 ? TopSum / Frames : 0.0;
            public double MeanDrawCalls => MonitorMean(2);
            public double AllocKilobytes => AllocBytes / 1024.0;
            public double AllocBytesPerFrame => Frames > 0 ? AllocBytes / (double)Frames : 0.0;

            public double MonitorMean(int index) => Frames > 0 ? _monitorSum[index] / Frames : 0.0;
            public double MonitorMax(int index) => _monitorMax[index];
            public double MonitorMin(int index) => Frames > 0 ? _monitorMin[index] : 0.0;
            public double PassMean(int pass) => PassRuns[pass] > 0 ? PassSum[pass] / PassRuns[pass] : 0.0;
            public double PassWorst(int pass) => PassMax[pass];

            public void Open(long nowMs, long allocBytes, int g0, int g1, int g2, int cards)
            {
                StartMs = nowMs;
                _allocStart = allocBytes;
                _gc0Start = g0;
                _gc1Start = g1;
                _gc2Start = g2;
                FirstCards = cards;
                LastCards = cards;
            }

            public void Add(double frameMs, ReadOnlySpan<double> passes, ReadOnlySpan<double> monitors,
                bool animating, bool gcInPass, int collections)
            {
                Frames++;
                FrameSum += frameMs;
                if (frameMs > FrameMax) FrameMax = frameMs;

                double top = 0;
                for (int i = 0; i < TopLevelPasses.Length; i++) top += passes[TopLevelPasses[i]];
                TopSum += top;
                if (top > TopMax) TopMax = top;
                if (animating) AnimatingFrames++;

                // A GC pause inside a pass is not our layout cost: the worst-frame budget only judges non-GC frames, while
                // the mean still covers every frame (as before). Both sides are reported.
                if (gcInPass)
                {
                    GcFrames++;
                    if (top > WorstGcTop) WorstGcTop = top;
                    GcCollections += collections;
                    if (collections > GcCollectionsMax) GcCollectionsMax = collections;
                }
                else
                {
                    NonGcFrames++;
                    if (top > WorstNonGcTop) WorstNonGcTop = top;
                }

                for (int pass = 0; pass < PassSum.Length; pass++)
                {
                    double value = passes[pass];
                    if (value <= 0.0) continue;
                    PassSum[pass] += value;
                    if (value > PassMax[pass]) PassMax[pass] = value;
                    PassRuns[pass]++;
                }
                for (int i = 0; i < MonitorCount; i++)
                {
                    double value = monitors[i];
                    _monitorSum[i] += value;
                    if (value > _monitorMax[i]) _monitorMax[i] = value;
                    if (Frames == 1 || value < _monitorMin[i]) _monitorMin[i] = value;
                }
                // Engine.MaxFps is the launcher's governor output: 30 is its idle cap (FrameRateGovernor.IdleFrameRate).
                bool idleCapped = monitors[9] > 0.0 && monitors[9] <= FrameRateGovernor.IdleFrameRate;
                if (idleCapped) IdleCapFrames++;
                if (idleCapped && animating) MaxFpsIdleFrames++;
            }

            public void Close(long nowMs, long allocBytes, int g0, int g1, int g2, int cards)
            {
                DurationMs = Math.Max(0L, nowMs - StartMs);
                AllocBytes = Math.Max(0L, allocBytes - _allocStart);
                Gc0 = g0 - _gc0Start;
                Gc1 = g1 - _gc1Start;
                Gc2 = g2 - _gc2Start;
                LastCards = cards;
            }
        }
    }

    // ---- the game log watcher ------------------------------------------------------------------------------------------

    /// <summary>
    /// Listens to the game's global log callback (<c>MegaCrit.Sts2.Core.Logging.Log.LogCallback</c>, which every
    /// <c>Logger</c> forwards to) and keeps the "Asset not cached" warnings with their timestamps. Asset loads happen on
    /// several threads, hence the lock; timestamps come from <see cref="PerfClock"/>, not the engine, so the handler never
    /// touches a Godot object off the main thread.
    /// </summary>
    private sealed class CombatAssetWatcher
    {
        private const string Marker = "Asset not cached: ";
        private readonly object _lock = new();
        private readonly List<(long AtMs, string Asset)> _events = new();
        private bool _attached;

        public void Attach()
        {
            if (_attached) return;
            GameLog.LogCallback += OnGameLog;
            _attached = true;
        }

        public void Detach()
        {
            if (!_attached) return;
            GameLog.LogCallback -= OnGameLog;
            _attached = false;
        }

        private void OnGameLog(LogLevel level, string text, int skipFrames)
        {
            if (text is null || !text.Contains(Marker, StringComparison.Ordinal)) return;
            int at = text.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length;
            string asset = text[at..].Trim();
            lock (_lock) _events.Add((PerfClock.NowMs, asset));
        }

        public int Count(long fromMs, long toMs)
        {
            int count = 0;
            lock (_lock)
                foreach ((long at, _) in _events)
                    if (at >= fromMs && at <= toMs) count++;
            return count;
        }

        public (int Count, List<string> Unique) Between(long fromMs, long toMs)
        {
            int count = 0;
            var unique = new List<string>();
            lock (_lock)
            {
                foreach ((long at, string asset) in _events)
                {
                    if (at < fromMs || at > toMs) continue;
                    count++;
                    if (!unique.Contains(asset)) unique.Add(asset);
                }
            }
            return (count, unique);
        }
    }

    private static string Num(double value, int decimals = 2) =>
        value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
