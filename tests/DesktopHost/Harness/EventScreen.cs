using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The standard-event scenario (L7): enter a real default-scene event, inspect the page, drag its copy and choices (a drag
/// must never choose), take the first option, see the result page pinned, and Proceed back to the map. The launcher's
/// composition (art crop and fade, the text slot, four pinned rows, the first drawn frame, exclusions) lives in
/// LauncherEventChecks.cs.
/// </summary>
internal sealed partial class TestDriver
{
    private async Task RunEventScenarioAsync(RunState runState)
    {
        await WaitFramesAsync(4);
        BeginEventLayoutProbe();
        await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: ModelDb.Event<ByrdonisNest>(), showTransition: false);
        await WaitUntilAsync(
            () => NEventRoom.Instance is { } room
                  && GodotObject.IsInstanceValid(room)
                  && room.Layout is not null
                  && room.Layout.OptionButtons.Any(),
            "event options",
            MediumWait);
        await WaitSecondsAsync(1.0);

        NEventRoom eventRoom = NEventRoom.Instance!;
        NEventLayout layout = eventRoom.Layout!;
        List<NEventOptionButton> options = layout.OptionButtons.ToList();
        _report.Check("event_room_loaded", eventRoom.IsInsideTree(), "event room is present");
        _report.Check("event_has_options", options.Count > 0, $"options={options.Count}");
        foreach (NEventOptionButton option in options)
        {
            Log.Info($"[TEST] event option: size={option.Size} minimum={option.CustomMinimumSize} text={option.Option.Title.GetRawText()}");
        }

        CheckEventOptionMinimums(options);

        List<MegaRichTextLabel> eventLabels = FindAll<MegaRichTextLabel>(eventRoom);
        foreach (MegaRichTextLabel label in eventLabels)
        {
            Log.Info($"[TEST] event label: text={label.Text} size={label.Size} font={label.GetThemeFontSize("normal_font_size")} min={label.MinFontSize} max={label.MaxFontSize} auto={label.AutoSizeEnabled}");
        }
        CheckEventLabelFonts(eventLabels);

        var choices = layout.GetNodeOrNull<ScrollContainer>(EventChoicesNodeName());
        var textSlot = layout.GetNodeOrNull<Control>(EventTextSlotNodeName());
        Vector2? initialChoice = choices is null ? null : options[0].GlobalPosition;
        Rect2? initialTextSlot = textSlot?.GetGlobalRect();
        CheckEventPage(layout, options, eventLabels);
        await CheckEventLayoutProbeAsync("event");
        await CaptureAsync("event_01_options");
        await CheckEventDragAsync(eventRoom, layout, options);

        int beforeMaxHp = runState.Players[0].Creature.MaxHp;
        await TapEventOptionWithDiagnosticsAsync("event_option", options[0]);
        await WaitUntilAsync(() => runState.Players[0].Creature.MaxHp > beforeMaxHp, "event option applies its effect", MediumWait);
        await WaitUntilAsync(() => layout.OptionButtons.Any(o => o.Option.IsProceed), "event proceed option", MediumWait);
        var proceed = layout.OptionButtons.First(o => o.Option.IsProceed);
        await WaitForEventResultRevealAsync(layout, proceed);
        _report.Pass("event_tap_applies_effect", $"maxHP={beforeMaxHp}->{runState.Players[0].Creature.MaxHp}");
        if (choices is not null && textSlot is not null)
        {
            _report.Check("event_first_choice_stays_pinned_on_next_page", initialChoice is { } before
                && proceed.GlobalPosition.IsEqualApprox(before), $"first={initialChoice} proceed={proceed.GlobalPosition}");
            _report.Check("event_text_reserve_stays_fixed_on_next_page", initialTextSlot is { } first
                && textSlot.GetGlobalRect().IsEqualApprox(first), $"before={initialTextSlot} after={textSlot.GetGlobalRect()}");
        }
        else CheckEventLandscape(layout, choices, textSlot);
        await CaptureAsync("event_02_result");
        await _pointer.TapAsync(CenterOf(proceed));
        await WaitUntilAsync(() => NMapScreen.Instance?.IsOpen == true, "event proceed returns to map", MediumWait);
        _report.Pass("event_proceed_returns_to_map");
        await CheckEventExclusionsAsync(runState);
    }

    /// <summary>The launcher pins four full-size rows.</summary>
    private partial void CheckEventOptionMinimums(IReadOnlyList<NEventOptionButton> options);

    /// <summary>The host's composed event nodes, by the launcher's own names.</summary>
    private partial string EventArtNodeName();
    private partial string EventArtFadeNodeName();
    private partial string EventTextSlotNodeName();
    private partial string EventChoicesNodeName();
    private partial string EventRoomCoverNodeName();

    /// <summary>The host's gesture-arbitration state around a choice tap (drag flags, hit pads, claimed presses).</summary>
    private partial string EventTapHostDiagnostics(NEventOptionButton option);

    /// <summary>The event's own text at the host's readable size.</summary>
    private partial void CheckEventLabelFonts(IReadOnlyList<MegaRichTextLabel> labels);

    /// <summary>The whole page: the launcher's crop, fade, slot and pinned rows.</summary>
    private partial void CheckEventPage(NEventLayout layout, IReadOnlyList<NEventOptionButton> options,
        IReadOnlyList<MegaRichTextLabel> labels);

    /// <summary>Starts the drawn-frame sample before the room is entered (the launcher's no-snap check).</summary>
    private partial void BeginEventLayoutProbe();

    /// <summary>Checks the sampled frames after the page has settled.</summary>
    private partial Task CheckEventLayoutProbeAsync(string phase);

    private Task WaitForEventResultRevealAsync(NEventLayout layout, NEventOptionButton proceed)
    {
        var resultCopy = layout.GetNodeOrNull<RichTextLabel>("%EventDescription");
        return WaitUntilAsync(() => proceed.IsVisibleInTree() && proceed.MouseFilter == Control.MouseFilterEnum.Stop
            && proceed.Modulate.A >= 0.99f
            && (resultCopy is null || (resultCopy.VisibleRatio >= 0.99f && resultCopy.Modulate.A >= 0.99f)),
            "event result finishes its native reveal", MediumWait);
    }

    /// <summary>A drag over the copy and the choices scrolls at most and never chooses (the launcher's gesture).</summary>
    private partial Task CheckEventDragAsync(NEventRoom room, NEventLayout layout, IReadOnlyList<NEventOptionButton> options);

    /// <summary>The page after Proceed: shared pin checks when portrait containers exist, else the native landscape.</summary>
    private partial void CheckEventLandscape(NEventLayout layout, ScrollContainer? choices, Control? textSlot);

    /// <summary>Launcher-only: a live combat event keeps its tableau and a custom event gets no event composition.</summary>
    private partial Task CheckEventExclusionsAsync(RunState state);
}
