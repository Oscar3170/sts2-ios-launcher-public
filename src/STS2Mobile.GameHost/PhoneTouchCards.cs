using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Makes the hand work under a finger, in portrait only: in landscape the hand is the game's own, as on a desktop.
/// The game's card play is built for a mouse: a press picks the card up and it follows the pointer until it
/// reaches the play zone. Here one gesture decides everything:
/// <list type="bullet">
/// <item>A press only arms the gesture. The card is not picked up until the finger has moved
/// <see cref="TouchCardMath.PickupSlop"/>, so a quick tap does nothing: no play, no selection, no inspect. The play then
/// starts with the finger still down, so the game's release-to-target path (and only that path) is used.</item>
/// <item>A still finger held for <see cref="TouchCardMath.HoldSeconds"/> opens the game's card inspect screen on that card.
/// Holding works for cards that cannot be played, which are never picked up by a drag.</item>
/// <item>While aiming, the valid creature nearest the finger (inside <see cref="TouchCardMath.TargetPadding"/>, then
/// <see cref="TouchCardMath.TargetSnap"/> for a near miss) is highlighted before the release, and the release plays on it.
/// A release off every target puts the card back; there is no click-to-aim fallback.</item>
/// <item>An untargeted card is drawn with its bottom edge <see cref="TouchCardMath.DragLift"/> above the finger, at the
/// hand's pressed scale (<c>HeldCardScaleFor</c>). Its position, scale, angle and Z are written in a render-frame pass,
/// after the game's own move for the frame, so the card follows the finger without a fight with the game's tween. Its
/// modulate is left at the game's own value: the dragged card is not faded, matching the native game.</item>
/// <item>A targeted card stops following the finger once native aiming starts: it eases to the fixed park
/// (<see cref="TouchCardMath.AimingCardCenter"/>: top on End Turn's top, centred) at
/// <see cref="TouchCardMath.AimedCardScaleFor"/> (one size per shape), while the native arrow and reticle track the
/// creature under the finger.</item>
/// <item>In the game's hand-selection modes (Armaments and the like) a press stays the game's own: tap-to-select
/// is unchanged.</item>
/// </list>
/// Keeping the other cards still under a touch is the portrait hand layout's job (<see cref="PortraitCombatLayout"/>); the
/// dragged card's presentation belongs to this pass.
/// </summary>
internal sealed class PhoneTouchCards
{
    private const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticAll = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const string HandHolderTypeName = "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder";
    private const int DraggedZIndex = 8;

    /// <summary>Marks the inspect screen's upgrade box while our combat hold owns its portrait placement, so the overlay
    /// layout leaves it alone instead of growing/repositioning it over the hand.</summary>
    internal const string CombatInspectMeta = "phone_combat_inspect_tickbox";

    /// <summary>How many times a stationary hold opened the inspect screen since launch (for the proof runs): 0 in landscape.</summary>
    public static int InspectsOpened { get; private set; }

    /// <summary>Whether this pass is dragging a hand card right now; the hover-tip pass keeps card tips away while it is.</summary>
    public static bool CardIsBeingDragged { get; private set; }

    /// <summary>
    /// The Graphics "Readable Drag" choice: true (the default) draws an untargeted dragged card with its bottom
    /// <see cref="TouchCardMath.DragLift"/> above the finger, so the card is never under the finger; false keeps the point
    /// the player grabbed under the finger, as the native mouse drag does. Read each frame, so the choice applies to the
    /// next drag; a targeted card parks at the aim position in either mode.
    /// </summary>
    public static bool ReadableDrag { get; set; } = true;

    /// <summary>
    /// The emulated pointer still belongs to the finger after release; a parked mouse must not enlarge a neighbour.
    /// Only new real pointer or controller input takes ownership back.
    /// </summary>
    public static bool HoverFromTouch { get; private set; }

    /// <summary>
    /// The hand card under a finger that is down in play mode and has not been picked up yet (after
    /// <see cref="TouchCardMath.PickupSlop"/> it is dragging instead); the portrait hand grows this card in place under
    /// the finger. Null on release, after pickup and while the inspector is open.
    /// </summary>
    internal static Control? PressedHolder { get; private set; }

    private readonly Reflection? _r;
    private readonly List<Control> _targetCreatures = new();
    private readonly List<TouchCardMath.TargetBox> _targetBoxes = new();
    private Callable _renderFrame;
    private bool _failed;
    private SceneTree? _tree;

    private bool _down;
    private bool _pickedUp;
    private bool _gestureDecided;
    private double _downTime;
    private Vector2 _downWindowPos;
    private float _maxMoveWindow;
    private Vector2? _downCanvasPos;
    private Vector2? _grabOffsetCanvas;
    private Vector2 _fingerCanvas;
    private Control? _pendingHolder;
    private Control? _dragHolder;
    private bool _dragPresentation;
    private int _dragOriginalZ;
    private bool _released;
    private ulong _releaseFrame;
    private Vector2? _releaseCanvasPos;
    private bool _releaseHandled;
    private bool _dragLogged;
    private float _drawnScale;
    private float _handScale = 1f;
    private bool _dragJustPicked;
    private bool _aimParked;
    private bool _aimStarted;
    private bool _aimLogged;
    private Control? _inspectShield;
    private Control? _placedInspectScreen;
    private Control? _placedUpgrade;
    private Vector2 _placedUpgradePivot;
    private Vector2 _placedUpgradeScale;
    private float _placedUpgradeLeft, _placedUpgradeTop, _placedUpgradeRight, _placedUpgradeBottom;
    private Node.ProcessModeEnum _inspectShieldMode;
    private bool _inspectShieldArmed;
    private bool _inspectShieldApplied;
    private ulong _inspectShieldReleaseFrame = ulong.MaxValue;
    private int _renderErrors;

    public PhoneTouchCards()
    {
        try
        {
            _r = new Reflection(typeof(NGame).Assembly);
            if (!_r.Complete)
            {
                _failed = true;
                GD.PrintErr("Phone touch cards: the game's hand no longer has the expected members; card touches are left as the game handles them.");
            }
        }
        catch (Exception error)
        {
            _failed = true;
            GD.PrintErr("Phone touch cards: off: ", error.Message);
        }
    }

    public void Attach(SceneTree tree)
    {
        _tree = tree;
        _renderFrame = Callable.From(OnRenderFrame);
        tree.Root.WindowInput += OnWindowInput;
        // One pass per frame: the gesture decisions read the hand, and a deferred call after the frame's process-frame
        // callbacks writes the dragged card's presentation after the game's own move for that frame.
        tree.ProcessFrame += OnFrame;
    }

