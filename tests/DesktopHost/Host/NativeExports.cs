using System.Globalization;
using System.Runtime.InteropServices;
using Godot;

namespace STS2Mobile.Platform;

/// <summary>
/// Desktop replacement for the launcher's <c>STS2Mobile.Platform/NativeExports.cs</c> (excluded from this assembly). The launcher
/// looks C functions up by name in the iOS app's native units; here the three the portrait path asks for are managed
/// <see cref="UnmanagedCallersOnlyAttribute"/> functions, and every other name gives 0, exactly as off the phone.
/// <list type="bullet">
/// <item><c>sts2_screen_top_inset</c>: the top inset fraction of <c>STS2MOBILE_TEST_INSETS=top,side</c>, in portrait only.</item>
/// <item><c>sts2_screen_side_inset</c>: the side inset fraction, in landscape only (see the doc comments in SafeAreaCanvas).</item>
/// <item><c>sts2_orientation_set</c>: records and logs the request. The host never rotates or resizes anything.</item>
/// </list>
/// </summary>
internal static unsafe class NativeExports
{
    public const string InsetsVariable = "STS2MOBILE_TEST_INSETS";

    private static int _lastOrientation = -1;
    private static int _orientationRequests;

    static NativeExports()
    {
        (TopInsetFraction, SideInsetFraction, InsetsError) = ParseInsets(System.Environment.GetEnvironmentVariable(InsetsVariable));
    }

    /// <summary>Top inset as a fraction of the screen's height (portrait).</summary>
    public static double TopInsetFraction { get; }

    /// <summary>Side inset as a fraction of the screen's width (landscape).</summary>
    public static double SideInsetFraction { get; }

    /// <summary>Why <c>STS2MOBILE_TEST_INSETS</c> was not used (the insets are then 0,0), or null.</summary>
    public static string? InsetsError { get; }

    /// <summary>How many times the launcher asked for an orientation, and the last one (for RESULTS.json).</summary>
    public static string OrientationRequests =>
        _orientationRequests == 0 ? "none" : $"{_orientationRequests} request(s), last {(_lastOrientation != 0 ? "landscape" : "portrait")}";

    public static string InsetsText =>
        string.Create(CultureInfo.InvariantCulture, $"{TopInsetFraction:R},{SideInsetFraction:R}");

    public static nint Find(string name)
    {
        switch (name)
        {
            case "sts2_screen_top_inset": return (nint)(delegate* unmanaged<double>)&TopInset;
            case "sts2_screen_side_inset": return (nint)(delegate* unmanaged<double>)&SideInset;
            case "sts2_orientation_set": return (nint)(delegate* unmanaged<int, void>)&OrientationSet;
            default: return 0;
        }
    }

    [UnmanagedCallersOnly]
    private static double TopInset() => SafeAreaCanvas.IsPortrait ? TopInsetFraction : 0;

    [UnmanagedCallersOnly]
    private static double SideInset() => SafeAreaCanvas.IsPortrait ? 0 : SideInsetFraction;

    [UnmanagedCallersOnly]
    private static void OrientationSet(int landscape)
    {
        try
        {
            _lastOrientation = landscape;
            Interlocked.Increment(ref _orientationRequests);
            GD.Print($"Launcher host: orientation request ({(landscape != 0 ? "landscape" : "portrait")}) recorded; the host never rotates or resizes.");
        }
        catch
        {
            // Nothing may unwind into the caller of a native-style function.
        }
    }

    private static (double Top, double Side, string? Error) ParseInsets(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (0, 0, null);
        string[] parts = text.Split(',');
        if (parts.Length != 2
            || !double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double top)
            || !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double side)
            || !double.IsFinite(top) || !double.IsFinite(side) || top < 0 || top >= 1 || side < 0 || side >= 0.5)
            return (0, 0, $"{InsetsVariable}='{text}' is not 'top,side' fractions (top in [0,1), side in [0,0.5)); using 0,0.");
        return (top, side, null);
    }
}
