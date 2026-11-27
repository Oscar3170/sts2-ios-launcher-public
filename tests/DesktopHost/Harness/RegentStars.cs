using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

internal sealed partial class TestDriver
{
    /// <summary>
    /// Checks that the Regent's native star counter is visible on the combat rail and follows the native star signal. The
    /// host places the rail (TouchSpireUI on the TouchSpire host, PortraitCombatMath on the launcher), so only the shared
    /// behaviour lives here and the rail assertion is a host partial.
    /// </summary>
    private async Task RunRegentStarsAsync(RunState state)
    {
        var room = await EnterCombatAsync();
        var player = state.Players[0];
        var counter = FindAll<NStarCounter>(room.Ui).Single();
        var label = counter.GetNode<RichTextLabel>("%CountLabel");
        int initial = player.PlayerCombatState!.Stars;
        await WaitUntilAsync(() => label.Text.Contains($"]{initial}["), "Regent native starting star count", ShortWait);
        _report.Check("regent_starting_stars_visible", counter.IsVisibleInTree() && initial > 0,
            $"stars={initial} native label={label.Text}");
        CheckRegentStarRail("standard", room, counter, initial);
        // The launcher reads one frame for the star/energy ink and saves the draw-pile + star + energy detail crop. It
        // measures against the host's own rail; the TouchSpire host keeps the native placement untouched.
        await CheckRegentStarDetailAsync(room, counter);
        await CaptureAsync("regent_star_counter");
        // Exercise the native signal and counter animation without depending on the opening hand.
        await PlayerCmd.LoseStars(1m, player);
        await WaitUntilAsync(() => label.Text.Contains($"]{initial - 1}["), "updated star counter", ShortWait);
        _report.Pass("regent_star_counter_updates");
        // 0 -> 3 -> 10: the badge, the orb and the centred digit must not move, and the two-digit count stays centred.
        await CheckRegentStarCountsAsync(state, room, counter);
    }
}
