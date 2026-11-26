using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace STS2Mobile.GameHost;

/// <summary>
/// Loads the combat's one-shot resources before the frames that would otherwise load them.
/// <para>The game preloads an act's assets as sets, but several things a fight uses are loaded lazily on their first use and
/// are not in those sets: the card pool's frame material and energy sprite font (loaded by the first card visual of every
/// room), a power's big icon (the power flash and the hover tip), orb visuals and their VFX and sounds, the card hover-tip
/// scene and the card-deal sound. Every room load also unloads what the game calls its missed cache, so those loads happen
/// again in each fight. On the phone each one is a main-thread resource load inside the draw or the enemy turn (the device
/// log of 2026-11-21 shows 100-200 ms hitches on them).</para>
/// <para>This class collects those paths from the live fight (the player's whole deck, the powers on the starting creatures,
/// every power icon the game can show, the orb assets when the character has an orb queue, the debug audio the game plays)
/// and hands them to the game's own <c>AssetLoadingSession</c>: the same threaded loader its loading screens use, which
/// inserts into <c>AssetCache._cache</c>, so a later <c>GetAsset</c> finds the resource and never reports it as uncached.
/// Nothing is drawn or instantiated and no game state is touched; the assets are ordinary cached resources, and the start
/// and finish lines report how much video memory the warm-up actually added.</para>
/// </summary>
internal sealed class CombatAssetWarmup
{
    private const string OrbIconDirectory = "res://images/orbs";
    private const string OrbVisualsDirectory = "res://scenes/orbs/orb_visuals";
    private const string OrbVfxDirectory = "res://scenes/vfx/orbs";
    private const string SfxDirectory = "res://debug_audio";
    private const string HoverTipScene = "res://scenes/ui/card_hover_tip.tscn";
    private const string FoolSymbolScene = "res://scenes/combat/fool_combat_symbol.tscn";
    private const string KiVfxScene = "res://scenes/vfx/fool/ki_vfx.tscn";
    // The room's creatures and their powers arrive within a few frames of the room; a short wait gets them into the set
    // while the fight is still on its entrance.
    private const int RosterWaitFrames = 12;
    private const int MaxPaths = 1600;

    private readonly List<string> _paths = new(512);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private object? _session;
    private MethodInfo? _process;
    private PropertyInfo? _completed;
    private ulong _roomId;
    private int _frames;
    private bool _started;
    private bool _reported;
    private bool _failed;
    private long _vramBefore;
    private int _cardPaths, _creaturePaths, _iconPaths, _orbPaths, _sfxPaths, _scenePaths, _folderPaths;

