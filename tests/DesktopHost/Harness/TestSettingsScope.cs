using MegaCrit.Sts2.Core.Saves;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// In-memory-only intro-logo skip for faster test iterations. The original
/// setting is restored before the game exits.
/// </summary>
internal static class TestSettingsScope
{
    private static bool _active;
    internal static bool IsActive => _active;
    private static bool _original;

    public static void InstallSkipLogo()
    {
        try
        {
            if (SaveManager.Instance?.SettingsSave is not { } settings)
            {
                return;
            }

            _original = settings.SkipIntroLogo;
            settings.SkipIntroLogo = true;
            _active = true;
            Log.Info("[TEST] intro logo skipped for this run (in-memory only)");
        }
        catch (Exception ex)
        {
            Log.Warn($"[TEST] could not skip intro logo: {ex.Message}");
        }
    }

    public static void Restore()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        try
        {
            if (SaveManager.Instance?.SettingsSave is { } settings)
            {
                settings.SkipIntroLogo = _original;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[TEST] could not restore intro-logo setting: {ex.Message}");
        }
    }
}
