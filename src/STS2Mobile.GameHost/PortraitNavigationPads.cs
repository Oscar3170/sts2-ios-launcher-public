using System.Collections.Generic;
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

    private readonly HashSet<ulong> _tracked = new();
    private readonly Dictionary<ulong, Vector2> _paddedAt = new();
    private readonly List<ulong> _dead = new();

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
            // A screen layout that owns this control decides its pad: it knows the neighbours and may cap it short on purpose.
            if (control.HasMeta(PlannedControlMeta) || (PhoneHitArea.Of(control) is { } screenPad && screenPad.HasMeta(ScreenPadMeta)))
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
            if (_paddedAt.TryGetValue(id, out Vector2 seen) && seen == control.Size && PhoneHitArea.Of(control) != null) continue;
            Vector2 scale = control.GetGlobalTransformWithCanvas().Scale.Abs();
            if (scale.X < 0.01f || scale.Y < 0.01f) continue;
            float padX = Mathf.Max(0f, (PortraitBands.MinTarget - control.Size.X * scale.X) * 0.5f) / scale.X;
            float padY = Mathf.Max(0f, (PortraitBands.MinTarget - control.Size.Y * scale.Y) * 0.5f) / scale.Y;
            if (padX > 0f || padY > 0f) PhoneHitArea.Grow(control, padX, padY, padX, padY);
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
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                PhoneHitArea.Remove(control);
        _tracked.Clear();
        _paddedAt.Clear();
    }

    public void PruneDead()
    {
        _tracked.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in new List<ulong>(_paddedAt.Keys))
            if (!GodotObject.IsInstanceIdValid(id) || !_tracked.Contains(id)) _paddedAt.Remove(id);
    }
}
