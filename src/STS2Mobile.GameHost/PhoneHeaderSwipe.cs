using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Horizontal and vertical swipes on the run header's own scroll rows (the potion row and the relic band). The game's
/// buttons are pressed on touch down and act on the release, and the rows' contents are those buttons, so a drag that starts
/// on a potion or a relic is caught here before the GUI sees it: once it has moved <see cref="StartDistance"/> the press under
/// the finger is let go (a swipe then never opens a potion's popup, drinks it, or inspects a relic), its hover tip is taken
/// down, and the row follows the finger. A tap inside the slop is left entirely to the game, so tapping a potion or a relic
/// still does what the game does. Window input runs before the GUI dispatch, which is what lets the press be cleared in time.
/// In landscape nothing is registered and nothing changes.
/// </summary>
internal sealed class PhoneHeaderSwipe
{
    private const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string ClickableTypeName = "MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl";
    private const string TipSetTypeName = "MegaCrit.Sts2.Core.Nodes.HoverTips.NHoverTipSet";

    /// <summary>How far a touch moves, in canvas units, before it is a swipe: 28 units are about 10 points.</summary>
    public const float StartDistance = 28f;

    /// <summary>How far one wheel notch scrolls a row.</summary>
    private const float WheelStep = 160f;

    private readonly Type? _clickableType;
    private readonly FieldInfo? _clickablePressed;
    private readonly Type? _tipSetType;
    private readonly FieldInfo? _tipBlocked;
    private readonly MethodInfo? _clearTips;

    private SceneTree? _tree;
    private ulong _potionRow;
    private ulong _relicRow;

    private bool _down;
    private bool _swiping;
    private bool _horizontal;
    private ulong _row;
    private Vector2 _start;
    private int _initial;

