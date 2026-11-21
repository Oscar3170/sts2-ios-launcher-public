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
/// Pure layout math for the portrait shop: the cloth and the stock viewport it frames, the Cards and Items pages (the
/// desktop mod's 3-column card page with its staggered Power and colorless row, and its three-row Items page), the weird
/// shop's 2-column relic grid and the Blitz shop's 2-column slots over a footer, plus the Cards/Items tab row.
/// <para>
/// Lengths are in this app's canvas units (1080 across; 2.88 units are one point on an iPhone 11 Pro). The mod's own
/// template is 1680 wide for the same phone, so its canvas-space margins and heights are converted here; the game's own
/// slot sizes (a card is 300 x 422 units, a footprint divisor 319 or 1539) are already canvas units and stay as they are.
/// A returned scale is the slot's drawing scale in this canvas (1 draws the game's own art size).
/// </para>
/// </summary>
public static class PortraitShopMath
{
    /// <summary>One unit of the desktop mod's 1680-wide template, in this app's units.</summary>
    public const float TsuiToLauncher = 1080f / 1680f;

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

    /// <summary>Space around the stock's content.</summary>
    public const float Pad = 18f * TsuiToLauncher;

    // ---- The Cards page -------------------------------------------------------------------------------------------
    /// <summary>A card slot's share of a column at scale 1.</summary>
    public const float CardSlotWidth = 319f;

    /// <summary>The card block's height at scale 1: two class rows, the colorless row and their prices.</summary>
    public const float CardBlockHeight = 1539f;

    /// <summary>The first row's origin below the stock's top, at scale 1.</summary>
    public const float CardRowOrigin = 227f;

    /// <summary>Row pitch at scale 1 (a card's height plus the gap and price).</summary>
    public const float CardRowPitch = 513f;

    /// <summary>The class-card column margin, converted from the mod's template.</summary>
    public const float CardColumnGap = 28f * TsuiToLauncher;

    /// <summary>The card block's bottom margin, converted from the mod's template.</summary>
    public const float CardRowInset = 30f * TsuiToLauncher;

    /// <summary>The mod's colorless pair is spread over this part of the content width.</summary>
    public const float CardColorlessSpread = 2.65f;

    /// <summary>The mod's smallest and largest card scales, in this canvas's units.</summary>
    public const float MinCardScale = 0.82f * TsuiToLauncher;
    public const float MaxCardScale = 1.46f * TsuiToLauncher;

    // ---- The Items page -------------------------------------------------------------------------------------------
    public const float RelicScale = 2.35f * TsuiToLauncher;
    public const float PotionScale = 2.35f * TsuiToLauncher;
    public const float RemovalScale = 2.2f * TsuiToLauncher;
    /// <summary>The Items page leaves this much of the stock under the last row, converted from the mod's template.</summary>
    public const float ItemsBottomInset = 48f * TsuiToLauncher;
    private const float RelicRow = 0.16f;
    private const float PotionRow = 0.42f;
    private const float RemovalRow = 0.72f;

    // ---- The weird shop's relic grid -------------------------------------------------------------------------------
    private const float WeirdTop = 32f * TsuiToLauncher;
    private const float WeirdBottomInset = 46f * TsuiToLauncher;
    private const float WeirdCellHeight = 325f;
    private const float WeirdSideInset = 52f * TsuiToLauncher;
    public const float WeirdWidth = 332f;
    public const float WeirdMaxScale = 2f * TsuiToLauncher;

    // ---- The Blitz shop ---------------------------------------------------------------------------------------------
    /// <summary>The line under the slots that says how many picks remain.</summary>
    public const float BlitzFooter = 82f * TsuiToLauncher;

    private const float BlitzCellPad = 16f * TsuiToLauncher;
    public const float BlitzMaxScale = 1.8f * TsuiToLauncher;
    private const float BlitzWorstCase = 1.03f;
    public const int BlitzFooterFontSize = 31;

    // ---- The Cards/Items tab row ------------------------------------------------------------------------------------
    public const float TabHeight = 200f * TsuiToLauncher;
    public const float TabMinHeight = 127f;
    public const float TabGap = 24f;
    public const float TabStartX = 442f * TsuiToLauncher;
    public const float TabSideMargin = 60f * TsuiToLauncher;
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

    /// <summary>The row's vertical middle: the Back button and both tabs share it.</summary>
    public static float TabRowCentre(float canvasHeight, float backHeight) =>
        TabRowTop(canvasHeight, backHeight) + Math.Max(TabMinHeight, backHeight) / 2f;

    /// <summary>
    /// Where a shop button waits when hidden: its native hide offset from the show point, moved far enough left that no
    /// part of it (<paramref name="width"/>) is on the canvas.
    /// </summary>
    public static float HideX(float showX, float hideOffsetX, float width) =>
        Math.Min(showX + hideOffsetX, -Math.Abs(width));

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
