using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// The portrait composition of a standard event (<c>NEventLayout</c> on the game's default event scene, with an
/// illustration): the native picture cropped around its subject with its own VFX still attached, a dark fade into the choice
/// area, a translucent slot holding the title and the copy (set at phone size, and shrunk before it scrolls), and four
/// full-size choice rows pinned low. A drag on the copy or the choices scrolls instead of picking, and the act's rest-site
/// art covers the room behind, so no empty band shows.
/// <para>
/// The reference layout is a 1680-wide template for the same phone; every size here comes from
/// <see cref="PortraitEventMath" />, which converts it through points. The game's own nodes stay in place: the illustration is
/// moved into a clipping frame and the option column into a scroll, and both moves are reversed when Portrait Mode is turned
/// off. Everything else is the game's animation, its live particles and its own buttons.
/// </para>
/// </summary>
internal sealed partial class PortraitBetweenLayout
{
    public const string EventRoomTypeName = "MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom";
    public const string CombatEventLayoutTypeName = "MegaCrit.Sts2.Core.Nodes.Events.NCombatEventLayout";

    public const string EventArtClipName = "PortraitEventArt";
    public const string EventArtFadeName = "PortraitEventArtFade";
    public const string EventTextSlotName = "PortraitEventTextSlot";
    public const string EventChoicesScrollName = "PortraitEventChoices";
    public const string EventRoomCoverName = "PortraitEventRoomCover";

    private const string DefaultEventScenePath = "res://scenes/events/default_event_layout.tscn";
    private const string EventVeilTexture = "dark_overlay_events.png";
    private const string EventPanelName = "Panel";
    private const string EventCopyName = "Copy";
    private const string EventTextScrollName = "Text";
    private static readonly Color EventShade = new(0.042f, 0.034f, 0.040f);
    private static readonly Color EventCoverTint = new(0.30f, 0.30f, 0.30f);
    private static readonly string[] EventEffectNames = ["Outline", "Shadow", "RedFlash", "BlueFlash"];

    /// <summary>Where the bottom-anchored options of a live combat event end, in the compact shape (20 points).</summary>
    private const float CombatEventBottomInset = 58f;

    private static readonly Dictionary<string, Texture2D?> ActRestTextures = new(StringComparer.Ordinal);
    private static PropertyInfo? _eventRoomLayout;
    private static Type? _eventRoomLayoutOwner;
    private static readonly Dictionary<string, string> ActRestTexturePaths = new(StringComparer.Ordinal)
    {
        ["overgrowth"] = "res://images/rooms/rest_sites/overgrowth/overgrowth_rest_site_bg.png",
        ["underdocks"] = "res://images/rooms/rest_sites/underdocks/underdocks_rest_site_bg.png",
        ["hive"] = "res://images/rooms/rest_sites/hive/hive_rest_site_00.png",
        ["glory"] = "res://images/rooms/rest_sites/glory/glory_rest_site_00.png",
    };

    /// <summary>One composed event: the nodes the composition added, the game's nodes it moved, and what it measured.</summary>
    private sealed class EventState
    {
        public ulong ClipNode, ArtNode, FadeNode, SlotNode, PanelNode, TextNode, CopyNode, ChoicesNode, ContentNode, OptionsNode;
        public ulong TitleNode, DescriptionNode, SharedNode;
        public readonly List<EventRow> Rows = new(4);
        public Vector2 Canvas;
        public float Cap;
        public bool Compact;
        public bool Built;
        public bool Framed;
        public ulong LayoutNode;
        public ulong TextureId;
        public EventChoiceBand Band;
        public EventTextSlot TextSlot;
        public int Signature = int.MinValue;
        public bool PageDirty;
        public bool PageScheduled;
        public bool Tight;
        public bool Reduced;
        public float CopyInk;
        public bool ArtHidden;
        public int PendingFrames;
        public bool GivenUp;
    }

    private readonly struct EventRow
    {
        public EventRow(ulong id) => Id = id;
        public ulong Id { get; }
        public Vector2 Size { get; init; }
    }

    /// <summary>The game's own state of a node the composition changes, to put back in landscape.</summary>
    private sealed class EventRestore
    {
        public ulong Parent;
        public int Index;
        public bool Moved;
        public bool HasAutoSize;
        public bool AutoSize;
        public bool HasFit;
        public bool Fit;
        public bool HasScrollActive;
        public bool ScrollActive;
        public bool HasVAlign;
        public VerticalAlignment VAlign;
        public bool HasWrap;
        public TextServer.AutowrapMode Wrap;
        public bool HasFilter;
        public Control.MouseFilterEnum Filter;
        public bool HasExpand;
        public TextureRect.ExpandModeEnum Expand;
        public bool HasStretch;
        public TextureRect.StretchModeEnum Stretch;
    }

    private readonly Dictionary<ulong, EventState> _eventStates = new();
    private readonly Dictionary<ulong, EventRestore> _eventRestores = new();
    private readonly HashSet<ulong> _eventRooms = new();
    private readonly Dictionary<ulong, (ulong Layout, bool Wanted)> _eventRoomKinds = new();
    private readonly List<ulong> _eventRowIds = new(4);
    private readonly HashSet<ulong> _hiddenEventVeils = new();
    private readonly List<float> _eventRowRequired = new(4);
    private PhoneDragScroll? _eventDrag;

    // ---- room cover ----------------------------------------------------------------------------------------------------

    private void TrackEventRoom(Control room, float canvasWidth, float canvasHeight)
    {
        _eventRooms.Add(room.GetInstanceId());
        UpdateEventRoomCover(room, new Vector2(canvasWidth, canvasHeight));
    }

