using System;
using System.Collections.Generic;

namespace STS2Mobile.Platform;

/// <summary>One shop slot: where its origin stands in the stock viewport's units, and the scale to draw it at.</summary>
public readonly record struct ShopSlotPlacement(float X, float Y, float Scale);

public readonly record struct ShopLayoutRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public float CenterX => X + Width / 2f;
    public float CenterY => Y + Height / 2f;
}

/// <summary>The Cards page: the class cards (a Power beside two pairs), the colorless row, and the scale they use.</summary>
public readonly record struct ShopCardPage(
    float Scale,
    IReadOnlyList<ShopSlotPlacement> ClassCards,
    IReadOnlyList<ShopSlotPlacement> Colorless);

/// <summary>The Items page: the relic row, the potion row, the card removal and their scales.</summary>
public readonly record struct ShopItemsPage(
    IReadOnlyList<ShopSlotPlacement> Relics,
    IReadOnlyList<ShopSlotPlacement> Potions,
    ShopSlotPlacement Removal);

/// <summary>A Blitz slot's own footprint in its local units: where its middle is and how tall it is.</summary>
public readonly record struct ShopBlitzFootprint(float CenterY, float Height);

/// <summary>
/// Pure layout math for the portrait shop: the cloth and the stock viewport it frames, the Cards page (three columns, a
/// five-card Power staggered beside two pairs, and the colorless row), the Items page (relics, potions, card removal), the
/// weird shop's 2-column relic grid and the Blitz shop's 2-column slots over a footer, plus the Cards/Items tab row.
/// <para>
/// Lengths are in this app's canvas units (1080 across; 2.88 units are one point on an iPhone 11 Pro), sized in phone
/// points; the game's own slot sizes (a card is 300 x 422 units) are canvas units already. A returned scale is the slot's
/// drawing scale in this canvas (1 draws the game's own art size).
/// </para>
/// </summary>
public static class PortraitShopMath
{
    // ---- The cloth and the stock viewport inside it ---------------------------------------------------------------
    /// <summary>The rug's side margin from the canvas edge.</summary>
    public const float ClothSideMargin = 16f;

    /// <summary>The rug ends this far above the canvas bottom, behind the tab row.</summary>
    public const float ClothBottomMargin = 175f;

    /// <summary>The stock's inset inside the rug on each side.</summary>
    public const float ClothInsetSide = 42f;

    /// <summary>The stock's inset inside the rug at the top (the rug's torn edge shows above the stock).</summary>
    public const float ClothInsetTop = 40f;

    /// <summary>The stock's inset inside the rug at the bottom.</summary>
    public const float ClothInsetBottom = 40f;

    /// <summary>The closed stock is lifted this far past the rug's bottom, so no part of the rug remains on screen.</summary>
    public const float ClosedLift = 32f;

    /// <summary>Space around the stock's content (4 points).</summary>
    public const float Pad = 11.57f;

    // ---- The Cards page -------------------------------------------------------------------------------------------
    /// <summary>A card slot's share of a column at scale 1.</summary>
    public const float CardSlotWidth = 319f;

    /// <summary>The card block's height at scale 1: two class rows, the colorless row and their prices.</summary>
    public const float CardBlockHeight = 1539f;

    /// <summary>The first row's origin below the stock's top, at scale 1.</summary>
    public const float CardRowOrigin = 227f;

    /// <summary>Row pitch at scale 1 (a card's height plus the gap and price).</summary>
    public const float CardRowPitch = 513f;

    /// <summary>The class-card columns' side margin (6.25 points).</summary>
    public const float CardColumnGap = 18f;

    /// <summary>The card block's bottom margin (6.7 points).</summary>
    public const float CardRowInset = 19.29f;

    /// <summary>The colorless pair is spread over this part of the content width.</summary>
    public const float CardColorlessSpread = 2.65f;

    /// <summary>The smallest and largest card scale: a card 0.82 to 1.46 times its standard drawing.</summary>
    public const float MinCardScale = 0.527f;
    public const float MaxCardScale = 0.939f;

