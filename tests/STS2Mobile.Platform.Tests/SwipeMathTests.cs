using STS2Mobile.Platform;
using Xunit;

namespace STS2Mobile.Platform.Tests;

public class SwipeMathTests
{
    [Theory]
    [InlineData(0f, 0f, false)]
    [InlineData(0f, SwipeMath.GridDistance - 0.01f, false)]
    [InlineData(0f, SwipeMath.GridDistance, true)]
    [InlineData(0f, -SwipeMath.GridDistance, true)]
    [InlineData(0f, 200f, true)]
    public void AGridDragStartsAtTheDistance(float dx, float dy, bool expected) =>
        Assert.Equal(expected, SwipeMath.Along(dy, dx, SwipeMath.GridDistance, SwipeMath.Dominance));

    [Theory]
    // 24 units down with exactly 20 sideways is the dominance boundary: 20 * 1.2 == 24.
    [InlineData(20f, 24f, true)]
    [InlineData(-20f, 24f, true)]
    // A little more sideways than that is a sideways drag, not a scroll.
    [InlineData(21f, 24f, false)]
    [InlineData(-21f, SwipeMath.GridDistance, false)]
    // A wide, shallow drag never scrolls the grid even though it moves far.
    [InlineData(120f, 30f, false)]
    [InlineData(-120f, -30f, false)]
    // A vertical drag of any sideways amount stays vertical when it is the bigger movement.
    [InlineData(10f, 40f, true)]
    [InlineData(-10f, -40f, true)]
    public void AGridDragMustBeMostlyVertical(float dx, float dy, bool expected) =>
        Assert.Equal(expected, SwipeMath.Along(dy, dx, SwipeMath.GridDistance, SwipeMath.Dominance));

    [Theory]
    // A header row reads the same rule along its own axis: a sideways row at 28 units, a mostly-vertical drag on it is a tap.
    [InlineData(28f, 0f, true)]
    [InlineData(-28f, 0f, true)]
    [InlineData(28f, 23.33f, true)]
    [InlineData(28f, 24f, false)]
    [InlineData(27.9f, 0f, false)]
    [InlineData(0f, 40f, false)]
    public void AHeaderRowDragMustBeMostlyAlongTheRow(float along, float across, bool expected) =>
        Assert.Equal(expected, SwipeMath.Along(along, across, 28f, SwipeMath.Dominance));

    // The event's copy and choices read only the vertical movement (PhoneDragScroll passes the distance from the start),
    // so a wide drag that also moves 18 units up still scrolls the choices and picks nothing.
    [Theory]
    [InlineData(17.99f, false)]
    [InlineData(18f, true)]
    [InlineData(-18f, true)]
    [InlineData(-40f, true)]
    [InlineData(0f, false)]
    public void AnEventDragNeedsOnlyTheVerticalDistance(float dy, bool expected) =>
        Assert.Equal(expected, SwipeMath.Vertical(dy, PortraitEventMath.DragThreshold));

    [Theory]
    [InlineData(0f, 0f, false)]
    [InlineData(SwipeMath.ScreenDistance - 0.01f, 0f, false)]
    [InlineData(SwipeMath.ScreenDistance, 0f, true)]
    [InlineData(-SwipeMath.ScreenDistance, 0f, true)]
    [InlineData(0f, -SwipeMath.ScreenDistance, true)]
    // 17, 17 is 24.04 units: a diagonal swipe counts too.
    [InlineData(17f, 17f, true)]
    // 17, 16 is 23.3 units: still a tap.
    [InlineData(17f, 16f, false)]
    [InlineData(-17f, -16f, false)]
    public void AMapOrLootDragCountsInAnyDirection(float dx, float dy, bool expected) =>
        Assert.Equal(expected, SwipeMath.AnyDirection(dx, dy, SwipeMath.ScreenDistance));

    [Fact]
    public void TheDistancesKeepTheTestedThresholds()
    {
        Assert.Equal(24f, SwipeMath.GridDistance);
        Assert.Equal(1.2f, SwipeMath.Dominance);
        Assert.Equal(24f, SwipeMath.ScreenDistance);
        Assert.Equal(18f, PortraitEventMath.DragThreshold);
        Assert.Equal(23f, SelectConfirmMath.ConfirmSlop);
    }
}
