namespace STS2Mobile.Platform.Tests;

public class PortraitRestMathTests
{
    // The game's own option buttons: 2 x 246.786 wide with 100 between each pair.
    private const float TwoButtons = 2f * PortraitRestMath.NativeOptionWidth + PortraitRestMath.NativeOptionSeparation;
    private const float ThreeButtons = 3f * PortraitRestMath.NativeOptionWidth + 2f * PortraitRestMath.NativeOptionSeparation;
    private const float FourButtons = 4f * PortraitRestMath.NativeOptionWidth + 3f * PortraitRestMath.NativeOptionSeparation;

    [Fact]
    public void TwoOptionsArePinned232PointsAboveTheBottom()
    {
        RestOptionsRow tall = PortraitRestMath.OptionsRow(2, TwoButtons, 1080f, 2340f);
        Assert.Equal(800f, tall.Width);
        Assert.Equal(200f, tall.Height);
        Assert.Equal(PortraitRestMath.OptionsMaxScale, tall.Scale, 5);
        Assert.Equal(128.571f, tall.X, 3);
        Assert.Equal(1671.429f, tall.Y, 3);   // 2340 - 668.57

        RestOptionsRow compact = PortraitRestMath.OptionsRow(2, TwoButtons, 1080f, 1920f);
        Assert.Equal(1251.429f, compact.Y, 3); // the same 232 points above the bottom
        Assert.Equal(tall.Scale, compact.Scale);
    }

    [Fact]
    public void TheButtonsGrowTo58PointsAndStayFingerSized()
    {
        RestOptionsRow two = PortraitRestMath.OptionsRow(2, TwoButtons, 1080f, 2340f);
        Assert.Equal(167.53f, 162.879f * two.Scale, 2);  // 58.2 points tall
        Assert.True(two.Height * two.Scale > PortraitRestMath.TouchTarget);
        foreach (int count in new[] { 2, 3, 4 })
        {
            float natural = count * PortraitRestMath.NativeOptionWidth + (count - 1) * PortraitRestMath.NativeOptionSeparation;
            RestOptionsRow row = PortraitRestMath.OptionsRow(count, natural, 1080f, 2340f);
            Assert.True(natural * row.Scale >= PortraitRestMath.TouchTarget - 0.01f, $"{count} options keep a 44 point button");
            Assert.True(row.X >= 0f && row.X + row.Width * row.Scale <= 1080f, $"{count} options fit the canvas");
        }
    }

    [Fact]
    public void ThreeAndFourOptionsShrinkOnlyWhenTheRowRunsOutOfWidth()
    {
        RestOptionsRow three = PortraitRestMath.OptionsRow(3, ThreeButtons, 1080f, 2340f);
        Assert.Equal(950f, three.Width);
        Assert.Equal(PortraitRestMath.OptionsMaxScale, three.Scale, 5);
        Assert.Equal(51.429f, three.X, 3);

        RestOptionsRow four = PortraitRestMath.OptionsRow(4, FourButtons, 1080f, 2340f);
        Assert.Equal(1300f, four.Width);
        Assert.Equal(PortraitRestMath.MinOptionScale, four.Scale, 5); // 0.7797: exactly one touch target
        Assert.Equal(127f, PortraitRestMath.NativeOptionHeight * four.Scale, 1);
        Assert.Equal(33.182f, four.X, 3);
    }

    [Fact]
    public void ElevenOptionsWrapIntoBalancedRowsOfFour()
    {
        Assert.Equal(1, PortraitRestMath.OptionsRowCount(2));
        Assert.Equal(1, PortraitRestMath.OptionsRowCount(4));
        Assert.Equal(2, PortraitRestMath.OptionsRowCount(5));
        Assert.Equal(3, PortraitRestMath.OptionsRowCount(11));
        Assert.Equal(4, PortraitRestMath.OptionsRowCount(16));
        Assert.Equal(0, PortraitRestMath.OptionsRowCount(0));

        Assert.Equal(4, PortraitRestMath.OptionsInRow(11, 0));
        Assert.Equal(4, PortraitRestMath.OptionsInRow(11, 1));
        Assert.Equal(3, PortraitRestMath.OptionsInRow(11, 2));
        Assert.Equal(3, PortraitRestMath.OptionsInRow(5, 0));
        Assert.Equal(2, PortraitRestMath.OptionsInRow(5, 1));
        Assert.Equal(3, PortraitRestMath.OptionsInRow(9, 1));

        // Four at a time is the most that keeps a finger-sized button: 1003.6 units drawn inside the canvas.
        Assert.Equal(PortraitRestMath.MinOptionScale, PortraitRestMath.OptionsGridScale(11, 1080f), 5);
        float natural = PortraitRestMath.NaturalRowWidth(4);
        Assert.Equal(1003.6f, natural * PortraitRestMath.OptionsGridScale(11, 1080f), 1);
        Assert.True(PortraitRestMath.OptionsGridScale(11, 1080f) * PortraitRestMath.NativeOptionHeight >= PortraitRestMath.TouchTarget);
    }

