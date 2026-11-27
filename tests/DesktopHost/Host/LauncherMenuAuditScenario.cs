using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.Credits;
using MegaCrit.Sts2.Core.Nodes.Screens.FeedbackScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Timeline;
using STS2Mobile.GameHost;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The launcher host's <c>menu-audit</c>: the same main-menu side screens the launcher's <c>audit-menus</c> proof opens, each one
/// gated by the shared T5 helper (<see cref="TestDriver.CheckTouchTargets"/>) and captured. Unlike the proof, which logs
/// <c>PortraitAudit.DescribeSmall</c> and carries on, an undersized control here fails the run and is named with its size.
/// <para>
/// The tour is the launcher's own (the proof's reflection walk): settings tabs, singleplayer and its five setups, multiplayer
/// and join friend, the compendium family, the timeline (with an epoch inspect, the four unlock screens and the timeline
/// expansion), profile, modding, credits, patch notes, feedback and the abandon popup. Each step's overflow log is kept as a
/// passed step next to its gate so a layout regression is visible even when the targets are big enough.
/// </para>
/// </summary>
internal sealed partial class TestDriver
{
    private const string ScreensNamespace = "MegaCrit.Sts2.Core.Nodes.Screens.";
    // The relic collection's dense icon grid is drawn art like the map's nodes (a tap inspects the relic), so its 24 unit
    // entries are not 44 point targets by design; the same exemption the contract gives map nodes. menu-audit only.
    private const string RelicEntryAllow = "NRelicCollectionEntry";
    // The run history's dense inspect icons (the potion and map-point history entries) are the same drawn-art case: a tap
    // opens the inspected relic/card, not a 44 point action. runhistory step only.
    private static readonly string[] RunHistoryAllow = { "NPotionHolder", "NMapPointHistoryEntry", "NBadge" };
    // The stats screen's rows have no action of their own: a row only shows the game's hover tip (decision 53), the same
    // drawn-art/tip case as the relic grid and the run history's icons. stats step only.
    private static readonly string[] StatsAllow = { "NStatEntry" };

