using System.Collections.Generic;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Keeps hand cards on their portrait paths when the game re-lays out the hand for a desktop (without it the hand bounced on
/// every touch, as the cards came in at a fight's start and after each card played).
/// The game's layout (<c>NPlayerHand.RefreshLayout</c>) runs on every card drawn, played, focused or put back and gives every
/// card a desktop target, about 490 units below its portrait slot; setting a target moves the card one animation step at once
/// (about an eighth of the way), before the per-frame pass can set its portrait target again. So each card's place is recorded
/// at every pass, and a card whose target the game changed since the last pass is put back where that pass left it: it then
/// moves only toward its portrait slot, as smoothly as the game's own animation.
/// </summary>
internal sealed class HandStillness
{
    private readonly Dictionary<ulong, Place> _records = new();
    private int _passes;

    /// <summary>
    /// For the proof runs only (<c>phone-notes</c>): the earlier rule, which put a card back only when its own slot
    /// had not changed, so the motion of both can be measured in one run. Never set otherwise.
    /// </summary>
    internal static bool ProofOldRule;

    private struct Place
    {
        public Vector2 TargetPosition;
        public Vector2 TargetScale;
        public float TargetAngle;
        public Vector2 Position;
        public Vector2 Scale;
        public float Rotation;
        // Where the holder sat on screen: the game slides the whole hand on its enable/disable tween, and a card carried
        // by that slide is moving even when its own transform did not change.
        public Vector2 Global;
        public ulong Frame;
    }

    /// <summary>
    /// Before setting the holder's target to its slot: when the last pass left the holder with a target and the game has
    /// since changed <b>any</b> of position, scale or angle, put the holder back where that pass left it. Returns whether
    /// it did. The record is refreshed on every pass, so a hand that grows or is re-laid out does not invalidate it; a
    /// held or out-of-hand card's record goes stale after two frames, letting a returning card start from where it is.
    /// </summary>
    public bool RestoreIfRetargeted(Control holder, Vector2 slot, Vector2 currentPositionTarget,
        Vector2 currentScaleTarget, float currentAngleTarget)
    {
        if (!_records.TryGetValue(holder.GetInstanceId(), out Place record)) return false;
        if (ProofOldRule)
        {
            if ((record.TargetPosition - slot).LengthSquared() > 0.25f ||
                (currentPositionTarget - slot).LengthSquared() <= 0.25f) return false;
        }
        else
        {
            if (Engine.GetProcessFrames() - record.Frame > 2) return false;
            bool samePosition = (currentPositionTarget - record.TargetPosition).LengthSquared() <= 0.25f;
            bool sameScale = (currentScaleTarget - record.TargetScale).LengthSquared() <= 0.000004f;
            bool sameAngle = Mathf.Abs(currentAngleTarget - record.TargetAngle) <= 0.05f;
            if (samePosition && sameScale && sameAngle) return false;
        }
        holder.Position = record.Position;
        holder.Scale = record.Scale;
        holder.RotationDegrees = record.Rotation;
        return true;
    }

    /// <summary>
    /// Forgets every record. The transform a record holds belongs to the hand session that is ending: a card returning to
    /// a later session must be placed at the draw pile again (no record means a first pass), not capped against where it
    /// stood before the leave.
    /// </summary>
    public void Clear() => _records.Clear();

    /// <summary>Whether this holder has been through a pass (a returning card keeps its record; a brand-new one has none).</summary>
    public bool HasRecord(Control holder) => _records.ContainsKey(holder.GetInstanceId());

    /// <summary>Where the previous pass left the holder, for capping what one frame may move.</summary>
    public Vector2 PreviousPosition(Control holder, Vector2 fallback) =>
        _records.TryGetValue(holder.GetInstanceId(), out Place record) ? record.Position : fallback;

    /// <summary>The angle the previous pass drew the holder at, for the draw's direction-locked turn toward its slot.</summary>
    public float PreviousRotation(Control holder, float fallback) =>
        _records.TryGetValue(holder.GetInstanceId(), out Place record) ? record.Rotation : fallback;

    /// <summary>
    /// Whether the holder's drawn transform, or its place on screen, differs from what the last pass recorded: a native
    /// animation loop is still running (or the game wrote the transform), or the hand itself is moving (its enable and
    /// disable tweens slide the whole hand), so the pass has work to do. Engine reads only, no reflection.
    /// </summary>
    public bool Moved(Control holder)
    {
        if (!_records.TryGetValue(holder.GetInstanceId(), out Place record)) return true;
        if ((holder.Position - record.Position).LengthSquared() > 0.25f) return true;
        if ((holder.Scale - record.Scale).LengthSquared() > 0.000004f) return true;
        return Mathf.Abs(holder.RotationDegrees - record.Rotation) > 0.05f;
    }

    /// <summary>
    /// Whether the holder has moved <b>on screen</b> since the last pass: its position, local or global, changed by more
    /// than half a unit. Scale and angle do not count — they are not the card travelling — so this is the measure an
    /// observer watching the hand takes, and the one the effects and the frame cap follow. Engine reads only.
    /// </summary>
    public bool MovedOnScreen(Control holder)
    {
        if (!_records.TryGetValue(holder.GetInstanceId(), out Place record)) return true;
        if ((holder.Position - record.Position).LengthSquared() > 0.25f) return true;
        return (holder.GlobalPosition - record.Global).LengthSquared() > 0.25f;
    }

    /// <summary>
    /// Whether the holder is short of <paramref name="target"/> and has not moved at all since the previous pass: its
    /// native position animation has ended (the game cancels it when a layout pass sets a new target, and the restore above
    /// puts the position back), so nothing will carry it the rest of the way.
    /// </summary>
    public bool IsStalled(Control holder, Vector2 target)
    {
        if (!_records.TryGetValue(holder.GetInstanceId(), out Place record)) return false;
        if (Engine.GetProcessFrames() - record.Frame > 2) return false;
        if ((holder.Position - target).LengthSquared() <= 0.25f) return false;
        return (record.Position - holder.Position).LengthSquared() <= 0.25f;
    }

    /// <summary>After setting the holder's target to its slot: the target and where the holder is now.</summary>
    public void Record(Control holder, Vector2 targetPosition, Vector2 targetScale, float targetAngle)
    {
        ulong id = holder.GetInstanceId();
        _records[id] = new Place
        {
            TargetPosition = targetPosition,
            TargetScale = targetScale,
            TargetAngle = targetAngle,
            Position = holder.Position,
            Global = holder.GlobalPosition,
            Scale = holder.Scale,
            Rotation = holder.RotationDegrees,
            Frame = Engine.GetProcessFrames(),
        };
        if (++_passes % 600 == 0)
        {
            var dead = new List<ulong>();
            foreach (ulong recordId in _records.Keys)
                if (!GodotObject.IsInstanceIdValid(recordId)) dead.Add(recordId);
            foreach (ulong deadId in dead) _records.Remove(deadId);
        }
    }
}
