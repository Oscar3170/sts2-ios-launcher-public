using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The T5 gate (IOS_PORT_BATCH_G contract, decision 5): on an audited portrait screen every visible interactive control the
/// game itself makes tappable — <see cref="NClickableControl"/> (which every NButton, NTickbox, NDropdown and gold arrow is),
/// a plain <see cref="BaseButton"/> or a <see cref="Slider"/> — must offer at least one 44 point side, drawn or through the
/// host's larger touch area. It fails with the list of undersized controls instead of only logging them, and takes an explicit
/// allowlist per screen so every exemption is named in the step's detail.
/// <para>
/// The measurement is the launcher's own audit definition (PortraitAudit.DescribeSmall): the smaller side of the drawn rect,
/// merged with an invisible larger touch area when the host has one. Labels and other text are not targets here (they are
/// the launcher audit's 11 point text list).
/// </para>
/// <para>
/// Host answers: the safe frame, the minimum in canvas units and the effective target box are partials implemented in
/// LauncherMenuChecks.cs. The gate is skipped in landscape, where the launcher keeps the game's desktop layout.
/// </para>
/// </summary>
internal sealed partial class TestDriver
{
    // The launcher's own audit already reports these as small and keeps them: they are the game's navigation arrows and back
    // buttons, drawn at their native 72x64 / 200x110 units (25-38 pt). Named here once so every screen's detail says why
    // they are exempt rather than silently dropping them.
    private const string NativeArrowAllow = "NGoldArrowButton";
    private const string NativeBackAllow = "NBackButton";
    // A dropdown's open list is one scrolling column of native 44-unit rows; the launcher scales the list, not the rows, so
    // the rows are 20-35 pt. The closed dropdown anchor itself is still gated.
    private const string DropdownRowsAllow = "NDropdownItem";
    // The game over summary's award chips (NBadge, and NDiscoveredItem whose runtime type name is Nn). They scroll with the
    // summary; the launcher pads each one to 127 u. The launcher's own summary check (game_over_summary_chip_targets)
    // enforces that, so the shared gate names them as the host-checked exemption.
    private const string SummaryBadgeAllow = "NBadge";
    private const string SummaryItemAllow = "Nn";

    /// <summary>
    /// Records <paramref name="step"/>: every visible interactive control under <paramref name="root"/> has a target of at least
    /// the host's 44 points on its shorter side, except controls whose type or <c>Type:Name</c> matches one of
    /// <paramref name="allow"/>. On failure the detail lists each undersized control with its drawn size, and the allowlist
    /// entries that actually matched, so an exemption can never be silent.
    /// </summary>
    private void CheckTouchTargets(string step, string screen, Node root, params string[] allow)
    {
        if (!IsPortraitHost())
        {
            _report.Skip(step, $"{screen}: landscape keeps the game's desktop controls, the 44 point gate is portrait-only");
            return;
        }
        if (root is null || !GodotObject.IsInstanceValid(root) || !root.IsInsideTree())
        {
            _report.Fail(step, $"{screen}: the screen is not in the tree, nothing to measure");
            return;
        }

        float minimum = HostTouchTargetMinimum();
        var undersized = new List<string>();
        var allowed = new Dictionary<string, int>(StringComparer.Ordinal);
        int measured = 0;
        foreach (Node node in FindAll<Control>(root))
        {
            if (node is not Control control || !control.IsVisibleInTree() || control.IsQueuedForDeletion()) continue;
            if (!IsTouchTargetControl(control) || EffectiveAlpha(control) < 0.05f) continue;
            Rect2 box = HostTouchTargetBox(control);
            if (box.Size.X < 2f || box.Size.Y < 2f) continue;
            // A control scrolled out of its clipping ancestor is not on screen: only its drawn part counts, and one that is
            // entirely clipped away is not a target at all. A partly visible control is still measured at its full target size.
            if (!ClipOf(control).Intersects(box)) continue;
            measured++;
            string? match = allow.FirstOrDefault(entry => AllowMatches(entry, control)
                && HostAllowsTouchTargetExemption(entry));
            if (match is not null)
            {
                allowed[match] = allowed.TryGetValue(match, out int count) ? count + 1 : 1;
                continue;
            }
            float side = Mathf.Min(box.Size.X, box.Size.Y);
            if (side + 0.5f < minimum)
                undersized.Add($"{control.GetType().Name}:{control.Name} {box.Size.X:F0}x{box.Size.Y:F0}");
        }

        string allowText = allowed.Count == 0
            ? "none"
            : string.Join("; ", allowed.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"{entry.Key} x{entry.Value}"));
        string detail = $"minimum={minimum:F0}u {measured} target(s) on {screen}{(allow.Length > 0 ? $"; allowlist={allowText}" : "")}" +
                        (undersized.Count == 0
                            ? ""
                            : $"; undersized {undersized.Count}: {string.Join("; ", undersized.Take(40))}" +
                              (undersized.Count > 40 ? $" … +{undersized.Count - 40}" : ""));
        _report.Check(step, undersized.Count == 0, detail);
    }

    /// <summary>A control the game itself makes tappable (the contract's NClickableControl/BaseButton/NTickbox/slider/dropdown).</summary>
    private static bool IsTouchTargetControl(Control control) =>
        control is NClickableControl or BaseButton or Slider
        && control.MouseFilter != Control.MouseFilterEnum.Ignore;

    /// <summary>
    /// The part of a control the screen can show: its rect intersected with every clipping ancestor (a ScrollContainer, or a
    /// Control with ClipContents). Used to decide whether a control is on screen at all, not to resize its target.
    /// </summary>
    private static Rect2 ClipOf(Control control)
    {
        Rect2 rect = control.GetGlobalRect();
        for (Node? node = control.GetParent(); node is not null; node = node.GetParent())
            if (node is Control clip && (clip is ScrollContainer || clip.ClipContents))
                rect = rect.Intersection(clip.GetGlobalRect());
        return rect;
    }

    private static bool AllowMatches(string entry, Control control) =>
        entry == control.GetType().Name || entry == control.GetType().Name + ":" + control.Name;

    private static float EffectiveAlpha(CanvasItem item)
    {
        float alpha = 1f;
        for (Node? node = item; node is CanvasItem canvasItem; node = node.GetParent())
        {
            alpha *= canvasItem.Modulate.A * canvasItem.SelfModulate.A;
            if (alpha < 0.05f) break;
        }
        return alpha;
    }

    // The 44 point target in the host's canvas units: PortraitBands.MinTarget = 127 units on 1080.
    private partial float HostTouchTargetMinimum();

    // The rectangle a finger can reach for this control, merging the larger invisible touch area (PhoneHitArea) when there
    // is one.
    private partial Rect2 HostTouchTargetBox(Control control);

    // Extra exemptions for the Settings T5 steps: none, so the settings controls stay gated at 127 units.
    private partial string[] HostSettingsGateAllowlist();

    // Whether one of this file's named exemptions applies: only the game over summary chips keep theirs. Back buttons, gold
    // arrows and dropdown rows are ordinary phone targets and must be padded to 127 units, so they are measured and fail
    // until they are.
    private partial bool HostAllowsTouchTargetExemption(string entry);
}
