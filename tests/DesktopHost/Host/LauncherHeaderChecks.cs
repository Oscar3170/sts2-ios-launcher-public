using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens;
using MegaCrit.Sts2.Core.Nodes.TopBar;
using MegaCrit.Sts2.Core.Runs;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The launcher's answers for the shared header-inventory, potions and orbs scenarios.
/// Each method implements a partial declared in HarnessCore.cs (or OrbChecks.cs). Everything is found by node name,
/// measured from live rectangles and the launcher's own <see cref="PortraitBands"/>.
/// </summary>
internal sealed partial class TestDriver
{
    // The Batch C node names. The launcher's portrait layout owns them; they are looked up by name, never by type.
    private const string StoneHeaderName = "PhoneStoneHeader";
    private const string PotionRowName = "PhonePotionRow";
    private const string RelicRowName = "PhoneRelicRow";
    private const string ModifierButtonName = "PhoneModifierButton";
    private const string RemovedRelicPagerName = "PortraitRelicPager";

    /// <summary>The torn edge under the stone may reach this far past the band it covers.</summary>
    private const float StoneTornEdgeAllowance = 40f;
    /// <summary>A relic holder's touch target in the wrapped band.</summary>
    private const float RelicHolderTarget = 96f;
    /// <summary>Value labels stay readable down to nine phone points.</summary>
    private const float OrbValueFontFloor = 26f;
    /// <summary>One drawn frame of a 0.45 s retarget never moves an orb this far, or faster than the speed below.</summary>
    private const float OrbMotionStepLimit = 60f;
    private const float OrbMotionSpeedLimit = 3600f;

    /// <summary>How far the top row rises into the cap, and how far its touch box grows downward when the cap is too short.</summary>
    private static float TopRowRise(float cap) =>
        MathF.Min(PortraitBands.MinTarget - (PortraitBands.TopBarRowHeight - PortraitBands.TopBarRowInset),
            MathF.Max(0f, cap + PortraitBands.TopBarRowInset));

    private static float TopRowDrop(float cap) =>
        PortraitBands.MinTarget - (PortraitBands.TopBarRowHeight - PortraitBands.TopBarRowInset) - TopRowRise(cap);

    /// <summary>A top-bar button's drawn part: a MarginContainer's content, without the invisible touch box grown below it.</summary>
    private static Rect2 DrawnTopBarRect(Control control) =>
        control.IsClass("MarginContainer") && control.GetNodeOrNull<Control>("Control") is { } content
            ? content.GetGlobalRect() : control.GetGlobalRect();

    private static T? FindNamed<T>(Node root, string name) where T : Node =>
        FindAll<T>(root).FirstOrDefault(node => !node.IsQueuedForDeletion() && node.Name == name);

    private static Control? TopBarControl(Node topBar, string uniqueName) =>
        topBar.GetNodeOrNull<Control>(uniqueName) ?? topBar.FindChild(uniqueName.TrimStart('%'), true, false) as Control;

    private static bool IsUnder(Node node, Node ancestor)
    {
        for (Node? parent = node; parent is not null; parent = parent.GetParent())
            if (parent == ancestor) return true;
        return false;
    }

    private partial Rect2 PortraitSafeRect()
    {
        float cap = PortraitLayout.ComputeCapHeight(_tree.Root);
        Rect2 canvas = _tree.Root.GetViewport().GetVisibleRect();
        return new Rect2(0f, cap, canvas.Size.X, Math.Max(0f, canvas.Size.Y - cap));
    }

    // ---- header-inventory ----------------------------------------------------------------------------------------------

    // The launcher has one presented relic mode: the wrapped row in its fixed band. No pager, no per-page pill.
    private partial IEnumerable<string> RelicPresentations(int count) => new[] { "wrapped" };

    private partial async Task CheckRelicPresentationAsync(NRelicInventory inventory, int count, string presentation)
    {
        string id = $"header_{count}_{presentation}";
        float cap = PortraitLayout.ComputeCapHeight(_tree.Root);
        Rect2 band = new(0f, cap + PortraitBands.TopBarHeight, SafeAreaCanvas.PortraitWidth, PortraitBands.RelicBandHeight);
        ScrollContainer? row = FindNamed<ScrollContainer>(_tree.Root, RelicRowName);
        _report.Check(id + "_relic_row", row is not null, $"{RelicRowName} present");
        _report.Check(id + "_no_relic_pager", FindNamed<Control>(_tree.Root, RemovedRelicPagerName) is null,
            "the wrapped band has no page control");
        if (row is null) return;
        Rect2 rowRect = row.GetGlobalRect();
        _report.Check(id + "_inside_fixed_band", rowRect.Position.Y >= band.Position.Y - 2f
            && rowRect.End.Y <= band.End.Y + 2f && rowRect.Size.X <= band.Size.X + 1f,
            $"row={rowRect} band={band}");
        _report.Check(id + "_inventory_in_row", IsUnder(inventory, row),
            $"inventory parent={inventory.GetParent()?.Name}");
        _report.Check(id + "_count", inventory.RelicNodes.Count == count, $"relics={inventory.RelicNodes.Count}");
        int expectedLines = count >= 41 ? 4 : 2;
        _report.Check(id + "_wrapped_lines", inventory.GetLineCount() >= expectedLines,
            $"native lines={inventory.GetLineCount()} (expected >={expectedLines} for {count} relics)");
        _report.Check(id + "_targets", inventory.RelicNodes.All(holder =>
                holder.GetGlobalRect().Size.X >= RelicHolderTarget - 1f && holder.GetGlobalRect().Size.Y >= RelicHolderTarget - 1f),
            $"first={inventory.RelicNodes[0].GetGlobalRect()} minimum={RelicHolderTarget:F0}");

        bool Visible(NRelicInventoryHolder holder) => rowRect.Grow(2f).Encloses(holder.GetGlobalRect());
        var first = inventory.RelicNodes.First();
        var last = inventory.RelicNodes.Last();
        // The tip set a press creates is owned by the holder under the pointer. Between the relic animations' end and this
        // interaction the inventory keeps the relic nodes but the flow may still be settling, so the instance under the
        // pointed centre is resolved at press time; pressing a cached instance whose place now belongs to another holder
        // would create the tip and the native inspection for the live one and find neither for ours.
        NRelicInventoryHolder PressTarget(NRelicInventoryHolder intended)
        {
            Vector2 point = intended.GetGlobalRect().GetCenter();
            foreach (NRelicInventoryHolder live in inventory.RelicNodes)
                if (!live.IsQueuedForDeletion() && live.IsInsideTree() && live.GetGlobalRect().HasPoint(point)) return live;
            return intended;
        }
        // The row keeps its offset when the relic count grows (it is the same clip), so start each presentation at the top:
        // the first relic's reachability is what this checks, not where the previous pass left the scroll.
        for (int i = 0; i < 12 && !Visible(first); i++)
        {
            Vector2 start = rowRect.Position + new Vector2(rowRect.Size.X * 0.5f, rowRect.Size.Y * 0.2f);
            await _pointer.DragAsync(start, start + new Vector2(0f, rowRect.Size.Y * 0.7f), 20);
            await WaitSecondsAsync(0.2);
        }
        await CaptureAsync(id + "_initial");
        _report.Check(id + "_first_reachable", Visible(first),
            $"first={first.GetGlobalRect()} row={rowRect} scroll={row.ScrollVertical}");
        if (Visible(first)) await CheckRelicHoldAndInspectAsync(id + "_first", PressTarget(first));
        for (int i = 0; i < 12 && !Visible(last); i++)
        {
            Vector2 start = rowRect.Position + new Vector2(rowRect.Size.X * 0.5f, rowRect.Size.Y * 0.8f);
            await _pointer.DragAsync(start, start - new Vector2(0f, rowRect.Size.Y * 0.7f), 20);
            await WaitSecondsAsync(0.2);
            _report.Check(id + "_swipe_no_tip", !FindAll<NHoverTipSet>(_tree.Root).Any(t =>
                !t.IsQueuedForDeletion() && t.IsVisibleInTree() && NativeTipOwner(t) is NRelicInventoryHolder),
                "a scrolling finger does not inspect a relic");
        }
        bool visible = Visible(last);
        _report.Check(id + "_last_reachable", visible,
            $"scroll={row.ScrollVertical} last={last.GetGlobalRect()} row={rowRect}");
        _report.Check(id + "_swipe_no_inspection", !FindAll<NInspectRelicScreen>(_tree.Root).Any(s => s.IsVisibleInTree()),
            "scroll must not open the native inspector");
        await CaptureAsync(id + "_scrolled");
        if (visible) await CheckRelicHoldAndInspectAsync(id, PressTarget(last), capture: true);
    }

