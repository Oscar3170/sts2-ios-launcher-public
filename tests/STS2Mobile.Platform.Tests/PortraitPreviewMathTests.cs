namespace STS2Mobile.Platform.Tests;

public class PortraitPreviewMathTests
{
    private const float Unit = 1080f / 1680f;

    [Fact]
    public void PairFitDrawsTheReferenceComparisonAtTheSamePointSize()
    {
        // The reference's pair caps at 1.98 of a 300 x 422 card on its 1680 canvas; on 1080 the same drawn card is 1.98 * 1080/1680.
        Assert.Equal(1.272857f, PortraitPreviewMath.PairScale(1080f), 5);
        Assert.Equal(1.98f * Unit, PortraitPreviewMath.PairMaxScale, 5);
        PreviewPairFit fit = PortraitPreviewMath.PreviewPair(1080f, 1207.44f);
        Assert.Equal(540f, fit.CenterX, 3);
        Assert.Equal(1207.44f, fit.CenterY, 3);
        // The two cards' outer edges, with the pair's own +/- 235 card offsets and a 300 wide card.
        float half = (PortraitPreviewMath.PairCardOffset + PortraitPreviewMath.RowCardWidth * 0.5f) * fit.Scale;
        Assert.Equal(49.95f, fit.CenterX - half, 2);
        Assert.Equal(1030.05f, fit.CenterX + half, 2);
        Assert.True(fit.CenterX - half > 0f && fit.CenterX + half < 1080f);
    }

