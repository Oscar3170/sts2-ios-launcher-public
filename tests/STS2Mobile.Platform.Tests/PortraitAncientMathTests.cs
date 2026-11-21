using System;
using System.Linq;
using STS2Mobile.Platform;
using Xunit;

namespace STS2Mobile.Platform.Tests;

public class PortraitAncientMathTests
{
    private const float TallCanvas = 2340f;
    private const float CompactCanvas = 1920f;
    private const float ArtTop = 420f;

    private static float Rows(int count) =>
        PortraitAncientMath.Options.TotalHeight(Enumerable.Repeat(PortraitAncientMath.Options.RowHeight, count).ToArray());

    [Fact]
    public void FocusSitsOnEachAncientsAuthoredSubject()
    {
        Assert.Equal(995f, PortraitAncientMath.FocusX("Neow"));
        Assert.Equal(885f, PortraitAncientMath.FocusX("Darv"));
        Assert.Equal(1050f, PortraitAncientMath.FocusX("Pael"));
        Assert.Equal(895f, PortraitAncientMath.FocusX("Tanx"));
        Assert.Equal(780f, PortraitAncientMath.FocusX("Vakuu"));
        Assert.Equal(1010f, PortraitAncientMath.FocusX("Nonuepeipe"));
        Assert.Equal(960f, PortraitAncientMath.FocusX("Tezcatara"));
        Assert.Equal(960f, PortraitAncientMath.FocusX(null));
    }

    [Fact]
    public void TallArtStopsAboveTheRewardRowsAndCoversTheCanvasWidth()
    {
        float nameplateTop = PortraitAncientMath.NameplateTop(TallCanvas);
        AncientArtFrame frame = PortraitAncientMath.FrameArt(
            canvasWidth: 1080f, canvasHeight: TallCanvas, artTop: ArtTop, nameplateTop: nameplateTop,
            optionsHeight: Rows(3), lineReserve: PortraitAncientMath.DialogueLineReserve, compact: false, focusX: 995f);

        Assert.Equal(ArtTop, frame.ClipTop, 3);
        Assert.True(frame.OptionTop > nameplateTop - 600f && frame.OptionTop < nameplateTop);
        Assert.InRange(frame.OptionTop - frame.ClipBottom, 0f, 20f);
        Assert.True(frame.ClipBottom > frame.CameraBottom);
        Assert.True(frame.Scale >= 1080f / PortraitAncientMath.ArtSourceWidth);
        // The crop covers the canvas: no bare band on either side.
        Assert.True(frame.OffsetX <= 0f);
        Assert.True(frame.OffsetX + PortraitAncientMath.ArtSourceWidth * frame.Scale >= 1080f);
        // The subject is centred unless the crop cannot be pushed that far.
        Assert.Equal(540f, frame.OffsetX + 995f * frame.Scale, 2);
        Assert.True(frame.FadeStart < frame.FadeEnd);
        Assert.Equal(frame.ClipBottom - PortraitAncientMath.FadeEdge, frame.FadeEnd, 3);
    }

