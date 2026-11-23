using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace STS2Mobile.GameHost;

/// <summary>
/// Gives the menu screens' small targets a finger's touch area without moving their art: a control whose drawn rectangle is
/// under 44 points gets an invisible <see cref="PhoneHitArea"/> pad, grown towards the minimum but never past the midpoint
/// to the nearest other target on that side (so every target's own centre stays unambiguous) or past the screen's edge.
/// Plans are kept per screen and rebuilt a few times a second, so newly built rows, tabs or grids get their pads while the
/// screen is open. The pads take no events of their own and are hidden again in landscape.
/// </summary>
internal static class PortraitTargetPads
{
    private const float MinSide = PortraitBands.MinTarget;
    private const int ReplanFrames = 8;
    private const string PadMeta = "phone_target_pad";
    /// <summary>Marks every control this planner owns on the screens it lays out, so another navigation pad pass leaves it alone.</summary>
    public const string PlannedMeta = "phone_target_planned";

    private sealed class Target
    {
        public ulong Id;
        public float SizeX, SizeY;
        public float Left, Top, Right, Bottom;

        public float CenterX => Left + SizeX / 2f;
        public float CenterY => Top + SizeY / 2f;
    }

    private sealed class Plan
    {
        public Vector2 RootSize;
        public int Countdown;
        public readonly List<Target> Targets = new();
        public readonly List<ulong> Applied = new();
        public readonly List<ulong> Planned = new();
    }

    private static readonly Dictionary<ulong, Plan> Plans = new();

    public static void Ensure(Control root)
    {
        if (!GodotObject.IsInstanceValid(root) || !root.IsInsideTree()) return;
        ulong id = root.GetInstanceId();
        Rect2 canvas = root.GetGlobalRect();
        Plans.TryGetValue(id, out Plan? plan);
        if (plan != null && plan.Countdown > 0 && plan.RootSize == canvas.Size)
        {
            plan.Countdown--;
            return;
        }
        plan ??= new Plan();
        Plans[id] = plan;
        plan.RootSize = canvas.Size;
        plan.Countdown = ReplanFrames;
        Replan(root, canvas, plan);
    }

    private static void Replan(Control root, Rect2 canvas, Plan plan)
    {
        foreach (ulong id in plan.Planned)
            if (GodotObject.InstanceFromId(id) is Control planned && GodotObject.IsInstanceValid(planned))
                planned.RemoveMeta(PlannedMeta);
        plan.Planned.Clear();
        foreach (ulong id in plan.Applied)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                RemovePad(control);
        plan.Applied.Clear();
        plan.Targets.Clear();
        Collect(root, plan, canvas);
        foreach (Target target in plan.Targets)
        {
            if (GodotObject.InstanceFromId(target.Id) is not Control control || !GodotObject.IsInstanceValid(control)) continue;
            control.SetMeta(PlannedMeta, true);
            plan.Planned.Add(target.Id);
        }
        foreach (Target target in plan.Targets)
        {
            if (GodotObject.InstanceFromId(target.Id) is not Control control || !GodotObject.IsInstanceValid(control)) continue;
            if (PhoneHitArea.IsContainer(control) || !control.IsInsideTree()) continue;

            float needX = Math.Max(0f, MinSide - target.SizeX);
            float needY = Math.Max(0f, MinSide - target.SizeY);
            if (needX <= 0.5f && needY <= 0.5f) continue;

            float left = 0f, right = 0f, top = 0f, bottom = 0f;
            if (needX > 0f)
            {
                float half = needX / 2f;
                left = Math.Min(half, Math.Min(target.Left - canvas.Position.X,
                                               SideRoom(Neighbour(target, plan, true, false), target.SizeX)));
                right = Math.Min(half, Math.Min(canvas.End.X - target.Right,
                                                SideRoom(Neighbour(target, plan, true, true), target.SizeX)));
                left = Math.Max(0f, left);
                right = Math.Max(0f, right);
            }
            if (needY > 0f)
            {
                float half = needY / 2f;
                top = Math.Min(half, Math.Min(target.Top - canvas.Position.Y,
                                              SideRoom(Neighbour(target, plan, false, false), target.SizeY)));
                bottom = Math.Min(half, Math.Min(canvas.End.Y - target.Bottom,
                                                 SideRoom(Neighbour(target, plan, false, true), target.SizeY)));
                top = Math.Max(0f, top);
                bottom = Math.Max(0f, bottom);
            }
            if (left + right + top + bottom < 0.5f) continue;

            Vector2 scale = control.GetGlobalTransformWithCanvas().Scale.Abs();
            if (scale.X < 0.01f || scale.Y < 0.01f) continue;
            PhoneHitArea.Grow(control, left / scale.X, top / scale.Y, right / scale.X, bottom / scale.Y);
            if (PhoneHitArea.Of(control) is { } area) area.SetMeta(PadMeta, true);
            plan.Applied.Add(target.Id);
        }
    }

