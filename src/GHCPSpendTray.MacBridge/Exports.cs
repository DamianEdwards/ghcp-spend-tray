using System.Runtime.InteropServices;
using System.Text.Json;

namespace GHCPSpendTray.MacBridge;

public static class Exports
{
    private static readonly BridgeRuntime Runtime = new();

    [UnmanagedCallersOnly(EntryPoint = "ghcp_request")]
    public static nint Request(nint json)
    {
        try
        {
            string text = Marshal.PtrToStringUTF8(json) ?? throw new ArgumentException("Missing command.");
            if (text.Length > 128 * 1024) throw new ArgumentException("Command too large.");
            var command = JsonSerializer.Deserialize(text, BridgeJsonContext.Default.Command)
                ?? throw new ArgumentException("Invalid command.");
            return Encode(Runtime.Send(command));
        }
        catch (Exception)
        {
            return Encode(new Receipt("Invalid native application request."));
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "ghcp_poll")]
    public static nint Poll()
    {
        try { return Marshal.StringToCoTaskMemUTF8(JsonSerializer.Serialize(Runtime.Poll(), BridgeJsonContext.Default.BridgeEventArray)); }
        catch (Exception) { return Marshal.StringToCoTaskMemUTF8("[{\"kind\":\"error\",\"error\":\"Could not read application state.\"}]"); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ghcp_free")]
    public static void Free(nint text) => Marshal.FreeCoTaskMem(text);

    [UnmanagedCallersOnly(EntryPoint = "ghcp_shutdown")]
    public static void Shutdown()
    {
        try { Runtime.Dispose(); }
        catch (Exception) { System.Diagnostics.Trace.TraceError("GHCPSpendTray shutdown failed."); }
    }

    private static nint Encode(Receipt receipt) =>
        Marshal.StringToCoTaskMemUTF8(JsonSerializer.Serialize(receipt, BridgeJsonContext.Default.Receipt));
}
