using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

internal sealed partial class TestDriver
{
    // Smoke tour, not an auto-win solver: enter every catalogued encounter and
    // event, inspect initial layout and one enemy turn, save a review screenshot.
    // Offset/limit make a failed/hung item reproducible without replaying a tour.
    private async Task RunTourAsync(RunState state)
    {
        string actName = _options.Scenario[5..];
        var (actIndex, act) = await ConfigureTourActAsync(state, actName);
        List<AbstractModel> models = new();
        if (_options.TourKind is "all" or "encounters")
        {
            models.AddRange(act.AllEncounters);
            if (actIndex == 0) models.AddRange(ModelDb.EventEncounters);
        }
        if (_options.TourKind is "all" or "events")
        {
            models.AddRange(act.AllEvents);
            models.AddRange(act.AllAncients);
            if (actIndex == 0)
            {
                models.AddRange(ModelDb.AllSharedEvents);
                models.AddRange(ModelDb.AllSharedAncients);
                // Event encounters are visited by the encounters tour below.
            }
        }
        models = models.DistinctBy(m => m.Id).OrderBy(m => m.GetType().Name).ToList();
        File.WriteAllLines(Path.Combine(_options.OutputDirectory, "CATALOG.txt"),
            models.Select((m, i) => $"{i:000} {m.Id} ({m.GetType().Name})"));
        _report.Pass("tour_catalog", $"act={actName} models={models.Count} kind={_options.TourKind}");
        if (_options.TourKind is "all" or "encounters")
        {
            // Keep late-act first-turn damage from aborting the layout tour.
            // This is fixture-only, never applied to a normal player's run.
            LocalContext.GetMe(state)!.Creature.SetMaxHpInternal(9999);
        }
        foreach (var (model, index) in models.Select((m, i) => (m, i)).Skip(_options.TourOffset).Take(_options.TourLimit))
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            string name = $"tour_{index:000}_{model.GetType().Name}";
            // A durable marker remains even if the game stalls inside EnterRoomDebug.
            File.WriteAllText(Path.Combine(_options.OutputDirectory, "CURRENT.txt"), $"{index} {model.Id}");
            try
            {
                var player = LocalContext.GetMe(state)!;
                player.Creature.SetCurrentHpInternal(player.Creature.MaxHp);
                AbstractModel roomModel = model is EncounterModel encounter ? encounter.ToMutable() : model;
                await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: roomModel, showTransition: false);
                if (model is EncounterModel)
                {
                    await WaitUntilAsync(IsPlayerPlayPhase, name + " player phase", TimeSpan.FromSeconds(45));
                    await WaitSecondsAsync(4.5);
                    NCombatRoom room = NCombatRoom.Instance!;
                    CheckTourCombat(name, room);
                    await CaptureAsync(name + "_initial");
                    Control bg = room.GetNode<Control>("%BgContainer");
                    Transform2D transform = bg.GetGlobalTransform();
                    var initialRoster = room.CreatureNodes.Where(c => !c.Entity.IsPet).ToDictionary(c => c, c => c.GetGlobalTransform());
                    await _pointer.TapAsync(CenterOf(room.Ui.EndTurnButton));
                    await WaitUntilAsync(() => CombatManager.Instance.PlayerActionsDisabled, name + " enemy turn", ShortWait);
                    // Some enemy moves stop the turn on a card choice (Knowledge Demon's Curse of Knowledge): check it as a
                    // combat choice screen and take its first card the way a player would, then wait for the turn again.
                    for (int choice = 0; ; choice++)
                    {
                        await WaitUntilAsync(() => !CombatManager.Instance.PlayerActionsDisabled || VisibleChoiceScreen() is not null,
                            name + " next turn", TimeSpan.FromSeconds(45));
                        if (VisibleChoiceScreen() is not { } screen) break;
                        if (choice >= 3) throw new InvalidOperationException($"{name}: more than three card choices in one enemy turn");
                        await WaitSecondsAsync(0.65);
                        var holders = FindAll<NGridCardHolder>(screen);
                        CheckCombatChoiceControls($"{name}_choice_{choice}", screen, holders);
                        await CaptureAsync($"{name}_choice_{choice}");
                        await TakeChoiceCardAsync(holders[0]);
                        await WaitUntilAsync(() => !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree(),
                            $"{name} choice {choice} closes", ShortWait);
                    }
                    await WaitSecondsAsync(2.5);
                    _report.Check(name + "_stable_backdrop", bg.GetGlobalTransform().IsEqualApprox(transform),
                        $"before={transform} after={bg.GetGlobalTransform()}");
                    CheckTourCombat(name + "_next", room);
                    if (IsPortraitHost())
                    {
                        var currentRoster = room.CreatureNodes.Where(c => !c.Entity.IsPet).ToHashSet();
                        if (currentRoster.Any(c => !initialRoster.ContainsKey(c)) || initialRoster.Keys.Any(c => !currentRoster.Contains(c)))
                            foreach (var (node, before) in initialRoster.Where(p => currentRoster.Contains(p.Key) && p.Key.Entity.IsAlive))
                                _report.Check(name + "_roster_survivor_stable", node.GetGlobalTransform().IsEqualApprox(before),
                                    $"{node.Entity.Monster?.Id} before={before} after={node.GetGlobalTransform()}");
                    }
                    await CaptureAsync(name + "_next");
                }
                else
                {
                    await WaitUntilAsync(() => NEventRoom.Instance is { } r && r.IsInsideTree()
                        && (r.Layout is not null || r.GetNodeOrNull<Control>("EventContainer")?.GetChildCount() > 0),
                        name + " event layout", MediumWait);
                    await WaitSecondsAsync(2.0);
                    NEventRoom room = NEventRoom.Instance!;
                    List<NEventOptionButton> options = FindAll<NEventOptionButton>(room).Where(o => o.IsVisibleInTree()).ToList();
                    _report.Check(name + "_options", room.Layout is null || options.Count > 0,
                        $"options={options.Count} customLayout={room.Layout is null}");
                    await CaptureAsync(name + "_initial");
                    await CheckTourEventOptionsAsync(name, room, options);
                }
                _report.Pass(name + "_visited", model.Id.ToString());
            }
            catch (Exception ex)
            {
                _report.Fail(name + "_exception", ex.ToString());
                await CaptureAsync(name + "_failed");
            }
            // Checkpoint without publishing RESULTS.json (runner completion signal).
            string progress = Path.Combine(_options.OutputDirectory, "progress");
            _report.Save(progress);
        }
        await RunManager.Instance.EnterRoomDebug(RoomType.RestSite, showTransition: false);
        await WaitSecondsAsync(1);
        await CaptureAsync("tour_rest_" + actName);
        await RunManager.Instance.EnterRoomDebug(RoomType.Shop, showTransition: false);
        await WaitSecondsAsync(1);
        await CaptureAsync("tour_shop_" + actName);
    }

    private async Task CheckTourEventOptionsAsync(string name, NEventRoom room, List<NEventOptionButton> options)
    {
        Rect2 safe = PortraitSafeRect();
        if (room.Layout?.GetNodeOrNull<ScrollContainer>(EventChoicesNodeName()) is not { } choices)
        {
            foreach (NEventOptionButton option in options)
                _report.Check(name + "_option_in_frame", safe.Encloses(option.GetGlobalRect()),
                    $"option={option.GetGlobalRect()} safe={safe}");
            return;
        }

        var layout = room.Layout!;
        if (layout.GetNodeOrNull<Control>(EventTextSlotNodeName()) is { } slot)
        {
            Rect2 art = layout.GetNodeOrNull<Control>(EventArtNodeName())?.GetGlobalRect() ?? default;
            var panel = slot.GetNode<Control>("Panel").GetGlobalRect();
            var copy = slot.GetNode<ScrollContainer>("Panel/Text");
            var description = layout.GetNode<RichTextLabel>("%EventDescription");
            Rect2 textFrame = copy.GetGlobalRect();
            bool scrolling = copy.GetVScrollBar().IsVisibleInTree();
            bool allInkFits = description.GetContentHeight() <= description.Size.Y + 1f;
            bool copyFits = textFrame.Grow(1f).Encloses(description.GetGlobalRect());
            _report.Check(name + "_copy_readable_over_art", art != default && art.Encloses(panel) && allInkFits && (copyFits || scrolling),
                $"panel={panel} text={description.GetGlobalRect()} ink={description.GetContentHeight()} font={description.GetThemeFontSize("normal_font_size")} scroll={scrolling}");
            if (scrolling)
            {
                string originalCopy = description.Text;
                var lastInk = description.GetGlobalRect().Position.Y + description.GetContentHeight();
                for (int i = 0; i < 8 && lastInk > textFrame.End.Y + 1f; i++)
                {
                    var start = new Vector2(textFrame.GetCenter().X, textFrame.End.Y - 72f);
                    await _pointer.DragAsync(start, start - new Vector2(0f, textFrame.Size.Y * 0.55f), 20);
                    await WaitFramesAsync(4);
                    if (NEventRoom.Instance != room || !GodotObject.IsInstanceValid(description)) break;
                    lastInk = description.GetGlobalRect().Position.Y + description.GetContentHeight();
                }
                bool stayedOnPage = NEventRoom.Instance == room && GodotObject.IsInstanceValid(description)
                    && description.Text == originalCopy && options.All(o => GodotObject.IsInstanceValid(o) && o.IsVisibleInTree());
                _report.Check(name + "_long_copy_reachable_by_drag", stayedOnPage && lastInk <= textFrame.End.Y + 1f,
                    $"offset={copy.ScrollVertical} lastInk={lastInk:F0} frameBottom={textFrame.End.Y:F0} stayed={stayedOnPage}");
                if (stayedOnPage) await CaptureAsync(name + "_text_scrolled");
            }
        }

        Rect2 clip = choices.GetGlobalRect().Intersection(safe).Grow(2f);
        bool FullyVisible(NEventOptionButton option) => GodotObject.IsInstanceValid(option)
            && option.IsVisibleInTree() && clip.Encloses(option.GetGlobalRect());
        var remaining = options.Where(option => !FullyVisible(option)).ToHashSet();
        VScrollBar bar = choices.GetVScrollBar();
        bool compact = GetViewportSize().Y / GetViewportSize().X <= 16f / 9f + 0.001f;
        bool fourChoices = name.EndsWith("_WelcomeToWongos", StringComparison.Ordinal);
        if (fourChoices)
        {
            float spare = choices.GetGlobalRect().End.Y - options.Max(o => o.GetGlobalRect().End.Y);
            _report.Check(name + "_four_choices_fill_footer", options.Count == 4 && remaining.Count == 0 && spare is >= 0f and <= 64f,
                $"choices={options.Count} fullyVisible={options.Count - remaining.Count} bottomSlack={spare:F1}");
        }
        if (remaining.Count == 0)
        {
            _report.Check(name + "_options_fit_initially", !bar.IsVisibleInTree() && choices.ScrollVertical == 0,
                $"options={options.Count} clip={clip} scrollbar={bar.IsVisibleInTree()} offset={choices.ScrollVertical}");
            return;
        }

        if (compact && name.Split('_').Last() is ("BattlewornDummy" or "DollRoom" or "Amalgamator" or "ThermaeNonupeipe"))
            _report.Fail(name + "_options_should_fit_initially", $"missing={remaining.Count}/{options.Count} range={bar.MaxValue - bar.Page:F0}");
        _report.Check(name + "_options_scroll_affordance", bar.IsVisibleInTree() && bar.MaxValue > bar.Page
            && (!compact || bar.GetGlobalRect().Size.X >= 36f), $"bar={bar.GetGlobalRect()} range={bar.MaxValue - bar.Page:F0}");
        var initial = options.Where(o => o.IsEnabled).ToArray();
        bool stayed = true;
        for (int i = 0; i < 10 && remaining.Count > 0; i++)
        {
            float travel = clip.Size.Y * 0.40f;
            Vector2 start = new(clip.GetCenter().X, clip.End.Y - 90f);
            int before = choices.ScrollVertical;
            await _pointer.DragAsync(start, start - new Vector2(0f, travel), 20);
            await WaitFramesAsync(4);
            stayed = NEventRoom.Instance == room && initial.All(o => GodotObject.IsInstanceValid(o) && o.IsEnabled && o.IsVisibleInTree());
            if (!stayed || !GodotObject.IsInstanceValid(choices) || choices.ScrollVertical <= before) break;
            remaining.RemoveWhere(FullyVisible);
        }
        _report.Check(name + "_scroll_reveals_all_options_without_selecting", stayed && remaining.Count == 0,
            $"remaining={remaining.Count} stayedInEvent={stayed}");
        if (stayed) await CaptureAsync(name + "_options_scrolled");
    }

    private void CheckTourCombat(string name, NCombatRoom room)
    {
        Rect2 safe = PortraitSafeRect();
        List<NCreature> enemies = room.CreatureNodes.Where(c => c.Entity.IsMonster && c.Entity.IsAlive).ToList();
        _report.Check(name + "_enemies", enemies.Count > 0, $"enemies={enemies.Count}");
        foreach (NCreature enemy in enemies)
            _report.Check(name + "_target_visible", safe.Encloses(enemy.Hitbox.GetGlobalRect()),
                $"{enemy.Entity.Monster?.Id} hitbox={enemy.Hitbox.GetGlobalRect()} safe={safe}");
    }

    private NChooseACardSelectionScreen? VisibleChoiceScreen() =>
        FindAll<NChooseACardSelectionScreen>(_tree.Root).FirstOrDefault(s => s.IsVisibleInTree());
}
