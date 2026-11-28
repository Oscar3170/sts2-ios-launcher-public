using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.DesktopHost.Harness;

// Global settings are NOT profile-scoped. Exercise actual native widgets while suppressing disk persistence, then restore
// every touched value in finally. The launcher host already suppresses every settings write.
internal sealed partial class TestDriver
{
    private async Task RunSettingsInteractionsAsync()
    {
        var save = SaveManager.Instance;
        var originalFast = save.PrefsSave.FastMode;
        float originalVolume = save.SettingsSave.VolumeMaster;
        try
        {
            var menu = NGame.Instance!.MainMenu!;
            await TapMenuUntilAsync(menu.GetNode<Control>("MainMenuTextButtons/SettingsButton"), "settings_open",
                () => VisibleScreen("NSettingsScreen") is not null, "settings opened by touch", MediumWait);
            var settings = (NSettingsScreen)VisibleScreen("NSettingsScreen")!;
            Control clipper = settings.GetNode<Control>("ScrollContainer/Mask/Clipper");
            Control general = clipper.GetNode<Control>("GeneralSettings");
            var tick = general.GetNode<NTickbox>("VBoxContainer/FastMode/FastModeTickbox");
            bool before = tick.IsTicked;
            await TapMenuAsync(tick, "settings_fast_tick");
            _report.Check("settings_fast_effect", tick.IsTicked != before && save.PrefsSave.FastMode != originalFast, save.PrefsSave.FastMode.ToString());
            await TapMenuAsync(tick, "settings_fast_restore");
            _report.Check("settings_fast_restored", save.PrefsSave.FastMode == originalFast, "native second tap restores preference");
            CheckTouchTargets("t5_settings_general", "settings General", settings,
                new[] { NativeBackAllow }.Concat(HostSettingsGateAllowlist()).ToArray());
            await CaptureAsync("settings_01_general");

            // Open native language list without selecting/changing locale.
            var positioner = general.GetNode<NDropdownPositioner>("VBoxContainer/LanguageLine/DisplayDropdownAnchor");
            var dropdown = (Control)AccessTools.Field(typeof(NDropdownPositioner), "_dropdownNode").GetValue(positioner)!;
            await TapMenuAsync(dropdown, "settings_language_open");
            var list = dropdown.GetNode<NDropdownContainer>("Container/DropdownContainer");
            _report.Check("settings_language_list_visible", list.IsVisibleInTree(), list.GetGlobalRect().ToString());
            CheckSettingsLanguageListLayout(settings, list);
            CheckTouchTargets("t5_settings_language_list", "settings language list", settings,
                new[] { NativeBackAllow, DropdownRowsAllow }.Concat(HostSettingsGateAllowlist()).ToArray());
            await CaptureAsync("settings_02_language_list");
            Control items = list.GetNode<Control>("VBoxContainer");
            float initialY = items.Position.Y;
            await _pointer.DragAsync(list.GetGlobalRect().GetCenter() + new Vector2(0, 150), list.GetGlobalRect().GetCenter() - new Vector2(0, 150));
            await WaitSecondsAsync(0.6);
            // The native list may rebuild its item container while it scrolls; read it only when it is still alive.
            float scrolledY = GodotObject.IsInstanceValid(items) ? items.Position.Y : float.NaN;
            _report.Check("settings_language_scroll", !float.IsNaN(scrolledY) && scrolledY < initialY - 10,
                $"{initialY} -> {(float.IsNaN(scrolledY) ? "container rebuilt" : scrolledY.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
            Vector2 dismissPoint = SettingsDismissPoint();
            // Diagnostic only: who owns the dismiss point (an oversized hit pad here would swallow the outside tap and leave
            // the list open, which is what the next steps would then trip over).
            Log.Info($"[TEST] settings dismiss point owners: {DescribePoint(_tree, dismissPoint)}");
            await _pointer.TapAsync(dismissPoint);
            await WaitSecondsAsync(0.4);
            // The launcher's list can be rebuilt or freed while it is dragged, so it may already be gone (which is the state
            // the dismiss tap is meant to reach, just without a node left to ask).
            if (!GodotObject.IsInstanceValid(list))
                _report.Pass("settings_language_dismiss", "native list was already closed and freed before the outside tap");
            else
                _report.Check("settings_language_dismiss", !list.IsVisibleInTree(), "outside tap closes native list");

            await TapMenuAsync(settings.GetNode<Control>("SettingsTabManager/Sound"), "settings_sound_tab");
            CheckTouchTargets("t5_settings_sound", "settings Sound", settings,
                new[] { NativeBackAllow }.Concat(HostSettingsGateAllowlist()).ToArray());
            Control master = clipper.GetNode<Control>("SoundSettings/VBoxContainer/MasterVolume/MasterVolumeSlider");
            var slider = master.GetNode<Godot.Range>("Slider");
            Rect2 sliderRect = ((Control)slider).GetGlobalRect();
            await _pointer.DragAsync(sliderRect.GetCenter(), sliderRect.Position + sliderRect.Size * new Vector2(0.72f, 0.5f));
            await WaitSecondsAsync(0.6);
            _report.Check("settings_volume_effect", Math.Abs(save.SettingsSave.VolumeMaster - originalVolume) > 0.02f, $"{originalVolume} -> {save.SettingsSave.VolumeMaster}");
            _report.Check("settings_volume_label", master.GetNode<Label>("SliderValue").Text == $"{slider.Value}%", master.GetNode<Label>("SliderValue").Text);
            await CaptureAsync("settings_03_sound");
            // Restore through the native value-changed signal/audio handler.
            slider.Value = originalVolume * 100;

            await TapMenuAsync(settings.GetNode<Control>("SettingsTabManager/Input"), "settings_input_tab");
            CheckTouchTargets("t5_settings_input", "settings Input", settings,
                new[] { NativeBackAllow }.Concat(HostSettingsGateAllowlist()).ToArray());
            Control input = clipper.GetNode<Control>("InputSettings");
            await CaptureAsync("settings_04a_input_top");
            CheckSettingsInputLayout(settings, input);
            float topY = input.Position.Y;
            // The swipe has to stay on the rows it scrolls: 500 units below the clipper's centre is outside the 1080 unit
            // landscape canvas, and a press outside the window never reaches the scroll view (landscape keeps the game's
            // native scroll), so the reach and the centre are taken from the clip's on-canvas part. Both shapes then get
            // the same drag.
            Rect2 clip = clipper.GetGlobalRect();
            Rect2 area = clip.Intersection(new Rect2(Vector2.Zero, GetViewportSize()));
            if (!area.HasArea()) area = clip;
            float reach = Mathf.Min(500f, Mathf.Max(40f, area.Size.Y * 0.35f));
            // Landscape is the game's own scroll view. Its settings panel root is a mouse-filter STOP control, so a press
            // over the rows is swallowed by the panel and the native NScrollableContainer never gets the drag (only the
            // General panel is IGNORE in the scene). The scroll view owns the margins outside the panel: press there and
            // the game's own drag scrolling runs. In portrait the launcher's rows gesture owns the press anywhere in the
            // clip, so the centre column is kept exactly as before.
            float columnX = IsPortraitHost() ? clip.GetCenter().X - 400f : clip.Position.X + 16f;
            Log.Info($"[TEST] settings input drag column x={columnX:F1} clip={clip} area={area} reach={reach:F1}");
            for (int i = 0; i < 8; i++)
            {
                await _pointer.DragAsync(new Vector2(columnX, area.GetCenter().Y + reach), new Vector2(columnX, area.GetCenter().Y - reach));
                await WaitSecondsAsync(0.25);
            }
            _report.Check("settings_input_scroll", input.Position.Y < topY - 100, $"{topY} -> {input.Position.Y}");
            await CaptureAsync("settings_04_input_scrolled");
            await TapMenuAsync(settings.GetNode<Control>("BackButton"), "settings_close");
            await TapMenuUntilAsync(menu.GetNode<Control>("MainMenuTextButtons/SettingsButton"), "settings_reopen",
                () => VisibleScreen("NSettingsScreen") is not null, "settings reopened by touch", MediumWait);
            await TapMenuAsync(settings.GetNode<Control>("SettingsTabManager/General"), "settings_general_reopen");
            CheckSettingsReopenStable(settings, general);
            CheckTouchTargets("t5_settings_reopened", "settings General (reopened)", settings,
                new[] { NativeBackAllow }.Concat(HostSettingsGateAllowlist()).ToArray());
            await CaptureAsync("settings_05_reopened");
            await TapMenuAsync(settings.GetNode<Control>("BackButton"), "settings_final_back");
        }
        finally
        {
            save.PrefsSave.FastMode = originalFast;
            save.SettingsSave.VolumeMaster = originalVolume;
        }
    }

    // Host answer: the language list's frame (the safe frame and its capped height).
    private partial void CheckSettingsLanguageListLayout(NSettingsScreen settings, NDropdownContainer list);

    // Host answer: where an outside tap lands to dismiss the open language list.
    private partial Vector2 SettingsDismissPoint();

    // Host answer: the Input page's columns are inside the frame (the launcher checks each header cell).
    private partial void CheckSettingsInputLayout(NSettingsScreen settings, Control input);

    // Host answer: the reopened page is still at its portrait scale, not stacked or reset.
    private partial void CheckSettingsReopenStable(NSettingsScreen settings, Control general);

    // Host answer: one Settings tab's page, after the native tab switch (the launcher checks the tabs and rows sit under
    // its measured cap).
    private partial void CheckSettingsTabLayout(Control settings, string tab, Control page);

    // Host answer: where the page's own scroll view stops, against the drawn bottom of its last visible row. The phone
    // rows are not on this host, so the Graphics tab keeps the desktop rows (it fits in portrait) and the General tab
    // exercises the same page sizing; landscape keeps the game's own page.
    private partial Task CheckSettingsScrollEndAsync(Control settings, string tab, Control page);
}