    /// <summary>
    /// The act's rest-site art behind the event room, dimmed, so the bands the native 16:9 art cannot reach are the
    /// environment rather than black. It sits under everything; a live combat event keeps its own underlay.
    /// </summary>
    private void UpdateEventRoomCover(Control room, Vector2 canvas)
    {
        Control? layout = EventLayoutOf(room);
        ulong layoutId = layout?.GetInstanceId() ?? 0;
        ulong roomId = room.GetInstanceId();
        if (!_eventRoomKinds.TryGetValue(roomId, out (ulong Layout, bool Wanted) kind) || kind.Layout != layoutId)
        {
            kind = (layoutId, layout != null && IsEventLayout(layout.GetType()) && !IsCombatEventLayout(layout.GetType()));
            _eventRoomKinds[roomId] = kind;
        }
        bool wanted = kind.Wanted;
        TextureRect? cover = room.GetNodeOrNull<TextureRect>(EventRoomCoverName);
        if (!wanted)
        {
            if (cover != null && cover.Visible) cover.Visible = false;
            return;
        }
        if (cover == null)
        {
            if (ActRestTexture() is not { } texture) return;
            cover = new TextureRect
            {
                Name = EventRoomCoverName,
                Texture = texture,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Modulate = EventCoverTint,
                ZIndex = -1,
                Position = Vector2.Zero,
                Size = canvas,
            };
            room.AddChild(cover);
            room.MoveChild(cover, 0);
        }
        else if (cover.Texture == null && ActRestTexture() is { } late)
        {
            cover.Texture = late;
        }
        if (!cover.Visible) cover.Visible = true;
        if (!cover.Size.IsEqualApprox(canvas))
        {
            cover.Position = Vector2.Zero;
            cover.Size = canvas;
        }
    }

