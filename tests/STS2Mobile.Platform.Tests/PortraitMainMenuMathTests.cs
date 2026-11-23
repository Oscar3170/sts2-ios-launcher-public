namespace STS2Mobile.Platform.Tests;

public class PortraitMainMenuMathTests
{
    private const float NineSixteen = 1920f;
    private const float NineNineteenFive = 2340f;
    private const float Cap = 162f;

    [Fact]
    public void ColumnCentresSixRowsAboveTheMiddle()
    {
        MainMenuColumn column = PortraitMainMenuMath.Column(6, NineSixteen, Cap);
        Assert.Equal(837f, column.Height, 3);
        Assert.Equal(891.5f, column.Top, 3);
        Assert.Equal(127f, column.ButtonHeight);
        Assert.Equal(15f, column.Separation);
    }

    [Fact]
    public void ColumnRisesClearOfTheHomeIndicatorWithEveryRowShown()
    {
        MainMenuColumn column = PortraitMainMenuMath.Column(8, NineSixteen, Cap);
        Assert.Equal(1121f, column.Height, 3);
        Assert.Equal(NineSixteen - PortraitMainMenuMath.ColumnBottomMargin, column.Bottom, 3);
    }

    [Fact]
    public void TallerCanvasKeepsTheRowsInTheGamesOwnBand()
    {
        MainMenuColumn column = PortraitMainMenuMath.Column(6, NineNineteenFive, Cap);
        Assert.Equal(1101.5f, column.Top, 3);
        Assert.Equal(1938.5f, column.Bottom, 3);
    }

    [Fact]
    public void ColumnNeverRisesPastTheSafeCapAndLogo()
    {
        MainMenuColumn column = PortraitMainMenuMath.Column(1, 300f, Cap);
        Assert.Equal(Cap + 200f, column.Top);
    }

    [Fact]
    public void ZeroRowsStillLaysOutOneRow()
    {
        Assert.Equal(PortraitMainMenuMath.ButtonHeight, PortraitMainMenuMath.Column(0, NineSixteen, Cap).Height);
    }

    [Fact]
    public void OutlineGrowsWithTheFont()
    {
        Assert.Equal(28, PortraitMainMenuMath.Outline(18, 32, 50));
        Assert.Equal(28, PortraitMainMenuMath.Outline(28, 50, 50));
        Assert.Equal(1, PortraitMainMenuMath.Outline(0, 32, 50));
    }

    [Fact]
    public void ContinueInfoTakesTheReferenceScaleButFitsTheCanvas()
    {
        Assert.Equal(1.607f, PortraitMainMenuMath.ContinueInfoScale(448f, 1080f), 3);
        Assert.Equal(998f / 1200f, PortraitMainMenuMath.ContinueInfoScale(1200f, 1080f), 3);
    }

    [Fact]
    public void ProfileTextIsDrawnLargeEnoughToRead()
    {
        // 32 units are 11.1 points (2.88 units to a point).
        Assert.True(PortraitMainMenuMath.ProfileFontSize / (1080f / 375f) >= 11f);
    }
}
