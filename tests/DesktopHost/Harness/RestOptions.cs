using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// <c>rest-options</c> on both hosts: the rest site with more than the two base options.
/// <para>
/// First with the relics that add options (Shovel gives Dig, Girya gives Lift), so the room must offer Heal, Smith, Dig and
/// Lift together and both extra options must work: Lift raises Girya's counter and consumes the option, Dig grants its
/// relic. Then every option that opens its own screen or changes the run is exercised for real, one fresh rest site each,
/// from the relic or card that grants it (Cook/MeatCleaver, Clone/PaelsGrowth, Hatch/ByrdonisEgg, Evolve/Byrdpip,
/// Kindle/PumpkinCandle, Restore/Wuppy); the deck-removal and enchant screens are completed through their native controls
/// and measured with the shared deck-selection checks. A realistic single-player maximum follows with all those triggers at
/// once, then the synthetic maximum through the room's own option list (the synchronizer's list is filled and the room's own
/// <c>UpdateRestSiteOptions</c> rebuilds its buttons; the game's <c>generateForTests</c> hook cannot be used because it only
/// answers in test mode, and the rest-site room refuses to be created in test mode). Mend is built and laid out with the
/// synthetic set, but its ally picker needs a real multiplayer teammate, so it is never selected here.
/// </para>
/// <para>
/// The room's phone geometry is a host partial (<see cref="CheckRestSiteButtons"/> and <see cref="CheckRestRoomLayout"/>):
/// 127-unit targets inside the safe frame, no overlap and readable labels on the launcher; TouchSpireUI's own button scale
/// on the TouchSpire host.
/// </para>
/// </summary>
internal sealed partial class TestDriver
{
    private sealed record RestOptionTap(NRestSiteRoom Room, bool Offered);

    private async Task RunRestOptionsAsync(RunState runState)
    {
        Player player = runState.Players[0];
        await RelicCmd.Obtain<Shovel>(player);
        await RelicCmd.Obtain<Girya>(player);
        await WaitSecondsAsync(0.6);
        _report.Pass("rest_options_relics_obtained",
            $"relics={string.Join(",", player.Relics.Select(relic => relic.Id.ToString()))}");

        // ---- Heal, Smith, Dig and Lift from real relics -------------------------------------------------------------
        NRestSiteRoom room = await EnterRestSiteAsync(previousRoomId: 0);
        await WaitSecondsAsync(1.0);
        List<NRestSiteButton> buttons = FindAll<NRestSiteButton>(room);
        string[] ids = buttons.Select(button => button.Option.OptionId).ToArray();
        _report.Check("rest_options_relics_offer_heal_smith_dig_lift",
            new[] { "HEAL", "SMITH", "DIG", "LIFT" }.All(id => ids.Contains(id)),
            $"options={string.Join(",", ids)}");
        CheckRestSiteButtons(buttons);
        CheckRestRoomLayout(room);
        await CaptureAsync("rest_options_01_relics");
        // The copy (the option's description) while each end of the row is held: it must never touch an option.
        await DescribeRestOptionsAsync(room);

        // Lift: the native option increments Girya's counter and the room moves on to Proceed.
        NRestSiteButton? lift = buttons.FirstOrDefault(button => button.Option.OptionId == "LIFT");
        if (lift is null)
        {
            _report.Fail("rest_options_lift_raises_girya", "the room offered no Lift option");
        }
        else
        {
            int lifted = GiryaOf(player)?.TimesLifted ?? -1;
            await TapRestOptionAsync(room, lift, buttons.Count, "lift");
            try
            {
                await WaitUntilAsync(() => (GiryaOf(player)?.TimesLifted ?? -1) > lifted,
                    "Girya's Lift raises the counter", MediumWait);
            }
            catch (TimeoutException)
            {
                // Reported with the counts below.
            }
            _report.Check("rest_options_lift_raises_girya",
                lifted >= 0 && (GiryaOf(player)?.TimesLifted ?? -1) == lifted + 1,
                $"timesLifted={lifted}->{GiryaOf(player)?.TimesLifted}");
            await CheckRestProceedAsync("lift", room);
            await CaptureAsync("rest_options_01b_lift");
        }

        // Dig: a fresh rest site; the native option pulls a relic out of the dig reward.
        room = await EnterRestSiteAsync(room.GetInstanceId());
        await WaitSecondsAsync(1.0);
        buttons = FindAll<NRestSiteButton>(room);
        NRestSiteButton? dig = buttons.FirstOrDefault(button => button.Option.OptionId == "DIG");
        if (dig is null)
        {
            _report.Fail("rest_options_dig_grants_relic", $"the room offered no Dig option ({string.Join(",", buttons.Select(b => b.Option.OptionId))})");
        }
        else
        {
            int relicsBefore = player.Relics.Count;
            await TapRestOptionAsync(room, dig, buttons.Count, "dig");
            try
            {
                await WaitUntilAsync(() => player.Relics.Count > relicsBefore, "Dig grants its relic", MediumWait);
            }
            catch (TimeoutException)
            {
                // Reported with the counts below.
            }
            // Dig's native OnSelect calls RelicCmd.Obtain directly (the game gives the relic and animates the inventory);
            // this build shows no separate relic-reward screen for it.
            _report.Check("rest_options_dig_grants_relic", player.Relics.Count == relicsBefore + 1,
                $"relics={relicsBefore}->{player.Relics.Count} newest={player.Relics.LastOrDefault()?.Id}");
            await WaitSecondsAsync(0.8);
            await CaptureAsync("rest_options_01c_dig");
        }

        // ---- Each option that owns a screen or changes the run ------------------------------------------------------
        // Each phase is guarded: a failure is reported as a step (and the overlay stack cleared) instead of ending the run,
        // so RESULTS.json is always written and later phases still report.
        room = await GuardPhaseAsync("cook", room, current => RunCookPhaseAsync(player, current));
        room = await GuardPhaseAsync("clone", room, current => RunClonePhaseAsync(player, current));
        room = await GuardPhaseAsync("hatch", room, current => RunHatchPhaseAsync(player, current));
        room = await GuardPhaseAsync("evolve", room, current => RunEvolvePhaseAsync(player, current));
        room = await GuardPhaseAsync("kindle", room, current => RunKindlePhaseAsync(player, current));
        room = await GuardPhaseAsync("restore", room, current => RunRestorePhaseAsync(player, current));

        // ---- A realistic single-player maximum, then the synthetic one ----------------------------------------------
        room = await GuardPhaseAsync("realistic_max", room, current => RunRealisticMaxAsync(player, current));
        await GuardPhaseAsync("synthetic_max", room, current => RunSyntheticMaxAsync(player, current));

        _report.Pass("rest_options_done",
            "four relic-granted options, each screen-owning option in single player, the realistic maximum and the synthetic maximum were laid out and exercised");
    }

