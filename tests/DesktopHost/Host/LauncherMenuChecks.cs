using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using STS2Mobile.DesktopHost;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The launcher host's answers for the shared menu/settings/pause/game-over scenarios, with the launcher's thresholds:
/// 127 units are 44 points on the 1080 unit canvas (<see cref="PortraitBands.MinTarget"/>), numbers from the original
/// 1680-wide reference canvas are converted by 1080/1680 = 0.643, and a control's target is its drawn rectangle merged with
/// the launcher's larger <see cref="PhoneHitArea"/>.
/// </summary>
internal sealed partial class TestDriver
{
    private partial float HostTouchTargetMinimum() => PortraitBands.MinTarget;

    private partial Rect2 HostTouchTargetBox(Control control)
    {
        Rect2 rect = control.GetGlobalRect();
        if (PhoneHitArea.Of(control) is { } area && area.IsVisibleInTree()) rect = rect.Merge(area.GetGlobalRect());
        return rect;
    }

    // No settings exemptions on this host: its settings controls (paginator arrows, dropdowns) are gated at 127 units like
    // any other touch target.
    private partial string[] HostSettingsGateAllowlist() => Array.Empty<string>();

    /// <summary>
    /// T5 on this host means every interactive control has a 44 point target, so the gate's native-size exemptions for back
    /// buttons, gold arrows and dropdown rows are dropped: those are padded to 127 units and measured, and fail until they are.
    /// Only the game over summary's award chips keep their shared exemption (their pads are checked by
    /// <c>game_over_summary_chip_targets</c>).
    /// </summary>
    private partial bool HostAllowsTouchTargetExemption(string entry) =>
        entry != NativeBackAllow && entry != NativeArrowAllow && entry != DropdownRowsAllow;

    // ---- menus ---------------------------------------------------------------------------------------

    // ---- the live menu backdrop ----------------------------------------------------------------------
    //
    // The requirement's own framing, duplicated from the art review's geometry rather than read back from the launcher: a
    // 2304 square master, the live 2560 x 1200 stage at (157, 23), a camera that fills the canvas height and centres the
    // tower (x = 1451), generated water from row 1038 to the master's bottom, and the title at the phone reference's
    // (390 x 924) 16 / 126 / 259 units with a 10 unit gap to the tower's x = 1678 silhouette.

    private const string MenuWaterNodeName = "PhoneMainMenuWater";
    private const float MenuMasterSize = 2304f;
    private const float MenuNativeLeft = 157f, MenuNativeTop = 23f, MenuNativeWidth = 2560f, MenuNativeHeight = 1200f;
    private const float MenuCameraCentreX = 1451f, MenuWaterTop = 1038f, MenuTowerLeft = 1678f;
    private const float MenuWaterTextureWidth = 2304f, MenuWaterTextureHeight = 1266f;
    private const float MenuLogoReferenceWidth = 390f, MenuLogoReferenceHeight = 924f;
    private const float MenuLogoLeft = 16f, MenuLogoTop = 126f, MenuLogoWidth = 259f, MenuLogoTowerGap = 10f;

    private static (float Scale, float Left) MenuFraming(Vector2 canvas)
    {
        float scale = canvas.Y / MenuMasterSize;
        float visible = Math.Min(canvas.X / scale, MenuMasterSize);
        float left = Mathf.Clamp(MenuCameraCentreX - visible * 0.5f, 0f, MenuMasterSize - visible);
        return (scale, -left * scale);
    }

    private static Rect2 MenuStageRect(Vector2 canvas)
    {
        (float scale, float left) = MenuFraming(canvas);
        return new Rect2(left + MenuNativeLeft * scale, MenuNativeTop * scale, MenuNativeWidth * scale, MenuNativeHeight * scale);
    }

    private static Rect2 MenuWaterRect(Vector2 canvas)
    {
        (float scale, float left) = MenuFraming(canvas);
        return new Rect2(left, MenuWaterTop * scale, MenuWaterTextureWidth * scale, MenuWaterTextureHeight * scale);
    }

    private static (Rect2 Logo, float TowerLeft) MenuLogoRect(Vector2 canvas, float cap)
    {
        (float scale, float left) = MenuFraming(canvas);
        float titleLeft = canvas.X * MenuLogoLeft / MenuLogoReferenceWidth;
        float titleTop = Math.Max(canvas.Y * MenuLogoTop / MenuLogoReferenceHeight, cap + titleLeft);
        float preferred = canvas.X * MenuLogoWidth / MenuLogoReferenceWidth;
        float towerLeft = left + MenuTowerLeft * scale;
        float width = Math.Min(preferred, towerLeft - canvas.X * MenuLogoTowerGap / MenuLogoReferenceWidth - titleLeft);
        return (new Rect2(titleLeft, titleTop, width, 0f), towerLeft);
    }

