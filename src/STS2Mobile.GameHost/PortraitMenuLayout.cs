using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;

namespace STS2Mobile.GameHost;

/// <summary>
/// Portrait reflows for the main menu's run-setup screens. The game lays these screens out on its 1680 unit desktop canvas, so
/// each is drawn on a 1680 unit virtual canvas that fills the portrait width (the canvas the reference rules were measured
/// on) and laid out from the top of the safe band: the singleplayer mode cards wrap two to a row with the reference card
/// interior, character select (and Blitz and Dread) lay the roster out four to a row and frame the character art over the
/// screen (a separate backdrop scene where the character has one, otherwise a figure-biased cover), and the info panel,
/// ascension panel and confirm button take their measured places. Every edit is captured first so turning Portrait Mode off
/// restores the desktop layout live.
/// </summary>
internal sealed partial class PortraitMenuLayout
{
    private const string Ns = "MegaCrit.Sts2.Core.Nodes.Screens.";
    public const string SingleplayerTypeName = Ns + "MainMenu.NSingleplayerSubmenu";
    public const string MultiplayerHostTypeName = Ns + "MainMenu.NMultiplayerHostSubmenu";
    public const string CharacterSelectTypeName = Ns + "CharacterSelect.NCharacterSelectScreen";
    public const string BlitzTypeName = Ns + "Blitz.NBlitzScreen";
    public const string DreadTypeName = Ns + "Dread.NDreadScreen";
    public const string CreditsTypeName = Ns + "Credits.NCreditsScreen";

    public static readonly HashSet<string> WatchedTypeNames = new(StringComparer.Ordinal)
    {
        SingleplayerTypeName, MultiplayerHostTypeName, CharacterSelectTypeName, BlitzTypeName, DreadTypeName, CreditsTypeName,
    };

    /// <summary>The desktop canvas the game and the reference rules lay these screens out on, 1680 units across.</summary>
    public const float DesignWidth = 1680f;

    private sealed class ScreenState
    {
        public string TypeName = "";
        public float Scale = 1f;
        public Rect2 Safe;
        public bool ConfirmScaled;
        public bool ConfirmPlaced;
        public bool InfoPlaced;
        public Container? Grid;
        public Action? GridSort;
        public BoxContainer? ModeRow;
        public Action? ModeSort;
        public int FocusCount = -1;

        // Resolved scene nodes and the roster/focus plans, so the per-frame pass does no path lookups or LINQ.
        public Control? GridControl;
        public Control? ArtContainer;
        public readonly Dictionary<string, Control?> Named = new(StringComparer.Ordinal);
        public readonly List<Control> Roster = new();
        public int RosterChildren = -1;
        public Vector2 RosterCell;
        public bool FocusDirty = true;
        public readonly Dictionary<ulong, NodePath[]> FocusPaths = new();
        public readonly HashSet<ulong> ScaledHeaders = new();

        /// <summary>Forgets what only holds while portrait is on; the snapshots put the desktop layout back.</summary>
        public void ResetPortrait()
        {
            if (GridSort != null && GodotObject.IsInstanceValid(Grid)) Grid.SortChildren -= GridSort;
            GridSort = null;
            Grid = null;
            if (ModeSort != null && GodotObject.IsInstanceValid(ModeRow)) ModeRow!.SortChildren -= ModeSort;
            ModeSort = null;
            ModeRow = null;
            ConfirmScaled = false;
            ConfirmPlaced = false;
            InfoPlaced = false;
            FocusCount = -1;
            GridControl = ArtContainer = null;
            Named.Clear();
            Roster.Clear();
            RosterChildren = -1;
            RosterCell = Vector2.Zero;
            FocusDirty = true;
            FocusPaths.Clear();
            ScaledHeaders.Clear();
        }
    }

    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private readonly Dictionary<ulong, ScreenState> _screens = new();

    public void ApplyNode(Control screen, string typeName, float capHeight, Vector2 canvas)
    {
        ScreenState state = GetState(screen, typeName);
        Layout(screen, state, capHeight, canvas);
        PortraitTargetPads.Ensure(screen);
    }

    public void OnProcessFrame(Window root, float capHeight)
    {
        Vector2 canvas = root.GetVisibleRect().Size;
        foreach ((ulong id, ScreenState state) in _screens)
        {
            if (GodotObject.InstanceFromId(id) is not Control screen || !GodotObject.IsInstanceValid(screen) || !screen.IsVisibleInTree())
                continue;
            Layout(screen, state, capHeight, canvas);
            PortraitTargetPads.Ensure(screen);
        }
    }

    private ScreenState GetState(Control screen, string typeName)
    {
        ulong id = screen.GetInstanceId();
        if (!_screens.TryGetValue(id, out ScreenState? state)) _screens[id] = state = new ScreenState();
        state.TypeName = typeName;
        return state;
    }

    private readonly HashSet<string> _reportedLayoutErrors = new();

