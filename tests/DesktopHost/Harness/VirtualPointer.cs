using System;
using System.Threading.Tasks;
using Godot;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// Injects pointer input into the live game. Mouse mode uses real mouse events;
/// touch mode uses InputEventScreenTouch/ScreenDrag with Godot's touch-to-mouse
/// emulation enabled, matching the mod's normal phone/tablet path.
/// </summary>
internal sealed class VirtualPointer
{
    private readonly SceneTree _tree;
    private readonly PointerMode _mode;
    private Vector2 _position;

    public VirtualPointer(SceneTree tree, PointerMode mode)
    {
        _tree = tree;
        _mode = mode;
        Input.EmulateMouseFromTouch = true;
    }

    public PointerMode Mode => _mode;

    public Vector2 Position => _position;

    private Viewport Viewport => _tree.Root.GetViewport();

    public Vector2 ToScreenPosition(Vector2 position) => Viewport.GetScreenTransform() * position;

    public async Task TapAsync(Vector2 position)
    {
        await PressAsync(position);
        await WaitFramesAsync(2);
        await ReleaseAsync(position);
        await WaitFramesAsync(3);
    }

    public async Task LongPressAsync(Vector2 position, int frames = 45)
    {
        await PressAsync(position);
        await WaitFramesAsync(frames);
        await ReleaseAsync(position);
        await WaitFramesAsync(3);
    }

    public async Task DragAsync(Vector2 from, Vector2 to, int steps = 12)
    {
        await PressAsync(from);
        await WaitFramesAsync(2);

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            await MoveAsync(from.Lerp(to, t), pressed: true);
            await WaitFramesAsync(1);
        }

