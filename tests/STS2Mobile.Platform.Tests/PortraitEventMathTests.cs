namespace STS2Mobile.Platform.Tests;

public class PortraitEventMathTests
{
    [Theory]
    [InlineData(1080f, 1920f, true)]   // 9:16, the compact shape
    [InlineData(1080f, 2340f, false)]  // 9:19.5, a tall phone
    [InlineData(1080f, 1919f, true)]
    [InlineData(1080f, 1921f, false)]
    public void CompactIsThe9By16Shape(float width, float height, bool compact)
    {
        Assert.Equal(compact, PortraitEventMath.IsCompact(width, height));
    }

    [Fact]
    public void TallPhoneReservesFour53PointRowsLow()
    {
        EventChoiceBand band = PortraitEventMath.ChoiceBand(canvasHeight: 2340f, visibleTop: 390f, compact: false, tight: false);
        Assert.Equal(154f, band.RowHeight);
        Assert.Equal(15, band.Gap);
        Assert.Equal(2190f, band.Bottom);        // 150 units above the canvas bottom
        Assert.Equal(1516f, band.Top);           // 4 x 154 + 3 x 15 + 13 under them
        Assert.Equal(PortraitEventMath.ChoiceRowsHeight(154f, 15, 4) + 13f, band.Bottom - band.Top);
    }

    [Fact]
    public void CompactPhoneUses46PointRowsAndKeepsArtAboveThem()
    {
        EventChoiceBand band = PortraitEventMath.ChoiceBand(canvasHeight: 1920f, visibleTop: 364f, compact: true, tight: false);
        Assert.Equal(132f, band.RowHeight);   // 46 points
        Assert.Equal(12, band.Gap);
        Assert.Equal(1193f, band.Top);        // 4 x 132 + 3 x 12 + 13
        float art = PortraitEventMath.ArtBottom(band.Top) - 364f;
        Assert.Equal(803f, art);              // 279 points
        Assert.True(art > 250f, "the compact shape keeps a usable illustration");
    }

    [Fact]
    public void TighterRowsAreOneTouchTarget()
    {
        EventChoiceBand band = PortraitEventMath.ChoiceBand(canvasHeight: 2340f, visibleTop: 390f, compact: true, tight: true);
        Assert.Equal(127f, band.RowHeight);
        Assert.Equal(9, band.Gap);
        Assert.Equal(2190f - 4 * 127f - 3 * 9f - 13f, band.Top);
    }

    [Fact]
    public void BandNeverRisesAboveTheVisibleTop()
    {
        EventChoiceBand band = PortraitEventMath.ChoiceBand(canvasHeight: 1920f, visibleTop: 1100f, compact: false, tight: false);
        Assert.Equal(1220f, band.Top);   // visibleTop + the smallest art band
    }

    [Fact]
    public void TextSlotIs18PercentOfTheCanvasAndBottomAnchoredToTheChoices()
    {
        EventChoiceBand band = PortraitEventMath.ChoiceBand(2340f, 390f, compact: false, tight: false);
        EventTextSlot slot = PortraitEventMath.TextSlot(2340f, 390f, band.Top);
        Assert.Equal(421f, slot.Height);         // round(0.18 x 2340)
        Assert.Equal(band.Top - 35f, slot.Bottom);
        Assert.Equal(1060f, slot.Top);
        Assert.True(slot.Top > 390f);
        Assert.True(slot.Top < PortraitEventMath.ArtBottom(band.Top), "the slot sits over the art's lower part");

        EventTextSlot compact = PortraitEventMath.TextSlot(1920f, 364f, 1193f);
        Assert.Equal(346f, compact.Height);      // round(0.18 x 1920) = 120 points
        Assert.Equal(812f, compact.Top);
    }

    [Fact]
    public void TextSlotShrinksRatherThanCoverTheHeader()
    {
        EventTextSlot slot = PortraitEventMath.TextSlot(canvasHeight: 1920f, visibleTop: 900f, choiceTop: 1154f);
        Assert.Equal(900f, slot.Top);
        Assert.Equal(1119f, slot.Bottom);
        Assert.Equal(219f, slot.Height);
    }

    [Fact]
    public void ArtCropCoversTheFrameAndKeepsTheSubjectAtTheFocusPoint()
    {
        EventArtCrop crop = PortraitEventMath.ArtCrop(1080f, 1083f, 2560f, 1200f, PortraitEventMath.DefaultFocus);
        float width = 2560f * crop.Scale;
        float height = 1200f * crop.Scale;
        Assert.True(width >= 1080f - 0.01f && height >= 1083f - 0.01f, "cover on both axes");
        Assert.True(crop.X <= 0f && crop.X >= 1080f - width, "no blank beside the art");
        Assert.True(crop.Y <= 0f && crop.Y >= 1083f - height, "no blank above or below the art");
        Assert.Equal(540f, crop.X + width * PortraitEventMath.DefaultFocus.X, 2);
        Assert.Equal(1083f * PortraitEventMath.ArtFocusY, crop.Y + height * PortraitEventMath.DefaultFocus.Y, 2);
    }

