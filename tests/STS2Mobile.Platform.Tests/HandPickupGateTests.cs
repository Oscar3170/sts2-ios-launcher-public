using STS2Mobile.Platform;
using Xunit;

namespace STS2Mobile.Platform.Tests;

public class HandPickupGateTests
{
    [Fact]
    public void ATouchPressIsDeferredWhileTheHandWaits()
    {
        HandPickupGate.Disable();
        HandPickupGate.Enable();
        HandPickupGate.NotePress(-1);
        Assert.True(HandPickupGate.Defer());
    }

    [Fact]
    public void ARealMousePressKeepsTheGamesImmediatePickup()
    {
        HandPickupGate.Disable();
        HandPickupGate.Enable();
        HandPickupGate.NotePress(0);
        Assert.False(HandPickupGate.Defer());
    }

    [Fact]
    public void APressIsSpentByTheFirstQuestion()
    {
        HandPickupGate.Disable();
        HandPickupGate.Enable();
        HandPickupGate.NotePress(-1);
        Assert.True(HandPickupGate.Defer());
        Assert.False(HandPickupGate.Defer());
    }

    [Fact]
    public void AQuestionWithoutAPressIsNotDeferred()
    {
        HandPickupGate.Disable();
        HandPickupGate.Enable();
        Assert.False(HandPickupGate.Defer());
    }

    [Fact]
    public void AHandThatDoesNotWaitKeepsTheGamesPress()
    {
        HandPickupGate.Disable();
        HandPickupGate.NotePress(-1);
        Assert.False(HandPickupGate.Defer());
    }

    [Fact]
    public void DisablingForgetsAPendingTouchPress()
    {
        HandPickupGate.Disable();
        HandPickupGate.Enable();
        HandPickupGate.NotePress(-1);
        HandPickupGate.Disable();
        HandPickupGate.Enable();
        Assert.False(HandPickupGate.Defer());
    }
}