    private partial async Task CheckHeaderPotionRowAsync(Player player)
    {
        var ui = NRun.Instance!.GlobalUi;
        var topBar = ui.TopBar;
        float cap = PortraitLayout.ComputeCapHeight(_tree.Root);
        Rect2 canvas = _tree.Root.GetViewport().GetVisibleRect();
        Rect2 stoneExpected = new(0f, 0f, canvas.Size.X, cap + PortraitBands.TopBarHeight);
        Control? stoneNode = FindNamed<Control>(_tree.Root, StoneHeaderName);
        Rect2 stone = stoneNode?.GetGlobalRect() ?? default;
        // The cap is the screen's real top inset, not a fixed notch band: 0 at 9:16 with insets 0,0, 162 units at
        // 1080x2340 with 0.069. The stone's art starts at the canvas top (behind an island when there is one) and the
        // header's row is drawn from the cap down, so no control is placed above the canvas.
        float expectedCap = PortraitHeaderMath.CapHeight(SafeAreaCanvas.HasTopInset, SafeAreaCanvas.TopInset(), canvas.Size.Y);
        float expectedRowTop = cap + PortraitBands.TopBarRowInset - TopRowRise(cap);
        Rect2 settingsRect = TopBarControl(topBar, "%PauseButton")?.GetGlobalRect() ?? default;
        _report.Check("portrait_cap_matches_inset",
            Mathf.Abs(cap - expectedCap) <= 0.5f
            && Mathf.Abs(stone.Position.Y) <= 1f
            && Mathf.Abs(stone.End.Y - (cap + PortraitBands.TopBarHeight)) <= StoneTornEdgeAllowance
            && expectedRowTop >= 0f && Mathf.Abs(settingsRect.Position.Y - expectedRowTop) <= 2f,
            $"cap={cap:F0} expected={expectedCap:F0} inset={SafeAreaCanvas.TopInset():R} canvasH={canvas.Size.Y:F0} " +
            $"stoneTop={stone.Position.Y:F0} rowTop={settingsRect.Position.Y:F0} expectedRowTop={expectedRowTop:F0}");
        _report.Check("header_stone_header", stoneNode is NinePatchRect
                && Mathf.Abs(stone.Position.X) <= 1f && Mathf.Abs(stone.Position.Y) <= 1f
                && stone.Size.X >= canvas.Size.X - 1f
                && stone.End.Y >= stoneExpected.End.Y - 1f
                && stone.End.Y <= stoneExpected.End.Y + StoneTornEdgeAllowance,
            $"stone={stone} expected={stoneExpected} ninePatch={stoneNode is NinePatchRect}");
        if (stoneNode is NinePatchRect stonePatch)
        {
            TextureRect? background = topBar.GetNodeOrNull<TextureRect>("%BgImage");
            _report.Check("header_stone_uses_native_texture",
                background?.Texture is not null && stonePatch.Texture == background.Texture,
                "the stone reuses the top bar's cracked-stone texture");
            _report.Check("header_native_bar_covered",
                background is null || !background.IsVisibleInTree() || stone.Encloses(background.GetGlobalRect()),
                $"the stretched native bar no longer shows past the stone: {background?.GetGlobalRect()}");
        }

        // The status row: health, gold, room, floor and boss, then the settings button, all above the potion row.
        (string Name, Control? Node)[] statusRow =
        {
            ("hp", TopBarControl(topBar, "%TopBarHp")),
            ("gold", TopBarControl(topBar, "%TopBarGold")),
            ("room", TopBarControl(topBar, "%RoomIcon")),
            ("floor", TopBarControl(topBar, "%FloorIcon")),
            ("boss", TopBarControl(topBar, "%BossIcon")),
            ("settings", TopBarControl(topBar, "%PauseButton")),
        };
        float rowBottom = cap + PortraitBands.TopBarRowHeight;
        foreach ((string name, Control? node) in statusRow)
        {
            if (node is null)
            {
                _report.Check($"header_top_row_{name}", false, "control missing from the top bar");
                continue;
            }
            Rect2 rect = name == "settings" ? DrawnTopBarRect(node) : node.GetGlobalRect();
            _report.Check($"header_top_row_{name}", stone.Encloses(rect)
                    && rect.End.Y <= rowBottom + 4f && rect.Position.Y >= MathF.Max(0f, cap - TopRowRise(cap)),
                $"{name}={rect} stone={stone} topRowBottom={rowBottom:F0} safeCap={cap:F0}");
        }
        Control? hpNode = statusRow[0].Node, settingsNode = statusRow[5].Node;
        if (settingsNode is not null)
        {
            Rect2 touch = settingsNode.GetGlobalRect();
            _report.Check("header_top_row_settings_touch",
                touch.Size.X >= PortraitBands.MinTarget - 1f && touch.Size.Y >= PortraitBands.MinTarget - 1f,
                $"touch={touch} minimum={PortraitBands.MinTarget:F0} drop={TopRowDrop(cap):F0} cap={cap:F0}");
        }
        if (hpNode is not null && settingsNode is not null)
        {
            float[] others = statusRow.Skip(1).Select(entry => entry.Node).Where(node => node is not null)
                .Select(node => node!.GetGlobalRect().Position.X).ToArray();
            _report.Check("header_top_row_order", hpNode.GetGlobalRect().Position.X <= others.Min()
                    && settingsNode.GetGlobalRect().Position.X >= others.Max() - 1f,
                $"hp={hpNode.GetGlobalRect()} others={string.Join(",", others.Select(x => x.ToString("F0")))} " +
                $"settings={settingsNode.GetGlobalRect()}");
        }

        ScrollContainer? row = FindNamed<ScrollContainer>(_tree.Root, PotionRowName);
        _report.Check("header_potion_row", row is not null, $"{PotionRowName} present");
        if (row is null) return;
        Rect2 rowRect = row.GetGlobalRect();
        _report.Check("header_potion_clip_contained", stone.Encloses(rowRect), $"row={rowRect} stone={stone}");
        // The stone's torn bottom edge: the nine-patch's last 20 units are the dark rim and the transparent tear, so the stone
        // drawn behind the tray ends 20 units above its rectangle. The tray's dark box must end on the stone, not over the tear.
        const float StoneTornEdge = 20f;
        if (topBar.PotionContainer.GetNodeOrNull<Control>("PotionBg") is { } trayBox)
        {
            Rect2 tray = trayBox.GetGlobalRect();
            _report.Check("header_potion_tray_on_stone", tray.End.Y <= stone.End.Y - StoneTornEdge,
                $"tray={tray} stoneSurfaceEnd={stone.End.Y - StoneTornEdge:F0}");
        }
        else
        {
            _report.Fail("header_potion_tray_on_stone", "no PotionBg under the potion container");
        }
        var holders = topBar.PotionContainer.Holders;
        _report.Check("header_expanded_potions", holders.Count == 12, $"holders={holders.Count}");
        foreach (var (holder, i) in holders.Select((h, i) => (h, i)))
        {
            Rect2 rect = holder.GetGlobalRect();
            bool inView = rowRect.Grow(2f).Encloses(rect);
            // The slot is the header's own size (37 points), not the 44 point minimum: a 44 point slot reached into the
            // top row over the ascension badge.
            _report.Check($"header_potion_{i}_in_stone",
                rect.Position.Y >= stone.Position.Y && rect.End.Y <= stone.End.Y
                && (!inView || (rect.Position.X >= stone.Position.X - 1f && rect.End.X <= stone.End.X + 1f))
                && rect.Size.X >= PortraitBands.PotionSlotSize - 1f && rect.Size.Y >= PortraitBands.PotionSlotSize - 1f,
                $"holder={rect} stone={stone} inView={inView} slot={PortraitBands.PotionSlotSize:F0}");
        }
        int potionCount = player.Potions.Count();
        var lastPotion = holders.Last();
        for (int i = 0; i < 8 && !rowRect.Grow(2f).Encloses(lastPotion.GetGlobalRect()); i++)
        {
            Vector2 start = rowRect.Position + new Vector2(rowRect.Size.X - 80f, rowRect.Size.Y - 30f);
            await _pointer.DragAsync(start, start - new Vector2(1000f, 0f), 25);
            await WaitSecondsAsync(0.2);
        }
        _report.Check("header_last_potion_reachable", rowRect.Grow(2f).Encloses(lastPotion.GetGlobalRect()),
            $"scroll={row.ScrollHorizontal} last={lastPotion.GetGlobalRect()} row={rowRect}");
        _report.Check("header_potion_swipe_does_not_consume", player.Potions.Count() == potionCount,
            $"potions={player.Potions.Count()}/{potionCount}");
        _report.Check("header_potion_swipe_does_not_open_popup",
            !FindAll<NPotionPopup>(_tree.Root).Any(p => p.IsVisibleInTree()), "swiping must not activate a potion");

        // Map and deck sit to the right of the potion row.
        Control? map = TopBarControl(topBar, "%Map");
        Control? deck = TopBarControl(topBar, "%Deck");
        Rect2 mapRect = map?.GetGlobalRect() ?? default;
        Rect2 deckRect = deck?.GetGlobalRect() ?? default;
        bool beside = map is not null && deck is not null
            && mapRect.Position.X >= rowRect.End.X - 2f && deckRect.Position.X >= rowRect.End.X - 2f
            && mapRect.Position.Y >= cap + PortraitBands.TopBarRowHeight - 2f
            && stone.Encloses(mapRect) && stone.Encloses(deckRect);
        bool targets = mapRect.Size.X >= PortraitBands.MinTarget - 1f && mapRect.Size.Y >= PortraitBands.MinTarget - 1f
            && deckRect.Size.X >= PortraitBands.MinTarget - 1f && deckRect.Size.Y >= PortraitBands.MinTarget - 1f;
        _report.Check("header_map_deck_right_of_potions", beside && targets,
            $"row={rowRect} map={mapRect} deck={deckRect} minimum={PortraitBands.MinTarget:F0}");
    }

