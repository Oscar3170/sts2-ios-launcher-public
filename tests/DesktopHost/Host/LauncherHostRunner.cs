using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;
using STS2Mobile.DesktopHost.Harness;

namespace STS2Mobile.DesktopHost;

/// <summary>
/// The test run on the launcher host, started by <see cref="LauncherHostEntry"/> when <c>--sts2mobile-test</c> is present.
/// Scenarios: with <c>STS2_PORTRAIT_PROOF</c> set, the launcher's own <c>PortraitProof</c> is run and driven (screenshot and
/// acknowledgement per step); otherwise <c>menu</c>, <c>touch-probe</c>, the harness scenarios (<c>potion-reward</c>,
/// <c>orbs-ovicopter</c>, <c>enemy-order</c>, <c>decimillipede</c>, <c>hand-rows</c>, <c>hand-selection</c>,
/// <c>regent-stars</c>, <c>combat-piles</c>, <c>status-scaling</c>, <c>shop</c>, <c>rewards</c>, <c>treasure</c>,
/// <c>rest</c>, <c>rest-options</c>, <c>rest-tour</c>, <c>deck-view</c>, <c>upgrade</c>, <c>enchant</c>, <c>transform</c>, <c>map</c>,
/// <c>event</c>, <c>ancient</c>, <c>tour-&lt;act&gt;</c> and their variants), or the launcher's own <c>stage-encounters</c>,
/// <c>touch-combat</c>, <c>perf-combat</c> and <c>portrait-leave</c> through the host-neutral core. Writes RESULTS.json with the harness schema
/// plus the host fields, restores the scopes and quits, in the order TestDriver does.
/// </summary>
internal sealed class LauncherHostRunner
{
    private const string AckPath = "user://proof-next.txt";
    private const int MaxDetail = 8000;
    private const double DefaultTimeoutSeconds = 240;
    private const double TimeoutMarginSeconds = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // The second stage keeps explicit nulls (Proof, LauncherDirty) so every host field is always present.
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly SceneTree _tree;
    private readonly TestOptions _options;
    private readonly LauncherHostWiring _wiring;
    private readonly Exception? _wiringError;
    private readonly string? _captureError;
    private readonly UserDataSnapshot _before;
    private readonly LauncherSource _source;
    private readonly string _proof;
    private readonly TestReport _report;
    private readonly ScreenshotService _screenshots;
    private readonly double _deadlineSeconds;

    // The runner that owns this process's crash guard, and the once-only latch for RESULTS.json.
    private static LauncherHostRunner? _active;
    private int _resultsSaved;

