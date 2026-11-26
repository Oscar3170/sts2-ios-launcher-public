using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;

namespace STS2Mobile.GameHost;

internal static class PortraitActArt
{
    private static readonly Dictionary<string, Texture2D?> ActRestTextures = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> ActRestTexturePaths = new(StringComparer.Ordinal)
    {
        ["overgrowth"] = "res://images/rooms/rest_sites/overgrowth/overgrowth_rest_site_bg.png",
        ["underdocks"] = "res://images/rooms/rest_sites/underdocks/underdocks_rest_site_bg.png",
        ["hive"] = "res://images/rooms/rest_sites/hive/hive_rest_site_00.png",
        ["glory"] = "res://images/rooms/rest_sites/glory/glory_rest_site_00.png",
    };

    /// <summary>
    /// The current act's rest-site illustration for room covers (loaded once per act). Null when the act or
    /// its art is not known yet, so the caller can retry rather than leave a blank.
    /// </summary>
    public static Texture2D? RestTexture()
    {
        string? act = CurrentActName();
        if (string.IsNullOrEmpty(act)) return null;
        if (ActRestTextures.TryGetValue(act!, out Texture2D? cached)) return cached;
        if (!ActRestTexturePaths.TryGetValue(act!, out string? path) || !ResourceLoader.Exists(path)) return null;
        Texture2D? texture = ResourceLoader.Load<Texture2D>(path);
        if (texture != null) ActRestTextures[act!] = texture;
        return texture;
    }

    public static string? CurrentActName()
    {
        try
        {
            Assembly game = typeof(MegaCrit.Sts2.Core.Nodes.NGame).Assembly;
            Type? manager = game.GetType("MegaCrit.Sts2.Core.Runs.RunManager");
            object? instance = manager?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            object? state = manager?.GetMethod("DebugOnlyGetState")?.Invoke(instance, null);
            object? act = state?.GetType().GetProperty("Act")?.GetValue(state);
            return act?.GetType().Name.ToLowerInvariant();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