    [Theory]
    [InlineData(1, 1.298571f, 239.233f)]
    [InlineData(2, 1.298571f, 239.233f)]
    [InlineData(3, 1.045892f, 199.562f)]
    [InlineData(4, 0.782392f, 158.193f)]
    [InlineData(5, 0.624945f, 133.473f)]
    [InlineData(6, 0.52025f, 117.036f)]
    public void PreviewRowScalesLikeTheReferenceAndCentresItsContainer(int count, float scale, float left)
    {
        Assert.Equal(scale, PortraitPreviewMath.RowScale(1080f, count), 5);
        Assert.Equal(left, PortraitPreviewMath.RowLeft(1080f, count), 2);
        PreviewRowFit fit = PortraitPreviewMath.PreviewRow(1080f, count, 1251f);
        Assert.Equal(scale, fit.Scale, 5);
        Assert.Equal(left, 540f - fit.LocalWidth * fit.Scale * 0.5f, 2);
        Assert.Equal(1251f, fit.CenterY, 3);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    public void PreviewRowStaysInsideTheCanvas(int count)
    {
        PreviewRowFit fit = PortraitPreviewMath.PreviewRow(1080f, count, 1000f);
        float cardHalf = PortraitPreviewMath.RowCardWidth * 0.5f * fit.Scale;
        float firstCenter = 540f - PortraitPreviewMath.RowPitch * (count - 1) * fit.Scale * 0.5f;
        float left = firstCenter - cardHalf;
        float right = firstCenter + PortraitPreviewMath.RowPitch * (count - 1) * fit.Scale + cardHalf;
        Assert.True(left >= 0f, $"count {count} starts at {left}");
        Assert.True(right <= 1080f, $"count {count} ends at {right}");
        // Symmetric around the canvas middle whatever the count.
        Assert.Equal(1080f - right, left, 2);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void PreviewRowContainerWidthHoldsEveryCardCentre(int count)
    {
        float width = PortraitPreviewMath.RowLocalWidth(1080f, count);
        Assert.True(width >= PortraitPreviewMath.RowPitch * (count - 1), $"width {width} for {count} cards");
    }

    [Fact]
    public void PromptScaleCapsTheReferenceGrowthAndFitsTheCanvas()
    {
        // A prompt wider than the canvas is shrunk to fit 100 reference units of margin each side (64.3 here).
        Assert.Equal((1080f - 100f * Unit) / 1200f, PortraitPreviewMath.PromptScale(1080f, 1200f), 5);
        Assert.Equal(0.846428f, PortraitPreviewMath.PromptScale(1080f, 1200f), 5);
        // A narrow prompt grows to the reference's 2.2 (1.414 here) and no further.
        Assert.Equal(2.2f * Unit, PortraitPreviewMath.PromptScale(1080f, 400f), 5);
        Assert.Equal(1f, PortraitPreviewMath.PromptScale(1080f, 0f), 5);
    }

    [Fact]
    public void EnchantPromptPacksUnderTheLegendWithItsOwnGrowthCap()
    {
        Assert.Equal(2.12f * Unit, PortraitPreviewMath.FooterPromptMaxScale, 5);
        Assert.Equal((1080f - 120f * Unit) / 1200f, PortraitPreviewMath.FooterPromptScale(1080f, 1200f), 5);
        Assert.Equal(0.835714f, PortraitPreviewMath.FooterPromptScale(1080f, 1200f), 5);
        Assert.Equal(2.12f * Unit, PortraitPreviewMath.FooterPromptScale(1080f, 400f), 5);
    }

    [Theory]
    [InlineData(2340f, 1923.429f, 1905.429f, 2073.857f, 2055.857f, 2082.857f, 2064.857f)]
    [InlineData(1920f, 1503.429f, 1485.429f, 1653.857f, 1635.857f, 1662.857f, 1644.857f)]
    public void PromptTopsAndGridBottomsFollowTheCanvasBottom(
        float canvasHeight, float upgradePrompt, float upgradeGrid,
        float transformPrompt, float transformGrid, float selectPrompt, float selectGrid)
    {
        Assert.Equal(upgradePrompt, PortraitPreviewMath.UpgradePromptTop(canvasHeight), 3);
        Assert.Equal(upgradeGrid, PortraitPreviewMath.GridBottom(PortraitPreviewMath.UpgradePromptTop(canvasHeight)), 3);
        Assert.Equal(transformPrompt, PortraitPreviewMath.TransformPromptTop(canvasHeight), 3);
        Assert.Equal(transformGrid, PortraitPreviewMath.GridBottom(PortraitPreviewMath.TransformPromptTop(canvasHeight)), 3);
        Assert.Equal(selectPrompt, PortraitPreviewMath.SelectPromptTop(canvasHeight), 3);
        Assert.Equal(selectGrid, PortraitPreviewMath.GridBottom(PortraitPreviewMath.SelectPromptTop(canvasHeight)), 3);
    }

    [Theory]
    [InlineData(2340f, 1969.071f)]
    [InlineData(1920f, 1549.071f)]
    public void LegendPacksUpFromTheBottomAndThePromptStacksAboveIt(float canvasHeight, float legendTop)
    {
        Assert.Equal(legendTop, PortraitPreviewMath.LegendTop(canvasHeight, 150f), 3);
        Assert.Equal(2.08f * Unit, PortraitPreviewMath.LegendScale, 5);
        // A 100 unit prompt at 1.35 sits one 20 reference unit gap above the legend, and the grid ends 28 reference units above it.
        float promptTop = PortraitPreviewMath.PromptTopAboveLegend(legendTop, 100f, 1.35f);
        Assert.Equal(legendTop - 20f * Unit - 135f, promptTop, 3);
        Assert.Equal(promptTop - 28f * Unit, PortraitPreviewMath.GridBottom(promptTop), 3);
        // The legend is 990 units across as drawn, 45 in from the left edge.
        float drawn = PortraitPreviewMath.LegendLocalWidth(1080f) * PortraitPreviewMath.LegendScale;
        Assert.Equal(990f, drawn, 3);
        Assert.Equal(45f, PortraitPreviewMath.LegendX, 3);
    }

    [Theory]
    [InlineData(110f, false, 1.317857f)]
    [InlineData(110f, true, 1.35f)]
    [InlineData(140f, false, 1.317857f)]
    [InlineData(80f, false, 1.5875f)]
    [InlineData(0f, false, 1.317857f)]
    public void PreviewButtonsTakeTheReferenceGrowthAtLeastATouchTarget(float nativeHeight, bool cancel, float expected)
    {
        Assert.Equal(expected, PortraitPreviewMath.ButtonScale(nativeHeight, cancel), 5);
        if (nativeHeight > 0.5f) Assert.True(nativeHeight * expected >= PortraitPreviewMath.TouchTarget);
    }

    [Theory]
    [InlineData(2340f, 2179.286f)]
    [InlineData(1920f, 1759.286f)]
    public void ComparisonButtonsSitAboveTheCanvasBottom(float canvasHeight, float top)
    {
        Assert.Equal(top, PortraitPreviewMath.ButtonTop(canvasHeight), 3);
        // A 110 unit button at the reference growth is 145 tall (2.05 * 110 * 1080/1680) and keeps 15.7 units clear of the
        // bottom edge; that is above the 127 unit touch target.
        float height = 110f * PortraitPreviewMath.ButtonScale(110f, false);
        Assert.Equal(144.964f, height, 3);
        Assert.True(height >= PortraitPreviewMath.TouchTarget);
        Assert.Equal(15.75f, canvasHeight - (top + height), 2);
        Assert.Equal(1080f - 400f - 18f * Unit, PortraitPreviewMath.ConfirmLeft(1080f, 400f), 3);
    }

    [Theory]
    [InlineData(2340f)]
    [InlineData(1920f)]
    public void UpgradeCloseStandsAboveTheViewUpgradesBox(float canvasHeight)
    {
        // The reference's View Upgrades box for the upgrade screen starts at End.Y - 211 (135.6 here); the Close's bottom is
        // 18 units above it, as its top is at End.Y - 470 (302.1) and it is drawn 148.5 tall.
        float closeBottom = PortraitPreviewMath.CloseTop(canvasHeight) + 110f * PortraitPreviewMath.ButtonScale(110f, true);
        float toggleTop = canvasHeight - 211f * Unit;
        Assert.Equal(18f, toggleTop - closeBottom, 2);
    }

    [Theory]
    [InlineData(162f, 2340f, 0.48f, 1207.44f)]
    [InlineData(136f, 1920f, 0.48f, 992.32f)]
    [InlineData(162f, 2340f, 0.52f, 1294.56f)]
    [InlineData(136f, 1920f, 0.52f, 1063.68f)]
    [InlineData(162f, 2340f, 0.5f, 1251f)]
    [InlineData(136f, 1920f, 0.5f, 1028f)]
    public void PreviewCentreSitsBetweenTheNotchCapAndTheCanvasBottom(
        float capHeight, float canvasHeight, float fraction, float expected)
    {
        Assert.Equal(expected, PortraitPreviewMath.PreviewCenterY(capHeight, canvasHeight, fraction), 2);
    }

    [Theory]
    [InlineData(1920f, 136f, 850f, 422f, 535f, 960f, 1.185882f, 960f, 1008f, 500.44f)]
    [InlineData(1920f, 136f, 900.4f, 207.65f, 534.98f, 959.98f, 1.119502f, 959.98f, 1008f, 232.46f)]
    [InlineData(2340f, 162f, 850f, 422f, 535f, 960f, 1.185882f, 960f, 1008f, 500.44f)]
    [InlineData(2340f, 162f, 900.4f, 207.65f, 534.98f, 959.98f, 1.119502f, 959.98f, 1008f, 232.46f)]
    public void TransformFitSpansTheCanvasWithTheReferenceMargins(
        float canvasHeight, float capHeight, float width, float height, float centreX, float centreY,
        float scale, float middle, float drawnWidth, float drawnHeight)
    {
        TransformPreviewFit fit = PortraitPreviewMath.TransformFit(
            1080f, canvasHeight, capHeight, PortraitPreviewMath.TransformPromptTop(canvasHeight),
            centreX, centreY, width, height);
        Assert.Equal(scale, fit.Scale, 5);
        Assert.Equal(540f, fit.CenterX, 3);
        Assert.Equal(middle, fit.CenterY, 2);
        // The drawn composition is the canvas less 36 either side, and inside the band between the cap and the prompt.
        Assert.Equal(drawnWidth, width * fit.Scale, 2);
        Assert.Equal(drawnHeight, height * fit.Scale, 2);
        float promptTop = PortraitPreviewMath.TransformPromptTop(canvasHeight);
        Assert.True(fit.CenterY - height * fit.Scale * 0.5f >= capHeight + 7.9f);
        Assert.True(fit.CenterY + height * fit.Scale * 0.5f <= promptTop - 7.9f);
    }

    [Fact]
    public void TransformFitClampsLikeTheReferenceAndKeepsTheBand()
    {
        // A tiny composition grows to the reference's 1.98 (1.273 here); a huge one shrinks to 0.1 (0.064).
        Assert.Equal(PortraitPreviewMath.TransformMaxScale, PortraitPreviewMath.TransformFit(
            1080f, 1920f, 136f, PortraitPreviewMath.TransformPromptTop(1920f), 540f, 960f, 10f, 10f).Scale, 5);
        Assert.Equal(PortraitPreviewMath.TransformMinScale, PortraitPreviewMath.TransformFit(
            1080f, 1920f, 136f, PortraitPreviewMath.TransformPromptTop(1920f), 540f, 960f, 30000f, 30000f).Scale, 5);
        // A composition taller than the band is kept inside it (its middle stops where the band ends).
        TransformPreviewFit tall = PortraitPreviewMath.TransformFit(
            1080f, 1920f, 136f, PortraitPreviewMath.TransformPromptTop(1920f), 540f, 960f, 100f, 2000f);
        Assert.Equal(0.750929f, tall.Scale, 5);
        Assert.True(tall.CenterY - 2000f * tall.Scale * 0.5f >= 144f);
        // Nothing to measure keeps the game's own size and place.
        TransformPreviewFit none = PortraitPreviewMath.TransformFit(
            1080f, 1920f, 136f, 1600f, 500f, 900f, 0f, 0f);
        Assert.Equal(1f, none.Scale, 3);
        Assert.Equal(500f, none.CenterX, 3);
        Assert.Equal(900f, none.CenterY, 3);
    }

    [Fact]
    public void PreviewScaleFactorsConvertByTheReferenceUnit()
    {
        // A scale factor on a scene control converts like a length: 2.05 at 1680 is 1.3179 on the 1080 canvas.
        Assert.Equal(2.05f * Unit, PortraitPreviewMath.ConfirmScale, 5);
        Assert.Equal(2.1f * Unit, PortraitPreviewMath.CancelScale, 5);
        Assert.Equal(2.02f * Unit, PortraitPreviewMath.RowMaxScale, 5);
        Assert.Equal(112f * Unit, PortraitPreviewMath.PairSideMargin, 3);
        Assert.Equal(18f * Unit, PortraitPreviewMath.ButtonRightMargin, 3);
        Assert.Equal(250f * Unit, PortraitPreviewMath.ButtonTopFromBottom, 3);
        Assert.Equal(28f * Unit, PortraitPreviewMath.GridGap, 3);
    }
}
