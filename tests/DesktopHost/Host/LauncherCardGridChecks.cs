using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The launcher's answers for the shared deck-view, upgrade, enchant and transform scenarios (Batch F). Each method
/// implements a partial declared in HarnessCore.cs.
/// <para>
/// Every measurement is in the launcher's canvas units: 1080 across, so a 44-point touch target is 127 units and the three
/// grid columns are <c>(canvas − 80 − the grid's own side insets) / 3</c> wide (about 300, as the phone layout specifies).
/// Numbers quoted on the original 1680-wide reference canvas are converted by 1080/1680 where a converted floor helps.
/// Nodes are found by the game's own names and measured from live rectangles; nothing here reaches into the portrait pass.
/// </para>
/// </summary>
internal sealed partial class TestDriver
{
    // The native grid's CardPadding, reserved once around the three columns.
    private const float LauncherGridPadding = 80f;
    // The game's own card is 300 units wide; a three-column card in the launcher's canvas is that or wider.
    private const float LauncherCardDesignWidth = 300f;
    // A card may sit a few units under the exact three-column arithmetic and still be that layout.
    private const float LauncherGridWidthSlack = 12f;

    private static float LauncherTouchTarget => PortraitBands.MinTarget;

    /// <summary>
    /// The band a portrait grid must start below. The game's top bar is the base; while the run shows the relic row, the
    /// reference layout starts every grid below the relic holders that are actually visible (its
    /// <c>TopBelowInventory</c>), so a sort row or first card row cannot sit under a relic and lose its taps to it.
    /// </summary>
    private float LauncherGridTopBand()
    {
        float top = PortraitBands.HeaderBottom(PortraitLayout.ComputeCapHeight(_tree.Root));
        ScrollContainer? row = FindNamed<ScrollContainer>(_tree.Root, RelicRowName);
        if (row is null || !row.IsVisibleInTree()) return top;
        Rect2 clip = row.GetGlobalRect();
        foreach (NRelicInventoryHolder holder in FindAll<NRelicInventoryHolder>(row))
        {
            if (!holder.IsVisibleInTree()) continue;
            Rect2 visible = holder.GetGlobalRect().Intersection(clip);
            if (visible.HasArea()) top = Mathf.Max(top, visible.End.Y + 16f);
        }
        return top;
    }

    private static Control? LauncherGridScroll(NCardGrid grid) =>
        grid.GetNodeOrNull<Control>("%ScrollContainer") ?? grid.GetNodeOrNull<Control>("ScrollContainer");

    /// <summary>
    /// The smallest a card can be and still be one of three columns filling the scroll width: the launcher's canvas less
    /// the grid's padding and its own side insets, divided three ways. Measured from the live scroll container, so whatever
    /// scale the portrait pass keeps the grid at, the three-column arithmetic is the same.
    /// </summary>
    private static float LauncherGridCardWidthFloor(NCardGrid grid)
    {
        Rect2 view = grid.GetGlobalRect();
        Control? scroll = LauncherGridScroll(grid);
        float insets = scroll is null ? 0f : Math.Max(0f, view.Size.X - scroll.GetGlobalRect().Size.X);
        return (SafeAreaCanvas.PortraitWidth - LauncherGridPadding - insets) / 3f - LauncherGridWidthSlack;
    }

    /// <summary>
    /// The rect of the card a grid holder shows, in canvas units: the game centres the card on the zero-sized holder's origin
    /// and Godot's <c>Control.GetGlobalRect()</c> reports an unscaled local size, so the shared <see cref="DrawnCardRect"/>
    /// (card default size through the holder's global scale) is the drawn card — 312.3 u in the three-column grid.
    /// </summary>
    private static Rect2 LauncherGridCardRect(NGridCardHolder holder) => DrawnCardRect(holder);

    /// <summary>The grid's displayed cards in the grid's own viewport, grouped into rows by laid-out centre.</summary>
    private static List<IGrouping<long, NGridCardHolder>> LauncherGridRows(NCardGrid grid)
    {
        Rect2 view = grid.GetGlobalRect();
        return grid.CurrentlyDisplayedCardHolders
            .Where(holder => GodotObject.IsInstanceValid(holder) && holder.IsVisibleInTree()
                && view.Grow(2f).HasPoint(CenterOf(holder.Hitbox)))
            .GroupBy(holder => (long)Math.Round(LauncherGridCardRect(holder).GetCenter().Y / 10f))
            .ToList();
    }

