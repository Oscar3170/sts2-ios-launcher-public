using System;

namespace STS2Mobile.Platform;

/// <summary>How the hand is split over its permanent two rows: the back (upper) row fills first.</summary>
public readonly record struct HandRowSplit(int BackRowCount, int FrontRowCount);

/// <summary>
/// One hand's fit: the row split, the resting card scale, the flat-mode horizontal gap and the card's drawn size. The
/// scale is the shape's full size for every count and mode. Sizes are canvas units at scale 1.
/// </summary>
public readonly record struct HandRowFit(
    int UpperCount,
    int LowerCount,
    float Scale,
    float Gap,
    float CardWidth,
    float CardHeight);

public readonly record struct PortraitCardSlot(
    float LocalX,
    float LocalY,
    float Scale,
    float AngleDegrees,
    int RowIndex);

public readonly record struct PortraitHudLayout(
    float RailTopY,
    float EnergyContainerX,
    float EnergyContainerY,
    float EnergyScale,
    float StarCenterX,
    float StarCenterY,
    float ExhaustShowX,
    float ExhaustShowY,
    float ExhaustHideX,
    float ExhaustScale,
    float EndTurnShowX,
    float EndTurnShowY,
    float EndTurnHideX,
    float EndTurnScale,
    float DrawShowX,
    float DrawShowY,
    float DrawHideX,
    float DrawHideY,
    float DiscardShowX,
    float DiscardShowY,
    float DiscardHideX,
    float DiscardHideY,
    float PingShowX,
    float PingShowY,
    float PileScale);

public readonly record struct PortraitCreatureSlot(
    float CanvasX,
    float CanvasFeetY,
    float BaseScale,
    int RowDepth);

/// <summary>
/// Where the native in-hand selection UI goes in portrait: one large Confirm on the hand-control rail, and the upgrade
/// preview fitted between the selection prompt and the rail.
/// </summary>
public readonly record struct PortraitSelectionLayout(
    float ConfirmCenterX,
    float ConfirmCenterY,
    float ConfirmScale,
    float PreviewTopY,
    float PreviewCenterY,
    float PreviewScale);

/// <summary>
/// The native selected-card previews arranged in the band between the prompt and the rail: one row, or two when that is
/// materially larger, centred on the band. Positions are in the container's unscaled space and multiplied by
/// <see cref="Scale"/> when drawn.
/// </summary>
public readonly record struct PortraitSelectedCardsLayout(
    int Rows,
    int Columns,
    float StepX,
    float StepY,
    float CardWidth,
    float CardHeight,
    float Scale,
    float CenterY);

/// <summary>
/// The horizontal bands of a portrait fight, top to bottom, on the canvas. They depend only on the canvas and the top bar,
/// never on the hand, the turn or the roster, so nothing in the fight moves when a card is drawn or played.
/// </summary>
/// <param name="StageTopY">Where the stage starts under the run bars (<c>capHeight + PortraitBands.RunContentTop</c> in GameHost).</param>
/// <param name="FrontFeetY">The line the front creatures stand on.</param>
/// <param name="RailTopY">The top of the hand-control row: End Turn, energy and the piles.</param>
/// <param name="HandTopY">The bottom of the hand-control row and the top of the two hand rows; the floor art ends here.</param>
/// <param name="Compact">A 9:16 or shorter canvas: the stage is short, so creatures may reach up toward the run bars.</param>
public readonly record struct PortraitCombatBands(
    float StageTopY,
    float FrontFeetY,
    float RailTopY,
    float HandTopY,
    bool Compact);

/// <summary>
/// Pure portrait-mode combat geometry and playability contrast constants on the 1080-wide portrait canvas.
/// Kept free of engine types so layout invariants are directly unit-tested on the host.
/// </summary>
public static class PortraitCombatMath
{
    public const float CardDefaultWidth = 300f;
    public const float CardDefaultHeight = 422f;

    /// <summary>The portrait design is TouchSpireUI's 1680-wide canvas; every converted value is multiplied by this.</summary>
    private const float DesignScale = SafeAreaCanvas.PortraitWidth / 1680f;

    /// <summary>
    /// A hand card's resting scale on the compact (9:16) canvas; both rows use it at every hand size. Ten cards at this
    /// scale are 75 by 105 points, and the two rows plus the rail leave the stage about 350 units at 9:16.
    /// </summary>
    public const float CompactHandCardScale = 0.72f;

    /// <summary>The same at TouchSpireUI's PortraitHandCardScale (1.35 on 1680): cards are 24.1% of the canvas width.</summary>
    public const float TallHandCardScale = 1.35f * DesignScale;

    /// <summary>A pressed card grows by this factor of its own resting scale, in place about its centre.</summary>
    public const float HeldCardGrowth = 1.08f;

    /// <summary>The canvas height at or under which the fight uses the compact (9:16) bands.</summary>
    public const float CompactCanvasHeight = SafeAreaCanvas.MinPortraitHeight + 4f;

    private const float CompactHandGap = 8f;
    private const float CompactHandRowGap = 14f;
    // The bottom row's edge stays above the home indicator's 34-point gesture area (about 98 units).
    private const float CompactHandBottomMargin = 100f;
    private const float CompactRailHeight = 170f;

    // TouchSpireUI's portrait hand, converted from its 1680-wide canvas: card 1.35, gap 10, row gap 18,
    // side margin 72, bottom margin 6 + SafeBottom 90, rail 250 above the hand.
    private const float TallHandGap = 10f * DesignScale;
    private const float TallHandRowGap = 18f * DesignScale;
    private const float TallHandBottomMargin = (6f + 90f) * DesignScale;
    private const float TallRailHeight = 250f * DesignScale;

    private const float CompactFloorGap = 70f;
    private const float TallFloorGap = 130f;

    /// <summary>The rail's height for this shape (constant within a fight).</summary>
    public static float RailHeightFor(bool compact) => compact ? CompactRailHeight : TallRailHeight;

    /// <summary>The energy counter's rail X; shifted right to open the star slot beside the draw pile.</summary>
    public const float EnergyRailX = 180f;

    /// <summary>The native energy counter's orb is 128 units square.</summary>
    public const float EnergyOrbSize = 128f;

