using STS2Mobile.Platform;
using Xunit;

namespace STS2Mobile.Platform.Tests;

public class SelectConfirmMathTests
{
    [Theory]
    [InlineData(0f, 0.0, SelectPress.Pending)]
    [InlineData(SelectConfirmMath.ConfirmSlop, 0.0, SelectPress.Pending)]
    [InlineData(SelectConfirmMath.ConfirmSlop - 0.01f, SelectConfirmMath.HoldSeconds - 0.01, SelectPress.Pending)]
    [InlineData(0f, SelectConfirmMath.HoldSeconds, SelectPress.Inspect)]
    [InlineData(0f, SelectConfirmMath.HoldSeconds + 1, SelectPress.Inspect)]
    [InlineData(SelectConfirmMath.ConfirmSlop - 0.01f, SelectConfirmMath.HoldSeconds, SelectPress.Inspect)]
    [InlineData(SelectConfirmMath.ConfirmSlop + 0.01f, 0.0, SelectPress.Cancel)]
    [InlineData(SelectConfirmMath.ConfirmSlop + 0.01f, 0.1, SelectPress.Cancel)]
    [InlineData(60f, 2.0, SelectPress.Cancel)]
    public void AStillPressWaitsThenInspectsOrCancels(float moved, double seconds, SelectPress expected) =>
        Assert.Equal(expected, SelectConfirmMath.Decide(moved, seconds, released: false));

    [Theory]
    [InlineData(0f, 0.0, SelectPress.Confirm)]
    [InlineData(SelectConfirmMath.ConfirmSlop, SelectConfirmMath.HoldSeconds - 0.01, SelectPress.Confirm)]
    [InlineData(0f, SelectConfirmMath.HoldSeconds - 0.01, SelectPress.Confirm)]
    [InlineData(0f, SelectConfirmMath.HoldSeconds, SelectPress.Cancel)]
    [InlineData(0f, SelectConfirmMath.HoldSeconds + 1, SelectPress.Cancel)]
    [InlineData(SelectConfirmMath.ConfirmSlop + 0.01f, 0.1, SelectPress.Cancel)]
    [InlineData(60f, SelectConfirmMath.HoldSeconds + 1, SelectPress.Cancel)]
    public void AReleaseConfirmsOnlyBeforeTheHoldAndWithinTheSlop(float moved, double seconds, SelectPress expected) =>
        Assert.Equal(expected, SelectConfirmMath.Decide(moved, seconds, released: true));

    [Theory]
    [InlineData(0f, true)]
    [InlineData(SelectConfirmMath.ConfirmSlop, true)]
    [InlineData(SelectConfirmMath.ConfirmSlop + 0.01f, false)]
    [InlineData(100f, false)]
    public void ATapStaysWithinTheSlop(float moved, bool expected) =>
        Assert.Equal(expected, SelectConfirmMath.WithinSlop(moved));

    [Fact]
    public void TheSlopIsEightPhonePoints() => Assert.Equal(23f, SelectConfirmMath.ConfirmSlop);

    [Fact]
    public void TheHoldIsTheHandCardsHold() => Assert.Equal(TouchCardMath.HoldSeconds, SelectConfirmMath.HoldSeconds, 6);

    [Fact]
    public void TheSameItemNeedsBothNodeAndModel()
    {
        object card = new(), other = new();
        Assert.True(SelectConfirmMath.IsSameItem(7, card, 7, card));
        Assert.False(SelectConfirmMath.IsSameItem(7, other, 7, card));
        Assert.False(SelectConfirmMath.IsSameItem(8, card, 7, card));
        Assert.False(SelectConfirmMath.IsSameItem(0, card, 0, card));
    }

    [Fact]
    public void ElapsedSecondsCountFromThePress()
    {
        Assert.Equal(0.0, SelectConfirmMath.SecondsSince(1000, 1000));
        Assert.Equal(0.0, SelectConfirmMath.SecondsSince(1000, 900));
        Assert.Equal(0.6, SelectConfirmMath.SecondsSince(1000, 1600), 6);
    }

    [Fact]
    public void MovementIsTheStraightLineDistance()
    {
        Assert.Equal(5f, SelectConfirmMath.Moved(3f, 4f), 6);
        Assert.Equal(SelectConfirmMath.ConfirmSlop, SelectConfirmMath.Moved(SelectConfirmMath.ConfirmSlop, 0f), 6);
    }
}
