using System.Collections.Generic;
using System.Linq;
using Godot;

namespace STS2Mobile.GameHost;

internal sealed partial class PortraitEventLayout
{
    // The stone texture has an irregular transparent lower edge. Put artwork, not controls, behind it.
    private const float HeaderArtBleed = 20f;

    public const string EventLayoutTypeName = "MegaCrit.Sts2.Core.Nodes.Events.NEventLayout";
    public const string AncientLayoutTypeName = "MegaCrit.Sts2.Core.Nodes.Events.NAncientEventLayout";
    public const string EventOptionTypeName = "MegaCrit.Sts2.Core.Nodes.Events.NEventOptionButton";
    public const string AncientDialogueLineTypeName = "MegaCrit.Sts2.Core.Nodes.Events.NAncientDialogueLine";

    private static readonly string[] RichFontSizeNames =
        ["normal_font_size", "bold_font_size", "bold_italics_font_size", "italics_font_size", "mono_font_size"];

    private readonly Dictionary<ulong, PortraitControlSnapshot> _snapshots = new();

    private readonly HashSet<ulong> _events = new();

    private readonly Dictionary<ulong, int> _labelMaxFontSizes = new();
    private readonly Dictionary<ulong, int> _labelMinFontSizes = new();
    private readonly Dictionary<ulong, (bool Had, int Size)> _labelFontSizes = new();
    private readonly Dictionary<ulong, (bool Had, int Size)[]> _richFontSizes = new();

    public void Apply(Control control, string typeName, float capHeight, float canvasHeight, float canvasWidth)
    {
        switch (typeName)
        {
            case EventOptionTypeName:
                if (IsInLayout(control, AncientLayoutTypeName)) LayoutAncientOptionPortrait(control);
                else ApplyEventOption(control);
                break;
            case AncientDialogueLineTypeName:
                LayoutAncientDialogueLinePortrait(control);
                break;

            case AncientLayoutTypeName:
                LayoutAncientPortrait(control, capHeight, canvasWidth, canvasHeight);
                break;

            case EventLayoutTypeName:
                LayoutEventPortrait(control, capHeight, canvasHeight, canvasWidth);
                break;
            case CombatEventLayoutTypeName:
                LayoutCombatEventPortrait(control, capHeight, canvasHeight, canvasWidth);
                break;
            case EventRoomTypeName:
                TrackEventRoom(control, canvasWidth, canvasHeight);
                break;

        }
    }


    /// <summary>
    /// Called as a node is added, before the game sets its screen up: an ancient's option rows and dialogue lines get their
    /// portrait size here, because the game measures them for the column's slide in the frame they are added.
    /// Returns whether it changed anything.
    /// </summary>
    public bool LayoutOnAdd(Control control)
    {
        string? typeName = control.GetType().FullName;
        if (typeName == AncientDialogueLineTypeName)
        {
            LayoutAncientDialogueLinePortrait(control);
            return true;
        }
        if (typeName == EventOptionTypeName && IsInLayout(control, AncientLayoutTypeName))
        {
            LayoutAncientOptionPortrait(control);
            return true;
        }
        if (typeName == EventOptionTypeName) return ApplyEventOption(control);

        return typeName == EventLayoutTypeName && HoldEventArtUntilFramed(control);
    }

    private static bool IsInLayout(Node node, string layoutTypeName)
    {
        for (Node? parent = node.GetParent(); parent != null; parent = parent.GetParent())
            if (parent.GetType().FullName == layoutTypeName) return true;
        return false;
    }

    public void UpdateEvents(float capHeight, Vector2 canvas)
    {
        foreach (ulong id in _events)
            if (GodotObject.InstanceFromId(id) is Control layout && GodotObject.IsInstanceValid(layout) && layout.IsVisibleInTree())
                UpdateEventPortrait(layout, capHeight, canvas);
        foreach (ulong id in _eventRooms)
            if (GodotObject.InstanceFromId(id) is Control room && GodotObject.IsInstanceValid(room) && room.IsInsideTree())
                UpdateEventRoomCover(room, canvas);
    }

    public void RestoreEventStructure() => RestoreEventsLandscape();

