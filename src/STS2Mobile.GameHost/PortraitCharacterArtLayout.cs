using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// The character art of the portrait character select, Blitz and Dread screens: characters with a separate backdrop scene get
/// their backdrop TextureRect framed as a cover and the Spine as the figure; Ironclad, Regent and Defect share one Spine,
/// which is split into a second playback masked to the figure and the original masked to the scenery. The rectangular
/// ambient emitters of each character scene are fitted over the screen. All of that is per art container: it is framed once
/// when the art is ready and re-framed only when the art child or the screen size changes, and it keeps its own captured
/// controls and the emitter values put back in landscape. The menus own the screens, placement of the container above and
/// below the art; this owner only frames the picture inside the container the screen hands it.
/// </summary>
internal sealed class PortraitCharacterArtLayout
{
    // The character art is drawn from the single SpineSprite the scene already has. Characters with a separate backdrop
    // TextureRect frame it as cover and the Spine as the figure; the others are one composition, scaled so it covers the
    // screen and biased towards the side the figure stands on. Nothing is instanced, reparented or masked: this path never
    // touches a skeleton that is not fully ready (a second SpineSprite sharing a skeleton resource, or slot attachments
    // changed under the game's animation state, can abort the engine instead of raising an exception).
    // The composition is framed once and then left alone; only a changed art child (a character pick) starts the work again,
    // so the per-frame pass has no NodePath, LINQ, meta or Spine calls of its own.
    private const string ArtFrameMeta = "phone_character_art_frame";
    private const string FigureFrameMeta = "phone_character_figure_frame";
    private const string CoverMeta = "phone_character_cover";
    private static readonly Vector2 ArtSize = new(2560f, 1200f);

    private const string FigureNodeName = "PhonePortraitFigure";

    private sealed class FigureState
    {
        public Node2D? Figure;
        public bool Masked;
        public bool Animated;
    }

    private readonly Dictionary<ulong, Control?> _containers = new();
    private readonly Dictionary<ulong, CpuParticles2D[]> _emitters = new();
    private readonly Dictionary<(ulong Id, string Property), Variant> _originalNodeValues = new();
    private readonly Dictionary<ulong, ulong> _artFirstSeen = new();
    private readonly Dictionary<ulong, Node2D?> _artSpines = new();
    private readonly Dictionary<ulong, TextureRect?> _artTextures = new();
    private readonly List<ulong> _artIds = new();
    private readonly HashSet<ulong> _artFramed = new();
    private readonly Dictionary<ulong, Vector2> _artFramedSize = new();
    private readonly Dictionary<ulong, FigureState> _figures = new();
    private readonly Dictionary<ulong, Func<string, bool>> _maskRule = new();
    private readonly Dictionary<ulong, List<(GodotObject Slot, Variant Attachment)>> _maskedAttachments = new();
    private readonly HashSet<ulong> _maskWired = new();
    private readonly Dictionary<ulong, (Vector2 Scale, Vector2 Position)> _spineOriginals = new();
    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    // Art added this frame (a character pick) waits for its Spine's first update before any Spine call, but must not be drawn
    // as the game laid it out for landscape: it is framed just before this frame is drawn, and hidden until it can be.
    private readonly List<(Control Screen, Control Container, ulong ArtId)> _framedBeforeDraw = new();
    private readonly HashSet<ulong> _hiddenUntilFramed = new();
    private bool _preDrawSubscribed;

    private PortraitControlSnapshot Capture(Control control)
    {
        ulong id = control.GetInstanceId();
        if (!_snapshots.TryGetValue(id, out PortraitControlSnapshot? snap))
            _snapshots[id] = snap = new PortraitControlSnapshot(control, captureVisible: false);
        return snap;
    }

    /// <summary>Puts a control at a rectangle of its parent with the top left anchor, changing only what differs.</summary>
    private void Place(Control? control, float x, float y, float w, float h)
    {
        if (control == null) return;
        Capture(control);
        if (control.AnchorLeft != 0f || control.AnchorTop != 0f || control.AnchorRight != 0f || control.AnchorBottom != 0f)
        {
            control.AnchorLeft = 0f;
            control.AnchorTop = 0f;
            control.AnchorRight = 0f;
            control.AnchorBottom = 0f;
        }
        if (MathF.Abs(control.OffsetLeft - x) > 0.5f) control.OffsetLeft = x;
        if (MathF.Abs(control.OffsetTop - y) > 0.5f) control.OffsetTop = y;
        if (MathF.Abs(control.OffsetRight - (x + w)) > 0.5f) control.OffsetRight = x + w;
        if (MathF.Abs(control.OffsetBottom - (y + h)) > 0.5f) control.OffsetBottom = y + h;
    }