    /// <summary>
    /// The star badge's centre offsets from the drawn energy orb's centre. This is TouchSpireUI's lower-left corner
    /// badge, moved further left than its default -0.4 so the count's ink (up to two digits) clears both the draw pile
    /// and the energy number's ink while the badge still overlaps the orb's lower-left edge.
    /// </summary>
    public const float StarOrbOffsetX = -0.5273f;
    public const float StarOrbOffsetY = 0.3f;

    /// <summary>The native selected-card holder is drawn at 0.8 of a card, with a 24-unit gap when laid out in a row.</summary>
    public const float SelectionCardSmallScale = 0.8f;
    public const float SelectionCardGap = 24f;

    /// <summary>The largest scale a selected preview takes (TouchSpireUI's 1.72 at a 1680-wide canvas, converted).</summary>
    public const float SelectionCardMaxScale = 1.72f * (SafeAreaCanvas.PortraitWidth / 1680f) / SelectionCardSmallScale;

    /// <summary>The fan angle of the outermost card of a hand row; the row's middle card is upright.</summary>
    private const float HandRowAngle = 4.2f;

    /// <summary>The fight's bands for this canvas and stage top; see <see cref="PortraitCombatBands"/>.</summary>
    public static PortraitCombatBands ComputeCombatBands(float canvasHeight, float stageTopY)
    {
        float ch = Math.Max(canvasHeight, SafeAreaCanvas.MinPortraitHeight);
        bool compact = ch <= CompactCanvasHeight;
        float stageTop = stageTopY;
        float cardScale = compact ? CompactHandCardScale : TallHandCardScale;
        float rowGap = compact ? CompactHandRowGap : TallHandRowGap;
        float bottomMargin = compact ? CompactHandBottomMargin : TallHandBottomMargin;
        float handTop = ch - (2f * CardDefaultHeight * cardScale + rowGap + bottomMargin);
        float railTop = handTop - RailHeightFor(compact);
        float feet = railTop - (compact ? CompactFloorGap : TallFloorGap);
        return new PortraitCombatBands(stageTop, feet, railTop, handTop, compact);
    }

    public const float UnplayableBodyBrightness = 0.54f;

    /// <summary>How far the energy cost badge reaches past a card's left edge, in card units (measured).</summary>
    public const float CostBadgeOverhang = 28f;

    /// <summary>The gap kept between every hand card and the canvas's side edges.</summary>
    public const float HandEdgeMargin = 12f;

    private const float CanvasHalfWidth = SafeAreaCanvas.PortraitWidth * 0.5f;

    // #00F4FC playable highlight contour and #FF5252 unaffordable energy cost text.
    public static readonly (float R, float G, float B, float A) PlayableHighlightRgba = (0f, 244f / 255f, 252f / 255f, 0.98f);
    public static readonly (float R, float G, float B, float A) UnplayableBodyModulateRgba =
        (UnplayableBodyBrightness, UnplayableBodyBrightness, UnplayableBodyBrightness, 1f);
    public static readonly (float R, float G, float B, float A) UnaffordableCostRgba = (1f, 82f / 255f, 82f / 255f, 1f);

    public static int EffectiveCardCount(int activeHoldersInHand, bool inUncommittedCardPlay) =>
        Math.Max(0, activeHoldersInHand) + (inUncommittedCardPlay ? 1 : 0);

    /// <summary>
    /// The slot of the card at <paramref name="indexInHand"/> while the card that was at <paramref name="heldIndex"/> is held
    /// (-1: none): the held card's slot stays empty, so the cards after it keep their places.
    /// </summary>
    public static int SlotIndexBesideHeldCard(int indexInHand, int heldIndex) =>
        heldIndex >= 0 && indexInHand >= heldIndex ? indexInHand + 1 : indexInHand;

    /// <summary>
    /// The permanent two-row reserve: the upper row takes <c>max(3, ceil(count / 2))</c> cards and fills first, so one to
    /// three cards sit high instead of starting low and jumping up when the fourth arrives (4 is 3+1, 5 is 3+2, 10 is 5+5).
    /// </summary>
    public static HandRowSplit SplitHandRows(int cardCount)
    {
        int clamped = Math.Max(0, cardCount);
        int back = Math.Min(clamped, Math.Max(3, (clamped + 1) / 2));
        return new HandRowSplit(back, clamped - back);
    }

    /// <summary>
    /// The hand's fit for this count and shape. The card scale is the shape's full size for every count and mode; when
    /// a row is too wide its cards overlap rather than shrink or leave the canvas. The row bands themselves stay where
    /// the shape's reserve puts them.
    /// </summary>
    public static HandRowFit ComputeHandRowFit(PortraitCombatBands bands, int count)
    {
        int clamped = Math.Max(0, count);
        HandRowSplit split = SplitHandRows(clamped);
        float scale = bands.Compact ? CompactHandCardScale : TallHandCardScale;
        float gap = bands.Compact ? CompactHandGap : TallHandGap;
        return new HandRowFit(split.BackRowCount, split.FrontRowCount, scale, gap,
            CardDefaultWidth * scale, CardDefaultHeight * scale);
    }

    /// <summary>A hand card's resting scale for this count and shape.</summary>
    public static float HandCardScaleFor(PortraitCombatBands bands, int count) =>
        ComputeHandRowFit(bands, count).Scale;

    /// <summary>A pressed card's scale: its own resting scale, a little larger, in place about its centre.</summary>
    public static float HeldCardScaleFor(PortraitCombatBands bands, int count) =>
        HandCardScaleFor(bands, count) * HeldCardGrowth;

    /// <summary>A hand card's resting scale from the canvas height alone (for touch code without bands).</summary>
    public static float HandCardScaleFor(float canvasHeight, int count) =>
        HandCardScaleFor(ComputeCombatBands(canvasHeight, 0f), count);

    /// <summary>A pressed card's scale from the canvas height alone.</summary>
    public static float HeldCardScaleFor(float canvasHeight, int count) =>
        HandCardScaleFor(canvasHeight, count) * HeldCardGrowth;

