using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// The Spire Map in portrait: the native torn scroll fills the clipped chart (points, paths, drawings, marker).
/// The Back ribbon overlays it and the drawing tools and Share sit at the canvas bottom. Every edit is captured
/// first so leaving portrait puts the game's own map and footer back.
/// </summary>
internal sealed partial class PortraitBetweenLayout
{
    private const string LegendPillName = "PortraitLegendPill";
    private const string MapViewportName = "PortraitMapViewport";
    private const string MapShiftName = "PortraitMapShift";
    private const string MapShareMountName = "PortraitMapShare";

    private const float LegendPillWidth = 230f;
    private const int LegendPillFontSize = 34;
    // The map points are drawn a quarter larger around their own centres: the centres, the paths, the visited circles
    // (which are positioned from the point's unchanged pivot) and the centring measure all stay where the game put them.
    private const float MapPointScale = 1.25f;
    // The draw, erase and clear buttons are 60 units in the bar; the whole bar grows until each button is 44 points.
    private const float DrawingToolsScale = PortraitBands.MinTarget / 60f;
    // Share is 64 units tall natively; its mount grows it to the same 44 points without touching its own hover tween.
    // Fallback for the graph clip before the drawing bar can be measured.
    private const float MapFooterBand = 150f;
    // Share and the drawing tools sit this far above the canvas bottom; Share's right edge is 32 units from the edge.
    private const float MapFooterInset = 32f;
    private const float MapFooterBottomInset = 24f;
    private const float MapLegendInset = 20f;
    private const float MapToolGap = 6f;
    private const float MapToastGap = 8f;
    // Native legend is 340 x 454 at scale 1; the phone reference draws it at 2.3 on a 1680-wide canvas, which is
    // 2.3 * 1080 / 1680 at ours (about 1.48: 13-22 point text).
    private static readonly float MapLegendScale = PortraitBetweenMath.MapLegendScale;

    private sealed class MapFrame
    {
        public Control? Clip;
        public Control? Shift;
        public Control? Map;
        public Control? Points;
        public Control? Bg;
        public Control? Tools;
        public float BgBaseLeft;
        public float PaperCenterX;
        public float GraphOffsetX;
        public bool CenterPending;
        public Action? VisibilityHandler;
        public bool PaperLogged;
        public Control? Legend;
        public Button? Pill;
        public Control? Share;
        public Control? ShareMount;
        public Control? Toast;
        public PortraitControlSnapshot? MapSnapshot;
        public PortraitControlSnapshot? ShareSnapshot;
        public PortraitControlSnapshot? ToastSnapshot;
        public Vector2 ToastNativeTarget;
        public bool HasToastTarget;
        public int MapIndex;
        public int ShareIndex;

        public bool IsValid() =>
            Clip is { } clip && GodotObject.IsInstanceValid(clip) &&
            Shift is { } shift && GodotObject.IsInstanceValid(shift) &&
            Map is { } map && GodotObject.IsInstanceValid(map);
    }

    private readonly HashSet<ulong> _maps = new();
    private readonly Dictionary<ulong, MapFrame> _mapFrames = new();
    private readonly Dictionary<ulong, PortraitControlSnapshot> _mapBgSnapshots = new();
    private readonly Dictionary<ulong, PortraitControlSnapshot> _mapDrawingsSnapshots = new();
    private readonly Dictionary<ulong, (ulong First, int Count)> _mapPointLayouts = new();
    private readonly HashSet<ulong> _scaledMapPoints = new();
    private readonly Dictionary<ulong, TextureRect.StretchModeEnum> _stretchModes = new();
    private readonly Dictionary<ulong, bool> _mapHintVisibility = new();
    private bool _legendOpen;

    private Control? MapGraph(Control map)
    {
        if (_mapFrames.TryGetValue(map.GetInstanceId(), out MapFrame? frame) &&
            frame.Map is { } cached && GodotObject.IsInstanceValid(cached))
            return cached;
        return map.GetNodeOrNull<Control>("TheMap") ?? map.FindChild("TheMap", true, false) as Control;
    }