    /// <summary>
    /// Frames the screen's art container and the character scene inside it, from the container the screen hands over (its
    /// path, or the scene's unique <c>%AnimatedBg</c>). The container is resolved once per screen; a freed container is
    /// resolved again, a null one stays final until the next portrait session.
    /// </summary>
    public void Apply(Control screen, string path)
    {
        ulong screenId = screen.GetInstanceId();
        if (!_containers.TryGetValue(screenId, out Control? container) ||
            (container != null && !GodotObject.IsInstanceValid(container)))
        {
            container = screen.GetNodeOrNull<Control>(path) ?? screen.GetNodeOrNull<Control>("%AnimatedBg");
            _containers[screenId] = container;
        }
        if (container == null || !GodotObject.IsInstanceValid(container) || !container.IsInsideTree()) return;
        Capture(container);
        Place(container, 0f, 0f, screen.Size.X, screen.Size.Y);
        if (container.PivotOffset != Vector2.Zero) container.PivotOffset = Vector2.Zero;
        if (container.Scale != Vector2.One) container.Scale = Vector2.One;
        // The art is a picture: never let a full-screen TextureRect or scene root take a touch from the buttons over it.
        if (container.MouseFilter != Control.MouseFilterEnum.Ignore) container.MouseFilter = Control.MouseFilterEnum.Ignore;

        int count = container.GetChildCount();
        bool changed = _artIds.Count != count;
        if (!changed)
        {
            for (int i = 0; i < count; i++)
            {
                if (container.GetChild(i).GetInstanceId() != _artIds[i])
                {
                    changed = true;
                    break;
                }
            }
        }
        if (changed)
        {
            _artIds.Clear();
            for (int i = 0; i < count; i++) _artIds.Add(container.GetChild(i).GetInstanceId());
            _artFramed.Clear();
        }

        Transform2D toCanvas = screen.GetGlobalTransformWithCanvas();
        ulong now = Engine.GetProcessFrames();
        foreach (ulong id in _artIds)
        {
            bool framed = _artFramed.Contains(id);
            bool sizeChanged = framed &&
                (!_artFramedSize.TryGetValue(id, out Vector2 framedAt) || framedAt.DistanceSquaredTo(container.Size) > 0.25f);
            // Framing is applied once when the character's art is ready: re-reading growing skeleton bounds each frame moved
            // the figure for about a second after selection. Only a changed screen size (or a new art child) re-frames.
            if (framed && !sizeChanged) continue;
            if (GodotObject.InstanceFromId(id) is not Control art || !GodotObject.IsInstanceValid(art) ||
                art.IsQueuedForDeletion() || !art.IsInsideTree())
            {
                // A non-Control child has nothing to frame; a freed one disappears from the ids on the next pass.
                if (GodotObject.InstanceFromId(id) is not Control) _artFramed.Add(id);
                continue;
            }
            // Give a freshly added scene a frame to finish entering and initialising its Spine before any native Spine call:
            // it is framed just before this frame is drawn, after its Spine has been updated once.
            if (!_artFirstSeen.TryGetValue(id, out ulong seen)) _artFirstSeen[id] = seen = now;
            if (now <= seen)
            {
                FrameBeforeDraw(screen, container, id);
                continue;
            }
            if (sizeChanged) _artFramed.Remove(id);
            if (!FrameArtChild(art, container.Size, toCanvas))
            {
                HideUntilFramed(art);
                continue;
            }
            _artFramed.Add(id);
            _artFramedSize[id] = container.Size;
            ShowFramed(art);
        }
    }

    private void FrameBeforeDraw(Control screen, Control container, ulong artId)
    {
        foreach ((Control _, Control _, ulong pending) in _framedBeforeDraw)
            if (pending == artId) return;
        _framedBeforeDraw.Add((screen, container, artId));
        if (_preDrawSubscribed) return;
        RenderingServer.FramePreDraw += OnFramePreDraw;
        _preDrawSubscribed = true;
    }

