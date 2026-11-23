namespace STS2Mobile.Platform.Tests;

public class PortraitGridMathTests
{
    private const float NarrowHeight = 1920f;   // 9:16, 1080 wide
    private const float TallHeight = 2340f;     // 9:19.5, 1080 wide

    [Theory]
    [InlineData(1080f)]
    [InlineData(1044f)]
    [InlineData(1200f)]
    public void CardSizeGivesExactlyThreeNativeColumns(float canvasWidth)
    {
        float scroll = PortraitGridMath.ScrollWidth(canvasWidth);
        float card = PortraitGridMath.CardWidthFor(scroll);
        Assert.Equal(PortraitGridMath.Columns, PortraitGridMath.ColumnsFor(scroll, card));
        // The game's own getter, in float as it runs: it must not sit a rounding error under three and truncate to two.
        float quotient = (scroll + PortraitGridMath.CardPadding) / (card + PortraitGridMath.CardPadding);
        Assert.True(quotient >= 3.005f, $"quotient {quotient:F6} for a {card:F3} unit card in {scroll:F3}");
    }

    [Fact]
    public void ThreeColumnsFillTheScrollWidthWithTheNativePaddingAndNoOverlap()
    {
        float scroll = PortraitGridMath.ScrollWidth(1080f);
        Assert.Equal(1020f, scroll, 3);
        float card = PortraitGridMath.CardWidthFor(scroll);
        // One unit of slack per column, kept back so the float quotient is safely over three.
        Assert.Equal(312.333f, card, 2);
        float row = 3f * card + 2f * PortraitGridMath.CardPadding;
        Assert.True(row <= scroll && scroll - row <= 3f * PortraitGridMath.ColumnSlack + 0.01f, $"row {row:F2} of {scroll:F2}");
        // The native pitch is the card plus the padding: lay the three columns out as the grid does and check the gaps.
        float pitch = card + PortraitGridMath.CardPadding;
        Assert.Equal(352.333f, pitch, 2);
        float first = (scroll - row) * 0.5f + card * 0.5f;
        Assert.True(first - card * 0.5f >= -0.01f, $"first column starts at {first - card * 0.5f:F2}");
        Assert.True(first + 2f * pitch + card * 0.5f <= scroll + 0.01f, $"third column ends at {first + 2f * pitch + card * 0.5f:F2}");
        for (int i = 1; i < PortraitGridMath.Columns; i++)
        {
            float previousEnd = first + (i - 1) * pitch + card * 0.5f;
            float nextStart = first + i * pitch - card * 0.5f;
            Assert.True(nextStart - previousEnd >= PortraitGridMath.CardPadding - 0.01f, $"column {i} gap {nextStart - previousEnd:F2}");
        }
    }

    [Fact]
    public void CardHeightKeepsTheNativeShape()
    {
        float card = PortraitGridMath.CardWidthFor(PortraitGridMath.ScrollWidth(1080f));
        Assert.Equal(card * 422f / 300f, PortraitGridMath.CardHeightFor(card), 3);
        Assert.Equal(439.35f, PortraitGridMath.CardHeightFor(card), 2);
    }

    [Fact]
    public void HolderScaleTurnsANativeCardIntoTheColumnWidth()
    {
        float card = PortraitGridMath.CardWidthFor(PortraitGridMath.ScrollWidth(1080f));
        float scale = PortraitGridMath.HolderScale(card);
        Assert.Equal(312.333f, PortraitGridMath.CardWidth * scale, 2);
        Assert.True(scale > 1f);
    }

    [Theory]
    [InlineData(PortraitGridScreen.DeckView, 273f)]
    [InlineData(PortraitGridScreen.Pile, 273f)]
    [InlineData(PortraitGridScreen.Upgrade, 435f)]
    [InlineData(PortraitGridScreen.Enchant, 457f)]
    [InlineData(PortraitGridScreen.Transform, 284.1f)]
    [InlineData(PortraitGridScreen.DeckSelect, 275.1f)]
    public void EachScreenKeepsItsOwnFooterBand(PortraitGridScreen screen, float footer)
    {
        Assert.Equal(footer, PortraitGridMath.Footer(screen), 3);
        Assert.Equal(NarrowHeight - footer, PortraitGridMath.ViewportBottom(NarrowHeight, screen), 3);
        Assert.Equal(TallHeight - footer, PortraitGridMath.ViewportBottom(TallHeight, screen), 3);
    }

    [Fact]
    public void DeckViewSortRowSitsAboveTheGridAndTheFirstRowUnderIt()
    {
        const float headerBottom = 390f;    // safe cap 162 + the top bar's 228
        float top = PortraitGridMath.GridTop(headerBottom, sortRow: true);
        Assert.Equal(533f, top, 3);
        // The sort row is 8 under the header band and a whole 135 units tall, the grid starts at its bottom edge.
        Assert.Equal(headerBottom + PortraitGridMath.SortHeaderGap + PortraitGridMath.SortHeaderHeight, top, 3);
        Assert.True(PortraitGridMath.SortButtonHeight < PortraitGridMath.SortHeaderHeight);
        // The native deck view drops its cards by DeckYOffset for the sort row inside the scroll area; portrait zeroes that,
        // so the first card starts the grid's own 80-unit top padding under the sort row.
        Assert.True(PortraitGridMath.DeckYOffset > PortraitGridMath.ContentTopPadding);
        Assert.True(top + PortraitGridMath.ContentTopPadding < PortraitGridMath.ViewportBottom(TallHeight, PortraitGridScreen.DeckView));
    }