    // ---- The Items page -------------------------------------------------------------------------------------------
    /// <summary>Relics and potions are drawn 1.51 times their own art (a relic about 43 points wide).</summary>
    public const float RelicScale = 1.511f;
    public const float PotionScale = 1.511f;
    /// <summary>The card removal is drawn 1.41 times its own art (about 49 points).</summary>
    public const float RemovalScale = 1.414f;
    /// <summary>The Items page leaves this much of the stock under its last row (10.7 points).</summary>
    public const float ItemsBottomInset = 30.86f;
    private const float RelicRow = 0.16f;
    private const float PotionRow = 0.42f;
    private const float RemovalRow = 0.72f;

    // ---- The weird shop's relic grid -------------------------------------------------------------------------------
    /// <summary>The weird shop's first row starts this far down the stock (7.1 points).</summary>
    private const float WeirdTop = 20.57f;
    /// <summary>The relic grid leaves this much of the stock under its last row (10.3 points).</summary>
    private const float WeirdBottomInset = 29.57f;
    private const float WeirdCellHeight = 325f;
    /// <summary>The relic grid's side margin (11.6 points).</summary>
    private const float WeirdSideInset = 33.43f;
    public const float WeirdWidth = 332f;
    /// <summary>The grid never draws a relic larger than 1.29 times its art.</summary>
    public const float WeirdMaxScale = 1.286f;

    // ---- The Blitz shop ---------------------------------------------------------------------------------------------
    /// <summary>The line under the slots that says how many picks remain (18.3 points).</summary>
    public const float BlitzFooter = 52.71f;

    /// <summary>Room left inside a Blitz cell around its footprint (3.6 points).</summary>
    private const float BlitzCellPad = 10.29f;
    /// <summary>Blitz slots never grow past 1.16 times their art.</summary>
    public const float BlitzMaxScale = 1.157f;
    private const float BlitzWorstCase = 1.03f;
    public const int BlitzFooterFontSize = 31;

    // ---- The Cards/Items tab row ------------------------------------------------------------------------------------
    /// <summary>The tabs are 44.6 points tall.</summary>
    public const float TabHeight = 128.57f;
    public const float TabMinHeight = 127f;
    public const float TabGap = 24f;
    /// <summary>The first tab starts 98.7 points in, after the native Back button.</summary>
    public const float TabStartX = 284.14f;
    /// <summary>The tab row ends 13.4 points from the right edge.</summary>
    public const float TabSideMargin = 38.57f;
    /// <summary>The tab row's bottom sits this far above the canvas bottom (52 points: clear of the home indicator).</summary>
    public const float TabRowBottomMargin = 150f;
    public const int TabFontSize = 41;
    private const float TabMinWidth = 120f;

    /// <summary>The rug on the canvas: under the relic strip and above the tab row.</summary>
    public static ShopLayoutRect ClothRect(float canvasWidth, float visualTop, float canvasHeight)
    {
        float height = Math.Max(0f, canvasHeight - ClothBottomMargin - visualTop);
        return new ShopLayoutRect(ClothSideMargin, visualTop, Math.Max(0f, canvasWidth - 2f * ClothSideMargin), height);
    }

    /// <summary>
    /// The stock's viewport inside the rug, ending above the tab row: <paramref name="contentBottom"/> is the canvas Y the
    /// stock may not pass, so every card, removal and price stays above the tabs. The rug itself may run on behind them.
    /// </summary>
    public static ShopLayoutRect StockRect(ShopLayoutRect cloth, float contentBottom)
    {
        float top = cloth.Y + ClothInsetTop;
        float bottom = Math.Min(cloth.Bottom - ClothInsetBottom, contentBottom);
        return new ShopLayoutRect(
            cloth.X + ClothInsetSide,
            top,
            Math.Max(0f, cloth.Width - 2f * ClothInsetSide),
            Math.Max(0f, bottom - top));
    }

    /// <summary>The top of the Cards/Items row in the bottom band, its height set by the Back button it stands beside.</summary>
    public static float TabRowTop(float canvasHeight, float backHeight) =>
        canvasHeight - TabRowBottomMargin - Math.Max(TabMinHeight, backHeight);

    /// <summary>
    /// The stock's local Y when the shop is closed: high enough that the rug (which reaches
    /// <see cref="ClothInsetTop"/> above the stock) ends above the canvas.
    /// </summary>
    public static float ClosedY(float inventoryY, float clothHeight) =>
        -inventoryY - clothHeight + ClothInsetTop - ClosedLift;

