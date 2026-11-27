using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// <c>rest-tour</c> (and the TouchSpire host's landscape-only <c>rest-art</c>): every act's native rest site, captured in
/// portrait (<c>rest_&lt;act&gt;</c>), or with the characters and run UI hidden for the art-only pass. Run on both hosts; the
/// portrait state comes from the host's own <see cref="IsPortraitHost"/>.
/// </summary>
internal sealed partial class TestDriver
{
    private async Task RunNativeRestArtAsync(RunState state)
    {
        bool artOnly = _options.Scenario == "rest-art";
        if (artOnly && IsPortraitHost())
            throw new InvalidOperationException("Native rest-art capture requires a landscape launch; never change the canvas during a run.");
        foreach (string act in ModelDb.Acts.Select(a => a.GetType().Name.ToLowerInvariant()))
        {
            await ConfigureTourActAsync(state, act);
            await RunManager.Instance.EnterRoomDebug(RoomType.RestSite, showTransition: false);
            await WaitUntilAsync(() => NRestSiteRoom.Instance is { } room && room.IsInsideTree(), "live native rest room", MediumWait);
            await WaitSecondsAsync(3);
            NRestSiteRoom room = NRestSiteRoom.Instance!;
            if (!artOnly)
            {
                await CaptureAsync("rest_" + act);
                continue;
            }
            foreach (Control child in room.GetChildren().OfType<Control>())
                if (child.Name != "BgContainer") child.Hide();
            Control bg = room.GetNode<Control>("BgContainer");
            foreach (Control character in bg.GetChildren().OfType<Control>().Where(c => c.Name.ToString().StartsWith("Character", StringComparison.Ordinal)))
                character.Hide();
            var ui = MegaCrit.Sts2.Core.Nodes.NRun.Instance!.GlobalUi;
            bool wasVisible = ui.Visible;
            ui.Hide();
            try
            {
                await WaitFramesAsync(8);
                await CaptureAsync("native_rest_" + act);
            }
            finally { ui.Visible = wasVisible; }
        }
    }
}