    private void OnFramePreDraw()
    {
        if (_framedBeforeDraw.Count == 0) return;
        var pending = _framedBeforeDraw.ToArray();
        _framedBeforeDraw.Clear();
        if (!SafeAreaCanvas.IsPortrait) return;
        foreach ((Control screen, Control container, ulong id) in pending)
        {
            if (!GodotObject.IsInstanceValid(screen) || !GodotObject.IsInstanceValid(container) || !container.IsInsideTree()) continue;
            if (GodotObject.InstanceFromId(id) is not Control art || !GodotObject.IsInstanceValid(art) ||
                art.IsQueuedForDeletion() || !art.IsInsideTree() || _artFramed.Contains(id)) continue;
            if (FrameArtChild(art, container.Size, screen.GetGlobalTransformWithCanvas()))
            {
                _artFramed.Add(id);
                _artFramedSize[id] = container.Size;
                ShowFramed(art);
            }
            else
            {
                HideUntilFramed(art);
            }
        }
    }

    /// <summary>Art that cannot be framed yet is not drawn at all rather than drawn in its landscape layout.</summary>
    private void HideUntilFramed(Control art)
    {
        if (!art.Visible) return;
        art.Visible = false;
        _hiddenUntilFramed.Add(art.GetInstanceId());
    }

    private void ShowFramed(Control art)
    {
        if (_hiddenUntilFramed.Remove(art.GetInstanceId())) art.Visible = true;
    }

