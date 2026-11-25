using System;
using System.Collections;
using System.Collections.Generic;
using Godot;
using STS2Mobile.Platform;
using CombatReflection = STS2Mobile.GameHost.PortraitCombatLayout.CombatReflection;

namespace STS2Mobile.GameHost;

/// <summary>Owns the portrait orb arc, touch targets, value labels, and queue tweens.</summary>
internal sealed class PortraitCombatOrbs(PortraitCombatStage stage)
{
    private readonly Dictionary<ulong, Vector2> _orbDesktopScales = new();
    private readonly Dictionary<ulong, int> _enlargedFonts = new();

    public void Capture(Control creature, CombatReflection refl)
    {
        if (refl.CreatureOrbManagerProp?.GetValue(creature) is Control orbMgr)
            _orbDesktopScales.Add(creature.GetInstanceId(), orbMgr.Scale);
    }

    public void RestoreFonts()
    {
        foreach ((ulong labelId, int fontSize) in _enlargedFonts)
        {
            if (GodotObject.InstanceFromId(labelId) is Label label && GodotObject.IsInstanceValid(label))
                label.AddThemeFontSizeOverride("font_size", fontSize);
        }
        _enlargedFonts.Clear();
    }

    public void EndSession() => _orbDesktopScales.Clear();

