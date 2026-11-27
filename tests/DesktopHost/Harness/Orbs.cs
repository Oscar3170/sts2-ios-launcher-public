using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// <c>orbs</c>: the Defect queue's behaviour from three orbs to the native maximum and back. The counts and the native
/// queue order are host-neutral; the arc's placement, spacing and value labels are checked by each host's own
/// <see cref="CheckOrbLayout"/> (OrbChecks.cs), and the launcher additionally samples every drawn frame across each
/// transition to prove the arc tweens instead of snapping.
/// </summary>
internal sealed partial class TestDriver
{
    private string DescribeOrbInspection(Control bounds)
    {
        Vector2 target = CenterOf(bounds);
        var chain = new System.Collections.Generic.List<string>();
        for (Node? node = bounds; node is not null; node = node.GetParent())
        {
            if (node is not Control control) continue;
            chain.Add($"{control.Name}: {control.MouseFilter}/{control.MouseBehaviorRecursive} " +
                $"clip={control.ClipContents} inside={control.GetGlobalRect().HasPoint(target)} visible={control.IsVisibleInTree()}");
        }
        return $"bounds={bounds.GetGlobalRect()} hovered={_tree.Root.GuiGetHoveredControl()?.GetPath()} {string.Join("; ", chain)}";
    }

