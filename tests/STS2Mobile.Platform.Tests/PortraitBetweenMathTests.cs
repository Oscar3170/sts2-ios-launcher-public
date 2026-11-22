namespace STS2Mobile.Platform.Tests;

public class PortraitBetweenMathTests
{
    [Fact]
    public void MapScrollRangeBringsBossUnderTopBarAndStartAboveBottomEdge()
    {
        MapScrollRange range = PortraitBetweenMath.MapScrollRange(bossTop: -806f, startBottom: 2300f, visibleTop: 450f, visibleBottom: 2200f);
        Assert.True(range.Min < range.Max);
        Assert.Equal(450f, range.Max + -806f, 3);
        Assert.Equal(2200f, range.Min + 2300f, 3);
    }

    [Fact]
    public void MapScrollRangeCollapsesToCentreWhenWholeMapFits()
    {
        MapScrollRange range = PortraitBetweenMath.MapScrollRange(bossTop: 0f, startBottom: 1000f, visibleTop: 400f, visibleBottom: 2200f);
        Assert.Equal(range.Min, range.Max);
        Assert.Equal(800f, range.Max, 3);
    }

    [Fact]
    public void MapScrollClampKeepsGameTargetsInsidePortraitRange()
    {
        var range = new MapScrollRange(-250f, 560f);
        Assert.Equal(-250f, PortraitBetweenMath.ClampMapScroll(-600f, range));
        Assert.Equal(560f, PortraitBetweenMath.ClampMapScroll(1800f, range));
        Assert.Equal(100f, PortraitBetweenMath.ClampMapScroll(100f, range));
    }

    [Theory]
    [InlineData(-5f, 990f, 1080f, 47.5f)]
    [InlineData(40f, 1040f, 1080f, 0f)]
    [InlineData(-400f, 200f, 1080f, 120f)]
    [InlineData(900f, 1500f, 1080f, -120f)]
    public void MapCentreShiftCentresTheRoomPointsAndStaysBounded(float left, float right, float canvas, float expected)
    {
        Assert.Equal(expected, PortraitBetweenMath.MapCentreShift(left, right, canvas), 3);
    }

    [Theory]
    [InlineData(1080f, 0f)]
    [InlineData(2337f, 628.5f)]
    public void MapBackgroundShiftFollowsCanvasCentre(float canvas, float expected)
    {
        Assert.Equal(expected, PortraitBetweenMath.MapBackgroundShift(canvas), 3);
    }

    [Theory]
    [InlineData(1080f, 2340f, -420f, 630f)]
    [InlineData(1080f, 1920f, -420f, 420f)]
    [InlineData(1920f, 1080f, 0f, 0f)]
    public void MapLayerOffsetCentresTheAuthoredCanvas(float width, float height, float expectedX, float expectedY)
    {
        Assert.Equal(expectedX, PortraitBetweenMath.MapLayerOffsetX(width), 3);
        Assert.Equal(expectedY, PortraitBetweenMath.MapLayerOffsetY(height), 3);
    }

    [Fact]
    public void WidenedMapBackgroundAndNativeDrawingRepositionAgreeWithTheLayerOffset()
    {
        // The portrait layout widens MapBg by MaxMapCentreShift on both sides and moves it left by that much; the game's
        // own RepositionBasedOnBackground then centres the 1920-wide drawing layer on it.
        const float widen = PortraitBetweenMath.MaxMapCentreShift;
        const float canvasWidth = 1080f;
        float backgroundLeft = -widen;
        float backgroundWidth = canvasWidth + 2f * widen;
        float native = PortraitBetweenMath.MapDrawingsOffsetX(
            backgroundLeft, backgroundWidth, PortraitBetweenMath.DesktopCanvasWidth);
        Assert.Equal(PortraitBetweenMath.MapLayerOffsetX(canvasWidth), native, 3);
        // The points container is centre-anchored too, so a point keeps this very net coordinate.
        Assert.Equal(-420f, native, 3);
    }

    [Fact]
    public void NativeMapLegendGrowsFromTheReferencePointsSize()
    {
        // 2.3 at 1680 is 4.48 units per point; the launcher's 1080 canvas is 2.88, so the same legend is ~1.48.
        Assert.Equal(1.478571f, PortraitBetweenMath.MapLegendScale, 3);
        Assert.InRange(PortraitBetweenMath.MapLegendScale, 1.46f, 1.5f);
    }

