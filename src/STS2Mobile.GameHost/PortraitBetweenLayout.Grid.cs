using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Portrait card grids: the deck, the piles and every card selection screen show three cards to a row, the height of the
/// band the screen's own footer leaves, with the deck's sort row standing above the grid and the View Upgrades box in the
/// footer.
/// <para>
/// The game's own rows are kept: the grid's private card size is set so its <c>Columns</c> arithmetic comes out at three and
/// the holders are scaled to the column width, then the native <c>InitGrid</c> is asked to lay the rows out again. Row
/// recycling is never re-implemented. The deck view's sort row is moved out of the scrolling content (it is not freed or
/// disconnected by the move: its buttons register their hotkeys in <c>_EnterTree</c> and unregister them in
/// <c>_ExitTree</c>, and the screen's signals are connected to the buttons themselves), so it stays put while the cards
/// scroll under the grid's clipped top. Each grid's header, scroll insets, card size, holders, clip and the View Upgrades
/// box are re-asserted when the game re-lays them out, and every one of them is captured first and put back in landscape.
/// </para>
/// </summary>
internal sealed partial class PortraitBetweenLayout
{
    private const string DeckViewScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen";
    private const string PileScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.NCardPileScreen";
    private const string CardSelectionNamespace = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection";
    private const string UpgradeSelectTypeName = CardSelectionNamespace + ".NDeckUpgradeSelectScreen";
    private const string EnchantSelectTypeName = CardSelectionNamespace + ".NDeckEnchantSelectScreen";
    private const string TransformSelectTypeName = CardSelectionNamespace + ".NDeckTransformSelectScreen";
    private const string DeckSelectScreenTypeName = CardSelectionNamespace + ".NDeckCardSelectScreen";
    private const string SimpleSelectTypeName = CardSelectionNamespace + ".NSimpleCardSelectScreen";
    private const string CombatPileSelectTypeName = CardSelectionNamespace + ".NCombatPileCardSelectScreen";
    private const string GridHolderTypeName = "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NGridCardHolder";
    private const string RelicInventoryTypeName = "MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventory";
    private const string ViewUpgradesBoxName = "ViewUpgrades";
    private const string SortRowName = "SortingOptions";
    private const string DeckInfoTextName = "BottomText";

    /// <summary>One grid's portrait layout: its screen, its native card size and what this file changed.</summary>
    private sealed class GridState
    {
        public PortraitGridScreen Screen = PortraitGridScreen.DeckSelect;
        public bool KindKnown;
        public Vector2 NativeCardSize;
        public float AppliedCardWidth;
        public float AppliedTop;
        public float AppliedBottom;
        public bool NativeClip;
        public bool ClipSaved;
        public bool YOffsetSaved;
        public int NativeYOffset;
        public ulong Header;
        public ulong HeaderParent;
        public int HeaderIndex;
        public ulong ScreenId;
        public ulong RelicRow;
        public ulong Scroll;
        public ulong Prompt;
        public ulong InfoText;
        public bool Logged;
    }

    private readonly Dictionary<ulong, GridState> _gridStates = new();
    private readonly Dictionary<ulong, PortraitGridScreen> _viewUpgrades = new();

    private static Type? _cardGridType;
    private static FieldInfo? _cardSizeField;
    private static MethodInfo? _initGridMethod;
    private static FieldInfo? _needsReinitField;
    private static PropertyInfo? _yOffsetProperty;
    private static Type? _gridHolderType;
    private static FieldInfo? _hoverTweenField;
    private static float _gridHolderNativeScale = 0.8f;

    // ---- entry points from PortraitBetweenLayout ---------------------------------------------------------------

