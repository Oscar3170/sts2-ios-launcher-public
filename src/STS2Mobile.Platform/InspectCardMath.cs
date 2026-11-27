using System;
using System.Collections.Generic;

namespace STS2Mobile.Platform;

/// <summary>
/// The portrait inspect-card screen's composition: the inspected card keeps its centred size, keyword tips are packed into
/// rows above it (or below when they cannot fit above), card previews stack in the free column beside it, and the
/// View Upgrades box sits centred under the card exactly as the deck and card-grid inspection places it. No block is ever
/// allowed to overlap the inspected card; blocks shrink from the preferred phone scale down to the minimum readable one.
/// Pure canvas geometry (1080 wide), so the placement rules are unit-tested on the host.
/// </summary>
public static class InspectCardMath
{
    /// <summary>The native card's drawn size at scale one (NCard.defaultSize).</summary>
    public const float CardWidth = 300f;
    public const float CardHeight = 422f;

    /// <summary>The gap between two tips, two previews, or two rows of them.</summary>
    public const float Gap = 8f;

    /// <summary>The margin kept clear around every block and at the canvas edges.</summary>
    public const float Margin = 10f;

    /// <summary>The gap between the inspected card and a tip or preview block.</summary>
    public const float CardGap = 12f;

    /// <summary>The floor on the scale of a block that has to shrink; below it the block is kept whole and moved instead.</summary>
    public const float MinReadableScale = 0.9f;

    /// <summary>A scale no block is taken below when even the minimum readable scale cannot fit its side.</summary>
    public const float AbsoluteMinScale = 0.3f;

    public readonly record struct Size(float Width, float Height);

    public readonly record struct Point(float X, float Y);

    public readonly record struct Rect(float X, float Y, float Width, float Height)
    {
        public float Right => X + Width;
        public float Bottom => Y + Height;
        public float CentreX => X + Width * 0.5f;
        public float CentreY => Y + Height * 0.5f;

        public bool Intersects(Rect other) =>
            X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

        public bool Encloses(Rect other) =>
            other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;
    }

    /// <summary>An unscaled packing of items: the block's size and each item's top-left inside it.</summary>
    public readonly record struct Grid(Size Size, IReadOnlyList<Point> Offsets);

    /// <summary>A block placed on the canvas: its drawn rect, the scale applied to the unscaled items, and their offsets.</summary>
    public readonly record struct Block(Rect Rect, float Scale, IReadOnlyList<Point> Offsets);

    public readonly record struct Layout(Rect Card, Rect Upgrade, Block? Text, Block? Cards);

    public readonly record struct Request(
        float CanvasWidth,
        float CanvasHeight,
        float CardCentreX,
        float CardCentreY,
        float CardScale,
        IReadOnlyList<Size> TextTips,
        IReadOnlyList<Size> CardPreviews,
        Rect Upgrade,
        float PreferredScale,
        float MinScale,
        Rect? LeftArrow,
        Rect? RightArrow);

    /// <summary>
    /// Places the text tips and the preview cards around the inspected card, plus its View Upgrades box. The card itself is
    /// not moved: it is what the player opened and keeps the game's own size and centre.
    /// </summary>
    public static Layout Compute(Request request)
    {
        float canvasWidth = Math.Max(request.CanvasWidth, 1f);
        float canvasHeight = Math.Max(request.CanvasHeight, 1f);
        float cardScale = Math.Max(request.CardScale, 0.01f);
        var card = new Rect(
            request.CardCentreX - CardWidth * cardScale * 0.5f,
            request.CardCentreY - CardHeight * cardScale * 0.5f,
            CardWidth * cardScale,
            CardHeight * cardScale);

        Rect upgrade = request.Upgrade;
        bool upgradeShown = upgrade.Width > 0f && upgrade.Height > 0f;

        Block? text = PlaceText(request, card, upgrade, upgradeShown);
        Block? cards = PlacePreviews(request, card, text);
        return new Layout(card, upgrade, text, cards);
    }