    /// <summary>Frames one character scene; false while its Spine skeleton is still loading so a later frame retries.</summary>
    private bool FrameArtChild(Control art, Vector2 viewport, Transform2D toCanvas)
    {
        if (viewport.X < 1f || viewport.Y < 1f || !art.IsInsideTree()) return false;
        Capture(art);
        if (art.AnchorLeft != 0f || art.AnchorTop != 0f || art.AnchorRight != 0f || art.AnchorBottom != 0f)
            art.AnchorLeft = art.AnchorTop = art.AnchorRight = art.AnchorBottom = 0f;
        if (art.PivotOffset != Vector2.Zero) art.PivotOffset = Vector2.Zero;
        if (art.Size.DistanceSquaredTo(ArtSize) > 0.25f) art.Size = ArtSize;
        if (art.Scale != Vector2.One) art.Scale = Vector2.One;
        if (art.Position != Vector2.Zero) art.Position = Vector2.Zero;
        if (art.MouseFilter != Control.MouseFilterEnum.Ignore) art.MouseFilter = Control.MouseFilterEnum.Ignore;

        string name = ArtName(art);
        float overscan = name == "DEFECT_bg" ? 1.12f : 1.04f;
        Node2D? spine = SpineOf(art);
        // The skeleton loads asynchronously; leave the art as the game drew it until its bounds can be read.
        if (spine != null && spine.IsClass("SpineSprite") && ReadBounds(spine) is null) return false;

        TextureRect? texture = TextureOf(art);
        if (texture is { Texture: not null })
        {
            // Silent, Necrobinder and Fool already have a separate native backdrop: cover it, then frame the Spine as the
            // figure the way the reference does (it holds only the character for these scenes). The stage is the texture
            // rect's own size, never its live position: a full-rect TextureRect changes size when its position is written,
            // and feeding that back made the frame diverge.
            if (texture.MouseFilter != Control.MouseFilterEnum.Ignore) texture.MouseFilter = Control.MouseFilterEnum.Ignore;
            Vector2 stage = texture.Size;
            if (!float.IsFinite(stage.X) || !float.IsFinite(stage.Y) || stage.X < 1f || stage.Y < 1f ||
                stage.X > 20000f || stage.Y > 20000f) stage = ArtSize;
            float coverScale = Mathf.Max(viewport.X / stage.X, viewport.Y / stage.Y) * overscan;
            if (!float.IsFinite(coverScale) || coverScale <= 0f) return false;
            texture.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            if (texture.PivotOffset != Vector2.Zero) texture.PivotOffset = Vector2.Zero;
            if (texture.Size.DistanceSquaredTo(stage) > 0.25f) texture.Size = stage;
            texture.Scale = new Vector2(coverScale, coverScale);
            texture.Position = viewport * 0.5f - stage * 0.5f * coverScale;
            Vector2 stageDrawn = stage * coverScale;
            if (float.IsFinite(stageDrawn.X) && float.IsFinite(stageDrawn.Y))
                SetRectMeta(art, ArtFrameMeta, toCanvas * new Rect2(texture.Position, stageDrawn));

            if (spine != null && ReadBounds(spine) is { } figure && figure.Size.X > 1f && figure.Size.Y > 1f)
            {
                float figureScale = Mathf.Min(viewport.X * 1.40f / figure.Size.X, viewport.Y * 0.63f / figure.Size.Y);
                float focus = name switch
                {
                    "DEFECT_bg" => 0.36f,
                    "SILENT_bg" => 0.41f,
                    "FOOL_bg" or "NECROBINDER_bg" => 0.44f,
                    _ => 0.49f,
                };
                Vector2 target = viewport * new Vector2(focus, FigureCenterY);
                if (float.IsFinite(figureScale) && figureScale > 0f)
                {
                    spine.Scale = Vector2.One * figureScale;
                    spine.Position = target - figure.GetCenter() * figureScale;
                    Vector2 drawn = figure.Size * figureScale;
                    if (float.IsFinite(drawn.X) && float.IsFinite(drawn.Y))
                        SetRectMeta(art, FigureFrameMeta, toCanvas * new Rect2(target - drawn * 0.5f, drawn));
                }
            }
            else
            {
                if (art.HasMeta(FigureFrameMeta)) art.RemoveMeta(FigureFrameMeta);
            }
        }
        else if (spine != null && ReadBounds(spine) is { } bounds && bounds.Size.X > 1f && bounds.Size.Y > 1f)
        {
            // Ironclad, Regent and Defect: the figure and scenery share one Spine, and the reference splits them. Frame the
            // separated figure once its second playback is ready; until then the whole composition covers the screen.
            if (TryFrameSplitFigure(art, spine, name, viewport, toCanvas)) goto Emitters;
            // One composition (backdrop and figure in the same Spine): cover the screen. A small left bias brings the
            // right-of-centre figure closer to the middle of the narrow screen.
            if (!art.HasMeta(CoverMeta)) art.SetMeta(CoverMeta, bounds);
            Rect2 cover = art.GetMeta(CoverMeta).AsRect2();
            float coverScale = Mathf.Max(viewport.X / cover.Size.X, viewport.Y / cover.Size.Y) * overscan;
            if (!float.IsFinite(coverScale) || coverScale <= 0f) return false;
            float bias = name switch
            {
                "IRONCLAD_bg" or "REGENT_bg" => -0.07f,
                "DEFECT_bg" => -0.10f,
                _ => 0f,
            };
            spine.Scale = Vector2.One * coverScale;
            spine.Position = viewport * new Vector2(0.5f + bias, 0.5f) - cover.GetCenter() * coverScale;
            SetRectMeta(art, ArtFrameMeta, toCanvas * new Rect2(spine.Position + cover.Position * coverScale, cover.Size * coverScale));
            if (art.HasMeta(FigureFrameMeta)) art.RemoveMeta(FigureFrameMeta);
        }

        Emitters:
        // Dread draws its art in a SubViewport, a canvas of its own: points there are local, not canvas, coordinates.
        bool inSubViewport = art.GetViewport() is SubViewport;
        Vector2 CanvasPoint(Vector2 point) => inSubViewport ? point : toCanvas * point;
        foreach (CpuParticles2D particles in AmbientEmitters(art))
        {
            KeepOriginal(particles, "emission_rect_extents");
            KeepOriginal(particles, "global_position");
            Vector2 center = CanvasPoint(viewport * 0.5f);
            if (particles.GlobalPosition.DistanceSquaredTo(center) > 0.25f) particles.GlobalPosition = center;
            // The emitter may itself be rotated and non-uniformly scaled: fit all four screen corners in its local space.
            Vector2 extents = particles.ToLocal(CanvasPoint(Vector2.Zero)).Abs()
                .Max(particles.ToLocal(CanvasPoint(new Vector2(viewport.X, 0f))).Abs())
                .Max(particles.ToLocal(CanvasPoint(new Vector2(0f, viewport.Y))).Abs())
                .Max(particles.ToLocal(CanvasPoint(viewport)).Abs()) * 1.10f;
            if (particles.EmissionRectExtents.IsEqualApprox(extents)) continue;
            particles.EmissionRectExtents = extents;
            particles.Restart();
        }
        return true;
    }