    /// <summary>
    /// How far a pad may grow on one side: up to the midpoint to the nearest target on that side. With no neighbour the
    /// whole half of the missing size is available.
    /// </summary>
    private static float SideRoom(float neighbourDistance, float size) =>
        neighbourDistance > 0.5f ? Math.Max(0f, neighbourDistance / 2f - size / 2f) : float.MaxValue;

    /// <summary>
    /// The distance from this target's centre to the nearest other target's centre on one side, among targets whose
    /// cross-axis rectangles overlap; 0 when there is none.
    /// </summary>
    private static float Neighbour(Target target, Plan plan, bool horizontal, bool positive)
    {
        float best = 0f;
        foreach (Target other in plan.Targets)
        {
            if (other.Id == target.Id) continue;
            bool overlaps = horizontal
                ? target.Top < other.Bottom && other.Top < target.Bottom
                : target.Left < other.Right && other.Left < target.Right;
            if (!overlaps) continue;
            float delta = horizontal ? other.CenterX - target.CenterX : other.CenterY - target.CenterY;
            if (positive ? delta <= 0.5f : delta >= -0.5f) continue;
            float distance = Math.Abs(delta);
            if (best <= 0f || distance < best) best = distance;
        }
        return best;
    }

    private static void Collect(Node node, Plan plan, Rect2 canvas)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is not Control control || !GodotObject.IsInstanceValid(control)) continue;
            if (control.IsVisibleInTree() && PortraitAudit.IsTouchTarget(control))
            {
                Rect2 rect = control.GetGlobalRect();
                if (rect.Size.X >= 2f && rect.Size.Y >= 2f && rect.Intersects(canvas))
                {
                    plan.Targets.Add(new Target
                    {
                        Id = control.GetInstanceId(),
                        SizeX = rect.Size.X, SizeY = rect.Size.Y,
                        Left = rect.Position.X, Top = rect.Position.Y, Right = rect.End.X, Bottom = rect.End.Y,
                    });
                }
            }
            Collect(child, plan, canvas);
        }
    }

    private static void RemovePad(Control control)
    {
        if (GodotObject.IsInstanceValid(control) && control.GetNodeOrNull<Control>(PhoneHitArea.NodeName) is { } area &&
            area.HasMeta(PadMeta))
            PhoneHitArea.Remove(control);
    }

    public static void ReleaseAll()
    {
        foreach (Plan plan in Plans.Values)
        {
            foreach (ulong id in plan.Applied)
                if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                    RemovePad(control);
            foreach (ulong id in plan.Planned)
                if (GodotObject.InstanceFromId(id) is Control planned && GodotObject.IsInstanceValid(planned))
                    planned.RemoveMeta(PlannedMeta);
            plan.Applied.Clear();
            plan.Planned.Clear();
            plan.Targets.Clear();
            plan.Countdown = 0;
        }
        Plans.Clear();
    }

    public static void PruneDead()
    {
        foreach (ulong id in Plans.Keys.Where(id => !GodotObject.IsInstanceIdValid(id)).ToList()) Plans.Remove(id);
    }
}
