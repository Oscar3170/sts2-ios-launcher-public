using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Gives the game's back and gold arrow buttons a 44 point touch area in portrait without touching their art: the native
/// button keeps its size, hover and slide animation, and an invisible <see cref="PhoneHitArea"/> pad reaches out around it
/// (in the control's own units, so a screen drawn at a scale still gets a full-size target). Applied from the node-added pass
/// and kept on the visible buttons by the per-frame pass; removed again in landscape.
/// </summary>
internal sealed class PortraitNavigationPads
{
    private const string BackTypeName = "MegaCrit.Sts2.Core.Nodes.CommonUi.NBackButton";
    private const string ArrowTypeName = "MegaCrit.Sts2.Core.Nodes.CommonUi.NGoldArrowButton";
    /// <summary>The pad a screen's own layout already put on the control (PortraitTargetPads), which knows its neighbours.</summary>
    private const string ScreenPadMeta = "phone_target_pad";
    /// <summary>The control a screen's planner owns, even where it deliberately keeps its own target under the minimum.</summary>
    private const string PlannedControlMeta = "phone_target_planned";
    private const string ShowPosField = "_showPos";
    private const string HidePosField = "_hidePos";
    private const string EnabledField = "_isEnabled";
    private const string MoveTweenField = "_moveTween";
    private const string PosOffsetField = "_posOffset";
    private const float HideOffsetX = -180f;
    private const float FooterBottomMargin = 8f;
    private const BindingFlags AnyField = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

    private static readonly bool InputLog = OS.GetEnvironment("STS2_PORTRAIT_BACK_LOG") == "1";

    private readonly HashSet<ulong> _tracked = new();
    private readonly HashSet<ulong> _loggedInput = new();
    private readonly Dictionary<ulong, Vector2> _paddedAt = new();
    private readonly List<ulong> _dead = new();
    private readonly Dictionary<ulong, (Vector2 Show, Vector2 Hide)> _originalPositions = new();
    private readonly Dictionary<ulong, (Vector2 Show, Vector2 Hide)> _placedTargets = new();
    private static Type? _backType;
    private static FieldInfo? _showPos, _hidePos, _enabled, _moveTween, _posOffset;

    public static bool Handles(string? typeName) => typeName == BackTypeName || typeName == ArrowTypeName;

