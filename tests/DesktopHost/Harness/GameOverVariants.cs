using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The end screens' two missing variants, sharing <see cref="RunGameOverScreenAsync"/> with the recorded-fight scenarios:
/// <c>game-over-room</c> dies outside combat, where the native screen has no tableau and the launcher's act-art fallback has
/// to stand in for the room, and <c>game-over-boss-win</c> wins the act boss fight, so the victory banner and summary sit
/// over a boss instead of the defeat tableau <c>game-over-boss</c> shows. The layout answers are host partials: the
/// launcher's fallback art and frame are checked in LauncherMenuChecks.cs.
/// </summary>
internal sealed partial class TestDriver
{
    /// <summary>
    /// A death in the rest site: a room the native game over knows (it makes its own dying visuals at the seated players'
    /// places) and where there is no <see cref="NCombatRoom"/> behind the screen, which is what the launcher's act-art
    /// fallback is for. The run is killed through the same native path a combat defeat uses, with no fight to lose.
    /// </summary>
    private async Task RunGameOverRoomAsync(RunState state)
    {
        await RunManager.Instance.EnterRoomDebug(RoomType.RestSite, showTransition: false);
        // The native rest-site branch of the game over places one visuals node per player at that player's seat, so wait
        // for the room's characters before the kill.
        await WaitUntilAsync(
            () => NRestSiteRoom.Instance is { } room
                  && GodotObject.IsInstanceValid(room)
                  && room.IsInsideTree()
                  && room.GetCharacterForPlayer(state.Players[0]) is not null,
            "rest site room", MediumWait);
        await WaitFramesAsync(6);
        _report.Check("game_over_room_loaded", NRestSiteRoom.Instance is { } loaded && loaded.IsInsideTree(),
            "rest site room is present");
        _report.Check("game_over_room_outside_combat",
            NCombatRoom.Instance is null || !GodotObject.IsInstanceValid(NCombatRoom.Instance),
            $"combat room={(NCombatRoom.Instance is null ? "none" : "present")}");
        await CreatureCmd.Kill(state.Players[0].Creature, force: true);
        await RunGameOverScreenAsync(win: false, fight: null,
            Array.Empty<(Node2D Actor, float Y)>(), "game_over_room");
    }

    /// <summary>
    /// A real boss victory: the act boss fight runs, then the native end-of-run path the game itself uses for a won run
    /// (<see cref="RunManager.WinRun"/>, as TheArchitect's PROCEED does) marks the run won, kills the players and opens the
    /// native game over over the boss tableau. <c>game-over-boss</c> is the same room lost.
    /// </summary>
    private async Task RunGameOverBossWinAsync()
    {
        await RunManager.Instance.EnterRoomDebug(RoomType.Boss,
            model: ModelDb.Encounter<KaiserCrabBoss>().ToMutable(), showTransition: false);
        await WaitUntilAsync(IsPlayerPlayPhase, "recorded boss combat encounter", MediumWait);
        var fight = NCombatRoom.Instance!;
        _report.Check("game_over_boss_win_loaded", fight.IsInsideTree(),
            $"boss room with {fight.CreatureNodes.Count()} creatures");
        (Node2D Actor, float Y)[] actors = fight.CreatureNodes
            .Select(creature => ((Node2D)creature.Visuals, creature.Visuals.GlobalPosition.Y)).ToArray();
        await RunManager.Instance.WinRun();
        await RunGameOverScreenAsync(win: true, fight, actors, "game_over_boss_win");
    }
}
