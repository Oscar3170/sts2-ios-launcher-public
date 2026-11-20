using System;
using System.Collections.Generic;

namespace STS2Mobile.Platform;

/// <summary>
/// The touch decisions for a hand card in portrait. A press is not a pickup: the card stays in the hand until the finger
/// has moved <see cref="PickupSlop"/>, so a tap does nothing and a still finger can open the card large. While an
/// untargeted card is dragged its bottom edge stays <see cref="DragLift"/> above the finger; a targeted card parks at
/// <see cref="AimingCardCenter"/> once aiming starts. A release reaches for a target a little outside the creature's hit
/// box. Distances are canvas units on the 1080-wide portrait canvas, where one phone point is about 2.9 units; the
/// starting values are TouchSpireUI's 1680-wide ones converted (<c>1080 / 1680</c>).
/// </summary>
public static class TouchCardMath
{
    /// <summary>How far the finger must move from the press before the card is picked up (TouchSpireUI's 24, about 5.5 pt).</summary>
    public const float PickupSlop = 16f;

    /// <summary>How long a still finger must stay on a hand card before the card is inspected (TouchSpireUI's 0.60 s).</summary>
    public const double HoldSeconds = 0.6;

    /// <summary>How far outside a creature's hit box a release still targets it (TouchSpireUI's 60, about 13.4 pt).</summary>
    public const float TargetPadding = 39f;

    /// <summary>How far from the nearest valid creature a release still snaps to it, past the padding (about 31 pt).</summary>
    public const float TargetSnap = 90f;

    /// <summary>How far above the finger the dragged card's bottom edge is held, so the finger does not hide it.</summary>
    public const float DragLift = 51f;

    /// <summary>The dragged card's opacity: slightly faded, so the creature under it stays visible.</summary>
    public const float DragOpacity = 0.85f;

    private const float EdgeMargin = 8f;

    /// <summary>
    /// The aiming card's size: clearly larger than the hand's resting card for this canvas and hand size (1.5 times it),
    /// reached by easing from the pressed size, never as a jump at pickup.
    /// </summary>
    public static float AimedCardScaleFor(float restingScale) => restingScale * 1.5f;

    /// <summary>The End Turn button's pivot Y: ComputeHudLayout's <c>EndTurnShowY</c> is its unscaled top-left Y.</summary>
    public const float EndTurnHalfHeight = 45f;

    /// <summary>The visual top edge of the End Turn button from <see cref="PortraitCombatMath.ComputeHudLayout"/>.</summary>
    public static float EndTurnTopY(PortraitHudLayout hud) =>
        hud.EndTurnShowY - EndTurnHalfHeight * (hud.EndTurnScale - 1f);

    /// <summary>
    /// The centre of the fixed aim-park position: horizontally centred on the canvas, with the card's top edge on the End
    /// Turn button's top edge, so the aimed card sits over End Turn and the hand (inactive under a held card) while the
    /// native targeting arrow runs from it to the creature under the finger.
    /// </summary>
    public static (float X, float Y) AimingCardCenter(float endTurnTopY, float aimScale, float canvasWidth) =>
        (canvasWidth * 0.5f, endTurnTopY + PortraitCombatMath.CardDefaultHeight * aimScale * 0.5f);

    /// <summary>Whether a finger has moved far enough from its press for the press to become a drag.</summary>
    public static bool HasMovedPastPickupSlop(float movedCanvasUnits) => movedCanvasUnits >= PickupSlop;

    /// <summary>Whether a finger that has not crossed <see cref="PickupSlop"/> has rested long enough to inspect.</summary>
    public static bool IsStationaryHold(float movedCanvasUnits, double heldSeconds) =>
        movedCanvasUnits < PickupSlop && heldSeconds >= HoldSeconds;

    /// <summary>A creature's hit box as a centre and half extents on the portrait canvas.</summary>
    public readonly record struct TargetBox(float CenterX, float CenterY, float HalfWidth, float HalfHeight);

    /// <summary>
    /// The index of the valid creature nearest a released pointer: inside <see cref="TargetPadding"/> of its hit box
    /// first, then inside <see cref="TargetSnap"/> for a near miss. <c>-1</c> when the pointer is beyond both, which puts
    /// the card back in the hand rather than starting a click-to-aim.
    /// </summary>
    public static int NearestTarget(IReadOnlyList<TargetBox> targets, float x, float y)
    {
        int near = NearestWithin(targets, x, y, TargetPadding);
        return near >= 0 ? near : NearestWithin(targets, x, y, TargetSnap);
    }

    private static int NearestWithin(IReadOnlyList<TargetBox> targets, float x, float y, float radius)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < targets.Count; i++)
        {
            TargetBox target = targets[i];
            if (Math.Abs(x - target.CenterX) > target.HalfWidth + radius ||
                Math.Abs(y - target.CenterY) > target.HalfHeight + radius)
                continue;
            float dx = x - target.CenterX, dy = y - target.CenterY;
            float distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }
        return best;
    }

    /// <summary>
    /// Where a dragged card's centre goes for a finger at <paramref name="fingerX"/>, <paramref name="fingerY"/>: above
    /// the finger so the card's bottom edge is <see cref="DragLift"/> clear of it, and clamped inside the canvas. The
    /// scale is the card's on-canvas size (its own scale times the hand's), so the visual hit box lands where the finger
    /// expects even when the hand container is scaled.
    /// </summary>
    public static (float X, float Y) DraggedCardCenter(
        float fingerX, float fingerY, float scaleX, float scaleY, float canvasWidth, float canvasHeight)
    {
        float sx = Math.Max(scaleX, 0.01f);
        float sy = Math.Max(scaleY, 0.01f);
        float halfWidth = PortraitCombatMath.CardDefaultWidth * sx * 0.5f;
        float halfHeight = PortraitCombatMath.CardDefaultHeight * sy * 0.5f;
        float x = Math.Clamp(fingerX, halfWidth + EdgeMargin,
            Math.Max(halfWidth + EdgeMargin, canvasWidth - halfWidth - EdgeMargin));
        float y = Math.Clamp(fingerY - halfHeight - DragLift, halfHeight + EdgeMargin,
            Math.Max(halfHeight + EdgeMargin, canvasHeight - halfHeight - EdgeMargin));
        return (x, y);
    }

    /// <summary>The single-scale case of <see cref="DraggedCardCenter(float, float, float, float, float, float)"/>.</summary>
    public static (float X, float Y) DraggedCardCenter(
        float fingerX, float fingerY, float scale, float canvasWidth, float canvasHeight) =>
        DraggedCardCenter(fingerX, fingerY, scale, scale, canvasWidth, canvasHeight);
}
