namespace STS2Mobile.Platform.Tests;

public class PortraitOrbMathTests
{
    // The two launcher-host portrait canvases: the 9:19.5 iPhone (1080 x 2340, a 162-unit safe cap) and the 9:16 one
    // (1080 x 1920, the 136-unit cap floor). Their bars come from PortraitCombatMath.ComputeCombatBands.
    private const float Width = 1080f;
    private const float TallBarsEnd = 612f;      // cap 162 + RunContentTop 450
    private const float TallRailTop = 1373.7f;
    private const float CompactBarsEnd = 586f;   // cap 136 + RunContentTop 450
    private const float CompactStoneBottom = 364f; // cap 136 + TopBarHeight 228
    private const float CompactRailTop = 1028.3f;

    private static PortraitOrbArcPlan TallPlan(int count, float ownerCenterX = 220f, float ownerTopY = 940f,
        float labelWidth = 0f, bool single = false, float laneLeft = 0f, float laneRight = 0f) =>
        PortraitOrbMath.ComputeOrbArc(count, ownerCenterX, ownerTopY, Width, TallBarsEnd, TallRailTop, labelWidth,
            single, laneLeft, laneRight);

    private static PortraitOrbArcPlan CompactPlan(int count, float ownerCenterX = 220f, float ownerTopY = 640f,
        float labelWidth = 0f, bool single = false) =>
        PortraitOrbMath.ComputeOrbArc(count, ownerCenterX, ownerTopY, Width, CompactStoneBottom, CompactRailTop,
            labelWidth, single);