    /// <summary>
    /// Lays one card grid out for portrait: its viewport between the header band and the screen's footer, three columns and
    /// the deck view's sort row above it. Called for every <c>NCardGrid</c> the tree adds, and again whenever the grid or
    /// its screen moves it.
    /// </summary>
    private void LayoutCardGridPortrait(Control grid, float capHeight, float canvasHeight, float canvasWidth)
    {
        if (grid.GetType().FullName != CardGridTypeName) return;
        ulong id = grid.GetInstanceId();
        if (!_gridStates.TryGetValue(id, out GridState? state))
        {
            state = new GridState();
            _gridStates[id] = state;
        }
        if (!state.KindKnown)
        {
            state.Screen = GridScreenOf(grid, out Control? screen);
            state.ScreenId = screen?.GetInstanceId() ?? 0;
            state.KindKnown = true;
            if (screen != null)
            {
                if (state.Screen == PortraitGridScreen.Enchant)
                    state.Prompt = screen.GetNodeOrNull<Control>("%BottomText")?.GetInstanceId() ?? 0;
                if (state.Screen == PortraitGridScreen.DeckView)
                    state.InfoText = screen.GetNodeOrNull<Control>(DeckInfoTextName)?.GetInstanceId() ?? 0;
            }
        }
        ApplyGridPortrait(grid, state, new Vector2(canvasWidth, canvasHeight), capHeight);
    }

    /// <summary>Each frame: the grid's own rect, the holder scales and the deck's footer are kept where portrait put them.</summary>
    private void KeepGridPortrait(Control grid, float capHeight, Vector2 canvas)
    {
        if (!_gridStates.TryGetValue(grid.GetInstanceId(), out GridState? state)) return;
        float top = GridTopFor(state, capHeight);
        float bottom = GridBottomFor(state, canvas);
        bool drifted = Math.Abs(grid.OffsetTop - top) > 0.5f || Math.Abs(grid.OffsetBottom - bottom) > 0.5f ||
            Math.Abs(grid.OffsetRight - canvas.X) > 0.5f || grid.AnchorBottom != 0f || grid.AnchorRight != 0f ||
            grid.Scale != Vector2.One || !grid.ClipContents;
        if (drifted)
        {
            ApplyGridPortrait(grid, state, canvas, capHeight);
            return;
        }
        if (GridScroll(grid, state) is not { } scroll) return;
        float scrollWidth = PortraitGridMath.ScrollWidth(canvas.X);
        float cardWidth = PortraitGridMath.CardWidthFor(scrollWidth);
        if (Math.Abs(scroll.Size.X - scrollWidth) > 1f || Math.Abs(scroll.OffsetLeft - PortraitGridMath.SideInset) > 0.5f ||
            Math.Abs(state.AppliedCardWidth - cardWidth) > 0.05f)
        {
            ApplyGridPortrait(grid, state, canvas, capHeight);
            return;
        }
        // A holder back at the game's own scale means the grid has just built its rows again (a sort, a preview closing, a
        // resize): the deck view's own pass also puts its 100-unit card offset back, so lay the grid out again once.
        bool rebuilt = ScaleGridHolders(grid, state, PortraitGridMath.HolderScale(cardWidth));
        if (rebuilt && YOffsetOf(grid) != 0) ApplyGridPortrait(grid, state, canvas, capHeight);
    }

    /// <summary>Tracks the View Upgrades box a deck or selection screen's tickbox stands in and places it in the footer.</summary>
    private bool TrackViewUpgradesBox(Control tickbox)
    {
        Control? box = ViewUpgradesBoxOf(tickbox);
        if (box == null) return false;
        PortraitGridScreen screen = GridScreenOf(box, out _);
        if (screen is not (PortraitGridScreen.DeckView or PortraitGridScreen.Upgrade or PortraitGridScreen.Transform)) return false;
        if (!_viewUpgrades.TryAdd(box.GetInstanceId(), screen)) return false;
        UpdateViewUpgradesBox(box, screen, CanvasOf(box));
        return true;
    }

    private void PruneGridInstances()
    {
        foreach (ulong id in new List<ulong>(_gridStates.Keys))
            if (!GodotObject.IsInstanceIdValid(id)) _gridStates.Remove(id);
        foreach (ulong id in new List<ulong>(_viewUpgrades.Keys))
            if (!GodotObject.IsInstanceIdValid(id)) _viewUpgrades.Remove(id);
    }

