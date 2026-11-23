using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

internal sealed partial class PortraitMenuLayout
{
    // The character art: the backdrop is scaled so it covers the screen (a little overscan keeps the edges soft), the
    // figure is split into a second playback of the same skeleton and framed separately so it stands in the middle of the
    // narrow screen instead of off to the side, and the ambient emitters are widened over the screen.
    private const string FigureName = "PhonePortraitFigure";
    private const string ArtFrameMeta = "phone_character_art_frame";
    private const string FigureFrameMeta = "phone_character_figure_frame";
    private const string CoverMeta = "phone_character_cover";
    private const string FigureMeta = "phone_character_figure";
    private static readonly Vector2 ArtSize = new(2560f, 1200f);

    private sealed class SpineMask
    {
        public GodotObject? Sprite;
        public Callable Handler;
        public readonly List<(GodotObject Slot, Variant Attachment)> Slots = new();
    }

    private readonly List<SpineMask> _masks = new();
    private readonly HashSet<ulong> _figures = new();
    private readonly Dictionary<(ulong Id, string Property), Variant> _originalNodeValues = new();

    private void FrameArt(Control screen, string path)
    {
        Control? container = screen.GetNodeOrNull<Control>(path) ?? screen.GetNodeOrNull<Control>("%AnimatedBg");
        if (container == null) return;
        Capture(container);
        Place(container, 0f, 0f, screen.Size.X, screen.Size.Y);
        if (container.PivotOffset != Vector2.Zero) container.PivotOffset = Vector2.Zero;
        if (container.Scale != Vector2.One) container.Scale = Vector2.One;

        Transform2D toCanvas = screen.GetGlobalTransformWithCanvas();
        foreach (Control art in container.GetChildren().OfType<Control>())
        {
            if (art.IsQueuedForDeletion()) continue;
            FrameArtChild(art, container.Size, toCanvas);
        }
    }

