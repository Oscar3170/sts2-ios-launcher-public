using Godot;

namespace STS2Mobile.GameHost;

/// <summary>
/// What a control actually draws, in the control's own units: the union of its visible texture and nine-patch children.
/// The game's cards keep their background art larger than the control rectangle (the mode cards' 705 unit art stands in a
/// 545 unit control), so menu rows space cards by this box rather than by the rectangle, and the desktop checks compare
/// card art the same way.
/// </summary>
internal static class PortraitDrawnBounds
{
    public static Rect2 Of(Control control)
    {
        Transform2D inverse = control.GetGlobalTransformWithCanvas().AffineInverse();
        Rect2? union = null;
        void Walk(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is not Control item || !GodotObject.IsInstanceValid(item) || !item.IsVisibleInTree()) continue;
                if (item is TextureRect or NinePatchRect)
                {
                    Rect2 rect = inverse * item.GetGlobalRect();
                    union = union is { } current ? current.Merge(rect) : rect;
                }
                Walk(item);
            }
        }
        Walk(control);
        return union is { } bounds && bounds.Size.X >= 1f && bounds.Size.Y >= 1f
            ? bounds
            : new Rect2(Vector2.Zero, control.Size);
    }
}
