using System;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The main-menu scenarios (<c>menus</c>, <c>menu-hover</c>, <c>menu-utilities</c>, <c>settings</c>) and the entry the
/// launcher host uses for them: they open and close native screens, they never start a run except for the saved-run
/// fixture <c>menu-hover</c> needs. Each screen's geometry is the host's own partial (LauncherMenuChecks.cs); the T5 gate
/// runs inside the scenario files.
/// </summary>
internal sealed partial class TestDriver
{
    /// <summary>
    /// Runs one of the main-menu scenarios on a host that owns its own menu (the launcher host). The caller has already
    /// started the game and keeps its own report, screenshots and pointer.
    /// </summary>
    internal static async Task RunMenuScenarioAsync(TestOptions options, TestReport report, ScreenshotService screenshots,
        VirtualPointer pointer)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Log.Error("Test harness requires a SceneTree.");
            return;
        }

        await new TestDriver(tree, options, report, screenshots, pointer).RunMenuScenarioAsync(waitForMenu: true);
    }

    private async Task RunMenuScenarioAsync(bool waitForMenu)
    {
        if (waitForMenu) await WaitForMainMenuSceneAsync();
        // The game fades its whole window in over the main menu for two seconds after startup (and again after returning
        // from a run). NTransition is a full-window ColorRect above the menu, so a press or a hover delivered during the
        // fade is lost before it reaches a button: a tap during the fade lands inside it and the button never opens its
        // popup. Wait for the game's own transition to finish before touching anything.
        await WaitForMainMenuTransitionAsync();
        if (waitForMenu)
            // The game keeps loading common/menu assets after the main menu appears; wait for that real preload instead of
            // a fixed sleep, which is both slower than needed and still racy on a slow load ("Invalid Task ID").
            await WaitForMainMenuPreloadAsync();
        switch (_options.Scenario)
        {
            case "menus":
                await RunMenuNavigationAsync();
                break;
            case "menu-hover":
                await RunMenuHoverAsync();
                break;
            case "menu-utilities":
                await RunMenuUtilitiesAsync();
                break;
            case "settings":
                await RunSettingsInteractionsAsync();
                break;
            default:
                throw new ArgumentException($"'{_options.Scenario}' is not a main-menu scenario.");
        }
    }

    /// <summary>
    /// Waits until the game's own fade transition (NTransition, a full-window ColorRect above everything) has finished, so
    /// the next injected press or hover is not delivered into it. Also waits out a transition that starts while waiting.
    /// </summary>
    private async Task WaitForMainMenuTransitionAsync()
    {
        if (NGame.Instance?.Transition is not { } transition) return;
        await WaitUntilAsync(() => !GodotObject.IsInstanceValid(transition) || !transition.InTransition,
            "main menu fade-in", MediumWait);
    }

    /// <summary>Waits for the game's main menu to be the shown, opaque screen.</summary>
    private async Task WaitForMainMenuSceneAsync()
    {
        await WaitUntilAsync(
            () => NGame.Instance?.MainMenu is { } menu
                  && GodotObject.IsInstanceValid(menu)
                  && menu.IsVisibleInTree() && menu.Modulate.A > 0f
                  && SaveManager.Instance is not null,
            "main menu",
            LongWait);

        _report.Pass("main_menu_ready");
    }
}
