using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// The idle frame cap's one write, shared by the two places that need it: <c>PhoneGraphics</c> publishes the player's
/// choices and touch state every frame and applies the cap, and the portrait hand pass applies it again on a frame where
/// a draw has just started — that frame's process step, where <c>PhoneGraphics</c> ran, could not see the motion yet. The
/// test host makes the same two calls, so a run exercises the app's own path. Nothing is written until the choices have
/// been published.
/// </summary>
internal static class PhoneFrameCap
{
    private static PhoneGraphicsSettings _settings = PhoneGraphicsSettings.Default;
    private static int _gameLimit;
    private static double _secondsSinceTouch;
    private static int _thermalState = DevicePower.UnknownThermalState;
    private static bool _touching;
    private static bool _published;

    /// <summary>The state the pure <see cref="FrameRateGovernor.Target"/> decides from; called once a frame by PhoneGraphics.</summary>
    public static void SetInputs(int gameLimit, PhoneGraphicsSettings settings, bool touching, double secondsSinceTouch,
        int thermalState)
    {
        _gameLimit = gameLimit;
        _settings = settings;
        _touching = touching;
        _secondsSinceTouch = secondsSinceTouch;
        _thermalState = thermalState;
        _published = true;
    }

    /// <summary>Writes <c>Engine.MaxFps</c> from the published state and the game's activity now.</summary>
    public static void Apply()
    {
        if (!_published) return;
        int target = FrameRateGovernor.Target(_gameLimit, _settings, _touching, _secondsSinceTouch, _thermalState,
            PortraitCombatLayout.CombatAnimating);
        if (Engine.MaxFps != target) Engine.MaxFps = target;
    }
}