    /// <summary>Puts the sort row back inside the scrolling content, the grid's card size and clip, and every holder's scale.</summary>
    private void RestoreGridsLandscape()
    {
        foreach ((ulong id, GridState state) in _gridStates)
        {
            if (state.Header != 0 && GodotObject.InstanceFromId(state.Header) is Control header && GodotObject.IsInstanceValid(header) &&
                state.HeaderParent != 0 && GodotObject.InstanceFromId(state.HeaderParent) is Node parent && GodotObject.IsInstanceValid(parent) &&
                header.GetParent() != parent)
            {
                header.Reparent(parent, keepGlobalTransform: false);
                if (parent.GetChildCount() > 0) parent.MoveChild(header, Math.Clamp(state.HeaderIndex, 0, parent.GetChildCount() - 1));
            }
            if (GodotObject.InstanceFromId(id) is not Control grid || !GodotObject.IsInstanceValid(grid)) continue;
            if (state.ClipSaved) grid.ClipContents = state.NativeClip;
            if (state.YOffsetSaved && _yOffsetProperty != null) _yOffsetProperty.SetValue(grid, state.NativeYOffset);
            if (_cardSizeField != null && state.NativeCardSize.X > 0.5f) _cardSizeField.SetValue(grid, state.NativeCardSize);
            RestoreGridHolderScales(grid, state);
            if (_needsReinitField != null) _needsReinitField.SetValue(grid, true);
        }
        _gridStates.Clear();
        _viewUpgrades.Clear();
    }

    // ---- the grid's own rectangle and its cards -----------------------------------------------------------------

    private void ApplyGridPortrait(Control grid, GridState state, Vector2 canvas, float capHeight)
    {
        ResolveGridReflection();
        if (!state.ClipSaved)
        {
            state.NativeClip = grid.ClipContents;
            state.ClipSaved = true;
        }
        if (state.NativeCardSize.X < 50f && _cardSizeField?.GetValue(grid) is Vector2 native) state.NativeCardSize = native;
        ResolveRelicRow(state, grid);
        float headerBottom = PortraitGridMath.HeaderBottom(capHeight + PortraitBands.TopBarHeight, VisibleRelicBottom(state));
        float sortTop = headerBottom + PortraitGridMath.SortHeaderGap;
        float top = PortraitGridMath.GridTop(headerBottom, state.Screen == PortraitGridScreen.DeckView);
        float bottom = GridBottomFor(state, canvas);
        if (bottom - top < 300f) return;

        Capture(grid, captureVisible: false);
        grid.AnchorLeft = grid.AnchorTop = grid.AnchorRight = grid.AnchorBottom = 0f;
        grid.PivotOffset = Vector2.Zero;
        grid.Scale = Vector2.One;
        grid.OffsetLeft = 0f;
        grid.OffsetRight = canvas.X;
        grid.OffsetTop = top;
        grid.OffsetBottom = bottom;
        grid.ClipContents = true;

        // The deck view's sort row is measured from the scroll area's children before it is moved out of them.
        Control? header = SortRowOf(grid, state);
        float scrollWidth = PortraitGridMath.ScrollWidth(canvas.X);
        float cardWidth = PortraitGridMath.CardWidthFor(scrollWidth);
        float gridHeight = bottom - top;

        if (GridScroll(grid, state) is { } scroll)
        {
            Capture(scroll, captureVisible: false);
            scroll.OffsetLeft = PortraitGridMath.SideInset;
            scroll.OffsetRight = -PortraitGridMath.SideInset;
            scroll.Size = new Vector2(scrollWidth, gridHeight);
        }
        if (grid.GetNodeOrNull<Control>("Scrollbar") is { } scrollbar)
        {
            Capture(scrollbar, captureVisible: false);
            scrollbar.OffsetLeft = PortraitGridMath.ScrollbarLeft;
            scrollbar.OffsetRight = PortraitGridMath.ScrollbarRight;
        }

        // The native Columns getter is (scroll + padding) / (card + padding): this card size makes it exactly three, and it is
        // also the size the holders are drawn at, so the rows and the cards line up.
        bool relayout = Math.Abs(state.AppliedCardWidth - cardWidth) > 0.05f;
        if (relayout && _cardSizeField != null)
        {
            _cardSizeField.SetValue(grid, new Vector2(cardWidth, PortraitGridMath.CardHeightFor(cardWidth)));
            state.AppliedCardWidth = cardWidth;
        }
        int yOffset = YOffsetOf(grid);
        if (yOffset != 0 && _yOffsetProperty != null)
        {
            if (!state.YOffsetSaved)
            {
                state.NativeYOffset = yOffset;
                state.YOffsetSaved = true;
            }
            _yOffsetProperty.SetValue(grid, 0);
            relayout = true;
        }
        if (relayout) RelayoutGrid(grid);
        // A sort re-lays the grid out at its top; the game's own lerp can leave a fraction of a unit behind, so put the
        // scroll exactly on the top limit (the same call InitGrid makes, with nothing between here and the frame drawn).
        if (yOffset != 0 && GridScroll(grid, state) is { } topScroll && Math.Abs(topScroll.Position.Y) < 8f)
            grid.Call("SetScrollPosition", 0f);

        state.AppliedTop = top;
        state.AppliedBottom = bottom;
        if (header != null)
            LayoutSortRowPortrait(header, state, canvas, sortTop);
        // The footer box is a sibling of the grid: track it here too, so turning portrait on with the screen already open
        // places it as well as the screen's own tickbox node being added does.
        if ((state.Screen is PortraitGridScreen.DeckView or PortraitGridScreen.Upgrade or PortraitGridScreen.Transform) &&
            state.ScreenId != 0 && GodotObject.InstanceFromId(state.ScreenId) is Control owning &&
            owning.GetNodeOrNull<Control>(ViewUpgradesBoxName) is { } footer && footer.Size.Y >= 1f &&
            _viewUpgrades.TryAdd(footer.GetInstanceId(), state.Screen))
            UpdateViewUpgradesBox(footer, state.Screen, canvas);
        ScaleGridHolders(grid, state, PortraitGridMath.HolderScale(cardWidth));
        LayoutDeckInfoText(state, canvas);
        if (!state.Logged)
        {
            state.Logged = true;
            GD.Print($"Portrait between layout: {grid.GetParent()?.GetType().Name} grid {canvas.X:F0}x{gridHeight:F0} at "
                + $"{top:F0}..{bottom:F0}, three {cardWidth:F0}x{PortraitGridMath.CardHeightFor(cardWidth):F0} cards, "
                + $"holders {PortraitGridMath.HolderScale(cardWidth):F2}.");
        }
    }