    public void Track(Control control)
    {
        ulong id = control.GetInstanceId();
        if (!_tracked.Add(id)) return;
        if (!InputLog || !IsBack(control) || !_loggedInput.Add(id)) return;
        // Gated one-line trace (STS2_PORTRAIT_BACK_LOG=1): which Back received a press/release, where it is, and what the
        // engine says is under the pointer, so a lost tap can be attributed to the press or to the hit test.
        control.Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(input => NoteBackInput(control, input)));
    }

    private static void NoteBackInput(Control control, InputEvent input)
    {
        string kind = input switch
        {
            InputEventMouseButton mouse => $"mouse {(mouse.Pressed ? "press" : "release")} {mouse.ButtonIndex}",
            InputEventScreenTouch touch => $"touch {(touch.Pressed ? "press" : "release")} #{touch.Index}",
            _ => input.GetClass(),
        };
        if (kind.StartsWith("InputEvent", StringComparison.Ordinal)) return;
        EnsureBackFields(control.GetType());
        Control? hovered = control.GetViewport().GuiGetHoveredControl();
        GD.Print($"Portrait back input: {control.Name} {kind} at ({control.GlobalPosition.X:F1},{control.GlobalPosition.Y:F1}) " +
                 $"size=({control.Size.X:F1}x{control.Size.Y:F1}) enabled={_enabled?.GetValue(control)} " +
                 $"hovered={(hovered is null ? "none" : hovered.GetType().Name + ":" + hovered.Name)}");
    }

    public void OnProcessFrame()
    {
        if (!SafeAreaCanvas.IsPortrait || _tracked.Count == 0) return;
        _dead.Clear();
        foreach (ulong id in _tracked)
        {
            if (GodotObject.InstanceFromId(id) is not Control control || !GodotObject.IsInstanceValid(control))
            {
                _dead.Add(id);
                continue;
            }
            if (!control.IsVisibleInTree() || control.Size.X < 1f || control.Size.Y < 1f) continue;
            bool back = IsBack(control);
            if (back) PlaceBack(control);
            // A screen layout that owns this control decides its pad: it knows the neighbours and may cap it short on purpose.
            // Back is the exception: every back button must be a full target, so it is measured from its own art and padded
            // even where a planner claimed it (the gate found card-library and relic-collection Backs padded by nobody).
            if (!back && (control.HasMeta(PlannedControlMeta) ||
                          (PhoneHitArea.Of(control) is { } screenPad && screenPad.HasMeta(ScreenPadMeta))))
            {
                // Ours and added before the planner took the control: shrink its offsets back to the art's own box, but never
                // hide the shared area — the planner owns its visibility and would not re-show it for several frames.
                if (PhoneHitArea.Of(control) is { } stale && !stale.HasMeta(ScreenPadMeta))
                {
                    stale.OffsetLeft = 0f;
                    stale.OffsetTop = 0f;
                    stale.OffsetRight = 0f;
                    stale.OffsetBottom = 0f;
                }
                // Forget our own pad on this control: if the planner releases it, the next pass computes it again.
                _paddedAt.Remove(id);
                continue;
            }
            Vector2 scale = control.GetGlobalTransformWithCanvas().Scale.Abs();
            if (scale.X < 0.01f || scale.Y < 0.01f) continue;
            float padX = Mathf.Max(0f, (PortraitBands.MinTarget - control.Size.X * scale.X) * 0.5f) / scale.X;
            float padY = Mathf.Max(0f, (PortraitBands.MinTarget - control.Size.Y * scale.Y) * 0.5f) / scale.Y;
            Control? area = PhoneHitArea.Of(control);
            // Write only when something actually differs: a pad rebuilt every frame between press and release is a tap that
            // can be lost. A hidden area (a screen planner re-planning) still gets shown again here.
            bool stable = area != null
                && Math.Abs(area.OffsetLeft + padX) < 0.5f && Math.Abs(area.OffsetRight - padX) < 0.5f
                && Math.Abs(area.OffsetTop + padY) < 0.5f && Math.Abs(area.OffsetBottom - padY) < 0.5f;
            if (!stable && (padX > 0f || padY > 0f)) PhoneHitArea.Grow(control, padX, padY, padX, padY);
            _paddedAt[id] = control.Size;
        }
        foreach (ulong id in _dead)
        {
            _tracked.Remove(id);
            _paddedAt.Remove(id);
        }
    }

    public void RestoreLandscape()
    {
        foreach (ulong id in _tracked)
        {
            if (GodotObject.InstanceFromId(id) is not Control control || !GodotObject.IsInstanceValid(control)) continue;
            PhoneHitArea.Remove(control);
            if (_moveTween?.GetValue(control) is Tween tween && tween.IsValid()) tween.Kill();
            _moveTween?.SetValue(control, null);
            if (_originalPositions.TryGetValue(id, out (Vector2 Show, Vector2 Hide) original)) RestoreBack(control, original);
        }
        _tracked.Clear();
        _paddedAt.Clear();
        _originalPositions.Clear();
        _placedTargets.Clear();
    }

    /// <summary>Whether this is a back button (they are the only buttons that cache a slide position).</summary>
    public static bool IsBack(Control control) => control.GetType().FullName == BackTypeName;

    /// <summary>
    /// Places every Back in canvas space: drawn left edge at the canvas's left, bottom at the game's native footer gap
    /// below its parent screen (mapped through the parent's scale), never below 8 units from the canvas bottom. The cached
    /// show and hide positions are written in canvas coordinates, which is what the native slide (global_position) uses, so a
    /// screen scaled on a virtual canvas still lands at the same place. A slide already running is retargeted once; after that
    /// the settled position is corrected whenever the game moves it back.
    /// </summary>
    private void PlaceBack(Control control)
    {
        if (!EnsureBackFields(control.GetType()) || _showPos is null) return;
        Vector2 scale = control.GetGlobalTransformWithCanvas().Scale.Abs();
        if (scale.X < 0.01f || scale.Y < 0.01f) return;
        Rect2 canvas = control.GetViewport().GetVisibleRect();
        float sizeY = control.Size.Y * scale.Y;
        Vector2 offset = _posOffset?.GetValue(control) is Vector2 value ? value : new Vector2(40f, 354f);
        // The native footer gap below the screen's bottom edge (the button's own bottom before the screen edge).
        float gap = Math.Max(0f, offset.Y - control.Size.Y) * scale.Y;
        // The native back is anchored to ContentScaleSize.Y, the canvas bottom, not to its screen's own rect.
        float bottom = Math.Min(canvas.End.Y - FooterBottomMargin, canvas.End.Y - gap);
        float top = Math.Max(canvas.Position.Y, bottom - sizeY);
        var show = new Vector2(canvas.Position.X, top);
        var hide = show + new Vector2(HideOffsetX * scale.X, 0f);
        ulong id = control.GetInstanceId();
        if (!_originalPositions.ContainsKey(id))
        {
            var currentShow = (Vector2)_showPos.GetValue(control)!;
            var currentHide = _hidePos is null ? currentShow : (Vector2)_hidePos.GetValue(control)!;
            _originalPositions[id] = (currentShow, currentHide);
        }
        bool changed = !_placedTargets.TryGetValue(id, out (Vector2 Show, Vector2 Hide) placed)
            || placed.Show.DistanceTo(show) > 0.5f || placed.Hide.DistanceTo(hide) > 0.5f;
        bool enabled = _enabled?.GetValue(control) is true;
        Vector2 target = enabled ? show : hide;
        Tween? tween = _moveTween?.GetValue(control) as Tween;
        if (changed)
        {
            _placedTargets[id] = (show, hide);
            _showPos.SetValue(control, show);
            if (_hidePos is not null) _hidePos.SetValue(control, hide);
            if (tween is not null && tween.IsValid()) tween.Kill();
            _moveTween?.SetValue(control, null);
            if (control.GlobalPosition.DistanceTo(target) > 2f)
            {
                Tween slide = control.CreateTween();
                slide.TweenProperty(control, "global_position", target, 0.35)
                    .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
                _moveTween?.SetValue(control, slide);
            }
            else control.GlobalPosition = target;
            return;
        }
        // The game's own window handler rewrites the cached positions in its local space; restore ours only when it did.
        var storedShow = (Vector2)_showPos.GetValue(control)!;
        if (storedShow.DistanceTo(show) > 0.5f) _showPos.SetValue(control, show);
        if (_hidePos is not null && _hidePos.GetValue(control) is Vector2 storedHide && storedHide.DistanceTo(hide) > 0.5f)
            _hidePos.SetValue(control, hide);
        if (tween is not null && tween.IsValid() && tween.IsRunning()) return;
        if (control.GlobalPosition.DistanceTo(target) > 0.5f) control.GlobalPosition = target;
    }

    private static void RestoreBack(Control control, (Vector2 Show, Vector2 Hide) original)
    {
        if (!EnsureBackFields(control.GetType())) return;
        _showPos?.SetValue(control, original.Show);
        _hidePos?.SetValue(control, original.Hide);
    }

    private static bool EnsureBackFields(Type type)
    {
        if (_backType == type) return _showPos is not null;
        _backType = type;
        _showPos = type.GetField(ShowPosField, AnyField);
        _hidePos = type.GetField(HidePosField, AnyField);
        _enabled = type.GetField(EnabledField, AnyField);
        _moveTween = type.GetField(MoveTweenField, AnyField);
        _posOffset = type.GetField(PosOffsetField, AnyField);
        return _showPos is not null;
    }

    public void PruneDead()
    {
        _tracked.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in new List<ulong>(_paddedAt.Keys))
            if (!GodotObject.IsInstanceIdValid(id) || !_tracked.Contains(id)) _paddedAt.Remove(id);
        foreach (ulong id in new List<ulong>(_originalPositions.Keys))
            if (!GodotObject.IsInstanceIdValid(id) || !_tracked.Contains(id)) _originalPositions.Remove(id);
        foreach (ulong id in new List<ulong>(_placedTargets.Keys))
            if (!GodotObject.IsInstanceIdValid(id) || !_tracked.Contains(id)) _placedTargets.Remove(id);
        _loggedInput.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
    }
}
