using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// <c>touch-combat</c>: the launcher host's portrait touch contract, checked with real injected finger input (VirtualPointer
/// in touch mode) rather than by reading layout state. A Standard Ironclad run enters a multi-enemy fight; the hand is set to
/// five cards and then ten. The checks are the Batch A touch items:
/// <list type="bullet">
/// <item>a quick tap does nothing; a stationary hold of <see cref="TouchCardMath.HoldSeconds"/> opens the native inspector,
/// and a tap dismisses it;</item>
/// <item>a wiggle smaller than <see cref="TouchCardMath.PickupSlop"/> does not start card play, while a drag past it does; the
/// pressed card's drawn scale grows to the hand's held scale (<c>HeldCardScaleFor</c>) while its holder and hitbox stay put,
/// and the rest of the hand stays put;</item>
/// <item>an untargeted drag keeps the card's bottom edge <see cref="TouchCardMath.DragLift"/> above the finger at its native
/// opacity, while a targeted card that is aiming parks fully opaque over the rail's End Turn
/// button at the per-shape aim size (<c>AimedCardScaleFor(bands)</c>, the same for every hand count) and stays inside the
/// canvas;</item>
/// <item>a self-target skill released below the play zone goes back to the hand and leaves no click-to-aim selection; a
/// targeted attack released just outside an enemy lands on it within <see cref="TouchCardMath.TargetPadding"/>, and a near
/// miss within <see cref="TouchCardMath.TargetSnap"/> does too, while a release far from every enemy cancels;</item>
/// <item>End Turn is clear of the hand and tappable at five and ten cards, and a back-row card of a ten-card hand can be
/// dragged and played.</item>
/// </list>
/// This is a partial of <see cref="TestDriver"/> so it can use the shared run start, combat entry, waits and screenshots.
/// </summary>
internal sealed partial class TestDriver
{
    private const string StrikeId = "STRIKE_IRONCLAD";
    private const string DefendId = "DEFEND_IRONCLAD";
    private const string BashId = "BASH";

    // The aim presentation is one size per shape, not per hand size. Keep the drawn sizes from the five- and ten-card
    // aiming moments so they can be compared once both exist.
    private float? _aimedScaleFiveCards;
    private float? _aimedScaleTenCards;
    private bool _aimScaleCrossCheckReported;

    /// <summary>A check or screenshot name with an optional pass suffix (the flat-hand pass uses <c>flat</c>).</summary>
    private static string WithSuffix(string name, string suffix) => suffix.Length == 0 ? name : name + "_" + suffix;

