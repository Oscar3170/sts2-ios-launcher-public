using System.Collections.Generic;
using STS2Mobile.Platform;
using Xunit;

namespace STS2Mobile.Platform.Tests;

public class TouchCardMathTests
{
    [Theory]
    [InlineData(0f, 0.1, false)]
    [InlineData(0f, TouchCardMath.HoldSeconds - 0.01, false)]
    [InlineData(0f, TouchCardMath.HoldSeconds, true)]
    [InlineData(TouchCardMath.PickupSlop - 0.5f, 1.2, true)]
    [InlineData(TouchCardMath.PickupSlop, 1.2, false)]
    [InlineData(60f, 1.2, false)]
    public void AHoldIsStillAndLongEnough(float moved, double seconds, bool expected) =>
        Assert.Equal(expected, TouchCardMath.IsStationaryHold(moved, seconds));

    [Theory]
    [InlineData(0f, false)]
    [InlineData(TouchCardMath.PickupSlop - 0.5f, false)]
    [InlineData(TouchCardMath.PickupSlop, true)]
    [InlineData(60f, true)]
    public void APickupWaitsForThePickupSlop(float moved, bool expected) =>
        Assert.Equal(expected, TouchCardMath.HasMovedPastPickupSlop(moved));

    [Theory]
    [InlineData(1920f)]
    [InlineData(2340f)]
    public void AimingHasOneSizePerShapeWhateverTheHandHolds(float canvasHeight)
    {
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, 0f);
        float aim = TouchCardMath.AimedCardScaleFor(bands);
        Assert.Equal(PortraitCombatMath.HandCardScaleFor(bands, 1) * 1.5f, aim);
        for (int count = 1; count <= 10; count++)
        {
            // The aim size does not depend on the hand; the pressed (held) size never exceeds it, so aiming only grows.
            Assert.Equal(aim, TouchCardMath.AimedCardScaleFor(bands));
            Assert.True(aim >= PortraitCombatMath.HeldCardScaleFor(bands, count));
        }
    }

    [Theory]
    [InlineData(1920f)]
    [InlineData(2340f)]
    public void AimingPutsTheCardsTopOnEndTurnsTopCentredAndInsideTheCanvas(float canvasHeight)
    {
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, 0f);
        float endTurnTop = TouchCardMath.EndTurnTopY(PortraitCombatMath.ComputeHudLayout(canvasHeight, bands));
        float aim = TouchCardMath.AimedCardScaleFor(bands);
        (float x, float y) = TouchCardMath.AimingCardCenter(endTurnTop, aim, 1080f);
        float half = PortraitCombatMath.CardDefaultHeight * aim * 0.5f;

        Assert.Equal(540f, x);
        Assert.True(System.Math.Abs(y - half - endTurnTop) < 0.01f);
        Assert.True(y - half >= 0f);
        Assert.True(y + half <= canvasHeight);
    }

    [Fact]
    public void TheDraggedCardsBottomEdgeStaysAboveTheFinger()
    {
        float scale = PortraitCombatMath.HandCardScaleFor(PortraitCombatMath.ComputeCombatBands(2341f, 0f), 5);
        float halfHeight = PortraitCombatMath.CardDefaultHeight * scale * 0.5f;
        (float x, float y) = TouchCardMath.DraggedCardCenter(540f, 1200f, scale, 1080f, 2341f);
        Assert.Equal(540f, x);
        Assert.True(System.Math.Abs(y - (1200f - halfHeight - TouchCardMath.DragLift)) < 0.01f);
        Assert.True(y + halfHeight <= 1200f);
    }

    [Fact]
    public void TheDraggedCardStaysInsideTheCanvasAtTheEdges()
    {
        float scale = PortraitCombatMath.HandCardScaleFor(PortraitCombatMath.ComputeCombatBands(2341f, 0f), 5);
        float halfWidth = PortraitCombatMath.CardDefaultWidth * scale * 0.5f;
        float halfHeight = PortraitCombatMath.CardDefaultHeight * scale * 0.5f;

        (float left, float top) = TouchCardMath.DraggedCardCenter(0f, 0f, scale, 1080f, 2341f);
        Assert.True(left - halfWidth >= 0f);
        Assert.True(top - halfHeight >= 0f);

        (float right, float bottom) = TouchCardMath.DraggedCardCenter(5000f, 5000f, scale, 1080f, 2341f);
        Assert.True(right + halfWidth <= 1080f);
        Assert.True(bottom + halfHeight <= 2341f);
    }

    [Fact]
    public void AReleaseInsideTheTargetPaddingTakesThatTarget()
    {
        var targets = new List<TouchCardMath.TargetBox>
        {
            new(755f, 900f, 100f, 150f),
            new(400f, 900f, 100f, 150f),
        };
        // 30 units past the right target's edge: inside the padding.
        Assert.Equal(0, TouchCardMath.NearestTarget(targets, 755f + 100f + 30f, 900f));
    }

    [Fact]
    public void ANearMissInsideTheSnapTakesTheNearestTarget()
    {
        var targets = new List<TouchCardMath.TargetBox>
        {
            new(755f, 900f, 100f, 150f),
            new(400f, 900f, 100f, 150f),
        };
        // 60 units past the right target's edge: outside the padding, inside the snap.
        Assert.Equal(0, TouchCardMath.NearestTarget(targets, 755f + 100f + 60f, 900f));
        // Nearer the left one, still outside its padding but inside the snap.
        Assert.Equal(1, TouchCardMath.NearestTarget(targets, 400f - 100f - 60f, 900f));
    }

    [Fact]
    public void AReleaseBeyondTheSnapTakesNoTargetAndPutsTheCardBack()
    {
        var targets = new List<TouchCardMath.TargetBox> { new(755f, 900f, 100f, 150f) };
        Assert.Equal(-1, TouchCardMath.NearestTarget(targets, 755f + 100f + TouchCardMath.TargetSnap + 1f, 900f));
        Assert.Equal(-1, TouchCardMath.NearestTarget(new List<TouchCardMath.TargetBox>(), 755f, 900f));
    }

    [Fact]
    public void TheTouchDistancesAreTheCanvasValuesTheyAchieve()
    {
        // Canvas units on the 1080-wide portrait canvas, one point about 2.9 units: the pin is the value and the distance
        // a player sees, not a conversion from another canvas.
        Assert.Equal(16f, TouchCardMath.PickupSlop);
        Assert.Equal(39f, TouchCardMath.TargetPadding);
        Assert.Equal(90f, TouchCardMath.TargetSnap);
        Assert.Equal(51f, TouchCardMath.DragLift);
        Assert.InRange(TouchCardMath.PickupSlop / 2.9f, 5.4f, 5.6f);
        Assert.InRange(TouchCardMath.TargetPadding / 2.9f, 13.3f, 13.5f);
        Assert.InRange(TouchCardMath.TargetSnap / 2.9f, 30.9f, 31.1f);
        Assert.InRange(TouchCardMath.DragLift / 2.9f, 17.5f, 17.7f);
    }

    [Fact]
    public void TheHandTipBandKeepsTheStageMarginAtBothEnds()
    {
        Assert.Equal(24f, TouchCardMath.StageTipMargin);
        Assert.Equal((324f, 1576f), TouchCardMath.HandTipBand(300f, 1600f));
    }

    [Fact]
    public void TheInspectUpgradeBoxSitsInTheLeftMarginBelowTheStageOrTheCard()
    {
        // A 2340-tall canvas: the centred card's top is 1170 - 422 = 748, below the stage.
        (float left, float top, float width, float height, float scale) = TouchCardMath.InspectUpgradeRect(2340f, 300f);
        Assert.Equal(8f, left);
        Assert.Equal(748f + 24f, top, 0.01f);
        Assert.Equal((272f, 64f), (width, height));
        Assert.Equal(0.85f, scale);

        // A stage lower than the card wins.
        (_, float stageTop, _, _, _) = TouchCardMath.InspectUpgradeRect(2340f, 900f);
        Assert.Equal(900f + 24f, stageTop, 0.01f);
    }

    [Fact]
    public void ATipColumnShrinksToTheCanvasAndNeverBelowTheFloor()
    {
        Assert.Equal(1.4f, TouchCardMath.FitTipColumnScale(1.4f, 300f, 600f, 1080f, 2340f, 10f), 0.001f);
        // Wider than the canvas: the side margins decide.
        Assert.Equal((1080f - 20f) / 800f,
            TouchCardMath.FitTipColumnScale(1.4f, 800f, 600f, 1080f, 2340f, 10f), 0.001f);
        // Taller than the canvas: the height fit stops at the readable floor.
        Assert.Equal(0.5f, TouchCardMath.FitTipColumnScale(1.4f, 300f, 9000f, 1080f, 2340f, 10f), 0.001f);
    }

    [Fact]
    public void ATipColumnIsClampedIntoTheHandBandAndShrinksToIt()
    {
        // Shorter than the band: moved up so its bottom is inside.
        (float x, float y, float width, float height, float scale) =
            TouchCardMath.FitTipColumnToBand(40f, 1500f, 200f, 80f, 1f, 300f, 1500f);
        Assert.Equal((40f, 1420f, 200f, 80f, 1f), (x, y, width, height, scale));

        // Taller than the band: pinned to its top and shrunk to it.
        (x, y, width, height, scale) = TouchCardMath.FitTipColumnToBand(40f, 500f, 200f, 1200f, 1f, 300f, 900f);
        Assert.Equal((40f, 300f, 100f, 600f, 0.5f), (x, y, width, height, scale));

        // No band: untouched.
        (x, y, width, height, scale) = TouchCardMath.FitTipColumnToBand(40f, 10f, 200f, 80f, 1f, 300f, 300f);
        Assert.Equal((40f, 10f, 200f, 80f, 1f), (x, y, width, height, scale));
    }
}
