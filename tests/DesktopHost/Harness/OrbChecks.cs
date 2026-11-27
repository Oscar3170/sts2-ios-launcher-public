using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Orbs;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// Orb checks shared by both hosts: the native queue must contain the expected number of orbs. Their placement (safe frame,
/// spacing, order, values) is each host's own layout and is implemented by a host partial: TouchSpireUI's arc in
/// HostLayoutChecks.cs, the launcher's compact arc in LauncherHeaderChecks.cs.
/// </summary>
internal sealed partial class TestDriver
{
    private void CheckOrbBounds(string phase, int expected)
    {
        CheckOrbCount(phase, expected);
        CheckOrbLayout(phase, expected);
    }

    private void CheckOrbCount(string phase, int expected)
    {
        NOrb[] orbs = FindAll<NOrb>(_tree.Root).Where(o => !o.IsQueuedForDeletion()).ToArray();
        _report.Check($"orbs_{phase}_count", orbs.Length == expected, $"slots={orbs.Length}");
    }

    // Implemented by the TouchSpire host only (Orbs.cs): TouchSpireUI's portrait arc and safe frame.
    partial void CheckOrbLayout(string phase, int expected);
}
