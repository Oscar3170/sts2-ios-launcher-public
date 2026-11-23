using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace STS2Mobile.GameHost;

internal static partial class PortraitSubmenuLayout
{
    public const string CardLibraryTypeName = Ns + "CardLibrary.NCardLibrary";

    /// <summary>The card grid already fits at the canvas's width, two cards to a row.</summary>
    public const float CardLibraryDesignWidth = 1080f;

    // The desktop sidebar is 288 units wide: its widest parts (the four pool filters a row, 64 each, and the five cost
    // boxes) fill the 256 between its 16 unit margins, so it is drawn larger as a whole rather than spread wider.
    private const float CardLibrarySidebarWidth = 288f;
    // 1.5 makes the rarity rows 57 units (20 points), the sort buttons 60 (21), the bottom tickboxes 63 (22) and their 22
    // unit text 11.5 points. The sidebar is then 432 units wide and the grid 648.
    private const float CardLibrarySidebarScale = 1.5f;
    // The grid's scroll area (desktop: 50 in from the left, 150 from the right, with the scrollbar 100 to 50 from the right)
    // is 582 wide here: two 240 unit cards with 40 between need 520 and three need 800, so it stays two to a row, 31 units
    // each side for a card that grows when touched. The scrollbar moves to the right margin, inside the screen.
    private const float CardLibraryScrollLeft = 4f;
    private const float CardLibraryScrollRight = 62f;
    private const float CardLibraryScrollbarLeft = 58f;
    private const float CardLibraryScrollbarRight = 8f;
    // Space between the sidebar's modules (search, pools, type, rarity, cost, A - Z), in the sidebar's own units.
    private const int CardLibraryModuleGap = 8;

