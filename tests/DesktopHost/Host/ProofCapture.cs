using System.Reflection;
using System.Collections.Concurrent;
using Godot;
using HarmonyLib;

namespace STS2Mobile.DesktopHost;

/// <summary>
/// Captures the launcher's <c>PortraitProof</c> output, which only prints through <c>GD.Print</c>. A Harmony prefix on the two
/// <c>GD.Print</c> overloads (string, and params object[] which GodotSharp concatenates without a separator) queues the lines
/// the runner cares about: every <c>Portrait proof</c> line and the proofs' <c>... results:</c> summaries. The prefixes only
/// read the text; Godot still prints it, so <c>game.log</c> is unchanged. Test runs with a proof only, own Harmony id.
/// A custom <c>Godot.Logger</c> would need generated bindings, which this plain-SDK assembly does not get.
/// </summary>
internal static class ProofCapture
{
    public const string HarmonyId = "sts2mobile.desktophost.proofcapture";
    public const string ProofPrefix = "Portrait proof";

    private static readonly string[] ResultPrefixes =
    [
        "Touch rewards results:", "Touch choices results:", "Touch proof results:", "Stock landscape results:",
        "Switch mid choice results:", "Map acts results:", "Notes proof results:",
    ];

    private static readonly ConcurrentQueue<string> Lines = new();
    private static Harmony? _harmony;

    public static bool IsEmpty => Lines.IsEmpty;

    public static bool TryDequeue(out string line)
    {
        bool found = Lines.TryDequeue(out string? next);
        line = next ?? "";
        return found;
    }

    /// <summary>Installs the prefixes. Returns null on success, else why the output cannot be captured.</summary>
    public static string? Install()
    {
        if (_harmony != null) return null;
        try
        {
            MethodInfo printString = AccessTools.Method(typeof(GD), nameof(GD.Print), [typeof(string)])
                ?? throw new InvalidOperationException("GD.Print(string) not found.");
            MethodInfo printParams = AccessTools.Method(typeof(GD), nameof(GD.Print), [typeof(object[])])
                ?? throw new InvalidOperationException("GD.Print(params object[]) not found.");
            var harmony = new Harmony(HarmonyId);
            harmony.Patch(printString, prefix: new HarmonyMethod(typeof(ProofCapture), nameof(PrintString)));
            harmony.Patch(printParams, prefix: new HarmonyMethod(typeof(ProofCapture), nameof(PrintParams)));
            _harmony = harmony;
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static void PrintString(string __0) => Offer(__0);

    private static void PrintParams(object[] __0)
    {
        if (__0 == null) return;
        Offer(string.Concat(__0.Select(item => item?.ToString() ?? "null")));
    }

    private static void Offer(string? text)
    {
        if (text == null) return;
        if (text.StartsWith(ProofPrefix, StringComparison.Ordinal) || ResultPrefixes.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal)))
            Lines.Enqueue(text);
    }

    public static bool IsResultsLine(string line) => ResultPrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal));
}
