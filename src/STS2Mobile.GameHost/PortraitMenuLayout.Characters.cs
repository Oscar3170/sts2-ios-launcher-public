using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace STS2Mobile.GameHost;

internal sealed partial class PortraitMenuLayout
{
    // The roster: seven character buttons in two rows (four over three), at most 2.52 times their desktop size, the lower
    // row ending 282 units above the safe band's lower edge so the ascension panel and the confirm button keep their room.
    private const float GridGapX = 23f;
    private const float GridGapY = 14f;
    private const float GridMaxScale = 2.52f;
    private const float GridSideRoom = 64f;
    private const float GridBottomRoom = 282f;

    // The ascension panel: 1.8 times its desktop size, its flame under the roster, the level number centred on the opaque
    // body of the flame rather than its tips.
    private const float AscensionScale = 1.8f;
    private const float AscensionGap = 32f;

    // The confirm button: 2.1 times its desktop size, its show/hide places from the safe band's lower right corner.
    private const float ConfirmScale = 2.1f;
    private const float ConfirmRight = 40f;
    private const float ConfirmBottom = 20f;

    // The info panel: 526 units wide, 404 tall (434 on Blitz), drawn at most 2.35 times, centred on the safe band's middle
    // or above the ascension panel, whichever is higher.
    private const float InfoWidth = 526f;
    private const float InfoHeight = 404f;
    private const float InfoBlitzHeight = 434f;
    private const float InfoMaxScale = 2.35f;
    private const float InfoSideRoom = 44f;
    private const float InfoTopFraction = 0.445f;
    private const float InfoAscensionGap = 24f;

    private void LayoutCharacterSelect(Control screen, ScreenState state)
    {
        Rect2 safe = state.Safe;
        float gridTop = ArrangeCharacterButtons(state, screen, Roster(screen), safe);
        PrepareConfirm(state, screen, safe);
        EnsureBackOnTop(screen);

        // Blitz's title, its badge and its info button sit along the top edge, under the notch.
        foreach (string name in new[] { "BlitzPlaceholder", "%UnderConstruction", "%BlitzInfoButton" })
            ShiftTopControl(screen, name, safe.Position.Y);
        GrowInfoTarget(screen, "%BlitzInfoButton");

        float ascensionTop = PrepareAscension(screen, safe, gridTop);
        PrepareInfo(screen, state, state.TypeName == BlitzTypeName, safe, ascensionTop);
    }

    private static Control? Roster(Control screen) =>
        screen.GetNodeOrNull<Control>("CharSelectButtons/ButtonContainer") ??
        screen.GetNodeOrNull<Control>("%ButtonContainer");