    private partial ScrollContainer? PotionRow() => FindNamed<ScrollContainer>(_tree.Root, PotionRowName);

    // ---- header indicators (ascension badge, achievement lock, modifier chips) ------------------------------------------

    /// <summary>
    /// The stone header's top row for a locked ascension run: the native ascension badge (and its number) and the
    /// achievement lock, beside the health, gold, room, floor and boss icons and the settings button the header always shows,
    /// plus the run's one launcher-owned modifier button in place of the native chips. Each visible indicator is inside the
    /// stone and the row, inside the phone's width and clear of every other one; the potion row below keeps its band and map
    /// and deck keep their 44 point buttons beside it.
    /// </summary>
    partial void CheckHeaderIndicatorLayout(Control topBar, RunState state, string phase)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        float cap = PortraitLayout.ComputeCapHeight(_tree.Root);
        Rect2 canvas = _tree.Root.GetViewport().GetVisibleRect();
        Control? stoneNode = FindNamed<Control>(_tree.Root, StoneHeaderName);
        Rect2 stone = stoneNode?.GetGlobalRect() ?? default;
        _report.Check($"header_indicators_{phase}_stone", stoneNode is NinePatchRect
                && Mathf.Abs(stone.Position.X) <= 1f && Mathf.Abs(stone.Position.Y) <= 1f
                && stone.Size.X >= canvas.Size.X - 1f && stone.End.Y >= cap + PortraitBands.TopBarHeight - 1f,
            $"stone={stone} cap={cap:F0}");
        float rowBottom = cap + PortraitBands.TopBarRowHeight;
        float rowTop = MathF.Max(0f, cap + PortraitBands.TopBarRowInset - TopRowRise(cap));