    private static void SetRectMeta(Control art, string name, Rect2 rect)
    {
        if (art.HasMeta(name) && art.GetMeta(name).AsRect2().IsEqualApprox(rect)) return;
        art.SetMeta(name, rect);
    }

    private static string ArtName(Control art)
    {
        string name = art.Name.ToString();
        const string prefix = "char_select_bg_";
        string scene = System.IO.Path.GetFileNameWithoutExtension(art.SceneFilePath);
        if (scene.StartsWith(prefix, StringComparison.Ordinal))
            name = scene[prefix.Length..].ToUpperInvariant() + "_bg";
        return name;
    }

    private Node2D? SpineOf(Control art)
    {
        ulong id = art.GetInstanceId();
        if (_artSpines.TryGetValue(id, out Node2D? cached))
        {
            if (cached == null || GodotObject.IsInstanceValid(cached)) return cached;
            _artSpines.Remove(id);
        }
        Node2D? found = art.GetNodeOrNull<Node2D>("SpineSprite");
        _artSpines[id] = found;
        return found;
    }

    private TextureRect? TextureOf(Control art)
    {
        ulong id = art.GetInstanceId();
        if (_artTextures.TryGetValue(id, out TextureRect? cached))
        {
            if (cached == null || GodotObject.IsInstanceValid(cached)) return cached;
            _artTextures.Remove(id);
        }
        TextureRect? found = art.GetNodeOrNull<TextureRect>("TextureRect");
        _artTextures[id] = found;
        return found;
    }

    /// <summary>The rectangular ambient emitters of one character's art, found once: they are part of the scene, not rebuilt.</summary>
    private CpuParticles2D[] AmbientEmitters(Control art)
    {
        ulong id = art.GetInstanceId();
        if (_emitters.TryGetValue(id, out CpuParticles2D[]? found) && found.All(p => GodotObject.IsInstanceValid(p))) return found;
        found = art.FindChildren("*", "CPUParticles2D", true, false).OfType<CpuParticles2D>()
            .Where(p => p.EmissionShape == CpuParticles2D.EmissionShapeEnum.Rectangle).ToArray();
        _emitters[id] = found;
        return found;
    }

    private static Rect2? ReadBounds(Node2D sprite)
    {
        if (!GodotObject.IsInstanceValid(sprite) || !sprite.IsInsideTree()) return null;
        if (!sprite.HasMethod("get_skeleton")) return null;
        GodotObject? skeleton = sprite.Call("get_skeleton").AsGodotObject();
        if (skeleton == null || !GodotObject.IsInstanceValid(skeleton)) return null;
        Rect2 bounds = skeleton.Call("get_bounds").AsRect2();
        return bounds.Size.X > 1f && bounds.Size.Y > 1f ? bounds : null;
    }

    private void KeepOriginal(GodotObject node, string property)
    {
        var key = (node.GetInstanceId(), property);
        if (!_originalNodeValues.ContainsKey(key)) _originalNodeValues[key] = node.Get(property);
    }

    private static bool SplitsFigure(string name) => name is "IRONCLAD_bg" or "REGENT_bg" or "DEFECT_bg";

    private static string? BaseSlot(string name) => name switch
    {
        "IRONCLAD_bg" => "bg",
        "REGENT_bg" => "Layer 5",
        "DEFECT_bg" => "backgroundbottom",
        _ => null,
    };

    private static bool IsBackdropSlot(string name, string slot) => name switch
    {
        "IRONCLAD_bg" => slot is "bg" or "fire",
        "REGENT_bg" => slot is "Layer 5" or "swirls" or "spheric guardian" or "snecko" or "amogus" or "sentry"
            || slot is "shapes" or "deca" or "cultist" || slot.StartsWith("stars_", StringComparison.Ordinal)
            || slot.EndsWith("outline2", StringComparison.Ordinal),
        "DEFECT_bg" => slot is "backgroundbottom" or "backgroundtop" or "shine" or "sparkle",
        _ => true,
    };

