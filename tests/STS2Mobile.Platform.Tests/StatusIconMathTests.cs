using STS2Mobile.Platform;
using Xunit;

namespace STS2Mobile.Platform.Tests;

public class StatusIconMathTests
{
    private const float Width = 1080f;
    private const float Normal = Width * StatusIconMath.IconWidthShare;
    private const float HeroLeft = 47.4f;
    private const float HeroHitbox = 265.1f;
    private const float HeroContainer = HeroHitbox + StatusIconMath.ContainerPadding;

    [Fact]
    public void FiveHeroStatusesSitAtTheHitBoxLeft()
    {
        StatusIconLayout layout = StatusIconMath.Compute(5, HeroLeft, HeroHitbox, Width);
        Assert.Equal((6, 1, 5), (layout.Columns, layout.Rows, layout.Count));
        Assert.Equal(5f * Normal, layout.RowWidth, 0.01f);
        Assert.Equal(HeroLeft, layout.Left, 0.01f);
        Assert.Equal(0f, layout.LeftOverhang, 0.01f);
        Assert.Equal(0f, layout.RightOverhang, 0.01f);
    }

    [Fact]
    public void SevenHeroStatusesWrapSixPlusOneAndCentreTheSmallOverhang()
    {
        StatusIconLayout layout = StatusIconMath.Compute(7, HeroLeft, HeroHitbox, Width);
        Assert.Equal((6, 2), (layout.Columns, layout.Rows));
        Assert.Equal(6f * Normal, layout.RowWidth, 0.01f);
        Assert.Equal((6f * Normal - HeroContainer) * 0.5f, layout.LeftOverhang, 0.01f);
        Assert.Equal(HeroLeft - layout.LeftOverhang, layout.Left, 0.01f);
    }

    [Fact]
    public void FourteenHeroStatusesUseSevenColumnsAndStayOnCanvas()
    {
        StatusIconLayout layout = StatusIconMath.Compute(14, HeroLeft, HeroHitbox, Width);
        Assert.Equal((7, 2), (layout.Columns, layout.Rows));
        Assert.Equal(50f, StatusIconMath.MaxOverhang);
        Assert.True(layout.LeftOverhang <= StatusIconMath.MaxOverhang + 0.01f);
        Assert.True(layout.Left >= StatusIconMath.EdgeMargin - 0.01f);
    }

    [Fact]
    public void TwentyHeroStatusesShrinkToFiftyOverhangPerSide()
    {
        StatusIconLayout layout = StatusIconMath.Compute(20, HeroLeft, HeroHitbox, Width);
        Assert.Equal((10, 2), (layout.Columns, layout.Rows));
        Assert.Equal((HeroContainer + 100f) / 10f, layout.IconWidth, 0.01f);
        Assert.True(layout.IconWidth > Normal * StatusIconMath.MinIconScale);
        // Shrunk to 50 overhang a side, the canvas clamp then moves the block from -2.6 to the 8-unit margin.
        Assert.Equal(StatusIconMath.EdgeMargin, layout.Left, 0.01f);
        Assert.Equal(39.4f, layout.LeftOverhang, 0.01f);
        Assert.Equal(60.6f, layout.RightOverhang, 0.01f);
    }

    [Fact]
    public void ShrinkStopsAtTheFloorAndReportsTheRest()
    {
        StatusIconLayout layout = StatusIconMath.Compute(40, HeroLeft, HeroHitbox, Width);
        Assert.Equal(Normal * StatusIconMath.MinIconScale, layout.IconWidth, 0.01f);
        Assert.True(Math.Max(layout.LeftOverhang, layout.RightOverhang) > StatusIconMath.MaxOverhang);
        Assert.Equal(StatusIconMath.EdgeMargin, layout.Left, 0.01f);
    }

    [Fact]
    public void SmallEnemyStatusesStayAtNormalSizeWellInsideItsWideContainer()
    {
        StatusIconLayout five = StatusIconMath.Compute(5, 549.8f, 356.4f, Width);
        StatusIconLayout seven = StatusIconMath.Compute(7, 549.8f, 356.4f, Width);
        Assert.Equal((8, 1), (five.Columns, five.Rows));
        Assert.Equal((8, 1), (seven.Columns, seven.Rows));
        Assert.Equal(549.8f, five.Left, 0.01f);
        Assert.Equal(549.8f, seven.Left, 0.01f);
    }