    [Fact]
    public void SelectionGridsStartJustUnderTheHeader()
    {
        Assert.Equal(398f, PortraitGridMath.GridTop(390f, sortRow: false), 3);
        Assert.Equal(390f + PortraitGridMath.SortHeaderGap, PortraitGridMath.GridTop(390f, sortRow: false), 3);
    }

    [Fact]
    public void ViewportLeavesRoomForAtLeastTwoRowsAtBothShapes()
    {
        float card = PortraitGridMath.CardHeightFor(PortraitGridMath.CardWidthFor(PortraitGridMath.ScrollWidth(1080f)));
        foreach (PortraitGridScreen screen in Enum.GetValues<PortraitGridScreen>())
        {
            float top = PortraitGridMath.GridTop(390f, screen == PortraitGridScreen.DeckView);
            float gridHeight = PortraitGridMath.ViewportBottom(NarrowHeight, screen) - top;
            Assert.True(gridHeight > 2.4f * card, $"{screen} keeps {gridHeight:F0} units for {card:F0}-unit cards");
        }
    }

    [Theory]
    [InlineData(390f)]
    [InlineData(2340f)]
    public void GridStartsBelowTheTopBarOrBelowTheVisibleRelics(float topBarBottom)
    {
        // No relic row on screen: the grid starts under the top bar's own band.
        Assert.Equal(topBarBottom, PortraitGridMath.HeaderBottom(topBarBottom, 0f), 3);
        // Relics that end above the top bar's bottom change nothing; ones that reach lower push the header down by the gap.
        Assert.Equal(topBarBottom, PortraitGridMath.HeaderBottom(topBarBottom, topBarBottom - 40f), 3);
        float relicBottom = topBarBottom + 222f;
        Assert.Equal(relicBottom + PortraitGridMath.RelicGap, PortraitGridMath.HeaderBottom(topBarBottom, relicBottom), 3);
        // The deck view's sort row and the grid follow that band.
        float header = PortraitGridMath.HeaderBottom(topBarBottom, relicBottom);
        Assert.Equal(header + PortraitGridMath.SortHeaderGap + PortraitGridMath.SortHeaderHeight,
            PortraitGridMath.GridTop(header, sortRow: true), 3);
        Assert.Equal(header + PortraitGridMath.SortHeaderGap, PortraitGridMath.GridTop(header, sortRow: false), 3);
    }

    [Theory]
    [InlineData(NarrowHeight)]
    [InlineData(TallHeight)]
    public void BothTestShapesGiveThreeColumnsInFloat(float canvasHeight)
    {
        const float canvasWidth = 1080f;
        float scroll = PortraitGridMath.ScrollWidth(canvasWidth);
        float card = PortraitGridMath.CardWidthFor(scroll);
        // The game's getter as it runs: (scroll + padding) / (card + padding), truncated, in float.
        float quotient = (scroll + PortraitGridMath.CardPadding) / (card + PortraitGridMath.CardPadding);
        Assert.True(quotient >= 3.005f, $"{Shape()}: quotient {quotient:F6}");
        Assert.Equal(3, PortraitGridMath.ColumnsFor(scroll, card));
        Assert.True(quotient < 3.5f, $"{Shape()}: quotient {quotient:F6} must not reach four columns");
        // The grid band at this height still shows a whole card under the header/sort row and above the footer.
        float top = PortraitGridMath.GridTop(390f, sortRow: true);
        float bottom = PortraitGridMath.ViewportBottom(canvasHeight, PortraitGridScreen.DeckView);
        Assert.True(bottom - top > PortraitGridMath.CardHeightFor(card), $"{Shape()}: band {bottom - top:F0}");
        string Shape() => $"height {canvasHeight:F0}";
    }

    [Fact]
    public void LastRowStaysInsideTheGridWhenTheNativeLimitIsReached()
    {
        const int rows = 10;
        float card = PortraitGridMath.CardHeightFor(PortraitGridMath.CardWidthFor(PortraitGridMath.ScrollWidth(1080f)));
        float content = PortraitGridMath.ContentHeight(rows, card);
        Assert.Equal(rows * card + (rows - 1) * PortraitGridMath.CardPadding
            + PortraitGridMath.ContentTopPadding + PortraitGridMath.ContentBottomPadding, content, 2);
        float grid = PortraitGridMath.ViewportBottom(TallHeight, PortraitGridScreen.DeckView)
            - PortraitGridMath.GridTop(390f, sortRow: true);
        float limit = PortraitGridMath.ScrollLimit(grid, content);
        Assert.Equal(content - grid, -limit, 2);
        // The last row's bottom lands the game's own bottom padding above the grid's foot: reachable, above the footer.
        Assert.True(PortraitGridMath.LastRowBottomAtLimit(grid) > 0f);
        Assert.Equal(grid - PortraitGridMath.ContentBottomPadding, PortraitGridMath.LastRowBottomAtLimit(grid), 2);
    }

