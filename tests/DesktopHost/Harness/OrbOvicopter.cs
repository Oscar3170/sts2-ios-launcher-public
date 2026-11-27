using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

internal sealed partial class TestDriver
{
    // The channel animation's tween on an orb node; the old 0.8 s settles were there to let it end.
    private static readonly System.Reflection.FieldInfo? OrbCurTween = AccessTools.Field(typeof(NOrb), "_curTween");

    private async Task RunOvicopterOrbsAsync(RunState state)
    {
        var player = state.Players[0];
        _report.Check("ovi_orbs_defect", player.Character is Defect, player.Character.GetType().Name);
        await ConfigureTourActAsync(state, "Hive");
        var encounter = state.Act.AllEncounters.First(e => e.GetType().Name == "OvicopterNormal").ToMutable();
        await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: encounter, showTransition: false);
        await WaitUntilAsync(IsPlayerPlayPhase, "Hive Ovicopter player phase", TimeSpan.FromSeconds(45));
        // The old 4.5 s settle covered the room/hand/intent entry and the self-freeing turn banner; wait for that state.
        await WaitUntilStableAsync(OrbIntroSignature, "orbs intro", TimeSpan.FromSeconds(4.5));
        var room = NCombatRoom.Instance!;
        var initial = room.CreatureNodes.ToDictionary(c => c.GetInstanceId(), c => c.GetGlobalTransform());
        Transform2D background = room.Background.GetGlobalTransform();
        Rect2[] rail = new Control[] { room.Ui.DrawPile, room.Ui.EnergyCounterContainer, room.Ui.EndTurnButton, room.Ui.DiscardPile }
            .Select(c => c.GetGlobalRect()).ToArray();
        var context = new ThrowingPlayerChoiceContext();
        await OrbCmd.Channel<FrostOrb>(context, player);
        await OrbCmd.Channel<DarkOrb>(context, player);
        await WaitUntilStableAsync(() => OrbSettledSignature(3), "orbs initial three", TimeSpan.FromSeconds(0.8));
        CheckOrbBounds("ovi_initial_three", 3);
        await CaptureAsync("ovi_orbs_01_initial_three");

        // Actual End Turn and native LAY_EGGS_MOVE, not debug-added substitutes.
        await EndOvicopterTurnAsync(room, "lay eggs");
        _report.Check("ovi_orbs_native_eggs", room.CreatureNodes.Count(c => c.Entity.Monster is ToughEgg) == 3,
            string.Join(",", room.CreatureNodes.Select(c => c.Entity.Monster?.GetType().Name)));
        CheckOrbBounds("ovi_eggs_three", 3);
        CheckOrbEncounterClearance("ovi_eggs_three", room);
        await CaptureAsync("ovi_orbs_02_eggs_three");
        await OrbCmd.AddSlots(player, 2);
        await OrbCmd.Channel<LightningOrb>(context, player);
        await OrbCmd.Channel<FrostOrb>(context, player);
        await WaitUntilStableAsync(() => OrbSettledSignature(5), "orbs five", TimeSpan.FromSeconds(0.8));
        CheckOrbBounds("ovi_eggs_five", 5);
        CheckOrbEncounterClearance("ovi_eggs_five", room);
        await CaptureAsync("ovi_orbs_03_eggs_five");
        await OrbCmd.AddSlots(player, 5);
        await OrbCmd.Channel<DarkOrb>(context, player);
        await OrbCmd.Channel<PlasmaOrb>(context, player);
        await OrbCmd.Channel<GlassOrb>(context, player);
        await OrbCmd.Channel<FrostOrb>(context, player);
        await OrbCmd.Channel<LightningOrb>(context, player);
        await WaitUntilStableAsync(() => OrbSettledSignature(10), "orbs ten", TimeSpan.FromSeconds(0.8));
        CheckOrbBounds("ovi_eggs_ten", 10);
        CheckOrbEncounterClearance("ovi_eggs_ten", room);
        await CaptureAsync("ovi_orbs_04_eggs_ten");

