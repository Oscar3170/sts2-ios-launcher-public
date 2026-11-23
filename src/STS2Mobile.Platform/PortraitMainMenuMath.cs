using System;

namespace STS2Mobile.Platform;

/// <summary>The main menu's button column in portrait: its top and height, and the row size and gap inside it.</summary>
public readonly record struct MainMenuColumn(float Top, float Height, float ButtonHeight, float Separation)
{
    public float Bottom => Top + Height;
}

/// <summary>
/// Pure geometry for the portrait main menu on the 1080-wide canvas (2.88 units to a point on an iPhone 11 Pro): the column of
/// text buttons, the continue button's run details and the game's text outline at the portrait font size. Numbers taken from
/// the reference phone layout's 1680-wide canvas are converted by 1080/1680, as with the other portrait layouts; the game's own
/// band for the column stays the launcher's.
/// </summary>
public static class PortraitMainMenuMath
{
    /// <summary>The game's menu text button: a 32 unit label with an 18 unit outline on the 1680-wide canvas.</summary>
    public const int NativeFontSize = 32;
    public const int NativeOutlineSize = 18;

    /// <summary>The reference column's 1120 x 196 rows and 24 gap become 720 x 126 and 15; the row is raised to 127 for 44 points.</summary>
    public const float ButtonWidth = 720f;
    public const float ButtonHeight = 127f;
    public const float Separation = 15f;

    /// <summary>The label's 77 units become 50, and its outline grows with it instead of staying hairline thin.</summary>
    public const int FontSize = 50;

    /// <summary>The continue button's run details at the reference's 2.5x become 1.607 on the 1080-wide canvas.</summary>
    public const float ContinueScale = 1.607f;
    public const float ContinueSideMargin = 41f;
    public const float ContinueGap = 40f;

    /// <summary>The utility row under the safe cap, and the mod status along the canvas's bottom edge.</summary>
    public const float UtilityInset = 18f;
    public const float UtilityTopGap = 13f;
    public const float ReleaseWidth = 136f;
    public const float UtilityGap = 8f;
    /// <summary>The buttons stay above the home indicator's band; the mod status's own box sits at the very bottom.</summary>
    public const float ColumnBottomMargin = 98f;
    public const float ModStatusBottom = 20f;

    /// <summary>Fine print: the version label and the mod status at 8 points, readable but not another menu row.</summary>
    public const int ReleaseFontSize = 24;
    public const int ModStatusFontSize = 24;

    /// <summary>The profile row is drawn up to 1.8 times its desktop size, as the reference's 2.8 does on 1680.</summary>
    public const float ProfileContentMaxScale = 1.8f;
    public const float ProfilePadding = 9f;
    public const float ProfileMaxWidthFraction = 0.49f;

    /// <summary>
    /// The column for <paramref name="count"/> visible rows. The reference starts it at 48% of the screen; the launcher keeps
    /// the game's own band from a little above the middle to near the city at the bottom, centring the rows in it. The column
    /// never reaches below the home indicator's band, and never rises past the logo and the safe cap.
    /// </summary>
    public static MainMenuColumn Column(int count, float canvasHeight, float capHeight)
    {
        int rows = Math.Max(1, count);
        float height = rows * ButtonHeight + (rows - 1) * Separation;
        float bandTop = canvasHeight * 0.5f - 150f;
        float bandBottom = canvasHeight * 0.5f + 850f;
        float top = bandTop + (bandBottom - bandTop - height) * 0.5f;
        top = Math.Min(top, canvasHeight - ColumnBottomMargin - height);
        top = Math.Max(top, capHeight + 200f);
        return new MainMenuColumn(top, height, ButtonHeight, Separation);
    }

    /// <summary>The outline a label's text needs at <paramref name="fontSize"/>, scaled from the size it is drawn at now.</summary>
    public static int Outline(int currentOutline, int currentFontSize, int fontSize) =>
        Math.Max(1, (int)MathF.Round(currentOutline * (float)fontSize / Math.Max(1, currentFontSize)));

    /// <summary>The continue button's run details: the reference scale, clamped so they never reach past the canvas's sides.</summary>
    public static float ContinueInfoScale(float nativeWidth, float canvasWidth) =>
        Math.Min(ContinueScale, Math.Max(0f, canvasWidth - 2f * ContinueSideMargin) / Math.Max(1f, nativeWidth));

    /// <summary>
    /// The scale the profile row is drawn at: its desktop size times as much as the 44 point button allows, up to the
    /// reference's 1.8, so the profile name and description stay readable next to the icon and its hotkey badge.
    /// </summary>
    public static float ProfileScale(float buttonHeight, float contentWidth, float contentHeight, float canvasWidth)
    {
        float room = Math.Max(1f, buttonHeight - 2f * ProfilePadding);
        float width = Math.Max(1f, canvasWidth * ProfileMaxWidthFraction - 2f * ProfilePadding);
        return Math.Min(ProfileContentMaxScale,
            Math.Min(room / Math.Max(1f, contentHeight), width / Math.Max(1f, contentWidth)));
    }
}