        List<(string Name, Control? Node)> entries =
        [
            ("hp", TopBarControl(topBar, "%TopBarHp")),
            ("gold", TopBarControl(topBar, "%TopBarGold")),
            ("room", TopBarControl(topBar, "%RoomIcon")),
            ("floor", TopBarControl(topBar, "%FloorIcon")),
            ("boss", TopBarControl(topBar, "%BossIcon")),
            ("ascension", TopBarControl(topBar, "%AscensionIcon")),
            ("ascension_number", TopBarControl(topBar, "%AscensionLabel")),
            ("lock", TopBarControl(topBar, "%AchievementLock")),
            ("settings", TopBarControl(topBar, "%PauseButton")),
        ];
        if (state.Modifiers.Count > 0) entries.Add(("modifiers", TopBarControl(topBar, ModifierButtonName)));
        var boxes = new List<(string Name, Rect2 Rect)>();
        foreach ((string name, Control? node) in entries)
        {
            bool expectedVisible = name != "ascension_number" || phase == "ascension";
            if (node is null || node.IsVisibleInTree() != expectedVisible)
            {
                _report.Check($"header_indicators_{phase}_{name}", false,
                    $"{name}: node={node?.GetType().Name ?? "missing"} visible={node?.IsVisibleInTree()} expected={expectedVisible}");
                continue;
            }
            Rect2 rect = name == "settings" ? DrawnTopBarRect(node) : node.GetGlobalRect();
            if (name == "settings")
            {
                Rect2 touch = node.GetGlobalRect();
                _report.Check($"header_indicators_{phase}_settings_touch",
                    touch.Size.X >= PortraitBands.MinTarget - 1f && touch.Size.Y >= PortraitBands.MinTarget - 1f,
                    $"touch={touch} minimum={PortraitBands.MinTarget:F0} drop={TopRowDrop(cap):F0} cap={cap:F0}");
            }
            if (name == "ascension_number")
            {
                // The label's control box carries the game's line height plus outline, which reaches past the row while the
                // digits themselves sit on the flame. Judge the drawn ink (LauncherHandChecks' glyph rect: theme font, string
                // advance, ascent, alignment, outline, through the badge's 1.3 scale), so an oversized empty box cannot hide
                // a number drawn outside the row and the potion/map clearance below sees real pixels.
                rect = AscensionNumberInk((Label)node);
            }
            _report.Check($"header_indicators_{phase}_{name}_in_row",
                stone.Encloses(rect) && rect.Position.X >= -1f && rect.End.X <= canvas.Size.X + 1f
                && rect.Position.Y >= rowTop && rect.End.Y <= rowBottom + 4f,
                $"{name}={rect} stone={stone} row=({rowTop:F0}..{rowBottom:F0}) canvas={canvas.Size}");
            boxes.Add((name, rect));
        }

        for (int i = 0; i < boxes.Count; i++)
        for (int j = i + 1; j < boxes.Count; j++)
        {
            // The number is drawn inside its badge: one indicator in two boxes.
            bool sameBadge = boxes[i].Name == "ascension" && boxes[j].Name == "ascension_number"
                || boxes[i].Name == "ascension_number" && boxes[j].Name == "ascension";
            if (sameBadge) continue;
            _report.Check($"header_indicators_{phase}_{boxes[i].Name}_clear_of_{boxes[j].Name}",
                !boxes[i].Rect.Intersects(boxes[j].Rect),
                $"{boxes[i].Name}={boxes[i].Rect} {boxes[j].Name}={boxes[j].Rect}");
        }
        if (phase == "ascension" && TopBarControl(topBar, "%AscensionLabel") is { } number)
        {
            float readSize = number.GetThemeFontSize("font_size") * number.GetGlobalTransform().Scale.Abs().Y;
            _report.Check($"header_indicators_{phase}_ascension_number_readable", readSize >= 24f,
                $"number font={readSize:F1} (9 points at 1080 across)");
        }

        // The native modifier chips stay hidden in portrait: the phone's top row has no room for a 44 point row of them.
        // The chips themselves are untouched children of the bar, so a landscape run keeps the game's own presentation.
        Control? modifierBox = TopBarControl(topBar, "%Modifiers");
        Control[] chips = modifierBox?.GetChildren().OfType<Control>()
            .Where(child => !child.IsQueuedForDeletion()).ToArray() ?? [];
        _report.Check($"header_indicators_{phase}_chip_count", chips.Length == state.Modifiers.Count,
            $"native chips={chips.Length} run modifiers={state.Modifiers.Count}");
        _report.Check($"header_indicators_{phase}_chips_hidden", modifierBox is not null && !modifierBox.IsVisibleInTree()
                && chips.All(chip => !chip.IsVisibleInTree()),
            $"container={modifierBox?.IsVisibleInTree()} chips={chips.Count(chip => chip.IsVisibleInTree())}/{chips.Length} visible");

        // The native room icon sits in the row immediately left of the floor number, at the boss icon's size.
        Control? roomIcon = TopBarControl(topBar, "%RoomIcon");
        Control? floorBox = TopBarControl(topBar, "%FloorIcon");
        if (roomIcon is NTopBarRoomIcon && floorBox is not null)
        {
            Rect2 roomRect = roomIcon.GetGlobalRect();
            Rect2 floorRect = floorBox.GetGlobalRect();
            TextureRect? roomArt = roomIcon.GetNodeOrNull<TextureRect>("Icon");
            TextureRect? floorArt = floorBox.GetNodeOrNull<TextureRect>("FloorIconPositioner/FloorInfoIcon");
            TextureRect? bossArt = TopBarControl(topBar, "%BossIcon")?.GetNodeOrNull<TextureRect>("Icon");
            Rect2 roomInk = roomArt is null ? default : DrawnTextureRect(roomArt);
            Rect2 floorInk = floorArt is null ? default : DrawnTextureRect(floorArt);
            Rect2 bossInk = bossArt is null ? default : DrawnTextureRect(bossArt);
            bool texture = roomArt?.Texture is not null;
            bool sized = texture && roomInk.Size.X >= 40f && roomInk.Size.X <= 64f
                && (bossInk.Size.X <= 0f || Mathf.Abs(roomInk.Size.X - bossInk.Size.X) <= 8f)
                && (floorInk.Size.X <= 0f || roomInk.Size.X <= floorInk.Size.X + 1f);
            _report.Check($"header_indicators_{phase}_room_icon",
                roomIcon.IsVisibleInTree() && texture && sized
                && (bossInk.Size.Y <= 0f || Mathf.Abs(roomInk.GetCenter().Y - bossInk.GetCenter().Y) <= 8f),
                $"room={roomRect} ink={roomInk} bossInk={bossInk} floorInk={floorInk} texture={roomArt?.Texture?.ResourcePath}");
            _report.Check($"header_indicators_{phase}_room_left_of_floor",
                roomRect.End.X <= floorRect.Position.X + 1f && floorRect.Position.X - roomRect.End.X <= 12f,
                $"room={roomRect} floor={floorRect}");
            Control? gold = TopBarControl(topBar, "%TopBarGold");
            Rect2 goldRect = gold?.GetGlobalRect() ?? default;
            Rect2 settingsTouch = TopBarControl(topBar, "%PauseButton")?.GetGlobalRect() ?? default;
            _report.Check($"header_indicators_{phase}_room_clear_of_gold_settings",
                gold is not null && roomRect.Position.X >= goldRect.End.X + 4f
                && settingsTouch.Size.X > 1f && roomRect.End.X <= settingsTouch.Position.X - 4f,
                $"room={roomRect} gold={goldRect} settingsTouch={settingsTouch}");
        }
        else
        {
            _report.Check($"header_indicators_{phase}_room_icon", false,
                $"room={roomIcon?.GetType().Name ?? "missing"} floor={floorBox?.GetType().Name ?? "missing"}");
        }

