namespace STS2Mobile.Platform;

/// <summary>
/// The game starts a card play the moment a hand card is pressed. In portrait that press must only arm a gesture: a tap
/// does nothing, a still finger opens the card large, and the card is picked up only once the finger has moved
/// <see cref="TouchCardMath.PickupSlop"/>. The patched <c>NPlayerHand.OnHolderPressed</c> asks this gate whether to start
/// the play; the touch handler arms the gate while the hand is in play mode and re-invokes the game's own handler once the
/// finger has moved, with the finger still down, so the game's release-to-target path takes over unchanged.
/// <para>Only presses the engine emulated from a touch are held (its emulated mouse events carry device <c>-1</c>); a real
/// mouse, landscape and the hand's selection modes keep the game's immediate pickup. This class is engine-free on purpose:
/// the patched game assembly, which holds no Godot reference of its own here, calls it with plain numbers.</para>
/// </summary>
public static class HandPickupGate
{
    private static bool _enabled;
    private static bool _touchPress;

    /// <summary>
    /// Records whether the card press that is about to reach the hand came from a touch. Called from the patched
    /// <c>NHandCardHolder.OnMousePressed</c> with the event's device id.
    /// </summary>
    public static void NotePress(int device)
    {
        _touchPress = device < 0;
    }

    /// <summary>
    /// Called from the patched <c>NPlayerHand.OnHolderPressed</c>. True means this press waits for the finger to move: the
    /// game's play does not start, and the touch handler starts it itself after the pickup slop, with the finger still down.
    /// </summary>
    public static bool Defer()
    {
        bool touch = _touchPress;
        _touchPress = false;
        return _enabled && touch;
    }

    /// <summary>The hand's presses wait for the finger: portrait, play mode, no card in play, no overlay.</summary>
    public static void Enable() => _enabled = true;

    /// <summary>Stops deferring presses.</summary>
    public static void Disable()
    {
        _enabled = false;
        _touchPress = false;
    }
}
