using System.Runtime.InteropServices;
using System.Text;
using GHCPSpendTray.Shared;
using GHCPSpendTray.App.Platform;

namespace GHCPSpendTray.App.Native;

internal static partial class CommandLine
{
    internal static int Run(string[] arguments)
    {
        // Preserve redirected pipes. Attaching to the parent's console first can
        // replace those handles and break command substitution.
        nint output = GetStdHandle(-11);
        if (output == 0 || output == -1) _ = AttachConsole(uint.MaxValue);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
        try
        {
            bool explicitDirectory = arguments.Contains("--data-dir", StringComparer.Ordinal);
            bool packaged = PackageContext.IsPackaged;
            bool readsUsage = arguments[0] is "usage" or "prompt";
            string directory = !readsUsage || explicitDirectory ? "" : packaged ? PackageContext.DataDirectory :
                throw new ArgumentException("Use the installed GHCPSpendTray command, or --data-dir <absolute-directory> for development.");
            var result = UsageCommands.Run(arguments, directory,
                executable: packaged ? "ghcp-spend-tray.exe" : Environment.ProcessPath);
            if (result.Output.Length > 0) Console.Out.WriteLine(result.Output);
            if (result.Error is not null) Console.Error.WriteLine(result.Error);
            return result.ExitCode;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or COMException or InvalidOperationException)
        {
            Console.Error.WriteLine("GHCPSpendTray usage command failed. Check installation and the data-directory option.");
            return 1;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int standardHandle);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);
}
