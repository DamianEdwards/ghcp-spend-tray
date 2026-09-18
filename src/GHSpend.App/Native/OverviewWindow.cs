namespace GHSpend.App.Native;

internal sealed class OverviewWindow : NativeWindow
{
    private readonly IApplicationController _controller;
    private readonly TrayIcon _tray;
    private readonly Sparkline _graph;
    private readonly uint _taskbarCreated;
    private readonly List<NativeWindow> _dialogs = [];
    private readonly nint _total, _status, _accounts, _details, _progress, _history, _explanation;
    private readonly nint _refresh, _add, _settings, _reconnect, _edit, _remove, _selectedRefresh;
    private DashboardView _view = new("Consumption unavailable", "Starting...", "GHSpend | Starting", []);
    private string? _notificationAccount;
    internal OverviewWindow(IApplicationController controller) : base("GHSpend - Copilot consumption", 880, 680)
    {
        _controller = controller;
        _total = Label("Consumption unavailable");
        _status = Label("Starting...");
        _accounts = Control("LISTBOX", "", 100, Win32.WS_TABSTOP | Win32.WS_BORDER | Win32.WS_VSCROLL | 1);
        _details = Control("EDIT", "Add an account to monitor consumption.", 101,
            Win32.WS_TABSTOP | 0x800 | 0x4 | Win32.WS_VSCROLL);
        _progress = Control("msctls_progress32", "Allocation consumed", 102);
        Win32.SendMessage(_progress, 0x406, 0, 1000);
        _history = Label("Collecting history");
        _graph = new Sparkline(Handle);
        _explanation = Label("Consumption is AI-credit value in USD, not an invoice, internal budget, or all-product spend. " +
            "Observed rates are estimates between successful polls. Gaps and corrections are not treated as zero.");
        _refresh = Button("&Refresh all", 10);
        _add = Button("&Add account", 11);
        _settings = Button("&Settings", 12);
        _selectedRefresh = Button("Refresh &selected", 13);
        _reconnect = Button("Re&connect", 14);
        _edit = Button("&Edit account", 15);
        _remove = Button("Re&move", 16);
        _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreated == 0) throw new InvalidOperationException("Cannot register Explorer restart recovery.");
        _tray = new TrayIcon(Handle, controller.Portable ? controller.DataDirectory : null);
        _controller.Changed += OnChanged;
        _controller.SetNotificationHandler((key, title, message) =>
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try
                {
                    _notificationAccount = key;
                    completion.TrySetResult(_tray.Notify(title, message));
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
            return completion.Task;
        });
        Layout();
    }
    internal void Start() => RunOperation(_controller.InitializeAsync);
    internal void SmokeCheck()
    {
        if ((int)Win32.SendMessage(_accounts, 0x18B, 0, 0) != 2)
            throw new InvalidOperationException("Synthetic account list was not rendered.");
        Win32.SendMessage(_accounts, 0x186, 0, 0);
        UpdateSelection();
        if (!Text(_details).Contains("105%", StringComparison.Ordinal))
            throw new InvalidOperationException("Over-allocation text was lost.");
        if ((int)Win32.SendMessage(_progress, 0x408, 0, 0) != 1000)
            throw new InvalidOperationException("Native progress did not clamp above 100%.");
        Win32.SendMessage(_accounts, 0x186, 1, 0);
        UpdateSelection();
        if (!Text(_details).Contains("16.5%", StringComparison.Ordinal))
            throw new InvalidOperationException("Account selection did not update details.");
        var process = Win32.GetCurrentProcess();
        uint gdi = Win32.GetGuiResources(process, 0), user = Win32.GetGuiResources(process, 1);
        for (int i = 0; i < 50; i++)
        {
            using var settings = new SettingsWindow(Handle, _controller, _tray);
            using var onboarding = new AddAccountWindow(Handle, _controller, null);
            if (Win32.GetDlgItem(onboarding.Handle, 3) != 0)
                throw new InvalidOperationException("Onboarding must not expose a client-ID input.");
            using var account = new AccountSettingsWindow(Handle, _controller, _view.Accounts[0]);
        }
        if (Win32.GetGuiResources(process, 0) > gdi + 2 || Win32.GetGuiResources(process, 1) > user + 2)
            throw new InvalidOperationException("Repeated dialog creation leaked native GUI resources.");
        if (!_tray.Notify("GHSpend native smoke test", "Synthetic test of native Shell notification submission. No real account data."))
            throw new InvalidOperationException("Windows rejected the smoke notification.");
    }
    internal void ShowAccount(string? key = null)
    {
        if (key is not null)
        {
            var index = _view.Accounts.ToList().FindIndex(a => a.Key == key);
            if (index >= 0) Win32.SendMessage(_accounts, 0x186, (nuint)index, 0);
            UpdateSelection();
        }
        Show();
    }
    private void OnChanged(DashboardView view) => Post(() =>
    {
        string? selected = Selected()?.Key;
        _view = view;
        Win32.SetWindowText(_total, view.Total);
        Win32.SetWindowText(_status, view.Status);
        _tray.Update(view.Tooltip);
        Win32.SendMessage(_accounts, 0x184, 0, 0);
        foreach (var account in view.Accounts)
            Win32.SendText(_accounts, 0x180, 0, $"{account.Name} ({account.Login}) - {account.Host}");
        var index = selected is null ? 0 : view.Accounts.ToList().FindIndex(a => a.Key == selected);
        if (view.Accounts.Count > 0) Win32.SendMessage(_accounts, 0x186, (nuint)Math.Max(index, 0), 0);
        UpdateSelection();
    });
    private AccountView? Selected()
    {
        var index = (int)Win32.SendMessage(_accounts, 0x188, 0, 0);
        return index >= 0 && index < _view.Accounts.Count ? _view.Accounts[index] : null;
    }
    private void UpdateSelection()
    {
        var account = Selected();
        Win32.SetWindowText(_details, account?.Details ?? "Add an account to monitor consumption.\r\n\r\n" +
            "Sign-in uses the GitHub CLI OAuth application. No client ID or app registration needs to be entered.");
        Win32.SetWindowText(_history, account?.HistoryText ?? "Collecting history");
        Win32.SendMessage(_progress, 0x402, (nuint)Math.Clamp((account?.Percent ?? 0m) * 10m, 0m, 1000m), 0);
        Win32.SetWindowText(_progress, account?.Percent is decimal percent ? $"{percent:F2}% of allocation consumed" : "Allocation percentage not available");
        _graph.SetPoints(account?.Graph ?? []);
        foreach (var button in new[] { _selectedRefresh, _reconnect, _edit, _remove })
            Win32.EnableWindow(button, account is null ? 0 : 1);
    }
    protected override void Layout()
    {
        var (width, height) = ClientSize();
        int w = Math.Max(520, width), h = Math.Max(440, height);
        Place(_total, 16, 12, w - 32, 26);
        Place(_status, 16, 42, w - 32, 44);
        Place(_accounts, 16, 92, w - 32, 100);
        Place(_details, 16, 202, w - 32, Math.Max(92, h - 440));
        var y = Math.Max(304, h - 228);
        Place(_progress, 16, y, w - 32, 18);
        Place(_history, 16, y + 24, w - 32, 38);
        Place(_graph.Handle, 16, y + 66, w - 32, 60);
        Place(_selectedRefresh, 16, y + 132, 138, 28);
        Place(_reconnect, 164, y + 132, 105, 28);
        Place(_edit, 279, y + 132, 112, 28);
        Place(_remove, 401, y + 132, 92, 28);
        Place(_explanation, 16, y + 166, w - 32, 54);
        // Main actions stay above account details, independent of the selected account.
        Place(_refresh, 16, 164, 110, 28);
        Place(_add, 136, 164, 120, 28);
        Place(_settings, 266, 164, 100, 28);
        Place(_accounts, 16, 88, w - 32, 68);
    }
    protected override void Command(int id, int notification)
    {
        if (id == 100 && notification == 1) { UpdateSelection(); return; }
        switch (id)
        {
            case 10: RunOperation(() => _controller.RefreshAsync()); break;
            case 11: AddAccount(); break;
            case 12:
                var settings = new SettingsWindow(Handle, _controller, _tray);
                TrackDialog(settings); break;
            case 13: if (Selected() is { } refresh) RunOperation(() => _controller.RefreshAsync(refresh.Key)); break;
            case 14: if (Selected() is { } reconnect) AddAccount(reconnect); break;
            case 15:
                if (Selected() is { } edit)
                {
                    var dialog = new AccountSettingsWindow(Handle, _controller, edit);
                    TrackDialog(dialog);
                }
                break;
            case 16:
                if (Selected() is { } remove && Win32.MessageBox(Handle,
                    $"Remove {remove.Login} from local monitoring and delete its stored credential?\n\n" +
                    "This does not revoke the OAuth grant. Manage authorized apps on the account's GitHub settings page.",
                    "Remove account", Win32.MB_YESNO | Win32.MB_ICONWARNING) == 6)
                    RunOperation(() => _controller.RemoveAsync(remove.Key));
                break;
            case 17:
                RunOperation(() => _controller.SaveSettingsAsync(_controller.Settings with { Startup = !_controller.Settings.Startup }));
                break;
            case 18: Dispose(); Win32.PostQuitMessage(0); break;
        }
    }
    private void AddAccount(AccountView? reconnect = null)
    {
        var window = new AddAccountWindow(Handle, _controller, reconnect);
        TrackDialog(window);
    }
    private void TrackDialog(NativeWindow window)
    {
        _dialogs.Add(window);
        window.Closed += dialog => _dialogs.Remove(dialog);
        window.Show();
    }
    protected override nint? Message(uint message, nuint wParam, nint lParam)
    {
        if (message == _taskbarCreated)
        {
            _tray.Add();
            _tray.Update(_view.Tooltip);
            return 0;
        }
        if (message == Win32.WM_POWERBROADCAST && (wParam == 7 || wParam == 18))
        {
            RunOperation(_controller.ResumeAsync); return 1;
        }
        if (message == Win32.WM_TRAY)
        {
            var action = (int)((nuint)lParam & 0xFFFF);
            if (action is 0x400 or 0x401 or 0x202) ShowAccount();
            if (action == 0x405) ShowAccount(_notificationAccount);
            if (action is 0x7B or 0x205) ContextMenu();
            return 0;
        }
        return null;
    }
    private void ContextMenu()
    {
        var menu = Win32.CreatePopupMenu();
        if (menu == 0) throw new InvalidOperationException("Cannot open the tray menu.");
        try
        {
            Win32.AppendMenu(menu, 0, 19, "&Open");
            Win32.AppendMenu(menu, 0, 10, "&Refresh now");
            Win32.AppendMenu(menu, 0, 11, "&Add account");
            Win32.AppendMenu(menu, 0, 12, "&Settings");
            Win32.AppendMenu(menu, _controller.Portable ? 1u : (_controller.Settings.Startup ? 8u : 0u), 17, "Start with &Windows");
            Win32.AppendMenu(menu, 0x800, 0, null);
            Win32.AppendMenu(menu, 0, 18, "E&xit");
            Win32.GetCursorPos(out var point);
            Win32.SetForegroundWindow(Handle);
            var command = Win32.TrackPopupMenuEx(menu, 0x100 | 2, point.x, point.y, Handle, 0);
            Win32.PostMessage(Handle, 0, 0, 0);
            if (command == 19) ShowAccount();
            else if (command != 0) Command(command, 0);
        }
        finally { Win32.DestroyMenu(menu); }
    }
    protected override void ReleaseResources()
    {
        _controller.Changed -= OnChanged;
        foreach (var dialog in _dialogs.ToArray()) dialog.Dispose();
        _graph.Dispose();
        _tray.Dispose();
    }
}
