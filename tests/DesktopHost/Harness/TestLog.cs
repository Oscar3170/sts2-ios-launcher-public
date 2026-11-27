using MegaCrit.Sts2.Core.Logging;

namespace STS2Mobile.DesktopHost.Harness;

internal static class Log
{
    private static readonly Logger Logger = new("STS2MobileDesktopHost", LogType.Generic);

    public static void Info(string message) => Logger.Info(message);

    public static void Warn(string message) => Logger.Warn(message);

    public static void Error(string message) => Logger.Error(message);
}