    public void PruneDead(List<ulong> dead)
    {
        dead.Clear();
        foreach (ulong id in _orbDesktopScales.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _orbDesktopScales.Remove(id);
        dead.Clear();
        foreach (ulong id in _enlargedFonts.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _enlargedFonts.Remove(id);
    }

    /// <summary>
    /// Lays every player's orb queue out again in the frame that is drawn: the game lays its own arc out from the creature's
    /// bounds after the stage pass (<c>NCreature.SetOrbManagerPosition</c> and <c>NOrbManager.TweenLayout</c>) and does so
    /// again whenever a slot, a channel or an evoke changes the queue, so the phone's arc has to be the queue's last writer.
    /// </summary>
    public void Apply(Control room, CombatReflection refl, PortraitCombatBands? currentBands)
    {
        if (!stage.HasOrbs || currentBands is not { } bands || refl.RoomCreatureNodesField?.GetValue(room) is not IEnumerable creatures)
            return;
        foreach (object? item in creatures)
        {
            if (item is not Control creature || !GodotObject.IsInstanceValid(creature) || creature.IsQueuedForDeletion())
                continue;
            object? entity = refl.CreatureEntityProp?.GetValue(creature);
            if (entity == null || !refl.IsPlayerSide(entity) || !refl.CreatureHasOrbs(creature))
                continue;
            if (refl.CreatureOrbManagerProp?.GetValue(creature) is Control orbMgr)
                ApplyOrbArc(creature, orbMgr, refl, bands);
        }
    }

    // The queue's own bookkeeping, kept on the nodes it describes: a fought room's orbs are freed with it, so none of it
    // outlives the fight and landscape has nothing to restore but the touched node properties.
    private const string OrbCountMeta = "phone_orb_count";
    private const string OrbLabelWidthMeta = "phone_orb_label_width";
    private const string OrbTweenMeta = "phone_orb_tween";
    private const string OrbTargetMeta = "phone_orb_target";
    private const string OrbContainerHomeMeta = "phone_orb_container_home";
    private const string OrbBoundsHomeMeta = "phone_orb_bounds_home";
    private const string OrbBoundsPivotMeta = "phone_orb_bounds_pivot";
    private const string OrbLabelZMeta = "phone_orb_label_z";
    private const string OrbLabelMouseMeta = "phone_orb_label_mouse";
    private const string OrbContainerRefMeta = "phone_orb_container_ref";
    private const string OrbBoundsRefMeta = "phone_orb_bounds_ref";

    // The one list every queue walk fills: the layout and the gate both use it, every frame of an orb fight.
    private readonly List<Control> _orbScratch = new();
    // The places the running retarget tween is heading to, in the container's own coordinates: one entry per orb.
    private Vector2[] _orbTargetScratch = new Vector2[16];

    /// <summary>
    /// Places one player's orb queue on the phone. The game's own layout is a desktop arc whose radius is a function of the
    /// queue's capacity, so it cannot fit a portrait stage at a readable size: the queue is re-anchored and re-spaced here,
    /// its native order (index 0 rightmost/front, the last leftmost/back), art, value labels and evoke effects untouched.
    /// One to three orbs keep the game's own arc, tightened to the phone; four take one wide arc that leaves no gap between
    /// neighbours; five to ten take one compact arc above the owner whose pitch is the phone's overlapping spacing, widened
    /// by the widest value label and narrowed by the safe width or an enemy lane. Tall phones keep the arc under the relic
    /// band (the stage top); at 9:16 it may rise over the relic band but never the stone header. Every retarget is one
    /// 0.45 s sine tween from the orbs' current places, so a channel, evoke, overflow or capacity change glides.
    /// </summary>
    private void ApplyOrbArc(Control creature, Control orbMgr, CombatReflection refl, PortraitCombatBands bands)
    {
        if (!GodotObject.IsInstanceValid(orbMgr) || orbMgr.IsQueuedForDeletion() || !orbMgr.IsInsideTree())
            return;
        // The game's own list, not the child count: an evoked orb fades for a quarter second as a child of the manager.
        int count = OrbQueue(orbMgr, refl, _orbScratch);
        if (count == 0)
            return;
        if (OrbContainerOf(orbMgr) is not { } container)
            return;
        if (stage.HitboxOf(creature) is not { } hitbox || !GodotObject.IsInstanceValid(hitbox))
            return;
        Rect2 body = hitbox.GetGlobalRect();
        if (body.Size.Y < 1f)
            return;

        // One world size for the orbs whatever scale the body is drawn at: the manager hangs off the creature, so its own
        // scale is the world size divided by the creature's (the clamp keeps a tiny boss-fight actor's orbs from growing
        // past the accepted look). The game rewrites it from the visuals, so it is re-asserted here every pass.
        float parentScale = 1f;
        if (orbMgr.GetParent() is Control parent && GodotObject.IsInstanceValid(parent))
            parentScale = MathF.Max(0.0001f, parent.GetGlobalTransform().Scale.Y);
        float localScale = Math.Clamp(PortraitOrbMath.OrbWorldScale / parentScale, 0.6f, 2.4f);
        float orbWorld = localScale * parentScale;
        var scale = new Vector2(localScale, localScale);
        if ((orbMgr.Scale - scale).LengthSquared() > 0.0001f)
            orbMgr.Scale = scale;

        // The orbit line: below the relic band on a tall phone, the stone header's bottom at 9:16 (decision 10, which lets
        // the short canvas's arc rise over the relic strip rather than over the hero).
        float capHeight = MathF.Max(0f, bands.StageTopY - PortraitBands.RunContentTop);
        float orbitTop = bands.Compact ? PortraitBands.HeaderBottom(capHeight) : PortraitBands.RelicBandBottom(capHeight);

        // The label walk is not per frame, but it is also not only on a slot count change: an evoke replaces one orb with
        // another at the same slot, and the new orb's own labels have to be raised and measured too. An orb that is here
        // without a target is the one that just arrived.
        int knownCount = orbMgr.HasMeta(OrbCountMeta) ? orbMgr.GetMeta(OrbCountMeta).AsInt32() : -1;
        float labelWidth = orbMgr.HasMeta(OrbLabelWidthMeta) ? orbMgr.GetMeta(OrbLabelWidthMeta).AsSingle() : 0f;
        bool relabel = knownCount != count;
        for (int i = 0; i < count && !relabel; i++)
            relabel = !_orbScratch[i].HasMeta(OrbTargetMeta);
        if (relabel)
        {
            labelWidth = PrepareOrbArcLabels(count);
            orbMgr.SetMeta(OrbCountMeta, count);
            orbMgr.SetMeta(OrbLabelWidthMeta, labelWidth);
        }

        PortraitOrbArcPlan plan = PlanOrbArc(count, body, bands, orbitTop, labelWidth, orbWorld);
        if (!plan.IsSingle && OrbArcMeetsEnemy(plan))
            plan = PlanOrbArc(count, body, bands, orbitTop, labelWidth, orbWorld, single: true);
        else if (plan.IsSingle && count > 5 &&
                 OrbArcEnemyLane(plan, body.GetCenter().X, out float laneLeft, out float laneRight))
            plan = PlanOrbArc(count, body, bands, orbitTop, labelWidth, orbWorld, single: true, laneLeft, laneRight);

        // The container is pinned on the canvas, joined to the body only through the owner's own line: the game rewrites the
        // manager's place from the creature's bounds, which would carry the queue with it. The origin does not depend on the
        // count or the arc's shape, so a slot arriving only moves the orbs' own targets — the container never jumps.
        if (!container.HasMeta(OrbContainerHomeMeta))
            container.SetMeta(OrbContainerHomeMeta, container.Position);
        float anchorX = Math.Clamp(body.GetCenter().X, PortraitOrbMath.SingleArcSide,
            SafeAreaCanvas.PortraitWidth - PortraitOrbMath.SingleArcSide);
        var anchor = new Vector2(anchorX, body.Position.Y);
        if ((container.GlobalPosition - anchor).LengthSquared() > 0.25f)
            container.GlobalPosition = anchor;

        float touchLocal = plan.TouchWorld / MathF.Max(0.01f, orbWorld);
        if (_orbTargetScratch.Length < count)
            _orbTargetScratch = new Vector2[count];
        Transform2D inverse = container.GetGlobalTransform().AffineInverse();
        bool retarget = false;
        for (int i = 0; i < count; i++)
        {
            Control orb = _orbScratch[i];
            if (!GodotObject.IsInstanceValid(orb) || orb.IsQueuedForDeletion())
            {
                _orbTargetScratch[i] = Vector2.Zero;
                continue;
            }
            SetOrbTouchTarget(orb, touchLocal);
            (float worldX, float worldY) = plan.Position(i);
            Vector2 target = inverse * new Vector2(worldX, worldY);
            _orbTargetScratch[i] = target;
            Vector2 known = orb.HasMeta(OrbTargetMeta) ? orb.GetMeta(OrbTargetMeta).AsVector2() : new Vector2(
                float.PositiveInfinity, float.PositiveInfinity);
            if (MathF.Abs(target.X - known.X) <= 0.25f && MathF.Abs(target.Y - known.Y) <= 0.25f)
                continue;
            orb.SetMeta(OrbTargetMeta, target);
            retarget = true;
        }

        // The game starts its own tween on every queue change (and hands the manager a new one): the first frame it is
        // seen, it is killed and replaced by this one, from the orbs' current places — never a frame at the game's places.
        Tween? running = refl.OrbManagerTweenField?.GetValue(orbMgr) as Tween;
        if (running != null && !GodotObject.IsInstanceValid(running))
            running = null;
        ulong knownTween = orbMgr.HasMeta(OrbTweenMeta) ? (ulong)orbMgr.GetMeta(OrbTweenMeta).AsInt64() : 0UL;
        ulong runningId = running != null ? running.GetInstanceId() : 0UL;
        bool takeover = runningId != 0UL && runningId != knownTween;
        if (!retarget && !takeover)
            return;
        if (running != null && running.IsRunning())
            running.Kill();
        Tween tween = orbMgr.CreateTween().SetParallel();
        for (int i = 0; i < count; i++)
        {
            if (!GodotObject.IsInstanceValid(_orbScratch[i]) || _orbScratch[i].IsQueuedForDeletion())
                continue;
            tween.TweenProperty(_orbScratch[i], "position", _orbTargetScratch[i], PortraitOrbMath.TweenSeconds)
                .SetEase(Tween.EaseType.InOut).SetTrans(Tween.TransitionType.Sine);
        }
        refl.OrbManagerTweenField?.SetValue(orbMgr, tween);
        orbMgr.SetMeta(OrbTweenMeta, (long)tween.GetInstanceId());
    }

    /// <summary>One plan for this queue: the owner's box, the bars, the orbit line and the widest value.</summary>
    private static PortraitOrbArcPlan PlanOrbArc(int count, Rect2 body, PortraitCombatBands bands, float orbitTop,
        float labelWidth, float orbWorld, bool single = false, float laneLeft = 0f, float laneRight = 0f) =>
        PortraitOrbMath.ComputeOrbArc(count, body.GetCenter().X, body.Position.Y, SafeAreaCanvas.PortraitWidth,
            orbitTop, bands.RailTopY, labelWidth, single, laneLeft, laneRight, orbWorld);

    /// <summary>
    /// Whether the game's own arc would put an icon on an enemy: the arc then falls back to the single phone row, as the
    /// phone's arc must not draw through a body, its health bar or its intents. The probe keeps a margin past the icon and
    /// covers the bar above the body; it is measured against the stage pass's enemies, which stand where they are drawn.
    /// </summary>
    private bool OrbArcMeetsEnemy(in PortraitOrbArcPlan plan)
    {
        if (stage.Enemies.IsEmpty)
            return false;
        // The probe measures the whole square the finger hits (the touch region, never the smaller art inside it).
        float reach = plan.BoxHalf + PortraitOrbMath.CanvasEdgeMargin;
        for (int i = 0; i < plan.Count; i++)
        {
            (float x, float y) = plan.Position(i);
            foreach (Control enemy in stage.Enemies)
            {
                if (!GodotObject.IsInstanceValid(enemy) || !enemy.IsVisibleInTree() ||
                    stage.HitboxOf(enemy) is not { } hitbox || !GodotObject.IsInstanceValid(hitbox))
                    continue;
                Rect2 box = hitbox.GetGlobalRect();
                // Health bars and intents stand above the body: the probe grows upward so the arc clears those too.
                box = new Rect2(box.Position - new Vector2(10f, 48f), box.Size + new Vector2(20f, 58f));
                if (x + reach > box.Position.X && box.End.X > x - reach &&
                    y + reach > box.Position.Y && box.End.Y > y - reach)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The lane left of the nearest enemy that the queue's vertical band reaches, as a range for orb centres: a dense queue
    /// next to a boss is squeezed into the wall beside it instead of drawn over its body and bar (the phone's answer to a
    /// boss occupying the stage's middle). False when no enemy stands to the right or the lane cannot hold the queue.
    /// </summary>
    private bool OrbArcEnemyLane(in PortraitOrbArcPlan plan, float ownerCenterX, out float laneLeft, out float laneRight)
    {
        laneLeft = 0f;
        laneRight = 0f;
        if (stage.Enemies.IsEmpty)
            return false;
        // The lane's own clearance covers the whole touch square plus 10 units, so no part of an orb reaches the body.
        float extent = plan.BoxHalf + 10f;
        laneLeft = extent;
        laneRight = SafeAreaCanvas.PortraitWidth - extent;
        bool found = false;
        foreach (Control enemy in stage.Enemies)
        {
            if (!GodotObject.IsInstanceValid(enemy) || !enemy.IsVisibleInTree() ||
                stage.HitboxOf(enemy) is not { } hitbox || !GodotObject.IsInstanceValid(hitbox))
                continue;
            Rect2 box = hitbox.GetGlobalRect();
            if (box.Position.X <= ownerCenterX)
                continue;
            if (box.Position.Y > plan.CenterY + extent || box.End.Y < plan.CenterY - plan.Depth - extent)
                continue;
            laneRight = MathF.Min(laneRight, box.Position.X - extent);
            found = true;
        }
        return found && laneRight > laneLeft + 1f;
    }

    /// <summary>
    /// The value labels, once per slot change: they draw above every orb's art (so a dense queue's numbers stay legible over
    /// the next orb), never take a press, and are raised to the ten-point floor when the orb's own scale leaves them under
    /// it. Returns the widest visible label's drawn width, which widens the queue's pitch.
    /// </summary>
    private float PrepareOrbArcLabels(int count)
    {
        float widest = 0f;
        for (int i = 0; i < count; i++)
        {
            Control orb = _orbScratch[i];
            if (!GodotObject.IsInstanceValid(orb))
                continue;
            if ((orb.GetNodeOrNull<Control>("%LabelContainer") ?? orb.GetNodeOrNull<Control>("LabelContainer")) is not
                { } labels || !GodotObject.IsInstanceValid(labels))
                continue;
            if (!labels.HasMeta(OrbLabelZMeta))
            {
                labels.SetMeta(OrbLabelZMeta, labels.ZIndex);
                labels.SetMeta(OrbLabelMouseMeta, (int)labels.MouseFilter);
            }
            if (labels.ZIndex != 2)
                labels.ZIndex = 2;
            if (labels.MouseFilter != Control.MouseFilterEnum.Ignore)
                labels.MouseFilter = Control.MouseFilterEnum.Ignore;
            int children = labels.GetChildCount();
            for (int c = 0; c < children; c++)
            {
                if (labels.GetChild(c) is not Label label || !label.Visible || string.IsNullOrWhiteSpace(label.Text))
                    continue;
                float drawnScale = MathF.Max(0.01f, label.GetGlobalTransform().Scale.Y);
                EnlargeLabelFont(label,
                    (int)MathF.Ceiling(PortraitOrbMath.ValueWorldHeight / drawnScale));
                widest = MathF.Max(widest, label.GetMinimumSize().X * label.GetGlobalTransform().Scale.X);
            }
        }
        return widest;
    }

    /// <summary>
    /// An orb's touch region, kept at its previous world size while the art scales with the body: the invisible rect is the
    /// finger's target (the orb's own art is smaller in a dense queue), so neighbours overlap like the game's own hover
    /// regions rather than stealing each other's presses.
    /// </summary>
    private static void SetOrbTouchTarget(Control orb, float localSize)
    {
        if (OrbBoundsOf(orb) is not { } bounds)
            return;
        if (!bounds.HasMeta(OrbBoundsHomeMeta))
        {
            bounds.SetMeta(OrbBoundsHomeMeta, new Rect2(bounds.Position, bounds.Size));
            bounds.SetMeta(OrbBoundsPivotMeta, bounds.PivotOffset);
        }
        if (MathF.Abs(bounds.Size.X - localSize) <= 0.05f && MathF.Abs(bounds.Size.Y - localSize) <= 0.05f &&
            MathF.Abs(bounds.Position.X + localSize * 0.5f) <= 0.05f)
            return;
        bounds.Position = new Vector2(-localSize * 0.5f, -localSize * 0.5f);
        bounds.Size = new Vector2(localSize, localSize);
        bounds.PivotOffset = bounds.Size * 0.5f;
    }

    /// <summary>The manager's own orb container, resolved once per fight: the queue's places are relative to it.</summary>
    private static Control? OrbContainerOf(Control orbMgr)
    {
        if (orbMgr.HasMeta(OrbContainerRefMeta) &&
            orbMgr.GetMeta(OrbContainerRefMeta).AsGodotObject() is Control cached && GodotObject.IsInstanceValid(cached))
            return cached;
        Control? found = orbMgr.GetNodeOrNull<Control>("%Orbs");
        if (found != null && GodotObject.IsInstanceValid(found))
            orbMgr.SetMeta(OrbContainerRefMeta, found);
        return found;
    }

    /// <summary>An orb's invisible touch region, resolved once per orb rather than by path every frame.</summary>
    private static Control? OrbBoundsOf(Control orb)
    {
        if (orb.HasMeta(OrbBoundsRefMeta) &&
            orb.GetMeta(OrbBoundsRefMeta).AsGodotObject() is Control cached && GodotObject.IsInstanceValid(cached))
            return cached;
        Control? found = orb.GetNodeOrNull<Control>("Bounds");
        if (found != null && GodotObject.IsInstanceValid(found))
            orb.SetMeta(OrbBoundsRefMeta, found);
        return found;
    }

    /// <summary>
    /// Puts the queue back the way the game draws it in landscape: the labels' own z-index and mouse filter, the orbs' touch
    /// regions, the container's own place and the bookkeeping this layout kept. The manager's scale and place are restored
    /// by the per-creature restore pass.
    /// </summary>
    private void RestoreOrbArcLandscape(Control orbMgr, CombatReflection refl)
    {
        if (!GodotObject.IsInstanceValid(orbMgr) || orbMgr.IsQueuedForDeletion())
            return;
        if (refl.OrbManagerTweenField?.GetValue(orbMgr) is Tween running && GodotObject.IsInstanceValid(running))
        {
            ulong known = orbMgr.HasMeta(OrbTweenMeta) ? (ulong)orbMgr.GetMeta(OrbTweenMeta).AsInt64() : 0UL;
            if (known != 0UL && running.GetInstanceId() == known && running.IsRunning())
                running.Kill();
        }
        if (OrbContainerOf(orbMgr) is { } container && container.HasMeta(OrbContainerHomeMeta))
        {
            container.Position = container.GetMeta(OrbContainerHomeMeta).AsVector2();
            container.RemoveMeta(OrbContainerHomeMeta);
        }
        int count = OrbQueue(orbMgr, refl, _orbScratch);
        for (int i = 0; i < count; i++)
        {
            Control orb = _orbScratch[i];
            if (!GodotObject.IsInstanceValid(orb))
                continue;
            if (OrbBoundsOf(orb) is { } bounds && bounds.HasMeta(OrbBoundsHomeMeta))
            {
                var home = bounds.GetMeta(OrbBoundsHomeMeta).AsRect2();
                bounds.Position = home.Position;
                bounds.Size = home.Size;
                bounds.PivotOffset = bounds.GetMeta(OrbBoundsPivotMeta).AsVector2();
                bounds.RemoveMeta(OrbBoundsHomeMeta);
                bounds.RemoveMeta(OrbBoundsPivotMeta);
            }
            orb.RemoveMeta(OrbTargetMeta);
            if ((orb.GetNodeOrNull<Control>("%LabelContainer") ?? orb.GetNodeOrNull<Control>("LabelContainer")) is not
                { } labels || !GodotObject.IsInstanceValid(labels) || !labels.HasMeta(OrbLabelZMeta))
                continue;
            labels.ZIndex = labels.GetMeta(OrbLabelZMeta).AsInt32();
            labels.MouseFilter = (Control.MouseFilterEnum)labels.GetMeta(OrbLabelMouseMeta).AsInt32();
            labels.RemoveMeta(OrbLabelZMeta);
            labels.RemoveMeta(OrbLabelMouseMeta);
        }
        orbMgr.RemoveMeta(OrbTweenMeta);
        orbMgr.RemoveMeta(OrbCountMeta);
        orbMgr.RemoveMeta(OrbLabelWidthMeta);
    }

    /// <summary>
    /// One manager's queue, in the game's own order: the list it lays out from. An evoked orb keeps its node for the fade
    /// but leaves this list at once, so a queue that is being replaced is never measured with the orb that is leaving.
    /// </summary>
    private static int OrbQueue(Control orbMgr, CombatReflection refl, List<Control> orbs)
    {
        orbs.Clear();
        if (refl.OrbListField?.GetValue(orbMgr) is System.Collections.IList queue)
        {
            for (int i = 0; i < queue.Count; i++)
            {
                if (queue[i] is Control orb && GodotObject.IsInstanceValid(orb) && !orb.IsQueuedForDeletion())
                    orbs.Add(orb);
            }
            return orbs.Count;
        }
        CollectOrbs(orbMgr, orbs);
        return orbs.Count;
    }

    private static void CollectOrbs(Node node, List<Control> orbs)
    {
        int children = node.GetChildCount();
        for (int i = 0; i < children; i++)
        {
            Node child = node.GetChild(i);
            if (child is Control control && control.GetType().Name == "NOrb")
                orbs.Add(control);
            else
                CollectOrbs(child, orbs);
        }
    }

    /// <summary>
    /// Sets a label's font size up to <paramref name="fontSize"/>, keeping the size it had so landscape gets it back.
    /// For labels the game does not size itself (auto size off): it would put its own size back on the next text change.
    /// </summary>
    private void EnlargeLabelFont(Label? label, int fontSize)
    {
        if (label == null || !GodotObject.IsInstanceValid(label)) return;
        int current = label.GetThemeFontSize("font_size");
        if (current >= fontSize) return;
        _enlargedFonts.TryAdd(label.GetInstanceId(), current);
        label.AddThemeFontSizeOverride("font_size", fontSize);
    }

    public void RestoreCreature(Control creature, CombatReflection refl)
    {
        if (refl.CreatureOrbManagerProp?.GetValue(creature) is not Control orbMgr)
            return;
        RestoreOrbArcLandscape(orbMgr, refl);
        orbMgr.Scale = _orbDesktopScales.GetValueOrDefault(creature.GetInstanceId(), Vector2.One);
    }
}
