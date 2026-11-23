using System;
using System.Collections.Generic;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// The single-Spine character scenes (Ironclad, Regent, Defect) where the figure and the scenery share one skeleton. The
/// reference separates them: the original playback keeps only the backdrop slots (which are covered over the screen), and a
/// second playback of the same skeleton and animation keeps only the figure, framed per character so the head/face sits over
/// the info panel. Nothing is reparented: the second Spine is a sibling of the original inside the same character scene.
/// </summary>
internal sealed partial class PortraitMenuLayout
{
    private const string FigureNodeName = "PhonePortraitFigure";

    private sealed class FigureState
    {
        public Node2D? Figure;
        public bool Masked;
        public bool Animated;
    }

    private readonly Dictionary<ulong, FigureState> _figures = new();
    private readonly Dictionary<ulong, Func<string, bool>> _maskRule = new();
    private readonly Dictionary<ulong, List<(GodotObject Slot, Variant Attachment)>> _maskedAttachments = new();
    private readonly HashSet<ulong> _maskWired = new();
    private readonly Dictionary<ulong, (Vector2 Scale, Vector2 Position)> _spineOriginals = new();

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
        if (ReadBaseBounds(spine, baseSlot) is not { } cover || cover.Size.X < 1f || cover.Size.Y < 1f) return false;

        float overscan = name == "DEFECT_bg" ? 1.12f : 1.04f;
        float artScale = Mathf.Max(viewport.X / cover.Size.X, viewport.Y / cover.Size.Y) * overscan;
        if (!float.IsFinite(artScale) || artScale <= 0f) return false;
        art.Scale = new Vector2(artScale, artScale);
        art.Position = viewport * 0.5f - cover.GetCenter() * artScale;
        float figureScale = Mathf.Min(viewport.X * 1.40f / figureBounds.Size.X, viewport.Y * 0.63f / figureBounds.Size.Y);
        if (!float.IsFinite(figureScale) || figureScale <= 0f) return false;
        Vector2 target = viewport * new Vector2(FigureFocus(name), 0.39f);
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

    /// <summary>Puts every slot's original attachment back and drops the second playbacks (landscape).</summary>
    private void RestoreFigures()
    {
        foreach ((ulong _, List<(GodotObject Slot, Variant Attachment)> saved) in _maskedAttachments)
            foreach ((GodotObject slot, Variant attachment) in saved)
                if (GodotObject.IsInstanceValid(slot)) slot.Call("set_attachment", attachment);
        foreach (FigureState state in _figures.Values)
            if (state.Figure is not null && GodotObject.IsInstanceValid(state.Figure)) state.Figure.QueueFree();
        _figures.Clear();
        _maskRule.Clear();
        _maskedAttachments.Clear();
        _maskWired.Clear();
        _spineOriginals.Clear();
    }
}
