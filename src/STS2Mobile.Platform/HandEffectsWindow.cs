namespace STS2Mobile.Platform;

/// <summary>
/// The whole hand's Reduce Effects decision. The setting exists for the frame cost of a drawn card: the game runs its
/// half-second <c>AnimShow</c> (a shader-width write on every frame of it) for every card it draws, and a card arriving
/// from the draw pile also draws over the whole card while it is at the pile and glides to its slot. A card that only
/// re-spaces within the hand — a play closing the row, a draw pushing the earlier cards along, the hand's own
/// enable/disable slide — restarts no tween and is as cheap to draw with its glow as without it, so it must keep its
/// effects.
/// <para>The decision is taken once for the hand, not per card: the first arrival opens the window, every card drawn into
/// a hand whose window is open stays hidden, and the window closes on one frame — <see cref="ShouldRestore"/> turning
/// true — after which the caller puts the whole hand's effects back together. Three bounds close it even when the motion
/// signal never settles: the hand being still for <see cref="SettleFrames"/> frames with no draw batch open, the hand
/// being quiet for <see cref="QuietFrames"/> frames while a batch is still open, and a hard cap of
/// <see cref="MaxHiddenFrames"/> frames (10 s at 60 fps) from the first arrival, so a playable glow can never disappear
/// for good. This type is engine-free on purpose: frames are plain numbers, which keeps the rule unit-testable.</para>
/// </summary>
public struct HandEffectsWindow
{
    /// <summary>
    /// Frames the hand must be still, with no draw batch open, before the effects come back. A glide can pause for a
    /// frame at an ease plateau, and a card that has just landed can be nudged by the next card of the draw; the restore
    /// also only begins after the frames an observer still reads as motion (a position sampled once a frame).
    /// </summary>
    public const long SettleFrames = 2;

    /// <summary>
    /// And the window closes this long after the last motion whatever a draw batch says: a batch whose count lock waits
    /// for a card that never comes must not keep a glow off a hand that is not moving.
    /// </summary>
    public const long QuietFrames = 20;

    /// <summary>And no drawn card stays hidden longer than this, full stop (10 s at 60 fps).</summary>
    public const long MaxHiddenFrames = 600;

    private bool _hiding;
    private long _startFrame;
    private long _lastMotionFrame;

    /// <summary>Whether the hand's drawn cards are hidden: a card arriving this frame must be hidden with its hand.</summary>
    public readonly bool Hiding => _hiding;

    /// <summary>The frame the current window opened, from which the hard cap counts.</summary>
    public readonly long StartFrame => _startFrame;

    /// <summary>Forgets the window (the hand session ended, or nothing is hidden any more).</summary>
    public void Reset()
    {
        _hiding = false;
        _startFrame = 0;
        _lastMotionFrame = long.MinValue / 4;
    }

    /// <summary>
    /// A card arrives from the draw pile this frame, so the hand's effects are hidden from now on. A window that is
    /// already open keeps its start frame: the cap counts from the first arrival of the draw, not the latest card, so
    /// the hand is put back together as one.
    /// </summary>
    public void Begin(long frame)
    {
        if (!_hiding)
        {
            _hiding = true;
            _startFrame = frame;
            _lastMotionFrame = frame;
            return;
        }

        NoteMotion(frame);
    }

    /// <summary>A holder moved on screen this frame (or a card arrived): the settle and quiet counts restart.</summary>
    public void NoteMotion(long frame)
    {
        if (_hiding && frame > _lastMotionFrame)
            _lastMotionFrame = frame;
    }

    /// <summary>
    /// Whether the whole hand gets its effects back on this frame. True closes the window; the caller restores every
    /// entry in one sweep, so no card is put back on its own frame. The window also closes when Reduce Effects is off, so
    /// switching the setting off inside a draw restores the hand on that frame.
    /// </summary>
    public bool ShouldRestore(bool reduceEffects, bool drawOpen, long frame)
    {
        if (!_hiding)
            return false;

        long still = frame - _lastMotionFrame;
        bool settled = !drawOpen && still > SettleFrames;
        bool quiet = still >= QuietFrames;
        bool capped = frame - _startFrame >= MaxHiddenFrames;
        if (!reduceEffects || settled || quiet || capped)
        {
            _hiding = false;
            return true;
        }

        return false;
    }
}