    /// <summary>The card scale that fits three columns and the three rows of the Cards page in the stock.</summary>
    public static float CardScale(float stockWidth, float stockHeight)
    {
        float innerWidth = Math.Max(0f, stockWidth - 2f * Pad);
        float byWidth = (innerWidth / 3f - CardColumnGap) / CardSlotWidth;
        float byHeight = (stockHeight - 2f * Pad - CardRowInset) / CardBlockHeight;
        return Math.Clamp(Math.Min(byWidth, byHeight), MinCardScale, MaxCardScale);
    }

    /// <summary>
    /// The Cards page: class cards in three columns, with a five-card shop's Power staggered beside two pairs of two and
    /// the colorless cards centred on the row below.
    /// </summary>
    public static ShopCardPage CardPage(int classCount, int powerIndex, int colorlessCount, float stockWidth, float stockHeight)
    {
        float scale = CardScale(stockWidth, stockHeight);
        float innerWidth = Math.Max(0f, stockWidth - 2f * Pad);
        float firstRow = Pad + CardRowOrigin * scale;
        float pitch = CardRowPitch * scale;
        float Column(int index, int columns) => Pad + innerWidth * (index + 0.5f) / columns;

        var cards = new ShopSlotPlacement[Math.Max(0, classCount)];
        int rows;
        if (classCount == 5 && powerIndex >= 0 && powerIndex < 5)
        {
            int paired = 0;
            for (int i = 0; i < classCount; i++)
            {
                if (i == powerIndex) continue;
                cards[i] = new ShopSlotPlacement(Column(paired % 2, 3), firstRow + paired / 2 * pitch, scale);
                paired++;
            }
            cards[powerIndex] = new ShopSlotPlacement(Column(2, 3), firstRow + pitch * 0.5f, scale);
            rows = 2;
        }
        else
        {
            for (int i = 0; i < classCount; i++)
                cards[i] = new ShopSlotPlacement(Column(i % 3, 3), firstRow + i / 3 * pitch, scale);
            rows = classCount <= 0 ? 0 : (classCount + 2) / 3;
        }

        var colorless = new ShopSlotPlacement[Math.Max(0, colorlessCount)];
        float centre = stockWidth / 2f;
        for (int i = 0; i < colorlessCount; i++)
        {
            int count = Math.Min(2, colorlessCount - i / 2 * 2);
            float x = centre + (i % 2 - (count - 1) * 0.5f) * innerWidth / CardColorlessSpread;
            colorless[i] = new ShopSlotPlacement(x, firstRow + (rows + i / 2) * pitch, scale);
        }
        return new ShopCardPage(scale, cards, colorless);
    }

    /// <summary>The Items page: relics, potions and the card removal spread down the stock.</summary>
    public static ShopItemsPage ItemsPage(int relicCount, int potionCount, float stockWidth, float stockHeight)
    {
        float innerWidth = Math.Max(0f, stockWidth - 2f * Pad);
        float rowArea = Math.Max(0f, stockHeight - ItemsBottomInset);
        float Column(int index, int columns) => Pad + innerWidth * (index + 0.5f) / Math.Max(1, columns);

        var relics = new ShopSlotPlacement[Math.Max(0, relicCount)];
        for (int i = 0; i < relicCount; i++)
            relics[i] = new ShopSlotPlacement(Column(i, relicCount), rowArea * RelicRow, RelicScale);
        var potions = new ShopSlotPlacement[Math.Max(0, potionCount)];
        for (int i = 0; i < potionCount; i++)
            potions[i] = new ShopSlotPlacement(Column(i, potionCount), rowArea * PotionRow, PotionScale);
        var removal = new ShopSlotPlacement(stockWidth / 2f, rowArea * RemovalRow, RemovalScale);
        return new ShopItemsPage(relics, potions, removal);
    }