    // ---- Cook: MeatCleaver's deck-removal screen, 2 cards for 5 max HP ------------------------------------------------

    private async Task<NRestSiteRoom> RunCookPhaseAsync(Player player, NRestSiteRoom room)
    {
        await RelicCmd.Obtain<MeatCleaver>(player);
        await WaitSecondsAsync(0.5);
        int deckBefore = player.Deck.Cards.Count;
        int maxHpBefore = player.Creature.MaxHp;
        RestOptionTap tap = await EnterAndTapOptionAsync("COOK", room.GetInstanceId());
        room = tap.Room;
        if (!tap.Offered) return room;

        NDeckCardSelectScreen screen = await WaitForVisibleScreenAsync<NDeckCardSelectScreen>("Cook's deck-removal screen", MediumWait);
        await WaitSecondsAsync(0.6);
        NCardGrid grid = screen.GetNode<NCardGrid>("%CardGrid");
        // Cook selects two cards and previews them; the phone flow is the game's own card taps + confirm.
        await CheckDeckSelectionScrollAsync("cook", grid, screen.GetNode<Control>("%BottomText"),
            screen.GetNode<Control>("%PreviewContainer"));
        NGridCardHolder[] holders = grid.SortedCards.Take(2).Select(card => grid.GetCardHolder(card)!).ToArray();
        foreach (NGridCardHolder holder in holders) await _pointer.TapAsync(CenterOf(holder.Hitbox));
        await WaitUntilAsync(() => screen.GetNode<Control>("%PreviewContainer").Visible, "Cook's native preview", ShortWait);
        await WaitSecondsAsync(0.5);
        CheckScreenBacks("cook_preview", screen);
        await CaptureAsync("rest_options_03_cook_select");
        await _pointer.TapAsync(CenterOf(screen.GetNode<Control>("%PreviewConfirm")));
        await WaitForClosedAsync(screen, "Cook's selection ends");
        try
        {
            await WaitUntilAsync(() => player.Deck.Cards.Count == deckBefore - 2 && player.Creature.MaxHp == maxHpBefore + 5,
                "Cook removes two cards and gains 5 max HP", MediumWait);
        }
        catch (TimeoutException)
        {
            // Reported below.
        }
        _report.Check("rest_options_cook_removes_two_and_gains_max_hp",
            player.Deck.Cards.Count == deckBefore - 2 && player.Creature.MaxHp == maxHpBefore + 5,
            $"deck={deckBefore}->{player.Deck.Cards.Count} maxHp={maxHpBefore}->{player.Creature.MaxHp}");
        await CheckRestProceedAsync("cook", room);
        await CaptureAsync("rest_options_03_cook");
        return room;
    }

