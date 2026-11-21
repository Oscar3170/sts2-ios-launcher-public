using System;

namespace STS2Mobile.Platform;

/// <summary>What a press that began on a shop item, choice card or offered relic has become.</summary>
public enum SelectPress
{
    /// <summary>Still down, still a tap candidate: nothing happens until the finger lifts.</summary>
    Pending,
    /// <summary>A short, still tap released on the item: the second tap confirms it.</summary>
    Confirm,
    /// <summary>Held still past <see cref="SelectConfirmMath.HoldSeconds"/>: the item's native inspection opens; a release then confirms nothing.</summary>
    Inspect,
    /// <summary>The finger moved past <see cref="SelectConfirmMath.ConfirmSlop"/>, or a hold was released: no confirmation.</summary>
    Cancel,
}

/// <summary>
/// The select-then-confirm thresholds shared by the shop, card rewards, choose-a-card, relic choice and treasure relics. The
/// first press on an item selects it at once; the second tap confirms when the finger lifts, so that a hold can open the
/// item's inspection instead and a swipe can scroll or drag without buying or taking anything. Distances are canvas units on
/// the 1080-wide portrait canvas, where one phone point is about 2.9 units: 23 units is a deliberate 8 points of slop for
/// a second tap, and 0.6 s is the hold that inspects the item instead (the hand's own
/// <see cref="TouchCardMath.HoldSeconds"/>).
/// </summary>
public static class SelectConfirmMath
{
    /// <summary>The furthest a tap may wander and still confirm, in canvas units (8 phone points).</summary>
    public const float ConfirmSlop = 23f;

    /// <summary>A press held still this long inspects the item instead of confirming it, in seconds.</summary>
    public const float HoldSeconds = 0.6f;

    /// <summary>
    /// Reads a press on an item: its greatest movement since it went down, how long it has been down, and whether it has been
    /// released. A release within the slop and before the hold confirms; a still press that lasts the hold opens the inspection
    /// once; moving past the slop cancels for the rest of the press, and a release after it or after the hold confirms nothing.
    /// </summary>
    public static SelectPress Decide(float moved, double seconds, bool released)
    {
        if (moved > ConfirmSlop) return SelectPress.Cancel;
        if (released) return seconds >= HoldSeconds ? SelectPress.Cancel : SelectPress.Confirm;
        return seconds >= HoldSeconds ? SelectPress.Inspect : SelectPress.Pending;
    }

    /// <summary>
    /// Whether the item under a press is still the selected one: same node (a slot or holder) and same model (a shop entry's
    /// current card, relic or potion, a card or a relic). A Courier restock keeps the slot and swaps the model, so the slot
    /// alone would confirm a replacement.
    /// </summary>
    public static bool IsSameItem(ulong itemId, object? model, ulong selectedId, object? selectedModel) =>
        itemId != 0 && itemId == selectedId && ReferenceEquals(model, selectedModel);

    /// <summary>A movement that a tap still allows.</summary>
    public static bool WithinSlop(float moved) => moved <= ConfirmSlop;

    /// <summary>How long the press has been down, from millisecond ticks.</summary>
    public static double SecondsSince(ulong pressTicks, ulong nowTicks) =>
        nowTicks <= pressTicks ? 0 : (nowTicks - pressTicks) / 1000.0;

    /// <summary>Movement between two points, for a press that may wander in either direction.</summary>
    public static float Moved(float dx, float dy) => MathF.Sqrt(dx * dx + dy * dy);
}