        // One launcher-owned button carries every modifier chip: the first chip's icon and a count. Tapping it stacks the
        // chips' native tips under the header.
        Control? modifierButton = TopBarControl(topBar, ModifierButtonName);
        if (state.Modifiers.Count > 0)
        {
            TextureRect? buttonArt = modifierButton?.GetNodeOrNull<TextureRect>("Icon");
            TextureRect? firstChipArt = chips.FirstOrDefault()?.GetNodeOrNull<TextureRect>("Icon");
            Label? countBadge = modifierButton?.GetNodeOrNull<Label>("Count");
            Rect2 touch = modifierButton is null ? default : HostTouchTargetBox(modifierButton);
            int buttons = FindAll<Control>(topBar)
                .Count(node => node.Name == ModifierButtonName && !node.IsQueuedForDeletion());
            bool badge = chips.Length > 1
                ? countBadge is { Visible: true } && countBadge.Text.Trim() == chips.Length.ToString()
                : countBadge is null || !countBadge.Visible;
            _report.Check($"header_indicators_{phase}_modifier_button",
                buttons == 1 && modifierButton is not null && modifierButton.IsVisibleInTree()
                && buttonArt is { Texture: not null } icon && firstChipArt is { Texture: not null } chipIcon
                && icon.Texture == chipIcon.Texture && badge
                && touch.Size.X >= PortraitBands.MinTarget - 1f && touch.Size.Y >= PortraitBands.MinTarget - 1f
                && stone.Encloses(touch),
                $"button={modifierButton?.GetGlobalRect()} touch={touch} buttons={buttons} " +
                $"icon={buttonArt?.Texture?.ResourcePath} chipIcon={firstChipArt?.Texture?.ResourcePath} " +
                $"badge='{countBadge?.Text}' visible={countBadge?.IsVisibleInTree()} count={chips.Length}");
        }

        ScrollContainer? potionRow = PotionRow();
        if (potionRow is not null)
        {
            Rect2 potionRect = potionRow.GetGlobalRect();
            bool clear = potionRect.Position.Y >= rowBottom - 2f && stone.Encloses(potionRect)
                && !boxes.Any(box => box.Rect.Intersects(potionRect));
            _report.Check($"header_indicators_{phase}_potion_row_clear", clear,
                $"potions={potionRect} stone={stone}");
        }

