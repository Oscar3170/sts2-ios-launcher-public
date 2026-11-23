using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    private const string StoneHeaderName = "PhoneStoneHeader";
    private const string ExpandSpacerName = "PortraitExpandSpacer";
    private const string TopBarTypeName = "MegaCrit.Sts2.Core.Nodes.CommonUi.NTopBar";
    private const string RelicInventoryTypeName = "MegaCrit.Sts2.Core.Nodes.Relics.NRelicInventory";
    private const string PotionTypeName = "MegaCrit.Sts2.Core.Nodes.Potions.NPotion";
    private const string PotionPopupTypeName = "MegaCrit.Sts2.Core.Nodes.Potions.NPotionPopup";
    private const string EarlyAccessDisclaimerTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NEarlyAccessDisclaimer";
    private const float DefaultCapHeight = 162f;
    private const float MinCapHeight = 136f;
    private const float TopBarRowHeight = PortraitBands.TopBarRowHeight;
    // The right margin the game's scene gives the top bar's boss icon.
    private const int BossIconMarginRight = 4;
    // The top bar's items were small: relics 96 units (33 points, were 78), wrapped into lines inside the fixed band.
    private const float PortraitRelicSize = 96f;
    private const float DesktopRelicSize = 68f;
    private const float RelicSeparation = 8f;
    // Sizes for a finger. A potion slot is 44 points (127 units) in its own row under the top bar, and the map,
    // deck and settings buttons are 44 points wide, their touch area reaching up into the safe cap to be 44 points tall.
    private const string PotionRowName = "PotionMarginifier";
    internal const string PotionRowScrollName = "PhonePotionRow";
    private const string PotionStripName = "Strip";
    private const string MapDeckRowName = "PhoneMapDeckRow";
    internal const string RelicClipName = "PhoneRelicRow";
    private const string RelicExtentName = "Extent";
    private const float PotionSlotSize = PortraitBands.MinTarget;
    private const float PotionPictureScale = 1.5f;
    private const float PotionRowMargin = 10f;
    private const float PotionRowGap = 8f;
    private const float PotionHolderSeparation = 4f;
    private const float RelicSideMargin = 20f;
    private const float RelicClipTopGap = 14f;
    private const float RelicFadeSeconds = 0.25f;
    private const float MapDeckSeparation = 8f;
    private const float TopBarButtonWidth = PortraitBands.MinTarget;
    private const float TopBarRowInset = 4f;
    private const float TopBarTouchRise = PortraitBands.MinTarget - (TopBarRowHeight - TopBarRowInset);
    // Health, gold and floor numbers at 38 units (13 points; the game's 32 is 11) and their icons 48 wide (were
    // 40), as much as the row's width allows with three digit health.
    private const int TopBarNumberFontSize = 38;
    private const float TopBarIconWidth = 48f;

    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();
    private readonly Dictionary<ulong, Vector2> _relicOriginalPos = new();
    private readonly Dictionary<ulong, RelicClipState> _relicClips = new();
    private readonly PhoneHeaderSwipe _headerSwipe = new();
    private readonly HashSet<ulong> _wiredTopBars = new();
    private readonly HashSet<ulong> _wiredInventories = new();
    private readonly HashSet<ulong> _wiredPotionHolders = new();
    // Labels whose font portrait enlarges (top bar numbers, the deck count), with the game's own font size to put back in
    // landscape.
    private readonly Dictionary<ulong, (bool HadOverride, int Size)> _fontSizes = new();
    private readonly PortraitCombatLayout _combat = new();
    private readonly PortraitBetweenLayout _between = new();
    private readonly PortraitSelectionPreview _previews = new();
    private readonly PortraitScreenFit _fit = new();
    private readonly PortraitMenuLayout _menus = new();
    private readonly PortraitMainMenuLayout _mainMenu = new();
    private readonly PortraitSettingsLayout _settings = new();
    private readonly PortraitNavigationPads _navPads = new();
    private readonly PortraitOverlayLayout _overlays = new();
    private SceneTree? _tree;
    private FieldInfo? _topBarHideTween;
    // The run's top bar and relic strip, as last laid out in portrait (see OwnRunBarTweens).
    private ulong _runTopBarId;
    private ulong _runRelicsId;
    // The header nodes a swipe may scroll: the potion row and the relic band's clip.
    private ulong _potionRowId;
    private ulong _relicClipId;
    private ulong _blitzMapId;
    private ulong _blitzContainerId;
    private ulong _topBarHpId;
    private ulong _pauseButtonId;
    // The scroll offsets the holders' touch areas were last matched to (see RefreshHeaderHolderInput).
    private int _potionScrollSeen = -1;
    private readonly Dictionary<ulong, int> _relicScrollSeen = new();
    private bool _applyQueued;
    private int _switchGeneration;

    /// <summary>One relic inventory's scroll clip, and the layout to put back when Portrait Mode is turned off.</summary>
    private sealed class RelicClipState
    {
        public ScrollContainer Clip = null!;
        public Control Extent = null!;
        public Node OriginalParent = null!;
        public int OriginalIndex;
        public Control.MouseFilterEnum OriginalMouseFilter;
        public FieldInfo? TweenField;
        public Tween? Fade;
        public bool FadedOut;
        public int LastCount = -1;
    }

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
        RenderingServer.FramePreDraw += OnHeaderPreDraw;
        _headerSwipe.Attach(tree);
        _settings.Attach(tree);
        PortraitCombatLayout.SetFlatHandListener(ApplyImmediate);
        QueueApply();
    }

    public void Detach()
    {
        if (_tree == null) return;
        PortraitCombatLayout.SetFlatHandListener(null);
        _combat.Detach();
        RenderingServer.FramePreDraw -= OnHeaderPreDraw;
        _headerSwipe.Detach();
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
        if (fullName is TopBarTypeName or RelicInventoryTypeName or EarlyAccessDisclaimerTypeName or PortraitCombatLayout.CombatRoomTypeName ||
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
        double insetFraction = SafeAreaCanvas.TopInset();
        float capHeight = insetFraction > 0 ? MathF.Ceiling((float)(insetFraction * canvasHeight)) : DefaultCapHeight;
        return Math.Max(capHeight, MinCapHeight);
    }

    /// <summary>In a run the top bar stays over fitted screens such as the pause menu, so their band starts below it.</summary>
    private static float FitBandTop(string typeName, float capHeight) =>
        PortraitScreenFit.InRunScreens.Contains(typeName) ? capHeight + PortraitBands.TopBarHeight : capHeight;

    private void QueueApply()
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
                if (typeName == TopBarTypeName)
                {
                    _runTopBarId = control.GetInstanceId();
                    LayoutTopBarPortrait(control, capHeight, canvas);
                }
                else if (typeName == RelicInventoryTypeName && control is FlowContainer flow)
                {
                    _runRelicsId = flow.GetInstanceId();
                    LayoutRelicInventoryPortrait(flow, capHeight, canvas);
                }
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
        RefreshHeaderSwipeRows();
        _potionScrollSeen = -1;
        _relicScrollSeen.Clear();
    }

    /// <summary>The swipe rows, as last laid out: the potion row and whichever relic clip the run's inventory has.</summary>
    private void RefreshHeaderSwipeRows()
    {
        Control? potions = _potionRowId != 0 ? GodotObject.InstanceFromId(_potionRowId) as Control : null;
        Control? relics = _relicClipId != 0 ? GodotObject.InstanceFromId(_relicClipId) as Control : null;
        _headerSwipe.SetRows(
            potions is { } p && GodotObject.IsInstanceValid(p) ? p : null,
            relics is { } r && GodotObject.IsInstanceValid(r) ? r : null);
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

        foreach (Node node in Traverse(root))
        {
            if (node is Control control)
            {
                string? typeName = control.GetType().FullName;
                if (typeName == TopBarTypeName)
                {
                    if (control.GetNodeOrNull<CanvasItem>(StoneHeaderName) is { } stone) stone.Visible = false;
                    if (control.GetNodeOrNull<CanvasItem>("LeftAlignedStuff/" + ExpandSpacerName) is { } spacer) spacer.Visible = false;
                    if (control.GetNodeOrNull<Node>("%TimerContainer") is { } timer && timer.HasMethod("RefreshVisibility"))
                        timer.Call("RefreshVisibility");
                    // Map and deck back into the desktop row, before the settings button.
                    if (control.GetNodeOrNull<HBoxContainer>("RightAlignedStuff") is { } rightBox)
                    {
                        int at = control.GetNodeOrNull<Control>("%PauseButton")?.GetIndex() ?? rightBox.GetChildCount();
                        foreach (string name in new[] { "%Map", "%Deck" })
                        {
                            if (control.GetNodeOrNull<Control>(name) is not { } button || button.GetParent() == rightBox) continue;
                            button.Reparent(rightBox, keepGlobalTransform: false);
                            rightBox.MoveChild(button, Math.Clamp(at++, 0, rightBox.GetChildCount() - 1));
                        }
                    }
                    if (control.GetNodeOrNull<HBoxContainer>(MapDeckRowName) is { } mapDeckRow) mapDeckRow.QueueFree();
                    // The potion tray back into the top bar's row, after the gold, where the game's scene has it.
                    Control? potionRow = control.GetNodeOrNull<ScrollContainer>(PotionRowScrollName)
                        ?.GetNodeOrNull<Control>(PotionStripName)?.GetNodeOrNull<Control>(PotionRowName)
                        ?? control.FindChild(PotionRowName, true, false) as Control;
                    if (potionRow != null && control.GetNodeOrNull<Control>("LeftAlignedStuff") is { } leftBox)
                    {
                        potionRow.Reparent(leftBox, keepGlobalTransform: false);
                        int after = leftBox.GetNodeOrNull<Node>("TopBarGold")?.GetIndex() ?? 2;
                        leftBox.MoveChild(potionRow, Math.Min(after + 1, leftBox.GetChildCount() - 1));
                    }
                    if (control.GetNodeOrNull<ScrollContainer>(PotionRowScrollName) is { } potionScroll) potionScroll.QueueFree();
                    if (control.GetNodeOrNull<Control>("%TopBarPortraitTip") is { } portraitTip)
                        PhoneHitArea.Remove(portraitTip);
                    RestorePotionHoldersLandscape(control);
                    _potionRowId = 0;
                    _blitzMapId = 0;
                    _blitzContainerId = 0;
                    _topBarHpId = 0;
                    _pauseButtonId = 0;
                }
                else if (typeName == RelicInventoryTypeName && control is FlowContainer inventory)
                {
                    RestoreRelicInventoryLandscape(inventory);
                }
            }
        }

        foreach ((ulong id, (bool hadOverride, int size)) in _fontSizes)
        {
            if (GodotObject.InstanceFromId(id) is not Control label || !GodotObject.IsInstanceValid(label)) continue;
            if (hadOverride) label.AddThemeFontSizeOverride("font_size", size);
            else label.RemoveThemeFontSizeOverride("font_size");
        }
        _fontSizes.Clear();
        _headerSwipe.Clear();
        _potionRowId = 0;
        _relicClipId = 0;
    }

    /// <summary>Sets a label's font size in portrait, keeping the game's own to put back in landscape.</summary>
    private void SetFontSize(Control label, int size)
    {
        ulong id = label.GetInstanceId();
        if (!_fontSizes.ContainsKey(id))
        {
            bool had = label.HasThemeFontSizeOverride("font_size");
            _fontSizes[id] = (had, label.GetThemeFontSize("font_size"));
        }
        label.AddThemeFontSizeOverride("font_size", size);
    }

    /// <summary>The game's own font size of a label portrait may have changed.</summary>
    private int GameFontSize(Control label) =>
        _fontSizes.TryGetValue(label.GetInstanceId(), out (bool HadOverride, int Size) font) ? font.Size : label.GetThemeFontSize("font_size");

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

    /// <summary>
    /// Before each frame is drawn, the game's own run-bar slides are taken over. The game slides the top bar and the relic
    /// strip up out of view while the pause menu, Settings or the compendium is open; in portrait the top bar's desktop slide
    /// would stop it inside the notch's band, and the relics would slide out of their band. The top bar is held exactly where
    /// the fight puts it and the relic clip fades out and in instead of sliding, as the reference layout does. Running from
    /// <see cref="RenderingServer.FramePreDraw"/> is what makes this invisible: every tween the game created this frame has
    /// already written, and no frame is drawn before the correction. The Blitz map's own reposition is taken over here too,
    /// because the game places it between the desktop left box and the deck button, which portrait has moved.
    /// </summary>
    private void OnHeaderPreDraw()
    {
        if (_tree == null || !SafeAreaCanvas.IsPortrait || !GodotObject.IsInstanceValid(_tree.Root)) return;
        OwnRunBarTweens();
        KeepBlitzMapInRow();
        RefreshHeaderHolderInput();
    }

    /// <summary>
    /// A scroll clip hides its content but Godot still offers every holder to a touch, wherever it was scrolled to: a potion
    /// scrolled out of the row would sit over the map and deck buttons, a third line of relics over the stage. Each holder's
    /// touch area is turned off until the whole of it is inside its clip. Checked when an offset changes, not per holder per
    /// frame.
    /// </summary>
    private void RefreshHeaderHolderInput()
    {
        if (_potionRowId != 0 && GodotObject.InstanceFromId(_potionRowId) is ScrollContainer potions &&
            GodotObject.IsInstanceValid(potions))
        {
            int scroll = potions.ScrollHorizontal;
            if (scroll != _potionScrollSeen)
            {
                _potionScrollSeen = scroll;
                FilterHolderInput(potions);
            }
        }
        foreach ((ulong id, RelicClipState state) in _relicClips)
        {
            if (!GodotObject.IsInstanceValid(state.Clip)) continue;
            int scroll = state.Clip.ScrollVertical;
            if (_relicScrollSeen.TryGetValue(id, out int seen) && seen == scroll) continue;
            _relicScrollSeen[id] = scroll;
            FilterHolderInput(state.Clip);
        }
    }

    private static void FilterHolderInput(Control clip)
    {
        // Two units of slack: a slot scrolled flush with the clip's edge still shows (and is tapped) as a whole.
        Rect2 visible = clip.GetGlobalRect().Grow(2f);
        FilterHolderInput(clip, visible);
    }

    private static void FilterHolderInput(Node node, Rect2 visible)
    {
        foreach (Node child in node.GetChildren())
        {
            // A potion's popup is a child of its holder but is drawn and placed below the relic band: its Use and Discard
            // are the screen's buttons, not the scrolling row's, and keep every touch.
            if (child.GetType().FullName == PotionPopupTypeName)
            {
                ResetHolderInput(child);
                continue;
            }
            if (child is NClickableControl clickable)
            {
                Control.MouseFilterEnum want = visible.Encloses(clickable.GetGlobalRect())
                    ? Control.MouseFilterEnum.Stop
                    : Control.MouseFilterEnum.Ignore;
                if (clickable.MouseFilter != want) clickable.MouseFilter = want;
            }
            FilterHolderInput(child, visible);
        }
    }

    /// <summary>Leaving portrait: every holder is offered to the game again, wherever the desktop scene puts it.</summary>
    private static void ResetHolderInput(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is NClickableControl clickable) clickable.MouseFilter = Control.MouseFilterEnum.Stop;
            ResetHolderInput(child);
        }
    }

    private void OwnRunBarTweens()
    {
        if (_runTopBarId != 0 && GodotObject.InstanceFromId(_runTopBarId) is Control topBar && GodotObject.IsInstanceValid(topBar) &&
            topBar.IsInsideTree())
        {
            _topBarHideTween ??= topBar.GetType().GetField("_hideTween", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_topBarHideTween?.GetValue(topBar) is Tween tween && GodotObject.IsInstanceValid(tween) && tween.IsRunning())
                tween.Kill();
            if (MathF.Abs(topBar.Position.Y) > 0.01f) topBar.Position = new Vector2(topBar.Position.X, 0f);
        }

        foreach ((ulong id, RelicClipState state) in _relicClips)
        {
            if (GodotObject.InstanceFromId(id) is not Control relics || !GodotObject.IsInstanceValid(relics) ||
                !relics.IsInsideTree() || !GodotObject.IsInstanceValid(state.Clip)) continue;
            state.TweenField ??= relics.GetType().GetField("_curTween", BindingFlags.Instance | BindingFlags.NonPublic);
            // The game moves the whole inventory (a slide of 68 units a line plus 90) from AnimHide/AnimShow and from
            // ShowImmediately/HideImmediately. The clip does the hiding instead, so the inventory stays at its home.
            if (state.TweenField?.GetValue(relics) is Tween slide && GodotObject.IsInstanceValid(slide) && slide.IsRunning())
                slide.Kill();
            if (relics.Position.LengthSquared() > 0.04f) relics.Position = Vector2.Zero;
            // AnimHide/AnimShow mark their intent here; ShowImmediately/HideImmediately do not and only flicker the
            // inventory out for a screenshot, which the pin above undoes.
            bool shown = relics.FocusBehaviorRecursive != Control.FocusBehaviorRecursiveEnum.Disabled;
            if (shown == state.FadedOut) FadeRelicClip(state, shown);
        }
    }

    /// <summary>The relic clip fades where the game would slide, and stops taking touches as it fades out.</summary>
    private static void FadeRelicClip(RelicClipState state, bool shown)
    {
        state.FadedOut = !shown;
        state.Clip.MouseBehaviorRecursive = shown
            ? Control.MouseBehaviorRecursiveEnum.Inherited
            : Control.MouseBehaviorRecursiveEnum.Disabled;
        state.Fade?.Kill();
        if (!state.Clip.IsInsideTree())
        {
            state.Clip.Modulate = new Color(1f, 1f, 1f, shown ? 1f : 0f);
            return;
        }
        state.Fade = state.Clip.CreateTween();
        state.Fade.TweenProperty(state.Clip, "modulate:a", shown ? 1f : 0f, RelicFadeSeconds)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
    }

    /// <summary>
    /// The Blitz run's map is a strip of map points the game centres between the left box and the deck button every time a
    /// box sorts. Portrait puts the deck button in the second row, so the strip is placed here instead: between the health
    /// and the settings button in the top row, scaled down to what that room allows and never larger than 1.6 times its art.
    /// </summary>
    private void KeepBlitzMapInRow()
    {
        if (_blitzMapId == 0 || GodotObject.InstanceFromId(_blitzMapId) is not Control map || !GodotObject.IsInstanceValid(map) ||
            !map.IsInsideTree() || !map.IsVisibleInTree()) return;
        if (map.GetParent() is not Control topBar || topBar.GetType().FullName != TopBarTypeName) return;
        if (_blitzContainerId == 0 || GodotObject.InstanceFromId(_blitzContainerId) is not Control container ||
            !GodotObject.IsInstanceValid(container)) return;
        float width = container.Size.X, height = container.Size.Y;
        // An empty strip (every non-Blitz run) keeps the scene's 40 x 40 box: nothing to place.
        if (width <= 100f || height <= 1f) return;
        if (_topBarHpId == 0 || GodotObject.InstanceFromId(_topBarHpId) is not Control hp || !GodotObject.IsInstanceValid(hp)) return;
        float left = hp.GetGlobalRect().End.X + 22f;
        float right = (_pauseButtonId != 0 && GodotObject.InstanceFromId(_pauseButtonId) is Control pause &&
            GodotObject.IsInstanceValid(pause) ? pause.GetGlobalRect().Position.X : topBar.Size.X) - 22f;
        if (right - left < 120f) return;
        float scale = MathF.Min(1.6f, MathF.Min((right - left) / width, TopBarRowHeight / height));
        container.PivotOffset = Vector2.Zero;
        container.Scale = new Vector2(scale, scale);
        container.Position = new Vector2((left + right - width * scale) / 2f - map.Position.X,
            (TopBarRowHeight - height * scale) / 2f);
    }

    private void LayoutTopBarPortrait(Control topBar, float capHeight, Vector2 canvas)
    {
        WireTopBarSignals(topBar);
        LayoutStoneHeader(topBar, capHeight, canvas);

        // The row's boxes start above the row, in the safe cap, so what they hold is 44 points tall to a finger. Each
        // picture is kept in the row itself by a top margin or by sitting at the bottom of its box.
        float rowTop = capHeight + TopBarRowInset - TopBarTouchRise;
        float rowBottom = capHeight + TopBarRowHeight;
        int rise = (int)TopBarTouchRise;
        // The settings button is the row's only button: map and deck moved to the right of the potion row.
        float rightWidth = TopBarButtonWidth;

        if (topBar.GetNodeOrNull<HBoxContainer>("RightAlignedStuff") is { } rightBox)
        {
            Capture(rightBox, captureVisible: false);
            rightBox.AnchorLeft = 1f;
            rightBox.AnchorRight = 1f;
            rightBox.OffsetLeft = -(rightWidth + 10f);
            rightBox.OffsetRight = -10f;
            rightBox.OffsetTop = rowTop;
            rightBox.OffsetBottom = rowBottom;
            rightBox.AddThemeConstantOverride("separation", 0);

            foreach (string hiddenName in new[] { "Padding", "SaveIndicator", "TimerContainer" })
            {
                if (rightBox.GetNodeOrNull<Control>(hiddenName) is { } hidden)
                {
                    Capture(hidden, captureVisible: true);
                    hidden.Visible = false;
                }
            }

            if (topBar.GetNodeOrNull<Control>("%PauseButton") is { } pause)
            {
                _pauseButtonId = pause.GetInstanceId();
                StyleTopBarButton(pause, rise, secondRow: false);
            }
        }

        if (topBar.GetNodeOrNull<HBoxContainer>("LeftAlignedStuff") is { } leftBox)
        {
            Capture(leftBox, captureVisible: false);
            leftBox.AnchorLeft = 0f;
            leftBox.AnchorRight = 1f;
            leftBox.OffsetLeft = 10f;
            leftBox.OffsetRight = -(rightWidth + 18f);
            leftBox.OffsetTop = rowTop;
            leftBox.OffsetBottom = rowBottom;
            leftBox.AddThemeConstantOverride("separation", 8);

            if (leftBox.GetNodeOrNull<Control>("PortraitContainer") is { } portraitContainer)
            {
                Capture(portraitContainer, captureVisible: false);
                portraitContainer.AddThemeConstantOverride("margin_left", 2);
                portraitContainer.AddThemeConstantOverride("margin_top", 4 + rise);
                // Room on the right for the character tip's touch area (below).
                portraitContainer.AddThemeConstantOverride("margin_right", 38);
                portraitContainer.AddThemeConstantOverride("margin_bottom", 4);
            }

            if (topBar.GetNodeOrNull<Control>("%TopBarPortraitTip") is { } portraitTip)
            {
                Capture(portraitTip, captureVisible: false);
                portraitTip.CustomMinimumSize = new Vector2(72f, 72f);
                PhoneHitArea.Grow(portraitTip, 2f, 4f + TopBarTouchRise, 36f, 4f);
                if (topBar.GetNodeOrNull<Control>("%AscensionIcon") is { } ascensionIcon)
                {
                    // The ascension number is 7 points: as large as the badge can be without covering the character.
                    Capture(ascensionIcon, captureVisible: false);
                    ascensionIcon.PivotOffset = ascensionIcon.Size;
                    ascensionIcon.Scale = new Vector2(1.3f, 1.3f);
                }
            }

            if (topBar.GetNodeOrNull<Control>("%TopBarHp") is { } hpBox)
            {
                _topBarHpId = hpBox.GetInstanceId();
                Capture(hpBox, captureVisible: false);
                hpBox.AddThemeConstantOverride("separation", 2);
                if (hpBox.GetNodeOrNull<Control>("HpIcon") is { } hpIcon)
                {
                    Capture(hpIcon, captureVisible: false);
                    hpIcon.CustomMinimumSize = new Vector2(TopBarIconWidth, 80f);
                    hpIcon.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
                }
                if (topBar.GetNodeOrNull<Control>("%HpLabel") is { } hpLabel)
                {
                    Capture(hpLabel, captureVisible: false);
                    hpLabel.CustomMinimumSize = new Vector2(0f, 80f);
                    hpLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
                    SetFontSize(hpLabel, TopBarNumberFontSize);
                }
            }

            if (topBar.GetNodeOrNull<Control>("%TopBarGold") is { } goldBox)
            {
                Capture(goldBox, captureVisible: false);
                goldBox.AddThemeConstantOverride("separation", 2);
                goldBox.CustomMinimumSize = new Vector2(PortraitBands.MinTarget, 80f);
                if (goldBox.GetNodeOrNull<Control>("Spacer") is { } spacer)
                {
                    Capture(spacer, captureVisible: true);
                    spacer.Visible = false;
                }
                if (goldBox.GetNodeOrNull<Control>("GoldIcon") is { } goldIcon)
                {
                    Capture(goldIcon, captureVisible: false);
                    goldIcon.CustomMinimumSize = new Vector2(TopBarIconWidth, 80f);
                    goldIcon.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
                }
                if (topBar.GetNodeOrNull<Control>("%GoldLabel") is { } goldLabel)
                {
                    Capture(goldLabel, captureVisible: false);
                    goldLabel.CustomMinimumSize = new Vector2(0f, 80f);
                    goldLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
                    SetFontSize(goldLabel, TopBarNumberFontSize);
                }
            }

            if (topBar.GetNodeOrNull<Control>("%RoomIcons") is { } roomIcons)
            {
                var expandSpacer = leftBox.GetNodeOrNull<Control>(ExpandSpacerName);
                if (expandSpacer == null)
                {
                    expandSpacer = new Control
                    {
                        Name = ExpandSpacerName,
                        MouseFilter = Control.MouseFilterEnum.Ignore,
                        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    };
                    leftBox.AddChild(expandSpacer);
                }
                expandSpacer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                expandSpacer.Visible = true;
                int roomIndex = roomIcons.GetIndex();
                if (expandSpacer.GetIndex() != roomIndex - 1)
                    leftBox.MoveChild(expandSpacer, roomIndex);

                Capture(roomIcons, captureVisible: false);
                roomIcons.AddThemeConstantOverride("separation", 4);

                if (roomIcons.GetNodeOrNull<Control>("RoomIconResizer") is { } roomIconResizer)
                {
                    Capture(roomIconResizer, captureVisible: true);
                    roomIconResizer.Visible = false;
                }
                if (topBar.GetNodeOrNull<Control>("%BossIcon") is { } bossIcon)
                {
                    // The game shows and hides this icon itself on every room, so it is left to the game. At a double boss it
                    // hangs the second icon behind the first, reaching past the icon's own box, which at the end of this row
                    // put it under the map button: give the box that room.
                    Capture(bossIcon, captureVisible: false);
                    bossIcon.AddThemeConstantOverride("margin_left", 12);
                    bossIcon.AddThemeConstantOverride("margin_right", BossIconMarginRight + 12 + (int)MathF.Ceiling(SecondBossIconOverhang(bossIcon)));
                    bossIcon.AddThemeConstantOverride("margin_top", -4 + rise);
                }
                if (topBar.GetNodeOrNull<Control>("%FloorIcon") is { } floorIcon)
                {
                    Capture(floorIcon, captureVisible: false);
                    floorIcon.AddThemeConstantOverride("separation", 2);
                    floorIcon.CustomMinimumSize = new Vector2(96f, 0f);
                    if (floorIcon.GetNodeOrNull<Control>("FloorIconPositioner") is { } floorPicture)
                    {
                        Capture(floorPicture, captureVisible: false);
                        floorPicture.AddThemeConstantOverride("margin_top", -3 + rise);
                    }
                    if (topBar.GetNodeOrNull<Control>("%FloorNumLabel") is { } floorLabel)
                    {
                        Capture(floorLabel, captureVisible: false);
                        floorLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
                        SetFontSize(floorLabel, TopBarNumberFontSize);
                    }
                }
            }

            if (topBar.GetNodeOrNull<Control>("%Modifiers") is { } modifiers)
            {
                Capture(modifiers, captureVisible: true);
                modifiers.Visible = false;
            }
        }

        LayoutMapDeckRow(topBar, capHeight, canvas);
        LayoutPotionRow(topBar, capHeight, canvas);

        // The Blitz run's map strip: in the top row, beside the settings button. The game's own reposition runs between
        // desktop boxes that portrait no longer has, so the strip is placed again before each frame (KeepBlitzMapInRow).
        if (topBar.GetNodeOrNull<Control>("%BlitzMap") is { } blitzMap)
        {
            Capture(blitzMap, captureVisible: false);
            _blitzMapId = blitzMap.GetInstanceId();
            _blitzContainerId = blitzMap.GetNodeOrNull<Control>("BlitzMapContainer")?.GetInstanceId() ?? 0;
            blitzMap.AnchorLeft = 0f;
            blitzMap.AnchorRight = 1f;
            blitzMap.AnchorTop = 0f;
            blitzMap.AnchorBottom = 0f;
            blitzMap.OffsetLeft = 0f;
            blitzMap.OffsetRight = 0f;
            blitzMap.OffsetTop = capHeight;
            blitzMap.OffsetBottom = capHeight + TopBarRowHeight;
        }

        if (topBar.GetNodeOrNull<Control>("GoldPopup") is { } goldPopup)
        {
            PortraitControlSnapshot snap = Capture(goldPopup, captureVisible: false);
            goldPopup.OffsetTop = snap.InitialOffsetTop + capHeight;
            goldPopup.OffsetBottom = snap.InitialOffsetBottom + capHeight;
        }
    }

    /// <summary>
    /// The cracked-stone header: the game's own top-bar texture as a nine-patch over the safe cap and the top bar, with the
    /// game's torn bottom edge at the header's bottom. The native stretched background is hidden; in landscape it is shown
    /// again exactly as the game had it.
    /// </summary>
    private void LayoutStoneHeader(Control topBar, float capHeight, Vector2 canvas)
    {
        if (topBar.GetNodeOrNull<NinePatchRect>(StoneHeaderName) is not { } stone)
        {
            if (topBar.GetNodeOrNull<TextureRect>("%BgImage") is not { } background || background.Texture == null) return;
            StonePatchMargins margins = PortraitHeaderMath.StonePatchMargins(
                (int)background.Texture.GetWidth(), (int)background.Texture.GetHeight());
            stone = new NinePatchRect
            {
                Name = StoneHeaderName,
                Texture = background.Texture,
                Material = background.Material,
                PatchMarginLeft = margins.Left,
                PatchMarginTop = margins.Top,
                PatchMarginRight = margins.Right,
                PatchMarginBottom = margins.Bottom,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            topBar.AddChild(stone);
            topBar.MoveChild(stone, 0);
        }

        stone.AnchorLeft = 0f;
        stone.AnchorTop = 0f;
        stone.AnchorRight = 0f;
        stone.AnchorBottom = 0f;
        stone.OffsetLeft = 0f;
        stone.OffsetTop = 0f;
        stone.OffsetRight = canvas.X;
        stone.OffsetBottom = PortraitBands.HeaderBottom(capHeight);
        stone.Visible = true;

        if (topBar.GetNodeOrNull<TextureRect>("%BgImage") is { } native)
        {
            Capture(native, captureVisible: true);
            native.Visible = false;
        }
    }

    /// <summary>
    /// A top bar button with a 44 point touch box. In the first row the box reaches up into the safe cap (<paramref name="rise"/>)
    /// so the picture stays in the row; in the second row the box is the button itself. Both keep the 64 unit picture box the
    /// game pivots its picture around: the map's margin container and the deck's picture control.
    /// </summary>
    private void StyleTopBarButton(Control button, int rise, bool secondRow)
    {
        Capture(button, captureVisible: false);
        button.CustomMinimumSize = new Vector2(TopBarButtonWidth, secondRow ? PortraitBands.MinTarget : 80f);
        if (button.IsClass("MarginContainer"))
        {
            // Map is a margin container to the engine (its script class derives from a plain control, so a C# type test
            // would miss it): the picture fills the box inside the margins. The gear's box stays as the game has it: the
            // game turns and scales the picture around a pivot fixed for that width, so a wider box swings it off place.
            int spare = (int)TopBarButtonWidth - 64;
            button.AddThemeConstantOverride("margin_left", spare / 2);
            button.AddThemeConstantOverride("margin_right", spare - spare / 2);
            if (secondRow)
            {
                button.AddThemeConstantOverride("margin_top", 31);
                button.AddThemeConstantOverride("margin_bottom", 32);
            }
            else
            {
                button.AddThemeConstantOverride("margin_top", 8 + rise);
                button.AddThemeConstantOverride("margin_bottom", 8);
            }
        }
        else if (button.GetNodeOrNull<Control>("Control") is { } pictureBox)
        {
            // Deck: the picture is centred in a box that fills the button.
            Capture(pictureBox, captureVisible: false);
            pictureBox.OffsetTop = secondRow ? 0f : rise;
            if (button.GetNodeOrNull<Control>("DeckCardCount") is { } deckCount)
            {
                // The deck's card count is under 10 points. By its font, not its scale: the game animates
                // the label's scale whenever the count changes, which would put a scale of ours back to 1.
                SetFontSize(deckCount, (int)MathF.Round(GameFontSize(deckCount) * 1.2f));
            }
        }
    }

    /// <summary>
    /// Map and deck move from the first row to the right of the potion row, where the reference layout has them: two 44 point
    /// buttons side by side at the screen's right edge, level with the tray.
    /// </summary>
    private void LayoutMapDeckRow(Control topBar, float capHeight, Vector2 canvas)
    {
        float width = 2f * TopBarButtonWidth + MapDeckSeparation;
        HBoxContainer? row = topBar.GetNodeOrNull<HBoxContainer>(MapDeckRowName);
        if (row == null)
        {
            row = new HBoxContainer { Name = MapDeckRowName, MouseFilter = Control.MouseFilterEnum.Ignore };
            topBar.AddChild(row);
        }
        HeaderBox box = PortraitHeaderMath.MapDeckRow(canvas.X, capHeight, TopBarRowHeight, PortraitBands.PotionRowHeight,
            width, PortraitBands.MinTarget, PotionRowMargin);
        row.AnchorLeft = 0f;
        row.AnchorRight = 0f;
        row.AnchorTop = 0f;
        row.AnchorBottom = 0f;
        row.OffsetLeft = box.Left;
        row.OffsetRight = box.Right;
        row.OffsetTop = box.Top;
        row.OffsetBottom = box.Bottom;
        row.AddThemeConstantOverride("separation", (int)MapDeckSeparation);
        // Blitz hides the map button; the deck then keeps the screen's right edge.
        row.Alignment = BoxContainer.AlignmentMode.End;

        foreach (string name in new[] { "%Map", "%Deck" })
        {
            if (topBar.GetNodeOrNull<Control>(name) is not { } button) continue;
            // Captured before the first move: these offsets are the desktop row's.
            Capture(button, captureVisible: false);
            if (button.GetParent() != row) button.Reparent(row, keepGlobalTransform: false);
            StyleTopBarButton(button, 0, secondRow: true);
        }
    }

    /// <summary>
    /// The potion row: the game's own tray (its dark box and the slots) inside a horizontal scroll clip, left of the map and
    /// deck buttons. Any number of slots can be reached by swiping; the tray never shrinks the slots to make them fit.
    /// </summary>
    private void LayoutPotionRow(Control topBar, float capHeight, Vector2 canvas)
    {
        ScrollContainer? row = topBar.GetNodeOrNull<ScrollContainer>(PotionRowScrollName);
        if (row == null)
        {
            row = new ScrollContainer
            {
                Name = PotionRowScrollName,
                FollowFocus = false,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ClipContents = true,
                HorizontalScrollMode = ScrollContainer.ScrollMode.ShowNever,
                VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            topBar.AddChild(row);
            row.AddChild(new Control { Name = PotionStripName, MouseFilter = Control.MouseFilterEnum.Ignore });
        }
        _potionRowId = row.GetInstanceId();
        Control strip = row.GetNode<Control>(PotionStripName);

        float mapDeckWidth = 2f * TopBarButtonWidth + MapDeckSeparation;
        HeaderBox box = PortraitHeaderMath.PotionRow(canvas.X, capHeight, TopBarRowHeight, PortraitBands.PotionRowHeight,
            mapDeckWidth, PotionRowMargin, PotionRowGap);
        row.AnchorLeft = 0f;
        row.AnchorRight = 0f;
        row.AnchorTop = 0f;
        row.AnchorBottom = 0f;
        row.OffsetLeft = box.Left;
        row.OffsetRight = box.Right;
        row.OffsetTop = box.Top;
        row.OffsetBottom = box.Bottom;

        Control? tray = strip.GetNodeOrNull<Control>(PotionRowName) ?? topBar.FindChild(PotionRowName, true, false) as Control;
        if (tray != null)
        {
            Capture(tray, captureVisible: false);
            if (tray.GetParent() != strip) tray.Reparent(strip, keepGlobalTransform: false);
            tray.AnchorLeft = 0f;
            tray.AnchorRight = 0f;
            tray.AnchorTop = 0f;
            tray.AnchorBottom = 0f;
            tray.OffsetLeft = 0f;
            tray.OffsetTop = 0f;
            tray.AddThemeConstantOverride("margin_top", 0);
            tray.AddThemeConstantOverride("margin_bottom", 0);
            // The tray is as long as every slot; only the slots take touches. The tray's own box, its dark background and
            // the slots' margin container are 1588 units wide with twelve slots, well past the row, so they must not offer
            // themselves to a finger over the map, the deck or the stage.
            tray.MouseFilter = Control.MouseFilterEnum.Ignore;
        }

        if (topBar.GetNodeOrNull<Control>("%PotionContainer") is { } potionContainer)
        {
            Capture(potionContainer, captureVisible: false);
            potionContainer.CustomMinimumSize = new Vector2(0f, 72f);
            potionContainer.AddThemeConstantOverride("margin_top", 2);
            potionContainer.AddThemeConstantOverride("margin_bottom", 2);
            potionContainer.MouseFilter = Control.MouseFilterEnum.Ignore;
        }

        if (topBar.GetNodeOrNull<Control>("%PotionContainer/MarginContainer") is { } trayMargin)
        {
            Capture(trayMargin, captureVisible: false);
            trayMargin.AddThemeConstantOverride("margin_left", (int)PotionRowMargin);
            trayMargin.AddThemeConstantOverride("margin_right", (int)PotionRowMargin);
            trayMargin.AddThemeConstantOverride("margin_top", 2);
            trayMargin.AddThemeConstantOverride("margin_bottom", 2);
            trayMargin.MouseFilter = Control.MouseFilterEnum.Ignore;
        }

        foreach (string background in new[] { "%PotionContainer/PotionBg", "%PotionContainer/PotionErrorBg" })
            if (topBar.GetNodeOrNull<Control>(background) is { } potionBackground)
                potionBackground.MouseFilter = Control.MouseFilterEnum.Ignore;

        RefreshPotionHoldersPortrait(topBar);

        if (tray != null)
        {
            // A control outside a container never takes its own size: after a slot is added or removed, take it again.
            tray.ResetSize();
            Vector2 traySize = tray.GetCombinedMinimumSize();
            int slots = CountPotionHolders(topBar);
            float width = PortraitHeaderMath.PotionStripWidth(slots, PotionSlotSize, PotionHolderSeparation,
                PotionRowMargin, PotionRowMargin);
            strip.CustomMinimumSize = new Vector2(MathF.Max(width, traySize.X), traySize.Y);
            tray.Size = strip.CustomMinimumSize;
        }
    }

    private static int CountPotionHolders(Control topBar)
    {
        if (topBar.GetNodeOrNull<Control>("%PotionContainer/MarginContainer/PotionHolders") is not { } holdersBox) return 0;
        int count = 0;
        foreach (Node child in holdersBox.GetChildren())
            if (child is Control holder && !holder.IsQueuedForDeletion()) count++;
        return count;
    }

    private void WireTopBarSignals(Control topBar)
    {
        ulong id = topBar.GetInstanceId();
        if (!_wiredTopBars.Add(id)) return;
        if (topBar.GetNodeOrNull<Control>("%PotionContainer/MarginContainer/PotionHolders") is { } holdersBox)
            holdersBox.ChildOrderChanged += QueueApply;
        if (topBar.GetNodeOrNull<Control>("%TimerContainer") is { } timer)
        {
            timer.VisibilityChanged += () =>
            {
                if (SafeAreaCanvas.IsPortrait && timer.Visible)
                    timer.Visible = false;
            };
        }
        if (topBar.GetNodeOrNull<Control>("%BossIcon/Icon") is { } bossTexture)
        {
            // The second boss icon is added when an act with two bosses starts, and hidden again at the first of them.
            foreach (CanvasItem existing in bossTexture.GetChildren().OfType<CanvasItem>())
                existing.VisibilityChanged += QueueApply;
            bossTexture.ChildEnteredTree += child =>
            {
                if (child is CanvasItem item) item.VisibilityChanged += QueueApply;
                QueueApply();
            };
        }
    }

    /// <summary>How far a shown second boss icon reaches past the right edge of the first one, zero when there is none.</summary>
    private static float SecondBossIconOverhang(Control bossIcon)
    {
        if (bossIcon.GetNodeOrNull<Control>("Icon") is not { } first) return 0f;
        float overhang = 0f;
        foreach (Control child in first.GetChildren().OfType<Control>())
        {
            if (child.Name.ToString() == "Outline" || !child.Visible || child.IsQueuedForDeletion()) continue;
            overhang = Math.Max(overhang, child.Position.X + child.Size.X * child.Scale.X - first.Size.X);
        }
        return overhang;
    }

    private void RefreshPotionHoldersPortrait(Control topBar)
    {
        if (topBar.GetNodeOrNull<HBoxContainer>("%PotionContainer/MarginContainer/PotionHolders") is not { } holdersBox) return;
        Capture(holdersBox, captureVisible: false);

        var holders = new List<Control>();
        foreach (Node child in holdersBox.GetChildren())
        {
            if (child is Control holder && !holder.IsQueuedForDeletion())
                holders.Add(holder);
        }

        float holderSize = PotionSlotSize;
        holdersBox.AddThemeConstantOverride("separation", (int)PotionHolderSeparation);

        foreach (Control holder in holders)
        {
            Capture(holder, captureVisible: false);
            holder.CustomMinimumSize = new Vector2(holderSize, holderSize);
            holder.Size = new Vector2(holderSize, holderSize);
            WirePotionHolder(holder);
            if (holder.GetNodeOrNull<Control>("%EmptyIcon") is { } emptyIcon)
            {
                // The game scales the empty slot's picture and the potion by one number, so give them one size: the
                // potion is 60 units centred on the slot, and the empty picture fills the slot unless it is told otherwise.
                Capture(emptyIcon, captureVisible: false);
                emptyIcon.AnchorLeft = emptyIcon.AnchorRight = emptyIcon.AnchorTop = emptyIcon.AnchorBottom = 0.5f;
                emptyIcon.OffsetLeft = emptyIcon.OffsetTop = -30f;
                emptyIcon.OffsetRight = emptyIcon.OffsetBottom = 30f;
            }
            ScaleChildPotions(holder, PotionPictureScale / 0.9f);
        }
    }

    private void WirePotionHolder(Control holder)
    {
        ulong id = holder.GetInstanceId();
        if (!_wiredPotionHolders.Add(id)) return;
        holder.ChildOrderChanged += QueueApply;
    }

    private void RestorePotionHoldersLandscape(Control topBar)
    {
        if (topBar.GetNodeOrNull<HBoxContainer>("%PotionContainer/MarginContainer/PotionHolders") is not { } holdersBox) return;
        if (topBar.GetNodeOrNull<Control>("%PotionContainer") is { } potionContainer) ResetHolderInput(potionContainer);
        foreach (string name in new[] { PotionRowName, "%PotionContainer", "%PotionContainer/MarginContainer",
                     "%PotionContainer/PotionBg", "%PotionContainer/PotionErrorBg" })
            if (topBar.FindChild(name.TrimStart('%'), true, false) is Control box)
                box.MouseFilter = Control.MouseFilterEnum.Stop;
        foreach (Node child in holdersBox.GetChildren())
        {
            if (child is not Control holder) continue;
            holder.CustomMinimumSize = new Vector2(60f, 60f);
            ScaleChildPotions(holder, 1f);
        }
    }

    private static void ScaleChildPotions(Control holder, float scale)
    {
        Vector2 potionScale = new(0.9f * scale, 0.9f * scale);
        FieldInfo? scaleField = holder.GetType().GetField("_potionScale", BindingFlags.Instance | BindingFlags.NonPublic);
        scaleField?.SetValue(holder, potionScale);

        if (holder.GetNodeOrNull<Control>("%EmptyIcon") is { } emptyIcon)
        {
            emptyIcon.PivotOffset = new Vector2(30f, 30f);
            emptyIcon.Scale = potionScale;
        }

        foreach (Node child in holder.GetChildren())
        {
            if (child is Control control && control.GetType().FullName == PotionTypeName)
            {
                control.PivotOffset = new Vector2(30f, 30f);
                control.Scale = potionScale;
            }
        }
    }

    private void LayoutRelicInventoryPortrait(FlowContainer inventory, float capHeight, Vector2 canvas)
    {
        if (!inventory.IsInsideTree() || inventory.GetParent() == null) return;
        WireRelicInventorySignals(inventory);
        Capture(inventory, captureVisible: false);
        SaveRelicOriginalPos(inventory);
        RelicClipState? state = EnsureRelicClip(inventory);
        if (state == null || !GodotObject.IsInstanceValid(state.Clip) || !GodotObject.IsInstanceValid(state.Extent)) return;

        // The clip is fixed inside the band the stage was tuned around: relics wrap into lines and scroll; neither the count
        // nor a scroll ever moves the stage, the backgrounds or the control rail.
        HeaderBox clip = PortraitHeaderMath.RelicClip(canvas.X, capHeight, PortraitBands.TopBarHeight,
            PortraitBands.RelicBandHeight, RelicSideMargin, RelicClipTopGap);
        state.Clip.AnchorLeft = 0f;
        state.Clip.AnchorTop = 0f;
        state.Clip.AnchorRight = 0f;
        state.Clip.AnchorBottom = 0f;
        state.Clip.OffsetLeft = clip.Left;
        state.Clip.OffsetTop = clip.Top;
        state.Clip.OffsetRight = clip.Right;
        state.Clip.OffsetBottom = clip.Bottom;

        inventory.AnchorLeft = 0f;
        inventory.AnchorTop = 0f;
        inventory.AnchorRight = 0f;
        inventory.AnchorBottom = 0f;
        inventory.OffsetLeft = 0f;
        inventory.OffsetTop = 0f;
        inventory.AddThemeConstantOverride("h_separation", (int)RelicSeparation);
        inventory.AddThemeConstantOverride("v_separation", (int)RelicSeparation);

        RefreshRelicHoldersPortrait(inventory);

        var holders = GetRelicHolders(inventory);
        float content = PortraitHeaderMath.RelicContentHeight(holders.Count, clip.Width, PortraitRelicSize, RelicSeparation);
        inventory.CustomMinimumSize = new Vector2(clip.Width, 0f);
        inventory.Size = new Vector2(clip.Width, content);
        inventory.OffsetRight = clip.Width;
        inventory.OffsetBottom = inventory.Size.Y;
        // The scroll container measures the content control: it must advertise the wrapped flow's height, which its own
        // minimum size does not (it is measured before the wrap, and can be larger than the wrapped lines). Advertising
        // the larger number let the row scroll past its content, hiding the first line with a blank tail below the last.
        state.Extent.CustomMinimumSize = new Vector2(clip.Width, MathF.Max(content, clip.Height));
        // A new relic set starts at its first line: an offset kept from a smaller inventory would hide it (and swiping
        // back up could not recover it when the old offset exceeded the new content).
        if (holders.Count != state.LastCount)
        {
            state.LastCount = holders.Count;
            state.Clip.ScrollVertical = 0;
        }
        SetRelicOriginalPos(inventory, Vector2.Zero);
        inventory.Position = Vector2.Zero;
    }

    /// <summary>
    /// Moves the game's relic flow into a scroll clip inside the relic band, once, before its holders matter to the layout.
    /// Reparenting keeps the native holders, their signals and the native inspect on tap/hold: only the flow's own parent
    /// changes. The game's own slide is replaced by a fade of the clip (OwnRunBarTweens).
    /// </summary>
    private RelicClipState? EnsureRelicClip(FlowContainer inventory)
    {
        ulong id = inventory.GetInstanceId();
        if (_relicClips.TryGetValue(id, out RelicClipState? existing) && GodotObject.IsInstanceValid(existing.Clip) &&
            GodotObject.IsInstanceValid(existing.Extent))
        {
            if (inventory.GetParent() != existing.Extent)
                inventory.Reparent(existing.Extent, keepGlobalTransform: false);
            return existing;
        }

        if (inventory.GetParent() is not { } parent) return null;
        var state = new RelicClipState
        {
            OriginalParent = parent,
            OriginalIndex = inventory.GetIndex(),
            OriginalMouseFilter = inventory.MouseFilter,
            TweenField = inventory.GetType().GetField("_curTween", BindingFlags.Instance | BindingFlags.NonPublic),
        };
        var clip = new ScrollContainer
        {
            Name = RelicClipName,
            FollowFocus = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            ClipContents = true,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever,
        };
        var extent = new Control { Name = RelicExtentName, MouseFilter = Control.MouseFilterEnum.Ignore };
        clip.AddChild(extent);
        parent.AddChild(clip);
        // Just above the top bar, so the band draws over the room but under every screen opened later.
        Node? bar = null;
        foreach (Node child in parent.GetChildren())
        {
            if (child.GetType().FullName != TopBarTypeName) continue;
            bar = child;
            break;
        }
        if (bar != null) parent.MoveChild(clip, Math.Min(bar.GetIndex() + 1, parent.GetChildCount() - 1));

        state.Clip = clip;
        state.Extent = extent;
        (state.TweenField?.GetValue(inventory) as Tween)?.Kill();
        inventory.Reparent(extent, keepGlobalTransform: false);
        inventory.MouseFilter = Control.MouseFilterEnum.Ignore;
        inventory.PivotOffset = Vector2.Zero;
        inventory.FocusBehaviorRecursive = Control.FocusBehaviorRecursiveEnum.Inherited;
        SetRelicOriginalPos(inventory, Vector2.Zero);
        inventory.Position = Vector2.Zero;
        _relicClips[id] = state;
        if (id == _runRelicsId) _relicClipId = clip.GetInstanceId();
        return state;
    }

    private void WireRelicInventorySignals(FlowContainer inventory)
    {
        ulong id = inventory.GetInstanceId();
        if (!_wiredInventories.Add(id)) return;
        inventory.ChildOrderChanged += QueueApply;
        if (inventory.HasSignal("RelicsChanged"))
            inventory.Connect("RelicsChanged", Callable.From(QueueApply));
    }

    /// <summary>Every relic holder at 33 points, wrapped into the clip's lines; no holder is ever hidden.</summary>
    private void RefreshRelicHoldersPortrait(FlowContainer inventory)
    {
        float scale = PortraitRelicSize / DesktopRelicSize;
        foreach (Control holder in GetRelicHolders(inventory))
        {
            holder.CustomMinimumSize = new Vector2(PortraitRelicSize, PortraitRelicSize);
            holder.Visible = true;
            if ((holder.GetNodeOrNull<Control>("%Relic") ?? holder.GetNodeOrNull<Control>("Relic")) is { } relicNode)
            {
                relicNode.PivotOffset = Vector2.Zero;
                relicNode.Scale = new Vector2(scale, scale);
            }
        }
    }

    private void RestoreRelicInventoryLandscape(FlowContainer inventory)
    {
        ulong invId = inventory.GetInstanceId();
        if (_relicClips.TryGetValue(invId, out RelicClipState? state))
        {
            state.Fade?.Kill();
            if (GodotObject.IsInstanceValid(state.OriginalParent) && inventory.GetParent() == state.Extent)
            {
                inventory.Reparent(state.OriginalParent, keepGlobalTransform: false);
                state.OriginalParent.MoveChild(inventory,
                    Math.Clamp(state.OriginalIndex, 0, Math.Max(0, state.OriginalParent.GetChildCount() - 1)));
            }
            inventory.FocusBehaviorRecursive = Control.FocusBehaviorRecursiveEnum.Inherited;
            inventory.MouseFilter = state.OriginalMouseFilter;
            if (_relicClipId == state.Clip.GetInstanceId()) _relicClipId = 0;
            if (GodotObject.IsInstanceValid(state.Clip)) state.Clip.QueueFree();
            _relicClips.Remove(invId);
        }

        if (_relicOriginalPos.TryGetValue(invId, out Vector2 origPos))
        {
            SetRelicOriginalPos(inventory, origPos);
            inventory.Position = origPos;
        }

        ResetHolderInput(inventory);
        _relicScrollSeen.Remove(invId);
        foreach (Control holder in GetRelicHolders(inventory))
        {
            holder.CustomMinimumSize = new Vector2(DesktopRelicSize, DesktopRelicSize);
            holder.Visible = true;
            if ((holder.GetNodeOrNull<Control>("%Relic") ?? holder.GetNodeOrNull<Control>("Relic")) is { } relicNode)
            {
                relicNode.PivotOffset = Vector2.Zero;
                relicNode.Scale = Vector2.One;
            }
        }
    }

    private static List<Control> GetRelicHolders(FlowContainer inventory)
    {
        var list = new List<Control>();
        foreach (Node child in inventory.GetChildren())
        {
            if (child is Control control && !control.IsQueuedForDeletion())
                list.Add(control);
        }
        return list;
    }

    private void SaveRelicOriginalPos(FlowContainer inventory)
    {
        ulong id = inventory.GetInstanceId();
        if (_relicOriginalPos.ContainsKey(id)) return;
        FieldInfo? field = inventory.GetType().GetField("_originalPos", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(inventory) is Vector2 pos)
            _relicOriginalPos[id] = pos;
    }

    private static void SetRelicOriginalPos(FlowContainer inventory, Vector2 pos)
    {
        FieldInfo? field = inventory.GetType().GetField("_originalPos", BindingFlags.Instance | BindingFlags.NonPublic);
        field?.SetValue(inventory, pos);
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
        foreach (ulong id in _fontSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _fontSizes.Remove(id);

        dead.Clear();
        foreach (ulong id in _relicOriginalPos.Keys)
            if (!GodotObject.IsInstanceIdValid(id)) dead.Add(id);
        foreach (ulong id in dead) _relicOriginalPos.Remove(id);
        dead.Clear();
        foreach ((ulong id, RelicClipState state) in _relicClips)
            if (!GodotObject.IsInstanceIdValid(id) || !GodotObject.IsInstanceValid(state.Clip)) dead.Add(id);
        foreach (ulong id in dead) _relicClips.Remove(id);
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
