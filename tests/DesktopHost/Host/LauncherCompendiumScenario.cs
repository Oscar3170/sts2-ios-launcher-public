using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Saves;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The launcher's <c>compendium</c> scenario: the card library laid out as the portrait interface (header with the search,
/// pool tabs and sort buttons, a collapsible advanced panel, and a three-column touch-scrolled grid under them). The checks
/// read the live native controls: the search bar, the pool filter grid, the four sort buttons, the type/rarity/cost tickboxes
/// and the stat toggles are the game's own nodes, only moved and sized. The collapsed state is asserted before anything is
/// touched, the advanced panel is opened with a real tap on the native-styled toggle, a type filter tap must change the
/// grid's sorted cards, and a touch drag on the grid must move its scroll content and return to the top. In landscape the
/// native sidebar/grid split is asserted instead, so the desktop layout is proven unchanged.
/// </summary>
internal sealed partial class TestDriver
{
    private const string CompendiumLibraryType = "CardLibrary.NCardLibrary";
    private const string CompendiumToggleName = "PortraitAdvancedToggle";

    private static readonly string[] CompendiumHeaderNames =
        ["SearchBar", "PoolFilters", "CardTypeSorter", "RaritySorter", "CostSorter", "AlphabetSorter"];

    private static readonly string[] CompendiumAdvancedNames =
    [
        "AttackType", "SkillType", "PowerType", "OtherType",
        "CommonRarity", "UncommonRarity", "RareRarity", "OtherRarity",
        "Cost0", "Cost1", "Cost2", "Cost3+", "CostX",
        "MultiplayerCards", "Stats", "Upgrades",
    ];

