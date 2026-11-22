using System;
using System.Collections.Generic;

namespace STS2Mobile.Platform;

public readonly record struct MapScrollRange(float Min, float Max);

public readonly record struct GridSlot(float CenterX, float CenterY);

public readonly record struct CardGridColumns(int Columns, float CardScale, float Gap);

/// <summary>A loot window's drawn rectangle and scale, in canvas units.</summary>
public readonly record struct LootWindowFit(float Scale, float X, float Y, float Width, float Height, float WindowHeight);

/// <summary>A card grid's scale and the height of the whole block, relative to its first row's top.</summary>
public readonly record struct CardGridFit(float Scale, float BlockHeight);

/// <summary>
/// Pure layout math for portrait between-fight screens: the Spire Map's scroll range, the 2 x 2 card reward grid,
/// the 3-column deck grid, the loot window and the action lane, and the fit scale used to keep desktop panels inside the
/// 1080-wide portrait canvas.
/// </summary>
public static class PortraitBetweenMath
{
    /// <summary>Desktop design height of the map scene's canvas; its points are laid out around the canvas centre.</summary>
    public const float DesktopCanvasHeight = 1080f;

    /// <summary>Desktop design width of the map scene's canvas (NGame's dev resolution); point positions assume it.</summary>
    public const float DesktopCanvasWidth = 1920f;

    /// <summary>
    /// How far the map's fixed-size layers move in a portrait canvas. NMapScreen places points with
    /// <c>position + (containerSize - 1920x1080) / 2</c> because the points container is centre-anchored, and
    /// NMapDrawings.RepositionBasedOnBackground centres the drawing layer on MapBg. Widening MapBg by the same amount
    /// on both sides and moving it left by half that (as the portrait layout does) leaves the native formula equal to
    /// this offset, so a stroke keeps the net coordinate of the point under the finger and scrolls with the graph.
    /// </summary>
    public static float MapLayerOffsetX(float canvasWidth) => (canvasWidth - DesktopCanvasWidth) * 0.5f;

    /// <summary>The vertical half of <see cref="MapLayerOffsetX"/>'s centring: the canvas's extra height, half above.</summary>
    public static float MapLayerOffsetY(float canvasHeight) => (canvasHeight - DesktopCanvasHeight) * 0.5f;

    /// <summary>NMapDrawings.RepositionBasedOnBackground: the drawing layer is centred on the map background.</summary>
    public static float MapDrawingsOffsetX(float backgroundLeft, float backgroundWidth, float drawingsWidth) =>
        backgroundLeft + (backgroundWidth - drawingsWidth) * 0.5f;

    /// <summary>
    /// The native map legend is drawn at 2.3 on the reference phone layout's 1680-wide canvas; on the 1080-wide canvas the
    /// same size in points is 2.3 * 1080 / 1680 (about 1.48), which makes the legend's 24-42 unit text 13-22 points.
    /// </summary>
    public const float MapLegendScale = 2.3f * 1080f / 1680f;

    /// <summary>The act's map art is a 1920-wide sheet; the parchment body measures about 1260 units across.</summary>
    public const float MapPaperTextureWidth = 1260f;

    /// <summary>How far the parchment's torn side edges should sit inside the portrait canvas.</summary>
    public const float MapPaperSideMargin = 28f;

    /// <summary>
    /// Horizontal scale for the map background so the torn-scroll sheet is framed inside the canvas with
    /// <see cref="MapPaperSideMargin"/> on each side. Only the paper is scaled (the point columns keep their own centring),
    /// and the vertical scale is left at 1 so the sheet still covers every scroll position.
    /// </summary>
    public static float MapPaperScale(float canvasWidth) =>
        Math.Clamp((canvasWidth - 2f * MapPaperSideMargin) / MapPaperTextureWidth, 0.5f, 1f);

