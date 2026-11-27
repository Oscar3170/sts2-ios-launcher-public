using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;
using STS2Mobile.DesktopHost;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// <c>portrait-leave</c>: the paths that put portrait combat back when the player leaves it, on the launcher host.
/// <list type="number">
/// <item>Portrait → landscape → portrait in the middle of a turn draw, with Reduce Effects on: after the switch every
/// hand card's glow and shadow are visible again and the glow width is the game's own, the pile touch areas are gone, no
/// portrait metadata is left on the room or the creatures, and the HP labels are back at their native position and
/// scale. Back in portrait the piles enter on the rail and the hand lays out as it did before the switch.</item>
/// <item>Two Kaiser Crab fights in a row in one portrait session: the background skeleton's arm fit is applied in both,
/// not only the first.</item>
/// <item>A fight that ends while a turn draw is in flight, then the card reward screen and the deck view: no card still
/// has a hidden glow or shadow or a dimmed body from the fight.</item>
/// <item>A card held open in the inspector while Portrait Mode is turned off: the native "View Upgrades" box is back at
/// its own scale and offsets.</item>
/// </list>
/// The live portrait switch is the launcher's own <c>PortraitLayout.Apply</c>, which the Graphics "Portrait Mode" row
/// calls; on the host the canvas changes instead of the window rotating. The Reduce Effects choice is the launcher static
/// this host does not get from PhoneGraphics, so the scenario sets it directly (as <c>perf-combat</c> does).
/// </summary>
internal sealed partial class TestDriver
{
    private static readonly string[] PortraitRoomMetaNames =
    {
        "portrait_hud_floor_y", "portrait_peer_scale", "portrait_peer_scale_floored",
        "portrait_floor_horizon", "portrait_formation_offset",
    };

    private static readonly string[] PortraitBackgroundContainerMetaNames =
    {
        "portrait_floor_horizon", "portrait_formation_offset",
    };

    private static readonly string[] PortraitBackgroundRootMetaNames =
    {
        "portrait_floor_aligned", "portrait_art_bottom", "portrait_pier", "portrait_custom_arena",
        "portrait_pier_surface", "portrait_floor_horizon", "portrait_formation_offset",
        "portrait_foreground_visible",
    };

    // What the portrait pass dims on an unplayable card's body; the card node's own static children are left alone.
    private static readonly HashSet<string> PortraitSkippedCardBodyChildren = new()
    {
        "Shadow", "Highlight", "EnergyIcon", "StarIcon", "CardVfxContainer", "CardSparkles",
    };

