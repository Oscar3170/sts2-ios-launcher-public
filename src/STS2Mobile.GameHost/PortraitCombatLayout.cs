using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Portrait-mode combat room layout: a stage and background frozen once per room from the encounter's own slots and a
/// shared legibility scale for the bodies, the permanent two-row hand (<c>NPlayerHand</c>), the one-row hand-control rail,
/// and high-contrast card playability. The bands come from the canvas and the run bars only, so the hand, rail and stage
/// never move when a card is drawn or played. Captures initial desktop layout before any portrait edit so toggling
/// Portrait Mode off mid-fight restores the original landscape combat layout cleanly. The End Turn and pile buttons are
/// 44 points across, and each creature's health display and intents are drawn at one size on the canvas so their text is
/// 11 points.
/// </summary>
internal sealed class PortraitCombatLayout
{
    /// <summary>
    /// Runs after the game's own process/tween steps and before the frame is drawn. It re-applies only what a native tween can
    /// move after the full rail pass (End Turn, the energy counter, the three piles, the star on the counter), and the hand
    /// only when its inputs changed; everything else on the rail is set once per state change.
    /// </summary>
    private void OnFramePreDraw()
    {
        // The frame's timing bucket closes on every path, including the early returns (PortraitFrameTiming folds a frame only
        // once, so a second call is harmless, but a missed one would carry this frame's passes into the next).
        try
        {
            if (!SafeAreaCanvas.IsPortrait)
                return;
            CombatReflection refl = EnsureReflection();
            Control? room = refl.GetActiveCombatRoom() as Control;
            if (room != null && (!GodotObject.IsInstanceValid(room) || !room.IsInsideTree()))
                room = null;
            Control? ui = room?.GetNodeOrNull<Control>("%CombatUi") ?? room?.GetNodeOrNull<Control>("CombatUi");
            Control? hand = ui is { } liveUi && liveUi.IsVisibleInTree()
                ? liveUi.GetNodeOrNull<Control>("%Hand") ?? liveUi.GetNodeOrNull<Control>("Hand")
                : null;
            if (room == null || ui == null || hand == null)
            {
                // The room, its UI or the hand went away while a card was still losing its glow or shadow: put back anything
                // the passes took away. The card pool does not reset these, so the card would draw them on its next use.
                _hand.OnHandUnavailable(refl);
                return;
            }

            try
            {
                using (PortraitFrameTiming.Begin(PortraitFrameTiming.PreDraw))
                {
                    EnsureRoomState(room, refl);
                    float canvasHeight = Math.Max(room.GetViewport().GetVisibleRect().Size.Y, SafeAreaCanvas.MinPortraitHeight);
                    PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, stageTopY: 0f);
                    PortraitHudLayout hud = PortraitCombatMath.ComputeHudLayout(canvasHeight, bands);
                    using (PortraitFrameTiming.Begin(PortraitFrameTiming.Rail))
                        _rail.OnFramePreDraw(ui, room, refl, hud);
                    using (PortraitFrameTiming.Begin(PortraitFrameTiming.Hand))
                        _hand.SyncHandAndPlayabilityPortrait(hand, refl, bands);
                    // The game writes its orb manager's own place and scale from the creature's bounds whenever it lays the
                    // creature out (NCreature.SetOrbManagerPosition), and that write lands after this frame's stage pass: the
                    // arc's fit is re-applied here, after every native process/tween step and before the draw, like the rail.
                    using (PortraitFrameTiming.Begin(PortraitFrameTiming.Orbs))
                        RefitOrbArcs(room, refl);
                }
            }
            catch (Exception error)
            {
                GD.PrintErr("Portrait pre-draw rail layout failed: ", error.Message);
            }
        }
        finally
        {
            PortraitFrameTiming.EndFrame();
        }
    }

    /// <summary>
    /// Lays every player's orb queue out again in the frame that is drawn: the game lays its own arc out from the creature's
    /// bounds after the stage pass (<c>NCreature.SetOrbManagerPosition</c> and <c>NOrbManager.TweenLayout</c>) and does so
    /// again whenever a slot, a channel or an evoke changes the queue, so the phone's arc has to be the queue's last writer.
    /// </summary>
    private void RefitOrbArcs(Control room, CombatReflection refl)
    {
        if (!_stage.HasOrbs || _lastBands is not { } bands || refl.RoomCreatureNodesField?.GetValue(room) is not IEnumerable creatures)
            return;
        foreach (object? item in creatures)
        {
            if (item is not Control creature || !GodotObject.IsInstanceValid(creature) || creature.IsQueuedForDeletion())
                continue;
            object? entity = refl.CreatureEntityProp?.GetValue(creature);
            if (entity == null || !refl.IsPlayerSide(entity) || !refl.CreatureHasOrbs(creature))
                continue;
            if (refl.CreatureOrbManagerProp?.GetValue(creature) is Control orbMgr)
                ApplyOrbArc(creature, orbMgr, refl, bands);
        }
    }

    public PortraitCombatLayout()
    {
        _hand = new PortraitHandLayout(EnsureReflection);
        _rail = new PortraitCombatRail(CaptureRoomForRailEntrance);
        _stage = new PortraitCombatStage(CaptureCreatureHud, _creatureHud.Queue);
    }

    public void Attach()
    {
        _hand.Attach();
        _stage.Attach();
        RenderingServer.FramePreDraw += OnFramePreDraw;
    }

    public void Detach()
    {
        RenderingServer.FramePreDraw -= OnFramePreDraw;
        _hand.Detach();
        _stage.Detach();
    }

    public const string CombatRoomTypeName = "MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom";
    private static CombatReflection? _activityReflection;

    /// <summary>
    /// Whether the game is animating something the player is waiting on, for the frame-rate governor's idle cap: the hand
    /// is moving (<see cref="PortraitHandLayout.HandMoving"/>), or a fight is running with player actions disabled —
    /// the enemy turn, a card resolving, or the turn-start draw (the game disables actions when End Turn is pressed
    /// and only enables them after the turn's start hooks). In landscape, only the native fight state applies.
    /// </summary>
    public static bool CombatAnimating
    {
        get
        {
            if (PortraitHandLayout.HandMoving) return true;
            CombatReflection refl = _activityReflection ??= new CombatReflection(typeof(NGame).Assembly);
            return refl.IsCombatInProgress() && refl.PlayerActionsDisabled();
        }
    }


    private readonly Dictionary<ulong, PortraitCombatRail.DesktopState> _rooms = new();
    private readonly Dictionary<ulong, Vector2> _orbDesktopScales = new();
    private PortraitCombatBands? _lastBands;
    private readonly PortraitHandLayout _hand;
    private readonly PortraitCombatRail _rail;
    private readonly PortraitCombatStage _stage;
    private readonly PortraitCreatureHud _creatureHud = new();
    private Control? _cachedUi;
    // The fight's one-shot resources, loaded ahead of the frames that would load them (see CombatAssetWarmup).
    private readonly CombatAssetWarmup _warmup = new();
    private CombatReflection? _reflection;


    // Rooms whose portrait pass threw: they are left as last placed and skipped, so a layout bug cannot take the app down.
    private readonly HashSet<ulong> _failedRooms = new();

    // The combat-rules FTUE, once found: the per-frame pass must not walk the scene tree for it. The game only creates it
    // while the save has not seen the tutorial, so the search almost always finds nothing; it runs once, and again only
    // after a matching node is added (NoteNodeAdded) or the remembered node dies.
    private const string FtueTypeName = "MegaCrit.Sts2.Core.Nodes.Ftue.NCombatRulesFtue";
    private ulong _ftueId;
    private bool _ftueSearched;

    public void OnProcessFrame(SceneTree tree, float capHeight)
    {
        if (!SafeAreaCanvas.IsPortrait || !GodotObject.IsInstanceValid(tree.Root))
            return;

        CombatReflection refl = EnsureReflection();
        Node? activeRoom = refl.GetActiveCombatRoom();
        if (activeRoom is not Control roomControl || !GodotObject.IsInstanceValid(roomControl) || !roomControl.IsInsideTree())
            return;
        if (_failedRooms.Contains(roomControl.GetInstanceId()))
            return;

        float canvasHeight = Math.Max(tree.Root.GetVisibleRect().Size.Y, SafeAreaCanvas.MinPortraitHeight);
        try
        {
            using (PortraitFrameTiming.Begin(PortraitFrameTiming.Combat))
                ApplyCombatRoomPortrait(roomControl, refl, capHeight, canvasHeight);
            using (PortraitFrameTiming.Begin(PortraitFrameTiming.Ftue))
                FitCombatFtueModal(tree.Root, portrait: true);
        }
        catch (Exception error)
        {
            FailRoom(roomControl, error);
        }
    }

    public void ApplyRoomImmediate(Control roomControl, float capHeight, float canvasHeight)
    {
        if (_failedRooms.Contains(roomControl.GetInstanceId()))
            return;
        CombatReflection refl = EnsureReflection();
        try
        {
            ApplyCombatRoomPortrait(roomControl, refl, capHeight, canvasHeight);
        }
        catch (Exception error)
        {
            FailRoom(roomControl, error);
        }
    }

    /// <summary>
    /// A layout exception leaves the room as last placed and skips it from then on; the first failure per room is logged
    /// with its stack. This keeps a portrait bug from crashing the whole app.
    /// </summary>
    private void FailRoom(Control room, Exception error)
    {
        ulong id = room.GetInstanceId();
        if (!_failedRooms.Add(id))
            return;
        GD.PrintErr($"Portrait combat layout failed; the room is left as last placed and will not be laid out again: {error}");
    }

    public void RestoreLandscape(Node root)
    {
        CombatReflection refl = EnsureReflection();

        FitCombatFtueModal(root, portrait: false);
        _hand.RestoreCardVisuals(refl);
        _rail.RestoreEndTurnFilters();
        _hand.RestoreSelectionUi();
        _stage.RestoreBackgroundBosses();
        _warmup.Stop();
        _hand.RestoreFonts();

        _creatureHud.Restore(refl, _stage, RestoreCreatureOrbs);
        _orbDesktopScales.Clear();

        foreach ((ulong roomId, PortraitCombatRail.DesktopState rail) in _rooms)
        {
            if (GodotObject.InstanceFromId(roomId) is not Control room || !GodotObject.IsInstanceValid(room))
                continue;
            _stage.RestoreRoom(room, refl);
            Control? ui = room.GetNodeOrNull<Control>("%CombatUi") ?? room.GetNodeOrNull<Control>("CombatUi");
            if (ui != null)
            {
                rail.RestoreUi(room, ui, refl);
                PortraitHandLayout.RefreshNativeTargets(ui, refl);
            }

            PortraitCombatRail.RestoreProceed(room, refl);
        }
        _rooms.Clear();
        _stage.EndSession();
        _failedRooms.Clear();
        _rail.EndSession();
        _hand.EndSession();
    }

    public void PruneDead()
    {
        var dead = new List<ulong>();
        foreach (ulong id in _rooms.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _rooms.Remove(id);
        dead.Clear();
        foreach (ulong id in _failedRooms)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _failedRooms.Remove(id);

        dead.Clear();
        foreach (ulong id in _orbDesktopScales.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _creatures.Remove(id);

        _hand.PruneDead(dead);

        _creatureHud.PruneDead(dead);

        _rail.PruneDead(dead);

        _stage.PruneDead(dead);
    }

    private void FitCombatFtueModal(Node root, bool portrait)
    {
        if (!GodotObject.IsInstanceValid(root)) return;
        Control? ftue = CachedFtue(root);
        if (ftue == null) return;
        if (portrait)
        {
            ftue.PivotOffset = ftue.Size * 0.5f;
            ftue.Scale = new Vector2(0.68f, 0.68f);
        }
        else
        {
            ftue.Scale = Vector2.One;
        }
    }

    /// <summary>
    /// The combat-rules FTUE, searched for once and then remembered. The game only creates it while the save has not seen
    /// the tutorial, so the search finds nothing for the rest of the game; walking the whole scene tree for it every frame
    /// was the only per-frame work in this layout that grew with the size of the scene. A new search only happens while
    /// nothing has been found yet, and once more after a remembered node dies.
    /// </summary>
    private Control? CachedFtue(Node root)
    {
        if (_ftueId != 0)
        {
            if (GodotObject.InstanceFromId(_ftueId) is Control known && GodotObject.IsInstanceValid(known))
                return known;
            _ftueId = 0;
            _ftueSearched = false;
        }
        if (_ftueSearched) return null;
        _ftueSearched = true;
        if (FindCombatRulesFtue(root) is not { } found) return null;
        _ftueId = found.GetInstanceId();
        return found;
    }

    /// <summary>Called for every node the scene tree adds: a combat-rules FTUE is the one node worth a fresh search.</summary>
    internal void NoteNodeAdded(string? typeName)
    {
        if (typeName != FtueTypeName)
            return;
        _ftueId = 0;
        _ftueSearched = false;
    }

    private static Control? FindCombatRulesFtue(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is Control c && c.GetType().FullName == FtueTypeName)
                return c;
            if (FindCombatRulesFtue(child) is { } nested)
                return nested;
        }
        return null;
    }

    private void ApplyCombatRoomPortrait(
        Control room,
        CombatReflection refl,
        float capHeight,
        float canvasHeight)
    {
        ulong roomId = room.GetInstanceId();
        EnsureRoomState(room, refl);

        Control? ui = Remembered(room, "CombatUi", ref _cachedUi);
        if (ui != null)
        {
            _rail.WireCombatUi(ui, roomId);
            ui.AnchorLeft = 0f;
            ui.AnchorTop = 0f;
            ui.AnchorRight = 1f;
            ui.AnchorBottom = 1f;
            ui.OffsetLeft = 0f;
            ui.OffsetTop = 0f;
            ui.OffsetRight = 0f;
            ui.OffsetBottom = 0f;
        }

        Control? hand = ui != null ? _hand.RememberHand(ui) : null;
        // Load the fight's lazily loaded resources (card frames, power icons, orb assets, sounds) while the room is still on
        // its entrance, through the game's own threaded loader, so no card visual or status pays for them later.
        _warmup.Tick(room, hand, refl);

        // The bands depend only on the canvas and the run bars, so the rail, the hand and the stage all stay exactly
        // where they are whatever the hand's size: drawing or playing a card cannot move End Turn or the creatures.
        float stageTopY = capHeight + PortraitBands.RunContentTop;
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, stageTopY);
        _lastBands = bands;
        _hand.SetSelectionBounds(bands, canvasHeight);
        // Native layout still updates input and navigation. Its geometry is disabled before any desktop target is set;
        // the portrait writer applies its targets before draw, after the hand's membership and touch state settle.
        PortraitHudLayout hud = PortraitCombatMath.ComputeHudLayout(canvasHeight, bands);

        if (ui != null)
            _rail.Apply(ui, room, hand, refl, hud);
        if (hand != null)
            using (PortraitFrameTiming.Begin(PortraitFrameTiming.Selection))
                _hand.LayoutHandSelectionPortrait(hand, refl, bands, canvasHeight);
        _creatureHud.BeginFrame();
        using (PortraitFrameTiming.Begin(PortraitFrameTiming.Stage))
            _stage.Apply(room, refl, bands, canvasHeight);
        using (PortraitFrameTiming.Begin(PortraitFrameTiming.Labels))
            _creatureHud.Apply(room, refl, _stage);
    }

    private void EnsureRoomState(Control room, CombatReflection refl)
    {
        ulong roomId = room.GetInstanceId();
        if (_rooms.ContainsKey(roomId))
            return;
        _stage.CaptureRoom(room);
        _rooms.Add(roomId, new PortraitCombatRail.DesktopState(room, refl));
    }

    private CombatReflection CaptureRoomForRailEntrance(Control room)
    {
        CombatReflection refl = EnsureReflection();
        EnsureRoomState(room, refl);
        return refl;
    }

    /// <summary>
    /// A node found by its unique name, remembered while it stays in the tree. A <c>%</c> path resolves through the
    /// owner's unique-name map and converts the path twice on every call; this pass asks for the same two nodes every
    /// frame, and a new room frees the old ones, so the remembered node revalidates to nothing and the search runs again.
    /// </summary>
    private static Control? Remembered(Control parent, string unique, ref Control? cached)
    {
        if (cached is { } known && GodotObject.IsInstanceValid(known) && known.IsInsideTree())
            return known;
        cached = parent.GetNodeOrNull<Control>("%" + unique) ?? parent.GetNodeOrNull<Control>(unique);
        return cached;
    }

    // The queue's own bookkeeping, kept on the nodes it describes: a fought room's orbs are freed with it, so none of it
    // outlives the fight and landscape has nothing to restore but the touched node properties.
    private const string OrbCountMeta = "phone_orb_count";
    private const string OrbLabelWidthMeta = "phone_orb_label_width";
    private const string OrbTweenMeta = "phone_orb_tween";
    private const string OrbTargetMeta = "phone_orb_target";
    private const string OrbContainerHomeMeta = "phone_orb_container_home";
    private const string OrbBoundsHomeMeta = "phone_orb_bounds_home";
    private const string OrbBoundsPivotMeta = "phone_orb_bounds_pivot";
    private const string OrbLabelZMeta = "phone_orb_label_z";
    private const string OrbLabelMouseMeta = "phone_orb_label_mouse";
    private const string OrbContainerRefMeta = "phone_orb_container_ref";
    private const string OrbBoundsRefMeta = "phone_orb_bounds_ref";

    // The one list every queue walk fills: the layout and the gate both use it, every frame of an orb fight.
    private static readonly List<Control> OrbScratch = new();
    // The places the running retarget tween is heading to, in the container's own coordinates: one entry per orb.
    private static Vector2[] _orbTargetScratch = new Vector2[16];

    /// <summary>
    /// Places one player's orb queue on the phone. The game's own layout is a desktop arc whose radius is a function of the
    /// queue's capacity, so it cannot fit a portrait stage at a readable size: the queue is re-anchored and re-spaced here,
    /// its native order (index 0 rightmost/front, the last leftmost/back), art, value labels and evoke effects untouched.
    /// One to three orbs keep the game's own arc, tightened to the phone; four take one wide arc that leaves no gap between
    /// neighbours; five to ten take one compact arc above the owner whose pitch is the phone's overlapping spacing, widened
    /// by the widest value label and narrowed by the safe width or an enemy lane. Tall phones keep the arc under the relic
    /// band (the stage top); at 9:16 it may rise over the relic band but never the stone header. Every retarget is one
    /// 0.45 s sine tween from the orbs' current places, so a channel, evoke, overflow or capacity change glides.
    /// </summary>
    private void ApplyOrbArc(Control creature, Control orbMgr, CombatReflection refl, PortraitCombatBands bands)
    {
        if (!GodotObject.IsInstanceValid(orbMgr) || orbMgr.IsQueuedForDeletion() || !orbMgr.IsInsideTree())
            return;
        // The game's own list, not the child count: an evoked orb fades for a quarter second as a child of the manager.
        int count = OrbQueue(orbMgr, refl, OrbScratch);
        if (count == 0)
            return;
        if (OrbContainerOf(orbMgr) is not { } container)
            return;
        if (_stage.HitboxOf(creature) is not { } hitbox || !GodotObject.IsInstanceValid(hitbox))
            return;
        Rect2 body = hitbox.GetGlobalRect();
        if (body.Size.Y < 1f)
            return;

        // One world size for the orbs whatever scale the body is drawn at: the manager hangs off the creature, so its own
        // scale is the world size divided by the creature's (the clamp keeps a tiny boss-fight actor's orbs from growing
        // past the accepted look). The game rewrites it from the visuals, so it is re-asserted here every pass.
        float parentScale = 1f;
        if (orbMgr.GetParent() is Control parent && GodotObject.IsInstanceValid(parent))
            parentScale = MathF.Max(0.0001f, parent.GetGlobalTransform().Scale.Y);
        float localScale = Math.Clamp(PortraitOrbMath.OrbWorldScale / parentScale, 0.6f, 2.4f);
        float orbWorld = localScale * parentScale;
        var scale = new Vector2(localScale, localScale);
        if ((orbMgr.Scale - scale).LengthSquared() > 0.0001f)
            orbMgr.Scale = scale;

        // The orbit line: below the relic band on a tall phone, the stone header's bottom at 9:16 (decision 10, which lets
        // the short canvas's arc rise over the relic strip rather than over the hero).
        float capHeight = MathF.Max(0f, bands.StageTopY - PortraitBands.RunContentTop);
        float orbitTop = bands.Compact ? PortraitBands.HeaderBottom(capHeight) : PortraitBands.RelicBandBottom(capHeight);

        // The label walk is not per frame, but it is also not only on a slot count change: an evoke replaces one orb with
        // another at the same slot, and the new orb's own labels have to be raised and measured too. An orb that is here
        // without a target is the one that just arrived.
        int knownCount = orbMgr.HasMeta(OrbCountMeta) ? orbMgr.GetMeta(OrbCountMeta).AsInt32() : -1;
        float labelWidth = orbMgr.HasMeta(OrbLabelWidthMeta) ? orbMgr.GetMeta(OrbLabelWidthMeta).AsSingle() : 0f;
        bool relabel = knownCount != count;
        for (int i = 0; i < count && !relabel; i++)
            relabel = !OrbScratch[i].HasMeta(OrbTargetMeta);
        if (relabel)
        {
            labelWidth = PrepareOrbArcLabels(count);
            orbMgr.SetMeta(OrbCountMeta, count);
            orbMgr.SetMeta(OrbLabelWidthMeta, labelWidth);
        }

        PortraitOrbArcPlan plan = PlanOrbArc(count, body, bands, orbitTop, labelWidth, orbWorld);
        if (!plan.IsSingle && OrbArcMeetsEnemy(plan))
            plan = PlanOrbArc(count, body, bands, orbitTop, labelWidth, orbWorld, single: true);
        else if (plan.IsSingle && count > 5 &&
                 OrbArcEnemyLane(plan, body.GetCenter().X, out float laneLeft, out float laneRight))
            plan = PlanOrbArc(count, body, bands, orbitTop, labelWidth, orbWorld, single: true, laneLeft, laneRight);

        // The container is pinned on the canvas, joined to the body only through the owner's own line: the game rewrites the
        // manager's place from the creature's bounds, which would carry the queue with it. The origin does not depend on the
        // count or the arc's shape, so a slot arriving only moves the orbs' own targets — the container never jumps.
        if (!container.HasMeta(OrbContainerHomeMeta))
            container.SetMeta(OrbContainerHomeMeta, container.Position);
        float anchorX = Math.Clamp(body.GetCenter().X, PortraitOrbMath.SingleArcSide,
            SafeAreaCanvas.PortraitWidth - PortraitOrbMath.SingleArcSide);
        var anchor = new Vector2(anchorX, body.Position.Y);
        if ((container.GlobalPosition - anchor).LengthSquared() > 0.25f)
            container.GlobalPosition = anchor;

        float touchLocal = plan.TouchWorld / MathF.Max(0.01f, orbWorld);
        if (_orbTargetScratch.Length < count)
            _orbTargetScratch = new Vector2[count];
        Transform2D inverse = container.GetGlobalTransform().AffineInverse();
        bool retarget = false;
        for (int i = 0; i < count; i++)
        {
            Control orb = OrbScratch[i];
            if (!GodotObject.IsInstanceValid(orb) || orb.IsQueuedForDeletion())
            {
                _orbTargetScratch[i] = Vector2.Zero;
                continue;
            }
            SetOrbTouchTarget(orb, touchLocal);
            (float worldX, float worldY) = plan.Position(i);
            Vector2 target = inverse * new Vector2(worldX, worldY);
            _orbTargetScratch[i] = target;
            Vector2 known = orb.HasMeta(OrbTargetMeta) ? orb.GetMeta(OrbTargetMeta).AsVector2() : new Vector2(
                float.PositiveInfinity, float.PositiveInfinity);
            if (MathF.Abs(target.X - known.X) <= 0.25f && MathF.Abs(target.Y - known.Y) <= 0.25f)
                continue;
            orb.SetMeta(OrbTargetMeta, target);
            retarget = true;
        }

        // The game starts its own tween on every queue change (and hands the manager a new one): the first frame it is
        // seen, it is killed and replaced by this one, from the orbs' current places — never a frame at the game's places.
        Tween? running = refl.OrbManagerTweenField?.GetValue(orbMgr) as Tween;
        if (running != null && !GodotObject.IsInstanceValid(running))
            running = null;
        ulong knownTween = orbMgr.HasMeta(OrbTweenMeta) ? (ulong)orbMgr.GetMeta(OrbTweenMeta).AsInt64() : 0UL;
        ulong runningId = running != null ? running.GetInstanceId() : 0UL;
        bool takeover = runningId != 0UL && runningId != knownTween;
        if (!retarget && !takeover)
            return;
        if (running != null && running.IsRunning())
            running.Kill();
        Tween tween = orbMgr.CreateTween().SetParallel();
        for (int i = 0; i < count; i++)
        {
            if (!GodotObject.IsInstanceValid(OrbScratch[i]) || OrbScratch[i].IsQueuedForDeletion())
                continue;
            tween.TweenProperty(OrbScratch[i], "position", _orbTargetScratch[i], PortraitOrbMath.TweenSeconds)
                .SetEase(Tween.EaseType.InOut).SetTrans(Tween.TransitionType.Sine);
        }
        refl.OrbManagerTweenField?.SetValue(orbMgr, tween);
        orbMgr.SetMeta(OrbTweenMeta, (long)tween.GetInstanceId());
    }

    /// <summary>One plan for this queue: the owner's box, the bars, the orbit line and the widest value.</summary>
    private static PortraitOrbArcPlan PlanOrbArc(int count, Rect2 body, PortraitCombatBands bands, float orbitTop,
        float labelWidth, float orbWorld, bool single = false, float laneLeft = 0f, float laneRight = 0f) =>
        PortraitOrbMath.ComputeOrbArc(count, body.GetCenter().X, body.Position.Y, SafeAreaCanvas.PortraitWidth,
            orbitTop, bands.RailTopY, labelWidth, single, laneLeft, laneRight, orbWorld);

    /// <summary>
    /// Whether the game's own arc would put an icon on an enemy: the arc then falls back to the single phone row, as the
    /// phone's arc must not draw through a body, its health bar or its intents. The probe keeps a margin past the icon and
    /// covers the bar above the body; it is measured against the stage pass's enemies, which stand where they are drawn.
    /// </summary>
    private bool OrbArcMeetsEnemy(in PortraitOrbArcPlan plan)
    {
        if (_stage.Enemies.IsEmpty)
            return false;
        // The probe measures the whole square the finger hits (the touch region, never the smaller art inside it).
        float reach = plan.BoxHalf + PortraitOrbMath.CanvasEdgeMargin;
        for (int i = 0; i < plan.Count; i++)
        {
            (float x, float y) = plan.Position(i);
            foreach (Control enemy in _stage.Enemies)
            {
                if (!GodotObject.IsInstanceValid(enemy) || !enemy.IsVisibleInTree() ||
                    _stage.HitboxOf(enemy) is not { } hitbox || !GodotObject.IsInstanceValid(hitbox))
                    continue;
                Rect2 box = hitbox.GetGlobalRect();
                // Health bars and intents stand above the body: the probe grows upward so the arc clears those too.
                box = new Rect2(box.Position - new Vector2(10f, 48f), box.Size + new Vector2(20f, 58f));
                if (x + reach > box.Position.X && box.End.X > x - reach &&
                    y + reach > box.Position.Y && box.End.Y > y - reach)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The lane left of the nearest enemy that the queue's vertical band reaches, as a range for orb centres: a dense queue
    /// next to a boss is squeezed into the wall beside it instead of drawn over its body and bar (the phone's answer to a
    /// boss occupying the stage's middle). False when no enemy stands to the right or the lane cannot hold the queue.
    /// </summary>
    private bool OrbArcEnemyLane(in PortraitOrbArcPlan plan, float ownerCenterX, out float laneLeft, out float laneRight)
    {
        laneLeft = 0f;
        laneRight = 0f;
        if (_stage.Enemies.IsEmpty)
            return false;
        // The lane's own clearance covers the whole touch square plus 10 units, so no part of an orb reaches the body.
        float extent = plan.BoxHalf + 10f;
        laneLeft = extent;
        laneRight = SafeAreaCanvas.PortraitWidth - extent;
        bool found = false;
        foreach (Control enemy in _stage.Enemies)
        {
            if (!GodotObject.IsInstanceValid(enemy) || !enemy.IsVisibleInTree() ||
                _stage.HitboxOf(enemy) is not { } hitbox || !GodotObject.IsInstanceValid(hitbox))
                continue;
            Rect2 box = hitbox.GetGlobalRect();
            if (box.Position.X <= ownerCenterX)
                continue;
            if (box.Position.Y > plan.CenterY + extent || box.End.Y < plan.CenterY - plan.Depth - extent)
                continue;
            laneRight = MathF.Min(laneRight, box.Position.X - extent);
            found = true;
        }
        return found && laneRight > laneLeft + 1f;
    }

    /// <summary>
    /// The value labels, once per slot change: they draw above every orb's art (so a dense queue's numbers stay legible over
    /// the next orb), never take a press, and are raised to the ten-point floor when the orb's own scale leaves them under
    /// it. Returns the widest visible label's drawn width, which widens the queue's pitch.
    /// </summary>
    private float PrepareOrbArcLabels(int count)
    {
        float widest = 0f;
        for (int i = 0; i < count; i++)
        {
            Control orb = OrbScratch[i];
            if (!GodotObject.IsInstanceValid(orb))
                continue;
            if ((orb.GetNodeOrNull<Control>("%LabelContainer") ?? orb.GetNodeOrNull<Control>("LabelContainer")) is not
                { } labels || !GodotObject.IsInstanceValid(labels))
                continue;
            if (!labels.HasMeta(OrbLabelZMeta))
            {
                labels.SetMeta(OrbLabelZMeta, labels.ZIndex);
                labels.SetMeta(OrbLabelMouseMeta, (int)labels.MouseFilter);
            }
            if (labels.ZIndex != 2)
                labels.ZIndex = 2;
            if (labels.MouseFilter != Control.MouseFilterEnum.Ignore)
                labels.MouseFilter = Control.MouseFilterEnum.Ignore;
            int children = labels.GetChildCount();
            for (int c = 0; c < children; c++)
            {
                if (labels.GetChild(c) is not Label label || !label.Visible || string.IsNullOrWhiteSpace(label.Text))
                    continue;
                float drawnScale = MathF.Max(0.01f, label.GetGlobalTransform().Scale.Y);
                EnlargeLabelFont(label,
                    (int)MathF.Ceiling(PortraitOrbMath.ValueWorldHeight / drawnScale));
                widest = MathF.Max(widest, label.GetMinimumSize().X * label.GetGlobalTransform().Scale.X);
            }
        }
        return widest;
    }

    /// <summary>
    /// An orb's touch region, kept at its previous world size while the art scales with the body: the invisible rect is the
    /// finger's target (the orb's own art is smaller in a dense queue), so neighbours overlap like the game's own hover
    /// regions rather than stealing each other's presses.
    /// </summary>
    private static void SetOrbTouchTarget(Control orb, float localSize)
    {
        if (OrbBoundsOf(orb) is not { } bounds)
            return;
        if (!bounds.HasMeta(OrbBoundsHomeMeta))
        {
            bounds.SetMeta(OrbBoundsHomeMeta, new Rect2(bounds.Position, bounds.Size));
            bounds.SetMeta(OrbBoundsPivotMeta, bounds.PivotOffset);
        }
        if (MathF.Abs(bounds.Size.X - localSize) <= 0.05f && MathF.Abs(bounds.Size.Y - localSize) <= 0.05f &&
            MathF.Abs(bounds.Position.X + localSize * 0.5f) <= 0.05f)
            return;
        bounds.Position = new Vector2(-localSize * 0.5f, -localSize * 0.5f);
        bounds.Size = new Vector2(localSize, localSize);
        bounds.PivotOffset = bounds.Size * 0.5f;
    }

    /// <summary>The manager's own orb container, resolved once per fight: the queue's places are relative to it.</summary>
    private static Control? OrbContainerOf(Control orbMgr)
    {
        if (orbMgr.HasMeta(OrbContainerRefMeta) &&
            orbMgr.GetMeta(OrbContainerRefMeta).AsGodotObject() is Control cached && GodotObject.IsInstanceValid(cached))
            return cached;
        Control? found = orbMgr.GetNodeOrNull<Control>("%Orbs");
        if (found != null && GodotObject.IsInstanceValid(found))
            orbMgr.SetMeta(OrbContainerRefMeta, found);
        return found;
    }

    /// <summary>An orb's invisible touch region, resolved once per orb rather than by path every frame.</summary>
    private static Control? OrbBoundsOf(Control orb)
    {
        if (orb.HasMeta(OrbBoundsRefMeta) &&
            orb.GetMeta(OrbBoundsRefMeta).AsGodotObject() is Control cached && GodotObject.IsInstanceValid(cached))
            return cached;
        Control? found = orb.GetNodeOrNull<Control>("Bounds");
        if (found != null && GodotObject.IsInstanceValid(found))
            orb.SetMeta(OrbBoundsRefMeta, found);
        return found;
    }

    /// <summary>
    /// Puts the queue back the way the game draws it in landscape: the labels' own z-index and mouse filter, the orbs' touch
    /// regions, the container's own place and the bookkeeping this layout kept. The manager's scale and place are restored
    /// by the per-creature restore pass.
    /// </summary>
    private static void RestoreOrbArcLandscape(Control orbMgr, CombatReflection refl)
    {
        if (!GodotObject.IsInstanceValid(orbMgr) || orbMgr.IsQueuedForDeletion())
            return;
        if (refl.OrbManagerTweenField?.GetValue(orbMgr) is Tween running && GodotObject.IsInstanceValid(running))
        {
            ulong known = orbMgr.HasMeta(OrbTweenMeta) ? (ulong)orbMgr.GetMeta(OrbTweenMeta).AsInt64() : 0UL;
            if (known != 0UL && running.GetInstanceId() == known && running.IsRunning())
                running.Kill();
        }
        if (OrbContainerOf(orbMgr) is { } container && container.HasMeta(OrbContainerHomeMeta))
        {
            container.Position = container.GetMeta(OrbContainerHomeMeta).AsVector2();
            container.RemoveMeta(OrbContainerHomeMeta);
        }
        int count = OrbQueue(orbMgr, refl, OrbScratch);
        for (int i = 0; i < count; i++)
        {
            Control orb = OrbScratch[i];
            if (!GodotObject.IsInstanceValid(orb))
                continue;
            if (OrbBoundsOf(orb) is { } bounds && bounds.HasMeta(OrbBoundsHomeMeta))
            {
                var home = bounds.GetMeta(OrbBoundsHomeMeta).AsRect2();
                bounds.Position = home.Position;
                bounds.Size = home.Size;
                bounds.PivotOffset = bounds.GetMeta(OrbBoundsPivotMeta).AsVector2();
                bounds.RemoveMeta(OrbBoundsHomeMeta);
                bounds.RemoveMeta(OrbBoundsPivotMeta);
            }
            orb.RemoveMeta(OrbTargetMeta);
            if ((orb.GetNodeOrNull<Control>("%LabelContainer") ?? orb.GetNodeOrNull<Control>("LabelContainer")) is not
                { } labels || !GodotObject.IsInstanceValid(labels) || !labels.HasMeta(OrbLabelZMeta))
                continue;
            labels.ZIndex = labels.GetMeta(OrbLabelZMeta).AsInt32();
            labels.MouseFilter = (Control.MouseFilterEnum)labels.GetMeta(OrbLabelMouseMeta).AsInt32();
            labels.RemoveMeta(OrbLabelZMeta);
            labels.RemoveMeta(OrbLabelMouseMeta);
        }
        orbMgr.RemoveMeta(OrbTweenMeta);
        orbMgr.RemoveMeta(OrbCountMeta);
        orbMgr.RemoveMeta(OrbLabelWidthMeta);
    }

    /// <summary>
    /// One manager's queue, in the game's own order: the list it lays out from. An evoked orb keeps its node for the fade
    /// but leaves this list at once, so a queue that is being replaced is never measured with the orb that is leaving.
    /// </summary>
    private static int OrbQueue(Control orbMgr, CombatReflection refl, List<Control> orbs)
    {
        orbs.Clear();
        if (refl.OrbListField?.GetValue(orbMgr) is System.Collections.IList queue)
        {
            for (int i = 0; i < queue.Count; i++)
            {
                if (queue[i] is Control orb && GodotObject.IsInstanceValid(orb) && !orb.IsQueuedForDeletion())
                    orbs.Add(orb);
            }
            return orbs.Count;
        }
        CollectOrbs(orbMgr, orbs);
        return orbs.Count;
    }

    private static void CollectOrbs(Node node, List<Control> orbs)
    {
        int children = node.GetChildCount();
        for (int i = 0; i < children; i++)
        {
            Node child = node.GetChild(i);
            if (child is Control control && control.GetType().Name == "NOrb")
                orbs.Add(control);
            else
                CollectOrbs(child, orbs);
        }
    }

    private void CaptureCreatureHud(Control creature, Control? hitbox, CombatReflection refl)
    {
        if (refl.CreatureOrbManagerProp?.GetValue(creature) is Control orbMgr)
            _orbDesktopScales.Add(creature.GetInstanceId(), orbMgr.Scale);
        _creatureHud.Capture(creature, hitbox, refl);
    }

    private void RestoreCreatureOrbs(Control creature, CombatReflection refl)
    {
        if (refl.CreatureOrbManagerProp?.GetValue(creature) is not Control orbMgr)
            return;
        RestoreOrbArcLandscape(orbMgr, refl);
        orbMgr.Scale = _orbDesktopScales.GetValueOrDefault(creature.GetInstanceId(), Vector2.One);
    }

    private CombatReflection EnsureReflection() => _reflection ??= new CombatReflection(typeof(NGame).Assembly);

    internal sealed class CombatReflection
    {
        private readonly PropertyInfo? _combatRoomInstanceProp;
        private readonly PropertyInfo? _combatMgrInstanceProp;
        private readonly PropertyInfo? _combatMgrInProgressProp;
        private readonly PropertyInfo? _combatMgrStartingProp;
        private readonly FieldInfo? _combatMgrBaseHandDrawField;

        public readonly FieldInfo? RoomCreatureNodesField;
        public readonly MethodInfo? RoomAdjustScaleMethod;
        public readonly PropertyInfo? RoomEncounterSlotsProp;
        public readonly PropertyInfo? RoomCombatVfxContainerProp;
        public readonly PropertyInfo? RoomBackgroundProp;

        public readonly FieldInfo? EnergyCounterField;
        public readonly FieldInfo? StarCounterField;
        public readonly FieldInfo? FoolCombatUiField;
        public readonly FieldInfo? EnergyCounterPlayerField;
        public readonly PropertyInfo? PlayerCombatStateProp;
        public readonly PropertyInfo? PlayerCombatTurnProp;
        public readonly PropertyInfo? PlayerCombatEnergyProp;
        public readonly PropertyInfo? PlayerCombatStarsProp;
        public readonly PropertyInfo? CombatMgrActionsDisabledProp;
        public readonly FieldInfo? HandCombatStateField;
        public readonly MethodInfo? HandDrawHookMethod;
        public readonly MethodInfo? CardPileGetMethod;
        public readonly PropertyInfo? CardPileCardsProp;
        public readonly PropertyInfo? CardKeywordsProp;
        public readonly object? InnateKeyword;

        public readonly PropertyInfo? HandInCardPlayProp;
        public readonly FieldInfo? HandDraggedIndexField;
        public readonly PropertyInfo? HandFocusedHolderProp;
        public readonly PropertyInfo? HandInCardSelectionProp;
        public readonly MethodInfo? HandForceRefreshMethod;
        public readonly FieldInfo? HandSelectedCardTweenField;

        public readonly PropertyInfo? CardHolderCardNodeProp;
        public readonly FieldInfo? HolderTargetPosField;
        public readonly FieldInfo? HolderTargetScaleField;
        public readonly FieldInfo? HolderTargetAngleField;
        public readonly MethodInfo? HolderSetTargetPosMethod;
        public readonly MethodInfo? HolderSetTargetScaleMethod;
        public readonly MethodInfo? HolderSetTargetAngleMethod;
        public readonly PropertyInfo? HolderGlowGoldProp;
        public readonly PropertyInfo? HolderGlowRedProp;
        public readonly FieldInfo? SelectedHolderTweenField;

        public readonly PropertyInfo? CardBodyProp;
        public readonly PropertyInfo? CardHighlightProp;
        public readonly PropertyInfo? CardModelProp;
        public readonly PropertyInfo? CardDisplayingPileProp;
        public readonly FieldInfo? CardEnergyLabelField;
        public readonly FieldInfo? CardUnplayableEnergyIconField;
        public readonly MethodInfo? CardUpdateEnergyCostMethod;
        public readonly MethodInfo? CardModelCanPlayMethod;
        public readonly FieldInfo? HighlightTweenField;

        // The combat asset warmup's paths: a card model's pool frame material and energy icon, its banner material and the
        // atlas sprites it draws, and the big/atlas icons of a creature's powers.
        private readonly PropertyInfo? _cardPoolProp, _poolFrameMaterialPathProp, _poolEnergyIconPathProp;
        private readonly PropertyInfo? _bannerMaterialPathProp, _portraitPathProp, _framePathProp, _portraitBorderPathProp,
            _bannerTexturePathProp;
        private readonly PropertyInfo? _creaturePowersProp, _powerBigIconPathProp, _powerPackedIconPathProp;

        public readonly FieldInfo? EndTurnStateField;
        public readonly FieldInfo? EndTurnPosTweenField;
        public readonly PropertyInfo? EndTurnShowPosProp;
        public readonly PropertyInfo? EndTurnHidePosProp;

        public readonly FieldInfo? ConfirmShowPosField;
        public readonly FieldInfo? ConfirmHidePosField;
        public readonly FieldInfo? ConfirmMoveTweenField;

        public readonly FieldInfo? PingStateField;
        public readonly FieldInfo? PingPosTweenField;
        public readonly PropertyInfo? PingShowPosProp;
        public readonly PropertyInfo? PingHidePosProp;

        public readonly PropertyInfo? ButtonIsEnabledProp;
        public readonly FieldInfo? ProceedAnimTweenField;
        public readonly PropertyInfo? ProceedShowPosProp;
        public readonly PropertyInfo? ProceedHidePosProp;

        public readonly FieldInfo? PileShowPosField;
        public readonly FieldInfo? PileHidePosField;
        public readonly FieldInfo? PilePosTweenField;
        public readonly MethodInfo? ExhaustSetAnimPositionsMethod;

        public readonly PropertyInfo? CreatureEntityProp;
        public readonly PropertyInfo? CreatureVisualsProp;
        public readonly PropertyInfo? CreatureOrbManagerProp;
        // The orb manager's own queue and the tween it lays the queue out with: the launcher re-places the queue and has to
        // see when the game starts a new layout of its own.
        public readonly FieldInfo? OrbListField;
        public readonly FieldInfo? OrbManagerTweenField;
        private readonly MethodInfo? _creatureUpdateBoundsMethod;
        private readonly MethodInfo? _stateDisplaySetBoundsMethod;
        private readonly MethodInfo? _powerContainerSetBoundsMethod;
        private bool _stateDisplayFailureLogged;
        public readonly PropertyInfo? EntityIsPlayerProp;
        private readonly PropertyInfo? _entitySideProp;
        private readonly PropertyInfo? _entityPlayerProp;
        public readonly PropertyInfo? EntityMonsterProp;
        public readonly PropertyInfo? CreatureSlotNameProp;
        private readonly PropertyInfo? _entityCombatStateProp;
        private readonly PropertyInfo? _combatStateRunStateProp;
        private readonly PropertyInfo? _combatStateEncounterProp;
        private readonly MethodInfo? _encounterCustomBackgroundGetter;
        private readonly PropertyInfo? _runStateActProp;
        private readonly PropertyInfo? _monsterBarReductionProp;
        private readonly PropertyInfo? _playerCombatStateProp;
        private readonly PropertyInfo? _orbQueueProp;
        private readonly PropertyInfo? _orbCapacityProp;
        private readonly PropertyInfo? _visualsBoundsProp;

        public CombatReflection(Assembly sts2)
        {
            const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags StaticAll = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            Type? combatRoomType = sts2.GetType(CombatRoomTypeName);
            _combatRoomInstanceProp = combatRoomType?.GetProperty("Instance", StaticAll);
            RoomCreatureNodesField = FindField(combatRoomType, "_creatureNodes");
            RoomAdjustScaleMethod = combatRoomType?.GetMethod("AdjustCreatureScaleForAspectRatio", InstAll);
            RoomEncounterSlotsProp = FindProperty(combatRoomType, "EncounterSlots");
            RoomCombatVfxContainerProp = FindProperty(combatRoomType, "CombatVfxContainer");
            RoomBackgroundProp = FindProperty(combatRoomType, "Background");

            Type? combatMgrType = sts2.GetType("MegaCrit.Sts2.Core.Combat.CombatManager");
            _combatMgrInstanceProp = combatMgrType?.GetProperty("Instance", StaticAll);
            _combatMgrInProgressProp = combatMgrType?.GetProperty("IsInProgress", InstAll);
            _combatMgrStartingProp = combatMgrType?.GetProperty("IsStarting", InstAll);
            // CombatManager.baseHandDrawCount: the hand size a turn starts with, before any draw modifier.
            _combatMgrBaseHandDrawField = combatMgrType?.GetField("baseHandDrawCount", StaticAll);

            Type? combatUiType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NCombatUi");
            EnergyCounterField = FindField(combatUiType, "_energyCounter");
            StarCounterField = FindField(combatUiType, "_starCounter");
            FoolCombatUiField = FindField(combatUiType, "_foolCombatUi");

            // The energy counter holds the player (public `_player`); its combat state carries the turn number, energy and
            // stars that decide what is playable. All public members, five reflected reads a pass.
            Type? energyCounterType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NEnergyCounter");
            EnergyCounterPlayerField = FindField(energyCounterType, "_player");
            Type? playerModelType = sts2.GetType("MegaCrit.Sts2.Core.Entities.Players.Player");
            PlayerCombatStateProp = FindProperty(playerModelType, "PlayerCombatState");
            Type? playerCombatStateType = sts2.GetType("MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState");
            PlayerCombatTurnProp = FindProperty(playerCombatStateType, "TurnNumber");
            PlayerCombatEnergyProp = FindProperty(playerCombatStateType, "Energy");
            PlayerCombatStarsProp = FindProperty(playerCombatStateType, "Stars");
            CombatMgrActionsDisabledProp = FindProperty(combatMgrType, "PlayerActionsDisabled");

            // The turn's hand draw is a local of CombatManager's turn-start state machine, but the game computes it with
            // this pure query (audited: no ModifyHandDraw override mutates anything), so it can be asked again for the
            // exact count instead of assuming the base size.
            Type? handDrawHookType = sts2.GetType("MegaCrit.Sts2.Core.Hooks.Hook");
            HandDrawHookMethod = handDrawHookType?.GetMethod("ModifyHandDraw", StaticAll);
            Type? cardPileType = sts2.GetType("MegaCrit.Sts2.Core.Entities.Cards.CardPile");
            CardPileGetMethod = null;
            if (cardPileType != null)
            {
                foreach (MethodInfo candidate in cardPileType.GetMethods(StaticAll))
                {
                    ParameterInfo[] parameters = candidate.GetParameters();
                    if (candidate.Name == "Get" && parameters.Length == 2 && parameters[0].ParameterType.IsEnum &&
                        parameters[1].ParameterType.Name == "Player")
                    {
                        CardPileGetMethod = candidate;
                        break;
                    }
                }
            }
            CardPileCardsProp = FindProperty(cardPileType, "Cards");
            Type? cardModelTypeForKeywords = sts2.GetType("MegaCrit.Sts2.Core.Models.CardModel");
            CardKeywordsProp = FindProperty(cardModelTypeForKeywords, "Keywords");
            InnateKeyword = sts2.GetType("MegaCrit.Sts2.Core.Entities.Cards.CardKeyword") is { } keywordType &&
                Enum.TryParse(keywordType, "Innate", out object? innate) ? innate : null;

            Type? handType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand");
            HandInCardPlayProp = handType?.GetProperty("InCardPlay", InstAll);
            HandDraggedIndexField = FindField(handType, "_draggedHolderIndex");
            HandFocusedHolderProp = handType?.GetProperty("FocusedHolder", InstAll);
            HandInCardSelectionProp = handType?.GetProperty("IsInCardSelection", InstAll);
            HandForceRefreshMethod = handType?.GetMethod("ForceRefreshCardIndices", InstAll);
            HandSelectedCardTweenField = FindField(handType, "_selectedCardScaleTween");
            HandCombatStateField = FindField(handType, "_combatState");

            Type? handHolderType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder");
            CardHolderCardNodeProp = FindProperty(handHolderType, "CardNode");
            HolderTargetPosField = FindField(handHolderType, "_targetPosition");
            HolderTargetScaleField = FindField(handHolderType, "_targetScale");
            HolderTargetAngleField = FindField(handHolderType, "_targetAngle");
            HolderSetTargetPosMethod = handHolderType?.GetMethod("SetTargetPosition", InstAll);
            HolderSetTargetScaleMethod = handHolderType?.GetMethod("SetTargetScale", InstAll);
            HolderSetTargetAngleMethod = handHolderType?.GetMethod("SetTargetAngle", InstAll);
            HolderGlowGoldProp = FindProperty(handHolderType, "ShouldGlowGold");
            HolderGlowRedProp = FindProperty(handHolderType, "ShouldGlowRed");
            Type? selectedHolderType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Cards.Holders.NSelectedHandCardHolder");
            SelectedHolderTweenField = FindField(selectedHolderType, "_tween");

            Type? cardType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Cards.NCard");
            CardBodyProp = FindProperty(cardType, "Body");
            CardHighlightProp = FindProperty(cardType, "CardHighlight");
            CardModelProp = FindProperty(cardType, "Model");
            CardDisplayingPileProp = FindProperty(cardType, "DisplayingPile");
            CardEnergyLabelField = FindField(cardType, "_energyLabel");
            CardUnplayableEnergyIconField = FindField(cardType, "_unplayableEnergyIcon");
            CardUpdateEnergyCostMethod = cardType?.GetMethod("UpdateEnergyCostColor", InstAll);

            Type? cardModelType = sts2.GetType("MegaCrit.Sts2.Core.Models.CardModel");
            CardModelCanPlayMethod = cardModelType?.GetMethod("CanPlay", InstAll, null, Type.EmptyTypes, null);
            // The warmup's card paths: the pool's frame material and energy sprite font, and the card's own banner material
            // and atlas sprites.
            Type? cardPoolModelType = sts2.GetType("MegaCrit.Sts2.Core.Models.CardPoolModel");
            _poolFrameMaterialPathProp = FindProperty(cardPoolModelType, "FrameMaterialPath");
            _poolEnergyIconPathProp = FindProperty(cardPoolModelType, "EnergyIconPath");
            _cardPoolProp = FindProperty(cardModelType, "Pool");
            _bannerMaterialPathProp = FindProperty(cardModelType, "BannerMaterialPath");
            _portraitPathProp = FindProperty(cardModelType, "PortraitPath");
            _framePathProp = FindProperty(cardModelType, "FramePath");
            _portraitBorderPathProp = FindProperty(cardModelType, "PortraitBorderPath");
            _bannerTexturePathProp = FindProperty(cardModelType, "BannerTexturePath");
            Type? creatureModelType = sts2.GetType("MegaCrit.Sts2.Core.Entities.Creatures.Creature");
            _creaturePowersProp = FindProperty(creatureModelType, "Powers");
            Type? powerModelType = sts2.GetType("MegaCrit.Sts2.Core.Models.PowerModel");
            _powerBigIconPathProp = FindProperty(powerModelType, "BigIconPath");
            _powerPackedIconPathProp = FindProperty(powerModelType, "PackedIconPath");

            Type? highlightType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Cards.NCardHighlight");
            HighlightTweenField = FindField(highlightType, "_curTween");

            Type? endTurnType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NEndTurnButton");
            EndTurnStateField = FindField(endTurnType, "_state");
            EndTurnPosTweenField = FindField(endTurnType, "_positionTween");
            EndTurnShowPosProp = FindProperty(endTurnType, "ShowPos");
            EndTurnHidePosProp = FindProperty(endTurnType, "HidePos");

            Type? confirmType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.CommonUi.NConfirmButton");
            ConfirmShowPosField = FindField(confirmType, "_showPos");
            ConfirmHidePosField = FindField(confirmType, "_hidePos");
            ConfirmMoveTweenField = FindField(confirmType, "_moveTween");

            Type? pingType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NPingButton");
            PingStateField = FindField(pingType, "_state");
            PingPosTweenField = FindField(pingType, "_positionTween");
            PingShowPosProp = FindProperty(pingType, "ShowPos");
            PingHidePosProp = FindProperty(pingType, "HidePos");

            Type? proceedType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.CommonUi.NProceedButton");
            ButtonIsEnabledProp = FindProperty(proceedType, "IsEnabled");
            ProceedAnimTweenField = FindField(proceedType, "_animTween");
            ProceedShowPosProp = FindProperty(proceedType, "ShowPos");
            ProceedHidePosProp = FindProperty(proceedType, "HidePos");

            Type? pileBaseType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NCombatCardPile");
            PileShowPosField = FindField(pileBaseType, "_showPosition");
            PileHidePosField = FindField(pileBaseType, "_hidePosition");
            PilePosTweenField = FindField(pileBaseType, "_positionTween");

            Type? exhaustType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NExhaustPileButton");
            ExhaustSetAnimPositionsMethod = exhaustType?.GetMethod("SetAnimInOutPositions", InstAll);

            Type? creatureNodeType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NCreature");
            CreatureEntityProp = FindProperty(creatureNodeType, "Entity");
            CreatureVisualsProp = FindProperty(creatureNodeType, "Visuals");
            CreatureOrbManagerProp = FindProperty(creatureNodeType, "OrbManager");
            Type? orbManagerType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Orbs.NOrbManager");
            OrbListField = FindField(orbManagerType, "_orbs");
            OrbManagerTweenField = FindField(orbManagerType, "_curTween");
            _creatureUpdateBoundsMethod = creatureNodeType?.GetMethod("UpdateBounds", InstAll, null, [typeof(Node)], null);

            Type? stateDisplayType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NCreatureStateDisplay");
            _stateDisplaySetBoundsMethod = stateDisplayType?.GetMethod("SetCreatureBounds", InstAll, null, [typeof(Control)], null);
            Type? powerContainerType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NPowerContainer");
            _powerContainerSetBoundsMethod = powerContainerType?.GetMethod("SetCreatureBounds", InstAll, null, [typeof(Control)], null);

            Type? entityType = sts2.GetType("MegaCrit.Sts2.Core.Entities.Creatures.Creature");
            EntityIsPlayerProp = FindProperty(entityType, "IsPlayer");
            _entitySideProp = FindProperty(entityType, "Side");
            _entityPlayerProp = FindProperty(entityType, "Player");
            EntityMonsterProp = FindProperty(entityType, "Monster");
            CreatureSlotNameProp = FindProperty(entityType, "SlotName");
            _entityCombatStateProp = FindProperty(entityType, "CombatState");
            _combatStateRunStateProp = sts2.GetType("MegaCrit.Sts2.Core.Combat.ICombatState")?.GetProperty("RunState");
            _combatStateEncounterProp = sts2.GetType("MegaCrit.Sts2.Core.Combat.ICombatState")?.GetProperty("Encounter");
            // EncounterModel.HasCustomBackground is protected virtual: the boss models override it.
            _encounterCustomBackgroundGetter = FindProperty(
                sts2.GetType("MegaCrit.Sts2.Core.Models.EncounterModel"), "HasCustomBackground")?.GetGetMethod(nonPublic: true);
            _runStateActProp = sts2.GetType("MegaCrit.Sts2.Core.Runs.IRunState")?.GetProperty("Act");
            _monsterBarReductionProp = FindProperty(sts2.GetType("MegaCrit.Sts2.Core.Models.MonsterModel"), "HpBarSizeReduction");

            Type? playerType = sts2.GetType("MegaCrit.Sts2.Core.Entities.Players.Player");
            _playerCombatStateProp = FindProperty(playerType, "PlayerCombatState");

            Type? pcsType = sts2.GetType("MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState");
            _orbQueueProp = FindProperty(pcsType, "OrbQueue");

            Type? orbQueueType = sts2.GetType("MegaCrit.Sts2.Core.Entities.Orbs.OrbQueue");
            _orbCapacityProp = FindProperty(orbQueueType, "Capacity");

            Type? visualsType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NCreatureVisuals");
            _visualsBoundsProp = FindProperty(visualsType, "Bounds");
        }

        public Node? GetActiveCombatRoom() => _combatRoomInstanceProp?.GetValue(null) as Node;

        public bool IsCombatInProgress()
        {
            object? inst = _combatMgrInstanceProp?.GetValue(null);
            return inst != null && (bool)(_combatMgrInProgressProp?.GetValue(inst) ?? false);
        }

        /// <summary>
        /// Whether this fight is over: neither running nor still being set up. The combat UI is drawn, and the rail with
        /// it, from <c>CombatSetUp</c> until <c>IsInProgress</c> flips true, so <see cref="IsCombatInProgress"/> alone
        /// reports "not running" during the whole entrance.
        /// </summary>
        public bool IsCombatOver()
        {
            object? inst = _combatMgrInstanceProp?.GetValue(null);
            if (inst == null) return true;
            bool inProgress = (bool)(_combatMgrInProgressProp?.GetValue(inst) ?? false);
            bool starting = (bool)(_combatMgrStartingProp?.GetValue(inst) ?? false);
            return !inProgress && !starting;
        }

        /// <summary>
        /// The game's turn-start hand size (<c>CombatManager.baseHandDrawCount</c>), capped at the hand's ten card limit
        /// (<c>CardPile.MaxCardsInHand</c>); zero when the field is gone, which leaves the hand's count to itself.
        /// </summary>
        public int HandDrawCount() =>
            _combatMgrBaseHandDrawField?.GetValue(null) is int draw && draw > 0 ? Math.Min(draw, 10) : 0;

        /// <summary>
        /// The player's turn number (the game increments it at the side switch, before that turn's hand draw), so a draw
        /// that starts it can be told from the others. Zero when it cannot be read.
        /// </summary>
        public int PlayerTurnNumber(Control hand) =>
            PlayerCombatStateOf(hand) is { } state && PlayerCombatTurnProp?.GetValue(state) is int turn ? turn : 0;

        /// <summary>The player's energy and stars, the two state inputs of a card's playability.</summary>
        public (int Energy, int Stars) PlayerResources(Control hand) =>
            PlayerCombatStateOf(hand) is { } state
                ? ((int)(PlayerCombatEnergyProp?.GetValue(state) ?? 0), (int)(PlayerCombatStarsProp?.GetValue(state) ?? 0))
                : (0, 0);

        /// <summary>Whether the game has locked player actions for this fight (a card or potion resolving, an enemy turn).</summary>
        public bool PlayerActionsDisabled()
        {
            object? inst = _combatMgrInstanceProp?.GetValue(null);
            return inst != null && (bool)(CombatMgrActionsDisabledProp?.GetValue(inst) ?? false);
        }

        private object? PlayerCombatStateOf(Control hand)
        {
            if (hand.GetParent() is not Control ui)
                return null;
            if (EnergyCounterField?.GetValue(ui) is not Control counter)
                return null;
            object? player = EnergyCounterPlayerField?.GetValue(counter);
            return player == null ? null : PlayerCombatStateProp?.GetValue(player);
        }

        private object? PlayerOf(Control hand)
        {
            if (hand.GetParent() is not Control ui)
                return null;
            if (EnergyCounterField?.GetValue(ui) is not Control counter)
                return null;
            return EnergyCounterPlayerField?.GetValue(counter);
        }

        // ---- Combat asset warmup (see CombatAssetWarmup): the paths the game loads on their first use in a fight. ----

        private static readonly string[] CombatPileNames = ["Hand", "Draw", "Discard", "Exhaust", "Play"];

        /// <summary>Every card model in the player's combat piles, i.e. the whole deck this fight can draw or show.</summary>
        public List<object> CombatCardModels(Control hand)
        {
            var models = new List<object>(64);
            object? player = PlayerOf(hand);
            if (player == null || CardPileGetMethod == null || CardPileCardsProp == null)
                return models;
            Type pileType = CardPileGetMethod.GetParameters()[0].ParameterType;
            foreach (string pileName in CombatPileNames)
            {
                object? pile;
                try
                {
                    pile = CardPileGetMethod.Invoke(null, [Enum.Parse(pileType, pileName), player]);
                }
                catch (Exception)
                {
                    continue;
                }
                if (pile == null || CardPileCardsProp.GetValue(pile) is not System.Collections.IEnumerable cards)
                    continue;
                foreach (object? card in cards)
                    if (card != null)
                        models.Add(card);
            }
            return models;
        }

        /// <summary>
        /// The visual resources a card model draws: the pool's frame material and energy sprite font (the two the device log
        /// shows loaded on the first card of every room), the banner material, and the atlas-backed portrait, frame, border
        /// and banner sprites.
        /// </summary>
        public void AddCardVisualPaths(object model, List<string> paths)
        {
            AddWarmupPath(_bannerMaterialPathProp?.GetValue(model) as string, paths);
            AddWarmupPath(_portraitPathProp?.GetValue(model) as string, paths);
            AddWarmupPath(_framePathProp?.GetValue(model) as string, paths);
            AddWarmupPath(_portraitBorderPathProp?.GetValue(model) as string, paths);
            AddWarmupPath(_bannerTexturePathProp?.GetValue(model) as string, paths);
            if (_cardPoolProp?.GetValue(model) is { } pool)
            {
                AddWarmupPath(_poolFrameMaterialPathProp?.GetValue(pool) as string, paths);
                AddWarmupPath(_poolEnergyIconPathProp?.GetValue(pool) as string, paths);
            }
        }

        /// <summary>The big icon (the power flash and the hover tip) and the atlas icon of every power on a creature.</summary>
        public void AddCreaturePowerPaths(Control creature, List<string> paths)
        {
            object? entity = CreatureEntityProp?.GetValue(creature);
            if (entity == null || _creaturePowersProp?.GetValue(entity) is not System.Collections.IEnumerable powers)
                return;
            foreach (object? power in powers)
            {
                if (power == null)
                    continue;
                AddWarmupPath(_powerBigIconPathProp?.GetValue(power) as string, paths);
                AddWarmupPath(_powerPackedIconPathProp?.GetValue(power) as string, paths);
            }
        }

        /// <summary>The room's live creature nodes (players, pets and enemies).</summary>
        public List<Control> RoomCreatures(Control room)
        {
            var creatures = new List<Control>(8);
            if (RoomCreatureNodesField?.GetValue(room) is not System.Collections.IEnumerable nodes)
                return creatures;
            foreach (object? item in nodes)
                if (item is Control creature && GodotObject.IsInstanceValid(creature))
                    creatures.Add(creature);
            return creatures;
        }

        /// <summary>Whether any player-side creature has an orb queue, i.e. the fight can draw or evoke an orb.</summary>
        public bool AnyPlayerHasOrbs(Control room)
        {
            foreach (Control creature in RoomCreatures(room))
            {
                object? entity = CreatureEntityProp?.GetValue(creature);
                if (entity == null || !IsPlayerSide(entity))
                    continue;
                if (CreatureHasOrbs(creature))
                    return true;
            }
            return false;
        }

        private static void AddWarmupPath(string? path, List<string> paths)
        {
            if (!string.IsNullOrEmpty(path))
                paths.Add(path!);
        }

        /// <summary>
        /// The count this turn's hand draw was made with, from the game's own query
        /// (<c>Hook.ModifyHandDraw(state, player, 5, out modifiers)</c>), which is pure — every override in the game only
        /// returns a count. It is asked again after the game has read it, so the two modifiers that consume their bonus in
        /// their own <c>AfterModifyingHandDraw</c> (<c>DrawCardsNextTurnPower</c> removes itself, <c>PollinousCore</c> zeroes
        /// its counter) come back without it: an under-count, which only leaves that draw on the base layout. It can never
        /// over-count, so no card is ever placed right of where it rests. Returns -1 when it cannot be read.
        /// </summary>
        public int TurnDrawCount(Control hand)
        {
            if (HandDrawHookMethod == null)
                return -1;
            object? state = HandCombatStateField?.GetValue(hand);
            object? player = PlayerOf(hand);
            if (state == null || player == null)
                return -1;
            try
            {
                object?[] args = [state, player, 5m, null];
                return HandDrawHookMethod.Invoke(null, args) is decimal count ? (int)count : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// How many cards with <c>Innate</c> the turn-start draw is made of: on the first turn the game moves them to the top
        /// of the draw pile and raises the hand draw to at least their number, and nothing consumes them before the draw, so
        /// counting them in the hand and the draw pile at the draw's first card is exact. Returns -1 if it cannot read them.
        /// </summary>
        public int InnateCards(Control hand, IReadOnlyList<Control> holders)
        {
            if (InnateKeyword == null || CardKeywordsProp == null)
                return -1;
            object? player = PlayerOf(hand);
            if (player == null || CardPileGetMethod == null || CardPileCardsProp == null)
                return -1;
            try
            {
                int innate = 0;
                foreach (Control holder in holders)
                {
                    if (CardModelOf(CardHolderCardNodeProp?.GetValue(holder) as Control) is { } model &&
                        KeywordsContainInnate(model))
                        innate++;
                }
                object? pile = CardPileGetMethod.Invoke(null,
                    [Enum.Parse(CardPileGetMethod.GetParameters()[0].ParameterType, "Draw"), player]);
                if (pile != null && CardPileCardsProp.GetValue(pile) is System.Collections.IEnumerable cards)
                {
                    foreach (object? card in cards)
                    {
                        if (card != null && KeywordsContainInnate(card))
                            innate++;
                    }
                }
                return innate;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private bool KeywordsContainInnate(object model)
        {
            if (CardKeywordsProp?.GetValue(model) is not System.Collections.IEnumerable keywords)
                return false;
            foreach (object? keyword in keywords)
            {
                if (Equals(keyword, InnateKeyword))
                    return true;
            }
            return false;
        }

        public bool CanCardPlay(Control? cardNode)
        {
            if (cardNode == null) return true;
            return CanCardPlay(CardModelProp?.GetValue(cardNode));
        }

        /// <summary>
        /// The game's own answer for a card model (<c>CardModel.CanPlay</c> → <c>Hook.ShouldPlay</c>, which walks the relics
        /// and powers). Called only when the inputs that decide it changed.
        /// </summary>
        public bool CanCardPlay(object? model) =>
            model == null || (bool)(CardModelCanPlayMethod?.Invoke(model, null) ?? true);

        /// <summary>The card model a holder's card node shows (the game's own <c>CardNode.Model</c>).</summary>
        public object? CardModelOf(Control? cardNode) => cardNode == null ? null : CardModelProp?.GetValue(cardNode);

        public bool IsPlayerSide(object entity)
        {
            object? side = _entitySideProp?.GetValue(entity);
            return side != null && Convert.ToInt32(side) == 1; // CombatSide.Player == 1 (check below or fallback to IsPlayer/PetOwner)
        }

        public bool CreatureHasOrbs(Control creature)
        {
            object? entity = CreatureEntityProp?.GetValue(creature);
            object? player = entity != null ? _entityPlayerProp?.GetValue(entity) : null;
            object? pcs = player != null ? _playerCombatStateProp?.GetValue(player) : null;
            object? orbQueue = pcs != null ? _orbQueueProp?.GetValue(pcs) : null;
            int cap = orbQueue != null ? Convert.ToInt32(_orbCapacityProp?.GetValue(orbQueue) ?? 0) : 0;
            return cap > 0;
        }

        public Vector2 GetCreatureRawBoundsSize(Control creature)
        {
            if (CreatureVisualsProp?.GetValue(creature) is Node visuals &&
                _visualsBoundsProp?.GetValue(visuals) is Control bounds)
            {
                return bounds.Size;
            }
            return new Vector2(220f, 220f);
        }

        public Vector2 GetCreatureVisualsScale(Control creature) =>
            CreatureVisualsProp?.GetValue(creature) is Node2D visuals ? visuals.Scale : Vector2.One;

        public void InvokeUpdateBounds(Control creature)
        {
            if (CreatureVisualsProp?.GetValue(creature) is Node visuals)
                _creatureUpdateBoundsMethod?.Invoke(creature, [visuals]);
        }

        /// <summary>The width the game adds to a creature's hit box for its health bar: 24 less the monster's reduction, none for a player.</summary>
        public float GetHealthBarExtraWidth(Control creature)
        {
            object? monster = MonsterOf(creature);
            if (monster == null)
                return 0f;
            return 24f - (_monsterBarReductionProp?.GetValue(monster) is float reduction ? reduction : 0f);
        }

        /// <summary>The encounter slot's name the game gave the creature (an egg's <c>egg5</c>, a segment's <c>segment2</c>).</summary>
        public string? GetSlotName(Control creature)
        {
            object? entity = CreatureEntityProp?.GetValue(creature);
            return entity != null ? CreatureSlotNameProp?.GetValue(entity) as string : null;
        }

        /// <summary>Whether the creature's monster model is <paramref name="typeName"/> or derives from it.</summary>
        public bool IsMonsterFamily(Control creature, string typeName)
        {
            for (Type? type = MonsterOf(creature)?.GetType(); type != null; type = type.BaseType)
            {
                if (type.Name == typeName)
                    return true;
            }
            return false;
        }

        public string GetMonsterTypeName(Control creature) => MonsterOf(creature)?.GetType().Name ?? "";

        /// <summary>Whether the run is in the Underdocks, whose narrow pier cannot be framed like a broad floor.</summary>
        public bool IsUnderdocks(Control creature)
        {
            object? entity = CreatureEntityProp?.GetValue(creature);
            object? combatState = entity != null ? _entityCombatStateProp?.GetValue(entity) : null;
            object? runState = combatState != null ? _combatStateRunStateProp?.GetValue(combatState) : null;
            object? act = runState != null ? _runStateActProp?.GetValue(runState) : null;
            return act?.GetType().Name == "Underdocks";
        }

        /// <summary>
        /// Whether this encounter brings its own arena scene instead of being drawn on the act's background. The game's own
        /// signal (<c>EncounterModel.HasCustomBackground</c>); a room that has one is never framed as the act's pier.
        /// </summary>
        public bool EncounterHasCustomBackground(List<Control> players, List<Control> enemies)
        {
            if (_combatStateEncounterProp == null || _encounterCustomBackgroundGetter == null)
                return false;
            Control? probe = players.Count > 0 ? players[0] : (enemies.Count > 0 ? enemies[0] : null);
            if (probe == null)
                return false;
            object? entity = CreatureEntityProp?.GetValue(probe);
            object? combatState = entity != null ? _entityCombatStateProp?.GetValue(entity) : null;
            object? encounter = combatState != null ? _combatStateEncounterProp.GetValue(combatState) : null;
            if (encounter == null)
                return false;
            try
            {
                return _encounterCustomBackgroundGetter.Invoke(encounter, null) is true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The merged hit box and drawn bounds a segment is fitted and centred by, in its own units.</summary>
        public Vector2 GetCreatureFootprintSize(Control creature)
        {
            Vector2 bounds = GetCreatureRawBoundsSize(creature);
            Vector2 hitbox = Vector2.Zero;
            if ((creature.GetNodeOrNull<Control>("%Hitbox") ?? creature.GetNodeOrNull<Control>("Hitbox")) is { } hit)
                hitbox = hit.Size;
            return new Vector2(Math.Max(bounds.X, hitbox.X), Math.Max(bounds.Y, hitbox.Y));
        }

        private object? MonsterOf(Control creature)
        {
            object? entity = CreatureEntityProp?.GetValue(creature);
            return entity != null ? EntityMonsterProp?.GetValue(entity) : null;
        }

        /// <summary>
        /// Whether a creature's health display can be laid out for other bounds than its hit box. Without that the display
        /// keeps the game's scale: scaled up with the game's own layout it would be far wider than its creature.
        /// </summary>
        public bool CanLayOutStateDisplay => _stateDisplaySetBoundsMethod != null;

        /// <summary><c>NCreatureStateDisplay.SetCreatureBounds</c>: places the bar, block, name and powers for these bounds.</summary>
        public bool LayOutStateDisplay(Control display, Control bounds)
        {
            if (_stateDisplaySetBoundsMethod == null)
                return false;
            try
            {
                _stateDisplaySetBoundsMethod.Invoke(display, [bounds]);
                return true;
            }
            catch (Exception error)
            {
                if (!_stateDisplayFailureLogged)
                    GD.PrintErr("Portrait health display layout failed: ", (error.InnerException ?? error).Message);
                _stateDisplayFailureLogged = true;
                return false;
            }
        }

        /// <summary><c>NPowerContainer.SetCreatureBounds</c>: wraps the power icons to the width of these bounds.</summary>
        public void LayOutPowerContainer(Control powerContainer, Control bounds)
        {
            try
            {
                _powerContainerSetBoundsMethod?.Invoke(powerContainer, [bounds]);
            }
            catch (Exception error)
            {
                GD.PrintErr("Portrait power layout failed: ", (error.InnerException ?? error).Message);
            }
        }

        private static FieldInfo? FindField(Type? type, string name)
        {
            for (Type? t = type; t != null; t = t.BaseType)
            {
                FieldInfo? f = t.GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        private static PropertyInfo? FindProperty(Type? type, string name)
        {
            for (Type? t = type; t != null; t = t.BaseType)
            {
                PropertyInfo? p = t.GetProperty(
                    name,
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (p != null) return p;
            }
            return null;
        }
    }
}
