namespace STS2Mobile.Platform.Tests;

public class PortraitCombatMathTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(2, 2, 0)]
    [InlineData(3, 3, 0)]
    [InlineData(4, 3, 1)]
    [InlineData(5, 3, 2)]
    [InlineData(6, 3, 3)]
    [InlineData(7, 4, 3)]
    [InlineData(8, 4, 4)]
    [InlineData(9, 5, 4)]
    [InlineData(10, 5, 5)]
    [InlineData(11, 6, 5)]
    [InlineData(12, 6, 6)]
    public void AlternatingUpperRowFillsFirstAndTakesAtLeastThree(int count, int expectedUpper, int expectedLower)
    {
        HandRowSplit split = PortraitCombatMath.SplitHandRows(count, HandRowMode.Alternating);
        Assert.Equal(expectedUpper, split.BackRowCount);
        Assert.Equal(expectedLower, split.FrontRowCount);
        Assert.Equal(count, split.BackRowCount + split.FrontRowCount);
        Assert.True(split.BackRowCount >= split.FrontRowCount);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(2, 2, 0)]
    [InlineData(3, 3, 0)]
    [InlineData(4, 4, 0)]
    [InlineData(5, 5, 0)]
    [InlineData(6, 5, 1)]
    [InlineData(7, 5, 2)]
    [InlineData(8, 5, 3)]
    [InlineData(9, 5, 4)]
    [InlineData(10, 5, 5)]
    [InlineData(11, 5, 6)]
    [InlineData(12, 5, 7)]
    public void FixedUpperRowFillsToFiveBeforeTheLowerRowTakesAny(int count, int expectedUpper, int expectedLower)
    {
        HandRowSplit split = PortraitCombatMath.SplitHandRows(count, HandRowMode.Fixed);
        Assert.Equal(expectedUpper, split.BackRowCount);
        Assert.Equal(expectedLower, split.FrontRowCount);
        Assert.Equal(count, split.BackRowCount + split.FrontRowCount);
        Assert.True(split.BackRowCount <= 5);
    }

    [Fact]
    public void FixedKeepsOneToFiveInTheUpperBandAtTheFullScaleAndOverlapsThem()
    {
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(1920f, stageTopY: 612f);
        float upperBandY = PortraitCombatMath.ComputeHandCardSlot(1, 0, bands, mode: HandRowMode.Fixed).LocalY;
        for (int count = 1; count <= 5; count++)
        {
            for (int index = 0; index < count; index++)
            {
                PortraitCardSlot slot = PortraitCombatMath.ComputeHandCardSlot(count, index, bands, mode: HandRowMode.Fixed);
                Assert.Equal(0, slot.RowIndex);
                Assert.Equal(upperBandY, slot.LocalY, 4);
                Assert.Equal(PortraitCombatMath.CompactHandCardScale, slot.Scale, 4);
            }
        }

        // Five cards in one row keep the shape's full scale and overlap; they never shrink.
        PortraitCardSlot left = PortraitCombatMath.ComputeHandCardSlot(5, 0, bands, mode: HandRowMode.Fixed);
        PortraitCardSlot next = PortraitCombatMath.ComputeHandCardSlot(5, 1, bands, mode: HandRowMode.Fixed);
        Assert.True(next.LocalX - left.LocalX < PortraitCombatMath.CardDefaultWidth * left.Scale);

        // The sixth card is the first one in the lower row, below the upper band.
        PortraitCardSlot sixth = PortraitCombatMath.ComputeHandCardSlot(6, 5, bands, mode: HandRowMode.Fixed);
        Assert.Equal(1, sixth.RowIndex);
        Assert.True(sixth.LocalY > upperBandY);
    }

    [Fact]
    public void NegativeCountsClampToAnEmptyHandInBothModes()
    {
        foreach (HandRowMode mode in new[] { HandRowMode.Alternating, HandRowMode.Fixed })
            Assert.Equal(new HandRowSplit(0, 0), PortraitCombatMath.SplitHandRows(-3, mode));
    }

    [Theory]
    [InlineData(1920f)]
    [InlineData(2340f)]
    public void HandLayoutModeKeepsTheFitAndOnlyMovesTheRowBoundary(float canvasHeight)
    {
        // The upper row takes the first indices in both modes, and the shape's fit (scale, gap, card size) is identical: only
        // the boundary between the rows moves (4 is 3+1 alternating, 4+0 fixed; 6 is 3+3 alternating, 5+1 fixed).
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, stageTopY: 612f);
        for (int count = 0; count <= 12; count++)
        {
            HandRowSplit alternating = PortraitCombatMath.SplitHandRows(count, HandRowMode.Alternating);
            HandRowSplit fixedMode = PortraitCombatMath.SplitHandRows(count, HandRowMode.Fixed);
            HandRowFit alternatingFit = PortraitCombatMath.ComputeHandRowFit(bands, count, HandRowMode.Alternating);
            HandRowFit fixedFit = PortraitCombatMath.ComputeHandRowFit(bands, count, HandRowMode.Fixed);
            Assert.Equal(alternating.BackRowCount, alternatingFit.UpperCount);
            Assert.Equal(fixedMode.BackRowCount, fixedFit.UpperCount);
            Assert.Equal(alternatingFit.Scale, fixedFit.Scale);
            Assert.Equal(alternatingFit.Gap, fixedFit.Gap);
            Assert.Equal(alternatingFit.CardWidth, fixedFit.CardWidth);
            Assert.Equal(alternatingFit.CardHeight, fixedFit.CardHeight);

            for (int index = 0; index < count; index++)
            {
                foreach ((HandRowMode mode, HandRowSplit split, HandRowFit fit) in new[]
                         {
                             (HandRowMode.Alternating, alternating, alternatingFit),
                             (HandRowMode.Fixed, fixedMode, fixedFit),
                         })
                {
                    PortraitCardSlot slot =
                        PortraitCombatMath.ComputeHandCardSlot(count, index, bands, flat: false, mode: mode);
                    Assert.Equal(index < split.BackRowCount ? 0 : 1, slot.RowIndex);
                    Assert.Equal(fit.Scale, slot.Scale, 4);
                }
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OneToThreeCardsSitInTheUpperRowSoTheFirstDrawsNeverStartLow(int count)
    {
        foreach (float canvasHeight in new[] { 1920f, 2340f })
        {
            PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, 612f);
            for (int i = 0; i < count; i++)
            {
                PortraitCardSlot slot = PortraitCombatMath.ComputeHandCardSlot(count, i, bands);
                Assert.Equal(0, slot.RowIndex);
            }
            Assert.Equal(
                PortraitCombatMath.ComputeHandCardSlot(3, 0, bands).LocalY,
                PortraitCombatMath.ComputeHandCardSlot(1, 0, bands).LocalY);
        }
    }

    [Theory]
    [InlineData(1920f)]
    [InlineData(2340f)]
    public void RowsKeepTheirVerticalPlacementWhateverTheCount(float canvasHeight)
    {
        // The two rows are a permanent reserve: every card in a row has the same centre at every hand size, so drawing a
        // card never slides the existing rows up or down (a shrunken tall card is centred inside its reserved band).
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, stageTopY: 612f);
        float? upper = null;
        float? lower = null;
        for (int count = 1; count <= 12; count++)
        {
            bool hasLeader = false;
            for (int i = 0; i < count; i++)
            {
                PortraitCardSlot slot = PortraitCombatMath.ComputeHandCardSlot(count, i, bands);
                if (slot.RowIndex == 0)
                {
                    if (upper is { } expectedUpper)
                        Assert.Equal(expectedUpper, slot.LocalY, 3);
                    else
                        upper = slot.LocalY;
                    hasLeader = true;
                }
                else
                {
                    if (lower is { } expectedLower)
                        Assert.Equal(expectedLower, slot.LocalY, 3);
                    else
                        lower = slot.LocalY;
                }

                float top = canvasHeight + slot.LocalY - PortraitCombatMath.CardDefaultHeight * slot.Scale * 0.5f;
                float bottom = canvasHeight + slot.LocalY + PortraitCombatMath.CardDefaultHeight * slot.Scale * 0.5f;
                Assert.True(top >= bands.HandTopY - 0.01f, $"Card {i}/{count} top {top} is above hand top {bands.HandTopY}.");
                Assert.True(bottom <= canvasHeight + 0.01f, $"Card {i}/{count} bottom {bottom} is off the canvas.");
            }
            Assert.True(hasLeader);
            if (count > 3)
                Assert.NotNull(lower);
        }

        Assert.NotNull(upper);
        Assert.NotNull(lower);
        Assert.True(upper.Value < lower.Value, $"Upper row centre {upper.Value} is not above lower row centre {lower.Value}.");
    }

    [Fact]
    public void HoldingACardKeepsTheTwoRowCountUntilCardPlayFinishes()
    {
        // A held card's slot stays empty, so the effective count must keep the row split (and the other cards' places)
        // exactly as they were with the card in the hand.
        int whileDragging = PortraitCombatMath.EffectiveCardCount(activeHoldersInHand: 5, inUncommittedCardPlay: true);
        Assert.Equal(PortraitCombatMath.SplitHandRows(6), PortraitCombatMath.SplitHandRows(whileDragging));
    }

    [Fact]
    public void CompactBandsAndHandAreUnchanged()
    {
        const float canvasHeight = 1920f;
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, 612f);
        Assert.True(bands.Compact);
        Assert.Equal(1198.32f, bands.HandTopY, 1);
        Assert.Equal(1028.32f, bands.RailTopY, 1);
        Assert.Equal(958.32f, bands.FrontFeetY, 1);

        for (int count = 1; count <= 10; count++)
        {
            Assert.Equal(PortraitCombatMath.CompactHandCardScale, PortraitCombatMath.HandCardScaleFor(bands, count), 4);
            Assert.Equal(PortraitCombatMath.CompactHandCardScale * PortraitCombatMath.HeldCardGrowth,
                PortraitCombatMath.HeldCardScaleFor(bands, count), 4);
        }
    }

    [Fact]
    public void TallBandsAndHandUseTheTallShape()
    {
        const float canvasHeight = 2340f;
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, 612f);
        Assert.False(bands.Compact);
        Assert.Equal(1534.24f, bands.HandTopY, 1);
        Assert.Equal(1373.53f, bands.RailTopY, 1);
        Assert.Equal(1243.53f, bands.FrontFeetY, 1);
        Assert.Equal(160.71f, PortraitCombatMath.RailHeightFor(compact: false), 2);

        // The default is the fan at tall too. Scale is the shape's full size for every count; only the fan angle
        // changes, and every card stays inside the canvas above the rail.
        for (int count = 1; count <= 10; count++)
        {
            HandRowFit fit = PortraitCombatMath.ComputeHandRowFit(bands, count);
            for (int i = 0; i < count; i++)
            {
                PortraitCardSlot slot = PortraitCombatMath.ComputeHandCardSlot(count, i, bands);
                Assert.Equal(fit.Scale, slot.Scale, 4);
                Assert.InRange(slot.AngleDegrees, -2.401f, 2.401f);
                float radians = MathF.Abs(slot.AngleDegrees) * MathF.PI / 180f;
                float halfWidth = (PortraitCombatMath.CardDefaultWidth * MathF.Cos(radians) +
                    PortraitCombatMath.CardDefaultHeight * MathF.Sin(radians)) * 0.5f * slot.Scale;
                float halfHeight = (PortraitCombatMath.CardDefaultHeight * MathF.Cos(radians) +
                    PortraitCombatMath.CardDefaultWidth * MathF.Sin(radians)) * 0.5f * slot.Scale;
                float centerX = 540f + slot.LocalX;
                float centerY = canvasHeight + slot.LocalY;
                Assert.True(centerX - halfWidth >= PortraitCombatMath.HandEdgeMargin - 0.01f);
                Assert.True(centerX + halfWidth <= 1080f - PortraitCombatMath.HandEdgeMargin + 0.01f);
                Assert.True(centerY - halfHeight >= bands.RailTopY);
                Assert.True(centerY + halfHeight <= canvasHeight + 0.01f);
            }
        }

        // No crowded-row shrink: the full tall scale at every count.
        for (int count = 1; count <= 10; count++)
            Assert.InRange(PortraitCombatMath.HandCardScaleFor(bands, count), 0.866f, 0.870f);
        Assert.Equal(PortraitCombatMath.HandCardScaleFor(bands, 10) * 1.08f,
            PortraitCombatMath.HeldCardScaleFor(bands, 10), 4);
    }

    [Theory]
    [InlineData(1920f)]
    [InlineData(2340f)]
    public void BothHandModesUseTheSameBandsFullScaleAndCanvasSpace(float canvasHeight)
    {
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, stageTopY: 612f);
        float expectedScale = bands.Compact
            ? PortraitCombatMath.CompactHandCardScale
            : PortraitCombatMath.TallHandCardScale;

        for (int count = 1; count <= 10; count++)
        {
            HandRowSplit split = PortraitCombatMath.SplitHandRows(count);
            for (int mode = 0; mode < 2; mode++)
            {
                bool flat = mode == 1;
                for (int i = 0; i < count; i++)
                {
                    PortraitCardSlot slot = PortraitCombatMath.ComputeHandCardSlot(count, i, bands, flat);
                    Assert.Equal(expectedScale, slot.Scale, 4);
                    Assert.Equal(expectedScale, PortraitCombatMath.HandCardScaleFor(bands, count), 4);
                    Assert.Equal(expectedScale * 1.08f, PortraitCombatMath.HeldCardScaleFor(bands, count), 4);
                    Assert.Equal(i < split.BackRowCount ? 0 : 1, slot.RowIndex);

                    if (flat)
                    {
                        Assert.Equal(0f, slot.AngleDegrees);
                    }
                    else
                    {
                        Assert.InRange(slot.AngleDegrees, -2.401f, 2.401f);
                        if (Math.Abs(slot.AngleDegrees) > 0.01f)
                            Assert.InRange(Math.Abs(slot.AngleDegrees), 0.01f, 2.401f);
                        // The rotated card's centre moves by at most half its width times sin(angle); with the 2.4-degree
                        // cap that stays under the hand-row harness's 6-unit Y bound at both shapes.
                        float yShift = MathF.Abs(slot.AngleDegrees) * MathF.PI / 180f *
                            PortraitCombatMath.CardDefaultWidth * 0.5f * slot.Scale;
                        Assert.True(yShift < 6f, $"Fan Y shift {yShift} exceeds 6 units.");
                    }

                    float halfExtent = PortraitCombatMath.HandSlotHalfExtent(slot.Scale, slot.AngleDegrees);
                    Assert.True(540f + slot.LocalX - halfExtent >= PortraitCombatMath.HandEdgeMargin - 0.01f,
                        $"{count} cards, flat={flat}, card {i} left edge leaves the margin.");
                    Assert.True(540f + slot.LocalX + halfExtent <= 1080f - PortraitCombatMath.HandEdgeMargin + 0.01f,
                        $"{count} cards, flat={flat}, card {i} right edge leaves the margin.");

                    float radians = MathF.Abs(slot.AngleDegrees) * MathF.PI / 180f;
                    float halfHeight = (PortraitCombatMath.CardDefaultHeight * MathF.Cos(radians) +
                        PortraitCombatMath.CardDefaultWidth * MathF.Sin(radians)) * 0.5f * slot.Scale;
                    float centerY = canvasHeight + slot.LocalY;
                    Assert.True(centerY - halfHeight >= bands.RailTopY);
                    Assert.True(centerY + halfHeight <= canvasHeight + 0.01f);
                }
            }
        }
    }

    [Theory]
    [InlineData(1920f)]
    [InlineData(2340f)]
    public void HandModeChoiceOnlyChangesTheRowShape(float canvasHeight)
    {
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, stageTopY: 612f);
        for (int count = 1; count <= 10; count++)
        {
            for (int i = 0; i < count; i++)
            {
                PortraitCardSlot fan = PortraitCombatMath.ComputeHandCardSlot(count, i, bands, flat: false);
                PortraitCardSlot flat = PortraitCombatMath.ComputeHandCardSlot(count, i, bands, flat: true);
                Assert.Equal(fan.RowIndex, flat.RowIndex);
                Assert.Equal(fan.LocalY, flat.LocalY, 4);
                Assert.Equal(fan.Scale, flat.Scale, 4);
                if (count <= 1)
                {
                    Assert.Equal(0f, fan.AngleDegrees);
                    Assert.Equal(fan.LocalX, flat.LocalX, 4);
                }
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(10)]
    public void HandNeverReachesTheRailAtEitherTestShape(int count)
    {
        foreach (float canvasHeight in new[] { 1920f, 2340f })
        {
            PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, stageTopY: 612f);
            PortraitHudLayout hud = PortraitCombatMath.ComputeHudLayout(canvasHeight, bands);

            Assert.Equal(bands.RailTopY, hud.RailTopY);

            foreach (bool flat in new[] { false, true })
            {
                for (int i = 0; i < count; i++)
                {
                    PortraitCardSlot slot = PortraitCombatMath.ComputeHandCardSlot(count, i, bands, flat);
                    float radians = MathF.Abs(slot.AngleDegrees) * MathF.PI / 180f;
                    float halfHeight = (PortraitCombatMath.CardDefaultHeight * MathF.Cos(radians) +
                        PortraitCombatMath.CardDefaultWidth * MathF.Sin(radians)) * 0.5f * slot.Scale;
                    float cardTop = canvasHeight + slot.LocalY - halfHeight;
                    Assert.True(cardTop >= bands.RailTopY,
                        $"Card {i}/{count} at {canvasHeight}, flat={flat} reached the rail.");
                }
            }
        }
    }

    // On a phone the leftmost of five cards lost its cost badge and left side past the screen edge.
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]
    public void EveryGrownHandCardKeepsItsBadgeInsideTheSideEdgesAtItsRestingAngle(int count)
    {
        foreach (float canvasHeight in new[] { 1920f, 2340f })
        {
            PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, 612f);
            for (int i = 0; i < count; i++)
            {
                PortraitCardSlot slot = PortraitCombatMath.ComputeHandCardSlot(count, i, bands);
                float extent = PortraitCombatMath.HandSlotHalfExtent(slot.Scale * PortraitCombatMath.HeldCardGrowth, slot.AngleDegrees);
                float left = 540f + slot.LocalX - extent;
                float right = 540f + slot.LocalX + extent;
                Assert.True(left >= -0.01f, $"Grown card {i}/{count} left edge {left}.");
                Assert.True(right <= 1080f + 0.01f, $"Grown card {i}/{count} right edge {right}.");
            }
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    public void HoldingACardLeavesItsSlotEmptySoTheOthersStayPut(int count)
    {
        // Every touch picks the card up; the rest of the hand keeps the places it had with the card in it.
        foreach (float canvasHeight in new[] { 1920f, 2340f })
        {
            PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, 612f);
            for (int held = 0; held < count; held++)
            {
                for (int i = 0; i < count - 1; i++)
                {
                    int slot = PortraitCombatMath.SlotIndexBesideHeldCard(i, held);
                    int original = i < held ? i : i + 1;
                    Assert.Equal(original, slot);
                    Assert.Equal(
                        PortraitCombatMath.ComputeHandCardSlot(count, original, bands),
                        PortraitCombatMath.ComputeHandCardSlot(count, slot, bands));
                }
            }
        }
        Assert.Equal(3, PortraitCombatMath.SlotIndexBesideHeldCard(3, -1));
    }

    [Fact]
    public void FiveCardsFillThreeUpperSlotsAndTwoLowerOnes()
    {
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(1920f, 612f);
        PortraitCardSlot upperLeft = PortraitCombatMath.ComputeHandCardSlot(5, 0, bands);
        PortraitCardSlot upperMiddle = PortraitCombatMath.ComputeHandCardSlot(5, 1, bands);
        PortraitCardSlot upperRight = PortraitCombatMath.ComputeHandCardSlot(5, 2, bands);
        PortraitCardSlot lowerLeft = PortraitCombatMath.ComputeHandCardSlot(5, 3, bands);
        PortraitCardSlot lowerRight = PortraitCombatMath.ComputeHandCardSlot(5, 4, bands);

        Assert.All(new[] { upperLeft, upperMiddle, upperRight }, slot => Assert.Equal(0, slot.RowIndex));
        Assert.All(new[] { lowerLeft, lowerRight }, slot => Assert.Equal(1, slot.RowIndex));
        Assert.Equal(0f, upperMiddle.LocalX);
        Assert.Equal(-upperLeft.LocalX, upperRight.LocalX, 3);
        Assert.Equal(-lowerLeft.LocalX, lowerRight.LocalX, 3);
        Assert.InRange(upperRight.LocalX - upperLeft.LocalX, 300f, 450f);
    }

    [Fact]
    public void TenCardsFillBothRowsAndKeepTheUpperRowCardsReadable()
    {
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(1920f, 612f);
        PortraitCardSlot upperCenter = PortraitCombatMath.ComputeHandCardSlot(10, 2, bands);
        PortraitCardSlot lowerCenter = PortraitCombatMath.ComputeHandCardSlot(10, 7, bands);

        Assert.Equal(0, upperCenter.RowIndex);
        Assert.Equal(1, lowerCenter.RowIndex);

        float cardHeight = PortraitCombatMath.CardDefaultHeight * upperCenter.Scale;
        float visibleUpperHeight = lowerCenter.LocalY - upperCenter.LocalY - PortraitCombatMath.CardDefaultHeight * lowerCenter.Scale * 0.5f
            + PortraitCombatMath.CardDefaultHeight * upperCenter.Scale * 0.5f;
        Assert.True(visibleUpperHeight > 0f);
        Assert.True(visibleUpperHeight / cardHeight > 0.9f);
    }

    [Theory]
    [InlineData(1920f)]
    [InlineData(2340f)]
    public void SelectionConfirmSitsOnTheRailAndPreviewsFitAboveIt(float canvasHeight)
    {
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, stageTopY: 612f);
        float promptBottom = canvasHeight * 0.5f - 292f;
        PortraitSelectionLayout layout = PortraitCombatMath.ComputeSelectionLayout(bands, promptBottom);

        // The Confirm takes the rail's right end, over the inactive discard pile, with the hand edge margin.
        Assert.Equal(948f, layout.ConfirmCenterX, 3);
        Assert.True(layout.ConfirmCenterX > 540f);
        Assert.True(layout.ConfirmCenterX - 100f * layout.ConfirmScale >= PortraitCombatMath.HandEdgeMargin - 0.01f);
        Assert.True(layout.ConfirmCenterX + 100f * layout.ConfirmScale <= 1080f - PortraitCombatMath.HandEdgeMargin + 0.01f);
        Assert.InRange(layout.ConfirmCenterY, bands.RailTopY, bands.HandTopY);
        Assert.True(200f * layout.ConfirmScale >= 127f);
        Assert.True(110f * layout.ConfirmScale >= 127f);

        Assert.True(layout.PreviewTopY > promptBottom);
        Assert.InRange(layout.PreviewScale, 0.55f, 0.9f);
        Assert.True(300f * layout.PreviewScale >= 127f);
        Assert.True(layout.PreviewCenterY + PortraitCombatMath.CardDefaultHeight * layout.PreviewScale * 0.5f
            <= bands.RailTopY - PortraitCombatMath.SelectionPreviewGap + 0.01f);
    }

    [Theory]
    [InlineData(1920f)]
    [InlineData(2340f)]
    public void RailSitsBetweenTheBandsWithFortyFourPointControls(float canvasHeight)
    {
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, stageTopY: 612f);
        PortraitHudLayout hud = PortraitCombatMath.ComputeHudLayout(canvasHeight, bands);

        Assert.Equal(bands.RailTopY, hud.RailTopY);
        Assert.True(bands.RailTopY < bands.HandTopY);
        Assert.True(bands.FrontFeetY < bands.RailTopY);

        // Every control's centre line stays inside the rail.
        Assert.InRange(hud.EndTurnShowY + 45f, bands.RailTopY, bands.HandTopY);
        Assert.InRange(hud.EnergyContainerY + 64f, bands.RailTopY, bands.HandTopY);
        Assert.InRange(hud.DrawShowY + 40f, bands.RailTopY, bands.HandTopY);
        Assert.InRange(hud.DiscardShowY + 40f, bands.RailTopY, bands.HandTopY);
        Assert.InRange(hud.ExhaustShowY + 40f, bands.RailTopY, bands.HandTopY);

        // End Turn is the launcher's 220x90 at 1.42 (312.4x127.8); the piles are 80-unit art at 1.543 (123.4 drawn,
        // 42.9 points, with the touch area grown back to the 44-point guidance around the drawn pile), energy 128 at 1.12.
        Assert.Equal(1.42f, hud.EndTurnScale, 3);
        Assert.Equal(312.4f, 220f * hud.EndTurnScale, 2);
        Assert.Equal(127.8f, 90f * hud.EndTurnScale, 2);
        Assert.True(90f * hud.EndTurnScale >= 127f);
        Assert.Equal(1.543f, hud.PileScale, 3);
        Assert.True(80f * hud.PileScale >= 120f);
        Assert.True(128f * hud.EnergyScale >= 127f);
        Assert.True(hud.EndTurnShowY + 45f + 90f * hud.EndTurnScale * 0.5f <= bands.HandTopY);
        Assert.True(hud.PingShowY < bands.RailTopY);

        // End Turn is centred for every character, and its box stays clear of the energy orb, the star badge, the
        // draw/discard/exhaust piles and the Confirm rail end.
        float endTurnVisualLeft = hud.EndTurnShowX - 110f * (hud.EndTurnScale - 1f);
        float endTurnVisualRight = endTurnVisualLeft + 220f * hud.EndTurnScale;
        float endTurnVisualTop = hud.EndTurnShowY + 45f * (1f - hud.EndTurnScale);
        float endTurnVisualBottom = endTurnVisualTop + 90f * hud.EndTurnScale;
        Assert.Equal(540f, endTurnVisualLeft + 110f * hud.EndTurnScale, 3);
        Assert.True(endTurnVisualLeft > hud.DrawShowX + 40f * (1f - hud.PileScale) + 80f * hud.PileScale);
        Assert.True(endTurnVisualRight < hud.ExhaustShowX + 40f * (1f - hud.PileScale));
        Assert.True(endTurnVisualRight < hud.DiscardShowX + 40f * (1f - hud.PileScale));
        Assert.True(endTurnVisualTop >= bands.RailTopY + 0.01f);
        Assert.True(endTurnVisualBottom <= bands.HandTopY - 0.01f);

        // The energy counter's orb centre is 267 canvas units (a quarter of the canvas width), which leaves 45 units to
        // End Turn's left edge. The star keeps its native scene size and only inherits the counter's 1.12 scale.
        Assert.Equal(203f, hud.EnergyContainerX, 3);
        float orbSize = PortraitCombatMath.EnergyOrbSize * hud.EnergyScale;
        float orbCenterX = hud.EnergyContainerX + PortraitCombatMath.EnergyOrbSize * 0.5f;
        float orbCenterY = hud.EnergyContainerY + PortraitCombatMath.EnergyOrbSize * 0.5f;
        float orbLeft = orbCenterX - orbSize * 0.5f;
        float orbRight = orbCenterX + orbSize * 0.5f;
        float orbTop = orbCenterY - orbSize * 0.5f;
        float orbBottom = orbCenterY + orbSize * 0.5f;

        const float starSceneScale = 0.8f;
        float starSize = PortraitCombatMath.EnergyOrbSize * starSceneScale * hud.EnergyScale;
        Assert.Equal(orbSize * 0.8f, starSize, 2);
        Assert.Equal(orbCenterX + orbSize * PortraitCombatMath.StarOrbOffsetX, hud.StarCenterX, 2);
        Assert.Equal(orbCenterY + orbSize * PortraitCombatMath.StarOrbOffsetY, hud.StarCenterY, 2);

        float starHalf = starSize * 0.5f;
        float starLeft = hud.StarCenterX - starHalf;
        float starRight = hud.StarCenterX + starHalf;
        float starTop = hud.StarCenterY - starHalf;
        float starBottom = hud.StarCenterY + starHalf;

        // End Turn (now back at 1.42) clears the shifted orb and the star badge, and leaves room for Confirm.
        Assert.True(endTurnVisualLeft > orbRight);
        Assert.True(endTurnVisualLeft - orbRight >= 44f);
        Assert.True(endTurnVisualLeft > starRight);
        Assert.True(endTurnVisualRight < 948f - 100f * PortraitCombatMath.SelectionConfirmScale);

        // The star badge's lower-left placement: native size, no lift, still overlapping the orb's lower-left art. Its
        // left tip is the only part near the draw pile, and the star draws in front of the orb and of the "3/3" (it stays
        // the counter's last child), so neither count is covered.
        Assert.True(starLeft < orbRight && starRight > orbLeft && starTop < orbBottom && starBottom > orbTop);
        Assert.Equal(orbSize * -0.4f, hud.StarCenterX - orbCenterX, 2);
        Assert.Equal(orbSize * 0.3f, hud.StarCenterY - orbCenterY, 2);
        Assert.True(hud.StarCenterX < orbCenterX);
        Assert.True(hud.StarCenterY > orbCenterY);
        float drawRight = hud.DrawShowX + 40f * (1f - hud.PileScale) + 80f * hud.PileScale;
        Assert.True(starLeft > drawRight, $"star left {starLeft} vs draw pile right {drawRight}.");

        // Ink model: advance width + 14 outline each side horizontally; digit ascent + 14 outline vertically. The proof's
        // star count is one digit. The number is centred on the star (the native CountLabel box is 41 units lower), so it
        // stays inside the badge and clear of the pile and End Turn; it reaches the "3/3" only at their lower-left corner,
        // which is the overlap the star is drawn over.
        const float starInkScale = starSceneScale * 1.12f;
        const float digitAdvance = 19f;
        const float digitAscent = 36f;
        const float starOutline = 14f;
        float starInkHeight = (digitAscent + 2f * starOutline) * starInkScale;
        float endTurnLeft = hud.EndTurnShowX - 110f * (hud.EndTurnScale - 1f);

        foreach (int digits in new[] { 1 })
        {
            float inkWidth = (digitAdvance * digits + 2f * starOutline) * starInkScale;
            float inkLeft = hud.StarCenterX - inkWidth * 0.5f;
            float inkRight = hud.StarCenterX + inkWidth * 0.5f;
            float inkBottom = hud.StarCenterY + starInkHeight * 0.5f;
            Assert.True(inkLeft > drawRight, $"{digits}-digit star ink left {inkLeft} vs draw right {drawRight}.");
            Assert.True(inkBottom < bands.HandTopY,
                $"{digits}-digit star ink bottom {inkBottom} vs hand top {bands.HandTopY}.");
            Assert.True(inkLeft >= starLeft && inkRight <= starRight);
            Assert.True(inkRight < endTurnLeft);
            Assert.True(inkLeft > 0f && inkBottom <= canvasHeight);
        }
        Assert.True(starSize >= 100f); // readable
    }

    [Theory]
    [InlineData(1920f)]
    [InlineData(2340f)]
    public void TenSelectedPreviewsWrapIntoTwoRowsInsideThePromptRailBand(float canvasHeight)
    {
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, stageTopY: 612f);
        float promptBottom = canvasHeight * 0.5f - 292f;
        PortraitSelectedCardsLayout layout = PortraitCombatMath.ComputeSelectedCardsLayout(10, bands, promptBottom);

        Assert.Equal(2, layout.Rows);
        Assert.Equal(5, layout.Columns);
        float top = Math.Max(bands.StageTopY + 8f, promptBottom + 8f);
        float bottom = bands.RailTopY - PortraitCombatMath.SelectionPreviewGap;
        float totalWidth = layout.CardWidth + layout.StepX * (layout.Columns - 1);
        float totalHeight = layout.CardHeight + layout.StepY * (layout.Rows - 1);

        Assert.True(totalWidth * layout.Scale <= 1080f - 32f + 0.01f);
        Assert.True(totalHeight * layout.Scale <= bottom - top + 0.01f);
        Assert.True(layout.StepX > layout.CardWidth);
        Assert.True(layout.StepY > layout.CardHeight);
        Assert.True(layout.CenterY - totalHeight * layout.Scale * 0.5f >= top - 0.01f);
        Assert.True(layout.CenterY + totalHeight * layout.Scale * 0.5f <= bottom + 0.01f);

        for (int i = 0; i < 10; i++)
        {
            int row = i / layout.Columns;
            int column = i % layout.Columns;
            int rowCount = Math.Min(layout.Columns, 10 - row * layout.Columns);
            float x = 540f + (column - (rowCount - 1) * 0.5f) * layout.StepX * layout.Scale;
            float y = layout.CenterY + (row - (layout.Rows - 1) * 0.5f) * layout.StepY * layout.Scale;
            float halfW = layout.CardWidth * layout.Scale * 0.5f;
            float halfH = layout.CardHeight * layout.Scale * 0.5f;
            Assert.InRange(x - halfW, 16f - 0.01f, 1064f);
            Assert.InRange(x + halfW, 16f, 1064f + 0.01f);
            Assert.InRange(y - halfH, top - 0.01f, bottom);
            Assert.InRange(y + halfH, top, bottom + 0.01f);
        }
    }

    [Fact]
    public void FiveSelectedPreviewsStayInOneRow()
    {
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(2340f, stageTopY: 612f);
        PortraitSelectedCardsLayout layout = PortraitCombatMath.ComputeSelectedCardsLayout(5, bands, 878f);
        Assert.Equal(1, layout.Rows);
        Assert.Equal(5, layout.Columns);
        Assert.True(layout.Scale > 0.7f);
    }

    [Fact]
    public void FiveEnemyAndPetStageUsesTwoBackPlusThreeFrontStaggerWithoutCrossingColumns()
    {
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight: 2340f, stageTopY: 436f);

        PortraitCreatureSlot[] allies = PortraitCombatMath.ComputeAllyStageSlots(
            playerCount: 1, petCount: 1, hasOrbs: true, bands);
        Assert.Equal(2, allies.Length);
        Assert.All(allies, slot => Assert.InRange(slot.CanvasX, 120f, 395f));
        Assert.True(allies[1].CanvasFeetY > allies[0].CanvasFeetY + 150f);

        var shape = new PortraitCombatMath.PortraitStageShape(Capacity: 5, bands.Compact, Dominant: false, Segmented: false);
        PortraitCreatureSlot[] enemies = PortraitCombatMath.ComputeEnemyStageSlots(shape, bands);
        Assert.Equal(5, enemies.Length);
        Assert.Equal(2, enemies.Count(e => e.RowDepth == 0));
        Assert.Equal(3, enemies.Count(e => e.RowDepth == 1));
        Assert.All(enemies, slot => Assert.InRange(slot.CanvasX, 480f, 1010f));

        // The two ranks share columns, ordered left to right; the back rank stands exactly one 250-unit rank higher.
        for (int i = 0; i + 1 < enemies.Length; i++)
            Assert.True(enemies[i].CanvasX < enemies[i + 1].CanvasX);
        Assert.All(enemies, slot => Assert.Equal(
            slot.RowDepth == 0 ? bands.FrontFeetY - 250f : bands.FrontFeetY, slot.CanvasFeetY));
    }

    [Fact]
    public void EnemiesKeepAFifthOfTheWidthFromTheLonePlayer()
    {
        // The Waterfall Giant fight with the earlier layout: the player's hit box ended at 353, the giant's started at 496.
        Assert.Equal(216f, PortraitCombatMath.PlayerEnemyGap);
        Assert.Equal(527f, PortraitCombatMath.EnemyStageLeft(playersHitRight: 311f, alliesHitRight: 311f, minimum: 360f));
        // A pet in front of the player keeps half a gap from the enemies.
        Assert.Equal(560f, PortraitCombatMath.EnemyStageLeft(playersHitRight: 300f, alliesHitRight: 452f, minimum: 360f));
        Assert.Equal(360f, PortraitCombatMath.EnemyStageLeft(playersHitRight: 0f, alliesHitRight: 0f, minimum: 360f));

        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight: 2340f, stageTopY: 436f);
        PortraitCreatureSlot[] lone = PortraitCombatMath.ComputeAllyStageSlots(1, 0, hasOrbs: false, bands);
        Assert.Equal(PortraitCombatMath.LonePlayerX, lone[0].CanvasX);
        Assert.Equal(0.80f, lone[0].BaseScale);
    }

    [Fact]
    public void ASqueezedEnemyGroupStartsAtTheGapAndKeepsItsArrangement()
    {
        Assert.Equal(1f, PortraitCombatMath.EnemyGroupSqueeze(leftmostHitLeft: 540f, enemyLeft: 527f));
        float k = PortraitCombatMath.EnemyGroupSqueeze(leftmostHitLeft: 415f, enemyLeft: 527f);
        Assert.InRange(k, 0.82f, 0.84f);

        // The leftmost hit box started 140 units left of its slot's centre; drawn k times as large, it starts 140 * k left
        // of the squeezed centre, which is where the gap ends.
        var slot = new PortraitCreatureSlot(555f, 1200f, 0.76f, 1);
        PortraitCreatureSlot squeezed = PortraitCombatMath.SqueezeEnemySlot(slot, k);
        Assert.Equal(527f, squeezed.CanvasX - 140f * k, 0.5f);
        Assert.Equal(slot.CanvasFeetY, squeezed.CanvasFeetY);
        Assert.Equal(0.76f * k, squeezed.BaseScale, 0.0001f);
        Assert.Equal(slot, PortraitCombatMath.SqueezeEnemySlot(slot, 1f));
    }

    [Fact]
    public void CombatStageDependsOnlyOnTheBandsAndNeverOnTheHand()
    {
        // The bands are the whole stage geometry: canvas height and stage top in, nothing about cards out. Two passes give
        // the same stage, whatever the hand holds, and the front rank stands on the fixed foot line.
        PortraitCombatBands tall = PortraitCombatMath.ComputeCombatBands(canvasHeight: 2340f, stageTopY: 436f);
        Assert.False(tall.Compact);
        Assert.Equal(tall, PortraitCombatMath.ComputeCombatBands(2340f, 436f));
        Assert.True(tall.FrontFeetY < tall.RailTopY && tall.FrontFeetY > tall.StageTopY);

        var shape = new PortraitCombatMath.PortraitStageShape(Capacity: 2, tall.Compact, Dominant: false, Segmented: false);
        PortraitCreatureSlot[] first = PortraitCombatMath.ComputeEnemyStageSlots(shape, tall);
        PortraitCreatureSlot[] second = PortraitCombatMath.ComputeEnemyStageSlots(shape, tall);
        Assert.True(first.SequenceEqual(second));
        Assert.Equal(tall.FrontFeetY, first[0].CanvasFeetY);
        Assert.Equal(tall.FrontFeetY - 118f, first[1].CanvasFeetY);

        PortraitCombatBands compact = PortraitCombatMath.ComputeCombatBands(canvasHeight: 1920f, stageTopY: 436f);
        Assert.True(compact.Compact);
        Assert.Equal(compact.HandTopY - 240f, compact.FrontFeetY);
    }

    [Fact]
    public void SharedPeerScaleUsesTheTightestFrameAndStopsAtTheLegibilityFloor()
    {
        var roomy = new[] { new PortraitCombatMath.PortraitFrameFit(300f, 420f, 500f, 600f, 0.88f) };
        Assert.Equal(1f, PortraitCombatMath.SharedPeerScale(roomy, out bool floored));
        Assert.False(floored);

        // A large monster in a tight frame needs 0.483 / 0.82 = 0.589 of its slot, below the floor: the room shares the
        // floor instead of shrinking every peer to the giant's size.
        var crowded = new[]
        {
            new PortraitCombatMath.PortraitFrameFit(300f, 420f, 500f, 600f, 0.88f),
            new PortraitCombatMath.PortraitFrameFit(600f, 800f, 290f, 420f, 0.82f),
        };
        float peer = PortraitCombatMath.SharedPeerScale(crowded, out floored);
        Assert.True(floored);
        Assert.Equal(PortraitCombatMath.PeerScaleFloor, peer);
        Assert.InRange(PortraitCombatMath.FitCreatureScale(0.82f, peer, 600f, 800f, 290f, 420f), 0.48f, 0.49f);
        Assert.Equal(0.88f * PortraitCombatMath.PeerScaleFloor,
            PortraitCombatMath.FitCreatureScale(0.88f, peer, 300f, 420f, 500f, 600f), 0.0001f);

        // Without the floor the same shared scale shrinks every peer by the same amount.
        var medium = new[] { new PortraitCombatMath.PortraitFrameFit(600f, 800f, 500f, 600f, 0.88f) };
        float gentle = PortraitCombatMath.SharedPeerScale(medium, out floored);
        Assert.False(floored);
        Assert.InRange(gentle, 0.84f, 0.86f);
    }

    [Fact]
    public void SharedRoomScaleIsAbsoluteAndCappedByTheHeroFrame()
    {
        // The hero's frame (270x360 over a 242x278 body) is the tightest fit: 1.116, so the room is drawn at 1.116 even
        // though the Nibbit's cell would allow 1.266. One multiplier is resolved for the whole room.
        var room = new[]
        {
            new PortraitCombatMath.PortraitFrameFit(242f, 278f, 270f, 360f, 0.75f),
            new PortraitCombatMath.PortraitFrameFit(297f, 161f, 376f, 346f, 1.05f),
        };
        float shared = PortraitCombatMath.SharedRoomScale(room, out bool floored);
        Assert.False(floored);
        Assert.Equal(270f / 242f, shared, 0.0001f);

        // A monster too large even for the legibility floor cannot shrink the room below it.
        var oversized = new[] { new PortraitCombatMath.PortraitFrameFit(500f, 600f, 270f, 360f, 1f) };
        Assert.Equal(PortraitCombatMath.PeerScaleFloor, PortraitCombatMath.SharedRoomScale(oversized, out floored));
        Assert.True(floored);

        // Nothing caps an ordinary body at more than the preferred shared scale (1.32 on this canvas).
        var tiny = new[] { new PortraitCombatMath.PortraitFrameFit(100f, 100f, 600f, 600f, 1f) };
        Assert.Equal(PortraitCombatMath.PreferredSharedBodyScale, PortraitCombatMath.SharedRoomScale(tiny, out floored));
        Assert.False(floored);
    }

    [Fact]
    public void BroadBackdropFramingIsTheSameRuleAtBothShapes()
    {
        // The Waterfall Giant arena: a 2764.8x1296 art whose authored top sits at -648 in
        // the scene's space and whose TextureRect bottom is at 1181 in the container's local space.
        const float artWidth = 2764.8f;
        const float artHeight = 1296f;
        const float artTopInScene = -648f;
        const float artBottomLocal = 1181f;

        PortraitCombatBands tall = PortraitCombatMath.ComputeCombatBands(canvasHeight: 2340f, stageTopY: 612f);
        float barBottom = tall.StageTopY - 450f;
        PortraitCombatMath.PortraitBackdropFraming tallFrame = PortraitCombatMath.ComputeBroadBackdropFraming(
            artWidth, artHeight, artTopInScene, artBottomLocal, 1080f, tall, barBottom, 1f);
        Assert.Equal(1.065f, tallFrame.Scale, 0.0005f);
        Assert.Equal(tall.HandTopY, tallFrame.ArtBottomY, 0.0001f);
        // The art reaches exactly the bottom of the run bars: no strip of the underlay shows inside the stage.
        Assert.Equal(barBottom - 8f, tallFrame.ArtTopY, 0.1f);
        Assert.True(tallFrame.FloorAligned);
        // The mossy ledge (scene y 320, local 853 once the layer's authored -533 offset is taken off) sits a little above
        // the foot line, so the hero and the giant stand on it.
        Assert.InRange(tallFrame.OriginY + 853f * tallFrame.Scale, tall.FrontFeetY - 70f, tall.FrontFeetY);

        PortraitCombatBands compact = PortraitCombatMath.ComputeCombatBands(canvasHeight: 1920f, stageTopY: 612f);
        float compactBarBottom = compact.StageTopY - 450f;
        PortraitCombatMath.PortraitBackdropFraming compactFrame = PortraitCombatMath.ComputeBroadBackdropFraming(
            artWidth, artHeight, artTopInScene, artBottomLocal, 1080f, compact, compactBarBottom, 1f);
        Assert.Equal(0.8058f, compactFrame.Scale, 0.0005f);
        Assert.Equal(compact.HandTopY, compactFrame.ArtBottomY, 0.0001f);
        Assert.Equal(compactBarBottom - 8f, compactFrame.ArtTopY, 0.1f);
        Assert.InRange(compactFrame.OriginY + 853f * compactFrame.Scale, compact.FrontFeetY - 40f, compact.FrontFeetY);

        // Measuring the wrapper's local rect instead of the scene's top would triple the zoom this arena gets
        // (200 - (-115) instead of 200 - (-648)), which is what the deleted waterfall special case worked around.
        PortraitCombatMath.PortraitBackdropFraming broken = PortraitCombatMath.ComputeBroadBackdropFraming(
            artWidth, artHeight, artBottomLocal - artHeight, artBottomLocal, 1080f, tall, barBottom, 1f);
        Assert.True(broken.Scale > tallFrame.Scale * 2f);
    }

    [Fact]
    public void BroadBackdropFramingFollowsTheArenaTopWhenItIsNotCoverBound()
    {
        // A short arena (a 540x720-ish stage) needs the art's own span: the scale follows the arena, not the bar cover.
        PortraitCombatBands shortBands = PortraitCombatMath.ComputeCombatBands(canvasHeight: 1400f, stageTopY: 400f);
        float barBottom = shortBands.StageTopY - 450f;
        PortraitCombatMath.PortraitBackdropFraming frame = PortraitCombatMath.ComputeBroadBackdropFraming(
            2764.8f, 1296f, -648f, 1181f, 1080f, shortBands, barBottom, 1f);
        float widthCover = 1080f / 2764.8f;
        Assert.True(frame.Scale >= widthCover);
        Assert.Equal(shortBands.HandTopY, frame.ArtBottomY, 0.0001f);
    }

    [Fact]
    public void CoverFramingFillsTheCanvasAndCentresThePicture()
    {
        // The Architect's ending picture: the 6144x2880 base art at 0.47 inside a 2881x1350 scene frame centred on the
        // container origin, so its drawn rectangle is 2887.68 wide, from -675.36 to 678.24.
        const float artWidth = 2887.68f;
        const float artHeight = 1353.6f;
        const float artTop = -675.36f;
        const float artBottom = 678.24f;

        foreach (float canvasHeight in new[] { 1920f, 2340f })
        {
            PortraitCombatMath.PortraitCoverFraming frame = PortraitCombatMath.ComputeCoverFraming(
                artWidth, artHeight, artTop, artBottom, 1080f, canvasHeight);
            // The taller canvas is covered by height; the shorter one too (this picture is much wider than 9:16).
            Assert.Equal(canvasHeight / artHeight, frame.Scale, 0.0005f);
            Assert.True(artWidth * frame.Scale >= 1080f - 0.01f);
            float drawnTop = frame.OriginY + artTop * frame.Scale;
            float drawnBottom = frame.OriginY + artBottom * frame.Scale;
            // No band is left above or below the picture, and its composition centre stays on the canvas centre.
            Assert.True(drawnTop <= 0.01f, $"top band {drawnTop}");
            Assert.True(drawnBottom >= canvasHeight - 0.01f, $"bottom band {drawnBottom}");
            Assert.Equal(canvasHeight * 0.5f, (drawnTop + drawnBottom) * 0.5f, 0.01f);
        }
    }

    [Fact]
    public void EncounterSlotsAreFrozenBeforeTheEggsSpawn()
    {
        // Ovicopter's markers, with only the boss present; the encounter fills the last free slot first (egg5).
        var markers = new List<PortraitCombatMath.PortraitSlotMarker>
        {
            new("egg1", 961f, 678f, 0f),
            new("egg2", 1140f, 697f, 0f),
            new("egg3", 1323f, 654f, 0f),
            new("egg4", 1498f, 705f, 0f),
            new("egg5", 1651f, 665f, 0f),
            new("ovicopter", 1971f, 699f, 200000f),
        };
        string[] names = PortraitCombatMath.OrderEncounterSlotNames(markers, dominant: true);
        Assert.True(names.SequenceEqual(new[] { "ovicopter", "egg1", "egg2", "egg3", "egg4", "egg5" }));

        var table = new List<string>(names);
        Assert.Equal(5, PortraitCombatMath.EnsureSlotIndex(table, "egg5"));
        Assert.Equal(4, PortraitCombatMath.EnsureSlotIndex(table, "egg4"));
        Assert.Equal(4, table.IndexOf("egg4"));
        Assert.Equal(5, table.IndexOf("egg5"));

        // A summon past the frozen table is appended after every slot, so the six placed eggs keep their indices.
        Assert.Equal(6, PortraitCombatMath.EnsureSlotIndex(table, "instance:42"));
        Assert.Equal(names, table.GetRange(0, names.Length));

        // The formation itself is drawn from the same frozen count, so it never depends on how many are alive.
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight: 2340f, stageTopY: 436f);
        var shape = new PortraitCombatMath.PortraitStageShape(6, bands.Compact, Dominant: true, Segmented: false);
        PortraitCreatureSlot[] formation = PortraitCombatMath.ComputeEnemyStageSlots(shape, bands);
        Assert.Equal(6, formation.Length);
        Assert.All(formation, slot => Assert.Equal(bands.FrontFeetY, slot.CanvasFeetY));
    }

    [Fact]
    public void EnemiesStandOnTheFrontFootLineInEveryFormation()
    {
        foreach (float canvasHeight in new[] { 1920f, 2340f })
        {
            PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, stageTopY: 436f);
            for (int capacity = 1; capacity <= 6; capacity++)
            {
                var shape = new PortraitCombatMath.PortraitStageShape(capacity, bands.Compact, Dominant: capacity > 2, Segmented: false);
                PortraitCreatureSlot[] slots = PortraitCombatMath.ComputeEnemyStageSlots(shape, bands);
                Assert.Equal(capacity, slots.Length);
                Assert.All(slots, slot =>
                {
                    Assert.InRange(slot.CanvasX, 8f, 1072f);
                    Assert.True(slot.CanvasFeetY >= bands.StageTopY);
                    Assert.True(slot.CanvasFeetY <= bands.FrontFeetY + 0.1f);
                });
                // Some rank always reaches the front foot line; higher ranks are lifted, never lowered.
                Assert.Contains(slots, slot => slot.CanvasFeetY >= bands.FrontFeetY - 20f);

                var chain = new PortraitCombatMath.PortraitStageShape(3, bands.Compact, Dominant: false, Segmented: true);
                Assert.All(PortraitCombatMath.ComputeEnemyStageSlots(chain, bands),
                    slot => Assert.Equal(bands.FrontFeetY, slot.CanvasFeetY));
            }
        }
    }

    [Fact]
    public void FramesStayInsideTheCanvasAtBothPortraitHeights()
    {
        foreach (float canvasHeight in new[] { 1920f, 2340f })
        {
            float stageTop = canvasHeight <= 1924f ? 586f : 612f;
            PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, stageTop);
            for (int capacity = 1; capacity <= 6; capacity++)
            {
                bool dominant = capacity > 2;
                var shape = new PortraitCombatMath.PortraitStageShape(capacity, bands.Compact, dominant, Segmented: false);
                PortraitCreatureSlot[] slots = PortraitCombatMath.ComputeEnemyStageSlots(shape, bands);
                for (int i = 0; i < slots.Length; i++)
                {
                    PortraitCreatureSlot slot = slots[i];
                    bool bossFrame = dominant && i == 0;
                    float lane = 1080f * 0.635f / Math.Max(1, capacity - 1);
                    float maxW = bossFrame ? 594f : (dominant ? lane * 0.94f : (capacity >= 4 ? 225f : (capacity >= 2 ? 290f : 712f)));
                    float maxH = bossFrame ? 900f : (dominant ? 340f : (capacity >= 4 ? 300f : (capacity == 3 ? 380f : (capacity == 2 ? 420f : 600f))));
                    float budget = Math.Min(maxH, Math.Max(140f, slot.CanvasFeetY - bands.StageTopY - (bands.Compact ? 0f : 104.8f)));
                    float scale = PortraitCombatMath.FitCreatureScale(slot.BaseScale, 1f, 300f, 400f, maxW, budget);
                    float halfWidth = 300f * scale * 0.5f;
                    float height = 400f * scale;
                    Assert.InRange(slot.CanvasX - halfWidth, -8f, 1080f);
                    Assert.InRange(slot.CanvasX + halfWidth, 0f, 1088f);
                    Assert.True(slot.CanvasFeetY <= bands.HandTopY);
                    Assert.True(slot.CanvasFeetY - height >= bands.StageTopY - 160f,
                        $"{canvasHeight}:{capacity}:{i} top {slot.CanvasFeetY - height} stage {bands.StageTopY}");
                }
            }
        }
    }

    [Theory]
    [InlineData(1920f)]
    [InlineData(2340f)]
    public void CrowdedRowKeepsBodiesBarsAndIntentsClear(float canvasHeight)
    {
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, stageTopY: 436f);

        void Check(int capacity, int bossSlot, float nativeW, float nativeH, int intents, int bossIntents)
        {
            PortraitCombatMath.PortraitStageCells cells =
                PortraitCombatMath.ComputeCrowdedCells(capacity, bossSlot, leftEdge: 360f, bands);
            var rects = new List<(float L, float T, float R, float B)>();
            for (int i = 0; i < capacity; i++)
            {
                bool boss = i == bossSlot;
                float bodyW = nativeW;
                float bodyH = nativeH;
                float baseScale = boss ? 0.94f : 0.82f;
                float scale = Math.Clamp(Math.Min(baseScale, Math.Min(cells.Widths[i] / bodyW, cells.Heights[i] / bodyH)),
                    PortraitCombatMath.MinBodyScale, PortraitCombatMath.MaxBodyScale);
                float w = bodyW * scale;
                float h = bodyH * scale;
                float top = bands.FrontFeetY - h;
                rects.Add((cells.X[i] - w / 2f, top, cells.X[i] + w / 2f, bands.FrontFeetY));
                rects.Add((cells.X[i] - cells.Bars[i] / 2f, bands.FrontFeetY, cells.X[i] + cells.Bars[i] / 2f, bands.FrontFeetY + 44f));
                float intentW = (boss ? bossIntents : intents) * 93f;
                rects.Add((cells.X[i] - intentW / 2f, top - 105f, cells.X[i] + intentW / 2f, top));
            }
            for (int a = 0; a < rects.Count; a++)
            {
                for (int b = a + 1; b < rects.Count; b++)
                {
                    (float L, float T, float R, float B) p = rects[a];
                    (float L, float T, float R, float B) q = rects[b];
                    bool intersects = p.L < q.R && q.L < p.R && p.T < q.B && q.T < p.B;
                    Assert.False(intersects, $"{canvasHeight}: cap {capacity}: rect {a} {p} meets {b} {q}");
                }
            }
            Assert.All(cells.X, x => Assert.InRange(x, 0f, 1080f));
        }

        for (int capacity = 1; capacity <= 5; capacity++)
            Check(capacity, bossSlot: -1, nativeW: 300f, nativeH: 400f, intents: 1, bossIntents: 1);

        // Ovicopter: the boss with all five reserved egg slots filled, in their frozen cells.
        Check(6, bossSlot: 5, nativeW: 277f, nativeH: 300f, intents: 1, bossIntents: 2);
    }

    [Fact]
    public void TallBossRowClearsTheFrontRow()
    {
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight: 2340f, stageTopY: 612f);
        PortraitCombatMath.PortraitStageCells cells =
            PortraitCombatMath.ComputeCrowdedCells(capacity: 5, bossSlot: -1, leftEdge: 360f, bands);
        float cellWidth = cells.Widths[0];
        float frontHeight = Math.Min(cellWidth / 277f * 300f, bands.FrontFeetY - bands.StageTopY);
        float bossFeet = bands.FrontFeetY - frontHeight - 105f - 52f;
        float bossHeight = bossFeet - bands.StageTopY - 105f;
        float bossScale = Math.Clamp(Math.Min(0.94f, Math.Min(420f / 403f, bossHeight / 476f)),
            PortraitCombatMath.MinBodyScale, PortraitCombatMath.MaxBodyScale);
        float bossW = 403f * bossScale;
        float bossH = 476f * bossScale;
        var bossRects = new (float L, float T, float R, float B)[]
        {
            (540f - bossW / 2f, bossFeet - bossH, 540f + bossW / 2f, bossFeet),
            (540f - 150f, bossFeet, 540f + 150f, bossFeet + 44f),
            (540f - 93f, bossFeet - bossH - 105f, 540f + 93f, bossFeet - bossH),
        };
        var front = new List<(float L, float T, float R, float B)>();
        for (int i = 0; i < 5; i++)
        {
            float scale = Math.Clamp(Math.Min(0.82f, Math.Min(cells.Widths[i] / 277f, 300f > 0 ? cells.Heights[i] / 300f : 1f)),
                PortraitCombatMath.MinBodyScale, PortraitCombatMath.MaxBodyScale);
            float h = 300f * scale;
            float top = bands.FrontFeetY - h;
            front.Add((cells.X[i] - 277f * scale / 2f, top, cells.X[i] + 277f * scale / 2f, bands.FrontFeetY));
            front.Add((cells.X[i] - cells.Bars[i] / 2f, bands.FrontFeetY, cells.X[i] + cells.Bars[i] / 2f, bands.FrontFeetY + 44f));
            front.Add((cells.X[i] - 46.5f, top - 105f, cells.X[i] + 46.5f, top));
        }
        foreach ((float L, float T, float R, float B) p in bossRects)
        {
            foreach ((float L, float T, float R, float B) q in front)
                Assert.False(p.L < q.R && q.L < p.R && p.T < q.B && q.T < p.B, $"boss {p} meets front {q}");
        }
        Assert.True(bossHeight >= 180f, $"boss height {bossHeight}");
    }

    [Fact]
    public void BossSlotRequiresAUniqueMonsterWellAboveTheRest()
    {
        var fourExoskeletons = new[]
        {
            (Slot: 1, Area: 100f, Type: "Exoskeleton"),
            (Slot: 2, Area: 100f, Type: "Exoskeleton"),
            (Slot: 3, Area: 100f, Type: "Exoskeleton"),
            (Slot: 4, Area: 100f, Type: "Exoskeleton"),
        };
        Assert.Equal(-1, PortraitCombatMath.PickBossIndex(fourExoskeletons, capacity: 4, segmented: false));

        var equalStrangers = new[] { (Slot: 0, Area: 100f, Type: "Slime"), (Slot: 1, Area: 100f, Type: "Beetle") };
        Assert.Equal(-1, PortraitCombatMath.PickBossIndex(equalStrangers, capacity: 4, segmented: false));

        var sameTypeBoss = new[] { (Slot: 0, Area: 900f, Type: "Slime"), (Slot: 1, Area: 100f, Type: "Slime") };
        Assert.Equal(-1, PortraitCombatMath.PickBossIndex(sameTypeBoss, capacity: 4, segmented: false));

        var ovicopter = new[]
        {
            (Slot: 0, Area: 900f, Type: "Ovicopter"),
            (Slot: 1, Area: 100f, Type: "ToughEgg"),
            (Slot: 2, Area: 100f, Type: "ToughEgg"),
        };
        Assert.Equal(0, PortraitCombatMath.PickBossIndex(ovicopter, capacity: 4, segmented: false));
        Assert.Equal(-1, PortraitCombatMath.PickBossIndex(ovicopter, capacity: 4, segmented: true));

        var lone = new[] { (Slot: 5, Area: 900f, Type: "Ovicopter") };
        Assert.Equal(5, PortraitCombatMath.PickBossIndex(lone, capacity: 6, segmented: false));
        Assert.Equal(-1, PortraitCombatMath.PickBossIndex(lone, capacity: 2, segmented: false));
    }

    [Fact]
    public void EncounterBodyMultipliersVaryByEncounter()
    {
        Assert.Equal(1f, PortraitCombatMath.EncounterBodyMultiplier("Exoskeleton|LeafSlimeM"));
        Assert.Equal(0.83f, PortraitCombatMath.EncounterBodyMultiplier("CeremonialBeast"));
        Assert.Equal(0.78f, PortraitCombatMath.EncounterBodyMultiplier("WaterfallGiant"));
        Assert.Equal(0.73f, PortraitCombatMath.EncounterBodyMultiplier("KnowledgeDemon"));
        Assert.Equal(0.66f, PortraitCombatMath.EncounterBodyMultiplier("KinFollower|KinPriest"));
        // The Kaiser Crab is excluded by the caller, not by a special multiplier.
        Assert.Equal(1f, PortraitCombatMath.EncounterBodyMultiplier("Crusher|Rocket"));
    }

    [Fact]
    public void TallFormationUsesCanvasDepths()
    {
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight: 2340f, stageTopY: 612f);
        float floor = bands.FrontFeetY;

        PortraitCreatureSlot[] pair = PortraitCombatMath.ComputeTallFormation(2, bands);
        Assert.Equal(1080f * 0.475f, pair[0].CanvasX, 0.5f);
        Assert.Equal(1080f * 0.805f, pair[1].CanvasX, 0.5f);
        Assert.Equal(floor - 64.28571f, pair[0].CanvasFeetY, 0.5f);
        Assert.Equal(floor - 12.857142f, pair[1].CanvasFeetY, 0.5f);

        PortraitCreatureSlot[] three = PortraitCombatMath.ComputeTallFormation(3, bands);
        Assert.Equal(0, three[1].RowDepth);
        Assert.Equal(floor - 327.85715f, three[1].CanvasFeetY, 0.5f);
        Assert.Equal(1, three[0].RowDepth);
        Assert.Equal(floor + 80.35714f, three[0].CanvasFeetY, 0.5f);

        PortraitCreatureSlot[] five = PortraitCombatMath.ComputeTallFormation(5, bands);
        Assert.Equal(1080f * 0.18f, five[0].CanvasX, 0.5f);
        Assert.Equal(0, five[0].RowDepth);
        Assert.Equal(1080f * 0.49f, five[1].CanvasX, 0.5f);
        Assert.Equal(1080f * 0.81f, five[4].CanvasX, 0.5f);

        // Frame width cannot exceed the column pitch; height is the 305-unit body cap.
        (float w3, float h3, float l3, float r3) = PortraitCombatMath.TallFormationFrame(three, 0, bands);
        Assert.InRange(w3, 60f, MathF.Abs(three[2].CanvasX - three[0].CanvasX) - 9f);
        Assert.InRange(h3, 141f, 305f);
        Assert.True(l3 >= 8f && r3 <= 1072f);
    }

    [Fact]
    public void DominantStageKeepsBossFrameAndLanesOnTheFrontLine()
    {
        // The Phrog Parasite elite: one boss and four reserved wriggler slots. The dominant frame at both shapes, without
        // its boss lift, so the boss's feet stay on the front foot line and the lanes stand on the same band.
        foreach (float canvasHeight in new[] { 2340f, 1920f })
        {
            bool compact = canvasHeight < 2000f;
            PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, stageTopY: 612f);
            Assert.Equal(compact, bands.Compact);
            PortraitCombatMath.PortraitDominantStage stage = PortraitCombatMath.ComputeDominantStage(5, bands);
            Assert.Equal(5, stage.Slots.Length);
            Assert.Equal(1080f * (compact ? 0.76f : 0.70f), stage.Slots[0].CanvasX, 0.5f);
            Assert.Equal(1080f * (compact ? 0.43f : 0.55f), stage.BossWidth, 0.5f);
            Assert.Equal(0, stage.Slots[0].RowDepth);
            Assert.All(stage.Slots, slot => Assert.Equal(bands.FrontFeetY, slot.CanvasFeetY));
            // The boss frame ends exactly at the canvas's right margin (1053) at both shapes.
            Assert.Equal(1053f, stage.Slots[0].CanvasX + stage.BossWidth * 0.5f, 0.5f);

            // Four equal lanes from 0.35 over 0.635 of the width, 0.94 filled: centres 463.7/635.2/806.6/978.1.
            float laneWidth = 1080f * 0.635f / 4f * 0.94f;
            Assert.Equal(laneWidth, stage.LaneWidth, 0.05f);
            Assert.Equal(1080f * 0.429375f, stage.Slots[1].CanvasX, 0.5f);
            Assert.Equal(1080f * 0.588125f, stage.Slots[2].CanvasX, 0.5f);
            Assert.Equal(1080f * 0.746875f, stage.Slots[3].CanvasX, 0.5f);
            Assert.Equal(1080f * 0.905625f, stage.Slots[4].CanvasX, 0.5f);
            for (int i = 1; i < stage.Slots.Length; i++)
            {
                Assert.Equal(1, stage.Slots[i].RowDepth);
                Assert.Equal(0.76f, stage.Slots[i].BaseScale);
                if (i > 1)
                    Assert.True(stage.Slots[i].CanvasX - stage.Slots[i - 1].CanvasX - laneWidth > 9f);
            }
            Assert.True(stage.Slots[1].CanvasX - laneWidth * 0.5f >= 1080f * 0.35f);
            Assert.True(stage.Slots[4].CanvasX + laneWidth * 0.5f <= 1080f * 0.985f);

            // A three-slot summoner keeps two equal lanes; a lone body has no lane at all.
            PortraitCombatMath.PortraitDominantStage three = PortraitCombatMath.ComputeDominantStage(3, bands);
            Assert.Equal(1080f * 0.50875f, three.Slots[1].CanvasX, 0.5f);
            Assert.Equal(1080f * 0.82625f, three.Slots[2].CanvasX, 0.5f);
            Assert.Equal(1080f * 0.635f / 2f * 0.94f, three.LaneWidth, 0.05f);
            Assert.Equal(0f, PortraitCombatMath.ComputeDominantStage(1, bands).LaneWidth, 0.01f);
        }
    }

    [Fact]
    public void TallThinRostersOfThreeOrFourChooseTheSingleRow()
    {
        // The Phantasmal Gardeners (208 by 380, four of them): the 2x2 cell is 313 wide but only 200 tall above
        // its intent band, so the table's tightest fit is 0.528; the single row across the pier gives 0.855.
        PortraitCombatBands tall = PortraitCombatMath.ComputeCombatBands(canvasHeight: 2340f, stageTopY: 612f);
        PortraitCombatBands compact = PortraitCombatMath.ComputeCombatBands(canvasHeight: 1920f, stageTopY: 612f);
        const float intentBand = 64f * 1.45f + 12f;
        // The hero's drawn hit box (Ironclad: x 180, local right 121) at its own room scale ends at 315, so the row starts
        // at 321 — the same edge the crowded stage freezes at 9:16.
        const float heroRight = 315f;
        const float heroRoomScale = 1.116f;
        (float Width, float Height)[] gardeners = { (208f, 380f), (208f, 380f), (208f, 380f), (208f, 380f) };
        PortraitCombatMath.PortraitRowFit row =
            PortraitCombatMath.FitSingleRow(4, gardeners, tall, heroRight + 6f, heroRoomScale, intentBand);
        Assert.Equal(321f, row.LeftEdge, 0.01f);
        Assert.Equal(177.75f, row.CellWidth, 0.05f);
        Assert.Equal(0.8546f, row.RoomScale, 0.001f);
        Assert.Equal(208f * row.RoomScale, row.CellWidth, 0.1f);
        // 9:16 needs no intent band above the body: the same cells and the same scale, which is why the short room already
        // stands them in this row.
        Assert.Equal(row.RoomScale, PortraitCombatMath.FitSingleRow(4, gardeners, compact, heroRight + 6f,
            heroRoomScale, 0f).RoomScale, 0.001f);

        float formation = Math.Min(313.2f / 208f, (Math.Min(305.36f, tall.FrontFeetY - tall.StageTopY) - intentBand) / 380f);
        Assert.Equal(0.5278f, formation, 0.001f);
        Assert.True(PortraitCombatMath.PrefersSingleRow(gardeners, formation, row.RoomScale));
        Assert.InRange(row.RoomScale / Math.Min(formation, PortraitCombatMath.MaxBodyScale), 1.6f, 1.7f);

        // Every other 3–4 body roster keeps its authored table: the row is not materially bigger (ConstructMenagerie 1.35,
        // Knights 1.10) or the bodies are not tall-thin (Exoskeletons 0.98, Inklets 1.34, Scrolls 0.91).
        (float Width, float Height)[] constructs = { (272f, 306f), (180f, 294f), (180f, 294f) };
        Assert.Equal(0.8836f, PortraitCombatMath.FitSingleRow(3, constructs, tall, 321f, heroRoomScale, intentBand).RoomScale, 0.002f);
        Assert.False(PortraitCombatMath.PrefersSingleRow(constructs, 0.6554f,
            PortraitCombatMath.FitSingleRow(3, constructs, tall, 321f, heroRoomScale, intentBand).RoomScale));

        (float Width, float Height)[] knights = { (404f, 372f), (364f, 330f), (220f, 327f) };
        Assert.False(PortraitCombatMath.PrefersSingleRow(knights, 0.5391f,
            PortraitCombatMath.FitSingleRow(3, knights, tall, 321f, heroRoomScale, intentBand).RoomScale));

        (float Width, float Height)[] exoskeletons = { (172f, 168f), (172f, 168f), (172f, 168f), (172f, 168f) };
        Assert.False(PortraitCombatMath.PrefersSingleRow(exoskeletons, 1.1938f,
            PortraitCombatMath.FitSingleRow(4, exoskeletons, tall, 321f, heroRoomScale, intentBand).RoomScale));

        (float Width, float Height)[] inklets = { (140f, 187f), (140f, 187f), (140f, 187f) };
        Assert.False(PortraitCombatMath.PrefersSingleRow(inklets, 1.0725f,
            PortraitCombatMath.FitSingleRow(3, inklets, tall, 321f, heroRoomScale, intentBand).RoomScale));

        (float Width, float Height)[] scrolls = { (183f, 166f), (183f, 166f), (183f, 166f), (183f, 166f) };
        Assert.False(PortraitCombatMath.PrefersSingleRow(scrolls, 1.2083f,
            PortraitCombatMath.FitSingleRow(4, scrolls, tall, 321f, heroRoomScale, intentBand).RoomScale));

        (float Width, float Height)[] slugs = { (232f, 164f), (232f, 164f), (232f, 164f) };
        Assert.False(PortraitCombatMath.PrefersSingleRow(slugs, 1.2232f,
            PortraitCombatMath.FitSingleRow(3, slugs, tall, 321f, heroRoomScale, intentBand).RoomScale));

        // Two bodies never qualify (they use the pair table), however tall they are.
        Assert.False(PortraitCombatMath.PrefersSingleRow(new (float, float)[] { (208f, 380f), (208f, 380f) }, 0.5f, 1f));
    }

    [Fact]
    public void UnplayableCardDimmingIsStrictlyNeutralGreyWithZeroColourCast()
    {
        var (r, g, b, a) = PortraitCombatMath.UnplayableBodyModulateRgba;
        Assert.Equal(0.54f, r);
        Assert.Equal(r, g);
        Assert.Equal(g, b);
        Assert.Equal(1f, a);
    }

    [Fact]
    public void BlockedHpTextShiftsFirstAndShrinksOnlyWhenTheBarIsTooNarrow()
    {
        // The hero's "9999/9999" (drawn ink 207 wide) with a shield whose drawn right edge is 45 past the bar's left. A
        // bar 265 wide takes the shift alone: the digits start 4 after the shield and end 4 before the bar's end.
        PortraitCombatMath.HpTextBlockFit roomy = PortraitCombatMath.FitHpTextBesideBlock(
            inkLeft: (265f - 207f) * 0.5f, inkWidth: 207f, pivotX: 0f, shieldRight: 45f, barRight: 265f, gap: 4f,
            minScale: 0.50f);
        Assert.Equal(1f, roomy.Scale, 0.001f);
        Assert.Equal(20f, roomy.Shift, 0.01f);
        Assert.Equal(49f, (265f - 207f) * 0.5f + roomy.Shift, 0.01f);
        Assert.True((265f - 207f) * 0.5f + roomy.Shift + 207f <= 265f - 4f + 0.01f);

        // A 207-wide bar cannot take the shift: the digits scale by what the gap and the bar leave, and still land 4
        // after the shield and 4 before the bar's end.
        PortraitCombatMath.HpTextBlockFit exact = PortraitCombatMath.FitHpTextBesideBlock(
            inkLeft: 0f, inkWidth: 207f, pivotX: 0f, shieldRight: 45f, barRight: 207f, gap: 4f, minScale: 0.50f);
        Assert.Equal(154f / 207f, exact.Scale, 0.001f);
        Assert.Equal(49f, exact.Shift, 0.01f);
        Assert.Equal(207f - 4f, 49f + 207f * exact.Scale, 0.01f);

        // The Kin's 176-wide bar needs 0.62, which is above the 0.50 floor: it fits, the ink still ends 4 before the bar.
        PortraitCombatMath.HpTextBlockFit kin = PortraitCombatMath.FitHpTextBesideBlock(
            inkLeft: (176f - 207f) * 0.5f, inkWidth: 207f, pivotX: 0f, shieldRight: 45f, barRight: 176f, gap: 4f,
            minScale: 0.50f);
        Assert.Equal(123f / 207f, kin.Scale, 0.001f);
        float kinInkLeft = (176f - 207f) * 0.5f * kin.Scale + kin.Shift;
        Assert.Equal(49f, kinInkLeft, 0.01f);
        Assert.Equal(176f - 4f, kinInkLeft + 207f * kin.Scale, 0.01f);

        // Only a bar narrower than the 0.50 floor still overflows, and that is what the caller reports.
        PortraitCombatMath.HpTextBlockFit floored = PortraitCombatMath.FitHpTextBesideBlock(
            inkLeft: (100f - 207f) * 0.5f, inkWidth: 207f, pivotX: 0f, shieldRight: 45f, barRight: 100f, gap: 4f,
            minScale: 0.50f);
        Assert.Equal(0.50f, floored.Scale, 0.001f);
        Assert.True((100f - 207f) * 0.5f * floored.Scale + floored.Shift + 207f * floored.Scale > 100f - 4f);
    }

    [Fact]
    public void BlockedHpTextShrinksExactlyToFitTheThinGardenerBar()
    {
        // The Underdocks' Phantasmal Gardener: a 172-wide bar whose 160-wide ink must sit beside a 51-wide shield band.
        // The scale lands just above the floor, the digits start 4 after the shield and end 4 before the bar's end.
        PortraitCombatMath.HpTextBlockFit fit = PortraitCombatMath.FitHpTextBesideBlock(
            inkLeft: (172f - 160f) * 0.5f, inkWidth: 160f, pivotX: 0f, shieldRight: 51f, barRight: 172f, gap: 4f,
            minScale: 0.70f);
        Assert.InRange(fit.Scale, 0.70f, 0.71f);
        float inkLeft = (172f - 160f) * 0.5f * fit.Scale + fit.Shift;
        Assert.Equal(55f, inkLeft, 0.01f);
        Assert.True(inkLeft + 160f * fit.Scale <= 172f - 4f + 0.01f);

        // A wide bar whose centred text already clears the shield is left exactly as the game draws it.
        PortraitCombatMath.HpTextBlockFit wide = PortraitCombatMath.FitHpTextBesideBlock(
            inkLeft: 134f, inkWidth: 160f, pivotX: 0f, shieldRight: 40f, barRight: 400f, gap: 4f, minScale: 0.70f);
        Assert.Equal(new PortraitCombatMath.HpTextBlockFit(0f, 1f), wide);
    }

    [Fact]
    public void ACardWithinTwoUnitsOfItsSlotSettlesExactlyOntoIt()
    {
        // The native loop stops within a unit, so a card a fraction short is pinned: position, angle and scale all rest.
        PortraitCombatMath.PortraitCardMotion motion = PortraitCombatMath.CapCardArrival(
            currentX: 200f, currentY: 100f, previousX: 199f, previousY: 99f,
            currentScale: 0.9f, currentRotationDegrees: 3f,
            slotX: 200f, slotY: 100.5f, slotScale: 0.87f, slotAngleDegrees: 2f,
            previousRotationDegrees: 3f, interactive: false, turnDrawBatch: false, frameSeconds: 1f / 60f);
        Assert.Equal(200f, motion.X);
        Assert.Equal(100.5f, motion.Y);
        Assert.Equal(2f, motion.RotationDegrees);
        Assert.True(motion.ScaleCapped);
        Assert.True(motion.Settled);
        Assert.Equal(0.87f, motion.Scale);

        // A card the game is still shrinking as it lands is lifted to the resting scale too (no cap flag: it is the pin).
        PortraitCombatMath.PortraitCardMotion shrinking = PortraitCombatMath.CapCardArrival(
            currentX: 200f, currentY: 100f, previousX: 199f, previousY: 99f,
            currentScale: 0.70f, currentRotationDegrees: 3f,
            slotX: 200f, slotY: 100.5f, slotScale: 0.87f, slotAngleDegrees: 2f,
            previousRotationDegrees: 3f, interactive: false, turnDrawBatch: false, frameSeconds: 1f / 60f);
        Assert.False(shrinking.ScaleCapped);
        Assert.True(shrinking.Settled);
        Assert.Equal(0.87f, shrinking.Scale);
    }

    [Fact]
    public void OnAnOrdinaryPassNoFrameDrawsMoreThanOneNativeStep()
    {
        // clamp(frameSeconds * 7) of the distance left at the last pass, plus a unit, in whatever direction the game took.
        const float frameSeconds = 1f / 60f;
        float allowed = 1000f * Math.Clamp(frameSeconds * 7f, 0f, 1f) + 1f;
        PortraitCombatMath.PortraitCardMotion motion = PortraitCombatMath.CapCardArrival(
            currentX: 500f, currentY: 0f, previousX: 0f, previousY: 0f,
            currentScale: 0.8f, currentRotationDegrees: 4f,
            slotX: 1000f, slotY: 0f, slotScale: 0.8f, slotAngleDegrees: 2.4f,
            previousRotationDegrees: 4f, interactive: true, turnDrawBatch: true, frameSeconds: frameSeconds);
        Assert.Equal(allowed, motion.X, 0.01f);
        Assert.Equal(0f, motion.Y);
        // The always-on rule never turns a card: the game's angle is left exactly as it wrote it.
        Assert.Equal(4f, motion.RotationDegrees);
        Assert.False(motion.ScaleCapped);
    }

    [Fact]
    public void ATurnDrawOnlyMovesACardAlongTheLineToItsSlot()
    {
        // A native step in another direction is projected onto slot − previous and clamped to the same one-step share.
        PortraitCombatMath.PortraitCardMotion sideways = PortraitCombatMath.CapCardArrival(
            currentX: 0f, currentY: 50f, previousX: 0f, previousY: 0f,
            currentScale: 0.8f, currentRotationDegrees: 0f,
            slotX: 100f, slotY: 0f, slotScale: 0.8f, slotAngleDegrees: 0f,
            previousRotationDegrees: 0f, interactive: false, turnDrawBatch: true, frameSeconds: 0.1f);
        Assert.Equal(0f, sideways.X, 0.01f);
        Assert.Equal(0f, sideways.Y, 0.01f);

        PortraitCombatMath.PortraitCardMotion diagonal = PortraitCombatMath.CapCardArrival(
            currentX: 100f, currentY: 100f, previousX: 0f, previousY: 0f,
            currentScale: 0.8f, currentRotationDegrees: 0f,
            slotX: 100f, slotY: 0f, slotScale: 0.8f, slotAngleDegrees: 0f,
            previousRotationDegrees: 0f, interactive: false, turnDrawBatch: true, frameSeconds: 0.1f);
        // The projection is 100 of the 100-unit line, clamped to 100 * 0.7 + 1 = 71.
        Assert.Equal(71f, diagonal.X, 0.01f);
        Assert.Equal(0f, diagonal.Y, 0.01f);
    }

    [Fact]
    public void ATurnDrawTurnsACardOnlyTowardItsSlot()
    {
        // weight = 0.1 s × 7 = 0.7: at most 0.7 of the 5-degree gap plus a tenth of a degree may be taken this frame, and
        // never a turn away from the slot.
        PortraitCombatMath.PortraitCardMotion toward = PortraitCombatMath.CapCardArrival(
            currentX: 90f, currentY: 0f, previousX: 0f, previousY: 0f,
            currentScale: 0.8f, currentRotationDegrees: 20f,
            slotX: 100f, slotY: 0f, slotScale: 0.8f, slotAngleDegrees: 5f,
            previousRotationDegrees: 0f, interactive: false, turnDrawBatch: true, frameSeconds: 0.1f);
        Assert.Equal(3.6f, toward.RotationDegrees, 0.001f);

        PortraitCombatMath.PortraitCardMotion away = PortraitCombatMath.CapCardArrival(
            currentX: 90f, currentY: 0f, previousX: 0f, previousY: 0f,
            currentScale: 0.8f, currentRotationDegrees: -20f,
            slotX: 100f, slotY: 0f, slotScale: 0.8f, slotAngleDegrees: 5f,
            previousRotationDegrees: 0f, interactive: false, turnDrawBatch: true, frameSeconds: 0.1f);
        Assert.Equal(0f, away.RotationDegrees);
    }

    [Fact]
    public void DrawQuietWindowFollowsTheDeliveringGap()
    {
        // A turn draw with a known size gets the long patience; any other draw one pile tween plus half the gap it sees.
        Assert.Equal(PortraitCombatMath.TurnDrawQuietMsec, PortraitCombatMath.DrawQuietWindowMsec(2000f, true));
        Assert.Equal(PortraitCombatMath.DrawFirstAddQuietMsec, PortraitCombatMath.DrawQuietWindowMsec(0f, false));
        Assert.Equal(PortraitCombatMath.DrawFirstAddQuietMsec, PortraitCombatMath.DrawQuietWindowMsec(200f, false));
        Assert.Equal(1.5f * 1000f, PortraitCombatMath.DrawQuietWindowMsec(1000f, false), 0.01f);
    }

    [Fact]
    public void StarBadgeHangsOnTheOrbsLowerLeftCorner()
    {
        (float x, float y) = PortraitCombatMath.StarBadgeCenter(500f, 300f, 200f, 200f);
        Assert.Equal(500f + 200f * PortraitCombatMath.StarOrbOffsetX, x, 0.001f);
        Assert.Equal(300f + 200f * PortraitCombatMath.StarOrbOffsetY, y, 0.001f);
        Assert.True(x < 500f && y > 300f);

        // The rail layout and the per-frame placement use the same formula.
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(2340f, stageTopY: 612f);
        PortraitHudLayout hud = PortraitCombatMath.ComputeHudLayout(2340f, bands);
        float orbSize = PortraitCombatMath.EnergyOrbSize * hud.EnergyScale;
        (float hudX, float hudY) = PortraitCombatMath.StarBadgeCenter(
            hud.EnergyContainerX + PortraitCombatMath.EnergyOrbSize * 0.5f, hud.EnergyContainerY + 64f, orbSize, orbSize);
        Assert.Equal(hud.StarCenterX, hudX, 0.001f);
        Assert.Equal(hud.StarCenterY, hudY, 0.001f);
    }

    [Fact]
    public void PierFramingPutsItsPlankLineOnTheFrontFootLine()
    {
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(2340f, stageTopY: 612f);
        const float artWidth = 1296f, artTop = 0f, artBottom = 1296f;
        float plank = artBottom - (artBottom - artTop) * (861f / 1296f);
        PortraitCombatMath.PortraitPierFraming framing = PortraitCombatMath.ComputePierFraming(
            artWidth, artTop, artBottom, plank, 1080f, bands);

        // The plank line lands exactly on the front foot line, and the art covers the canvas width.
        Assert.Equal(bands.FrontFeetY, framing.OriginY + plank * framing.Scale, 0.01f);
        Assert.True(artWidth * framing.Scale >= 1080f - 0.01f);
        // The art also reaches the stage top, so no seam shows inside the stage.
        Assert.True(framing.OriginY + artTop * framing.Scale <= bands.StageTopY + 0.01f);
        Assert.Equal(framing.OriginY + PortraitCombatMath.PierFarEdgeArtY * framing.Scale, framing.FarEdgeY, 0.001f);

        // A wider art needs less scale to cover the canvas, and the foot line still holds.
        PortraitCombatMath.PortraitPierFraming wide = PortraitCombatMath.ComputePierFraming(
            artWidth * 2f, artTop, artBottom, plank, 1080f, bands);
        Assert.Equal(bands.FrontFeetY, wide.OriginY + plank * wide.Scale, 0.01f);
        Assert.True(wide.Scale > 0f);
    }
}
