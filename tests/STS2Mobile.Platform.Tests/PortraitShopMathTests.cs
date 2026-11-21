using System;
using System.Linq;
using STS2Mobile.Platform;
using Xunit;

namespace STS2Mobile.Platform.Tests;

public class PortraitShopMathTests
{
    // The Back button's row is 110 units tall and lives in the bottom band above the home indicator.
    private const float BackHeight = 110f;

    private static ShopLayoutRect StockRectFor(float canvasHeight)
    {
        ShopLayoutRect cloth = PortraitShopMath.ClothRect(1080f, 620f, canvasHeight);
        float tabTop = PortraitShopMath.TabRowTop(canvasHeight, BackHeight);
        return PortraitShopMath.StockRect(cloth, tabTop - 16f);
    }

    private static (float Width, float Height) Stock(float canvasHeight = 2340f)
    {
        ShopLayoutRect stock = StockRectFor(canvasHeight);
        return (stock.Width, stock.Height);
    }

    [Fact]
    public void ClothSitsUnderTheRelicStripAndAboveTheTabRow()
    {
        ShopLayoutRect cloth = PortraitShopMath.ClothRect(1080f, 620f, 2340f);
        Assert.Equal(16f, cloth.X);
        Assert.Equal(1048f, cloth.Width);
        Assert.Equal(620f, cloth.Y);
        Assert.Equal(2340f - 175f, cloth.Bottom, 3);
        Assert.True(cloth.Bottom > 2000f, "the rug reaches down behind the tabs");
    }

    [Theory]
    [InlineData(2340f)]
    [InlineData(1920f)]
    public void StockIsInsetInsideTheRugAndEndsAboveTheTabs(float canvasHeight)
    {
        ShopLayoutRect cloth = PortraitShopMath.ClothRect(1080f, 620f, canvasHeight);
        ShopLayoutRect stock = StockRectFor(canvasHeight);
        Assert.Equal(cloth.X + PortraitShopMath.ClothInsetSide, stock.X);
        Assert.Equal(cloth.Y + PortraitShopMath.ClothInsetTop, stock.Y);
        Assert.Equal(cloth.Width - 2f * PortraitShopMath.ClothInsetSide, stock.Width);
        Assert.True(stock.Bottom < cloth.Bottom);
        float tabTop = PortraitShopMath.TabRowTop(canvasHeight, BackHeight);
        Assert.True(stock.Bottom <= tabTop - 15f, $"stock {stock.Bottom} tab top {tabTop}");
        Assert.Equal(canvasHeight - PortraitShopMath.TabRowBottomMargin, tabTop + PortraitShopMath.TabMinHeight, 3);
        Assert.True(stock.Height > 900f, $"stock height {stock.Height}");
    }

    [Theory]
    [InlineData(2340f)]
    [InlineData(1920f)]
    public void CardRowsAndPricesStayAboveTheTabs(float canvasHeight)
    {
        (float width, float height) = Stock(canvasHeight);
        ShopCardPage page = PortraitShopMath.CardPage(5, 2, 2, width, height);
        foreach (ShopSlotPlacement slot in page.ClassCards.Concat(page.Colorless))
        {
            // The slot's price row reaches 278 units below its origin at scale 1.
            float priceBottom = slot.Y + 278f * page.Scale;
            Assert.True(priceBottom <= height - PortraitShopMath.Pad + 0.01f,
                $"price bottom {priceBottom} in stock {height}");
        }
        Assert.Equal(2, page.Colorless.Count);
        Assert.True(page.ClassCards[0].Y < page.ClassCards[3].Y);
        Assert.True(page.ClassCards[3].Y < page.Colorless[0].Y);
        // 9:16 still gives three rows and readable cards (the smallest the scale may go is the mod's floor).
        Assert.True(page.Scale >= PortraitShopMath.MinCardScale);
        Assert.True(page.Scale > PortraitShopMath.MinCardScale, "9:16 needs more than the floor scale");
    }