    /// <summary>Called by <c>LauncherHostRunner</c> for <c>--sts2mobile-test=touch-combat</c>.</summary>
    internal static async Task RunLauncherTouchCombatAsync(TestOptions options, TestReport report, ScreenshotService screenshots,
        VirtualPointer pointer)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Log.Error("Launcher touch-combat requires a SceneTree.");
            return;
        }
        await new TestDriver(tree, options, report, screenshots, pointer).RunLauncherTouchCombatInternalAsync();
    }

    private async Task RunLauncherTouchCombatInternalAsync()
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            _report.Fail("touch-combat_portrait", $"touch-combat needs a portrait window; window={DisplayServer.WindowGetSize()} " +
                                                  $"canvas={GetViewportSize()}");
            return;
        }
        await WaitForMainMenuPreloadAsync();

        RunState state = await StartIroncladRunAsync();
        Player player = LocalContext.GetMe(state) ?? throw new InvalidOperationException("No local player in the run.");
        // The turn tests have to survive enemy attacks; keep the player out of the death path.
        player.Creature.SetMaxHpInternal(9999);
        player.Creature.SetCurrentHpInternal(9999);

        NCombatRoom room = await EnterTouchCombatAsync(state);
        NPlayerHand hand = room.Ui.Hand;
        await SettleInspectScreenAsync();
        _report.Check("touch-combat_play_mode", hand.CurrentMode == NPlayerHand.Mode.Play, $"hand mode={hand.CurrentMode}");

        // ---- five cards: tap, hold, growth, pickup slop, drag lift ----
        await SetHandAsync(player, BashId, StrikeId, DefendId, DefendId, DefendId);
        await SetEnergyAsync(player, 9m);
        await ParkPointerAsync();
        await CaptureAsync("touch-combat_01_five_cards");
        _report.Check("touch-combat_five_cards", hand.ActiveHolders.Count == 5, $"holders={hand.ActiveHolders.Count}");

        NHandCardHolder rightmost = RightmostHolder(hand);
        await TestQuickTapAsync(hand, player, rightmost);
        await TestStationaryHoldAsync(hand, player);
        await CheckPressedCardTipAsync(hand);

        await TestPressedGrowthAsync(hand);
        await TestDragPastSlopAsync(hand, player);
        await TestSlowDragPickupAsync(hand, player);
        await TestSelfTargetReturnsAsync(hand, player);
        await TestReadableDragAsync(room, hand, player);

        // ---- five cards: target padding, near-miss snap, far release ----
        await ResetGestureAsync(hand);
        await SetHandAsync(player, DefendId, DefendId, DefendId, StrikeId, StrikeId);
        await SetEnergyAsync(player, 9m);
        await ParkPointerAsync();
        await TestTargetingAsync(room, hand, player);
        await ResetGestureAsync(hand);
        await SetHandAsync(player, DefendId, DefendId, DefendId, StrikeId, StrikeId);
        await SetEnergyAsync(player, 9m);
        await ParkPointerAsync();
        await TestSkillReleaseOpensNoTipsAsync(room, hand, player);

        // ---- five cards: End Turn is clear of the hand and works ----
        await ResetGestureAsync(hand);
        await SetHandAsync(player, StrikeId, DefendId, StrikeId, DefendId, BashId);
        await SetEnergyAsync(player, 9m);
        await ParkPointerAsync();
        await CaptureAsync("touch-combat_10_five_cards_end_turn");
        bool ended = await TapEndTurnAsync(room, hand, "five");
        if (!ended)
        {
            _report.Fail("touch-combat_five_end_turn_stops", "the five-card End Turn did not start the enemy turn; " +
                "the remaining ten-card checks were skipped instead of idling to the broker timeout.");
            return;
        }
        if (!await WaitForNextPlayerTurnAsync("five")) return;
        await WaitUntilStableAsync(() => IsPlayerPlayPhase()
                ? SettledRoomSignature(includeHealthBars: false, includeIntentFade: false)
                : null,
            "touch-combat five next turn", TimeSpan.FromSeconds(1.5));

        // ---- ten cards: two rows, back-row drag, End Turn ----
        hand = GetHand();
        await ResetGestureAsync(hand);
        await SetHandAsync(player, StrikeId, DefendId, DefendId, StrikeId, DefendId,
            StrikeId, DefendId, StrikeId, DefendId, StrikeId);
        await SetEnergyAsync(player, 9m);
        await ParkPointerAsync();
        await WaitFramesAsync(30);
        await CaptureAsync("touch-combat_11_ten_cards");
        _report.Check("touch-combat_ten_cards", hand.ActiveHolders.Count == 10, $"holders={hand.ActiveHolders.Count}");
        List<float> rowY = hand.ActiveHolders.Select(h => h.Hitbox.GetGlobalRect().GetCenter().Y).ToList();
        _report.Check("touch-combat_ten_cards_two_rows",
            rowY.Count >= 2 && rowY.Max() - rowY.Min() > 150f,
            $"rows={rowY.Count(y => Mathf.Abs(y - rowY.Min()) < 60f)}+{rowY.Count(y => Mathf.Abs(y - rowY.Max()) < 60f)} " +
            $"y={string.Join(",", rowY.Select(y => y.ToString("F0")))}");

        await TestBackRowDragOnUnblockedAsync(room, hand);
        ReportAimScaleConsistency();

        // Put the tenth card back so the End Turn check runs with a full ten-card hand.
        if (hand.ActiveHolders.Count == 9)
        {
            await AddCardAsync(player, DefendId);
            await WaitUntilAsync(() => GetHand().ActiveHolders.Count == 10, "ten cards again", ShortWait);
            await WaitUntilStableAsync(CurrentHandSettledSignature, "touch-combat ten cards again",
                TimeSpan.FromSeconds(1.2));
        }
        await SetEnergyAsync(player, 9m);
        await ParkPointerAsync();
        await CaptureAsync("touch-combat_13_ten_cards_end_turn");
        bool tenEnded = await TapEndTurnAsync(room, GetHand(), "ten");
        if (!tenEnded) return;
        if (!await WaitForNextPlayerTurnAsync("ten")) return;
        await RunFlatHandPassAsync(room, player);
        await RunAimCancelPassesAsync(room, player);
        await RunBackRowFightAsync(player);
        _report.Pass("touch-combat_complete", "all launcher-host touch-combat steps ran");
    }

    /// <summary>
    /// A Standard run as the Ironclad. <see cref="StartRunAsync"/> uses the random character the Standard launcher picks, while
    /// these checks need a known deck (Strike/Defend/Bash), so this mirrors the harness run start with the character fixture
    /// the orbs scenarios use for the Defect.
    /// </summary>
    private async Task<RunState> StartIroncladRunAsync()
    {
        NMainMenu menu = NGame.Instance?.MainMenu ?? throw new InvalidOperationException("Main menu disappeared before run start.");
        SaveManager.Instance.PrefsSave.FastMode = FastModeType.Fast;
        SaveManager.Instance.PrefsSave.IsLongPressEnabled = false;
        SaveManager.Instance.SetFtuesEnabled(false);
        if (AutoSlayer.IsActive) throw new InvalidOperationException("AutoSlay is active; launch the test without --autoslay.");
        NGame.Instance!.DebugSeedOverride = _options.Seed;

        Log.Info("[TEST] starting a Standard Ironclad run through the character select");
        await LaunchCharacterFixtureAsync<MegaCrit.Sts2.Core.Models.Characters.Ironclad>(menu);
        await WaitUntilAsync(
            () => RunManager.Instance.DebugOnlyGetState() is not null && NGame.Instance?.CurrentRunNode is not null,
            "run state", LongWait);
        RunState state = RunManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Run state was null after starting the run.");

        // StandardRunLauncher returns as soon as Embark is clicked; the act map and NRun scene are still initializing.
        await WaitUntilAsync(
            () => NRun.Instance is { } run && GodotObject.IsInstanceValid(run) && run.IsInsideTree()
                  && run.GlobalUi is not null && run.GetNodeOrNull<Control>("%RoomContainer") is not null,
            "run scene initialization", MediumWait);
        await WaitSecondsAsync(2);
        await WaitFramesAsync(4);

        // The Standard run draws a random seed (DebugSeedOverride is not consumed by the character-select embark), so act
        // 1 is Overgrowth or Underdocks from run to run and the accidental encounter changes with it. Pin the first act so
        // every shape and every run below fights the same encounters on the same backdrop. Native runs expect act indices
        // 0..2; ConfigureTourActAsync builds that three-act list, as the stage scenarios already do on this host.
        await ConfigureTourActAsync(state, "Overgrowth");
        if (state.Act.GetType().Name != "Overgrowth")
            throw new InvalidOperationException(
                $"touch-combat needs act 1 pinned to Overgrowth; the run is on {state.Act.GetType().Name}.");
        await WaitFramesAsync(4);

        _report.Pass("run_started",
            $"seed={state.Rng.StringSeed} character=Ironclad act={state.Act.GetType().Name}");
        _report.Phase("run_started",
            $"seed={state.Rng.StringSeed} character=Ironclad act={state.Act.GetType().Name}");
        return state;
    }

    /// <summary>
    /// Enters the fixed NibbitsNormal encounter (two small enemies, one row at both shapes), so every shape and every run
    /// sees the same main fight. The act is pinned to Overgrowth, where NibbitsNormal is native, and the encounter is
    /// looked up by type; a missing or too-small fight throws instead of falling back to another encounter. The prefix
    /// names the host-scenario step that records the fight (<c>touch-combat</c> or <c>perf-combat</c>).
    /// </summary>
    private async Task<NCombatRoom> EnterTouchCombatAsync(RunState state, string prefix = "touch-combat")
    {
        if (state.Act.GetType().Name != "Overgrowth")
            throw new InvalidOperationException(
                $"The {prefix} fight expects Overgrowth; the run is on {state.Act.GetType().Name}.");
        EncounterModel encounter = ModelDb.Encounter<NibbitsNormal>().ToMutable();
        encounter.GenerateMonstersWithSlots(state);
        int generated = encounter.MonstersWithSlots.Count;
        if (generated < 2)
            throw new InvalidOperationException(
                $"NibbitsNormal generated {generated} monsters; the {prefix} checks need at least two enemies.");

        Log.Info($"[TEST] entering fixed multi-enemy encounter {encounter.GetType().Name} " +
                 $"({generated} monsters, act {state.Act.GetType().Name})");
        await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: encounter, showTransition: false);
        await WaitUntilAsync(IsPlayerPlayPhase, "NibbitsNormal player phase", MediumWait);
        await WaitUntilAsync(() => GetHand().ActiveHolders.Count > 0, "NibbitsNormal combat hand", ShortWait);
        _report.Phase("combat_entered",
            $"act={state.Act.GetType().Name} encounter={encounter.GetType().Name} monsters={generated}");
        // The old 1.5 s settle let the room, hand and entry animations arrive; wait for that state instead. The touch
        // checks read cards and creatures, not the room's health bars or intent fade, so neither is waited for.
        await WaitUntilStableAsync(() => IsPlayerPlayPhase()
                ? SettledRoomSignature(includeHealthBars: false, includeIntentFade: false)
                : null,
            $"{prefix} intro", TimeSpan.FromSeconds(1.5));

        NCombatRoom room = NCombatRoom.Instance ?? throw new InvalidOperationException("Combat room disappeared.");
        int enemies = room.CreatureNodes.Count(c => GodotObject.IsInstanceValid(c) && !c.Entity.IsPlayer && c.Entity.IsAlive);
        _report.Check($"{prefix}_multi_enemy", enemies >= 2,
            $"act={state.Act.GetType().Name} encounter={encounter.GetType().Name} living enemies={enemies}");
        if (enemies < 2)
            throw new InvalidOperationException(
                $"NibbitsNormal entered with {enemies} living enemies; the {prefix} checks need at least two.");
        return room;
    }

    /// <summary>
    /// A second fixed fight: four Exoskeletons (fixed slots first..fourth). At 9:19.5 the launcher stages them as two front
    /// and two back-row enemies (front feet ~1324, back ~916-935); at 9:16 they form the one crowd row. The NibbitsNormal
    /// fight above is one row at both shapes, so this pass keeps the tall shape's back-row enemy target coverage (padding
    /// and snap releases aimed across the front row) deterministic instead of leaving it to whichever act the run drew.
    /// (Corpse Slugs used to serve here, but a short trio is one row on both shapes now.) The act stays Overgrowth, so the
    /// backdrop/scene is the same valid act scene at both shapes; the fight, its four enemies and the shape's formation are
    /// asserted, and a mismatch throws instead of continuing.
    /// </summary>
    private async Task RunBackRowFightAsync(Player player)
    {
        const int FightSize = 4;
        RunState state = RunManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("No run state for the back-row fight.");
        EncounterModel encounter = ModelDb.Encounter<ExoskeletonsNormal>().ToMutable();
        encounter.GenerateMonstersWithSlots(state);
        int generated = encounter.MonstersWithSlots.Count;
        if (generated != FightSize)
            throw new InvalidOperationException(
                $"ExoskeletonsNormal generated {generated} monsters; the back-row fight needs exactly {FightSize}.");

        Log.Info($"[TEST] entering fixed back-row encounter {encounter.GetType().Name} " +
                 $"({generated} monsters, act {state.Act.GetType().Name})");
        await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: encounter, showTransition: false);
        await WaitUntilAsync(IsPlayerPlayPhase, "back-row fight player phase", MediumWait);
        await WaitUntilAsync(() => GetHand().ActiveHolders.Count > 0, "back-row fight hand", ShortWait);
        // A new combat restores the player from the run; keep it out of the death path for this fight too.
        player.Creature.SetMaxHpInternal(9999);
        player.Creature.SetCurrentHpInternal(9999);
        await WaitUntilStableAsync(() => IsPlayerPlayPhase()
                ? SettledRoomSignature(includeHealthBars: false, includeIntentFade: false)
                : null,
            "back-row fight intro", TimeSpan.FromSeconds(1.5));

        NCombatRoom room = NCombatRoom.Instance
            ?? throw new InvalidOperationException("The back-row fight combat room disappeared.");
        PortraitCombatBands bands = LauncherBands();
        List<NCreature> enemies = LivingEnemies(room);
        List<float> feet = enemies.Select(e => e.Hitbox.GetGlobalRect().End.Y).ToList();
        float spread = feet.Count == 0 ? 0f : feet.Max() - feet.Min();
        string feetText = string.Join(",", feet.Select(y => y.ToString("F1")));
        _report.Phase("back_row_fight_entered",
            $"act={state.Act.GetType().Name} encounter={encounter.GetType().Name} monsters={enemies.Count} " +
            $"compact={bands.Compact} feet=[{feetText}]");
        _report.Check("touch-combat_back_row_fight_enemies", enemies.Count == FightSize,
            $"encounter={encounter.GetType().Name} living enemies={enemies.Count}");
        _report.Check("touch-combat_back_row_fight_formation",
            bands.Compact ? spread <= 8f : spread >= 150f,
            $"compact={bands.Compact} feet=[{feetText}] spread={spread:F1} " +
            "(single row <=8 at 9:16, staggered back row >=150 at 9:19.5)");
        if (enemies.Count != FightSize)
            throw new InvalidOperationException(
                $"ExoskeletonsNormal entered with {enemies.Count} living enemies; the back-row pass needs {FightSize}.");

        // At 9:19.5 target the back-row (highest) enemy explicitly; at 9:16 every enemy is the one row, so take the
        // clearest release available. The points must be outside every other hitbox or the pass fails loudly.
        Vector2 canvas = GetViewportSize();
        NCreature backRow = enemies.OrderBy(e => e.Hitbox.GetGlobalRect().End.Y).First();
        NCreature target = bands.Compact ? PickSnapTarget(enemies, canvas).Enemy : backRow;
        float paddingDistance = TouchCardMath.TargetPadding * 0.5f;
        float snapDistance = TouchCardMath.TargetPadding + (TouchCardMath.TargetSnap - TouchCardMath.TargetPadding) * 0.5f;
        ReleaseCandidate padding = PickTargetRelease(enemies, target, paddingDistance, canvas);
        ReleaseCandidate snap = PickTargetRelease(enemies, target, snapDistance, canvas);
        _report.Check("touch-combat_back_row_fight_back_row_target",
            bands.Compact || Mathf.Abs(target.Hitbox.GetGlobalRect().End.Y - feet.Min()) <= 1f,
            $"compact={bands.Compact} targetFeet={target.Hitbox.GetGlobalRect().End.Y:F1} " +
            $"backRowFeet={feet.Min():F1} feet=[{feetText}]");
        _report.Check("touch-combat_back_row_fight_target_points",
            padding.Clearance > TouchCardMath.TargetPadding && snap.Clearance > TouchCardMath.TargetSnap,
            $"padding={padding.Point} clearance={padding.Clearance:F1} (>39) " +
            $"snap={snap.Point} clearance={snap.Clearance:F1} (>90) compact={bands.Compact}");
        if (padding.Clearance <= TouchCardMath.TargetPadding || snap.Clearance <= TouchCardMath.TargetSnap)
            throw new InvalidOperationException(
                $"The back-row fight has no padding/snap release clear of the other enemies " +
                $"(padding={padding.Clearance:F1} snap={snap.Clearance:F1}).");

        await RaiseHpAsync(target, 500);
        await SetHandAsync(player, StrikeId, DefendId, DefendId, StrikeId, BashId);
        await SetEnergyAsync(player, 9m);
        await ParkPointerAsync();
        await CaptureAsync("touch-combat_14_back_row_fight");
        await TestTargetedReleaseAsync(GetHand(), target, padding.Point, "second_padding",
            $"padding {padding.Distance:F1} outside the {(bands.Compact ? "single-row" : "back-row")} target " +
            $"(clearance {padding.Clearance:F1} from the others)",
            "touch-combat_14a_second_padding_aiming", "touch-combat_14_second_padding_play");
        await SetEnergyAsync(player, 9m);
        await WaitUntilStableAsync(CurrentHandSettledSignature, "back-row fight between releases",
            TimeSpan.FromSeconds(1.2));
        await TestTargetedReleaseAsync(GetHand(), target, snap.Point, "second_snap",
            $"a near miss {snap.Distance:F1} outside the {(bands.Compact ? "single-row" : "back-row")} target " +
            $"(clearance {snap.Clearance:F1} from the others)",
            "touch-combat_15a_second_snap_aiming", "touch-combat_15_second_snap_play");
        _report.Pass("touch-combat_back_row_fight_complete",
            $"four Exoskeletons, compact={bands.Compact}, back-row target releases ran");
    }

    /// <summary>
    /// The aim-cancel passes: a five-card hand (three upper, two lower), a Strike in each row; each pass presses that
    /// row's Strike, drags to a living enemy (the card aims), drags back down into the hand and releases, so the play is
    /// cancelled. The bug is on the release's drawn frame: the other cards can drop ~100 units and come back for one
    /// frame. Four releases are the plain hand-band drop; four are released exactly on another card's centre (aimed lower
    /// onto an upper card, aimed upper onto another upper card) because the user saw the big snap when the release left
    /// the pointer over another card. Every one runs normal and slow (Engine.MaxFps=30 plus a 120 ms hitch on the release
    /// frame), and the slow passes hold the aim 90 frames so the hand pass goes idle and HandStillness's record is stale.
    /// </summary>
    private async Task RunAimCancelPassesAsync(NCombatRoom room, Player player)
    {
        NPlayerHand hand = GetHand();
        await ResetGestureAsync(hand);
        await SetHandAsync(player, StrikeId, DefendId, DefendId, StrikeId, DefendId);
        await SetEnergyAsync(player, 9m);
        await ParkPointerAsync();
        await WaitFramesAsync(10);
        await RunAimCancelPassAsync(room, player, lowerRow: true, slow: false, CancelReleaseTarget.HandBand);
        await RunAimCancelPassAsync(room, player, lowerRow: false, slow: false, CancelReleaseTarget.HandBand);
        await RunAimCancelPassAsync(room, player, lowerRow: true, slow: false, CancelReleaseTarget.OverUpperCard);
        await RunAimCancelPassAsync(room, player, lowerRow: false, slow: false, CancelReleaseTarget.OverUpperCard);
        await RunAimCancelPassAsync(room, player, lowerRow: true, slow: true, CancelReleaseTarget.HandBand);
        await RunAimCancelPassAsync(room, player, lowerRow: false, slow: true, CancelReleaseTarget.HandBand);
        await RunAimCancelPassAsync(room, player, lowerRow: true, slow: true, CancelReleaseTarget.OverUpperCard);
        await RunAimCancelPassAsync(room, player, lowerRow: false, slow: true, CancelReleaseTarget.OverUpperCard);
    }

    /// <summary>Where the aimed card is released: the plain hand band, or exactly on another upper-row card's centre.</summary>
    private enum CancelReleaseTarget
    {
        HandBand,
        OverUpperCard,
    }

    private async Task RunAimCancelPassAsync(NCombatRoom room, Player player, bool lowerRow, bool slow,
        CancelReleaseTarget releaseTarget)
    {
        string rowName = lowerRow ? "lower" : "upper";
        string nameBase = releaseTarget == CancelReleaseTarget.OverUpperCard
            ? $"touch-combat_cancel_{rowName}_onto_upper"
            : $"touch-combat_cancel_{rowName}";
        string suffix = slow ? "slow" : "";
        int previousMaxFps = Engine.MaxFps;
        ReleaseHitchNode? hitch = null;
        CancelHandSampler? sampler = null;
        try
        {
            if (slow)
            {
                Engine.MaxFps = 30;
                hitch = new ReleaseHitchNode();
                _tree.Root.AddChild(hitch);
            }

            NPlayerHand hand = GetHand();
            await ResetGestureAsync(hand);
            if (hand.ActiveHolders.Count != 5)
            {
                await SetHandAsync(player, StrikeId, DefendId, DefendId, StrikeId, DefendId);
                hand = GetHand();
            }
            await SetEnergyAsync(player, 9m);
            await ParkPointerAsync();

            List<NHandCardHolder> holders = hand.ActiveHolders
                .Where(holder => GodotObject.IsInstanceValid(holder))
                .ToList();
            List<NHandCardHolder> upper = UpperRow(holders);
            List<NHandCardHolder> lower = LowerRow(holders);
            List<NHandCardHolder> row = lowerRow ? lower : upper;
            float rowSpread = holders.Count == 0
                ? 0f
                : holders.Max(h => h.GlobalPosition.Y) - holders.Min(h => h.GlobalPosition.Y);
            _report.Check(WithSuffix($"{nameBase}_hand_two_rows", suffix),
                upper.Count > 0 && lower.Count > 0 && rowSpread > 150f,
                $"upper={upper.Count} lower={lower.Count} rowSpread={rowSpread:F1} holders={holders.Count}");
            NHandCardHolder? card = row.FirstOrDefault(h => IsTargetedPlayable(h)
                && h.CardModel?.Id.Entry == StrikeId);
            _report.Check(WithSuffix($"{nameBase}_hand_target", suffix),
                card is not null,
                $"card={card?.CardModel?.Id.Entry ?? "none"} row={rowName} rowCards={row.Count} " +
                $"cards=[{string.Join(",", row.Select(h => h.CardModel?.Id.Entry ?? "?"))}]");
            if (card is null)
            {
                _report.Fail(WithSuffix($"{nameBase}_hand_still", suffix),
                    $"no targeted Strike in the {rowName} row; the cancel pass cannot run");
                return;
            }

            NHandCardHolder? releaseCard = releaseTarget == CancelReleaseTarget.OverUpperCard
                ? upper.FirstOrDefault(h => h != card)
                : null;
            if (releaseTarget == CancelReleaseTarget.OverUpperCard)
            {
                _report.Check(WithSuffix($"{nameBase}_hand_release_card", suffix), releaseCard is not null,
                    $"releaseCard={releaseCard?.CardModel?.Id.Entry ?? "none"} aimed={card.CardModel?.Id.Entry ?? "?"} " +
                    $"upper=[{string.Join(",", upper.Select(h => h.CardModel?.Id.Entry ?? "?"))}]");
                if (releaseCard is null) return;
            }

            NCreature enemy = FrontEnemy(room);
            await RaiseHpAsync(enemy, 500);
            int energy = player.PlayerCombatState!.Energy;
            int enemyHp = enemy.Entity.CurrentHp;
            NHandCardHolder[] before = hand.ActiveHolders.ToArray();
            Vector2 start = PointOnHolder(card, 0.5f, 0.5f);
            Vector2 lift = start + new Vector2(0f, -TouchCardMath.PickupSlop * 3f);
            Vector2 aim = enemy.Hitbox.GetGlobalRect().GetCenter();
            Vector2 releasePoint = releaseCard is not null
                ? PointOnHolder(releaseCard, 0.5f, 0.5f)
                : new Vector2(GetViewportSize().X * 0.5f, LauncherBands().HandTopY + 140f);

            await _pointer.PressAsync(start);
            await _pointer.MoveAsync(lift, pressed: true);
            bool started = await TryWaitAsync(() => hand.InCardPlay,
                $"{rowName}-row cancel pickup", TimeSpan.FromSeconds(2));
            if (started)
            {
                for (int i = 1; i <= 10; i++)
                {
                    await _pointer.MoveAsync(lift.Lerp(aim, i / 10f), pressed: true);
                    await WaitFramesAsync(1);
                }
                await WaitFramesAsync(2);
            }
            bool aiming = started && IsTargeting();
            if (releaseTarget == CancelReleaseTarget.HandBand)
                await CaptureAsync(WithSuffix($"touch-combat_16_cancel_{rowName}_aim", suffix));
            if (slow)
            {
                // The hand pass otherwise runs at least every HandIdlePassFrames (30). More than two frames without a
                // pass makes HandStillness's record stale; 90 frames at 30 fps is 3 s of idle aim on the release path.
                await WaitFramesAsync(90);
            }

            sampler = new CancelHandSampler(_tree, hand, card);
            sampler.Begin();
            if (started)
            {
                for (int i = 1; i <= 10; i++)
                {
                    await _pointer.MoveAsync(aim.Lerp(releasePoint, i / 10f), pressed: true);
                    await WaitFramesAsync(1);
                }
                await WaitFramesAsync(4);
            }
            hitch?.Arm();
            sampler.MarkRelease(releasePoint);
            await _pointer.ReleaseAsync(releasePoint,
                keepHover: releaseTarget == CancelReleaseTarget.OverUpperCard);
            await sampler.WaitForWindowAsync();
            await WaitUntilStableAsync(CurrentHandSettledSignature,
                $"touch-combat cancel {rowName} hand settle", TimeSpan.FromSeconds(2));
            sampler.CaptureSlots();
            (bool still, string stillDetail) = sampler.Evaluate();

            _report.Check(WithSuffix($"{nameBase}_hand_aimed", suffix),
                aiming, $"pickedUp={started} targeting={IsTargeting()} aim={aim} release={releasePoint} " +
                        $"slow={slow} releaseCard={releaseCard?.CardModel?.Id.Entry ?? "hand band"}");
            _report.Check(WithSuffix($"{nameBase}_hand_cancelled", suffix),
                started && !hand.InCardPlay && player.PlayerCombatState!.Energy == energy
                && enemy.Entity.CurrentHp == enemyHp && SameCards(hand, before),
                $"pickedUp={started} cardPlay={hand.InCardPlay} hand={hand.ActiveHolders.Count}/{before.Length} " +
                $"energy={player.PlayerCombatState!.Energy}/{energy} enemyHp={enemy.Entity.CurrentHp}/{enemyHp} " +
                $"release={releasePoint}");
            _report.Check(WithSuffix($"{nameBase}_hand_still", suffix), started && still,
                started ? stillDetail : "the card never entered play; " + stillDetail);
            if (releaseTarget == CancelReleaseTarget.OverUpperCard)
            {
                _report.Check(WithSuffix($"{nameBase}_hand_pointer_held", suffix),
                    started && sampler.WindowFrames >= 3 && sampler.PointerDeviation <= 2f,
                    $"maxPointerDeviation={sampler.PointerDeviation:F1}u release={releasePoint} " +
                    $"frames={sampler.WindowFrames} hoverMotion=True");
            }
        }
        finally
        {
            sampler?.Cancel();
            if (hitch is not null && GodotObject.IsInstanceValid(hitch))
            {
                hitch.Disarm();
                hitch.QueueFree();
            }
            Engine.MaxFps = previousMaxFps;
        }
    }

    /// <summary>
    /// Drawn-frame sampler for one aim-cancel release. It records every non-aimed holder's hitbox rect at
    /// <c>FramePostDraw</c> (the frame the phone shows), from before the release until the caller has 10 drawn frames
    /// after it. <see cref="Evaluate"/> compares the release window with the settled slots: a card may move more than
    /// 2 units in a frame only while closing its distance to that slot (its own path back); a step away from the slot,
    /// or a card that stops being drawn, fails.
    /// </summary>
    private sealed class CancelHandSampler
    {
        private const float StillStep = 2f;
        private const int WindowBefore = 2;
        private const int WindowAfter = 10;

        private readonly SceneTree _tree;
        private readonly NPlayerHand _hand;
        private readonly List<(ulong Id, string Name, NHandCardHolder Holder)> _tracked = new();
        private readonly List<(long Frame, Dictionary<ulong, Rect2> Rects, int FocusIndex, string FocusCard,
            string PressedCard, Vector2 Mouse, bool HoverFromTouch)> _samples = new();
        private readonly Dictionary<ulong, Rect2> _slots = new();
        private long _frame = -1;
        private long _releaseFrame = -1;
        private Vector2 _releasePoint;
        private bool _running;

        /// <summary>Largest distance between the engine's pointer and the release point across the sampled window.</summary>
        public float PointerDeviation { get; private set; }

        /// <summary>Number of drawn frames inside the release window (-2..+10).</summary>
        public int WindowFrames { get; private set; }

        public CancelHandSampler(SceneTree tree, NPlayerHand hand, NHandCardHolder aimed)
        {
            _tree = tree;
            _hand = hand;
            foreach (NHandCardHolder holder in hand.ActiveHolders)
            {
                if (holder == aimed || !GodotObject.IsInstanceValid(holder)) continue;
                _tracked.Add((holder.GetInstanceId(), holder.CardModel?.Id.Entry ?? "card", holder));
            }
        }

        public void Begin()
        {
            _running = true;
            _frame = -1;
            RenderingServer.FramePostDraw += OnFramePostDraw;
        }

        public void MarkRelease(Vector2 releasePoint)
        {
            _releaseFrame = _frame;
            _releasePoint = releasePoint;
        }

        public void Cancel()
        {
            if (!_running) return;
            RenderingServer.FramePostDraw -= OnFramePostDraw;
            _running = false;
        }

        private void OnFramePostDraw()
        {
            if (!_running) return;
            _frame++;
            var rects = new Dictionary<ulong, Rect2>(_tracked.Count);
            foreach ((ulong id, _, NHandCardHolder holder) in _tracked)
            {
                if (!GodotObject.IsInstanceValid(holder) || !holder.IsInsideTree() || !holder.IsVisibleInTree())
                    continue;
                rects[id] = holder.Hitbox.GetGlobalRect();
            }
            // The one-frame hover/focus the user suspects: the native hand focus and the touch pass's pressed holder,
            // recorded on the same drawn frame as the rects.
            NHandCardHolder? focused = _hand.FocusedHolder;
            int focusIndex = focused is null ? -1 : _hand.ActiveHolders.ToList().IndexOf(focused);
            string focusCard = focused?.CardModel?.Id.Entry ?? "none";
            string pressedCard = PhoneTouchCards.PressedHolder is NHandCardHolder pressed
                ? pressed.CardModel?.Id.Entry ?? "none"
                : "none";
            Vector2 mouse = _tree.Root.GetMousePosition();
            _samples.Add((_frame, rects, focusIndex, focusCard, pressedCard, mouse,
                PhoneTouchCards.HoverFromTouch));
            if (_releaseFrame >= 0 && _frame > _releaseFrame + WindowAfter + 30) Cancel();
        }

        /// <summary>Waits (bounded) for the drawn-frame count to pass the release window.</summary>
        public async Task WaitForWindowAsync()
        {
            double start = Time.GetTicksMsec();
            while (_running && (_releaseFrame < 0 || _frame < _releaseFrame + WindowAfter + 2)
                   && Time.GetTicksMsec() - start < 4000.0)
            {
                await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
            }
            Cancel();
        }

        /// <summary>The settled rect per tracked card, after the hand has returned to its resting layout.</summary>
        public void CaptureSlots()
        {
            _slots.Clear();
            foreach ((ulong id, _, NHandCardHolder holder) in _tracked)
            {
                if (!GodotObject.IsInstanceValid(holder) || !holder.IsInsideTree()) continue;
                _slots[id] = holder.Hitbox.GetGlobalRect();
            }
        }

        public (bool Ok, string Detail) Evaluate()
        {
            if (_releaseFrame < 0)
                return (false, "the release frame was never marked");
            List<(long Frame, Dictionary<ulong, Rect2> Rects, int FocusIndex, string FocusCard, string PressedCard,
                Vector2 Mouse, bool HoverFromTouch)> window = _samples
                    .Where(sample => sample.Frame >= _releaseFrame - WindowBefore
                                     && sample.Frame <= _releaseFrame + WindowAfter)
                    .ToList();
            WindowFrames = window.Count;
            PointerDeviation = 0f;
            foreach ((_, _, _, _, _, Vector2 mouse, _) in window)
                PointerDeviation = MathF.Max(PointerDeviation, mouse.DistanceTo(_releasePoint));
            var violations = new List<string>();
            if (_tracked.Count == 0) violations.Add("no non-aimed cards were tracked");
            if (window.Count < 3) violations.Add($"only {window.Count} drawn frames around the release");
            if (!_samples.Any(sample => sample.Frame >= _releaseFrame + WindowAfter))
                violations.Add(_samples.Count == 0
                    ? "no drawn frames were sampled"
                    : $"the drawn-frame window stopped at +{_samples[^1].Frame - _releaseFrame}");
            float worstStep = 0f;
            string worstCard = "none";
            foreach ((ulong id, string name, _) in _tracked)
            {
                if (!_slots.TryGetValue(id, out Rect2 slotRect))
                {
                    violations.Add($"{name}: not drawn in the hand after the cancel");
                    continue;
                }
                Vector2 slot = slotRect.GetCenter();
                Vector2? previous = null;
                foreach ((long frame, Dictionary<ulong, Rect2> rects, int focusIndex, string focusCard,
                             string pressedCard, Vector2 mouse, bool hoverFromTouch) in window)
                {
                    if (!rects.TryGetValue(id, out Rect2 rect))
                    {
                        violations.Add($"{name}: not drawn at {Relative(frame)} " +
                                       $"focus={focusIndex}:{focusCard}/pressed={pressedCard} " +
                                       $"mouse=({mouse.X:F0},{mouse.Y:F0})");
                        break;
                    }
                    Vector2 now = rect.GetCenter();
                    if (previous is { } prev)
                    {
                        float step = now.DistanceTo(prev);
                        if (step > worstStep)
                        {
                            worstStep = step;
                            worstCard = name;
                        }
                        if (step > StillStep)
                        {
                            // A fast move is allowed only along the card's own path back to its settled slot; an
                            // away-and-back step is the one-frame drop the phone showed.
                            float before = prev.DistanceTo(slot);
                            float after = now.DistanceTo(slot);
                            if (after > before + StillStep)
                                violations.Add(
                                    $"{name} at {Relative(frame)}: moved {step:F1}u away from its slot " +
                                    $"({before:F1}->{after:F1}) focus={focusIndex}:{focusCard}/pressed={pressedCard} " +
                                    $"mouse=({mouse.X:F0},{mouse.Y:F0}) hoverFromTouch={hoverFromTouch}");
                        }
                    }
                    previous = now;
                }
            }
            // Per drawn frame: where the engine's pointer is, the native focused card index/card, the touch pass's
            // pressed card and the touch-hover gate, so a one-frame focus on the release card is visible next to the
            // movement numbers and the pointer is proved to stay at the release point.
            var focusTrace = new List<string>(window.Count);
            foreach ((long frame, _, int focusIndex, string focusCard, string pressedCard, Vector2 mouse,
                         bool hoverFromTouch) in window)
                focusTrace.Add($"{Relative(frame)}:m({mouse.X:F0},{mouse.Y:F0}):" +
                               $"{focusIndex}:{focusCard}/{pressedCard}:h{(hoverFromTouch ? 'T' : 'F')}");
            string detail = $"frames={window.Count} (-{WindowBefore}..+{WindowAfter}) cards={_tracked.Count} " +
                            $"worstStep={worstStep:F1}u card={worstCard} " +
                            $"releaseFrame={(_releaseFrame >= 0 ? _releaseFrame.ToString() : "none")} " +
                            $"pointerDeviation={PointerDeviation:F1}u focus=[{string.Join(",", focusTrace)}] " +
                            (violations.Count == 0 ? "no card left its path" : string.Join("; ", violations.Take(3)));
            return (violations.Count == 0, detail);
        }

        private string Relative(long frame) => $"{(frame - _releaseFrame):+0;-0;0}";
    }

    /// <summary>
    /// Test-only slow device: blocks the next process frame with a 120 ms sleep, so the frame that first draws the
    /// release reflow is as long as on the phone. Same pattern as LauncherHandChecks' SlowDeviceHitchNode, armed per
    /// release instead of per card arrival.
    /// </summary>
    private sealed class ReleaseHitchNode : Node
    {
        private const int HitchMsec = 120;
        private bool _armed;

        public void Arm() => _armed = true;

        public void Disarm() => _armed = false;

        public override void _Process(double delta)
        {
            if (!_armed) return;
            _armed = false;
            OS.DelayMsec(HitchMsec);
        }
    }

    // ---- gestures ----

    /// <summary>A quick tap: one press and release without moving. It must not play the card or open the inspector.</summary>
    private async Task TestQuickTapAsync(NPlayerHand hand, Player player, NHandCardHolder card)
    {
        NHandCardHolder[] before = hand.ActiveHolders.ToArray();
        int energy = player.PlayerCombatState!.Energy;
        int block = player.Creature.Block;
        Vector2 point = PointOnHolder(card, 0.5f, 0.55f);

        await _pointer.TapAsync(point);
        await WaitSecondsAsync(0.35);

        bool inspect = InspectOpen();
        _report.Check("touch-combat_quick_tap_does_nothing",
            !hand.InCardPlay && !inspect && SameCards(hand, before)
            && player.PlayerCombatState!.Energy == energy && player.Creature.Block == block,
            $"cardPlay={hand.InCardPlay} inspect={inspect} hand={hand.ActiveHolders.Count}/{before.Length} " +
            $"energy={player.PlayerCombatState!.Energy}/{energy} block={player.Creature.Block}/{block}");
        await CaptureAsync("touch-combat_02_quick_tap");
    }

    /// <summary>A still finger held past <see cref="TouchCardMath.HoldSeconds"/> opens the native inspector; a tap closes it.</summary>
    private async Task TestStationaryHoldAsync(NPlayerHand hand, Player player)
    {
        NHandCardHolder card = RightmostHolder(hand);
        int energy = player.PlayerCombatState!.Energy;
        NHandCardHolder[] before = hand.ActiveHolders.ToArray();
        Vector2 point = PointOnHolder(card, 0.5f, 0.5f);

        await _pointer.PressAsync(point);
        bool openedWhileDown = await TryWaitAsync(InspectOpen, "card inspector while the finger is still down",
            TimeSpan.FromSeconds(1.6));
        _report.Check("touch-combat_hold_does_not_pick_up", !hand.InCardPlay && SameCards(hand, before),
            $"cardPlay={hand.InCardPlay} hand={hand.ActiveHolders.Count}/{before.Length}");
        if (openedWhileDown) await CaptureAsync("touch-combat_03_hold_inspector");

        await _pointer.ReleaseAsync(point);
        bool opened = openedWhileDown || await TryWaitAsync(InspectOpen, "card inspector after the hold",
            TimeSpan.FromSeconds(1.2));
        _report.Check("touch-combat_hold_opens_inspector", opened,
            $"whileDown={openedWhileDown} afterRelease={InspectOpen()} uptime={(Time.GetTicksMsec() / 1000.0):F2}s");
        await WaitSecondsAsync(0.2);
        _report.Check("touch-combat_inspector_stays_after_release", InspectOpen(), "the release did not close the inspector");

        if (opened && NGame.Instance?.GetInspectCardScreen() is { } screen)
        {
            await WaitForInspectorCardAsync(screen);
            await CaptureAsync("touch-combat_03b_inspector_open");
            CheckInspectorPresentation(hand, screen, "touch-combat");
        }

        await _pointer.TapAsync(new Vector2(60f, 420f));
        bool closed = await TryWaitAsync(() => !InspectOpen(), "inspector dismissed by a tap", TimeSpan.FromSeconds(2.5));
        _report.Check("touch-combat_tap_dismisses_inspector", closed, $"inspect={InspectOpen()}");
        _report.Check("touch-combat_hold_keeps_hand", SameCards(hand, before)
                && !hand.InCardPlay && player.PlayerCombatState!.Energy == energy,
            $"hand={hand.ActiveHolders.Count}/{before.Length} cardPlay={hand.InCardPlay} energy={player.PlayerCombatState!.Energy}/{energy}");
    }

    /// <summary>Waits for the inspector's open tween: its card opaque and drawn and the backstop dimming the fight.</summary>
    private async Task WaitForInspectorCardAsync(Control screen)
    {
        for (int attempt = 0; attempt < 90; attempt++)
        {
            Control? card = screen.GetNodeOrNull<Control>("Card");
            Control? backstop = screen.GetNodeOrNull<Control>("Backstop");
            if (screen.IsVisibleInTree() && card is { } c && c.IsVisibleInTree() && c.Modulate.A >= 0.95f
                && backstop is { } b && b.IsVisibleInTree() && b.Modulate.A >= 0.5f)
                return;
            await WaitFramesAsync(1);
        }
    }

    /// <summary>
    /// The opened card inspector must actually draw its card: a visible, readable (>=500 units tall), opaque card inside the
    /// canvas, and a visible dim behind it. No inspector control (the deck-grid View Upgrades tickbox and label, the arrows)
    /// may overlap the hand rows; the native deck-grid layout used to leave that tickbox floating over the second hand card.
    /// </summary>
    private void CheckInspectorPresentation(NPlayerHand hand, Control screen, string prefix)
    {
        Control? card = screen.GetNodeOrNull<Control>("Card");
        Control? backstop = screen.GetNodeOrNull<Control>("Backstop");
        // The NCard root is a zero-size Control positioned at the drawn card's centre (hence GetGlobalRect() is a point);
        // the drawn card is GetCurrentSize() around that centre. A non-card Control keeps its own rect.
        Rect2 drawnRect;
        Vector2 visualSize;
        if (card is NCard ncard)
        {
            visualSize = ncard.GetCurrentSize();
            drawnRect = new Rect2(ncard.GlobalPosition - visualSize * 0.5f, visualSize);
        }
        else
        {
            drawnRect = card?.GetGlobalRect() ?? default;
            visualSize = drawnRect.Size;
        }
        float height = visualSize.Y;
        bool inside = card is not null && InsideCanvas(drawnRect);
        _report.Check($"{prefix}_inspector_card_drawn",
            card is { } node && screen.IsVisibleInTree() && node.IsVisibleInTree() && node.Modulate.A >= 0.95f
            && inside && height >= 500f,
            $"visible={card?.IsVisibleInTree()} alpha={card?.Modulate.A:F2} rect={drawnRect} centre={card?.GlobalPosition} " +
            $"height={height:F0} inside={inside}");
        bool dimmed = backstop is { } dim && dim.IsVisibleInTree() && dim.Modulate.A >= 0.5f;
        _report.Check($"{prefix}_inspector_dimmed", dimmed,
            $"backstop={(backstop is { } b ? $"visible={b.IsVisibleInTree()} alpha={b.Modulate.A:F2}" : "none")}");

        // The View Upgrades box uses the deck/grid inspection's placement: centred under the card, 44 points tall. The
        // hand rows sit behind the inspector's dim, so the old rule (clear of the hand) is not what the phone wants; what
        // matters is that the box is readable, a finger tall and never over the card.
        Control? upgrade = screen.GetNodeOrNull<Control>("%Upgrade");
        if (upgrade is { } box && box.IsVisibleInTree() && box.Modulate.A >= 0.05f)
        {
            Rect2 rect = box.GetGlobalRect();
            bool centred = Math.Abs(rect.GetCenter().X - drawnRect.GetCenter().X) <= 2f;
            bool below = rect.Position.Y >= drawnRect.End.Y - 0.5f && rect.Position.Y <= drawnRect.End.Y + 40f;
            bool tall = rect.Size.Y >= PortraitBands.MinTarget - 1f;
            bool clear = !rect.Intersects(drawnRect);
            _report.Check($"{prefix}_inspector_upgrade_deck_placement", centred && below && tall && clear,
                $"upgrade={rect} card={drawnRect} centred={centred} below={below} tall={tall} clear={clear}");
            Control? label = screen.GetNodeOrNull<Control>("%ShowUpgradeLabel");
            if (label is { } text && text.IsVisibleInTree() && text.Modulate.A >= 0.05f)
                _report.Check($"{prefix}_inspector_upgrade_label_inside_box", rect.Grow(2f).Encloses(text.GetGlobalRect()),
                    $"label={text.GetGlobalRect()} box={rect}");
        }
        Rect2 handRows = HandRowsRect(hand);
        foreach (string name in new[] { "LeftArrow", "RightArrow" })
        {
            Control? control = screen.GetNodeOrNull<Control>(name);
            if (control is null || !control.IsVisibleInTree() || control.Modulate.A < 0.05f) continue;
            Rect2 rect = control.GetGlobalRect();
            _report.Check($"{prefix}_inspector_{name}_clear_of_hand", !rect.Intersects(handRows),
                $"{name}={rect} handRows={handRows}");
        }
    }

    private Rect2 HandRowsRect(NPlayerHand hand)
    {
        Rect2? union = null;
        foreach (NHandCardHolder holder in hand.ActiveHolders)
        {
            if (!GodotObject.IsInstanceValid(holder)) continue;
            Rect2 rect = holder.Hitbox.GetGlobalRect();
            union = union is { } rows ? rows.Merge(rect) : rect;
        }
        if (union is { } present) return present.Grow(4f);
        PortraitCombatBands bands = LauncherBands();
        return new Rect2(0f, bands.HandTopY, GetViewportSize().X, GetViewportSize().Y - bands.HandTopY);
    }

    /// <summary>
    /// The flat-hand pass: the same fight with <see cref="PortraitHandLayout.FlatHand"/> on. Every gesture is repeated
    /// with the <c>flat</c> suffix: press-grows-in-place, drag-past-slop pickup, a targeted snap play (aim park) and the
    /// ten-card back-row drag. Flat rows have no rotation and the same per-shape card scale at five and ten cards.
    /// </summary>
    private async Task RunFlatHandPassAsync(NCombatRoom room, Player player)
    {
        _report.Check("touch-combat_flat_hand_default_off", !PortraitHandLayout.FlatHand,
            $"FlatHand={PortraitHandLayout.FlatHand}");
        // Control returns while the turn-start draw and hand animation are still running (the fan path settles here too);
        // let the room and hand reach their final state before switching the layout and replacing the hand.
        await WaitUntilStableAsync(() => IsPlayerPlayPhase()
                ? SettledRoomSignature(includeHealthBars: false, includeIntentFade: false)
                : null,
            "touch-combat flat next turn", TimeSpan.FromSeconds(2));
        PortraitHandLayout.FlatHand = true;
        try
        {
            NPlayerHand hand = GetHand();
            await SetHandAsync(player, BashId, StrikeId, DefendId, DefendId, DefendId);
            await SetEnergyAsync(player, 9m);
            await ParkPointerAsync();
            await WaitForFlatHandAsync(hand, "5");
            await CaptureAsync("touch-combat_03e_hand_flat");
            await TestPressedGrowthAsync(hand, "flat");
            await TestDragPastSlopAsync(hand, player, "flat");

            // A targeted card at the exposed right edge for the aim-park and snap release.
            await ResetGestureAsync(hand);
            await SetHandAsync(player, DefendId, DefendId, DefendId, BashId, StrikeId);
            await SetEnergyAsync(player, 9m);
            await ParkPointerAsync();
            await WaitForFlatHandAsync(hand, "targeted");
            List<NCreature> enemies = LivingEnemies(room);
            if (enemies.Count == 0)
            {
                _report.Skip("touch-combat_flat_targeted_play", "no living enemy");
            }
            else
            {
                (NCreature enemy, ReleaseCandidate snap) = PickSnapTarget(enemies, GetViewportSize());
                if (snap.Clearance <= TouchCardMath.TargetSnap)
                {
                    _report.Fail("touch-combat_flat_target_points_valid",
                        $"no enemy has a near-miss point clear of the others (best clearance={snap.Clearance:F1})");
                }
                else
                {
                    await RaiseHpAsync(enemy, 500);
                    await TestTargetedReleaseAsync(hand, enemy, snap.Point, "flat_snap",
                        $"flat near miss at {snap.Distance:F1} from {enemy.Name}",
                        "touch-combat_08a_snap_aiming_flat", "touch-combat_08_snap_play_flat");
                }
            }

            await ResetGestureAsync(hand);
            await SetHandAsync(player, StrikeId, DefendId, DefendId, StrikeId, DefendId,
                StrikeId, DefendId, StrikeId, DefendId, StrikeId);
            await SetEnergyAsync(player, 9m);
            await ParkPointerAsync();
            await WaitUntilAsync(() => GetHand().ActiveHolders.Count == 10, "flat ten cards", ShortWait);
            await WaitForFlatHandAsync(GetHand(), "10");
            await CaptureAsync("touch-combat_11b_ten_cards_flat");
            await TestBackRowDragOnUnblockedAsync(room, GetHand(), "flat");
        }
        finally
        {
            PortraitHandLayout.FlatHand = false;
        }
        PortraitHandLayout.FlatHand = false;
        await WaitForDrawnFrameAsync();
    }

    /// <summary>
    /// Waits for the flat layout and checks it: every card unrotated, and every card at the per-shape resting scale
    /// (the same at five and ten cards; the helpers no longer shrink with the hand count).
    /// </summary>
    private async Task WaitForFlatHandAsync(NPlayerHand hand, string label)
    {
        PortraitCombatBands bands = LauncherBands();
        int count = PortraitCombatMath.EffectiveCardCount(hand.ActiveHolders.Count, hand.InCardPlay);
        float resting = PortraitCombatMath.HandCardScaleFor(bands, count);
        bool flat = await TryWaitAsync(
            () => hand.ActiveHolders.Count > 0 && hand.ActiveHolders.All(h =>
                MathF.Abs(h.RotationDegrees) <= 0.05f && Mathf.Abs(h.Scale.X - resting) <= 0.03f),
            $"flat hand ({label})", TimeSpan.FromSeconds(2));
        await WaitForDrawnFrameAsync();
        NHandCardHolder[] holders = hand.ActiveHolders.ToArray();
        _report.Check($"touch-combat_flat_rows_unrotated_{label}",
            flat && holders.Length > 0 && holders.All(h => MathF.Abs(h.RotationDegrees) <= 0.05f),
            $"rotations={string.Join(",", holders.Select(h => h.RotationDegrees.ToString("F1")))}");
        _report.Check($"touch-combat_flat_resting_scale_{label}",
            holders.Length > 0 && holders.All(h => Mathf.Abs(h.Scale.X - resting) <= 0.03f),
            $"scales={string.Join(",", holders.Select(h => h.Scale.X.ToString("F3")))} " +
            $"expected={resting:F3} compact={bands.Compact} count={count}");
    }

    /// <summary>
    /// The aim presentation is one size per shape: the five- and ten-card aiming moments must draw the card at the same
    /// scale (within 1%), so the parked card does not change size when a draw changes the hand count.
    /// </summary>
    private void ReportAimScaleConsistency()
    {
        if (_aimScaleCrossCheckReported) return;
        _aimScaleCrossCheckReported = true;
        if (_aimedScaleFiveCards is { } five && _aimedScaleTenCards is { } ten)
        {
            float difference = MathF.Abs(five - ten) / MathF.Max(ten, 0.01f);
            _report.Check("touch-combat_aim_scale_5_vs_10", difference <= 0.01f,
                $"five={five:F3} ten={ten:F3} difference={difference * 100f:F2}%");
        }
        else
        {
            _report.Skip("touch-combat_aim_scale_5_vs_10",
                $"five={_aimedScaleFiveCards?.ToString("F3") ?? "none"} ten={_aimedScaleTenCards?.ToString("F3") ?? "none"}");
        }
    }

    /// <summary>
    /// Press a hand card that has keyword tips (the fixture's Bash) without moving: the game's tip set must sit beside the
    /// drawn card (entirely left or right of it, never over it), inside the canvas, as the landscape game puts a hand card's
    /// tips. The old portrait behaviour clamped them above the hand in the stage band; that covered the play area.
    /// </summary>
    private async Task CheckPressedCardTipAsync(NPlayerHand hand)
    {
        NHandCardHolder? card = hand.ActiveHolders.FirstOrDefault(h => h.CardModel?.HoverTips.Any() == true);
        if (card is null)
        {
            _report.Skip("touch-combat_tip_beside_card", "no hand card with keyword tips");
            return;
        }
        Vector2 point = PointOnHolder(card, 0.5f, 0.5f);
        await _pointer.PressAsync(point);
        Control? tip = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            tip = TipSetFor(card);
            if (tip is { } shown && shown.IsVisibleInTree()) break;
            await WaitFramesAsync(1);
        }
        await WaitForDrawnFrameAsync();
        Rect2? rect = tip is { } tipControl && tipControl.IsVisibleInTree() ? VisibleTipRect(tipControl) : null;
        Rect2 cardRect = DrawnCardRect(card);
        // Beside, not above or below: the tip's whole width is clear of the card on one side, and it shares the card's
        // vertical band (the native landscape alignment). Inside the canvas, since a phone cannot scroll the tips.
        bool sideBySide = rect is { } r
            && (r.End.X <= cardRect.Position.X + 2f || r.Position.X >= cardRect.End.X - 2f);
        bool sameBand = rect is { } band && band.Position.Y < cardRect.End.Y && band.End.Y > cardRect.Position.Y;
        bool inside = rect is { } drawn && InsideCanvas(drawn);
        _report.Check("touch-combat_tip_beside_card",
            sideBySide && sameBand && inside && rect is { } placed && !placed.Intersects(cardRect),
            $"card={card.CardModel?.Id.Entry ?? "?"} tip={rect} cardRect={cardRect} canvas={GetViewportSize()} " +
            $"sideBySide={sideBySide} sameBand={sameBand} inside={inside} " +
            $"tipW={rect?.Size.X:F1} tipH={rect?.Size.Y:F1}");
        if (rect is not null) await CaptureAsync("touch-combat_03c_card_tip");
        await _pointer.ReleaseAsync(point);
        NHoverTipSet.Remove(card);
        if (InspectOpen())
        {
            await _pointer.TapAsync(new Vector2(60f, 420f));
            await TryWaitAsync(() => !InspectOpen(), "inspector dismissed after the tip check",
                TimeSpan.FromSeconds(1.5));
        }
    }

    private static Control? TipSetFor(Control owner)
    {
        try
        {
            var field = HarmonyLib.AccessTools.Field(typeof(NHoverTipSet), "_activeHoverTips");
            if (field?.GetValue(null) is not System.Collections.IDictionary active) return null;
            return active[owner] as Control;
        }
        catch
        {
            return null;
        }
    }

    private static Rect2? VisibleTipRect(Control tipSet)
    {
        Rect2? union = null;
        void Visit(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is Control control && control.IsVisibleInTree())
                {
                    Rect2 rect = control.GetGlobalRect();
                    if (rect.Size.X >= 1f && rect.Size.Y >= 1f)
                        union = union is { } present ? present.Merge(rect) : rect;
                }
                Visit(child);
            }
        }
        Visit(tipSet);
        if (union is null && tipSet.GetGlobalRect() is { } fallback && fallback.Size.Y >= 1f) union = fallback;
        return union;
    }

    /// <summary>
    /// Pressing a hand card grows its drawn card (<c>holder.Scale.X × CardNode.Scale.X</c>) to the hand's held scale
    /// (<c>HeldCardScaleFor</c>) about its own centre while the holder, hitbox rect and neighbours stay put; a wiggle
    /// smaller than <see cref="TouchCardMath.PickupSlop"/> still does not start card play, and the card's visual scale
    /// settles back to the resting one once the finger is gone.
    /// </summary>
    private async Task TestPressedGrowthAsync(NPlayerHand hand, string suffix = "")
    {
        await ParkPointerAsync();
        NHandCardHolder card = RightmostHolder(hand);
        Vector2 point = PointOnHolder(card, 0.5f, 0.5f);
        // The card's visual centre: Hitbox.GetGlobalRect().GetCenter() ignores the holder's fan rotation and reports a
        // different point for a rotated card, so compare the transformed centre the finger actually landed on.
        Vector2 resting = point;
        Rect2 restingHitbox = card.Hitbox.GetGlobalRect();
        int count = hand.ActiveHolders.Count;
        PortraitCombatBands bands = LauncherBands();
        int scaleCount = PortraitCombatMath.EffectiveCardCount(count, hand.InCardPlay);
        float heldScale = PortraitCombatMath.HeldCardScaleFor(bands, scaleCount);
        float restingScale = PortraitCombatMath.HandCardScaleFor(bands, scaleCount);
        // Pressed growth lives on the card node's visual scale, not the holder: the holder keeps the resting fan scale and
        // the hitbox keeps the finger's own rectangle. Record what "resting" looks like as drawn so release is compared
        // with the same frame-sized quantity that paused on the press.
        float restingVisualScale = VisibleCardScale(card);
        float restingHolderScale = card.Scale.X;
        HolderSnapshot[] neighbours = SnapshotOthers(hand, card);

        // The injected touch is independent of the OS cursor, but the launcher's drag/aim paths read the real cursor, so
        // assert that a warp takes. Under XWayland a warp can be dropped while the window loses pointer focus; aligning
        // re-asserts the cursor (no input event) and waits a bounded number of frames instead of sampling immediately
        // after the async settle. The tolerance is one window pixel: the warp can only land on whole pixels.
        await _pointer.AlignPointerAsync(point);
        await _pointer.PressAsync(point);
        Vector2 mouse = _tree.Root.GetMousePosition();
        if (mouse.DistanceTo(point) > _pointer.PointerTolerance)
            mouse = await _pointer.AlignPointerAsync(point);
        _report.Check(WithSuffix("touch-combat_press_mouse_agrees", suffix),
            mouse.DistanceTo(point) <= _pointer.PointerTolerance,
            $"injected finger={point} Root.GetMousePosition()={mouse} delta={mouse - point} " +
            $"tolerance={_pointer.PointerTolerance:F2} focused={DisplayServer.WindowIsFocused()}");

        // Below the pickup slop: a small wiggle must not pick the card up. Do it straight after the press, well inside
        // HoldSeconds, so the still-finger inspector cannot open before this check.
        float wiggle = TouchCardMath.PickupSlop * 0.55f;
        Vector2 wiggled = point + new Vector2(wiggle, -wiggle * 0.5f);
        await _pointer.MoveAsync(wiggled, pressed: true);
        await WaitFramesAsync(3);
        _report.Check(WithSuffix("touch-combat_wiggle_below_pickup_slop_keeps_hand", suffix),
            !hand.InCardPlay && hand.ActiveHolders.Count == count && !InspectOpen(),
            $"moved={point.DistanceTo(wiggled):F1}/{TouchCardMath.PickupSlop:F0} cardPlay={hand.InCardPlay} " +
            $"hand={hand.ActiveHolders.Count}/{count} inspect={InspectOpen()}");

        bool grown = await TryWaitAsync(
            () => Mathf.Abs(VisibleCardScale(card) - heldScale) <= 0.03f,
            "pressed card at the held scale", TimeSpan.FromSeconds(1));
        Vector2 centre = PointOnHolder(card, 0.5f, 0.5f);
        Rect2 grownHitbox = card.Hitbox.GetGlobalRect();
        float hitboxShift = grownHitbox.GetCenter().DistanceTo(restingHitbox.GetCenter());
        float hitboxSize = Mathf.Max(Mathf.Abs(grownHitbox.Size.X - restingHitbox.Size.X),
            Mathf.Abs(grownHitbox.Size.Y - restingHitbox.Size.Y));
        _report.Check(WithSuffix("touch-combat_pressed_card_grows", suffix), grown,
            $"visibleScale={VisibleCardScale(card):F3} expected={heldScale:F3} holder={card.Scale.X:F3} " +
            $"cardNode={CardNodeScale(card):F3} count={scaleCount}");
        _report.Check(WithSuffix("touch-combat_pressed_card_grows_in_place", suffix), centre.DistanceTo(point) <= 6f,
            $"centre={centre} finger={point} offset={centre.DistanceTo(point):F1}");
        _report.Check(WithSuffix("touch-combat_pressed_card_hitbox_stays", suffix),
            hitboxShift <= 1f && hitboxSize <= 1f && Mathf.Abs(card.Scale.X - restingHolderScale) <= 0.01f,
            $"shift={hitboxShift:F2} size={hitboxSize:F2} hitbox={grownHitbox} resting={restingHitbox} " +
            $"holderScale={card.Scale.X:F3} restingHolder={restingHolderScale:F3}");
        // The launcher's pickup gate suppresses the native hover/focus on a touch press, so the card under the finger is
        // the touch pass's pressed holder (native focus can also be set, e.g. by a mouse or controller).
        bool pressed = PhoneTouchCards.PressedHolder == card || hand.FocusedHolder == card;
        _report.Check(WithSuffix("touch-combat_pressed_card_focused", suffix), pressed,
            $"pressed={(PhoneTouchCards.PressedHolder == card ? "the pressed card" : PhoneTouchCards.PressedHolder?.Name.ToString() ?? "none")} " +
            $"focused={(hand.FocusedHolder == card ? "the pressed card" : hand.FocusedHolder?.Name.ToString() ?? "none")}");
        _report.Check(WithSuffix("touch-combat_press_does_not_start_play", suffix), !hand.InCardPlay, $"cardPlay={hand.InCardPlay}");
        CheckNeighboursStill(hand, neighbours, WithSuffix("pressed", suffix));
        await CaptureAsync(WithSuffix("touch-combat_04_pressed_growth", suffix));

        await _pointer.ReleaseAsync(wiggled);
        await WaitUntilAsync(() => !hand.InCardPlay, "no card play after the wiggle", ShortWait);
        // The growth wait keeps the finger down past HoldSeconds, which legitimately opens the inspector; dismiss it so
        // the next gesture starts from a calm hand.
        if (InspectOpen())
        {
            await _pointer.TapAsync(new Vector2(60f, 420f));
            await TryWaitAsync(() => !InspectOpen(), "inspector dismissed after the pressed-growth press", TimeSpan.FromSeconds(2));
        }
        await ParkPointerAsync();
        if (!GodotObject.IsInstanceValid(card) || !card.IsInsideTree())
        {
            _report.Fail(WithSuffix("touch-combat_pressed_card_reverts", suffix), "the pressed card left the hand after a sub-slop wiggle");
            return;
        }
        bool reverted = await TryWaitAsync(
            () => Mathf.Abs(VisibleCardScale(card) - restingVisualScale) <= 0.04f,
            "pressed card back at the resting scale", TimeSpan.FromSeconds(1));
        Vector2 revertedCentre = PointOnHolder(card, 0.5f, 0.5f);
        Rect2 revertedHitbox = card.Hitbox.GetGlobalRect();
        float revertedHitboxShift = revertedHitbox.GetCenter().DistanceTo(restingHitbox.GetCenter());
        float revertedHitboxSize = Mathf.Max(Mathf.Abs(revertedHitbox.Size.X - restingHitbox.Size.X),
            Mathf.Abs(revertedHitbox.Size.Y - restingHitbox.Size.Y));
        _report.Check(WithSuffix("touch-combat_pressed_card_reverts", suffix),
            reverted && revertedCentre.DistanceTo(resting) <= 6f,
            $"visibleScale={VisibleCardScale(card):F3} expected={restingVisualScale:F3} holder={card.Scale.X:F3} " +
            $"cardNode={CardNodeScale(card):F3} restingScale={restingScale:F3} count={scaleCount} " +
            $"centre={revertedCentre} resting={resting}");
        _report.Check(WithSuffix("touch-combat_pressed_card_hitbox_restored", suffix),
            revertedHitboxShift <= 1f && revertedHitboxSize <= 1f,
            $"shift={revertedHitboxShift:F2} size={revertedHitboxSize:F2} hitbox={revertedHitbox} resting={restingHitbox}");
        _report.Check(WithSuffix("touch-combat_wiggle_keeps_hand", suffix), hand.ActiveHolders.Count == count,
            $"hand={hand.ActiveHolders.Count}/{count}");
    }

    /// <summary>
    /// The slow pickup pass: hold the pressed card still long enough for the hand pass to idle and HandStillness's
    /// records to go stale, then cross the pickup slop with a 120 ms hitch on the pickup frame. This is the device timing
    /// that turns a native retarget into a visible one-frame hand wobble; the regular drag pass runs at 60 fps with fresh
    /// records and can miss it. The same probe is restarted before the cancel release, so the cards that stay behind are
    /// also checked while the released card returns and reopens its slot — the other half of the visible wobble.
    /// </summary>
    private async Task TestSlowDragPickupAsync(NPlayerHand hand, Player player)
    {
        await ResetGestureAsync(hand);
        await SetHandAsync(player, BashId, StrikeId, DefendId, DefendId, DefendId);
        await SetEnergyAsync(player, 9m);
        await ParkPointerAsync();
        int previousMaxFps = Engine.MaxFps;
        ReleaseHitchNode? hitch = null;
        DragPickupWobbleProbe? wobble = null;
        DragPickupWobbleProbe? returnWobble = null;
        try
        {
            Engine.MaxFps = 30;
            hitch = new ReleaseHitchNode();
            _tree.Root.AddChild(hitch);

            NHandCardHolder card = hand.ActiveHolders.LastOrDefault(IsTargetedPlayable) ?? RightmostHolder(hand);
            Vector2 start = PointOnHolder(card, 0.5f, 0.5f);
            _report.Pass("touch-combat_slow_drag_card",
                $"card={card.CardModel?.Id.Entry ?? "?"} targeted={IsTargetedPlayable(card)}");
            // The device's touch-to-mouse path sends the hover motion before the press; the committed host's
            // PhoneTouchHover may still parse it after the button. Inject the same motion directly so the reproduction
            // does not depend on that ordering. The probe starts before the hover and focus: those are part of the
            // pickup transition whose one-frame relayout the player sees, not just the touch press.
            wobble = new DragPickupWobbleProbe(hand, card);
            wobble.Begin();
            HoverBeforeTouch(start);
            await WaitFramesAsync(2);
            _report.Pass("touch-combat_slow_drag_before_press",
                $"inCardPlay={hand.InCardPlay} active={hand.ActiveHolders.Count} " +
                $"index={hand.ActiveHolders.ToList().IndexOf(card)} card={card.CardModel?.Id.Entry ?? "?"} " +
                "probe started before the press");
            await _pointer.PressAsync(start);
            // Stay below TouchCardMath.HoldSeconds (0.6 s) while letting the pressed-growth pass go idle; the records
            // are then older than HandStillness's two-frame freshness rule but the gesture is still a drag.
            await WaitFramesAsync(10);
            hitch.Arm();
            await _pointer.MoveAsync(start + new Vector2(0f, -TouchCardMath.PickupSlop * 3f), pressed: true);
            bool started = await TryWaitAsync(() => hand.InCardPlay, "slow drag pickup", TimeSpan.FromSeconds(2));
            await WaitFramesAsync(20);
            wobble.Finish();
            _report.Check("touch-combat_slow_drag_pickup_neighbours_no_wobble", started && wobble.Passed,
                $"pickedUp={started} {wobble.Describe()}");
            Vector2 below = new(Mathf.Clamp(start.X, 120f, GetViewportSize().X - 120f),
                GetViewportSize().Y - 150f);
            await _pointer.MoveAsync(below, pressed: true);
            // The card returning to the hand reopens its slot. The cards that stayed must not shift or rescale while
            // that happens; sample the same properties as the pickup, across the release and the settle.
            returnWobble = new DragPickupWobbleProbe(hand, card);
            returnWobble.Begin();
            await _pointer.ReleaseAsync(below);
            bool returned = await TryWaitAsync(() => !hand.InCardPlay, "slow drag cancelled", TimeSpan.FromSeconds(2));
            await WaitFramesAsync(20);
            returnWobble.Finish();
            _report.Check("touch-combat_slow_drag_return_neighbours_no_wobble", returned && returnWobble.Passed,
                $"returned={returned} {returnWobble.Describe()}");
        }
        finally
        {
            wobble?.Finish();
            returnWobble?.Finish();
            hitch?.QueueFree();
            Engine.MaxFps = previousMaxFps;
            await ParkPointerAsync();
            await WaitUntilStableAsync(CurrentHandSettledSignature, "slow drag hand restored",
                TimeSpan.FromSeconds(1.2));
        }
    }

    /// <summary>
    /// A drag past <see cref="TouchCardMath.PickupSlop"/> starts card play; the dragged card is lifted
    /// <see cref="TouchCardMath.DragLift"/> above the finger at full opacity, at least
    /// the hand's held scale (<c>HeldCardScaleFor</c>), inside the canvas, and goes back when released below the play zone.
    /// </summary>
    private async Task TestDragPastSlopAsync(NPlayerHand hand, Player player, string suffix = "")
    {
        await ParkPointerAsync();
        NHandCardHolder card = RightmostHolder(hand);
        NHandCardHolder[] before = hand.ActiveHolders.ToArray();
        Rect2 resting = card.Hitbox.GetGlobalRect();
        HolderSnapshot[] neighbours = SnapshotOthers(hand, card);
        PortraitCombatBands bands = LauncherBands();
        int energy = player.PlayerCombatState!.Energy;
        Vector2 start = PointOnHolder(card, 0.5f, 0.5f);
        var wobble = new DragPickupWobbleProbe(hand, card);
        wobble.Begin();
        // Lift the card into the stage so the lift, opacity and scale are measured on the card the finger is dragging,
        // not on one still lying over the hand.
        Vector2 finger = start + new Vector2(0f, -300f);

        await _pointer.PressAsync(start);
        // Let the press's own growth settle before measuring the pickup itself.
        await WaitFramesAsync(4);
        await _pointer.MoveAsync(finger, pressed: true);
        Vector2 mouse = await _pointer.AlignPointerAsync(finger);
        _report.Check(WithSuffix("touch-combat_drag_mouse_agrees", suffix),
            mouse.DistanceTo(finger) <= _pointer.PointerTolerance,
            $"injected finger={finger} Root.GetMousePosition()={mouse} delta={mouse - finger} " +
            $"tolerance={_pointer.PointerTolerance:F2} focused={DisplayServer.WindowIsFocused()}");
        bool started = await TryWaitAsync(() => hand.InCardPlay, "card play after crossing the pickup slop",
            TimeSpan.FromSeconds(2));
        _report.Check(WithSuffix("touch-combat_drag_past_slop_starts_play", suffix), started, $"cardPlay={hand.InCardPlay}");

        if (started)
        {
            await WaitFramesAsync(20);
            // The portrait pass presents the dragged card from a deferred call after the process-frame callbacks; read
            // the geometry once that call has run, so the check measures the frame the player sees.
            await WaitForDrawnFrameAsync();
            Rect2 dragged = DraggedCardRect(card);
            float lift = finger.Y - dragged.End.Y;
            _report.Check(WithSuffix("touch-combat_dragged_card_lifted", suffix), Mathf.Abs(lift - TouchCardMath.DragLift) <= 10f,
                $"cardBottom={dragged.End.Y:F1} finger={finger.Y:F1} lift={lift:F1} expected={TouchCardMath.DragLift:F0}");
            _report.Check(WithSuffix("touch-combat_dragged_card_opaque", suffix),
                EffectiveAlpha(card) >= 0.97f,
                $"alpha={EffectiveAlpha(card):F2} expected=1.0");
            int scaleCount = PortraitCombatMath.EffectiveCardCount(hand.ActiveHolders.Count, hand.InCardPlay);
            float heldScale = PortraitCombatMath.HeldCardScaleFor(bands, scaleCount);
            _report.Check(WithSuffix("touch-combat_dragged_card_scale", suffix),
                card.Scale.X >= heldScale - 0.02f,
                $"scale={card.Scale.X:F3} minimum={heldScale:F3} count={scaleCount}");
            _report.Check(WithSuffix("touch-combat_dragged_card_inside_canvas", suffix), InsideCanvas(dragged),
                $"rect={dragged} canvas={GetViewportSize()}");
            CheckNeighboursStill(hand, neighbours, WithSuffix("dragged", suffix));
            wobble.Finish();
            _report.Check(WithSuffix("touch-combat_drag_pickup_neighbours_no_wobble", suffix), wobble.Passed,
                wobble.Describe());
            await CaptureAsync(WithSuffix("touch-combat_05_dragging", suffix));

            Vector2 below = new(Mathf.Clamp(finger.X, 120f, GetViewportSize().X - 120f), GetViewportSize().Y - 150f);
            await _pointer.MoveAsync(below, pressed: true);
            await _pointer.ReleaseAsync(below);
            bool ended = await TryWaitAsync(() => !hand.InCardPlay, "card play ended below the play zone",
                TimeSpan.FromSeconds(2));
            await WaitFramesAsync(4);
            _report.Check(WithSuffix("touch-combat_drag_released_below_play_zone_returns_card", suffix),
                ended && SameCards(hand, before) && player.PlayerCombatState!.Energy == energy,
                $"cardPlay={hand.InCardPlay} hand={hand.ActiveHolders.Count}/{before.Length} " +
                $"energy={player.PlayerCombatState!.Energy}/{energy}");
        }
        else
        {
            wobble.Finish();
            await _pointer.ReleaseAsync(finger);
        }

        await ParkPointerAsync();
        if (!GodotObject.IsInstanceValid(card) || !card.IsInsideTree())
        {
            _report.Fail(WithSuffix("touch-combat_dragged_card_reverts_after_release", suffix),
                "the dragged card left the hand after a release below the play zone");
            return;
        }
        float restingScale = PortraitCombatMath.HandCardScaleFor(bands,
            PortraitCombatMath.EffectiveCardCount(hand.ActiveHolders.Count, hand.InCardPlay));
        bool reverted = await TryWaitAsync(
            () => Mathf.Abs(card.Scale.X - restingScale) <= 0.04f
                  && card.Hitbox.GetGlobalRect().GetCenter().DistanceTo(resting.GetCenter()) <= 8f,
            "dragged card back at the resting scale and its slot", TimeSpan.FromSeconds(2));
        await WaitForDrawnFrameAsync();
        _report.Check(WithSuffix("touch-combat_dragged_card_reverts_after_release", suffix),
            reverted && card.Hitbox.GetGlobalRect().GetCenter().DistanceTo(resting.GetCenter()) <= 8f,
            $"scale={card.Scale.X:F3} expected={restingScale:F3} " +
            $"rot={card.RotationDegrees:F1} pivot={card.PivotOffset} pos={card.Position} " +
            $"centre={card.Hitbox.GetGlobalRect().GetCenter()} resting={resting.GetCenter()}");
        _report.Check(WithSuffix("touch-combat_drag_cancel_does_not_aim", suffix), !IsTargeting(), "NTargetManager left selection");
    }

    /// <summary>
    /// Readable Drag off: an untargeted dragged card keeps the point grabbed at the press under the finger, as the native
    /// mouse drag does, instead of lifting its bottom <see cref="TouchCardMath.DragLift"/> above it. A targeted card still
    /// parks at the aim position and plays, so the mode only changes the untargeted follow.
    /// </summary>
    private async Task TestReadableDragAsync(NCombatRoom room, NPlayerHand hand, Player player)
    {
        _report.Check("touch-combat_readable_drag_default_on", PhoneTouchCards.ReadableDrag,
            $"ReadableDrag={PhoneTouchCards.ReadableDrag}");
        PhoneTouchCards.ReadableDrag = false;
        try
        {
            await ResetGestureAsync(hand);
            await SetHandAsync(player, DefendId, DefendId, DefendId, DefendId, DefendId);
            await SetEnergyAsync(player, 9m);
            await ParkPointerAsync();
            NHandCardHolder card = hand.ActiveHolders[2];
            // Grab above the card's centre: the offset the native drag keeps is visible and points down from the finger.
            Vector2 press = PointOnHolder(card, 0.5f, 0.3f);
            Vector2 grab = card.Hitbox.GetGlobalRect().GetCenter() - press;
            Vector2 finger = press + new Vector2(0f, -300f);

            await _pointer.PressAsync(press);
            await WaitFramesAsync(4);
            await _pointer.MoveAsync(finger, pressed: true);
            bool started = await TryWaitAsync(() => hand.InCardPlay, "readable-drag-off pickup", TimeSpan.FromSeconds(2));
            await WaitFramesAsync(20);
            await WaitForDrawnFrameAsync();
            Rect2 dragged = DraggedCardRect(card);
            Vector2 expected = finger + grab;
            float offsetError = dragged.GetCenter().DistanceTo(expected);
            _report.Check("touch-combat_readable_drag_off_keeps_grab",
                started && offsetError <= 12f,
                $"card={card.CardModel?.Id.Entry ?? "?"} grab={grab} finger={finger} expected={expected} " +
                $"centre={dragged.GetCenter()} error={offsetError:F1}");
            float lift = finger.Y - dragged.End.Y;
            _report.Check("touch-combat_readable_drag_off_not_lifted",
                started && Mathf.Abs(lift - TouchCardMath.DragLift) > 20f,
                $"bottom={dragged.End.Y:F1} finger={finger.Y:F1} lift={lift:F1} readableLift={TouchCardMath.DragLift:F0}");
            await CaptureAsync("touch-combat_05b_drag_grab_anchored");

            Vector2 below = new(Mathf.Clamp(finger.X, 120f, GetViewportSize().X - 120f), GetViewportSize().Y - 150f);
            await _pointer.MoveAsync(below, pressed: true);
            await _pointer.ReleaseAsync(below);
            bool returned = await TryWaitAsync(() => !hand.InCardPlay, "readable-drag-off release", TimeSpan.FromSeconds(2));
            await WaitFramesAsync(4);
            _report.Check("touch-combat_readable_drag_off_returns", returned, $"cardPlay={hand.InCardPlay}");
            await ParkPointerAsync();
            await WaitUntilStableAsync(CurrentHandSettledSignature, "readable drag hand restored",
                TimeSpan.FromSeconds(1.2));

            // The targeted path is the same in either mode: the card parks at the aim position, the arrow follows the
            // finger, and the release plays on the creature.
            await SetHandAsync(player, DefendId, DefendId, DefendId, StrikeId, StrikeId);
            await SetEnergyAsync(player, 9m);
            await ParkPointerAsync();
            List<NCreature> enemies = LivingEnemies(room);
            if (enemies.Count == 0)
            {
                _report.Skip("touch-combat_readable_drag_off_aim", "no living enemy");
            }
            else
            {
                NCreature enemy = enemies[0];
                await RaiseHpAsync(enemy, 500);
                await TestTargetedReleaseAsync(hand, enemy, enemy.Hitbox.GetGlobalRect().GetCenter(),
                    "readable_off", "readable drag off, released on the enemy",
                    "touch-combat_05c_drag_grab_anchored_aim", "touch-combat_05d_drag_grab_anchored_play");
            }
        }
        finally
        {
            PhoneTouchCards.ReadableDrag = true;
        }
        await ResetGestureAsync(hand);
        await SetHandAsync(player, BashId, StrikeId, DefendId, DefendId, DefendId);
        await SetEnergyAsync(player, 9m);
        await ParkPointerAsync();
        await WaitUntilStableAsync(CurrentHandSettledSignature, "touch-combat hand after readable drag",
            TimeSpan.FromSeconds(1.2));
    }

    /// <summary>
    /// A self-target skill dragged above the play zone and brought back down before release returns to the hand; energy and
    /// block are untouched and no targeting selection is left behind.
    /// </summary>
    private async Task TestSelfTargetReturnsAsync(NPlayerHand hand, Player player)
    {
        await ParkPointerAsync();
        NHandCardHolder card = RightmostHolder(hand);
        CardModel? model = card.CardModel;
        bool usable = model is { TargetType: TargetType.Self, Type: CardType.Skill } && model.CanPlay();
        _report.Check("touch-combat_self_target_skill_in_hand", usable,
            $"card={model?.Id.Entry ?? "none"} type={model?.Type} target={model?.TargetType}");
        if (!usable) return;

        NHandCardHolder[] before = hand.ActiveHolders.ToArray();
        int energy = player.PlayerCombatState!.Energy;
        int block = player.Creature.Block;
        Rect2 resting = card.Hitbox.GetGlobalRect();
        Vector2 start = PointOnHolder(card, 0.5f, 0.5f);
        Vector2 arena = start + new Vector2(0f, -420f);
        Vector2 below = new(Mathf.Clamp(start.X, 120f, GetViewportSize().X - 120f), GetViewportSize().Y - 150f);

        await _pointer.PressAsync(start);
        await _pointer.MoveAsync(start + new Vector2(0f, -TouchCardMath.PickupSlop * 3f), pressed: true);
        bool started = await TryWaitAsync(() => hand.InCardPlay, "self-target card picked up", TimeSpan.FromSeconds(2));
        if (started)
        {
            await _pointer.MoveAsync(arena, pressed: true);
            await WaitFramesAsync(10);
            await _pointer.MoveAsync(below, pressed: true);
            await WaitFramesAsync(4);
        }
        await _pointer.ReleaseAsync(below);
        await TryWaitAsync(() => !hand.InCardPlay, "self-target play ended", TimeSpan.FromSeconds(2));
        await WaitFramesAsync(4);
        await ParkPointerAsync();
        await WaitFramesAsync(12);

        _report.Check("touch-combat_self_target_below_play_zone_returns", started && !hand.InCardPlay
                && SameCards(hand, before),
            $"pickedUp={started} cardPlay={hand.InCardPlay} hand={hand.ActiveHolders.Count}/{before.Length}");
        _report.Check("touch-combat_self_target_unchanged_energy_and_block",
            player.PlayerCombatState!.Energy == energy && player.Creature.Block == block,
            $"energy={player.PlayerCombatState!.Energy}/{energy} block={player.Creature.Block}/{block}");
        _report.Check("touch-combat_self_target_no_click_to_aim", !IsTargeting(), "NTargetManager left selection");
        if (GodotObject.IsInstanceValid(card) && card.IsInsideTree())
            _report.Check("touch-combat_self_target_returns_to_its_slot",
                card.Hitbox.GetGlobalRect().GetCenter().DistanceTo(resting.GetCenter()) <= 8f,
                $"centre={card.Hitbox.GetGlobalRect().GetCenter()} resting={resting.GetCenter()}");
        else
            _report.Fail("touch-combat_self_target_returns_to_its_slot", "the skill left the hand instead of returning");
        await CaptureAsync("touch-combat_06_self_target_returned");
    }

    /// <summary>The enemy whose near-miss release point is clearest of the others, and that point.</summary>
    private (NCreature Enemy, ReleaseCandidate Snap) PickSnapTarget(IReadOnlyList<NCreature> enemies, Vector2 canvas)
    {
        float snapDistance = TouchCardMath.TargetPadding + (TouchCardMath.TargetSnap - TouchCardMath.TargetPadding) * 0.5f;
        NCreature enemy = enemies[0];
        ReleaseCandidate snap = default;
        float bestClearance = -1f;
        foreach (NCreature candidate in enemies)
        {
            ReleaseCandidate release = PickTargetRelease(enemies, candidate, snapDistance, canvas);
            if (release.Clearance > bestClearance)
            {
                bestClearance = release.Clearance;
                enemy = candidate;
                snap = release;
            }
        }
        return (enemy, snap);
    }

    /// <summary>
    /// An attack released just outside an enemy's hitbox still lands on it (padding), a near miss within
    /// <see cref="TouchCardMath.TargetSnap"/> does too, and a release far from every enemy cancels without playing.
    /// </summary>
    private async Task TestTargetingAsync(NCombatRoom room, NPlayerHand hand, Player player)
    {
        List<NCreature> enemies = LivingEnemies(room);
        if (enemies.Count == 0)
        {
            _report.Skip("touch-combat_targeting", "no living enemy");
            return;
        }
        Vector2 canvas = GetViewportSize();
        float paddingDistance = TouchCardMath.TargetPadding * 0.5f;

        // Pick the enemy whose near-miss point is clearest of the others: the nearest-enemy rule must still resolve to it.
        (NCreature enemy, ReleaseCandidate snap) = PickSnapTarget(enemies, canvas);
        float bestClearance = snap.Clearance;
        ReleaseCandidate padding = PickTargetRelease(enemies, enemy, paddingDistance, canvas);
        Vector2 farPoint = FarFromEnemies(enemies, canvas);
        if (bestClearance <= TouchCardMath.TargetSnap)
        {
            _report.Fail("touch-combat_target_points_valid",
                $"no enemy has a near-miss release point at least {TouchCardMath.TargetSnap:F0} from every other enemy " +
                $"(best clearance={bestClearance:F1})");
            return;
        }
        await RaiseHpAsync(enemy, 500);

        _report.Check("touch-combat_target_points_inside_canvas",
            InsideCanvas(new Rect2(padding.Point, Vector2.Zero)) && InsideCanvas(new Rect2(snap.Point, Vector2.Zero)),
            $"padding={padding.Point} snap={snap.Point}");
        Rect2 enemyRect = enemy.Hitbox.GetGlobalRect();
        float paddingActual = DistanceOutside(enemyRect, padding.Point);
        float snapActual = DistanceOutside(enemyRect, snap.Point);
        float farActual = enemies.Min(e => DistanceOutside(e.Hitbox.GetGlobalRect(), farPoint));
        float paddingClearance = ClearanceFromOthers(enemies, enemy, padding.Point);
        _report.Check("touch-combat_target_points_valid",
            Mathf.Abs(paddingActual - padding.Distance) <= 1f && Mathf.Abs(snapActual - snap.Distance) <= 1f
            && paddingClearance > TouchCardMath.TargetPadding
            && farActual > TouchCardMath.TargetSnap,
            $"padding={paddingActual:F1}/{padding.Distance:F1} (other {paddingClearance:F1}) " +
            $"snap={snapActual:F1}/{snap.Distance:F1} far={farActual:F1} (>{TouchCardMath.TargetSnap:F0}) " +
            $"enemy={enemyRect} clearance={bestClearance:F1}");

        await TestTargetedReleaseAsync(hand, enemy, padding.Point, "padding",
            $"within {TouchCardMath.TargetPadding:F0} of {enemy.Name} ({paddingActual:F1} outside its hitbox)");
        await SetEnergyAsync(player, 9m);
        await WaitUntilStableAsync(CurrentHandSettledSignature, "touch-combat between target releases",
            TimeSpan.FromSeconds(1.2));
        await TestTargetedReleaseAsync(hand, enemy, snap.Point, "snap",
            $"a near miss at {snapActual:F1} from {enemy.Name}, within {TouchCardMath.TargetSnap:F0}");

        await SetEnergyAsync(player, 9m);
        if (!hand.ActiveHolders.Any(h => IsTargetedPlayable(h)))
        {
            await AddCardAsync(player, StrikeId);
            await WaitUntilStableAsync(CurrentHandSettledSignature, "touch-combat added targeted card",
                TimeSpan.FromSeconds(1.2));
        }
        int handBefore = hand.ActiveHolders.Count;
        int energy = player.PlayerCombatState!.Energy;
        int[] hp = enemies.Select(e => e.Entity.CurrentHp).ToArray();
        NHandCardHolder card = hand.ActiveHolders.Where(IsTargetedPlayable)
            .OrderBy(h => h.Hitbox.GetGlobalRect().GetCenter().X).Last();
        await ParkPointerAsync();
        bool dragged = await DragFromToAsync(PointOnHolder(card, 0.5f, 0.5f), farPoint,
            beforeRelease: () => CaptureAimAsync(card, "touch-combat_09a_far_aiming", "far"));
        await WaitSecondsAsync(0.8);
        _report.Check("touch-combat_far_release_cancels", dragged && !hand.InCardPlay
                && hand.ActiveHolders.Count == handBefore && player.PlayerCombatState!.Energy == energy
                && enemies.Select((e, i) => e.Entity.CurrentHp == hp[i]).All(unchanged => unchanged),
            $"dragged={dragged} cardPlay={hand.InCardPlay} hand={hand.ActiveHolders.Count}/{handBefore} " +
            $"energy={player.PlayerCombatState!.Energy}/{energy} enemyHp={string.Join(",", enemies.Select(e => e.Entity.CurrentHp))}");
        _report.Check("touch-combat_far_release_no_click_to_aim", !IsTargeting(), "NTargetManager left selection");
        await CaptureAsync("touch-combat_09_far_release");
    }

    private async Task TestTargetedReleaseAsync(NPlayerHand hand, NCreature enemy, Vector2 release,
        string label, string detail, string? aimShot = null, string? playShot = null)
    {
        if (!hand.ActiveHolders.Any(h => IsTargetedPlayable(h)))
        {
            _report.Skip($"touch-combat_{label}_targeted_card", "no playable targeted card in the hand");
            return;
        }
        NHandCardHolder card = hand.ActiveHolders.Where(IsTargetedPlayable)
            .OrderBy(h => h.Hitbox.GetGlobalRect().GetCenter().X).Last();
        int handBefore = hand.ActiveHolders.Count;
        int hpBefore = enemy.Entity.CurrentHp;
        int[] others = LivingEnemies(NCombatRoom.Instance!).Where(e => e != enemy).Select(e => e.Entity.CurrentHp).ToArray();
        string cardName = card.CardModel?.Id.Entry ?? "unknown";
        await ParkPointerAsync();
        Vector2 start = PointOnHolder(card, 0.5f, 0.5f);
        string aimName = aimShot ?? $"touch-combat_{(label == "padding" ? "07a_padding" : "08a_snap")}_aiming";
        bool dragged = await DragFromToAsync(start, release, beforeRelease: () =>
            CaptureAimAsync(card, aimName, label));
        bool played = await TryWaitAsync(
            () => hand.ActiveHolders.Count < handBefore || enemy.Entity.CurrentHp < hpBefore,
            $"{label} attack played", TimeSpan.FromSeconds(6));
        await WaitFramesAsync(6);

        _report.Check($"touch-combat_{label}_plays_on_enemy",
            dragged && played && enemy.Entity.CurrentHp < hpBefore,
            $"card={cardName} {detail} release={release} hand={hand.ActiveHolders.Count}/{handBefore} " +
            $"enemyHp={enemy.Entity.CurrentHp}/{hpBefore} cardPlay={hand.InCardPlay}");
        _report.Check($"touch-combat_{label}_hits_only_that_enemy",
            LivingEnemies(NCombatRoom.Instance!).Where(e => e != enemy).Select(e => e.Entity.CurrentHp).SequenceEqual(others),
            $"otherHp={string.Join(",", LivingEnemies(NCombatRoom.Instance!).Where(e => e != enemy).Select(e => e.Entity.CurrentHp))} " +
            $"before={string.Join(",", others)}");
        await CaptureAsync(playShot ?? $"touch-combat_0{(label == "padding" ? 7 : 8)}_{label}_play");

        if (enemy.Entity.IsAlive) await CreatureCmd.SetCurrentHp(enemy.Entity, enemy.Entity.MaxHp);
    }

    /// <summary>
    /// A back-row drag's target must have no block: the Strikes in the ten-card hand deal six damage, so a target holding
    /// block would consume the card without changing HP and fail the check for the wrong reason.
    /// </summary>
    private async Task TestBackRowDragOnUnblockedAsync(NCombatRoom room, NPlayerHand hand, string suffix = "")
    {
        NCreature? enemy = UnblockedFrontEnemy(room);
        if (enemy is null)
        {
            _report.Fail(WithSuffix("touch-combat_back_row_card_target", suffix),
                "no living, hittable enemy without block; the six-damage back-row card cannot prove a hit on any of them");
            return;
        }
        _report.Pass(WithSuffix("touch-combat_back_row_card_target", suffix),
            $"enemy={enemy.Entity.Name} hp={enemy.Entity.CurrentHp}/{enemy.Entity.MaxHp} block={enemy.Entity.Block}");
        await RaiseHpAsync(enemy, 500);
        await TestBackRowDragAsync(hand, enemy, suffix);
    }

    /// <summary>A back-row card of a ten-card hand, taken by its exposed top strip, drags to an enemy and plays.</summary>
    private async Task TestBackRowDragAsync(NPlayerHand hand, NCreature enemy, string suffix = "")
    {
        await ParkPointerAsync();
        List<NHandCardHolder> holders = hand.ActiveHolders.ToList();
        List<NHandCardHolder> backRow = UpperRow(holders);
        NHandCardHolder? card = backRow.FirstOrDefault(h => IsTargetedPlayable(h));
        if (card is null)
        {
            _report.Fail(WithSuffix("touch-combat_back_row_card", suffix), "no playable targeted card in the back row");
            return;
        }

        int handBefore = hand.ActiveHolders.Count;
        int hpBefore = enemy.Entity.CurrentHp;
        string cardName = card.CardModel?.Id.Entry ?? "unknown";
        Rect2 rect = card.Hitbox.GetGlobalRect();
        // The upper row's left strip is the strip no later card covers, so it is the reliable horizontal touch target.
        // Keep the press vertically mid-card: the host's emulated pointer sits a little above the injected point, and a
        // press 0.12 of a card height below the top lands above the card (the run logged focused=another card).
        Vector2 start = PointOnHolder(card, 0.12f, 0.5f);
        _report.Check(WithSuffix("touch-combat_back_row_card_exposed", suffix),
            rect.Position.Y < holders.Max(h => h.Hitbox.GetGlobalRect().Position.Y),
            $"backRowTop={rect.Position.Y:F1} frontRowTop={holders.Max(h => h.Hitbox.GetGlobalRect().Position.Y):F1}");

        await _pointer.PressAsync(start);
        await WaitFramesAsync(8);
        // The pickup gate suppresses the native hover/focus on a touch press; the touch pass records the pressed holder.
        bool grabbed = PhoneTouchCards.PressedHolder == card || hand.FocusedHolder == card;
        Rect2 target = enemy.Hitbox.GetGlobalRect();
        Vector2 release = target.GetCenter();
        await _pointer.MoveAsync(start + new Vector2(0f, -TouchCardMath.PickupSlop * 3f), pressed: true);
        await TryWaitAsync(() => hand.InCardPlay, "back-row card picked up", TimeSpan.FromSeconds(2));
        for (int i = 1; i <= 14; i++)
        {
            await _pointer.MoveAsync(start.Lerp(release, i / 14f), pressed: true);
            await WaitFramesAsync(1);
        }
        await WaitFramesAsync(10);
        await CaptureAimAsync(card, WithSuffix("touch-combat_12a_back_row_aiming", suffix), WithSuffix("back_row", suffix));
        await _pointer.ReleaseAsync(release);
        bool played = await TryWaitAsync(
            () => hand.ActiveHolders.Count < handBefore || enemy.Entity.CurrentHp < hpBefore,
            "back-row card play", TimeSpan.FromSeconds(6));
        await WaitFramesAsync(8);

        _report.Check(WithSuffix("touch-combat_back_row_card_grabbed", suffix), grabbed,
            $"pressed at {start} pressedCard={PhoneTouchCards.PressedHolder?.Name.ToString() ?? "none"} " +
            $"focused={(hand.FocusedHolder == card ? cardName : "another card")}");
        _report.Check(WithSuffix("touch-combat_back_row_card_plays", suffix), grabbed && played && enemy.Entity.CurrentHp < hpBefore,
            $"card={cardName} release={release} hand={hand.ActiveHolders.Count}/{handBefore} " +
            $"enemyHp={enemy.Entity.CurrentHp}/{hpBefore} enemyBlock={enemy.Entity.Block} cardPlay={hand.InCardPlay}");
        await CaptureAsync(WithSuffix("touch-combat_12_back_row_drag", suffix));
        if (enemy.Entity.IsAlive) await CreatureCmd.SetCurrentHp(enemy.Entity, enemy.Entity.MaxHp);
    }

    /// <summary>
    /// Waits for the enemy turn to hand control back, but only for a short while: a turn loop that died mid-resolution
    /// would otherwise leave this scenario waiting until the broker's timeout. A dead loop shows as combat still in
    /// progress with player actions still disabled, and is reported as such.
    /// </summary>
    private async Task<bool> WaitForNextPlayerTurnAsync(string label)
    {
        bool returned = await TryWaitAsync(
            () => !CombatManager.Instance.PlayerActionsDisabled || !CombatManager.Instance.IsInProgress,
            $"next player turn ({label} cards)", TimeSpan.FromSeconds(30));
        if (!returned || !CombatManager.Instance.IsInProgress)
        {
            _report.Fail($"touch-combat_{label}_next_turn",
                $"the fight did not return control within 30 s: combatInProgress={CombatManager.Instance.IsInProgress} " +
                $"actionsDisabled={CombatManager.Instance.PlayerActionsDisabled} " +
                $"hand={HandCountSafe()}. The turn loop stopped; the rest of the scenario was skipped.");
            return false;
        }
        bool hand = await TryWaitAsync(() => HandCountSafe() > 0, "next turn hand", TimeSpan.FromSeconds(8));
        _report.Check($"touch-combat_{label}_next_turn", hand, $"next turn hand={HandCountSafe()}");
        return hand;
    }

    private int HandCountSafe()
    {
        try
        {
            return GetHand().ActiveHolders.Count;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>Taps End Turn after checking that no hand card covers it; returns whether the enemy turn started.</summary>
    private async Task<bool> TapEndTurnAsync(NCombatRoom room, NPlayerHand hand, string label)
    {
        NEndTurnButton? endTurn = room.Ui.EndTurnButton;
        bool ready = endTurn is not null && GodotObject.IsInstanceValid(endTurn) && endTurn.IsEnabled;
        _report.Check($"touch-combat_{label}_end_turn_ready", ready,
            $"button={(endTurn is null ? "none" : endTurn.GetGlobalRect().ToString())} enabled={endTurn?.IsEnabled}");
        if (!ready) return false;

        Rect2 button = endTurn!.GetGlobalRect();
        Rect2[] cards = hand.ActiveHolders.Select(h => h.Hitbox.GetGlobalRect()).ToArray();
        float cardTop = cards.Length > 0 ? cards.Min(r => r.Position.Y) : float.PositiveInfinity;
        bool overlap = cards.Any(r => button.Grow(-2f).Intersects(r.Grow(-2f)));
        _report.Check($"touch-combat_{label}_end_turn_clear_of_cards",
            cards.Length > 0 && button.End.Y <= cardTop + 6f && !overlap,
            $"button={button} cardTop={cardTop:F1} overlaps={overlap} cards={cards.Length}");

        await _pointer.TapAsync(CenterOf(endTurn));
        bool started = await TryWaitAsync(() => CombatManager.Instance.PlayerActionsDisabled, $"enemy turn ({label} cards)",
            ShortWait);
        _report.Check($"touch-combat_{label}_end_turn_tap_works", started,
            $"actionsDisabled={CombatManager.Instance.PlayerActionsDisabled}");
        return started;
    }

    // ---- setup ----

    private async Task SetHandAsync(Player player, params string[] cardIds)
    {
        PlayerCombatState? combat = player.PlayerCombatState;
        if (combat is null) throw new InvalidOperationException("Player is not in combat.");
        NPlayerHand hand = GetHand();
        // The turn-start draw and the hand's own animations keep the counts moving for a moment after control returns;
        // let both the model and the visible holders be unchanged for ten frames before replacing the hand, or the game
        // keeps drawing into the fixture and the exact count never appears.
        await WaitForHandQuietAsync(player, hand);
        // Send the previous fixture's cards back to the draw pile instead of removing them from combat: they keep their
        // combat registration, so End Turn's discard and reshuffle never meet a card the CombatState does not know.
        CardModel[] previous = combat.Hand.Cards.ToArray();
        if (previous.Length > 0) await CardPileCmd.Add(previous, PileType.Draw);
        bool emptied = await TryWaitAsync(
            () => player.PlayerCombatState!.Hand.Cards.Count == 0 && GetHand().ActiveHolders.Count == 0,
            "hand emptied before the next fixture", ShortWait);
        if (!emptied)
            throw new InvalidOperationException("The hand did not empty for the next fixture. " + DescribeHand(player));
        foreach (string id in cardIds)
            await AddCardAsync(player, id);
        bool filled = await TryWaitAsync(
            () => player.PlayerCombatState!.Hand.Cards.Count == cardIds.Length
                  && GetHand().ActiveHolders.Count == cardIds.Length,
            $"hand of {cardIds.Length} cards", ShortWait);
        if (!filled)
            throw new InvalidOperationException(
                $"The hand fixture of {cardIds.Length} cards did not appear. " + DescribeHand(player));
        await WaitUntilStableAsync(CurrentHandSettledSignature, $"touch-combat {cardIds.Length}-card layout",
            TimeSpan.FromSeconds(1.2));
    }

    /// <summary>Waits until the model hand and its visible holders have not changed for ten frames (bounded).</summary>
    private async Task WaitForHandQuietAsync(Player player, NPlayerHand hand)
    {
        int lastModel = -1;
        int lastActive = -1;
        int stable = 0;
        ulong start = Time.GetTicksMsec();
        while (stable < 10 && Time.GetTicksMsec() - start < 2000)
        {
            int model = player.PlayerCombatState?.Hand.Cards.Count ?? -1;
            int active = GodotObject.IsInstanceValid(hand) ? hand.ActiveHolders.Count : -1;
            stable = model == lastModel && active == lastActive ? stable + 1 : 0;
            lastModel = model;
            lastActive = active;
            await WaitFramesAsync(1);
        }
    }

    /// <summary>
    /// The hand's state when a fixture fails: model cards, every holder the container has (visible or not, with its scale,
    /// rotation and position), the hand mode, FlatHand, and the turn state. Logged with the fixture exception so the next
    /// run shows why the expected count did not appear.
    /// </summary>
    private string DescribeHand(Player player)
    {
        NPlayerHand? hand = null;
        try
        {
            hand = GetHand();
        }
        catch
        {
            // No room/hand: the description below still reports the model and turn state.
        }

        string holders = "none";
        if (hand is not null)
        {
            Control? container = hand.GetNodeOrNull<Control>("%CardHolderContainer")
                                 ?? hand.GetNodeOrNull<Control>("CardHolderContainer");
            if (container is not null)
            {
                var states = new List<string>();
                foreach (Node child in container.GetChildren())
                {
                    if (child is not NHandCardHolder holder) continue;
                    string id = holder.CardModel?.Id.Entry ?? "?";
                    states.Add($"{id}:visible={holder.IsVisibleInTree()} scale={holder.Scale.X:F3} " +
                               $"rot={holder.RotationDegrees:F1} pos={holder.Position}");
                }
                holders = states.Count == 0 ? "none" : string.Join(" | ", states);
            }
        }

        CardModel[] model = player.PlayerCombatState?.Hand.Cards.ToArray() ?? Array.Empty<CardModel>();
        return $"model=[{string.Join(",", model.Select(card => card.Id.Entry))}] ({model.Length}) " +
               $"active={hand?.ActiveHolders.Count ?? -1} mode={hand?.CurrentMode.ToString() ?? "?"} " +
               $"inPlay={hand?.InCardPlay} flat={PortraitHandLayout.FlatHand} " +
               $"playPhase={IsPlayerPlayPhase()} actionsDisabled={CombatManager.Instance.PlayerActionsDisabled} " +
               $"holders=[{holders}]";
    }

    private async Task AddCardAsync(Player player, string cardId)
    {
        CardModel? canonical = ModelDb.AllCards.FirstOrDefault(card => card.Id.Entry == cardId);
        if (canonical is null) throw new InvalidOperationException($"Card '{cardId}' is not in the game's card pools.");
        ICombatState combat = player.Creature.CombatState
            ?? throw new InvalidOperationException("Player is not in combat.");
        // Create through the combat scope, as HandSelection's fixture does. A raw ToMutable card has no pile yet, so it
        // slips past the pile's registration check and is only rejected later: End Turn's discard/reshuffle then throws
        // "must be added to a CombatState before adding it to this pile" and the turn loop dies.
        CardModel card = ((ICardScope)combat).CreateCard(canonical, player);
        await CardPileCmd.AddGeneratedCardToCombat(card, PileType.Hand, player);
    }

    private static async Task SetEnergyAsync(Player player, decimal energy) => await PlayerCmd.SetEnergy(energy, player);

    private static async Task RaiseHpAsync(NCreature enemy, int hp)
    {
        await CreatureCmd.SetMaxAndCurrentHp(enemy.Entity, hp);
    }

    /// <summary>Ends a half-finished gesture or inspector so the next step starts from a calm hand.</summary>
    private async Task ResetGestureAsync(NPlayerHand hand)
    {
        await ParkPointerAsync();
        if (hand.InCardPlay) hand.CancelAllCardPlay();
        if (InspectOpen()) await _pointer.TapAsync(new Vector2(60f, 420f));
        await WaitFramesAsync(8);
    }

    private async Task ParkPointerAsync()
    {
        await _pointer.MoveAsync(new Vector2(24f, GetViewportSize().Y * 0.2f), pressed: false);
        // Let the pointer leave the cards and any hand reflow converge before the next capture/gesture.
        await WaitUntilStableAsync(CurrentHandSettledSignature, "pointer parked", TimeSpan.FromSeconds(1.2));
    }

    /// <summary>
    /// Waits for this frame's deferred calls to run (the portrait touch pass presents the dragged card there), so geometry
    /// read afterwards is what the frame draws, not the game's own process-frame move for the same frame.
    /// </summary>
    private async Task WaitForDrawnFrameAsync()
    {
        var drawn = new TaskCompletionSource();
        Callable.From(() => { drawn.TrySetResult(); }).CallDeferred();
        await drawn.Task;
    }

    /// <summary>
    /// A targeted card stops following the finger while it aims: it eases to a fixed park whose top edge is on the hand-control
    /// rail's End Turn top edge, horizontally centred, stays fully opaque (the arrow, not the card, shows the target) and
    /// grows to the per-shape aim size (<c>AimedCardScaleFor(bands)</c>), independent of the hand count. Waits for the park to
    /// settle while the finger is still down, captures that frame, then checks the presentation.
    /// </summary>
    private async Task CaptureAimAsync(NHandCardHolder card, string shotName, string label)
    {
        float handScale = MathF.Max(0.01f, MathF.Abs(
            (card.GetParent() as CanvasItem)?.GetGlobalTransformWithCanvas().Scale.Y ?? 1f));
        NPlayerHand hand = GetHand();
        PortraitCombatBands bands = LauncherBands();
        int cardCount = PortraitCombatMath.EffectiveCardCount(hand.ActiveHolders.Count, hand.InCardPlay);
        float aimedScale = TouchCardMath.AimedCardScaleFor(bands);
        float expectedScale = aimedScale * handScale;
        Rect2 endTurn = NCombatRoom.Instance?.Ui.EndTurnButton.GetGlobalRect()
                        ?? throw new InvalidOperationException("Combat room lost its End Turn button while aiming.");
        float expectedX = GetViewportSize().X * 0.5f;
        float expectedTop = endTurn.Position.Y;

        // The park eases at a fixed step per drawn frame; wait for it rather than sampling mid-move. The finger is still
        // down, so the release has not happened yet.
        for (int attempt = 0; attempt < 120; attempt++)
        {
            Rect2 now = card.Hitbox.GetGlobalRect();
            bool settled = Mathf.Abs(now.Position.Y - expectedTop) <= 8f
                           && Mathf.Abs(now.GetCenter().X - expectedX) <= 8f
                           && Mathf.Abs(now.Size.X / PortraitCombatMath.CardDefaultWidth - expectedScale) <= 0.05f
                           && EffectiveAlpha(card) >= 0.97f;
            if (settled) break;
            await WaitFramesAsync(1);
        }
        await WaitForDrawnFrameAsync();
        await CaptureAsync(shotName);

        Rect2 aimed = card.Hitbox.GetGlobalRect();
        float scale = aimed.Size.X / PortraitCombatMath.CardDefaultWidth;
        if (cardCount == 5) _aimedScaleFiveCards = scale;
        else if (cardCount == 10) _aimedScaleTenCards = scale;
        _report.Check($"touch-combat_{label}_aim_opaque", EffectiveAlpha(card) >= 0.97f,
            $"alpha={EffectiveAlpha(card):F2} expected=1.0");
        _report.Check($"touch-combat_{label}_aim_parked_on_rail",
            Mathf.Abs(aimed.Position.Y - expectedTop) <= 8f && Mathf.Abs(aimed.GetCenter().X - expectedX) <= 8f,
            $"top={aimed.Position.Y:F1} expectedTop={expectedTop:F1} endTurn={endTurn} " +
            $"centreX={aimed.GetCenter().X:F1} expectedX={expectedX:F1}");
        _report.Check($"touch-combat_{label}_aim_scale", Mathf.Abs(scale - expectedScale) <= 0.05f,
            $"scale={scale:F3} expected={expectedScale:F3} (AimedCardScaleFor(bands)={aimedScale:F3} " +
            $"compact={bands.Compact} hand={handScale:F3} count={cardCount})");
        _report.Check($"touch-combat_{label}_aim_inside_canvas", InsideCanvas(aimed),
            $"rect={aimed} canvas={GetViewportSize()}");
    }

    private async Task<bool> DragFromToAsync(Vector2 from, Vector2 to, int steps = 12, int holdFrames = 10,
        Func<Task>? beforeRelease = null)
    {
        Vector2 lift = from + new Vector2(0f, -TouchCardMath.PickupSlop * 3f);
        await _pointer.PressAsync(from);
        await _pointer.MoveAsync(lift, pressed: true);
        bool started = await TryWaitAsync(() => GetHand().InCardPlay, "card picked up", TimeSpan.FromSeconds(2));
        if (!started)
        {
            await _pointer.ReleaseAsync(lift);
            return false;
        }
        for (int i = 1; i <= steps; i++)
        {
            await _pointer.MoveAsync(lift.Lerp(to, i / (float)steps), pressed: true);
            await WaitFramesAsync(1);
        }
        await WaitFramesAsync(holdFrames);
        if (beforeRelease != null) await beforeRelease();
        await _pointer.ReleaseAsync(to);
        await WaitFramesAsync(4);
        return true;
    }

    // ---- geometry ----

    /// <summary>
    /// Samples the other hand cards on every drawn frame while one card is picked up and dragged. A pickup reparents the
    /// held card and runs the native hand layout; the portrait hand must put the remaining cards back before the frame is
    /// drawn. The existing end-of-gesture check cannot see a one- or two-frame nudge, so this probe records the largest
    /// movement of every card that stays in the hand, both the holder and hitbox and the card node's visible canvas
    /// transform (origin, scale and rotation), which carries any growth applied to the card itself.
    /// </summary>
    private sealed class DragPickupWobbleProbe
    {
        private readonly NPlayerHand _hand;
        private readonly NHandCardHolder _dragged;
        private readonly List<Baseline> _before = new();
        private bool _running;

        public int Frames { get; private set; }
        public bool Missing { get; private set; }
        public float WorstShift { get; private set; }
        public float WorstSize { get; private set; }
        public float WorstScale { get; private set; }
        public float WorstRotation { get; private set; }
        public float WorstCardShift { get; private set; }
        public float WorstCardScale { get; private set; }
        public float WorstCardRotation { get; private set; }
        public string WorstCard { get; private set; } = "";
        public int WorstFrame { get; private set; }

        public bool Passed => _before.Count > 0 && Frames >= 3 && !Missing
            && WorstShift <= 2f && WorstSize <= 2f && WorstScale <= 0.01f && WorstRotation <= 0.1f
            && WorstCardShift <= 2f && WorstCardScale <= 0.01f && WorstCardRotation <= 0.1f;

        private readonly record struct Baseline(NHandCardHolder Holder, Vector2 Center, Vector2 Size, float Scale, float Rotation,
            Control? Card, Vector2 CardOrigin, float CardScale, float CardRotation);

        public DragPickupWobbleProbe(NPlayerHand hand, NHandCardHolder dragged)
        {
            _hand = hand;
            _dragged = dragged;
        }

        public void Begin()
        {
            if (_running) return;
            _before.Clear();
            foreach (NHandCardHolder holder in _hand.ActiveHolders)
                if (holder != _dragged && GodotObject.IsInstanceValid(holder) && holder.IsInsideTree())
                {
                    Rect2 rect = holder.Hitbox.GetGlobalRect();
                    Control? card = holder.CardNode;
                    Transform2D cardTransform = card?.GetGlobalTransformWithCanvas() ?? Transform2D.Identity;
                    _before.Add(new Baseline(holder, rect.GetCenter(), rect.Size, holder.Scale.X, holder.RotationDegrees,
                        card, cardTransform.Origin, cardTransform.Scale.X, cardTransform.Rotation));
                }
            _running = true;
            RenderingServer.FramePostDraw += Sample;
        }

        public void Finish()
        {
            if (!_running) return;
            RenderingServer.FramePostDraw -= Sample;
            _running = false;
        }

        private void Sample()
        {
            if (!_running) return;
            Frames++;
            foreach (Baseline before in _before)
            {
                NHandCardHolder holder = before.Holder;
                if (!GodotObject.IsInstanceValid(holder) || !holder.IsInsideTree() || !_hand.ActiveHolders.Contains(holder))
                {
                    Missing = true;
                    if (WorstCard.Length == 0) WorstCard = holder.CardModel?.Id.Entry ?? "card";
                    continue;
                }
                if (before.Card is not { } visual || !GodotObject.IsInstanceValid(visual) || !visual.IsInsideTree())
                {
                    Missing = true;
                    if (WorstCard.Length == 0) WorstCard = holder.CardModel?.Id.Entry ?? "card";
                    continue;
                }
                Rect2 rect = holder.Hitbox.GetGlobalRect();
                float shift = rect.GetCenter().DistanceTo(before.Center);
                float size = Mathf.Max(Mathf.Abs(rect.Size.X - before.Size.X), Mathf.Abs(rect.Size.Y - before.Size.Y));
                float scale = Mathf.Abs(holder.Scale.X - before.Scale);
                float rotation = Mathf.Abs(holder.RotationDegrees - before.Rotation);
                // What is actually drawn: the card node's canvas transform includes the holder's fan transform and any
                // scale the press puts on the node itself, so a native target step or a growth leak shows here even when
                // the hitbox and holder do not move.
                Transform2D cardTransform = visual.GetGlobalTransformWithCanvas();
                float cardShift = cardTransform.Origin.DistanceTo(before.CardOrigin);
                float cardScale = Mathf.Abs(cardTransform.Scale.X - before.CardScale);
                float cardRotation = Mathf.RadToDeg(Mathf.Abs(Mathf.AngleDifference(cardTransform.Rotation, before.CardRotation)));
                if (shift > WorstShift || size > WorstSize || scale > WorstScale || rotation > WorstRotation
                    || cardShift > WorstCardShift || cardScale > WorstCardScale || cardRotation > WorstCardRotation)
                {
                    WorstShift = Mathf.Max(WorstShift, shift);
                    WorstSize = Mathf.Max(WorstSize, size);
                    WorstScale = Mathf.Max(WorstScale, scale);
                    WorstRotation = Mathf.Max(WorstRotation, rotation);
                    WorstCardShift = Mathf.Max(WorstCardShift, cardShift);
                    WorstCardScale = Mathf.Max(WorstCardScale, cardScale);
                    WorstCardRotation = Mathf.Max(WorstCardRotation, cardRotation);
                    WorstCard = holder.CardModel?.Id.Entry ?? "card";
                    WorstFrame = Frames;
                }
            }
        }

        public string Describe() =>
            $"frames={Frames} cards={_before.Count} worstShift={WorstShift:F1} worstSize={WorstSize:F1} " +
            $"worstScale={WorstScale:F3} worstRotation={WorstRotation:F2} " +
            $"worstCardShift={WorstCardShift:F1} worstCardScale={WorstCardScale:F3} worstCardRotation={WorstCardRotation:F2} " +
            $"card={WorstCard} frame={WorstFrame} missing={Missing} hand={_hand.ActiveHolders.Count}";
    }

    private readonly record struct HolderSnapshot(NHandCardHolder Holder, Rect2 Rect, float Scale);

    /// <summary>The hand as card instances, in order: the same cards in the same places.</summary>
    private static bool SameCards(NPlayerHand hand, IReadOnlyList<NHandCardHolder> before) =>
        hand.ActiveHolders.Count == before.Count
        && hand.ActiveHolders.Select(h => h.CardModel).SequenceEqual(before.Select(h => h.CardModel));

    /// <summary>
    /// The cards above the vertical middle of the hand: the upper (back) row the launcher fills first. A one-row hand comes
    /// back whole, so callers never index into an empty row.
    /// </summary>
    private static List<NHandCardHolder> UpperRow(IReadOnlyList<NHandCardHolder> holders)
    {
        if (holders.Count == 0) return new List<NHandCardHolder>();
        float min = holders.Min(h => h.Hitbox.GetGlobalRect().GetCenter().Y);
        float max = holders.Max(h => h.Hitbox.GetGlobalRect().GetCenter().Y);
        if (max - min < 150f) return holders.ToList();
        float middle = (min + max) * 0.5f;
        return holders.Where(h => h.Hitbox.GetGlobalRect().GetCenter().Y < middle).ToList();
    }

    /// <summary>
    /// The cards below the vertical middle of the hand: the lower (front) row. A one-row hand comes back empty, so a
    /// caller can require two rows without special-casing the count.
    /// </summary>
    private static List<NHandCardHolder> LowerRow(IReadOnlyList<NHandCardHolder> holders)
    {
        if (holders.Count == 0) return new List<NHandCardHolder>();
        float min = holders.Min(h => h.Hitbox.GetGlobalRect().GetCenter().Y);
        float max = holders.Max(h => h.Hitbox.GetGlobalRect().GetCenter().Y);
        if (max - min < 150f) return new List<NHandCardHolder>();
        float middle = (min + max) * 0.5f;
        return holders.Where(h => h.Hitbox.GetGlobalRect().GetCenter().Y >= middle).ToList();
    }

    private static NHandCardHolder RightmostHolder(NPlayerHand hand) =>
        hand.ActiveHolders.OrderBy(h => h.Hitbox.GetGlobalRect().GetCenter().X).Last();

    private static Vector2 PointOnHolder(NHandCardHolder holder, float fx, float fy)
    {
        Control hitbox = holder.Hitbox;
        return hitbox.GetGlobalTransformWithCanvas() * new Vector2(hitbox.Size.X * fx, hitbox.Size.Y * fy);
    }

    // The hitbox is the card-shaped Control (300x422 at scale 1) centred on the holder; NCard's own rect is empty.
    private static Rect2 DraggedCardRect(NHandCardHolder holder) => holder.Hitbox.GetGlobalRect();

    /// <summary>
    /// The card as drawn: NCard is centred on the holder's origin and carries the pressed growth on its own scale, so its
    /// empty rect is not usable. The four transformed corners give the card's on-canvas AABB at any fan rotation.
    /// </summary>
    private static Rect2 DrawnCardRect(NHandCardHolder holder)
    {
        NCard? card = holder.CardNode;
        if (card == null || !GodotObject.IsInstanceValid(card)) return holder.Hitbox.GetGlobalRect();
        Transform2D xform = card.GetGlobalTransformWithCanvas();
        Vector2 half = new(PortraitCombatMath.CardDefaultWidth * 0.5f, PortraitCombatMath.CardDefaultHeight * 0.5f);
        Vector2 a = xform * new Vector2(-half.X, -half.Y);
        Vector2 b = xform * new Vector2(half.X, -half.Y);
        Vector2 c = xform * new Vector2(half.X, half.Y);
        Vector2 d = xform * new Vector2(-half.X, half.Y);
        float left = Mathf.Min(Mathf.Min(a.X, b.X), Mathf.Min(c.X, d.X));
        float top = Mathf.Min(Mathf.Min(a.Y, b.Y), Mathf.Min(c.Y, d.Y));
        float right = Mathf.Max(Mathf.Max(a.X, b.X), Mathf.Max(c.X, d.X));
        float bottom = Mathf.Max(Mathf.Max(a.Y, b.Y), Mathf.Max(c.Y, d.Y));
        return new Rect2(left, top, right - left, bottom - top);
    }

    private static float EffectiveAlpha(NHandCardHolder holder)
    {
        float alpha = holder.Modulate.A * (holder.CardNode?.Modulate.A ?? 1f);
        if (holder.CardNode?.GetNodeOrNull<Control>("%CardContainer") is { } body) alpha *= body.Modulate.A;
        return alpha;
    }

    /// <summary>
    /// How large the card is actually drawn: the holder carries the hand's fan scale and the card node carries the pressed
    /// growth (the game zooms the node itself in <c>UpdateHandFocus</c>). A missing card node gives -1, so it can never pass
    /// a scale check.
    /// </summary>
    private static float VisibleCardScale(NHandCardHolder holder) => holder.Scale.X * CardNodeScale(holder);

    private static float CardNodeScale(NHandCardHolder holder)
    {
        NCard? card = holder.CardNode;
        return card != null && GodotObject.IsInstanceValid(card) ? card.Scale.X : -1f;
    }

    private bool InsideCanvas(Rect2 rect)
    {
        Vector2 size = GetViewportSize();
        return rect.Position.X >= -1f && rect.Position.Y >= -1f
               && rect.End.X <= size.X + 1f && rect.End.Y <= size.Y + 1f;
    }

    private HolderSnapshot[] SnapshotOthers(NPlayerHand hand, NHandCardHolder held) =>
        hand.ActiveHolders.Where(h => h != held)
            .Select(h => new HolderSnapshot(h, h.Hitbox.GetGlobalRect(), h.Scale.X))
            .ToArray();

    private void CheckNeighboursStill(NPlayerHand hand, HolderSnapshot[] before, string label)
    {
        float worstShift = 0f;
        float worstSize = 0f;
        float worstScale = 0f;
        string worst = "";
        foreach (HolderSnapshot snapshot in before)
        {
            if (!GodotObject.IsInstanceValid(snapshot.Holder) || !hand.ActiveHolders.Contains(snapshot.Holder))
            {
                _report.Fail($"touch-combat_{label}_neighbours_stay_put",
                    $"a neighbour left the hand while {label} (hand={hand.ActiveHolders.Count})");
                return;
            }
            Rect2 now = snapshot.Holder.Hitbox.GetGlobalRect();
            float shift = now.GetCenter().DistanceTo(snapshot.Rect.GetCenter());
            float size = Mathf.Max(Mathf.Abs(now.Size.X - snapshot.Rect.Size.X), Mathf.Abs(now.Size.Y - snapshot.Rect.Size.Y));
            float scale = Mathf.Abs(snapshot.Holder.Scale.X - snapshot.Scale);
            worstShift = Mathf.Max(worstShift, shift);
            worstSize = Mathf.Max(worstSize, size);
            worstScale = Mathf.Max(worstScale, scale);
            if (shift > 1f || size > 1f || scale > 0.01f)
                worst = $"{snapshot.Holder.CardModel?.Id.Entry ?? "card"} shift={shift:F1} size={size:F1} scale={scale:F3}";
        }
        _report.Check($"touch-combat_{label}_neighbours_stay_put", worstShift <= 4f && worstSize <= 4f && worstScale <= 0.04f,
            $"worstShift={worstShift:F1} worstSize={worstSize:F1} worstScale={worstScale:F3} {worst}");
    }

    /// <summary>Sends the hover motion the device's touch-to-mouse path produces before a press at a canvas point.</summary>
    private void HoverBeforeTouch(Vector2 canvasPoint)
    {
        Vector2 window = _tree.Root.GetFinalTransform() * canvasPoint;
        _tree.Root.PushInput(new InputEventMouseMotion
        {
            Device = (int)InputEvent.DeviceIdEmulation,
            Position = window,
            GlobalPosition = window,
            Relative = Vector2.Zero,
        }, inLocalCoords: false);
    }

    private static bool IsTargeting()
    {
        try
        {
            return NTargetManager.Instance.IsInSelection;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The inspect screen is created lazily on the first <see cref="NGame.GetInspectCardScreen"/> call, and the screen's
    /// own <c>_Ready</c> close tween leaves it reading visible for a moment. Create and settle it closed before the checks,
    /// so <see cref="InspectOpen"/> only means an inspector opened by a gesture.
    /// </summary>
    private async Task SettleInspectScreenAsync()
    {
        try
        {
            NGame.Instance?.GetInspectCardScreen()?.Close();
        }
        catch
        {
            // Absent in this game build: InspectOpen() stays false.
        }
        await TryWaitAsync(() => !InspectOpen(), "inspect screen settled closed", TimeSpan.FromSeconds(2));
        await WaitFramesAsync(4);
    }

    private bool InspectOpen()
    {
        try
        {
            return NGame.Instance?.GetInspectCardScreen() is { } screen && screen.IsVisibleInTree();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A skill let go over an enemy is played, not a tap on the enemy: its status tips must not open after the release (the
    /// game re-shows a focused creature's tips when the play changes the combat state). A real tap on the enemy afterwards
    /// still opens them.
    /// </summary>
    private async Task TestSkillReleaseOpensNoTipsAsync(NCombatRoom room, NPlayerHand hand, Player player)
    {
        List<NCreature> enemies = LivingEnemies(room);
        NHandCardHolder? skill = hand.ActiveHolders.FirstOrDefault(h => h.CardModel?.Id.Entry == DefendId);
        if (enemies.Count == 0 || skill is null)
        {
            _report.Fail("touch-combat_skill_release_no_enemy_tips", $"enemies={enemies.Count} defend={skill is not null}");
            return;
        }
        NCreature enemy = enemies[0];
        // A status so the enemy has tips to show.
        await PowerCmd.Apply<StrengthPower>(new ThrowingPlayerChoiceContext(), enemy.Entity, 1m, null, null, silent: true);
        await WaitFramesAsync(3);
        bool EnemyTip() => FindAll<NHoverTipSet>(_tree.Root).Any(tip => tip.IsVisibleInTree());
        int handBefore = hand.ActiveHolders.Count;
        Vector2 onEnemy = enemy.Hitbox.GetGlobalRect().GetCenter();
        // Count the frames a tip set is drawn, after the frame is drawn: a set the host takes down in the pre-draw pass but
        // only queues for deletion is still drawn that frame, a flash that a sample taken during the process step misses.
        int drawnTipFrames = 0, drawnFrames = 0;
        Action countDrawn = () =>
        {
            drawnFrames++;
            if (EnemyTip()) drawnTipFrames++;
        };
        await _pointer.DragAsync(skill.Hitbox.GetGlobalRect().GetCenter(), onEnemy, 16);
        RenderingServer.FramePostDraw += countDrawn;
        int tipFrames = 0;
        try
        {
            for (int i = 0; i < 60; i++)
            {
                await WaitFramesAsync(1);
                if (EnemyTip()) tipFrames++;
            }
        }
        finally
        {
            RenderingServer.FramePostDraw -= countDrawn;
        }
        _report.Check("touch-combat_skill_release_no_enemy_tips",
            tipFrames == 0 && drawnTipFrames == 0 && hand.ActiveHolders.Count == handBefore - 1,
            $"framesWithTips={tipFrames}/60 drawnWithTips={drawnTipFrames}/{drawnFrames} hand={handBefore}->{hand.ActiveHolders.Count}");
        await CaptureAsync("touch-combat_09b_skill_released_on_enemy");
        await _pointer.TapAsync(onEnemy);
        bool tapped = await WaitForAsync(EnemyTip, TimeSpan.FromSeconds(1.5));
        _report.Check("touch-combat_enemy_tap_opens_tips", tapped, "a tap on the enemy after the release still opens its tips");
        await ParkPointerAsync();
    }

    private static bool IsTargetedPlayable(NHandCardHolder holder) =>
        holder.CardModel is { } card && card.CanPlay() && card.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly;

    private static List<NCreature> LivingEnemies(NCombatRoom room) => room.CreatureNodes
        .Where(c => GodotObject.IsInstanceValid(c) && !c.Entity.IsPlayer && c.Entity.IsAlive && c.Entity.IsHittable)
        .OrderBy(c => c.Hitbox.GetGlobalRect().Position.X)
        .ToList();

    private static NCreature FrontEnemy(NCombatRoom room) => room.CreatureNodes
        .Where(c => GodotObject.IsInstanceValid(c) && !c.Entity.IsPlayer && c.Entity.IsAlive && c.Entity.IsHittable)
        .OrderByDescending(c => c.Hitbox.GetGlobalRect().End.Y)
        .First();

    /// <summary>The front-most living, hittable enemy with no block, or null when every target would absorb the hit.</summary>
    private static NCreature? UnblockedFrontEnemy(NCombatRoom room) => room.CreatureNodes
        .Where(c => GodotObject.IsInstanceValid(c) && !c.Entity.IsPlayer && c.Entity.IsAlive && c.Entity.IsHittable
                    && c.Entity.Block <= 0)
        .OrderByDescending(c => c.Hitbox.GetGlobalRect().End.Y)
        .FirstOrDefault();

    private readonly record struct ReleaseCandidate(Vector2 Point, float Distance, float Clearance);

    /// <summary>
    /// A release point <paramref name="distance"/> outside <paramref name="enemy"/>'s hitbox, on the side that leaves the most
    /// room from every other enemy. Side-aligned points keep the Euclidean and axis-aligned distance equal, which is what a
    /// padded/snapped hitbox test uses.
    /// </summary>
    private static ReleaseCandidate PickTargetRelease(IReadOnlyList<NCreature> enemies, NCreature enemy,
        float distance, Vector2 canvas)
    {
        Rect2 rect = enemy.Hitbox.GetGlobalRect();
        float midX = rect.GetCenter().X;
        float midY = rect.GetCenter().Y;
        Vector2[] candidates =
        [
            new(rect.Position.X - distance, midY),
            new(rect.End.X + distance, midY),
            new(midX, rect.Position.Y - distance),
            new(midX, rect.End.Y + distance),
        ];

        ReleaseCandidate best = new(new Vector2(canvas.X * 0.5f, canvas.Y * 0.3f), distance, -1f);
        foreach (Vector2 point in candidates)
        {
            if (point.X < 24f || point.Y < 260f || point.X > canvas.X - 24f || point.Y > canvas.Y - 900f) continue;
            if (enemies.Any(other => other != enemy && other.Hitbox.GetGlobalRect().HasPoint(point))) continue;
            float clearance = ClearanceFromOthers(enemies, enemy, point);
            if (clearance > best.Clearance) best = new(point, distance, clearance);
        }
        return best;
    }

    /// <summary>A point in the upper stage that is farther than the snap radius from every living enemy.</summary>
    private static Vector2 FarFromEnemies(IReadOnlyList<NCreature> enemies, Vector2 canvas)
    {
        Vector2 best = new(canvas.X * 0.5f, canvas.Y * 0.22f);
        float bestDistance = -1f;
        for (int ix = 1; ix <= 8; ix++)
        {
            for (int iy = 2; iy <= 6; iy++)
            {
                var point = new Vector2(canvas.X * ix / 9f, canvas.Y * iy / 12f);
                float nearest = float.PositiveInfinity;
                foreach (NCreature enemy in enemies)
                    nearest = Mathf.Min(nearest, DistanceOutside(enemy.Hitbox.GetGlobalRect(), point));
                if (nearest > bestDistance)
                {
                    bestDistance = nearest;
                    best = point;
                }
            }
        }
        return best;
    }

    /// <summary>The gap from a point to every enemy except <paramref name="enemy"/>: infinite when it is alone.</summary>
    private static float ClearanceFromOthers(IReadOnlyList<NCreature> enemies, NCreature enemy, Vector2 point)
    {
        float clearance = float.PositiveInfinity;
        foreach (NCreature other in enemies)
        {
            if (other == enemy) continue;
            clearance = Mathf.Min(clearance, DistanceOutside(other.Hitbox.GetGlobalRect(), point));
        }
        return clearance;
    }

    private static float DistanceOutside(Rect2 rect, Vector2 point)
    {
        float dx = Mathf.Max(0f, Mathf.Max(rect.Position.X - point.X, point.X - rect.End.X));
        float dy = Mathf.Max(0f, Mathf.Max(rect.Position.Y - point.Y, point.Y - rect.End.Y));
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    // ---- waits ----

    private async Task<bool> TryWaitAsync(Func<bool> condition, string description, TimeSpan timeout)
    {
        try
        {
            await WaitUntilAsync(condition, description, timeout);
            return true;
        }
        catch (TimeoutException)
        {
            Log.Warn($"[TEST] touch-combat: timed out waiting for {description}");
            return false;
        }
    }
}
