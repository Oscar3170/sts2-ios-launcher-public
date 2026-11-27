using System;
using System.Collections.Generic;

namespace STS2Mobile.Platform;

/// <summary>
/// The touch decisions and placements for the portrait overlays. A press is not a pickup: the card stays in the hand until
/// the finger has moved <see cref="PickupSlop"/>, so a tap does nothing and a still finger can open the card large. An
/// untargeted card is dragged above the finger (its bottom <see cref="DragLift"/> clear of it) or, when the player turns
/// Readable Drag off, keeps the point grabbed at the press under the finger (<see cref="GrabbedCardCenter"/>). A targeted
/// card parks at <see cref="AimingCardCenter"/> once aiming starts. A release reaches for a target a little outside the
/// creature's hit box; a hand card's tips go beside the card (<see cref="PlaceTipBlockBeside"/>). Distances are canvas units
/// on the 1080-wide portrait canvas, where one phone point is about 2.9 units.
/// </summary>
public static class TouchCardMath
{
    /// <summary>How far the finger must move from the press before the card is picked up (16 canvas units, about 5.5 pt).</summary>
    public const float PickupSlop = 16f;

    /// <summary>How long a still finger must stay on a hand card before the card is inspected (0.60 s).</summary>
    public const double HoldSeconds = 0.6;

    /// <summary>How far outside a creature's hit box a release still targets it (39 canvas units, about 13.4 pt).</summary>
    public const float TargetPadding = 39f;

    /// <summary>How far from the nearest valid creature a release still snaps to it, past the padding (about 31 pt).</summary>
    public const float TargetSnap = 90f;

    /// <summary>How far above the finger the dragged card's bottom edge is held, so the finger does not hide it
    /// (51 canvas units, about 17.6 pt).</summary>
    public const float DragLift = 51f;

    private const float EdgeMargin = 8f;

    /// <summary>The gap the inspect screen's View Upgrades box keeps from the stage top or the card top (24 canvas units, about 8.3 pt).</summary>
    public const float StageTipMargin = 24f;

    private const float UpgradeBoxLeft = 8f, UpgradeBoxWidth = 272f, UpgradeBoxHeight = 64f, UpgradeBoxScale = 0.85f;

    /// <summary>
    /// The aiming card's size: one value per shape, 1.5 times the shape's largest resting hand card
    /// (<c>HandCardScaleFor(bands, 1)</c>), so the parked card is the same size whatever the hand holds. It is reached by
    /// easing from the pressed size, never as a jump or a shrink at pickup.
    /// </summary>
    public static float AimedCardScaleFor(PortraitCombatBands bands) =>
        PortraitCombatMath.HandCardScaleFor(bands, 1) * 1.5f;

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

    /// <summary>Where a hand card's tip block lands on the canvas, and the scale it is drawn at.</summary>
    public readonly record struct TipBlock(float X, float Y, float Width, float Height, float Scale, bool RightOfOwner);

    /// <summary>
    /// A hand card's tips beside the card, as the landscape game draws them: the whole block on one side of the card (left
    /// or right, whichever has room for it), never over the card, inside the canvas. The block is
    /// <paramref name="blockWidth"/> by <paramref name="blockHeight"/> at scale 1; it keeps <paramref name="margin"/> from
    /// the canvas edges and <paramref name="gap"/> from the card. It is drawn at <paramref name="preferredScale"/> when that
    /// side and the canvas height fit it, and shrunk only as far as they require.
    /// </summary>
    public static TipBlock PlaceTipBlockBeside(float ownerX, float ownerY, float ownerWidth, float ownerHeight,
        float canvasWidth, float canvasHeight, float blockWidth, float blockHeight,
        float preferredScale, float margin, float gap)
    {
        float width = MathF.Max(blockWidth, 0.01f);
        float height = MathF.Max(blockHeight, 0.01f);
        float leftRoom = MathF.Max(0f, ownerX - gap - margin);
        float rightRoom = MathF.Max(0f, canvasWidth - margin - (ownerX + ownerWidth + gap));
        float canvasRoom = MathF.Max(0.01f, canvasHeight - 2f * margin);
        float cap = MathF.Min(preferredScale, canvasRoom / height);
        // The native default is the right side; take it whenever the block fits there at the capped scale, else the left
        // when it fits there, else the roomier side with the block shrunk to it.
        bool right = rightRoom >= width * cap || (!(leftRoom >= width * cap) && rightRoom >= leftRoom);
        float room = right ? rightRoom : leftRoom;
        float scale = MathF.Max(0f, MathF.Min(cap, room / width));
        float drawnWidth = width * scale;
        float drawnHeight = height * scale;
        float x = right ? ownerX + ownerWidth + gap : ownerX - gap - drawnWidth;
        x = Math.Clamp(x, margin, MathF.Max(margin, canvasWidth - margin - drawnWidth));
        float y = Math.Clamp(ownerY, margin, MathF.Max(margin, canvasHeight - margin - drawnHeight));
        return new TipBlock(x, y, drawnWidth, drawnHeight, scale, right);
    }

