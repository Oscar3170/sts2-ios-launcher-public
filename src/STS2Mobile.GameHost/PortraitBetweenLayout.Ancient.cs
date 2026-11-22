using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// An Ancient event in portrait: the native painting cropped around the ancient's authored subject and bled into the act's
/// room cover, the native dialogue bubbles widened and stacked over that art, and the reward rows grown to a finger with the
/// nameplate enlarged once it settles at the bottom. The crop is cached per ancient instance, so rebuilding the reward rows
/// never reframes the art; every node is put back for landscape.
/// </summary>
internal sealed partial class PortraitBetweenLayout
{
    /// <summary>The clip that crops the painting; it holds <c>%AncientBgContainer</c> while portrait is on.</summary>
    public const string AncientArtClipName = "PortraitAncientArt";

    /// <summary>The room cover's pixels, fading in over the crop's bottom.</summary>
    public const string AncientArtFadeName = "PortraitAncientArtFade";

    // The event room's full-canvas act cover lies under the layout; it is looked up by name so this file never depends on
    // the code that owns it.
    private const string AncientCoverNodeName = "PortraitEventRoomCover";

    // The dialogue's "Continue" hint is placed 268 to 663 units right of the centre, for a desktop-wide screen.
    private const float AncientNextEdgeMargin = 40f;

    // Where the settled nameplate's title and epithet are put; the banner itself animates to x 48.
    private const string AncientBannerTypeName = "MegaCrit.Sts2.Core.Nodes.NAncientNameBanner";

    private const string AncientFadeShaderCode = """
        shader_type canvas_item;
        uniform float lower_start;
        uniform float lower_end;
        varying float local_y;
        void vertex() { local_y = VERTEX.y; }
        void fragment() { COLOR.a *= smoothstep(lower_start, lower_end, local_y); }
        """;

    private readonly Dictionary<ulong, AncientState> _ancientStates = new();
    private readonly Dictionary<ulong, Callable> _ancientLineForwards = new();
    private readonly Dictionary<ulong, bool> _ancientAutoSizes = new();
    private readonly Dictionary<ulong, TextServer.AutowrapMode> _ancientAutowraps = new();
    private readonly Dictionary<ulong, Control.MouseFilterEnum> _ancientMouseFilters = new();
    private readonly HashSet<ulong> _ancientPivots = new();
    private static Shader? _ancientFadeShader;

    private sealed class AncientState
    {
        public ulong Id;
        public Vector2 Canvas;
        /// <summary>The safe area's top and bottom inset measured for this screen.</summary>
        public float SafeInset;
        /// <summary>Bottom of the stone header: the art starts just under it, with the relic strip over the art.</summary>
        public float HeaderBottom;
        /// <summary>Frames left to retry the energy-icon rewrite while a rebuilt page settles its text.</summary>
        public int IconPassesLeft;
        public AncientArtFrame Frame;
        public float ContentTop;
        public float ContentWidth;
        public float ContentLeft;
        public float ContainerLeft;
        public float ContainerWidth;
        public float DialogueHeight;
        public float RewardHeight;
        public ulong ContainerId;
        public ulong BannerId, TitleId, EpithetId;
        public bool NameplateHooked, NameplateSettled;
        public ulong HitboxId;
        public int HitboxIndex = -1;
        public ulong CoverId;
        public ulong BgId, BgParentId;
        public int BgIndex = -1;
        public ulong ClipId, FadeId;
        public float FadeStart, FadeEnd;
        public bool ContentHeightCaptured;
        public float OriginalContentHeight;
    }

    // ---- entry points ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Lays out an ancient as it appears: the art's crop and fade, the clipped dialogue/options column over it, the native
    /// hitbox under that column so bubbles can be hovered, and the nameplate's enlargement hook. The reward rows themselves
    /// are sized as they are added (see <see cref="LayoutOnAdd"/>), before the game measures them for its slide.
    /// </summary>
    private void LayoutAncientPortrait(Control layout, float capHeight, float canvasWidth, float canvasHeight)
    {
        ulong id = layout.GetInstanceId();
        if (!_ancientStates.TryGetValue(id, out AncientState? state))
        {
            state = new AncientState { Id = id };
            _ancientStates[id] = state;
        }
        state.Canvas = new Vector2(canvasWidth, canvasHeight);
        state.SafeInset = capHeight;
        state.HeaderBottom = capHeight + PortraitBands.TopBarHeight;
        ShiftAncientNextButton(layout, canvasWidth);
        // Framed once, from the event's option list; rebuilding the rows later never touches the crop.
        if (state.Frame.ClipBottom <= state.Frame.ClipTop)
            ConfigureAncientFrame(state, layout, AncientOptionCount(layout));
        HookAncientBanner(state, layout);
    }

    /// <summary>Per frame: the game may move the painting on a window change, and the fade waits for the room cover.</summary>
    private void UpdateAncientPortraits(float capHeight, Vector2 canvas)
    {
        foreach (ulong id in _ancientStates.Keys)
            if (GodotObject.InstanceFromId(id) is Control ancient && GodotObject.IsInstanceValid(ancient) && ancient.IsVisibleInTree())
                UpdateAncientPortrait(ancient, capHeight, canvas);
    }

