using System.Globalization;
using GHCPSpendTray.App.Native;
using GHCPSpendTray.Core;

namespace GHCPSpendTray.App.UI;

internal enum SettingsPage { Usage, General, Accounts, Notifications, About }

internal sealed class AppSession : IDisposable
{
    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _signIn;
    private readonly CancellationTokenSource _lifetime = new();
    private string? _notice;
    private readonly System.Threading.Timer _countdown;
    private bool _disposed;
    internal IApplicationController Controller { get; }
    internal DashboardView Dashboard { get; private set; } = new("Loading", "Loading accounts...", "GHCPSpendTray | Loading", []);
    internal SettingsPage Page { get; private set; } = SettingsPage.Usage;
    internal event Action? Changed;
    internal event Action? DashboardChanged;
    internal Action<SettingsPage>? OpenSettings { get; set; }
    internal Action? HideFlyout { get; set; }
    internal Func<bool>? TestNotification { get; set; }
    internal Action<string>? CopyToClipboard { get; set; }
    internal int Revision { get; private set; }
    internal bool Initialized { get; private set; }
    internal bool Busy { get; private set; }
    internal bool SigningIn => _signIn is not null;
    internal string? Error { get; private set; }
    internal string? Notice
    {
        get => _notice;
        private set { _notice = value; NoticeIsSuccess = false; }
    }
    internal bool NoticeIsSuccess { get; private set; }
    internal bool ConnectingAccount { get; private set; }
    internal bool EditingHost { get; private set; }
    internal bool CodeCopied { get; private set; }
    internal string? ClipboardError { get; private set; }
    internal string? SelectedAccount { get; private set; }
    internal bool ShowAddForm { get; private set; }
    internal bool CanGoBack => !_disposed && Page == SettingsPage.Accounts &&
        (ShowAddForm || SelectedAccount is { } key && Dashboard.Accounts.Any(account => account.Key == key));
    internal bool ConfirmRemove { get; set; }
    internal bool ShowAdvancedDetails { get; set; }
    internal HashSet<string> ExpandedUsageAccounts { get; } = new(StringComparer.Ordinal);
    internal DevicePrompt? Prompt { get; private set; }
    internal string Host { get; set; } = "github.com";
    internal bool CustomHost { get; private set; }
    internal string ClientId { get; private set; } = "";
    internal bool OfflineAccess { get; set; }
    internal string? ReconnectKey { get; private set; }
    internal string PollMinutes { get; set; } = "60";
    internal string Thresholds { get; set; } = "50, 80, 100";
    internal string Increment { get; set; } = "";
    internal bool Notifications { get; set; } = true;
    internal bool Startup { get; set; }
    internal TrayIconStyle TrayStyle { get; set; }
    internal TrayDisplayMode TrayMode { get; set; }
    internal HashSet<string> ExcludedTrayAccounts { get; } = new(StringComparer.Ordinal);
    internal string DisplayName { get; set; } = "";
    internal string AccountThresholds { get; set; } = "";
    internal string AccountIncrement { get; set; } = "";
    internal bool InheritIncrement { get; set; } = true;

