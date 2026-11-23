using System;

namespace STS2Mobile.Platform;

/// <summary>The rest site's options row: its box in canvas units and the scale its buttons are drawn at.</summary>
public readonly record struct RestOptionsRow(float Width, float Height, float Scale, float X, float Y);

/// <summary>A rectangle for one of the rest site's texts (the question above the buttons, the hovered option's copy).</summary>
public readonly record struct RestTextBlock(float X, float Y, float Width, float Height);

/// <summary>A point in the rest site's background container, or in one of its layers.</summary>
public readonly record struct RestPoint(float X, float Y);

/// <summary>The container's place and uniform scale that put the act's rest-site art over the whole canvas.</summary>
public readonly record struct RestCover(float Scale, float X, float Y);

/// <summary>
/// Pure layout for the portrait rest site: the two-to-four option row pinned above the bottom edge, the question and the
/// option copy, and the framing that turns the act's wide rest-site art into a cover of the phone canvas with the campfire
/// and the seated characters inside it.
/// <para>
/// The reference is the 1680-wide template for the same phone (4.48 units per point); this canvas is 1080 wide
/// (2.88 units per point), so a length from the template converts through points by <see cref="TemplateToLauncher"/>.
/// A scale applied to one of the game's own controls converts the same way, which keeps the drawn control the same number
/// of points across; a font set inside such a control keeps the template's own unit value, because the row's scale already
/// carries the conversion.
/// </para>
/// </summary>
public static class PortraitRestMath
{
    /// <summary>One unit of the 1680-wide reference template, in this app's units.</summary>
    public const float TemplateToLauncher = 1080f / 1680f;

    // ---- the options row -----------------------------------------------------------------------------------------------

    /// <summary>The game's option button, from its scene (a 250 x 163 drawing with a 246.786 x 162.879 minimum).</summary>
    public const float NativeOptionWidth = 246.786f;
    public const float NativeOptionHeight = 162.879f;
    public const float NativeOptionSeparation = 100f;

    /// <summary>One touch target: 44 points on the reference phone.</summary>
    public const float TouchTarget = 127f;

    /// <summary>
    /// The row's box is at least 800 template units wide, and 250 per button with 100 between them. Those are the button's
    /// own scene units (its drawing is 250 wide), so they are not converted: the row's scale carries the conversion.
    /// </summary>
    public const float OptionsFloorWidth = 800f;
    public const float OptionsButtonWidth = 250f;
    public const float OptionsSeparation = 100f;
    public const float OptionsHeight = 200f;

    /// <summary>The row grows to 1.6 at the template's width: 1.028571 on this canvas, so the drawn button stays 58 points.</summary>
    public const float OptionsMaxScale = 1.6f * TemplateToLauncher;

    /// <summary>The row keeps 128 template units from the sides (64 each), 82.29 here.</summary>
    public const float OptionsSideMargin = 128f * TemplateToLauncher;

    /// <summary>The row's top sits 1040 template units above the safe bottom (232 points), 668.57 here.</summary>
    public const float OptionsBottomInset = 1040f * TemplateToLauncher;

    /// <summary>
    /// The smallest scale that keeps a 44 point button. It is used when the row still fits the canvas; with five or more
    /// options the row has to shrink further, as in the reference.
    /// </summary>
    public const float MinOptionScale = TouchTarget / NativeOptionHeight;

    /// <summary>
    /// The option label is set in the button's own units at the template's 48, so it grows with the row's scale and reads at
    /// about 17 points when the row is at its full size.
    /// </summary>
    public const int OptionLabelFontSize = 48;

    /// <summary>
    /// Two options keep the game's own row; three or more always take exactly two rows (<see cref="OptionsRowCount"/>), so the
    /// widest row holds at most three buttons until the count passes six.
    /// </summary>
    public const int SingleRowMax = 2;

    /// <summary>
    /// The gap between the two rows, in the buttons' own units. An option's label hangs about 25 units below its box, so 48
    /// leaves it a little room above the next row's box.
    /// </summary>
    public const float OptionsRowGap = 48f;

    /// <summary>The row's box width: the template's rule, raised to the buttons' own combined minimum so a container never fights it.</summary>
    public static float OptionsWidth(int count, float minimumWidth) =>
        Math.Max(Math.Max(OptionsFloorWidth, count * OptionsButtonWidth + Math.Max(0, count - 1) * OptionsSeparation), minimumWidth);

    /// <summary>
    /// The row's scale: the width the row needs fits <see cref="OptionsSideMargin"/> inside the canvas, never above the
    /// template's 1.6 (converted). A row of up to four options keeps its buttons at 44 points; a longer row uses the two-row
    /// grid instead (<see cref="OptionsRowCount"/>).
    /// </summary>
    public static float OptionsScale(float rowWidth, float canvasWidth)
    {
        if (rowWidth <= 0f) return OptionsMaxScale;
        float fit = (canvasWidth - OptionsSideMargin) / rowWidth;
        float scale = Math.Min(OptionsMaxScale, fit);
        if (rowWidth * MinOptionScale <= canvasWidth) scale = Math.Max(scale, MinOptionScale);
        return Math.Max(scale, 0.05f);
    }