    private static string LauncherRowCounts(IEnumerable<IGrouping<long, NGridCardHolder>> rows) =>
        string.Join(",", rows.Select(row => row.Count()));

    /// <summary>
    /// Three columns means the cards in a row do not overlap: the native pitch (<c>_cardSize.X + CardPadding</c>) has to
    /// be at least the card's own drawn width. A row of rects that intersect is two-and-a-bit columns, not three.
    /// </summary>
    private static bool LauncherRowsDoNotOverlap(IEnumerable<IGrouping<long, NGridCardHolder>> rows) =>
        rows.All(row => row.Select(LauncherGridCardRect).OrderBy(rect => rect.Position.X)
            .Zip(row.Select(LauncherGridCardRect).OrderBy(rect => rect.Position.X).Skip(1))
            .All(pair => pair.First.End.X <= pair.Second.Position.X + 1f));

    /// <summary>
    /// The game keeps the sort row inside the grid's scroll container; the launcher's pass leaves it there (or the screen
    /// may own it after a re-parent). Look both places, as the launcher's own grid layout does.
    /// </summary>
    private partial Control DeckSortRow(NDeckViewScreen screen, NCardGrid grid) =>
        screen.GetNodeOrNull<Control>("SortingOptions")
        ?? GridScrollContent(grid).GetNodeOrNull<Control>("SortingOptions")
        ?? throw new InvalidOperationException("the deck view has no SortingOptions row");

    private partial bool ChoiceSelectionIdle(NGridCardHolder holder) => PhoneSelectConfirm.SelectedOwner is null;

    private partial void CheckDeckSortersPosition(Control header)
    {
        Rect2 rect = header.GetGlobalRect();
        float band = LauncherGridTopBand();
        _report.Check("deck_sorters_high_below_header", rect.Position.Y >= band - 1f
            && rect.Position.Y <= band + 240f,
            $"headerBottom={band:F1} sortTop={rect.Position.Y} sortBottom={rect.End.Y}");
    }

    private partial bool DeckGridStartFits(Control header, Rect2 view, Rect2 firstCard,
        IReadOnlyList<NCardViewSortButton> sorters)
    {
        Rect2 rect = header.GetGlobalRect();
        bool cardBelowHeader = firstCard.Size.Y < 1f || rect.End.Y <= firstCard.Position.Y + 1f;
        bool gridBelowHeader = rect.End.Y <= view.Position.Y + 1f;
        return sorters.Count == 4
            && sorters.All(button => button.GetGlobalRect().Size.Y >= LauncherTouchTarget - 1f)
            && rect.Position.Y >= LauncherGridTopBand() - 1f
            && (cardBelowHeader || gridBelowHeader);
    }

    private partial void CheckDeckGridColumns(NCardGrid grid)
    {
        var rows = LauncherGridRows(grid);
        float floor = LauncherGridCardWidthFloor(grid);
        bool widths = rows.SelectMany(row => row).All(holder => LauncherGridCardRect(holder).Size.X >= floor - 1f);
        _report.Check("deck_three_columns", rows.Count > 0 && rows.Any(row => row.Count() == 3)
            && rows.All(row => row.Count() <= 3) && widths && LauncherRowsDoNotOverlap(rows),
            $"rows={LauncherRowCounts(rows)} minCard={floor:F0} overlap={!LauncherRowsDoNotOverlap(rows)} " +
            $"widths={string.Join(",", rows.SelectMany(row => row).Select(holder => LauncherGridCardRect(holder).Size.X.ToString("F0")))}");
    }

    private partial void CheckGridStartsBelowHeader(string name, Rect2 view, bool atTop, float offset)
    {
        float band = LauncherGridTopBand();
        _report.Check($"{name}_grid_starts_below_header", view.Position.Y >= band - 1f && atTop,
            $"grid={view} headerBottom={band:F1} offset={offset}");
    }

    private partial void CheckEnchantLegend(Control prompt, Control legend)
    {
        Rect2 legendRect = legend.GetGlobalRect();
        Rect2 promptRect = prompt.GetGlobalRect();
        ColorRect? plate = legend.GetNodeOrNull<ColorRect>("ColorRect");
        _report.Check("enchant_legend_opaque_and_below_prompt", PortraitSafeRect().Encloses(legendRect)
            && promptRect.End.Y < legendRect.Position.Y && plate is not null && plate.Color.A == 1f,
            $"legend={legendRect} prompt={promptRect} plate={(plate is null ? "missing" : plate.Color.A.ToString("F2"))}");
    }

