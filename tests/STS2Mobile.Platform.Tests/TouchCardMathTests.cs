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
    public void AHandTipBlockGoesBesideTheCardNeverOverIt()
    {
        // A 1080-wide canvas, a card at 400..680 and a text column 360 wide: the right side has 382 units, so the block
        // keeps 1.06x (the side's room), starts just right of the card and stays inside the canvas.
        TouchCardMath.TipBlock block = TouchCardMath.PlaceTipBlockBeside(
            ownerX: 400f, ownerY: 1400f, ownerWidth: 280f, ownerHeight: 300f,
            canvasWidth: 1080f, canvasHeight: 1920f, blockWidth: 360f, blockHeight: 200f,
            preferredScale: 1.4f, margin: 10f, gap: 8f);
        Assert.True(block.RightOfOwner);
        Assert.Equal(382f / 360f, block.Scale, 0.001f);
        Assert.Equal(688f, block.X, 0.01f);
        Assert.Equal(1400f, block.Y, 0.01f);
        Assert.True(block.X >= 680f);
        Assert.True(block.X + block.Width <= 1080f - 10f + 0.01f);
        Assert.True(block.Y + block.Height <= 1920f);

        // Near the right edge the left side has the room and the block keeps the preferred size.
        block = TouchCardMath.PlaceTipBlockBeside(
            ownerX: 700f, ownerY: 300f, ownerWidth: 280f, ownerHeight: 300f,
            canvasWidth: 1080f, canvasHeight: 1920f, blockWidth: 360f, blockHeight: 200f,
            preferredScale: 1.4f, margin: 10f, gap: 8f);
        Assert.False(block.RightOfOwner);
        Assert.Equal(1.4f, block.Scale, 0.001f);
        Assert.Equal(700f - 8f - 504f, block.X, 0.01f);

        // Near the left edge the right side has the room.
        block = TouchCardMath.PlaceTipBlockBeside(
            ownerX: 10f, ownerY: 300f, ownerWidth: 280f, ownerHeight: 300f,
            canvasWidth: 1080f, canvasHeight: 1920f, blockWidth: 360f, blockHeight: 200f,
            preferredScale: 1.4f, margin: 10f, gap: 8f);
        Assert.True(block.RightOfOwner);
        Assert.Equal(1.4f, block.Scale, 0.001f);
        Assert.Equal(298f, block.X, 0.01f);
    }

    [Fact]
    public void AHandTipBlockShrinksToTheRoomierSideAndTheCanvasHeight()
    {
        // Neither side fits the 360-wide block at 1.4x: the roomier (right, 482 versus 292) gives 482/360.
        TouchCardMath.TipBlock block = TouchCardMath.PlaceTipBlockBeside(
            ownerX: 310f, ownerY: 1000f, ownerWidth: 270f, ownerHeight: 300f,
            canvasWidth: 1080f, canvasHeight: 1920f, blockWidth: 360f, blockHeight: 200f,
            preferredScale: 1.4f, margin: 10f, gap: 8f);
        Assert.True(block.RightOfOwner);
        Assert.Equal(482f / 360f, block.Scale, 0.001f);
        Assert.True(block.X >= 588f && block.X + block.Width <= 1080f - 10f + 0.01f);

        // The block is taller than the canvas: the height fit wins and it is pinned inside the canvas.
        block = TouchCardMath.PlaceTipBlockBeside(
            ownerX: 400f, ownerY: 1800f, ownerWidth: 280f, ownerHeight: 300f,
            canvasWidth: 1080f, canvasHeight: 1920f, blockWidth: 200f, blockHeight: 4000f,
            preferredScale: 1.4f, margin: 10f, gap: 8f);
        Assert.Equal((1920f - 20f) / 4000f, block.Scale, 0.0001f);
        Assert.Equal(10f, block.Y, 0.01f);
        Assert.True(block.Y + block.Height <= 1920f);
    }

    [Fact]
    public void TheGrabbedCardKeepsThePressOffsetUnderTheFinger()
    {
        float scale = PortraitCombatMath.HandCardScaleFor(PortraitCombatMath.ComputeCombatBands(2341f, 0f), 5);
        (float x, float y) = TouchCardMath.GrabbedCardCenter(540f, 1200f, 40f, -60f, scale, 1080f, 2341f);
        Assert.Equal(580f, x, 0.01f);
        Assert.Equal(1140f, y, 0.01f);

        // At the canvas edges the card is kept inside, exactly as the lifted drag is.
        float halfWidth = PortraitCombatMath.CardDefaultWidth * scale * 0.5f;
        float halfHeight = PortraitCombatMath.CardDefaultHeight * scale * 0.5f;
        (x, y) = TouchCardMath.GrabbedCardCenter(0f, 0f, -500f, -500f, scale, 1080f, 2341f);
        Assert.True(x - halfWidth >= 0f);
        Assert.True(y - halfHeight >= 0f);
        (x, y) = TouchCardMath.GrabbedCardCenter(5000f, 5000f, 500f, 500f, scale, 1080f, 2341f);
        Assert.True(x + halfWidth <= 1080f);
        Assert.True(y + halfHeight <= 2341f);
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
}