    /// <summary>
    /// How far the map container may scroll in portrait so the boss medallion can be brought just under the top bar and the
    /// starting point just above the bottom edge. <paramref name="bossTop"/> and <paramref name="startBottom"/> are in the map
    /// container's own coordinates, so a point shows at <c>scroll + local</c>. When the whole map fits in the band, both ends
    /// collapse to the scroll that centres it.
    /// </summary>
    public static MapScrollRange MapScrollRange(float bossTop, float startBottom, float visibleTop, float visibleBottom)
    {
        float max = visibleTop - bossTop;
        float min = visibleBottom - startBottom;
        if (min > max)
        {
            float mid = (min + max) / 2f;
            return new MapScrollRange(mid, mid);
        }
        return new MapScrollRange(min, max);
    }

    /// <summary>The scroll the game would aim for, clamped into the portrait range.</summary>
    public static float ClampMapScroll(float scroll, MapScrollRange range) => Math.Clamp(scroll, range.Min, range.Max);

    /// <summary>The most the map is ever moved sideways to centre it; the background is widened by this much on both sides.</summary>
    public const float MaxMapCentreShift = 120f;

    /// <summary>
    /// How far right the map must move so its room points sit centred in the canvas. The game lays the columns out left of
    /// centre, leaving room for the legend panel on the right; in portrait the legend is a button, and the left column would
    /// touch or pass the screen edge. <paramref name="pointsLeft"/> and <paramref name="pointsRight"/> are the outer edges
    /// of the room points in the map container's own coordinates.
    /// </summary>
    public static float MapCentreShift(float pointsLeft, float pointsRight, float canvasWidth) =>
        Math.Clamp((canvasWidth - (pointsLeft + pointsRight)) / 2f, -MaxMapCentreShift, MaxMapCentreShift);

    /// <summary>
    /// How far the desktop map background (anchored to the top) must move down so it stays behind points that are laid out
    /// around the taller portrait canvas centre.
    /// </summary>
    public static float MapBackgroundShift(float canvasHeight) => (canvasHeight - DesktopCanvasHeight) / 2f;

    /// <summary>
    /// Centres of card reward slots in portrait: one row for one or two cards, 2 + 1 for three (the third centred below),
    /// 2 x 2 for four, and rows of two beyond that. Slots are given relative to the centre of the whole block.
    /// </summary>
    public static IReadOnlyList<GridSlot> RewardSlots(int count, float cardWidth, float cardHeight, float gapX, float gapY)
    {
        var slots = new List<GridSlot>(Math.Max(count, 0));
        if (count <= 0) return slots;
        int rows = count <= 2 ? 1 : (count + 1) / 2;
        float stepX = cardWidth + gapX;
        float stepY = cardHeight + gapY;
        float firstY = -(rows - 1) * stepY / 2f;
        for (int i = 0; i < count; i++)
        {
            int row = count <= 2 ? 0 : i / 2;
            int inRow = count <= 2 ? count : Math.Min(2, count - row * 2);
            int col = count <= 2 ? i : i % 2;
            float rowStartX = -(inRow - 1) * stepX / 2f;
            slots.Add(new GridSlot(rowStartX + col * stepX, firstY + row * stepY));
        }
        return slots;
    }

    /// <summary>
    /// Columns for a card grid in a given width: as many cards of <paramref name="cardWidth"/> (at
    /// <paramref name="maxScale"/>) as fit with <paramref name="minGap"/> between them, at least <paramref name="minColumns"/>,
    /// shrinking the cards when even that many do not fit.
    /// </summary>
    public static CardGridColumns GridColumns(float availableWidth, float cardWidth, float minGap, int minColumns, float maxScale)
    {
        if (availableWidth <= 0f || cardWidth <= 0f) return new CardGridColumns(Math.Max(minColumns, 1), maxScale, minGap);
        int columns = Math.Max(minColumns, (int)MathF.Floor((availableWidth + minGap) / (cardWidth * maxScale + minGap)));
        columns = Math.Max(columns, 1);
        float scale = Math.Min(maxScale, (availableWidth - (columns - 1) * minGap) / (columns * cardWidth));
        float gap = columns > 1 ? (availableWidth - columns * cardWidth * scale) / (columns - 1) : 0f;
        return new CardGridColumns(columns, scale, gap);
    }