    /// <summary>
    /// Called every process frame of a combat room. Starts the load once the room's roster is there (or after a short wait),
    /// then pumps the game's loading session until it finishes; a room change starts over.
    /// </summary>
    public void Tick(Control room, Control? hand, PortraitCombatLayout.CombatReflection refl)
    {
        if (_failed)
            return;
        ulong roomId = room.GetInstanceId();
        if (roomId != _roomId)
        {
            _roomId = roomId;
            _paths.Clear();
            _seen.Clear();
            _session = null;
            _frames = 0;
            _started = false;
            _reported = false;
            _cardPaths = _creaturePaths = _iconPaths = _orbPaths = _sfxPaths = _scenePaths = _folderPaths = 0;
        }
        if (_session != null)
        {
            try
            {
                if (_completed?.GetValue(_session) is true)
                {
                    if (!_reported)
                        Report();
                    return;
                }
                _process?.Invoke(_session, null);
            }
            catch (Exception error)
            {
                Fail(error);
            }
            return;
        }
        if (_started || hand == null)
            return;
        _frames++;
        List<Control> creatures = refl.RoomCreatures(room);
        if (creatures.Count == 0 && _frames < RosterWaitFrames)
            return;
        try
        {
            CollectPaths(room, hand, refl, creatures);
            if (_paths.Count == 0)
                return;
            _vramBefore = (long)Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed);
            if (!StartSession())
            {
                _failed = true;
                return;
            }
            _started = true;
            GD.Print($"[stats] warmup start paths={_paths.Count} cards={_cardPaths} powers={_creaturePaths} " +
                     $"icons={_iconPaths} orbs={_orbPaths} sfx={_sfxPaths} scenes={_scenePaths} folders={_folderPaths} " +
                     $"vram_mb={_vramBefore / 1048576}");
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    /// <summary>
    /// The fight or portrait session is over: drop the loading session and everything collected for it, so the next fight
    /// starts clean. The assets it already loaded stay in the game's cache; a failure is kept, so a broken warm-up is not
    /// retried every room.
    /// </summary>
    public void Stop()
    {
        _session = null;
        _paths.Clear();
        _seen.Clear();
        _roomId = 0;
        _frames = 0;
        _started = false;
        _reported = false;
    }

    private void CollectPaths(Control room, Control hand, PortraitCombatLayout.CombatReflection refl, List<Control> creatures)
    {
        // 1. The deck: every card's pool frame material and energy sprite font, its banner material and atlas sprites.
        var cardPaths = new List<string>(64);
        foreach (object model in refl.CombatCardModels(hand))
            refl.AddCardVisualPaths(model, cardPaths);
        foreach (string path in cardPaths)
            _cardPaths += Add(path) ? 1 : 0;
        // 2. Every power on the creatures the fight starts with: the big icon the flash and the hover tip use, and the atlas icon.
        var creaturePaths = new List<string>(32);
        foreach (Control creature in creatures)
            refl.AddCreaturePowerPaths(creature, creaturePaths);
        foreach (string path in creaturePaths)
            _creaturePaths += Add(path) ? 1 : 0;
        // 3. Every power icon the game can show: a status applied mid-fight would otherwise load its icon then.
        var iconPaths = new List<string>(640);
        AddAllPowerIconPaths(iconPaths);
        foreach (string path in iconPaths)
            _iconPaths += AddVerified(path) ? 1 : 0;
        // 4. The scenes and sounds the combat UI and the draw load on first use.
        _scenePaths += Add(HoverTipScene) ? 1 : 0;
        _scenePaths += Add(FoolSymbolScene) ? 1 : 0;
        _scenePaths += Add(KiVfxScene) ? 1 : 0;
        foreach (string path in DebugAudioPaths())
            _sfxPaths += Add(path) ? 1 : 0;
        // 5. Orbs, only for a character whose fight can channel or evoke one (the VFX and sound folders hold the orb's own).
        if (refl.AnyPlayerHasOrbs(room))
        {
            foreach (string path in DirectoryFiles(OrbIconDirectory, ".png", recursive: false))
                _orbPaths += Add(path) ? 1 : 0;
            foreach (string path in DirectoryFiles(OrbVisualsDirectory, ".tscn", recursive: false))
                _orbPaths += Add(path) ? 1 : 0;
            foreach (string path in DirectoryFiles(OrbVfxDirectory, ".tscn", recursive: true))
                _orbPaths += Add(path) ? 1 : 0;
            foreach (string path in DirectoryFiles(SfxDirectory, "", recursive: false))
                if (path.Contains("_orb_", StringComparison.Ordinal))
                    _orbPaths += Add(path) ? 1 : 0;
        }
        // 6. Any other power icon the folder lists (a mod's, or a path the model list did not name).
        foreach (string path in DirectoryFiles("res://images/powers", ".png", recursive: false))
            _folderPaths += Add(path) ? 1 : 0;
    }

    /// <summary>Adds a path once, and only when the game's loader can resolve it.</summary>
    private bool Add(string? path)
    {
        if (!Usable(path))
            return false;
        if (!ResourceLoader.Exists(path!))
            return false;
        return Store(path!);
    }

    /// <summary>Adds a path the game's own models produced (they already resolved it with <c>ResourceLoader.Exists</c>).</summary>
    private bool AddVerified(string? path) => Usable(path) && Store(path!);

    private bool Usable(string? path) =>
        _paths.Count < MaxPaths && !string.IsNullOrEmpty(path) && path!.StartsWith("res://", StringComparison.Ordinal) &&
        !IsAtlasSprite(path);

    /// <summary>
    /// An atlas sprite (<c>res://images/atlases/&lt;atlas&gt;.sprites/&lt;name&gt;.tres</c>) is made by the game's own C#
    /// <c>AtlasResourceLoader</c>. Loaded on the session's worker threads, many of them at once raced in the engine's C#
    /// binding of the objects they share: one worker set up a binding inside <c>ResourceFormatLoader::load</c> while another
    /// released the same object at the end of <c>ResourceLoader::_run_load_task</c>, and the release's binding callback read a
    /// null pointer (device crash reports of 2026-11-26, at the start of two fights in a row). These stay with the game, which
    /// loads each on its first use.
    /// </summary>
    private static bool IsAtlasSprite(string path) =>
        path.StartsWith("res://images/atlases/", StringComparison.Ordinal) && path.Contains(".sprites/", StringComparison.Ordinal);

    private bool Store(string path)
    {
        if (!_seen.Add(path))
            return false;
        _paths.Add(path);
        return true;
    }

    /// <summary>
    /// Every power's big icon and atlas icon, from the game's own model database: the big icon is what the power flash and the
    /// hover tip draw, so a status that appears mid-fight would load it in the fight otherwise.
    /// </summary>
    private static void AddAllPowerIconPaths(List<string> paths)
    {
        try
        {
            Assembly sts2 = typeof(NGame).Assembly;
            const BindingFlags StaticAll = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type? modelDb = sts2.GetType("MegaCrit.Sts2.Core.Models.ModelDb");
            if (modelDb?.GetProperty("AllPowers", StaticAll)?.GetValue(null) is not IEnumerable powers)
                return;
            Type? powerModel = sts2.GetType("MegaCrit.Sts2.Core.Models.PowerModel");
            PropertyInfo? resolved = powerModel?.GetProperty("ResolvedBigIconPath", InstAll);
            PropertyInfo? big = powerModel?.GetProperty("BigIconPath", InstAll);
            PropertyInfo? packed = powerModel?.GetProperty("PackedIconPath", InstAll);
            foreach (object? power in powers)
            {
                if (power == null)
                    continue;
                // ResolvedBigIconPath is the path the game's own BigIcon loads (it prefers the beta art when the main is
                // absent); it caches the resolution per model.
                if ((resolved?.GetValue(power) as string ?? big?.GetValue(power) as string) is { Length: > 0 } icon)
                    paths.Add(icon);
                if (packed?.GetValue(power) is string atlas)
                    paths.Add(atlas);
            }
        }
        catch (Exception)
        {
            // The folder listing below still names the icons; a failed model walk only loses its precision.
        }
    }

    /// <summary>The sound files the game's debug audio manager can play (<c>TmpSfx.assetPaths</c>), which is what the draw plays.</summary>
    private static List<string> DebugAudioPaths()
    {
        var paths = new List<string>(48);
        try
        {
            Type? tmpSfx = typeof(NGame).Assembly.GetType("MegaCrit.Sts2.Core.Audio.Debug.TmpSfx");
            const BindingFlags StaticAll = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            if (tmpSfx?.GetProperty("assetPaths", StaticAll)?.GetValue(null) is IEnumerable assets)
                foreach (object? path in assets)
                    if (path is string text)
                        paths.Add(text);
        }
        catch (Exception)
        {
            // The list is an optimization; without it the sounds load on their first play.
        }
        return paths;
    }

    /// <summary>
    /// Files of one resource directory. A texture that the pack carries only as its import remap lists as
    /// <c>name.png.import</c>, whose resource path is the name without the suffix.
    /// </summary>
    private static List<string> DirectoryFiles(string directory, string suffix, bool recursive)
    {
        var files = new List<string>(64);
        DirAccess? access = DirAccess.Open(directory);
        if (access == null)
            return files;
        foreach (string name in access.GetFiles())
        {
            if (suffix.Length == 0 ? !IsResourceName(name) : !Matches(name, suffix))
                continue;
            files.Add(ResourceName(directory, name));
        }
        if (recursive)
            foreach (string child in access.GetDirectories())
                files.AddRange(DirectoryFiles(directory + "/" + child, suffix, recursive));
        return files;
    }

    private static bool Matches(string name, string suffix) =>
        name.EndsWith(suffix, StringComparison.Ordinal) ||
        (name.EndsWith(".import", StringComparison.Ordinal) && name.AsSpan(0, name.Length - ".import".Length).EndsWith(suffix, StringComparison.Ordinal));

    private static bool IsResourceName(string name) =>
        name.EndsWith(".png", StringComparison.Ordinal) || name.EndsWith(".tres", StringComparison.Ordinal) ||
        name.EndsWith(".tscn", StringComparison.Ordinal) || name.EndsWith(".mp3", StringComparison.Ordinal) ||
        name.EndsWith(".wav", StringComparison.Ordinal);

    private static string ResourceName(string directory, string name) =>
        directory + "/" + (name.EndsWith(".import", StringComparison.Ordinal) ? name[..^".import".Length] : name);

    /// <summary>
    /// Hands the paths to the game's own <c>AssetLoadingSession</c>, which loads them on its worker threads and inserts them
    /// into <c>AssetCache._cache</c> on the frames <see cref="Tick"/> pumps it. That is the cache the game's
    /// <c>GetAsset</c> reads, so a later use is a hit; it is also the path the game's own preload sets take, so no asset is
    /// reported as uncached.
    /// </summary>
    private bool StartSession()
    {
        Assembly sts2 = typeof(NGame).Assembly;
        const BindingFlags StaticAll = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags InstAll = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type? preloadType = sts2.GetType("MegaCrit.Sts2.Core.Assets.PreloadManager");
        object? assetCache = preloadType?.GetProperty("Cache", StaticAll)?.GetValue(null);
        object? cache = assetCache?.GetType().GetField("_cache", InstAll)?.GetValue(assetCache);
        Type? sessionType = sts2.GetType("MegaCrit.Sts2.Core.Assets.AssetLoadingSession");
        if (assetCache == null || cache == null || sessionType == null)
            return false;
        ConstructorInfo? constructor = null;
        foreach (ConstructorInfo candidate in sessionType.GetConstructors(InstAll))
        {
            if (candidate.GetParameters().Length == 4)
            {
                constructor = candidate;
                break;
            }
        }
        if (constructor == null)
            return false;
        _session = constructor.Invoke(["CombatWarmup", _paths, cache, assetCache]);
        _process = sessionType.GetMethod("Process", InstAll);
        _completed = sessionType.GetProperty("IsCompleted", InstAll);
        return _session != null && _process != null && _completed != null;
    }

    private void Report()
    {
        _reported = true;
        long vram = (long)Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed);
        GD.Print($"[stats] warmup done paths={_paths.Count} added_mb={(vram - _vramBefore) / 1048576.0:F1} " +
                 $"vram_mb={vram / 1048576.0:F0}");
    }

    private void Fail(Exception error)
    {
        _failed = true;
        _session = null;
        GD.PrintErr("Combat asset warmup stopped: ", error.GetType().Name, ": ", error.Message);
    }
}