    private static float FigureFocus(string name) => name switch
    {
        "DEFECT_bg" => 0.36f,
        "SILENT_bg" => 0.41f,
        "FOOL_bg" or "NECROBINDER_bg" => 0.44f,
        _ => 0.49f,
    };

    // The reference's fixed vertical band for the figure's centre. Never derive this from the live InfoPanel: when the art
    // is framed on its first ready frame the panel is still being animated and its global transform can read as y=0, which
    // moved the whole portrait off the top of the screen.
    private const float FigureCenterY = 0.39f;

    /// <summary>
    /// Frames one split-composition character. False while its second playback is not ready (the caller falls back to the
    /// whole composition's cover for those frames, as the reference does).
    /// </summary>
    private bool TryFrameSplitFigure(Control art, Node2D spine, string name, Vector2 viewport, Transform2D toCanvas)
    {
        if (!SplitsFigure(name) || BaseSlot(name) is not { } baseSlot) return false;
        // The second playback needs the skeleton transform callback the reference relies on; leave the scene alone otherwise.
        if (!spine.HasSignal("before_world_transforms_change")) return false;
        ulong artId = art.GetInstanceId();
        if (!_spineOriginals.ContainsKey(spine.GetInstanceId()))
            _spineOriginals[spine.GetInstanceId()] = (spine.Scale, spine.Position);
        (Vector2 Scale, Vector2 Position) native = _spineOriginals[spine.GetInstanceId()];
        if (!_figures.TryGetValue(artId, out FigureState? state)) _figures[artId] = state = new FigureState();
        if (state.Figure is null || !GodotObject.IsInstanceValid(state.Figure))
        {
            state.Figure = CreateFigure(art, spine, native);
            state.Masked = false;
            state.Animated = false;
            if (state.Figure is null) return false;
        }
        Node2D figure = state.Figure;
        // The original stays the backdrop: only its scenery slots remain attached.
        spine.Scale = native.Scale;
        spine.Position = native.Position;
        if (!state.Masked)
        {
            if (figure.Call("get_skeleton").AsGodotObject() is null) return false;
            MaskSlots(spine, slot => !IsBackdropSlot(name, slot));
            MaskSlots(figure, slot => IsBackdropSlot(name, slot));
            state.Masked = true;
        }
        if (!state.Animated)
        {
            if (figure.Call("get_animation_state").AsGodotObject() is not { } animation) return false;
            animation.Call("set_animation", "animation", true);
            figure.Show();
            state.Animated = true;
        }
        if (ReadBounds(figure) is not { } figureBounds || figureBounds.Size.X < 1f || figureBounds.Size.Y < 1f) return false;
        if (ReadBaseBounds(spine, baseSlot) is not { } skeletonCover || skeletonCover.Size.X < 1f || skeletonCover.Size.Y < 1f)
            return false;
        // Skeleton bounds are in the spine's own space; map them through the node's native transform before covering with them.
        var cover = new Rect2(native.Position + skeletonCover.Position * native.Scale, skeletonCover.Size * native.Scale.Abs());
        if (cover.Size.X < 1f || cover.Size.Y < 1f) return false;

        float overscan = name == "DEFECT_bg" ? 1.12f : 1.04f;
        float artScale = Mathf.Max(viewport.X / cover.Size.X, viewport.Y / cover.Size.Y) * overscan;
        if (!float.IsFinite(artScale) || artScale <= 0f) return false;
        art.Scale = new Vector2(artScale, artScale);
        art.Position = viewport * 0.5f - cover.GetCenter() * artScale;
        float figureScale = Mathf.Min(viewport.X * 1.40f / figureBounds.Size.X, viewport.Y * 0.63f / figureBounds.Size.Y);
        if (!float.IsFinite(figureScale) || figureScale <= 0f) return false;
        Vector2 target = viewport * new Vector2(FigureFocus(name), FigureCenterY);
        figure.Scale = new Vector2(figureScale / artScale, figureScale / artScale);
        figure.Position = (target - art.Position - figureBounds.GetCenter() * figureScale) / artScale;
        Vector2 drawn = figureBounds.Size * figureScale;
        if (!float.IsFinite(drawn.X) || !float.IsFinite(drawn.Y)) return false;
        SetRectMeta(art, ArtFrameMeta, toCanvas * new Rect2(art.Position + cover.Position * artScale, cover.Size * artScale));
        SetRectMeta(art, FigureFrameMeta, toCanvas * new Rect2(target - drawn * 0.5f, drawn));
        return true;
    }

