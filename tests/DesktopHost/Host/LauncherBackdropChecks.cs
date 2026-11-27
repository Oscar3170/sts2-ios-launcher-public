using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// <c>stage-backdrops</c> (launcher host): the fight backdrops that are not framed like the rest of their act, and the
/// Architect's ending picture.
/// <para>
/// The Underdocks fights are laid out on the act's narrow pier. Two bosses bring their own background scene that is the
/// same pier art (the Lagavulin Matriarch and the Soul Fysh), so their backdrop must land exactly where a normal
/// Underdocks fight's does: same container scale, same origin and the same drawn art rectangle. The Architect's ending is
/// a combat room whose background is the ending picture rather than an arena: it must cover the whole canvas with the
/// composition centred, and the Architect itself must stay in view.
/// </para>
/// <para>
/// The fixture enters the two bosses, a normal Underdocks reference fight and the Waterfall Giant (the act's other custom
/// arena, which must keep its broad floor), then enters TheArchitect event. On the desktop landscape profile it only
/// checks that no portrait backdrop pass ran and captures the native screens, because landscape keeps the game's own
/// framing.
/// </para>
/// </summary>
internal sealed partial class TestDriver
{
    private const string UnderdocksPierArt = "res://images/rooms/underdocks/underdocks_00.png";
    private const string ArchitectEndingArt = "/architect_victory_bg.png";
    // The backdrop is one frozen transform: an equal pier framing moves it by less than a unit.
    private const float BackdropScaleTolerance = 0.004f;
    private const float BackdropPositionTolerance = 1.5f;
    private const float BackdropSizeTolerance = 1.5f;
    // The ending picture is centred and its whole canvas is covered; a few units of slack is rounding, not framing.
    private const float EndingCoverTolerance = 1.5f;
    private const float EndingCentreTolerance = 54f;

    private readonly record struct BackdropProbe(
        Vector2 Scale,
        Vector2 Origin,
        Rect2 ArtRect,
        string ArtPath,
        float ArtBottom,
        bool Pier,
        bool CustomArena,
        bool FloorAligned);