    public void Detach()
    {
        if (_tree != null && GodotObject.IsInstanceValid(_tree.Root)) _tree.Root.WindowInput -= OnWindowInput;
        if (_tree != null) _tree.ProcessFrame -= OnFrame;
        _tree = null;
        ReleaseEverything();
    }

    private static double Now() => Time.GetTicksMsec() / 1000.0;


    /// <summary>
    /// Every frame, before the game's own process-frame work: in landscape the hand goes back to the game's own handling;
    /// in portrait a dragged card's presentation is scheduled for after that work.
    /// </summary>
    private void OnFrame()
    {
        if (_failed) return;
        try
        {
            if (_inspectShield != null && Engine.GetProcessFrames() > _inspectShieldReleaseFrame) RestoreInspectShield();
            if (_placedInspectScreen != null &&
                (!GodotObject.IsInstanceValid(_placedInspectScreen) || !_placedInspectScreen.IsVisibleInTree()))
                RestoreInspectUpgrade();
            if (!SafeAreaCanvas.IsPortrait)
            {
                HoverFromTouch = false;
                if (_dragHolder != null || CardIsBeingDragged || _down || _pendingHolder != null || _inspectShield != null ||
                    _placedInspectScreen != null)
                    ReleaseEverything();
                else HandPickupGate.Disable();
                return;
            }
            if (_dragHolder != null) _renderFrame.CallDeferred();
        }
        catch (Exception error)
        {
            // A targeting call the game rejects leaves this card to the game's own presentation; the touch path stays on.
            EndDrag();
            if (++_renderErrors <= 3) GD.PrintErr("Phone touch cards: a dragged card was left to the game: ", error.Message);
        }
    }

    /// <summary>
    /// The dragged card's presentation and the release decision, run from the message queue just after this frame's
    /// process-frame callbacks, so the game's own move for the frame has already happened.
    /// </summary>
    private void OnRenderFrame()
    {
        if (_failed) return;
        try
        {
            if (SafeAreaCanvas.IsPortrait && _dragHolder != null) PresentDrag();
        }
        catch (Exception error)
        {
            // A targeting call the game rejects leaves this card to the game's own presentation; the touch path stays on.
            EndDrag();
            if (++_renderErrors <= 3) GD.PrintErr("Phone touch cards: a dragged card was left to the game: ", error.Message);
        }
    }

