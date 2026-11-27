using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// Variant coverage for standard events (L7), shared by both hosts: every act's widest and longest pages, not just
/// <c>ByrdonisNest</c>.
/// <para>
/// The selection is read from v0.113.1's event models. Four visible options is the native maximum on an initial page
/// (<c>WelcomeToWongos</c>; its three priced rows plus Leave, <c>WelcomeToWongos.cs</c> GenerateInitialOptions). No standard
/// event reaches five: <c>SelfHelpBook</c>, <c>TeaMaster</c>, <c>WaterloggedScriptorium</c> and <c>RanwidTheElder</c> carry
/// five <c>new EventOption</c> sites but each pair is one always-present row (unlocked/locked), so the page has three. The
/// longest copy is taken from the story-rich shared events (<c>WelcomeToWongos</c>, <c>RanwidTheElder</c>, <c>SelfHelpBook</c>)
/// and the wide act pages (<c>WoodCarvings</c>, <c>LuminousChoir</c>, <c>WaterloggedScriptorium</c>, <c>WuppyInTrouble</c>,
/// <c>ZenWeaver</c>, <c>Amalgamator</c>, <c>BattlewornDummy</c>, <c>ThermaeNonupeipe</c>), including the four the known
/// 9:16 clipping report names (Wood Carvings, Wuppy, Amalgamator, Welcome to Wongos). The localized
/// strings themselves are only inside the game pack, so the copy length is measured from the live
/// <c>%EventDescription</c> and logged per event rather than pre-sorted from the reference.
/// </para>
/// <para>
/// For each event the page must fit the host's portrait contract (row minimums, readable copy, the host's page check), the
/// copy must fit its slot or scroll to its last line, every option must sit inside the safe rect without overlap, and a page
/// that cannot show all rows at once must scroll without selecting. Two events then take an option and prove the result page
/// and Proceed. The host layout answers are the existing partials (LauncherEventChecks.cs, HostLayoutChecks.cs).
/// </para>
/// </summary>
internal sealed partial class TestDriver
{
    private async Task RunEventsTightAsync(RunState state)
    {
        // Act, model, and whether this page takes an option. Four-option WelcomeToWongos and three-option RanwidTheElder
        // finish on a plain click (Leave / GiveGold), so no nested deck picker is involved. The layout-only pages are
        // entered without choosing, as the existing tour does.
        var plan = new (string Act, EventModel Model, bool Choose)[]
        {
            ("Overgrowth", ModelDb.Event<WoodCarvings>(), false),
            ("Overgrowth", ModelDb.Event<LuminousChoir>(), false),
            ("Underdocks", ModelDb.Event<WaterloggedScriptorium>(), false),
            ("Underdocks", ModelDb.Event<WuppyInTrouble>(), false),
            ("Hive", ModelDb.Event<ZenWeaver>(), false),
            ("Hive", ModelDb.Event<Amalgamator>(), false),
            ("Glory", ModelDb.Event<BattlewornDummy>(), false),
            ("Glory", ModelDb.Event<ThermaeNonupeipe>(), false),
            ("Glory", ModelDb.Event<WelcomeToWongos>(), true),
            ("Glory", ModelDb.Event<SelfHelpBook>(), false),
            ("Glory", ModelDb.Event<RanwidTheElder>(), true),
        };

        string currentAct = "";
        int measuredPages = 0;
        foreach (var (act, model, choose) in plan)
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            if (!string.Equals(act, currentAct, StringComparison.Ordinal))
            {
                await ConfigureTourActAsync(state, act);
                currentAct = act;
            }

            string name = "events_tight_" + model.GetType().Name;
            int deckBefore = state.Players[0].Deck.Cards.Count;
            int relicsBefore = state.Players[0].Relics.Count;
            // The 4-option page is where the layout has the least room: sample it from before the room is entered so the
            // rows must already hold their final place and height on the first drawn frame. Amalgamator's long copy
            // overflows its slot, so its sample also covers the copy's scrollbar against the native reveal.
            bool probe = model is WelcomeToWongos or Amalgamator;
            if (probe) BeginEventLayoutProbe();
            await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: model, showTransition: false);
            await WaitUntilAsync(() => NEventRoom.Instance is { } room
                    && GodotObject.IsInstanceValid(room)
                    && room.Layout is { } layout
                    && layout.OptionButtons.Any(),
                name + " event page", MediumWait);
            await WaitSecondsAsync(1.2);