    private void UpdateAncientPortrait(Control layout, float capHeight, Vector2 canvas)
    {
        ulong id = layout.GetInstanceId();
        if (!_ancientStates.TryGetValue(id, out AncientState? state)) return;
        Control? bg = state.BgId != 0 ? GodotObject.InstanceFromId(state.BgId) as Control : null;
        if (bg != null && !GodotObject.IsInstanceValid(bg)) bg = null;
        bg ??= layout.GetNodeOrNull<Control>("%AncientBgContainer");
        if (bg == null) return;
        if (state.Frame.ClipBottom <= state.Frame.ClipTop)
        {
            state.Canvas = canvas;
            state.SafeInset = capHeight;
            state.HeaderBottom = capHeight + PortraitBands.TopBarHeight;
            ConfigureAncientFrame(state, layout, AncientOptionCount(layout));
        }
        if (state.ClipId == 0 || GodotObject.InstanceFromId(state.ClipId) is not Control existing || !GodotObject.IsInstanceValid(existing))
            CreateAncientClip(state, layout, bg);
        EnsureAncientArt(state, layout, bg, canvas);
        EnsureAncientFade(state, layout);
        HookAncientBanner(state, layout);
        KeepAncientHitboxBelowColumn(state, layout);
        if (state.IconPassesLeft > 0)
        {
            state.IconPassesLeft--;
            RewriteAncientPageIcons(state);
        }
    }

    /// <summary>
    /// An ancient reward row is 1000 x 100 units with text up to 24 units and a 60 unit relic icon, shown left of the text.
    /// In portrait it becomes a 180 unit row (62.5 points) with a 108 unit icon (37.5 points) and 39 unit text (13.4
    /// points), its frame and flashes stretch to the row and their pivots stay centred, and its text gets every unit left
    /// after the icon. Called as the row is added, before the game measures the column.
    /// </summary>
    private void LayoutAncientOptionPortrait(Control option)
    {
        AncientOptionMetrics metrics = PortraitAncientMath.Options;
        AncientState? state = AncientStateAbove(option);
        float rowWidth = state?.ContentWidth is > 100f ? state.ContentWidth : 1080f - 2f * PortraitAncientMath.ContentSideInset;

        Capture(option, captureVisible: false);
        option.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (option.GetNodeOrNull<Control>("HBoxContainer") is { } hbox)
        {
            Capture(hbox, captureVisible: false);
            hbox.AnchorLeft = 0f;
            hbox.AnchorTop = 0f;
            hbox.AnchorRight = 1f;
            hbox.AnchorBottom = 1f;
            hbox.OffsetLeft = metrics.InsetX;
            hbox.OffsetTop = metrics.InsetY;
            hbox.OffsetRight = -metrics.InsetX;
            hbox.OffsetBottom = -metrics.InsetY;
            hbox.MouseFilter = Control.MouseFilterEnum.Ignore;
        }
        bool icon = false;
        if (option.GetNodeOrNull<TextureRect>("%RelicIcon") is { } relic)
        {
            Capture(relic, captureVisible: true);
            var iconSize = new Vector2(metrics.IconSize, metrics.IconSize);
            if (!relic.CustomMinimumSize.IsEqualApprox(iconSize)) relic.CustomMinimumSize = iconSize;
            relic.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            icon = relic.Visible;
        }
        if (option.GetNodeOrNull<Control>("HBoxContainer/Spacer") is { } spacer)
        {
            Capture(spacer, captureVisible: true);
            if (spacer.Visible != icon) spacer.Visible = icon;
            spacer.CustomMinimumSize = new Vector2(metrics.IconGap, 0f);
        }
        float height = metrics.RowHeight;
        if (option.GetNodeOrNull<RichTextLabel>("%Text") is { } label)
        {
            Capture(label, captureVisible: false);
            StyleAncientText(label, metrics.FontSize);
            label.CustomMinimumSize = Vector2.Zero;
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            SetAncientAutowrap(label, TextServer.AutowrapMode.WordSmart);
            label.Size = new Vector2(metrics.TextWidth(rowWidth, icon), label.Size.Y);
            RewriteAncientEnergyIcons(label);
            height = metrics.HeightFor(label.GetContentHeight());
        }
        var rowSize = new Vector2(rowWidth, height);
        if (!option.CustomMinimumSize.IsEqualApprox(rowSize)) option.CustomMinimumSize = rowSize;
        TrackAncientPivot(option);
        foreach (string name in new[] { "Outline", "RedFlash", "BlueFlash" })
            if (option.GetNodeOrNull<NinePatchRect>(name) is { } effect) StretchAncientOptionEffect(effect);

        if (option.GetParent() is Control options) SizeAncientOptions(options, rowWidth, metrics);
        // A rebuilt page (new dialogue or new rows) must have its column ready in the same frame the game measures it,
        // and the page's text may only be final after its labels are ready, so the rewrite is retried for a few frames.
        if (state != null && AncientLayoutAbove(option) is { } layout)
        {
            state.IconPassesLeft = 3;
            ApplyAncientColumn(state, layout, -1f);
        }
    }