    [Theory]
    [InlineData(2340f)]
    [InlineData(1920f)]
    public void RemovalPriceFitsAboveTheTabs(float canvasHeight)
    {
        (_, float height) = Stock(canvasHeight);
        const float nativeTop = -180f;
        const float nativeBottom = 210f;
        ShopItemsPage page = PortraitShopMath.ItemsPage(3, 3, 968f, height);
        float potionPriceBottom = page.Potions[0].Y + PortraitShopMath.PotionScale * 160f;
        ShopSlotPlacement fitted = PortraitShopMath.FitRemoval(potionPriceBottom, height, nativeTop, nativeBottom,
            PortraitShopMath.RemovalScale);
        float bottom = fitted.Y + fitted.Scale * nativeBottom;
        Assert.True(bottom <= height + 0.01f, $"removal price {bottom} in stock {height}");
        Assert.True(page.Relics[0].Y < page.Potions[0].Y && page.Potions[0].Y < fitted.Y);
    }

    [Fact]
    public void ClosedStockLiftsTheWholeRugAboveTheCanvas()
    {
        const float inventoryY = 540f;
        const float clothHeight = 1545f;
        float closedY = PortraitShopMath.ClosedY(inventoryY, clothHeight);
        // The rug's top in canvas space is the stock's Y minus its top inset; it must end above 0.
        float rugBottom = inventoryY + closedY - PortraitShopMath.ClothInsetTop + clothHeight;
        Assert.True(rugBottom <= 0f, $"rug bottom {rugBottom}");
    }

    [Fact]
    public void FiveClassCardsPutThePowerBesideTwoPairs()
    {
        (float width, float height) = Stock();
        ShopCardPage page = PortraitShopMath.CardPage(5, 2, 2, width, height);
        Assert.Equal(5, page.ClassCards.Count);
        // The power sits between the two rows and in the third column; the four paired cards fill columns 1 and 2.
        Assert.Equal(page.ClassCards[0].Y, page.ClassCards[1].Y, 3);
        Assert.Equal(page.ClassCards[3].Y, page.ClassCards[4].Y, 3);
        Assert.Equal(page.ClassCards[0].Y + (page.ClassCards[3].Y - page.ClassCards[0].Y) / 2f, page.ClassCards[2].Y, 3);
        Assert.True(page.ClassCards[2].X > page.ClassCards[1].X);
        Assert.Equal(page.ClassCards[0].X, page.ClassCards[3].X, 3);
        Assert.Equal(page.ClassCards[1].X, page.ClassCards[4].X, 3);
        // The colorless pair is centred on its own row under both.
        Assert.Equal(2, page.Colorless.Count);
        Assert.Equal(page.ClassCards[0].Y + 2f * (page.ClassCards[3].Y - page.ClassCards[0].Y), page.Colorless[0].Y, 3);
        Assert.Equal(width, page.Colorless[0].X + page.Colorless[1].X, 3);
        Assert.Equal(page.Colorless[0].Y, page.Colorless[1].Y, 3);
    }

    [Fact]
    public void CardPageStaysInsideTheStock()
    {
        (float width, float height) = Stock();
        ShopCardPage page = PortraitShopMath.CardPage(5, 4, 2, width, height);
        float halfWidth = 150f * page.Scale;
        float halfHeight = 211f * page.Scale;
        foreach (ShopSlotPlacement slot in page.ClassCards.Concat(page.Colorless))
        {
            Assert.InRange(slot.X - halfWidth, PortraitShopMath.Pad - 0.01f, width - PortraitShopMath.Pad + 0.01f);
            Assert.InRange(slot.Y - halfHeight, PortraitShopMath.Pad - 0.01f, height - PortraitShopMath.Pad + 0.01f);
            Assert.InRange(slot.Scale, PortraitShopMath.MinCardScale, PortraitShopMath.MaxCardScale);
        }
    }

    [Fact]
    public void ThreeClassCardsUseOneRowOfThree()
    {
        (float width, float height) = Stock();
        ShopCardPage page = PortraitShopMath.CardPage(3, -1, 0, width, height);
        Assert.Equal(3, page.ClassCards.Count);
        Assert.All(page.ClassCards, slot => Assert.Equal(page.ClassCards[0].Y, slot.Y, 3));
        Assert.True(page.ClassCards[0].X < page.ClassCards[1].X && page.ClassCards[1].X < page.ClassCards[2].X);
    }