    // ---- Clone: PaelsGrowth's enchant picker, then the duplicate ------------------------------------------------------

    private async Task<NRestSiteRoom> RunClonePhaseAsync(Player player, NRestSiteRoom room)
    {
        // PaelsGrowth's own obtain opens the enchant picker; the obtain completes only after it is answered.
        Task<PaelsGrowth> obtain = RelicCmd.Obtain<PaelsGrowth>(player);
        NDeckEnchantSelectScreen screen = await WaitForVisibleScreenAsync<NDeckEnchantSelectScreen>(
            "PaelsGrowth's enchant picker", MediumWait);
        await WaitSecondsAsync(0.6);
        NCardGrid grid = screen.GetNode<NCardGrid>("%CardGrid");
        Control preview = screen.GetNode<Control>("%EnchantSinglePreviewContainer");
        NConfirmButton confirm = preview.GetNode<NConfirmButton>("Confirm");
        await CheckDeckSelectionScrollAsync("clone", grid, screen.GetNode<Control>("%BottomText"), preview);
        NGridCardHolder holder = grid.GetCardHolder(grid.SortedCards[0])!;
        await _pointer.TapAsync(CenterOf(holder.Hitbox));
        await WaitUntilAsync(() => preview.Visible && confirm.IsEnabled, "PaelsGrowth's enchant preview", ShortWait);
        await WaitSecondsAsync(0.5);
        CheckScreenBacks("clone_preview", screen);
        await CaptureAsync("rest_options_03_clone_select");
        await _pointer.TapAsync(CenterOf(confirm));
        await WaitForClosedAsync(screen, "PaelsGrowth's enchant picker ends");
        await obtain;
        await WaitSecondsAsync(0.5);

        int deckBefore = player.Deck.Cards.Count;
        RestOptionTap tap = await EnterAndTapOptionAsync("CLONE", room.GetInstanceId());
        room = tap.Room;
        if (!tap.Offered) return room;
        try
        {
            await WaitUntilAsync(() => player.Deck.Cards.Count == deckBefore + 1, "Clone duplicates the enchanted card", MediumWait);
        }
        catch (TimeoutException)
        {
            // Reported below.
        }
        _report.Check("rest_options_clone_duplicates_card", player.Deck.Cards.Count == deckBefore + 1,
            $"deck={deckBefore}->{player.Deck.Cards.Count}");
        await CheckRestProceedAsync("clone", room);
        await CaptureAsync("rest_options_03_clone");
        return room;
    }

    // ---- Hatch: a ByrdonisEgg in the deck hatches into Byrdpip --------------------------------------------------------

    private async Task<NRestSiteRoom> RunHatchPhaseAsync(Player player, NRestSiteRoom room)
    {
        // The egg is a card, not a relic: it is created and added the way the game's own Byrdonis Nest event does it
        // (RunState.CreateCard gives it its owner; CardPileCmd.Add is the native deck-add path).
        await AddByrdonisEggAsync(player);
        await WaitSecondsAsync(0.5);
        int deckBefore = player.Deck.Cards.Count;
        int eggsBefore = player.Deck.Cards.Count(card => card is ByrdonisEgg);
        int swoopsBefore = player.Deck.Cards.Count(card => card is ByrdSwoop);
        RestOptionTap tap = await EnterAndTapOptionAsync("HATCH", room.GetInstanceId());
        room = tap.Room;
        if (!tap.Offered) return room;
        try
        {
            await WaitUntilAsync(() => player.GetRelic<Byrdpip>() is not null, "Hatch obtains Byrdpip", MediumWait);
        }
        catch (TimeoutException)
        {
            // Reported below.
        }
        await WaitSecondsAsync(0.8);
        // What Hatch does (HatchRestSiteOption.OnSelect -> RelicCmd.Obtain<Byrdpip>, and Byrdpip.AfterObtained transforms each
        // ByrdonisEgg in the deck into a ByrdSwoop): the relic arrives, no egg is left, and one ByrdSwoop takes each egg's
        // place, so the deck keeps its size instead of shrinking.
        bool eggGone = player.Deck.Cards.All(card => card is not ByrdonisEgg);
        int swoops = player.Deck.Cards.Count(card => card is ByrdSwoop);
        _report.Check("rest_options_hatch_gives_byrdpip",
            player.GetRelic<Byrdpip>() is not null && eggGone && eggsBefore >= 1
            && swoops == swoopsBefore + eggsBefore && player.Deck.Cards.Count == deckBefore,
            $"byrdpip={player.GetRelic<Byrdpip>() is not null} eggs={eggsBefore}->0 swoops={swoopsBefore}->{swoops} " +
            $"deck={deckBefore}->{player.Deck.Cards.Count}");
        await CheckRestProceedAsync("hatch", room);
        await CaptureAsync("rest_options_03_hatch");
        return room;
    }

