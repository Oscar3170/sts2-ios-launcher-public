using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

internal sealed partial class TestDriver
{
    private async Task RunDecimillipedeAsync(RunState state)
    {
        const string encounterName = "DecimillipedeElite";
        var act = ModelDb.Acts.First(a => a.AllEncounters.Any(e => e.GetType().Name == encounterName));
        await ConfigureTourActAsync(state, act.GetType().Name);
        var player = state.Players[0].Creature;
        player.SetMaxHpInternal(9999);
        player.SetCurrentHpInternal(player.MaxHp);
        var encounter = state.Act.AllEncounters.First(e => e.GetType().Name == encounterName).ToMutable();
        await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: encounter, showTransition: false);
        await WaitUntilAsync(IsPlayerPlayPhase, "Decimillipede player turn", MediumWait);
        await WaitSecondsAsync(1.5);
        var room = NCombatRoom.Instance!;
        var segments = room.CreatureNodes.Where(c => c.Entity.Monster is DecimillipedeSegment)
            .OrderBy(c => c.Entity.SlotName, StringComparer.Ordinal).ToArray();
        _report.Check("decimillipede_native_segments", segments.Length == 3, $"segments={segments.Length}");
        if (segments.Length != 3) return;
        var authored = segments.ToDictionary(c => c, c => c.GetGlobalTransform());
        void CheckChain(string phase)
        {
            if (!IsPortraitHost()) return;
            Rect2[] bodies = segments.Select(s => s.Visuals.Bounds.GetGlobalRect()).ToArray();
            Rect2[] footprints = segments.Select((s, i) => s.Hitbox.GetGlobalRect().Merge(bodies[i])).ToArray();
            float[] scales = segments.Select(s => s.GetGlobalTransform().Scale.X).ToArray();
            Rect2 hero = room.CreatureNodes.First(c => c.Entity.IsPlayer).Hitbox.GetGlobalRect();
            _report.Check($"decimillipede_{phase}_one_ground_line_and_scale", bodies.Max(b => b.End.Y) - bodies.Min(b => b.End.Y) <= 4f
                && scales.Max() - scales.Min() <= 0.01f, $"scales={string.Join(", ", scales)} bodies={string.Join("; ", bodies)}");
            _report.Check($"decimillipede_{phase}_ordered_clear_chain", footprints.Zip(footprints.Skip(1), (a, b) => a.End.X <= b.Position.X).All(x => x)
                && footprints.All(b => b.Position.X > hero.End.X && PortraitStageFrame().Encloses(b)),
                $"hero={hero} footprints={string.Join("; ", footprints)}");
        }
        CheckChain("initial");
        await CaptureAsync("decimillipede_01_initial");

        var middle = segments[1];
        await CreatureCmd.Kill(middle.Entity, force: false);
        await WaitSecondsAsync(1f);
        _report.Check("decimillipede_dead_segment_retains_its_slot", middle.Entity.IsDead && room.CreatureNodes.Contains(middle),
            $"slot={middle.Entity.SlotName}");
        await CaptureAsync("decimillipede_02_dead_middle");
        for (int i = 0; i < 4 && middle.Entity.IsDead; i++)
        {
            await _pointer.TapAsync(CenterOf(room.Ui.EndTurnButton));
            await WaitUntilAsync(() => CombatManager.Instance.PlayerActionsDisabled, "Decimillipede enemy turn", ShortWait);
            await WaitUntilAsync(IsPlayerPlayPhase, "Decimillipede next turn", MediumWait);
            await WaitSecondsAsync(0.5);
        }
        _report.Check("decimillipede_native_reattach_preserves_slots", middle.Entity.IsAlive
            && segments.All(s => GodotObject.IsInstanceValid(s) && s.GetGlobalTransform().IsEqualApprox(authored[s])),
            $"middleAlive={middle.Entity.IsAlive}");
        CheckChain("reattached");
        await CaptureAsync("decimillipede_03_reattached");
    }
}