    /// <summary>
    /// The grid's top edge: below the top bar and below the relic rows when the strip reaches lower, then below the deck
    /// view's sort row where there is one. The relic strip is live (it scrolls and grows), so this is read each frame.
    /// </summary>
    private static float GridTopFor(GridState state, float capHeight) =>
        PortraitGridMath.GridTop(
            PortraitGridMath.HeaderBottom(capHeight + PortraitBands.TopBarHeight, VisibleRelicBottom(state)),
            state.Screen == PortraitGridScreen.DeckView);

    /// <summary>The lowest visible relic holder's bottom, clamped to the strip it scrolls in (0 when there is none).</summary>
    private static float VisibleRelicBottom(GridState state)
    {
        if (state.RelicRow == 0 || GodotObject.InstanceFromId(state.RelicRow) is not Control row ||
            !GodotObject.IsInstanceValid(row) || !row.IsVisibleInTree()) return 0f;
        Rect2 strip = row.GetParent() is Control parent && parent.ClipContents ? parent.GetGlobalRect() : new Rect2();
        float bottom = 0f;
        for (int i = 0; i < row.GetChildCount(); i++)
        {
            if (row.GetChild(i) is not Control holder || !holder.IsVisibleInTree()) continue;
            Rect2 rect = holder.GetGlobalRect();
            if (strip.HasArea()) rect = rect.Intersection(strip);
            if (rect.HasArea()) bottom = Math.Max(bottom, rect.End.Y);
        }
        return bottom;
    }

