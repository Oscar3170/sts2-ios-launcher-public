using System;
using System.Collections.Generic;
using Godot;
using STS2Mobile.Platform;
using CombatReflection = STS2Mobile.GameHost.PortraitCombatLayout.CombatReflection;

namespace STS2Mobile.GameHost;

/// <summary>Owns portrait hand targets, in-hand selection, and the visual state of pooled hand cards.</summary>
internal sealed class PortraitHandLayout
{
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
    /// The Graphics "Reduce Effects" choice, for the hand: a card arriving from the draw pile draws without its glow (the
    /// <c>NCardHighlight</c> shader quad over the whole card) and without its shadow, and the glow's tween — the game's
    /// <c>AnimShow</c>, which writes the shader width on every frame of it — is stopped rather than run for half a second
    /// on every drawn card. A card that only re-spaces within the hand (a play closing the row, a draw pushing the earlier
    /// cards along, the hand's own enable/disable slide) keeps both: it restarts no tween. The hand decides once, not per
    /// card: every card drawn while the hand's effects window is open stays hidden until the hand has settled, then they all
    /// get their effects back on one frame, at the width the game would have left — the playable boost for a card that can
    /// be played, the game's own 0.075 for a gold or red glow, 0 for nothing. PhoneGraphics writes it before the next hand
    /// pass and the test host may set it directly; the pass runs every frame while the window is open and after any change,
    /// so it takes effect on the next frame.
    /// </summary>
    public static bool ReduceEffects { get; set; }

    // Whether the hand pass last saw a draw's batch open or a holder moving, and the frame it saw it on; see HandMoving.
    private static bool _handMoving;
    private static ulong _handMovingFrame;

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

    /// <summary>The game's hand hide (<c>NPlayerHand.AnimOut</c>): a 0.8 s tween to 500 units down.</summary>
    private const float NativeHandHideY = 500f;
    private const double NativeHandHideSeconds = 0.8;

    /// <summary>The hand hide that replaced the game's, so it is not replaced again.</summary>
    private ulong _handHideTweenId;

    private readonly Dictionary<ulong, Dictionary<ulong, Color>> _dimmedCardChildren = new();
    private readonly HashSet<ulong> _boostedHighlights = new();
    // Hand cards drawn from the pile whose glow and shadow Reduce Effects took away while they arrive, keyed by the card
    // node, with what they were before, so the pass can put them back together when the hand settles (a card keeps its
    // node for its whole life). The list is refilled per pass for the cards that left the hand.
    private readonly Dictionary<ulong, ReducedCardEffects> _reducedCardEffects = new();
    private readonly List<ulong> _reduceRestoreIds = new();
    private readonly HashSet<ulong> _liveReducedCardIds = new();
    private bool _lastReduceEffects;
    // The hand's own decision for those entries: opened by a card arriving from the draw pile, closed on one frame once
    // the hand has settled (see HandEffectsWindow). A card that merely re-spaces never opens it.
    private HandEffectsWindow _effectsWindow;
    private readonly HashSet<ulong> _wiredSelectionHands = new();
    private readonly HashSet<ulong> _wiredSelectionContainers = new();
    private readonly HashSet<ulong> _wiredSelectedHolders = new();
    private readonly HashSet<ulong> _wiredHolderRemovals = new();
    private readonly HashSet<ulong> _wiredHandContainers = new();

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
    private ulong _lastFocusedHolderId;
    private ulong _lastDraggedHolderId;
    private ulong _nativeLayoutHandId;
    private ulong _requestedNativeHandId;
    private ulong _layoutSessionHandId;
    private Control? _focusedHandVisual;
    private Vector2 _focusedHandVisualScale;
    private PortraitCombatBands? _lastHandBands;
    private bool _lastHandFlat;
    // The open draw batch is a turn's draw with a known size (set by the turn-number block): only such a draw is
    // direction-capped and waits TurnDrawQuietMsec for its next card; other batches (a card effect's draw, a refill of an
    // emptied hand) follow the native animation and the adaptive quiet window.
    private bool _turnDrawBatch;

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

