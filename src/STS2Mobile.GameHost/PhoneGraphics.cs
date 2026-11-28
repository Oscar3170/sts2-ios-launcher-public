using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Saves;
using STS2Mobile.Platform;

namespace STS2Mobile.GameHost;

/// <summary>
/// The phone's battery and heat choices: keeps them (<see cref="PhoneGraphicsSettings"/>), applies the
/// frame cap they ask for, and shows them in the game's own Graphics settings tab in place of the
/// desktop-only window rows. The render resolution is applied by native code at launch
/// (<c>native/power_state.mm</c>). A failure here leaves the game as it was.
/// </summary>
public sealed partial class PhoneGraphics : Node
{
    private const string SettingsPath = "user://" + PhoneGraphicsSettings.FileName;
    private static readonly string[] DesktopRows =
        ["Fullscreen", "FullscreenDivider", "DisplaySelection", "Line11", "WindowedResolution", "Line12",
         "AspectRatio", "Line10", "WindowResizing", "Line14", "VSync", "Line13"];
    // "Custom" is only listed while it is in force, so paging never lands on a choice that changes nothing.
    private static readonly string[] PresetChoices = ["Off", "Balanced", "Maximum"];
    private static readonly string[] PresetChoicesWithCustom = [.. PresetChoices, "Custom"];
    private static readonly string[] ResolutionChoices = ["100%", "83%", "67%"];
    private static readonly string[] OffOn = ["Off", "On"];
    private static readonly string[] HandLayoutChoices = ["Alternating", "Fixed"];
    private readonly HashSet<int> _touches = new();
    private readonly List<PhoneOptionPaginator> _rows = new();
    private readonly PortraitLayout _portraitLayout = new();
    private readonly PhoneTouchCards _touchCards = new();
    private readonly PhoneTouchScroll _touchScroll = new();
    private readonly PhoneHoverTips _hoverTips = new();
    private readonly PhoneSelectConfirm _selectConfirm = new();
    private readonly PhoneCardRewardChoice _cardRewardChoice = new();
    private readonly PhoneRewardTapGuard _rewardTapGuard = new();
    private readonly PhoneChoiceConfirm _choiceConfirm = new();
    private readonly long _origin = Stopwatch.GetTimestamp();
    private PhoneGraphicsSettings _settings = PhoneGraphicsSettings.Default;
    private string _path = "";
    private double _appliedFactor = 1;
    private double _lastTouch;
    private bool _gameRunning;
    private bool _focused = true;
    private MegaRichTextLabel? _resolutionLabel;
    private NSettingsPanel? _activeGraphicsPanel;
    private Control? _portraitDefaultRow;
    private Control? _portraitDefaultDivider;
    private readonly HashSet<ulong> _graphicsPanels = new();
    private readonly HashSet<ulong> _fitPending = new();
    private readonly HashSet<ulong> _watchedRows = new();
    private readonly List<(ulong Id, Callable Hook)> _rowHooks = new();

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        try
        {
            _path = ProjectSettings.GlobalizePath(SettingsPath);
            _settings = PhoneGraphicsSettings.Load(_path);
            // Landscape is locked initially unless both Portrait Mode and Set Portrait as Default were chosen.
            if (!_settings.StartInPortrait && _settings.PortraitMode)
                _settings = _settings.WithPortraitMode(false);
            PortraitHandLayout.FlatHand = _settings.FlatHand;
            PortraitHandLayout.RowMode = _settings.HandRow;
            PortraitHandLayout.ReduceEffects = _settings.ReduceEffects;
            PhoneTouchCards.ReadableDrag = _settings.ReadableDrag;
            PortraitFrameTiming.SetEnabled(_settings.PerformanceLog);
            _appliedFactor = DevicePower.RenderScale();
            GD.Print("Phone graphics: ", _settings.Describe(_appliedFactor));
            GetTree().Root.WindowInput += OnWindowInput;
            GetTree().NodeAdded += OnNodeAdded;
            _portraitLayout.Attach(GetTree(), _settings.StartInPortrait);
            // After the portrait layout's own handler: a window that turns resizes the rows area, the game sizes each page
            // for it then, and the phone rows make the Graphics page taller than that area (see FitPanelToRows).
            GetTree().Root.SizeChanged += OnRootSizeChanged;
            _touchCards.Attach(GetTree());
            _touchScroll.Attach(GetTree());
            GetTree().ProcessFrame += OnTouchFrame;
        }
        catch (Exception error)
        {
            Disable(error);
        }
    }

    public override void _ExitTree()
    {
        ReleaseRowWatches();
        _portraitLayout.Detach();
        _touchCards.Detach();
        _touchScroll.Detach();
        _hoverTips.Release();
        GetTree().ProcessFrame -= OnTouchFrame;
    }

    // Hand cards under a finger and hover tips kept on screen; cards, relics and shop items taken only after a confirm (a second
    // tap, or the game's own confirm). Portrait only: in landscape the game plays as it does on a desktop, with a touch as the
    // mouse. The screen passes below still run in landscape, where a card reward opened in portrait hands its source back to the
    // game, so turning Portrait Mode off over an open screen leaves it working as the game does.
    private void OnTouchFrame()
    {
        if (!_gameRunning) return;
        if (SafeAreaCanvas.IsPortrait)
        {
            SceneTree tree = GetTree();
            _touchCards.OnProcessFrame(tree);
            _hoverTips.OnProcessFrame(tree);
        }
        _cardRewardChoice.OnProcessFrame();
        _rewardTapGuard.OnProcessFrame();
        _selectConfirm.OnProcessFrame();
        _choiceConfirm.OnProcessFrame();
    }

    /// <summary>Called once the real game has started and its settings save is loaded.</summary>
    public void GameStarted()
    {
        try
        {
            // The imported desktop settings say 2x MSAA, but the game only applies MSAA when the row is
            // changed, never at launch, so the phone has always drawn without it. Make the row say so
            // once; a later choice by the player is kept.
            if (!_settings.MsaaCleared)
            {
                SaveManager.Instance.SettingsSave.Msaa = 0;
                SaveManager.Instance.SaveSettings();
                Store(_settings with { MsaaCleared = true });
            }
            else if (PhoneGraphicsSettings.Load(_path) != _settings)
            {
                // Persist normalized Landscape start when Portrait Mode had been left on without Set Portrait as Default.
                _settings.Save(_path);
            }
            _gameRunning = true;
            _portraitLayout.ApplyImmediate();
        }
        catch (Exception error)
        {
            Disable(error);
        }
    }

    public override void _Process(double delta)
    {
        if (!_gameRunning || !_focused) return;
        try
        {
            double now = Now();
            if (_touches.Count > 0) _lastTouch = now;
            // The game's own activity counts as activity: a turn draw and an enemy turn start more than two seconds after
            // the last touch, and capping those to 30 fps was what made draws and enemy turns run at half rate. The idle
            // cap comes back the frame the fight settles. PhoneFrameCap is also called from the hand pass, which sees a
            // draw start in the same frame's pre-draw pass.
            PhoneFrameCap.SetInputs(SaveManager.Instance.SettingsSave.FpsLimit, _settings, _touches.Count > 0,
                now - _lastTouch, _settings.CoolDownWhenHot ? DevicePower.ThermalState() : DevicePower.UnknownThermalState);
            PhoneFrameCap.Apply();
        }
        catch (Exception error)
        {
            Disable(error);
        }
    }

    public override void _Notification(int what)
    {
        // While the app is in the background the game's own handler owns the frame cap.
        if (what == NotificationApplicationFocusOut) _focused = false;
        else if (what == NotificationApplicationFocusIn)
        {
            _focused = true;
            _lastTouch = Now();
        }
    }

    private void OnWindowInput(InputEvent input)
    {
        PhoneTouchHover.OnWindowInput(input);
        _selectConfirm.OnWindowInput(input);
        if (input is InputEventScreenTouch touch)
        {
            if (touch.Pressed) _touches.Add(touch.Index);
            else _touches.Remove(touch.Index);
            _lastTouch = Now();
        }
        else if (input is InputEventScreenDrag) _lastTouch = Now();
    }

    private void OnNodeAdded(Node node)
    {
        // Choice screens, reward lists and shop slots get the two-tap behaviour only in portrait (see AdoptOpenScreens for the
        // ones already open when Portrait Mode is turned on).
        if (SafeAreaCanvas.IsPortrait)
        {
            _cardRewardChoice.OnNodeAdded(node);
            _rewardTapGuard.OnNodeAdded(node);
            _selectConfirm.OnNodeAdded(node);
            _choiceConfirm.OnNodeAdded(node);
        }
        if (node is NSettingsScreen screen)
            screen.Connect(Node.SignalName.Ready, Callable.From(() => AddRows(screen)), (uint)ConnectFlags.OneShot);
    }

    /// <summary>
    /// Portrait Mode was turned on with the game running: choice screens, reward lists and shop slots that are already open
    /// get the portrait behaviour too. They need it: in the portrait layout the game's own take on release does not fire for a
    /// still tap on a reward card, so a screen left as it was could only be skipped (<c>switch-mid-choice</c>).
    /// </summary>
    private void AdoptOpenScreens()
    {
        var pending = new Stack<Node>();
        pending.Push(GetTree().Root);
        while (pending.Count > 0)
        {
            Node node = pending.Pop();
            _cardRewardChoice.OnNodeAdded(node);
            _rewardTapGuard.OnNodeAdded(node);
            _choiceConfirm.OnNodeAdded(node);
            foreach (Node child in node.GetChildren()) pending.Push(child);
        }
        _selectConfirm.AdoptOpenScreens();
    }

    private void AddRows(NSettingsScreen screen)
    {
        try
        {
            var panel = screen.GetNode<NSettingsPanel>("%GraphicsSettings");
            VBoxContainer content = panel.Content;
            foreach (string name in DesktopRows)
            {
                if (content.GetNodeOrNull<CanvasItem>(name) is not { } row) continue;
                row.Visible = false;
                // A row's dropdown is drawn by the panel over the row, shown while the row's anchor is: with
                // only the row hidden it was left over the first row shown. Hiding the anchor hides it for good.
                foreach (Node child in row.GetChildren())
                    if (child is CanvasItem anchor && child.GetType().Name == "NDropdownPositioner") anchor.Visible = false;
            }
            var template = content.GetNode<Control>("Msaa");
            var divider = content.GetNode<Control>("Line6");
            int index = divider.GetIndex() + 1;
            _rows.RemoveAll(row => !IsInstanceValid(row));
            _activeGraphicsPanel = panel;
            _graphicsPanels.Add(panel.GetInstanceId());
            index = AddRow(content, template, divider, index, "Portrait Mode", () => OffOn,
                () => _settings.PortraitMode ? 1 : 0,
                choice =>
                {
                    Store(_settings.WithPortraitMode(choice == 1));
                    RefreshPortraitDefaultRow();
                    if (!_settings.PortraitMode) _hoverTips.Release();
                    _portraitLayout.Apply(GetTree(), _settings.PortraitMode);
                    if (_settings.PortraitMode) AdoptOpenScreens();
                },
                out _, out _, out _, subtitle: "Landscape is locked initially. Toggle to play in Portrait.");
            index = AddRow(content, template, divider, index, "Set Portrait as Default", () => OffOn,
                () => _settings.PortraitDefault ? 1 : 0,
                choice => Store(_settings.WithPortraitDefault(choice == 1)),
                out _, out _portraitDefaultRow, out _portraitDefaultDivider,
                indentLeft: 44, subtitle: "Launch directly in Portrait when opening the game.");
            index = AddRow(content, template, divider, index, "Flat Hand", () => OffOn,
                () => _settings.FlatHand ? 1 : 0,
                choice =>
                {
                    Store(_settings.WithFlatHand(choice == 1));
                    PortraitHandLayout.FlatHand = _settings.FlatHand;
                    _portraitLayout.ApplyImmediate();
                },
                out _, out _, out _, subtitle: "Fan the hand, or lay it out in flat rows.");
            index = AddRow(content, template, divider, index, "Readable Drag", () => OffOn,
                () => _settings.ReadableDrag ? 1 : 0,
                choice =>
                {
                    Store(_settings.WithReadableDrag(choice == 1));
                    PhoneTouchCards.ReadableDrag = _settings.ReadableDrag;
                },
                out _, out _, out _,
                subtitle: "Lift the card above the finger; off keeps the point you grabbed.");
            index = AddRow(content, template, divider, index, "Hand Layout", () => HandLayoutChoices,
                () => (int)_settings.HandRow,
                choice =>
                {
                    Store(_settings.WithHandRow((HandRowMode)choice));
                    PortraitHandLayout.RowMode = _settings.HandRow;
                    _portraitLayout.ApplyImmediate();
                },
                out _, out _, out _,
                subtitle: "Alternating balances both rows; Fixed fills the upper row to five first.");
            index = AddRow(content, template, divider, index, "Performance Log", () => OffOn,
                () => _settings.PerformanceLog ? 1 : 0,
                choice =>
                {
                    Store(_settings.WithPerformanceLog(choice == 1));
                    PortraitFrameTiming.SetEnabled(_settings.PerformanceLog);
                },
                out _, out _, out _,
                subtitle: "Write per-frame portrait timings to the game log. For testing.");
            index = AddRow(content, template, divider, index, "Battery Saver",
                () => _settings.Preset == BatteryPreset.Custom ? PresetChoicesWithCustom : PresetChoices,
                () => (int)_settings.Preset, choice => Store(_settings.WithPreset((BatteryPreset)choice)), out _, out _, out _);
            index = AddRow(content, template, divider, index, ResolutionTitle(), () => ResolutionChoices,
                () => (int)_settings.Resolution, choice => Store(_settings with { Resolution = (RenderResolution)choice }), out _resolutionLabel, out _, out _);
            index = AddRow(content, template, divider, index, "Lower FPS When Idle", () => OffOn,
                () => _settings.LowerIdleFrameRate ? 1 : 0, choice => Store(_settings with { LowerIdleFrameRate = choice == 1 }), out _, out _, out _);
            AddRow(content, template, divider, index, "Cool Down When Hot", () => OffOn,
                () => _settings.CoolDownWhenHot ? 1 : 0, choice => Store(_settings with { CoolDownWhenHot = choice == 1 }), out _, out _, out _);
            AddRow(content, template, divider, index, "Reduce Effects", () => OffOn,
                () => _settings.ReduceEffects ? 1 : 0,
                choice => Store(_settings.WithReduceEffects(choice == 1)),
                out _, out _, out _,
                subtitle: "Skip hand card glow and shadows while they move (portrait). No gameplay change.");
            RefreshPortraitDefaultRow();
            // The run's settings screen is created in NRunSubmenuStack._Ready while GlobalUi is still adding children.
            // Moving the relic inventory into its clip from this Ready callback made AddChild/Reparent fail and left the
            // inventory detached (no new relic holders, AnimateRelic throwing on every acquisition). Apply after setup.
            _portraitLayout.QueueApply();
            // The panel sizes its scroll area and builds controller navigation in its own _Ready. FitPanelToRows refreshes
            // that size after the rows above exist (in portrait the scaled page is PortraitSettingsLayout's to size).
            FitPanelToRows(panel);
            // The first fit measures rows that have not had their width yet; the deferred pass runs after this frame's sort.
            if (!SafeAreaCanvas.IsPortrait) QueueFit(panel);
            panel.Call(NSettingsPanel.MethodName.UpdateNavigation);
        }
        catch (Exception error)
        {
            // The settings screen keeps working without the phone rows.
            GD.PushError("Phone graphics rows not added: " + error.GetType().Name + ": " + error.Message);
        }
    }

    private void RefreshPortraitDefaultRow()
    {
        bool show = _settings.PortraitMode;
        if (_portraitDefaultRow != null && IsInstanceValid(_portraitDefaultRow)) _portraitDefaultRow.Visible = show;
        if (_portraitDefaultDivider != null && IsInstanceValid(_portraitDefaultDivider)) _portraitDefaultDivider.Visible = show;
        if (_activeGraphicsPanel != null && IsInstanceValid(_activeGraphicsPanel) && _activeGraphicsPanel.IsNodeReady())
        {
            FitPanelToRows(_activeGraphicsPanel);
            _activeGraphicsPanel.Call(NSettingsPanel.MethodName.UpdateNavigation);
        }
    }

    /// <summary>
    /// The rows area scrolls against the page's own height, and the game grows a page taller than that area by 40% of it
    /// (<c>NSettingsPanel.RefreshSize</c>), so a controller can bring its last row to the middle. The phone rows above are
    /// what make the Graphics page overflow, so in landscape that padding is a third of the screen of scrollable empty
    /// space below the last setting: size the page to its visible rows instead. Portrait is different: the page is drawn
    /// 1.55 times larger there and <see cref="PortraitSettingsLayout"/> sizes it (with the spacer its drawn rows need), so
    /// this leaves the page alone.
    /// <para>
    /// A row's minimum is only final once its container has given it a width: a wrapping subtitle measured before that
    /// reports one word per line, so the page fitted right after the rows are added can be screens taller than the rows it
    /// draws. A fit that changed the page therefore asks for one deferred pass (after this frame's container sort), and the
    /// rows container's minimum-size changes ask for the same; both stop when the page stops changing.
    /// </para>
    /// </summary>
    private void FitPanelToRows(NSettingsPanel panel)
    {
        if (SafeAreaCanvas.IsPortrait) return;
        if (!GodotObject.IsInstanceValid(panel)) return;
        if (panel.GetNodeOrNull<VBoxContainer>("VBoxContainer") is not { } content) return;
        WatchRows(panel, content);
        // Size the page here rather than calling the game's RefreshSize: that re-adds its 40% padding every call, and this
        // fit would then change the page again on every deferred pass.
        float rows = content.GetMinimumSize().Y;
        if (Math.Abs(panel.Size.Y - rows) <= 0.5f) return;
        panel.Size = new Vector2(panel.Size.X, rows);
        QueueFit(panel);
    }

    /// <summary>Refits a page once this frame's containers have sorted and its rows report their settled minimum.</summary>
    private void QueueFit(NSettingsPanel panel)
    {
        ulong id = panel.GetInstanceId();
        if (!_fitPending.Add(id)) return;
        Callable.From(() =>
        {
            _fitPending.Remove(id);
            if (GodotObject.InstanceFromId(id) is NSettingsPanel live && GodotObject.IsInstanceValid(live)) FitPanelToRows(live);
        }).CallDeferred();
    }

    /// <summary>Connects a page's rows container once: a width or row change that settles its minimum asks for a refit.</summary>
    private void WatchRows(NSettingsPanel panel, VBoxContainer content)
    {
        ulong contentId = content.GetInstanceId();
        if (!_watchedRows.Add(contentId)) return;
        ulong panelId = panel.GetInstanceId();
        var hook = Callable.From(() =>
        {
            if (GodotObject.InstanceFromId(panelId) is NSettingsPanel live && GodotObject.IsInstanceValid(live)) QueueFit(live);
        });
        content.Connect(Control.SignalName.MinimumSizeChanged, hook);
        _rowHooks.Add((contentId, hook));
    }

    private void ReleaseRowWatches()
    {
        foreach ((ulong id, Callable hook) in _rowHooks)
        {
            if (GodotObject.InstanceFromId(id) is not Control content || !GodotObject.IsInstanceValid(content)) continue;
            if (content.IsConnected(Control.SignalName.MinimumSizeChanged, hook))
                content.Disconnect(Control.SignalName.MinimumSizeChanged, hook);
        }
        _rowHooks.Clear();
        _watchedRows.Clear();
        _fitPending.Clear();
    }

    /// <summary>
    /// The window turned: the game has just resized every page to the new rows area (padding included), and the deferred fit
    /// runs after that work, not in this signal's own handler order (the game's panels connect to it after this node does).
    /// </summary>
    private void OnRootSizeChanged() => Callable.From(FitGraphicsPanels).CallDeferred();

    private void FitGraphicsPanels()
    {
        if (SafeAreaCanvas.IsPortrait) return;
        foreach (ulong id in _graphicsPanels)
            if (GodotObject.InstanceFromId(id) is NSettingsPanel panel && GodotObject.IsInstanceValid(panel))
                FitPanelToRows(panel);
    }

    private int AddRow(VBoxContainer content, Control template, Control divider, int index, string title, Func<string[]> choices,
        Func<int> read, Action<int> write, out MegaRichTextLabel label, out Control rowNode, out Control lineNode,
        int indentLeft = 0, string? subtitle = null)
    {
        var row = new MarginContainer
        {
            Name = "Phone" + title.Replace(" ", ""),
            CustomMinimumSize = subtitle == null
                ? template.CustomMinimumSize
                : new Vector2(template.CustomMinimumSize.X, Math.Max(template.CustomMinimumSize.Y, 88f)),
        };
        foreach (string side in new[] { "left", "top", "right", "bottom" })
        {
            int margin = template.GetThemeConstant("margin_" + side) + (side == "left" ? indentLeft : 0);
            row.AddThemeConstantOverride("margin_" + side, margin);
        }
        label = (MegaRichTextLabel)template.GetNode<MegaRichTextLabel>("Label").Duplicate();
        label.Text = title;
        if (subtitle == null)
        {
            row.AddChild(label);
        }
        else
        {
            label.CustomMinimumSize = new Vector2(0f, 38f);
            var textMargin = new MarginContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            textMargin.AddThemeConstantOverride("margin_right", 336);
            var textStack = new VBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            textStack.AddThemeConstantOverride("separation", 2);
            textStack.AddChild(label);
            var sub = new Label
            {
                Text = subtitle,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            sub.AddThemeColorOverride("font_color", new Color(0.66f, 0.75f, 0.80f, 1f));
            sub.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.45f));
            sub.AddThemeConstantOverride("outline_size", 6);
            sub.AddThemeFontSizeOverride("font_size", 18);
            if (label.GetThemeFont("normal_font") is { } font)
                sub.AddThemeFontOverride("font", font);
            textStack.AddChild(sub);
            textMargin.AddChild(textStack);
            row.AddChild(textMargin);
        }
        var paginator = PhoneOptionPaginator.Create(choices, read, write);
        row.AddChild(paginator);
        _rows.Add(paginator);
        content.AddChild(row);
        content.MoveChild(row, index);
        var line = (Control)divider.Duplicate();
        content.AddChild(line);
        content.MoveChild(line, index + 1);
        rowNode = row;
        lineNode = line;
        return index + 2;
    }

    private void Store(PhoneGraphicsSettings settings)
    {
        if (settings == _settings) return;
        _settings = settings;
        PortraitHandLayout.ReduceEffects = _settings.ReduceEffects;
        try
        {
            _settings.Save(_path);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            GD.PushError("Phone graphics not saved: " + error.Message);
        }
        GD.Print("Phone graphics: ", _settings.Describe(_appliedFactor));
        // A preset moves several rows at once; show every row as it now stands.
        foreach (PhoneOptionPaginator row in _rows)
            if (IsInstanceValid(row) && row.IsInsideTree()) row.SetFromSettings();
        if (_resolutionLabel != null && IsInstanceValid(_resolutionLabel)) _resolutionLabel.Text = ResolutionTitle();
    }

    /// <summary>The resolution is chosen before the engine starts, so a new choice needs a restart.</summary>
    private string ResolutionTitle() =>
        Math.Abs(_settings.ResolutionFactor - _appliedFactor) < 0.001 ? "Render Resolution" : "Render Resolution (after restart)";

    private double Now() => Stopwatch.GetElapsedTime(_origin).TotalSeconds;

    private void Disable(Exception error)
    {
        GD.PushError("Phone graphics disabled: " + error.GetType().Name + ": " + error.Message);
        _gameRunning = false;
        SetProcess(false);
    }
}
