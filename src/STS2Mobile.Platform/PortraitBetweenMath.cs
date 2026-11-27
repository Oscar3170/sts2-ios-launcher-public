using System;
using System.Collections.Generic;

namespace STS2Mobile.Platform;

public readonly record struct MapScrollRange(float Min, float Max);

public readonly record struct GridSlot(float CenterX, float CenterY);

public readonly record struct CardGridColumns(int Columns, float CardScale, float Gap);

/// <summary>A loot window's drawn rectangle and scale, in canvas units.</summary>
public readonly record struct LootWindowFit(float Scale, float X, float Y, float Width, float Height, float WindowHeight);

/// <summary>A card grid's scale, the height of the whole block, its arrangement, and the gap between its columns.</summary>
public readonly record struct CardGridFit(float Scale, float BlockHeight, int Rows, int Columns, float GapX);

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
    /// The one uniform scale for the whole map graph — the parchment sheet, the points, the paths, the free strokes, the
    /// marker and the points' hit pads are all its children. It makes the parchment body fit the canvas with
    /// <see cref="MapPaperSideMargin"/> on each side, so both torn edges stay visible over the act art.
    /// </summary>
    public static float MapGraphScale(float canvasWidth) =>
        Math.Clamp((canvasWidth - 2f * MapPaperSideMargin) / MapPaperTextureWidth, 0.5f, 1f);

    /// <summary>The parchment body's width on the portrait canvas: the 1260-unit sheet, scaled with the graph.</summary>
    public static float MapPaperWidth(float canvasWidth) => MapPaperTextureWidth * MapGraphScale(canvasWidth);

    /// <summary>The parchment body's left edge on the portrait canvas (the graph is framed around it).</summary>
    public static float MapPaperLeft(float canvasWidth) => (canvasWidth - MapPaperWidth(canvasWidth)) * 0.5f;

    /// <summary>The parchment body's right edge on the portrait canvas.</summary>
    public static float MapPaperRight(float canvasWidth) => MapPaperLeft(canvasWidth) + MapPaperWidth(canvasWidth);

    /// <summary>
    /// How far every drawn node icon must stay from the parchment's torn side edges: the reference's phone layout (1680
    /// units wide) fits the graph with 120 units of clearance, 60 a side; converted to the 1080 canvas that is 38.6.
    /// </summary>
    public const float MapNodePaperMargin = 60f * 1080f / 1680f;

    /// <summary>
    /// The native room icon's growth beyond its drawn rect. <c>NNormalMapPoint</c> pulses its icon container to a 1.2 base
    /// with a 0.25 swing (1.45 maximum) and its hover tween scales the icon to 1.45, so 1.45 covers both states.
    /// </summary>
    public const float MapNodeFocusGrowth = 1.45f;

    /// <summary>The left edge a fully decorated node icon may reach on the parchment, inset by the reference margin.</summary>
    public static float MapNodePaperLeft(float canvasWidth) => MapPaperLeft(canvasWidth) + MapNodePaperMargin;

    /// <summary>The right edge a fully decorated node icon may reach on the parchment, inset by the reference margin.</summary>
    public static float MapNodePaperRight(float canvasWidth) => MapPaperRight(canvasWidth) - MapNodePaperMargin;

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

    /// <summary>
    /// How far the map background is widened on both sides. Its tiles are 1080-wide slices of a 1920-wide sheet and cover
    /// their control; without the extra width each tile would crop 90 units off the parchment's torn edges.
    /// </summary>
    public const float MaxMapCentreShift = 120f;

    /// <summary>
    /// How far the desktop map background (anchored to the top) must move down so it stays behind points that are laid out
    /// around the taller portrait canvas centre.
    /// </summary>
    public static float MapBackgroundShift(float canvasHeight) => (canvasHeight - DesktopCanvasHeight) / 2f;

    /// <summary>
    /// Centres of card reward slots in portrait: one row for one or two cards, 2 + 1 for three (the third centred below),
    /// 2 x 2 for four, and rows of two beyond that. Slots are given relative to the centre of the whole block.
    /// </summary>
    public static IReadOnlyList<GridSlot> RewardSlots(int count, float cardWidth, float cardHeight, float gapX, float gapY) =>
        RewardSlots(count, Math.Min(Math.Max(count, 1), 2), cardWidth, cardHeight, gapX, gapY);

    /// <summary>Slots for an explicit number of columns (one column is a single row).</summary>
    public static IReadOnlyList<GridSlot> RewardSlots(
        int count, int columns, float cardWidth, float cardHeight, float gapX, float gapY)
    {
        var slots = new List<GridSlot>(Math.Max(count, 0));
        if (count <= 0) return slots;
        columns = Math.Max(1, Math.Min(columns, count));
        int rows = (count + columns - 1) / columns;
        float stepX = cardWidth + gapX;
        float stepY = cardHeight + gapY;
        float firstY = -(rows - 1) * stepY / 2f;
        for (int i = 0; i < count; i++)
        {
            int row = i / columns;
            int inRow = Math.Min(columns, count - row * columns);
            int col = i % columns;
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

    /// <summary>Gap between two loot rows (was the game's 10; the rows' labels need the extra room).</summary>
    public const float LootRowSeparation = 14f;

    /// <summary>The loot mask's insets inside the window, in the window's own units (the game's 526 x 640 panel).</summary>
    public const float LootMaskLeft = 26f;
    public const float LootMaskRight = 26f;
    public const float LootMaskTop = 103f;
    public const float LootMaskBottom = 36f;

    /// <summary>Room the mask keeps below the last row before its lower edge, in the window's own units.</summary>
    public const float LootContentPadding = 16f;

    /// <summary>
    /// The shortest window portrait draws, in the window's own units: a single reward still sits in a panel rather than a
    /// sliver under the banner.
    /// </summary>
    public const float LootWindowMinHeight = 360f;

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
    /// The loot window in portrait: its top is placed so the rows' block is centred on the canvas's middle (the banner
    /// overhang keeps it under the stone header), and the window then reaches down to <see cref="LootLaneGap"/> above
    /// <paramref name="laneTop"/>, as the game's panel reaches its Skip/Proceed, never taller than the game's own panel.
    /// When the panel cannot reach that far from the centred top, the window keeps the lane and moves down; when the rows
    /// are too many for one panel the window takes the room it has (the list scrolls).
    /// </summary>
    public static LootWindowFit LootWindow(
        float canvasWidth, float canvasHeight, float headerBottom, float laneTop, float nativeWidth, float nativeHeight,
        float contentHeight)
    {
        float scale = LootScale(canvasWidth);
        float minTop = headerBottom + LootTopGap + LootBannerOverhang * scale;
        float maxBottom = Math.Max(laneTop - LootLaneGap, minTop + 1f);
        float available = Math.Max(1f, (maxBottom - minTop) / scale);
        float needed = contentHeight + LootMaskTop + LootMaskBottom + LootContentPadding;
        float floor = Math.Clamp(Math.Min(Math.Max(needed, LootWindowMinHeight), available), Math.Min(120f, nativeHeight), nativeHeight);
        // The rows start at the mask's top plus the list's own offset and keep the window's scale; their middle is the
        // point that takes the canvas's middle.
        float rowsOffset = (LootMaskTop + LootContainerTop) * scale + contentHeight * scale * 0.5f;
        float y = Math.Clamp(
            canvasHeight * 0.5f - rowsOffset,
            minTop,
            Math.Max(minTop, maxBottom - floor * scale));
        float height = Math.Clamp((maxBottom - y) / scale, floor, nativeHeight);
        y = Math.Max(minTop, maxBottom - height * scale);
        return new LootWindowFit(
            scale,
            (canvasWidth - nativeWidth * scale) * 0.5f,
            y,
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
    /// How far a card grows past its laid-out edge when the game focuses it: focus sets the holder's scale to 1.0 from the
    /// grid's 0.8, so a card grows a quarter larger about its middle and half of that (an eighth of its height) reaches past
    /// each edge. A focused card must not draw over the banner, the skip/peek footer or the alternatives row.
    /// </summary>
    public const float CardFocusGrowth = 0.125f;

    /// <summary>
    /// The reference's card row for one to three cards: 0.92 of the canvas across with a 40-unit gap (40 of the reference's
    /// 1680-wide units is 25.71 here) and its cap of 1.85 converted the same way (1.1893), so a three-card row is 314 units
    /// (109 points) on the phone. Slots are one row, given relative to the block's centre.
    /// </summary>
    public const float CardRowWidthFraction = 0.92f;
    public const float CardRowGap = 40f * 1080f / 1680f;
    public const float CardRowMaxScale = 1.85f * 1080f / 1680f;

    /// <summary>
    /// One row for one to three cards, scaled like the reference's row and reserving <see cref="CardFocusGrowth"/> of a
    /// card's height above and below for the focused card. The band only limits very short canvases.
    /// </summary>
    public static CardGridFit CardRow(
        int count, float canvasWidth, float bandTop, float bandBottom, float cardWidth, float cardHeight)
    {
        int cards = Math.Max(count, 1);
        float byWidth = (canvasWidth * CardRowWidthFraction - (cards - 1) * CardRowGap) / (cards * cardWidth);
        float byHeight = (bandBottom - bandTop) / (cardHeight * (1f + 2f * CardFocusGrowth));
        float scale = Math.Clamp(Math.Min(CardRowMaxScale, Math.Min(byWidth, byHeight)), 0.2f, CardRowMaxScale);
        return new CardGridFit(scale, cardHeight * scale, 1, cards, CardRowGap);
    }

    /// <summary>
    /// Scale and arrangement for a card grid in the band between <paramref name="bandTop"/> and <paramref name="bandBottom"/>,
    /// with a margin either side of the canvas, reserving <see cref="CardFocusGrowth"/> of a card's height above and below the
    /// block for the focused card. Two to a row is preferred; when that cannot give a card at least
    /// <paramref name="minScale"/> (the phone's floor of 289 units), three cards take one row instead, which is larger on the
    /// shorter canvas. Larger sets cannot take one row; they add a third (then a fourth or fifth) column while that keeps the
    /// cards closer to the floor, so a five-card choice is two rows of three and two rather than three rows squeezed onto the
    /// tall screen. The card screens use <see cref="CardRow"/> for one to three cards; this rule is kept for four or more and
    /// for the bundle choice.
    /// </summary>
    public static CardGridFit CardGrid(
        int count,
        float canvasWidth,
        float bandTop,
        float bandBottom,
        float cardWidth,
        float cardHeight,
        float gapX,
        float gapY,
        float maxScale,
        float sideMargin,
        float minScale)
    {
        if (count <= 0) return new CardGridFit(maxScale, 0f, 0, 0, gapX);
        CardGridFit Fit(int columns)
        {
            int rows = (count + columns - 1) / columns;
            float byWidth = (canvasWidth - 2f * sideMargin - (columns - 1) * gapX) / (columns * cardWidth);
            float bandHeight = bandBottom - bandTop - (rows - 1) * gapY;
            float byHeight = bandHeight / (cardHeight * (rows + 2f * CardFocusGrowth));
            float scale = Math.Clamp(Math.Min(maxScale, Math.Min(byWidth, byHeight)), 0.2f, maxScale);
            return new CardGridFit(scale, rows * cardHeight * scale + (rows - 1) * gapY, rows, columns, gapX);
        }
        CardGridFit grid = Fit(Math.Min(count, 2));
        if (grid.Scale >= minScale) return grid;
        if (count <= 3)
        {
            // Three cards: one row is wider than half as wide, and it is the taller canvas that makes the pair too short.
            CardGridFit singleRow = Fit(count);
            return singleRow.Scale > grid.Scale ? singleRow : grid;
        }
        // Four or more: the pair's rows cannot hold the floor, so widen the grid a column at a time and keep the largest
        // arrangement (ties keep the narrower one, so the pair still wins when widening does not help).
        CardGridFit best = grid;
        for (int columns = 3; columns <= Math.Min(count, 5); columns++)
        {
            CardGridFit candidate = Fit(columns);
            if (candidate.Scale > best.Scale + 0.0001f) best = candidate;
        }
        return best;
    }
}