    private partial void CheckEnchantPreviewFit(int count, IReadOnlyList<Rect2> cards, Control confirm)
    {
        Rect2 safe = PortraitSafeRect();
        Rect2 button = confirm.GetGlobalRect();
        Rect2 icon = confirm.GetNode<Control>("Image/Icon").GetGlobalRect();
        float cardFloor = 405f * ReferenceToCanvas;
        _report.Check($"enchant_{count}_preview_and_confirm_fit", cards.Count == 2
            && cards.All(c => c.Size.X >= cardFloor - 1f && safe.Encloses(c))
            && button.Size.Y >= LauncherTouchTarget - 1f && safe.Encloses(button) && button.Encloses(icon),
            $"cards={string.Join(", ", cards)} confirm={button} checkmark={icon} minCard={cardFloor:F0}");
    }

    private partial void CheckTransformPreviewFit(int count, IReadOnlyList<Rect2> bounds, Control confirm, Vector2 previewScale)
    {
        Rect2 safe = PortraitSafeRect();
        Rect2 button = confirm.GetGlobalRect();
        // The launcher fits the whole native composition into the room above its buttons, so the geometry that matters is
        // that every card and the Confirm stay inside the canvas, the two sides do not collide, and Confirm is a 44-point
        // target. The reference layout's per-card widths belong to its own composition, not this one.
        _report.Check($"transform_{count}_preview_fits", bounds.Count == count * 2
            && bounds.All(safe.Encloses)
            && bounds.Zip(bounds.Skip(1)).All(pair => pair.First.Intersection(pair.Second).Size.X < 5f)
            && safe.Encloses(button) && button.Size.Y >= LauncherTouchTarget - 1f,
            $"scale={previewScale} cards={string.Join(", ", bounds)} confirm={button}");
    }

    private partial void CheckUpgradeCardsScaled(NCardGrid grid, IReadOnlyList<NGridCardHolder> holders)
    {
        if (!SafeAreaCanvas.IsPortrait)
        {
            // Landscape is the game's own window and the game's own scale: NGridCardHolder.Create/InitGrid set every holder
            // to NCardHolder.smallScale (0.8), no selection holder is scaled up, and a card draws NCard.defaultSize (300)
            // x 0.8 = 240 units wide.
            float cardWidth = NCard.defaultSize.X;
            bool nativeScale = holders.Count > 0
                && holders.All(holder => Mathf.Abs(holder.Scale.X - holder.SmallScale.X) <= 0.01f);
            bool nativeWidth = holders.All(holder => Mathf.Abs(cardWidth * holder.Scale.X - 240f) <= 2f);
            _report.Check("upgrade_cards_scaled", nativeScale && nativeWidth,
                $"native grid card scale={string.Join(",", holders.Select(holder => holder.Scale.X.ToString("F2")))} " +
                $"width={string.Join(",", holders.Select(holder => (cardWidth * holder.Scale.X).ToString("F0")))}");
            return;
        }
        float floor = LauncherGridCardWidthFloor(grid);
        _report.Check("upgrade_cards_scaled", holders.Count > 0
            && holders.All(holder => LauncherGridCardRect(holder).Size.X >= floor - 1f),
            $"minCard={floor:F0} widths={string.Join(",", holders.Select(holder => LauncherGridCardRect(holder).Size.X.ToString("F0")))}");
    }

    private partial void CheckUpgradeColumns(NCardGrid grid, IReadOnlyList<NGridCardHolder> holders)
    {
        var rows = holders.GroupBy(holder => (long)Math.Round(LauncherGridCardRect(holder).GetCenter().Y / 10f)).ToList();
        float floor = LauncherGridCardWidthFloor(grid);
        bool widths = holders.All(holder => LauncherGridCardRect(holder).Size.X >= floor - 1f);
        _report.Check("upgrade_three_columns", rows.Any(row => row.Count() == 3) && rows.All(row => row.Count() <= 3) && widths
            && LauncherRowsDoNotOverlap(rows),
            $"rows={LauncherRowCounts(rows)} minCard={floor:F0} overlap={!LauncherRowsDoNotOverlap(rows)}");
    }

