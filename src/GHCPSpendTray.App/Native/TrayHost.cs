using GHCPSpendTray.App.Platform;
using GHCPSpendTray.Core;

namespace GHCPSpendTray.App.Native;

// Shell callbacks require a hidden top-level HWND even though all visible UI is Reactor.
internal sealed class TrayHost : ShellWindow
{
    private readonly uint _taskbarCreated;
    private readonly TrayIconSet _icons;
    private uint _activeId = 1;
    private TrayPresentation _presentation = TrayPresentation.Unavailable;
    internal IReadOnlyCollection<TrayIcon> Icons => _icons.Icons;
    internal event Action<string?>? OpenRequested, NotificationClicked;
    internal event Action? SettingsRequested, RefreshRequested, ExitRequested, ResumeRequested;
    internal event Action? AppearanceChanged;

    internal TrayHost(string? portableDirectory, TrayIcon.ShellCall? shell = null)
    {
        _icons = new(Handle, portableDirectory, shell);
        try
        {
            _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
            if (_taskbarCreated == 0) throw new InvalidOperationException("Cannot register taskbar restart recovery.");
            Update(_presentation);
        }
        catch { Dispose(); throw; }
    }
    internal void Update(TrayPresentation presentation)
    {
        _presentation = presentation;
        _icons.Update(presentation, IconSize, TrayIconRenderer.SystemPalette());
    }
    internal bool ContainsCursor() => Icons.Any(icon => icon.ContainsCursor());
    internal bool Notify(NotificationView notification)
    {
        var icon = _icons.ForAccount(notification.AccountKey) ?? _icons.Primary;
        return icon.Notify(notification, TrayIconRenderer.NotificationSizeForDpi(Monitor(icon).Dpi),
            TrayIconRenderer.SystemPalette());
    }
    private int IconSize(TrayIcon? icon) =>
        TrayIconRenderer.SizeForDpi(Monitor(icon).Dpi);
    internal unsafe (PixelRect Anchor, PixelRect Work, uint Dpi) Placement()
    {
        return Monitor(_icons.Find(_activeId) ?? _icons.Primary);
    }
    private unsafe (PixelRect Anchor, PixelRect Work, uint Dpi) Monitor(TrayIcon? icon)
    {
        var rect = icon?.GetBounds() ?? new Win32.RECT();
        var monitor = Win32.MonitorFromRect(ref rect, 2);
        var info = new Win32.MONITORINFO { cbSize = (uint)sizeof(Win32.MONITORINFO) };
        if (Win32.GetMonitorInfo(monitor, ref info) == 0)
            throw new InvalidOperationException("Cannot read the tray monitor's work area.");
        if (Win32.GetDpiForMonitor(monitor, 0, out uint dpi, out _) != 0)
            dpi = Win32.GetDpiForWindow(Handle);
        return (new(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top),
            new(info.work.left, info.work.top, info.work.right - info.work.left, info.work.bottom - info.work.top), dpi);
    }
    protected override nint? Message(uint message, nuint wParam, nint lParam)
    {
        if (message == _taskbarCreated) { _icons.Restore(); Update(_presentation); return 0; }
        if (message is Win32.WM_DPICHANGED or 0x1A or 0x7E or 0x31A)
        { Update(_presentation); AppearanceChanged?.Invoke(); return 0; }
        if (message == Win32.WM_POWERBROADCAST && wParam is 7 or 18) { ResumeRequested?.Invoke(); return 1; }
        if (message != Win32.WM_TRAY) return null;
        var action = (int)((nuint)lParam & 0xFFFF);
        uint id = (uint)(((nuint)lParam >> 16) & 0xFFFF);
        if (_icons.Find(id) is not { } source) return 0;
        _activeId = id;
        // Version four supplies NIN_SELECT as well as raw mouse messages. Handle only
        // semantic selection, so a physical click is not toggled again on button-up.
        if (action is Win32.NIN_SELECT or Win32.NIN_KEYSELECT) OpenRequested?.Invoke(source.AccountKey);
        else if (action == Win32.NIN_BALLOONUSERCLICK) NotificationClicked?.Invoke(source.NotificationAccount);
        else if (action == Win32.WM_CONTEXTMENU) ContextMenu();
        return 0;
    }
    private void ContextMenu()
    {
        var menu = Win32.CreatePopupMenu();
        if (menu == 0) throw new InvalidOperationException("Cannot create the tray menu.");
        try
        {
            Win32.AppendMenu(menu, 0, 1, "Open GHCPSpendTray");
            Win32.AppendMenu(menu, 0, 2, "Refresh now");
            Win32.AppendMenu(menu, 0, 3, "Settings");
            Win32.AppendMenu(menu, 0x800, 0, null);
            Win32.AppendMenu(menu, 0, 4, "Exit");
            Win32.GetCursorPos(out var point);
            Win32.SetForegroundWindow(Handle);
            var command = Win32.TrackPopupMenuEx(menu, 0x102, point.x, point.y, Handle, 0);
            Win32.PostMessage(Handle, 0, 0, 0);
            ExecuteMenuCommand(command);
        }
        finally { Win32.DestroyMenu(menu); }
    }
    internal void ExecuteMenuCommand(int command)
    {
        switch (command)
        {
            case 1: OpenRequested?.Invoke(null); break;
            case 2: RefreshRequested?.Invoke(); break;
            case 3: SettingsRequested?.Invoke(); break;
            case 4: ExitRequested?.Invoke(); break;
        }
    }
    protected override void ReleaseResources() => _icons.Dispose();
}
