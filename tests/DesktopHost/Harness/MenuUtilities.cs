using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The <c>menu-utilities</c> scenario: the profile button, the patch notes button and the mod status in the main menu's
/// corners, opened and closed by touch, plus the native hover that reveals the mod details. The flow and step names are
/// shared; the layout answers (corners, sizes, the mod status' place and the button reflow) are in LauncherMenuChecks.cs.
/// </summary>
internal sealed partial class TestDriver
{
    private async Task RunMenuUtilitiesAsync()
    {
        var menu = NGame.Instance!.MainMenu!;
        var profile = menu.GetNode<Control>("%ChangeProfileButton");
        var profileRow = profile.GetNode<Control>("HBoxContainer");
        var notes = menu.GetNode<Control>("%PatchNotesButton");
        var modded = menu.GetNode<Label>("%ModdedWarning");
        var details = menu.GetNode<Control>("%ModWarningContainer");
        await CheckMenuUtilitiesLayoutAsync(menu, profile, profileRow, notes, modded);
        CheckTouchTargets("t5_main_menu_utilities", "main menu", menu, NativeBackAllow);

        var mouse = new VirtualPointer(_tree, PointerMode.Mouse);
        for (int attempt = 0; attempt < 3 && profile.Scale.X <= 1.01f; attempt++)
        {
            if (attempt > 0) Log.Warn("[TEST] native profile hover did not take; moving the pointer onto it again");
            await mouse.HoverAsync(CenterOf(profile) + new Vector2(attempt, 0f));
            await WaitSecondsAsync(attempt == 0 ? 0.35 : 0.5);
        }
        CheckMenuProfileHover(profile, profileRow);
        Rect2 status = modded.GetGlobalRect();
        for (int attempt = 0; attempt < 3 && !details.IsVisibleInTree(); attempt++)
        {
            if (attempt > 0) Log.Warn("[TEST] native mod details did not open on hover; moving the pointer onto the status again");
            await mouse.HoverAsync(new Vector2(status.End.X - 12f, status.GetCenter().Y) + new Vector2(0f, attempt));
            _ = await WaitOrFalseAsync(details.IsVisibleInTree, TimeSpan.FromSeconds(attempt == 0 ? 1.5 : 3));
        }
        await WaitUntilAsync(details.IsVisibleInTree, "native mod details on hover", ShortWait);
        CheckMenuModDetails(details, modded);
        await CaptureAsync("menu_utilities_mod_details");
        await mouse.HoverAsync(PortraitSafeRect().GetCenter());
        await WaitUntilAsync(() => !details.IsVisibleInTree(), "native mod details hide on leave", ShortWait);

        await TapMenuAsync(profile, "menu_profile_open");
        await WaitUntilAsync(() => VisibleScreen("NProfileScreen") is not null, "native profile picker", ShortWait);
        await TapMenuAsync(VisibleScreen("NProfileScreen")!.GetNode<Control>("BackButton"), "menu_profile_back");
        await WaitUntilAsync(() => VisibleScreen("NProfileScreen") is null, "native profile picker closed", ShortWait);
        await TapMenuAsync(notes, "menu_release_notes_open");
        await WaitUntilAsync(() => menu.PatchNotesScreen.IsOpen, "native release notes", ShortWait);
        await TapMenuAsync(menu.PatchNotesScreen.GetNode<Control>("%BackButton"), "menu_release_notes_back");
        await WaitUntilAsync(() => !menu.PatchNotesScreen.IsOpen, "native release notes closed", ShortWait);
        _report.Pass("menu_utility_native_open_and_back");
    }

    // Host answer: where the profile/notes controls sit, that they are 44 point targets inside the frame, that the mod status
    // does not move the menu column and stays in its corner.
    private partial Task CheckMenuUtilitiesLayoutAsync(NMainMenu menu, Control profile, Control profileRow, Control notes, Label modded);

    // Host answer: the profile button's own hover growth (the launcher keeps native).
    private partial void CheckMenuProfileHover(Control profile, Control profileRow);

    // Host answer: the mod details panel stays inside the frame, above the status.
    private partial void CheckMenuModDetails(Control details, Label modded);
}