    private static Rect2 MenuNodeDrawnRect(CanvasItem item, Rect2 local)
    {
        Transform2D transform = item.GetGlobalTransformWithCanvas();
        Vector2 a = transform * local.Position;
        Vector2 b = transform * local.End;
        return new Rect2(new Vector2(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y)), (b - a).Abs());
    }

    /// <summary>The portrait canvas the launcher lays out on: the visible rect, with its minimum height as in PortraitLayout.</summary>
    private Vector2 MenuCanvas()
    {
        Vector2 size = GetViewportSize();
        return new Vector2(size.X, Math.Max(size.Y, SafeAreaCanvas.MinPortraitHeight));
    }

    /// <summary>The live stage and water are framed exactly as the registered master asks, and the water reaches the bottom.</summary>
    private bool MenuPortraitFramed(NMainMenu menu, out string detail)
    {
        Vector2 canvas = MenuCanvas();
        var background = menu.GetNodeOrNull<Control>("%MainMenuBg");
        Control? stage = background?.GetNodeOrNull<Control>("BgContainer");
        TextureRect? water = background?.GetNodeOrNull<TextureRect>(MenuWaterNodeName);
        Rect2 expectedStage = MenuStageRect(canvas);
        Rect2 expectedWater = MenuWaterRect(canvas);
        Rect2 stageRect = stage is null ? default : LauncherDrawnRect(stage);
        Rect2 waterRect = water is null ? default : LauncherDrawnRect(water);
        bool framed = stage is not null && water is not null && water.IsVisibleInTree()
            && Mathf.Abs(stageRect.Position.X - expectedStage.Position.X) <= 2f
            && Mathf.Abs(stageRect.Position.Y - expectedStage.Position.Y) <= 2f
            && Mathf.Abs(stageRect.Size.X - expectedStage.Size.X) <= 2f
            && Mathf.Abs(stageRect.Size.Y - expectedStage.Size.Y) <= 2f
            && Mathf.Abs(waterRect.Position.Y - expectedWater.Position.Y) <= 2f
            && Mathf.Abs(waterRect.End.Y - canvas.Y) <= 2f
            && waterRect.Position.X <= 0f && waterRect.End.X >= canvas.X - 0.5f;
        detail = $"stage={stageRect} expectedStage={expectedStage} water={waterRect} expectedWaterTop={expectedWater.Position.Y:F1} canvas={canvas}";
        return framed;
    }

    /// <summary>
    /// The game's own menu in landscape: no generated water node, and the stage, its title holder and the title back at the
    /// scene's anchors, pivot, z and scale (the framing writes all four in portrait).
    /// </summary>
    private static bool MenuLandscapeNative(NMainMenu menu, out string detail)
    {
        var background = menu.GetNodeOrNull<Control>("%MainMenuBg");
        Control? stage = background?.GetNodeOrNull<Control>("BgContainer");
        Control? holder = background?.GetNodeOrNull<Control>("BgContainer/Control");
        Node2D? logo = background?.GetNodeOrNull<Node2D>("BgContainer/Control/Logo");
        bool waterGone = background?.GetNodeOrNull<Node>(MenuWaterNodeName) is null;
        // The native scene anchors the stage at the middle with its 1280 x 600 pivot and draws it at 1.01 or 1.04 (the
        // game's own window-ratio rule); the title holder sits at the stage's middle and the logo at its own -1120,-582.
        bool stageNative = stage is { } s && Mathf.Abs(s.AnchorLeft - 0.5f) < 0.001f && Mathf.Abs(s.AnchorTop - 0.5f) < 0.001f
            && s.PivotOffset.IsEqualApprox(new Vector2(1280f, 600f)) && s.ZIndex == 0
            && Mathf.Abs(s.Scale.X - 1.01f) < 0.031f;
        bool holderNative = holder is { } h && Mathf.Abs(h.AnchorLeft - 0.5f) < 0.001f && h.Scale.IsEqualApprox(Vector2.One);
        bool logoNative = logo is { } l && l.Scale.IsEqualApprox(new Vector2(0.45f, 0.45f));
        detail = $"water={(waterGone ? "none" : "present")} stage anchors={stage?.AnchorLeft}/{stage?.AnchorTop} " +
                 $"pivot={stage?.PivotOffset} scale={stage?.Scale} z={stage?.ZIndex} holderAnchors={holder?.AnchorLeft} " +
                 $"holderScale={holder?.Scale} logoScale={logo?.Scale} logoPos={logo?.Position}";
        return waterGone && stageNative && holderNative && logoNative;
    }

    private static float MenuSpineTrackTime(NMainMenu menu)
    {
        Node2D? spine = menu.GetNodeOrNull<Node2D>("%MainMenuBg/BgContainer/Fg");
        if (spine is null) return -1f;
        GodotObject? state = spine.Call("get_animation_state").AsGodotObject();
        if (state is null) return -1f;
        GodotObject? track = state.Call("get_current", 0).AsGodotObject();
        if (track is null || !track.HasMethod("get_track_time")) return -1f;
        return track.Call("get_track_time").AsSingle();
    }

    /// <summary>
    /// Samples two drawn frames 1.2 s apart at the same 36 x 36 points of two regions: the live art above the water and beside
    /// the menu's widgets (sky, stars and the spire), and a strip of generated water left of the button text. Returns how many
    /// samples changed in each.
    /// </summary>
    private async Task<(int Art, int Water)> MenuFrameChangesAsync(Vector2 canvas)
    {
        await WaitFramesAsync(2);
        using Image first = _screenshots.ReadRenderedFrame("menu_backdrop_frame_a");
        await WaitSecondsAsync(1.2);
        await WaitFramesAsync(2);
        using Image second = _screenshots.ReadRenderedFrame("menu_backdrop_frame_b");
        return (MenuChangedSamples(first, second, canvas, 0.50f, 0.14f, 0.48f, 0.21f),
            MenuChangedSamples(first, second, canvas, 0.03f, 0.60f, 0.13f, 0.26f));
    }

    private static int MenuChangedSamples(Image first, Image second, Vector2 canvas, float left, float top, float width, float height)
    {
        float sx = first.GetWidth() / canvas.X, sy = first.GetHeight() / canvas.Y;
        int changed = 0;
        for (int y = 0; y < 36; y++)
        for (int x = 0; x < 36; x++)
        {
            var point = new Vector2(canvas.X * (left + width * x / 35f), canvas.Y * (top + height * y / 35f));
            int px = Mathf.Clamp((int)(point.X * sx), 0, first.GetWidth() - 1);
            int py = Mathf.Clamp((int)(point.Y * sy), 0, first.GetHeight() - 1);
            Color a = first.GetPixel(px, py), b = second.GetPixel(px, py);
            if (Mathf.Abs(a.R - b.R) > 2f / 255f || Mathf.Abs(a.G - b.G) > 2f / 255f || Mathf.Abs(a.B - b.B) > 2f / 255f)
                changed++;
        }
        return changed;
    }

    /// <summary>
    /// Portrait: the live stage framed on the registered master (the tower stands beside the title), the generated water from
    /// the water row to the canvas's bottom with the buttons drawn over it, and successive drawn frames differing. Landscape:
    /// no water node and the game's own stage back.
    /// </summary>
    private partial async Task CheckMenuBackdropAsync(NMainMenu menu)
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            _report.Check("menu_backdrop_landscape_native", MenuLandscapeNative(menu, out string landscape), landscape);
            return;
        }
        Vector2 canvas = MenuCanvas();
        var background = menu.GetNode<Control>("%MainMenuBg");
        Control? stage = background.GetNodeOrNull<Control>("BgContainer");
        TextureRect? water = background.GetNodeOrNull<TextureRect>(MenuWaterNodeName);
        Rect2 expectedStage = MenuStageRect(canvas);
        Rect2 expectedWater = MenuWaterRect(canvas);
        Rect2 stageRect = stage is null ? default : LauncherDrawnRect(stage);
        Rect2 waterRect = water is null ? default : LauncherDrawnRect(water);
        // The live stage is the master's 2560 x 1200 box at the camera's scale, wide enough to cover the canvas (its Spine
        // art draws above the box's own top edge), and its lower edge reaches past the water's first row (art under the seam).
        _report.Check("menu_backdrop_native_stage_framed",
            stage is not null
            && Mathf.Abs(stageRect.Position.X - expectedStage.Position.X) <= 2f
            && Mathf.Abs(stageRect.Position.Y - expectedStage.Position.Y) <= 2f
            && Mathf.Abs(stageRect.Size.X - expectedStage.Size.X) <= 2f
            && Mathf.Abs(stageRect.Size.Y - expectedStage.Size.Y) <= 2f
            && stageRect.Position.X <= 0f && stageRect.End.X >= canvas.X - 0.5f
            && stageRect.End.Y > expectedWater.Position.Y,
            $"stage={stageRect} expected={expectedStage} canvas={canvas}");

        // The generated water: its own 2304 x 1266 texture, from the master's water row to the canvas's bottom, behind every
        // control (relative z below the stage and the menu) and out of the input path.
        Vector2 texture = water?.Texture?.GetSize() ?? Vector2.Zero;
        _report.Check("menu_backdrop_water_covers_bottom",
            water is not null && water.IsVisibleInTree()
            && texture.IsEqualApprox(new Vector2(MenuWaterTextureWidth, MenuWaterTextureHeight))
            && Mathf.Abs(waterRect.Position.Y - expectedWater.Position.Y) <= 2f
            && Mathf.Abs(waterRect.End.Y - canvas.Y) <= 2f
            && waterRect.Position.X <= 0f && waterRect.End.X >= canvas.X - 0.5f
            && waterRect.Size.Y >= canvas.Y - expectedWater.Position.Y - 2f
            && water.ZIndex < 0 && stage is not null && stage.ZIndex < water.ZIndex
            && water.MouseFilter == Control.MouseFilterEnum.Ignore
            && water.GetParent() == background,
            $"water={waterRect} top={expectedWater.Position.Y:F1} texture={texture} z={water?.ZIndex} stageZ={stage?.ZIndex} " +
            $"filter={water?.MouseFilter}");

        // The title: the reference's left position and width, below the safe cap, ending before the framed tower's silhouette.
        Node2D? logo = background.GetNodeOrNull<Node2D>("BgContainer/Control/Logo");
        Rect2 bounds = logo is not null && logo.HasMeta("phone_main_menu_logo_bounds")
            ? logo.GetMeta("phone_main_menu_logo_bounds").AsRect2() : default;
        Rect2 logoRect = logo is null || bounds.Size.X <= 1f ? default : MenuNodeDrawnRect(logo, bounds);
        (Rect2 expectedLogo, float towerLeft) = MenuLogoRect(canvas, PortraitLayout.ComputeCapHeight(_tree.Root));
        _report.Check("menu_backdrop_title_beside_tower",
            logo is not null && bounds.Size.X > 1f
            && Mathf.Abs(logoRect.Position.X - expectedLogo.Position.X) <= 3f
            && Mathf.Abs(logoRect.Position.Y - expectedLogo.Position.Y) <= 3f
            && Mathf.Abs(logoRect.Size.X - expectedLogo.Size.X) <= 3f
            && towerLeft > canvas.X * 0.60f && towerLeft < canvas.X * 0.90f
            && logoRect.End.X <= towerLeft - canvas.X * 0.02f
            && PortraitSafeRect().Grow(1f).Encloses(logoRect),
            $"logo={logoRect} expectedLeft/top/width={expectedLogo.Position.X:F1}/{expectedLogo.Position.Y:F1}/{expectedLogo.Size.X:F1} " +
            $"towerLeft={towerLeft:F1} bounds={bounds}");

        // The buttons are drawn over the water: the column sits after the background and above the water's z, the lowest
        // row lies on the water, and every row is inside the canvas.
        Control column = menu.GetNode<Control>("%MainMenuTextButtons");
        Control[] rows = column.GetChildren().OfType<Control>().Where(row => row.IsVisibleInTree() && !row.IsQueuedForDeletion()).ToArray();
        Rect2 lowest = rows.Length == 0 ? default : LauncherDrawnRect(rows[^1]);
        _report.Check("menu_backdrop_buttons_over_water",
            rows.Length > 0 && water is not null
            && column.ZIndex > water.ZIndex
            && background.GetIndex() < column.GetIndex()
            && rows.All(row =>
            {
                Rect2 box = LauncherDrawnRect(row);
                return box.Size.X > 2f && box.Position.X >= -0.5f && box.End.X <= canvas.X + 0.5f && box.End.Y <= canvas.Y;
            })
            && waterRect.HasPoint(lowest.GetCenter()) && lowest.Position.Y > expectedWater.Position.Y,
            $"column={column.GetGlobalRect()} rows={rows.Length} lowest={lowest} water={waterRect} z={water?.ZIndex} " +
            $"columnZ={column.ZIndex} backgroundIndex={background.GetIndex()} columnIndex={column.GetIndex()}");

        float trackStart = MenuSpineTrackTime(menu);
        (int artChanged, int waterChanged) = await MenuFrameChangesAsync(canvas);
        float trackEnd = MenuSpineTrackTime(menu);
        // A menu animation that loops inside the window is still live: the track's time either moved on or wrapped back.
        bool trackAdvanced = trackStart < 0f || MathF.Abs(trackEnd - trackStart) > 0.25f;
        _report.Check("menu_backdrop_live_animation", artChanged >= 6 && trackAdvanced,
            $"changedSamples={artChanged}/1296 in the art region; native foreground track {trackStart:F2} -> {trackEnd:F2}");
        _report.Check("menu_backdrop_water_animates", waterChanged >= 40,
            $"changedSamples={waterChanged}/1296 in the generated water strip");
        await CaptureAsync("menu_backdrop_portrait");
    }

    /// <summary>
    /// Controller focus over the menu rows in either orientation: the hidden Quit row is never a neighbour, each visible
    /// focusable row links up and down to its visible neighbour, and the first and last rows stay on themselves.
    /// </summary>
    private partial void CheckMenuFocusChain(NMainMenu menu)
    {
        Control column = menu.GetNode<Control>("%MainMenuTextButtons");
        Control[] focusable = column.GetChildren().OfType<Control>()
            .Where(row => row.IsVisibleInTree() && row.FocusMode != Control.FocusModeEnum.None).ToArray();
        var problems = new List<string>();
        for (int i = 0; i < focusable.Length; i++)
        {
            Control row = focusable[i];
            Control? up = row.FindValidFocusNeighbor(Side.Top);
            Control? down = row.FindValidFocusNeighbor(Side.Bottom);
            Control expectedUp = focusable[i == 0 ? 0 : i - 1];
            Control expectedDown = focusable[i == focusable.Length - 1 ? i : i + 1];
            if (up != expectedUp) problems.Add($"{row.Name}.up={up?.Name ?? "none"} expected={expectedUp.Name}");
            if (down != expectedDown) problems.Add($"{row.Name}.down={down?.Name ?? "none"} expected={expectedDown.Name}");
            if (up?.Name == "QuitButton" || down?.Name == "QuitButton") problems.Add($"{row.Name} links to Quit");
        }
        _report.Check("menu_focus_skips_quit", focusable.Length > 1 && problems.Count == 0,
            problems.Count == 0
                ? $"rows={focusable.Length} chain={string.Join(" > ", focusable.Select(row => row.Name))}"
                : string.Join("; ", problems));
    }

    /// <summary>Portrait only: the visible rows fill the column, so hiding Quit leaves no empty row-sized slot.</summary>
    private partial void CheckMenuColumnRespace(NMainMenu menu)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Control column = menu.GetNode<Control>("%MainMenuTextButtons");
        Control[] rows = column.GetChildren().OfType<Control>().Where(row => row.IsVisibleInTree() && !row.IsQueuedForDeletion()).ToArray();
        if (rows.Length < 2)
        {
            _report.Fail("menu_column_reflows_without_quit", $"only {rows.Length} visible rows");
            return;
        }
        Rect2 columnRect = LauncherDrawnRect(column);
        Rect2 first = LauncherDrawnRect(rows[0]), last = LauncherDrawnRect(rows[^1]);
        float[] gaps = rows.Zip(rows.Skip(1),
            (above, below) => LauncherDrawnRect(below).Position.Y - LauncherDrawnRect(above).End.Y).ToArray();
        bool quitHidden = menu.GetNodeOrNull<Control>("MainMenuTextButtons/QuitButton") is not { } quit || !quit.IsVisibleInTree();
        _report.Check("menu_column_reflows_without_quit",
            quitHidden && Mathf.Abs(first.Position.Y - columnRect.Position.Y) <= 1f
            && Mathf.Abs(last.End.Y - columnRect.End.Y) <= 1f
            && gaps.All(gap => Mathf.Abs(gap - gaps[0]) <= 0.5f) && gaps[0] >= 0f,
            $"rows={rows.Length} column={columnRect} first={first} last={last} " +
            $"gaps={string.Join(",", gaps.Select(gap => gap.ToString("F1")))} quitHidden={quitHidden}");
    }

    /// <summary>
    /// Portrait only: leaving portrait while the menu is open puts the game's own stage and logo back and removes the water;
    /// coming back frames the same master again. Every drawn frame of the switch is sampled: no landscape frame may hold the
    /// water or the portrait stage framing (a queue_free'd node would still draw for that frame).
    /// </summary>
    private partial async Task CheckMenuBackdropRoundTripAsync(NMainMenu menu)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        if (LauncherHostWiring.Active is not { } wiring)
        {
            _report.Skip("menu_backdrop_rotation_round_trip", "the launcher host wiring is not attached");
            return;
        }
        var leaks = new List<string>();
        void SampleDrawn()
        {
            if (SafeAreaCanvas.IsPortrait || !GodotObject.IsInstanceValid(menu)) return;
            var background = menu.GetNodeOrNull<Control>("%MainMenuBg");
            if (background?.GetNodeOrNull<TextureRect>(MenuWaterNodeName) is { } water && water.IsVisibleInTree())
                leaks.Add("water drawn on a landscape frame");
            if (background?.GetNodeOrNull<Control>("BgContainer") is { } stage && Mathf.Abs(stage.AnchorLeft) < 0.001f)
                leaks.Add("portrait stage framing drawn on a landscape frame");
        }

        RenderingServer.FramePostDraw += SampleDrawn;
        try
        {
            wiring.ApplyOrientation(false);
            await WaitFramesAsync(4);
            string landscape = "";
            bool native = !SafeAreaCanvas.IsPortrait && MenuLandscapeNative(menu, out landscape);
            _report.Check("menu_backdrop_landscape_after_switch", native, landscape);
            await CaptureAsync("menu_backdrop_landscape");

            wiring.ApplyOrientation(true);
            bool framed = false;
            string detail = "";
            bool returned = await WaitOrFalseAsync(() =>
            {
                framed = SafeAreaCanvas.IsPortrait && MenuPortraitFramed(menu, out detail);
                return framed;
            }, MediumWait);
            _report.Check("menu_backdrop_portrait_after_switch", returned && framed, detail);
            await WaitFramesAsync(2);
        }
        finally
        {
            RenderingServer.FramePostDraw -= SampleDrawn;
        }
        _report.Check("menu_backdrop_landscape_never_draws_portrait_layers", leaks.Count == 0,
            leaks.Count == 0 ? "no drawn landscape frame held the water or the portrait stage" : string.Join("; ", leaks.Distinct()));
        await CaptureAsync("menu_backdrop_portrait_again");
    }

    private partial void CheckModeCardsLayout(Control modes, Control[] cards)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Rect2 safe = PortraitSafeRect();
        _report.Check("menu_mode_cards_fit", cards.Length >= 4 && cards.All(card => safe.Encloses(card.GetGlobalRect()))
            && cards.Select(card => Mathf.RoundToInt(card.GetGlobalRect().GetCenter().Y)).Distinct().Count() >= 2,
            string.Join(", ", cards.Select(card => $"{card.Name}={card.GetGlobalRect()}")));
        // Up to three cards a line, the lines balanced (5 = 3 + 2, 4 = 2 + 2), in the native order, and a full line spans
        // most of the safe width instead of leaving wide empty sides.
        var lines = cards.GroupBy(card => Mathf.RoundToInt(card.GetGlobalRect().GetCenter().Y)).OrderBy(line => line.Key)
            .Select(line => line.OrderBy(card => card.GetGlobalRect().Position.X).ToArray()).ToArray();
        int expectedLines = (cards.Length + 2) / 3;
        bool inOrder = lines.SelectMany(line => line).Select(card => card.GetIndex())
            .SequenceEqual(cards.Select(card => card.GetIndex()).OrderBy(i => i));
        float widest = lines.Max(line => line[^1].GetGlobalRect().End.X - line[0].GetGlobalRect().Position.X);
        _report.Check("menu_mode_cards_three_a_line", lines.Length == expectedLines
            && lines.All(line => line.Length <= 3) && lines.Max(l => l.Length) - lines.Min(l => l.Length) <= 1
            && inOrder && widest >= safe.Size.X * 0.85f,
            $"lines={string.Join("+", lines.Select(l => l.Length))} expected={expectedLines} inOrder={inOrder} " +
            $"widest={widest:F0} safeWidth={safe.Size.X:F0}");
    }

    // ---- compendium submenu (menu-audit) ---------------------------------------------------------------

    private float _modeCardDrawnWidth;

    /// <summary>
    /// Records a singleplayer mode card's DRAWN width in canvas units before that screen closes, so the compendium's cards
    /// are compared with the card the player actually saw on the mode picker, not with a constant.
    /// </summary>
    private void RememberModeCardWidth(Control modes)
    {
        _modeCardDrawnWidth = 0f;
        if (!SafeAreaCanvas.IsPortrait) return;
        Control? card = modes.GetNodeOrNull<Control>("ButtonContainer")?.GetChildren().OfType<Control>()
            .FirstOrDefault(candidate => candidate.Visible);
        if (card is null) return;
        _modeCardDrawnWidth = DrawnWidth(card);
    }

    /// <summary>A card's drawn art width in canvas units, without the card's own hover scale (the row owns the portrait scale).</summary>
    private static float DrawnWidth(Control card)
    {
        Rect2 local = PortraitDrawnBounds.Of(card);
        Control parent = card.GetParent() as Control ?? card;
        return local.Size.X * Math.Abs(parent.GetGlobalTransformWithCanvas().Scale.X);
    }

    /// <summary>
    /// The compendium's four cards follow the singleplayer mode cards: two lines of two, each card as wide as a mode card,
    /// and the Character Stats / Run History buttons centred under the lower line, touch-sized and clear of the cards.
    /// </summary>
    private void CheckCompendiumLayout(Control screen)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Rect2 safe = PortraitSafeRect();
        Control[] cards = FindAll<Control>(screen)
            .Where(control => control.GetType().Name == "NShortSubmenuButton" && control.IsVisibleInTree()).ToArray();
        Control[] buttons = FindAll<Control>(screen)
            .Where(control => control.GetType().Name == "NCompendiumBottomButton" && control.IsVisibleInTree()).ToArray();
        if (cards.Length < 3 || buttons.Length < 1)
        {
            _report.Fail("menu_compendium_layout", $"cards={cards.Length} buttons={buttons.Length}: nothing to measure");
            return;
        }
        Rect2[] cardRects = cards.Select(card => card.GetGlobalRect()).ToArray();
        float[] widths = cards.Select(DrawnWidth).ToArray();
        Control[][] lines = cards.GroupBy(card => Mathf.RoundToInt(card.GetGlobalRect().GetCenter().Y))
            .OrderBy(line => line.Key)
            .Select(line => line.OrderBy(card => card.GetGlobalRect().Position.X).ToArray()).ToArray();
        bool inOrder = lines.SelectMany(line => line).Select(card => card.GetIndex())
            .SequenceEqual(cards.Select(card => card.GetIndex()).OrderBy(index => index));
        bool twoLines = lines.Length == 2 && lines[0].Length <= 2 && lines[0].Length >= lines[1].Length;
        bool sameWidth = _modeCardDrawnWidth > 0f
            && widths.All(width => Mathf.Abs(width - _modeCardDrawnWidth) <= 2f);

        Rect2[] buttonRects = buttons.Select(button => button.GetGlobalRect()).ToArray();
        Rect2 buttonUnion = buttonRects[0];
        bool buttonsClear = true;
        for (int i = 0; i < buttons.Length; i++)
        {
            buttonUnion = buttonUnion.Merge(buttonRects[i]);
            if (cardRects.Any(card => card.Intersects(buttonRects[i]))) buttonsClear = false;
            for (int j = i + 1; j < buttons.Length; j++)
                if (buttonRects[i].Intersects(buttonRects[j])) buttonsClear = false;
        }
        float cardsBottom = cardRects.Max(card => card.End.Y);
        bool buttonsBelow = buttonRects.All(button => button.Position.Y >= cardsBottom - 0.5f);
        bool buttonsTouchSized = buttons.All(button =>
        {
            Vector2 box = HostTouchTargetBox(button).Size;
            return box.X >= PortraitBands.MinTarget - 0.5f && box.Y >= PortraitBands.MinTarget - 0.5f;
        });
        bool onCanvas = cardRects.All(safe.Encloses) && buttonRects.All(safe.Encloses);

        _report.Check("menu_compendium_layout",
            twoLines && inOrder && sameWidth && onCanvas && buttonsBelow && buttonsClear
            && Mathf.Abs(buttonUnion.GetCenter().X - safe.GetCenter().X) <= 8f && buttonsTouchSized,
            $"lines={string.Join("+", lines.Select(line => line.Length))} widths=[{string.Join(",", widths.Select(w => w.ToString("F1")))}] " +
            $"mode={_modeCardDrawnWidth:F1} cards={string.Join(" ", cardRects.Select(r => r.ToString()))} " +
            $"buttons={string.Join(" ", buttonRects.Select(r => r.ToString()))} union={buttonUnion} safe={safe}");
    }

    private partial void CheckModeSetupLayout(string mode, Control setup, IReadOnlyList<NCharacterSelectButton> roster)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        // Two rows of character cards, each card a 44 point target across (190 units on the original 1680-wide reference
        // canvas are 122 here; the launcher's standard is 127).
        _report.Check($"menu_{mode}_portrait_roster", roster.Count > 1
            && roster.All(b => HostTouchTargetBox(b).Size.X >= PortraitBands.MinTarget - 0.5f)
            && roster.Select(b => Mathf.RoundToInt(b.GetGlobalRect().GetCenter().Y)).Distinct().Count() == 2,
            string.Join(", ", roster.Select(b => $"{b.Name}={b.GetGlobalRect()}")));
        if (mode != "Blitz") return;
        var title = setup.GetNode<RichTextLabel>("BlitzPlaceholder");
        var badge = setup.GetNode<Control>("%UnderConstruction");
        Vector2 inkTop = (title.Size - new Vector2(title.GetContentWidth(), title.GetContentHeight())) * 0.5f;
        float titleTop = (title.GetGlobalTransform() * inkTop).Y;
        float safeTop = PortraitSafeRect().Position.Y;
        _report.Check("menu_blitz_title_and_badge_below_safe_top", titleTop >= safeTop
            && (!badge.IsVisibleInTree() || badge.GetGlobalRect().Position.Y >= safeTop),
            $"title={titleTop:F1}, badge={badge.GetGlobalRect().Position.Y:F1}, safeTop={safeTop:F1}");
    }

    private partial void CheckCharacterGrid(Control characters, IReadOnlyList<NCharacterSelectButton> buttons, string suffix)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        var rows = buttons.GroupBy(b => Mathf.RoundToInt(b.GetGlobalRect().GetCenter().Y)).ToArray();
        _report.Check("menu_character_two_rows" + suffix, rows.Length == 2 && Math.Abs(rows[0].Count() - rows[1].Count()) <= 1,
            string.Join(", ", rows.Select(row => $"Y={row.Key}: {row.Count()}")));
        var ascension = characters.GetNode<Control>("%AscensionPanel");
        Control icon = ascension.GetNode<Control>("HBoxContainer/AscensionContainer/AscensionIconStandard");
        Control number = ascension.GetNode<Control>("HBoxContainer/AscensionContainer/AscensionLevel");
        _report.Check("menu_ascension_number_on_flame" + suffix,
            Mathf.Abs(icon.GetGlobalRect().GetCenter().X - number.GetGlobalRect().GetCenter().X) < 1f
                && icon.GetGlobalRect().HasPoint(number.GetGlobalRect().GetCenter()),
            $"number={number.GetGlobalRect().GetCenter()} flame={icon.GetGlobalRect()}");
        Rect2 safe = PortraitSafeRect();
        // The original 54 point card width: the same 54 points at the launcher's 2.88 units per point.
        float minimumWidth = PortraitBands.MinTarget * (54f / 44f);
        _report.Check("menu_character_phone_targets" + suffix, buttons.All(b => HostTouchTargetBox(b).Size.X >= minimumWidth),
            $"minimum={minimumWidth:F0}, targets={string.Join(", ", buttons.Select(b => b.GetGlobalRect().Size.X))}");
        _report.Check("menu_character_compact_row_spacing" + suffix, rows.All(row =>
        {
            Rect2[] ordered = row.Select(b => b.GetGlobalRect()).OrderBy(rect => rect.Position.X).ToArray();
            return ordered.Zip(ordered.Skip(1)).All(pair =>
                pair.Second.Position.X - pair.First.End.X <= minimumWidth * 0.29f);
        }), "adjacent buttons use the available space without overlapping");
        var ascensionArt = ascension.GetNode<Control>("Background");
        Control confirm = characters.GetNode<Control>("ConfirmButton");
        _report.Check("menu_character_confirm_touch_target" + suffix,
            HostTouchTargetBox(confirm).Size.Y >= PortraitBands.MinTarget - 0.5f && safe.Encloses(confirm.GetGlobalRect()),
            $"confirm={confirm.GetGlobalRect()} target={HostTouchTargetBox(confirm)}");
        Rect2 infoArt = characters.GetNode<Control>("InfoPanel/NinePatchRect").GetGlobalRect();
        _report.Check("menu_character_info_centered" + suffix, safe.Encloses(infoArt)
            && Mathf.Abs(infoArt.GetCenter().X - safe.GetCenter().X) < 2f, $"plate={infoArt} safe={safe}");
        _report.Check("menu_character_info_clear_of_ascension" + suffix,
            infoArt.End.Y <= ascensionArt.GetGlobalRect().Position.Y + 1f,
            $"info ends at {infoArt.End.Y:F0}; ascension starts at {ascensionArt.GetGlobalRect().Position.Y:F0}");
        Control[] adjacent = { ascensionArt, characters.GetNode<Control>("BackButton"), confirm };
        _report.Check("menu_character_grid_clear_of_navigation" + suffix, buttons.All(button =>
            adjacent.All(control => !control.GetGlobalRect().Intersects(button.GetGlobalRect()))),
            $"ascension art ends at {ascensionArt.GetGlobalRect().End.Y:F0}; grid starts at {buttons.Min(b => b.GetGlobalRect().Position.Y):F0}");
        _report.Check("menu_character_cross_row_focus" + suffix, rows.Length == 2 && rows[0].All(button =>
            rows[1].Contains(button.GetNodeOrNull<NCharacterSelectButton>(button.FocusNeighborBottom))),
            "Down from the first row focuses a native button in the second row");
    }

    private partial Func<bool>? CharacterArtDrawnUnframed(Control characters)
    {
        if (!SafeAreaCanvas.IsPortrait) return null;
        Control container = characters.GetNode<Control>("AnimatedBg");
        // A drawn frame is bad when the art has no portrait frame yet, when the art is hidden (a blank background), or when its
        // frame moved after it was first drawn framed (a figure that jumps on the second frame is the same flicker).
        var firstDrawn = new Dictionary<ulong, (Variant Art, Variant Figure)>();
        return () => container.GetChildren().OfType<Control>().Any(art =>
        {
            if (art.IsQueuedForDeletion()) return false;
            if (!art.IsVisibleInTree()) return container.IsVisibleInTree();
            if (!art.HasMeta("phone_character_art_frame")) return true;
            var now = (art.GetMeta("phone_character_art_frame"), art.GetMeta("phone_character_figure_frame", new Rect2()));
            if (!firstDrawn.TryGetValue(art.GetInstanceId(), out var first))
            {
                firstDrawn[art.GetInstanceId()] = now;
                return false;
            }
            return !first.Art.AsRect2().IsEqualApprox(now.Item1.AsRect2()) || !first.Figure.AsRect2().IsEqualApprox(now.Item2.AsRect2());
        });
    }

    private partial async Task CheckCharacterArtAsync(Control characters, string name, bool isRandom)
    {
        Control background = characters.GetNode<Control>("AnimatedBg").GetChildren().OfType<Control>().Single();
        Control art = FindAll<Control>(characters).FirstOrDefault(c => c.HasMeta("phone_character_art_frame")) ?? background;
        bool portrait = SafeAreaCanvas.IsPortrait;
        _report.Check("menu_character_native_landscape_" + name,
            portrait || !art.HasMeta("phone_character_art_frame"), "portrait framing must not run in landscape");
        if (!portrait) return;
        Vector2 viewport = _tree.Root.GetVisibleRect().Size;
        Rect2 frame = art.GetMeta("phone_character_art_frame", new Rect2()).AsRect2();
        _report.Check("menu_character_art_cover_" + name,
            frame.Grow(1).Encloses(new Rect2(Vector2.Zero, viewport)), frame.ToString());
        var ambient = background.FindChildren("*", "CPUParticles2D", true, false).OfType<CpuParticles2D>()
            .Where(p => p.EmissionShape == CpuParticles2D.EmissionShapeEnum.Rectangle).ToArray();
        _report.Check("menu_character_particles_at_top_" + name,
            ambient.Any(p => p.Emitting && new[] { Vector2.Zero, new Vector2(viewport.X, 0f) }.All(corner =>
            {
                Vector2 local = p.ToLocal(corner).Abs();
                return local.X <= p.EmissionRectExtents.X && local.Y <= p.EmissionRectExtents.Y;
            })), $"live ambient emitter covers both top corners (found {ambient.Length})");
        // The backdrop must actually draw under the figure at full-canvas cover size: the split masks the original
        // playback to the scenery slots, so the named backdrop attachment has to remain attached and the cover fill the canvas.
        string? backdropSlot = name switch
        {
            "Ironclad" => "bg",
            "Regent" => "Layer 5",
            "Defect" => "backgroundbottom",
            _ => null,
        };
        if (backdropSlot is not null)
        {
            // The split keeps the scenery on the original playback and masks it on the figure: any playback that keeps the
            // named backdrop attachment proves the backdrop still draws.
            bool attached = false;
            foreach (Node2D candidate in FindAll<Node2D>(art).Where(n => n.IsClass("SpineSprite")))
            {
                if (candidate.Call("get_skeleton").AsGodotObject() is not { } skeleton) continue;
                foreach (Variant value in skeleton.Call("get_slots").AsGodotArray())
                {
                    if (value.AsGodotObject() is not { } slot) continue;
                    string slotName = slot.Call("get_data").AsGodotObject()?.Call("get_attachment_name").AsString() ?? "";
                    if (slotName != backdropSlot) continue;
                    attached = slot.Call("get_attachment").AsGodotObject() is not null;
                    break;
                }
                if (attached) break;
            }
            _report.Check("menu_character_backdrop_drawn_" + name,
                attached && frame.Grow(1f).Encloses(new Rect2(Vector2.Zero, viewport)),
                $"slot={backdropSlot} attached={attached} art={frame} viewport={viewport}");
        }
        if (isRandom) return;
        if (!art.HasMeta("phone_character_figure_frame"))
        {
            _report.Pass("menu_character_independent_figure_" + name,
                $"single-sprite figure-biased cover: no separate figure rect (backdrop={frame})");
            return;
        }
        Rect2 figure = art.GetMeta("phone_character_figure_frame", new Rect2()).AsRect2();
        _report.Check("menu_character_independent_figure_" + name,
            figure.Size.Y > 0 && figure.Size.Y < viewport.Y * 0.65f && figure.Size.X < frame.Size.X,
            $"figure={figure} backdrop={frame}");
        Node2D? spine = characters.GetNodeOrNull<Node2D>("AnimatedBg/SpineSprite")
            ?? FindAll<Node2D>(characters).FirstOrDefault(n => n.GetType().Name.Contains("SpineSprite", StringComparison.Ordinal));
        static double TrackTime(Node2D? sprite)
        {
            GodotObject? track = sprite?.Call("get_animation_state").AsGodotObject()?.Call("get_current", 0).AsGodotObject();
            return track?.HasMethod("get_track_time") == true ? track.Call("get_track_time").AsDouble() : -1.0;
        }
        double before = TrackTime(spine);
        await WaitSecondsAsync(0.25);
        double after = TrackTime(spine);
        if (before < 0) _report.Skip("menu_character_live_spine_" + name, "no native spine node found under character select");
        else _report.Check("menu_character_live_spine_" + name, after > before, $"native track {before:F2} → {after:F2}");
        // Framing is applied once when the art is ready: the frames must not move after selection.
        Rect2 artBefore = frame, figureBefore = figure;
        await WaitFramesAsync(30);
        Rect2 artAfter = art.GetMeta("phone_character_art_frame", new Rect2()).AsRect2();
        Rect2 figureAfter = art.GetMeta("phone_character_figure_frame", new Rect2()).AsRect2();
        _report.Check("menu_character_stable_" + name,
            artBefore.IsEqualApprox(artAfter) && figureBefore.IsEqualApprox(figureAfter),
            $"art {artBefore} -> {artAfter}; figure {figureBefore} -> {figureAfter}");
        Vector2 focus = figure.GetCenter();
        // The figure's centre is not its face: on compact portrait its torso sits behind the info plate.
        _report.Check("menu_character_figure_in_art_band_" + name,
            figure.Position.Y >= -1f && focus.Y >= viewport.Y * 0.30f && focus.Y <= viewport.Y * 0.47f
            && new Rect2(Vector2.Zero, viewport).HasPoint(focus) && frame.Grow(1f).HasPoint(focus),
            $"focus={focus} figure={figure} viewport={viewport} art={frame}");
    }

    // ---- menu-hover ----------------------------------------------------------------------------------

    private partial async Task CheckModConfigOutlineAsync(NMainMenu menu)
    {
        string modPath = "MainMenuTextButtons/ModConfigButton/Label";
        if (!SafeAreaCanvas.IsPortrait) return;
        // The Mod Config button belongs to a mod (BaseLib); without one installed there is nothing to compare.
        if (menu.GetNodeOrNull<Label>(modPath) is not { } mods)
        {
            _report.Skip("mod_config_matches_settings", "no Mod Config button: no mod that adds one is installed");
            return;
        }
        Label settings = menu.GetNode<Label>("MainMenuTextButtons/SettingsButton/Label");
        int FontSize(Label label) => label.LabelSettings?.FontSize ?? label.GetThemeFontSize("font_size");
        _report.Check("mod_config_matches_settings_font_size", FontSize(mods) == FontSize(settings),
            $"settings={FontSize(settings)} mod config={FontSize(mods)}");
        int Outline(Label label) => label.LabelSettings?.OutlineSize ?? label.GetThemeConstant("outline_size");
        Color OutlineColor(Label label) => label.LabelSettings?.OutlineColor ?? label.GetThemeColor("font_outline_color");
        _report.Check("mod_config_matches_settings_outline", Outline(mods) == Outline(settings)
            && Outline(mods) > 0 && OutlineColor(mods).IsEqualApprox(OutlineColor(settings)),
            $"settings={Outline(settings)} {OutlineColor(settings)} mod config={Outline(mods)} {OutlineColor(mods)}");
        await CaptureAsync("menu_mod_config_outline");
    }

    private partial void CheckMenuButtonHover(NMainMenu menu, Control button, string name)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Label label = button.GetChild<Label>(0);
        Control left = menu.GetNode<Control>("%ButtonReticleLeft");
        Control right = menu.GetNode<Control>("%ButtonReticleRight");
        Rect2 safe = PortraitSafeRect();
        Rect2 text = label.GetGlobalRect();
        _report.Check(name + "_native_outline", label.GetThemeConstant("outline_size") >= label.GetThemeFontSize("font_size") * 0.5f
            && label.GetThemeColor("font_outline_color").R < 0.01f, $"font={label.GetThemeFontSize("font_size")} outline={label.GetThemeConstant("outline_size")}");
        _report.Check(name + "_carets_visible", left.Modulate.A > 0.9f && right.Modulate.A > 0.9f, "native hover is active");
        // The original 24 unit tolerance is 15 on the 1080 canvas.
        _report.Check(name + "_carets_aligned", Math.Abs(left.GetGlobalRect().GetCenter().Y - text.GetCenter().Y) < 15f
            && Math.Abs(right.GetGlobalRect().GetCenter().Y - text.GetCenter().Y) < 15f,
            $"left={left.GetGlobalRect()} right={right.GetGlobalRect()} text={text}");
        _report.Check(name + "_carets_in_frame", safe.Encloses(left.GetGlobalRect()) && safe.Encloses(right.GetGlobalRect()),
            "native carets stay inside the phone frame");
        // The original 80 unit caret width is 51 here.
        _report.Check(name + "_carets_sized", left.Size.X >= 51f && right.Size.X >= 51f, $"left={left.Size} right={right.Size}");
        if (name != "ContinueButton") return;
        var info = menu.ContinueRunInfo;
        _report.Check("continue_info_visible", info.HasResult && info.Modulate.A > 0.9f, "saved-run information opens on hover");
        _report.Check("continue_info_in_frame", safe.Encloses(info.GetGlobalRect()), $"info={info.GetGlobalRect()} safe={safe}");
        _report.Check("continue_info_above_buttons", info.GetGlobalRect().End.Y < button.GetGlobalRect().Position.Y, "native information does not cover menu targets");
        // The original 2.5 is 1.607 on the 1080 canvas (contract decision 1).
        _report.Check("continue_info_readable_scale", info.Scale.X >= 1.607f, $"scale={info.Scale}");
    }

    // ---- menu-utilities ------------------------------------------------------------------------------

    private partial async Task CheckMenuUtilitiesLayoutAsync(NMainMenu menu, Control profile, Control profileRow, Control notes, Label modded)
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            _report.Check("menu_utilities_landscape_native", profileRow.Scale.IsEqualApprox(Vector2.One)
                && notes.Size.X < 100f && modded.GetThemeFontSize("font_size") < 32,
                $"profile content scale={profileRow.Scale} notes={notes.Size} mod font={modded.GetThemeFontSize("font_size")}");
            return;
        }
        Rect2 safe = PortraitSafeRect();
        float minimum = PortraitBands.MinTarget;
        Rect2 profileRect = HostTouchTargetBox(profile), notesRect = HostTouchTargetBox(notes);
        _report.Check("menu_utilities_phone_targets", safe.Encloses(profileRect) && safe.Encloses(notesRect)
            && profileRect.Size.Y >= minimum - 0.5f && notesRect.Size.Y >= minimum - 0.5f && notesRect.Size.X >= minimum - 0.5f
            && !profileRect.Intersects(notesRect) && profileRect.Grow(1f).Encloses(profileRow.GetGlobalRect()),
            $"profile={profileRect} drawn={profile.GetGlobalRect()} contents={profileRow.GetGlobalRect()} notes={notesRect} minimum={minimum:F0}");
        // Resting state: the row is kept at the game's 1x size (its 11 point labels come from the reflow, not a permanent
        // enlargement); the native hover enlarges it only while the pointer is on it.
        _report.Check("menu_profile_row_resting_scale", profileRow.Scale.IsEqualApprox(Vector2.One),
            $"content={profileRow.Scale}");
        var buttons = menu.GetNode<Control>("%MainMenuTextButtons").GetChildren().OfType<Control>()
            .Where(c => c.IsVisibleInTree()).ToArray();
        var before = buttons.Select(c => c.GetGlobalRect()).ToArray();
        modded.Hide();
        menu.RefreshButtons();
        await WaitFramesAsync(2);
        _report.Check("menu_mod_status_does_not_reflow_buttons", before.SequenceEqual(buttons.Select(c => c.GetGlobalRect())),
            "button rectangles are identical with and without the mod status");
        modded.Show();
        Rect2 status = modded.GetGlobalRect();
        _report.Check("menu_mod_status_small_bottom_right", modded.IsVisibleInTree() && safe.Encloses(status)
            && status.GetCenter().X > safe.GetCenter().X && safe.End.Y - status.End.Y <= 40f,
            $"status={status} font={modded.GetThemeFontSize("font_size")} safe={safe}");
        // Contract decision 1: the mod status is a 44 point target in the reflowed utility corner.
        _report.Check("menu_mod_status_touch_target", HostTouchTargetBox(modded).Size.Y >= minimum - 0.5f,
            $"target={HostTouchTargetBox(modded)} drawn={status}");
        // The status stays in its corner: with the Quit row gone it must not sit over (and take input from) any menu row.
        var rowRects = menu.GetNode<Control>("%MainMenuTextButtons").GetChildren().OfType<Control>()
            .Where(c => c.IsVisibleInTree()).Select(LauncherDrawnRect).ToArray();
        _report.Check("menu_mod_status_clear_of_buttons",
            rowRects.Length > 0 && rowRects.All(row => !row.Intersects(status)),
            $"status={status} rows={string.Join("; ", rowRects)}");
    }

    private partial void CheckMenuProfileHover(Control profile, Control profileRow)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        // The row itself stays at the game's 1x size with its 11 point labels (so it cannot overlap the menu); only the
        // button's own native hover growth applies.
        _report.Check("menu_profile_native_hover_retains_size", profile.Scale.X > 1.01f
            && profileRow.Scale.IsEqualApprox(Vector2.One),
            $"hover={profile.Scale} content={profileRow.Scale}");
    }

    private partial void CheckMenuModDetails(Control details, Label modded)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        _report.Check("menu_mod_details_above_status", PortraitSafeRect().Encloses(details.GetGlobalRect())
            && details.GetGlobalRect().End.Y <= modded.GlobalPosition.Y,
            $"details={details.GetGlobalRect()} status={modded.GetGlobalRect()}");
    }

    // ---- settings ------------------------------------------------------------------------------------

    private partial void CheckSettingsLanguageListLayout(NSettingsScreen settings, NDropdownContainer list)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Rect2 safe = PortraitSafeRect();
        Rect2 rect = list.GetGlobalRect();
        _report.Check("settings_language_list_safe", safe.Grow(1).Encloses(rect), rect.ToString());
        // The original 600 unit list cap is 386 here.
        _report.Check("settings_language_list_capped", rect.Size.Y <= 386f + 2f, $"height={rect.Size.Y:F0} cap=386");
    }

    private partial Vector2 SettingsDismissPoint() => new(20, PortraitSafeRect().End.Y - 300f * (1080f / 1680f));

    private partial void CheckSettingsInputLayout(NSettingsScreen settings, Control input)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        foreach (Control column in input.GetNode<Control>("VBoxContainer/Header").GetChildren().OfType<Control>())
            _report.Check("settings_input_column_safe_" + column.Name, PortraitSafeRect().Grow(1).Encloses(column.GetGlobalRect()),
                column.GetGlobalRect().ToString());
    }

    private partial void CheckSettingsReopenStable(NSettingsScreen settings, Control general)
    {
        // The launcher scales the settings page itself, `ScrollContainer/Mask/Clipper/GeneralSettings` (1.55); its
        // VBoxContainer stays at 1 and inherits that scale, so the page is the node that proves the layout was neither
        // stacked nor reset on reopen. Landscape is the game's own desktop layout: there the page must be back at its
        // native 1x scale, so no portrait scale was left behind.
        Vector2 scale = general.Scale;
        if (!SafeAreaCanvas.IsPortrait)
        {
            _report.Check("settings_reopen_scale_stable", scale.IsEqualApprox(Vector2.One),
                $"landscape page scale={scale} (1, native)");
            return;
        }
        _report.Check("settings_reopen_scale_stable", Mathf.Abs(scale.X - 1.55f) <= 0.01f && Mathf.Abs(scale.Y - 1.55f) <= 0.01f,
            $"page scale={scale} (1.55)");
    }

    private partial void CheckSettingsTabLayout(Control settings, string tab, Control page)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        if (settings.GetNodeOrNull<Control>("SettingsTabManager") is not { } tabManager) return;
        bool tabsInside = PortraitSafeRect().Encloses(tabManager.GetGlobalRect())
            && tabManager.GetChildren().OfType<Control>().Where(c => c.IsVisibleInTree())
                .All(c => PortraitSafeRect().Grow(1f).Encloses(c.GetGlobalRect()));
        _report.Check("settings_tab_inside_frame_" + tab.ToLowerInvariant(), tabsInside, $"tabs={tabManager.GetGlobalRect()}");
        if (settings.GetNodeOrNull<Control>("ScrollContainer/Mask/Clipper") is not { } clipper) return;
        // The page is the scroll extent (1.55-scaled panel, several screens tall), so only its clipped part is drawn: compare
        // that with the frame, and require the page's own top edge to start inside the clip.
        Rect2 visible = page.GetGlobalRect();
        for (Node? node = page.GetParent(); node is not null; node = node.GetParent())
            if (node is Control clip && (clip is ScrollContainer || clip.ClipContents))
                visible = visible.Intersection(clip.GetGlobalRect());
        _report.Check("settings_rows_below_tabs_" + tab.ToLowerInvariant(),
            clipper.GetGlobalRect().Position.Y >= tabManager.GetGlobalRect().End.Y - 1f
                && clipper.GetGlobalRect().Grow(1f).Encloses(new Rect2(page.GlobalPosition, Vector2.One))
                && visible.HasArea() && PortraitSafeRect().Grow(1f).Encloses(visible),
            $"clipper={clipper.GetGlobalRect()} tabs={tabManager.GetGlobalRect()} page={page.GetGlobalRect()} visible={visible}");
    }

    /// <summary>
    /// The rows area may scroll; its range has to end with its rows. The game sizes a page taller than the rows area as its
    /// rows plus 40% of that area (<c>NSettingsPanel.RefreshSize</c>), so a controller can bring its last row to the middle,
    /// and a page within 50 units of fitting gets the same padding. The launcher draws the portrait page 1.55 times larger
    /// and the scroll view measures the page's own height, so that padding becomes that fraction of the rows area of
    /// scrollable empty space below the last setting. The <c>menus</c> scenario installs the phone's Graphics rows, so that
    /// page overflows at both portrait sizes and in landscape; the same fixture leaves the General page to the game's own
    /// layout in portrait, where it is padded. Put the page's own target at its limit, let the native scroll settle, and
    /// require its last visible row to end on the clip inside the game's own 20 + 30 unit padding, rounding included, not a
    /// fraction of the rows area below it. Then fire the viewport SizeChanged the phone gets after it has laid the screen
    /// out and require the same; the game's panels re-add their padding from that signal, after the launcher's handler.
    /// </summary>
    private partial async Task CheckSettingsScrollEndAsync(Control settings, string tab, Control page)
    {
        string slug = tab.ToLowerInvariant();
        string step = "settings_" + slug + "_scroll_end";
        if (!SafeAreaCanvas.IsPortrait)
        {
            // Landscape keeps the game's own pages and their own scroll padding (General and Input overflow natively); only
            // the Graphics page, the one the launcher adds rows to on the phone, is this host's to measure as the control.
            if (tab != "Graphics") return;
        }
        else if (tab == "Input")
        {
            // NInputSettingsPanel.RefreshSize sizes that page itself (asynchronously, after its own visibility change); the
            // launcher's rows layout does not own its height.
            _report.Skip(step, "the Input page sizes itself after it is shown; the launcher's rows layout does not own it");
            return;
        }
        if (settings.GetNodeOrNull<Control>("ScrollContainer") is not { } scroll ||
            settings.GetNodeOrNull<Control>("ScrollContainer/Mask/Clipper") is not { } clip ||
            page.GetNodeOrNull<Control>("VBoxContainer") is not { } rows)
        {
            _report.Fail(step, "the settings screen has no rows view, clip or page container");
            return;
        }
        var limitProperty = AccessTools.Property(typeof(NScrollableContainer), "ScrollLimitBottom");
        var targetField = AccessTools.Field(typeof(NScrollableContainer), "_targetDragPosY");
        var paddingField = AccessTools.Field(typeof(NScrollableContainer), "_paddingTop");
        if (limitProperty is null || targetField is null || paddingField is null ||
            limitProperty.GetValue(scroll) is not float limit || targetField.GetValue(scroll) is not float target ||
            paddingField.GetValue(scroll) is not float paddingTop)
        {
            _report.Skip(step, "the game's NScrollableContainer no longer exposes its scroll limit and target");
            return;
        }
        // The page's last visible row and the clip it scrolls in, in the same canvas coordinates. The portrait reflow's
        // spacer is the empty tail that makes the page as tall as its drawn rows; it is not a row.
        float RowsBottom()
        {
            float bottom = float.NegativeInfinity;
            foreach (Node child in rows.GetChildren())
            {
                if (child is not Control row || !row.IsVisibleInTree() || row.Name == "PortraitScaleSpacer") continue;
                bottom = MathF.Max(bottom, row.GetGlobalRect().End.Y);
            }
            return bottom;
        }
        float clipBottom = clip.GetGlobalRect().End.Y;
        bool phoneRows = tab == "Graphics" && PhoneGraphicsRows.IsInstalledOn(page);
        string Describe() => $"page={page.Size.Y:F0}/{page.Scale.Y:F2} content-min={rows.GetMinimumSize().Y:F0} clip={clip.Size.Y:F0}";
        string PageRows() => phoneRows ? $"; {PhoneGraphicsRows.Describe(page)}" : "";
        // The native clamp is [min(limit, 0), max(limit, 0)] and a tab switch starts the target at 0. Put the target at the
        // limit, let the native scroll settle, and measure where the page's last visible row stopped.
        async Task<(float Gap, float Moved, float MaxScroll, float RestBottom, float Scrolled)> RunToLimitAsync()
        {
            if (limitProperty.GetValue(scroll) is not float atLimit || targetField.GetValue(scroll) is not float from)
                return (float.NaN, 0f, 0f, RowsBottom(), float.NaN);
            float rest = RowsBottom();
            float travel = MathF.Max(from - MathF.Min(atLimit, 0f), 0f);
            targetField.SetValue(scroll, MathF.Min(atLimit, 0f));
            await WaitSecondsAsync(0.65);
            float now = RowsBottom();
            targetField.SetValue(scroll, from);
            await WaitSecondsAsync(0.35);
            return (clipBottom - now, rest - now, travel, rest, now);
        }
        float rowsBottom = RowsBottom();
        if (!float.IsFinite(rowsBottom))
        {
            _report.Fail(step, "the page has no visible row");
            return;
        }
        // Decide "the rows fit" at the page's resting top, whatever the scroll is doing now: the rows move with the page,
        // and a tab switch inside the page may already have lifted them.
        float lifted = page.Position.Y - paddingTop;
        bool fits = rowsBottom - lifted <= clipBottom + 1f;
        float maxScroll = MathF.Max(target - MathF.Min(limit, 0f), 0f);
        targetField.SetValue(scroll, MathF.Min(limit, 0f));
        await WaitSecondsAsync(0.65);
        float rowsNow = RowsBottom();
        float moved = rowsBottom - rowsNow;
        float gap = clipBottom - rowsNow;
        targetField.SetValue(scroll, target);
        await WaitSecondsAsync(0.35);
        // The game's bottom padding under the last row is 30 units (20 above it): a stopped page ends 39-50 units above the
        // clip. 60 allows that and rounding, not a third of the rows area.
        const float margin = 60f;
        bool stopped = MathF.Abs(gap) <= margin;
        bool travelled = MathF.Abs(moved - maxScroll) <= 4f;
        bool ok = fits ? maxScroll <= margin : stopped && travelled;
        _report.Check(step, ok,
            $"limit={limit:F0} (max scroll {maxScroll:F0}) moved {moved:F0}; last row {gap:F0} above the clip bottom " +
            $"{clipBottom:F0} at {rowsNow:F0} (rest {rowsBottom:F0}, lifted {lifted:F0}); {Describe()}" +
            (ok ? "" : PageRows()));
        if (!phoneRows || settings.GetTree().Root is not { } root) return;
        // The phone's viewport changes size after this page is laid out (safe area, render scale, app resume). The game's
        // panels re-add their 40% padding from that same signal, so this is the ordering the device gets: the launcher's
        // sizing has to be the last word, in portrait and in landscape.
        root.EmitSignal(Window.SignalName.SizeChanged);
        await WaitSecondsAsync(0.5);
        targetField.SetValue(scroll, 0f);
        await WaitSecondsAsync(0.3);
        float rest2 = RowsBottom();
        if (!float.IsFinite(rest2))
        {
            _report.Fail(step + "_after_resize", "the page has no visible row after the viewport changed size");
            return;
        }
        float lifted2 = page.Position.Y - paddingTop;
        bool fits2 = rest2 - lifted2 <= clipBottom + 1f;
        var second = await RunToLimitAsync();
        bool stopped2 = MathF.Abs(second.Gap) <= margin;
        bool travelled2 = MathF.Abs(second.Moved - second.MaxScroll) <= 4f;
        bool ok2 = fits2 ? second.MaxScroll <= margin : stopped2 && travelled2;
        _report.Check(step + "_after_resize", ok2,
            $"after viewport SizeChanged: max scroll {second.MaxScroll:F0} moved {second.Moved:F0}; last row " +
            $"{second.Gap:F0} above the clip bottom {clipBottom:F0} at {second.Scrolled:F0} " +
            $"(rest {rest2:F0}, lifted {lifted2:F0}); {Describe()}" + (ok2 ? "" : PageRows()));
    }

    // ---- pause ---------------------------------------------------------------------------------------

    private partial void CheckPauseLayout(NPauseMenu menu, Control panel, IReadOnlyList<NPauseMenuButton> visible, Label title, Rect2 before)
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            _report.Check("pause_landscape_keeps_native_scale", panel.Scale.IsEqualApprox(Vector2.One), $"panel scale={panel.Scale}");
            return;
        }
        Rect2 safe = PortraitSafeRect();
        // One point is 2.88 units on this canvas; 127 units are the 44 points a row needs.
        const float pointsPerUnit = 375f / 1080f;
        _report.Check("pause_all_buttons_safe_and_touch_sized", visible.All(button => safe.Encloses(button.GetGlobalRect())
            && HostTouchTargetBox(button).Size.Y >= PortraitBands.MinTarget - 0.5f),
            string.Join("; ", visible.Select(button => $"{button.Name}: {button.GetGlobalRect()}")));
        float titlePoints = title.GetThemeFontSize("font_size") * Math.Abs(title.GetGlobalTransform().Scale.Y) * pointsPerUnit;
        _report.Check("pause_title_enlarged_inside_safe_frame", safe.Encloses(title.GetGlobalRect()) && titlePoints >= 28f,
            $"title={title.GetGlobalRect()} at {titlePoints:F1}pt");
        _report.Check("pause_panel_centered", safe.Encloses(panel.GetGlobalRect())
            && Mathf.Abs(panel.GetGlobalRect().GetCenter().X - safe.GetCenter().X) < 2f,
            $"panel={panel.GetGlobalRect()} safe={safe}");
    }

    // ---- game over -----------------------------------------------------------------------------------

    /// <summary>
    /// The launcher scrolls the settled summary in place: the game's own CenterContainer is the clipped window
    /// (ClipContents, scaled 1.337) and the column's offset is carried on it as canvas-unit metadata, so no ScrollContainer
    /// exists. The offsets are read through delegates because they change as the drag is processed after this call.
    /// </summary>
    private partial GameOverSummary? GameOverSummaryOf(NGameOverScreen screen)
    {
        if (!SafeAreaCanvas.IsPortrait) return null;
        if (screen.GetNodeOrNull<Control>("%ScreenshakeContainer/CenterContainer") is not { } window) return null;
        if (!window.ClipContents) return null;
        return new GameOverSummary(window, window.GetGlobalRect(),
            () => window.HasMeta("phone_summary_scroll") ? window.GetMeta("phone_summary_scroll").AsInt32() : 0,
            () => window.HasMeta("phone_summary_max_scroll") ? window.GetMeta("phone_summary_max_scroll").AsInt32() : 0);
    }

    private partial async Task CheckGameOverOpeningAsync(NGameOverScreen screen, NGameOverContinueButton proceed,
        IReadOnlyList<(Node2D Actor, float Y)> actors, bool win)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        CheckGameOverButton(proceed, "game_over_continue");
        var tableau = screen.GetNode<Control>("%CreatureContainer");
        _report.Check("game_over_actors_keep_combat_height", actors.All(actor => GodotObject.IsInstanceValid(actor.Actor)
            && actor.Actor.GetParent() == tableau && Mathf.Abs(actor.Actor.GlobalPosition.Y - actor.Y) < 3f),
            string.Join(", ", actors.Select(actor => $"{actor.Y:F1}->{actor.Actor.GlobalPosition.Y:F1}")));
        if (!win) return;
        var message = screen.GetNode<RichTextLabel>("%VictoryDamageLabel");
        await WaitUntilAsync(() => message.VisibleRatio >= 1f, "settled native victory message", ShortWait);
        _report.Check("game_over_victory_message_fits", message.GetContentHeight() <= message.Size.Y
            && PortraitSafeRect().Encloses(message.GetGlobalRect()),
            $"label={message.GetGlobalRect()} text height={message.GetContentHeight()}");
    }

    private partial void CheckGameOverSettled(NGameOverScreen screen, NReturnToMainMenuButton mainMenu, GridContainer scores,
        GameOverSummary? summary, NCombatRoom? fight, bool win)
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            _report.Check("game_over_landscape_untouched", summary is null, "native layout");
            return;
        }
        CheckGameOverButton(mainMenu, "game_over_main_menu");
        // A fight keeps its tableau behind the summary; with no fight the act's art stands in for the room, so the screen
        // still has a backdrop, and that backdrop is what the summary window has to sit inside.
        bool backdrop = fight is null
            ? screen.FindChild("PhoneGameOverArt", true, false) is Control { } art && art.IsVisibleInTree()
            : fight.IsInsideTree();
        _report.Check("game_over_scrolling_summary_and_art", summary is not null
            && backdrop && PortraitSafeRect().Encloses(summary.Clip),
            $"window={summary?.Clip} backdrop={(fight is null ? "act art" : "fight")}");
        _report.Check("game_over_scores_visible_inside_scroll", summary is not null && scores.IsVisibleInTree()
            && summary.Clip.Grow(2f).Encloses(scores.GetGlobalRect()),
            $"scoreRect={scores.GetGlobalRect()} clip={summary?.Clip}");
        // The shared gate exempts the summary's award chips (NBadge/Nn); on this host they are padded, so they are measured
        // here: every interactive control inside the clipped window keeps a 127 u target (the pads' overlaps are allowed,
        // each target is measured on its own).
        if (summary is null) return;
        var chips = new List<string>();
        foreach (Node node in FindAll<Control>(summary.Window))
        {
            if (node is not Control chip || !chip.IsVisibleInTree() || chip.IsQueuedForDeletion()) continue;
            if (chip is not NClickableControl and not BaseButton and not Slider) continue;
            if (chip.MouseFilter == Control.MouseFilterEnum.Ignore) continue;
            Rect2 box = HostTouchTargetBox(chip);
            if (Mathf.Min(box.Size.X, box.Size.Y) + 0.5f < PortraitBands.MinTarget)
                chips.Add($"{chip.GetType().Name}:{chip.Name} {box.Size.X:F0}x{box.Size.Y:F0}");
        }
        _report.Check("game_over_summary_chip_targets", chips.Count == 0,
            chips.Count == 0 ? "every summary chip has a 127 u target" : string.Join("; ", chips.Take(20)));
    }

    private partial void CheckGameOverOutsideCombat(NGameOverScreen screen, GameOverSummary? summary)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Rect2 frame = _tree.Root.GetViewport().GetVisibleRect();
        Control? art = screen.FindChild("PhoneGameOverArt", true, false) as Control;
        _report.Check("game_over_no_fight_act_art_fills_frame", art is not null && art.IsVisibleInTree()
            && art.GetGlobalRect().Grow(2f).Encloses(frame),
            $"art={(art is null ? "none" : art.GetGlobalRect().ToString())} frame={frame}");
        // The native screen still makes a tableau for a room without a fight (the rest site's own branch places one visuals
        // node per seated player); the fallback art is for the room behind it, not a replacement for the bodies.
        var creatures = screen.GetNode<Control>("%CreatureContainer");
        _report.Check("game_over_no_fight_room_bodies_placed", creatures.GetChildCount() > 0,
            $"creature container children={creatures.GetChildCount()}");
        var banner = screen.GetNode<Control>("%Banner");
        Rect2 bannerRect = banner.GetGlobalRect();
        Rect2 safe = PortraitSafeRect();
        _report.Check("game_over_no_fight_banner_under_run_bar", safe.Grow(2f).Encloses(bannerRect),
            $"banner={bannerRect} safe={safe}");
        var quote = screen.GetNode<RichTextLabel>("%DeathQuoteLabel");
        Rect2 quoteRect = quote.GetGlobalRect();
        _report.Check("game_over_no_fight_quote_wraps_in_banner", quoteRect.Position.X >= bannerRect.Position.X - 1f
            && quoteRect.End.X <= bannerRect.End.X + 1f
            && quoteRect.Position.Y >= bannerRect.Position.Y - 1f && quoteRect.Position.Y <= bannerRect.End.Y,
            $"quote={quoteRect} banner={bannerRect}");
        _report.Check("game_over_no_fight_banner_above_summary", summary is null
            || bannerRect.End.Y <= summary.Clip.Position.Y + 1f,
            $"banner={bannerRect} window={summary?.Clip}");
    }

    /// <summary>The lowest drawn rect of the summary's content: its score rows and the chips below them.</summary>
    private static Rect2 LowestContent(GridContainer scores, Control[] rows)
    {
        Rect2 lowest = rows[0].GetGlobalRect();
        foreach (Control row in rows)
        {
            Rect2 rect = row.GetGlobalRect();
            if (rect.End.Y > lowest.End.Y) lowest = rect;
        }
        for (Node? node = scores.GetParent(); node is not null; node = node.GetParent())
        {
            if (node is not Control block) break;
            foreach (Node child in block.GetChildren())
            {
                if (child is not Control part || part == scores || !GodotObject.IsInstanceValid(part)) continue;
                if (!part.IsVisibleInTree() || part.Size.Y < 2f || part.Modulate.A < 0.05f) continue;
                Rect2 rect = part.GetGlobalRect();
                if (rect.Size.Y < 2f || rect.Intersects(lowest)) continue;
                if (rect.End.Y > lowest.End.Y) lowest = rect;
            }
            if (node.GetParent() is Control up && up != node) block = up;
        }
        return lowest;
    }

    /// <summary>
    /// The in-place summary must actually clip: every visible score row needs a <see cref="Control.ClipContents"/> ancestor
    /// whose rect sits inside the reported window, and anything that is not clipped that way must not reach the banner or the
    /// act-art backstop. Rects alone (the settled check) cannot see a list drawn over them.
    /// </summary>
    private partial void CheckGameOverSummaryClipping(NGameOverScreen screen, GameOverSummary? summary, GridContainer scores)
    {
        if (!SafeAreaCanvas.IsPortrait || summary is null) return;
        Rect2 window = summary.Clip;
        var rows = scores.GetChildren().OfType<Control>()
            .Where(row => row.IsVisibleInTree() && row.GetGlobalRect().Size.X >= 2f && row.GetGlobalRect().Size.Y >= 2f).ToArray();
        var unclipped = new List<string>();
        var unclippedRects = new List<Rect2>();
        var notInert = new List<string>();
        foreach (Control row in rows)
        {
            Rect2 rect = row.GetGlobalRect();
            Control? clipNode = null;
            Rect2 clipUsed = default;
            for (Node? node = row.GetParent(); node is not null && node != screen; node = node.GetParent())
            {
                if (node is not Control clip || !clip.ClipContents) continue;
                // Where the pinned local rect actually lands on the canvas, through the clipping item's live transform:
                // the layout pins Rect2(Vector2.Zero, window / scale) in the item's own space, so the canvas rect is that
                // rect transformed. Comparing the window against the metadata the layout itself writes would be
                // tautological - this reads the geometry the renderer will use.
                Transform2D toCanvas = clip.GetGlobalTransformWithCanvas();
                Vector2 scale = toCanvas.Scale.Abs();
                if (scale.X < 0.01f || scale.Y < 0.01f) continue;
                var local = new Rect2(Vector2.Zero, window.Size / scale);
                var landed = new Rect2(toCanvas * local.Position, toCanvas.BasisXform(local.Size));
                bool positioned = MathF.Abs(landed.Position.X - window.Position.X) <= 2f &&
                                  MathF.Abs(landed.Position.Y - window.Position.Y) <= 2f &&
                                  MathF.Abs(landed.Size.X - window.Size.X) <= 2f &&
                                  MathF.Abs(landed.Size.Y - window.Size.Y) <= 2f;
                if (!positioned) continue;
                clipNode = clip;
                clipUsed = landed;
                break;
            }
            if (clipNode is null)
            {
                unclipped.Add($"{row.Name} {rect}");
                unclippedRects.Add(rect);
                continue;
            }
            if (!rect.Intersects(clipUsed) && row.MouseFilter != Control.MouseFilterEnum.Ignore)
                notInert.Add($"{row.Name} {rect} outside clip {clipUsed}");
        }
        _report.Check("game_over_summary_rows_clipped", rows.Length == 0 || unclipped.Count == 0,
            $"rows={rows.Length} window={window} unclipped={(unclipped.Count == 0 ? "none" : string.Join("; ", unclipped.Take(8)))}");
        _report.Check("game_over_summary_outside_rows_inert", notInert.Count == 0,
            $"outside={(notInert.Count == 0 ? "none" : string.Join("; ", notInert.Take(8)))}");

        Control? banner = screen.FindChild("Banner", true, false) as Control;
        Control? art = screen.FindChild("PhoneGameOverArt", true, false) as Control;
        var clashes = new List<string>();
        foreach (Rect2 rect in unclippedRects)
        {
            if (banner is { } b && b.IsVisibleInTree() && b.GetGlobalRect().Intersects(rect))
                clashes.Add($"banner {b.GetGlobalRect()} x row {rect}");
            if (art is { } a && a.IsVisibleInTree() && a.GetGlobalRect().Intersects(rect))
                clashes.Add($"art {a.GetGlobalRect()} x row {rect}");
        }
        // The scroll's end: at the maximum offset the last score row must sit on the window's bottom, not leave an empty
        // band. Predicted from the current rects and the two offsets, so no second drag is needed.
        // At capture time the rows must fill the window: an empty band under the last drawn row means the clip, the range or
        // the rows themselves are hiding content, even when the predicted end of the range looks right.
        if (rows.Length > 0)
        {
            // What a finger sees as the column's content: the score rows and the chips below them. A short summary is
            // centred in the window on purpose, so an empty band is only a defect when there is something to scroll.
            Rect2 lowest = LowestContent(scores, rows);
            float band = window.End.Y - MathF.Min(lowest.End.Y, window.End.Y);
            float rowHeight = MathF.Max(lowest.Size.Y, rows.Max(row => row.GetGlobalRect().Size.Y));
            // At the offset the run is at, the window must have no empty band under the content; a summary shorter than the
            // window is centred on purpose and has nothing to scroll.
            _report.Check("game_over_summary_fills_window",
                summary.Max() <= 8f || band <= MathF.Max(rowHeight, 8f),
                $"band={band:F1} (row {rowHeight:F1}) offset={summary.Offset()} max={summary.Max()} " +
                $"lowest={lowest} window={window}");
        }

        if (rows.Length > 0 && summary.Max() > summary.Offset())
        {
            // At the end of the range the last thing the column draws (a score row or a chip under it) must sit on the
            // window's bottom: anything left over was an empty tail the range should not have included.
            Rect2 lowest = LowestContent(scores, rows);
            float remaining = summary.Max() - summary.Offset();
            float predictedBottom = lowest.End.Y - remaining;
            float band = window.End.Y - predictedBottom;
            float rowHeight = MathF.Max(lowest.Size.Y, rows.Min(row => row.GetGlobalRect().Size.Y));
            _report.Check("game_over_summary_end_shows_last_row",
                MathF.Abs(band) <= MathF.Max(rowHeight, 8f),
                $"lowest content bottom at max {predictedBottom:F1} vs window bottom {window.End.Y:F1} " +
                $"(band {band:F1}, row {rowHeight:F1}, offset {summary.Offset()} of {summary.Max()})");
        }

        // The death quote over the tableau: a creature's drawn rect may reach the quote's line, but then the quote must be
        // drawn above it (the launcher lifts the banner), so the text stays readable.
        RichTextLabel? quote = screen.FindChild("DeathQuoteLabel", true, false) as RichTextLabel;
        Control? quotesParent = quote?.GetParent() as Control;
        int quoteZ = quotesParent?.ZIndex ?? 0;
        var quoteOverlaps = new List<string>();
        if (quote is { } q && q.IsVisibleInTree() && q.GetContentHeight() > 1f &&
            screen.GetNodeOrNull<Control>("%CreatureContainer") is { } tableau)
        {
            Vector2 scale = q.GetGlobalTransformWithCanvas().Scale;
            Rect2 text = new(q.GetGlobalRect().Position, new Vector2(q.Size.X * MathF.Abs(scale.X), q.GetContentHeight() * MathF.Abs(scale.Y)));
            foreach (Node child in tableau.GetChildren())
            {
                if (child is not NCreatureVisuals body || !GodotObject.IsInstanceValid(body)) continue;
                Rect2 drawn = body.Bounds.GetGlobalRect();
                if (!text.Intersects(drawn)) continue;
                // The quote may sit over a body as long as it is drawn above it (the launcher lifts the banner): only a
                // body drawn over the quote makes the text unreadable.
                if (quoteZ > body.ZIndex) continue;
                quoteOverlaps.Add($"body drawn over quote: {drawn} x {text} (quote z {quoteZ} vs body z {body.ZIndex})");
            }
        }
        _report.Check("game_over_quote_clear_or_above_bodies", quoteOverlaps.Count == 0,
            quoteOverlaps.Count == 0
                ? $"clear; quote z={quoteZ}"
                : $"clashes={(quoteOverlaps.Count == 0 ? "none" : string.Join("; ", quoteOverlaps.Take(6)))}");

        _report.Check("game_over_unclipped_rows_clear_of_banner_and_art", clashes.Count == 0,
            $"banner={(banner is { } vb ? vb.GetGlobalRect().ToString() : "none")} " +
            $"art={(art is { } va ? va.GetGlobalRect().ToString() : "none")} " +
            $"clashes={(clashes.Count == 0 ? "none" : string.Join("; ", clashes.Take(6)))}");
    }

    private void CheckGameOverButton(Control button, string name)
    {
        Rect2 rect = button.GetGlobalRect();
        _report.Check(name + "_safe_touch_target", rect.Size.Y >= PortraitBands.MinTarget - 1f && PortraitSafeRect().Encloses(rect),
            $"rect={rect} minimum={PortraitBands.MinTarget}");
    }
}