    /// <summary>
    /// The card removal fitted under the potions: its whole sprite and price (from <paramref name="nativeTop"/> to
    /// <paramref name="nativeBottom"/> in its own units) must fit between the potion prices and the stock's bottom, so the
    /// coin is centred on that band at the largest scale that fits.
    /// </summary>
    public static ShopSlotPlacement FitRemoval(
        float previousBottom, float stockHeight, float nativeTop, float nativeBottom, float maxScale)
    {
        float topOfRow = previousBottom + Pad;
        float bottomOfRow = Math.Max(topOfRow, stockHeight - Pad);
        float span = nativeBottom - nativeTop;
        float scale = maxScale;
        if (span > 0.01f) scale = Math.Min(maxScale, (bottomOfRow - topOfRow) / span);
        if (scale < 0.1f) scale = maxScale;
        float y = (topOfRow + bottomOfRow - scale * (nativeTop + nativeBottom)) * 0.5f;
        return new ShopSlotPlacement(0f, y, scale);
    }

    /// <summary>The weird shop's six relics: two columns and three rows, all inside the stock without scrolling.</summary>
    public static IReadOnlyList<ShopSlotPlacement> WeirdRelics(int count, float stockWidth, float stockHeight)
    {
        var slots = new ShopSlotPlacement[Math.Max(0, count)];
        if (count <= 0) return slots;
        int rows = Math.Max(1, (count + 1) / 2);
        float innerWidth = Math.Max(0f, stockWidth - 2f * Pad);
        float cellHeight = (stockHeight - WeirdBottomInset) / rows;
        float scale = Math.Min(WeirdMaxScale,
            Math.Min(cellHeight / WeirdCellHeight, (innerWidth / 2f - WeirdSideInset) / WeirdWidth));
        scale = Math.Max(scale, 0.1f);
        for (int i = 0; i < count; i++)
        {
            float x = Pad + innerWidth * (i % 2 + 0.5f) / 2f;
            float y = WeirdTop + (i / 2 + 0.5f) * cellHeight;
            slots[i] = new ShopSlotPlacement(x, y, scale);
        }
        return slots;
    }

    /// <summary>
    /// The Blitz shop: two columns of slots whose own footprints (icon and, when shown, its cost) fit the cell above the
    /// footer that carries the remaining-picks text. Each slot is centred on its cell.
    /// </summary>
    public static IReadOnlyList<ShopSlotPlacement> BlitzSlots(
        IReadOnlyList<ShopBlitzFootprint> footprints, float stockWidth, float stockHeight)
    {
        var slots = new ShopSlotPlacement[Math.Max(0, footprints.Count)];
        if (footprints.Count == 0) return slots;
        int rows = (footprints.Count + 1) / 2;
        float innerWidth = Math.Max(0f, stockWidth - 2f * Pad);
        float cellHeight = (stockHeight - BlitzFooter - 2f * Pad) / rows;
        float tallest = 0f;
        foreach (ShopBlitzFootprint footprint in footprints) tallest = Math.Max(tallest, footprint.Height);
        float scale = (cellHeight - BlitzCellPad) / Math.Max(1f, tallest * BlitzWorstCase);
        scale = Math.Clamp(scale, 0.1f, BlitzMaxScale);
        for (int i = 0; i < footprints.Count; i++)
        {
            float x = Pad + innerWidth * (i % 2 + 0.5f) / 2f;
            float y = Pad + (i / 2 + 0.5f) * cellHeight - footprints[i].CenterY * scale;
            slots[i] = new ShopSlotPlacement(x, y, scale);
        }
        return slots;
    }

    /// <summary>
    /// The two tabs beside the native Back button: one row, the same height and vertical middle as the button, starting
    /// after it and filling the width to the side margin.
    /// </summary>
    public static (ShopLayoutRect Cards, ShopLayoutRect Items) TabRow(ShopLayoutRect back, float canvasWidth)
    {
        float height = Math.Max(TabMinHeight, back.Height);
        float top = back.CenterY - height / 2f;
        float right = canvasWidth - TabSideMargin;
        float x = Math.Max(back.Right + TabGap, TabStartX);
        float width = (right - x - TabGap) / 2f;
        if (width < TabMinWidth)
        {
            // The button is unusually far right: keep both tabs on the canvas, as wide as they still fit.
            x = Math.Max(0f, Math.Min(x, right - 2f * TabMinWidth - TabGap));
            width = Math.Max(1f, (right - x - TabGap) / 2f);
        }
        return (new ShopLayoutRect(x, top, width, height), new ShopLayoutRect(x + width + TabGap, top, width, height));
    }
}
