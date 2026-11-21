using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Vertical drags on portrait scroll regions: the event's copy and its choice rows. The game presses one of its own buttons
/// on touch down and acts on the release, and a <see cref="ScrollContainer"/> never sees a drag that began on such a button,
/// so the drag is caught here before the GUI: once it has moved <see cref="PortraitEventMath.DragThreshold"/> the press under
/// the finger is let go (a swipe scrolls and never picks an option), and the region follows the finger. A tap inside the slop
/// is left entirely to the game, so tapping a choice still does what the game does. Window input runs before the GUI
/// dispatch, which is what lets the press be cleared in time. In landscape nothing is registered and nothing changes.
/// </summary>
internal sealed class PhoneDragScroll
{
    private const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string ClickableTypeName = "MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl";
    private const string TipSetTypeName = "MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet";

    /// <summary>How far one wheel notch scrolls a region; the desktop host's mouse scrolls the same regions.</summary>
    private const float WheelStep = 160f;

    private readonly Type? _clickableType;
    private readonly FieldInfo? _clickablePressed;
    private readonly FieldInfo? _tipBlocked;
    private readonly MethodInfo? _clearTips;
    private readonly List<ulong> _targets = new(4);

    private Window? _window;
    private ulong _layout;
    private bool _down;
    private bool _dragging;
    private ulong _active;
    private float _startY;
    private int _initial;

