using System.Globalization;
using GHSpend.App.Native;

namespace GHSpend.App.UI;

internal enum SettingsPage { General, Accounts, Notifications, About }

internal sealed class AppSession : IDisposable
{
    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _signIn;
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource<bool>? _confirmation;
    private readonly System.Threading.Timer _countdown;
    private bool _disposed;
    internal IApplicationController Controller { get; }
    internal DashboardView Dashboard { get; private set; } = new("Loading", "Loading accounts...", "GHSpend | Loading", []);
    internal SettingsPage Page { get; private set; } = SettingsPage.General;
    internal event Action? Changed;
    internal Action<SettingsPage>? OpenSettings { get; set; }
    internal Action? HideFlyout { get; set; }
    internal Func<bool>? TestNotification { get; set; }
    internal int Revision { get; private set; }
    internal bool Initialized { get; private set; }
    internal bool Busy { get; private set; }
    internal bool SigningIn => _signIn is not null;
    internal string? Error { get; private set; }
    internal string? Notice { get; private set; }
    internal string? SelectedAccount { get; private set; }
    internal bool ShowAddForm { get; private set; }
    internal bool ConfirmRemove { get; set; }
    internal bool ShowAdvancedDetails { get; set; }
    internal bool ShowAccountHistory { get; set; }
    internal DevicePrompt? Prompt { get; private set; }
    internal PendingIdentity? Identity { get; private set; }
    internal string Host { get; set; } = "github.com";
    internal bool OfflineAccess { get; set; }
    internal string? ReconnectKey { get; private set; }
    internal string PollMinutes { get; set; } = "60";
    internal string Thresholds { get; set; } = "50, 80, 100";
    internal string Increment { get; set; } = "";
    internal bool Notifications { get; set; } = true;
    internal bool Startup { get; set; }
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
    private void OnDashboard(DashboardView view) => Post(() => { Dashboard = view; Notify(); });
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
        ReconnectKey = null; Host = "github.com"; Prompt = null; Identity = null;
        Error = null; Notice = null;
        OpenSettings?.Invoke(SettingsPage.Accounts);
        Notify();
    }
    internal void EditAccount(string key)
    {
        try
        {
            CancelSignIn();
            var account = Controller.AccountSettings(key);
            SelectedAccount = key; ShowAddForm = false; ConfirmRemove = false;
            ShowAdvancedDetails = false; ShowAccountHistory = false;
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
        Host = account.Host; ReconnectKey = account.Key; SelectedAccount = null;
        ShowAddForm = true; Prompt = null; Identity = null; Error = null; Notify();
    }
    internal void Run(Func<Task> operation, Action? success = null)
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
            catch (Exception ex) { Post(() => SetError(SafeMessage(ex))); }
            finally { Post(() => { Busy = false; Notify(); }); }
        });
    }
    internal void Refresh(string? key = null) => Run(() => Controller.RefreshAsync(key));
    internal void SaveGlobal()
    {
        if (!int.TryParse(PollMinutes, out var minutes) || minutes is < 5 or > 1440)
        { SetError("Enter a polling interval from 5 through 1440 minutes."); return; }
        if (!TryAmount(Increment, out var increment)) return;
        var next = new SettingsView(minutes, Thresholds, Notifications, Startup, increment);
        Run(() => Controller.SaveSettingsAsync(next), () => { ReloadSettings(); Notice = "Settings saved."; });
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
            SelectedAccount = null; ConfirmRemove = false; Notice = "Account removed from this device.";
        });
    }
    internal string HostDescription()
    {
        try { return Controller.ResolveHostDescription(Host); }
        catch (AppOperationException ex) { return ex.Message; }
    }
    internal void StartSignIn()
    {
        if (SigningIn || Busy) return;
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _signIn = cancel; Prompt = null; Identity = null; Error = null; Notice = null;
        string host = Host; bool offline = OfflineAccess; string? reconnect = ReconnectKey;
        Notify();
        _ = Task.Run(async () =>
        {
            try
            {
                await Controller.AddAsync(host, offline, reconnect,
                    prompt => Post(() => { if (_signIn == cancel) { Prompt = prompt; Notify(); } }),
                    identity =>
                    {
                        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        Post(() =>
                        {
                            if (_signIn != cancel) { completion.TrySetCanceled(); return; }
                            _confirmation = completion; Identity = identity; Prompt = null; Notify();
                        });
                        return completion.Task.WaitAsync(cancel.Token);
                    }, cancel.Token).ConfigureAwait(false);
                Post(() =>
                {
                    if (_signIn != cancel) return;
                    ShowAddForm = false; SelectedAccount = null; Notice = "Account connected.";
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Post(() => { if (_signIn == cancel) SetError(SafeMessage(ex)); }); }
            finally
            {
                Post(() =>
                {
                    if (_signIn == cancel) { _signIn = null; Prompt = null; Identity = null; _confirmation = null; Notify(); }
                });
                cancel.Dispose();
            }
        });
    }
    internal void ConfirmIdentity(bool accepted) => _confirmation?.TrySetResult(accepted);
    internal void CancelSignIn()
    {
        var cancel = _signIn; _signIn = null;
        if (cancel is not null)
        {
            try { cancel.Cancel(); }
            catch (ObjectDisposedException) { /* Completion already disposed this operation. */ }
        }
        _confirmation?.TrySetResult(false); _confirmation = null;
        Prompt = null; Identity = null;
        Notify();
    }
    internal void CloseSettings() { CancelSignIn(); ShowAddForm = false; SelectedAccount = null; }
    internal void SetError(string message) { Error = message; Notice = null; Notify(); }
    internal void OpenLink(string uri, nint owner)
    {
        try { ShellServices.Open(uri, owner); }
        catch (Exception ex) { SetError(SafeMessage(ex)); }
    }
    internal void CopyCode(nint owner)
    {
        if (Prompt is not { } prompt) return;
        try { ShellServices.CopyText(owner, prompt.Code); Notice = "Code copied."; Notify(); }
        catch (Exception) { SetError("The clipboard is busy. Select the code and copy it, or try again."); }
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
        _confirmation?.TrySetCanceled();
    }
}
