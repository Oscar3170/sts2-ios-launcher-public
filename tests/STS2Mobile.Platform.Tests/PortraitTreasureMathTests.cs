namespace STS2Mobile.Platform.Tests;

public class PortraitTreasureMathTests
{
    // The phone's two shapes, both with the stone header's bottom at 390 (a 162-unit cap plus the 228-unit header) and the
    // relic strip ending at 612 (PortraitBands.RelicBandBottom).
    private const float CanvasWidth = 1080f;
    private const float TallHeight = 2340f;
    private const float CompactHeight = 1920f;
    private const float SceneTop = 390f;
    private const float BannerTop = 612f;
    private const float BannerWidth = 654f;   // the game's banner control
    private const float BannerHeight = 162f;
    private const float EmptyWidth = 500f;    // the empty-chest label
    private const float FightWidth = 320f;    // the fought-over label

    private static TreasureScene Plan(float height) =>
        PortraitTreasureMath.Plan(CanvasWidth, height, SceneTop, BannerTop, BannerWidth, EmptyWidth, FightWidth);

    [Theory]
    [InlineData(TallHeight, 1482f)]
    [InlineData(CompactHeight, 1246.8f)]
    public void MouthSitsFiftySixPercentOfTheWayDownFromTheHeader(float height, float expected)
    {
        Assert.Equal(expected, PortraitTreasureMath.MouthY(height, SceneTop), 2);
        Assert.Equal(expected, Plan(height).MouthY, 2);
    }

    [Fact]
    public void ChestIsFramedAroundTheMiddleWithItsSpriteCoveringTheWidth()
    {
        TreasureScene tall = Plan(TallHeight);
        Assert.Equal(1.0046512f, tall.ChestScale, 4);
        Assert.Equal(118.0465f, tall.ChestX, 3);
        Assert.Equal(1336.3256f, tall.ChestY, 3);
        // The control is 800 x 500 units, so at this scale it is 74% of the canvas wide: bigger than the acceptance floor.
        float chestWidth = 800f * tall.ChestScale;
        Assert.True(chestWidth >= 0.7f * CanvasWidth);
        Assert.True(tall.ChestX >= 0f && tall.ChestX + chestWidth <= CanvasWidth + 1f);
    }

    [Theory]
    [InlineData(TallHeight)]
    [InlineData(CompactHeight)]
    public void GoldAndFloorFollowTheChestAndStayInsideItsControl(float height)
    {
        TreasureScene scene = Plan(height);
        Assert.Equal(454.6046f, scene.GoldX, 3);
        Assert.Equal(scene.MouthY, scene.GoldY, 3);
        Assert.Equal(514.8837f, scene.FloorX, 3);
        Assert.Equal(scene.ChestY + 477.2093f, scene.FloorY, 2);

        float right = scene.ChestX + 800f * scene.ChestScale;
        float bottom = scene.ChestY + 500f * scene.ChestScale;
        foreach ((float x, float y) in new[] { (scene.GoldX, scene.GoldY), (scene.FloorX, scene.FloorY) })
        {
            Assert.InRange(x, scene.ChestX, right);
            Assert.InRange(y, scene.ChestY, bottom);
        }
    }

    [Theory]
    [InlineData(TallHeight)]
    [InlineData(CompactHeight)]
    public void BannerKeepsTheReferenceShareOfTheScreenUnderTheRelicStrip(float height)
    {
        TreasureScene scene = Plan(height);
        Assert.Equal(PortraitTreasureMath.BannerMaxScale, scene.BannerScale, 4);
        Assert.Equal(81.7329f, scene.BannerX, 3);
        Assert.Equal(BannerTop, scene.BannerY);
        Assert.Equal(0.8486429f, BannerWidth * scene.BannerScale / CanvasWidth, 4);
    }

    [Fact]
    public void BannerScaleShrinksForAWiderBannerInsteadOfOvergrowingIt()
    {
        Assert.Equal(1f, PortraitTreasureMath.BannerScale(CanvasWidth, 0f), 4);
        // A banner already as wide as the reference's share stays put; a much narrower one grows to the cap.
        Assert.Equal(0.9165f, PortraitTreasureMath.BannerScale(1080f, 1000f), 4);
        Assert.Equal(PortraitTreasureMath.BannerMaxScale, PortraitTreasureMath.BannerScale(1080f, 300f), 4);
        Assert.Equal(1.4014f, PortraitTreasureMath.BannerMaxScale, 4);
    }

