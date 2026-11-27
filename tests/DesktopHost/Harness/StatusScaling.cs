using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// Status-icon scaling (Batch A, combat): the native power row under a creature's health bar, measured for the player with 5,
/// 7, 14 and 20 statuses, a lone small enemy with 5 and 7, two adjacent small enemies with 7 each, and the act's big boss
/// with 5, 7 and 10. The launcher draws a creature's health display at a fixed canvas size
/// (<c>StateDisplayCanvasScale</c>) and caps the health-bar width in crowded fights, so the power row's place relative to
/// the hit box, the bar and its neighbours is what a player sees; the scenario applies distinct harmless powers through the
/// native command, waits for the native icon tweens and the room to settle, and writes the measured rectangles to
/// <c>status_&lt;subject&gt;_&lt;count&gt;_geometry.txt</c> next to <c>status_&lt;subject&gt;_&lt;count&gt;.png</c>.
/// <para>
/// The launcher's status-icon rules are asserted there: the container is the hit box (width the hit box plus the game's 25 units),
/// at most two rows, columns = <c>max(ceil(n/2), ceil(containerWidth/pitch))</c>, the icon block centred on the container
/// (the hit box plus the game's 25 units) when it is wider (first icon at the hit box's left otherwise), icons at 52.6
/// canvas units normally and uniformly smaller when the block overhangs its container (per side at most 50 units or half the
/// gap to the neighbouring status block, never below 60% of normal), the block inside the canvas with an 8-unit margin,
/// every icon inside the canvas, no icon over another creature's body, and no creature's status icons touching another's.
/// </para>
/// <para>
/// Every applied power is also watched frame by frame while it is drawn (<see cref="StatusJumpProbe"/>): an icon that is
/// drawn more than 4 units from its settled position is the right-of-place flash a player reported. The hand/rail band is
/// measured and reported as information while the 9:16 space question is open.
/// </para>
/// </summary>
internal sealed partial class TestDriver
{
    // Two canvas units; touching rectangles are not an overlap.
    private const float StatusClearanceTolerance = 4f;
    // Status icons of different creatures may share at most this many units before they count as touching.
    private const float StatusBlockOverlapTolerance = 2f;
    // A drawn icon farther than this from where it settles is the one-frame jump the jump probe fails.
    private const float StatusJumpTolerance = 4f;

    // The agreed icon size on the 1080-wide canvas, the tolerance around it, the two-row
    // ceiling, the per-side overhang ceiling and the smallest an icon may shrink to (60% of normal).
    private const float StatusIconTargetWidth = 52.6f;
    private const float StatusIconWidthTolerance = 2f;
    private const int StatusMaxIconRows = 2;
    private const float StatusBlockOverhangLimit = 50f;
    private const float StatusIconMinShrink = 0.6f;
    // The status block keeps this far from the canvas edges.
    private const float StatusCanvasMargin = 8f;
    // What the game adds to the hit box's width for the power container (<c>NPowerContainer.SetCreatureBounds</c>).
    private const float StatusContainerExtraWidth = 25f;

    private const string StatusSmallEnemyEncounter = "NibbitsWeak";
    // Two small Nibbits in the front/back slots of one normal fight: the adjacent pair that exercises the neighbour limit.
    private const string StatusPairEncounter = "NibbitsNormal";
    private const string StatusBossEncounter = "CeremonialBeastBoss";
    private static readonly string[] StatusActs = { "Overgrowth", "Underdocks", "Hive", "Glory" };

    // The first drawn frame after an add is saved as a PNG once per subject and for every failing add.
    private readonly HashSet<string> _statusFirstFrameSaved = new();

    // Distinct powers, in apply order, so each one adds exactly one icon: stacking an existing type only changes its
    // number. All are visible on their owner, harmless without a turn or a card play (Regen heals, Thorns retaliates,
    // Blur/Buffer/Afterimage/Barricade only change a later turn's block, Ritual adds Strength at a turn end that does not
    // come, Burst/Duplication/Gigantification only change a card played later, Corruption only changes card costs,
    // EnergyNextTurn only pays at a turn start), and none opens a choice screen. Debuffs are applied before Artifact: an
    // Artifact charge would otherwise absorb the next debuff and its icon would never appear.
    private static readonly (Type Power, decimal Amount)[] StatusPlayerPlan =
    {
        (typeof(StrengthPower), 2m), (typeof(DexterityPower), 2m), (typeof(FocusPower), 2m),
        (typeof(ThornsPower), 2m), (typeof(RegenPower), 2m), (typeof(VigorPower), 2m), (typeof(BlurPower), 1m),
        (typeof(BufferPower), 1m), (typeof(AfterimagePower), 1m), (typeof(BarricadePower), 1m),
        (typeof(WeakPower), 1m), (typeof(FrailPower), 1m), (typeof(VulnerablePower), 1m), (typeof(RitualPower), 1m),
        (typeof(BurstPower), 1m), (typeof(CorruptionPower), 1m), (typeof(DuplicationPower), 1m),
        (typeof(EnergyNextTurnPower), 1m), (typeof(GigantificationPower), 1m), (typeof(ArtifactPower), 1m),
        // Spares, so a plan entry the creature already had cannot leave the row short of twenty icons.
        (typeof(AccuracyPower), 1m), (typeof(ArsenalPower), 1m),
    };

    private static readonly (Type Power, decimal Amount)[] StatusSmallEnemyPlan =
    {
        (typeof(WeakPower), 1m), (typeof(FrailPower), 1m), (typeof(VulnerablePower), 1m),
        (typeof(StrengthPower), 2m), (typeof(DexterityPower), 2m), (typeof(ThornsPower), 2m), (typeof(RegenPower), 2m),
    };

    private static readonly (Type Power, decimal Amount)[] StatusBossPlan =
    {
        (typeof(WeakPower), 1m), (typeof(FrailPower), 1m), (typeof(VulnerablePower), 1m),
        (typeof(StrengthPower), 2m), (typeof(DexterityPower), 2m), (typeof(FocusPower), 2m),
        (typeof(ThornsPower), 2m), (typeof(RegenPower), 2m), (typeof(BlurPower), 1m), (typeof(ArtifactPower), 1m),
    };

    // The icon scene's tween fields, looked up per node type so no game type outside the public surface is named.
    private static readonly Dictionary<Type, FieldInfo?> StatusPowerTweenFields = new();

    /// <summary>One power application's drawn-frame verdict, for the milestone geometry and the summary check.</summary>
    private sealed class StatusAddRecord
    {
        public string Power = "";
        public bool Ok;
        public string Summary = "";
        public string Detail = "";
    }

