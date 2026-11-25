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
        _stage = new PortraitCombatStage(CaptureCreatureHud, QueueCreatureLabel);
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

    // Text: a creature is drawn at 0.42 to 0.94 of its size to fit the stage, which left its health, block and
    // name text (24 units), power amounts (18) and intent values (22) at about 6 points. The health display and the
    // intents are drawn at one fixed size on the canvas instead, whatever the creature's own scale: 24 units become 11
    // points at 1.33, 22 units at 1.45, and power amounts are set in 24 units.
    private const float StateDisplayCanvasScale = 1.33f;
    internal const float IntentCanvasScale = 1.45f;
    // The clearance the HP text keeps after the block shield and inside the bar's right end, and the smallest the digits
    // may shrink to when a bar is too narrow for the shield and the text together. The floor is low because the rule is
    // hard (the digits never pass the bar's end): the worst real case is the fixture's 4/4-digit hero HP on a 176 bar,
    // which needs 0.62, while real HP numbers are far shorter.
    private const float HpTextClearance = 4f;
    private const float HpTextMinScale = 0.50f;
    private const int PowerAmountFontSize = 24;

    private const float MinHealthBarWidth = PortraitCombatStage.MinHealthBarWidth;
    private const float MinNameplateWidth = 250f;
    private const float StageSideMargin = PortraitCombatStage.StageSideMargin;
    private const float PowerIconPitch = 48f;

    private readonly Dictionary<ulong, PortraitCombatRail.DesktopState> _rooms = new();
    private readonly Dictionary<ulong, CreatureDesktopState> _creatures = new();
    private readonly Dictionary<ulong, int> _enlargedFonts = new();
    // Power icons whose amount label already got its portrait size (a power keeps its icon node for its whole life).
    private readonly HashSet<ulong> _enlargedPowerAmounts = new();
    private PortraitCombatBands? _lastBands;
    // Whether any player on the stage has an orb queue at all: the pre-draw orb refit has nothing to do otherwise.
    // The frame's label requests, refilled rather than allocated every frame (the batch only reads the list).
    private readonly List<LabelRequest> _labelRequests = new();
    // The status batch's planning collections, refilled per frame instead of allocated (see the batch).
    private readonly List<(LabelRequest Request, StatusIconLayout? Layout, int Rank, float RowY)> _plannedLabels = new();
    private readonly Dictionary<(int Rank, float RowY), List<int>> _statusRows = new();
    private readonly List<List<int>> _statusRowIndexLists = new();
    private int _statusRowListsUsed;
    private readonly Dictionary<ulong, StatusIconLayout> _limitedStatus = new();
    private readonly List<StatusRowEntry> _statusRowEntries = new();
    private readonly List<(StatusRowEntry Entry, StatusIconLayout Layout)> _statusRowPlan = new();
    // The hit-box rects one status-planning pass reads (see StatusBodyRect): cleared when a new pass begins.
    private readonly Dictionary<ulong, Rect2> _statusBodyRects = new();
    private int _statusBodyEpoch;
    private int _statusBodyRectsEpoch = -1;
    private readonly Dictionary<ulong, (Control Container, Callable Handler)> _wiredPowerContainers = new();
    private readonly PortraitHandLayout _hand;
    private readonly PortraitCombatRail _rail;
    private readonly PortraitCombatStage _stage;
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

        foreach ((ulong labelId, int fontSize) in _enlargedFonts)
        {
            if (GodotObject.InstanceFromId(labelId) is Label label && GodotObject.IsInstanceValid(label))
                label.AddThemeFontSizeOverride("font_size", fontSize);
        }
        _enlargedFonts.Clear();
        _enlargedPowerAmounts.Clear();

        foreach ((ulong creatureId, CreatureDesktopState state) in _creatures)
        {
            if (GodotObject.InstanceFromId(creatureId) is Control creature && GodotObject.IsInstanceValid(creature))
                state.Restore(creature, refl, _stage);
        }
        _creatures.Clear();

        foreach ((ulong containerId, (Control container, Callable handler)) in _wiredPowerContainers)
        {
            if (GodotObject.InstanceFromId(containerId) is Control live && GodotObject.IsInstanceValid(live) &&
                live.HasSignal("child_entered_tree"))
                live.Disconnect("child_entered_tree", handler);
        }
        _wiredPowerContainers.Clear();

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
        foreach (ulong id in _creatures.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _creatures.Remove(id);

        _hand.PruneDead(dead);

        dead.Clear();
        foreach (ulong id in _enlargedFonts.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _enlargedFonts.Remove(id);

        dead.Clear();
        foreach (ulong id in _enlargedPowerAmounts)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _enlargedPowerAmounts.Remove(id);

        _rail.PruneDead(dead);

        dead.Clear();
        foreach (ulong id in _wiredPowerContainers.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _wiredPowerContainers.Remove(id);

        _stage.PruneDead(dead);

        dead.Clear();
        foreach (ulong id in _statusBodyRects.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _statusBodyRects.Remove(id);
    }

    /// <summary>
    /// Sets a label's font size up to <paramref name="fontSize"/>, keeping the size it had so landscape gets it back.
    /// For labels the game does not size itself (auto size off): it would put its own size back on the next text change.
    /// </summary>
    private void EnlargeLabelFont(Label? label, int fontSize)
    {
        if (label == null || !GodotObject.IsInstanceValid(label)) return;
        int current = label.GetThemeFontSize("font_size");
        if (current >= fontSize) return;
        _enlargedFonts.TryAdd(label.GetInstanceId(), current);
        label.AddThemeFontSizeOverride("font_size", fontSize);
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

        _labelRequests.Clear();
        using (PortraitFrameTiming.Begin(PortraitFrameTiming.Stage))
            _stage.Apply(room, refl, bands, canvasHeight);
        using (PortraitFrameTiming.Begin(PortraitFrameTiming.Labels))
            ApplyCreatureLabelsPortraitBatch(_labelRequests, room, refl);
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
        if (!_creatures.TryGetValue(creature.GetInstanceId(), out CreatureDesktopState? combatState) ||
            combatState?.Hitbox is not { } hitbox || !GodotObject.IsInstanceValid(hitbox))
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
                    !_creatures.TryGetValue(enemy.GetInstanceId(), out CreatureDesktopState? state) ||
                    state?.Hitbox is not { } hitbox || !GodotObject.IsInstanceValid(hitbox))
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
                !_creatures.TryGetValue(enemy.GetInstanceId(), out CreatureDesktopState? state) ||
                state?.Hitbox is not { } hitbox || !GodotObject.IsInstanceValid(hitbox))
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
    /// by the creature state (see <see cref="CreatureDesktopState.Restore"/>), which calls this.
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

    /// <summary>
    /// A creature's intents and its health display (bar, block, name, powers) are drawn at one size on the canvas
    /// (<see cref="IntentCanvasScale"/>, <see cref="StateDisplayCanvasScale"/>) whatever scale the creature has, so their
    /// text is 11 points. The game lays the health display out from the creature's hit box, as wide as the creature;
    /// scaled up, that would be up to twice as wide and start at the hit box's left edge. So the display is laid out again
    /// from a stand-in for the hit box (a hidden control) sized for a bar of the creature's width on the canvas, centred
    /// under it, no wider than <paramref name="maxBarWidth"/> and inside the canvas. This runs after the game's own layout
    /// (<c>NCreature.UpdateBounds</c>), and again whenever the game has laid the display out since.
    /// </summary>
    private void ApplyCreatureLabelsPortrait(
        Control creature,
        CreatureDesktopState cState,
        CombatReflection refl,
        float maxBarWidth,
        bool isAlly,
        StatusIconLayout? plannedStatus = null)
    {
        float creatureScale = creature.GetGlobalTransform().Scale.X;
        if (creatureScale < 0.05f)
            return;

        if (!isAlly && cState.Intents is { } intents && GodotObject.IsInstanceValid(intents))
        {
            // Grown from the bottom centre, so the intents rise above the head instead of sinking into it. The game owns
            // the row's position; the row only needs laying out again when its icons change or the creature's scale does,
            // and enumerating it every frame was one Godot array per creature per frame.
            float intentScale = IntentCanvasScale / creatureScale;
            int intentCount = intents.GetChildCount();
            float intentWidth = intents.Size.X;
            if (intentCount != cState.LastIntentCount || MathF.Abs(cState.LastIntentScale - intentScale) > 0.002f ||
                MathF.Abs(cState.LastIntentWidth - intentWidth) > 0.25f)
            {
                cState.LastIntentCount = intentCount;
                cState.LastIntentScale = intentScale;
                cState.LastIntentWidth = intentWidth;
                LayoutCombatIntents(creature, intents, creatureScale, intentScale);
            }
        }

        if (!refl.CanLayOutStateDisplay || cState.StateDisplayLayoutFailed ||
            cState.Display is not { } display || !GodotObject.IsInstanceValid(display) ||
            cState.Hitbox is not { } hitbox || !GodotObject.IsInstanceValid(hitbox) ||
            cState.Nameplate is not { } nameplate || !GodotObject.IsInstanceValid(nameplate) ||
            cState.HpBar is not { } hpBar || !GodotObject.IsInstanceValid(hpBar))
            return;

        float displayScale = StateDisplayCanvasScale / creatureScale;
        if (display.PivotOffset != Vector2.Zero)
            display.PivotOffset = Vector2.Zero;
        if (MathF.Abs(display.Scale.X - displayScale) > 0.002f)
            display.Scale = new Vector2(displayScale, displayScale);

        Control? powerContainer = cState.PowerContainer is { } p && GodotObject.IsInstanceValid(p) ? p : null;
        if (powerContainer != null)
        {
            // Each icon's amount label is enlarged once, when the icon first appears; a power's icon keeps its node and its
            // font override, so looking the label up again every frame was two Godot calls per power per creature. The
            // walk itself is a Godot array per creature per frame, and an icon can only appear with a child-count change
            // or the container's own add signal, so it is only taken then.
            int powerChildren = powerContainer.GetChildCount();
            if (powerChildren != cState.LastPowerChildCount || cState.PowerChildrenDirty)
            {
                cState.LastPowerChildCount = powerChildren;
                cState.PowerChildrenDirty = false;
                for (int i = 0; i < powerChildren; i++)
                {
                    Node power = powerContainer.GetChild(i);
                    if (!_enlargedPowerAmounts.Add(power.GetInstanceId()))
                        continue;
                    EnlargeLabelFont(power.GetNodeOrNull<Label>("%AmountLabel") ?? power.GetNodeOrNull<Label>("AmountLabel"),
                        PowerAmountFontSize);
                }
            }
        }

        // One unit of the display on the canvas, and the bar's place there: as wide as the game draws it (the hit box and
        // what the game adds to it), centred under the hit box.
        float unit = creatureScale * display.Scale.X;
        float hitWidth = hitbox.Size.X * creatureScale;
        float barWidth = Math.Clamp(
            hitWidth + cState.BarExtraWidth * creatureScale, Math.Min(MinHealthBarWidth, maxBarWidth), maxBarWidth);
        float barLeft = LeftEdgeOnCanvas(hitbox.GlobalPosition.X + hitWidth * 0.5f, barWidth);
        // The name may be wider than the bar, but never wider than the room the slot gives it: the game shrinks a name
        // that does not fit, and only one name shows at a time.
        float nameWidth = Math.Max(barWidth, Math.Min(MinNameplateWidth, maxBarWidth));
        float nameLeft = LeftEdgeOnCanvas(barLeft + barWidth * 0.5f, nameWidth);
        float nameLocalX = (nameLeft - display.GlobalPosition.X) / unit;
        float nameLocalWidth = nameWidth / unit;

        // The name plate is the marker for the game's own bar layout: when it is already where this layout puts it, only
        // the power icons (which change with the status count, not the name) are laid out again below.
        bool nameSynced = MathF.Abs(nameplate.Position.X - nameLocalX) <= 0.75f &&
            MathF.Abs(nameplate.Size.X - nameLocalWidth) <= 0.75f;
        float boundsY = hitbox.GlobalPosition.Y;
        Control? bounds = null;
        if (!nameSynced)
        {
            bounds = cState.EnsureBoundsStandIn(creature);
            if (bounds == null)
                return;

            // The bar: the game makes it wider than the bounds by its extra width in display units, and starts it half
            // that many canvas units left of them.
            bounds.Size = new Vector2(Math.Max(8f, barWidth / unit - cState.BarExtraWidth), 8f);
            bounds.GlobalPosition = new Vector2(barLeft + cState.BarExtraWidth * 0.5f, boundsY);
            if (!refl.LayOutStateDisplay(display, bounds))
            {
                // Scaled up with the game's own layout the display would be far wider than its creature: this creature
                // keeps the game's size and layout from here on.
                cState.GiveUpStateDisplay(creature, refl);
                return;
            }
        }

        // The powers: the game's own two-row layout (container from the hit box, columns from its width and the count)
        // with the portrait icon size and the neighbour-limited block placed left of it.
        if (powerContainer != null)
        {
            // The batch already counted the block's icons to plan it; only a creature the batch did not plan needs a count.
            int powerCount = plannedStatus is { Count: > 0 } knownCount ? knownCount.Count : CountVisiblePowers(powerContainer);
            if (powerCount > 0)
            {
                StatusIconLayout icons = plannedStatus is { Count: > 0 } planned && planned.Count == powerCount
                    ? planned
                    : ComputeStatusLayout(cState.Hitbox, powerCount);
                ApplyStatusBlock(creature, cState, refl, icons, powerCount);
            }
            // Wire the container even while it holds no powers: the FIRST power a creature gains is added inside
            // NPowerContainer.Add, and a container that was only wired once it already had an icon drew that first icon
            // for one frame at the native size and the native row pitch.
            EnsurePowerContainerWired(powerContainer, creature, cState, refl);
        }

        // The bar band hangs under the creature's OWN foot line, never under another row's: a back-rank peer or a flyer
        // stands on its slot's feet, and pinning every bar to the front foot line put its bar across the front row's
        // bodies. The band also clears the foot line by a few units instead of starting exactly on it, because every body
        // ends on that line and a bar touching it is reported as overlapping the body standing beside it.
        float ownFeet = OwnFeetLine(creature, cState);
        // The label the band and the block-shield rule use, remembered on the state while it stays valid: this pass asks for
        // it on every frame of the fight, and a % path resolves through the unique-name map.
        Label? hpLabel = cState.HpRuleLabel is { } knownLabel && GodotObject.IsInstanceValid(knownLabel)
            ? knownLabel
            : hpBar.GetNodeOrNull<Label>("%HpLabel") ?? hpBar.GetNodeOrNull<Label>("HpLabel");
        Control bandNode = hpLabel ?? hpBar;
        float bandTop = bandNode.GetGlobalRect().Position.Y;
        if (ownFeet > 1f)
        {
            float target = ownFeet + BarBandClearance;
            float shift = target - bandTop;
            if (MathF.Abs(shift) > 0.25f)
            {
                hpBar.GlobalPosition = new Vector2(hpBar.GlobalPosition.X, hpBar.GlobalPosition.Y + shift);
                if (powerContainer != null)
                    powerContainer.GlobalPosition = new Vector2(powerContainer.GlobalPosition.X,
                        powerContainer.GlobalPosition.Y + shift);
            }
        }
        // The block shield is drawn at the bar's left end; on a narrow bar it covers the first digit of the centred HP
        // text (a 7 shield over "12/29" shows "2/29"). The native bar never moves the label, so the digit is moved clear
        // here — only while a block is shown, only as far as it must, and never past the bar's right end.
        if (hpLabel != null)
            using (PortraitFrameTiming.Begin(PortraitFrameTiming.HpText))
                KeepHpTextClearOfBlockBand(creature, cState, hpBar, hpLabel);

        if (nameSynced)
            return;
        nameplate.Position = new Vector2(nameLocalX, nameplate.Position.Y);
        nameplate.Size = new Vector2(nameLocalWidth, nameplate.Size.Y);

        // The bar's touch area (258 units wide in the scene) follows the bar, so it does not reach over a neighbour's bar.
        if (cState.HpBarHitbox is { } barHitbox && GodotObject.IsInstanceValid(barHitbox))
        {
            barHitbox.OffsetLeft = hpBar.Position.X - 8f;
            barHitbox.OffsetRight = hpBar.Position.X + barWidth / unit + 8f;
        }
    }

    /// <summary>
    /// Keeps the HP text clear of the block shield the game draws at the bar's left end. The native bar toggles the
    /// shield (and slides it in on a block gain) but never moves <c>%HpLabel</c>, so its place is ours to set: the label
    /// is moved right by the overlap the shield has with the centred text, capped inside the bar's right end, and it goes
    /// back to its native X as soon as the shield is hidden again. Everything is measured in canvas units and the write
    /// is converted through the label's parent scale, so a nested or scaled bar behaves the same.
    /// <para>The expensive part is the glyph metrics — a text shape plus the font lookups for the digits and for the
    /// block number — and they only change with the text, the label's rect or its theme font size, so they are cached on
    /// the creature's state. The bar, the shield and the label are still measured live on every frame, so the native block
    /// slide is followed exactly; and a bar with no shield shown is put back and left alone before any of it is touched.</para>
    /// </summary>
    private static void KeepHpTextClearOfBlockBand(Control creature, CreatureDesktopState state, Control hpBar,
        Label hpLabel)
    {
        if (hpLabel == hpBar)
            return;   // no HP label in this bar's scene: the bar itself is never moved sideways
        if (state.HpRuleLabel != hpLabel)
        {
            // The first pass on this label (or a new one): its native place is captured once, and the metas keep it for
            // the landscape restore. The measured inks belong to this label, so they are dropped with it.
            state.HpRuleLabel = hpLabel;
            if (!hpLabel.HasMeta("portrait_hp_x"))
                hpLabel.SetMeta("portrait_hp_x", hpLabel.Position.X);
            if (!hpLabel.HasMeta("portrait_hp_scale"))
                hpLabel.SetMeta("portrait_hp_scale", hpLabel.Scale.X);
            state.HpNativeX = hpLabel.GetMeta("portrait_hp_x").AsSingle();
            state.HpNativeScale = hpLabel.GetMeta("portrait_hp_scale").AsSingle();
            state.HpInk = default;
            state.HpBlockInk = default;
            // Whatever the creature already carries (a report written before a portrait toggle) is taken as the known
            // state: the first frame that computes the same report writes nothing, and the first frame without a block
            // removes it.
            state.HpCapped = creature.HasMeta("portrait_hp_shift_capped")
                ? creature.GetMeta("portrait_hp_shift_capped").ToString()
                : null;
        }
        float nativeX = state.HpNativeX;
        float nativeScale = state.HpNativeScale;

        Control? shield = state.HpBlockShield;
        if (shield == null || !GodotObject.IsInstanceValid(shield))
        {
            shield = hpBar.GetNodeOrNull<Control>("%BlockContainer");
            state.HpBlockShield = shield;
            state.HpBlockIcon = null;
            state.HpBlockLabel = null;
        }
        if (shield == null || !shield.IsVisibleInTree())
        {
            // No block: the digits go back to the native place and size, so a bar the game draws cleanly is untouched.
            if (MathF.Abs(hpLabel.Scale.X - nativeScale) > 0.001f)
                hpLabel.Scale = new Vector2(nativeScale, nativeScale);
            if (MathF.Abs(hpLabel.Position.X - nativeX) > 0.01f)
                hpLabel.Position = new Vector2(nativeX, hpLabel.Position.Y);
            if (state.HpCapped is not null)
            {
                if (creature.HasMeta("portrait_hp_shift_capped"))
                    creature.RemoveMeta("portrait_hp_shift_capped");
                state.HpCapped = null;
            }
            return;
        }
        if (hpLabel.GetParent() is not Control parent)
            return;
        Transform2D parentTransform = parent.GetGlobalTransform();
        float parentScale = MathF.Max(0.0001f, parentTransform.Scale.X);
        // The ink as the native bar lays it out (the label's own scale is 1 there), mapped onto the canvas.
        Vector2 nativeOrigin = new(nativeX, hpLabel.Position.Y);
        Rect2 localInk = LabelInkLocal(hpLabel, hpLabel.Text, ref state.HpInk);
        Rect2 nativeInk = parentTransform * new Rect2(localInk.Position + nativeOrigin, localInk.Size);
        float pivotX = (parentTransform * (nativeOrigin + hpLabel.PivotOffset)).X;
        Rect2 barRect = hpBar.GetGlobalRect();
        Rect2 shieldInk = ShieldInk(shield, state);
        PortraitCombatMath.HpTextBlockFit fit = PortraitCombatMath.FitHpTextBesideBlock(
            nativeInk.Position.X, nativeInk.Size.X, pivotX, shieldInk.End.X, barRect.End.X, HpTextClearance,
            HpTextMinScale);
        float targetScale = fit.Scale * nativeScale;
        if (MathF.Abs(hpLabel.Scale.X - targetScale) > 0.001f)
            hpLabel.Scale = new Vector2(targetScale, targetScale);
        float targetX = nativeX + fit.Shift / parentScale;
        if (MathF.Abs(hpLabel.Position.X - targetX) > 0.01f)
            hpLabel.Position = new Vector2(targetX, hpLabel.Position.Y);
        // The floor: a bar too narrow for the shield and the smallest allowed digits still overflows its right end; keep
        // the legibility floor, say so on the creature, and let the stage report take it to the user. The string is only
        // written when it changes (the report reads it, and the fixture's 4-digit hero HP is capped for whole fights).
        string? capped = null;
        float inkRight = nativeInk.Position.X + fit.Shift + nativeInk.Size.X * fit.Scale;
        if (fit.Scale <= HpTextMinScale + 0.001f && inkRight > barRect.End.X - HpTextClearance + 0.5f)
            capped = $"need={fit.Shift:F1} scale={fit.Scale:F3} inkRight={inkRight:F1} barRight={barRect.End.X:F1}";
        if (!string.Equals(capped, state.HpCapped, StringComparison.Ordinal))
        {
            if (capped is null)
                creature.RemoveMeta("portrait_hp_shift_capped");
            else
                creature.SetMeta("portrait_hp_shift_capped", capped);
            state.HpCapped = capped;
        }
    }

    /// <summary>
    /// A label's drawn glyph ink in its own units, as the stage checks measure it: the text's advance at the label's own
    /// theme font size, its cap height, and the outline grown on every side, placed by the label's alignment inside its
    /// rect. The control's rect is not the ink (the game's HP label is wider and taller than the digits), and the metrics
    /// are scale-free so the caller can place the ink at the native scale itself.
    /// <para>The measurement is cached per label by its text, rect and theme font size: a text shape and the font
    /// metrics are the two expensive calls here, and they only change with those three (the game's auto-sized labels
    /// adjust the size from the rect when the text changes, and again if the rect resizes).</para>
    /// </summary>
    private static Rect2 LabelInkLocal(Label label, string text, ref CreatureDesktopState.GlyphInk cache)
    {
        int fontSize = label.GetThemeFontSize("font_size");
        Vector2 box = label.Size;
        if (cache.Known && cache.FontSize == fontSize && box == cache.Box &&
            string.Equals(text, cache.Text, StringComparison.Ordinal))
            return cache.Ink;
        Rect2 ink = MeasureLabelInk(label, text, box, fontSize);
        cache.Text = text;
        cache.Box = box;
        cache.FontSize = fontSize;
        cache.Ink = ink;
        cache.Known = true;
        return ink;
    }

    /// <summary>The uncached part of <see cref="LabelInkLocal"/>: the font metrics and the text's advance.</summary>
    private static Rect2 MeasureLabelInk(Label label, string text, Vector2 box, int fontSize)
    {
        Font? font = label.GetThemeFont("font");
        if (font == null || string.IsNullOrWhiteSpace(text))
            return new Rect2(Vector2.Zero, box);
        int outline = Math.Max(0, label.GetThemeConstant("outline_size"));
        Vector2 advance = font.GetStringSize(text, label.HorizontalAlignment, -1f, fontSize);
        float ascent = font.GetAscent(fontSize);
        Vector2 textSize = new(advance.X, ascent > 0f ? ascent : advance.Y);
        float x = label.HorizontalAlignment switch
        {
            HorizontalAlignment.Center => (box.X - textSize.X) * 0.5f,
            HorizontalAlignment.Right => box.X - textSize.X,
            _ => 0f,
        };
        float y = label.VerticalAlignment switch
        {
            VerticalAlignment.Center => (box.Y - textSize.Y) * 0.5f,
            VerticalAlignment.Bottom => box.Y - textSize.Y,
            _ => 0f,
        };
        return new Rect2(new Vector2(x - outline, y - outline),
            textSize + new Vector2(outline * 2f, outline * 2f));
    }

    /// <summary>
    /// The block shield's drawn ink: its icon merged with the block number's glyph ink when it shows one. The two nodes and
    /// the number's measurement are cached on the creature's state; the number's text is what changes them, and the shield's
    /// own place (which the native block slide tweens) is still read live.
    /// </summary>
    private static Rect2 ShieldInk(Control shield, CreatureDesktopState state)
    {
        Rect2? ink = null;
        TextureRect? icon = state.HpBlockIcon;
        if (icon == null || !GodotObject.IsInstanceValid(icon))
        {
            icon = shield.GetNodeOrNull<TextureRect>("BlockIcon");
            state.HpBlockIcon = icon;
        }
        if (icon != null && icon.IsVisibleInTree())
            ink = icon.GetGlobalRect();
        Label? blockLabel = state.HpBlockLabel;
        if (blockLabel == null || !GodotObject.IsInstanceValid(blockLabel))
        {
            blockLabel = shield.GetNodeOrNull<Label>("BlockLabel");
            state.HpBlockLabel = blockLabel;
        }
        if (blockLabel != null && blockLabel.IsVisibleInTree())
        {
            string blockText = blockLabel.Text;
            if (!string.IsNullOrWhiteSpace(blockText))
            {
                Rect2 number = blockLabel.GetGlobalTransform() *
                    LabelInkLocal(blockLabel, blockText, ref state.HpBlockInk);
                ink = ink is { } known ? known.Merge(number) : number;
            }
        }
        return ink ?? shield.GetGlobalRect();
    }

    /// <summary>
    /// The intents above a creature's head, at one size on the canvas whatever the body's scale: a row of several that
    /// would pass a side of the canvas is moved back in through its pivot (a pivot d units right of the centre moves the
    /// scaled row d * (scale - 1) left). Run only when the row changed (see last intent count/scale on the state).
    /// </summary>
    private static void LayoutCombatIntents(Control creature, Control intents, float creatureScale, float intentScale)
    {
        float pivotX = intents.Size.X * 0.5f;
        int shown = 0;
        foreach (Node intent in intents.GetChildren())
        {
            if (intent is Control { Visible: true } && !intent.IsQueuedForDeletion())
                shown++;
        }
        if (shown > 1 && intentScale > 1.05f)
        {
            float rowHalf = (shown * 64f - (shown - 1) * 10f) * IntentCanvasScale * 0.5f;
            float center = creature.GlobalPosition.X + (intents.Position.X + intents.Size.X * 0.5f) * creatureScale;
            float clamped = LeftEdgeOnCanvas(center, rowHalf * 2f) + rowHalf;
            pivotX += (center - clamped) / (creatureScale * (intentScale - 1f));
        }
        var intentPivot = new Vector2(pivotX, intents.Size.Y);
        if ((intents.PivotOffset - intentPivot).LengthSquared() > 0.25f)
            intents.PivotOffset = intentPivot;
        if (MathF.Abs(intents.Scale.X - intentScale) > 0.002f)
            intents.Scale = new Vector2(intentScale, intentScale);
    }

    /// <summary>The clearance a health bar keeps below its own foot line (the harness/collision margin is 2 units).</summary>
    internal const float BarBandClearance = 3f;

    /// <summary>
    /// A creature's own foot line: the line its slot assigned (<c>portrait_feet_y</c>), not the front row's and not the
    /// hit box bottom, which some scenes lay a few dozen units above the feet. Falls back to the body bottom, then to the
    /// front foot line before the creature has been placed.
    /// </summary>
    private static float OwnFeetLine(Control creature, CreatureDesktopState cState)
    {
        if (creature.HasMeta("portrait_feet_y"))
        {
            float feet = creature.GetMeta("portrait_feet_y").AsSingle();
            if (feet > 1f)
                return feet;
        }
        if (cState.Hitbox is { } hit && GodotObject.IsInstanceValid(hit))
        {
            Rect2 rect = hit.GetGlobalRect();
            if (rect.Size.Y > 1f && rect.End.Y > 1f)
                return rect.End.Y;
        }
        return 0f;
    }

    /// <summary>
    /// A creature's label pass, deferred until every creature of the room is placed so status blocks can see their
    /// neighbours. A struct: one is queued per creature on every frame of the fight.
    /// </summary>
    private readonly record struct LabelRequest(Control Creature, CreatureDesktopState State,
        float MaxBarWidth, bool IsAlly);

    /// <summary>
    /// Lays out the health displays and status blocks for every creature of the room. The blocks are planned first from the
    /// final hit boxes, then neighbours on the same status row (same rank and foot line, split at their midpoints) cap each
    /// other's overhang and a neighbour body standing in the block's own band is a hard edge, so the result does not depend
    /// on the order the creatures were laid out in.
    /// </summary>
    private void ApplyCreatureLabelsPortraitBatch(List<LabelRequest> requests, Control room, CombatReflection refl)
    {
        if (requests.Count == 0)
            return;

        // The planning collections are refilled, not reallocated: this batch runs on every frame of the fight.
        _statusBodyEpoch++;
        List<(LabelRequest Request, StatusIconLayout? Layout, int Rank, float RowY)> planned = _plannedLabels;
        planned.Clear();
        Dictionary<(int Rank, float RowY), List<int>> rows = _statusRows;
        rows.Clear();
        _statusRowListsUsed = 0;
        foreach (LabelRequest request in requests)
        {
            if (!GodotObject.IsInstanceValid(request.Creature))
                continue;
            StatusIconLayout? layout = null;
            if (request.State.PowerContainer is { } powers && GodotObject.IsInstanceValid(powers))
            {
                int count = CountVisiblePowers(powers);
                if (count > 0)
                    layout = ComputeStatusLayout(request.State.Hitbox, count);
            }

            // The row key is the foot line the stage assigned (portrait_feet_y), not the hit box bottom: two peers of
            // different native heights can carry hit boxes 50 units apart while standing on the same line, and a split
            // row silently skipped the neighbour limits between them.
            int rank = 0;
            float rowY = OwnFeetLine(request.Creature, request.State);
            if (_stage.TallSlot(room, request.Creature) is { } slot)
            {
                rank = slot.RowDepth;
                if (rowY <= 0f)
                    rowY = slot.CanvasFeetY;
            }
            else if (rowY <= 0f && request.State.Hitbox is { } hit && GodotObject.IsInstanceValid(hit))
            {
                Transform2D t = hit.GetGlobalTransformWithCanvas();
                rowY = (t * new Vector2(0f, hit.Size.Y)).Y;
            }
            rowY = MathF.Round(rowY / 16f) * 16f;
            planned.Add((request, layout, rank, rowY));
            if (layout is { Count: > 0 })
            {
                var key = (rank, rowY);
                if (!rows.TryGetValue(key, out List<int>? row))
                {
                    row = NextStatusRowIndexList();
                    rows[key] = row;
                }
                row.Add(planned.Count - 1);
            }
        }

        // Gaps and body edges are read from every creature's untouched base block before any block is written, so writing
        // one block never becomes the next block's limit (that order-dependence made this pass disagree with the power
        // add's own plan, and the icons already on screen moved for one frame when it caught up).
        Dictionary<ulong, StatusIconLayout> limited = _limitedStatus;
        limited.Clear();
        foreach (List<int> row in rows.Values)
        {
            List<StatusRowEntry> entries = _statusRowEntries;
            entries.Clear();
            foreach (int index in row)
            {
                (LabelRequest request, StatusIconLayout? layout, _, _) = planned[index];
                entries.Add(new StatusRowEntry(request.Creature, request.State, layout!.Value));
            }
            // PlanStatusRow fills _statusRowPlan; it is folded into `limited` before the next row asks for it.
            foreach ((StatusRowEntry entry, StatusIconLayout plannedLayout) in PlanStatusRow(entries))
                limited[entry.Creature.GetInstanceId()] = plannedLayout;
        }

        foreach ((LabelRequest request, StatusIconLayout? layout, _, _) in planned)
        {
            StatusIconLayout? plannedLayout = limited.TryGetValue(request.Creature.GetInstanceId(), out StatusIconLayout found)
                ? found
                : layout;
            ApplyCreatureLabelsPortrait(request.Creature, request.State, refl, request.MaxBarWidth, request.IsAlly, plannedLayout);
        }
    }

    /// <summary>One index list per status row, reused across frames (the dictionary that holds them is cleared first).</summary>
    private List<int> NextStatusRowIndexList()
    {
        if (_statusRowListsUsed == _statusRowIndexLists.Count)
            _statusRowIndexLists.Add(new List<int>());
        List<int> list = _statusRowIndexLists[_statusRowListsUsed++];
        list.Clear();
        return list;
    }

    /// <summary>One creature on a status row: its node and state, and the untouched base block the native rule gives it.</summary>
    private readonly record struct StatusRowEntry(Control Creature, CreatureDesktopState State, StatusIconLayout Base);

    /// <summary>
    /// Plans one status row: the base blocks are sorted once, every gap and every neighbour body is read from those
    /// untouched blocks, and only then are the limited blocks returned. Keeping the plan separate from the write is what
    /// makes the result order-independent — a limited block must never become the limit of the next one.
    /// </summary>
    private List<(StatusRowEntry Entry, StatusIconLayout Layout)> PlanStatusRow(List<StatusRowEntry> row)
    {
        // Refilled per row (see NextStatusRowIndexList): the caller folds it before the next row plans.
        List<(StatusRowEntry Entry, StatusIconLayout Layout)> planned = _statusRowPlan;
        planned.Clear();
        if (row.Count == 0)
            return planned;
        row.Sort((a, b) => a.Base.Left.CompareTo(b.Base.Left));
        for (int i = 0; i < row.Count; i++)
        {
            StatusRowEntry entry = row[i];
            float leftGap = i > 0
                ? entry.Base.Left - (row[i - 1].Base.Left + row[i - 1].Base.RowWidth)
                : float.PositiveInfinity;
            float rightGap = i + 1 < row.Count
                ? row[i + 1].Base.Left - (entry.Base.Left + entry.Base.RowWidth)
                : float.PositiveInfinity;
            if (entry.State.Hitbox is not { } hitbox || !GodotObject.IsInstanceValid(hitbox))
            {
                planned.Add((entry, entry.Base));
                continue;
            }
            Transform2D t = hitbox.GetGlobalTransformWithCanvas();
            Vector2 tl = t * Vector2.Zero;
            Vector2 br = t * hitbox.Size;
            float hitLeft = MathF.Min(tl.X, br.X);
            float hitWidth = MathF.Abs(br.X - tl.X);
            (float bodyLeft, float bodyRight) = StatusBodyEdges(entry);
            planned.Add((entry, StatusIconMath.ApplyNeighbourLimits(entry.Base, hitLeft, hitWidth,
                SafeAreaCanvas.PortraitWidth, leftGap, rightGap, bodyLeft, bodyRight)));
        }
        return planned;
    }

    /// <summary>
    /// The bodies standing in this block's own icon band, as the hard edges <see cref="StatusIconMath.ApplyNeighbourLimits"/>
    /// takes: <c>BodyLeft</c> is the nearest body edge on the block's right, <c>BodyRight</c> the nearest on its left. A body on
    /// another foot line counts too — a front- or back-rank body taller than its row reaches under this row's icons (the
    /// 9:19.5 enemy pair) — and an icon may never be drawn over it. The band is the power container's line plus the
    /// block's rows, the band the icons are drawn in.
    /// </summary>
    private (float BodyLeft, float BodyRight) StatusBodyEdges(StatusRowEntry entry)
    {
        float bodyLeft = float.PositiveInfinity;
        float bodyRight = float.NegativeInfinity;
        if (entry.State.PowerContainer is not { } container || !GodotObject.IsInstanceValid(container))
            return (bodyLeft, float.PositiveInfinity);
        float top = container.GetGlobalRect().Position.Y;
        float bottom = top + Math.Max(1f, entry.Base.Rows * entry.Base.IconWidth * 5f / 6f);
        float centre = entry.Base.Left + entry.Base.RowWidth * 0.5f;
        ulong selfId = entry.Creature.GetInstanceId();
        foreach ((ulong id, CreatureDesktopState other) in _creatures)
        {
            if (id == selfId)
                continue;
            if (GodotObject.InstanceFromId(id) is not Control node || !GodotObject.IsInstanceValid(node) ||
                !node.IsVisibleInTree())
                continue;
            if (other.Hitbox is not { } hitbox || !GodotObject.IsInstanceValid(hitbox))
                continue;
            Rect2 body = StatusBodyRect(id, hitbox);
            if (body.End.Y <= top || body.Position.Y >= bottom)
                continue;
            if (body.Position.X + body.Size.X * 0.5f >= centre)
                bodyLeft = MathF.Min(bodyLeft, body.Position.X);
            else
                bodyRight = MathF.Max(bodyRight, body.End.X);
        }
        return (bodyLeft, float.IsNegativeInfinity(bodyRight) ? float.PositiveInfinity : bodyRight);
    }

    /// <summary>
    /// A creature's hit box on the canvas, measured once per status-planning pass. One row entry asks for every other
    /// creature's rect, and a row with n blocks asks n times each: no game code runs while a pass plans, so the rects
    /// cannot move between the reads (the pass that writes them runs after the plan). The epoch is bumped where a plan
    /// begins — the deferred batch and a power add's own replan.
    /// </summary>
    private Rect2 StatusBodyRect(ulong id, Control hitbox)
    {
        if (_statusBodyRectsEpoch != _statusBodyEpoch)
        {
            _statusBodyRects.Clear();
            _statusBodyRectsEpoch = _statusBodyEpoch;
        }
        if (_statusBodyRects.TryGetValue(id, out Rect2 known))
            return known;
        Rect2 rect = hitbox.GetGlobalRect();
        _statusBodyRects[id] = rect;
        return rect;
    }

    /// <summary>Plans and writes one status row: the deferred pass's rows and a power add's own row both come here.</summary>
    private void ApplyStatusRow(List<StatusRowEntry> row, CombatReflection refl)
    {
        foreach ((StatusRowEntry entry, StatusIconLayout layout) in PlanStatusRow(row))
            ApplyStatusBlock(entry.Creature, entry.State, refl, layout, entry.Base.Count);
    }

    private static StatusIconLayout ComputeStatusLayout(Control? hitbox, int count)
    {
        if (hitbox == null || !GodotObject.IsInstanceValid(hitbox) || hitbox.Size.X < 1f)
            return StatusIconMath.Compute(count, SafeAreaCanvas.PortraitWidth * 0.5f, 200f, SafeAreaCanvas.PortraitWidth);
        Transform2D t = hitbox.GetGlobalTransformWithCanvas();
        Vector2 tl = t * Vector2.Zero;
        Vector2 br = t * hitbox.Size;
        float left = MathF.Min(tl.X, br.X);
        float width = MathF.Abs(br.X - tl.X);
        return StatusIconMath.Compute(count, left, width, SafeAreaCanvas.PortraitWidth);
    }

    /// <summary>
    /// Applies a planned status block to a creature's power container. The native layout call gets the planned left as the
    /// bounds X, so the container's <c>_originalPosition</c> is our position and the game's own <c>UpdatePositions</c> (on
    /// every power add) rewrites the same place instead of the canvas edge.
    /// </summary>
    private void ApplyStatusBlock(Control creature, CreatureDesktopState cState, CombatReflection refl,
        StatusIconLayout icons, int powerCount)
    {
        using (PortraitFrameTiming.Begin(PortraitFrameTiming.Status))
            ApplyStatusBlockCore(creature, cState, refl, icons, powerCount);
    }

    /// <summary>
    /// See <see cref="ApplyStatusBlock"/>. The container is only laid out again when the block it carries is not already
    /// the one this pass would write: the native layout is interpreted game code that repositions every icon, and a
    /// settled fight asks for the same block on every frame. Everything the native call reads is in the signature.
    /// </summary>
    private void ApplyStatusBlockCore(Control creature, CreatureDesktopState cState, CombatReflection refl,
        StatusIconLayout icons, int powerCount)
    {
        if (cState.PowerContainer is not { } powerContainer || !GodotObject.IsInstanceValid(powerContainer) ||
            cState.Hitbox is not { } hitbox || !GodotObject.IsInstanceValid(hitbox))
            return;
        if (cState.StatusBlockIsApplied(powerContainer, icons, powerCount))
            return;
        float creatureScale = creature.GetGlobalTransform().Scale.X;
        if (creatureScale < 0.05f || cState.Display is not { } display || !GodotObject.IsInstanceValid(display))
            return;
        float unit = creatureScale * display.Scale.X;
        float nativeIcon = PowerIconPitch;
        foreach (Node power in powerContainer.GetChildren())
        {
            if (power is Control { Visible: true } iconNode && iconNode.Size.X > 1f)
            {
                nativeIcon = iconNode.Size.X;
                break;
            }
        }
        Control? bounds = cState.EnsureBoundsStandIn(creature);
        if (bounds == null)
            return;
        bounds.Size = new Vector2(nativeIcon * icons.Columns - 25f, 8f);
        bounds.GlobalPosition = new Vector2(icons.Left, hitbox.GlobalPosition.Y);
        refl.LayOutPowerContainer(powerContainer, bounds);
        float iconScale = icons.IconWidth / (nativeIcon * unit);
        if (powerContainer.PivotOffset != Vector2.Zero)
            powerContainer.PivotOffset = Vector2.Zero;
        if (MathF.Abs(powerContainer.Scale.X - iconScale) > 0.002f)
            powerContainer.Scale = new Vector2(iconScale, iconScale);
        // The native layout centres only when the row is wider than the container it was given; here it is not, so this is
        // a no-op safety net rather than the place that positions the block.
        if (MathF.Abs(powerContainer.GlobalPosition.X - icons.Left) > 0.01f)
            powerContainer.GlobalPosition = new Vector2(icons.Left, powerContainer.GlobalPosition.Y);
        cState.RememberStatusBlock(powerContainer, icons, powerCount);
    }

    /// <summary>
    /// Connects one handler per power container. It runs synchronously while <c>NPowerContainer.Add</c> is adding the new
    /// icon (before that method's own <c>UpdatePositions</c>) and replans the whole status row through
    /// <see cref="ReapplyStatusRowNeighbours"/> — the same plan the deferred batch makes — so the add's final write, the
    /// icons already drawn beside it and the next pass all agree.
    /// </summary>
    private void EnsurePowerContainerWired(Control powerContainer, Control creature, CreatureDesktopState cState,
        CombatReflection refl)
    {
        ulong containerId = powerContainer.GetInstanceId();
        if (_wiredPowerContainers.ContainsKey(containerId))
            return;
        ulong creatureId = creature.GetInstanceId();
        Callable handler = Callable.From<Node>(_ =>
        {
            if (!SafeAreaCanvas.IsPortrait)
                return;
            if (GodotObject.InstanceFromId(creatureId) is not Control liveCreature ||
                !GodotObject.IsInstanceValid(liveCreature) ||
                !_creatures.TryGetValue(creatureId, out CreatureDesktopState? state))
                return;
            // An icon just appeared: the next labels pass must walk the row and enlarge its amount label (see
            // ApplyCreatureLabelsPortrait), even if the container's child count happens to be unchanged.
            state.PowerChildrenDirty = true;
            if (state.PowerContainer is not { } container || !GodotObject.IsInstanceValid(container))
                return;
            if (CountVisiblePowers(container) == 0 || state.Hitbox is not { } hitbox ||
                !GodotObject.IsInstanceValid(hitbox))
                return;
            // One plan for the whole row: replanning only this creature from cached gaps made the add's first frame differ
            // from the batch that follows it (a neighbour's limit moved without the neighbour being laid out again), and
            // the icons already on screen jumped by that difference.
            ReapplyStatusRowNeighbours(liveCreature, state, refl);
        });
        if (powerContainer.HasSignal("child_entered_tree"))
            powerContainer.Connect("child_entered_tree", handler);
        _wiredPowerContainers[containerId] = (powerContainer, handler);
    }

    /// <summary>
    /// Re-plans and re-applies every status block standing on the same row as <paramref name="creature"/> (its own foot
    /// line), because one block's size changes the gap the neighbours may overhang into. Called synchronously from a
    /// power add so every block on the row is drawn at its final geometry on that same frame. The plan uses the same
    /// inputs as <see cref="ApplyCreatureLabelsPortraitBatch"/>: every creature's untouched base block, the gaps between
    /// those blocks, and the neighbours' hit-box bodies as hard edges.
    /// </summary>
    private void ReapplyStatusRowNeighbours(Control creature, CreatureDesktopState state, CombatReflection refl)
    {
        if (SafeAreaCanvas.IsPortrait is false)
            return;
        _statusBodyEpoch++;
        float rowY = MathF.Round(OwnFeetLine(creature, state) / 16f) * 16f;
        var row = new List<StatusRowEntry>();
        foreach ((ulong id, CreatureDesktopState other) in _creatures)
        {
            if (other.PowerContainer is not { } container || !GodotObject.IsInstanceValid(container))
                continue;
            if (GodotObject.InstanceFromId(id) is not Control node || !GodotObject.IsInstanceValid(node))
                continue;
            float otherRow = MathF.Round(OwnFeetLine(node, other) / 16f) * 16f;
            if (otherRow != rowY)
                continue;
            int count = CountVisiblePowers(container);
            if (count == 0 || other.Hitbox is not { } hitbox || !GodotObject.IsInstanceValid(hitbox))
                continue;
            row.Add(new StatusRowEntry(node, other, ComputeStatusLayout(other.Hitbox, count)));
        }
        ApplyStatusRow(row, refl);
    }

    private static int CountVisiblePowers(Control powerContainer)
    {
        int count = 0;
        int children = powerContainer.GetChildCount();
        for (int i = 0; i < children; i++)
        {
            if (powerContainer.GetChild(i) is Control { Visible: true } icon && !icon.IsQueuedForDeletion())
                count++;
        }
        return count;
    }

    /// <summary>The left edge of something <paramref name="width"/> wide centred on <paramref name="centerX"/>, kept on the canvas.</summary>
    private static float LeftEdgeOnCanvas(float centerX, float width)
    {
        float half = width * 0.5f;
        float min = StageSideMargin + half;
        float max = SafeAreaCanvas.PortraitWidth - StageSideMargin - half;
        return (max < min ? SafeAreaCanvas.PortraitWidth * 0.5f : Math.Clamp(centerX, min, max)) - half;
    }

    private void CaptureCreatureHud(Control creature, Control? hitbox, CombatReflection refl) =>
        _creatures.Add(creature.GetInstanceId(), CreatureDesktopState.Capture(creature, hitbox, refl));

    private void QueueCreatureLabel(Control creature, float maxBarWidth, bool isAlly) =>
        _labelRequests.Add(new LabelRequest(creature, _creatures[creature.GetInstanceId()], maxBarWidth, isAlly));

    private CombatReflection EnsureReflection() => _reflection ??= new CombatReflection(typeof(NGame).Assembly);

    private sealed class CreatureDesktopState
    {
        private const string BoundsStandInName = "PortraitStateBounds";

        private readonly Vector2 _orbScale;
        private readonly Vector2 _displayScale, _displayPivot;
        private readonly Vector2 _intentScale, _intentPivot;
        private readonly Vector2 _powerScale, _powerPosition, _powerPivot, _powerSize;
        private readonly float _barHitboxLeft, _barHitboxRight;
        // The bar's own place inside the health display: the band rule shifts its global Y every frame.
        private readonly Vector2 _hpBarPosition;
        private Control? _boundsStandIn;
        /// <summary>The intent row's icon count and scale as last laid out (see LayoutCombatIntents).</summary>
        public int LastIntentCount = -1;
        public float LastIntentScale = -1f;
        public float LastIntentWidth = -1f;

        /// <summary>The power container's child count as last walked for amount-label enlarging, and whether its add signal
        /// fired since: the walk is a Godot array per creature per frame, and an icon can only appear with a count change
        /// or an add.</summary>
        public int LastPowerChildCount = -1;
        public bool PowerChildrenDirty;

        /// <summary>The status block last written to the power container; <see cref="StatusBlock.Applied"/> is false until
        /// the first write, and a block is only laid out again when the container no longer carries it.</summary>
        public StatusBlock AppliedStatus;

        /// <summary>
        /// The block-shield rule's cache: the HP label it rules, its native place, the shield and the shield's two nodes,
        /// the measured glyph ink of the digits and of the block number, and the last capped report. The nodes and the two
        /// measurements were looked up and shaped again on every frame of the fight; the bar, shield and label geometry is
        /// still read live, so a sliding shield is followed exactly.
        /// </summary>
        public Label? HpRuleLabel;
        public Control? HpBlockShield;
        public TextureRect? HpBlockIcon;
        public Label? HpBlockLabel;
        public float HpNativeX, HpNativeScale;
        public GlyphInk HpInk, HpBlockInk;
        public string? HpCapped;

        /// <summary>A label's glyph ink as last measured, keyed by the text, the label's rect and the theme font size (all
        /// exact): its advance at that size, its ascent, and the outline, placed by the label's alignment inside the rect.</summary>
        public struct GlyphInk
        {
            public string? Text;
            public Vector2 Box;
            public int FontSize;
            public Rect2 Ink;
            public bool Known;
        }

        /// <summary>What <c>NPowerContainer.SetCreatureBounds</c> and the icon scale were given, and what they left behind.</summary>
        public readonly record struct StatusBlock(float Left, float IconWidth, float ContainerScaleX, int Columns, int Count,
            bool Applied);

        /// <summary>Whether the container still carries exactly the block <paramref name="icons"/> describes.</summary>
        public bool StatusBlockIsApplied(Control powerContainer, StatusIconLayout icons, int powerCount)
        {
            StatusBlock applied = AppliedStatus;
            if (!applied.Applied || applied.Count != powerCount || applied.Columns != icons.Columns) return false;
            if (MathF.Abs(applied.Left - icons.Left) > 0.01f || MathF.Abs(applied.IconWidth - icons.IconWidth) > 0.01f)
                return false;
            if (!powerContainer.IsVisibleInTree()) return false;
            return MathF.Abs(powerContainer.Scale.X - applied.ContainerScaleX) <= 0.002f;
        }

        /// <summary>After the native layout call: what the container now carries.</summary>
        public void RememberStatusBlock(Control powerContainer, StatusIconLayout icons, int powerCount) =>
            AppliedStatus = new StatusBlock(icons.Left, icons.IconWidth, powerContainer.Scale.X, icons.Columns, powerCount,
                true);

        /// <summary>
        /// What the game adds to the hit box's width for this creature's health bar (<c>NHealthBar.UpdateLayoutForCreatureBounds</c>):
        /// nothing for a player, 24 for a monster less the monster's own reduction.
        /// </summary>
        public readonly float BarExtraWidth;

        /// <summary>The game's layout call failed for this creature: its health display stays as the game draws it.</summary>
        public bool StateDisplayLayoutFailed { get; private set; }

        /// <summary>The creature's parts drawn larger in portrait: its health display and what that holds, its hit box and its intents.</summary>
        public readonly Control? Display, Hitbox, Intents, Nameplate, HpBar, HpBarHitbox, PowerContainer;

        private CreatureDesktopState(Control creature, Control? hitbox, CombatReflection refl)
        {
            _orbScale = refl.CreatureOrbManagerProp?.GetValue(creature) is Control orbMgr ? orbMgr.Scale : Vector2.One;

            // In the creature's scene the health display (NCreatureStateDisplay) is the node named HealthBar; inside it the
            // bar itself (NHealthBar) has that name again.
            Display = creature.GetNodeOrNull<Control>("%HealthBar") ?? creature.GetNodeOrNull<Control>("HealthBar");
            Hitbox = hitbox;
            Intents = creature.GetNodeOrNull<Control>("%Intents") ?? creature.GetNodeOrNull<Control>("Intents");
            Control? healthBar = Display?.GetNodeOrNull<Control>("%HealthBar") ?? Display?.GetNodeOrNull<Control>("HealthBar");
            HpBar = healthBar?.GetNodeOrNull<Control>("%HpBarContainer") ?? healthBar?.GetNodeOrNull<Control>("HpBarContainer");
            HpBarHitbox = Display?.GetNodeOrNull<Control>("%HpBarHitbox") ?? healthBar?.GetNodeOrNull<Control>("HpBarHitbox");
            Nameplate = Display?.GetNodeOrNull<Control>("%NameplateContainer") ?? Display?.GetNodeOrNull<Control>("NameplateContainer");
            PowerContainer = Display?.GetNodeOrNull<Control>("%PowerContainer") ?? Display?.GetNodeOrNull<Control>("PowerContainer");

            _displayScale = Display?.Scale ?? Vector2.One;
            _displayPivot = Display?.PivotOffset ?? Vector2.Zero;
            _intentScale = Intents?.Scale ?? Vector2.One;
            _intentPivot = Intents?.PivotOffset ?? new Vector2(500f, 20f);
            _powerScale = PowerContainer?.Scale ?? Vector2.One;
            _powerPosition = PowerContainer?.Position ?? Vector2.Zero;
            _powerPivot = PowerContainer?.PivotOffset ?? Vector2.Zero;
            _powerSize = PowerContainer?.Size ?? Vector2.Zero;
            _barHitboxLeft = HpBarHitbox?.OffsetLeft ?? -138f;
            _barHitboxRight = HpBarHitbox?.OffsetRight ?? 120f;
            _hpBarPosition = HpBar?.Position ?? Vector2.Zero;
            BarExtraWidth = refl.GetHealthBarExtraWidth(creature);
        }

        public static CreatureDesktopState Capture(Control creature, Control? hitbox, CombatReflection refl) =>
            new(creature, hitbox, refl);

        /// <summary>
        /// The hidden control that stands in for the creature's hit box when the health display is laid out again: the
        /// game reads its left edge on the canvas and its width. A child of the creature, so it goes when the creature
        /// does; null while the creature cannot take a child yet.
        /// </summary>
        public Control? EnsureBoundsStandIn(Control creature)
        {
            if (_boundsStandIn is { } known && GodotObject.IsInstanceValid(known) && known.IsInsideTree())
                return known;
            if (!creature.IsInsideTree() || !creature.IsNodeReady())
                return null;

            var standIn = new Control
            {
                Name = BoundsStandInName,
                Visible = false,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                FocusMode = Control.FocusModeEnum.None,
            };
            creature.AddChild(standIn);
            if (!standIn.IsInsideTree())
            {
                standIn.Free();
                return null;
            }
            _boundsStandIn = standIn;
            return standIn;
        }

        public void GiveUpStateDisplay(Control creature, CombatReflection refl)
        {
            StateDisplayLayoutFailed = true;
            RestoreStateDisplay();
            try
            {
                refl.InvokeUpdateBounds(creature);
            }
            catch (Exception)
            {
                // The game's own layout fails for this creature too: nothing more to put back.
            }
        }

        private void RestoreStateDisplay()
        {
            if (Display != null && GodotObject.IsInstanceValid(Display))
            {
                Display.Scale = _displayScale;
                Display.PivotOffset = _displayPivot;
            }
            if (HpBar != null && GodotObject.IsInstanceValid(HpBar))
                HpBar.Position = _hpBarPosition;
            if (HpBarHitbox != null && GodotObject.IsInstanceValid(HpBarHitbox))
            {
                HpBarHitbox.OffsetLeft = _barHitboxLeft;
                HpBarHitbox.OffsetRight = _barHitboxRight;
            }
            if (PowerContainer != null && GodotObject.IsInstanceValid(PowerContainer))
            {
                PowerContainer.Scale = _powerScale;
                PowerContainer.PivotOffset = _powerPivot;
                PowerContainer.Size = _powerSize;
                PowerContainer.Position = _powerPosition;
            }
            if (_boundsStandIn != null && GodotObject.IsInstanceValid(_boundsStandIn))
                _boundsStandIn.QueueFree();
            _boundsStandIn = null;
        }

        public void Restore(Control creature, CombatReflection refl, PortraitCombatStage stage)
        {
            // The larger health display and intents first: the game lays the display out again from the hit box below.
            RestoreStateDisplay();
            if (Intents != null && GodotObject.IsInstanceValid(Intents))
            {
                Intents.Scale = _intentScale;
                Intents.PivotOffset = _intentPivot;
            }

            stage.RestoreCreatureRoot(creature);
            if (refl.CreatureOrbManagerProp?.GetValue(creature) is Control orbMgr)
            {
                RestoreOrbArcLandscape(orbMgr, refl);
                orbMgr.Scale = _orbScale;
            }
            // The block-shield rule may have moved and shrunk the HP number: the game's own bar gets its label back.
            if (HpBar is { } hpBar && GodotObject.IsInstanceValid(hpBar) &&
                hpBar.GetNodeOrNull<Label>("%HpLabel") is { } hpLabel)
            {
                if (hpLabel.HasMeta("portrait_hp_x"))
                {
                    hpLabel.Position = new Vector2(hpLabel.GetMeta("portrait_hp_x").AsSingle(), hpLabel.Position.Y);
                    hpLabel.RemoveMeta("portrait_hp_x");
                }
                if (hpLabel.HasMeta("portrait_hp_scale"))
                {
                    float hpScale = hpLabel.GetMeta("portrait_hp_scale").AsSingle();
                    hpLabel.Scale = new Vector2(hpScale, hpScale);
                    hpLabel.RemoveMeta("portrait_hp_scale");
                }
            }
            if (creature.HasMeta("portrait_hp_shift_capped"))
                creature.RemoveMeta("portrait_hp_shift_capped");
            // The stage's foot assignment is portrait bookkeeping (the harness reads it there): landscape must not carry it.
            if (creature.HasMeta("portrait_feet_y"))
                creature.RemoveMeta("portrait_feet_y");
            refl.InvokeUpdateBounds(creature);
        }
    }

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