    [Fact]
    public void WrappedRowsStackAboveTheSingleRowsPlaceAndStayInsideTheCanvas()
    {
        Span<RestOptionsRow> rows = stackalloc RestOptionsRow[8];
        foreach (int count in new[] { 5, 6, 7, 8, 9, 10, 11 })
        {
            foreach (float canvasHeight in new[] { 1920f, 2340f })
            {
                int n = PortraitRestMath.OptionsGrid(count, 1080f, canvasHeight, rows);
                Assert.Equal(PortraitRestMath.OptionsRowCount(count), n);
                float bottomRowTop = canvasHeight - PortraitRestMath.OptionsBottomInset;
                Assert.Equal(bottomRowTop, rows[n - 1].Y, 3);
                Assert.Equal(PortraitRestMath.OptionsGridHeight(count, 1080f), (n - 1) * (rows[1].Y - rows[0].Y) + rows[0].Height * rows[0].Scale, 2);
                float previousBottom = float.NegativeInfinity;
                int seen = 0;
                for (int i = 0; i < n; i++)
                {
                    int inRow = PortraitRestMath.OptionsInRow(count, i);
                    seen += inRow;
                    float drawnWidth = PortraitRestMath.NaturalRowWidth(inRow) * rows[i].Scale;
                    Assert.Equal(1080f * 0.5f, rows[i].X + rows[i].Width * rows[i].Scale * 0.5f, 2);
                    Assert.True(rows[i].X + rows[i].Width * rows[i].Scale <= 1080f);
                    Assert.True(rows[i].X >= 0f);
                    Assert.True(rows[i].Height * rows[i].Scale >= PortraitRestMath.TouchTarget, "button box keeps a touch target");
                    Assert.True(rows[i].X + (rows[i].Width * rows[i].Scale - drawnWidth) * 0.5f >= 0f);
                    // The rows do not touch: the label hangs 25 units under its box, the gap is 48.
                    float boxBottom = rows[i].Y + rows[i].Height * rows[i].Scale;
                    if (i > 0) Assert.True(rows[i].Y > previousBottom, $"row {i} clears the one above ({rows[i].Y:F1} vs {previousBottom:F1})");
                    previousBottom = boxBottom;
                }
                Assert.Equal(count, seen);
                // Every row sits below the stone header and inside the canvas's top edge.
                Assert.True(rows[0].Y - PortraitRestMath.HeaderGap > 390f, $"{count} options keep the question under the header");
            }
        }
    }

    [Fact]
    public void FiveOptionsWrapAtTheTouchTargetInsteadOfShrinking()
    {
        Span<RestOptionsRow> rows = stackalloc RestOptionsRow[4];
        int n = PortraitRestMath.OptionsGrid(5, 1080f, 2340f, rows);
        Assert.Equal(2, n);
        Assert.Equal(0.77972f, rows[0].Scale, 4);
        Assert.Equal(3, PortraitRestMath.OptionsInRow(5, 0));
        Assert.Equal(950f, rows[0].Width);
        // The single row this replaced would have drawn 99 unit buttons; wrapped, the box stays 156 tall (127+ after scaling).
        Assert.True(rows[1].Height * rows[1].Scale >= PortraitRestMath.TouchTarget);
    }

    [Fact]
    public void TheRowIsCentred()
    {
        foreach (int count in new[] { 2, 3, 4 })
        {
            float natural = count * PortraitRestMath.NativeOptionWidth + (count - 1) * PortraitRestMath.NativeOptionSeparation;
            RestOptionsRow row = PortraitRestMath.OptionsRow(count, natural, 1080f, 2340f);
            float left = row.X + (row.Width * row.Scale - natural * row.Scale) * 0.5f;
            float right = 1080f - left - natural * row.Scale;
            Assert.Equal(left, right, 3);
        }
    }

