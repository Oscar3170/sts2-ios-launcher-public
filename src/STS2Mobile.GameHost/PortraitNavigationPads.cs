using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Gives the game's back and gold arrow buttons a 44 point touch area in portrait without touching their art: the native
/// button keeps its size, hover and slide animation, and an invisible <see cref="PhoneHitArea"/> pad reaches out around it
/// (in the control's own units, so a screen drawn at a scale still gets a full-size target). Applied from the node-added pass
/// and kept on the visible buttons by the per-frame pass; removed again in landscape.
/// <para>It is also the single writer of every Back's <c>_showPos</c>/<c>_hidePos</c>: a shown Back puts its rect's top on the
/// screen's footer line, and a disabled one is parked wholly outside the canvas, so no sliver of its enlarged art is left at the
/// left edge (the native hide offset only clears the game's own unscaled button). A Back is never pivoted and its places are
/// written in its parent's coordinates, the space the game's own <c>Position = _showPos</c> assumes.</para>
/// </summary>
internal sealed class PortraitNavigationPads
{
    private const string BackTypeName = "MegaCrit.Sts2.Core.Nodes.CommonUi.NBackButton";
    private const string ArrowTypeName = "MegaCrit.Sts2.Core.Nodes.CommonUi.NGoldArrowButton";
    private const string ShowPosField = "_showPos";
    private const string HidePosField = "_hidePos";
    private const string EnabledField = "_isEnabled";
    private const string MoveTweenField = "_moveTween";
    private const string PosOffsetField = "_posOffset";
    private const string HoverTweenField = "_hoverTween";
    /// <summary>The reference Back drawn 1.8 times its 1680 canvas size on the 1080 canvas: 230 units across.</summary>
    private const float DrawnWidth = 230f;
    /// <summary>How far outside the canvas a hidden Back's art is parked: no sliver may show at the left edge.</summary>
    private const float HideMargin = 24f;
    /// <summary>NBackButton's largest hover factor (its <c>_hoverScale</c>): the parking spot leaves room for the growth.</summary>
    private const float HoverHeadroom = 1.05f;
    private const float FooterBottomMargin = 8f;
    private const BindingFlags AnyField = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

    private static readonly bool InputLog = OS.GetEnvironment("STS2_PORTRAIT_BACK_LOG") == "1";

    private readonly HashSet<ulong> _tracked = new();
    private readonly HashSet<ulong> _loggedInput = new();
    private readonly Dictionary<ulong, Vector2> _paddedAt = new();
    private readonly List<ulong> _dead = new();
    private readonly Dictionary<ulong, (Vector2 Show, Vector2 Hide, Vector2 Pivot, Vector2 Scale)> _originalPositions = new();
    private readonly Dictionary<ulong, float> _backScales = new();
    private readonly HashSet<ulong> _hoveredBacks = new();
    private readonly HashSet<ulong> _hoverWired = new();
    private readonly Dictionary<ulong, (Vector2 Show, Vector2 Hide)> _placedTargets = new();
    /// <summary>The enchant legend a screen draws, or null when it has none; resolved once per screen, not per frame.</summary>
    private readonly Dictionary<ulong, Control?> _footerLegends = new();
    /// <summary>The members one concrete button type declares; resolved once per type, never applied to another type.</summary>
    private sealed class Fields
    {
        public FieldInfo? ShowPos, HidePos, Enabled, MoveTween, PosOffset, HoverTween;
    }

    private static readonly Dictionary<Type, Fields> FieldCache = new();

    private static Fields FieldsFor(Type type)
    {
        if (FieldCache.TryGetValue(type, out Fields? fields)) return fields;
        fields = new Fields
        {
            ShowPos = type.GetField(ShowPosField, AnyField),
            HidePos = type.GetField(HidePosField, AnyField),
            Enabled = type.GetField(EnabledField, AnyField),
            MoveTween = type.GetField(MoveTweenField, AnyField),
            PosOffset = type.GetField(PosOffsetField, AnyField),
            HoverTween = type.GetField(HoverTweenField, AnyField),
        };
        FieldCache[type] = fields;
        return fields;
    }

    public static bool Handles(string? typeName) => typeName == BackTypeName || typeName == ArrowTypeName;

    public void Attach() => RenderingServer.FramePreDraw += OnPreDraw;

    public void Detach() => RenderingServer.FramePreDraw -= OnPreDraw;

