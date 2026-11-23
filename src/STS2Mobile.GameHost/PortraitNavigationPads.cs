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
    private const BindingFlags AnyField = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

    private readonly HashSet<ulong> _tracked = new();
    private readonly Dictionary<ulong, Vector2> _paddedAt = new();
    private readonly List<ulong> _dead = new();
    private readonly Dictionary<ulong, (Vector2 Show, Vector2 Hide)> _originalPositions = new();
    private static Type? _backType;
    private static FieldInfo? _showPos, _hidePos, _enabled, _moveTween;

    public static bool Handles(string? typeName) => typeName == BackTypeName || typeName == ArrowTypeName;

    public void Track(Control control) => _tracked.Add(control.GetInstanceId());

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
            if (!back && _paddedAt.TryGetValue(id, out Vector2 seen) && seen == control.Size && PhoneHitArea.Of(control) != null) continue;
            Vector2 scale = control.GetGlobalTransformWithCanvas().Scale.Abs();
            if (scale.X < 0.01f || scale.Y < 0.01f) continue;
            float padX = Mathf.Max(0f, (PortraitBands.MinTarget - control.Size.X * scale.X) * 0.5f) / scale.X;
            float padY = Mathf.Max(0f, (PortraitBands.MinTarget - control.Size.Y * scale.Y) * 0.5f) / scale.Y;
            if (padX > 0f || padY > 0f) PhoneHitArea.Grow(control, padX, padY, padX, padY);
            // A Back is re-padded every frame: a screen planner that re-plans it may shrink or hide the area in between.
            if (!back) _paddedAt[id] = control.Size;
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
            if (_originalPositions.TryGetValue(id, out (Vector2 Show, Vector2 Hide) original)) RestoreBack(control, original);
        }
        _tracked.Clear();
        _paddedAt.Clear();
        _originalPositions.Clear();
    }

    /// <summary>Whether this is a back button (they are the only buttons that cache a slide position).</summary>
    public static bool IsBack(Control control) => control.GetType().FullName == BackTypeName;

    /// <summary>
    /// The native Back sits with 40 units of its art past the left edge (its cached show position is the offset). Move its
    /// cached show and hide positions to x = 0 so the slide in lands fully inside the canvas, and settle a button that already
    /// finished sliding; a slide still running is left to land and corrected the frame it stops.
    /// </summary>
    private void PlaceBack(Control control)
    {
        if (!EnsureBackFields(control.GetType()) || _showPos is null) return;
        var show = (Vector2)_showPos.GetValue(control)!;
        var hide = _hidePos is null ? show : (Vector2)_hidePos.GetValue(control)!;
        ulong id = control.GetInstanceId();
        if (!_originalPositions.ContainsKey(id)) _originalPositions[id] = (show, hide);
        if (show.X < -0.5f)
        {
            float shift = -show.X;
            var shifted = new Vector2(0f, show.Y);
            var shiftedHide = hide + new Vector2(shift, 0f);
            _showPos.SetValue(control, shifted);
            if (_hidePos is not null) _hidePos.SetValue(control, shiftedHide);
            show = shifted;
            hide = shiftedHide;
        }
        if (_moveTween?.GetValue(control) is Tween tween && tween.IsValid() && tween.IsRunning()) return;
        bool enabled = _enabled?.GetValue(control) is true;
        Vector2 target = enabled ? (Vector2)_showPos.GetValue(control)! : (_hidePos is null ? show : (Vector2)_hidePos.GetValue(control)!);
        if (control.GlobalPosition.DistanceTo(target) > 0.6f) control.GlobalPosition = target;
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
        return _showPos is not null;
    }

    public void PruneDead()
    {
        _tracked.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in new List<ulong>(_paddedAt.Keys))
            if (!GodotObject.IsInstanceIdValid(id) || !_tracked.Contains(id)) _paddedAt.Remove(id);
        foreach (ulong id in new List<ulong>(_originalPositions.Keys))
            if (!GodotObject.IsInstanceIdValid(id) || !_tracked.Contains(id)) _originalPositions.Remove(id);
    }
}