    private partial void CheckUpgradeToggle(Control upgrades, Rect2 gridView)
    {
        Rect2 rect = upgrades.GetGlobalRect();
        _report.Check("upgrade_toggle_touch_size", rect.Size.Y >= LauncherTouchTarget - 1f, $"rect={rect}");
        _report.Check("upgrade_toggle_safe", PortraitSafeRect().Encloses(rect) && rect.Position.Y >= gridView.End.Y - 1f,
            $"rect={rect} grid={gridView} safe={PortraitSafeRect()}");
    }

    private partial void CheckUpgradePreview(NDeckUpgradeSelectScreen screen, Control comparison, Control confirm)
    {
        Rect2 safe = PortraitSafeRect();
        Rect2[] previewCards = FindAll<NCard>(comparison).Where(card => card.IsVisibleInTree()).Select(card =>
        {
            Vector2 size = NCard.defaultSize * card.GetGlobalTransform().Scale;
            return new Rect2(card.GlobalPosition - size * 0.5f, size);
        }).ToArray();
        float cardFloor = LauncherCardDesignWidth;
        _report.Check("upgrade_portrait_comparison_and_confirm", previewCards.Length == 2
            && previewCards.All(box => safe.Encloses(box) && box.Size.X >= cardFloor - 1f)
            && !previewCards[0].Intersects(previewCards[1])
            && safe.Encloses(confirm.GetGlobalRect()) && confirm.GetGlobalRect().Size.Y >= LauncherTouchTarget - 1f
            && !screen.GetNode<Control>("%BottomText").Visible && !screen.GetNode<Control>("ViewUpgrades").Visible,
            $"cards={string.Join(", ", previewCards)} confirm={confirm.GetGlobalRect()} minCard={cardFloor:F0}");
    }

    private partial void CheckUpgradeBackAboveToggle(Control close, Control toggle)
    {
        // Measure the drawn art, not GetGlobalRect: a Back pivots at its left/bottom, so GetGlobalRect's position is the pivot
        // and its end reads a whole button height below the art.
        Rect2 back = DrawnBackRect(close);
        Rect2 toggleRect = toggle.GetGlobalRect();
        _report.Check("upgrade_back_above_toggle", back.End.Y <= toggleRect.Position.Y
            && back.Size.Y >= LauncherTouchTarget - 1f && PortraitSafeRect().Grow(1f).Encloses(back),
            $"back={back} toggle={toggleRect}");
    }

    /// <summary>
    /// A disabled Back is parked outside the canvas by PortraitNavigationPads (its <c>PlaceBack</c>: its places are its
    /// top-left plus one drawn width, margin included). The conservative union of everything it could draw — box and every
    /// visible child — must be off the canvas: a sliver at the left edge is the regression this checks.
    /// </summary>
    private partial void CheckBackHidden(string name, Control back)
    {
        Rect2 drawn = DrawnBackUnion(back);
        Rect2 canvas = back.GetViewport().GetVisibleRect();
        _report.Check($"{name}_back_hidden_off_canvas",
            drawn.End.X <= canvas.Position.X + 0.5f && !drawn.Intersects(canvas),
            $"drawn={drawn} box={DrawnBackRect(back)} canvas={canvas} " +
            $"enabled={back is NClickableControl { IsEnabled: true }}");
    }

    /// <summary>
    /// A shown Back must be wholly inside the safe rect (with the 44 point pad it also has to reach) and must not be drawn
    /// under the enchant legend or a hover tip: on the selection screens the legend is packed across the footer, so the Back
    /// sits on the comparison buttons' line below it.
    /// </summary>
    private partial void CheckBackShown(string name, Control back)
    {
        Rect2 drawn = DrawnBackRect(back);
        Rect2 safe = PortraitSafeRect();
        Rect2[] tips = VisibleFooterTips(back).Where(tip => tip.Intersects(drawn)).ToArray();
        _report.Check($"{name}_back_shown_safe_and_clear",
            safe.Grow(1f).Encloses(drawn) && back is NClickableControl { IsEnabled: true } && tips.Length == 0,
            $"drawn={drawn} safe={safe} enabled={back is NClickableControl { IsEnabled: true }} " +
            $"tips=[{string.Join(", ", tips)}]");
    }
}
