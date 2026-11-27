using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// <c>header-inventory</c>: the run header, the potion row and the relic band. The fixture (real owned relics and potions)
/// and the stage-stability comparisons are host-neutral; the band geometry, the scrolling interaction and the presentational
/// modes are partial methods implemented by the host (Host/LauncherHeaderChecks.cs).
/// </summary>
internal sealed partial class TestDriver
{
    private async Task RunHeaderInventoryAsync(RunState state)
    {
        var room = await EnterCombatAsync();
        await WaitSecondsAsync(2);
        var player = state.Players[0];
        NRelicInventory inventory = NRun.Instance!.GlobalUi.RelicInventory;
        if (!IsPortraitHost())
        {
            _report.Check("header_landscape_keeps_native_inventory", inventory.GetParent().Name != "Extent",
                "relics are not inside a portrait clip");
            await CheckRelicHoldAndInspectAsync("header_landscape", inventory.RelicNodes.First());
            return;
        }
        var roots = room.CreatureNodes.ToDictionary(c => c.GetInstanceId(), c => c.GetGlobalTransform());
        Transform2D background = room.Background.GetGlobalTransform();
        Control[] controls = { room.Ui.DrawPile, room.Ui.EnergyCounterContainer, room.Ui.EndTurnButton, room.Ui.DiscardPile };
        Rect2[] rail = controls.Select(c => c.GetGlobalRect()).ToArray();
        // Real owned models/native holders and acquisition animation, without
        // running unrelated relic gameplay choices. Thirty is the normal
        // visual acceptance case; overflow remains reachable in every presentation.
        foreach (int count in new[] { 30, 41 })
        {
            foreach (RelicModel model in ModelDb.AllRelics.Where(r => r.GetType().Assembly == typeof(RelicModel).Assembly)
                         .OrderBy(r => r.Id.Entry).Where(r => !player.Relics.Any(owned => owned.Id == r.Id))
                         .Take(count - player.Relics.Count).ToList())
            {
                RelicModel relic = model.ToMutable();
                player.AddRelicInternal(relic);
                inventory.AnimateRelic(relic);
            }
            await WaitSecondsAsync(2);
            foreach (string presentation in RelicPresentations(count))
            {
                await CheckRelicPresentationAsync(inventory, count, presentation);
                _report.Check($"header_{count}_{presentation}_background_stable", room.Background.GetGlobalTransform() == background,
                    "count/mode/scroll/inspection do not reframe room");
                _report.Check($"header_{count}_{presentation}_actors_stable", room.CreatureNodes.All(c =>
                    roots.TryGetValue(c.GetInstanceId(), out Transform2D t) && t == c.GetGlobalTransform()), "actor transforms unchanged");
                _report.Check($"header_{count}_{presentation}_controls_stable",
                    controls.Select(c => c.GetGlobalRect()).SequenceEqual(rail), "control row unchanged");
            }
        }
        // Starting capacity depends on ascension; this fixture exercises exactly twelve slots.
        player.AddToMaxPotionCount(12 - player.MaxPotionCount);
        foreach (var model in ModelDb.AllPotions.Take(12)) player.AddPotionInternal(model.ToMutable());
        await WaitSecondsAsync(1);
        await CheckHeaderPotionRowAsync(player);
        _report.Check("header_expansion_keeps_background", room.Background.GetGlobalTransform() == background,
            "expanded potion capacity does not reframe room");
        _report.Check("header_expansion_keeps_actors", room.CreatureNodes.All(c =>
            roots.TryGetValue(c.GetInstanceId(), out Transform2D t) && t == c.GetGlobalTransform()), "actor transforms unchanged");
        _report.Check("header_expansion_keeps_controls", controls.Select(c => c.GetGlobalRect()).SequenceEqual(rail),
            "control row unchanged");
        await CaptureAsync("header_04_potions_scrolled");
        await TapPotionAsync(NRun.Instance.GlobalUi.TopBar.PotionContainer.Holders.Last(), "header_expanded_potion_actions");
        await CaptureAsync("header_05_actions_below_wrapped_relics");
    }

    /// <summary>
    /// The native hold inspection: a stationary finger shows the holder's own tip (inside the safe frame),
    /// the release opens the native relic inspector and a tap outside it closes that screen.
    /// </summary>
    private async Task CheckRelicHoldAndInspectAsync(string id, NRelicInventoryHolder holder, bool capture = false)
    {
        await _pointer.PressAsync(CenterOf(holder));
        await WaitSecondsAsync(0.3);
        NHoverTipSet? tip = FindNativeTip(holder);
        _report.Check(id + "_native_hold_tip", tip is not null, tip is not null
            ? "stationary touch uses native relic tip"
            : $"no tip owned by holder #{holder.GetInstanceId()} at {holder.GetGlobalRect()}; live tips: " +
              string.Join("; ", FindAll<NHoverTipSet>(_tree.Root)
                  .Where(set => !set.IsQueuedForDeletion() && set.IsVisibleInTree())
                  .Select(set => $"#{NativeTipOwner(set)?.GetInstanceId()} {NativeTipOwner(set)?.GetType().Name} " +
                                 $"{NativeTipOwner(set)?.GetGlobalRect()}")));
        if (tip is not null && IsPortraitHost())
        {
            Control[] parts = tip.GetChildren().OfType<Control>().Where(c => c.Visible && c.Size.X > 1f && c.Size.Y > 1f).ToArray();
            _report.Check(id + "_tip_safe", parts.Length > 0 && parts.All(c => PortraitSafeRect().Encloses(c.GetGlobalRect())),
                "all native tip parts fit safe frame");
            _report.Check(id + "_tip_near_owner", parts.Any(c => c.GetGlobalRect().Position.Y < holder.GetGlobalRect().End.Y + 240f),
                "scaling does not send tip down toward hand");
            if (capture) await CaptureAsync(id + "_hold_tip");
        }
        await _pointer.ReleaseAsync(CenterOf(holder));
        await WaitSecondsAsync(0.5);
        NInspectRelicScreen? inspector = FindAll<NInspectRelicScreen>(_tree.Root).FirstOrDefault(s => s.IsVisibleInTree());
        _report.Check(id + "_native_inspection", inspector is not null, "release opens native relic inspector");
        if (_pointer.Mode == PointerMode.Touch)
            _report.Check(id + "_hold_tip_cleared", FindNativeTip(holder) is null, "release dismisses the held tip");
        if (inspector is not null)
        {
            if (capture) await CaptureAsync(id + "_inspector");
            await _pointer.TapAsync(PortraitSafeRect().Position + new Vector2(80f, 120f));
            await WaitSecondsAsync(0.4);
            _report.Check(id + "_native_close", !inspector.IsVisibleInTree(), "outside tap closes native inspector");
        }
    }
}
