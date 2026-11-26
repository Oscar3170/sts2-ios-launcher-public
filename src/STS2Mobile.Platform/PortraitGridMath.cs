using System;

namespace STS2Mobile.Platform;

/// <summary>
/// The screen a portrait card grid stands in. Each screen's own footer (its prompt, its View Upgrades box, its Back button)
/// owns the bottom of the screen, so the grid's viewport stops above a different band on each.
/// </summary>
public enum PortraitGridScreen
{
    DeckView,
    Upgrade,
    Enchant,
    Transform,
    DeckSelect,
    Pile,
}

/// <summary>
/// Pure layout math for the portrait card grids: three columns of native rows, the deck view's sort row, the per-screen
/// footer bands and the View Upgrades box. All numbers are canvas units on the 1080-wide portrait canvas (2.88 units to a
/// point), taken from the reference phone layout's 1680-wide canvas by 1080/1680 where they are ported from it.
/// <para>
/// The grid keeps the game's own row recycling: <see cref="CardWidthFor"/> is the card size that makes the native
/// <c>Columns</c> arithmetic ((scroll + padding) / (card + padding)) come out at exactly three, so
/// <c>InitGrid</c> and <c>AllocateCardHolders</c> stay the only things that lay cards out.
/// </para>
/// </summary>
public static class PortraitGridMath
{
    /// <summary>Cards to a row in every portrait grid.</summary>
    public const int Columns = 3;

    /// <summary>NCard.defaultSize: a card's design width and height.</summary>
    public const float CardWidth = 300f;
    public const float CardHeight = 422f;

    /// <summary>NCardGrid.CardPadding: the gap the native grid leaves between cards.</summary>
    public const float CardPadding = 40f;

    /// <summary>The grid's own side margins around the scrolling cards.</summary>
    public const float SideInset = 30f;

    /// <summary>The scrollbar's offsets from the grid's right edge, so it stands in the right margin.</summary>
    public const float ScrollbarLeft = -38f;
    public const float ScrollbarRight = -8f;

    /// <summary>
    /// NDeckViewScreen.DisplayCards puts its cards 100 units down for the native sort row inside the scroll area. Portrait
    /// keeps that offset and puts the sort row above the grid instead, so the grid's top is pulled up by it and the first
    /// row still starts 80 units under the sort row.
    /// </summary>
    public const float DeckYOffset = 100f;

    /// <summary>NCardGrid.InitGrid leaves this much room above the first row, and this much below the last.</summary>
    public const float ContentTopPadding = 80f;
    public const float ContentBottomPadding = 320f;

    /// <summary>
    /// How far the cards fade out at the grid's top and bottom edges (the game's own border band is 5 % of a 1080 unit
    /// screen, 54). Shorter than <see cref="ContentTopPadding"/>, so the first row at rest is never faded.
    /// </summary>
    public const float GridMaskFade = 64f;

    /// <summary>The deck view's sort row: four buttons across the grid's width, 44 points tall.</summary>
    public const float SortHeaderHeight = 135f;
    public const float SortHeaderGap = 8f;
    public const float SortButtonHeight = 127f;
    public const float SortButtonGap = 6f;
    public const float SortArrowSize = 41f;
    /// <summary>The largest sort label: 28 units is 9.7 points, with room for the text plus its arrow in a quarter of the row.</summary>
    public const int SortFontMax = 28;
    public const int SortFontMin = 20;

    /// <summary>The View Upgrades box drawn 44 points tall (checkbox and label together).</summary>
    public const float ToggleHeight = 127f;

    /// <summary>
    /// How much of the canvas's bottom each screen keeps for its own footer: the deck view's sort-less bottom text and
    /// View Upgrades box, the selection screens' prompt/legend/confirm and Back. The four selection numbers are the
    /// reference layout's 425 / (648 + 28) / 710 / (414 + 28) / (400 + 28) on its 1680-wide canvas.
    /// </summary>
    public const float FooterDeckView = 273f;
    public const float FooterUpgrade = 435f;
    public const float FooterEnchant = 457f;
    public const float FooterTransform = 284.1f;
    public const float FooterDeckSelect = 275.1f;

    /// <summary>The gap between a selection screen's prompt and the grid above it (the reference layout's 28).</summary>
    public const float PromptGap = 18f;

    /// <summary>The top edge of the View Upgrades box, measured up from the canvas's bottom.</summary>
    public const float ToggleTopDeckView = 144f;
    public const float ToggleTopUpgrade = 135.6f;
    public const float ToggleTopTransform = 144f;

    /// <summary>
    /// The deck view's pile info line: a right-aligned block inside the canvas above the footer, clear of the Back button
    /// on the left. Half the canvas wide, wrapped, at 11 points (the reference layout's right-hand line).
    /// </summary>
    public const float InfoTextTop = 270f;
    public const float InfoTextWidthShare = 0.5f;
    public const float InfoTextHeight = 120f;
    public const int InfoTextFontSize = 32;

    /// <summary>The band each screen keeps at the bottom of the canvas.</summary>
    public static float Footer(PortraitGridScreen screen) => screen switch
    {
        PortraitGridScreen.DeckView => FooterDeckView,
        PortraitGridScreen.Pile => FooterDeckView,
        PortraitGridScreen.Upgrade => FooterUpgrade,
        PortraitGridScreen.Enchant => FooterEnchant,
        PortraitGridScreen.Transform => FooterTransform,
        _ => FooterDeckSelect,
    };