    /// <summary>Entry for <see cref="TouchSpire.LauncherHost.LauncherHostRunner"/>'s <c>stage-backdrops</c> scenario.</summary>
    internal static async Task RunBackdropScenarioAsync(TestOptions options, TestReport report,
        ScreenshotService screenshots, VirtualPointer pointer)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            Log.Error("Test harness requires a SceneTree.");
            return;
        }

        await new TestDriver(tree, options, report, screenshots, pointer).RunBackdropScenarioInternalAsync();
    }

    private async Task RunBackdropScenarioInternalAsync()
    {
        _cancellation.CancelAfter(TimeSpan.FromMinutes(15));
        await WaitForMainMenuPreloadAsync();
        RunState state = await StartRunAsync();
        Player me = state.Players[0];
        me.Creature.SetMaxHpInternal(9999);
        me.Creature.SetCurrentHpInternal(me.Creature.MaxHp);
        Directory.CreateDirectory(_options.OutputDirectory);
        bool portrait = SafeAreaCanvas.IsPortrait;
        _report.Pass("backdrops_host_layout", portrait
            ? "portrait: the Underdocks bosses and the Architect's ending are measured against the act's own framing"
            : "landscape: native captures only (no portrait backdrop assertions)");

        await ConfigureTourActAsync(state, "Underdocks");
        if (!portrait)
        {
            foreach (string name in new[] { "CorpseSlugsNormal", "LagavulinMatriarchBoss", "SoulFyshBoss", "WaterfallGiantBoss" })
            {
                await EnterBackdropRoomAsync(state, me, name);
                NCombatRoom room = NCombatRoom.Instance
                    ?? throw new InvalidOperationException($"Combat room disappeared during {name}.");
                CheckLandscapeNative(room, name);
                await CaptureAsync("backdrop_" + name);
            }
            var (_, endingCombat, _, _) = await EnterArchitectEndingAsync();
            CheckLandscapeNative(endingCombat, "TheArchitectEnding");
            await CaptureAsync("backdrop_TheArchitectEnding");
            _report.Pass("backdrops_landscape_native",
                "the Underdocks fights and the Architect's ending keep the game's own framing in landscape");
            return;
        }

        BackdropProbe reference = await ProbeEncounterBackdropAsync(state, me, "CorpseSlugsNormal");
        await CaptureAsync("backdrop_CorpseSlugsNormal");
        _report.Check("backdrop_underdocks_reference_is_pier", reference.Pier,
            $"pier={reference.Pier} art={reference.ArtPath} scale={reference.Scale} origin={reference.Origin}");
        _report.Check("backdrop_underdocks_reference_uses_act_art", reference.ArtPath == UnderdocksPierArt,
            $"art={reference.ArtPath}");

        foreach (string boss in new[] { "LagavulinMatriarchBoss", "SoulFyshBoss" })
        {
            BackdropProbe probe = await ProbeEncounterBackdropAsync(state, me, boss);
            await CaptureAsync("backdrop_" + boss);
            CheckBossBackdrop(boss, reference, probe);
        }

        // The other Underdocks boss with a custom background is a real arena: the pier rule must not reach it, and the
        // broad floor keeps ending at the hand row.
        BackdropProbe giant = await ProbeEncounterBackdropAsync(state, me, "WaterfallGiantBoss");
        await CaptureAsync("backdrop_WaterfallGiantBoss");
        float canvasHeight = GetViewportSize().Y;
        float capHeight = PortraitLayout.ComputeCapHeight(_tree.Root);
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, capHeight + PortraitBands.RunContentTop);
        _report.Check("WaterfallGiantBoss_backdrop_keeps_its_own_arena", !giant.Pier && giant.CustomArena,
            $"pier={giant.Pier} customArena={giant.CustomArena} art={giant.ArtPath}");
        _report.Check("WaterfallGiantBoss_backdrop_is_its_waterfall_art",
            giant.ArtPath.Contains("waterfallgiant", StringComparison.Ordinal), $"art={giant.ArtPath}");
        _report.Check("WaterfallGiantBoss_backdrop_still_ends_at_the_hand_row",
            giant.FloorAligned && MathF.Abs(giant.ArtBottom - bands.HandTopY) <= BackdropSizeTolerance,
            $"floorAligned={giant.FloorAligned} artBottom={giant.ArtBottom:F1} handTop={bands.HandTopY:F1}");

        await CheckArchitectEndingAsync();
    }

    /// <summary>Landscape keeps the game's own background: no portrait underlay and no portrait framing facts on the room.</summary>
    private void CheckLandscapeNative(NCombatRoom room, string what)
    {
        Control? bgContainer = room.GetNodeOrNull<Control>("%BgContainer");
        Control? arena = bgContainer?.GetChildren().OfType<Control>().FirstOrDefault();
        bool underlay = room.GetNodeOrNull<TextureRect>("PortraitCombatBackdrop") is not null;
        bool pierMeta = arena?.HasMeta("portrait_pier") == true;
        bool customMeta = arena?.HasMeta("portrait_custom_arena") == true;
        _report.Check(what + "_landscape_native_backdrop", !underlay && !pierMeta && !customMeta,
            $"underlay={underlay} pierMeta={pierMeta} customMetric={customMeta}");
    }

    /// <summary>Enters one Underdocks fight and reads the backdrop the launcher froze for it.</summary>
    private async Task<BackdropProbe> ProbeEncounterBackdropAsync(RunState state, Player me, string encounterName)
    {
        await EnterBackdropRoomAsync(state, me, encounterName);
        NCombatRoom room = NCombatRoom.Instance
            ?? throw new InvalidOperationException($"Combat room disappeared during {encounterName}.");
        await WaitFramesAsync(4);
        return ProbeBackdrop(room, encounterName);
    }

    private async Task EnterBackdropRoomAsync(RunState state, Player me, string encounterName)
    {
        EncounterModel encounter = state.Act.AllEncounters.First(e => e.GetType().Name == encounterName).ToMutable();
        me.Creature.SetCurrentHpInternal(me.Creature.MaxHp);
        await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: encounter, showTransition: false);
        await WaitUntilAsync(IsPlayerPlayPhase, encounterName + " player phase", TimeSpan.FromSeconds(45));
        await WaitUntilStableAsync(StageIntroSignature, encounterName + " intro settle", TimeSpan.FromSeconds(4.5));
    }

    /// <summary>The framed backdrop of a fight: the container transform, the art's drawn rectangle, and its facts.</summary>
    private BackdropProbe ProbeBackdrop(NCombatRoom room, string what)
    {
        Control bgContainer = room.GetNode<Control>("%BgContainer");
        Control? arena = bgContainer.GetChildren().OfType<Control>().FirstOrDefault();
        if (arena is null)
        {
            _report.Fail(what + "_backdrop_arena", "no arena control under %BgContainer");
            return default;
        }
        TextureRect? art = LargestBackgroundArt(arena, 5);
        if (art is null)
        {
            _report.Fail(what + "_backdrop_art", "no drawn background texture found under the arena");
            return default;
        }
        bool pier = arena.HasMeta("portrait_pier") && arena.GetMeta("portrait_pier").AsBool();
        bool customArena = arena.HasMeta("portrait_custom_arena") && arena.GetMeta("portrait_custom_arena").AsBool();
        bool floorAligned = arena.HasMeta("portrait_floor_aligned") && arena.GetMeta("portrait_floor_aligned").AsBool();
        float artBottom = arena.HasMeta("portrait_art_bottom") ? arena.GetMeta("portrait_art_bottom").AsSingle() : float.NaN;
        return new BackdropProbe(
            bgContainer.Scale,
            bgContainer.GlobalPosition,
            art.GetGlobalRect(),
            art.Texture?.ResourcePath ?? string.Empty,
            artBottom,
            pier,
            customArena,
            floorAligned);
    }

    /// <summary>The same rule the launcher frames a backdrop by: the largest drawn background texture.</summary>
    private static TextureRect? LargestBackgroundArt(Node? node, int depth)
    {
        if (node is null || depth < 0) return null;
        TextureRect? best = null;
        float bestArea = 0f;
        Collect(node, depth, ref best, ref bestArea);
        return best;

        static void Collect(Node node, int depth, ref TextureRect? best, ref float bestArea)
        {
            if (depth < 0) return;
            foreach (Node child in node.GetChildren())
            {
                if (child is TextureRect { Texture: not null } rect && rect.Size.X > 1000f && rect.Size.Y > 300f)
                {
                    float area = rect.Size.X * rect.Size.Y * MathF.Abs(rect.Scale.X * rect.Scale.Y);
                    if (area > bestArea)
                    {
                        best = rect;
                        bestArea = area;
                    }
                }
                Collect(child, depth - 1, ref best, ref bestArea);
            }
        }
    }

    private void CheckBossBackdrop(string name, BackdropProbe reference, BackdropProbe probe)
    {
        bool sameScale = MathF.Abs(probe.Scale.X - reference.Scale.X) <= BackdropScaleTolerance &&
                         MathF.Abs(probe.Scale.Y - reference.Scale.Y) <= BackdropScaleTolerance;
        bool sameOrigin = MathF.Abs(probe.Origin.X - reference.Origin.X) <= BackdropPositionTolerance &&
                          MathF.Abs(probe.Origin.Y - reference.Origin.Y) <= BackdropPositionTolerance;
        bool sameRect = MathF.Abs(probe.ArtRect.Position.X - reference.ArtRect.Position.X) <= BackdropPositionTolerance &&
                        MathF.Abs(probe.ArtRect.Position.Y - reference.ArtRect.Position.Y) <= BackdropPositionTolerance &&
                        MathF.Abs(probe.ArtRect.Size.X - reference.ArtRect.Size.X) <= BackdropSizeTolerance &&
                        MathF.Abs(probe.ArtRect.Size.Y - reference.ArtRect.Size.Y) <= BackdropSizeTolerance;
        _report.Check(name + "_backdrop_uses_the_act_art", probe.ArtPath == UnderdocksPierArt,
            $"art={probe.ArtPath} reference={reference.ArtPath}");
        _report.Check(name + "_backdrop_scale_matches_underdocks", sameScale,
            $"scale={probe.Scale} reference={reference.Scale}");
        _report.Check(name + "_backdrop_origin_matches_underdocks", sameOrigin,
            $"origin={probe.Origin} reference={reference.Origin}");
        _report.Check(name + "_backdrop_rect_matches_underdocks", sameRect,
            $"art={probe.ArtRect} reference={reference.ArtRect}");
        _report.Check(name + "_backdrop_art_bottom_matches_underdocks",
            MathF.Abs(probe.ArtBottom - reference.ArtBottom) <= BackdropSizeTolerance,
            $"artBottom={probe.ArtBottom:F1} reference={reference.ArtBottom:F1}");
        _report.Check(name + "_backdrop_is_the_pier", probe.Pier,
            $"pier={probe.Pier} customArena={probe.CustomArena} art={probe.ArtPath}");
        _report.Check(name + "_backdrop_not_a_custom_arena", !probe.CustomArena,
            $"customArena={probe.CustomArena} pier={probe.Pier}");
    }

    private async Task CheckArchitectEndingAsync()
    {
        (NEventRoom eventRoom, NCombatRoom combat, Control bgContainer, Control arena) = await EnterArchitectEndingAsync();
        Vector2 canvas = GetViewportSize();
        await CaptureAsync("backdrop_TheArchitectEnding");

        TextureRect? art = FindTexturedRect(arena, ArchitectEndingArt);
        if (art is null)
        {
            _report.Fail("architect_ending_art", $"the ending picture ({ArchitectEndingArt}) is not drawn under the arena");
            return;
        }
        Rect2 drawn = art.GetGlobalRect();
        float leftBand = MathF.Max(0f, drawn.Position.X);
        float topBand = MathF.Max(0f, drawn.Position.Y);
        float rightBand = MathF.Max(0f, canvas.X - drawn.End.X);
        float bottomBand = MathF.Max(0f, canvas.Y - drawn.End.Y);
        bool covers = leftBand <= EndingCoverTolerance && topBand <= EndingCoverTolerance &&
                      rightBand <= EndingCoverTolerance && bottomBand <= EndingCoverTolerance;
        _report.Check("architect_ending_backdrop_covers_canvas", covers,
            $"art={drawn} canvas={canvas} bands=l{leftBand:F0} t{topBand:F0} r{rightBand:F0} b{bottomBand:F0}");
        Vector2 centre = drawn.GetCenter();
        _report.Check("architect_ending_backdrop_centred",
            MathF.Abs(centre.X - canvas.X * 0.5f) <= EndingCentreTolerance &&
            MathF.Abs(centre.Y - canvas.Y * 0.5f) <= EndingCentreTolerance,
            $"centre={centre} canvasCentre={canvas * 0.5f} art={drawn}");

        // The ending's subject is the Architect, a real creature of the event's combat room: it has to sit inside the view.
        NCreature? architect = combat.CreatureNodes.FirstOrDefault(c => c.Entity.IsMonster);
        if (architect is null)
        {
            _report.Fail("architect_ending_subject", "the ending's combat room has no enemy creature");
            return;
        }
        Rect2 body = architect.Visuals.Bounds.GetGlobalRect();
        var canvasRect = new Rect2(Vector2.Zero, canvas);
        _report.Check("architect_ending_subject_in_view",
            body.Size.X > 1f && body.Size.Y > 1f && canvasRect.Intersects(body),
            $"architect={body} canvas={canvas}");
        _report.Pass("architect_ending_loaded", $"event={eventRoom.Name} arena={arena.Name} art={drawn}");
    }

    private async Task<(NEventRoom EventRoom, NCombatRoom Combat, Control BgContainer, Control Arena)> EnterArchitectEndingAsync()
    {
        await RunManager.Instance.EnterRoomDebug(RoomType.Event, model: ModelDb.Event<TheArchitect>(), showTransition: false);
        await WaitUntilAsync(
            () => NEventRoom.Instance is { } room && GodotObject.IsInstanceValid(room) && room.Layout is NCombatEventLayout
                { EmbeddedCombatRoom: { } combat } && combat.GetNodeOrNull<Control>("%BgContainer") is not null,
            "architect ending scene", TimeSpan.FromSeconds(45));
        // The event's opening page and its dialogue bubble settle over the first second; the backdrop pass ran when the
        // embedded combat room was added.
        await WaitSecondsAsync(1.5);
        NEventRoom eventRoom = NEventRoom.Instance!;
        NCombatEventLayout layout = (NCombatEventLayout)eventRoom.Layout!;
        NCombatRoom combat = layout.EmbeddedCombatRoom
            ?? throw new InvalidOperationException("The Architect's event has no embedded combat room.");
        Control bgContainer = combat.GetNode<Control>("%BgContainer");
        Control arena = bgContainer.GetChildren().OfType<Control>().FirstOrDefault()
            ?? throw new InvalidOperationException("The Architect's ending has no arena control under %BgContainer.");
        _report.Pass("architect_ending_loaded_room",
            $"event={eventRoom.Name} combat={combat.Name} arena={arena.Name} mode={combat.Mode}");
        return (eventRoom, combat, bgContainer, arena);
    }

    private static TextureRect? FindTexturedRect(Node? node, string pathSuffix)
    {
        if (node is null) return null;
        foreach (Node child in node.GetChildren())
        {
            if (child is TextureRect { Texture: not null } rect && rect.Texture.ResourcePath.EndsWith(pathSuffix, StringComparison.Ordinal))
                return rect;
            if (FindTexturedRect(child, pathSuffix) is { } found) return found;
        }
        return null;
    }
}