    private void FrameArtChild(Control art, Vector2 viewport, Transform2D toCanvas)
    {
        if (viewport.X < 1f || viewport.Y < 1f) return;
        Capture(art);
        if (art.AnchorLeft != 0f || art.AnchorTop != 0f || art.AnchorRight != 0f || art.AnchorBottom != 0f)
            art.AnchorLeft = art.AnchorTop = art.AnchorRight = art.AnchorBottom = 0f;
        if (art.PivotOffset != Vector2.Zero) art.PivotOffset = Vector2.Zero;
        if (art.Size.DistanceSquaredTo(ArtSize) > 0.25f) art.Size = ArtSize;
        if (art.Scale != Vector2.One) art.Scale = Vector2.One;

        Node2D? original = art.GetNodeOrNull<Node2D>("SpineSprite");
        // The skeleton loads asynchronously: leave the art as the game drew it until its bounds can be read.
        if (original != null && original.IsClass("SpineSprite") && ReadBounds(original) == null) return;
        Node2D? foreground = original;
        string name = ArtName(art);
        Rect2 cover = new(Vector2.Zero, art.Size);
        if (art.GetNodeOrNull<TextureRect>("TextureRect") is { Texture: not null } texture)
        {
            // Silent, Necrobinder and Fool already have a separate native backdrop.
            cover = texture.GetRect();
        }
        else if (original != null)
        {
            Func<string, bool>? isBackdrop = name switch
            {
                "IRONCLAD_bg" => slot => slot is "bg" or "fire",
                "REGENT_bg" => slot => slot is "Layer 5" or "swirls" || slot.StartsWith("stars_", StringComparison.Ordinal)
                    || slot is "spheric guardian" or "snecko" or "amogus" or "sentry" or "shapes" or "deca" or "cultist"
                    || slot.EndsWith("outline2", StringComparison.Ordinal),
                "DEFECT_bg" => slot => slot is "backgroundbottom" or "backgroundtop" or "shine" or "sparkle",
                _ => null,
            };
            foreground = isBackdrop is null ? null : SplitSpine(art, original, isBackdrop);
            string? baseAttachment = name switch
            {
                "IRONCLAD_bg" => "bg",
                "REGENT_bg" => "Layer 5",
                "DEFECT_bg" => "backgroundbottom",
                _ => null,
            };
            // Ambient glows can extend beyond the opaque scenery. Measure the base attachment, not the union of the rig.
            if (!art.HasMeta(CoverMeta))
            {
                Rect2? nativeBounds = foreground is not null && baseAttachment is not null
                    ? ReadBaseBounds(original, baseAttachment) : ReadBounds(original);
                if (nativeBounds is not { } bounds) return;
                cover = new Rect2(original.Position + bounds.Position * original.Scale, bounds.Size * original.Scale.Abs());
            }
        }
        if (cover.Size.X <= 1f || cover.Size.Y <= 1f) return;

        // Cover only the backdrop. Character placement below is independent of it. While a secondary Spine is loading,
        // render the original composition as cover, but don't cache that temporary framing as the final pose.
        if (original == null || foreground != null || !art.HasNode(FigureName))
        {
            if (!art.HasMeta(CoverMeta)) art.SetMeta(CoverMeta, cover);
            cover = art.GetMeta(CoverMeta).AsRect2();
        }
        float overscan = name == "DEFECT_bg" ? 1.12f : 1.04f;
        float scale = Mathf.Max(viewport.X / cover.Size.X, viewport.Y / cover.Size.Y) * overscan;
        art.Scale = Vector2.One * scale;
        art.Position = viewport * 0.5f - cover.GetCenter() * scale;
        // The met rectangle is in canvas units, so a check can compare it with the window's visible rect (the art is drawn
        // on a 1680 unit screen that is itself scaled onto the canvas).
        art.SetMeta(ArtFrameMeta, toCanvas * new Rect2(art.Position + cover.Position * scale, cover.Size * scale));

        if (foreground != null && ReadBounds(foreground) is { } figure)
        {
            if (!foreground.HasMeta(FigureMeta)) foreground.SetMeta(FigureMeta, figure);
            figure = foreground.GetMeta(FigureMeta).AsRect2();
            // A little horizontal bleed retains the native waist/weapon crop.
            float figureScale = Mathf.Min(viewport.X * 1.40f / figure.Size.X, viewport.Y * 0.63f / figure.Size.Y);
            float focus = name switch
            {
                "DEFECT_bg" => 0.36f,
                "SILENT_bg" => 0.41f,
                "FOOL_bg" or "NECROBINDER_bg" => 0.44f,
                _ => 0.49f,
            };
            Vector2 target = viewport * new Vector2(focus, 0.39f);
            foreground.Scale = Vector2.One * (figureScale / scale);
            foreground.Position = (target - art.Position - figure.GetCenter() * figureScale) / scale;
            art.SetMeta(FigureFrameMeta, toCanvas * new Rect2(target - figure.Size * figureScale * 0.5f, figure.Size * figureScale));
        }

        foreach (CpuParticles2D particles in AmbientEmitters(art))
        {
            // Only expand the ambient rectangular emitters, not local point glows or bone effects.
            if (particles.EmissionShape != CpuParticles2D.EmissionShapeEnum.Rectangle) continue;
            KeepOriginal(particles, "emission_rect_extents");
            KeepOriginal(particles, "global_position");
            Vector2 center = toCanvas * (viewport * 0.5f);
            if (particles.GlobalPosition.DistanceSquaredTo(center) > 0.25f) particles.GlobalPosition = center;
            // The emitter may itself be rotated and non-uniformly scaled: fit all four screen corners in its local space.
            Vector2 extents = particles.ToLocal(toCanvas * Vector2.Zero).Abs()
                .Max(particles.ToLocal(toCanvas * new Vector2(viewport.X, 0f)).Abs())
                .Max(particles.ToLocal(toCanvas * new Vector2(0f, viewport.Y)).Abs())
                .Max(particles.ToLocal(toCanvas * viewport).Abs()) * 1.10f;
            if (particles.EmissionRectExtents.IsEqualApprox(extents)) continue;
            particles.EmissionRectExtents = extents;
            particles.Restart();
        }
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

    private readonly Dictionary<ulong, CpuParticles2D[]> _emitters = new();

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

    private Node2D? SplitSpine(Control art, Node2D original, Func<string, bool> isBackdrop)
    {
        Node2D? figure = art.GetNodeOrNull<Node2D>(FigureName);
        if (figure == null)
        {
            if (!original.HasSignal("before_world_transforms_change")) return null;
            if (ClassDB.Instantiate("SpineSprite").AsGodotObject() is not Node2D created) return null;
            created.Name = FigureName;
            foreach (string property in new[]
                     { "normal_material", "additive_material", "multiply_material", "screen_material", "skeleton_data_res" })
                created.Set(property, original.Get(property));
            created.Position = original.Position;
            created.Scale = original.Scale;
            created.Hide();
            art.AddChild(created);
            art.MoveChild(created, original.GetIndex() + 1);
            _figures.Add(created.GetInstanceId());
            return null;
        }
        if (figure.IsQueuedForDeletion()) return null;
        // The second playback's skeleton loads asynchronously too; keep the desktop composition until it can be measured.
        if (ReadBounds(figure) is null) return null;
        if (!figure.Visible)
        {
            // The original SpineSprite stays the backdrop, including native constellation skins and hover controls; the
            // second playback of the same animation supplies just the figure.
            MaskSlots(original, name => !isBackdrop(name));
            MaskSlots(figure, isBackdrop);
            if (figure.HasMethod("get_animation_state") &&
                figure.Call("get_animation_state").AsGodotObject() is { } state)
                state.Call("set_animation", "animation", true, 0);
            figure.Show();
        }
        return figure;
    }

    /// <summary>
    /// Hides the slots this playback does not need by taking their attachments away, remembering them so the desktop
    /// composition comes back in landscape, and keeps them hidden when the game's own timeline or a skin change restores
    /// one.
    /// </summary>
    private void MaskSlots(Node2D sprite, Func<string, bool> hide)
    {
        if (sprite.Call("get_skeleton").AsGodotObject() is not { } skeleton) return;
        var slots = skeleton.Call("get_slots").AsGodotArray().Select(value => value.AsGodotObject()!)
            .Where(slot => hide(slot.Call("get_data").AsGodotObject()!.Call("get_attachment_name").AsString())).ToArray();
        var mask = new SpineMask { Sprite = sprite };
        foreach (var slot in slots) mask.Slots.Add((slot, slot.Call("get_attachment")));
        void Apply(GodotObject _)
        {
            if (!SafeAreaCanvas.IsPortrait) return;
            foreach (var slot in slots)
                if (slot.Call("get_attachment").AsGodotObject() is not null) slot.Call("set_attachment", default(Variant));
        }
        Apply(skeleton);
        mask.Handler = Callable.From<GodotObject>(Apply);
        sprite.Connect("before_world_transforms_change", mask.Handler);
        _masks.Add(mask);
    }

    private static Rect2? ReadBaseBounds(Node2D sprite, string baseAttachment)
    {
        if (sprite.Call("get_skeleton").AsGodotObject() is not { } skeleton) return null;
        var others = skeleton.Call("get_slots").AsGodotArray().Select(v => v.AsGodotObject()!)
            .Where(slot => slot.Call("get_data").AsGodotObject()!.Call("get_attachment_name").AsString() != baseAttachment)
            .Select(slot => (Slot: slot, Attachment: slot.Call("get_attachment")))
            .Where(entry => entry.Attachment.AsGodotObject() is not null).ToArray();
        try
        {
            foreach (var entry in others) entry.Slot.Call("set_attachment", default(Variant));
            return ReadBounds(sprite);
        }
        finally
        {
            foreach (var entry in others) entry.Slot.Call("set_attachment", entry.Attachment);
        }
    }

    private static Rect2? ReadBounds(Node2D sprite)
    {
        if (!sprite.HasMethod("get_skeleton") || sprite.Call("get_skeleton").AsGodotObject() is not { } skeleton)
            return null;
        Rect2 bounds = skeleton.Call("get_bounds").AsRect2();
        return bounds.Size.X > 1f && bounds.Size.Y > 1f ? bounds : null;
    }

    private void KeepOriginal(GodotObject node, string property)
    {
        var key = (node.GetInstanceId(), property);
        if (!_originalNodeValues.ContainsKey(key)) _originalNodeValues[key] = node.Get(property);
    }

    private void RestoreArt()
    {
        // The masked backdrop keeps its hidden slots only while portrait is on; unhide them before the figure goes.
        foreach (SpineMask mask in _masks)
        {
            foreach ((GodotObject slot, Variant attachment) in mask.Slots)
                if (GodotObject.IsInstanceValid(slot)) slot.Call("set_attachment", attachment);
            if (mask.Sprite is { } sprite && GodotObject.IsInstanceValid(sprite) && sprite.IsConnected("before_world_transforms_change", mask.Handler))
                sprite.Disconnect("before_world_transforms_change", mask.Handler);
        }
        _masks.Clear();
        foreach (ulong id in _figures)
        {
            if (GodotObject.InstanceFromId(id) is not Node2D figure || !GodotObject.IsInstanceValid(figure)) continue;
            figure.Visible = false;
            figure.QueueFree();
        }
        _figures.Clear();
        foreach (((ulong id, string property), Variant value) in _originalNodeValues)
        {
            if (GodotObject.InstanceFromId(id) is not GodotObject node || !GodotObject.IsInstanceValid(node)) continue;
            node.Set(property, value);
            if (node is CpuParticles2D particles) particles.Restart();
        }
        _originalNodeValues.Clear();
    }

    private void PruneArt()
    {
        _masks.RemoveAll(mask => mask.Sprite is not { } sprite || !GodotObject.IsInstanceValid(sprite));
        _figures.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in _emitters.Keys.Where(id => !GodotObject.IsInstanceIdValid(id)).ToList()) _emitters.Remove(id);
    }
}
