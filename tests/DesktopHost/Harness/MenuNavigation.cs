using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The <c>menus</c> scenario: the native touch path from the main menu (with the Quit row hidden) through the four Settings
/// tabs, the singleplayer mode picker, Blitz/Dread setup, character select with its ascension control, and Embark into a run.
/// <para>
/// The flow, the taps and the step names are the same throughout the scenario. Every screen's geometry answer is a partial:
/// the launcher answers with its own layout numbers (LauncherMenuChecks.cs, 127 units = 44 points, reference numbers
/// x0.643). The T5 gate runs on the main menu and each Settings tab.
/// </para>
/// </summary>
internal sealed partial class TestDriver
{
    private Control? VisibleScreen(string type) => FindAll<Control>(_tree.Root)
        .LastOrDefault(c => c.GetType().Name == type && c.IsVisibleInTree());

    /// <summary>
    /// Whether a control the scenario taps is inside the portrait frame. The launcher places every control fully on canvas
    /// (its Back buttons included), so the drawn rect itself has to fit; a touch pad reaching past the edge does not excuse
    /// the control.
    /// </summary>
    private bool ControlInFrame(Control control)
    {
        Rect2 safe = PortraitSafeRect().Grow(1f);
        Rect2 box = HostTouchTargetBox(control);
        if (safe.Encloses(box)) return true;
        Rect2 drawn = control.GetGlobalRect();
        return drawn.Position.X >= safe.Position.X - 1f && drawn.End.X <= safe.End.X + 1f
            && drawn.Position.Y >= safe.Position.Y - 1f && drawn.End.Y <= safe.End.Y + 1f;
    }

    private async Task TapMenuAsync(Control control, string name)
    {
        try
        {
            await WaitUntilAsync(() => control.IsVisibleInTree() && (control is not NButton button || button.IsEnabled),
                name + " visible and enabled", ShortWait);
        }
        catch (TimeoutException)
        {
            // Diagnostic only: the failure is rethrown unchanged, with a screenshot and the state that explains it.
            await FailureDiagnosticsAsync(_tree, _report, _screenshots, name + "_not_tappable", control);
            throw;
        }
        if (IsPortraitHost())
            _report.Check(name + "_in_frame", ControlInFrame(control),
                $"rect={control.GetGlobalRect()} target={HostTouchTargetBox(control)} safe={PortraitSafeRect()}");
        await _pointer.TapAsync(CenterOf(control));
        await WaitSecondsAsync(0.8);
    }