    internal AppSession(IApplicationController controller, Action<Action> dispatch)
    {
        Controller = controller; _dispatch = dispatch;
        controller.Changed += OnDashboard;
        _countdown = new(_ => Post(() => { if (Prompt is not null) Notify(); }), null, 1000, 1000);
    }
    internal void Post(Action action)
    {
        if (!_disposed) _dispatch(() => { if (!_disposed) action(); });
    }
    internal void Notify() { Revision++; Changed?.Invoke(); }
    private void OnDashboard(DashboardView view) => Post(() =>
    {
        Dashboard = view;
        ExpandedUsageAccounts.RemoveWhere(key => !view.Accounts.Any(account => account.Key == key));
        DashboardChanged?.Invoke();
        Notify();
    });
    internal TrayPresentation PreviewTray(DateTimeOffset? now = null)
    {
        var states = Dashboard.TrayStates ?? [];
        return TrayUsage.Create(new()
        {
            PollIntervalMinutes = Controller.Settings.PollMinutes,
            TrayStyle = TrayStyle,
            TrayMode = TrayMode,
            Accounts = states.Select(state => state.Account with
            {
                ExcludeFromTray = ExcludedTrayAccounts.Contains(state.Account.Key)
            }).ToArray()
        }, states, now ?? DateTimeOffset.UtcNow);
    }
    internal void Initialize() => Run(Controller.InitializeAsync, () =>
    {
        Initialized = true;
        ReloadSettings();
    });
    internal void ReloadSettings()
    {
        var settings = Controller.Settings;
        PollMinutes = settings.PollMinutes.ToString(CultureInfo.InvariantCulture);
        Thresholds = settings.Thresholds;
        Increment = settings.SpendIncrementUsd?.ToString(CultureInfo.InvariantCulture) ?? "";
        Notifications = settings.Notifications; Startup = settings.Startup;
        TrayStyle = settings.TrayStyle; TrayMode = settings.TrayMode;
        ExcludedTrayAccounts.Clear();
        ExcludedTrayAccounts.UnionWith(settings.ExcludedTrayAccounts ?? []);
    }
    internal void Navigate(SettingsPage page)
    {
        if (SigningIn) CancelSignIn();
        Page = page; Error = null; Notice = null; ConfirmRemove = false;
        Notify();
    }
    internal void AddAccount()
    {
        CancelSignIn();
        Page = SettingsPage.Accounts; ShowAddForm = true; SelectedAccount = null;
        ReconnectKey = null; Host = "github.com"; CustomHost = false; ClientId = "";
        EditingHost = false;
        Error = null; Notice = null;
        OpenSettings?.Invoke(SettingsPage.Accounts);
        StartSignIn();
    }
    internal void EditAccount(string key)
    {
        try
        {
            CancelSignIn();
            var account = Controller.AccountSettings(key);
            SelectedAccount = key; ShowAddForm = false; ConfirmRemove = false;
            ShowAdvancedDetails = false;
            DisplayName = account.DisplayName; AccountThresholds = account.Thresholds;
            InheritIncrement = account.SpendIncrementUsd is null;
            AccountIncrement = account.SpendIncrementUsd?.ToString(CultureInfo.InvariantCulture) ?? "";
            Page = SettingsPage.Accounts; Error = null; Notice = null;
            OpenSettings?.Invoke(SettingsPage.Accounts); Notify();
        }
        catch (AppOperationException ex) { SetError(ex.Message); }
    }
    internal void Reconnect(AccountView account)
    {
        CancelSignIn();
        try
        {
            Host = account.Host; CustomHost = account.Host != "github.com";
            ClientId = CustomHost ? Controller.AccountClientId(account.Key) ?? "" : "";
            ReconnectKey = account.Key; SelectedAccount = null;
            ShowAddForm = true; Error = null; Notice = null;
            EditingHost = CustomHost && string.IsNullOrWhiteSpace(ClientId);
            if (EditingHost) Notify();
            else StartSignIn();
        }
        catch (Exception ex) { SetError(SafeMessage(ex)); }
    }
    internal void ChangeHost()
    {
        if (ReconnectKey is not null || ConnectingAccount) return;
        CancelSignIn();
        EditingHost = true; Error = null; Notice = null; Notify();
    }
    internal void SelectHost(bool custom)
    {
        if (ReconnectKey is not null || CustomHost == custom) return;
        CustomHost = custom;
        Host = custom ? "" : "github.com";
        ClientId = "";
        Notify();
    }
    internal void SetHost(string host)
    {
        if (ReconnectKey is not null) return;
        if (Host == host) return;
        Host = host;
        ClientId = "";
        try
        {
            if (HostResolver.Resolve(host).Host == "msft.ghe.com")
                ClientId = GitHubOAuth.MicrosoftEnterpriseClientId;
        }
        catch (ArgumentException) { /* Incomplete hostname while typing. */ }
        Notify();
    }
    internal void SetClientId(string value) { ClientId = value; Notify(); }
    internal void Run(Func<Task> operation, Action? success = null, Action? failure = null)
    {
        if (Busy) return;
        Busy = true; Error = null; Notice = null; Notify();
        _ = Task.Run(async () =>
        {
            try
            {
                await operation().ConfigureAwait(false);
                Post(() => success?.Invoke());
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Post(() => { failure?.Invoke(); SetError(SafeMessage(ex)); }); }
            finally { Post(() => { Busy = false; Notify(); }); }
        });
    }
    internal void Refresh(string? key = null) => Run(() => Controller.RefreshAsync(key));
    internal void RefreshAccount(string key) => Run(() => Controller.RefreshAccountAsync(key));
    internal void SaveGlobal()
    {
        if (!int.TryParse(PollMinutes, out var minutes) || minutes is < 5 or > 1440)
        { SetError("Enter a polling interval from 5 through 1440 minutes."); return; }
        if (!TryAmount(Increment, out var increment)) return;
        var next = new SettingsView(minutes, Thresholds, Notifications, Startup, increment,
            TrayStyle: TrayStyle, TrayMode: TrayMode, ExcludedTrayAccounts: ExcludedTrayAccounts.ToArray());
        Run(() => Controller.SaveSettingsAsync(next),
            () => { ReloadSettings(); Notice = "Settings saved."; },
            () => Startup = Controller.Settings.Startup);
    }
    internal void SaveAccount()
    {
        if (SelectedAccount is not { } key) return;
        decimal? amount = null;
        if (!InheritIncrement && !TryAmount(AccountIncrement, out amount)) return;
        if (!InheritIncrement) amount ??= 0;
        string name = DisplayName, thresholds = AccountThresholds;
        Run(() => Controller.SaveAccountAsync(key, name, thresholds, amount), () => Notice = "Account settings saved.");
    }
    private bool TryAmount(string text, out decimal? amount)
    {
        amount = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < 0 || decimal.Round(parsed, 2) != parsed)
        { SetError("Enter a USD increment with at most two decimal places, such as 50 or 12.50. Use 0 to disable."); return false; }
        amount = parsed;
        return true;
    }
    internal void RemoveAccount()
    {
        if (SelectedAccount is not { } key) return;
        Run(() => Controller.RemoveAsync(key), () =>
        {
            ExcludedTrayAccounts.Remove(key);
            SelectedAccount = null; ConfirmRemove = false; Notice = "Account removed from this device.";
        });
    }
    internal string HostDescription()
    {
        if (CustomHost && string.IsNullOrWhiteSpace(Host))
            return "Enter the web hostname and its OAuth app's client ID.";
        try { return Controller.ResolveHostDescription(Host); }
        catch (AppOperationException ex) { return ex.Message; }
    }
    internal void StartSignIn()
    {
        if (SigningIn || _disposed) return;
        if (CustomHost)
        {
            try { _ = GitHubOAuth.ResolveClientId(Host, ClientId); }
            catch (Exception ex) when (ex is ArgumentException or ServiceException)
            {
                Diagnostics.Record($"Account sign-in host validation failed ({ex.GetType().Name}).");
                SetError(ex.Message);
                return;
            }
        }
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _signIn = cancel; Prompt = null; ConnectingAccount = false; EditingHost = false;
        CodeCopied = false; ClipboardError = null; Error = null; Notice = null;
        string host = Host; bool offline = OfflineAccess; string? reconnect = ReconnectKey;
        string? clientId = CustomHost ? ClientId : null;
        Notify();
        _ = Task.Run(async () =>
        {
            try
            {
                await Controller.AddAsync(host, offline, reconnect,
                    prompt => Post(() =>
                    {
                        if (_signIn != cancel || ConnectingAccount) return;
                        Prompt = prompt;
                        CopyCode();
                    }),
                    () => Post(() =>
                    {
                        if (_signIn != cancel) return;
                        ConnectingAccount = true; Prompt = null; CodeCopied = false; ClipboardError = null; Notice = null; Notify();
                    }), cancel.Token, clientId).ConfigureAwait(false);
                Post(() =>
                {
                    if (_signIn != cancel) return;
                    ShowAddForm = false; SelectedAccount = null;
                    Notice = reconnect is null ? "Account connected." : "Account reconnected.";
                    NoticeIsSuccess = true;
                });
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch (Exception ex)
            {
                string message = SafeMessage(ex);
                Post(() => { if (_signIn == cancel) SetError(message); });
            }
            finally
            {
                Post(() =>
                {
                    if (_signIn == cancel)
                    {
                        _signIn = null; Prompt = null; ConnectingAccount = false;
                        CodeCopied = false; ClipboardError = null; Notify();
                    }
                });
                cancel.Dispose();
            }
        });
    }
    internal void CancelSignIn()
    {
        var cancel = _signIn; _signIn = null;
        if (cancel is not null)
        {
            try { cancel.Cancel(); }
            catch (ObjectDisposedException) { /* Completion already disposed this operation. */ }
        }
        Prompt = null; ConnectingAccount = false; CodeCopied = false; ClipboardError = null;
        Notify();
    }
    internal void CloseSettings() { CancelSignIn(); ShowAddForm = false; SelectedAccount = null; }
    internal bool TryGoBack()
    {
        if (!CanGoBack) return false;
        if (ShowAddForm && SigningIn) CancelSignIn();
        else { CloseSettings(); Navigate(SettingsPage.Accounts); }
        return true;
    }
    internal void SetError(string message) { Error = message; Notice = null; Notify(); }
    internal void ReportTrayError()
    {
        const string message = "Windows could not update the tray display. Refresh to retry.";
        if (Error != message) SetError(message);
    }
    internal void OpenLink(string uri, nint owner)
    {
        try { ShellServices.Open(uri, owner); }
        catch (Exception ex) { SetError(SafeMessage(ex)); }
    }
    internal void OpenSignInBrowser(nint owner)
    {
        if (Prompt is not { } prompt) return;
        try
        {
            ShellServices.Open(prompt.VerificationUri.AbsoluteUri, owner);
            Error = null; Notify();
        }
        catch (Exception ex)
        {
            Diagnostics.Record($"Account sign-in browser launch failed ({ex.GetType().Name}).");
            SetError("The browser could not be opened. Try Open browser again.");
        }
    }
    internal void CopyCode()
    {
        if (Prompt is not { } prompt) return;
        try
        {
            (CopyToClipboard ?? throw new InvalidOperationException("No clipboard owner is available."))(prompt.Code);
            CodeCopied = true; ClipboardError = null;
        }
        catch (Exception ex)
        {
            Diagnostics.Record($"Account sign-in code copy failed ({ex.GetType().Name}).");
            CodeCopied = false;
            ClipboardError = "The code could not be copied. Select Copy code to retry, or select and copy it yourself.";
        }
        Notify();
    }
    internal void SendTest()
    {
        bool accepted = TestNotification?.Invoke() ?? false;
        if (!accepted) SetError("Windows rejected the notification submission.");
        else { Notice = "Submitted to Windows. Do Not Disturb may suppress display."; Notify(); }
    }
    private static string SafeMessage(Exception ex)
    {
        Diagnostics.Record($"UI operation failed ({ex.GetType().Name}).");
        return ex is AppOperationException ? ex.Message :
            "The operation could not be completed. Check connectivity, permissions, and the diagnostic log.";
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel(); _countdown.Dispose();
        Controller.Changed -= OnDashboard;
    }
}
