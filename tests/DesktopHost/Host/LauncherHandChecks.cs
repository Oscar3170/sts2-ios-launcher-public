using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The launcher host's answers for the hand scenarios that run on both hosts: measured against
/// <see cref="PortraitCombatMath.ComputeCombatBands"/> and the 1080-wide portrait canvas instead of TouchSpireUI's safe
/// frame. They assert behaviour and invariants (rows fill the upper row first, the rail stays put, touch targets are 44
/// points) rather than the exact numbers a host happens to use today. The declarations live in HarnessCore.cs; the
/// TouchSpire host implements them in HostLayoutChecks.cs.
/// </summary>
internal sealed partial class TestDriver
{
    private Rect2[]? _handRowsRail;
    private Transform2D? _handRowsStage;
    private Rect2? _combatPilesEndTurn;
    private CombatRailProbe? _combatRailProbe;
    private HandDrawProbe? _handDrawProbe;
    private PassCostProbe? _turnCostProbe;
    private SelectionMotionProbe? _selectionProbe;

    private float LauncherCanvasHeight() => Math.Max(GetViewportSize().Y, SafeAreaCanvas.MinPortraitHeight);

    private Rect2 LauncherCanvas() =>
        new(Vector2.Zero, new Vector2(SafeAreaCanvas.PortraitWidth, LauncherCanvasHeight()));

    private PortraitCombatBands LauncherBands() =>
        PortraitCombatMath.ComputeCombatBands(LauncherCanvasHeight(),
            PortraitLayout.ComputeCapHeight(_tree.Root) + PortraitBands.RunContentTop);

    // The container is the positioned node; the counter child carries the icon's size.
    private NEnergyCounter? LauncherEnergyCounter(NCombatRoom room) =>
        FindAll<NEnergyCounter>(room.Ui.EnergyCounterContainer).FirstOrDefault();

    private Rect2 LauncherEnergyRect(NCombatRoom room) =>
        (LauncherEnergyCounter(room) ?? (Control)room.Ui.EnergyCounterContainer).GetGlobalRect();

    // A preview's visual card, including its scale and rotation, or its hit box when it has no card node yet.
    private static Rect2 LauncherCardRect(NCardHolder holder) =>
        holder.CardNode is { } card
            ? card.GetGlobalTransform() * new Rect2(-NCard.defaultSize / 2f, NCard.defaultSize)
            : holder.Hitbox.GetGlobalRect();

    // The Armaments upgrade preview's before/after cards, when that preview is the one on screen.
    private static Rect2[] LauncherUpgradePairRects(NUpgradePreview preview)
    {
        if (!preview.IsVisibleInTree()) return Array.Empty<Rect2>();
        Rect2[] rects = new[] { "Before", "After" }
            .Select(side => preview.GetNodeOrNull<Control>("%" + side) ?? preview.GetNodeOrNull<Control>(side))
            .Select(side => side?.GetChildren().OfType<NPreviewCardHolder>().FirstOrDefault())
            .Where(holder => holder is not null)
            .Select(holder => LauncherCardRect(holder!))
            .ToArray();
        return rects;
    }

    // ---- hand-rows ----------------------------------------------------------------------------------------------------

    // The permanent two-row hand holds ten cards. The upper row fills first and keeps at least three cards before the
    // lower row takes any, so the first three draws never change rows.
    private partial int HandRowsCardLimit() => 10;

    private partial int HandRowsUpperCount(int count) => PortraitCombatMath.SplitHandRows(count).BackRowCount;

    // Both rows keep the shape's full card size and never leave the 12-unit margin; cards overlap when crowded.
    private partial float HandRowsRowTolerance() => 8f;

    // The pivot row line: rotation moves a card's hit-box AABB but not the holder origin.
    private partial float HandRowsRowY(NHandCardHolder holder) => holder.GlobalPosition.Y;

    private partial float HandRowsLowerRowTolerance() => 8f;

    private partial void CheckHandRowsLayout(int count, IReadOnlyList<NHandCardHolder> cards, NCombatRoom room)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        PortraitCombatBands bands = LauncherBands();
        Rect2 canvasMargin = LauncherCanvas().Grow(-PortraitCombatMath.HandEdgeMargin);
        float upperY = HandRowsRowY(cards[0]);
        int upperCount = HandRowsUpperCount(count);
        // The planned row line, not the rotated hit-box AABB: the fan moves the AABB centre by a few units.
        int observedUpper = cards.Count(card => HandRowsRowY(card) < upperY + 100f);
        _report.Check($"hand_{count}_upper_row_fills_first", observedUpper == upperCount,
            $"upper row cards={observedUpper}, expected={upperCount} of {count}");
        // Both hand rows sit between the rail and the canvas bottom. A rotated card may lift its AABB up to the fan
        // bound (6 units) above the row's flat top; that lift is allowed, the row line is not.
        float expectedScale = PortraitCombatMath.HandCardScaleFor(bands, count);
        bool bandOk = true;
        var cardDetails = new List<string>();
        foreach (NHandCardHolder holder in cards)
        {
            Rect2 card = LauncherCardRect(holder);
            float rowLine = HandRowsRowY(holder);
            float badge = PortraitCombatMath.CostBadgeOverhang * expectedScale;
            Rect2 withBadge = new(card.Position - new Vector2(badge, 0f), card.Size + new Vector2(badge, 0f));
            bandOk &= canvasMargin.Encloses(withBadge)
                && card.Position.Y >= bands.HandTopY - 6f
                && Math.Abs(card.GetCenter().Y - rowLine) <= 6f;
            cardDetails.Add($"{card} row={rowLine:F1}");
        }
        _report.Check($"hand_{count}_hand_band", bandOk,
            $"hand top={bands.HandTopY:F0}; cards={string.Join("; ", cardDetails)}");

        Rect2[] rail =
        [
            room.Ui.EndTurnButton.GetGlobalRect(),
            LauncherEnergyRect(room),
            room.Ui.DrawPile.GetGlobalRect(),
            room.Ui.DiscardPile.GetGlobalRect(),
        ];
        Transform2D stage = room.CreatureNodes.First(c => c.Entity.IsPlayer).GetGlobalTransform();
        if (count == 1)
        {
            _handRowsRail = rail;
            _handRowsStage = stage;
            return;
        }

