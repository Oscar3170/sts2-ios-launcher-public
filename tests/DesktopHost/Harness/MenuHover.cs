using System;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The <c>menu-hover</c> scenario: the main menu's native hover on Continue, Settings and Quit with a real mouse pointer, the
/// mod-config label's outline, and the saved run's Continue details. The fixture (a real save in the isolated test profile),
/// the pointer moves and the step names are shared; the host asserts its own hover geometry in
/// <c>CheckModConfigOutlineAsync</c> / <c>CheckMenuButtonHover</c> (native carets and 1.607x Continue details).
/// </summary>
internal sealed partial class TestDriver
{
    private async Task RunMenuHoverAsync()
    {
        var menu = NGame.Instance!.MainMenu!;
        if (!menu.GetNode<Control>("MainMenuTextButtons/ContinueButton").Visible)
        {
            // Create an actual save in the isolated test profile, then use the
            // normal save-and-return path. Never borrow the user's saved run.
            await StartRunAsync();
            await WaitSecondsAsync(2);
            await SaveManager.Instance.SaveRun(null, saveProgress: false);
            await NGame.Instance.ReturnToMainMenu();
            await WaitForMainMenuSceneAsync();
            await WaitSecondsAsync(3);
            menu = NGame.Instance.MainMenu!;
        }
        if (!menu.GetNode<Control>("MainMenuTextButtons/ContinueButton").Visible)
            throw new InvalidOperationException("Saved-run fixture did not expose Continue; do not test a hidden control.");
        var mouse = new VirtualPointer(_tree, PointerMode.Mouse);
        await CheckModConfigOutlineAsync(menu);
        foreach (string name in new[] { "ContinueButton", "SettingsButton", "QuitButton" })
        {
            Control button = menu.GetNode<Control>("MainMenuTextButtons/" + name);
            await HoverMenuButtonAsync(mouse, menu, button);
            CheckMenuButtonHover(menu, button, name);
            await CaptureAsync("menu_hover_" + name);
        }
        CheckTouchTargets("t5_main_menu_hover", "main menu", menu, NativeBackAllow);
        _report.Pass("menu_native_hover_checked");
    }

    /// <summary>
    /// Hovers a main-menu button until the native carets are on it, bounded to three moves. The carets are the game's own
    /// focus feedback; a motion the engine drops while it re-lays the menu out (tests/docs/bugs/TEST-HARNESS-ISSUES.md
    /// H-009 measures the same dropped focus) leaves them on the previous button, which is what the retry detects. The hover
    /// is driven by <see cref="VirtualPointer.HoverAsync"/>: a settled warp followed by one exact motion event at the button,
    /// because the real cursor's native motion can be a window-manager frame away from the requested point (720x1280 hovers
    /// the next row down) while the game's own readback agrees with the warp.
    /// </summary>
    private async Task HoverMenuButtonAsync(VirtualPointer mouse, NMainMenu menu, Control button)
    {
        Control reticle = menu.GetNode<Control>("%ButtonReticleLeft");
        Rect2 text = button.GetChild<Label>(0).GetGlobalRect();
        bool CaretsOnButton() => reticle.Modulate.A > 0.9f
            && Math.Abs(reticle.GetGlobalRect().GetCenter().Y - text.GetCenter().Y) < 40f;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await mouse.HoverAsync(CenterOf(button) + new Vector2(attempt, 0f));
            await WaitSecondsAsync(0.4);
            if (CaretsOnButton()) break;
            Control? hovered = _tree.Root.GetViewport().GuiGetHoveredControl();
            Log.Warn($"[TEST] native hover did not reach {button.Name}; the engine hovers " +
                     $"{(hovered is null ? "none" : $"{hovered.GetType().Name}:{hovered.Name} rect={hovered.GetGlobalRect()}")} " +
                     $"at {mouse.Position}; moving the pointer onto it again");
        }
        // The caret tween is 0.25 s Back/Out and the label's hover scale runs with it.
        await WaitSecondsAsync(0.2);
    }

    // Host answer: the mod-config row's label copies the Settings label's font and black outline in portrait.
    private partial Task CheckModConfigOutlineAsync(NMainMenu menu);

    // Host answer: the outlined label, both hover carets (visible, aligned to the measured text, inside the frame, wide
    // enough) and, for Continue, the saved run's details (open, inside the frame, above the buttons, at their readable scale).
    private partial void CheckMenuButtonHover(NMainMenu menu, Control button, string name);
}