    [Fact]
    public void ShortContentIsCentredNotScrolled()
    {
        Assert.Equal(0f, PortraitGridMath.ScrollLimit(gridHeight: 1600f, contentHeight: 900f), 3);
        Assert.True(PortraitGridMath.ScrollLimit(gridHeight: 900f, contentHeight: 1600f) < 0f);
    }

    [Fact]
    public void EnchantFooterGrowsWithItsLegend()
    {
        // A prompt whose top is closer to the bottom than the static band leaves the static band in charge.
        Assert.Equal(PortraitGridMath.FooterEnchant,
            PortraitGridMath.PromptReserve(PortraitGridMath.FooterEnchant, TallHeight, TallHeight - 400f), 3);
        // A legend that pushes the prompt up by more than the static band keeps the prompt's own room.
        Assert.Equal(600f + PortraitGridMath.PromptGap,
            PortraitGridMath.PromptReserve(PortraitGridMath.FooterEnchant, TallHeight, TallHeight - 600f), 3);
    }

    [Fact]
    public void SortButtonsAreAWholeTouchTargetAndFitTheRow()
    {
        float width = PortraitGridMath.ScrollWidth(1080f);
        float button = PortraitGridMath.SortButtonWidth(width, 4);
        Assert.Equal(250f, button, 3);
        Assert.True(button * 4 + PortraitGridMath.SortButtonGap * 3 <= width);
        Assert.True(PortraitGridMath.SortButtonHeight >= 127f);
        Assert.True(PortraitGridMath.SortHeaderHeight >= PortraitGridMath.SortButtonHeight);
        Assert.True(PortraitGridMath.SortFontMax > PortraitGridMath.SortFontMin);
    }

    [Fact]
    public void ToggleBoxIsFortyFourPointsTallAndCentred()
    {
        const float native = 64f;
        const float nativeWidth = 262f;
        float scale = PortraitGridMath.ToggleScale(native);
        Assert.Equal(PortraitGridMath.ToggleHeight, native * scale, 3);
        Assert.True(scale > 1.9f);
        float left = PortraitGridMath.ToggleLeft(1080f, nativeWidth, scale);
        Assert.Equal(1080f - (left + nativeWidth * scale), left, 2);
        Assert.Equal(1f, PortraitGridMath.ToggleScale(1000f), 3);
    }

    [Fact]
    public void ToggleStandsAtTheReferenceHeightsAndBelowTheGrid()
    {
        Assert.Equal(144f, PortraitGridMath.ToggleTop(PortraitGridScreen.DeckView), 3);
        Assert.Equal(135.6f, PortraitGridMath.ToggleTop(PortraitGridScreen.Upgrade), 3);
        Assert.Equal(144f, PortraitGridMath.ToggleTop(PortraitGridScreen.Transform), 3);
        float gridBottom = PortraitGridMath.ViewportBottom(TallHeight, PortraitGridScreen.Upgrade);
        float toggleTop = TallHeight - PortraitGridMath.ToggleTop(PortraitGridScreen.Upgrade);
        Assert.True(toggleTop > gridBottom);
        Assert.True(TallHeight - (toggleTop + PortraitGridMath.ToggleHeight) > 0f);
    }

    [Fact]
    public void DeckViewInfoLineFitsBetweenGridAndToggle()
    {
        float top = TallHeight - PortraitGridMath.InfoTextTop;
        Assert.True(top > PortraitGridMath.ViewportBottom(TallHeight, PortraitGridScreen.DeckView));
        // Half the canvas, flush to the right margin, so the Back button keeps the left and nothing runs off the canvas.
        Assert.Equal(540f, PortraitGridMath.InfoTextWidth(1080f), 3);
        Assert.Equal(510f, PortraitGridMath.InfoTextLeft(1080f), 3);
        Assert.Equal(1080f, PortraitGridMath.InfoTextLeft(1080f) + PortraitGridMath.InfoTextWidth(1080f) + PortraitGridMath.SideInset, 3);
        // The box stands above the View Upgrades box and inside the footer band.
        Assert.True(PortraitGridMath.InfoTextTop - PortraitGridMath.InfoTextHeight > PortraitGridMath.ToggleTop(PortraitGridScreen.DeckView));
        // 11 points at 2.88 units per point.
        Assert.True(PortraitGridMath.InfoTextFontSize >= 11f * 1080f / 375f - 0.5f);
        Assert.True(PortraitGridMath.InfoTextFontSize <= 11f * 1080f / 375f + 1f);
    }
}
