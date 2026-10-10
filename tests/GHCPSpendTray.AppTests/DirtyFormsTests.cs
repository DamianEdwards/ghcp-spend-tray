using System.Collections.Concurrent;
using GHCPSpendTray.App.UI;
using GHCPSpendTray.Core;
using GHCPSpendTray.Shared;

internal static class DirtyFormsTests
{
    internal static async Task<int> RunAsync(string directory)
    {
        int assertions = 0, prompts = 0;
        var pending = new ConcurrentQueue<Action>();
        using var controller = new DraftController(directory);
        using var session = new AppSession(controller, pending.Enqueue);
        session.UnsavedChangesRequested += () => prompts++;
        session.Initialize();
        await Until(() => session.Initialized && !session.Busy);
        string personal = session.Dashboard.Accounts[0].Key, work = session.Dashboard.Accounts[1].Key;
        session.EditAccount(work);
        Check(session.HasEditableForm && !session.HasUnsavedChanges, "Opening an account starts with a clean baseline.");
        session.SetInput(SettingsField.DisplayName, "Renamed work");
        Check(session.HasUnsavedChanges && session.TryGoBack() && session.HasPendingNavigation &&
            session.SelectedAccount == work && prompts == 1, "Back protects dirty account settings.");
        session.ResolveUnsavedChanges(UnsavedChangesChoice.KeepEditing);
        Check(session.SelectedAccount == work && session.DisplayName == "Renamed work" && session.HasUnsavedChanges,
            "Keep editing preserves the exact draft and the current form.");
        session.SetCustomBudgetEnabled(true);
        session.SetInput(SettingsField.CustomBudget, "test");
        session.Navigate(SettingsPage.About);
        session.ResolveUnsavedChanges(UnsavedChangesChoice.Save);
        Check(session.Page == SettingsPage.Accounts && session.FieldError(SettingsField.CustomBudget) is not null &&
            session.HasUnsavedChanges && !session.HasPendingNavigation && controller.AccountSettings(work).DisplayName != "Renamed work",
            "Invalid Save stays in the form and does not continue the requested navigation.");
        session.SetInput(SettingsField.CustomBudget, "50");
        session.TryGoBack();
        session.ResolveUnsavedChanges(UnsavedChangesChoice.Save);
        await Until(() => !session.Busy);
        Check(session.SelectedAccount is null && session.Page == SettingsPage.Accounts &&
            controller.AccountSettings(work).DisplayName == "Renamed work" &&
            session.Dashboard.Accounts[1].CustomBudgetUsd == 50m,
            "Save commits the draft before continuing Back.");
        session.EditAccount(work);
        session.SetInput(SettingsField.CustomBudget, "75");
        session.EditAccount(personal);
        Check(session.HasPendingNavigation && session.SelectedAccount == work, "Account switches are guarded.");
        session.ResolveUnsavedChanges(UnsavedChangesChoice.Discard);
        Check(session.SelectedAccount == personal && !session.HasUnsavedChanges &&
            session.Dashboard.Accounts[1].CustomBudgetUsd == 50m, "Discard changes accounts without altering the saved budget.");
        session.EditAccount(work);
        session.SetInput(SettingsField.CustomBudget, "test");
        session.SaveAccount();
        session.CancelChanges();
        Check(session.CustomBudget == "50" && !session.HasUnsavedChanges && !session.HasFieldErrors && session.Error is null,
            "Cancel restores persisted account values and clears validation.");
        session.ShowPeriodEstimate = true;
        Check(session.HasUnsavedChanges, "Estimate toggles participate in dirty tracking.");
        session.ShowPeriodEstimate = false;
        Check(!session.HasUnsavedChanges, "Returning to the original value makes the form clean.");
        session.SetCustomBudgetEnabled(false);
        session.SetInput(SettingsField.CustomBudget, "unused");
        session.CancelChanges();
        Check(!session.HasUnsavedChanges && session.CustomBudget == "50", "Cancel also restores override modes.");

        session.SetInput(SettingsField.DisplayName, "Blocked save");
        controller.RejectSave = true;
        session.Navigate(SettingsPage.General);
        session.ResolveUnsavedChanges(UnsavedChangesChoice.Save);
        await Until(() => !session.Busy);
        Check(session.Page == SettingsPage.Accounts && session.SelectedAccount == work &&
            session.DisplayName == "Blocked save" && session.HasUnsavedChanges && session.Error == "Synthetic save failed.",
            "A failed save preserves the draft and never navigates away.");
        controller.RejectSave = false;
        controller.SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Navigate(SettingsPage.General);
        session.ResolveUnsavedChanges(UnsavedChangesChoice.Save);
        Check(session.Saving && session.Busy && session.TryGoBack() &&
            !session.TryCloseSettings(() => throw new InvalidOperationException("Must not close during Save.")),
            "Navigation and closing are held while a save is in flight.");
        controller.SaveGate.SetResult();
        await Until(() => !session.Busy);
        controller.SaveGate = null;
        Check(session.Page == SettingsPage.General && !session.HasUnsavedChanges,
            "A successful asynchronous save completes the original navigation once.");

        session.SetInput(SettingsField.PollMinutes, "20");
        session.Startup = true;
        session.RefreshStartup();
        Check(session.Startup && session.HasUnsavedChanges, "Window activation cannot overwrite a dirty startup draft.");
        session.TrayStyle = TrayIconStyle.Percentage;
        session.TrayMode = TrayDisplayMode.PerAccount;
        session.ExcludedTrayAccounts.Add(personal);
        session.Navigate(SettingsPage.Notifications);
        Check(session.HasPendingNavigation && session.Page == SettingsPage.General, "Global page changes are guarded.");
        session.ResolveUnsavedChanges(UnsavedChangesChoice.Save);
        await Until(() => !session.Busy);
        Check(session.Page == SettingsPage.Notifications && !session.HasUnsavedChanges &&
            controller.Settings.PollMinutes == 20 && controller.Settings.TrayStyle == TrayIconStyle.Percentage &&
            controller.Settings.ExcludedTrayAccounts!.Contains(personal),
            "Global Save includes refresh, tray mode, style and account selections before navigating.");
        session.Notifications = false;
        Check(session.HasUnsavedChanges, "Notification toggles participate in dirty tracking.");
        int closes = 0;
        Check(!session.TryCloseSettings(() => closes++) && closes == 0 && session.HasPendingNavigation,
            "Closing Settings protects unsaved global preferences.");
        session.ResolveUnsavedChanges(UnsavedChangesChoice.KeepEditing);
        Check(closes == 0 && !session.Notifications, "Keep editing cancels the close.");
        session.TryCloseSettings(() => closes++);
        session.ResolveUnsavedChanges(UnsavedChangesChoice.Discard);
        Check(closes == 1 && session.Notifications && !session.HasUnsavedChanges,
            "Discard restores preferences before allowing the close.");
        session.SetInput(SettingsField.Increment, "5");
        session.AddAccount();
        Check(session.HasPendingNavigation && !session.ShowAddForm, "Add account cannot discard a settings draft.");
        session.ResolveUnsavedChanges(UnsavedChangesChoice.KeepEditing);
        session.CancelChanges();
        session.EditAccount(work);
        session.SetInput(SettingsField.DisplayName, "Reconnect draft");
        session.Reconnect(session.Dashboard.Accounts[1]);
        Check(session.HasPendingNavigation && session.SelectedAccount == work && !session.ShowAddForm,
            "Reconnect is guarded before replacing account settings with sign-in.");
        session.ResolveUnsavedChanges(UnsavedChangesChoice.KeepEditing);
        session.CancelChanges();
        Check(!Directory.Exists(directory), "Dirty-form tests use isolated in-memory examples.");
        return assertions;

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
        async Task Until(Func<bool> condition)
        {
            for (int attempt = 0; attempt < 250; attempt++)
            {
                while (pending.TryDequeue(out var action)) action();
                if (condition()) return;
                await Task.Delay(20);
            }
            throw new TimeoutException("Dirty form state did not settle.");
        }
    }