        await ReleaseAsync(to);
        await WaitFramesAsync(4);
    }

    // Some X11 window managers place a warp against the window-manager frame origin instead of the client area, leaving a
    // constant offset (the frame decoration) between the requested canvas point and the point the engine reports. The offset
    // is learned only from a read that confirms the correction landed, and is applied to later warps so the engine's pointer
    // is already right in the frame the injected event is processed. The offset is in canvas units.
    /// <summary>
    /// Set by the launcher host's pointer shim: its game paths read the OS cursor, so they need the canvas warp and its
    /// learned offset.
    /// </summary>
    internal static bool UseViewportWarp { get; set; }

    /// <summary>
    /// Skips the OS cursor warp while set. A phone sends only ScreenTouch/ScreenDrag; the harness moves the desktop cursor
    /// so the game paths that read it follow the finger, but on X11 each warp also arrives as a native mouse motion, and the
    /// map's freehand drawing records every motion as another line point. A gesture whose points must match its endpoints
    /// suppresses the warp so the game sees only the stream a phone sends.
    /// </summary>
    internal static bool SuppressOsWarp { get; set; }

    private static Vector2 _warpError;
    private static bool _warpErrorVerified;
    private static long _warpGeneration;

    // A warp whose read is farther than this from the requested canvas point did not take (the window lost pointer
    // focus, or another window owns the cursor). Retry the same target; never add that read to the correction. The
    // bound is in window pixels, scaled by the canvas: a WM frame/title bar is ~53-100 window px, while the stray-cursor
    // reads this guards against are hundreds of pixels. A fixed canvas-unit bound wrongly rejected a 53-px frame offset
    // at the original 1680-wide reference canvas (167 canvas units) while accepting it at 720 wide (125 units).
    private const float MaxSettleCorrectionPixels = 128f;

    /// <summary>
    /// Best-effort synchronous warp. Before a correction has been verified (the first press, or a window that has just been
    /// moved/mapped) no learned offset is applied, so a stale or bogus read cannot fling the cursor across the screen.
    /// </summary>
    internal static void WarpToCanvas(Viewport viewport, Vector2 canvasPoint) =>
        viewport.WarpMouse(canvasPoint + (_warpErrorVerified ? _warpError : Vector2.Zero));

    /// <summary>
    /// Starts a settled warp for an injected event. Once the offset is verified a later event needs no settling at all:
    /// the warp is exact synchronously, which keeps drag timing identical to the pre-correction harness. Until then it
    /// supersedes any earlier pending settle (a newer touch wins), waits one frame after each warp, and commits the offset
    /// only when the engine's own position is within one window pixel of the requested canvas point.
    /// </summary>
    internal static void RequestSettledWarp(SceneTree tree, Viewport viewport, Vector2 canvasPoint)
    {
        if (SuppressOsWarp) return;
        if (_warpErrorVerified)
        {
            viewport.WarpMouse(canvasPoint + _warpError);
            return;
        }
        long generation = ++_warpGeneration;
        _ = SettleWarpAsync(tree, viewport, canvasPoint, generation);
    }

    private static async Task SettleWarpAsync(SceneTree tree, Viewport viewport, Vector2 canvasPoint, long generation)
    {
        try
        {
            float quantum = CanvasPerWindowPixel(viewport);
            Vector2 target = canvasPoint;
            Vector2 bestTarget = target;
            float bestError = float.PositiveInfinity;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                if (SuppressOsWarp) return;
                viewport.WarpMouse(target);
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                if (SuppressOsWarp || generation != _warpGeneration || !GodotObject.IsInstanceValid(viewport)) return;
                Vector2 error = canvasPoint - viewport.GetMousePosition();
                float errorSquared = error.LengthSquared();
                if (errorSquared <= 0.25f)
                {
                    _warpError = target - canvasPoint;
                    _warpErrorVerified = true;
                    return;
                }
                if (errorSquared < bestError)
                {
                    bestError = errorSquared;
                    bestTarget = target;
                }
                if (error.Length() > MaxSettleCorrectionPixels * quantum)
                {
                    // The warp did not take (the window lost pointer focus, or another window owns the cursor). Retry
                    // the same target next attempt: accumulating this read would chase that cursor across the screen.
                    continue;
                }
                target += error;
            }
            if (bestError <= quantum * 0.6f * (quantum * 0.6f))
            {
                _warpError = bestTarget - canvasPoint;
                _warpErrorVerified = true;
            }
        }
        catch (Exception)
        {
            // Best-effort pointer alignment; the probe reports any residual offset.
        }
    }

    /// <summary>
    /// The pointer warp for a harness move. The launcher host installs the pointer shim because its game paths read the OS
    /// cursor; there the canvas warp with its learned offset is used.
    /// </summary>
    private async Task WarpForMoveAsync(Vector2 canvasPoint)
    {
        if (SuppressOsWarp) return;
        if (UseViewportWarp)
        {
            await WarpToCanvasAsync(canvasPoint);
            return;
        }
        Input.WarpMouse(ToScreenPosition(canvasPoint));
    }

    /// <summary>
    /// A warp whose first (unverified) gesture waits a frame before each read; once the offset is verified every move is a
    /// single synchronous warp with no extra frame, so the harness does not slow down drags or change their timing. The
    /// commit tolerance is one window pixel, the finest the platform warp can place the cursor.
    /// </summary>
    private async Task WarpToCanvasAsync(Vector2 canvasPoint)
    {
        if (SuppressOsWarp) return;
        if (_warpErrorVerified)
        {
            Viewport.WarpMouse(canvasPoint + _warpError);
            return;
        }
        float quantum = CanvasPerWindowPixel(Viewport);
        Vector2 target = canvasPoint;
        Vector2 bestTarget = target;
        float bestError = float.PositiveInfinity;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            Viewport.WarpMouse(target);
            await WaitFramesAsync(1);
            Vector2 error = canvasPoint - Viewport.GetMousePosition();
            float errorSquared = error.LengthSquared();
            if (errorSquared <= 0.25f)
            {
                _warpError = target - canvasPoint;
                _warpErrorVerified = true;
                return;
            }
            if (errorSquared < bestError)
            {
                bestError = errorSquared;
                bestTarget = target;
            }
            if (error.Length() > MaxSettleCorrectionPixels * quantum)
            {
                // A read this far from the requested point is not the warp's frame offset; retry the same target
                // instead of adding the error (see SettleWarpAsync).
                continue;
            }
            target += error;
        }
        if (bestError <= quantum * 0.6f * (quantum * 0.6f))
        {
            _warpError = bestTarget - canvasPoint;
            _warpErrorVerified = true;
        }
    }

    /// <summary>Canvas units per window pixel; the warp lands on whole window pixels, so this bounds its precision.</summary>
    private static float CanvasPerWindowPixel(Viewport viewport)
    {
        Vector2I window = DisplayServer.WindowGetSize();
        Vector2 canvas = viewport.GetVisibleRect().Size;
        if (window.X <= 0 || window.Y <= 0) return 1f;
        return MathF.Max(0.01f, MathF.Max(canvas.X / window.X, canvas.Y / window.Y));
    }

    /// <summary>
    /// The finest pointer agreement the platform can give: the warp lands on whole window pixels, so a requested
    /// fractional canvas point can be off by up to one pixel per axis (its diagonal is about 1.5 pixels).
    /// </summary>
    public float PointerTolerance => MathF.Max(2f, CanvasPerWindowPixel(Viewport) * 1.5f);

    /// <summary>
    /// Re-asserts the OS cursor warp for an injected canvas point and waits (bounded) for the engine's pointer to agree.
    /// A warp is a global side effect that can be dropped while the window loses pointer focus (XWayland) or while a
    /// settle is still running, so the press/drag checks re-warp instead of sampling two frames after the event. Only
    /// the cursor moves; no input event is injected. Returns the final engine position.
    /// </summary>
    public async Task<Vector2> AlignPointerAsync(Vector2 canvasPoint, int frames = 4)
    {
        if (SuppressOsWarp) return Viewport.GetMousePosition();
        Vector2 mouse = Viewport.GetMousePosition();
        for (int i = 0; i < frames && mouse.DistanceTo(canvasPoint) > PointerTolerance; i++)
        {
            await WarpForMoveAsync(canvasPoint);
            await WaitFramesAsync(1);
            mouse = Viewport.GetMousePosition();
        }
        return mouse;
    }

    // ---- window guard ------------------------------------------------------------------------------------------------

    private const long GuardStaleFrames = 90;
    private const float GuardTolerance = 4f;
    private static SceneTree? _guardTree;
    private static TestReport? _guardReport;
    private static long _guardLastFrame = long.MinValue;
    private static bool _guardBlocked;

    /// <summary>
    /// Registers the scenario's window/pointer guard. Called once at the host's scenario entry; the state is static so
    /// every VirtualPointer instance (the entries create several) shares it.
    /// </summary>
    internal static void RegisterWindowGuard(SceneTree tree, TestReport report)
    {
        _guardTree = tree;
        _guardReport = report;
        _guardLastFrame = long.MinValue;
        _guardBlocked = false;
    }

    /// <summary>
    /// Detection-first obstruction check. The host is Wayland with the game as an X11/XWayland client, so the compositor
    /// will not let the game raise or focus itself above native windows: the raise/focus calls below are best effort and
    /// the gate is whether the OS pointer can reach a known canvas point. An obscured window makes the warp and the
    /// readback disagree; report one clear step and abort the scenario's touch steps instead of failing each one.
    /// </summary>
    internal static async Task EnsureWindowUnobscuredAsync(bool initial, Vector2? resumeTo = null)
    {
        SceneTree? tree = _guardTree;
        TestReport? report = _guardReport;
        if (tree is null || report is null) return;
        if (_guardBlocked) throw new WindowObscuredException("the window is already known to be obscured");
        Viewport viewport = tree.Root.GetViewport();

        // A warp issued while the window manager is still moving the window resolves against a stale origin.
        Vector2I lastSize = new(int.MinValue, int.MinValue);
        Vector2I lastPosition = new(int.MinValue, int.MinValue);
        int stable = 0;
        double placementStart = Time.GetTicksMsec();
        while (Time.GetTicksMsec() - placementStart < 1000.0)
        {
            Vector2I size = DisplayServer.WindowGetSize();
            Vector2I position = DisplayServer.WindowGetPosition();
            if (size == lastSize && position == lastPosition && size.X > 0 && size.Y > 0)
            {
                if (++stable >= 2) break;
            }
            else
            {
                stable = 0;
                lastSize = size;
                lastPosition = position;
            }
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }

        // Best effort (Wayland refuses to raise an X11 client above native windows); never the gate.
        try { DisplayServer.WindowMoveToForeground(); } catch (Exception) { /* unsupported on some display servers */ }
        try { tree.Root.GrabFocus(); } catch (Exception) { /* same */ }
        for (int i = 0; i < 2; i++)
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

        Vector2 point = ParkPoint(viewport);
        // Viewport.WarpMouse and Viewport.GetMousePosition share one coordinate space. Input.WarpMouse is relative to the
        // window-manager frame (its title bar), which lands ~53 window pixels above the client point and reads back as a
        // false "obstructed" (Y short by a varied canvas amount, X right).
        RequestSettledWarp(tree, viewport, point);
        Vector2 mouse = viewport.GetMousePosition();
        for (int i = 0; i < 10 && mouse.DistanceTo(point) > GuardTolerance; i++)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            mouse = viewport.GetMousePosition();
        }

        // Stop any pending verification settle and put the pointer on the resume point (the next press) synchronously:
        // no drawn frame is left with the pointer at the verification point over the header/stage.
        _warpGeneration++;
        _guardLastFrame = (long)Engine.GetProcessFrames();
        float delta = mouse.DistanceTo(point);
        WarpDirect(viewport, resumeTo ?? point);

        if (delta > GuardTolerance)
        {
            _guardBlocked = true;
            string detail =
                "another window covers the game window or the pointer is not reaching it — uncover the game window and rerun; " +
                $"warp={point} read={mouse} delta={delta:F1} " +
                $"window={DisplayServer.WindowGetSize()}@{DisplayServer.WindowGetPosition()} " +
                $"focused={DisplayServer.WindowIsFocused()}";
            report.Fail($"{report.Scenario}_window_unobscured", detail);
            Log.Error($"[TEST] window guard failed: {detail}");
            throw new WindowObscuredException(detail);
        }

        if (initial)
        {
            report.Pass($"{report.Scenario}_window_unobscured",
                $"window raised/focused (best effort); warp={point} read={mouse} delta={delta:F1} " +
                $"window={DisplayServer.WindowGetSize()}@{DisplayServer.WindowGetPosition()} " +
                $"focused={DisplayServer.WindowIsFocused()} quantum={CanvasPerWindowPixel(viewport):F2} " +
                $"warpError={(_warpErrorVerified ? $"{_warpError.Length():F1}u" : "unverified")}");
        }
        else
        {
            Log.Info($"[TEST] window guard re-check: warp={point} read={mouse} delta={delta:F1}");
        }
    }

    /// <summary>
    /// Press hook: re-check only when the window lost focus or the last good check is old (90 frames, ~1.5 s at 60 fps),
    /// so each touch pass group is checked without warping before every gesture. On success the last warp is the press
    /// point, not the verification point.
    /// </summary>
    private static async Task GuardBeforePressAsync(Vector2 pressPoint)
    {
        if (_guardTree is null || _guardReport is null) return;
        if (_guardBlocked) throw new WindowObscuredException("the window is already known to be obscured");
        bool stale = (long)Engine.GetProcessFrames() - _guardLastFrame >= GuardStaleFrames;
        if (!stale && DisplayServer.WindowIsFocused()) return;
        await EnsureWindowUnobscuredAsync(initial: false, resumeTo: pressPoint);
    }

    /// <summary>The harness's normal idle pointer point: whole window pixels at both shapes and no hoverable content.</summary>
    private static Vector2 ParkPoint(Viewport viewport) =>
        new(24f, viewport.GetVisibleRect().Size.Y * 0.2f);

    private static void WarpDirect(Viewport viewport, Vector2 canvasPoint)
    {
        if (UseViewportWarp)
            viewport.WarpMouse(canvasPoint + (_warpErrorVerified ? _warpError : Vector2.Zero));
        else
            Input.WarpMouse(viewport.GetScreenTransform() * canvasPoint);
    }

    public async Task PressAsync(Vector2 position)
    {
        await GuardBeforePressAsync(position);
        if (_mode == PointerMode.Mouse)
        {
            await MoveAsync(position, pressed: false);
            if (UseViewportWarp) await AlignPointerAsync(position, frames: 10);
            SendMouseButton(position, pressed: true);
        }
        else
        {
            // The desktop still needs cursor alignment for native hover. Don't inject a separate mouse move
            // before a screen touch; explicit hover probes can call MoveAsync themselves.
            // The engine re-evaluates its hover from the real cursor whenever a mouse event enters the viewport
            // (Viewport::push_input → _update_mouse_over → DisplayServer.mouse_get_position), and an emulated touch
            // button is a mouse event: under Wine SetCursorPos lands asynchronously, so a press sent while the readback
            // still shows the old point sends MouseExited to the hovered target and drops the press (NClickableControl
            // needs IsFocused). Wait for the engine's own readback to agree before sending the touch.
            if (UseViewportWarp) await AlignPointerAsync(position, frames: 10);
            else await WarpForMoveAsync(position);
            _position = position;
            await WaitFramesAsync(1);
            SendTouch(position, pressed: true);
        }

        await WaitFramesAsync(2);
    }

    /// <summary>
    /// A touch as a phone delivers it after an earlier touch lifted: the emulated mouse is still where that touch lifted, so
    /// the engine's hover pass on the press (<c>Viewport::push_input → _update_mouse_over</c>, which reads the cursor) sees
    /// the old point before anything moves it. <see cref="PressAsync"/> warps the desktop cursor to the new point first,
    /// which no phone does. Touch mode only; the mouse mode falls back to <see cref="PressAsync"/>.
    /// </summary>
    public async Task PressWhereLastLiftedAsync(Vector2 position)
    {
        if (_mode == PointerMode.Mouse)
        {
            await PressAsync(position);
            return;
        }
        await GuardBeforePressAsync(position);
        _position = position;
        SendTouch(position, pressed: true);
        await WaitFramesAsync(2);
    }

    /// <summary>
    /// A deliberate mouse hover: moves the real cursor and then claims the hover with one exact event at the requested point.
    /// The warp is still needed (the game reads the cursor), but its own native motion can report a point up to a
    /// window-manager title bar away from the requested one: under Wine, <c>Viewport.WarpMouse</c> can land against the
    /// frame origin while <c>Viewport.GetMousePosition</c> (the value the offset learning trusts) agrees with it. At
    /// 720x1280 that is 53 window pixels (79.5 canvas units): asking for Continue hovered Abandon, Settings hovered Mod
    /// Configuration, Quit hovered nothing. Waiting for the warp's native motion to be pumped first and then pushing one
    /// exact <see cref="InputEventMouseMotion"/> makes that event the last hover evaluation (the engine only re-evaluates
    /// the hover for mouse motion), so the hover lands on the requested control regardless of where the warp landed. This
    /// keeps the requested control authoritative instead of the real cursor. Presses keep <see cref="MoveAsync"/> (plus the
    /// readback settle in <see cref="PressAsync"/>) and the Touch mode keeps the existing injected-motion hover.
    /// </summary>
    public async Task HoverAsync(Vector2 position)
    {
        if (_mode != PointerMode.Mouse)
        {
            await MoveAsync(position, pressed: false);
            return;
        }
        _position = position;
        await WarpToCanvasAsync(position);
        for (int i = 0; i < 3 && Viewport.GetMousePosition().DistanceTo(position) > PointerTolerance; i++)
        {
            await WaitFramesAsync(1);
            await WarpToCanvasAsync(position);
        }
        // Drain the warp's own native motion (it may hover a point a title bar away) before claiming the hover.
        await WaitFramesAsync(1);
        Push(new InputEventMouseMotion
        {
            Position = position,
            GlobalPosition = ToScreenPosition(position),
            Relative = Vector2.Zero,
            ButtonMask = 0,
        });
        await WaitFramesAsync(1);
    }

    /// <summary>Per-second velocity of an injected motion, so the game's mouse-input detector sees a moving mouse.</summary>
    private static Vector2 MotionVelocity(Vector2 relative)
    {
        float fps = (float)Engine.GetFramesPerSecond();
        return relative * (fps > 1f ? fps : 60f);
    }

    public async Task MoveAsync(Vector2 position, bool pressed)
    {
        Vector2 relative = position - _position;
        _position = position;

        if (_mode == PointerMode.Mouse)
        {
            await WarpForMoveAsync(position);
            Push(new InputEventMouseMotion
            {
                Position = position,
                GlobalPosition = ToScreenPosition(position),
                Relative = relative,
                Velocity = MotionVelocity(relative),
                ButtonMask = pressed ? MouseButtonMask.Left : 0,
            });
        }
        else
        {
            Vector2 screenPosition = ToScreenPosition(position);
            Vector2 screenRelative = screenPosition - ToScreenPosition(_position - relative);
            await WarpForMoveAsync(position);
            if (pressed)
                Input.ParseInputEvent(new InputEventScreenDrag
                {
                    Index = 0,
                    Position = screenPosition,
                    Relative = screenRelative,
                    Pressure = 1f,
                });
            else
                Push(new InputEventMouseMotion
                {
                    Position = position,
                    GlobalPosition = screenPosition,
                    Relative = relative,
                    ButtonMask = 0,
                }); // Moving between taps is NOT a new screen drag.
        }

        await WaitFramesAsync(1);
    }

    /// <summary>
    /// Releases the touch/mouse at <paramref name="position"/>. With <paramref name="keepHover"/>, one stationary mouse
    /// motion is injected at the same point after the release: a lifted finger leaves the last touch point as the mouse,
    /// and the engine only re-evaluates hover/focus on a mouse motion (the touch release alone does not produce one), so
    /// the card under the lift point becomes the native focused holder for that frame. Off by default; every existing
    /// caller keeps exactly the old event stream.
    /// </summary>
    public async Task ReleaseAsync(Vector2 position, bool keepHover = false)
    {
        if (_mode == PointerMode.Touch)
        {
            Vector2 mouse = Viewport.GetMousePosition();
            Log.Info($"[test pointer] release canvas={position} mouse={mouse} delta={mouse - position}");
        }
        if (_mode == PointerMode.Mouse)
        {
            SendMouseButton(position, pressed: false);
        }
        else
        {
            SendTouch(position, pressed: false);
        }

        if (keepHover)
        {
            Push(new InputEventMouseMotion
            {
                Position = position,
                GlobalPosition = ToScreenPosition(position),
                Relative = Vector2.Zero,
                ButtonMask = 0,
            });
        }

        _position = position;
        await WaitFramesAsync(2);
    }

    private void SendMouseButton(Vector2 position, bool pressed)
    {
        Push(new InputEventMouseButton
        {
            Position = position,
            GlobalPosition = ToScreenPosition(position),
            ButtonIndex = MouseButton.Left,
            ButtonMask = pressed ? MouseButtonMask.Left : 0,
            Pressed = pressed,
        });
    }

    private void SendTouch(Vector2 position, bool pressed)
    {
        Input.ParseInputEvent(new InputEventScreenTouch
        {
            Index = 0,
            Position = ToScreenPosition(position),
            Pressed = pressed,
        });
    }

    private void Push(InputEvent inputEvent)
    {
        // PushInput with local coordinates avoids any window/content-scale
        // ambiguity. Viewport.WarpMouse above keeps Viewport.GetMousePosition in
        // sync for the card-drag code.
        Viewport.PushInput(inputEvent, inLocalCoords: true);
    }

    private async Task WaitFramesAsync(int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
        }
    }
}

/// <summary>
/// The window guard proved that the pointer cannot reach the game (another window covers it, or the pointer is not
/// arriving). The scenario entry catches this to skip the remaining touch steps after reporting one clear failed step.
/// </summary>
internal sealed class WindowObscuredException : InvalidOperationException
{
    public WindowObscuredException(string message) : base(message)
    {
    }
}
