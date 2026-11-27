using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost;

/// <summary>
/// Shim <c>TouchHoverOwnership</c>, test runs only. This host warps the real cursor onto every injected touch because on a
/// <c>FEATURE_MOUSE</c> display server the game reads <c>Viewport.GetMousePosition()</c> and its native hover from that
/// cursor; on a phone there is no mouse feature, every mouse event a finger sends is emulated (device -1), and
/// <see cref="PhoneTouchCards.HoverFromTouch"/> therefore keeps the hand's hover with the finger for the whole gesture.
/// X11 delivers each warp as a real (device 0) mouse motion as well, and <see cref="PhoneTouchCards.OnWindowInput"/>
/// takes that as a real pointer and clears the ownership with it: the portrait hand then follows the native focus of
/// whichever card sits under the parked cursor (the lower-row card under a cancel release) and grows its card node one
/// held-scale step as the played card returns. That event is one a finger cannot send, so this shim skips the launcher's
/// call for the warp's motion only: while the finger is down, or within <see cref="TouchPointSlop"/> canvas units of the
/// point it last left. A real pointer that has moved away from the touch point, a real press and controller input still
/// clear the ownership exactly as the launcher already does. The phone build has neither the warp nor this shim.
/// </summary>
internal static class TouchHoverOwnership
{
    public const string Name = "TouchHoverOwnership";
    public const string HarmonyId = "sts2mobile.desktophost.touchhover";

    /// <summary>Canvas units a warp's motion may land from the emulated touch point and still be the finger's own.</summary>
    private const float TouchPointSlop = 16f;

    private static Harmony? _harmony;
    private static bool _hasTouchPoint;
    private static bool _down;
    private static Vector2 _touchCanvas;

    public static void Install()
    {
        if (_harmony != null) return;
        try
        {
            MethodInfo onWindowInput = AccessTools.Method(typeof(PhoneTouchCards), "OnWindowInput", [typeof(InputEvent)])
                ?? throw new InvalidOperationException("PhoneTouchCards.OnWindowInput(InputEvent) not found.");
            var harmony = new Harmony(HarmonyId);
            harmony.Patch(onWindowInput, prefix: new HarmonyMethod(typeof(TouchHoverOwnership), nameof(Prefix)));
            _harmony = harmony;
            PhonePatchShims.AddResult(Name, null);
            Harness.Log.Info($"Launcher host shim applied: {Name}");
        }
        catch (Exception ex)
        {
            PhonePatchShims.AddResult(Name, ex.Message);
            Harness.Log.Error($"Launcher host shim {Name} failed: {ex}");
        }
    }

    /// <summary>
    /// False drops the launcher's handler for a warp motion a finger cannot send; every other event, including the real
    /// pointer motions that should take the hover back, runs the launcher's own method unchanged.
    /// </summary>
    private static bool Prefix(InputEvent input)
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            // Landscape is the game's own; forget the finger so a later portrait entry starts clean.
            _hasTouchPoint = false;
            _down = false;
            return true;
        }
        if (input is InputEventScreenTouch touch && touch.Index == 0)
        {
            _down = touch.Pressed;
            RememberTouch(touch.Position);
            return true;
        }
        if (input is InputEventScreenDrag drag && drag.Index == 0)
        {
            RememberTouch(drag.Position);
            return true;
        }
        if (_hasTouchPoint && input is InputEventMouseMotion { Device: >= 0 } motion)
            return !(_down || ToCanvas(motion.Position).DistanceTo(_touchCanvas) <= TouchPointSlop);
        return true;
    }

    private static void RememberTouch(Vector2 windowPosition)
    {
        _hasTouchPoint = true;
        _touchCanvas = ToCanvas(windowPosition);
    }

    private static Vector2 ToCanvas(Vector2 windowPosition)
    {
        if (Engine.GetMainLoop() is not SceneTree tree) return windowPosition;
        return tree.Root.GetFinalTransform().AffineInverse() * windowPosition;
    }
}