    [Fact]
    public void QuestionSitsAboveTheRowAndTheCopyNearTheBottom()
    {
        RestOptionsRow row = PortraitRestMath.OptionsRow(2, TwoButtons, 1080f, 2340f);
        RestTextBlock header = PortraitRestMath.Header(row.Y, 1080f);
        Assert.Equal(41.143f, header.X, 3);
        Assert.Equal(1542.857f, header.Y, 3);
        Assert.Equal(997.714f, header.Width, 3);
        Assert.Equal(96.429f, header.Height, 3);
        Assert.Equal(51, PortraitRestMath.HeaderFontSize);

        RestTextBlock copy = PortraitRestMath.Description(1080f, 2340f);
        Assert.Equal(61.714f, copy.X, 3);
        Assert.Equal(1992.857f, copy.Y, 3);
        Assert.Equal(956.571f, copy.Width, 3);
        Assert.Equal(231.429f, copy.Height, 3);
        Assert.Equal(41, PortraitRestMath.DescriptionFontSize);

        // The copy starts below the labels that hang under the buttons (165 button units plus a 48 unit label).
        float labelBottom = row.Y + ((row.Height - PortraitRestMath.NativeOptionHeight) * 0.5f + 165f + 48f) * row.Scale;
        Assert.True(copy.Y > labelBottom, $"copy {copy.Y:F0} under labels {labelBottom:F0}");
        Assert.Equal(1920f - 347.143f, PortraitRestMath.Description(1080f, 1920f).Y, 3);
    }

    [Fact]
    public void ArtCoversTheCanvasWithoutStretching()
    {
        Assert.Equal(2.1666667f, PortraitRestMath.CoverScale(1080f, 2340f), 4); // the tall canvas's height decides
        Assert.Equal(1.7777778f, PortraitRestMath.CoverScale(1080f, 1920f), 4);
        RestPoint anchor = PortraitRestMath.ArtAnchor(-934f, -466f);
        Assert.Equal(934f, anchor.X);
        Assert.Equal(466f, anchor.Y);

        RestCover tall = PortraitRestMath.ArtCover(1080f, 2340f, anchor);
        Assert.Equal(2.1666667f, tall.Scale, 4);
        Assert.Equal(-1483.667f, tall.X, 2);
        Assert.Equal(160.333f, tall.Y, 2);

        RestCover compact = PortraitRestMath.ArtCover(1080f, 1920f, anchor);
        Assert.Equal(-1120.444f, compact.X, 2);
        Assert.Equal(131.556f, compact.Y, 2);
    }

    [Fact]
    public void TheCampfireIsFramedAtTheAuthoredFocus()
    {
        RestPoint anchor = PortraitRestMath.ArtAnchor(-934f, -466f);
        RestPoint focus = PortraitRestMath.ArtFocus(anchor);
        Assert.Equal(857.2f, focus.X, 3);   // 0.46 of 1920, from the authored centre
        Assert.Equal(595.6f, focus.Y, 3);   // 0.62 of 1080

        // Character_1's own place (624.775, 641.775), a half-scale container, no pivot.
        RestPoint framed = PortraitRestMath.FramePosition(
            new RestPoint(624.775f, 641.775f), new RestPoint(0f, 0f), anchor, focus, PortraitRestMath.RestZoom);
        Assert.Equal(762.01f, framed.X, 2);
        Assert.Equal(500.170f, framed.Y, 3);
        Assert.Equal(new RestPoint(0.37f, 0.37f), PortraitRestMath.FrameScale(new RestPoint(0.5f, 0.5f), PortraitRestMath.RestZoom));
    }

    [Fact]
    public void APivotMovesTheDrawingByTheZoomItLacks()
    {
        RestPoint framed = PortraitRestMath.FramePosition(
            new RestPoint(100f, 100f), new RestPoint(10f, 20f), new RestPoint(0f, 0f), new RestPoint(50f, 50f), 0.5f);
        Assert.Equal(20f, framed.X, 3);
        Assert.Equal(15f, framed.Y, 3);
    }

    [Theory]
    [InlineData("RestSiteLLog", true)]
    [InlineData("RestSiteRLog2", true)]
    [InlineData("RestSiteFireLogs", true)]
    [InlineData("LLogHighlight", true)]
    [InlineData("light_small2", true)]
    [InlineData("light_front", true)]
    [InlineData("FireLight", true)]
    [InlineData("fire_small", true)]
    [InlineData("SteppedFire", true)]
    [InlineData("sparks big", true)]
    [InlineData("smoke", true)]
    [InlineData("RestSiteGroundLighting2", false)]
    [InlineData("WallLight7", false)]
    [InlineData("RestSiteBG", false)]
    [InlineData("RestSiteForeground2", false)]
    [InlineData("roots", false)]
    [InlineData("Character_1", false)]
    public void OnlyTheCampItselfIsReframed(string name, bool camp)
    {
        Assert.Equal(camp, PortraitRestMath.IsCampPiece(name));
    }
}
