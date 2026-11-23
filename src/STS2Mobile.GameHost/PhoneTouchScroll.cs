using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// A scroll that starts on a card should scroll. The game's scroll views (the card grids of the
/// deck, the piles and the select, upgrade and transform screens, <c>NCardGrid</c>; and the lists, <c>NScrollableContainer</c>)
/// scroll a mouse drag only when the press lands on their own background: a press on a card goes to the card, so on a phone,
/// where the cards fill the screen, most drags did nothing. Here, in portrait only, a touch that moves mostly up or down by more
/// than <see cref="StartDistance"/> scrolls the view under it wherever it started: the press on the card or button under the
/// finger is let go (it will not pick, select or press when the finger lifts), and the view follows the finger as it does for a
/// drag on its background, springing back past its ends as the game's own drag does. A drag on the background is left to the
/// game. In landscape nothing changes.
/// <para>
/// The same input lets go of the press under a swipe on the screens that scroll themselves or that hold a footer beside a
/// grid: the map (the game drags its own graph, and its points cancel their own press at 20 or 30 units, but the Blitz row's
/// points carry no such value), the loot list (a reward row takes the reward on the release of a press on it) and a card
/// screen's footer, View Upgrades included. There the swipe only lets go of presses, in any direction past
/// <see cref="SwipeMath.ScreenDistance"/>: the map still follows the finger through the game's own drag, and the loot list
/// follows it through the game's own scroll target. A swipe never picks a card, selects a sort, toggles View Upgrades,
/// travels a map node or takes a reward, and a tap inside the slop is still left entirely to the game.
/// </para>
/// </summary>
internal sealed class PhoneTouchScroll
{
    private const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string GridTypeName = "MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid";
    private const string ListTypeName = "MegaCrit.Sts2.Core.Nodes.GodotExtensions.NScrollableContainer";

    // The screens whose own scroll (the map, the loot list) or whose footer (View Upgrades, Back, confirm) shares the
    // screen with a tap: a swipe on one of them lets go of the press under the finger. The grid screens are the two base
    // classes the game's card screens derive from, so a new selection screen is covered by its own base.
    private const string CardsViewScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.NCardsViewScreen";
    private const string GridSelectionScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardGridSelectionScreen";
    private const string CardPileScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.NCardPileScreen";

    /// <summary>How far a touch moves, in canvas units, before it is a scroll: 24 units are about 8 points across a 375-point phone.</summary>
    public const float StartDistance = SwipeMath.GridDistance;

    /// <summary>The loot list's own top resting position, as the game sets it (<c>_targetDragPos</c> starts at 35).</summary>
    private const float LootTopRest = 35f;

    /// <summary>How many touches became a scroll since launch (for the proof runs).</summary>
    public static int ScrollsStarted { get; private set; }

    /// <summary>How many presses a swipe let go since launch (for the proof runs).</summary>
    public static int PressesReleased { get; private set; }

    /// <summary>How many swipes started on a screen's own scroll or footer since launch (for the proof runs).</summary>
    public static int ScreensSwiped { get; private set; }

    private readonly HashSet<ulong> _views = new();
    private readonly HashSet<ulong> _screens = new();
    private readonly List<ulong> _dead = new();
    private readonly Type? _gridType, _listType;
    private readonly Type[] _screenTypes = [];
    private readonly Type? _rewardsType;
    private readonly FieldInfo? _gridTarget, _gridDragging, _listTarget, _listDragging, _listContent, _rewardsTarget;
    private readonly PropertyInfo? _gridCanScroll, _gridLimitTop, _gridLimitBottom, _listScrollbar, _rewardsCanScroll, _rewardsLimit;
    private readonly bool _failed;
    private SceneTree? _tree;

    private bool _down;
    private bool _scrolling;
    private bool _screenSwiped;
    private ulong _view;
    private ulong _screen;
    private Vector2 _start;

