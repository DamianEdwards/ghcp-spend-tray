using GHSpend.App.Platform;

namespace GHSpend.App.Native;

// Shell callbacks require a hidden top-level HWND even though all visible UI is Reactor.
internal sealed class TrayHost : ShellWindow
{
    private readonly uint _taskbarCreated;
    private string _tooltip = "GHSpend | Starting";
    internal TrayIcon Icon { get; }
    internal event Action? OpenRequested, SettingsRequested, RefreshRequested, ExitRequested, ResumeRequested;
    internal event Action? NotificationClicked;

    internal TrayHost(string? portableDirectory)
    {
        Icon = new TrayIcon(Handle, portableDirectory);
        _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreated == 0) throw new InvalidOperationException("Cannot register taskbar restart recovery.");
    }
    internal void Update(string tooltip) { _tooltip = tooltip; Icon.Update(tooltip); }
    internal unsafe (PixelRect Anchor, PixelRect Work, uint Dpi) Placement()
    {
        var rect = Icon.GetBounds();
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
        if (message == _taskbarCreated) { Icon.Add(); Icon.Update(_tooltip); return 0; }
        if (message == Win32.WM_POWERBROADCAST && wParam is 7 or 18) { ResumeRequested?.Invoke(); return 1; }
        if (message != Win32.WM_TRAY) return null;
        var action = (int)((nuint)lParam & 0xFFFF);
        if (action is 0x400 or 0x401) OpenRequested?.Invoke();
        else if (action == 0x405) NotificationClicked?.Invoke();
        else if (action == 0x7B) ContextMenu();
        return 0;
    }
    private void ContextMenu()
    {
        var menu = Win32.CreatePopupMenu();
        if (menu == 0) throw new InvalidOperationException("Cannot create the tray menu.");
        try
        {
            Win32.AppendMenu(menu, 0, 1, "Open GHSpend");
            Win32.AppendMenu(menu, 0, 2, "Refresh now");
            Win32.AppendMenu(menu, 0, 3, "Settings");
            Win32.AppendMenu(menu, 0x800, 0, null);
            Win32.AppendMenu(menu, 0, 4, "Exit");
            Win32.GetCursorPos(out var point);
            Win32.SetForegroundWindow(Handle);
            var command = Win32.TrackPopupMenuEx(menu, 0x102, point.x, point.y, Handle, 0);
            Win32.PostMessage(Handle, 0, 0, 0);
            switch (command)
            {
                case 1: OpenRequested?.Invoke(); break;
                case 2: RefreshRequested?.Invoke(); break;
                case 3: SettingsRequested?.Invoke(); break;
                case 4: ExitRequested?.Invoke(); break;
            }
        }
        finally { Win32.DestroyMenu(menu); }
    }
    protected override void ReleaseResources() => Icon.Dispose();
}