    private async Task RunStatusScalingAsync(RunState state)
    {
        _cancellation.CancelAfter(TimeSpan.FromMinutes(14));
        Player me = state.Players[0];
        me.Creature.SetMaxHpInternal(9999);
        me.Creature.SetCurrentHpInternal(me.Creature.MaxHp);
        Directory.CreateDirectory(_options.OutputDirectory);
        _report.Pass("status_fixture", $"character={me.Creature.Name} maxHp={me.Creature.MaxHp} " +
                                      $"window={GetViewportSize()}");

        ActModel act = ModelDb.Acts.First(a => StatusActs.Contains(a.GetType().Name)
            && a.AllEncounters.Any(e => e.GetType().Name == StatusSmallEnemyEncounter)
            && a.AllEncounters.Any(e => e.GetType().Name == StatusPairEncounter)
            && a.AllEncounters.Any(e => e.GetType().Name == StatusBossEncounter));
        _report.Pass("status_act", act.GetType().Name);
        await ConfigureTourActAsync(state, act.GetType().Name);

        // A lone small enemy in a normal fight.
        NCombatRoom room = await EnterStatusEncounterAsync(state, me, StatusSmallEnemyEncounter);
        NCreature small = Enemies(room).OrderBy(c => c.Hitbox.GetGlobalRect().Size.Y).First();
        _report.Pass("status_small_enemy", $"encounter={StatusSmallEnemyEncounter} monster={StatusBlockName(small)} " +
                                          $"body={Q(small.Hitbox.GetGlobalRect())}");
        await ApplyStatusMilestonesAsync(room, small, StatusSmallEnemyPlan, new[] { 5, 7 }, "small_enemy");

        // Two adjacent small enemies, seven statuses each: the neighbour-limit fixture.
        room = await EnterStatusEncounterAsync(state, me, StatusPairEncounter);
        List<NCreature> pair = Enemies(room);
        if (pair.Count < 2)
        {
            _report.Fail("status_small_enemy_pair_fixture",
                $"{StatusPairEncounter} added {pair.Count} enemy(ies); two are needed");
        }
        else
        {
            (NCreature a, NCreature b) = NearestCreaturePair(pair);
            Rect2 boxA = a.Hitbox.GetGlobalRect();
            Rect2 boxB = b.Hitbox.GetGlobalRect();
            float hitboxGap = MathF.Max(boxA.Position.X, boxB.Position.X) - MathF.Min(boxA.End.X, boxB.End.X);
            _report.Pass("status_small_enemy_pair",
                $"encounter={StatusPairEncounter} a={StatusBlockName(a)} b={StatusBlockName(b)} " +
                $"hitboxGap={Q(hitboxGap, 1)} aBody={Q(boxA)} bBody={Q(boxB)}");
            (List<string>? appliedA, List<StatusAddRecord> addsA) =
                await ApplyStatusesUntilAsync(room, a, StatusSmallEnemyPlan, 7, "small_enemy_pair_a");
            (List<string>? appliedB, List<StatusAddRecord> addsB) = appliedA is null
                ? (null, new List<StatusAddRecord>())
                : await ApplyStatusesUntilAsync(room, b, StatusSmallEnemyPlan, 7, "small_enemy_pair_b");
            if (appliedA is not null && appliedB is not null)
            {
                CheckStatusAdds("status_small_enemy_pair_07", addsA.Concat(addsB).ToList());
                await CheckStatusPairAsync(room, a, b, appliedA, appliedB, addsA, addsB, hitboxGap);
            }
        }

        // The act's big boss; the same plan reaches 5, 7 and 10 icons on it.
        room = await EnterStatusEncounterAsync(state, me, StatusBossEncounter);
        NCreature boss = Enemies(room).OrderByDescending(c => c.Hitbox.GetGlobalRect().Size.Y).First();
        _report.Pass("status_boss", $"encounter={StatusBossEncounter} monster={StatusBlockName(boss)} " +
                                   $"body={Q(boss.Hitbox.GetGlobalRect())}");
        await ApplyStatusMilestonesAsync(room, boss, StatusBossPlan, new[] { 5, 7, 10 }, "boss");

        // The player last, in the boss room: the player is present in every fight, and the room's crowd is the state the
        // layout has to survive (the boss's ten icons are its neighbour).
        NCreature player = room.CreatureNodes.First(c => c.Entity.IsPlayer);
        await ApplyStatusMilestonesAsync(room, player, StatusPlayerPlan, new[] { 5, 7, 14, 20 }, "player");
    }

    private static List<NCreature> Enemies(NCombatRoom room) => room.CreatureNodes
        .Where(c => GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion() && c.Entity.IsMonster && !c.Entity.IsPet
                    && c.Entity.IsAlive)
        .ToList();

    /// <summary>The two creatures whose hit boxes are closest horizontally: the neighbours the overhang limit is about.</summary>
    private static (NCreature A, NCreature B) NearestCreaturePair(List<NCreature> creatures)
    {
        NCreature bestA = creatures[0];
        NCreature bestB = creatures[1];
        float best = float.MaxValue;
        for (int i = 0; i < creatures.Count; i++)
        for (int j = i + 1; j < creatures.Count; j++)
        {
            Rect2 a = creatures[i].Hitbox.GetGlobalRect();
            Rect2 b = creatures[j].Hitbox.GetGlobalRect();
            float distance = MathF.Abs(a.Position.X + a.Size.X * 0.5f - (b.Position.X + b.Size.X * 0.5f));
            if (distance >= best) continue;
            best = distance;
            bestA = creatures[i];
            bestB = creatures[j];
        }
        return bestA.Hitbox.GetGlobalRect().Position.X <= bestB.Hitbox.GetGlobalRect().Position.X
            ? (bestA, bestB)
            : (bestB, bestA);
    }

