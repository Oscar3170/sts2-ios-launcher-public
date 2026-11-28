using System;

namespace STS2Mobile.Platform;

/// <summary>The main menu's button column in portrait: its top and height, and the row size and gap inside it.</summary>
public readonly record struct MainMenuColumn(float Top, float Height, float ButtonHeight, float Separation)
{
    public float Bottom => Top + Height;
}

/// <summary>
/// Where the registered menu master is drawn on the canvas: the scale it is drawn at and the canvas x of its left edge. The
/// camera fills the canvas height exactly, so the master's top sits on the canvas's top edge.
/// </summary>
public readonly record struct MainMenuFraming(float Scale, float Left);

/// <summary>A rectangle in portrait canvas units, measured from the canvas's top left corner.</summary>
public readonly record struct MainMenuRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
}

/// <summary>Where the game's animated title goes: its top left corner and width in canvas units.</summary>
public readonly record struct MainMenuLogoPlacement(float Left, float Top, float Width);

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

    /// <summary>The profile row keeps its desktop shape; its name and description are drawn at 11 points so they read.</summary>
    public const int ProfileFontSize = 32;
    public const float ProfilePadding = 9f;

    /// <summary>
    /// The registered main-menu master: the live 2560 x 1200 background stage at (157, 23) on a 2304 square, drawn at a camera
    /// centred on the tower at x = 1451. The generated water is the master's rows from y = 1038 to its bottom; the tower's left
    /// silhouette through the title band is at x = 1678, which the title must not cross.
    /// </summary>
    public const float BackdropMasterSize = 2304f;
    public const float BackdropNativeLeft = 157f;
    public const float BackdropNativeTop = 23f;
    public const float BackdropNativeWidth = 2560f;
    public const float BackdropNativeHeight = 1200f;
    public const float BackdropCameraCentreX = 1451f;
    public const float BackdropWaterTop = 1038f;
    public const float BackdropTowerLeft = 1678f;
    /// <summary>The generated water texture's own size: the master's width, and its rows from the water's top to its bottom.</summary>
    public const float WaterTextureWidth = BackdropMasterSize;
    public const float WaterTextureHeight = BackdropMasterSize - BackdropWaterTop;

    /// <summary>
    /// The title's place on the phone reference (390 x 924 points): 16 units in from the left, 126 down from the top, 259 wide,
    /// with a 10 unit gap to the tower. On a wider crop the tower is further in, so the title keeps its width up to that gap.
    /// </summary>
    public const float LogoReferenceWidth = 390f;
    public const float LogoReferenceHeight = 924f;
    public const float LogoLeft = 16f;
    public const float LogoTop = 126f;
    public const float LogoWidth = 259f;
    public const float LogoTowerGap = 10f;

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

    /// <summary>
    /// The registered master's camera for a canvas: it fills the canvas height, shows as much width as the canvas asks for (up
    /// to the whole master) and keeps the tower at the middle, clamped so the master's own edges never show.
    /// </summary>
    public static MainMenuFraming Backdrop(float canvasWidth, float canvasHeight)
    {
        float height = Math.Max(1f, canvasHeight);
        float scale = height / BackdropMasterSize;
        float visible = Math.Min(Math.Max(1f, canvasWidth) / scale, BackdropMasterSize);
        float left = Math.Clamp(BackdropCameraCentreX - visible * 0.5f, 0f, BackdropMasterSize - visible);
        return new MainMenuFraming(scale, -left * scale);
    }

    /// <summary>Where the live background stage lands: its unscaled 2560 x 1200 box positioned and scaled by the camera.</summary>
    public static MainMenuRect BackdropNativeStage(MainMenuFraming framing) => new(
        framing.Left + BackdropNativeLeft * framing.Scale,
        BackdropNativeTop * framing.Scale,
        BackdropNativeWidth * framing.Scale,
        BackdropNativeHeight * framing.Scale);

    /// <summary>The generated water's rect: the master's full width, from the water's top row down to the canvas's bottom.</summary>
    public static MainMenuRect BackdropWater(MainMenuFraming framing) => new(
        framing.Left,
        BackdropWaterTop * framing.Scale,
        WaterTextureWidth * framing.Scale,
        WaterTextureHeight * framing.Scale);

    /// <summary>
    /// The animated title on the phone reference: in from the left, down from the top (or below the safe cap, whichever is
    /// lower) and as wide as the reference, narrowed when the framed tower's silhouette is closer than the reference's.
    /// </summary>
    public static MainMenuLogoPlacement BackdropLogo(float canvasWidth, float canvasHeight, float capHeight, MainMenuFraming framing)
    {
        float width = Math.Max(1f, canvasWidth);
        float left = width * LogoLeft / LogoReferenceWidth;
        float top = Math.Max(Math.Max(1f, canvasHeight) * LogoTop / LogoReferenceHeight, capHeight + left);
        float preferred = width * LogoWidth / LogoReferenceWidth;
        float towerLeft = framing.Left + BackdropTowerLeft * framing.Scale;
        float clear = towerLeft - width * LogoTowerGap / LogoReferenceWidth - left;
        return new MainMenuLogoPlacement(left, top, Math.Clamp(clear, 1f, Math.Max(1f, preferred)));
    }

    /// <summary>The outline a label's text needs at <paramref name="fontSize"/>, scaled from the size it is drawn at now.</summary>
    public static int Outline(int currentOutline, int currentFontSize, int fontSize) =>
        Math.Max(1, (int)MathF.Round(currentOutline * (float)fontSize / Math.Max(1, currentFontSize)));

    /// <summary>The continue button's run details: the reference scale, clamped so they never reach past the canvas's sides.</summary>
    public static float ContinueInfoScale(float nativeWidth, float canvasWidth) =>
        Math.Min(ContinueScale, Math.Max(0f, canvasWidth - 2f * ContinueSideMargin) / Math.Max(1f, nativeWidth));
}
