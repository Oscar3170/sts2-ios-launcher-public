using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The Fabricator's mid-fight summons (<c>stage-fabricator</c>). The encounter reserves four bot slots around its single
/// starting body (<c>bot1</c>/<c>bot2</c>/<c>fabricator</c>/<c>bot3</c>/<c>bot4</c>), and the fight's own Fabricate move adds
/// one or two bots while the boss lives: the shield and aggro spawns can put all four slots on the stage. The stage plan is
/// frozen with the boss alone, so where a later bot appears is decided before it exists; a bot drawn over the boss's body
/// is the reported defect, not a missing slot.
/// <para>
/// The fight is driven through its real enemy turns with the player kept alive (the fixture's 9999 HP, refilled each turn;
/// no card is played and the boss is never damaged). After every turn that adds a bot, the drawn frame is sampled through
/// <c>RenderingServer.FramePostDraw</c> once the fall animation has settled and reports, in canvas coordinates: every living
/// creature's visible body clear of the Fabricator's, every body inside the room's published HUD floor and the canvas, every
/// hitbox reachable (its centre not covered by another creature's hitbox), and no health label or intent row over any other
/// creature's HUD or body. The fixture's own <c>CheckStageHud</c> and <c>CheckCreatureClearance</c> answers run on the same
/// settled state, so the Fabricator is held to the stage-encounters HUD rules where its raised rank can meet them. Each
/// summon is captured. The fight picks its one- or two-bot summon move 50/50, so while fewer than four bots are alive the
/// pass asks the boss's own move state machine for the move whose spawns fill the remaining reserved slots; a pinned
/// full-roster state checks all four slots and is captured before two bots are killed and the next summon turns are run, so
/// the freed reserved slots are exercised too.
/// </para>
/// <para>
/// Portrait gets the geometry checks; landscape runs the same summons and captures as the native reference and only fails
/// on a crash or a fight that never summons.
/// </para>
/// </summary>
internal sealed partial class TestDriver
{
    private const string FabricatorEncounter = "FabricatorNormal";
    private const string FabricatorMonster = "Fabricator";
    private const int FabricatorMaxMinions = 4;
    // The Fabricator's own move-state ids (v0.114.0): Fabricate spawns two bots, the strike one.
    private const string FabricateMoveId = "FABRICATE_MOVE";
    private const string FabricatingStrikeMoveId = "FABRICATING_STRIKE_MOVE";
    // The stage-clearance tolerance the room's own checks use: touching by less than this is not an overlap.
    private const float FabricatorBodyTolerance = 4f;
    private const int FabricatorTurnLimit = 12;

