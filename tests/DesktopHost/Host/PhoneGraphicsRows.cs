using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using STS2Mobile.GameHost;
using STS2Mobile.Platform;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// Test fixture: the phone's Graphics rows on the desktop host, which does not compile <c>PhoneGraphics</c>. It mirrors
/// <c>PhoneGraphics.AddRows</c>/<c>AddRow</c> node for node (the same desktop rows hidden, the same eleven rows inserted
/// after Line6 with the same margins, title, autowrapped subtitle, and the game's paginator scene), and its landscape page
/// fit mirrors <c>PhoneGraphics.FitPanelToRows</c>/<c>OnRootSizeChanged</c>. Only the settings writes are left out: the
/// paginators read fixed choices and their choices go nowhere.
/// <para>
/// It runs before the portrait layout's deferred apply, like the phone: the screen's <c>Ready</c> signal adds the rows, and
/// the launcher's <c>NodeAdded</c> handler has already asked for its layout pass by then. Portrait page sizing is left to
/// <c>PortraitSettingsLayout</c>, exactly as on the phone.
/// </para>
/// </summary>
internal static class PhoneGraphicsRows
{
    private static readonly string[] DesktopRows =
        ["Fullscreen", "FullscreenDivider", "DisplaySelection", "Line11", "WindowedResolution", "Line12",
         "AspectRatio", "Line10", "WindowResizing", "Line14", "VSync", "Line13"];

    private static SceneTree? _tree;
    private static readonly HashSet<ulong> Filled = new();
    private static readonly HashSet<ulong> Panels = new();
    private static readonly HashSet<ulong> WatchedRows = new();
    private static readonly HashSet<ulong> FitPending = new();
    private static readonly List<(ulong Id, Callable Hook)> RowHooks = new();

    public static void Install(SceneTree tree)
    {
        if (_tree == tree) return;
        Detach();
        _tree = tree;
        tree.NodeAdded += OnNodeAdded;
        tree.Root.SizeChanged += OnRootSizeChanged;
        // The game creates its settings screen with GlobalUi at startup, before a scenario can install this fixture: adopt
        // it the way PhoneGraphics sees it (a Ready callback) so the menu's Graphics tab gets the rows too.
        Adopt(tree.Root);
        GD.Print("[phone rows] installed: the Graphics tab gets the phone's rows");
    }