        _report.Check($"hand_{count}_rail_stable", _handRowsRail is { } before
            && rail.Zip(before).All(pair => pair.First.Position.DistanceTo(pair.Second.Position) < 2f),
            $"rail now={string.Join("; ", rail)} before={string.Join("; ", _handRowsRail ?? Array.Empty<Rect2>())}");
        _report.Check($"hand_{count}_stage_stable", _handRowsStage is { } stageBefore && stage.IsEqualApprox(stageBefore),
            "the player's stage transform stays fixed while the hand grows");
    }

    // ---- Flat Hand mode (launcher only) -------------------------------------------------------------------------------

    private partial bool FlatHandSupported() => SafeAreaCanvas.IsPortrait;

    private partial void SetFlatHandMode(bool flat) => PortraitHandLayout.FlatHand = flat;

    private partial void ResetFlatHandMode() => PortraitHandLayout.FlatHand = false;

    /// <summary>
    /// At every hand size: verify the fan, record the room, flip to Flat Hand, wait for the per-frame pass to relayout,
    /// verify flat rows and that only the hand moved, capture the 5/10-card views, then restore fan.
    /// </summary>
    private partial async Task CheckHandRowsModesAsync(int count, IReadOnlyList<NHandCardHolder> cards, NCombatRoom room)
    {
        if (!SafeAreaCanvas.IsPortrait || cards.Count == 0) return;
        PortraitCombatBands bands = LauncherBands();
        Rect2[] fanRail = LauncherHandRailRects(room);
        Transform2D fanStage = room.CreatureNodes.First(c => c.Entity.IsPlayer).GetGlobalTransform();
        CheckHandModeGeometry(count, cards, bands, flat: false, "fan");
        if (count is 5 or 10) await CaptureAsync($"hand_rows_{count}_fan");

        SetFlatHandMode(true);
        try
        {
            await WaitForHandModeAsync(count, cards, bands, flat: true, $"hand_rows_{count}_flat");
            await WaitSecondsAsync(0.35);
            CheckHandModeGeometry(count, cards, bands, flat: true, "flat");
            AssertHandLayoutStable(room, bands, fanRail, fanStage, count);
            if (count is 5 or 10) await CaptureAsync($"hand_rows_{count}_flat");
        }
        finally
        {
            SetFlatHandMode(false);
        }

        await WaitForHandModeAsync(count, cards, bands, flat: false, $"hand_rows_{count}_fan_return");
    }

    private Rect2[] LauncherHandRailRects(NCombatRoom room) =>
    [
        room.Ui.EndTurnButton.GetGlobalRect(),
        LauncherEnergyRect(room),
        room.Ui.DrawPile.GetGlobalRect(),
        room.Ui.DiscardPile.GetGlobalRect(),
        room.Ui.ExhaustPile.GetGlobalRect(),
    ];

    /// <summary>The per-frame pass normally retargets the hand within one or two frames of a mode flip.</summary>
    private async Task<long> WaitForHandModeAsync(int count, IReadOnlyList<NHandCardHolder> cards,
        PortraitCombatBands bands, bool flat, string step)
    {
        long start = (long)Engine.GetProcessFrames();
        while (true)
        {
            bool matches = true;
            string mismatch = "";
            for (int i = 0; i < cards.Count; i++)
            {
                PortraitCardSlot slot = PortraitCombatMath.ComputeHandCardSlot(count, i, bands, flat);
                NHandCardHolder holder = cards[i];
                Vector2 target = new(slot.LocalX, slot.LocalY);
                if (holder.TargetPosition.DistanceTo(target) > 0.5f)
                {
                    matches = false;
                    mismatch = $"card {i} position {holder.TargetPosition} != {target}";
                    break;
                }
                if (Math.Abs(holder.TargetAngle - slot.AngleDegrees) > 0.05f)
                {
                    matches = false;
                    mismatch = $"card {i} angle {holder.TargetAngle:F2} != {slot.AngleDegrees:F2}";
                    break;
                }
                if (Math.Abs(holder.Scale.X - slot.Scale) > 0.002f)
                {
                    matches = false;
                    mismatch = $"card {i} scale {holder.Scale.X:F4} != {slot.Scale:F4}";
                    break;
                }
            }

            long frames = (long)Engine.GetProcessFrames() - start;
            if (matches)
            {
                _report.Check(step + "_settled", true, $"{frames} frames");
                return frames;
            }
            if (frames > 10)
            {
                _report.Check(step + "_settled", false, $"after {frames} frames: {mismatch}");
                return frames;
            }
            await WaitFramesAsync(1);
        }
    }

    private void CheckHandModeGeometry(int count, IReadOnlyList<NHandCardHolder> cards, PortraitCombatBands bands,
        bool flat, string mode)
    {
        int upper = PortraitCombatMath.SplitHandRows(count).BackRowCount;
        Rect2 margin = LauncherCanvas().Grow(-PortraitCombatMath.HandEdgeMargin);
        float expected = PortraitCombatMath.HandCardScaleFor(bands, count);
        bool scaleOk = true, rotationOk = true, marginOk = true, rowLineOk = true;
        float maxRowOffset = 0f;
        int observedUpper = cards.Count(card => HandRowsRowY(card) < HandRowsRowY(cards[0]) + 100f);
        for (int i = 0; i < cards.Count; i++)
        {
            NHandCardHolder holder = cards[i];
            PortraitCardSlot slot = PortraitCombatMath.ComputeHandCardSlot(count, i, bands, flat);
            scaleOk &= Math.Abs(holder.Scale.X - expected) < 0.02f;
            float angle = flat ? 0f : slot.AngleDegrees;
            rotationOk &= Math.Abs(holder.RotationDegrees - angle) < 0.6f;
            Rect2 card = LauncherCardRect(holder);
            float badge = PortraitCombatMath.CostBadgeOverhang * expected;
            Rect2 withBadge = new(card.Position - new Vector2(badge, 0f), card.Size + new Vector2(badge, 0f));
            marginOk &= margin.Encloses(withBadge) && card.Position.Y >= bands.HandTopY - 6f;
            // A rotated card's AABB centre lifts up to HandRail's 6-unit fan bound from its pivot row line.
            float rowOffset = Math.Abs(card.GetCenter().Y - HandRowsRowY(holder));
            maxRowOffset = Math.Max(maxRowOffset, rowOffset);
            rowLineOk &= rowOffset <= 6f;
        }

        _report.Check($"hand_rows_{count}_{mode}_scale", scaleOk,
            $"expected {expected:F4} for every count and both rows");
        _report.Check($"hand_rows_{count}_{mode}_rotation", rotationOk,
            flat ? "flat rows: every card upright" : "fan: native row angles");
        _report.Check($"hand_rows_{count}_{mode}_margin", marginOk, $"margin={margin}");
        _report.Check($"hand_rows_{count}_{mode}_row_line", rowLineOk,
            $"max |card centre Y - row line|={maxRowOffset:F2} (bound 6)");
        _report.Check($"hand_rows_{count}_{mode}_upper_row", observedUpper == upper,
            $"upper={observedUpper} expected={upper}");
    }

    private void AssertHandLayoutStable(NCombatRoom room, PortraitCombatBands before, Rect2[] railBefore,
        Transform2D stageBefore, int count)
    {
        PortraitCombatBands now = LauncherBands();
        Rect2[] rail = LauncherHandRailRects(room);
        bool bandsStable = Math.Abs(now.HandTopY - before.HandTopY) <= 0.5f
            && Math.Abs(now.RailTopY - before.RailTopY) <= 0.5f
            && Math.Abs(now.FrontFeetY - before.FrontFeetY) <= 0.5f;
        bool railStable = rail.Zip(railBefore).All(pair =>
            pair.First.Position.DistanceTo(pair.Second.Position) < 2f);
        Transform2D stage = room.CreatureNodes.First(c => c.Entity.IsPlayer).GetGlobalTransform();
        bool stageStable = stage.IsEqualApprox(stageBefore);
        _report.Check($"hand_rows_{count}_flat_layout_stable", bandsStable && railStable && stageStable,
            $"hand top {before.HandTopY:F1}->{now.HandTopY:F1}; rail={string.Join("; ", rail)} " +
            $"before={string.Join("; ", railBefore)}; stage stable={stageStable}");
    }

    // ---- regent-stars -------------------------------------------------------------------------------------------------

    // The drawn glyphs of a native label, not its control box: the effective theme font size (theme overrides included),
    // the parsed text, the label's alignment and its global transform, with the outline growing the rect on every side.
    private static Rect2 LauncherLabelGlyphRect(Control label)
    {
        if (label is RichTextLabel rich)
        {
            string text = rich.GetParsedText();
            Font? font = rich.GetThemeFont("normal_font");
            if (font is null || string.IsNullOrWhiteSpace(text)) return label.GetGlobalRect();
            int fontSize = rich.GetThemeFontSize("normal_font_size");
            int outline = rich.GetThemeConstant("outline_size");
            // NStarCounter centers its glyph with the [center] BBCode tag; the property itself is often still Left.
            HorizontalAlignment horizontal = rich.Text.Contains("[center]", StringComparison.OrdinalIgnoreCase)
                ? HorizontalAlignment.Center
                : rich.HorizontalAlignment;
            return LauncherGlyphRect(label, font, text, fontSize, outline, horizontal, rich.VerticalAlignment);
        }

        if (label is Label plain)
        {
            string text = plain.Text;
            Font? font = plain.GetThemeFont("font");
            if (font is null || string.IsNullOrWhiteSpace(text)) return label.GetGlobalRect();
            int fontSize = plain.GetThemeFontSize("font_size");
            int outline = plain.GetThemeConstant("outline_size");
            return LauncherGlyphRect(label, font, text, fontSize, outline, plain.HorizontalAlignment, plain.VerticalAlignment);
        }

        return label.GetGlobalRect();
    }

    private static Rect2 LauncherGlyphRect(Control label, Font font, string text, int fontSize, int outline,
        HorizontalAlignment horizontal, VerticalAlignment vertical)
    {
        // Ink, not the line box: the advance width and the digits' cap height (font ascent), not the 1.28 line height.
        Vector2 advance = font.GetStringSize(text, horizontal, -1f, fontSize);
        float inkHeight = font.GetAscent(fontSize);
        if (inkHeight <= 0f) inkHeight = advance.Y;
        Vector2 textSize = new(advance.X, inkHeight);
        Vector2 box = label.Size;
        float x = horizontal switch
        {
            HorizontalAlignment.Center => (box.X - textSize.X) * 0.5f,
            HorizontalAlignment.Right => box.X - textSize.X,
            _ => 0f,
        };
        float y = vertical switch
        {
            VerticalAlignment.Center => (box.Y - textSize.Y) * 0.5f,
            VerticalAlignment.Bottom => box.Y - textSize.Y,
            _ => 0f,
        };
        float pad = Math.Max(0, outline);
        Rect2 local = new(new Vector2(x - pad, y - pad), textSize + new Vector2(pad * 2f, pad * 2f));
        return label.GetGlobalTransform() * local;
    }

    // Fraction of an ink rect covered by the union of the occluders, sampled on a 1-unit grid so overlaps don't double count.
    private static float LauncherCoveredFraction(Rect2 ink, IReadOnlyList<Rect2> occluders)
    {
        if (ink.Size.X <= 0f || ink.Size.Y <= 0f || occluders.Count == 0) return 0f;
        int total = 0, covered = 0;
        for (float y = ink.Position.Y; y < ink.End.Y; y += 1f)
        for (float x = ink.Position.X; x < ink.End.X; x += 1f)
        {
            total++;
            Vector2 point = new(x + 0.5f, y + 0.5f);
            foreach (Rect2 occluder in occluders)
            {
                if (!occluder.HasPoint(point)) continue;
                covered++;
                break;
            }
        }
        return total == 0 ? 0f : covered / (float)total;
    }

    private Rect2? _regentStarBadge;
    private Rect2? _regentStarOrb;

    // Separation between two rects: 0 when they intersect, otherwise the shortest gap along the separated axes.
    private static float LauncherRectGap(Rect2 a, Rect2 b)
    {
        float dx = Mathf.Max(0f, Mathf.Max(a.Position.X - b.End.X, b.Position.X - a.End.X));
        float dy = Mathf.Max(0f, Mathf.Max(a.Position.Y - b.End.Y, b.Position.Y - a.End.Y));
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    private static Rect2I LauncherPixelRect(Rect2 rect, float scale, int width, int height)
    {
        int left = Mathf.Clamp(Mathf.FloorToInt(rect.Position.X * scale), 0, width);
        int top = Mathf.Clamp(Mathf.FloorToInt(rect.Position.Y * scale), 0, height);
        int right = Mathf.Clamp(Mathf.CeilToInt(rect.End.X * scale), 0, width);
        int bottom = Mathf.Clamp(Mathf.CeilToInt(rect.End.Y * scale), 0, height);
        return new Rect2I(left, top, Mathf.Max(0, right - left), Mathf.Max(0, bottom - top));
    }

    // The native count labels draw cream glyphs; the star body behind the "3/3" is the only blue in that ink box.
    private static bool LauncherCreamPixel(Color color) =>
        color.R > 0.88f && color.G > 0.84f && color.B > 0.75f;

    private static bool LauncherStarBluePixel(Color color) =>
        color.B > 0.58f && color.G > 0.45f && color.R < 0.62f && color.B - color.R > 0.22f;

    private static readonly Dictionary<ulong, Rect2> LauncherTextureInkCache = new();

    // The non-transparent part of a texture in its own pixels. Cached: the star textures never change.
    private static Rect2 LauncherTextureInk(Texture2D texture)
    {
        ulong id = texture.GetInstanceId();
        if (LauncherTextureInkCache.TryGetValue(id, out Rect2 cached)) return cached;
        Vector2 size = texture.GetSize();
        Rect2 ink = new(Vector2.Zero, size);
        if (texture.GetImage() is { } image && !image.IsEmpty())
        {
            int width = image.GetWidth(), height = image.GetHeight();
            int step = Mathf.Max(1, Mathf.Min(width, height) / 128);
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (int y = 0; y < height; y += step)
            for (int x = 0; x < width; x += step)
            {
                if (image.GetPixel(x, y).A <= 0.15f) continue;
                minX = Mathf.Min(minX, x);
                minY = Mathf.Min(minY, y);
                maxX = Mathf.Max(maxX, x);
                maxY = Mathf.Max(maxY, y);
            }
            if (maxX >= minX)
            {
                float pixelWidth = maxX - minX + step, pixelHeight = maxY - minY + step;
                ink = new Rect2(
                    new Vector2(minX / (float)width * size.X, minY / (float)height * size.Y),
                    new Vector2(pixelWidth / width * size.X, pixelHeight / height * size.Y));
            }
            image.Dispose();
        }
        LauncherTextureInkCache[id] = ink;
        return ink;
    }

    // Where a texture's ink lands on screen for the TextureRect's actual stretch mode and transform.
    private static Rect2 LauncherDrawnInk(TextureRect rect)
    {
        if (rect.Texture is null) return rect.GetGlobalRect();
        Vector2 texture = rect.Texture.GetSize();
        if (texture.X <= 0f || texture.Y <= 0f) return rect.GetGlobalRect();
        Rect2 ink = LauncherTextureInk(rect.Texture);
        Vector2 size = rect.Size;
        Vector2 origin, scale;
        if (rect.StretchMode is TextureRect.StretchModeEnum.KeepAspectCentered
            or TextureRect.StretchModeEnum.KeepAspectCovered)
        {
            float fit = rect.StretchMode == TextureRect.StretchModeEnum.KeepAspectCovered
                ? Mathf.Max(size.X / texture.X, size.Y / texture.Y)
                : Mathf.Min(size.X / texture.X, size.Y / texture.Y);
            Vector2 drawn = texture * fit;
            origin = (size - drawn) * 0.5f + ink.Position * fit;
            scale = Vector2.One * fit;
        }
        else
        {
            origin = new Vector2(ink.Position.X / texture.X * size.X, ink.Position.Y / texture.Y * size.Y);
            scale = new Vector2(size.X / texture.X, size.Y / texture.Y);
        }
        return rect.GetGlobalTransform() * new Rect2(origin, ink.Size * scale);
    }

    // The solid star art (base Icon texture). The rotating sparkle layers are decorative and deliberately not unioned:
    // a rotating AABB would overstate the badge's reach by the texture diagonal.
    private static Rect2 LauncherStarInkRect(NStarCounter counter) =>
        counter.GetNodeOrNull<TextureRect>("Icon") is { Texture: not null } icon
            ? LauncherDrawnInk(icon)
            : counter.GetGlobalRect();

    // The visible ink of a pile's count badge: its red count art plus the cream digit, without the CountContainer's
    // transparent padding. Falls back to the control rect when those nodes are absent.
    private static Rect2 LauncherCountBadgeInk(Control? badge)
    {
        if (badge is null) return default;
        Rect2? ink = null;
        if (badge.GetNodeOrNull<TextureRect>("Background") is { Texture: not null } art)
            ink = LauncherDrawnInk(art);
        if (badge.GetNodeOrNull<Label>("Count") is { } count && !string.IsNullOrWhiteSpace(count.Text))
        {
            Rect2 glyph = LauncherLabelGlyphRect(count);
            ink = ink is { } existing ? existing.Merge(glyph) : glyph;
        }
        return ink is { } result && result.Size.X > 0f && result.Size.Y > 0f
            ? result
            : badge.GetGlobalRect();
    }

    private partial void CheckRegentStarRail(string name, NCombatRoom room, NStarCounter counter, int initial)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        RichTextLabel starLabel = counter.GetNode<RichTextLabel>("%CountLabel");
        Rect2 starBadge = counter.GetGlobalRect();
        Rect2 starInk = LauncherLabelGlyphRect(starLabel);
        NEnergyCounter? energyCounter = LauncherEnergyCounter(room);
        Control? energy = energyCounter;
        Label? energyLabel = energyCounter?.GetNodeOrNull<Label>("Label")
            ?? energyCounter?.GetNodeOrNull<Label>("%Label");
        Rect2 energyInk = energyLabel is null ? default : LauncherLabelGlyphRect(energyLabel);
        Rect2 orb = energy?.GetGlobalRect() ?? default;
        _regentStarBadge = starBadge;
        _regentStarOrb = orb;

        // 1. Size and aspect: the native 128-unit scene at its 0.8 scale under the energy counter's scale. The launcher
        //    canvas is 1080 wide, so TouchSpireUI's 184.32-unit badge (1680 canvas) is the same fraction of the width
        //    here: 184.32 * 1080/1680 = 118.5, and the scene/energy scales give 128 * 0.8 * 1.12 = 114.688 (within 5%).
        Vector2 energyScale = energy is null ? Vector2.One : energy.GetGlobalTransform().Scale.Abs();
        Vector2 expectedSize = new Vector2(128f, 128f) * 0.8f * energyScale;
        float larger = Mathf.Max(starBadge.Size.X, starBadge.Size.Y);
        TextureRect? icon = counter.GetNodeOrNull<TextureRect>("Icon");
        bool iconFills = icon is not null && icon.Size.DistanceTo(counter.Size) <= 1f;
        bool badgeSquare = Mathf.Abs(starBadge.Size.X - starBadge.Size.Y) <= 0.05f * Mathf.Max(1f, larger);
        bool badgeNative = iconFills
            && Mathf.Abs(counter.Size.X - 128f) <= 1f && Mathf.Abs(counter.Size.Y - 128f) <= 1f
            && Mathf.Abs(starBadge.Size.X - expectedSize.X) <= 0.05f * expectedSize.X
            && Mathf.Abs(starBadge.Size.Y - expectedSize.Y) <= 0.05f * expectedSize.Y;
        _report.Check("regent_star_badge_square_and_size", badgeSquare && badgeNative,
            $"badge={starBadge} local={counter.Size} icon={icon?.Size} " +
            $"scale={counter.GetGlobalTransform().Scale.Abs()} " +
            $"energy scale={energyScale} expected={expectedSize} " +
            $"TSUI-equivalent={starBadge.Size * (1680f / SafeAreaCanvas.PortraitWidth)}");

        // 2. TouchSpireUI's lower-left corner badge: orb centre + orb size * (-0.4, +0.3).
        Vector2 expectedOffset = orb.Size * new Vector2(-0.4f, 0.3f);
        Vector2 actualOffset = starBadge.GetCenter() - orb.GetCenter();
        float offsetError = (actualOffset - expectedOffset).Length();
        _report.Check("regent_star_offset_from_energy_orb", offsetError <= 6f,
            $"badge centre={starBadge.GetCenter()} orb={orb} expected offset={expectedOffset} " +
            $"actual offset={actualOffset} error={offsetError:F2}");

        // 3. Draw order, not rects: the star must be the energy counter's last child, after the orb art and the "3/3"
        //    label, so the orb's spikes and front VFX cannot paint over it (TouchSpireUI's native order).
        Control? starParent = counter.GetParent() as Control;
        string[] artNames = { "EnergyVfxBack", "Layers", "EnergyVfxFront" };
        Control[] orbArt = artNames
            .SelectMany(art => new[] { starParent?.GetNodeOrNull<Control>("%" + art), starParent?.GetNodeOrNull<Control>(art) })
            .OfType<Control>()
            .Where(art => GodotObject.IsInstanceValid(art))
            .Distinct().ToArray();
        int lastArtIndex = orbArt.Length == 0 ? -1 : orbArt.Max(art => art.GetIndex());
        int labelIndex = energyLabel?.GetIndex() ?? -1;
        bool starAfterArt = starParent is not null && starParent == energy && counter.GetIndex() > lastArtIndex;
        bool starAfterLabel = starParent is not null && starParent == energy && counter.GetIndex() > labelIndex;
        bool starLast = starParent is not null && counter.GetIndex() == starParent.GetChildCount() - 1;
        _report.Check("regent_star_draws_over_orb_art",
            orbArt.Length > 0 && starAfterArt && orbArt.All(art => counter.ZIndex >= art.ZIndex),
            $"star index={counter.GetIndex()} z={counter.ZIndex} parent={starParent?.Name}; " +
            $"orb art={string.Join("; ", orbArt.Select(art => $"{art.Name}@{art.GetIndex()}z{art.ZIndex}"))} " +
            $"label index={labelIndex}");
        // The step name is kept from the earlier ordering; TouchSpireUI draws the star last (in front), not below.
        _report.Check("regent_star_art_below_energy_label", starAfterLabel && starLast,
            $"star index={counter.GetIndex()} of {starParent?.GetChildCount()} in {starParent?.Name}; " +
            $"energy label index={labelIndex}: the star draws last, as in TouchSpireUI");

        // 4. The digit is centred in the badge in TouchSpireUI; the native label keeps that when the badge is not moved
        //    relative to its own rect.
        float inkError = starBadge.GetCenter().DistanceTo(starInk.GetCenter());
        _report.Check("regent_star_number_centred", starInk.Size.X > 0f && inkError <= 6f,
            $"digit ink={starInk} centre={starInk.GetCenter()} badge centre={starBadge.GetCenter()} error={inkError:F2} " +
            $"label rect={starLabel.GetGlobalRect()}");

        // 5. Ink to ink: the drawn star art must clear the draw pile's visible count badge by at least 5 canvas units
        //    (the accepted layout measures 7.54; the original overlap bug was gap 0, so 5 still catches it), and the
        //    pile button rect must not overlap the star art. The badge and star controls both carry transparent padding,
        //    so control rects would both under- and over-state what the eye sees; the count label's glyph ink is part of
        //    the badge art too.
        Rect2 starArt = LauncherStarInkRect(counter);
        Rect2 draw = room.Ui.DrawPile.GetGlobalRect();
        Control? drawBadge = room.Ui.DrawPile.GetNodeOrNull<Control>("CountContainer")
            ?? room.Ui.DrawPile.GetNodeOrNull<Control>("%CountContainer");
        Rect2 countBadge = drawBadge?.GetGlobalRect() ?? draw;
        Rect2 badgeInk = drawBadge is null ? draw : LauncherCountBadgeInk(drawBadge);
        float buttonGap = LauncherRectGap(starArt, draw);
        float controlGap = LauncherRectGap(starArt, countBadge);
        float inkGap = LauncherRectGap(starArt, badgeInk);
        _report.Check("regent_star_clear_of_draw_pile",
            !starArt.Intersects(draw) && inkGap >= 5f,
            $"star art={starArt} badge={starBadge} draw pile={draw} count badge={countBadge} badge ink={badgeInk} " +
            $"button gap={buttonGap:F2} control gap={controlGap:F2} ink gap={inkGap:F2} (minimum 5)");
        Rect2 turn = room.Ui.EndTurnButton.GetGlobalRect();
        float turnGap = LauncherRectGap(starArt, turn);
        _report.Check("regent_star_clear_of_end_turn", turnGap >= 10f,
            $"star art={starArt} badge={starBadge} end turn={turn} gap={turnGap:F2}");

        // 6. The digit must stay visible: the energy "3/3" is drawn before the star, so only later-drawn UI (hand,
        //    piles, End Turn, creature HUD) may cover it. The rendered "3/3" readability is measured on the frame.
        Rect2[] handCards = room.Ui.Hand.ActiveHolders.Where(h => h.IsVisibleInTree())
            .Select(h => h.Hitbox.GetGlobalRect()).ToArray();
        Rect2[] railRects =
        [
            turn,
            draw,
            room.Ui.DiscardPile.GetGlobalRect(),
            room.Ui.ExhaustPile.GetGlobalRect(),
        ];
        var creatureRects = new List<Rect2>();
        Rect2 playerBody = default, playerHealth = default;
        foreach (NCreature creature in room.CreatureNodes)
        {
            if (!GodotObject.IsInstanceValid(creature) || !creature.IsVisibleInTree()) continue;
            if (creature.Entity.IsPlayer) playerBody = creature.Hitbox.GetGlobalRect();
            Control? state = creature.GetNodeOrNull<Control>("%HealthBar");
            if (state is null) continue;
            Rect2? health = null;
            if (state.GetNodeOrNull<Control>("%HpBarHitbox") is { } hitbox && hitbox.IsVisibleInTree())
                health = hitbox.GetGlobalRect();
            else if (state.GetNodeOrNull<Control>("HealthBar/HpBarContainer/HpLabel") is { } hpLabel && hpLabel.IsVisibleInTree())
                health = hpLabel.GetGlobalRect();
            else if (state.IsVisibleInTree())
                health = state.GetGlobalRect();
            if (health is { } healthRect)
            {
                creatureRects.Add(healthRect);
                if (creature.Entity.IsPlayer) playerHealth = healthRect;
            }
            Control? nameplate = state.GetNodeOrNull<Control>("%NameplateLabel")
                ?? state.GetNodeOrNull<Control>("NameplateContainer/NameplateLabel")
                ?? state.GetNodeOrNull<Control>("%NameplateContainer")
                ?? state.GetNodeOrNull<Control>("NameplateContainer");
            if (nameplate is not null && nameplate.IsVisibleInTree()) creatureRects.Add(nameplate.GetGlobalRect());
            if (creature.IntentContainer is { } intents && GodotObject.IsInstanceValid(intents))
            {
                foreach (Control intent in intents.GetChildren().OfType<Control>())
                    if (intent.IsVisibleInTree()) creatureRects.Add(intent.GetGlobalRect());
            }
        }
        Rect2[] others = handCards.Concat(railRects).Concat(creatureRects).ToArray();
        float energyBoxOverlap = LauncherCoveredFraction(starInk, new[] { energyInk });
        float otherCoverage = LauncherCoveredFraction(starInk, others);
        Rect2[] touching = others.Where(r => starInk.Intersects(r)).Distinct().ToArray();
        _report.Check("regent_star_count_readable",
            starLabel.IsVisibleInTree() && otherCoverage <= 0.05f && (starAfterLabel || energyBoxOverlap <= 0f),
            $"star ink={starInk} energy ink={energyInk} energy box overlap={energyBoxOverlap:P0} " +
            $"(the star draws on top, so the box overlap is not occlusion) later coverage={otherCoverage:P0} " +
            $"touching={string.Join("; ", touching)} badge={starBadge} player body={playerBody} " +
            $"player health={playerHealth} label rect={starLabel.GetGlobalRect()}");
        _report.Check("regent_star_and_energy_counts_visible",
            starLabel.IsVisibleInTree() && !string.IsNullOrWhiteSpace(starLabel.GetParsedText())
            && energyLabel is not null && energyLabel.IsVisibleInTree() && !string.IsNullOrWhiteSpace(energyLabel.Text),
            $"star text='{starLabel.GetParsedText()}' visible={starLabel.IsVisibleInTree()}; " +
            $"energy text='{energyLabel?.Text}' visible={energyLabel?.IsVisibleInTree()}");
        _report.Check("end_turn_centred_for_character",
            Math.Abs(turn.GetCenter().X - SafeAreaCanvas.PortraitWidth * 0.5f) <= 2f,
            $"end turn={turn} canvas centre={SafeAreaCanvas.PortraitWidth * 0.5f}");
    }

    // 0 -> 3 -> 10: the badge and the orb must stay put and the (two-digit) count must stay centred.
    private partial async Task CheckRegentStarCountsAsync(RunState state, NCombatRoom room, NStarCounter counter)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        if (_regentStarBadge is not { } referenceBadge || _regentStarOrb is not { } referenceOrb)
        {
            _report.Fail("regent_star_counts_stable_0_3_10", "the standard star sample was not taken");
            return;
        }
        Player player = state.Players[0];
        RichTextLabel label = counter.GetNode<RichTextLabel>("%CountLabel");
        bool allStable = true, allCentred = true;
        var details = new List<string>();
        foreach ((int count, string name) in new[] { (0, "zero"), (3, "three"), (10, "ten") })
        {
            await PlayerCmd.SetStars(count, player);
            await WaitUntilAsync(() => label.Text.Contains($"]{count}["), $"star count {count}", ShortWait);
            await WaitSecondsAsync(0.3);
            Rect2 badge = counter.GetGlobalRect();
            Rect2 orb = LauncherEnergyRect(room);
            Rect2 ink = LauncherLabelGlyphRect(label);
            bool stable = badge.Position.DistanceTo(referenceBadge.Position) < 0.5f
                && badge.Size.DistanceTo(referenceBadge.Size) < 0.5f
                && orb.Position.DistanceTo(referenceOrb.Position) < 0.5f
                && orb.Size.DistanceTo(referenceOrb.Size) < 0.5f;
            float inkError = ink.Size.X > 0f ? ink.GetCenter().DistanceTo(badge.GetCenter()) : float.MaxValue;
            bool centred = inkError <= 6f;
            allStable &= stable;
            allCentred &= centred;
            details.Add($"count={count} badge={badge} orb={orb} ink={ink} ink error={inkError:F2}");
            _report.Check($"regent_star_count_stable_{name}", stable && centred,
                $"count={count} badge={badge} reference={referenceBadge} orb={orb} reference orb={referenceOrb} " +
                $"digit ink={ink} centre error={inkError:F2} text='{label.Text}'");
        }
        _report.Check("regent_star_counts_stable_0_3_10", allStable && allCentred, string.Join(" | ", details));
        await PlayerCmd.SetStars(3m, player);
        await WaitUntilAsync(() => label.Text.Contains("]3["), "restored star count", ShortWait);
    }

    // One rendered frame: measure how much of the energy count's cream ink the star body covers, then save the
    // draw-pile + star + energy region at 3x for review. The star draws after the "3/3", so this is the only way to
    // see the real overlap; ring/box tests would over-count the transparent padding around the glyphs.
    private partial async Task CheckRegentStarDetailAsync(NCombatRoom room, NStarCounter counter)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        NEnergyCounter? energyCounter = LauncherEnergyCounter(room);
        Label? energyLabel = energyCounter?.GetNodeOrNull<Label>("Label")
            ?? energyCounter?.GetNodeOrNull<Label>("%Label");
        RichTextLabel starLabel = counter.GetNode<RichTextLabel>("%CountLabel");
        if (energyCounter is null || energyLabel is null)
        {
            _report.Fail("regent_star_energy_count_legible", "the energy counter or its label is missing");
            return;
        }
        await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
        await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
        Image frame = _screenshots.ReadRenderedFrame("regent_star_detail");
        try
        {
            float scale = frame.GetWidth() / (float)SafeAreaCanvas.PortraitWidth;
            Rect2 energyInk = LauncherLabelGlyphRect(energyLabel);
            Rect2I energyPixels = LauncherPixelRect(energyInk, scale, frame.GetWidth(), frame.GetHeight());
            int creamVisible = 0, blueVisible = 0;
            for (int y = energyPixels.Position.Y; y < energyPixels.End.Y; y++)
            for (int x = energyPixels.Position.X; x < energyPixels.End.X; x++)
            {
                Color color = frame.GetPixel(x, y);
                if (LauncherCreamPixel(color)) creamVisible++;
                else if (LauncherStarBluePixel(color)) blueVisible++;
            }
            // The honest "3/3 covered by the star" measure: count the same cream glyph pixels with the star hidden for
            // two frames. A geometric box test would over-count the transparent padding and the orb's bright core.
            int creamUnobstructed = creamVisible;
            bool starWasVisible = counter.Visible;
            try
            {
                counter.Visible = false;
                await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
                await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
                using Image clearFrame = _screenshots.ReadRenderedFrame("regent_star_detail_clear");
                creamUnobstructed = 0;
                for (int y = energyPixels.Position.Y; y < energyPixels.End.Y; y++)
                for (int x = energyPixels.Position.X; x < energyPixels.End.X; x++)
                    if (LauncherCreamPixel(clearFrame.GetPixel(x, y))) creamUnobstructed++;
            }
            finally
            {
                counter.Visible = starWasVisible;
            }
            await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
            await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
            float covered = creamUnobstructed > 0
                ? 1f - creamVisible / (float)creamUnobstructed
                : 1f;
            _report.Check("regent_star_energy_count_legible",
                creamUnobstructed > 0 && creamVisible > 0 && covered <= 0.05f,
                $"energy ink={energyInk} pixels={energyPixels} visible cream={creamVisible} " +
                $"unobstructed cream={creamUnobstructed} star pixels={blueVisible} covered={covered:P1}");
            Rect2 starInk = LauncherLabelGlyphRect(starLabel);
            Rect2I digitPixels = LauncherPixelRect(starInk, scale, frame.GetWidth(), frame.GetHeight());
            int digitCream = 0;
            for (int y = digitPixels.Position.Y; y < digitPixels.End.Y; y++)
            for (int x = digitPixels.Position.X; x < digitPixels.End.X; x++)
                if (LauncherCreamPixel(frame.GetPixel(x, y))) digitCream++;
            _report.Check("regent_star_number_drawn", digitCream > 0,
                $"digit ink={starInk} pixels={digitPixels} cream={digitCream} text='{starLabel.GetParsedText()}'");

            Rect2 draw = room.Ui.DrawPile.GetGlobalRect();
            Control? drawBadge = room.Ui.DrawPile.GetNodeOrNull<Control>("CountContainer")
                ?? room.Ui.DrawPile.GetNodeOrNull<Control>("%CountContainer");
            Rect2 region = draw.Merge(drawBadge?.GetGlobalRect() ?? draw)
                .Merge(counter.GetGlobalRect())
                .Merge(energyCounter.GetGlobalRect())
                .Grow(12f);
            Rect2I crop = LauncherPixelRect(region, scale, frame.GetWidth(), frame.GetHeight());
            if (crop.Size.X < 8 || crop.Size.Y < 8)
            {
                _report.Fail("screenshot:regent_star_detail", $"empty crop {crop} from region {region}");
                return;
            }
            Image enlarged = frame.GetRegion(crop);
            try
            {
                enlarged.Resize(crop.Size.X * 3, crop.Size.Y * 3, Image.Interpolation.Bilinear);
                string path = _screenshots.SaveFrame("regent_star_detail", enlarged);
                _report.Screenshot("regent_star_detail", path);
            }
            finally
            {
                enlarged.Dispose();
            }
        }
        finally
        {
            frame.Dispose();
        }
    }

    // ---- combat-piles -------------------------------------------------------------------------------------------------

    private partial void BeginCombatRailProbe()
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        _combatRailProbe?.Cancel();
        _combatRailProbe = new CombatRailProbe(_tree, _screenshots, _report);
        _combatRailProbe.Begin();
    }

    private partial void MarkCombatRailProbeTurnStarted() => _combatRailProbe?.MarkTurnStarted();

    private partial async Task FinishCombatRailProbeAsync()
    {
        CombatRailProbe? probe = _combatRailProbe;
        _combatRailProbe = null;
        if (probe is not null) await probe.FinishAsync();
    }

    private partial void CancelCombatRailProbe()
    {
        _combatRailProbe?.Cancel();
        _combatRailProbe = null;
    }

    private partial void BeginHandDrawProbe(string step) => BeginHandDrawProbe(step, 0);

    private partial void BeginHandDrawProbe(string step, int extraDraw) => StartHandDrawProbe(step, extraDraw, false);

    private partial void BeginHandDrawProbe(string step, bool allCardsStill) => StartHandDrawProbe(step, 0, allCardsStill);

    private void StartHandDrawProbe(string step, int extraDraw, bool allCardsStill)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        _handDrawProbe?.Cancel();
        _handDrawProbe = new HandDrawProbe(_tree, _screenshots, _report, _options.OutputDirectory,
            () => CurrentHandSettledSignature());
        _handDrawProbe.Begin(step, extraDraw, allCardsStill);
    }

    private partial async Task FinishHandDrawProbeAsync(string step)
    {
        HandDrawProbe? probe = _handDrawProbe;
        _handDrawProbe = null;
        if (probe is not null) await probe.FinishAsync(step);
    }

    private partial void CancelHandDrawProbe()
    {
        _handDrawProbe?.Cancel();
        _handDrawProbe = null;
    }

    // ---- enemy turn cost ----------------------------------------------------------------------------------------------

    private partial void BeginEnemyTurnCostProbe()
    {
        _turnCostProbe?.Cancel();
        _turnCostProbe = new PassCostProbe(_tree, _report);
        _turnCostProbe.Begin();
    }

    private partial async Task FinishEnemyTurnCostProbeAsync(string step)
    {
        PassCostProbe? probe = _turnCostProbe;
        _turnCostProbe = null;
        if (probe is not null) await probe.FinishAsync(step);
    }

    /// <summary>
    /// Per-pass cost of the host's portrait passes over a window of frames: what <see cref="PortraitFrameTiming"/> recorded
    /// for each pass, averaged over the frames the pass ran in, with the worst frame. Reported as a passed step
    /// (<c>enemy_turn_pass_ms</c>), never gated: these are desktop numbers that rank the passes, and the device line
    /// (<c>[stats] perf</c>) is the real measurement.
    /// </summary>
    private sealed class PassCostProbe
    {
        private const double MaxLifetimeSeconds = 60.0;
        private readonly SceneTree _tree;
        private readonly TestReport _report;
        private readonly double[] _sum = new double[PortraitFrameTiming.PassCount];
        private readonly double[] _max = new double[PortraitFrameTiming.PassCount];
        private readonly int[] _runs = new int[PortraitFrameTiming.PassCount];
        private readonly double _startedMsec;
        private int _frames;

        public PassCostProbe(SceneTree tree, TestReport report)
        {
            _tree = tree;
            _report = report;
            _startedMsec = Time.GetTicksMsec();
        }

        public void Begin() => RenderingServer.FramePostDraw += OnFramePostDraw;

        public void Cancel()
        {
            RenderingServer.FramePostDraw -= OnFramePostDraw;
        }

        private void OnFramePostDraw()
        {
            if (Time.GetTicksMsec() - _startedMsec > MaxLifetimeSeconds * 1000.0)
            {
                Cancel();
                return;
            }
            ReadOnlySpan<double> ms = PortraitFrameTiming.LastFrameMs();
            _frames++;
            for (int pass = 0; pass < PortraitFrameTiming.PassCount; pass++)
            {
                double value = ms[pass];
                if (value <= 0.0) continue;
                _sum[pass] += value;
                if (value > _max[pass]) _max[pass] = value;
                _runs[pass]++;
            }
        }

        public async Task FinishAsync(string step)
        {
            // Two more drawn frames so the window ends on a complete frame, then report.
            for (int i = 0; i < 2; i++)
                await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
            Cancel();
            var text = new System.Text.StringBuilder();
            text.Append("frames=").Append(_frames);
            for (int pass = 0; pass < PortraitFrameTiming.PassCount; pass++)
            {
                int runs = _runs[pass];
                if (runs == 0) continue;
                text.Append(' ').Append(PortraitFrameTiming.Names[pass]).Append('=')
                    .Append((_sum[pass] / runs).ToString("F2"))
                    .Append('/').Append(_max[pass].ToString("F2"))
                    .Append('@').Append(runs);
            }
            _report.Pass($"{step}_pass_ms", text.ToString());
        }
    }

    private partial async Task CheckCombatPilesRailAsync(NCombatRoom room, Rect2[] before)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        await WaitSecondsAsync(0.6);
        var ui = room.Ui;
        PortraitCombatBands bands = LauncherBands();
        Rect2 canvas = LauncherCanvas();
        Rect2[] after =
        [
            ui.DrawPile.GetGlobalRect(), ui.EndTurnButton.GetGlobalRect(), ui.DiscardPile.GetGlobalRect(),
        ];
        Rect2 exhaust = ui.ExhaustPile.GetGlobalRect();
        Rect2 energy = LauncherEnergyRect(room);
        Rect2[] rail = [.. after, energy, exhaust];
        _report.Check("combat_piles_on_the_rail", rail.All(r => canvas.Encloses(r))
            && rail.All(r => r.GetCenter().Y >= bands.RailTopY && r.GetCenter().Y <= bands.HandTopY),
            $"rail y={bands.RailTopY:F0}..{bands.HandTopY:F0}; controls={string.Join("; ", rail)}");
        Control[] targets = [ui.EndTurnButton, ui.DrawPile, ui.DiscardPile, ui.ExhaustPile];
        // A pile's drawn rect is smaller than its finger target; PhoneHitArea is the grown child that takes the touch.
        // Use it when present and fall back to the drawn rect for controls that do not need (or have not got) one.
        Rect2 TargetRect(Control control) => PhoneHitArea.Of(control)?.GetGlobalRect() ?? control.GetGlobalRect();
        Rect2[] targetRects = targets.Select(TargetRect).ToArray();
        // A pile the game has hidden (normally the exhaust pile until a card is exhausted) has no touch target to
        // measure: its area is not visible, so PhoneHitArea.Of finds nothing and the drawn rect is not what takes a
        // finger. Only a control that is actually shown owes the 44-point target; hidden ones are reported and skipped.
        bool[] shown = targets.Select(control => control.IsVisibleInTree()).ToArray();
        string TargetSize(int index) => shown[index] ? targetRects[index].Size.ToString() : "hidden (skipped)";
        _report.Check("combat_piles_touch_targets",
            targetRects.Where((rect, index) => shown[index])
                .All(r => r.Size.X >= PortraitBands.MinTarget && r.Size.Y >= PortraitBands.MinTarget),
            $"targets: EndTurn={TargetSize(0)} draw={TargetSize(1)} " +
            $"discard={TargetSize(2)} exhaust={TargetSize(3)}; " +
            $"drawn: EndTurn={ui.EndTurnButton.GetGlobalRect().Size} draw={ui.DrawPile.GetGlobalRect().Size} " +
            $"discard={ui.DiscardPile.GetGlobalRect().Size} exhaust={ui.ExhaustPile.GetGlobalRect().Size}");
        Rect2 endTurn = after[1];
        _report.Check("end_turn_target_size",
            Math.Abs(endTurn.Size.X - 312.4f) <= 2f && Math.Abs(endTurn.Size.Y - 127.8f) <= 2f,
            $"end turn={endTurn.Size}; expected 312.4x127.8 (220x90 at 1.42)");
        _report.Check("exhaust_fits_on_the_rail", canvas.Encloses(exhaust)
            && after.All(other => !other.Intersects(exhaust)),
            $"exhaust={exhaust} draw={after[0]} end turn={after[1]} discard={after[2]} energy={energy}");
        _report.Check("combat_piles_rail_stable", after.Zip(before).All(pair => pair.First.Position.DistanceTo(pair.Second.Position) < 2f),
            $"rail now={string.Join("; ", after)} before={string.Join("; ", before)}");
        _report.Check("end_turn_centred_for_character",
            Math.Abs(after[1].GetCenter().X - SafeAreaCanvas.PortraitWidth * 0.5f) <= 2f,
            $"end turn={after[1]} canvas centre={SafeAreaCanvas.PortraitWidth * 0.5f}");
        _combatPilesEndTurn = after[1];
    }

    private partial async Task CheckEndTurnAfterNextTurnAsync(NCombatRoom room)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        await WaitSecondsAsync(0.8);
        Rect2 now = room.Ui.EndTurnButton.GetGlobalRect();
        _report.Check("end_turn_stable_across_turn", _combatPilesEndTurn is { } before
            && now.Position.DistanceTo(before.Position) < 2f,
            $"before={_combatPilesEndTurn} now={now}");
    }

    /// <summary>
    /// Test-only slow device: a harness node that blocks one 120 ms process frame right after each card arrival while
    /// the caller holds <see cref="Engine.MaxFps"/> at 30. The long frame is what makes a lagging phone draw the
    /// uncorrected RefreshLayout step; the probe's arrival_still check fails if that step becomes visible.
    /// </summary>
    private sealed class SlowDeviceHitchNode : Node
    {
        private const int HitchMsec = 120;
        private int _lastCount = -1;
        private bool _armed;
        private bool _pending;

        public void Arm()
        {
            _lastCount = -1;
            _pending = false;
            _armed = true;
            RenderingServer.FramePostDraw -= OnFramePostDraw;
            RenderingServer.FramePostDraw += OnFramePostDraw;
        }

        public void Disarm()
        {
            _armed = false;
            _pending = false;
            RenderingServer.FramePostDraw -= OnFramePostDraw;
        }

        private void OnFramePostDraw()
        {
            if (!_armed) return;
            NPlayerHand? hand = NCombatRoom.Instance?.Ui?.Hand;
            if (hand is null || !GodotObject.IsInstanceValid(hand)) return;
            int count = hand.ActiveHolders.Count(holder =>
                GodotObject.IsInstanceValid(holder) && holder.IsVisibleInTree());
            if (_lastCount >= 0 && count > _lastCount) _pending = true;
            _lastCount = count;
        }

        public override void _Process(double delta)
        {
            if (!_pending) return;
            _pending = false;
            OS.DelayMsec(HitchMsec);
        }

        public override void _ExitTree() => RenderingServer.FramePostDraw -= OnFramePostDraw;
    }

    private partial async Task CheckSlowTurnDrawAsync(NCombatRoom room)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        int previousMaxFps = Engine.MaxFps;
        double slowStart = Time.GetTicksMsec();
        SlowDeviceHitchNode? hitches = null;
        try
        {
            Engine.MaxFps = 30;
            hitches = new SlowDeviceHitchNode();
            _tree.Root.AddChild(hitches);
            hitches.Arm();
            _report.Pass("slow_turn_device",
                "Engine.MaxFps=30 with a 120ms hitch on the frame after each card arrival");
            BeginHandDrawProbe("slow_turn");
            var ui = room.Ui;
            await _pointer.TapAsync(CenterOf(ui.EndTurnButton));
            await WaitUntilAsync(() => CombatManager.Instance.PlayerActionsDisabled, "End Turn advances combat", ShortWait);
            await WaitUntilAsync(IsPlayerPlayPhase, "slow draw turn", MediumWait);
            await FinishHandDrawProbeAsync("slow_turn");
            _report.Pass("hand_draw_slow_turn_completed",
                $"hand={ui.Hand.ActiveHolders.Count} at Engine.MaxFps=30 with 120ms hitches");
        }
        catch (Exception error)
        {
            _report.Fail("hand_draw_slow_turn_completed", error.Message);
        }
        finally
        {
            if (hitches is not null)
            {
                hitches.Disarm();
                hitches.QueueFree();
            }
            Engine.MaxFps = previousMaxFps;
            _report.Pass("slow_turn_cost",
                $"{(Time.GetTicksMsec() - slowStart) / 1000.0:F1}s (30 fps + 120ms hitches, one turn draw)");
        }
    }

    // ---- hand-selection -----------------------------------------------------------------------------------------------

    private partial Vector2 HandSelectionNeutralPoint() => new(GetViewportSize().X * 0.5f, GetViewportSize().Y - 10f);

    // The native selection reflows when a card joins it (holders added, Confirm enabled, container re-scaled), and layout
    // passes can make Confirm or the peek button hop down and back for a few frames. Sample every process frame and every
    // drawn frame from the press until 0.6 s after the selection settles, then compare with the settled rail rects.
    private partial async Task TapSelectingAsync(NPlayerHand hand, VirtualPointer pointer, Vector2 point, string step,
        Func<bool> settled)
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            await pointer.TapAsync(point);
            return;
        }

        _selectionProbe ??= new SelectionMotionProbe(_tree);
        SelectionMotionProbe probe = _selectionProbe;
        probe.Begin(hand);
        try
        {
            await pointer.TapAsync(point);
            await WaitUntilAsync(settled, step + " selection settles", ShortWait);
            await WaitSecondsAsync(0.6);
            probe.EndAndCheck(_report, step);
        }
        finally
        {
            probe.Cancel();
        }
    }

    private partial void CheckHandSelectionPreview(string name, NPlayerHand hand, NButton confirm, RichTextLabel prompt,
        Node container, NCardHolder[] shown)
    {
        if (!SafeAreaCanvas.IsPortrait) return;
        Rect2 canvas = LauncherCanvas();
        Rect2 inset = canvas.Grow(-12f);
        Rect2[] cards = shown.Select(LauncherCardRect).ToArray();
        Rect2[] hitboxes = shown.Select(h => h.Hitbox.GetGlobalRect()).ToArray();
        Rect2[] activeHand = hand.ActiveHolders.Where(h => h.IsVisibleInTree()).Select(h => h.Hitbox.GetGlobalRect()).ToArray();
        _report.Check(name + "_selection_visible", shown.Length > 0 && container is CanvasItem { } item && item.IsVisibleInTree()
            && shown.All(h => h.IsVisibleInTree()), $"container={container.Name}; holders={hitboxes.Length}");
        // Every selected or previewed card stays 12 units inside the canvas, including the synthetic ten-card selection:
        // the layout must wrap to two rows rather than run the outer cards off the screen.
        _report.Check(name + "_cards_inside_canvas", shown.Length > 0 && cards.All(r => inset.Encloses(r))
            && hitboxes.All(r => canvas.Intersects(r)),
            $"canvas={inset}; cards={string.Join("; ", cards)}");
        bool pairwise = cards.Select((r, i) => cards.Skip(i + 1).All(other => !r.Intersects(other.Grow(-2f)))).All(ok => ok);
        _report.Check(name + "_cards_do_not_overlap", pairwise, $"cards={string.Join("; ", cards)}");

        Rect2 confirmBounds = confirm.GetGlobalRect();
        Rect2 promptBounds = prompt.GetGlobalRect();
        NCombatRoom? room = NCombatRoom.Instance;
        Control[] railControls = room is null
            ? Array.Empty<Control>()
            : new Control[] { room.Ui.EndTurnButton, room.Ui.DrawPile, room.Ui.DiscardPile, room.Ui.ExhaustPile };
        Rect2[] rail = railControls.Where(c => GodotObject.IsInstanceValid(c) && c.IsVisibleInTree())
            .Select(c => c.GetGlobalRect()).ToArray();
        if (room is not null) rail = rail.Append(LauncherEnergyRect(room)).ToArray();
        _report.Check(name + "_cards_clear_of_controls", cards.All(r => !r.Intersects(promptBounds)
                && !r.Intersects(confirmBounds) && rail.All(other => !r.Intersects(other))),
            $"prompt={promptBounds} confirm={confirmBounds} rail={string.Join("; ", rail)}");

        Vector2 confirmCenter = confirmBounds.GetCenter();
        _report.Check(name + "_confirm_reachable", confirm.IsVisibleInTree() && confirm.IsEnabled
            && canvas.Encloses(confirmBounds) && confirmBounds.Size.X >= PortraitBands.MinTarget
            && confirmBounds.Size.Y >= PortraitBands.MinTarget && activeHand.All(h => !h.HasPoint(confirmCenter)),
            $"confirm={confirmBounds} size={confirmBounds.Size} hand={string.Join("; ", activeHand)}");

        Rect2 peekBounds = hand.PeekButton.GetGlobalRect();
        Rect2[] upgradePair = hand.GetNodeOrNull<NUpgradePreview>("%UpgradePreview") is { } upgrade
            ? LauncherUpgradePairRects(upgrade)
            : Array.Empty<Rect2>();
        _report.Check(name + "_peek_reachable", hand.PeekButton.IsVisibleInTree()
                && peekBounds.Size.X >= PortraitBands.MinTarget && peekBounds.Size.Y >= PortraitBands.MinTarget
                && canvas.Encloses(peekBounds) && cards.All(r => !peekBounds.Intersects(r))
                && upgradePair.All(r => !peekBounds.Intersects(r))
                && rail.All(r => !peekBounds.Intersects(r)),
            $"peek={peekBounds} upgrade pair={string.Join("; ", upgradePair)} cards={string.Join("; ", cards)} " +
            $"rail={string.Join("; ", rail)}");
    }

    private partial async Task CheckArmamentsPreviewInteractionsAsync(NPlayerHand hand, NUpgradePreview preview, NButton confirm,
        NPreviewCardHolder before, NPreviewCardHolder after, CardModel first)
    {
        Rect2 canvas = LauncherCanvas().Grow(-12f);
        Rect2 beforeRect = LauncherCardRect(before), afterRect = LauncherCardRect(after);
        Rect2 confirmRect = confirm.GetGlobalRect();
        _report.Check("hand_select_armaments_preview_reachable", preview.IsVisibleInTree() && confirm.IsEnabled
            && canvas.Encloses(confirmRect) && confirmRect.Size.Y >= PortraitBands.MinTarget
            && canvas.Encloses(beforeRect) && canvas.Encloses(afterRect)
            && before.Hitbox.GetGlobalRect().Size.X >= PortraitBands.MinTarget
            && after.Hitbox.GetGlobalRect().Size.X >= PortraitBands.MinTarget,
            $"preview={preview.GetGlobalRect()} before={beforeRect} after={afterRect} confirm={confirmRect}");
        _report.Check("hand_select_armaments_card_selectable_by_tap", preview.Card == first && confirm.IsEnabled,
            "the tapped card is the native upgrade preview and Confirm is enabled");
        if (SafeAreaCanvas.IsPortrait)
        {
            Rect2 peekRect = hand.PeekButton.GetGlobalRect();
            Rect2[] rail = NCombatRoom.Instance is { } room
                ? new Control[] { room.Ui.DrawPile, room.Ui.DiscardPile, room.Ui.ExhaustPile }
                    .Where(c => GodotObject.IsInstanceValid(c) && c.IsVisibleInTree())
                    .Select(c => c.GetGlobalRect()).ToArray()
                : Array.Empty<Rect2>();
            _report.Check("hand_select_armaments_peek_off_rail", hand.PeekButton.IsVisibleInTree()
                    && rail.All(r => !peekRect.Intersects(r)),
                $"peek={peekRect} rail={string.Join("; ", rail)}");
            Vector2 peekPoint = CenterOf(hand.PeekButton);
            await _pointer.TapAsync(peekPoint);
            await WaitUntilAsync(() => hand.PeekButton.IsPeeking && !preview.IsVisibleInTree(),
                "the eye hides the upgrade preview", ShortWait);
            _report.Check("hand_select_armaments_peek_hides_selection",
                !preview.IsVisibleInTree() && !confirm.IsEnabled,
                $"peeking={hand.PeekButton.IsPeeking} preview={preview.IsVisibleInTree()} confirm={confirm.IsEnabled}");
            await CaptureAsync("hand_select_armaments_peeked");
            await _pointer.TapAsync(peekPoint);
            await WaitUntilAsync(() => !hand.PeekButton.IsPeeking && preview.IsVisibleInTree() && confirm.IsEnabled,
                "the eye restores the upgrade preview", MediumWait);
            _report.Pass("hand_select_armaments_peek_restores_selection",
                "the eye hides and restores the preview and Confirm");
        }
    }

    private partial Task CheckBurningPactPreviewInteractionsAsync(NPlayerHand hand, NSelectedHandCardContainer selected,
        NButton confirm, CardModel victim)
    {
        Rect2 canvas = LauncherCanvas();
        Rect2 confirmRect = confirm.GetGlobalRect();
        Rect2[] previews = selected.Holders.Select(h => h.Hitbox.GetGlobalRect()).ToArray();
        _report.Check("hand_select_burning_pact_preview_reachable", selected.IsVisibleInTree() && confirm.IsEnabled
            && canvas.Encloses(confirmRect) && confirmRect.Size.X >= PortraitBands.MinTarget
            && confirmRect.Size.Y >= PortraitBands.MinTarget
            && previews.Length == 1 && canvas.Encloses(previews[0])
            && previews[0].Size.X >= PortraitBands.MinTarget
            && selected.Holders.Any(h => h.CardModel == victim),
            $"selected={selected.GetGlobalRect()} previews={string.Join("; ", previews)} confirm={confirmRect}");
        return Task.CompletedTask;
    }

    private partial async Task CheckWholeHandPreviewInteractionsAsync(NPlayerHand hand, NSelectedHandCardContainer selected,
        NButton confirm, CardModel last)
    {
        Rect2 canvas = LauncherCanvas();
        Rect2 confirmRect = confirm.GetGlobalRect();
        Rect2[] previews = selected.Holders.Select(h => h.Hitbox.GetGlobalRect()).ToArray();
        _report.Check("hand_select_whole_hand_selection_visible", selected.IsVisibleInTree()
            && previews.Length == selected.Holders.Count(h => h.IsVisibleInTree()) && previews.Length == 10,
            $"selected={selected.GetGlobalRect()} holders={previews.Length}");
        _report.Check("hand_select_whole_hand_confirm_reachable", confirm.IsEnabled && canvas.Encloses(confirmRect)
            && confirmRect.Size.X >= PortraitBands.MinTarget && confirmRect.Size.Y >= PortraitBands.MinTarget,
            $"confirm={confirmRect} canvas={canvas}");
        await CaptureAsync("hand_select_whole_hand_ten");
    }

    /// <summary>
    /// Records the global rects of the native selection controls (Confirm, peek and the selected row or upgrade preview,
    /// with every holder) once per process frame and once after each draw for the duration of one selection tap. Confirm
    /// and peek must not move more than 2 units from their settled rect. The container and its holders may move while the
    /// layout settles, but not jump (a frame step over 40 units), reverse direction, or keep moving after the 0.6 s settle.
    /// </summary>
    private sealed class SelectionMotionProbe
    {
        private const float StrictDeviation = 2f;
        private const float TailDeviation = 2f;
        private const float MaxFrameStep = 40f;
        private const float ReversalStep = 1f;
        private const long TailFrames = 12;

        private readonly SceneTree _tree;
        private readonly List<TrackedControl> _controls = new();
        private readonly Dictionary<ulong, TrackedHolder> _holders = new();
        private NSelectedHandCardContainer? _selected;
        private NUpgradePreview? _upgrade;
        private bool _running;
        private long _startFrame;

        public SelectionMotionProbe(SceneTree tree) => _tree = tree;

        private readonly record struct FrameRect(long Frame, bool Visible, Rect2 Rect);

        private sealed class TrackedControl(string suffix, Control? node, bool strict)
        {
            public readonly string Suffix = suffix;
            public readonly Control? Node = node;
            public readonly bool Strict = strict;
            public readonly List<FrameRect> Frames = new();
        }

        private sealed class TrackedHolder
        {
            public readonly List<FrameRect> Frames = new();
        }

        public void Begin(NPlayerHand hand)
        {
            _controls.Clear();
            _holders.Clear();
            _selected = null;
            _upgrade = null;
            Control? confirm = hand.GetNodeOrNull<Control>("%SelectModeConfirmButton");
            Control? peek = hand.GetNodeOrNull<Control>("%PeekButton") ?? hand.PeekButton;
            if (hand.CurrentMode == NPlayerHand.Mode.UpgradeSelect)
                _upgrade = hand.GetNodeOrNull<NUpgradePreview>("%UpgradePreview");
            else
                _selected = hand.GetNodeOrNull<NSelectedHandCardContainer>("%SelectedHandCardContainer");
            Control? selection = _upgrade is { } upgradePreview ? upgradePreview : _selected;
            _controls.Add(new TrackedControl("confirm", confirm, strict: true));
            _controls.Add(new TrackedControl("peek", peek, strict: true));
            _controls.Add(new TrackedControl("selection_container", selection, strict: false));
            _startFrame = (long)Engine.GetProcessFrames();
            _running = true;
            _tree.ProcessFrame += Sample;
            RenderingServer.FramePostDraw += Sample;
        }

        public void Cancel()
        {
            if (!_running) return;
            _tree.ProcessFrame -= Sample;
            RenderingServer.FramePostDraw -= Sample;
            _running = false;
        }

        private void Sample()
        {
            if (!_running) return;
            long frame = (long)Engine.GetProcessFrames() - _startFrame;
            foreach (TrackedControl control in _controls)
            {
                bool visible = control.Node is { } node && GodotObject.IsInstanceValid(node) && node.IsVisibleInTree();
                control.Frames.Add(new FrameRect(frame, visible, visible ? control.Node!.GetGlobalRect() : default));
            }
            if (_selected is { } selected && GodotObject.IsInstanceValid(selected))
            {
                foreach (NSelectedHandCardHolder holder in selected.Holders)
                    SampleHolder(holder, frame);
            }
            if (_upgrade is { } upgrade && GodotObject.IsInstanceValid(upgrade) && upgrade.IsVisibleInTree())
            {
                foreach (string side in new[] { "Before", "After" })
                {
                    Control? sideNode = upgrade.GetNodeOrNull<Control>("%" + side) ?? upgrade.GetNodeOrNull<Control>(side);
                    if (sideNode?.GetChildren().OfType<NPreviewCardHolder>().FirstOrDefault() is { } holder)
                        SampleHolder(holder, frame);
                }
            }
        }

        private void SampleHolder(NCardHolder holder, long frame)
        {
            if (!GodotObject.IsInstanceValid(holder) || !holder.IsVisibleInTree()) return;
            ulong id = holder.GetInstanceId();
            if (!_holders.TryGetValue(id, out TrackedHolder? tracked))
            {
                tracked = new TrackedHolder();
                _holders[id] = tracked;
            }
            tracked.Frames.Add(new FrameRect(frame, true, LauncherCardRect(holder)));
        }

        public void EndAndCheck(TestReport report, string step)
        {
            Sample();
            Cancel();
            long lastFrame = _controls.SelectMany(c => c.Frames).Select(f => f.Frame).DefaultIfEmpty(0).Max();
            foreach (TrackedControl control in _controls)
            {
                string name = $"{step}_{control.Suffix}_stable";
                if (control.Node is null)
                {
                    report.Check(name, false, control.Suffix + " node not found");
                    continue;
                }
                List<FrameRect> visible = control.Frames.Where(f => f.Visible).ToList();
                if (visible.Count == 0)
                {
                    report.Check(name, false, control.Suffix + " not visible during the selection window");
                    continue;
                }
                if (control.Strict)
                {
                    Rect2 settled = visible[^1].Rect;
                    float max = 0f;
                    long at = visible[0].Frame;
                    Vector2 worst = settled.Position;
                    foreach (FrameRect frame in visible)
                    {
                        float distance = frame.Rect.Position.DistanceTo(settled.Position);
                        if (distance > max)
                        {
                            max = distance;
                            at = frame.Frame;
                            worst = frame.Rect.Position;
                        }
                    }
                    report.Check(name, max <= StrictDeviation,
                        $"max deviation {max:F1}u at +{at} frames (settled={settled.Position}, worst={worst}, samples={visible.Count})");
                }
                else
                {
                    report.Check(name, Smooth(visible, lastFrame, out string detail), detail);
                }
            }

            if (_holders.Count > 0)
            {
                bool ok = true;
                var details = new List<string>();
                int index = 0;
                foreach (TrackedHolder holder in _holders.Values)
                {
                    List<FrameRect> visible = holder.Frames.Where(f => f.Visible).ToList();
                    if (visible.Count < 2) continue;
                    bool stable = Smooth(visible, lastFrame, out string detail);
                    ok &= stable;
                    details.Add($"holder {index++}: {detail}");
                }
                report.Check(step + "_selected_holders_stable", ok, string.Join("; ", details));
            }
        }

        /// <summary>The container's motion may settle, but it must not jump, reverse or drift once settled.</summary>
        private static bool Smooth(List<FrameRect> visible, long lastFrame, out string detail)
        {
            Rect2 settled = visible[^1].Rect;
            long tailStart = Math.Max(0L, lastFrame - TailFrames);
            float tailMax = 0f;
            long tailAt = lastFrame;
            foreach (FrameRect frame in visible.Where(f => f.Frame >= tailStart))
            {
                float distance = frame.Rect.Position.DistanceTo(settled.Position);
                if (distance > tailMax)
                {
                    tailMax = distance;
                    tailAt = frame.Frame;
                }
            }

            FrameRect previous = visible[0];
            float previousDistance = previous.Rect.Position.DistanceTo(settled.Position);
            int direction = 0;
            float maxStep = 0f;
            long stepAt = 0;
            bool reversed = false;
            long reversalAt = 0;
            Vector2 reversalFrom = previous.Rect.Position;
            Vector2 reversalTo = previous.Rect.Position;
            for (int i = 1; i < visible.Count; i++)
            {
                FrameRect current = visible[i];
                float step = current.Rect.Position.DistanceTo(previous.Rect.Position);
                if (step > maxStep)
                {
                    maxStep = step;
                    stepAt = current.Frame;
                }
                float distance = current.Rect.Position.DistanceTo(settled.Position);
                int sign = distance > previousDistance + ReversalStep ? -1
                    : distance < previousDistance - ReversalStep ? 1
                    : 0;
                if (sign != 0)
                {
                    if (direction != 0 && sign != direction && !reversed)
                    {
                        reversed = true;
                        reversalAt = current.Frame;
                        reversalFrom = previous.Rect.Position;
                        reversalTo = current.Rect.Position;
                    }
                    direction = sign;
                }
                previous = current;
                previousDistance = distance;
            }

            if (tailMax <= TailDeviation && maxStep <= MaxFrameStep && !reversed)
            {
                detail = $"smooth settle (max step {maxStep:F1}u, settled tail {tailMax:F1}u, samples={visible.Count})";
                return true;
            }

            var notes = new List<string>();
            if (tailMax > TailDeviation) notes.Add($"settled tail deviation {tailMax:F1}u at +{tailAt}");
            if (maxStep > MaxFrameStep) notes.Add($"frame step {maxStep:F1}u at +{stepAt}");
            if (reversed) notes.Add($"direction reversal at +{reversalAt} ({reversalFrom} -> {reversalTo})");
            detail = string.Join("; ", notes);
            return false;
        }
    }

    /// <summary>
    /// Watches the combat rail from the moment an NCombatRoom enters the tree until 1.5 s after the player's first turn.
    /// Only drawn frames (RenderingServer.FramePostDraw) are enforced: a control seen there more than 4 units from its final
    /// settled position is the corner-to-rail snap the player saw. NodeAdded and ProcessFrame samples are kept for the
    /// report because they can include pre-layout positions that are never drawn.
    /// </summary>
    private sealed class CombatRailProbe
    {
        private const float MaxDrawnDeviation = 4f;
        private const double TurnGraceSeconds = 1.5;
        private const double MaxLifetimeSeconds = 30.0;

        private readonly SceneTree _tree;
        private readonly ScreenshotService _screenshots;
        private readonly TestReport _report;
        private readonly List<RoleSamples> _roles = new();
        private readonly double _startedMsec;
        private NCombatRoom? _room;
        private long _roomFrame;
        private double _turnStartMsec;
        private bool _turnStarted;
        private bool _running;
        private bool _firstPilesSeen;
        private string? _firstPilesPath;
        private string? _firstPilesError;

        public CombatRailProbe(SceneTree tree, ScreenshotService screenshots, TestReport report)
        {
            _tree = tree;
            _screenshots = screenshots;
            _report = report;
            _startedMsec = Time.GetTicksMsec();
        }

        private sealed class RoleSamples(string name)
        {
            public readonly string Name = name;
            public readonly List<FrameSample> Samples = new();
        }

        private readonly record struct FrameSample(long Frame, bool Drawn, Rect2 Rect);

        public void Begin()
        {
            _running = true;
            _tree.NodeAdded += OnNodeAdded;
            _tree.ProcessFrame += OnProcessFrame;
            RenderingServer.FramePostDraw += OnFramePostDraw;
            if (NCombatRoom.Instance is { } room)
            {
                _room = room;
                _roomFrame = (long)Engine.GetProcessFrames();
                Sample(drawn: false);
            }
        }

        public void Cancel()
        {
            if (!_running) return;
            _tree.NodeAdded -= OnNodeAdded;
            _tree.ProcessFrame -= OnProcessFrame;
            RenderingServer.FramePostDraw -= OnFramePostDraw;
            _running = false;
        }

        public void MarkTurnStarted()
        {
            if (!_running) return;
            _turnStarted = true;
            _turnStartMsec = Time.GetTicksMsec();
        }

        public async Task FinishAsync()
        {
            if (!_running) return;
            if (!_turnStarted)
            {
                Cancel();
                return;
            }

            try
            {
                double end = _turnStartMsec + TurnGraceSeconds * 1000.0;
                while (_running && Time.GetTicksMsec() < end)
                    await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
            }
            finally
            {
                Cancel();
            }

            Evaluate();
        }

        private void OnNodeAdded(Node node)
        {
            if (!_running || _room is not null) return;
            if (node is not NCombatRoom room) return;
            _room = room;
            _roomFrame = (long)Engine.GetProcessFrames();
            // Pre-first-process-frame positions, before the launcher's own ProcessFrame layout pass (never enforced).
            Sample(drawn: false);
        }

        private void OnProcessFrame()
        {
            if (!_running) return;
            if (Time.GetTicksMsec() - _startedMsec > MaxLifetimeSeconds * 1000.0)
            {
                Cancel();
                return;
            }
            Sample(drawn: false);
        }

        private void OnFramePostDraw()
        {
            if (!_running) return;
            Sample(drawn: true);
            if (_firstPilesSeen || _room is not { } room || room.Ui is not { } ui) return;
            if (!ui.DrawPile.IsVisibleInTree() || !ui.DiscardPile.IsVisibleInTree()) return;
            _firstPilesSeen = true;
            const string name = "combat_piles_first_visible";
            try
            {
                Image image = _screenshots.ReadRenderedFrame(name);
                _firstPilesPath = _screenshots.SaveFrame(name, image);
                _report.Screenshot(name, _firstPilesPath);
            }
            catch (Exception error)
            {
                _firstPilesError = error.Message;
            }
        }

        private void Sample(bool drawn)
        {
            if (_room is not { } room || !GodotObject.IsInstanceValid(room) || !room.IsInsideTree()) return;
            if (room.Ui is not { } ui) return;
            long frame = (long)Engine.GetProcessFrames() - _roomFrame;
            Add("draw", ui.DrawPile, frame, drawn);
            Add("discard", ui.DiscardPile, frame, drawn);
            Add("exhaust", ui.ExhaustPile, frame, drawn);
            Add("end-turn", ui.EndTurnButton, frame, drawn);
            Control energy = FindAll<NEnergyCounter>(ui.EnergyCounterContainer).FirstOrDefault()
                ?? (Control)ui.EnergyCounterContainer;
            Add("energy", energy, frame, drawn);
            if (FindAll<NStarCounter>(room).FirstOrDefault() is { } star)
                Add("star", star, frame, drawn);
        }

        private void Add(string role, Control? control, long frame, bool drawn)
        {
            if (control is null || !GodotObject.IsInstanceValid(control) || !control.IsVisibleInTree()) return;
            Role(role).Samples.Add(new FrameSample(frame, drawn, control.GetGlobalRect()));
        }

        private RoleSamples Role(string name)
        {
            foreach (RoleSamples role in _roles)
                if (role.Name == name) return role;
            var created = new RoleSamples(name);
            _roles.Add(created);
            return created;
        }

        private void Evaluate()
        {
            if (_firstPilesSeen && _firstPilesPath is null)
                _report.Check("combat_piles_first_visible_capture", false, _firstPilesError ?? "capture failed");

            string[] required = ["draw", "discard", "exhaust", "end-turn", "energy"];
            string[] missing = required
                .Where(name => !_roles.Any(role => role.Name == name && role.Samples.Any(sample => sample.Drawn)))
                .ToArray();
            if (missing.Length > 0)
                _report.Check("combat_rail_controls_visible", false,
                    "no drawn visible sample for " + string.Join(", ", missing));

            string worstRole = "none";
            float worst = 0f;
            long worstFrame = 0;
            Vector2 worstSample = default, worstSettled = default;
            var detail = new List<string>();
            foreach (RoleSamples role in _roles)
            {
                List<FrameSample> drawn = role.Samples.Where(sample => sample.Drawn).ToList();
                List<FrameSample> info = role.Samples.Where(sample => !sample.Drawn).ToList();
                if (drawn.Count == 0)
                {
                    detail.Add($"{role.Name}: no drawn sample ({info.Count} process samples)");
                    continue;
                }

                Vector2 settled = drawn[^1].Rect.Position;
                FrameSample hot = drawn[0];
                float max = 0f;
                foreach (FrameSample sample in drawn)
                {
                    float distance = sample.Rect.Position.DistanceTo(settled);
                    if (distance > max)
                    {
                        max = distance;
                        hot = sample;
                    }
                }
                float processMax = 0f;
                foreach (FrameSample sample in info)
                    processMax = Math.Max(processMax, sample.Rect.Position.DistanceTo(settled));
                detail.Add($"{role.Name}: drawn max {max:F1}u at +{hot.Frame} {hot.Rect.Position} " +
                           $"settled {settled}; process max {processMax:F1}u");
                if (max > worst)
                {
                    worst = max;
                    worstRole = role.Name;
                    worstFrame = hot.Frame;
                    worstSample = hot.Rect.Position;
                    worstSettled = settled;
                }
            }

            _report.Check("combat_rail_controls_do_not_move", worst <= MaxDrawnDeviation,
                $"worst {worstRole}: {worst:F1}u at +{worstFrame} (sample={worstSample}, settled={worstSettled}); " +
                string.Join("; ", detail));
        }
    }

    /// <summary>
    /// Samples every visible hand holder on drawn frames from the start of a turn draw until the hand settles: position,
    /// global scale and rotation. Cards must travel straight and keep one size: no scale reversal, no more than 2% above
    /// the final scale, no X/Y direction reversal over 2 units, and no per-frame step over the native glide step
    /// (distance to the slot × clamp(dt·7, 0, 1), with a 10% + 3-unit margin; broken entrances jumped several times that).
    /// </summary>
    private sealed class HandDrawProbe
    {
        private const float ScaleReversalStep = 0.01f;
        private const float ScaleOvershoot = 0.02f;
        private const float AxisReversal = 2f;
        private const int MiddleFrameOffset = 15;
        private const double MaxLifetimeSeconds = 30.0;

        private readonly SceneTree _tree;
        private readonly ScreenshotService _screenshots;
        private readonly TestReport _report;
        private readonly string _outputDirectory;
        private readonly Func<string?> _settledSignature;
        private readonly double _startedMsec;
        private readonly Dictionary<ulong, CardTrack> _cards = new();
        private readonly Dictionary<ulong, int> _cardIndex = new();
        // Holder count per drawn frame, for the modified-draw no-retarget check.
        private readonly List<(long Frame, int Count)> _frameCounts = new();
        // Arrival frames (holder count increase): the hand/container origins before and at the arrival, so a drawn
        // one-frame jump can be attributed to the holder or to a parent transform.
        private readonly List<ArrivalSample> _arrivals = new();
        private HashSet<ulong> _drawnIds = new();
        private long _lastDrawnFrame = -1;
        private Vector2 _lastContainerPos, _lastHandPos;
        // What the host's portrait passes cost during this window, per pass: the frames the pass ran in, its total, its
        // worst frame. Reported, never gated (the numbers rank passes; they are desktop numbers, not device numbers).
        private readonly double[] _passSum = new double[PortraitFrameTiming.PassCount];
        private readonly double[] _passMax = new double[PortraitFrameTiming.PassCount];
        private readonly int[] _passRuns = new int[PortraitFrameTiming.PassCount];
        private int _sampledFrames;
        private int _cardsAtEnd;
        private HashSet<ulong> _previousIds = new();
        private int _previousCount;
        private string _step = "";
        private bool _running;
        private bool _drawStarted;
        private int _extraDraw;
        private bool _allCardsStill;
        private long _drawStartFrame;
        private long _drawnFrames;
        private long _firstDrawnFrameCount;
        // Wall-clock between drawn frames: a hitch frame legitimately lets the native lerp cover more distance.
        private double _lastFrameMsec;
        private string? _firstShot;
        private string? _firstShotError;
        private string? _middleShot;
        private string? _middleShotError;

        public HandDrawProbe(SceneTree tree, ScreenshotService screenshots, TestReport report, string outputDirectory,
            Func<string?> settledSignature)
        {
            _tree = tree;
            _screenshots = screenshots;
            _report = report;
            _outputDirectory = outputDirectory;
            _settledSignature = settledSignature;
            _startedMsec = Time.GetTicksMsec();
        }

        private sealed class CardTrack
        {
            public readonly List<CardSample> Samples = new();
        }

        // Wall-clock between drawn frames (reporting) and the frame's engine process delta (the layout's dt, used for
        // the native glide allowance): a hitch frame legitimately lets the native lerp cover more distance.
        private readonly record struct CardSample(long Frame, Vector2 Position, float Scale, float Rotation,
            float FrameDtMs, float ProcessDtMs, Vector2 Target, Rect2 Hit);

        private readonly record struct ArrivalSample(long Frame, long PrevFrame, float DtMs, float ProcessDtMs,
            Vector2 Container, Vector2 Hand, Vector2 PrevContainer, Vector2 PrevHand);

        public void Begin(string step, int extraDraw, bool allCardsStill)
        {
            _step = step;
            _extraDraw = extraDraw;
            _allCardsStill = allCardsStill;
            _lastFrameMsec = 0.0;
            _arrivals.Clear();
            _drawnIds = new HashSet<ulong>();
            _lastDrawnFrame = -1;
            _lastContainerPos = Vector2.Zero;
            _lastHandPos = Vector2.Zero;
            _previousIds = CurrentIds();
            _previousCount = _previousIds.Count;
            _running = true;
            RenderingServer.FramePostDraw += OnFramePostDraw;
        }

        public void Cancel()
        {
            if (!_running) return;
            RenderingServer.FramePostDraw -= OnFramePostDraw;
            _running = false;
        }

        private static NPlayerHand? CurrentHand()
        {
            NCombatRoom? room = NCombatRoom.Instance;
            return room is null ? null : room.Ui?.Hand;
        }

        private static HashSet<ulong> CurrentIds()
        {
            NPlayerHand? hand = CurrentHand();
            if (hand is null || !GodotObject.IsInstanceValid(hand)) return new HashSet<ulong>();
            return hand.ActiveHolders
                .Where(holder => GodotObject.IsInstanceValid(holder) && holder.IsVisibleInTree())
                .Select(holder => holder.GetInstanceId())
                .ToHashSet();
        }

        private void OnFramePostDraw()
        {
            if (!_running) return;
            double frameMsec = Time.GetTicksMsec();
            float frameDtMs = _lastFrameMsec > 0.0
                ? (float)(frameMsec - _lastFrameMsec)
                : 16.7f;
            if (frameDtMs <= 0f) frameDtMs = 16.7f;
            _lastFrameMsec = frameMsec;
            if (Time.GetTicksMsec() - _startedMsec > MaxLifetimeSeconds * 1000.0)
            {
                Cancel();
                return;
            }

            NPlayerHand? hand = CurrentHand();
            if (hand is null || !GodotObject.IsInstanceValid(hand)) return;
            // The engine's delta for the frame just processed: this is the dt the native hand lerp used (the layout's
            // cap). The wall-clock between our FramePostDraw samples can differ by a few percent on the same frame.
            float processDtMs = (float)(hand.GetProcessDeltaTime() * 1000.0);
            if (processDtMs <= 0f) processDtMs = 16.7f;
            List<NHandCardHolder> holders = hand.ActiveHolders
                .Where(holder => GodotObject.IsInstanceValid(holder) && holder.IsVisibleInTree())
                .ToList();
            HashSet<ulong> ids = holders.Select(holder => holder.GetInstanceId()).ToHashSet();
            long frame = (long)Engine.GetProcessFrames();

            if (!_drawStarted)
            {
                bool entered = ids.Any(id => !_previousIds.Contains(id)) || ids.Count > _previousCount;
                _previousIds = ids;
                _previousCount = ids.Count;
                if (!entered) return;
                _drawStarted = true;
                _drawStartFrame = frame;
                _firstDrawnFrameCount = _drawnFrames;
                (_firstShot, _firstShotError) = CaptureFrame("hand_draw_" + _step + "_first");
            }

            // The draw's own frames only. This probe lives from before the End Turn tap through the next player turn, so
            // sampling from its start reported the enemy-turn window (enemy_turn_pass_ms) a second time, with the same
            // numbers under two names.
            SamplePasses();

            _drawnFrames++;
            long drawnFrame = frame - _drawStartFrame;
            _frameCounts.Add((drawnFrame, ids.Count));
            // A holder-count increase is a card arrival. Existing holders must not jump on the frame it lands; the hand
            // and CardHolderContainer origins tell whether a drawn jump came from a parent transform.
            if (_drawnIds.Count > 0 && ids.Count > _drawnIds.Count)
            {
                Vector2 handPos = hand.GlobalPosition;
                Vector2 containerPos = hand.CardHolderContainer?.GlobalPosition ?? handPos;
                _arrivals.Add(new ArrivalSample(drawnFrame, _lastDrawnFrame, frameDtMs, processDtMs,
                    containerPos, handPos, _lastContainerPos, _lastHandPos));
            }
            _drawnIds = ids;
            _lastDrawnFrame = drawnFrame;
            _lastHandPos = hand.GlobalPosition;
            _lastContainerPos = hand.CardHolderContainer?.GlobalPosition ?? _lastHandPos;
            if (_middleShot is null && _middleShotError is null
                && _drawnFrames - _firstDrawnFrameCount >= MiddleFrameOffset)
            {
                (_middleShot, _middleShotError) = CaptureFrame("hand_draw_" + _step + "_middle");
            }

            foreach (NHandCardHolder holder in holders)
            {
                ulong id = holder.GetInstanceId();
                if (!_cards.TryGetValue(id, out CardTrack? track))
                {
                    track = new CardTrack();
                    _cards[id] = track;
                    _cardIndex[id] = _cardIndex.Count;
                }
                Transform2D transform = holder.GetGlobalTransform();
                // TargetPosition is the holder's position in the hand container's space; compare it in global space
                // with the sampled global position (the container transform is stable during the draw).
                Vector2 target = holder.GetParent() is CanvasItem parent
                    ? parent.GetGlobalTransform() * holder.TargetPosition
                    : holder.TargetPosition;
                track.Samples.Add(new CardSample(drawnFrame, transform.Origin,
                    Math.Abs(transform.Scale.X), Mathf.RadToDeg(transform.Rotation), frameDtMs, processDtMs, target,
                    holder.Hitbox.GetGlobalRect()));
            }
        }

        /// <summary>
        /// Folds the last completed frame's per-pass milliseconds into the draw's figures; called only once the draw has
        /// started, so the window is the draw and not the turn before it.
        /// </summary>
        private void SamplePasses()
        {
            ReadOnlySpan<double> ms = PortraitFrameTiming.LastFrameMs();
            _sampledFrames++;
            for (int pass = 0; pass < PortraitFrameTiming.PassCount; pass++)
            {
                double value = ms[pass];
                if (value <= 0.0) continue;
                _passSum[pass] += value;
                if (value > _passMax[pass]) _passMax[pass] = value;
                _passRuns[pass]++;
            }
        }

        /// <summary>
        /// The window's cost, as one reported (never gated) step: every bucket of
        /// <see cref="PortraitFrameTiming"/> that ran, as mean/worst ms over the frames it ran in, plus the frames
        /// sampled and the hand size at the end. The report loops all <see cref="PortraitFrameTiming.PassCount"/>
        /// buckets, so the newer <c>stage</c>, <c>selection</c>, <c>orbs</c> and <c>hpText</c> buckets are included.
        /// <c>combat</c> is the whole room pass and <c>preDraw</c> the whole pre-draw pass, with <c>stage</c>/<c>selection</c>
        /// nested inside <c>combat</c>, <c>orbs</c> inside <c>preDraw</c> and <c>hpText</c> inside <c>labels</c>.
        /// </summary>
        private void ReportPassCost(string name)
        {
            var text = new System.Text.StringBuilder();
            text.Append("frames=").Append(_sampledFrames).Append(" cards=").Append(_cardsAtEnd);
            for (int pass = 0; pass < PortraitFrameTiming.PassCount; pass++)
            {
                int runs = _passRuns[pass];
                if (runs == 0) continue;
                text.Append(' ').Append(PortraitFrameTiming.Names[pass]).Append('=')
                    .Append((_passSum[pass] / runs).ToString("F2"))
                    .Append('/').Append(_passMax[pass].ToString("F2"))
                    .Append('@').Append(runs);
            }
            _report.Pass(name, text.ToString());
        }

        private (string? Path, string? Error) CaptureFrame(string name)
        {
            try
            {
                Image image = _screenshots.ReadRenderedFrame(name);
                return (_screenshots.SaveFrame(name, image), null);
            }
            catch (Exception error)
            {
                return (null, error.Message);
            }
        }

        public async Task FinishAsync(string step)
        {
            if (!_running) return;
            _step = step;
            bool settled = false;
            try
            {
                if (_drawStarted)
                {
                    string? previous = null;
                    int stable = 0;
                    double start = Time.GetTicksMsec();
                    while (Time.GetTicksMsec() - start < 4000)
                    {
                        await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
                        string? signature = _settledSignature();
                        if (signature is not null && !IsPending(signature) && signature == previous)
                        {
                            if (++stable >= 2)
                            {
                                settled = true;
                                break;
                            }
                        }
                        else
                        {
                            stable = 0;
                        }
                        previous = signature;
                    }
                }

                // Ten more drawn frames so the settled tail is in the track and the middle capture has had time.
                for (int i = 0; i < 10; i++)
                    await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
            }
            finally
            {
                Cancel();
            }

            Evaluate(settled);
        }

        private void Evaluate(bool settled)
        {
            _cardsAtEnd = _cards.Count;
            ReportPassCost($"hand_draw_{_step}_pass_ms");
            if (!_drawStarted)
            {
                _report.Check($"hand_draw_{_step}_detected", false,
                    "no new hand holder appeared before the draw window closed");
                return;
            }

            _report.Check($"hand_draw_{_step}_detected", true,
                $"draw started at frame 0, {_cards.Count} cards, {_drawnFrames} drawn frames" +
                (_allCardsStill ? " (reduced first draw: BigMushroom 5 - 2 = 3 unless Innate raises it)" : ""));
            _report.Check($"hand_draw_{_step}_settled", settled,
                settled ? "hand settled" : "hand did not settle within 4 s");
            if (_firstShot is not null) _report.Screenshot($"hand_draw_{_step}_first", _firstShot);
            else _report.Check($"hand_draw_{_step}_first", false, _firstShotError ?? "first frame not captured");
            if (_middleShot is not null) _report.Screenshot($"hand_draw_{_step}_middle", _middleShot);
            else _report.Check($"hand_draw_{_step}_middle", false, _middleShotError ?? "middle frame not captured");

            var violations = new List<string>();
            var finals = new List<string>();
            bool anyCard = false;
            float worstSeverity = 0f;
            string worstCard = "none";
            long worstFrame = 0;
            string worstDetail = "";
            float worstDtMs = 0f;
            float worstProcessDtMs = 0f;

            foreach ((ulong id, CardTrack track) in _cards)
            {
                if (track.Samples.Count < 3) continue;
                anyCard = true;
                foreach (CardSample sample in track.Samples)
                {
                    if (sample.FrameDtMs > worstDtMs) worstDtMs = sample.FrameDtMs;
                    if (sample.ProcessDtMs > worstProcessDtMs) worstProcessDtMs = sample.ProcessDtMs;
                }
                int index = _cardIndex[id];
                Vector2 finalPosition = track.Samples[^1].Position;
                float finalScale = track.Samples[^1].Scale;
                float maxStep = 0f;
                float worstStepRatio = 0f;
                float worstStep = 0f, worstStepAllowed = 0f, worstStepDt = 16.7f, worstStepWallDt = 16.7f;
                long worstStepFrame = 0;
                float maxOver = 0f;
                long overFrame = 0;
                bool scaleReversed = false;
                long scaleFrame = 0;
                float scaleFrom = 0f, scaleTo = 0f;
                bool reversedX = false, reversedY = false;
                long revXFrame = 0, revYFrame = 0;
                int scaleSign = 0, xSign = 0, ySign = 0;

                for (int i = 1; i < track.Samples.Count; i++)
                {
                    CardSample a = track.Samples[i - 1];
                    CardSample b = track.Samples[i];
                    float deltaScale = b.Scale - a.Scale;
                    if (Math.Abs(deltaScale) > ScaleReversalStep)
                    {
                        int sign = Math.Sign(deltaScale);
                        if (scaleSign != 0 && sign != scaleSign)
                        {
                            scaleReversed = true;
                            scaleFrame = b.Frame;
                            scaleFrom = a.Scale;
                            scaleTo = b.Scale;
                        }
                        scaleSign = sign;
                    }

                    if (b.Scale > finalScale * (1f + ScaleOvershoot) + 0.0001f)
                    {
                        float over = b.Scale - finalScale * (1f + ScaleOvershoot);
                        if (over > maxOver)
                        {
                            maxOver = over;
                            overFrame = b.Frame;
                        }
                    }

                    // One native glide step: the hand lerp moves distance_to_slot × clamp(dt·7, 0, 1) each frame. Trips
                    // from the draw pile to the far slots are 800-900u, so legitimate first steps are 94-105u at 60 fps;
                    // a fixed 90u cap flagged them. Long frames are excused by max(dt, 16.7) and the clamp, and the
                    // 10% + 3u margin covers sampling/rounding.
                    float stepSeconds = MathF.Max(b.ProcessDtMs, 16.7f) / 1000f;
                    float glide = Math.Clamp(stepSeconds * 7f, 0f, 1f);
                    float allowed = a.Position.DistanceTo(finalPosition) * glide * 1.10f + 3f;
                    float step = b.Position.DistanceTo(a.Position);
                    if (step > maxStep)
                        maxStep = step;
                    float stepRatio = step / allowed;
                    if (stepRatio > worstStepRatio)
                    {
                        worstStepRatio = stepRatio;
                        worstStep = step;
                        worstStepAllowed = allowed;
                        worstStepDt = b.ProcessDtMs;
                        worstStepWallDt = b.FrameDtMs;
                        worstStepFrame = b.Frame;
                    }

                    int moveX = AxisMovement(a.Position.X, b.Position.X, finalPosition.X);
                    if (moveX != 0)
                    {
                        if (xSign != 0 && moveX != xSign) reversedX = true;
                        if (moveX != 0) revXFrame = b.Frame;
                        xSign = moveX;
                    }
                    int moveY = AxisMovement(a.Position.Y, b.Position.Y, finalPosition.Y);
                    if (moveY != 0)
                    {
                        if (ySign != 0 && moveY != ySign) reversedY = true;
                        if (moveY != 0) revYFrame = b.Frame;
                        ySign = moveY;
                    }
                }

                bool bad = scaleReversed || maxOver > 0f || reversedX || reversedY || worstStepRatio > 1f;
                float severity = 0f;
                long frame = 0;
                string detail = "";
                if (scaleReversed)
                {
                    severity = 2f;
                    frame = scaleFrame;
                    detail = $"scale reversal {scaleFrom:F3}->{scaleTo:F3}";
                }
                if (maxOver > 0f)
                {
                    float candidate = 1f + maxOver / Math.Max(0.0001f, finalScale * ScaleOvershoot);
                    if (candidate > severity)
                    {
                        severity = candidate;
                        frame = overFrame;
                        detail = $"scale above final by {maxOver:F3}";
                    }
                }
                if (reversedX && 2f > severity)
                {
                    severity = 2f;
                    frame = revXFrame;
                    detail = "x direction reversal";
                }
                if (reversedY && 2f > severity)
                {
                    severity = 2f;
                    frame = revYFrame;
                    detail = "y direction reversal";
                }
                if (worstStepRatio > 1f)
                {
                    float candidate = 1f + worstStepRatio;
                    if (candidate > severity)
                    {
                        severity = candidate;
                        frame = worstStepFrame;
                        detail = $"frame step {worstStep:F1}u > {worstStepAllowed:F1}u allowed " +
                                 $"(process dt {worstStepDt:F1}ms, wall dt {worstStepWallDt:F1}ms)";
                    }
                }

                finals.Add($"card {index}: final pos={finalPosition} scale={finalScale:F4} " +
                           $"rot={track.Samples[^1].Rotation:F2}");
                if (bad)
                {
                    violations.Add($"card {index} at +{frame}: {detail} " +
                                   $"(max step {maxStep:F1}u, over {maxOver:F3})");
                    if (severity > worstSeverity)
                    {
                        worstSeverity = severity;
                        worstCard = $"card {index}";
                        worstFrame = frame;
                        worstDetail = detail;
                    }
                }
            }

            bool ok = anyCard && violations.Count == 0;
            _report.Check($"hand_draw_{_step}_straight", ok,
                $"worst wall dt={worstDtMs:F1}ms process dt={worstProcessDtMs:F1}ms; " +
                $"worst {worstCard} at +{worstFrame}: {worstDetail}; " +
                $"violations={string.Join("; ", violations)}; finals={string.Join("; ", finals)}");
            CheckArrivalStill();
            if (_extraDraw > 0 || _allCardsStill) CheckNoRetarget();
            WriteTrack(violations, finals);
        }

        /// <summary>
        /// On every card-arrival frame (holder count increase), a holder that was already in the hand must not jump: its
        /// drawn hitbox may move only one native glide step toward its final slot (2u floor) from the previous drawn
        /// frame. This is the device bug where the native RefreshLayout's first synchronous AnimPosition step was drawn
        /// uncorrected (+50,+160 for one frame). The hand/container origins are reported to attribute a jump to a
        /// parent transform.
        /// </summary>
        private void CheckArrivalStill()
        {
            const float MinAllowance = 2f;
            string name = $"hand_draw_{_step}_arrival_still";
            float worstDelta = 0f, worstAllowed = 0f, worstDt = 0f, worstWallDt = 0f;
            float worstContainerDelta = 0f, worstHandDelta = 0f;
            string worstCard = "none";
            long worstFrame = 0;
            int checkedHolders = 0;
            float worstExcess = float.NegativeInfinity;
            foreach (ArrivalSample arrival in _arrivals)
            {
                foreach ((ulong id, CardTrack track) in _cards)
                {
                    int at = track.Samples.FindIndex(sample => sample.Frame == arrival.Frame);
                    if (at <= 0 || track.Samples[at - 1].Frame != arrival.PrevFrame) continue;
                    CardSample previous = track.Samples[at - 1];
                    CardSample current = track.Samples[at];
                    checkedHolders++;
                    float delta = current.Hit.Position.DistanceTo(previous.Hit.Position);
                    Vector2 finalSlot = track.Samples[^1].Hit.Position;
                    float glide = Math.Clamp(MathF.Max(arrival.ProcessDtMs, 16.7f) / 1000f * 7f, 0f, 1f);
                    float allowed = MathF.Max(MinAllowance,
                        previous.Hit.Position.DistanceTo(finalSlot) * glide * 1.10f + 3f);
                    float excess = delta - allowed;
                    if (excess > worstExcess)
                    {
                        worstExcess = excess;
                        worstDelta = delta;
                        worstAllowed = allowed;
                        worstDt = arrival.ProcessDtMs;
                        worstWallDt = arrival.DtMs;
                        worstCard = $"card {_cardIndex[id]}";
                        worstFrame = arrival.Frame;
                        worstContainerDelta = arrival.Container.DistanceTo(arrival.PrevContainer);
                        worstHandDelta = arrival.Hand.DistanceTo(arrival.PrevHand);
                    }
                }
            }

            bool ok = checkedHolders == 0 || worstExcess <= 0f;
            _report.Check(name, ok,
                checkedHolders == 0
                    ? $"arrival frames={_arrivals.Count}, no holder existed before an arrival"
                    : $"arrival frames={_arrivals.Count} checked={checkedHolders}; worst {worstCard} at +{worstFrame}: " +
                      $"drawn delta={worstDelta:F2}u allowed={worstAllowed:F2}u " +
                      $"(process dt {worstDt:F1}ms, wall dt {worstWallDt:F1}ms); " +
                      $"container Δ={worstContainerDelta:F2}u hand Δ={worstHandDelta:F2}u");
        }

        /// <summary>
        /// No retarget: a card's layout target must not change after it arrives, and its motion must stay toward that
        /// target (no backward step over 2u). The modified turn draw checks the cards already in hand when the extra
        /// ones arrive (from <c>steadyFrame</c>); the reduced first draw checks every card from its own arrival, so
        /// releasing the layout lock at the final count cannot re-centre the hand.
        /// </summary>
        private void CheckNoRetarget()
        {
            const float Tolerance = 2f;
            string name = $"hand_draw_{_step}_existing_still";
            if (_frameCounts.Count == 0)
            {
                _report.Check(name, false, "no drawn frames to measure");
                return;
            }
            int finalCount = _frameCounts[^1].Count;
            int baseCount = _allCardsStill ? finalCount : finalCount - _extraDraw;
            long steady = 0;
            if (!_allCardsStill)
            {
                if (baseCount <= 0)
                {
                    _report.Check(name, false,
                        $"final hand={finalCount} leaves no base hand above {_extraDraw} extra draws");
                    return;
                }
                long? steadyFrame = null;
                foreach ((long frame, int count) in _frameCounts)
                {
                    if (count < baseCount) continue;
                    steadyFrame = frame;
                    break;
                }
                if (steadyFrame is not { } found)
                {
                    _report.Check(name, false,
                        $"hand peaked at {finalCount}, never reached the {baseCount}-card base hand");
                    return;
                }
                steady = found;
            }

            float worstTargetChange = 0f, worstReversal = 0f, worstSettle = 0f;
            string worstTargetCard = "none", worstReversalCard = "none", worstSettleCard = "none";
            long worstTargetFrame = 0, worstReversalFrame = 0;
            int checkedCards = 0;
            foreach ((ulong id, CardTrack track) in _cards)
            {
                if (track.Samples.Count == 0) continue;
                if (!_allCardsStill && track.Samples[0].Frame > steady) continue;
                int first = _allCardsStill ? 0 : track.Samples.FindIndex(sample => sample.Frame >= steady);
                if (first < 0) continue;
                checkedCards++;
                int index = _cardIndex[id];
                Vector2 finalRest = track.Samples[^1].Position;
                Vector2 referenceTarget = track.Samples[first].Target != Vector2.Zero
                    ? track.Samples[first].Target
                    : finalRest;
                float previousDistance = (track.Samples[first].Position - referenceTarget).Length();
                for (int i = first; i < track.Samples.Count; i++)
                {
                    CardSample sample = track.Samples[i];
                    Vector2 target = sample.Target != Vector2.Zero ? sample.Target : referenceTarget;
                    float change = (target - referenceTarget).Length();
                    if (change > worstTargetChange)
                    {
                        worstTargetChange = change;
                        worstTargetCard = $"card {index}";
                        worstTargetFrame = sample.Frame;
                    }
                    float distance = (sample.Position - referenceTarget).Length();
                    if (i > first && distance > previousDistance + Tolerance)
                    {
                        float reversal = distance - previousDistance;
                        if (reversal > worstReversal)
                        {
                            worstReversal = reversal;
                            worstReversalCard = $"card {index}";
                            worstReversalFrame = sample.Frame;
                        }
                    }
                    previousDistance = distance;
                }
                float settle = (track.Samples[^1].Position - referenceTarget).Length();
                if (settle > worstSettle)
                {
                    worstSettle = settle;
                    worstSettleCard = $"card {index}";
                }
            }

            bool ok = checkedCards > 0 && worstTargetChange <= Tolerance && worstReversal <= Tolerance;
            _report.Check(name, ok,
                (_allCardsStill
                    ? $"all cards (final hand={finalCount}); "
                    : $"final hand={finalCount} base={baseCount} steady frame=+{steady}; ") +
                $"checked={checkedCards}; " +
                $"worst target change={worstTargetChange:F2}u ({worstTargetCard} at +{worstTargetFrame}); " +
                $"worst reversal={worstReversal:F2}u ({worstReversalCard} at +{worstReversalFrame}); " +
                $"worst final settle={worstSettle:F2}u ({worstSettleCard})");
        }

        /// <summary>Sign of motion relative to the final position, ignoring moves of 2 units or less.</summary>
        private static int AxisMovement(float previous, float current, float final)
        {
            float move = current - previous;
            if (Math.Abs(move) <= AxisReversal) return 0;
            float toFinal = final - previous;
            if (Math.Abs(toFinal) <= 0.5f) return 0;
            return Math.Sign(move) == Math.Sign(toFinal) ? 1 : -1;
        }

        private void WriteTrack(List<string> violations, List<string> finals)
        {
            try
            {
                Directory.CreateDirectory(_outputDirectory);
                string path = Path.Combine(_outputDirectory, $"hand_draw_{_step}.txt");
                var lines = new List<string>
                {
                    $"# hand draw {_step}",
                    $"# draw start frame 0, {_drawnFrames} drawn frames",
                    "frame dtms pdtms card x y scale rotation",
                };
                foreach ((ulong id, CardTrack track) in _cards)
                {
                    int index = _cardIndex[id];
                    foreach (CardSample sample in track.Samples)
                        lines.Add($"{sample.Frame} {sample.FrameDtMs:F1} {sample.ProcessDtMs:F1} {index} " +
                                  $"{sample.Position.X:F2} {sample.Position.Y:F2} {sample.Scale:F5} {sample.Rotation:F3}");
                }
                lines.Add("# finals");
                lines.AddRange(finals);
                lines.Add("# violations");
                List<string> violationLines = violations.Count == 0 ? new List<string> { "none" } : violations;
                lines.AddRange(violationLines);
                File.WriteAllLines(path, lines);
                _report.Pass($"hand_draw_{_step}_track", path);
            }
            catch (Exception error)
            {
                _report.Check($"hand_draw_{_step}_track", false, error.Message);
            }
        }
    }
}
