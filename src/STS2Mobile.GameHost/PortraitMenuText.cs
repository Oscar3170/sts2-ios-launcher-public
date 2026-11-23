using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace STS2Mobile.GameHost;

/// <summary>
/// Raises the menu screens' small text towards a readable 11 points without touching card faces: a label drawn under 11
/// points has its own theme font size raised (by at most 1.6 times, which the desktop menu already accepts for its main
/// entry labels), and a rich text label's auto-size ceiling is raised so it may use the room it has. The desktop values are
/// kept and put back in landscape.
/// </summary>
internal static class PortraitMenuText
{
    private const float MinTextPoints = 11f;
    private const float MaxScale = 1.7f;

    private static readonly Dictionary<(ulong Id, string Property), (bool Had, int Value)> Fonts = new();
    private static readonly Dictionary<(ulong Id, string Property), Variant> Properties = new();
    private static readonly HashSet<string> SkippedNames = new(StringComparer.Ordinal) { "ModdedWarning", "ReleaseInfo" };

    public static void Apply(Control root)
    {
        if (!GodotObject.IsInstanceValid(root) || !root.IsInsideTree()) return;
        Visit(root, root);
    }

    private static void Visit(Control root, Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is not Control control || !GodotObject.IsInstanceValid(control)) continue;
            if (InCardFace(control, root) || SkippedNames.Contains(control.Name.ToString()))
            {
                // Card faces keep their artwork's own text sizing; the status lines belong to the main menu.
                continue;
            }
            if (control.IsVisibleInTree())
            {
                if (control is RichTextLabel rich) ScaleRich(rich);
                else if (control is Label label) ScaleLabel(label);
            }
            Visit(root, control);
        }
    }

    private static bool InCardFace(Control control, Control root)
    {
        for (Node? node = control; node != null && node != root; node = node.GetParent())
        {
            string name = node.GetType().Name;
            if (name is "NCard" or "NCardHolder" or "NCardVisuals" or "NDeckViewScreen" ||
                name.StartsWith("NCardHolder", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static void ScaleLabel(Label label)
    {
        float points = label.GetThemeFontSize("font_size") * Math.Abs(label.GetGlobalTransformWithCanvas().Scale.Y) / PortraitBands.UnitsPerPoint;
        if (points <= 0f || points >= MinTextPoints) return;
        int size = label.GetThemeFontSize("font_size");
        int wanted = Mathf.RoundToInt(size * Math.Min(MaxScale, MinTextPoints / points));
        if (wanted <= size) return;
        KeepFont(label, "font_size", size);
        label.AddThemeFontSizeOverride("font_size", wanted);
    }

    private static void ScaleRich(RichTextLabel rich)
    {
        float points = rich.GetThemeFontSize("normal_font_size") * Math.Abs(rich.GetGlobalTransformWithCanvas().Scale.Y) / PortraitBands.UnitsPerPoint;
        if (points <= 0f || points >= MinTextPoints) return;
        int size = rich.GetThemeFontSize("normal_font_size");
        int wanted = Mathf.RoundToInt(size * Math.Min(MaxScale, MinTextPoints / points));
        if (wanted > size)
        {
            KeepFont(rich, "normal_font_size", size);
            rich.AddThemeFontSizeOverride("normal_font_size", wanted);
        }
        // MegaRichTextLabel sizes itself up to its ceiling; raising that lets it use the box it already has.
        int max = rich.Get("MaxFontSize").AsInt32();
        if (max > 0 && wanted > max) KeepProperty(rich, "MaxFontSize", Variant.From(max), Variant.From(wanted));
    }

    private static void KeepFont(Control label, string property, int original)
    {
        var key = (label.GetInstanceId(), property);
        if (!Fonts.ContainsKey(key)) Fonts[key] = (label.HasThemeFontSizeOverride(property), original);
    }

    private static void KeepProperty(GodotObject node, string property, Variant original, Variant value)
    {
        var key = (node.GetInstanceId(), property);
        if (!Properties.ContainsKey(key)) Properties[key] = original;
        node.Set(property, value);
    }

    public static void Release()
    {
        foreach (((ulong id, string property), Variant value) in Properties)
            if (GodotObject.InstanceFromId(id) is GodotObject node && GodotObject.IsInstanceValid(node))
                node.Set(property, value);
        Properties.Clear();
        foreach (((ulong id, string property), (bool had, int value)) in Fonts)
        {
            if (GodotObject.InstanceFromId(id) is not Control label || !GodotObject.IsInstanceValid(label)) continue;
            if (had) label.AddThemeFontSizeOverride(property, value);
            else label.RemoveThemeFontSizeOverride(property);
            // A game label sizes its text when its box or ceiling changes; size it again with the desktop values.
            if (label is RichTextLabel rich && rich.HasMethod("AdjustFontSize")) rich.Call("AdjustFontSize");
        }
        Fonts.Clear();
    }

    /// <summary>Forgets the recorded fonts of screens that closed (their nodes are gone).</summary>
    public static void PruneDead()
    {
        foreach (var key in Fonts.Keys.Where(k => !GodotObject.IsInstanceIdValid(k.Id)).ToList()) Fonts.Remove(key);
        foreach (var key in Properties.Keys.Where(k => !GodotObject.IsInstanceIdValid(k.Id)).ToList()) Properties.Remove(key);
    }
}
