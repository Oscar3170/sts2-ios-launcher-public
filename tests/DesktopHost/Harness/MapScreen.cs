using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The Spire Map scenario (L6): open the real act map, count its points, toggle the host's legend, draw / erase / clear
/// strokes and Share, swipe the chart and travel by tapping a reachable node. The launcher's answers (its clipped chart,
/// band coverage, legend size, footer targets and the native landscape) are in Host/LauncherMapChecks.cs.
/// </summary>
internal sealed partial class TestDriver
{
    private async Task RunMapScenarioAsync(RunState runState)
    {
        _ = runState;
        await WaitUntilAsync(
            () => NMapScreen.Instance is { } screen
                  && GodotObject.IsInstanceValid(screen)
                  && screen.IsInsideTree(),
            "map screen",
            MediumWait);
        NMapScreen map = NMapScreen.Instance!;
        // The standard launcher may still be in Neow. Open the real act map
        // and enable its eligible node instead of making this test depend on a blessing.
        if (!map.IsOpen) map.Open(isOpenedFromTopBar: true);
        // Opening from the top bar skips the game's one-time "Select a Starting Room" hint, and NMapPoint.OnRelease refuses
        // a row-0 point until that hint is recorded: the act's only reachable row would silently ignore the travel tap.
        // Record it on the isolated test profile, as the other scenarios do for their hints.
        SaveManager.Instance.MarkFtueAsComplete("map_select_ftue");
        map.SetTravelEnabled(true);
        await WaitSecondsAsync(1);
        CheckMapBackdrop(map);
        List<NMapPoint> points = FindAll<NMapPoint>(map)
            .Where(point => point.IsVisibleInTree())
            .ToList();
        foreach (NMapPoint point in points)
        {
            Log.Info($"[TEST] map point: type={point.GetType().Name} coord={point.Point.coord} scale={point.Scale} size={point.Size}");
        }

        Control? viewport = map.GetNodeOrNull<Control>(MapViewportNodeName());
        CheckMapLayout(map, viewport, points);
        _report.Check("map_screen_loaded", map.IsInsideTree(), "map screen is present");
        _report.Check("map_has_points", points.Count > 0, $"points={points.Count}");
        if (IsPortraitHost())
            _report.Check(
                "map_nodes_scaled",
                points.Any(point => point.Scale.X >= MapNodeExpectedScale() * 0.95f),
                $"scales={string.Join(",", points.Select(point => point.Scale.X.ToString("F2")))} expected>={MapNodeExpectedScale():F2}");
        await WaitSecondsAsync(2);
        await CaptureAsync("map_01_nodes");
        if (await ToggleMapLegendAsync(map))
        {
            CheckMapLegend(map, true);
            await CaptureAsync("map_01b_legend_open");
            await ToggleMapLegendAsync(map);
            CheckMapLegend(map, false);
        }

        if (!IsPortraitHost() || viewport is not null) await CheckMapToolsAsync(map, viewport);
        if (IsPortraitHost() && viewport is not null)
        {
            Control graph = (Control)map.FindChild("TheMap", true, false)!;
            Vector2 before = graph.Position;
            // Stay below the relic hover strip, which is taller in 9:21.
            Vector2 swipeStart = viewport.GetGlobalRect().Position + new Vector2(40f, 440f);
            await _pointer.DragAsync(swipeStart, swipeStart + new Vector2(0f, 900f), 30);
            await WaitSecondsAsync(0.5);
            _report.Check("map_swipe_scrolls", Math.Abs(graph.Position.Y - before.Y) > 100f,
                $"before={before} after={graph.Position}");
            await CaptureAsync("map_02_scrolled");
            // T4: the same scroll begun on a node must move the chart and never travel to that node. The finger leaves the
            // chart at its top centre (the header, where no map point sits), so the release cannot select another node.
            Rect2 chartRect = MapInteractiveRect(map, viewport);
            NMapPoint? swipeNode = points.Where(point => point.IsEnabled && chartRect.Encloses(point.GetGlobalRect()))
                .OrderByDescending(point => CenterOf(point).Y).FirstOrDefault();
            if (swipeNode is not null)
            {
                Vector2 nodeCentre = CenterOf(swipeNode);
                Vector2 nodeBefore = graph.Position;
                await _pointer.DragAsync(nodeCentre, new Vector2(chartRect.GetCenter().X, chartRect.Position.Y - 60f), 24);
                await WaitSecondsAsync(0.6);
                _report.Check("map_swipe_from_node_never_travels",
                    map.IsOpen && !map.IsTraveling
                    && Math.Abs(graph.Position.Y - nodeBefore.Y) > 60f,
                    $"node={swipeNode.Point.coord} open={map.IsOpen} traveling={map.IsTraveling} " +
                    $"scrolls={Math.Abs(graph.Position.Y - nodeBefore.Y) > 60f} before={nodeBefore} after={graph.Position}");
                await CaptureAsync("map_02a_swiped_from_node");
            }
            else _report.Skip("map_swipe_from_node_never_travels", "no enabled node inside the chart");
            await CheckMapScrollBoundsAsync(map, viewport);
            CheckMapNodesInsidePaper(map, "");
            // Keep the return-to-start drag off the footer's Back ribbon, which owns the bottom-left lane and would
            // swallow the press, so the chart actually follows the finger back to the act's starting point.
            Vector2 scrollLane = new(chartRect.GetCenter().X, swipeStart.Y);
            await _pointer.DragAsync(scrollLane + new Vector2(0f, 1000f), scrollLane, 30);
            await WaitSecondsAsync(0.5);
            NMapPoint? reachable = points.FirstOrDefault(p =>
                MapNodeTravelable(p) && chartRect.Encloses(p.GetGlobalRect()));
            if (reachable is not null)
            {
                // Keep this finger stationary in the GUI as well as in the injected touch stream.
                await VirtualPointer.EnsureWindowUnobscuredAsync(initial: false, resumeTo: CenterOf(reachable));
                bool previousWarp = VirtualPointer.SuppressOsWarp;
                VirtualPointer.SuppressOsWarp = true;
                try
                {
                    await _pointer.TapAsync(CenterOf(reachable));
                }
                finally
                {
                    VirtualPointer.SuppressOsWarp = previousWarp;
                }
                await WaitUntilAsync(() => !map.IsOpen, "map node navigation", MediumWait);
                _report.Pass("map_node_tap_navigates");
                await WaitSecondsAsync(1);
                await CaptureAsync("map_03_destination");
            }
            else _report.Fail("map_node_tap_navigates", "no reachable node after scrolling back");
            await CheckMapSecondActAsync(map);
        }
    }

