using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

internal static partial class PortraitSubmenuLayout
{
    public const string CardLibraryTypeName = Ns + "CardLibrary.NCardLibrary";

    /// <summary>The card library is fitted at the canvas's own width; its header and grid use the full 1080 units.</summary>
    public const float CardLibraryDesignWidth = 1080f;

    // The header: one column of controls across the canvas, 24 units in from each edge. The gaps are small because four
    // rows of 127 unit (44 point) controls, an expanded advanced panel and a visible grid must all fit a 9:16 phone's 1718
    // unit band.
    private const float CardLibraryMargin = 24f;
    private const float CardLibraryTop = 12f;
    private const float CardLibraryGap = 8f;
    private const float CardLibraryRowGap = 4f;
    private const float CardLibraryControlHeight = 127f;
    private const float CardLibrarySearchHeight = 127f;
    private const float CardLibraryPoolWidth = 762f; // six 127 unit pool tabs
    private const float CardLibraryPoolTab = 127f;
    private const int CardLibraryPoolColumns = 6;
    private const float CardLibraryToggleWidth = 258f;
    private const float CardLibraryGridGap = 8f;
    private const float CardLibraryGridBottom = 100f;
    private const int CardLibraryAdvancedRows = 4;
    private const int CardLibrarySearchFontSize = 40;
    private const string CardLibraryToggleName = "PortraitAdvancedToggle";
    private const string CardLibraryToggleScene = "res://scenes/screens/card_library/library_sort_button.tscn";
    private const string CardLibraryHolderTypeName = "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NGridCardHolder";

    private static readonly string[] CardLibraryPoolNames =
    [
        "IroncladPool", "SilentPool", "RegentPool", "NecrobinderPool", "DefectPool", "FoolPool",
        "ColorlessPool", "AncientsPool", "MiscPool",
    ];

    private static readonly string[] CardLibrarySorterNames =
        ["CardTypeSorter", "RaritySorter", "CostSorter", "AlphabetSorter"];

    private static readonly string[] CardLibraryTypeNames = ["AttackType", "SkillType", "PowerType", "OtherType"];
    private static readonly string[] CardLibraryRarityNames = ["CommonRarity", "UncommonRarity", "RareRarity", "OtherRarity"];
    private static readonly string[] CardLibraryCostNames = ["Cost0", "Cost1", "Cost2", "Cost3+", "CostX"];
    private static readonly string[] CardLibraryStatNames = ["MultiplayerCards", "Stats", "Upgrades"];

    /// <summary>One card library screen's portrait state: the advanced panel's state and the native values to put back.</summary>
    private sealed class CardLibraryState
    {
        public bool AdvancedOpen;
        public ulong Toggle;
        public Callable? ToggleHandler;
        public ulong Screen;
        public Vector2 NativeCardSize;
        public bool CardSizeSaved;
        public float CardWidth;
        public readonly Dictionary<ulong, Vector2> HolderScales = new();
    }

    private static readonly Dictionary<ulong, CardLibraryState> CardLibraryStates = new();
    private static SceneTree? _cardLibraryTree;

    // The grid's native layout members. NCardGrid's recycler decides from the whole screen, so in a grid that starts under
    // the header it rebinds the row standing at the grid's clipped foot; the reconcile pass below re-points the same
    // holders at the window the visible area needs, after the game's own pass.
    private static Type? _cardLibraryGridType;
    private static FieldInfo? _cardLibraryCardSizeField, _cardLibraryNeedsReinitField;
    private static Type? _cardLibraryHolderType;
    private static FieldInfo? _cardLibraryHoverTweenField;
    private static FieldInfo? _cardLibraryRowsField, _cardLibrarySlidingIndexField;
    private static PropertyInfo? _cardLibrarySortedCardsProperty;
    private static MethodInfo? _cardLibraryAssignRowMethod, _cardLibraryPositionRowsMethod, _cardLibraryUpdateNavigationMethod;
    private static readonly object[] CardLibraryRowArgs = new object[2];
    private static readonly object[] CardLibraryIndexArgs = new object[1];

