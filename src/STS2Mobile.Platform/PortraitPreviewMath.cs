using System;

namespace STS2Mobile.Platform;

/// <summary>A before/after comparison pair's scale and the canvas point its origin (the middle between the cards) takes.</summary>
public readonly record struct PreviewPairFit(float Scale, float CenterX, float CenterY);

/// <summary>A row of preview cards' scale, the width its container takes in its own units, and the canvas middle it takes.</summary>
public readonly record struct PreviewRowFit(float Scale, float LocalWidth, float CenterY);

/// <summary>
/// Pure layout math for the deck's card selection screens in portrait: the upgrade/enchant comparison pair, the multi-card
/// preview rows (upgrade, enchant and the generic deck select), the prompts and the enchant legend under them, the enlarged
/// Confirm/Cancel buttons, and the transform preview's composition fit.
/// <para>The reference is the portrait design on the game's 1680-wide canvas. Lengths chosen on that canvas (margins, prompt
/// tops, button offsets) convert by 1080/1680; lengths that come from the game's own scenes (card 300 x 422, pair spans, row
/// pitch) stay as they are, and a scale factor that draws a scene control converts by the same 1080/1680 so a card is the same
/// size in points. All values are canvas units on the launcher's 1080-wide portrait canvas, where 127 units are 44 points.</para>
/// </summary>
public static class PortraitPreviewMath
{
    /// <summary>The portrait design's reference canvas width, in the units its margins and offsets were chosen in.</summary>
    public const float ReferenceWidth = 1680f;

    /// <summary>The launcher's portrait canvas width.</summary>
    public const float CanvasWidth = 1080f;

    /// <summary>Reference units to launcher canvas units: a design length or scale is drawn at 1080/1680 of it.</summary>
    public const float Unit = CanvasWidth / ReferenceWidth;

    /// <summary>The smallest side a touch target should have (44 points on the 1080 canvas).</summary>
    public const float TouchTarget = 127f;

    // The upgrade/enchant comparison pair: two 300-wide cards at +/- 235 around the pair's origin, with arrows between, and
    // its whole width counted as 790 for the fit; at most 1.98 on the reference canvas.
    public const float PairCardOffset = 235f;
    public const float PairSpan = 790f;
    public const float PairSideMargin = 112f * Unit;
    public const float PairMaxScale = 1.98f * Unit;
    public const float PairArrowScale = 0.8f;

    // A preview row: 300-wide cards 325 apart, the fit counting 315 for the first card plus 325 for each after it, inside the
    // canvas less 110 on the reference (70.7 here), at most 2.02 (1.2986 here).
    public const float RowCardWidth = 300f;
    public const float RowPitch = 325f;
    public const float RowNeed = 315f;
    public const float RowMargin = 110f * Unit;
    public const float RowMaxScale = 2.02f * Unit;
    public const float RowSeparation = 325f;
    // The game lays the row out from its origin; the last card has to fit inside the container's own width.
    public const float RowLocalReserve = 314f;

    // The prompt under a grid: 2.2 times its size at most, less to keep 100 reference units of margin each side; the enchant
    // legend and its prompt use 2.08 and 2.12 with 140 and 120 reference units of margin.
    public const float PromptSideMargin = 100f * Unit;
    public const float PromptMaxScale = 2.2f * Unit;
    public const float FooterPromptMargin = 120f * Unit;
    public const float FooterPromptMaxScale = 2.12f * Unit;
    public const float LegendScale = 2.08f * Unit;
    public const float LegendSideMargin = 140f * Unit;
    public const float LegendX = 70f * Unit;
    public const float PromptLegendGap = 20f * Unit;

    // Prompt tops and the legend's base, counted up from the canvas's bottom edge (the launcher has no bottom safe inset).
    public const float UpgradePromptFromBottom = 648f * Unit;
    public const float TransformPromptFromBottom = 414f * Unit;
    public const float SelectPromptFromBottom = 400f * Unit;
    public const float LegendFromBottom = 265f * Unit;
    // The grid ends this far above the prompt it belongs to.
    public const float GridGap = 28f * Unit;

