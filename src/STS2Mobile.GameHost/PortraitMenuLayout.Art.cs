using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

internal sealed partial class PortraitMenuLayout
{
    // The character art is drawn from the single SpineSprite the scene already has. Characters with a separate backdrop
    // TextureRect frame it as cover and the Spine as the figure; the others are one composition, scaled so it covers the
    // screen and biased towards the side the figure stands on. Nothing is instanced, reparented or masked: this path never
    // touches a skeleton that is not fully ready (a second SpineSprite sharing a skeleton resource, or slot attachments
    // changed under the game's animation state, can abort the engine instead of raising an exception).
    private const string ArtFrameMeta = "phone_character_art_frame";
    private const string FigureFrameMeta = "phone_character_figure_frame";
    private const string CoverMeta = "phone_character_cover";
    private static readonly Vector2 ArtSize = new(2560f, 1200f);

    private readonly Dictionary<ulong, CpuParticles2D[]> _emitters = new();
    private readonly Dictionary<(ulong Id, string Property), Variant> _originalNodeValues = new();
    private readonly Dictionary<ulong, ulong> _artFirstSeen = new();

    private void FrameArt(Control screen, string path)
    {
        Control? container = screen.GetNodeOrNull<Control>(path) ?? screen.GetNodeOrNull<Control>("%AnimatedBg");
        if (container == null || !GodotObject.IsInstanceValid(container) || !container.IsInsideTree()) return;
        Capture(container);
        Place(container, 0f, 0f, screen.Size.X, screen.Size.Y);
        if (container.PivotOffset != Vector2.Zero) container.PivotOffset = Vector2.Zero;
        if (container.Scale != Vector2.One) container.Scale = Vector2.One;

        Transform2D toCanvas = screen.GetGlobalTransformWithCanvas();
        ulong now = Engine.GetProcessFrames();
        foreach (Control art in container.GetChildren().OfType<Control>())
        {
            if (!GodotObject.IsInstanceValid(art) || art.IsQueuedForDeletion()) continue;
            // Give a freshly added scene a frame to finish entering and initialising its Spine before any native Spine call.
            ulong id = art.GetInstanceId();
            if (!_artFirstSeen.TryGetValue(id, out ulong seen))
            {
                _artFirstSeen[id] = seen = now;
            }
            if (now <= seen) continue;
            FrameArtChild(art, container.Size, toCanvas);
        }
    }

    private void FrameArtChild(Control art, Vector2 viewport, Transform2D toCanvas)
    {
        if (viewport.X < 1f || viewport.Y < 1f || !art.IsInsideTree()) return;
        Capture(art);
        if (art.AnchorLeft != 0f || art.AnchorTop != 0f || art.AnchorRight != 0f || art.AnchorBottom != 0f)
            art.AnchorLeft = art.AnchorTop = art.AnchorRight = art.AnchorBottom = 0f;
        if (art.PivotOffset != Vector2.Zero) art.PivotOffset = Vector2.Zero;
        if (art.Size.DistanceSquaredTo(ArtSize) > 0.25f) art.Size = ArtSize;
        if (art.Scale != Vector2.One) art.Scale = Vector2.One;
        if (art.Position != Vector2.Zero) art.Position = Vector2.Zero;

        string name = ArtName(art);
        float overscan = name == "DEFECT_bg" ? 1.12f : 1.04f;
        Node2D? spine = art.GetNodeOrNull<Node2D>("SpineSprite");
        // The skeleton loads asynchronously; leave the art as the game drew it until its bounds can be read.
        if (spine != null && spine.IsClass("SpineSprite") && ReadBounds(spine) is null) return;

        if (art.GetNodeOrNull<TextureRect>("TextureRect") is { Texture: not null } texture)
        {
            // Silent, Necrobinder and Fool already have a separate native backdrop: cover it, then frame the Spine as the
            // figure the way the reference does (it holds only the character for these scenes).
            Rect2 backdrop = texture.GetRect();
            if (backdrop.Size.X < 1f || backdrop.Size.Y < 1f) return;
            float coverScale = Mathf.Max(viewport.X / backdrop.Size.X, viewport.Y / backdrop.Size.Y) * overscan;
            texture.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            if (texture.PivotOffset != Vector2.Zero) texture.PivotOffset = Vector2.Zero;
            texture.Scale = new Vector2(coverScale, coverScale);
            texture.Position = viewport * 0.5f - backdrop.GetCenter() * coverScale;
            art.SetMeta(ArtFrameMeta, toCanvas * new Rect2(texture.Position, backdrop.Size * coverScale));

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
                Vector2 target = viewport * new Vector2(focus, 0.39f);
                spine.Scale = Vector2.One * figureScale;
                spine.Position = target - figure.GetCenter() * figureScale;
                art.SetMeta(FigureFrameMeta, toCanvas * new Rect2(target - figure.Size * figureScale * 0.5f, figure.Size * figureScale));
            }
            else
            {
                art.RemoveMeta(FigureFrameMeta);
            }
        }
        else if (spine != null && ReadBounds(spine) is { } bounds && bounds.Size.X > 1f && bounds.Size.Y > 1f)
        {
            // One composition (backdrop and figure in the same Spine): cover the screen. A small left bias brings the
            // right-of-centre figure closer to the middle of the narrow screen.
            if (!art.HasMeta(CoverMeta)) art.SetMeta(CoverMeta, bounds);
            Rect2 cover = art.GetMeta(CoverMeta).AsRect2();
            float coverScale = Mathf.Max(viewport.X / cover.Size.X, viewport.Y / cover.Size.Y) * overscan;
            float bias = name switch
            {
                "IRONCLAD_bg" or "REGENT_bg" => -0.07f,
                "DEFECT_bg" => -0.10f,
                _ => 0f,
            };
            spine.Scale = Vector2.One * coverScale;
            spine.Position = viewport * new Vector2(0.5f + bias, 0.5f) - cover.GetCenter() * coverScale;
            art.SetMeta(ArtFrameMeta, toCanvas * new Rect2(spine.Position + cover.Position * coverScale, cover.Size * coverScale));
            art.RemoveMeta(FigureFrameMeta);
        }

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

    private void RestoreArt()
    {
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
        foreach (ulong id in _emitters.Keys.Where(id => !GodotObject.IsInstanceIdValid(id)).ToList()) _emitters.Remove(id);
        foreach (ulong id in _artFirstSeen.Keys.Where(id => !GodotObject.IsInstanceIdValid(id)).ToList()) _artFirstSeen.Remove(id);
    }
}
