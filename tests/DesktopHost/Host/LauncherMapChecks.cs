using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// Launcher-specific checks for the shared <c>map</c> and <c>map-history</c> scenarios: native parchment,
/// chart clipping, the overlay Back button, bottom tools, the legend toggle and landscape restoration.
/// </summary>
internal sealed partial class TestDriver
{
    /// <summary>The original map-node scale, kept by the launcher's neighbour-aware 44 pt pads.</summary>
    private const float LauncherMapPointScale = 1.25f;
    /// <summary>The native legend's original size in points: 2.3 at 1680 ≈ 1.48 at 1080.</summary>
    private const float LauncherLegendScale = 1.4f;
    /// <summary>Six phone points: below this no legend line reads, whatever the native theme's base size.</summary>
    private const float LauncherLegendMinFont = 6f * PortraitBands.UnitsPerPoint;

    private static float LauncherMapHeaderBottom(Window root) =>
        PortraitBands.HeaderBottom(PortraitLayout.ComputeCapHeight(root));

    private partial string MapViewportNodeName() => "PortraitMapViewport";

    private partial string MapLegendToggleNodeName() => "PortraitLegendPill";

    private partial void CheckMapBackdrop(NMapScreen map)
    {
        bool hasOverlay = map.GetNodeOrNull<Control>("PortraitMapBackdrop") is not null;
        _report.Check("map_has_no_tinted_backdrop", !hasOverlay, "use the native parchment and backstop");
        if (!SafeAreaCanvas.IsPortrait) return;

        Control? chart = map.GetNodeOrNull<Control>(MapViewportNodeName());
        Control? parchment = chart?.FindChild("MapBg", true, false) as Control;
        Rect2 band = chart?.GetGlobalRect() ?? default;
        Rect2 tiles = default;
        bool hasArt = false;
        if (parchment is not null && parchment.IsVisibleInTree())
        {
            foreach (TextureRect tile in parchment.GetChildren().OfType<TextureRect>())
            {
                Rect2 rect = tile.GetGlobalRect();
                if (!tile.IsVisibleInTree() || tile.Texture is null || !rect.Intersects(band)) continue;
                tiles = hasArt ? tiles.Merge(rect) : rect;
                hasArt = true;
            }
        }
        // Torn edges are intentional. The native tiles must still cover the chart vertically.
        _report.Check("map_parchment_covers_chart_height", hasArt &&
            tiles.Position.Y <= band.Position.Y + 2f && tiles.End.Y >= band.End.Y - 2f,
            $"parchment={tiles} chart={band}");
    }

