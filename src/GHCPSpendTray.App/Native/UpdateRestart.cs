using System.Runtime.InteropServices;

namespace GHCPSpendTray.App.Native;

internal static partial class UpdateRestart
{
    internal static TimeSpan RemainingDelay(TimeSpan uptime) =>
        uptime < TimeSpan.FromSeconds(61) ? TimeSpan.FromSeconds(61) - uptime : TimeSpan.Zero;

    internal static void Register() =>
        Marshal.ThrowExceptionForHR(RegisterApplicationRestart("--startup", 1 | 2 | 8));

    internal static void Unregister() => Marshal.ThrowExceptionForHR(UnregisterApplicationRestart());

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterApplicationRestart(string commandLine, uint flags);

    [LibraryImport("kernel32.dll")]
    private static partial int UnregisterApplicationRestart();
}