            NEventRoom eventRoom = NEventRoom.Instance!;
            NEventLayout page = eventRoom.Layout!;
            List<NEventOptionButton> options = page.OptionButtons.ToList();
            _report.Check(name + "_has_options", options.Count > 0 && options.All(o => o.IsVisibleInTree()),
                $"options={options.Count} titles={string.Join("|", options.Select(o => o.Option.Title.GetRawText()))}");
            if (model is WelcomeToWongos)
                _report.Check(name + "_four_option_maximum", options.Count == 4,
                    $"welcome-to-wongos is v0.113.1's widest initial standard page; options={options.Count}");
            else if (model is Amalgamator)
                _report.Check(name + "_two_option_long_copy", options.Count == 2,
                    $"amalgamator's long page has two rows; options={options.Count}");

            bool portrait = IsPortraitHost();
            bool composed = page.GetNodeOrNull<Control>(EventTextSlotNodeName()) is not null;
            List<MegaRichTextLabel> labels = FindAll<MegaRichTextLabel>(eventRoom);
            if (portrait && composed)
            {
                // The host's own page contract first (row minimums, readable fonts, art/fade/slot, native VFX).
                CheckEventOptionMinimums(options);
                CheckEventLabelFonts(labels);
                CheckEventPage(page, options, labels);
                await CheckTightEventPageAsync(name, eventRoom, page, options);
            }
            else
            {
                // Landscape keeps the native composition; an unillustrated event also keeps the game's own rows.
                _report.Skip(name + "_portrait_composition",
                    portrait ? "event has no portrait composition (native rows kept)" : "landscape keeps the native event layout");
                Rect2 safe = PortraitSafeRect();
                _report.Check(name + "_options_in_frame",
                    options.All(o => safe.Grow(1f).Encloses(o.GetGlobalRect())),
                    $"options={string.Join(", ", options.Select(o => o.GetGlobalRect()))} safe={safe}");
            }

            await CaptureAsync(name);
            if (probe) await CheckEventLayoutProbeAsync(name);
            measuredPages++;
            Log.Info($"[TEST] {name}: options={options.Count} portrait={portrait} composed={composed}");