    internal static async Task RunLauncherCompendiumAsync(TestOptions options, TestReport report, ScreenshotService screenshots,
        VirtualPointer pointer)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Log.Error("Test harness requires a SceneTree.");
            return;
        }
        await new TestDriver(tree, options, report, screenshots, pointer).RunLauncherCompendiumInternalAsync();
    }

    private async Task RunLauncherCompendiumInternalAsync()
    {
        await WaitForMainMenuSceneAsync();
        await WaitForMainMenuPreloadAsync();
        if (SaveManager.Instance.CurrentProfileId != TestProfileScope.TestProfileId)
            throw new InvalidOperationException("Refusing to open the compendium on a non-test profile.");

        NMainMenu menu = NGame.Instance!.MainMenu!;
        menu.RefreshButtons();
        await WaitSecondsAsync(0.5);
        menu.OpenCompendiumSubmenu();
        await WaitFramesAsync(20);
        NSubmenu library = Push(menu, CompendiumLibraryType);
        // There is no run at the main menu.
        if (library is NCardLibrary cardLibrary) cardLibrary.Initialize(null!);
        await WaitFramesAsync(60);

        Control screen = library;
        NCardGrid grid = screen.GetNodeOrNull<NCardGrid>("%CardGrid")
            ?? throw new InvalidOperationException("the card library has no CardGrid");
        await WaitUntilAsync(() => grid.CurrentlyDisplayedCardHolders.Any(h => GodotObject.IsInstanceValid(h) && h.IsVisibleInTree()),
            "the card library grid fills", MediumWait);
        await WaitFramesAsync(30);

        if (!SafeAreaCanvas.IsPortrait)
        {
            CheckCompendiumLandscapeNative(screen, grid);
            await CaptureAsync("compendium_landscape");
            PopAll(menu);
            return;
        }

        Control? sidebar = screen.GetNodeOrNull<Control>("Sidebar");
        if (sidebar is null)
        {
            _report.Fail("compendium_sidebar", "the card library has no Sidebar");
            PopAll(menu);
            return;
        }

        CheckCompendiumHeader(screen, sidebar);
        CheckCompendiumAdvanced(screen, sidebar, expectedVisible: false);
        CheckCompendiumGrid("compendium_collapsed", screen, sidebar, grid);
        await CaptureAsync("compendium_collapsed");

        Control? toggle = CompendiumToggle(screen);
        if (toggle is null)
        {
            _report.Fail("compendium_toggle_exists", "no native-styled advanced filters toggle in the header");
            PopAll(menu);
            return;
        }

        float collapsedHeaderBottom = sidebar.GetGlobalRect().End.Y;
        float collapsedGridTop = grid.GetGlobalRect().Position.Y;
        await _pointer.TapAsync(CenterOf(toggle));
        await WaitSecondsAsync(0.4);
        await WaitFramesAsync(10);
        CheckCompendiumAdvanced(screen, sidebar, expectedVisible: true);
        bool grew = sidebar.GetGlobalRect().End.Y > collapsedHeaderBottom + 100f
            && grid.GetGlobalRect().Position.Y > collapsedGridTop + 100f;
        _report.Check("compendium_advanced_expands", grew,
            $"header {collapsedHeaderBottom:F1}->{sidebar.GetGlobalRect().End.Y:F1} " +
            $"grid {collapsedGridTop:F1}->{grid.GetGlobalRect().Position.Y:F1}");
        CheckCompendiumHeader(screen, sidebar);
        CheckCompendiumGrid("compendium_expanded", screen, sidebar, grid);
        await CaptureAsync("compendium_expanded");

        Control? attack = CompendiumUnique(screen, "AttackType");
        if (attack is not null)
        {
            int before = grid.SortedCards.Count;
            await _pointer.TapAsync(CenterOf(attack));
            await WaitSecondsAsync(0.8);
            await WaitFramesAsync(20);
            int after = grid.SortedCards.Count;
            _report.Check("compendium_filter_changes_grid", after > 0 && after < before,
                $"sorted cards {before}->{after} after the Attack filter tap");
            CheckCompendiumGrid("compendium_filtered", screen, sidebar, grid);
            await CaptureAsync("compendium_filtered");
        }
        else
        {
            _report.Fail("compendium_filter_changes_grid", "no AttackType filter to tap");
        }

        await _pointer.TapAsync(CenterOf(toggle));
        await WaitSecondsAsync(0.4);
        await WaitFramesAsync(10);
        CheckCompendiumAdvanced(screen, sidebar, expectedVisible: false);
        _report.Check("compendium_advanced_collapses",
            Mathf.Abs(sidebar.GetGlobalRect().End.Y - collapsedHeaderBottom) < 2f
            && Mathf.Abs(grid.GetGlobalRect().Position.Y - collapsedGridTop) < 2f,
            $"header={sidebar.GetGlobalRect().End.Y:F1} grid={grid.GetGlobalRect().Position.Y:F1}");

        // Touch scrolling on the card grid itself: the first swipe moves the content up, the second returns it to the top.
        Control? content = LauncherGridScroll(grid);
        if (content is null)
        {
            _report.Fail("compendium_grid_touch_scroll", "the card grid has no ScrollContainer");
        }
        else
        {
            Rect2 view = grid.GetGlobalRect();
            Vector2 from = new(view.GetCenter().X, view.Position.Y + view.Size.Y * 0.72f);
            Vector2 to = new(view.GetCenter().X, view.Position.Y + view.Size.Y * 0.32f);
            await _pointer.DragAsync(from, to, steps: 16);
            await WaitSecondsAsync(0.6);
            float moved = content.Position.Y;
            await CaptureAsync("compendium_scrolled");
            await _pointer.DragAsync(to, from, steps: 16);
            await WaitSecondsAsync(0.8);
            float returned = content.Position.Y;
            _report.Check("compendium_grid_touch_scroll", moved < -100f && Mathf.Abs(returned) < 8f,
                $"content {content.Position.Y:F1} after the down swipe {moved:F1}, after the up swipe {returned:F1}");
        }

        PopAll(menu);
    }

    private static Control? CompendiumUnique(Control screen, string name) =>
        screen.GetNodeOrNull<Control>("%" + name) ?? screen.FindChild(name, recursive: true, owned: false) as Control;

    private static Control? CompendiumToggle(Control screen) =>
        screen.FindChild(CompendiumToggleName, recursive: true, owned: false) as Control;

    private static List<Control> CompendiumHeader(Control screen)
    {
        var controls = new List<Control>();
        foreach (string name in CompendiumHeaderNames)
            if (CompendiumUnique(screen, name) is { } control) controls.Add(control);
        if (CompendiumToggle(screen) is { } toggle) controls.Add(toggle);
        return controls;
    }

    private static List<Control> CompendiumAdvanced(Control screen) =>
        CompendiumAdvancedNames.Select(name => CompendiumUnique(screen, name)).OfType<Control>().ToList();

    private static string CompendiumMissing(Control screen)
    {
        var missing = CompendiumHeaderNames.Where(name => CompendiumUnique(screen, name) is null)
            .Concat(CompendiumAdvancedNames.Where(name => CompendiumUnique(screen, name) is null))
            .ToList();
        if (CompendiumToggle(screen) is null) missing.Add(CompendiumToggleName);
        return string.Join(",", missing);
    }

    private bool CompendiumTargetsFit(IReadOnlyList<Control> controls)
    {
        Rect2 safe = PortraitSafeRect();
        return controls.All(control => control.IsVisibleInTree()
            && safe.Grow(1f).Encloses(control.GetGlobalRect())
            && control.GetGlobalRect().Size.X >= LauncherTouchTarget - 1f
            && control.GetGlobalRect().Size.Y >= LauncherTouchTarget - 1f);
    }

    private static bool CompendiumNoOverlap(IReadOnlyList<Control> controls)
    {
        for (int i = 0; i < controls.Count; i++)
            for (int j = i + 1; j < controls.Count; j++)
            {
                Rect2 a = controls[i].GetGlobalRect();
                Rect2 b = controls[j].GetGlobalRect();
                Rect2 shared = a.Intersection(b);
                if (shared.Size.X > 2f && shared.Size.Y > 2f) return false;
            }
        return true;
    }

    private void CheckCompendiumHeader(Control screen, Control sidebar)
    {
        List<Control> header = CompendiumHeader(screen);
        string missing = CompendiumMissing(screen);
        Rect2 bar = sidebar.GetGlobalRect();
        bool inBar = header.All(control => bar.Grow(2f).Encloses(control.GetGlobalRect()));
        _report.Check("compendium_header_controls",
            missing.Length == 0 && header.Count == CompendiumHeaderNames.Length + 1
            && CompendiumTargetsFit(header) && CompendiumNoOverlap(header) && inBar,
            $"missing=[{missing}] inBar={inBar} targets={CompendiumTargetsFit(header)} " +
            $"overlap={!CompendiumNoOverlap(header)} header={bar} " +
            string.Join("; ", header.Select(c => $"{c.Name}={c.GetGlobalRect()}")));
    }

    private void CheckCompendiumAdvanced(Control screen, Control sidebar, bool expectedVisible)
    {
        List<Control> advanced = CompendiumAdvanced(screen);
        bool allThere = advanced.Count == CompendiumAdvancedNames.Length;
        bool state = allThere && advanced.All(control => control.IsVisibleInTree() == expectedVisible);
        bool fit = !allThere || !expectedVisible || (CompendiumTargetsFit(advanced) && CompendiumNoOverlap(advanced)
            && sidebar.GetGlobalRect().Grow(2f).Encloses(advanced[0].GetGlobalRect().Merge(advanced[^1].GetGlobalRect())));
        _report.Check(expectedVisible ? "compendium_advanced_expanded" : "compendium_advanced_collapsed",
            allThere && state && fit,
            $"controls={advanced.Count}/{CompendiumAdvancedNames.Length} visible={state} fit={fit} " +
            string.Join("; ", advanced.Select(c => $"{c.Name}={(c.IsVisibleInTree() ? c.GetGlobalRect().ToString() : "hidden")}")));
    }

    private void CheckCompendiumGrid(string prefix, Control screen, Control sidebar, NCardGrid grid)
    {
        Rect2 bar = sidebar.GetGlobalRect();
        Rect2 view = grid.GetGlobalRect();
        Rect2 safe = PortraitSafeRect();
        bool below = view.Position.Y >= bar.End.Y - 1f && safe.Grow(1f).Encloses(view);
        bool width = Math.Abs(view.Size.X - safe.Size.X) <= 2f && view.Position.X <= 1f;
        bool tall = view.Size.Y >= 420f;
        var rows = LauncherGridRows(grid);
        float floor = LauncherGridCardWidthFloor(grid);
        bool widths = rows.SelectMany(row => row).All(holder => LauncherGridCardRect(holder).Size.X >= floor - 1f);
        bool columns = rows.Count > 0 && rows.Any(row => row.Count() == 3) && rows.All(row => row.Count() <= 3)
            && widths && LauncherRowsDoNotOverlap(rows);
        _report.Check($"{prefix}_grid", grid.ClipContents && below && width && tall && columns,
            $"view={view} header={bar} clipped={grid.ClipContents} rows={LauncherRowCounts(rows)} " +
            $"minCard={floor:F0} widths={widths} overlap={!LauncherRowsDoNotOverlap(rows)}");
    }

    private void CheckCompendiumLandscapeNative(Control screen, NCardGrid grid)
    {
        Control? sidebar = screen.GetNodeOrNull<Control>("Sidebar");
        Control? search = CompendiumUnique(screen, "SearchBar");
        Rect2 sidebarRect = sidebar?.GetGlobalRect() ?? default;
        Rect2 gridRect = grid.GetGlobalRect();
        bool advancedVisible = CompendiumAdvanced(screen).All(control => control.IsVisibleInTree());
        bool insideSidebar = search is not null && sidebar is not null
            && sidebar.GetGlobalRect().Grow(2f).Encloses(search.GetGlobalRect());
        _report.Check("compendium_landscape_native",
            sidebar is not null && Math.Abs(sidebarRect.Size.X - 288f) < 2f
            && Math.Abs(gridRect.Position.X - 288f) < 2f
            && insideSidebar && advancedVisible && CompendiumToggle(screen) is null,
            $"sidebar={sidebarRect} grid={gridRect} searchInside={insideSidebar} advanced={advancedVisible} " +
            $"toggle={(CompendiumToggle(screen) is null ? "none" : "present")}");
    }
}