    // ---- Evolve: Byrdpip with six swoops becomes Byrdine --------------------------------------------------------------

    private async Task<NRestSiteRoom> RunEvolvePhaseAsync(Player player, NRestSiteRoom room)
    {
        Byrdpip? pip = player.GetRelic<Byrdpip>();
        if (pip is null)
        {
            _report.Fail("rest_options_evolve_offered", "Hatch did not leave a Byrdpip to evolve");
            return room;
        }
        // The option only appears after six swoops; the counter is the relic's own saved property.
        pip.SwoopsPlayed = 6;
        RestOptionTap tap = await EnterAndTapOptionAsync("EVOLVE", room.GetInstanceId());
        room = tap.Room;
        if (!tap.Offered) return room;
        try
        {
            await WaitUntilAsync(() => player.GetRelic<Byrdpip>() is null && player.GetRelic<Byrdine>() is not null,
                "Evolve replaces Byrdpip with Byrdine", LongWait);
        }
        catch (TimeoutException)
        {
            // Reported below.
        }
        await WaitSecondsAsync(0.5);
        _report.Check("rest_options_evolve_gives_byrdine",
            player.GetRelic<Byrdpip>() is null && player.GetRelic<Byrdine>() is not null,
            $"byrdpip={player.GetRelic<Byrdpip>() is not null} byrdine={player.GetRelic<Byrdine>() is not null}");
        await CheckRestProceedAsync("evolve", room);
        await CaptureAsync("rest_options_03_evolve");
        return room;
    }

    // ---- Kindle: PumpkinCandle gains five charges ---------------------------------------------------------------------

    private async Task<NRestSiteRoom> RunKindlePhaseAsync(Player player, NRestSiteRoom room)
    {
        await RelicCmd.Obtain<PumpkinCandle>(player);
        await WaitSecondsAsync(0.5);
        PumpkinCandle? candle = player.GetRelic<PumpkinCandle>();
        int kindleBefore = candle?.KindleCount ?? -1;
        RestOptionTap tap = await EnterAndTapOptionAsync("KINDLE", room.GetInstanceId());
        room = tap.Room;
        if (!tap.Offered) return room;
        try
        {
            await WaitUntilAsync(() => (player.GetRelic<PumpkinCandle>()?.KindleCount ?? -1) == kindleBefore + 5,
                "Kindle adds five charges", MediumWait);
        }
        catch (TimeoutException)
        {
            // Reported below.
        }
        _report.Check("rest_options_kindle_rekindles_candle",
            kindleBefore >= 0 && (player.GetRelic<PumpkinCandle>()?.KindleCount ?? -1) == kindleBefore + 5,
            $"kindle={kindleBefore}->{player.GetRelic<PumpkinCandle>()?.KindleCount}");
        await CheckRestProceedAsync("kindle", room);
        await CaptureAsync("rest_options_03_kindle");
        return room;
    }

    // ---- Restore: a damaged Wuppy returns to full health --------------------------------------------------------------

    private async Task<NRestSiteRoom> RunRestorePhaseAsync(Player player, NRestSiteRoom room)
    {
        await RelicCmd.Obtain<Wuppy>(player);
        await WaitSecondsAsync(0.5);
        Wuppy? wuppy = player.GetRelic<Wuppy>();
        if (wuppy is null)
        {
            _report.Fail("rest_options_restore_offered", "Wuppy was not obtained");
            return room;
        }
        // Restore's button is enabled only while the companion is hurt; the relic's own HP is a saved property.
        wuppy.CurrentHp = Math.Max(0, wuppy.MaxHp - 10);
        int before = wuppy.CurrentHp;
        RestOptionTap tap = await EnterAndTapOptionAsync("RESTORE", room.GetInstanceId());
        room = tap.Room;
        if (!tap.Offered) return room;
        try
        {
            await WaitUntilAsync(() => wuppy.CurrentHp == wuppy.MaxHp, "Restore heals Wuppy fully", MediumWait);
        }
        catch (TimeoutException)
        {
            // Reported below.
        }
        _report.Check("rest_options_restore_heals_wuppy", wuppy.CurrentHp == wuppy.MaxHp,
            $"hp={before}->{wuppy.CurrentHp}/{wuppy.MaxHp}");
        await CheckRestProceedAsync("restore", room);
        await CaptureAsync("rest_options_03_restore");
        return room;
    }