    /// <summary>
    /// The card library: the sidebar's filters and sort buttons drawn 1.5 times as large, over the height the desktop
    /// layout left empty. The sidebar keeps its 288 unit inner layout and is scaled as a whole (drawn 432 wide); it scrolls,
    /// because its filter rows and tickboxes are grown to a finger's 44 points, and its lower part ends above the back
    /// button instead of under it. The grid starts at the sidebar's new edge with a wider scroll area, so it keeps two cards
    /// to a row.
    /// </summary>
    private static void LayoutCardLibrary(Control screen, float w, float h, Action<Control> capture)
    {
        const float k = CardLibrarySidebarScale;
        float sidebarWidth = CardLibrarySidebarWidth * k;
        // A filter row is 38 units on the desktop: 85 here is 127 units (44 points) on the canvas.
        const float FingerSize = 85f;

        if (screen.GetNodeOrNull<Control>("Sidebar") is { } sidebar)
        {
            float scrollHeight = h / k - (CornerButtonsTop + Gap) / k;
            // Nothing leaves the tree: the sidebar clips and the filter column moves under the finger (a container clip only
            // lays it out again when it sorts, and the offset is re-applied every frame).
            Place(sidebar, 0f, 0f, CardLibrarySidebarWidth, Math.Max(200f, scrollHeight), capture);
            if (sidebar.PivotOffset != Vector2.Zero) sidebar.PivotOffset = Vector2.Zero;
            var scale = new Vector2(k, k);
            if (sidebar.Scale != scale) sidebar.Scale = scale;
            if (sidebar.GetNodeOrNull<Control>("MarginContainer") is { } margins)
            {
                capture(margins);
                SetKept(sidebar, "clip_contents", true);
                SetConstant(margins, "margin_bottom", (int)Gap);
                SetConstant(margins.GetNodeOrNull<Control>("TopVBox"), "separation", CardLibraryModuleGap);
                GrowCardLibraryFilters(margins, 256f, FingerSize, capture);
                PortraitInPlaceScroll.Shared.Set(sidebar, margins, screen);
            }
        }

        if (screen.GetNodeOrNull<Control>("CardGrid") is { } grid)
        {
            // The grid keeps the desktop's anchors (the whole screen) and only its left edge moves; its resize asks it to
            // lay the cards out again.
            capture(grid);
            if (MathF.Abs(grid.OffsetLeft - sidebarWidth) > 0.5f) grid.OffsetLeft = sidebarWidth;
            if (grid.GetNodeOrNull<Control>("ScrollContainer") is { } scroll)
            {
                // The grid works out its columns from the scroll area's width and sets the area's size itself after laying
                // out (deferred, with the width it had then), so the edges are held every frame and a change relays it.
                capture(scroll);
                bool changed = false;
                if (MathF.Abs(scroll.OffsetLeft - CardLibraryScrollLeft) > 0.5f)
                {
                    scroll.OffsetLeft = CardLibraryScrollLeft;
                    changed = true;
                }
                if (MathF.Abs(scroll.OffsetRight + CardLibraryScrollRight) > 0.5f)
                {
                    scroll.OffsetRight = -CardLibraryScrollRight;
                    changed = true;
                }
                if (changed) Field(grid, "_needsReinit")?.SetValue(grid, true);
            }
            if (grid.GetNodeOrNull<Control>("Scrollbar") is { } scrollbar)
            {
                capture(scrollbar);
                if (MathF.Abs(scrollbar.OffsetLeft + CardLibraryScrollbarLeft) > 0.5f) scrollbar.OffsetLeft = -CardLibraryScrollbarLeft;
                if (MathF.Abs(scrollbar.OffsetRight + CardLibraryScrollbarRight) > 0.5f) scrollbar.OffsetRight = -CardLibraryScrollbarRight;
            }
        }

        // "No cards found." is centred over the grid (desktop: 285 from the left, the sidebar's edge less 3).
        if (screen.GetNodeOrNull<Control>("NoResultsLabel") is { } noResults)
        {
            capture(noResults);
            if (MathF.Abs(noResults.OffsetLeft - (sidebarWidth - 3f)) > 0.5f) noResults.OffsetLeft = sidebarWidth - 3f;
        }

        // A spacing constant the snapshot holds (margins, separation): captured before the first change, then set only
        // when it differs.
        void SetConstant(Control? control, string name, int value)
        {
            if (control == null) return;
            if (control.HasThemeConstantOverride(name) && control.GetThemeConstant(name) == value) return;
            capture(control);
            control.AddThemeConstantOverride(name, value);
        }
    }

    /// <summary>
    /// Grows the sidebar's controls to a finger's size. Rows that already run the full width only need height; the small
    /// pool/type/cost boxes get both. A grid whose cells no longer fit at 85 units each goes to the two columns the 256
    /// unit inner width holds, and the scroll added around the sidebar takes the extra height.
    /// </summary>
    private static void GrowCardLibraryFilters(Control root, float width, float finger, Action<Control> capture)
    {
        var controls = new List<Control>();
        void Walk(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is not Control control || !GodotObject.IsInstanceValid(control)) continue;
                if (control is GridContainer grid && grid.GetChildren().OfType<Control>().Any(PortraitAudit.IsTouchTarget))
                {
                    int columns = Math.Max(1, (int)MathF.Floor((width + 4f) / (finger + 4f)));
                    SetKept(grid, "columns", columns);
                }
                if (PortraitAudit.IsTouchTarget(control) && control.IsVisibleInTree()) controls.Add(control);
                Walk(control);
            }
        }
        Walk(root);
        foreach (Control control in controls)
        {
            Vector2 current = control.CustomMinimumSize;
            SetMin(control, new Vector2(Math.Max(finger, current.X), Math.Max(finger, current.Y)), capture);
        }
    }

    /// <summary>Sets a control's minimum size (the snapshot holds it), changing only when it differs.</summary>
    private static void SetMin(Control control, Vector2 size, Action<Control> capture)
    {
        if (control.CustomMinimumSize.DistanceSquaredTo(size) <= 0.25f) return;
        capture(control);
        control.CustomMinimumSize = size;
    }
}