    private void Layout(Control screen, ScreenState state, float capHeight, Vector2 canvas)
    {
        try
        {
            if (state.TypeName == CreditsTypeName)
            {
                // The credits are a modal laid out by the game; only its two plain buttons are too short for a finger.
                GrowCreditsTargets(screen);
                return;
            }
            Fit(screen, state, capHeight, canvas);
            switch (state.TypeName)
            {
                case SingleplayerTypeName:
                case MultiplayerHostTypeName:
                    LayoutModeCards(screen, state);
                    break;
                case CharacterSelectTypeName:
                case BlitzTypeName:
                    LayoutCharacterSelect(screen, state);
                    FrameArt(screen, state, "AnimatedBg");
                    break;
                case DreadTypeName:
                    LayoutDread(screen, state);
                    FrameArt(screen, state, "SubViewportContainer/SubViewport/AnimatedBg");
                    break;
            }
        }
        catch (Exception error)
        {
            // A layout fault must not take the game down: log the first error per screen type and leave the screen usable.
            if (_reportedLayoutErrors.Add(state.TypeName))
                GD.PrintErr($"Portrait menu: {state.TypeName} layout stopped: {error}");
        }
    }

    /// <summary>
    /// Draws the screen on a virtual canvas <see cref="DesignWidth"/> units across, filling the phone's canvas, and keeps the
    /// safe band in its units (the cap over the notch at the top, the canvas's lower edge at the bottom). The reference
    /// numbers are all in this canvas's units.
    /// </summary>
    private void Fit(Control screen, ScreenState state, float capHeight, Vector2 canvas)
    {
        float scale = Math.Min(1f, canvas.X / DesignWidth);
        if (scale < 0.01f) scale = 1f;
        float localHeight = canvas.Y / scale;
        var size = new Vector2(DesignWidth, localHeight);
        Vector2 parentOrigin = screen.GetParent() is Control parent ? parent.GetGlobalTransformWithCanvas().Origin : Vector2.Zero;
        Vector2 position = -parentOrigin;

        Capture(screen);
        if (screen.AnchorLeft != 0f || screen.AnchorTop != 0f || screen.AnchorRight != 0f || screen.AnchorBottom != 0f)
        {
            screen.AnchorLeft = 0f;
            screen.AnchorTop = 0f;
            screen.AnchorRight = 0f;
            screen.AnchorBottom = 0f;
        }
        if (screen.PivotOffset != Vector2.Zero) screen.PivotOffset = Vector2.Zero;
        var drawScale = new Vector2(scale, scale);
        if (screen.Scale.DistanceSquaredTo(drawScale) > 0.000001f) screen.Scale = drawScale;
        if (screen.Position.DistanceSquaredTo(position) > 0.25f) screen.Position = position;
        if (screen.Size.DistanceSquaredTo(size) > 0.25f) screen.Size = size;

        state.Scale = scale;
        state.Safe = new Rect2(0f, capHeight / scale, DesignWidth, Math.Max(1f, (canvas.Y - capHeight) / scale));
    }

    public void RestoreLandscape()
    {
        foreach ((ulong id, ScreenState state) in _screens) state.ResetPortrait();
        RestoreArt();
        RestoreFigures();
        RestoreFocus();
        PortraitTargetPads.ReleaseAll();
        PortraitMenuText.Release();
        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
        // The info panel may slide again from wherever it is: give the game's own resting place back.
        foreach ((ulong id, ScreenState _) in _screens)
            if (GodotObject.InstanceFromId(id) is Control screen && GodotObject.IsInstanceValid(screen) &&
                screen.GetNodeOrNull<Control>("InfoPanel") is { } info)
                SetField(screen, "_infoPanelPosFinalVal", info.Position);
    }

