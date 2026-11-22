using System;
using System.Collections.Generic;

namespace STS2Mobile.Platform;

/// <summary>Where one relic holder's centre goes, in canvas units.</summary>
public readonly record struct TreasureHolderSlot(float CenterX, float CenterY);

/// <summary>
/// The portrait treasure room in canvas units (1080 across): the chest framed around the middle of the screen with its gold
/// burst at its mouth, the banner under the relic strip, the relic holders in three columns above the chest, and the labels
/// for an empty chest and a fought-over relic.
/// </summary>
public readonly record struct TreasureScene(
    float SceneTop,
    float MouthY,
    float ChestScale,
    float ChestX,
    float ChestY,
    float GoldX,
    float GoldY,
    float FloorX,
    float FloorY,
    float BannerScale,
    float BannerX,
    float BannerY,
    float EmptyX,
    float EmptyY,
    float EmptyScale,
    float FightX,
    float FightY,
    float FightScale);

/// <summary>
/// Pure geometry for the portrait treasure room. Lengths come from the room's own scene, which is authored in canvas units,
/// and from the 1680-wide reference phone layout, whose lengths convert through <see cref="OnePoint"/> (the phone's 1080-unit
/// canvas is the same physical width as that template's 1680). Ratios — the mouth's 56% down the screen, the holder columns —
/// need no conversion.
/// </summary>
public static class PortraitTreasureMath
{
    /// <summary>One unit of the 1680-wide reference phone layout in canvas units.</summary>
    public const float OnePoint = 1080f / 1680f;

    /// <summary>The chest sprite's authored width; the chest grows until the sprite covers the canvas, up to 1.56.</summary>
    public const float ChestArtWidth = 1075f;
    public const float ChestMaxScale = 1.56f;

    /// <summary>The sprite's centre inside the 800 x 500 chest control (its vines and open lid overhang the control).</summary>
    public const float ChestArtCenterX = 420f;

    /// <summary>The gold burst and its floor, relative to the chest control's top-left in the room's own scene units.</summary>
    public const float MouthOffsetX = 335f;
    public const float MouthOffsetY = 145f;
    public const float FloorOffsetX = 395f;
    public const float FloorOffsetY = 475f;

    /// <summary>How much brighter the chest is drawn so it reads against the dimmed act art.</summary>
    public const float ChestTint = 1.14f;

    /// <summary>The chest's mouth sits 56% of the way from the stone header's bottom to the bottom of the screen.</summary>
    public const float MouthFraction = 0.56f;

    /// <summary>The banner spans the reference's 1425.72 of 1680 units, and never grows past its 2.18 template scale.</summary>
    public const float BannerWidthFraction = 1425.72f / 1680f;
    public const float BannerMaxScale = 2.18f * OnePoint;
    public const float BannerHideDrop = 85f * OnePoint;

    /// <summary>A holder's shared hitbox: the native layouts use 136 to 204 units, the icon 136 either way.</summary>
    public const float HolderBox = 204f;
    public const float HolderHalfBox = 102f;

    /// <summary>Holder centres are 228 units apart at scale 1; three columns less a 110-unit margin fit the canvas.</summary>
    public const float HolderStepLength = 228f;
    public const int HolderColumns = 3;
    public const float HolderMaxScale = 2.05f * OnePoint;
    public const float HolderSideInset = 110f * OnePoint;

    /// <summary>The first holder row sits 375 units above the mouth, and never closer than this to the banner.</summary>
    public const float HolderRowDrop = 375f * OnePoint;
    public const float HolderBannerGap = 36f * OnePoint;

    /// <summary>The empty-chest label goes 460 units above the mouth; the fought-over label 400 below the header.</summary>
    public const float EmptyDrop = 460f * OnePoint;
    public const float EmptyScale = 1.8f * OnePoint;
    public const float FightDrop = 400f * OnePoint;
    public const float FightScale = 1.7f * OnePoint;

    /// <summary>The chest's gold mouth, measured down from the stone header's bottom (<paramref name="sceneTop"/>).</summary>
    public static float MouthY(float canvasHeight, float sceneTop) =>
        sceneTop + MouthFraction * Math.Max(0f, canvasHeight - sceneTop);

    /// <summary>How far the chest grows so its sprite covers the canvas width, never past <see cref="ChestMaxScale"/>.</summary>
    public static float ChestScale(float canvasWidth) =>
        canvasWidth <= 0f ? 1f : Math.Min(ChestMaxScale, canvasWidth / ChestArtWidth);

