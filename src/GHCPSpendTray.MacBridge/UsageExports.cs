using System.Runtime.InteropServices;
using GHCPSpendTray.Shared;

namespace GHCPSpendTray.MacBridge;

public static class UsageExports
{
    [UnmanagedCallersOnly(EntryPoint = "ghcp_cli")]
    public static int Run(nint directory, int argumentCount, nint arguments)
    {
        try
        {
            if (argumentCount is < 1 or > 32)
            {
                Console.Error.WriteLine("Supply a shell command with at most 32 arguments.");
                return 2;
            }
            var values = new string[argumentCount];
            for (int index = 0; index < argumentCount; index++)
                values[index] = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(arguments, index * nint.Size)) ??
                    throw new ArgumentException("Missing argument.");
            var result = UsageCommands.Run(values, Marshal.PtrToStringUTF8(directory) ??
                throw new ArgumentException("Missing data directory."));
            if (result.Output.Length > 0) Console.Out.WriteLine(result.Output);
            if (result.Error is not null) Console.Error.WriteLine(result.Error);
            return result.ExitCode;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("GHCPSpendTray could not read saved usage.");
            return 1;
        }
    }
}
