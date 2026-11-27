using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.AutoSlay;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.AutoSlay.Launchers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The host-neutral part of <see cref="TestDriver"/>: the services every scenario uses (tree, report, screenshots, pointer,
/// cancellation), the run start and combat entry, and the waits, captures and finds. It references no TouchSpireUI type, so
/// it is compiled into both TouchSpire.TestHarness and TouchSpire.LauncherHost.
/// <para>
/// Behaviour that depends on the host's own portrait layout is declared here as a partial method and implemented in a file
/// that only one host compiles: the TouchSpire layout assertions in HostLayoutChecks.cs, the launcher's tap-then-Choose input
/// and combat-stage run in TouchSpire.LauncherHost. A <c>partial void</c> with no implementation is dropped by the compiler,
/// so the launcher still runs the behaviour checks without the TouchSpire geometry ones.
/// </para>
/// </summary>
internal sealed partial class TestDriver
{
    private static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MediumWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongWait = TimeSpan.FromMinutes(3);

    // The game's own deferred main-menu load: NGame.LoadDeferredStartupAssetsAsync awaits
    // PreloadManager.LoadCommonAndMainMenuAssets(), which runs one AssetLoadingSession named "Common" through NAssetLoader.
    // That session is the state that means "safe to start a run"; a fixed sleep is both longer than the 4-6 s the load
    // usually takes and still racy when it is slower. The completion test is the game's own data: every Common/main-menu
    // asset is in PreloadManager.Cache and no session is active.
    private const string MainMenuPreloadSession = "Common";
    private static readonly TimeSpan MainMenuPreloadTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MainMenuPreloadStartWarning = TimeSpan.FromSeconds(5);
    private static readonly FieldInfo? AssetLoaderInstance = AccessTools.Field(typeof(NAssetLoader), "_instance");
    private static readonly FieldInfo? AssetLoaderSessions = AccessTools.Field(typeof(NAssetLoader), "_sessions");
    private static readonly FieldInfo? AssetLoaderCurrentSession = AccessTools.Field(typeof(NAssetLoader), "_currentSession");
    private static readonly FieldInfo? AssetSessionName = AccessTools.Field(typeof(AssetLoadingSession), "_name");

    // Native animation state for the condition waits below. The fields are private (found by name, so both hosts work), and
    // only their running/not-running bit is read: the checks still read the same settled state they read after a fixed sleep.
    private static readonly FieldInfo? CreatureScaleTween = AccessTools.Field(typeof(NCreature), "_scaleTween");
    private static readonly FieldInfo? CreatureShakeTween = AccessTools.Field(typeof(NCreature), "_shakeTween");
    private static readonly FieldInfo? CreatureIntentTween = AccessTools.Field(typeof(NCreature), "_intentFadeTween");
    private static readonly FieldInfo? HandAnimInTween = AccessTools.Field(typeof(NPlayerHand), "_animInTween");
    private static readonly FieldInfo? HandAnimOutTween = AccessTools.Field(typeof(NPlayerHand), "_animOutTween");
    private static readonly FieldInfo? HandAnimEnableTween = AccessTools.Field(typeof(NPlayerHand), "_animEnableTween");
    private static readonly FieldInfo? HandSelectedScaleTween = AccessTools.Field(typeof(NPlayerHand), "_selectedCardScaleTween");
    private static readonly FieldInfo? HolderHoveredField = AccessTools.Field(typeof(NCardHolder), "_isHovered");
    // Hand holders converge to their layout targets with per-frame Lerps, not tweens: read the targets and the game's
    // own snap thresholds. Selected/preview holders use _hoverTween and NSelectedHandCardHolder._tween.
    private static readonly FieldInfo? HolderTargetScaleField = AccessTools.Field(typeof(NHandCardHolder), "_targetScale");
    private static readonly FieldInfo? HolderHoverTweenField = AccessTools.Field(typeof(NCardHolder), "_hoverTween");
    private static readonly FieldInfo? SelectedHolderTweenField = AccessTools.Field(typeof(NSelectedHandCardHolder), "_tween");
    // The combat-start health bar fades in after a 1.3-1.7 s random delay; its running bit is what the intro settle needs.
    private static readonly FieldInfo? StateShowHideTween =
        AccessTools.Field(typeof(NCreatureStateDisplay), "_showHideTween");

    private readonly SceneTree _tree;
    private readonly TestOptions _options;
    private readonly TestReport _report;
    private readonly ScreenshotService _screenshots;
    private readonly VirtualPointer _pointer;
    private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromMinutes(6));

    private TestDriver(SceneTree tree, TestOptions options)
        : this(tree, options,
            new TestReport(options.Scenario, options.PointerMode.ToString().ToLowerInvariant(), options.Seed),
            new ScreenshotService(tree, options.OutputDirectory),
            new VirtualPointer(tree, options.PointerMode))
    {
    }

    internal TestDriver(SceneTree tree, TestOptions options, TestReport report, ScreenshotService screenshots, VirtualPointer pointer)
    {
        _tree = tree;
        _options = options;
        if (options.Scenario.StartsWith("tour-", StringComparison.Ordinal)) _cancellation.CancelAfter(TimeSpan.FromMinutes(20));
        _report = report;
        _screenshots = screenshots;
        _pointer = pointer;
    }

    /// <summary>
    /// Runs one of the scenarios that both hosts share, on a host that owns the run itself (the launcher host). The caller
    /// has already waited for the main menu and keeps its own report, screenshots and pointer.
    /// </summary>
    internal static async Task RunLinkedScenarioAsync(TestOptions options, TestReport report, ScreenshotService screenshots,
        VirtualPointer pointer)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Log.Error("Test harness requires a SceneTree.");
            return;
        }

        await new TestDriver(tree, options, report, screenshots, pointer).RunLinkedScenarioInternalAsync();
    }

    private async Task RunLinkedScenarioInternalAsync()
    {
        // The game keeps loading common/menu assets after the main menu appears; wait for that real preload instead of a
        // fixed 10 s sleep, which is both slower than needed and still racy on a slow load ("Invalid Task ID").
        await WaitForMainMenuPreloadAsync();

        RunState state = await StartRunAsync();
        if (_options.Scenario.StartsWith("tour-", StringComparison.Ordinal))
        {
            await RunTourAsync(state);
            return;
        }
        switch (_options.Scenario)
        {
            case "map":
                await RunMapScenarioAsync(state);
                break;
            case "map-history":
                await RunMapHistoryAsync(state);
                break;
            case "event":
                await RunEventScenarioAsync(state);
                break;
            case "event-popup":
                await RunEventPopupAsync(state);
                break;
            case "events-tight":
                await RunEventsTightAsync(state);
                break;
            case "ancient":
                await RunAncientScenarioAsync();
                break;
            case "ancient-pael":
                await RunAncientPaelAsync(state);
                break;
            case "ancient-art-tour":
                await RunAncientArtTourAsync(state);
                break;
            case "ancient-dialogue":
                await RunAncientDialogueScenarioAsync();
                break;
            case "potion-reward":
                await RunPotionRewardAsync(state);
                break;
            case "orbs-ovicopter":
                await RunOvicopterOrbsAsync(state);
                break;
            case "enemy-order":
                await RunEnemyOrderAsync(state);
                break;
            case "decimillipede":
                await RunDecimillipedeAsync(state);
                break;
            case "hand-rows":
                await RunHandRowsAsync(state);
                break;
            case "hand-selection":
                await RunHandSelectionAsync(state);
                break;
            case "regent-stars":
                await RunRegentStarsAsync(state);
                break;
            case "combat-piles":
                await RunCombatPilesAsync(state);
                break;
            case "status-scaling":
                await RunStatusScalingAsync(state);
                break;
            case "header-inventory":
                await RunHeaderInventoryAsync(state);
                break;
            case "header-indicators":
                await RunHeaderIndicatorsAsync(state);
                break;
            case "potions":
                await RunPotionPopupAsync(state);
                break;
            case "orbs":
                await RunOrbsAsync(state);
                break;
            case "shop":
            case "shop-courier":
            case "weirdshop":
                await RunShopScenarioAsync(state);
                break;
            case "shop-states":
                await RunShopStatesAsync(state);
                break;
            case "rewards":
                await RunRewardsScenarioAsync(state);
                break;
            case "rewards-variants":
                await RunRewardsVariantsAsync(state);
                break;
            case "rest":
                await RunRestScenarioAsync(state);
                break;
            case "rest-options":
                await RunRestOptionsAsync(state);
                break;
            case "rest-tour":
                await RunNativeRestArtAsync(state);
                break;
            case "screens":
                await RunScreensScenarioAsync(state);
                break;
            case "treasure":
                await RunTreasureAsync(state);
                break;
            case "deck-view":
                await RunDeckViewAsync(state);
                break;
            case "upgrade":
                await RunUpgradeScenarioAsync(state);
                break;
            case "enchant":
                await RunEnchantAsync(state);
                break;
            case "transform":
                await RunTransformAsync(state);
                break;
            case "blitz":
                await RunBlitzAsync(state);
                break;
            case "pause-menu":
                await RunPauseMenuAsync();
                break;
            case "settings-deep":
                await RunSettingsDeepAsync(state);
                break;
            case "capstone-over-hand":
                await RunCapstoneOverHandAsync(state);
                break;
            case "game-over":
            case "game-over-win":
            case "game-over-boss":
                await RunGameOverAsync(state, _options.Scenario == "game-over-win", _options.Scenario == "game-over-boss");
                break;
            case "game-over-room":
                await RunGameOverRoomAsync(state);
                break;
            case "game-over-boss-win":
                await RunGameOverBossWinAsync();
                break;
            default:
                throw new ArgumentException($"'{_options.Scenario}' is not a shared harness scenario.");
        }
    }

    private async Task<RunState> StartRunAsync()
    {
        NMainMenu menu = NGame.Instance?.MainMenu
            ?? throw new InvalidOperationException("Main menu disappeared before run start.");

        SaveManager.Instance.PrefsSave.FastMode = FastModeType.Fast;
        SaveManager.Instance.PrefsSave.IsLongPressEnabled = false;
        SaveManager.Instance.SetFtuesEnabled(false);

        if (AutoSlayer.IsActive)
        {
            throw new InvalidOperationException("AutoSlay is active; launch the test without --autoslay.");
        }

        NGame.Instance!.DebugSeedOverride = _options.Seed;
        var rng = new Rng(StringHelper.GetDeterministicHashCode(_options.Seed));

        Log.Info("[TEST] starting run through the normal menu flow");
        if (_options.Scenario == "header-indicators")
            await LaunchHeaderIndicatorsRunAsync(menu);
        else if (_options.Scenario == "blitz")
        {
            new MegaCrit.Sts2.Core.DevConsole.ConsoleCommands.UnlockConsoleCmd().Process(null, new[] { "all" });
            SaveManager.Instance.MarkFtueAsComplete("ascension_singleplayer_ftue");
            SaveManager.Instance.MarkFtueAsComplete("blitz_disclaimer");
            await new BlitzRunLauncher().LaunchAsync(menu, rng, _options.Seed, _cancellation.Token);
        }
        else if (_options.Scenario is "orbs" or "orbs-ovicopter")
            await LaunchCharacterFixtureAsync<MegaCrit.Sts2.Core.Models.Characters.Defect>(menu);
        else if (_options.Scenario == "regent-stars")
            await LaunchCharacterFixtureAsync<MegaCrit.Sts2.Core.Models.Characters.Regent>(menu);
        else if (_options.Scenario == "stage-osty")
            await LaunchCharacterFixtureAsync<MegaCrit.Sts2.Core.Models.Characters.Necrobinder>(menu);
        else await new StandardRunLauncher().LaunchAsync(menu, rng, _options.Seed, _cancellation.Token);

        await WaitUntilAsync(
            () => RunManager.Instance.DebugOnlyGetState() is not null
                  && NGame.Instance?.CurrentRunNode is not null,
            "run state",
            LongWait);

        RunState state = RunManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Run state was null after starting the run.");

        _report.Pass("run_started", $"seed={state.Rng.StringSeed}");
        _report.Phase("run_started", $"seed={state.Rng.StringSeed}");

        // StandardRunLauncher returns as soon as Embark is clicked. The act
        // map and NRun scene can still be initializing at that point; entering
        // a debug room during that window creates the combat node without
        // making it the current room.
        await WaitUntilAsync(
            () => NRun.Instance is { } run
                  && GodotObject.IsInstanceValid(run)
                  && run.IsInsideTree()
                  && run.GlobalUi is not null
                  && run.GetNodeOrNull<Control>("%RoomContainer") is not null,
            "run scene initialization",
            MediumWait);
        await WaitSecondsAsync(2);

        await WaitFramesAsync(4);
        return state;
    }

    private async Task LaunchCharacterFixtureAsync<T>(NMainMenu menu)
    {
        await UiHelper.Click(menu.GetNode<NButton>("MainMenuTextButtons/SingleplayerButton"));
        await WaitUntilAsync(() => menu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen")?.IsVisibleInTree() == true
            || menu.GetNodeOrNull<Control>("Submenus/SingleplayerSubmenu")?.IsVisibleInTree() == true, "character or standard submenu", ShortWait);
        if (menu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen")?.IsVisibleInTree() != true)
            await UiHelper.Click(menu.GetNode<NButton>("Submenus/SingleplayerSubmenu/%StandardButton"));
        await WaitUntilAsync(() => menu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen")?.IsVisibleInTree() == true,
            "Defect character fixture", ShortWait);
        Control screen = menu.GetNode<Control>("Submenus/CharacterSelectScreen");
        MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectButton character =
            FindAll<MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectButton>(screen).First(b => b.Character is T);
        // Test profile only: use the native unlock and selection controls.
        character.DebugUnlock();
        character.Select();
        await WaitFramesAsync(4);
        await UiHelper.Click(screen.GetNode<NButton>("ConfirmButton"));
    }

    private async Task<(int Index, ActModel Act)> ConfigureTourActAsync(RunState state, string actName)
    {
        AccessTools.Property(typeof(RunState), "UnlockState").SetValue(state, MegaCrit.Sts2.Core.Unlocks.UnlockState.all);
        ActModel Find(string name) => ModelDb.Acts.First(a => a.GetType().Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToMutable();
        // Overgrowth and Underdocks are alternative FIRST acts, not two
        // consecutive acts. A four-act fixture breaks native rest animations
        // (which only accept indices 0..2) and changes encounter/event rules.
        int index = actName.ToLowerInvariant() switch
        {
            "overgrowth" or "underdocks" => 0,
            "hive" => 1,
            "glory" => 2,
            _ => throw new ArgumentException("Unknown tour act: " + actName),
        };
        List<ActModel> acts = new() { Find(actName.Equals("underdocks", StringComparison.OrdinalIgnoreCase) ? "Underdocks" : "Overgrowth"), Find("Hive"), Find("Glory") };
        AccessTools.Property(typeof(RunState), "Acts").SetValue(state, acts);
        RunManager.Instance.GenerateRooms();
        await RunManager.Instance.SetActInternal(index);
        return (index, state.Act);
    }

    private async Task<NCombatRoom> EnterCombatAsync(EncounterModel? encounter = null)
    {
        // A live combat's Encounter is already a mutable model; ToMutable() on it throws ("used in incorrect place").
        // Resolve the canonical model by id (the id is copied by MemberwiseClone). Never fall back to another fight.
        EncounterModel? canonical = encounter;
        if (encounter is { IsMutable: true })
        {
            canonical = ModelDb.GetByIdOrNull<EncounterModel>(encounter.Id);
            if (canonical is null)
                throw new InvalidOperationException(
                    $"canonical encounter {encounter.Id} not found in ModelDb; refusing to enter a different fight");
        }
        Log.Info("[TEST] entering a debug monster room");
        await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Unassigned,
            canonical?.ToMutable(), showTransition: false);

        NCombatRoom? createdRoom = NCombatRoom.Instance;
        Log.Info($"[TEST] after EnterRoomDebug: nrun={NRun.Instance is not null} currentRoom={NRun.Instance?.CombatRoom is not null} instance={createdRoom is not null} ui={(createdRoom?.Ui is not null)} hand={(createdRoom?.Ui?.Hand is not null)}");

        try
        {
            await WaitUntilAsync(
                () => NCombatRoom.Instance is { } room
                      && GodotObject.IsInstanceValid(room)
                      && room.Ui?.Hand is { } hand
                      && GodotObject.IsInstanceValid(hand)
                      && hand.IsInsideTree(),
                "combat room",
                MediumWait);
        }
        catch
        {
            NCombatRoom? current = NCombatRoom.Instance;
            Log.Error($"[TEST] combat-room wait failed: nrun={NRun.Instance is not null} currentRoom={NRun.Instance?.CombatRoom is not null} instance={current is not null} valid={(current is not null && GodotObject.IsInstanceValid(current))} ui={(current?.Ui is not null)} hand={(current?.Ui?.Hand is not null)} inside={(current?.Ui?.Hand is not null && current.Ui.Hand.IsInsideTree())}");
            throw;
        }

        await WaitUntilAsync(IsPlayerPlayPhase, "player play phase", LongWait);
        await WaitUntilAsync(() => GetHand().ActiveHolders.Count > 0, "hand cards", ShortWait);
        await WaitFramesAsync(4);

        _report.Phase("combat_entered", $"hand={GetHand().ActiveHolders.Count}");
        return NCombatRoom.Instance ?? throw new InvalidOperationException("Combat room disappeared.");
    }

    private async Task<NPotionPopup> TapPotionAsync(NPotionHolder holder, string prefix)
    {
        await _pointer.TapAsync(CenterOf(holder));
        await WaitUntilAsync(() => LivePotionPopup() is { Modulate.A: >= 0.99f }, prefix + " native action panel", ShortWait);
        NPotionPopup popup = LivePotionPopup()!;
        await WaitUntilAsync(() => ((Tween?)AccessTools.Field(typeof(NPotionPopup), "_tween").GetValue(popup))?.IsRunning() != true,
            prefix + " action panel settled", ShortWait);
        _report.Pass(prefix + "_native_popup_created", $"tapped {holder.GetGlobalRect()}");
        // The popup's placement and touch targets are the host's own layout: TouchSpireUI on the TouchSpire host.
        CheckPotionPopupLayout(prefix, popup);
        return popup;
    }

    // Implemented by both hosts: popup placement inside the host's portrait band and safe frame, touch target sizes and
    // native tip clearance (TouchSpireUI in HostLayoutChecks.cs, the launcher in LauncherHeaderChecks.cs).
    partial void CheckPotionPopupLayout(string prefix, NPotionPopup popup);

    private static NPotionPopupButton Action(NPotionPopup popup, string name) => popup.GetNode<NPotionPopupButton>($"%{name}Button");

    private NPotionPopup? LivePotionPopup() => FindAll<NPotionPopup>(_tree.Root)
        .FirstOrDefault(p => p.IsVisibleInTree() && !p.IsMarkedForRemoval);

    private bool IsPlayerPlayPhase()
    {
        try
        {
            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            if (state == null || CombatManager.Instance.PlayerActionsDisabled)
            {
                return false;
            }

            Player? me = LocalContext.GetMe(state);
            return me?.PlayerCombatState?.Phase == PlayerTurnPhase.Play;
        }
        catch
        {
            return false;
        }
    }

    private NPlayerHand GetHand()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        return room?.Ui?.Hand ?? throw new InvalidOperationException("Player hand is not available.");
    }

    private Vector2 GetViewportSize()
    {
        return _tree.Root.GetViewport().GetVisibleRect().Size;
    }

    private static Vector2 CenterOf(Control control)
    {
        Rect2 rect = control.GetGlobalRect();
        if (rect.Size.LengthSquared() <= 0.01f)
        {
            return control.GlobalPosition;
        }

        return rect.GetCenter();
    }

    private async Task<string> CaptureAsync(string name)
    {
        string path = await _screenshots.CaptureAsync(name);
        _report.Screenshot(name, path);
        return path;
    }

    /// <summary>
    /// Waits for the game's deferred <c>Common</c> preload instead of sleeping 10 s. The predicate reads the game's own
    /// state (the preload cache plus the loader queue) rather than a shortened wall clock, so a slow load is awaited
    /// rather than raced and a fast load costs only the frames it takes to see the completion. Bounded by
    /// <see cref="MainMenuPreloadTimeout"/>: on expiry the scenario logs an error and continues, as the old sleep did.
    /// </summary>
    private async Task WaitForMainMenuPreloadAsync()
    {
        _report.Phase("main_menu_preload_wait");
        if (!PreloadManager.Enabled)
        {
            Log.Info("[TEST] main-menu preload skipped: asset preloading is disabled");
            _report.Phase("main_menu_preload_complete", "asset preloading is disabled");
            return;
        }

        var elapsed = Stopwatch.StartNew();
        bool sawCommon = false;
        bool warnedNotStarted = false;
        string state = "";
        while (true)
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            if (MainMenuPreloadCompleted(ref sawCommon, out state))
            {
                break;
            }

            if (!warnedNotStarted && !sawCommon && elapsed.Elapsed > MainMenuPreloadStartWarning)
            {
                warnedNotStarted = true;
                Log.Warn($"[TEST] no '{MainMenuPreloadSession}' preload session seen after " +
                         $"{MainMenuPreloadStartWarning.TotalSeconds:F0}s ({state}); still waiting");
            }

            if (elapsed.Elapsed > MainMenuPreloadTimeout)
            {
                Log.Error($"[TEST] main-menu preload unfinished after {MainMenuPreloadTimeout.TotalSeconds:F0}s ({state}); " +
                          "continuing so the scenario still reports");
                _report.Phase("main_menu_preload_timeout", state);
                return;
            }

            await WaitFramesAsync(1);
        }

        Log.Info($"[TEST] main-menu preload complete in {elapsed.ElapsedMilliseconds}ms ({state})");
        _report.Phase("main_menu_preload_complete", $"{state} in {elapsed.ElapsedMilliseconds}ms");
    }

    private static bool MainMenuPreloadCompleted(ref bool sawCommon, out string state)
    {
        int active = 0;
        if (AssetLoaderInstance?.GetValue(null) is NAssetLoader loader)
        {
            if (AssetLoaderCurrentSession?.GetValue(loader) is AssetLoadingSession { IsCompleted: false } current)
            {
                active++;
                sawCommon |= IsCommonSession(current);
            }

            if (AssetLoaderSessions?.GetValue(loader) is System.Collections.IEnumerable queued)
            {
                foreach (object? entry in queued)
                {
                    if (entry is not AssetLoadingSession { IsCompleted: false } session) continue;
                    active++;
                    sawCommon |= IsCommonSession(session);
                }
            }
        }

        IReadOnlySet<string> loaded = PreloadManager.Cache.GetLoadedCacheAssets();
        int total = 0, missing = 0;
        string? firstMissing = null;
        foreach (string path in AssetSets.CommonAssets)
        {
            total++;
            if (loaded.Contains(path)) continue;
            missing++;
            firstMissing ??= path;
        }

        foreach (string path in AssetSets.MainMenuSet)
        {
            total++;
            if (loaded.Contains(path)) continue;
            missing++;
            firstMissing ??= path;
        }

        state = $"assets={loaded.Count} common_missing={missing}/{total} active_sessions={active} common_seen={sawCommon}" +
                (firstMissing is null ? "" : $" first_missing={firstMissing}");
        return active == 0 && missing == 0;
    }

    private static bool IsCommonSession(AssetLoadingSession session) =>
        AssetSessionName?.GetValue(session) as string == MainMenuPreloadSession;

    /// <summary>
    /// Replaces a fixed settle with a wait for the game's own presentation state: returns as soon as
    /// <paramref name="sample"/> produces the same signature for <paramref name="stableFrames"/> consecutive comparisons.
    /// A null sample means "room/hand not ready"; a <see cref="Pending"/> sample names the native animation that is still
    /// running (signatures are <c>key=value|key=value</c>, so the fallback log can name the first field that changed).
    /// The old fixed wait (<paramref name="fallback"/>) is the bound; hitting it logs and continues as the sleep did.
    /// </summary>
    private async Task WaitUntilStableAsync(Func<string?> sample, string description, TimeSpan fallback,
        int stableFrames = 2)
    {
        var clock = Stopwatch.StartNew();
        string? stableSample = null;   // most recent non-pending signature
        string? previousSample = null; // the one before it, for the fallback diagnostic
        string? pending = null;
        int stable = 0;
        while (true)
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            // One sample per frame: without the yield three synchronous samples would look "stable" within one frame.
            await WaitFramesAsync(1);
            string? current = sample();
            if (current is null)
            {
                pending = "no signature (room or hand not ready)";
                stable = 0;
            }
            else if (IsPending(current))
            {
                pending = PendingReason(current);
                stable = 0;
            }
            else
            {
                pending = null;
                if (current == stableSample)
                {
                    stable++;
                }
                else
                {
                    stable = 0;
                    previousSample = stableSample;
                    stableSample = current;
                }
            }

            if (stable >= stableFrames)
            {
                Log.Info($"[TEST] settled {description} in {clock.ElapsedMilliseconds}ms " +
                         $"(old fixed wait {fallback.TotalMilliseconds:F0}ms){HandSettleBranch(stableSample)}");
                return;
            }

            if (clock.Elapsed >= fallback)
            {
                string cause = pending is not null
                    ? $"gated by {pending}"
                    : $"first differing field {FirstFieldDifference(previousSample, stableSample)}";
                Log.Warn($"[TEST] settle fallback: {description} after {fallback.TotalMilliseconds:F0}ms; {cause}");
                return;
            }
        }
    }

    private const string PendingMarker = "!pending:";

    /// <summary>Marks a sample as "a named native animation is still running"; never equal to a signature.</summary>
    private static string Pending(string reason) => string.Concat(PendingMarker, reason);

    private static bool IsPending(string sample) => sample.StartsWith(PendingMarker, StringComparison.Ordinal);

    private static string PendingReason(string sample) => sample[PendingMarker.Length..];

    /// <summary>The first <c>key=value</c> field that differs between the last two distinct signatures.</summary>
    private static string FirstFieldDifference(string? before, string? after)
    {
        if (after is null) return "<no signature sampled>";
        if (before is null) return $"<first signature> {Shorten(after)}";
        string[] left = before.Split('|');
        string[] right = after.Split('|');
        int count = Math.Max(left.Length, right.Length);
        for (int i = 0; i < count; i++)
        {
            string a = i < left.Length ? left[i] : "<missing>";
            string b = i < right.Length ? right[i] : "<missing>";
            if (a == b) continue;
            return $"{FieldName(a, b)}: {Shorten(a)} -> {Shorten(b)}";
        }
        return "<only trailing separators differ>";
    }

    private static string FieldName(string left, string right)
    {
        int index = left.IndexOf('=');
        if (index > 0) return left[..index];
        index = right.IndexOf('=');
        return index > 0 ? right[..index] : "field";
    }

    private static string Shorten(string text) => text.Length <= 96 ? text : text[..96] + "…";

    /// <summary>
    /// Names the branch that ended a hand settle when the settled signature carries <c>hand.native</c>: "native target" when
    /// every holder satisfied the game's own snap condition, "resting" when the portrait layout holds a holder away from its
    /// native target and the quantized Position/Scale/Rotation simply stopped changing.
    /// </summary>
    private static string HandSettleBranch(string? sample)
    {
        const string marker = "hand.native=";
        if (sample is null) return "";
        int start = sample.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return "";
        int valueStart = start + marker.Length;
        int end = sample.IndexOf('|', valueStart);
        string value = end < 0 ? sample[valueStart..] : sample[valueStart..end];
        return value switch
        {
            "True" => "; hand settle=native target",
            "False" => "; hand settle=resting (native target differs; cards not moving)",
            _ => "",
        };
    }

    /// <summary>
    /// Waits for a state that ends the previous phase (a card added to the hand, the next player turn). The old fixed wait
    /// is the bound; hitting it logs and continues exactly as the sleep did.
    /// </summary>
    private async Task<bool> WaitUntilSettledAsync(Func<bool> settled, string description, TimeSpan fallback)
    {
        var clock = Stopwatch.StartNew();
        while (!settled())
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            if (clock.Elapsed >= fallback)
            {
                Log.Warn($"[TEST] settle fallback: {description} not reached after {fallback.TotalMilliseconds:F0}ms; " +
                         "continuing as the fixed wait did");
                return false;
            }
            await WaitFramesAsync(1);
        }
        Log.Info($"[TEST] settled {description} in {clock.ElapsedMilliseconds}ms " +
                 $"(old fixed wait {fallback.TotalMilliseconds:F0}ms)");
        return true;
    }

    /// <summary>
    /// Rounds to fixed decimals, never to negative zero. Idle animation and transform propagation drift sub-pixel (measured
    /// as an origin moving 0.002 units over a whole encounter), which made an exact string signature never repeat; 0.1
    /// canvas units (0.05 px at 2x) still catches real layout motion while ignoring that noise. Culture-invariant so the
    /// signature cannot depend on the machine locale.
    /// </summary>
    private static string Q(float value, int decimals)
    {
        double rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        if (rounded == 0d) rounded = 0d;
        return rounded.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private static string Q(Vector2 value, int decimals) =>
        string.Concat(Q(value.X, decimals), ",", Q(value.Y, decimals));

    private static string Q(Rect2 value) =>
        string.Concat(Q(value.Position, 1), ";", Q(value.Size, 1));

    private static string Q(Transform2D value) =>
        string.Concat(Q(value.X, 2), ";", Q(value.Y, 2), ";", Q(value.Origin, 1));

    /// <summary>
    /// The hand's resting layout, labelled and quantized. Besides the geometry the checks read, each holder's own local
    /// Position/Scale/Rotation is included: those are the values the native Lerp loops change, so an unchanged signature
    /// means the cards are not moving, even if the portrait layout holds one away from its native target.
    /// </summary>
    private static string HandLayoutSignature(NPlayerHand hand)
    {
        var builder = new StringBuilder(256);
        builder.Append("hand.mode=").Append(hand.CurrentMode).Append('|')
            .Append("hand.inPlay=").Append(hand.InCardPlay).Append('|');
        foreach (NHandCardHolder holder in hand.ActiveHolders)
        {
            if (!GodotObject.IsInstanceValid(holder) || holder.IsQueuedForDeletion()) continue;
            string id = holder.GetInstanceId().ToString(CultureInfo.InvariantCulture);
            builder.Append("hand.").Append(id).Append(".rect=").Append(Q(holder.Hitbox.GetGlobalRect())).Append('|')
                .Append("hand.").Append(id).Append(".cardScale=")
                    .Append(Q(holder.CardNode?.Scale ?? Vector2.Zero, 3)).Append('|')
                .Append("hand.").Append(id).Append(".pos=").Append(Q(holder.Position, 1)).Append('|')
                .Append("hand.").Append(id).Append(".holderScale=").Append(Q(holder.Scale, 3)).Append('|')
                .Append("hand.").Append(id).Append(".rot=").Append(Q(holder.RotationDegrees, 2)).Append('|')
                .Append("hand.").Append(id).Append(".hover=").Append(HolderHovered(holder)).Append('|')
                .Append("hand.").Append(id).Append(".z=").Append(holder.ZIndex).Append('|');
        }
        return builder.ToString();
    }

    /// <summary>
    /// Pending only while a native hand show/hide/disable/selected-row tween is still running. Holder motion is left to the
    /// signature itself: a holder easing to a layout target changes its quantized Position/Scale/Rotation every frame, so the
    /// wait settles once the cards stop moving. This is deliberately a dual gate: on the tall (9:19.5) shape the portrait
    /// layout (HandStillness/PortraitCombatLayout) can hold a holder at a position that differs from the native
    /// <c>_targetPosition</c>, so the native snap condition never fires although nothing moves; "not moving" still ends the
    /// wait. The final <c>hand.native</c> field records which branch applies so the settled log can name it.
    /// </summary>
    private string? HandSettledSignature(NPlayerHand hand)
    {
        if (TweenRunning(hand, HandAnimInTween) || TweenRunning(hand, HandAnimOutTween)
            || TweenRunning(hand, HandAnimEnableTween) || TweenRunning(hand, HandSelectedScaleTween))
        {
            return Pending("hand.animTween");
        }

        bool nativeConverged = true;
        NHandCardHolder? focused = hand.FocusedHolder;
        bool focusedValid = focused is not null && GodotObject.IsInstanceValid(focused);
        foreach (NHandCardHolder holder in hand.ActiveHolders)
        {
            if (!GodotObject.IsInstanceValid(holder) || holder.IsQueuedForDeletion()) continue;
            if (HolderReachedTarget(holder, focusedValid && focused == holder)) continue;
            nativeConverged = false;
            break;
        }
        return string.Concat(HandLayoutSignature(hand), "hand.native=", nativeConverged ? "True|" : "False|");
    }

    /// <summary>
    /// The game snaps a holder when it is within 1 unit / 0.002 scale / 0.1 degree of its target. The focused holder is
    /// special-cased by RefreshLayout: its scale and angle are set instantly to One/0 and its target scale/angle are not
    /// updated, so only its position target applies.
    /// </summary>
    private static bool HolderReachedTarget(NHandCardHolder holder, bool focused)
    {
        if (holder.Position.DistanceSquaredTo(holder.TargetPosition) >= 1f) return false;
        if (focused) return true;
        if (MathF.Abs(holder.RotationDegrees - holder.TargetAngle) >= 0.1f) return false;
        return HolderTargetScaleField?.GetValue(holder) is not Vector2 target
               || (MathF.Abs(target.X - holder.Scale.X) < 0.002f && MathF.Abs(target.Y - holder.Scale.Y) < 0.002f);
    }

    private string? CurrentHandSettledSignature()
    {
        NPlayerHand? hand = NCombatRoom.Instance?.Ui?.Hand;
        return hand is null || !GodotObject.IsInstanceValid(hand) ? null : HandSettledSignature(hand);
    }

    /// <summary>
    /// The combat presentation's resting state: hand layout, every creature's root and health-bar placement, the native
    /// spawn/hurt/intent tweens and the action phase. Two equal samples mean the room has stopped moving, which is the state
    /// the old fixed settles were there to reach.
    /// </summary>
    private string? CombatSettledSignature() => SettledRoomSignature(includeHealthBars: true);

    private string? SettledRoomSignature(bool includeHealthBars, bool includeIntentFade = true)
    {
        NCombatRoom? room = NCombatRoom.Instance;
        if (room is null || !GodotObject.IsInstanceValid(room) || !room.IsInsideTree()) return null;
        NPlayerHand? hand = room.Ui?.Hand;
        if (hand is null || !GodotObject.IsInstanceValid(hand) || !hand.IsInsideTree()) return null;
        string? handSample = HandSettledSignature(hand);
        if (handSample is null || IsPending(handSample)) return handSample;

        var builder = new StringBuilder(handSample);
        builder.Append("combat.inProgress=").Append(CombatManager.Instance.IsInProgress).Append('|')
            .Append("combat.actionsDisabled=").Append(CombatManager.Instance.PlayerActionsDisabled).Append('|');
        foreach (NCreature creature in room.CreatureNodes)
        {
            if (!GodotObject.IsInstanceValid(creature) || creature.IsQueuedForDeletion()) continue;
            string id = creature.GetInstanceId().ToString(CultureInfo.InvariantCulture);
            // A dying creature's death animation is long and its geometry is excluded from every post-action check
            // (checks filter IsAlive), so its motion must not hold the room's settle open.
            if (creature.Entity.IsDead || creature.IsPlayingDeathAnimation)
            {
                builder.Append("c.").Append(id).Append(".dying=True|");
                continue;
            }
            // End-state conditions: a running entry/hurt/intent tween can plateau in value, which used to look "stable".
            if (TweenRunning(creature, CreatureScaleTween))
                return Pending($"c.{id}.scaleTween");
            if (TweenRunning(creature, CreatureShakeTween))
                return Pending($"c.{id}.shakeTween");
            if (includeIntentFade && TweenRunning(creature, CreatureIntentTween))
                return Pending($"c.{id}.intentTween");
            NCreatureStateDisplay? stateDisplay = null;
            if (includeHealthBars)
            {
                stateDisplay = creature.GetNodeOrNull<NCreatureStateDisplay>("HealthBar");
                if (stateDisplay is not null && TweenRunning(stateDisplay, StateShowHideTween))
                    return Pending($"c.{id}.hpTween");
            }
            builder.Append("c.").Append(id).Append(".xform=").Append(Q(creature.GetGlobalTransform())).Append('|');
            if (includeIntentFade && GodotObject.IsInstanceValid(creature.IntentContainer))
                builder.Append("c.").Append(id).Append(".intentA=")
                    .Append(Q(creature.IntentContainer.Modulate.A, 2)).Append('|');
            if (stateDisplay is not null)
                builder.Append("c.").Append(id).Append(".hpPos=").Append(Q(stateDisplay.Position, 1)).Append('|')
                    .Append("c.").Append(id).Append(".hpA=").Append(Q(stateDisplay.Modulate.A, 2)).Append('|');
        }
        return builder.ToString();
    }

    private static bool TweenRunning(GodotObject? owner, FieldInfo? field)
    {
        if (owner is null || field is null || !GodotObject.IsInstanceValid(owner)) return false;
        return field.GetValue(owner) is Tween tween && GodotObject.IsInstanceValid(tween) && tween.IsRunning();
    }

    private static bool HolderHovered(NCardHolder holder) =>
        HolderHoveredField?.GetValue(holder) is true;

    private async Task WaitFramesAsync(int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
        }
    }

    private async Task WaitSecondsAsync(double seconds)
    {
        _cancellation.Token.ThrowIfCancellationRequested();
        SceneTreeTimer timer = _tree.CreateTimer(seconds, processAlways: true);
        await _tree.ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
    }

    private async Task WaitUntilAsync(Func<bool> condition, string description, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            if (condition())
            {
                return;
            }

            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException($"Timed out waiting for {description} after {timeout.TotalSeconds:F0}s.");
            }

            await WaitFramesAsync(1);
        }
    }

    private static List<T> FindAll<T>(Node root) where T : Node
    {
        var result = new List<T>();
        if (root is null || !GodotObject.IsInstanceValid(root))
        {
            return result;
        }

        if (root is T match)
        {
            result.Add(match);
        }

        foreach (Node child in root.GetChildren())
        {
            result.AddRange(FindAll<T>(child));
        }

        return result;
    }

    /// <summary>The first node of <paramref name="root"/>'s subtree that is a <typeparamref name="T"/>, or null.</summary>
    private static T? FindFirst<T>(Node root) where T : Node
    {
        if (root is null || !GodotObject.IsInstanceValid(root))
        {
            return null;
        }

        if (root is T match)
        {
            return match;
        }

        foreach (Node child in root.GetChildren())
        {
            T? found = FindFirst<T>(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    // The host's portrait layout answers (Host/*.cs): choice-screen geometry, orb art clearance from enemy bodies and HUD.
    partial void CheckCombatChoiceControls(string name, NChooseACardSelectionScreen screen, IReadOnlyList<NGridCardHolder> holders);
    partial void CheckOrbEncounterClearance(string name, NCombatRoom room);

    // SafeAreaCanvas.IsPortrait and the launcher's stage band (LauncherStageChecks.cs).
    private partial bool IsPortraitHost();
    private partial Rect2 PortraitStageFrame();

    // Taps the card, checks the reticle, then presses the screen's Choose button (Host/HarnessHostInput.cs).
    private partial Task TakeChoiceCardAsync(NGridCardHolder holder);

    // Where the pointer rests after Ovicopter's turn so hover effects do not cover the capture (the viewport bottom).
    private partial Vector2 PointerParkingPosition();

    // The hand policy the hand-rows scenario draws against: the launcher's permanent two-row hand fills the upper row first
    // and holds up to ten (LauncherHandChecks.cs).
    private partial int HandRowsCardLimit();
    private partial int HandRowsUpperCount(int count);
    private partial float HandRowsRowTolerance();
    private partial float HandRowsRowY(NHandCardHolder holder);
    private partial float HandRowsLowerRowTolerance();
    private partial void CheckHandRowsLayout(int count, IReadOnlyList<NHandCardHolder> cards, NCombatRoom room);
    // hand-rows flips the launcher's Flat Hand setting per count and checks both modes; the hand-selection flow adds one
    // flat pass.
    private partial Task CheckHandRowsModesAsync(int count, IReadOnlyList<NHandCardHolder> cards, NCombatRoom room);
    private partial bool FlatHandSupported();
    private partial void SetFlatHandMode(bool flat);
    private partial void ResetFlatHandMode();

    // Where the Regent's native star counter sits on the combat rail.
    private partial void CheckRegentStarRail(string name, NCombatRoom room, NStarCounter counter, int initial);

    // Sweeps the native star count 0 -> 3 -> 10 to prove the badge, the orb and the centred digit do not move and that the
    // two-digit number stays centred.
    private partial Task CheckRegentStarCountsAsync(RunState state, NCombatRoom room, NStarCounter counter);

    // Measures the rendered star/energy ink and writes the 3x review crop.
    private partial Task CheckRegentStarDetailAsync(NCombatRoom room, NStarCounter counter);

    // The combat piles' rail placement and touch targets, and End Turn staying put across a turn.
    private partial Task CheckCombatPilesRailAsync(NCombatRoom room, Rect2[] before);
    private partial Task CheckEndTurnAfterNextTurnAsync(NCombatRoom room);
    // One extra turn draw at 30 fps with a 120 ms hitch after each card arrival (slow-device jump reproduction).
    private partial Task CheckSlowTurnDrawAsync(NCombatRoom room);
    // combat-piles samples the rail from the moment the combat room enters the tree until 1.5 s after the player's first
    // turn: every drawn frame is recorded and a control seen away from its settled rail position fails
    // (LauncherHandChecks.cs).
    private partial void BeginCombatRailProbe();
    private partial void MarkCombatRailProbeTurnStarted();
    private partial Task FinishCombatRailProbeAsync();
    private partial void CancelCombatRailProbe();
    // Drawn-frame straightness probe for the opening and second-turn hand draw.
    private partial void BeginHandDrawProbe(string step);
    // Same probe for a modified turn draw: extraDraw is how many of the arriving cards are above the native 5, and
    // turns on the no-retarget check for the cards already in hand.
    private partial void BeginHandDrawProbe(string step, int extraDraw);
    // Same probe for a reduced first draw: every card's target must be locked from its arrival (no re-centre at release).
    private partial void BeginHandDrawProbe(string step, bool allCardsStill);
    private partial Task FinishHandDrawProbeAsync(string step);
    private partial void CancelHandDrawProbe();
    // The cost of the host's portrait passes (PortraitFrameTiming) over an enemy turn, from the End Turn tap to the next
    // player phase. Reported in RESULTS, never gated.
    private partial void BeginEnemyTurnCostProbe();
    private partial Task FinishEnemyTurnCostProbeAsync(string step);

    // hand-selection's native select/confirm flow; the launcher's own layout is checked in LauncherHandChecks.cs.
    private partial Vector2 HandSelectionNeutralPoint();
    // One tap that adds a card to the native selection, sampling Confirm, the peek button and the selected row every process
    // frame and after each draw around the tap (LauncherHandChecks.cs).
    private partial Task TapSelectingAsync(NPlayerHand hand, VirtualPointer pointer, Vector2 point, string step,
        Func<bool> settled);
    private partial void CheckHandSelectionPreview(string name, NPlayerHand hand, NButton confirm, RichTextLabel prompt,
        Node container, NCardHolder[] shown);
    private partial Task CheckArmamentsPreviewInteractionsAsync(NPlayerHand hand, NUpgradePreview preview, NButton confirm,
        NPreviewCardHolder before, NPreviewCardHolder after, CardModel first);
    private partial Task CheckBurningPactPreviewInteractionsAsync(NPlayerHand hand, NSelectedHandCardContainer selected,
        NButton confirm, CardModel victim);
    private partial Task CheckWholeHandPreviewInteractionsAsync(NPlayerHand hand, NSelectedHandCardContainer selected,
        NButton confirm, CardModel last);

    // ---- Shop and Blitz (Batch B) ---------------------------------------------------------------------------------------
    // The shop scenarios run on both hosts. TouchSpireUI reflows the stock into its Cards/Items pages under a rotated rug;
    // the launcher lays the same groups out in rows and pages them between a cloth, two tabs and select-then-confirm
    // purchases. Everything the two do differently is a host partial: the TSUI implementations are in HostShopChecks.cs,
    // the launcher's in TouchSpire.LauncherHost/src/LauncherShopChecks.cs.

    /// <summary>The host's cloth node under the merchant inventory (TSUI's rotated rug, the launcher's cloth).</summary>
    private partial string ShopClothNodeName();
    /// <summary>The host's Cards / Items tab nodes, empty in a shop without pages (weird, Blitz).</summary>
    private partial string ShopCardsTabNodeName();
    private partial string ShopItemsTabNodeName();
    /// <summary>Layout assertions for the open inventory: page shape, stock rows, tabs and touch targets.</summary>
    private partial void CheckShopStockGeometry(NMerchantInventory inventory, Control stock);
    /// <summary>Switches the inventory's page by tapping its tab and checks the host's page state.</summary>
    private partial Task SelectShopPageAsync(NMerchantInventory inventory, bool items);
    /// <summary>The Items page's own layout: selected stock, target sizes and price legibility.</summary>
    private partial void CheckShopItemsLayout(NMerchantInventory inventory, Rect2 clip);
    /// <summary>
    /// The room around the inventory: TouchSpireUI frames the merchant, party and backdrop itself; the launcher keeps the
    /// native room and must still cover the canvas below the header.
    /// </summary>
    private partial void CheckShopRoomFraming(NMerchantButton merchant, bool weird);

    /// <summary>
    /// Confirms one slot through the host's native commit path: TouchSpireUI taps once (the native release buys), the
    /// launcher's first press selects and its second tap confirms.
    /// </summary>
    private partial Task ConfirmShopSlotAsync(NMerchantSlot slot);
    /// <summary>
    /// One purchase through the host's commit path. TouchSpireUI taps once; the launcher selects on the first press, checks
    /// the reticle, selection and native tip with nothing bought, then confirms on the second tap and pays exactly the
    /// listed price.
    /// </summary>
    private partial Task BuyShopSlotAsync(NMerchantSlot slot);
    /// <summary>Launcher-only: a Courier restock clears the selection, so a further tap cannot buy the replacement.</summary>
    private partial Task CheckShopRestockSelectionClearedAsync(NMerchantSlot slot, Player player);
    /// <summary>Launcher-only: a stationary hold inspects without committing, and a press elsewhere clears the selection.</summary>
    private partial void CheckShopHoldHost(int goldBefore, int commitsBefore, int clearsBefore);
    /// <summary>Launcher-only: a swipe past the slop buys nothing, leaves no half-armed confirmation and no other selection.</summary>
    private partial Task CheckShopSwipeHostAsync(NMerchantSlot slot, int goldBefore, int commitsBefore, int clearsBefore);
    /// <summary>Launcher-only: closing the inventory clears the selection.</summary>
    private partial void CheckShopCleared(string reason, bool hadSelection, int commitsBefore, int clearsBefore);
    /// <summary>Selects one stocked slot before a page change or a close, so the clear can be observed; TouchSpireUI no-ops.</summary>
    private partial Task ArmShopSelectionAsync(NMerchantInventory inventory);
    /// <summary>Whether a slot is selected right now (the launcher's select-then-confirm state); TouchSpireUI has none.</summary>
    private partial bool ShopSelected();
    /// <summary>Launcher-only: the Proceed button is a reachable phone-sized target.</summary>
    private partial void CheckShopProceedTarget(Control proceed);
    /// <summary>The native deck-select preview a card removal opens; the host sizes and places it differently.</summary>
    private partial void CheckShopRemovalPreview(NCard previewCard, NConfirmButton confirm);
    /// <summary>The launcher's select-then-confirm counters; TouchSpireUI has no selection so reports zero.</summary>
    private partial int ShopCommits();
    private partial int ShopClears();
    /// <summary>A point outside the shop's stock that dismisses the native inspector and clears any selection.</summary>
    private partial Vector2 ShopDismissPoint();

    /// <summary>Blitz's own header band: TouchSpireUI measures its stone header and potion row.</summary>
    private partial void CheckBlitzHeaderLayout(string name, NTopBar topBar);
    /// <summary>The native Blitz merchant room (TouchSpireUI scales and underlays it) and its action lane.</summary>
    private partial void CheckBlitzMerchantLayout(NBlitzMerchantRoom room);
    /// <summary>Blitz stock inside the host's cloth, including the native choices-remaining footer.</summary>
    private partial void CheckBlitzInventoryLayout(NMerchantInventory inventory, NBlitzMerchantSlot[] slots);
    /// <summary>
    /// The choices-remaining footer after a purchase: the launcher checks that its text still fits the cloth at the
    /// phone font size; the reference host only reports the line is present.
    /// </summary>
    private partial void CheckBlitzFooterLayout(NMerchantInventory inventory, Control footer, int remaining);
    /// <summary>The number of host-owned selection reticles left anywhere in the inventory; the reference host has none.</summary>
    private partial int ShopHighlightCount(NMerchantInventory inventory);
    /// <summary>Neow's three-card blessing grid under the host's header.</summary>
    private partial void CheckBlitzChooseThreeLayout(NCardGrid grid, NGridCardHolder[] cards);
    /// <summary>Skip / Proceed in the host's action lane.</summary>
    private partial void CheckBlitzActionLane(string name, NProceedButton action, Rect2? content);
    /// <summary>
    /// Skip / Proceed in the loot screen's lane. The launcher's loot layout is Batch E (L12): at 9:16 its panel's bottom
    /// right reaches into the native lane, so the launcher reports the measurement as deferred; TouchSpireUI checks it.
    /// </summary>
    private partial void CheckBlitzLootAction(string name, NProceedButton action, Rect2? content);
    /// <summary>
    /// Whether Skip and Proceed hold one position in the loot lane. The launcher's loot layout is Batch E (L12), so it
    /// reports the measurement as deferred there; TouchSpireUI keeps its own check.
    /// </summary>
    private partial void CheckBlitzSkipProceedPosition(Vector2 skipCenter, Vector2 proceedCenter);
    /// <summary>The loot panel and its reward rows at phone size.</summary>
    private partial void CheckBlitzLootLayout(NRewardsScreen loot, Control panel);
    /// <summary>Takes the first card of a Blitz card reward through the host's commit path.</summary>
    private partial Task TakeBlitzRewardCardAsync(NCardRewardSelectionScreen screen, NGridCardHolder holder);

    // ---- Rewards, card screens and treasure (Batch B) -------------------------------------------------------------------
    // These scenarios run on both hosts. Taking a card or a relic is the host's own input: TouchSpireUI's native release
    // takes it on one tap; the launcher selects on the first press and confirms on the second tap of the same instance,
    // except for a treasure chest whose every relic can be taken (a single player with no more visible relics than picks
    // left: a normal one-relic chest), which keeps the game's own single tap. The rows' fonts and sizes, the choice
    // screens' card/Skip geometry and the treasure room's framing are each host's own: the TouchSpire implementations are
    // in HostRewardChecks.cs, the launcher's in TouchSpire.LauncherHost/src/LauncherRewardChecks.cs.

    /// <summary>A card is taken from the card reward screen.</summary>
    private partial Task TakeRewardCardAsync(NGridCardHolder holder);
    /// <summary>A relic is taken from the treasure chest's relic holders through the host's tap contract.</summary>
    private partial Task TakeTreasureRelicAsync(NTreasureRoomRelicHolder holder);
    /// <summary>The press → native tip → release that takes the treasure's relic (one native tap when every relic is claimable).</summary>
    private partial Task CheckTreasurePressAwardAsync(string name, NTreasureRoomRelicHolder holder, Player player);

    /// <summary>The loot list's reward-row labels, heights, panel width and clear lane.</summary>
    partial void CheckRewardsRowsLayout(string name, NRewardsScreen screen, IReadOnlyList<NRewardButton> rows);
    /// <summary>Skip and reroll under the reward cards.</summary>
    partial void CheckRewardAlternativesLayout(string name, NCardRewardSelectionScreen screen,
        IReadOnlyList<NCardRewardAlternativeButton> alternatives);
    /// <summary>Skip's slot after a reroll, with the loot list's Proceed hidden behind it.</summary>
    partial void CheckRewardSkipLayout(string name, NCardRewardSelectionScreen screen, NCardRewardAlternativeButton skip,
        Control lootProceed);
    /// <summary>Records the background hand's state before Skip is tapped.</summary>
    partial void BeforeRewardSkipTap();
    /// <summary>The overlay's Skip tap did not reach the combat hand behind it.</summary>
    partial void CheckRewardSkipHandUntouched(string name);
    /// <summary>The reward cards' phone width.</summary>
    partial void CheckRewardCardsLayout(string name, NCardRewardSelectionScreen screen,
        IReadOnlyList<NGridCardHolder> holders);
    /// <summary>The choose-a-card screen's card scale and Skip size.</summary>
    partial void CheckCardScreensLayout(string name, NChooseACardSelectionScreen screen,
        IReadOnlyList<NGridCardHolder> holders, NChoiceSelectionSkipButton? skip);
    /// <summary>Whether the host's selection model is idle: nothing on the choice screen is armed or selected.</summary>
    private partial bool ChoiceSelectionIdle(NGridCardHolder holder);
    /// <summary>The card reward fixture's card scale.</summary>
    partial void CheckCardRewardScreenLayout(string name, NCardRewardSelectionScreen screen,
        IReadOnlyList<NGridCardHolder> holders);
    /// <summary>
    /// The loot list's long variant: a reward row's phone height and the list's phone width with the window shrunk around
    /// them, and, when <paramref name="scrolled"/> is set, the last row inside the mask and the Proceed/Skip in its lane.
    /// </summary>
    partial void CheckLootLongListLayout(string name, NRewardsScreen screen, IReadOnlyList<NRewardButton> rows, bool scrolled);
    /// <summary>A card reward's alternative row for any count (1, 2 or the synthetic 3).</summary>
    partial void CheckRewardAlternativeRow(string name, NCardRewardSelectionScreen screen,
        IReadOnlyList<NCardRewardAlternativeButton> alternatives);
    /// <summary>A choose-a-card screen with a variant card count: phone-sized, inside the safe frame, clear of the banner and Skip.</summary>
    partial void CheckChoiceCountLayout(string name, Control screen, IReadOnlyList<NGridCardHolder> holders, Control? skip);
    /// <summary>A relic choice with a variant relic count: phone targets clear of each other, with Skip under them.</summary>
    partial void CheckRelicCountLayout(string name, Control screen, IReadOnlyList<Control> holders, Control? skip);
    /// <summary>The bundle choice before or after a pack is opened: pack/preview cards at the phone's size, footer controls reachable.</summary>
    partial void CheckBundleLayout(string name, Control screen, IReadOnlyList<NCardBundle> packs, bool preview);
    /// <summary>
    /// The treasure room at one of its phases: <c>closed</c> (Proceed out of sight, chest and gold origin), <c>landscape</c>
    /// (untouched native room), <c>skip</c> (the delayed Skip button), <c>open</c> (relics and banner) and <c>proceed</c>.
    /// </summary>
    partial void CheckTreasureLayout(string name, string phase, NTreasureRoom room,
        NTreasureRoomRelicCollection collection);

    // ---- Rest site (L9) --------------------------------------------------------------------------------------------------
    // `rest` and `rest-tour` run on both hosts. TouchSpireUI scales the native option buttons to their phone size and leaves
    // the room's art where the game framed it; the launcher grows the row, measures its touch targets and covers the canvas
    // with the act's rest-site illustration. The TSUI implementations are in HostLayoutChecks.cs, the launcher's in
    // TouchSpire.LauncherHost/src/LauncherRewardChecks.cs.

    /// <summary>The rest site's option buttons at the host's phone size, reported as <c>rest_site_buttons_scaled</c>.</summary>
    private partial void CheckRestSiteButtons(IReadOnlyList<NRestSiteButton> buttons);
    /// <summary>The rest room's portrait framing: the options row inside the canvas and the act art covering it.</summary>
    private partial void CheckRestRoomLayout(NRestSiteRoom room);
    /// <summary>Whether a rest option takes two taps on this host (the launcher's select-then-confirm) or one (the reference).</summary>
    private partial bool RestOptionsTakeTwoTaps();
    /// <summary>Whether this host draws its selection reticle on a rest option that is selected but not taken.</summary>
    private partial bool RestOptionSelected(NRestSiteButton button);
    /// <summary>A selected-but-untaken rest option keeps the game's own hovered look (the launcher's select-then-confirm).</summary>
    private partial void CheckRestSelectedLooksHovered(NRestSiteButton button);
    /// <summary>The option copy (the native description) and the question against the option buttons, for the launcher's layout.</summary>
    private partial void CheckRestDescriptionClear(NRestSiteRoom room, int count);
    /// <summary>The room's Proceed once an option is spent: the launcher keeps it on the rest site's raised lane.</summary>
    private partial void CheckRestProceedLane(string name, NRestSiteRoom room);

    // ---- Card grids and selection flows (Batch F) ------------------------------------------------------------------------
    // `deck-view`, `upgrade`, `enchant` and `transform` run on both hosts. The grids' columns and card width, the sort row,
    // the View Upgrades box, the previews and their confirm buttons, and where Back sits are each host's own phone
    // geometry: TouchSpireUI's answers keep the original checks in HostCardGridChecks.cs, the launcher's (three columns on
    // its narrower canvas, 127-unit targets, the screen's own footer under the grid) are in
    // TouchSpire.LauncherHost/src/LauncherCardGridChecks.cs.

    /// <summary>The deck sort row below the host's header band, reported as <c>deck_sorters_high_below_header</c>.</summary>
    private partial void CheckDeckSortersPosition(Control header);
    /// <summary>The deck's four-way sort row (the game keeps it in the grid's scroll; TouchSpireUI re-parents it).</summary>
    private partial Control DeckSortRow(NDeckViewScreen screen, NCardGrid grid);
    /// <summary>The sort row and grid top: four 44-point sorters with the grid below the header.</summary>
    private partial bool DeckGridStartFits(Control header, Rect2 view, Rect2 firstCard,
        IReadOnlyList<NCardViewSortButton> sorters);
    /// <summary>The deck grid's three columns, reported as <c>deck_three_columns</c> when the host checks it.</summary>
    private partial void CheckDeckGridColumns(NCardGrid grid);
    /// <summary>A selection screen's grid top below the host's header band.</summary>
    private partial void CheckGridStartsBelowHeader(string name, Rect2 view, bool atTop, float offset);
    /// <summary>The enchant legend: opaque and below the prompt.</summary>
    private partial void CheckEnchantLegend(Control prompt, Control legend);
    /// <summary>The enchant preview's cards and confirm button at their phone size.</summary>
    private partial void CheckEnchantPreviewFit(int count, IReadOnlyList<Rect2> cards, Control confirm);
    /// <summary>The transform preview's composition and confirm button at their phone size.</summary>
    private partial void CheckTransformPreviewFit(int count, IReadOnlyList<Rect2> bounds, Control confirm, Vector2 previewScale);
    /// <summary>The upgrade grid's cards at three-column size.</summary>
    private partial void CheckUpgradeCardsScaled(NCardGrid grid, IReadOnlyList<NGridCardHolder> holders);
    /// <summary>The upgrade grid's three columns and three-column card width.</summary>
    private partial void CheckUpgradeColumns(NCardGrid grid, IReadOnlyList<NGridCardHolder> holders);
    /// <summary>The View Upgrades box in the footer below the grid, at its phone touch size.</summary>
    private partial void CheckUpgradeToggle(Control upgrades, Rect2 gridView);
    /// <summary>The upgrade comparison's cards and confirm at their phone size, with the prompt and toggle hidden.</summary>
    private partial void CheckUpgradePreview(NDeckUpgradeSelectScreen screen, Control comparison, Control confirm);
    /// <summary>Back above the View Upgrades box.</summary>
    private partial void CheckUpgradeBackAboveToggle(Control close, Control toggle);

    // ---- Run header, potion row, wrapped relics and the orb arc (Batch C) -----------------------------------------------

    // The phone's usable rectangle below the notch, which tips, popups and header content stay inside: the launcher's
    // measured safe cap (LauncherStageChecks.cs).
    private partial Rect2 PortraitSafeRect();

    // header-inventory's relic presentations: the launcher has one wrapped row.
    private partial System.Collections.Generic.IEnumerable<string> RelicPresentations(int count);
    private partial Task CheckRelicPresentationAsync(MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventory inventory, int count,
        string presentation);

    // The potion row's own layout - the clip inside the stone, twelve holders reachable by swiping, and no potion consumed
    // or activated by the swipe.
    private partial Task CheckHeaderPotionRowAsync(Player player);

    // The potion row the potions scenario scrolls.
    private partial ScrollContainer? PotionRow();

    // Drawn-frame continuity of the orb arc across an add, evoke or capacity change, so a re-layout that snaps instead of
    // tweening fails (LauncherHeaderChecks.cs).
    partial void BeginOrbMotionProbe(string step);
    partial void EndOrbMotionProbe(string step);
}
