using System;
using System.Linq;
using Godot;

namespace STS2Mobile.GameHost;

internal sealed partial class PortraitMenuLayout
{
    // Dread keeps the character-select roster and confirm button; its name, score, win streak and rotation tickbox take the
    // places measured from the desktop layout: the name under the top edge, the score and streak above the roster and the
    // tickbox between them, each grown for a phone.
    private const float DreadNameTop = 260f;
    private const float DreadNameScale = 1.7f;
    private const float DreadScoreScale = 1.65f;
    private const float DreadStreakScale = 1.3f;
    private const float DreadTickboxScale = 1.8f;

    private void LayoutDread(Control screen, ScreenState state)
    {
        Rect2 safe = state.Safe;
        float gridTop = ArrangeCharacterButtons(state, screen, Roster(screen), safe);
        PrepareConfirm(state, screen, safe);
        PlaceBackButton(screen, state);

        // Dread's badge and info button sit on the top edge on the desktop, under the notch here: shift them with the safe
        // band like Blitz's header.
        foreach (string name in new[] { "%UnderConstruction", "%DreadInfoButton" })
            ShiftTopControl(screen, name, safe.Position.Y);
        GrowInfoTarget(screen, "%DreadInfoButton");

        // A finger's target is 127 canvas units, 197.5 of this screen's units; the tickbox and the win streak above it are
        // short wide blocks, so they are stacked with the room between them that the pad needs to reach that size.
        float tickHeight = DreadHeight(screen, "RotatingTickbox", DreadTickboxScale);
        float streakHeight = DreadHeight(screen, "DreadWinStreak", DreadStreakScale);
        float scoreHeight = DreadHeight(screen, "ScoreContainer", DreadScoreScale);
        float tickTop = gridTop - 150f;
        float gap = Math.Max(24f, 197.5f - (tickHeight + streakHeight) / 2f);
        float streakTop = tickTop - gap - streakHeight;
        float scoreTop = streakTop - 32f - scoreHeight;

        PlaceDreadControl(screen, "DreadNameText", safe.Position.Y + DreadNameTop, DreadNameScale, safe);
        PlaceDreadControl(screen, "ScoreContainer", scoreTop, DreadScoreScale, safe);
        PlaceDreadControl(screen, "DreadWinStreak", streakTop, DreadStreakScale, safe);
        PlaceDreadControl(screen, "RotatingTickbox", tickTop, DreadTickboxScale, safe);
    }

    private static float DreadHeight(Control screen, string name, float scale)
    {
        Control? control = screen.GetNodeOrNull<Control>(name) ?? screen.GetNodeOrNull<Control>("%" + name);
        return control == null ? 0f : Math.Max(0f, control.Size.Y * scale);
    }

    /// <summary>
    /// Places one of Dread's own blocks centred in the width at a height of the screen's units, drawn at the measured scale
    /// (smaller when its picture would not fit the width). Its own size is kept, so the native clock and chain animations
    /// keep their local layout.
    /// </summary>
    private void PlaceDreadControl(Control screen, string name, float y, float scale, Rect2 safe)
    {
        Control? control = screen.GetNodeOrNull<Control>(name) ?? screen.GetNodeOrNull<Control>("%" + name);
        if (control == null) return;
        Capture(control);
        Vector2 size = control.Size;
        if (size.X < 1f || size.Y < 1f) return;
        scale = Math.Min(scale, (safe.Size.X - 80f) / size.X);
        if (scale < 0.2f) scale = 0.2f;
        control.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        if (control.Size.DistanceSquaredTo(size) > 0.25f) control.Size = size;
        SetScale(control, new Vector2(scale, scale), Vector2.Zero);
        var place = new Vector2(safe.GetCenter().X - size.X * scale * 0.5f, y);
        if (control.Position.DistanceSquaredTo(place) > 0.25f) control.Position = place;
    }
}