    /// <summary>
    /// A row's fixed centre above the holder container: the lower row keeps the hand's bottom margin, the upper row sits
    /// one resting card and the row gap above it. The centres use the shape's reserve-scale card, not the fitted card, so
    /// they are identical for every hand size; a shrunken tall card is centred inside its reserved band rather than
    /// sliding down as cards are drawn.
    /// </summary>
    private static float HandRowCenterLocalY(PortraitCombatBands bands, int rowIndex)
    {
        float cardHeight = CardDefaultHeight * (bands.Compact ? CompactHandCardScale : TallHandCardScale);
        float bottomMargin = bands.Compact ? CompactHandBottomMargin : TallHandBottomMargin;
        float rowGap = bands.Compact ? CompactHandRowGap : TallHandRowGap;
        float lowerCenter = -(bottomMargin + cardHeight * 0.5f);
        return rowIndex == 0 ? lowerCenter - (cardHeight + rowGap) : lowerCenter;
    }

    /// <summary>
    /// Half the width a hand card takes on the canvas, rotation and cost badge included: the card is centred on its holder,
    /// so a card at <c>540 + x</c> fits when <c>|x| + HandSlotHalfExtent</c> stays inside the canvas.
    /// </summary>
    public static float HandSlotHalfExtent(float scale, float angleDegrees)
    {
        float radians = MathF.Abs(angleDegrees) * MathF.PI / 180f;
        float rotated = CardDefaultWidth * 0.5f * MathF.Cos(radians) + CardDefaultHeight * 0.5f * MathF.Sin(radians);
        return scale * (rotated + CostBadgeOverhang);
    }

    /// <summary>The farthest a card's centre may sit from the canvas centre and still keep the edge margin on both sides.</summary>
    public static float MaxHandCenterOffset(float scale, float angleDegrees) =>
        Math.Max(0f, CanvasHalfWidth - HandEdgeMargin - HandSlotHalfExtent(scale, angleDegrees));

    /// <summary>
    /// The card's place in its row, as a local offset from the bottom-centre holder container: the upper row fills first,
    /// rows are centred, and the focused card only grows in place (same centre and row; an edge card is straightened as
    /// it grows, as the native hover does) by its own resting scale × <see cref="HeldCardGrowth"/>.
    /// Off (<paramref name="flat"/> false) fans every shape like the launcher's compact hand; on, it uses TouchSpireUI's
    /// flat rows at every shape. Both keep the full shape card size, fill the upper row first, and let cards overlap
    /// rather than leave the canvas; the focused card grows in place.
    /// </summary>
    public static PortraitCardSlot ComputeHandCardSlot(int count, int index, int focusedIndex, PortraitCombatBands bands,
        bool flat = false)
    {
        int clamped = Math.Max(0, count);
        HandRowFit fit = ComputeHandRowFit(bands, clamped);
        if (clamped == 0)
            return new PortraitCardSlot(0f, HandRowCenterLocalY(bands, 1), fit.Scale, 0f, 1);

        int safeIndex = Math.Clamp(index, 0, clamped - 1);
        bool inUpperRow = safeIndex < fit.UpperCount;
        int rowIndex = inUpperRow ? 0 : 1;
        int col = inUpperRow ? safeIndex : safeIndex - fit.UpperCount;
        int rowCount = inUpperRow ? fit.UpperCount : fit.LowerCount;
        float heldScale = fit.Scale * HeldCardGrowth;
        float rowY = HandRowCenterLocalY(bands, rowIndex);
        bool focused = focusedIndex == safeIndex;
        float scale = focused ? heldScale : fit.Scale;

        if (flat)
        {
            // A centred flat row. The step keeps the held edge card's cost badge inside the margin, so a crowded row
            // overlaps its neighbours instead of shrinking or running off the canvas.
            float limit = MaxHandCenterOffset(heldScale, 0f);
            float step = fit.CardWidth + fit.Gap;
            if (rowCount > 1)
                step = Math.Min(step, 2f * limit / (rowCount - 1));
            float x = (col - (rowCount - 1) * 0.5f) * step;
            return new PortraitCardSlot(x, rowY, scale, 0f, rowIndex);
        }

        // The launcher's fan, at every shape. The half-span uses the held card's margin so pressing an edge card grows
        // it in place without moving the row; the focused card is straightened as the native hover does.
        float t = rowCount <= 1 ? 0f : (col - (rowCount - 1) * 0.5f) / ((rowCount - 1) * 0.5f);
        float fanLimit = MaxHandCenterOffset(heldScale, HandRowAngle);
        float halfSpan = Math.Min((rowCount - 1) * 99f, fanLimit);
        float fanX = t * halfSpan;
        float fanAngle = focused ? 0f : t * HandRowAngle;
        return new PortraitCardSlot(Math.Clamp(fanX, -fanLimit, fanLimit), rowY, scale, fanAngle, rowIndex);
    }

    /// <summary>
    /// The hand-control rail: one row between <see cref="PortraitCombatBands.RailTopY"/> and
    /// <see cref="PortraitCombatBands.HandTopY"/> with the draw pile, energy (and the Regent star badge), End Turn, Ping,
    /// exhaust and discard, in the order a hand reads left to right. It depends only on the bands, so the rail never
    /// moves with the hand's size. Controls are centred on the rail (the End Turn and pile buttons pivot on their centre
    /// lines), End Turn keeps the canvas's centre, and every touch target is at least <c>PortraitBands.MinTarget</c>.
    /// </summary>
    public static PortraitHudLayout ComputeHudLayout(float canvasHeight, PortraitCombatBands bands)
    {
        float ch = Math.Max(canvasHeight, SafeAreaCanvas.MinPortraitHeight);
        float railTop = bands.RailTopY;
        float railCenter = railTop + RailHeightFor(bands.Compact) * 0.5f;

        // The energy counter keeps the same place for every character; the star is a small corner badge on top of it.
        const float energyX = EnergyRailX;
        const float energyScale = 1.12f;
        const float endTurnScale = 1.42f;
        const float endTurnShowX = SafeAreaCanvas.PortraitWidth * 0.5f - 110f;

        // TouchSpireUI keeps the star at its native scene size and simply places its global top-left from the orb:
        // orb centre + orb.Size * offset - star span * 0.5. Star centre is the same value plus half its span; the badge
        // size is the native 128 at its scene scale under the counter.
        float orbSize = EnergyOrbSize * energyScale;
        float starCenterX = energyX + EnergyOrbSize * 0.5f + orbSize * StarOrbOffsetX;
        float starCenterY = railCenter + orbSize * StarOrbOffsetY;
        float endTurnVisualLeft = endTurnShowX - 110f * (endTurnScale - 1f);
        float endTurnVisualRight = endTurnVisualLeft + 220f * endTurnScale;

        return new PortraitHudLayout(
            RailTopY: railTop,
            EnergyContainerX: energyX,
            EnergyContainerY: railCenter - 64f,
            EnergyScale: energyScale,
            StarCenterX: starCenterX,
            StarCenterY: starCenterY,
            ExhaustShowX: 806f,
            ExhaustShowY: railCenter - 40f,
            ExhaustHideX: -180f,
            ExhaustScale: 1.6f,
            EndTurnShowX: endTurnShowX,
            EndTurnShowY: railCenter - 45f,
            EndTurnHideX: 1130f,
            EndTurnScale: endTurnScale,
            DrawShowX: 41f,
            DrawShowY: railCenter - 40f,
            DrawHideX: -160f,
            DrawHideY: ch + 40f,
            DiscardShowX: 968f,
            DiscardShowY: railCenter - 40f,
            DiscardHideX: 1160f,
            DiscardHideY: ch + 40f,
            // Ping is multiplayer's small button: it floats over the floor between the compact feet line and the rail,
            // centred between End Turn and the exhaust pile, so it never covers a rail control or a creature.
            PingShowX: (endTurnVisualRight + 782f) * 0.5f - 52.5f,
            PingShowY: railTop - 63.5f,
            PileScale: 1.6f);
    }

