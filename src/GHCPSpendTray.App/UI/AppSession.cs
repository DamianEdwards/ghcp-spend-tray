using System.Globalization;
using GHCPSpendTray.App.Native;
using GHCPSpendTray.App.Platform;
using GHCPSpendTray.Core;

namespace GHCPSpendTray.App.UI;

internal enum SettingsPage { Usage, General, Accounts, Notifications, About }
internal enum SettingsField { PollMinutes, Thresholds, Increment, DisplayName, AccountThresholds, AccountIncrement, CustomBudget }
internal enum UnsavedChangesChoice { Save, Discard, KeepEditing }

internal sealed class AppSession : IDisposable
{
    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _signIn;
    private readonly CancellationTokenSource _lifetime = new();
    private string? _notice;
    private readonly System.Threading.Timer _countdown;
    private bool _disposed;
    private const string ValidationMessage = "Correct the highlighted fields before saving.";
    private readonly Dictionary<SettingsField, string> _fieldErrors = [];
    private GlobalDraft? _savedGlobal;
    private AccountDraft? _savedAccount;
    private Action? _pendingNavigation;
    internal IApplicationController Controller { get; }
    internal StoreUpdateSession? StoreUpdates { get; }
    internal DashboardView Dashboard { get; private set; } = new("Loading", "Loading accounts...", "GHCPSpendTray | Loading", []);
    internal SettingsPage Page { get; private set; } = SettingsPage.Usage;
    internal event Action? Changed;
    internal event Action? DashboardChanged;
    internal event Action? UnsavedChangesRequested;
    internal Action<SettingsPage>? OpenSettings { get; set; }
    internal Action? HideFlyout { get; set; }
    internal Func<bool>? TestNotification { get; set; }
    internal Action<string>? CopyToClipboard { get; set; }
    internal int Revision { get; private set; }
    internal bool Initialized { get; private set; }
    internal bool Busy { get; private set; }
    internal bool Saving { get; private set; }
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
    internal string PollMinutes { get; set; } = "10";
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
    internal bool ShowPeriodEstimate { get; set; }
    internal bool UseCustomBudget { get; set; }
    internal string CustomBudget { get; set; } = "";
    internal string? FieldError(SettingsField field) => _fieldErrors.GetValueOrDefault(field);
    internal bool HasFieldErrors => _fieldErrors.Count > 0;
    internal bool HasValidationError => HasFieldErrors && Error == ValidationMessage;
    internal bool HasEditableForm => Initialized && (Page is SettingsPage.General or SettingsPage.Notifications ||
        Page == SettingsPage.Accounts && !ShowAddForm && SelectedAccount is { } key &&
        Dashboard.Accounts.Any(account => account.Key == key));
    internal bool HasUnsavedChanges => HasEditableForm && (Page == SettingsPage.Accounts
        ? _savedAccount is not null && CurrentAccountDraft() != _savedAccount
        : HasGlobalChanges);
    internal bool HasPendingNavigation => _pendingNavigation is not null;
    private bool HasGlobalChanges => _savedGlobal is not null && CurrentGlobalDraft() != _savedGlobal;

    private GlobalDraft CurrentGlobalDraft() => new(PollMinutes, Thresholds, Increment, Notifications, Startup,
        TrayStyle, TrayMode, string.Join("\n", ExcludedTrayAccounts.Order(StringComparer.Ordinal)));
    private AccountDraft CurrentAccountDraft() => new(SelectedAccount, DisplayName, AccountThresholds,
        InheritIncrement, InheritIncrement ? "" : AccountIncrement, ShowPeriodEstimate,
        UseCustomBudget, UseCustomBudget ? CustomBudget : "");

    private sealed record GlobalDraft(string PollMinutes, string Thresholds, string Increment, bool Notifications,
        bool Startup, TrayIconStyle TrayStyle, TrayDisplayMode TrayMode, string ExcludedAccounts);
    private sealed record AccountDraft(string? Key, string DisplayName, string Thresholds, bool InheritIncrement,
        string Increment, bool ShowPeriodEstimate, bool UseCustomBudget, string Budget);

    private bool CanLeaveForm(Action continuation)
    {
        if (Saving || HasPendingNavigation) return false;
        if (!HasUnsavedChanges) return true;
        _pendingNavigation = continuation;
        Notify();
        UnsavedChangesRequested?.Invoke();
        return false;
    }

    internal bool TryCloseSettings(Action close) => CanLeaveForm(close);