    // ---- The maximum a single-player run can actually offer ------------------------------------------------------------

    private async Task<NRestSiteRoom> RunRealisticMaxAsync(Player player, NRestSiteRoom room)
    {
        // Hatch and Evolve can stand together: an egg in the deck plus a Byrdpip with six swoops. The Evolve phase consumed
        // its Byrdpip, and Hatch consumed its egg, so both are put back; Wuppy is hurt again so Restore is enabled.
        await RelicCmd.Obtain<Byrdpip>(player);
        await AddByrdonisEggAsync(player);
        if (player.GetRelic<Byrdpip>() is { } pip) pip.SwoopsPlayed = 6;
        if (player.GetRelic<Wuppy>() is { } wuppy) wuppy.CurrentHp = Math.Max(0, wuppy.MaxHp - 10);
        await WaitSecondsAsync(0.6);

        room = await EnterRestSiteAsync(room.GetInstanceId());
        await WaitSecondsAsync(1.0);
        List<NRestSiteButton> buttons = LiveRestButtons(room);
        string[] ids = buttons.Select(button => button.Option.OptionId).ToArray();
        int renderedCount = FindAll<NRestSiteButton>(room).Count;
        string[] expected = { "HEAL", "SMITH", "DIG", "LIFT", "COOK", "CLONE", "KINDLE", "RESTORE", "HATCH", "EVOLVE" };
        // The exact set, not just presence: a row that still holds the buttons a rebuild replaced would count extras here.
        _report.Check("rest_options_realistic_max_lists_single_player_options",
            renderedCount == ids.Length
            && ids.OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual(expected.OrderBy(id => id, StringComparer.Ordinal), StringComparer.Ordinal),
            $"expected={string.Join(",", expected)} rendered={string.Join(",", ids)} room={renderedCount}");
        CheckRestSiteButtons(buttons);
        CheckRestRoomLayout(room);
        await CaptureAsync("rest_options_02b_realistic_max");
        await DescribeRestOptionsAsync(room);
        return room;
    }

    // ---- Every option type, through the room's own option list ---------------------------------------------------------

    private async Task<NRestSiteRoom> RunSyntheticMaxAsync(Player player, NRestSiteRoom room)
    {
        // The game's generateForTests hook only answers in test mode, and NRestSiteRoom.Create refuses to make the room at all
        // in test mode (`if (TestMode.IsOn) return null;`), which is why arming it made this phase time out. The options are
        // therefore put on the room's own synchronizer after a normal entry: RestSiteRoom.Options reads that list, and the
        // room's own UpdateRestSiteOptions rebuilds its buttons from it.
        (List<RestSiteOption> options, List<string> skipped) = BuildEveryOption(player);
        room = await EnterRestSiteAsync(room.GetInstanceId());
        await WaitSecondsAsync(0.6);
        FillRestSiteOptions(room, options);
        try
        {
            // UpdateRestSiteOptions queues the row it replaces before it adds the new buttons. For one frame a plain scan
            // sees both rows, and the queued buttons are disposed once that frame ends, so wait for the old row to retire
            // and for the live row to be exactly the list that was installed.
            await WaitUntilAsync(() =>
            {
                List<NRestSiteButton> live = LiveRestButtons(room);
                return FindAll<NRestSiteButton>(room).Count == options.Count
                    && live.Count == options.Count
                    && options.All(option => live.Any(button => button.Option.OptionId == option.OptionId));
            }, "every rest option rendered and the replaced row retired", MediumWait);
        }
        catch (TimeoutException)
        {
            // Reported below with the rendered ids.
        }
        await WaitSecondsAsync(1.0);
        List<NRestSiteButton> maxButtons = LiveRestButtons(room);
        string[] maxIds = maxButtons.Select(button => button.Option.OptionId).ToArray();
        int renderedCount = FindAll<NRestSiteButton>(room).Count;
        string[] expectedIds = options.Select(option => option.OptionId).ToArray();
        _report.Check("rest_options_max_lists_every_option",
            renderedCount == maxIds.Length
            && maxIds.OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual(expectedIds.OrderBy(id => id, StringComparer.Ordinal), StringComparer.Ordinal),
            $"expected={string.Join(",", expectedIds)} rendered={string.Join(",", maxIds)} room={renderedCount} " +
            $"skipped=[{string.Join(",", skipped)}]");
        if (skipped.Count > 0)
            _report.Skip("rest_options_skipped_options", string.Join(",", skipped));
        CheckRestSiteButtons(maxButtons);
        CheckRestRoomLayout(room);
        await CaptureAsync("rest_options_02_max");
        await DescribeRestOptionsAsync(room);
        // Mend's ally picker needs a real second player; its button is laid out with the rest but its selection is not
        // exercised by this single-player fixture.
        _report.Skip("rest_options_mend_needs_multiplayer",
            "Mend is constructed and laid out, but its ally picker needs a real multiplayer teammate; not tapped here");
        return room;
    }