    private static Block? PlaceText(Request request, Rect card, Rect upgrade, bool upgradeShown)
    {
        if (request.TextTips.Count == 0) return null;
        float canvasWidth = Math.Max(request.CanvasWidth, 1f);
        float canvasHeight = Math.Max(request.CanvasHeight, 1f);
        float maxWidth = Math.Max(canvasWidth - 2f * Margin, 1f);
        Grid grid = Pack(request.TextTips, maxWidth, Gap);
        if (grid.Size.Width <= 0f || grid.Size.Height <= 0f) return null;

        float aboveTop = Margin;
        float aboveBottom = card.Y - CardGap;
        float aboveHeight = aboveBottom - aboveTop;
        float belowTop = upgradeShown ? Math.Max(card.Bottom + CardGap, upgrade.Bottom + CardGap) : card.Bottom + CardGap;
        float belowHeight = canvasHeight - Margin - belowTop;

        float aboveScale = FitScale(grid.Size, maxWidth, aboveHeight, request.PreferredScale);
        float belowScale = FitScale(grid.Size, maxWidth, belowHeight, request.PreferredScale);
        bool useAbove;
        float scale;
        if (aboveScale >= request.MinScale)
        {
            useAbove = true;
            scale = aboveScale;
        }
        else if (belowScale >= request.MinScale)
        {
            useAbove = false;
            scale = belowScale;
        }
        else if (aboveHeight >= belowHeight)
        {
            // Neither side fits at the readable floor: keep the block out of the card, shrinking it as the last resort.
            useAbove = true;
            scale = Math.Max(AbsoluteMinScale, aboveScale);
        }
        else
        {
            useAbove = false;
            scale = Math.Max(AbsoluteMinScale, belowScale);
        }

        float width = grid.Size.Width * scale;
        float height = grid.Size.Height * scale;
        float x = (canvasWidth - width) * 0.5f;
        float y = useAbove ? card.Y - CardGap - height : belowTop;
        // A scale clamped to the absolute floor can still be too tall for the chosen band (a pathological stack); pin it
        // inside the canvas while the card is what may overlap then, never the other way round.
        y = Math.Clamp(y, Margin, Math.Max(Margin, canvasHeight - Margin - height));
        var rect = new Rect(x, y, width, height);
        return new Block(rect, scale, grid.Offsets);
    }

    private static Block? PlacePreviews(Request request, Rect card, Block? text)
    {
        if (request.CardPreviews.Count == 0) return null;
        float canvasWidth = Math.Max(request.CanvasWidth, 1f);
        float canvasHeight = Math.Max(request.CanvasHeight, 1f);
        float maxWidth = Math.Max(canvasWidth - 2f * Margin, 1f);
        Grid column = PackColumn(request.CardPreviews, maxWidth, Gap);
        if (column.Size.Width <= 0f || column.Size.Height <= 0f) return null;

        // A column beside the card: the previews stay at the card's own vertical band, clear of the keyword rows above.
        Rect rightSide = new(card.Right + CardGap, Margin, Math.Max(canvasWidth - Margin - card.Right - CardGap, 0f), canvasHeight - 2f * Margin);
        Rect leftSide = new(Margin, Margin, Math.Max(card.X - Margin - CardGap, 0f), canvasHeight - 2f * Margin);
        bool preferRight = rightSide.Width >= leftSide.Width;
        Block? side = PlaceSide(column, preferRight ? rightSide : leftSide, card,
            preferRight ? request.RightArrow : request.LeftArrow, request, text);
        if (side is { } beside && (text is not { } firstText || !beside.Rect.Intersects(firstText.Rect)))
            return beside;

        Block? other = PlaceSide(column, preferRight ? leftSide : rightSide, card,
            preferRight ? request.LeftArrow : request.RightArrow, request, text);
        if (other is { } flipped && (text is not { } secondText || !flipped.Rect.Intersects(secondText.Rect)))
            return flipped;
        if (side is { } fallback && (text is not { } thirdText || !fallback.Rect.Intersects(thirdText.Rect)))
            return fallback;

        return PlacePreviewRow(request, card, text);
    }

    private static Block? PlaceSide(Grid column, Rect side, Rect card, Rect? arrow, Request request, Block? text)
    {
        if (side.Width <= 0f || side.Height <= 0f) return null;
        float canvasHeight = Math.Max(request.CanvasHeight, 1f);
        float scale = Math.Min(request.PreferredScale, side.Width / column.Size.Width);
        if (scale < request.MinScale) return null;

        float height = column.Size.Height * scale;
        float start = Math.Max(card.Y, side.Y);
        if (arrow is { } blocked && blocked.Intersects(new Rect(side.X, side.Y, side.Width, side.Height)))
        {
            float belowArrow = blocked.Bottom + Gap;
            if (belowArrow + height <= canvasHeight - Margin)
                start = Math.Max(start, belowArrow);
        }
        float available = canvasHeight - Margin - start;
        if (height > available)
        {
            scale = Math.Min(scale, available / column.Size.Height);
            if (scale < request.MinScale) return null;
            height = column.Size.Height * scale;
        }
        // When the keyword tips had to go below the card, keep the previews beside it (above the tips) instead of running
        // down into them.
        if (text is { } placed && placed.Rect.Y >= card.Bottom)
        {
            float besideRoom = card.Bottom - CardGap - start;
            if (height > besideRoom)
            {
                scale = Math.Min(scale, besideRoom / column.Size.Height);
                if (scale < request.MinScale) return null;
                height = column.Size.Height * scale;
            }
        }
        float width = column.Size.Width * scale;
        float x = side.X + (side.Width - width) * 0.5f;
        float y = Math.Min(start, Math.Max(side.Y, canvasHeight - Margin - height));
        return new Block(new Rect(x, y, width, height), scale, column.Offsets);
    }

