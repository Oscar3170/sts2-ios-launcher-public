using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// The portrait rest site: the act's wide rest-site art covers the whole canvas with the campfire and the seated characters
/// framed inside it, the question and the hovered option's copy are set at phone size, and the option row is pinned above
/// the bottom edge with 58 point buttons. The art is the game's own scene, so the fire keeps flickering and the characters
/// keep their rest animations; only the container and the camp pieces are reframed, never hidden.
/// <para>
/// Every transform is captured before its first change and put back when Portrait Mode is turned off: the background
/// container's own layout, each camp piece's place and scale, and the two covers this file adds to the room. The row and the
/// two texts are re-asserted each frame because the game rebuilds the options after an action and re-lays the copy out; the
/// art is framed once per canvas, so nothing moves while the rest is being spent.
/// </para>
/// </summary>
internal sealed partial class PortraitBetweenLayout
{
    public const string RestUnderlayName = "PortraitRestUnderlay";
    public const string RestWaterName = "PortraitRestWater";

    private const string RestBackgroundName = "BgContainer";
    private const string RestLightingName = "RestSiteLighting";
    private const string RestPrimaryBackgroundName = "RestSiteBG";
    private const string RestUnderdocksBackgroundName = "RestSiteBG2";
    private const string RestWaterSourceName = "RestSiteBG";

    /// <summary>One rest room's framed nodes, covers, the wrapped options row and the canvas they were framed for.</summary>
    private sealed class RestState
    {
        public Vector2 Canvas;
        public bool Framed;
        public bool Underlay;
        public bool HasAnchor;
        public RestPoint Anchor;
        public ulong UnderlayNode;
        public ulong WaterNode;
        public readonly Dictionary<ulong, RestFramed> Items = new();
        /// <summary>The choices row while its options are wrapped: the box this file gives it and the re-sort handler.</summary>
        public ulong Choices;
        public Action? Sorted;
        public Vector2 GridCanvas;
    }

    /// <summary>A node's own place, scale and pivot, before this file reframed it.</summary>
    private readonly struct RestFramed
    {
        public RestFramed(ulong id, Vector2 position, Vector2 scale, Vector2 pivot)
        {
            Id = id;
            Position = position;
            Scale = scale;
            Pivot = pivot;
        }

        public ulong Id { get; }
        public Vector2 Position { get; }
        public Vector2 Scale { get; }
        public Vector2 Pivot { get; }
    }

    private readonly Dictionary<ulong, RestState> _restStates = new();
    private readonly HashSet<ulong> _restStyledLabels = new();
    private readonly Dictionary<ulong, int> _restStyledText = new();
    private static Type? _restRoomType;
    private static FieldInfo? _restDescriptionTweenField;

    // ---- entry points from PortraitBetweenLayout ---------------------------------------------------------------

    /// <summary>The room was added: lay it out now, so the first frame is already the portrait one.</summary>
    private void TrackRestSite(Control room, float canvasWidth, float canvasHeight)
    {
        _restSites.Add(room.GetInstanceId());
        UpdateRestSitePortrait(room, new Vector2(canvasWidth, canvasHeight));
    }

    /// <summary>Each frame: the row and the two texts, and the art when its canvas changed.</summary>
    private void UpdateRestSitePortrait(Control room, Vector2 canvas)
    {
        ulong id = room.GetInstanceId();
        if (!_restStates.TryGetValue(id, out RestState? state))
        {
            state = new RestState();
            _restStates[id] = state;
        }
        float headerTop = LayoutRestChoices(room, state, canvas);
        LayoutRestText(room, canvas, headerTop);
        if (!state.Framed || !state.Canvas.IsEqualApprox(canvas)) ApplyRestArt(room, state, canvas);
    }

