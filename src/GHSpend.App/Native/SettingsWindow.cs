namespace GHSpend.App.Native;

internal sealed class SettingsWindow : NativeWindow
{
    private readonly IApplicationController _controller;
    private readonly TrayIcon _tray;
    private readonly nint _intervalLabel, _interval, _thresholdLabel, _thresholds, _notifications,
        _startup, _note, _save, _cancel, _test, _folder;
    internal SettingsWindow(nint owner, IApplicationController controller, TrayIcon tray) : base("GHSpend settings", 580, 410, owner)
    {
        _controller = controller; _tray = tray;
        DefaultCommand = 10;
        var settings = controller.Settings;
        _intervalLabel = Label("&Polling interval in minutes (5-1440):");
        _interval = Edit(settings.PollMinutes.ToString(), 1);
        _thresholdLabel = Label("Default alert &thresholds (%), comma-separated:");
        _thresholds = Edit(settings.Thresholds, 2);
        _notifications = Control("BUTTON", "&Enable notifications", 3, Win32.WS_TABSTOP | 3);
        _startup = Control("BUTTON", "Start with &Windows", 4, Win32.WS_TABSTOP | 3);
        Win32.SendMessage(_notifications, 0xF1, settings.Notifications ? 1u : 0u, 0);
        Win32.SendMessage(_startup, 0xF1, settings.Startup ? 1u : 0u, 0);
        Win32.EnableWindow(_startup, controller.Portable ? 0 : 1);
        _note = Label("Thresholds are distinct positive percentages; values above 100 are allowed. " +
            "Windows may suppress submitted notifications under Do Not Disturb. Shell notifications do not promise history or activation after exit." +
            (controller.Portable ? "\r\nPortable mode: startup changes are disabled." : ""));
        _save = Button("&Save", 10); _cancel = Button("Cancel", 11);
        _test = Button("&Test notification", 12); _folder = Button("Open &data folder", 13);
        Layout();
    }
    protected override void Layout()
    {
        var (w, _) = ClientSize();
        Place(_intervalLabel, 16, 16, w - 32, 24); Place(_interval, 16, 44, 150, 27);
        Place(_thresholdLabel, 16, 82, w - 32, 24); Place(_thresholds, 16, 110, w - 32, 27);
        Place(_notifications, 16, 151, 230, 28); Place(_startup, 260, 151, 240, 28);
        Place(_note, 16, 191, w - 32, 85);
        Place(_test, 16, 286, 160, 30); Place(_folder, 186, 286, 160, 30);
        Place(_save, 356, 286, 80, 30); Place(_cancel, 446, 286, 80, 30);
    }
    protected override void Command(int id, int notification)
    {
        if (id == 10)
        {
            if (!int.TryParse(Text(_interval), out var minutes) || minutes is < 5 or > 1440)
            { Error("Enter a polling interval from 5 through 1440 minutes."); return; }
            var settings = new SettingsView(minutes, Text(_thresholds),
                Win32.SendMessage(_notifications, 0xF0, 0, 0) == 1, Win32.SendMessage(_startup, 0xF0, 0, 0) == 1);
            RunOperation(() => _controller.SaveSettingsAsync(settings), Dispose);
        }
        else if (id == 11) Dispose();
        else if (id == 12)
        {
            var accepted = _tray.Notify("GHSpend test", "Test notification. Windows notification settings may suppress display.");
            Win32.MessageBox(Handle, accepted ? "Submitted to Windows. Delivery is not guaranteed." :
                "Windows rejected the notification submission.", "GHSpend", accepted ? 0u : Win32.MB_ICONERROR);
        }
        else if (id == 13) ShellServices.Open(_controller.DataDirectory, Handle);
    }
    protected override void Closing() => Dispose();
}

internal sealed class AccountSettingsWindow : NativeWindow
{
    private readonly IApplicationController _controller;
    private readonly AccountView _account;
    private readonly nint _nameLabel, _name, _thresholdLabel, _thresholds, _note, _save, _cancel, _grant;
    internal AccountSettingsWindow(nint owner, IApplicationController controller, AccountView account) :
        base($"Account settings - {account.Login}", 580, 320, owner)
    {
        _controller = controller; _account = account;
        DefaultCommand = 10;
        var settings = controller.AccountSettings(account.Key);
        _nameLabel = Label("&Display name (optional):"); _name = Edit(settings.DisplayName, 1);
        _thresholdLabel = Label("Account alert &thresholds (%), blank to inherit defaults:");
        _thresholds = Edit(settings.Thresholds, 3);
        _note = Label("Local removal does not revoke the OAuth grant. Review authorized applications on this account's host.");
        _save = Button("&Save", 10); _cancel = Button("Cancel", 11); _grant = Button("Manage OAuth &grants", 12);
        Layout();
    }
    protected override void Layout()
    {
        var (w, _) = ClientSize();
        Place(_nameLabel, 16, 16, w - 32, 24); Place(_name, 16, 44, w - 32, 27);
        Place(_thresholdLabel, 16, 82, w - 32, 24); Place(_thresholds, 16, 110, w - 32, 27);
        Place(_note, 16, 150, w - 32, 50);
        Place(_grant, 16, 210, 200, 30); Place(_save, 350, 210, 80, 30); Place(_cancel, 440, 210, 80, 30);
    }
    protected override void Command(int id, int notification)
    {
        if (id == 10)
        {
            var name = Text(_name); var thresholds = Text(_thresholds);
            RunOperation(() => _controller.SaveAccountAsync(_account.Key, name, thresholds), Dispose);
        }
        else if (id == 11) Dispose();
        else if (id == 12) ShellServices.Open($"https://{_account.Host}/settings/applications", Handle);
    }
    protected override void Closing() => Dispose();
}
