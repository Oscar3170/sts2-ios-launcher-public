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
    public void OptionsTakeTwoRowsFromThreeOnwards()
    {
        Assert.Equal(0, PortraitRestMath.OptionsRowCount(0));
        Assert.Equal(1, PortraitRestMath.OptionsRowCount(1));
        Assert.Equal(1, PortraitRestMath.OptionsRowCount(2));
        Assert.Equal(2, PortraitRestMath.OptionsRowCount(3));
        Assert.Equal(2, PortraitRestMath.OptionsRowCount(11));

        // 3 = 2+1, 4 = 2x2, 5 = 3+2, 6 = 3+3, 7+ = ceil/floor: the top row takes the odd one.
        (int Count, int Top, int Bottom)[] splits =
            [(3, 2, 1), (4, 2, 2), (5, 3, 2), (6, 3, 3), (7, 4, 3), (8, 4, 4), (9, 5, 4), (10, 5, 5), (11, 6, 5)];
        foreach ((int count, int top, int bottom) in splits)
        {
            Assert.Equal(top, PortraitRestMath.OptionsInRow(count, 0));
            Assert.Equal(bottom, PortraitRestMath.OptionsInRow(count, 1));
        }
    }

    [Fact]
    public void TwoAndThreeWideRowsGrowToTheSingleRowsCapAndFourKeepsThePhoneScale()
    {
        // Rows of two or three take the single row's cap (the reference's 1.6 converted): 88x58 pt buttons, never wider than a single row.
        foreach (int count in new[] { 3, 4, 5, 6 })
        {
            Assert.Equal(PortraitRestMath.OptionsMaxScale, PortraitRestMath.OptionsGridScale(count, 1080f), 5);
            int top = PortraitRestMath.OptionsInRow(count, 0);
            Assert.True(PortraitRestMath.NaturalRowWidth(top) * PortraitRestMath.OptionsGridScale(count, 1080f)
                        <= PortraitRestMath.NaturalRowWidth(4) * PortraitRestMath.MinOptionScale + 0.01f,
                $"{count} options are no wider than the four-wide phone row");
        }
        // A four-wide top row is exactly the 44 point row (0.77972), the same as four options in one row.
        Assert.Equal(PortraitRestMath.MinOptionScale, PortraitRestMath.OptionsGridScale(7, 1080f), 5);
        Assert.Equal(127f, PortraitRestMath.NativeOptionHeight * PortraitRestMath.OptionsGridScale(7, 1080f), 1);
        Span<RestOptionsRow> rows = stackalloc RestOptionsRow[2];
        PortraitRestMath.OptionsGrid(7, 1080f, 2340f, rows);
        Assert.Equal(1300f, rows[0].Width);
        Assert.Equal(33.182f, rows[0].X, 3);
        Assert.Equal(1003.6f, PortraitRestMath.NaturalRowWidth(4) * rows[0].Scale, 1);
    }

    [Fact]
    public void TwoRowsStayInsideTheCanvasAndClearOfTheQuestionAndCopyAtBothShapes()
    {
        Span<RestOptionsRow> rows = stackalloc RestOptionsRow[2];
        foreach (int count in new[] { 3, 4, 5, 6, 7, 9, 10, 11 })
        {
            foreach (float canvasHeight in new[] { 1920f, 2340f })
            {
                int n = PortraitRestMath.OptionsGrid(count, 1080f, canvasHeight, rows);
                Assert.Equal(2, n);
                float scale = rows[0].Scale;
                float bottomTop = canvasHeight - PortraitRestMath.OptionsBottomInset;
                // The bottom row keeps the single row's pin; the top row stands one row gap above it.
                Assert.Equal(bottomTop, rows[1].Y, 3);
                Assert.Equal(bottomTop - (PortraitRestMath.OptionsHeight + PortraitRestMath.OptionsRowGap) * scale, rows[0].Y, 3);
                for (int i = 0; i < n; i++)
                {
                    Assert.Equal(540f, rows[i].X + rows[i].Width * rows[i].Scale * 0.5f, 2);
                    Assert.True(rows[i].X >= 0f && rows[i].X + rows[i].Width * rows[i].Scale <= 1080f, "row inside the canvas");
                    // The top row keeps the four-wide row's edge margin (38.2); the narrower bottom row is centred under it.
                    float drawn = PortraitRestMath.NaturalRowWidth(PortraitRestMath.OptionsInRow(count, i)) * scale;
                    float edge = (1080f - drawn) * 0.5f;
                    Assert.True(edge >= PortraitRestMath.GridRowEdge(1080f) - 0.02f, $"row {i} keeps the grid's edge ({edge:F1})");
                    Assert.Equal(540f, rows[i].X + (rows[i].Width * rows[i].Scale - drawn) * 0.5f + drawn * 0.5f, 2);
                }
                // The question's box ends above the top row.
                float questionBottom = rows[0].Y - PortraitRestMath.HeaderGap + PortraitRestMath.HeaderHeight;
                Assert.True(questionBottom < rows[0].Y, $"{count}: question {questionBottom:F1} above {rows[0].Y:F1}");
                Assert.True(rows[0].Y - PortraitRestMath.HeaderGap > 390f, $"{count}: question under the stone header");
                // The bottom row's label ink (165 units down plus the 48 unit label) ends above the copy and the footer lane.
                float labelBottom = bottomTop + 213f * scale;
                Assert.True(labelBottom < canvasHeight - PortraitRestMath.DescriptionBottomInset,
                    $"{count}: labels {labelBottom:F1} above the copy {canvasHeight - PortraitRestMath.DescriptionBottomInset:F1}");
                Assert.True(labelBottom < canvasHeight - PortraitBetweenMath.ActionFooterMargin - PortraitRestMath.TouchTarget,
                    $"{count}: labels clear of the footer lane");
            }
        }
    }

    [Fact]
    public void FromFiveWideRowsTheGridShrinksToKeepTheFourWideMargins()
    {
        // 9 and 10 options want a five-wide top row; 11 (synthetic) a six-wide one.
        Assert.Equal(0.614232f, PortraitRestMath.OptionsGridScale(9, 1080f), 5);
        Assert.Equal(0.614232f, PortraitRestMath.OptionsGridScale(10, 1080f), 5);
        Assert.Equal(0.506691f, PortraitRestMath.OptionsGridScale(11, 1080f), 5);
        Assert.Equal(38.19f, PortraitRestMath.GridRowEdge(1080f), 2);

        Span<RestOptionsRow> rows = stackalloc RestOptionsRow[2];
        foreach ((int count, float scale) in new[] { (9, 0.614232f), (10, 0.614232f), (11, 0.506691f) })
        {
            PortraitRestMath.OptionsGrid(count, 1080f, 2340f, rows);
            Assert.Equal(scale, rows[0].Scale, 5);
            // The top row is drawn at exactly the four-wide row's margins.
            Assert.Equal(1003.6f, PortraitRestMath.NaturalRowWidth(PortraitRestMath.OptionsInRow(count, 0)) * rows[0].Scale, 1);
            Assert.Equal(PortraitRestMath.GridRowEdge(1080f), rows[0].X + (rows[0].Width * rows[0].Scale
                - PortraitRestMath.NaturalRowWidth(PortraitRestMath.OptionsInRow(count, 0)) * rows[0].Scale) * 0.5f, 1);
        }

        // Reported drawn sizes: 9/10 -> 151.6 x 100.0 u (52.6 x 34.7 pt) with a 29.5 u label; 11 -> 125.0 x 82.5 u.
        Assert.Equal(151.6f, PortraitRestMath.NativeOptionWidth * PortraitRestMath.OptionsGridScale(9, 1080f), 1);
        Assert.Equal(100.0f, PortraitRestMath.NativeOptionHeight * PortraitRestMath.OptionsGridScale(9, 1080f), 1);
        Assert.Equal(29.5f, PortraitRestMath.OptionLabelFontSize * PortraitRestMath.OptionsGridScale(9, 1080f), 1);
        Assert.Equal(125.0f, PortraitRestMath.NativeOptionWidth * PortraitRestMath.OptionsGridScale(11, 1080f), 1);
        Assert.Equal(82.5f, PortraitRestMath.NativeOptionHeight * PortraitRestMath.OptionsGridScale(11, 1080f), 1);
        Assert.Equal(24.3f, PortraitRestMath.OptionLabelFontSize * PortraitRestMath.OptionsGridScale(11, 1080f), 1);
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