    private async Task<NCombatRoom> EnterStatusEncounterAsync(RunState state, Player me, string name)
    {
        me.Creature.SetCurrentHpInternal(me.Creature.MaxHp);
        EncounterModel encounter = state.Act.AllEncounters.First(e => e.GetType().Name == name).ToMutable();
        await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: encounter, showTransition: false);
        await WaitUntilAsync(IsPlayerPlayPhase, name + " player phase", TimeSpan.FromSeconds(45));
        await WaitUntilStableAsync(StatusIntroSignature, name + " intro settle", TimeSpan.FromSeconds(4.5));
        _report.Phase("combat_entered", name);
        return NCombatRoom.Instance ?? throw new InvalidOperationException($"Combat room disappeared during {name}.");
    }

    /// <summary>Pending until the room and hand are ready and the entry/turn banners have gone, like the stage fixture.</summary>
    private string? StatusIntroSignature()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        if (room is null || !GodotObject.IsInstanceValid(room) || !room.IsInsideTree()) return null;
        if (room.GetChildren().Any(child => child is NCombatStartBanner or NPlayerTurnBanner or NEnemyTurnBanner))
            return Pending("room.banner");
        if (!IsPlayerPlayPhase()) return Pending("room.playPhase");
        if (room.Ui?.Hand is not { } hand || hand.ActiveHolders.Count == 0) return Pending("room.hand");
        return CombatSettledSignature();
    }

    /// <summary>Applies the plan until the target shows the milestone's icon count, capturing each milestone.</summary>
    private async Task ApplyStatusMilestonesAsync(NCombatRoom room, NCreature target, (Type Power, decimal Amount)[] plan,
        int[] milestones, string subject)
    {
        var applied = new List<string>();
        string label = StatusBlockName(target);
        foreach (int milestone in milestones)
        {
            (List<string>? appliedSince, List<StatusAddRecord> addsSince) =
                await ApplyStatusesUntilAsync(room, target, plan, milestone, subject);
            if (appliedSince is null) return;
            applied.AddRange(appliedSince);
            CheckStatusAdds($"status_{subject}_{milestone:00}", addsSince);
            await CheckStatusMilestoneAsync(room, target, subject, milestone, applied, addsSince, label);
        }
    }

    /// <summary>
    /// Applies the next distinct power until the target shows <paramref name="milestone"/> icons, or returns null after
    /// recording the fixture failure when the plan runs out. Each application is watched on every drawn frame by
    /// <see cref="StatusJumpProbe"/> from just before the apply until the layout settles.
    /// </summary>
    private async Task<(List<string>? Applied, List<StatusAddRecord> Adds)> ApplyStatusesUntilAsync(NCombatRoom room,
        NCreature target, (Type Power, decimal Amount)[] plan, int milestone, string subject)
    {
        var existing = target.Entity.Powers.Select(p => p.GetType().Name).ToHashSet(StringComparer.Ordinal);
        var queue = new Queue<(Type Power, decimal Amount)>(
            plan.Where(entry => !existing.Contains(entry.Power.Name)));
        var applied = new List<string>();
        var adds = new List<StatusAddRecord>();
        _report.Pass($"status_{subject}_{milestone:00}_fixture",
            $"creature={StatusBlockName(target)} prePowers=[{string.Join(", ", existing)}] " +
            $"queued={queue.Count} targetIcons={milestone}");
        while (StatusIcons(target).Count < milestone)
        {
            if (queue.Count == 0)
            {
                _report.Fail($"status_{subject}_{milestone:00}_fixture_exhausted",
                    $"only {StatusIcons(target).Count} icon(s) of the {milestone} needed; " +
                    $"applied=[{string.Join(", ", applied)}]");
                return (null, adds);
            }
            (Type power, decimal amount) = queue.Dequeue();
            int index = adds.Count + 1;
            string step = $"status_{subject}_add_{index:00}";
            var probe = new StatusJumpProbe(_screenshots, _report, room, subject, index);
            probe.Begin();
            await ApplyStatusPowerAsync(power, target.Entity, amount);
            if (target.Entity.Powers.Any(p => p.GetType() == power)) applied.Add(power.Name);
            await WaitUntilStableAsync(StatusSettledSignature,
                $"{subject} {applied.Count} power(s) settle", TimeSpan.FromSeconds(2.5));
            bool saveFirstFrame = _statusFirstFrameSaved.Add(subject);
            (bool ok, string summary, string detail) = probe.Finish(step, saveFirstFrame);
            _report.Check(step + "_status_icons_no_jump", ok, detail);
            adds.Add(new StatusAddRecord { Power = power.Name, Ok = ok, Summary = summary, Detail = detail });
        }
        return (applied, adds);
    }

    /// <summary>The milestone's aggregate of the drawn-frame watches that produced this state.</summary>
    private void CheckStatusAdds(string step, IReadOnlyList<StatusAddRecord> adds)
    {
        List<StatusAddRecord> failed = adds.Where(add => !add.Ok).ToList();
        _report.Check(step + "_status_icons_no_jump", failed.Count == 0,
            failed.Count == 0
                ? $"{adds.Count} add(s) drew every icon within {StatusJumpTolerance:F0} units of its settled position"
                : string.Join(" | ", failed.Select(add => $"{add.Power}: {add.Detail}")));
    }

    /// <summary>The native command, one branch per plan type so no reflection is needed to build a mutable model.</summary>
    private async Task<PowerModel?> ApplyStatusPowerAsync(Type power, Creature target, decimal amount)
    {
        var context = new ThrowingPlayerChoiceContext();
        // silent: true keeps the power-applied VFX out of the captures; the icon node and its row still appear.
        if (power == typeof(StrengthPower))
            return await PowerCmd.Apply<StrengthPower>(context, target, amount, null, null, silent: true);
        if (power == typeof(DexterityPower))
            return await PowerCmd.Apply<DexterityPower>(context, target, amount, null, null, silent: true);
        if (power == typeof(FocusPower))
            return await PowerCmd.Apply<FocusPower>(context, target, amount, null, null, silent: true);
        if (power == typeof(ThornsPower))
            return await PowerCmd.Apply<ThornsPower>(context, target, amount, null, null, silent: true);
        if (power == typeof(RegenPower))
            return await PowerCmd.Apply<RegenPower>(context, target, amount, null, null, silent: true);
        if (power == typeof(VigorPower))
            return await PowerCmd.Apply<VigorPower>(context, target, amount, null, null, silent: true);
        if (power == typeof(BlurPower))
            return await PowerCmd.Apply<BlurPower>(context, target, amount, null, null, silent: true);
        if (power == typeof(BufferPower))
            return await PowerCmd.Apply<BufferPower>(context, target, amount, null, null, silent: true);
        if (power == typeof(AfterimagePower))
            return await PowerCmd.Apply<AfterimagePower>(context, target, amount, null, null, silent: true);
        if (power == typeof(BarricadePower))
            return await PowerCmd.Apply<BarricadePower>(context, target, amount, null, null, silent: true);
        if (power == typeof(WeakPower))
            return await PowerCmd.Apply<WeakPower>(context, target, amount, null, null, silent: true);
        if (power == typeof(FrailPower))
            return await PowerCmd.Apply<FrailPower>(context, target, amount, null, null, silent: true);
        if (power == typeof(VulnerablePower))
            return await PowerCmd.Apply<VulnerablePower>(context, target, amount, null, null, silent: true);
        if (power == typeof(RitualPower))
            return await PowerCmd.Apply<RitualPower>(context, target, amount, null, null, silent: true);
        if (power == typeof(BurstPower))
            return await PowerCmd.Apply<BurstPower>(context, target, amount, null, null, silent: true);
        if (power == typeof(CorruptionPower))
            return await PowerCmd.Apply<CorruptionPower>(context, target, amount, null, null, silent: true);
        if (power == typeof(DuplicationPower))
            return await PowerCmd.Apply<DuplicationPower>(context, target, amount, null, null, silent: true);
        if (power == typeof(EnergyNextTurnPower))
            return await PowerCmd.Apply<EnergyNextTurnPower>(context, target, amount, null, null, silent: true);
        if (power == typeof(GigantificationPower))
            return await PowerCmd.Apply<GigantificationPower>(context, target, amount, null, null, silent: true);
        if (power == typeof(ArtifactPower))
            return await PowerCmd.Apply<ArtifactPower>(context, target, amount, null, null, silent: true);
        if (power == typeof(AccuracyPower))
            return await PowerCmd.Apply<AccuracyPower>(context, target, amount, null, null, silent: true);
        if (power == typeof(ArsenalPower))
            return await PowerCmd.Apply<ArsenalPower>(context, target, amount, null, null, silent: true);
        throw new ArgumentException($"No status plan branch for {power.Name}.");
    }

    /// <summary>
    /// Combat plus every icon's rectangle, so two equal samples mean the icons have stopped moving in; pending while an
    /// icon's own scale-in tween is running, like the other native animations the shared settles read.
    /// </summary>
    private string? StatusSettledSignature()
    {
        string? roomSample = CombatSettledSignature();
        if (roomSample is null || IsPending(roomSample)) return roomSample;
        NCombatRoom? room = NCombatRoom.Instance;
        if (room is null || !GodotObject.IsInstanceValid(room)) return null;
        var builder = new StringBuilder(roomSample);
        foreach (NCreature creature in room.CreatureNodes)
        {
            if (!GodotObject.IsInstanceValid(creature) || creature.IsQueuedForDeletion()) continue;
            Control? container = StatusPowerContainer(creature);
            if (container is null) continue;
            builder.Append("p.").Append(creature.GetInstanceId()).Append(".container=")
                .Append(Q(container.GetGlobalRect())).Append('|');
            foreach (Control icon in StatusIcons(creature))
            {
                if (StatusIconAnimating(icon)) return Pending($"c.{creature.GetInstanceId()}.powerTween");
                builder.Append("p.").Append(creature.GetInstanceId()).Append('.').Append(icon.GetInstanceId())
                    .Append(".rect=").Append(Q(StatusIconRect(icon))).Append('|');
            }
        }
        return builder.ToString();
    }

    /// <summary>One creature's milestone state: measure, check, write its geometry file and capture it.</summary>
    private async Task CheckStatusMilestoneAsync(NCombatRoom room, NCreature target, string subject, int milestone,
        IReadOnlyList<string> applied, IReadOnlyList<StatusAddRecord> adds, string label)
    {
        string step = $"status_{subject}_{milestone:00}";
        await WaitUntilStableAsync(StatusSettledSignature, step + " settle", TimeSpan.FromSeconds(2.5));
        var lines = new List<string>();
        RecordStatusState(room, target, step, applied, adds, label, lines);
        CheckStatusBlocksDisjoint(step, room);
        string path = Path.Combine(_options.OutputDirectory, step + "_geometry.txt");
        File.WriteAllLines(path, lines);
        _report.Pass(step + "_geometry_file", path);
        await CaptureAsync(step);
    }

    /// <summary>The adjacent pair: both creatures' geometry in one file, one capture, plus the room-wide disjointness.</summary>
    private async Task CheckStatusPairAsync(NCombatRoom room, NCreature a, NCreature b, IReadOnlyList<string> appliedA,
        IReadOnlyList<string> appliedB, IReadOnlyList<StatusAddRecord> addsA, IReadOnlyList<StatusAddRecord> addsB,
        float hitboxGap)
    {
        const string step = "status_small_enemy_pair_07";
        await WaitUntilStableAsync(StatusSettledSignature, step + " settle", TimeSpan.FromSeconds(2.5));
        var lines = new List<string>
        {
            $"subject=small_enemy_pair milestone=7 encounter={StatusPairEncounter} hitboxGap={Q(hitboxGap, 1)}",
        };
        RecordStatusState(room, a, step + "_a", appliedA, addsA, StatusBlockName(a), lines);
        RecordStatusState(room, b, step + "_b", appliedB, addsB, StatusBlockName(b), lines);
        CheckStatusBlocksDisjoint(step, room);
        string path = Path.Combine(_options.OutputDirectory, step + "_geometry.txt");
        File.WriteAllLines(path, lines);
        _report.Pass(step, $"encounter={StatusPairEncounter} " +
                           $"a={StatusBlockName(a)} icons=7 b={StatusBlockName(b)} icons=7 hitboxGap={Q(hitboxGap, 1)}");
        _report.Pass(step + "_geometry_file", path);
        await CaptureAsync(step);
    }

    /// <summary>Measures one creature's status block, appends its geometry and records the layout checks.</summary>
    private void RecordStatusState(NCombatRoom room, NCreature target, string step, IReadOnlyList<string> applied,
        IReadOnlyList<StatusAddRecord> adds, string label, List<string> lines)
    {
        Rect2 canvas = _tree.Root.GetViewport().GetVisibleRect();
        Control? display = StatusDisplay(target);
        Control? container = StatusPowerContainer(target);
        List<Control> icons = StatusIcons(target);
        List<Rect2> rects = icons.Select(StatusIconRect).ToList();
        List<StatusRow> rows = StatusIconRowList(rects);
        int columns = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
        float pitch = StatusIconPitch(rows, rects);
        Rect2 body = target.Hitbox.GetGlobalRect();
        Rect2 visual = GodotObject.IsInstanceValid(target.Visuals) ? target.Visuals.Bounds.GetGlobalRect() : new Rect2();
        Rect2 barRect = StatusHealthBar(target)?.GetGlobalRect() ?? new Rect2();
        Rect2 containerRect = container?.GetGlobalRect() ?? new Rect2();
        Rect2 block = StatusUnion(rects);
        float unit = display?.GetGlobalTransform().Scale.X ?? 0f;
        float iconWidth = rects.Count == 0 ? 0f : rects.Average(r => r.Size.X);
        float iconHeight = rects.Count == 0 ? 0f : rects.Average(r => r.Size.Y);
        float iconMinWidth = rects.Count == 0 ? 0f : rects.Min(r => r.Size.X);
        float iconMaxWidth = rects.Count == 0 ? 0f : rects.Max(r => r.Size.X);
        float bodyHeight = Math.Max(1f, body.Size.Y);
        float barWidth = Math.Max(1f, barRect.Size.X);
        float containerWidth = Math.Max(1f, containerRect.Size.X);
        float expectedContainerWidth = body.Size.X + StatusContainerExtraWidth * MathF.Max(unit, 0.01f);
        // The native grid uses max(ceil(n/2), ceil(containerWidth/icon)) columns; with fewer icons than that the first
        // row simply holds every icon, so the expectation is capped by the icon count. The launcher computes the columns
        // from the normal icon width and keeps them when the icons shrink, so both bases are accepted.
        int ColumnFormula(float iconWidth) => Math.Max((int)MathF.Ceiling(rects.Count * 0.5f),
            (int)MathF.Ceiling(containerWidth / MathF.Max(1f, iconWidth)));
        int expectedColumns = rects.Count == 0 ? 0 : Math.Min(rects.Count, ColumnFormula(StatusIconTargetWidth));
        int expectedColumnsShrunk = rects.Count == 0 ? 0 : Math.Min(rects.Count, ColumnFormula(pitch));

        int columnsNative = Math.Max((int)MathF.Ceiling(rects.Count * 0.5f),
            (int)MathF.Ceiling(expectedContainerWidth / StatusIconTargetWidth));
        // The first row holds min(columns, count) icons (StatusIconMath.used), so a short block cannot overhang.
        float blockWidthNative = Math.Min(columnsNative, rects.Count) * StatusIconTargetWidth;
        // The shrink rule is measured against the container the hit box defines (hit box + the native padding), which is
        // the same container the launcher plans from. The drawn container is as wide as the launcher wrote it (columns x
        // the native icon), so using it here hid a legitimate shrink and demanded the normal size.
        float overhangNative = MathF.Max(0f, (blockWidthNative - expectedContainerWidth) * 0.5f);
        // The neighbour limits come from the untouched base blocks, exactly as the launcher plans them: every creature's
        // normal-size block centred on its own hit-box container. The drawn blocks are already shrunk by those limits, so
        // measuring the gap between them made the harness believe a shrunken block had room it never had.
        Rect2 selfBase = StatusBaseBlock(target, rects.Count, block, canvas);
        (float allowedLeft, float allowedRight) = StatusNeighbourLimits(selfBase, block, room, target, canvas);
        // "Disjoint on screen" is reported, not used: two blocks that had to shrink are disjoint precisely because they
        // shrank, so the shrink is expected whenever the base blocks would overhang past their limits.
        bool blocksDisjoint = true;
        float nearestNeighbourGap = float.PositiveInfinity;
        foreach ((NCreature other, List<Rect2> otherIcons) in StatusBlocks(room))
        {
            if (other == target || otherIcons.Count == 0) continue;
            Rect2 otherBlock = StatusUnion(otherIcons);
            float gap = MathF.Max(otherBlock.Position.X - block.End.X, block.Position.X - otherBlock.End.X);
            nearestNeighbourGap = MathF.Min(nearestNeighbourGap, gap);
            if (gap < -0.5f) blocksDisjoint = false;
        }
        bool shrinkExpected = overhangNative > MathF.Min(allowedLeft, allowedRight);
        // How far the drawn block reaches past the container the rule is about, which is not the drawn container (the
        // launcher writes that as wide as the block, so measuring against it always read zero).
        float leftOverhang = MathF.Max(0f, body.Position.X - block.Position.X);
        float rightOverhang = MathF.Max(0f, block.End.X - (body.Position.X + expectedContainerWidth));
        bool atFloor = rects.Count > 0 && iconMinWidth <= StatusIconTargetWidth * StatusIconMinShrink + 0.5f;

        // The documented rule (StatusIconMath): the container starts at the hit box's left and is as wide as the hit box
        // plus the native 25-unit padding; a block wider than that container is centred on it, otherwise the first icon
        // starts at the hit box's left. The canvas margin clamp (8 units) may then move the block back in from an edge.
        float innerLeft = canvas.Position.X + StatusCanvasMargin;
        float innerRight = canvas.End.X - StatusCanvasMargin;
        float unclampedLeft = block.Size.X > expectedContainerWidth
            ? body.Position.X + (expectedContainerWidth - block.Size.X) * 0.5f
            : body.Position.X;
        float expectedLeft = block.Size.X <= MathF.Max(0f, innerRight - innerLeft)
            ? Math.Clamp(unclampedLeft, innerLeft, innerRight - block.Size.X)
            : innerLeft;
        bool clamped = MathF.Abs(expectedLeft - unclampedLeft) > 0.5f;
        float placementDelta = block.Position.X - expectedLeft;
        // A block that cannot sit centred steps off the neighbour's status block or its body — the same two edges the
        // launcher treats as hard — and, at the 60% floor, may take back the room the tighter overhang limit denied it.
        // The room is measured from the plain native place, so no offset beyond that documented move is accepted.
        float shiftRoomRight = 0f, shiftRoomLeft = 0f;
        float unshiftedRight = expectedLeft + block.Size.X;
        float blockCentre = block.Position.X + block.Size.X * 0.5f;
        float nearestRightEdge = float.PositiveInfinity;
        float nearestLeftEdge = float.NegativeInfinity;
        foreach ((NCreature other, List<Rect2> otherIcons) in StatusBlocks(room))
        {
            if (other == target || otherIcons.Count == 0) continue;
            Rect2 otherDrawn = StatusUnion(otherIcons);
            Rect2 otherBody = other.Hitbox.GetGlobalRect();
            // The neighbour's block limits this row only when the two blocks share a vertical band; its BODY counts
            // whenever it stands in this block's band, including from another foot line (a taller front- or back-rank
            // body reaches under this row's icons), which is the edge the launcher steps off.
            bool blockRow = otherDrawn.End.Y > block.Position.Y && block.End.Y > otherDrawn.Position.Y;
            bool bodyRow = otherBody.End.Y > block.Position.Y && block.End.Y > otherBody.Position.Y;
            if (!blockRow && !bodyRow) continue;
            if (blockRow)
            {
                Rect2 otherBase = StatusBaseBlock(other, otherIcons.Count, otherDrawn, canvas);
                if (otherBase.Position.X + otherBase.Size.X * 0.5f >= blockCentre)
                    nearestRightEdge = MathF.Min(nearestRightEdge, otherBase.Position.X);
                else
                    nearestLeftEdge = MathF.Max(nearestLeftEdge, otherBase.End.X);
            }
            if (bodyRow)
            {
                if (otherBody.Position.X + otherBody.Size.X * 0.5f >= blockCentre)
                    nearestRightEdge = MathF.Min(nearestRightEdge, otherBody.Position.X);
                else
                    nearestLeftEdge = MathF.Max(nearestLeftEdge, otherBody.End.X);
            }
        }
        if (!float.IsPositiveInfinity(nearestRightEdge))
            shiftRoomRight = MathF.Max(0f, unshiftedRight - nearestRightEdge);
        if (!float.IsNegativeInfinity(nearestLeftEdge))
            shiftRoomLeft = MathF.Max(0f, nearestLeftEdge - expectedLeft);
        shiftRoomRight = MathF.Max(shiftRoomRight, rightOverhang - allowedRight);
        shiftRoomLeft = MathF.Max(shiftRoomLeft, leftOverhang - allowedLeft);
        bool shiftAllowed = (placementDelta < 0f && -placementDelta <= shiftRoomRight + 1f)
            || (placementDelta > 0f && placementDelta <= shiftRoomLeft + 1f);

        var powers = target.Entity.Powers;
        lines.Add($"subject={step} creature={label} visibleIcons={icons.Count} appliedPowers={applied.Count} " +
                  $"powers=[{string.Join(", ", powers.Select(p => p.GetType().Name))}]");
        lines.Add($"canvas={Q(canvas)} body={Q(body)} visuals={Q(visual)} " +
                  $"creatureScale={Q(target.GetGlobalTransform().Scale.X, 3)} displayScale={Q(unit, 3)}");
        lines.Add($"healthBar={Q(barRect)} barWidth={Q(barRect.Size.X, 1)}");
        lines.Add($"powerContainer={Q(containerRect)} containerWidth={Q(containerWidth, 1)} " +
                  $"expectedContainerWidth={Q(expectedContainerWidth, 1)} containerLocal={Q(container?.Position ?? Vector2.Zero, 1)}");
        lines.Add($"rows={rows.Count} rowCounts=[{string.Join(",", rows.Select(row => row.Count))}] columns={columns} " +
                  $"expectedColumns={expectedColumns} expectedColumnsShrunk={expectedColumnsShrunk} " +
                  $"pitch={Q(pitch, 1)} iconCanvas={Q(iconWidth, 1)}x{Q(iconHeight, 1)} " +
                  $"iconHeightOverBody={Q(iconHeight / bodyHeight, 3)} iconWidthOverBar={Q(iconWidth / barWidth, 3)}");
        lines.Add($"iconLayout=block={Q(block)} blockWidth={Q(block.Size.X, 1)} blockLeft={Q(block.Position.X, 1)} " +
                  $"blockCentre={Q(block.Position.X + block.Size.X * 0.5f, 1)} expectedLeft={Q(expectedLeft, 1)} " +
                  $"placementDelta={Q(placementDelta, 1)} clamp={(clamped ? "yes" : "no")} " +
                  $"overhangNative={Q(overhangNative, 1)} overhangLeft={Q(leftOverhang, 1)} " +
                  $"overhangRight={Q(rightOverhang, 1)} allowedLeft={Q(allowedLeft, 1)} " +
                  $"allowedRight={Q(allowedRight, 1)} sizeRule={(shrinkExpected ? "shrunk" : "normal")} " +
                  $"floor={atFloor} iconWidth={Q(iconMinWidth, 1)}..{Q(iconMaxWidth, 1)} canvasMargin={StatusCanvasMargin:F0}");
        lines.Add($"jumps=adds:{adds.Count} failed:{adds.Count(add => !add.Ok)} " +
                  $"detail=[{string.Join(", ", adds.Select(add => add.Power + ":" + add.Summary))}]");
        for (int i = 0; i < icons.Count; i++)
        {
            string type = i < powers.Count ? powers[i].GetType().Name : "?";
            string amount = icons[i].GetNodeOrNull<Label>("%AmountLabel")?.Text
                            ?? icons[i].GetNodeOrNull<Label>("AmountLabel")?.Text ?? "";
            lines.Add($"icon[{i}] type={type} amount={amount} rect={Q(rects[i])} size={Q(rects[i].Size, 1)} " +
                      $"nodeSize={Q(icons[i].Size, 1)} scale={Q(icons[i].GetGlobalTransform().Scale.X, 3)} " +
                      $"left={Q(rects[i].Position.X, 1)} top={Q(rects[i].Position.Y, 1)}");
        }

        // Rows and columns: at most two rows, and the native grid's column count (capped by the icon count).
        _report.Check(step + "_icon_rows", rows.Count <= StatusMaxIconRows,
            $"rows={rows.Count} rowCounts=[{string.Join(",", rows.Select(row => row.Count))}] " +
            $"icons={rects.Count} (max {StatusMaxIconRows} rows)");
        _report.Check(step + "_icon_columns",
            rects.Count == 0 || Math.Abs(columns - expectedColumns) <= 1
                             || Math.Abs(columns - expectedColumnsShrunk) <= 1,
            $"columns={columns} expected={expectedColumns} (shrunk-base {expectedColumnsShrunk}) n={rects.Count} " +
            $"containerWidth={Q(containerWidth, 1)} pitch={Q(pitch, 1)}");

        // The block: starts at the hit box's left, centred on the container when it is wider, stepped off an overlapping
        // neighbour, and inside the canvas with the margin (a canvas clamp is allowed and recorded).
        _report.Check(step + "_native_layout", rects.Count > 0 && (MathF.Abs(placementDelta) <= 4f || shiftAllowed),
            $"block={Q(block)} blockWidth={Q(block.Size.X, 1)} body={Q(body)} " +
            $"containerWidth={Q(expectedContainerWidth, 1)} expectedLeft={Q(expectedLeft, 1)} " +
            $"actualLeft={Q(block.Position.X, 1)} delta={Q(placementDelta, 1)} clamped={clamped} " +
            $"shiftAllowed={shiftAllowed} shiftRoom={Q(shiftRoomLeft, 1)}/{Q(shiftRoomRight, 1)} " +
            $"margin={StatusCanvasMargin:F0}");
        var inner = new Rect2(innerLeft, canvas.Position.Y + StatusCanvasMargin,
            MathF.Max(0f, canvas.Size.X - 2f * StatusCanvasMargin),
            MathF.Max(0f, canvas.Size.Y - 2f * StatusCanvasMargin));
        bool blockInside = rects.Count > 0 && inner.Grow(1f).Encloses(block);
        _report.Check(step + "_block_inside_canvas", blockInside,
            blockInside ? $"block={Q(block)} inside {Q(inner)}" : $"block={Q(block)} outside {Q(inner)}");

        // The icon size: the normal 52.6 up to where the block overhangs its container, then a uniform shrink that keeps
        // each side within 50 units or half the gap to the neighbouring status block (never below 60% of normal).
        string widthDetail = $"icons={rects.Count} widths={Q(iconMinWidth, 1)}..{Q(iconMaxWidth, 1)} " +
                             $"rule={(shrinkExpected ? "shrunk" : "normal")} " +
                             $"overhangNative={Q(overhangNative, 1)} " +
                             $"allowed={Q(allowedLeft, 1)}/{Q(allowedRight, 1)} " +
                             $"overhang={Q(leftOverhang, 1)}/{Q(rightOverhang, 1)} floor={atFloor} " +
                             $"blocksDisjoint={blocksDisjoint} neighbourGap={Q(nearestNeighbourGap, 1)}";
        bool sizeOk = rects.Count > 0 && (shrinkExpected
            ? iconMaxWidth < StatusIconTargetWidth - 1f
              && iconMinWidth >= StatusIconTargetWidth * StatusIconMinShrink - 0.5f
              // The canvas margin outranks the overhang limits: a wide block pushed in from an edge may reach further
              // past its container than 50 units, which is the canvas's decision, not a shrink the rule missed.
              && ((leftOverhang <= allowedLeft + 2f && rightOverhang <= allowedRight + 2f) || atFloor || clamped)
            : iconMinWidth >= StatusIconTargetWidth - StatusIconWidthTolerance
              && iconMaxWidth <= StatusIconTargetWidth + StatusIconWidthTolerance);
        _report.Check(step + "_icon_size", sizeOk, widthDetail);

        List<Rect2> outside = rects.Where(rect => !canvas.Grow(1f).Encloses(rect)).ToList();
        _report.Check(step + "_icons_inside_canvas", outside.Count == 0,
            outside.Count == 0
                ? $"{rects.Count} icon(s) inside {canvas}"
                : $"outside=[{string.Join("; ", outside)}] canvas={canvas}");
        lines.Add($"canvasClearance={(outside.Count == 0 ? "inside" : "outside:" + string.Join("; ", outside))}");

        List<NCreature> others = room.CreatureNodes
            .Where(c => c != target && GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion() && c.Entity.IsAlive)
            .ToList();
        bool bodiesClear = true;
        string worst = "none";
        float worstArea = 0f;
        foreach (Rect2 icon in rects)
        foreach (NCreature other in others)
        {
            Rect2 otherBody = other.Hitbox.GetGlobalRect();
            if (!StatusOverlaps(icon, otherBody, StatusClearanceTolerance)) continue;
            bodiesClear = false;
            Rect2 covered = icon.Intersection(otherBody);
            float area = covered.Size.X * covered.Size.Y;
            if (area <= worstArea) continue;
            worstArea = area;
            worst = $"icon={icon} vs {StatusBlockName(other)} body={otherBody} overlap={covered.Size}";
        }
        lines.Add($"creatureClearance={(bodiesClear ? "clear" : "overlap:" + worst)} others={others.Count}");
        _report.Check(step + "_creature_clearance", bodiesClear,
            bodiesClear
                ? $"{rects.Count} icon(s) clear of {others.Count} other creature body/bodies"
                : $"worst={worst}");

        // The launcher's hand/rail band, reported as information.
        CheckStatusBands(step, canvas, rects, lines);

        _report.Pass(step, $"icons={icons.Count} rows={rows.Count} columns={columns} " +
                           $"icon={Q(iconWidth, 1)}x{Q(iconHeight, 1)} " +
                           $"iconHeight/bodyHeight={Q(iconHeight / bodyHeight, 2)} " +
                           $"iconWidth/barWidth={Q(iconWidth / barWidth, 2)} " +
                           $"applied=[{string.Join(", ", applied)}]");
    }

    /// <summary>No creature's status icons may touch another creature's status icons (2-unit tolerance).</summary>
    private void CheckStatusBlocksDisjoint(string step, NCombatRoom room)
    {
        List<(NCreature Creature, List<Rect2> Rects)> blocks = StatusBlocks(room);
        bool clear = true;
        string worst = "none";
        float worstArea = 0f;
        for (int i = 0; i < blocks.Count; i++)
        for (int j = i + 1; j < blocks.Count; j++)
        foreach (Rect2 a in blocks[i].Rects)
        foreach (Rect2 b in blocks[j].Rects)
        {
            if (!StatusOverlaps(a, b, StatusBlockOverlapTolerance)) continue;
            clear = false;
            Rect2 covered = a.Intersection(b);
            float area = covered.Size.X * covered.Size.Y;
            if (area <= worstArea) continue;
            worstArea = area;
            worst = $"{StatusBlockName(blocks[i].Creature)} icon={a} vs " +
                    $"{StatusBlockName(blocks[j].Creature)} icon={b} overlap={covered.Size}";
        }
        _report.Check(step + "_status_blocks_disjoint", clear,
            clear
                ? $"{blocks.Count} status block(s) disjoint (tolerance {StatusBlockOverlapTolerance})"
                : $"worst={worst}");
    }

    private static Control? StatusDisplay(NCreature creature) =>
        creature.GetNodeOrNull<Control>("%HealthBar") ?? creature.GetNodeOrNull<Control>("HealthBar");

    private static Control? StatusPowerContainer(NCreature creature)
    {
        Control? display = StatusDisplay(creature);
        return display?.GetNodeOrNull<Control>("%PowerContainer")
               ?? display?.GetNodeOrNull<Control>("PowerContainer");
    }

    private static Control? StatusHealthBar(NCreature creature) =>
        creature.GetNodeOrNull<Control>("HealthBar/HealthBar/HpBarContainer")
        ?? creature.GetNodeOrNull<Control>("HealthBar/HealthBar");

    /// <summary>The drawn icons: the power container only ever holds icon nodes (NPowerContainer.Add).</summary>
    private static List<Control> StatusIcons(NCreature creature)
    {
        Control? container = StatusPowerContainer(creature);
        if (container is null) return new List<Control>();
        return container.GetChildren().OfType<Control>()
            .Where(icon => !icon.IsQueuedForDeletion() && icon.IsVisibleInTree())
            .ToList();
    }

    /// <summary>The icon's drawn rectangle on the canvas, with the icon art as a fallback for a zero-sized control.</summary>
    private static Rect2 StatusIconRect(Control icon)
    {
        Rect2 rect = icon.GetGlobalRect();
        if (rect.Size.X > 1f && rect.Size.Y > 1f) return rect;
        Control? art = icon.GetNodeOrNull<Control>("%Icon") ?? icon.GetNodeOrNull<Control>("Icon");
        if (art is not null && GodotObject.IsInstanceValid(art))
        {
            Rect2 artRect = art.GetGlobalRect();
            if (artRect.Size.X > 1f && artRect.Size.Y > 1f) return artRect;
        }
        Vector2 local = icon.Size * icon.GetGlobalTransform().Scale.Abs();
        return local.X > 1f && local.Y > 1f ? new Rect2(icon.GlobalPosition, local) : rect;
    }

    /// <summary>The icon art's drawn rectangle: its own entry tween moves it vertically by design, so it is watched apart.</summary>
    private static Rect2 StatusIconArtRect(Control icon)
    {
        Control? art = icon.GetNodeOrNull<Control>("%Icon") ?? icon.GetNodeOrNull<Control>("Icon");
        return art is not null && GodotObject.IsInstanceValid(art) ? art.GetGlobalRect() : StatusIconRect(icon);
    }

    /// <summary>Finds an icon's scale-in tween by name, cached per node type so no game type is named at compile time.</summary>
    private static bool StatusIconAnimating(Control icon)
    {
        Type type = icon.GetType();
        if (!StatusPowerTweenFields.TryGetValue(type, out FieldInfo? field))
        {
            field = type.GetField("_animInTween", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            StatusPowerTweenFields[type] = field;
        }
        return TweenRunning(icon, field);
    }

    /// <summary>
    /// Watches every drawn frame (<see cref="RenderingServer.FramePostDraw"/>) from just before a power is applied until
    /// the layout settles, and fails an icon that is ever drawn more than <see cref="StatusJumpTolerance"/> units from its
    /// settled position. The icon's own art rises in from 24 display units above (its native entry tween), so the control's
    /// layout rect is what must not jump; the art's horizontal position must not jump either, while its vertical rise is
    /// reported and not failed. The first drawn frame that contains the new icon is read back for the PNG: saved for the
    /// first add of each subject and for every failing add, disposed otherwise.
    /// </summary>
    private sealed class StatusJumpProbe
    {
        private readonly ScreenshotService _screenshots;
        private readonly TestReport _report;
        private readonly NCombatRoom _room;
        private readonly string _name;
        private readonly HashSet<ulong> _existing = new();
        private readonly Dictionary<ulong, List<(long Frame, Rect2 Control, Rect2 Art)>> _samples = new();
        private readonly long _startFrame;
        private bool _running;
        private bool _captured;
        private Image? _firstFrame;
        private string? _captureError;
        private long _firstFrameAt = -1;

        public StatusJumpProbe(ScreenshotService screenshots, TestReport report, NCombatRoom room, string subject,
            int add)
        {
            _screenshots = screenshots;
            _report = report;
            _room = room;
            _name = $"status_add_{subject}_{add:00}_first_frame";
            foreach (NCreature creature in Creatures())
            foreach (Control icon in StatusIcons(creature))
                _existing.Add(icon.GetInstanceId());
            _startFrame = (long)Engine.GetProcessFrames();
        }

        public void Begin()
        {
            _running = true;
            RenderingServer.FramePostDraw += OnFramePostDraw;
        }

        private void Cancel()
        {
            if (!_running) return;
            RenderingServer.FramePostDraw -= OnFramePostDraw;
            _running = false;
        }

        private IEnumerable<NCreature> Creatures() => _room.CreatureNodes
            .Where(c => GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion() && c.Entity.IsAlive);

        private void OnFramePostDraw()
        {
            if (!_running) return;
            long frame = (long)Engine.GetProcessFrames() - _startFrame;
            bool newIcon = false;
            foreach (NCreature creature in Creatures())
            foreach (Control icon in StatusIcons(creature))
            {
                ulong id = icon.GetInstanceId();
                Rect2 control = StatusIconRect(icon);
                Rect2 art = StatusIconArtRect(icon);
                if (!_existing.Contains(id)) newIcon = true;
                if (!_samples.TryGetValue(id, out List<(long Frame, Rect2 Control, Rect2 Art)>? list))
                {
                    list = new List<(long, Rect2, Rect2)>();
                    _samples[id] = list;
                }
                // Collapse consecutive identical samples; the first sample of every icon is always kept, so a one-frame
                // jump can never be collapsed away.
                if (list.Count == 0 || list[^1].Control != control || list[^1].Art != art)
                    list.Add((frame, control, art));
            }
            if (!newIcon || _captured) return;
            _captured = true;
            _firstFrameAt = frame;
            try
            {
                _firstFrame = _screenshots.ReadRenderedFrame(_name);
            }
            catch (Exception error)
            {
                _captureError = error.Message;
            }
        }

        /// <summary>Stops sampling, compares every drawn sample to the settled rects and saves the first frame if asked.</summary>
        public (bool Ok, string Summary, string Detail) Finish(string step, bool saveAlways)
        {
            Cancel();
            var settled = new Dictionary<ulong, (Rect2 Control, Rect2 Art)>();
            foreach (NCreature creature in Creatures())
            foreach (Control icon in StatusIcons(creature))
                settled[icon.GetInstanceId()] = (StatusIconRect(icon), StatusIconArtRect(icon));

            float worstControl = 0f;
            float worstArt = 0f;
            string worstDetail = "none";
            bool failed = false;
            long worstFrame = 0;
            int sampledIcons = 0;
            long sampledFrames = 0;
            foreach ((ulong id, List<(long Frame, Rect2 Control, Rect2 Art)> samples) in _samples)
            {
                if (!settled.TryGetValue(id, out (Rect2 Control, Rect2 Art) final))
                {
                    failed = true;
                    worstDetail = $"icon {id} drew {samples.Count} sample(s) but is gone at the settle";
                    continue;
                }
                sampledIcons++;
                sampledFrames = Math.Max(sampledFrames, samples[^1].Frame + 1);
                bool isNew = !_existing.Contains(id);
                foreach ((long frame, Rect2 control, Rect2 art) in samples)
                {
                    float controlDelta = control.Position.DistanceTo(final.Control.Position);
                    float artDelta = MathF.Abs(art.Position.X - final.Art.Position.X);
                    if (controlDelta > worstControl)
                    {
                        worstControl = controlDelta;
                        worstFrame = frame;
                        worstDetail = $"icon {id} ({(isNew ? "new" : "existing")}) frame +{frame}: " +
                                      $"drawn={Q(control)} settled={Q(final.Control)} delta={Q(controlDelta, 1)}u";
                    }
                    if (artDelta > worstArt) worstArt = artDelta;
                    if (controlDelta <= StatusJumpTolerance && artDelta <= StatusJumpTolerance) continue;
                    failed = true;
                    if (worstDetail == "none" || controlDelta > StatusJumpTolerance)
                        worstDetail = $"icon {id} ({(isNew ? "new" : "existing")}) frame +{frame}: " +
                                      $"drawn={Q(control)} settled={Q(final.Control)} " +
                                      $"delta={Q(controlDelta, 1)}u artDelta={Q(artDelta, 1)}u";
                }
            }
            if (_samples.Count == 0)
            {
                failed = true;
                worstDetail = "no drawn frame sampled the room";
            }

            if ((saveAlways || failed) && _firstFrame is not null)
            {
                try
                {
                    string path = _screenshots.SaveFrame(_name, _firstFrame);
                    _report.Screenshot(_name, path);
                }
                catch (Exception error)
                {
                    _report.Fail(step + "_first_frame_capture", error.Message);
                }
                finally
                {
                    _firstFrame.Dispose();
                    _firstFrame = null;
                }
            }
            else if ((saveAlways || failed) && _firstFrame is null)
            {
                _report.Fail(step + "_first_frame_capture",
                    _captureError ?? "no drawn frame containing the new icon was seen");
            }
            else
            {
                _firstFrame?.Dispose();
                _firstFrame = null;
            }

            string summary = $"{Q(worstControl, 1)}u@+{worstFrame}" + (failed ? " JUMP" : "");
            string detail = failed
                ? worstDetail
                : $"{sampledIcons} icon(s) over {sampledFrames} drawn frame(s) stayed within " +
                  $"{StatusJumpTolerance:F0} units of their settled position (worst {Q(worstControl, 1)}u, " +
                  $"art horizontal {Q(worstArt, 1)}u, first frame +{_firstFrameAt})";
            return (!failed, summary, detail);
        }
    }

    /// <summary>One row of the icon grid: icons sharing a Y band, left to right.</summary>
    private sealed class StatusRow
    {
        public float Y;
        public List<Rect2> Rects { get; } = new();
        public int Count => Rects.Count;
        public float Left => Rects.Min(rect => rect.Position.X);
        public float Right => Rects.Max(rect => rect.End.X);
    }

    /// <summary>Rows of icons top to bottom; icons are a square grid, so half an icon separates rows.</summary>
    private static List<StatusRow> StatusIconRowList(IReadOnlyList<Rect2> rects)
    {
        var rows = new List<StatusRow>();
        foreach (Rect2 rect in rects.OrderBy(r => r.Position.Y).ThenBy(r => r.Position.X))
        {
            StatusRow? row = rows.Count == 0 ? null : rows[^1];
            float tolerance = Math.Max(2f, rect.Size.Y * 0.5f);
            if (row is null || MathF.Abs(rect.Position.Y - row.Y) > tolerance)
            {
                row = new StatusRow { Y = rect.Position.Y };
                rows.Add(row);
            }
            row.Rects.Add(rect);
        }
        return rows;
    }

    /// <summary>The icon advance: the median horizontal step inside a row, falling back to the icon width.</summary>
    private static float StatusIconPitch(IReadOnlyList<StatusRow> rows, IReadOnlyList<Rect2> rects)
    {
        var steps = new List<float>();
        foreach (StatusRow row in rows)
        {
            Rect2[] sorted = row.Rects.OrderBy(rect => rect.Position.X).ToArray();
            for (int i = 1; i < sorted.Length; i++) steps.Add(sorted[i].Position.X - sorted[i - 1].Position.X);
        }
        if (steps.Count > 0)
        {
            steps.Sort();
            return steps[steps.Count / 2];
        }
        return rects.Count > 0 ? rects.Average(rect => rect.Size.X) : StatusIconTargetWidth;
    }

    private static List<(NCreature Creature, List<Rect2> Rects)> StatusBlocks(NCombatRoom room) => room.CreatureNodes
        .Where(c => GodotObject.IsInstanceValid(c) && !c.IsQueuedForDeletion() && c.Entity.IsAlive)
        .Select(c => (Creature: c, Rects: StatusIcons(c).Select(StatusIconRect).ToList()))
        .Where(entry => entry.Rects.Count > 0)
        .ToList();

    private static string StatusBlockName(NCreature creature) =>
        creature.Entity.Monster?.GetType().Name ?? "player";

    private static Rect2 StatusUnion(IReadOnlyList<Rect2> rects)
    {
        if (rects.Count == 0) return new Rect2();
        Rect2 union = rects[0];
        for (int i = 1; i < rects.Count; i++) union = union.Merge(rects[i]);
        return union;
    }

    /// <summary>
    /// The block a creature's status icons would take before any neighbour limit: normal-width icons, columns from the
    /// container the hit box defines (<c>max(ceil(n/2), ceil(container / normal))</c>), centred on that container when
    /// wider and kept on the canvas. The launcher plans its neighbour gaps from these untouched blocks; the drawn blocks
    /// are already shrunk by those gaps, so a checker that measured them saw room the plan never had. The drawn block's
    /// vertical band is reused so the same row test applies.
    /// </summary>
    private static Rect2 StatusBaseBlock(NCreature creature, int count, Rect2 drawnBlock, Rect2 canvas)
    {
        if (count <= 0) return new Rect2();
        Rect2 body = creature.Hitbox.GetGlobalRect();
        float unit = StatusDisplay(creature)?.GetGlobalTransform().Scale.X ?? 0f;
        float container = MathF.Max(1f, body.Size.X) + StatusContainerExtraWidth * MathF.Max(unit, 0.01f);
        // The launcher's normal icon is the canvas share (82/1680), not this checker's rounded 52.6 constant: the base
        // block's edges must land where the plan's do, or a canvas clamp on a neighbour reads a unit short.
        float normal = canvas.Size.X * (82f / 1680f);
        int columns = Math.Max(1, Math.Max((int)MathF.Ceiling(count / 2f),
            (int)MathF.Ceiling(container / normal)));
        float width = Math.Min(columns, count) * normal;
        float overhang = MathF.Max(0f, (width - container) * 0.5f);
        float innerLeft = canvas.Position.X + StatusCanvasMargin;
        float innerRight = canvas.End.X - StatusCanvasMargin;
        float left = width <= MathF.Max(0f, innerRight - innerLeft)
            ? Math.Clamp(body.Position.X - overhang, innerLeft, innerRight - width)
            : innerLeft;
        return new Rect2(left, drawnBlock.Position.Y, width, drawnBlock.Size.Y);
    }

    /// <summary>
    /// How far the status block may overhang its container on each side before the icons shrink: half the gap between the
    /// two untouched base blocks, never more than 50 units (50 when there is no neighbour). A NEGATIVE gap is the rule's
    /// hard edge — the blocks already overlap, so the block steps off the neighbour instead of shrinking — and only the
    /// 50-unit ceiling applies to that side. Only blocks that share the block's vertical band count: two blocks at
    /// different heights cannot collide, so a creature behind or above imposes no sideways limit.
    /// </summary>
    private static (float Left, float Right) StatusNeighbourLimits(Rect2 selfBase, Rect2 block, NCombatRoom room,
        NCreature self, Rect2 canvas)
    {
        float leftGap = float.PositiveInfinity;
        float rightGap = float.PositiveInfinity;
        foreach ((NCreature creature, List<Rect2> icons) in StatusBlocks(room))
        {
            if (creature == self) continue;
            Rect2 otherDrawn = StatusUnion(icons);
            if (otherDrawn.End.Y <= block.Position.Y || block.End.Y <= otherDrawn.Position.Y) continue;
            Rect2 otherBase = StatusBaseBlock(creature, icons.Count, otherDrawn, canvas);
            if (otherBase.Position.X + otherBase.Size.X * 0.5f >= selfBase.Position.X + selfBase.Size.X * 0.5f)
                rightGap = MathF.Min(rightGap, otherBase.Position.X - selfBase.End.X);
            else
                leftGap = MathF.Min(leftGap, selfBase.Position.X - otherBase.End.X);
        }
        return (Limit(leftGap), Limit(rightGap));
        static float Limit(float gap) => float.IsInfinity(gap) || gap < 0f
            ? StatusBlockOverhangLimit
            : MathF.Min(StatusBlockOverhangLimit, gap * 0.5f);
    }

    private static bool StatusOverlaps(Rect2 a, Rect2 b, float tolerance) =>
        a.Position.X + tolerance < b.End.X && b.Position.X + tolerance < a.End.X
        && a.Position.Y + tolerance < b.End.Y && b.Position.Y + tolerance < a.End.Y;

    /// <summary>
    /// The hand/rail band for the current icons, reported as information (never a failure) while the 9:16 space question is
    /// open (LauncherStageChecks.cs, which owns <c>PortraitCombatMath</c>).
    /// </summary>
    partial void CheckStatusBands(string step, Rect2 canvas, IReadOnlyList<Rect2> iconRects, List<string> lines);
}
