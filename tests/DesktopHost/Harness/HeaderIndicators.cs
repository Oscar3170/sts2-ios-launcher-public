using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// <c>header-indicators</c>: the run header with the native indicators an ordinary run never shows - the ascension badge,
/// run-modifier chips and the achievement lock. The run is a custom singleplayer run at max ascension holding three
/// modifiers, which is what makes all three native indicators visible at once; the Dread icon is then applied through the
/// game's own icon config. Both portrait hosts hide the native chips by design (the phone row has no room for them), so the
/// scenario asserts that hide and leaves their native landscape presentation to the native bar. The launcher's top-row
/// geometry is a host partial (LauncherHeaderChecks.cs).
/// </summary>
internal sealed partial class TestDriver
{
    private const int HeaderIndicatorAscension = 10;

    // The preferred ascension the test profile had for the fixture character before this run. The game writes the run's
    // ascension into that preference when the lobby begins (StartRunLobby.BeginRunForAllPlayers), and the profile is shared
    // by every scenario run: leaving 10 there starts the next standard scenario at ascension 10 (one potion slot fewer, so
    // header-inventory saw 11 holders instead of 12). Restored once the fixture run is up.
    private ModelId? _headerIndicatorCharacter;
    private int _headerIndicatorPreferredAscension;
    private bool _headerIndicatorPreferredSaved;