    /// <summary>The native Confirm is 200 by 110; 1.2 makes it 240 by 132, over the 44-point side a finger needs.</summary>
    public const float SelectionConfirmScale = 1.2f;

    /// <summary>The gap kept between the selection previews and the rail's top or the prompt above them.</summary>
    public const float SelectionPreviewGap = 12f;

    /// <summary>
    /// Where the native selection UI goes in portrait: the Confirm takes the rail's right end, over the inactive discard
    /// pile (and the exhaust pile beside it) while the selection's backstop is up, and the upgrade preview shrinks to fit
    /// between the prompt and the rail, so neither is covered by the hand rows.
    /// </summary>
    public static PortraitSelectionLayout ComputeSelectionLayout(PortraitCombatBands bands, float promptBottomY)
    {
        float railCenter = (bands.RailTopY + bands.HandTopY) * 0.5f;
        float previewBottom = bands.RailTopY - SelectionPreviewGap;
        float topFloor = bands.StageTopY + 8f;
        float previewTop = promptBottomY > 0f ? Math.Max(topFloor, promptBottomY + 8f) : topFloor;
        if (previewTop > previewBottom - 1f)
            previewTop = Math.Max(topFloor, previewBottom - 1f);

        float available = Math.Max(1f, previewBottom - previewTop);
        float previewScale = Math.Clamp(available / CardDefaultHeight, 0.55f, 0.9f);
        float previewCenter = previewTop + CardDefaultHeight * previewScale * 0.5f;

        // Right-aligned on the rail with the canvas's hand edge margin: at 1.2 the 200-wide Confirm becomes 240 wide, so
        // its centre is 120 units in from the right edge.
        float confirmCenterX = SafeAreaCanvas.PortraitWidth - HandEdgeMargin - 100f * SelectionConfirmScale;

        return new PortraitSelectionLayout(
            ConfirmCenterX: confirmCenterX,
            ConfirmCenterY: railCenter,
            ConfirmScale: SelectionConfirmScale,
            PreviewTopY: previewTop,
            PreviewCenterY: previewCenter,
            PreviewScale: previewScale);
    }

    /// <summary>
    /// The native selected-card previews (Purity, Burning Pact, the whole-hand choice) in the band between the prompt and
    /// the rail, ported from TouchSpireUI's selected-row fit: up to five-in-a-row, wrapped to two rows only when that
    /// gives a materially larger card. Positions are in the container's own space; the container's scale is the returned
    /// <see cref="PortraitSelectedCardsLayout.Scale"/>, and rows are centred on the band.
    /// </summary>
    public static PortraitSelectedCardsLayout ComputeSelectedCardsLayout(
        int count,
        PortraitCombatBands bands,
        float promptBottomY,
        float sideInset = 16f)
    {
        int cards = Math.Max(1, count);
        float topFloor = bands.StageTopY + 8f;
        float top = promptBottomY > 0f ? Math.Max(topFloor, promptBottomY + 8f) : topFloor;
        float bottom = bands.RailTopY - SelectionPreviewGap;
        if (top > bottom - 1f)
            top = Math.Max(topFloor, bottom - 1f);

        float maxHeight = Math.Max(1f, bottom - top);
        float maxWidth = Math.Max(1f, SafeAreaCanvas.PortraitWidth - 2f * sideInset);
        float cardWidth = CardDefaultWidth * SelectionCardSmallScale;
        float cardHeight = CardDefaultHeight * SelectionCardSmallScale;
        float stepX = cardWidth + SelectionCardGap;
        float stepY = cardHeight + SelectionCardGap;
        float centerY = (top + bottom) * 0.5f;

        PortraitSelectedCardsLayout Fit(int rows, int columns)
        {
            float width = cardWidth + stepX * (columns - 1);
            float height = cardHeight + stepY * (rows - 1);
            float scale = Math.Min(SelectionCardMaxScale, Math.Min(maxWidth / width, maxHeight / height));
            return new PortraitSelectedCardsLayout(rows, columns, stepX, stepY, cardWidth, cardHeight,
                Math.Max(0.2f, scale), centerY);
        }

        PortraitSelectedCardsLayout singleRow = Fit(1, cards);
        if (cards < 5)
            return singleRow;
        PortraitSelectedCardsLayout doubleRow = Fit(2, (cards + 1) / 2);
        return doubleRow.Scale > singleRow.Scale * 1.05f ? doubleRow : singleRow;
    }

    /// <summary>
    /// What a fight's enemy stage freezes when the room opens: how many slots the encounter reserves, whether one boss
    /// dominates it, whether the enemies are a Decimillipede chain, and whether the canvas is the short (9:16) one.
    /// </summary>
    public readonly record struct PortraitStageShape(int Capacity, bool Compact, bool Dominant, bool Segmented);

    /// <summary>An encounter marker's name, where the game put it, and the area of the enemy standing in it (0 when empty).</summary>
    public readonly record struct PortraitSlotMarker(string Name, float X, float Y, float Area);