    private static Block PlacePreviewRow(Request request, Rect card, Block? text)
    {
        float canvasWidth = Math.Max(request.CanvasWidth, 1f);
        float canvasHeight = Math.Max(request.CanvasHeight, 1f);
        float maxWidth = Math.Max(canvasWidth - 2f * Margin, 1f);
        Grid row = Pack(request.CardPreviews, maxWidth, Gap);
        bool textBelow = text is { } placed && placed.Rect.Y >= card.Bottom;
        // Put the row on the side opposite the keyword tips; when the tips are above (the usual case) the previews go
        // under the card, below the View Upgrades box.
        Rect upgrade = request.Upgrade;
        bool upgradeShown = upgrade.Width > 0f && upgrade.Height > 0f;
        float top = textBelow
            ? Margin
            : upgradeShown ? Math.Max(card.Bottom + CardGap, upgrade.Bottom + CardGap) : card.Bottom + CardGap;
        float bottom = textBelow
            ? card.Y - CardGap
            : canvasHeight - Margin;
        float height = Math.Max(bottom - top, 1f);
        float scale = FitScale(row.Size, maxWidth, height, request.PreferredScale);
        if (scale < request.MinScale)
            scale = Math.Max(AbsoluteMinScale, scale);
        float width = row.Size.Width * scale;
        float drawnHeight = row.Size.Height * scale;
        float x = (canvasWidth - width) * 0.5f;
        float y = textBelow ? Math.Max(Margin, bottom - drawnHeight) : top;
        y = Math.Clamp(y, Margin, Math.Max(Margin, canvasHeight - Margin - drawnHeight));
        return new Block(new Rect(x, y, width, drawnHeight), scale, row.Offsets);
    }

    /// <summary>Packs items left to right into rows no wider than <paramref name="maxWidth"/>, top-aligned in each row.</summary>
    public static Grid Pack(IReadOnlyList<Size> items, float maxWidth, float gap)
    {
        var offsets = new Point[items.Count];
        float x = 0f, y = 0f, rowWidth = 0f, rowHeight = 0f, width = 0f;
        bool empty = true;
        for (int i = 0; i < items.Count; i++)
        {
            Size item = items[i];
            if (!empty && x + item.Width > maxWidth)
            {
                y += rowHeight + gap;
                x = 0f;
                rowWidth = 0f;
                rowHeight = 0f;
            }
            offsets[i] = new Point(x, y);
            x += item.Width + gap;
            rowWidth = Math.Max(rowWidth, x - gap);
            rowHeight = Math.Max(rowHeight, item.Height);
            width = Math.Max(width, rowWidth);
            empty = false;
        }
        float height = empty ? 0f : y + rowHeight;
        return new Grid(new Size(width, height), offsets);
    }

    /// <summary>Stacks items in one column.</summary>
    public static Grid PackColumn(IReadOnlyList<Size> items, float maxWidth, float gap)
    {
        var offsets = new Point[items.Count];
        float y = 0f, width = 0f;
        for (int i = 0; i < items.Count; i++)
        {
            Size item = items[i];
            offsets[i] = new Point(0f, y);
            y += item.Height + gap;
            width = Math.Max(width, item.Width);
        }
        float height = items.Count == 0 ? 0f : y - gap;
        return new Grid(new Size(Math.Min(width, Math.Max(maxWidth, 1f)), height), offsets);
    }

    private static float FitScale(Size block, float maxWidth, float maxHeight, float preferred)
    {
        if (block.Width <= 0f || block.Height <= 0f || maxWidth <= 0f || maxHeight <= 0f) return 0f;
        return Math.Min(preferred, Math.Min(maxWidth / block.Width, maxHeight / block.Height));
    }
}
