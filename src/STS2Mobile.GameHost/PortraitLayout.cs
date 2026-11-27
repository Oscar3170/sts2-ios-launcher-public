using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// Live iOS orientation switching and portrait reflow for the game's main menu, settings screen, top bar and 2-row relic strip
/// Every control touched in portrait has its desktop layout captured before the first change so turning Portrait Mode
/// off restores the original landscape layout without restarting the game.
/// </summary>
internal sealed class PortraitLayout
{
    private const string EarlyAccessDisclaimerTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NEarlyAccessDisclaimer";
    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private readonly PortraitCombatLayout _combat = new();
    private readonly PortraitBetweenLayout _between = new();
    private readonly PortraitSelectionPreview _previews = new();
    private readonly PortraitScreenFit _fit = new();
    private readonly PortraitMenuLayout _menus = new();
    private readonly PortraitMainMenuLayout _mainMenu = new();
    private readonly PortraitSettingsLayout _settings = new();
    private readonly PortraitNavigationPads _navPads = new();
    private readonly PortraitOverlayLayout _overlays = new();
    private readonly PortraitRunHeader _header;
    private SceneTree? _tree;
    private bool _applyQueued;
    private int _switchGeneration;

    public PortraitLayout() => _header = new PortraitRunHeader(QueueApply);

    // True from the first portrait layout until landscape has been put back after Portrait Mode is turned off. While it is
    // false nothing here writes to the game, so landscape with Portrait Mode never on is the game's own.
    private bool _needsRestore;

    public void Attach(SceneTree tree, bool initialPortrait)
    {
        _tree = tree;
        SafeAreaCanvas.SetPortrait(initialPortrait);
        tree.Root.SizeChanged += OnRootSizeChanged;
        tree.NodeAdded += OnNodeAdded;
        tree.ProcessFrame += OnProcessFrame;
        _combat.Attach();
        _header.Attach(tree);
        _navPads.Attach();
        _settings.Attach(tree);
        PortraitHandLayout.SetFlatHandListener(ApplyImmediate);
        PortraitGridWindow.Attach();
        QueueApply();
    }

    public void Detach()
    {
        if (_tree == null) return;
        PortraitHandLayout.SetFlatHandListener(null);
        PortraitGridWindow.Detach();
        _combat.Detach();
        _header.Detach();
        _navPads.Detach();
        _settings.Detach();
        _tree.Root.SizeChanged -= OnRootSizeChanged;
        _tree.NodeAdded -= OnNodeAdded;
        _tree.ProcessFrame -= OnProcessFrame;
        _tree = null;
    }

    public void Apply(SceneTree tree, bool portrait)
    {
        _tree = tree;
        SafeAreaCanvas.SetPortrait(portrait);
        ScreenOrientation.SetDeferEdges(true);
        ScreenOrientation.Set(landscape: !portrait);
        UpdateWindowCanvas(tree.Root);
        ApplyImmediate();
        _ = FollowGeometryRotationAsync(tree, portrait, ++_switchGeneration);
    }

    private async Task FollowGeometryRotationAsync(SceneTree tree, bool portrait, int generation)
    {
        for (int frame = 0; frame < 60; frame++)
        {
            if (_switchGeneration != generation || !GodotObject.IsInstanceValid(tree.Root)) return;
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (_switchGeneration != generation || !GodotObject.IsInstanceValid(tree.Root)) return;
            Vector2I size = DisplayServer.WindowGetSize();
            bool rotated = portrait ? size.Y >= size.X : size.X >= size.Y;
            if (rotated && frame >= 2)
            {
                UpdateWindowCanvas(tree.Root);
                ApplyImmediate();
                FinishRestore(portrait);
                GD.Print($"Orientation switched live to {(portrait ? "portrait" : "landscape")}: window {size.X}x{size.Y}, canvas {tree.Root.ContentScaleSize.X}x{tree.Root.ContentScaleSize.Y}.");
                if (ScreenOrientation.GestureState() is { } gestures) GD.Print($"Edge gestures: {gestures}.");
                return;
            }
        }
        UpdateWindowCanvas(tree.Root);
        ApplyImmediate();
        FinishRestore(portrait);
    }

    /// <summary>The window has turned back to landscape and the layout was put back at its final size: nothing is left to restore.</summary>
    private void FinishRestore(bool portrait)
    {
        if (portrait || SafeAreaCanvas.IsPortrait || !_needsRestore) return;
        _needsRestore = false;
        GD.Print("Portrait layout: landscape restored; the game's own layout from here.");
    }