    private void OnWindowInput(InputEvent input)
    {
        if (_failed) return;
        if (!SafeAreaCanvas.IsPortrait)
        {
            ReleaseEverything();
            return;
        }
        // A hold-opened inspector must not be dismissed by the release that ends the hold. The engine dispatches the
        // emulated mouse release before the touch release, and both reach this signal before GUI/_Input dispatch, so
        // arming the input shield here covers both without freezing the screen while the finger is down.
        if (_inspectShieldArmed &&
            (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } ||
             input is InputEventScreenTouch { Pressed: false }))
            ShieldInspectInput();
        if (input is InputEventScreenTouch touch && touch.Index == 0)
        {
            _fingerCanvas = ToCanvas(touch.Position);
            if (touch.Pressed)
            {
                if (_down) return;
                StartGesture(touch);
            }
            else if (_down)
            {
                EndGesture(touch);
            }
        }
        else if (input is InputEventMouseMotion { Device: >= 0 } or InputEventMouseButton { Device: >= 0, Pressed: true }
            or InputEventJoypadButton { Pressed: true } || input is InputEventJoypadMotion axis && MathF.Abs(axis.AxisValue) > 0.5f)
        {
            HoverFromTouch = false;
        }
        else if (input is InputEventScreenDrag drag && drag.Index == 0 && _down)
        {
            _fingerCanvas = ToCanvas(drag.Position);
            _maxMoveWindow = Math.Max(_maxMoveWindow, drag.Position.DistanceTo(_downWindowPos));
            // Pick up as soon as the finger has moved, while it is still down: the game's own play then starts in its
            // release-to-target mode and the release event the engine sends next finishes it.
            if (!_pickedUp && !_gestureDecided && _pendingHolder != null && MovedCanvas() >= TouchCardMath.PickupSlop)
                TryPickUp();
        }
    }

    private void StartGesture(InputEventScreenTouch touch)
    {
        if (_dragHolder != null) EndDrag();
        RestoreInspectShield();
        HoverFromTouch = true;
        PhoneHoverTips.UnparkAfterCardRelease(_fingerCanvas);
        _down = true;
        _pickedUp = false;
        _released = false;
        _releaseHandled = false;
        _gestureDecided = false;
        _pendingHolder = null;
        PressedHolder = null;
        _dragHolder = null;
        _downTime = Now();
        _downWindowPos = touch.Position;
        _maxMoveWindow = 0f;
        _releaseCanvasPos = null;
        _downCanvasPos = _fingerCanvas;

        Reflection r = _r!;
        Control? hand = r.Hand();
        if (hand == null)
        {
            _gestureDecided = true;
            return;
        }
        // The engine sent this touch's emulated mouse press before the touch itself, so the game's pressed handler has
        // already been held back by the gate. The card the game would have picked up is the one under the press.
        Vector2 point = _downCanvasPos ?? Vector2.Zero;
        if (r.InPlayMode(hand) && r.OverlayCount() == 0 && r.CurrentCardPlay(hand) == null)
        {
            _pendingHolder = HolderUnderPress(r, hand, point);
            PressedHolder = _pendingHolder;
            // Readable Drag off keeps this offset: the point grabbed at the press stays under the finger. The hit box is
            // the card shape centred on the holder, so its centre is the point the card's visual centre must keep.
            _grabOffsetCanvas = _pendingHolder is { } grabHolder ? CardCentre(grabHolder) - point : null;
        }
        else
        {
            _grabOffsetCanvas = null;
        }

        Control? endTurn = EndTurnButton(r);
        if (endTurn != null && GodotObject.IsInstanceValid(endTurn) && endTurn.GetGlobalRect().Grow(8f).HasPoint(point))
            PhoneHoverTips.ParkEndTurn(endTurn);
        else
            PhoneHoverTips.UnparkEndTurn();
    }

    private void EndGesture(InputEventScreenTouch touch)
    {
        _down = false;
        _maxMoveWindow = Math.Max(_maxMoveWindow, touch.Position.DistanceTo(_downWindowPos));
        _released = true;
        _releaseFrame = Engine.GetProcessFrames();
        _releaseCanvasPos = _fingerCanvas;
        // Letting a dragged card go is not a tap on whatever the finger lifted over.
        if (_pickedUp) PhoneHoverTips.ParkAfterCardRelease();
        if (!_pickedUp)
        {
            _pendingHolder = null;
            PressedHolder = null;
        }
        // Keep the input shield (armed above for this release) through this frame's dispatch; the next frame re-enables
        // the screen, so only this gesture's release is ignored.
        if (_inspectShieldArmed || _inspectShieldApplied)
            _inspectShieldReleaseFrame = Engine.GetProcessFrames();
    }

    /// <summary>Called each frame in portrait only.</summary>
    public void OnProcessFrame(SceneTree tree)
    {
        if (_failed || _r == null || !GodotObject.IsInstanceValid(tree.Root)) return;
        try
        {
            using (PortraitFrameTiming.Begin(PortraitFrameTiming.Touch))
                Step();
        }
        catch (Exception error)
        {
            _failed = true;
            ReleaseEverything();
            GD.PrintErr("Phone touch cards: turned off after an error: ", error);
        }
    }

    private void Step()
    {
        Reflection r = _r!;
        Control? hand = r.Hand();
        if (hand == null || !GodotObject.IsInstanceValid(hand) || !hand.IsVisibleInTree())
        {
            ReleaseEverything();
            return;
        }

        bool overlays = r.OverlayCount() > 0;
        bool playInProgress = r.CurrentCardPlay(hand) != null;
        bool playMode = r.InPlayMode(hand);

        // A press waits for the finger only while it could begin an ordinary card play. Selection modes and overlays
        // keep the game's own press handling; a play already under way does too.
        if (playMode && !overlays && !playInProgress) HandPickupGate.Enable();
        else HandPickupGate.Disable();

        if (overlays && !_pickedUp)
        {
            _pendingHolder = null;
            PressedHolder = null;
            _gestureDecided = true;
        }
        // A play the game started itself (a real mouse, or a press before the gate was armed): this gesture does not
        // take it over.
        if (playInProgress && !_pickedUp && _dragHolder == null)
        {
            _pendingHolder = null;
            PressedHolder = null;
        }
        // A card that left the hand under the finger (a discard or a play from elsewhere) no longer receives the press.
        if (PressedHolder is { } pressed && (!GodotObject.IsInstanceValid(pressed) || !pressed.IsInsideTree()))
        {
            _pendingHolder = null;
            PressedHolder = null;
        }

        float moved = MovedCanvas();
        if (_down && !_pickedUp && !playInProgress && _pendingHolder != null && !overlays && playMode)
        {
            if (TouchCardMath.HasMovedPastPickupSlop(moved))
            {
                TryPickUp();
            }
            else if (!_gestureDecided && TouchCardMath.IsStationaryHold(moved, Now() - _downTime))
            {
                _gestureDecided = true;
                Control holder = _pendingHolder;
                _pendingHolder = null;
                PressedHolder = null;
                Inspect(r, hand, holder, "hold");
            }
        }

        if (_dragHolder == null && _released) ResetGesture();
    }

    private void TryPickUp()
    {
        Reflection r = _r!;
        Control? hand = r.Hand();
        if (hand == null || _pendingHolder is not { } holder) return;
        if (r.OverlayCount() > 0 || !r.InPlayMode(hand) || r.CurrentCardPlay(hand) != null) return;
        if (!GodotObject.IsInstanceValid(holder) || !holder.IsInsideTree())
        {
            _pendingHolder = null;
            PressedHolder = null;
            return;
        }

        _pendingHolder = null;
        PressedHolder = null;
        _gestureDecided = true;
        // The card is about to be reparented to the hand and the game's BeginDrag raises the holder to 1. Keep the size it
        // has under the finger now (the pressed growth) so the pickup does not pop larger.
        float pressedCanvasScale = MathF.Abs((r.CardVisual(holder) ?? holder).GetGlobalTransformWithCanvas().Scale.Y);
        try
        {
            r.PressHandHolder(hand, holder);
        }
        catch (Exception error)
        {
            GD.PrintErr("Phone touch cards: the game did not start the card play: ", error.Message);
            return;
        }
        if (r.CurrentCardPlay(hand) == null)
        {
            // The game refused the play outright (for example a card that cannot be played this turn).
            return;
        }
        // The game measured the play zone from the point where it started the drag, which is now after the finger has
        // already moved. Measure it from the press instead, as it would have with an immediate pickup: a short drag off
        // the card stays out of the play zone and goes back, and a drag into the arena plays.
        if (_downCanvasPos is { } down) r.SetDragStart(hand, down.Y);

        _pickedUp = true;
        _dragHolder = holder;
        _handScale = MathF.Max(0.01f, MathF.Abs(
            (holder.GetParent() as CanvasItem)?.GetGlobalTransformWithCanvas().Scale.Y ?? 1f));
        float canvasHeight = _tree?.Root.GetVisibleRect().Size.Y ?? 1920f;
        PortraitCombatBands pickupBands = PortraitCombatMath.ComputeCombatBands(canvasHeight, 0f);
        _drawnScale = pressedCanvasScale > 0.01f
            ? pressedCanvasScale
            : PortraitCombatMath.HeldCardScaleFor(pickupBands, HandCardCount(r, hand)) * _handScale;
        _dragJustPicked = true;
        _aimParked = false;
        _aimStarted = false;
        _aimLogged = false;
        _dragOriginalZ = holder.ZIndex;
        CardIsBeingDragged = true;
        _dragPresentation = true;
        _dragLogged = false;
        PresentDrag(holder);
        GD.Print("Phone touch cards: card picked up after the finger moved.");
    }

    /// <summary>
    /// Moves the dragged card's presentation to the finger, after the game's own per-frame move. Called once at pickup and
    /// then from the render pass every frame; the game keeps writing its raw pointer target, but this pass is what is drawn.
    /// </summary>
    private void PresentDrag()
    {
        Reflection r = _r!;
        Control? hand = r.Hand();
        if (hand == null || _dragHolder is not { } holder) return;
        if (!GodotObject.IsInstanceValid(holder) || !holder.IsInsideTree())
        {
            EndDrag();
            return;
        }
        object? play = r.CurrentCardPlay(hand);
        if (play == null || !r.IsMouseCardPlay(play) || !ReferenceEquals(r.PlayHolder(play), holder))
        {
            // Played, cancelled, or put back by the game: the card is the hand's again.
            EndDrag();
            return;
        }

        PresentDrag(holder);

        if (r.IsTargeting()) HoverAssist(r);

        if (!_released || _releaseHandled) return;
        ulong age = Engine.GetProcessFrames() - _releaseFrame;
        if (r.IsTargeting() && age >= 2)
            _releaseHandled = ResolveTargetingRelease(r);
        if (!_releaseHandled && age >= 12 && r.IsSingleTarget(holder))
        {
            // Released before the game entered targeting, or its click-to-aim fallback was entered: never leave the
            // card waiting for a click that will not come.
            _releaseHandled = true;
            r.CancelAllCardPlay(hand);
        }
    }

    private void PresentDrag(Control holder)
    {
        SceneTree? tree = holder.GetTree();
        if (tree == null) return;
        Vector2 finger = _fingerCanvas;
        Vector2 canvas = tree.Root.GetVisibleRect().Size;
        Control? parent = holder.GetParent() as Control;
        Transform2D parentTransform = parent?.GetGlobalTransformWithCanvas() ?? Transform2D.Identity;
        // The hit box is 300 x 422 at the holder's scale, and the hand container may be scaled too: the lift is measured
        // on the card as drawn, not on its parent-local units. The size eases from the pressed card's size to the drag
        // size (and larger while aiming) instead of jumping at pickup. Resting/pressed sizes come from the shape- and
        // hand-count-aware helpers, not a fixed constant.
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvas.Y, 0f);
        int count = HandCardCount(_r!, _r!.Hand());
        float restingScale = PortraitCombatMath.HandCardScaleFor(bands, count);
        float heldScale = PortraitCombatMath.HeldCardScaleFor(bands, count);
        float aimScale = TouchCardMath.AimedCardScaleFor(bands);
        // Like the mod's Center/Suspend latch: once native aiming starts the card stays parked until the play is over,
        // including the release/cancel frames while the arrow is being taken down.
        if (_r?.IsTargeting() == true) _aimStarted = true;
        bool aiming = _aimStarted;
        float targetCanvas = (aiming ? aimScale : heldScale) * _handScale;
        if (_dragJustPicked) _dragJustPicked = false;
        else _drawnScale = Mathf.Lerp(_drawnScale, targetCanvas, 0.18f);
        float onCanvas = MathF.Max(_drawnScale, 0.01f);
        float localScale = onCanvas / _handScale;
        Vector2 local;
        Vector2 canvasTarget;
        if (aiming)
        {
            // The card stops following the finger and sits at a fixed park: top edge on End Turn's top, centred, while the
            // native arrow and reticle follow the finger. Its modulate is the game's own (not faded) and it is clearly
            // larger than the pressed card; the move eases, and the top edge stays aligned while the size grows.
            float endTurnTop = EndTurnTop(_r!, canvas, bands);
            (float px, float py) = TouchCardMath.AimingCardCenter(endTurnTop, onCanvas, canvas.X);
            canvasTarget = new Vector2(px, py);
            if (!_aimLogged)
            {
                _aimLogged = true;
                GD.Print($"Phone touch cards: aiming park target=({px:F1},{py:F1}) endTurnTop={endTurnTop:F1} " +
                    $"count={count} resting={restingScale:F3} held={heldScale:F3} aim={aimScale:F3} " +
                    $"onCanvas={onCanvas:F3}");
            }
            Vector2 park = parentTransform.AffineInverse() * canvasTarget;
            local = _aimParked ? park : holder.Position.Lerp(park, 0.25f);
            if (local.DistanceTo(park) < 0.75f)
            {
                local = park;
                _aimParked = true;
            }
        }
        else
        {
            _aimParked = false;
            float x, y;
            if (!ReadableDrag && _grabOffsetCanvas is { } grab)
            {
                // The native mouse drag: the card's centre keeps the offset it had at the press, so the grabbed point
                // stays under the finger. The lifted placement is what Readable Drag turns off.
                (x, y) = TouchCardMath.GrabbedCardCenter(
                    finger.X, finger.Y, grab.X, grab.Y, onCanvas, canvas.X, canvas.Y);
            }
            else
            {
                (x, y) = TouchCardMath.DraggedCardCenter(
                    finger.X, finger.Y, onCanvas, canvas.X, canvas.Y);
            }
            canvasTarget = new Vector2(x, y);
            local = parentTransform.AffineInverse() * canvasTarget;
        }
        holder.Position = local;
        holder.Scale = Vector2.One * localScale;
        holder.RotationDegrees = 0f;
        holder.ZIndex = DraggedZIndex;
        // The holder's own animation runs off this target; it is put to the lift as well, after the game's raw pointer
        // write for this frame, so the animation follows the portrait move.
        _r?.SetTargetPosition(holder, local);
        _r?.SetTargetScale(holder, Vector2.One * localScale);
        if (!_dragLogged)
        {
            _dragLogged = true;
            Control? hitbox = holder.GetNodeOrNull<Control>("%Hitbox") ?? holder.GetNodeOrNull<Control>("Hitbox");
            Rect2 rect = hitbox?.GetGlobalRect() ?? default;
            GD.Print($"Phone touch cards: drag finger=({finger.X:F1},{finger.Y:F1}) parentScale={parentTransform.Scale} " +
                $"holderScale={holder.Scale} onCanvas={onCanvas:F3} handScale={_handScale:F3} count={count} " +
                $"resting={restingScale:F3} held={heldScale:F3} aim={aimScale:F3} aiming={aiming} " +
                $"readable={ReadableDrag} grab={_grabOffsetCanvas} " +
                $"canvasTarget=({canvasTarget.X:F1},{canvasTarget.Y:F1}) localTarget={local} holderPos={holder.Position} " +
                $"hitbox={rect} bottom={rect.End.Y:F1} lift={finger.Y - rect.End.Y:F1} expected={TouchCardMath.DragLift:F0}");
        }
    }

    private void EndDrag()
    {
        if (_dragHolder is { } holder && _dragPresentation && GodotObject.IsInstanceValid(holder))
        {
            holder.ZIndex = _dragOriginalZ;
            if (_dragLogged)
            {
                Control? hitbox = holder.GetNodeOrNull<Control>("%Hitbox") ?? holder.GetNodeOrNull<Control>("Hitbox");
                GD.Print($"Phone touch cards: drag ended holderPos={holder.Position} scale={holder.Scale} " +
                    $"hitbox={(hitbox?.GetGlobalRect() ?? default)}");
                _dragLogged = false;
            }
        }
        _dragHolder = null;
        _dragPresentation = false;
        _pickedUp = false;
        _aimParked = false;
        _aimStarted = false;
        _aimLogged = false;
        CardIsBeingDragged = false;
    }

    /// <summary>
    /// The finger is up while the game is still choosing a target. A near-miss release plays on the nearest valid
    /// creature; a release further out than <see cref="TouchCardMath.TargetSnap"/> cancels the targeting, which puts the
    /// card back. Returns whether the targeting was resolved.
    /// </summary>
    private bool ResolveTargetingRelease(Reflection r)
    {
        Vector2 point = _releaseCanvasPos ?? _fingerCanvas;
        Control? target = FindTarget(r, point);
        if (target != null)
        {
            r.Hover(target);
            if (r.FinishTargeting(false))
            {
                GD.Print("Phone touch cards: released near a target; the card plays on it.");
                return true;
            }
        }
        if (r.CancelTargeting())
        {
            GD.Print("Phone touch cards: card put back (released off every target).");
            return true;
        }
        return false;
    }

    /// <summary>
    /// The valid creature nearest the finger: inside its padded hit box, else within the near-miss snap. Moves the game's
    /// hover to it so the reticle and the highlight are on the creature the release will choose.
    /// </summary>
    private void HoverAssist(Reflection r)
    {
        if (!r.CanTarget || _tree == null) return;
        Vector2 point = _fingerCanvas;
        Control? target = FindTarget(r, point);
        Node? hovered = r.HoveredNode();
        if (ReferenceEquals(hovered, target)) return;
        if (hovered is Control old && GodotObject.IsInstanceValid(old) && r.IsCreature(old)) r.Unhover(old);
        if (target != null) r.Hover(target);
    }

    private Control? FindTarget(Reflection r, Vector2 point)
    {
        if (!r.CanTarget) return null;
        List<Control> creatures = _targetCreatures;
        List<TouchCardMath.TargetBox> boxes = _targetBoxes;
        creatures.Clear();
        boxes.Clear();
        foreach (object? node in r.Creatures())
        {
            if (node is not Control creature || !GodotObject.IsInstanceValid(creature) || !creature.IsInsideTree()) continue;
            if (!r.AllowedToTarget(creature)) continue;
            Control? hitbox = creature.GetNodeOrNull<Control>("%Hitbox") ?? creature.GetNodeOrNull<Control>("Hitbox");
            if (hitbox == null || hitbox.Size.X < 1f || hitbox.Size.Y < 1f) continue;
            Rect2 rect = hitbox.GetGlobalRect();
            creatures.Add(creature);
            boxes.Add(new TouchCardMath.TargetBox(
                rect.GetCenter().X, rect.GetCenter().Y, rect.Size.X * 0.5f, rect.Size.Y * 0.5f));
        }
        int index = TouchCardMath.NearestTarget(boxes, point.X, point.Y);
        return index >= 0 ? creatures[index] : null;
    }

    /// <summary>
    /// The hand card under the press: the game's focused card when the point is on it, else the topmost holder whose hit
    /// box contains the point (the game's own pick is by draw order, and a higher Z is drawn on top).
    /// </summary>
    private static Control? HolderUnderPress(Reflection r, Control hand, Vector2 point)
    {
        if (r.FocusedHolder(hand) is { } focused && GodotObject.IsInstanceValid(focused) && HitboxContains(focused, point))
            return focused;
        IList? holders = r.ActiveHolders(hand);
        if (holders == null) return null;
        Control? best = null;
        int bestZ = int.MinValue;
        int bestIndex = -1;
        for (int i = 0; i < holders.Count; i++)
        {
            if (holders[i] is not Control holder || !GodotObject.IsInstanceValid(holder) || !holder.IsVisibleInTree()) continue;
            if (!HitboxContains(holder, point)) continue;
            if (holder.ZIndex < bestZ || (holder.ZIndex == bestZ && i <= bestIndex)) continue;
            best = holder;
            bestZ = holder.ZIndex;
            bestIndex = i;
        }
        return best;
    }

    private static bool HitboxContains(Control holder, Vector2 point)
    {
        Control? hitbox = holder.GetNodeOrNull<Control>("%Hitbox") ?? holder.GetNodeOrNull<Control>("Hitbox");
        if (hitbox == null || hitbox.Size.X < 1f) return false;
        // The game only delivers the press to a hit box that is enabled (NClickableControl._GuiInput).
        if (hitbox.GetType().GetProperty("IsEnabled", InstAll)?.GetValue(hitbox) is false) return false;
        Vector2 local = hitbox.GetGlobalTransformWithCanvas().AffineInverse() * point;
        return new Rect2(Vector2.Zero, hitbox.Size).HasPoint(local);
    }

    /// <summary>The card's visual centre on the canvas; the hit box is the card shape centred on the holder.</summary>
    private static Vector2 CardCentre(Control holder)
    {
        Control? hitbox = holder.GetNodeOrNull<Control>("%Hitbox") ?? holder.GetNodeOrNull<Control>("Hitbox");
        return hitbox?.GetGlobalRect().GetCenter() ?? holder.GetGlobalRect().GetCenter();
    }

    private void ResetGesture()
    {
        RestoreInspectShield();
        _down = false;
        _pickedUp = false;
        _released = false;
        _releaseHandled = false;
        _gestureDecided = false;
        _pendingHolder = null;
        PressedHolder = null;
        _downCanvasPos = null;
        _grabOffsetCanvas = null;
        _releaseCanvasPos = null;
        _maxMoveWindow = 0f;
    }

    private void ReleaseEverything()
    {
        EndDrag();
        ResetGesture();
        HoverFromTouch = false;
        RestoreInspectUpgrade();
        HandPickupGate.Disable();
        PhoneHoverTips.UnparkEndTurn();
        PhoneHoverTips.UnparkAfterCardRelease();
    }

    private float MovedCanvas() =>
        _tree == null ? 0f : _maxMoveWindow * CanvasPerWindowPixel(_tree.Root);

    /// <summary>The touch's position on the portrait canvas. The finger is the authority, not the engine's emulated mouse:
    /// the host pointer can sit a little away from the injected touch, which would lift the card from the wrong point.</summary>
    private Vector2 ToCanvas(Vector2 windowPosition) =>
        _tree == null ? windowPosition : _tree.Root.GetFinalTransform().AffineInverse() * windowPosition;

    private static float CanvasPerWindowPixel(Window root)
    {
        Vector2I window = DisplayServer.WindowGetSize();
        float canvasWidth = root.GetVisibleRect().Size.X;
        return window.X <= 0 ? 1f : canvasWidth / window.X;
    }

    /// <summary>The hand's size for the scale helpers: the visible holders plus the card being played.</summary>
    private static int HandCardCount(Reflection r, Control? hand)
    {
        if (hand == null || !GodotObject.IsInstanceValid(hand)) return 1;
        int active = r.ActiveHolders(hand)?.Count ?? 0;
        return PortraitCombatMath.EffectiveCardCount(active, r.CurrentCardPlay(hand) != null);
    }

    /// <summary>The End Turn button's visual top, preferring the live control; the HUD layout is the fallback.</summary>
    private static float EndTurnTop(Reflection r, Vector2 canvas, PortraitCombatBands bands)
    {
        if (EndTurnButton(r) is { } endTurn && GodotObject.IsInstanceValid(endTurn) && endTurn.IsVisibleInTree())
            return endTurn.GetGlobalRect().Position.Y;
        return TouchCardMath.EndTurnTopY(PortraitCombatMath.ComputeHudLayout(canvas.Y, bands));
    }

    private static Control? EndTurnButton(Reflection r) =>
        r.CombatUi() is { } ui ? ui.GetNodeOrNull<Control>("%EndTurnButton") ?? ui.GetNodeOrNull<Control>("EndTurnButton") : null;

    private void Inspect(Reflection r, Control hand, Control holder, string how)
    {
        if (!GodotObject.IsInstanceValid(holder) || r.OverlayCount() > 0) return;
        IList? holders = r.ActiveHolders(hand);
        object? card = r.HolderCardModel(holder);
        if (holders == null || card == null) return;
        IList cards = r.NewCardList();
        int index = 0;
        foreach (object? h in holders)
        {
            if (h is not Control other || r.HolderCardModel(other) is not { } model) continue;
            if (other == holder) index = cards.Count;
            cards.Add(model);
        }
        if (cards.Count == 0) cards.Add(card);
        if (r.OpenInspect(cards, index))
        {
            InspectsOpened++;
            if (how == "hold")
            {
                // Keep the native screen fully processing (its open tween draws the dim, card and arrows) and only shield
                // its input for the later release.
                ArmInspectShield(r);
            }
            GD.Print($"Phone touch cards: {how} opened card {index + 1} of {cards.Count} large.");
        }
    }

    /// <summary>
    /// Remembers the inspector a hold opened and moves its native "View Upgrades" control clear of the card and hand. The
    /// screen itself keeps processing so its open tween (dim, card, arrows) runs; only the release gets the input shield.
    /// </summary>
    private void ArmInspectShield(Reflection r)
    {
        if (_inspectShield != null) RestoreInspectShield();
        RestoreInspectUpgrade();
        if (r.InspectScreen() is not { } screen || !GodotObject.IsInstanceValid(screen)) return;
        _inspectShield = screen;
        _inspectShieldMode = screen.ProcessMode;
        _inspectShieldArmed = true;
        _inspectShieldApplied = false;
        _inspectShieldReleaseFrame = ulong.MaxValue;
        PlaceInspectUpgrade(screen);
    }

    /// <summary>
    /// Takes the screen out of input picking for exactly the release dispatch (a disabled process mode is skipped by the
    /// viewport's GUI and _Input paths). It is re-enabled on the next frame.
    /// </summary>
    private void ShieldInspectInput()
    {
        if (!_inspectShieldArmed) return;
        _inspectShieldArmed = false;
        if (_inspectShield is not { } screen || !GodotObject.IsInstanceValid(screen)) return;
        _inspectShieldMode = screen.ProcessMode;
        screen.ProcessMode = Node.ProcessModeEnum.Disabled;
        _inspectShieldApplied = true;
        _inspectShieldReleaseFrame = Engine.GetProcessFrames();
    }

    private void RestoreInspectShield()
    {
        if (_inspectShieldApplied && _inspectShield != null && GodotObject.IsInstanceValid(_inspectShield))
            _inspectShield.ProcessMode = _inspectShieldMode;
        _inspectShield = null;
        _inspectShieldArmed = false;
        _inspectShieldApplied = false;
        _inspectShieldReleaseFrame = ulong.MaxValue;
    }

    /// <summary>
    /// The native inspect screen's "View Upgrades" box sits below the centred card, which in portrait is over the hand's
    /// upper row. Native keeps it visible for a card with an upgrade level; in portrait it is moved to the left of the
    /// card, just above the hand-control rail, scaled to the free margin.
    /// </summary>
    private void PlaceInspectUpgrade(Control screen)
    {
        if (screen.GetNodeOrNull<Control>("%Upgrade") is not { } upgrade) return;
        if (_placedInspectScreen != null) RestoreInspectUpgrade();
        _placedInspectScreen = screen;
        _placedUpgrade = upgrade;
        upgrade.SetMeta(CombatInspectMeta, true);
        _placedUpgradePivot = upgrade.PivotOffset;
        _placedUpgradeScale = upgrade.Scale;
        _placedUpgradeLeft = upgrade.OffsetLeft;
        _placedUpgradeTop = upgrade.OffsetTop;
        _placedUpgradeRight = upgrade.OffsetRight;
        _placedUpgradeBottom = upgrade.OffsetBottom;
        // The native box is 272 wide and sits under the centred card, which in portrait is over the hand. The card is
        // 600 wide with 240 free on the left, so scale the box slightly and put it in that margin, level with the card's
        // top and inside the stage band, clear of the card, the arrows, the rail and the hand.
        float stageTop = screen.GetTree() is { } tree
            ? PortraitLayout.ComputeCapHeight(tree.Root) + PortraitBands.RunContentTop
            : 0f;
        (float left, float top, float width, float height, float scale) =
            TouchCardMath.InspectUpgradeRect(screen.Size.Y, stageTop);
        upgrade.PivotOffset = Vector2.Zero;
        upgrade.Scale = new Vector2(scale, scale);
        float halfW = screen.Size.X * 0.5f, halfH = screen.Size.Y * 0.5f;
        upgrade.OffsetLeft = left - halfW;
        upgrade.OffsetRight = left - halfW + width;
        upgrade.OffsetTop = top - halfH;
        upgrade.OffsetBottom = top - halfH + height;
    }

    /// <summary>Puts the native upgrade box back where its other screens (deck, card grid) expect it.</summary>
    private void RestoreInspectUpgrade()
    {
        if (_placedUpgrade != null && GodotObject.IsInstanceValid(_placedUpgrade))
        {
            _placedUpgrade.RemoveMeta(CombatInspectMeta);
            _placedUpgrade.PivotOffset = _placedUpgradePivot;
            _placedUpgrade.Scale = _placedUpgradeScale;
            _placedUpgrade.OffsetLeft = _placedUpgradeLeft;
            _placedUpgrade.OffsetTop = _placedUpgradeTop;
            _placedUpgrade.OffsetRight = _placedUpgradeRight;
            _placedUpgrade.OffsetBottom = _placedUpgradeBottom;
        }
        _placedUpgrade = null;
        _placedInspectScreen = null;
    }

    private sealed class Reflection
    {
        private readonly PropertyInfo? _roomInstance;
        private readonly PropertyInfo? _roomUi;
        private readonly PropertyInfo? _uiHand;
        private readonly FieldInfo? _roomCreatureNodes;
        private readonly FieldInfo? _currentCardPlay;
        private readonly MethodInfo? _cancelAll;
        private readonly MethodInfo? _pressHandHolder;
        private readonly PropertyInfo? _activeHolders;
        private readonly PropertyInfo? _focusedHolder;
        private readonly PropertyInfo? _playHolder;
        private readonly Type? _mouseCardPlayType;
        private readonly FieldInfo? _dragStartY;
        private readonly MethodInfo? _setTargetPosition;
        private readonly MethodInfo? _setTargetScale;
        private readonly PropertyInfo? _targetManagerInstance;
        private readonly PropertyInfo? _isInSelection;
        private readonly MethodInfo? _allowedToTarget;
        private readonly MethodInfo? _onNodeHovered;
        private readonly MethodInfo? _onNodeUnhovered;
        private readonly PropertyInfo? _hoveredNode;
        private readonly MethodInfo? _finishTargeting;
        private readonly MethodInfo? _cancelTargeting;
        private readonly PropertyInfo? _holderCardModel;
        private readonly PropertyInfo? _holderCardVisual;
        private readonly PropertyInfo? _targetType;
        private readonly PropertyInfo? _overlayInstance;
        private readonly PropertyInfo? _overlayCount;
        private readonly PropertyInfo? _capstoneInstance;
        private readonly PropertyInfo? _capstoneInUse;
        private readonly PropertyInfo? _gameInstance;
        private readonly MethodInfo? _getInspectScreen;
        private readonly Type? _cardModelType;
        private readonly PropertyInfo? _currentMode;

        public Reflection(Assembly sts2)
        {
            Type? roomType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom");
            _roomInstance = roomType?.GetProperty("Instance", StaticAll);
            _roomUi = roomType?.GetProperty("Ui", InstAll);
            _roomCreatureNodes = roomType?.GetField("_creatureNodes", InstAll);
            _uiHand = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NCombatUi")?.GetProperty("Hand", InstAll);
            Type? handType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand");
            _currentCardPlay = handType?.GetField("_currentCardPlay", InstAll);
            _cancelAll = handType?.GetMethod("CancelAllCardPlay", InstAll, Type.EmptyTypes);
            _pressHandHolder = handType?.GetMethod("OnHolderPressed", InstAll);
            _activeHolders = handType?.GetProperty("ActiveHolders", InstAll);
            _focusedHolder = handType?.GetProperty("FocusedHolder", InstAll);
            _currentMode = handType?.GetProperty("CurrentMode", InstAll);
            _playHolder = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NCardPlay")?.GetProperty("Holder", InstAll);
            _mouseCardPlayType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NMouseCardPlay");
            _dragStartY = _mouseCardPlayType?.GetField("_dragStartYPosition", InstAll);
            Type? holderType = sts2.GetType(HandHolderTypeName);
            _holderCardModel = holderType?.GetProperty("CardModel", InstAll);
            _holderCardVisual = holderType?.GetProperty("CardNode", InstAll);
            _setTargetPosition = holderType?.GetMethod("SetTargetPosition", InstAll, [typeof(Vector2)]);
            _setTargetScale = holderType?.GetMethod("SetTargetScale", InstAll, [typeof(Vector2)]);
            Type? targetType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NTargetManager");
            _targetManagerInstance = targetType?.GetProperty("Instance", StaticAll);
            _isInSelection = targetType?.GetProperty("IsInSelection", InstAll);
            _allowedToTarget = targetType?.GetMethod("AllowedToTargetNode", InstAll, [typeof(Node)]);
            _onNodeHovered = targetType?.GetMethod("OnNodeHovered", InstAll, [typeof(Node)]);
            _onNodeUnhovered = targetType?.GetMethod("OnNodeUnhovered", InstAll, [typeof(Node)]);
            _hoveredNode = targetType?.GetProperty("HoveredNode", InstAll);
            _finishTargeting = targetType?.GetMethod("FinishTargeting", InstAll, [typeof(bool)]);
            _cancelTargeting = targetType?.GetMethod("CancelTargeting", InstAll, Type.EmptyTypes);
            Type? overlayType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack");
            _overlayInstance = overlayType?.GetProperty("Instance", StaticAll);
            _overlayCount = overlayType?.GetProperty("ScreenCount", InstAll);
            // The deck view, the pile screens and the in-run pause/settings open in the capstone container, not the overlay
            // stack; while one is open the hand under it must not take a touch either.
            Type? capstoneType = sts2.GetType("MegaCrit.Sts2.Core.Nodes.Screens.Capstones.NCapstoneContainer");
            _capstoneInstance = capstoneType?.GetProperty("Instance", StaticAll);
            _capstoneInUse = capstoneType?.GetProperty("InUse", InstAll);
            _gameInstance = typeof(NGame).GetProperty("Instance", StaticAll);
            _getInspectScreen = typeof(NGame).GetMethod("GetInspectCardScreen", InstAll, Type.EmptyTypes);
            _cardModelType = sts2.GetType("MegaCrit.Sts2.Core.Models.CardModel");
            _targetType = _cardModelType?.GetProperty("TargetType", InstAll);
        }

        /// <summary>Whether a release can be snapped to a target at all; without the members the game's cancel handling is
        /// used instead.</summary>
        public bool CanTarget =>
            _targetManagerInstance != null && _allowedToTarget != null && _onNodeHovered != null &&
            _roomCreatureNodes != null && _cancelTargeting != null;

        public bool InPlayMode(Control hand) => _currentMode?.GetValue(hand)?.ToString() == "Play";

        public bool Complete =>
            _roomInstance != null && _roomUi != null && _uiHand != null && _roomCreatureNodes != null &&
            _currentCardPlay != null && _cancelAll != null && _pressHandHolder != null && _activeHolders != null &&
            _playHolder != null && _mouseCardPlayType != null && _targetManagerInstance != null && _isInSelection != null &&
            _holderCardModel != null && _gameInstance != null && _getInspectScreen != null && _cardModelType != null &&
            _cancelTargeting != null;

        public Control? Hand()
        {
            object? room = _roomInstance!.GetValue(null);
            if (room is not Node node || !GodotObject.IsInstanceValid(node) || !node.IsInsideTree()) return null;
            object? ui = _roomUi!.GetValue(room);
            return ui == null ? null : _uiHand!.GetValue(ui) as Control;
        }

        public Control? CombatUi()
        {
            object? room = _roomInstance!.GetValue(null);
            if (room is not Node node || !GodotObject.IsInstanceValid(node)) return null;
            return _roomUi!.GetValue(room) as Control;
        }

        public object? CurrentCardPlay(Control hand) =>
            _currentCardPlay!.GetValue(hand) is Node play && GodotObject.IsInstanceValid(play) && play.IsInsideTree() ? play : null;

        public bool IsMouseCardPlay(object play) => _mouseCardPlayType!.IsInstanceOfType(play);

        public Control? PlayHolder(object play) => _playHolder!.GetValue(play) as Control;

        public void PressHandHolder(Control hand, Control holder) => _pressHandHolder!.Invoke(hand, [holder]);

        public void SetDragStart(Control hand, float y)
        {
            object? play = _currentCardPlay?.GetValue(hand);
            if (play != null && _dragStartY != null) _dragStartY.SetValue(play, y);
        }

        public void SetTargetPosition(Control holder, Vector2 position) => _setTargetPosition?.Invoke(holder, [position]);

        public void SetTargetScale(Control holder, Vector2 scale) => _setTargetScale?.Invoke(holder, [scale]);

        public void CancelAllCardPlay(Control hand) => _cancelAll!.Invoke(hand, null);

        public IList? ActiveHolders(Control hand) => _activeHolders!.GetValue(hand) as IList;

        public Control? FocusedHolder(Control hand) => _focusedHolder?.GetValue(hand) as Control;

        public Control? CardVisual(Control holder) => _holderCardVisual?.GetValue(holder) as Control;

        public object? HolderCardModel(Control holder) => _holderCardModel!.GetValue(holder);

        public string TargetTypeName(Control holder)
        {
            object? model = HolderCardModel(holder);
            if (model == null || _targetType == null) return "";
            return _targetType.GetValue(model)?.ToString() ?? "";
        }

        public bool IsSingleTarget(Control holder) => TargetTypeName(holder) is "AnyEnemy" or "AnyAlly";

        public bool IsTargeting()
        {
            try
            {
                object? manager = TargetManager();
                return manager != null && (bool)(_isInSelection!.GetValue(manager) ?? false);
            }
            catch (TargetInvocationException)
            {
                return false;
            }
        }

        public object? TargetManager() => _targetManagerInstance?.GetValue(null);

        public IEnumerable Creatures()
        {
            object? room = _roomInstance?.GetValue(null);
            if (room == null) return Array.Empty<object>();
            return _roomCreatureNodes?.GetValue(room) as IEnumerable ?? Array.Empty<object>();
        }

        public bool IsCreature(Control node)
        {
            foreach (object? creature in Creatures())
                if (ReferenceEquals(creature, node)) return true;
            return false;
        }

        public bool AllowedToTarget(Node node)
        {
            object? manager = TargetManager();
            if (manager == null || _allowedToTarget == null) return false;
            try
            {
                return (bool)(_allowedToTarget.Invoke(manager, [node]) ?? false);
            }
            catch (TargetInvocationException)
            {
                return false;
            }
        }

        public Node? HoveredNode()
        {
            object? manager = TargetManager();
            return manager == null ? null : _hoveredNode?.GetValue(manager) as Node;
        }

        public void Hover(Node node)
        {
            if (TargetManager() is { } manager) _onNodeHovered?.Invoke(manager, [node]);
        }

        public void Unhover(Node node)
        {
            if (TargetManager() is { } manager) _onNodeUnhovered?.Invoke(manager, [node]);
        }

        public bool FinishTargeting(bool cancel)
        {
            if (TargetManager() is not { } manager || _finishTargeting == null) return false;
            try
            {
                _finishTargeting.Invoke(manager, [cancel]);
                return true;
            }
            catch (Exception error)
            {
                GD.PrintErr("Phone touch cards: the game did not finish targeting: ", error.Message);
                return false;
            }
        }

        public bool CancelTargeting()
        {
            if (TargetManager() is not { } manager || _cancelTargeting == null) return false;
            try
            {
                _cancelTargeting.Invoke(manager, null);
                return true;
            }
            catch (Exception error)
            {
                GD.PrintErr("Phone touch cards: the game did not cancel targeting: ", error.Message);
                return false;
            }
        }

        /// <summary>Screens drawn over the hand: the overlay stack's, plus one for an open capstone screen.</summary>
        public int OverlayCount()
        {
            object? stack = _overlayInstance?.GetValue(null);
            int count = stack == null ? 0 : (int)(_overlayCount?.GetValue(stack) ?? 0);
            object? capstone = _capstoneInstance?.GetValue(null);
            if (capstone is GodotObject node && GodotObject.IsInstanceValid(node) && _capstoneInUse?.GetValue(capstone) is true)
                count++;
            return count;
        }

        public IList NewCardList() => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(_cardModelType!))!;

        public bool OpenInspect(IList cards, int index)
        {
            object? game = _gameInstance!.GetValue(null);
            if (game == null) return false;
            object? screen = _getInspectScreen!.Invoke(game, null);
            MethodInfo? open = screen?.GetType().GetMethod("Open", InstAll);
            if (screen == null || open == null) return false;
            open.Invoke(screen, [cards, index, false]);
            return true;
        }

        public Control? InspectScreen()
        {
            object? game = _gameInstance?.GetValue(null);
            if (game == null || _getInspectScreen == null) return null;
            return _getInspectScreen.Invoke(game, null) as Control;
        }
    }
}