    public PhoneHeaderSwipe()
    {
        try
        {
            Assembly game = typeof(NGame).Assembly;
            _clickableType = game.GetType(ClickableTypeName);
            _clickablePressed = _clickableType?.GetField("_isPressed", InstAll);
            _tipSetType = game.GetType(TipSetTypeName);
            _tipBlocked = _tipSetType?.GetField("shouldBlockHoverTips", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            _clearTips = _tipSetType?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        }
        catch (Exception error)
        {
            GD.PrintErr("Phone header swipe: off: ", error.Message);
        }
    }

    /// <summary>The rows a swipe may scroll; either may be null (a screen with no potions, say).</summary>
    public void SetRows(Control? potionRow, Control? relicRow)
    {
        _potionRow = Valid(potionRow) ? potionRow!.GetInstanceId() : 0;
        _relicRow = Valid(relicRow) ? relicRow!.GetInstanceId() : 0;
        if (_row != 0 && _row != _potionRow && _row != _relicRow) End();
    }

    public void Clear() => End();

    public void Attach(SceneTree tree)
    {
        _tree = tree;
        tree.Root.WindowInput += OnWindowInput;
    }

    public void Detach()
    {
        if (_tree != null && GodotObject.IsInstanceValid(_tree.Root))
            _tree.Root.WindowInput -= OnWindowInput;
        _tree = null;
        End();
    }

    private static bool Valid(Node? node) => node != null && GodotObject.IsInstanceValid(node) && node.IsInsideTree();

    private void OnWindowInput(InputEvent input)
    {
        if (_tree == null || !GodotObject.IsInstanceValid(_tree.Root)) return;
        if (!SafeAreaCanvas.IsPortrait)
        {
            End();
            return;
        }
        try
        {
            switch (input)
            {
                case InputEventScreenTouch touch when touch.Index == 0:
                    if (touch.Pressed) Begin(touch.Position);
                    else End();
                    return;
                case InputEventScreenDrag drag when drag.Index == 0:
                    Move(drag.Position);
                    return;
                // The Linux host and the desktop editor send a mouse; the phone sends touches. Both describe one gesture.
                case InputEventMouseButton button when button.ButtonIndex == MouseButton.Left:
                    if (button.Pressed) Begin(button.Position);
                    else End();
                    return;
                case InputEventMouseMotion motion:
                    Move(motion.Position);
                    return;
                case InputEventMouseButton wheel when wheel.Pressed &&
                    wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown:
                    if (RowAt(ToCanvas(wheel.Position)) is { } row)
                    {
                        Nudge(row, wheel.ButtonIndex == MouseButton.WheelUp ? -WheelStep : WheelStep);
                        row.GetViewport().SetInputAsHandled();
                    }
                    return;
                default:
                    return;
            }
        }
        catch (Exception error)
        {
            GD.PrintErr("Phone header swipe: ", error.Message);
            End();
        }
    }

    private Vector2 ToCanvas(Vector2 point) => _tree == null ? point : _tree.Root.GetFinalTransform().AffineInverse() * point;

    private void Begin(Vector2 point)
    {
        End();
        Vector2 canvas = ToCanvas(point);
        Control? row = RowAt(canvas);
        if (row == null) return;
        _down = true;
        _row = row.GetInstanceId();
        _horizontal = Horizontal(row);
        _start = canvas;
        _initial = row is ScrollContainer clip ? (_horizontal ? clip.ScrollHorizontal : clip.ScrollVertical) : 0;
    }

    private void Move(Vector2 point)
    {
        if (_down && _row != 0)
        {
            if (GodotObject.InstanceFromId(_row) is not Control row || !Valid(row) || !row.IsVisibleInTree())
            {
                End();
                return;
            }
            Vector2 moved = ToCanvas(point) - _start;
            if (!_swiping)
            {
                float along = Horizontal(row) ? moved.X : moved.Y;
                if (MathF.Abs(along) < StartDistance || MathF.Abs(along) < MathF.Abs(Horizontal(row) ? moved.Y : moved.X) * 1.2f)
                    return;
                _swiping = true;
                ClearTips();
                ReleasePresses(row);
            }
            Scroll(row, Horizontal(row) ? -moved.X : -moved.Y);
        }
    }

    private void End()
    {
        if (_swiping && _row != 0 && GodotObject.InstanceFromId(_row) is Control row && Valid(row))
            ReleasePresses(row);
        _down = false;
        _swiping = false;
        _row = 0;
    }

    /// <summary>The registered row whose rect holds the point, if any.</summary>
    private Control? RowAt(Vector2 point)
    {
        Control? row = RowAt(_potionRow, point);
        return row ?? RowAt(_relicRow, point);
    }

    private Control? RowAt(ulong id, Vector2 point)
    {
        if (id == 0 || GodotObject.InstanceFromId(id) is not Control row || !Valid(row) || !row.IsVisibleInTree() ||
            row.Modulate.A < 0.1f || row.MouseBehaviorRecursive == Control.MouseBehaviorRecursiveEnum.Disabled) return null;
        Vector2 local = row.GetGlobalTransformWithCanvas().AffineInverse() * point;
        return new Rect2(Vector2.Zero, row.Size).HasPoint(local) ? row : null;
    }

    /// <summary>The potion row scrolls sideways; the relic band scrolls up and down.</summary>
    private bool Horizontal(Control row) => row.GetInstanceId() == _potionRow;

    /// <summary>Sets the row's offset from the gesture's start, clamped to what it can show.</summary>
    private void Scroll(Control row, float delta) => SetOffset(row, _initial + delta);

    /// <summary>One wheel notch, from wherever the row stands now.</summary>
    private void Nudge(Control row, float amount) => SetOffset(row, Current(row) + amount);

    private void SetOffset(Control row, float value)
    {
        if (row is not ScrollContainer clip) return;
        bool horizontal = Horizontal(row);
        ScrollBar bar = horizontal ? clip.GetHScrollBar() : clip.GetVScrollBar();
        int offset = (int)MathF.Round(Math.Clamp(value, 0f, MathF.Max(0f, (float)(bar.MaxValue - bar.Page))));
        if (horizontal) clip.ScrollHorizontal = offset;
        else clip.ScrollVertical = offset;
    }

    private float Current(Control row) => row is not ScrollContainer clip ? 0f :
        Horizontal(row) ? clip.ScrollHorizontal : clip.ScrollVertical;

    /// <summary>A swipe is not a press: take down the tip that followed the finger onto the row.</summary>
    private void ClearTips()
    {
        if (_tipBlocked?.GetValue(null) is true) return;
        _clearTips?.Invoke(null, null);
    }

    /// <summary>Lets go of every press inside the row, so the potion or relic under the finger does nothing when it lifts.</summary>
    private void ReleasePresses(Node node)
    {
        if (_clickableType == null || _clickablePressed == null) return;
        var stack = new Stack<Node>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            Node current = stack.Pop();
            if (_clickableType.IsInstanceOfType(current) && _clickablePressed.GetValue(current) is true)
                _clickablePressed.SetValue(current, false);
            foreach (Node child in current.GetChildren()) stack.Push(child);
        }
    }
}