    public void RestoreControls()
    {
        foreach ((ulong id, PortraitControlSnapshot snapshot) in _snapshots)
            if (GodotObject.InstanceFromId(id) is Control control && GodotObject.IsInstanceValid(control))
                snapshot.Restore(control);

        _snapshots.Clear();

        foreach ((ulong id, int size) in _labelMaxFontSizes)
            if (GodotObject.InstanceFromId(id) is Control label && GodotObject.IsInstanceValid(label))
                label.Set("MaxFontSize", size);
        _labelMaxFontSizes.Clear();
        foreach ((ulong id, int size) in _labelMinFontSizes)
            if (GodotObject.InstanceFromId(id) is Control label && GodotObject.IsInstanceValid(label))
                label.Set("MinFontSize", size);
        _labelMinFontSizes.Clear();
        foreach ((ulong id, (bool had, int size)) in _labelFontSizes)
        {
            if (GodotObject.InstanceFromId(id) is not Control label || !GodotObject.IsInstanceValid(label)) continue;
            if (had) label.AddThemeFontSizeOverride("font_size", size);
            else label.RemoveThemeFontSizeOverride("font_size");
        }
        _labelFontSizes.Clear();
        foreach ((ulong id, (bool Had, int Size)[] sizes) in _richFontSizes)
        {
            if (GodotObject.InstanceFromId(id) is not Control label || !GodotObject.IsInstanceValid(label)) continue;
            for (int i = 0; i < RichFontSizeNames.Length; i++)
            {
                if (sizes[i].Had) label.AddThemeFontSizeOverride(RichFontSizeNames[i], sizes[i].Size);
                else label.RemoveThemeFontSizeOverride(RichFontSizeNames[i]);
            }
        }
        _richFontSizes.Clear();

        // Put the painting back after restoring its native geometry.
        RestoreAncientLandscape();
    }

    public void PruneDead()
    {
        foreach (ulong id in _snapshots.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _snapshots.Remove(id);
        _events.RemoveWhere(id => !GodotObject.IsInstanceIdValid(id));
        PruneAncientInstances();
        foreach (ulong id in _labelMaxFontSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _labelMaxFontSizes.Remove(id);
        foreach (ulong id in _labelMinFontSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _labelMinFontSizes.Remove(id);
        foreach (ulong id in _labelFontSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _labelFontSizes.Remove(id);
        foreach (ulong id in _richFontSizes.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) _richFontSizes.Remove(id);
        PruneEventInstances();

    }


    /// <summary>Sets a label's font size in portrait, keeping the game's own to put back in landscape.</summary>
    private void SetLabelFontSize(Control label, int size)
    {
        ulong id = label.GetInstanceId();
        if (!_labelFontSizes.ContainsKey(id))
            _labelFontSizes[id] = (label.HasThemeFontSizeOverride("font_size"), label.GetThemeFontSize("font_size"));
        if (label.GetThemeFontSize("font_size") != size) label.AddThemeFontSizeOverride("font_size", size);
    }

    private PortraitControlSnapshot Capture(Control control, bool captureVisible)
    {
        ulong id = control.GetInstanceId();
        if (_snapshots.TryGetValue(id, out PortraitControlSnapshot? existing))
        {
            if (captureVisible && !existing.CaptureVisible) existing.RecordInitialVisibility(control.Visible);
            return existing;
        }
        var created = new PortraitControlSnapshot(control, captureVisible);
        _snapshots[id] = created;
        return created;
    }

    /// <summary>Sets every font size of a rich text label that does not size itself, keeping what it had for landscape.</summary>
    private void SetRichFontSize(Control label, int size)
    {
        ulong id = label.GetInstanceId();
        if (!_richFontSizes.ContainsKey(id))
            _richFontSizes[id] = RichFontSizeNames
                .Select(name => (label.HasThemeFontSizeOverride(name), label.HasThemeFontSizeOverride(name) ? label.GetThemeFontSize(name) : 0))
                .ToArray();
        foreach (string name in RichFontSizeNames)
            if (!label.HasThemeFontSizeOverride(name) || label.GetThemeFontSize(name) != size)
                label.AddThemeFontSizeOverride(name, size);
    }

}
