using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The <c>pause-menu</c> scenario: the top bar's gear opens the pause menu in a live fight, Settings opens and returns without
/// resuming, Resume and Back keep the same combat room, and reopening does not accumulate scale. The flow and step names are
/// shared; the layout answer (44 point rows, the title, the panel's place) is the host's
/// (HostMenuChecks.cs / LauncherMenuChecks.cs). The T5 gate runs on the pause menu and on the in-run Settings it opens.
/// </summary>
internal sealed partial class TestDriver
{
    private async Task RunPauseMenuAsync()
    {
        var room = await EnterCombatAsync();
        var pauseButton = NRun.Instance!.GlobalUi.TopBar.Pause;

        async Task<NPauseMenu> OpenAsync()
        {
            await _pointer.TapAsync(CenterOf(pauseButton));
            await WaitUntilAsync(() => RunManager.Instance.IsPaused && VisibleScreen(nameof(NPauseMenu)) is not null,
                "pause via top-bar gear", ShortWait);
            await WaitSecondsAsync(0.65);
            return (NPauseMenu)VisibleScreen(nameof(NPauseMenu))!;
        }

        var menu = await OpenAsync();
        var panel = menu.GetNode<Control>("PanelContainer");
        var buttons = menu.GetNode<Control>("%ButtonContainer");
        var visible = buttons.GetChildren().OfType<NPauseMenuButton>().Where(button => button.IsVisibleInTree()).ToArray();
        var title = menu.GetNode<Label>("%PausedText/Label");
        Rect2 before = panel.GetGlobalRect();
        _report.Check("pause_native_buttons_visible", visible.Length >= 4 && visible.All(button => button.GetNode<Control>("ButtonImage").IsVisibleInTree()),
            string.Join(", ", visible.Select(button => button.Name.ToString())));
        CheckPauseLayout(menu, panel, visible, title, before);
        CheckTouchTargets("t5_pause_menu", "pause menu", menu, NativeBackAllow);
        await CaptureAsync("pause_01_menu");

        await _pointer.TapAsync(CenterOf(buttons.GetNode<Control>("Settings")));
        await WaitUntilAsync(() => VisibleScreen("NSettingsScreen") is not null, "in-run Settings opens", ShortWait);
        await WaitSecondsAsync(0.5);
        var settings = VisibleScreen("NSettingsScreen")!;
        _report.Check("pause_settings_keeps_run_paused", RunManager.Instance.IsPaused, "opening Settings does not resume combat");
        CheckTouchTargets("t5_pause_settings", "in-run settings", settings,
            new[] { NativeBackAllow }.Concat(HostSettingsGateAllowlist()).ToArray());
        await _pointer.TapAsync(CenterOf(settings.GetNode<Control>("BackButton")));
        await WaitUntilAsync(() => menu.IsVisibleInTree() && !settings.IsVisibleInTree(), "Settings Back returns to pause", ShortWait);
        _report.Check("pause_layout_stable_after_settings", panel.GetGlobalRect().IsEqualApprox(before) && RunManager.Instance.IsPaused,
            $"panel={panel.GetGlobalRect()} before={before}");

        await _pointer.TapAsync(CenterOf(buttons.GetNode<Control>("Resume")));
        await WaitUntilAsync(() => !RunManager.Instance.IsPaused, "Resume returns to combat", ShortWait);
        _report.Check("pause_resume_preserves_combat", NRun.Instance?.CombatRoom == room, "same combat room after Resume");
        await WaitSecondsAsync(0.65);
        var reopened = await OpenAsync();
        _report.Check("pause_reopens_without_accumulating_scale", reopened.GetNode<Control>("PanelContainer").GetGlobalRect().IsEqualApprox(before),
            $"reopened={reopened.GetNode<Control>("PanelContainer").GetGlobalRect()} before={before}");
        await _pointer.TapAsync(CenterOf(reopened.GetNode<Control>("%BackButton")));
        await WaitUntilAsync(() => !RunManager.Instance.IsPaused && VisibleScreen(nameof(NPauseMenu)) is null,
            "pause Back returns to combat", ShortWait);
        _report.Check("pause_back_preserves_combat", NRun.Instance?.CombatRoom == room, "same combat room after Back");
    }

    // Host answer: every row inside the frame with a 44 point target, the title's readable size, the panel's place, and (in
    // landscape) that the game's own scale is untouched.
    private partial void CheckPauseLayout(NPauseMenu menu, Control panel, IReadOnlyList<NPauseMenuButton> visible, Label title, Rect2 before);
}
