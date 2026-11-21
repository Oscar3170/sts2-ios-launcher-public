using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Horizontal and vertical swipes on the run header's own scroll rows (the potion row and the relic band). The game's
/// buttons are pressed on touch down and act on the release, and the rows' contents are those buttons, so a drag that starts
/// on a potion or a relic is caught here before the GUI sees it: once it has moved <see cref="StartDistance"/> the press under
/// the finger is let go (a swipe then never opens a potion's popup, drinks it, or inspects a relic), its hover tip is taken
/// down, and the row follows the finger. A tap inside the slop is left entirely to the game, so tapping a potion or a relic
/// still does what the game does. Window input runs before the GUI dispatch, which is what lets the press be cleared in time.
/// In landscape nothing is registered and nothing changes.
/// </summary>
internal sealed class PhoneHeaderSwipe
{
    private const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string ClickableTypeName = "MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl";
    private const string TipSetTypeName = "MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet";

    /// <summary>How far a touch moves, in canvas units, before it is a swipe: 28 units are about 10 points.</summary>
    public const float StartDistance = 28f;

    /// <summary>How far one wheel notch scrolls a row.</summary>
    private const float WheelStep = 160f;

    private readonly Type? _clickableType;
    private readonly FieldInfo? _clickablePressed;
    private readonly Type? _tipSetType;
    private readonly FieldInfo? _tipBlocked;
    private readonly MethodInfo? _clearTips;
    private readonly FieldInfo? _clickableEnabled;
    private readonly MethodInfo? _pressHandler;
    private readonly MethodInfo? _releaseHandler;

    private SceneTree? _tree;
    private ulong _potionRow;
    private ulong _relicRow;

    private bool _down;
    private bool _swiping;
    private bool _horizontal;
    private ulong _row;
    private ulong _popupButton;
    private Vector2 _start;
    private int _initial;

    /// <summary>
    /// When the header last scrolled, in engine milliseconds. A tip the game creates under the moving pointer (touch is
    /// emulated as a mouse, so the pointer hovers every relic it passes) is not a hover the player asked for; the tip fitter
    /// takes those down for <see cref="TipSuppressMilliseconds"/> after the gesture.
    /// </summary>
    public static ulong LastSwipeMsec { get; private set; }

    /// <summary>
    /// How long after a swipe a relic or potion tip is still treated as the finger's. Long enough to take down the tip the
    /// last drag's emulated hover creates (a frame or two), short enough that a stationary hold a moment later keeps its
    /// native tip.
    /// </summary>
    public const ulong TipSuppressMilliseconds = 120;

    /// <summary>Whether a header swipe happened within <see cref="TipSuppressMilliseconds"/>.</summary>
    public static bool RecentlySwiped
    {
        get
        {
            ulong now = Time.GetTicksMsec();
            return LastSwipeMsec != 0 && now >= LastSwipeMsec && now - LastSwipeMsec < TipSuppressMilliseconds;
        }
    }

