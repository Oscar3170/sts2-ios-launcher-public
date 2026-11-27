using System;
using System.Collections.Generic;
using System.Linq;
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
internal sealed class PortraitCardGridLayout
{
    public const string CardGridTypeName = "MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid";
    public const string UpgradeTickboxTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens.NUpgradePreviewTickbox";

    private const string DeckViewScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.NDeckViewScreen";
    private const string PileScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.NCardPileScreen";
    private const string CardSelectionNamespace = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection";
    private const string UpgradeSelectTypeName = CardSelectionNamespace + ".NDeckUpgradeSelectScreen";
    private const string EnchantSelectTypeName = CardSelectionNamespace + ".NDeckEnchantSelectScreen";
    private const string TransformSelectTypeName = CardSelectionNamespace + ".NDeckTransformSelectScreen";
    private const string DeckSelectScreenTypeName = CardSelectionNamespace + ".NDeckCardSelectScreen";
    private const string SimpleSelectTypeName = CardSelectionNamespace + ".NSimpleCardSelectScreen";
    private const string CombatPileSelectTypeName = CardSelectionNamespace + ".NCombatPileCardSelectScreen";
    private const string RelicInventoryTypeName = "MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventory";
    private const string ViewUpgradesBoxName = "ViewUpgrades";
    private const string SortRowName = "SortingOptions";
    private const string DeckInfoTextName = "BottomText";

