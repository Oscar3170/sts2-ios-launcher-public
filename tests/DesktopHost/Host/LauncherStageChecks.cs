using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The launcher host's own combat-stage acceptance run (C1/C2/C3): the fixed encounter fixture of the <c>combat-stage</c>
/// scenario, measured against the launcher's portrait bands.
/// <para>
/// Every fight is checked for a stage that stays inside the canvas and under the run bars, one body scale per row, one
/// ground line, legible hit boxes and health bars/intents, a broad floor that ends at the hand rail (a narrow pier under
/// the Underdocks), fixed framing while the hand grows, a card is played, a turn passes, an enemy dies and a creature
/// is summoned into its slot, and clearance: no living creature's body, health bar or intents overlap another's, the
/// player stays a legible size, and a Defect's orbs stay readable. The Kaiser Crab keeps the Crusher left and the Rocket
/// right of the player, with its background body and claws in place.
/// </para>
/// <para>
/// The launcher's stage pass publishes the measurements it owns as node metadata (<c>portrait_peer_scale</c> on the room;
/// <c>portrait_floor_aligned</c>, <c>portrait_art_bottom</c>, <c>portrait_pier</c> and <c>portrait_pier_surface</c> on the
/// arena control); everything else is read from live node transforms and rectangles, then written per encounter to
/// <c>stage_&lt;encounter&gt;_geometry.txt</c> next to the screenshots. <c>STS2_STAGE_ENCOUNTERS</c> takes a comma-separated
/// list of encounter names to rerun one or a few fights.
/// </para>
/// </summary>
internal sealed partial class TestDriver
{
    // One phone point is 2.88 units, so 44 points are 127 units. Every enemy except a back rank stands exactly on the
    // front foot line, a dominant boss included; back ranks are 118-250 units above it.
    private const float StageFrontRankWindow = 48f;
    private const float StageGroundTolerance = 8f;
    private const float StageFloorTolerance = 4f;
    // Touching rectangles may share up to this many canvas units before they count as an overlap.
    private const float StageClearanceTolerance = 4f;
    // An orb is a touch target and a readable icon: 26 points of an iPhone 11 Pro, in canvas units. Up to five orbs must
    // keep that size; six to ten may be smaller but stay legible (Batch C owns the compact ten-orb arc).
    private const float OrbMinDiameter = 75f;
    private const float OrbDenseMinDiameter = 50f;
    // The gap an orb icon keeps from the canvas side and bottom edges, and the run bars above it.
    private const float OrbEdgeMargin = 8f;
    // On the compact canvas the bodies fill the stage: a tall dominant boss's head and the fixed intents above it may
    // reach up over the relic band.
    private const float StageCompactReach = 240f;

    /// <summary>
    /// The lowest line a body or an intent row may reach in this room. The room publishes its own floor
    /// (<c>portrait_hud_floor_y</c>: the potion row's bottom + 10, the real end of the run header, which sits under the
    /// relic band); only a room without one falls back to the stage top with the host's body allowance.
    /// </summary>
    private static float StageFloor(NCombatRoom room, PortraitCombatBands bands) =>
        room.HasMeta("portrait_hud_floor_y")
            ? room.GetMeta("portrait_hud_floor_y").AsSingle()
            : bands.StageTopY - (bands.Compact ? StageCompactReach : 6f);

    private static readonly string[] StageEncounters =
    {
        "SlimesNormal", "CeremonialBeastBoss", "PhrogParasiteElite", "KaiserCrabBoss", "OvicopterNormal",
        "ExoskeletonsNormal", "KnowledgeDemonBoss", "TheKinBoss", "TestSubjectBoss", "SludgeSpinnerWeak",
        "CorpseSlugsNormal", "CultistsNormal", "PhantasmalGardenersElite", "WaterfallGiantBoss", "ToadpolesWeak",
        "DecimillipedeElite", "TwoTailedRatsNormal",
    };

    // Encounters in this fixture whose monsters add creatures with a native summon move. Verified in
    // .native-reference/v0.113.1/decompiled-full and, where the decompile has no source, by reflecting sts2.dll:
    //  - OvicopterNormal: Ovicopter's LAY_EGGS_MOVE (SummonIntent) calls CreatureCmd.Add<ToughEgg>.
    //  - TwoTailedRatsNormal: TwoTailedRat's MoveState("CALL_FOR_BACKUP_MOVE", CallForBackup, new SummonIntent())
    //    (private const _callForBackupMoveId = "CALL_FOR_BACKUP_MOVE"; UseOnlyOnce, enabled after
    //    _turnsUntilSummonable = 2). CallForBackup calls CreatureCmd.Add and plays "two_tail_rats_summon".
    // PhrogParasiteElite adds creatures too, but on its death rather than on a move: the Phrog's InfestedPower(4)
    // AfterDeath adds the four Wrigglers to wriggler1-4, hidden until the death animation ends. That is a death branch of
    // its own (CheckPhrogParasiteInfestAsync) and stays out of this list, which only drives the grow-the-roster turns.
    // The other fifteen encounters add no creature: SlimesNormal (Leaf/Twig slimes have no split/add move),
    // CeremonialBeastBoss (PlowPower only), KaiserCrabBoss (fixed crusher/rocket pair), ExoskeletonsNormal (first..fourth
    // fixed), PhantasmalGardenersElite (four PhantasmalGardener in first..fourth, no spawn), KnowledgeDemonBoss (Curse of
    // Knowledge picks a card; no spawn), TheKinBoss (KinFollower/KinPriest apply self powers), TestSubjectBoss
    // (AdaptablePower revives the same body), SludgeSpinnerWeak (Weak/Strength self powers), CorpseSlugsNormal (Ravenous
    // self power), CultistsNormal, WaterfallGiantBoss (SteamEruption self power), ToadpolesWeak, DecimillipedeElite
    // (segments reattach; the decimillipede scenario covers it).
    private static readonly string[] NativeSummonEncounters = { "OvicopterNormal", "TwoTailedRatsNormal" };

    // The game exposes no "flies" flag on MonsterModel or NCreature (checked .native-reference); the only monsters named
    // for flight (OwlMagistrate, ThievingHopper) are not in this fixture. Ovicopter visibly hovers, so it is exempt from
    // the ground line explicitly; every other creature in these seventeen fights is treated as standing on the floor.
    private static readonly string[] FlyingMonsters = { "Ovicopter" };
    private const float GroundHorizonTolerance = 4f;
    // The tall formation lifts a pair's first enemy by about this many launcher units; the stage may publish
    // the exact offset as portrait_formation_offset.
    private const float StageFormationRaise = 64f;
    private static readonly string[] FormationOffsetMetaNames =
        { "portrait_formation_offset", "portrait_rank_offset", "portrait_pair_offset" };
    private static readonly string[] HorizonMetaNames = { "portrait_floor_horizon", "portrait_horizon" };

    // The stage publishes each creature's assigned foot line as portrait_feet_y; the first value seen is the stable
    // assignment every later phase must keep. Assignments are per room (Godot instance ids can be reused after a room).
    private readonly Dictionary<ulong, float> _stageFeetAssignments = new();
    private ulong _stageFeetRoomId;

    private partial bool IsPortraitHost() => SafeAreaCanvas.IsPortrait;

    // The act whose rooms are configured. The fixture's encounter order visits the same act in runs (Overgrowth, Hive, ...),
    // and ConfigureTourActAsync regenerates the act's rooms; only a change of act needs that now.
    private string? _configuredStageAct;

    /// <summary>
    /// The launcher's answer for the shared orbs scenario's clearance hook: it runs the same body/bar/intent, player-size
    /// and orb-readability checks the stage fixture uses after every egg spawn and hatch.
    /// </summary>
    partial void CheckOrbEncounterClearance(string name, NCombatRoom room)
    {
        _ = room;
        CheckCreatureClearance(name);
    }

