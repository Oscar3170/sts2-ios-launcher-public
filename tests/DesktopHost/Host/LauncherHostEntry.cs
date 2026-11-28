using Godot;
using MegaCrit.Sts2.Core.Modding;
using STS2Mobile.DesktopHost.Harness;

namespace STS2Mobile.DesktopHost;

/// <summary>
/// Desktop test host for the launcher's in-game portrait source (docs/TESTING.md). The launcher's <c>STS2Mobile.GameHost</c>
/// and <c>STS2Mobile.Platform</c> are compiled, unmodified, into this assembly and run inside the installed desktop game; it
/// is a development tool, not a launcher port and not iOS validation.
/// <para>
/// Inert (no Harmony, no snapshot, no logging) unless the command line has <c>--sts2mobile-test[=scenario]</c> or
/// <see cref="PortraitFlag"/>. Active: the PhonePatches shims are applied (<see cref="PhonePatchShims"/>), then the portrait
/// wiring is attached (<see cref="LauncherHostWiring"/>), so the game plays the launcher's portrait. A test run also installs
/// the harness isolation (window size, test profile, skip-logo, user-data and settings protection) and
/// <see cref="LauncherHostRunner"/> writes RESULTS.json. Environment: <c>STS2_PORTRAIT_PROOF</c> (the launcher's proof
/// mode), <c>STS2_PORTRAIT_NOFIT</c> (read by the launcher code), <c>STS2MOBILE_TEST_INSETS=top,side</c> (see
/// <c>NativeExports</c>), <c>STS2MOBILE_TEST_TIMEOUT</c> seconds.
/// </para>
/// </summary>
[ModInitializer(nameof(Initialize))]
public static class LauncherHostEntry
{
    /// <summary>Plays the launcher's portrait without a test: the wiring and shims only, the player's own profile.</summary>
    public const string PortraitFlag = "--sts2mobile-portrait";

    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        string[] args = OS.GetCmdlineArgs()
            .Concat(OS.GetCmdlineUserArgs())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        bool testRun = TestOptions.IsRequested(args);
        if (!testRun && !args.Contains(PortraitFlag)) return;

        // The host's own runs measure the portrait passes: the perf line and the per-pass figure are what the harness
        // reports. Device builds leave it off unless the user turns "Performance Log" on in Graphics.
        STS2Mobile.GameHost.PortraitFrameTiming.SetEnabled(true);

        TestOptions? options = null;
        UserDataSnapshot? before = null;
        string? captureError = null;
        try
        {
            if (testRun)
            {
                // Parsing only reads the command line; the snapshot must still come before anything can write. The
                // isolated test profile is skipped because UserDataGuard.Changes ignores every path under it anyway.
                options = TestOptions.Parse(args);
                before = UserDataGuard.Take(UserDataGuard.ResolveIsolatedTestProfileId(options.ProfileId));
            }

            PhonePatchShims.Install();

            if (options != null)
            {
                PhonePatchShims.SuppressSettingsWrites = true;
                PointerFollowsInjectedInput.Install();
                TouchHoverOwnership.Install();
                if (OS.GetEnvironment("STS2_PORTRAIT_PROOF").Length > 0) captureError = ProofCapture.Install();
                if (args.Contains("--sts2mobile-test-sync-assets"))
                {
                    MegaCrit.Sts2.Core.Assets.PreloadManager.Enabled = false;
                    Log.Info("[TEST] Using synchronous asset loading (fixture stability)");
                }
                // Isolation first: the window, then the test profile, then the logo skip.
                TestWindowScope.Install(options.WindowSize);
                TestWindowScope.MakeBorderless();
                TestProfileScope.Install(options.ProfileId);
                if (options.SkipLogo) TestSettingsScope.InstallSkipLogo();
                Log.Info($"Desktop host test requested: scenario={options.Scenario} pointer={options.PointerMode} " +
                         $"profile={options.ProfileId} output={options.OutputDirectory}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Desktop host could not start: {ex}");
            return;
        }

        LauncherSource source = LauncherSource.Read();
        Log.Info($"Desktop host active: launcher {source.Revision}{(source.Dirty == true ? "+dirty" : "")}, " +
                 $"insets {STS2Mobile.Platform.NativeExports.InsetsText}");
        Callable.From(() => { MegaCrit.Sts2.Core.Helpers.TaskHelper.RunSafely(StartAsync(options, before, captureError, source)); }).CallDeferred();
    }

    private static async Task StartAsync(TestOptions? options, UserDataSnapshot? before, string? captureError, LauncherSource source)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Log.Error("Launcher host requires a SceneTree.");
            return;
        }

        var wiring = new LauncherHostWiring();
        Exception? wiringError = null;
        // The menus scenario measures the phone's Graphics page. This host does not compile PhoneGraphics, so a fixture adds
        // that page's rows the way the phone does: before the game creates its settings screens, so their Ready callbacks add
        // the rows before the portrait layout's first pass sees them.
        if (options?.Scenario == "menus") PhoneGraphicsRows.Install(tree);
        // Decided once, at attach: portrait when the requested test window (else the window as it is now) is taller than wide.
        Vector2I window = options?.WindowSize ?? DisplayServer.WindowGetSize();
        bool portrait = window.Y > window.X;
        Log.Info($"Launcher host layout: {(portrait ? "portrait" : "landscape")} (window {window.X}x{window.Y}" +
                 $"{(options?.WindowSize != null ? ", requested" : "")})");
        try
        {
            wiring.Start(tree, portrait);
        }
        catch (Exception ex)
        {
            wiringError = ex;
            Log.Error($"Launcher host wiring failed: {ex}");
        }

        if (options == null || before == null) return;
        await LauncherHostRunner.RunAsync(options, wiring, wiringError, captureError, before, source);
    }
}
