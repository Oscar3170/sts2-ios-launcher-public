using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Scrolls a portrait list in place: the container that holds the list is clipped and its content is moved under the finger.
/// Nothing is reparented, so the game's initialized buttons keep their hotkey and manager registrations (their <c>_ExitTree</c>
/// handlers never run). The drag is caught before the GUI like <see cref="PhoneDragScroll"/>: once it has moved past the drag
/// threshold the press under the finger is let go, so a swipe scrolls instead of choosing the row it started on; a tap within
/// the slop is left entirely to the game. In landscape nothing is registered and the content's own position comes back.
/// </summary>
internal sealed class PortraitInPlaceScroll
{
    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const string TipSetTypeName = "MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet";
    private const float WheelStep = 160f;

    private sealed class Region
    {
        public ulong Clip;
        public ulong Content;
        public ulong Layout;
        public float BaseTop;
        public float Offset;
    }

    private readonly List<Region> _regions = new(2);
    private readonly FieldInfo? _tipBlocked;
    private readonly MethodInfo? _clearTips;
    private Window? _window;
    private bool _down;
    private bool _dragging;
    private Region? _active;
    private float _startY;
    private float _startOffset;

    public static PortraitInPlaceScroll Shared { get; } = new();

    public PortraitInPlaceScroll()
    {
        try
        {
            Assembly game = typeof(NGame).Assembly;
            Type? tipSet = game.GetType(TipSetTypeName);
            _tipBlocked = tipSet?.GetField("shouldBlockHoverTips", AnyStatic);
            _clearTips = tipSet?.GetMethod("Clear", AnyStatic, null, Type.EmptyTypes, null);
        }
        catch (Exception error)
        {
            GD.PrintErr("Portrait in-place scroll: off: ", error.Message);
        }
    }

    /// <summary>
    /// Registers one clipped list: <paramref name="clip"/> draws and clips, <paramref name="content"/> moves. The content must
    /// be a direct child of the clip and the clip must not lay its children out (or its own sort would undo the offset).
    /// </summary>
    public void Set(Control clip, Control content, Control layout)
    {
        if (!Valid(clip) || !Valid(content) || content.GetParent() != clip || clip.IsClass("Container")) return;
        Prune();
        Region? region = _regions.Find(entry => entry.Clip == clip.GetInstanceId());
        if (region == null || region.Content != content.GetInstanceId())
        {
            region = new Region
            {
                Clip = clip.GetInstanceId(),
                Content = content.GetInstanceId(),
                BaseTop = content.Position.Y,
            };
            _regions.RemoveAll(entry => entry.Clip == region.Clip);
            _regions.Add(region);
        }
        region.Layout = layout.GetInstanceId();
        Hold(region);
        Attach(clip.GetWindow());
    }

    public void Clear()
    {
        _regions.Clear();
        _active = null;
        Detach();
    }

    private void Attach(Window? window)
    {
        if (window == null || _window == window) return;
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

    private static bool Valid(Node? node) => node != null && GodotObject.IsInstanceValid(node) && node.IsInsideTree();

    private void Prune()
    {
        _regions.RemoveAll(entry =>
            GodotObject.InstanceFromId(entry.Clip) is not Control clip || !Valid(clip) ||
            GodotObject.InstanceFromId(entry.Content) is not Control content || !Valid(content));
    }

    /// <summary>Puts the content at its resting place plus the scroll offset, and clamps the offset to the list's extent.</summary>
    private void Hold(Region region)
    {
        if (GodotObject.InstanceFromId(region.Clip) is not Control clip || !Valid(clip) ||
            GodotObject.InstanceFromId(region.Content) is not Control content || !Valid(content)) return;
        float limit = Limit(clip, content);
        region.Offset = Math.Clamp(region.Offset, -limit, 0f);
        float top = region.BaseTop + region.Offset;
        if (MathF.Abs(content.Position.Y - top) > 0.01f)
            content.Position = new Vector2(content.Position.X, top);
    }

    private static float Limit(Control clip, Control content) =>
        MathF.Max(0f, content.GetCombinedMinimumSize().Y - clip.Size.Y);

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
                    if (TargetAt(ToCanvas(wheel.Position)) is { } region)
                    {
                        region.Offset += wheel.ButtonIndex == MouseButton.WheelUp ? WheelStep : -WheelStep;
                        Hold(region);
                        _window.SetInputAsHandled();
                    }
                    return;
            }
        }
        catch (Exception error)
        {
            GD.PrintErr("Portrait in-place scroll: ", error.Message);
            End();
        }
    }

    private Vector2 ToCanvas(Vector2 point) => _window == null ? point : _window.GetFinalTransform().AffineInverse() * point;

    private void Begin(Vector2 point)
    {
        End();
        _active = TargetAt(point);
        if (_active == null) return;
        _down = true;
        _startY = point.Y;
        _startOffset = _active.Offset;
    }

    private void Move(Vector2 point)
    {
        if (!_down || _active == null) return;
        if (GodotObject.InstanceFromId(_active.Clip) is not Control clip || !Valid(clip) ||
            GodotObject.InstanceFromId(_active.Content) is not Control content || !Valid(content) ||
            !clip.IsVisibleInTree())
        {
            End();
            return;
        }
        float distance = _startY - point.Y;
        if (!_dragging)
        {
            if (!SwipeMath.Vertical(distance, PortraitEventMath.DragThreshold)) return;
            _dragging = true;
            ClearTips();
            PhoneSwipeRelease.Shared.Release(PressedLayout());
        }
        _active.Offset = _startOffset - distance;
        Hold(_active);
        _window?.SetInputAsHandled();
    }

    private void End()
    {
        if (_dragging) PhoneSwipeRelease.Shared.Release(PressedLayout());
        _down = false;
        _dragging = false;
        _active = null;
    }

    private Region? TargetAt(Vector2 point)
    {
        foreach (Region region in _regions)
        {
            if (GodotObject.InstanceFromId(region.Clip) is not Control clip || !Valid(clip) || !clip.IsVisibleInTree() ||
                clip.Modulate.A < 0.1f) continue;
            if (clip.GetGlobalRect().HasPoint(point)) return region;
        }
        return null;
    }

    private void ClearTips()
    {
        if (_tipBlocked?.GetValue(null) is true) return;
        _clearTips?.Invoke(null, null);
    }

    private Node? PressedLayout()
    {
        if (_active == null || _active.Layout == 0) return null;
        return GodotObject.InstanceFromId(_active.Layout) is Node layout && GodotObject.IsInstanceValid(layout) ? layout : null;
    }
}