    /// <summary>
    /// The card library: a header bar with the native search field, the pool tabs and the four sort buttons, an advanced
    /// filters panel (type, rarity, cost and the view toggles) that opens from a native-styled toggle and starts collapsed,
    /// and the card grid filling the canvas below. The grid is the native grid at three columns: its card size is set so its
    /// own <c>Columns</c> arithmetic lands on three and the holders are scaled to the column, then its <c>InitGrid</c> lays
    /// the rows out again. Everything is re-asserted each frame; the landscape values are captured first and put back by the
    /// shared snapshots, <see cref="SetKept"/> and <see cref="MoveKept"/>.
    /// </summary>
    private static void LayoutCardLibrary(Control screen, float w, float h, Action<Control> capture)
    {
        if (screen.GetNodeOrNull<Control>("Sidebar") is not { } sidebar) return;
        CardLibraryState state = CardLibraryStateFor(screen);
        Control? toggle = EnsureCardLibraryToggle(screen, sidebar, state, capture);
        Control? poolFilters = CardLibraryControl(screen, "PoolFilters");
        Control? search = CardLibraryUnique(screen, "SearchBar");
        Control? shadow = screen.GetNodeOrNull<Control>("Sidebar/Panel/Shadow");

        // Everything the header owns is moved out of the desktop sidebar's containers once. Nothing is freed: the filter
        // controls keep their signals and their game state, and MoveKept puts every parent back in landscape.
        MoveCardLibraryToSidebar(search, sidebar);
        MoveCardLibraryToSidebar(poolFilters, sidebar);
        MoveCardLibraryToSidebar(toggle, sidebar);
        foreach (string name in CardLibrarySorterNames) MoveCardLibraryToSidebar(CardLibraryUnique(screen, name), sidebar);
        foreach (string name in CardLibraryTypeNames.Concat(CardLibraryRarityNames).Concat(CardLibraryCostNames)
                     .Concat(CardLibraryStatNames))
            MoveCardLibraryToSidebar(CardLibraryUnique(screen, name), sidebar);

        if (screen.GetNodeOrNull<Control>("Sidebar/MarginContainer") is { } margins)
            SetKept(margins, "visible", false);

        float content = Math.Max(0f, w - 2f * CardLibraryMargin);
        float y = CardLibraryTop;
        Place(search, CardLibraryMargin, y, content, CardLibrarySearchHeight, capture);
        if (search?.GetNodeOrNull<LineEdit>("TextArea") is { } textArea)
        {
            SetKept(textArea, "theme_override_font_sizes/font_size", CardLibrarySearchFontSize);
            if (search.GetNodeOrNull<Control>("ClearButton") is { } clear)
                Place(clear, content - CardLibraryControlHeight, 0f, CardLibraryControlHeight, CardLibraryControlHeight,
                    capture);
        }
        y += CardLibrarySearchHeight + CardLibraryGap;

        float poolWidth = Math.Min(CardLibraryPoolWidth, Math.Max(CardLibraryControlHeight,
            content - CardLibraryToggleWidth - CardLibraryGap));
        int poolRows = (CardLibraryPoolNames.Length + CardLibraryPoolColumns - 1) / CardLibraryPoolColumns;
        float poolHeight = poolRows * CardLibraryControlHeight;
        if (poolFilters is not null)
        {
            SetKept(poolFilters, "columns", CardLibraryPoolColumns);
            foreach (Control tab in poolFilters.GetChildren().OfType<Control>())
                SetMin(tab, new Vector2(CardLibraryPoolTab, CardLibraryControlHeight), capture);
            Place(poolFilters, CardLibraryMargin, y, poolWidth, poolHeight, capture);
        }
        if (toggle is not null)
        {
            float width = Math.Max(CardLibraryControlHeight, content - poolWidth - CardLibraryGap);
            Place(toggle, CardLibraryMargin + poolWidth + CardLibraryGap, y, width, CardLibraryControlHeight, capture);
            if (toggle.GetNodeOrNull<TextureRect>("HBoxContainer/Image") is { } arrow)
                if (arrow.FlipV != state.AdvancedOpen) arrow.FlipV = state.AdvancedOpen;
        }
        y += Math.Max(poolHeight, CardLibraryControlHeight) + CardLibraryGap;

        float sortWidth = Math.Max(1f, (content - 3f * CardLibraryGap) / 4f);
        for (int i = 0; i < CardLibrarySorterNames.Length; i++)
            Place(CardLibraryUnique(screen, CardLibrarySorterNames[i]),
                CardLibraryMargin + i * (sortWidth + CardLibraryGap), y, sortWidth, CardLibraryControlHeight, capture);
        y += CardLibraryControlHeight + CardLibraryGap;

        // The advanced panel is four 44 point rows: type and rarity four across, cost five, the three view toggles across.
        // Collapsed, the rows are hidden, not unmade, so their ticked state and signals survive an open/collapse.
        float advancedTop = y;
        if (state.AdvancedOpen)
        {
            float typeWidth = Math.Max(1f, (content - 3f * CardLibraryGap) / 4f);
            LayoutCardLibraryRow(screen, CardLibraryTypeNames, CardLibraryMargin, advancedTop, typeWidth, capture);
            advancedTop += CardLibraryControlHeight + CardLibraryRowGap;
            LayoutCardLibraryRow(screen, CardLibraryRarityNames, CardLibraryMargin, advancedTop, typeWidth, capture);
            advancedTop += CardLibraryControlHeight + CardLibraryRowGap;
            float costWidth = Math.Max(1f, (content - 4f * CardLibraryGap) / 5f);
            LayoutCardLibraryRow(screen, CardLibraryCostNames, CardLibraryMargin, advancedTop, costWidth, capture);
            advancedTop += CardLibraryControlHeight + CardLibraryRowGap;
            float statWidth = Math.Max(1f, (content - 2f * CardLibraryGap) / 3f);
            LayoutCardLibraryRow(screen, CardLibraryStatNames, CardLibraryMargin, advancedTop, statWidth, capture);
            y = advancedTop + CardLibraryControlHeight;
        }
        foreach (string name in CardLibraryTypeNames.Concat(CardLibraryRarityNames).Concat(CardLibraryCostNames)
                     .Concat(CardLibraryStatNames))
            if (CardLibraryUnique(screen, name) is { } control)
                SetKept(control, "visible", state.AdvancedOpen);

        float headerBottom = y + CardLibraryGap;
        Place(sidebar, 0f, 0f, w, headerBottom, capture);
        if (shadow is not null)
            Place(shadow, 0f, headerBottom - 4f, w, 4f, capture);

        float gridTop = headerBottom + CardLibraryGridGap;
        float gridBottom = Math.Max(gridTop + 200f, h - CardLibraryGridBottom);
        if (screen.GetNodeOrNull<Control>("%CardGrid") is { } grid)
            LayoutCardLibraryGrid(grid, state, w, gridTop, gridBottom, capture);
        if (screen.GetNodeOrNull<Control>("%NoResultsLabel") is { } noResults)
            Place(noResults, 0f, gridTop, w, gridBottom - gridTop, capture);
    }

