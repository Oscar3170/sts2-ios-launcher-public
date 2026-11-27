using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>Feed a test size into the native startup window setup, not the portrait canvas.
/// Never change the monitor mode or resize after the menu has initialized.</summary>
[HarmonyPatch(typeof(NGame), nameof(NGame.ApplyDisplaySettings))]
internal static class TestWindowScope
{
    private static Vector2I? _requested;
    private static bool _applied;

    public static void Install(Vector2I? size) => _requested = size;

    private static bool _borderless;

    /// <summary>
    /// Removes the window-manager frame before the game applies its display settings. Under Wine a decorated window's
    /// client origin — the value <c>Viewport.GetMousePosition</c> and the warps use — differs from the client coordinates of
    /// native <c>WM_MOUSEMOVE</c> messages by the title bar (53 window pixels; 79.5 canvas units at 720x1280). The warp and
    /// the readback then agree while the real cursor's native motion hovers a point one frame away, which is the offset that
    /// put the menu carets on the next row and, on every test size, moved the orb tip onto the arena-wide
    /// <c>BackCombatVfxContainer</c> after it had opened. A borderless window has one client space, so warp, readback and
    /// native motion coincide. The game's own display settings still apply the requested size and position afterwards.
    /// </summary>
    public static void MakeBorderless()
    {
        if (_borderless) return;
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree || !GodotObject.IsInstanceValid(tree.Root)) return;
            if (!DisplayServer.WindowGetFlag(DisplayServer.WindowFlags.Borderless))
                DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, true);
            _borderless = DisplayServer.WindowGetFlag(DisplayServer.WindowFlags.Borderless);
            Log.Info($"[TEST] test window borderless: flag={_borderless} size={DisplayServer.WindowGetSize()} " +
                     $"position={DisplayServer.WindowGetPosition()}");
        }
        catch (Exception e)
        {
            Log.Warn($"[TEST] test window borderless: {e.Message}");
        }
    }

    private readonly record struct Original(Vector2I Size, Vector2I Position, int Display);

    [HarmonyPrefix]
    private static void Prefix(out Original? __state)
    {
        __state = null;
        if (_applied || _requested is not { } requested || SaveManager.Instance?.SettingsSave is not { } settings) return;
        _applied = true;
        __state = new Original(settings.WindowSize, settings.WindowPosition, settings.TargetDisplay);
        settings.WindowSize = requested;
        settings.WindowPosition = new Vector2I(-1, -1);
        settings.TargetDisplay = DisplayServer.GetPrimaryScreen();
        Log.Info($"[TEST] requested native window size {requested} (windowed startup only)");
    }

    [HarmonyFinalizer]
    private static void Finalizer(Original? __state)
    {
        if (__state is not { } original || SaveManager.Instance?.SettingsSave is not { } settings) return;
        settings.WindowSize = original.Size;
        settings.WindowPosition = original.Position;
        settings.TargetDisplay = original.Display;
        // The game sizes a windowed startup for a decorated frame; without one the window manager later grows the client by
        // the title bar (720x1280 became 720x1317: a taller canvas and the other portrait profile). Put the requested size
        // back when that startup growth arrives; a few corrections at most, so no later resize is ever fought.
        if (!_borderless || _requested is not { } requested || Engine.GetMainLoop() is not SceneTree tree) return;
        Window root = tree.Root;
        int corrections = 0;
        void Correct()
        {
            if (root.Size == requested) return;
            // Second half of a nudge: the one-pixel-taller window arrived, so the requested size is now a real change.
            if (root.Size == requested + new Vector2I(0, 1))
            {
                DisplayServer.WindowSetSize(requested);
                return;
            }
            if (corrections >= 4) return;
            corrections++;
            Log.Info($"[TEST] borderless window grew to {root.Size}; restoring the requested {requested}");
            // The display server may already cache the requested size while the window manager kept the grown one, which
            // makes a direct request a no-op; ask for one pixel more first and finish on the resulting size change.
            DisplayServer.WindowSetSize(requested + new Vector2I(0, 1));
        }
        root.SizeChanged += Correct;
        Correct();
    }

    /// <summary>
    /// Waits, before the scenario starts, for the window manager's startup growth to be undone. Without slow-starting mods
    /// the main menu can be ready before the late title-bar growth arrives; the window then stays at the grown size while
    /// the display server's cached size already equals the request, so a plain re-request is a no-op. Each attempt first
    /// asks for one pixel more, then the requested size. Bounded; returns the final root size.
    /// </summary>
    public static async System.Threading.Tasks.Task<Vector2I> SettleAsync(SceneTree tree)
    {
        Window root = tree.Root;
        if (!_borderless || _requested is not { } requested) return root.Size;
        for (int frame = 0; frame < 180 && root.Size != requested; frame++)
        {
            if (frame % 30 == 0)
            {
                Log.Info($"[TEST] window settle: root={root.Size} display={DisplayServer.WindowGetSize()}; requesting {requested}");
                DisplayServer.WindowSetSize(requested + new Vector2I(0, 1));
            }
            else if (frame % 30 == 2) DisplayServer.WindowSetSize(requested);
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
        return root.Size;
    }
}