    [Theory]
    [InlineData(TallHeight)]
    [InlineData(CompactHeight)]
    public void HolderGridGrowsThreeColumnsIntoTheCanvas(float height)
    {
        TreasureScene scene = Plan(height);
        float factor = PortraitTreasureMath.HolderFactor(CanvasWidth);
        Assert.Equal(1.3178571f, factor, 4);
        float step = PortraitTreasureMath.HolderStep(factor);
        Assert.Equal(300.4714f, step, 3);
        // The drawn hitbox: 269 units, 93 points, against the 44-point minimum.
        float box = PortraitTreasureMath.HolderBox * factor;
        Assert.Equal(268.8429f, box, 3);
        Assert.True(box >= 127f);

        float bannerBottom = scene.BannerY + BannerHeight * scene.BannerScale;
        Assert.Equal(839.0314f, bannerBottom, 3);
        for (int count = 1; count <= 6; count++)
        {
            float firstRow = PortraitTreasureMath.FirstHolderRow(scene.MouthY, count, step, factor, bannerBottom);
            IReadOnlyList<TreasureHolderSlot> slots = PortraitTreasureMath.HolderSlots(count, CanvasWidth / 2f, firstRow, step);
            Assert.Equal(count, slots.Count);
            for (int i = 0; i < slots.Count; i++)
            {
                float left = slots[i].CenterX - box / 2f;
                float top = slots[i].CenterY - box / 2f;
                Assert.True(left >= 0f && left + box <= CanvasWidth, $"holder {i} of {count} across");
                Assert.True(top >= 0f && top + box <= height, $"holder {i} of {count} down");
                Assert.True(top >= bannerBottom, $"holder {i} of {count} clears the banner");
                for (int j = 0; j < i; j++)
                {
                    float otherLeft = slots[j].CenterX - box / 2f;
                    float otherTop = slots[j].CenterY - box / 2f;
                    bool apart = left + box <= otherLeft || otherLeft + box <= left
                        || top + box <= otherTop || otherTop + box <= top;
                    Assert.True(apart, $"holders {j} and {i} of {count} overlap");
                }
            }
        }
    }

    [Theory]
    [InlineData(TallHeight, 1240.9286f)]
    [InlineData(CompactHeight, 1005.7286f)]
    public void OneHolderRowSitsAboveTheMouth(float height, float expected)
    {
        TreasureScene scene = Plan(height);
        float factor = PortraitTreasureMath.HolderFactor(CanvasWidth);
        float step = PortraitTreasureMath.HolderStep(factor);
        float bannerBottom = scene.BannerY + BannerHeight * scene.BannerScale;
        Assert.Equal(expected, PortraitTreasureMath.FirstHolderRow(scene.MouthY, 3, step, factor, bannerBottom), 3);
        Assert.Equal(540f, PortraitTreasureMath.HolderSlots(1, 540f, expected, step)[0].CenterX, 3);
        TreasureHolderSlot[] pair = PortraitTreasureMath.HolderSlots(2, 540f, expected, step).ToArray();
        Assert.Equal(389.7643f, pair[0].CenterX, 3);
        Assert.Equal(690.2357f, pair[1].CenterX, 3);
    }

    [Theory]
    [InlineData(TallHeight)]
    [InlineData(CompactHeight)]
    public void TwoHolderRowsAreHeldUnderTheBannerInBothShapes(float height)
    {
        TreasureScene scene = Plan(height);
        float factor = PortraitTreasureMath.HolderFactor(CanvasWidth);
        float step = PortraitTreasureMath.HolderStep(factor);
        float bannerBottom = scene.BannerY + BannerHeight * scene.BannerScale;
        // At 1080 the phone's header and relic strip leave less room than the reference's, so the banner clamp is what places
        // a two-row grid even on the tall shape.
        Assert.Equal(996.5957f, PortraitTreasureMath.FirstHolderRow(scene.MouthY, 4, step, factor, bannerBottom), 3);
        Assert.Equal(996.5957f, PortraitTreasureMath.FirstHolderRow(scene.MouthY, 6, step, factor, bannerBottom), 3);
        TreasureHolderSlot[] six = PortraitTreasureMath.HolderSlots(6, 540f, 996.5957f, step).ToArray();
        Assert.Equal(239.5286f, six[0].CenterX, 3);
        Assert.Equal(840.4714f, six[2].CenterX, 3);
        Assert.Equal(996.5957f + step, six[3].CenterY, 3);
    }

    [Theory]
    [InlineData(TallHeight)]
    [InlineData(CompactHeight)]
    public void LabelsAreCentredScaledAndOnCanvas(float height)
    {
        TreasureScene scene = Plan(height);
        Assert.Equal(scene.MouthY - PortraitTreasureMath.EmptyDrop, scene.EmptyY, 3);
        Assert.Equal(1.1571429f, scene.EmptyScale, 4);
        Assert.Equal(540f, scene.EmptyX + EmptyWidth * scene.EmptyScale / 2f, 3);
        Assert.Equal(SceneTop + PortraitTreasureMath.FightDrop, scene.FightY, 3);
        Assert.Equal(1.0928571f, scene.FightScale, 4);
        Assert.Equal(540f, scene.FightX + FightWidth * scene.FightScale / 2f, 3);
        Assert.InRange(scene.EmptyY, 0f, height);
        Assert.InRange(scene.FightY, 0f, height);
    }
}