    /// <summary>Where the View Upgrades box's top edge stands, in the three footers that have one.</summary>
    public static float ToggleTop(PortraitGridScreen screen) => screen switch
    {
        PortraitGridScreen.Upgrade => ToggleTopUpgrade,
        PortraitGridScreen.Transform => ToggleTopTransform,
        _ => ToggleTopDeckView,
    };

    /// <summary>
    /// Slack left in each column so the native <c>Columns</c> quotient, computed in float, cannot land just under three and
    /// truncate to two. The card is one unit narrower than an exact third; the three cards then have 3 units to spare.
    /// </summary>
    public const float ColumnSlack = 1f;

    /// <summary>
    /// The band the grid starts from: the top bar's bottom, or below the relic rows when the strip reaches lower (the deck and
    /// every selection screen keep the run's relics on screen; a relic holder over a sort button or a card is not acceptable).
    /// </summary>
    public static float HeaderBottom(float topBarBottom, float lowestRelicBottom) =>
        lowestRelicBottom > 0.01f ? Math.Max(topBarBottom, lowestRelicBottom + RelicGap) : topBarBottom;

    /// <summary>The gap between the lowest visible relic holder and the grid's header band (the reference layout's 16).</summary>
    public const float RelicGap = 16f;

    /// <summary>The cards' scrolling width: the canvas less the grid's own side margins.</summary>
    public static float ScrollWidth(float canvasWidth) => Math.Max(0f, canvasWidth - 2f * SideInset);

    /// <summary>
    /// The card width that makes the native <c>Columns</c> getter yield exactly <see cref="Columns"/>: the padding between
    /// the columns is taken out of the scroll width first, then the rest is shared less <see cref="ColumnSlack"/>.
    /// </summary>
    public static float CardWidthFor(float scrollWidth) =>
        Math.Max(0f, (scrollWidth - CardPadding * (Columns - 1)) / Columns - ColumnSlack);

    /// <summary>A card's height at that width, keeping the native 300 : 422 shape.</summary>
    public static float CardHeightFor(float cardWidth) => cardWidth * (CardHeight / CardWidth);

    /// <summary>The scale a grid card holder needs so a 300-unit card is <paramref name="cardWidth"/> across.</summary>
    public static float HolderScale(float cardWidth) => cardWidth <= 0.01f ? 1f : cardWidth / CardWidth;

    /// <summary>The native <c>Columns</c> arithmetic, for checking the size actually gives three.</summary>
    public static int ColumnsFor(float scrollWidth, float cardWidth) =>
        cardWidth <= 0.01f ? 0 : (int)((scrollWidth + CardPadding) / (cardWidth + CardPadding));

    /// <summary>The grid's top edge, just under the header band (and under the sort row in the deck view).</summary>
    public static float GridTop(float headerBottom, bool sortRow)
    {
        float top = headerBottom + SortHeaderGap;
        return sortRow ? top + SortHeaderHeight : top;
    }

    /// <summary>The grid's bottom edge: the canvas less the screen's footer band.</summary>
    public static float ViewportBottom(float canvasHeight, PortraitGridScreen screen) => canvasHeight - Footer(screen);

    /// <summary>
    /// The footer band for a prompt whose own top is known (the enchant screen's legend makes it taller than the static
    /// band): whichever of the two leaves the prompt more room.
    /// </summary>
    public static float PromptReserve(float staticReserve, float canvasHeight, float promptTop) =>
        Math.Max(staticReserve, canvasHeight - promptTop + PromptGap);

    /// <summary>NCardGrid's scroll limit: content taller than the grid scrolls up to its foot, shorter content is centred.</summary>
    public static float ScrollLimit(float gridHeight, float contentHeight) => Math.Min(0f, gridHeight - contentHeight);

    /// <summary>
    /// The native content height for a number of rows: the cards, their padding and the grid's own top and bottom padding.
    /// </summary>
    public static float ContentHeight(int rows, float cardHeight) =>
        rows <= 0 ? 0f : rows * cardHeight + (rows - 1) * CardPadding + ContentTopPadding + ContentBottomPadding;

    /// <summary>
    /// Where the last row's bottom edge sits in the grid when the native limit is reached: the game's own bottom padding
    /// above the grid's foot, so the last row is always reachable above the screen's footer.
    /// </summary>
    public static float LastRowBottomAtLimit(float gridHeight) => gridHeight - ContentBottomPadding;

    /// <summary>One sort button's width: the row shared between four buttons with a small gap.</summary>
    public static float SortButtonWidth(float headerWidth, int buttons) =>
        buttons <= 0 ? 0f : MathF.Floor((headerWidth - SortButtonGap * (buttons - 1)) / buttons);

    /// <summary>The View Upgrades box's scale: 44 points tall, never smaller than the game draws it.</summary>
    public static float ToggleScale(float nativeHeight) =>
        nativeHeight <= 0.01f ? 1f : Math.Max(1f, ToggleHeight / nativeHeight);

    /// <summary>The box's left edge when it is centred across the canvas.</summary>
    public static float ToggleLeft(float canvasWidth, float nativeWidth, float scale) =>
        (canvasWidth - nativeWidth * scale) * 0.5f;

    /// <summary>The deck view's info line box: half the canvas, flush to the right margin (Back keeps the left).</summary>
    public static float InfoTextWidth(float canvasWidth) => canvasWidth * InfoTextWidthShare;

    /// <summary>Its right edge: the canvas less the grid's own side margin.</summary>
    public static float InfoTextLeft(float canvasWidth) => canvasWidth - SideInset - InfoTextWidth(canvasWidth);
}