    public void PruneDead()
    {
        foreach (ulong id in _snapshots.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _snapshots.Remove(id);
        foreach (ulong id in _screens.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _screens.Remove(id);
        PruneFocus();
        PruneArt();
        PortraitTargetPads.PruneDead();
        PortraitMenuText.PruneDead();
    }

    /// <summary>A scene node under this screen, resolved once (a null is final for these scenes; ResetPortrait clears it).</summary>
    private static Control? Node(Control screen, ScreenState state, string path)
    {
        if (state.Named.TryGetValue(path, out Control? cached))
        {
            if (cached == null || GodotObject.IsInstanceValid(cached)) return cached;
            state.Named.Remove(path);
        }
        Control? found = screen.GetNodeOrNull<Control>(path);
        state.Named[path] = found;
        return found;
    }

    /// <summary>A node below a cached parent, resolved once and kept in the state's name cache.</summary>
    private static Control? Node(Control parent, ScreenState state, string path, string cacheKey)
    {
        if (state.Named.TryGetValue(cacheKey, out Control? cached))
        {
            if (cached == null || GodotObject.IsInstanceValid(cached)) return cached;
            state.Named.Remove(cacheKey);
        }
        Control? found = parent.GetNodeOrNull<Control>(path);
        state.Named[cacheKey] = found;
        return found;
    }

    private PortraitControlSnapshot Capture(Control control, bool captureVisible = false)
    {
        if (!_snapshots.TryGetValue(control.GetInstanceId(), out PortraitControlSnapshot? snap))
        {
            _snapshots[control.GetInstanceId()] = snap = new PortraitControlSnapshot(control, captureVisible);
        }
        else if (captureVisible && !snap.CaptureVisible)
        {
            snap.RecordInitialVisibility(control.Visible);
        }
        return snap;
    }

    /// <summary>
    /// Keeps the back button's input on top: it becomes the last child of its own parent, which the GUI hit test asks first.
    /// Moving a node within its own parent does not exit the tree, so its connections and hotkeys stay. The position belongs
    /// to the navigation pad pass (the single writer for back buttons).
    /// </summary>
    private static void EnsureBackOnTop(Control screen, ScreenState state)
    {
        Control? back = Node(screen, state, "BackButton") ?? Node(screen, state, "%BackButton");
        if (back?.GetParent() is Control parent && !PhoneHitArea.IsContainer(parent) &&
            back.GetIndex() != parent.GetChildCount() - 1)
            parent.MoveChild(back, parent.GetChildCount() - 1);
    }

    // ---- shared reflection ------------------------------------------------------------------------------------------------

    private static readonly Dictionary<(Type, string), FieldInfo?> Fields = new();

    private static FieldInfo? Field(object target, string name)
    {
        Type type = target.GetType();
        if (!Fields.TryGetValue((type, name), out FieldInfo? field))
            Fields[(type, name)] = field = FindField(type, name);
        return field;
    }

    private static object? GetField(object target, string name) => Field(target, name)?.GetValue(target);

    private static void SetField(object target, string name, object? value) => Field(target, name)?.SetValue(target, value);

    private static FieldInfo? FindField(Type? type, string name)
    {
        for (; type != null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } field)
                return field;
        return null;
    }

    private static bool TweenRunning(object owner, string field) =>
        GetField(owner, field) is Tween tween && tween.IsValid() && tween.IsRunning();

    /// <summary>Puts a control at a rectangle of its parent with the top left anchor, changing only what differs.</summary>
    private void Place(Control? control, float x, float y, float w, float h)
    {
        if (control == null) return;
        Capture(control);
        if (control.AnchorLeft != 0f || control.AnchorTop != 0f || control.AnchorRight != 0f || control.AnchorBottom != 0f)
        {
            control.AnchorLeft = 0f;
            control.AnchorTop = 0f;
            control.AnchorRight = 0f;
            control.AnchorBottom = 0f;
        }
        if (MathF.Abs(control.OffsetLeft - x) > 0.5f) control.OffsetLeft = x;
        if (MathF.Abs(control.OffsetTop - y) > 0.5f) control.OffsetTop = y;
        if (MathF.Abs(control.OffsetRight - (x + w)) > 0.5f) control.OffsetRight = x + w;
        if (MathF.Abs(control.OffsetBottom - (y + h)) > 0.5f) control.OffsetBottom = y + h;
    }

    /// <summary>Sets a control's scale and pivot, changing only what differs (a native hover tween may own the scale).</summary>
    private static void SetScale(Control control, Vector2 scale, Vector2 pivot)
    {
        if (control.PivotOffset.DistanceSquaredTo(pivot) > 0.25f) control.PivotOffset = pivot;
        if (control.Scale.DistanceSquaredTo(scale) > 0.000001f) control.Scale = scale;
    }

    private static void SetMinSize(Control control, Vector2 size)
    {
        if (control.CustomMinimumSize.DistanceSquaredTo(size) > 0.25f) control.CustomMinimumSize = size;
    }

    // ---- focus neighbours -------------------------------------------------------------------------------------------------

    private readonly List<(ulong Id, NodePath? Left, NodePath? Right, NodePath? Top, NodePath? Bottom)> _focus = new();

    private void SaveFocus(Control button)
    {
        ulong id = button.GetInstanceId();
        for (int i = 0; i < _focus.Count; i++)
            if (_focus[i].Id == id) return;
        _focus.Add((id, button.FocusNeighborLeft, button.FocusNeighborRight, button.FocusNeighborTop, button.FocusNeighborBottom));
    }

    private void RestoreFocus()
    {
        foreach ((ulong id, NodePath? left, NodePath? right, NodePath? top, NodePath? bottom) in _focus)
        {
            if (GodotObject.InstanceFromId(id) is not Control button || !GodotObject.IsInstanceValid(button)) continue;
            if (left is { } l) button.FocusNeighborLeft = l;
            if (right is { } r) button.FocusNeighborRight = r;
            if (top is { } t) button.FocusNeighborTop = t;
            if (bottom is { } b) button.FocusNeighborBottom = b;
        }
        _focus.Clear();
    }

    private void PruneFocus() =>
        _focus.RemoveAll(entry => !GodotObject.IsInstanceIdValid(entry.Id));
}