    private partial void CheckMapLayout(NMapScreen map, Control? viewport, IReadOnlyList<NMapPoint> points)
    {
        Rect2 safe = PortraitSafeRect();
        Control? tools = map.GetNodeOrNull<Control>("%DrawingTools") ?? map.GetNodeOrNull<Control>("DrawingTools");
        if (!SafeAreaCanvas.IsPortrait)
        {
            _report.Check("map_landscape_has_no_chart", viewport is null, $"viewport={viewport?.GetGlobalRect()}");
            _report.Check("map_tools_native_landscape_scale", tools is null || tools.Scale == Vector2.One,
                $"scale={tools?.Scale}");
            Control? theMap = map.GetNodeOrNull<Control>("TheMap");
            _report.Check("map_landscape_native_centre", theMap is null || Mathf.IsZeroApprox(theMap.Position.X),
                $"TheMap.x={theMap?.Position.X}");
            return;
        }

        float headerBottom = LauncherMapHeaderBottom(_tree.Root);
        _report.Check("map_chart_clipped", viewport is { ClipContents: true },
            $"viewport={(viewport is null ? "missing" : $"{viewport.GetType().Name} clip={viewport.ClipContents}")}");
        if (viewport is null) return;
        Rect2 chart = viewport.GetGlobalRect();
        _report.Check("map_chart_below_header", chart.Position.Y >= headerBottom - 1f,
            $"chart={chart} headerBottom={headerBottom:F0}");
        _report.Check("map_chart_inside_safe", safe.Grow(1f).Encloses(chart), $"chart={chart} safe={safe}");
        _report.Check("map_reachable_nodes_in_frame", points.Where(point => MapNodeTravelable(point))
            .Any(point => chart.Encloses(point.GetGlobalRect())), "at least one reachable node is fully touchable");

        // Back overlays the parchment; only the drawing tools and Share sit below it.
        Control? back = map.GetNodeOrNull<Control>("Back");
        Rect2 canvasRect = new(Vector2.Zero, GetViewportSize());
        Control? footer = map.GetNodeOrNull<Control>("PortraitMapFooterInput");
        _report.Check("map_draws_behind_footer", MathF.Abs(chart.End.Y - canvasRect.End.Y) <= 1f &&
            footer is { MouseFilter: Control.MouseFilterEnum.Stop } && tools is { } toolbar &&
            MathF.Abs(footer.GetGlobalRect().Position.Y - toolbar.GetGlobalRect().Position.Y) <= 1f,
            $"chart={chart} footer={footer?.GetGlobalRect()} tools={tools?.GetGlobalRect()}");
        _report.Check("map_back_over_chart", back is not null && chart.Grow(1f).Encloses(back.GetGlobalRect()),
            $"chart={chart} back={back?.GetGlobalRect()}");
        if (tools is { } bar && back is { } backButton)
        {
            Rect2 barRect = bar.GetGlobalRect();
            Rect2 backRect = backButton.GetGlobalRect();
            Rect2 backTarget = MapToolTouchBox(backButton);
            float bottomGap = canvasRect.End.Y - barRect.End.Y;
            _report.Check("map_footer_tools_at_bottom",
                barRect.Position.Y >= backRect.End.Y - 0.5f && bottomGap >= -0.5f
                && bottomGap <= 10f * PortraitBands.UnitsPerPoint,
                $"back={backRect} tools={barRect} bottomGap={bottomGap:F1} canvas={canvasRect}");
            _report.Check("map_footer_back_clear_of_tools",
                !barRect.Intersects(backRect) && backTarget.Size.Y >= PortraitBands.MinTarget - 1f,
                $"back={backRect} target={backTarget} tools={barRect}");
        }
        if (tools is { } barUnderShare && FindFirst<NMapShareButton>(map) is { } share)
        {
            Rect2 shareRect = share.GetGlobalRect();
            Rect2 barRect = barUnderShare.GetGlobalRect();
            bool clearOfBack = back is null || !shareRect.Intersects(back.GetGlobalRect());
            _report.Check("map_footer_share_clear",
                clearOfBack && !shareRect.Intersects(barRect) && safe.Grow(1f).Encloses(shareRect),
                $"share={shareRect} back={back?.GetGlobalRect()} tools={barRect} safe={safe}");
        }

        Control? pill = map.GetNodeOrNull<Control>(MapLegendToggleNodeName());
        _report.Check("map_legend_pill_touch_target", pill is not null && pill.Size.Y >= PortraitBands.MinTarget - 1f
            && safe.Encloses(pill.GetGlobalRect()), $"pill={pill?.GetGlobalRect()} chart={chart} safe={safe}");
        Control? legend = map.GetNodeOrNull<Control>("%MapLegend");
        _report.Check("map_legend_closed_initially", legend is null || !legend.Visible, "the native legend starts closed");
    }

    private partial float MapNodeExpectedScale() => LauncherMapPointScale;

    private partial float MapToolMinSize() => PortraitBands.MinTarget;

    private partial Rect2 MapToolTouchBox(Control button)
    {
        Rect2 drawn = button.GetGlobalRect();
        if (PhoneHitArea.Of(button) is not { } area) return drawn;
        Rect2 hit = area.GetGlobalRect();
        return hit.Size.Y > drawn.Size.Y ? hit : drawn;
    }

    private partial async Task<bool> ToggleMapLegendAsync(NMapScreen map)
    {
        if (!SafeAreaCanvas.IsPortrait) return false;
        if (map.GetNodeOrNull<Control>(MapLegendToggleNodeName()) is not { } pill)
        {
            _report.Fail("map_legend_toggle", "the launcher's PortraitLegendPill was not found");
            return false;
        }
        await _pointer.TapAsync(CenterOf(pill));
        await WaitFramesAsync(6);
        return true;
    }