    /// <summary>Waits up to <paramref name="timeout"/> for a condition, without throwing.</summary>
    private async Task<bool> WaitOrFalseAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout) return false;
            await WaitFramesAsync(1);
        }
        return true;
    }

    /// <summary>
    /// Taps a menu control and, if the screen it opens did not appear, taps it once more. A press or hover delivered while
    /// the game moves focus or fades a screen is dropped before it reaches the button (docs/bugs/TEST-HARNESS-ISSUES.md
    /// H-008/H-009 measure the same thing for drags and card focus); the retry exists only for that, is logged, and still
    /// fails when the control or its handler is wrong. A timed-out wait logs what the engine says is under the pointer; it
    /// never changes the pass/fail outcome.
    /// </summary>
    private async Task TapMenuUntilAsync(Control control, string name, Func<bool> until, string what, TimeSpan timeout)
    {
        await TapMenuAsync(control, name);
        if (await WaitOrFalseAsync(until, TimeSpan.FromSeconds(2.5))) return;
        Log.Warn($"[TEST] {what} did not open after tapping {name}; tapping once more");
        await LogTapDiagnosticsAsync(name, control);
        await TapMenuAsync(control, name + "_retry");
        if (await WaitOrFalseAsync(until, timeout)) return;
        await LogTapDiagnosticsAsync(name + "_timeout", control);
        throw new TimeoutException($"Timed out waiting for {what} after {timeout.TotalSeconds:F0}s.");
    }

    /// <summary>
    /// Diagnostic only (never a check): puts the pointer on the control's centre and logs the engine's hovered control, the
    /// control's own enabled/visible/filter state, and each ancestor up to the screen with its rect and filter. Used when a
    /// tapped screen does not open, to tell "the press reached nothing" from "the control refused the press".
    /// </summary>
    private async Task LogTapDiagnosticsAsync(string label, Control control)
    {
        try
        {
            Vector2 point = CenterOf(control);
            await _pointer.MoveAsync(point, false);
            await WaitFramesAsync(2);
            Control? hovered = _tree.Root.GetViewport().GuiGetHoveredControl();
            string enabled = control is NButton button
                ? $"IsEnabled={button.IsEnabled} _isEnabled={AccessTools.Field(typeof(NClickableControl), "_isEnabled")?.GetValue(control)}"
                : "not a button";
            Log.Info($"[TEST] tap diagnostics {label}: point={point} hovered={DescribeControl(hovered)} " +
                     $"target={DescribeControl(control)} {enabled} Visible={control.Visible} InTree={control.IsVisibleInTree()} " +
                     $"MouseFilter={control.MouseFilter} clip={(control.ClipContents ? "on" : "off")}");
            int depth = 0;
            for (Node? node = control; node is not null && depth < 14; node = node.GetParent(), depth++)
            {
                string rect = node is Control c ? c.GetGlobalRect().ToString() : "-";
                string filter = node is Control f ? f.MouseFilter.ToString() : "-";
                string visible = node is CanvasItem item ? item.IsVisibleInTree().ToString() : "-";
                Log.Info($"[TEST] tap diagnostics {label} ancestor[{depth}] {node.GetType().Name}:{node.Name} " +
                         $"visible={visible} rect={rect} filter={filter}");
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[TEST] tap diagnostics {label} failed: {e.GetBaseException().Message}");
        }
    }

    private static string DescribeControl(Control? control) => control is null
        ? "none"
        : $"{control.GetType().Name}:{control.Name} path={control.GetPath()} rect={control.GetGlobalRect()} filter={control.MouseFilter}";

    private async Task RunMenuNavigationAsync()
    {
        if (SaveManager.Instance.CurrentProfileId != TestProfileScope.TestProfileId)
            throw new InvalidOperationException("Refusing to unlock a non-test profile.");
        // The exact native `unlock all` console command, isolated to profile 3.
        new MegaCrit.Sts2.Core.DevConsole.ConsoleCommands.UnlockConsoleCmd().Process(null, new[] { "all" });
        SaveManager.Instance.SetFtuesEnabled(false);
        // Unlocking ascensions introduces a separate native popup which does
        // not honor the general FTUE toggle. Mark it seen only in this profile.
        SaveManager.Instance.MarkFtueAsComplete("ascension_singleplayer_ftue");
        SaveManager.Instance.MarkFtueAsComplete("blitz_disclaimer");
        SaveManager.Instance.MarkFtueAsComplete("dread_disclaimer");
        var menu = NGame.Instance!.MainMenu!;
        // Fixture-only: previously abandoned test runs can queue a Timeline
        // unlock and disable Singleplayer. Test menu layout, not progression.
        menu.RefreshButtons();
        await WaitSecondsAsync(0.5);
        CheckTouchTargets("t5_main_menu", "main menu", menu, NativeBackAllow);

        Control quit = menu.GetNode<Control>("MainMenuTextButtons/QuitButton");
        _report.Check("menu_quit_hidden", !quit.IsVisibleInTree(),
            $"The game's Quit does nothing on iOS; the row must be hidden (visible={quit.IsVisibleInTree()} rect={quit.GetGlobalRect()})");

        await TapMenuUntilAsync(menu.GetNode<Control>("MainMenuTextButtons/SettingsButton"), "menu_settings",
            () => VisibleScreen("NSettingsScreen") is not null, "settings opened by touch", MediumWait);
        Control settings = VisibleScreen("NSettingsScreen")!;
        foreach (string tab in new[] { "General", "Graphics", "Sound", "Input" })
        {
            await TapMenuAsync(settings.GetNode<Control>("SettingsTabManager/" + tab), "settings_" + tab);
            Control page = settings.GetNode<Control>("ScrollContainer/Mask/Clipper/" + tab + "Settings");
            _report.Check("settings_" + tab + "_selected", page.IsVisibleInTree(), "native settings page switched");
            CheckSettingsTabLayout(settings, tab, page);
            CheckTouchTargets("t5_settings_" + tab.ToLowerInvariant(), "settings " + tab, settings,
                new[] { NativeBackAllow, DropdownRowsAllow }.Concat(HostSettingsGateAllowlist()).ToArray());
            await CaptureAsync("menu_settings_" + tab.ToLowerInvariant());
            System.IO.File.WriteAllLines(System.IO.Path.Combine(_options.OutputDirectory, "geometry_" + tab + ".txt"),
                FindAll<Control>(page).Select(c => $"{c.GetPath()} {c.GetType().Name} size={c.Size} min={c.GetCombinedMinimumSize()} scale={c.Scale} global={c.GetGlobalRect()}"));
            await CheckSettingsScrollEndAsync(settings, tab, page);
        }
        await TapMenuAsync(settings.GetNode<Control>("BackButton"), "settings_back");
        await WaitUntilAsync(() => menu.GetNode<Control>("MainMenuTextButtons").IsVisibleInTree(), "settings back returns to menu", MediumWait);

        if (menu.GetNode<Control>("MainMenuTextButtons/AbandonRunButton").IsVisibleInTree())
        {
            // Earlier fixture runs leave a save in the temporary test profile.
            // Never abandon the user's profile; only reset this fixture save.
            if (SaveManager.Instance.CurrentProfileId != TestProfileScope.TestProfileId)
                throw new InvalidOperationException("Refusing to abandon a non-test profile's run.");
            menu.AbandonRun();
            await WaitSecondsAsync(1);
        }

        // Temporarily bypass only the fixture profile's pending-Timeline gate.
        // This does not alter progress or production menu enablement rules.
        menu.GetNode<NButton>("MainMenuTextButtons/SingleplayerButton").Enable();
        await TapMenuUntilAsync(menu.GetNode<Control>("MainMenuTextButtons/SingleplayerButton"), "menu_singleplayer",
            () => VisibleScreen("NSingleplayerSubmenu") is not null || VisibleScreen("NCharacterSelectScreen") is not null,
            "singleplayer opened by touch", MediumWait);
        await CaptureAsync("menu_after_singleplayer_tap");
        _report.Pass("menus_visible_screens", string.Join("; ", FindAll<Control>(_tree.Root)
            .Where(c => c.IsVisibleInTree() && c.GetType().Name.StartsWith("N") && (c.GetType().Name.EndsWith("Screen") || c.GetType().Name.Contains("Submenu")))
            .Select(c => c.GetType().Name + " " + c.GetPath())));

        await WaitUntilAsync(() => VisibleScreen("NSingleplayerSubmenu") is not null || VisibleScreen("NCharacterSelectScreen") is not null,
            "singleplayer opened by touch", MediumWait);
        // Native first-run profiles skip mode selection entirely.
        if (VisibleScreen("NSingleplayerSubmenu") is { } modes)
        {
            await CaptureAsync("menu_singleplayer");
            var modeCards = modes.GetNode<Control>("ButtonContainer").GetChildren().OfType<Control>().Where(c => c.Visible).ToArray();
            CheckModeCardsLayout(modes, modeCards);
            foreach (string mode in new[] { "Blitz", "Dread" })
            {
                Control modeButton = modes.GetNode<Control>($"%{mode}Button");
                if (!modeButton.IsVisibleInTree() || modeButton is NButton { IsEnabled: false }) continue;
                await TapMenuUntilAsync(modeButton, $"menu_{mode}_open",
                    () => VisibleScreen($"N{mode}Screen") is not null, $"native {mode} setup", MediumWait);
                Control setup = VisibleScreen($"N{mode}Screen")!;
                var roster = setup.GetNode<Control>("CharSelectButtons/ButtonContainer").GetChildren().OfType<NCharacterSelectButton>()
                    .Where(b => b.IsVisibleInTree() && !b.IsLocked).ToArray();
                CheckModeSetupLayout(mode, setup, roster);
                var candidate = roster.FirstOrDefault(b => !b.IsSelected && !b.IsRandom) ?? roster[0];
                await TapMenuAsync(candidate, $"menu_{mode}_select");
                _report.Check($"menu_{mode}_native_selected", candidate.IsSelected, candidate.Character.GetType().Name);
                await CaptureAsync($"menu_{mode}_setup");
                await TapMenuUntilAsync(setup.GetNode<Control>("BackButton"), $"menu_{mode}_back",
                    () => modes.IsVisibleInTree(), "returns to the native mode picker", MediumWait);
            }
            await TapMenuUntilAsync(modes.GetNode<Control>("%StandardButton"), "menu_standard",
                () => VisibleScreen("NCharacterSelectScreen") is not null, "character selection opened by touch", MediumWait);
        }

        await WaitUntilAsync(() => VisibleScreen("NCharacterSelectScreen") is not null, "character selection opened by touch", MediumWait);
        Control characters = VisibleScreen("NCharacterSelectScreen")!;
        var buttons = characters.GetNode<Control>("CharSelectButtons/ButtonContainer").GetChildren().OfType<NCharacterSelectButton>().Where(c => c.IsVisibleInTree()).ToArray();
        CheckCharacterGrid(characters, buttons, "");
        foreach (var button in buttons)
        {
            string name = button.Character.GetType().Name;
            _report.Check("menu_character_unlocked_" + name, !button.IsLocked, "native unlock all, test profile only");
            // Every drawn frame from the tap on: a character's art must never be drawn as the game laid it out for landscape.
            Func<bool>? unframed = CharacterArtDrawnUnframed(characters);
            int unframedFrames = 0, sampled = 0;
            void Sample()
            {
                sampled++;
                if (unframed!()) unframedFrames++;
            }
            if (unframed != null) RenderingServer.FramePreDraw += Sample;
            try
            {
                await TapMenuAsync(button, "menu_character_" + name);
                if (unframed != null) await WaitFramesAsync(20);
            }
            finally
            {
                if (unframed != null) RenderingServer.FramePreDraw -= Sample;
            }
            if (unframed != null)
                _report.Check("menu_character_art_never_landscape_" + name, unframedFrames == 0,
                    $"frames drawn with unframed art: {unframedFrames}/{sampled}");
            _report.Check("menu_character_selected_" + name, button.IsSelected, "native selection changed");
            await CheckCharacterArtAsync(characters, name, button.IsRandom);
            await CaptureAsync("menu_character_" + name);
        }
        // Diagnostic first: this Back tap is the one that does not close character select at 9:16 (the 9:19.5 flow passes).
        await LogTapDiagnosticsAsync("menu_character_back_before_tap", characters.GetNode<Control>("BackButton"));
        await TapMenuAsync(characters.GetNode<Control>("BackButton"), "menu_character_back");
        await WaitUntilAsync(() => VisibleScreen("NCharacterSelectScreen") is null, "character selection closes", ShortWait);
        if (VisibleScreen("NSingleplayerSubmenu") is { } reopenedModes)
            await TapMenuUntilAsync(reopenedModes.GetNode<Control>("%StandardButton"), "menu_standard_reopen",
                () => VisibleScreen("NCharacterSelectScreen") is not null, "character selection reopens", MediumWait);
        else
            await TapMenuUntilAsync(menu.GetNode<Control>("MainMenuTextButtons/SingleplayerButton"), "menu_singleplayer_reopen",
                () => VisibleScreen("NCharacterSelectScreen") is not null, "character selection reopens", MediumWait);
        characters = VisibleScreen("NCharacterSelectScreen")!;
        var firstCharacter = characters.GetNode<Control>("CharSelectButtons/ButtonContainer").GetChildren()
            .OfType<NCharacterSelectButton>().First(c => c.IsVisibleInTree() && !c.IsRandom);
        await TapMenuAsync(firstCharacter, "menu_select_first_character");
        await CheckCharacterArtAsync(characters, "reopened", false);

        var ascension = characters.GetNode<NAscensionPanel>("%AscensionPanel");
        Control ascensionLeft = ascension.GetNode<Control>("HBoxContainer/LeftArrowContainer/LeftArrow");
        int startingAscension = ascension.Ascension;
        // The test profile remembers the last picked character's ascension; it can be at the maximum, where the increase
        // arrow is disabled (a fixture precondition, not a layout defect). Step down first so the increase can be tapped.
        if (startingAscension >= 10)
        {
            await TapMenuAsync(ascensionLeft, "menu_ascension_below_max");
            startingAscension = ascension.Ascension;
        }
        await TapMenuAsync(ascension.GetNode<Control>("HBoxContainer/RightArrowContainer/RightArrow"), "menu_ascension_increase");
        _report.Check("menu_ascension_increased", ascension.Ascension == startingAscension + 1, $"{startingAscension} → {ascension.Ascension}");
        CheckCharacterGrid(characters, characters.GetNode<Control>("CharSelectButtons/ButtonContainer").GetChildren()
            .OfType<NCharacterSelectButton>().Where(b => b.IsVisibleInTree()).ToArray(), "_after_ascension");
        await CaptureAsync("menu_ascension_one");
        await TapMenuAsync(ascensionLeft, "menu_ascension_restore");
        _report.Check("menu_ascension_restored", ascension.Ascension == startingAscension, $"{ascension.Ascension} (expected {startingAscension})");
        await CaptureAsync("menu_character_select");
        await TapMenuAsync(characters.GetNode<Control>("ConfirmButton"), "menu_embark");
        await WaitUntilAsync(() => NGame.Instance?.CurrentRunNode is not null, "touch menu flow starts a run", LongWait);
        await WaitSecondsAsync(2);
        await CaptureAsync("menu_run_started");
        _report.Pass("native_menu_touch_flow_starts_run");
    }

    // ---- host answers for the menus flow -------------------------------------------------------------

    /// <summary>The singleplayer mode cards: the host keeps them inside its frame in two rows.</summary>
    private partial void CheckModeCardsLayout(Control modes, Control[] cards);

    /// <summary>Blitz/Dread setup: the character roster rows and targets, and Blitz's title and badge below the safe top.</summary>
    private partial void CheckModeSetupLayout(string mode, Control setup, IReadOnlyList<NCharacterSelectButton> roster);

    /// <summary>Character select: the 4+3 grid, ascension flame, info plate, navigation clearance and native focus order.</summary>
    private partial void CheckCharacterGrid(Control characters, IReadOnlyList<NCharacterSelectButton> buttons, string suffix);

    /// <summary>Character select art: cover framing, an independent figure and live native animation, per character.</summary>
    private partial Task CheckCharacterArtAsync(Control characters, string name, bool isRandom);

    /// <summary>
    /// Whether the frame about to be drawn shows character art the host has not framed for portrait yet, or null where the
    /// host has nothing to check (landscape, or a host without per-art framing marks).
    /// </summary>
    private partial Func<bool>? CharacterArtDrawnUnframed(Control characters);
}