    [Fact]
    public void CardsShrinkWithTheStockAndNeverBelowTheMinimum()
    {
        float wide = PortraitShopMath.CardScale(968f, 1465f);
        float narrow = PortraitShopMath.CardScale(968f, 900f);
        float tiny = PortraitShopMath.CardScale(968f, 400f);
        Assert.True(wide > narrow && narrow > tiny);
        Assert.Equal(PortraitShopMath.MinCardScale, tiny, 3);
        Assert.InRange(wide, PortraitShopMath.MinCardScale, PortraitShopMath.MaxCardScale);
    }

    [Fact]
    public void ItemsPageRowsGoRelicsThenPotionsThenRemoval()
    {
        (float width, float height) = Stock();
        ShopItemsPage page = PortraitShopMath.ItemsPage(3, 3, width, height);
        Assert.Equal(3, page.Relics.Count);
        Assert.Equal(3, page.Potions.Count);
        Assert.All(page.Relics, slot => Assert.Equal(page.Relics[0].Y, slot.Y, 3));
        Assert.All(page.Potions, slot => Assert.Equal(page.Potions[0].Y, slot.Y, 3));
        Assert.True(page.Relics[0].Y < page.Potions[0].Y && page.Potions[0].Y < page.Removal.Y);
        Assert.Equal(width / 2f, page.Removal.X);
        Assert.Equal(PortraitShopMath.RelicScale, page.Relics[0].Scale);
        Assert.Equal(PortraitShopMath.RemovalScale, page.Removal.Scale);
        // Three relics and three potions stand in three columns, centred under each other.
        Assert.Equal(page.Relics[0].X, page.Potions[0].X, 3);
        Assert.Equal(page.Relics[2].X, page.Potions[2].X, 3);
    }

    [Fact]
    public void RemovalFitsBetweenThePotionPricesAndTheStockBottom()
    {
        (_, float height) = Stock();
        ShopItemsPage page = PortraitShopMath.ItemsPage(3, 3, 968f, height);
        const float nativeTop = -180f;
        const float nativeBottom = 210f;
        float potionPriceBottom = page.Potions[0].Y + PortraitShopMath.PotionScale * 160f;
        ShopSlotPlacement fitted = PortraitShopMath.FitRemoval(potionPriceBottom, height, nativeTop, nativeBottom,
            PortraitShopMath.RemovalScale);
        Assert.InRange(fitted.Scale, 0.1f, PortraitShopMath.RemovalScale);
        float top = fitted.Y + fitted.Scale * nativeTop;
        float bottom = fitted.Y + fitted.Scale * nativeBottom;
        Assert.True(top >= potionPriceBottom - 0.01f, $"top {top} over potions {potionPriceBottom}");
        Assert.True(bottom <= height + 0.01f, $"bottom {bottom} in {height}");
    }

    [Fact]
    public void WeirdShopPutsSixRelicsInTwoColumnsAndThreeRows()
    {
        (float width, float height) = Stock();
        var relics = PortraitShopMath.WeirdRelics(6, width, height);
        Assert.Equal(6, relics.Count);
        Assert.All(relics, slot => Assert.InRange(slot.X, 0f, width));
        for (int i = 0; i < 6; i += 2)
        {
            Assert.True(relics[i].X < relics[i + 1].X);
            Assert.Equal(relics[i].Y, relics[i + 1].Y, 3);
        }
        for (int i = 2; i < 6; i++) Assert.True(relics[i].Y > relics[i - 2].Y);
        Assert.InRange(relics[0].Scale, 0.1f, PortraitShopMath.WeirdMaxScale);
        // Two columns of the relic art stay inside the stock.
        float halfWidth = PortraitShopMath.WeirdWidth / 2f * relics[0].Scale;
        Assert.True(relics[0].X - halfWidth >= -0.5f);
        Assert.True(relics[1].X + halfWidth <= width + 0.5f);
    }

