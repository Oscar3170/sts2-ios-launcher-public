using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace STS2Mobile.Platform;

/// <summary>
/// Where an ancient's painting is cropped in portrait, in launcher canvas units: the clip that holds it, the camera row the
/// crop ends on, the uniform scale and horizontal offset that put the ancient's subject in the middle, and the band over
/// which the art fades into the act's room cover below. <see cref="OptionTop"/> is where the reward rows are expected to
/// start (the native column ends them 10 units above its clipped box's bottom).
/// </summary>
public readonly record struct AncientArtFrame(
    float ClipTop, float ClipBottom, float OptionTop, float CameraBottom, float Scale, float OffsetX, float FadeStart, float FadeEnd);

/// <summary>
/// One ancient reward row in portrait, in launcher canvas units: a 62.5 point row with a 37.5 point relic icon and the
/// 13.4 point text the desktop rows get on a phone, its insets, the gap after the icon and the spacing between rows.
/// </summary>
public readonly record struct AncientOptionMetrics(
    float RowHeight, float IconSize, int FontSize, float InsetX, float InsetY, float IconGap, float Separation, float BottomGutter)
{
    /// <summary>A row is at least the minimum height, and grows to hold text that needs more than that.</summary>
    public float HeightFor(float textHeight) => MathF.Max(RowHeight, textHeight + 2f * InsetY);

    /// <summary>The width the text label gets in a row of <paramref name="rowWidth"/>, after the insets and a shown relic icon.</summary>
    public float TextWidth(float rowWidth, bool icon) =>
        rowWidth - 2f * InsetX - (icon ? IconSize + IconGap : 0f);

    /// <summary>The options column's height: every row, the gaps between them and the room the last row's frame overhangs into.</summary>
    public float TotalHeight(IReadOnlyList<float> rowHeights)
    {
        if (rowHeights.Count == 0) return 0f;
        float total = BottomGutter;
        for (int i = 0; i < rowHeights.Count; i++) total += rowHeights[i];
        return total + (rowHeights.Count - 1) * Separation;
    }
}

/// <summary>
/// Pure layout math for an Ancient event in portrait (Neow, Darv, Pael, Tanx, Vakuu, Nonuepeipe and the rest): the painting
/// cropped around each ancient's authored subject and bled into the room cover, the dialogue column over that art, the
/// enlarged nameplate, and the finger-sized reward rows. Sizes are written in points and converted to the 1080 unit canvas
/// (<see cref="UnitsPerPoint"/>), so the same design holds on a 9:19.5 and a 9:16 phone.
/// </summary>
public static class PortraitAncientMath
{
    /// <summary>Canvas units to a point: the launcher's canvas is 1080 units across a 375 point wide phone.</summary>
    public const float UnitsPerPoint = 1080f / 375f;

    /// <summary>The authored size of every ancient background painting.</summary>
    public const float ArtSourceWidth = 1920f, ArtSourceHeight = 1080f;

    /// <summary>Gap between the run's header (top bar, potion row and relic strip) and the art.</summary>
    public const float ArtTopGap = 30f;

    /// <summary>Top of the dialogue column, below the art's top edge.</summary>
    public const float ContentTopGap = 16f * UnitsPerPoint;

    /// <summary>Side inset of the clipped dialogue-and-options box.</summary>
    public const float ContainerInset = 5.4f * UnitsPerPoint;

    /// <summary>The dialogue column's side inset inside that box (room for the rows' frames to overhang).</summary>
    public const float ContainerGutter = 8.9f * UnitsPerPoint;

    /// <summary>Side inset of the reward rows.</summary>
    public const float ContentSideInset = 14.3f * UnitsPerPoint;

    /// <summary>While the dialogue runs its column's bottom sits this far above the art's cropped bottom.</summary>
    public const float DialogueBottomGap = 34.6f * UnitsPerPoint;

    /// <summary>Separation between the dialogue stack and the reward rows.</summary>
    public const float ContentSeparation = 6.25f * UnitsPerPoint;