    private static Rect2 MapInteractiveRect(NMapScreen map, Control? viewport)
    {
        Rect2 chart = viewport?.GetGlobalRect() ?? map.GetGlobalRect();
        if (map.GetNodeOrNull<Control>("PortraitMapFooterInput") is { } footer)
            chart.Size = new Vector2(chart.Size.X, footer.GetGlobalRect().Position.Y - chart.Position.Y);
        return chart;
    }

    /// <summary>
    /// The node whose picture must cover the band below the header without empty bands (the launcher's parchment) and the
    /// portrait-only chart framing and legend geometry.
    /// </summary>
    private partial void CheckMapBackdrop(NMapScreen map);

    /// <summary>The host's chart window around <c>TheMap</c> (lost in landscape).</summary>
    private partial string MapViewportNodeName();

    /// <summary>The one control that opens and closes the host's legend.</summary>
    private partial string MapLegendToggleNodeName();

    /// <summary>The chart window's clip and band, the points inside it, the legend's size and the native landscape.</summary>
    private partial void CheckMapLayout(NMapScreen map, Control? viewport, IReadOnlyList<NMapPoint> points);

    /// <summary>Taps the host's legend toggle once and returns whether the host has one.</summary>
    private partial Task<bool> ToggleMapLegendAsync(NMapScreen map);

    /// <summary>The legend after the toggle: the launcher's readable native one.</summary>
    private partial void CheckMapLegend(NMapScreen map, bool open);

    /// <summary>The point scale the host gives every room node (the launcher's constant).</summary>
    private partial float MapNodeExpectedScale();

    /// <summary>The smallest footer tool side in the host's units: 44 points.</summary>
    private partial float MapToolMinSize();

    /// <summary>The untouched native footer when the host leaves landscape alone.</summary>
    private partial void CheckMapFooterLandscape(NMapScreen map, Control? viewport, Control tools, Control share);

    /// <summary>
    /// The chart's scroll range: dragging to one end must bring the act's boss fully into the chart and the other end the
    /// starting point.
    /// </summary>
    private partial Task CheckMapScrollBoundsAsync(NMapScreen map, Control? viewport);

    /// <summary>
    /// Every drawn map icon — all rows, at the chart's top and bottom scroll — must sit on the parchment, inside its torn
    /// side edges by the reference margin, even with the native focus growth. Empty suffix is the act-1 step name.
    /// </summary>
    private partial void CheckMapNodesInsidePaper(NMapScreen map, string suffix);

    /// <summary>An act-2 map pass for the same framing check (the launcher rebuilds the chart on Hive).</summary>
    private partial Task CheckMapSecondActAsync(NMapScreen map);

    /// <summary>
    /// The game's own "this node can be travelled to now" answer (NMapPoint.IsTravelable, internal in the shipping assembly,
    /// so read by name). IsEnabled only means the node accepts input, which includes decorative/non-travelable points.
    /// </summary>
    private static bool MapNodeTravelable(NMapPoint point) =>
        point.GetType().GetProperty("IsTravelable", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(point) is true;
}
