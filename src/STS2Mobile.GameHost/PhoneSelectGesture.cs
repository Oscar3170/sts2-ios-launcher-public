using System;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// The tap, hold and swipe-cancel policy shared by every item that waits for a second tap (see <see cref="PhoneSelectConfirm"/>):
/// the press in flight, its point on the portrait canvas, the hold timer, the slop that turns a press into a scroll, and the
/// selection a press made and may have to put back. The selection itself, its reticle and its tips belong to
/// <see cref="PhoneSelectConfirm"/>; what inspecting and committing an item mean belongs to its
/// <see cref="PhoneSelectConfirm.SelectAdapter"/>; the game's own record of the press is let go by the adapters.
/// <para>A press on an item selects it at once; the release of a second tap on the selected item confirms it, while a hold
/// (still for <see cref="SelectConfirmMath.HoldSeconds"/>) opens the item's native inspection and moving past
/// <see cref="SelectConfirmMath.ConfirmSlop"/> cancels the confirmation, so a swipe never buys or takes. The release is read
/// from the window's own touch and mouse events, not the game's release signals, which need a hover that touch does not always
/// set on the simulator. The gesture only ever acts on the press it is tracking; it does not route other owners' input.</para>
/// </summary>
internal sealed class PhoneSelectGesture
{
    private readonly PhoneSelectConfirm _owner;

    // The press being tracked: where it began, on what, and how far it has wandered since.
    private bool _pressed;
    private bool _pressOnSelected;
    private bool _holdDone;
    private Control? _pressScreen;
    private Control? _pressItem;
    private PhoneSelectConfirm.SelectAdapter? _pressAdapter;
    private Vector2 _pressCanvas;
    private Vector2 _lastCanvas;
    private Vector2 _windowPressCanvas;
    private float _maxMove;
    private ulong _pressMsec;
    private int _pressTouch = -1;
    private bool _pressSeen;
    private bool _itemPressed;

    // A press that makes a selection owns it only while it stays a tap: a swipe lets it go again and puts back what stood
    // there before (a second tap on the selected item only ever confirms, so it owns nothing).
    private bool _pressMadeSelection;
    private bool _pressSwiped;
    private PhoneSelectConfirm.Selection? _pressPrevious;

    private static int _confirms;
    private static int _holdInspects;

    internal PhoneSelectGesture(PhoneSelectConfirm owner) => _owner = owner;

    /// <summary>How many items the second tap has confirmed (for the tests and the proofs).</summary>
    internal static int Confirms => _confirms;

    /// <summary>How many holds have opened an item's inspection (for the tests and the proofs).</summary>
    internal static int HoldInspects => _holdInspects;

    /// <summary>Every press and release of the window's own input, before the game's controls see it.</summary>
    internal void OnWindowInput(InputEvent input)
    {
        if (_owner.Failed) return;
        try
        {
            HandleWindowInput(input);
        }
        catch (Exception error)
        {
            _owner.Fail(error);
        }
    }