    /// <summary>Finds the run's relic inventory once per grid: its holders decide how far the grid's header descends.</summary>
    private static void ResolveRelicRow(GridState state, Control grid)
    {
        if (state.RelicRow != 0 && GodotObject.IsInstanceIdValid(state.RelicRow)) return;
        state.RelicRow = 0;
        if (grid.GetTree()?.Root is not Node root) return;
        foreach (Node node in Descendants(root))
            if (node is Control control && control.GetType().FullName == RelicInventoryTypeName)
            {
                state.RelicRow = control.GetInstanceId();
                return;
            }
    }

    private float GridBottomFor(GridState state, Vector2 canvas)
    {
        if (state.Screen != PortraitGridScreen.Enchant) return PortraitGridMath.ViewportBottom(canvas.Y, state.Screen);
        // The enchant legend sets the prompt's height, so the grid follows where the prompt actually stands.
        float reserve = PortraitGridMath.Footer(PortraitGridScreen.Enchant);
        if (state.Prompt == 0 && GodotObject.InstanceFromId(state.ScreenId) is Control screen)
            state.Prompt = screen.GetNodeOrNull<Control>("%BottomText")?.GetInstanceId() ?? 0;
        if (state.Prompt != 0 && GodotObject.InstanceFromId(state.Prompt) is Control prompt && GodotObject.IsInstanceValid(prompt))
            reserve = PortraitGridMath.PromptReserve(reserve, canvas.Y, prompt.GetGlobalRect().Position.Y);
        return canvas.Y - reserve;
    }

    /// <summary>
    /// Asks the grid to lay its rows out again through the game's own <c>InitGrid</c> (the method its <c>_Process</c> calls
    /// when it decides it needs one), so the new card size and column count are in place before the frame is drawn.
    /// </summary>
    private static void RelayoutGrid(Control grid)
    {
        ResolveGridReflection();
        if (_initGridMethod != null)
        {
            try
            {
                _initGridMethod.Invoke(grid, null);
                return;
            }
            catch (Exception error)
            {
                GD.PrintErr("Portrait between layout: the card grid could not lay itself out: ", error.Message);
            }
        }
        // No way in this frame: ask the grid to lay itself out on its next one, as its own resize notification does.
        if (_needsReinitField == null)
            GD.PrintErr("Portrait between layout: the card grid's layout method was not found; cards keep the game's size.");
        else
            _needsReinitField.SetValue(grid, true);
    }