    private static Node2D? CreateFigure(Control art, Node2D spine, (Vector2 Scale, Vector2 Position) native)
    {
        if (art.GetNodeOrNull<Node2D>(FigureNodeName) is { } existing) return existing;
        if (ClassDB.Instantiate("SpineSprite").AsGodotObject() is not Node2D figure) return null;
        figure.Name = FigureNodeName;
        foreach (string property in new[]
                 {
                     "normal_material", "additive_material", "multiply_material", "screen_material", "skeleton_data_res",
                 })
            figure.Set(property, spine.Get(property));
        figure.Position = native.Position;
        figure.Scale = native.Scale;
        figure.Hide();
        art.AddChild(figure);
        art.MoveChild(figure, spine.GetIndex() + 1);
        return figure;
    }

    /// <summary>The bounds of the one backdrop attachment, leaving every other attachment out of the measurement.</summary>
    private static Rect2? ReadBaseBounds(Node2D sprite, string baseAttachment)
    {
        if (sprite.Call("get_skeleton").AsGodotObject() is not { } skeleton) return null;
        var others = new List<(GodotObject Slot, Variant Attachment)>();
        foreach (Variant value in skeleton.Call("get_slots").AsGodotArray())
        {
            if (value.AsGodotObject() is not { } slot) continue;
            string slotName = slot.Call("get_data").AsGodotObject()?.Call("get_attachment_name").AsString() ?? "";
            if (slotName == baseAttachment) continue;
            Variant attachment = slot.Call("get_attachment");
            if (attachment.AsGodotObject() is null) continue;
            others.Add((slot, attachment));
        }
        try
        {
            foreach ((GodotObject slot, Variant _) in others) slot.Call("set_attachment", default(Variant));
            return ReadBounds(sprite);
        }
        finally
        {
            foreach ((GodotObject slot, Variant attachment) in others) slot.Call("set_attachment", attachment);
        }
    }

    /// <summary>Hides the named slots on one playback and re-hides them after the native timeline or skin changes.</summary>
    private void MaskSlots(Node2D sprite, Func<string, bool> hide)
    {
        ulong id = sprite.GetInstanceId();
        if (!_maskedAttachments.TryGetValue(id, out List<(GodotObject Slot, Variant Attachment)>? saved))
            _maskedAttachments[id] = saved = new List<(GodotObject, Variant)>();
        if (sprite.Call("get_skeleton").AsGodotObject() is not { } skeleton) return;
        foreach (Variant value in skeleton.Call("get_slots").AsGodotArray())
        {
            if (value.AsGodotObject() is not { } slot) continue;
            string slotName = slot.Call("get_data").AsGodotObject()?.Call("get_attachment_name").AsString() ?? "";
            if (!hide(slotName)) continue;
            Variant attachment = slot.Call("get_attachment");
            if (attachment.AsGodotObject() is null) continue;
            saved.Add((slot, attachment));
            slot.Call("set_attachment", default(Variant));
        }
        if (_maskWired.Add(id))
            sprite.Connect("before_world_transforms_change", Callable.From<GodotObject>(_ =>
            {
                if (SafeAreaCanvas.IsPortrait && _maskRule.TryGetValue(id, out Func<string, bool>? rule)) ApplyMask(sprite, rule);
            }));
        _maskRule[id] = hide;
    }

    private static void ApplyMask(Node2D sprite, Func<string, bool> hide)
    {
        if (sprite.Call("get_skeleton").AsGodotObject() is not { } skeleton) return;
        foreach (Variant value in skeleton.Call("get_slots").AsGodotArray())
        {
            if (value.AsGodotObject() is not { } slot) continue;
            string slotName = slot.Call("get_data").AsGodotObject()?.Call("get_attachment_name").AsString() ?? "";
            if (hide(slotName) && slot.Call("get_attachment").AsGodotObject() is not null)
                slot.Call("set_attachment", default(Variant));
        }
    }

