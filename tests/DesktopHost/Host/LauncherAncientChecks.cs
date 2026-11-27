using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The launcher's answers for the shared <c>ancient</c>, <c>ancient-pael</c>, <c>ancient-dialogue</c> and <c>ancient-art-tour</c>
/// scenarios (L8): the clipped art band with its per-ancient focus, the fade into the same room cover, the settled native
/// nameplate inside the safe frame, and 62.5 pt option rows with 37.5 pt relic icons and 13.4 pt text. The dialogue stack and
/// Pael's <c>[img=NxN]</c> rewrite are asserted by the shared scenario. Node names only; no D-only symbols.
/// </summary>
internal sealed partial class TestDriver
{
    /// <summary>The original 1680-wide canvas's 280 u row: 62.5 points.</summary>
    private const float LauncherAncientRowHeight = 62.5f * PortraitBands.UnitsPerPoint;
    /// <summary>The original 1680-wide canvas's 168 u relic icon: 37.5 points.</summary>
    private const float LauncherAncientIconSize = 37.5f * PortraitBands.UnitsPerPoint;
    /// <summary>The original 1680-wide canvas's 60 u option text: 13.4 points.</summary>
    private const float LauncherAncientTextFont = 13.4f * PortraitBands.UnitsPerPoint * 0.96f;

    private partial string AncientArtNodeName() => "PortraitAncientArt";

    private partial string AncientArtFadeNodeName() => "PortraitAncientArtFade";

