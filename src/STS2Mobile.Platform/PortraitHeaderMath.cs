using System;

namespace STS2Mobile.Platform;

/// <summary>A rectangle in portrait canvas units (1080 across), measured from the canvas's top left corner.</summary>
public readonly record struct HeaderBox(float Left, float Top, float Width, float Height)
{
    public float Right => Left + Width;
    public float Bottom => Top + Height;
    public bool HasArea => Width > 0f && Height > 0f;
}

/// <summary>A nine-patch's patch margins, in the stone texture's own pixels.</summary>
public readonly record struct StonePatchMargins(int Left, int Top, int Right, int Bottom);

/// <summary>Where a potion's popup goes: the scale it is drawn at and the top left corner (canvas units) it takes.</summary>
public readonly record struct PopupPlacement(float Scale, float X, float Y)
{
    public float DrawnWidth(float popupWidth) => popupWidth * Scale;
    public float DrawnHeight(float popupHeight) => popupHeight * Scale;
    public bool Visible => Scale > 0f;
}

/// <summary>
/// Pure geometry for the portrait run header: the cracked-stone panel over the safe cap and the top bar, the potion row
/// that scrolls beside the map and deck buttons, the relic band under it and the potion popup that opens below both.
/// The band heights themselves live in the launcher's <c>PortraitBands</c>; they are passed in so this stays a plain
/// calculator. All numbers are canvas units (1080 across, 2.88 to a point on an iPhone 11 Pro).
/// </summary>
public static class PortraitHeaderMath
{
    /// <summary>
    /// TouchSpireUI's nine-patch margins for the game's own top-bar stone texture. They are texture pixels, so the
    /// NinePatchRect draws the torn edges and the side seams at their own size and stretches only the middle for a header of
    /// any height; a texture smaller than twice a margin is clamped so a middle still exists.
    /// </summary>
    public const int TusiPatchLeft = 30, TusiPatchTop = 20, TusiPatchRight = 30, TusiPatchBottom = 30;

    /// <summary>
    /// The potion row, left of the map and deck buttons in the second header row. Its height is the row's own; what potions
    /// do not use shows the stone behind the tray.
    /// </summary>
    public static HeaderBox PotionRow(float canvasWidth, float capHeight, float topBarRowHeight, float potionRowHeight,
        float mapDeckWidth, float margin, float gap) =>
        new(margin, capHeight + topBarRowHeight,
            Math.Max(0f, canvasWidth - 2f * margin - gap - mapDeckWidth), Math.Max(0f, potionRowHeight));

    /// <summary>The map and deck buttons, right of the potion row and centred on it.</summary>
    public static HeaderBox MapDeckRow(float canvasWidth, float capHeight, float topBarRowHeight, float potionRowHeight,
        float mapDeckWidth, float buttonHeight, float margin) =>
        new(canvasWidth - margin - mapDeckWidth,
            capHeight + topBarRowHeight + (potionRowHeight - buttonHeight) / 2f,
            Math.Max(0f, mapDeckWidth), Math.Max(0f, buttonHeight));

    /// <summary>The relic band's scroll clip: fixed under the stone, inside the band the stage was tuned around.</summary>
    public static HeaderBox RelicClip(float canvasWidth, float capHeight, float topBarHeight, float bandHeight,
        float sideMargin, float topGap) =>
        new(sideMargin, capHeight + topBarHeight + topGap,
            Math.Max(0f, canvasWidth - 2f * sideMargin), Math.Max(0f, bandHeight - topGap));

    /// <summary>How many relic holders of <paramref name="holder"/> units fit in a line across <paramref name="width"/>.</summary>
    public static int RelicColumns(float width, float holder, float separation)
    {
        if (holder <= 0f || width <= 0f) return 1;
        return Math.Max(1, (int)MathF.Floor((width + separation) / (holder + separation)));
    }

    /// <summary>The height of <paramref name="count"/> relic holders wrapped into lines across <paramref name="width"/>.</summary>
    public static float RelicContentHeight(int count, float width, float holder, float separation)
    {
        if (count <= 0) return 0f;
        int columns = RelicColumns(width, holder, separation);
        int lines = (count + columns - 1) / columns;
        return lines * holder + (lines - 1) * Math.Max(0f, separation);
    }

    /// <summary>
    /// The tray's width for <paramref name="slots"/> potion holders: the tray margin on either side plus the holders and
    /// their separation. The tray scrolls as one piece, so its dark box stays behind every slot.
    /// </summary>
    public static float PotionStripWidth(int slots, float holder, float separation, float insetLeft, float insetRight)
    {
        if (slots <= 0) return insetLeft + insetRight;
        return insetLeft + insetRight + slots * holder + (slots - 1) * Math.Max(0f, separation);
    }

    /// <summary>The stone's patch margins for a texture of this size, clamped so the middle still has a source region.</summary>
    public static StonePatchMargins StonePatchMargins(int textureWidth, int textureHeight)
    {
        int horizontal = Math.Max(0, textureWidth / 2 - 1);
        int vertical = Math.Max(0, textureHeight / 2 - 1);
        return new StonePatchMargins(
            Math.Min(TusiPatchLeft, horizontal), Math.Min(TusiPatchTop, vertical),
            Math.Min(TusiPatchRight, horizontal), Math.Min(TusiPatchBottom, vertical));
    }

    /// <summary>
    /// A potion's popup: below the relic band (<paramref name="inventoryBottom"/>), centred on the visible part of its holder
    /// (<paramref name="anchorX"/>), inside the safe frame, drawn as large as the Use and Discard buttons need
    /// (<paramref name="buttonScale"/>, capped at <paramref name="maxScale"/>) but never larger than the room below the band.
    /// </summary>
    public static PopupPlacement PotionPopup(float safeLeft, float safeTop, float safeRight, float safeBottom,
        float anchorX, float inventoryBottom, float popupWidth, float popupHeight, float buttonScale, float maxScale,
        float gap)
    {
        if (popupWidth <= 0f || popupHeight <= 0f || safeRight - safeLeft <= 1f) return new PopupPlacement(0f, anchorX, safeTop);
        float y = Math.Max(safeTop, inventoryBottom + gap);
        float room = safeBottom - y;
        if (room <= 1f) return new PopupPlacement(0f, anchorX, y);
        float scale = Math.Min(Math.Clamp(buttonScale, 1f, Math.Max(1f, maxScale)),
            Math.Min((safeRight - safeLeft) / popupWidth, room / popupHeight));
        if (scale <= 0.01f) return new PopupPlacement(0f, anchorX, y);
        float width = popupWidth * scale;
        float x = Math.Clamp(anchorX - width / 2f, safeLeft, Math.Max(safeLeft, safeRight - width));
        return new PopupPlacement(scale, x, y);
    }
}