    internal void ResolveUnsavedChanges(UnsavedChangesChoice choice)
    {
        if (_pendingNavigation is not { } continuation) return;
        _pendingNavigation = null;
        switch (choice)
        {
            case UnsavedChangesChoice.KeepEditing: Notify(); break;
            case UnsavedChangesChoice.Discard:
                CancelChanges();
                if (!HasUnsavedChanges) continuation();
                break;
            case UnsavedChangesChoice.Save:
                if (Busy) { SetError("Wait for the current operation to finish, then save your changes."); break; }
                if (Page == SettingsPage.Accounts) SaveAccount(continuation);
                else SaveGlobal(continuation);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(choice));
        }
    }

    internal void CancelChanges()
    {
        if (Saving) return;
        try
        {
            if (Page == SettingsPage.Accounts && SelectedAccount is { } key) ReloadAccount(key);
            else ReloadSettings();
            _fieldErrors.Clear(); Error = null; Notice = null;
            Notify();
        }
        catch (AppOperationException ex) { SetError(ex.Message); }
    }

    internal void RefreshStartup()
    {
        if (HasGlobalChanges) return;
        Startup = Controller.Settings.Startup;
        _savedGlobal = CurrentGlobalDraft();
        Notify();
    }

    internal void SetInput(SettingsField field, string value)
    {
        switch (field)
        {
            case SettingsField.PollMinutes: PollMinutes = value; break;
            case SettingsField.Thresholds: Thresholds = value; break;
            case SettingsField.Increment: Increment = value; break;
            case SettingsField.DisplayName: DisplayName = value; break;
            case SettingsField.AccountThresholds: AccountThresholds = value; break;
            case SettingsField.AccountIncrement: AccountIncrement = value; break;
            case SettingsField.CustomBudget: CustomBudget = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }
        if (_fieldErrors.ContainsKey(field)) ValidateField(field);
        if (_fieldErrors.Count == 0 && Error == ValidationMessage) Error = null;
        Notice = null;
        Notify();
    }

    internal void SetCustomBudgetEnabled(bool enabled)
    {
        UseCustomBudget = enabled;
        if (!enabled) ClearFieldError(SettingsField.CustomBudget);
        Notify();
    }

    internal void SetInheritIncrement(bool inherit)
    {
        InheritIncrement = inherit;
        if (inherit) ClearFieldError(SettingsField.AccountIncrement);
        Notify();
    }

    private void ClearFieldError(SettingsField field)
    {
        _fieldErrors.Remove(field);
        if (_fieldErrors.Count == 0 && Error == ValidationMessage) Error = null;
    }

    internal AppSession(IApplicationController controller, Action<Action> dispatch, IStoreUpdates? storeUpdates = null)
    {
        Controller = controller; _dispatch = dispatch;
        if (storeUpdates is not null)
        {
            StoreUpdates = new(storeUpdates, Post);
            StoreUpdates.Changed += Notify;
        }
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
        _fieldErrors.Clear();
        var settings = Controller.Settings;
        PollMinutes = settings.PollMinutes.ToString(CultureInfo.InvariantCulture);
        Thresholds = settings.Thresholds;
        Increment = settings.SpendIncrementUsd?.ToString(CultureInfo.InvariantCulture) ?? "";
        Notifications = settings.Notifications; Startup = settings.Startup;
        TrayStyle = settings.TrayStyle; TrayMode = settings.TrayMode;
        ExcludedTrayAccounts.Clear();
        ExcludedTrayAccounts.UnionWith(settings.ExcludedTrayAccounts ?? []);
        _savedGlobal = CurrentGlobalDraft();
    }
    internal void Navigate(SettingsPage page)
    {
        if (page != Page && !CanLeaveForm(() => Navigate(page))) return;
        _fieldErrors.Clear();
        if (SigningIn) CancelSignIn();
        Page = page; Error = null; Notice = null; ConfirmRemove = false;
        if (page == SettingsPage.About) StoreUpdates?.Check();
        Notify();
    }
    internal void AddAccount()
    {
        if (!CanLeaveForm(AddAccount)) return;
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
        if (!CanLeaveForm(() => EditAccount(key))) return;
        try
        {
            CancelSignIn();
            _fieldErrors.Clear();
            SelectedAccount = key; ShowAddForm = false; ConfirmRemove = false;
            ShowAdvancedDetails = false;
            ReloadAccount(key);
            Page = SettingsPage.Accounts; Error = null; Notice = null;
            OpenSettings?.Invoke(SettingsPage.Accounts); Notify();
        }
        catch (AppOperationException ex) { SetError(ex.Message); }
    }
    private void ReloadAccount(string key)
    {
        var account = Controller.AccountSettings(key);
        var view = Dashboard.Accounts.FirstOrDefault(a => a.Key == key)
            ?? throw new AppOperationException("That account is no longer configured.");
        DisplayName = account.DisplayName; AccountThresholds = account.Thresholds;
        InheritIncrement = account.SpendIncrementUsd is null;
        AccountIncrement = account.SpendIncrementUsd?.ToString(CultureInfo.InvariantCulture) ?? "";
        ShowPeriodEstimate = account.ShowPeriodEstimate;
        UseCustomBudget = view.CustomBudgetUsd is not null;
        CustomBudget = view.CustomBudgetUsd?.ToString(CultureInfo.InvariantCulture) ?? "";
        _savedAccount = CurrentAccountDraft();
    }
    internal void Reconnect(AccountView account)
    {
        if (!CanLeaveForm(() => Reconnect(account))) return;
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
    internal void Run(Func<Task> operation, Action? success = null, Action? failure = null, bool saving = false)
    {
        if (Busy) return;
        Busy = true; Saving = saving; Error = null; Notice = null; Notify();
        _ = Task.Run(async () =>
        {
            try
            {
                await operation().ConfigureAwait(false);
                Post(() => { Saving = false; success?.Invoke(); });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Post(() => { Saving = false; failure?.Invoke(); SetError(SafeMessage(ex)); }); }
            finally { Post(() => { Busy = false; Saving = false; Notify(); }); }
        });
    }
    internal void Refresh(string? key = null) => Run(() => Controller.RefreshAsync(key));
    internal void RefreshAccount(string key) => Run(() => Controller.RefreshAccountAsync(key));
    internal void SaveGlobal() => SaveGlobal(null);
    private void SaveGlobal(Action? continuation)
    {
        if (!ValidateFields(SettingsField.PollMinutes, SettingsField.Thresholds, SettingsField.Increment)) return;
        int minutes = int.Parse(PollMinutes, CultureInfo.InvariantCulture);
        decimal? increment = ParseAmount(Increment);
        var next = new SettingsView(minutes, Thresholds, Notifications, Startup, increment,
            TrayStyle: TrayStyle, TrayMode: TrayMode, ExcludedTrayAccounts: ExcludedTrayAccounts.ToArray());
        Run(() => Controller.SaveSettingsAsync(next),
            () => { ReloadSettings(); Notice = "Settings saved."; continuation?.Invoke(); }, saving: true);
    }
    internal void SaveAccount() => SaveAccount(null);
    private void SaveAccount(Action? continuation)
    {
        if (SelectedAccount is not { } key) return;
        if (!ValidateFields(SettingsField.DisplayName, SettingsField.AccountThresholds,
            SettingsField.AccountIncrement, SettingsField.CustomBudget)) return;
        decimal? amount = InheritIncrement ? null : ParseAmount(AccountIncrement) ?? 0;
        string name = DisplayName, thresholds = AccountThresholds;
        bool showPeriodEstimate = ShowPeriodEstimate;
        decimal? budget = UseCustomBudget ? ParseAmount(CustomBudget) : null;
        var saved = CurrentAccountDraft();
        Run(() => Controller.SaveAccountAsync(key, name, thresholds, amount, showPeriodEstimate, budget, updateCustomBudget: true),
            () => { _savedAccount = saved; Notice = "Account settings saved."; continuation?.Invoke(); }, saving: true);
    }
    private static decimal? ParseAmount(string text) => string.IsNullOrWhiteSpace(text) ? null :
        decimal.Parse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    private bool ValidateFields(params SettingsField[] fields)
    {
        _fieldErrors.Clear();
        foreach (var field in fields) ValidateField(field);
        if (_fieldErrors.Count == 0) return true;
        SetError(ValidationMessage);
        return false;
    }

    private void ValidateField(SettingsField field)
    {
        string? error = field switch
        {
            SettingsField.PollMinutes => int.TryParse(PollMinutes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) &&
                minutes is >= 5 and <= 1440
                ? null : "Enter a refresh interval from 5 through 1440 minutes.",
            SettingsField.Thresholds => ThresholdError(Thresholds, inherit: false),
            SettingsField.AccountThresholds => ThresholdError(AccountThresholds, inherit: true),
            SettingsField.DisplayName => DisplayName.Trim() is { } name && (name.Length > 128 || name.Any(char.IsControl))
                ? "Use at most 128 characters with no control characters." : null,
            SettingsField.Increment => AmountError(Increment),
            SettingsField.AccountIncrement => InheritIncrement ? null : AmountError(AccountIncrement),
            SettingsField.CustomBudget => UseCustomBudget ? AmountError(CustomBudget, budget: true) : null,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        if (error is null) _fieldErrors.Remove(field);
        else _fieldErrors[field] = error;
    }

    private static string? AmountError(string text, bool budget = false)
    {
        if (!budget && string.IsNullOrWhiteSpace(text)) return null;
        if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) &&
            (budget ? value > 0 : value >= 0) && decimal.Round(value, 2) == value) return null;
        return budget
            ? "Enter a budget greater than $0 with at most two decimal places, such as 500 or 12.50."
            : "Enter a USD increment with at most two decimal places, such as 50 or 12.50. Use 0 to disable.";
    }

    private static string? ThresholdError(string text, bool inherit)
    {
        if (inherit && string.IsNullOrWhiteSpace(text)) return null;
        try { _ = GHCPSpendTray.Shared.ApplicationController.ParseThresholds(text); return null; }
        catch (AppOperationException ex) { return ex.Message; }
        catch (ArgumentException ex) { return ex.Message; }
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
    internal void CloseSettings()
    {
        CancelSignIn(); ShowAddForm = false; SelectedAccount = null;
        _fieldErrors.Clear(); _savedAccount = null; _pendingNavigation = null;
    }
    internal bool TryGoBack()
    {
        if (!CanGoBack) return false;
        if (!CanLeaveForm(() => TryGoBack())) return true;
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
        StoreUpdates?.Dispose();
        _lifetime.Cancel(); _countdown.Dispose();
        Controller.Changed -= OnDashboard;
    }
}