    // The comparison's own Confirm and Cancel: 2.05 and 2.1 times their scene size on the reference canvas, at the right and
    // left edges 18 reference units in, their top 250 above the canvas bottom. The upgrade screen's Close stands higher, above
    // its View Upgrades box (470).
    public const float ConfirmScale = 2.05f * Unit;
    public const float CancelScale = 2.1f * Unit;
    public const float ButtonRightMargin = 18f * Unit;
    public const float ButtonTopFromBottom = 250f * Unit;
    public const float CloseAboveToggle = 470f * Unit;

    // The transform preview's composition is fitted inside the canvas less 56 reference units either side, less 40 above and
    // below and 250 at the bottom for the buttons, between 0.1 and 1.98 of its size.
    public const float TransformSideMargin = 56f * Unit;
    public const float TransformBottomMargin = 40f * Unit;
    public const float TransformBottomReserve = 250f * Unit;
    public const float TransformMinScale = 0.1f * Unit;
    public const float TransformMaxScale = 1.98f * Unit;

    // Where the comparison sits down the canvas, as a fraction of the room between the notch cap and the bottom edge.
    public const float UpgradeCenterFraction = 0.48f;
    public const float EnchantCenterFraction = 0.52f;
    public const float RowCenterFraction = 0.5f;

    /// <summary>The upgrade/enchant pair's scale: as large as fits the canvas width, up to 1.98 of the reference design.</summary>
    public static float PairScale(float canvasWidth) =>
        MathF.Min(PairMaxScale, (canvasWidth - PairSideMargin) / PairSpan);

    /// <summary>
    /// A preview row's scale for <paramref name="count"/> cards. The same fit for the multi-card upgrade/enchant previews and
    /// the generic deck select's preview row; the cards stay the same size and the row gets narrower as the count grows.
    /// </summary>
    public static float RowScale(float canvasWidth, int count) =>
        MathF.Min(RowMaxScale, (canvasWidth - RowMargin) / (RowNeed + RowPitch * Math.Max(0, count - 1)));

    /// <summary>
    /// The left edge of a row's container so that its middle is the canvas's middle: the game's row centres its cards in the
    /// container, so a row drawn from this edge is centred whatever the count.
    /// </summary>
    public static float RowLeft(float canvasWidth, int count) =>
        (canvasWidth - RowLocalWidth(canvasWidth, count) * RowScale(canvasWidth, count)) * 0.5f;

    /// <summary>The container width that holds every card's centre, in the container's own units (before its scale).</summary>
    public static float RowLocalWidth(float canvasWidth, int count)
    {
        float scale = MathF.Max(RowScale(canvasWidth, count), 0.01f);
        return (canvasWidth - RowMargin) / scale - RowLocalReserve;
    }

    /// <summary>The whole row's fit, for the host to apply in one step.</summary>
    public static PreviewRowFit PreviewRow(float canvasWidth, int count, float centerY) =>
        new(RowScale(canvasWidth, count), RowLocalWidth(canvasWidth, count), centerY);

    /// <summary>The whole pair's fit; its origin is the middle between the two cards.</summary>
    public static PreviewPairFit PreviewPair(float canvasWidth, float centerY) =>
        new(PairScale(canvasWidth), canvasWidth * 0.5f, centerY);

    /// <summary>
    /// A prompt's scale under a grid: its own size times as much as 2.2, less when that would take it past 100 reference units
    /// of margin at either edge.
    /// </summary>
    public static float PromptScale(float canvasWidth, float promptWidth) =>
        promptWidth <= 0.5f ? 1f : MathF.Min(PromptMaxScale, (canvasWidth - PromptSideMargin) / promptWidth);

    /// <summary>The enchant screen's prompt, packed under its legend: 2.12 at most, 120 reference units of margin each side.</summary>
    public static float FooterPromptScale(float canvasWidth, float promptWidth) =>
        promptWidth <= 0.5f ? 1f : MathF.Min(FooterPromptMaxScale, (canvasWidth - FooterPromptMargin) / promptWidth);

