using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using STS2Mobile.Platform;
using CombatReflection = STS2Mobile.GameHost.PortraitCombatLayout.CombatReflection;

namespace STS2Mobile.GameHost;

/// <summary>Owns the encounter formation, background, body placement, and world-effect anchors.</summary>
internal sealed class PortraitCombatStage
{
    private const float IntentBandHeight = 64f * PortraitCreatureHud.IntentCanvasScale + 12f;
    // Measured from the compact Ovicopter fight: the egg and flyer intents have different top clearance.
    private const float EnemyHudBoxHeight = 41.23f;
    private const float EggIntentRise = 54.4f;
    private const float OviIntentRise = 64f;
    private const float LoneEnemyLeft = 360f;
    private const float SurroundedPlayerHalf = 160f;
    private const float SurroundedSideGap = 100f;
    internal const float MinHealthBarWidth = 150f;
    internal const float StageSideMargin = 8f;

    private readonly List<Control> _stagePlayers = new();
    private readonly List<Control> _stagePets = new();
    private readonly List<Control> _stageEnemies = new();
    private readonly List<Control> _stageLeft = new();
    private readonly List<Control> _stageAllies = new();
    private PortraitCreatureSlot[] _stageAllySlots = [];
    private readonly Dictionary<ulong, (Vector2 Position, Vector2 Scale)> _bossArmOriginals = new();
    private bool _crabArmFitFrozen;

    private const string KaiserCrabBackgroundTypeName = "MegaCrit.Sts2.Core.Nodes.Vfx.Backgrounds.NKaiserCrabBossBackground";
    private const string BackdropNodeName = "PortraitCombatBackdrop";
    private const string ForegroundVisibleMeta = "portrait_foreground_visible";
    // The Underdocks deck's plank line sits 320 units from the container origin in the authored 1296-tall layer art, i.e.
    // 861 units above the art's bottom. Measured from the art so any Underdocks layer background keeps its own plank line.
    private const float PlankFromBottomFraction = 861f / 1296f;
    // The act's own pier art. An encounter may bring its own background scene that is that same pier (the Lagavulin
    // Matriarch and the Soul Fysh do), and it is the art, not the encounter's custom-background flag, that the plank line
    // belongs to.
    private const string UnderdocksPierArtPath = "res://images/rooms/underdocks/underdocks_00.png";
    // The Architect's ending is drawn as a combat room, but its background is the ending picture, not a fight arena: it
    // has no floor line, so it is framed as a cover of the whole canvas instead of ending at the hand row.
    private const string ArchitectEndingBackgroundScenePath =
        "res://scenes/backgrounds/the_architect_event_encounter/the_architect_event_encounter_background.tscn";
    private const string DecimillipedeSegmentTypeName = "DecimillipedeSegment";
    private const string CrusherMonsterTypeName = "Crusher";
    private const string RocketMonsterTypeName = "Rocket";

    // Every native effect that spawned into the fight's VFX container, so each is placed on its body once.
    private readonly HashSet<ulong> _alignedVfx = new();
    private int _vfxPasses;
    // The VFX containers already listening for new effects, and whether one arrived since the last placement pass.
    private readonly HashSet<ulong> _wiredVfxContainers = new();
    private readonly List<Control> _vfxCreatures = new();
    private bool _vfxDirty = true;

    private readonly Dictionary<ulong, CombatRoomState> _rooms = new();
    private readonly Dictionary<ulong, CreaturePlacement> _creatures = new();
    private readonly Action<Control, Control?, CombatReflection> _captureHud;
    private readonly Action<Control, float, bool> _queueLabel;

    public PortraitCombatStage(Action<Control, Control?, CombatReflection> captureHud, Action<Control, float, bool> queueLabel)
    {
        _captureHud = captureHud;
        _queueLabel = queueLabel;
    }

    public bool HasOrbs { get; private set; }
    public ReadOnlySpan<Control> Enemies => CollectionsMarshal.AsSpan(_stageEnemies);

    public Control? HitboxOf(Control creature) =>
        _creatures.TryGetValue(creature.GetInstanceId(), out var placement) ? placement.Hitbox : null;

    public void Attach() => OstyPositionGate.OwnsPosition = OwnsCreaturePosition;
    public void Detach() => OstyPositionGate.OwnsPosition = null;

    public void CaptureRoom(Control room) => _rooms.Add(room.GetInstanceId(), CombatRoomState.Capture(room));

    public void RestoreRoom(Control room, CombatReflection refl) => _rooms[room.GetInstanceId()].Restore(room, refl);

    public PortraitCreatureSlot? TallSlot(Control room, Control creature)
    {
        if (_rooms[room.GetInstanceId()].Stage is { } plan &&
            plan.SlotByCreature.TryGetValue(creature.GetInstanceId(), out int index) && index < plan.TallFormation.Length)
            return plan.TallFormation[index];
        return null;
    }

    public void RestoreCreatureRoot(Control creature)
    {
        CreaturePlacement state = _creatures[creature.GetInstanceId()];
        state.RestoreRoot(creature);
    }

    public void RestoreCreatureBounds(Control creature, CombatReflection refl)
    {
        RemoveMeta(creature, "portrait_feet_y");
        refl.InvokeUpdateBounds(creature);
    }

    public void EndSession()
    {
        _rooms.Clear();
        _creatures.Clear();
    }