    public PhoneTouchScroll()
    {
        try
        {
            Assembly game = typeof(NGame).Assembly;
            _gridType = game.GetType(GridTypeName);
            _listType = game.GetType(ListTypeName);
            _gridTarget = _gridType?.GetField("_targetDrag", InstAll);
            _gridDragging = _gridType?.GetField("_isDragging", InstAll);
            _gridCanScroll = _gridType?.GetProperty("CanScroll", InstAll);
            _gridLimitTop = _gridType?.GetProperty("ScrollLimitTop", InstAll);
            _gridLimitBottom = _gridType?.GetProperty("ScrollLimitBottom", InstAll);
            _listTarget = _listType?.GetField("_targetDragPosY", InstAll);
            _listDragging = _listType?.GetField("_isDragging", InstAll);
            _listContent = _listType?.GetField("_content", InstAll);
            _listScrollbar = _listType?.GetProperty("Scrollbar", InstAll);
            var screens = new List<Type>(4);
            foreach (string name in new[] { CardsViewScreenTypeName, GridSelectionScreenTypeName, CardPileScreenTypeName,
                PortraitBetweenLayout.MapScreenTypeName, PortraitBetweenLayout.RewardsScreenTypeName })
                if (game.GetType(name) is { } screen) screens.Add(screen);
            _screenTypes = screens.ToArray();
            _rewardsType = game.GetType(PortraitBetweenLayout.RewardsScreenTypeName);
            _rewardsTarget = _rewardsType?.GetField("_targetDragPos", InstAll);
            _rewardsCanScroll = _rewardsType?.GetProperty("CanScroll", InstAll);
            _rewardsLimit = _rewardsType?.GetProperty("ScrollLimitBottom", InstAll);
            if (_gridTarget == null || _gridDragging == null || _listTarget == null || _listDragging == null)
            {
                _failed = true;
                GD.PrintErr("Phone touch scroll: the game's scroll views no longer have the expected members; scrolling is left to the game.");
            }
        }
        catch (Exception error)
        {
            _failed = true;
            GD.PrintErr("Phone touch scroll: off: ", error.Message);
        }
    }

    public void Attach(SceneTree tree)
    {
        if (_failed) return;
        _tree = tree;
        tree.NodeAdded += OnNodeAdded;
        tree.Root.WindowInput += OnWindowInput;
    }

    public void Detach()
    {
        if (_tree != null && GodotObject.IsInstanceValid(_tree.Root))
        {
            _tree.NodeAdded -= OnNodeAdded;
            _tree.Root.WindowInput -= OnWindowInput;
        }
        _tree = null;
    }