    /// <summary>The native column ends the reward rows 10 units above its clipped box's bottom.</summary>
    public const float NativeRewardDrop = 10f;

    /// <summary>The nameplate's footer: the home indicator's 31.25 points plus its 4 point inset.</summary>
    public const float NameplateBottom = 35.25f * UnitsPerPoint;

    /// <summary>
    /// Room kept for the nameplate once it settles: the native title is 54 units (18.75 points) tall and enlarges by 1.32,
    /// with the epithet under it. 43 points leaves the title clear of the reward rows.
    /// </summary>
    public const float NameplateReserve = 43f * UnitsPerPoint;

    /// <summary>How much the settled epithet is enlarged; its pivot is its bottom-left corner.</summary>
    public const float EpithetScale = 1.32f;

    /// <summary>Where the art's camera row sits before the reward rows are considered.</summary>
    public const float CameraFraction = 0.685f;

    /// <summary>The lowest the camera row may go when the reward rows are close to the top.</summary>
    public const float CameraMaxFraction = 0.76f;

    /// <summary>The native paintings bleed this many source pixels below their authored 1080.</summary>
    public const float BleedSource = 84f;

    /// <summary>Clear space between the art and the reward rows on a tall phone.</summary>
    public const float TallRewardGap = 2.7f * UnitsPerPoint;

    /// <summary>How far past the reward rows' top the art reaches on a 9:16 phone.</summary>
    public const float CompactRewardReach = 4f * UnitsPerPoint;

    /// <summary>Distance from the art's bottom to the point where the fade is fully room cover.</summary>
    public const float FadeEdge = 10.7f * UnitsPerPoint;

    /// <summary>Length of the fade, capped by a fraction of the crop.</summary>
    public const float FadeLength = 96f * UnitsPerPoint;
    public const float FadeFraction = 0.32f;

    /// <summary>Reserve for the last dialogue line when the art is framed.</summary>
    public const float DialogueLineReserve = 27.9f * UnitsPerPoint;

    /// <summary>13.4 points: the text of the ancient rows and their dialogue bubbles.</summary>
    public const int FontSize = 39;

    /// <summary>The native dialogue bubble, its container and its label are 800, 672 and 634 units wide.</summary>
    public const float DialogueLineBase = 800f, DialogueContainerBase = 672f, DialogueTextBase = 634f;

    /// <summary>
    /// The crop's horizontal centre in the 1920 wide painting, by the authored scene name on the background's first child.
    /// The scenes are not all centred on their subject; these are the measured centres.
    /// </summary>
    public static float FocusX(string? sceneName) => sceneName switch
    {
        "Neow" => 995f,
        "Darv" => 885f,
        "Pael" => 1050f,
        "Tanx" => 895f,
        "Vakuu" => 780f,
        "Nonuepeipe" => 1010f,
        _ => 960f,
    };

    /// <summary>Top of the art: just under the run's header.</summary>
    public static float ArtTop(float headerBottom) => headerBottom + ArtTopGap;

    /// <summary>Top of the nameplate's room at the screen's bottom; the reward rows end above it.</summary>
    public static float NameplateTop(float canvasHeight) => canvasHeight - NameplateBottom - NameplateReserve;

    /// <summary>The settled nameplate's footer line.</summary>
    public static float NameplateFooter(float canvasHeight) => canvasHeight - NameplateBottom;

    /// <summary>
    /// The settled epithet's local y inside the banner: its bottom-left pivot (0, its own height) is its footer, so scaling
    /// it leaves the text where it is put.
    /// </summary>
    public static float EpithetLocalY(float canvasHeight, float bannerGlobalY, float epithetHeight) =>
        NameplateFooter(canvasHeight) - bannerGlobalY - epithetHeight;

