using System.Collections.Concurrent;
using GHCPSpendTray.App.UI;
using GHCPSpendTray.Shared;

internal static class FormValidationTests
{
    internal static async Task<int> RunAsync(string directory)
    {
        int assertions = 0;
        using var controller = new DemoController(directory);
        var pending = new ConcurrentQueue<Action>();
        using var session = new AppSession(controller, pending.Enqueue);
        session.Initialize();
        await Until(() => session.Initialized && !session.Busy);
        session.EditAccount(session.Dashboard.Accounts[1].Key);
        session.SetCustomBudgetEnabled(true);
        session.SetInheritIncrement(false);
        session.SetInput(SettingsField.CustomBudget, "test");
        session.SetInput(SettingsField.AccountIncrement, "test");
        session.SetInput(SettingsField.AccountThresholds, "50, test");
        session.SetInput(SettingsField.DisplayName, new string('x', 129));
        session.SaveAccount();
        foreach (var field in new[] { SettingsField.CustomBudget, SettingsField.AccountIncrement,
            SettingsField.AccountThresholds, SettingsField.DisplayName })
            Check(session.FieldError(field) is not null, "Save marks every invalid account input, not just the first.");
        Check(!session.Busy && session.Dashboard.Accounts[1].CustomBudgetUsd is null &&
            UI.ValidationFeedback(session) is not null && UI.Feedback(session) is null,
            "Rejected saves preserve data and show feedback beside Save, not only at the top of the page.");
        session.SetInput(SettingsField.CustomBudget, "50");
        Check(session.FieldError(SettingsField.CustomBudget) is null &&
            session.FieldError(SettingsField.AccountIncrement) is not null,
            "Correcting one field clears only that field's error.");
        session.SetInput(SettingsField.AccountThresholds, "100, 50, 80, 50");
        Check(session.FieldError(SettingsField.AccountThresholds) is null,
            "Field validation preserves the controller's threshold normalization rules.");
        session.SetInput(SettingsField.DisplayName, "Work (demo)");
        session.SetInheritIncrement(true);
        Check(!session.HasFieldErrors && session.Error is null && UI.ValidationFeedback(session) is null,
            "Corrections and disabling overrides clear all validation feedback.");
        session.SaveAccount();
        await Until(() => !session.Busy);
        Check(session.Error is null && session.Dashboard.Accounts[1].CustomBudgetUsd == 50m,
            "Corrected inputs can be saved normally.");
        session.SetInput(SettingsField.CustomBudget, "test");
        session.SaveAccount();
        session.SetCustomBudgetEnabled(false);
        Check(session.FieldError(SettingsField.CustomBudget) is null && session.Error is null,
            "Choosing API allocation clears the inactive budget field's error.");
        session.SetCustomBudgetEnabled(true);
        session.SaveAccount();
        session.EditAccount(session.Dashboard.Accounts[0].Key);
        session.ResolveUnsavedChanges(UnsavedChangesChoice.Discard);
        Check(!session.HasFieldErrors && session.Error is null,
            "Field errors cannot leak into another account.");

        session.Navigate(SettingsPage.General);
        session.SetInput(SettingsField.PollMinutes, "test");
        session.SetInput(SettingsField.Thresholds, "0, 50");
        session.SetInput(SettingsField.Increment, "-1");
        session.SaveGlobal();
        Check(session.FieldError(SettingsField.PollMinutes) is not null &&
            session.FieldError(SettingsField.Thresholds) is not null &&
            session.FieldError(SettingsField.Increment) is not null && !session.Busy,
            "Global Save identifies interval, threshold and increment errors together.");
        session.SetInput(SettingsField.PollMinutes, "10");
        session.SetInput(SettingsField.Thresholds, "50, 80, 100");
        session.SetInput(SettingsField.Increment, "");
        Check(!session.HasFieldErrors && session.Error is null, "Global input correction clears feedback immediately.");
        session.SaveGlobal();
        await Until(() => !session.Busy);
        Check(session.Error is null && controller.Settings.PollMinutes == 10,
            "Valid global inputs still save.");
        session.SetInput(SettingsField.PollMinutes, "4");
        session.SaveGlobal();
        session.Navigate(SettingsPage.Notifications);
        session.ResolveUnsavedChanges(UnsavedChangesChoice.Discard);
        Check(!session.HasFieldErrors && session.Error is null,
            "Navigation clears transient field errors.");
        Check(!Directory.Exists(directory), "Validation examples never modify real settings.");
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
            throw new TimeoutException("Form validation session did not settle.");
        }
    }
}