    private partial void CheckMapLegend(NMapScreen map, bool open)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Control? legend = map.GetNodeOrNull<Control>("%MapLegend");
        Control? pill = map.GetNodeOrNull<Control>(MapLegendToggleNodeName());
        Rect2 safe = PortraitSafeRect();
        if (open)
        {
            Rect2 rect = legend?.GetGlobalRect() ?? default;
            float smallest = legend is null
                ? 0f
                : FindAll<Label>(legend).Where(label => label.IsVisibleInTree())
                    .Select(label => label.GetThemeFontSize("font_size") * label.GetGlobalTransform().Scale.Abs().Y)
                    .DefaultIfEmpty(0f).Min();
            _report.Check("map_legend_opens", legend is { } shown && shown.Visible
                && shown.Scale.X >= LauncherLegendScale - 0.02f && safe.Grow(1f).Encloses(rect)
                && pill is not null && rect.Position.Y >= pill.GetGlobalRect().End.Y - 1f,
                $"scale={legend?.Scale} rect={rect} pill={pill?.GetGlobalRect()} safe={safe}");
            _report.Check("map_legend_readable", smallest >= LauncherLegendMinFont - 0.5f,
                $"smallest line={smallest:F1} units ({smallest / PortraitBands.UnitsPerPoint:F1} pt), min={LauncherLegendMinFont:F1}");
        }
        else
        {
            _report.Check("map_legend_closes", legend is null || !legend.Visible,
                $"legend={(legend is null ? "missing" : legend.Visible.ToString())}");
        }
    }

    private partial void CheckMapFooterLandscape(NMapScreen map, Control? viewport, Control tools, Control share)
    {
        _report.Check("map_tools_native_landscape", viewport is null && tools.Scale == Vector2.One && share.GetParent() == map,
            $"drawingScale={tools.Scale} shareScale={share.Scale}");
        _report.Check("map_landscape_no_legend_pill", map.GetNodeOrNull<Control>(MapLegendToggleNodeName()) is null,
            "the portrait legend pill is gone");
    }

    private partial async Task CheckMapScrollBoundsAsync(NMapScreen map, Control? viewport)
    {
        if (!SafeAreaCanvas.IsPortrait || viewport is not { } clip) return;
        Type type = map.GetType();
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (type.GetField("_bossPointNode", Flags)?.GetValue(map) is not Control boss) return;
        Control topBoss = type.GetField("_secondBossPointNode", Flags)?.GetValue(map) is Control second
            && second.IsVisibleInTree() && second.GlobalPosition.Y < boss.GlobalPosition.Y ? second : boss;
        Control? start = type.GetField("_startingPointNode", Flags)?.GetValue(map) as Control;
        Rect2 chart = MapInteractiveRect(map, clip);
        // Start both drags clear of the Back ribbon, which overlays the bottom-left of the parchment.
        Vector2 nearTop = new(chart.GetCenter().X, chart.Position.Y + chart.Size.Y * 0.2f);
        Vector2 nearBottom = new(nearTop.X, chart.Position.Y + chart.Size.Y * 0.9f);

        // Dragging down raises the scroll target towards the top of the act: the boss medallion must come into the chart,
        // and its whole touch target with it.
        Rect2 bossBox = default;
        for (int i = 0; i < 5; i++)
        {
            await _pointer.DragAsync(nearTop, nearBottom, 22);
            await WaitSecondsAsync(0.6);
            bossBox = topBoss.GetGlobalRect();
            if (chart.Grow(10f).Encloses(bossBox)) break;
        }
        _report.Check("map_scroll_reaches_boss", chart.Grow(10f).Encloses(bossBox), $"boss={bossBox} chart={chart}");
        await CaptureAsync("map_02b_boss");
        CheckMapNodesInsidePaper(map, "top");
        if (start is null) return;

        // Dragging up goes the other way: the starting point must be fully reachable above the chart's bottom.
        Rect2 startBox = default;
        for (int i = 0; i < 6; i++)
        {
            await _pointer.DragAsync(nearBottom, nearTop, 22);
            await WaitSecondsAsync(0.6);
            startBox = start.GetGlobalRect();
            if (chart.Grow(10f).Encloses(startBox)) break;
        }
        _report.Check("map_scroll_reaches_start", chart.Grow(10f).Encloses(startBox), $"start={startBox} chart={chart}");
    }

    /// <summary>
    /// The parchment rect is measured from the drawn <c>MapBg</c> (its centre is the sheet's centre, its scale is the
    /// graph's) so the check follows the art, not the constants. Every visible map icon, grown by the native 1.45 focus
    /// growth, must sit inside that rect inset by <see cref="PortraitBetweenMath.MapNodePaperMargin"/>.
    /// </summary>
    private partial void CheckMapNodesInsidePaper(NMapScreen map, string suffix)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        string name = suffix.Length == 0 ? "map_nodes_inside_paper" : $"map_nodes_inside_paper_{suffix}";
        Vector2 canvas = GetViewportSize();
        Control? bg = map.FindChild("MapBg", true, false) as Control;
        float paperWidth;
        float paperCentre;
        if (bg is not null && GodotObject.IsInstanceValid(bg))
        {
            // MapBg is the 1080-wide sheet control widened by MaxMapCentreShift a side; the 1260-wide parchment body is
            // centred in it. Control.GetGlobalRect ignores CanvasItem scale in its size, so take the drawn scale and
            // centre from the actual global transform.
            Transform2D bgXform = bg.GetGlobalTransform();
            paperWidth = PortraitBetweenMath.MapPaperTextureWidth * bgXform.Scale.Abs().X;
            paperCentre = (bgXform * (bg.Size * 0.5f)).X;
        }
        else
        {
            paperWidth = PortraitBetweenMath.MapPaperWidth(canvas.X);
            paperCentre = canvas.X * 0.5f;
        }
        float paperLeft = paperCentre - paperWidth * 0.5f;
        float paperRight = paperCentre + paperWidth * 0.5f;
        float boundLeft = paperLeft + PortraitBetweenMath.MapNodePaperMargin;
        float boundRight = paperRight - PortraitBetweenMath.MapNodePaperMargin;
        float unitsPerPoint = canvas.X / 390f;
        float worstLeft = float.MaxValue, worstRight = float.MaxValue, widest = 0f, widestFocus = 0f;
        int count = 0, outside = 0;
        string worst = "";
        foreach (NMapPoint point in FindAll<NMapPoint>(map).Where(point => point.IsVisibleInTree()))
        {
            // Measure the drawn icon itself, not the point's input rect: the boss/start markers draw their picture through
            // an offset IconContainer, and a node's scale is not part of Control.GetGlobalRect's size.
            Control drawn = point.GetNodeOrNull<Control>("%IconContainer") ?? point;
            Transform2D xform = drawn.GetGlobalTransform();
            Vector2 scale = xform.Scale.Abs();
            Vector2 centre = xform * (drawn.Size * 0.5f);
            float width = drawn.Size.X * scale.X;
            float half = width * 0.5f * PortraitBetweenMath.MapNodeFocusGrowth;
            float left = centre.X - half, right = centre.X + half;
            count++;
            widest = Math.Max(widest, width);
            widestFocus = Math.Max(widestFocus, width * PortraitBetweenMath.MapNodeFocusGrowth);
            float leftClearance = left - boundLeft, rightClearance = boundRight - right;
            if (Math.Min(leftClearance, rightClearance) < Math.Min(worstLeft, worstRight))
                worst = $"{point.GetType().Name} {point.Point.coord} centre={centre.X:F1} rect={left:F1}..{right:F1}";
            worstLeft = Math.Min(worstLeft, leftClearance);
            worstRight = Math.Min(worstRight, rightClearance);
            if (left < boundLeft - 0.5f || right > boundRight + 0.5f) outside++;
        }
        _report.Check(name, count > 0 && outside == 0,
            $"paper={paperLeft:F1}..{paperRight:F1} inset={boundLeft:F1}..{boundRight:F1} margin={PortraitBetweenMath.MapNodePaperMargin:F1} " +
            $"points={count} outside={outside} clearance left={worstLeft:F1} right={worstRight:F1} units " +
            $"({worstLeft / unitsPerPoint:F1}/{worstRight / unitsPerPoint:F1} pt) " +
            $"icon={widest:F1} units ({widest / unitsPerPoint:F1} pt) focus={widestFocus:F1} units ({widestFocus / unitsPerPoint:F1} pt) " +
            $"worst=({worst})");
    }

    /// <summary>
    /// Rebuilds the chart on Hive (act 2) and repeats the parchment check, so a different act's columns are covered too.
    /// Runs at the end of the map scenario: the act switch replaces the run's map.
    /// </summary>
    private partial async Task CheckMapSecondActAsync(NMapScreen map)
    {
        if (!SafeAreaCanvas.IsPortrait || !GodotObject.IsInstanceValid(map)) return;
        RunState? state = RunManager.Instance.DebugOnlyGetState();
        if (state is null) return;
        try
        {
            map.Close();
            await ConfigureTourActAsync(state, "Hive");
            await WaitSecondsAsync(0.8);
            map.SetMap(state.Map, state.Rng.Seed, clearDrawings: true);
            map.Open(isOpenedFromTopBar: true);
            await WaitSecondsAsync(1.2);
        }
        catch (Exception error)
        {
            // The act switch is fixture-only; if it cannot run at the end of a travelled scenario, say so instead of failing
            // the framing run. The act-1 parchment check has already run by this point.
            _report.Skip("map_nodes_inside_paper_act2", $"act-2 map pass unavailable: {error.Message}");
            return;
        }
        CheckMapNodesInsidePaper(map, "act2");
        await CaptureAsync("map_04_act2");
    }
}
