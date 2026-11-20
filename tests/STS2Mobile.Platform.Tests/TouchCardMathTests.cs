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
}