    private async partial Task CheckAncientNameplateAndArtAsync(NAncientEventLayout layout, Control? art)
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            _report.Check("ancient_landscape_has_no_portrait_crop",
                art is null && layout.GetNodeOrNull<Control>(AncientArtFadeNodeName()) is null,
                "native landscape framing");
            return;
        }

        Vector2 canvas = GetViewportSize();
        Rect2 safe = PortraitSafeRect();
        float headerBottom = LauncherMapHeaderBottom(_tree.Root);
        var background = layout.GetNodeOrNull<Control>("%AncientBgContainer");
        Rect2 artRect = art?.GetGlobalRect() ?? default;
        _report.Check("ancient_art_clipped", art is { ClipContents: true } && background is not null && IsUnder(background, art),
            $"art={(art is null ? "missing" : $"{artRect} clip={art.ClipContents}")}");
        _report.Check("ancient_art_bleeds_behind_header", art is not null
            && headerBottom - artRect.Position.Y is >= 8f and <= 32f
            && artRect.Position.X <= 1f && artRect.End.X >= canvas.X - 2f,
            $"art={artRect} headerBottom={headerBottom:F0}");
        _report.Check("ancient_art_covers_frame", art is not null && background is not null
            && background.GetGlobalRect().Grow(1f).Encloses(artRect),
            $"background={background?.GetGlobalRect()} art={artRect}");

        float optionTop = layout.OptionButtons.Min(button => button.GetGlobalRect().Position.Y);
        float gap = optionTop - artRect.End.Y;
        bool compact = canvas.Y / canvas.X <= 16f / 9f + 0.001f;
        _report.Check("ancient_art_reaches_rewards", art is not null && (compact
            ? Math.Abs(gap) <= 24f
            : artRect.End.Y > canvas.Y * 0.6f && gap is >= 0f and <= 260f),
            $"artBottom={artRect.End.Y:F0} optionTop={optionTop:F0} gap={gap:F0} compact={compact}");

        var fade = layout.GetNodeOrNull<TextureRect>(AncientArtFadeNodeName());
        var cover = NEventRoom.Instance?.GetNodeOrNull<TextureRect>(EventRoomCoverNodeName());
        _report.Check("ancient_art_blends_into_room_cover", fade is not null && cover is not null
            && cover.Texture is not null && fade.Texture == cover.Texture
            && fade.GetGlobalRect().IsEqualApprox(cover.GetGlobalRect()),
            $"fade={fade?.GetGlobalRect()} cover={cover?.GetGlobalRect()} texture={fade?.Texture?.ResourcePath ?? "null"}");

        NAncientNameBanner? banner = layout.GetChildren().OfType<NAncientNameBanner>().FirstOrDefault();
        if (banner is null)
        {
            _report.Fail("ancient_nameplate_larger_below_rewards", "no NAncientNameBanner in the ancient layout");
            return;
        }
        Label epithet = banner.GetNode<Label>("%Epithet");
        RichTextLabel title = banner.GetNode<RichTextLabel>("%Title");
        await WaitUntilAsync(() => epithet.Scale.X >= 1.3f && title.HorizontalAlignment == HorizontalAlignment.Left
            && title.VerticalAlignment == VerticalAlignment.Bottom, "settled ancient nameplate", ShortWait);
        float footerBottom = (epithet.GetGlobalTransform() * new Vector2(0f, epithet.Size.Y)).Y;
        float buttonBottom = layout.OptionButtons.Max(button => button.GetGlobalRect().End.Y);
        float titleTop = footerBottom - title.GetContentHeight() * epithet.Scale.Y;
        _report.Check("ancient_nameplate_larger_below_rewards", footerBottom <= safe.End.Y + 1f && titleTop > buttonBottom,
            $"scale={epithet.Scale.X:F2} titleTop={titleTop:F0} footerBottom={footerBottom:F0} buttonBottom={buttonBottom:F0}");
        Rect2 nameplate = epithet.GetGlobalRect();
        _report.Check("ancient_nameplate_inside_safe", safe.Grow(2f).Encloses(nameplate) && epithet.PivotOffset.Y > 0f
            && Mathf.IsZeroApprox(epithet.PivotOffset.X), $"nameplate={nameplate} pivot={epithet.PivotOffset} safe={safe}");
    }

    private partial IReadOnlyList<float> AncientOptionScales() => new[] { 1f };

    private partial void SetAncientOptionScale(float scale) => _ = scale;

    private partial float AncientRelicIconMinSize() => LauncherAncientIconSize * 0.98f;

    private partial void CheckAncientOptionLayout(NEventOptionButton button, string id)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Rect2 row = button.GetGlobalRect();
        _report.Check(id + "_row_phone_height", row.Size.Y >= LauncherAncientRowHeight - 2f,
            $"row={row.Size} expected>={LauncherAncientRowHeight:F1}");
        var text = button.GetNode<RichTextLabel>("%Text");
        _report.Check(id + "_text_phone_size",
            text.GetThemeFontSize("normal_font_size") >= LauncherAncientTextFont, $"font={text.GetThemeFontSize("normal_font_size")}");
        var icon = button.GetNode<TextureRect>("%RelicIcon");
        if (icon.Visible)
            _report.Check(id + "_icon_phone_size", icon.Size.X >= LauncherAncientIconSize - 2f
                && icon.Size.Y >= LauncherAncientIconSize - 2f, $"icon={icon.Size} expected>={LauncherAncientIconSize:F1}");
    }

    /// <summary>
    /// The launcher's painting is placed once (decision 27-30): only the clip's bottom edge eases between the dialogue height
    /// and the rewards height. So the art's Position and Size.X must match the setup's, the camera transform must be unchanged,
    /// and the clip height recorded on dialogue line 1 must persist on the later dialogue lines; the last line may already show
    /// the rewards height. Detail prints top/width/height.
    /// </summary>
    private partial bool CheckAncientArtStable(int index, int count, Control? art, Rect2 initialArt, Control artScene,
        Transform2D initialCamera, ref float? dialogueClipHeight, out string detail)
    {
        Rect2 current = (art ?? artScene).GetGlobalRect();
        bool samePosition = Mathf.Abs(current.Position.X - initialArt.Position.X) <= 1f
            && Mathf.Abs(current.Position.Y - initialArt.Position.Y) <= 1f;
        bool sameWidth = Mathf.Abs(current.Size.X - initialArt.Size.X) <= 1f;
        bool sameCamera = artScene.GetGlobalTransform().IsEqualApprox(initialCamera);
        bool sameDialogueHeight;
        if (index == 0)
        {
            dialogueClipHeight = current.Size.Y;
            sameDialogueHeight = true;
        }
        else if (index < count - 1)
            sameDialogueHeight = dialogueClipHeight is { } height && Mathf.Abs(current.Size.Y - height) <= 1f;
        else sameDialogueHeight = true; // the final line may already show the rewards height
        detail = $"top={current.Position.Y:F1} width={current.Size.X:F1} height={current.Size.Y:F1} " +
                 $"dialogueHeight={(dialogueClipHeight is { } h ? h.ToString("F1") : "-")}";
        return samePosition && sameWidth && sameCamera && sameDialogueHeight;
    }
}