    /// <summary>
    /// How far the banner grows so it spans <see cref="BannerWidthFraction"/> of the canvas — the same share of the screen the
    /// reference gives it — never past <see cref="BannerMaxScale"/>.
    /// </summary>
    public static float BannerScale(float canvasWidth, float bannerWidth)
    {
        if (bannerWidth <= 1f || canvasWidth <= 0f) return 1f;
        return Math.Min(BannerMaxScale, canvasWidth * BannerWidthFraction / bannerWidth);
    }

    /// <summary>
    /// How much a relic holder grows: three 228-unit steps fit the canvas less its 110-unit side margins. At 1080 the
    /// reference's 2.05 cap applies instead, so a holder's 204-unit box is drawn 269 units (93 points) across, 31 units apart.
    /// </summary>
    public static float HolderFactor(float canvasWidth) =>
        Math.Min(HolderMaxScale, Math.Max(0f, canvasWidth - HolderSideInset) / (HolderColumns * HolderStepLength));

    /// <summary>The distance between holder centres.</summary>
    public static float HolderStep(float factor) => HolderStepLength * Math.Max(0f, factor);

    public static int HolderRows(int count) => Math.Max(1, (Math.Max(0, count) + HolderColumns - 1) / HolderColumns);

    /// <summary>
    /// The first holder row's centre: <see cref="HolderRowDrop"/> above the chest's mouth, with the rows stacking downwards,
    /// and never so high that a holder's box touches the banner. The phone's stone header and relic strip are taller than the
    /// reference's, so the banner holds a two-row grid down in both shapes.
    /// </summary>
    public static float FirstHolderRow(float mouthY, int count, float step, float factor, float bannerBottom)
    {
        float row = mouthY - HolderRowDrop - (HolderRows(count) - 1) * Math.Max(0f, step);
        return Math.Max(row, bannerBottom + HolderHalfBox * Math.Max(0f, factor) + HolderBannerGap);
    }

    /// <summary>Centres of the holders, three to a row, centred on the canvas, in the collection's own order.</summary>
    public static IReadOnlyList<TreasureHolderSlot> HolderSlots(int count, float centerX, float firstRow, float step)
    {
        var slots = new List<TreasureHolderSlot>(Math.Max(0, count));
        if (count <= 0) return slots;
        for (int row = 0; row * HolderColumns < count; row++)
        {
            int columns = Math.Min(HolderColumns, count - row * HolderColumns);
            for (int column = 0; column < columns; column++)
                slots.Add(new TreasureHolderSlot(
                    centerX + (column - (columns - 1) * 0.5f) * step,
                    firstRow + row * step));
        }
        return slots;
    }

    /// <summary>
    /// The whole composition for a canvas whose stone header ends at <paramref name="sceneTop"/> and whose relic strip ends at
    /// <paramref name="bannerTop"/>. <paramref name="bannerWidth"/>, <paramref name="emptyWidth"/> and
    /// <paramref name="fightWidth"/> are the game's controls, so the scale each needs is measured rather than assumed.
    /// </summary>
    public static TreasureScene Plan(
        float canvasWidth, float canvasHeight, float sceneTop, float bannerTop,
        float bannerWidth, float emptyWidth, float fightWidth)
    {
        float centerX = canvasWidth * 0.5f;
        float mouthY = MouthY(canvasHeight, sceneTop);
        float chestScale = ChestScale(canvasWidth);
        float chestX = centerX - ChestArtCenterX * chestScale;
        float chestY = mouthY - MouthOffsetY * chestScale;
        float bannerScale = BannerScale(canvasWidth, bannerWidth);
        float emptyCenterX = centerX - Math.Max(0f, emptyWidth) * EmptyScale * 0.5f;
        float fightCenterX = centerX - Math.Max(0f, fightWidth) * FightScale * 0.5f;
        return new TreasureScene(
            sceneTop,
            mouthY,
            chestScale,
            chestX,
            chestY,
            chestX + MouthOffsetX * chestScale,
            mouthY,
            chestX + FloorOffsetX * chestScale,
            chestY + FloorOffsetY * chestScale,
            bannerScale,
            centerX - Math.Max(0f, bannerWidth) * bannerScale * 0.5f,
            bannerTop,
            emptyCenterX,
            mouthY - EmptyDrop,
            EmptyScale,
            fightCenterX,
            sceneTop + FightDrop,
            FightScale);
    }
}