    private static void Adopt(Node root)
    {
        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            Node node = pending.Pop();
            if (node is NSettingsScreen screen) Hook(screen);
            foreach (Node child in node.GetChildren()) pending.Push(child);
        }
    }

    private static void Hook(NSettingsScreen screen)
    {
        ulong id = screen.GetInstanceId();
        if (Filled.Contains(id)) return;
        if (screen.IsNodeReady())
        {
            AddRows(screen);
            return;
        }
        screen.Connect(Node.SignalName.Ready, Callable.From(() => AddRows(screen)), (uint)GodotObject.ConnectFlags.OneShot);
    }

    public static void Detach()
    {
        ReleaseRowWatches();
        if (_tree == null) return;
        if (GodotObject.IsInstanceValid(_tree.Root)) _tree.Root.SizeChanged -= OnRootSizeChanged;
        _tree.NodeAdded -= OnNodeAdded;
        _tree = null;
        Panels.Clear();
    }

    /// <summary>Whether this page carries the fixture's rows (the phone's Graphics page).</summary>
    public static bool IsInstalledOn(Control page) =>
        page.GetNodeOrNull<Control>("VBoxContainer/PhonePortraitMode") != null;

    /// <summary>The rows' own answer, for the check's detail: every visible row's minimum and laid-out height.</summary>
    public static string Describe(Control page)
    {
        if (page.GetNodeOrNull<VBoxContainer>("VBoxContainer") is not { } content) return "no VBoxContainer";
        var text = new System.Text.StringBuilder();
        text.Append($"page={page.Size} scale={page.Scale} content={content.Size} min={content.GetMinimumSize()} rows:");
        foreach (Node child in content.GetChildren())
        {
            if (child is not Control control || !control.Visible) continue;
            text.Append($" {control.Name}(min {control.GetCombinedMinimumSize().Y:F0} size {control.Size.Y:F0})");
        }
        return text.ToString();
    }

    private static void OnNodeAdded(Node node)
    {
        if (node is NSettingsScreen screen) Hook(screen);
    }

    private static void AddRows(NSettingsScreen screen)
    {
        Filled.Add(screen.GetInstanceId());
        try
        {
            var panel = screen.GetNode<NSettingsPanel>("%GraphicsSettings");
            VBoxContainer content = panel.Content;
            foreach (string name in DesktopRows)
            {
                if (content.GetNodeOrNull<CanvasItem>(name) is not { } row) continue;
                row.Visible = false;
                foreach (Node child in row.GetChildren())
                    if (child is CanvasItem anchor && child.GetType().Name == "NDropdownPositioner") anchor.Visible = false;
            }
            var template = content.GetNode<Control>("Msaa");
            var divider = content.GetNode<Control>("Line6");
            int index = divider.GetIndex() + 1;
            Panels.Add(panel.GetInstanceId());
            index = AddRow(content, template, divider, index, "Portrait Mode", ["Off", "On"], 1,
                subtitle: "Landscape is locked initially. Toggle to play in Portrait.");
            index = AddRow(content, template, divider, index, "Set Portrait as Default", ["Off", "On"], 0,
                indentLeft: 44, subtitle: "Launch directly in Portrait when opening the game.");
            index = AddRow(content, template, divider, index, "Flat Hand", ["Off", "On"], 0,
                subtitle: "Fan the hand, or lay it out in flat rows.");
            index = AddRow(content, template, divider, index, "Readable Drag", ["Off", "On"], 0,
                subtitle: "Lift the card above the finger; off keeps the point you grabbed.");
            index = AddRow(content, template, divider, index, "Hand Layout", ["Alternating", "Fixed"], 0,
                subtitle: "Alternating balances both rows; Fixed fills the upper row to five first.");
            index = AddRow(content, template, divider, index, "Performance Log", ["Off", "On"], 0,
                subtitle: "Write per-frame portrait timings to the game log. For testing.");
            index = AddRow(content, template, divider, index, "Battery Saver", ["Off", "Balanced", "Maximum"], 1);
            index = AddRow(content, template, divider, index, "Render Resolution", ["100%", "83%", "67%"], 0);
            index = AddRow(content, template, divider, index, "Lower FPS When Idle", ["Off", "On"], 1);
            index = AddRow(content, template, divider, index, "Cool Down When Hot", ["Off", "On"], 1);
            AddRow(content, template, divider, index, "Reduce Effects", ["Off", "On"], 0,
                subtitle: "Skip hand card glow and shadows while they move (portrait). No gameplay change.");
            FitPanelToRows(panel);
            if (!SafeAreaCanvas.IsPortrait) QueueFit(panel);
            GD.Print($"[phone rows] added the phone's Graphics rows to {screen.GetPath()} ({content.GetChildCount()} children)");
        }
        catch (Exception error)
        {
            GD.PushError("[phone rows] fixture failed: " + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Mirrors <c>PhoneGraphics.AddRow</c>: the row, its title, optional autowrapped subtitle and paginator.</summary>
    private static int AddRow(VBoxContainer content, Control template, Control divider, int index, string title,
        string[] choices, int choice, int indentLeft = 0, string? subtitle = null)
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
        var label = (MegaRichTextLabel)template.GetNode<MegaRichTextLabel>("Label").Duplicate();
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
        row.AddChild(PhoneOptionPaginator.Create(() => choices, () => choice, _ => { }));
        content.AddChild(row);
        content.MoveChild(row, index);
        var line = (Control)divider.Duplicate();
        content.AddChild(line);
        content.MoveChild(line, index + 1);
        return index + 2;
    }

    /// <summary>
    /// Mirrors <c>PhoneGraphics.FitPanelToRows</c>: landscape's page is its rows, refitted when the rows settle; portrait's
    /// page is <c>PortraitSettingsLayout</c>'s to size.
    /// </summary>
    private static void FitPanelToRows(NSettingsPanel panel)
    {
        if (SafeAreaCanvas.IsPortrait) return;
        if (!GodotObject.IsInstanceValid(panel)) return;
        if (panel.GetNodeOrNull<VBoxContainer>("VBoxContainer") is not { } content) return;
        WatchRows(panel, content);
        // Mirrors the owner: size here, not through the game's RefreshSize, which re-adds padding on every pass.
        float rows = content.GetMinimumSize().Y;
        if (Math.Abs(panel.Size.Y - rows) <= 0.5f) return;
        panel.Size = new Vector2(panel.Size.X, rows);
        QueueFit(panel);
    }

    private static void QueueFit(NSettingsPanel panel)
    {
        ulong id = panel.GetInstanceId();
        if (!FitPending.Add(id)) return;
        Callable.From(() =>
        {
            FitPending.Remove(id);
            if (GodotObject.InstanceFromId(id) is NSettingsPanel live && GodotObject.IsInstanceValid(live)) FitPanelToRows(live);
        }).CallDeferred();
    }

    private static void WatchRows(NSettingsPanel panel, VBoxContainer content)
    {
        ulong contentId = content.GetInstanceId();
        if (!WatchedRows.Add(contentId)) return;
        ulong panelId = panel.GetInstanceId();
        var hook = Callable.From(() =>
        {
            if (GodotObject.InstanceFromId(panelId) is NSettingsPanel live && GodotObject.IsInstanceValid(live)) QueueFit(live);
        });
        content.Connect(Control.SignalName.MinimumSizeChanged, hook);
        RowHooks.Add((contentId, hook));
    }

    private static void ReleaseRowWatches()
    {
        foreach ((ulong id, Callable hook) in RowHooks)
        {
            if (GodotObject.InstanceFromId(id) is not Control content || !GodotObject.IsInstanceValid(content)) continue;
            if (content.IsConnected(Control.SignalName.MinimumSizeChanged, hook))
                content.Disconnect(Control.SignalName.MinimumSizeChanged, hook);
        }
        RowHooks.Clear();
        WatchedRows.Clear();
        FitPending.Clear();
    }

    /// <summary>Mirrors <c>PhoneGraphics.OnRootSizeChanged</c>: after the game's panels have resized, fit ours again.</summary>
    private static void OnRootSizeChanged() => Callable.From(FitPanels).CallDeferred();

    private static void FitPanels()
    {
        if (SafeAreaCanvas.IsPortrait) return;
        foreach (ulong id in Panels)
            if (GodotObject.InstanceFromId(id) is NSettingsPanel panel && GodotObject.IsInstanceValid(panel))
                FitPanelToRows(panel);
    }
}