    [Fact]
    public void ArtCropOfTheCompactShapeZoomsLessThanTheTallOne()
    {
        EventArtCrop tall = PortraitEventMath.ArtCrop(1080f, 1100f, 2560f, 1200f, PortraitEventMath.DefaultFocus);
        EventArtCrop compact = PortraitEventMath.ArtCrop(1080f, 799f, 2560f, 1200f, PortraitEventMath.DefaultFocus);
        Assert.True(tall.Scale > compact.Scale, "the shorter frame needs less zoom to cover its height");
        Assert.True(2560f * compact.Scale >= 1080f && 1200f * compact.Scale >= 799f);
        Assert.Equal(540f, compact.X + 2560f * compact.Scale * PortraitEventMath.DefaultFocus.X, 2);
    }

    [Fact]
    public void ArtCropIgnoresAnImpossibleFocusInsteadOfDividingByZero()
    {
        EventArtCrop crop = PortraitEventMath.ArtCrop(1000f, 500f, 1000f, 500f, new EventFocus(0.5f, 0f));
        Assert.True(float.IsFinite(crop.Scale) && float.IsFinite(crop.X) && float.IsFinite(crop.Y));
        Assert.True(1000f * crop.Scale >= 1000f - 0.01f && 500f * crop.Scale >= 500f - 0.01f, "still covers the frame");
    }

    [Theory]
    [InlineData("byrdonis_nest", 0.33f, 0.53f)]
    [InlineData("sunken_statue", 0.30f, 0.50f)]
    [InlineData(null, 0.30f, 0.50f)]
    public void ArtFocusComesFromTheSceneName(string? name, float x, float y)
    {
        EventFocus focus = PortraitEventMath.ArtFocus(name);
        Assert.Equal(x, focus.X);
        Assert.Equal(y, focus.Y);
    }

    [Fact]
    public void FadeStartsAboveTheArtBottomAndRunsToTheCanvasEnd()
    {
        float artBottom = PortraitEventMath.ArtBottom(1516f);
        Assert.Equal(1490f, artBottom);
        Assert.Equal(1490f - 2340f * 0.145f, PortraitEventMath.FadeTop(artBottom, 2340f), 2);
    }

    [Fact]
    public void TextPanelFitsWhenItCanAndScrollsWhenItCannot()
    {
        EventTextPanel fits = PortraitEventMath.TextPanel(300f, 400f);
        Assert.Equal(300f, fits.Height);
        Assert.False(fits.Scrolls);

        EventTextPanel scrolls = PortraitEventMath.TextPanel(500f, 400f);
        Assert.Equal(400f, scrolls.Height);
        Assert.True(scrolls.Scrolls);
    }

    [Fact]
    public void CopyShrinksAFewUnitsAtATimeBeforeItScrolls()
    {
        Assert.Equal(36, PortraitEventMath.NextTextFont(39));
        Assert.Equal(28, PortraitEventMath.NextTextFont(30));
        Assert.Equal(28, PortraitEventMath.NextTextFont(28));
        Assert.Equal(52, PortraitEventMath.TitleFont(39));
        Assert.Equal(32, PortraitEventMath.SharedFont(39));
    }

    [Fact]
    public void OptionRowsGrowPastTheMinimumOnlyForTallerText()
    {
        Assert.Equal(154f, PortraitEventMath.OptionRowHeight(70f, 15f, 154f));
        Assert.Equal(170f, PortraitEventMath.OptionRowHeight(140f, 15f, 154f));
        Assert.Equal(170f, PortraitEventMath.UniformRowHeight(new[] { 150f, 170f, 130f }, 154f));
        Assert.Equal(154f, PortraitEventMath.UniformRowHeight(new[] { 120f, 130f }, 154f));
        Assert.Equal(0f, PortraitEventMath.ChoiceRowsHeight(154f, 15, 0));
        Assert.Equal(661f, PortraitEventMath.ChoiceRowsHeight(154f, 15, 4));
        Assert.Equal(954f, PortraitEventMath.OptionTextWidth(1008f));
    }

    [Fact]
    public void PointsConversionMatchesTheModsTemplate()
    {
        // The reference's 60-unit event copy is 13.4 points, which is 39 units on this canvas; its rows and slop likewise.
        Assert.Equal(PortraitEventMath.TextFont, (int)MathF.Round(60f * PortraitEventMath.TemplateToLauncher));
        Assert.Equal(PortraitEventMath.RowHeightTall, MathF.Round(240f * PortraitEventMath.TemplateToLauncher), 2);
        Assert.Equal(PortraitEventMath.RowHeightCompact, MathF.Round(206f * PortraitEventMath.TemplateToLauncher), 2);
        Assert.Equal(PortraitEventMath.RowHeightTight, MathF.Round(198f * PortraitEventMath.TemplateToLauncher), 2);
        Assert.Equal(PortraitEventMath.DragThreshold, MathF.Round(28f * PortraitEventMath.TemplateToLauncher), 2);
    }
}