    private static void AssertInsideCanvas(in PortraitOrbArcPlan plan)
    {
        for (int i = 0; i < plan.Count; i++)
        {
            (float left, float top, float right, float bottom) = plan.IconBox(i);
            Assert.True(left >= PortraitOrbMath.CanvasEdgeMargin - 0.01f, $"orb {i} left {left}");
            Assert.True(right <= Width - PortraitOrbMath.CanvasEdgeMargin + 0.01f, $"orb {i} right {right}");
            Assert.True(top >= 0f, $"orb {i} top {top}");
            Assert.True(bottom <= CompactRailTop, $"orb {i} bottom {bottom}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OneToThreeOrbsKeepTheNativeRadiusArc(int count)
    {
        PortraitOrbArcPlan plan = TallPlan(count);
        Assert.Equal(PortraitOrbArcKind.Native, plan.Kind);
        Assert.Equal(PortraitOrbMath.NativeRadius, plan.Radius);
        Assert.Equal(count, plan.Count);
        Assert.Equal(0f, plan.Pitch);
        Assert.Equal(PortraitOrbMath.NativeTouchWorld, plan.TouchWorld);
        // Index 0 is the rightmost/front orb, the last the leftmost/back one, exactly as the game orders them.
        for (int i = 1; i < count; i++)
            Assert.True(plan.Position(i - 1).X > plan.Position(i).X);
    }

    [Fact]
    public void SingleOrbSitsAWholeRadiusAboveItsAnchor()
    {
        PortraitOrbArcPlan plan = TallPlan(1);
        (float x, float y) = plan.Position(0);
        Assert.Equal(plan.CenterX, x, 0.001f);
        Assert.Equal(plan.CenterY - PortraitOrbMath.NativeRadius, y, 0.001f);
    }

    [Fact]
    public void FourOrbsTakeOneWideArcThatNeverTouchesItsNeighboursArt()
    {
        PortraitOrbArcPlan plan = TallPlan(4);
        Assert.Equal(PortraitOrbArcKind.Single, plan.Kind);
        Assert.Equal(PortraitOrbMath.SingleTouchWorld, plan.TouchWorld);
        // The art's own width plus a gap: no two four-orb icons overlap.
        Assert.True(plan.Pitch >= PortraitOrbMath.OrbArtUnits * PortraitOrbMath.OrbWorldScale + 9.99f);
        Assert.True(plan.Pitch >= plan.IconWorld);
        // One shallow arc: even spacing, the ends level, the middle higher.
        float first = plan.Position(0).X;
        float step = first - plan.Position(1).X;
        for (int i = 1; i < plan.Count; i++)
            Assert.Equal(step, plan.Position(i - 1).X - plan.Position(i).X, 0.01f);
        Assert.Equal(plan.Position(0).Y, plan.Position(3).Y, 0.01f);
        Assert.True(plan.Position(1).Y < plan.Position(0).Y - 40f);
        Assert.True(plan.Position(1).Y > plan.Position(0).Y - plan.Depth);
    }

    [Theory]
    [InlineData(5, 44f)]
    [InlineData(6, 44f)]
    [InlineData(7, 44f)]
    [InlineData(8, 44f)]
    [InlineData(9, 44f)]
    [InlineData(10, 44f)]
    public void FiveToTenOrbsShareOneDenseQueuePitch(int count, float expectedPitch)
    {
        PortraitOrbArcPlan plan = TallPlan(count);
        Assert.Equal(PortraitOrbArcKind.Single, plan.Kind);
        Assert.Equal(expectedPitch, plan.Pitch, 0.01f);
        Assert.Equal(PortraitOrbMath.DenseTouchWorld, plan.TouchWorld);
        AssertInsideCanvas(plan);
        // Ten points on the 1080 canvas (2.88 units each): the dense queue's values must never go under it.
        Assert.True(plan.ValueWorldHeight >= 10f * (1080f / 375f) - 0.2f, "values reach 10 points");
    }

    [Fact]
    public void TenOrbsStayUnderTheSafeWidthAndInsideTheCanvas()
    {
        // The whole queue at the dense pitch is 9 x 44 wide; the clamp centres it so no icon leaves the 8-unit margin.
        PortraitOrbArcPlan plan = TallPlan(10);
        Assert.Equal(44f, plan.Pitch, 0.01f);
        AssertInsideCanvas(plan);
        var (left, _, _, _) = plan.IconBox(9);
        var (_, _, right, _) = plan.IconBox(0);
        Assert.True(left >= PortraitOrbMath.CanvasEdgeMargin - 0.01f);
        Assert.True(right <= Width - PortraitOrbMath.CanvasEdgeMargin + 0.01f);
    }

    [Fact]
    public void AQueueIsCappedByTheSafeWidthRatherThanOverflowing()
    {
        // A canvas narrower than the phone's (a uniform scale of the same layout) caps the pitch instead of spilling.
        PortraitOrbArcPlan plan = PortraitOrbMath.ComputeOrbArc(10, 220f, 940f, 720f, TallBarsEnd, TallRailTop,
            labelWorldWidth: 120f);
        Assert.Equal((720f - PortraitOrbMath.SingleArcPitchMargin) / 9f, plan.Pitch, 0.01f);
        for (int i = 0; i < plan.Count; i++)
        {
            (float left, _, float right, _) = plan.IconBox(i);
            Assert.True(left >= PortraitOrbMath.CanvasEdgeMargin - 0.01f);
            Assert.True(right <= 720f - PortraitOrbMath.CanvasEdgeMargin + 0.01f);
        }
    }

    [Fact]
    public void DenseQueueFloatsAboveTheOwnerAndClearOfTheRunBars()
    {
        PortraitOrbArcPlan plan = TallPlan(10);
        // The arc's ends rest above the owner's head, its top stays below the relic band on a tall phone.
        (_, float top, _, float bottom) = plan.IconBox(4);
        Assert.True(bottom < 940f - 80f, $"bottom {bottom} above the head");
        Assert.True(top >= TallBarsEnd + PortraitOrbMath.CanvasEdgeMargin - 0.01f, $"top {top} clear of the bars");
        Assert.Equal(10, plan.Count);
    }

    [Fact]
    public void CompactQueueRisesOverTheRelicBandButNeverIntoTheStone()
    {
        // At 9:16 the ten-orb queue may overlap the relic strip (decision 10) but its top icon stays below the stone.
        PortraitOrbArcPlan plan = CompactPlan(10);
        float highest = float.MaxValue;
        for (int i = 0; i < plan.Count; i++)
            highest = MathF.Min(highest, plan.IconBox(i).Top);
        Assert.True(highest >= CompactStoneBottom, $"highest {highest} under the stone at {CompactStoneBottom}");
        Assert.True(highest < CompactBarsEnd, "the compact queue is allowed over the relic band");
    }

    [Fact]
    public void NativeArcOnTheCompactCanvasKeepsItsTopUnderTheStone()
    {
        PortraitOrbArcPlan plan = CompactPlan(3);
        for (int i = 0; i < plan.Count; i++)
            Assert.True(plan.IconBox(i).Top >= CompactStoneBottom);
        Assert.True(plan.Position(0).Y < CompactRailTop);
    }

    [Fact]
    public void ValueLabelsWidenTheDensePitchWhenTheyDoNotFit()
    {
        // A wide value (a large evoke number) widens the queue; the canvas cap still wins for ten orbs.
        PortraitOrbArcPlan wide = TallPlan(5, labelWidth: 80f);
        Assert.Equal(88f, wide.Pitch, 0.01f);
        PortraitOrbArcPlan capped = TallPlan(10, labelWidth: 120f);
        Assert.Equal((Width - PortraitOrbMath.SingleArcPitchMargin) / 9f, capped.Pitch, 0.01f);
    }

    [Fact]
    public void EnemyLaneNarrowsTheQueueAndKeepsItLeftOfTheBody()
    {
        // A boss body's left edge at 400: the queue's centres stay inside [48, 400], its icons 10 units clear of the body.
        PortraitOrbArcPlan plan = TallPlan(10, laneLeft: 48f, laneRight: 400f);
        Assert.True(plan.Pitch < 44f);
        Assert.Equal(48f, plan.Position(9).X, 0.01f);
        Assert.Equal(400f, plan.Position(0).X, 0.01f);
        Assert.True(plan.IconBox(0).Right < 400f + plan.IconWorld);
    }

    [Fact]
    public void ALaneWiderThanTheQueueRecentresItInsideTheLane()
    {
        // The queue's own pitch already fits the lane: the lane still moves it left of the body at 500, never under it.
        PortraitOrbArcPlan plan = TallPlan(10, laneLeft: 48f, laneRight: 500f);
        Assert.Equal(44f, plan.Pitch, 0.01f);
        Assert.Equal(48f, plan.Position(9).X, 0.01f);
        Assert.Equal(444f, plan.Position(0).X, 0.01f);
        Assert.True(plan.IconBox(0).Right < 500f);
    }

    [Fact]
    public void TooNarrowALaneKeepsTheDesignPitchInsteadOfStackingTheValues()
    {
        // 60 units of lane for ten orbs is 6.7 apart: the labels would collide, so the canvas bounds win instead.
        PortraitOrbArcPlan plan = TallPlan(10, laneLeft: 20f, laneRight: 80f);
        Assert.Equal(44f, plan.Pitch, 0.01f);
    }

    [Fact]
    public void EnemyProbeFallbackReplacesTheNativeArcWithThePhoneRow()
    {
        PortraitOrbArcPlan plan = TallPlan(3, single: true);
        Assert.Equal(PortraitOrbArcKind.Single, plan.Kind);
        Assert.Equal(PortraitOrbMath.ShallowWidePitch, plan.Pitch, 0.01f);
        AssertInsideCanvas(plan);
    }

    [Fact]
    public void AVeryTallOwnerIsLiftedRatherThanLeftOnTheHandRail()
    {
        PortraitOrbArcPlan plan = TallPlan(10, ownerTopY: 1500f);
        for (int i = 0; i < plan.Count; i++)
            Assert.True(plan.IconBox(i).Bottom <= TallRailTop - PortraitOrbMath.CanvasEdgeMargin + 0.01f);
    }

    [Fact]
    public void OrbIconIsTheBoundsSceneSizeAtTheOrbAndManagerScales()
    {
        Assert.Equal(60f * 1.48f * 0.85f, PortraitOrbMath.IconWorldFor(1.48f), 0.001f);
        PortraitOrbArcPlan plan = TallPlan(3);
        Assert.Equal(PortraitOrbMath.IconWorldFor(1.48f), plan.IconWorld, 0.001f);
        (float left, float top, float right, float bottom) = plan.IconBox(1);
        Assert.Equal(plan.IconWorld, right - left, 0.001f);
        Assert.Equal(plan.IconWorld, bottom - top, 0.001f);
        Assert.Equal(plan.Position(1).X, (left + right) * 0.5f, 0.001f);
    }

    [Fact]
    public void AnEmptyQueueStillYieldsALegalPlan()
    {
        PortraitOrbArcPlan plan = TallPlan(0);
        Assert.Equal(1, plan.Count);
        Assert.Equal(PortraitOrbArcKind.Native, plan.Kind);
        AssertInsideCanvas(plan);
    }
}