    /// <summary>
    /// Puts a chosen option list on the current rest site's synchronizer (the list the room's own buttons are built from) and
    /// asks the room to rebuild them. Single player: the local player is the synchronizer's first rest site.
    /// </summary>
    private static void FillRestSiteOptions(NRestSiteRoom room, List<RestSiteOption> options)
    {
        object synchronizer = RunManager.Instance.RestSiteSynchronizer;
        FieldInfo? sites = synchronizer.GetType().GetField("_restSites", BindingFlags.Instance | BindingFlags.NonPublic);
        if (sites?.GetValue(synchronizer) is not System.Collections.IList list || list.Count == 0)
            throw new InvalidOperationException("the rest site synchronizer has no player rest site to fill");
        object playerRestSite = list[0]!;
        FieldInfo? stored = playerRestSite.GetType().GetField("options", BindingFlags.Instance | BindingFlags.Public);
        if (stored == null) throw new InvalidOperationException("the rest site synchronizer's option list moved");
        stored.SetValue(playerRestSite, new List<RestSiteOption>(options));
        room.Call("UpdateRestSiteOptions");
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    /// <summary>Enters a rest site, waits for it, and taps the option with this id; reports whether it was offered.</summary>
    private async Task<RestOptionTap> EnterAndTapOptionAsync(string optionId, ulong previousRoomId)
    {
        NRestSiteRoom room = await EnterRestSiteAsync(previousRoomId);
        await WaitSecondsAsync(1.0);
        List<NRestSiteButton> buttons = FindAll<NRestSiteButton>(room);
        NRestSiteButton? button = buttons.FirstOrDefault(candidate => candidate.Option.OptionId == optionId);
        _report.Check($"rest_options_{optionId.ToLowerInvariant()}_offered", button is not null,
            $"options={string.Join(",", buttons.Select(candidate => candidate.Option.OptionId))}");
        if (button is null) return new RestOptionTap(room, false);
        await TapRestOptionAsync(room, button, buttons.Count, optionId.ToLowerInvariant());
        return new RestOptionTap(room, true);
    }

    /// <summary>The room's own option count (the game's list), so a selecting tap can be proven not to have taken one.</summary>
    private static int RestOptionTotal(NRestSiteRoom room)
    {
        object? options = room.GetType().GetProperty("Options")?.GetValue(room);
        return options is System.Collections.IList list ? list.Count : -1;
    }

    /// <summary>
    /// The room's live option buttons: the tree still owns them and no rebuild has queued them for freeing. A rebuild
    /// (<c>UpdateRestSiteOptions</c>) leaves the replaced buttons as children until the frame ends; touching those after
    /// that throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    private static List<NRestSiteButton> LiveRestButtons(NRestSiteRoom room) =>
        FindAll<NRestSiteButton>(room)
            .Where(button => GodotObject.IsInstanceValid(button) && button.IsInsideTree() && !button.IsQueuedForDeletion())
            .ToList();

    /// <summary>Waits until the room has the option copy on screen (the game shows it while an option has focus).</summary>
    private async Task WaitForRestDescriptionAsync(NRestSiteRoom room)
    {
        try
        {
            await WaitUntilAsync(() =>
            {
                RichTextLabel? copy = room.GetNodeOrNull<RichTextLabel>("%Description");
                return copy != null && copy.IsVisibleInTree() && copy.GetContentHeight() > 0f && copy.Modulate.A > 0.9f;
            }, "the option description", MediumWait);
        }
        catch (TimeoutException)
        {
            // Reported by the checks that follow.
        }
    }

    /// <summary>
    /// Taps a rest option the host's way: the launcher's first tap only selects it (reticle, and the room's own description,
    /// checked against the options) and the second takes it; the reference host takes it on the first tap.
    /// </summary>
    private async Task TapRestOptionAsync(NRestSiteRoom room, NRestSiteButton button, int count, string label)
    {
        if (!GodotObject.IsInstanceValid(button)) return;
        string optionId = button.Option.OptionId;
        int before = RestOptionTotal(room);
        await _pointer.TapAsync(CenterOf(button));
        if (!RestOptionsTakeTwoTaps()) return;
        await WaitFramesAsync(3);
        _report.Check($"rest_option_{label}_first_tap_only_selects",
            RestOptionSelected(button) && RestOptionTotal(room) == before,
            $"reticle={RestOptionSelected(button)} options={before}->{RestOptionTotal(room)}");
        await WaitForRestDescriptionAsync(room);
        CheckRestDescriptionClear(room, count);
        await CaptureAsync($"rest_option_described_{count}_{label}");
        // The waits above can outlive a native rebuild of the row: take the option by its id from the room's live buttons.
        NRestSiteButton? again = FindAll<NRestSiteButton>(room)
            .FirstOrDefault(candidate => GodotObject.IsInstanceValid(candidate) && !candidate.IsQueuedForDeletion()
                && candidate.Option.OptionId == optionId);
        if (again != null) await _pointer.TapAsync(CenterOf(again));
        await WaitFramesAsync(2);
    }

    /// <summary>
    /// Presses an option and holds it (no release) so the room shows its description, captures and checks the copy, then moves
    /// off and releases outside: nothing is chosen.
    /// </summary>
    private async Task DescribeRestOptionAsync(NRestSiteRoom room, NRestSiteButton button, int count, string label, bool capture)
    {
        if (!RestOptionsTakeTwoTaps()) return;
        Vector2 point = CenterOf(button);
        Vector2 canvas = GetViewportSize();
        await _pointer.PressAsync(point);
        await WaitForRestDescriptionAsync(room);
        await WaitFramesAsync(4);
        if (capture) await CaptureAsync($"rest_option_described_{count}_{label}");
        CheckRestDescriptionClear(room, count);
        Vector2 away = new(canvas.X * 0.5f, canvas.Y * 0.2f);
        await _pointer.MoveAsync(point.Lerp(away, 0.5f), pressed: true);
        await _pointer.MoveAsync(away, pressed: true);
        await _pointer.ReleaseAsync(away);
        await WaitFramesAsync(3);
    }

    /// <summary>Press-and-holds the first and the last option of the room, checking the copy against the options each time.</summary>
    private async Task DescribeRestOptionsAsync(NRestSiteRoom room)
    {
        if (!RestOptionsTakeTwoTaps()) return;
        List<NRestSiteButton> live = LiveRestButtons(room).Where(button => button.IsVisibleInTree()).ToList();
        if (live.Count == 0) return;
        await DescribeRestOptionAsync(room, live[0], live.Count, live[0].Option.OptionId.ToLowerInvariant(), capture: true);
        // The first selection can outlive a row rebuild on a host; re-read the last option instead of pinning the old node.
        live = LiveRestButtons(room).Where(button => button.IsVisibleInTree()).ToList();
        if (live.Count > 1)
            await DescribeRestOptionAsync(room, live[^1], live.Count, live[^1].Option.OptionId.ToLowerInvariant(), capture: true);
    }

    /// <summary>
    /// The option was spent: the room's own Proceed is enabled and reachable on the host's phone frame. The ribbon slides in
    /// from the game's hide place over half a second, so the rectangle is read only once that slide has settled: three frames
    /// in a row at the same place, bounded so a tween that never stops still reports.
    /// </summary>
    private async Task CheckRestProceedAsync(string name, NRestSiteRoom room)
    {
        try
        {
            await WaitUntilAsync(() => room.ProceedButton.IsEnabled, $"{name}'s Proceed", MediumWait);
        }
        catch (TimeoutException)
        {
            // Reported below.
        }
        ulong started = Time.GetTicksMsec();
        Vector2 settled = room.ProceedButton.Position;
        int steady = 0;
        while (steady < 3 && Time.GetTicksMsec() - started < 1500)
        {
            await WaitFramesAsync(1);
            Vector2 now = room.ProceedButton.Position;
            steady = now.IsEqualApprox(settled) ? steady + 1 : 0;
            settled = now;
        }
        double settleSeconds = (Time.GetTicksMsec() - started) / 1000.0;
        Rect2 rect = room.ProceedButton.GetGlobalRect();
        _report.Check($"rest_options_{name}_proceed_reachable",
            room.ProceedButton.IsEnabled && PortraitSafeRect().Grow(1f).Encloses(rect),
            $"enabled={room.ProceedButton.IsEnabled} proceed={rect} safe={PortraitSafeRect()} " +
            $"settle={settleSeconds:F2}s steady={steady}");
        CheckRestProceedLane(name, room);
    }

    /// <summary>Every option type the game ships, constructed for this player. A type the constructor refuses is logged and
    /// left out rather than failing the fixture.</summary>
    private static (List<RestSiteOption> Options, List<string> Skipped) BuildEveryOption(Player player)
    {
        (string Id, Func<Player, RestSiteOption> Create)[] all =
        {
            ("HEAL", owner => new HealRestSiteOption(owner)),
            ("SMITH", owner => new SmithRestSiteOption(owner)),
            ("DIG", owner => new DigRestSiteOption(owner)),
            ("LIFT", owner => new LiftRestSiteOption(owner)),
            // Mend is multiplayer-only (its picker targets a teammate); it is built here so its button is laid out, but it is
            // never selected in this single-player fixture.
            ("MEND", owner => new MendRestSiteOption(owner)),
            ("CLONE", owner => new CloneRestSiteOption(owner)),
            ("COOK", owner => new CookRestSiteOption(owner)),
            ("EVOLVE", owner => new EvolveRestSiteOption(owner)),
            ("HATCH", owner => new HatchRestSiteOption(owner)),
            ("KINDLE", owner => new KindleRestSiteOption(owner)),
            ("RESTORE", owner => new RestoreRestSiteOption(owner)),
        };
        var options = new List<RestSiteOption>(all.Length);
        var skipped = new List<string>();
        foreach ((string id, Func<Player, RestSiteOption> create) in all)
        {
            try
            {
                options.Add(create(player));
            }
            catch (Exception error)
            {
                skipped.Add(id);
                Log.Warn($"[TEST] rest-options: could not build {id} in single player: {error.GetBaseException().Message}");
            }
        }
        return (options, skipped);
    }

    /// <summary>Enters a rest site and waits for a room other than <paramref name="previousRoomId"/>.</summary>
    private async Task<NRestSiteRoom> EnterRestSiteAsync(ulong previousRoomId)
    {
        await RunManager.Instance.EnterRoomDebug(RoomType.RestSite, showTransition: false);
        await WaitUntilAsync(
            () => NRestSiteRoom.Instance is { } candidate && GodotObject.IsInstanceValid(candidate) && candidate.IsInsideTree()
                  && candidate.GetInstanceId() != previousRoomId,
            "rest site room",
            MediumWait);
        await WaitFramesAsync(6);
        return NRestSiteRoom.Instance!;
    }

    /// <summary>The first visible screen of this type, waited for because the game opens it from a command continuation.</summary>
    private async Task<T> WaitForVisibleScreenAsync<T>(string what, TimeSpan timeout) where T : Control
    {
        T? found = null;
        await WaitUntilAsync(() =>
        {
            found = FindAll<T>(_tree.Root).FirstOrDefault(node => GodotObject.IsInstanceValid(node) && node.IsVisibleInTree());
            return found is not null;
        }, what, timeout);
        return found!;
    }

    private async Task WaitForClosedAsync(Control screen, string what)
    {
        try
        {
            await WaitUntilAsync(() => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree(), what, MediumWait);
        }
        catch (TimeoutException)
        {
            // The caller's effect check reports the consequence.
        }
        await WaitFramesAsync(4);
    }

    /// <summary>
    /// Runs one option phase. A failure (a missing option, a screen that never answered, a model the game refuses) is
    /// reported as its own step and the overlay stack is cleared, so the remaining phases still run and RESULTS.json is
    /// written; only an engine-level abort would lose the run.
    /// </summary>
    private async Task<NRestSiteRoom> GuardPhaseAsync(string name, NRestSiteRoom room,
        Func<NRestSiteRoom, Task<NRestSiteRoom>> phase)
    {
        try
        {
            return await phase(room);
        }
        catch (Exception error)
        {
            _report.Fail($"rest_options_{name}_phase", error.GetBaseException().Message);
            Log.Error($"[TEST] rest-options: {name} phase failed: {error}");
            try
            {
                NOverlayStack.Instance?.Clear();
            }
            catch (Exception clear)
            {
                Log.Warn($"[TEST] rest-options: could not clear overlays after {name}: {clear.GetBaseException().Message}");
            }
            await WaitFramesAsync(6);
            return NRestSiteRoom.Instance is { } live && GodotObject.IsInstanceValid(live) ? live : room;
        }
    }

    /// <summary>
    /// Puts a ByrdonisEgg in the player's deck the way the game's own Byrdonis Nest event does: the card is created for the
    /// player (so it has an owner) and added through the native pile command. Adding an ownerless model directly to the
    /// deck leaves the run's models inconsistent and is not a path the game takes.
    /// </summary>
    private static async Task AddByrdonisEggAsync(Player player)
    {
        RunState state = RunManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("the run state disappeared");
        CardModel egg = state.CreateCard<ByrdonisEgg>(player);
        await CardPileCmd.Add(egg, PileType.Deck, CardPilePosition.Bottom, clonedBy: null, skipVisuals: true);
    }

    private static Girya? GiryaOf(Player player) => player.GetRelic<Girya>();
}
