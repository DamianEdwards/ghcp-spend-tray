using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GHCPSpendTray.App.Native;

internal static unsafe class ShellServices
{
    internal static void Open(string destination, nint owner)
    {
        fixed (char* verb = "open")
        fixed (char* file = destination)
        {
            var info = new Win32.SHELLEXECUTEINFO
            {
                cbSize = (uint)sizeof(Win32.SHELLEXECUTEINFO),
                fMask = 0x400 | 0x100,
                hwnd = owner, lpVerb = verb, lpFile = file, nShow = 1
            };
            if (Win32.ShellExecuteEx(ref info) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not open the requested destination.");
        }
    }
    internal static void CopyText(nint owner, string text)
    {
        if (Win32.OpenClipboard(owner) == 0) throw new InvalidOperationException("The clipboard is busy. Try again.");
        nint memory = 0;
        try
        {
            memory = Win32.GlobalAlloc(0x42, checked((nuint)((text.Length + 1) * 2)));
            if (memory == 0) throw new OutOfMemoryException();
            var address = Win32.GlobalLock(memory);
            if (address == 0) throw new Win32Exception("Cannot lock clipboard memory.");
            try
            {
                text.AsSpan().CopyTo(new Span<char>((void*)address, text.Length));
                ((char*)address)[text.Length] = '\0';
            }
            finally { Win32.GlobalUnlock(memory); }
            if (Win32.EmptyClipboard() == 0 || Win32.SetClipboardData(13, memory) == 0)
                throw new Win32Exception("Windows rejected the clipboard data.");
            memory = 0;
        }
        finally
        {
            if (memory != 0) Win32.GlobalFree(memory);
            Win32.CloseClipboard();
        }
    }
}

internal sealed unsafe class TrayIcon : IDisposable
{
    private Win32.NOTIFYICONDATA _data;
    private bool _added;
    private readonly nint _icon;
    internal bool ContainsCursor()
    {
        var id = new Win32.NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)sizeof(Win32.NOTIFYICONIDENTIFIER),
            hWnd = _data.hWnd, uID = _data.uID, guidItem = _data.guidItem
        };
        return Win32.ShellNotifyIconGetRect(ref id, out var rect) == 0 &&
            Win32.GetCursorPos(out var point) != 0 &&
            point.x >= rect.left && point.x < rect.right &&
            point.y >= rect.top && point.y < rect.bottom;
    }
    internal Win32.RECT GetBounds()
    {
        var id = new Win32.NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)sizeof(Win32.NOTIFYICONIDENTIFIER),
            hWnd = _data.hWnd, uID = _data.uID, guidItem = _data.guidItem
        };
        if (Win32.ShellNotifyIconGetRect(ref id, out var rect) == 0) return rect;
        if (Win32.GetCursorPos(out var point) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot locate the tray icon or pointer.");
        return new() { left = point.x, right = point.x + 1, top = point.y, bottom = point.y + 1 };
    }
    internal TrayIcon(nint owner, string? portableDirectory = null)
    {
        _icon = Win32.LoadImage(Win32.GetModuleHandle(null), 32512, 1, 32, 32, 0);
        if (_icon == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot load the GHCPSpendTray icon.");
        _data = new Win32.NOTIFYICONDATA
        {
            cbSize = (uint)sizeof(Win32.NOTIFYICONDATA),
            hWnd = owner, uID = 1, hIcon = _icon, uCallbackMessage = Win32.WM_TRAY,
            guidItem = portableDirectory is null ? new Guid("5d9db52a-41a0-4b3d-910e-031c331ef83a") :
                new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("GHCPSpendTray.Tray/" +
                    Path.GetFullPath(portableDirectory).ToUpperInvariant())).AsSpan(0, 16))
        };
        try { Add(); }
        catch { Win32.DestroyIcon(_icon); throw; }
    }
    internal void Add()
    {
        _data.uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_GUID | Win32.NIF_SHOWTIP;
        fixed (char* tip = _data.szTip) Set(tip, 128, "GHCPSpendTray | Starting");
        if (Win32.ShellNotifyIcon(Win32.NIM_ADD, ref _data) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows rejected the tray icon.");
        _added = true;
        _data.uTimeoutOrVersion = 4;
        if (Win32.ShellNotifyIcon(Win32.NIM_SETVERSION, ref _data) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot enable keyboard-accessible tray behavior.");
    }
    internal void Update(string tooltip)
    {
        _data.uFlags = Win32.NIF_TIP | Win32.NIF_GUID | Win32.NIF_SHOWTIP;
        fixed (char* tip = _data.szTip) Set(tip, 128, tooltip);
        if (Win32.ShellNotifyIcon(Win32.NIM_MODIFY, ref _data) == 0)
            Diagnostics.Record("Tray tooltip update rejected by Windows.");
    }
    internal bool Notify(string title, string message)
    {
        _data.uFlags = Win32.NIF_INFO | Win32.NIF_GUID;
        _data.dwInfoFlags = Win32.NIIF_INFO | Win32.NIIF_RESPECT_QUIET_TIME;
        fixed (char* value = _data.szInfoTitle) Set(value, 64, title);
        fixed (char* value = _data.szInfo) Set(value, 256, message);
        var submitted = Win32.ShellNotifyIcon(Win32.NIM_MODIFY, ref _data) != 0;
        if (!submitted) Diagnostics.Record("Shell notification submission rejected.");
        return submitted;
    }
    private static void Set(char* buffer, int capacity, string value)
    {
        var target = new Span<char>(buffer, capacity);
        target.Clear();
        var length = Math.Min(value.Length, capacity - 1);
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        value.AsSpan(0, length).CopyTo(target);
    }
    public void Dispose()
    {
        if (!_added) return;
        _data.uFlags = Win32.NIF_GUID;
        Win32.ShellNotifyIcon(Win32.NIM_DELETE, ref _data);
        Win32.DestroyIcon(_icon);
        _added = false;
    }
}
