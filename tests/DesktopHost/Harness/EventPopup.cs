using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

internal sealed partial class TestDriver
{
    /// <summary>
    /// An exact native Calling Bell tip over a full-width ancient option (fixture, not a reward selection). The tip's own
    /// placement and scale are the host's: TouchSpireUI's in HostLayoutChecks.cs, the launcher's in LauncherEventChecks.cs.
    /// </summary>
    private async Task RunEventPopupAsync(RunState state)
    {
        await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: ModelDb.AncientEvent<Tezcatara>(), showTransition: false);
        await WaitUntilAsync(() => NEventRoom.Instance?.Layout?.OptionButtons.Any() == true, "ancient options for popup fixture", ShortWait);
        await WaitSecondsAsync(1);
        var option = NEventRoom.Instance!.Layout!.OptionButtons.First();
        // Exact native Calling Bell text/card renderer against a real full-width
        // event option. Direct preview fixture, not a gameplay reward selection.
        var bell = ModelDb.Relic<CallingBell>().ToMutable();
        state.Players[0].AddRelicInternal(bell);
        NHoverTipSet.Clear();
        NHoverTipSet tips = NHoverTipSet.CreateAndShow(option, bell.HoverTips, HoverTipAlignment.Left)!;
        await WaitSecondsAsync(0.5);
        Control[] parts = tips.GetChildren().OfType<Control>().Where(c => c.Size.X > 1f && c.Size.Y > 1f).ToArray();
        _report.Check("event_popup_native_parts", parts.Length >= 2, $"text/card parts={parts.Length}");
        foreach (var (part, i) in parts.Select((p, i) => (p, i)))
            _report.Check($"event_popup_{i}_safe", PortraitSafeRect().Encloses(part.GetGlobalRect()), $"rect={part.GetGlobalRect()}");
        _report.Check("event_popup_phone_scale", tips.Scale.X >= HoverTipMinScale(), $"scale={tips.Scale}");
        await CaptureAsync("event_popup_01_calling_bell");
    }

    /// <summary>The smallest tip scale the host's portrait tip placement uses.</summary>
    private partial float HoverTipMinScale();
}
