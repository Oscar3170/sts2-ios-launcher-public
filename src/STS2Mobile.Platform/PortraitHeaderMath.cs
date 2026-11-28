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
/// that scrolls beside the map and deck buttons, the relic band under it and the potion popup that opens under its slot.
/// The band heights themselves live in the launcher's <c>PortraitBands</c>; they are passed in so this stays a plain
/// calculator. All numbers are canvas units (1080 across, 2.88 to a point on an iPhone 11 Pro).
/// </summary>
public static class PortraitHeaderMath
{
    /// <summary>
    /// The portrait header's nine-patch margins for the game's own top-bar stone texture. They are texture pixels, so the
    /// NinePatchRect draws the torn edges and the side seams at their own size and stretches only the middle for a header of
    /// any height; a texture smaller than twice a margin is clamped so a middle still exists.
    /// </summary>
    public const int StonePatchLeft = 30, StonePatchTop = 20, StonePatchRight = 30, StonePatchBottom = 30;

    /// <summary>
    /// The safe cap a desktop/unknown host reserves above the top bar: the launcher's old fixed band, kept only where the
    /// native query that reports the real inset is missing.
    /// </summary>
    public const float DefaultCapHeight = 162f;

    /// <summary>
    /// The safe cap over the top bar in canvas units. With the native query (<paramref name="hasTopInset"/>) the cap follows
    /// the screen's real inset: <c>ceil(insetFraction * canvasHeight)</c>, and 0 is a valid answer (a 9:16 phone with no notch
    /// reserves nothing). Without the query, a desktop host keeps <see cref="DefaultCapHeight"/>. There is no floor: a floor
    /// would reserve a notch that the screen does not have.
    /// </summary>
    public static float CapHeight(bool hasTopInset, double insetFraction, float canvasHeight)
    {
        if (!hasTopInset) return DefaultCapHeight;
        if (!(insetFraction > 0) || !float.IsFinite(canvasHeight) || canvasHeight <= 0f) return 0f;
        return MathF.Ceiling((float)(insetFraction * canvasHeight));
    }

    /// <summary>
    /// How far the potion row starts below the top row's band: the ascension badge stands on the band's bottom and its
    /// number's ink reaches 3 units past it, so the tray starts clear of both.
    /// </summary>
    public const float PotionRowDrop = 4f;

    /// <summary>
    /// How far the potion row ends above the header's bottom. The stone's last 20 units are its torn edge (the dark rim and
    /// the tear the stage shows through), not stone; ending the row 17 units above the bottom keeps the tray's dark box (the
    /// slot plus 4 units, 2 below the row's top) 4 units above the torn edge, on the stone.
    /// </summary>
    public const float PotionRowLift = 17f;

    /// <summary>
    /// The potion row, left of the map and deck buttons in the second header row: from just under the top row to just above
    /// the stone's torn edge, so it is shorter than the band. What potions do not use shows the stone behind the tray.
    /// </summary>
    public static HeaderBox PotionRow(float canvasWidth, float capHeight, float topBarRowHeight, float potionRowHeight,
        float mapDeckWidth, float margin, float gap) =>
        new(margin, capHeight + topBarRowHeight + PotionRowDrop,
            Math.Max(0f, canvasWidth - 2f * margin - gap - mapDeckWidth),
            Math.Max(0f, potionRowHeight - PotionRowDrop - PotionRowLift));

    /// <summary>The map and deck buttons, right of the potion row and centred on the second row's band.</summary>
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
            Math.Min(StonePatchLeft, horizontal), Math.Min(StonePatchTop, vertical),
            Math.Min(StonePatchRight, horizontal), Math.Min(StonePatchBottom, vertical));
    }

    /// <summary>
    /// How far the top row's right edge is pulled in for the modifier button: its 44 point touch area grows to the right of
    /// the 48 unit picture (never into the boss icon), so the whole extra width plus <paramref name="gap"/> is reserved before
    /// the settings button.
    /// </summary>
    public static float ModifierClusterReserve(float buttonWidth, float touchTarget, float gap) =>
        Math.Max(0f, touchTarget - buttonWidth) + gap;

    /// <summary>
    /// A potion's popup where the game's own <c>NPotionPopup._Ready</c> puts it: its top left corner is the holder's top left
    /// plus half the holder's width less half the popup's drawn width, and one and a half holder heights down, which leaves
    /// half a holder between the slot and the popup. It is centred on the holder, inside the safe frame, drawn as large as
    /// the Use and Discard buttons need (<paramref name="buttonScale"/>, capped at <paramref name="maxScale"/>) but never
    /// larger than the screen allows. It may cover the relic band below the header; portrait does not lift it past the band.
    /// </summary>
    public static PopupPlacement PotionPopup(float safeLeft, float safeTop, float safeRight, float safeBottom,
        float anchorX, float anchorTop, float popupWidth, float popupHeight, float buttonScale, float maxScale)
    {
        if (popupWidth <= 0f || popupHeight <= 0f || safeRight - safeLeft <= 1f) return new PopupPlacement(0f, anchorX, safeTop);
        float y = Math.Max(safeTop, anchorTop);
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
