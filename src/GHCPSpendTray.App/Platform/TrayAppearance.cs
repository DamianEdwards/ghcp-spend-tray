using Microsoft.Win32;

namespace GHCPSpendTray.App.Platform;

internal static class TrayAppearance
{
    internal static bool IsLightTheme()
    {
        return Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 1) is not int value || value != 0;
    }
}
