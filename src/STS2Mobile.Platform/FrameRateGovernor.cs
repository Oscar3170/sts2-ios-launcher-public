namespace STS2Mobile.Platform;

/// <summary>
/// Decides the frame cap from the game's own Max FPS setting and the phone's choices. It only ever
/// lowers the game's setting, never raises it.
/// </summary>
public static class FrameRateGovernor
{
    public const int IdleFrameRate = 30;
    public const double IdleAfterSeconds = 2;
    public const int SeriousFrameRate = 30;
    public const int CriticalFrameRate = 20;

    /// <param name="gameLimit">The game's Max FPS setting; 0 or less means no limit, as for <c>Engine.MaxFps</c>.</param>
    /// <param name="animating">
    /// The game is busy with something the player is waiting on (an action executing, hand cards moving). A turn draw and
    /// an enemy turn come seconds after the last touch, so without this the idle cap would run them at 30 fps; a settle
    /// after the activity gives the cap back at once.
    /// </param>
    /// <returns>The value for <c>Engine.MaxFps</c>; 0 means no limit.</returns>
    public static int Target(int gameLimit, PhoneGraphicsSettings settings, bool touching, double secondsSinceTouch,
        int thermalState, bool animating)
    {
        int cap = int.MaxValue;
        if (settings.LowerIdleFrameRate && !touching && !animating && secondsSinceTouch >= IdleAfterSeconds)
            cap = IdleFrameRate;
        if (settings.CoolDownWhenHot && thermalState >= DevicePower.Critical) cap = Math.Min(cap, CriticalFrameRate);
        else if (settings.CoolDownWhenHot && thermalState >= DevicePower.Serious) cap = Math.Min(cap, SeriousFrameRate);
        if (gameLimit <= 0) return cap == int.MaxValue ? 0 : cap;
        return Math.Min(gameLimit, cap);
    }
}
