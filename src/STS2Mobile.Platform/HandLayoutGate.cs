using System;

namespace STS2Mobile.Platform;

/// <summary>Lets the active portrait host take over geometry before the game's hand moves any cards.</summary>
public static class HandLayoutGate
{
    public static Func<object, bool>? TryOwnTargets { private get; set; }

    // The patched game calls this with its NPlayerHand. No host means the game keeps all its own layout.
    public static bool SuppressNativeTargets(object hand) => SafeAreaCanvas.IsPortrait && TryOwnTargets?.Invoke(hand) == true;
}