    public PhoneHeaderSwipe()
    {
        try
        {
            Assembly game = typeof(NGame).Assembly;
            _clickableType = game.GetType(ClickableTypeName);
            _clickablePressed = _clickableType?.GetField("_isPressed", InstAll);
            _tipSetType = game.GetType(TipSetTypeName);
            _tipBlocked = _tipSetType?.GetField("shouldBlockHoverTips", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            _clearTips = _tipSetType?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            _clickableEnabled = _clickableType?.GetField("_isEnabled", InstAll);
            _pressHandler = _clickableType?.GetMethod("OnPressHandler", InstAll, null, Type.EmptyTypes, null);
            _releaseHandler = _clickableType?.GetMethod("OnReleaseHandler", InstAll, null, Type.EmptyTypes, null);
        }
        catch (Exception error)
        {
            GD.PrintErr("Phone header swipe: off: ", error.Message);
        }
    }

    /// <summary>The rows a swipe may scroll; either may be null (a screen with no potions, say).</summary>
    public void SetRows(Control? potionRow, Control? relicRow)
    {
        _potionRow = Valid(potionRow) ? potionRow!.GetInstanceId() : 0;
        _relicRow = Valid(relicRow) ? relicRow!.GetInstanceId() : 0;
        if (_row != 0 && _row != _potionRow && _row != _relicRow) End();
    }

    public void Clear()
    {
        _popupButton = 0;
        End();
    }

    public void Attach(SceneTree tree)
    {
        _tree = tree;
        tree.Root.WindowInput += OnWindowInput;
    }

    public void Detach()
    {
        if (_tree != null && GodotObject.IsInstanceValid(_tree.Root))
            _tree.Root.WindowInput -= OnWindowInput;
        _tree = null;
        _popupButton = 0;
        End();
    }

    private static bool Valid(Node? node) => node != null && GodotObject.IsInstanceValid(node) && node.IsInsideTree();

    private void OnWindowInput(InputEvent input)
    {
        if (_tree == null || !GodotObject.IsInstanceValid(_tree.Root)) return;
        if (!SafeAreaCanvas.IsPortrait)
        {
            End();
            return;
        }
        try
        {
            switch (input)
            {
                case InputEventScreenTouch touch when touch.Index == 0:
                    if (touch.Pressed)
                    {
                        PressPotionAction(touch.Position);
                        Begin(touch.Position);
                    }
                    else
                    {
                        ReleasePotionAction();
                        End();
                    }
                    return;
                case InputEventScreenDrag drag when drag.Index == 0:
                    Move(drag.Position);
                    return;
                // The Linux host and the desktop editor send a mouse; the phone sends touches. Both describe one gesture.
                case InputEventMouseButton button when button.ButtonIndex == MouseButton.Left:
                    if (button.Pressed)
                    {
                        PressPotionAction(button.Position);
                        Begin(button.Position);
                    }
                    else
                    {
                        ReleasePotionAction();
                        End();
                    }
                    return;
                case InputEventMouseMotion motion:
                    Move(motion.Position);
                    return;
                case InputEventMouseButton wheel when wheel.Pressed &&
                    wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown:
                    if (RowAt(ToCanvas(wheel.Position)) is { } row)
                    {
                        Nudge(row, wheel.ButtonIndex == MouseButton.WheelUp ? -WheelStep : WheelStep);
                        row.GetViewport().SetInputAsHandled();
                    }
                    return;
                default:
                    return;
            }
        }
        catch (Exception error)
        {
            GD.PrintErr("Phone header swipe: ", error.Message);
            End();
        }
    }

    private Vector2 ToCanvas(Vector2 point) => _tree == null ? point : _tree.Root.GetFinalTransform().AffineInverse() * point;

    private void Begin(Vector2 point)
    {
        End();
        Vector2 canvas = ToCanvas(point);
        Control? row = RowAt(canvas);
        if (row == null) return;
        // A finger put on the row again is a new gesture: a tip it asks for (a stationary hold) is the player's, not the
        // previous swipe's.
        LastSwipeMsec = 0;
        _down = true;
        _row = row.GetInstanceId();
        _horizontal = Horizontal(row);
        _start = canvas;
        _initial = row is ScrollContainer clip ? (_horizontal ? clip.ScrollHorizontal : clip.ScrollVertical) : 0;
    }

    private void Move(Vector2 point)
    {
        if (_down && _row != 0)
        {
            if (GodotObject.InstanceFromId(_row) is not Control row || !Valid(row) || !row.IsVisibleInTree())
            {
                End();
                return;
            }
            Vector2 moved = ToCanvas(point) - _start;
            if (!_swiping)
            {
                float along = Horizontal(row) ? moved.X : moved.Y;
                if (MathF.Abs(along) < StartDistance || MathF.Abs(along) < MathF.Abs(Horizontal(row) ? moved.Y : moved.X) * 1.2f)
                    return;
                _swiping = true;
                ReleasePresses(row);
            }
            // Every frame, not only the first: the game creates a hover tip for whatever relic or potion the emulated pointer
            // moves onto after the gesture has started.
            LastSwipeMsec = Time.GetTicksMsec();
            ClearTips();
            Scroll(row, Horizontal(row) ? -moved.X : -moved.Y);
        }
    }

    /// <summary>
    /// Forwards a press on an open potion popup's Use or Discard to that button. The popup is top level (drawn below the
    /// relic band) and Godot does not offer top-level controls to the GUI, so without this its buttons are inert.
    /// </summary>
    private void PressPotionAction(Vector2 point)
    {
        Control? button = PortraitOverlayLayout.PotionActionAt(ToCanvas(point));
        if (button == null || _pressHandler == null || _clickableEnabled?.GetValue(button) is false) return;
        // Touch and its emulated mouse describe one press.
        if (_popupButton == button.GetInstanceId()) return;
        Control target = button;
        _popupButton = target.GetInstanceId();
        // Deferred: the press belongs after this event has finished its GUI/targeting pass, as the native control's would.
        try
        {
            Callable.From(() =>
            {
                if (!GodotObject.IsInstanceValid(target) || !target.IsInsideTree()) return;
                try { _pressHandler!.Invoke(target, null); }
                catch (Exception error) { GD.PrintErr("Phone header swipe: potion action press: ", error.Message); }
            }).CallDeferred();
        }
        catch (Exception error)
        {
            GD.PrintErr("Phone header swipe: potion action press: ", error.Message);
        }
    }

    /// <summary>The release goes to the button the press landed on, as a mouse's would.</summary>
    private void ReleasePotionAction()
    {
        if (_popupButton == 0) return;
        ulong id = _popupButton;
        _popupButton = 0;
        if (GodotObject.InstanceFromId(id) is not Control button || !GodotObject.IsInstanceValid(button) || !button.IsInsideTree())
            return;
        Control target = button;
        // Deferred: a targeted potion starts its native targeting on the release; letting the release event finish first
        // keeps the target manager from reading that same event as a click on empty space and cancelling it.
        try
        {
            Callable.From(() =>
            {
                if (!GodotObject.IsInstanceValid(target) || !target.IsInsideTree()) return;
                try { _releaseHandler?.Invoke(target, null); }
                catch (Exception error) { GD.PrintErr("Phone header swipe: potion action release: ", error.Message); }
            }).CallDeferred();
        }
        catch (Exception error)
        {
            GD.PrintErr("Phone header swipe: potion action release: ", error.Message);
        }
    }

    private void End()
    {
        if (_swiping && _row != 0 && GodotObject.InstanceFromId(_row) is Control row && Valid(row))
        {
            ReleasePresses(row);
            LastSwipeMsec = Time.GetTicksMsec();
        }
        _down = false;
        _swiping = false;
        _row = 0;
    }

    /// <summary>The registered row whose rect holds the point, if any.</summary>
    private Control? RowAt(Vector2 point)
    {
        Control? row = RowAt(_potionRow, point);
        return row ?? RowAt(_relicRow, point);
    }

    private Control? RowAt(ulong id, Vector2 point)
    {
        if (id == 0 || GodotObject.InstanceFromId(id) is not Control row || !Valid(row) || !row.IsVisibleInTree() ||
            row.Modulate.A < 0.1f || row.MouseBehaviorRecursive == Control.MouseBehaviorRecursiveEnum.Disabled) return null;
        Vector2 local = row.GetGlobalTransformWithCanvas().AffineInverse() * point;
        return new Rect2(Vector2.Zero, row.Size).HasPoint(local) ? row : null;
    }

    /// <summary>The potion row scrolls sideways; the relic band scrolls up and down.</summary>
    private bool Horizontal(Control row) => row.GetInstanceId() == _potionRow;

    /// <summary>Sets the row's offset from the gesture's start, clamped to what it can show.</summary>
    private void Scroll(Control row, float delta) => SetOffset(row, _initial + delta);

    /// <summary>One wheel notch, from wherever the row stands now.</summary>
    private void Nudge(Control row, float amount) => SetOffset(row, Current(row) + amount);

    private void SetOffset(Control row, float value)
    {
        if (row is not ScrollContainer clip) return;
        bool horizontal = Horizontal(row);
        ScrollBar bar = horizontal ? clip.GetHScrollBar() : clip.GetVScrollBar();
        int offset = (int)MathF.Round(Math.Clamp(value, 0f, MathF.Max(0f, (float)(bar.MaxValue - bar.Page))));
        if (horizontal) clip.ScrollHorizontal = offset;
        else clip.ScrollVertical = offset;
    }

    private float Current(Control row) => row is not ScrollContainer clip ? 0f :
        Horizontal(row) ? clip.ScrollHorizontal : clip.ScrollVertical;

    /// <summary>A swipe is not a press: take down the tip that followed the finger onto the row.</summary>
    private void ClearTips()
    {
        if (_tipBlocked?.GetValue(null) is true) return;
        _clearTips?.Invoke(null, null);
    }

    /// <summary>Lets go of every press inside the row, so the potion or relic under the finger does nothing when it lifts.</summary>
    private void ReleasePresses(Node node)
    {
        if (_clickableType == null || _clickablePressed == null) return;
        var stack = new Stack<Node>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            Node current = stack.Pop();
            if (_clickableType.IsInstanceOfType(current) && _clickablePressed.GetValue(current) is true)
                _clickablePressed.SetValue(current, false);
            foreach (Node child in current.GetChildren()) stack.Push(child);
        }
    }
}
