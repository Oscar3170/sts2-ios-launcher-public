using STS2Mobile.Platform;
using Xunit;

namespace STS2Mobile.Platform.Tests;

public class HandEffectsWindowTests
{
    [Fact]
    public void AReSpacingHandNeverHidesAnything()
    {
        var window = new HandEffectsWindow();
        // A play closes the row: every holder moves, no card arrives from the draw pile.
        for (long frame = 100; frame < 140; frame++)
            window.NoteMotion(frame);
        Assert.False(window.Hiding);
        Assert.False(window.ShouldRestore(reduceEffects: true, drawOpen: false, frame: 140));
    }

    [Fact]
    public void AnArrivalOpensTheWindow()
    {
        var window = new HandEffectsWindow();
        Assert.False(window.Hiding);
        window.Begin(500);
        Assert.True(window.Hiding);
        Assert.Equal(500, window.StartFrame);
        Assert.False(window.ShouldRestore(reduceEffects: true, drawOpen: true, frame: 500));
    }

    [Fact]
    public void TheWindowClosesSettleFramesAfterTheHandStops()
    {
        var window = new HandEffectsWindow();
        window.Begin(100);
        window.NoteMotion(104);
        // Motion is read from the draw's own frames: the last one is still motion, and the two frames that follow it are
        // the ones an observer would need to agree the card has stopped.
        Assert.False(window.ShouldRestore(true, drawOpen: false, frame: 105));
        Assert.False(window.ShouldRestore(true, drawOpen: false, frame: 106));
        Assert.True(window.ShouldRestore(true, drawOpen: false, frame: 107));
        Assert.False(window.Hiding);
        // Closed means closed: no second restore, and no hiding until another card arrives.
        Assert.False(window.ShouldRestore(true, drawOpen: false, frame: 109));
        window.NoteMotion(110);
        Assert.False(window.Hiding);
    }

    [Fact]
    public void AnOpenDrawBatchHoldsTheWindowPastTheSettleCount()
    {
        var window = new HandEffectsWindow();
        window.Begin(10);
        // The batch stays open while the rest of a turn draw is on its way: no card may get its effects back between
        // two arrivals, or the hand would light up one card at a time.
        for (long frame = 11; frame < 29; frame++)
            Assert.False(window.ShouldRestore(true, drawOpen: true, frame));
        Assert.True(window.ShouldRestore(true, drawOpen: true, frame: 30));
    }

    [Fact]
    public void ALaterArrivalJoinsTheOpenWindowWithoutMovingItsCap()
    {
        var window = new HandEffectsWindow();
        window.Begin(0);
        window.NoteMotion(5);
        window.Begin(10);
        Assert.True(window.Hiding);
        Assert.Equal(0, window.StartFrame);
        // The second card's own motion counts, and the window still closes for the whole hand on one frame.
        Assert.False(window.ShouldRestore(true, drawOpen: false, frame: 12));
        Assert.True(window.ShouldRestore(true, drawOpen: false, frame: 13));
    }

    [Fact]
    public void TheQuietBoundClosesAnOpenBatchThatStoppedDelivering()
    {
        var window = new HandEffectsWindow();
        window.Begin(0);
        Assert.False(window.ShouldRestore(true, drawOpen: true, frame: 19));
        Assert.True(window.ShouldRestore(true, drawOpen: true, frame: 20));
    }

    [Fact]
    public void TheHardCapClosesEvenAHandThatNeverStopsMoving()
    {
        var window = new HandEffectsWindow();
        window.Begin(0);
        for (long frame = 1; frame < HandEffectsWindow.MaxHiddenFrames; frame++)
        {
            window.NoteMotion(frame);
            Assert.False(window.ShouldRestore(true, drawOpen: true, frame));
        }
        window.NoteMotion(HandEffectsWindow.MaxHiddenFrames);
        Assert.True(window.ShouldRestore(true, drawOpen: true, frame: HandEffectsWindow.MaxHiddenFrames));
    }

    [Fact]
    public void TurningTheSettingOffRestoresAtOnce()
    {
        var window = new HandEffectsWindow();
        window.Begin(0);
        window.NoteMotion(1);
        Assert.True(window.ShouldRestore(reduceEffects: false, drawOpen: true, frame: 2));
        Assert.False(window.Hiding);
        Assert.False(window.ShouldRestore(reduceEffects: false, drawOpen: true, frame: 3));
    }

    [Fact]
    public void ResetForgetsAnOpenWindow()
    {
        var window = new HandEffectsWindow();
        window.Begin(0);
        window.Reset();
        Assert.False(window.Hiding);
        Assert.False(window.ShouldRestore(true, drawOpen: false, frame: 600));
        window.Begin(700);
        Assert.Equal(700, window.StartFrame);
    }

    [Fact]
    public void AWholeDrawRestoresOnceForEveryCard()
    {
        // Three cards arrive while the batch is open; the hand keeps moving between them and after the last one. The
        // window allows exactly one restore frame, and that frame is the one the whole hand comes back on.
        var window = new HandEffectsWindow();
        int restores = 0;
        long restoreFrame = -1;
        long[] arrivals = [10, 22, 34];
        int next = 0;
        for (long frame = 10; frame <= 60; frame++)
        {
            if (next < arrivals.Length && arrivals[next] == frame)
            {
                window.Begin(frame);
                next++;
            }
            if (frame <= 40)
                window.NoteMotion(frame); // every drawn card, and the row re-centring around it, moves
            if (window.ShouldRestore(reduceEffects: true, drawOpen: frame <= 34, frame))
            {
                restores++;
                restoreFrame = frame;
            }
        }
        Assert.Equal(1, restores);
        Assert.Equal(43, restoreFrame);
    }
}