    internal static async Task RunLauncherMenuAuditAsync(TestOptions options, TestReport report, ScreenshotService screenshots,
        VirtualPointer pointer)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Log.Error("Test harness requires a SceneTree.");
            return;
        }

        await new TestDriver(tree, options, report, screenshots, pointer).RunLauncherMenuAuditInternalAsync();
    }

    private async Task RunLauncherMenuAuditInternalAsync()
    {
        await WaitForMainMenuSceneAsync();
        await WaitForMainMenuPreloadAsync();
        if (SaveManager.Instance.CurrentProfileId != TestProfileScope.TestProfileId)
            throw new InvalidOperationException("Refusing to audit a non-test profile.");

        NMainMenu menu = NGame.Instance!.MainMenu!;
        menu.RefreshButtons();
        await WaitSecondsAsync(0.5);

        await AuditOpenedAsync("main-menu", menu, null);

        foreach (string tab in new[] { "General", "Graphics", "Sound", "Input" })
        {
            string step = "settings-" + tab.ToLowerInvariant();
            menu.OpenSettingsMenu();
            await WaitFramesAsync(20);
            Control? settings = ScreenOf("NSettingsScreen");
            if (settings is not null)
            {
                await TapMenuAsync(settings.GetNode<Control>("SettingsTabManager/" + tab), "audit_" + step + "_tab");
                await WaitFramesAsync(10);
            }
            await AuditOpenedAsync(step, settings, settings is null
                ? null
                : async () => await TapMenuAsync(settings.GetNode<Control>("BackButton"), "audit_settings_back"));
            PopAll(menu);
        }

        NSubmenu singleplayer = menu.OpenSingleplayerSubmenu();
        await AuditOpenedAsync("singleplayer", singleplayer, () =>
        {
            // Remember the card the player sees on the mode picker: the compendium's cards must match its drawn width.
            RememberModeCardWidth(singleplayer);
            PopAll(menu);
            return Task.CompletedTask;
        });

        foreach (string screen in new[]
                 {
                     "CharacterSelect.NCharacterSelectScreen", "CustomRun.NCustomRunScreen", "DailyRun.NDailyRunScreen",
                     "Blitz.NBlitzScreen", "Dread.NDreadScreen",
                 })
        {
            string step = ShortName(screen);
            NSubmenu pushed = PushSingleplayer(menu, screen);
            if (step == "characterselect")
            {
                // The picked state: the first character selected, the info plate and confirm shown.
                NCharacterSelectButton? button = FindAll<NCharacterSelectButton>(pushed).FirstOrDefault(b => b.IsVisibleInTree());
                button?.Call("ForceClick");
                await WaitFramesAsync(40);
                await AuditOpenedAsync("characterselect-picked", pushed, () => { PopAll(menu); return Task.CompletedTask; });
            }
            else
            {
                await AuditOpenedAsync(step, pushed, () => { PopAll(menu); return Task.CompletedTask; });
            }
        }

        await AuditOpenedAsync("multiplayer", menu.OpenMultiplayerSubmenu(), null);
        await AuditOpenedAsync("multiplayer-host", Push(menu, "MainMenu.NMultiplayerHostSubmenu"), () => { PopAll(menu); return Task.CompletedTask; });
        menu.Call("OpenMultiplayerSubmenu");
        await AuditOpenedAsync("join-friend", Push(menu, "MainMenu.NJoinFriendScreen"), () => { PopAll(menu); return Task.CompletedTask; });

        NCompendiumSubmenu? compendium = menu.OpenCompendiumSubmenu();
        await AuditOpenedAsync("compendium", compendium, null);
        NSubmenu? library = null;
        if (compendium is not null)
        {
            CheckCompendiumLayout(compendium);
            await TapMenuAsync(compendium.GetNode<Control>("%CardLibraryButton"), "audit_compendium_card_library");
            library = FindAll<Control>(_tree.Root).OfType<NCardLibrary>().LastOrDefault(screen => screen.IsVisibleInTree());
            _report.Check("menu_compendium_card_library_opens", library is not null,
                "tapping Card Library on the compendium opens the native card library");
        }
        if (library is null)
        {
            library = Push(menu, "CardLibrary.NCardLibrary");
            // There is no run at the main menu.
            if (library is NCardLibrary pushed) pushed.Initialize(null!);
        }
        await AuditOpenedAsync("card-library", library, () => { menu.SubmenuStack.Pop(); return Task.CompletedTask; });
        foreach (string screen in new[]
                 {
                     "RelicCollection.NRelicCollection", "PotionLab.NPotionLab", "Bestiary.NBestiary",
                     "StatsScreen.NStatsScreen", "RunHistoryScreen.NRunHistory",
                 })
        {
            string step = ShortName(screen);
            NSubmenu pushed = Push(menu, screen);
            await AuditOpenedAsync(step, pushed, () => { menu.SubmenuStack.Pop(); return Task.CompletedTask; },
                step switch
                {
                    "reliccollection" => new[] { RelicEntryAllow },
                    "runhistory" => RunHistoryAllow,
                    "stats" => StatsAllow,
                    _ => Array.Empty<string>(),
                });
        }
        PopAll(menu);

        NSubmenu timeline = Push(menu, "Timeline.NTimelineScreen");
        await AuditOpenedAsync("timeline", timeline, null);
        NTimelineScreen timelineScreen = NTimelineScreen.Instance;
        NEpochSlot? slot = FindAll<NEpochSlot>(timelineScreen).FirstOrDefault(s => s.IsVisibleInTree());
        if (slot is not null) timelineScreen.OpenInspectScreen(slot, false);
        await WaitFramesAsync(40);
        Control? inspect = timelineScreen.GetNodeOrNull<Control>("%EpochInspectScreen");
        await AuditOpenedAsync("epoch-inspect", inspect, () =>
        {
            if (inspect is not null) AccessTools.Method(inspect.GetType(), "Close")?.Invoke(inspect, null);
            return Task.CompletedTask;
        });
        await WaitFramesAsync(30);

        await AuditUnlockAsync("unlock-cards", () =>
            timelineScreen.QueueCardUnlock(ModelDb.AllCards.Take(3).ToList()));
        await AuditUnlockAsync("unlock-relics", () =>
            timelineScreen.QueueRelicUnlock(ModelDb.AllRelics.Take(3).ToList()));
        await AuditUnlockAsync("unlock-misc", () =>
            timelineScreen.QueueMiscUnlock("A new mode is now available. This line stands in for the game's unlock text, long enough to wrap."));
        await AuditUnlockAsync("unlock-character", () =>
        {
            EpochModel epoch = EpochModel.Get(EpochModel.GetId(typeof(MegaCrit.Sts2.Core.Timeline.Epochs.Silent1Epoch)));
            timelineScreen.QueueCharacterUnlock<MegaCrit.Sts2.Core.Models.Characters.Silent>(epoch);
        });
        await AuditUnlockAsync("unlock-timeline", () =>
        {
            var shown = new HashSet<string>(FindAll<NEpochSlot>(timelineScreen)
                .Where(s => s.model is not null).Select(s => s.model.Id));
            List<EpochSlotData> picks = EpochModel.AllEpochIds.Where(id => !shown.Contains(id)).Take(2)
                .Select(id => new EpochSlotData(EpochModel.Get(id), EpochSlotState.NotObtained)).ToList();
            timelineScreen.QueueTimelineExpansion(picks);
        });
        PopAll(menu);

        menu.OpenProfileScreen();
        await AuditOpenedAsync("profile", menu.SubmenuStack.Peek(), () => { PopAll(menu); return Task.CompletedTask; });
        await AuditOpenedAsync("modding", Push(menu, "ModdingScreen.NModdingScreen"), () => { PopAll(menu); return Task.CompletedTask; });

        NCreditsScreen? credits = NCreditsScreen.Create();
        if (credits is not null) NModalContainer.Instance?.Add(credits);
        await AuditOpenedAsync("credits", credits, () => { NModalContainer.Instance?.Clear(); return Task.CompletedTask; });

        menu.PatchNotesScreen.Open();
        await AuditOpenedAsync("patch-notes", menu.PatchNotesScreen, () =>
        {
            AccessTools.Method(menu.PatchNotesScreen.GetType(), "Close")?.Invoke(menu.PatchNotesScreen, null);
            return Task.CompletedTask;
        });

        NSendFeedbackScreen feedback = NGame.Instance.GetOrCreateFeedbackScreen();
        feedback.Open();
        await AuditOpenedAsync("feedback", feedback, () =>
        {
            AccessTools.Method(feedback.GetType(), "Close")?.Invoke(feedback, null);
            return Task.CompletedTask;
        });

        Control? abandon = NAbandonRunConfirmPopup.Create(menu);
        if (abandon is not null) NModalContainer.Instance?.Add(abandon);
        await AuditOpenedAsync("abandon-popup-menu", abandon, () => { NModalContainer.Instance?.Clear(); return Task.CompletedTask; });

        _report.Pass("menu_audit_completed", $"profile={SaveManager.Instance.CurrentProfileId}");
    }

    private async Task AuditUnlockAsync(string step, Action queue)
    {
        NTimelineScreen? timeline = NTimelineScreen.Instance;
        if (timeline is null)
        {
            _report.Fail("t5_" + step, "timeline is not open, cannot queue " + step);
            return;
        }
        try
        {
            queue();
            timeline.OpenQueuedScreen();
        }
        catch (Exception e)
        {
            _report.Fail("audit_" + step + "_open", e.GetBaseException().Message);
            return;
        }
        await WaitFramesAsync(60);
        Control? screen = FindAll<Control>(_tree.Root).LastOrDefault(c => c.IsVisibleInTree()
            && c.GetType().Namespace == ScreensNamespace + "Timeline.UnlockScreens");
        await AuditOpenedAsync(step, screen, async () =>
        {
            if (screen?.GetNodeOrNull<NClickableControl>("ConfirmButton") is { } confirm)
                confirm.EmitSignal(NClickableControl.SignalName.Released, confirm);
            else if (screen is not null)
                await (Task)AccessTools.Method(screen.GetType(), "Close")!.Invoke(screen, null)!;
            await WaitFramesAsync(30);
        });
    }

    /// <summary>Measures and captures an opened screen, then closes it when <paramref name="close"/> is given.</summary>
    private async Task AuditOpenedAsync(string step, Node? screen, Func<Task>? close, params string[] extraAllow)
    {
        await WaitFramesAsync(10);
        if (screen is null || !GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree() || !IsShown(screen))
        {
            _report.Fail("t5_" + step, $"{step}: screen not found, nothing to gate");
        }
        else
        {
            CheckTouchTargets("t5_" + step, step, screen,
                new[] { NativeBackAllow, DropdownRowsAllow }.Concat(extraAllow).ToArray());
            _report.Pass("audit_" + step + "_overflow", PortraitAudit.Describe(_tree.Root));
            var outside = PortraitAudit.OffCanvasInteractive(_tree.Root);
            _report.Check("audit_" + step + "_in_frame", outside.Count == 0,
                outside.Count == 0 ? "interactive content inside the canvas" : string.Join("; ", outside.Take(10)));
            await CaptureAsync("menu_audit_" + step);
        }
        if (close is null) return;
        try
        {
            await close();
        }
        catch (Exception e)
        {
            _report.Fail("audit_" + step + "_close", e.GetBaseException().Message);
        }
        await WaitFramesAsync(10);
    }

    private static bool IsShown(Node node) => node is not CanvasItem item || item.IsVisibleInTree();

    private static string ShortName(string screen)
    {
        string name = screen[(screen.IndexOf(".N", StringComparison.Ordinal) + 2)..];
        return name.Replace("Screen", "").ToLowerInvariant();
    }

    private static Type ScreenType(string name) =>
        typeof(NGame).Assembly.GetType(ScreensNamespace + name)
        ?? throw new InvalidOperationException("missing screen type " + ScreensNamespace + name);

    private static NSubmenu Push(NMainMenu menu, string name)
    {
        NSubmenu submenu = menu.SubmenuStack.GetSubmenuType(ScreenType(name));
        menu.SubmenuStack.Push(submenu);
        return submenu;
    }

    private static NSubmenu PushSingleplayer(NMainMenu menu, string name)
    {
        // Initialize before the push, in the order the launcher's audit-menus proof uses: pushing first puts the screen in
        // the tree (and runs its own _Ready layout) before it has a mode, which the game does not expect.
        NSubmenu submenu = menu.SubmenuStack.GetSubmenuType(ScreenType(name));
        submenu.Call("InitializeSingleplayer");
        menu.SubmenuStack.Push(submenu);
        return submenu;
    }

    private void PopAll(NMainMenu menu)
    {
        for (int i = 0; i < 8 && menu.SubmenuStack.SubmenusOpen; i++) menu.SubmenuStack.Pop();
        NModalContainer.Instance?.Clear();
    }

    private Control? ScreenOf(string typeName) =>
        FindAll<Control>(_tree.Root).LastOrDefault(c => c.GetType().Name == typeName && c.IsVisibleInTree());
}