    /// <summary>
    /// Puts the emitter values and the split slots back and drops the second playbacks, then forgets the framing so the next
    /// portrait pass frames the art again with the screen's new size. The captured controls go back with
    /// <see cref="RestoreControls"/> in the menu's own restore order.
    /// </summary>
    public void RestoreLandscape()
    {
        _containers.Clear();
        RestoreArt();
        RestoreFigures();
    }

    private void RestoreArt()
    {
        foreach (((ulong id, string property), Variant value) in _originalNodeValues)
        {
            if (GodotObject.InstanceFromId(id) is not GodotObject node || !GodotObject.IsInstanceValid(node)) continue;
            node.Set(property, value);
            if (node is CpuParticles2D particles) particles.Restart();
        }
        _originalNodeValues.Clear();
        // The next portrait pass frames the art again with the screen's new size.
        _artIds.Clear();
        _artFramed.Clear();
        _artFramedSize.Clear();
        _artSpines.Clear();
        _artTextures.Clear();
        _artFirstSeen.Clear();
        _framedBeforeDraw.Clear();
        if (_preDrawSubscribed)
        {
            RenderingServer.FramePreDraw -= OnFramePreDraw;
            _preDrawSubscribed = false;
        }
        foreach (ulong id in _hiddenUntilFramed)
            if (GodotObject.InstanceFromId(id) is Control art && GodotObject.IsInstanceValid(art)) art.Visible = true;
        _hiddenUntilFramed.Clear();
        _emitters.Clear();
    }

    /// <summary>Puts every slot's original attachment back and drops the second playbacks (landscape).</summary>
    private void RestoreFigures()
    {
        // A slot is a view into its playback's skeleton: once the playback has gone (the character select freed when a run
        // starts) the slot object can outlive it, and writing to it reaches freed Spine memory. Only a live playback is put back.
        foreach ((ulong spriteId, List<(GodotObject Slot, Variant Attachment)> saved) in _maskedAttachments)
        {
            if (!LivePlayback(spriteId)) continue;
            foreach ((GodotObject slot, Variant attachment) in saved)
                if (GodotObject.IsInstanceValid(slot)) slot.Call("set_attachment", attachment);
        }
        foreach (FigureState state in _figures.Values)
            if (state.Figure is not null && GodotObject.IsInstanceValid(state.Figure)) state.Figure.QueueFree();
        _figures.Clear();
        _maskRule.Clear();
        _maskedAttachments.Clear();
        _maskWired.Clear();
        _spineOriginals.Clear();
    }

    public void RestoreControls()
    {
        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
        _snapshots.Clear();
    }

    public void PruneDead()
    {
        foreach (ulong id in _snapshots.Keys.Where(id => !GodotObject.IsInstanceIdValid(id)).ToList()) _snapshots.Remove(id);
        foreach (ulong id in _containers.Keys.Where(id => !GodotObject.IsInstanceIdValid(id)).ToList()) _containers.Remove(id);
        foreach (ulong id in _emitters.Keys.Where(id => !GodotObject.IsInstanceIdValid(id)).ToList()) _emitters.Remove(id);
        foreach (ulong id in _artFirstSeen.Keys.Where(id => !GodotObject.IsInstanceIdValid(id)).ToList()) _artFirstSeen.Remove(id);
        _hiddenUntilFramed.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in _artSpines.Keys.Where(id => !GodotObject.IsInstanceIdValid(id)).ToList()) _artSpines.Remove(id);
        foreach (ulong id in _artTextures.Keys.Where(id => !GodotObject.IsInstanceIdValid(id)).ToList()) _artTextures.Remove(id);
        // A gone playback's saved slots must never be written again (see RestoreFigures).
        foreach (ulong id in _maskedAttachments.Keys.Where(id => !LivePlayback(id)).ToList())
        {
            _maskedAttachments.Remove(id);
            _maskRule.Remove(id);
            _maskWired.Remove(id);
        }
        foreach (ulong id in _spineOriginals.Keys.Where(id => !GodotObject.IsInstanceIdValid(id)).ToList()) _spineOriginals.Remove(id);
    }

    private static bool LivePlayback(ulong spriteId) =>
        GodotObject.InstanceFromId(spriteId) is Node sprite && GodotObject.IsInstanceValid(sprite) && !sprite.IsQueuedForDeletion();
}