    /// <summary>Places one row of advanced filter controls, keeping each at a finger's size.</summary>
    private static void LayoutCardLibraryRow(Control screen, string[] names, float left, float top, float cellWidth,
        Action<Control> capture)
    {
        for (int i = 0; i < names.Length; i++)
        {
            if (CardLibraryUnique(screen, names[i]) is not { } control) continue;
            SetMin(control, new Vector2(CardLibraryControlHeight, CardLibraryControlHeight), capture);
            Place(control, left + i * (cellWidth + CardLibraryGap), top, cellWidth, CardLibraryControlHeight, capture);
        }
    }

    /// <summary>The grid's rectangle and card size: three columns, clipped under the header, touch-scrolled by the game.</summary>
    private static void LayoutCardLibraryGrid(Control grid, CardLibraryState state, float w, float top, float bottom,
        Action<Control> capture)
    {
        ResolveCardLibraryGridReflection();
        capture(grid);
        grid.AnchorLeft = grid.AnchorTop = grid.AnchorRight = grid.AnchorBottom = 0f;
        grid.PivotOffset = Vector2.Zero;
        grid.Scale = Vector2.One;
        if (MathF.Abs(grid.OffsetLeft) > 0.5f) grid.OffsetLeft = 0f;
        if (MathF.Abs(grid.OffsetTop - top) > 0.5f) grid.OffsetTop = top;
        if (MathF.Abs(grid.OffsetRight - w) > 0.5f) grid.OffsetRight = w;
        if (MathF.Abs(grid.OffsetBottom - bottom) > 0.5f) grid.OffsetBottom = bottom;
        if (!grid.ClipContents) grid.ClipContents = true;

        if (grid.GetNodeOrNull<Control>("%ScrollContainer") is { } scroll)
        {
            capture(scroll);
            if (MathF.Abs(scroll.OffsetLeft - PortraitGridMath.SideInset) > 0.5f)
                scroll.OffsetLeft = PortraitGridMath.SideInset;
            if (MathF.Abs(scroll.OffsetRight + PortraitGridMath.SideInset) > 0.5f)
                scroll.OffsetRight = -PortraitGridMath.SideInset;
        }
        if (grid.GetNodeOrNull<Control>("Scrollbar") is { } scrollbar)
        {
            capture(scrollbar);
            if (MathF.Abs(scrollbar.OffsetLeft - PortraitGridMath.ScrollbarLeft) > 0.5f)
                scrollbar.OffsetLeft = PortraitGridMath.ScrollbarLeft;
            if (MathF.Abs(scrollbar.OffsetRight - PortraitGridMath.ScrollbarRight) > 0.5f)
                scrollbar.OffsetRight = PortraitGridMath.ScrollbarRight;
        }

        float cardWidth = PortraitGridMath.CardWidthFor(PortraitGridMath.ScrollWidth(w));
        state.CardWidth = cardWidth;
        var cardSize = new Vector2(cardWidth, PortraitGridMath.CardHeightFor(cardWidth));
        if (_cardLibraryCardSizeField?.GetValue(grid) is Vector2 current)
        {
            if (!state.CardSizeSaved)
            {
                state.CardSizeSaved = true;
                state.NativeCardSize = current;
            }
            if (current.DistanceSquaredTo(cardSize) > 0.25f)
            {
                _cardLibraryCardSizeField.SetValue(grid, cardSize);
                SetCardLibraryNeedsReinit(grid);
            }
        }
        ScaleCardLibraryHolders(grid, state, PortraitGridMath.HolderScale(cardWidth));
        ReconcileCardLibraryWindow(grid, state);
    }