    /// <summary>
    /// An ancient's dialogue bubble is 800 units wide with its text at a fixed 24 units (8 points). In portrait it takes the
    /// column's width, its text is drawn at 13.4 points, and the bubble grows with the text. A touch on a bubble advances
    /// the dialogue exactly like the native full-screen hitbox, which sits under the bubbles so hovering re-reads history.
    /// </summary>
    private void LayoutAncientDialogueLinePortrait(Control line)
    {
        AncientState? state = AncientStateAbove(line);
        float contentWidth = state?.ContentWidth is > 100f ? state.ContentWidth : 1080f - 2f * PortraitAncientMath.ContentSideInset;
        float widen = PortraitAncientMath.DialogueWiden(contentWidth);
        foreach (string path in new[] { ".", "DialogueContainer", "DialogueContainer/TextContainer/Text" })
        {
            if (line.GetNodeOrNull<Control>(path) is not { } part) continue;
            PortraitControlSnapshot snap = Capture(part, captureVisible: false);
            var minimum = new Vector2(snap.InitialCustomMinimumSize.X + widen, snap.InitialCustomMinimumSize.Y);
            if (!part.CustomMinimumSize.IsEqualApprox(minimum)) part.CustomMinimumSize = minimum;
        }
        if (line.GetNodeOrNull<Control>("DialogueContainer/TextContainer/Text") is { } text)
        {
            StyleAncientText(text, PortraitAncientMath.FontSize);
            RewriteAncientEnergyIcons(text);
        }
        // The bubble and its tail are plain controls with the default Stop filter: under the native layout they never see a
        // tap (the full-screen hitbox lies above them), but now the bubble must let its own line button take the touch.
        foreach (string path in new[]
                 {
                     "DialogueContainer/HBoxContainer/Bubble",
                     "DialogueContainer/HBoxContainer/Bubble/Shadow",
                     "DialogueContainer/HBoxContainer/DialogueTailLeft",
                     "DialogueContainer/HBoxContainer/DialogueTailRight",
                 })
            if (line.GetNodeOrNull<Control>(path) is { } part && part.MouseFilter != Control.MouseFilterEnum.Ignore)
            {
                if (!_ancientMouseFilters.ContainsKey(part.GetInstanceId())) _ancientMouseFilters[part.GetInstanceId()] = part.MouseFilter;
                part.MouseFilter = Control.MouseFilterEnum.Ignore;
            }
        ForwardAncientLineRelease(line);
    }

    // ---- composition ----------------------------------------------------------------------------------------------------

    private void ConfigureAncientFrame(AncientState state, Control layout, int optionCount)
    {
        AncientOptionMetrics metrics = PortraitAncientMath.Options;
        float nameplateTop = PortraitAncientMath.NameplateTop(state.Canvas.Y, state.SafeInset);
        float optionsHeight = metrics.TotalHeight(Enumerable.Repeat(metrics.RowHeight, Math.Max(optionCount, 0)).ToArray());
        bool compact = state.Canvas.Y <= state.Canvas.X * (16f / 9f) + 1f;
        float focus = PortraitAncientMath.FocusX(AncientBackgroundScene(layout));
        state.Frame = PortraitAncientMath.FrameArt(
            state.Canvas.X, state.Canvas.Y, PortraitAncientMath.ArtTop(state.HeaderBottom), nameplateTop, optionsHeight,
            PortraitAncientMath.DialogueLineReserve, compact, focus);

        // The column holds the dialogue stack over the art; the game later grows it down to the nameplate for the rewards.
        state.ContentWidth = state.Canvas.X - 2f * PortraitAncientMath.ContentSideInset;
        state.ContentLeft = PortraitAncientMath.ContentSideInset;
        state.ContainerLeft = PortraitAncientMath.ContainerInset;
        state.ContainerWidth = state.Canvas.X - 2f * PortraitAncientMath.ContainerInset;
        state.ContentTop = state.Frame.ClipTop + PortraitAncientMath.ContentTopGap;
        state.DialogueHeight = Math.Max(1f, state.Frame.ClipBottom - PortraitAncientMath.DialogueBottomGap - state.ContentTop);
        state.RewardHeight = Math.Max(state.DialogueHeight, nameplateTop - state.ContentTop);
        ApplyAncientColumn(state, layout, optionsHeight);
        if (layout.GetNodeOrNull<Control>("%AncientBgContainer") is { } bg)
        {
            if (state.ClipId == 0 || GodotObject.InstanceFromId(state.ClipId) is not Control clip || !GodotObject.IsInstanceValid(clip))
                CreateAncientClip(state, layout, bg);
            EnsureAncientArt(state, layout, bg, state.Canvas);
        }
    }