    private static void UpdateWindowCanvas(Window root)
    {
        Vector2I size = DisplayServer.WindowGetSize();
        long aspect = SafeAreaCanvas.Layout(size.X, size.Y, out int cw, out int ch);
        root.ContentScaleAspect = (Window.ContentScaleAspectEnum)aspect;
        root.ContentScaleSize = new Vector2I(cw, ch);
    }

    private void OnRootSizeChanged() => QueueApply();

    private void OnNodeAdded(Node node)
    {
        _overlays.Track(node);
        // An ancient's rows are sized as they are added: the game measures them for its slide in the same frame.
        if (SafeAreaCanvas.IsPortrait && node is Control added && _between.LayoutOnAdd(added)) _needsRestore = true;
        if (node is NMainMenu or NSettingsScreen)
        {
            QueueApply();
            return;
        }
        string? fullName = node.GetType().FullName;
        if (node is Control navButton && PortraitNavigationPads.Handles(fullName)) _navPads.Track(navButton);
        // The combat-rules FTUE is the one node worth searching the scene tree for again; the combat layout remembers it.
        _combat.NoteNodeAdded(fullName);
        if (PortraitRunHeader.Handles(fullName) ||
            fullName is EarlyAccessDisclaimerTypeName or PortraitCombatLayout.CombatRoomTypeName ||
            (fullName != null && (PortraitBetweenLayout.WatchedTypeNames.Contains(fullName) ||
             PortraitSelectionPreview.WatchedTypeNames.Contains(fullName) || PortraitScreenFit.Handles(fullName) ||
             PortraitMenuLayout.WatchedTypeNames.Contains(fullName) || PortraitOverlayLayout.WatchedTypeNames.Contains(fullName) ||
             PortraitSettingsLayout.WatchedTypeNames.Contains(fullName))))
            QueueApply();
    }

    private void OnProcessFrame()
    {
        if (_tree == null || !SafeAreaCanvas.IsPortrait || !GodotObject.IsInstanceValid(_tree.Root))
            return;
        _needsRestore = true;
        float capHeight = ComputeCapHeight(_tree.Root);
        _combat.OnProcessFrame(_tree, capHeight);
        _previews.OnProcessFrame(_tree.Root, capHeight);
        _between.OnProcessFrame(_tree.Root, capHeight);
        _fit.OnProcessFrame(_tree.Root, capHeight);
        _menus.OnProcessFrame(_tree.Root, capHeight);
        _mainMenu.OnProcessFrame(_tree.Root, capHeight);
        _settings.OnProcessFrame(_tree.Root, capHeight);
        _navPads.OnProcessFrame();
        _overlays.OnProcessFrame(_tree.Root);
    }

    internal static float ComputeCapHeight(Window root)
    {
        float canvasHeight = Math.Max(root.GetVisibleRect().Size.Y, SafeAreaCanvas.MinPortraitHeight);
        return PortraitHeaderMath.CapHeight(SafeAreaCanvas.HasTopInset, SafeAreaCanvas.TopInset(), canvasHeight);
    }

    /// <summary>In a run the top bar stays over fitted screens such as the pause menu, so their band starts below it.</summary>
    private static float FitBandTop(string typeName, float capHeight) =>
        PortraitScreenFit.InRunScreens.Contains(typeName) ? capHeight + PortraitBands.TopBarHeight : capHeight;

    /// <summary>
    /// Asks for one layout pass after the current scene setup. Structural changes (the relic clip) must not run from a
    /// <c>Ready</c>/<c>NodeAdded</c> callback while an ancestor is still adding children: Godot refuses the move then.
    /// </summary>
    internal void QueueApply()
    {
        if (_applyQueued || _tree == null) return;
        _applyQueued = true;
        Callable.From(ApplyDeferred).CallDeferred();
    }

    private void ApplyDeferred()
    {
        _applyQueued = false;
        ApplyImmediate();
    }

