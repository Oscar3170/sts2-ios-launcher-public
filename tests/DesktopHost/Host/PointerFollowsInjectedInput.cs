using System.Reflection;
using Godot;
using HarmonyLib;

namespace STS2Mobile.DesktopHost;

/// <summary>
/// Shim <c>PointerFollowsInjectedInput</c>, test runs only. On the phone the pointer position follows the touch and mouse events
/// the launcher's proofs inject with <c>Input.ParseInputEvent</c>. On X11 the game's card-drag code reads
/// <c>Viewport.GetMousePosition</c>, which is the real cursor, so an injected press or drag never moved it and every proof card play
/// failed. A prefix on <c>Input.ParseInputEvent</c> converts the event's window point to the viewport's canvas point and
/// calls <c>Viewport.WarpMouse</c>, as <c>VirtualPointer</c> does for the harness. That is the API that agrees with
/// <c>Viewport.GetMousePosition</c>: <c>Input.WarpMouse</c> is relative to the window-manager frame, whose title bar sits
/// above the client area. This replaces the phone's pointer behaviour; it is not launcher logic, and the launcher's code
/// is not involved. Warping again to the same point (VirtualPointer) is harmless.
/// </summary>
internal static class PointerFollowsInjectedInput
{
    public const string Name = "PointerFollowsInjectedInput";
    public const string HarmonyId = "sts2mobile.desktophost.pointer";

    private static Harmony? _harmony;

    [ThreadStatic]
    private static bool _warping;

    public static void Install()
    {
        if (_harmony != null) return;
        try
        {
            MethodInfo parse = AccessTools.Method(typeof(Input), nameof(Input.ParseInputEvent), [typeof(InputEvent)])
                ?? throw new InvalidOperationException("Input.ParseInputEvent(InputEvent) not found.");
            var harmony = new Harmony(HarmonyId);
            harmony.Patch(parse, prefix: new HarmonyMethod(typeof(PointerFollowsInjectedInput), nameof(Prefix)));
            _harmony = harmony;
            // The launcher's game paths read the OS cursor, so this host uses the canvas warp and its learned offset (see
            // VirtualPointer.WarpForMoveAsync).
            Harness.VirtualPointer.UseViewportWarp = true;
            PhonePatchShims.AddResult(Name, null);
            Harness.Log.Info($"Launcher host shim applied: {Name}");
        }
        catch (Exception ex)
        {
            PhonePatchShims.AddResult(Name, ex.Message);
            Harness.Log.Error($"Launcher host shim {Name} failed: {ex}");
        }
    }

    private static void Prefix(InputEvent __0)
    {
        if (_warping || Harness.VirtualPointer.SuppressOsWarp
            || Engine.GetMainLoop() is not SceneTree tree) return;
        // The harness and the launcher's proofs inject window pixels (the canvas point through GetFinalTransform), so the
        // inverse of the viewport's screen transform recovers the canvas point the game should see.
        Vector2? windowPosition = __0 switch
        {
            InputEventMouseButton button => button.GlobalPosition,
            InputEventMouseMotion motion => motion.GlobalPosition,
            InputEventScreenTouch touch => touch.Position,
            InputEventScreenDrag drag => drag.Position,
            _ => null,
        };
        if (windowPosition is not { } target) return;
        Window root = tree.Root;
        Vector2 canvas = root.GetScreenTransform().AffineInverse() * target;
        _warping = true;
        try
        {
            // Viewport.WarpMouse takes viewport (canvas) coordinates, but on X11 the platform warp can still land against
            // the window-manager frame instead of the client area. The cheap warp applies only an offset that has already
            // been verified; the settled warp then reads after a frame, corrects, and commits the offset only once the
            // engine reports the requested point, so a stale first read cannot teach a bogus offset.
            Harness.VirtualPointer.WarpToCanvas(root, canvas);
            Harness.VirtualPointer.RequestSettledWarp(tree, root, canvas);
        }
        finally
        {
            _warping = false;
        }
    }
}
