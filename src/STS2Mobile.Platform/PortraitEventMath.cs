using System;

namespace STS2Mobile.Platform;

/// <summary>The band pinned to the bottom of a standard event for its choice rows, and the row size that reserves it.</summary>
public readonly record struct EventChoiceBand(float Top, float Bottom, float RowHeight, int Gap);

/// <summary>The translucent text slot above the choices: its top, bottom and height in canvas units.</summary>
public readonly record struct EventTextSlot(float Top, float Bottom, float Height);

/// <summary>How the subject of an event's illustration is shifted and scaled inside the art frame.</summary>
public readonly record struct EventArtCrop(float Scale, float X, float Y);

/// <summary>The panel's height after fitting, and whether the copy still needs the scroll.</summary>
public readonly record struct EventTextPanel(float Height, bool Scrolls);

/// <summary>The point of the source illustration that must stay in view, as a fraction of its width and height.</summary>
public readonly record struct EventFocus(float X, float Y);

/// <summary>
/// Pure layout math for the portrait standard event: the subject-aware crop of the native illustration, the fade into the
/// choice area, the translucent text slot with its fit-before-scroll font, and the four full-size choice rows pinned low.
/// <para>
/// Lengths are canvas units (1080 across; 2.88 units are one point on an iPhone 11 Pro). The reference template is
/// 1680 wide for the same phone (4.48 units per point), so each of its margins and sizes is converted here through points;
/// the numbers on every constant name what it came from. The illustration itself is the game's own
/// <c>default_event_layout.tscn</c> rectangle, 2560 x 1200.
/// </para>
/// </summary>
public static class PortraitEventMath
{
    /// <summary>One unit of the 1680-wide reference template, in this app's units.</summary>
    public const float TemplateToLauncher = 1080f / 1680f;

    /// <summary>The art and the slot keep this margin from each side (56 reference units are 12.5 points).</summary>
    public const float SideInset = 36f;

    /// <summary>How far above the canvas bottom the pinned choices end (the app's bottom band, 52 points).</summary>
    public const float BottomInset = 150f;

    /// <summary>Space under the four reserved rows (20 reference units).</summary>
    public const float BandReserve = 13f;

    /// <summary>Rows reserved for choices; longer pages scroll instead of shrinking the art.</summary>
    public const int Capacity = 4;

    /// <summary>A row is 53.6 points on the tall shape and 46 points on the compact one.</summary>
    public const float RowHeightTall = 154f;
    public const float RowHeightCompact = 132f;

    /// <summary>One touch target: 44 points, used only when four compact rows cannot hold their text.</summary>
    public const float RowHeightTight = 127f;

    public const int GapTall = 15;
    public const int GapCompact = 12;
    public const int GapTight = 9;

    /// <summary>The gap between the choices and the text slot (54 reference units are 12 points).</summary>
    public const float TextGap = 35f;

    /// <summary>The art stops this far above the choices (40 reference units are 9 points).</summary>
    public const float ArtBottomGap = 26f;

    /// <summary>The slot's own height as a fraction of the canvas: 18 % of it.</summary>
    public const float TextSlotFraction = 0.18f;

    /// <summary>The fade begins this fraction of the canvas above the art's bottom edge and runs to the canvas end.</summary>
    public const float FadeFraction = 0.145f;

    /// <summary>The smallest band the art may keep, so a very short screen does not collapse it.</summary>
    public const float MinArtHeight = 120f;

    /// <summary>The slot's panel insets and corner (38 x 20 reference units, 24 radius).</summary>
    public const float PanelInsetX = 24f;
    public const float PanelInsetY = 13f;
    public const int PanelCornerRadius = 15;

    /// <summary>The panel is this opaque; the art shows through it.</summary>
    public const float PanelAlpha = 0.58f;

    /// <summary>Copy is set at 13.4 points and may fall to 9.7 before the slot scrolls (60 to 44 reference units).</summary>
    public const int TextFont = 39;
    public const int MinTextFont = 28;
    public const int TextFontStep = 3;

    /// <summary>The title is set a little larger than the copy, the shared-event note a little smaller (+20/-10 reference units).</summary>
    public const int TitleExtra = 13;
    public const int SharedDelta = -7;

    /// <summary>Space between the title, the copy and the shared note, and the extra ink a label is given (12 and 6 reference units).</summary>
    public const float CopyGap = 8f;
    public const float InkGutter = 4f;

    /// <summary>Choice text insets (42 across, 24/18/16 down) and the room a scrollbar needs (52 reference units).</summary>
    public const float OptionInsetX = 27f;
    public const float OptionInsetYTall = 15f;
    public const float OptionInsetYCompact = 12f;
    public const float OptionInsetYTight = 10f;
    public const float ScrollbarGutter = 33f;

    /// <summary>The tighter rows set their text a little smaller (54 reference units are 12 points).</summary>
    public const int OptionTightFont = 35;

    /// <summary>
    /// The choices' and the copy's scrollbars: 12.5 points, wide enough to see and to aim at on the compact shape.
    /// </summary>
    public const float ScrollbarWidth = 36f;

    /// <summary>How far a touch moves before it scrolls instead of pressing a choice (28 reference units are 6.25 points).</summary>
    public const float DragThreshold = 18f;

    /// <summary>Where the subject's focal point sits inside the art frame: 39 % down, above the text slot.</summary>
    public const float ArtFocusY = 0.39f;

    /// <summary>The game's own event illustration rectangle (<c>default_event_layout.tscn</c>).</summary>
    public const float SourceWidth = 2560f;
    public const float SourceHeight = 1200f;

    /// <summary>Where most events put their subject, and the one scene that does not.</summary>
    public static readonly EventFocus DefaultFocus = new(0.30f, 0.50f);
    public static readonly EventFocus ByrdonisNestFocus = new(0.33f, 0.53f);