    private void ApplyAncientColumn(AncientState state, Control layout, float optionsHeight)
    {
        if (layout.GetNodeOrNull<Control>("%ContentContainer") is not { } container) return;
        state.ContainerId = container.GetInstanceId();
        Capture(container, captureVisible: false);
        container.AnchorLeft = 0f;
        container.AnchorTop = 0f;
        container.AnchorRight = 0f;
        container.AnchorBottom = 0f;
        container.Position = new Vector2(state.ContainerLeft, state.ContentTop);
        // The game grows the box to its reward height only for the last dialogue line; a rebuilt page asks again.
        bool lastLine = layout.GetType().GetProperty("IsDialogueOnLastLine", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(layout) is true;
        container.Size = new Vector2(state.ContainerWidth, lastLine ? state.RewardHeight : state.DialogueHeight);
        if (container.GetNodeOrNull<Control>("Content") is { } content)
        {
            Capture(content, captureVisible: false);
            content.AddThemeConstantOverride("separation", Mathf.RoundToInt(PortraitAncientMath.ContentSeparation));
            content.CustomMinimumSize = new Vector2(state.ContentWidth, content.CustomMinimumSize.Y);
            content.Position = new Vector2(state.ContentLeft - state.ContainerLeft, content.Position.Y);
            content.Size = new Vector2(state.ContentWidth, content.Size.Y);
        }
        if (container.GetNodeOrNull<Control>("%OptionsContainer") is { } options)
            SizeAncientOptions(options, state.ContentWidth, PortraitAncientMath.Options, optionsHeight);
        WriteAncientRewardHeight(state, layout);
        KeepAncientHitboxBelowColumn(state, layout);
    }

    /// <summary>
    /// The game restores its clipped box to <c>_originalContentContainerHeight</c> for the last dialogue line and slides the
    /// column to end its options at that box's bottom. Writing that field (instead of moving the content each frame) lets
    /// the native slide put the portrait column, and its taller rows, exactly where they belong.
    /// </summary>
    private static void WriteAncientRewardHeight(AncientState state, Control layout)
    {
        FieldInfo? field = layout.GetType().GetField("_originalContentContainerHeight", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) return;
        if (!state.ContentHeightCaptured)
        {
            state.OriginalContentHeight = field.GetValue(layout) is float kept ? kept : 0f;
            state.ContentHeightCaptured = true;
        }
        if (field.GetValue(layout) is not float current || Math.Abs(current - state.RewardHeight) > 0.5f)
            field.SetValue(layout, state.RewardHeight);
    }

    /// <summary>
    /// The dialogue bubbles are buttons, and the native full-screen hitbox lies over them, so hovering a bubble never
    /// reaches it. Portrait puts the hitbox under the column: hover re-reads history, and a bubble's own release is
    /// forwarded to the hitbox so a touch on it still advances the dialogue.
    /// </summary>
    private static void KeepAncientHitboxBelowColumn(AncientState state, Control layout)
    {
        Control? hitbox = state.HitboxId != 0 ? GodotObject.InstanceFromId(state.HitboxId) as Control : null;
        if (hitbox == null || !GodotObject.IsInstanceValid(hitbox))
        {
            hitbox = layout.GetNodeOrNull<Control>("%DialogueHitbox");
            if (hitbox == null) return;
            state.HitboxId = hitbox.GetInstanceId();
            state.HitboxIndex = hitbox.GetIndex();
        }
        Control? container = state.ContainerId != 0 ? GodotObject.InstanceFromId(state.ContainerId) as Control : null;
        if (container == null || !GodotObject.IsInstanceValid(container)) return;
        int wanted = container.GetIndex();
        if (hitbox.GetIndex() > wanted) layout.MoveChild(hitbox, wanted);
    }

    private void ShiftAncientNextButton(Control layout, float canvasWidth)
    {
        if (layout.GetNodeOrNull<Control>("%FakeNextButtonContainer/FakeNextButton") is not { } next) return;
        PortraitControlSnapshot snap = Capture(next, captureVisible: false);
        float shift = Math.Max(0f, snap.InitialOffsetRight - (canvasWidth / 2f - AncientNextEdgeMargin));
        if (Math.Abs(next.OffsetLeft - (snap.InitialOffsetLeft - shift)) > 0.01f) next.OffsetLeft = snap.InitialOffsetLeft - shift;
        if (Math.Abs(next.OffsetRight - (snap.InitialOffsetRight - shift)) > 0.01f) next.OffsetRight = snap.InitialOffsetRight - shift;
    }

    // ---- the painting ---------------------------------------------------------------------------------------------------

    private void CreateAncientClip(AncientState state, Control layout, Control background)
    {
        Control? clip = layout.GetNodeOrNull<Control>(AncientArtClipName);
        if (clip == null)
        {
            clip = new Control
            {
                Name = AncientArtClipName,
                ClipContents = true,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            layout.AddChild(clip);
        }
        layout.MoveChild(clip, 0);
        if (state.BgId == 0)
        {
            state.BgId = background.GetInstanceId();
            state.BgParentId = background.GetParent()?.GetInstanceId() ?? 0;
            state.BgIndex = background.GetIndex();
        }
        if (background.GetParent() != clip) background.Reparent(clip, keepGlobalTransform: false);
        state.ClipId = clip.GetInstanceId();
        Capture(background, captureVisible: false);
        background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        background.PivotOffset = Vector2.Zero;
    }

    private static void EnsureAncientArt(AncientState state, Control layout, Control background, Vector2 canvas)
    {
        if (state.ClipId == 0 || GodotObject.InstanceFromId(state.ClipId) is not Control clip || !GodotObject.IsInstanceValid(clip))
            return;
        var clipPosition = new Vector2(0f, state.Frame.ClipTop - layout.GlobalPosition.Y);
        var clipSize = new Vector2(canvas.X, Math.Max(1f, state.Frame.ClipBottom - state.Frame.ClipTop));
        if (!clip.Position.IsEqualApprox(clipPosition)) clip.Position = clipPosition;
        if (!clip.Size.IsEqualApprox(clipSize)) clip.Size = clipSize;

        // The game's own window handler re-places the painting on a size change; hold the portrait crop over it.
        var sourceSize = new Vector2(PortraitAncientMath.ArtSourceWidth, PortraitAncientMath.ArtSourceHeight);
        if (!background.Size.IsEqualApprox(sourceSize)) background.Size = sourceSize;
        var scale = new Vector2(state.Frame.Scale, state.Frame.Scale);
        if (!background.Scale.IsEqualApprox(scale)) background.Scale = scale;
        var position = new Vector2(state.Frame.OffsetX - clip.Position.X, 0f);
        if (!background.Position.IsEqualApprox(position)) background.Position = position;
    }

    /// <summary>
    /// Fades the crop's bottom into the room cover behind the event: the fade is a copy of the cover's pixels drawn over the
    /// art, transparent where the art is clear and solid where the cover takes over, so the seam disappears into the same
    /// pixels rather than a second colour.
    /// </summary>
    private void EnsureAncientFade(AncientState state, Control layout)
    {
        TextureRect? cover = state.CoverId != 0 ? GodotObject.InstanceFromId(state.CoverId) as TextureRect : null;
        if (cover == null || !GodotObject.IsInstanceValid(cover))
        {
            cover = FindAncientCover(layout);
            if (cover == null) return;
            state.CoverId = cover.GetInstanceId();
        }
        if (cover.Texture == null) return;
        TextureRect? fade = state.FadeId != 0 ? GodotObject.InstanceFromId(state.FadeId) as TextureRect : null;
        if (fade == null || !GodotObject.IsInstanceValid(fade))
        {
            _ancientFadeShader ??= new Shader { Code = AncientFadeShaderCode };
            fade = new TextureRect
            {
                Name = AncientArtFadeName,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = cover.StretchMode,
                Material = new ShaderMaterial { Shader = _ancientFadeShader },
            };
            layout.AddChild(fade);
            state.FadeId = fade.GetInstanceId();
            state.FadeStart = float.NaN;
            state.FadeEnd = float.NaN;
        }
        if (state.ClipId != 0 && GodotObject.InstanceFromId(state.ClipId) is Control clip && GodotObject.IsInstanceValid(clip) &&
            fade.GetIndex() != clip.GetIndex() + 1)
            layout.MoveChild(fade, clip.GetIndex() + 1);
        if (fade.Texture != cover.Texture) fade.Texture = cover.Texture;
        if (fade.Modulate != cover.Modulate) fade.Modulate = cover.Modulate;
        if (fade.StretchMode != cover.StretchMode) fade.StretchMode = cover.StretchMode;
        if (!fade.Size.IsEqualApprox(cover.Size)) fade.Size = cover.Size;
        if (!fade.GlobalPosition.IsEqualApprox(cover.GlobalPosition)) fade.GlobalPosition = cover.GlobalPosition;
        if (fade.Material is not ShaderMaterial material) return;
        float origin = fade.GlobalPosition.Y;
        if (!Mathf.IsEqualApprox(state.FadeStart, state.Frame.FadeStart - origin))
        {
            state.FadeStart = state.Frame.FadeStart - origin;
            material.SetShaderParameter("lower_start", state.FadeStart);
        }
        if (!Mathf.IsEqualApprox(state.FadeEnd, state.Frame.FadeEnd - origin))
        {
            state.FadeEnd = state.Frame.FadeEnd - origin;
            material.SetShaderParameter("lower_end", state.FadeEnd);
        }
    }

    private static TextureRect? FindAncientCover(Node layout)
    {
        for (Node? node = layout.GetParent(); node != null; node = node.GetParent())
        {
            if (node.GetNodeOrNull<TextureRect>(AncientCoverNodeName) is { } cover) return cover;
            foreach (Node child in node.GetChildren())
                if (child is TextureRect rect && rect.Name.ToString().EndsWith("EventRoomCover", StringComparison.Ordinal))
                    return rect;
        }
        return null;
    }

    /// <summary>The authored scene on the background's first child decides the crop's focus (Neow, Darv, Pael, ...).</summary>
    private static string? AncientBackgroundScene(Control layout)
    {
        if (layout.GetNodeOrNull<Control>("%AncientBgContainer") is not { } background) return null;
        foreach (Node child in background.GetChildren())
            if (child is Control control) return control.Name.ToString();
        return null;
    }

    // ---- the nameplate --------------------------------------------------------------------------------------------------

    private void HookAncientBanner(AncientState state, Control layout)
    {
        if (!state.NameplateHooked)
        {
            Control? banner = null;
            foreach (Node child in layout.GetChildren())
                if (child is Control control && control.GetType().FullName == AncientBannerTypeName)
                {
                    banner = control;
                    break;
                }
            if (banner == null) return;
            if (banner.GetNodeOrNull<RichTextLabel>("%Title") is not { } title || banner.GetNodeOrNull<Control>("%Epithet") is not { } epithet)
                return;
            state.BannerId = banner.GetInstanceId();
            state.TitleId = title.GetInstanceId();
            state.EpithetId = epithet.GetInstanceId();
            state.NameplateHooked = true;
            ulong id = state.Id;
            // The game changes the title's alignment and theme when its intro settles at the bottom of the screen.
            title.Connect(Control.SignalName.ThemeChanged, Callable.From(() => QueueAncientNameplate(id)));
        }
        ApplyAncientNameplate(state, layout);
    }

    private void QueueAncientNameplate(ulong id) => Callable.From(() =>
    {
        if (!_ancientStates.TryGetValue(id, out AncientState? state)) return;
        if (GodotObject.InstanceFromId(id) is not Control layout || !GodotObject.IsInstanceValid(layout)) return;
        ApplyAncientNameplate(state, layout);
    }).CallDeferred();

    /// <summary>
    /// Enlarges the settled nameplate and puts its footer inside the bottom safe area. Nothing happens during the intro:
    /// the game animates the title and epithet in the middle of the screen, and the settled state is left-aligned at the
    /// bottom of the title label.
    /// </summary>
    private void ApplyAncientNameplate(AncientState state, Control layout)
    {
        if (state.NameplateSettled || !state.NameplateHooked) return;
        if (GodotObject.InstanceFromId(state.TitleId) is not RichTextLabel title || !GodotObject.IsInstanceValid(title)) return;
        if (title.HorizontalAlignment != HorizontalAlignment.Left || title.VerticalAlignment != VerticalAlignment.Bottom) return;
        if (GodotObject.InstanceFromId(state.EpithetId) is not Control epithet || !GodotObject.IsInstanceValid(epithet)) return;
        if (GodotObject.InstanceFromId(state.BannerId) is not Control banner || !GodotObject.IsInstanceValid(banner)) return;
        Capture(epithet, captureVisible: true);
        Capture(title, captureVisible: false);
        // The native epithet is a full-screen label; scaled about its bottom edge its rect would reach far above the screen.
        // Give it the size of the text it actually draws (plus the title's room above) and scale that.
        Vector2 textSize = epithet.GetCombinedMinimumSize();
        float titleHeight = title.GetContentHeight();
        const float titleGap = 32f;
        float height = MathF.Max(60f, titleHeight + titleGap + MathF.Max(textSize.Y, 22f) + 8f);
        float width = Math.Clamp(MathF.Max(80f, textSize.X + 8f), 80f, 760f);
        float left = epithet.Position.X;
        epithet.AnchorLeft = 0f;
        epithet.AnchorTop = 0f;
        epithet.AnchorRight = 0f;
        epithet.AnchorBottom = 0f;
        epithet.Size = new Vector2(width, height);
        epithet.Position = new Vector2(left,
            PortraitAncientMath.EpithetLocalY(state.Canvas.Y, state.SafeInset, banner.GlobalPosition.Y, height));
        epithet.PivotOffset = new Vector2(0f, height);
        epithet.Scale = new Vector2(PortraitAncientMath.EpithetScale, PortraitAncientMath.EpithetScale);
        // Narrow the native 42 unit title gap in portrait so the enlarged pair clears the reward rows.
        title.OffsetTop = -titleGap;
        title.OffsetBottom = -titleGap;
        state.NameplateSettled = true;
        GD.Print($"Portrait ancient: nameplate settled, footer {PortraitAncientMath.NameplateFooter(state.Canvas.Y, state.SafeInset):F0} of {state.Canvas.Y:F0}.");
    }

    // ---- reward row internals -------------------------------------------------------------------------------------------

    private void SizeAncientOptions(Control options, float rowWidth, AncientOptionMetrics metrics, float knownHeight = -1f)
    {
        Capture(options, captureVisible: false);
        options.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        int separation = Mathf.RoundToInt(metrics.Separation);
        if (!options.HasThemeConstantOverride("separation") || options.GetThemeConstant("separation") != separation)
            options.AddThemeConstantOverride("separation", separation);
        float height = knownHeight;
        if (height < 0f)
        {
            var rows = new List<float>();
            foreach (Node child in options.GetChildren())
                if (child is Control row) rows.Add(row.CustomMinimumSize.Y);
            height = metrics.TotalHeight(rows);
        }
        var size = new Vector2(rowWidth, height);
        if (!options.CustomMinimumSize.IsEqualApprox(size)) options.CustomMinimumSize = size;
        if (!options.Size.IsEqualApprox(size)) options.Size = size;
    }

    private void StretchAncientOptionEffect(NinePatchRect effect)
    {
        Capture(effect, captureVisible: false);
        // The scene pins both edges to the centre of its 100 unit row: keep the artwork's overhang but anchor it to the
        // row's actual top and bottom.
        if (Mathf.IsEqualApprox(effect.AnchorTop, 0.5f) && Mathf.IsEqualApprox(effect.AnchorBottom, 0.5f))
        {
            float top = effect.OffsetTop + 50f;
            float bottom = effect.OffsetBottom - 50f;
            effect.AnchorTop = 0f;
            effect.AnchorBottom = 1f;
            effect.OffsetTop = top;
            effect.OffsetBottom = bottom;
        }
        TrackAncientPivot(effect);
    }

    private void TrackAncientPivot(Control control)
    {
        if (_ancientPivots.Add(control.GetInstanceId()))
            control.Connect(Control.SignalName.Resized, Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(control) && SafeAreaCanvas.IsPortrait) control.PivotOffset = control.Size / 2f;
            }));
        var pivot = control.Size / 2f;
        if (!control.PivotOffset.IsEqualApprox(pivot)) control.PivotOffset = pivot;
    }

    private void StyleAncientText(Control label, int size)
    {
        SetRichFontSize(label, size);
        ulong id = label.GetInstanceId();
        if (!_labelMaxFontSizes.ContainsKey(id) && label.Get("MaxFontSize").VariantType == Variant.Type.Int)
        {
            _labelMaxFontSizes[id] = label.Get("MaxFontSize").AsInt32();
            label.Set("MaxFontSize", size);
        }
        if (!_labelMinFontSizes.ContainsKey(id) && label.Get("MinFontSize").VariantType == Variant.Type.Int)
        {
            _labelMinFontSizes[id] = label.Get("MinFontSize").AsInt32();
            label.Set("MinFontSize", size);
        }
        if (label.Get("AutoSizeEnabled").VariantType != Variant.Type.Bool) return;
        if (!_ancientAutoSizes.TryGetValue(id, out bool autoSize))
        {
            autoSize = label.Get("AutoSizeEnabled").AsBool();
            _ancientAutoSizes[id] = autoSize;
        }
        if (autoSize) label.Set("AutoSizeEnabled", false);
    }

    private void SetAncientAutowrap(Control label, TextServer.AutowrapMode mode)
    {
        if (label is not RichTextLabel text) return;
        ulong id = label.GetInstanceId();
        if (!_ancientAutowraps.ContainsKey(id)) _ancientAutowraps[id] = text.AutowrapMode;
        if (text.AutowrapMode != mode) text.AutowrapMode = mode;
    }

    /// <summary>
    /// The native formatter emits bare [img] tags; rewrite the energy sprites for the larger text. The native option labels
    /// fill their text in as they become ready, so this is retried per page and is a no-op once the tags carry a size.
    /// </summary>
    private void RewriteAncientEnergyIcons(Control label)
    {
        if (label is not RichTextLabel text) return;
        string current = text.Text;
        if (!current.Contains("_energy_icon.png", StringComparison.Ordinal)) return;
        string rewritten = PortraitAncientMath.RewriteEnergyIcons(current, PortraitAncientMath.FontSize);
        if (rewritten != current) text.Text = rewritten;
    }

    /// <summary>Rewrites every reward row's and dialogue bubble's text on the open page.</summary>
    private void RewriteAncientPageIcons(AncientState state)
    {
        if (state.ContainerId == 0 || GodotObject.InstanceFromId(state.ContainerId) is not Control container) return;
        if (container.GetNodeOrNull<Control>("%OptionsContainer") is { } options)
            foreach (Node child in options.GetChildren())
                if (child is Control option && option.GetNodeOrNull<RichTextLabel>("%Text") is { } text)
                    RewriteAncientEnergyIcons(text);
        if (container.GetNodeOrNull<Control>("%DialogueContainer") is { } dialogue)
            foreach (Node child in dialogue.GetChildren())
                if (child is Control line && line.GetNodeOrNull<RichTextLabel>("DialogueContainer/TextContainer/Text") is { } text)
                    RewriteAncientEnergyIcons(text);
    }

    private void ForwardAncientLineRelease(Control line)
    {
        if (!line.HasSignal("Released") || _ancientLineForwards.ContainsKey(line.GetInstanceId())) return;
        if (AncientLayoutAbove(line) is not { } layout || layout.GetNodeOrNull<Control>("%DialogueHitbox") is not { } hitbox) return;
        ulong hitboxId = hitbox.GetInstanceId();
        Callable callable = Callable.From((Control _) =>
        {
            if (GodotObject.InstanceFromId(hitboxId) is Control box && GodotObject.IsInstanceValid(box) && box.Visible)
                box.EmitSignal("Released", box);
        });
        line.Connect("Released", callable);
        _ancientLineForwards[line.GetInstanceId()] = callable;
    }

    // ---- lookups --------------------------------------------------------------------------------------------------------

    private static Control? AncientLayoutAbove(Node node)
    {
        for (Node? parent = node.GetParent(); parent != null; parent = parent.GetParent())
            if (parent.GetType().FullName == AncientLayoutTypeName) return parent as Control;
        return null;
    }

    private AncientState? AncientStateAbove(Node node)
    {
        for (Node? parent = node.GetParent(); parent != null; parent = parent.GetParent())
            if (parent.GetType().FullName == AncientLayoutTypeName && _ancientStates.TryGetValue(parent.GetInstanceId(), out AncientState? state))
                return state;
        return null;
    }

    /// <summary>How many reward rows the event will show: its model's option list, or the rows already added.</summary>
    private static int AncientOptionCount(Control layout)
    {
        object? model = layout.GetType().GetField("_event", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(layout);
        if (model?.GetType().GetProperty("CurrentOptions")?.GetValue(model) is { } options &&
            options.GetType().GetProperty("Count")?.GetValue(options) is int count && count > 0)
            return count;
        if (layout.GetNodeOrNull<Control>("%OptionsContainer") is { } container && container.GetChildCount() > 0)
            return container.GetChildCount();
        return 3;
    }

    // ---- put back for landscape -----------------------------------------------------------------------------------------

    /// <summary>
    /// Takes the painting out of its clip, gives the hitbox its place back, drops the clip and fade, and restores every
    /// property the snapshot cannot hold (text auto-size, wrapping, the cached reward height and any line forwards).
    /// </summary>
    private void RestoreAncientLandscape()
    {
        foreach (AncientState state in _ancientStates.Values)
        {
            if (state.BgId != 0 && GodotObject.InstanceFromId(state.BgId) is Control background && GodotObject.IsInstanceValid(background))
            {
                if (state.BgParentId != 0 && GodotObject.InstanceFromId(state.BgParentId) is Node parent && GodotObject.IsInstanceValid(parent))
                {
                    if (background.GetParent() != parent)
                    {
                        background.GetParent()?.RemoveChild(background);
                        parent.AddChild(background);
                    }
                    int index = Math.Clamp(state.BgIndex, 0, Math.Max(0, parent.GetChildCount() - 1));
                    if (background.GetIndex() != index) parent.MoveChild(background, index);
                }
                if (background.HasMethod("OnWindowChange")) background.Call("OnWindowChange");
            }
            if (state.HitboxId != 0 && GodotObject.InstanceFromId(state.HitboxId) is Control hitbox && GodotObject.IsInstanceValid(hitbox) &&
                hitbox.GetParent() is Node column && GodotObject.IsInstanceValid(column) && state.HitboxIndex >= 0)
            {
                int index = Math.Clamp(state.HitboxIndex, 0, Math.Max(0, column.GetChildCount() - 1));
                if (hitbox.GetIndex() != index) column.MoveChild(hitbox, index);
            }
            RemoveAncientNode(state.ClipId);
            RemoveAncientNode(state.FadeId);
            if (state.ContentHeightCaptured && GodotObject.InstanceFromId(state.Id) is Control layout &&
                GodotObject.IsInstanceValid(layout) &&
                layout.GetType().GetField("_originalContentContainerHeight", BindingFlags.Instance | BindingFlags.NonPublic) is { } field)
                field.SetValue(layout, state.OriginalContentHeight);
        }
        _ancientStates.Clear();
        _ancientPivots.Clear();
        foreach ((ulong id, Callable callable) in _ancientLineForwards)
            if (GodotObject.InstanceFromId(id) is Node line && GodotObject.IsInstanceValid(line) && line.IsConnected("Released", callable))
                line.Disconnect("Released", callable);
        _ancientLineForwards.Clear();
        foreach ((ulong id, bool autoSize) in _ancientAutoSizes)
            if (GodotObject.InstanceFromId(id) is Control label && GodotObject.IsInstanceValid(label) && label.Get("AutoSizeEnabled").VariantType == Variant.Type.Bool)
                label.Set("AutoSizeEnabled", autoSize);
        _ancientAutoSizes.Clear();
        foreach ((ulong id, TextServer.AutowrapMode mode) in _ancientAutowraps)
            if (GodotObject.InstanceFromId(id) is RichTextLabel text && GodotObject.IsInstanceValid(text))
                text.AutowrapMode = mode;
        _ancientAutowraps.Clear();
        foreach ((ulong id, Control.MouseFilterEnum filter) in _ancientMouseFilters)
            if (GodotObject.InstanceFromId(id) is Control part && GodotObject.IsInstanceValid(part))
                part.MouseFilter = filter;
        _ancientMouseFilters.Clear();
    }

    private void PruneAncientInstances()
    {
        foreach (ulong id in _ancientStates.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _ancientStates.Remove(id);
        foreach (ulong id in _ancientLineForwards.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList())
            _ancientLineForwards.Remove(id);
        foreach (ulong id in _ancientAutoSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _ancientAutoSizes.Remove(id);
        foreach (ulong id in _ancientAutowraps.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _ancientAutowraps.Remove(id);
        foreach (ulong id in _ancientMouseFilters.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _ancientMouseFilters.Remove(id);
        _ancientPivots.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
    }

    private static void RemoveAncientNode(ulong id)
    {
        if (id == 0 || GodotObject.InstanceFromId(id) is not Node node || !GodotObject.IsInstanceValid(node)) return;
        node.GetParent()?.RemoveChild(node);
        node.QueueFree();
    }
}