        var spawned = room.CreatureNodes.ToDictionary(c => c.GetInstanceId(), c => c.GetGlobalTransform());
        await EndOvicopterTurnAsync(room, "hatch minions");
        _report.Check("ovi_orbs_native_hatched_minions", room.CreatureNodes.Count(c => c.Entity.Monster is ToughEgg { IsHatched: true }) == 3,
            string.Join(",", room.CreatureNodes.Where(c => c.Entity.Monster is ToughEgg).Select(c => $"{c.Entity.SlotName}:{((ToughEgg)c.Entity.Monster!).IsHatched}")));
        CheckOrbBounds("ovi_hatched_ten", 10);
        CheckOrbEncounterClearance("ovi_hatched_ten", room);
        foreach (var node in room.CreatureNodes)
            if (spawned.TryGetValue(node.GetInstanceId(), out Transform2D transform))
                _report.Check("ovi_orbs_hatching_root_stable_" + node.GetInstanceId(), node.GetGlobalTransform().IsEqualApprox(transform), node.Entity.Monster?.GetType().Name ?? "player");
        _report.Check("ovi_orbs_initial_roots_stable", room.CreatureNodes.Where(c => initial.ContainsKey(c.GetInstanceId())).All(c => c.GetGlobalTransform().IsEqualApprox(initial[c.GetInstanceId()])), "native spawn/hatch/slot growth retain original roots");
        _report.Check("ovi_orbs_background_stable", room.Background.GetGlobalTransform().IsEqualApprox(background), "spawn, hatch, three-to-ten orbs preserve room framing");
        _report.Check("ovi_orbs_controls_stable", new Control[] { room.Ui.DrawPile, room.Ui.EnergyCounterContainer, room.Ui.EndTurnButton, room.Ui.DiscardPile }
            .Select(c => c.GetGlobalRect()).Zip(rail, (a, b) => a.Position.DistanceTo(b.Position) < 1f && a.Size.DistanceTo(b.Size) < 1f).All(v => v), $"before={string.Join(";", rail)} after={string.Join(";", new Control[] { room.Ui.DrawPile, room.Ui.EnergyCounterContainer, room.Ui.EndTurnButton, room.Ui.DiscardPile }.Select(c => c.GetGlobalRect()))}");
        await CaptureAsync("ovi_orbs_05_hatched_ten");
    }

    private async Task EndOvicopterTurnAsync(NCombatRoom room, string reason)
    {
        await _pointer.TapAsync(CenterOf(room.Ui.EndTurnButton));
        await WaitUntilAsync(() => CombatManager.Instance.PlayerActionsDisabled, reason + " enemy turn", ShortWait);
        await WaitUntilAsync(IsPlayerPlayPhase, reason + " next player turn", TimeSpan.FromSeconds(45));
        // The old 3 s settle covered the native egg spawn/hatch and intent animations of the returning player turn.
        await WaitUntilStableAsync(() => OrbSettledSignature(), reason + " turn settle", TimeSpan.FromSeconds(3));
        await _pointer.MoveAsync(PointerParkingPosition(), pressed: false);
        // Let the pointer leave the cards so hover effects do not cover the capture, and let any hand reflow settle.
        await WaitUntilStableAsync(CombatSettledSignature, reason + " pointer parked", TimeSpan.FromSeconds(1.2));
    }

    /// <summary>Pending until the room, hand and entry presentation (including the self-freeing banners) are settled.</summary>
    private string? OrbIntroSignature()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        if (room is null || !GodotObject.IsInstanceValid(room) || !room.IsInsideTree()) return null;
        if (room.GetChildren().Any(child => child is NCombatStartBanner or NPlayerTurnBanner or NEnemyTurnBanner))
            return Pending("room.banner");
        if (!IsPlayerPlayPhase()) return Pending("room.playPhase");
        if (room.Ui?.Hand is not { } hand || hand.ActiveHolders.Count == 0) return Pending("room.hand");
        return OrbSettledSignature();
    }

    /// <summary>Combat plus orb-node placement and the native channel tween; stable means the orbs have arrived.</summary>
    private string? OrbSettledSignature(int? expectedCount = null)
    {
        NCombatRoom? room = NCombatRoom.Instance;
        // The old 3 s turn settle always outlived the turn/combat-start banners; keep captures banner-free.
        if (room is null) return null;
        if (room.GetChildren().Any(child => child is NCombatStartBanner or NPlayerTurnBanner or NEnemyTurnBanner))
            return Pending("room.banner");
        string? combat = CombatSettledSignature();
        if (combat is null || IsPending(combat)) return combat;
        NOrb[] orbs = FindAll<NOrb>(_tree.Root)
            .Where(o => GodotObject.IsInstanceValid(o) && !o.IsQueuedForDeletion()).ToArray();
        if (expectedCount is int expected && orbs.Length != expected)
            return Pending($"orb.count={orbs.Length}/{expected}");
        var builder = new StringBuilder(combat);
        foreach (NOrb orb in orbs)
        {
            string id = orb.GetInstanceId().ToString(System.Globalization.CultureInfo.InvariantCulture);
            // End-state condition: the 0.5 s channel tween scales the orb sprite and can plateau in value.
            if (TweenRunning(orb, OrbCurTween)) return Pending($"orb.{id}.channelTween");
            builder.Append("orb.").Append(id).Append(".rect=").Append(Q(orb.GetGlobalRect())).Append('|');
        }
        return builder.ToString();
    }

}