    /// <summary>
    /// The macaque's 9:16 shape is the compact one: the shorter canvas takes 46 point rows with 4 point gaps and a 120 point
    /// slot, which leaves the illustration 277 points instead of the 382 a taller phone gives it.
    /// </summary>
    public static bool IsCompact(float canvasWidth, float canvasHeight) =>
        canvasWidth > 0f && 9f * canvasHeight <= 16f * canvasWidth + 0.5f;

    /// <summary>
    /// The rows' band, from the four reserved rows at the chosen size up to <paramref name="visibleTop"/> at the least.
    /// <paramref name="tight"/> uses one touch target per row (44 points); otherwise rows are 53.6 or 46 points.
    /// </summary>
    public static EventChoiceBand ChoiceBand(float canvasHeight, float visibleTop, bool compact, bool tight)
    {
        float row = tight ? RowHeightTight : compact ? RowHeightCompact : RowHeightTall;
        int gap = tight ? GapTight : compact ? GapCompact : GapTall;
        float bottom = canvasHeight - BottomInset;
        float top = bottom - Capacity * row - (Capacity - 1) * gap - BandReserve;
        top = MathF.Max(top, visibleTop + MinArtHeight);
        return new EventChoiceBand(top, MathF.Max(bottom, top), row, gap);
    }

    /// <summary>The choice rows' height at this size and, if any, the taller row a long option needs.</summary>
    public static float ChoiceRowsHeight(float rowHeight, int gap, int count) =>
        count <= 0 ? 0f : count * rowHeight + (count - 1) * gap;

    /// <summary>The slot above the choices: bottom anchored <see cref="TextGap"/> above them, at most 18 % of the canvas.</summary>
    public static EventTextSlot TextSlot(float canvasHeight, float visibleTop, float choiceTop)
    {
        float bottom = choiceTop - TextGap;
        float top = MathF.Max(visibleTop, bottom - MathF.Round(canvasHeight * TextSlotFraction));
        return new EventTextSlot(top, bottom, MathF.Max(0f, bottom - top));
    }

    /// <summary>The bottom of the illustration, just above the choices.</summary>
    public static float ArtBottom(float choiceTop) => choiceTop - ArtBottomGap;

    /// <summary>Where the fade starts above the art's bottom edge.</summary>
    public static float FadeTop(float artBottom, float canvasHeight) => artBottom - canvasHeight * FadeFraction;

    /// <summary>
    /// Cover crop of the source illustration into a frame, keeping <paramref name="focus"/> at the frame's centre
    /// horizontally and <see cref="ArtFocusY"/> down it. The scale covers the frame on both axes, and the position keeps
    /// the frame filled (never shows past the source).
    /// </summary>
    public static EventArtCrop ArtCrop(float frameWidth, float frameHeight, float sourceWidth, float sourceHeight, EventFocus focus)
    {
        if (frameWidth <= 0f || frameHeight <= 0f || sourceWidth <= 0f || sourceHeight <= 0f) return new EventArtCrop(1f, 0f, 0f);
        float targetY = frameHeight * ArtFocusY;
        float focusY = Math.Clamp(focus.Y, 0.01f, 0.99f);
        float focusX = Math.Clamp(focus.X, 0f, 1f);
        float scale = MathF.Max(
            frameWidth / sourceWidth,
            MathF.Max(targetY / focusY, (frameHeight - targetY) / (1f - focusY)) / sourceHeight);
        float width = sourceWidth * scale;
        float height = sourceHeight * scale;
        float x = Math.Clamp(frameWidth * 0.5f - width * focusX, MathF.Min(0f, frameWidth - width), 0f);
        float y = targetY - height * focusY;
        return new EventArtCrop(scale, x, y);
    }

    /// <summary>The focus point of a portrait texture, by the name of its scene file.</summary>
    public static EventFocus ArtFocus(string? textureFileName) =>
        string.Equals(textureFileName, "byrdonis_nest", StringComparison.Ordinal) ? ByrdonisNestFocus : DefaultFocus;

    /// <summary>
    /// The panel's height for the measured copy: the copy's height when it fits the limit, otherwise the limit with the
    /// scroll taking the rest.
    /// </summary>
    public static EventTextPanel TextPanel(float required, float copyLimit)
    {
        if (copyLimit < 0f) copyLimit = 0f;
        return required > copyLimit + 0.5f
            ? new EventTextPanel(copyLimit, Scrolls: true)
            : new EventTextPanel(required, Scrolls: false);
    }

    /// <summary>The next smaller copy size, down to the readable minimum.</summary>
    public static int NextTextFont(int font) => Math.Max(MinTextFont, font - TextFontStep);

    /// <summary>
    /// The title's size (from the copy's full size, not a shrunk one), and the shared-event note's size for a copy size.
    /// </summary>
    public static int TitleFont(int font) => font + TitleExtra;
    public static int SharedFont(int font) => Math.Max(MinTextFont - 4, font + SharedDelta);

    /// <summary>A choice row's height: its text plus the insets, never under the phone-sized minimum.</summary>
    public static float OptionRowHeight(float textHeight, float insetY, float minHeight) =>
        MathF.Max(minHeight, textHeight + 2f * insetY);

    /// <summary>The tallest of the rows' required heights, for the uniform height a tall shape gives every row.</summary>
    public static float UniformRowHeight(ReadOnlySpan<float> required, float minHeight)
    {
        float tallest = minHeight;
        foreach (float height in required)
            if (height > tallest) tallest = height;
        return tallest;
    }

    /// <summary>The choice text's width inside a row.</summary>
    public static float OptionTextWidth(float width) => MathF.Max(0f, width - 2f * OptionInsetX);
}