            if (choose) await ChooseTightEventOptionAsync(name, state, eventRoom, page, deckBefore, relicsBefore);
        }

        _report.Check("events_tight_pages_covered", measuredPages >= 6,
            $"pages={measuredPages} across Overgrowth/Underdocks/Hive/Glory");
        _report.Pass("events_tight_done",
            "widest and longest standard-event pages were laid out and scrolled; the two plain-action pages were exercised");
    }

    /// <summary>
    /// The shared variant checks: the copy fits or scrolls to its last line, the options sit in the safe rect without
    /// overlap, and a page that cannot show every row at once scrolls instead of selecting.
    /// </summary>
    private async Task CheckTightEventPageAsync(string name, NEventRoom room, NEventLayout layout,
        IReadOnlyList<NEventOptionButton> options)
    {
        Rect2 safe = PortraitSafeRect();
        Rect2[] rects = options.Select(option => option.GetGlobalRect()).ToArray();
        bool inSafe = rects.All(rect => safe.Grow(1f).Encloses(rect));
        bool ordered = true;
        for (int i = 1; i < rects.Length; i++)
            if (rects[i].Position.Y < rects[i - 1].End.Y - 1f) ordered = false;
        bool noOverlap = true;
        for (int i = 0; i < rects.Length; i++)
            for (int j = i + 1; j < rects.Length; j++)
                if (rects[i].Intersection(rects[j]).Size.X > 1f && rects[i].Intersection(rects[j]).Size.Y > 1f)
                    noOverlap = false;
        _report.Check(name + "_options_inside_safe_rect", inSafe,
            $"rects={string.Join(", ", rects.Select(r => r.ToString()))} safe={safe}");
        _report.Check(name + "_options_ordered_without_overlap", ordered && noOverlap,
            $"heights={string.Join(",", rects.Select(r => r.Size.Y.ToString("F0")))}");

        // Every row's own label must be inside the row and its wrapped ink inside the label's box.
        bool optionTextFits = options.All(option =>
        {
            Control? label = option.GetNodeOrNull<Control>("%Text");
            if (label is null || label.GetGlobalRect().Size.Y < 1f) return false;
            if (!option.GetGlobalRect().Grow(1f).Encloses(label.GetGlobalRect())) return false;
            return label is not RichTextLabel rich || rich.GetContentHeight() <= rich.Size.Y + 1f;
        });
        _report.Check(name + "_option_text_fits_rows", optionTextFits,
            $"labels={string.Join(",", options.Select(option => option.GetNodeOrNull<Control>("%Text")?.GetGlobalRect().Size.Y.ToString("F0") ?? "missing"))}");

        var description = layout.GetNodeOrNull<RichTextLabel>("%EventDescription");
        var slot = layout.GetNodeOrNull<Control>(EventTextSlotNodeName());
        var textScroll = slot?.GetNodeOrNull<ScrollContainer>("Panel/Text");
        if (description is null || slot is null || textScroll is null)
        {
            _report.Fail(name + "_copy_slot", "the portrait text slot is missing its description scroll");
        }
        else
        {
            float ink = description.GetContentHeight();
            int characters = description.GetTotalCharacterCount();
            bool scrolling = textScroll.GetVScrollBar().IsVisibleInTree();
            bool fits = ink <= textScroll.Size.Y + 1f
                && description.GetGlobalRect().Size.Y <= textScroll.Size.Y + 1f;
            _report.Check(name + "_copy_fits_slot_or_scrolls", fits || scrolling,
                $"chars={characters} ink={ink:F0} viewport={textScroll.Size} font={description.GetThemeFontSize("normal_font_size")} scroll={scrolling}");
            _report.Pass(name + "_copy_measured", $"chars={characters} ink={ink:F0} scroll={scrolling}");

            if (scrolling)
            {
                Rect2 frame = textScroll.GetGlobalRect();
                float lastInk = description.GetGlobalRect().Position.Y + ink;
                string original = description.Text;
                for (int i = 0; i < 8 && lastInk > frame.End.Y + 1f; i++)
                {
                    Vector2 start = new(frame.GetCenter().X, frame.End.Y - 24f);
                    await _pointer.DragAsync(start, start - new Vector2(0f, frame.Size.Y * 0.55f), 20);
                    await WaitFramesAsync(4);
                    if (NEventRoom.Instance != room || !GodotObject.IsInstanceValid(description)) break;
                    lastInk = description.GetGlobalRect().Position.Y + ink;
                }
                bool copyStayed = NEventRoom.Instance == room && GodotObject.IsInstanceValid(description)
                    && description.Text == original && options.All(o => GodotObject.IsInstanceValid(o) && o.IsVisibleInTree());
                _report.Check(name + "_copy_last_line_reachable_by_drag", copyStayed && lastInk <= frame.End.Y + 1f,
                    $"offset={textScroll.ScrollVertical} lastInk={lastInk:F0} frameBottom={frame.End.Y:F0} stayed={copyStayed}");
                if (copyStayed) await CaptureAsync(name + "_copy_scrolled");
                if (textScroll.ScrollVertical > 0)
                {
                    Rect2 back = textScroll.GetGlobalRect();
                    await _pointer.DragAsync(new Vector2(back.GetCenter().X, back.Position.Y + 24f),
                        new Vector2(back.GetCenter().X, back.End.Y - 24f), 20);
                    await WaitFramesAsync(4);
                }
            }
        }

        if (layout.GetNodeOrNull<ScrollContainer>(EventChoicesNodeName()) is not { } choices)
        {
            _report.Fail(name + "_choices_scroll", "the portrait choices scroll is missing");
            return;
        }

        Rect2 clip = choices.GetGlobalRect().Intersection(safe).Grow(2f);
        // The native hover tween expands the artwork beyond its slot. Its unscaled layout bounds, not the
        // transient hover outline, determine whether the scroll can expose the whole choice.
        bool FullyVisible(NEventOptionButton option) => GodotObject.IsInstanceValid(option)
            && option.IsVisibleInTree() && option.GetParent() is Control parent
            && clip.Encloses(parent.GetGlobalTransform() * new Rect2(option.Position, option.Size));
        var remaining = options.Where(option => !FullyVisible(option)).ToHashSet();
        VScrollBar bar = choices.GetVScrollBar();
        if (remaining.Count == 0)
        {
            _report.Check(name + "_options_fit_without_scrolling",
                !bar.IsVisibleInTree() && choices.ScrollVertical == 0,
                $"options={options.Count} scrollbar={bar.IsVisibleInTree()} offset={choices.ScrollVertical}");
            return;
        }

        _report.Check(name + "_options_scroll_affordance", bar.IsVisibleInTree() && bar.MaxValue > bar.Page,
            $"bar={bar.GetGlobalRect()} range={bar.MaxValue - bar.Page:F0} remaining={remaining.Count}");
        var initial = options.Where(o => o.IsEnabled).ToArray();
        bool stayed = true;
        for (int i = 0; i < 10 && remaining.Count > 0; i++)
        {
            Vector2 start = new(clip.GetCenter().X, clip.End.Y - 90f);
            int before = choices.ScrollVertical;
            await _pointer.DragAsync(start, start - new Vector2(0f, clip.Size.Y * 0.40f), 20);
            await WaitFramesAsync(4);
            stayed = NEventRoom.Instance == room && initial.All(o => GodotObject.IsInstanceValid(o) && o.IsEnabled && o.IsVisibleInTree());
            if (!stayed || !GodotObject.IsInstanceValid(choices) || choices.ScrollVertical <= before) break;
            remaining.RemoveWhere(FullyVisible);
        }
        _report.Check(name + "_scroll_reveals_all_options_without_selecting", stayed && remaining.Count == 0,
            $"remaining={remaining.Count} stayedInEvent={stayed}");
        if (stayed) await CaptureAsync(name + "_options_scrolled");
    }

    /// <summary>
    /// Take one option that resolves in place (no nested deck picker), wait for the native result page, and Proceed back
    /// to the map. <c>WelcomeToWongos</c> takes its Leave row; <c>RanwidTheElder</c> takes GiveGold.
    /// </summary>
    private async Task ChooseTightEventOptionAsync(string name, RunState state, NEventRoom room, NEventLayout layout,
        int deckBefore, int relicsBefore)
    {
        List<NEventOptionButton> options = layout.OptionButtons.ToList();
        NEventOptionButton choice = name.EndsWith("WelcomeToWongos", StringComparison.Ordinal)
            ? options[^1]
            : options[1];
        string diagnostics = await TapEventOptionWithDiagnosticsAsync(name, choice);
        bool progressed = await WaitForEventProceedAsync(room, layout, MediumWait);
        if (!progressed)
        {
            _report.Fail(name + "_choice_progresses", "native result page did not appear; " + diagnostics);
            await CaptureAsync(name + "_choice_failed");
            return;
        }

        NEventOptionButton proceed = layout.OptionButtons.First(option => option.Option.IsProceed);
        await WaitForEventResultRevealAsync(layout, proceed);
        bool effect = name.EndsWith("WelcomeToWongos", StringComparison.Ordinal)
            || (state.Players[0].Deck.Cards.Count == deckBefore
                && state.Players[0].Relics.Count == relicsBefore + 1);
        _report.Check(name + "_choice_progresses",
            proceed.IsVisibleInTree() && proceed.MouseFilter == Control.MouseFilterEnum.Stop && effect,
            $"options={layout.OptionButtons.Count()} deck={deckBefore}->{state.Players[0].Deck.Cards.Count} "
            + $"relics={relicsBefore}->{state.Players[0].Relics.Count}");
        await CaptureAsync(name + "_result");
        string proceedDiagnostics = await TapEventOptionWithDiagnosticsAsync(name + "_proceed", proceed);
        bool returned = await WaitForMapAsync(MediumWait);
        _report.Check(name + "_proceed_returns_to_map", returned,
            $"mapOpen={NMapScreen.Instance?.IsOpen == true}; " + proceedDiagnostics);
    }

    /// <summary>
    /// Tap an event option once and log the full native input path: the hovered control, the option's enabled/focus/filter
    /// state, its ancestors, whether the native MousePressed/MouseReleased signals actually reached the button, and the
    /// host's gesture arbitration (drag flags, hit pads, claimed press). Returns the same detail for a failing step.
    /// </summary>
    private async Task<string> TapEventOptionWithDiagnosticsAsync(string name, NEventOptionButton option)
    {
        Vector2 point = CenterOf(option);
        bool pressReached = false, releaseReached = false, nativeReleased = false;
        string atPress = "", atRelease = "";
        string State() => $"focused={MemberValue(option, "IsFocused")} pressed={MemberValue(option, "_isPressed")}";
        void SamplePress(InputEvent _)
        {
            pressReached = true;
            atPress = State();
        }
        Callable pressed = Callable.From<InputEvent>(SamplePress);
        Callable released = Callable.From((InputEvent _) => { releaseReached = true; atRelease = State(); });
        Callable releasedRan = Callable.From((GodotObject _) => { nativeReleased = true; });
        bool watchPress = option.HasSignal("MousePressed");
        bool watchRelease = option.HasSignal("MouseReleased");
        bool watchReleased = option.HasSignal("Released");
        if (watchPress) option.Connect("MousePressed", pressed);
        if (watchRelease) option.Connect("MouseReleased", released);
        if (watchReleased) option.Connect("Released", releasedRan);
        string before = DescribeEventTap(option, point);
        try
        {
            await _pointer.TapAsync(point);
            await WaitFramesAsync(4);
        }
        finally
        {
            if (GodotObject.IsInstanceValid(option))
            {
                if (watchPress) option.Disconnect("MousePressed", pressed);
                if (watchRelease) option.Disconnect("MouseReleased", released);
                if (watchReleased) option.Disconnect("Released", releasedRan);
            }
        }
        string detail = $"{name}: pressReached={pressReached} releaseReached={releaseReached} nativeReleased={nativeReleased} "
            + $"press=[{atPress}] release=[{atRelease}] "
            + $"before=[{before}] after=[{(GodotObject.IsInstanceValid(option) ? DescribeEventTap(option, point) : "option freed (page advanced)")}]";
        Log.Info("[TEST] event tap diagnostics " + detail);
        return detail;
    }

    private string DescribeEventTap(NEventOptionButton option, Vector2 point)
    {
        Control? hovered = option.GetViewport().GuiGetHoveredControl();
        var text = new System.Text.StringBuilder();
        text.Append($"point={point} title=\"{option.Option.Title.GetRawText()}\" locked={option.Option.IsLocked} ")
            .Append($"index={MemberValue(option, "Index")} _isPressed={MemberValue(option, "_isPressed")} ")
            .Append($"IsEnabled={option.IsEnabled} _isEnabled={MemberValue(option, "_isEnabled")} ")
            .Append($"IsFocused={MemberValue(option, "IsFocused")} ")
            .Append($"filter={option.MouseFilter} visible={option.IsVisibleInTree()} rect={option.GetGlobalRect()} ")
            .Append($"animInTween={DescribeTween(MemberValue(option, "_animInTween"))} ")
            .Append($"hovered={(hovered is null ? "none" : $"{hovered.Name}<{hovered.GetType().Name}> filter={hovered.MouseFilter} rect={hovered.GetGlobalRect()}")} ");
        text.Append("ancestors=[");
        for (Node? node = option.GetParent(); node is not null; node = node.GetParent())
        {
            if (node is Control control)
                text.Append($"{control.Name}<{control.GetType().Name}> filter={control.MouseFilter} ")
                    .Append($"rect={control.GetGlobalRect()} clip={control.ClipContents}; ");
            else text.Append($"{node.Name}<{node.GetType().Name}>; ");
        }
        text.Append("] host=").Append(EventTapHostDiagnostics(option));
        return text.ToString();
    }

    private static string DescribeTween(object? value) => value switch
    {
        null => "none",
        Tween tween => GodotObject.IsInstanceValid(tween) ? (tween.IsRunning() ? "running" : "finished") : "freed",
        _ => value.GetType().Name,
    };

    private static object? MemberValue(object target, string name)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.DeclaredOnly;
        for (Type? type = target.GetType(); type is not null; type = type.BaseType)
        {
            object? value = type.GetField(name, flags)?.GetValue(target)
                ?? type.GetProperty(name, flags)?.GetValue(target);
            if (value is not null) return value;
        }
        return null;
    }

    /// <summary>Waits for the page's Proceed row; false on timeout (the caller reports the choice result).</summary>
    private async Task<bool> WaitForEventProceedAsync(NEventRoom room, NEventLayout layout, TimeSpan timeout)
    {
        try
        {
            await WaitUntilAsync(() => NEventRoom.Instance == room
                    && ReferenceEquals(NEventRoom.Instance?.Layout, layout)
                    && layout.OptionButtons.Any(option => option.Option.IsProceed),
                "event result page", timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Waits for the map after Proceed; false on timeout so a lost tap can be retried.</summary>
    private async Task<bool> WaitForMapAsync(TimeSpan timeout)
    {
        try
        {
            await WaitUntilAsync(() => NMapScreen.Instance?.IsOpen == true, "map after Proceed", timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