    /// <summary>The reward rows: 62.5 point rows, 37.5 point icons and 13.4 point text.</summary>
    public static AncientOptionMetrics Options { get; } = new(
        RowHeight: 62.5f * UnitsPerPoint,
        IconSize: 37.5f * UnitsPerPoint,
        FontSize: FontSize,
        InsetX: 6.7f * UnitsPerPoint,
        InsetY: 4f * UnitsPerPoint,
        IconGap: 4f * UnitsPerPoint,
        Separation: 5.4f * UnitsPerPoint,
        BottomGutter: 5.4f * UnitsPerPoint);

    /// <summary>
    /// The art crop for a portrait canvas. The camera row starts at 68.5 % of the screen and is pulled up so the last
    /// dialogue line and the reward rows stay on the art; a tall phone keeps a gap between art and rows, a 9:16 phone lets
    /// the art reach the rows and extends the camera into the painting's bleed instead. The scale covers the canvas width
    /// and reaches the camera row, and the horizontal offset centres <paramref name="focusX"/> under the canvas's middle.
    /// </summary>
    public static AncientArtFrame FrameArt(
        float canvasWidth, float canvasHeight, float artTop, float nameplateTop, float optionsHeight, float lineReserve,
        bool compact, float focusX)
    {
        float optionTop = nameplateTop - NativeRewardDrop - optionsHeight;
        float cameraBottom = Math.Clamp(optionTop - lineReserve - TallRewardGap, artTop, canvasHeight * CameraMaxFraction);
        float artBottom;
        if (compact)
        {
            artBottom = optionTop + CompactRewardReach;
            // The painting's own bleed covers the extra height without changing the crop's focus.
            cameraBottom = MathF.Max(cameraBottom, artTop + (artBottom - artTop) * ArtSourceHeight / (ArtSourceHeight + BleedSource));
        }
        else
        {
            float bleed = BleedSource * (cameraBottom - artTop) / ArtSourceHeight;
            artBottom = MathF.Max(cameraBottom, MathF.Min(optionTop - TallRewardGap, cameraBottom + bleed));
        }
        float scale = MathF.Max(canvasWidth / ArtSourceWidth, (cameraBottom - artTop) / ArtSourceHeight);
        float offsetX = Math.Clamp(canvasWidth * 0.5f - focusX * scale, canvasWidth - ArtSourceWidth * scale, 0f);
        float fadeEnd = artBottom - FadeEdge;
        float fadeStart = fadeEnd - MathF.Min(FadeLength, (artBottom - artTop) * FadeFraction);
        return new AncientArtFrame(artTop, artBottom, optionTop, cameraBottom, scale, offsetX, fadeStart, fadeEnd);
    }

    /// <summary>
    /// The size of the inline energy icons in a label of this font: the native bare <c>[img]</c> tags are rewritten so the
    /// icons keep their 0.85 font ratio when the text is enlarged.
    /// </summary>
    public static int EnergyIconSize(int fontSize) => (int)MathF.Round(fontSize * 0.85f);

    // The native energy formatter emits bare [img] tags around its square sprite-font icons; authored illustrations and
    // other BBCode are left alone. Re-running it replaces the previous dimensions instead of compounding them.
    private static readonly Regex EnergyIconTag = new(
        @"\[img(?:=\d+x\d+)?\](res://images/packed/sprite_fonts/[a-z_]+_energy_icon\.png)\[/img\]",
        RegexOptions.CultureInvariant);

    /// <summary>The dialogue/option text with its inline energy icons sized for <paramref name="fontSize"/>.</summary>
    public static string RewriteEnergyIcons(string text, int fontSize)
    {
        int size = EnergyIconSize(fontSize);
        return EnergyIconTag.Replace(text, "[img=" + size + "x" + size + "]$1[/img]");
    }

    /// <summary>The dialogue line's own width in portrait: the column's width less the speaker icon (56 units).</summary>
    public static float DialogueLineWidth(float contentWidth) => contentWidth - 56f;

    /// <summary>How much wider the native 800 unit bubble and its parts become; all three grow by the same amount.</summary>
    public static float DialogueWiden(float contentWidth) => MathF.Max(0f, DialogueLineWidth(contentWidth) - DialogueLineBase);
}
