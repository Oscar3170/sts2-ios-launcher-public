using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace STS2Mobile.GameHost;

/// <summary>
/// Portrait reflows for the main menu's run-setup screens. The game lays these screens out on its 1680 unit desktop canvas, so
/// each is drawn on a 1680 unit virtual canvas that fills the portrait width (the canvas the reference rules were measured
/// on) and laid out from the top of the safe band. This owner keeps that canvas fit, the per-screen error guard, the
/// captured controls and the dispatch; the screen compositions live with their own lifetimes:
/// <see cref="PortraitModeCardsLayout"/> owns the singleplayer and multiplayer-host mode cards,
/// <see cref="PortraitCharacterSelectLayout"/> owns character select, Blitz and Dread (roster, ascension and info panels,
/// confirm button, Dread blocks), and <see cref="PortraitCharacterArtLayout"/> owns the character art framed behind them. The
/// credits modal is the one menu screen here with nothing but its buttons to grow. Every edit is captured first so turning
/// Portrait Mode off restores the desktop layout live.
/// </summary>
internal sealed class PortraitMenuLayout
{
    private const string Ns = "MegaCrit.Sts2.Core.Nodes.Screens.";
    public const string CreditsTypeName = Ns + "Credits.NCreditsScreen";

    public static readonly HashSet<string> WatchedTypeNames = new(StringComparer.Ordinal)
    {
        PortraitModeCardsLayout.SingleplayerTypeName, PortraitModeCardsLayout.MultiplayerHostTypeName,
        PortraitCharacterSelectLayout.CharacterSelectTypeName, PortraitCharacterSelectLayout.BlitzTypeName,
        PortraitCharacterSelectLayout.DreadTypeName, CreditsTypeName,
    };

    /// <summary>The desktop canvas the game and the reference rules lay these screens out on, 1680 units across.</summary>
    public const float DesignWidth = 1680f;

    /// <summary>One menu screen's fitted canvas and safe band, passed to the screen owner that lays it out.</summary>
    private sealed class ScreenState
    {
        public string TypeName = "";
        public float Scale = 1f;
        public Rect2 Safe;
    }

    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private readonly Dictionary<ulong, ScreenState> _screens = new();
    private readonly PortraitModeCardsLayout _modeCards = new();
    private readonly PortraitCharacterSelectLayout _characterSelect = new();
    private readonly PortraitCharacterArtLayout _art = new();

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
                case PortraitModeCardsLayout.SingleplayerTypeName:
                case PortraitModeCardsLayout.MultiplayerHostTypeName:
                    _modeCards.Apply(screen, state.Safe, state.Scale);
                    break;
                case PortraitCharacterSelectLayout.CharacterSelectTypeName:
                    _characterSelect.Apply(screen, state.Safe, state.Scale, blitz: false);
                    _art.Apply(screen, "AnimatedBg");
                    break;
                case PortraitCharacterSelectLayout.BlitzTypeName:
                    _characterSelect.Apply(screen, state.Safe, state.Scale, blitz: true);
                    _art.Apply(screen, "AnimatedBg");
                    break;
                case PortraitCharacterSelectLayout.DreadTypeName:
                    _characterSelect.ApplyDread(screen, state.Safe, state.Scale);
                    _art.Apply(screen, "SubViewportContainer/SubViewport/AnimatedBg");
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
        // The per-screen plans and callbacks go first, then the art, then the focus links; each owner restores what it
        // wrote at the same points the menu always has.
        SwitchTrace.Note("menus mode reset");
        _modeCards.ResetPortrait();
        SwitchTrace.Note("menus character reset");
        _characterSelect.ResetPortrait();
        SwitchTrace.Note("menus art");
        _art.RestoreLandscape();
        SwitchTrace.Note("menus focus");
        _modeCards.RestoreFocus();
        _characterSelect.RestoreFocus();
        SwitchTrace.Note("menus pads");
        PortraitTargetPads.ReleaseAll();
        SwitchTrace.Note("menus text");
        PortraitMenuText.Release();
        SwitchTrace.Note("menus snapshots");
        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
        SwitchTrace.Note("menus controls");
        _modeCards.RestoreControls();
        _characterSelect.RestoreControls();
        _art.RestoreControls();
        SwitchTrace.Note("menus resting");
        // The info panel may slide again from wherever it is: give the game's own resting place back.
        _characterSelect.RestoreRestingPlaces();
        SwitchTrace.Note("menus done");
    }

    public void PruneDead()
    {
        foreach (ulong id in _snapshots.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _snapshots.Remove(id);
        foreach (ulong id in _screens.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _screens.Remove(id);
        _modeCards.PruneDead();
        _characterSelect.PruneDead();
        _art.PruneDead();
        PortraitTargetPads.PruneDead();
        PortraitMenuText.PruneDead();
    }

    private PortraitControlSnapshot Capture(Control control)
    {
        if (!_snapshots.TryGetValue(control.GetInstanceId(), out PortraitControlSnapshot? snap))
            _snapshots[control.GetInstanceId()] = snap = new PortraitControlSnapshot(control, captureVisible: false);
        return snap;
    }

    /// <summary>
    /// The credits modal's plain engine buttons at a finger's height. The screen keeps its own layout; only the button boxes
    /// grow (their art and text follow the box).
    /// </summary>
    private void GrowCreditsTargets(Control screen)
    {
        void Walk(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is not Control control || !GodotObject.IsInstanceValid(control)) continue;
                if (control is BaseButton && control.IsVisibleInTree() &&
                    control.CustomMinimumSize.Y < PortraitBands.MinTarget)
                {
                    Capture(control);
                    control.CustomMinimumSize = new Vector2(control.CustomMinimumSize.X, PortraitBands.MinTarget);
                }
                Walk(control);
            }
        }
        Walk(screen);
    }
}
