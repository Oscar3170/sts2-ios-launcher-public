using System;

namespace STS2Mobile.Platform;

/// <summary>
/// Lets the portrait host keep a card grid's row window in place of the game's own recycler. The game decides from the whole
/// screen (row 0 below the screen's top, the last row above its bottom); a portrait grid starts under the header and ends
/// above the footer, so both held at once and the game rebound rows back and forth while the content moved.
/// </summary>
public static class CardGridWindowGate
{
    public static Func<object, bool>? KeepWindow { private get; set; }

    // The patched game calls this with its NCardGrid at the start of AllocateCardHolders. No host means the game's own recycler.
    public static bool KeepsWindow(object grid) => SafeAreaCanvas.IsPortrait && KeepWindow?.Invoke(grid) == true;
}
