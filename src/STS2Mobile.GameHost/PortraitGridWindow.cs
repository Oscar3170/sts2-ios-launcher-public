using System;
using System.Collections;
using System.Reflection;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Keeps a card grid's pooled rows on the cards its own view shows, in place of the game's recycler (NCardGrid's
/// AllocateCardHolders, which the patched game hands to <see cref="CardGridWindowGate"/> in portrait). The game decides from the
/// whole screen: row 0 below the screen's top means "add a row above" and the last row above its bottom means "add one below".
/// A portrait grid starts under the header and ends above the footer, so both held at once and it rebound the same rows back
/// and forth every frame the content moved. This decides from the scroll offset alone: the row standing at the grid's top,
/// with one row of the pool above it, the same overscan the game's own pool is sized for.
/// <para>
/// It must only run where the game's own recycler runs or earlier in the frame (the grid's _Process, or process_frame): a
/// holder rebound after the frame's last deferred-call flush moves at once, but its card redraws its face only in the next
/// frame, so the frame drawn shows the previous card in that place (a row of wrong cards while scrolling).
/// </para>
/// </summary>
internal static class PortraitGridWindow
{
    private const string CardGridTypeName = "MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid";

    private static bool _resolved;
    private static Type? _gridType;
    private static FieldInfo? _cardRows;
    private static FieldInfo? _slidingIndex;
    private static FieldInfo? _cardSize;
    private static FieldInfo? _scrollContainer;
    private static FieldInfo? _needsReinit;
    private static PropertyInfo? _sortedCards;
    private static PropertyInfo? _columns;
    private static PropertyInfo? _cardPadding;
    private static PropertyInfo? _yOffset;
    private static MethodInfo? _assignRow;
    private static MethodInfo? _positionRows;
    private static MethodInfo? _updateNavigation;
    private static readonly object[] RowArgs = new object[2];
    private static readonly object[] IndexArgs = new object[1];

    /// <summary>Installs the keeper behind the patched game's recycler.</summary>
    public static void Attach() => CardGridWindowGate.KeepWindow = Keep;

    public static void Detach() => CardGridWindowGate.KeepWindow = null;

    /// <summary>The gate: whether this keeper took the grid's recycling for this frame (false leaves the game's own).</summary>
    private static bool Keep(object grid) => grid is Control control && Reconcile(control);

    /// <summary>
    /// Re-points the grid's rows at the window its scroll offset needs, with their positions, when the window is not already
    /// that one (one int compare otherwise). Returns false only when the grid's members cannot be read, so the caller keeps the
    /// game's own recycler.
    /// </summary>
    public static bool Reconcile(Control grid)
    {
        Resolve();
        if (_gridType == null || !_gridType.IsInstanceOfType(grid) || _cardRows == null || _slidingIndex == null ||
            _cardSize == null || _scrollContainer == null || _sortedCards == null || _columns == null || _cardPadding == null ||
            _yOffset == null || _assignRow == null || _positionRows == null)
            return false;
        // A grid about to build its rows again keeps the window InitGrid gives it.
        if (_needsReinit?.GetValue(grid) is true) return true;
        if (_cardRows.GetValue(grid) is not IList rows || rows.Count == 0) return true;
        if (_sortedCards.GetValue(grid) is not ICollection cards || cards.Count == 0) return true;
        if (_scrollContainer.GetValue(grid) is not Control scroll || _cardSize.GetValue(grid) is not Vector2 cardSize) return true;
        if (_columns.GetValue(grid) is not int columns || columns < 1) return true;
        float pitch = cardSize.Y + (float)_cardPadding.GetValue(grid)!;
        if (pitch < 1f) return true;
        float contentTop = (int)_yOffset.GetValue(grid)! + PortraitGridMath.ContentTopPadding;
        int total = (cards.Count + columns - 1) / columns;
        int pool = rows.Count;
        int firstVisible = (int)MathF.Floor((-scroll.Position.Y - contentTop) / pitch);
        int wanted = Math.Clamp(firstVisible - 1, 0, Math.Max(0, total - pool));
        int current = (int)_slidingIndex.GetValue(grid)! / columns;
        if (wanted == current) return true;
        int start = wanted * columns;
        _slidingIndex.SetValue(grid, start);
        for (int i = 0; i < pool; i++)
        {
            RowArgs[0] = rows[i]!;
            RowArgs[1] = start + i * columns;
            _assignRow.Invoke(grid, RowArgs);
        }
        IndexArgs[0] = start;
        _positionRows.Invoke(grid, IndexArgs);
        _updateNavigation?.Invoke(grid, null);
        return true;
    }

    private static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        _gridType = typeof(MegaCrit.Sts2.Core.Nodes.NGame).Assembly.GetType(CardGridTypeName);
        if (_gridType is not { } type) return;
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        _cardRows = type.GetField("_cardRows", any);
        _slidingIndex = type.GetField("_slidingWindowCardIndex", any);
        _cardSize = type.GetField("_cardSize", any);
        _scrollContainer = type.GetField("_scrollContainer", any);
        _needsReinit = type.GetField("_needsReinit", any);
        _sortedCards = type.GetProperty("SortedCards", any);
        _columns = type.GetProperty("Columns", any);
        _cardPadding = type.GetProperty("CardPadding", any);
        _yOffset = type.GetProperty("YOffset", any);
        foreach (MethodInfo method in type.GetMethods(any))
        {
            switch (method.Name)
            {
                case "AssignCardsToRow" when method.GetParameters().Length == 2:
                    _assignRow ??= method;
                    break;
                case "UpdateGridPositions" when method.GetParameters().Length == 1:
                    _positionRows ??= method;
                    break;
                case "UpdateGridNavigation" when method.GetParameters().Length == 0:
                    _updateNavigation ??= method;
                    break;
            }
        }
        if (_cardRows == null || _slidingIndex == null || _assignRow == null || _positionRows == null || _columns == null)
            GD.PrintErr("Portrait grid window: NCardGrid's rows could not be read; the game's own recycler stays in charge.");
    }
}
