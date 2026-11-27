using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// Test-only: while <see cref="Active"/>, <c>NGame.ApplyDisplaySettings</c> is skipped, so choosing another display, aspect
/// ratio or resolution from a settings dropdown records the preference without moving the live test window (the same
/// separation <see cref="TestWindowScope"/> makes for the startup size). The scenario restores every touched value afterwards.
/// </summary>
[HarmonyPatch(typeof(NGame), nameof(NGame.ApplyDisplaySettings))]
internal static class SettingsDisplaySaveGuard
{
    internal static bool Active;

    [HarmonyPrefix]
    private static bool Prefix() => !Active;
}

/// <summary>
/// The <c>settings-deep</c> scenario: the parts of Settings and the pause menu that live behind a dropdown or a confirmation,
/// and the card pile screens the run's rail opens.
/// <para>
/// On the launcher host the Graphics tab still carries the game's desktop rows (the host deliberately does not run
/// <c>PhoneGraphics</c>, which on the phone replaces them with the battery/portrait rows), so those native dropdowns are
/// opened here: their list must stay in the safe frame, never taller than the rows area it opens in, every row a finger's
/// size, scroll all the way to the last entry and dismiss. The display, windowed-resolution and aspect-ratio handlers call
/// <c>NGame.ApplyDisplaySettings</c>, which would resize the live test window; <see cref="SettingsDisplaySaveGuard"/> stands
/// in for that call while the last entry is taken, so every list can be driven to its end and the touched values are put
/// back afterwards.
/// </para>
/// <para>
/// The pause menu's Compendium opens and comes back, Give Up opens its native confirmation and is cancelled (the run must
/// stay paused, in the same fight and not game over), and Save &amp; Quit is not tapped: v0.113.1 has no confirmation for it
/// (<c>NPauseMenu.OnSaveAndQuitButtonPressed</c> calls <c>CloseToMenu</c> directly), so a tap would leave the run. The draw
/// and discard pile screens are opened with 0, 1 and 30 cards in the run's capstone container: three columns, the close
/// button reachable, the info line on screen and the list scrolling. Step names are <c>settings_deep_*</c>; the captures are
/// <c>settings_deep_*.png</c>.
/// </para>
/// </summary>
internal sealed partial class TestDriver
{
    /// <summary>How many times a dropdown list is dragged before the last entry must be in view.</summary>
    private const int DropdownScrollPasses = 4;