    [Fact]
    public void CeremonialBeastTenStatusesFitOneNormalRow()
    {
        StatusIconLayout layout = StatusIconMath.Compute(10, 456.3f, 496.1f, Width);
        Assert.Equal((11, 1), (layout.Columns, layout.Rows));
        Assert.Equal(10f * Normal, layout.RowWidth, 0.01f);
        Assert.Equal(456.3f, layout.Left, 0.01f);
    }

    [Fact]
    public void TwoCloseNeighboursShrinkUntilTheyMeetAndNeverOverlap()
    {
        StatusIconLayout left = StatusIconMath.Compute(14, 47.4f, 265.1f, Width);
        StatusIconLayout right = StatusIconMath.Compute(14, 390f, 265.1f, Width);
        Assert.True(left.Left + left.RowWidth > right.Left);

        float gap = right.Left - (left.Left + left.RowWidth);
        StatusIconLayout leftFit = StatusIconMath.ApplyNeighbourLimits(left, 47.4f, 265.1f, Width,
            float.PositiveInfinity, gap);
        StatusIconLayout rightFit = StatusIconMath.ApplyNeighbourLimits(right, 390f, 265.1f, Width,
            gap, float.PositiveInfinity);

        Assert.True(leftFit.IconWidth < Normal);
        Assert.True(rightFit.IconWidth < Normal);
        Assert.True(leftFit.Left + leftFit.RowWidth <= rightFit.Left + 0.01f);
    }

    [Fact]
    public void NeighbourLimitsAreIndependentOfOrder()
    {
        StatusIconLayout a = StatusIconMath.Compute(20, 47.4f, 265.1f, Width);
        StatusIconLayout b = StatusIconMath.Compute(20, 500f, 265.1f, Width);
        float gap = b.Left - (a.Left + a.RowWidth);
        StatusIconLayout aFit = StatusIconMath.ApplyNeighbourLimits(a, 47.4f, 265.1f, Width, float.PositiveInfinity, gap);
        StatusIconLayout bFit = StatusIconMath.ApplyNeighbourLimits(b, 500f, 265.1f, Width, gap, float.PositiveInfinity);
        StatusIconLayout aAgain = StatusIconMath.ApplyNeighbourLimits(a, 47.4f, 265.1f, Width, float.PositiveInfinity, gap);
        Assert.Equal(aFit, aAgain);
        Assert.True(aFit.Left + aFit.RowWidth <= bFit.Left + 0.01f);
    }

    [Fact]
    public void FloorCannotAvoidTouchingSoBlocksShiftApartAndReportTheExcess()
    {
        StatusIconLayout left = StatusIconMath.Compute(40, 200f, 400f, Width);
        StatusIconLayout right = StatusIconMath.Compute(40, 380f, 400f, Width);
        Assert.True(left.Left + left.RowWidth > right.Left);

        float gap = right.Left - (left.Left + left.RowWidth);
        StatusIconLayout leftFit = StatusIconMath.ApplyNeighbourLimits(left, 200f, 400f, Width,
            float.PositiveInfinity, gap);
        StatusIconLayout rightFit = StatusIconMath.ApplyNeighbourLimits(right, 380f, 400f, Width,
            gap, float.PositiveInfinity);

        Assert.Equal(Normal * StatusIconMath.MinIconScale, leftFit.IconWidth, 0.01f);
        Assert.Equal(Normal * StatusIconMath.MinIconScale, rightFit.IconWidth, 0.01f);
        Assert.True(leftFit.RightOverhang > gap * 0.5f || leftFit.Left + leftFit.RowWidth > rightFit.Left);
        Assert.True(rightFit.LeftOverhang > gap * 0.5f || rightFit.RightOverhang > gap * 0.5f);
        Assert.True(leftFit.Left >= StatusIconMath.EdgeMargin - 0.01f);
        Assert.True(rightFit.Left + rightFit.RowWidth <= Width - StatusIconMath.EdgeMargin + 0.01f);
    }

    [Fact]
    public void OuterSideKeepsItsFiftyLimitWhenTheInnerGapIsTight()
    {
        StatusIconLayout layout = StatusIconMath.Compute(20, 47.4f, 265.1f, Width);
        StatusIconLayout fit = StatusIconMath.ApplyNeighbourLimits(layout, 47.4f, 265.1f, Width,
            float.PositiveInfinity, 0f);
        Assert.True(fit.LeftOverhang <= 50f + 0.01f);
        Assert.True(fit.Left >= StatusIconMath.EdgeMargin - 0.01f);
    }
}