    private void HandleWindowInput(InputEvent input)
    {
        switch (input)
        {
            case InputEventScreenTouch { Pressed: true } touch:
                _pressSeen = true;
                _pressTouch = touch.Index;
                _windowPressCanvas = ToCanvas(touch.Position);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } press:
                _pressSeen = true;
                _pressTouch = -1;
                _windowPressCanvas = ToCanvas(press.Position);
                break;
            case InputEventScreenTouch { Pressed: false } release when _pressed && (_pressTouch < 0 || release.Index == _pressTouch):
                Release(release.Position);
                break;
            case InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left } release when _pressed:
                // A touch's release is also delivered as this; the first release has already ended the press.
                Release(release.Position);
                break;
            case InputEventScreenDrag drag when _pressed:
                Moved(drag.Position);
                break;
            case InputEventMouseMotion motion when _pressed && (motion.ButtonMask & MouseButtonMask.Left) != 0:
                Moved(motion.Position);
                break;
        }
    }

    /// <summary>
    /// The game's press signal on one of the screen's items, before the item takes it. Only a left press on a screen that
    /// still waits for two taps starts the tracking.
    /// </summary>
    internal void ItemPressed(Control screen, Control item, PhoneSelectConfirm.SelectAdapter adapter, InputEvent e)
    {
        if (_owner.Failed || !SafeAreaCanvas.IsPortrait) return;
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press) return;
        if (!GodotObject.IsInstanceValid(screen) || !GodotObject.IsInstanceValid(item)) return;
        if (!adapter.Confirms(screen)) return;
        Begin(screen, item, adapter, press.Position);
    }

    /// <summary>
    /// Starts tracking a press. <paramref name="guiPosition" /> is the press point the item's own signal carries (the canvas's
    /// coordinates); the window's own press point is used instead when there was one, so that it is measured in the same space
    /// as the window's drag and release points.
    /// </summary>
    internal void Begin(Control screen, Control item, PhoneSelectConfirm.SelectAdapter adapter, Vector2 guiPosition)
    {
        try
        {
            _itemPressed = true;
            // The game takes these items on the release of a press; never here.
            adapter.Suppress(screen, item);
            bool second = _owner.IsSelected(screen, item, adapter);
            PhoneSelectConfirm.Selection? previous = _owner.CurrentSelection;
            if (second)
            {
                if (adapter.IsCard) _owner.HoverOnly(item);
                GD.Print($"Phone select confirm: {adapter.Label} {item.Name} tapped again; releasing confirms.");
            }
            else
            {
                _owner.Drop("other item", counted: true);
                _owner.Select(screen, item, adapter);
            }
            _pressed = true;
            _pressOnSelected = second;
            _pressMadeSelection = !second;
            _pressSwiped = false;
            _pressPrevious = previous;
            _holdDone = false;
            _pressScreen = screen;
            _pressItem = item;
            _pressAdapter = adapter;
            _pressCanvas = _lastCanvas = _pressSeen ? _windowPressCanvas : guiPosition;
            _maxMove = 0f;
            _pressMsec = Time.GetTicksMsec();
        }
        catch (Exception error)
        {
            _owner.Fail(error);
        }
    }

    /// <summary>Drops the selection a press made when the press landed on no item; runs before the frame's other work.</summary>
    internal void BeginFrame()
    {
        // A press that lands on no item lets the selection go.
        if (_pressSeen && !_itemPressed) _owner.Drop("elsewhere", counted: true);
        _pressSeen = _itemPressed = false;
    }

    /// <summary>Opens the item's native inspection once a still press has lasted the hold; runs once per frame.</summary>
    internal void Step()
    {
        if (!_pressed || _holdDone || _pressItem == null || _pressAdapter == null) return;
        double elapsed = SelectConfirmMath.SecondsSince(_pressMsec, Time.GetTicksMsec());
        if (elapsed < SelectConfirmMath.HoldSeconds) return;
        SelectPress action = SelectConfirmMath.Decide(_maxMove, elapsed, released: false);
        if (action != SelectPress.Inspect)
        {
            if (action == SelectPress.Cancel) _holdDone = true;
            return;
        }
        _holdDone = true;
        if (_owner.CurrentSelection is not { } selected || !ReferenceEquals(selected.Item, _pressItem)) return;
        if (_pressScreen == null) return;
        try
        {
            _pressAdapter.Inspect(_pressScreen, _pressItem);
            _holdInspects++;
            GD.Print($"Phone select confirm: {_pressAdapter.Label} {_pressItem.Name} held; its inspection opened.");
        }
        catch (Exception error)
        {
            // A tip or an inspection that cannot open leaves the item under the two taps as it was.
            GD.PrintErr("Phone select confirm: the item's inspection could not open: ", error.Message);
        }
    }

    /// <summary>Forgets the press when Portrait Mode is turned off over it.</summary>
    internal void Reset()
    {
        _pressed = false;
        _pressOnSelected = false;
        _pressMadeSelection = false;
        _pressSwiped = false;
        _pressPrevious = null;
        _holdDone = false;
        _pressScreen = null;
        _pressItem = null;
        _pressAdapter = null;
        _maxMove = 0f;
    }

    private void Moved(Vector2 windowPosition)
    {
        _lastCanvas = ToCanvas(windowPosition);
        _maxMove = Math.Max(_maxMove, _pressCanvas.DistanceTo(_lastCanvas));
        CancelSwipedSelection();
    }

    /// <summary>
    /// A press that has become a swipe never made a selection: it lets go of the one the press made and puts back the one
    /// that stood before, so scrolling a card grid or a shop page never moves the reticle, and the next tap selects afresh.
    /// A second tap on the selected item owns nothing, so a swipe from it only cancels its confirmation (the selection stays).
    /// </summary>
    private void CancelSwipedSelection()
    {
        if (!_pressed || _pressSwiped || !_pressMadeSelection || SelectConfirmMath.WithinSlop(_maxMove)) return;
        _pressSwiped = true;
        _holdDone = true;
        // The press's own release is still on its way: keep the item's release suppression until it has gone (a shop slot's
        // ignore flag is cleared by its next press), or the swipe itself would buy.
        _owner.Drop("swipe", counted: true, restore: false);
        PhoneSelectConfirm.Selection? previous = _pressPrevious;
        _pressPrevious = null;
        if (previous != null) _owner.RestoreSelection(previous);
    }

    private void Release(Vector2 windowPosition)
    {
        Control? item = _pressItem;
        Control? screen = _pressScreen;
        PhoneSelectConfirm.SelectAdapter? adapter = _pressAdapter;
        bool second = _pressOnSelected;
        bool held = _holdDone;
        double elapsed = SelectConfirmMath.SecondsSince(_pressMsec, Time.GetTicksMsec());
        Moved(windowPosition);
        float moved = _maxMove;
        Reset();
        if (!second || held || item == null || screen == null || adapter == null) return;
        if (SelectConfirmMath.Decide(moved, elapsed, released: true) != SelectPress.Confirm) return;
        if (_owner.CurrentSelection is not { } selected || !ReferenceEquals(selected.Item, item)) return;
        if (!_owner.StillSelected(selected, out string changed))
        {
            _owner.Drop($"item changed ({changed})", counted: true);
            return;
        }
        if (!adapter.HitRect(screen, item).HasPoint(_lastCanvas)) return;
        if (!adapter.CanConfirm(screen, item)) return;
        Confirm(screen, item, adapter);
    }

    private void Confirm(Control screen, Control item, PhoneSelectConfirm.SelectAdapter adapter)
    {
        // The game's own release of this press is still on its way: the item keeps suppressing it until the next press.
        _owner.Drop("confirmed", counted: false, restore: false);
        _confirms++;
        GD.Print($"Phone select confirm: {adapter.Label} {item.Name} confirmed.");
        adapter.Commit(screen, item);
    }

    /// <summary>The touch's point on the portrait canvas (the finger is the authority, not the engine's emulated mouse).</summary>
    private static Vector2 ToCanvas(Vector2 windowPosition)
    {
        if (Engine.GetMainLoop() is not SceneTree tree || !GodotObject.IsInstanceValid(tree.Root)) return windowPosition;
        return tree.Root.GetFinalTransform().AffineInverse() * windowPosition;
    }
}
