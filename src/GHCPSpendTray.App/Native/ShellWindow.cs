using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GHCPSpendTray.App.Native;

internal abstract unsafe class ShellWindow : IDisposable
{
    private static readonly Dictionary<nint, ShellWindow> Windows = [];
    private static bool _registered;
    internal nint Handle { get; private set; }

    protected ShellWindow()
    {
        if (!_registered)
        {
            fixed (char* name = "GHCPSpendTray.ShellWindow")
            {
                var cls = new Win32.WNDCLASSEX
                {
                    cbSize = (uint)sizeof(Win32.WNDCLASSEX), lpfnWndProc = &WindowProc,
                    hInstance = Win32.GetModuleHandle(null), lpszClassName = name
                };
                if (Win32.RegisterClassEx(ref cls) == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot register the Shell callback window.");
            }
            _registered = true;
        }
        // A message-only HWND would miss Explorer's TaskbarCreated broadcast.
        Handle = Win32.CreateWindowEx(0x80, "GHCPSpendTray.ShellWindow", "GHCPSpendTray shell host", 0,
            0, 0, 1, 1, 0, 0, Win32.GetModuleHandle(null), 0);
        if (Handle == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot create the Shell callback window.");
        Windows.Add(Handle, this);
    }
    protected abstract nint? Message(uint message, nuint wParam, nint lParam);
    protected virtual void ReleaseResources() { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == Win32.WM_CLOSE) return 0;
            if (Windows.TryGetValue(hwnd, out var window) && window.Message(message, wParam, lParam) is { } result)
                return result;
        }
        catch (Exception ex)
        {
            Diagnostics.Record($"Shell callback failed ({ex.GetType().Name}).");
            Win32.MessageBox(0, "A tray operation failed. Check the diagnostic log.", "GHCPSpendTray", Win32.MB_ICONERROR);
        }
        return Win32.DefWindowProc(hwnd, message, wParam, lParam);
    }
    public void Dispose()
    {
        if (Handle == 0) return;
        ReleaseResources();
        Windows.Remove(Handle);
        Win32.DestroyWindow(Handle);
        Handle = 0;
        GC.SuppressFinalize(this);
    }
}
