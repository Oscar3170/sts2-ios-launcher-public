using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The end screens (<c>game-over</c> defeat, <c>game-over-win</c> victory, <c>game-over-boss</c> the act boss): fight, win or
/// die, open the native game over, Continue, then the settled scoring and summary, a long summary scrolled by finger, and
/// Main Menu. The fixture, the taps and the step names are shared; the pillarbox and the summary are the host's
/// (HostMenuChecks.cs / LauncherMenuChecks.cs). The T5 gate runs on the opening and the settled screen. GameOverVariants.cs
/// reuses <see cref="RunGameOverScreenAsync"/> for a death outside combat and a boss victory.
/// </summary>
internal sealed partial class TestDriver
{
    private async Task RunGameOverAsync(RunState state, bool win, bool boss = false)
    {
        await RunManager.Instance.EnterRoomDebug(boss ? RoomType.Boss : RoomType.Monster,
            model: boss ? ModelDb.Encounter<KaiserCrabBoss>().ToMutable() : ModelDb.Encounter<BowlbugsWeak>().ToMutable(),
            showTransition: false);
        await WaitUntilAsync(IsPlayerPlayPhase, "recorded combat encounter", MediumWait);
        var fight = NCombatRoom.Instance!;
        (Node2D Actor, float Y)[] actors = fight.CreatureNodes
            .Select(creature => ((Node2D)creature.Visuals, creature.Visuals.GlobalPosition.Y)).ToArray();
        if (win) await RunManager.Instance.WinRun();
        else await CreatureCmd.Kill(state.Players[0].Creature, force: true);
        await RunGameOverScreenAsync(win, fight, actors, "game_over");
    }

    /// <summary>
    /// The end screen itself, after the room (a fight, a rest site, a victory) has already ended: the native game over, its
    /// Continue entrance, the settled scoring and summary, a finger-scrolled long summary and Main Menu. <paramref name="fight"/>
    /// is null when the run ended outside combat, where there is no tableau to keep at its combat height and the launcher's
    /// act-art fallback has to stand in for the room; <paramref name="capture"/> prefixes the three screenshots.
    /// </summary>
    private async Task RunGameOverScreenAsync(bool win, NCombatRoom? fight,
        IReadOnlyList<(Node2D Actor, float Y)> actors, string capture)
    {
        await WaitUntilAsync(() => FindFirst<NGameOverScreen>(_tree.Root) is { } s && s.IsVisibleInTree(), "native game over", LongWait);
        NGameOverScreen screen = FindFirst<NGameOverScreen>(_tree.Root)!;
        var proceed = screen.GetNode<NGameOverContinueButton>("%ContinueButton");
        await WaitUntilAsync(() => proceed.IsVisibleInTree() && proceed.IsEnabled, "native Continue entrance", MediumWait);
        await WaitSecondsAsync(0.65);
        await CheckGameOverOpeningAsync(screen, proceed, actors, win);
        CheckTouchTargets("t5_game_over_opening", "game over (opening)", screen, NativeBackAllow, SummaryBadgeAllow, SummaryItemAllow);
        await CaptureAsync(capture + "_01_opening");
        await _pointer.TapAsync(CenterOf(proceed));
        var menu = screen.GetNode<NReturnToMainMenuButton>("%MainMenuButton");
        await WaitUntilAsync(() => menu.IsVisibleInTree() && menu.IsEnabled, "settled scoring and Main Menu", LongWait);
        await WaitSecondsAsync(0.65);
        var scores = screen.GetNode<GridContainer>("%ScoreLineContainer");
        _report.Check("game_over_native_scoring", scores.GetChildCount() >= 2, $"result={(win ? "win" : "defeat")} items={scores.GetChildCount()}");
        GameOverSummary? summary = GameOverSummaryOf(screen);
        CheckGameOverSettled(screen, menu, scores, summary, fight, win);
        if (fight is null) CheckGameOverOutsideCombat(screen, summary);
        CheckTouchTargets("t5_game_over_settled", "game over (settled)", screen, NativeBackAllow, SummaryBadgeAllow, SummaryItemAllow);
        await CaptureAsync(capture + "_02_summary");
        if (summary is not null && !win)
        {
            // Isolated long-summary stress fixture after native score settlement.
            // It does not modify the saved run, score, or unlocks.
            var firstRow = scores.GetChild<Control>(0);
            for (int i = 0; i < 24; i++) scores.AddChild(firstRow.Duplicate());
            await WaitFramesAsync(4);
            // The offset's limit is recomputed with the column's size; wait for it before dragging.
            await WaitOrFalseAsync(() => summary.Max() >= 100, TimeSpan.FromSeconds(2));
            Rect2 clip = summary.Clip;
            Vector2 from = clip.Position + clip.Size * new Vector2(0.5f, 0.79f);
            Vector2 to = clip.Position + clip.Size * new Vector2(0.5f, 0.26f);
            await _pointer.DragAsync(from, to, steps: 16);
            _report.Check("game_over_long_summary_scrolls_by_touch", summary.Offset() > 100 && summary.Max() >= summary.Offset(),
                $"offset={summary.Offset()} max={summary.Max()} window={clip}");
        }
        // The summary must be clipped, not merely inside a rect: a long list that draws over the banner and the art is the
        // failure a rect comparison cannot see. Checked after the stress fixture has been applied (and settled/scrolled).
        CheckGameOverSummaryClipping(screen, summary, scores);
        await CaptureAsync(capture + "_03_long_summary");
        // A tap whose screen did not open is retried once, bounded and logged, instead of waiting the long scenario timeout.
        await TapMenuUntilAsync(menu, "game_over_main_menu",
            () => NGame.Instance?.MainMenu is { } m && m.IsVisibleInTree(), "return to Main Menu", MediumWait);
        _report.Pass("game_over_main_menu_navigation");
    }

