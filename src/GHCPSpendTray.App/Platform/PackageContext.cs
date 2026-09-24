using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.Storage;

namespace GHCPSpendTray.App.Platform;

internal static partial class PackageContext
{
    internal static bool IsPackaged
    {
        get
        {
            uint length = 0;
            var result = GetCurrentPackageFullName(ref length, 0);
            return result switch
            {
                122 => true,
                15700 => false,
                _ => throw new Win32Exception(result, "Could not determine the application's package identity.")
            };
        }
    }
    internal static string FamilyName => Package.Current.Id.FamilyName;
    internal static string DataDirectory => Path.Combine(ApplicationData.Current.LocalFolder.Path, "Data");

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref uint packageFullNameLength, nint packageFullName);
}
