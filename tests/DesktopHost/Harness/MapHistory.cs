using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

internal sealed partial class TestDriver
{
    private async Task RunMapHistoryAsync(RunState state)
    {
        NMapScreen map = NMapScreen.Instance!;
        // Isolated history fixture: reconstruct a connected nine-floor route
        // using real run state, then let native SetMap/Open create its circles.
        // This checks reconstruction, not nine completed gameplay encounters.
        state.ClearVisitedMapCoordsDebug();
        var cursor = state.Map.StartingMapPoint;
        for (int i = 0; i < 9 && cursor is not null; i++)
        {
            state.AddVisitedMapCoord(cursor.coord);
            cursor = cursor.Children.FirstOrDefault();
        }
        map.Close();
        map.SetMap(state.Map, state.Rng.Seed, clearDrawings: true);
        map.Open();
        await WaitSecondsAsync(1);
        CheckVisitedCircles(map, "reconstructed");
        await CaptureAsync("map_history_01");
        Rect2 window = map.GetNodeOrNull<Control>(MapViewportNodeName())?.GetGlobalRect() ?? map.GetGlobalRect();
        Vector2 start = window.Position + new Vector2(24f, 200f);
        await _pointer.DragAsync(start, start + new Vector2(0f, 900f), 30);
        await WaitSecondsAsync(0.6);
        CheckVisitedCircles(map, "scrolled");
        await CaptureAsync("map_history_02_scrolled");
        map.Close();
        map.Open();
        await WaitSecondsAsync(0.6);
        CheckVisitedCircles(map, "reopened");
        // Also exercise a circle created AFTER the enlarged hitbox exists.
        NNormalMapPoint next = FindAll<NNormalMapPoint>(map).First(p => !state.VisitedMapCoords.Contains(p.Point.coord));
        next.State = MapPointState.Traveled;
        await WaitSecondsAsync(0.6);
        CheckVisitedCircles(map, "new_visit");
        await CaptureAsync("map_history_03_new_visit");
    }

    private void CheckVisitedCircles(NMapScreen map, string phase)
    {
        int count = 0;
        foreach (NNormalMapPoint point in FindAll<NNormalMapPoint>(map))
        foreach (NMapCircleVfx circle in point.GetChildren().OfType<NMapCircleVfx>())
        {
            count++;
            Vector2 expected = point.GetGlobalTransform() * point.PivotOffset;
            Vector2 center = circle.GetGlobalTransform() * (circle.Size * 0.5f);
            _report.Check($"map_circle_{phase}_{point.Point.coord}", expected.DistanceTo(center) < 3f,
                $"point={expected} circle={center} delta={expected.DistanceTo(center):F1}");
        }
        _report.Check($"map_circle_{phase}_coverage", count >= 8, $"native circles={count}");
    }
}