    private LauncherHostRunner(SceneTree tree, TestOptions options, LauncherHostWiring wiring, Exception? wiringError,
        string? captureError, UserDataSnapshot before, LauncherSource source)
    {
        _tree = tree;
        _options = options;
        _wiring = wiring;
        _wiringError = wiringError;
        _captureError = captureError;
        _before = before;
        _source = source;
        _proof = OS.GetEnvironment("STS2_PORTRAIT_PROOF");
        _report = new TestReport(options.Scenario, options.PointerMode.ToString().ToLowerInvariant(), options.Seed);
        _screenshots = new ScreenshotService(tree, options.OutputDirectory);
        // run.sh gives up on RESULTS.json after STS2MOBILE_TEST_TIMEOUT seconds from the launch; finish a little before.
        double total = double.TryParse(System.Environment.GetEnvironmentVariable("STS2MOBILE_TEST_TIMEOUT"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) && value > 0
            ? value
            : DefaultTimeoutSeconds;
        _deadlineSeconds = Math.Max(30, total - TimeoutMarginSeconds);
    }

    public static async Task RunAsync(TestOptions options, LauncherHostWiring wiring, Exception? wiringError, string? captureError,
        UserDataSnapshot before, LauncherSource source)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Log.Error("Launcher host test run requires a SceneTree.");
            return;
        }
        await new LauncherHostRunner(tree, options, wiring, wiringError, captureError, before, source).RunInternalAsync();
    }

    // Seconds since the engine started, which is the clock run.sh's timeout is closest to.
    private static double Uptime => Time.GetTicksMsec() / 1000.0;

    private bool PastDeadline => Uptime > _deadlineSeconds;

    private async Task RunInternalAsync()
    {
        Log.Info($"[TEST] starting scenario={_options.Scenario} mode={_options.PointerMode} seed={_options.Seed} host=launcher " +
                 $"profile={TestProfileScope.TestProfileId} proof={(_proof.Length > 0 ? _proof : "none")}");
        InstallCrashGuard();
        try
        {
            ReportHostConfiguration();
            if (_wiringError != null) throw new InvalidOperationException("Launcher wiring failed: " + _wiringError.Message, _wiringError);

            await WaitForGameAsync();
            _report.Pass("main_menu_ready", "launcher wiring attached and portrait applied at the main menu");
            _report.Phase("menu_ready");
            // The portrait profile follows the canvas aspect, so a window the startup resized is a different test.
            if (_options.WindowSize is { } requested && await TestWindowScope.SettleAsync(_tree) is var settled && settled != requested)
                throw new InvalidOperationException($"Requested test window {requested}, got {settled}.");
            // Detection-first: the host is Wayland (the game is an X11 client), so raising the window is best effort;
            // the pointer readback is what proves another window is not covering the game. Every scenario here touches
            // the game, so this runs before the dispatch.
            VirtualPointer.RegisterWindowGuard(_tree, _report);
            await VirtualPointer.EnsureWindowUnobscuredAsync(initial: true);

            if (_proof.Length > 0) await RunProofAsync();
            else if (_options.Scenario.StartsWith("tour-", StringComparison.Ordinal))
            {
                await RunHarnessScenarioAsync();
            }
            else
            {
                switch (_options.Scenario)
                {
                    case "menu":
                        await RunMenuAsync();
                        break;
                    case "menus":
                    case "menu-hover":
                    case "menu-utilities":
                    case "settings":
                        await RunMenuScenarioAsync();
                        break;
                    case "menu-audit":
                        await RunMenuAuditAsync();
                        break;
                    case "compendium":
                        await RunCompendiumAsync();
                        break;
                    case "touch-probe":
                        await RunTouchProbeAsync();
                        break;
                    case "potion-reward":
                    case "orbs-ovicopter":
                    case "enemy-order":
                    case "decimillipede":
                    case "hand-rows":
                    case "hand-selection":
                    case "regent-stars":
                    case "combat-piles":
                    case "status-scaling":
                    case "shop":
                    case "shop-courier":
                    case "weirdshop":
                    case "shop-states":
                    case "blitz":
                    case "header-inventory":
                    case "header-indicators":
                    case "potions":
                    case "orbs":
                    case "rewards":
                    case "rewards-variants":
                    case "screens":
                    case "treasure":
                    case "rest":
                    case "rest-options":
                    case "rest-tour":
                    case "deck-view":
                    case "upgrade":
                    case "enchant":
                    case "transform":
                    case "map":
                    case "map-history":
                    case "event":
                    case "event-popup":
                    case "events-tight":
                    case "ancient":
                    case "ancient-pael":
                    case "ancient-art-tour":
                    case "ancient-dialogue":
                    case "pause-menu":
                    case "settings-deep":
                    case "capstone-over-hand":
                    case "game-over":
                    case "game-over-win":
                    case "game-over-boss":
                    case "game-over-room":
                    case "game-over-boss-win":
                        await RunHarnessScenarioAsync();
                        break;
                    case "select-confirm":
                        await RunLauncherSelectConfirmAsync();
                        break;
                    case "reward-guard":
                        await RunLauncherRewardGuardAsync();
                        break;
                    case "stage-encounters":
                    case "stage-osty":
                        await RunStageEncountersAsync();
                        break;
                    case "stage-backdrops":
                        await RunStageBackdropsAsync();
                        break;
                    case "touch-combat":
                        await RunTouchCombatAsync();
                        break;
                    case "perf-combat":
                        await RunPerfCombatAsync();
                        break;
                    case "portrait-leave":
                        await RunPortraitLeaveAsync();
                        break;
                    default:
                        _report.Fail("scenario", $"Unknown launcher-host scenario '{_options.Scenario}' without STS2_PORTRAIT_PROOF. " +
                                                 "Use menu, menus, menu-hover, menu-utilities, menu-audit, settings, pause-menu, settings-deep, game-over, game-over-win, " +
                                                 "game-over-boss, game-over-room, game-over-boss-win, capstone-over-hand, touch-probe, touch-combat, perf-combat, portrait-leave, potion-reward, orbs-ovicopter, enemy-order, " +
                                                 "decimillipede, hand-rows, hand-selection, regent-stars, combat-piles, status-scaling, " +
                                                 "shop, shop-courier, weirdshop, shop-states, blitz, header-inventory, header-indicators, potions, orbs, rewards, rewards-variants, screens, treasure, rest, rest-options, rest-tour, " +
                                                 "map, map-history, event, event-popup, events-tight, ancient, ancient-pael, ancient-art-tour, ancient-dialogue, " +
                                                 "tour-<act>, select-confirm, reward-guard, stage-encounters, stage-backdrops, or set STS2_PORTRAIT_PROOF to a PortraitProof mode.");
                        break;
                }
            }
            if (_report.Steps.All(step => step.Status != "failed")) _report.Pass("scenario_completed");
        }
        catch (WindowObscuredException ex)
        {
            // The guard already reported the single <scenario>_window_unobscured failure. Stop here instead of letting
            // every touch step fail against a pointer that cannot reach the game. The failed step keeps Passed=false.
            _report.Skip($"{_options.Scenario}_touch_skipped",
                "window obscured: touch steps were not run — " + ex.Message);
            Log.Warn($"[TEST] touch steps skipped: {ex.Message}");
        }
        catch (Exception ex)
        {
            await TestDriver.FailureDiagnosticsAsync(_tree, _report, _screenshots, "scenario_exception", null,
                _tree.Root.GetMousePosition());
            _report.Error = ex.ToString();
            _report.Fail("scenario_exception", ex.Message);
            Log.Error($"[TEST] scenario failed: {ex}");
        }
        finally
        {
            _report.Phase("teardown");
            // The order TestDriver uses: scopes restored first, then the results, then quit.
            TestSettingsScope.Restore();
            TestProfileScope.Restore();
            _report.OriginalProfileId = TestProfileScope.OriginalProfileId.ToString();
            _report.TestProfileId = TestProfileScope.TestProfileId.ToString();
            DeleteAck();

            try
            {
                // Captures were encoded on background tasks; finish them before RESULTS.json and the quit.
                await _screenshots.FlushAsync();
            }
            catch (Exception ex)
            {
                Log.Error($"[TEST] screenshot write failed: {ex}");
                _report.Fail("screenshot_write", ex.GetBaseException().Message);
            }

            try
            {
                // profile.save is written back by the restore above; give the game's writer a moment before comparing.
                await WaitFramesAsync(15);
                ReportUserData();
                ReportOnlyOwnPatches();
                _report.Pass("settings-writes-suppressed",
                    $"{PhonePatchShims.SuppressedSettingsWrites} SaveSettings call(s) skipped so settings.save stayed as it was");
                string results = SaveResultsOnce();
                Log.Info($"[TEST] saved {results}");
            }
            catch (Exception ex)
            {
                Log.Error($"[TEST] could not save results: {ex}");
            }

            await WaitFramesAsync(2);
            _wiring.Detach();
            RemoveCrashGuard();
            _tree.Quit();
        }
    }

    private void ReportHostConfiguration()
    {
        _report.Pass("host-config",
            $"launcher={_source.Revision}{(_source.Dirty == true ? "+dirty" : "")} " +
            $"checkout={_source.Checkout ?? "unknown"} " +
            $"proof={(_proof.Length > 0 ? _proof : "none")} portrait={SafeAreaCanvas.IsPortrait} insets={NativeExports.InsetsText} " +
            $"requestedWindow={(_options.WindowSize is { } size ? $"{size.X}x{size.Y}" : "default")} nofit={OS.GetEnvironment("STS2_PORTRAIT_NOFIT")}");
        if (NativeExports.InsetsError != null) _report.Fail("insets", NativeExports.InsetsError);
        foreach ((string name, string? error) in PhonePatchShims.Results)
        {
            if (error == null) _report.Pass($"shim:{name}");
            else _report.Fail($"shim:{name}", error);
        }
        if (_proof.Length > 0 && _captureError != null)
            _report.Fail("proof-capture", "PortraitProof output cannot be captured: " + _captureError);
    }

    private async Task WaitForGameAsync()
    {
        while (!_wiring.GameStarted.IsCompleted)
        {
            if (PastDeadline) throw new TimeoutException("Timed out waiting for the main menu.");
            await WaitFramesAsync(1);
        }
        await _wiring.GameStarted;
    }

    // ---- scenarios ----

    private async Task RunMenuAsync()
    {
        await WaitFramesAsync(60);
        string path = await _screenshots.CaptureAsync("main-menu");
        _report.Screenshot("main-menu", path);
        _report.Pass("menu-settled", $"portrait={SafeAreaCanvas.IsPortrait} {DescribeGeometry()}");
    }

    /// <summary>
    /// The main-menu scenarios (<c>menus</c>, <c>menu-hover</c>, <c>menu-utilities</c>, <c>settings</c>): no run is
    /// started except the isolated saved-run fixture <c>menu-hover</c> needs. The flow, the taps and the T5 gate come from
    /// the harness; this host's screen geometry is LauncherMenuChecks.cs.
    /// </summary>
    private async Task RunMenuScenarioAsync()
    {
        var pointer = new VirtualPointer(_tree, PointerMode.Touch);
        await TestDriver.RunMenuScenarioAsync(_options, _report, _screenshots, pointer);
    }

    /// <summary>
    /// The launcher host's own menu audit: the same screens as <c>STS2_PORTRAIT_PROOF=audit-menus</c>, each gated for the 44
    /// point touch minimum instead of only logged (LauncherMenuAuditScenario.cs).
    /// </summary>
    private async Task RunMenuAuditAsync()
    {
        var pointer = new VirtualPointer(_tree, PointerMode.Touch);
        await TestDriver.RunLauncherMenuAuditAsync(_options, _report, _screenshots, pointer);
    }

    /// <summary>The card library's portrait interface (LauncherCompendiumScenario.cs): header filters, collapsible advanced panel and the touch-scrolled three-column grid.</summary>
    private async Task RunCompendiumAsync()
    {
        var pointer = new VirtualPointer(_tree, PointerMode.Touch);
        await TestDriver.RunLauncherCompendiumAsync(_options, _report, _screenshots, pointer);
    }

    /// <summary>
    /// Feasibility check 2: a touch injected the way the launcher's own proofs and our harness do reaches Root.WindowInput, which is
    /// where PhoneTouchCards, PhoneTouchScroll and PhoneChoiceConfirm listen. Positions are a corner of the canvas, clear of menu buttons.
    /// </summary>
    private async Task RunTouchProbeAsync()
    {
        await WaitFramesAsync(30);
        // The first press is also what focuses the window. A warp issued while the window manager is still moving the
        // window resolves against a stale origin, which can land hundreds of units away (seen once as a -450 canvas x);
        // wait for the size and position to stop changing before the first injection.
        await WaitForWindowPlacementAsync();
        var probe = new InputProbe();
        _tree.Root.WindowInput += probe.OnInput;
        try
        {
            // Not the extreme corner: X can clamp a warp there. The points are canvas coordinates that map to whole window
            // pixels at both test shapes (1.5x: /3; 2x: /2), because XWarpPointer truncates to integer window pixels.
            var start = new Vector2(42, 42);
            var end = new Vector2(42, 84);
            int scrolls = PhoneTouchScroll.ScrollsStarted, released = PhoneTouchScroll.PressesReleased, inspects = PhoneTouchCards.InspectsOpened;

            // The launcher's own injection (PortraitProofTouch.SendTouch / SendTouchDrag): Input.ParseInputEvent, window coordinates.
            Transform2D toWindow = _tree.Root.GetFinalTransform();
            Input.ParseInputEvent(new InputEventScreenTouch { Index = 0, Pressed = true, Position = toWindow * start });
            await WaitFramesAsync(4);
            await CheckPointerAsync("touch-probe-mouse-after-press", start);
            Input.ParseInputEvent(new InputEventScreenDrag { Index = 0, Position = toWindow * end, Relative = toWindow * end - toWindow * start });
            await WaitFramesAsync(2);
            await CheckPointerAsync("touch-probe-mouse-after-drag", end);
            Input.ParseInputEvent(new InputEventScreenTouch { Index = 0, Pressed = false, Position = toWindow * end });
            await WaitFramesAsync(3);
            _report.Check("touch-probe-direct", probe.SawAll,
                $"Input.ParseInputEvent -> Root.WindowInput: {probe.Describe()}; expected press at {toWindow * start}");

            // Our harness's injection (VirtualPointer, touch mode).
            probe.Reset();
            var pointer = new VirtualPointer(_tree, PointerMode.Touch);
            await pointer.DragAsync(start, end, steps: 4);
            await WaitFramesAsync(3);
            _report.Check("touch-probe-virtualpointer", probe.SawAll,
                $"VirtualPointer.DragAsync (touch) -> Root.WindowInput: {probe.Describe()}; expected press at {pointer.ToScreenPosition(start)}");
            await CheckPointerAsync("touch-probe-virtualpointer-mouse", end);

            _report.Pass("touch-probe-counters",
                $"PhoneTouchScroll.ScrollsStarted {scrolls}->{PhoneTouchScroll.ScrollsStarted}, PressesReleased {released}->{PhoneTouchScroll.PressesReleased}, " +
                $"PhoneTouchCards.InspectsOpened {inspects}->{PhoneTouchCards.InspectsOpened} (they count scrolls/inspects on cards and lists; a bare corner touch is not one)");
            string shot = await _screenshots.CaptureAsync("touch-probe");
            _report.Screenshot("touch-probe", shot);
        }
        finally
        {
            _tree.Root.WindowInput -= probe.OnInput;
        }
    }

    private async Task<bool> TryWaitAsync(Func<bool> condition, string description, TimeSpan timeout)
    {
        ulong start = Time.GetTicksMsec();
        while (!condition())
        {
            if (Time.GetTicksMsec() - start > (ulong)timeout.TotalMilliseconds)
            {
                Log.Warn($"[TEST] timed out waiting for {description}");
                return false;
            }
            await WaitFramesAsync(1);
        }
        return true;
    }

    private async Task WaitForWindowPlacementAsync()
    {
        Vector2I lastSize = new(int.MinValue, int.MinValue);
        Vector2I lastPosition = new(int.MinValue, int.MinValue);
        bool stable = await TryWaitAsync(
            () =>
            {
                Vector2I size = DisplayServer.WindowGetSize();
                Vector2I position = DisplayServer.WindowGetPosition();
                bool same = size == lastSize && position == lastPosition;
                lastSize = size;
                lastPosition = position;
                return same && size.X > 0 && size.Y > 0 && position.X >= 0 && position.Y >= 0;
            },
            "window size and position stable before the probe", TimeSpan.FromSeconds(5));
        _report.Check("touch-probe-window-placed", stable,
            $"size={DisplayServer.WindowGetSize()} position={DisplayServer.WindowGetPosition()} " +
            $"root={_tree.Root.Position} canvas={_tree.Root.GetVisibleRect().Size}");
    }

    /// <summary>
    /// The engine's pointer position must be the canvas point the harness injected: on the phone the finger and the
    /// emulated mouse coincide, and every mouse-driven game path (<c>NMouseCardPlay</c>, targeting, holder hover) reads
    /// <c>Root.GetMousePosition</c>. A window-manager/client-origin mismatch shows up here as a constant offset, which a
    /// screenshot alone does not reveal.
    /// </summary>
    private async Task CheckPointerAsync(string name, Vector2 expected)
    {
        await WaitFramesAsync(2);
        Vector2 mouse = _tree.Root.GetMousePosition();
        Vector2 delta = mouse - expected;
        _report.Check(name, delta.Length() <= 1f,
            $"Root.GetMousePosition()={mouse} injected canvas point={expected} delta={delta} " +
            $"window={DisplayServer.WindowGetSize()} canvas={_tree.Root.GetVisibleRect().Size}");
    }

    /// <summary>
    /// The harness scenarios: <c>potion-reward</c> (Attack Potion use, Peek, Skip and select),
    /// <c>orbs-ovicopter</c> (a real End Turn press, native eggs and hatching, three-to-ten orbs), <c>status-scaling</c>
    /// (the native power row for the player, a lone small enemy and the act boss), the shop scenarios and, since Batch B,
    /// <c>rewards</c>, <c>rewards-variants</c> (the seven-row loot list, alternative counts, choose-a-card 1/2/4/5, relic
    /// choice 1/2/5 and the bundle choice), <c>screens</c>, <c>treasure</c>, <c>rest</c> and <c>rest-tour</c>. They run the
    /// host-neutral core with
    /// this run's report, screenshots and a touch pointer. On this host the first tap on a choice card only selects it and
    /// the second tap takes it (<c>TakeChoiceCardAsync</c>); reward cards work the same way, while a treasure relic needs the
    /// second tap only when the chest offers a choice (a single player's normal one-relic chest keeps the game's own tap).
    /// Each screen's layout assertions are the launcher's own (LauncherRewardChecks.cs, LauncherShopChecks.cs).
    /// </summary>
    private async Task RunHarnessScenarioAsync()
    {
        // The launcher's portrait handlers (PhoneTouchCards, PhoneSelectConfirm, the tap guard) listen for screen touches;
        // touch mode also matches every other launcher-host run the broker starts (run.sh --touch).
        var pointer = new VirtualPointer(_tree, PointerMode.Touch);
        await TestDriver.RunLinkedScenarioAsync(_options, _report, _screenshots, pointer);
    }

    /// <summary>
    /// The launcher's own combat-stage acceptance run (C1/C2/C3): the fixed encounter fixture from the <c>combat-stage</c>
    /// scenario, measured against the launcher's portrait bands. The scenario and its geometry live in
    /// LauncherStageChecks.cs, which is compiled only into this host.
    /// </summary>
    private async Task RunStageEncountersAsync()
    {
        var pointer = new VirtualPointer(_tree, PointerMode.Touch);
        await TestDriver.RunStageScenarioAsync(_options, _report, _screenshots, pointer);
    }

    /// <summary>
    /// The launcher's own backdrop fixture (<c>stage-backdrops</c>): the Underdocks bosses whose custom background is the
    /// act's own pier, measured against a normal Underdocks fight, and the Architect's ending picture, measured as a canvas
    /// cover. The scenario lives in LauncherBackdropChecks.cs.
    /// </summary>
    private async Task RunStageBackdropsAsync()
    {
        var pointer = new VirtualPointer(_tree, PointerMode.Touch);
        await TestDriver.RunBackdropScenarioAsync(_options, _report, _screenshots, pointer);
    }

    /// <summary>
    /// The launcher host's portrait touch acceptance run (C7/T1-T3): a Standard Ironclad fight with five and then ten cards,
    /// driven entirely by injected finger input (VirtualPointer in touch mode). The checks live in LauncherTouchScenario.cs,
    /// which is compiled only into this host and asserts against the launcher's TouchCardMath/PortraitCombatMath constants.
    /// </summary>
    private async Task RunTouchCombatAsync()
    {
        var pointer = new VirtualPointer(_tree, PointerMode.Touch);
        await TestDriver.RunLauncherTouchCombatAsync(_options, _report, _screenshots, pointer);
    }

    /// <summary>
    /// The launcher host's performance run: the same fixed NibbitsNormal fight as touch-combat, driven by real touch input,
    /// with per-second and per-draw-window frame monitors, the launcher's portrait pass buckets and GC counters, plus the
    /// preload, pass-budget and ReduceEffects gates. The scenario lives in LauncherPerfScenario.cs.
    /// </summary>
    private async Task RunPerfCombatAsync()
    {
        var pointer = new VirtualPointer(_tree, PointerMode.Touch);
        await TestDriver.RunLauncherPerfCombatAsync(_options, _report, _screenshots, pointer);
    }

    /// <summary>
    /// The launcher host's leave-restore run: portrait -> landscape -> portrait mid-draw with Reduce Effects on, the
    /// Kaiser Crab's arm fit in two fights in a row, a fight ending during a turn draw and its reward/deck cards, and the
    /// inspector's upgrade box across a live Portrait Mode switch. The scenario lives in LauncherLeaveScenario.cs.
    /// </summary>
    private async Task RunPortraitLeaveAsync()
    {
        var pointer = new VirtualPointer(_tree, PointerMode.Touch);
        await TestDriver.RunLauncherPortraitLeaveAsync(_options, _report, _screenshots, pointer);
    }

    /// <summary>
    /// The launcher's select-then-confirm acceptance (Batch B) on every screen that lost the Choose button: card
    /// rewards, choose-a-card, relic choice, treasure relics (one native tap when the chest offers every relic, two taps
    /// when it offers a choice), and the portrait/landscape round trip. Driven entirely by injected touch input (VirtualPointer
    /// in touch mode), because the second tap is read from the window's own touch/mouse release.
    /// </summary>
    private async Task RunLauncherSelectConfirmAsync()
    {
        var pointer = new VirtualPointer(_tree, PointerMode.Touch);
        await TestDriver.RunLauncherSelectConfirmAsync(_options, _report, _screenshots, pointer);
    }

    /// <summary>
    /// The loot list's guard, kept separate from select-then-confirm: fade-in taps, a claim, a press spanning the rows'
    /// move, and a settled row taking a new tap.
    /// </summary>
    private async Task RunLauncherRewardGuardAsync()
    {
        var pointer = new VirtualPointer(_tree, PointerMode.Touch);
        await TestDriver.RunLauncherRewardGuardAsync(_options, _report, _screenshots, pointer);
    }

    private sealed class InputProbe
    {
        private int _presses, _releases, _drags;
        private Vector2 _firstPress;

        public bool SawAll => _presses > 0 && _releases > 0 && _drags > 0;

        public void Reset() => (_presses, _releases, _drags, _firstPress) = (0, 0, 0, Vector2.Zero);

        public void OnInput(InputEvent input)
        {
            switch (input)
            {
                case InputEventScreenTouch { Pressed: true } touch:
                    if (_presses++ == 0) _firstPress = touch.Position;
                    break;
                case InputEventScreenTouch:
                    _releases++;
                    break;
                case InputEventScreenDrag:
                    _drags++;
                    break;
            }
        }

        public string Describe() => $"touch presses={_presses} (first at {_firstPress}), releases={_releases}, drags={_drags}";
    }

    // ---- STS2_PORTRAIT_PROOF ----

    private async Task RunProofAsync()
    {
        if (_captureError != null) return;
        DeleteAck();
        NGame game = NGame.Instance ?? throw new InvalidOperationException("NGame is gone.");
        // GameEntry.RunPortraitProofAfterLauncherHidesAsync: three frames, then the proof.
        await WaitFramesAsync(3);
        Task proof = PortraitProof.RunIfRequestedAsync(_tree, game);

        while (true)
        {
            if (ProofCapture.TryDequeue(out string line))
            {
                if (await HandleProofLineAsync(line)) return;
                continue;
            }
            if (proof.IsCompleted)
            {
                if (proof.IsFaulted) _report.Fail("proof-task", proof.Exception?.GetBaseException().Message ?? "faulted");
                _report.Fail("proof-no-ready-line", $"PortraitProof '{_proof}' returned without a 'Portrait proof ready:' line.");
                return;
            }
            if (PastDeadline)
            {
                _report.Fail("proof-timeout", $"No 'Portrait proof ready:' line for '{_proof}' within {_deadlineSeconds:F0}s of launch " +
                                              "(raise STS2MOBILE_TEST_TIMEOUT for long proofs).");
                return;
            }
            await WaitFramesAsync(1);
        }
    }

    /// <summary>Returns true when the proof has finished (ready, failed or unknown mode).</summary>
    private async Task<bool> HandleProofLineAsync(string line)
    {
        string detail = line.Length > MaxDetail ? line[..MaxDetail] + "…" : line;
        const string step = "Portrait proof step: ";
        const string ready = "Portrait proof ready:";

        if (line.StartsWith(step, StringComparison.Ordinal))
        {
            string rest = line[step.Length..];
            int space = rest.IndexOf(' ');
            string name = space < 0 ? rest : rest[..space];
            await ProofStepAsync(name, detail);
            return false;
        }
        if (line.StartsWith(ready, StringComparison.Ordinal))
        {
            string path = await TryScreenshotAsync("proof-ready");
            _report.Pass("proof-ready", detail);
            if (path != "") _report.Screenshot("proof-ready", path);
            return true;
        }
        if (line.StartsWith("Portrait proof failed", StringComparison.Ordinal) || line.StartsWith("Portrait proof unknown mode", StringComparison.Ordinal))
        {
            _report.Fail("proof-failed", detail);
            return true;
        }
        if (line.StartsWith("Portrait proof step ack timeout", StringComparison.Ordinal))
        {
            _report.Fail("proof-ack-timeout", detail);
            return false;
        }
        _report.Pass(ProofCapture.IsResultsLine(line) ? "proof-results" : "proof-info", detail);
        return false;
    }

    private async Task ProofStepAsync(string name, string line)
    {
        string path = await TryScreenshotAsync(name);
        _report.Pass($"proof-step:{name}", line);
        if (path != "") _report.Screenshot(name, path);
        await AckAsync(name);
    }

    private async Task<string> TryScreenshotAsync(string name)
    {
        try
        {
            return await _screenshots.CaptureAsync(name);
        }
        catch (Exception ex)
        {
            _report.Fail($"screenshot:{name}", ex.Message);
            return "";
        }
    }

    // Writes the file PortraitProof.WaitForHostStepAckAsync polls for. It deletes the file when it reads it; a step that logs without
    // waiting (LogCombatSnapshot's intermediate lines) leaves it, and the next real wait would run straight through, so it is removed.
    private async Task AckAsync(string step)
    {
        using (Godot.FileAccess? file = Godot.FileAccess.Open(AckPath, Godot.FileAccess.ModeFlags.Write))
        {
            if (file == null) throw new InvalidOperationException($"Could not write {AckPath}: {Godot.FileAccess.GetOpenError()}");
            file.StoreString("next");
        }
        for (int frame = 0; frame < 30; frame++)
        {
            await WaitFramesAsync(1);
            if (!Godot.FileAccess.FileExists(AckPath)) return;
        }
        DeleteAck();
        _report.Pass($"proof-step-noack:{step}", "The proof did not wait for an acknowledgement after this line; the screenshot was taken without pausing it.");
    }

    private static void DeleteAck()
    {
        if (Godot.FileAccess.FileExists(AckPath)) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(AckPath));
    }

    // ---- guards ----

    private void ReportUserData()
    {
        int testProfileId = TestProfileScope.TestProfileId;
        if (_before.ExcludedProfileId is int excluded && excluded != testProfileId)
        {
            _report.Fail("user-data-unchanged",
                $"the pre-run snapshot skipped profile{excluded} but the run used profile{testProfileId}; " +
                "the user data cannot be proven unchanged, so no hash result is accepted");
            return;
        }

        // Same exclusion on both sides: Changes ignores this directory's differences either way.
        UserDataSnapshot after = UserDataGuard.Take(testProfileId);
        _report.Phase("user_data_hashed", $"{after.Hashes.Count} file(s)");
        List<string> changes = UserDataGuard.Changes(_before, after, testProfileId, out int ignored);
        string detail = $"{_before.Hashes.Count} file(s) hashed under {_before.Root} before the run, {after.Hashes.Count} after; " +
                        $"{ignored} difference(s) inside the test profile (profile{testProfileId}) ignored" +
                        (_before.ExcludedProfileId is int skipped ? $"; profile{skipped} not hashed (already ignored)" : "");
        if (changes.Count == 0) _report.Pass("user-data-unchanged", detail);
        else _report.Fail("user-data-unchanged", detail + "; changed outside the test profile: " + string.Join(", ", changes.Take(30)));
    }

    /// <summary>
    /// The game under test is the game plus this host: a Harmony patch from any other mod (BaseLib, a gameplay mod) changes
    /// what the checks measure.
    /// </summary>
    private void ReportOnlyOwnPatches()
    {
        var owners = new SortedSet<string>(StringComparer.Ordinal);
        var foreign = new List<string>();
        foreach (MethodBase method in Harmony.GetAllPatchedMethods().ToList())
        {
            Patches? info = Harmony.GetPatchInfo(method);
            if (info == null) continue;
            foreach (Patch patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
            {
                owners.Add(patch.owner);
                if (!patch.owner.StartsWith(PhonePatchShims.HarmonyId, StringComparison.Ordinal))
                    foreign.Add($"{method.DeclaringType?.Name}.{method.Name} ({patch.owner})");
            }
        }
        string detail = $"patch owners: {string.Join(", ", owners)}";
        if (foreign.Count == 0) _report.Pass("only-own-patches", detail);
        else _report.Fail("only-own-patches", $"{detail}; other mods' patches on: {string.Join(", ", foreign.Take(20))}");
    }

    // ---- crash guard ----------------------------------------------------------------------------------------------------

    private void InstallCrashGuard()
    {
        _active = this;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void RemoveCrashGuard()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        if (ReferenceEquals(_active, this)) _active = null;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args) =>
        _active?.RecordUnhandled(args.ExceptionObject as Exception, "unhandled exception");

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        args.SetObserved();
        _active?.RecordUnhandled(args.Exception, "unobserved task exception");
    }

    private string SaveResultsOnce()
    {
        string path = SaveResults();
        Interlocked.Exchange(ref _resultsSaved, 1);
        return path;
    }

    /// <summary>
    /// Last-resort results writer for an exception that escapes a Godot signal-resumed continuation: the runtime terminates
    /// the process there, before this runner's own <c>finally</c> can write. Record the failure, put the scopes back and
    /// write RESULTS.json here. Runs at most once, and never after a normal results write.
    /// </summary>
    private void RecordUnhandled(Exception? error, string source)
    {
        if (Interlocked.Exchange(ref _resultsSaved, 1) != 0) return;
        string text = error?.ToString() ?? "unknown exception";
        Log.Error($"[TEST] {source}: {text}");
        try
        {
            _report.Error = text;
            _report.Fail("scenario_exception", $"{source}: {error?.GetBaseException().Message ?? text}");
            _report.Phase("unhandled_exception");
            try
            {
                TestSettingsScope.Restore();
                TestProfileScope.Restore();
                _report.OriginalProfileId = TestProfileScope.OriginalProfileId.ToString();
                _report.TestProfileId = TestProfileScope.TestProfileId.ToString();
            }
            catch (Exception restore)
            {
                Log.Error($"[TEST] crash restore failed: {restore}");
            }
            try
            {
                // Captures were encoded on background tasks; do not wait long for them in a dying process.
                if (!_screenshots.FlushAsync().Wait(TimeSpan.FromSeconds(2)))
                    Log.Warn("[TEST] crash screenshot flush timed out");
            }
            catch (Exception flush)
            {
                Log.Error($"[TEST] crash screenshot flush failed: {flush}");
            }
            string results = SaveResults();
            Log.Info($"[TEST] saved {results} after an {source}");
        }
        catch (Exception write)
        {
            Log.Error($"[TEST] could not save results after an unhandled exception: {write}");
            try
            {
                // Engine-free fallback: the broker still needs a RESULTS.json that says the run failed.
                Directory.CreateDirectory(_options.OutputDirectory);
                JsonObject minimal = new()
                {
                    ["Scenario"] = _options.Scenario,
                    ["Passed"] = false,
                    ["Error"] = text,
                    ["Steps"] = new JsonArray(new JsonObject
                    {
                        ["Name"] = "scenario_exception",
                        ["Status"] = "failed",
                        ["Detail"] = source + ": " + (error?.GetBaseException().Message ?? text),
                    }),
                };
                File.WriteAllText(Path.Combine(_options.OutputDirectory, "RESULTS.json"), minimal.ToJsonString(WriteOptions));
            }
            catch (Exception last)
            {
                Log.Error($"[TEST] minimal results write failed: {last}");
            }
        }
    }

    // ---- results ----

    private string DescribeGeometry()
    {
        Vector2I window = DisplayServer.WindowGetSize();
        Vector2I canvas = _tree.Root.ContentScaleSize;
        return $"window={window.X}x{window.Y} canvas={canvas.X}x{canvas.Y}";
    }

    private string SaveResults()
    {
        _report.Phase("results_written");
        _report.FinishedAt = DateTimeOffset.Now;
        _report.TotalMs = (long)(_report.FinishedAt.Value - _report.StartedAt).TotalMilliseconds;
        JsonObject results = JsonSerializer.SerializeToNode(_report, JsonOptions)!.AsObject();
        Vector2I window = DisplayServer.WindowGetSize();
        Vector2I canvas = _tree.Root.ContentScaleSize;
        results["Host"] = "launcher";
        results["LauncherRevision"] = _source.Revision;
        results["LauncherDirty"] = _source.Dirty;
        results["GameVersion"] = GameVersion();
        results["Window"] = $"{window.X}x{window.Y}";
        results["Canvas"] = $"{canvas.X}x{canvas.Y}";
        results["Insets"] = NativeExports.InsetsText;
        results["Proof"] = _proof.Length > 0 ? _proof : null;
        results["Portrait"] = SafeAreaCanvas.IsPortrait;
        results["OrientationRequests"] = NativeExports.OrientationRequests;

        Directory.CreateDirectory(_options.OutputDirectory);
        string path = Path.Combine(_options.OutputDirectory, "RESULTS.json");
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, results.ToJsonString(WriteOptions));
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    private static string GameVersion()
    {
        try
        {
            return MegaCrit.Sts2.Core.Debug.ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private async Task WaitFramesAsync(int frames)
    {
        for (int i = 0; i < frames; i++) await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
    }
}