    private static Control? EventLayoutOf(Control room)
    {
        Type type = room.GetType();
        if (_eventRoomLayoutOwner != type)
        {
            _eventRoomLayoutOwner = type;
            _eventRoomLayout = type.GetProperty("Layout", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }
        return _eventRoomLayout?.GetValue(room) as Control;
    }

    private static bool IsEventLayout(Type type)
    {
        for (Type? t = type; t != null; t = t.BaseType)
            if (t.FullName == EventLayoutTypeName) return true;
        return false;
    }

    private static bool IsCombatEventLayout(Type type)
    {
        for (Type? t = type; t != null; t = t.BaseType)
            if (t.FullName == CombatEventLayoutTypeName) return true;
        return false;
    }

    /// <summary>
    /// The current act's rest-site illustration for the cover behind an event (loaded once per act). Null when the act or
    /// its art is not known yet, so the caller can retry rather than leave a blank.
    /// </summary>
    internal static Texture2D? ActRestTexture()
    {
        string? act = CurrentActName();
        if (string.IsNullOrEmpty(act)) return null;
        if (ActRestTextures.TryGetValue(act!, out Texture2D? cached)) return cached;
        if (!ActRestTexturePaths.TryGetValue(act!, out string? path) || !ResourceLoader.Exists(path)) return null;
        Texture2D? texture = ResourceLoader.Load<Texture2D>(path);
        if (texture != null) ActRestTextures[act!] = texture;
        return texture;
    }

    private static string? CurrentActName()
    {
        try
        {
            Assembly game = typeof(MegaCrit.Sts2.Core.Nodes.NGame).Assembly;
            Type? manager = game.GetType("MegaCrit.Sts2.Core.Runs.RunManager");
            object? instance = manager?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            object? state = manager?.GetMethod("DebugOnlyGetState")?.Invoke(instance, null);
            object? act = state?.GetType().GetProperty("Act")?.GetValue(state);
            return act?.GetType().Name.ToLowerInvariant();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- standard event ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A standard event's page. The illustration is only framed once the game has assigned it, so an event without one keeps
    /// the game's own layout; everything else is arranged on the frame the picture appears and follows page changes.
    /// </summary>
    private void LayoutEventPortrait(Control layout, float capHeight, float canvasHeight, float canvasWidth)
    {
        if (layout.GetType().FullName != EventLayoutTypeName) return;
        _events.Add(layout.GetInstanceId());
        UpdateEventPortrait(layout, capHeight, new Vector2(canvasWidth, canvasHeight));
    }

    /// <summary>
    /// Called as a standard event's layout is added, before the game assigns its picture: the picture would show at the
    /// game's own crop for the frame or two until the composition frames it, so it is held back until that frame.
    /// Returns whether the picture was hidden.
    /// </summary>
    private bool HoldEventArtUntilFramed(Control layout)
    {
        if (layout.GetType().FullName != EventLayoutTypeName) return false;
        _events.Add(layout.GetInstanceId());
        if (layout.GetNodeOrNull<TextureRect>("%Portrait") is not { } portrait || portrait.Texture != null) return false;
        Capture(portrait, captureVisible: true);
        portrait.Visible = false;
        StateFor(layout).ArtHidden = true;
        return true;
    }

    private void RevealEventArt(EventState state, Control layout)
    {
        if (!state.ArtHidden) return;
        state.ArtHidden = false;
        if (layout.GetNodeOrNull<TextureRect>("%Portrait") is { } portrait) portrait.Visible = true;
    }

    private void UpdateEventPortrait(Control layout, float capHeight, Vector2 canvas)
    {
        if (layout.GetType().FullName != EventLayoutTypeName) return;
        if (canvas.X < 1f || canvas.Y < 1f) canvas = layout.GetViewport()?.GetVisibleRect().Size ?? canvas;
        EventState state = StateFor(layout);
        if (!state.Built && state.GivenUp) return;
        bool compact = PortraitEventMath.IsCompact(canvas.X, canvas.Y);
        if (!state.Built)
        {
            if (!CanComposeEvent(layout))
            {
                // The picture has not been assigned yet (an event without one never gets it): do not keep it hidden, and
                // stop looking after a second so the page costs nothing for the rest of the run.
                if (!state.GivenUp && ++state.PendingFrames > 60)
                {
                    state.GivenUp = true;
                    RevealEventArt(state, layout);
                }
                return;
            }
            BuildEventComposition(state, layout);
            if (!state.Built) return;
            state.Cap = capHeight;
            state.Canvas = canvas;
            state.Compact = compact;
            PlaceEventFrames(state, layout);
            RefreshEventPage(state);
            FrameEventArt(state);
            return;
        }
        if (!state.Canvas.IsEqualApprox(canvas) || !Mathf.IsEqualApprox(state.Cap, capHeight) || state.Compact != compact)
        {
            state.Canvas = canvas;
            state.Cap = capHeight;
            state.Compact = compact;
            PlaceEventFrames(state, layout);
            state.PageDirty = true;
        }
        FrameEventArt(state);
        RefreshEventPage(state);
        KeepEventRows(state);
        RefreshEventCopyInk(state);
    }

    /// <summary>
    /// The description's shaped height is measured before it has ever drawn; if the game shapes it differently, the copy is
    /// laid out again from the measured ink rather than leaving the panel a line short.
    /// </summary>
    private void RefreshEventCopyInk(EventState state)
    {
        if (state.CopyInk <= 0f || ResolveEventNode(state.DescriptionNode) is not RichTextLabel description || !description.Visible) return;
        if (MathF.Abs(description.GetContentHeight() - state.CopyInk) <= 1.5f) return;
        ApplyEventCopy(state);
    }

    private EventState StateFor(Control layout)
    {
        ulong id = layout.GetInstanceId();
        if (_eventStates.TryGetValue(id, out EventState? known)) return known;
        var created = new EventState();
        _eventStates[id] = created;
        return created;
    }

    /// <summary>
    /// The game's default event scene with an illustration: the only events whose subject, copy and choices the composition
    /// knows. A custom layout, a live combat event or an event without a picture keeps exactly what the game built.
    /// </summary>
    private static bool CanComposeEvent(Control layout)
    {
        if (layout.GetType().FullName != EventLayoutTypeName) return false;
        string scene = layout.SceneFilePath;
        if (!string.IsNullOrEmpty(scene) && scene != DefaultEventScenePath) return false;
        return layout.GetNodeOrNull<TextureRect>("%Portrait")?.Texture != null
            && layout.GetNodeOrNull<Control>("%OptionsContainer")?.GetParent() is VBoxContainer
            && layout.GetNodeOrNull<Label>("%Title") != null
            && layout.GetNodeOrNull<RichTextLabel>("%EventDescription") != null;
    }

    private void BuildEventComposition(EventState state, Control layout)
    {
        TextureRect? portrait = layout.GetNodeOrNull<TextureRect>("%Portrait");
        Control? options = layout.GetNodeOrNull<Control>("%OptionsContainer");
        Label? title = layout.GetNodeOrNull<Label>("%Title");
        RichTextLabel? description = layout.GetNodeOrNull<RichTextLabel>("%EventDescription");
        if (portrait == null || options == null || options.GetParent() is not VBoxContainer content || title == null || description == null)
            return;

        // The illustration: a full-width clipping frame, the game's picture scaled into it and its own VFX following it.
        var clip = new Control
        {
            Name = EventArtClipName,
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            FocusMode = Control.FocusModeEnum.None,
        };
        layout.AddChild(clip);
        layout.MoveChild(clip, 0);
        state.ClipNode = clip.GetInstanceId();
        MoveEventNode(portrait, clip);
        Capture(portrait, captureVisible: false);
        RecordEventNode(portrait);
        portrait.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        portrait.PivotOffset = Vector2.Zero;
        portrait.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        portrait.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
        state.ArtNode = portrait.GetInstanceId();

        // The fade from the picture's lower edge into the dark choice area.
        var fade = MakeEventFade();
        layout.AddChild(fade);
        layout.MoveChild(fade, 1);
        state.FadeNode = fade.GetInstanceId();

        // The copy: translucent panel, a scroll for the long passages, and the game's own title and description inside.
        var slot = new Control { Name = EventTextSlotName, MouseFilter = Control.MouseFilterEnum.Ignore };
        var panel = MakeEventPanel();
        var textScroll = MakeEventScroll(EventTextScrollName, followFocus: false);
        var copy = new Control
        {
            Name = EventCopyName,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        layout.AddChild(slot);
        slot.AddChild(panel);
        panel.AddChild(textScroll);
        textScroll.AddChild(copy);
        state.SlotNode = slot.GetInstanceId();
        state.PanelNode = panel.GetInstanceId();
        state.TextNode = textScroll.GetInstanceId();
        state.CopyNode = copy.GetInstanceId();
        state.TitleNode = title.GetInstanceId();
        state.DescriptionNode = description.GetInstanceId();
        MoveEventCopy(title, copy);
        MoveEventCopy(description, copy);
        if (layout.GetNodeOrNull<Label>("%SharedEventLabel") is { } shared)
        {
            state.SharedNode = shared.GetInstanceId();
            MoveEventCopy(shared, copy);
        }
        RecordEventNode(description);
        description.FitContent = false;
        description.ScrollActive = false;
        description.VerticalAlignment = VerticalAlignment.Top;
        description.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        description.MouseFilter = Control.MouseFilterEnum.Ignore;
        description.Set("AutoSizeEnabled", false);

        // The choices: the game's own option column inside a scroll that always reserves four phone-sized rows.
        var choices = MakeEventScroll(EventChoicesScrollName, followFocus: true);
        layout.AddChild(choices);
        state.ChoicesNode = choices.GetInstanceId();
        MoveEventNode(content, choices);
        RecordEventNode(content);
        content.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        content.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        content.AddThemeConstantOverride("separation", 0);
        if (content.GetNodeOrNull<Control>("Spacer") is { } spacer)
        {
            Capture(spacer, captureVisible: true);
            spacer.Visible = false;
        }
        state.ContentNode = content.GetInstanceId();
        state.OptionsNode = options.GetInstanceId();
        Capture(options, captureVisible: false);
        RecordEventNode(options);
        RecordEventNode(choices);

        state.Built = true;
        state.LayoutNode = layout.GetInstanceId();
        _eventDrag ??= new PhoneDragScroll();
        if (layout.GetWindow() is { } window) _eventDrag.Attach(window);
        _eventDrag.SetTargets(textScroll, choices, layout);
        GD.Print($"Portrait event: {layout.SceneFilePath.GetFile()} composed for {layout.Size}.");
    }

    private static TextureRect MakeEventFade()
    {
        var gradient = new Gradient
        {
            Offsets = [0f, 0.3f, 1f],
            Colors = [new Color(EventShade, 0f), EventShade, EventShade],
        };
        var texture = new GradientTexture2D
        {
            Gradient = gradient,
            Width = 4,
            Height = 512,
            FillFrom = new Vector2(0f, 0f),
            FillTo = new Vector2(0f, 1f),
        };
        return new TextureRect
        {
            Name = EventArtFadeName,
            Texture = texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
    }

    private static Panel MakeEventPanel()
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.035f, 0.028f, 0.029f, PortraitEventMath.PanelAlpha),
            CornerDetail = 8,
        };
        style.SetCornerRadiusAll(PortraitEventMath.PanelCornerRadius);
        var panel = new Panel { Name = EventPanelName, MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", style);
        return panel;
    }

    private static ScrollContainer MakeEventScroll(string name, bool followFocus)
    {
        var scroll = new ScrollContainer
        {
            Name = name,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            FollowFocus = followFocus,
        };
        scroll.GetVScrollBar().CustomMinimumSize = new Vector2(PortraitEventMath.ScrollbarWidth, 0f);
        return scroll;
    }

    private void MoveEventCopy(Control label, Control copy)
    {
        RecordEventNode(label);
        MoveEventNode(label, copy);
        label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        label.CustomMinimumSize = Vector2.Zero;
        label.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        if (label is Label plain) plain.AutowrapMode = TextServer.AutowrapMode.WordSmart;
    }

    private void MoveEventNode(Control node, Node parent)
    {
        EventRestore bits = EventBits(node);
        bits.Moved = true;
        node.Reparent(parent, keepGlobalTransform: false);
    }

    /// <summary>Frame the picture and the fade, the copy's slot and the choice band from the canvas's shape.</summary>
    private void PlaceEventFrames(EventState state, Control layout)
    {
        Control? clip = ResolveEventNode(state.ClipNode);
        TextureRect? fade = ResolveEventNode(state.FadeNode) as TextureRect;
        Control? slot = ResolveEventNode(state.SlotNode);
        Control? choices = ResolveEventNode(state.ChoicesNode);
        if (clip == null || fade == null || slot == null || choices == null) return;

        float bandTop = state.Cap + PortraitBands.TopBarHeight;
        EventChoiceBand band = PortraitEventMath.ChoiceBand(state.Canvas.Y, bandTop, state.Compact, state.Tight);
        EventTextSlot textSlot = PortraitEventMath.TextSlot(state.Canvas.Y, bandTop, band.Top);
        state.Band = band;
        state.TextSlot = textSlot;
        float width = MathF.Max(0f, state.Canvas.X - 2f * PortraitEventMath.SideInset);
        float artBottom = PortraitEventMath.ArtBottom(band.Top);
        Vector2 origin = layout.GlobalPosition;

        Vector2 clipSize = new(state.Canvas.X, MathF.Max(0f, artBottom - bandTop));
        Vector2 clipAt = new(-origin.X, bandTop - origin.Y);
        if (!clip.Size.IsEqualApprox(clipSize))
        {
            clip.Size = clipSize;
            state.Framed = false;
        }
        if (!clip.Position.IsEqualApprox(clipAt)) clip.Position = clipAt;

        float fadeFrom = PortraitEventMath.FadeTop(artBottom, state.Canvas.Y);
        Vector2 fadeAt = new(-origin.X, fadeFrom - origin.Y);
        Vector2 fadeSize = new(state.Canvas.X, MathF.Max(0f, state.Canvas.Y - fadeFrom));
        if (!fade.Position.IsEqualApprox(fadeAt)) fade.Position = fadeAt;
        if (!fade.Size.IsEqualApprox(fadeSize)) fade.Size = fadeSize;
        UpdateEventFade(fade, artBottom, fadeFrom);

        Vector2 slotSize = new(width, textSlot.Height);
        Vector2 slotAt = new(PortraitEventMath.SideInset - origin.X, textSlot.Top - origin.Y);
        if (!slot.Size.IsEqualApprox(slotSize)) slot.Size = slotSize;
        if (!slot.Position.IsEqualApprox(slotAt)) slot.Position = slotAt;

        Vector2 choiceSize = new(width, MathF.Max(0f, band.Bottom - band.Top));
        Vector2 choiceAt = new(PortraitEventMath.SideInset - origin.X, band.Top - origin.Y);
        if (!choices.Size.IsEqualApprox(choiceSize)) choices.Size = choiceSize;
        if (!choices.Position.IsEqualApprox(choiceAt)) choices.Position = choiceAt;
    }

    private static void UpdateEventFade(TextureRect fade, float artBottom, float from)
    {
        if (fade.Texture is not GradientTexture2D texture) return;
        float height = MathF.Max(1f, fade.Size.Y);
        float at = Math.Clamp((artBottom - from) / height, 0f, 1f);
        Gradient gradient = texture.Gradient;
        if (gradient.Offsets.Length == 3 && MathF.Abs(gradient.Offsets[1] - at) < 0.002f) return;
        gradient.Offsets = [0f, at, 1f];
        gradient.Colors = [new Color(EventShade, 0f), EventShade, EventShade];
    }

    /// <summary>
    /// Scale the game's picture, and the VFX hung from it, so the subject stays in view and the frame is fully covered.
    /// The desktop copy-dimming particle is hidden: the fade and the panel replace it, and in the portrait crop its edge
    /// shows. The event's other particles and lights stay alive.
    /// </summary>
    private void FrameEventArt(EventState state)
    {
        if (ResolveEventNode(state.ArtNode) is not TextureRect art) return;
        Texture2D? texture = art.Texture;
        if (texture == null) return;
        ulong textureId = texture.GetInstanceId();
        if (state.Framed && state.TextureId == textureId) return;
        if (ResolveEventNode(state.ClipNode) is not Control clip || clip.Size.X < 1f || clip.Size.Y < 1f) return;

        EventFocus focus = PortraitEventMath.ArtFocus(texture.ResourcePath.GetFile().GetBaseName());
        EventArtCrop crop = PortraitEventMath.ArtCrop(
            clip.Size.X, clip.Size.Y, PortraitEventMath.SourceWidth, PortraitEventMath.SourceHeight, focus);
        art.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        art.PivotOffset = Vector2.Zero;
        art.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        art.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
        art.Size = new Vector2(PortraitEventMath.SourceWidth, PortraitEventMath.SourceHeight);
        art.Scale = Vector2.One * crop.Scale;
        art.Position = new Vector2(crop.X, crop.Y);
        state.Framed = true;
        state.TextureId = textureId;
        if (state.ArtHidden)
        {
            state.ArtHidden = false;
            art.Visible = true;
        }
        HideEventVeil(art);
    }

    private void HideEventVeil(Node node)
    {
        if (node is GpuParticles2D particles)
        {
            Texture2D? texture = particles.Texture;
            if (string.Equals(texture?.ResourcePath.GetFile(), EventVeilTexture, StringComparison.Ordinal) && particles.Visible)
            {
                _hiddenEventVeils.Add(particles.GetInstanceId());
                particles.Visible = false;
            }
        }
        foreach (Node child in node.GetChildren()) HideEventVeil(child);
    }

    /// <summary>
    /// A page change moves the game's copy into the slot and its options into the pinned rows. The rows try their full size
    /// first, then one touch target each, and only then scroll.
    /// </summary>
    private void RefreshEventPage(EventState state)
    {
        Control? options = ResolveEventNode(state.OptionsNode);
        if (options == null || ResolveEventNode(state.DescriptionNode) is not RichTextLabel description) return;
        int signature = (options.GetChildCount() * 397) ^ (description.GetTotalCharacterCount() * 31)
            ^ (state.Rows.Count << 5) ^ ((ResolveEventNode(state.TitleNode) as Label)?.Text.Length ?? 0);
        if (!state.PageDirty && signature == state.Signature) return;
        bool pageChanged = signature != state.Signature;
        state.Signature = signature;
        state.PageDirty = false;
        ApplyEventPage(state, pageChanged);
    }

    private void ScheduleEventPage(EventState state, Control layout)
    {
        state.PageDirty = true;
        if (state.PageScheduled) return;
        state.PageScheduled = true;
        ulong layoutId = layout.GetInstanceId();
        Callable.From(() =>
        {
            if (!_eventStates.TryGetValue(layoutId, out EventState? known)) return;
            known.PageScheduled = false;
            if (ResolveEventNode(layoutId) is { } control && control.IsInsideTree())
                UpdateEventPortrait(control, known.Cap, known.Canvas);
        }).CallDeferred();
    }

    private void ApplyEventPage(EventState state, bool newPage)
    {
        if (newPage)
        {
            state.Tight = false;
            state.Reduced = false;
            // A new page always starts from the full-row band, so a page that follows a tight one gets the full-row
            // reserve back.
            ReframeEventPage(state);
        }
        CollectEventRows(state);
        if (!LayoutEventOptions(state, state.Tight, state.Reduced) && !state.Tight)
        {
            state.Tight = true;
            // The tight rows are shorter than the band reserved for full rows; recompute the band so the rows still end
            // at the band bottom and the height they free goes to the illustration, not to an empty strip under them.
            ReframeEventPage(state);
            LayoutEventOptions(state, tight: true, state.Reduced);
        }
        if (!LayoutEventOptions(state, state.Tight, state.Reduced) && !state.Reduced)
        {
            state.Reduced = true;
            LayoutEventOptions(state, state.Tight, tightText: true);
        }
        ApplyEventCopy(state);
        if (newPage) ResetEventScrolls(state);
    }

    /// <summary>
    /// Recompute the art frame, the copy slot and the choice band for the current row size, then re-crop the picture to
    /// the new frame. Called when a page's row size changes, since the band and slot were measured for the other size.
    /// </summary>
    private void ReframeEventPage(EventState state)
    {
        if (ResolveEventNode(state.LayoutNode) is not Control layout) return;
        PlaceEventFrames(state, layout);
        FrameEventArt(state);
    }

    private void ResetEventScrolls(EventState state)
    {
        if (ResolveEventNode(state.TextNode) is ScrollContainer text) text.ScrollVertical = 0;
        if (ResolveEventNode(state.ChoicesNode) is ScrollContainer choices) choices.ScrollVertical = 0;
    }

    // ---- choices -------------------------------------------------------------------------------------------------------

    private void CollectEventRows(EventState state)
    {
        _eventRowIds.Clear();
        if (ResolveEventNode(state.OptionsNode) is { } options)
        {
            foreach (Node child in options.GetChildren())
            {
                if (child is Control row && child.GetType().FullName == EventOptionTypeName)
                    _eventRowIds.Add(row.GetInstanceId());
            }
        }
        state.Rows.Clear();
        foreach (ulong id in _eventRowIds) state.Rows.Add(new EventRow(id));
    }

    /// <summary>Size the rows, measure their text and report whether they fit the reserved band.</summary>
    private bool LayoutEventOptions(EventState state, bool tight, bool tightText)
    {
        Control? options = ResolveEventNode(state.OptionsNode);
        Control? content = ResolveEventNode(state.ContentNode);
        if (options == null || content == null) return true;
        float width = MathF.Max(0f, state.Canvas.X - 2f * PortraitEventMath.SideInset);
        float minHeight = tight ? PortraitEventMath.RowHeightTight
            : state.Compact ? PortraitEventMath.RowHeightCompact : PortraitEventMath.RowHeightTall;
        float insetY = tight ? PortraitEventMath.OptionInsetYTight
            : state.Compact ? PortraitEventMath.OptionInsetYCompact : PortraitEventMath.OptionInsetYTall;
        int font = tight ? PortraitEventMath.OptionTightFont : PortraitEventMath.TextFont;
        int gap = tight ? PortraitEventMath.GapTight : state.Compact ? PortraitEventMath.GapCompact : PortraitEventMath.GapTall;
        float textWidth = MathF.Max(0f, PortraitEventMath.OptionTextWidth(width) -
            (tightText ? PortraitEventMath.ScrollbarGutter : 0f));

        content.AddThemeConstantOverride("separation", 0);
        options.AddThemeConstantOverride("separation", gap);
        options.CustomMinimumSize = new Vector2(width, 0f);
        options.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        // Measure each row at its final width: a label set to the row's width reports its wrapped height at once.
        _eventRowRequired.Clear();
        foreach (EventRow entry in state.Rows)
        {
            if (ResolveEventNode(entry.Id) is not Control row) continue;
            LayoutEventRow(row, width, minHeight, font);
            _eventRowRequired.Add(RowTextHeight(row, textWidth, insetY, minHeight));
        }
        bool uniform = !state.Compact && !tight;
        float tallest = PortraitEventMath.UniformRowHeight(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_eventRowRequired), minHeight);
        float total = 0f;
        int seen = 0;
        for (int i = 0; i < state.Rows.Count; i++)
        {
            if (ResolveEventNode(state.Rows[i].Id) is not Control row) continue;
            int measured = Math.Min(seen, _eventRowRequired.Count - 1);
            if (measured < 0) continue;
            float height = uniform ? tallest : _eventRowRequired[measured];
            SetEventRowSize(row, width, height, insetY, textWidth);
            total += height;
            seen++;
        }
        if (uniform) total = PortraitEventMath.ChoiceRowsHeight(tallest, gap, seen);
        else if (seen > 1) total += (seen - 1) * gap;
        return total <= state.Band.Bottom - state.Band.Top + 0.5f;
    }

    private void LayoutEventRow(Control row, float width, float minHeight, int font)
    {
        Capture(row, captureVisible: false);
        row.CustomMinimumSize = new Vector2(width, minHeight);
        row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (row.GetNodeOrNull<Control>("%Text") is { } label)
        {
            if (label is RichTextLabel rich) rich.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            SetEventFont(label, font, rich: label is RichTextLabel);
            Capture(label, captureVisible: false);
            // Placed by its own rect (no anchors), so a measurement at a known width does not drift when the row grows.
            label.AnchorLeft = 0f;
            label.AnchorTop = 0f;
            label.AnchorRight = 0f;
            label.AnchorBottom = 0f;
        }
        StretchEventOptionEffects(row);
    }

    /// <summary>The text's wrapped height at this width, never under the row's minimum.</summary>
    private static float RowTextHeight(Control row, float textWidth, float insetY, float minHeight)
    {
        if (row.GetNodeOrNull<Control>("%Text") is not RichTextLabel label) return minHeight;
        label.Size = new Vector2(textWidth, label.Size.Y);
        return PortraitEventMath.OptionRowHeight(label.GetContentHeight(), insetY, minHeight);
    }

    /// <summary>Give the row its final height and put its text the insets inside it.</summary>
    private static void SetEventRowSize(Control row, float width, float height, float insetY, float textWidth)
    {
        row.CustomMinimumSize = new Vector2(width, height);
        row.Size = new Vector2(width, height);
        row.PivotOffset = new Vector2(width, height) * 0.5f;
        if (row.GetNodeOrNull<Control>("%Text") is not { } label) return;
        label.Position = new Vector2(PortraitEventMath.OptionInsetX, insetY);
        label.Size = new Vector2(textWidth, MathF.Max(0f, height - 2f * insetY));
    }

    private void StretchEventOptionEffects(Control row)
    {
        foreach (string name in EventEffectNames)
        {
            if (row.GetNodeOrNull<Control>(name) is not { } effect) continue;
            Capture(effect, captureVisible: false);
            if (Mathf.IsEqualApprox(effect.AnchorTop, 0.5f) && Mathf.IsEqualApprox(effect.AnchorBottom, 0.5f))
            {
                // The scene pins both edges to the centre of its 100-unit button; keep the artwork's overhang but anchor
                // it to the actual row edges, so the flash and the outline follow a taller row.
                float top = effect.OffsetTop + 50f;
                float bottom = effect.OffsetBottom - 50f;
                effect.AnchorTop = 0f;
                effect.AnchorBottom = 1f;
                effect.OffsetTop = top;
                effect.OffsetBottom = bottom;
            }
        }
    }

    /// <summary>Keep the rows' pivots centred on their real size, so the game's own press and confirm animations stay put.</summary>
    private void KeepEventRows(EventState state)
    {
        if (state.Rows.Count == 0) return;
        for (int i = 0; i < state.Rows.Count; i++)
        {
            EventRow entry = state.Rows[i];
            if (ResolveEventNode(entry.Id) is not Control row) continue;
            if (row.Size.IsEqualApprox(entry.Size)) continue;
            var pivot = row.Size * 0.5f;
            if (!row.PivotOffset.IsEqualApprox(pivot)) row.PivotOffset = pivot;
            foreach (string name in EventEffectNames)
                if (row.GetNodeOrNull<Control>(name) is { } effect && effect.Size.X > 0f && effect.Size.Y > 0f)
                    effect.PivotOffset = effect.Size * 0.5f;
            state.Rows[i] = new EventRow(entry.Id) { Size = row.Size };
        }
    }

    /// <summary>An option row added to a standard event: sized before the game's own column measures it.</summary>
    private bool PrepareEventOption(Control option, Control layout)
    {
        // The game assigns the picture before it adds the options; an event without one keeps the game's own rows.
        if (layout.GetNodeOrNull<TextureRect>("%Portrait")?.Texture == null) return false;
        EventState state = StateFor(layout);
        // The game clears the page's rows before it adds the next page's first one: start every page at full size.
        if (layout.GetNodeOrNull<Control>("%OptionsContainer") is { } rows && rows.GetChildCount() <= 1)
        {
            state.Tight = false;
            state.Reduced = false;
        }
        float width = state.Canvas.X > 0f ? state.Canvas.X - 2f * PortraitEventMath.SideInset : 1080f - 2f * PortraitEventMath.SideInset;
        float minHeight = state.Tight ? PortraitEventMath.RowHeightTight
            : state.Compact ? PortraitEventMath.RowHeightCompact : PortraitEventMath.RowHeightTall;
        float insetY = state.Tight ? PortraitEventMath.OptionInsetYTight
            : state.Compact ? PortraitEventMath.OptionInsetYCompact : PortraitEventMath.OptionInsetYTall;
        int font = state.Tight ? PortraitEventMath.OptionTightFont : PortraitEventMath.TextFont;
        LayoutEventRow(option, width, minHeight, font);
        SetEventRowSize(option, width, minHeight, insetY,
            MathF.Max(0f, PortraitEventMath.OptionTextWidth(width) - (state.Reduced ? PortraitEventMath.ScrollbarGutter : 0f)));
        SyncEventColumn(state, width, minHeight, insetY);
        if (state.Built) ScheduleEventPage(state, layout);
        return true;
    }

    /// <summary>
    /// Give every row in the column its portrait size the moment one is added, before the container's first sort, so the
    /// rows' places and heights are already final on the frame they are first drawn; only a page pass that measures taller
    /// text may change them afterwards.
    /// </summary>
    private void SyncEventColumn(EventState state, float width, float minHeight, float insetY)
    {
        if (ResolveEventNode(state.OptionsNode) is not { } options || ResolveEventNode(state.ContentNode) is not { } content) return;
        int gap = state.Tight ? PortraitEventMath.GapTight : state.Compact ? PortraitEventMath.GapCompact : PortraitEventMath.GapTall;
        float textWidth = MathF.Max(0f, PortraitEventMath.OptionTextWidth(width) - (state.Reduced ? PortraitEventMath.ScrollbarGutter : 0f));
        content.AddThemeConstantOverride("separation", 0);
        options.AddThemeConstantOverride("separation", gap);
        options.CustomMinimumSize = new Vector2(width, 0f);
        options.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        int index = 0;
        foreach (Node child in options.GetChildren())
        {
            if (child is not Control row || child.GetType().FullName != EventOptionTypeName) continue;
            row.CustomMinimumSize = new Vector2(width, minHeight);
            row.Size = new Vector2(width, minHeight);
            // The column's container sorts at the end of the frame; give the row its final place now so the first drawn
            // frame already has it (the sort then writes the same values).
            row.Position = new Vector2(0f, index * (minHeight + gap));
            row.PivotOffset = new Vector2(width, minHeight) * 0.5f;
            if (row.GetNodeOrNull<Control>("%Text") is { } label)
            {
                label.Position = new Vector2(PortraitEventMath.OptionInsetX, insetY);
                label.Size = new Vector2(textWidth, MathF.Max(0f, minHeight - 2f * insetY));
            }
            index++;
        }
    }

    // ---- live combat events ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A live combat event (Punch Off, The Lantern Key) keeps its tableau; only in the compact 9:16 shape does its option
    /// stack become a bottom-anchored, phone-sized column. The taller shape leaves the game's composition untouched.
    /// </summary>
    private void LayoutCombatEventPortrait(Control layout, float capHeight, float canvasHeight, float canvasWidth)
    {
        if (!PortraitEventMath.IsCompact(canvasWidth, canvasHeight)) return;
        if (layout.GetNodeOrNull<Control>("VBoxContainer") is { } box)
        {
            // Anchored to the bottom of the screen and growing up as the options are added, above the home indicator.
            Capture(box, captureVisible: false);
            box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
            box.OffsetLeft = PortraitEventMath.SideInset;
            box.OffsetRight = -PortraitEventMath.SideInset;
            box.OffsetBottom = -CombatEventBottomInset;
            box.GrowVertical = Control.GrowDirection.Begin;
        }
        if (layout.GetNodeOrNull<Control>("%OptionsContainer") is { } options)
            foreach (Node child in options.GetChildren())
                if (child is Control row && child.GetType().FullName == EventOptionTypeName)
                    LayoutCombatEventRow(row, canvasWidth);
    }

    private bool PrepareCombatEventOption(Control option, Control layout)
    {
        Vector2 canvas = layout.GetViewport()?.GetVisibleRect().Size ?? Vector2.Zero;
        if (!PortraitEventMath.IsCompact(canvas.X, canvas.Y)) return false;
        LayoutCombatEventRow(option, canvas.X);
        return true;
    }

    /// <summary>The option rows of the layout the row belongs to, if it is one of the two layouts the composition owns.</summary>
    private bool ApplyEventOption(Control option)
    {
        if (FindEventLayout(option, EventLayoutTypeName) is { } standard) return PrepareEventOption(option, standard);
        if (FindEventLayout(option, CombatEventLayoutTypeName) is { } combat) return PrepareCombatEventOption(option, combat);
        return false;
    }

    private static Control? FindEventLayout(Control node, string typeName)
    {
        for (Node? parent = node.GetParent(); parent != null; parent = parent.GetParent())
            if (parent.GetType().FullName == typeName) return parent as Control;
        return null;
    }

    private void LayoutCombatEventRow(Control row, float canvasWidth)
    {
        float width = MathF.Max(0f, canvasWidth - 2f * PortraitEventMath.SideInset);
        LayoutEventRow(row, width, PortraitEventMath.RowHeightTight, PortraitEventMath.OptionTightFont);
        SetEventRowSize(row, width, PortraitEventMath.RowHeightTight, PortraitEventMath.OptionInsetYTight,
            PortraitEventMath.OptionTextWidth(width));
    }

    // ---- copy ----------------------------------------------------------------------------------------------------------

    private void ApplyEventCopy(EventState state)
    {
        Control? slot = ResolveEventNode(state.SlotNode);
        Panel? panel = ResolveEventNode(state.PanelNode) as Panel;
        ScrollContainer? scroll = ResolveEventNode(state.TextNode) as ScrollContainer;
        Control? copy = ResolveEventNode(state.CopyNode);
        Label? title = ResolveEventNode(state.TitleNode) as Label;
        RichTextLabel? description = ResolveEventNode(state.DescriptionNode) as RichTextLabel;
        Label? shared = ResolveEventNode(state.SharedNode) as Label;
        if (slot == null || panel == null || scroll == null || copy == null || description == null) return;
        float width = MathF.Max(0f, state.Canvas.X - 2f * PortraitEventMath.SideInset);
        float copyWidth = MathF.Max(0f, width - 2f * PortraitEventMath.PanelInsetX);
        float limit = MathF.Max(0f, state.TextSlot.Height - 2f * PortraitEventMath.PanelInsetY);
        int font = PortraitEventMath.TextFont;
        float required = LayoutEventCopyLabels(copy, title, description, shared, copyWidth, font);
        while (required > limit + 0.5f && font > PortraitEventMath.MinTextFont)
        {
            font = PortraitEventMath.NextTextFont(font);
            required = LayoutEventCopyLabels(copy, title, description, shared, copyWidth, font);
        }
        if (required > limit + 0.5f)
        {
            required = LayoutEventCopyLabels(copy, title, description, shared, MathF.Max(0f, copyWidth - PortraitEventMath.ScrollbarGutter), font);
        }
        EventTextPanel fitted = PortraitEventMath.TextPanel(required, limit);
        float panelHeight = fitted.Height + 2f * PortraitEventMath.PanelInsetY;
        panel.Position = new Vector2(0f, MathF.Max(0f, state.TextSlot.Height - panelHeight));
        panel.Size = new Vector2(width, panelHeight);
        scroll.Position = new Vector2(PortraitEventMath.PanelInsetX, PortraitEventMath.PanelInsetY);
        scroll.Size = new Vector2(copyWidth, fitted.Height);
        state.CopyInk = description.GetContentHeight();
    }

    /// <summary>Place the copy's labels one under the other and return the stack's height.</summary>
    private float LayoutEventCopyLabels(Control copy, Label? title, RichTextLabel description, Label? shared, float width, int font)
    {
        float y = 0f;
        if (title != null && title.Visible)
        {
            // The title keeps the full size while the copy shrinks to fit: it stays the page's heading, and a slot that
            // has to give up its heading to fit a long passage reads worse than one whose body got smaller.
            SetEventFont(title, PortraitEventMath.TitleFont(PortraitEventMath.TextFont), rich: false);
            title.Position = new Vector2(0f, y);
            title.Size = new Vector2(width, title.Size.Y);
            float height = title.GetMinimumSize().Y + PortraitEventMath.InkGutter;
            title.Size = new Vector2(width, height);
            y += height + PortraitEventMath.CopyGap;
        }
        if (description.Visible)
        {
            SetEventFont(description, font, rich: true);
            description.Size = new Vector2(width, description.Size.Y);
            float height = description.GetContentHeight();
            description.Position = new Vector2(0f, y);
            description.Size = new Vector2(width, height + PortraitEventMath.InkGutter);
            y += height + PortraitEventMath.InkGutter + PortraitEventMath.CopyGap;
        }
        if (shared != null && shared.Visible)
        {
            SetEventFont(shared, PortraitEventMath.SharedFont(font), rich: false);
            shared.Position = new Vector2(0f, y);
            shared.Size = new Vector2(width, shared.Size.Y);
            float height = shared.GetMinimumSize().Y + PortraitEventMath.InkGutter;
            shared.Size = new Vector2(width, height);
            y += height + PortraitEventMath.CopyGap;
        }
        float required = MathF.Max(0f, y - PortraitEventMath.CopyGap);
        copy.CustomMinimumSize = new Vector2(width, required);
        copy.Size = new Vector2(width, required);
        return required;
    }

    /// <summary>Set one label's font at phone size and stop it sizing itself, keeping the game's own for landscape.</summary>
    private void SetEventFont(Control label, int size, bool rich)
    {
        RecordEventNode(label);
        ExtensionLimits(label, size);
        if (rich) SetRichFontSize(label, size);
        else SetLabelFontSize(label, size);
        if (label.Get("AutoSizeEnabled").VariantType == Variant.Type.Bool) label.Set("AutoSizeEnabled", false);
    }

    private void ExtensionLimits(Control label, int size)
    {
        ulong id = label.GetInstanceId();
        Variant max = label.Get("MaxFontSize");
        if (max.VariantType == Variant.Type.Int)
        {
            if (!_labelMaxFontSizes.ContainsKey(id)) _labelMaxFontSizes[id] = max.AsInt32();
            label.Set("MaxFontSize", size);
        }
        Variant min = label.Get("MinFontSize");
        if (min.VariantType == Variant.Type.Int)
        {
            if (!_labelMinFontSizes.ContainsKey(id)) _labelMinFontSizes[id] = min.AsInt32();
            label.Set("MinFontSize", size);
        }
    }

    // ---- shared state --------------------------------------------------------------------------------------------------

    private static Control? ResolveEventNode(ulong id) =>
        id == 0 ? null : GodotObject.InstanceFromId(id) as Control;

    /// <summary>The game's own state of a node the event composition touches, captured before the first change.</summary>
    private EventRestore EventBits(Control control)
    {
        ulong id = control.GetInstanceId();
        if (_eventRestores.TryGetValue(id, out EventRestore? known)) return known;
        var bits = new EventRestore
        {
            Parent = control.GetParent()?.GetInstanceId() ?? 0,
            Index = control.GetIndex(),
            HasFilter = true,
            Filter = control.MouseFilter,
        };
        Variant autoSize = control.Get("AutoSizeEnabled");
        if (autoSize.VariantType == Variant.Type.Bool)
        {
            bits.HasAutoSize = true;
            bits.AutoSize = autoSize.AsBool();
        }
        if (control is RichTextLabel rich)
        {
            bits.HasFit = true;
            bits.Fit = rich.FitContent;
            bits.HasScrollActive = true;
            bits.ScrollActive = rich.ScrollActive;
            bits.HasVAlign = true;
            bits.VAlign = rich.VerticalAlignment;
        }
        if (control is Label label)
        {
            bits.HasWrap = true;
            bits.Wrap = label.AutowrapMode;
        }
        if (control is TextureRect texture)
        {
            bits.HasExpand = true;
            bits.Expand = texture.ExpandMode;
            bits.HasStretch = true;
            bits.Stretch = texture.StretchMode;
        }
        _eventRestores[id] = bits;
        return bits;
    }

    private void RecordEventNode(Control control) => EventBits(control);

    /// <summary>Put every moved node back and free what the composition added; the game's own layout returns.</summary>
    private void RestoreEventsLandscape()
    {
        foreach ((ulong id, EventRestore bits) in _eventRestores)
        {
            if (GodotObject.InstanceFromId(id) is not Control control || !GodotObject.IsInstanceValid(control)) continue;
            if (bits.Moved && bits.Parent != 0 && GodotObject.InstanceFromId(bits.Parent) is Node parent &&
                GodotObject.IsInstanceValid(parent))
            {
                control.Reparent(parent, keepGlobalTransform: false);
                parent.MoveChild(control, Math.Clamp(bits.Index, 0, Math.Max(0, parent.GetChildCount() - 1)));
            }
            if (bits.HasAutoSize) control.Set("AutoSizeEnabled", bits.AutoSize);
            if (control is RichTextLabel rich)
            {
                if (bits.HasScrollActive) rich.ScrollActive = bits.ScrollActive;
                if (bits.HasFit) rich.FitContent = bits.Fit;
                if (bits.HasVAlign) rich.VerticalAlignment = bits.VAlign;
            }
            if (control is Label label && bits.HasWrap) label.AutowrapMode = bits.Wrap;
            if (bits.HasFilter) control.MouseFilter = bits.Filter;
            if (control is TextureRect texture)
            {
                if (bits.HasExpand) texture.ExpandMode = bits.Expand;
                if (bits.HasStretch) texture.StretchMode = bits.Stretch;
            }
        }
        _eventRestores.Clear();

        foreach (EventState state in _eventStates.Values)
        {
            FreeEventNode(state.ClipNode);
            FreeEventNode(state.FadeNode);
            FreeEventNode(state.SlotNode);
            FreeEventNode(state.ChoicesNode);
        }
        foreach (ulong id in _hiddenEventVeils)
            if (GodotObject.InstanceFromId(id) is GpuParticles2D veil && GodotObject.IsInstanceValid(veil))
                veil.Visible = true;
        _hiddenEventVeils.Clear();
        _eventStates.Clear();
        _eventRowIds.Clear();
        _eventDrag?.Clear();

        foreach (ulong id in _eventRooms)
            if (GodotObject.InstanceFromId(id) is Control room && GodotObject.IsInstanceValid(room))
                FreeEventNode(room.GetNodeOrNull<Control>(EventRoomCoverName)?.GetInstanceId() ?? 0);
    }

    private static void FreeEventNode(ulong id)
    {
        if (id == 0 || GodotObject.InstanceFromId(id) is not Node node || !GodotObject.IsInstanceValid(node)) return;
        node.GetParent()?.RemoveChild(node);
        node.QueueFree();
    }

    private void PruneEventInstances()
    {
        foreach (ulong id in _eventStates.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _eventStates.Remove(id);
        foreach (ulong id in _eventRestores.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _eventRestores.Remove(id);
        _eventRooms.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in _eventRoomKinds.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _eventRoomKinds.Remove(id);
        _hiddenEventVeils.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
    }
}