    private void LayoutMapPortrait(Control map, float capHeight, float canvasHeight, float canvasWidth)
    {
        float shiftY = PortraitBetweenMath.MapBackgroundShift(canvasHeight);
        Control? graph = MapGraph(map);
        if (graph?.GetNodeOrNull<Control>("MapBg") is { } bg)
        {
            if (!_mapBgSnapshots.TryGetValue(bg.GetInstanceId(), out PortraitControlSnapshot? snap))
                _mapBgSnapshots[bg.GetInstanceId()] = snap = Capture(bg, captureVisible: false);
            bg.OffsetTop = snap.InitialOffsetTop + shiftY;
            bg.OffsetBottom = snap.InitialOffsetBottom + shiftY;
            // Wider on both sides: each parchment tile covers its control, and the 1920-wide sheet's torn sides would be
            // cropped away in a 1080-wide slice. The graph's uniform scale below then frames the 1260-wide body on the canvas.
            bg.OffsetLeft = snap.InitialOffsetLeft - PortraitBetweenMath.MaxMapCentreShift;
            bg.OffsetRight = snap.InitialOffsetRight + PortraitBetweenMath.MaxMapCentreShift;
            // Each parchment tile is a 1080-tall slice of desktop-wide art: cover the narrow tile instead of fitting inside it,
            // which would leave black bands between the tiles.
            foreach (TextureRect tile in bg.GetChildren().OfType<TextureRect>())
            {
                _stretchModes.TryAdd(tile.GetInstanceId(), tile.StretchMode);
                tile.StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered;
            }
        }
        if (graph?.GetNodeOrNull<Control>("Drawings") is { } drawings)
        {
            PortraitControlSnapshot snap = Capture(drawings, captureVisible: false);
            // The drawing layer is authored on the game's 1920 x 1080 canvas. Its points container is centre-anchored, so
            // the game shifts every point by (container - 1920x1080) / 2, and NMapDrawings.RepositionBasedOnBackground
            // puts this layer at the same place once MapBg is widened symmetrically: a stroke keeps its point's net
            // coordinate and moves exactly with the graph (PortraitBetweenMath.MapLayerOffset).
            float shiftX = PortraitBetweenMath.MapLayerOffsetX(canvasWidth);
            drawings.OffsetLeft = snap.InitialOffsetLeft + shiftX;
            drawings.OffsetRight = snap.InitialOffsetRight + shiftX;
            drawings.OffsetTop = snap.InitialOffsetTop + PortraitBetweenMath.MapLayerOffsetY(canvasHeight);
            drawings.OffsetBottom = snap.InitialOffsetBottom + PortraitBetweenMath.MapLayerOffsetY(canvasHeight);
            // The native drawing pipeline assumes its layer is drawn at scale 1: it stores line points in a half-size
            // SubViewport and samples them through a TextureRect. The uniform graph scale on TheMap would otherwise scale
            // the layer, so a stroke would land away from the finger. Counter-scale the layer (its local position, which
            // the points container shares, is unchanged) to keep its displayed units at 1 while still scrolling with the
            // graph. The mapping is only identity when the layer's global scale is 1.
            float inverse = 1f / PortraitBetweenMath.MapGraphScale(canvasWidth);
            drawings.PivotOffset = Vector2.Zero;
            drawings.Scale = new Vector2(inverse, inverse);
            _mapDrawingsSnapshots[drawings.GetInstanceId()] = snap;
        }
        if (map.GetNodeOrNull<Control>("%MapLegend") is { } legend)
        {
            Capture(legend, captureVisible: true);
            legend.PivotOffset = Vector2.Zero;
            legend.Scale = new Vector2(MapLegendScale, MapLegendScale);
            legend.Visible = _legendOpen;
            // The keyboard hotkey hint is meaningless on a phone and would be drawn at 1.48x over the title.
            if (legend.GetNodeOrNull<Control>("LegendHotkeyIcon") is { } hint && hint.Visible)
            {
                _mapHintVisibility.TryAdd(hint.GetInstanceId(), true);
                hint.Hide();
            }
        }
        EnsureLegendPill(map).Visible = true;

        // The draw, erase and clear buttons are 60 units (21 points) in a bar at the bottom left, with nothing beside it
        // up to the Share button: the whole bar grows from its bottom left corner until each button is 44 points.
        if (map.GetNodeOrNull<Control>("%DrawingTools") is { } tools)
        {
            Capture(tools, captureVisible: false);
            tools.PivotOffset = new Vector2(0f, tools.Size.Y);
            tools.Scale = new Vector2(DrawingToolsScale, DrawingToolsScale);
            PlaceMapTools(tools, canvasHeight);
        }
        Vector2 canvas = new(canvasWidth, canvasHeight);
        float clipTop = PortraitBands.HeaderBottom(capHeight);
        MapFrame? frame = EnsureMapFrame(map, canvas, clipTop, MapClipBottom(map, null, canvasHeight));
        if (frame != null)
        {
            PlaceMapBg(frame);
            if (frame.Map is { } theMap) UpdateMapGraph(theMap, frame, canvasWidth);
            UpdateMapFrame(map, frame, canvas, clipTop, MapClipBottom(map, frame, canvasHeight));
        }
    }

    /// <summary>
    /// Puts the untouched drawing bar's visible bottom <see cref="MapFooterBottomInset"/> above the canvas bottom, using the
    /// same canvas height the rest of the portrait layout reads (the native bar is positioned from the design height).
    /// Position.Y is the bar's unscaled origin and its pivot is the bottom-left corner, so the drawn bottom is
    /// Position.Y + Size.Y while the bar itself grows upwards.
    /// </summary>
    private static void PlaceMapTools(Control? tools, float canvasHeight)
    {
        if (tools is not { } bar || !GodotObject.IsInstanceValid(bar) || bar.Size.Y <= 0f) return;
        float y = canvasHeight - MapFooterBottomInset - bar.Size.Y;
        if (!Mathf.IsEqualApprox(bar.Position.Y, y)) bar.Position = new Vector2(bar.Position.X, y);
    }