    /// <summary>
    /// The Back is drawn larger than its native rect: the game owns Scale for the hover/press animation (and resets it to one
    /// on every enable), so the reference size is applied after the game's tweens and before the draw, re-based onto whatever
    /// hover factor the game's own hover tween left for this frame.
    /// </summary>
    private void OnPreDraw()
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        foreach ((ulong id, float scale) in _backScales)
        {
            if (GodotObject.InstanceFromId(id) is not Control control || !GodotObject.IsInstanceValid(control)) continue;
            if (!control.IsVisibleInTree()) continue;
            Fields fields = FieldsFor(control.GetType());
            float factor = 1f;
            if (fields.HoverTween?.GetValue(control) is Tween hover && hover.IsValid() && hover.IsRunning())
                factor = Math.Clamp(control.Scale.X, 1f, 1.05f);
            else if (_hoveredBacks.Contains(id)) factor = 1.05f;
            var target = new Vector2(scale * factor, scale * factor);
            if (!control.Scale.IsEqualApprox(target)) control.Scale = target;
            // The art scales about the rect's top-left, the corner the hit test uses; a pivot would draw it away from that rect.
            if (control.PivotOffset != Vector2.Zero) control.PivotOffset = Vector2.Zero;
        }
    }

    public void Track(Control control)
    {
        ulong id = control.GetInstanceId();
        if (!_tracked.Add(id)) return;
        if (!IsBack(control)) return;
        if (_hoverWired.Add(id))
        {
            // The game's hover tween writes absolute 1/1.05 scale values; the pre-draw pass needs to know the hover state.
            control.Connect("Focused", Callable.From<NClickableControl>(_ => { if (SafeAreaCanvas.IsPortrait) _hoveredBacks.Add(id); }));
            control.Connect("Unfocused", Callable.From<NClickableControl>(_ => _hoveredBacks.Remove(id)));
        }
        if (!InputLog || !_loggedInput.Add(id)) return;
        // Gated one-line trace (STS2_PORTRAIT_BACK_LOG=1): which Back received a press/release, where it is, and what the
        // engine says is under the pointer, so a lost tap can be attributed to the press or to the hit test.
        control.Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(input => NoteBackInput(control, input)));
    }

    private static void NoteBackInput(Control control, InputEvent input)
    {
        string kind = input switch
        {
            InputEventMouseButton mouse => $"mouse {(mouse.Pressed ? "press" : "release")} {mouse.ButtonIndex}",
            InputEventScreenTouch touch => $"touch {(touch.Pressed ? "press" : "release")} #{touch.Index}",
            _ => input.GetClass(),
        };
        if (kind.StartsWith("InputEvent", StringComparison.Ordinal)) return;
        Fields fields = FieldsFor(control.GetType());
        Control? hovered = control.GetViewport().GuiGetHoveredControl();
        GD.Print($"Portrait back input: {control.Name} {kind} at ({control.GlobalPosition.X:F1},{control.GlobalPosition.Y:F1}) " +
                 $"size=({control.Size.X:F1}x{control.Size.Y:F1}) enabled={fields.Enabled?.GetValue(control)} " +
                 $"hovered={(hovered is null ? "none" : hovered.GetType().Name + ":" + hovered.Name)}");
    }

    public void OnProcessFrame()
    {
        if (!SafeAreaCanvas.IsPortrait || _tracked.Count == 0) return;
        _dead.Clear();
        foreach (ulong id in _tracked)
        {
            if (GodotObject.InstanceFromId(id) is not Control control || !GodotObject.IsInstanceValid(control))
            {
                _dead.Add(id);
                continue;
            }
            if (!control.IsVisibleInTree() || control.Size.X < 1f || control.Size.Y < 1f) continue;
            if (IsBack(control)) PlaceBack(control);
            Vector2 scale = control.GetGlobalTransformWithCanvas().Scale.Abs();
            if (scale.X < 0.01f || scale.Y < 0.01f) continue;
            float padX = Mathf.Max(0f, (PortraitBands.MinTarget - control.Size.X * scale.X) * 0.5f) / scale.X;
            float padY = Mathf.Max(0f, (PortraitBands.MinTarget - control.Size.Y * scale.Y) * 0.5f) / scale.Y;
            Control? area = PhoneHitArea.Of(control);
            // Write only when something actually differs: a pad rebuilt every frame between press and release is a tap that
            // can be lost. An area hidden by a screen change is shown again here.
            bool stable = area != null
                && Math.Abs(area.OffsetLeft + padX) < 0.5f && Math.Abs(area.OffsetRight - padX) < 0.5f
                && Math.Abs(area.OffsetTop + padY) < 0.5f && Math.Abs(area.OffsetBottom - padY) < 0.5f;
            if (!stable && (padX > 0f || padY > 0f)) PhoneHitArea.Grow(control, padX, padY, padX, padY);
            _paddedAt[id] = control.Size;
        }
        foreach (ulong id in _dead)
        {
            _tracked.Remove(id);
            _paddedAt.Remove(id);
        }
    }

    public void RestoreLandscape()
    {
        foreach (ulong id in _tracked)
        {
            try
            {
                if (GodotObject.InstanceFromId(id) is not Control control || !GodotObject.IsInstanceValid(control)) continue;
                PhoneHitArea.Remove(control);
                // Only a Back has the slide fields; a gold arrow is untracked here beyond its pad.
                if (!IsBack(control) ||
                    !_originalPositions.TryGetValue(id, out (Vector2 Show, Vector2 Hide, Vector2 Pivot, Vector2 Scale) original))
                    continue;
                Fields fields = FieldsFor(control.GetType());
                if (fields.MoveTween?.GetValue(control) is Tween tween && tween.IsValid()) tween.Kill();
                fields.MoveTween?.SetValue(control, null);
                RestoreBack(control, original);
            }
            catch (Exception error)
            {
                GD.PushWarning($"Portrait nav: could not restore {id}: {error.GetType().Name}: {error.Message}");
            }
        }
        _tracked.Clear();
        _paddedAt.Clear();
        _originalPositions.Clear();
        _placedTargets.Clear();
        _footerLegends.Clear();
        _backScales.Clear();
        _hoveredBacks.Clear();
        _hoverWired.Clear();
    }

    /// <summary>Whether this is a back button (they are the only buttons that cache a slide position).</summary>
    public static bool IsBack(Control control) => control.GetType().FullName == BackTypeName;

    /// <summary>
    /// Places every Back in canvas space. A shown Back sits in the screen's footer: a comparison panel's Cancel and a Back on
    /// the enchant screen (whose legend plate ends 260 units above the canvas bottom) put their top on the comparison buttons'
    /// line, the upgrade screen's Close above its View Upgrades box, and any other screen keeps the game's own footer gap under
    /// its bottom. A hidden Back is parked wholly outside the canvas: the native slide is kept, but its -180 local units cannot
    /// clear a portrait Back drawn from x = 0.
    /// <para>Everything is written to <c>Position</c>, the rect's top-left: that is the corner the game's own <c>_showPos</c>
    /// means (<c>OnWindowChange</c> assigns it straight to <c>Position</c>) and the corner Godot's hit test, <c>GetGlobalRect</c>
    /// and the harness all read, so the art, the tap target and the measured rect cannot drift apart. The button is never pivoted:
    /// a bottom pivot renders the art one height away from the rect that receives the taps.</para>
    /// </summary>
    private void PlaceBack(Control control)
    {
        Fields fields = FieldsFor(control.GetType());
        if (fields.ShowPos is null) return;
        Control? parent = control.GetParent() as Control;
        Vector2 parentScale = parent is not null ? parent.GetGlobalTransform().Scale.Abs() : Vector2.One;
        if (parentScale.X < 0.01f || parentScale.Y < 0.01f) return;
        Rect2 canvas = control.GetViewport().GetVisibleRect();
        // The reference Back is 230 units across, whatever the screen's own scale. The button's own Scale is ours (the game's
        // hover tween is re-based in OnPreDraw).
        float scale = DrawnWidth / (control.Size.X * parentScale.X);
        ulong id = control.GetInstanceId();
        _backScales[id] = scale;
        // Capture the game's own places, pivot and scale before anything below writes over them: leaving portrait has to put
        // the game's button back, not this pass's portrait one.
        if (!_originalPositions.ContainsKey(id))
        {
            var currentShow = (Vector2)fields.ShowPos.GetValue(control)!;
            var currentHide = fields.HidePos is null ? currentShow : (Vector2)fields.HidePos.GetValue(control)!;
            _originalPositions[id] = (currentShow, currentHide, control.PivotOffset, control.Scale);
        }
        if (control.PivotOffset != Vector2.Zero) control.PivotOffset = Vector2.Zero;
        if (!control.Scale.IsEqualApprox(new Vector2(scale, scale))) control.Scale = new Vector2(scale, scale);
        // The box the button occupies on the canvas: its own size through our scale and the parent's.
        float height = control.Size.Y * scale * parentScale.Y;
        // Park clear of everything the button can draw, not just its box: the native art plane is wider than the box and the
        // hover/focus outline lights up around it. `reach` is the art's right edge measured from the box's left edge.
        Rect2 box = CanvasRect(control);
        float reach = Math.Max(DrawnWidth, DrawnUnion(control).End.X - box.Position.X);
        Vector2 offset = fields.PosOffset?.GetValue(control) is Vector2 value ? value : new Vector2(40f, 354f);
        float top;
        if (IsUpgradeClose(control))
        {
            // The reference lifts the upgrade screen's Close above its View Upgrades box (PortraitPreviewMath.CloseTop, 470
            // reference units). The button's scene offsets are in its scaled parent's units and landed it level with the box.
            top = PortraitPreviewMath.CloseTop(canvas.End.Y);
        }
        else if (IsPanelBack(control) || HasFooterLegend(control))
        {
            // The comparison buttons' line (PortraitPreviewMath.ButtonTop): its Cancel and Confirm put their tops on it, and it
            // is the first line under the legend plate, whose bottom is always 260 canvas units above the canvas's bottom.
            top = PortraitPreviewMath.ButtonTop(canvas.End.Y);
        }
        else
        {
            // The game's own footer gap below the screen's bottom edge (the button's own bottom before the screen edge),
            // mapped through the parent's scale; the enlarged art then grows up from that gap.
            float gap = Math.Max(0f, offset.Y - control.Size.Y) * parentScale.Y;
            top = canvas.End.Y - gap - height;
        }
        float highest = Math.Max(canvas.Position.Y + FooterBottomMargin, canvas.End.Y - height - FooterBottomMargin);
        top = Math.Min(Math.Max(top, canvas.Position.Y + FooterBottomMargin), highest);
        var show = LocalPoint(control, new Vector2(canvas.Position.X, top));
        // The native slide moves one button width left (NBackButton._hideOffset is -180 local units); keep its direction and
        // timing, but clear everything the button can draw, plus the hover growth about the top-left it scales from.
        var hide = LocalPoint(control, new Vector2(canvas.Position.X - reach * HoverHeadroom - HideMargin, top));
        bool changed = !_placedTargets.TryGetValue(id, out (Vector2 Show, Vector2 Hide) placed)
            || placed.Show.DistanceTo(show) > 0.5f || placed.Hide.DistanceTo(hide) > 0.5f;
        bool enabled = fields.Enabled?.GetValue(control) is true;
        Vector2 target = enabled ? show : hide;
        Tween? tween = fields.MoveTween?.GetValue(control) as Tween;
        if (changed)
        {
            _placedTargets[id] = (show, hide);
            fields.ShowPos.SetValue(control, show);
            fields.HidePos?.SetValue(control, hide);
            if (tween is not null && tween.IsValid()) tween.Kill();
            fields.MoveTween?.SetValue(control, null);
            if (control.GlobalPosition.DistanceTo(target) > 2f)
            {
                Tween slide = control.CreateTween();
                slide.TweenProperty(control, "global_position", target, 0.35)
                    .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
                fields.MoveTween?.SetValue(control, slide);
            }
            else control.GlobalPosition = target;
            return;
        }
        // The game's own window handler rewrites the cached positions in its local space; restore ours only when it did.
        var storedShow = (Vector2)fields.ShowPos.GetValue(control)!;
        if (storedShow.DistanceTo(show) > 0.5f) fields.ShowPos.SetValue(control, show);
        if (fields.HidePos is not null && fields.HidePos.GetValue(control) is Vector2 storedHide && storedHide.DistanceTo(hide) > 0.5f)
            fields.HidePos.SetValue(control, hide);
        if (tween is not null && tween.IsValid() && tween.IsRunning()) return;
        if (control.GlobalPosition.DistanceTo(target) > 0.5f) control.GlobalPosition = target;
    }

    private static void RestoreBack(Control control, (Vector2 Show, Vector2 Hide, Vector2 Pivot, Vector2 Scale) original)
    {
        Fields fields = FieldsFor(control.GetType());
        if (fields.ShowPos is not null) fields.ShowPos.SetValue(control, original.Show);
        fields.HidePos?.SetValue(control, original.Hide);
        control.PivotOffset = original.Pivot;
        control.Scale = original.Scale;
    }

    /// <summary>A canvas point in the Back's parent's own coordinates, where <c>Position</c> is written.</summary>
    private static Vector2 LocalPoint(Control control, Vector2 canvas)
    {
        if (control.GetParent() is not Control parent || !parent.IsInsideTree()) return canvas;
        return parent.GetGlobalTransform().AffineInverse() * canvas;
    }

    /// <summary>
    /// Everything the button can draw in canvas units: its box and every visible child through their live canvas transforms,
    /// whatever their modulate. A hidden Back is parked beyond all of it (the native art plane is wider than the box and the
    /// outline lights up when focused), so no sliver is ever left at the canvas edge.
    /// </summary>
    private static Rect2 DrawnUnion(Control control)
    {
        Rect2 union = CanvasRect(control);
        var pending = new Stack<Node>();
        foreach (Node child in control.GetChildren()) pending.Push(child);
        while (pending.Count > 0)
        {
            Node node = pending.Pop();
            if (node is Control child && child.IsVisibleInTree() && child.Size.X > 0.5f && child.Size.Y > 0.5f)
                union = union.Merge(CanvasRect(child));
            foreach (Node grandChild in node.GetChildren()) pending.Push(grandChild);
        }
        return union;
    }

    private static Rect2 CanvasRect(Control control)
    {
        Transform2D transform = control.GetGlobalTransformWithCanvas();
        Vector2 corner = transform * Vector2.Zero;
        return new Rect2(corner, (transform * control.Size - corner).Abs());
    }

    /// <summary>
    /// Whether the game lays this Back out inside a comparison panel. Its cached scene offsets then describe the panel's own
    /// box (a tall preview container's corner), not the screen's footer edge, so they cannot seed the portrait footer line.
    /// </summary>
    private static bool IsPanelBack(Control control)
    {
        for (Node? node = control.GetParent(); node is not null && node is not Window; node = node.GetParent())
            if (node is Control parent && parent.Name.ToString().Contains("PreviewContainer", StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// Whether this is the upgrade screen's Close, which the reference lifts above its View Upgrades box. Its own scene offsets
    /// are written in its scaled parent's units and, once the comparison panel has grown it, land it level with that box.
    /// </summary>
    private static bool IsUpgradeClose(Control control)
    {
        if (!string.Equals(control.Name.ToString(), "Close", StringComparison.Ordinal)) return false;
        for (Node? node = control.GetParent(); node is not null && node is not Window; node = node.GetParent())
            if (node is Control screen && screen.GetType().Name == "NDeckUpgradeSelectScreen") return true;
        return false;
    }

    /// <summary>
    /// Whether the Back's screen draws the enchant legend across the bottom of the canvas. Its plate is the bottom-left tip a
    /// shown Back must not be drawn under; the reference layout keeps the legend above the Back's line.
    /// </summary>
    private bool HasFooterLegend(Control control)
    {
        for (Node? node = control.GetParent(); node is not null && node is not Window; node = node.GetParent())
        {
            if (node is not Control screen) continue;
            ulong id = screen.GetInstanceId();
            if (!_footerLegends.TryGetValue(id, out Control? legend))
            {
                // The same lookup the game and the reference make from the selection screen itself.
                legend = screen.GetNodeOrNull<Control>("%EnchantmentDescriptionContainer");
                _footerLegends[id] = legend;
            }
            if (legend is not null && GodotObject.IsInstanceValid(legend) && legend.IsVisibleInTree()) return true;
        }
        return false;
    }

    public void PruneDead()
    {
        _tracked.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in new List<ulong>(_paddedAt.Keys))
            if (!GodotObject.IsInstanceIdValid(id) || !_tracked.Contains(id)) _paddedAt.Remove(id);
        foreach (ulong id in new List<ulong>(_originalPositions.Keys))
            if (!GodotObject.IsInstanceIdValid(id) || !_tracked.Contains(id)) _originalPositions.Remove(id);
        foreach (ulong id in new List<ulong>(_placedTargets.Keys))
            if (!GodotObject.IsInstanceIdValid(id) || !_tracked.Contains(id)) _placedTargets.Remove(id);
        _loggedInput.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _hoveredBacks.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        _hoverWired.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in new List<ulong>(_backScales.Keys))
            if (!GodotObject.IsInstanceIdValid(id) || !_tracked.Contains(id)) _backScales.Remove(id);
        foreach (ulong id in new List<ulong>(_footerLegends.Keys))
            if (!GodotObject.IsInstanceIdValid(id)
                || (_footerLegends[id] is { } legend && !GodotObject.IsInstanceValid(legend)))
                _footerLegends.Remove(id);
    }
}
