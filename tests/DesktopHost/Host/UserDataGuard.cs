using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using STS2Mobile.DesktopHost.Harness;

namespace STS2Mobile.DesktopHost;

/// <summary>SHA-256 of every file a test run must leave alone, keyed by path relative to the user data directory.</summary>
internal sealed class UserDataSnapshot
{
    public required string Root { get; init; }
    public required SortedDictionary<string, string> Hashes { get; init; }
    /// <summary>The profile directory left out of <see cref="Hashes"/> because <see cref="UserDataGuard.Changes"/> ignores it.</summary>
    public int? ExcludedProfileId { get; init; }
}

/// <summary>
/// Proves a launcher-host test run did not touch the player's data. The files (under <c>OS.GetUserDataDir()</c>,
/// <c>~/.local/share/SlayTheSpire2</c> on Linux) are, per UserDataPathProvider/SaveManager in the decompiled game:
/// <list type="bullet">
/// <item><c>{steam|default|editor}/&lt;id&gt;/</c>, the whole account directory: <c>settings.save</c>, <c>profile.save</c>
/// (also under <c>modded/</c>), and every <c>[modded/]profileN/</c> tree (<c>saves/prefs.save</c>, <c>progress.save</c>, run saves,
/// <c>history/</c>, replays). The isolated test profile's directory is skipped before hashing and ignored by the comparison.</item>
/// <item><c>touchspire_backdrops.json</c> and the legacy pre-account <c>settings.save</c>/<c>profile.save</c> at the root.</item>
/// </list>
/// </summary>
internal static class UserDataGuard
{
    private static readonly string[] PlatformDirectories = ["steam", "default", "editor"];
    private static readonly string[] RootFiles = ["touchspire_backdrops.json", "settings.save", "profile.save"];

    /// <param name="excludedProfileId">
    /// A profile directory to leave out of the hashes. Only <see cref="ResolveIsolatedTestProfileId"/> (before a run) or the
    /// run's own <see cref="TestProfileScope.TestProfileId"/> (after it) may be passed: <see cref="Changes"/> already
    /// ignores every path under that directory (the <c>path.Split('/').Contains(testDirectory)</c> branch), so the
    /// comparison is unchanged while the run stops hashing saves it must not report on.
    /// </param>
    public static UserDataSnapshot Take(int? excludedProfileId = null)
    {
        string root = OS.GetUserDataDir();
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Files(root))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (excludedProfileId is int profileId && IsProfilePath(relative, profileId)) continue;
            hashes[relative] = Hash(file);
        }

        return new UserDataSnapshot { Root = root, Hashes = hashes, ExcludedProfileId = excludedProfileId };
    }

    private static IEnumerable<string> Files(string root)
    {
        foreach (string name in RootFiles)
        {
            string path = Path.Combine(root, name);
            if (File.Exists(path)) yield return path;
        }
        foreach (string platform in PlatformDirectories)
        {
            string directory = Path.Combine(root, platform);
            if (!Directory.Exists(directory)) continue;
            IEnumerable<string> found;
            try
            {
                found = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (string file in found) yield return file;
        }
    }

    private static bool IsProfilePath(string relativePath, int profileId) =>
        relativePath.Split('/').Contains($"profile{profileId}");

    /// <summary>
    /// The profile directory the run will use, resolved from the same <c>profile.save</c> the game reads
    /// (<see cref="MegaCrit.Sts2.Core.Saves.ProfileSave.LastProfileId"/>, default 1 for a new save) with the same rule as
    /// <see cref="TestProfileScope.ResolveTestProfileId"/>. Null (exclude nothing) if the copies disagree or cannot be read,
    /// so the guard never skips a directory that the comparison would not ignore.
    /// </summary>
    public static int? ResolveIsolatedTestProfileId(int requested)
    {
        var originals = new HashSet<int>();
        foreach (string file in Files(OS.GetUserDataDir()))
        {
            if (Path.GetFileName(file) != "profile.save") continue;
            if (TryReadLastProfileId(file) is int original) originals.Add(original);
        }

        int active;
        if (originals.Count == 0) active = 1;
        else if (originals.Count > 1) return null;
        else active = originals.First();

        return TestProfileScope.ResolveTestProfileId(requested, active);
    }

    private static int? TryReadLastProfileId(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("last_profile_id", out JsonElement value) && value.TryGetInt32(out int id)
                ? id
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string Hash(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unreadable:" + ex.GetType().Name;
        }
    }

    /// <summary>
    /// Paths that differ between the snapshots, outside the test profile's directory. <paramref name="ignored"/> counts the
    /// differing paths inside it.
    /// </summary>
    public static List<string> Changes(UserDataSnapshot before, UserDataSnapshot after, int testProfileId, out int ignored)
    {
        string testDirectory = $"profile{testProfileId}";
        var changes = new List<string>();
        ignored = 0;
        foreach (string path in before.Hashes.Keys.Union(after.Hashes.Keys, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
        {
            bool inBefore = before.Hashes.TryGetValue(path, out string? was);
            bool inAfter = after.Hashes.TryGetValue(path, out string? now);
            if (inBefore && inAfter && was == now) continue;
            if (path.Split('/').Contains(testDirectory))
            {
                ignored++;
                continue;
            }
            changes.Add(!inBefore ? $"added: {path}" : !inAfter ? $"removed: {path}" : $"changed: {path}");
        }
        return changes;
    }
}