    /// <summary>One grid's portrait layout: its screen, its native card size and what this owner changed.</summary>
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
        public ulong BorderFade;
        public bool BorderFadeVisible;
        public ulong EdgeShade;
        public float EdgeShadeHeight;
    }

    /// <summary>One text control's own wrapping and sizing, before portrait took it in hand.</summary>
    private readonly struct GridTextMode(TextServer.AutowrapMode wrap, bool fit, Control.SizeFlags flagsH, Control.SizeFlags flagsV)
    {
        public readonly TextServer.AutowrapMode Wrap = wrap;
        public readonly bool Fit = fit;
        public readonly Control.SizeFlags FlagsH = flagsH;
        public readonly Control.SizeFlags FlagsV = flagsV;
    }

    private readonly Dictionary<ulong, GridState> _gridStates = new();
    private readonly Dictionary<ulong, PortraitGridScreen> _viewUpgrades = new();
    private readonly Dictionary<ulong, GridTextMode> _gridTextModes = new();
    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private static readonly string[] RichFontSizeNames =
        ["normal_font_size", "bold_font_size", "bold_italics_font_size", "italics_font_size", "mono_font_size"];
    private readonly Dictionary<ulong, (bool Had, int Size)> _labelFontSizes = new();
    private readonly Dictionary<ulong, (bool Had, int Size)[]> _richFontSizes = new();

    private static Type? _cardGridType;
    private static FieldInfo? _cardSizeField;
    private static MethodInfo? _initGridMethod;
    private static FieldInfo? _needsReinitField;
    private static PropertyInfo? _yOffsetProperty;

    private PortraitControlSnapshot Capture(Control control, bool captureVisible)
    {
        ulong id = control.GetInstanceId();
        if (_snapshots.TryGetValue(id, out PortraitControlSnapshot? existing))
        {
            if (captureVisible && !existing.CaptureVisible) existing.RecordInitialVisibility(control.Visible);
            return existing;
        }
        var created = new PortraitControlSnapshot(control, captureVisible);
        _snapshots[id] = created;
        return created;
    }

    /// <summary>Sets a label's font size in portrait, keeping the game's own to put back in landscape.</summary>
    private void SetLabelFontSize(Control label, int size)
    {
        ulong id = label.GetInstanceId();
        if (!_labelFontSizes.ContainsKey(id))
            _labelFontSizes[id] = (label.HasThemeFontSizeOverride("font_size"), label.GetThemeFontSize("font_size"));
        if (label.GetThemeFontSize("font_size") != size) label.AddThemeFontSizeOverride("font_size", size);
    }

    /// <summary>Sets every font size of a rich text label that does not size itself, keeping what it had for landscape.</summary>
    private void SetRichFontSize(Control label, int size)
    {
        ulong id = label.GetInstanceId();
        if (!_richFontSizes.ContainsKey(id))
            _richFontSizes[id] = RichFontSizeNames
                .Select(name => (label.HasThemeFontSizeOverride(name), label.HasThemeFontSizeOverride(name) ? label.GetThemeFontSize(name) : 0))
                .ToArray();
        foreach (string name in RichFontSizeNames)
            if (!label.HasThemeFontSizeOverride(name) || label.GetThemeFontSize(name) != size)
                label.AddThemeFontSizeOverride(name, size);
    }

    // ---- entry points from PortraitBetweenLayout ---------------------------------------------------------------

    /// <summary>
    /// Lays one card grid out for portrait: its viewport between the header band and the screen's footer, three columns and
    /// the deck view's sort row above it. Called for every <c>NCardGrid</c> the tree adds, and again whenever the grid or
    /// its screen moves it.
    /// </summary>
    public void Apply(Control grid, float capHeight, float canvasHeight, float canvasWidth)
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

    /// <summary>Each frame: the grids, then the View Upgrades boxes their screens keep in the footer.</summary>
    public void Update(float capHeight, Vector2 canvas)
    {
        foreach (ulong id in _gridStates.Keys)
            if (GodotObject.InstanceFromId(id) is Control grid && GodotObject.IsInstanceValid(grid) && grid.IsVisibleInTree())
                KeepGridPortrait(grid, capHeight, canvas);
        foreach ((ulong id, PortraitGridScreen screen) in _viewUpgrades)
            if (GodotObject.InstanceFromId(id) is Control box && GodotObject.IsInstanceValid(box) && box.IsVisibleInTree())
                UpdateViewUpgradesBox(box, screen, canvas);
    }

    /// <summary>Each frame: the grid's own rect, its card size and the deck's footer are kept where portrait put them.</summary>
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
        // Holders entering the grid mean it has just built its rows again (a sort, a preview closing, a resize): the deck
        // view's own pass also puts its 100-unit card offset back, so lay the grid out again once.
        if (PortraitGridHolderScale.TakeRebuilt(scroll) && YOffsetOf(grid) != 0) ApplyGridPortrait(grid, state, canvas, capHeight);
        // While the content moves the game's recycler hands the window to PortraitGridWindow; this catches a scroll offset
        // set directly (which the recycler does not see), before the grid draws.
        PortraitGridWindow.Reconcile(grid);
    }

    /// <summary>Tracks the View Upgrades box a deck or selection screen's tickbox stands in and places it in the footer.</summary>
    public bool TrackViewUpgradesBox(Control tickbox)
    {
        Control? box = ViewUpgradesBoxOf(tickbox);
        if (box == null) return false;
        PortraitGridScreen screen = GridScreenOf(box, out _);
        if (screen is not (PortraitGridScreen.DeckView or PortraitGridScreen.Upgrade or PortraitGridScreen.Transform)) return false;
        if (!_viewUpgrades.TryAdd(box.GetInstanceId(), screen)) return false;
        UpdateViewUpgradesBox(box, screen, CanvasOf(box));
        return true;
    }

    // ---- the grid's shaded ends ----------------------------------------------------------------------------------

    private const string EdgeShadeName = "PortraitGridEdgeShade";

    /// <summary>
    /// The game darkens the grid's first and last 5 % with a black BorderGradient over the cards, which in landscape fades
    /// them into the screen's edges. Portrait ends the grid mid-screen over the stage, so the bottom band was a black stripe
    /// ending in a hard cut. Portrait hides that band and draws the same black in a sibling over the grid, which the grid's
    /// clip does not cut: the top keeps the game's fade inside the grid, and the foot's shade straddles the edge, rising to
    /// the cut and falling away over the stage below it, so the cut cards sink into a soft shadow instead of a hard line.
    /// (Fading the cards themselves through the scroll area's clip_children mask blanks their portraits, which are masked
    /// the same way.)
    /// </summary>
    private static void ShadeGridEdges(Control grid, GridState state, float top, float bottom, float width)
    {
        if (state.BorderFade == 0 && grid.GetNodeOrNull<CanvasItem>("BorderGradient") is { } fade)
        {
            state.BorderFade = fade.GetInstanceId();
            state.BorderFadeVisible = fade.Visible;
        }
        if (state.BorderFade != 0 && GodotObject.InstanceFromId(state.BorderFade) is CanvasItem border &&
            GodotObject.IsInstanceValid(border) && border.Visible)
            border.Visible = false;
        if (grid.GetParent() is not Control parent) return;
        float edge = PortraitGridMath.GridEdgeFade;
        float height = bottom - top + edge;
        if (state.EdgeShade == 0 || GodotObject.InstanceFromId(state.EdgeShade) is not TextureRect shade || !GodotObject.IsInstanceValid(shade))
        {
            shade = new TextureRect
            {
                Name = EdgeShadeName,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
            };
            parent.AddChild(shade);
            parent.MoveChild(shade, grid.GetIndex() + 1);
            state.EdgeShade = shade.GetInstanceId();
            state.EdgeShadeHeight = 0f;
        }
        if (Math.Abs(state.EdgeShadeHeight - height) > 0.5f)
        {
            state.EdgeShadeHeight = height;
            shade.Texture = EdgeShadeTexture(bottom - top, edge);
        }
        shade.Position = new Vector2(grid.Position.X, top);
        shade.Size = new Vector2(width, height);
    }

    /// <summary>
    /// The shade over a grid <paramref name="gridHeight"/> tall plus <paramref name="edge"/> below it: the game's 0.9 black
    /// fading in over the grid's first <paramref name="edge"/> units, clear down the middle, then 0.75 black at the foot's
    /// cut, falling off to clear on both sides. The game's own smoothstep-like (cubic) interpolation.
    /// </summary>
    private static GradientTexture2D EdgeShadeTexture(float gridHeight, float edge)
    {
        float total = gridHeight + edge;
        Color clear = new(0f, 0f, 0f, 0f);
        var gradient = new Gradient
        {
            InterpolationMode = Gradient.InterpolationModeEnum.Cubic,
            Offsets = [0f, edge / total, (gridHeight - edge) / total, gridHeight / total, 1f],
            Colors = [new Color(0f, 0f, 0f, 0.9f), clear, clear, new Color(0f, 0f, 0f, 0.75f), clear],
        };
        return new GradientTexture2D { Gradient = gradient, Width = 2, Height = 1024, FillTo = new Vector2(0f, 1f) };
    }

    private static void RemoveEdgeShade(GridState state)
    {
        if (state.EdgeShade != 0 && GodotObject.InstanceFromId(state.EdgeShade) is Node shade && GodotObject.IsInstanceValid(shade))
            shade.QueueFree();
        if (state.BorderFade != 0 && GodotObject.InstanceFromId(state.BorderFade) is CanvasItem border && GodotObject.IsInstanceValid(border))
            border.Visible = state.BorderFadeVisible;
        state.EdgeShade = state.BorderFade = 0;
        state.EdgeShadeHeight = 0f;
    }

    /// <summary>Puts the sort row back inside the scrolling content, the grid's card size and clip, and every holder's scale.</summary>
    public void RestoreGridsLandscape()
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
            RemoveEdgeShade(state);
            if (state.YOffsetSaved && _yOffsetProperty != null) _yOffsetProperty.SetValue(grid, state.NativeYOffset);
            if (_cardSizeField != null && state.NativeCardSize.X > 0.5f) _cardSizeField.SetValue(grid, state.NativeCardSize);
            if (GridScroll(grid, state) is { } scroll) PortraitGridHolderScale.Untrack(scroll);
            if (_needsReinitField != null) _needsReinitField.SetValue(grid, true);
        }
        _gridStates.Clear();
        _viewUpgrades.Clear();
        foreach ((ulong id, GridTextMode mode) in _gridTextModes)
        {
            if (GodotObject.InstanceFromId(id) is not RichTextLabel label || !GodotObject.IsInstanceValid(label)) continue;
            label.AutowrapMode = mode.Wrap;
            label.FitContent = mode.Fit;
            label.SizeFlagsHorizontal = mode.FlagsH;
            label.SizeFlagsVertical = mode.FlagsV;
        }
        _gridTextModes.Clear();
    }

    /// <summary>Puts every control this owner moved back where the game had it.</summary>
    public void RestoreControls()
    {
        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
        _snapshots.Clear();
    }

    /// <summary>The sort row's and the info line's own text sizes go back to the game's.</summary>
    public void RestoreFonts()
    {
        foreach ((ulong id, (bool had, int size)) in _labelFontSizes)
        {
            if (GodotObject.InstanceFromId(id) is not Control label || !GodotObject.IsInstanceValid(label)) continue;
            if (had) label.AddThemeFontSizeOverride("font_size", size);
            else label.RemoveThemeFontSizeOverride("font_size");
        }
        _labelFontSizes.Clear();
        foreach ((ulong id, (bool Had, int Size)[] sizes) in _richFontSizes)
        {
            if (GodotObject.InstanceFromId(id) is not Control label || !GodotObject.IsInstanceValid(label)) continue;
            for (int i = 0; i < RichFontSizeNames.Length; i++)
            {
                if (sizes[i].Had) label.AddThemeFontSizeOverride(RichFontSizeNames[i], sizes[i].Size);
                else label.RemoveThemeFontSizeOverride(RichFontSizeNames[i]);
            }
        }
        _richFontSizes.Clear();
    }

    public void PruneDead()
    {
        foreach (ulong id in _snapshots.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _snapshots.Remove(id);
        foreach (ulong id in _gridStates.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _gridStates.Remove(id);
        foreach (ulong id in _viewUpgrades.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _viewUpgrades.Remove(id);
        foreach (ulong id in _gridTextModes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _gridTextModes.Remove(id);
        foreach (ulong id in _labelFontSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _labelFontSizes.Remove(id);
        foreach (ulong id in _richFontSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _richFontSizes.Remove(id);
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
        ShadeGridEdges(grid, state, top, bottom, canvas.X);

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
        // The game sizes its cards through the patched holder scale, so its own rows, hover and unhover keep the column size;
        // tracked before InitGrid, its new holders enter at that size.
        if (GridScroll(grid, state) is { } cards) PortraitGridHolderScale.Track(cards, PortraitGridMath.HolderScale(cardWidth));
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
        foreach (Node node in PortraitBetweenLayout.Descendants(root))
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
        // The game's row separates its four buttons by 50 units, which alone pushes 4 x 250 units off the canvas.
        row.AddThemeConstantOverride("separation", (int)PortraitGridMath.SortButtonGap);

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
        // Only the deck view's BottomText is an info line: the selection screens' BottomText is their prompt, a dark box that
        // hugs its text, and stretching it to the info line's wrap box left it twice as tall as the text.
        if (state.Screen != PortraitGridScreen.DeckView) return;
        if (state.InfoText == 0 && GodotObject.InstanceFromId(state.ScreenId) is Control screen)
            state.InfoText = screen.GetNodeOrNull<Control>(DeckInfoTextName)?.GetInstanceId() ?? 0;
        if (state.InfoText == 0 || GodotObject.InstanceFromId(state.InfoText) is not Control line || !GodotObject.IsInstanceValid(line)) return;
        if (line.Size.X < 1f || line.Size.Y < 1f) return;
        float width = PortraitGridMath.InfoTextWidth(canvas.X);
        float left = PortraitGridMath.InfoTextLeft(canvas.X);
        float top = canvas.Y - PortraitGridMath.InfoTextTop;
        // The game draws the line on one unwrapped row: the label hugs its text and shrinks to its centre, so it runs past
        // both edges of any box. Portrait gives it the box's whole width and height: the width bounds the wrap, the forced
        // fill keeps a real height even though fit_content's minimum width is the text's own.
        if (line.FindChild("BottomLabel", recursive: true, owned: false) is RichTextLabel label)
        {
            if (!_gridTextModes.ContainsKey(label.GetInstanceId()))
                _gridTextModes[label.GetInstanceId()] = new GridTextMode(label.AutowrapMode, label.FitContent,
                    label.SizeFlagsHorizontal, label.SizeFlagsVertical);
            if (label.AutowrapMode != TextServer.AutowrapMode.WordSmart) label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            if (label.SizeFlagsHorizontal != Control.SizeFlags.Fill) label.SizeFlagsHorizontal = Control.SizeFlags.Fill;
            if (label.SizeFlagsVertical != Control.SizeFlags.Fill) label.SizeFlagsVertical = Control.SizeFlags.Fill;
            SetRichFontSize(label, PortraitGridMath.InfoTextFontSize);
        }
        if (Math.Abs(line.OffsetLeft - left) < 0.25f && Math.Abs(line.OffsetRight - (left + width)) < 0.25f &&
            Math.Abs(line.OffsetTop - top) < 0.25f && line.Scale == Vector2.One) return;
        Capture(line, captureVisible: false);
        line.AnchorLeft = line.AnchorTop = line.AnchorRight = line.AnchorBottom = 0f;
        line.PivotOffset = Vector2.Zero;
        line.Scale = Vector2.One;
        line.OffsetLeft = left;
        line.OffsetRight = left + width;
        line.OffsetTop = top;
        line.OffsetBottom = top + PortraitGridMath.InfoTextHeight;
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
        if (_cardSizeField != null && _initGridMethod != null) return;
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
    }
}
