using System;

namespace STS2Mobile.Platform;

/// <summary>
/// When a press has become a drag rather than a tap, on the surfaces where a scroll and a tap share the screen. A drag at
/// or past its surface's distance is a swipe: the control the finger went down on is let go before the finger lifts, so it
/// cannot pick a card, select a sort, toggle View Upgrades, travel a map node, take a reward or pick an option.
/// <para>
/// Each surface keeps the distance and the direction it already used: the card grids and lists scroll at 24 units when the
/// drag is mostly along the scroll's own axis (1.2 times the movement across it); the header rows read the same rule along
/// their own axis at 28 units; the events scroll at 18 units up or down, whatever the sideways movement; the
/// select-then-confirm items allow 23 units of slop in any direction; the map and the loot list let go of a press on any
/// movement past 24 units, which is the map's own behaviour (its points carry a 20 or 30 unit cancel in their scenes, in any
/// direction). Lengths are canvas units on the 1080-wide portrait canvas, where one phone point is about 2.9 units.
/// </para>
/// </summary>
public static class SwipeMath
{
    /// <summary>A card grid or list starts to scroll after this much along its axis (about 8 points).</summary>
    public const float GridDistance = 24f;

    /// <summary>A grid drag must move this many times more along the axis than across it, so a sideways drag stays a tap.</summary>
    public const float Dominance = 1.2f;

    /// <summary>The map and the loot list let go of a press after this much movement in any direction.</summary>
    public const float ScreenDistance = 24f;

    /// <summary>
    /// A drag along one axis that is at least <paramref name="distance"/> long and at least <paramref name="dominance"/>
    /// times its movement across that axis: the card grids and lists, and the header rows.
    /// </summary>
    public static bool Along(float along, float across, float distance, float dominance) =>
        MathF.Abs(along) >= distance && MathF.Abs(along) >= MathF.Abs(across) * dominance;

    /// <summary>A drag that is at least <paramref name="distance"/> up or down, whatever it does sideways: the events.</summary>
    public static bool Vertical(float dy, float distance) => MathF.Abs(dy) >= distance;

    /// <summary>A drag of at least <paramref name="distance"/> in any direction: the map and the loot list.</summary>
    public static bool AnyDirection(float dx, float dy, float distance) =>
        dx * dx + dy * dy >= distance * distance;
}
