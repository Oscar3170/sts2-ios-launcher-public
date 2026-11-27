using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// <c>potions</c>: the native potion actions and the scrollable potion row. The flow is the game's own (model, holder and
/// popup); the popup's placement inside the host's portrait band and the row node are partial methods (the launcher's in
/// LauncherHeaderChecks.cs), and <see cref="CheckPotionPopupLayout"/> comes from HarnessCore.cs.
/// </summary>
internal sealed partial class TestDriver
{
    private async Task RunPotionPopupAsync(RunState state)
    {
        Player player = state.Players[0];
        SaveManager.Instance.MarkFtueAsComplete("obtain_potion_ftue");
        await RunManager.Instance.EnterRoomDebug(RoomType.RestSite, showTransition: false);
        await WaitUntilAsync(() => NRestSiteRoom.Instance?.IsInsideTree() == true, "rest site", MediumWait);
        await WaitSecondsAsync(0.5);

        var ui = NRun.Instance!.GlobalUi;
        var belt = ui.TopBar.PotionContainer.Holders;
        bool portrait = IsPortraitHost();
        await _pointer.TapAsync(CenterOf(belt[0]));
        _report.Check("potion_empty_slot_has_no_actions", player.Potions.Count() == 0 && LivePotionPopup() is null,
            "tapped a native empty slot");

        var attack = ModelDb.Potion<AttackPotion>().ToMutable();
        player.AddPotionInternal(attack);
        NPotionPopup rest = await TapPotionAsync(belt[0], "potion_rest_combat_only");
        _report.Check("potion_rest_respects_combat_only", !rest.IsUsable && Action(rest, "Discard").IsEnabled,
            "combat-only use disabled; discard enabled");
        await CaptureAsync("potion_rest_actions");
        Vector2 outside = _tree.Root.GetVisibleRect().GetCenter() with { Y = PortraitSafeRect().Position.Y + 60f };
        await _pointer.TapAsync(outside);
        await WaitUntilAsync(() => LivePotionPopup() is null, "outside tap dismisses the popup", ShortWait);
        _report.Check("potion_outside_tap_dismisses", player.Potions.Contains(attack), "outside tap did not spend the potion");
        rest = await TapPotionAsync(belt[0], "potion_rest_reopen");
        await _pointer.TapAsync(CenterOf(Action(rest, "Discard")));
        await WaitUntilAsync(() => !player.Potions.Contains(attack), "native discard action", ShortWait);
        _report.Check("potion_rest_discard_removes_model", !belt[0].HasPotion, "both the model and native holder were cleared");

        var blood = ModelDb.Potion<BloodPotion>().ToMutable();
        await WaitSecondsAsync(0.55); // allow the previous holder's native discard animation to finish
        player.AddPotionInternal(blood);
        player.Creature.SetCurrentHpInternal(player.Creature.MaxHp - 25);
        int hp = player.Creature.CurrentHp;
        rest = await TapPotionAsync(belt[0], "potion_rest_drink");
        _report.Check("potion_rest_any_time_enabled", rest.IsUsable, "native Drink is enabled for Blood Potion");
        await _pointer.TapAsync(CenterOf(Action(rest, "Use")));
        await WaitUntilAsync(() => !player.Potions.Contains(blood) && player.Creature.CurrentHp > hp, "Blood Potion heals at rest", ShortWait);
        _report.Pass("potion_rest_drink_heals", $"HP {hp} -> {player.Creature.CurrentHp}");
        var block = ModelDb.Potion<BlockPotion>().ToMutable();
        player.AddPotionInternal(block);
        var room = await EnterCombatAsync();
        Transform2D background = room.Background.GetGlobalTransform();
        if (portrait)
        {
            player.AddToMaxPotionCount(5 - player.MaxPotionCount);
            for (int i = 0; i < 4; i++) player.AddPotionInternal(ModelDb.Potion<BloodPotion>().ToMutable());
            await WaitSecondsAsync(0.4);
            _report.Check("potion_full_five_slot_belt", player.Potions.Count() == 5 && belt.Count == 5, $"capacity={belt.Count}");
        }
        var popup = await TapPotionAsync(belt[0], "potion_combat_drink");
        int beforeBlock = player.Creature.Block;
        await CaptureAsync("potion_combat_actions");
        await _pointer.TapAsync(CenterOf(Action(popup, "Use")));
        await WaitUntilAsync(() => !player.Potions.Contains(block) && player.Creature.Block > beforeBlock, "Block Potion grants block", ShortWait);
        _report.Pass("potion_combat_drink_grants_block", $"block {beforeBlock} -> {player.Creature.Block}");
        if (!portrait) return;

        ScrollContainer row = PotionRow() ?? throw new System.InvalidOperationException("The portrait potion row is missing.");
        player.AddToMaxPotionCount(4);
        var fire = ModelDb.Potion<FirePotion>().ToMutable();
        player.AddPotionInternal(fire, 8);
        foreach (int slot in Enumerable.Range(0, 8).Where(i => player.PotionSlots[i] is null))
            player.AddPotionInternal(ModelDb.Potion<BloodPotion>().ToMutable(), slot);
        await WaitSecondsAsync(0.5);
        NPotionHolder last = belt.Last();
        Rect2 clip = row.GetGlobalRect();
        Vector2 right = clip.Position + new Vector2(clip.Size.X - 96f, 110f);
        Vector2 left = clip.Position + new Vector2(64f, 110f);
        for (int i = 0; i < 4 && !row.GetGlobalRect().Grow(2f).Encloses(last.GetGlobalRect()); i++)
            await _pointer.DragAsync(right, left, 16);
        _report.Check("potion_swipe_reaches_last_slot", row.ScrollHorizontal > 0 && row.GetGlobalRect().Grow(2f).Encloses(last.GetGlobalRect()),
            $"capacity={belt.Count} offset={row.ScrollHorizontal} finalSlot={last.GetGlobalRect()}");
        _report.Check("potion_swipe_does_not_activate", LivePotionPopup() is null && player.Potions.Count() == 9,
            "dragging the row neither opens actions nor spends a potion");
        _report.Check("potion_capacity_keeps_stage_framing", room.Background.GetGlobalTransform().IsEqualApprox(background), "combat stage unchanged");

        popup = await TapPotionAsync(last, "potion_scrolled_last_slot");
        await CaptureAsync("potion_scrolled_last_slot");
        int scrollAtEnd = row.ScrollHorizontal;
        await _pointer.DragAsync(left, right, 16);
        _report.Check("potion_scroll_dismisses_open_popup", row.ScrollHorizontal < scrollAtEnd && popup.IsMarkedForRemoval
            && LivePotionPopup() is null && player.Potions.Count() == 9, "swiping after opening dismisses without activating");
        await _pointer.DragAsync(right, left, 16);
        popup = await TapPotionAsync(last, "potion_immediate_tap_after_swipe");
        await _pointer.TapAsync(CenterOf(Action(popup, "Use")));
        await WaitUntilAsync(() => NTargetManager.Instance.IsInSelection && !GodotObject.IsInstanceValid(popup),
            "Fire Potion targeting ready and action panel gone", ShortWait);
        var enemy = room.CreatureNodes.First(c => c.Entity.IsEnemy && c.Entity.IsAlive);
        int enemyHp = enemy.Entity.CurrentHp;
        await _pointer.TapAsync(CenterOf(enemy.Hitbox));
        await WaitUntilAsync(() => !player.Potions.Contains(fire) && enemy.Entity.CurrentHp < enemyHp, "Fire Potion hits selected enemy", ShortWait);
        _report.Pass("potion_throw_hits_target", $"enemy HP {enemyHp} -> {enemy.Entity.CurrentHp}");
    }

    private static readonly AccessTools.FieldRef<NHoverTipSet, Control> NativeTipOwner =
        AccessTools.FieldRefAccess<NHoverTipSet, Control>("_owner");

    private NHoverTipSet? FindNativeTip(Control owner) => FindAll<NHoverTipSet>(_tree.Root)
        .FirstOrDefault(t => !t.IsQueuedForDeletion() && t.IsVisibleInTree() && NativeTipOwner(t) == owner);
}
