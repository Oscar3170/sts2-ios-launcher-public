using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// Failure-only diagnostics (never a check, never a pass/fail change): a screenshot and one block of state at
/// the moment a scenario gives up — a tap whose target never became visible/enabled, or a scenario exception. It answers the
/// questions a timeout cannot: what the screen/stack/modal/focus state was, what a finger at a given point would actually
/// hit, and which control was asked for and why it could not be tapped.
/// </summary>
internal sealed partial class TestDriver
{
    /// <summary>
    /// Captures <c>failure_&lt;label&gt;.png</c> and logs the main menu's submenu stack top, any visible modal/popup/feedback
    /// screen, the focus owner, the settings screen (when open), and the target control's own state. <paramref name="point"/>
    /// additionally logs what a finger at that canvas point owns, the PhoneHitArea pads included.
    /// </summary>
    internal static async Task FailureDiagnosticsAsync(SceneTree tree, TestReport report, ScreenshotService screenshots,
        string label, Control? target = null, Vector2? point = null)
    {
        try
        {
            string safe = new(label.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_').ToArray());
            string path = await screenshots.CaptureAsync("failure_" + safe);
            report.Screenshot("failure_" + safe, path);
            Log.Info($"[TEST] failure diagnostics {label}: screenshot={path}{DescribeState(tree, target, point)}");
        }
        catch (Exception e)
        {
            Log.Warn($"[TEST] failure diagnostics {label} failed: {e.GetBaseException().Message}");
        }
    }

    private static string DescribeState(SceneTree tree, Control? target, Vector2? point)
    {
        var parts = new List<string>();
        try
        {
            Viewport viewport = tree.Root.GetViewport();
            Control? focused = viewport.GuiGetFocusOwner();
            parts.Add($"focus={DescribeControl(focused)}");
            NMainMenu? menu = NGame.Instance?.MainMenu;
            if (menu is not null)
            {
                NSubmenu? top = menu.SubmenuStack.SubmenusOpen ? menu.SubmenuStack.Peek() : null;
                parts.Add($"stackOpen={menu.SubmenuStack.SubmenusOpen} top={DescribeNode(top)}");
                parts.Add($"menuVisible={menu.IsVisibleInTree()} menuA={menu.Modulate.A:F2}");
            }
            var modals = new List<string>();
            if (NModalContainer.Instance is { } container)
                modals.AddRange(container.GetChildren().OfType<CanvasItem>()
                    .Where(child => child.IsVisibleInTree())
                    .Select(child => $"{child.GetType().Name}:{child.Name}"));
            parts.Add("modals=" + (modals.Count == 0 ? "none" : string.Join(",", modals)));
            var screens = new List<string>();
            foreach (Control control in FindAll<Control>(tree.Root))
            {
                if (!control.IsVisibleInTree()) continue;
                string type = control.GetType().Name;
                if (type is "NVerticalPopup" or "NGenericPopup" or "NSendFeedbackScreen" or "NDropdownContainer"
                    || type.EndsWith("Popup", StringComparison.Ordinal))
                    screens.Add($"{type}:{control.Name}");
            }
            parts.Add("popups=" + (screens.Count == 0 ? "none" : string.Join(",", screens.Distinct())));
            if (target is not null)
            {
                string enabled = target is NButton button
                    ? $"IsEnabled={button.IsEnabled} _isEnabled={AccessTools.Field(typeof(NClickableControl), "_isEnabled")?.GetValue(target)}"
                    : "not a button";
                parts.Add($"target={DescribeControl(target)} {enabled} Visible={target.Visible} " +
                          $"InTree={target.IsVisibleInTree()} MouseFilter={target.MouseFilter}");
            }
            // The settings screen and its scroll state, when that is what the run was doing.
            Control? settings = FindAll<Control>(tree.Root)
                .LastOrDefault(c => c.GetType().Name == "NSettingsScreen" && c.IsVisibleInTree());
            if (settings is not null)
                parts.Add($"settings={DescribeControl(settings)} tabs={DescribeControl(settings.GetNodeOrNull<Control>("SettingsTabManager"))}");
            if (point is { } at) parts.Add(DescribePoint(tree, at));
        }
        catch (Exception e)
        {
            parts.Add("stateError=" + e.GetBaseException().Message);
        }
        return "\n  " + string.Join("\n  ", parts);
    }

    /// <summary>
    /// What owns a canvas point: the engine's hovered control, and every visible PhoneHitArea pad whose rect contains the
    /// point with its owner (the pads are how the launcher grows a small picture to a 44 point target, so an oversized pad can
    /// own a tap meant for something else).
    /// </summary>
    private static string DescribePoint(SceneTree tree, Vector2 point)
    {
        var parts = new List<string>();
        Control? hovered = tree.Root.GetViewport().GuiGetHoveredControl();
        parts.Add($"point={point} hovered={DescribeControl(hovered)}");
        var pads = new List<string>();
        foreach (Control pad in FindAll<Control>(tree.Root))
        {
            if (pad.Name != "PhoneHitArea" || !pad.IsVisibleInTree()) continue;
            Rect2 rect = pad.GetGlobalRect();
            if (!rect.HasPoint(point)) continue;
            pads.Add($"pad rect={rect} owner={DescribeControl(pad.GetParent() as Control)} filter={pad.MouseFilter}");
        }
        foreach (Control pad in FindAll<Control>(tree.Root))
        {
            if (pad.Name != "PortraitHitPadHolder" || !pad.IsVisibleInTree()) continue;
            if (pad.GetNodeOrNull<Control>("PhoneHitArea") is not { } inner || !inner.GetGlobalRect().HasPoint(point)) continue;
            pads.Add($"pad(holder) rect={inner.GetGlobalRect()} owner={DescribeControl(pad.GetParent() as Control)}");
        }
        parts.Add("pads=" + (pads.Count == 0 ? "none" : string.Join("; ", pads)));
        return string.Join(" ", parts);
    }

    private static string DescribeNode(Node? node) => node is null ? "none" : $"{node.GetType().Name}:{node.Name}";
}