    /// <summary>
    /// The inspect screen's native "View Upgrades" box in portrait, on the canvas: the native 272 x 64 box at
    /// <see cref="UpgradeBoxScale"/> scale, its left edge in the free margin beside the centred card and its top
    /// <see cref="StageTipMargin"/> below the stage top or the card's top, whichever is lower, so it clears the card, the
    /// arrows, the rail and the hand. Width and height are unscaled (the caller scales the control about its top-left).
    /// </summary>
    public static (float Left, float Top, float Width, float Height, float Scale) InspectUpgradeRect(
        float screenHeight, float stageTop)
    {
        float cardTop = screenHeight * 0.5f - PortraitCombatMath.CardDefaultHeight;
        float top = MathF.Max(stageTop + StageTipMargin, cardTop + StageTipMargin);
        return (UpgradeBoxLeft, top, UpgradeBoxWidth, UpgradeBoxHeight, UpgradeBoxScale);
    }

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
    /// The uniform scale a hover-tip column takes on the canvas: at most <paramref name="preferred"/>, reduced to keep
    /// <paramref name="margin"/> clear on each side; the height fit never goes below 0.5, so a column keeps a readable
    /// size even when it has to run under the item it describes.
    /// </summary>
    public static float FitTipColumnScale(
        float preferred, float columnWidth, float columnHeight, float canvasWidth, float canvasHeight, float margin)
    {
        float s = Math.Min(preferred, (canvasWidth - 2f * margin) / Math.Max(columnWidth, 0.01f));
        return Math.Min(s, Math.Max(0.5f, (canvasHeight - 2f * margin) / Math.Max(columnHeight, 0.01f)));
    }

    /// <summary>
    /// Where a dragged card's centre goes when Readable Drag is off: the finger plus the offset from the finger to the
    /// card's centre at the press, so the point the player grabbed stays under the finger as the native mouse drag does.
    /// It is clamped inside the canvas exactly as <see cref="DraggedCardCenter"/> is.
    /// </summary>
    public static (float X, float Y) GrabbedCardCenter(
        float fingerX, float fingerY, float grabOffsetX, float grabOffsetY,
        float scale, float canvasWidth, float canvasHeight)
    {
        float s = Math.Max(scale, 0.01f);
        float halfWidth = PortraitCombatMath.CardDefaultWidth * s * 0.5f;
        float halfHeight = PortraitCombatMath.CardDefaultHeight * s * 0.5f;
        float x = Math.Clamp(fingerX + grabOffsetX, halfWidth + EdgeMargin,
            Math.Max(halfWidth + EdgeMargin, canvasWidth - halfWidth - EdgeMargin));
        float y = Math.Clamp(fingerY + grabOffsetY, halfHeight + EdgeMargin,
            Math.Max(halfHeight + EdgeMargin, canvasHeight - halfHeight - EdgeMargin));
        return (x, y);
    }

    /// <summary>
    /// Where a dragged card's centre goes for a finger at <paramref name="fingerX"/>, <paramref name="fingerY"/>: above
    /// the finger so the card's bottom edge is <see cref="DragLift"/> clear of it, and clamped inside the canvas. The
    /// scale is the card's on-canvas size (its own scale times the hand's), so the visual hit box lands where the finger
    /// expects even when the hand container is scaled.
    /// </summary>
    public static (float X, float Y) DraggedCardCenter(
        float fingerX, float fingerY, float scale, float canvasWidth, float canvasHeight)
    {
        float s = Math.Max(scale, 0.01f);
        float halfWidth = PortraitCombatMath.CardDefaultWidth * s * 0.5f;
        float halfHeight = PortraitCombatMath.CardDefaultHeight * s * 0.5f;
        float x = Math.Clamp(fingerX, halfWidth + EdgeMargin,
            Math.Max(halfWidth + EdgeMargin, canvasWidth - halfWidth - EdgeMargin));
        float y = Math.Clamp(fingerY - halfHeight - DragLift, halfHeight + EdgeMargin,
            Math.Max(halfHeight + EdgeMargin, canvasHeight - halfHeight - EdgeMargin));
        return (x, y);
    }
}