    /// <summary>
    /// Pivot height for a panel the game slides to a fixed <paramref name="openY"/>, so that scaled by <paramref name="scale"/> its
    /// content starting at <paramref name="contentTop"/> (panel-local) shows at <paramref name="visualTop"/> on screen. A control
    /// draws local y at <c>position.y + pivot + (y - pivot) * scale</c>.
    /// </summary>
    public static float PivotForVisualTop(float openY, float contentTop, float visualTop, float scale)
    {
        if (Math.Abs(1f - scale) < 0.0001f) return 0f;
        return (visualTop - openY - contentTop * scale) / (1f - scale);
    }

    /// <summary>Scale that fits a desktop panel of <paramref name="contentWidth"/> inside <paramref name="availableWidth"/>, never above 1.</summary>
    public static float FitScale(float contentWidth, float availableWidth, float maxScale = 1f)
    {
        if (contentWidth <= 0f || availableWidth <= 0f) return maxScale;
        return Math.Min(maxScale, availableWidth / contentWidth);
    }

    // ---- Loot window and the action lane --------------------------------------------------------------------------------
    //
    // The loot window is 526 wide and 640 tall and its banner overhangs its top by 28; the reference desktop layout fits the
    // banner plus a 112-unit margin into the phone's width in its own 1680-wide units. On this 1080-wide canvas the same
    // margins are 112 * 1080 / 1680 = 72 and the fit width (the 652-wide banner plus a unit either side) stays 654, so the
    // panel is 75 % of the canvas across and its banner row 93 %. Rows are 88 tall inside the window (was 86), so the first
    // row's 44 points stay readable at every shape.

    /// <summary>The reference layout's side margin (112 of its 1680-wide units) in canvas units.</summary>
    public const float LootWidthInset = 72f;

    /// <summary>The banner's width plus a unit either side, which the width fit is measured against.</summary>
    public const float LootFitWidth = 654f;

    /// <summary>The loot banner overhangs the window's top by this much (inside the window, before its scale).</summary>
    public const float LootBannerOverhang = 28f;

    /// <summary>Gap between the stone header and the banner's top.</summary>
    public const float LootTopGap = 24f;

    /// <summary>Gap between the action lane's top and the window's bottom.</summary>
    public const float LootLaneGap = 24f;

    /// <summary>Unit a loot row keeps between it and the mask's edges (inside the window).</summary>
    public const float LootRowMinHeight = 88f;

    /// <summary>The action's right edge keeps this margin from the canvas's right edge (48 of the reference's 1680 units).</summary>
    public const float ActionRightMargin = 31f;

    /// <summary>How far the action's bottom edge stands above the canvas's bottom edge when it hangs from the footer.</summary>
    public const float ActionFooterMargin = 25.71f;

    /// <summary>The Blitz run's single action lane, as a fraction of the canvas height.</summary>
    public const float BlitzLaneFraction = 0.625f;

    /// <summary>Width scale for the loot window: the banner and the reference's margins fit the canvas.</summary>
    public static float LootScale(float canvasWidth) =>
        Math.Max(0.2f, (canvasWidth - LootWidthInset) / LootFitWidth);

    /// <summary>
    /// The loot window in portrait: its banner starts at <paramref name="headerBottom"/> + <see cref="LootTopGap"/>, its
    /// bottom stays <see cref="LootLaneGap"/> above <paramref name="laneTop"/>, and the window shrinks in height (and the
    /// mask with it) rather than growing over the action. The window is centred between the two, less the banner overhang.
    /// </summary>
    public static LootWindowFit LootWindow(
        float canvasWidth, float headerBottom, float laneTop, float nativeWidth, float nativeHeight)
    {
        float scale = LootScale(canvasWidth);
        float top = headerBottom + LootTopGap;
        float bottom = Math.Max(laneTop - LootLaneGap, top + 1f);
        float room = (bottom - top) / scale;
        float height = Math.Min(nativeHeight, room - LootBannerOverhang);
        height = Math.Max(height, 120f);
        float centreY = (top + bottom + LootBannerOverhang * scale) * 0.5f;
        return new LootWindowFit(
            scale,
            (canvasWidth - nativeWidth * scale) * 0.5f,
            centreY - height * scale * 0.5f,
            nativeWidth * scale,
            height * scale,
            height);
    }