    private readonly Func<CombatReflection> _getReflection;
    private readonly Dictionary<ulong, int> _enlargedFonts = new();
    private PortraitCombatBands? _selectionBands;
    private float _selectionCanvasHeight;

    public PortraitHandLayout(Func<CombatReflection> getReflection) => _getReflection = getReflection;

    public void Attach() => HandLayoutGate.TryOwnTargets = TryOwnNativeHandTargets;

    public void Detach()
    {
        HandLayoutGate.TryOwnTargets = null;
        ClearHandFocus();
    }

    private bool TryOwnNativeHandTargets(object nativeHand)
    {
        if (nativeHand is not Control hand || !hand.IsInsideTree()) return false;
        ulong id = hand.GetInstanceId();
        if (_nativeLayoutHandId != id)
        {
            CombatReflection refl = _getReflection();
            if (refl.HolderSetTargetPosMethod == null || refl.HolderSetTargetScaleMethod == null ||
                refl.HolderSetTargetAngleMethod == null || refl.HolderTargetPosField == null ||
                refl.HolderTargetScaleField == null || refl.HolderTargetAngleField == null)
                return false;
            _nativeLayoutHandId = id;
        }
        _handDirty = true;
        return true;
    }

    public Control? RememberHand(Control ui)
    {
        if (_cachedHand is { } known && GodotObject.IsInstanceValid(known) && known.IsInsideTree())
            return known;
        _cachedHand = ui.GetNodeOrNull<Control>("%Hand") ?? ui.GetNodeOrNull<Control>("Hand");
        return _cachedHand;
    }

    public void SetSelectionBounds(PortraitCombatBands bands, float canvasHeight)
    {
        _selectionBands = bands;
        _selectionCanvasHeight = canvasHeight;
    }

    public void OnHandUnavailable(CombatReflection refl)
    {
        ClearHandFocus();
        RestoreCardEffectsWithoutPass(refl);
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

    public void RestoreCardVisuals(CombatReflection refl)
    {
        // The reduce restore writes each card the width the game would show at rest; the dim restore resets boosted widths
        // to the game's own 0.075 and clears the boost set, so it must run first or a playable card keeps the portrait boost.
        RestoreAllReducedCardEffects();
        RestoreAllDimmedCards(refl);
    }

    public void RestoreFonts()
    {
        foreach ((ulong labelId, int fontSize) in _enlargedFonts)
        {
            if (GodotObject.InstanceFromId(labelId) is Label label && GodotObject.IsInstanceValid(label))
                label.AddThemeFontSizeOverride("font_size", fontSize);
        }
        _enlargedFonts.Clear();
    }

    public static void RefreshNativeTargets(Control ui, CombatReflection refl)
    {
        if ((ui.GetNodeOrNull<Control>("%Hand") ?? ui.GetNodeOrNull<Control>("Hand")) is { } hand)
        {
            refl.HandForceRefreshMethod?.Invoke(hand, null);
        }
    }

    public void EndSession()
    {
        ClearHandFocus();
        _nativeLayoutHandId = _requestedNativeHandId = _layoutSessionHandId = 0;
        ResetHandSession();
        _stillness.Clear();
    }

    /// <summary>
    /// The game hides the hand (combat end) by tweening it to a fixed 500 units down, enough for its one landscape row; the
    /// portrait hand is two rows tall, so its upper row stayed on screen under the rewards. The first frame the game's hide
    /// runs, it is replaced by the same 0.8 s ease-in/back tween to just below the canvas.
    /// </summary>
    private void ExtendHandHide(Control hand, CombatReflection refl, PortraitCombatBands bands)
    {
        if (refl.HandAnimOutTweenField?.GetValue(hand) is not Tween native || !GodotObject.IsInstanceValid(native) ||
            !native.IsRunning() || native.GetInstanceId() == _handHideTweenId)
            return;
        float canvasHeight = hand.GetViewport().GetVisibleRect().Size.Y;
        float hideY = PortraitCombatMath.HandHideOffset(canvasHeight, bands);
        if (hideY <= NativeHandHideY)
            return;
        native.Kill();
        Tween tween = hand.CreateTween();
        tween.TweenProperty(hand, "position", new Vector2(0f, hideY), NativeHandHideSeconds)
            .SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Back);
        refl.HandAnimOutTweenField.SetValue(hand, tween);
        _handHideTweenId = tween.GetInstanceId();
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
        _lastFocusedHolderId = _lastDraggedHolderId = 0;
        _lastReduceEffects = false;
        _effectsWindow.Reset();
        _handDirty = true;
    }

