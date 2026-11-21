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
                RestoreCardEffectsWithoutPass(refl);
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
                    {
                        CorrectRailTweenPortrait(ui, room, refl, hud);
                        ForceNewlyVisiblePiles(ui, refl, hud);
                    }
                    using (PortraitFrameTiming.Begin(PortraitFrameTiming.Hand))
                        SyncHandAndPlayabilityPortrait(hand, refl, bands);
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
    /// Puts back what the hand and playability passes may have taken away when they no longer run (the fight ended, the room
    /// or its UI is gone, the hand is not there): a card's glow and shadow, an unplayable body's dim, a playable highlight's
    /// boost. A card keeps its node through the pool, and the pool resets neither, so without this the card would be reused
    /// later with no glow or shadow.
    /// </summary>
    private void RestoreCardEffectsWithoutPass(CombatReflection refl)
    {
        if (_reducedCardEffects.Count > 0)
            RestoreAllReducedCardEffects();
        if (_dimmedCardChildren.Count > 0 || _boostedHighlights.Count > 0)
            RestoreAllDimmedCards(refl);
    }

    /// <summary>
    /// Lays every player's orb queue out again in the frame that is drawn: the game lays its own arc out from the creature's
    /// bounds after the stage pass (<c>NCreature.SetOrbManagerPosition</c> and <c>NOrbManager.TweenLayout</c>) and does so
    /// again whenever a slot, a channel or an evoke changes the queue, so the phone's arc has to be the queue's last writer.
    /// </summary>
    private void RefitOrbArcs(Control room, CombatReflection refl)
    {
        if (!_orbsOnStage || _lastBands is not { } bands || refl.RoomCreatureNodesField?.GetValue(room) is not IEnumerable creatures)
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

    /// <summary>
    /// The rail places a native show/hide tween can move after the full pass: the energy counter's own slide, the End Turn
    /// button, the three piles, and the star, which hangs off the counter's live rect. Nothing here runs on a frame where no
    /// tween moved: each place is only written when it differs.
    /// </summary>
    private void CorrectRailTweenPortrait(Control ui, Control room, CombatReflection refl, PortraitHudLayout hud)
    {
        Control? energyCounter = refl.EnergyCounterField?.GetValue(ui) as Control;
        if (energyCounter != null && GodotObject.IsInstanceValid(energyCounter))
        {
            if (energyCounter.Position != Vector2.Zero)
                energyCounter.Position = Vector2.Zero;
            if (refl.StarCounterField?.GetValue(ui) is Control starCounter && starCounter.IsVisibleInTree())
                PlaceStarCounter(starCounter, energyCounter);
        }

        if ((ui.GetNodeOrNull<Control>("%EndTurnButton") ?? ui.GetNodeOrNull<Control>("EndTurnButton")) is { } endTurn &&
            GodotObject.IsInstanceValid(endTurn))
        {
            if (refl.EndTurnPosTweenField?.GetValue(endTurn) is Tween posTween)
            {
                if (posTween.IsRunning()) posTween.Kill();
                refl.EndTurnPosTweenField.SetValue(endTurn, null);
            }
            Vector2 target = EndTurnRailPosition(endTurn, refl, hud);
            if (endTurn.Position != target)
                endTurn.Position = target;
        }

        if ((ui.GetNodeOrNull<Control>("%PingButton") ?? ui.GetNodeOrNull<Control>("PingButton")) is { } pingBtn &&
            GodotObject.IsInstanceValid(pingBtn) && pingBtn.Visible)
        {
            int pingState = Convert.ToInt32(refl.PingStateField?.GetValue(pingBtn) ?? 2);
            if (refl.PingPosTweenField?.GetValue(pingBtn) is Tween pingTween)
            {
                if (pingTween.IsRunning()) pingTween.Kill();
                refl.PingPosTweenField.SetValue(pingBtn, null);
            }
            Vector2 target = new(pingState == 2 ? 1140f : hud.PingShowX, hud.PingShowY);
            if (pingBtn.Position != target)
                pingBtn.Position = target;
        }

        if ((room.GetNodeOrNull<Control>("%ProceedButton") ?? room.GetNodeOrNull<Control>("ProceedButton")) is { } proceedBtn &&
            GodotObject.IsInstanceValid(proceedBtn))
        {
            bool enabled = (bool)(refl.ButtonIsEnabledProp?.GetValue(proceedBtn) ?? false);
            if (refl.ProceedAnimTweenField?.GetValue(proceedBtn) is Tween procTween)
            {
                if (procTween.IsRunning()) procTween.Kill();
                refl.ProceedAnimTweenField.SetValue(proceedBtn, null);
            }
            float procW = Math.Max(200f, proceedBtn.Size.X);
            Vector2 target = new(enabled ? hud.EndTurnShowX + 110f - procW * 0.5f : 1140f, hud.EndTurnShowY);
            if (proceedBtn.Position != target)
                proceedBtn.Position = target;
        }

        if ((ui.GetNodeOrNull<Control>("%CombatPileContainer") ?? ui.GetNodeOrNull<Control>("CombatPileContainer")) is { } pileContainer &&
            GodotObject.IsInstanceValid(pileContainer))
        {
            CorrectPile(pileContainer, refl, "%DrawPile", "DrawPile",
                new Vector2(hud.DrawShowX, hud.DrawShowY), new Vector2(hud.DrawHideX, hud.DrawHideY), hud.PileScale);
            CorrectPile(pileContainer, refl, "%DiscardPile", "DiscardPile",
                new Vector2(hud.DiscardShowX, hud.DiscardShowY), new Vector2(hud.DiscardHideX, hud.DiscardHideY), hud.PileScale);
            CorrectPile(pileContainer, refl, "%ExhaustPile", "ExhaustPile",
                new Vector2(hud.ExhaustShowX, hud.ExhaustShowY), new Vector2(hud.ExhaustHideX, hud.ExhaustShowY), hud.PileScale);
        }
    }

    private static void CorrectPile(Control pileContainer, CombatReflection refl, string unique, string name,
        Vector2 showPos, Vector2 hidePos, float scale)
    {
        if ((pileContainer.GetNodeOrNull<Control>(unique) ?? pileContainer.GetNodeOrNull<Control>(name)) is not { } pile ||
            !GodotObject.IsInstanceValid(pile) || !pile.IsVisibleInTree())
            return;
        LayoutPileButton(pile, refl, showPos, hidePos, scale);
    }

    /// <summary>
    /// The End Turn button's place on the rail: its own x while the fight runs, the hidden x once it is over (the Proceed
    /// button takes its place), clamped so the scaled button stays on the canvas.
    /// </summary>
    private static Vector2 EndTurnRailPosition(Control endTurn, CombatReflection refl, PortraitHudLayout hud)
    {
        float showX = Math.Min(hud.EndTurnShowX, SafeAreaCanvas.PortraitWidth - HudEdgeMargin - 110f * (1f + hud.EndTurnScale));
        int btnState = Convert.ToInt32(refl.EndTurnStateField?.GetValue(endTurn) ?? 2);
        return new Vector2(btnState == 2 && refl.IsCombatOver() ? hud.EndTurnHideX : showX, hud.EndTurnShowY);
    }

    /// <summary>
    /// The star's place from the energy counter's live rect: native size and TopLeft anchors, hung on the orb's
    /// lower-left corner. The native size is captured once per star node, before any of our writes. The target is computed
    /// first and each property is only written when it differs, so a settled star costs a rect read and no layout
    /// invalidation (the node is the counter's last child, so it already draws in front of the orb and the "3/3").
    /// </summary>
    private void PlaceStarCounter(Control starCounter, Control energyCounter)
    {
        ulong starId = starCounter.GetInstanceId();
        if (!_starNativeSizes.TryGetValue(starId, out Vector2 nativeSize))
            _starNativeSizes[starId] = nativeSize = starCounter.Size;
        Rect2 orb = energyCounter.GetGlobalRect();
        Vector2 span = nativeSize * starCounter.GetGlobalTransform().Scale.Abs();
        (float centerX, float centerY) = PortraitCombatMath.StarBadgeCenter(
            orb.GetCenter().X, orb.GetCenter().Y, orb.Size.X, orb.Size.Y);
        var target = new Vector2(centerX, centerY) - span * 0.5f;

        if (starCounter.AnchorLeft != 0f || starCounter.AnchorTop != 0f ||
            starCounter.AnchorRight != 0f || starCounter.AnchorBottom != 0f)
            starCounter.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        if (starCounter.Size != nativeSize)
            starCounter.Size = nativeSize;
        if (starCounter.GlobalPosition != target)
            starCounter.GlobalPosition = target;
    }

    /// <summary>
    /// A pile that first becomes visible after combat start (normally exhaust) gets the same entrance treatment as the
    /// initial draw/discard piles: no native desktop tween, placed on its rail show position from its first drawn frame.
    /// Later AnimOut tweens are left alone once the pile has been seen.
    /// </summary>
    private void ForceNewlyVisiblePiles(Control ui, CombatReflection refl, PortraitHudLayout hud)
    {
        if ((ui.GetNodeOrNull<Control>("%CombatPileContainer") ?? ui.GetNodeOrNull<Control>("CombatPileContainer")) is not { } pileContainer)
            return;
        Check(pileContainer.GetNodeOrNull<Control>("%DrawPile") ?? pileContainer.GetNodeOrNull<Control>("DrawPile"),
            new Vector2(hud.DrawShowX, hud.DrawShowY));
        Check(pileContainer.GetNodeOrNull<Control>("%DiscardPile") ?? pileContainer.GetNodeOrNull<Control>("DiscardPile"),
            new Vector2(hud.DiscardShowX, hud.DiscardShowY));
        Check(pileContainer.GetNodeOrNull<Control>("%ExhaustPile") ?? pileContainer.GetNodeOrNull<Control>("ExhaustPile"),
            new Vector2(hud.ExhaustShowX, hud.ExhaustShowY));

        void Check(Control? pile, Vector2 show)
        {
            if (pile == null)
                return;
            ulong id = pile.GetInstanceId();
            bool visible = pile.IsVisibleInTree();
            if (!visible)
            {
                _railVisiblePiles.Remove(id);
                return;
            }
            if (!_railVisiblePiles.Add(id))
                return;
            if (refl.PilePosTweenField?.GetValue(pile) is Tween tween)
            {
                if (tween.IsRunning()) tween.Kill();
                refl.PilePosTweenField.SetValue(pile, null);
            }
            refl.PileShowPosField?.SetValue(pile, show);
            refl.PileHidePosField?.SetValue(pile, show);
            pile.Position = show;
        }
    }

    public PortraitCombatLayout()
    {
        // The last chance before a frame is drawn: native AnimIn/target steps for End Turn, energy and the piles can
        // run after the normal process pass, so the per-draw rail correction belongs here.
        RenderingServer.FramePreDraw += OnFramePreDraw;
    }

    /// <summary>Takes the pre-draw correction out of the rendering server when the phone host is detached.</summary>
    public void Detach() => RenderingServer.FramePreDraw -= OnFramePreDraw;

    public const string CombatRoomTypeName = "MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom";
    private const string ConfirmButtonTypeName = "MegaCrit.Sts2.Core.Nodes.CommonUi.NConfirmButton";

    /// <summary>
    /// The Graphics "Flat Hand" choice: false fans the hand at every shape, true gives flat rows. It only
    /// changes the row shape, never the bands, card size or selection layout; PhoneGraphics writes it before the next
    /// frame and the test host may set it directly.
    /// </summary>
    private static bool _flatHand;
    private static Action? _flatHandChanged;

    public static bool FlatHand
    {
        get => _flatHand;
        set
        {
            if (_flatHand == value) return;
            _flatHand = value;
            _flatHandChanged?.Invoke();
        }
    }

    /// <summary>Called by PortraitLayout so a static mode change relayouts immediately, not on some later frame.</summary>
    internal static void SetFlatHandListener(Action? listener) => _flatHandChanged = listener;

    /// <summary>
    /// The Graphics "Reduce Effects" choice, for the hand: a card still on its way to its slot draws without its glow
    /// (the <c>NCardHighlight</c> shader quad over the whole card) and without its shadow, and the glow's tween — the
    /// game's <c>AnimShow</c>, which writes the shader width on every frame of it — is stopped rather than run for half a
    /// second on every drawn card. The moment the card rests in its slot it gets all of it back, at the width the game
    /// would have left: the playable boost for a card that can be played, the game's own 0.075 for a gold or red glow,
    /// 0 for nothing. PhoneGraphics writes it before the next hand pass and the test host may set it directly; the pass
    /// runs every frame of a draw and after any change, so it takes effect on the next frame.
    /// </summary>
    public static bool ReduceEffects { get; set; }

    // Whether the hand pass last saw a draw's batch open or a holder moving, and the frame it saw it on; see HandMoving.
    private static bool _handMoving;
    private static ulong _handMovingFrame;
    private static CombatReflection? _activityReflection;

    /// <summary>
    /// Whether hand cards are moving right now: a draw's batch is open, or a holder has left the transform the last pass
    /// drew it at (a card gliding in, the row re-centring, a card settling). The hand pass sets it; the value lapses two
    /// frames after the pass last saw motion, so it cannot stay true after the hand, the fight or portrait mode goes
    /// away. Engine reads only, and the frame governor asks once a frame.
    /// </summary>
    public static bool HandMoving
    {
        get
        {
            ulong now = Engine.GetProcessFrames();
            return _handMoving && now >= _handMovingFrame && now - _handMovingFrame <= 2;
        }
    }

    /// <summary>
    /// Whether the game is animating something the player is waiting on, for the frame-rate governor's idle cap: the hand
    /// is moving (<see cref="HandMoving"/>), or a fight is running with player actions disabled — the enemy turn, a card
    /// resolving, the turn-start draw (the game disables actions when End Turn is pressed and only enables them after the
    /// turn's start hooks). The fight half is the game's own state, so it holds in landscape too; the two reads are
    /// cached members of one reflection instance, built on the first ask.
    /// </summary>
    public static bool CombatAnimating
    {
        get
        {
            if (HandMoving) return true;
            CombatReflection refl = _activityReflection ??= new CombatReflection(typeof(NGame).Assembly);
            return refl.IsCombatInProgress() && refl.PlayerActionsDisabled();
        }
    }

    private static readonly Color PlayableHighlightColor = new(
        PortraitCombatMath.PlayableHighlightRgba.R,
        PortraitCombatMath.PlayableHighlightRgba.G,
        PortraitCombatMath.PlayableHighlightRgba.B,
        PortraitCombatMath.PlayableHighlightRgba.A);

    private static readonly Color UnaffordableCostColor = new(
        PortraitCombatMath.UnaffordableCostRgba.R,
        PortraitCombatMath.UnaffordableCostRgba.G,
        PortraitCombatMath.UnaffordableCostRgba.B,
        PortraitCombatMath.UnaffordableCostRgba.A);

    private static readonly HashSet<StringName> SkippedCardBodyChildren = new()
    {
        "Shadow",
        "Highlight",
        "EnergyIcon",
        "StarIcon",
        "CardVfxContainer",
        "CardSparkles",
    };

    // Touch targets: the End Turn button (220 by 90 in the scene) and the pile buttons (80 by 80) are drawn at the
    // scales in PortraitHudLayout, large enough for the 44-point side a finger needs (PortraitBands.MinTarget).
    private const float HudEdgeMargin = 16f;

    // Text: a creature is drawn at 0.42 to 0.94 of its size to fit the stage, which left its health, block and
    // name text (24 units), power amounts (18) and intent values (22) at about 6 points. The health display and the
    // intents are drawn at one fixed size on the canvas instead, whatever the creature's own scale: 24 units become 11
    // points at 1.33, 22 units at 1.45, and power amounts are set in 24 units.
    private const float StateDisplayCanvasScale = 1.33f;
    private const float IntentCanvasScale = 1.45f;
    // The clearance the HP text keeps after the block shield and inside the bar's right end, and the smallest the digits
    // may shrink to when a bar is too narrow for the shield and the text together. The floor is low because the rule is
    // hard (the digits never pass the bar's end): the worst real case is the fixture's 4/4-digit hero HP on a 176 bar,
    // which needs 0.62, while real HP numbers are far shorter.
    private const float HpTextClearance = 4f;
    private const float HpTextMinScale = 0.50f;
    // The health display's own HUD box (the bar plus the HP value under it, the box the stage checks measure) is this
    // tall, so a bar band placed BarBandClearance under a creature's foot line ends this far below those feet.
    private const float EnemyHudBoxHeight = 41.23f;
    // The game draws an enemy's intent row over the top of its sprite: on the egg row (bodies at the legibility floor) the
    // row's top stands this far above the body's top (measured on the 9:16 Ovicopter fight).
    private const float EggIntentRise = 54.4f;
    // The flyer's own intent row stands this far above its body top at the approved 9:16 frame (scale 0.612), so reserving
    // this instead of the full IntentBandHeight keeps the row off the header without shrinking the body further than the
    // bar clearance already needs.
    private const float OviIntentRise = 64f;
    private const int PowerAmountFontSize = 24;
    private const int HandIndexFontSize = 44;

    /// <summary>
    /// The most consecutive frames the hand may go without a full pass. Everything the pass corrects is signalled, this is
    /// only the belt-and-braces net for a native write we do not have a signal for; the stillness records stay fresh while
    /// it runs, so a card that is moving keeps the pass running every frame anyway.
    /// </summary>
    private const long HandIdlePassFrames = 30;

    /// <summary>The game's hand limit (<c>CardPile.MaxCardsInHand</c>).</summary>
    private const int HandCardLimit = 10;

    /// <summary>The largest single frame counted into the draw's clock, so one load hitch cannot end a draw early.</summary>
    private const double HandDrawMaxFrameMsec = 100.0;

    /// <summary>What an enemy's intents take above its head at <see cref="IntentCanvasScale"/> (64 units each, and a gap).</summary>
    private const float IntentBandHeight = 64f * IntentCanvasScale + 12f;

    // A lone enemy may use the stage from just right of the player to the canvas's edge, kept a
    // fifth of the width from the player (PortraitCombatMath.EnemyStageLeft), never left of this. With enemies on both sides the player stands in the middle and keeps this much either side of it.
    private const float LoneEnemyLeft = 360f;
    private const float SurroundedPlayerHalf = 160f;
    private const float SurroundedSideGap = 100f;

    private const float MinHealthBarWidth = 150f;
    private const float MinNameplateWidth = 250f;
    private const float StageSideMargin = 8f;
    private const float PowerIconPitch = 48f;

    private readonly Dictionary<ulong, CombatRoomState> _rooms = new();
    private readonly Dictionary<ulong, CreatureDesktopState> _creatures = new();
    private readonly Dictionary<ulong, int> _enlargedFonts = new();
    // Power icons whose amount label already got its portrait size (a power keeps its icon node for its whole life).
    private readonly HashSet<ulong> _enlargedPowerAmounts = new();
    private readonly Dictionary<ulong, Dictionary<ulong, Color>> _dimmedCardChildren = new();
    private readonly HashSet<ulong> _boostedHighlights = new();
    // Hand cards whose glow and shadow ReduceEffects took away while they move, keyed by the card node, with what they
    // were before, so the pass can put them back at rest (a card keeps its node for its whole life). The list is
    // refilled per pass for the cards that left the hand.
    private readonly Dictionary<ulong, ReducedCardEffects> _reducedCardEffects = new();
    private readonly List<ulong> _reduceRestoreIds = new();
    private readonly HashSet<ulong> _liveReducedCardIds = new();
    private bool _lastReduceEffects;
    // The last frame any holder moved; the backstop bound that restores reduced cards (see ReducedHandQuietFrames).
    private long _handLastMotionFrame = long.MinValue / 4;
    private readonly HashSet<ulong> _wiredHolders = new();
    private readonly HashSet<ulong> _wiredSelectionHands = new();
    private readonly HashSet<ulong> _wiredSelectionContainers = new();
    private readonly HashSet<ulong> _wiredSelectedHolders = new();
    private readonly HashSet<ulong> _wiredHolderRemovals = new();
    private readonly HashSet<ulong> _wiredHandContainers = new();
    private readonly HashSet<ulong> _wiredCombatUis = new();
    private readonly HashSet<ulong> _railEntranceRooms = new();
    private readonly HashSet<ulong> _railVisiblePiles = new();
    private readonly Dictionary<ulong, Vector2> _starNativeSizes = new();
    private readonly Dictionary<ulong, PlayabilityEntry> _playability = new();
    private bool _handDrawBatch;
    private int _handCountLock;
    private int _lastHandHolderCount;
    private int _lastPlayerTurnNumber;
    private int _turnDrawCount = -1;
    private bool _turnDrawInnate;
    private bool _turnDrawPending;
    private double _handBatchMsec;
    private double _handAddIntervalMsec;
    private bool _handDirty;
    private long _handLastPassFrame;
    private bool _lastInCardPlay;
    private ulong _lastPressedHolder;
    private PortraitCombatBands? _lastHandBands;
    private bool _lastHandFlat;
    // The open draw batch is a turn's draw with a known size (set by the turn-number block): only such a draw is
    // direction-capped and waits TurnDrawQuietMsec for its next card; other batches (a card effect's draw, a refill of an
    // emptied hand) follow the native animation and the adaptive quiet window.
    private bool _turnDrawBatch;
    private PortraitCombatBands? _lastBands;
    // Whether any player on the stage has an orb queue at all: the pre-draw orb refit has nothing to do otherwise.
    private bool _orbsOnStage;
    private List<LabelRequest>? _pendingLabels;
    // The frame's label requests, refilled rather than allocated every frame (the batch only reads the list).
    private readonly List<LabelRequest> _labelRequests = new();
    // The hand pass's per-pass collections are refilled rather than allocated: the pass runs every drawn frame of a draw.
    private readonly List<Control> _activeHolders = new();
    private readonly List<Control> _allHoldersInHand = new();
    private readonly HashSet<ulong> _activeDimmedCardIds = new();
    // The cards leaving the dimmed set, refilled per pass (the pass runs on every drawn frame of a draw).
    private readonly List<ulong> _restoreCardIds = new();
    // One argument array for every reflective setter call (the boxed value still allocates; the array does not).
    private readonly object[] _oneArg = new object[1];
    // The few node lookups the hand and selection passes make on every pass: a holder's hand-index label, and the hand's
    // selected-card container (both remembered while they stay in the tree).
    private readonly Dictionary<ulong, Label> _handIndexLabels = new();
    private readonly Dictionary<ulong, Control> _selectionContainerCache = new();
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
    private CombatRoomState? _pendingRoomState;
    private readonly Dictionary<ulong, (Control Container, Callable Handler)> _wiredPowerContainers = new();
    private float _lastCanvasHeight;
    // The room's combat UI and hand, remembered while they stay in the tree (see Remembered).
    private Control? _cachedUi;
    private Control? _cachedHand;
    private bool _selectionLayoutFailed;
    private bool _selectionLayoutFailureLogged;
    private readonly HandStillness _stillness = new();
    // Each card node's body, highlight and the two icons the playability visuals write, resolved once per card node instead
    // of a reflection read and a node-path lookup per card per pass (the hand pass runs on every drawn frame of a draw).
    private sealed class CardVisualNodes
    {
        public Control? Body;
        public Control? Highlight;
        public CanvasItem? EnergyIcon;
        public CanvasItem? StarIcon;
    }
    private readonly Dictionary<ulong, CardVisualNodes> _cardVisualNodes = new();
    // The hand's draw-pile node, remembered while it stays in the tree (the first pass of each drawn card starts there).
    private Control? _drawPileNode;
    // The fight's one-shot resources, loaded ahead of the frames that would load them (see CombatAssetWarmup).
    private readonly CombatAssetWarmup _warmup = new();
    // The stage pass's per-frame lists and its copy of the ally slots, refilled rather than allocated: the pass runs every
    // frame of the fight, and six lists plus an array per frame is most of what this layout allocates.
    private readonly List<Control> _stagePlayers = new();
    private readonly List<Control> _stagePets = new();
    private readonly List<Control> _stageEnemies = new();
    private readonly List<Control> _stageLeft = new();
    private readonly List<Control> _stageAllies = new();
    private PortraitCreatureSlot[] _stageAllySlots = [];
    private readonly Dictionary<ulong, Control.MouseFilterEnum> _endTurnFilters = new();
    private readonly Dictionary<ulong, ControlDesktopState> _selectionControls = new();
    private readonly Dictionary<ulong, SelectionHolderEase> _selectionHolderEase = new();
    private readonly Dictionary<ulong, (Vector2 Position, Vector2 Scale)> _selectionHolderOriginals = new();
    private ulong _selectionSession;
    private long _selectionFrame = -1;
    private PortraitSelectedCardsLayout _selectionTarget;
    private int _selectionTargetCount = -1;
    private bool _selectionTargetReady;
    private float _selectionCenterY;
    private float _selectionPromptBottom;
    private readonly Dictionary<ulong, (Vector2 Position, Vector2 Scale)> _bossArmOriginals = new();
    private bool _crabArmFitFrozen;
    private CombatReflection? _reflection;

    private const string KaiserCrabBackgroundTypeName = "MegaCrit.Sts2.Core.Nodes.Vfx.Backgrounds.NKaiserCrabBossBackground";
    private const string BackdropNodeName = "PortraitCombatBackdrop";
    private const string ForegroundVisibleMeta = "portrait_foreground_visible";
    // The Underdocks deck's plank line sits 320 units from the container origin in the authored 1296-tall layer art, i.e.
    // 861 units above the art's bottom. Measured from the art so any Underdocks layer background keeps its own plank line.
    private const float PlankFromBottomFraction = 861f / 1296f;
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
        // The reduce restore writes each card the width the game would show at rest; the dim restore resets boosted widths
        // to the game's own 0.075 and clears the boost set, so it must run first or a playable card keeps the portrait boost.
        RestoreAllReducedCardEffects();
        RestoreAllDimmedCards(refl);
        RestoreEndTurnFilters();
        RestoreSelectionUi();
        RestoreBackgroundBosses();
        _warmup.Stop();

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
                state.Restore(creature, refl);
        }
        _creatures.Clear();

        foreach ((ulong containerId, (Control container, Callable handler)) in _wiredPowerContainers)
        {
            if (GodotObject.InstanceFromId(containerId) is Control live && GodotObject.IsInstanceValid(live) &&
                live.HasSignal("child_entered_tree"))
                live.Disconnect("child_entered_tree", handler);
        }
        _wiredPowerContainers.Clear();

        foreach ((ulong roomId, CombatRoomState roomState) in _rooms)
        {
            if (GodotObject.InstanceFromId(roomId) is Control room && GodotObject.IsInstanceValid(room))
                roomState.Restore(room, refl);
        }
        _rooms.Clear();
        _failedRooms.Clear();
        // The rail and hand bookkeeping belongs to the fight that just ended: a later portrait session in the same room
        // must force the piles' entrance and place every pile again, and a returning card must start from the draw pile
        // rather than be capped against where it stood before the leave.
        _railEntranceRooms.Clear();
        _railVisiblePiles.Clear();
        ResetHandSession();
        _stillness.Clear();
    }

    /// <summary>
    /// Forgets the draw and turn bookkeeping of the hand session that is ending, so the next portrait session reads its
    /// first draw from scratch: a stale count lock would hold the row at the old size, a stale turn number would swallow
    /// the new turn's draw, and stale "last" inputs would suppress the first pass's work.
    /// </summary>
    private void ResetHandSession()
    {
        _handDrawBatch = false;
        _turnDrawBatch = false;
        _handCountLock = 0;
        _handAddIntervalMsec = 0.0;
        _handBatchMsec = 0.0;
        _turnDrawCount = -1;
        _turnDrawInnate = false;
        _turnDrawPending = false;
        _lastHandHolderCount = 0;
        _lastPlayerTurnNumber = 0;
        _lastHandBands = null;
        _lastHandFlat = _flatHand;
        _lastInCardPlay = false;
        _lastPressedHolder = 0;
        _lastReduceEffects = false;
        _handLastMotionFrame = long.MinValue / 4;
        _handDirty = true;
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

        dead.Clear();
        foreach (ulong id in _dimmedCardChildren.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _dimmedCardChildren.Remove(id);

        dead.Clear();
        foreach (ulong id in _wiredHolders)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _wiredHolders.Remove(id);

        dead.Clear();
        foreach (ulong id in _boostedHighlights)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _boostedHighlights.Remove(id);

        dead.Clear();
        foreach (ulong id in _reducedCardEffects.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _reducedCardEffects.Remove(id);

        dead.Clear();
        foreach (ulong id in _enlargedFonts.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _enlargedFonts.Remove(id);

        dead.Clear();
        foreach (ulong id in _enlargedPowerAmounts)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _enlargedPowerAmounts.Remove(id);

        dead.Clear();
        foreach (ulong id in _endTurnFilters.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _endTurnFilters.Remove(id);

        dead.Clear();
        foreach (ulong id in _selectionControls.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _selectionControls.Remove(id);

        dead.Clear();
        foreach (ulong id in _wiredSelectionHands)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _wiredSelectionHands.Remove(id);

        dead.Clear();
        foreach (ulong id in _wiredSelectionContainers)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _wiredSelectionContainers.Remove(id);

        dead.Clear();
        foreach (ulong id in _wiredSelectedHolders)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _wiredSelectedHolders.Remove(id);

        dead.Clear();
        foreach (ulong id in _wiredHolderRemovals)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _wiredHolderRemovals.Remove(id);

        dead.Clear();
        foreach (ulong id in _wiredHandContainers)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _wiredHandContainers.Remove(id);

        dead.Clear();
        foreach (ulong id in _wiredCombatUis)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _wiredCombatUis.Remove(id);

        dead.Clear();
        foreach (ulong id in _wiredPowerContainers.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _wiredPowerContainers.Remove(id);

        dead.Clear();
        foreach (ulong id in _wiredVfxContainers)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _wiredVfxContainers.Remove(id);

        dead.Clear();
        foreach (ulong id in _railEntranceRooms)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _railEntranceRooms.Remove(id);

        dead.Clear();
        foreach (ulong id in _railVisiblePiles)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _railVisiblePiles.Remove(id);

        dead.Clear();
        foreach (ulong id in _selectionHolderEase.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _selectionHolderEase.Remove(id);

        dead.Clear();
        foreach (ulong id in _selectionHolderOriginals.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _selectionHolderOriginals.Remove(id);

        dead.Clear();
        foreach (ulong id in _bossArmOriginals.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _bossArmOriginals.Remove(id);

        dead.Clear();
        foreach (ulong id in _starNativeSizes.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _starNativeSizes.Remove(id);

        dead.Clear();
        foreach (ulong id in _playability.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _playability.Remove(id);

        dead.Clear();
        foreach (ulong id in _handIndexLabels.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _handIndexLabels.Remove(id);

        dead.Clear();
        foreach (ulong id in _selectionContainerCache.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _selectionContainerCache.Remove(id);

        dead.Clear();
        foreach (ulong id in _statusBodyRects.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _statusBodyRects.Remove(id);

        dead.Clear();
        foreach (ulong id in _cardVisualNodes.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _cardVisualNodes.Remove(id);
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

    /// <summary>
    /// A holder's hand-index label, remembered while it stays valid: the hand pass asks for it every time it runs (every
    /// drawn frame of a draw) and each <c>%</c> path resolves through the unique-name map.
    /// </summary>
    private Label? HandIndexLabel(Control holder)
    {
        ulong id = holder.GetInstanceId();
        if (_handIndexLabels.TryGetValue(id, out Label? known) && GodotObject.IsInstanceValid(known))
            return known;
        Label? found = holder.GetNodeOrNull<Label>("%HandIndex") ?? holder.GetNodeOrNull<Label>("HandIndex");
        if (found == null)
            return null;
        _handIndexLabels[id] = found;
        return found;
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
        CombatRoomState state = EnsureRoomState(room, refl);

        Control? ui = Remembered(room, "CombatUi", ref _cachedUi);
        if (ui != null)
        {
            WireCombatUi(room, ui, roomId);
            ui.AnchorLeft = 0f;
            ui.AnchorTop = 0f;
            ui.AnchorRight = 1f;
            ui.AnchorBottom = 1f;
            ui.OffsetLeft = 0f;
            ui.OffsetTop = 0f;
            ui.OffsetRight = 0f;
            ui.OffsetBottom = 0f;
        }

        Control? hand = ui != null ? Remembered(ui, "Hand", ref _cachedHand) : null;
        // Load the fight's lazily loaded resources (card frames, power icons, orb assets, sounds) while the room is still on
        // its entrance, through the game's own threaded loader, so no card visual or status pays for them later.
        _warmup.Tick(room, hand, refl);

        // The bands depend only on the canvas and the run bars, so the rail, the hand and the stage all stay exactly
        // where they are whatever the hand's size: drawing or playing a card cannot move End Turn or the creatures.
        float stageTopY = capHeight + PortraitBands.RunContentTop;
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, stageTopY);
        _lastBands = bands;
        _lastCanvasHeight = canvasHeight;
        // The hand is laid out once per frame, in the pre-draw pass: that is after every native write of the frame (the
        // Add's two layout passes, focus snaps) and before the frame is drawn, so a drawn frame is always the corrected
        // one, and the motion cap gets exactly one allowance per frame.
        PortraitHudLayout hud = PortraitCombatMath.ComputeHudLayout(canvasHeight, bands);

        if (ui != null)
        {
            LayoutCombatHudPortrait(ui, room, refl, hud);
            // The first frame a room is laid out also settles the piles' entrance: kill any native corner tween and put
            // the show/hide targets on the rail before NCombatUi.AnimIn can run its slide.
            if (_railEntranceRooms.Add(roomId))
                ForcePileEntrance(ui, refl, hud);
            KeepEndTurnUnlitWhileHolding(ui, hand, refl);
        }
        if (hand != null)
            using (PortraitFrameTiming.Begin(PortraitFrameTiming.Selection))
                LayoutHandSelectionPortrait(hand, refl, bands, canvasHeight);

        _labelRequests.Clear();
        _pendingLabels = _labelRequests;
        using (PortraitFrameTiming.Begin(PortraitFrameTiming.Stage))
            LayoutCombatStagePortrait(room, refl, state, bands, capHeight, canvasHeight);
        List<LabelRequest> pending = _pendingLabels;
        _pendingLabels = null;
        _pendingRoomState = null;
        using (PortraitFrameTiming.Begin(PortraitFrameTiming.Labels))
            ApplyCreatureLabelsPortraitBatch(pending, refl);
    }

    private CombatRoomState EnsureRoomState(Control room, CombatReflection refl)
    {
        ulong roomId = room.GetInstanceId();
        if (!_rooms.TryGetValue(roomId, out CombatRoomState? state))
        {
            state = CombatRoomState.Capture(room, refl);
            _rooms[roomId] = state;
        }
        return state;
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

    /// <summary>
    /// NCombatUi.Activate sets Visible=true and immediately calls AnimIn; the piles' AnimIn starts at their native
    /// desktop show/hide targets. VisibilityChanged fires between those two lines, so this handler puts the whole rail on
    /// its portrait places before the entrance tween is created and makes the piles' first entrance target the rail.
    /// </summary>
    private void WireCombatUi(Control room, Control ui, ulong roomId)
    {
        if (!_wiredCombatUis.Add(ui.GetInstanceId()))
            return;
        if (ui.HasSignal("visibility_changed"))
            ui.Connect("visibility_changed", Callable.From(() => OnCombatUiVisibilityChanged(roomId)));
    }

    private void OnCombatUiVisibilityChanged(ulong roomId)
    {
        if (!SafeAreaCanvas.IsPortrait || GodotObject.InstanceFromId(roomId) is not Control room ||
            !GodotObject.IsInstanceValid(room) || !room.IsInsideTree())
            return;
        Control? ui = room.GetNodeOrNull<Control>("%CombatUi") ?? room.GetNodeOrNull<Control>("CombatUi");
        if (ui == null || !ui.Visible || !ui.IsVisibleInTree())
            return;

        CombatReflection refl = EnsureReflection();
        EnsureRoomState(room, refl);
        float canvasHeight = Math.Max(room.GetViewport().GetVisibleRect().Size.Y, SafeAreaCanvas.MinPortraitHeight);
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, stageTopY: 0f);
        PortraitHudLayout hud = PortraitCombatMath.ComputeHudLayout(canvasHeight, bands);
        LayoutCombatHudPortrait(ui, room, refl, hud);
        _railEntranceRooms.Add(roomId);
        ForcePileEntrance(ui, refl, hud);
    }

    /// <summary>
    /// Stops the native pile entrance from using its desktop corner targets: kill the running position tween, point both
    /// show and hide at the rail show position (so AnimIn starts and ends there), and place the pile on the rail now.
    /// </summary>
    private static void ForcePileEntrance(Control ui, CombatReflection refl, PortraitHudLayout hud)
    {
        if ((ui.GetNodeOrNull<Control>("%CombatPileContainer") ?? ui.GetNodeOrNull<Control>("CombatPileContainer")) is not { } pileContainer)
            return;

        void Force(Control? pile, Vector2 show)
        {
            if (pile == null)
                return;
            if (refl.PilePosTweenField?.GetValue(pile) is Tween tween)
            {
                if (tween.IsRunning())
                    tween.Kill();
                refl.PilePosTweenField.SetValue(pile, null);
            }
            refl.PileShowPosField?.SetValue(pile, show);
            refl.PileHidePosField?.SetValue(pile, show);
            pile.Position = show;
        }

        Force(pileContainer.GetNodeOrNull<Control>("%DrawPile") ?? pileContainer.GetNodeOrNull<Control>("DrawPile"),
            new Vector2(hud.DrawShowX, hud.DrawShowY));
        Force(pileContainer.GetNodeOrNull<Control>("%DiscardPile") ?? pileContainer.GetNodeOrNull<Control>("DiscardPile"),
            new Vector2(hud.DiscardShowX, hud.DiscardShowY));
        Force(pileContainer.GetNodeOrNull<Control>("%ExhaustPile") ?? pileContainer.GetNodeOrNull<Control>("ExhaustPile"),
            new Vector2(hud.ExhaustShowX, hud.ExhaustShowY));
    }

    /// <summary>
    /// In portrait End Turn sits in the hand's band, so a card carried toward an enemy passes
    /// over it, and the button lit up (its hover: brighter, its label red, the playable cards flashed) as if letting go there
    /// would end the turn, which it never does while a card is held. While a card is held the button takes no pointer, so it
    /// cannot light; its filter is put back as soon as the card is played or put back, and when portrait is left.
    /// </summary>
    private void KeepEndTurnUnlitWhileHolding(Control ui, Control? hand, CombatReflection refl)
    {
        if ((ui.GetNodeOrNull<Control>("%EndTurnButton") ?? ui.GetNodeOrNull<Control>("EndTurnButton")) is not { } endTurn)
            return;
        bool holding = hand != null && (bool)(refl.HandInCardPlayProp?.GetValue(hand) ?? false);
        ulong id = endTurn.GetInstanceId();
        if (holding)
        {
            if (endTurn.MouseFilter != Control.MouseFilterEnum.Ignore)
            {
                _endTurnFilters[id] = endTurn.MouseFilter;
                endTurn.MouseFilter = Control.MouseFilterEnum.Ignore;
            }
        }
        else if (_endTurnFilters.Remove(id, out Control.MouseFilterEnum filter))
        {
            endTurn.MouseFilter = filter;
        }
    }

    private void RestoreEndTurnFilters()
    {
        foreach ((ulong id, Control.MouseFilterEnum filter) in _endTurnFilters)
        {
            if (GodotObject.InstanceFromId(id) is Control endTurn && GodotObject.IsInstanceValid(endTurn))
                endTurn.MouseFilter = filter;
        }
        _endTurnFilters.Clear();
    }

    private void SyncHandAndPlayabilityPortrait(Control? hand, CombatReflection refl, PortraitCombatBands bands)
    {
        if (hand == null)
            return;

        Control? container = hand.GetNodeOrNull<Control>("%CardHolderContainer") ?? hand.GetNodeOrNull<Control>("CardHolderContainer");
        if (container == null)
            return;

        bool combatInProgress = refl.IsCombatInProgress();
        bool inCardPlay = (bool)(refl.HandInCardPlayProp?.GetValue(hand) ?? false);
        object? focusedObj = refl.HandFocusedHolderProp?.GetValue(hand);

        // The pass's lists are refilled rather than allocated: the pass runs on every drawn frame of a draw.
        List<Control> activeHolders = _activeHolders;
        List<Control> allHoldersInHand = _allHoldersInHand;
        activeHolders.Clear();
        allHoldersInHand.Clear();
        int holderChildren = container.GetChildCount();
        for (int i = 0; i < holderChildren; i++)
        {
            if (container.GetChild(i) is Control holder && !holder.IsQueuedForDeletion() &&
                holder.GetType().FullName == "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder")
            {
                allHoldersInHand.Add(holder);
                if (holder.IsVisibleInTree())
                    activeHolders.Add(holder);
            }
        }

        // Also check any holder temporarily reparented onto NPlayerHand during drag.
        Control? draggedHolder = null;
        int handChildren = hand.GetChildCount();
        for (int i = 0; i < handChildren; i++)
        {
            if (hand.GetChild(i) is Control holder && !holder.IsQueuedForDeletion() &&
                holder.GetType().FullName == "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder")
            {
                draggedHolder = holder;
                break;
            }
        }

        bool holding = inCardPlay && draggedHolder != null;
        // The hand always reserves two rows; only its count changes. A held card keeps its slot empty, so the rest of the
        // hand stays still: every touch picks a card up, and closing the gap then opening it again when the card went back
        // made the whole hand bounce.
        int effectiveCount = PortraitCombatMath.EffectiveCardCount(activeHolders.Count, holding);
        int heldIndex = holding && refl.HandDraggedIndexField?.GetValue(hand) is int dragged ? Math.Clamp(dragged, 0, activeHolders.Count) : -1;

        // The card under a finger is the one that grows. The pickup gate suppresses the native hover/focus on a touch
        // press, so the touch pass's pressed holder comes first and the native focus is only the fallback (mouse,
        // controller or a game-driven focus). While the hover itself belongs to a touch it is not a fallback at all: the
        // engine parks its emulated mouse under the released finger, so the game reports the card there as focused for a
        // frame after a cancel and lays the whole hand out around it, which is the one-frame snapshot when a card is
        // released on top of another.
        int focusedIndex = -1;
        if (PhoneTouchCards.PressedHolder is { } pressedHolder)
            focusedIndex = activeHolders.IndexOf(pressedHolder);
        if (focusedIndex < 0 && !PhoneTouchCards.HoverFromTouch && focusedObj is Control focusedControl)
            focusedIndex = activeHolders.IndexOf(focusedControl);
        int focusedSlotIndex = focusedIndex >= 0 ? PortraitCombatMath.SlotIndexBesideHeldCard(focusedIndex, heldIndex) : -1;

        // The draw's clock runs on process time, the same clock the pile tween uses, so a slow device stretches the gap
        // between cards and the wait with it; one load hitch is capped so it cannot end the draw early.
        double frameMsec = Math.Min(hand.GetProcessDeltaTime() * 1000.0, HandDrawMaxFrameMsec);
        _handBatchMsec += frameMsec;
        int playerTurn = refl.PlayerTurnNumber(hand);
        if (playerTurn > 0 && playerTurn != _lastPlayerTurnNumber)
        {
            // A new player turn. The holder count keeps last frame's value (it is refreshed every frame, before the pass
            // gate), so the turn draw's first card reads as an increase over the cards retained from the last turn; any batch
            // still open belongs to the old turn, and the game's own hand-draw query gives this turn's size (see
            // TurnDrawCount for why it can only under-count; the innate bump is added at the first card, when the hand and
            // the draw pile hold exactly that draw's innate cards).
            _handDrawBatch = false;
            _turnDrawBatch = false;
            _handCountLock = 0;
            _turnDrawCount = refl.TurnDrawCount(hand);
            _turnDrawInnate = playerTurn == 1;
            _turnDrawPending = true;
        }
        if (activeHolders.Count > _lastHandHolderCount)
        {
            // A card arrived. A turn draw's size is laid out from its first card, so the centred row only moves a card left
            // as the count grows and no card is ever drawn right of where it will rest; anything else uses the live count.
            // The pending turn is consumed here whatever state the previous batch was in, so a batch left open across the
            // turn boundary cannot swallow it.
            bool startingBatch = !_handDrawBatch;
            if (startingBatch)
            {
                _handDrawBatch = true;
                _handAddIntervalMsec = 0.0;
                _handCountLock = 0;
                _turnDrawBatch = false;
            }
            else if (_handBatchMsec > 1.0)
            {
                // The gap the draw is delivering at, which is what a slow device's frames stretch.
                _handAddIntervalMsec = _handBatchMsec;
            }
            if (_turnDrawPending)
            {
                _turnDrawPending = false;
                int expected = _turnDrawCount;
                if (_turnDrawInnate && refl.InnateCards(hand, activeHolders) is int innate && innate > expected)
                    expected = innate;
                _turnDrawInnate = false;
                if (expected < 0)
                    expected = refl.HandDrawCount();
                _handCountLock = Math.Clamp(_lastHandHolderCount + expected, 0, HandCardLimit);
                // A size is known from here on: the batch becomes a turn draw's, which means the long patience below
                // (a slow device delivers its cards far apart) and the direction lock in CapArrivalMotion.
                _turnDrawBatch = _handCountLock > 0;
            }
            _handBatchMsec = 0.0;
        }
        _lastPlayerTurnNumber = playerTurn;
        _lastHandHolderCount = activeHolders.Count;
        if (activeHolders.Count == 0)
        {
            _handDrawBatch = false;
            _turnDrawBatch = false;
            _handCountLock = 0;
            _handAddIntervalMsec = 0.0;
        }
        int handCount = heldIndex >= 0 ? effectiveCount : Math.Max(activeHolders.Count, _handCountLock);

        // Whether anything in the hand is in motion this frame: a draw's batch is open, or a holder left the transform the
        // last pass drew it at. The pass gate needs it, and so does the frame-rate governor, which must not cap a draw to
        // 30 fps (see HandMoving); the check is engine reads only.
        long frame = (long)Engine.GetProcessFrames();
        // Motion the game applied before this pass (a native step, the hand's own slide). The pass's own writes below —
        // the settled pin especially — move cards too, so HandMoving is published at the end of the loop from both.
        bool handMovedBeforePass = false;
        foreach (Control holder in activeHolders)
        {
            if (!_stillness.Moved(holder))
                continue;
            handMovedBeforePass = true;
            break;
        }

        // Everything below is reflective work per card, and the game only re-lays the hand out after an add, a focus, a
        // selection or a card-play change, all of which are signals or one cheap field. So the pass runs when one of those
        // is dirty, while the hand moves (a draw's cap and the settle need every frame), and at least every
        // HandIdlePassFrames.
        ulong pressed = PhoneTouchCards.PressedHolder?.GetInstanceId() ?? 0UL;
        bool needsPass = _handDirty
            || _handDrawBatch
            || handMovedBeforePass
            // A card whose effects are still reduced needs the first frame it rests on, so it gets them back then, not on
            // the next idle pass.
            || _reducedCardEffects.Count > 0
            || FlatHand != _lastHandFlat
            || ReduceEffects != _lastReduceEffects
            || inCardPlay != _lastInCardPlay
            || pressed != _lastPressedHolder
            || _lastHandBands is not { } lastBands || !lastBands.Equals(bands)
            || frame - _handLastPassFrame >= HandIdlePassFrames;
        PortraitFrameTiming.SetCards(activeHolders.Count);
        PortraitFrameTiming.SetDrawing(_handDrawBatch);
        if (!needsPass)
            return;
        _handDirty = false;
        _handLastPassFrame = frame;
        _lastInCardPlay = inCardPlay;
        _lastPressedHolder = pressed;
        _lastHandBands = bands;
        _lastHandFlat = FlatHand;
        _lastReduceEffects = ReduceEffects;

        HashSet<ulong> activeDimmedCardIds = _activeDimmedCardIds;
        activeDimmedCardIds.Clear();
        bool anyShiftedThisFrame = false;
        // The card nodes this pass laid out: a reduced entry whose card is not here (the node was replaced, pooled, its
        // holder was skipped, or it left the hand) belongs to no card on screen and is put back in the sweep below.
        HashSet<ulong> liveCards = _liveReducedCardIds;
        liveCards.Clear();

        // The two state inputs a card's playability depends on, read once for the whole hand.
        (int energy, int stars) = refl.PlayerResources(hand);
        bool actionsDisabled = refl.PlayerActionsDisabled();

        WireSelectionUi(hand);
        WireHandHolderContainer(container, hand.GetInstanceId());
        foreach (Control holder in allHoldersInHand)
        {
            WireHolderFocus(holder, hand);
            WireHolderRemoval(holder, hand.GetInstanceId());
            // The card's number in the hand: 32 units are 11 points at the smallest tall hand scale, so the label is 44.
            EnlargeLabelFont(HandIndexLabel(holder), HandIndexFontSize);
        }

        for (int i = 0; i < activeHolders.Count; i++)
        {
            Control holder = activeHolders[i];
            // The dragged card's position, scale, alpha and Z belong to the touch pass; the hand only lays out the rest.
            if (holder == draggedHolder)
                continue;
            Control? cardNode = refl.CardHolderCardNodeProp?.GetValue(holder) as Control;
            bool playable = PlayableFor(holder, refl, cardNode, energy, stars, actionsDisabled, activeHolders.Count,
                combatInProgress);

            int slotIndex = PortraitCombatMath.SlotIndexBesideHeldCard(i, heldIndex);
            PortraitCardSlot slot = PortraitCombatMath.ComputeHandCardSlot(handCount, slotIndex, focusedSlotIndex, bands, FlatHand);

            bool isFocused = focusedIndex == i;
            bool interactive = isFocused || PhoneTouchCards.CardIsBeingDragged ||
                PhoneTouchCards.PressedHolder == holder;
            var slotPosition = new Vector2(slot.LocalX, slot.LocalY);

            // A card's first pass is its arrival: the game has just created the holder at the draw pile and stepped it once
            // toward its desktop target. Put it back at the pile, at its resting size, so the first drawn frame is the card
            // it is; every later pass only ever caps what one frame may do.
            if (!_stillness.HasRecord(holder))
            {
                if (DrawPileLocalPoint(hand, container) is { } pilePoint)
                    holder.Position = pilePoint;
                holder.Scale = new Vector2(slot.Scale, slot.Scale);
                holder.RotationDegrees = slot.AngleDegrees;
            }

            // The game's own focus branch snaps the focused card's Y and scale, and nudges its neighbours, before this pass
            // runs; put every card back where the last pass left it, so none of those instant writes is ever drawn. The
            // focused card then only grows in place: its centre, row, angle and neighbours do not move.
            Vector2 currentTargetPos = (Vector2)(refl.HolderTargetPosField?.GetValue(holder) ?? holder.Position);
            Vector2 currentTargetScale = (Vector2)(refl.HolderTargetScaleField?.GetValue(holder) ?? holder.Scale);
            float currentTargetAngle = (float)(refl.HolderTargetAngleField?.GetValue(holder) ?? holder.RotationDegrees);
            _stillness.RestoreIfRetargeted(holder, slotPosition, currentTargetPos, currentTargetScale, currentTargetAngle);
            ApplyHolderTargets(holder, refl, slot, isFocused, currentTargetPos, currentTargetScale, currentTargetAngle);
            // A holder the game stopped short of its slot (a native target cancelled by a layout pass leaves it where it
            // was) has no animation left to carry it; restart the one step it needs. A card that is simply travelling is
            // never touched: it moved since the last pass.
            if (_stillness.IsStalled(holder, slotPosition) && refl.HolderSetTargetPosMethod is { } setTargetPos)
            {
                _oneArg[0] = slotPosition;
                setTargetPos.Invoke(holder, _oneArg);
            }
            // The transform a card rests at in this slot: the focused card grows straight (0°), every other card takes the
            // slot's tilt. The settled pin below holds exactly this, and the record stores it.
            float restingAngle = isFocused ? 0f : slot.AngleDegrees;
            CapArrivalMotion(holder, slot, slotPosition, hand, refl, interactive, restingAngle);
            // Whether this frame moved the holder at all (a native step, the pin above, a press): read before the record
            // below, which then stores where the frame drew it. The layout's own pin counts, which is why the hand's motion
            // for the governor is taken from these, not from the pre-pass state only.
            bool holderMoved = _stillness.Moved(holder);
            // Position only: what an observer watching the hand sees travel, and what the frame cap and the reset of a
            // card's effects follow (a scale or angle animation is not the card going anywhere).
            bool holderShifted = _stillness.MovedOnScreen(holder);
            anyShiftedThisFrame |= holderShifted;
            _stillness.Record(holder, slotPosition, new Vector2(slot.Scale, slot.Scale), restingAngle);
            // A card rests when this frame neither moved it nor found it off its slot: the frame it lands still counts as
            // in flight (it moved), the frame after it does not. Anything short of that is in flight: the frame a draw
            // arrives on (at the draw pile) and the glide toward its slot. Only Reduce Effects reads it (the pass runs on
            // every drawn frame of a draw), so with the setting off the check is skipped.
            bool resting = !ReduceEffects || (!holderMoved &&
                (holder.Position - slotPosition).LengthSquared() <= 1f &&
                MathF.Abs(holder.Scale.X - slot.Scale) <= 0.01f &&
                MathF.Abs(holder.RotationDegrees - restingAngle) <= 0.5f);

            if (cardNode != null)
            {
                liveCards.Add(cardNode.GetInstanceId());
                ApplyCardPlayabilityVisuals(holder, cardNode, refl, combatInProgress, playable, activeDimmedCardIds, resting,
                    holderShifted, frame);
            }
        }

        // Publish the frame's motion once the layout has had its say: a draw's batch, a native step or the pass's own pin
        // all count, so the frame-rate governor never caps a frame the player sees a card moving on. The last motion frame
        // is the backstop that puts a card's effects back even if one holder's own signal never settles.
        if (anyShiftedThisFrame) _handLastMotionFrame = frame;
        _handMoving = _handDrawBatch || anyShiftedThisFrame;
        _handMovingFrame = (ulong)frame;
        // A draw's first frame is already past the process step, where the frame cap was last decided and could not yet
        // see the hand move; decide again now, with it moving, so not even that frame is held at the idle rate.
        if (_handMoving) PhoneFrameCap.Apply();

        // The batch closes on the count it was waiting for, or when the draw stops arriving: a turn draw with a known size
        // gets the long patience, so a slow device's gaps between its cards never end it early (only a draw that comes up
        // short, e.g. the pile ran out, does); every other batch keeps the adaptive quiet window, which follows the gap the
        // draw is actually delivering at.
        if (_handDrawBatch && (_handCountLock > 0 && activeHolders.Count >= _handCountLock
                || _handBatchMsec > PortraitCombatMath.DrawQuietWindowMsec((float)_handAddIntervalMsec, _turnDrawBatch)))
        {
            // The hand reached the draw size, or the draw stopped arriving. A hand that ended up smaller than the draw size
            // is centred now, from the left, which is the only direction it can move: nothing is ever sent right of where
            // it rests.
            _handDrawBatch = false;
            _turnDrawBatch = false;
            _handCountLock = 0;
            _handAddIntervalMsec = 0.0;
        }

        // Undim any previously dimmed card that left the active unplayable hand set (played, discarded, pooled, or now affordable).
        if (_dimmedCardChildren.Count > 0)
        {
            var restoreIds = _restoreCardIds;
            restoreIds.Clear();
            foreach (ulong cardId in _dimmedCardChildren.Keys)
            {
                if (!activeDimmedCardIds.Contains(cardId))
                    restoreIds.Add(cardId);
            }
            foreach (ulong cardId in restoreIds)
            {
                if (GodotObject.InstanceFromId(cardId) is Control card && GodotObject.IsInstanceValid(card))
                    RestoreSingleCardVisuals(card, refl);
                else
                    _dimmedCardChildren.Remove(cardId);
            }
        }

        // A reduced card that left the hand mid-draw gets its glow and shadow back before it is drawn anywhere else.
        RestoreReducedEffectsOfLostCards();
    }

    /// <summary>
    /// A card's playability, from the game's own <c>CanPlay</c> only when the inputs that decide it changed: the model, the
    /// player's energy and stars, whether actions are locked, the hand size and the fight. On a settled hand nothing calls
    /// into the game's hook walk at all.
    /// </summary>
    private bool PlayableFor(Control holder, CombatReflection refl, Control? cardNode, int energy, int stars,
        bool actionsDisabled, int handCount, bool combatInProgress)
    {
        if (!combatInProgress)
            return true;
        object? model = refl.CardModelOf(cardNode);
        ulong id = holder.GetInstanceId();
        if (_playability.TryGetValue(id, out PlayabilityEntry entry) && ReferenceEquals(entry.Model, model) &&
            entry.Energy == energy && entry.Stars == stars && entry.ActionsDisabled == actionsDisabled &&
            entry.HandCount == handCount)
            return entry.Playable;
        bool playable = refl.CanCardPlay(model);
        _playability[id] = new PlayabilityEntry
        {
            Model = model,
            Energy = energy,
            Stars = stars,
            ActionsDisabled = actionsDisabled,
            HandCount = handCount,
            Playable = playable,
        };
        return playable;
    }

    private struct PlayabilityEntry
    {
        public object? Model;
        public int Energy;
        public int Stars;
        public bool ActionsDisabled;
        public int HandCount;
        public bool Playable;
    }

    /// <summary>
    /// The draw pile's point in the holder container's coordinates: where the game starts a drawn card, taken from the pile
    /// node the player sees. Only asked for on a holder's first pass.
    /// </summary>
    private Vector2? DrawPileLocalPoint(Control hand, Control container)
    {
        Control? pile = _drawPileNode;
        if (pile == null || !GodotObject.IsInstanceValid(pile) || !pile.IsInsideTree())
        {
            Control? ui = hand.GetParent() as Control;
            Control? piles = ui?.GetNodeOrNull<Control>("%CombatPileContainer")
                ?? ui?.GetNodeOrNull<Control>("CombatPileContainer");
            _drawPileNode = pile = piles?.GetNodeOrNull<Control>("%DrawPile") ?? piles?.GetNodeOrNull<Control>("DrawPile");
        }
        if (pile == null)
            return null;
        return container.GetGlobalTransform().AffineInverse() * pile.GetGlobalRect().GetCenter();
    }

    /// <summary>
    /// Applies <see cref="PortraitCombatMath.CapCardArrival"/>: the pure rule decides where one frame may draw the card, and
    /// only what differs is written (a set on the same float still dirties the transform and re-pins the card).
    /// </summary>
    private void CapArrivalMotion(Control holder, PortraitCardSlot slot, Vector2 slotPosition, Control hand,
        CombatReflection refl, bool interactive, float restingAngle)
    {
        Vector2 previous = _stillness.PreviousPosition(holder, holder.Position);
        PortraitCombatMath.PortraitCardMotion motion = PortraitCombatMath.CapCardArrival(
            holder.Position.X, holder.Position.Y, previous.X, previous.Y,
            MathF.Max(holder.Scale.X, holder.Scale.Y), holder.RotationDegrees,
            slotPosition.X, slotPosition.Y, slot.Scale, slot.AngleDegrees, restingAngle,
            _stillness.PreviousRotation(holder, holder.RotationDegrees),
            interactive, _turnDrawBatch, (float)hand.GetProcessDeltaTime());
        if (motion.ScaleCapped || motion.Settled)
        {
            var restScale = new Vector2(motion.Scale, motion.Scale);
            if (holder.Scale != restScale)
                holder.Scale = restScale;
            if (motion.ScaleCapped)
                refl.HolderTargetScaleField?.SetValue(holder, restScale);
        }
        var target = new Vector2(motion.X, motion.Y);
        if (holder.Position != target)
            holder.Position = target;
        if (holder.RotationDegrees != motion.RotationDegrees)
            holder.RotationDegrees = motion.RotationDegrees;
    }

    /// <summary>
    /// Guarded entry point for the selection layout. A failure is logged once and the pass is skipped for the rest of the
    /// selection, instead of throwing (and flooding the log) on every frame; the next selection starts fresh.
    /// </summary>
    private void LayoutHandSelectionPortrait(Control hand, CombatReflection refl, PortraitCombatBands bands, float canvasHeight)
    {
        bool selecting = (bool)(refl.HandInCardSelectionProp?.GetValue(hand) ?? false);
        if (!selecting)
            _selectionLayoutFailed = false;
        if (_selectionLayoutFailed)
            return;

        try
        {
            LayoutHandSelectionPortraitCore(hand, refl, bands, canvasHeight);
        }
        catch (Exception error)
        {
            _selectionLayoutFailed = true;
            if (!_selectionLayoutFailureLogged)
            {
                _selectionLayoutFailureLogged = true;
                GD.PrintErr("Portrait selection layout failed; leaving its nodes as they are: ", error.Message);
            }
            try { RestoreSelectionUi(); } catch (Exception restoreError) { GD.PrintErr("Portrait selection restore failed: ", restoreError.Message); }
        }
    }

    /// <summary>
    /// The game's in-hand selection (Armaments, Burning Pact, Purity and the whole-hand choice) keeps its native nodes and
    /// gestures; only their portrait places are set. The Confirm sits at the hand-control rail's right end as one large
    /// button over the inactive discard pile, the upgrade preview shrinks to sit between the prompt and the rail, and the
    /// selected-card previews are arranged in one or two rows above it. Everything is put back when the selection ends or
    /// portrait is left.
    /// </summary>
    private void LayoutHandSelectionPortraitCore(Control hand, CombatReflection refl, PortraitCombatBands bands, float canvasHeight)
    {
        bool selecting = (bool)(refl.HandInCardSelectionProp?.GetValue(hand) ?? false);
        if (!selecting)
        {
            RestoreSelectionUi();
            return;
        }

        Control? confirm = hand.GetNodeOrNull<Control>("%SelectModeConfirmButton") ?? hand.GetNodeOrNull<Control>("SelectModeConfirmButton");
        Control? preview = hand.GetNodeOrNull<Control>("%UpgradePreview") ?? hand.GetNodeOrNull<Control>("UpgradePreview");
        Control? previewContainer = hand.GetNodeOrNull<Control>("%UpgradePreviewContainer") ?? hand.GetNodeOrNull<Control>("UpgradePreviewContainer");
        Control? selected = hand.GetNodeOrNull<Control>("%SelectedHandCardContainer") ?? hand.GetNodeOrNull<Control>("SelectedHandCardContainer");

        float promptBottom = SelectionPromptBottom(hand);
        PortraitSelectionLayout layout = PortraitCombatMath.ComputeSelectionLayout(bands, promptBottom);

        if (confirm != null)
        {
            bool isConfirmButton = confirm.GetType().FullName == ConfirmButtonTypeName;
            ulong confirmId = confirm.GetInstanceId();
            if (!_selectionControls.TryGetValue(confirmId, out ControlDesktopState confirmDesktop))
            {
                confirmDesktop = CaptureControl(confirm);
                if (isConfirmButton)
                {
                    if (refl.ConfirmShowPosField?.GetValue(confirm) is Vector2 showPos)
                        confirmDesktop.ConfirmShowPos = showPos;
                    if (refl.ConfirmHidePosField?.GetValue(confirm) is Vector2 hidePos)
                        confirmDesktop.ConfirmHidePos = hidePos;
                }
                _selectionControls[confirmId] = confirmDesktop;
            }

            float scale = layout.ConfirmScale;
            float left = layout.ConfirmCenterX - 100f * scale;
            float top = layout.ConfirmCenterY - 55f * scale;

            // NConfirmButton.OnEnable/OnDisable tween position to its own _showPos/_hidePos (native bottom-right), and
            // OnWindowChange writes position there directly. Point both at the rail before any selection change can start
            // a tween, and kill the tween, so the button cannot visibly leave the rail for even one frame. The fields
            // only exist on NConfirmButton, never on the other selection nodes (e.g. NPeekButton).
            if (isConfirmButton)
            {
                var railPosition = new Vector2(left, top);
                if (refl.ConfirmMoveTweenField?.GetValue(confirm) is Tween moveTween)
                {
                    if (moveTween.IsRunning()) moveTween.Kill();
                    refl.ConfirmMoveTweenField.SetValue(confirm, null);
                }
                refl.ConfirmShowPosField?.SetValue(confirm, railPosition);
                refl.ConfirmHidePosField?.SetValue(confirm, railPosition);
            }

            confirm.AnchorLeft = 0f;
            confirm.AnchorTop = 0f;
            confirm.AnchorRight = 0f;
            confirm.AnchorBottom = 0f;
            confirm.PivotOffset = Vector2.Zero;
            confirm.Scale = new Vector2(scale, scale);
            confirm.OffsetLeft = left;
            confirm.OffsetTop = top;
            confirm.OffsetRight = left + 200f;
            confirm.OffsetBottom = top + 110f;
        }

        if (preview != null && preview.IsVisibleInTree() && previewContainer != null && previewContainer.IsVisibleInTree())
        {
            if (!_selectionControls.ContainsKey(preview.GetInstanceId()))
                _selectionControls[preview.GetInstanceId()] = CaptureControl(preview);

            preview.AnchorLeft = 0.5f;
            preview.AnchorTop = 0.5f;
            preview.AnchorRight = 0.5f;
            preview.AnchorBottom = 0.5f;
            preview.OffsetLeft = 0f;
            preview.OffsetRight = 0f;
            preview.OffsetTop = layout.PreviewCenterY - canvasHeight * 0.5f;
            preview.OffsetBottom = preview.OffsetTop;
            preview.Scale = new Vector2(layout.PreviewScale, layout.PreviewScale);
        }
        else if (preview != null && _selectionControls.TryGetValue(preview.GetInstanceId(), out ControlDesktopState previewDesktop))
        {
            RestoreControl(preview, previewDesktop);
            _selectionControls.Remove(preview.GetInstanceId());
        }

        PlaceSelectedCards(hand, selected, refl, bands, promptBottom);
        PlaceSelectionPeekButton(hand, bands);
    }

    /// <summary>The prompt's bottom on the canvas, or 0 when there is no visible prompt.</summary>
    private static float SelectionPromptBottom(Control hand)
    {
        if ((hand.GetNodeOrNull<Control>("%SelectionHeader") ?? hand.GetNodeOrNull<Control>("SelectionHeader")) is not { } prompt ||
            !prompt.IsVisibleInTree() || prompt.Size.Y < 2f)
            return 0f;
        return prompt.GetGlobalRect().End.Y;
    }

    // A count change interpolates each existing holder from its current position/scale to its new fixed canvas target.
    // Position and scale share one progress, so the card's whole rect moves in a straight line inside the canvas; the
    // container itself never scales or moves after the session starts. The progress step is also capped by the straight
    // line's length so a card that changes rows (for example 8 -> 9, lower row to upper row) never takes a >40-unit step.
    private const float SelectionEaseStep = 0.06f;
    private const float SelectionMaxRectStep = 30f;

    /// <summary>
    /// The selected-card previews (Purity, Burning Pact, the whole-hand choice) as one or two rows strictly between the
    /// prompt and the rail. The container is fixed at scale 1 on the band centre from before the first pick; each holder
    /// carries the fitted card size and is clamped inside the canvas and band. A pick eases existing holders' position
    /// and scale together toward the fixed new grid, so their drawn rects move monotonically and never leave the canvas.
    /// </summary>
    private void PlaceSelectedCards(Control hand, Control? selected, CombatReflection refl, PortraitCombatBands bands, float promptBottom)
    {
        if (selected == null || !selected.IsVisibleInTree())
            return;

        var holders = new List<Control>();
        foreach (Node child in selected.GetChildren())
        {
            if (child is Control holder && holder.IsVisibleInTree() && !holder.IsQueuedForDeletion() &&
                holder.GetType().Name == "NSelectedHandCardHolder")
                holders.Add(holder);
        }

        // The prompt's measured bottom fixes the preview band; never lay out from a zero rect and then chase the prompt
        // as it finishes its text layout.
        if (promptBottom <= 1f)
            return;

        if (!_selectionControls.ContainsKey(selected.GetInstanceId()))
            _selectionControls[selected.GetInstanceId()] = CaptureControl(selected);
        KillSelectedContainerTween(hand);

        ulong selectedId = selected.GetInstanceId();
        bool fresh = _selectionSession != selectedId;
        if (fresh)
        {
            _selectionSession = selectedId;
            _selectionHolderEase.Clear();
            _selectionHolderOriginals.Clear();
            _selectionFrame = -1;
            _selectionTargetReady = false;
            _selectionTargetCount = -1;
        }

        int targetCount = Math.Max(1, holders.Count);
        if (!_selectionTargetReady || _selectionTargetCount != targetCount)
        {
            // Freeze the prompt bottom and band centre for the whole selection; only the row shape and card size follow
            // the count. A prompt that finishes laying out later therefore cannot move the settled row.
            float bandPrompt = _selectionTargetReady ? _selectionPromptBottom : promptBottom;
            if (!_selectionTargetReady)
                _selectionPromptBottom = promptBottom;
            PortraitSelectedCardsLayout layout = PortraitCombatMath.ComputeSelectedCardsLayout(targetCount, bands, bandPrompt);
            _selectionTarget = _selectionTargetReady ? layout with { CenterY = _selectionCenterY } : layout;
            _selectionCenterY = _selectionTarget.CenterY;
            _selectionTargetReady = true;
            _selectionTargetCount = targetCount;
        }

        // One fixed frame for the whole session: per-count size lives on the holders, never on a scale tween.
        selected.AnchorLeft = 0f;
        selected.AnchorTop = 0f;
        selected.AnchorRight = 0f;
        selected.AnchorBottom = 0f;
        selected.PivotOffset = Vector2.Zero;
        selected.Position = new Vector2(SafeAreaCanvas.PortraitWidth * 0.5f, _selectionCenterY);
        selected.Scale = Vector2.One;

        long frame = (long)Engine.GetProcessFrames();
        bool advance = frame != _selectionFrame;
        if (advance)
            _selectionFrame = frame;

        int columns = Math.Max(1, _selectionTarget.Columns);
        float cardScale = _selectionTarget.Scale * PortraitCombatMath.SelectionCardSmallScale;
        float halfWidth = PortraitCombatMath.CardDefaultWidth * cardScale * 0.5f;
        float halfHeight = PortraitCombatMath.CardDefaultHeight * cardScale * 0.5f;
        float minCenterX = 12f + halfWidth;
        float maxCenterX = SafeAreaCanvas.PortraitWidth - 12f - halfWidth;
        float minCenterY = _selectionPromptBottom + 2f + halfHeight;
        float maxCenterY = bands.RailTopY - 2f - halfHeight;

        for (int i = 0; i < holders.Count; i++)
        {
            Control holder = holders[i];
            ulong holderId = holder.GetInstanceId();
            _selectionHolderOriginals.TryAdd(holderId, (holder.Position, holder.Scale));

            int row = i / columns;
            int column = i % columns;
            int rowCount = Math.Min(columns, holders.Count - row * columns);
            float canvasX = SafeAreaCanvas.PortraitWidth * 0.5f +
                (column - (rowCount - 1) * 0.5f) * _selectionTarget.StepX * _selectionTarget.Scale;
            float canvasY = _selectionCenterY +
                (row - (_selectionTarget.Rows - 1) * 0.5f) * _selectionTarget.StepY * _selectionTarget.Scale;
            canvasX = Math.Clamp(canvasX, minCenterX, Math.Max(minCenterX, maxCenterX));
            canvasY = Math.Clamp(canvasY, minCenterY, Math.Max(minCenterY, maxCenterY));
            var targetPosition = new Vector2(canvasX - SafeAreaCanvas.PortraitWidth * 0.5f, canvasY - _selectionCenterY);
            var targetScale = new Vector2(cardScale, cardScale);

            if (!_selectionHolderEase.TryGetValue(holderId, out SelectionHolderEase ease))
            {
                ease = new SelectionHolderEase
                {
                    FromPosition = targetPosition,
                    FromScale = targetScale,
                    TargetPosition = targetPosition,
                    TargetScale = targetScale,
                    Progress = 1f,
                    Count = targetCount,
                };
                _selectionHolderEase[holderId] = ease;
            }
            else if (ease.Count != targetCount)
            {
                ease.FromPosition = ease.FromPosition.Lerp(ease.TargetPosition, ease.Progress);
                ease.FromScale = ease.FromScale.Lerp(ease.TargetScale, ease.Progress);
                ease.TargetPosition = targetPosition;
                ease.TargetScale = targetScale;
                ease.Progress = 0f;
                ease.Count = targetCount;
            }

            if (advance)
            {
                float distance = SelectedCardTopLeft(ease.FromPosition, ease.FromScale)
                    .DistanceTo(SelectedCardTopLeft(ease.TargetPosition, ease.TargetScale));
                float step = distance > SelectionMaxRectStep
                    ? Math.Min(SelectionEaseStep, SelectionMaxRectStep / distance)
                    : SelectionEaseStep;
                ease.Progress = Math.Min(1f, ease.Progress + step);
            }
            Vector2 position = ease.FromPosition.Lerp(ease.TargetPosition, ease.Progress);
            Vector2 scale = ease.FromScale.Lerp(ease.TargetScale, ease.Progress);
            holder.Position = position;
            holder.Scale = scale;
            ResetSelectedHolderCard(holder, refl);
        }
    }

    /// <summary>The drawn card's top-left for a selected holder's centre and uniform scale (CardNode is centred on it).</summary>
    private static Vector2 SelectedCardTopLeft(Vector2 position, Vector2 scale) =>
        new(
            position.X - PortraitCombatMath.CardDefaultWidth * scale.X * 0.5f,
            position.Y - PortraitCombatMath.CardDefaultHeight * scale.Y * 0.5f);

    /// <summary>
    /// A selected holder's card node is offset by the game's own 0.15 s fly-in tween
    /// (<c>NSelectedHandCardHolder._Ready</c> tweens <c>CardNode.Position</c> to the holder's position at ready time).
    /// If our grid runs first that target is large, so the card is drawn twice as far out. Stop the tween and keep the
    /// card at its holder's centre: the holder's Position is then the only placement, in one coordinate space.
    /// </summary>
    private static void ResetSelectedHolderCard(Control holder, CombatReflection refl)
    {
        if (refl.SelectedHolderTweenField?.GetValue(holder) is Tween tween)
        {
            if (tween.IsRunning()) tween.Kill();
            refl.SelectedHolderTweenField.SetValue(holder, null);
        }
        if (refl.CardHolderCardNodeProp?.GetValue(holder) is Control card && GodotObject.IsInstanceValid(card))
            card.Position = Vector2.Zero;
    }

    /// <summary>After the holder's <c>_Ready</c> has created its fly-in tween: stop it before it takes a step.</summary>
    private void WireSelectedHolder(Control holder)
    {
        if (!_wiredSelectedHolders.Add(holder.GetInstanceId()))
            return;
        if (holder.IsNodeReady())
        {
            ResetSelectedHolderCard(holder, EnsureReflection());
            return;
        }

        ulong holderId = holder.GetInstanceId();
        if (holder.HasSignal("ready"))
        {
            holder.Connect("ready", Callable.From(() =>
            {
                if (GodotObject.InstanceFromId(holderId) is Control readyHolder && GodotObject.IsInstanceValid(readyHolder))
                    ResetSelectedHolderCard(readyHolder, EnsureReflection());
            }));
        }
    }

    /// <summary>
    /// The native peek/hide (eye) button moves to the rail's left end, over the inactive draw pile, during a selection:
    /// out of the preview band, ≥127 units, and clear of the selected previews and the hand cards.
    /// </summary>
    private void PlaceSelectionPeekButton(Control hand, PortraitCombatBands bands)
    {
        if ((hand.GetNodeOrNull<Control>("%PeekButton") ?? hand.GetNodeOrNull<Control>("PeekButton")) is not { } peek ||
            !peek.Visible)
            return;

        if (!_selectionControls.ContainsKey(peek.GetInstanceId()))
            _selectionControls[peek.GetInstanceId()] = CaptureControl(peek);

        float width = Math.Max(127f, peek.Size.X);
        float height = Math.Max(127f, peek.Size.Y);
        float railCenter = (bands.RailTopY + bands.HandTopY) * 0.5f;
        peek.AnchorLeft = 0f;
        peek.AnchorTop = 0f;
        peek.AnchorRight = 0f;
        peek.AnchorBottom = 0f;
        peek.PivotOffset = Vector2.Zero;
        peek.Scale = Vector2.One;
        peek.OffsetLeft = 20f;
        peek.OffsetTop = railCenter - height * 0.5f;
        peek.OffsetRight = peek.OffsetLeft + width;
        peek.OffsetBottom = peek.OffsetTop + height;
    }

    /// <summary>Puts the native selection nodes back when selection ends or portrait is left.</summary>
    private void RestoreSelectionUi()
    {
        foreach ((ulong holderId, (Vector2 position, Vector2 scale)) in _selectionHolderOriginals)
        {
            if (GodotObject.InstanceFromId(holderId) is Control holder && GodotObject.IsInstanceValid(holder))
            {
                // The holder's place and size belong to the native row: the portrait grid wrote both, so both go back.
                if (holder.Position != position)
                    holder.Position = position;
                if (holder.Scale != scale)
                    holder.Scale = scale;
            }
        }
        _selectionHolderOriginals.Clear();
        _selectionHolderEase.Clear();
        _selectionSession = 0;
        _selectionFrame = -1;
        _selectionTargetReady = false;
        _selectionTargetCount = -1;
        _selectionPromptBottom = 0f;

        if (_selectionControls.Count == 0)
            return;

        CombatReflection refl = EnsureReflection();
        foreach ((ulong id, ControlDesktopState desktop) in _selectionControls)
        {
            if (GodotObject.InstanceFromId(id) is not Control control || !GodotObject.IsInstanceValid(control))
                continue;
            try
            {
                RestoreControl(control, desktop);
                // The Confirm's own move targets exist only on NConfirmButton; the other captured selection nodes
                // (preview, selected row, peek) must never have the confirm field accessors applied to them.
                if (control.GetType().FullName == ConfirmButtonTypeName)
                {
                    if (desktop.ConfirmShowPos is Vector2 showPos)
                        refl.ConfirmShowPosField?.SetValue(control, showPos);
                    if (desktop.ConfirmHidePos is Vector2 hidePos)
                        refl.ConfirmHidePosField?.SetValue(control, hidePos);
                    if (refl.ConfirmMoveTweenField?.GetValue(control) is Tween moveTween)
                    {
                        if (moveTween.IsRunning()) moveTween.Kill();
                        refl.ConfirmMoveTweenField.SetValue(control, null);
                    }
                }
            }
            catch (Exception error)
            {
                if (!_selectionLayoutFailureLogged)
                {
                    _selectionLayoutFailureLogged = true;
                    GD.PrintErr("Portrait selection restore failed for one node; continuing: ", error.Message);
                }
            }
        }
        _selectionControls.Clear();
    }

    private static ControlDesktopState CaptureControl(Control control) => new()
    {
        AnchorLeft = control.AnchorLeft,
        AnchorTop = control.AnchorTop,
        AnchorRight = control.AnchorRight,
        AnchorBottom = control.AnchorBottom,
        OffsetLeft = control.OffsetLeft,
        OffsetTop = control.OffsetTop,
        OffsetRight = control.OffsetRight,
        OffsetBottom = control.OffsetBottom,
        Scale = control.Scale,
        Pivot = control.PivotOffset,
    };

    private static void RestoreControl(Control control, ControlDesktopState desktop)
    {
        control.AnchorLeft = desktop.AnchorLeft;
        control.AnchorTop = desktop.AnchorTop;
        control.AnchorRight = desktop.AnchorRight;
        control.AnchorBottom = desktop.AnchorBottom;
        control.OffsetLeft = desktop.OffsetLeft;
        control.OffsetTop = desktop.OffsetTop;
        control.OffsetRight = desktop.OffsetRight;
        control.OffsetBottom = desktop.OffsetBottom;
        control.Scale = desktop.Scale;
        control.PivotOffset = desktop.Pivot;
    }

    /// <summary>
    /// One selected holder's transition: position and scale are interpolated together from the state they had when the
    /// card count changed, so the card's whole rect follows a straight line between its old and new places.
    /// </summary>
    private sealed class SelectionHolderEase
    {
        public Vector2 FromPosition;
        public Vector2 FromScale;
        public Vector2 TargetPosition;
        public Vector2 TargetScale;
        public float Progress;
        public int Count;
    }

    private sealed class ControlDesktopState
    {
        public float AnchorLeft, AnchorTop, AnchorRight, AnchorBottom;
        public float OffsetLeft, OffsetTop, OffsetRight, OffsetBottom;
        public Vector2 Scale, Pivot;
        // The native Confirm's own move targets, overridden to the rail while a selection is open.
        public Vector2? ConfirmShowPos, ConfirmHidePos;
    }

    /// <summary>
    /// Under a finger the hover and the press come in one event. Hovering a card runs the game's desktop hand
    /// layout at once (<c>NPlayerHand.RefreshLayout</c>): its focus branch snaps the card's Y and scale, and nudges its
    /// neighbours by up to 100 units, before the press is delivered. Connected after the hand's own handler, this puts the
    /// portrait layout back in the same event, so none of those instant writes is ever drawn.
    /// </summary>
    private void WireHolderFocus(Control holder, Control hand)
    {
        if (!_wiredHolders.Add(holder.GetInstanceId())) return;
        ulong handId = hand.GetInstanceId();
        Callable relayout = Callable.From((Control _) => RelayoutHandNow(handId));
        if (holder.HasSignal("HolderFocused")) holder.Connect("HolderFocused", relayout);
        if (holder.HasSignal("HolderUnfocused")) holder.Connect("HolderUnfocused", relayout);
    }

    /// <summary>
    /// A selected card leaves the hand through <c>NPlayerHand.RemoveCardHolder</c>, which runs after
    /// <c>NSelectedHandCardContainer.Add</c> has written its native row and fly-in start. Running the portrait selection
    /// layout from this signal lands after those writes and before the frame is drawn, so the native frame is not seen.
    /// </summary>
    private void WireHolderRemoval(Control holder, ulong handId)
    {
        if (!_wiredHolderRemovals.Add(holder.GetInstanceId()))
            return;
        if (holder.HasSignal("tree_exiting"))
            holder.Connect("tree_exiting", Callable.From(() => OnHandHolderListChanged(handId)));
    }

    /// <summary>
    /// The hand's holder list changes when a selected card is put back (the new hand holder is added after the native
    /// selected-card container has re-laid its row) and when a card is picked. Re-run the portrait selection layout in
    /// the same signal, after the native writes, before the frame is drawn.
    /// </summary>
    private void WireHandHolderContainer(Control container, ulong handId)
    {
        if (!_wiredHandContainers.Add(container.GetInstanceId()))
            return;
        if (container.HasSignal("child_entered_tree"))
            container.Connect("child_entered_tree", Callable.From((Node _) => OnHandHolderListChanged(handId)));
        if (container.HasSignal("child_exiting_tree"))
            container.Connect("child_exiting_tree", Callable.From((Node _) => OnHandHolderListChanged(handId)));
    }

    private void OnHandHolderListChanged(ulong handId)
    {
        // A holder came or went: the hand's rows and playability both change, so the next pass must run.
        _handDirty = true;
        if (SafeAreaCanvas.IsPortrait)
            RelayoutSelectionNow(handId);
    }

    private void KillSelectedContainerTween(Control hand)
    {
        CombatReflection refl = EnsureReflection();
        if (refl.HandSelectedCardTweenField?.GetValue(hand) is not Tween tween)
            return;
        if (tween.IsRunning())
            tween.Kill();
        refl.HandSelectedCardTweenField.SetValue(hand, null);
    }

    private void RelayoutHandNow(ulong handId)
    {
        if (!SafeAreaCanvas.IsPortrait || GodotObject.InstanceFromId(handId) is not Control hand || !GodotObject.IsInstanceValid(hand))
            return;
        // Focus or hover moved the native layout under us; the pass runs now, and once more on the next frame in case a
        // native write lands after this one.
        try
        {
            if (_lastBands is not { } bands)
                return;
            SyncHandAndPlayabilityPortrait(hand, EnsureReflection(), bands);
            _handDirty = true;
        }
        catch (Exception error)
        {
            GD.PrintErr("Portrait hand relayout on focus failed: ", error.Message);
        }
    }

    /// <summary>
    /// The native selection changes are signals: the confirm's Enable/Disable tween is created inside them. Connected
    /// after the game's own handlers, this re-runs the selection layout in the same event, before any tween can take a
    /// step, so the Confirm never leaves the rail. <c>ModeChanged</c> covers selection entry/exit; the selected-card
    /// container's child signals cover every select/deselect.
    /// </summary>
    private void WireSelectionUi(Control hand)
    {
        ulong handId = hand.GetInstanceId();
        if (_wiredSelectionHands.Add(handId) && hand.HasSignal("ModeChanged"))
            hand.Connect("ModeChanged", Callable.From(() => RelayoutSelectionNow(handId)));

        // Remembered while it stays in the tree: this runs on every hand pass, and a % path resolves through the
        // unique-name map (a string to StringName conversion and a tree lookup) on each call.
        if (_selectionContainerCache.TryGetValue(handId, out Control? remembered) &&
            GodotObject.IsInstanceValid(remembered) && remembered.IsInsideTree())
            return;
        Control? selected = hand.GetNodeOrNull<Control>("%SelectedHandCardContainer") ??
                            hand.GetNodeOrNull<Control>("SelectedHandCardContainer");
        if (selected == null)
            return;
        _selectionContainerCache[handId] = selected;
        if (!_wiredSelectionContainers.Add(selected.GetInstanceId()))
            return;
        if (selected.HasSignal("child_entered_tree"))
            selected.Connect("child_entered_tree", Callable.From((Node child) => OnSelectedCardEntered(handId, child)));
        if (selected.HasSignal("child_exiting_tree"))
            selected.Connect("child_exiting_tree", Callable.From((Node _) => OnSelectedCardChanged(handId)));
    }

    private void OnSelectedCardEntered(ulong handId, Node child)
    {
        if (child is Control holder && holder.GetType().Name == "NSelectedHandCardHolder")
            WireSelectedHolder(holder);
        OnSelectedCardChanged(handId);
    }

    private void OnSelectedCardChanged(ulong handId)
    {
        // This handler runs after the game's own child handler (which creates UpdateSelectedCardContainer's 0.5 s
        // position/scale tween). Kill it immediately so it can never finish on the native target later; the hand-holder
        // removal/add hooks then apply the fixed portrait grid after the native Add/Refresh writes have all landed.
        if (SafeAreaCanvas.IsPortrait &&
            GodotObject.InstanceFromId(handId) is Control hand && GodotObject.IsInstanceValid(hand))
        {
            KillSelectedContainerTween(hand);
            // Re-apply once more at the deferred flush as a fallback if the holder did not leave the hand tree.
            Callable.From(() => RelayoutSelectionNow(handId)).CallDeferred();
        }
    }

    private void RelayoutSelectionNow(ulong handId)
    {
        if (!SafeAreaCanvas.IsPortrait || _lastBands is not { } bands)
            return;
        if (GodotObject.InstanceFromId(handId) is not Control hand || !GodotObject.IsInstanceValid(hand) || !hand.IsInsideTree())
            return;
        try
        {
            LayoutHandSelectionPortrait(hand, EnsureReflection(), bands, _lastCanvasHeight);
        }
        catch (Exception error)
        {
            GD.PrintErr("Portrait selection relayout failed: ", error.Message);
        }
    }

    private void ApplyHolderTargets(Control holder, CombatReflection refl, PortraitCardSlot slot, bool isFocused,
        Vector2 currentTargetPos, Vector2 currentTargetScale, float currentTargetAngle)
    {
        var targetPos = new Vector2(slot.LocalX, slot.LocalY);
        var targetScale = new Vector2(slot.Scale, slot.Scale);

        // Positions and angles are only ever set as targets, so the game's animation moves the card from where it is and
        // the press never shows a discontinuity. The card under the finger grows at once, as the game's own hover does;
        // the other cards keep animating back to rest when a press ends.
        //
        // While the holder is already easing toward that axis' old target its animation loop is running and re-reads the
        // target fields every frame, so writing the field gives the same motion without cancelling and starting a
        // CancellationTokenSource, a Task and the loop (that restart, once per holder per layout write, is what made a
        // draw hitch). The loops snap and stop exactly at the game's own thresholds, so "further from the old target than
        // that" means the loop is alive; otherwise the real setter is called, which is also what restarts a stopped one.
        bool movingPosition = (holder.Position - currentTargetPos).LengthSquared() > 1f;
        bool movingScale = MathF.Abs(holder.Scale.X - currentTargetScale.X) > 0.002f;
        bool movingAngle = MathF.Abs(holder.RotationDegrees - currentTargetAngle) > 0.1f;

        if ((currentTargetPos - targetPos).LengthSquared() > 0.25f)
        {
            if (movingPosition)
                refl.HolderTargetPosField?.SetValue(holder, targetPos);
            else if (refl.HolderSetTargetPosMethod is { } setPosition)
            {
                _oneArg[0] = targetPos;
                setPosition.Invoke(holder, _oneArg);
            }
        }
        if (MathF.Abs(currentTargetScale.X - targetScale.X) > 0.002f)
        {
            if (isFocused)
            {
                refl.HolderTargetScaleField?.SetValue(holder, targetScale);
                if (refl.HolderSetScaleInstantlyMethod is { } setScaleInstantly)
                {
                    _oneArg[0] = targetScale;
                    setScaleInstantly.Invoke(holder, _oneArg);
                }
            }
            else if (movingScale)
            {
                refl.HolderTargetScaleField?.SetValue(holder, targetScale);
            }
            else if (refl.HolderSetTargetScaleMethod is { } setScale)
            {
                _oneArg[0] = targetScale;
                setScale.Invoke(holder, _oneArg);
            }
        }
        if (isFocused)
        {
            // Straight at once, as the game's own hover does: rotation is about the holder's origin, but a rotated card's
            // axis-aligned rect centre is offset, and rotating while it grows made that measured centre drift.
            refl.HolderTargetAngleField?.SetValue(holder, 0f);
            if (refl.HolderSetAngleInstantlyMethod is { } setAngleInstantly)
            {
                _oneArg[0] = 0f;
                setAngleInstantly.Invoke(holder, _oneArg);
            }
        }
        else if (MathF.Abs(currentTargetAngle - slot.AngleDegrees) > 0.05f)
        {
            if (movingAngle)
                refl.HolderTargetAngleField?.SetValue(holder, slot.AngleDegrees);
            else if (refl.HolderSetTargetAngleMethod is { } setAngle)
            {
                _oneArg[0] = slot.AngleDegrees;
                setAngle.Invoke(holder, _oneArg);
            }
        }

        if (isFocused)
            holder.ZIndex = 2;
        else if (holder.ZIndex > 1)
            holder.ZIndex = 0;
    }

    private void ApplyCardPlayabilityVisuals(
        Control holder,
        Control cardNode,
        CombatReflection refl,
        bool combatInProgress,
        bool playable,
        HashSet<ulong> activeDimmedCardIds,
        bool resting,
        bool holderShifted,
        long frame)
    {
        ulong cardId = cardNode.GetInstanceId();
        CardVisualNodes nodes = CardVisualNodesOf(cardNode, refl);
        Control? body = nodes.Body;
        Control? highlight = nodes.Highlight;

        if (!combatInProgress || playable)
        {
            // A card that stays playable keeps the highlight boost it already has: restoring it here and re-setting it
            // below wrote the shader width twice on every pass of a drawn hand for a value that never changed.
            RestoreSingleCardVisuals(cardNode, refl, keepHighlightBoost: combatInProgress);
            if (combatInProgress && highlight != null)
            {
                bool glowGold = (bool)(refl.HolderGlowGoldProp?.GetValue(holder) ?? false);
                bool glowRed = (bool)(refl.HolderGlowRedProp?.GetValue(holder) ?? false);
                if (!glowGold && !glowRed && highlight.Modulate != PlayableHighlightColor)
                    highlight.Modulate = PlayableHighlightColor;

                if (highlight.Material is ShaderMaterial mat)
                {
                    Tween? tween = refl.HighlightTweenField?.GetValue(highlight) as Tween;
                    // While the native hover tween is running it is fighting this width and the write has to stay; once
                    // it has stopped the width the last pass set is still there, and setting the same value again only
                    // re-notifies the shader. The id is recorded either way, so the restore still knows it boosted this.
                    bool boosted = _boostedHighlights.Add(highlight.GetInstanceId());
                    if (boosted || (tween != null && tween.IsRunning()))
                        mat.SetShaderParameter("width", 0.095f);
                }
            }
            ApplyReducedCardEffects(holder, cardId, refl, combatInProgress, playable, body, highlight, resting,
                holderShifted, frame);
            return;
        }

        activeDimmedCardIds.Add(cardId);
        if (body != null)
        {
            if (!_dimmedCardChildren.TryGetValue(cardId, out Dictionary<ulong, Color>? saved))
            {
                saved = new Dictionary<ulong, Color>();
                _dimmedCardChildren[cardId] = saved;
            }

            float dim = PortraitCombatMath.UnplayableBodyBrightness;
            int bodyChildren = body.GetChildCount();
            for (int i = 0; i < bodyChildren; i++)
            {
                if (body.GetChild(i) is not CanvasItem item || SkippedCardBodyChildren.Contains(item.Name))
                    continue;
                ulong childId = item.GetInstanceId();
                if (!saved.TryGetValue(childId, out Color orig))
                {
                    orig = item.Modulate;
                    saved[childId] = orig;
                }
                // The dim is the same value every pass; a control the game did not touch keeps the one already set.
                var dimmed = new Color(orig.R * dim, orig.G * dim, orig.B * dim, orig.A);
                if (item.Modulate != dimmed)
                    item.Modulate = dimmed;
            }
        }

        // Keep energy and star icons at 100% brightness and sharpen unaffordable red cost digits (#FF5252).
        if (nodes.EnergyIcon is { } energyIcon && GodotObject.IsInstanceValid(energyIcon) &&
            energyIcon.Modulate != Colors.White)
            energyIcon.Modulate = Colors.White;
        if (nodes.StarIcon is { } starIcon && GodotObject.IsInstanceValid(starIcon) &&
            starIcon.Modulate != Colors.White)
            starIcon.Modulate = Colors.White;

        if (refl.CardEnergyLabelField?.GetValue(cardNode) is Control energyLabel)
        {
            CanvasItem? slashIcon = refl.CardUnplayableEnergyIconField?.GetValue(cardNode) as CanvasItem;
            bool hasNonResourceSlash = slashIcon != null && slashIcon.Visible;
            if (!hasNonResourceSlash && energyLabel.HasThemeColorOverride("font_color"))
            {
                Color c = energyLabel.GetThemeColor("font_color");
                if (c.R > 0.85f && c.G < 0.45f)
                    energyLabel.AddThemeColorOverride("font_color", UnaffordableCostColor);
            }
        }

        ApplyReducedCardEffects(holder, cardId, refl, combatInProgress, playable, body, highlight, resting, holderShifted, frame);
    }

    /// <summary>
    /// What Reduce Effects took away from a moving card. Both nodes are visible in the card scene and the game never
    /// hides either — <c>AnimHide</c> only takes the glow's shader width to 0 — so putting them back is simply visible,
    /// with the width the game would have been showing at rest.
    /// </summary>
    private struct ReducedCardEffects
    {
        public ulong HolderId;
        public ulong HighlightId;
        // The width the game's own half-second tween was heading to, written back once instead (see the restore).
        public float HighlightWidth;
        public ulong ShadowId;
        // The last frame this card moved, and the frame it lost its effects: the restore is bounded by both.
        public long LastMovedFrame;
        public long HiddenFrame;
    }

    // Frames a card stays reduced after it last moved. A glide can pause for a frame at an ease plateau, and a card that has
    // just landed can be nudged by the next card of the draw; two frames also covers the frames an observer still reads as
    // motion (a position sampled once a frame), so no frame that moved a card draws its glow.
    private const long ReducedSettleFrames = 2;
    // A card is put back as soon as the whole hand has been quiet this long, whatever one card's own motion signal says:
    // the glow must come back even if a stray transform write keeps one holder "moving" for ever.
    private const long ReducedHandQuietFrames = 20;
    // And no card stays reduced longer than this, full stop (10 s at 60 fps): a playable glow never disappears for good,
    // while a normal flight (under a second) is never cut short by the cap.
    private const long ReducedMaxHiddenFrames = 600;

    /// <summary>
    /// Reduce Effects on the hand: a card in flight from a draw — its first frames at the draw pile, its glide to its
    /// slot, the row re-centring around the cards still arriving — draws without its glow and its shadow, and the glow's
    /// tween is killed rather than run: the game's half-second <c>AnimShow</c> writes the highlight's shader width on
    /// every frame of it, for every drawn card. A card that once lost them keeps them off until it has itself been still
    /// for <see cref="ReducedSettleFrames"/> frames, so the frame it lands on, a glide's paused frame and a nudge by the
    /// next card all stay reduced. The restore cannot be starved: besides that per-card settle, a card comes back once the
    /// hand has been quiet for <see cref="ReducedHandQuietFrames"/> frames (a stray transform write must not keep a glow
    /// off) and, whatever happens, after <see cref="ReducedMaxHiddenFrames"/> frames reduced. Then the width is written
    /// once instead of tweened, so a resting playable card looks exactly as it does with the setting off. "Moving" is on
    /// screen: the hand's own enable/disable slide (the game moves the whole hand up or down 100 units over 0.2 s when the
    /// player's actions are re-enabled or disabled) counts for every card in it, so a card carried by that slide is
    /// reduced too.
    /// </summary>
    private void ApplyReducedCardEffects(Control holder, ulong cardId, CombatReflection refl, bool combatInProgress,
        bool playable, Control? body, Control? highlight, bool resting, bool holderShifted, long frame)
    {
        ulong holderId = holder.GetInstanceId();
        if (_reducedCardEffects.TryGetValue(cardId, out ReducedCardEffects already))
        {
            if (already.HolderId == holderId)
            {
                if (holderShifted)
                {
                    already.LastMovedFrame = frame;
                    _reducedCardEffects[cardId] = already;
                }
                // Three ways back, all bounded: this card has been still for the observer's motion carry; the whole hand has
                // been quiet for a long moment (a stray write must not keep a glow off); or the card has been reduced for
                // the hard cap.
                long still = frame - already.LastMovedFrame;
                bool handQuiet = frame - _handLastMotionFrame >= ReducedHandQuietFrames;
                bool capped = frame - already.HiddenFrame >= ReducedMaxHiddenFrames;
                if (!ReduceEffects || still > ReducedSettleFrames || handQuiet || capped)
                {
                    RestoreReducedCardEffects(cardId);
                    return;
                }
                // The game may start a new glow tween while the card is still moving (a cost change, a model update), and
                // that tween would write the shader width of a highlight nobody draws on every frame: stop it again.
                if (refl.HighlightTweenField?.GetValue(highlight) is Tween movedTween && movedTween.IsRunning())
                    movedTween.Kill();
                return;
            }
            // The card node came back from the pool under a new holder: put the old state back (the nodes are the same
            // ones, now this card's) and decide for this card from scratch.
            RestoreReducedCardEffects(cardId);
        }

        // A resting card was never hidden; anything in motion on screen — a drawn card, the row re-centring around one, the
        // hand's own slide when it is enabled or disabled — loses its effects for as long as it moves.
        if (!ReduceEffects || resting)
        {
            RestoreReducedCardEffects(cardId);
            return;
        }

        bool glowGold = (bool)(refl.HolderGlowGoldProp?.GetValue(holder) ?? false);
        bool glowRed = (bool)(refl.HolderGlowRedProp?.GetValue(holder) ?? false);
        var state = new ReducedCardEffects
        {
            HolderId = holderId,
            LastMovedFrame = frame,
            HiddenFrame = frame,
            // The width the game would be showing at rest: the playable boost in a fight, the game's own AnimShow value
            // for a gold or red glow, nothing at all (a card's AnimHide target) for the rest.
            HighlightWidth = combatInProgress && playable ? 0.095f : glowGold || glowRed ? 0.075f : 0f,
        };
        if (highlight != null)
        {
            if (refl.HighlightTweenField?.GetValue(highlight) is Tween tween && tween.IsRunning())
                tween.Kill();
            state.HighlightId = highlight.GetInstanceId();
            highlight.Visible = false;
        }
        if (body?.GetNodeOrNull<CanvasItem>("Shadow") is { } shadow)
        {
            state.ShadowId = shadow.GetInstanceId();
            shadow.Visible = false;
        }
        _reducedCardEffects[cardId] = state;
    }

    /// <summary>Puts a card's glow and shadow back, writing the glow width once instead of running its tween.</summary>
    private void RestoreReducedCardEffects(ulong cardId)
    {
        if (!_reducedCardEffects.Remove(cardId, out ReducedCardEffects state))
            return;
        if (state.HighlightId != 0 && GodotObject.InstanceFromId(state.HighlightId) is Control highlight &&
            GodotObject.IsInstanceValid(highlight))
        {
            highlight.Visible = true;
            if (highlight.Material is ShaderMaterial mat)
                mat.SetShaderParameter("width", state.HighlightWidth);
        }
        if (state.ShadowId != 0 && GodotObject.InstanceFromId(state.ShadowId) is CanvasItem shadow &&
            GodotObject.IsInstanceValid(shadow))
            shadow.Visible = true;
    }

    /// <summary>
    /// A reduced entry whose card is not one of the nodes this pass laid out belongs to no card on screen any more: the
    /// card left the hand, its node was pooled (the pool reset does not touch the highlight's visibility), or its holder
    /// was replaced while the entry stayed keyed to the old node. It gets its effects back here, before it is shown
    /// anywhere else. This is also what makes a held glow impossible: an entry the pass never sees is always restored on
    /// the next pass, whatever its motion bookkeeping says.
    /// </summary>
    private void RestoreReducedEffectsOfLostCards()
    {
        if (_reducedCardEffects.Count == 0)
            return;
        List<ulong> lost = _reduceRestoreIds;
        lost.Clear();
        foreach (ulong cardId in _reducedCardEffects.Keys)
        {
            if (!_liveReducedCardIds.Contains(cardId))
                lost.Add(cardId);
        }
        foreach (ulong cardId in lost)
            RestoreReducedCardEffects(cardId);
    }

    /// <summary>Every reduced card gets its effects back (Portrait Mode off, the setting off, or the layout giving up).</summary>
    private void RestoreAllReducedCardEffects()
    {
        if (_reducedCardEffects.Count == 0)
            return;
        var cards = new List<ulong>(_reducedCardEffects.Keys);
        foreach (ulong cardId in cards)
            RestoreReducedCardEffects(cardId);
        _reducedCardEffects.Clear();
    }

    /// <summary>
    /// A card node's body, highlight and the two icons the playability visuals write, resolved once and remembered while the
    /// nodes stay valid: they are scene children of the card and survive a pooled card's reuse for another model. Resolving
    /// them was one reflection read and up to three node-path lookups per card per pass, and the pass runs on every drawn
    /// frame of a draw.
    /// </summary>
    private CardVisualNodes CardVisualNodesOf(Control cardNode, CombatReflection refl)
    {
        ulong cardId = cardNode.GetInstanceId();
        if (_cardVisualNodes.TryGetValue(cardId, out CardVisualNodes? known) &&
            (known.Body == null || GodotObject.IsInstanceValid(known.Body)) &&
            (known.Highlight == null || GodotObject.IsInstanceValid(known.Highlight)) &&
            (known.EnergyIcon == null || GodotObject.IsInstanceValid(known.EnergyIcon)) &&
            (known.StarIcon == null || GodotObject.IsInstanceValid(known.StarIcon)))
            return known;
        Control? body = refl.CardBodyProp?.GetValue(cardNode) as Control ?? cardNode.GetNodeOrNull<Control>("%CardContainer");
        Control? highlight = refl.CardHighlightProp?.GetValue(cardNode) as Control ?? body?.GetNodeOrNull<Control>("Highlight");
        var nodes = new CardVisualNodes
        {
            Body = body,
            Highlight = highlight,
            EnergyIcon = body?.GetNodeOrNull<CanvasItem>("EnergyIcon"),
            StarIcon = body?.GetNodeOrNull<CanvasItem>("StarIcon"),
        };
        if (body != null)
            _cardVisualNodes[cardId] = nodes;
        return nodes;
    }

    private void RestoreSingleCardVisuals(Control cardNode, CombatReflection refl, bool keepHighlightBoost = false)
    {
        ulong cardId = cardNode.GetInstanceId();
        if (_dimmedCardChildren.TryGetValue(cardId, out Dictionary<ulong, Color>? saved))
        {
            foreach ((ulong childId, Color orig) in saved)
            {
                if (GodotObject.InstanceFromId(childId) is CanvasItem item && GodotObject.IsInstanceValid(item))
                    item.Modulate = orig;
            }
            _dimmedCardChildren.Remove(cardId);
        }

        if (keepHighlightBoost)
            return;
        if (CardVisualNodesOf(cardNode, refl).Highlight is { } highlight &&
            _boostedHighlights.Remove(highlight.GetInstanceId()) &&
            highlight.Material is ShaderMaterial mat)
        {
            float current = mat.GetShaderParameter("width").AsSingle();
            if (current > 0.08f)
                mat.SetShaderParameter("width", 0.075f);
        }
    }

    private void RestoreAllDimmedCards(CombatReflection refl)
    {
        foreach ((ulong cardId, Dictionary<ulong, Color> saved) in _dimmedCardChildren)
        {
            foreach ((ulong childId, Color orig) in saved)
            {
                if (GodotObject.InstanceFromId(childId) is CanvasItem item && GodotObject.IsInstanceValid(item))
                    item.Modulate = orig;
            }
            if (GodotObject.InstanceFromId(cardId) is Control card && GodotObject.IsInstanceValid(card) &&
                refl.CardDisplayingPileProp?.GetValue(card) is { } pileEnum)
            {
                refl.CardUpdateEnergyCostMethod?.Invoke(card, [pileEnum]);
            }
        }
        _dimmedCardChildren.Clear();

        foreach (ulong hlId in _boostedHighlights)
        {
            if (GodotObject.InstanceFromId(hlId) is Control hl && GodotObject.IsInstanceValid(hl) && hl.Material is ShaderMaterial mat)
            {
                float current = mat.GetShaderParameter("width").AsSingle();
                if (current > 0.08f)
                    mat.SetShaderParameter("width", 0.075f);
                if (MathF.Abs(hl.Modulate.R - PlayableHighlightColor.R) < 0.02f)
                    hl.Modulate = new Color(0.298f, 0.980f, 1.0f, hl.Modulate.A);
            }
        }
        _boostedHighlights.Clear();
    }

    private void LayoutCombatHudPortrait(
        Control ui,
        Control room,
        CombatReflection refl,
        PortraitHudLayout hud)
    {
        if ((ui.GetNodeOrNull<Control>("%EnergyCounterContainer") ?? ui.GetNodeOrNull<Control>("EnergyCounterContainer")) is { } energyContainer)
        {
            energyContainer.AnchorLeft = 0f;
            energyContainer.AnchorRight = 0f;
            energyContainer.AnchorTop = 0f;
            energyContainer.AnchorBottom = 0f;
            energyContainer.Position = new Vector2(hud.EnergyContainerX, hud.EnergyContainerY);

            if (refl.EnergyCounterField?.GetValue(ui) is Control energyCounter)
            {
                energyCounter.PivotOffset = new Vector2(64f, 64f);
                energyCounter.Scale = new Vector2(hud.EnergyScale, hud.EnergyScale);
                // NEnergyCounter.AnimIn slides its own Position from (-480,128) to zero after Activate; the container is
                // already on the rail, so keep the counter itself at the origin for every drawn frame.
                energyCounter.Position = Vector2.Zero;
            }
        }

        if (refl.StarCounterField?.GetValue(ui) is Control starCounter && starCounter.IsVisibleInTree() &&
            refl.EnergyCounterField?.GetValue(ui) is Control starOrbCounter)
            PlaceStarCounter(starCounter, starOrbCounter);

        if (refl.FoolCombatUiField?.GetValue(ui) is Control foolUi && foolUi.IsVisibleInTree())
        {
            foolUi.AnchorLeft = 0f;
            foolUi.AnchorRight = 0f;
            foolUi.AnchorTop = 0f;
            foolUi.AnchorBottom = 0f;
            foolUi.Scale = new Vector2(0.72f, 0.72f);
            foolUi.Position = new Vector2(14f, hud.RailTopY - 190f);
        }

        // End Turn button: kill the desktop ShowPos/HidePos tween and keep the button in its rail place. It grows about
        // its centre (44 points tall), so the target is clamped to keep its right edge on the canvas.
        if ((ui.GetNodeOrNull<Control>("%EndTurnButton") ?? ui.GetNodeOrNull<Control>("EndTurnButton")) is { } endTurn)
        {
            endTurn.PivotOffset = new Vector2(110f, 45f);
            endTurn.Scale = new Vector2(hud.EndTurnScale, hud.EndTurnScale);

            if (refl.EndTurnPosTweenField?.GetValue(endTurn) is Tween posTween)
            {
                if (posTween.IsRunning()) posTween.Kill();
                refl.EndTurnPosTweenField.SetValue(endTurn, null);
            }

            // The rail is fixed: the button takes its place at once, so no native show/hide or layout tween can leave it
            // somewhere else when a turn ends (a scaled control's pivot would also shift the visible rect). The native
            // state is Hidden until the player's first turn starts (SetState calls AnimIn before it records Enabled), and
            // the combat UI is drawn from CombatSetUp, before the fight counts as in progress: while the fight is being
            // set up or is running the button stays on the rail, so its first drawn frame is already settled. It leaves
            // only when the fight is over and the Proceed button takes its place.
            endTurn.Position = EndTurnRailPosition(endTurn, refl, hud);
        }

        if ((ui.GetNodeOrNull<Control>("%PingButton") ?? ui.GetNodeOrNull<Control>("PingButton")) is { } pingBtn && pingBtn.Visible)
        {
            if (refl.PingPosTweenField?.GetValue(pingBtn) is Tween pingTween)
            {
                if (pingTween.IsRunning()) pingTween.Kill();
                refl.PingPosTweenField.SetValue(pingBtn, null);
            }
            int pingState = Convert.ToInt32(refl.PingStateField?.GetValue(pingBtn) ?? 2);
            bool pingHidden = pingState == 2;
            pingBtn.Scale = new Vector2(0.85f, 0.85f);
            // 150 by 70 about its centre (75, 35): it floats over the floor above the rail, between End Turn and the
            // exhaust pile, and its touch area reaches 40 units above and below the picture (44 points tall).
            pingBtn.Position = new Vector2(pingHidden ? 1140f : hud.PingShowX, hud.PingShowY);
            PhoneHitArea.Grow(pingBtn, 0f, 40f, 0f, 40f);
        }

        // Proceed button at end of fight: it takes End Turn's place in the rail, so the discard pile stays clear.
        if ((room.GetNodeOrNull<Control>("%ProceedButton") ?? room.GetNodeOrNull<Control>("ProceedButton")) is { } proceedBtn)
        {
            bool enabled = (bool)(refl.ButtonIsEnabledProp?.GetValue(proceedBtn) ?? false);
            if (refl.ProceedAnimTweenField?.GetValue(proceedBtn) is Tween procTween && procTween.IsRunning())
            {
                procTween.Kill();
                refl.ProceedAnimTweenField.SetValue(proceedBtn, null);
            }
            float procW = Math.Max(200f, proceedBtn.Size.X);
            float targetX = enabled ? hud.EndTurnShowX + 110f - procW * 0.5f : 1140f;
            proceedBtn.Position = new Vector2(targetX, hud.EndTurnShowY);
        }

        // Draw, Discard, and Exhaust piles: 80 by 80 about their centre, drawn 44 points across.
        if ((ui.GetNodeOrNull<Control>("%CombatPileContainer") ?? ui.GetNodeOrNull<Control>("CombatPileContainer")) is { } pileContainer)
        {
            pileContainer.AnchorLeft = 0f;
            pileContainer.AnchorTop = 0f;
            pileContainer.AnchorRight = 1f;
            pileContainer.AnchorBottom = 1f;
            pileContainer.OffsetLeft = 0f;
            pileContainer.OffsetTop = 0f;
            pileContainer.OffsetRight = 0f;
            pileContainer.OffsetBottom = 0f;

            if ((pileContainer.GetNodeOrNull<Control>("%DrawPile") ?? pileContainer.GetNodeOrNull<Control>("DrawPile")) is { } drawPile)
            {
                LayoutPileButton(
                    drawPile,
                    refl,
                    new Vector2(hud.DrawShowX, hud.DrawShowY),
                    new Vector2(hud.DrawHideX, hud.DrawHideY),
                    hud.PileScale);
            }

            if ((pileContainer.GetNodeOrNull<Control>("%DiscardPile") ?? pileContainer.GetNodeOrNull<Control>("DiscardPile")) is { } discardPile)
            {
                LayoutPileButton(
                    discardPile,
                    refl,
                    new Vector2(hud.DiscardShowX, hud.DiscardShowY),
                    new Vector2(hud.DiscardHideX, hud.DiscardHideY),
                    hud.PileScale);
            }

            if ((pileContainer.GetNodeOrNull<Control>("%ExhaustPile") ?? pileContainer.GetNodeOrNull<Control>("ExhaustPile")) is { } exhaustPile)
            {
                LayoutPileButton(
                    exhaustPile,
                    refl,
                    new Vector2(hud.ExhaustShowX, hud.ExhaustShowY),
                    new Vector2(hud.ExhaustHideX, hud.ExhaustShowY),
                    hud.PileScale);
            }
        }
    }

    private static void LayoutPileButton(Control pile, CombatReflection refl, Vector2 showPos, Vector2 hidePos, float scale)
    {
        pile.PivotOffset = new Vector2(40f, 40f);
        pile.Scale = new Vector2(scale, scale);
        // The pile's 80-unit art drawn at 1.543 is 123.44 canvas units = 42.9 points, a little short of the 44-point
        // minimum target, so the touch area is grown back around it on all four sides; the drawn pile keeps its size and
        // its badge stays where it is.
        float pileGrow = 80f * scale < PortraitBands.MinTarget ? (PortraitBands.MinTarget - 80f * scale) * 0.5f + 2f : 0f;
        if (pileGrow > 0f)
            PhoneHitArea.Grow(pile, pileGrow, pileGrow, pileGrow, pileGrow);

        refl.PileShowPosField?.SetValue(pile, showPos);
        refl.PileHidePosField?.SetValue(pile, hidePos);

        Tween? tween = refl.PilePosTweenField?.GetValue(pile) as Tween;
        if (tween != null && tween.IsRunning())
            return;

        // When combat is active and the pile is visible, keep it anchored cleanly on showPos.
        if (pile.Visible && refl.IsCombatInProgress())
            pile.Position = showPos;
    }

    private void LayoutCombatStagePortrait(
        Control room,
        CombatReflection refl,
        CombatRoomState state,
        PortraitCombatBands bands,
        float capHeight,
        float canvasHeight)
    {
        Control? sceneContainer = room.GetNodeOrNull<Control>("%CombatSceneContainer") ?? room.GetNodeOrNull<Control>("CombatSceneContainer");
        if (sceneContainer == null)
            return;
        _pendingRoomState = state;

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
        _orbsOnStage = hasOrbs;
        CombatStagePlan plan = EnsureStagePlan(room, state, refl, bands, canvasHeight, players, pets, currentEnemies, hasOrbs);
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
        if (players.Count > 0 && _creatures.TryGetValue(players[0].GetInstanceId(), out CreatureDesktopState? playerDesktop))
        {
            foreach (Control enemy in currentEnemies)
            {
                if (_creatures.TryGetValue(enemy.GetInstanceId(), out CreatureDesktopState? enemyDesktop) &&
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
                    float hudDrop = BarBandClearance + EnemyHudBoxHeight;
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
        if (!_creatures.TryGetValue(creature.GetInstanceId(), out CreatureDesktopState? st) || st.Hitbox is not { } hit ||
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
        if (_creatures.TryGetValue(creature.GetInstanceId(), out CreatureDesktopState? st) && st.Hitbox is { } hit &&
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
        if (_creatures.TryGetValue(creature.GetInstanceId(), out CreatureDesktopState? st) && st.Hitbox is { } hit &&
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
        if (!_creatures.TryGetValue(left.GetInstanceId(), out CreatureDesktopState? l) || l.Hitbox is not { } hitL ||
            !_creatures.TryGetValue(right.GetInstanceId(), out CreatureDesktopState? r) || r.Hitbox is not { } hitR)
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

    private void RestoreBackgroundBosses()
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
        bool haveState = _creatures.TryGetValue(id, out CreatureDesktopState? cState) && cState != null;
        var placement = new CreatureDesktopState.StagePlacement(slot, maxW, maxH, maxBarWidth, minLeft, maxRight,
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
            CreatureDesktopState known = cState;
            if (known.PlacementSettle > 0)
            {
                known.PlacementSettle--;
                SnapAndClampHitbox(creature, known, slot, bands, known.LastStageScale.X, isAlly);
                (float settleLeft, float settleRight) = HitboxLocalExtent(creature, refl);
                (_, float settleFoot) = HitboxLocalVerticalExtent(creature, refl);
                known.RememberOrigin(creature, settleLeft, settleRight, settleFoot);
            }
            else
            {
                GrowHitboxToTouchTarget(known, slot, bands, known.LastStageScale.X, isAlly);
            }
            known.RememberStage(creature, placement, boundsSize);
            if (_pendingRoomState is { } settledRoom)
                _pendingLabels?.Add(new LabelRequest(creature, known, settledRoom, maxBarWidth, isAlly));
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
        bool scaleChanged = MathF.Abs(creature.Scale.X - fittedScale) > 0.003f;

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

        // A wide creature moves in so its hit box stays between minLeft and maxRight (the hit box itself, which is not
        // always centred on the feet), and the hit box's bottom edge lands on the foot line.
        float fromX = minLeft - lo * fittedScale;
        float canvasX = Math.Clamp(slot.CanvasX, fromX, Math.Max(fromX, maxRight - hi * fittedScale));
        // Two-unit grid: a hit box that shifts by a fraction of a unit between frames must not nudge a placed creature.
        canvasX = MathF.Round(canvasX * 0.5f) * 2f;
        Vector2 targetLocalPos = new(canvasX - containerOrigin.X,
            slot.CanvasFeetY - foot * fittedScale - containerOrigin.Y);
        Vector2 targetScale = new(fittedScale, fittedScale);

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
                placing.RememberPlacement(slot, creature.Position, creature.Scale.X, liveLeft, liveRight, liveFoot);
            }
        }
        else
        {
            GrowHitboxToTouchTarget(cState, slot, bands, fittedScale, isAlly);
        }

        if (cState == null)
            return;
        cState.RememberStage(creature, placement, boundsSize);
        if (_pendingRoomState is { } roomState)
            _pendingLabels?.Add(new LabelRequest(creature, cState, roomState, maxBarWidth, isAlly));
    }

    /// <summary>
    /// Puts a creature's body on its slot's foot line and its hit box on the canvas. This runs only for a placement (and the
    /// two settle frames after it, while the game's own late layout may still land): after that the body's origin is stored
    /// and re-asserted, so nothing the game does mid-fight moves a body.
    /// </summary>
    private void SnapAndClampHitbox(Control creature, CreatureDesktopState? cState, PortraitCreatureSlot slot,
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
    private static void GrowHitboxToTouchTarget(CreatureDesktopState? cState, PortraitCreatureSlot slot,
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
        float orbitTop = bands.Compact ? capHeight + PortraitBands.TopBarHeight : bands.StageTopY;

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

        float iconHalf = PortraitOrbMath.IconWorldFor(orbWorld) * 0.5f;
        PortraitOrbArcPlan plan = PlanOrbArc(count, body, bands, orbitTop, labelWidth, orbWorld);
        if (!plan.IsSingle && OrbArcMeetsEnemy(plan, iconHalf))
            plan = PlanOrbArc(count, body, bands, orbitTop, labelWidth, orbWorld, single: true);
        else if (plan.IsSingle && count > 5 && OrbArcEnemyLane(plan, body.GetCenter().X, iconHalf, out float laneLeft,
                     out float laneRight))
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
    private bool OrbArcMeetsEnemy(in PortraitOrbArcPlan plan, float iconHalf)
    {
        if (_stageEnemies.Count == 0)
            return false;
        float reach = iconHalf + PortraitOrbMath.CanvasEdgeMargin;
        for (int i = 0; i < plan.Count; i++)
        {
            (float x, float y) = plan.Position(i);
            foreach (Control enemy in _stageEnemies)
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
    private bool OrbArcEnemyLane(in PortraitOrbArcPlan plan, float ownerCenterX, float iconHalf, out float laneLeft,
        out float laneRight)
    {
        laneLeft = 0f;
        laneRight = 0f;
        if (_stageEnemies.Count == 0)
            return false;
        float extent = iconHalf + 10f;
        laneLeft = extent;
        laneRight = SafeAreaCanvas.PortraitWidth - extent;
        bool found = false;
        foreach (Control enemy in _stageEnemies)
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

    /// <summary>
    /// The bottom of the run screen's header (top bar row plus the potion row) plus a small margin, in canvas units: the
    /// lowest line an enemy's intent row may reach on this screen. The potion row is part of the header stone, so intents
    /// stay under it even though they may sit beside the relic icons.
    /// </summary>
    private static float PortraitHudFloorY(in PortraitCombatBands bands) =>
        Math.Max(0f, bands.StageTopY - PortraitBands.RunContentTop + PortraitBands.TopBarHeight) + 10f;

    /// <summary>The clearance a health bar keeps below its own foot line (the harness/collision margin is 2 units).</summary>
    private const float BarBandClearance = 3f;

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
    private readonly record struct LabelRequest(Control Creature, CreatureDesktopState State, CombatRoomState Room,
        float MaxBarWidth, bool IsAlly);

    /// <summary>
    /// Lays out the health displays and status blocks for every creature of the room. The blocks are planned first from the
    /// final hit boxes, then neighbours on the same status row (same rank and foot line, split at their midpoints) cap each
    /// other's overhang and a neighbour body standing in the block's own band is a hard edge, so the result does not depend
    /// on the order the creatures were laid out in.
    /// </summary>
    private void ApplyCreatureLabelsPortraitBatch(List<LabelRequest>? requests, CombatReflection refl)
    {
        if (requests == null || requests.Count == 0)
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
            if (request.Room.Stage != null &&
                request.Room.Stage.SlotByCreature.TryGetValue(request.Creature.GetInstanceId(), out int slotIndex) &&
                slotIndex < request.Room.Stage.TallFormation.Length)
            {
                PortraitCreatureSlot slot = request.Room.Stage.TallFormation[slotIndex];
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
        // (the Waterfall Giant is an Underdocks boss with a broad waterfall arena) is framed as a broad floor.
        plan.CustomArena = refl.EncounterHasCustomBackground(players, enemies);
        plan.Pier = IsUnderdocksActor(refl, players, enemies) && !plan.CustomArena;

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
            if (PortraitCombatMath.PrefersSingleRow(bodies, formationScale, row.RoomScale))
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

        float scale;
        float originY;
        // The pier is not a ground plane: its plank line is a fixed part of the art, measured relative to the art's bottom
        // (the authored deck sits 861 of the 1296-unit layer art above it). Underdocks uses this at both portrait shapes;
        // the broad floor would push the deck far above the feet and leave only water on screen.
        float artHeight = Math.Max(1f, bottomRight.Y - topLeft.Y);
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

    private static TextureRect? FindArtTextureRect(Node? node, int depth)
    {
        if (node == null || depth < 0)
            return null;
        foreach (Node child in node.GetChildren())
        {
            if (child is TextureRect rect && rect.Texture != null && rect.Size.X > 1000f && rect.Size.Y > 300f)
                return rect;
            if (FindArtTextureRect(child, depth - 1) is { } found)
                return found;
        }
        return null;
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
            if (!_creatures.TryGetValue(creature.GetInstanceId(), out CreatureDesktopState? state))
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

    private static bool TryHitboxCanvasRect(CreatureDesktopState state, out Rect2 rect)
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
        _creatures[id] = CreatureDesktopState.Capture(creature, refl);
    }

    private CombatReflection EnsureReflection() => _reflection ??= new CombatReflection(typeof(NGame).Assembly);

    private sealed class CreatureDesktopState
    {
        private const string BoundsStandInName = "PortraitStateBounds";

        private readonly Vector2 _position;
        private readonly Vector2 _scale;
        private readonly Vector2 _pivotOffset;
        private readonly int _zIndex;
        private readonly Vector2 _orbScale;
        private readonly Vector2 _displayScale, _displayPivot;
        private readonly Vector2 _intentScale, _intentPivot;
        private readonly Vector2 _powerScale, _powerPosition, _powerPivot, _powerSize;
        private readonly float _barHitboxLeft, _barHitboxRight;
        // The bar's own place inside the health display: the band rule shifts its global Y every frame.
        private readonly Vector2 _hpBarPosition;
        private Control? _boundsStandIn;
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
        public void RememberPlacement(PortraitCreatureSlot slot, Vector2 origin, float scale, float left, float right,
            float foot)
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

        /// <summary>Everything that decides where a creature stands: the slot, its frame and the container origin.</summary>
        public readonly record struct StagePlacement(PortraitCreatureSlot Slot, float MaxW, float MaxH, float MaxBar,
            float MinLeft, float MaxRight, float ForceScale, Vector2 Origin, bool IsAlly, bool Segmented, float PeerScale);

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

        /// <summary>
        /// What the game adds to the hit box's width for this creature's health bar (<c>NHealthBar.UpdateLayoutForCreatureBounds</c>):
        /// nothing for a player, 24 for a monster less the monster's own reduction.
        /// </summary>
        public readonly float BarExtraWidth;

        /// <summary>The game's layout call failed for this creature: its health display stays as the game draws it.</summary>
        public bool StateDisplayLayoutFailed { get; private set; }

        /// <summary>The creature's parts drawn larger in portrait: its health display and what that holds, its hit box and its intents.</summary>
        public readonly Control? Display, Hitbox, Intents, Nameplate, HpBar, HpBarHitbox, PowerContainer;

        private CreatureDesktopState(Control creature, CombatReflection refl)
        {
            _position = creature.Position;
            _scale = creature.Scale;
            _pivotOffset = creature.PivotOffset;
            _zIndex = creature.ZIndex;
            _orbScale = refl.CreatureOrbManagerProp?.GetValue(creature) is Control orbMgr ? orbMgr.Scale : Vector2.One;

            // In the creature's scene the health display (NCreatureStateDisplay) is the node named HealthBar; inside it the
            // bar itself (NHealthBar) has that name again.
            Display = creature.GetNodeOrNull<Control>("%HealthBar") ?? creature.GetNodeOrNull<Control>("HealthBar");
            Hitbox = creature.GetNodeOrNull<Control>("%Hitbox") ?? creature.GetNodeOrNull<Control>("Hitbox");
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

        public static CreatureDesktopState Capture(Control creature, CombatReflection refl) => new(creature, refl);

        /// <summary>Where the game stood the creature across the stage (its container's units; the stage's middle is 0).</summary>
        public float DesktopX => _position.X;

        /// <summary>Where a point of the creature (its own units) was in its container when the game laid it out.</summary>
        public Vector2 DesktopPoint(Vector2 local) => _position + _pivotOffset + _scale * (local - _pivotOffset);

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

        public void Restore(Control creature, CombatReflection refl)
        {
            // The larger health display and intents first: the game lays the display out again from the hit box below.
            RestoreStateDisplay();
            if (Intents != null && GodotObject.IsInstanceValid(Intents))
            {
                Intents.Scale = _intentScale;
                Intents.PivotOffset = _intentPivot;
            }

            creature.PivotOffset = _pivotOffset;
            creature.Scale = _scale;
            creature.Position = _position;
            creature.ZIndex = _zIndex;
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

    private sealed class CombatRoomState
    {
        public CombatStagePlan? Stage;

        private readonly Vector2 _sceneScale, _scenePivot, _scenePos;
        private readonly Vector2 _bgScale;
        private readonly float _bgOffLeft, _bgOffTop, _bgOffRight, _bgOffBottom;
        private readonly Vector2 _layer00Scale, _layer00Pos;
        private readonly Vector2 _fgPos;
        private readonly float _energyAnchorTop, _energyAnchorBottom, _energyOffLeft, _energyOffTop, _energyOffRight, _energyOffBottom;
        private readonly Vector2 _energyScale;
        private readonly Vector2 _starScale, _starSize;
        private readonly float _starAnchorLeft, _starAnchorTop, _starAnchorRight, _starAnchorBottom;
        private readonly float _starOffLeft, _starOffTop, _starOffRight, _starOffBottom;
        private readonly Vector2 _foolScale, _foolPos;
        private readonly Vector2 _drawShow, _drawHide, _drawScale;
        private readonly Vector2 _discardShow, _discardHide, _discardScale;
        private readonly Vector2 _exhaustShow, _exhaustHide, _exhaustScale;

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
            Vector2 fgPos,
            float energyAnchorTop,
            float energyAnchorBottom,
            float energyOffLeft,
            float energyOffTop,
            float energyOffRight,
            float energyOffBottom,
            Vector2 energyScale,
            Vector2 starScale,
            Vector2 starSize,
            float starAnchorLeft,
            float starAnchorTop,
            float starAnchorRight,
            float starAnchorBottom,
            float starOffLeft,
            float starOffTop,
            float starOffRight,
            float starOffBottom,
            Vector2 foolScale,
            Vector2 foolPos,
            Vector2 drawShow,
            Vector2 drawHide,
            Vector2 drawScale,
            Vector2 discardShow,
            Vector2 discardHide,
            Vector2 discardScale,
            Vector2 exhaustShow,
            Vector2 exhaustHide,
            Vector2 exhaustScale)
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
            _energyAnchorTop = energyAnchorTop;
            _energyAnchorBottom = energyAnchorBottom;
            _energyOffLeft = energyOffLeft;
            _energyOffTop = energyOffTop;
            _energyOffRight = energyOffRight;
            _energyOffBottom = energyOffBottom;
            _energyScale = energyScale;
            _starScale = starScale;
            _starSize = starSize;
            _starAnchorLeft = starAnchorLeft;
            _starAnchorTop = starAnchorTop;
            _starAnchorRight = starAnchorRight;
            _starAnchorBottom = starAnchorBottom;
            _starOffLeft = starOffLeft;
            _starOffTop = starOffTop;
            _starOffRight = starOffRight;
            _starOffBottom = starOffBottom;
            _foolScale = foolScale;
            _foolPos = foolPos;
            _drawShow = drawShow;
            _drawHide = drawHide;
            _drawScale = drawScale;
            _discardShow = discardShow;
            _discardHide = discardHide;
            _discardScale = discardScale;
            _exhaustShow = exhaustShow;
            _exhaustHide = exhaustHide;
            _exhaustScale = exhaustScale;
        }

        public static CombatRoomState Capture(Control room, CombatReflection refl)
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

            Control? ui = room.GetNodeOrNull<Control>("%CombatUi") ?? room.GetNodeOrNull<Control>("CombatUi");
            Control? energyCont = ui?.GetNodeOrNull<Control>("%EnergyCounterContainer") ?? ui?.GetNodeOrNull<Control>("EnergyCounterContainer");
            Control? energy = (ui != null ? refl.EnergyCounterField?.GetValue(ui) as Control : null);
            Control? star = (ui != null ? refl.StarCounterField?.GetValue(ui) as Control : null);
            Control? fool = (ui != null ? refl.FoolCombatUiField?.GetValue(ui) as Control : null);
            Control? piles = ui?.GetNodeOrNull<Control>("%CombatPileContainer") ?? ui?.GetNodeOrNull<Control>("CombatPileContainer");
            Control? draw = piles?.GetNodeOrNull<Control>("%DrawPile") ?? piles?.GetNodeOrNull<Control>("DrawPile");
            Control? discard = piles?.GetNodeOrNull<Control>("%DiscardPile") ?? piles?.GetNodeOrNull<Control>("DiscardPile");
            Control? exhaust = piles?.GetNodeOrNull<Control>("%ExhaustPile") ?? piles?.GetNodeOrNull<Control>("ExhaustPile");

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
                fg?.Position ?? Vector2.Zero,
                energyCont?.AnchorTop ?? 1f,
                energyCont?.AnchorBottom ?? 1f,
                energyCont?.OffsetLeft ?? 100f,
                energyCont?.OffsetTop ?? -252f,
                energyCont?.OffsetRight ?? 100f,
                energyCont?.OffsetBottom ?? -252f,
                energy?.Scale ?? Vector2.One,
                star?.Scale ?? new Vector2(0.8f, 0.8f),
                star?.Size ?? new Vector2(128f, 128f),
                star?.AnchorLeft ?? 0f,
                star?.AnchorTop ?? 1f,
                star?.AnchorRight ?? 0f,
                star?.AnchorBottom ?? 1f,
                star?.OffsetLeft ?? 64f,
                star?.OffsetTop ?? -212f,
                star?.OffsetRight ?? 192f,
                star?.OffsetBottom ?? -84f,
                fool?.Scale ?? Vector2.One,
                fool?.Position ?? Vector2.Zero,
                ReadVec(refl.PileShowPosField, draw, new Vector2(15f, 985f)),
                ReadVec(refl.PileHidePosField, draw, new Vector2(-135f, 1085f)),
                draw?.Scale ?? Vector2.One,
                ReadVec(refl.PileShowPosField, discard, new Vector2(1586f, 985f)),
                ReadVec(refl.PileHidePosField, discard, new Vector2(1736f, 1085f)),
                discard?.Scale ?? Vector2.One,
                ReadVec(refl.PileShowPosField, exhaust, new Vector2(1590f, 790f)),
                ReadVec(refl.PileHidePosField, exhaust, new Vector2(1740f, 790f)),
                exhaust?.Scale ?? Vector2.One);
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

            Control? ui = room.GetNodeOrNull<Control>("%CombatUi") ?? room.GetNodeOrNull<Control>("CombatUi");
            if (ui != null)
            {
                if ((ui.GetNodeOrNull<Control>("%EnergyCounterContainer") ?? ui.GetNodeOrNull<Control>("EnergyCounterContainer")) is { } energyCont)
                {
                    energyCont.AnchorLeft = 0f;
                    energyCont.AnchorRight = 0f;
                    energyCont.AnchorTop = _energyAnchorTop;
                    energyCont.AnchorBottom = _energyAnchorBottom;
                    energyCont.OffsetLeft = _energyOffLeft;
                    energyCont.OffsetTop = _energyOffTop;
                    energyCont.OffsetRight = _energyOffRight;
                    energyCont.OffsetBottom = _energyOffBottom;
                }
                if (refl.EnergyCounterField?.GetValue(ui) is Control energy)
                    energy.Scale = _energyScale;
                if (refl.StarCounterField?.GetValue(ui) is Control star)
                {
                    star.AnchorLeft = _starAnchorLeft;
                    star.AnchorTop = _starAnchorTop;
                    star.AnchorRight = _starAnchorRight;
                    star.AnchorBottom = _starAnchorBottom;
                    star.OffsetLeft = _starOffLeft;
                    star.OffsetTop = _starOffTop;
                    star.OffsetRight = _starOffRight;
                    star.OffsetBottom = _starOffBottom;
                    star.Size = _starSize;
                    star.Scale = _starScale;
                }
                if (refl.FoolCombatUiField?.GetValue(ui) is Control fool)
                {
                    fool.Scale = _foolScale;
                    fool.Position = _foolPos;
                }

                if ((ui.GetNodeOrNull<Control>("%EndTurnButton") ?? ui.GetNodeOrNull<Control>("EndTurnButton")) is { } endTurn)
                {
                    endTurn.Scale = Vector2.One;
                    int btnState = Convert.ToInt32(refl.EndTurnStateField?.GetValue(endTurn) ?? 2);
                    PropertyInfo? posProp = btnState == 2 ? refl.EndTurnHidePosProp : refl.EndTurnShowPosProp;
                    if (posProp?.GetValue(endTurn) is Vector2 landscapePos)
                        endTurn.Position = landscapePos;
                }

                if ((ui.GetNodeOrNull<Control>("%PingButton") ?? ui.GetNodeOrNull<Control>("PingButton")) is { } pingBtn)
                {
                    PhoneHitArea.Remove(pingBtn);
                    pingBtn.Scale = Vector2.One;
                    int pState = Convert.ToInt32(refl.PingStateField?.GetValue(pingBtn) ?? 2);
                    PropertyInfo? pPos = pState == 2 ? refl.PingHidePosProp : refl.PingShowPosProp;
                    if (pPos?.GetValue(pingBtn) is Vector2 lp)
                        pingBtn.Position = lp;
                }

                if ((ui.GetNodeOrNull<Control>("%CombatPileContainer") ?? ui.GetNodeOrNull<Control>("CombatPileContainer")) is { } piles)
                {
                    Vector2 vpSize = room.GetViewportRect().Size;
                    if ((piles.GetNodeOrNull<Control>("%DrawPile") ?? piles.GetNodeOrNull<Control>("DrawPile")) is { } draw)
                    {
                        PhoneHitArea.Remove(draw);
                        draw.Scale = _drawScale;
                        Vector2 show = new(15f, vpSize.Y - 95f);
                        Vector2 hide = show + new Vector2(-150f, 100f);
                        refl.PileShowPosField?.SetValue(draw, show);
                        refl.PileHidePosField?.SetValue(draw, hide);
                        if (draw.Visible) draw.Position = show;
                    }
                    if ((piles.GetNodeOrNull<Control>("%DiscardPile") ?? piles.GetNodeOrNull<Control>("DiscardPile")) is { } discard)
                    {
                        PhoneHitArea.Remove(discard);
                        discard.Scale = _discardScale;
                        Vector2 show = new(vpSize.X - 94f, vpSize.Y - 95f);
                        Vector2 hide = show + new Vector2(150f, 100f);
                        refl.PileShowPosField?.SetValue(discard, show);
                        refl.PileHidePosField?.SetValue(discard, hide);
                        if (discard.Visible) discard.Position = show;
                    }
                    if ((piles.GetNodeOrNull<Control>("%ExhaustPile") ?? piles.GetNodeOrNull<Control>("ExhaustPile")) is { } exhaust)
                    {
                        PhoneHitArea.Remove(exhaust);
                        exhaust.Scale = _exhaustScale;
                        refl.ExhaustSetAnimPositionsMethod?.Invoke(exhaust, null);
                        if (exhaust.Visible && refl.PileShowPosField?.GetValue(exhaust) is Vector2 show)
                            exhaust.Position = show;
                    }
                }

                if ((ui.GetNodeOrNull<Control>("%Hand") ?? ui.GetNodeOrNull<Control>("Hand")) is { } hand)
                {
                    refl.HandForceRefreshMethod?.Invoke(hand, null);
                }
            }

            if ((room.GetNodeOrNull<Control>("%ProceedButton") ?? room.GetNodeOrNull<Control>("ProceedButton")) is { } proceedBtn)
            {
                bool enabled = (bool)(refl.ButtonIsEnabledProp?.GetValue(proceedBtn) ?? false);
                PropertyInfo? prop = enabled ? refl.ProceedShowPosProp : refl.ProceedHidePosProp;
                if (prop?.GetValue(proceedBtn) is Vector2 p)
                    proceedBtn.Position = p;
            }
        }

        private static Vector2 ReadVec(FieldInfo? field, object? target, Vector2 fallback) =>
            target != null && field?.GetValue(target) is Vector2 v ? v : fallback;
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
        public readonly MethodInfo? HolderSetScaleInstantlyMethod;
        public readonly MethodInfo? HolderSetAngleInstantlyMethod;
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
            HolderSetScaleInstantlyMethod = handHolderType?.GetMethod("SetScaleInstantly", InstAll);
            HolderSetAngleInstantlyMethod = handHolderType?.GetMethod("SetAngleInstantly", InstAll);
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