    /// <summary>
    /// Gives every grid card holder the column's scale and keeps it there: the game's own unhover tween pulls a card back to
    /// 0.8, which would shrink it under the three-column pitch. Returns whether a holder was still at the game's own scale,
    /// which means the grid has just built its rows again.
    /// </summary>
    private static bool ScaleGridHolders(Control grid, GridState state, float scale)
    {
        if (GridScroll(grid, state) is not { } scroll) return false;
        ResolveGridReflection();
        if (_gridHolderType == null) return false;
        float native = _gridHolderNativeScale;
        bool rebuilt = false;
        int count = scroll.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            if (scroll.GetChild(i) is not Control holder || holder.GetType() != _gridHolderType) continue;
            Vector2 current = holder.Scale;
            if (Math.Abs(current.X - native) < 0.002f && Math.Abs(current.Y - native) < 0.002f) rebuilt = true;
            if (Math.Abs(current.X - scale) < 0.002f && Math.Abs(current.Y - scale) < 0.002f) continue;
            if (_hoverTweenField?.GetValue(holder) is Tween tween && GodotObject.IsInstanceValid(tween) && tween.IsValid() && tween.IsRunning())
                tween.Kill();
            holder.Scale = new Vector2(scale, scale);
        }
        return rebuilt;
    }

    private static void RestoreGridHolderScales(Control grid, GridState state)
    {
        if (GridScroll(grid, state) is not { } scroll) return;
        ResolveGridReflection();
        if (_gridHolderType == null) return;
        var native = new Vector2(_gridHolderNativeScale, _gridHolderNativeScale);
        int count = scroll.GetChildCount();
        for (int i = 0; i < count; i++)
            if (scroll.GetChild(i) is Control holder && holder.GetType() == _gridHolderType && holder.Scale != native)
                holder.Scale = native;
    }

    // ---- the deck view's sort row ------------------------------------------------------------------------------

    private static Control? SortRowOf(Control grid, GridState state)
    {
        if (state.Header != 0 && GodotObject.InstanceFromId(state.Header) is Control known && GodotObject.IsInstanceValid(known))
            return known;
        if (state.Screen != PortraitGridScreen.DeckView) return null;
        Control? header = GridScroll(grid, state)?.GetNodeOrNull<Control>(SortRowName);
        if (header == null && GodotObject.InstanceFromId(state.ScreenId) is Control screen)
            header = screen.GetNodeOrNull<Control>(SortRowName);
        if (header == null) return null;
        state.Header = header.GetInstanceId();
        return header;
    }

    /// <summary>
    /// The deck's four sort buttons stand in one row above the grid: the scroll area's width, 135 units tall with 44 point
    /// buttons, the text as large as fits and each button sized so its own text and arrow still show. The row is moved out of
    /// the scrolling content once, which frees nothing and disconnects nothing.
    /// </summary>
    private void LayoutSortRowPortrait(Control header, GridState state, Vector2 canvas, float headerTop)
    {
        if (GodotObject.InstanceFromId(state.ScreenId) is not Control screen || !GodotObject.IsInstanceValid(screen)) return;
        if (header.GetParent() != screen)
        {
            if (state.HeaderParent == 0)
            {
                state.HeaderParent = header.GetParent()?.GetInstanceId() ?? 0;
                state.HeaderIndex = header.GetIndex();
            }
            header.Reparent(screen, keepGlobalTransform: false);
        }
        Capture(header, captureVisible: false);
        header.AnchorLeft = header.AnchorTop = header.AnchorRight = header.AnchorBottom = 0f;
        header.PivotOffset = Vector2.Zero;
        header.Scale = Vector2.One;
        header.OffsetLeft = PortraitGridMath.SideInset;
        header.OffsetTop = headerTop;
        header.OffsetRight = PortraitGridMath.SideInset + PortraitGridMath.ScrollWidth(canvas.X);
        header.OffsetBottom = headerTop + PortraitGridMath.SortHeaderHeight;
        if (header.GetNodeOrNull<Control>("HBoxContainer") is not { } row) return;
        Capture(row, captureVisible: false);
        row.AnchorTop = row.AnchorBottom = 0f;
        row.OffsetLeft = 0f;
        row.OffsetRight = 0f;
        row.OffsetTop = 0f;
        row.OffsetBottom = PortraitGridMath.SortButtonHeight;

        List<Control> buttons = new();
        for (int i = 0; i < row.GetChildCount(); i++)
            if (row.GetChild(i) is Control button && button.Visible) buttons.Add(button);
        if (buttons.Count == 0) return;
        float width = PortraitGridMath.SortButtonWidth(PortraitGridMath.ScrollWidth(canvas.X), buttons.Count);
        int font = PortraitGridMath.SortFontMax;
        while (font > PortraitGridMath.SortFontMin && !SortButtonsFit(buttons, font, width)) font--;
        foreach (Control button in buttons)
        {
            Capture(button, captureVisible: false);
            var size = new Vector2(width, PortraitGridMath.SortButtonHeight);
            if (button.CustomMinimumSize != size) button.CustomMinimumSize = size;
            if (button.GetNodeOrNull<Label>("%Label") is { } label && font != label.GetThemeFontSize("font_size"))
                SetLabelFontSize(label, font);
            if (button.GetNodeOrNull<Control>("HBoxContainer/Image") is { } arrow)
            {
                var arrowSize = new Vector2(PortraitGridMath.SortArrowSize, PortraitGridMath.SortArrowSize);
                if (arrow.CustomMinimumSize != arrowSize)
                {
                    Capture(arrow, captureVisible: false);
                    arrow.CustomMinimumSize = arrowSize;
                }
            }
        }
    }

    private static bool SortButtonsFit(List<Control> buttons, int font, float width)
    {
        foreach (Control button in buttons)
            if (SortButtonNeed(button, font) > width) return false;
        return true;
    }

    /// <summary>A sort button's own text, its 12-unit separation, the arrow and the 8-unit inset both sides.</summary>
    private static float SortButtonNeed(Control button, int font)
    {
        if (button.GetNodeOrNull<Label>("%Label") is not { } label) return 140f;
        Font? face = label.GetThemeFont("font");
        float text = face?.GetStringSize(label.Text, HorizontalAlignment.Left, -1, font).X ?? label.Text.Length * font * 0.55f;
        return text + 12f + PortraitGridMath.SortArrowSize + 8f + 4f;
    }

    // ---- the deck view's info line and the View Upgrades box -----------------------------------------------------

    /// <summary>
    /// The deck view's pile info line used to sit in the bottom middle under the cards; the View Upgrades box takes that
    /// band now, so the line stands above it, as large as 43 % of the canvas allows.
    /// </summary>
    private void LayoutDeckInfoText(GridState state, Vector2 canvas)
    {
        if (state.InfoText == 0 && GodotObject.InstanceFromId(state.ScreenId) is Control screen)
            state.InfoText = screen.GetNodeOrNull<Control>(DeckInfoTextName)?.GetInstanceId() ?? 0;
        if (state.InfoText == 0 || GodotObject.InstanceFromId(state.InfoText) is not Control line || !GodotObject.IsInstanceValid(line)) return;
        if (line.Size.X < 1f || line.Size.Y < 1f) return;
        float scale = PortraitGridMath.InfoTextScale(line.Size.X, canvas.X);
        float top = canvas.Y - PortraitGridMath.InfoTextTop;
        float left = (canvas.X - line.Size.X) * 0.5f;
        if (Math.Abs(line.Scale.X - scale) < 0.002f && Math.Abs(line.OffsetTop - top) < 0.25f &&
            Math.Abs(line.OffsetLeft - left) < 0.25f) return;
        Capture(line, captureVisible: false);
        line.AnchorLeft = line.AnchorTop = line.AnchorRight = line.AnchorBottom = 0f;
        line.PivotOffset = new Vector2(line.Size.X * 0.5f, line.Size.Y);
        line.Scale = new Vector2(scale, scale);
        line.OffsetLeft = left;
        line.OffsetRight = left + line.Size.X;
        line.OffsetTop = top;
        line.OffsetBottom = top + line.Size.Y;
    }

    private static Control? ViewUpgradesBoxOf(Control tickbox)
    {
        for (Node? node = tickbox.GetParent(); node != null; node = node.GetParent())
            if (node is Control { Name: var name } box && name == ViewUpgradesBoxName) return box;
        return null;
    }

    /// <summary>
    /// The View Upgrades box (checkbox and label together) is 64 units tall natively; portrait draws it 44 points tall,
    /// centred, with its top at the footer line the screen's prompt and Back button leave it.
    /// </summary>
    private void UpdateViewUpgradesBox(Control box, PortraitGridScreen screen, Vector2 canvas)
    {
        if (box.Size.X < 1f || box.Size.Y < 1f) return;
        float scale = PortraitGridMath.ToggleScale(box.Size.Y);
        float top = canvas.Y - PortraitGridMath.ToggleTop(screen);
        float left = PortraitGridMath.ToggleLeft(canvas.X, box.Size.X, scale);
        if (Math.Abs(box.Scale.X - scale) < 0.002f && box.PivotOffset == Vector2.Zero &&
            Math.Abs(box.OffsetLeft - left) < 0.25f && Math.Abs(box.OffsetTop - top) < 0.25f &&
            Math.Abs(box.OffsetRight - (left + box.Size.X)) < 0.25f) return;
        Capture(box, captureVisible: false);
        box.AnchorLeft = box.AnchorTop = box.AnchorRight = box.AnchorBottom = 0f;
        box.PivotOffset = Vector2.Zero;
        box.Scale = new Vector2(scale, scale);
        box.OffsetLeft = left;
        box.OffsetRight = left + box.Size.X;
        box.OffsetTop = top;
        box.OffsetBottom = top + box.Size.Y;
    }

    // ---- shared helpers ------------------------------------------------------------------------------------------

    /// <summary>The grid's scrolling content, remembered per grid: the deck view's instance does not resolve the unique name.</summary>
    private static Control? GridScroll(Control grid, GridState state)
    {
        if (state.Scroll != 0 && GodotObject.InstanceFromId(state.Scroll) is Control cached && GodotObject.IsInstanceValid(cached))
            return cached;
        Control? scroll = grid.GetNodeOrNull<Control>("%ScrollContainer") ?? grid.GetNodeOrNull<Control>("ScrollContainer");
        state.Scroll = scroll?.GetInstanceId() ?? 0;
        return scroll;
    }

    private static Vector2 CanvasOf(Control control) =>
        control.GetViewport()?.GetVisibleRect().Size ?? Vector2.Zero;

    private static int YOffsetOf(Control grid) =>
        _yOffsetProperty?.GetValue(grid) is int offset ? offset : 0;

    /// <summary>The screen a grid or a footer control belongs to, from its ancestors' type names.</summary>
    private static PortraitGridScreen GridScreenOf(Node node, out Control? screen)
    {
        screen = null;
        for (Node? parent = node.GetParent(); parent != null; parent = parent.GetParent())
        {
            if (parent is not Control control) continue;
            PortraitGridScreen kind;
            switch (control.GetType().FullName)
            {
                case DeckViewScreenTypeName: kind = PortraitGridScreen.DeckView; break;
                case PileScreenTypeName: kind = PortraitGridScreen.Pile; break;
                case UpgradeSelectTypeName: kind = PortraitGridScreen.Upgrade; break;
                case EnchantSelectTypeName: kind = PortraitGridScreen.Enchant; break;
                case TransformSelectTypeName: kind = PortraitGridScreen.Transform; break;
                case DeckSelectScreenTypeName:
                case SimpleSelectTypeName:
                case CombatPileSelectTypeName:
                    kind = PortraitGridScreen.DeckSelect;
                    break;
                default:
                    continue;
            }
            screen = control;
            return kind;
        }
        return PortraitGridScreen.DeckSelect;
    }

    /// <summary>
    /// Resolves the grid's private members once. Each member is resolved on its own and a failure is reported, never left
    /// half-done: NCardGrid has two InitGrid methods (the protected parameterless one the grid's _Process calls, and a
    /// private one that takes a Task), so the parameterless overload has to be asked for by its empty signature.
    /// </summary>
    private static void ResolveGridReflection()
    {
        if (_cardSizeField != null && _initGridMethod != null && _gridHolderType != null) return;
        Assembly game = typeof(NGame).Assembly;
        Type? type = _cardGridType ??= game.GetType(CardGridTypeName);
        if (type != null)
        {
            _cardSizeField ??= type.GetField("_cardSize", BindingFlags.Instance | BindingFlags.NonPublic);
            _needsReinitField ??= type.GetField("_needsReinit", BindingFlags.Instance | BindingFlags.NonPublic);
            _yOffsetProperty ??= type.GetProperty("YOffset", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (Type? level = type; level != null && _initGridMethod == null; level = level.BaseType)
            {
                try
                {
                    _initGridMethod = level.GetMethod("InitGrid",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                }
                catch (AmbiguousMatchException)
                {
                    // An overload set without a filter: nothing below can be trusted, so wait for the next pass.
                }
            }
        }
        _gridHolderType ??= game.GetType(GridHolderTypeName);
        if (_gridHolderType == null) return;
        _hoverTweenField ??= FindField(_gridHolderType, "_hoverTween");
        // NCardHolder.smallScale: the scale the game gives a grid card (0.8 at every size it ships).
        for (Type? level = _gridHolderType; level != null && level != typeof(object); level = level.BaseType)
        {
            if (level.GetField("smallScale", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is not { } field) continue;
            if (field.GetValue(null) is Vector2 scale && scale.X > 0.01f) _gridHolderNativeScale = scale.X;
            break;
        }
    }

    private static FieldInfo? FindField(Type type, string name)
    {
        for (Type? level = type; level != null; level = level.BaseType)
        {
            FieldInfo? field = level.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        return null;
    }
}
