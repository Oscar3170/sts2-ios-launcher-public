using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Orbs;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// Orb checks: the native queue must contain the expected number of orbs. Their placement (safe frame,
/// spacing, order, values) is the host's own layout and is implemented by a host partial: the launcher's compact arc in
/// LauncherHeaderChecks.cs.
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

    // Implemented by the host (LauncherHeaderChecks.cs): the launcher's portrait arc and safe frame.
    partial void CheckOrbLayout(string phase, int expected);
}