    /// <summary>
    /// Lays the character buttons out four over three, each at the largest cell the roster's minimum sizes need and the width
    /// allows, with the rows' focus crossing between them. Returns the roster's top in the screen's units.
    /// </summary>
    private float ArrangeCharacterButtons(ScreenState state, Control screen, Control? grid, Rect2 safe)
    {
        float fallback = safe.End.Y - GridBottomRoom;
        if (grid == null) return fallback;
        List<Control> buttons = grid.GetChildren().OfType<Control>()
            .Where(c => c.Visible && c.GetType().Name == "NCharacterSelectButton").ToList();
        if (buttons.Count == 0) return fallback;

        int columns = (buttons.Count + 1) / 2;
        Vector2 cell = Vector2.Zero;
        foreach (Control button in buttons) cell = cell.Max(button.GetCombinedMinimumSize());
        if (cell.X < 1f || cell.Y < 1f) return fallback;

        float scale = Math.Min(GridMaxScale, (safe.Size.X - GridSideRoom) / (columns * (cell.X + GridGapX) - GridGapX));
        if (scale < 0.3f) scale = 0.3f;
        float width = safe.Size.X / scale;
        float height = buttons.Count > 1 ? 2f * cell.Y + GridGapY : cell.Y;
        float top = safe.End.Y - GridBottomRoom - height * scale;

        Capture(grid);
        if (grid.AnchorLeft != 0f || grid.AnchorTop != 0f || grid.AnchorRight != 0f || grid.AnchorBottom != 0f)
            grid.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        SetScale(grid, new Vector2(scale, scale), Vector2.Zero);
        Vector2 target = screen.GetGlobalTransformWithCanvas() * new Vector2(safe.Position.X, top);
        if (grid.GlobalPosition.DistanceSquaredTo(target) > 0.25f) grid.GlobalPosition = target;

        // The game sorts the row again when a character unlocks or a random button appears; lay out again right after it.
        if (grid is Container container && state.Grid != container)
        {
            if (state.GridSort != null && GodotObject.IsInstanceValid(state.Grid)) state.Grid!.SortChildren -= state.GridSort;
            state.Grid = container;
            state.GridSort = () =>
            {
                if (GodotObject.IsInstanceValid(container) && state.Scale > 0f)
                    ArrangeCharacterButtons(state, screen, container, state.Safe);
            };
            container.SortChildren += state.GridSort;
        }

        void PositionRow(int start, int count, float y)
        {
            float left = (width - count * (cell.X + GridGapX) + GridGapX) * 0.5f;
            for (int i = 0; i < count; i++)
            {
                Control button = buttons[start + i];
                if (button.Size.DistanceSquaredTo(cell) > 0.25f) button.Size = cell;
                var place = new Vector2(left + i * (cell.X + GridGapX), y);
                if (button.Position.DistanceSquaredTo(place) > 0.25f) button.Position = place;
            }
        }
        PositionRow(0, columns, 0f);
        if (columns < buttons.Count) PositionRow(columns, buttons.Count - columns, cell.Y + GridGapY);

        if (state.FocusCount != buttons.Count)
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                Control button = buttons[i];
                SaveFocus(button);
                int row = i < columns ? 0 : 1;
                int rowStart = row == 0 ? 0 : columns;
                int rowCount = row == 0 ? columns : buttons.Count - columns;
                int inRow = i - rowStart;
                button.FocusNeighborLeft = buttons[rowStart + (inRow + rowCount - 1) % rowCount].GetPath();
                button.FocusNeighborRight = buttons[rowStart + (inRow + 1) % rowCount].GetPath();
                bool upper = row == 0;
                Control other = (upper ? buttons.Skip(columns) : buttons.Take(columns))
                    .MinBy(candidate => MathF.Abs(candidate.Position.X - button.Position.X)) ?? button;
                button.FocusNeighborTop = (upper ? button : other).GetPath();
                button.FocusNeighborBottom = (upper ? other : button).GetPath();
            }
            state.FocusCount = buttons.Count;
        }
        return top;
    }

    /// <summary>Scales the ascension panel, centers its number on the flame and places it under the roster; returns its top.</summary>
    private float PrepareAscension(Control screen, Rect2 safe, float gridTop)
    {
        if (screen.GetNodeOrNull<Control>("%AscensionPanel") is not { } ascension) return gridTop;
        Capture(ascension);
        Vector2 size = ascension.Size;
        ascension.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        ascension.Size = size;
        SetScale(ascension, new Vector2(AscensionScale, AscensionScale), Vector2.Zero);

        Control? art = ascension.GetNodeOrNull<Control>("Background");
        Rect2 artRect = art?.GetRect() ?? new Rect2(Vector2.Zero, size);
        Vector2 place = new(
            (safe.Size.X - size.X * AscensionScale) * 0.5f,
            gridTop - artRect.End.Y * AscensionScale - AscensionGap);
        if (ascension.Position.DistanceSquaredTo(place) > 0.25f) ascension.Position = place;
        float top = place.Y + artRect.Position.Y * AscensionScale;

        if (ascension.GetNodeOrNull<Control>("HBoxContainer/AscensionContainer/AscensionLevel") is { } number)
        {
            if (number.OffsetLeft != 0f) number.OffsetLeft = 0f;
            if (number.OffsetRight != 0f) number.OffsetRight = 0f;
            if (number.OffsetTop != 11f) number.OffsetTop = 11f;
            if (number.OffsetBottom != 11f) number.OffsetBottom = 11f;
        }
        return top;
    }

    /// <summary>
    /// Places the info panel centred in the safe band's upper half, above the ascension panel once it is there. While the
    /// game's own slide runs the panel is left to the tween; the slide's resting place is kept at the new spot so the next
    /// pick starts from it.
    /// </summary>
    private void PrepareInfo(Control screen, ScreenState state, bool blitz, Rect2 safe, float ascensionTop)
    {
        if (screen.GetNodeOrNull<Control>("InfoPanel") is not { } info) return;
        Control plate = info.GetNodeOrNull<Control>("NinePatchRect") ?? info;
        Capture(info);
        info.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        info.PivotOffset = Vector2.Zero;
        var size = new Vector2(InfoWidth, blitz ? InfoBlitzHeight : InfoHeight);
        if (info.Size.DistanceSquaredTo(size) > 0.25f) info.Size = size;
        float scale = Math.Min(InfoMaxScale, (safe.Size.X - InfoSideRoom) / Math.Max(1f, plate.Size.X));
        if (scale < 0.2f) scale = 0.2f;
        var drawScale = new Vector2(scale, scale);
        if (info.Scale.DistanceSquaredTo(drawScale) > 0.000001f) info.Scale = drawScale;

        Rect2 plateRect = plate.GetRect();
        var place = new Vector2(
            safe.GetCenter().X - plateRect.GetCenter().X * scale,
            Math.Min(safe.Position.Y + safe.Size.Y * InfoTopFraction, ascensionTop - plateRect.End.Y * scale - InfoAscensionGap));
        bool first = !state.InfoPlaced;
        if (first && TweenRunning(screen, "_infoPanelTween"))
        {
            // The screen's opening slide was aimed at the desktop place; stop it and stand the panel at its own.
            (GetField(screen, "_infoPanelTween") as Tween)?.Kill();
        }
        if ((first || !TweenRunning(screen, "_infoPanelTween")) && info.Position.DistanceSquaredTo(place) > 0.5f)
            info.Position = place;
        if (GetField(screen, "_infoPanelPosFinalVal") is not Vector2 final || final.DistanceSquaredTo(place) > 0.5f)
            SetField(screen, "_infoPanelPosFinalVal", place);
        state.InfoPlaced = true;
    }

    /// <summary>
    /// Enlarges the confirm button once per portrait session (its picture, outline and label together, pivoting like the
    /// native button), gives it the safe band's lower right corner as its shown place and keeps it there when the game's
    /// show/hide slide is not running.
    /// </summary>
    private void PrepareConfirm(ScreenState state, Control screen, Rect2 safe)
    {
        if (screen.GetNodeOrNull<Control>("ConfirmButton") is not { } confirm) return;
        if (!state.ConfirmScaled)
        {
            state.ConfirmScaled = true;
            var rectangles = new List<(Control Node, Rect2 Rect, Vector2 Pivot)>();
            Collect(confirm, rectangles);
            Capture(confirm);
            confirm.Size *= ConfirmScale;
            confirm.PivotOffset *= ConfirmScale;
            foreach ((Control node, Rect2 rect, Vector2 pivot) in rectangles)
            {
                Capture(node);
                node.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
                node.Position = rect.Position * ConfirmScale;
                node.Size = rect.Size * ConfirmScale;
                node.PivotOffset = pivot * ConfirmScale;
            }
        }
        confirm.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        Vector2 show = safe.End - confirm.Size - new Vector2(ConfirmRight, ConfirmBottom);
        var hide = new Vector2(screen.Size.X + 80f, show.Y);
        if (GetField(confirm, "_showPos") is not Vector2 shown || shown.DistanceSquaredTo(show) > 0.5f)
            SetField(confirm, "_showPos", show);
        if (GetField(confirm, "_hidePos") is not Vector2 hidden || hidden.DistanceSquaredTo(hide) > 0.5f)
            SetField(confirm, "_hidePos", hide);
        bool first = !state.ConfirmPlaced;
        if (TweenRunning(confirm, "_moveTween"))
        {
            if (!first) return;
            (GetField(confirm, "_moveTween") as Tween)?.Kill();
        }
        Vector2 target = GetField(confirm, "_isEnabled") is true ? show : hide;
        if (confirm.Position.DistanceSquaredTo(target) > 1f) confirm.Position = target;
        state.ConfirmPlaced = true;
    }

    private static void Collect(Control node, List<(Control Node, Rect2 Rect, Vector2 Pivot)> into)
    {
        foreach (Control child in node.GetChildren().OfType<Control>())
        {
            into.Add((child, child.GetRect(), child.PivotOffset));
            Collect(child, into);
        }
    }
}
