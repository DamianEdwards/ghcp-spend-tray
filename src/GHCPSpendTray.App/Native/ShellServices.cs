using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using GHCPSpendTray.Core;

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
    internal delegate int ShellCall(uint message, ref Win32.NOTIFYICONDATA data);
    private Win32.NOTIFYICONDATA _data;
    private bool _added;
    private bool _disposed;
    private bool _updatePending;
    private TrayImage _icon;
    private TrayImage? _notificationIcon;
    private readonly ShellCall _shell;
    private (TrayIndicator Indicator, TrayIconStyle Style, int Size, TrayPalette Palette) _rendered;
    internal uint Id => _data.uID;
    internal string? AccountKey { get; }
    internal string? NotificationAccount { get; set; }
    internal nint ImageHandle => _icon.DangerousGetHandle();
    internal Guid Identity => _data.guidItem;
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
    internal TrayIcon(nint owner, uint id, string? portableDirectory, TrayIndicator indicator,
        TrayIconStyle style, int size, TrayPalette palette, ShellCall? shell = null)
    {
        _shell = shell ?? Win32.ShellNotifyIcon;
        AccountKey = indicator.AccountKey;
        _icon = TrayIconRenderer.Create(indicator, style, size, palette);
        _rendered = (indicator, style, size, palette);
        _data = new Win32.NOTIFYICONDATA
        {
            cbSize = (uint)sizeof(Win32.NOTIFYICONDATA),
            hWnd = owner, uID = id, hIcon = ImageHandle, uCallbackMessage = Win32.WM_TRAY,
            guidItem = StableIdentity(portableDirectory, indicator.AccountKey)
        };
        fixed (char* tip = _data.szTip) Set(tip, 128, indicator.Tooltip);
        try { Add(); }
        catch { _icon.Dispose(); throw; }
    }
    internal static Guid StableIdentity(string? portableDirectory, string? accountKey)
    {
        if (portableDirectory is null && accountKey is null)
            return new("5d9db52a-41a0-4b3d-910e-031c331ef83a");
        string scope = portableDirectory is null ? "installed" : Path.GetFullPath(portableDirectory).ToUpperInvariant();
        string identity = "GHCPSpendTray.Tray/" + scope + (accountKey is null ? "" : "/account/" + accountKey);
        return new(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
    }
    internal void Add()
    {
        _data.uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_GUID | Win32.NIF_SHOWTIP;
        if (_shell(Win32.NIM_ADD, ref _data) == 0)
        {
            var failure = new Win32Exception(Marshal.GetLastPInvokeError(), "Windows rejected the tray icon.");
            Diagnostics.RecordFailure("Shell NIM_ADD rejected", failure);
            throw failure;
        }
        _added = true;
        _data.uTimeoutOrVersion = 4;
        if (_shell(Win32.NIM_SETVERSION, ref _data) == 0)
        {
            var failure = new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot enable keyboard-accessible tray behavior.");
            Diagnostics.RecordFailure("Shell NIM_SETVERSION rejected", failure);
            Remove();
            throw failure;
        }
    }
    internal void Restore()
    {
        Remove();
        Add();
    }
    internal void Invalidate() => _updatePending = true;
    internal void Update(TrayIndicator indicator, TrayIconStyle style, int size, TrayPalette palette)
    {
        if (_added && !_updatePending && _rendered == (indicator, style, size, palette)) return;
        var image = TrayIconRenderer.Create(indicator, style, size, palette);
        var previous = _data;
        bool wasAdded = _added;
        try
        {
            _data.hIcon = image.DangerousGetHandle();
            _data.uFlags = Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_GUID | Win32.NIF_SHOWTIP;
            fixed (char* tip = _data.szTip) Set(tip, 128, indicator.Tooltip);
            if (_added && _shell(Win32.NIM_MODIFY, ref _data) == 0)
            {
                Diagnostics.Record($"Shell NIM_MODIFY rejected (native error {Marshal.GetLastPInvokeError()}).");
                _added = false;
            }
            if (!_added) Add();
        }
        catch { _data = previous; _added |= wasAdded; _updatePending = true; image.Dispose(); throw; }
        _icon.Dispose();
        _icon = image;
        _rendered = (indicator, style, size, palette);
        _updatePending = false;
    }
    internal bool Notify(NotificationView notification, int size, TrayPalette palette, bool updateAvailable = false)
    {
        var image = TrayIconRenderer.CreateNotification(notification, size, palette, updateAvailable);
        try
        {
            var data = _data;
            data.uFlags = Win32.NIF_INFO | Win32.NIF_GUID;
            data.dwInfoFlags = Win32.NIIF_USER | Win32.NIIF_LARGE_ICON | Win32.NIIF_RESPECT_QUIET_TIME;
            data.hBalloonIcon = image.DangerousGetHandle();
            Set(data.szInfoTitle, 64, notification.Title);
            Set(data.szInfo, 256, notification.Message);
            if (_shell(Win32.NIM_MODIFY, ref data) == 0)
            {
                Diagnostics.Record("Shell notification submission rejected.");
                image.Dispose();
                return false;
            }
        }
        catch { image.Dispose(); throw; }
        _notificationIcon?.Dispose();
        _notificationIcon = image;
        NotificationAccount = notification.AccountKey;
        return true;
    }
    private static void Set(char* buffer, int capacity, string value)
    {
        var target = new Span<char>(buffer, capacity);
        target.Clear();
        var length = Math.Min(value.Length, capacity - 1);
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        value.AsSpan(0, length).CopyTo(target);
    }
    private void Remove()
    {
        if (!_added) return;
        _data.uFlags = Win32.NIF_GUID;
        if (_shell(Win32.NIM_DELETE, ref _data) == 0)
            Diagnostics.Record("Tray removal rejected (the Shell may have restarted).");
        _added = false;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Remove();
        _notificationIcon?.Dispose();
        _icon.Dispose();
    }
}