    /// <summary>Gives every holder the column's scale and keeps it there past the game's own unhover tween.</summary>
    private static void ScaleCardLibraryHolders(Control grid, CardLibraryState state, float scale)
    {
        if (grid.GetNodeOrNull<Control>("%ScrollContainer") is not { } scroll) return;
        if (_cardLibraryHolderType is null) return;
        for (int i = 0; i < scroll.GetChildCount(); i++)
        {
            if (scroll.GetChild(i) is not Control holder || holder.GetType() != _cardLibraryHolderType) continue;
            Vector2 current = holder.Scale;
            if (MathF.Abs(current.X - scale) < 0.002f && MathF.Abs(current.Y - scale) < 0.002f) continue;
            ulong id = holder.GetInstanceId();
            if (!state.HolderScales.ContainsKey(id)) state.HolderScales[id] = current;
            if (_cardLibraryHoverTweenField?.GetValue(holder) is Tween tween && GodotObject.IsInstanceValid(tween) &&
                tween.IsValid() && tween.IsRunning()) tween.Kill();
            holder.Scale = new Vector2(scale, scale);
        }
    }

    /// <summary>
    /// Keeps the native row window on the cards the visible grid is showing. The game's recycler measures the whole screen,
    /// so a grid that starts under the header makes it rebind the row at the clipped foot back and forth. This pass runs each
    /// frame after the game's, re-points the same holders at the window the grid needs and does nothing while it is right.
    /// </summary>
    private static void ReconcileCardLibraryWindow(Control grid, CardLibraryState state)
    {
        if (state.CardWidth < 1f) return;
        ResolveCardLibraryGridReflection();
        if (_cardLibraryNeedsReinitField?.GetValue(grid) is true) return;
        if (_cardLibraryRowsField is null || _cardLibrarySortedCardsProperty is null) return;
        if (_cardLibraryRowsField.GetValue(grid) is not System.Collections.IList rows || rows.Count == 0) return;
        if (_cardLibrarySortedCardsProperty.GetValue(grid) is not System.Collections.IList cards || cards.Count == 0) return;
        if (grid.GetNodeOrNull<Control>("%ScrollContainer") is not { } scroll) return;
        int columns = PortraitGridMath.Columns;
        int total = (cards.Count + columns - 1) / columns;
        int pool = rows.Count;
        float pitch = PortraitGridMath.CardHeightFor(state.CardWidth) + PortraitGridMath.CardPadding;
        if (pitch < 1f) return;
        int firstVisible = (int)MathF.Floor((-scroll.Position.Y - PortraitGridMath.ContentTopPadding) / pitch);
        int wanted = Math.Clamp(firstVisible - 1, 0, Math.Max(0, total - pool));
        int current = _cardLibrarySlidingIndexField?.GetValue(grid) is int sliding ? sliding / columns : 0;
        if (wanted == current) return;
        int start = wanted * columns;
        if (_cardLibrarySlidingIndexField != null) _cardLibrarySlidingIndexField.SetValue(grid, start);
        if (_cardLibraryAssignRowMethod == null) return;
        for (int i = 0; i < pool; i++)
        {
            CardLibraryRowArgs[0] = rows[i]!;
            CardLibraryRowArgs[1] = start + i * columns;
            _cardLibraryAssignRowMethod.Invoke(grid, CardLibraryRowArgs);
        }
        CardLibraryIndexArgs[0] = start;
        _cardLibraryPositionRowsMethod?.Invoke(grid, CardLibraryIndexArgs);
        _cardLibraryUpdateNavigationMethod?.Invoke(grid, null);
    }