    /// <summary>
    /// The canvas y the map's Back must stay above so nothing it draws reaches the drawing bar on the canvas bottom: the
    /// bar's drawn top less <see cref="MapToolGap"/>. Null until the bar has a size. The Back's placement and tween stay
    /// <see cref="PortraitNavigationPads"/>'s; it asks this only to lift the Back when the game's own footer gap would put
    /// its art into the bar.
    /// </summary>
    internal static float? MapBackFooterLimit(Control map)
    {
        Control? tools = map.GetNodeOrNull<Control>("%DrawingTools") ?? map.GetNodeOrNull<Control>("DrawingTools");
        if (tools is not { } bar || !GodotObject.IsInstanceValid(bar) || bar.Size.Y <= 0f || bar.Scale.Y <= 0f)
            return null;
        Transform2D transform = bar.GetGlobalTransformWithCanvas();
        Vector2 corner = transform * Vector2.Zero;
        Vector2 end = transform * bar.Size;
        return Math.Min(corner.Y, end.Y) - MapToolGap;
    }

    /// <summary>
    /// The parchment is part of the graph and scales with it; this only holds its own position, because the game rewrites
    /// <c>NMapBg.Position</c> on a viewport change from the offset it cached before the portrait layout widened it.
    /// </summary>
    private void PlaceMapBg(MapFrame frame)
    {
        if (frame.Bg is not { } bg || !GodotObject.IsInstanceValid(bg))
        {
            frame.Bg = frame.Map?.GetNodeOrNull<Control>("MapBg");
            bg = frame.Bg;
            if (bg == null || !GodotObject.IsInstanceValid(bg)) return;
            frame.BgBaseLeft = bg.Position.X;
            frame.PaperCenterX = bg.Position.X + bg.Size.X * 0.5f;
        }
        if (!Mathf.IsEqualApprox(bg.Position.X, frame.BgBaseLeft))
            bg.Position = new Vector2(frame.BgBaseLeft, bg.Position.Y);
    }

    /// <summary>
    /// One uniform transform for the whole graph: parchment, points, paths, strokes, marker and the points' hit pads. The
    /// sheet's 1260-wide body is scaled to the canvas (both torn edges stay visible) and translated so its centre sits on
    /// the canvas centre; every point column keeps its authored offset inside the paper. The points' own 1.25 icon scale
    /// is inside this transform too, and the hit pads are sized in canvas units (see <see cref="UpdateMapPointsPortrait"/>).
    /// </summary>
    private static void UpdateMapGraph(Control theMap, MapFrame frame, float canvasWidth)
    {
        float scale = PortraitBetweenMath.MapGraphScale(canvasWidth);
        if (!Mathf.IsEqualApprox(theMap.Scale.X, scale) || !Mathf.IsEqualApprox(theMap.Scale.Y, scale))
            theMap.Scale = new Vector2(scale, scale);
        if (theMap.PivotOffset != Vector2.Zero) theMap.PivotOffset = Vector2.Zero;
        frame.GraphOffsetX = canvasWidth * 0.5f - scale * frame.PaperCenterX;
        if (!Mathf.IsEqualApprox(theMap.Position.X, frame.GraphOffsetX))
            theMap.Position = new Vector2(frame.GraphOffsetX, theMap.Position.Y);
        if (!frame.PaperLogged)
        {
            frame.PaperLogged = true;
            GD.Print($"Portrait map paper: canvas={canvasWidth:F0} scale={scale:F3} centre={frame.PaperCenterX:F0} " +
                     $"offset={frame.GraphOffsetX:F1} paper={PortraitBetweenMath.MapPaperLeft(canvasWidth):F1}" +
                     $"..{PortraitBetweenMath.MapPaperRight(canvasWidth):F1}");
        }
    }

    /// <summary>
    /// The game centres its opening scroll on the current row using the authored (unscaled) point positions, so after the
    /// graph transform that row would sit <c>(1 - scale) × y</c> above the intended spot. On every open, once, put the
    /// reachable row's centre at 70 % of the chart instead.
    /// </summary>
    private static void CenterMapOnOpen(Control map, Control theMap, Control points, float canvasWidth,
        float clipTop, float clipBottom, MapScrollRange range, FieldInfo? targetField, float offsetX)
    {
        Transform2D inverse = theMap.GetGlobalTransform().AffineInverse();
        float sum = 0f;
        int count = 0;
        foreach (Control point in points.GetChildren().OfType<Control>())
        {
            if (point.IsQueuedForDeletion()) continue;
            if (point.GetType().GetProperty("IsEnabled", BindingFlags.Instance | BindingFlags.Public)?.GetValue(point) is not true)
                continue;
            sum += (inverse * point.GetGlobalRect().GetCenter()).Y;
            count++;
        }
        if (count == 0) return;
        float scale = PortraitBetweenMath.MapGraphScale(canvasWidth);
        float targetY = clipTop + (clipBottom - clipTop) * 0.70f - sum / count * scale;
        targetField?.SetValue(map, new Vector2(offsetX, PortraitBetweenMath.ClampMapScroll(targetY, range)));
    }