    private void PruneRestInstances()
    {
        foreach (ulong id in _restStates.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _restStates.Remove(id);
        _restStyledLabels.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        foreach (ulong id in _restStyledText.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _restStyledText.Remove(id);
    }

    private void RestoreRestLandscape()
    {
        foreach (RestState state in _restStates.Values)
        {
            DisconnectRestSort(state);
            foreach (RestFramed framed in state.Items.Values)
            {
                if (GodotObject.InstanceFromId(framed.Id) is not CanvasItem item || !GodotObject.IsInstanceValid(item)) continue;
                if (item is Control control)
                {
                    control.Position = framed.Position;
                    control.Scale = framed.Scale;
                    control.PivotOffset = framed.Pivot;
                }
                else if (item is Node2D node)
                {
                    node.Position = framed.Position;
                    node.Scale = framed.Scale;
                }
            }
            RemoveRestCover(state.UnderlayNode);
            RemoveRestCover(state.WaterNode);
        }
        _restStates.Clear();
        _restStyledLabels.Clear();
        _restStyledText.Clear();
    }

    // ---- the options row -----------------------------------------------------------------------------------------

    /// <summary>
    /// Lays the options out and returns the top the question stands above. Up to four options stay in the game's own row;
    /// more wrap into balanced rows of at most four, because a single row cannot keep a finger-sized button. The buttons are
    /// never moved: they stay the game's children, so its own rebuild, disable and focus code keeps working. Godot 4.5's
    /// <c>Container</c> emits <c>sort_children</c> after the box fit (<c>scene/gui/container.cpp</c>), so the handler this
    /// file connects re-places the buttons on that signal, after every native re-sort including the game's rebuild.
    /// </summary>
    private float LayoutRestChoices(Control room, RestState state, Vector2 canvas)
    {
        float pinnedTop = canvas.Y - PortraitRestMath.OptionsBottomInset;
        if (room.GetNodeOrNull<Container>("%ChoicesContainer") is not { } choices) return pinnedTop;
        int count = RestOptionCount(choices);
        if (count <= 0) return pinnedTop;
        if (PortraitRestMath.OptionsRowCount(count) <= 1)
        {
            DisconnectRestSort(state);
            for (int i = 0; i < choices.GetChildCount(); i++)
                if (choices.GetChild(i) is Control button && !button.IsQueuedForDeletion()) StyleRestOptionLabel(button);
            RestOptionsRow row = PortraitRestMath.OptionsRow(count, choices.GetCombinedMinimumSize().X, canvas.X, canvas.Y);
            bool placed = Mathf.IsEqualApprox(choices.Scale.X, row.Scale)
                && Mathf.IsEqualApprox(choices.Size.X, row.Width) && Mathf.IsEqualApprox(choices.Size.Y, row.Height)
                && Mathf.IsEqualApprox(choices.Position.X, row.X) && Mathf.IsEqualApprox(choices.Position.Y, row.Y)
                && choices.PivotOffset.LengthSquared() < 0.0001f
                && choices.AnchorLeft == 0f && choices.AnchorTop == 0f && choices.AnchorRight == 0f && choices.AnchorBottom == 0f;
            if (!placed)
            {
                Capture(choices, captureVisible: false);
                choices.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                choices.Size = new Vector2(row.Width, row.Height);
                choices.Position = new Vector2(row.X, row.Y);
                choices.PivotOffset = Vector2.Zero;
                choices.Scale = new Vector2(row.Scale, row.Scale);
            }
            return row.Y;
        }
        return LayoutRestRows(state, choices, count, canvas);
    }

    /// <summary>
    /// Gives the game's row the wrapped grid's box and returns its top. The row's own width stays as the game made it (one
    /// line of all its buttons), which is at least the widest wrapped row; only its height takes the grid's, so its rect
    /// covers every row. The buttons inside are placed by <see cref="PlaceRestGrid"/> after each native re-sort.
    /// </summary>
    private float LayoutRestRows(RestState state, Container choices, int count, Vector2 canvas)
    {
        state.GridCanvas = canvas;
        RestOptionsRow first = RestGridFirst(count, canvas);
        float height = RestGridHeightUnits(count);
        bool placed = Mathf.IsEqualApprox(choices.Scale.X, first.Scale)
            && Mathf.IsEqualApprox(choices.Position.X, first.X) && Mathf.IsEqualApprox(choices.Position.Y, first.Y)
            && Mathf.IsEqualApprox(choices.Size.X, first.Width) && Mathf.IsEqualApprox(choices.Size.Y, height)
            && choices.PivotOffset.LengthSquared() < 0.0001f
            && choices.AnchorLeft == 0f && choices.AnchorTop == 0f && choices.AnchorRight == 0f && choices.AnchorBottom == 0f;
        if (!placed)
        {
            Capture(choices, captureVisible: false);
            choices.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            choices.Size = new Vector2(first.Width, height);
            choices.Position = new Vector2(first.X, first.Y);
            choices.PivotOffset = Vector2.Zero;
            choices.Scale = new Vector2(first.Scale, first.Scale);
        }
        if (state.Sorted == null || state.Choices != choices.GetInstanceId())
        {
            DisconnectRestSort(state);
            state.Choices = choices.GetInstanceId();
            state.Sorted = () => PlaceRestGrid(state);
            choices.SortChildren += state.Sorted;
        }
        // The grid can change without a re-sort (a new canvas); placing it again is idempotent and allocates nothing.
        PlaceRestGrid(state);
        for (int i = 0; i < choices.GetChildCount(); i++)
            if (choices.GetChild(i) is Control button && !button.IsQueuedForDeletion()) StyleRestOptionLabel(button);
        return first.Y;
    }

    /// <summary>
    /// The <c>sort_children</c> handler: after the game's box has fitted its buttons into one line, place each in its grid
    /// cell. It runs during the sort, so it allocates nothing and writes only the rects that differ.
    /// </summary>
    private void PlaceRestGrid(RestState state)
    {
        if (GodotObject.InstanceFromId(state.Choices) is not Container choices || !GodotObject.IsInstanceValid(choices)) return;
        int total = choices.GetChildCount();
        int count = RestOptionCount(choices);
        if (PortraitRestMath.OptionsRowCount(count) <= 1) return;
        int rows = PortraitRestMath.OptionsRowCount(count);
        Span<RestOptionsRow> boxes = stackalloc RestOptionsRow[rows];
        int placedRows = PortraitRestMath.OptionsGrid(count, state.GridCanvas.X, state.GridCanvas.Y, boxes);
        float widest = boxes[0].Width;
        float pitch = PortraitRestMath.OptionsHeight + PortraitRestMath.OptionsRowGap;
        int index = 0;
        for (int row = 0; row < placedRows; row++)
        {
            int inRow = PortraitRestMath.OptionsInRow(count, row);
            float start = (widest - PortraitRestMath.NaturalRowWidth(inRow)) * 0.5f;
            float top = row * pitch;
            for (int column = 0; column < inRow; column++)
            {
                while (index < total && (choices.GetChild(index) is not Control live || live.IsQueuedForDeletion())) index++;
                if (index >= total) return;
                var button = (Control)choices.GetChild(index);
                index++;
                float x = start + column * (PortraitRestMath.NativeOptionWidth + PortraitRestMath.NativeOptionSeparation);
                if (!Mathf.IsEqualApprox(button.Position.X, x) || !Mathf.IsEqualApprox(button.Position.Y, top))
                    button.Position = new Vector2(x, top);
                if (!Mathf.IsEqualApprox(button.Size.X, PortraitRestMath.NativeOptionWidth)
                    || !Mathf.IsEqualApprox(button.Size.Y, PortraitRestMath.OptionsHeight))
                    button.Size = new Vector2(PortraitRestMath.NativeOptionWidth, PortraitRestMath.OptionsHeight);
            }
        }
    }

    /// <summary>The option buttons the game has in its row: its rebuild queues the old ones for deletion in the same frame.</summary>
    private static int RestOptionCount(Container choices)
    {
        int count = 0;
        for (int i = 0; i < choices.GetChildCount(); i++)
            if (choices.GetChild(i) is Control button && !button.IsQueuedForDeletion()) count++;
        return count;
    }

    /// <summary>The first wrapped row's box: where the row container goes and at what scale.</summary>
    private static RestOptionsRow RestGridFirst(int count, Vector2 canvas)
    {
        Span<RestOptionsRow> boxes = stackalloc RestOptionsRow[1];
        PortraitRestMath.OptionsGrid(count, canvas.X, canvas.Y, boxes);
        return boxes[0];
    }

    /// <summary>The wrapped grid's height in the buttons' own units, from the top row's box to the bottom row's box.</summary>
    private static float RestGridHeightUnits(int count) =>
        (PortraitRestMath.OptionsRowCount(count) - 1) * (PortraitRestMath.OptionsHeight + PortraitRestMath.OptionsRowGap)
        + PortraitRestMath.OptionsHeight;

    /// <summary>Stops re-placing the row's buttons and asks the game's box to lay them out again as one line.</summary>
    private static void DisconnectRestSort(RestState state)
    {
        if (state.Sorted == null) return;
        if (GodotObject.InstanceFromId(state.Choices) is Container container && GodotObject.IsInstanceValid(container))
        {
            container.SortChildren -= state.Sorted;
            container.QueueSort();
        }
        state.Sorted = null;
        state.Choices = 0;
    }

    /// <summary>The label under an option is set in the button's own units, so it grows with the row and reads large enough for a phone.</summary>
    private void StyleRestOptionLabel(Control button)
    {
        if (button.GetNodeOrNull<Label>("Label") is not { } label) return;
        if (!_restStyledLabels.Add(label.GetInstanceId())) return;
        SetLabelFontSize(label, PortraitRestMath.OptionLabelFontSize);
        ExtensionLimits(label, PortraitRestMath.OptionLabelFontSize);
    }

    // ---- the question and the copy -------------------------------------------------------------------------------

    private void LayoutRestText(Control room, Vector2 canvas, float rowTop)
    {
        if (room.GetNodeOrNull<Control>("%Header") is { } header)
            PlaceRestText(header, PortraitRestMath.Header(rowTop, canvas.X),
                PortraitRestMath.HeaderFontSize, rich: false, move: true);
        if (room.GetNodeOrNull<Control>("%Description") is { } description)
        {
            RestTextBlock block = PortraitRestMath.Description(canvas.X, canvas.Y);
            // The game slides the copy down while a card is being picked; hold its own y until that tween is done.
            bool moving = !Mathf.IsEqualApprox(description.Position.Y, block.Y)
                && RestDescriptionTween(room) is { } tween && tween.IsValid() && tween.IsRunning();
            PlaceRestText(description, block, PortraitRestMath.DescriptionFontSize, rich: true, move: !moving);
        }
    }

    private void PlaceRestText(Control label, RestTextBlock block, int font, bool rich, bool move)
    {
        ulong id = label.GetInstanceId();
        if (!_restStyledText.TryGetValue(id, out int styled) || styled != font)
        {
            if (rich) SetRichFontSize(label, font);
            else SetLabelFontSize(label, font);
            ExtensionLimits(label, font);
            _restStyledText[id] = font;
        }
        bool topLeft = label.AnchorLeft == 0f && label.AnchorTop == 0f && label.AnchorRight == 0f && label.AnchorBottom == 0f;
        float targetY = move || !topLeft ? block.Y : label.Position.Y;
        bool placed = topLeft
            && Mathf.IsEqualApprox(label.Position.X, block.X) && Mathf.IsEqualApprox(label.Position.Y, targetY)
            && Mathf.IsEqualApprox(label.Size.X, block.Width) && Mathf.IsEqualApprox(label.Size.Y, block.Height);
        if (placed) return;
        Capture(label, captureVisible: false);
        label.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        label.Size = new Vector2(block.Width, block.Height);
        label.Position = new Vector2(block.X, targetY);
    }

    private static Tween? RestDescriptionTween(Control room)
    {
        Type type = room.GetType();
        if (type != _restRoomType)
        {
            _restRoomType = type;
            _restDescriptionTweenField = type.GetField("_descriptionPositionTween", BindingFlags.Instance | BindingFlags.NonPublic);
        }
        return _restDescriptionTweenField?.GetValue(room) as Tween;
    }

    // ---- the act's rest-site art ---------------------------------------------------------------------------------

    private void ApplyRestArt(Control room, RestState state, Vector2 canvas)
    {
        if (room.GetNodeOrNull<Control>(RestBackgroundName) is not { } bg) return;
        Control? art = null;
        foreach (Node child in bg.GetChildren())
        {
            if (child is Control candidate && !IsRestCharacter(candidate))
            {
                art = candidate;
                break;
            }
        }
        if (art == null) return;

        bool underdocks = CurrentActName() == "underdocks";
        TextureRect? background = art.GetNodeOrNull<TextureRect>(underdocks ? RestUnderdocksBackgroundName : RestPrimaryBackgroundName)
            ?? art.GetNodeOrNull<TextureRect>(RestPrimaryBackgroundName);
        if (background?.Texture is { } texture && !state.Underlay)
        {
            // The native 16:9 sheet cannot reach the phone's top and bottom bands on every act, so the act's own picture also
            // covers the room behind it. Underdocks draws its water in a second layer, which gets the same treatment.
            state.UnderlayNode = AddRestCover(room, texture, RestUnderlayName, underdocks ? 1 : 0, null, canvas);
            state.Underlay = true;
        }
        if (underdocks && state.WaterNode == 0 && art.GetNodeOrNull<TextureRect>(RestWaterSourceName) is { Texture: not null } water)
            state.WaterNode = AddRestCover(room, water.Texture, RestWaterName, 0, water.Material, canvas);
        ResizeRestCover(state.UnderlayNode, canvas);
        ResizeRestCover(state.WaterNode, canvas);

        // The container's own offsets are the game's room centre reflected; keep them, because the next pass (another
        // canvas) reads a container this file has already moved.
        if (!state.HasAnchor)
        {
            state.Anchor = PortraitRestMath.ArtAnchor(bg.OffsetLeft, bg.OffsetTop);
            state.HasAnchor = true;
        }
        RestPoint anchor = state.Anchor;
        RestCover cover = PortraitRestMath.ArtCover(canvas.X, canvas.Y, anchor);
        Capture(bg, captureVisible: false);
        bg.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        bg.PivotOffset = Vector2.Zero;
        bg.Scale = new Vector2(cover.Scale, cover.Scale);
        bg.Position = new Vector2(cover.X, cover.Y);

        RestPoint focus = PortraitRestMath.ArtFocus(anchor);
        var anchorPoint = new Vector2(anchor.X, anchor.Y);
        var focusPoint = new Vector2(focus.X, focus.Y);
        foreach (Node child in bg.GetChildren())
            if (child is Control character && IsRestCharacter(character))
                FrameRestItem(character, anchorPoint, focusPoint, state);

        // The camp pieces are drawn inside the act's scene, whose own transform may offset them; frame them in its space.
        Transform2D inverse = art.GetTransform().AffineInverse();
        Vector2 artAnchor = inverse * anchorPoint;
        Vector2 artFocus = inverse * focusPoint;
        foreach (Node child in art.GetChildren())
            if (child is CanvasItem piece && PortraitRestMath.IsCampPiece(piece.Name.ToString()))
                FrameRestItem(piece, artAnchor, artFocus, state);
        if (art.GetNodeOrNull<Control>(RestLightingName) is { } lighting)
        {
            inverse = lighting.GetTransform().AffineInverse();
            Vector2 lightAnchor = inverse * artAnchor;
            Vector2 lightFocus = inverse * artFocus;
            foreach (Node child in lighting.GetChildren())
                if (child is CanvasItem piece && PortraitRestMath.IsCampPiece(piece.Name.ToString()))
                    FrameRestItem(piece, lightAnchor, lightFocus, state);
        }

        state.Framed = true;
        state.Canvas = canvas;
    }

    /// <summary>
    /// Frames one camp piece around the fire: the piece's own place and scale are kept for landscape, and it is drawn at
    /// <see cref="PortraitRestMath.RestZoom"/> of them around the focus carried onto the canvas centre. A piece the game
    /// animates itself keeps that animation: its own tween writes the same property, so the last write wins and the rest
    /// keeps its fire.
    /// </summary>
    private void FrameRestItem(CanvasItem item, Vector2 anchor, Vector2 focus, RestState state)
    {
        Vector2 position;
        Vector2 scale;
        Vector2 pivot = Vector2.Zero;
        if (item is Control controlItem)
        {
            position = controlItem.Position;
            scale = controlItem.Scale;
            pivot = controlItem.PivotOffset;
        }
        else if (item is Node2D node)
        {
            position = node.Position;
            scale = node.Scale;
        }
        else
        {
            return;
        }
        ulong id = item.GetInstanceId();
        if (!state.Items.TryGetValue(id, out RestFramed framed))
        {
            framed = new RestFramed(id, position, scale, pivot);
            state.Items[id] = framed;
        }
        RestPoint target = PortraitRestMath.FramePosition(
            new RestPoint(framed.Position.X, framed.Position.Y),
            new RestPoint(framed.Pivot.X, framed.Pivot.Y),
            new RestPoint(anchor.X, anchor.Y),
            new RestPoint(focus.X, focus.Y),
            PortraitRestMath.RestZoom);
        var drawn = new Vector2(target.X, target.Y);
        var size = new Vector2(framed.Scale.X * PortraitRestMath.RestZoom, framed.Scale.Y * PortraitRestMath.RestZoom);
        if (item is Control controlTarget)
        {
            controlTarget.Position = drawn;
            controlTarget.Scale = size;
        }
        else if (item is Node2D nodeTarget)
        {
            nodeTarget.Position = drawn;
            nodeTarget.Scale = size;
        }
    }

    private static bool IsRestCharacter(Control control) =>
        control.Name.ToString().StartsWith("Character", StringComparison.Ordinal);

    private static ulong AddRestCover(Control host, Texture2D texture, string name, int index, Material? material, Vector2 canvas)
    {
        var cover = new TextureRect
        {
            Name = name,
            Texture = texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ZIndex = -1,
            Position = Vector2.Zero,
            Size = canvas,
        };
        if (material != null) cover.Material = material;
        host.AddChild(cover);
        host.MoveChild(cover, Math.Min(index, host.GetChildCount() - 1));
        return cover.GetInstanceId();
    }

    private static void ResizeRestCover(ulong id, Vector2 canvas)
    {
        if (id == 0 || GodotObject.InstanceFromId(id) is not Control cover || !GodotObject.IsInstanceValid(cover)) return;
        if (cover.Size.IsEqualApprox(canvas)) return;
        cover.Position = Vector2.Zero;
        cover.Size = canvas;
    }

    private static void RemoveRestCover(ulong id)
    {
        if (id == 0 || GodotObject.InstanceFromId(id) is not Control cover || !GodotObject.IsInstanceValid(cover)) return;
        cover.GetParent()?.RemoveChild(cover);
        cover.QueueFree();
    }
}
