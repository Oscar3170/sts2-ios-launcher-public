namespace STS2Mobile.Platform.Tests;

public class FrameRateGovernorTests
{
    private static readonly PhoneGraphicsSettings Off = PhoneGraphicsSettings.Default.WithPreset(BatteryPreset.Off);
    private static readonly PhoneGraphicsSettings Idle = Off with { LowerIdleFrameRate = true };
    private static readonly PhoneGraphicsSettings Cool = Off with { CoolDownWhenHot = true };

    [Fact]
    public void WithoutChoicesTheGameSettingStands() =>
        Assert.Equal(60, FrameRateGovernor.Target(60, Off, false, 100, DevicePower.Critical, animating: false));

    [Fact]
    public void UnlimitedGameSettingStaysUnlimited() =>
        Assert.Equal(0, FrameRateGovernor.Target(0, Off, false, 100, DevicePower.Nominal, animating: false));

    [Fact]
    public void TouchingOrARecentTouchKeepsTheFullRate()
    {
        Assert.Equal(60, FrameRateGovernor.Target(60, Idle, true, 100, DevicePower.Nominal, animating: false));
        Assert.Equal(60, FrameRateGovernor.Target(60, Idle, false, 1.9, DevicePower.Nominal, animating: false));
    }

    [Fact]
    public void IdleLowersTo30()
    {
        Assert.Equal(30, FrameRateGovernor.Target(60, Idle, false, 2, DevicePower.Nominal, animating: false));
        Assert.Equal(30, FrameRateGovernor.Target(0, Idle, false, 5, DevicePower.Nominal, animating: false));
    }

    [Fact]
    public void CapNeverRaisesTheGameSetting() =>
        Assert.Equal(24, FrameRateGovernor.Target(24, Idle, false, 5, DevicePower.Nominal, animating: false));

    [Fact]
    public void GameActivityKeepsTheFullRate()
    {
        // A turn draw or an enemy turn comes seconds after the last touch; the hand moving (and the game's own actions
        // being disabled) must not be capped to 30.
        Assert.Equal(60, FrameRateGovernor.Target(60, Idle, false, 100, DevicePower.Nominal, animating: true));
        Assert.Equal(0, FrameRateGovernor.Target(0, Idle, false, 100, DevicePower.Nominal, animating: true));
        Assert.Equal(24, FrameRateGovernor.Target(24, Idle, false, 100, DevicePower.Nominal, animating: true));
    }

    [Fact]
    public void ActivityEndingGivesTheIdleCapBackAtOnce()
    {
        Assert.Equal(60, FrameRateGovernor.Target(60, Idle, false, 10, DevicePower.Nominal, animating: true));
        Assert.Equal(30, FrameRateGovernor.Target(60, Idle, false, 10, DevicePower.Nominal, animating: false));
    }

    [Theory]
    [InlineData(DevicePower.Fair, 60)]
    [InlineData(DevicePower.Serious, 30)]
    [InlineData(DevicePower.Critical, 20)]
    [InlineData(DevicePower.UnknownThermalState, 60)]
    public void HeatLowersTheCapByThermalState(int thermalState, int expected) =>
        Assert.Equal(expected, FrameRateGovernor.Target(60, Cool, true, 0, thermalState, animating: false));

    [Theory]
    [InlineData(DevicePower.Fair, 60)]
    [InlineData(DevicePower.Serious, 30)]
    [InlineData(DevicePower.Critical, 20)]
    public void HeatStillCapsWhileTheGameAnimates(int thermalState, int expected) =>
        Assert.Equal(expected, FrameRateGovernor.Target(60, Cool, false, 100, thermalState, animating: true));
}
