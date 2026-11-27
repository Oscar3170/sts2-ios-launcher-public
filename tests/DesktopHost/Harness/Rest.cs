using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// <c>rest</c>: a rest site entered from a wounded run, its native option buttons, the Heal option tapped through the
/// host's own input, and the native framing staying put while it heals. The room's own phone geometry (the buttons' touch
/// size, the options row's place and the act cover behind it) is a host partial: the launcher's answers are in
/// Host/LauncherRewardChecks.cs. The scenario ends with the shared card-screen fixture over the same rest site.
/// </summary>
internal sealed partial class TestDriver
{
    private async Task RunRestScenarioAsync(RunState runState)
    {
        runState.Players[0].Creature.SetCurrentHpInternal(runState.Players[0].Creature.MaxHp - 20);
        await WaitFramesAsync(4);
        await RunManager.Instance.EnterRoomDebug(RoomType.RestSite, showTransition: false);
        await WaitUntilAsync(
            () => NRestSiteRoom.Instance is { } room
                  && GodotObject.IsInstanceValid(room)
                  && room.IsInsideTree(),
            "rest site room",
            MediumWait);
        await WaitFramesAsync(6);

        NRestSiteRoom restRoom = NRestSiteRoom.Instance!;
        List<NRestSiteButton> buttons = FindAll<NRestSiteButton>(restRoom);
        _report.Check("rest_site_loaded", restRoom.IsInsideTree(), "rest site room is present");
        _report.Check("rest_site_has_buttons", buttons.Count > 0, $"buttons={buttons.Count}");
        await WaitSecondsAsync(1.0);

        foreach (NRestSiteButton button in buttons)
        {
            Log.Info($"[TEST] rest button {button.Name}: scale={button.Scale.X:F2} size={button.Size} parent={button.GetParent()?.Name} visible={button.IsVisibleInTree()} global={button.GlobalPosition}");
        }

        CheckRestSiteButtons(buttons);
        CheckRestRoomLayout(restRoom);

        await CaptureAsync("rest_01_room");
        // Each end of the row held down: the room shows that option's description, captured and checked against the options.
        await DescribeRestOptionsAsync(restRoom);
        int beforeHp = runState.Players[0].Creature.CurrentHp;
        var heal = buttons.First(b => b.Option.GetType().Name.Contains("Heal", StringComparison.OrdinalIgnoreCase));
        Transform2D frame = restRoom.GetNode<Control>("BgContainer").GetGlobalTransform();
        int optionCount = buttons.Count;
        int optionsBefore = RestOptionTotal(restRoom);
        // Select-then-confirm: the launcher's first tap only selects (the game's own reticle and option description), the
        // second takes the option.
        await _pointer.TapAsync(CenterOf(heal));
        if (RestOptionsTakeTwoTaps())
        {
            await WaitFramesAsync(3);
            _report.Check("rest_first_tap_only_selects",
                runState.Players[0].Creature.CurrentHp == beforeHp && RestOptionSelected(heal)
                && RestOptionTotal(restRoom) == optionsBefore,
                $"hp={runState.Players[0].Creature.CurrentHp} reticle={RestOptionSelected(heal)} " +
                $"options={optionsBefore}->{RestOptionTotal(restRoom)}");
            await WaitForRestDescriptionAsync(restRoom);
            // Past the native 1 s unhover tween: a selected option must still wear the game's hovered look.
            await WaitSecondsAsync(1.2);
            CheckRestSelectedLooksHovered(heal);
            CheckRestDescriptionClear(restRoom, optionCount);
            await CaptureAsync($"rest_option_described_{optionCount}_heal");
            // Check cancellation before healing: the native action consumes every rest option.
            Vector2 canvas = GetViewportSize();
            await _pointer.TapAsync(new Vector2(canvas.X * 0.5f, canvas.Y * 0.2f));
            _report.Check("rest_tap_elsewhere_lets_go",
                !RestOptionSelected(heal) && RestOptionTotal(restRoom) == optionsBefore
                && runState.Players[0].Creature.CurrentHp == beforeHp,
                $"selected={RestOptionSelected(heal)} options={optionsBefore}->{RestOptionTotal(restRoom)}");
            await _pointer.TapAsync(CenterOf(heal));
            _report.Check("rest_option_can_be_selected_again", RestOptionSelected(heal)
                && runState.Players[0].Creature.CurrentHp == beforeHp, "reselecting does not heal");
            await _pointer.TapAsync(CenterOf(heal));
        }
        await WaitUntilAsync(() => runState.Players[0].Creature.CurrentHp > beforeHp, "rest action heals", MediumWait);
        await WaitSecondsAsync(1.2);
        _report.Pass("rest_tap_heals", $"hp={beforeHp}->{runState.Players[0].Creature.CurrentHp}");
        _report.Check("rest_framing_stays_fixed", restRoom.GetNode<Control>("BgContainer").GetGlobalTransform().IsEqualApprox(frame), "native rest action does not reframe the room");
        await CaptureAsync("rest_02_after_heal");
        await RunCardScreenTestsAsync(runState);
    }
}