    /// <summary>Called by <c>LauncherHostRunner</c> for <c>--touchspire-test=portrait-leave</c>.</summary>
    internal static async Task RunLauncherPortraitLeaveAsync(TestOptions options, TestReport report,
        ScreenshotService screenshots, VirtualPointer pointer)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Log.Error("Launcher portrait-leave requires a SceneTree.");
            return;
        }
        await new TestDriver(tree, options, report, screenshots, pointer).RunLauncherPortraitLeaveInternalAsync();
    }

    private async Task RunLauncherPortraitLeaveInternalAsync()
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            _report.Fail("portrait-leave_portrait", $"portrait-leave needs a portrait window; window={DisplayServer.WindowGetSize()} " +
                                                    $"canvas={GetViewportSize()}");
            return;
        }

        await WaitForMainMenuPreloadAsync();
        RunState state = await StartIroncladRunAsync();
        Player player = LocalContext.GetMe(state) ?? throw new InvalidOperationException("No local player in the run.");
        player.Creature.SetMaxHpInternal(9999);
        player.Creature.SetCurrentHpInternal(9999);

        // Test-only fixture, as in perf-combat: the Graphics row that stores this runs in PhoneGraphics, which this host
        // does not start. Reduce Effects takes the glow and shadow away from a card arriving from the draw (a card that
        // only re-spaces keeps them), so the leave paths must be exercised with it on.
        PortraitHandLayout.ReduceEffects = true;
        _report.Pass("portrait-leave_reduce_effects_on",
            "PortraitHandLayout.ReduceEffects=true for the hand checks (test-only fixture)");
        try
        {
            // One fight to start from; every phase afterwards brings its own room, and a phase that fails must not keep
            // the later ones from running (each one is reported on its own).
            NCombatRoom room = await EnterTouchCombatAsync(state, "portrait-leave");
            // The inspector test runs first, in the fight's fresh portrait session and before any rotation, so its
            // placement cannot be confused with state the round trip below leaves behind.
            await RunPhaseAsync("inspect", () => CheckInspectUpgradeAcrossLeavingPortraitAsync(state, player));
            await RunPhaseAsync("round_trip", () => CheckPortraitLeaveRoundTripAsync(state, player, room));
            await RunPhaseAsync("kaiser", () => CheckTwoKaiserCrabFightsAsync(state, player));
            await RunPhaseAsync("ending_draw", () => CheckFightEndingDuringDrawAsync(state, player));
            _report.Pass("portrait-leave_complete",
                "mid-draw portrait/landscape round trip, inspect across the switch, two Kaiser Crab fits and the " +
                "fight-ending draw all ran");
        }
        finally
        {
            PortraitHandLayout.ReduceEffects = false;
        }
    }

    /// <summary>
    /// Runs one phase; a phase that fails (or times out) is reported and the next one still runs from a fresh fight. The
    /// run's own cancellation is left to the driver.
    /// </summary>
    private async Task RunPhaseAsync(string phase, Func<Task> run)
    {
        try
        {
            // A phase that failed may have left the canvas in landscape, an inspector open or a reward overlay up; the
            // next one starts from portrait with nothing on top of the fight.
            if (!SafeAreaCanvas.IsPortrait) SetPortraitMode(true);
            ClearOverlays();
            await WaitFramesAsync(2);
            await run();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            _report.Fail($"portrait-leave_{phase}_exception", error.GetBaseException().Message);
        }
    }

    // ---- 1. portrait -> landscape -> portrait mid-draw ------------------------------------------------

    private async Task CheckPortraitLeaveRoundTripAsync(RunState state, Player player, NCombatRoom room)
    {
        if (!GodotObject.IsInstanceValid(room) || !room.IsInsideTree() || NCombatRoom.Instance != room)
        {
            _report.Fail("portrait-leave_room_lost", "the fight the round trip started from is gone");
            return;
        }
        await WaitUntilStableAsync(() => IsPlayerPlayPhase() ? CurrentHandSettledSignature() : null,
            "portrait-leave resting portrait hand", TimeSpan.FromSeconds(2.5));

        // The game's own numbers for the HP labels, taken before the switch: the portrait pass stores them as the label's
        // native x and scale, and landscape must write them back.
        var nativeHp = new Dictionary<ulong, (Label Label, float X, float Scale)>();
        foreach (NCreature creature in room.CreatureNodes)
        {
            if (HpLabelOf(creature) is not { } label || !label.HasMeta("portrait_hp_x") || !label.HasMeta("portrait_hp_scale"))
                continue;
            nativeHp[creature.GetInstanceId()] =
                (label, label.GetMeta("portrait_hp_x").AsSingle(), label.GetMeta("portrait_hp_scale").AsSingle());
        }
        _report.Check("portrait-leave_hp_native_captured", nativeHp.Count > 0,
            $"{nativeHp.Count} of {room.CreatureNodes.Count()} creature HP label(s) had the portrait pass's native x/scale");

        // CheckCombatPilesRailAsync compares draw, End Turn, discard in that order; the hand rows check compares
        // End Turn, energy, draw, discard. Both baselines are the portrait rail the piles must return to.
        Rect2[] pilesBefore =
        [
            room.Ui.DrawPile.GetGlobalRect(), room.Ui.EndTurnButton.GetGlobalRect(),
            room.Ui.DiscardPile.GetGlobalRect(),
        ];
        _handRowsRail =
        [
            room.Ui.EndTurnButton.GetGlobalRect(), LauncherEnergyRect(room),
            room.Ui.DrawPile.GetGlobalRect(), room.Ui.DiscardPile.GetGlobalRect(),
        ];
        _handRowsStage = room.CreatureNodes.First(c => c.Entity.IsPlayer).GetGlobalTransform();
        await CaptureAsync("portrait-leave-portrait-before");

        // The turn-start draw is the moment cards move: start it with the game's own End Turn, then catch the frames in
        // which Reduce Effects has taken at least one card's glow or shadow away.
        await _pointer.TapAsync(CenterOf(room.Ui.EndTurnButton));
        await WaitUntilAsync(() => CombatManager.Instance.PlayerActionsDisabled, "the enemy turn before the draw",
            ShortWait);
        bool drawing = await WaitForAsync(
            () => IsPlayerPlayPhase() && (PortraitHandLayout.HandMoving || HandCountSafe() is > 0 and < 5),
            MediumWait);
        if (!drawing)
        {
            _report.Fail("portrait-leave_draw_in_flight",
                $"the turn draw was not caught: phase={IsPlayerPlayPhase()} " +
                $"actionsDisabled={CombatManager.Instance.PlayerActionsDisabled} hand={HandCountSafe()}");
            return;
        }

        bool reduced = await WaitForAsync(() => ReducedCardCount() > 0, TimeSpan.FromSeconds(6));
        _report.Check("portrait-leave_reduce_effects_active", reduced,
            $"{ReducedCardCount()} of {HandCountSafe()} drawn card(s) were without their glow/shadow when the switch began");
        await CaptureAsync("portrait-leave-draw-in-flight");

        // The live Portrait Mode switch: PortraitLayout.Apply, the same call the launcher's Graphics row makes.
        SetPortraitMode(false);
        await WaitFramesAsync(2);
        _report.Check("portrait-leave_landscape_canvas", !SafeAreaCanvas.IsPortrait,
            $"portrait={SafeAreaCanvas.IsPortrait} window={DisplayServer.WindowGetSize()} canvas={_tree.Root.ContentScaleSize} visible={GetViewportSize()}");
        await WaitSecondsAsync(0.8);
        await CaptureAsync("portrait-leave-landscape-mid-draw");

        CheckLandscapeHandEffects(room);
        CheckLandscapePiles(room);
        CheckLandscapeMetadata(room);
        CheckLandscapeHp(nativeHp);

        SetPortraitMode(true);
        await WaitFramesAsync(4);
        _report.Check("portrait-leave_portrait_returned", SafeAreaCanvas.IsPortrait,
            $"portrait={SafeAreaCanvas.IsPortrait} canvas={_tree.Root.ContentScaleSize} visible={GetViewportSize()}");
        await WaitUntilStableAsync(() => IsPlayerPlayPhase() ? CurrentHandSettledSignature() : null,
            "portrait-leave hand after the return", TimeSpan.FromSeconds(2.5));
        await CaptureAsync("portrait-leave-portrait-return");

        await CheckCombatPilesRailAsync(room, pilesBefore);
        List<NHandCardHolder> cards = GetHand().ActiveHolders
            .Where(holder => GodotObject.IsInstanceValid(holder) && holder.IsVisibleInTree()).ToList();
        if (cards.Count == 0)
            _report.Fail("portrait-leave_hand_rows", "no visible hand card after the return");
        else
            CheckHandRowsLayout(cards.Count, cards, room);
    }

    private void CheckLandscapeHandEffects(NCombatRoom room)
    {
        List<NHandCardHolder> holders = room.Ui.Hand.ActiveHolders
            .Where(holder => GodotObject.IsInstanceValid(holder)).ToList();
        int checkedCards = 0, hiddenGlow = 0, hiddenShadow = 0, boosted = 0, missing = 0;
        float widest = 0f;
        string firstProblem = "";
        foreach (NHandCardHolder holder in holders)
        {
            if (holder.CardNode is not NCard card || !GodotObject.IsInstanceValid(card)) continue;
            Control? body = card.GetNodeOrNull<Control>("%CardContainer") ?? card.GetNodeOrNull<Control>("CardContainer");
            if (body == null)
            {
                missing++;
                continue;
            }
            checkedCards++;
            Control? highlight = card.GetNodeOrNull<Control>("%Highlight") ?? body.GetNodeOrNull<Control>("Highlight");
            CanvasItem? shadow = body.GetNodeOrNull<CanvasItem>("Shadow");
            if (highlight == null) missing++;
            else if (!highlight.Visible)
            {
                hiddenGlow++;
                firstProblem = $"glow hidden on {card.Name}";
            }
            if (shadow == null) missing++;
            else if (!shadow.Visible)
            {
                hiddenShadow++;
                firstProblem = $"shadow hidden on {card.Name}";
            }
            if (highlight?.Material is ShaderMaterial material)
            {
                float width = material.GetShaderParameter("width").AsSingle();
                widest = MathF.Max(widest, width);
                if (width > 0.09f)
                {
                    boosted++;
                    firstProblem = $"glow width {width:F3} on {card.Name}";
                }
            }
        }
        _report.Check("portrait-leave_landscape_hand_effects",
            checkedCards > 0 && missing == 0 && hiddenGlow == 0 && hiddenShadow == 0,
            $"{checkedCards} hand card(s): glow hidden={hiddenGlow}, shadow hidden={hiddenShadow}, missing nodes={missing}" +
            (firstProblem.Length > 0 ? $"; first={firstProblem}" : ""));
        _report.Check("portrait-leave_landscape_highlight_width", checkedCards > 0 && boosted == 0,
            $"widest glow={widest:F3} (native 0.075; the portrait playable boost is 0.095); boosted={boosted}");
    }

    private void CheckLandscapePiles(NCombatRoom room)
    {
        Control[] piles = [room.Ui.DrawPile, room.Ui.DiscardPile, room.Ui.ExhaustPile];
        string[] names = ["draw", "discard", "exhaust"];
        var offenders = new List<string>();
        for (int i = 0; i < piles.Length; i++)
        {
            if (PhoneHitArea.Of(piles[i]) is { } area)
                offenders.Add($"{names[i]} touch area {area.GetGlobalRect()}");
        }
        _report.Check("portrait-leave_landscape_pile_hit_areas_removed", offenders.Count == 0,
            offenders.Count == 0
                ? "draw, discard and exhaust have no PhoneHitArea in landscape"
                : string.Join("; ", offenders));
    }

    private void CheckLandscapeMetadata(NCombatRoom room)
    {
        var offenders = new List<string>();
        foreach (NCreature creature in room.CreatureNodes)
        {
            if (!GodotObject.IsInstanceValid(creature)) continue;
            if (creature.HasMeta("portrait_feet_y"))
                offenders.Add($"creature {creature.Name}: portrait_feet_y");
            if (creature.HasMeta("portrait_hp_shift_capped"))
                offenders.Add($"creature {creature.Name}: portrait_hp_shift_capped");
        }
        foreach (string key in PortraitRoomMetaNames)
            if (room.HasMeta(key)) offenders.Add($"room: {key}");

        Control? sceneContainer = room.GetNodeOrNull<Control>("%CombatSceneContainer") ??
                                  room.GetNodeOrNull<Control>("CombatSceneContainer");
        Control? bgContainer = sceneContainer?.GetNodeOrNull<Control>("%BgContainer") ??
                               sceneContainer?.GetNodeOrNull<Control>("BgContainer");
        if (bgContainer != null)
        {
            foreach (string key in PortraitBackgroundContainerMetaNames)
                if (bgContainer.HasMeta(key)) offenders.Add($"%BgContainer: {key}");
            if (bgContainer.GetChildren().OfType<Control>().FirstOrDefault() is { } bgRoot)
                foreach (string key in PortraitBackgroundRootMetaNames)
                    if (bgRoot.HasMeta(key)) offenders.Add($"background {bgRoot.Name}: {key}");
        }
        else offenders.Add("no %BgContainer found");
        _report.Check("portrait-leave_landscape_metadata_removed", offenders.Count == 0,
            offenders.Count == 0
                ? "no portrait_* metadata remains on the room, the background or the creatures"
                : string.Join("; ", offenders));
    }

    private void CheckLandscapeHp(Dictionary<ulong, (Label Label, float X, float Scale)> nativeHp)
    {
        var offenders = new List<string>();
        int checkedLabels = 0;
        foreach ((ulong id, (Label label, float x, float scale)) in nativeHp)
        {
            if (!GodotObject.IsInstanceValid(label))
            {
                offenders.Add($"{id}: label gone");
                continue;
            }
            checkedLabels++;
            if (MathF.Abs(label.Position.X - x) > 0.5f)
                offenders.Add($"{label.Name}.X {label.Position.X:F1} != native {x:F1}");
            if (MathF.Abs(label.Scale.X - scale) > 0.005f)
                offenders.Add($"{label.Name}.scale {label.Scale.X:F3} != native {scale:F3}");
        }
        _report.Check("portrait-leave_landscape_hp_labels_native",
            checkedLabels > 0 && offenders.Count == 0,
            offenders.Count == 0
                ? $"{checkedLabels} HP label(s) at their native x and scale"
                : string.Join("; ", offenders.Take(12)));
    }

    private static Label? HpLabelOf(NCreature creature)
    {
        Control? display = creature.GetNodeOrNull<Control>("%HealthBar") ?? creature.GetNodeOrNull<Control>("HealthBar");
        Control? bar = display?.GetNodeOrNull<Control>("%HealthBar") ?? display?.GetNodeOrNull<Control>("HealthBar");
        return bar?.GetNodeOrNull<Label>("%HpLabel") ?? bar?.GetNodeOrNull<Label>("HpLabel");
    }

    // ---- 4. the inspect screen's upgrade box across the switch ----------------------------------------

    private async Task CheckInspectUpgradeAcrossLeavingPortraitAsync(RunState state, Player player)
    {
        NCombatRoom room = NCombatRoom.Instance ?? throw new InvalidOperationException("The Nibbits fight ended early.");
        await WaitUntilStableAsync(() => IsPlayerPlayPhase() ? CurrentHandSettledSignature() : null,
            "portrait-leave inspect hand", TimeSpan.FromSeconds(2.5));
        // InspectOpen() asks NGame for the inspect screen, which CREATES it on first call and leaves it visible for its
        // initial close tween. Prime and settle it before the hold, or the predicate would report an open inspector the
        // launcher never opened and the finger would be released before TouchCardMath.HoldSeconds.
        await SettleInspectScreenAsync();
        // The screen's own "View Upgrades" box, before the launcher touches it: its scale, pivot and offsets are what the
        // restore has to put back (the portrait overlay may have grown it while closed, so the scene values are not the
        // only native state).
        Control? nativeBox = NGame.Instance?.GetInspectCardScreen()?.GetNodeOrNull<Control>("%Upgrade");
        (Vector2 Scale, Vector2 Pivot, float Left, float Top, float Right, float Bottom)? nativeUpgrade = nativeBox is null
            ? null
            : (nativeBox.Scale, nativeBox.PivotOffset, nativeBox.OffsetLeft, nativeBox.OffsetTop,
                nativeBox.OffsetRight, nativeBox.OffsetBottom);
        NPlayerHand hand = GetHand();
        NHandCardHolder card = RightmostHolder(hand);
        Vector2 point = PointOnHolder(card, 0.5f, 0.5f);
        int inspectsBefore = PhoneTouchCards.InspectsOpened;

        await _pointer.PressAsync(point);
        bool openedWhileDown = await WaitForAsync(
            () => PhoneTouchCards.InspectsOpened > inspectsBefore, TimeSpan.FromSeconds(2.5));
        await _pointer.ReleaseAsync(point);
        bool opened = openedWhileDown && await WaitForAsync(InspectOpen, TimeSpan.FromSeconds(2));
        _report.Check("portrait-leave_inspect_open", opened,
            $"whileDown={openedWhileDown} afterRelease={InspectOpen()} " +
            $"inspects={PhoneTouchCards.InspectsOpened - inspectsBefore}");
        if (!opened || NGame.Instance?.GetInspectCardScreen() is not { } screen)
        {
            _report.Fail("portrait-leave_inspect_upgrade_placed", "the hold did not open the native inspect screen");
            return;
        }

        await WaitForInspectorCardAsync(screen);
        Control? upgrade = screen.GetNodeOrNull<Control>("%Upgrade");
        (Rect2 cardRect, Rect2 upgradeRect) = InspectCardAndUpgrade(screen);
        bool centred = Math.Abs(upgradeRect.GetCenter().X - cardRect.GetCenter().X) <= 2f;
        bool below = upgradeRect.Position.Y >= cardRect.End.Y - 0.5f &&
                     upgradeRect.Position.Y <= cardRect.End.Y + 40f;
        bool tall = upgradeRect.Size.Y >= PortraitBands.MinTarget - 1f;
        bool clear = !upgradeRect.Intersects(cardRect);
        bool placed = upgrade is not null && centred && below && tall && clear;
        _report.Check("portrait-leave_inspect_upgrade_placed", placed,
            $"upgrade={upgradeRect} card={cardRect} centred={centred} below={below} tall={tall} clear={clear} " +
            $"meta={(upgrade?.HasMeta(PhoneTouchCards.CombatInspectMeta) ?? false)}");
        await CaptureAsync("portrait-leave-inspect-portrait");

        SetPortraitMode(false);
        bool restored = await WaitForAsync(
            () => upgrade is not null && GodotObject.IsInstanceValid(upgrade) && NativeInspectUpgrade(upgrade, nativeUpgrade),
            TimeSpan.FromSeconds(3));
        _report.Check("portrait-leave_inspect_upgrade_native_after_landscape", restored,
            upgrade is null || !GodotObject.IsInstanceValid(upgrade)
                ? "the upgrade box went away with the screen"
                : $"scale={upgrade.Scale} offsets={upgrade.OffsetLeft},{upgrade.OffsetTop},{upgrade.OffsetRight}," +
                  $"{upgrade.OffsetBottom} pivot={upgrade.PivotOffset} " +
                  $"meta={upgrade.HasMeta(PhoneTouchCards.CombatInspectMeta)} " +
                  $"native={nativeUpgrade}");
        await CaptureAsync("portrait-leave-inspect-landscape");

        // Put the inspector away in landscape, then back to portrait for the rest of the run.
        if (InspectOpen())
        {
            await _pointer.TapAsync(new Vector2(60f, 420f));
            await WaitForAsync(() => !InspectOpen(), TimeSpan.FromSeconds(2));
        }
        if (InspectOpen()) ClearOverlays();
        SetPortraitMode(true);
        await WaitFramesAsync(4);
        _report.Check("portrait-leave_inspect_portrait_returned", SafeAreaCanvas.IsPortrait,
            $"portrait={SafeAreaCanvas.IsPortrait} canvas={_tree.Root.ContentScaleSize} visible={GetViewportSize()}");
    }

    /// <summary>
    /// The native inspect screen's "View Upgrades" box: the placement marker gone, and scale, pivot and offsets back at
    /// the values the screen had before the launcher placed it.
    /// </summary>
    private static bool NativeInspectUpgrade(Control upgrade,
        (Vector2 Scale, Vector2 Pivot, float Left, float Top, float Right, float Bottom)? native) =>
        !upgrade.HasMeta(PhoneTouchCards.CombatInspectMeta)
        && (native is not { } box
            || (upgrade.Scale.DistanceTo(box.Scale) < 0.001f
                && upgrade.PivotOffset.DistanceTo(box.Pivot) < 0.01f
                && MathF.Abs(upgrade.OffsetLeft - box.Left) < 0.5f
                && MathF.Abs(upgrade.OffsetTop - box.Top) < 0.5f
                && MathF.Abs(upgrade.OffsetRight - box.Right) < 0.5f
                && MathF.Abs(upgrade.OffsetBottom - box.Bottom) < 0.5f));

    // ---- 2. two Kaiser Crab fights in one portrait session --------------------------------------------

    private async Task CheckTwoKaiserCrabFightsAsync(RunState state, Player player)
    {
        for (int fight = 1; fight <= 2; fight++)
        {
            player.Creature.SetCurrentHpInternal(player.Creature.MaxHp);
            EncounterModel encounter = ModelDb.Encounter<KaiserCrabBoss>().ToMutable();
            await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: encounter, showTransition: false);
            // The fight is identified by its own monsters: the old room may still report the player's play phase while the
            // debug room is being replaced.
            bool entered = await WaitForAsync(
                () => IsPlayerPlayPhase() && NCombatRoom.Instance is { } current
                      && GodotObject.IsInstanceValid(current)
                      && current.CreatureNodes.Any(c => c.Entity.Monster?.GetType().Name == "Crusher")
                      && current.CreatureNodes.Any(c => c.Entity.Monster?.GetType().Name == "Rocket"),
                MediumWait);
            if (!entered)
            {
                _report.Fail($"portrait-leave_kaiser{fight}_entered",
                    "the Kaiser Crab fight did not reach its player phase");
                return;
            }

            NCombatRoom room = NCombatRoom.Instance!;
            // The fit runs on a stage pass after the room's first frame. The game's own skeleton is scale (1,1); only
            // FitBackgroundBossArms writes this node's scale, so a scale away from one is the fit having been applied.
            bool fit = await WaitForAsync(
                () =>
                {
                    if (!GodotObject.IsInstanceValid(room)) return false;
                    Node2D? body = room.Background?.GetNodeOrNull<Node2D>("%KaiserCrab");
                    return body != null && GodotObject.IsInstanceValid(body) && MathF.Abs(body.Scale.X - 1f) > 0.1f;
                },
                TimeSpan.FromSeconds(6));
            await WaitUntilStableAsync(() => IsPlayerPlayPhase() ? CombatSettledSignature() : null,
                $"Kaiser Crab fight {fight} intro", TimeSpan.FromSeconds(2.5));

            Node2D? crab = room.Background?.GetNodeOrNull<Node2D>("%KaiserCrab");
            NCreature me = room.CreatureNodes.First(c => c.Entity.IsPlayer);
            List<NCreature> enemies = room.CreatureNodes
                .Where(c => GodotObject.IsInstanceValid(c) && !c.Entity.IsPlayer && !c.Entity.IsPet).ToList();
            CheckKaiserCrab($"portrait-leave_kaiser{fight}", room, me, enemies);
            _report.Check($"portrait-leave_kaiser{fight}_arm_fit", fit,
                crab is null || !GodotObject.IsInstanceValid(crab)
                    ? "the crab body is not in the background"
                    : $"crab scale={crab.Scale} position={crab.Position} (native scale is one; only the arm fit writes it)");
            await CaptureAsync($"portrait-leave-kaiser-{fight}");

            List<Creature> victims = enemies.Where(c => c.Entity.IsAlive).Select(c => c.Entity).ToList();
            if (victims.Count > 0) await CreatureCmd.Kill(victims, force: true);
            // CreatureCmd.Kill collects the deaths but only the turn loop evaluates the win condition; ask the game for
            // that check the way the loop (and AutoSlay) does, or the fight would sit there with no enemies alive.
            await CombatManager.Instance.CheckWinCondition();
            bool over = await WaitForAsync(() => !CombatManager.Instance.IsInProgress, TimeSpan.FromSeconds(20));
            if (!over)
            {
                _report.Fail($"portrait-leave_kaiser{fight}_ended", "the Kaiser Crab fight did not end after the kills");
                return;
            }
            // The win opens the reward overlay; it must not stay on top of the next debug room (or the pointer checks).
            await WaitSecondsAsync(0.8);
            ClearOverlays();
            await WaitFramesAsync(2);
        }
    }

    // ---- 3. a fight that ends during the turn draw, then the reward screen ----------------------------

    private async Task CheckFightEndingDuringDrawAsync(RunState state, Player player)
    {
        player.Creature.SetCurrentHpInternal(player.Creature.MaxHp);
        NCombatRoom room = await EnterTouchCombatAsync(state, "portrait-leave-end");
        await WaitUntilStableAsync(() => IsPlayerPlayPhase() ? CurrentHandSettledSignature() : null,
            "portrait-leave ending-draw hand", TimeSpan.FromSeconds(2.5));

        await _pointer.TapAsync(CenterOf(room.Ui.EndTurnButton));
        await WaitUntilAsync(() => CombatManager.Instance.PlayerActionsDisabled, "the enemy turn before the ending draw",
            ShortWait);
        bool drawing = await WaitForAsync(
            () => IsPlayerPlayPhase() && (PortraitHandLayout.HandMoving || HandCountSafe() is > 0 and < 5),
            MediumWait);
        bool reduced = drawing && await WaitForAsync(() => ReducedCardCount() > 0, TimeSpan.FromSeconds(6));
        _report.Check("portrait-leave_end_draw_in_flight", drawing && reduced,
            $"phase={IsPlayerPlayPhase()} actionsDisabled={CombatManager.Instance.PlayerActionsDisabled} " +
            $"hand={HandCountSafe()} reduced={ReducedCardCount()}");
        // With no energy every card in the draw is unplayable, so the portrait pass dims the bodies as well as hiding the
        // arriving cards' glow and shadow: the leave has all three kinds of state to put back.
        await SetEnergyAsync(player, 0m);
        await WaitFramesAsync(3);
        List<Creature> enemies = room.CreatureNodes
            .Where(c => GodotObject.IsInstanceValid(c) && !c.Entity.IsPlayer && !c.Entity.IsPet && c.Entity.IsAlive)
            .Select(c => c.Entity).ToList();
        _report.Check("portrait-leave_end_enemies", enemies.Count > 0, $"{enemies.Count} living enemy/enemies to kill");
        // The game's own kill command, right on the draw's first moving frames, so the fight ends with cards still in the
        // air (the state the leave restore has to clean up). The win condition is the turn loop's step; ask for it here
        // because no turn is running when the kill lands.
        await CreatureCmd.Kill(enemies, force: true);
        await CombatManager.Instance.CheckWinCondition();

        bool over = await WaitForAsync(() => !CombatManager.Instance.IsInProgress, TimeSpan.FromSeconds(20));
        _report.Check("portrait-leave_end_combat_over", over,
            $"combatInProgress={CombatManager.Instance.IsInProgress} hand={HandCountSafe()}");
        bool rewards = await WaitForAsync(
            () => FindAll<NRewardsScreen>(_tree.Root).Any(s => GodotObject.IsInstanceValid(s) && s.IsVisibleInTree()),
            MediumWait);
        _report.Check("portrait-leave_end_rewards_screen", rewards, "the fight's reward screen appeared");
        if (SafeAreaCanvas.IsPortrait && NCombatRoom.Instance?.Ui?.Hand is { } endHand && GodotObject.IsInstanceValid(endHand))
        {
            // The game's combat-end hide (0.8 s) must take both portrait hand rows below the canvas, as in landscape.
            float bottom = GetViewportSize().Y;
            bool hidden = await WaitForAsync(() => endHand.ActiveHolders.All(holder => !GodotObject.IsInstanceValid(holder)
                || !holder.IsVisibleInTree() || holder.Hitbox.GetGlobalRect().Position.Y >= bottom), TimeSpan.FromSeconds(3));
            float highest = endHand.ActiveHolders.Where(holder => GodotObject.IsInstanceValid(holder) && holder.IsVisibleInTree())
                .Select(holder => holder.Hitbox.GetGlobalRect().Position.Y).DefaultIfEmpty(float.PositiveInfinity).Min();
            _report.Check("portrait-leave_end_hand_leaves_screen", hidden,
                $"highest card top={highest:F1} canvas bottom={bottom:F1} hand={endHand.Position}");
        }
        await CaptureAsync("portrait-leave-end-during-draw-rewards");
        if (!rewards) return;

        // The card reward's three choices are NCards: they must draw their glow, shadow and body as the game made them.
        Node? cardReward = FindAll<Control>(_tree.Root).FirstOrDefault(control =>
            control.GetType().FullName == "MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton"
            && control.IsVisibleInTree()
            && control.GetType().GetProperty("Reward")?.GetValue(control)?.GetType().Name == "CardReward");
        cardReward?.Call("ForceClick");
        bool choices = await WaitForAsync(
            () => FindAll<NCardRewardSelectionScreen>(_tree.Root).Any(s => GodotObject.IsInstanceValid(s) && s.IsVisibleInTree()),
            ShortWait);
        _report.Check("portrait-leave_end_card_choices", choices, "the card reward selection opened");
        if (choices)
        {
            NCardRewardSelectionScreen screen =
                FindAll<NCardRewardSelectionScreen>(_tree.Root).First(s => s.IsVisibleInTree());
            await WaitForAsync(() => FindAll<NCard>(screen).Any(c => GodotObject.IsInstanceValid(c) && c.IsVisibleInTree()),
                TimeSpan.FromSeconds(2));
            CheckCardsNativeEffects("portrait-leave_end_reward_cards",
                FindAll<NCard>(screen).Where(c => GodotObject.IsInstanceValid(c) && c.IsVisibleInTree()));
            await CaptureAsync("portrait-leave-end-card-choices");
        }

        // The deck view reuses cards from the same pool: no card there may still carry the fight's dim or hidden effects.
        ClearOverlays();
        await WaitFramesAsync(3);
        NDeckViewScreen deck = NDeckViewScreen.ShowScreen(player) ??
                               throw new InvalidOperationException("The deck view did not open.");
        bool deckCards = await WaitForAsync(
            () => FindAll<NCard>(deck).Any(c => GodotObject.IsInstanceValid(c) && c.IsVisibleInTree()),
            ShortWait);
        _report.Check("portrait-leave_end_deck_view", deckCards && deck.IsVisibleInTree(),
            $"deck cards={FindAll<NCard>(deck).Count(c => GodotObject.IsInstanceValid(c) && c.IsVisibleInTree())}");
        if (deckCards)
        {
            CheckCardsNativeEffects("portrait-leave_end_deck_cards",
                FindAll<NCard>(deck).Where(c => GodotObject.IsInstanceValid(c) && c.IsVisibleInTree()));
            await CaptureAsync("portrait-leave-end-deck-view");
        }
    }

    /// <summary>
    /// No card may still carry what the fight's portrait pass did to it: a hidden glow quad, a hidden shadow, the
    /// portrait playable glow width, or a body dimmed as if the card were unplayable.
    /// </summary>
    private void CheckCardsNativeEffects(string prefix, IEnumerable<NCard> cards)
    {
        int examined = 0, hiddenGlow = 0, hiddenShadow = 0, boosted = 0, dimmed = 0, missing = 0;
        var offenders = new List<string>();
        foreach (NCard card in cards)
        {
            Control? body = card.GetNodeOrNull<Control>("%CardContainer") ?? card.GetNodeOrNull<Control>("CardContainer");
            if (body == null)
            {
                missing++;
                continue;
            }
            examined++;
            Control? highlight = card.GetNodeOrNull<Control>("%Highlight") ?? body.GetNodeOrNull<Control>("Highlight");
            CanvasItem? shadow = body.GetNodeOrNull<CanvasItem>("Shadow");
            if (highlight == null)
            {
                missing++;
            }
            else
            {
                if (!highlight.Visible)
                {
                    hiddenGlow++;
                    offenders.Add($"glow hidden: {card.Name}");
                }
                if (highlight.Material is ShaderMaterial material)
                {
                    float width = material.GetShaderParameter("width").AsSingle();
                    if (width > 0.09f)
                    {
                        boosted++;
                        offenders.Add($"glow width {width:F3}: {card.Name}");
                    }
                }
            }
            if (shadow == null) missing++;
            else if (!shadow.Visible)
            {
                hiddenShadow++;
                offenders.Add($"shadow hidden: {card.Name}");
            }
            if (DimmedBodyChild(body) is { } dim)
            {
                dimmed++;
                offenders.Add($"body dimmed ({dim}): {card.Name}");
            }
        }
        _report.Check(prefix + "_native_effects",
            examined > 0 && missing == 0 && hiddenGlow == 0 && hiddenShadow == 0 && boosted == 0 && dimmed == 0,
            $"{examined} card(s): glow hidden={hiddenGlow}, shadow hidden={hiddenShadow}, boosted={boosted}, " +
            $"dimmed={dimmed}, missing nodes={missing}" +
            (offenders.Count > 0 ? "; " + string.Join("; ", offenders.Take(10)) : ""));
    }

    private static string? DimmedBodyChild(Control body)
    {
        for (int i = 0; i < body.GetChildCount(); i++)
        {
            if (body.GetChild(i) is not CanvasItem item || item.IsQueuedForDeletion()) continue;
            string name = item.Name.ToString();
            if (PortraitSkippedCardBodyChildren.Contains(name)) continue;
            Color modulate = item.Modulate;
            // The unplayable dim multiplies each child's colour by 0.54; on a card's white body art that is a flat
            // (0.54, 0.54, 0.54). The game's own tints on these children are coloured or translucent, never that grey.
            if (modulate.A >= 0.99f
                && MathF.Abs(modulate.R - modulate.G) < 0.02f && MathF.Abs(modulate.G - modulate.B) < 0.02f
                && modulate.R > 0.50f && modulate.R < 0.58f)
                return $"{name}={modulate}";
        }
        return null;
    }

    // ---- shared helpers -------------------------------------------------------------------------------

    /// <summary>
    /// The launcher's live Portrait Mode switch: the same <c>PortraitLayout.Apply</c> the Graphics row calls. The host
    /// window does not rotate; the canvas the game is laid out on changes instead.
    /// </summary>
    private static void SetPortraitMode(bool portrait)
    {
        LauncherHostWiring wiring = LauncherHostWiring.Active
            ?? throw new InvalidOperationException("The launcher host wiring is not active.");
        wiring.ApplyOrientation(portrait);
    }

    private async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        ulong start = Time.GetTicksMsec();
        while (!condition())
        {
            if (Time.GetTicksMsec() - start > (ulong)timeout.TotalMilliseconds) return false;
            await WaitFramesAsync(1);
        }
        return true;
    }

    /// <summary>Visible hand cards whose glow or shadow Reduce Effects has taken away this frame.</summary>
    private int ReducedCardCount()
    {
        if (NCombatRoom.Instance?.Ui?.Hand is not { } hand || !GodotObject.IsInstanceValid(hand)) return 0;
        int hidden = 0;
        foreach (NHandCardHolder holder in hand.ActiveHolders)
        {
            if (!GodotObject.IsInstanceValid(holder) || holder.CardNode is not NCard card ||
                !GodotObject.IsInstanceValid(card))
                continue;
            Control? body = card.GetNodeOrNull<Control>("%CardContainer") ?? card.GetNodeOrNull<Control>("CardContainer");
            if (body == null) continue;
            Control? highlight = card.GetNodeOrNull<Control>("%Highlight") ?? body.GetNodeOrNull<Control>("Highlight");
            CanvasItem? shadow = body.GetNodeOrNull<CanvasItem>("Shadow");
            if (highlight is { Visible: false } || shadow is { Visible: false }) hidden++;
        }
        return hidden;
    }

    private static void ClearOverlays()
    {
        try
        {
            Type? overlayType = typeof(NGame).Assembly.GetType(
                "MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack");
            object? instance = overlayType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            overlayType?.GetMethod("Clear")?.Invoke(instance, null);
        }
        catch (Exception error)
        {
            Log.Warn($"[TEST] portrait-leave: the overlay stack could not be cleared: {error.GetBaseException().Message}");
        }
    }
}
