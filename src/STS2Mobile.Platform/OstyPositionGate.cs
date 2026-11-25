using System;

namespace STS2Mobile.Platform;

/// <summary>Leaves Osty's growth native while the active portrait stage owns his root position.</summary>
public static class OstyPositionGate
{
    public static Func<object, bool>? OwnsPosition { private get; set; }

    public static bool SuppressNativePosition(object creature) =>
        SafeAreaCanvas.IsPortrait && OwnsPosition?.Invoke(creature) == true;
}