    private static void SetCardLibraryNeedsReinit(Control grid)
    {
        if (_cardLibraryNeedsReinitField != null) _cardLibraryNeedsReinitField.SetValue(grid, true);
    }

    /// <summary>Resolves the native grid's private layout members once.</summary>
    private static void ResolveCardLibraryGridReflection()
    {
        if (_cardLibraryGridType != null && _cardLibraryRowsField != null) return;
        Assembly game = typeof(MegaCrit.Sts2.Core.Nodes.NGame).Assembly;
        _cardLibraryGridType ??= game.GetType("MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid");
        if (_cardLibraryGridType is { } type)
        {
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            _cardLibraryCardSizeField ??= type.GetField("_cardSize", hidden);
            _cardLibraryNeedsReinitField ??= type.GetField("_needsReinit", hidden);
            if (_cardLibraryRowsField == null)
            {
                const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.DeclaredOnly;
                _cardLibraryRowsField = type.GetField("_cardRows", any);
                _cardLibrarySlidingIndexField = type.GetField("_slidingWindowCardIndex", any);
                _cardLibrarySortedCardsProperty = type.GetProperty("SortedCards", BindingFlags.Instance | BindingFlags.Public);
                foreach (MethodInfo method in type.GetMethods(any))
                {
                    switch (method.Name)
                    {
                        case "AssignCardsToRow" when method.GetParameters().Length == 2:
                            _cardLibraryAssignRowMethod ??= method;
                            break;
                        case "UpdateGridPositions" when method.GetParameters().Length == 1:
                            _cardLibraryPositionRowsMethod ??= method;
                            break;
                        case "UpdateGridNavigation" when method.GetParameters().Length == 0:
                            _cardLibraryUpdateNavigationMethod ??= method;
                            break;
                    }
                }
            }
        }
        _cardLibraryHolderType ??= game.GetType(CardLibraryHolderTypeName);
        if (_cardLibraryHolderType is not null)
            _cardLibraryHoverTweenField ??= FindField(_cardLibraryHolderType, "_hoverTween");
    }

    /// <summary>Finds a unique-named control of the card library, before or after it has been moved out of its container.</summary>
    private static Control? CardLibraryUnique(Control screen, string name) =>
        screen.GetNodeOrNull<Control>("%" + name);

    /// <summary>Finds a control that has no unique name of its own (the pool grid) by its game name.</summary>
    private static Control? CardLibraryControl(Control screen, string name) =>
        screen.FindChild(name, recursive: true, owned: false) as Control;

    private static void MoveCardLibraryToSidebar(Control? control, Control sidebar)
    {
        if (control != null && control.GetParent() != sidebar) MoveKept(control, sidebar);
    }

    private static CardLibraryState CardLibraryStateFor(Control screen)
    {
        ulong id = screen.GetInstanceId();
        if (!CardLibraryStates.TryGetValue(id, out CardLibraryState? state))
        {
            state = new CardLibraryState { Screen = id };
            CardLibraryStates[id] = state;
            ulong screenId = id;
            screen.Connect("visibility_changed", Callable.From(() =>
            {
                if (GodotObject.InstanceFromId(screenId) is Control live && GodotObject.IsInstanceValid(live) && live.Visible &&
                    CardLibraryStates.TryGetValue(screenId, out CardLibraryState? known))
                    known.AdvancedOpen = false;
            }));
            WatchCardLibraryTree(screen);
        }
        return state;
    }