    public void ApplyImmediate()
    {
        if (_tree == null || !GodotObject.IsInstanceValid(_tree.Root)) return;
        PruneDeadInstances();
        bool portrait = SafeAreaCanvas.IsPortrait;
        if (!portrait)
        {
            // Landscape that was never laid out in portrait (or has been put back since) is left to the game.
            if (_needsRestore) RestoreLandscape(_tree.Root);
            return;
        }
        _needsRestore = true;
        Vector2 canvasSize = _tree.Root.GetVisibleRect().Size;
        float canvasHeight = Math.Max(canvasSize.Y, SafeAreaCanvas.MinPortraitHeight);
        float capHeight = ComputeCapHeight(_tree.Root);
        Vector2 canvas = new(canvasSize.X, canvasHeight);

        foreach (Node node in Traverse(_tree.Root))
        {
            if (node is NMainMenu mainMenu) _mainMenu.Apply(mainMenu, capHeight, canvas);
            else if (node is NSettingsScreen settings) _settings.ApplyNode(settings, capHeight);
            else if (node is Control control)
            {
                string? typeName = control.GetType().FullName;
                if (PortraitNavigationPads.Handles(typeName)) { _navPads.Track(control); continue; }
                if (PortraitRunHeader.Handles(typeName)) _header.ApplyNode(control, typeName, capHeight, canvas);
                else if (typeName == EarlyAccessDisclaimerTypeName) LayoutEarlyAccessPortrait(control);
                else if (typeName == PortraitSettingsLayout.FeedbackScreenTypeName) _settings.ApplyFeedback(control, capHeight);
                else if (typeName == PortraitCombatLayout.CombatRoomTypeName) _combat.ApplyRoomImmediate(control, capHeight, canvasHeight);
                else if (typeName != null && PortraitSelectionPreview.WatchedTypeNames.Contains(typeName))
                    _previews.ApplyNode(control, typeName, capHeight, canvas);
                else if (typeName != null && PortraitBetweenLayout.WatchedTypeNames.Contains(typeName))
                    _between.ApplyNode(control, typeName, capHeight, canvasHeight, _tree.Root.GetVisibleRect().Size.X);
                else if (typeName != null && PortraitScreenFit.Handles(typeName))
                    _fit.ApplyNode(control, typeName, FitBandTop(typeName, capHeight), _tree.Root.GetVisibleRect().Size);
                else if (typeName != null && PortraitMenuLayout.WatchedTypeNames.Contains(typeName))
                    _menus.ApplyNode(control, typeName, capHeight, _tree.Root.GetVisibleRect().Size);
                else if (typeName != null && PortraitOverlayLayout.WatchedTypeNames.Contains(typeName))
                    _overlays.ApplyNode(control, typeName, capHeight, _tree.Root.GetVisibleRect().Size);
            }
        }
        _header.FinishApply();
    }

    private void RestoreLandscape(Node root)
    {
        _combat.RestoreLandscape(root);
        _previews.RestoreLandscape();
        _between.RestoreLandscape();
        _fit.RestoreLandscape();
        _menus.RestoreLandscape();
        _mainMenu.RestoreLandscape();
        _settings.Restore();
        _navPads.RestoreLandscape();
        _overlays.RestoreLandscape();

        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
        {
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);
        }

        _header.RestoreLandscape(Traverse(root));
    }

    private void LayoutEarlyAccessPortrait(Control disclaimer)
    {
        if (disclaimer.GetNodeOrNull<Control>("Image") is { } image)
        {
            Capture(image, captureVisible: false);
            image.AnchorLeft = 0.5f;
            image.AnchorRight = 0.5f;
            image.AnchorTop = 0.5f;
            image.AnchorBottom = 0.5f;
            image.OffsetLeft = -364f;
            image.OffsetRight = 364f;
            image.OffsetTop = -415f;
            image.OffsetBottom = 415f;
        }
    }

    private PortraitControlSnapshot Capture(Control control, bool captureVisible)
    {
        ulong id = control.GetInstanceId();
        if (_snapshots.TryGetValue(id, out PortraitControlSnapshot? existing))
        {
            if (captureVisible && !existing.CaptureVisible)
                existing.RecordInitialVisibility(control.Visible);
            return existing;
        }
        var created = new PortraitControlSnapshot(control, captureVisible);
        _snapshots[id] = created;
        return created;
    }

    private void PruneDeadInstances()
    {
        var dead = new List<ulong>();
        foreach (ulong id in _snapshots.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _snapshots.Remove(id);
        _header.PruneDead(dead);
        _combat.PruneDead();
        _previews.PruneDead();
        _between.PruneDead();
        _fit.PruneDead();
        _menus.PruneDead();
        _mainMenu.PruneDead();
        _settings.PruneDead();
        _navPads.PruneDead();
        _overlays.PruneDead();
    }

    private static IEnumerable<Node> Traverse(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Node current = stack.Pop();
            yield return current;
            int count = current.GetChildCount();
            for (int i = count - 1; i >= 0; i--)
                stack.Push(current.GetChild(i));
        }
    }
}