    private partial Rect2 PortraitStageFrame()
    {
        float canvasHeight = Math.Max(_tree.Root.GetViewport().GetVisibleRect().Size.Y, SafeAreaCanvas.MinPortraitHeight);
        float capHeight = PortraitLayout.ComputeCapHeight(_tree.Root);
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, capHeight + PortraitBands.RunContentTop);
        return new Rect2(0f, bands.StageTopY, SafeAreaCanvas.PortraitWidth, bands.HandTopY - bands.StageTopY);
    }

    /// <summary>
    /// The launcher's answer for the status fixture: where the power icons sit against the hand-control rail
    /// (<c>PortraitCombatBands.RailTopY</c>) and the hand rows below <c>HandTopY</c>. Information only while the 9:16 space
    /// question is open: the step always passes and reports the deepest icon and its overflow in canvas units, and the
    /// geometry file carries the band, the deepest icon, how much of it lies in the band and the overflow.
    /// </summary>
    partial void CheckStatusBands(string step, Rect2 canvas, IReadOnlyList<Rect2> iconRects, List<string> lines)
    {
        float canvasHeight = Math.Max(canvas.Size.Y, SafeAreaCanvas.MinPortraitHeight);
        float capHeight = PortraitLayout.ComputeCapHeight(_tree.Root);
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, capHeight + PortraitBands.RunContentTop);
        var band = new Rect2(0f, bands.RailTopY, canvas.Size.X, Math.Max(0f, canvas.Size.Y - bands.RailTopY));
        Rect2 worst = default;
        float worstFraction = 0f;
        foreach (Rect2 icon in iconRects)
        {
            Rect2 covered = band.Intersection(icon);
            float iconArea = Math.Max(1f, icon.Size.X * icon.Size.Y);
            float fraction = covered.Size.X * covered.Size.Y / iconArea;
            if (fraction <= worstFraction) continue;
            worstFraction = fraction;
            worst = icon;
        }
        float deepestBottom = iconRects.Count == 0 ? bands.RailTopY : iconRects.Max(icon => icon.End.Y);
        float railOverflow = Math.Max(0f, deepestBottom - bands.RailTopY);
        float handOverflow = Math.Max(0f, deepestBottom - bands.HandTopY);
        lines.Add($"bands=stageTop:{Q(bands.StageTopY, 1)} frontFeet:{Q(bands.FrontFeetY, 1)} " +
                  $"railTop:{Q(bands.RailTopY, 1)} handTop:{Q(bands.HandTopY, 1)} compact:{bands.Compact} " +
                  $"band={Q(band)} deepestIcon={Q(worst)} deepestBottom:{Q(deepestBottom, 1)} " +
                  $"iconBandFraction:{Q(worstFraction, 3)} railOverflow:{Q(railOverflow, 1)} " +
                  $"handOverflow:{Q(handOverflow, 1)}");
        _report.Pass(step + "_band_clearance",
            $"{iconRects.Count} power icon(s); deepest bottom {Q(deepestBottom, 1)} is " +
            $"{Q(railOverflow, 1)} units below railTop {Q(bands.RailTopY, 1)} " +
            $"(handTop {Q(bands.HandTopY, 1)}, hand overflow {Q(handOverflow, 1)}; " +
            $"deepest icon {Q(worstFraction * 100f, 1)}% in the band) — information only");
    }

    /// <summary>Entry for <see cref="LauncherHostRunner"/>'s <c>stage-encounters</c> scenario.</summary>
    internal static async Task RunStageScenarioAsync(TestOptions options, TestReport report, ScreenshotService screenshots,
        VirtualPointer pointer)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Log.Error("Test harness requires a SceneTree.");
            return;
        }

        await new TestDriver(tree, options, report, screenshots, pointer).RunStageEncountersInternalAsync();
    }

    private async Task RunStageEncountersInternalAsync()
    {
        // Seventeen fights, each with a full turn, a death and a summon. The shared driver's six minutes is far too short;
        // run.sh's STS2MOBILE_TEST_TIMEOUT is the real bound and is checked before every encounter.
        _cancellation.CancelAfter(TimeSpan.FromMinutes(30));
        await WaitForMainMenuPreloadAsync();
        RunState state = await StartRunAsync();
        Player me = state.Players[0];
        me.Creature.SetMaxHpInternal(9999);
        me.Creature.SetCurrentHpInternal(me.Creature.MaxHp);
        Directory.CreateDirectory(_options.OutputDirectory);
        bool portrait = SafeAreaCanvas.IsPortrait;
        _report.Pass("stage_host_layout", portrait
            ? "portrait: encounter geometry and interaction checks"
            : "landscape: native encounter reference captures (no portrait geometry assertions)");

        if (_options.Scenario == "stage-osty")
        {
            await RunStageEncounterAsync(state, me, "SludgeSpinnerWeak", portrait);
            return;
        }

        string[] requested = OS.GetEnvironment("STS2_STAGE_ENCOUNTERS")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] unknown = requested
            .Where(name => !StageEncounters.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0)
            _report.Fail("stage_unknown_encounters",
                $"{string.Join(", ", unknown)}; known: {string.Join(", ", StageEncounters)}");
        string[] cases = requested.Length == 0
            ? StageEncounters
            : StageEncounters.Where(name => requested.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (cases.Length == 0)
        {
            _report.Fail("stage_no_encounters", "STS2_STAGE_ENCOUNTERS matched no known encounter");
            return;
        }

        double deadline = StageDeadlineSeconds();
        foreach (string name in cases)
        {
            if (PastStageDeadline(deadline))
            {
                _report.Fail("stage_stopped_at_deadline",
                    $"stopped before {name}; the {cases.Length}-encounter fixture needs STS2MOBILE_TEST_TIMEOUT=1200 " +
                    $"(currently {deadline + 20f:F0}s). Set STS2_STAGE_ENCOUNTERS to run fewer fights.");
                break;
            }
            try
            {
                await RunStageEncounterAsync(state, me, name, portrait);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                // One encounter's native behaviour must not take the rest of the fixture with it.
                _report.Fail(name + "_stage_exception", error.Message);
                Log.Error($"[TEST] stage check for {name} failed: {error}");
                try
                {
                    await RecoverStageEncounterAsync(name);
                }
                catch (Exception recoveryError) when (recoveryError is not OperationCanceledException)
                {
                    Log.Error($"[TEST] could not clear {name} before the next encounter: {recoveryError}");
                }
            }
        }
    }

    /// <summary>
    /// Best effort after a failed encounter: answer or dismiss a native choose-a-card screen (an enemy turn can block on
    /// one), then let the room settle so the fixture can enter the next encounter.
    /// </summary>
    private async Task RecoverStageEncounterAsync(string name)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            NChooseACardSelectionScreen? screen = FindAll<NChooseACardSelectionScreen>(_tree.Root)
                .FirstOrDefault(s => GodotObject.IsInstanceValid(s) && s.IsVisibleInTree() && !s.IsQueuedForDeletion());
            if (screen is null) break;
            if (!await AnswerStageChoiceAsync(name + "_recovery", screen)) break;
        }
        // Let the abandoned turn finish (or the fight end) before the next debug room replaces this one.
        var settle = System.Diagnostics.Stopwatch.StartNew();
        while (CombatManager.Instance.IsInProgress && !IsPlayerPlayPhase() && settle.Elapsed < ShortWait)
            await WaitFramesAsync(1);
        await WaitSecondsAsync(0.5);
        _report.Pass(name + "_stage_failure_recovered", "native screens cleared; continuing with the next encounter");
    }

    private async Task RunStageEncounterAsync(RunState state, Player me, string name, bool portrait)
    {
        ActModel act = ModelDb.Acts.First(a =>
            new[] { "Overgrowth", "Underdocks", "Hive", "Glory" }.Contains(a.GetType().Name)
            && a.AllEncounters.Any(e => e.GetType().Name == name));
        _report.Pass(name + "_stage_act", act.GetType().Name);
        string actName = act.GetType().Name;
        if (_configuredStageAct != actName)
        {
            await ConfigureTourActAsync(state, actName);
            _configuredStageAct = actName;
        }
        else
        {
            Log.Info($"[TEST] {name}: reusing the configured {actName} act");
        }
        me.Creature.SetCurrentHpInternal(me.Creature.MaxHp);

        EncounterModel encounter = state.Act.AllEncounters.First(e => e.GetType().Name == name).ToMutable();
        await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: encounter, showTransition: false);
        await WaitUntilAsync(IsPlayerPlayPhase, name + " player phase", TimeSpan.FromSeconds(45));
        _report.Phase("combat_entered", name);
        // The old 4.5 s settle existed to let the room, hand, intents and entry animations finish. Wait for that state
        // directly: the turn banner self-frees after its display (so the initial capture still has no banner), the hand
        // is dealt, and no native creature/hand/intent tween is running for two consecutive frames.
        await WaitUntilStableAsync(StageIntroSignature, name + " intro settle", TimeSpan.FromSeconds(4.5));
        NCombatRoom room = NCombatRoom.Instance
            ?? throw new InvalidOperationException($"Combat room disappeared during {name}.");

        if (_options.Scenario == "stage-osty")
        {
            await CheckOstyGrowthAsync(name, room, me, portrait);
            return;
        }

        if (portrait)
            await CheckStageEncounterAsync(name, act, room, me);
        else
            await CaptureAsync("stage_" + name + "_initial");
    }

    private async Task CheckOstyGrowthAsync(string name, NCombatRoom room, Player player, bool portrait)
    {
        var context = new ThrowingPlayerChoiceContext();
        await OstyCmd.Summon(context, player, 10m, null);
        await WaitUntilStableAsync(CombatSettledSignature, name + " Osty summon", ShortWait);
        NCreature osty = room.CreatureNodes.Single(c => c.Entity.IsPet && c.Entity.Monster?.GetType().Name == "Osty");
        Vector2 origin = osty.Position;
        Vector2 visualScale = osty.Visuals.Scale;
        float maxSlide = 0f, maxGrowth = 0f;
        int frames = 0;
        void Sample()
        {
            maxSlide = Math.Max(maxSlide, origin.DistanceTo(osty.Position));
            maxGrowth = Math.Max(maxGrowth, visualScale.DistanceTo(osty.Visuals.Scale));
            frames++;
        }
        RenderingServer.FramePostDraw += Sample;
        try
        {
            // Grow the existing, placed pet through the actual native summon command.
            await OstyCmd.Summon(context, player, 60m, null);
            await WaitUntilStableAsync(CombatSettledSignature, name + " Osty growth", ShortWait);
            await _pointer.TapAsync(CenterOf(room.Ui.EndTurnButton));
            await WaitUntilAsync(() => CombatManager.Instance.PlayerActionsDisabled, name + " Osty enemy turn", ShortWait);
            await WaitThroughEnemyTurnAsync(name);
            await WaitUntilStableAsync(StageIntroSignature, name + " Osty next turn", ShortWait);
        }
        finally
        {
            RenderingServer.FramePostDraw -= Sample;
        }
        _report.Check(name + "_osty_growth", frames >= 10 && maxGrowth > 0.05f,
            $"frames={frames} visualScaleDelta={maxGrowth:F3}");
        _report.Check(name + "_osty_position", portrait ? maxSlide <= 1f : maxSlide > 1f,
            $"drawnFrames={frames} maxRootDisplacement={maxSlide:F2} portrait={portrait}");
        await CaptureAsync("stage_" + name + "_osty");
    }

    /// <summary>Pending until the room and hand are ready and the entry/turn-banner presentation has stopped.</summary>
    private string? StageIntroSignature()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        if (room is null || !GodotObject.IsInstanceValid(room) || !room.IsInsideTree()) return null;
        // The old 4.5 s settle always outlived the combat-start/turn banners; waiting for them keeps captures banner-free.
        if (room.GetChildren().Any(child => child is NCombatStartBanner or NPlayerTurnBanner or NEnemyTurnBanner))
            return Pending("room.banner");
        if (!IsPlayerPlayPhase()) return Pending("room.playPhase");
        if (room.Ui?.Hand is not { } hand || hand.ActiveHolders.Count == 0) return Pending("room.hand");
        return CombatSettledSignature();
    }

    private async Task CheckStageEncounterAsync(string name, ActModel act, NCombatRoom room, Player me)
    {
        float canvasHeight = Math.Max(_tree.Root.GetViewport().GetVisibleRect().Size.Y, SafeAreaCanvas.MinPortraitHeight);
        float capHeight = PortraitLayout.ComputeCapHeight(_tree.Root);
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, capHeight + PortraitBands.RunContentTop);
        _report.Check(name + "_stage_bands",
            bands.StageTopY < bands.FrontFeetY && bands.FrontFeetY < bands.RailTopY && bands.RailTopY < bands.HandTopY,
            $"canvas={canvasHeight} cap={capHeight} stageTop={bands.StageTopY} frontFeet={bands.FrontFeetY} " +
            $"railTop={bands.RailTopY} handTop={bands.HandTopY} compact={bands.Compact}");

        NCreature player = room.CreatureNodes.First(c => c.Entity.IsPlayer);
        List<NCreature> enemies = StageEnemies(room);
        _report.Check(name + "_stage_enemies", enemies.Count > 0,
            $"enemies={string.Join(", ", enemies.Select(e => e.Entity.Monster?.GetType().Name))}");

        Control bgContainer = room.GetNode<Control>("%BgContainer");
        Control? arena = bgContainer.GetChildren().OfType<Control>().FirstOrDefault();
        Transform2D background = bgContainer.GetGlobalTransform();
        Transform2D arenaTransform = arena?.GetGlobalTransform() ?? background;
        var creatureTransforms = room.CreatureNodes.ToDictionary(c => c.GetInstanceId(), c => c.GetGlobalTransform());
        Node2D? crab = room.Background.GetNodeOrNull<Node2D>("%KaiserCrab");
        Transform2D crabTransform = crab?.GetGlobalTransform() ?? Transform2D.Identity;
        bool underdocks = act.GetType().Name == "Underdocks";
        bool pier = arena is not null && arena.HasMeta("portrait_pier") && arena.GetMeta("portrait_pier").AsBool();

        CheckStageGeometry(name, room, player, enemies, bands, canvasHeight, pier);
        CheckStageFloor(name, arena, bands, underdocks);
        CheckBackdropProportion(name, room, bgContainer, arena);
        CheckCreatureClearance(name);
        if (name == "KaiserCrabBoss") CheckKaiserCrab(name, room, player, enemies);
        WriteStageGeometry(name, room, bands, canvasHeight, capHeight, pier);
        await CaptureAsync("stage_" + name + "_initial");
        await CheckBlockHpReadableAsync(name, room);

        await DrawStageHandAsync(me, room);
        // Bound 1.2 s: NHandCardHolder eases to its layout target with per-frame Lerps and only snaps at <1 unit, so the
        // old 0.6 s fixed wait ended mid-convergence (that was the 14/14 after-draw fallback). The wait returns as soon as
        // the holders snap, normally ~0.6-0.8 s.
        await WaitUntilStableAsync(CombatSettledSignature, name + " after draw", TimeSpan.FromSeconds(1.2));
        _report.Pass(name + "_hand_after_draw", $"cards={room.Ui.Hand.ActiveHolders.Count}");
        CheckStageStable(name + "_after_draw", bgContainer, background, arena, arenaTransform, creatureTransforms, crab, crabTransform);
        CheckCreatureClearance(name + "_after_draw");
        await CaptureAsync("stage_" + name + "_hand");

        await PlayStageCardsAsync(name, room, bands);
        await WaitUntilStableAsync(CombatSettledSignature, name + " after play", TimeSpan.FromSeconds(1.2));
        CheckStageStable(name + "_after_play", bgContainer, background, arena, arenaTransform, creatureTransforms, crab, crabTransform);
        CheckCreatureClearance(name + "_after_play");
        await CaptureAsync("stage_" + name + "_after_play");

        if (!CombatManager.Instance.IsInProgress)
        {
            _report.Pass(name + "_stage_encounter_ended", "the played cards finished the fight before the turn step");
            return;
        }

        await _pointer.TapAsync(CenterOf(room.Ui.EndTurnButton));
        await WaitUntilAsync(() => CombatManager.Instance.PlayerActionsDisabled, name + " enemy turn", ShortWait);
        await WaitThroughEnemyTurnAsync(name);
        await WaitUntilStableAsync(() => IsPlayerPlayPhase() ? CombatSettledSignature() : null,
            name + " after turn", TimeSpan.FromSeconds(1.5));
        enemies = StageEnemies(room);
        pier = arena is not null && arena.HasMeta("portrait_pier") && arena.GetMeta("portrait_pier").AsBool();
        CheckStageGeometry(name + "_after_turn", room, player, enemies, bands, canvasHeight, pier);
        CheckStageFloor(name + "_after_turn", arena, bands, underdocks);
        CheckCreatureClearance(name + "_after_turn");
        if (name == "KaiserCrabBoss") CheckKaiserCrab(name + "_after_turn", room, player, enemies);
        CheckStageStable(name + "_after_turn", bgContainer, background, arena, arenaTransform, creatureTransforms, crab, crabTransform);
        WriteStageGeometry(name + "_after_turn", room, bands, canvasHeight, capHeight, pier);
        await CaptureAsync("stage_" + name + "_after_turn");

        // The encounter's own summon move adds creatures during the enemy turn (Ovicopter's LAY_EGGS_MOVE adds ToughEggs).
        // Record it and run the post-summon checks there; the death step then has nothing synthetic to add.
        List<NCreature> summoned = room.CreatureNodes
            .Where(c => GodotObject.IsInstanceValid(c) && !creatureTransforms.ContainsKey(c.GetInstanceId())
                        && c.Entity.IsMonster && c.Entity.IsAlive)
            .ToList();
        bool summonsNatively = NativeSummonEncounters.Contains(name, StringComparer.Ordinal);
        bool nativeSummonSeen = summonsNatively && summoned.Count > 0;
        if (nativeSummonSeen)
        {
            _report.Pass(name + "_stage_native_summon",
                $"the enemy turn added {summoned.Count} creature(s): " +
                string.Join(", ", summoned.Select(c => c.Entity.Monster?.GetType().Name)));
            CheckStageStable(name + "_after_summon", bgContainer, background, arena, arenaTransform, creatureTransforms,
                crab, crabTransform);
            CheckCreatureClearance(name + "_after_summon");
            CheckCreatureGrounding(name + "_after_summon", room, bands, pier);
            if (HasFullPublishedFeet(room)) CheckPublishedFeet(name + "_after_summon", room, bands, pier);
            WriteStageGeometry(name + "_after_summon", room, bands, canvasHeight, capHeight, pier);
            await CaptureAsync("stage_" + name + "_after_summon");
        }

        // The Phrog Parasite's death is this room's own branch (its InfestedPower adds the Wrigglers). The generic step
        // would instead pick the leftmost surviving Wriggler once the Phrog is gone and measure slots that are hidden.
        if (name == "PhrogParasiteElite")
            await CheckPhrogParasiteInfestAsync(name, room, bands, canvasHeight, underdocks, bgContainer, background,
                arena, arenaTransform, crab, crabTransform);
        else
            await CheckStageDeathAndSummonAsync(name, room, bands, canvasHeight, bgContainer, background, arena,
                arenaTransform, crab, crabTransform, summonsNatively, nativeSummonSeen);

        if (name is "OvicopterNormal" or "TwoTailedRatsNormal")
            await RunGrowthTurnsAsync(name, room, bands, canvasHeight, capHeight, pier);
    }

    // ---- block shield vs HP digits ----------------------------------------------------------------------------------

    // The native block shield (%BlockContainer) is drawn at the health bar's left end and covers the first digit of the HP
    // text once the bar is narrow enough (the Underdocks' four thin Phantasmal Gardeners are the reported case). The
    // launcher moves the label clear of that band while block is shown, so this pass grants block through the game's own
    // command, gives the enemies the HP-number shapes the user needs to see, waits for the native AnimateInBlock tween,
    // measures the drawn glyph ink against the shield's drawn ink, and saves a 3x crop. HP and block are put back before
    // the encounter's later steps, which depend on them.
    private static readonly FieldInfo? BlockTweenField = AccessTools.Field(typeof(NHealthBar), "_blockTween");
    // A 3-digit current under a 3-digit max, then the widest 3/3-digit pair: assigned to an enemy by its index in the room.
    // The hero keeps the fixture's 4/4-digit "9999/9999".
    private static readonly (int Max, int Current)[] BlockHpSamples = { (150, 123), (188, 188) };
    private const int BlockHpGrant = 3;
    // A touching rect is not readable: the glyph ink clears the shield's ink by at least this many canvas units.
    private const float BlockHpGap = 1f;
    // Below this applied label scale the number is reported as hard to read (information only: the user's rule lets the
    // number shrink while block is shown, this is the point where it is worth flagging).
    private const float BlockHpScaleFloor = 0.7f;

    /// <summary>
    /// Every creature showing an HP number gets a 3-digit enemy sample (the hero keeps its 4-digit fixture HP) and block
    /// through <see cref="CreatureCmd.GainBlock"/>. For each one the pass asserts that the label's drawn glyph ink (outline
    /// included) is disjoint from the shield's drawn ink and that the ink's horizontal span stays inside its own bar, then
    /// reports the shift and the label scale the launcher applied (the number shrinks while block is shown when it cannot
    /// fit beside the shield; a scale below <see cref="BlockHpScaleFloor"/> is flagged as information). The ink is taller
    /// than the native bar band by design (about 62 units against 21.3), so vertical overflow is ignored. When even the
    /// launcher's minimum scale floor cannot make the number fit, the creature carries <c>portrait_hp_shift_capped</c> and the
    /// overflow is reported instead of failed. A 3x crop of the tightest creature is saved, and the HP and block are restored
    /// so the draw/play/turn/death steps see the fight as before.
    /// </summary>
    private async Task CheckBlockHpReadableAsync(string name, NCombatRoom room)
    {
        List<NCreature> labelled = room.CreatureNodes
            .Where(c => GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion() && c.Entity.IsAlive
                        && BlockHpLabel(c) is not null)
            .ToList();
        if (labelled.Count == 0)
        {
            _report.Fail(name + "_block_hp_granted", "no creature in the room shows an HP number");
            return;
        }

        var restoreHp = new List<(NCreature Node, int Max, int Current)>();
        int enemyIndex = 0;
        foreach (NCreature creature in labelled)
        {
            if (!creature.Entity.IsMonster) continue;
            (int max, int current) = BlockHpSamples[enemyIndex++ % BlockHpSamples.Length];
            if (creature.Entity.MaxHp != max || creature.Entity.CurrentHp != current)
            {
                restoreHp.Add((creature, creature.Entity.MaxHp, creature.Entity.CurrentHp));
                // Max first: a current HP above a shrinking max is not a valid creature state.
                creature.Entity.SetMaxHpInternal(max);
                creature.Entity.SetCurrentHpInternal(current);
            }
            // The HP text and the shield's number are written by the native RefreshBlockUi/RefreshText, which the game runs
            // on a combat-state change rather than on the block or HP event itself.
            RefreshHpBar(creature);
        }
        await WaitFramesAsync(1);
        // The number's place and size before any block is shown: the shift and the shrink are measured against it.
        var nativeInk = new Dictionary<ulong, Rect2>();
        foreach (NCreature creature in labelled)
            if (BlockHpLabel(creature) is { } unblocked)
                nativeInk[creature.GetInstanceId()] = LauncherLabelGlyphRect(unblocked);

        var restoreBlock = new List<(NCreature Node, int Prior)>();
        foreach (NCreature creature in labelled)
        {
            if (creature.Entity.Block <= 0)
            {
                restoreBlock.Add((creature, creature.Entity.Block));
                await CreatureCmd.GainBlock(creature.Entity, BlockHpGrant, ValueProp.Unpowered, null, fast: true);
            }
            RefreshHpBar(creature);
        }
        await WaitUntilStableAsync(BlockShieldSettled, name + " block shield settle", TimeSpan.FromSeconds(1.5));
        await WaitFramesAsync(1);

        var lines = new List<string>();
        var scales = new List<string>();
        var belowFloor = new List<string>();
        bool readable = true, shown = true;
        float tightestGap = float.MaxValue;
        Rect2 tightestRegion = default;
        foreach (NCreature creature in labelled)
        {
            string who = creature.Entity.Monster?.GetType().Name ?? "player";
            Label? label = BlockHpLabel(creature);
            Control? bar = BlockHpBar(creature);
            Control? shield = BlockShield(creature);
            TextureRect? icon = shield?.GetNodeOrNull<TextureRect>("BlockIcon");
            Label? shieldLabel = shield?.GetNodeOrNull<Label>("BlockLabel");
            if (label is null || bar is null || shield is null || icon is null)
            {
                shown = readable = false;
                lines.Add($"{who}: label={label is not null} bar={bar is not null} shield={shield is not null} " +
                          $"icon={icon is not null}");
                continue;
            }
            Rect2 barRect = bar.GetGlobalRect();
            Rect2 hpInk = LauncherLabelGlyphRect(label);
            Rect2 shieldInk = LauncherDrawnInk(icon);
            if (shieldLabel is not null && !string.IsNullOrWhiteSpace(shieldLabel.Text))
                shieldInk = shieldInk.Merge(LauncherLabelGlyphRect(shieldLabel));
            Rect2 beforeInk = nativeInk.TryGetValue(creature.GetInstanceId(), out Rect2 native) ? native : hpInk;
            bool visible = shield.IsVisibleInTree() && creature.Entity.Block > 0;
            float gap = LauncherRectGap(hpInk, shieldInk);
            // Horizontal span only: the ink is taller than the native bar band by design, so vertical overflow is ignored.
            // The number stays inside its own bar and, when it cannot fit beside the shield, it shrinks until it does (only
            // while block is shown) - the user's rule, so passing the bar's right end is a failure. The one documented
            // exception is the launcher's minimum scale floor (0.50 at the time of writing, independent of this check's
            // 0.70 reporting threshold): when even the floor cannot fit, the label keeps the floor, the ink
            // overflows, and the creature carries portrait_hp_shift_capped (need/scale/inkRight/barRight) - report that as
            // information instead of failing, so the step still flags it if the meta is absent.
            bool inside = hpInk.Position.X >= barRect.Position.X - BlockHpGap
                          && hpInk.End.X <= barRect.End.X + BlockHpGap;
            bool centred = hpInk.GetCenter().X >= barRect.Position.X - BlockHpGap
                           && hpInk.GetCenter().X <= barRect.End.X + BlockHpGap;
            string? capped = creature.HasMeta("portrait_hp_shift_capped")
                ? creature.GetMeta("portrait_hp_shift_capped").ToString()
                : null;
            float shiftedBy = hpInk.Position.X - beforeInk.Position.X;
            float inkScale = beforeInk.Size.X > 0.5f ? hpInk.Size.X / beforeInk.Size.X : 1f;
            float barScale = MathF.Max(0.0001f, bar.GetGlobalTransform().Scale.X);
            float appliedScale = label.GetGlobalTransform().Scale.X / barScale;
            float rightClearance = barRect.End.X - hpInk.End.X;
            scales.Add($"{who}:{appliedScale:F2}");
            if (appliedScale < BlockHpScaleFloor || inkScale < BlockHpScaleFloor)
                belowFloor.Add($"{who} scale={appliedScale:F2} inkScale={inkScale:F2}");
            shown &= visible;
            readable &= visible && gap >= BlockHpGap && centred && (inside || capped is not null);
            lines.Add($"{who} hp='{label.Text}' block={creature.Entity.Block} " +
                      $"bar=({barRect.Position.X:F0},{barRect.Size.X:F0}) label=({hpInk.Position.X:F0}," +
                      $"{hpInk.Size.X:F0}) shield=({shieldInk.Position.X:F0},{shieldInk.Size.X:F0}) " +
                      $"gap={gap:F1} shift={shiftedBy:F1} scale={appliedScale:F2} inkScale={inkScale:F2} " +
                      $"insideBar={inside} rightClearance={rightClearance:F1} " +
                      $"capped={capped ?? "none"} shieldShown={visible}");
            if (visible && gap < tightestGap)
            {
                tightestGap = gap;
                tightestRegion = barRect.Merge(shieldInk).Merge(hpInk).Grow(10f);
            }
        }
        string detail = string.Join(" | ", lines);
        string tightest = tightestGap < float.MaxValue ? $"{tightestGap:F1}" : "n/a";
        _report.Check(name + "_block_hp_granted", shown,
            $"{labelled.Count} HP number(s), block={BlockHpGrant} granted on {restoreBlock.Count}, " +
            $"HP sampled on {restoreHp.Count}: {detail}");
        _report.Pass(name + "_block_hp_label_scale",
            $"{labelled.Count} label scale(s) {string.Join(", ", scales)}" +
            (belowFloor.Count == 0
                ? $"; none below {BlockHpScaleFloor:F1}"
                : $"; below {BlockHpScaleFloor:F1} (info only): {string.Join(", ", belowFloor)}"));
        _report.Check(name + "_block_hp_readable", readable,
            readable
                ? $"HP number clear of the native block shield and inside its bar horizontally for {labelled.Count} " +
                  $"creature(s); tightest gap {tightest} (bound {BlockHpGap:F1}); {detail}"
                : detail);
        await CaptureBlockHpCropAsync(name, tightestRegion);

        foreach ((NCreature node, int max, int current) in restoreHp)
        {
            if (!GodotObject.IsInstanceValid(node) || node.IsQueuedForDeletion() || !node.Entity.IsAlive) continue;
            node.Entity.SetMaxHpInternal(max);
            node.Entity.SetCurrentHpInternal(current);
            RefreshHpBar(node);
        }
        foreach ((NCreature node, int prior) in restoreBlock)
        {
            if (!GodotObject.IsInstanceValid(node) || node.IsQueuedForDeletion() || !node.Entity.IsAlive) continue;
            int drop = node.Entity.Block - prior;
            if (drop > 0) node.Entity.LoseBlockInternal(drop);
            RefreshHpBar(node);
        }
        await WaitFramesAsync(2);
    }

    /// <summary>
    /// Re-runs the native health-bar refresh by its own method name (block shield visibility and number, HP text). The game
    /// runs it on a combat-state change, so a block or HP write made by the fixture has to ask for it.
    /// </summary>
    private static void RefreshHpBar(NCreature creature) =>
        creature.GetNodeOrNull<Control>("HealthBar/HealthBar")?.Call("RefreshValues");

    /// <summary>Pending while a native block shield is still sliding in (NHealthBar.AnimateInBlock runs for 0.5 s).</summary>
    private static string? BlockShieldSettled()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        if (room is null || !GodotObject.IsInstanceValid(room) || !room.IsInsideTree()) return null;
        foreach (NCreature creature in room.CreatureNodes)
        {
            if (!GodotObject.IsInstanceValid(creature) || creature.IsQueuedForDeletion()) continue;
            if (creature.GetNodeOrNull<Control>("HealthBar/HealthBar") is { } hpBar && TweenRunning(hpBar, BlockTweenField))
                return Pending("block." + creature.GetInstanceId());
        }
        return "block.settled";
    }

    /// <summary>The creature's visible HP number, or null when its bar is hidden or shows no text.</summary>
    private static Label? BlockHpLabel(NCreature creature)
    {
        Label? label = creature.GetNodeOrNull<Label>("HealthBar/HealthBar/HpBarContainer/HpLabel");
        return label is not null && label.IsVisibleInTree() && !string.IsNullOrWhiteSpace(label.Text) ? label : null;
    }

    private static Control? BlockHpBar(NCreature creature) =>
        creature.GetNodeOrNull<Control>("HealthBar/HealthBar/HpBarContainer");

    private static Control? BlockShield(NCreature creature) =>
        creature.GetNodeOrNull<Control>("HealthBar/HealthBar/BlockContainer");

    /// <summary>The tightest bar, shield and HP digits at 3x, next to the room's other captures.</summary>
    private async Task CaptureBlockHpCropAsync(string name, Rect2 region)
    {
        if (region.Size.X < 8f || region.Size.Y < 8f) return;
        await WaitFramesAsync(2);
        Image frame = _screenshots.ReadRenderedFrame("block_hp");
        try
        {
            float scale = frame.GetWidth() / (float)SafeAreaCanvas.PortraitWidth;
            Rect2I crop = LauncherPixelRect(region, scale, frame.GetWidth(), frame.GetHeight());
            if (crop.Size.X < 8 || crop.Size.Y < 8)
            {
                _report.Fail("screenshot:" + name + "_block_hp", $"empty crop {crop} from region {region}");
                return;
            }
            Image enlarged = frame.GetRegion(crop);
            try
            {
                enlarged.Resize(crop.Size.X * 3, crop.Size.Y * 3, Image.Interpolation.Bilinear);
                string file = "stage_" + name + "_block_hp";
                _report.Screenshot(file, _screenshots.SaveFrame(file, enlarged));
            }
            finally
            {
                enlarged.Dispose();
            }
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <summary>
    /// Keeps ending turns for a summoner that grows its own roster (Ovicopter eggs/hatchlings, Two-Tailed Rats calling
    /// backup), bounded by an encounter-slot-full streak, a rat cap, or eight enemy turns. After every enemy turn the full
    /// stage checks run, existing bodies must not move or rescale, and the roster is captured to the geometry file.
    /// </summary>
    private async Task RunGrowthTurnsAsync(string name, NCombatRoom room, PortraitCombatBands bands, float canvasHeight,
        float capHeight, bool pier)
    {
        bool rats = name == "TwoTailedRatsNormal";
        int slotCapacity = EncounterSlotCount(room);
        bool wasFull = false;
        for (int turn = 1; turn <= 8; turn++)
        {
            // A combat keeps IsInProgress true while it is ending (the last primary enemy died and the win is being
            // processed), so a fight that is already over must be recognised before tapping a button that no longer
            // takes input. The growth turns need a live player turn; they never wait for one that cannot come.
            if (!await WaitForGrowthTurnAsync(name, turn))
            {
                bool over = CombatManager.Instance.IsOverOrEnding;
                _report.Fail(name + (over ? "_stage_growth_ended" : "_stage_growth_stalled"),
                    over
                        ? $"the fight ended before growth turn {turn}; growth turns did not run " +
                          $"(inProgress={CombatManager.Instance.IsInProgress} ending={CombatManager.Instance.IsEnding})"
                        : $"no player turn arrived for growth turn {turn} after {ShortWait.TotalSeconds:F0}s");
                break;
            }
            Dictionary<ulong, Transform2D> before = room.CreatureNodes
                .Where(c => GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion())
                .ToDictionary(c => c.GetInstanceId(), c => c.GetGlobalTransform());
            await _pointer.TapAsync(CenterOf(room.Ui.EndTurnButton));
            await WaitUntilAsync(() => CombatManager.Instance.PlayerActionsDisabled || CombatManager.Instance.IsOverOrEnding,
                name + " growth turn " + turn, ShortWait);
            if (!await WaitThroughEnemyTurnAsync(name + " growth turn " + turn, stopWhenFightEnds: true))
            {
                _report.Fail(name + "_stage_growth_ended",
                    $"growth turn {turn} ended the fight; the later growth turns and their checks did not run");
                return;
            }
            await WaitSecondsAsync(1.5);
            if (!CombatManager.Instance.IsInProgress || CombatManager.Instance.IsEnding)
            {
                _report.Fail(name + "_stage_growth_ended",
                    $"growth turn {turn} left no living primary enemy; the growth checks did not run");
                break;
            }

            string phase = name + "_turn_" + turn;
            NCreature player = room.CreatureNodes.First(c => c.Entity.IsPlayer);
            List<NCreature> current = StageEnemies(room);
            CheckStageGeometry(phase, room, player, current, bands, canvasHeight, pier);
            CheckCreatureClearance(phase);
            CheckStageSurvivorsStable(phase, before);
            if (rats) CheckRatsShareOneScale(phase, current);
            _report.Pass(phase + "_roster",
                $"turn={turn} enemies={current.Count} types=" +
                string.Join(",", current.Select(c => c.Entity.Monster?.GetType().Name).GroupBy(t => t)
                    .Select(g => g.Key + "x" + g.Count())));
            WriteStageGeometry(phase, room, bands, canvasHeight, capHeight, pier);
            await CaptureAsync("stage_" + name + "_turn_" + turn);

            bool full = !rats && slotCapacity > 0 && current.Count >= slotCapacity;
            if (full && wasFull) break;
            wasFull = full;
            if (rats && current.Count >= 6) break;
        }
    }

    /// <summary>
    /// The growth turns need a live player turn. A fight that is over or ending (IsInProgress stays true while the win is
    /// processed) answers false, as does a combat that never hands the turn back, so the caller records a clear outcome
    /// instead of tapping a dead End Turn button and timing out.
    /// </summary>
    private async Task<bool> WaitForGrowthTurnAsync(string name, int turn)
    {
        if (CombatManager.Instance.IsOverOrEnding) return false;
        try
        {
            await WaitUntilAsync(() => IsPlayerPlayPhase() || CombatManager.Instance.IsOverOrEnding,
                name + " growth turn " + turn, ShortWait);
        }
        catch (TimeoutException)
        {
            return false;
        }
        return IsPlayerPlayPhase();
    }

    /// <summary>The number of authored encounter slot markers (the roster the stage froze the room at).</summary>
    private static int EncounterSlotCount(NCombatRoom room)
    {
        if (AccessTools.Property(typeof(NCombatRoom), "EncounterSlots")?.GetValue(room) is not Node slots)
            return 0;
        int count = 0;
        foreach (Node child in slots.GetChildren())
            if (child is Marker2D) count++;
        return count;
    }

    /// <summary>All Two-Tailed Rats are peers: one body scale, and no rat is picked as a boss.</summary>
    private void CheckRatsShareOneScale(string phase, List<NCreature> enemies)
    {
        List<NCreature> rats = enemies
            .Where(e => e.Entity.Monster?.GetType().Name == "TwoTailedRat").ToList();
        if (rats.Count == 0) return;
        float[] scales = rats.Select(e => e.GetGlobalTransform().Scale.X).ToArray();
        float ratio = scales.Max() / Math.Max(1e-3f, scales.Min());
        NCreature? boss = PickStageBoss(enemies);
        _report.Check(phase + "_rats_shared_scale", boss is null && ratio <= 1.06f,
            $"rats={rats.Count} scales={string.Join(", ", scales.Select(s => s.ToString("F3")))} ratio={ratio:F3} " +
            $"bossExcluded={boss?.Entity.Monster?.GetType().Name ?? "none"}");
    }

    /// <summary>
    /// Waits out an enemy turn. Some enemies (the Knowledge Demon's Curse of Knowledge) open the game's choose-a-card
    /// screen during their move and block until the player answers; those are answered through the host's select-then-confirm
    /// path (first tap selects, second tap takes). With <paramref name="stopWhenFightEnds"/> a
    /// combat that is over or ending answers false instead of waiting for a player turn the game will never give.
    /// </summary>
    private async Task<bool> WaitThroughEnemyTurnAsync(string name, bool stopWhenFightEnds = false)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!IsPlayerPlayPhase())
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            if (stopWhenFightEnds && CombatManager.Instance.IsOverOrEnding) return false;
            if (stopwatch.Elapsed > MediumWait)
                throw new TimeoutException($"Timed out waiting for {name} next player turn after {MediumWait.TotalSeconds:F0}s.");
            NChooseACardSelectionScreen? screen = FindAll<NChooseACardSelectionScreen>(_tree.Root)
                .FirstOrDefault(s => GodotObject.IsInstanceValid(s) && s.IsVisibleInTree() && !s.IsQueuedForDeletion());
            if (screen is not null && await AnswerStageChoiceAsync(name, screen))
                continue;
            await WaitFramesAsync(1);
        }
        return true;
    }

    /// <summary>Answers the native choose-a-card screen with its first card; returns whether a card was taken.</summary>
    private async Task<bool> AnswerStageChoiceAsync(string name, NChooseACardSelectionScreen screen)
    {
        if (!GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree()) return false;
        NGridCardHolder? holder = FindAll<NGridCardHolder>(screen)
            .FirstOrDefault(h => GodotObject.IsInstanceValid(h) && h.IsVisibleInTree() && !h.IsQueuedForDeletion());
        if (holder is null) return false;
        string card = holder.CardModel?.GetType().Name ?? holder.Name.ToString();
        await _pointer.TapAsync(CenterOf(holder.Hitbox));
        try
        {
            await WaitUntilAsync(() => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree()
                    || ReferenceEquals(PhoneSelectConfirm.SelectedOwner, holder),
                name + " choice card selected", ShortWait);
        }
        catch (TimeoutException)
        {
            return false;
        }
        if (GodotObject.IsInstanceValid(screen) && screen.IsVisibleInTree()
            && ReferenceEquals(PhoneSelectConfirm.SelectedOwner, holder))
            await _pointer.TapAsync(CenterOf(holder.Hitbox));
        try
        {
            await WaitUntilAsync(() => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree(),
                name + " choice closes", ShortWait);
        }
        catch (TimeoutException)
        {
            return false;
        }
        _report.Pass(name + "_native_turn_choice", $"answered {card} on the game's choose-a-card screen");
        await WaitSecondsAsync(0.4);
        return true;
    }

    /// <summary>
    /// Kills one enemy the fight survives (a secondary enemy when the encounter has one, else the leftmost), then
    /// exercises the encounter's own summon move. Only encounters whose monsters add creatures natively are chased (up to
    /// four enemy turns); every other fight records that it has no native summon. The survivors-and-framing checks always
    /// run after the death.
    /// </summary>
    private async Task CheckStageDeathAndSummonAsync(string name, NCombatRoom room, PortraitCombatBands bands, float canvasHeight,
        Control bgContainer, Transform2D background, Control? arena, Transform2D arenaTransform, Node2D? crab,
        Transform2D crabTransform, bool summonsNatively, bool nativeSummonSeen)
    {
        List<NCreature> initial = StageEnemies(room);
        if (initial.Count == 0 || !CombatManager.Instance.IsInProgress) return;
        // The death must keep the fight alive: a secondary enemy (MinionPower, e.g. the Ovicopter's ToughEggs) can never
        // be the last living primary, so its death cannot make the combat end. The Ovicopter is this room's only primary,
        // so it is never the victim; a room without a secondary enemy keeps the old leftmost pick.
        NCreature victim = initial.FirstOrDefault(c => c.Entity.IsSecondaryEnemy) ?? initial[0];
        _report.Pass(name + "_stage_death_target",
            $"killed {victim.Entity.SlotName ?? victim.Entity.Monster?.GetType().Name} " +
            $"secondary={victim.Entity.IsSecondaryEnemy} primaries={initial.Count(c => c.Entity.IsPrimaryEnemy)}");
        var survivors = room.CreatureNodes
            .Where(c => c != victim && GodotObject.IsInstanceValid(c))
            .ToDictionary(c => c.GetInstanceId(), c => c.GetGlobalTransform());

        await CreatureCmd.Kill(victim.Entity, force: true);
        await WaitSecondsAsync(1.8);
        if (initial.Count > 1)
            _report.Check(name + "_stage_death_kept_fight", !CombatManager.Instance.IsOverOrEnding,
                $"victim={victim.Entity.Monster?.GetType().Name} secondary={victim.Entity.IsSecondaryEnemy} " +
                $"inProgress={CombatManager.Instance.IsInProgress} ending={CombatManager.Instance.IsEnding}");
        CheckStageSurvivorsStable(name + "_after_death", survivors);
        CheckStageFramingStable(name + "_after_death", bgContainer, background, arena, arenaTransform, crab, crabTransform);
        CheckCreatureClearance(name + "_after_death");
        await CaptureAsync("stage_" + name + "_after_death");

        if (!summonsNatively)
        {
            _report.Pass(name + "_stage_summon_skipped", "no native summon in this encounter");
            return;
        }
        if (nativeSummonSeen)
        {
            _report.Pass(name + "_stage_summon_skipped",
                "native summon observed on the enemy turn; post-summon checks ran there");
            return;
        }
        if (initial.Count == 1 || !CombatManager.Instance.IsInProgress)
        {
            _report.Fail(name + "_stage_native_summon", "the death ended the fight before a summon turn");
            return;
        }

        // Native summon chase: keep ending turns until the enemy's summon move adds a creature, bounded so a bad move
        // roll cannot hang the fixture.
        var known = room.CreatureNodes.Select(c => c.GetInstanceId()).ToHashSet();
        for (int turn = 0; turn < 4; turn++)
        {
            if (!CombatManager.Instance.IsInProgress) break;
            await _pointer.TapAsync(CenterOf(room.Ui.EndTurnButton));
            await WaitUntilAsync(() => CombatManager.Instance.PlayerActionsDisabled, name + " summon turn " + (turn + 1), ShortWait);
            await WaitThroughEnemyTurnAsync(name);
            await WaitSecondsAsync(1.2);
            List<NCreature> added = room.CreatureNodes
                .Where(c => GodotObject.IsInstanceValid(c) && !known.Contains(c.GetInstanceId())
                            && c.Entity.IsMonster && c.Entity.IsAlive)
                .ToList();
            if (added.Count > 0)
            {
                CheckStageSurvivorsStable(name + "_after_summon", survivors);
                CheckStageFramingStable(name + "_after_summon", bgContainer, background, arena, arenaTransform, crab, crabTransform);
                CheckCreatureClearance(name + "_after_summon");
                bool pier = arena is not null && arena.HasMeta("portrait_pier") && arena.GetMeta("portrait_pier").AsBool();
                CheckCreatureGrounding(name + "_after_summon", room, bands, pier);
                if (HasFullPublishedFeet(room)) CheckPublishedFeet(name + "_after_summon", room, bands, pier);
                _report.Pass(name + "_stage_native_summon",
                    $"enemy turn {turn + 1} added {added.Count} creature(s): " +
                    string.Join(", ", added.Select(c => c.Entity.Monster?.GetType().Name)));
                WriteStageGeometry(name + "_after_summon", room, bands, canvasHeight,
                    PortraitLayout.ComputeCapHeight(_tree.Root),
                    arena is not null && arena.HasMeta("portrait_pier") && arena.GetMeta("portrait_pier").AsBool());
                await CaptureAsync("stage_" + name + "_after_summon");
                return;
            }
            known = room.CreatureNodes.Select(c => c.GetInstanceId()).ToHashSet();
        }
        _report.Fail(name + "_stage_native_summon", "no creature was added within 4 enemy turns");
    }

    /// <summary>
    /// The Phrog Parasite's death branch. The Phrog is the room's only starting enemy; on its death its InfestedPower(4)
    /// adds four stunned Wrigglers to the reserved slots wriggler1-4, hidden until the death animation ends, and
    /// <c>ShouldStopCombatFromEnding</c> keeps the fight alive. The four revealed lanes stand on the front foot line, the
    /// hero and the framing must not have moved or rescaled, and the room's own checks run again as <c>_after_infest_*</c>.
    /// </summary>
    private async Task CheckPhrogParasiteInfestAsync(string name, NCombatRoom room, PortraitCombatBands bands,
        float canvasHeight, bool underdocks, Control bgContainer, Transform2D background, Control? arena,
        Transform2D arenaTransform, Node2D? crab, Transform2D crabTransform)
    {
        NCreature player = room.CreatureNodes.First(c => c.Entity.IsPlayer);
        Transform2D playerBefore = player.GetGlobalTransform();
        NCreature? phrog = room.CreatureNodes.FirstOrDefault(c =>
            GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion()
            && c.Entity.IsAlive && c.Entity.Monster?.GetType().Name == "PhrogParasite");
        // The played cards can finish the Phrog early; its death branch still runs here, so the survivors are captured
        // whether or not there is a body left to kill.
        var survivors = room.CreatureNodes
            .Where(c => c != phrog && GodotObject.IsInstanceValid(c))
            .ToDictionary(c => c.GetInstanceId(), c => c.GetGlobalTransform());
        if (phrog is not null)
        {
            _report.Pass(name + "_stage_death_target",
                $"killed {phrog.Entity.SlotName ?? "phrog"} secondary=False; InfestedPower adds the Wrigglers");
            await CreatureCmd.Kill(phrog.Entity, force: true);
            await WaitSecondsAsync(1.8);
        }
        else
        {
            _report.Pass(name + "_stage_death_target",
                "the played cards already killed the PhrogParasite; its infest branch still runs");
        }

        bool phrogDead = phrog is null || phrog.Entity.IsDead;
        bool fightContinues = phrogDead && CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsOverOrEnding;
        _report.Check(name + "_phrog_kept_fight", fightContinues,
            $"phrogDead={phrogDead} inProgress={CombatManager.Instance.IsInProgress} " +
            $"ending={CombatManager.Instance.IsEnding} roster=" +
            string.Join(", ", room.CreatureNodes.Select(c =>
                c.Entity.SlotName ?? c.Entity.Monster?.GetType().Name ?? "player")));
        if (!fightContinues)
        {
            await CaptureAsync("stage_" + name + "_after_infest_stopped");
            return;
        }

        try
        {
            await WaitUntilAsync(() => StageWrigglers(room) is { Count: 4 } revealed
                                       && revealed.All(w => w.Visible && w.IsVisibleInTree()),
                name + " infested wrigglers revealed", ShortWait);
        }
        catch (TimeoutException error)
        {
            _report.Fail(name + "_infest_spawned", $"{error.Message} wrigglers=" +
                string.Join(", ", room.CreatureNodes
                    .Where(c => c.Entity.Monster?.GetType().Name == "Wriggler")
                    .Select(c => $"{c.Entity.SlotName}: alive={c.Entity.IsAlive} visible={c.Visible} " +
                                 $"inTree={c.IsVisibleInTree()}")));
            await CaptureAsync("stage_" + name + "_infest_timeout");
            return;
        }
        // The reveal is the last step of the death sequence; let the four lanes settle before measuring them.
        await WaitUntilStableAsync(CombatSettledSignature, name + " after infest", TimeSpan.FromSeconds(1.5));

        List<NCreature> wrigglers = StageWrigglers(room)
            .OrderBy(w => w.Entity.SlotName ?? string.Empty, StringComparer.Ordinal)
            .ToList();
        _report.Pass(name + "_infest_spawned", string.Join(", ", wrigglers.Select(w =>
            $"{w.Entity.SlotName} hp={w.Entity.CurrentHp}/{w.Entity.MaxHp} visible={w.Visible}")));
        string[] slots = wrigglers.Select(w => w.Entity.SlotName ?? string.Empty).ToArray();
        string[] reserved = { "wriggler1", "wriggler2", "wriggler3", "wriggler4" };
        int authoredSlots = EncounterSlotCount(room);
        _report.Check(name + "_after_infest_reserved_slots",
            slots.Length == 4 && slots.Distinct().Count() == 4
            && slots.OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(reserved)
            && authoredSlots == reserved.Length + 1,
            $"slots={string.Join(", ", slots)} authoredSlots={authoredSlots} " +
            $"expected={string.Join(", ", reserved)} + phrog");
        var feet = wrigglers.Select(w => (Slot: w.Entity.SlotName, Feet: w.Hitbox.GetGlobalRect().End.Y)).ToArray();
        _report.Check(name + "_wriggler_on_front_line",
            feet.Length == 4 && feet.All(f => Math.Abs(f.Feet - bands.FrontFeetY) <= StageFloorTolerance),
            $"frontFeet={bands.FrontFeetY:F1} tolerance={StageFloorTolerance} " +
            string.Join(", ", feet.Select(f => $"{f.Slot}={f.Feet:F1}")));

        List<NCreature> enemies = StageEnemies(room);
        bool pier = arena is not null && arena.HasMeta("portrait_pier") && arena.GetMeta("portrait_pier").AsBool();
        CheckStageGeometry(name + "_after_infest", room, player, enemies, bands, canvasHeight, pier);
        CheckStageFloor(name + "_after_infest", arena, bands, underdocks);
        CheckBackdropProportion(name + "_after_infest", room, bgContainer, arena);
        CheckCreatureClearance(name + "_after_infest");
        CheckStageSurvivorsStable(name + "_after_infest", survivors);
        CheckStageFramingStable(name + "_after_infest", bgContainer, background, arena, arenaTransform, crab, crabTransform);
        Transform2D playerAfter = player.GetGlobalTransform();
        const float playerTolerance = 0.05f;
        _report.Check(name + "_after_infest_player_stable",
            playerBefore.Origin.DistanceTo(playerAfter.Origin) <= playerTolerance
            && playerBefore.X.DistanceTo(playerAfter.X) <= playerTolerance
            && playerBefore.Y.DistanceTo(playerAfter.Y) <= playerTolerance,
            $"before={playerBefore} after={playerAfter} tolerance={playerTolerance}");
        WriteStageGeometry(name + "_after_infest", room, bands, canvasHeight,
            PortraitLayout.ComputeCapHeight(_tree.Root), pier);
        await CaptureAsync("stage_" + name + "_after_infest");
    }

    /// <summary>The living Wrigglers of the Phrog Parasite's infest (one per reserved slot, wriggler1-4).</summary>
    private static List<NCreature> StageWrigglers(NCombatRoom room) => room.CreatureNodes
        .Where(c => GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion()
                    && c.Entity.IsAlive && c.Entity.Monster?.GetType().Name == "Wriggler")
        .ToList();

    private void CheckStageGeometry(string phase, NCombatRoom room, NCreature player, List<NCreature> enemies,
        PortraitCombatBands bands, float canvasHeight, bool pier)
    {
        float[] scales = enemies.Select(e => e.GetGlobalTransform().Scale.X).ToArray();
        Rect2 playerBox = player.Hitbox.GetGlobalRect();
        float playerFloor = StageFloor(room, bands);
        _report.Check(phase + "_player_inside_canvas",
            playerBox.Position.X >= -1f && playerBox.End.X <= SafeAreaCanvas.PortraitWidth + 1f
            && playerBox.Position.Y >= -1f && playerBox.End.Y <= canvasHeight + 1f,
            $"box={playerBox} canvas={SafeAreaCanvas.PortraitWidth}x{canvasHeight}");
        _report.Check(phase + "_player_below_run_bars", playerBox.Position.Y >= playerFloor,
            $"top={playerBox.Position.Y} stageTop={bands.StageTopY} floor={playerFloor:F1}");
        _report.Check(phase + "_peer_scale_recorded", room.HasMeta("portrait_peer_scale"),
            "the stage records the room-wide body scale");
        if (room.HasMeta("portrait_peer_scale"))
        {
            // The recorded value is a multiplier on each slot's own base size (<=1, floored at 0.62): every creature of one
            // depth must move by it, so their bodies share one scale. A boss too large for its frame may be the one body
            // fitted below that.
            float shared = room.GetMeta("portrait_peer_scale").AsSingle();
            bool floored = room.HasMeta("portrait_peer_scale_floored")
                && room.GetMeta("portrait_peer_scale_floored").AsBool();
            bool rowsShared = shared >= PortraitCombatMath.PeerScaleFloor - 0.001f
                && shared <= PortraitCombatMath.PreferredPeerScale + 0.001f;
            // Without the floor, every peer of one depth takes exactly its slot size times the shared multiplier. With the
            // floor, a body too large for its frame is fitted below it, so the per-depth equality no longer holds. The
            // encounter's dominant boss is not a peer (Ovicopter on the compact canvas is one row with its eggs).
            NCreature? boss = PickStageBoss(enemies);
            if (!floored)
            {
                foreach (IGrouping<int, NCreature> row in enemies.Where(e => e != boss).GroupBy(e => e.ZIndex))
                {
                    List<NCreature> members = row.ToList();
                    if (members.Count < 2) continue;
                    float largest = members.Max(e => e.GetGlobalTransform().Scale.X);
                    float smallest = members.Min(e => e.GetGlobalTransform().Scale.X);
                    rowsShared &= largest / Math.Max(1e-3f, smallest) <= 1.06f;
                }
            }
            string bossName = boss?.Entity.Monster?.GetType().Name ?? "none";
            _report.Check(phase + "_peer_scale_shared", rowsShared,
                $"shared={shared:F3} floored={floored} bossExcluded={bossName} " +
                $"scales={string.Join(", ", scales.Select(s => s.ToString("F3")))} " +
                $"rows={string.Join("; ", enemies.GroupBy(e => e.ZIndex).Select(g => $"{g.Key}:{string.Join("/", g.Select(e => e.GetGlobalTransform().Scale.X.ToString("F3")))}"))}");
        }

        foreach (NCreature enemy in enemies)
        {
            Rect2 box = enemy.Hitbox.GetGlobalRect();
            string label = $"{phase}_{enemy.Entity.SlotName ?? enemy.Entity.Monster?.GetType().Name ?? "enemy"}";
            _report.Check(label + "_inside_canvas",
                box.Position.X >= -1f && box.End.X <= SafeAreaCanvas.PortraitWidth + 1f
                && box.Position.Y >= -1f && box.End.Y <= canvasHeight + 1f,
                $"box={box} canvas={SafeAreaCanvas.PortraitWidth}x{canvasHeight}");
            float floor = StageFloor(room, bands);
            _report.Check(label + "_below_run_bars", box.Position.Y >= floor,
                $"top={box.Position.Y} stageTop={bands.StageTopY} floor={floor:F1}");
            _report.Check(label + "_touch_target", box.Size.Y >= PortraitBands.MinTarget - 1f,
                $"height={box.Size.Y} minimum={PortraitBands.MinTarget}");
            CheckStageHud(label, enemy, enemies, bands, room);
        }

        float[] bottoms = enemies.Select(e => e.Hitbox.GetGlobalRect().End.Y).ToArray();
        if (bottoms.Length == 0) return;
        _report.Check(phase + "_feet_above_rail", bottoms.All(b => b <= bands.RailTopY + 1f),
            $"railTop={bands.RailTopY} bottoms={string.Join(", ", bottoms.Select(b => b.ToString("F1")))}");
        // The stage assigns each creature its own foot line (portrait_feet_y): a pair's first is higher, the second a
        // little higher, lone enemies a little raised. Use those assignments when every non-flying creature has one; the
        // old front-line rules are only a fallback for a room that does not publish them.
        bool publishedFeet = HasFullPublishedFeet(room);
        _report.Pass(phase + "_creature_feet_source",
            publishedFeet
                ? "published: portrait_feet_y on every living non-flying creature"
                : "fallback: portrait_feet_y absent (or incomplete); using the front-line rules");
        if (publishedFeet)
            CheckPublishedFeet(phase, room, bands, pier);
        else
            CheckFrontFeetFallback(phase, room, bands, pier, bottoms);
        CheckCreatureGrounding(phase, room, bands, pier);
    }

    /// <summary>Whether every living non-flying creature publishes its own assigned foot line.</summary>
    private static bool HasFullPublishedFeet(NCombatRoom room)
    {
        bool any = false;
        foreach (NCreature creature in room.CreatureNodes.Where(c =>
                     GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion() && c.Entity.IsAlive))
        {
            string type = creature.Entity.Monster?.GetType().Name ?? "player";
            if (FlyingMonsters.Contains(type, StringComparer.Ordinal)) continue;
            if (!creature.HasMeta("portrait_feet_y")) return false;
            any = true;
        }
        return any;
    }

    /// <summary>
    /// Per-creature foot assignment: every non-flying body's hit box bottom must equal its own <c>portrait_feet_y</c> (±4),
    /// every assigned line must lie in [published floor horizon, HandTopY), and each assignment must be unchanged since
    /// the room first published it.
    /// </summary>
    private void CheckPublishedFeet(string phase, NCombatRoom room, PortraitCombatBands bands, bool pier)
    {
        ulong roomId = room.GetInstanceId();
        if (_stageFeetRoomId != roomId)
        {
            _stageFeetAssignments.Clear();
            _stageFeetRoomId = roomId;
        }
        float horizon = GroundHorizon(room, bands, pier, out string horizonSource);
        var offAssignment = new List<string>();
        var offFloor = new List<string>();
        var unstable = new List<string>();
        int counted = 0, flying = 0, compared = 0;
        foreach (NCreature creature in room.CreatureNodes.Where(c =>
                     GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion() && c.Entity.IsAlive))
        {
            string type = creature.Entity.Monster?.GetType().Name ?? "player";
            if (FlyingMonsters.Contains(type, StringComparer.Ordinal))
            {
                flying++;
                continue;
            }
            if (!creature.HasMeta("portrait_feet_y")) continue;
            counted++;
            float assigned = creature.GetMeta("portrait_feet_y").AsSingle();
            float feet = creature.Hitbox.GetGlobalRect().End.Y;
            if (Math.Abs(feet - assigned) > StageFloorTolerance)
                offAssignment.Add($"{type}: feet={feet:F1} assigned={assigned:F1}");
            if (assigned < horizon - GroundHorizonTolerance || assigned >= bands.HandTopY)
                offFloor.Add($"{type}: assigned={assigned:F1}");
            ulong id = creature.GetInstanceId();
            if (_stageFeetAssignments.TryGetValue(id, out float before))
            {
                compared++;
                if (Math.Abs(before - assigned) > 0.5f)
                    unstable.Add($"{type}: {before:F1}->{assigned:F1}");
            }
            else
            {
                _stageFeetAssignments[id] = assigned;
            }
        }
        _report.Check(phase + "_creature_feet_assignment", offAssignment.Count == 0,
            offAssignment.Count == 0
                ? $"{counted} creature(s) stand on their own portrait_feet_y (±{StageFloorTolerance:F0}); " +
                  $"{flying} flying exempt"
                : string.Join(" | ", offAssignment));
        _report.Check(phase + "_creature_feet_on_floor", offFloor.Count == 0,
            offFloor.Count == 0
                ? $"every assigned foot line lies in [{horizon:F1} ({horizonSource}), {bands.HandTopY:F1})"
                : $"horizon={horizon:F1} ({horizonSource}) handTop={bands.HandTopY:F1} " +
                  string.Join(" | ", offFloor));
        if (compared > 0)
            _report.Check(phase + "_creature_feet_stable", unstable.Count == 0,
                unstable.Count == 0
                    ? $"{compared} foot assignment(s) unchanged since first seen"
                    : string.Join(" | ", unstable));
    }

    /// <summary>
    /// The old front-rank rules, used only when the room does not publish portrait_feet_y: a foot is on the base line
    /// (±4) or, when a real raised line exists, on the formation line (±8). Feet are classified by the first matching
    /// line, so a zero offset cannot count one foot on two lines.
    /// </summary>
    private void CheckFrontFeetFallback(string phase, NCombatRoom room, PortraitCombatBands bands, bool pier, float[] bottoms)
    {
        float formationRaise = FormationRaise(room);
        bool hasRaised = formationRaise > 1f;
        float raisedLine = bands.FrontFeetY - formationRaise;
        static bool OnLine(float feet, float line, float tolerance) => Math.Abs(feet - line) <= tolerance;
        float[] frontBottoms = bottoms
            .Where(b => Math.Abs(b - bands.FrontFeetY) <= StageFrontRankWindow
                        || (hasRaised && Math.Abs(b - raisedLine) <= StageGroundTolerance)).ToArray();
        if (frontBottoms.Length == 0)
        {
            _report.Pass(phase + "_front_rank",
                $"fallback: no enemy on the front foot line or its formation line (raise={formationRaise:F0}; " +
                $"a dominant boss may stand alone in its back rank); " +
                $"hitboxes={string.Join(", ", bottoms.Select(b => b.ToString("F1")))}");
            return;
        }
        var baseFeet = new List<float>();
        var raisedFeet = new List<float>();
        var other = new List<float>();
        foreach (float feet in frontBottoms)
        {
            if (OnLine(feet, bands.FrontFeetY, StageFloorTolerance)) baseFeet.Add(feet);
            else if (hasRaised && OnLine(feet, raisedLine, StageGroundTolerance)) raisedFeet.Add(feet);
            else other.Add(feet);
        }
        bool classified = other.Count == 0;
        bool linesStable = (baseFeet.Count < 2 || baseFeet.Max() - baseFeet.Min() <= StageGroundTolerance)
            && (raisedFeet.Count < 2 || raisedFeet.Max() - raisedFeet.Min() <= StageGroundTolerance);
        _report.Pass(phase + "_front_rank",
            $"fallback: {frontBottoms.Length} front-rank body(ies): {baseFeet.Count} on {bands.FrontFeetY:F1}, " +
            $"{raisedFeet.Count} on the formation line {raisedLine:F1} (raise={formationRaise:F0})");
        _report.Check(phase + "_front_feet_one_line", classified && linesStable,
            $"base={string.Join(", ", baseFeet.Select(y => y.ToString("F1")))} " +
            $"raised={string.Join(", ", raisedFeet.Select(y => y.ToString("F1")))} " +
            $"other={string.Join(", ", other.Select(y => y.ToString("F1")))}");
        _report.Check(phase + "_front_feet_on_band", classified,
            $"hitboxes={string.Join(", ", frontBottoms.Select(y => y.ToString("F1")))} " +
            $"band={bands.FrontFeetY:F1} formation={(hasRaised ? raisedLine : bands.FrontFeetY):F1}");
        if (!pier) return;
        NCreature? player = room.CreatureNodes.FirstOrDefault(c =>
            GodotObject.IsInstanceValid(c) && c.Entity.IsPlayer && c.Entity.IsAlive);
        if (player is null) return;
        float playerBottom = player.Hitbox.GetGlobalRect().End.Y;
        _report.Check(phase + "_pier_ground_line",
            frontBottoms.All(b => OnLine(b, bands.FrontFeetY, StageFloorTolerance)
                                  || (hasRaised && OnLine(b, raisedLine, StageGroundTolerance)))
            && Math.Abs(playerBottom - bands.FrontFeetY) <= StageGroundTolerance,
            $"pierSurface={bands.FrontFeetY:F1} formation={(hasRaised ? raisedLine : bands.FrontFeetY):F1} " +
            $"player={playerBottom:F1} front={string.Join(", ", frontBottoms.Select(b => b.ToString("F1")))}");
    }

    /// <summary>
    /// The encounter's dominant boss under the launcher's own rule (<see cref="PortraitCombatMath.PickBossIndex"/>): a
    /// uniquely typed enemy much larger than the rest, in a room with more than two slots and no segment chain. The
    /// Ovicopter is also taken when the rule is inconclusive, since it is the designated flying boss on the compact canvas.
    /// A boss is not a peer, so it is excluded from the per-row shared body scale.
    /// </summary>
    private static NCreature? PickStageBoss(List<NCreature> enemies)
    {
        if (enemies.Count == 0) return null;
        bool segmented = enemies.Any(e =>
            (e.Entity.Monster?.GetType().Name ?? "").Contains("DecimillipedeSegment", StringComparison.Ordinal));
        var candidates = new List<(int Slot, float Area, string Type)>(enemies.Count);
        for (int i = 0; i < enemies.Count; i++)
        {
            Vector2 bounds = enemies[i].Visuals.Bounds.Size;
            candidates.Add((i, Math.Max(1f, bounds.X) * Math.Max(1f, bounds.Y),
                enemies[i].Entity.Monster?.GetType().Name ?? "unknown"));
        }
        int bossSlot = PortraitCombatMath.PickBossIndex(candidates, Math.Max(enemies.Count, 1), segmented);
        if (bossSlot >= 0 && bossSlot < enemies.Count) return enemies[bossSlot];
        return enemies.FirstOrDefault(e => FlyingMonsters.Contains(
            e.Entity.Monster?.GetType().Name ?? "", StringComparer.Ordinal));
    }

    private void CheckStageHud(string label, NCreature enemy, List<NCreature> enemies, PortraitCombatBands bands,
        NCombatRoom room)
    {
        List<Rect2> hud = EnemyHud(enemy);
        // The room's own floor: published as portrait_hud_floor_y (the potion row's bottom + 10, the real end of the run
        // header, which sits under the relic band). The Ovicopter keeps its intents just under the potion row at 9:19.5,
        // beside the relic icons, so the stage top must not be used as a tighter line.
        float hudFloor = StageFloor(room, bands);
        _report.Check(label + "_hud_readable",
            hud.Count > 0 && hud.All(r => r.Size.X >= 24f && r.Size.Y >= 24f
                && r.Position.X >= -1f && r.End.X <= SafeAreaCanvas.PortraitWidth + 1f
                && r.Position.Y >= hudFloor
                && r.End.Y <= bands.HandTopY),
            $"hud={string.Join("; ", hud)} floor={hudFloor:F0}");

        bool clear = true;
        var detail = new List<string>();
        foreach (NCreature other in enemies.Where(o => o != enemy))
        {
            Rect2 otherBox = other.Hitbox.GetGlobalRect();
            List<Rect2> otherHud = EnemyHud(other);
            if (hud.Any(r => r.Grow(2f).Intersects(otherBox)) || hud.Any(r => otherHud.Any(o => r.Grow(2f).Intersects(o))))
            {
                clear = false;
                detail.Add($"other={other.Entity.SlotName} box={otherBox} hud={string.Join("; ", otherHud)}");
            }
        }
        _report.Check(label + "_hud_clear_of_neighbours", clear,
            clear ? "health bar and intents stay inside this enemy's column" : string.Join(" | ", detail));
    }

    /// <summary>The visible health bar and intent rectangles of one enemy, with layout spacers that have no size left out.</summary>
    private static List<Rect2> EnemyHud(NCreature enemy)
    {
        var hud = new List<Rect2>();
        Control? hpLabel = enemy.GetNodeOrNull<Control>("HealthBar/HealthBar/HpBarContainer/HpLabel");
        if (hpLabel is not null && hpLabel.IsVisibleInTree()) AddSized(hud, hpLabel.GetGlobalRect());
        foreach (Control intent in enemy.IntentContainer.GetChildren().OfType<Control>().Where(c => c.IsVisibleInTree()))
            AddSized(hud, intent.GetGlobalRect());
        return hud;
    }

    private static void AddSized(List<Rect2> rects, Rect2 rect)
    {
        if (rect.Size.X >= 1f && rect.Size.Y >= 1f) rects.Add(rect);
    }

    private void CheckStageFloor(string phase, Control? arena, PortraitCombatBands bands, bool underdocks)
    {
        if (arena is null)
        {
            _report.Fail(phase + "_floor_art", "no arena control under %BgContainer");
            return;
        }
        bool aligned = arena.HasMeta("portrait_floor_aligned") && arena.GetMeta("portrait_floor_aligned").AsBool();
        bool pierMeta = arena.HasMeta("portrait_pier") && arena.GetMeta("portrait_pier").AsBool();
        bool customArena = arena.HasMeta("portrait_custom_arena") && arena.GetMeta("portrait_custom_arena").AsBool();
        float artBottom = arena.HasMeta("portrait_art_bottom") ? arena.GetMeta("portrait_art_bottom").AsSingle() : float.NaN;
        // The launcher lays the Underdocks' own arena out as its narrow pier at every canvas shape; every other act is a
        // broad floor, and so is a boss that brings its own arena scene into that act (the Waterfall Giant).
        _report.Check(phase + "_pier_case", !pierMeta || (underdocks && !customArena),
            $"pier={pierMeta} act={(underdocks ? "Underdocks" : "other")} customArena={customArena}");
        _report.Check(phase + "_pier_recognised", !underdocks || customArena || pierMeta,
            $"the Underdocks pier applies to the act's own arena, not to a custom-background boss " +
            $"(pier={pierMeta}, compact={bands.Compact}, customArena={customArena})");
        if (pierMeta)
        {
            float surface = arena.HasMeta("portrait_pier_surface")
                ? arena.GetMeta("portrait_pier_surface").AsSingle() : float.NaN;
            _report.Check(phase + "_pier_surface", Math.Abs(surface - bands.FrontFeetY) <= StageFloorTolerance,
                $"surface={surface} frontFeet={bands.FrontFeetY}");
            _report.Check(phase + "_pier_not_stretched", !aligned, "the narrow pier is not laid out as a broad floor");
            _report.Check(phase + "_pier_art_fills_band", artBottom >= bands.HandTopY - StageFloorTolerance,
                $"artBottom={artBottom} handTop={bands.HandTopY}");
        }
        else
        {
            _report.Check(phase + "_floor_aligned", aligned, "the broad floor art ends at the hand rail");
            _report.Check(phase + "_floor_ends_at_hand_top", Math.Abs(artBottom - bands.HandTopY) <= StageFloorTolerance,
                $"artBottom={artBottom} handTop={bands.HandTopY}");
            // The arena control itself can be a zero-size holder (its layers carry the art), so the drawn width has to come
            // from the artwork: the widest TextureRect the background shapes.
            TextureRect? art = FindArenaArt(arena, 5);
            Rect2 drawn = art is not null && art.IsVisibleInTree() ? art.GetGlobalRect() : new Rect2();
            _report.Check(phase + "_floor_full_width",
                art is not null && drawn.Position.X <= 2f && drawn.End.X >= SafeAreaCanvas.PortraitWidth - 2f,
                $"art={drawn} node={arena.GetGlobalRect()}");
        }
    }

    /// <summary>The broad arena artwork: the same widest background texture the launcher frames its floor from.</summary>
    private static TextureRect? FindArenaArt(Node? node, int depth)
    {
        if (node is null || depth < 0) return null;
        foreach (Node child in node.GetChildren())
        {
            if (child is TextureRect { Texture: not null } rect && rect.Size.X > 1000f)
                return rect;
            if (FindArenaArt(child, depth - 1) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// Every living creature's body, health bar and visible intents must stay inside the canvas and clear of every other
    /// creature's, and a player's orbs must stay readable (icon diameter and value labels).
    /// </summary>
    private void CheckCreatureClearance(string name)
    {
        // Landscape is the game's native layout: nothing here may impose portrait geometry on it.
        if (!SafeAreaCanvas.IsPortrait)
        {
            _report.Pass(name + "_portrait_checks", "landscape: native layout, portrait checks skipped");
            return;
        }
        if (NCombatRoom.Instance is not { } room) return;
        Rect2 canvas = _tree.Root.GetViewport().GetVisibleRect();
        List<NCreature> creatures = room.CreatureNodes
            .Where(c => GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion() && c.Entity.IsAlive)
            .ToList();
        if (creatures.Count == 0) return;

        var parts = creatures.ToDictionary(c => c.GetInstanceId(), CreatureParts);
        foreach (NCreature creature in creatures)
        {
            string label = $"{name}_{creature.Entity.Monster?.GetType().Name ?? "player"}";
            List<Rect2> rects = parts[creature.GetInstanceId()];
            _report.Check(label + "_parts_inside_canvas",
                rects.Count > 0 && rects.All(r => canvas.Grow(1f).Encloses(r)),
                $"parts={RectList(rects)} canvas={canvas}");
        }

        bool clear = true;
        var overlaps = new List<string>();
        for (int i = 0; i < creatures.Count; i++)
        for (int j = i + 1; j < creatures.Count; j++)
        {
            string left = creatures[i].Entity.Monster?.GetType().Name ?? "player";
            string right = creatures[j].Entity.Monster?.GetType().Name ?? "player";
            List<Rect2> a = parts[creatures[i].GetInstanceId()];
            List<Rect2> b = parts[creatures[j].GetInstanceId()];
            bool pairClear = a.All(ra => b.All(rb => !OverlapsBeyond(ra, rb, StageClearanceTolerance)));
            if (!pairClear)
            {
                clear = false;
                overlaps.Add($"{left} {RectList(a)} vs {right} {RectList(b)}");
            }
        }
        _report.Check(name + "_creature_clearance", clear,
            clear
                ? $"{creatures.Count} living creatures: bodies, health bars and intents clear of each other"
                : string.Join(" | ", overlaps));

        // Status icons hang under each bar and can reach a neighbour's icons: no creature's status block may touch
        // another creature's status block (2-unit tolerance).
        var statusBlocks = creatures
            .Select(c => (Creature: c, Rects: StatusIconRects(c)))
            .Where(entry => entry.Rects.Count > 0)
            .ToList();
        bool statusClear = true;
        string statusWorst = "none";
        float statusWorstArea = 0f;
        for (int i = 0; i < statusBlocks.Count; i++)
        for (int j = i + 1; j < statusBlocks.Count; j++)
        foreach (Rect2 a in statusBlocks[i].Rects)
        foreach (Rect2 b in statusBlocks[j].Rects)
        {
            if (!OverlapsBeyond(a, b, 2f)) continue;
            statusClear = false;
            Rect2 covered = a.Intersection(b);
            float area = covered.Size.X * covered.Size.Y;
            if (area <= statusWorstArea) continue;
            statusWorstArea = area;
            string leftName = statusBlocks[i].Creature.Entity.Monster?.GetType().Name ?? "player";
            string rightName = statusBlocks[j].Creature.Entity.Monster?.GetType().Name ?? "player";
            statusWorst = $"{leftName} icon={a} vs {rightName} icon={b} overlap={covered.Size}";
        }
        _report.Check(name + "_status_blocks_disjoint", statusClear,
            statusClear
                ? $"{statusBlocks.Count} status block(s) disjoint (tolerance 2)"
                : $"worst={statusWorst}");

        CheckPlayerMinimumSize(name, room);
        CheckOrbReadability(name, room);
    }

    /// <summary>The drawn power icons under a creature's health display, one canvas rect per icon.</summary>
    private static List<Rect2> StatusIconRects(NCreature creature)
    {
        Control? display = creature.GetNodeOrNull<Control>("%HealthBar") ?? creature.GetNodeOrNull<Control>("HealthBar");
        Control? powerContainer = display?.GetNodeOrNull<Control>("%PowerContainer")
                                  ?? display?.GetNodeOrNull<Control>("PowerContainer");
        if (powerContainer is null) return new List<Rect2>();
        return powerContainer.GetChildren().OfType<Control>()
            .Where(icon => !icon.IsQueuedForDeletion() && icon.IsVisibleInTree())
            .Select(icon => icon.GetGlobalRect())
            .ToList();
    }

    /// <summary>A player's body must stay a legible touch target and must not be shrunk below the room's body scale floor.</summary>
    private void CheckPlayerMinimumSize(string name, NCombatRoom room)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        float peer = room.HasMeta("portrait_peer_scale") ? room.GetMeta("portrait_peer_scale").AsSingle() : 1f;
        foreach (NCreature player in room.CreatureNodes.Where(c =>
                     GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion() && c.Entity.IsPlayer && c.Entity.IsAlive))
        {
            Rect2 box = player.Hitbox.GetGlobalRect();
            float scale = player.GetGlobalTransform().Scale.X;
            float visual = player.Visuals.Scale.X;
            _report.Check(name + "_player_minimum_size",
                box.Size.Y >= PortraitBands.MinTarget - 1f
                && scale >= PortraitCombatMath.PeerScaleFloor - 0.001f,
                $"height={box.Size.Y:F1} (min {PortraitBands.MinTarget:F0}) scale={scale:F3} visual={visual:F3} " +
                $"roomPeerScale={peer:F3} floor={PortraitCombatMath.PeerScaleFloor:F3}");
        }
    }

    /// <summary>An orb icon is readable at 26 points across, and its passive/evoke value label is visible.</summary>
    private void CheckOrbReadability(string name, NCombatRoom room)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        foreach (NCreature player in room.CreatureNodes.Where(c =>
                     GodotObject.IsInstanceValid(c) && c.Entity.IsPlayer && c.OrbManager is not null))
        {
            NOrb[] orbs = FindAll<NOrb>(player.OrbManager!)
                .Where(o => GodotObject.IsInstanceValid(o) && !o.IsQueuedForDeletion() && o.IsVisibleInTree())
                .ToArray();
            if (orbs.Length == 0) continue;
            var iconRects = new Rect2[orbs.Length];
            var sources = new string[orbs.Length];
            for (int i = 0; i < orbs.Length; i++)
                iconRects[i] = OrbVisualRect(orbs[i], out sources[i]);
            float[] diameters = iconRects.Select(r => Math.Max(r.Size.X, r.Size.Y)).ToArray();
            NOrb[] labelled = orbs.Where(OrbValueLabelsApply).ToArray();
            NOrb[] unlabelled = orbs.Where(o => !OrbValueLabelsApply(o)).ToArray();
            float sizeFloor = orbs.Length <= 5 ? OrbMinDiameter : OrbDenseMinDiameter;
            // One probe per check: the orb's own origin, the rect size source and the resulting rect, so a reported
            // off-canvas rect can be traced to the orb node itself rather than to the measurement.
            _report.Pass(name + "_orb_rect_probe",
                $"orb={orbs[0].Name}({orbs[0].Model?.GetType().Name}) global={orbs[0].GlobalPosition} " +
                $"source={sources[0]} rect={iconRects[0]} shellRect={orbs[0].GetGlobalRect()}");
            _report.Pass(name + "_orb_icon_size",
                $"orbs={orbs.Length} diameters={string.Join(", ", diameters.Select(d => d.ToString("F1")))} " +
                $"floor={sizeFloor:F0} sources={string.Join(",", sources.Distinct())}");
            _report.Check(name + "_orb_icons_readable", diameters.All(d => d >= sizeFloor),
                $"diameters={string.Join(", ", diameters.Select(d => d.ToString("F1")))} minimum={sizeFloor:F0} " +
                $"({(orbs.Length <= 5 ? "up to five orbs" : "six to ten orbs")})");
            _report.Check(name + "_orb_value_labels_visible", labelled.All(OrbLabelsVisible),
                string.Join("; ", labelled.Select(o => $"{o.Name}({o.Model?.GetType().Name}):{OrbLabelsVisible(o)}")));
            if (unlabelled.Length > 0)
                _report.Pass(name + "_orb_value_labels_by_design",
                    $"{unlabelled.Length} orb(s) show no value by design: " +
                    string.Join(", ", unlabelled.Select(o => o.Model?.GetType().Name ?? "empty slot")));

            // Every icon stays in the canvas with an edge margin and clear of the run bars. On a tall phone the bars are
            // the stone, the potion row and the whole relic band; at 9:16 the arc may use the relic band but never the
            // stone header (IOS_PORT_BATCH_BC_DECISIONS.md, decision 10).
            Rect2 canvas = _tree.Root.GetViewport().GetVisibleRect();
            float capHeight = PortraitLayout.ComputeCapHeight(_tree.Root);
            PortraitCombatBands canvasBands = PortraitCombatMath.ComputeCombatBands(
                Math.Max(canvas.Size.Y, SafeAreaCanvas.MinPortraitHeight), capHeight + PortraitBands.RunContentTop);
            float barsBottom = canvasBands.Compact ? capHeight + PortraitBands.TopBarHeight : capHeight + PortraitBands.RunContentTop;
            var bars = new Rect2(0f, 0f, canvas.Size.X, barsBottom);
            bool onStage = true;
            string worst = "none";
            float worstScore = 0f;
            for (int i = 0; i < orbs.Length; i++)
            {
                Rect2 rect = iconRects[i];
                float margin = Math.Min(Math.Min(rect.Position.X, rect.Position.Y),
                    Math.Min(canvas.End.X - rect.End.X, canvas.End.Y - rect.End.Y));
                Rect2 covered = bars.Intersection(rect);
                float score = Math.Max(0f, OrbEdgeMargin - margin) + Math.Max(0f, covered.Size.Y);
                if (score <= 0.001f) continue;
                onStage = false;
                if (score <= worstScore) continue;
                worstScore = score;
                worst = $"{orbs[i].Name}({orbs[i].Model?.GetType().Name}): rect={rect} " +
                        $"edgeMargin={margin:F1} barOverlap={covered.Size}";
            }
            _report.Check(name + "_orb_icons_on_stage", onStage,
                onStage
                    ? $"{orbs.Length} orb icon(s) inside the canvas with {OrbEdgeMargin:F0}-unit margins and below the " +
                      $"run bars (which end at {bars.Size.Y:F0}, {(canvasBands.Compact ? "compact: stone only" : "tall: stone + relic band")})"
                    : $"worst={worst}; bars={bars} canvas={canvas}");

            // Orb icons must also stay clear of enemy bodies, health bars and intents (the arc floats over the stage).
            List<NCreature> enemies = room.CreatureNodes
                .Where(c => GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion()
                            && c.Entity.IsMonster && !c.Entity.IsPet && c.Entity.IsAlive)
                .ToList();
            if (enemies.Count > 0)
            {
                bool clear = true;
                string worstOverlap = "none";
                float worstArea = 0f;
                for (int i = 0; i < orbs.Length; i++)
                {
                    foreach (NCreature enemy in enemies)
                    foreach (Rect2 part in CreatureParts(enemy))
                    {
                        if (!OverlapsBeyond(iconRects[i], part, StageClearanceTolerance)) continue;
                        clear = false;
                        Rect2 covered = iconRects[i].Intersection(part);
                        float area = covered.Size.X * covered.Size.Y;
                        if (area <= worstArea) continue;
                        worstArea = area;
                        worstOverlap = $"{orbs[i].Name}({orbs[i].Model?.GetType().Name}) icon={iconRects[i]} " +
                                       $"vs {enemy.Entity.Monster?.GetType().Name} part={part} overlap={covered.Size}";
                    }
                }
                _report.Check(name + "_orb_enemy_clearance", clear,
                    clear
                        ? $"{orbs.Length} orb icon(s) clear of {enemies.Count} enemy body/bar/intent rect(s)"
                        : $"worst={worstOverlap}");
            }
        }
    }

    /// <summary>The body, health bar and visible intent rectangles of one creature.</summary>
    private static List<Rect2> CreatureParts(NCreature creature)
    {
        var parts = new List<Rect2>();
        AddSized(parts, creature.Hitbox.GetGlobalRect());
        Control? bar = creature.GetNodeOrNull<Control>("HealthBar/HealthBar/HpBarContainer")
            ?? creature.GetNodeOrNull<Control>("HealthBar/HealthBar/HpBarContainer/HpLabel");
        if (bar is not null && bar.IsVisibleInTree()) AddSized(parts, bar.GetGlobalRect());
        foreach (Control intent in creature.IntentContainer.GetChildren().OfType<Control>().Where(c => c.IsVisibleInTree()))
            AddSized(parts, intent.GetGlobalRect());
        return parts;
    }

    /// <summary>True when the two rectangles overlap by more than <paramref name="tolerance"/> on both axes.</summary>
    private static bool OverlapsBeyond(Rect2 a, Rect2 b, float tolerance) =>
        a.Position.X + tolerance < b.End.X && b.Position.X + tolerance < a.End.X
        && a.Position.Y + tolerance < b.End.Y && b.Position.Y + tolerance < a.End.Y;

    private static string RectList(List<Rect2> rects) => string.Join("; ", rects);

    /// <summary>
    /// The orb's drawn icon rectangle in canvas units. The orb scene is a zero-size control whose sprite, outline and
    /// value labels are arranged around its origin, so the icon is centred on the orb node's global position; a child's
    /// global rect only supplies the drawn size (its position is not the drawn orb on this anchored layout).
    /// </summary>
    private static Rect2 OrbVisualRect(NOrb orb, out string source)
    {
        foreach ((string Name, Control? Candidate) in new[]
                 {
                     ("VisualContainer", orb.GetNodeOrNull<Control>("%VisualContainer")),
                     ("Bounds", orb.GetNodeOrNull<Control>("Bounds")),
                     ("Outline", orb.GetNodeOrNull<Control>("%Outline")),
                 })
        {
            if (Candidate is null || !Candidate.IsVisibleInTree()) continue;
            Rect2 child = Candidate.GetGlobalRect();
            float diameter = Math.Max(child.Size.X, child.Size.Y);
            if (diameter <= 1f) continue;
            source = Name;
            Vector2 centre = orb.GlobalPosition;
            return new Rect2(centre - new Vector2(diameter * 0.5f, diameter * 0.5f), new Vector2(diameter, diameter));
        }
        // The game's orb art is 104 units across at scale 1.
        Vector2 scale = orb.GetGlobalTransform().Scale.Abs();
        float fallback = 104f * Math.Max(scale.X, scale.Y);
        source = "art-scale";
        Vector2 origin = orb.GlobalPosition;
        return new Rect2(origin - new Vector2(fallback * 0.5f, fallback * 0.5f), new Vector2(fallback, fallback));
    }

    /// <summary>
    /// Whether the game draws a passive/evoke value for this orb. <c>NOrb.UpdateVisuals</c> hides both numbers for a
    /// <c>PlasmaOrb</c> by design, and an empty slot has no model and no label.
    /// </summary>
    private static bool OrbValueLabelsApply(NOrb orb) =>
        orb.Model is { } model && model.GetType().Name != "PlasmaOrb";

    /// <summary>Whether any value label (passive or evoke) is shown with text.</summary>
    private static bool OrbLabelsVisible(NOrb orb)
    {
        Control? labels = orb.GetNodeOrNull<Control>("%LabelContainer") ?? orb.GetNodeOrNull<Control>("LabelContainer");
        return labels is { } container && container.IsVisibleInTree()
            && FindAll<Label>(container).Any(label =>
                label.IsVisibleInTree() && !string.IsNullOrWhiteSpace(label.Text));
    }

    /// <summary>
    /// Information only: the backdrop's world scale against the room's shared creature body scale, so the creature-to-
    /// background proportion can be compared across encounters (a layout decision, not a pass/fail).
    /// </summary>
    private void CheckBackdropProportion(string name, NCombatRoom room, Control bgContainer, Control? arena)
    {
        TextureRect? art = FindArenaArt(arena, 5);
        float backdrop = bgContainer.GetGlobalTransform().Scale.X;
        float artScale = art is not null ? art.GetGlobalTransform().Scale.X : backdrop;
        float peer = room.HasMeta("portrait_peer_scale") ? room.GetMeta("portrait_peer_scale").AsSingle() : 1f;
        _report.Pass(name + "_backdrop_proportion",
            $"backdropScale={backdrop:F3} artScale={artScale:F3} creaturePeerScale={peer:F3} " +
            $"artToCreature={artScale / Math.Max(0.001f, peer):F2}");
    }

    /// <summary>
    /// The visible floor horizon: the stage publishes it per room as <c>portrait_floor_horizon</c> (for the Underdocks
    /// pier, the top of the plank area). When absent, fall back to the authored 200-unit background line projected
    /// through the container transform (or the pier's plank surface); <paramref name="source"/> says which was used.
    /// </summary>
    private static float GroundHorizon(NCombatRoom room, PortraitCombatBands bands, bool pier, out string source)
    {
        Control? bgContainer = room.GetNodeOrNull<Control>("%BgContainer");
        Control? arena = bgContainer?.GetChildren().OfType<Control>().FirstOrDefault();
        foreach (Node? node in new Node?[] { arena, bgContainer, room })
        {
            if (node is null) continue;
            foreach (string meta in HorizonMetaNames)
            {
                if (!node.HasMeta(meta)) continue;
                source = "published";
                return node.GetMeta(meta).AsSingle();
            }
        }
        if (pier && arena is not null && arena.HasMeta("portrait_pier_surface"))
        {
            source = "pierSurfaceFallback";
            return arena.GetMeta("portrait_pier_surface").AsSingle();
        }
        source = "authoredFallback";
        return bgContainer is not null
            ? (bgContainer.GetGlobalTransformWithCanvas() * new Vector2(0f, 200f)).Y
            : bands.FrontFeetY;
    }

    /// <summary>
    /// The tall formation's lift for a pair's first enemy (about 64 launcher units), from the stage's published offset
    /// meta when present, otherwise the fixed lift above.
    /// </summary>
    private static float FormationRaise(NCombatRoom room)
    {
        Control? bgContainer = room.GetNodeOrNull<Control>("%BgContainer");
        Control? arena = bgContainer?.GetChildren().OfType<Control>().FirstOrDefault();
        foreach (Node? node in new Node?[] { arena, bgContainer, room })
        {
            if (node is null) continue;
            foreach (string meta in FormationOffsetMetaNames)
            {
                if (!node.HasMeta(meta)) continue;
                return MathF.Abs(node.GetMeta(meta).AsSingle());
            }
        }
        return StageFormationRaise;
    }

    /// <summary>
    /// Every non-flying creature must stand on the visible floor: its hit box bottom at or below the floor horizon and
    /// above the hand rail. Back-rank slots lifted above the floor line are exactly what this catches.
    /// </summary>
    private void CheckCreatureGrounding(string phase, NCombatRoom room, PortraitCombatBands bands, bool pier)
    {
        float horizon = GroundHorizon(room, bands, pier, out string horizonSource);
        var offenders = new List<string>();
        int grounded = 0, flying = 0;
        foreach (NCreature creature in room.CreatureNodes.Where(c =>
                     GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion() && c.Entity.IsAlive))
        {
            string type = creature.Entity.Monster?.GetType().Name ?? "player";
            if (FlyingMonsters.Contains(type, StringComparer.Ordinal))
            {
                flying++;
                continue;
            }
            grounded++;
            float feet = creature.Hitbox.GetGlobalRect().End.Y;
            if (feet < horizon - GroundHorizonTolerance || feet > bands.HandTopY + 1f)
                offenders.Add($"{type}: feet={feet:F1}");
        }
        _report.Check(phase + "_creatures_grounded", offenders.Count == 0,
            offenders.Count == 0
                ? $"{grounded} grounded creature(s) at or below the floor horizon {horizon:F1} " +
                  $"(horizonSource={horizonSource}) and above HandTopY {bands.HandTopY:F1} " +
                  $"({flying} flying exempt)"
                : $"horizon={horizon:F1} horizonSource={horizonSource} handTop={bands.HandTopY:F1} " +
                  string.Join(" | ", offenders));
    }

    private void CheckKaiserCrab(string phase, NCombatRoom room, NCreature player, List<NCreature> enemies)
    {
        NCreature? crusher = enemies.FirstOrDefault(e => e.Entity.Monster?.GetType().Name == "Crusher");
        NCreature? rocket = enemies.FirstOrDefault(e => e.Entity.Monster?.GetType().Name == "Rocket");
        _report.Check(phase + "_kaiser_claws", crusher is not null && rocket is not null,
            $"enemies={string.Join(", ", enemies.Select(e => e.Entity.Monster?.GetType().Name))}");
        if (crusher is null || rocket is null) return;
        float playerX = player.Hitbox.GetGlobalRect().GetCenter().X;
        float crusherX = crusher.Hitbox.GetGlobalRect().GetCenter().X;
        float rocketX = rocket.Hitbox.GetGlobalRect().GetCenter().X;
        _report.Check(phase + "_kaiser_crusher_left", crusherX < playerX, $"crusher={crusherX:F1} player={playerX:F1}");
        _report.Check(phase + "_kaiser_rocket_right", rocketX > playerX, $"rocket={rocketX:F1} player={playerX:F1}");
        Node2D? crab = room.Background.GetNodeOrNull<Node2D>("%KaiserCrab");
        _report.Check(phase + "_kaiser_crab_body", crab is not null, "the crab body is in the arena background");
        if (crab is null) return;
        Node? leftArm = crab.GetNodeOrNull<Node2D>("%ArmBoneL") ?? FindNamed(crab, "ArmBoneL");
        Node? rightArm = crab.GetNodeOrNull<Node2D>("%ArmBoneR") ?? FindNamed(crab, "ArmBoneR");
        _report.Check(phase + "_kaiser_crab_claws", leftArm is not null && rightArm is not null,
            "both claw bones are present");
    }

    private void CheckStageStable(string prefix, Control bgContainer, Transform2D background, Control? arena,
        Transform2D arenaTransform, Dictionary<ulong, Transform2D> creatures, Node2D? crab, Transform2D crabTransform)
    {
        CheckStageFramingStable(prefix, bgContainer, background, arena, arenaTransform, crab, crabTransform);
        CheckStageSurvivorsStable(prefix, creatures);
    }

    private void CheckStageFramingStable(string prefix, Control bgContainer, Transform2D background, Control? arena,
        Transform2D arenaTransform, Node2D? crab, Transform2D crabTransform)
    {
        _report.Check(prefix + "_background_stable", bgContainer.GetGlobalTransform().IsEqualApprox(background),
            "the arena background does not move with the hand, a turn or a roster change");
        if (arena is not null && GodotObject.IsInstanceValid(arena))
            _report.Check(prefix + "_arena_stable", arena.GetGlobalTransform().IsEqualApprox(arenaTransform),
                $"before={arenaTransform} after={arena.GetGlobalTransform()}");
        if (crab is not null && GodotObject.IsInstanceValid(crab))
            _report.Check(prefix + "_crab_body_stable", crab.GetGlobalTransform().IsEqualApprox(crabTransform),
                $"before={crabTransform} after={crab.GetGlobalTransform()}");
    }

    private void CheckStageSurvivorsStable(string prefix, Dictionary<ulong, Transform2D> creatures)
    {
        foreach ((ulong id, Transform2D before) in creatures)
        {
            if (GodotObject.InstanceFromId(id) is not NCreature creature || !GodotObject.IsInstanceValid(creature))
                continue;
            // A creature a played card killed (or the victim of the death step) is covered by the death checks, not this one.
            if (creature.Entity.IsDead) continue;
            Transform2D after = creature.GetGlobalTransform();
            // Idle animation/transform propagation drifts sub-pixel (seen as origin 721.8926 -> 721.8905 on
            // WaterfallGiantBoss). Allow 0.05 canvas units (0.025 px at 2x) on the origin and both basis axes; a real
            // layout change moves by whole units.
            const float tolerance = 0.05f;
            bool stable = before.Origin.DistanceTo(after.Origin) <= tolerance
                          && before.X.DistanceTo(after.X) <= tolerance
                          && before.Y.DistanceTo(after.Y) <= tolerance;
            _report.Check(prefix + "_creature_stable", stable,
                $"{(creature.Entity.Monster?.GetType().Name ?? "player")}: before={before} after={after} " +
                $"tolerance={tolerance}");
        }
    }

    /// <summary>Draws until the hand holds ten cards (the starting hand is five, and the untouched deck holds the rest).</summary>
    private async Task DrawStageHandAsync(Player me, NCombatRoom room)
    {
        NPlayerHand hand = room.Ui.Hand;
        for (int i = 0; i < 10 && hand.ActiveHolders.Count < 10; i++)
        {
            int before = hand.ActiveHolders.Count;
            await CardPileCmd.Draw(new ThrowingPlayerChoiceContext(), me);
            // CardPileCmd.Draw already awaits the native draw; the old 0.3 s only covered the holder appearing.
            await WaitUntilSettledAsync(() => hand.ActiveHolders.Count > before,
                "stage card drawn", TimeSpan.FromSeconds(0.3));
            if (hand.ActiveHolders.Count <= before) break;
        }
    }

    /// <summary>
    /// Plays up to two cards through the touch pointer: a targeted card onto the healthiest enemy, anything else onto the
    /// stage above the hand. Reports whether the fixture could play at all so a hand with no playable card is not a failure.
    /// </summary>
    private async Task PlayStageCardsAsync(string name, NCombatRoom room, PortraitCombatBands bands)
    {
        NPlayerHand hand = room.Ui.Hand;
        int played = 0;
        for (int attempt = 0; attempt < 4 && played < 2 && CombatManager.Instance.IsInProgress; attempt++)
        {
            NHandCardHolder? targeted = hand.ActiveHolders.FirstOrDefault(IsTargetedPlayableCard);
            NHandCardHolder? holder = targeted ?? hand.ActiveHolders.FirstOrDefault(IsPlayableCard);
            if (holder is null) break;
            NCreature? target = targeted is null ? null : room.CreatureNodes
                .Where(c => c.Entity.IsMonster && c.Entity.IsAlive && !c.Entity.IsPet)
                .OrderByDescending(c => c.Entity.CurrentHp)
                .FirstOrDefault();
            Vector2 end = target is not null
                ? CenterOf(target.Hitbox)
                : new Vector2(540f, Math.Max(bands.StageTopY + 120f, bands.RailTopY - 140f));
            int before = hand.ActiveHolders.Count;
            await _pointer.DragAsync(CenterOf(holder.Hitbox), end, steps: 16);
            // The drag and the native play are what the fixture exercises; keep their real-time settle.
            await WaitSecondsAsync(0.8);
            if (hand.ActiveHolders.Count >= before) break;
            played++;
            // The old 0.4 s let the played card and the hand reflow finish before the next drag; wait for that state.
            await WaitUntilStableAsync(() => HandSettledSignature(hand), "stage next card", TimeSpan.FromSeconds(0.4));
        }
        _report.Check(name + "_stage_cards_played",
            played > 0 || !hand.ActiveHolders.Any(IsPlayableCard),
            $"played={played} hand={hand.ActiveHolders.Count}");
    }

    private static bool IsPlayableCard(NHandCardHolder holder) => holder.CardNode?.Model?.CanPlay() == true;

    private static List<NCreature> StageEnemies(NCombatRoom room) => room.CreatureNodes
        .Where(c => c.Entity.IsMonster && !c.Entity.IsPet && c.Entity.IsAlive
                    && GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion())
        .OrderBy(c => c.Hitbox.GetGlobalRect().Position.X)
        .ToList();

    private static Node? FindNamed(Node root, string name)
    {
        if (root.Name == name) return root;
        foreach (Node child in root.GetChildren())
        {
            if (FindNamed(child, name) is { } found) return found;
        }
        return null;
    }

    /// <summary>The per-encounter geometry record next to the screenshots: bands, published measurements and every transform read.</summary>
    private void WriteStageGeometry(string name, NCombatRoom room, PortraitCombatBands bands, float canvasHeight,
        float capHeight, bool pier)
    {
        var lines = new List<string>
        {
            $"encounter={name}",
            $"canvas={SafeAreaCanvas.PortraitWidth}x{canvasHeight} cap={capHeight} compact={bands.Compact} pier={pier}",
            $"stageTop={bands.StageTopY} frontFeet={bands.FrontFeetY} railTop={bands.RailTopY} handTop={bands.HandTopY}",
            $"peerScale={Meta(room, "portrait_peer_scale")} peerScaleFloored={Meta(room, "portrait_peer_scale_floored")}",
            $"endTurn={Rect(room.Ui.EndTurnButton)} energy={Rect(room.Ui.EnergyCounterContainer)} " +
            $"draw={Rect(room.Ui.DrawPile)} discard={Rect(room.Ui.DiscardPile)} hand={room.Ui.Hand.ActiveHolders.Count}",
        };
        Control? bgContainer = room.GetNodeOrNull<Control>("%BgContainer");
        Control? arena = bgContainer?.GetChildren().OfType<Control>().FirstOrDefault();
        TextureRect? backdropArt = arena is not null ? FindArenaArt(arena, 5) : null;
        float backdropScale = (backdropArt ?? bgContainer)?.GetGlobalTransform().Scale.X ?? 1f;
        float artScale = backdropArt is not null ? backdropArt.GetGlobalTransform().Scale.X : backdropScale;
        float peerScale = room.HasMeta("portrait_peer_scale")
            ? room.GetMeta("portrait_peer_scale").AsSingle() : 1f;
        lines.Add($"proportion=backdropScale:{backdropScale:F3} artScale:{artScale:F3} " +
                  $"creaturePeerScale:{peerScale:F3} artToCreature:{artScale / Math.Max(0.001f, peerScale):F2}");
        float groundHorizon = GroundHorizon(room, bands, pier, out string horizonSource);
        lines.Add($"groundHorizon={groundHorizon:F1} horizonSource={horizonSource} " +
                  $"frontFeet={bands.FrontFeetY:F1} handTop={bands.HandTopY:F1} " +
                  $"formationRaise={FormationRaise(room):F1}");
        if (arena is not null)
        {
            lines.Add($"arena={Rect(arena)} floorAligned={Meta(arena, "portrait_floor_aligned")} " +
                      $"artBottom={Meta(arena, "portrait_art_bottom")} pier={Meta(arena, "portrait_pier")} " +
                      $"pierSurface={Meta(arena, "portrait_pier_surface")}");
            if (FindArenaArt(arena, 5) is { } art)
                lines.Add($"arenaArt={Rect(art)} texture={art.Texture?.GetSize()}");
        }
        foreach (NCreature creature in room.CreatureNodes)
        {
            Rect2 hitbox = creature.Hitbox.GetGlobalRect();
            float hitboxDrop = hitbox.End.Y - creature.GlobalPosition.Y;
            string type = creature.Entity.Monster?.GetType().Name ?? "player";
            bool flying = FlyingMonsters.Contains(type, StringComparer.Ordinal);
            lines.Add($"creature={type} slot={creature.Entity.SlotName} " +
                      $"hp={creature.Entity.CurrentHp}/{creature.Entity.MaxHp} global={creature.GlobalPosition} " +
                      $"scale={creature.GetGlobalTransform().Scale} z={creature.ZIndex} " +
                      $"hitbox={Rect(creature.Hitbox)} feet={hitbox.End.Y:F1} groundHorizon={groundHorizon:F1} " +
                      $"flying={flying} hitboxDrop={hitboxDrop:F1} body={Rect(creature.Visuals.Bounds)}");
        }
        foreach (NHandCardHolder holder in room.Ui.Hand.ActiveHolders)
        {
            lines.Add($"card={holder.CardNode?.Model?.GetType().Name} holder={Rect(holder)} " +
                      $"hitbox={Rect(holder.Hitbox)} scale={holder.GetGlobalTransform().Scale}");
        }
        string path = Path.Combine(_options.OutputDirectory, "stage_" + name + "_geometry.txt");
        File.WriteAllLines(path, lines);
        _report.Pass(name + "_stage_geometry_file", path);
    }

    private static string Meta(Node node, string name) => node.HasMeta(name)
        ? node.GetMeta(name).ToString()
        : "missing";

    private static string Rect(Control control) => control.GetGlobalRect().ToString();

    private static double StageDeadlineSeconds()
    {
        // The same bound the launcher runner uses: run.sh stops reading RESULTS.json at STS2MOBILE_TEST_TIMEOUT.
        double total = double.TryParse(System.Environment.GetEnvironmentVariable("STS2MOBILE_TEST_TIMEOUT"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value > 0 ? value : 240;
        return Math.Max(30, total - 20);
    }

    private static bool PastStageDeadline(double seconds) => Time.GetTicksMsec() / 1000.0 > seconds;
}