    /// <summary>One creature's native size against the frame it has to fit, and the placement's own scale for it.</summary>
    public readonly record struct PortraitFrameFit(
        float NativeWidth,
        float NativeHeight,
        float MaxWidth,
        float MaxHeight,
        float SlotScale);

    /// <summary>The room's shared body scale never grows past this: the placement's own slot sizes are the largest.</summary>
    public const float PreferredPeerScale = 1f;

    /// <summary>
    /// The smallest the room's shared body scale goes, so a room of large monsters does not turn every peer into a
    /// miniature. A body too large even at the floor still fits itself inside its own slot.
    /// </summary>
    public const float PeerScaleFloor = 0.62f;

    /// <summary>The smallest a creature body is drawn, so its silhouette stays readable while its fixed-size labels carry the text.</summary>
    public const float MinBodyScale = 0.25f;

    /// <summary>The largest a creature body is drawn; a lone enemy already uses this for the stage.</summary>
    public const float MaxBodyScale = 1.32f;

    /// <summary>Preferred shared body scale for an ordinary encounter, re-derived for the 1080 canvas (TouchSpireUI's 2.05).</summary>
    public const float PreferredSharedBodyScale = 1.32f;

    /// <summary>
    /// TouchSpireUI's per-encounter body multipliers, applied to the preferred shared scale. The Kaiser Crab is excluded
    /// (the launcher's own arm fit owns it).
    /// </summary>
    public static float EncounterBodyMultiplier(string encounterKey)
    {
        if (encounterKey.Contains("CeremonialBeast", StringComparison.OrdinalIgnoreCase)) return 0.83f;
        if (encounterKey.Contains("WaterfallGiant", StringComparison.OrdinalIgnoreCase)) return 0.78f;
        if (encounterKey.Contains("KnowledgeDemon", StringComparison.OrdinalIgnoreCase)) return 0.73f;
        if (encounterKey.Contains("Kin", StringComparison.Ordinal)) return 0.66f;
        return 1f;
    }

    /// <summary>
    /// The one scale every creature body in the room shares: the smallest fit any of them needs in its slot, so a large
    /// monster makes its peers smaller instead of only itself, floored at <see cref="PeerScaleFloor"/> so the room stays
    /// legible. <paramref name="floorApplied"/> says that the floor, not a frame, set it.
    /// </summary>
    public static float SharedPeerScale(ReadOnlySpan<PortraitFrameFit> fits, out bool floorApplied)
    {
        float peer = PreferredPeerScale;
        foreach (PortraitFrameFit fit in fits)
        {
            if (fit.SlotScale <= 0.01f || fit.NativeWidth <= 1f || fit.NativeHeight <= 1f)
                continue;
            float byWidth = fit.MaxWidth > 1f ? fit.MaxWidth / fit.NativeWidth : float.MaxValue;
            float byHeight = fit.MaxHeight > 1f ? fit.MaxHeight / fit.NativeHeight : float.MaxValue;
            peer = Math.Min(peer, Math.Min(byWidth, byHeight) / fit.SlotScale);
        }
        floorApplied = peer < PeerScaleFloor;
        return Math.Clamp(peer, PeerScaleFloor, PreferredPeerScale);
    }

