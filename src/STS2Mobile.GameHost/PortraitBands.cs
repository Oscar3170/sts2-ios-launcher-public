namespace STS2Mobile.GameHost;

/// <summary>
/// The fixed bands at the top of a run screen in portrait, in canvas units (1080 across; 2.88 units are one point on an
/// iPhone 11 Pro, so 127 units are the 44 points a touch target should have). From the top: the safe cap over the notch
/// (the screen's real inset, passed around as capHeight; 0 on a notchless 9:16 phone), the top bar's row, the potion row
/// (potions do not fit in the top bar's row), then the relic strip. Every portrait layout takes its top from
/// here.
/// </summary>
internal static class PortraitBands
{
    /// <summary>One point of an iPhone 11 Pro in canvas units.</summary>
    public const float UnitsPerPoint = 1080f / 375f;

    /// <summary>The smallest side a touch target should have: 44 points.</summary>
    public const float MinTarget = 127f;

    /// <summary>The top bar's own row: character, health, gold, floor, boss, then map, deck and settings.</summary>
    public const float TopBarRowHeight = 88f;

    /// <summary>The row's content inset from the safe cap's edge.</summary>
    public const float TopBarRowInset = 4f;

    /// <summary>The potion row under the top bar's row.</summary>
    public const float PotionRowHeight = 140f;

    /// <summary>
    /// A potion slot's side (37 points). The potion row runs from just under the top row to just above the stone's torn edge
    /// (PortraitHeaderMath.PotionRow, 119 units); the tray's dark box, the slot plus 4 units, fits it with the tray on the
    /// stone and clear of the ascension badge. Smaller than <see cref="MinTarget"/> by the user's choice: a 44 point slot
    /// reached into the top row.
    /// </summary>
    public const float PotionSlotSize = 106f;

    /// <summary>Everything the top bar covers below the safe cap. Screens that hide the relic strip start here.</summary>
    public const float TopBarHeight = TopBarRowHeight + PotionRowHeight;

    /// <summary>The relic strip right under the stone: two rows of 96 unit relics (186 with 78 unit relics) and the space below.</summary>
    public const float RelicBandHeight = 222f;

    /// <summary>Below the safe cap, where a screen that keeps the relic strip in view starts (the fight's stage).</summary>
    public const float RunContentTop = TopBarHeight + RelicBandHeight;

    /// <summary>The bottom of the stone header (and of the potion row inside it) in canvas coordinates.</summary>
    public static float HeaderBottom(float capHeight) => capHeight + TopBarHeight;

    /// <summary>The bottom of the relic band, below which the fight's stage starts, in canvas coordinates.</summary>
    public static float RelicBandBottom(float capHeight) => capHeight + RunContentTop;
}