    /// <summary>
    /// The advanced filters toggle: a fresh native sort button (same scene, texture, label and arrow), labelled "Advanced".
    /// It is decked over the header, not part of the scene; the per-frame pass frees it when portrait is turned off.
    /// </summary>
    private static Control? EnsureCardLibraryToggle(Control screen, Control sidebar, CardLibraryState state,
        Action<Control> capture)
    {
        if (state.Toggle != 0 && GodotObject.InstanceFromId(state.Toggle) is Control known &&
            GodotObject.IsInstanceValid(known)) return known;
        Control? toggle = null;
        try
        {
            if (ResourceLoader.Exists(CardLibraryToggleScene))
            {
                using var scene = GD.Load<PackedScene>(CardLibraryToggleScene);
                toggle = scene?.Instantiate() as Control;
            }
        }
        catch (Exception error)
        {
            GD.PushError("Portrait card library: the advanced filters button could not be made: " + error.Message);
        }
        toggle ??= CardLibraryControl(screen, "AlphabetSorter")?.Duplicate() as Control;
        if (toggle is null) return null;
        toggle.Name = CardLibraryToggleName;
        toggle.MouseFilter = Control.MouseFilterEnum.Stop;
        sidebar.AddChild(toggle);
        capture(toggle);
        toggle.Call("SetLabel", "Advanced");
        ulong screenId = screen.GetInstanceId();
        Callable handler = Callable.From((Control _) =>
        {
            if (GodotObject.InstanceFromId(screenId) is Control live && GodotObject.IsInstanceValid(live) &&
                CardLibraryStates.TryGetValue(screenId, out CardLibraryState? liveState))
                liveState.AdvancedOpen = !liveState.AdvancedOpen;
        });
        state.ToggleHandler = handler;
        toggle.Connect("Released", handler);
        state.Toggle = toggle.GetInstanceId();
        return toggle;
    }

    private static void WatchCardLibraryTree(Control screen)
    {
        SceneTree? tree = screen.GetTree();
        if (tree is null || tree == _cardLibraryTree) return;
        if (_cardLibraryTree != null && GodotObject.IsInstanceValid(_cardLibraryTree))
            _cardLibraryTree.ProcessFrame -= OnCardLibraryProcessFrame;
        _cardLibraryTree = tree;
        tree.ProcessFrame += OnCardLibraryProcessFrame;
    }

    /// <summary>
    /// The one pass that must also run when portrait is off: puts the native card size and holder scales back, frees the
    /// header's toggle, and forgets the states. The grid's own <c>_needsReinit</c> lays its rows out at the native size again.
    /// </summary>
    private static void OnCardLibraryProcessFrame()
    {
        if (SafeAreaCanvas.IsPortrait)
        {
            foreach (ulong id in CardLibraryStates.Keys.Where(id => !GodotObject.IsInstanceIdValid(id)).ToList())
                CardLibraryStates.Remove(id);
            return;
        }
        if (CardLibraryStates.Count == 0) return;
        foreach (CardLibraryState state in CardLibraryStates.Values)
        {
            if (GodotObject.InstanceFromId(state.Screen) is Control screen && GodotObject.IsInstanceValid(screen) &&
                screen.GetNodeOrNull<Control>("%CardGrid") is { } grid)
            {
                if (state.CardSizeSaved && _cardLibraryCardSizeField != null)
                {
                    _cardLibraryCardSizeField.SetValue(grid, state.NativeCardSize);
                    SetCardLibraryNeedsReinit(grid);
                }
                if (grid.GetNodeOrNull<Control>("%ScrollContainer") is { } scroll)
                    foreach ((ulong id, Vector2 scale) in state.HolderScales)
                        if (GodotObject.InstanceFromId(id) is Control holder && GodotObject.IsInstanceValid(holder))
                            holder.Scale = scale;
            }
            if (state.Toggle != 0 && GodotObject.InstanceFromId(state.Toggle) is Control toggle &&
                GodotObject.IsInstanceValid(toggle))
            {
                toggle.GetParent()?.RemoveChild(toggle);
                toggle.QueueFree();
            }
        }
        CardLibraryStates.Clear();
    }

    /// <summary>Grows a control's minimum size towards a finger, changing only what differs and never shrinking it.</summary>
    private static void SetMin(Control control, Vector2 size, Action<Control> capture)
    {
        Vector2 current = control.CustomMinimumSize;
        var wanted = new Vector2(Math.Max(size.X, current.X), Math.Max(size.Y, current.Y));
        if (current.DistanceSquaredTo(wanted) <= 0.25f) return;
        capture(control);
        control.CustomMinimumSize = wanted;
    }
}