    /// <summary>
    /// The settled run summary as this host scrolls it: the window that clips it (its canvas rect), and where it is scrolled
    /// to and how far it can go, in canvas units. The TouchSpire host uses its own ScrollContainer; the launcher keeps the
    /// game's own CenterContainer in place and clips it, carrying the offset in node metadata. Null when the host has no
    /// scrolling summary (landscape, or a fixed native summary), which the landscape check relies on.
    /// </summary>
    private sealed record GameOverSummary(Control Window, Rect2 Clip, Func<int> Offset, Func<int> Max);

    // Host answer: the summary as described above, or null when this screen has none.
    private partial GameOverSummary? GameOverSummaryOf(NGameOverScreen screen);

    // Host answer: the opening screen's Continue button, the combat tableau's actors at their combat height, and a win's
    // victory message fitting its label and the frame.
    private partial Task CheckGameOverOpeningAsync(NGameOverScreen screen, NGameOverContinueButton proceed,
        IReadOnlyList<(Node2D Actor, float Y)> actors, bool win);

    // Host answer: after the long-summary stress fixture, the summary's rows are really clipped by a ClipContents ancestor
    // inside the reported window and any row it does not clip clears the banner and the act art (the launcher); the TouchSpire
    // host has its own summary and no such check (no-op there).
    private partial void CheckGameOverSummaryClipping(NGameOverScreen screen, GameOverSummary? summary, GridContainer scores);

    // Host answer: the settled screen's Main Menu button and scoring, the summary window inside the frame over the tableau
    // (or, with no fight, over the act art that stands in for the room), and (in landscape) that the game's own summary is
    // untouched.
    private partial void CheckGameOverSettled(NGameOverScreen screen, NReturnToMainMenuButton mainMenu, GridContainer scores,
        GameOverSummary? summary, NCombatRoom? fight, bool win);

    // Host answer: a defeat with no fight behind it. The room is gone, so the host's act-art fallback must cover the frame
    // with the banner under the run bar, the quote wrapped inside the banner and the summary window below both.
    private partial void CheckGameOverOutsideCombat(NGameOverScreen screen, GameOverSummary? summary);
}