    /// <summary>The row's box and where it is drawn: centred, pinned <see cref="OptionsBottomInset"/> above the safe bottom.</summary>
    public static RestOptionsRow OptionsRow(int count, float minimumWidth, float canvasWidth, float canvasHeight)
    {
        float width = OptionsWidth(count, minimumWidth);
        float scale = OptionsScale(width, canvasWidth);
        return new RestOptionsRow(width, OptionsHeight, scale, (canvasWidth - width * scale) * 0.5f, canvasHeight - OptionsBottomInset);
    }

    // ---- the two-row grid ------------------------------------------------------------------------------------------------

    /// <summary>The natural width of a row of this many of the game's buttons.</summary>
    public static float NaturalRowWidth(int count) =>
        count * NativeOptionWidth + Math.Max(0, count - 1) * NativeOptionSeparation;

    /// <summary>
    /// The margin the widest four-button row has at <see cref="MinOptionScale"/> (38.2 units on the 1080 canvas). A grid's
    /// widest row keeps at least this margin, so the phone layout never runs closer to the edge than the single row does, and
    /// a four-button top row lands exactly on <see cref="MinOptionScale"/>.
    /// </summary>
    public static float GridRowEdge(float canvasWidth) =>
        Math.Max(0f, (canvasWidth - NaturalRowWidth(4) * MinOptionScale) * 0.5f);

    /// <summary>
    /// How many rows the options take: two keep the game's own row; three or more always take two, the top row taking the odd
    /// one (three options are 2+1, five 3+2, seven 4+3, eleven 6+5).
    /// </summary>
    public static int OptionsRowCount(int count) => count <= 0 ? 0 : count <= SingleRowMax ? 1 : 2;

    /// <summary>The options in one row: the top row takes the odd one, the bottom row is never wider than the top.</summary>
    public static int OptionsInRow(int count, int row)
    {
        int rows = OptionsRowCount(count);
        if (rows <= 1) return count;
        return row == 0 ? (count + 1) / 2 : count / 2;
    }

    /// <summary>
    /// The scale of both rows. Rows of two or three grow to the single row's cap (<see cref="OptionsMaxScale"/>): the grid is
    /// never wider than the matching single row would be. A four-button top row keeps the 44 point button, and from five in the
    /// top row (nine options) the whole grid shrinks so that row keeps <see cref="GridRowEdge"/> at each side.
    /// </summary>
    public static float OptionsGridScale(int count, float canvasWidth)
    {
        int widest = Math.Max(1, OptionsInRow(count, 0));
        float fit = (canvasWidth - 2f * GridRowEdge(canvasWidth)) / NaturalRowWidth(widest);
        float widestScale = widest <= 3 ? OptionsMaxScale : MinOptionScale;
        return Math.Max(Math.Min(widestScale, fit), 0.05f);
    }

    /// <summary>How tall the grid is drawn, from the top row's box to the bottom row's box.</summary>
    public static float OptionsGridHeight(int count, float canvasWidth)
    {
        int rows = Math.Max(1, OptionsRowCount(count));
        float scale = OptionsGridScale(count, canvasWidth);
        return ((rows - 1) * (OptionsHeight + OptionsRowGap) + OptionsHeight) * scale;
    }

    /// <summary>
    /// Fills one box per row: rows are centred and the bottom row is centred under the top one; the bottom row's box top stays
    /// exactly where a single row's is (<see cref="OptionsBottomInset"/> above the safe bottom) and the top row stands one
    /// row-gap above it. The caller's buffer bounds the number of rows, so it is never overrun.
    /// </summary>
    public static int OptionsGrid(int count, float canvasWidth, float canvasHeight, Span<RestOptionsRow> rows)
    {
        int rowCount = OptionsRowCount(count);
        if (rowCount <= 0 || rows.Length == 0) return 0;
        rowCount = Math.Min(rowCount, rows.Length);
        float scale = OptionsGridScale(count, canvasWidth);
        float pitch = (OptionsHeight + OptionsRowGap) * scale;
        float bottomRow = canvasHeight - OptionsBottomInset;
        for (int row = 0; row < rowCount; row++)
        {
            int inRow = OptionsInRow(count, row);
            float natural = NaturalRowWidth(inRow);
            float width = OptionsWidth(inRow, natural);
            rows[row] = new RestOptionsRow(
                width, OptionsHeight, scale, (canvasWidth - width * scale) * 0.5f, bottomRow - (rowCount - 1 - row) * pitch);
        }
        return rowCount;
    }

    // ---- the question and the copy -------------------------------------------------------------------------------------