    /// <summary>Entry for <see cref="LauncherHostRunner"/>'s <c>stage-fabricator</c> scenario.</summary>
    internal static async Task RunFabricatorScenarioAsync(TestOptions options, TestReport report,
        ScreenshotService screenshots, VirtualPointer pointer)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Log.Error("Test harness requires a SceneTree.");
            return;
        }

        await new TestDriver(tree, options, report, screenshots, pointer).RunFabricatorScenarioInternalAsync();
    }

    private async Task RunFabricatorScenarioInternalAsync()
    {
        // Entering the act, up to four summon turns, a death branch and its re-summon: the shared default timeout is too
        // short; run.sh's per-scenario bound is the real one.
        _cancellation.CancelAfter(TimeSpan.FromMinutes(12));
        await WaitForMainMenuPreloadAsync();
        RunState state = await StartRunAsync();
        var me = state.Players[0];
        me.Creature.SetMaxHpInternal(9999);
        me.Creature.SetCurrentHpInternal(me.Creature.MaxHp);
        await ConfigureTourActAsync(state, "Glory");
        EncounterModel encounter = state.Act.AllEncounters
            .First(e => e.GetType().Name == FabricatorEncounter).ToMutable();
        _report.Pass("stage_fabricator_act", $"act={state.Act.GetType().Name} encounter={encounter.Id}");

        await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: encounter, showTransition: false);
        await WaitUntilAsync(IsPlayerPlayPhase, "Fabricator player phase", TimeSpan.FromSeconds(45));
        await WaitUntilStableAsync(StageIntroSignature, "Fabricator intro settle", TimeSpan.FromSeconds(4.5));
        NCombatRoom room = NCombatRoom.Instance
            ?? throw new InvalidOperationException("Combat room disappeared after entering the Fabricator fight.");
        _report.Check("stage_fabricator_boss_alone", FabricatorMinions(room).Count == 0,
            $"minions={FabricatorMinions(room).Count} enemies={room.CreatureNodes.Count(c => c.Entity.IsMonster)}");
        await CheckFabricatorFrameAsync("stage_fabricator_initial", room);
        await CaptureAsync("stage_fabricator_initial");

        var seen = FabricatorMinions(room).Select(c => c.GetInstanceId()).ToHashSet();
        int summonPhase = 0;
        int quietTurns = 0;
        for (int turn = 1; turn <= FabricatorTurnLimit; turn++)
        {
            List<NCreature> before = FabricatorMinions(room);
            if (before.Count >= FabricatorMaxMinions) break;
            // While fewer than four living teammates exist the fight rolls Fabricate (two spawns) or Fabricating Strike
            // (one) 50/50, so the full roster depends on the roll. Ask the game's own move state machine for the move that
            // fills the remaining reserved slots exactly: two spawns with two bots alive, one with three.
            if (before.Count > 0)
            {
                string forced = ForceSummonMove(room, before.Count);
                _report.Pass("stage_fabricator_force_" + turn, $"alive={before.Count} {forced}");
            }
            me.Creature.SetCurrentHpInternal(me.Creature.MaxHp);
            await EndFabricatorTurnAsync(room, "Fabricator turn " + turn);
            // The bots fall into their slot with the encounter's own arrival animation; it is not a root tween, so wait
            // past it and then for the room's resting state before the drawn-frame sample.
            await WaitSecondsAsync(1.2);
            List<NCreature> minions = FabricatorMinions(room);
            List<NCreature> added = minions.Where(c => !seen.Contains(c.GetInstanceId())).ToList();
            foreach (NCreature minion in minions) seen.Add(minion.GetInstanceId());
            _report.Pass("stage_fabricator_turn_" + turn,
                $"minions={minions.Count} added={added.Count} " +
                $"types={string.Join(",", minions.Select(c => c.Entity.Monster?.GetType().Name))} " +
                $"slots={string.Join(",", minions.Select(c => c.Entity.SlotName))}");
            if (added.Count == 0)
            {
                // Nothing was added even with the forced move: stop after a run of empty turns rather than spin.
                if (++quietTurns >= 3) break;
                continue;
            }
            quietTurns = 0;
            summonPhase++;
            await WaitUntilStableAsync(CombatSettledSignature, "Fabricator summon " + summonPhase + " settle", ShortWait);
            await CheckFabricatorFrameAsync("stage_fabricator_summon_" + summonPhase, room);
            await CaptureAsync("stage_fabricator_summon_" + summonPhase);
            if (minions.Count >= FabricatorMaxMinions) break;
        }

        List<NCreature> roster = FabricatorMinions(room);
        string[] fullSlots = roster.Select(c => c.Entity.SlotName ?? "")
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();
        string[] expectedSlots = { "bot1", "bot2", "bot3", "bot4" };
        bool fullRoster = roster.Count == FabricatorMaxMinions && fullSlots.SequenceEqual(expectedSlots);
        _report.Check("stage_fabricator_summoned_its_roster", fullRoster,
            $"minions={roster.Count} of {FabricatorMaxMinions}; slots={string.Join(",", fullSlots)}");
        int fullCount = roster.Count;
        if (fullRoster)
        {
            // The pinned full-roster state: every check the fixture has, on the four-slot arc/2x2.
            await WaitUntilStableAsync(CombatSettledSignature, "Fabricator full roster settle", ShortWait);
            await CheckFabricatorFrameAsync("stage_fabricator_full_roster", room);
            await CaptureAsync("stage_fabricator_full_roster");
        }

        // Two deaths are needed to make CanFabricate true again when all four bots arrived (it stops at four living
        // teammates, the boss included). The next summon must land in a freed reserved slot and keep the same clearance.
        List<NCreature> victims = FabricatorMinions(room).Take(2).ToList();
        if (victims.Count == 0)
        {
            _report.Fail("stage_fabricator_death_branch", "no minion was alive to kill; the re-summon branch did not run");
            return;
        }
        string[] freedSlots = victims.Select(v => v.Entity.SlotName ?? "").ToArray();
        foreach (NCreature victim in victims)
            await CreatureCmd.Kill(victim.Entity, force: true);
        await WaitSecondsAsync(2.0);
        await WaitUntilStableAsync(CombatSettledSignature, "Fabricator after deaths", ShortWait);
        _report.Check("stage_fabricator_death_kept_fight",
            CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsOverOrEnding && FabricatorBoss(room) is not null,
            $"killed={string.Join(",", freedSlots)} minions={FabricatorMinions(room).Count} " +
            $"inProgress={CombatManager.Instance.IsInProgress} ending={CombatManager.Instance.IsEnding}");
        await CheckFabricatorFrameAsync("stage_fabricator_after_death", room);
        await CaptureAsync("stage_fabricator_after_death");

        for (int turn = 1; turn <= 4; turn++)
        {
            me.Creature.SetCurrentHpInternal(me.Creature.MaxHp);
            await EndFabricatorTurnAsync(room, "Fabricator re-summon turn " + turn);
            await WaitSecondsAsync(1.2);
            List<NCreature> added = FabricatorMinions(room)
                .Where(c => !seen.Contains(c.GetInstanceId())).ToList();
            foreach (NCreature minion in FabricatorMinions(room)) seen.Add(minion.GetInstanceId());
            _report.Pass("stage_fabricator_resummon_turn_" + turn,
                $"minions={FabricatorMinions(room).Count} added={added.Count} " +
                $"slots={string.Join(",", added.Select(c => c.Entity.SlotName))}");
            if (added.Count == 0) continue;
            await WaitUntilStableAsync(CombatSettledSignature, "Fabricator re-summon settle", ShortWait);
            _report.Pass("stage_fabricator_resummon_slot",
                added.Any(c => freedSlots.Contains(c.Entity.SlotName ?? ""))
                    ? $"reused a freed slot ({string.Join(",", added.Select(c => c.Entity.SlotName))})"
                    : $"the game used an unused reserved slot ({string.Join(",", added.Select(c => c.Entity.SlotName))})");
            await CheckFabricatorFrameAsync("stage_fabricator_resummon", room);
            await CaptureAsync("stage_fabricator_resummon");
            break;
        }

        _report.Pass("stage_fabricator_completed", IsPortraitHost()
            ? $"summons={summonPhase} peakMinions={fullCount} freedSlots={string.Join(",", freedSlots)}"
            : $"landscape native reference: summons={summonPhase} peakMinions={fullCount}");
    }

    /// <summary>
    /// Asks the Fabricator's own move state machine for the summon move that fills the remaining reserved slots: FABRICATE
    /// spawns two bots (one defensive, one aggressive), FABRICATING_STRIKE one. The fight picks between them 50/50 while
    /// fewer than four living teammates exist, so the full roster would otherwise depend on the roll; SetMoveImmediate is
    /// the game's own "set the next move" API (its stun path uses it too).
    /// </summary>
    private string ForceSummonMove(NCombatRoom room, int alive)
    {
        NCreature? boss = FabricatorBoss(room);
        if (boss?.Entity.Monster is not MonsterModel monster)
            return "force skipped: no boss model";
        MonsterMoveStateMachine? machine = monster.MoveStateMachine;
        string moveId = alive >= 3 ? FabricatingStrikeMoveId : FabricateMoveId;
        if (machine == null || !machine.States.TryGetValue(moveId, out MonsterState? state) ||
            state is not MoveState move)
            return $"force skipped: {moveId} not found";
        monster.SetMoveImmediate(move, forceTransition: true);
        return $"forced {moveId}";
    }

    private async Task EndFabricatorTurnAsync(NCombatRoom room, string name)
    {
        await _pointer.TapAsync(CenterOf(room.Ui.EndTurnButton));
        await WaitUntilAsync(() => CombatManager.Instance.PlayerActionsDisabled || CombatManager.Instance.IsOverOrEnding,
            name + " enemy turn", ShortWait);
        await WaitThroughEnemyTurnAsync(name);
        await WaitUntilStableAsync(() => IsPlayerPlayPhase() ? CombatSettledSignature() : null,
            name + " next turn", TimeSpan.FromSeconds(1.5));
    }

    private static List<NCreature> FabricatorMinions(NCombatRoom room) =>
        room.CreatureNodes.Where(c => GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion()
                                      && c.Entity.IsAlive && c.Entity.IsMonster
                                      && c.Entity.Monster?.GetType().Name != FabricatorMonster).ToList();

    private static NCreature? FabricatorBoss(NCombatRoom room) =>
        room.CreatureNodes.FirstOrDefault(c => GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion()
                                               && c.Entity.IsAlive && c.Entity.Monster?.GetType().Name == FabricatorMonster);

    /// <summary>The drawn body, hit box and HUD of one living creature, in canvas coordinates.</summary>
    private readonly record struct FabricatorBody(ulong Id, string Name, string Slot, bool IsBoss, Rect2 Hitbox,
        Rect2 Visual, Rect2[] Hud)
    {
        // The stage fits and targets a creature by its footprint: the hit box merged with the visual bounds. Using the
        // same union here measures what the stage placed, not just the tappable control.
        public Rect2 Body => Visual.Size.X > 1f && Visual.Size.Y > 1f ? Hitbox.Merge(Visual) : Hitbox;
    }

    /// <summary>
    /// Samples the last frame the renderer drew (transforms are applied at FramePreDraw, so a live read can be one frame
    /// ahead of the picture) and reports the stage's body/band/reachability answers for that frame.
    /// </summary>
    private async Task CheckFabricatorFrameAsync(string phase, NCombatRoom room)
    {
        List<FabricatorBody> drawn = new();
        void Sample()
        {
            if (NCombatRoom.Instance is { } current && GodotObject.IsInstanceValid(current))
                drawn = DrawFabricatorBodies(current);
        }

        RenderingServer.FramePostDraw += Sample;
        try
        {
            await WaitFramesAsync(3);
        }
        finally
        {
            RenderingServer.FramePostDraw -= Sample;
        }
        if (drawn.Count == 0)
        {
            _report.Fail(phase + "_drawn_frame", "no drawn frame with living creatures was sampled");
            return;
        }
        if (!IsPortraitHost())
        {
            _report.Pass(phase + "_portrait_checks",
                $"landscape: native layout, portrait geometry skipped (drawn creatures={drawn.Count})");
            return;
        }

        Rect2 canvas = _tree.Root.GetViewport().GetVisibleRect();
        PortraitCombatBands bands = CurrentBands();
        // The room publishes its own HUD floor (portrait_hud_floor_y): the line under the run bars. A raised summon rank
        // legitimately stands above the stage top and below that floor, exactly as the stage-encounters fixture measures it.
        float floorY = StageFloor(room, bands);
        Rect2 band = new(0f, floorY, SafeAreaCanvas.PortraitWidth, Math.Max(0f, bands.HandTopY - floorY));
        List<FabricatorBody> boss = drawn.Where(b => b.IsBoss).ToList();
        if (boss.Count != 1)
        {
            _report.Fail(phase + "_boss_found", $"drawn Fabricator count={boss.Count} creatures={drawn.Count}");
            return;
        }

        bool inside = true;
        var outside = new List<string>();
        foreach (FabricatorBody body in drawn)
        {
            Rect2 rect = body.Body;
            bool fits = canvas.Grow(1f).Encloses(rect) && band.Grow(1f).Encloses(rect);
            if (fits) continue;
            inside = false;
            outside.Add($"{body.Name} body={rect} canvas={canvas} band={band}");
        }
        _report.Check(phase + "_bodies_inside_stage", inside,
            inside
                ? $"{drawn.Count} drawn bod(ies) inside the room's floor band {band} and canvas {canvas}"
                : string.Join(" | ", outside));

        Rect2 bossBody = boss[0].Body;
        var overlaps = new List<string>();
        float worstArea = 0f;
        foreach (FabricatorBody body in drawn.Where(b => !b.IsBoss))
        {
            Rect2 intersection = body.Body.Intersection(bossBody);
            if (!OverlapsBeyond(body.Body, bossBody, FabricatorBodyTolerance)) continue;
            float area = intersection.Size.X * intersection.Size.Y;
            worstArea = Math.Max(worstArea, area);
            overlaps.Add($"{body.Name} body={body.Body} over Fabricator body={bossBody} overlap={intersection.Size}");
        }
        _report.Check(phase + "_bodies_clear_of_fabricator", overlaps.Count == 0,
            overlaps.Count == 0
                ? $"{drawn.Count - 1} drawn bod(ies) clear of the Fabricator {bossBody} (tolerance {FabricatorBodyTolerance})"
                : $"worstArea={worstArea:F0} " + string.Join(" | ", overlaps));

        bool reachable = true;
        var covered = new List<string>();
        foreach (FabricatorBody body in drawn)
        {
            var hitbox = body.Hitbox;
            foreach (FabricatorBody other in drawn)
            {
                if (other.Id == body.Id) continue;
                Rect2 overlap = hitbox.Intersection(other.Hitbox);
                float fraction = hitbox.Size.X * hitbox.Size.Y <= 1f
                    ? 0f
                    : overlap.Size.X * overlap.Size.Y / (hitbox.Size.X * hitbox.Size.Y);
                if (fraction < 0.5f && !other.Hitbox.HasPoint(hitbox.GetCenter())) continue;
                reachable = false;
                covered.Add($"{body.Name} hitbox={hitbox} centreCovered=" +
                            $"{other.Hitbox.HasPoint(hitbox.GetCenter())} by={other.Name} hitbox={other.Hitbox} " +
                            $"covered={fraction * 100f:F0}%");
            }
        }
        _report.Check(phase + "_hitboxes_reachable", reachable,
            reachable
                ? $"{drawn.Count} hit box(es) reachable: no other body covers a centre or half of one"
                : string.Join(" | ", covered));

        var minionOverlap = new List<string>();
        List<FabricatorBody> minions = drawn.Where(b => !b.IsBoss).ToList();
        for (int i = 0; i < minions.Count; i++)
        for (int j = i + 1; j < minions.Count; j++)
        {
            if (!OverlapsBeyond(minions[i].Body, minions[j].Body, FabricatorBodyTolerance)) continue;
            Rect2 overlap = minions[i].Body.Intersection(minions[j].Body);
            minionOverlap.Add($"{minions[i].Name} {minions[i].Body} vs {minions[j].Name} {minions[j].Body} " +
                              $"overlap={overlap.Size}");
        }
        _report.Check(phase + "_minion_bodies_clear", minionOverlap.Count == 0,
            minionOverlap.Count == 0
                ? $"{minions.Count} minion bod(ies) clear of each other"
                : string.Join(" | ", minionOverlap));

        // HUD, from the same drawn frame: every health label and intent row readable in the room's run-bar floor and
        // clear of every other creature's HUD and body. This is the stage-encounters CheckStageHud rule, measured on what
        // the renderer drew rather than on a live read that can be a frame ahead of the picture.
        bool hudInside = true;
        var hudOutside = new List<string>();
        int hudCount = 0;
        foreach (FabricatorBody body in drawn)
        foreach (Rect2 rect in body.Hud)
        {
            hudCount++;
            bool fits = rect.Size.X >= 24f && rect.Size.Y >= 24f
                && canvas.Grow(1f).Encloses(rect)
                && rect.Position.Y >= floorY - 0.5f
                && rect.End.Y <= bands.HandTopY + 0.5f;
            if (fits) continue;
            hudInside = false;
            hudOutside.Add($"{body.Name} hud={rect} floor={floorY:F0} handTop={bands.HandTopY:F0} canvas={canvas}");
        }
        _report.Check(phase + "_hud_readable", hudInside,
            hudInside
                ? $"{hudCount} drawn HUD rect(s) readable between the run-bar floor {floorY:F0} and HandTopY " +
                  $"{bands.HandTopY:F0}"
                : string.Join(" | ", hudOutside));

        bool hudClear = true;
        var hudOverlaps = new List<string>();
        for (int i = 0; i < drawn.Count; i++)
        {
            foreach (Rect2 a in drawn[i].Hud)
            {
                for (int j = 0; j < drawn.Count; j++)
                {
                    if (i == j) continue;
                    if (OverlapsBeyond(a, drawn[j].Body, 2f))
                    {
                        hudClear = false;
                        hudOverlaps.Add($"{drawn[i].Name} hud={a} over {drawn[j].Name} body={drawn[j].Body}");
                    }
                    if (j <= i) continue;
                    foreach (Rect2 b in drawn[j].Hud)
                    {
                        if (!OverlapsBeyond(a, b, 2f)) continue;
                        hudClear = false;
                        hudOverlaps.Add($"{drawn[i].Name} hud={a} vs {drawn[j].Name} hud={b}");
                    }
                }
            }
        }
        _report.Check(phase + "_hud_clear_of_neighbours", hudClear,
            hudClear
                ? $"{hudCount} drawn HUD rect(s) clear of every other creature's HUD and body (tolerance 2)"
                : string.Join(" | ", hudOverlaps));

        // The fixture's own answers on the same settled state: the stage's HUD rule per enemy (run-bar floor, in canvas,
        // clear of neighbours) and its full body/bar/intent/status clearance pass.
        List<NCreature> enemies = room.CreatureNodes
            .Where(c => GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion() && c.Entity.IsAlive && c.Entity.IsMonster)
            .ToList();
        foreach (NCreature enemy in enemies)
        {
            string slot = enemy.Entity.SlotName ?? enemy.Entity.Monster?.GetType().Name ?? "enemy";
            CheckStageHud(phase + "_" + slot, enemy, enemies, bands, room);
        }
        CheckCreatureClearance(phase);
        if (bands.Compact)
            CheckFabricatorTwoByTwo(phase, boss[0], drawn, bands);
        else
            CheckFabricatorArc(phase, boss[0], drawn, bands);
    }

    /// <summary>How far off the front foot line a low flank bot may stand and still count as on the floor (native raises
    /// its side bots slightly).</summary>
    private const float ArcFloorTolerance = 96f;

    /// <summary>
    /// The tall canvas's native-like arc: bot1 and bot4 low on the boss's left and right flanks, bot2 and bot3 raised above
    /// the boss's top, left and right of its centre, with the boss centred in the band the two flanks span. Slots are
    /// identified by the encounter's own names (bot1..bot4), so a re-summon into a freed slot must land where native would
    /// put that bot; slots not on stage yet are reserved and simply not measured.
    /// </summary>
    private void CheckFabricatorArc(string phase, FabricatorBody boss, List<FabricatorBody> drawn,
        PortraitCombatBands bands)
    {
        Dictionary<string, FabricatorBody> bots = SummonBots(drawn);
        if (bots.Count == 0)
        {
            _report.Pass(phase + "_arc_shape", "no summon slot is on stage yet; the arc positions are reserved");
            return;
        }
        var failures = new List<string>();
        float bossCentre = boss.Body.GetCenter().X;
        if (bots.TryGetValue("bot1", out FabricatorBody bot1))
        {
            if (bot1.Body.GetCenter().X >= bossCentre)
                failures.Add($"bot1 centre={bot1.Body.GetCenter().X:F1} not left of the boss centre {bossCentre:F1}");
            if (bot1.Body.End.X > boss.Body.Position.X + 4f)
                failures.Add($"bot1 body={bot1.Body} reaches the boss body={boss.Body}");
            if (MathF.Abs(bot1.Body.End.Y - bands.FrontFeetY) > ArcFloorTolerance)
                failures.Add($"bot1 feet={bot1.Body.End.Y:F1} not near the floor line {bands.FrontFeetY:F1}");
        }
        if (bots.TryGetValue("bot4", out FabricatorBody bot4))
        {
            if (bot4.Body.GetCenter().X <= bossCentre)
                failures.Add($"bot4 centre={bot4.Body.GetCenter().X:F1} not right of the boss centre {bossCentre:F1}");
            if (bot4.Body.Position.X < boss.Body.End.X - 4f)
                failures.Add($"bot4 body={bot4.Body} reaches the boss body={boss.Body}");
            if (MathF.Abs(bot4.Body.End.Y - bands.FrontFeetY) > ArcFloorTolerance)
                failures.Add($"bot4 feet={bot4.Body.End.Y:F1} not near the floor line {bands.FrontFeetY:F1}");
        }
        if (bots.TryGetValue("bot2", out FabricatorBody bot2))
        {
            if (bot2.Body.End.Y > boss.Body.Position.Y)
                failures.Add($"bot2 bottom={bot2.Body.End.Y:F1} not above the boss top {boss.Body.Position.Y:F1}");
            if (bot2.Body.GetCenter().X >= bossCentre)
                failures.Add($"bot2 centre={bot2.Body.GetCenter().X:F1} not left of the boss centre {bossCentre:F1}");
        }
        if (bots.TryGetValue("bot3", out FabricatorBody bot3))
        {
            if (bot3.Body.End.Y > boss.Body.Position.Y)
                failures.Add($"bot3 bottom={bot3.Body.End.Y:F1} not above the boss top {boss.Body.Position.Y:F1}");
            if (bot3.Body.GetCenter().X <= bossCentre)
                failures.Add($"bot3 centre={bot3.Body.GetCenter().X:F1} not right of the boss centre {bossCentre:F1}");
        }
        if (bots.Count == 4 && bots.TryGetValue("bot1", out bot1) && bots.TryGetValue("bot4", out bot4))
        {
            // "Centred in the band": the two low flanks span the band; the boss sits in its middle third.
            float left = bot1.Body.Position.X;
            float right = bot4.Body.End.X;
            float third = (right - left) / 3f;
            if (bossCentre < left + third || bossCentre > left + 2f * third)
                failures.Add($"boss centre={bossCentre:F1} outside the band's middle third " +
                             $"[{left + third:F1}, {left + 2f * third:F1}] of [{left:F1}, {right:F1}]");
        }
        _report.Check(phase + "_arc_shape", failures.Count == 0,
            failures.Count == 0
                ? $"{bots.Count} summon slot(s) on the native arc; boss centre={bossCentre:F1} " +
                  $"floor={bands.FrontFeetY:F1}"
                : string.Join(" | ", failures));
    }

    /// <summary>
    /// The short canvas's 2x2: bot1 and bot2 stand on the front foot line (the low rank) and bot3 and bot4 in the raised
    /// rank above it. Slot identity follows the encounter's names, so a freed slot is reused in the same place.
    /// </summary>
    private void CheckFabricatorTwoByTwo(string phase, FabricatorBody boss, List<FabricatorBody> drawn,
        PortraitCombatBands bands)
    {
        _ = boss;
        Dictionary<string, FabricatorBody> bots = SummonBots(drawn);
        if (bots.Count == 0)
        {
            _report.Pass(phase + "_two_by_two", "no summon slot is on stage yet; the 2x2 positions are reserved");
            return;
        }
        var failures = new List<string>();
        foreach (string slot in new[] { "bot1", "bot2" })
        {
            if (!bots.TryGetValue(slot, out FabricatorBody bot)) continue;
            if (MathF.Abs(bot.Body.End.Y - bands.FrontFeetY) > ArcFloorTolerance)
                failures.Add($"{slot} feet={bot.Body.End.Y:F1} not on the front line {bands.FrontFeetY:F1}");
        }
        foreach (string slot in new[] { "bot3", "bot4" })
        {
            if (!bots.TryGetValue(slot, out FabricatorBody bot)) continue;
            if (bot.Body.End.Y > bands.FrontFeetY - 100f)
                failures.Add($"{slot} feet={bot.Body.End.Y:F1} not in the raised rank above {bands.FrontFeetY - 100f:F1}");
        }
        _report.Check(phase + "_two_by_two", failures.Count == 0,
            failures.Count == 0
                ? $"{bots.Count} summon slot(s) in the 2x2; front={bands.FrontFeetY:F1}"
                : string.Join(" | ", failures));
    }

    /// <summary>The alive summon slots of the drawn frame, keyed by the encounter's slot name.</summary>
    private static Dictionary<string, FabricatorBody> SummonBots(List<FabricatorBody> drawn)
    {
        var bots = new Dictionary<string, FabricatorBody>(StringComparer.Ordinal);
        foreach (FabricatorBody body in drawn)
        {
            if (body.IsBoss || !body.Slot.StartsWith("bot", StringComparison.Ordinal)) continue;
            bots[body.Slot] = body;
        }
        return bots;
    }

    private PortraitCombatBands CurrentBands()
    {
        float canvasHeight = Math.Max(_tree.Root.GetViewport().GetVisibleRect().Size.Y, SafeAreaCanvas.MinPortraitHeight);
        float capHeight = PortraitLayout.ComputeCapHeight(_tree.Root);
        return PortraitCombatMath.ComputeCombatBands(canvasHeight, capHeight + PortraitBands.RunContentTop);
    }

    private static List<FabricatorBody> DrawFabricatorBodies(NCombatRoom room)
    {
        var bodies = new List<FabricatorBody>();
        foreach (NCreature creature in room.CreatureNodes)
        {
            if (!GodotObject.IsInstanceValid(creature) || creature.IsQueuedForDeletion() || !creature.Entity.IsAlive)
                continue;
            if (!GodotObject.IsInstanceValid(creature.Hitbox)) continue;
            Rect2 hitbox = creature.Hitbox.GetGlobalRect();
            Rect2 visual = GodotObject.IsInstanceValid(creature.Visuals) && GodotObject.IsInstanceValid(creature.Visuals.Bounds)
                ? creature.Visuals.Bounds.GetGlobalRect()
                : default;
            string name = creature.Entity.Monster?.GetType().Name ?? "player";
            bodies.Add(new FabricatorBody(creature.GetInstanceId(), name, creature.Entity.SlotName ?? "",
                name == FabricatorMonster, hitbox, visual, EnemyHud(creature).ToArray()));
        }
        return bodies;
    }
}
