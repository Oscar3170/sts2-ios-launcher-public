using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

internal sealed partial class TestDriver
{
    private async Task RunEnemyOrderAsync(RunState state)
    {
        await ConfigureTourActAsync(state, "Underdocks");
        await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: ModelDb.Encounter<ToadpolesWeak>().ToMutable(), showTransition: false);
        await WaitUntilAsync(IsPlayerPlayPhase, "Toadpoles player phase", TimeSpan.FromSeconds(45));
        await WaitSecondsAsync(2.5);
        var room = NCombatRoom.Instance!;
        var front = room.CreatureNodes.Single(c => c.Entity.Monster is Toadpole { IsFront: true });
        var rear = room.CreatureNodes.Single(c => c.Entity.Monster is Toadpole { IsFront: false });
        _report.Check("toadpoles_keep_native_left_to_right_order", front.GlobalPosition.X < rear.GlobalPosition.X,
            $"front={front.GlobalPosition} rear={rear.GlobalPosition}");
        _report.Check("toadpole_hitboxes_follow_art", front.Hitbox.GetGlobalRect().GetCenter().X < rear.Hitbox.GetGlobalRect().GetCenter().X,
            $"front={front.Hitbox.GetGlobalRect()} rear={rear.Hitbox.GetGlobalRect()}");
        var frontMonster = (Toadpole)front.Entity.Monster!;
        var rearMonster = (Toadpole)rear.Entity.Monster!;
        _report.Check("toadpoles_keep_native_opening_intents", frontMonster.NextMove.Intents.Any(i => i is BuffIntent)
            && rearMonster.IntendsToAttack, "buff is on the left and attack is on the right");
        await CaptureAsync("enemy_order_01_toadpoles");
        if (IsPortraitHost())
        {
            var strike = room.Ui.Hand.ActiveHolders.First(IsTargetedPlayableCard);
            int leftHp = front.Entity.CurrentHp;
            int rightHp = rear.Entity.CurrentHp;
            await _pointer.DragAsync(CenterOf(strike.Hitbox), CenterOf(rear.Hitbox), steps: 16);
            await WaitUntilAsync(() => rear.Entity.CurrentHp < rightHp, "touch damage to the right-hand Toadpole", ShortWait);
            _report.Check("toadpoles_drag_target_keeps_creature_identity", front.Entity.CurrentHp == leftHp,
                $"HP left {leftHp}→{front.Entity.CurrentHp}; right {rightHp}→{rear.Entity.CurrentHp}");
        }
        await _pointer.TapAsync(CenterOf(room.Ui.EndTurnButton));
        await WaitUntilAsync(() => CombatManager.Instance.PlayerActionsDisabled, "Toadpoles enemy turn", ShortWait);
        await WaitUntilAsync(IsPlayerPlayPhase, "Toadpoles next player turn", MediumWait);
        _report.Check("toadpole_turn_two_intents_follow_same_roots", frontMonster.IntendsToAttack
            && rearMonster.NextMove.Intents.Any(i => i is BuffIntent) && front.GlobalPosition.X < rear.GlobalPosition.X,
            "attack moved to the left and buff to the right without moving the creatures");
        var transform = rear.GetGlobalTransform();
        var background = room.Background.GetGlobalTransform();
        await CreatureCmd.Kill(front.Entity, force: true);
        await WaitSecondsAsync(1.6);
        _report.Check("toadpole_survivor_stays_in_place", rear.GetGlobalTransform().IsEqualApprox(transform), "rear retained its position and scale after the front died");
        _report.Check("toadpole_background_stays_in_place", room.Background.GetGlobalTransform().IsEqualApprox(background), "background unchanged");
        await CaptureAsync("enemy_order_toadpoles_front_killed");
        await CaptureAsync("enemy_order_02_survivor");

        if (!IsPortraitHost()) return;
        await CreatureCmd.Add(ModelDb.Monster<Toadpole>().ToMutable(), rear.Entity.CombatState!);
        await WaitSecondsAsync(0.7);
        _report.Check("toadpole_summon_reuses_left_slot", room.CreatureNodes.Any(c => c != rear && c.Entity.IsMonster && c.Entity.IsAlive
            && c.GlobalPosition.X < rear.GlobalPosition.X) && rear.GetGlobalTransform().IsEqualApprox(transform),
            "new creature took the vacated left slot without moving the survivor");
        await CaptureAsync("enemy_order_toadpoles_after_summon");
        string[] named = { "MytesNormal", "BowlbugsWeak", "NibbitsNormal", "FogmogNormal", "TheObscuraNormal", "BowlbugsNormal" };
        foreach (string name in named)
        {
            var act = ModelDb.Acts.First(a => a.GetType().Name is "Overgrowth" or "Underdocks" or "Hive" or "Glory"
                && a.AllEncounters.Any(e => e.GetType().Name == name));
            await ConfigureTourActAsync(state, act.GetType().Name);
            var encounter = state.Act.AllEncounters.Single(e => e.GetType().Name == name).ToMutable();
            await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: encounter, showTransition: false);
            await WaitUntilAsync(IsPlayerPlayPhase, name + " player phase", MediumWait);
            await WaitSecondsAsync(0.4);
            await CaptureAsync("enemy_order_" + name + "_initial");
            var current = NCombatRoom.Instance!;
            if (name is "FogmogNormal" or "TheObscuraNormal")
            {
                var summoner = current.CreatureNodes.Single(c => c.Entity.IsMonster);
                var original = summoner.GetGlobalTransform();
                await WaitSecondsAsync(0.5);
                await _pointer.TapAsync(CenterOf(current.Ui.EndTurnButton));
                await WaitUntilAsync(() => current.CreatureNodes.Any(c => c.Entity.SlotName == "illusion") && IsPlayerPlayPhase(),
                    name + " native summon and next player phase", MediumWait);
                _report.Check(name + "_summoner_held_its_slot", summoner.GetGlobalTransform().IsEqualApprox(original), "native summon did not move the original unit");
                await CaptureAsync("enemy_order_" + name + "_after_summon");
            }
            var slots = (Control)AccessTools.Property(typeof(NCombatRoom), "EncounterSlots").GetValue(current)!;
            var enemies = current.CreatureNodes.Where(c => c.Entity.IsMonster && !c.Entity.IsPet)
                .OrderBy(c => slots.GetNode<Marker2D>(c.Entity.SlotName!).Position.X).ToArray();
            _report.Check(name + "_authored_slot_order", enemies.Length >= 2
                && enemies.Zip(enemies.Skip(1), (left, right) => left.Hitbox.GetGlobalRect().GetCenter().X < right.Hitbox.GetGlobalRect().GetCenter().X).All(ok => ok),
                string.Join(" | ", enemies.Select(c => $"{c.Entity.SlotName}={c.GlobalPosition}")));
        }
    }

    /// <summary>A hand card that may be dragged onto a single enemy.</summary>
    private static bool IsTargetedPlayableCard(NHandCardHolder holder)
    {
        CardModel? card = holder.CardNode?.Model;
        return card is not null && card.CanPlay() && card.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly;
    }
}