    [Fact]
    public void CompactArtReachesTheRewardRowsInsteadOfStoppingAboveThem()
    {
        AncientArtFrame frame = PortraitAncientMath.FrameArt(
            canvasWidth: 1080f, canvasHeight: CompactCanvas, artTop: ArtTop,
            nameplateTop: PortraitAncientMath.NameplateTop(CompactCanvas), optionsHeight: Rows(3),
            lineReserve: PortraitAncientMath.DialogueLineReserve, compact: true, focusX: 995f);

        Assert.InRange(frame.OptionTop - frame.ClipBottom, -24f, 0f);
        Assert.True(frame.ClipBottom > CompactCanvas * 0.5f);
        Assert.True(frame.FadeEnd < frame.OptionTop);
        // The camera extends into the painting's bleed so the whole crop is covered.
        Assert.True(frame.CameraBottom > ArtTop);
        Assert.True(frame.Scale >= 1080f / PortraitAncientMath.ArtSourceWidth);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void MoreRewardRowsRaiseTheCameraAndKeepTheArtAboveThem(int count)
    {
        float nameplateTop = PortraitAncientMath.NameplateTop(TallCanvas);
        AncientArtFrame two = PortraitAncientMath.FrameArt(
            1080f, TallCanvas, ArtTop, nameplateTop, Rows(2), PortraitAncientMath.DialogueLineReserve, false, 995f);
        AncientArtFrame frame = PortraitAncientMath.FrameArt(
            1080f, TallCanvas, ArtTop, nameplateTop, Rows(count), PortraitAncientMath.DialogueLineReserve, false, 995f);

        Assert.True(frame.CameraBottom <= two.CameraBottom);
        Assert.True(frame.ClipBottom <= two.ClipBottom);
        Assert.InRange(frame.OptionTop - frame.ClipBottom, 0f, 250f);
        Assert.True(frame.OptionTop >= PortraitAncientMath.NameplateTop(TallCanvas) - Rows(4) - 10f - 1f);
    }

    [Fact]
    public void FramingIsStableForTheSameInputs()
    {
        float nameplateTop = PortraitAncientMath.NameplateTop(TallCanvas);
        AncientArtFrame first = PortraitAncientMath.FrameArt(1080f, TallCanvas, ArtTop, nameplateTop, Rows(3), 80f, false, 1050f);
        AncientArtFrame second = PortraitAncientMath.FrameArt(1080f, TallCanvas, ArtTop, nameplateTop, Rows(3), 80f, false, 1050f);
        Assert.Equal(first, second);
    }

    [Fact]
    public void RewardRowsAreAFingerTallWithAScaledNativeIcon()
    {
        AncientOptionMetrics options = PortraitAncientMath.Options;
        Assert.Equal(180f, options.RowHeight, 3);      // 62.5 points
        Assert.Equal(108f, options.IconSize, 3);       // 37.5 points
        Assert.Equal(39, options.FontSize);           // 13.4 points
        Assert.Equal(108f, options.IconSize * 1f, 3);
        Assert.Equal(996f - 2f * options.InsetX - options.IconSize - options.IconGap, options.TextWidth(996f, icon: true), 3);
        Assert.Equal(996f - 2f * options.InsetX, options.TextWidth(996f, icon: false), 3);
    }

    [Fact]
    public void RowsGrowForTextThatDoesNotFitAndTheColumnAddsGapsAndTheEffectGutter()
    {
        AncientOptionMetrics options = PortraitAncientMath.Options;
        Assert.Equal(180f, options.HeightFor(100f), 3);
        Assert.Equal(200f + 2f * options.InsetY, options.HeightFor(200f), 3);

        var rows = new[] { 180f, 214f, 180f, 180f };
        float total = options.TotalHeight(rows);
        Assert.Equal(754f + 3f * options.Separation + options.BottomGutter, total, 3);
        Assert.Equal(0f, options.TotalHeight(Array.Empty<float>()), 3);
    }

    [Fact]
    public void NameplateSitsInsideTheBottomSafeAreaAndItsReserveClearsTheRewardRows()
    {
        float footer = PortraitAncientMath.NameplateFooter(TallCanvas);
        Assert.Equal(2238.48f, footer, 2);
        Assert.True(PortraitAncientMath.NameplateTop(TallCanvas) < footer);
        Assert.True(footer - PortraitAncientMath.NameplateTop(TallCanvas) >= 100f);
        // The settled epithet's pivot is its own bottom, which is put on the footer line.
        Assert.Equal(198.48f, PortraitAncientMath.EpithetLocalY(TallCanvas, -300f, TallCanvas), 2);
        Assert.Equal(footer, PortraitAncientMath.EpithetLocalY(TallCanvas, -300f, 2340f) - 300f + 2340f, 1);
    }

    [Fact]
    public void DialogueWidensTheNativeBubbleToTheColumnsWidth()
    {
        Assert.Equal(942f, PortraitAncientMath.DialogueLineWidth(998f), 2);
        Assert.Equal(142f, PortraitAncientMath.DialogueWiden(998f), 2);
        Assert.Equal(0f, PortraitAncientMath.DialogueWiden(400f), 2);
    }

    [Theory]
    [InlineData(39, 33)]
    [InlineData(60, 51)]
    [InlineData(24, 20)]
    public void EnergyIconsFollowTheTextSizeAtEightyFivePercent(int font, int expected)
    {
        Assert.Equal(expected, PortraitAncientMath.EnergyIconSize(font));
    }

    [Theory]
    [InlineData("[img]res://images/packed/sprite_fonts/tears_energy_icon.png[/img] Gain 2 Tears.",
        "[img=33x33]res://images/packed/sprite_fonts/tears_energy_icon.png[/img] Gain 2 Tears.")]
    [InlineData("[img=10x10]res://images/packed/sprite_fonts/ironclad_energy_icon.png[/img][img]res://images/packed/sprite_fonts/ironclad_energy_icon.png[/img]",
        "[img=33x33]res://images/packed/sprite_fonts/ironclad_energy_icon.png[/img][img=33x33]res://images/packed/sprite_fonts/ironclad_energy_icon.png[/img]")]
    [InlineData("[img]res://images/packed/common_ui/star.png[/img] and [b]bold[/b]",
        "[img]res://images/packed/common_ui/star.png[/img] and [b]bold[/b]")]
    public void OnlyNativeEnergySpriteIconsAreResized(string text, string expected)
    {
        Assert.Equal(expected, PortraitAncientMath.RewriteEnergyIcons(text, 39));
    }

    [Fact]
    public void RewritingIsIdempotentAtOneSize()
    {
        const string native = "[img]res://images/packed/sprite_fonts/regent_energy_icon.png[/img]";
        string once = PortraitAncientMath.RewriteEnergyIcons(native, 39);
        Assert.Equal(once, PortraitAncientMath.RewriteEnergyIcons(once, 39));
        Assert.NotEqual(once, PortraitAncientMath.RewriteEnergyIcons(native, 60));
    }
}