    /// <summary>The question is 80 template units (51 here, 17.9 points) in a 150 unit box, 64 units in from the left.</summary>
    public const int HeaderFontSize = 51;
    public const float HeaderInset = 64f * TemplateToLauncher;
    public const float HeaderRightInset = 128f * TemplateToLauncher;
    public const float HeaderHeight = 150f * TemplateToLauncher;

    /// <summary>It stands 200 template units above the row (128.57 here).</summary>
    public const float HeaderGap = 200f * TemplateToLauncher;

    /// <summary>The option copy is 64 template units (41 here, 14.3 points) in a 360 unit box, 540 above the safe bottom.</summary>
    public const int DescriptionFontSize = 41;
    public const float DescriptionInset = 96f * TemplateToLauncher;
    public const float DescriptionRightInset = 192f * TemplateToLauncher;
    public const float DescriptionHeight = 360f * TemplateToLauncher;
    public const float DescriptionBottomInset = 540f * TemplateToLauncher;

    public static RestTextBlock Header(float rowTop, float canvasWidth) =>
        new(HeaderInset, rowTop - HeaderGap, canvasWidth - HeaderRightInset, HeaderHeight);

    public static RestTextBlock Description(float canvasWidth, float canvasHeight) =>
        new(DescriptionInset, canvasHeight - DescriptionBottomInset, canvasWidth - DescriptionRightInset, DescriptionHeight);

    // ---- the act's art -------------------------------------------------------------------------------------------------

    /// <summary>The act's rest-site scenes are authored on a 1920 x 1080 sheet.</summary>
    public const float AuthoredWidth = 1920f;
    public const float AuthoredHeight = 1080f;

    /// <summary>The campfire's place on that sheet, and how much the camp is drawn up around it.</summary>
    public const float RestFocusX = 0.46f;
    public const float RestFocusY = 0.62f;
    public const float RestZoom = 0.74f;

    /// <summary>The uniform scale that makes the 1920 x 1080 sheet cover the canvas (never a stretch).</summary>
    public static float CoverScale(float canvasWidth, float canvasHeight) =>
        Math.Max(canvasWidth / AuthoredWidth, canvasHeight / AuthoredHeight);

    /// <summary>
    /// The point of the container that lands on the canvas centre: the centre of the game's 1920 x 1080 room, which is the
    /// container's origin (its offsets) reflected. Children of a centre-anchored container are laid out around that origin.
    /// </summary>
    public static RestPoint ArtAnchor(float containerOffsetLeft, float containerOffsetTop) =>
        new(-containerOffsetLeft, -containerOffsetTop);

    /// <summary>The campfire: a fraction of the authored sheet, in the container's coordinates.</summary>
    public static RestPoint ArtFocus(RestPoint anchor, float focusX = RestFocusX, float focusY = RestFocusY) =>
        new(anchor.X + AuthoredWidth * (focusX - 0.5f), anchor.Y + AuthoredHeight * (focusY - 0.5f));

    /// <summary>Where the container goes, and at what scale, so its anchor is the canvas centre and its scale is the cover.</summary>
    public static RestCover ArtCover(float canvasWidth, float canvasHeight, RestPoint anchor)
    {
        float scale = CoverScale(canvasWidth, canvasHeight);
        return new RestCover(scale, canvasWidth * 0.5f - anchor.X * scale, canvasHeight * 0.5f - anchor.Y * scale);
    }

    /// <summary>
    /// A camp piece's place after framing: the whole sheet is scaled around <paramref name="focus"/> and the focus is
    /// carried onto the anchor, then the piece is scaled about its own pivot (which is why a pivot moves the drawing by
    /// <c>(zoom - 1)</c> of itself).
    /// </summary>
    public static RestPoint FramePosition(RestPoint basePosition, RestPoint basePivot, RestPoint anchor, RestPoint focus, float zoom) =>
        new(
            anchor.X + (basePosition.X - focus.X) * zoom + (zoom - 1f) * basePivot.X,
            anchor.Y + (basePosition.Y - focus.Y) * zoom + (zoom - 1f) * basePivot.Y);

    /// <summary>A camp piece's scale after framing.</summary>
    public static RestPoint FrameScale(RestPoint baseScale, float zoom) => new(baseScale.X * zoom, baseScale.Y * zoom);

    /// <summary>
    /// The pieces that make up the camp rather than its surroundings: the logs, the fire and its lights, the smoke and the
    /// sparks. The wall lights and the full-room ground light stay with the art, so no hard edge of a shrunk layer shows.
    /// </summary>
    public static bool IsCampPiece(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.Contains("Log", StringComparison.Ordinal)
            || name.StartsWith("light_", StringComparison.Ordinal)
            || name.StartsWith("Fire", StringComparison.Ordinal)
            || name.StartsWith("fire_", StringComparison.Ordinal)
            || name.StartsWith("SteppedFire", StringComparison.Ordinal)
            || name.StartsWith("sparks", StringComparison.Ordinal)
            || string.Equals(name, "smoke", StringComparison.Ordinal);
    }
}
