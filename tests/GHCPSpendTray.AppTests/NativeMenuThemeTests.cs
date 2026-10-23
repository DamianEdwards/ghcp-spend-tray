using System.Runtime.InteropServices;
using GHCPSpendTray.App.Native;

internal static partial class NativeMenuThemeTests
{
    internal static void Run(Action<bool, string> check, nint owner)
    {
        var originalMode = SetPreferredAppMode(NativeMenuTheme.AppMode.Default);
        SetPreferredAppMode(originalMode);
        foreach (var (light, contrast, expected) in new[]
        {
            (false, false, NativeMenuTheme.AppMode.ForceDark),
            (true, false, NativeMenuTheme.AppMode.ForceLight),
            (false, true, NativeMenuTheme.AppMode.Default),
            (true, true, NativeMenuTheme.AppMode.Default),
            (false, false, NativeMenuTheme.AppMode.ForceDark)
        })
        {
            using (var theme = NativeMenuTheme.Apply(owner, light, contrast))
            {
                check(theme is not null, "native menu theme exports are available on supported Windows");
                var mode = SetPreferredAppMode(NativeMenuTheme.AppMode.Default);
                SetPreferredAppMode(mode);
                check(mode == expected, "native menu follows the system theme and yields to high contrast");
                check((IsDarkModeAllowedForWindow(owner) != 0) == (!light && !contrast),
                    "native popup owner opts into dark mode only for a dark, non-contrast taskbar");
            }
            var restored = SetPreferredAppMode(NativeMenuTheme.AppMode.Default);
            SetPreferredAppMode(restored);
            check(restored == originalMode && IsDarkModeAllowedForWindow(owner) == 0,
                "closing a tray menu restores the app preference and clears its owner's dark-mode opt-in");
        }
    }

    [LibraryImport("uxtheme.dll", EntryPoint = "#135")]
    private static partial NativeMenuTheme.AppMode SetPreferredAppMode(NativeMenuTheme.AppMode mode);

    [LibraryImport("uxtheme.dll", EntryPoint = "#137")]
    private static partial byte IsDarkModeAllowedForWindow(nint owner);
}