    [Theory]
    [InlineData(1080f)]
    public void MapPaperScaleLeavesTheTornEdgesInsideTheCanvas(float canvasWidth)
    {
        float scale = PortraitBetweenMath.MapPaperScale(canvasWidth);
        float paper = PortraitBetweenMath.MapPaperTextureWidth * scale;
        Assert.Equal(canvasWidth - 2f * PortraitBetweenMath.MapPaperSideMargin, paper, 2);
        Assert.Equal(PortraitBetweenMath.MapPaperSideMargin, (canvasWidth - paper) * 0.5f, 2);
        Assert.True(scale is > 0.75f and < 1f);
    }

    [Fact]
    public void FourRewardCardsFormTwoByTwoGridAroundCentre()
    {
        var slots = PortraitBetweenMath.RewardSlots(4, cardWidth: 300f, cardHeight: 420f, gapX: 40f, gapY: 30f);
        Assert.Equal(4, slots.Count);
        Assert.Equal(new GridSlot(-170f, -225f), slots[0]);
        Assert.Equal(new GridSlot(170f, -225f), slots[1]);
        Assert.Equal(new GridSlot(-170f, 225f), slots[2]);
        Assert.Equal(new GridSlot(170f, 225f), slots[3]);
    }

    [Fact]
    public void ThreeRewardCardsArePairAboveCentredThird()
    {
        var slots = PortraitBetweenMath.RewardSlots(3, cardWidth: 300f, cardHeight: 420f, gapX: 40f, gapY: 30f);
        Assert.Equal(new GridSlot(-170f, -225f), slots[0]);
        Assert.Equal(new GridSlot(170f, -225f), slots[1]);
        Assert.Equal(new GridSlot(0f, 225f), slots[2]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OneOrTwoRewardCardsStayOnOneRow(int count)
    {
        var slots = PortraitBetweenMath.RewardSlots(count, cardWidth: 300f, cardHeight: 420f, gapX: 40f, gapY: 30f);
        Assert.All(slots, s => Assert.Equal(0f, s.CenterY));
        Assert.Equal(0f, slots[0].CenterX + slots[^1].CenterX, 3);
    }

    [Fact]
    public void FiveRewardCardsUseRowsOfTwo()
    {
        var slots = PortraitBetweenMath.RewardSlots(5, cardWidth: 300f, cardHeight: 420f, gapX: 40f, gapY: 30f);
        Assert.Equal(3, slots.Select(s => s.CenterY).Distinct().Count());
        Assert.Equal(0f, slots[4].CenterX);
    }

    [Fact]
    public void NoRewardCardsGiveNoSlots()
    {
        Assert.Empty(PortraitBetweenMath.RewardSlots(0, 300f, 420f, 40f, 30f));
    }

    [Fact]
    public void PortraitDeckGridFitsThreeColumnsInPortraitWidth()
    {
        CardGridColumns grid = PortraitBetweenMath.GridColumns(availableWidth: 1000f, cardWidth: 300f, minGap: 24f, minColumns: 3, maxScale: 1f);
        Assert.Equal(3, grid.Columns);
        Assert.True(grid.CardScale <= 1f);
        Assert.Equal(1000f, grid.Columns * 300f * grid.CardScale + (grid.Columns - 1) * grid.Gap, 2);
    }

    [Fact]
    public void GridShrinksCardsWhenMinimumColumnsDoNotFit()
    {
        CardGridColumns grid = PortraitBetweenMath.GridColumns(availableWidth: 600f, cardWidth: 300f, minGap: 20f, minColumns: 3, maxScale: 1f);
        Assert.Equal(3, grid.Columns);
        Assert.Equal((600f - 40f) / 900f, grid.CardScale, 4);
    }

    [Fact]
    public void LandscapeWidthGivesMoreColumns()
    {
        CardGridColumns grid = PortraitBetweenMath.GridColumns(availableWidth: 1800f, cardWidth: 300f, minGap: 24f, minColumns: 3, maxScale: 1f);
        Assert.Equal(5, grid.Columns);
        Assert.Equal(1f, grid.CardScale);
    }

    [Fact]
    public void ShopPanelPivotPutsContentUnderTopBarWhenOpenAndOffScreenWhenClosed()
    {
        const float scale = 0.815f;
        float pivot = PortraitBetweenMath.PivotForVisualTop(openY: 80f, contentTop: 150f, visualTop: 280f, scale);
        float VisualY(float positionY, float localY) => positionY + pivot + (localY - pivot) * scale;
        Assert.Equal(280f, VisualY(80f, 150f), 3);
        Assert.True(VisualY(-1000f, 978f) < 0f);
    }

    [Fact]
    public void PivotIsZeroAtFullScale()
    {
        Assert.Equal(0f, PortraitBetweenMath.PivotForVisualTop(80f, 150f, 280f, 1f));
    }

    [Theory]
    [InlineData(1920f, 1040f, 1040f / 1920f)]
    [InlineData(800f, 1040f, 1f)]
    [InlineData(0f, 1040f, 1f)]
    public void FitScaleNeverEnlarges(float content, float available, float expected)
    {
        Assert.Equal(expected, PortraitBetweenMath.FitScale(content, available), 4);
    }

    // Canvas 1080 wide; cap 162 and stone header bottom 390 for both test shapes; the game's shown Y is 764/1080 of the
    // height; the grown action is 127 units tall.
    private const float HeaderBottom = 390f;
    private const float ActionHeight = 127f;

    [Fact]
    public void LootWindowIsThreeQuartersOfTheCanvasAcross()
    {
        float scale = PortraitBetweenMath.LootScale(1080f);
        Assert.Equal(1008f / 654f, scale, 4);
        Assert.Equal(526f * scale, PortraitBetweenMath.LootWindow(1080f, HeaderBottom, 1000f, 526f, 640f).Width, 3);
        Assert.True(526f * scale >= 1080f * 0.74f);
    }

    [Fact]
    public void LootWindowKeepsItsNativeHeightAtTheTallShape()
    {
        float laneTop = PortraitBetweenMath.ActionLaneTop(false, 2340f, ActionHeight);
        Assert.Equal(PortraitBetweenMath.ProceedShowY(2340f), laneTop, 3);
        LootWindowFit fit = PortraitBetweenMath.LootWindow(1080f, HeaderBottom, laneTop, 526f, 640f);
        Assert.Equal(640f, fit.WindowHeight, 3);
        Assert.Equal(640f * fit.Scale, fit.Height, 3);
        // Banner top (the window's top less the overhang) below the header and the panel's bottom above the lane.
        Assert.True(fit.Y - PortraitBetweenMath.LootBannerOverhang * fit.Scale >= HeaderBottom);
        Assert.True(fit.Y + fit.Height <= laneTop - PortraitBetweenMath.LootLaneGap + 3f);
    }

    [Fact]
    public void LootWindowShrinksAtTheSixteenByNineShapeAndClearsTheLane()
    {
        float laneTop = PortraitBetweenMath.ActionLaneTop(false, 1920f, ActionHeight);
        LootWindowFit fit = PortraitBetweenMath.LootWindow(1080f, HeaderBottom, laneTop, 526f, 640f);
        Assert.True(fit.WindowHeight < 640f);
        Assert.Equal(569.05f, fit.WindowHeight, 1);
        Assert.Equal(457.16f, fit.Y, 2);
        // Harness floor: the panel's bottom is clear of the lane and its top is under the header.
        Assert.True(fit.Y + fit.Height <= laneTop - 8f);
        Assert.True(fit.Y >= HeaderBottom);
        Assert.True(fit.Y + fit.Height <= 1920f);
        // The mask keeps its row: two and a half rows still show.
        Assert.True((fit.WindowHeight - 139f) * fit.Scale >= 2.8f * PortraitBetweenMath.LootRowMinHeight * fit.Scale);
    }

    [Fact]
    public void BlitzLaneReservesOneBandForSkipAndProceed()
    {
        Assert.Equal(1200f, PortraitBetweenMath.BlitzLaneCentreY(1920f), 3);
        Assert.Equal(1462.5f, PortraitBetweenMath.BlitzLaneCentreY(2340f), 3);
        float top = PortraitBetweenMath.ActionLaneTop(true, 1920f, ActionHeight);
        Assert.Equal(1136.5f, top, 3);
        LootWindowFit fit = PortraitBetweenMath.LootWindow(1080f, HeaderBottom, top, 526f, 640f);
        Assert.Equal(425.16f, fit.WindowHeight, 1);
        Assert.True(fit.Y + fit.Height <= top - 8f);
        Assert.True((fit.WindowHeight - 139f) * fit.Scale >= 2.8f * PortraitBetweenMath.LootRowMinHeight * fit.Scale);
    }

    [Fact]
    public void GameLaneCentreHangsFromTheShownYForTheTallShape()
    {
        Assert.Equal(1655.33f + ActionHeight / 2f, PortraitBetweenMath.ActionLaneCentreY(false, 2340f, ActionHeight), 2);
        Assert.Equal(PortraitBetweenMath.ProceedShowY(2340f), PortraitBetweenMath.ActionLaneTop(false, 2340f, ActionHeight), 2);
        Assert.Equal(1080f - PortraitBetweenMath.ActionRightMargin, 1049f, 3);
    }

    [Fact]
    public void TreasureActionHangsFromTheCanvasFooter()
    {
        Assert.Equal(2250.79f, PortraitBetweenMath.ActionFooterCentreY(2340f, ActionHeight), 2);
        Assert.Equal(1830.79f, PortraitBetweenMath.ActionFooterCentreY(1920f, ActionHeight), 2);
        Assert.Equal(25.71f, 1920f - (PortraitBetweenMath.ActionFooterCentreY(1920f, ActionHeight) + ActionHeight / 2f), 2);
    }

    [Theory]
    [InlineData(394f, 286f, 543f)]
    [InlineData(292f, 286f, 441f)]
    [InlineData(496f, 430f, 501f)]
    [InlineData(88f, 286f, 0f)]
    [InlineData(496f, 501f, 0f)]
    [InlineData(394f, 430f, 0f)]
    public void LootListIsMadeScrollableOnlyWhenItsRowsOverflowTheMask(float content, float mask, float expected)
    {
        Assert.Equal(expected, PortraitBetweenMath.LootScrollMinimumHeight(content, mask), 2);
    }

    [Fact]
    public void LootScrollMinimumLetsTheDragReachTheLastRow()
    {
        // The game stops a drag at 35 - height + 400; that position must bring the content's bottom to the mask's bottom.
        const float content = 394f, mask = 286f;
        float height = PortraitBetweenMath.LootScrollMinimumHeight(content, mask);
        float limit = PortraitBetweenMath.LootContainerTop - height + PortraitBetweenMath.LootScrollRange;
        Assert.Equal(mask, limit + content, 2);
    }

    [Theory]
    [InlineData(1920f, 812f, 1683f)]
    [InlineData(2340f, 814f, 2103f)]
    public void CardRewardGridKeepsCardsAtLeastTwoHundredAndEightyNineWide(float height, float top, float bottom)
    {
        CardGridFit fit = PortraitBetweenMath.CardGrid(
            3, 1080f, top, bottom, 300f, 422f, 48f, 32f, 1.5f, 30f);
        Assert.True(300f * fit.Scale >= 289f, $"scale {fit.Scale:F3} gives {300f * fit.Scale:F1}");
        Assert.Equal(2f * 422f * fit.Scale + 32f, fit.BlockHeight, 1);
        Assert.True(top + fit.BlockHeight <= bottom + 0.01f);
        Assert.True(top + fit.BlockHeight + 30f + ActionHeight <= height);
    }

    [Theory]
    [InlineData(1920f, 812f, 1667.2f)]
    [InlineData(2340f, 814f, 2087.2f)]
    public void ChooseACardGridKeepsCardsAtLeastTwoHundredAndEightyNineWide(float height, float top, float bottom)
    {
        CardGridFit fit = PortraitBetweenMath.CardGrid(
            3, 1080f, top, bottom, 300f, 422f, 48f, 32f, 1.5f, 30f);
        Assert.True(300f * fit.Scale >= 289f, $"scale {fit.Scale:F3} gives {300f * fit.Scale:F1}");
        Assert.Equal(height == 1920f ? 0.97536f : 1.47062f, fit.Scale, 3);
    }

    [Fact]
    public void CardGridHonoursItsWidthWhenTheBandAllowsTheCap()
    {
        CardGridFit twoCards = PortraitBetweenMath.CardGrid(
            2, 1080f, 400f, 2000f, 300f, 422f, 48f, 32f, 1.5f, 30f);
        Assert.Equal(1.5f, twoCards.Scale, 3);
        Assert.Equal(422f * 1.5f, twoCards.BlockHeight, 2);
        Assert.Equal(Math.Min(1.5f, (1080f - 60f - 48f) / 600f), twoCards.Scale, 3);
        // Four cards are two rows and keep the row gap between them.
        CardGridFit fourCards = PortraitBetweenMath.CardGrid(
            4, 1080f, 400f, 2000f, 300f, 422f, 48f, 32f, 1.5f, 30f);
        Assert.Equal(2f * 422f * 1.5f + 32f, fourCards.BlockHeight, 2);
    }
}
