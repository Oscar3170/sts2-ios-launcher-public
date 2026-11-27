using System.Text.Json;

namespace STS2Mobile.DesktopHost;

/// <summary>
/// Which checkout this assembly was built from: <c>launcher-source.json</c>, written by scripts/desktop-test/build.sh next to
/// the installed dll as <c>{"revision":"&lt;git rev-parse HEAD&gt;","dirty":&lt;tracked src/ or tests/DesktopHost/ differ
/// from HEAD&gt;,"path":"..."}</c>. The harness and the portrait source are one checkout, so this maps a RESULTS.json to
/// the exact code of both.
/// </summary>
internal sealed record LauncherSource(string Revision, bool? Dirty, string? Checkout)
{
    public const string FileName = "launcher-source.json";

    public static readonly LauncherSource Unknown = new("unknown", null, null);

    public static LauncherSource Read()
    {
        try
        {
            string directory = Path.GetDirectoryName(typeof(LauncherSource).Assembly.Location) ?? "";
            string file = Path.Combine(directory, FileName);
            if (!File.Exists(file)) return Unknown;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = document.RootElement;
            string revision = root.TryGetProperty("revision", out JsonElement r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? "unknown"
                : "unknown";
            bool? dirty = root.TryGetProperty("dirty", out JsonElement d) && d.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? d.GetBoolean()
                : null;
            string? checkout = root.TryGetProperty("path", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            return new LauncherSource(revision, dirty, checkout);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Unknown;
        }
    }
}
