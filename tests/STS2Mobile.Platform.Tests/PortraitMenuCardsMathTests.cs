using Xunit;

namespace STS2Mobile.Platform.Tests;

public class PortraitMenuCardsMathTests
{
    [Fact]
    public void LinesAreBalancedWithTheEarlierLinesTakingTheExtraCard()
    {
        Assert.Equal(new[] { 0, 2, 4 }, PortraitMenuCardsMath.LineStarts(4));
        Assert.Equal(new[] { 0, 3, 5 }, PortraitMenuCardsMath.LineStarts(5));
        Assert.Equal(new[] { 0, 3, 6 }, PortraitMenuCardsMath.LineStarts(6));
        Assert.Equal(new[] { 0, 3, 5, 7 }, PortraitMenuCardsMath.LineStarts(7));
        Assert.Equal(new[] { 0, 3 }, PortraitMenuCardsMath.LineStarts(3));
    }

    [Fact]
    public void EmptyAndSingleCountsStillReportOneLine()
    {
        Assert.Equal(new[] { 0, 0 }, PortraitMenuCardsMath.LineStarts(0));
        Assert.Equal(new[] { 0, 1 }, PortraitMenuCardsMath.LineStarts(1));
    }

    [Fact]
    public void CellReportsTheLineAndColumn()
    {
        int[] starts = PortraitMenuCardsMath.LineStarts(5);
        Assert.Equal((0, 0), PortraitMenuCardsMath.Cell(starts, 0));
        Assert.Equal((0, 2), PortraitMenuCardsMath.Cell(starts, 2));
        Assert.Equal((1, 0), PortraitMenuCardsMath.Cell(starts, 3));
        Assert.Equal((1, 1), PortraitMenuCardsMath.Cell(starts, 4));
    }

    [Fact]
    public void NearestInLineClampsToAShorterLine()
    {
        int[] starts = PortraitMenuCardsMath.LineStarts(5);
        Assert.Equal(3, PortraitMenuCardsMath.NearestInLine(starts, 1, 0));
        Assert.Equal(4, PortraitMenuCardsMath.NearestInLine(starts, 1, 7));
        Assert.Equal(0, PortraitMenuCardsMath.NearestInLine(starts, 0, -1));
    }

    [Fact]
    public void FourModeCardsAreTwoLinesSizedForAFullLineOfThree()
    {
        MenuCardLines plan = PortraitMenuCardsMath.Plan(
            4, 1680f, 252f, 3388f, 340f, 705f);
        Assert.Equal(2, plan.Lines);
        Assert.Equal(new[] { 0, 2, 4 }, plan.Starts);
        // 3 x 340 + 2 x 18 = 1056 across, so (1680 - 80) / 1056 is the width limit and the height limit is larger.
        Assert.Equal((1680f - 80f) / 1056f, plan.Scale, 4);
        Assert.Equal(1056f, plan.Width, 2);
        Assert.Equal(2f * 705f + 18f, plan.Height, 2);
        Assert.Equal(252f + 100f + (3388f - 430f - plan.Height * plan.Scale) * 0.5f, plan.Top, 2);
    }

    [Fact]
    public void RoomBelowTheLastLineGrowsTheBlockAndCentresItHigher()
    {
        MenuCardLines without = PortraitMenuCardsMath.Plan(4, 1680f, 252f, 3388f, 368f, 490f);
        MenuCardLines with = PortraitMenuCardsMath.Plan(4, 1680f, 252f, 3388f, 368f, 490f, 200f);
        Assert.Equal(without.Scale, with.Scale, 4);
        Assert.Equal(without.Height + PortraitMenuCardsMath.Gap + 200f, with.Height, 2);
        Assert.Equal(without.Top - (PortraitMenuCardsMath.Gap + 200f) * without.Scale * 0.5f, with.Top, 2);
    }

    [Fact]
    public void SixCardsMakeTwoFullLines()
    {
        MenuCardLines plan = PortraitMenuCardsMath.Plan(6, 1680f, 252f, 3388f, 340f, 705f);
        Assert.Equal(2, plan.Lines);
        Assert.Equal(new[] { 0, 3, 6 }, plan.Starts);
    }

    [Fact]
    public void CellOffsetCentresTheDrawnArtInItsCell()
    {
        int[] starts = PortraitMenuCardsMath.LineStarts(4);
        // A 340 wide, 705 tall drawn box at (11, -80) in a 340 x 705 cell: the art's top must land on the cell top.
        (float x0, float y0) = PortraitMenuCardsMath.CellOffset(starts, 0, 340f, 705f, 18f, 340f, 705f, 11f, -80f);
        (float x1, float y1) = PortraitMenuCardsMath.CellOffset(starts, 1, 340f, 705f, 18f, 340f, 705f, 11f, -80f);
        Assert.Equal(-360f, x0, 2);
        Assert.Equal(-2f, x1, 2);
        Assert.Equal(80f, y0, 2);
        Assert.Equal(80f, y1, 2);
        (float x2, float y2) = PortraitMenuCardsMath.CellOffset(starts, 2, 340f, 705f, 18f, 340f, 705f, 11f, -80f);
        Assert.Equal(x0, x2, 2);
        Assert.Equal(705f + 18f + 80f, y2, 2);
    }

    [Fact]
    public void ATinyBandKeepsTheMinimumScale()
    {
        MenuCardLines plan = PortraitMenuCardsMath.Plan(4, 200f, 0f, 100f, 340f, 705f);
        Assert.Equal(PortraitMenuCardsMath.MinScale, plan.Scale, 4);
    }
}