    public void PruneDead(List<ulong> dead)
    {
        foreach (ulong id in _rooms.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _rooms.Remove(id);
        dead.Clear();
        foreach (ulong id in _creatures.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _creatures.Remove(id);

        dead.Clear();
        foreach (ulong id in _wiredVfxContainers)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _wiredVfxContainers.Remove(id);

        dead.Clear();
        foreach (ulong id in _bossArmOriginals.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _bossArmOriginals.Remove(id);
    }

    private bool OwnsCreaturePosition(object node) =>
        node is Control creature && _creatures.TryGetValue(creature.GetInstanceId(), out var state) &&
        state.PlacementApplied;

    public void Apply(
        Control room,
        CombatReflection refl,
        PortraitCombatBands bands,
        float canvasHeight)
    {
        Control? sceneContainer = room.GetNodeOrNull<Control>("%CombatSceneContainer") ?? room.GetNodeOrNull<Control>("CombatSceneContainer");
        if (sceneContainer == null)
            return;

        sceneContainer.Scale = Vector2.One;
        sceneContainer.PivotOffset = sceneContainer.Size * 0.5f;

        Control? allyContainer = sceneContainer.GetNodeOrNull<Control>("%AllyContainer") ?? sceneContainer.GetNodeOrNull<Control>("AllyContainer");
        Control? enemyContainer = sceneContainer.GetNodeOrNull<Control>("%EnemyContainer") ?? sceneContainer.GetNodeOrNull<Control>("EnemyContainer");
        Vector2 centerOrigin = new(540f, canvasHeight * 0.5f);

        if (allyContainer != null)
        {
            allyContainer.Scale = Vector2.One;
            allyContainer.Position = centerOrigin;
        }
        if (enemyContainer != null)
        {
            enemyContainer.Scale = Vector2.One;
            enemyContainer.Position = centerOrigin;
        }

        // Partition active creature nodes into players, pets, and enemies. The lists are refilled: the pass runs every frame.
        List<Control> players = _stagePlayers;
        List<Control> pets = _stagePets;
        List<Control> currentEnemies = _stageEnemies;
        players.Clear();
        pets.Clear();
        currentEnemies.Clear();
        bool hasOrbs = false;

        if (refl.RoomCreatureNodesField?.GetValue(room) is IEnumerable creatureList)
        {
            foreach (object? item in creatureList)
            {
                if (item is not Control creature || !GodotObject.IsInstanceValid(creature) || creature.IsQueuedForDeletion())
                    continue;

                CaptureCreatureIfNeeded(creature, refl);
                object? entity = refl.CreatureEntityProp?.GetValue(creature);
                if (entity == null) continue;

                bool isPlayerSide = refl.IsPlayerSide(entity);
                if (isPlayerSide)
                {
                    bool isPlayer = (bool)(refl.EntityIsPlayerProp?.GetValue(entity) ?? false);
                    if (isPlayer)
                    {
                        players.Add(creature);
                        if (refl.CreatureHasOrbs(creature))
                            hasOrbs = true;
                    }
                    else
                    {
                        pets.Add(creature);
                    }
                }
                else
                {
                    currentEnemies.Add(creature);
                }
            }
        }

        // The pre-draw orb refit runs after this pass, so it reads the presence from here instead of re-walking every
        // player's orb queue chain again a frame later.
        HasOrbs = hasOrbs;
        CombatStagePlan plan = EnsureStagePlan(room, _rooms[room.GetInstanceId()], refl, bands, canvasHeight, players, pets, currentEnemies, hasOrbs);
        // The room's body scale is frozen with the roster it opened with: a summon appearing later must fit into that
        // scale (and its own cell), never rescale the hero and the units already on screen.
        if (plan.BodyScaleFrozen)
            plan.SharedBodyScale = plan.FrozenBodyScale;
        PortraitCreatureSlot[] allySlots = _stageAllySlots;
        if (allySlots.Length != plan.AllySlots.Length)
            _stageAllySlots = allySlots = new PortraitCreatureSlot[plan.AllySlots.Length];
        Array.Copy(plan.AllySlots, allySlots, plan.AllySlots.Length);

        // The game stands some enemies on the player's left (the Kaiser Crab's Crusher, with the
        // player in the middle and the Rocket on the right; its Surrounded power makes the side the player faces matter).
        // Those keep their side: the player moves to the middle and the enemies the game has on the left stand left of it.
        List<Control> leftEnemies = _stageLeft;
        leftEnemies.Clear();
        if (players.Count > 0 && _creatures.TryGetValue(players[0].GetInstanceId(), out CreaturePlacement? playerDesktop))
        {
            foreach (Control enemy in currentEnemies)
            {
                if (_creatures.TryGetValue(enemy.GetInstanceId(), out CreaturePlacement? enemyDesktop) &&
                    enemyDesktop.DesktopX < playerDesktop.DesktopX - SurroundedSideGap)
                    leftEnemies.Add(enemy);
            }
        }
        bool liveSurrounded = leftEnemies.Count > 0;
        if (plan.SurroundedFrozen is null)
        {
            plan.SurroundedFrozen = liveSurrounded;
            plan.PlayerShift = 0f;
            if (liveSurrounded)
            {
                if (players.Count > 0 && allySlots.Length > 0)
                {
                    (float plo, float phi) = HitboxLocalExtent(players[0], refl);
                    plan.PlayerShift = SafeAreaCanvas.PortraitWidth * 0.5f -
                        (allySlots[0].CanvasX + (plo + phi) * 0.5f * allySlots[0].BaseScale);
                }
                foreach (Control enemy in leftEnemies)
                    plan.CrabLeftIds.Add(enemy.GetInstanceId());
            }
        }
        bool surrounded = plan.SurroundedFrozen == true;
        if (surrounded)
        {
            // The sides are frozen with the room: when a claw dies or a summon arrives the player stays between the claws
            // and the survivors keep their slots.
            leftEnemies.Clear();
            foreach (Control enemy in currentEnemies)
            {
                if (plan.CrabLeftIds.Contains(enemy.GetInstanceId()))
                    leftEnemies.Add(enemy);
            }
            if (plan.PlayerShift != 0f)
            {
                for (int i = 0; i < allySlots.Length; i++)
                    allySlots[i] = allySlots[i] with { CanvasX = allySlots[i].CanvasX + plan.PlayerShift };
            }
        }

        List<Control> orderedAllies = _stageAllies;
        orderedAllies.Clear();
        orderedAllies.AddRange(players);
        orderedAllies.AddRange(pets);

        bool tallRow = plan.TallFormation.Length > 0;
        float maxAllyW = orderedAllies.Count > 1 ? 210f : (tallRow ? 360f : 270f);
        float maxAllyH = orderedAllies.Count > 1 ? 260f : (tallRow ? 560f : 360f);
        // One room scale: the hero's own frame caps the world scale for the whole room, so
        // the hero and the monsters are never drawn at two different sizes. Frozen with the roster.
        if (!plan.HeroScaleComputed && players.Count > 0)
        {
            var heroFits = new PortraitCombatMath.PortraitFrameFit[players.Count];
            for (int i = 0; i < players.Count; i++)
            {
                Vector2 native = refl.GetCreatureFootprintSize(players[i]);
                heroFits[i] = new PortraitCombatMath.PortraitFrameFit(native.X, native.Y, maxAllyW, maxAllyH, 1f);
            }
            plan.HeroRoomScale = PortraitCombatMath.SharedRoomScale(heroFits, out _);
            plan.HeroScaleComputed = true;
        }
        plan.SharedBodyScale = plan.HeroRoomScale > 0f
            ? (plan.SharedBodyScale > 0f ? Math.Min(plan.SharedBodyScale, plan.HeroRoomScale) : plan.HeroRoomScale)
            : plan.SharedBodyScale;
        // The widest health bar a creature may have, so the bars of neighbours in one row stay apart.
        float maxAllyBar = orderedAllies.Count > 1 ? 170f : 320f;
        for (int i = 0; i < orderedAllies.Count && i < allySlots.Length; i++)
        {
            float allyForce = 0f;
            if (plan.SharedBodyScale > 0f)
            {
                // Allies have no intent band above them: fit the body to its own frame, then take the room's shared scale.
                Vector2 allyNative = refl.GetCreatureFootprintSize(orderedAllies[i]);
                float allyFit = PortraitCombatMath.MaxBodyScale;
                if (allyNative.X > 1f) allyFit = Math.Min(allyFit, maxAllyW / allyNative.X);
                if (allyNative.Y > 1f) allyFit = Math.Min(allyFit, maxAllyH / allyNative.Y);
                // The room's scale, capped by the ally's own frame; the legibility floor wins over a room of tiny cells
                // (the oversized body caps itself, never the hero's readability).
                allyForce = Math.Max(PortraitCombatMath.PeerScaleFloor,
                    Math.Min(plan.SharedBodyScale, Math.Max(PortraitCombatMath.MinBodyScale, allyFit)));
            }
            ApplyCreatureSlot(orderedAllies[i], refl, allySlots[i], centerOrigin, maxAllyW, maxAllyH, maxAllyBar, bands,
                isAlly: true, peerScale: plan.PeerScale, segmented: plan.Segmented,
                minLeft: StageSideMargin, maxRight: SafeAreaCanvas.PortraitWidth - StageSideMargin,
                forceScale: allyForce);
        }
        // A pet the fight did not start with stands beside its owner rather than moving the player to make room.
        for (int i = allySlots.Length; i < orderedAllies.Count; i++)
        {
            float ownerX = allySlots.Length > 0 ? allySlots[0].CanvasX : PortraitCombatMath.LonePlayerX;
            float x = Math.Min(SafeAreaCanvas.PortraitWidth - 70f, ownerX + 170f + 70f * (i - allySlots.Length));
            var petSlot = new PortraitCreatureSlot(x, bands.FrontFeetY, 0.70f, 1);
            ApplyCreatureSlot(orderedAllies[i], refl, petSlot, centerOrigin, 170f, 220f, 150f, bands,
                isAlly: true, peerScale: plan.PeerScale, segmented: plan.Segmented);
        }

        if (surrounded)
        {
            LayoutSurroundingEnemies(refl, sceneContainer, leftEnemies, currentEnemies, centerOrigin, plan, bands);
            AlignNativeVfx(room, refl, orderedAllies, currentEnemies);
            return;
        }

        // The enemy stood much closer to the player than on a desktop, so the enemies' hit boxes start a fifth
        // of the width right of the player's (half of it from pets and other players), measured from where the allies
        // stand now.
        float playersRight = 0f, alliesRight = 0f;
        foreach (Control ally in orderedAllies)
        {
            if (HitboxCanvasRight(ally) is not float right) continue;
            alliesRight = Math.Max(alliesRight, right);
            if (players.Contains(ally)) playersRight = Math.Max(playersRight, right);
        }
        float gap = plan.Capacity <= 2 ? 110f : PortraitCombatMath.PlayerEnemyGap;
        float enemyLeft = PortraitCombatMath.EnemyStageLeft(playersRight, alliesRight, LoneEnemyLeft, gap);

        if (!bands.Compact && plan.Capacity == 6 && plan.TallFormation.Length == 0 && !plan.OviFight &&
            !plan.EmbeddedCrab && !plan.Segmented)
        {
            // Six bodies on the tall stage: three columns right of the hero (a smaller gap, since the group is dense), a
            // front and a back rat per column. Built once with the real enemy-left, then frozen like every other slot.
            float sixLeft = PortraitCombatMath.EnemyStageLeft(playersRight, alliesRight, LoneEnemyLeft, 110f);
            plan.TallFormation = PortraitCombatMath.ComputeSixFormation(sixLeft, bands);
            plan.CrowdEnabled = false;
            float tightest = 0f;
            foreach (Control enemy in currentEnemies)
            {
                if (!plan.SlotByCreature.TryGetValue(enemy.GetInstanceId(), out int slotIndex) || slotIndex >= plan.TallFormation.Length)
                    continue;
                (float frameW, float frameH, _, _) = PortraitCombatMath.TallFormationFrame(plan.TallFormation, slotIndex, bands);
                float fit = CellFit(enemy, frameW, frameH, refl, bands);
                tightest = tightest <= 0f ? fit : Math.Min(tightest, fit);
            }
            if (tightest > 0f)
            {
                if (!plan.BodyScaleFrozen)
                    plan.SharedBodyScale = HeroCapped(plan, Math.Clamp(Math.Min(plan.BodyTargetScale, tightest),
                        PortraitCombatMath.MinBodyScale, PortraitCombatMath.MaxBodyScale));
                plan.SharedLocked = true;
            }
        }
        if (!plan.SqueezeComputed)
        {
            plan.Squeeze = ComputeGroupSqueeze(plan, refl, bands, currentEnemies, enemyLeft);
            plan.SqueezeComputed = true;
        }
        if (plan.Segmented && !plan.ChainComputed)
        {
            // The chain is rigid: it starts just right of the hero (not a whole player gap away) and its parts are fitted to
            // their own cells across the free width, with up to the stage height, so a reattached part keeps its place.
            float left = Math.Max(StageSideMargin + 6f, playersRight + 6f);
            float available = Math.Max(240f, 1066f - left);
            plan.ChainLeft = left;
            plan.ChainLane = available / Math.Max(1, plan.Capacity);
            plan.ChainMaxW = Math.Max(80f, plan.ChainLane - 10f);
            plan.ChainComputed = true;
        }
        if (plan.CrowdEnabled && !plan.CrowdComputed)
        {
            // The short canvas cannot stack ranks without the back rank's bar landing on the front rank. One row of cells
            // right of the hero instead; the boss's cell is wider, so it stays dominant and everything keeps its own space.
            plan.CrowdLeft = Math.Max(StageSideMargin + 6f, playersRight + 6f);
            plan.CrowdBoss = plan.Capacity >= 4 ? plan.BossSlot : -1;
            plan.BackBoss = !bands.Compact && !plan.OviFight && plan.Capacity >= 5 && plan.CrowdBoss >= 0;
            int peers = plan.BackBoss ? plan.Capacity - 1 : plan.Capacity;
            plan.Crowd = PortraitCombatMath.ComputeCrowdedCells(peers, plan.BackBoss ? -1 : plan.CrowdBoss, plan.CrowdLeft, bands);
            plan.CrowdMap = new int[plan.Capacity];
            int next = 0;
            for (int i = 0; i < plan.Capacity; i++)
                plan.CrowdMap[i] = plan.BackBoss && i == plan.CrowdBoss ? -1 : next++;
            if (plan.OviFight)
            {
                // The eggs own every cell right of the hero from room start: six uniform cells over the full enemy width
                // (the Ovi takes the last on 9:16, or its upper-back slot when tall). Cells are frozen at full capacity,
                // so a later egg or hatchling never lands over the player and nothing moves.
                plan.CrowdLeft = Math.Max(StageSideMargin + 6f, playersRight + 110f);
                plan.Crowd = PortraitCombatMath.ComputeCrowdedCells(plan.Capacity, -1, plan.CrowdLeft, bands);
                for (int i = 0; i < plan.Capacity; i++)
                    plan.CrowdMap[i] = i;
                plan.BackBoss = false;
            }
            if (plan.BackBoss)
            {
                // Reserve the boss's rank above the tallest front body the cells can hold (a ToughEgg is 277 by 300), its
                // intents and the boss's own bar. If that leaves no legible boss, keep the single row instead.
                float cellWidth = plan.Crowd.Widths.Length > 0 ? plan.Crowd.Widths[0] : 130f;
                float frontHeight = Math.Min(cellWidth / 277f * 300f, bands.FrontFeetY - bands.StageTopY);
                plan.BossBackFeet = bands.FrontFeetY - frontHeight - IntentBandHeight - 52f;
                float bossHeight = plan.BossBackFeet - bands.StageTopY - IntentBandHeight;
                if (bossHeight < 180f)
                {
                    plan.BackBoss = false;
                    plan.Crowd = PortraitCombatMath.ComputeCrowdedCells(plan.Capacity, plan.BossSlot, plan.CrowdLeft, bands);
                    for (int i = 0; i < plan.Capacity; i++)
                        plan.CrowdMap[i] = i;
                }
            }
            plan.CrowdComputed = true;

            // One shared body scale for the encounter (plus the authored-size floor), then the encounter's own body
            // multiplier. It is still capped by the tightest cell so bodies cannot leave their cells; the boss keeps its own
            // frame (and the Ovicopter its back rank). Every peer is later drawn at <c>min(shared, its own cell fit)</c>.
            float target = plan.BodyTargetScale > 0f
                ? plan.BodyTargetScale
                : PortraitCombatMath.MaxBodyScale;
            float tightestFit = 0f;
            for (int i = 0; i < plan.Capacity && i < plan.EnemySlots.Length; i++)
            {
                if (i == plan.BossSlot)
                    continue;
                float width, height;
                if (plan.Staggered && i < plan.StaggerWidths.Length)
                {
                    width = plan.StaggerWidths[i];
                    height = plan.StaggerHeights[i];
                }
                else if (plan.Segmented && plan.ChainComputed)
                {
                    width = plan.ChainMaxW;
                    height = 900f;
                }
                else if (i < plan.Crowd.Widths.Length)
                {
                    int cell = plan.CrowdMap.Length > i ? plan.CrowdMap[i] : i;
                    if (cell < 0 || cell >= plan.Crowd.Widths.Length)
                        continue;
                    width = plan.Crowd.Widths[cell];
                    height = plan.Crowd.Heights[cell];
                }
                else
                {
                    continue;
                }
                foreach (Control enemy in currentEnemies)
                {
                    if (!plan.SlotByCreature.TryGetValue(enemy.GetInstanceId(), out int slotIndex) || slotIndex != i)
                        continue;
                    float fit = CellFit(enemy, width, height, refl, bands);
                    tightestFit = tightestFit <= 0f ? fit : Math.Min(tightestFit, fit);
                    break;
                }
            }
            if (!plan.BodyScaleFrozen)
                plan.SharedBodyScale = HeroCapped(plan, tightestFit > 0f
                    ? Math.Clamp(Math.Min(target, tightestFit), PortraitCombatMath.MinBodyScale, PortraitCombatMath.MaxBodyScale)
                    : Math.Clamp(target, PortraitCombatMath.MinBodyScale, PortraitCombatMath.MaxBodyScale));

            if (!plan.SingleRow && !bands.Compact && !plan.BackBoss && plan.Capacity is 4 or 5 && plan.CrowdBoss < 0 &&
                plan.HorizonY > 0f && bands.FrontFeetY - plan.HorizonY >= 420f)
            {
                // Four or five ordinary enemies: two ranks with disjoint vertical bands. The front bodies may then be much
                // taller than a single row allows, and the back rank's bar and intents sit entirely above the front ones, so
                // the columns may overlap in x without any body, bar or intent touching.
                int count = plan.Capacity;
                int frontCount = (count + 1) / 2;
                int backCount = count - frontCount;
                float stageHeight = bands.FrontFeetY - bands.StageTopY;
                float frontHeight = Math.Clamp((stageHeight - 262f) * 0.68f, 220f, 360f);
                float backHeight = Math.Max(80f, stageHeight - 262f - frontHeight);
                float backFeet = bands.FrontFeetY - frontHeight - 157f;
                float left = plan.CrowdLeft;
                float span = Math.Max(200f, 1066f - left);
                float frontWidth = span / frontCount;
                float backWidth = span / (backCount + 0.3f);
                var temp = new List<(PortraitCreatureSlot Slot, float W, float H, float Bar)>(count);
                for (int i = 0; i < count; i++)
                {
                    bool front = i < frontCount;
                    int column = front ? i : i - frontCount;
                    float width = front ? frontWidth : backWidth;
                    // Back columns sit between the front ones, inside the row; sorting by x then gives the authored order.
                    float x = front
                        ? left + (column + 0.5f) * width
                        : left + (column + 0.65f) * width;
                    var slot = new PortraitCreatureSlot(x, front ? bands.FrontFeetY : backFeet,
                        front ? 0.94f : 0.82f, front ? 1 : 0);
                    temp.Add((slot, width - 10f, front ? frontHeight : backHeight,
                        Math.Max(MinHealthBarWidth * 0.6f, width - 16f)));
                }
                temp.Sort((a, b) => a.Slot.CanvasX.CompareTo(b.Slot.CanvasX));
                plan.StaggerSlots = new PortraitCreatureSlot[count];
                plan.StaggerWidths = new float[count];
                plan.StaggerHeights = new float[count];
                plan.StaggerBars = new float[count];
                for (int i = 0; i < count; i++)
                {
                    plan.StaggerSlots[i] = temp[i].Slot;
                    plan.StaggerWidths[i] = temp[i].W;
                    plan.StaggerHeights[i] = temp[i].H;
                    plan.StaggerBars[i] = temp[i].Bar;
                }
                plan.Staggered = true;
            }
        }

        // The shared body scale is only meaningful once the encounter's peers exist (the Ovicopter freezes its plan with
        // only the boss). Lock it on the first frame with peers, as the tightest of their cell fits, so every peer uses one
        // value rather than each one's own (slightly different) fit.
        if (plan.CrowdEnabled && plan.CrowdComputed && !plan.SharedLocked)
        {
            float tight = 0f;
            int peers = 0;
            foreach (Control enemy in currentEnemies)
            {
                if (!plan.SlotByCreature.TryGetValue(enemy.GetInstanceId(), out int slotIndex) || slotIndex >= plan.Capacity)
                    continue;
                if (slotIndex == plan.CrowdBoss)
                    continue;
                int cell = plan.CrowdMap.Length > slotIndex ? plan.CrowdMap[slotIndex] : slotIndex;
                if (cell < 0 || cell >= plan.Crowd.Widths.Length)
                    continue;
                float fit = CrowdCellFit(enemy, plan.Crowd, cell, refl, bands);
                tight = tight <= 0f ? fit : Math.Min(tight, fit);
                peers++;
            }
            if (peers > 0)
            {
                if (!plan.BodyScaleFrozen)
                    plan.SharedBodyScale = HeroCapped(plan, Math.Clamp(Math.Min(plan.BodyTargetScale, tight),
                        PortraitCombatMath.MinBodyScale, PortraitCombatMath.MaxBodyScale));
                plan.SharedLocked = true;
            }
        }

        if (!plan.StageLogged)
        {
            var cellWidths = new List<string>();
            if (plan.Crowd.Widths != null)
            {
                for (int i = 0; i < plan.Crowd.Widths.Length && i < 8; i++)
                    cellWidths.Add(plan.Crowd.Widths[i].ToString("F0"));
            }
            GD.Print($"Portrait stage: capacity={plan.Capacity} dominant={plan.Dominant} crowd={plan.CrowdEnabled}/{plan.CrowdComputed} " +
                     $"reserved={plan.ReservedBoss} row={plan.SingleRow} " +
                     $"backBoss={plan.BackBoss} stagger={plan.Staggered} chain={plan.Segmented} target={plan.BodyTargetScale:F3} " +
                     $"shared={plan.SharedBodyScale:F3} locked={plan.SharedLocked} left={plan.CrowdLeft:F0} " +
                     $"cells=[{string.Join(",", cellWidths)}]");
            plan.StageLogged = true;
        }

        // Freeze the one room scale now that the opening roster has been fitted: later summons fit into it.
        if (!plan.BodyScaleFrozen && plan.SharedBodyScale > 0f && currentEnemies.Count > 0)
        {
            plan.BodyScaleFrozen = true;
            plan.FrozenBodyScale = plan.SharedBodyScale;
        }
        else if (plan.BodyScaleFrozen)
        {
            plan.SharedBodyScale = plan.FrozenBodyScale;
        }

        foreach (Control enemy in currentEnemies)
        {
            if (!plan.SlotByCreature.TryGetValue(enemy.GetInstanceId(), out int slotIndex))
                continue;
            bool overflow = slotIndex >= plan.EnemySlots.Length;
            PortraitCreatureSlot slot = overflow
                ? PortraitCombatMath.OverflowEnemySlot(slotIndex, plan.Capacity, bands)
                : plan.EnemySlots[slotIndex];
            if (!overflow && plan.Squeeze < 1f)
                slot = PortraitCombatMath.SqueezeEnemySlot(slot, plan.Squeeze);

            (float maxW, float maxH, float minLeft, float maxRight) = EnemySlotLimits(plan, slotIndex, enemyLeft);
            float maxBar = EnemyBarLimit(plan, slotIndex);
            float forceScale = 0f;
            // One chain: the Ovicopter's own back slot and the staggered pair rows come before the crowded row they are
            // carved out of, so exactly one rule lays each enemy out and none is written twice in a frame.
            if (!overflow && plan.OviFight && slotIndex == plan.OviSlot && plan.CrowdComputed &&
                slotIndex < plan.CrowdMap.Length)
            {
                // The Ovicopter slot at both shapes: an upper back slot over the right end of the egg row, 0.43W wide and
                // 170 units above the floor at 9:16, 0.55W wide and 257 units above it at 9:19.5. The flyer is drawn as
                // large as the room scale allows once its intent row is kept just under the potion/relic header, never
                // under the header itself.
                float oviFrameW = (bands.Compact ? 0.43f : 0.55f) * SafeAreaCanvas.PortraitWidth;
                float oviLift = bands.Compact ? 170f : 257f;
                float oviFeet = bands.FrontFeetY - oviLift;
                if (bands.Compact)
                {
                    // The egg row stands on the front line with its intent row drawn above it, and the flyer's whole HUD
                    // box (bar plus the HP value under it) hangs under its own feet: at 9:16 that box was drawn inside
                    // those intents. Stand the flyer high enough that the box's bottom clears the row by 4; maxH below
                    // keeps its own intent row under the header floor, and the frame it leaves decides its scale.
                    float eggIntentTop = bands.FrontFeetY - PortraitBands.MinTarget - EggIntentRise;
                    float hudDrop = PortraitCreatureHud.BarBandClearance + EnemyHudBoxHeight;
                    oviFeet = Math.Min(oviFeet, eggIntentTop - 4f - hudDrop);
                }
                float oviX = (bands.Compact ? 0.76f : 0.70f) * SafeAreaCanvas.PortraitWidth;
                slot = new PortraitCreatureSlot(oviX, oviFeet, 0.94f, 0);
                float hudFloor = PortraitHudFloorY(bands);
                maxW = oviFrameW;
                maxH = Math.Max(180f, oviFeet - (hudFloor + (bands.Compact ? OviIntentRise : IntentBandHeight)));
                maxBar = Math.Min(oviFrameW, bands.Compact ? 360f : 480f);
                minLeft = Math.Max(StageSideMargin, oviX - maxW * 0.5f);
                maxRight = Math.Min(SafeAreaCanvas.PortraitWidth - 8f, oviX + maxW * 0.5f);
                Vector2 oviNative = refl.GetCreatureFootprintSize(enemy);
                float fitW = oviNative.X > 1f ? maxW / oviNative.X : PortraitCombatMath.MaxBodyScale;
                float fitH = oviNative.Y > 1f ? maxH / oviNative.Y : PortraitCombatMath.MaxBodyScale;
                float roomScale = plan.BodyScaleFrozen ? plan.FrozenBodyScale : plan.BodyTargetScale;
                forceScale = Math.Min(roomScale, Math.Min(fitW, fitH));
            }
            else if (!overflow && plan.Staggered && slotIndex < plan.Capacity)
            {
                slot = plan.StaggerSlots[slotIndex];
                maxW = plan.StaggerWidths[slotIndex];
                maxH = plan.StaggerHeights[slotIndex];
                maxBar = plan.StaggerBars[slotIndex];
                float fit = CellFit(enemy, maxW, maxH, refl, bands);
                float shared = plan.SharedBodyScale > 0f ? plan.SharedBodyScale : plan.BodyTargetScale;
                forceScale = Math.Min(shared, fit);
                minLeft = plan.CrowdLeft;
                maxRight = SafeAreaCanvas.PortraitWidth - 8f;
            }
            else if (!overflow && plan.CrowdEnabled && plan.CrowdComputed)
            {
                if (plan.BackBoss && slotIndex == plan.CrowdBoss)
                {
                    // On the tall canvas the boss stands in its own rank above the row: the extra height makes it fill the
                    // stage and it is vertically clear of the front row's intents, bars and bodies.
                    float bossFeet = plan.BossBackFeet;
                    slot = new PortraitCreatureSlot(540f, bossFeet, 0.94f, 0);
                    maxW = plan.OviFight ? 480f : 420f;
                    maxH = Math.Max(180f, bossFeet - bands.StageTopY - IntentBandHeight);
                    maxBar = 300f;
                    minLeft = StageSideMargin;
                    maxRight = SafeAreaCanvas.PortraitWidth - StageSideMargin;
                    Vector2 bossNative = refl.GetCreatureRawBoundsSize(enemy);
                    forceScale = Math.Min(plan.BodyTargetScale,
                        FittedCreatureScale(enemy, refl, slot, maxW, maxH, bands, isAlly: false,
                            segmented: plan.Segmented, peerScale: 1f));
                    if (bossNative.Y < 1f)
                        forceScale = Math.Min(forceScale, plan.BodyTargetScale);
                }
                else
                {
                    int cell = plan.CrowdMap[slotIndex];
                    bool boss = slotIndex == plan.CrowdBoss;
                    slot = new PortraitCreatureSlot(plan.Crowd.X[cell], bands.FrontFeetY,
                        boss ? 0.94f : (plan.Capacity <= 2 ? 1.05f : 0.82f), 1);
                    maxW = plan.Crowd.Widths[cell];
                    maxH = plan.Crowd.Heights[cell];
                    maxBar = plan.Crowd.Bars[cell];
                    minLeft = plan.CrowdLeft;
                    maxRight = SafeAreaCanvas.PortraitWidth - 8f;
                    if (!boss)
                    {
                        float cellFit = CrowdCellFit(enemy, plan.Crowd, cell, refl, bands);
                        float shared = plan.SharedBodyScale > 0f ? plan.SharedBodyScale : plan.BodyTargetScale;
                        // One value for every peer: the shared scale already came from the tightest cell; a later body
                        // larger than that cell still fits itself in.
                        forceScale = cellFit >= shared - 0.02f ? shared : Math.Min(shared, cellFit);
                    }
                    else
                    {
                        float cellFit = CrowdCellFit(enemy, plan.Crowd, cell, refl, bands);
                        forceScale = Math.Min(plan.BodyTargetScale, cellFit);
                    }
                }
            }
            else if (!overflow && plan.TallFormation.Length > 0 && slotIndex < plan.TallFormation.Length)
            {
                slot = plan.TallFormation[slotIndex];
                (maxW, maxH, minLeft, maxRight) = PortraitCombatMath.TallFormationFrame(plan.TallFormation, slotIndex, bands);
                maxBar = Math.Max(90f, maxW - 6f);
                float fit = CellFit(enemy, maxW, maxH, refl, bands);
                float shared = plan.SharedBodyScale > 0f ? plan.SharedBodyScale : plan.BodyTargetScale;
                forceScale = fit >= shared - 0.02f ? shared : Math.Min(shared, fit);
            }
            else if (!overflow && plan.ReservedBoss && plan.DominantStage.Slots.Length > 0)
            {
                // The dominant frame on the front line, capped by the frozen room scale: the boss fills its
                // 0.55W/0.43W frame and the reserved lanes fit their own bodies when their summons arrive, without the
                // later body resizing the room or moving a peer.
                if (slotIndex == plan.BossSlot)
                    maxW = plan.DominantStage.BossWidth;
                else
                    maxW = plan.DominantStage.LaneWidth;
                float roomScale = plan.BodyScaleFrozen ? plan.FrozenBodyScale : plan.SharedBodyScale;
                float fit = CellFit(enemy, maxW, maxH, refl, bands);
                if (roomScale > 0.01f)
                    forceScale = Math.Min(roomScale, fit);
                else
                    forceScale = fit;
            }
            else if (!overflow && plan.Segmented && plan.ChainComputed)
            {
                slot = new PortraitCreatureSlot(plan.ChainLeft + plan.ChainLane * (slotIndex + 0.5f),
                    bands.FrontFeetY, 0.76f, 1);
                maxW = plan.ChainMaxW;
                maxH = 900f;
                maxBar = Math.Max(MinHealthBarWidth * 0.6f, plan.ChainLane - 14f);
                minLeft = plan.ChainLeft;
                maxRight = 1066f;
                float chainFit = CellFit(enemy, maxW, maxH, refl, bands);
                float shared = plan.SharedBodyScale > 0f ? plan.SharedBodyScale : plan.BodyTargetScale;
                forceScale = Math.Min(shared, chainFit);
            }
            if (!overflow && forceScale <= 0.01f && plan.BodyTargetScale > 0f && !plan.EmbeddedCrab)
            {
                // One shared authored-size scale for the encounter, still bounded by the frame.
                forceScale = Math.Min(plan.BodyTargetScale, CellFit(enemy, maxW, maxH, refl, bands));
            }
            if (overflow)
            {
                maxW = 225f;
                maxH = 300f;
                maxBar = Math.Max(MinHealthBarWidth, 150f);
            }
            if (plan.PlacementLogged.Add(enemy.GetInstanceId()))
            {
                Vector2 footprint = refl.GetCreatureFootprintSize(enemy);
                GD.Print($"Portrait stage place {refl.GetMonsterTypeName(enemy)} slot={slotIndex} x={slot.CanvasX:F0} " +
                         $"forced={(forceScale > 0f ? forceScale : -1f):F3} max=({maxW:F0},{maxH:F0}) footprint={footprint.X:F0}x{footprint.Y:F0}");
            }
            ApplyCreatureSlot(enemy, refl, slot, centerOrigin, maxW, maxH, maxBar, bands, isAlly: false,
                peerScale: plan.PeerScale, segmented: plan.Segmented, minLeft: minLeft, maxRight: maxRight,
                forceScale: forceScale);
        }

        AlignNativeVfx(room, refl, orderedAllies, currentEnemies);
    }

    /// <summary>Where a creature's hit box ends on the right, on the canvas; null before it has one.</summary>
    private float? HitboxCanvasRight(Control creature)
    {
        if (!_creatures.TryGetValue(creature.GetInstanceId(), out CreaturePlacement? st) || st.Hitbox is not { } hit ||
            !GodotObject.IsInstanceValid(hit) || hit.Size.X < 1f)
            return null;
        Transform2D t = hit.GetGlobalTransformWithCanvas();
        return Math.Max((t * Vector2.Zero).X, (t * new Vector2(hit.Size.X, 0f)).X);
    }

    /// <summary>
    /// Where a creature's hit box starts and ends across it, in its own units from its feet (scale 1): the hit box of
    /// some creatures is not centred on the feet (the Waterfall Giant's is 30 units right of them). Without a hit box,
    /// its bounds centred on the feet.
    /// </summary>
    private (float Left, float Right) HitboxLocalExtent(Control creature, CombatReflection refl)
    {
        if (_creatures.TryGetValue(creature.GetInstanceId(), out CreaturePlacement? st) && st.Hitbox is { } hit &&
            GodotObject.IsInstanceValid(hit) && hit.Size.X >= 1f && MathF.Abs(creature.Scale.X) > 0.05f)
        {
            Transform2D toLocal = creature.GetGlobalTransformWithCanvas().AffineInverse() * hit.GetGlobalTransformWithCanvas();
            float a = (toLocal * Vector2.Zero).X, b = (toLocal * new Vector2(hit.Size.X, 0f)).X;
            return (Math.Min(a, b), Math.Max(a, b));
        }
        float half = refl.GetCreatureRawBoundsSize(creature).X * 0.5f;
        return (-half, half);
    }

    /// <summary>
    /// Where a creature's hit box starts and ends vertically, in its own units from its origin: its bottom edge is what
    /// stands on the foot line (most scenes have it at the origin, some do not).
    /// </summary>
    private (float Top, float Bottom) HitboxLocalVerticalExtent(Control creature, CombatReflection refl)
    {
        if (_creatures.TryGetValue(creature.GetInstanceId(), out CreaturePlacement? st) && st.Hitbox is { } hit &&
            GodotObject.IsInstanceValid(hit) && hit.Size.Y >= 1f && MathF.Abs(creature.Scale.Y) > 0.05f)
        {
            Transform2D toLocal = creature.GetGlobalTransformWithCanvas().AffineInverse() * hit.GetGlobalTransformWithCanvas();
            float a = (toLocal * Vector2.Zero).Y, b = (toLocal * new Vector2(0f, hit.Size.Y)).Y;
            return (Math.Min(a, b), Math.Max(a, b));
        }
        return (-refl.GetCreatureRawBoundsSize(creature).Y, 0f);
    }

    /// <summary>The scale <see cref="ApplyCreatureSlot"/> draws a creature at in a slot.</summary>
    private static float FittedCreatureScale(
        Control creature,
        CombatReflection refl,
        PortraitCreatureSlot slot,
        float maxW,
        float maxH,
        PortraitCombatBands bands,
        bool isAlly,
        bool segmented,
        float peerScale) =>
        FittedCreatureScale(creature, refl, slot, maxW, maxH, bands, isAlly, segmented, peerScale,
            refl.GetCreatureRawBoundsSize(creature), hitbox: null);

    /// <summary>
    /// The fit with the bounds and hit box already in hand (the stage pass reads both once per creature and uses them for
    /// its own gate), so this does not look the hit box up by node path again.
    /// </summary>
    private static float FittedCreatureScale(
        Control creature,
        CombatReflection refl,
        PortraitCreatureSlot slot,
        float maxW,
        float maxH,
        PortraitCombatBands bands,
        bool isAlly,
        bool segmented,
        float peerScale,
        Vector2 boundsSize,
        Control? hitbox)
    {
        // An enemy's intents stand above its head at a fixed size. On the tall canvas they stay under the run bars, so an
        // enemy that would push them over is drawn smaller. On the short canvas the stage is only just tall enough for the
        // bodies themselves; they use it whole and the intents may reach up over the relic band.
        if (!isAlly)
            maxH = Math.Min(maxH, EnemyHeightBudget(slot, bands, maxH));

        Vector2 hit = hitbox != null && GodotObject.IsInstanceValid(hitbox) ? hitbox.Size : Vector2.Zero;
        float width = Math.Max(boundsSize.X, hit.X);
        float height = Math.Max(boundsSize.Y, hit.Y);
        return PortraitCombatMath.FitCreatureScale(slot.BaseScale, peerScale, width, height, maxW, maxH);
    }

    /// <summary>
    /// How tall an enemy body may stand in a slot: its frame, capped so that on the tall canvas the fixed-size intents above
    /// its head still fit between the creature and the run bars. The short canvas spends the whole stage on the bodies.
    /// </summary>
    private static float EnemyHeightBudget(PortraitCreatureSlot slot, PortraitCombatBands bands, float frameHeight)
    {
        float intentRoom = bands.Compact ? 0f : IntentBandHeight;
        return Math.Min(frameHeight, Math.Max(140f, slot.CanvasFeetY - bands.StageTopY - intentRoom));
    }

    /// <summary>
    /// Enemies on both sides of a player in the middle (the Kaiser Crab): each side gets the stage between the
    /// player and its edge, its enemies side by side in the order the game has them, at the lone enemy's size rules.
    /// The portrait placement is the only owner of the background skeleton.
    /// </summary>
    private void LayoutSurroundingEnemies(
        CombatReflection refl,
        Control sceneContainer,
        List<Control> leftEnemies,
        List<Control> currentEnemies,
        Vector2 centerOrigin,
        CombatStagePlan plan,
        PortraitCombatBands bands)
    {
        float middle = SafeAreaCanvas.PortraitWidth * 0.5f;
        var right = new List<Control>();
        foreach (Control enemy in currentEnemies)
            if (!leftEnemies.Contains(enemy)) right.Add(enemy);
        // The encounter's slot order, so an enemy keeps its place on its side when another dies.
        int Order(Control c) => plan.SlotByCreature.TryGetValue(c.GetInstanceId(), out int index) ? index : int.MaxValue;
        leftEnemies.Sort((a, b) => Order(a).CompareTo(Order(b)));
        right.Sort((a, b) => Order(a).CompareTo(Order(b)));

        void Side(List<Control> side, float from, float to)
        {
            if (side.Count == 0) return;
            float width = (to - from) / side.Count;
            for (int i = 0; i < side.Count; i++)
            {
                PortraitCreatureSlot slot = new(from + (i + 0.5f) * width, bands.FrontFeetY, bands.Compact ? 0.84f : 0.88f, 1);
                float minLeft = from + i * width;
                float maxRight = from + (i + 1) * width;
                float sideScale = plan.CrabFrozen ? plan.CrabSharedScale : 0f;
                ulong id = side[i].GetInstanceId();
                if (plan.CrabFrozen && plan.CrabSlots.TryGetValue(id, out PortraitCreatureSlot frozenSlot))
                {
                    slot = frozenSlot;
                    if (plan.CrabBounds.TryGetValue(id, out (float MinLeft, float MaxLeft) bounds))
                    {
                        minLeft = bounds.MinLeft;
                        maxRight = bounds.MaxLeft;
                    }
                }
                else if (plan.CrabFrozen)
                {
                    // Should the game ever add a body to this room, it takes the generic overflow lane; the frozen claws
                    // keep their slots.
                    slot = PortraitCombatMath.OverflowEnemySlot(i, plan.Capacity, bands);
                    minLeft = StageSideMargin;
                    maxRight = PortraitCombatMath.EnemyStageRight;
                    sideScale = 0f;
                }
                ApplyCreatureSlot(side[i], refl, slot, centerOrigin, Math.Max(80f, maxRight - minLeft), 600f,
                    Math.Min(Math.Max(80f, maxRight - minLeft) - 16f, 360f), bands,
                    isAlly: false, peerScale: plan.PeerScale, segmented: plan.Segmented,
                    minLeft: minLeft, maxRight: maxRight,
                    forceScale: sideScale);
            }
        }

        // The Crab's two rigs are one pair of claws: freeze their slots and one scale the first time the fight is seen, so a
        // dying claw or a summon cannot move or resize the survivor.
        if (plan.EmbeddedCrab && !plan.CrabFrozen)
        {
            float shared = 0f;
            foreach (Control enemy in currentEnemies)
            {
                bool leftSide = leftEnemies.Contains(enemy);
                List<Control> side = leftSide ? leftEnemies : right;
                int position = side.IndexOf(enemy);
                if (position < 0) continue;
                float from = leftSide ? StageSideMargin : middle + SurroundedPlayerHalf;
                float to = leftSide ? middle - SurroundedPlayerHalf : SafeAreaCanvas.PortraitWidth - StageSideMargin;
                float width = (to - from) / side.Count;
                var slot = new PortraitCreatureSlot(from + (position + 0.5f) * width, bands.FrontFeetY,
                    bands.Compact ? 0.84f : 0.88f, 1);
                float fitted = FittedCreatureScale(enemy, refl, slot, width, 600f, bands,
                    isAlly: false, segmented: plan.Segmented, peerScale: plan.PeerScale);
                shared = shared <= 0f ? fitted : Math.Min(shared, fitted);
                plan.CrabSlots[enemy.GetInstanceId()] = slot;
                plan.CrabBounds[enemy.GetInstanceId()] = (from + position * width, from + (position + 1) * width);
            }
            plan.CrabSharedScale = shared;
            plan.CrabFrozen = true;
        }

        Side(leftEnemies, StageSideMargin, middle - SurroundedPlayerHalf);
        Side(right, middle + SurroundedPlayerHalf, SafeAreaCanvas.PortraitWidth - StageSideMargin);
        // The fit freezes itself only when it succeeded (FitBackgroundBossArms); a failed fit (no skeleton yet, no hit
        // boxes, a span too small) is retried on the next frame instead of being frozen for the room.
        if (plan.EmbeddedCrab && !_crabArmFitFrozen && leftEnemies.Count == 1 && right.Count == 1)
            FitBackgroundBossArms(sceneContainer, leftEnemies[0], right[0]);
    }

    /// <summary>
    /// The Kaiser Crab is one skeleton drawn in the fight's background (<c>NKaiserCrabBossBackground</c>): its body and both
    /// arms; the Crusher and the Rocket are invisible hit areas the game stands over the claws. Where the game stood those
    /// two hit areas gives where the claws are in the background; the skeleton is scaled and moved so its claws land on the
    /// portrait hit areas' outer edges, as the launcher always drew it. Applied once per room (frozen through deaths and
    /// summons); landscape gets the game's own transform back.
    /// </summary>
    private void FitBackgroundBossArms(Control sceneContainer, Control left, Control right)
    {
        Control? bgContainer = sceneContainer.GetNodeOrNull<Control>("%BgContainer") ?? sceneContainer.GetNodeOrNull<Control>("BgContainer");
        if (FindByTypeName(bgContainer, KaiserCrabBackgroundTypeName, 6) is not Node2D body || body.GetParent() is not CanvasItem parent)
            return;
        if (!_creatures.TryGetValue(left.GetInstanceId(), out CreaturePlacement? l) || l.Hitbox is not { } hitL ||
            !_creatures.TryGetValue(right.GetInstanceId(), out CreaturePlacement? r) || r.Hitbox is not { } hitR)
            return;

        Vector2 targetL = hitL.GetGlobalTransformWithCanvas() * new Vector2(0f, hitL.Size.Y * 0.5f);
        Vector2 targetR = hitR.GetGlobalTransformWithCanvas() * new Vector2(hitR.Size.X, hitR.Size.Y * 0.5f);

        Vector2 inLeft = left.GetGlobalTransformWithCanvas().AffineInverse() * targetL;
        Vector2 inRight = right.GetGlobalTransformWithCanvas().AffineInverse() * targetR;
        Vector2 bgOffset = new(DesktopBackgroundOffsetX, 0f);
        Transform2D bgCanvas = bgContainer!.GetGlobalTransformWithCanvas();
        Vector2 clawL = bgCanvas * (l.DesktopPoint(inLeft) - bgOffset);
        Vector2 clawR = bgCanvas * (r.DesktopPoint(inRight) - bgOffset);
        float span = clawR.X - clawL.X;
        if (span < 100f || targetR.X - targetL.X < 100f)
            return;

        ulong id = body.GetInstanceId();
        if (!_bossArmOriginals.TryGetValue(id, out (Vector2 Position, Vector2 Scale) original))
            _bossArmOriginals[id] = original = (body.Position, body.Scale);
        Transform2D parentCanvas = parent.GetGlobalTransformWithCanvas();
        Vector2 origin = parentCanvas * original.Position;
        float f = (targetR.X - targetL.X) / span;
        Vector2 newOrigin = (targetL + targetR) * 0.5f + f * (origin - (clawL + clawR) * 0.5f);
        body.Position = parentCanvas.AffineInverse() * newOrigin;
        body.Scale = original.Scale * f;
        _crabArmFitFrozen = true;
        GD.Print($"Portrait combat: the Kaiser Crab drawn at {body.Scale.X:F2} with its arms on the Crusher ({targetL.X:F0}) and the Rocket ({targetR.X:F0}).");
    }

    /// <summary>The combat room's background container sits this far right of the stage's middle (<c>offset_left</c>).</summary>
    private const float DesktopBackgroundOffsetX = 23f;

    private static Node? FindByTypeName(Node? node, string typeName, int depth)
    {
        if (node == null || depth < 0) return null;
        if (node.GetType().FullName == typeName) return node;
        foreach (Node child in node.GetChildren())
            if (FindByTypeName(child, typeName, depth - 1) is { } found) return found;
        return null;
    }

    public void RestoreBackgroundBosses()
    {
        foreach ((ulong id, (Vector2 position, Vector2 scale)) in _bossArmOriginals)
        {
            if (GodotObject.InstanceFromId(id) is Node2D body && GodotObject.IsInstanceValid(body))
            {
                body.Position = position;
                body.Scale = scale;
            }
        }
        _bossArmOriginals.Clear();
        _crabArmFitFrozen = false;
    }

    private void ApplyCreatureSlot(
        Control creature,
        CombatReflection refl,
        PortraitCreatureSlot slot,
        Vector2 containerOrigin,
        float maxW,
        float maxH,
        float maxBarWidth,
        PortraitCombatBands bands,
        bool isAlly,
        float peerScale,
        bool segmented,
        float minLeft = 0f,
        float maxRight = SafeAreaCanvas.PortraitWidth,
        float forceScale = 0f)
    {
        // The foot line this placement assigns, for the harness and for anyone diagnosing the stage.
        if (!creature.HasMeta("portrait_feet_y") ||
            !Mathf.IsEqualApprox(creature.GetMeta("portrait_feet_y").AsSingle(), slot.CanvasFeetY))
            creature.SetMeta("portrait_feet_y", slot.CanvasFeetY);
        ulong id = creature.GetInstanceId();
        bool haveState = _creatures.TryGetValue(id, out CreaturePlacement? cState) && cState != null;
        var placement = new CreaturePlacement.StagePlacement(slot, maxW, maxH, maxBarWidth, minLeft, maxRight,
            forceScale, containerOrigin, isAlly, segmented, peerScale);
        // The visual bounds are what the fit is measured from, so they are read once here and used both for the gate and
        // to decide whether the game's own bounds layout has to run again.
        Vector2 boundsSize = refl.GetCreatureRawBoundsSize(creature);
        if (haveState && cState!.StageIsCurrent(creature, placement, boundsSize))
        {
            // Nothing that decides this creature's place changed since the last pass: it stands where this pass put it.
            // Bodies are never re-derived from the live hit box here — the game re-lays the hit box from animated bounds
            // and the native game never moves a body for that. Only the touch target is grown back if it was left short,
            // and for the two frames after a placement the body is settled onto its line once more (the game's own layout
            // lands a frame late); after that its origin is fixed.
            CreaturePlacement known = cState;
            if (known.PlacementSettle > 0)
            {
                known.PlacementSettle--;
                SnapAndClampHitbox(creature, known, slot, bands, MathF.Abs(known.LastStageScale.X), isAlly);
                (float settleLeft, float settleRight) = HitboxLocalExtent(creature, refl);
                (_, float settleFoot) = HitboxLocalVerticalExtent(creature, refl);
                known.RememberOrigin(creature, settleLeft, settleRight, settleFoot);
            }
            else
            {
                GrowHitboxToTouchTarget(known, slot, bands, MathF.Abs(known.LastStageScale.X), isAlly);
            }
            known.RememberStage(creature, placement, boundsSize);
            _queueLabel(creature, maxBarWidth, isAlly);
            return;
        }

        float fittedScale = forceScale > 0.01f
            ? forceScale
            : FittedCreatureScale(creature, refl, slot, maxW, maxH, bands, isAlly, segmented, peerScale, boundsSize,
                cState?.Hitbox);
        if (isAlly)
            fittedScale = Math.Max(fittedScale, PortraitCombatMath.PeerScaleFloor);

        creature.PivotOffset = Vector2.Zero;
        creature.ZIndex = slot.RowDepth;
        // The stage owns scale magnitude. Native scenes can mirror the root without moving it to another slot.
        float facing = creature.Scale.X < 0f ? -1f : 1f;
        bool scaleChanged = MathF.Abs(MathF.Abs(creature.Scale.X) - fittedScale) > 0.003f;

        // A body only ever moves for a real placement change: its first placement, a new slot, or a new drawn scale (the
        // room's scale is re-frozen when the roster arrives, so the provisional placement of the player is replaced by a
        // real one — that is the change that used to leave him 25/50 units below his line). A hit box the game re-lays, a
        // changed visual bounds, or an animation never qualifies: the stored origin is re-asserted instead, so nothing the
        // game does mid-fight can move a body.
        bool needsPlacement = cState is not { PlacementApplied: true, FrameFrozen: true } placed ||
            placed.PlacementSlot != slot || scaleChanged;

        float lo, hi, foot;
        if (!needsPlacement && cState is { FrameFrozen: true } frozen)
        {
            (lo, hi, foot) = (frozen.FrameLeft, frozen.FrameRight, frozen.FrameFoot);
        }
        else
        {
            (lo, hi) = HitboxLocalExtent(creature, refl);
            (_, foot) = HitboxLocalVerticalExtent(creature, refl);
        }
        if (facing < 0f)
            (lo, hi) = (-hi, -lo);

        // A wide creature moves in so its hit box stays between minLeft and maxRight (the hit box itself, which is not
        // always centred on the feet), and the hit box's bottom edge lands on the foot line.
        float fromX = minLeft - lo * fittedScale;
        float canvasX = Math.Clamp(slot.CanvasX, fromX, Math.Max(fromX, maxRight - hi * fittedScale));
        // Two-unit grid: a hit box that shifts by a fraction of a unit between frames must not nudge a placed creature.
        canvasX = MathF.Round(canvasX * 0.5f) * 2f;
        Vector2 targetLocalPos = new(canvasX - containerOrigin.X,
            slot.CanvasFeetY - foot * fittedScale - containerOrigin.Y);
        Vector2 targetScale = new(facing * fittedScale, fittedScale);

        bool posChanged;
        if (needsPlacement)
        {
            posChanged = (creature.Position - targetLocalPos).LengthSquared() > 0.25f;
            if (posChanged) creature.Position = targetLocalPos;
            if (scaleChanged) creature.Scale = targetScale;
        }
        else
        {
            // Same slot, same drawn scale: the origin measured for it is the answer, whatever the game has done since.
            posChanged = false;
            if (cState is { PlacementApplied: true } applied &&
                (creature.Position - applied.PlacementOrigin).LengthSquared() > 0.25f)
                creature.Position = applied.PlacementOrigin;
        }

        // The visuals' own scale, which is what the game lays the hit box out from when the bounds are updated.
        Vector2 visualsScale = refl.GetCreatureVisualsScale(creature);
        bool visualsChanged = false;
        if (haveState && (cState!.LastSyncedVisualsScale - visualsScale).LengthSquared() > 0.0004f)
        {
            cState!.LastSyncedVisualsScale = visualsScale;
            visualsChanged = true;
        }

        if (needsPlacement || posChanged || scaleChanged || visualsChanged)
            refl.InvokeUpdateBounds(creature);

        if (needsPlacement)
        {
            // The game's layout for this body is done: put it on its line and the canvas once, then remember where it
            // stands. Every later pass re-asserts that origin instead of measuring again.
            SnapAndClampHitbox(creature, cState, slot, bands, fittedScale, isAlly);
            if (cState is { } placing)
            {
                (float liveLeft, float liveRight) = HitboxLocalExtent(creature, refl);
                (_, float liveFoot) = HitboxLocalVerticalExtent(creature, refl);
                placing.RememberPlacement(slot, creature.Position, liveLeft, liveRight, liveFoot);
            }
        }
        else
        {
            GrowHitboxToTouchTarget(cState, slot, bands, fittedScale, isAlly);
        }

        if (cState == null)
            return;
        cState.RememberStage(creature, placement, boundsSize);
        _queueLabel(creature, maxBarWidth, isAlly);
    }

    /// <summary>
    /// Puts a creature's body on its slot's foot line and its hit box on the canvas. This runs only for a placement (and the
    /// two settle frames after it, while the game's own late layout may still land): after that the body's origin is stored
    /// and re-asserted, so nothing the game does mid-fight moves a body.
    /// </summary>
    private void SnapAndClampHitbox(Control creature, CreaturePlacement? cState, PortraitCreatureSlot slot,
        PortraitCombatBands bands, float fittedScale, bool isAlly)
    {
        if (cState?.Hitbox is not { } placedHitbox || !GodotObject.IsInstanceValid(placedHitbox) ||
            placedHitbox.Size.Y < 1f)
            return;

        Transform2D hitTransform = placedHitbox.GetGlobalTransformWithCanvas();
        float hitBottom = (hitTransform * new Vector2(0f, placedHitbox.Size.Y)).Y;
        float groundDelta = slot.CanvasFeetY - hitBottom;
        if (MathF.Abs(groundDelta) > 0.75f)
            creature.Position += new Vector2(0f, groundDelta);

        GrowHitboxToTouchTarget(cState, slot, bands, fittedScale, isAlly);

        // The hit box is the whole tappable and targetable body: keep it (and so the bar laid out under it) on the canvas,
        // even where a scene puts part of it left of the creature's origin. This is the one place that may move a body for
        // it; afterwards the stored origin keeps it there.
        hitTransform = placedHitbox.GetGlobalTransformWithCanvas();
        Vector2 hitTopLeft = hitTransform * Vector2.Zero;
        Vector2 hitBottomRight = hitTransform * placedHitbox.Size;
        float hitLeft = Math.Min(hitTopLeft.X, hitBottomRight.X);
        float hitRight = Math.Max(hitTopLeft.X, hitBottomRight.X);
        if (hitLeft < StageSideMargin)
            creature.Position += new Vector2(StageSideMargin - hitLeft, 0f);
        else if (hitRight > SafeAreaCanvas.PortraitWidth - StageSideMargin)
            creature.Position -= new Vector2(hitRight - (SafeAreaCanvas.PortraitWidth - StageSideMargin), 0f);
    }

    /// <summary>
    /// A small enemy keeps its native proportions but its target must still take a finger: grow the hit box upward (never
    /// over the row above it) without touching the drawing or the creature's place. Idempotent, so it is safe on the gated
    /// frames: it only acts when the game has left the hit box shorter than a finger's target, and it keeps its bottom.
    /// </summary>
    private static void GrowHitboxToTouchTarget(CreaturePlacement? cState, PortraitCreatureSlot slot,
        PortraitCombatBands bands, float fittedScale, bool isAlly)
    {
        if (isAlly || cState?.Hitbox is not { } hitbox || !GodotObject.IsInstanceValid(hitbox) || hitbox.Size.Y < 1f)
            return;
        if (fittedScale <= 0.01f)
            return;
        float needed = PortraitBands.MinTarget / fittedScale;
        float room = Math.Max(0f, (slot.CanvasFeetY - bands.StageTopY) / fittedScale);
        needed = Math.Min(needed, room);
        if (needed <= hitbox.Size.Y)
            return;
        float bottom = hitbox.Position.Y + hitbox.Size.Y;
        hitbox.Size = new Vector2(hitbox.Size.X, needed);
        hitbox.Position = new Vector2(hitbox.Position.X, bottom - needed);
    }

    /// <summary>
    /// The bottom of the run screen's header (top bar row plus the potion row) plus a small margin, in canvas units: the
    /// lowest line an enemy's intent row may reach on this screen. The potion row is part of the header stone, so intents
    /// stay under it even though they may sit beside the relic icons.
    /// </summary>
    private static float PortraitHudFloorY(in PortraitCombatBands bands) =>
        Math.Max(0f, bands.StageTopY - PortraitBands.RunContentTop + PortraitBands.TopBarHeight) + 10f;

    /// <summary>
    /// Freezes the fight's stage once: the encounter's full slot table, the formation drawn from it, the one body scale
    /// the room shares, and the background transform. Nothing here reads the hand, the turn or the live roster, so only a
    /// canvas size change rebuilds it; a death or a summon later in the fight cannot move a creature already placed.
    /// </summary>
    private CombatStagePlan EnsureStagePlan(
        Control room,
        CombatRoomState state,
        CombatReflection refl,
        PortraitCombatBands bands,
        float canvasHeight,
        List<Control> players,
        List<Control> pets,
        List<Control> enemies,
        bool hasOrbs)
    {
        CombatStagePlan? plan = state.Stage;
        if (plan != null && plan.Provisional)
        {
            if (enemies.Count == 0)
            {
                if (MathF.Abs(plan.CanvasHeight - canvasHeight) >= 0.5f || plan.Bands != bands)
                {
                    plan.CanvasHeight = canvasHeight;
                    plan.Bands = bands;
                    plan.AllySlots = PortraitCombatMath.ComputeAllyStageSlots(players.Count, pets.Count, hasOrbs, bands);
                    plan.BackgroundApplied = false;
                }
                ApplyStageBackground(room, plan, refl, bands, canvasHeight);
                return plan;
            }
            // The roster has arrived: freeze for real. The background and its underlay are kept, and the meta on the
            // background remembers what the game originally drew.
            plan = null;
        }
        if (plan == null && enemies.Count == 0)
        {
            // The room is still being built: there is no roster to freeze yet. Lay the allies out provisionally and wait.
            var provisional = new CombatStagePlan { CanvasHeight = canvasHeight, Bands = bands, Capacity = 1, Provisional = true };
            provisional.AllySlots = PortraitCombatMath.ComputeAllyStageSlots(players.Count, pets.Count, hasOrbs, bands);
            state.Stage = provisional;
            ApplyStageBackground(room, provisional, refl, bands, canvasHeight);
            return provisional;
        }
        if (plan != null &&
            MathF.Abs(plan.CanvasHeight - canvasHeight) < 0.5f &&
            MathF.Abs(plan.Bands.StageTopY - bands.StageTopY) < 0.5f &&
            MathF.Abs(plan.Bands.FrontFeetY - bands.FrontFeetY) < 0.5f &&
            MathF.Abs(plan.Bands.HandTopY - bands.HandTopY) < 0.5f &&
            plan.Bands.Compact == bands.Compact)
        {
            AssignCreatureSlots(plan, refl, enemies);
            if (!plan.BackgroundApplied)
                ApplyStageBackground(room, plan, refl, bands, canvasHeight);
            return plan;
        }

        bool freshRoom = plan == null;
        if (plan != null)
            RestoreStageBackground(room, plan);
        if (freshRoom)
        {
            // A new room's stage: the Kaiser Crab's arm fit and the game transform it replaced belonged to the previous
            // fight, so a second Crab encounter fits its own arms instead of skipping the fit.
            _crabArmFitFrozen = false;
            _bossArmOriginals.Clear();
        }

        plan = new CombatStagePlan { CanvasHeight = canvasHeight, Bands = bands };
        state.Stage = plan;

        bool segmented = enemies.Count > 0;
        bool crusher = false, rocket = false;
        foreach (Control enemy in enemies)
        {
            if (!refl.IsMonsterFamily(enemy, DecimillipedeSegmentTypeName))
                segmented = false;
            if (refl.IsMonsterFamily(enemy, CrusherMonsterTypeName))
                crusher = true;
            if (refl.IsMonsterFamily(enemy, RocketMonsterTypeName))
                rocket = true;
        }
        plan.Segmented = segmented;
        plan.EmbeddedCrab = crusher && rocket;

        List<PortraitCombatMath.PortraitSlotMarker> markers = ReadEncounterSlotMarkers(room, refl, enemies);
        int capacity = Math.Max(Math.Max(1, markers.Count), enemies.Count);
        // TwoTailedRats starts with three and can summon three more (five authored slots, six bodies).
        foreach (Control enemy in enemies)
        {
            if (refl.GetMonsterTypeName(enemy) == "TwoTailedRat")
            {
                capacity = Math.Max(capacity, 6);
                break;
            }
        }
        // A boss dominates only when the roster it starts with fills every reserved slot. An encounter that reserves empty
        // slots is a summoner (the Ovicopter's eggs): its slots and depth ranks are already the right shape, and forcing a
        // dominant boss over them would put its wide health display across the summons that later fill those slots.
        bool dominant = !plan.Segmented && !plan.EmbeddedCrab && capacity > 2 && capacity == enemies.Count &&
                        DominantByArea(enemies, refl);
        // The one exception is a boss whose encounter reserves its summons' slots and starts with that boss alone: the Phrog
        // Parasite and its four wrigglers (also the Fabricator's bots and the Living Fog's bombs). It keeps the
        // dominant frame at both shapes — without the boss lift — with its feet on the front line, and the reserved slots
        // keep their lanes there from room start, so a summon appears where it was reserved and nothing reshuffles. The
        // Ovicopter is not this case: its reserved slots are eggs with their own row and its own back rank.
        plan.ReservedBoss = !dominant && !plan.Segmented && !plan.EmbeddedCrab && capacity > 2 &&
                            capacity > enemies.Count && enemies.Count == 1 && DominantByArea(enemies, refl) &&
                            BossOwnsFirstSlot(markers, enemies, refl) && !HasOvicopter(markers, enemies, refl);
        dominant |= plan.ReservedBoss;
        plan.Dominant = dominant;
        plan.Capacity = capacity;
        // The act's narrow pier, not every fight that happens to be in that act: a boss that brings its own arena scene
        // (the Waterfall Giant is an Underdocks boss with a broad waterfall arena) is framed as a broad floor. Whether a
        // custom scene is that arena or just a copy of the act's pier art is decided from the drawn art in the background
        // pass (the Lagavulin Matriarch and the Soul Fysh reuse the pier art).
        plan.Underdocks = IsUnderdocksActor(refl, players, enemies);
        plan.CustomArena = refl.EncounterHasCustomBackground(players, enemies);
        plan.Pier = plan.Underdocks && !plan.CustomArena;

        string[] names = PortraitCombatMath.OrderEncounterSlotNames(markers, dominant);
        foreach (string name in names)
        {
            if (plan.SlotIndexByName.ContainsKey(name))
                continue;
            plan.SlotIndexByName[name] = plan.SlotNames.Count;
            plan.SlotNames.Add(name);
        }

        plan.Shape = new PortraitCombatMath.PortraitStageShape(capacity, bands.Compact, plan.Dominant, plan.Segmented);
        plan.EnemySlots = PortraitCombatMath.ComputeEnemyStageSlots(plan.Shape, bands);
        plan.AllySlots = PortraitCombatMath.ComputeAllyStageSlots(players.Count, pets.Count, hasOrbs, bands,
            spacious: capacity <= 2);

        AssignCreatureSlots(plan, refl, enemies);
        plan.BossSlot = LargestEnemySlot(plan, refl, enemies);
        if (plan.ReservedBoss && plan.BossSlot == 0)
        {
            // The dominant stage without its boss lift: the boss keeps its authored frame (0.55W on the tall
            // canvas, 0.43W centred at 0.76 on the short one) with its feet on the front foot line, and the reserved slots
            // keep Capacity−1 equal lanes on that same ground band. The room scale freezes as usual, so a wriggler that
            // appears later fits its own lane without moving anything.
            plan.DominantStage = PortraitCombatMath.ComputeDominantStage(capacity, bands);
            plan.EnemySlots = plan.DominantStage.Slots;
        }
        else
        {
            plan.ReservedBoss = false;
        }
        var targetTypes = new List<string>(enemies.Count);
        foreach (Control enemy in enemies)
            targetTypes.Add(refl.GetMonsterTypeName(enemy));
        plan.BodyTargetScale = Math.Clamp(
            PortraitCombatMath.PreferredSharedBodyScale * PortraitCombatMath.EncounterBodyMultiplier(string.Join("|", targetTypes)),
            PortraitCombatMath.MinBodyScale, PortraitCombatMath.MaxBodyScale);
        int oviSlot = plan.SlotNames.IndexOf("ovicopter");
        plan.OviFight = oviSlot >= 0;
        if (!plan.OviFight)
        {
            foreach (Control enemy in enemies)
            {
                if (refl.GetMonsterTypeName(enemy) != "Ovicopter")
                    continue;
                plan.OviFight = true;
                oviSlot = plan.SlotByCreature.TryGetValue(enemy.GetInstanceId(), out int mapped) ? mapped : -1;
                break;
            }
        }
        if (plan.OviFight && oviSlot >= 0)
        {
            // The Ovicopter is the fight's one large flyer; it always gets the back slot, never a summon lane.
            plan.OviSlot = oviSlot;
            plan.BossSlot = oviSlot;
            plan.Dominant = false;
        }
        plan.Dominant = plan.Dominant && plan.BossSlot >= 0;
        plan.TallFormation = !bands.Compact && !plan.OviFight && !plan.EmbeddedCrab && !plan.Segmented &&
                             !plan.ReservedBoss && capacity >= 1 && capacity <= 5
            ? PortraitCombatMath.ComputeTallFormation(capacity, bands)
            : Array.Empty<PortraitCreatureSlot>();
        // Every non-boss group uses the one clear row at 9:16 (a pier even for one peer pair).
        int minCrowd = 2;
        plan.CrowdEnabled = plan.TallFormation.Length == 0 && plan.Capacity >= minCrowd &&
                            !plan.Dominant && !plan.Segmented && !plan.EmbeddedCrab;
        if (plan.TallFormation.Length > 0)
        {
            // A single big monster (or one more than 1.8x the next) is fitted in its own frame; it must not drag the
            // hero and the ordinary peers down with it.
            int excludeSlot = plan.BossSlot;
            if (excludeSlot < 0)
            {
                int largestSlot = -1;
                float largest = 0f, second = 0f;
                foreach (Control enemy in enemies)
                {
                    if (!plan.SlotByCreature.TryGetValue(enemy.GetInstanceId(), out int slotIndex))
                        continue;
                    Vector2 size = refl.GetCreatureFootprintSize(enemy);
                    float area = Math.Max(1f, size.X) * Math.Max(1f, size.Y);
                    if (area > largest)
                    {
                        second = largest;
                        largest = area;
                        largestSlot = slotIndex;
                    }
                    else if (area > second)
                    {
                        second = area;
                    }
                }
                if (enemies.Count == 1 || (second > 0f && largest > second * 1.8f))
                    excludeSlot = largestSlot;
            }
            plan.SharedExcludeSlot = excludeSlot;
            float tightest = 0f;
            foreach (Control enemy in enemies)
            {
                if (!plan.SlotByCreature.TryGetValue(enemy.GetInstanceId(), out int slotIndex) || slotIndex >= capacity)
                    continue;
                if (slotIndex == excludeSlot)
                    continue;
                (float frameW, float frameH, _, _) = PortraitCombatMath.TallFormationFrame(plan.TallFormation, slotIndex, bands);
                float fit = CellFit(enemy, frameW, frameH, refl, bands);
                tightest = tightest <= 0f ? fit : Math.Min(tightest, fit);
            }
            plan.SharedBodyScale = Math.Clamp(
                Math.Min(plan.BodyTargetScale, tightest > 0f ? tightest : plan.BodyTargetScale),
                PortraitCombatMath.MinBodyScale, PortraitCombatMath.MaxBodyScale);
            plan.SharedLocked = true;
        }
        if (plan.TallFormation.Length > 0 && !plan.Dominant && plan.BossSlot < 0 && capacity is 3 or 4 &&
            enemies.Count == capacity && players.Count > 0)
        {
            // A tall-thin roster of three or four draws bigger in one row than in the tall table: its cells are only
            // as tall as the stage above their intent band (the Gardeners' 380-unit bodies fit 200), while a row of four
            // across the stage lets them use their width (0.53 in the 2x2 against 0.85 in the row). The row is the crowded
            // stage, so the table is simply not built and the same frozen row that lays the 9:16 room out lays this one.
            var bodies = new (float Width, float Height)[enemies.Count];
            for (int i = 0; i < enemies.Count; i++)
            {
                Vector2 native = refl.GetCreatureFootprintSize(enemies[i]);
                bodies[i] = (Math.Max(1f, native.X), Math.Max(1f, native.Y));
            }
            bool crowded = players.Count + pets.Count > 1;
            float heroRoomScale = HeroRoomScaleFor(players, refl, crowded);
            float heroX = plan.AllySlots.Length > 0 ? plan.AllySlots[0].CanvasX : PortraitCombatMath.LonePlayerX;
            (_, float heroRightLocal) = HitboxLocalExtent(players[0], refl);
            float heroDrawn = Math.Max(PortraitCombatMath.PeerScaleFloor, heroRoomScale);
            float rowLeft = Math.Max(StageSideMargin + 6f, heroX + Math.Max(0f, heroRightLocal) * heroDrawn + 6f);
            PortraitCombatMath.PortraitRowFit row = PortraitCombatMath.FitSingleRow(capacity, bodies, bands, rowLeft,
                heroRoomScale, bands.Compact ? 0f : IntentBandHeight);
            float formationScale = plan.SharedBodyScale > 0f ? plan.SharedBodyScale : plan.BodyTargetScale;
            // Short trios read as one group rather than two tiers if the lineup keeps most of their current size.
            // Require space for a phone-sized target per enemy even before the crowded row grows a small hitbox.
            bool shortTrioFits = capacity == 3 && !crowded && row.CellWidth >= PortraitBands.MinTarget &&
                row.RoomScale >= Math.Max(PortraitCombatMath.PeerScaleFloor, Math.Min(formationScale, heroRoomScale) * 0.85f) &&
                Array.TrueForAll(bodies, body => body.Height <= body.Width);
            if (shortTrioFits || PortraitCombatMath.PrefersSingleRow(bodies, formationScale, row.RoomScale))
            {
                plan.SingleRow = true;
                plan.TallFormation = Array.Empty<PortraitCreatureSlot>();
                plan.SharedBodyScale = 0f;
                plan.SharedLocked = false;
                plan.SharedExcludeSlot = -1;
                plan.CrowdEnabled = plan.Capacity >= 2 && !plan.Segmented && !plan.EmbeddedCrab;
            }
        }
        foreach (PortraitCreatureSlot tall in plan.TallFormation)
        {
            float raise = bands.FrontFeetY - tall.CanvasFeetY;
            // A raised front-line enemy (the first of a pair, or a lone enemy) stands a little above the floor line; the
            // back ranks (hundreds of units up) are not that offset.
            if (raise > 0f && raise < 100f)
                plan.FormationOffset = Math.Max(plan.FormationOffset, raise);
        }
        plan.FormationOffset = Math.Max(0f, plan.FormationOffset);
        plan.PeerScale = ComputePeerScale(plan, refl, bands, players, pets, enemies, out bool floored);
        plan.PeerScaleFloored = floored;

        ApplyStageBackground(room, plan, refl, bands, canvasHeight);
        PublishRoomFacts(room, plan);
        return plan;
    }

    /// <summary>Gives every enemy its frozen slot index; a later summon is appended or takes a free cell, never a taken one.</summary>
    private static void AssignCreatureSlots(CombatStagePlan plan, CombatReflection refl, List<Control> enemies)
    {
        // Nothing to do while the live roster is the one already assigned: no slot can move without an add or a removal,
        // and this runs on every frame of the fight.
        bool sameRoster = enemies.Count == plan.LiveIds.Count;
        if (sameRoster)
        {
            foreach (Control enemy in enemies)
            {
                if (plan.LiveIds.Contains(enemy.GetInstanceId())) continue;
                sameRoster = false;
                break;
            }
        }
        if (sameRoster && plan.LiveIds.Count > 0)
            return;

        var live = new HashSet<ulong>(enemies.Count);
        foreach (Control enemy in enemies)
            live.Add(enemy.GetInstanceId());

        // Which live creature owns each cell already.
        var taken = new Dictionary<int, ulong>();
        foreach ((ulong id, int index) in plan.SlotByCreature)
        {
            if (live.Contains(id) && index < plan.Capacity && !taken.ContainsKey(index))
                taken[index] = id;
        }

        foreach (Control enemy in enemies)
        {
            ulong id = enemy.GetInstanceId();
            for (int i = 0; i < plan.Capacity; i++)
            {
                if (taken.TryGetValue(i, out ulong ownerId) && ownerId == id)
                    taken.Remove(i);
            }
            string key = refl.GetSlotName(enemy) ?? "";
            if (key.Length == 0)
                key = "instance:" + id;

            int index;
            if (!plan.SlotIndexByName.TryGetValue(key, out index))
            {
                index = PortraitCombatMath.EnsureSlotIndex(plan.SlotNames, key);
                plan.SlotIndexByName[key] = index;
            }
            bool free = index < plan.Capacity && (!taken.TryGetValue(index, out ulong owner) || owner == id);
            if (!free)
            {
                // A duplicate slot name (a summon reusing a living one) or an index past the frozen cells: take a cell the
                // dead left behind before adding an overflow lane, so the summon lands where the vacant slot was.
                int vacant = -1;
                for (int i = 0; i < plan.Capacity && i < plan.EnemySlots.Length; i++)
                {
                    if (!taken.ContainsKey(i))
                    {
                        vacant = i;
                        break;
                    }
                }
                index = vacant >= 0 ? vacant : Math.Max(plan.Capacity, plan.SlotNames.Count);
            }
            plan.SlotByCreature[id] = index;
            if (index < plan.Capacity)
                taken[index] = id;
        }
        plan.LiveIds.Clear();
        foreach (Control enemy in enemies)
            plan.LiveIds.Add(enemy.GetInstanceId());
    }

    /// <summary>The encounter's slot markers, with the area of the enemy standing in each (empty slots count as 0).</summary>
    private static List<PortraitCombatMath.PortraitSlotMarker> ReadEncounterSlotMarkers(
        Control room,
        CombatReflection refl,
        List<Control> enemies)
    {
        var markers = new List<PortraitCombatMath.PortraitSlotMarker>();
        if (refl.RoomEncounterSlotsProp?.GetValue(room) is not Control slots)
            return markers;

        var areas = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (Control enemy in enemies)
        {
            string name = refl.GetSlotName(enemy) ?? "";
            if (name.Length == 0)
                continue;
            Vector2 bounds = refl.GetCreatureRawBoundsSize(enemy);
            areas[name] = Math.Max(1f, bounds.X) * Math.Max(1f, bounds.Y);
        }

        foreach (Node child in slots.GetChildren())
        {
            if (child is not Node2D marker)
                continue;
            string name = marker.Name.ToString();
            if (name.Length == 0)
                continue;
            areas.TryGetValue(name, out float area);
            markers.Add(new PortraitCombatMath.PortraitSlotMarker(name, marker.Position.X, marker.Position.Y, area));
        }
        return markers;
    }

    /// <summary>The slot of the room's boss under the dominance rule; -1 when the enemies share one size.</summary>
    private static int LargestEnemySlot(CombatStagePlan plan, CombatReflection refl, List<Control> enemies)
    {
        var candidates = new List<(int Slot, float Area, string Type)>(enemies.Count);
        foreach (Control enemy in enemies)
        {
            if (!plan.SlotByCreature.TryGetValue(enemy.GetInstanceId(), out int index))
                continue;
            Vector2 size = refl.GetCreatureRawBoundsSize(enemy);
            candidates.Add((index, Math.Max(1f, size.X) * Math.Max(1f, size.Y), refl.GetMonsterTypeName(enemy)));
        }
        return PortraitCombatMath.PickBossIndex(candidates, plan.Capacity, plan.Segmented);
    }

    /// <summary>Whether one enemy is more than 1.8 times the area of the next: a boss with summons, not a peer group.</summary>
    private static bool DominantByArea(List<Control> enemies, CombatReflection refl)
    {
        if (enemies.Count == 0)
            return false;
        if (enemies.Count == 1)
            return true;
        float first = 0f, second = 0f;
        foreach (Control enemy in enemies)
        {
            Vector2 size = refl.GetCreatureRawBoundsSize(enemy);
            float area = Math.Max(1f, size.X) * Math.Max(1f, size.Y);
            if (area > first)
            {
                second = first;
                first = area;
            }
            else if (area > second)
            {
                second = area;
            }
        }
        return second <= 0f || first > second * 1.8f;
    }

    /// <summary>
    /// Whether the lone enemy's own authored marker is the one the dominant table promotes to the first slot: the table
    /// reorders the boss ahead of every other slot by area, so the two must name the same marker for the boss to keep the
    /// dominant frame when it is the roster's only body.
    /// </summary>
    private static bool BossOwnsFirstSlot(List<PortraitCombatMath.PortraitSlotMarker> markers, List<Control> enemies,
        CombatReflection refl)
    {
        if (enemies.Count != 1)
            return false;
        string key = refl.GetSlotName(enemies[0]) ?? "";
        if (key.Length == 0)
            return false;
        int boss = -1;
        for (int i = 0; i < markers.Count; i++)
        {
            if (markers[i].Area <= 0f) continue;
            if (boss < 0 || markers[i].Area > markers[boss].Area)
                boss = i;
        }
        return boss >= 0 && string.Equals(markers[boss].Name, key, StringComparison.Ordinal);
    }

    /// <summary>The Ovicopter's reserved slots are eggs with their own row and its own back rank, never a boss's lanes.</summary>
    private static bool HasOvicopter(List<PortraitCombatMath.PortraitSlotMarker> markers, List<Control> enemies,
        CombatReflection refl)
    {
        foreach (PortraitCombatMath.PortraitSlotMarker marker in markers)
        {
            if (string.Equals(marker.Name, "ovicopter", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        foreach (Control enemy in enemies)
        {
            if (refl.GetMonsterTypeName(enemy) == "Ovicopter")
                return true;
        }
        return false;
    }

    /// <summary>
    /// The hero's own room scale for a given ally frame: the shared multiplier capped by the hero's body fit,
    /// exactly as the stage resolves it (one ally's frame is 270 by 360, a crowd's 210 by 260).
    /// </summary>
    private static float HeroRoomScaleFor(List<Control> players, CombatReflection refl, bool crowded)
    {
        if (players.Count == 0)
            return PortraitCombatMath.PreferredSharedBodyScale;
        float maxW = crowded ? 210f : 270f;
        float maxH = crowded ? 260f : 360f;
        var fits = new PortraitCombatMath.PortraitFrameFit[players.Count];
        for (int i = 0; i < players.Count; i++)
        {
            Vector2 native = refl.GetCreatureFootprintSize(players[i]);
            fits[i] = new PortraitCombatMath.PortraitFrameFit(native.X, native.Y, maxW, maxH, 1f);
        }
        return PortraitCombatMath.SharedRoomScale(fits, out _);
    }

    private static bool IsUnderdocksActor(CombatReflection refl, List<Control> players, List<Control> enemies)
    {
        Control? probe = players.Count > 0 ? players[0] : (enemies.Count > 0 ? enemies[0] : null);
        return probe != null && refl.IsUnderdocks(probe);
    }

    /// <summary>
    /// The room's shared body scale, capped by the hero's own frame: the hero and the monsters share one size, one
    /// multiplier for the room. Before the roster has a player this is the value it already is.
    /// </summary>
    private static float HeroCapped(CombatStagePlan plan, float scale) =>
        plan.HeroRoomScale > 0f ? Math.Min(scale, plan.HeroRoomScale) : scale;

    /// <summary>The room's one body scale: the tightest fit any creature needs, floored so the fight stays legible.</summary>
    private static float ComputePeerScale(
        CombatStagePlan plan,
        CombatReflection refl,
        PortraitCombatBands bands,
        List<Control> players,
        List<Control> pets,
        List<Control> enemies,
        out bool floorApplied)
    {
        int allyCount = players.Count + pets.Count;
        bool crowded = allyCount > 1;
        float allyW = crowded ? 210f : 270f;
        float allyH = crowded ? 260f : 360f;
        var fallback = new PortraitCreatureSlot(PortraitCombatMath.LonePlayerX, bands.FrontFeetY, 0.76f, 1);

        var fits = new PortraitCombatMath.PortraitFrameFit[allyCount + enemies.Count];
        int count = 0;
        int allyIndex = 0;
        foreach (Control ally in players)
        {
            PortraitCreatureSlot slot = allyIndex < plan.AllySlots.Length ? plan.AllySlots[allyIndex] : fallback;
            allyIndex++;
            AddFrameFit(fits, ref count, ally, refl, bands, slot, allyW, allyH, isAlly: true, segmented: false);
        }
        foreach (Control pet in pets)
        {
            PortraitCreatureSlot slot = allyIndex < plan.AllySlots.Length ? plan.AllySlots[allyIndex] : fallback;
            allyIndex++;
            AddFrameFit(fits, ref count, pet, refl, bands, slot, allyW, allyH, isAlly: true, segmented: false);
        }
        foreach (Control enemy in enemies)
        {
            if (plan.EmbeddedCrab &&
                (refl.IsMonsterFamily(enemy, CrusherMonsterTypeName) || refl.IsMonsterFamily(enemy, RocketMonsterTypeName)))
            {
                // The Crab's two creatures are invisible hit rigs under one background skeleton; they must not shrink
                // the player they stand around.
                continue;
            }
            if (!plan.SlotByCreature.TryGetValue(enemy.GetInstanceId(), out int index) || index >= plan.EnemySlots.Length)
                continue;
            // A boss with its own large frame does not shrink every peer: it fits itself, and the hero and ordinary
            // enemies keep one shared size.
            if (index == plan.BossSlot && plan.Capacity > 2)
                continue;
            if (plan.Capacity <= 2 && index == plan.SharedExcludeSlot)
                continue;
            (float maxW, float maxH, _, _) = EnemySlotLimits(plan, index, LoneEnemyLeft);
            AddFrameFit(fits, ref count, enemy, refl, bands, plan.EnemySlots[index], maxW, maxH, isAlly: false, plan.Segmented);
        }
        return PortraitCombatMath.SharedPeerScale(fits.AsSpan(0, count), out floorApplied);
    }

    private static void AddFrameFit(
        PortraitCombatMath.PortraitFrameFit[] fits,
        ref int count,
        Control creature,
        CombatReflection refl,
        PortraitCombatBands bands,
        PortraitCreatureSlot slot,
        float maxW,
        float maxH,
        bool isAlly,
        bool segmented)
    {
        float height = isAlly ? maxH : EnemyHeightBudget(slot, bands, maxH);
        Vector2 native = refl.GetCreatureFootprintSize(creature);
        fits[count++] = new PortraitCombatMath.PortraitFrameFit(native.X, native.Y, maxW, height, slot.BaseScale);
    }

    /// <summary>
    /// How much the group is drawn down and toward the right edge so its leftmost hit box clears the player. Computed once,
    /// after the allies are placed, so a later death cannot change the survivors' places.
    /// </summary>
    private float ComputeGroupSqueeze(
        CombatStagePlan plan,
        CombatReflection refl,
        PortraitCombatBands bands,
        List<Control> enemies,
        float enemyLeft)
    {
        if (plan.Dominant || plan.EmbeddedCrab || plan.Segmented || plan.Capacity <= 1)
            return 1f;
        float leftmost = float.MaxValue;
        foreach (Control enemy in enemies)
        {
            if (!plan.SlotByCreature.TryGetValue(enemy.GetInstanceId(), out int index) || index >= plan.EnemySlots.Length)
                continue;
            (float maxW, float maxH, _, _) = EnemySlotLimits(plan, index, enemyLeft);
            float scale = FittedCreatureScale(enemy, refl, plan.EnemySlots[index], maxW, maxH, bands,
                isAlly: false, segmented: plan.Segmented, peerScale: plan.PeerScale);
            (float lo, _) = HitboxLocalExtent(enemy, refl);
            leftmost = Math.Min(leftmost, plan.EnemySlots[index].CanvasX + lo * scale);
        }
        return leftmost < float.MaxValue ? PortraitCombatMath.EnemyGroupSqueeze(leftmost, enemyLeft) : 1f;
    }

    /// <summary>The scale at which a creature's native body fits one crowded cell, never below its size floor or above its base.</summary>
    private static float CrowdCellFit(
        Control creature,
        PortraitCombatMath.PortraitStageCells cells,
        int cell,
        CombatReflection refl,
        PortraitCombatBands bands)
    {
        if (cell < 0 || cell >= cells.Widths.Length)
            return 0.82f;
        // Fit the drawn footprint (hit box merged with the visual bounds): the hit box can be wider than the art's
        // bounds (Exoskeleton arms), and using only the bounds let bodies overhang their cells.
        Vector2 native = refl.GetCreatureFootprintSize(creature);
        float fit = PortraitCombatMath.MaxBodyScale;
        float heightBudget = Math.Min(cells.Heights[cell], bands.FrontFeetY - bands.StageTopY) -
                             (bands.Compact ? 0f : IntentBandHeight);
        if (native.X > 1f)
            fit = Math.Min(fit, cells.Widths[cell] / native.X);
        if (native.Y > 1f)
            fit = Math.Min(fit, Math.Max(60f, heightBudget) / native.Y);
        return Math.Clamp(fit, PortraitCombatMath.MinBodyScale, PortraitCombatMath.MaxBodyScale);
    }

    private static float CellFit(
        Control creature,
        float width,
        float height,
        CombatReflection refl,
        PortraitCombatBands bands)
    {
        Vector2 native = refl.GetCreatureFootprintSize(creature);
        float fit = PortraitCombatMath.MaxBodyScale;
        float heightBudget = Math.Min(height, bands.FrontFeetY - bands.StageTopY) -
                             (bands.Compact ? 0f : IntentBandHeight);
        if (native.X > 1f)
            fit = Math.Min(fit, width / native.X);
        if (native.Y > 1f)
            fit = Math.Min(fit, Math.Max(60f, heightBudget) / native.Y);
        return Math.Clamp(fit, PortraitCombatMath.MinBodyScale, PortraitCombatMath.MaxBodyScale);
    }

    /// <summary>The frame an enemy at a frozen slot may use: a dominant boss's upper frame, a summon's lane, or the group table.</summary>
    private static (float MaxW, float MaxH, float MinLeft, float MaxRight) EnemySlotLimits(
        CombatStagePlan plan,
        int index,
        float enemyLeft)
    {
        if (plan.Dominant)
        {
            float lane = SafeAreaCanvas.PortraitWidth * 0.635f / Math.Max(1, plan.Capacity - 1);
            if (index == 0)
                return (SafeAreaCanvas.PortraitWidth * 0.55f, 900f, StageSideMargin, SafeAreaCanvas.PortraitWidth - StageSideMargin);
            return (lane * 0.94f, 340f, StageSideMargin, SafeAreaCanvas.PortraitWidth - StageSideMargin);
        }
        if (plan.Segmented)
        {
            float span = 1030f - 470f;
            float lane = span / Math.Max(1, plan.Capacity - 1);
            return (Math.Max(120f, lane), 600f, StageSideMargin, PortraitCombatMath.EnemyStageRight);
        }
        int count = Math.Max(1, plan.Capacity);
        float maxW = count >= 5 ? 360f : (count == 4 ? 225f : (count >= 2 ? 290f : PortraitCombatMath.EnemyStageRight - enemyLeft));
        float maxH = count >= 5 ? 420f : (count >= 4 ? 300f : (count == 3 ? 380f : (count == 2 ? 420f : 600f)));
        return (maxW, maxH, enemyLeft, PortraitCombatMath.EnemyStageRight);
    }

    private static float EnemyBaseBar(CombatStagePlan plan, int index)
    {
        if (plan.Dominant)
        {
            float lane = SafeAreaCanvas.PortraitWidth * 0.635f / Math.Max(1, plan.Capacity - 1);
            return index == 0 ? 480f : lane * 0.80f;
        }
        return plan.Capacity switch
        {
            <= 1 => 420f,
            2 => 262f,
            3 => 300f,
            4 => 264f,
            5 => 204f,
            _ => 170f,
        };
    }

    private static float EnemySlotX(CombatStagePlan plan, int index)
    {
        if (index < 0 || index >= plan.EnemySlots.Length)
            return 540f;
        float x = plan.EnemySlots[index].CanvasX;
        return plan.Squeeze < 1f ? PortraitCombatMath.EnemyStageRight - (PortraitCombatMath.EnemyStageRight - x) * plan.Squeeze : x;
    }

    /// <summary>
    /// The widest health display an enemy may have: no wider than the gap to its nearest slot, so two bars of a crowded
    /// encounter cannot run into each other, and never wider than its frame's own table.
    /// </summary>
    private static float EnemyBarLimit(CombatStagePlan plan, int index)
    {
        if (index >= plan.Capacity)
            return Math.Max(MinHealthBarWidth, 150f);
        float cap = EnemyBaseBar(plan, index) * plan.Squeeze;
        float x = EnemySlotX(plan, index);
        int depth = plan.EnemySlots[index].RowDepth;
        // Only a neighbour in the same rank competes for the bar's width: a back-rank bar sits a rank higher, so the two
        // never meet even when their columns are close (a dominant boss and its summons are the usual case).
        for (int j = index - 1; j >= 0; j--)
        {
            if (plan.EnemySlots[j].RowDepth != depth) continue;
            cap = Math.Min(cap, MathF.Abs(x - EnemySlotX(plan, j)) - 14f);
            break;
        }
        for (int j = index + 1; j < plan.Capacity; j++)
        {
            if (plan.EnemySlots[j].RowDepth != depth) continue;
            cap = Math.Min(cap, MathF.Abs(EnemySlotX(plan, j) - x) - 14f);
            break;
        }
        return Math.Max(MinHealthBarWidth * 0.7f, cap);
    }

    /// <summary>
    /// Frames the background once per room: the broad floor art's bottom edge ends exactly at the hand-control row's top,
    /// the skeleton of an Underdocks pier keeps its plank surface on the front foot line instead, a full-canvas underlay
    /// keeps native art behind the hand, and the near-camera Foreground lip is hidden so it cannot cut black over the cards.
    /// </summary>
    private void ApplyStageBackground(Control room, CombatStagePlan plan, CombatReflection refl, PortraitCombatBands bands, float canvasHeight)
    {
        if (plan.BackgroundApplied)
            return;
        if (refl.RoomBackgroundProp?.GetValue(room) == null)
            return;

        Control? sceneContainer = room.GetNodeOrNull<Control>("%CombatSceneContainer") ?? room.GetNodeOrNull<Control>("CombatSceneContainer");
        Control? bgContainer = sceneContainer?.GetNodeOrNull<Control>("%BgContainer") ?? sceneContainer?.GetNodeOrNull<Control>("BgContainer");
        if (sceneContainer == null || bgContainer == null)
            return;

        Control? bgRoot = null;
        foreach (Node child in bgContainer.GetChildren())
        {
            if (child is Control c)
            {
                bgRoot = c;
                break;
            }
        }
        if (bgRoot == null)
            return;

        Control? foreground = bgRoot?.GetNodeOrNull<Control>("Foreground");
        plan.ForegroundWasVisible = foreground?.Visible ?? true;
        if (foreground != null)
        {
            // The first plan hides the near-camera lip; a later one (a resize, or the provisional pass before the roster
            // exists) must remember what the game originally had, not read back its own hidden state.
            if (bgRoot!.HasMeta(ForegroundVisibleMeta))
                plan.ForegroundWasVisible = bgRoot.GetMeta(ForegroundVisibleMeta).AsBool();
            else
                bgRoot.SetMeta(ForegroundVisibleMeta, plan.ForegroundWasVisible);
            foreground.Visible = false;
        }

        Control? layer00 = bgRoot?.GetNodeOrNull<Control>("Layer_00");
        TextureRect? art = FindArtTextureRect(layer00 ?? bgRoot, 4) ?? FindArtTextureRect(bgRoot, 5);
        if (art == null || art.Texture == null)
        {
            // A custom arena with no broad floor art: cover the whole canvas with the background it has.
            float cover = Math.Max(1.92f, (canvasHeight + 220f) / 1296f);
            bgContainer.Scale = new Vector2(cover, cover);
            bgContainer.OffsetLeft = 0f;
            bgContainer.OffsetRight = 0f;
            bgContainer.OffsetTop = 0f;
            bgContainer.OffsetBottom = 0f;
            plan.FloorAligned = false;
            plan.PierSurface = bands.FrontFeetY;
            plan.ArtBottom = canvasHeight;
            plan.HorizonY = canvasHeight;
            plan.FloorHorizon = bands.FrontFeetY;
            PublishBackdropFacts(bgRoot, plan);
            PublishRoomFacts(room, plan);
            plan.BackgroundApplied = true;
            return;
        }

        // The floor layer itself goes back to its authored size before the art is measured, so the measured bottom edge is
        // the one this pass will draw. Its authored vertical offset still tells where the scene's foot line is.
        float nativeLayerY = layer00?.Position.Y ?? 0f;
        if (layer00 != null)
        {
            layer00.Scale = Vector2.One;
            layer00.Position = Vector2.Zero;
        }

        Transform2D toBackground = bgContainer.GetGlobalTransformWithCanvas().AffineInverse() * art.GetGlobalTransformWithCanvas();
        Vector2 topLeft = toBackground * Vector2.Zero;
        Vector2 bottomRight = toBackground * art.Size;
        float artWidth = Math.Max(1f, bottomRight.X - topLeft.X);
        float artHeight = Math.Max(1f, bottomRight.Y - topLeft.Y);

        // The Architect's ending is a picture, not a fight arena: cover the whole canvas with the scene's own backdrop,
        // centred on the composition, so the ending fills the phone with no band of empty room and its subject stays in
        // frame. A real fight keeps a ground line: its floor art is bottom-anchored below.
        if (IsEndingScene(bgRoot))
        {
            PortraitCombatMath.PortraitCoverFraming ending = PortraitCombatMath.ComputeCoverFraming(
                artWidth, artHeight, topLeft.Y, bottomRight.Y, SafeAreaCanvas.PortraitWidth, canvasHeight);
            float cover = ending.Scale;
            float eventOriginY = ending.OriginY;
            bgContainer.SetMeta("portrait_floor_horizon", eventOriginY + PortraitCombatMath.GroundArtLocalY * cover);
            bgContainer.SetMeta("portrait_formation_offset", plan.FormationOffset);
            bgContainer.Scale = new Vector2(cover, cover);
            bgContainer.OffsetLeft = 0f;
            bgContainer.OffsetRight = 0f;
            bgContainer.OffsetTop = eventOriginY - canvasHeight * 0.5f;
            bgContainer.OffsetBottom = eventOriginY - canvasHeight * 0.5f;
            plan.FloorAligned = false;
            plan.Pier = false;
            plan.CustomArena = true;
            plan.PierSurface = bands.FrontFeetY;
            plan.ArtBottom = eventOriginY + bottomRight.Y * cover;
            plan.HorizonY = eventOriginY + PortraitCombatMath.GroundArtLocalY * cover;
            plan.FloorHorizon = plan.HorizonY;
            PublishBackdropFacts(bgRoot, plan);
            PublishRoomFacts(room, plan);
            plan.BackgroundApplied = true;
            GD.Print($"Portrait combat: event backdrop covers the canvas at {cover:F3}, art bottom {plan.ArtBottom:F0}.");
            return;
        }

        // A custom background scene may be a copy of the act's own pier art (the Lagavulin Matriarch and the Soul Fysh both
        // are): it is the same pier, so frame it on the act's plank line instead of a broad floor.
        if (plan.Underdocks && !plan.Pier && IsUnderdocksPierArt(art))
        {
            plan.Pier = true;
            plan.CustomArena = false;
        }

        float scale;
        float originY;
        // The pier is not a ground plane: its plank line is a fixed part of the art, measured relative to the art's bottom
        // (the authored deck sits 861 of the 1296-unit layer art above it). Underdocks uses this at both portrait shapes;
        // the broad floor would push the deck far above the feet and leave only water on screen.
        float plankLocal = bottomRight.Y - artHeight * PlankFromBottomFraction;
        if (plan.Pier && plankLocal > topLeft.Y + 1f && plankLocal < bottomRight.Y - 1f)
        {
            // The art reaches the stage top with no seam, fills the band behind the cards, and its own plank line lands
            // exactly on the front foot line, so the creatures stand on the planks.
            PortraitCombatMath.PortraitPierFraming pier = PortraitCombatMath.ComputePierFraming(
                artWidth, topLeft.Y, bottomRight.Y, plankLocal, SafeAreaCanvas.PortraitWidth, bands);
            scale = pier.Scale;
            originY = pier.OriginY;
            plan.FloorAligned = false;
            plan.PierSurface = bands.FrontFeetY;
            float pierHorizon = pier.FarEdgeY;
            plan.FloorHorizon = pierHorizon;
            plan.HorizonY = pierHorizon;
            // If a back rank would stand beyond the plank area, keep it inside the pier instead.
            for (int i = 0; i < plan.TallFormation.Length; i++)
            {
                PortraitCreatureSlot tallSlot = plan.TallFormation[i];
                if (tallSlot.RowDepth == 0 && tallSlot.CanvasFeetY < pierHorizon + 10f)
                    plan.TallFormation[i] = tallSlot with { CanvasFeetY = pierHorizon + 10f };
            }
            plan.ArtBottom = originY + bottomRight.Y * scale;
        }
        else
        {
            float barBottom = Math.Max(0f, bands.StageTopY - PortraitBands.RunContentTop);
            float backFeet = float.MaxValue;
            foreach (PortraitCreatureSlot tallSlot in plan.TallFormation)
            {
                if (tallSlot.RowDepth == 0)
                    backFeet = Math.Min(backFeet, tallSlot.CanvasFeetY);
            }
            // The art's top in the scene's own space: Layer_00's authored offset is part of the composition (the base
            // art's registration against the parallax layers and the scene's VFX), and the 200-unit native
            // creature-origin line only means anything in that frame. The exposed arena is measured in that frame.
            float artTopInScene = topLeft.Y + nativeLayerY;
            PortraitCombatMath.PortraitBackdropFraming framing = PortraitCombatMath.ComputeBroadBackdropFraming(
                artWidth, artHeight, artTopInScene, bottomRight.Y, SafeAreaCanvas.PortraitWidth, bands, barBottom,
                plan.PeerScale, backFeet == float.MaxValue ? float.NaN : backFeet);
            scale = framing.Scale;
            originY = framing.OriginY;
            plan.FloorAligned = framing.FloorAligned;
            plan.PierSurface = bands.FrontFeetY;
            plan.ArtBottom = framing.ArtBottomY;
            plan.HorizonY = framing.FloorHorizonY;
            plan.FloorHorizon = framing.FloorHorizonY;
        }

        bgContainer.SetMeta("portrait_floor_horizon", plan.FloorHorizon);
        bgContainer.SetMeta("portrait_formation_offset", plan.FormationOffset);
        bgContainer.Scale = new Vector2(scale, scale);
        bgContainer.OffsetLeft = 0f;
        bgContainer.OffsetRight = 0f;
        bgContainer.OffsetTop = originY - canvasHeight * 0.5f;
        bgContainer.OffsetBottom = originY - canvasHeight * 0.5f;

        if (plan.Underlay == null || !GodotObject.IsInstanceValid(plan.Underlay))
        {
            TextureRect? existing = room.GetNodeOrNull<TextureRect>(BackdropNodeName);
            if (existing != null)
            {
                plan.Underlay = existing;
            }
            else
            {
                var underlay = new TextureRect
                {
                    Name = BackdropNodeName,
                    Texture = art.Texture,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    ZIndex = -20,
                };
                underlay.Position = Vector2.Zero;
                underlay.Size = new Vector2(SafeAreaCanvas.PortraitWidth, canvasHeight);
                room.AddChild(underlay);
                room.MoveChild(underlay, 0);
                plan.Underlay = underlay;
            }
        }

        PublishBackdropFacts(bgRoot, plan);
        PublishRoomFacts(room, plan);
        plan.BackgroundApplied = true;
        if (plan.FloorAligned)
            EnsureSeamBlend(room, plan, bands);
        GD.Print($"Portrait combat: backdrop {scale:F3} {(plan.FloorAligned ? "ends at the hand row" : "pier on the foot line")}, art bottom {plan.ArtBottom:F0}.");
    }

    /// <summary>
    /// Where the broad floor art stops at the hand row, a dark, vertically faded strip straddles the join so the brighter
    /// floor does not cut hard against the underlay behind the cards. It sits above the underlay and below the scene, so it
    /// darkens background only, never a card or a creature.
    /// </summary>
    private static void EnsureSeamBlend(Control room, CombatStagePlan plan, PortraitCombatBands bands)
    {
        if (plan.Seam is { } known && GodotObject.IsInstanceValid(known))
            return;
        var gradient = new Gradient();
        gradient.Offsets = [0f, 0.5f, 1f];
        gradient.Colors = [new Color(0f, 0f, 0f, 0f), new Color(0f, 0f, 0f, 0.22f), new Color(0f, 0f, 0f, 0f)];
        var texture = new GradientTexture2D
        {
            Gradient = gradient,
            Width = 8,
            Height = 128,
            FillFrom = new Vector2(0f, 0f),
            FillTo = new Vector2(0f, 1f),
        };
        var seam = new TextureRect
        {
            Name = "PortraitSeamBlend",
            Texture = texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ZIndex = -10,
            Position = new Vector2(0f, bands.HandTopY - 70f),
            Size = new Vector2(SafeAreaCanvas.PortraitWidth, 140f),
        };
        room.AddChild(seam);
        plan.Seam = seam;
    }

    /// <summary>
    /// The arena's own artwork: the largest texture the background shapes, not the first one met. A background may hold a
    /// cloud sheet as well as its base art (the Architect's ending scene has both), and the base art is the one that covers
    /// the arena; a smaller sheet framed as the floor would draw the scene at the wrong scale and place its subject off the
    /// screen.
    /// </summary>
    private static TextureRect? FindArtTextureRect(Node? node, int depth)
    {
        TextureRect? best = null;
        float bestArea = 0f;
        CollectArtTextureRect(node, depth, ref best, ref bestArea);
        return best;
    }

    private static void CollectArtTextureRect(Node? node, int depth, ref TextureRect? best, ref float bestArea)
    {
        if (node == null || depth < 0)
            return;
        foreach (Node child in node.GetChildren())
        {
            if (child is TextureRect rect && rect.Texture != null && rect.Size.X > 1000f && rect.Size.Y > 300f)
            {
                float area = rect.Size.X * rect.Size.Y * MathF.Abs(rect.Scale.X * rect.Scale.Y);
                if (area > bestArea)
                {
                    best = rect;
                    bestArea = area;
                }
            }
            CollectArtTextureRect(child, depth - 1, ref best, ref bestArea);
        }
    }

    /// <summary>Whether the background is the Architect's ending picture, which is framed as a cover instead of a floor.</summary>
    private static bool IsEndingScene(Control? bgRoot) =>
        bgRoot?.SceneFilePath == ArchitectEndingBackgroundScenePath;

    /// <summary>Whether the drawn art is the act's calibrated Underdocks pier.</summary>
    private static bool IsUnderdocksPierArt(TextureRect art)
    {
        string path = art.Texture?.ResourcePath ?? string.Empty;
        return path == UnderdocksPierArtPath || path.EndsWith("/underdocks/underdocks_00.png", StringComparison.Ordinal);
    }

    private static void PublishBackdropFacts(Control? bgRoot, CombatStagePlan plan)
    {
        if (bgRoot == null)
            return;
        bgRoot.SetMeta("portrait_floor_aligned", plan.FloorAligned);
        bgRoot.SetMeta("portrait_art_bottom", plan.ArtBottom);
        bgRoot.SetMeta("portrait_pier", plan.Pier);
        bgRoot.SetMeta("portrait_custom_arena", plan.CustomArena);
        bgRoot.SetMeta("portrait_pier_surface", plan.PierSurface);
        bgRoot.SetMeta("portrait_floor_horizon", plan.FloorHorizon);
        bgRoot.SetMeta("portrait_formation_offset", plan.FormationOffset);
    }

    private static void PublishRoomFacts(Control room, CombatStagePlan plan)
    {
        room.SetMeta("portrait_hud_floor_y", PortraitHudFloorY(plan.Bands));
        room.SetMeta("portrait_peer_scale", Math.Clamp(plan.PeerScale, PortraitCombatMath.PeerScaleFloor, 1f));
        room.SetMeta("portrait_peer_scale_floored", plan.PeerScaleFloored);
        room.SetMeta("portrait_floor_horizon", plan.FloorHorizon);
        room.SetMeta("portrait_formation_offset", plan.FormationOffset);
    }

    /// <summary>Removes a portrait measurement a node carries, if the node still has it.</summary>
    private static void RemoveMeta(GodotObject node, string key)
    {
        if (node.HasMeta(key))
            node.RemoveMeta(key);
    }

    /// <summary>Puts the background back the way the game drew it and removes the underlay this pass added.</summary>
    private static void RestoreStageBackground(Control room, CombatStagePlan plan)
    {
        if (plan.Underlay != null && GodotObject.IsInstanceValid(plan.Underlay))
        {
            plan.Underlay.GetParent()?.RemoveChild(plan.Underlay);
            plan.Underlay.QueueFree();
        }
        plan.Underlay = null;
        if (plan.Seam != null && GodotObject.IsInstanceValid(plan.Seam))
        {
            plan.Seam.GetParent()?.RemoveChild(plan.Seam);
            plan.Seam.QueueFree();
        }
        plan.Seam = null;

        Control? sceneContainer = room.GetNodeOrNull<Control>("%CombatSceneContainer") ?? room.GetNodeOrNull<Control>("CombatSceneContainer");
        Control? bgContainer = sceneContainer?.GetNodeOrNull<Control>("%BgContainer") ?? sceneContainer?.GetNodeOrNull<Control>("BgContainer");
        if (bgContainer != null)
        {
            // The backdrop facts are the portrait framing's measurements (the harness reads them in portrait); the game's own
            // landscape background must not be reported as if it were framed.
            RemoveMeta(bgContainer, "portrait_floor_horizon");
            RemoveMeta(bgContainer, "portrait_formation_offset");
            foreach (Node child in bgContainer.GetChildren())
            {
                if (child is not Control bgRoot)
                    continue;
                if (bgRoot.GetNodeOrNull<Control>("Foreground") is { } foreground)
                    foreground.Visible = plan.ForegroundWasVisible;
                if (bgRoot.HasMeta(ForegroundVisibleMeta))
                    bgRoot.RemoveMeta(ForegroundVisibleMeta);
                RemoveMeta(bgRoot, "portrait_floor_aligned");
                RemoveMeta(bgRoot, "portrait_art_bottom");
                RemoveMeta(bgRoot, "portrait_pier");
                RemoveMeta(bgRoot, "portrait_custom_arena");
                RemoveMeta(bgRoot, "portrait_pier_surface");
                RemoveMeta(bgRoot, "portrait_floor_horizon");
                RemoveMeta(bgRoot, "portrait_formation_offset");
                break;
            }
        }
        plan.BackgroundApplied = false;
    }

    /// <summary>
    /// Native numbers and effects spawn at anchors computed from an unscaled hit box; with the creature drawn at a portrait
    /// scale those points land off the body. Each new child of the fight's VFX container is placed once on the creature it
    /// belongs to, with the same clamps: numbers inside the hit box, Ki and relic flashes at its top, splashes at its feet.
    /// </summary>
    private void AlignNativeVfx(Control room, CombatReflection refl, List<Control> allies, List<Control> enemies)
    {
        PortraitFrameTiming.SetCreatures(allies.Count + enemies.Count);
        if (refl.RoomCombatVfxContainerProp?.GetValue(room) is not Control container)
            return;

        // The container holds every effect the fight has spawned so far and only ever grows. Its child_entered_tree signal
        // says exactly when there is something new to place, so the pass no longer marshals the whole child array every
        // frame (during an enemy turn that is dozens of damage numbers and effects). Freed effects need no work.
        ulong containerId = container.GetInstanceId();
        if (_wiredVfxContainers.Add(containerId))
        {
            _vfxDirty = true;
            if (container.HasSignal("child_entered_tree"))
                container.Connect("child_entered_tree", Callable.From<Node>(_ => _vfxDirty = true));
        }
        if (!_vfxDirty)
            return;
        _vfxDirty = false;

        using (PortraitFrameTiming.Begin(PortraitFrameTiming.Vfx))
        {
            _vfxCreatures.Clear();
            _vfxCreatures.AddRange(allies);
            _vfxCreatures.AddRange(enemies);

            int vfxChildren = container.GetChildCount();
            for (int i = 0; i < vfxChildren; i++)
            {
                Node child = container.GetChild(i);
                if (child.IsQueuedForDeletion())
                    continue;
                if (child is not Node2D && child is not Control)
                    continue;
                ulong id = child.GetInstanceId();
                if (!_alignedVfx.Add(id))
                    continue;

                Vector2 spawn = child is Node2D node2D ? node2D.GlobalPosition : ((Control)child).GlobalPosition;
                if (!TryFindVfxCreature(spawn, _vfxCreatures, out _, out Rect2 box))
                    continue;
                Vector2 target = VfxAnchorFor(child.GetType().Name, spawn, box);
                if (target.DistanceSquaredTo(spawn) < 0.01f)
                    continue;
                if (child.GetParent() is not CanvasItem parent)
                    continue;
                Vector2 local = parent.GetGlobalTransformWithCanvas().AffineInverse() * target;
                if (child is Node2D node)
                    node.Position = local;
                else
                    ((Control)child).Position = local;
            }
        }

        if (++_vfxPasses % 600 == 0)
            PruneAlignedVfx();
    }

    /// <summary>The creature whose hit box the effect spawned on, or the nearest one just beyond it.</summary>
    private bool TryFindVfxCreature(Vector2 point, List<Control> creatures, out Control? best, out Rect2 bestBox)
    {
        best = null;
        bestBox = default;
        float bestDistance = 160f;
        foreach (Control creature in creatures)
        {
            if (!_creatures.TryGetValue(creature.GetInstanceId(), out CreaturePlacement? state))
                continue;
            if (!TryHitboxCanvasRect(state, out Rect2 box))
                continue;
            float dx = Math.Max(Math.Max(box.Position.X - point.X, 0f), point.X - box.End.X);
            float dy = Math.Max(Math.Max(box.Position.Y - point.Y, 0f), point.Y - box.End.Y);
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = creature;
                bestBox = box;
            }
        }
        return best != null;
    }

    private static bool TryHitboxCanvasRect(CreaturePlacement state, out Rect2 rect)
    {
        rect = default;
        if (state.Hitbox is not { } hitbox || !GodotObject.IsInstanceValid(hitbox) || hitbox.Size.X < 1f || hitbox.Size.Y < 1f)
            return false;
        Transform2D transform = hitbox.GetGlobalTransformWithCanvas();
        Vector2 a = transform * Vector2.Zero;
        Vector2 b = transform * hitbox.Size;
        Vector2 topLeft = new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
        Vector2 bottomRight = new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        rect = new Rect2(topLeft, bottomRight - topLeft);
        return rect.Size.X > 2f && rect.Size.Y > 2f;
    }

    private static Vector2 VfxAnchorFor(string typeName, Vector2 spawn, Rect2 box)
    {
        float centreX = box.Position.X + box.Size.X * 0.5f;
        if (typeName.EndsWith("KiVfx", StringComparison.Ordinal))
            return new Vector2(centreX, box.Position.Y + box.Size.Y * 0.23f);
        if (typeName.EndsWith("SplashVfx", StringComparison.Ordinal))
            return new Vector2(centreX, box.End.Y);
        if (typeName.EndsWith("RelicFlashVfx", StringComparison.Ordinal))
            return new Vector2(centreX, box.Position.Y);
        if (typeName.Contains("DamageNumVfx", StringComparison.Ordinal) ||
            typeName.Contains("HealNumVfx", StringComparison.Ordinal) ||
            typeName.Contains("DamageBlockedVfx", StringComparison.Ordinal) ||
            typeName.Contains("PowerApplied", StringComparison.Ordinal) ||
            typeName.Contains("PowerUpVfx", StringComparison.Ordinal))
            return OnBody(spawn, box);
        // Everything else keeps the game's own placement: only the anchors the port corrects are moved.
        return spawn;
    }

    /// <summary>The body clamp: a spawn point kept within the middle of the hit box.</summary>
    private static Vector2 OnBody(Vector2 point, Rect2 box)
    {
        float left = box.Position.X + box.Size.X * 0.25f;
        float right = box.End.X - box.Size.X * 0.25f;
        float top = box.Position.Y + box.Size.Y * 0.30f;
        float bottom = box.End.Y - box.Size.Y * 0.25f;
        float x = right > left ? Math.Clamp(point.X, left, right) : box.Position.X + box.Size.X * 0.5f;
        float y = bottom > top ? Math.Clamp(point.Y, top, bottom) : box.Position.Y + box.Size.Y * 0.5f;
        return new Vector2(x, y);
    }

    private void PruneAlignedVfx()
    {
        var dead = new List<ulong>();
        foreach (ulong id in _alignedVfx)
        {
            if (!GodotObject.IsInstanceIdValid(id))
                dead.Add(id);
        }
        foreach (ulong id in dead)
            _alignedVfx.Remove(id);
    }

    /// <summary>
    /// What a fight froze when the room opened: the encounter's full slot table (empty slots included), the formation
    /// drawn from it, the room's one body scale, and the background transform and its underlay.
    /// </summary>
    private sealed class CombatStagePlan
    {
        public float CanvasHeight;
        public PortraitCombatBands Bands;
        public PortraitCombatMath.PortraitStageShape Shape;
        public int Capacity;
        public bool Dominant;
        public bool Segmented;
        /// <summary>
        /// A lone boss whose encounter reserves its summons' slots: it keeps the dominant frame with its feet on
        /// the front line, and the reserved slots keep their lanes there from room start.
        /// </summary>
        public bool ReservedBoss;
        public PortraitCombatMath.PortraitDominantStage DominantStage;
        /// <summary>A tall-thin 3–4 body roster drawn in the crowded single row instead of the tall table.</summary>
        public bool SingleRow;
        public bool Pier;
        /// <summary>The fight is in the Underdocks, whose layer-00 art is the narrow pier when the encounter draws it.</summary>
        public bool Underdocks;
        /// <summary>This encounter brings its own arena scene, so the act's pier is not what the room is drawn on.</summary>
        public bool CustomArena;
        public bool EmbeddedCrab;
        public float PeerScale = 1f;
        public bool PeerScaleFloored;
        public float Squeeze = 1f;
        public bool SqueezeComputed;
        public bool ChainComputed;
        public float ChainLeft;
        public float ChainLane;
        public float ChainMaxW;
        public int BossSlot = -1;
        public bool OviFight;
        public int OviSlot = -1;
        public bool CrowdEnabled;
        public bool CrowdComputed;
        public bool Staggered;
        public PortraitCreatureSlot[] StaggerSlots = Array.Empty<PortraitCreatureSlot>();
        public float[] StaggerWidths = Array.Empty<float>();
        public float[] StaggerHeights = Array.Empty<float>();
        public float[] StaggerBars = Array.Empty<float>();
        public bool BackBoss;
        public int CrowdBoss = -1;
        public float BossBackFeet;
        public float CrowdLeft;
        public int[] CrowdMap = Array.Empty<int>();
        public PortraitCombatMath.PortraitStageCells Crowd;
        public PortraitCreatureSlot[] EnemySlots = Array.Empty<PortraitCreatureSlot>();
        public PortraitCreatureSlot[] TallFormation = Array.Empty<PortraitCreatureSlot>();
        public PortraitCreatureSlot[] AllySlots = Array.Empty<PortraitCreatureSlot>();
        public readonly List<string> SlotNames = new();
        public readonly Dictionary<string, int> SlotIndexByName = new(StringComparer.Ordinal);
        public readonly Dictionary<ulong, int> SlotByCreature = new();
        public bool BackgroundApplied;
        public bool? SurroundedFrozen;
        public float PlayerShift;
        public float HorizonY;
        public float FloorHorizon;
        public float FormationOffset;
        public int SharedExcludeSlot = -1;
        public readonly HashSet<ulong> CrabLeftIds = new();
        public bool CrabFrozen;
        public float CrabSharedScale;
        public readonly Dictionary<ulong, PortraitCreatureSlot> CrabSlots = new();
        public readonly Dictionary<ulong, (float MinLeft, float MaxLeft)> CrabBounds = new();
        public float SharedBodyScale;
        public bool SharedLocked;
        /// <summary>The world scale the hero's own frame allows, frozen with the roster; caps the whole room.</summary>
        public float HeroRoomScale;
        public bool HeroScaleComputed;
        /// <summary>The room scale frozen with the opening roster; later summons fit into it.</summary>
        public float FrozenBodyScale;
        public bool BodyScaleFrozen;
        /// <summary>The enemy roster the slot table was last assigned for, so an unchanged roster costs nothing.</summary>
        public readonly HashSet<ulong> LiveIds = new();
        public bool StageLogged;
        public readonly HashSet<ulong> PlacementLogged = new();
        public float BodyTargetScale;
        public TextureRect? Seam;
        public bool Provisional;
        public bool ForegroundWasVisible = true;
        public TextureRect? Underlay;
        public bool FloorAligned;
        public float ArtBottom;
        public float PierSurface;
    }

    private void CaptureCreatureIfNeeded(Control creature, CombatReflection refl)
    {
        ulong id = creature.GetInstanceId();
        if (_creatures.ContainsKey(id))
            return;
        var placement = new CreaturePlacement(creature);
        _captureHud(creature, placement.Hitbox, refl);
        _creatures.Add(id, placement);
    }

    private sealed class CreaturePlacement
    {
        private readonly Vector2 _position;
        private readonly Vector2 _scale;
        private readonly Vector2 _pivotOffset;
        private readonly int _zIndex;

        /// <summary>The visuals' scale when the game's own bounds layout last ran (see ApplyCreatureSlot).</summary>
        public Vector2 LastSyncedVisualsScale;

        /// <summary>
        /// Where the body was actually put, and the hit box frame it was put there with: how far the hit box's left,
        /// right and bottom sit from the creature's origin (creature units) when it was measured. A placement is only
        /// computed once — first placement, new slot, new drawn scale — and every later pass re-asserts
        /// <see cref="PlacementOrigin"/>; the live hit box can never move a body (see RememberPlacement).
        /// </summary>
        public bool PlacementApplied;
        public PortraitCreatureSlot PlacementSlot;
        public Vector2 PlacementOrigin;
        public bool FrameFrozen;
        public float FrameLeft, FrameRight, FrameFoot;

        /// <summary>Frames left in which the game's own late layout may still be settled onto the line after a placement.</summary>
        public int PlacementSettle;

        /// <summary>
        /// Remembers a placement: the body's local origin, and the hit box frame measured after the game laid it out and
        /// after the one-shot ground snap. The native game never moves a body when its hit box re-lays from an animated
        /// bounds change, so nothing re-reads the hit box once this is stored: a body that followed it drifted (2 units,
        /// then 50, after a card play).
        /// </summary>
        public void RememberPlacement(PortraitCreatureSlot slot, Vector2 origin, float left, float right, float foot)
        {
            PlacementApplied = true;
            PlacementSlot = slot;
            PlacementOrigin = origin;
            FrameLeft = left;
            FrameRight = right;
            FrameFoot = foot;
            FrameFrozen = true;
            PlacementSettle = 2;
        }

        /// <summary>After a settle frame's snap: the body is on its line, so this is its origin and frame from now on.</summary>
        public void RememberOrigin(Control creature, float left, float right, float foot)
        {
            PlacementOrigin = creature.Position;
            FrameLeft = left;
            FrameRight = right;
            FrameFoot = foot;
        }

        /// <summary>The placement the stage pass last wrote, so a creature nothing has moved is not laid out again.</summary>
        public StagePlacement LastPlacement;
        public bool StageApplied;
        public Vector2 LastStagePosition, LastStageScale, LastHitboxLocal, LastHitboxSize, LastBoundsSize;

        /// <summary>Everything that decides where a creature stands: the slot, its frame and the container origin.</summary>
        public readonly record struct StagePlacement(PortraitCreatureSlot Slot, float MaxW, float MaxH, float MaxBar,
            float MinLeft, float MaxRight, float ForceScale, Vector2 Origin, bool IsAlly, bool Segmented, float PeerScale);

        /// <summary>
        /// Whether nothing that decides this creature's place has changed since the stage pass last wrote it: the slot and
        /// frame it was given, the node's own transform, its hit box's local place and size, and the visual bounds the fit
        /// is measured from. A creature that passes this is already where the pass would put it.
        /// </summary>
        public bool StageIsCurrent(Control creature, StagePlacement placement, Vector2 boundsSize)
        {
            if (!StageApplied || LastPlacement != placement) return false;
            if ((creature.Position - LastStagePosition).LengthSquared() > 0.25f) return false;
            if ((creature.Scale - LastStageScale).LengthSquared() > 0.000004f) return false;
            // The pass also owns these two: the game raises a creature's Z while it attacks and never puts it back.
            if (creature.ZIndex != placement.Slot.RowDepth) return false;
            if (creature.PivotOffset != Vector2.Zero) return false;
            if ((boundsSize - LastBoundsSize).LengthSquared() > 0.25f) return false;
            if (Hitbox is not { } hitbox || !GodotObject.IsInstanceValid(hitbox)) return false;
            if ((hitbox.Position - LastHitboxLocal).LengthSquared() > 0.25f) return false;
            return (hitbox.Size - LastHitboxSize).LengthSquared() <= 0.25f;
        }

        /// <summary>After the stage pass placed the creature: everything <see cref="StageIsCurrent"/> compares.</summary>
        public void RememberStage(Control creature, StagePlacement placement, Vector2 boundsSize)
        {
            LastPlacement = placement;
            StageApplied = true;
            LastStagePosition = creature.Position;
            LastStageScale = creature.Scale;
            LastBoundsSize = boundsSize;
            if (Hitbox is { } hitbox && GodotObject.IsInstanceValid(hitbox))
            {
                LastHitboxLocal = hitbox.Position;
                LastHitboxSize = hitbox.Size;
            }
        }

        /// <summary>Where the game stood the creature across the stage (its container's units; the stage's middle is 0).</summary>
        public float DesktopX => _position.X;

        /// <summary>Where a point of the creature (its own units) was in its container when the game laid it out.</summary>
        public Vector2 DesktopPoint(Vector2 local) => _position + _pivotOffset + _scale * (local - _pivotOffset);

        public readonly Control? Hitbox;

        public CreaturePlacement(Control creature)
        {

            _position = creature.Position;
            _scale = creature.Scale;
            _pivotOffset = creature.PivotOffset;
            _zIndex = creature.ZIndex;

            Hitbox = creature.GetNodeOrNull<Control>("%Hitbox") ?? creature.GetNodeOrNull<Control>("Hitbox");

        }

        public void RestoreRoot(Control creature)
        {

            creature.PivotOffset = _pivotOffset;
            creature.Scale = new Vector2(MathF.CopySign(MathF.Abs(_scale.X), creature.Scale.X), _scale.Y);
            creature.Position = _position;
            creature.ZIndex = _zIndex;

        }
    }


    private sealed class CombatRoomState
    {
        public CombatStagePlan? Stage;

        private readonly Vector2 _sceneScale, _scenePivot, _scenePos;
        private readonly Vector2 _bgScale;
        private readonly float _bgOffLeft, _bgOffTop, _bgOffRight, _bgOffBottom;
        private readonly Vector2 _layer00Scale, _layer00Pos;
        private readonly Vector2 _fgPos;

        private CombatRoomState(
            Vector2 sceneScale,
            Vector2 scenePivot,
            Vector2 scenePos,
            Vector2 bgScale,
            float bgOffLeft,
            float bgOffTop,
            float bgOffRight,
            float bgOffBottom,
            Vector2 layer00Scale,
            Vector2 layer00Pos,
            Vector2 fgPos)
        {
            _sceneScale = sceneScale;
            _scenePivot = scenePivot;
            _scenePos = scenePos;
            _bgScale = bgScale;
            _bgOffLeft = bgOffLeft;
            _bgOffTop = bgOffTop;
            _bgOffRight = bgOffRight;
            _bgOffBottom = bgOffBottom;
            _layer00Scale = layer00Scale;
            _layer00Pos = layer00Pos;
            _fgPos = fgPos;
        }

        public static CombatRoomState Capture(Control room)
        {
            Control? scene = room.GetNodeOrNull<Control>("%CombatSceneContainer") ?? room.GetNodeOrNull<Control>("CombatSceneContainer");
            Control? bg = scene?.GetNodeOrNull<Control>("%BgContainer") ?? scene?.GetNodeOrNull<Control>("BgContainer");
            Control? bgRoot = null;
            if (bg != null)
            {
                foreach (Node child in bg.GetChildren())
                {
                    if (child is Control c) { bgRoot = c; break; }
                }
            }
            Control? layer00 = bgRoot?.GetNodeOrNull<Control>("Layer_00");
            Control? fg = bgRoot?.GetNodeOrNull<Control>("Foreground");

            return new CombatRoomState(
                scene?.Scale ?? Vector2.One,
                scene?.PivotOffset ?? new Vector2(960f, 540f),
                scene?.Position ?? Vector2.Zero,
                bg?.Scale ?? Vector2.One,
                bg?.OffsetLeft ?? 23f,
                bg?.OffsetTop ?? 0f,
                bg?.OffsetRight ?? 23f,
                bg?.OffsetBottom ?? 0f,
                layer00?.Scale ?? Vector2.One,
                layer00?.Position ?? Vector2.Zero,
                fg?.Position ?? Vector2.Zero);
        }

        public void Restore(Control room, CombatReflection refl)
        {
            if (Stage is { } plan)
            {
                RestoreStageBackground(room, plan);
                Stage = null;
            }
            // These are the stage's portrait measurements (the harness reads them while the room is in portrait): they must
            // not outlive the layout and be read as facts about the game's own room.
            RemoveMeta(room, "portrait_hud_floor_y");
            RemoveMeta(room, "portrait_peer_scale");
            RemoveMeta(room, "portrait_peer_scale_floored");
            RemoveMeta(room, "portrait_floor_horizon");
            RemoveMeta(room, "portrait_formation_offset");
            Control? scene = room.GetNodeOrNull<Control>("%CombatSceneContainer") ?? room.GetNodeOrNull<Control>("CombatSceneContainer");
            if (scene != null)
            {
                scene.PivotOffset = _scenePivot;
                scene.Scale = _sceneScale;
                scene.Position = _scenePos;

                if ((scene.GetNodeOrNull<Control>("%BgContainer") ?? scene.GetNodeOrNull<Control>("BgContainer")) is { } bg)
                {
                    bg.Scale = _bgScale;
                    bg.OffsetLeft = _bgOffLeft;
                    bg.OffsetTop = _bgOffTop;
                    bg.OffsetRight = _bgOffRight;
                    bg.OffsetBottom = _bgOffBottom;
                    foreach (Node child in bg.GetChildren())
                    {
                        if (child is not Control bgRoot) continue;
                        if (bgRoot.GetNodeOrNull<Control>("Layer_00") is { } l0)
                        {
                            l0.Scale = _layer00Scale;
                            l0.Position = _layer00Pos;
                        }
                        if (bgRoot.GetNodeOrNull<Control>("Foreground") is { } fg)
                            fg.Position = _fgPos;
                        break;
                    }
                    scene.Call("OnWindowChange");
                }

                if ((scene.GetNodeOrNull<Control>("%AllyContainer") ?? scene.GetNodeOrNull<Control>("AllyContainer")) is { } ally)
                    ally.Position = scene.Size * 0.5f;
                if ((scene.GetNodeOrNull<Control>("%EnemyContainer") ?? scene.GetNodeOrNull<Control>("EnemyContainer")) is { } enemy)
                    enemy.Position = scene.Size * 0.5f;
            }

            refl.RoomAdjustScaleMethod?.Invoke(room, null);

            if (refl.RoomCreatureNodesField?.GetValue(room) is IEnumerable creatures)
            {
                foreach (object? item in creatures)
                {
                    if (item is Control c && GodotObject.IsInstanceValid(c))
                        refl.InvokeUpdateBounds(c);
                }
            }


        }
    }
}
