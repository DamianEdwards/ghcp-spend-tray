param(
    [Parameter(Mandatory = $true)][string] $ApplicationId,
    [Parameter(Mandatory = $true)][string] $AppArguments,
    [Parameter(Mandatory = $true)][string] $ProcessIdPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Write-Output "$([DateTimeOffset]::UtcNow.ToString('O')) Activation helper started"
Add-Type @'
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
public static class PackageSmokeActivation {
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager {
        void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
        void ActivateForFile(IntPtr item, IntPtr array, IntPtr verb, out uint processId);
        void ActivateForProtocol(IntPtr item, IntPtr array, out uint processId);
    }
    public static void Launch(string id, string arguments, string processIdPath) {
        Console.WriteLine("{0:O} Creating activation manager", DateTimeOffset.UtcNow);
        var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), true);
        var manager = (IApplicationActivationManager)Activator.CreateInstance(type);
        try {
            Console.WriteLine("{0:O} Calling ActivateApplication", DateTimeOffset.UtcNow);
            uint pid;
            manager.ActivateApplication(id, arguments, 0, out pid);
            File.WriteAllText(processIdPath, pid.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("{0:O} Activation returned process ID {1}", DateTimeOffset.UtcNow, pid);
        }
        finally {
            Console.WriteLine("{0:O} Releasing activation manager", DateTimeOffset.UtcNow);
            Marshal.ReleaseComObject(manager);
        }
    }
}
'@
[PackageSmokeActivation]::Launch($ApplicationId, $AppArguments, $ProcessIdPath)
Write-Output "$([DateTimeOffset]::UtcNow.ToString('O')) Activation helper completed"
