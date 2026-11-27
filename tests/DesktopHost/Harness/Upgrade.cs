using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// <c>upgrade</c>: the native upgrade selector over a real rest site, its three-column grid and View Upgrades box, the
/// scrolled deck, a single-card comparison, Back and Confirm. The grid's columns and card width, the toggle's place and
/// touch size, the preview's fit and Back's place above the toggle are the host's own phone geometry, in
/// LauncherCardGridChecks.cs (three columns on a narrower canvas, 127-unit targets, the footer under the grid).
/// </summary>
internal sealed partial class TestDriver
{
    private async Task RunUpgradeScenarioAsync(RunState runState)
    {
        // Show the upgrade selector over a real rest site. The map screen
        // covers overlays while it is open, which would hide the screen.
        await RunManager.Instance.EnterRoomDebug(RoomType.RestSite, showTransition: false);
        await WaitUntilAsync(
            () => NRestSiteRoom.Instance is { } room
                  && GodotObject.IsInstanceValid(room)
                  && room.IsInsideTree(),
            "rest site for upgrade screen",
            MediumWait);
        await WaitFramesAsync(6);

        Player player = runState.Players[0];
        List<CardModel> cards = player.Deck.Cards.Take(8).ToList();
        if (cards.Count < 2)
        {
            _report.Skip("upgrade", "deck does not have at least two cards");
            return;
        }

        if (IsPortraitHost())
        {
            var original = cards.ToArray();
            for (int i = cards.Count; i < 28; i++)
            {
                var clone = runState.CloneCard(original[i % original.Length]);
                player.Deck.AddInternal(clone, silent: true);
                cards.Add(clone);
            }
        }

        NOverlayStack.Instance?.Clear();
        await WaitFramesAsync(4);

        var prefs = new CardSelectorPrefs(CardSelectorPrefs.UpgradeSelectionPrompt, 1) { Cancelable = true };
        NDeckUpgradeSelectScreen? screen = NDeckUpgradeSelectScreen.ShowScreen(cards, prefs, runState);
        if (screen is null)
        {
            _report.Fail("upgrade_screen_opens", "ShowScreen returned null");
            return;
        }

        Log.Info($"[TEST] upgrade state after ShowScreen: inside={screen.IsInsideTree()} visible={screen.Visible} parent={screen.GetParent()?.Name} size={screen.Size} modulate={screen.Modulate} stack={NOverlayStack.Instance?.ScreenCount}");
        await WaitUntilAsync(() => screen.IsInsideTree() && screen.Visible, "upgrade screen", MediumWait);
        await WaitSecondsAsync(0.6);

        List<NGridCardHolder> holders = FindAll<NGridCardHolder>(screen)
            .Where(holder => holder.IsVisibleInTree())
            .ToList();
        _report.Check("upgrade_screen_loaded", screen.IsInsideTree(), "upgrade screen is present");
        _report.Check("upgrade_has_cards", holders.Count > 0, $"cards={holders.Count}");
        NCardGrid grid = screen.GetNode<NCardGrid>("%CardGrid");
        CheckUpgradeCardsScaled(grid, holders);

        if (IsPortraitHost())
        {
            CheckUpgradeColumns(grid, holders);
            Control upgrades = screen.GetNode<Control>("ViewUpgrades");
            CheckUpgradeToggle(upgrades, grid.GetGlobalRect());
            bool before = grid.IsShowingUpgrades;
            await _pointer.TapAsync(CenterOf(screen.GetNode<Control>("%Upgrades")));
            await WaitSecondsAsync(0.3);
            _report.Check("upgrade_toggle_changes_preview", grid.IsShowingUpgrades != before, $"preview={grid.IsShowingUpgrades}");
        }
        await CaptureAsync("upgrade_01_grid");
        var comparison = screen.GetNode<Control>("%UpgradeSinglePreviewContainer");
        if (IsPortraitHost())
            await CheckDeckSelectionScrollAsync("upgrade", grid, screen.GetNode<Control>("%BottomText"),
                comparison, screen.GetNode<Control>("%UpgradeMultiPreviewContainer"));
        holders = grid.CurrentlyDisplayedCardHolders.Where(holder => holder.IsVisibleInTree()
            && grid.GetGlobalRect().HasPoint(CenterOf(holder.Hitbox))).ToList();
        await TestCardHoverScaleAsync("upgrade", holders);
        var candidate = holders.First();
        var confirm = comparison.GetNode<Control>("Confirm");
        await _pointer.TapAsync(CenterOf(candidate.Hitbox));
        await WaitUntilAsync(() => comparison.IsVisibleInTree(), "native upgrade comparison", ShortWait);
        await WaitSecondsAsync(0.8);
        if (IsPortraitHost())
        {
            CheckScreenBacks("upgrade_preview", screen);
            CheckUpgradePreview(screen, comparison, confirm);
        }
        await CaptureAsync("upgrade_02_comparison");
        await _pointer.TapAsync(CenterOf(comparison.GetNode<Control>("Cancel")));
        await WaitUntilAsync(() => !comparison.Visible, "upgrade preview cancel", ShortWait);
        await WaitSecondsAsync(0.85);
        Control close = screen.GetNode<Control>("%Close");
        if (IsPortraitHost())
            CheckUpgradeBackAboveToggle(close, screen.GetNode<Control>("ViewUpgrades"));
        await _pointer.TapAsync(CenterOf(close));
        await WaitUntilAsync(() => !GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree(), "upgrade Back returns to rest site", ShortWait);
        _report.Pass("upgrade_back_tap_closes_selector");

        screen = NDeckUpgradeSelectScreen.ShowScreen(cards, prefs, runState);
        await WaitSecondsAsync(0.85);
        comparison = screen.GetNode<Control>("%UpgradeSinglePreviewContainer");
        confirm = comparison.GetNode<Control>("Confirm");
        candidate = FindAll<NGridCardHolder>(screen).First(holder => screen.GetNode<Control>("%CardGrid").GetGlobalRect().HasPoint(CenterOf(holder.Hitbox)));
        await _pointer.TapAsync(CenterOf(candidate.Hitbox));
        await WaitUntilAsync(() => comparison.IsVisibleInTree(), "upgrade comparison reopened", ShortWait);
        await WaitSecondsAsync(0.85);
        await _pointer.TapAsync(CenterOf(confirm));
        await WaitUntilAsync(() => !GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree(), "upgrade Confirm completes native selection", ShortWait);
        _report.Pass("upgrade_confirm_completes_selection");
    }
}