    /// <summary>
    /// Starts the indicator run through the native Custom Run screen. The screen's singleplayer ascension control caps
    /// itself at 0, but the run it starts reads the lobby's ascension and takes it verbatim (the multiplayer host uses the
    /// same field to start ascension runs), so the fixture sets the panel to the maximum before pressing Embark. The
    /// screen's Confirm button sits a few units off the canvas at 9:16, so Embark is the handler the button releases into
    /// rather than a tap on its touch box: this scenario is the header's, not the Custom Run screen's.
    /// </summary>
    private async Task LaunchHeaderIndicatorsRunAsync(NMainMenu menu)
    {
        if (SaveManager.Instance.CurrentProfileId != TestProfileScope.TestProfileId)
            throw new InvalidOperationException("Refusing to unlock a non-test profile.");

        new UnlockConsoleCmd().Process(null, new[] { "all" });
        SaveManager.Instance.MarkFtueAsComplete("ascension_singleplayer_ftue");
        SaveManager.Instance.MarkFtueAsComplete("blitz_disclaimer");
        SaveManager.Instance.MarkFtueAsComplete("dread_disclaimer");
        menu.RefreshButtons();
        await WaitSecondsAsync(0.5);

        NSingleplayerSubmenu modes = menu.OpenSingleplayerSubmenu();
        await WaitUntilAsync(() => modes.IsVisibleInTree(), "custom run's mode picker", ShortWait);
        // The mode picker's own Custom action (internal to the game; the Custom button is its signal wrapper).
        MethodInfo openCustom = AccessTools.Method(typeof(NSingleplayerSubmenu), "OpenCustomScreen")
            ?? throw new InvalidOperationException("NSingleplayerSubmenu.OpenCustomScreen not found.");
        openCustom.Invoke(modes, new object?[] { null });
        await WaitUntilAsync(
            () => FindAll<NCustomRunScreen>(_tree.Root).Any(candidate => candidate.IsVisibleInTree()),
            "custom run screen", MediumWait);
        NCustomRunScreen screen = FindAll<NCustomRunScreen>(_tree.Root)
            .FirstOrDefault(candidate => candidate.IsVisibleInTree())
            ?? throw new InvalidOperationException("Custom run screen did not open.");
        await WaitUntilAsync(() => screen.Lobby.Players.Count == 1 && screen.Lobby.LocalPlayer.character is not null,
            "custom run lobby character", MediumWait);
        _headerIndicatorCharacter = screen.Lobby.LocalPlayer.character!.Id;
        _headerIndicatorPreferredAscension =
            SaveManager.Instance.Progress.GetOrCreateCharacterStats(_headerIndicatorCharacter).PreferredAscension;
        _headerIndicatorPreferredSaved = true;

        // Three real modifiers with no start-of-run hooks, ticked through the screen's own list so the lobby receives them.
        ModifierModel[] modifiers =
        {
            ModelDb.Modifier<Midas>().ToMutable(),
            ModelDb.Modifier<Flight>().ToMutable(),
            ModelDb.Modifier<Vintage>().ToMutable(),
        };
        var modifierList = screen.GetNode<NCustomRunModifiersList>("%ModifiersList");
        Control content = modifierList.GetNode<Control>("ScrollContainer/Mask/Content");
        NRunModifierTickbox[] rows = content.GetChildren().OfType<NRunModifierTickbox>().ToArray();
        NRunModifierTickbox? lastRow = rows.LastOrDefault();
        bool portrait = IsPortraitHost();
        if (portrait)
        {
            // Portrait: the host's own touch scrolling must reach the last modifier inside the list's viewport.
            float contentY = content.Position.Y;
            // The list is much longer than one drag: keep dragging (bounded) until the last modifier is in the viewport.
            for (int drag = 0; drag < 8 && rows.Length > 1; drag++)
            {
                Rect2 listRect = modifierList.GetGlobalRect();
                if (lastRow is not null && listRect.Grow(2f).HasPoint(CenterOf(lastRow))) break;
                float before = content.Position.Y;
                await _pointer.DragAsync(listRect.GetCenter() + new Vector2(0f, 200f), listRect.GetCenter() - new Vector2(0f, 200f), steps: 12);
                await WaitSecondsAsync(0.3);
                if (Mathf.Abs(content.Position.Y - before) < 0.5f) break;
            }
            NRunModifierTickbox? last = lastRow;
            bool scrolled = content.Position.Y < contentY - 5f;
            bool fits = content.Size.Y <= modifierList.GetGlobalRect().Size.Y + 1f;
            bool lastShown = last is not null && last.IsVisibleInTree()
                && modifierList.GetGlobalRect().Grow(2f).HasPoint(CenterOf(last));
            bool lastTickable = last is not null && last.MouseFilter != Control.MouseFilterEnum.Ignore
                && Mathf.Min(HostTouchTargetBox(last).Size.X, HostTouchTargetBox(last).Size.Y)
                    >= HostTouchTargetMinimum() - 0.5f;
            _report.Check("header_indicators_custom_list_scrolls", (scrolled || fits) && lastShown && lastTickable,
                $"contentY {contentY:F1}->{content.Position.Y:F1} fits={fits} last={last?.Modifier?.Id.Entry} " +
                $"visible={lastShown} target={(last is null ? default : HostTouchTargetBox(last))}");
        }
        else
        {
            // Native scrolling uses a separate mask and a percentage-based NScrollbar, not a Godot ScrollContainer.
            var nativeList = modifierList.GetNodeOrNull<NScrollableContainer>("ScrollContainer");
            Control? mask = nativeList?.GetNodeOrNull<Control>("Mask");
            NScrollbar? bar = nativeList?.Scrollbar;
            bool nativeStructure = mask is not null && content.GetParent() == mask && bar?.GetParent() == nativeList;
            float before = content.Position.Y;
            if (nativeStructure && bar is { } scrollbar && scrollbar.IsVisibleInTree())
            {
                Rect2 track = scrollbar.GetGlobalRect();
                await _pointer.PressAsync(new Vector2(track.GetCenter().X, track.End.Y - 1f));
                await WaitFramesAsync(5);
                await _pointer.ReleaseAsync(new Vector2(track.GetCenter().X, track.End.Y - 1f));
                await WaitSecondsAsync(0.5);
            }
            bool lastVisible = lastRow is not null && mask is not null
                && mask.GetGlobalRect().Intersects(lastRow.GetGlobalRect());
            _report.Check("header_indicators_custom_list_native", nativeStructure && lastVisible && content.Position.Y < before - 1f,
                $"scroll={nativeList?.Name} mask={mask?.GetGlobalRect()} scrollbar={bar?.Value:F1} " +
                $"contentY={before:F1}->{content.Position.Y:F1} rows={rows.Length} lastVisible={lastVisible}");
        }

        modifierList.SetTickedModifiers(modifiers);
        screen.GetNode<NAscensionPanel>("%AscensionPanel").SetAscensionLevel(HeaderIndicatorAscension);
        await WaitFramesAsync(4);
        List<ModifierModel> ticked = modifierList.GetModifiersTickedOn();
        _report.Check("header_indicators_fixture_lobby", screen.Lobby.Ascension == HeaderIndicatorAscension
                && ticked.Count == modifiers.Length,
            $"ascension={screen.Lobby.Ascension} modifiers={string.Join(",", ticked.Select(m => m.Id.Entry))}");

        _report.Pass("header_indicators_fixture_embark", $"{screen.Lobby.LocalPlayer.character!.Id.Entry} ascension={HeaderIndicatorAscension}");
        Control confirm = screen.GetNode<Control>("ConfirmButton");
        await WaitUntilAsync(() => confirm.IsVisibleInTree() && (confirm is not NButton button || button.IsEnabled),
            "custom run Confirm visible and enabled", ShortWait);
        Rect2 confirmBox = confirm.GetGlobalRect();
        bool confirmEnabled = confirm is not NButton confirmButton || confirmButton.IsEnabled;
        if (portrait)
        {
            Rect2 confirmTarget = HostTouchTargetBox(confirm);
            _report.Check("header_indicators_custom_confirm_in_frame",
                PortraitSafeRect().Grow(1f).Encloses(confirmBox)
                && Mathf.Min(confirmTarget.Size.X, confirmTarget.Size.Y) >= HostTouchTargetMinimum() - 0.5f,
                $"rect={confirmBox} target={confirmTarget} safe={PortraitSafeRect()}");
        }
        else
        {
            // Native landscape keeps the game's own button placement, including its overhang past the right edge; what
            // must hold is that it is enabled and reachable by a finger on the canvas.
            Rect2 landscapeCanvas = _tree.Root.GetVisibleRect();
            _report.Check("header_indicators_custom_confirm_reachable",
                confirmEnabled && landscapeCanvas.Intersects(confirmBox) && landscapeCanvas.HasPoint(confirmBox.GetCenter()),
                $"rect={confirmBox} canvas={landscapeCanvas} enabled={confirmEnabled} centre={confirmBox.GetCenter()}");
        }
        await _pointer.TapAsync(CenterOf(confirm));
        bool started = await WaitOrFalseAsync(() => NRun.Instance is not null, MediumWait);
        _report.Check("header_indicators_custom_confirm_by_tap", started, started
            ? "the custom run started from a tap on Confirm"
            : "no run started within the wait after tapping Confirm");
    }

