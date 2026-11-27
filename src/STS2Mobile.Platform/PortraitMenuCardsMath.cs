using System;

namespace STS2Mobile.Platform;

/// <summary>A menu screen's cards laid in balanced lines and the room the whole block takes in the safe band.</summary>
public readonly record struct MenuCardLines(int[] Starts, float Scale, float Top, float Width, float Height)
{
    /// <summary>How many lines the cards form (the last entry in <see cref="Starts"/> is the end).</summary>
    public int Lines => Starts.Length - 1;
}

/// <summary>
/// Pure geometry for the portrait menu card rows shared by the singleplayer/multiplayer mode cards and the compendium:
/// balanced lines of up to three cards (4 = 2 + 2, 5 = 3 + 2), sized for a full line of three so hiding a card never
/// resizes the rest, spaced from each card's DRAWN bounds (the art is taller than some card controls, and spacing the
/// controls made one line's art cover the next), and centred in the safe band above the screen's reserved footer. The
/// line boundaries, the row scale and top, and each card's place in its cell are all decided here; the screen owners only
/// measure their cards and write the results to their nodes.
/// </summary>
public static class PortraitMenuCardsMath
{
    /// <summary>The most cards a line holds; a row is always sized for this many.</summary>
    public const int LineMax = 3;

    /// <summary>The gap between cards and between lines, in card units (native 18).</summary>
    public const float Gap = 18f;

    /// <summary>The first line starts this far under the safe band's top; the block is then centred lower if it fits.</summary>
    public const float TopGap = 100f;

    /// <summary>The room a menu screen keeps under the card block for its own title and the Back button.</summary>
    public const float Reserved = 430f;

    /// <summary>The band kept empty either side of a full line.</summary>
    public const float SideMargin = 80f;

    /// <summary>The smallest a card is drawn, so a short screen still shows something usable.</summary>
    public const float MinScale = 0.3f;

    /// <summary>The least room a row is laid out in, even on a window that reports a tiny band.</summary>
    public const float MinAvailable = 200f;

    /// <summary>
    /// Where each line starts, plus the count as the end: ceil(count / lineMax) lines, the earlier lines taking the extra
    /// card. A count of zero still reports one empty line.
    /// </summary>
    public static int[] LineStarts(int count, int lineMax = LineMax)
    {
        int cards = Math.Max(0, count);
        int perLine = Math.Max(1, lineMax);
        int lines = Math.Max(1, (cards + perLine - 1) / perLine);
        var starts = new int[lines + 1];
        for (int line = 0; line < lines; line++)
            starts[line + 1] = starts[line] + cards / lines + (line < cards % lines ? 1 : 0);
        return starts;
    }

    /// <summary>The line and column of the card at <paramref name="index"/>.</summary>
    public static (int Line, int Col) Cell(int[] starts, int index)
    {
        int line = 0;
        while (line < starts.Length - 2 && index >= starts[line + 1]) line++;
        return (line, index - starts[line]);
    }

    /// <summary>The card nearest <paramref name="col"/> in <paramref name="line"/> (a shorter line keeps its last card).</summary>
    public static int NearestInLine(int[] starts, int line, int col) =>
        Math.Min(starts[line] + Math.Max(0, col), starts[line + 1] - 1);

    /// <summary>
    /// The scale and top of a row of <paramref name="count"/> cards whose drawn cells are <paramref name="cellWidth"/> by
    /// <paramref name="cellHeight"/>: the row is sized for <see cref="LineMax"/> cells (whether or not that many are shown),
    /// <paramref name="below"/> adds room under the last line for a row of buttons, and the whole block is centred in the
    /// band above the screen's reserved footer.
    /// </summary>
    public static MenuCardLines Plan(
        int count, float safeWidth, float safeTop, float safeHeight, float cellWidth, float cellHeight, float below = 0f)
    {
        int cards = Math.Max(1, count);
        int[] starts = LineStarts(cards);
        int rows = starts.Length - 1;
        float cellW = Math.Max(1f, cellWidth);
        float cellH = Math.Max(1f, cellHeight);
        float rowWidth = LineMax * cellW + (LineMax - 1) * Gap;
        float height = rows * cellH + (rows - 1) * Gap;
        if (below > 0f) height += Gap + below;
        float available = Math.Max(MinAvailable, safeHeight - Reserved);
        float byWidth = (safeWidth - SideMargin) / rowWidth;
        float byHeight = available / Math.Max(1f, height);
        float scale = Math.Max(MinScale, Math.Min(byWidth, byHeight));
        float top = safeTop + TopGap + Math.Max(0f, available - height * scale) * 0.5f;
        return new MenuCardLines(starts, scale, top, rowWidth, height);
    }

    /// <summary>
    /// The top-left position of the card at <paramref name="index"/> inside the row, before the row scale: its cell's
    /// centre, moved by the difference between the cell and the card's DRAWN bounds so the art (not the control rectangle)
    /// is what the gap is measured from.
    /// </summary>
    public static (float X, float Y) CellOffset(
        int[] starts, int index, float cellWidth, float cellHeight, float gap,
        float drawnWidth, float drawnHeight, float drawnX, float drawnY)
    {
        (int line, int col) = Cell(starts, index);
        int inLine = Math.Max(1, starts[line + 1] - starts[line]);
        float cellLeft = col * (cellWidth + gap) - (inLine * (cellWidth + gap) - gap) * 0.5f;
        return (
            cellLeft + (cellWidth - drawnWidth) * 0.5f - drawnX,
            line * (cellHeight + gap) + (cellHeight - drawnHeight) * 0.5f - drawnY);
    }
}