    private void OnNodeAdded(Node node)
    {
        Type type = node.GetType();
        if ((_gridType != null && _gridType.IsAssignableFrom(type)) || (_listType != null && _listType.IsAssignableFrom(type)))
            _views.Add(node.GetInstanceId());
        foreach (Type screen in _screenTypes)
            if (screen.IsAssignableFrom(type))
            {
                _screens.Add(node.GetInstanceId());
                return;
            }
    }

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
            Transform2D toCanvas = _tree.Root.GetFinalTransform().AffineInverse();
            if (input is InputEventScreenTouch touch && touch.Index == 0)
            {
                if (touch.Pressed)
                {
                    End();
                    _down = true;
                    _start = toCanvas * touch.Position;
                    _view = ViewAt(_start);
                    _screen = ScreenAt(_start);
                }
                else
                {
                    End();
                }
            }
            else if (input is InputEventScreenDrag drag && drag.Index == 0 && _down)
            {
                if (_view != 0) ViewMove(toCanvas * drag.Position, toCanvas.BasisXform(drag.Relative).Y);
                else if (_screen != 0) ScreenMove(toCanvas * drag.Position, toCanvas.BasisXform(drag.Relative).Y);
            }
        }
        catch (Exception error)
        {
            GD.PrintErr("Phone touch scroll: ", error.Message);
            End();
        }
    }

    /// <summary>The view under the finger scrolls, as it does for a drag on its own background.</summary>
    private void ViewMove(Vector2 point, float dy)
    {
        if (GodotObject.InstanceFromId(_view) is not Control view || !GodotObject.IsInstanceValid(view) || !view.IsVisibleInTree())
        {
            _view = 0;
            _screen = 0;
            return;
        }
        if (_scrolling)
        {
            Scroll(view, dy);
            return;
        }
        Vector2 moved = point - _start;
        if (!SwipeMath.Along(moved.Y, moved.X, StartDistance, SwipeMath.Dominance)) return;
        if (Dragging(view) || !Covers(view))
        {
            // A drag on the view's background is the game's own; a view under another screen is not scrolled. This gesture
            // is not ours on the screen behind it either.
            _view = 0;
            _screen = 0;
            return;
        }
        _scrolling = true;
        ScrollsStarted++;
        PressesReleased += PhoneSwipeRelease.Shared.Release(ReleaseSurface(view));
        SetDragging(view, true);
        Scroll(view, moved.Y);
    }

    /// <summary>
    /// The screen under the finger scrolls itself or holds the footer beside a grid: the press under it is let go, in any
    /// direction past <see cref="SwipeMath.ScreenDistance"/>, and the loot list follows the finger through the game's own
    /// scroll target. The map's graph is dragged by the game; the press is all that is taken from it.
    /// </summary>
    private void ScreenMove(Vector2 point, float dy)
    {
        if (GodotObject.InstanceFromId(_screen) is not Control screen || !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree())
        {
            _screen = 0;
            return;
        }
        if (!_screenSwiped)
        {
            Vector2 moved = point - _start;
            if (!SwipeMath.AnyDirection(moved.X, moved.Y, SwipeMath.ScreenDistance)) return;
            _screenSwiped = true;
            ScreensSwiped++;
            PressesReleased += PhoneSwipeRelease.Shared.Release(screen);
            // The movement the swipe took to start counts too, as it does for a grid's own scroll target.
            ScrollLoot(screen, moved.Y);
            return;
        }
        ScrollLoot(screen, dy);
    }

    /// <summary>
    /// The loot list follows the finger: the game's own scroll target (what its wheel and trackpad scroll write) moved by
    /// the drag, inside the range it springs back to (<c>ScrollLimitBottom</c> to 35). Other screens scroll themselves.
    /// </summary>
    private void ScrollLoot(Control screen, float dy)
    {
        if (_rewardsType == null || !_rewardsType.IsInstanceOfType(screen)) return;
        if (_rewardsCanScroll?.GetValue(screen) is not true) return;
        if (!GodotObject.IsInstanceValid(screen) || _rewardsTarget?.GetValue(screen) is not Vector2 target) return;
        float bottom = _rewardsLimit?.GetValue(screen) is float limit ? MathF.Min(limit, LootTopRest) : LootTopRest;
        float y = Math.Clamp(target.Y + dy, bottom, LootTopRest);
        if (y != target.Y) _rewardsTarget.SetValue(screen, new Vector2(target.X, y));
    }

    /// <summary>The screen whose footer and rows a swipe lets go of: the grid's own screen when it has one, else the grid.</summary>
    private Control ReleaseSurface(Control view)
    {
        if (_screen != 0 && GodotObject.InstanceFromId(_screen) is Control screen &&
            GodotObject.IsInstanceValid(screen) && screen.IsAncestorOf(view))
            return screen;
        return view;
    }

    /// <summary>The touch is over: the view springs back inside its ends, as after the game's own drag.</summary>
    private void End()
    {
        if (_scrolling && GodotObject.InstanceFromId(_view) is Control view && GodotObject.IsInstanceValid(view))
        {
            ClampTarget(view);
            SetDragging(view, false);
        }
        _down = false;
        _scrolling = false;
        _screenSwiped = false;
        _view = 0;
        _screen = 0;
    }

    /// <summary>The innermost scroll view that can scroll and holds the point, or 0.</summary>
    private ulong ViewAt(Vector2 point) => Innermost(_views, point, views: true);

    /// <summary>
    /// The innermost screen that holds the point and is not covered by another screen there, or 0. The control under the
    /// finger decides the coverage: a reward card over the loot list, or the stone header over the map, is not the screen's.
    /// </summary>
    private ulong ScreenAt(Vector2 point) => Innermost(_screens, point, views: false);

    /// <summary>The smallest of the tracked controls that holds the point and passes the filter, or 0.</summary>
    private ulong Innermost(HashSet<ulong> set, Vector2 point, bool views)
    {
        ulong best = 0;
        float bestArea = float.MaxValue;
        Control? under = views ? null : _tree?.Root.GuiGetHoveredControl();
        _dead.Clear();
        foreach (ulong id in set)
        {
            if (GodotObject.InstanceFromId(id) is not Control control || !GodotObject.IsInstanceValid(control))
            {
                _dead.Add(id);
                continue;
            }
            if (!control.IsInsideTree() || !control.IsVisibleInTree()) continue;
            if (views ? !CanScroll(control) : (under != null && under != control && !control.IsAncestorOf(under))) continue;
            Vector2 local = control.GetGlobalTransformWithCanvas().AffineInverse() * point;
            if (!new Rect2(Vector2.Zero, control.Size).HasPoint(local)) continue;
            float area = control.Size.X * control.Size.Y;
            if (area < bestArea)
            {
                best = id;
                bestArea = area;
            }
        }
        foreach (ulong id in _dead) set.Remove(id);
        return best;
    }

    private bool IsGrid(Control view) => _gridType != null && _gridType.IsInstanceOfType(view);

    private bool CanScroll(Control view)
    {
        if (IsGrid(view))
            return _gridCanScroll?.GetValue(view) is not false;
        // A list scrolls when its content is taller than its window, which is when the game shows its scroll bar.
        return _listContent?.GetValue(view) is Control && _listScrollbar?.GetValue(view) is CanvasItem { Visible: true };
    }

    private bool Dragging(Control view) =>
        (IsGrid(view) ? _gridDragging : _listDragging)?.GetValue(view) is true;

    private void SetDragging(Control view, bool dragging) =>
        (IsGrid(view) ? _gridDragging : _listDragging)?.SetValue(view, dragging);

    private void Scroll(Control view, float dy)
    {
        FieldInfo? target = IsGrid(view) ? _gridTarget : _listTarget;
        if (target?.GetValue(view) is float y)
            target.SetValue(view, y + dy);
    }

    /// <summary>
    /// Puts the view's scroll target back inside its own limits when the finger lifts. The game settles an overscrolled drag
    /// by an asymptotic lerp, which stops a unit or two short of the end, so a swipe to the first or last row could leave it
    /// a few units off the edge for good. A grid's limits are its own (<c>ScrollLimitTop</c> / <c>ScrollLimitBottom</c>, the
    /// pair its bounce settles between); a list's are its scrollbar's range.
    /// </summary>
    private void ClampTarget(Control view)
    {
        if (IsGrid(view))
        {
            if (_gridTarget?.GetValue(view) is not float target) return;
            float top = _gridLimitTop?.GetValue(view) is float a ? a : 0f;
            float bottom = _gridLimitBottom?.GetValue(view) is float b ? b : 0f;
            float clamped = Math.Clamp(target, MathF.Min(top, bottom), MathF.Max(top, bottom));
            if (clamped != target) _gridTarget.SetValue(view, clamped);
            return;
        }
        if (_listTarget?.GetValue(view) is not float y || view is not ScrollContainer list) return;
        ScrollBar bar = list.GetVScrollBar();
        float limit = MathF.Max(0f, (float)(bar.MaxValue - bar.Page));
        float inside = Math.Clamp(y, 0f, limit);
        if (inside != y) _listTarget.SetValue(view, inside);
    }

    /// <summary>The control under the finger is the view or inside it: no other screen covers the view there.</summary>
    private bool Covers(Control view)
    {
        Control? under = _tree?.Root.GuiGetHoveredControl();
        return under == null || under == view || view.IsAncestorOf(under);
    }
}