    private async Task RunHeaderIndicatorsAsync(RunState state)
    {
        RestoreHeaderIndicatorProfile();
        _report.Check("header_indicators_fixture_run", state.GameMode == GameMode.Standard && state.IsCustomGame
                && state.AscensionLevel == HeaderIndicatorAscension && state.Modifiers.Count == 3,
            $"mode={state.GameMode} custom={state.IsCustomGame} ascension={state.AscensionLevel} " +
            $"modifiers={string.Join(",", state.Modifiers.Select(m => m.Id.Entry))}");
        _report.Check("header_indicators_fixture_locked", GameModeHelper.AreAchievementsAndEpochsLockedForRun(state),
            "the run reports its achievements and epochs locked (the native lock's condition)");

        var topBar = NRun.Instance!.GlobalUi.TopBar;
        TextureRect? ascension = topBar.GetNodeOrNull<TextureRect>("%AscensionIcon");
        Label? number = topBar.GetNodeOrNull<Label>("%AscensionLabel");
        TextureRect? lockIcon = topBar.GetNodeOrNull<TextureRect>("%AchievementLock");
        Control? modifierBox = topBar.GetNodeOrNull<Control>("%Modifiers");
        Control[] chips = IndicatorChips(modifierBox);

        _report.Check("header_indicators_ascension_icon", ascension is not null && ascension.IsVisibleInTree()
                && ascension.Texture is not null,
            $"visible={ascension?.IsVisibleInTree()} texture={ascension?.Texture?.ResourcePath}");
        _report.Check("header_indicators_ascension_number", number is not null && number.IsVisibleInTree()
                && number.Text.Trim() == HeaderIndicatorAscension.ToString(),
            $"text='{number?.Text}' visible={number?.IsVisibleInTree()}");
        _report.Check("header_indicators_achievement_lock", lockIcon is not null && lockIcon.IsVisibleInTree()
                && lockIcon.Texture is not null,
            $"visible={lockIcon?.IsVisibleInTree()} texture={lockIcon?.Texture?.ResourcePath}");
        _report.Check("header_indicators_modifier_chip_count", chips.Length == state.Modifiers.Count,
            $"native chips={chips.Length} run modifiers={state.Modifiers.Count}");

        // Both portrait headers hide the native chips; a landscape run keeps the bar's own presentation.
        bool chipsShown = modifierBox is not null && modifierBox.IsVisibleInTree()
            && chips.Length > 0 && chips.All(chip => chip.IsVisibleInTree());
        if (IsPortraitHost())
            _report.Check("header_indicators_modifier_chips_hidden", !chipsShown,
                $"containerVisible={modifierBox?.IsVisibleInTree()} chipsVisible={chips.Count(chip => chip.IsVisibleInTree())}/{chips.Length}");
        else
            _report.Check("header_indicators_modifier_chips_native", chipsShown,
                $"containerVisible={modifierBox?.IsVisibleInTree()} chipsVisible={chips.Count(chip => chip.IsVisibleInTree())}/{chips.Length}");

        await EnterCombatAsync();
        await WaitSecondsAsync(1);
        CheckHeaderIndicatorLayout(topBar, state, "ascension");
        await CaptureAsync("header_indicators_top_row");

        // Dread styling on the same badge: Dread runs show the dread icon instead of the ascension flame and dim the bar
        // through a shader; only the icon's texture and colours change, so the row geometry must hold with it too.
        string? ascensionPath = ascension?.Texture?.ResourcePath;
        MethodInfo applyIcon = AccessTools.Method(topBar.GetType(), "ApplyAscensionIconConfig")
            ?? throw new InvalidOperationException("NTopBar.ApplyAscensionIconConfig not found.");
        applyIcon.Invoke(topBar, new object?[] { new DreadIconConfig(), null });
        await WaitFramesAsync(2);
        string dreadPath = new DreadIconConfig().Texture?.ResourcePath ?? "";
        _report.Check("header_indicators_dread_icon", ascension is not null && ascension.IsVisibleInTree()
                && number is not null && !number.Visible && ascension.Texture?.ResourcePath == dreadPath
                && dreadPath != ascensionPath,
            $"icon={ascension?.Texture?.ResourcePath} expected={dreadPath} numberVisible={number?.Visible}");
        CheckHeaderIndicatorLayout(topBar, state, "dread");
        await CaptureAsync("header_indicators_dread_icon");
    }

