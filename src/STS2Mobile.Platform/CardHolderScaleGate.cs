using System;

namespace STS2Mobile.Platform;

/// <summary>
/// Lets the portrait host answer a card holder's resting and hovered scale (<c>NCardHolder.SmallScale</c> and
/// <c>HoverScale</c>). A portrait grid draws its cards at its three-column size; with the game's own 0.8 and 1.0 its hover
/// and unhover tweens pulled a card away from that size, and a correction applied each frame could lose the race to the frame
/// drawn. Asked here, the game animates between the column size and itself.
/// </summary>
public static class CardHolderScaleGate
{
    /// <summary>The host's answer for a holder: its uniform scale, or 0 for the game's own.</summary>
    public static Func<object, float>? Scale { private get; set; }

    // The patched game calls this with the holder at the start of both getters. No host, or 0, means the game's own value.
    public static float ScaleFor(object holder) =>
        SafeAreaCanvas.IsPortrait && Scale is { } scale ? scale(holder) : 0f;
}