    /// <summary>The left edge that centres a control of this size and scale in the canvas.</summary>
    public static float CenteredX(float canvasWidth, float width, float scale) => (canvasWidth - width * scale) * 0.5f;

    /// <summary>Where the transform and generic deck select screens' prompts start, from the canvas bottom.</summary>
    public static float TransformPromptTop(float canvasHeight) => canvasHeight - TransformPromptFromBottom;

    /// <summary>Where the generic deck select's prompt starts, from the canvas bottom.</summary>
    public static float SelectPromptTop(float canvasHeight) => canvasHeight - SelectPromptFromBottom;

    /// <summary>Where the upgrade screen's prompt starts, from the canvas bottom.</summary>
    public static float UpgradePromptTop(float canvasHeight) => canvasHeight - UpgradePromptFromBottom;

    /// <summary>The enchant legend's top, packed up from the canvas bottom by its drawn height.</summary>
    public static float LegendTop(float canvasHeight, float legendHeight) =>
        canvasHeight - LegendFromBottom - legendHeight * LegendScale;

    /// <summary>The legend's own width, drawn at <see cref="LegendScale"/> inside 140 reference units of margin.</summary>
    public static float LegendLocalWidth(float canvasWidth) => (canvasWidth - LegendSideMargin) / LegendScale;

    /// <summary>The enchant prompt's top, above the legend.</summary>
    public static float PromptTopAboveLegend(float legendTop, float promptHeight, float promptScale) =>
        legendTop - PromptLegendGap - promptHeight * promptScale;

    /// <summary>The bottom of the card viewport that belongs to a prompt at <paramref name="promptTop"/>.</summary>
    public static float GridBottom(float promptTop) => promptTop - GridGap;

    /// <summary>Where a comparison sits between the notch cap and the canvas bottom.</summary>
    public static float PreviewCenterY(float capHeight, float canvasHeight, float fraction) =>
        capHeight + (canvasHeight - capHeight) * fraction;

    /// <summary>
    /// A preview button's growth: the reference's 2.05 (Confirm) or 2.1 (Cancel), but never less than a 44 point target.
    /// </summary>
    public static float ButtonScale(float nativeHeight, bool cancel)
    {
        float preferred = cancel ? CancelScale : ConfirmScale;
        return nativeHeight <= 0.5f ? preferred : MathF.Max(preferred, TouchTarget / nativeHeight);
    }

    /// <summary>The top edge of the comparison buttons, counted up from the canvas bottom.</summary>
    public static float ButtonTop(float canvasHeight) => canvasHeight - ButtonTopFromBottom;

    /// <summary>The upgrade screen's Close button stands above its View Upgrades box.</summary>
    public static float CloseTop(float canvasHeight) => canvasHeight - CloseAboveToggle;

    /// <summary>The left edge of a Confirm of this drawn width, a stone margin in from the canvas's right edge.</summary>
    public static float ConfirmLeft(float canvasWidth, float buttonWidth) =>
        canvasWidth - buttonWidth - ButtonRightMargin;

    /// <summary>
    /// The transform preview's scale: the whole native composition (both sides, every selected card) fitted into the room
    /// around the preview's own origin, less the margins the reference leaves and the band its buttons take at the bottom.
    /// </summary>
    public static float TransformScale(
        float centerX, float centerY, float safeLeft, float safeTop, float safeRight, float safeBottom,
        float extentX, float extentY)
    {
        if (extentX <= 0.5f || extentY <= 0.5f) return 1f;
        float roomX = MathF.Min(centerX - safeLeft, safeRight - centerX) - TransformSideMargin;
        float roomY = MathF.Min(centerY - safeTop, safeBottom - centerY - TransformBottomReserve) - TransformBottomMargin;
        if (roomX <= 0.5f || roomY <= 0.5f) return TransformMinScale;
        return Math.Clamp(MathF.Min(roomX / extentX, roomY / extentY), TransformMinScale, TransformMaxScale);
    }
}