    // NDropdown's scene members and CardPile's list are not public in the shipped assembly (the launcher host compiles against
    // it unpublicized), so they are read by name once, like the settings layout itself does.
    private static readonly FieldInfo? DropdownListField = AccessTools.Field(typeof(NDropdown), "_dropdownContainer");
    private static readonly FieldInfo? DropdownItemsField = AccessTools.Field(typeof(NDropdown), "_dropdownItems");
    private static readonly FieldInfo? DropdownLabelField = AccessTools.Field(typeof(NDropdown), "_currentOptionLabel");
    private static readonly FieldInfo? DropdownOpenField = AccessTools.Field(typeof(NDropdown), "_isOpen");
    private static readonly PropertyInfo? DropdownOpenProperty = typeof(NDropdown).GetProperty("IsOpen",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly FieldInfo? CardPileCardsField = AccessTools.Field(typeof(CardPile), "_cards");

    private static Control? DropdownListOf(NDropdown dropdown) => DropdownListField?.GetValue(dropdown) as Control;
    private static Control? DropdownItemsOf(NDropdown dropdown) => DropdownItemsField?.GetValue(dropdown) as Control;
    private static string DropdownLabelOf(NDropdown dropdown) =>
        DropdownLabelField?.GetValue(dropdown) is Label label ? label.Text : "";
    private static bool DropdownOpen(NDropdown dropdown) =>
        DropdownOpenProperty?.GetValue(dropdown) is bool open ? open
        : DropdownOpenField?.GetValue(dropdown) is bool field && field;
    private static List<CardModel>? PileCardsOf(CardPile pile) => CardPileCardsField?.GetValue(pile) as List<CardModel>;

    private async Task RunSettingsDeepAsync(RunState state)
    {
        NCombatRoom room = await EnterCombatAsync();
        await WaitSecondsAsync(0.6);

        await CheckPileScreensAsync(state);

        NPauseMenu menu = await OpenPauseMenuAsync();
        Control buttons = menu.GetNode<Control>("%ButtonContainer");
        await CaptureAsync("settings_deep_01_pause");

        Control settings = await OpenInRunSettingsAsync(buttons);
        await TapMenuAsync(settings.GetNode<Control>("SettingsTabManager/Graphics"), "settings_deep_graphics_tab");
        Control graphics = settings.GetNode<Control>("ScrollContainer/Mask/Clipper/GraphicsSettings");
        _report.Check("settings_deep_graphics_tab_open", graphics.IsVisibleInTree(), "native Graphics page switched");
        CheckTouchTargets("t5_settings_deep_graphics", "settings Graphics (in run)", settings,
            new[] { NativeBackAllow, DropdownRowsAllow }.Concat(HostSettingsGateAllowlist()).ToArray());
        await CaptureAsync("settings_deep_02_graphics");
        await CheckGraphicsDropdownsAsync(settings, graphics);

        if (settings.IsVisibleInTree())
        {
            await TapMenuAsync(settings.GetNode<Control>("BackButton"), "settings_deep_settings_back");
            bool backToPause = await WaitOrFalseAsync(() => menu.IsVisibleInTree() && VisibleScreen("NSettingsScreen") is null,
                ShortWait);
            _report.Check("settings_deep_settings_back", backToPause && RunManager.Instance.IsPaused,
                "Settings Back returns to the pause menu with the run still paused");
        }
        else
        {
            // A lost screen is a result, not a timeout: report it and keep the pause-menu checks running.
            _report.Fail("settings_deep_settings_back", "the in-run Settings screen closed before Back");
        }
        await WaitSecondsAsync(0.4);

        await CheckCompendiumAsync(menu, buttons);
        await CheckGiveUpPopupAsync(buttons, room, state);
        CheckSaveAndQuitAsync(buttons);

        await _pointer.TapAsync(CenterOf(buttons.GetNode<Control>("Resume")));
        await WaitUntilAsync(() => !RunManager.Instance.IsPaused, "Resume returns to combat", ShortWait);
        _report.Check("settings_deep_resume_preserves_combat", NRun.Instance?.CombatRoom == room,
            "same combat room after the pause-menu checks");
    }

    // ---- settings: the Graphics page's native dropdowns --------------------------------------------------------------

    private async Task<NPauseMenu> OpenPauseMenuAsync()
    {
        await _pointer.TapAsync(CenterOf(NRun.Instance!.GlobalUi.TopBar.Pause));
        await WaitUntilAsync(() => RunManager.Instance.IsPaused && VisibleScreen(nameof(NPauseMenu)) is not null,
            "pause via top-bar gear", ShortWait);
        await WaitSecondsAsync(0.65);
        return (NPauseMenu)VisibleScreen(nameof(NPauseMenu))!;
    }

    private async Task<Control> OpenInRunSettingsAsync(Control buttons)
    {
        await _pointer.TapAsync(CenterOf(buttons.GetNode<Control>("Settings")));
        await WaitUntilAsync(() => VisibleScreen("NSettingsScreen") is not null, "in-run Settings opens", ShortWait);
        await WaitSecondsAsync(0.5);
        Control settings = VisibleScreen("NSettingsScreen")!;
        _report.Check("settings_deep_in_run_with_run_paused", RunManager.Instance.IsPaused,
            "opening Settings from the pause menu does not resume the run");
        return settings;
    }

    private async Task CheckGraphicsDropdownsAsync(Control settings, Control graphics)
    {
        NDropdown[] dropdowns = FindAll<NDropdown>(graphics)
            .Where(dropdown => GodotObject.IsInstanceValid(dropdown) && dropdown.IsVisibleInTree())
            .OrderBy(dropdown => dropdown.GetGlobalRect().Position.Y)
            .ThenBy(dropdown => dropdown.GetGlobalRect().Position.X)
            .ToArray();
        _report.Pass("settings_deep_graphics_dropdowns", dropdowns.Length == 0
            ? "no native dropdown on this host's Graphics page"
            : string.Join(", ", dropdowns.Select(dropdown => $"{dropdown.GetType().Name}={DropdownItemsOf(dropdown)?.GetChildCount() ?? -1}")));
        if (dropdowns.Length == 0)
        {
            _report.Skip("settings_deep_graphics_dropdown_open",
                "the Graphics page has no native dropdown (the phone rows replace the desktop ones)");
            return;
        }

        // Restore the stored display settings afterwards: the launcher's FillScreen shim makes AspectRatioSetting read Auto
        // while the game runs, so the getter cannot be used for the original value.
        var aspectField = typeof(SettingsSave).GetField("<AspectRatioSetting>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        SettingsSave save = SaveManager.Instance.SettingsSave;
        object? originalAspect = aspectField?.GetValue(save);
        int originalDisplay = save.TargetDisplay;
        Vector2I originalSize = save.WindowSize;
        Vector2I originalPosition = save.WindowPosition;
        SettingsDisplaySaveGuard.Active = true;
        try
        {
            foreach (NDropdown dropdown in dropdowns)
            {
                string kind = dropdown.GetType().Name;
                if (!dropdown.IsEnabled)
                {
                    _report.Skip($"settings_deep_dropdown_{Slug(kind)}_disabled",
                        $"{kind} is disabled in this run (the windowed resolution list reads N/A while the game is fullscreen)");
                    continue;
                }
                await CheckGraphicsDropdownAsync(settings, dropdown, kind);
            }
        }
        finally
        {
            SettingsDisplaySaveGuard.Active = false;
            if (aspectField is not null && originalAspect is not null) aspectField.SetValue(save, originalAspect);
            save.TargetDisplay = originalDisplay;
            save.WindowSize = originalSize;
            save.WindowPosition = originalPosition;
        }
    }

    private async Task CheckGraphicsDropdownAsync(Control settings, NDropdown dropdown, string kind)
    {
        string slug = Slug(kind);
        Control clip = settings.GetNode<Control>("ScrollContainer/Mask/Clipper");
        // The rows area only takes a press inside the clipper; a row scrolled below it is drawn (the mask fades it) but a
        // tap there falls through to whatever is under Settings, so bring the row into the clip first, like a finger would.
        if (!await ScrollRowIntoClipAsync(clip, dropdown))
        {
            _report.Fail($"settings_deep_dropdown_{slug}_row_reachable",
                $"{kind} center={CenterOf(dropdown)} is outside the rows clip {clip.GetGlobalRect()}; not tapped");
            return;
        }
        string currentLabel = DropdownLabelOf(dropdown);
        await TapCleanAsync(CenterOf(dropdown));
        if (!await WaitOrFalseAsync(() => DropdownOpen(dropdown), ShortWait))
        {
            _report.Fail($"settings_deep_dropdown_{slug}_open", $"touching {kind} did not open its native list");
            return;
        }
        Control? list = DropdownListOf(dropdown);
        Control? items = DropdownItemsOf(dropdown);
        if (list is null || items is null)
        {
            _report.Fail($"settings_deep_dropdown_{slug}_open", $"{kind}: the dropdown's list members were not found");
            return;
        }
        await WaitSecondsAsync(0.35);
        _report.Check($"settings_deep_dropdown_{slug}_open", list.IsVisibleInTree() && list.GetGlobalRect().HasArea(),
            $"{kind} list={list.GetGlobalRect()}");

        Rect2 listRect = list.GetGlobalRect();
        _report.Check($"settings_deep_dropdown_{slug}_list_in_frame",
            PortraitSafeRect().Grow(1f).Encloses(listRect), $"list={listRect} safe={PortraitSafeRect()}");
        CheckTouchTargets($"t5_settings_deep_dropdown_{slug}_list", $"{kind} list", list,
            new[] { DropdownRowsAllow }.Concat(HostSettingsGateAllowlist()).ToArray());

        NDropdownItem[] rows = items.GetChildren().OfType<NDropdownItem>()
            .Where(row => GodotObject.IsInstanceValid(row)).ToArray();
        _report.Pass($"settings_deep_dropdown_{slug}_rows", $"{rows.Length} row(s), list={listRect.Size}");
        if (rows.Length == 0)
        {
            _report.Fail($"settings_deep_dropdown_{slug}_rows_present", $"{kind} opened with no rows");
            await DismissDropdownAsync(dropdown, slug, clip);
            return;
        }

        NDropdownItem last = rows[^1];
        string lastLabel = FindFirst<Label>(last)?.Text ?? "";
        float initialY = items.Position.Y;
        bool needsScroll = items.GetGlobalRect().Size.Y > listRect.Size.Y + 1f;
        for (int pass = 0; pass < DropdownScrollPasses && needsScroll && !ItemInList(list, last); pass++)
        {
            listRect = list.GetGlobalRect();
            await _pointer.DragAsync(listRect.GetCenter() + new Vector2(0f, listRect.Size.Y * 0.3f),
                listRect.GetCenter() - new Vector2(0f, listRect.Size.Y * 0.3f), steps: 10);
            await WaitSecondsAsync(0.3);
        }
        Rect2 settledList = list.GetGlobalRect();
        bool lastInView = ItemInList(list, last);
        if (needsScroll)
            _report.Check($"settings_deep_dropdown_{slug}_scrolls",
                Math.Abs(items.Position.Y - initialY) > 1f,
                $"content y {initialY:F0} -> {items.Position.Y:F0}, list {settledList.Size.Y:F0}");
        else
            _report.Pass($"settings_deep_dropdown_{slug}_scrolls",
                $"the whole list fits ({items.GetGlobalRect().Size.Y:F0} <= {settledList.Size.Y:F0})");
        _report.Check($"settings_deep_dropdown_{slug}_last_row_reachable", lastInView,
            $"last={last.GetGlobalRect()} list={settledList}");
        await CaptureAsync($"settings_deep_03_dropdown_{slug}");

        bool sameAsCurrent = rows.Length == 1 || lastLabel.Length > 0 && lastLabel == currentLabel;
        if (sameAsCurrent)
        {
            // Selecting the item already in force is the one pick the native handler treats as a no-op; the list then
            // stays open, which the dismissal below has to close.
            await TapCleanAsync(CenterOf(last));
            await WaitSecondsAsync(0.35);
            _report.Pass($"settings_deep_dropdown_{slug}_select_last",
                $"tapped the current item \"{lastLabel}\"; the native handler keeps the list open and the setting unchanged");
        }
        else if (lastInView)
        {
            await TapCleanAsync(CenterOf(last));
            bool closed = await WaitOrFalseAsync(() => !DropdownOpen(dropdown), ShortWait);
            _report.Check($"settings_deep_dropdown_{slug}_select_last", closed,
                $"last item \"{lastLabel}\" {(closed ? "selected and the list closed" : "did not close the list")} " +
                $"(was \"{currentLabel}\")");
        }
        else
        {
            _report.Skip($"settings_deep_dropdown_{slug}_select_last",
                "the last item could not be scrolled into the list");
        }

        if (DropdownOpen(dropdown)) await DismissDropdownAsync(dropdown, slug, clip);
    }

    /// <summary>
    /// Taps outside the open list but inside the settings rows, at a clean top-right point: the host dismiss point is low on
    /// the screen where, in a run, the settings Back button sits (the outside tap would close the screen instead of the list).
    /// The launcher dismisses there for real; a list the tap leaves open is closed through its own <c>CloseDropdown</c>
    /// afterwards so the scenario can continue.
    /// </summary>
    private async Task DismissDropdownAsync(NDropdown dropdown, string slug, Control clip)
    {
        Rect2 rows = clip.GetGlobalRect();
        Vector2 point = new(rows.End.X - 12f, rows.Position.Y + 12f);
        Control? list = DropdownListOf(dropdown);
        if (list is not null && list.GetGlobalRect().HasPoint(point))
            point = new Vector2(rows.Position.X + 12f, rows.Position.Y + 12f);
        await TapCleanAsync(point);
        await WaitSecondsAsync(0.4);
        bool closed = !DropdownOpen(dropdown);
        _report.Check($"settings_deep_dropdown_{slug}_outside_dismiss", closed,
            $"a tap at {point} outside the list closes it");
        if (DropdownOpen(dropdown) && GodotObject.IsInstanceValid(dropdown))
        {
            dropdown.Call("CloseDropdown");
            await WaitSecondsAsync(0.25);
        }
    }

    /// <summary>
    /// Scrolls the settings rows until <paramref name="control"/>'s centre is inside <paramref name="clip"/>, in whichever
    /// direction it lies. Returns whether it made it (a row that stays outside must not be tapped: the press would fall
    /// through to the pause menu under the settings screen).
    /// </summary>
    private async Task<bool> ScrollRowIntoClipAsync(Control clip, Control control)
    {
        // A press outside a *hidden* settings clip falls through to the pause menu under it (the run's Give Up was opened
        // that way once): never tap unless the clip and the row are really on screen.
        if (!clip.IsVisibleInTree() || !control.IsVisibleInTree()) return false;
        for (int pass = 0; pass < 6; pass++)
        {
            Rect2 rect = clip.GetGlobalRect();
            Vector2 center = CenterOf(control);
            if (rect.HasPoint(center)) return true;
            bool below = center.Y > rect.End.Y;
            float y = rect.GetCenter().Y + (below ? 1f : -1f) * rect.Size.Y * 0.3f;
            await _pointer.DragAsync(new Vector2(rect.GetCenter().X, y),
                new Vector2(rect.GetCenter().X, y + (below ? -1f : 1f) * rect.Size.Y * 0.5f), steps: 12);
            await WaitSecondsAsync(0.4);
        }
        return clip.IsVisibleInTree() && control.IsVisibleInTree() && clip.GetGlobalRect().HasPoint(CenterOf(control));
    }

    /// <summary>
    /// Settles the pointer on the target before the press: the launcher's pointer shim can warp the OS cursor after the press
    /// reaches the game, and a warp of over 28 units is read as a rows swipe (the press is let go and the dropdown never
    /// opens). Moving first makes the injected tap a tap.
    /// </summary>
    private async Task TapCleanAsync(Vector2 point)
    {
        await _pointer.MoveAsync(point, false);
        await WaitFramesAsync(2);
        await _pointer.TapAsync(point);
    }

    private static bool ItemInList(Control list, Control item) =>
        item.IsVisibleInTree() && list.GetGlobalRect().Grow(-2f).HasPoint(CenterOf(item));

    private static string Slug(string typeName) =>
        (typeName.StartsWith("N", StringComparison.Ordinal) ? typeName[1..] : typeName).ToLowerInvariant();

    // ---- pause menu: Compendium and the Give Up confirmation ----------------------------------------------------------

    private async Task CheckCompendiumAsync(NPauseMenu menu, Control buttons)
    {
        Control? openCompendium = buttons.GetNodeOrNull<Control>("Compendium");
        if (openCompendium is null || !openCompendium.IsVisibleInTree())
        {
            _report.Skip("settings_deep_compendium_open",
                "the pause menu has no Compendium button in this profile; nothing to open");
            return;
        }
        await _pointer.TapAsync(CenterOf(openCompendium));
        if (!await WaitOrFalseAsync(() => VisibleScreen("NCompendiumSubmenu") is not null, MediumWait))
        {
            _report.Fail("settings_deep_compendium_open", "tapping Compendium did not open the native compendium");
            return;
        }
        Control compendium = VisibleScreen("NCompendiumSubmenu")!;
        await WaitSecondsAsync(0.7);
        _report.Check("settings_deep_compendium_open", RunManager.Instance.IsPaused,
            "the run stays paused behind the compendium");

        string[] cardButtons = { "NShortSubmenuButton", "NCompendiumBottomButton" };
        Control[] entries = FindAll<Control>(compendium)
            .Where(control => control.IsVisibleInTree() && cardButtons.Contains(control.GetType().Name)).ToArray();
        _report.Check("settings_deep_compendium_entries_in_frame",
            entries.Length >= 4 && entries.All(ControlInFrame),
            $"{entries.Length} entr(ies): {string.Join(", ", entries.Select(entry => $"{entry.Name}={entry.GetGlobalRect()}"))}");
        CheckTouchTargets("t5_settings_deep_compendium", "in-run Compendium", compendium, new[] { NativeBackAllow });
        await CaptureAsync("settings_deep_04_compendium");

        Control back = compendium.GetNodeOrNull<Control>("BackButton")
            ?? throw new InvalidOperationException("the in-run compendium has no BackButton");
        await _pointer.TapAsync(CenterOf(back));
        bool returned = await WaitOrFalseAsync(
            () => VisibleScreen("NCompendiumSubmenu") is null && menu.IsVisibleInTree(), MediumWait);
        _report.Check("settings_deep_compendium_closed", returned && RunManager.Instance.IsPaused,
            "compendium Back returns to the pause menu, run still paused");
        await WaitSecondsAsync(0.5);
    }

    private async Task CheckGiveUpPopupAsync(Control buttons, NCombatRoom room, RunState state)
    {
        await _pointer.TapAsync(CenterOf(buttons.GetNode<Control>("GiveUp")));
        if (!await WaitOrFalseAsync(() => VisibleScreen("NAbandonRunConfirmPopup") is not null, ShortWait))
        {
            _report.Fail("settings_deep_give_up_open", "Give Up did not open its native confirmation");
            return;
        }
        Control popup = VisibleScreen("NAbandonRunConfirmPopup")!;
        await WaitSecondsAsync(0.5);
        Control plate = popup.GetNodeOrNull<Control>("VerticalPopup") ?? popup;
        _report.Check("settings_deep_give_up_in_frame", PortraitSafeRect().Grow(1f).Encloses(plate.GetGlobalRect()),
            $"plate={plate.GetGlobalRect()} safe={PortraitSafeRect()}");
        CheckTouchTargets("t5_settings_deep_give_up", "abandon confirmation", plate, Array.Empty<string>());

        Control? header = plate.GetNodeOrNull<Control>("Header");
        Control? body = plate.GetNodeOrNull<Control>("Description");
        string bodyText = body switch
        {
            RichTextLabel rich => rich.GetParsedText(),
            Label label => label.Text,
            _ => "",
        };
        _report.Check("settings_deep_give_up_readable",
            header is not null && body is not null && bodyText.Trim().Length > 0
            && PortraitSafeRect().Encloses(header.GetGlobalRect())
            && PortraitSafeRect().Encloses(body.GetGlobalRect()),
            $"header={(header?.GetGlobalRect().ToString() ?? "none")} body={(body?.GetGlobalRect().ToString() ?? "none")} " +
            $"text=\"{bodyText.Trim()}\"");
        Control yes = plate.GetNode<Control>("YesButton");
        Control no = plate.GetNode<Control>("NoButton");
        _report.Check("settings_deep_give_up_buttons_in_frame",
            yes.IsVisibleInTree() && no.IsVisibleInTree() && ControlInFrame(yes) && ControlInFrame(no) && !yes.GetGlobalRect().Intersects(no.GetGlobalRect()),
            $"yes={yes.GetGlobalRect()} no={no.GetGlobalRect()}");
        await CaptureAsync("settings_deep_05_give_up");

        await _pointer.TapAsync(CenterOf(no));
        bool cancelled = await WaitOrFalseAsync(() => VisibleScreen("NAbandonRunConfirmPopup") is null, ShortWait);
        _report.Check("settings_deep_give_up_cancelled",
            cancelled && RunManager.Instance.IsPaused && NGame.Instance?.CurrentRunNode is not null
            && NRun.Instance?.CombatRoom == room && !state.IsGameOver,
            "No closes the confirmation; the run is still in the same fight and paused");
        await WaitSecondsAsync(0.4);
    }

    private void CheckSaveAndQuitAsync(Control buttons)
    {
        Control saveQuit = buttons.GetNode<Control>("SaveAndQuit");
        _report.Check("settings_deep_save_and_quit_button",
            saveQuit.IsVisibleInTree() && saveQuit is NClickableControl { IsEnabled: true } && ControlInFrame(saveQuit),
            $"button={saveQuit.GetGlobalRect()}");
        // v0.113.1: NPauseMenu.OnSaveAndQuitButtonPressed runs CloseToMenu() directly; there is no confirmation to cancel,
        // and a tap would save and leave the run for the main menu.
        _report.Skip("settings_deep_save_and_quit_confirmation",
            "v0.113.1 has no Save & Quit confirmation: the button calls CloseToMenu directly, so it is not tapped " +
            "(the run must stay for the pile checks)");
    }

    // ---- card pile screens ------------------------------------------------------------------------------------------

    private async Task CheckPileScreensAsync(RunState state)
    {
        Player player = state.Players[0];
        IReadOnlyList<CardModel> deck = player.Deck.Cards;
        _report.Check("settings_deep_pile_fixture_deck", deck.Count > 0, $"deck={deck.Count} card(s)");
        if (deck.Count == 0) return;
        foreach ((PileType type, string slug) in new[] { (PileType.Draw, "draw"), (PileType.Discard, "discard") })
            foreach (int count in new[] { 0, 1, 30 })
                await CheckPileScreenAsync(type, slug, count, deck);
    }

    private async Task CheckPileScreenAsync(PileType type, string slug, int count, IReadOnlyList<CardModel> deck)
    {
        string name = $"{slug}_{count}";
        try
        {
            // A fixture pile of the same type the rail's own button opens: the screen, its grid and the close button are
            // exactly the native ones, with a count chosen so 0, 1 and a scrolling list are all seen.
            var pile = new CardPile(type);
            List<CardModel>? cards = PileCardsOf(pile)
                ?? throw new InvalidOperationException("CardPile._cards was not found on the shipped assembly");
            for (int i = 0; i < count; i++) cards.Add(deck[i % deck.Count]);
            NCardPileScreen screen = NCardPileScreen.ShowScreen(pile, Array.Empty<string>());
            await WaitUntilAsync(
                () => GodotObject.IsInstanceValid(screen) && screen.IsVisibleInTree() && screen.Pile == pile,
                $"native {type} pile screen ({count} cards)", MediumWait);
            await WaitSecondsAsync(0.7);

            NCardGrid grid = screen.GetNode<NCardGrid>("CardGrid");
            Control close = screen.GetNodeOrNull<Control>("BackButton")
                ?? throw new InvalidOperationException("the pile screen has no BackButton");
            Control info = screen.GetNodeOrNull<Control>("BottomText")
                ?? throw new InvalidOperationException("the pile screen has no BottomText");
            _report.Check($"settings_deep_pile_{name}_open", screen.Pile.Type == type && screen.Pile.Cards.Count == count,
                $"type={screen.Pile.Type} cards={screen.Pile.Cards.Count} expected={count}");

            NGridCardHolder[] holders = grid.CurrentlyDisplayedCardHolders
                .Where(holder => GodotObject.IsInstanceValid(holder) && holder.IsVisibleInTree()).ToArray();
            // NCardGrid recycles a pooled set of holders (fifteen here) and only reuses them for the rows on screen; the
            // card list is what must be complete, and the scroll below proves the rest are reachable.
            _report.Check($"settings_deep_pile_{name}_card_count",
                grid.SortedCards.Count == count && screen.Pile.Cards.Count == count,
                $"grid cards={grid.SortedCards.Count} pile={screen.Pile.Cards.Count} expected={count}, holders pooled={holders.Length}");

            // Rows below the grid's clip are laid out off screen; measure only the cards the band actually shows.
            Rect2 view = grid.GetGlobalRect();
            NGridCardHolder[] shown = holders
                .Where(holder => view.Grow(2f).HasPoint(CenterOf(holder.Hitbox))).ToArray();
            var rows = shown
                .GroupBy(holder => (long)Math.Round(DrawnCardRect(holder).GetCenter().Y / 10f))
                .Select(group => group.OrderBy(holder => DrawnCardRect(holder).Position.X).ToArray())
                .ToArray();
            bool threeColumns = rows.All(row => row.Length <= 3) && (count < 3 || rows.Any(row => row.Length == 3));
            bool noOverlap = rows.All(row => row.Select(DrawnCardRect).Zip(row.Select(DrawnCardRect).Skip(1))
                .All(pair => pair.First.End.X <= pair.Second.Position.X + 1f));
            _report.Check($"settings_deep_pile_{name}_three_columns", threeColumns,
                $"rows={string.Join(",", rows.Select(row => row.Length))}");
            _report.Check($"settings_deep_pile_{name}_columns_do_not_overlap", noOverlap,
                string.Join("; ", rows.Select(row => string.Join(",", row.Select(holder => DrawnCardRect(holder))))));
            _report.Check($"settings_deep_pile_{name}_cards_in_frame",
                rows.SelectMany(row => row).All(holder => PortraitSafeRect().Grow(8f).Encloses(DrawnCardRect(holder))),
                string.Join("; ", rows.SelectMany(row => row).Select(holder => DrawnCardRect(holder).ToString())));

            _report.Check($"settings_deep_pile_{name}_close_in_frame", close.IsVisibleInTree() && ControlInFrame(close),
                $"close={close.GetGlobalRect()}");
            CheckTouchTargets($"t5_settings_deep_pile_{name}", $"{type} pile ({count} cards)", screen, new[] { NativeBackAllow });
            _report.Check($"settings_deep_pile_{name}_info_in_frame",
                info.IsVisibleInTree() && PortraitSafeRect().Grow(1f).Encloses(info.GetGlobalRect())
                && !info.GetGlobalRect().Intersects(close.GetGlobalRect()),
                $"info={info.GetGlobalRect()} close={close.GetGlobalRect()}");
            await CaptureAsync($"settings_deep_06_pile_{name}");

            Control content = GridScrollContent(grid);
            if (count > 12)
            {
                float before = content.Position.Y;
                // The native grid's drag is 1:1 with the finger and only clamps its target back to the content bottom after
                // the lift: four 0.6-view passes stop a little short of a 30-card pile's end (13 units on a 9:16 screen).
                // Drag until the grid's own live limit is reached, bounded like the deck view's swipe.
                for (int pass = 0; pass < 10 && content.Position.Y - GridScrollLimitBottom(grid) > 2f; pass++)
                {
                    await _pointer.DragAsync(view.GetCenter() + new Vector2(0f, view.Size.Y * 0.3f),
                        view.GetCenter() - new Vector2(0f, view.Size.Y * 0.3f), steps: 14);
                    await WaitSecondsAsync(0.45);
                }
                float limit = GridScrollLimitBottom(grid);
                // A pile can hold the same card model more than once (this fixture pads the deck with repeated models), and
                // GetCardHolder returns the first bound copy, which may be a row that has scrolled above the view. The grid
                // keeps its rows in display order, so the last matching displayed holder is the bottom copy: the last row
                // the scroll has to reveal.
                CardModel lastCard = grid.SortedCards[^1];
                NGridCardHolder[] copies = grid.CurrentlyDisplayedCardHolders
                    .Where(holder => GodotObject.IsInstanceValid(holder) && holder.CardModel == lastCard).ToArray();
                NGridCardHolder? lastHolder = copies.LastOrDefault();
                bool lastReached = lastHolder is not null && GodotObject.IsInstanceValid(lastHolder)
                    && view.Grow(4f).Encloses(DrawnCardRect(lastHolder));
                _report.Check($"settings_deep_pile_{name}_scrolls",
                    content.Position.Y < before - 50f && content.Position.Y - limit <= 2f && lastReached,
                    $"content y {before:F0} -> {content.Position.Y:F0} limit={limit:F0} " +
                    $"last={(lastHolder is null ? "none" : DrawnCardRect(lastHolder).ToString())} copies={copies.Length}");
                await CaptureAsync($"settings_deep_07_pile_{name}_scrolled");
            }

            await _pointer.TapAsync(CenterOf(close));
            await WaitUntilAsync(() => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree(),
                $"close the {type} pile", ShortWait);
            await WaitUntilAsync(() => NCapstoneContainer.Instance is null || !NCapstoneContainer.Instance.InUse,
                "capstone container released", ShortWait);
            _report.Pass($"settings_deep_pile_{name}_closes");
            await WaitSecondsAsync(0.3);
        }
        catch (Exception error)
        {
            // Keep the failure with the count it belongs to and let the other five screens still run.
            _report.Fail($"settings_deep_pile_{name}_error", error.Message);
            CloseCapstoneScreen();
            await WaitSecondsAsync(0.4);
        }
    }
}
