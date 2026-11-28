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

    [Fact]
    public void SevenRowsCloseTheColumnWithoutTheQuitGap()
    {
        // Quit is hidden on iOS: seven rows and six gaps fill the column, so no empty slot is left where it was.
        MainMenuColumn seven = PortraitMainMenuMath.Column(7, NineNineteenFive, Cap);
        Assert.Equal(979f, seven.Height, 3);
        Assert.Equal(1030.5f, seven.Top, 3);
        Assert.Equal(2009.5f, seven.Bottom, 3);
        Assert.Equal(7f * (PortraitMainMenuMath.ButtonHeight + PortraitMainMenuMath.Separation)
                     - PortraitMainMenuMath.Separation, seven.Height, 3);
    }

    [Fact]
    public void BackdropFillsTallPhoneHeightAndCropsTheMasterAroundTheTower()
    {
        MainMenuFraming framing = PortraitMainMenuMath.Backdrop(1080f, NineNineteenFive);
        Assert.Equal(NineNineteenFive / 2304f, framing.Scale, 4);
        // 1063.4 master units are visible (1080 / scale), so the camera's left edge is 919.3.
        Assert.Equal(-933.672f, framing.Left, 2);

        MainMenuRect stage = PortraitMainMenuMath.BackdropNativeStage(framing);
        Assert.Equal(-774.22f, stage.X, 2);
        Assert.Equal(23.36f, stage.Y, 2);
        Assert.Equal(2600f, stage.Width, 2);
        Assert.Equal(1218.75f, stage.Height, 2);

        MainMenuRect water = PortraitMainMenuMath.BackdropWater(framing);
        Assert.Equal(-933.672f, water.X, 2);
        Assert.Equal(1054.22f, water.Y, 2);
        Assert.Equal(NineNineteenFive, water.Bottom, 2);
        Assert.Equal(PortraitMainMenuMath.WaterTextureHeight * framing.Scale, water.Height, 2);
    }

    [Fact]
    public void BackdropKeepsTheTowerBesideTheTitleAtNineSixteen()
    {
        MainMenuFraming framing = PortraitMainMenuMath.Backdrop(1080f, NineSixteen);
        Assert.Equal(NineSixteen / 2304f, framing.Scale, 4);
        // The frame is 1296 master units wide, so the tower's left silhouette lands at 729 of the 1080 canvas.
        Assert.Equal(729.17f, framing.Left + PortraitMainMenuMath.BackdropTowerLeft * framing.Scale, 2);
        MainMenuRect water = PortraitMainMenuMath.BackdropWater(framing);
        Assert.Equal(865f, water.Y, 2);
        Assert.Equal(NineSixteen, water.Bottom, 2);
    }

    [Fact]
    public void TitleSitsLeftOfTheTowerAndBelowTheSafeCap()
    {
        MainMenuFraming tall = PortraitMainMenuMath.Backdrop(1080f, NineNineteenFive);
        MainMenuLogoPlacement logo = PortraitMainMenuMath.BackdropLogo(1080f, NineNineteenFive, Cap, tall);
        Assert.Equal(44.31f, logo.Left, 2);
        Assert.Equal(319.09f, logo.Top, 2);
        // The framed tower leaves 698.5 units before the reference's 10 unit gap, less than the title's 717.2 preferred width.
        Assert.Equal(698.55f, logo.Width, 2);
        Assert.True(logo.Left + logo.Width < tall.Left + PortraitMainMenuMath.BackdropTowerLeft * tall.Scale);

        MainMenuFraming square = PortraitMainMenuMath.Backdrop(1080f, NineSixteen);
        MainMenuLogoPlacement narrow = PortraitMainMenuMath.BackdropLogo(1080f, NineSixteen, 0f, square);
        Assert.Equal(261.82f, narrow.Top, 2);
        Assert.Equal(657.17f, narrow.Width, 2);
    }

    [Fact]
    public void TitleDropsBelowADeepSafeCap()
    {
        MainMenuFraming framing = PortraitMainMenuMath.Backdrop(1080f, 1500f);
        MainMenuLogoPlacement logo = PortraitMainMenuMath.BackdropLogo(1080f, 1500f, 300f, framing);
        Assert.Equal(300f + logo.Left, logo.Top, 2);
        Assert.True(logo.Top > 1500f * PortraitMainMenuMath.LogoTop / PortraitMainMenuMath.LogoReferenceHeight);
    }

    [Fact]
    public void BackdropNeverShowsPastTheMastersOwnEdges()
    {
        // A canvas wider than the master (3000 x 2340 wants 2954 master units) shows the whole master from its left edge, so
        // no uncovered band appears at the sides; the camera clamps instead of centring past the edge.
        MainMenuFraming framing = PortraitMainMenuMath.Backdrop(3000f, NineNineteenFive);
        Assert.Equal(0f, framing.Left, 2);
        MainMenuRect stage = PortraitMainMenuMath.BackdropNativeStage(framing);
        Assert.Equal(PortraitMainMenuMath.BackdropNativeLeft * framing.Scale, stage.X, 2);
        Assert.Equal(PortraitMainMenuMath.BackdropMasterSize * framing.Scale, PortraitMainMenuMath.BackdropWater(framing).Width, 2);
    }
}
