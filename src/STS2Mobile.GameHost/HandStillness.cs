using System.Collections.Generic;
using Godot;

namespace STS2Mobile.GameHost;

/// <summary>
/// Samples the portrait hand's drawn transforms for arrival motion and the frame-rate governor.
/// </summary>
internal sealed class HandStillness
{
    private readonly Dictionary<ulong, Place> _records = new();
    private int _passes;

    private struct Place
    {
        public Vector2 Position;
        public Vector2 Scale;
        public float Rotation;
        // Where the holder sat on screen: the game slides the whole hand on its enable/disable tween, and a card carried
        // by that slide is moving even when its own transform did not change.
        public Vector2 Global;
    }

    /// <summary>
    /// Forgets every record. The transform a record holds belongs to the hand session that is ending: a card returning to
    /// a later session must be placed at the draw pile again (no record means a first pass), not capped against where it
    /// stood before the leave.
    /// </summary>
    public void Clear() => _records.Clear();

    /// <summary>
    /// Whether this holder has been through a pass (a returning card keeps its record; a brand-new one has none). The game
    /// creates a holder per drawn card and frees it when the card leaves the hand, so a holder without a record is exactly a
    /// card that has just arrived from the draw pile — which is what the Reduce Effects pass hides, and a holder with one
    /// can only re-space, which keeps its effects.
    /// </summary>
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
    /// observer watching the hand takes, and the one the frame cap and the hand's effects window follow. Engine reads only.
    /// </summary>
    public bool MovedOnScreen(Control holder)
    {
        if (!_records.TryGetValue(holder.GetInstanceId(), out Place record)) return true;
        if ((holder.Position - record.Position).LengthSquared() > 0.25f) return true;
        return (holder.GlobalPosition - record.Global).LengthSquared() > 0.25f;
    }

    public void Record(Control holder)
    {
        ulong id = holder.GetInstanceId();
        _records[id] = new Place
        {
            Position = holder.Position,
            Global = holder.GlobalPosition,
            Scale = holder.Scale,
            Rotation = holder.RotationDegrees,
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
