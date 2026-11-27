using System;
using System.IO;
using System.Linq;
using Godot;

namespace STS2Mobile.DesktopHost.Harness;

internal enum PointerMode
{
    Mouse,
    Touch,
}

internal sealed class TestOptions
{
    public string Scenario { get; private set; } = "menu";

    public PointerMode PointerMode { get; private set; } = PointerMode.Mouse;

    // The fixtures' seed since the harness was written: every fixture's map, encounters and rewards follow from it, so a new
    // value would change what each scenario sees.
    public string Seed { get; private set; } = "TOUCHSPIRE-TEST";

    public string OutputDirectory { get; private set; } = "";

    public int ProfileId { get; private set; } = 3;

    public bool SkipLogo { get; private set; }
    public Vector2I? WindowSize { get; private set; }
    public string TourKind { get; private set; } = "all";
    public int TourOffset { get; private set; }
    public int TourLimit { get; private set; } = int.MaxValue;

    public static bool IsRequested(string[] args) =>
        args.Any(arg => arg == "--sts2mobile-test" || arg.StartsWith("--sts2mobile-test=", StringComparison.Ordinal));

    public static TestOptions Parse(string[] args)
    {
        var options = new TestOptions();

        string? scenario = GetValue(args, "--sts2mobile-test");
        if (!string.IsNullOrWhiteSpace(scenario))
        {
            options.Scenario = scenario.Trim().ToLowerInvariant();
        }

        if (args.Any(arg => arg == "--sts2mobile-test-touch" || arg == "--sts2mobile-test-mode=touch"))
        {
            options.PointerMode = PointerMode.Touch;
        }

        options.SkipLogo = args.Any(arg => arg == "--sts2mobile-test-skip-logo");
        if (GetValue(args, "--sts2mobile-test-window") is { } dimensions)
        {
            string[] parts = dimensions.Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int width) || !int.TryParse(parts[1], out int height)
                || width <= 0 || height <= 0)
                throw new ArgumentException("Expected --sts2mobile-test-window=WIDTHxHEIGHT.");
            options.WindowSize = new Vector2I(width, height);
        }
        options.TourKind = GetValue(args, "--sts2mobile-tour-kind") ?? "all";
        if (int.TryParse(GetValue(args, "--sts2mobile-tour-offset"), out int offset)) options.TourOffset = Math.Max(0, offset);
        if (int.TryParse(GetValue(args, "--sts2mobile-tour-limit"), out int limit)) options.TourLimit = Math.Max(1, limit);

        string? seed = GetValue(args, "--sts2mobile-test-seed");
        if (!string.IsNullOrWhiteSpace(seed))
        {
            options.Seed = seed.Trim();
        }

        string? profile = GetValue(args, "--sts2mobile-test-profile");
        if (int.TryParse(profile, out int profileId) && profileId is >= 1 and <= 3)
        {
            options.ProfileId = profileId;
        }

        string? output = GetValue(args, "--sts2mobile-test-output");
        options.OutputDirectory = string.IsNullOrWhiteSpace(output)
            ? Path.Combine(TestRunsDirectory(), $"sts2mobile-test-{DateTime.Now:yyyyMMdd-HHmmss}")
            : Path.GetFullPath(output);

        return options;
    }

    /// <summary>
    /// Test results belong to the checkout, not to /tmp: scripts/desktop-test/run.sh passes --sts2mobile-test-output, and
    /// exports STS2MOBILE_PROJECT_ROOT so a run without it still lands in the checkout's .work/test-runs, where results
    /// survive reboots and stay visible to an isolated agent working in the repo. Without either (a bare manual launch) the
    /// system temporary directory is used.
    /// </summary>
    private static string TestRunsDirectory()
    {
        string? root = System.Environment.GetEnvironmentVariable("STS2MOBILE_PROJECT_ROOT");
        return string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Path.GetTempPath(), "sts2mobile-test")
            : Path.Combine(root, ".work", "test-runs");
    }

    private static string? GetValue(string[] args, string name)
    {
        string prefix = name + "=";
        foreach (string arg in args)
        {
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
            {
                return arg[prefix.Length..];
            }
        }

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