        foreach (string name in new[] { "%Map", "%Deck" })
        {
            Control? button = TopBarControl(topBar, name);
            Rect2 rect = button?.GetGlobalRect() ?? default;
            _report.Check($"header_indicators_{phase}_{name.TrimStart('%').ToLowerInvariant()}_clear",
                button is not null && button.IsVisibleInTree() && stone.Encloses(rect)
                && rect.Size.X >= PortraitBands.MinTarget - 1f && rect.Size.Y >= PortraitBands.MinTarget - 1f
                && !boxes.Any(box => box.Rect.Intersects(rect)),
                $"{name}={rect} minimum={PortraitBands.MinTarget:F0}");
        }
    }

    /// <summary>
    /// The part of a <see cref="TextureRect"/>'s box the texture is actually drawn in, in canvas units, for the keep-aspect
    /// stretch modes the header icons use. A 44 x 88 box holding a square icon draws a 44 x 44 picture centred in it.
    /// </summary>
    private static Rect2 DrawnTextureRect(TextureRect rect)
    {
        Rect2 box = rect.GetGlobalRect();
        if (rect.Texture is not { } texture) return box;
        Vector2 texel = texture.GetSize();
        if (texel.X <= 0f || texel.Y <= 0f) return box;
        TextureRect.StretchModeEnum mode = rect.StretchMode;
        if (mode is not (TextureRect.StretchModeEnum.KeepAspectCentered or TextureRect.StretchModeEnum.KeepAspectCovered))
            return box;
        float fit = mode == TextureRect.StretchModeEnum.KeepAspectCovered
            ? Mathf.Max(box.Size.X / texel.X, box.Size.Y / texel.Y)
            : Mathf.Min(box.Size.X / texel.X, box.Size.Y / texel.Y);
        Vector2 drawn = texel * fit;
        return new Rect2(box.GetCenter() - drawn * 0.5f, drawn);
    }

    /// <summary>
    /// The one modifier button a custom/daily run shows in portrait: a tap stacks every run modifier's native hover tip
    /// (name and description) under the header; a second tap, or a tap anywhere else, closes them again. The tips are
    /// owned by the button and are found through the game's own tip container.
    /// </summary>
    private partial async Task CheckHeaderModifierTipsAsync(Control topBar, RunState state, string phase)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        string id = $"header_indicators_{phase}_modifier";
        Control? button = TopBarControl(topBar, ModifierButtonName);
        if (button is null || state.Modifiers.Count == 0)
        {
            _report.Check(id + "_tips", false,
                $"button={button?.GetType().Name ?? "missing"} modifiers={state.Modifiers.Count}");
            return;
        }
        Control tapTarget = button.GetNodeOrNull<Control>("Icon") ?? button;
        await _pointer.TapAsync(CenterOf(tapTarget));
        NHoverTipSet? tipSet = null;
        bool shown = await WaitOrFalseAsync(() =>
        {
            NHoverTipSet? found = FindNativeTip(button);
            if (found is null || !found.IsVisibleInTree()) return false;
            tipSet = found;
            return true;
        }, ShortWait);
        _report.Check(id + "_tips_shown", shown, shown ? "a tap opened the modifier tips" : "no tip set appeared for the button");
        if (!shown || tipSet is null) return;
        await WaitFramesAsync(3);
        tipSet = FindNativeTip(button) ?? tipSet;
        Control? text = tipSet.GetNodeOrNull<Control>("textHoverTipContainer");
        Control[] tips = text?.GetChildren().OfType<Control>()
            .Where(child => !child.IsQueuedForDeletion() && child.Visible && child.Size.X > 1f && child.Size.Y > 1f)
            .ToArray() ?? [];
        string[] titles = tips
            .Select(tip => (tip.FindChild("Title", true, false) as Label)?.Text?.Trim() ?? "")
            .OrderBy(title => title, StringComparer.Ordinal).ToArray();
        string[] expected = state.Modifiers.Select(modifier => modifier.Title.GetFormattedText().Trim())
            .OrderBy(title => title, StringComparer.Ordinal).ToArray();
        bool descriptions = tips.All(tip =>
            tip.FindChild("Description", true, false) is RichTextLabel description
            && !string.IsNullOrWhiteSpace(description.Text));
        float cap = PortraitLayout.ComputeCapHeight(_tree.Root);
        float headerBottom = cap + PortraitBands.TopBarHeight;
        Rect2 tipRect = text?.GetGlobalRect() ?? default;
        bool placed = text is not null && tipRect.Size.X > 1f && tipRect.Size.Y > 1f
            && tipRect.Position.Y >= headerBottom - 2f && PortraitSafeRect().Encloses(tipRect);
        _report.Check(id + "_tips",
            shown && tips.Length == state.Modifiers.Count && titles.SequenceEqual(expected) && descriptions && placed,
            $"tips={tips.Length}/{state.Modifiers.Count} titles=[{string.Join("|", titles)}] expected=[{string.Join("|", expected)}] " +
            $"descriptions={descriptions} rect={tipRect} headerBottom={headerBottom:F0}");

        await _pointer.TapAsync(CenterOf(tapTarget));
        bool closed = await WaitOrFalseAsync(() => FindNativeTip(button) is null, ShortWait);
        _report.Check(id + "_second_tap_closes", closed, closed ? "the second tap closed the tips" : "the tips stayed open");

        await _pointer.TapAsync(CenterOf(tapTarget));
        bool reopened = await WaitOrFalseAsync(() => FindNativeTip(button) is not null, ShortWait);
        Vector2 outside = _tree.Root.GetVisibleRect().GetCenter() with { Y = PortraitSafeRect().Position.Y + 60f };
        await _pointer.TapAsync(outside);
        bool outsideClosed = await WaitOrFalseAsync(() => FindNativeTip(button) is null, ShortWait);
        _report.Check(id + "_outside_tap_closes", reopened && outsideClosed,
            $"reopened={reopened} closedOutside={outsideClosed} outside={outside}");
    }

    /// <summary>
    /// The drawn digits of the ascension number (plus outline and shadow) in canvas units. Godot centres a Label's line box -
    /// ascent plus descent - in its control and draws the glyphs from the line's top; LauncherHandChecks' glyph helper centres
    /// the ascent alone, which puts the same ink a descent/2 lower. The badge stands directly on the potion row, so that half
    /// line decides clearance: this is the text the player sees, not the label's oversized box.
    /// </summary>
    private static Rect2 AscensionNumberInk(Label label)
    {
        Font? font = label.GetThemeFont("font");
        string text = label.Text;
        if (font is null || string.IsNullOrWhiteSpace(text)) return label.GetGlobalRect();
        int size = label.GetThemeFontSize("font_size");
        Vector2 advance = font.GetStringSize(text, HorizontalAlignment.Left, -1f, size);
        float ascent = font.GetAscent(size);
        if (ascent <= 0f) ascent = advance.Y;
        float line = ascent + Math.Max(0f, font.GetDescent(size));
        Vector2 box = label.Size;
        float x = label.HorizontalAlignment switch
        {
            HorizontalAlignment.Center => (box.X - advance.X) * 0.5f,
            HorizontalAlignment.Right => box.X - advance.X,
            _ => 0f,
        };
        float y = label.VerticalAlignment switch
        {
            VerticalAlignment.Center => (box.Y - line) * 0.5f,
            VerticalAlignment.Bottom => box.Y - line,
            _ => 0f,
        };
        int outline = Math.Max(0, label.GetThemeConstant("outline_size"));
        Rect2 body = new(new Vector2(x - outline, y - outline), new Vector2(advance.X + outline * 2f, ascent + outline * 2f));
        int shadowX = label.GetThemeConstant("shadow_offset_x");
        int shadowY = label.GetThemeConstant("shadow_offset_y");
        if (shadowX != 0 || shadowY != 0)
            body = body.Merge(new Rect2(body.Position + new Vector2(shadowX, shadowY), body.Size));
        return label.GetGlobalTransform() * body;
    }

    // With the Batch C stone header the popup drops below the wrapped relic band; on a tree that does not have the stone
    // yet (the Batch B launcher) it keeps its pre-Batch-C placement, so only the frame and target rules apply.
    partial void CheckPotionPopupLayout(string prefix, NPotionPopup popup)
    {
        Control anchor = popup.GetNode<Control>("%HoverTipBounds");
        if (!SafeAreaCanvas.IsPortrait)
        {
            _report.Check(prefix + "_native_tip", FindNativeTip(anchor) is not null, "native popup description is visible");
            return;
        }
        float cap = PortraitLayout.ComputeCapHeight(_tree.Root);
        float bandBottom = cap + PortraitBands.RunContentTop;
        Rect2 safe = PortraitSafeRect();
        Rect2 visual = popup.GetGlobalRect();
        Rect2 use = Action(popup, "Use").GetGlobalRect();
        Rect2 discard = Action(popup, "Discard").GetGlobalRect();
        Control? stone = FindNamed<Control>(_tree.Root, StoneHeaderName);
        if (stone is null)
            _report.Check(prefix + "_panel_in_safe_frame", safe.Encloses(visual), $"popup={visual} safe={safe}");
        else
        {
            // The game opens the popup 1.5 holder heights below the holder's top, centred on it. Portrait keeps that place
            // (the popup is top level, so it can draw past the potion row) and may let it cover the relic band; it must stay
            // inside the screen and off the stone. It is not pushed below the band.
            Control? holder = popup.GetParent() as Control;
            Rect2 holderRect = holder?.GetGlobalRect() ?? default;
            float naturalTop = holderRect.Position.Y + holderRect.Size.Y * 1.5f;
            bool underSlot = holder is not null && Mathf.Abs(visual.Position.Y - naturalTop) <= 2f;
            bool overlapsBand = visual.Position.Y < bandBottom - 1f && visual.End.Y > cap + PortraitBands.TopBarHeight;
            _report.Check(prefix + "_panel_under_slot",
                underSlot && overlapsBand && safe.Encloses(visual) && !visual.Intersects(stone.GetGlobalRect()),
                $"popup={visual} naturalTop={naturalTop:F0} bandBottom={bandBottom:F0} safe={safe} stone={stone.GetGlobalRect()}");
        }
        _report.Check(prefix + "_phone_touch_targets",
            use.Size.X >= PortraitBands.MinTarget - 1f && use.Size.Y >= PortraitBands.MinTarget - 1f
            && discard.Size.X >= PortraitBands.MinTarget - 1f && discard.Size.Y >= PortraitBands.MinTarget - 1f
            && safe.Encloses(use) && safe.Encloses(discard),
            $"use={use} discard={discard} minimum={PortraitBands.MinTarget:F0}");
        Control[] tipParts = FindNativeTip(anchor)?.GetChildren().OfType<Control>()
            .Where(c => c.Visible && c.Size.X > 1f && c.Size.Y > 1f).ToArray() ?? [];
        _report.Check(prefix + "_native_tip_clear_of_actions", tipParts.Length > 0
            && tipParts.All(part => safe.Encloses(part.GetGlobalRect()) && !visual.Intersects(part.GetGlobalRect())),
            $"tips={string.Join(", ", tipParts.Select(p => p.GetGlobalRect()))}");
    }

    // ---- orbs ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The launcher's orb arc: one compact arc for six or more orbs (bottom above the body, inside the safe width), the
    /// accepted three-to-five looks untouched, values readable and unobscured, every orb below the stone and - on a tall
    /// phone - below the relic band. At 9:16 the arc may use the relic band but never the stone (decision 10).
    /// </summary>
    partial void CheckOrbLayout(string phase, int expected)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        NOrbManager? manager = FindAll<NOrbManager>(_tree.Root)
            .FirstOrDefault(m => !m.IsQueuedForDeletion() && GodotObject.IsInstanceValid(m));
        NOrb[] orbs = FindAll<NOrb>(_tree.Root)
            .Where(o => !o.IsQueuedForDeletion() && GodotObject.IsInstanceValid(o)).ToArray();
        if (orbs.Length == 0) return;
        Rect2 canvas = _tree.Root.GetViewport().GetVisibleRect();
        float canvasHeight = Math.Max(canvas.Size.Y, SafeAreaCanvas.MinPortraitHeight);
        float cap = PortraitLayout.ComputeCapHeight(_tree.Root);
        PortraitCombatBands bands = PortraitCombatMath.ComputeCombatBands(canvasHeight, cap + PortraitBands.RunContentTop);
        var iconRects = orbs.Select(orb => OrbVisualRect(orb, out _)).ToArray();
        float stoneBottom = cap + PortraitBands.TopBarHeight;
        float bandBottom = cap + PortraitBands.RunContentTop;

        if (manager is not null)
        {
            List<NOrb> ordered = AccessTools.FieldRefAccess<NOrbManager, List<NOrb>>("_orbs")(manager);
            _report.Check($"orbs_{phase}_native_order", ordered.Count > 1
                    && ordered.Zip(ordered.Skip(1), (right, left) => right.GlobalPosition.X > left.GlobalPosition.X - 1f).All(ok => ok),
                $"queue={ordered.Count} orbs={orbs.Length}: native index 0 is rightmost/front, the last is leftmost/back");
        }

        bool inCanvas = iconRects.All(rect =>
        {
            float margin = Math.Min(Math.Min(rect.Position.X, rect.Position.Y),
                Math.Min(canvas.End.X - rect.End.X, canvas.End.Y - rect.End.Y));
            return margin >= OrbEdgeMargin - 0.5f;
        });
        _report.Check($"orbs_{phase}_inside_canvas", inCanvas,
            $"rects={string.Join("; ", iconRects.Select(r => r.ToString()))} margin={OrbEdgeMargin:F0}");
        _report.Check($"orbs_{phase}_below_stone", iconRects.All(rect => rect.Position.Y >= stoneBottom - 1f),
            $"stoneBottom={stoneBottom:F0} rects={string.Join("; ", iconRects.Select(r => r.ToString()))}");
        if (bands.Compact)
            _report.Pass($"orbs_{phase}_relic_band_shared", $"compact canvas {canvas.Size.Y:F0}: the arc may use the relic band");
        else
            _report.Check($"orbs_{phase}_below_relic_band", iconRects.All(rect => rect.Position.Y >= bandBottom - 1f),
                $"bandBottom={bandBottom:F0} rects={string.Join("; ", iconRects.Select(r => r.ToString()))}");

        if (expected == 4)
        {
            bool overlapping = false;
            for (int i = 0; i < iconRects.Length && !overlapping; i++)
            for (int j = i + 1; j < iconRects.Length && !overlapping; j++)
                overlapping = iconRects[i].Intersects(iconRects[j]);
            _report.Check($"orbs_{phase}_four_no_overlap", !overlapping,
                "four orbs keep their own artwork, neither stacked nor clipped");
        }
        else if (expected == 5)
        {
            bool overlapping = false;
            for (int i = 0; i < iconRects.Length && !overlapping; i++)
            for (int j = i + 1; j < iconRects.Length && !overlapping; j++)
                overlapping = iconRects[i].Intersects(iconRects[j]);
            _report.Check($"orbs_{phase}_five_overlap", overlapping, "five orbs use the dense overlapping arc");
        }
        else if (expected >= 6)
        {
            NOrb rightmost = orbs.OrderByDescending(orb => orb.GlobalPosition.X).First();
            NOrb leftmost = orbs.OrderBy(orb => orb.GlobalPosition.X).First();
            float endsY = Math.Max(rightmost.GlobalPosition.Y, leftmost.GlobalPosition.Y);
            _report.Check($"orbs_{phase}_single_arc",
                orbs.Where(orb => orb != rightmost && orb != leftmost).All(orb => orb.GlobalPosition.Y < endsY),
                $"ends y={endsY:F1} middles={string.Join(",", orbs.Where(orb => orb != rightmost && orb != leftmost)
                    .Select(orb => orb.GlobalPosition.Y.ToString("F0")))}: no folded rows");
            NCreature? owner = manager is not null
                ? AccessTools.FieldRefAccess<NOrbManager, NCreature>("_creatureNode")(manager)
                : null;
            if (owner is not null && GodotObject.IsInstanceValid(owner))
            {
                float bodyTop = owner.Hitbox.GetGlobalRect().Position.Y;
                _report.Check($"orbs_{phase}_arc_above_body", iconRects.Max(rect => rect.End.Y) <= bodyTop + 1f,
                    $"arc bottom={iconRects.Max(rect => rect.End.Y):F1} body top={bodyTop:F1}");
            }
            float width = iconRects.Max(rect => rect.End.X) - iconRects.Min(rect => rect.Position.X);
            _report.Check($"orbs_{phase}_compact_width", width <= canvas.Size.X - 2f * OrbEdgeMargin,
                $"footprint={width:F1} canvas={canvas.Size.X:F0} margin={OrbEdgeMargin:F0}");
        }

        // Values: visible, readable at phone size, in the frame and never covered by a neighbouring orb's artwork - either
        // the label container draws above the arc or no other orb's art reaches the label.
        List<(NOrb Orb, Label Label)> labels = LabelPairs(orbs);
        bool aboveArt = orbs.All(orb =>
        {
            Control? container = orb.GetNodeOrNull<Control>("%LabelContainer") ?? orb.GetNodeOrNull<Control>("LabelContainer");
            return container is null || container.ZIndex >= 2;
        });
        bool clearOfArt = labels.All(entry => !orbs.Where(other => other != entry.Orb).Any(other =>
            OrbVisualRect(other, out _).Intersects(entry.Label.GetGlobalRect())));
        _report.Check($"orbs_{phase}_values_unobscured", aboveArt || clearOfArt,
            $"labels={labels.Count} aboveArt={aboveArt} clearOfArt={clearOfArt}");
        _report.Check($"orbs_{phase}_values_readable", labels.All(entry =>
                entry.Label.GetThemeFontSize("font_size") * entry.Label.GetGlobalTransform().Scale.Abs().Y >= OrbValueFontFloor),
            $"sizes={string.Join(",", labels.Select(entry => (entry.Label.GetThemeFontSize("font_size")
                * entry.Label.GetGlobalTransform().Scale.Abs().Y).ToString("F1")))} minimum={OrbValueFontFloor:F0}");
        _report.Check($"orbs_{phase}_values_in_frame", labels.All(entry => canvas.Encloses(entry.Label.GetGlobalRect())),
            $"labels={string.Join("; ", labels.Select(entry => entry.Label.GetGlobalRect().ToString()))}");
        bool separate = !labels.Any(a => labels.Any(b => a.Orb != b.Orb && a.Label.GetGlobalRect().Intersects(b.Label.GetGlobalRect())));
        _report.Check($"orbs_{phase}_values_separate", separate, "amount labels of different orbs do not intersect");
    }

    private static List<(NOrb Orb, Label Label)> LabelPairs(IEnumerable<NOrb> orbs)
    {
        var labels = new List<(NOrb, Label)>();
        foreach (NOrb orb in orbs)
        {
            Control? container = orb.GetNodeOrNull<Control>("%LabelContainer") ?? orb.GetNodeOrNull<Control>("LabelContainer");
            if (container is null) continue;
            foreach (Label label in FindAll<Label>(container).Where(label => label.IsVisibleInTree()))
                labels.Add((orb, label));
        }
        return labels;
    }

    // ---- no snapping: drawn-frame continuity of the arc ----------------------------------------------------------------

    private OrbMotionProbe? _orbMotionProbe;

    partial void BeginOrbMotionProbe(string step)
    {
        _orbMotionProbe?.Stop();
        _orbMotionProbe = new OrbMotionProbe(_tree, step);
        _orbMotionProbe.Start();
    }

    partial void EndOrbMotionProbe(string step)
    {
        if (_orbMotionProbe is null) return;
        _orbMotionProbe.Stop();
        OrbMotionProbe probe = _orbMotionProbe;
        _orbMotionProbe = null;
        _report.Check($"orbs_{step}_no_snap", probe.Ok, probe.Detail);
    }

    /// <summary>
    /// Samples every orb's drawn position once per drawn frame, so a re-layout that teleports the arc (writing the target
    /// instead of retargeting the native 0.45 s tween) shows up as a single-frame jump. A step is a snap when it is larger
    /// than the per-frame allowance at that frame's own duration, so a slow frame is not mistaken for one.
    /// </summary>
    private sealed class OrbMotionProbe(SceneTree tree, string step)
    {
        private readonly Dictionary<ulong, Vector2> _last = new();
        private NOrbManager? _manager;
        private ulong _previousMicroseconds;
        private float _worstRatio;
        private float _worstStep;
        private float _worstAllowed;
        private string _worstOrb = "none";
        private int _frames;
        private bool _running;
        public bool Ok { get; private set; } = true;
        public string Detail { get; private set; } = "no frames sampled";

        public void Start()
        {
            _previousMicroseconds = Time.GetTicksUsec();
            _running = true;
            Sample(); // the pre-change frame, so a change applied before the next draw is still measured
            RenderingServer.FramePostDraw += Sample;
        }

        public void Stop()
        {
            if (!_running) return;
            RenderingServer.FramePostDraw -= Sample;
            _running = false;
            Detail = Ok
                ? $"{_frames} drawn frames, largest step {_worstStep:F1} u (allowance {_worstAllowed:F1} u, orb {_worstOrb})"
                : $"snapped: {_worstOrb} moved {_worstStep:F1} u in one drawn frame (allowance {_worstAllowed:F1} u) " +
                  $"during {step}";
        }

        private void Sample()
        {
            if (!_running) return;
            ulong now = Time.GetTicksUsec();
            float seconds = Mathf.Max(1f / 240f, (now - _previousMicroseconds) / 1_000_000f);
            _previousMicroseconds = now;
            float allowance = Mathf.Max(OrbMotionStepLimit, OrbMotionSpeedLimit * seconds);
            _frames++;
            if (_manager is null || !GodotObject.IsInstanceValid(_manager) || _manager.IsQueuedForDeletion())
                _manager = FindAll<NOrbManager>(tree.Root)
                    .FirstOrDefault(candidate => !candidate.IsQueuedForDeletion() && GodotObject.IsInstanceValid(candidate));
            if (_manager is null) return;
            List<NOrb> orbs = AccessTools.FieldRefAccess<NOrbManager, List<NOrb>>("_orbs")(_manager);
            var current = new Dictionary<ulong, Vector2>();
            foreach (NOrb orb in orbs)
            {
                if (!GodotObject.IsInstanceValid(orb) || orb.IsQueuedForDeletion()) continue;
                ulong id = orb.GetInstanceId();
                current[id] = orb.GlobalPosition;
                if (!_last.TryGetValue(id, out Vector2 previous)) continue; // a new orb has no previous frame
                float moved = previous.DistanceTo(orb.GlobalPosition);
                if (moved <= allowance) continue;
                Ok = false;
                float ratio = moved / allowance;
                if (ratio <= _worstRatio) continue;
                _worstRatio = ratio;
                _worstStep = moved;
                _worstAllowed = allowance;
                _worstOrb = $"{orb.Name}({orb.Model?.GetType().Name})";
            }
            _last.Clear();
            foreach ((ulong id, Vector2 position) in current) _last[id] = position;
        }
    }
}