    /// <summary>The game's own shown position of the proceed button as a fraction of the canvas height (764 of 1080).</summary>
    public const float ProceedShowFraction = 764f / 1080f;

    /// <summary>Where the game slides the proceed button to, in canvas units (its top-left when the lane is the game's).</summary>
    public static float ProceedShowY(float canvasHeight) => canvasHeight * ProceedShowFraction;

    /// <summary>The Blitz run's action centre, which is shared by the loot screen's Skip and Proceed.</summary>
    public static float BlitzLaneCentreY(float canvasHeight) => canvasHeight * BlitzLaneFraction;

    /// <summary>
    /// Centre of the action lane on a screen. Without a Blitz lane the button keeps the game's own shown Y as its top edge,
    /// so growing it for a finger only extends it downwards and the game's slide never moves its centre; in Blitz both the
    /// Skip and the Proceed label hang from one centre at 0.625 of the canvas height.
    /// </summary>
    public static float ActionLaneCentreY(bool blitz, float canvasHeight, float actionHeight) =>
        blitz ? BlitzLaneCentreY(canvasHeight) : ProceedShowY(canvasHeight) + actionHeight * 0.5f;

    /// <summary>Top edge the action must take so that its centre is the lane's.</summary>
    public static float ActionLaneTop(bool blitz, float canvasHeight, float actionHeight) =>
        ActionLaneCentreY(blitz, canvasHeight, actionHeight) - actionHeight * 0.5f;

    /// <summary>The footer lane: the action hangs from the canvas's bottom edge, clear of the scene behind it (the treasure room's
    /// chest and its relic holders fill the middle of the screen, and the game's own shown Y would lie across them).
    /// </summary>
    public static float ActionFooterCentreY(float canvasHeight, float actionHeight) =>
        canvasHeight - ActionFooterMargin - actionHeight * 0.5f;

    /// <summary>The loot list's own top offset inside its mask (the game's container starts 35 below it).</summary>
    public const float LootContainerTop = 35f;

    /// <summary>The game's scroll window: its list can scroll once it is this tall, and its limit is top - height + this.</summary>
    public const float LootScrollRange = 400f;

    /// <summary>
    /// Minimum height the loot list needs when it overflows a shorter mask. The game's scroll engages at a 400-unit list and
    /// stops at <c>35 - height + 400</c>, so a list whose rows overflow the mask is made tall enough for the drag to bring its
    /// last row's bottom to the mask's bottom. Returns zero when everything fits (the game's own height is kept).
    /// </summary>
    public static float LootScrollMinimumHeight(float contentHeight, float maskHeight)
    {
        if (contentHeight <= maskHeight) return 0f;
        return contentHeight + Math.Max(0f, LootContainerTop + LootScrollRange - maskHeight);
    }

    /// <summary>
    /// Scale for a card reward grid (two to a row, an odd card centred below) in the band between
    /// <paramref name="blockTop"/> and <paramref name="blockBottom"/>, with a margin either side of the canvas. Both limits
    /// are honoured; a phone's smallest card is then 289 units wide (100 points at the 1680-wide reference).
    /// </summary>
    public static CardGridFit CardGrid(
        int count,
        float canvasWidth,
        float blockTop,
        float blockBottom,
        float cardWidth,
        float cardHeight,
        float gapX,
        float gapY,
        float maxScale,
        float sideMargin)
    {
        if (count <= 0) return new CardGridFit(maxScale, 0f);
        int columns = Math.Min(count, 2);
        int rows = (count + 1) / 2;
        float byWidth = (canvasWidth - 2f * sideMargin - (columns - 1) * gapX) / (columns * cardWidth);
        float byHeight = (blockBottom - blockTop - (rows - 1) * gapY) / (rows * cardHeight);
        float scale = Math.Clamp(Math.Min(maxScale, Math.Min(byWidth, byHeight)), 0.2f, maxScale);
        return new CardGridFit(scale, rows * cardHeight * scale + (rows - 1) * gapY);
    }
}