    /// <summary>
    /// The encounter's slots in the order the stage lays them out, frozen before any summon. Normally left to right, so
    /// the game's desktop placement maps across the stage. A dominant boss takes the first (widest) slot whatever its
    /// marker's X, and the rest follow left to right; an encounter's empty slots keep their places, so the first egg of an
    /// Ovicopter already owns the last slot before it exists and nothing moves when it appears.
    /// </summary>
    public static string[] OrderEncounterSlotNames(
        System.Collections.Generic.IReadOnlyList<PortraitSlotMarker> markers,
        bool dominant)
    {
        int bossIndex = -1;
        if (dominant)
        {
            for (int i = 0; i < markers.Count; i++)
            {
                if (markers[i].Area <= 0f) continue;
                if (bossIndex < 0 || markers[i].Area > markers[bossIndex].Area)
                    bossIndex = i;
            }
        }
        var rest = new System.Collections.Generic.List<PortraitSlotMarker>(markers.Count);
        for (int i = 0; i < markers.Count; i++)
        {
            if (i != bossIndex)
                rest.Add(markers[i]);
        }
        rest.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));

        var names = new string[markers.Count];
        int next = 0;
        if (bossIndex >= 0)
            names[next++] = markers[bossIndex].Name;
        foreach (PortraitSlotMarker marker in rest)
            names[next++] = marker.Name;
        return names;
    }

    /// <summary>
    /// The stage index of an encounter slot, given the fight's frozen slot names: a slot that is known keeps its index,
    /// and a new one (a summon the encounter did not reserve) is appended after every existing slot, so no placed
    /// creature moves. Its index past the frozen capacity marks it as an overflow spawn.
    /// </summary>
    public static int EnsureSlotIndex(System.Collections.Generic.List<string> slotNames, string slotKey)
    {
        int existing = slotNames.IndexOf(slotKey);
        if (existing >= 0)
            return existing;
        slotNames.Add(slotKey);
        return slotNames.Count - 1;
    }

    /// <summary>Where an enemy past the frozen slot table stands: a reserved lane on the ground, clear of the peers.</summary>
    public static PortraitCreatureSlot OverflowEnemySlot(int index, int capacity, PortraitCombatBands bands)
    {
        int lanes = Math.Max(1, capacity);
        float lane = 0.635f / lanes;
        float centre = 0.35f + lane * (Math.Max(0, index) % lanes + 0.5f);
        return new PortraitCreatureSlot(SafeAreaCanvas.PortraitWidth * centre, bands.FrontFeetY, 0.70f, 1);
    }

    /// <summary>
    /// Which enemy slot, if any, is the room's boss. TouchSpireUI's rule: not a segment chain, more than two slots, and the
    /// largest enemy is more than 1.8 times the next largest (a single enemy counts). Identical monster types never yield a
    /// boss, so four equal Exoskeletons share one row scale instead of one of them being promoted by a tie.
    /// </summary>
    public static int PickBossIndex(
        System.Collections.Generic.IReadOnlyList<(int Slot, float Area, string Type)> enemies,
        int capacity,
        bool segmented)
    {
        if (segmented || capacity <= 2 || enemies.Count == 0)
            return -1;
        int best = -1;
        float largest = 0f, second = 0f;
        string bestType = "";
        for (int i = 0; i < enemies.Count; i++)
        {
            if (enemies[i].Area > largest)
            {
                second = largest;
                largest = enemies[i].Area;
                best = enemies[i].Slot;
                bestType = enemies[i].Type;
            }
            else if (enemies[i].Area > second)
            {
                second = enemies[i].Area;
            }
        }
        if (best < 0)
            return -1;
        int sameType = 0;
        foreach ((int _, float _, string type) in enemies)
        {
            if (type == bestType)
                sameType++;
        }
        if (sameType > 1)
            return -1;
        return enemies.Count == 1 || largest > second * 1.8f ? best : -1;
    }

    /// <summary>One creature's cell in the crowded single-row stage: centre, body width cap, height cap and bar width cap.</summary>
    public readonly record struct PortraitStageCells(float[] X, float[] Widths, float[] Heights, float[] Bars);

    /// <summary>
    /// The single-row stage used when the short canvas cannot stack two ranks without them meeting: every slot gets a cell
    /// across <paramref name="leftEdge"/>..1072, and the boss's cell is two and a half times a peer's, so the boss stays
    /// dominant. Bodies, bars and single intent rows fit their own cell, so no two creatures touch. Frozen with the slot
    /// table: the same cells are used however many of the reserved slots are filled later.
    /// </summary>
    public static PortraitStageCells ComputeCrowdedCells(int capacity, int bossSlot, float leftEdge, PortraitCombatBands bands)
    {
        int count = Math.Max(0, capacity);
        var cells = new PortraitStageCells(new float[count], new float[count], new float[count], new float[count]);
        if (count == 0)
            return cells;

        float right = SafeAreaCanvas.PortraitWidth - 8f;
        bool boss = bossSlot >= 0 && bossSlot < count;
        const float bossWeight = 2.5f;
        float total = (count - (boss ? 1 : 0)) + (boss ? bossWeight : 0f);
        float unit = Math.Max(40f, (right - Math.Max(0f, leftEdge)) / Math.Max(1f, total));
        float x = Math.Max(0f, leftEdge);
        for (int i = 0; i < count; i++)
        {
            float width = boss && i == bossSlot ? unit * bossWeight : unit;
            cells.X[i] = x + width * 0.5f;
            cells.Widths[i] = Math.Max(30f, width - 10f);
            cells.Heights[i] = Math.Max(120f, bands.FrontFeetY - bands.StageTopY);
            cells.Bars[i] = Math.Max(80f, Math.Min(width - 16f, 420f));
            x += width;
        }
        return cells;
    }

    /// <summary>
    /// TouchSpireUI's tall (9:19.5 and taller) enemy formation, converted to the 1080 canvas and re-based on the launcher's
    /// floor line. Depths are TouchSpireUI units ×0.642857: a negative depth stands in front of the floor (below it on
    /// screen), a positive one behind it. The Ovicopter back rank, the Crab and the segment chain have their own layouts.
    /// </summary>
    public static PortraitCreatureSlot[] ComputeTallFormation(int capacity, PortraitCombatBands bands)
    {
        int count = Math.Max(0, capacity);
        var slots = new PortraitCreatureSlot[count];
        if (count == 0)
            return slots;
        float floor = bands.FrontFeetY;
        const float S = 1080f / 1680f;
        PortraitCreatureSlot Slot(float fraction, float depthTsu) =>
            new(SafeAreaCanvas.PortraitWidth * fraction, floor - depthTsu * S, 0.94f, depthTsu > 0f ? 0 : 1);

        if (count == 1)
        {
            slots[0] = Slot(0.675f, 25f);
            return slots;
        }
        if (count == 2)
        {
            slots[0] = Slot(0.475f, 100f);
            slots[1] = Slot(0.805f, 20f);
            return slots;
        }
        if (count == 3)
        {
            slots[0] = Slot(0.46f, -125f);
            slots[1] = Slot(0.655f, 510f);
            slots[2] = Slot(0.85f, -75f);
            return slots;
        }
        if (count == 5)
            slots[0] = Slot(0.18f, 510f);
        int o = count == 5 ? 1 : 0;
        slots[o] = Slot(0.49f, -125f);
        slots[o + 1] = Slot(0.49f, 480f);
        slots[o + 2] = Slot(0.81f, -125f);
        slots[o + 3] = Slot(0.81f, 510f);
        for (int i = o + 4; i < count; i++)
            slots[i] = Slot(0.50f + (i % 2) * 0.30f, 1030f);
        return slots;
    }

    /// <summary>
    /// The six-body tall formation (TwoTailedRats with its full summon roster): three columns between the permitted enemy
    /// left and the right margin, a front and a back rat sharing each column. Ranks are vertically disjoint, so sharing a
    /// column cannot make bodies, bars or intents touch. Positions are frozen at the first layout with the real enemy left.
    /// </summary>
    public static PortraitCreatureSlot[] ComputeSixFormation(float leftEdge, PortraitCombatBands bands)
    {
        var slots = new PortraitCreatureSlot[6];
        float floor = bands.FrontFeetY;
        float right = SafeAreaCanvas.PortraitWidth - 14f;
        float left = Math.Max(8f, leftEdge);
        float pitch = Math.Max(120f, (right - left) / 3f);
        for (int column = 0; column < 3; column++)
        {
            float x = left + (column + 0.5f) * pitch;
            slots[column] = new PortraitCreatureSlot(x, floor, 0.94f, 1);
            slots[3 + column] = new PortraitCreatureSlot(x, floor - 510f * (SafeAreaCanvas.PortraitWidth / 1680f), 0.94f, 0);
        }
        return slots;
    }

    /// <summary>
    /// A tall-formation slot's frame: width capped by the nearest column pitch (so bodies and bars cannot touch), height by
    /// TouchSpireUI's 220–475 converted to the launcher canvas.
    /// </summary>
    public static (float MaxWidth, float MaxHeight, float MinLeft, float MaxRight) TallFormationFrame(
        PortraitCreatureSlot[] formation,
        int index,
        PortraitCombatBands bands)
    {
        if (formation.Length == 0 || index < 0 || index >= formation.Length)
            return (225f, 305f, 8f, SafeAreaCanvas.PortraitWidth - 8f);
        PortraitCreatureSlot slot = formation[index];
        // Same-column ranks (front/back pairs share an x) are separated vertically, not horizontally, so they do not set
        // the horizontal pitch; a lone slot may use the whole width.
        float pitch = formation.Length == 1 ? 1000f : 400f;
        for (int j = 0; j < formation.Length; j++)
        {
            if (j == index || formation[j].RowDepth != slot.RowDepth) continue;
            float dx = MathF.Abs(formation[j].CanvasX - slot.CanvasX);
            if (dx > 1f)
                pitch = Math.Min(pitch, dx);
        }
        float baseWidth = formation.Length == 1 ? SafeAreaCanvas.PortraitWidth * 0.58f
            : formation.Length == 2 ? SafeAreaCanvas.PortraitWidth * 0.31f
            : SafeAreaCanvas.PortraitWidth * 0.29f;
        float width = Math.Max(90f, Math.Min(baseWidth, pitch - 10f));
        // TouchSpireUI's 475 clamp belongs to the 3–5 table; 1–2 use the whole stage from the top to their feet.
        float height = formation.Length <= 2
            ? Math.Max(120f, slot.CanvasFeetY - bands.StageTopY)
            : Math.Clamp(475f * (SafeAreaCanvas.PortraitWidth / 1680f), 141f, 305f);
        height = Math.Min(height, Math.Max(60f, slot.CanvasFeetY - bands.StageTopY));
        float half = Math.Max(width, pitch) * 0.5f;
        return (width, height, Math.Max(8f, slot.CanvasX - half),
            Math.Min(SafeAreaCanvas.PortraitWidth - 8f, slot.CanvasX + half));
    }

    public static PortraitCreatureSlot[] ComputeAllyStageSlots(
        int playerCount,
        int petCount,
        bool hasOrbs,
        PortraitCombatBands bands,
        bool spacious = false)
    {
        int pCount = Math.Max(0, playerCount);
        int pets = Math.Max(0, petCount);
        var slots = new PortraitCreatureSlot[pCount + pets];
        if (slots.Length == 0)
            return slots;

        float frontFeetY = bands.FrontFeetY;
        float upperFeetY = Math.Max(bands.StageTopY + 320f, frontFeetY - 210f);

        if (pCount == 1 && pets == 0)
        {
            // A little smaller and nearer the left edge, so the enemies can stand a fifth of the width
            // away (EnemyStageLeft) and still be large. The Fool's bow ends about 115 units in, left of the player.
            // The feet sit on the front foot line; orbs hang a little below it, so those fights raise the hero by 8.
            float feetY = frontFeetY - (hasOrbs ? 8f : 0f);
            // The orb arc reaches well left of the hero; those fights stand a little further from the edge.
            float x = LonePlayerX + (hasOrbs ? 40f : 0f);
            float baseScale = spacious ? 0.95f : (bands.Compact ? 0.75f : 0.80f);
            slots[0] = new PortraitCreatureSlot(x, feetY, baseScale, 1);
            return slots;
        }

        if (pCount == 1 && pets >= 1)
        {
            // Stack hero upper-left and pets (such as Osty) below-right inside the left ally column (X <= 395). The hero is
            // the back rank; the pet stands on the front foot line.
            slots[0] = new PortraitCreatureSlot(162f, upperFeetY, 0.78f, 0);
            for (int i = 0; i < pets; i++)
            {
                float petX = i == 0 ? 334f : Math.Max(130f, 334f - i * 165f);
                float petY = frontFeetY + (i > 1 ? -110f : 0f);
                slots[1 + i] = new PortraitCreatureSlot(petX, petY, i == 0 ? 0.76f : 0.70f, 1);
            }
            return slots;
        }

        // Co-op multi-player + optional pets in 2-column left stagger.
        int total = slots.Length;
        int backCount = total / 2;
        int frontCount = total - backCount;
        for (int i = 0; i < total; i++)
        {
            bool back = i < backCount;
            int col = back ? i : i - backCount;
            int rowLen = back ? backCount : frontCount;
            float t = rowLen <= 1 ? 0.5f : (float)col / (rowLen - 1);
            float minX = back ? 145f : 165f;
            float maxX = back ? 335f : 355f;
            float x = rowLen <= 1 ? (minX + maxX) * 0.5f : Lerp(minX, maxX, t);
            float y = back ? upperFeetY : frontFeetY;
            slots[i] = new PortraitCreatureSlot(x, y, 0.72f, back ? 0 : 1);
        }

        return slots;
    }

    public static PortraitCreatureSlot[] ComputeEnemyStageSlots(PortraitStageShape shape, PortraitCombatBands bands)
    {
        int capacity = Math.Max(0, shape.Capacity);
        var slots = new PortraitCreatureSlot[capacity];
        if (capacity == 0)
            return slots;

        float frontFeetY = bands.FrontFeetY;
        float backFeetY = Math.Max(bands.StageTopY + 60f, frontFeetY - 250f);

        if (shape.Dominant && capacity > 2)
        {
            // One large enemy takes the widest upper frame, its feet on the same ground line as its summons; the smaller
            // ones fill the lanes beside it. On the short canvas a tall boss's head may reach up over the relic band.
            slots[0] = new PortraitCreatureSlot(756f, frontFeetY, 0.94f, 0);
            float lane = 0.635f / (capacity - 1);
            for (int i = 1; i < capacity; i++)
            {
                float centre = 0.35f + lane * (i - 1 + 0.5f);
                slots[i] = new PortraitCreatureSlot(SafeAreaCanvas.PortraitWidth * centre, frontFeetY, 0.76f, 1);
            }
            return slots;
        }

        if (shape.Segmented && capacity > 0)
        {
            // A chain of segments is one creature: every part stands on the front foot line at one size, in the order the
            // encounter gave them, so a reattached part keeps its place in the line. The span keeps the parts clear of the
            // player on the left and inside the canvas on the right; the stage widens it to fit its actual bodies.
            const float chainLeft = 470f;
            const float chainRight = 950f;
            for (int i = 0; i < capacity; i++)
            {
                float x = capacity == 1 ? (chainLeft + chainRight) * 0.5f : Lerp(chainLeft, chainRight, (float)i / (capacity - 1));
                slots[i] = new PortraitCreatureSlot(x, frontFeetY, 0.76f, 1);
            }
            return slots;
        }

        if (capacity == 1)
        {
            slots[0] = new PortraitCreatureSlot(755f, frontFeetY, 1.10f, 1);
            return slots;
        }

        if (capacity == 2)
        {
            slots[0] = new PortraitCreatureSlot(625f, frontFeetY, shape.Compact ? 0.82f : 0.88f, 1);
            slots[1] = new PortraitCreatureSlot(908f, frontFeetY - 118f, shape.Compact ? 0.78f : 0.84f, 0);
            return slots;
        }

        if (capacity == 3)
        {
            // Authored slots run left to right; the middle one stands one rank back so the group reads as a formation.
            slots[0] = new PortraitCreatureSlot(555f, frontFeetY, 0.76f, 1);
            slots[1] = new PortraitCreatureSlot(755f, backFeetY + 25f, 0.73f, 0);
            slots[2] = new PortraitCreatureSlot(945f, frontFeetY, 0.76f, 1);
            return slots;
        }

        // 4+ enemies: two depth ranks whose columns are ordered left to right, so an encounter's authored slot order is also
        // the order the players see. The ranks share the same columns rather than putting one rank completely behind another.
        int backCount = capacity / 2;
        int frontCount = capacity - backCount;
        var columns = new System.Collections.Generic.List<PortraitCreatureSlot>(capacity);
        for (int i = 0; i < backCount; i++)
        {
            float x;
            if (capacity == 4)
                x = i == 0 ? 655f : 940f;
            else if (capacity == 5)
                x = i == 0 ? 635f : 855f;
            else
            {
                float t = backCount <= 1 ? 0.5f : (float)i / (backCount - 1);
                x = Lerp(560f, 950f, t);
            }
            columns.Add(new PortraitCreatureSlot(x, backFeetY, capacity >= 5 ? 0.76f : 0.72f, 0));
        }
        for (int j = 0; j < frontCount; j++)
        {
            float x;
            if (capacity == 4)
                x = j == 0 ? 540f : 825f;
            else if (capacity == 5)
                x = 525f + j * 220f;
            else
            {
                float t = frontCount <= 1 ? 0.5f : (float)j / (frontCount - 1);
                x = Lerp(515f, 975f, t);
            }
            columns.Add(new PortraitCreatureSlot(x, frontFeetY, capacity >= 5 ? 0.82f : 0.76f, 1));
        }
        columns.Sort((a, b) => a.CanvasX.CompareTo(b.CanvasX));
        for (int i = 0; i < slots.Length && i < columns.Count; i++)
            slots[i] = columns[i];

        return slots;
    }

    /// <summary>Where a lone player stands (its feet), in canvas units from the left edge.</summary>
    public const float LonePlayerX = 180f;

    /// <summary>The right end of the enemies' stage: their hit boxes end here.</summary>
    public const float EnemyStageRight = 1072f;

    /// <summary>
    /// In portrait the enemy stood much closer to the player than on a desktop. This is the gap kept between
    /// a lone player's hit box and the nearest enemy's, a fifth of the width. With pets or other players on the left,
    /// whose front stands nearer the enemies, half of it from the nearest of them.
    /// </summary>
    public const float PlayerEnemyGap = SafeAreaCanvas.PortraitWidth * 0.2f;

    /// <summary>
    /// Where the enemies' hit boxes may start: a full gap right of the player's hit box, and half a gap right of every
    /// other ally's (pets and other players), never left of <paramref name="minimum"/>. Whole units, so the layout holds
    /// still from frame to frame.
    /// </summary>
    public static float EnemyStageLeft(float playersHitRight, float alliesHitRight, float minimum) =>
        EnemyStageLeft(playersHitRight, alliesHitRight, minimum, PlayerEnemyGap);

    /// <summary>
    /// The same rule with an explicit gap: one- and two-enemy fights use a smaller gap so the bodies can be larger.
    /// </summary>
    public static float EnemyStageLeft(float playersHitRight, float alliesHitRight, float minimum, float gap) =>
        MathF.Round(Math.Max(minimum, Math.Max(playersHitRight + gap, alliesHitRight + gap * 0.5f)));

    /// <summary>
    /// How much a group of enemies is drawn smaller and nearer the right edge (<see cref="SqueezeEnemySlot"/>) so the
    /// leftmost hit box starts at <paramref name="enemyLeft"/>: 1 when it already does.
    /// </summary>
    public static float EnemyGroupSqueeze(float leftmostHitLeft, float enemyLeft)
    {
        if (leftmostHitLeft >= enemyLeft || EnemyStageRight - leftmostHitLeft < 1f)
            return 1f;
        return Math.Clamp((EnemyStageRight - enemyLeft) / (EnemyStageRight - leftmostHitLeft), 0.5f, 1f);
    }

    /// <summary>
    /// An enemy's slot drawn <paramref name="k"/> times as large and <paramref name="k"/> times as far from the stage's
    /// right end: with its size limits scaled by the same factor, every hit box edge moves to that end by
    /// <paramref name="k"/>, so the group keeps its arrangement.
    /// </summary>
    public static PortraitCreatureSlot SqueezeEnemySlot(PortraitCreatureSlot slot, float k) =>
        k >= 1f ? slot : slot with { CanvasX = EnemyStageRight - (EnemyStageRight - slot.CanvasX) * k, BaseScale = slot.BaseScale * k };

    /// <summary>
    /// The scale a creature body is drawn at: its slot's own size times the room's shared peer scale, then squeezed to fit
    /// its frame when it is still too large, never below <see cref="MinBodyScale"/> or above <see cref="MaxBodyScale"/>.
    /// </summary>
    public static float FitCreatureScale(
        float baseScale,
        float peerScale,
        float boundsWidth,
        float boundsHeight,
        float maxAllowedWidth,
        float maxAllowedHeight)
    {
        float scale = baseScale * Math.Clamp(peerScale, 0.05f, PreferredPeerScale);
        if (boundsWidth > 1f && maxAllowedWidth > 1f)
            scale = Math.Min(scale, maxAllowedWidth / boundsWidth);
        if (boundsHeight > 1f && maxAllowedHeight > 1f)
            scale = Math.Min(scale, maxAllowedHeight / boundsHeight);
        return Math.Clamp(scale, MinBodyScale, MaxBodyScale);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