    private sealed class DraftController(string directory) : IApplicationController
    {
        private readonly DemoController _demo = new(directory);
        internal bool RejectSave;
        internal TaskCompletionSource? SaveGate;
        public string DataDirectory => _demo.DataDirectory;
        public bool Portable => _demo.Portable;
        public SettingsView Settings => _demo.Settings;
        public event Action<DashboardView>? Changed { add => _demo.Changed += value; remove => _demo.Changed -= value; }
        public Task InitializeAsync() => _demo.InitializeAsync();
        public Task RefreshAsync(string? accountKey = null) => _demo.RefreshAsync(accountKey);
        public Task RefreshAccountAsync(string accountKey) => _demo.RefreshAccountAsync(accountKey);
        public async Task SaveSettingsAsync(SettingsView settings)
        {
            await BeforeSave();
            await _demo.SaveSettingsAsync(settings);
        }
        public async Task SaveAccountAsync(string key, string displayName, string thresholds, decimal? spendIncrementUsd = null,
            bool? showPeriodEstimate = null, decimal? customBudgetUsd = null, bool updateCustomBudget = false)
        {
            await BeforeSave();
            await _demo.SaveAccountAsync(key, displayName, thresholds, spendIncrementUsd, showPeriodEstimate, customBudgetUsd, updateCustomBudget);
        }
        private async Task BeforeSave()
        {
            if (SaveGate is { } gate) await gate.Task;
            if (RejectSave) throw new AppOperationException("Synthetic save failed.");
        }
        public (string DisplayName, string Thresholds, decimal? SpendIncrementUsd, bool ShowPeriodEstimate) AccountSettings(string key) =>
            _demo.AccountSettings(key);
        public Task RemoveAsync(string key) => _demo.RemoveAsync(key);
        public Task AddAsync(string host, bool offlineAccess, string? reconnectKey, Action<DevicePrompt> prompt,
            Action authorized, CancellationToken cancellationToken, string? clientId = null) =>
            _demo.AddAsync(host, offlineAccess, reconnectKey, prompt, authorized, cancellationToken, clientId);
        public string? AccountClientId(string key) => _demo.AccountClientId(key);
        public string ResolveHostDescription(string host) => _demo.ResolveHostDescription(host);
        public Task ResumeAsync() => _demo.ResumeAsync();
        public void SetNotificationHandler(Func<NotificationView, Task<bool>> handler) => _demo.SetNotificationHandler(handler);
        public void Dispose() => _demo.Dispose();
    }
}