    /// <summary>
    /// Puts the fixture character's preferred ascension back after the fixture run started. Only this scenario forces an
    /// ascension in the test profile; every later standard run in the same profile would otherwise start at it.
    /// </summary>
    private void RestoreHeaderIndicatorProfile()
    {
        if (!_headerIndicatorPreferredSaved || _headerIndicatorCharacter is not { } character) return;
        _headerIndicatorPreferredSaved = false;
        CharacterStats stats = SaveManager.Instance.Progress.GetOrCreateCharacterStats(character);
        if (stats.PreferredAscension != _headerIndicatorPreferredAscension)
        {
            stats.PreferredAscension = _headerIndicatorPreferredAscension;
            SaveManager.Instance.SaveProgressFile();
        }
        _report.Pass("header_indicators_profile_restored",
            $"{character.Entry} preferred ascension {HeaderIndicatorAscension}->{stats.PreferredAscension} after the fixture run started");
    }

    private static Control[] IndicatorChips(Control? container) =>
        container?.GetChildren().OfType<Control>().Where(child => !child.IsQueuedForDeletion()).ToArray() ?? [];

    // Implemented by the launcher only (LauncherHeaderChecks.cs): the stone header's top row, the safe cap and the
    // potion row's clearance around the ascension badge, the lock and the (hidden) modifier chips. A host without the
    // stone header does not get this call.
    partial void CheckHeaderIndicatorLayout(Control topBar, RunState state, string phase);
}