    public PhoneDragScroll()
    {
        try
        {
            Assembly game = typeof(NGame).Assembly;
            _clickableType = game.GetType(ClickableTypeName);
            _clickablePressed = _clickableType?.GetField("_isPressed", InstAll);
            Type? tipSet = game.GetType(TipSetTypeName);
            _tipBlocked = tipSet?.GetField("shouldBlockHoverTips", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            _clearTips = tipSet?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        }
        catch (Exception error)
        {
            GD.PrintErr("Phone drag scroll: off: ", error.Message);
        }
    }

    /// <summary>The regions a drag may scroll, and the layout whose pressed buttons a drag lets go of.</summary>
    public void SetTargets(Control? first, Control? second, Control? layout)
    {
        _targets.Clear();
        if (Valid(first)) _targets.Add(first!.GetInstanceId());
        if (Valid(second)) _targets.Add(second!.GetInstanceId());
        _layout = Valid(layout) ? layout!.GetInstanceId() : 0;
        if (_active != 0 && !_targets.Contains(_active)) End();
    }

    public void Attach(Window window)
    {
        if (_window == window) return;
        Detach();
        _window = window;
        window.WindowInput += OnWindowInput;
    }

    public void Detach()
    {
        if (_window != null && GodotObject.IsInstanceValid(_window)) _window.WindowInput -= OnWindowInput;
        _window = null;
        End();
    }

    public void Clear()
    {
        _targets.Clear();
        _layout = 0;
        End();
    }

    private static bool Valid(Node? node) => node != null && GodotObject.IsInstanceValid(node) && node.IsInsideTree();

    private void OnWindowInput(InputEvent input)
    {
        if (_window == null || !GodotObject.IsInstanceValid(_window) || !SafeAreaCanvas.IsPortrait)
        {
            End();
            return;
        }
        try
        {
            switch (input)
            {
                case InputEventScreenTouch touch when touch.Index == 0:
                    if (touch.Pressed) Begin(ToCanvas(touch.Position));
                    else End();
                    return;
                case InputEventScreenDrag drag when drag.Index == 0:
                    Move(ToCanvas(drag.Position));
                    return;
                // The Linux host and the desktop editor send a mouse; the phone sends touches. Both describe one gesture.
                case InputEventMouseButton button when button.ButtonIndex == MouseButton.Left:
                    if (button.Pressed) Begin(ToCanvas(button.Position));
                    else End();
                    return;
                case InputEventMouseMotion motion:
                    if (_down) Move(ToCanvas(motion.Position));
                    return;
                case InputEventMouseButton wheel when wheel.Pressed &&
                    wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown:
                    if (TargetAt(ToCanvas(wheel.Position)) is { } region && Scrolls(region))
                    {
                        Nudge(region, wheel.ButtonIndex == MouseButton.WheelUp ? -WheelStep : WheelStep);
                        _window.SetInputAsHandled();
                    }
                    return;
                default:
                    return;
            }
        }
        catch (Exception error)
        {
            GD.PrintErr("Phone drag scroll: ", error.Message);
            End();
        }
    }

    private Vector2 ToCanvas(Vector2 point) => _window == null ? point : _window.GetFinalTransform().AffineInverse() * point;

    private void Begin(Vector2 point)
    {
        End();
        ScrollContainer? region = TargetAt(point);
        if (region == null) return;
        _down = true;
        _active = region.GetInstanceId();
        _startY = point.Y;
        _initial = region.ScrollVertical;
        _dragging = false;
    }

    private void Move(Vector2 point)
    {
        if (!_down || _active == 0) return;
        if (GodotObject.InstanceFromId(_active) is not ScrollContainer region || !Valid(region) || !region.IsVisibleInTree())
        {
            End();
            return;
        }
        float distance = _startY - point.Y;
        if (!_dragging)
        {
            if (MathF.Abs(distance) < PortraitEventMath.DragThreshold) return;
            _dragging = true;
            ClearTips();
            ReleasePresses();
        }
        if (Scrolls(region))
        {
            var bar = region.GetVScrollBar();
            float limit = MathF.Max(0f, (float)(bar.MaxValue - bar.Page));
            region.ScrollVertical = (int)MathF.Round(Math.Clamp(_initial + distance, 0f, limit));
        }
        _window?.SetInputAsHandled();
    }

    private void End()
    {
        if (_dragging) ReleasePresses();
        _down = false;
        _dragging = false;
        _active = 0;
    }

    /// <summary>
    /// The registered region whose rect holds the point. A region with nothing to scroll still takes the gesture, so a drag
    /// over it lets go of the choice under the finger instead of picking it.
    /// </summary>
    private ScrollContainer? TargetAt(Vector2 point)
    {
        foreach (ulong id in _targets)
        {
            if (GodotObject.InstanceFromId(id) is not ScrollContainer region || !Valid(region) || !region.IsVisibleInTree() ||
                region.Modulate.A < 0.1f) continue;
            Rect2 rect = region.GetGlobalRect();
            if (!rect.HasPoint(point)) continue;
            return region;
        }
        return null;
    }

    private static bool Scrolls(ScrollContainer region)
    {
        var bar = region.GetVScrollBar();
        return bar.MaxValue > bar.Page + 0.5f;
    }

    private void Nudge(ScrollContainer region, float amount)
    {
        var bar = region.GetVScrollBar();
        float limit = MathF.Max(0f, (float)(bar.MaxValue - bar.Page));
        region.ScrollVertical = (int)MathF.Round(Math.Clamp(region.ScrollVertical + amount, 0f, limit));
    }

    /// <summary>A swipe is not a press: take down the tip that followed the finger onto a choice.</summary>
    private void ClearTips()
    {
        if (_tipBlocked?.GetValue(null) is true) return;
        _clearTips?.Invoke(null, null);
    }

    /// <summary>Lets go of every press in the layout, so the choice under the finger does nothing when it lifts.</summary>
    private void ReleasePresses()
    {
        if (_clickableType == null || _clickablePressed == null || _layout == 0) return;
        if (GodotObject.InstanceFromId(_layout) is not Node layout || !GodotObject.IsInstanceValid(layout)) return;
        var stack = new Stack<Node>();
        stack.Push(layout);
        while (stack.Count > 0)
        {
            Node current = stack.Pop();
            if (_clickableType.IsInstanceOfType(current) && _clickablePressed.GetValue(current) is true)
                _clickablePressed.SetValue(current, false);
            foreach (Node child in current.GetChildren()) stack.Push(child);
        }
    }
}
