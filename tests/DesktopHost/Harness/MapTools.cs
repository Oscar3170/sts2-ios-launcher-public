using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace STS2Mobile.DesktopHost.Harness;

internal sealed partial class TestDriver
{
    /// <summary>
    /// The map's drawing toolbar and Share, shared by both hosts: a finger on the toolbar toggles the native drawing mode,
    /// a drag draws under the finger, the stroke moves exactly with the graph when the chart scrolls, erasing and clearing
    /// work, and Share's native toast stays inside the host's portrait frame. Only the sizes and rocks are host partials.
    /// </summary>
    private async Task CheckMapToolsAsync(NMapScreen map, Control? viewport)
    {
        Control? drawingTools = map.GetNodeOrNull<Control>("DrawingTools") ?? map.GetNodeOrNull<Control>("%DrawingTools");
        if (drawingTools is not { } tools)
        {
            _report.Fail("map_tools_present", "the map's DrawingTools node was not found");
            return;
        }
        var draw = tools.GetNode<Control>("HBoxContainer/DrawButton");
        var erase = tools.GetNode<Control>("HBoxContainer/EraseButton");
        var clear = tools.GetNode<Control>("HBoxContainer/ClearButton");
        var share = FindFirst<NMapShareButton>(map)!;
        if (!IsPortraitHost())
        {
            CheckMapFooterLandscape(map, viewport, tools, share);
            return;
        }

        Rect2 safe = PortraitSafeRect();
        Rect2 graph = MapInteractiveRect(map, viewport);
        float minTool = MapToolMinSize();
        foreach (var button in new Control[] { draw, erase, clear, share })
        {
            Rect2 box = button.GetGlobalRect();
            Rect2 target = MapToolTouchBox(button);
            // The visual chart can extend behind the footer, but its input area ends above the controls.
            Rect2 canvasRect = new(Vector2.Zero, GetViewportSize());
            _report.Check($"map_tool_inside_footer_{button.Name}", canvasRect.Encloses(target)
                && target.Position.Y >= safe.Position.Y - 1f && !graph.Grow(-2f).Intersects(box)
                && target.Size.Y >= minTool - 0.01f,
                $"button={box} target={target} graph={graph} canvas={canvasRect} safe={safe} min={minTool:F0}");
        }
        var back = map.GetNode<Control>("Back");
        _report.Check("map_tools_clear_of_back", !back.GetGlobalRect().Intersects(tools.GetGlobalRect()),
            $"back={back.GetGlobalRect()} tools={tools.GetGlobalRect()}");

        int StrokeCount() => map.Drawings.GetSerializableMapDrawings().drawings.Sum(d => d.lines.Count);
        // Native lines live in a half-resolution SubViewport; project through
        // its displayed texture rather than treating line points as map pixels.
        Vector2 ProjectStrokePoint(Line2D stroke, int index)
        {
            var drawViewport = (SubViewport)stroke.GetParent();
            var texture = drawViewport.GetParent().GetNode<TextureRect>("DrawViewportTextureRect");
            return texture.GetGlobalTransform() * (stroke.GetPointPosition(index) * texture.Size / (Vector2)drawViewport.Size);
        }
        // A press on a toolbar button also reaches the native drawing input (the launcher has no `CanStartStroke` patch), so
        // a one-point stroke can appear at the tool. Only strokes that begin inside the chart are the player's.
        bool InChart(Line2D stroke) => stroke.GetPointCount() > 0 && graph.Grow(4f).HasPoint(ProjectStrokePoint(stroke, 0));
        int ChartStrokes() => FindAll<Line2D>(map.Drawings).Count(InChart);
        await _pointer.TapAsync(CenterOf(draw));
        await WaitFramesAsync(5);
        _report.Check("map_draw_tap_toggles", map.Drawings.GetLocalDrawingMode().ToString() == "Drawing" && ChartStrokes() == 0,
            $"mode={map.Drawings.GetLocalDrawingMode()} strokes={StrokeCount()} chartStrokes={ChartStrokes()}");
        Vector2 start = graph.Position + new Vector2(graph.Size.X * 0.27f, graph.Size.Y * 0.45f);
        Vector2 end = start + new Vector2(220f, 150f);
        // A phone sends only the touch drag; the harness's desktop cursor warp is a native mouse motion on X11, and the
        // game's freehand drawing records every motion as another point (a second, frame-offset stream alternating with the
        // touch points). Suppress the warp for this gesture so the line is exactly the stream the phone sends; the endpoints
        // are still checked to 8 units rather than tolerated.
        VirtualPointer.SuppressOsWarp = true;
        try
        {
            await _pointer.DragAsync(start, end, 12);
        }
        finally
        {
            VirtualPointer.SuppressOsWarp = false;
        }
        await WaitFramesAsync(5);
        var line = FindAll<Line2D>(map.Drawings).FirstOrDefault(InChart);
        Vector2? actualStart = line is null ? null : ProjectStrokePoint(line, 0);
        Vector2? actualEnd = line is null ? null : ProjectStrokePoint(line, line.GetPointCount() - 1);
        _report.Check("map_stroke_under_finger", ChartStrokes() == 1 && actualStart is { } a && a.DistanceTo(start) < 8f
            && actualEnd is { } b && b.DistanceTo(end) < 8f,
            $"strokes={StrokeCount()} chartStrokes={ChartStrokes()} from={start}->{actualStart} to={end}->{actualEnd}");
        await CaptureAsync("map_tools_01_drawing");
        if (line is not null)
        {
            await _pointer.TapAsync(CenterOf(draw));
            Control mapGraph = (Control)map.Drawings.GetParent();
            Vector2 graphBefore = mapGraph.GlobalPosition;
            Vector2 markBefore = ProjectStrokePoint(line, 0);
            Vector2 pan = graph.Position + new Vector2(40f, 440f);
            await _pointer.DragAsync(pan, pan + new Vector2(0f, 360f), 18);
            await WaitSecondsAsync(0.45);
            Vector2 graphDelta = mapGraph.GlobalPosition - graphBefore;
            Vector2 markDelta = ProjectStrokePoint(line, 0) - markBefore;
            _report.Check("map_stroke_scrolls_with_graph", graphDelta.Y > 100f && markDelta.DistanceTo(graphDelta) < 3f,
                $"stroke movement={markDelta} graph movement={graphDelta}");
            await _pointer.DragAsync(pan + new Vector2(0f, 420f), pan, 18);
            await WaitSecondsAsync(0.45);
        }
        await _pointer.TapAsync(CenterOf(erase));
        await WaitFramesAsync(5);
        _report.Check("map_erase_tap_no_stray_stroke", map.Drawings.GetLocalDrawingMode().ToString() == "Erasing" && ChartStrokes() == 1,
            $"mode={map.Drawings.GetLocalDrawingMode()} strokes={StrokeCount()} chartStrokes={ChartStrokes()}");
        await _pointer.DragAsync(start, end, 12);
        await WaitFramesAsync(5);
        _report.Check("map_erase_stroke", map.Drawings.GetSerializableMapDrawings().drawings.SelectMany(d => d.lines).Count(l => l.isEraser) == 1,
            $"strokes={StrokeCount()}");
        await _pointer.TapAsync(CenterOf(clear));
        await WaitFramesAsync(5);
        _report.Check("map_clear_tap_removes_strokes", map.Drawings.GetLocalDrawingMode().ToString() == "None" && ChartStrokes() == 0,
            $"strokes={StrokeCount()} chartStrokes={ChartStrokes()}");

        await _pointer.TapAsync(CenterOf(share));
        var toast = map.GetNode<Label>("ShareToast");
        await WaitUntilAsync(() => toast.SelfModulate.A > 0.5f, "native map Share toast", MediumWait);
        _report.Check("map_share_toast", safe.Encloses(toast.GetGlobalRect()), $"toast={toast.GetGlobalRect()}");
        await CaptureAsync("map_tools_02_shared");
    }

    /// <summary>The rectangle a finger can actually hit: the picture, or the host's larger touch area around it.</summary>
    private partial Rect2 MapToolTouchBox(Control button);
}