    private async Task RunOrbsAsync(RunState state)
    {
        var player = state.Players[0];
        _report.Check("orb_fixture_is_defect", player.Character is Defect, player.Character.GetType().Name);
        var room = await EnterCombatAsync();
        await WaitSecondsAsync(2);
        var context = new ThrowingPlayerChoiceContext();
        Transform2D background = room.Background.GetGlobalTransform();
        Control[] controls = { room.Ui.DrawPile, room.Ui.EnergyCounterContainer, room.Ui.EndTurnButton, room.Ui.DiscardPile };
        Rect2[] rail = controls.Select(c => c.GetGlobalRect()).ToArray();
        BeginOrbMotionProbe("three");
        await OrbCmd.Channel<FrostOrb>(context, player);
        await OrbCmd.Channel<DarkOrb>(context, player);
        await WaitSecondsAsync(0.8);
        EndOrbMotionProbe("three");
        CheckOrbBounds("three", 3);
        foreach (NOrb orb in FindAll<NOrb>(_tree.Root).Where(o => !o.IsQueuedForDeletion()))
        {
            Control bounds = orb.GetNode<Control>("Bounds");
            await _pointer.PressAsync(CenterOf(bounds));
            await WaitSecondsAsync(0.3);
            string id = $"orbs_ordinary_{orb.GetInstanceId()}";
            bool visibleTip = FindNativeTip(bounds) is not null;
            _report.Check(id + "_native_inspection", visibleTip, visibleTip
                ? "stationary press opens the native orb tooltip"
                : DescribeOrbInspection(bounds));
            await _pointer.ReleaseAsync(CenterOf(bounds));
            await _pointer.MoveAsync(PointerParkingPosition(), pressed: false);
            await WaitFramesAsync(3);
            if (_pointer.Mode == PointerMode.Touch)
                _report.Check(id + "_hold_tip_cleared", FindNativeTip(bounds) is null,
                    "the held tip is gone once the pointer leaves the orb");
        }
        await CaptureAsync("orbs_01_three");
        var originals = room.CreatureNodes.ToDictionary(c => c.GetInstanceId(), c => c.GlobalPosition);
        BeginOrbMotionProbe("five");
        await OrbCmd.AddSlots(player, 2);
        await OrbCmd.Channel<LightningOrb>(context, player);
        await OrbCmd.Channel<FrostOrb>(context, player);
        await WaitSecondsAsync(0.8);
        EndOrbMotionProbe("five");
        CheckOrbBounds("five", 5);
        await CaptureAsync("orbs_02_five");
        BeginOrbMotionProbe("evoked");
        await OrbCmd.EvokeNext(context, player);
        await WaitSecondsAsync(0.8);
        EndOrbMotionProbe("evoked");
        CheckOrbBounds("evoked", 5);
        await CaptureAsync("orbs_03_evoked");
        var queue = player.PlayerCombatState!.OrbQueue;
        BeginOrbMotionProbe("six");
        await OrbCmd.AddSlots(player, 1);
        await OrbCmd.Channel<PlasmaOrb>(context, player);
        await OrbCmd.Channel<GlassOrb>(context, player);
        await WaitSecondsAsync(0.8);
        EndOrbMotionProbe("six");
        CheckOrbBounds("six", 6);
        await CaptureAsync("orbs_03b_six");
        BeginOrbMotionProbe("eight");
        await OrbCmd.AddSlots(player, 2);
        await OrbCmd.Channel<FrostOrb>(context, player);
        await OrbCmd.Channel<DarkOrb>(context, player);
        await WaitSecondsAsync(0.8);
        EndOrbMotionProbe("eight");
        CheckOrbBounds("eight", 8);
        await CaptureAsync("orbs_03c_eight");
        BeginOrbMotionProbe("max");
        await OrbCmd.AddSlots(player, OrbQueue.maxCapacity - queue.Capacity);
        int added = 0;
        while (queue.Orbs.Count < OrbQueue.maxCapacity)
        {
            switch (added++ % 5)
            {
                case 0: await OrbCmd.Channel<FrostOrb>(context, player); break;
                case 1: await OrbCmd.Channel<DarkOrb>(context, player); break;
                case 2: await OrbCmd.Channel<LightningOrb>(context, player); break;
                case 3: await OrbCmd.Channel<PlasmaOrb>(context, player); break;
                case 4: await OrbCmd.Channel<GlassOrb>(context, player); break;
            }
        }
        await WaitSecondsAsync(0.8);
        EndOrbMotionProbe("max");
        _report.Check("orbs_native_max_filled", queue.Capacity == OrbQueue.maxCapacity && queue.Orbs.Count == OrbQueue.maxCapacity,
            $"capacity={queue.Capacity} filled={queue.Orbs.Count} nativeMax={OrbQueue.maxCapacity}");
        _report.Check("orbs_max_all_five_types", queue.Orbs.Select(o => o.GetType()).Distinct().Count() == 5,
            string.Join(",", queue.Orbs.Select(o => o.GetType().Name)));
        await OrbCmd.AddSlots(player, 1);
        _report.Check("orbs_native_max_is_enforced", queue.Capacity == OrbQueue.maxCapacity, $"capacity={queue.Capacity}");
        CheckOrbBounds("max", OrbQueue.maxCapacity);
        await CaptureAsync("orbs_04_max_filled");
        NOrbManager manager = FindAll<NOrbManager>(_tree.Root).First();
        var full = AccessTools.FieldRefAccess<NOrbManager, System.Collections.Generic.List<NOrb>>("_orbs")(manager);
        _report.Check("orbs_max_native_queue_order", full.Select(o => o.Model).SequenceEqual(queue.Orbs),
            "no reordering: native first orb is rightmost, native last is leftmost");
        var front = queue.Orbs[0];
        var blockBefore = player.Creature.Block;
        BeginOrbMotionProbe("max_overflow");
        await OrbCmd.Channel<FrostOrb>(context, player);
        await WaitSecondsAsync(0.8);
        EndOrbMotionProbe("max_overflow");
        _report.Check("orbs_overflow_evokes_front", !queue.Orbs.Contains(front) && queue.Orbs.Count == OrbQueue.maxCapacity,
            "channel into full queue evokes oldest orb and retains ten filled slots");
        _report.Check("orbs_overflow_native_frost_effect", front is FrostOrb && player.Creature.Block > blockBefore,
            $"block={blockBefore}->{player.Creature.Block}");
        CheckOrbBounds("max_overflow", OrbQueue.maxCapacity);
        await CaptureAsync("orbs_05_max_overflow");
        BeginOrbMotionProbe("max_evoked");
        await OrbCmd.EvokeNext(context, player);
        await WaitSecondsAsync(0.8);
        EndOrbMotionProbe("max_evoked");
        _report.Check("orbs_max_evoke_leaves_empty_slot", queue.Capacity == OrbQueue.maxCapacity && queue.Orbs.Count == OrbQueue.maxCapacity - 1,
            $"capacity={queue.Capacity} filled={queue.Orbs.Count}");
        CheckOrbBounds("max_evoked", OrbQueue.maxCapacity);
        BeginOrbMotionProbe("reduced");
        OrbCmd.RemoveSlots(player, OrbQueue.maxCapacity - 3);
        await WaitSecondsAsync(0.8);
        EndOrbMotionProbe("reduced");
        CheckOrbBounds("reduced", 3);
        _report.Check("orbs_capacity_reduction", queue.Capacity == 3 && queue.Orbs.Count == 3,
            $"capacity={queue.Capacity} filled={queue.Orbs.Count}");
        _report.Check("orb_changes_do_not_move_stage", room.CreatureNodes.All(c =>
            originals.TryGetValue(c.GetInstanceId(), out Vector2 p) && p.DistanceTo(c.GlobalPosition) < 2f), "channel, max capacity, overflow, evoke, reduction retain actor roots");
        _report.Check("orb_changes_keep_stage_framing", room.Background.GetGlobalTransform().IsEqualApprox(background)
            && controls.Select(c => c.GetGlobalRect()).SequenceEqual(rail), "orb layout never reframes the room or the action rail");
        await CaptureAsync("orbs_06_reduced");
    }
}