    [Fact]
    public void BlitzSlotsStandInTwoColumnsAboveTheFooter()
    {
        (float width, float height) = Stock();
        var footprints = new[]
        {
            new ShopBlitzFootprint(0f, 210f), new ShopBlitzFootprint(0f, 210f),
            new ShopBlitzFootprint(0f, 210f), new ShopBlitzFootprint(0f, 210f),
        };
        var slots = PortraitShopMath.BlitzSlots(footprints, width, height);
        Assert.Equal(4, slots.Count);
        Assert.All(slots, slot => Assert.InRange(slot.X, 0f, width));
        Assert.True(slots[0].X < slots[1].X);
        Assert.Equal(slots[0].Y, slots[1].Y, 3);
        Assert.True(slots[2].Y > slots[0].Y);
        Assert.InRange(slots[0].Scale, 0.1f, PortraitShopMath.BlitzMaxScale);
        // The lowest slot's art stays above the footer line.
        float bottom = slots[2].Y + 210f / 2f * slots[0].Scale;
        Assert.True(bottom <= height - PortraitShopMath.BlitzFooter + 0.01f, $"bottom {bottom}");
    }

    [Fact]
    public void BlitzUsesTheTallestFootprintForTheScale()
    {
        var shortOnes = Enumerable.Range(0, 6).Select(_ => new ShopBlitzFootprint(0f, 100f)).ToArray();
        var tallOnes = Enumerable.Range(0, 6).Select(_ => new ShopBlitzFootprint(0f, 500f)).ToArray();
        float shortScale = PortraitShopMath.BlitzSlots(shortOnes, 968f, 1465f)[0].Scale;
        float tallScale = PortraitShopMath.BlitzSlots(tallOnes, 968f, 1465f)[0].Scale;
        Assert.Equal(PortraitShopMath.BlitzMaxScale, shortScale, 3);
        Assert.True(tallScale < shortScale);
    }

    [Fact]
    public void TabRowStartsAfterTheBackButtonAndStaysReachable()
    {
        var back = new ShopLayoutRect(20f, 1985f, 198f, 109f);
        (ShopLayoutRect cards, ShopLayoutRect items) = PortraitShopMath.TabRow(back, 1080f);
        Assert.True(cards.X >= back.Right + PortraitShopMath.TabGap);
        Assert.Equal(cards.Y, items.Y, 3);
        Assert.Equal(cards.Width, items.Width, 3);
        Assert.Equal(cards.Right + PortraitShopMath.TabGap, items.X, 3);
        Assert.True(items.Right <= 1080f);
        foreach (ShopLayoutRect tab in new[] { cards, items })
        {
            Assert.True(tab.Height >= PortraitShopMath.TabMinHeight);
            Assert.Equal(back.CenterY, tab.CenterY, 3);
            Assert.InRange(tab.Y, 0f, 2340f);
        }
    }

    [Fact]
    public void TabRowFallsBackInsideTheCanvasWhenTheBackButtonSitsFarRight()
    {
        var back = new ShopLayoutRect(700f, 1985f, 198f, 109f);
        (ShopLayoutRect cards, ShopLayoutRect items) = PortraitShopMath.TabRow(back, 1080f);
        Assert.True(cards.X >= 0f);
        Assert.True(items.Right <= 1080f);
        Assert.True(cards.Width > 0f);
        Assert.Equal(back.CenterY, cards.CenterY, 3);
    }

    [Theory]
    [InlineData(20f, -40f, 200f, -200f)]
    [InlineData(20f, -300f, 200f, -280f)]
    public void HiddenShopButtonIsFullyOffCanvas(float showX, float hideOffsetX, float width, float expected)
    {
        Assert.Equal(expected, PortraitShopMath.HideX(showX, hideOffsetX, width), 3);
    }

    [Fact]
    public void TabRowNeverOverflowsTheCanvas()
    {
        var back = new ShopLayoutRect(20f, 1985f, 198f, 109f);
        foreach (float width in new[] { 900f, 1080f, 1242f })
        {
            (ShopLayoutRect cards, ShopLayoutRect items) = PortraitShopMath.TabRow(back, width);
            Assert.True(items.Right <= width + 0.01f, $"right {items.Right} canvas {width}");
        }
    }
}