    public void PruneDead(List<ulong> dead)
    {
        dead.Clear();
        foreach (ulong id in _dimmedCardChildren.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _dimmedCardChildren.Remove(id);

        dead.Clear();
        foreach (ulong id in _boostedHighlights)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _boostedHighlights.Remove(id);

        dead.Clear();
        foreach (ulong id in _reducedCardEffects.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _reducedCardEffects.Remove(id);

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
        foreach (ulong id in _selectionHolderEase.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _selectionHolderEase.Remove(id);

        dead.Clear();
        foreach (ulong id in _selectionHolderOriginals.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _selectionHolderOriginals.Remove(id);

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
        foreach (ulong id in _cardVisualNodes.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _cardVisualNodes.Remove(id);

        dead.Clear();
        foreach (ulong id in _enlargedFonts.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _enlargedFonts.Remove(id);
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

    public void SyncHandAndPlayabilityPortrait(Control? hand, CombatReflection refl, PortraitCombatBands bands)
    {
        if (hand == null)
            return;

        Control? container = hand.GetNodeOrNull<Control>("%CardHolderContainer") ?? hand.GetNodeOrNull<Control>("CardHolderContainer");
        if (container == null)
            return;
        ExtendHandHide(hand, refl, bands);

        ulong handId = hand.GetInstanceId();
        if (_nativeLayoutHandId != handId)
        {
            // An existing fight may enter portrait with an unchanged hand. Give the native hook one chance to claim it.
            if (_requestedNativeHandId != handId && container.GetChildCount() > 0)
            {
                _requestedNativeHandId = handId;
                refl.HandForceRefreshMethod?.Invoke(hand, null);
                if (_nativeLayoutHandId != handId)
                    GD.PrintErr("Portrait hand: native layout hook unavailable; keeping the game's card targets.");
            }
            if (_nativeLayoutHandId != handId) return;
        }
        if (_layoutSessionHandId != handId)
        {
            // A new fight can replace the hand without leaving portrait. Don't let the previous hand consume turn one.
            _layoutSessionHandId = handId;
            ResetHandSession();
            _stillness.Clear();
            _drawPileNode = null;
        }

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

        // The emulated mouse can focus the card beneath a lifted finger. That is not a new press or a real pointer.
        int focusedIndex = -1;
        if (PhoneTouchCards.PressedHolder is { } pressedHolder)
            focusedIndex = activeHolders.IndexOf(pressedHolder);
        if (focusedIndex < 0 && !PhoneTouchCards.HoverFromTouch && focusedObj is Control focusedControl)
            focusedIndex = activeHolders.IndexOf(focusedControl);
        ulong focusedId = focusedIndex >= 0 ? activeHolders[focusedIndex].GetInstanceId() : 0;
        ulong draggedId = draggedHolder?.GetInstanceId() ?? 0;
        ulong returningId = draggedId == 0 ? _lastDraggedHolderId : 0;

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
        bool needsPass = _handDirty
            || _handDrawBatch
            || handMovedBeforePass
            // A card whose effects are still reduced needs the first frame it rests on, so it gets them back then, not on
            // the next idle pass.
            || _reducedCardEffects.Count > 0
            || FlatHand != _lastHandFlat
            || ReduceEffects != _lastReduceEffects
            || inCardPlay != _lastInCardPlay
            || focusedId != _lastFocusedHolderId || draggedId != _lastDraggedHolderId
            || _lastHandBands is not { } lastBands || !lastBands.Equals(bands)
            || frame - _handLastPassFrame >= HandIdlePassFrames;
        PortraitFrameTiming.SetCards(activeHolders.Count);
        PortraitFrameTiming.SetDrawing(_handDrawBatch);
        if (!needsPass)
            return;
        _handDirty = false;
        _handLastPassFrame = frame;
        _lastInCardPlay = inCardPlay;
        _lastFocusedHolderId = focusedId;
        _lastDraggedHolderId = draggedId;
        _lastHandBands = bands;
        _lastHandFlat = FlatHand;
        _lastReduceEffects = ReduceEffects;

        HashSet<ulong> activeDimmedCardIds = _activeDimmedCardIds;
        activeDimmedCardIds.Clear();
        bool anyShiftedThisFrame = false;
        // Whether any card arrived from the draw pile this pass: the hand hides its effects for the whole draw, not per card.
        bool anyArrivalThisFrame = false;
        // The card nodes this pass laid out: a reduced entry whose card is not here (the node was replaced, pooled, its
        // holder was skipped, or it left the hand) belongs to no card on screen and is put back in the sweep below.
        HashSet<ulong> liveCards = _liveReducedCardIds;
        liveCards.Clear();

        // The two state inputs a card's playability depends on, read once for the whole hand.
        (int energy, int stars) = refl.PlayerResources(hand);
        bool actionsDisabled = refl.PlayerActionsDisabled();

        WireSelectionUi(hand);
        UpdateHandFocus(focusedIndex >= 0 ? refl.CardHolderCardNodeProp?.GetValue(activeHolders[focusedIndex]) as Control : null);
        WireHandHolderContainer(container, hand.GetInstanceId());
        foreach (Control holder in allHoldersInHand)
        {
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
            PortraitCardSlot slot = PortraitCombatMath.ComputeHandCardSlot(handCount, slotIndex, bands, FlatHand);

            bool isFocused = focusedIndex == i;
            bool interactive = isFocused || PhoneTouchCards.CardIsBeingDragged ||
                PhoneTouchCards.PressedHolder == holder;
            var slotPosition = new Vector2(slot.LocalX, slot.LocalY);

            // A newly drawn card starts at the portrait pile before its first target starts moving it.
            bool arrived = !_stillness.HasRecord(holder);
            if (arrived)
            {
                if (DrawPileLocalPoint(hand, container) is { } pilePoint)
                    holder.Position = pilePoint;
                holder.Scale = new Vector2(slot.Scale, slot.Scale);
                holder.RotationDegrees = slot.AngleDegrees;
            }

            Vector2 currentTargetPos = (Vector2)(refl.HolderTargetPosField?.GetValue(holder) ?? holder.Position);
            Vector2 currentTargetScale = (Vector2)(refl.HolderTargetScaleField?.GetValue(holder) ?? holder.Scale);
            float currentTargetAngle = (float)(refl.HolderTargetAngleField?.GetValue(holder) ?? holder.RotationDegrees);
            // On return a native drag may have stopped the old animations even when the stored targets match the slot.
            ApplyHolderTargets(holder, refl, slot, isFocused, arrived || holder.GetInstanceId() == returningId,
                currentTargetPos, currentTargetScale, currentTargetAngle);
            CapArrivalMotion(holder, slot, slotPosition, hand, refl, interactive);
            bool holderShifted = _stillness.MovedOnScreen(holder);
            anyShiftedThisFrame |= holderShifted;
            anyArrivalThisFrame |= arrived;
            _stillness.Record(holder);

            if (cardNode != null)
            {
                liveCards.Add(cardNode.GetInstanceId());
                ApplyCardPlayabilityVisuals(holder, cardNode, refl, combatInProgress, playable, activeDimmedCardIds, arrived,
                    frame);
            }
        }

        // Publish the frame's motion once the layout has had its say: a draw's batch, a native step or the pass's own pin
        // all count, so the frame-rate governor never caps a frame the player sees a card moving on. The same motion (and
        // every arrival) restarts the effects window's settle count.
        if (anyShiftedThisFrame || anyArrivalThisFrame) _effectsWindow.NoteMotion(frame);
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

        // The hand's effects come back together: the window closes on the first frame the hand has settled (or its quiet
        // or hard bound), and the restore is one sweep over every entry, so no drawn card lights up on its own frame while
        // its neighbours are still hidden. A card that only re-spaced was never hidden and is not touched here.
        if (_reducedCardEffects.Count == 0)
            _effectsWindow.Reset();
        else if (_effectsWindow.ShouldRestore(ReduceEffects, _handDrawBatch, frame))
            RestoreAllReducedCardEffects();
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
        CombatReflection refl, bool interactive)
    {
        Vector2 previous = _stillness.PreviousPosition(holder, holder.Position);
        PortraitCombatMath.PortraitCardMotion motion = PortraitCombatMath.CapCardArrival(
            holder.Position.X, holder.Position.Y, previous.X, previous.Y,
            MathF.Max(holder.Scale.X, holder.Scale.Y), holder.RotationDegrees,
            slotPosition.X, slotPosition.Y, slot.Scale, slot.AngleDegrees,
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
    public void LayoutHandSelectionPortrait(Control hand, CombatReflection refl, PortraitCombatBands bands, float canvasHeight)
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
            if (!_selectionControls.TryGetValue(confirmId, out ControlDesktopState? confirmDesktop))
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
        else if (preview != null && _selectionControls.TryGetValue(preview.GetInstanceId(), out ControlDesktopState? previewDesktop))
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

            if (!_selectionHolderEase.TryGetValue(holderId, out SelectionHolderEase? ease))
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
            ResetSelectedHolderCard(holder, _getReflection());
            return;
        }

        ulong holderId = holder.GetInstanceId();
        if (holder.HasSignal("ready"))
        {
            holder.Connect("ready", Callable.From(() =>
            {
                if (GodotObject.InstanceFromId(holderId) is Control readyHolder && GodotObject.IsInstanceValid(readyHolder))
                    ResetSelectedHolderCard(readyHolder, _getReflection());
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
    public void RestoreSelectionUi()
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

        CombatReflection refl = _getReflection();
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

    private void UpdateHandFocus(Control? card)
    {
        if (ReferenceEquals(card, _focusedHandVisual)) return;
        ClearHandFocus();
        if (card == null || !card.IsInsideTree()) return;
        _focusedHandVisual = card;
        _focusedHandVisualScale = card.Scale;
        card.TreeExiting += ClearHandFocus;
        card.Scale = _focusedHandVisualScale * PortraitCombatMath.HeldCardGrowth;
    }

    private void ClearHandFocus()
    {
        if (_focusedHandVisual is not { } card) return;
        _focusedHandVisual = null;
        if (!GodotObject.IsInstanceValid(card)) return;
        card.TreeExiting -= ClearHandFocus;
        card.Scale = _focusedHandVisualScale;
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
        CombatReflection refl = _getReflection();
        if (refl.HandSelectedCardTweenField?.GetValue(hand) is not Tween tween)
            return;
        if (tween.IsRunning())
            tween.Kill();
        refl.HandSelectedCardTweenField.SetValue(hand, null);
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
        if (!SafeAreaCanvas.IsPortrait || _selectionBands is not { } bands)
            return;
        if (GodotObject.InstanceFromId(handId) is not Control hand || !GodotObject.IsInstanceValid(hand) || !hand.IsInsideTree())
            return;
        try
        {
            LayoutHandSelectionPortrait(hand, _getReflection(), bands, _selectionCanvasHeight);
        }
        catch (Exception error)
        {
            GD.PrintErr("Portrait selection relayout failed: ", error.Message);
        }
    }

    private void ApplyHolderTargets(Control holder, CombatReflection refl, PortraitCardSlot slot, bool isFocused,
        bool startMotion, Vector2 currentTargetPos, Vector2 currentTargetScale, float currentTargetAngle)
    {
        var targetPos = new Vector2(slot.LocalX, slot.LocalY);
        var targetScale = new Vector2(slot.Scale, slot.Scale);

        // Native setters start the interpolation immediately. Don't restart it when only focus has changed.
        if ((currentTargetPos - targetPos).LengthSquared() > 0.25f ||
            (startMotion && (holder.Position - targetPos).LengthSquared() > 1f))
        {
            _oneArg[0] = targetPos;
            refl.HolderSetTargetPosMethod!.Invoke(holder, _oneArg);
        }
        if (!currentTargetScale.IsEqualApprox(targetScale) ||
            (startMotion && !holder.Scale.IsEqualApprox(targetScale)))
        {
            _oneArg[0] = targetScale;
            refl.HolderSetTargetScaleMethod!.Invoke(holder, _oneArg);
        }
        if (MathF.Abs(currentTargetAngle - slot.AngleDegrees) > 0.05f ||
            (startMotion && MathF.Abs(holder.RotationDegrees - slot.AngleDegrees) > 0.1f))
        {
            _oneArg[0] = slot.AngleDegrees;
            refl.HolderSetTargetAngleMethod!.Invoke(holder, _oneArg);
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
        bool arrived,
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
            ApplyReducedCardEffects(holder, cardId, refl, combatInProgress, playable, body, highlight, arrived, frame);
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

        ApplyReducedCardEffects(holder, cardId, refl, combatInProgress, playable, body, highlight, arrived, frame);
    }

    /// <summary>
    /// What Reduce Effects took away from a drawn card. Both nodes are visible in the card scene and the game never
    /// hides either — <c>AnimHide</c> only takes the glow's shader width to 0 — so putting them back is simply visible,
    /// with the width the game would have been showing at rest.
    /// </summary>
    private struct ReducedCardEffects
    {
        public ulong HighlightId;
        // The width the game's own half-second tween was heading to, written back once instead (see the restore).
        public float HighlightWidth;
        public ulong ShadowId;
    }

    /// <summary>
    /// Reduce Effects on the hand, for the card this pass laid out. Only an arrival from the draw pile is hidden: its
    /// first frames at the pile and its glide to its slot are the expensive case, because the game runs its half-second
    /// <c>AnimShow</c> — the highlight's shader width written on every frame of it — for every card it draws. A card that
    /// merely re-spaces (a play closing the row, a draw pushing the earlier cards along, the hand's own enable/disable
    /// slide) restarts no tween and keeps its glow and shadow, so hiding it would lose the look and save nothing. The
    /// decision is the hand's, not the card's: an arrival opens <see cref="HandEffectsWindow"/>, every card drawn while
    /// it is open stays hidden, and the window's close restores them all on one frame in the pass's tail. A hidden card
    /// whose glow tween the game starts again has it killed once more, since that tween would write the width of a
    /// highlight nobody draws on every frame.
    /// </summary>
    private void ApplyReducedCardEffects(Control holder, ulong cardId, CombatReflection refl, bool combatInProgress,
        bool playable, Control? body, Control? highlight, bool arrived, long frame)
    {
        if (!ReduceEffects)
            return;
        if (!arrived)
        {
            // A card already in the hand can only re-space between slots: its effects are left exactly as they are. The
            // only work is stopping a tween the game restarted on a card that is still hidden.
            if (highlight != null && _reducedCardEffects.ContainsKey(cardId) &&
                refl.HighlightTweenField?.GetValue(highlight) is Tween running && running.IsRunning())
                running.Kill();
            return;
        }

        // A card node can come back from the pool under a new holder: put the old state back (the nodes are the same ones,
        // now this card's) and hide this card's from scratch.
        RestoreReducedCardEffects(cardId);
        _effectsWindow.Begin(frame);
        bool glowGold = (bool)(refl.HolderGlowGoldProp?.GetValue(holder) ?? false);
        bool glowRed = (bool)(refl.HolderGlowRedProp?.GetValue(holder) ?? false);
        var state = new ReducedCardEffects
        {
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
    /// the next pass, whatever the effects window says.
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

    /// <summary>
    /// Every reduced card gets its effects back on this one frame (the hand settling, Portrait Mode off, the setting off, or
    /// the layout giving up), and the effects window closes with them.
    /// </summary>
    private void RestoreAllReducedCardEffects()
    {
        _effectsWindow.Reset();
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
}
