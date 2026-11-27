using HarmonyLib;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// Keeps test runs out of the active save profile.
///
/// Mods are initialized before SaveManager.InitProfileId(), so a postfix on
/// ProfileSaveManager.LoadProfile can redirect the in-memory profile selection.
/// The profile.save file itself is not rewritten with the test profile ID; the
/// original LastProfileId is restored before the game exits.
/// </summary>
internal static class TestProfileScope
{
    private const int FallbackProfileId = 2;

    private static readonly Harmony Harmony = new("sts2mobile.desktophost.profile");

    private static bool _installed;
    private static bool _captured;
    private static bool _restored;
    private static int _requestedProfileId = 3;
    private static int _testProfileId = 3;
    private static int _originalProfileId = 1;
    private static ProfileSaveManager? _manager;

    public static int TestProfileId => _testProfileId;

    public static int OriginalProfileId => _originalProfileId;

    /// <summary>
    /// The profile the run actually uses: the requested one unless that is the player's active profile, in which case the
    /// other slot. Shared with the launcher host's <c>UserDataGuard</c> so a pre-run snapshot can exclude exactly the
    /// directory the comparison ignores.
    /// </summary>
    internal static int ResolveTestProfileId(int requested, int original) =>
        requested == original ? (original == FallbackProfileId ? 3 : FallbackProfileId) : requested;

    public static void Install(int requestedProfileId)
    {
        _requestedProfileId = requestedProfileId;
        if (_installed)
        {
            return;
        }

        _installed = true;
        Harmony.PatchAll(typeof(TestProfileScope).Assembly);
    }

    /// <summary>Called by the Harmony postfix after the real profile.save has been read.</summary>
    internal static void OnProfileLoaded(ProfileSaveManager manager)
    {
        if (!_captured)
        {
            _captured = true;
            _manager = manager;
            _originalProfileId = manager.Profile.LastProfileId;

            _testProfileId = ResolveTestProfileId(_requestedProfileId, _originalProfileId);
        }

        manager.Profile.LastProfileId = _testProfileId;
    }

    /// <summary>
    /// Restores LastProfileId in memory and writes the original account profile
    /// selection back to disk. Called before quitting the game.
    /// </summary>
    public static void Restore()
    {
        if (!_captured || _restored || _manager is null)
        {
            return;
        }

        _restored = true;
        try
        {
            _manager.Profile.LastProfileId = _originalProfileId;
            _manager.SaveProfile();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not restore the original save profile: {ex}");
        }
    }
}

[HarmonyPatch(typeof(ProfileSaveManager), nameof(ProfileSaveManager.LoadProfile))]
internal static class ProfileSaveManagerLoadPatch
{
    [HarmonyPostfix]
    private static void Postfix(ProfileSaveManager __instance)
    {
        TestProfileScope.OnProfileLoaded(__instance);
    }
}