    /// <summary>
    /// The graph clips under the stone header and at the top of the drawing bar. The native Back ribbon stays in
    /// front of the parchment as a sibling of the chart, without reserving a separate row.
    /// </summary>
    private static float MapClipBottom(Control map, MapFrame? frame, float canvasHeight)
    {
        Control? tools = frame?.Tools;
        if (tools == null || !GodotObject.IsInstanceValid(tools))
        {
            tools = map.GetNodeOrNull<Control>("%DrawingTools");
            if (frame != null) frame.Tools = tools;
        }
        if (tools is { } bar && GodotObject.IsInstanceValid(bar) && bar.Size.Y > 0f && bar.Scale.Y > 0f)
        {
            return bar.Position.Y - bar.Size.Y * (bar.Scale.Y - 1f);
        }
        return canvasHeight - MapFooterBand;
    }

    private MapFrame? EnsureMapFrame(Control map, Vector2 canvas, float clipTop, float clipBottom)
    {
        ulong id = map.GetInstanceId();
        if (_mapFrames.TryGetValue(id, out MapFrame? known) && known.IsValid())
        {
            UpdateMapFrame(map, known, canvas, clipTop, clipBottom);
            return known;
        }
        Control? theMap = MapGraph(map);
        if (theMap == null) return null;
        if (known != null) TearDownMapFrame(map, known);

        int mapIndex = theMap.GetIndex();
        var shift = new Control { Name = MapShiftName, MouseFilter = Control.MouseFilterEnum.Pass };
        shift.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        shift.Size = canvas;
        shift.Position = new Vector2(0f, -clipTop);
        var clip = new Control
        {
            Name = MapViewportName,
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        clip.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        clip.Position = new Vector2(0f, clipTop);
        clip.Size = new Vector2(canvas.X, Math.Max(0f, clipBottom - clipTop));
        map.AddChild(clip);
        map.MoveChild(clip, mapIndex);
        clip.AddChild(shift);

        var frame = new MapFrame
        {
            Clip = clip,
            Shift = shift,
            Map = theMap,
            MapIndex = mapIndex,
            MapSnapshot = Capture(theMap, captureVisible: false),
        };
        Vector2 mapPosition = theMap.Position;
        // The shift cancels the clip's own offset, so the map container keeps the game's own position: the scroll target,
        // the drag start (which copies the container's position) and the game's lerp all keep their meaning.
        theMap.Reparent(shift, keepGlobalTransform: false);
        theMap.Position = mapPosition;
        _mapFrames[id] = frame;
        // The map is only updated while visible, so a close is otherwise invisible to the layout: this remembers that the
        // next visible frame is an open, when the scale-aware row centring has to run once.
        frame.VisibilityHandler = () =>
        {
            if (frame.Map is { } graph && GodotObject.IsInstanceValid(graph) && graph.IsVisibleInTree())
                frame.CenterPending = true;
        };
        map.VisibilityChanged += frame.VisibilityHandler;
        frame.CenterPending = map.IsVisibleInTree();

        frame.Points = theMap.GetNodeOrNull<Control>("Points");
        frame.Bg = theMap.GetNodeOrNull<Control>("MapBg");
        frame.BgBaseLeft = frame.Bg?.Position.X ?? -PortraitBetweenMath.MaxMapCentreShift;
        // The sheet is centred in its widened control, so the control's centre is the parchment body's centre in graph space.
        frame.PaperCenterX = frame.Bg is { } paper
            ? paper.Position.X + paper.Size.X * 0.5f
            : canvas.X * 0.5f;
        frame.Tools = map.GetNodeOrNull<Control>("%DrawingTools");
        frame.Legend = map.GetNodeOrNull<Control>("%MapLegend");
        MountMapShare(map, frame, canvas);
        UpdateMapGraph(theMap, frame, canvas.X);
        UpdateMapFrame(map, frame, canvas, clipTop, clipBottom);
        return frame;
    }

    private static void UpdateMapFrame(Control map, MapFrame frame, Vector2 canvas, float clipTop, float clipBottom)
    {
        if (frame.Clip is { } clip && GodotObject.IsInstanceValid(clip))
        {
            Vector2 size = new(canvas.X, Math.Max(0f, clipBottom - clipTop));
            if (clip.Position.Y != clipTop) clip.Position = new Vector2(0f, clipTop);
            if (clip.Size != size) clip.Size = size;
        }
        if (frame.Shift is { } shift && GodotObject.IsInstanceValid(shift))
        {
            if (shift.Position.Y != -clipTop) shift.Position = new Vector2(0f, -clipTop);
            if (shift.Size != canvas) shift.Size = canvas;
        }
    }

    /// <summary>Scales the native Share button through a mount: its own hover tween keeps driving the button's scale.</summary>
    private void MountMapShare(Control map, MapFrame frame, Vector2 canvas)
    {
        if (frame.Share is { } cached && GodotObject.IsInstanceValid(cached) && cached.GetParent() == frame.ShareMount)
        {
            PlaceMapShare(frame, canvas);
            return;
        }
        if (map.GetNodeOrNull<Control>("%ShareButton") is not { } share || share.GetParent() != map) return;
        int index = share.GetIndex();
        var mount = new Control { Name = MapShareMountName, MouseFilter = Control.MouseFilterEnum.Ignore };
        mount.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        map.AddChild(mount);
        map.MoveChild(mount, index);
        frame.Share = share;
        frame.ShareMount = mount;
        frame.ShareIndex = index;
        frame.ShareSnapshot = Capture(share, captureVisible: false);
        Vector2 size = share.Size;
        share.Reparent(mount, keepGlobalTransform: false);
        share.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        share.Position = Vector2.Zero;
        share.Size = size;
        PlaceMapShare(frame, canvas);
    }

    private static void PlaceMapShare(MapFrame frame, Vector2 canvas)
    {
        if (frame.Share is not { } share || frame.ShareMount is not { } mount ||
            !GodotObject.IsInstanceValid(share) || !GodotObject.IsInstanceValid(mount))
            return;
        float scale = PortraitBands.MinTarget / Math.Max(1f, share.Size.Y);
        if (mount.Scale.X != scale) mount.Scale = new Vector2(scale, scale);
        Vector2 drawn = share.Size * scale;
        Vector2 position = new(canvas.X - MapFooterInset - drawn.X, canvas.Y - MapFooterBottomInset - drawn.Y);
        if (mount.Position != position) mount.Position = position;
    }

    /// <summary>
    /// Places the Share confirmation toast through the button's own <c>_toastPosition</c>, so the game's show tween drives it
    /// from there. It sits centred just above the chart's clipped bottom, inside the safe frame; it is only moved while
    /// hidden so the native rise-and-fade animation is never fought.
    /// </summary>
    private void PlaceMapToast(Control map, MapFrame frame, Vector2 canvas, float clipBottom)
    {
        if (frame.Share is not { } share || !GodotObject.IsInstanceValid(share)) return;
        if (frame.Toast is not { } toast || !GodotObject.IsInstanceValid(toast))
        {
            toast = map.GetNodeOrNull<Control>("%ShareToast");
            if (toast == null) return;
            frame.Toast = toast;
            frame.ToastSnapshot ??= Capture(toast, captureVisible: false);
        }
        FieldInfo? field = share.GetType().GetField("_toastPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null) return;
        if (!frame.HasToastTarget && field.GetValue(share) is Vector2 original)
        {
            frame.ToastNativeTarget = original;
            frame.HasToastTarget = true;
        }
        Vector2 target = new((canvas.X - toast.Size.X) * 0.5f, clipBottom - MapToastGap - toast.Size.Y);
        if (field.GetValue(share) is Vector2 current && current != target) field.SetValue(share, target);
        if (toast.SelfModulate.A < 0.01f && toast.Position != target) toast.Position = target;
    }

    private void UpdateMapPortrait(Control map, Window root, Vector2 canvas)
    {
        float cap = PortraitLayout.ComputeCapHeight(root);
        float clipTop = PortraitBands.HeaderBottom(cap);
        MapFrame? probe = _mapFrames.TryGetValue(map.GetInstanceId(), out MapFrame? current) ? current : null;
        Control? tools = map.GetNodeOrNull<Control>("%DrawingTools");
        PlaceMapTools(tools, canvas.Y);
        float clipBottom = MapClipBottom(map, probe, canvas.Y);
        MapFrame? frame = EnsureMapFrame(map, canvas, clipTop, clipBottom);
        if (frame != null && frame.Tools == null)
            frame.Tools = tools;
        PlaceMapTools(frame?.Tools ?? tools, canvas.Y);
        clipBottom = MapClipBottom(map, frame, canvas.Y);
        // The game's scroll limits (-600..1800) were made for a 1080-tall canvas; keep its target inside the portrait range.
        MapScrollRange range = MapScrollLimits(map, root, clipBottom, frame?.Map);
        Type type = map.GetType();
        bool dragging = type.GetField("_isDragging", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(map) is true;
        FieldInfo? targetField = type.GetField("_targetDragPos", BindingFlags.Instance | BindingFlags.NonPublic);
        Control? theMap = frame?.Map ?? MapGraph(map);
        // The game keeps the map container at x = 0 and writes that back whenever it opens or scrolls the map; it follows this
        // target on both axes, so the uniform graph transform's x offset is held in the target as well.
        if (frame != null && theMap != null) UpdateMapGraph(theMap, frame, canvas.X);
        float shiftX = theMap?.Position.X ?? 0f;
        if (targetField?.GetValue(map) is Vector2 target)
        {
            float targetY = target.Y;
            if (!dragging)
            {
                float clamped = PortraitBetweenMath.ClampMapScroll(target.Y, range);
                if (!Mathf.IsEqualApprox(clamped, target.Y))
                    targetY = Mathf.Lerp(target.Y, clamped, (float)Math.Min(1.0, map.GetProcessDeltaTime() * 12.0));
            }
            if (targetY != target.Y || target.X != shiftX)
                targetField.SetValue(map, new Vector2(shiftX, targetY));
        }
        if (frame is { CenterPending: true } transition && !dragging && theMap != null &&
            transition.Points is { } pointLayer)
        {
            CenterMapOnOpen(map, theMap, pointLayer, canvas.X, clipTop, clipBottom, range, targetField, shiftX);
            transition.CenterPending = false;
        }
        if (frame != null) PlaceMapBg(frame);
        UpdateMapPointsPortrait(map, frame);
        if (frame is { } stack)
        {
            // The Share button sets its own scale whenever it is touched, so its mount keeps the picture and the 44 points.
            if (stack.Share is { } share && GodotObject.IsInstanceValid(share) &&
                share.GetParent() == stack.ShareMount && share.IsVisibleInTree())
                PlaceMapShare(stack, canvas);
            PlaceMapToast(map, stack, canvas, clipBottom);
        }

        (float bandTop, _) = VisibleBand(root);
        Button pill = frame?.Pill ?? EnsureLegendPill(map);
        if (frame != null) frame.Pill = pill;
        pill.Visible = true;
        pill.Position = new Vector2(canvas.X - pill.Size.X - 20f, bandTop - 40f);
        // Both the pill and the open legend overlay the chart, whose clip and point hit pads are later siblings; keep them
        // last in the map's child order so a press goes to the toggle, not to a node under it (9:16 has no cap, so the
        // pill sits over the top rows there).
        Control? legend = frame?.Legend;
        if (legend == null || !GodotObject.IsInstanceValid(legend))
        {
            legend = map.GetNodeOrNull<Control>("%MapLegend");
            if (frame != null) frame.Legend = legend;
        }
        if (legend != null)
        {
            legend.Visible = _legendOpen;
            legend.Scale = new Vector2(MapLegendScale, MapLegendScale);
            legend.Position = new Vector2(canvas.X - legend.Size.X * MapLegendScale - MapLegendInset,
                pill.Position.Y + pill.Size.Y + 13f);
            if (_legendOpen && legend.GetIndex() != map.GetChildCount() - 1)
                map.MoveChild(legend, map.GetChildCount() - 1);
        }
        if (pill.GetIndex() != map.GetChildCount() - 1) map.MoveChild(pill, map.GetChildCount() - 1);
    }

    /// <summary>
    /// Room points for a finger, done once for each act's points. A room point is 56 units (19 points) with a picture of
    /// 96. Each point is drawn a quarter larger around its own centre, which leaves the centres and the paths where they
    /// were, and gets a square hit pad of 44 points — the pad target is in canvas units, and <see cref="EnsureHitPad"/>
    /// divides by the node's global scale, so the graph's uniform scale never shrinks it. The native columns are as close as
    /// 119 units a row, so after the reframe two pads can overlap by a few units; the drawn icons stay separate and each
    /// node's centre is unambiguous. The point's own Size and PivotOffset are never touched: NNormalMapPoint.ShowCircleVfx
    /// adds the current pivot to a visited circle's position, so leaving it alone keeps existing circles centred.
    /// </summary>
    private void UpdateMapPointsPortrait(Control map, MapFrame? frame)
    {
        Control? points = frame?.Points;
        if (points == null || !GodotObject.IsInstanceValid(points))
        {
            points = MapGraph(map)?.GetNodeOrNull<Control>("Points");
            if (points == null) return;
            if (frame != null) frame.Points = points;
        }
        int count = points.GetChildCount();
        ulong first = count > 0 ? points.GetChild(0).GetInstanceId() : 0;
        ulong id = map.GetInstanceId();
        if (_mapPointLayouts.TryGetValue(id, out (ulong First, int Count) known) && known.First == first && known.Count == count)
            return;
        _mapPointLayouts[id] = (first, count);

        var rooms = new List<Control>();
        foreach (Control point in points.GetChildren().OfType<Control>())
        {
            if (point.IsQueuedForDeletion()) continue;
            if (point.GetType().Name == "NNormalMapPoint") rooms.Add(point);
        }
        foreach (Control point in rooms)
        {
            point.Scale = new Vector2(MapPointScale, MapPointScale);
            _scaledMapPoints.Add(point.GetInstanceId());
            // EnsureHitPad's target is in canvas units and divides by the node's global scale, so the 127-unit pad stays
            // 127 units after the graph transform (the icon picture itself is scaled with the graph).
            EnsureHitPad(point, PortraitBands.MinTarget, PortraitBands.MinTarget);
        }
    }

    private Button EnsureLegendPill(Control map)
    {
        if (map.GetNodeOrNull<Button>(LegendPillName) is { } existing) return existing;
        var style = new StyleBoxFlat
        {
            BgColor = PillFillColor,
            BorderColor = PillBorderColor,
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 63, CornerRadiusTopRight = 63, CornerRadiusBottomLeft = 63, CornerRadiusBottomRight = 63,
        };
        var pill = new Button
        {
            Name = LegendPillName,
            CustomMinimumSize = new Vector2(LegendPillWidth, PortraitBands.MinTarget),
            Size = new Vector2(LegendPillWidth, PortraitBands.MinTarget),
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        foreach (string state in new[] { "normal", "hover", "pressed", "focus", "disabled" })
            pill.AddThemeStyleboxOverride(state, style);
        string header = map.GetNodeOrNull<Label>("MapLegend/Header")?.Text ?? "Legend";
        var label = new Label
        {
            Name = "PillLabel",
            Text = header + " ?",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        label.AddThemeColorOverride("font_color", PillTextColor);
        label.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.65f));
        label.AddThemeConstantOverride("outline_size", 8);
        label.AddThemeFontSizeOverride("font_size", LegendPillFontSize);
        if (ResourceLoader.Exists("res://themes/kreon_bold_glyph_space_one.tres") &&
            ResourceLoader.Load<Font>("res://themes/kreon_bold_glyph_space_one.tres") is { } kreon)
            label.AddThemeFontOverride("font", kreon);
        pill.AddChild(label);
        // Toggle on the press: on a phone the release can be taken by a chart drag or a point under the finger, and the
        // legend must still open. Accepting the press keeps it away from the chart below.
        pill.GuiInput += input =>
        {
            if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            {
                _legendOpen = !_legendOpen;
                pill.AcceptEvent();
            }
        };
        map.AddChild(pill);
        return pill;
    }

    /// <summary>Back to the game's own map: remove the clip, shift, Share mount and point sizing.</summary>
    private void RestoreMapLandscape()
    {
        foreach ((ulong id, MapFrame frame) in _mapFrames.ToList())
        {
            if (GodotObject.InstanceFromId(id) is Control map && GodotObject.IsInstanceValid(map))
                TearDownMapFrame(map, frame);
            else
                TearDownMapFrame(null, frame);
        }
        _mapFrames.Clear();

        foreach ((ulong id, TextureRect.StretchModeEnum mode) in _stretchModes)
            if (GodotObject.InstanceFromId(id) is TextureRect rect && GodotObject.IsInstanceValid(rect))
                rect.StretchMode = mode;
        _stretchModes.Clear();
        foreach ((ulong id, bool visible) in _mapHintVisibility)
            if (GodotObject.InstanceFromId(id) is CanvasItem hint && GodotObject.IsInstanceValid(hint))
                hint.Visible = visible;
        _mapHintVisibility.Clear();
        foreach ((ulong id, PortraitControlSnapshot snap) in _mapBgSnapshots)
            if (GodotObject.InstanceFromId(id) is Control bg && GodotObject.IsInstanceValid(bg))
                snap.Restore(bg);
        _mapBgSnapshots.Clear();
        foreach ((ulong id, PortraitControlSnapshot snap) in _mapDrawingsSnapshots)
            if (GodotObject.InstanceFromId(id) is Control drawings && GodotObject.IsInstanceValid(drawings))
                snap.Restore(drawings);
        _mapDrawingsSnapshots.Clear();

        foreach (ulong id in _maps)
        {
            if (GodotObject.InstanceFromId(id) is not Control map || !GodotObject.IsInstanceValid(map)) continue;
            if (map.GetNodeOrNull<CanvasItem>(LegendPillName) is { } pill) pill.Visible = false;
            if (map.GetType().GetField("_targetDragPos", BindingFlags.Instance | BindingFlags.NonPublic) is { } targetField &&
                targetField.GetValue(map) is Vector2 target)
                targetField.SetValue(map, new Vector2(0f, target.Y));
            if (map.GetNodeOrNull<Control>("TheMap") is { } theMap)
                theMap.Position = new Vector2(0f, theMap.Position.Y);
            if (map.GetNodeOrNull<Control>("%MapLegend") is { } legend)
            {
                legend.Visible = true;
                legend.Position = new Vector2(map.Size.X * 0.8f, legend.Position.Y);
            }
        }

        foreach (ulong id in _scaledMapPoints)
            if (GodotObject.InstanceFromId(id) is Control point && GodotObject.IsInstanceValid(point))
                point.Scale = Vector2.One;
        _scaledMapPoints.Clear();
        _mapPointLayouts.Clear();
    }

    private static void TearDownMapFrame(Control? map, MapFrame frame)
    {
        if (frame.VisibilityHandler is { } handler)
        {
            if (map != null && GodotObject.IsInstanceValid(map)) map.VisibilityChanged -= handler;
            frame.VisibilityHandler = null;
        }
        if (frame.Toast is { } toast && GodotObject.IsInstanceValid(toast))
        {
            frame.ToastSnapshot?.Restore(toast);
            if (frame.HasToastTarget && frame.Share is { } toastShare && GodotObject.IsInstanceValid(toastShare) &&
                toastShare.GetType().GetField("_toastPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is { } toastField)
                toastField.SetValue(toastShare, frame.ToastNativeTarget);
        }
        if (frame.Share is { } share && GodotObject.IsInstanceValid(share))
        {
            if (frame.ShareMount is { } mount && GodotObject.IsInstanceValid(mount) && share.GetParent() == mount)
            {
                Node target = map ?? mount.GetParent();
                share.Reparent(target, keepGlobalTransform: false);
                frame.ShareSnapshot?.Restore(share);
                if (map != null && GodotObject.IsInstanceValid(map))
                    map.MoveChild(share, Math.Min(frame.ShareIndex, map.GetChildCount() - 1));
            }
        }
        if (frame.ShareMount is { } shareMount && GodotObject.IsInstanceValid(shareMount))
        {
            shareMount.GetParent()?.RemoveChild(shareMount);
            shareMount.QueueFree();
        }
        if (frame.Map is { } theMap && GodotObject.IsInstanceValid(theMap) && frame.Shift is { } shift &&
            GodotObject.IsInstanceValid(shift) && theMap.GetParent() == shift)
        {
            if (map != null && GodotObject.IsInstanceValid(map))
            {
                // Keep the scroll exactly where the last portrait frame showed it; native then continues from there.
                Vector2 shown = theMap.Position;
                theMap.Reparent(map, keepGlobalTransform: false);
                frame.MapSnapshot?.Restore(theMap);
                theMap.Position = new Vector2(0f, shown.Y);
                map.MoveChild(theMap, Math.Min(frame.MapIndex, map.GetChildCount() - 1));
            }
        }
        if (frame.Clip is { } clip && GodotObject.IsInstanceValid(clip))
        {
            clip.GetParent()?.RemoveChild(clip);
            clip.QueueFree();
        }
        frame.Clip = null;
        frame.Shift = null;
        frame.ShareMount = null;
    }

    private void PruneMapInstances()
    {
        _maps.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in _mapFrames.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _mapFrames.Remove(id);
        foreach (ulong id in _mapBgSnapshots.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _mapBgSnapshots.Remove(id);
        foreach (ulong id in _mapDrawingsSnapshots.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _mapDrawingsSnapshots.Remove(id);
        foreach (ulong id in _stretchModes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _stretchModes.Remove(id);
        foreach (ulong id in _mapHintVisibility.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _mapHintVisibility.Remove(id);
        _scaledMapPoints.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in _mapPointLayouts.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _mapPointLayouts.Remove(id);
    }

    /// <summary>Top below the relic strip and fallback bottom above the drawing tools.</summary>
    public static (float Top, float Bottom) VisibleBand(Window root)
    {
        float canvasHeight = Math.Max(root.GetVisibleRect().Size.Y, SafeAreaCanvas.MinPortraitHeight);
        float cap = PortraitLayout.ComputeCapHeight(root);
        return (cap + PortraitBands.TopBarHeight + 200f, canvasHeight - MapFooterBand);
    }

    public static MapScrollRange MapScrollLimits(Control map, Window root)
    {
        float canvasHeight = Math.Max(root.GetVisibleRect().Size.Y, SafeAreaCanvas.MinPortraitHeight);
        return MapScrollLimits(map, root,
            MapClipBottom(map, null, canvasHeight),
            map.GetNodeOrNull<Control>("TheMap") ?? map.FindChild("TheMap", true, false) as Control);
    }

    private static MapScrollRange MapScrollLimits(Control map, Window root, float visibleBottom, Control? graph)
    {
        (float top, _) = VisibleBand(root);
        Type type = map.GetType();
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Control? theMap = graph ?? map.GetNodeOrNull<Control>("TheMap") ?? map.FindChild("TheMap", true, false) as Control;
        if (theMap == null ||
            type.GetField("_bossPointNode", Flags)?.GetValue(map) is not Control boss ||
            type.GetField("_startingPointNode", Flags)?.GetValue(map) is not Control start)
            return new MapScrollRange(-600f, 1800f);
        Control topBoss = type.GetField("_secondBossPointNode", Flags)?.GetValue(map) is Control second && second.IsVisibleInTree() &&
            second.GlobalPosition.Y < boss.GlobalPosition.Y ? second : boss;
        float bossTop = topBoss.GetGlobalRect().Position.Y - theMap.GlobalPosition.Y;
        float startBottom = start.GetGlobalRect().End.Y - theMap.GlobalPosition.Y;
        return PortraitBetweenMath.MapScrollRange(bossTop - 20f, startBottom + 20f, top, visibleBottom);
    }
}
