using System.Collections.Concurrent;
using GHCPSpendTray.App;
using GHCPSpendTray.App.UI;

internal static class BackNavigationTests
{
    internal static async Task<int> RunAsync(string directory)
    {
        int assertions = 0;
        var pending = new ConcurrentQueue<Action>();
        using var controller = new BackController(directory);
        using var session = new AppSession(controller, pending.Enqueue);
        int opens = 0, hides = 0;
        session.OpenSettings = _ => opens++;
        session.HideFlyout = () => hides++;
        int copied = 0;
        session.CopyToClipboard = _ => copied++;
        session.Initialize();
        await Until(() => session.Initialized && !session.Busy);

        foreach (var page in Enum.GetValues<SettingsPage>())
        {
            session.Navigate(page);
            int revision = session.Revision;
            Check(!session.TryGoBack() && session.Revision == revision && session.Page == page,
                $"no history or consumed Back on top-level {page}");
        }

        var account = session.Dashboard.Accounts[0];
        session.EditAccount(account.Key);
        session.ConfirmRemove = true;
        int presentations = opens;
        Check(session.TryGoBack() && session.SelectedAccount is null && !session.ConfirmRemove &&
            session.Page == SettingsPage.Accounts, "details Back returns to accounts and clears removal confirmation");
        Check(opens == presentations && hides == 0, "Back neither opens nor hides a window");
        Check(!session.TryGoBack(), "Back on the account list is not handled");

        session.EditAccount(account.Key);
        session.Navigate(SettingsPage.General);
        int generalRevision = session.Revision;
        Check(!session.TryGoBack() && session.SelectedAccount == account.Key &&
            session.Revision == generalRevision, "hidden account details are not a Back target");
        session.Navigate(SettingsPage.Accounts);
        Check(session.TryGoBack(), "returning to visible account details restores the Back target");
        session.EditAccount("github.com:missing");
        Check(!session.TryGoBack(), "missing account details have no visible Back target");

        session.AddAccount();
        session.CancelSignIn();
        session.Navigate(SettingsPage.About);
        Check(!session.TryGoBack() && session.ShowAddForm, "hidden add form is not a Back target");
        session.Navigate(SettingsPage.Accounts);
        session.SetError("Synthetic validation error.");
        Check(session.TryGoBack() && !session.ShowAddForm && session.Error is null,
            "idle add form returns to accounts with the visible button's cleanup");

        session.EditAccount(account.Key);
        session.Reconnect(account);
        session.CancelSignIn();
        Check(session.TryGoBack() && !session.ShowAddForm && session.SelectedAccount is null,
            "idle reconnect returns to accounts rather than inventing a details history");

        session.EditAccount(account.Key);
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Run(() => work.Task);
        Check(session.Busy && session.TryGoBack(), "busy details preserve the enabled visible Back action");
        work.SetResult();
        await Until(() => !session.Busy);

        foreach (var stage in new[] { "requesting", "device prompt", "finishing connection" })
        {
            var attempt = new SignInAttempt();
            controller.Attempt = attempt;
            if (stage == "device prompt") session.Reconnect(account);
            else session.AddAccount();
            await attempt.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (stage != "requesting")
            {
                attempt.ShowPrompt.SetResult();
                await Until(() => session.Prompt is not null);
            }
            if (stage == "finishing connection")
            {
                attempt.Authorize.SetResult();
                await Until(() => session.ConnectingAccount);
            }
            Check(session.TryGoBack() && !session.SigningIn && session.ShowAddForm &&
                session.Prompt is null && !session.ConnectingAccount && attempt.Token.IsCancellationRequested,
                $"Back cancels {stage} and leaves the add/reconnect form visible");
            await attempt.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Drain();
            Check(!attempt.Saved && session.ShowAddForm && session.Error is null,
                $"canceled {stage} cannot connect an account or navigate late");
            Check(session.TryGoBack() && !session.ShowAddForm && !session.TryGoBack(),
                $"next Back after {stage} returns to accounts, then becomes unhandled");
        }

        // Complete an old cancellation only after a new attempt has started.
        var old = new SignInAttempt();
        controller.Attempt = old;
        session.AddAccount();
        await old.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(session.TryGoBack(), "old sign-in canceled");
        await old.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var current = new SignInAttempt();
        controller.Attempt = current;
        session.StartSignIn();
        await current.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        current.ShowPrompt.SetResult();
        await Until(() => session.Prompt is not null);
        int currentCopies = copied;
        old.PromptCallback!(new("STALE-CODE", new Uri("https://github.com/login/device"), DateTimeOffset.UtcNow.AddMinutes(1)));
        old.AuthorizedCallback!();
        Drain();
        Check(session.SigningIn && !current.Token.IsCancellationRequested,
            "queued completion from canceled sign-in does not cancel the new attempt");
        Check(session.Prompt?.Code == "TEST-CODE" && !session.ConnectingAccount && copied == currentCopies,
            "late cancelled prompts and authorization callbacks cannot replace the new code or clipboard");
        session.CloseSettings();
        await current.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Drain();
        Check(!session.TryGoBack() && !session.SigningIn && !session.ShowAddForm,
            "closing settings retains cancellation and clears the Back target");
        session.AddAccount();
        session.Dispose();
        Check(!session.TryGoBack(), "disposed session does not consume Back");
        return assertions;

        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + description);
            assertions++;
        }
        void Drain()
        {
            while (pending.TryDequeue(out var action)) action();
        }
        async Task Until(Func<bool> condition)
        {
            for (int i = 0; i < 250; i++)
            {
                Drain();
                if (condition()) return;
                await Task.Delay(20);
            }
            throw new TimeoutException("Back navigation state did not settle.");
        }
    }

    private sealed class SignInAttempt
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ShowPrompt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Authorize { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Save { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token { get; private set; }
        internal bool Saved { get; private set; }
        internal Action<DevicePrompt>? PromptCallback { get; private set; }
        internal Action? AuthorizedCallback { get; private set; }

        internal async Task RunAsync(Action<DevicePrompt> prompt, Action authorized,
            CancellationToken token)
        {
            Token = token;
            PromptCallback = prompt;
            AuthorizedCallback = authorized;
            Started.SetResult();
            try
            {
                await ShowPrompt.Task.WaitAsync(token);
                prompt(new("TEST-CODE", new Uri("https://github.com/login/device"), DateTimeOffset.UtcNow.AddMinutes(5)));
                await Authorize.Task.WaitAsync(token);
                authorized();
                await Save.Task.WaitAsync(token);
                Saved = true;
            }
            finally { Finished.SetResult(); }
        }
    }

    private sealed class BackController(string directory) : IApplicationController
    {
        private readonly DemoController _demo = new(directory);
        internal SignInAttempt? Attempt { get; set; }
        public string DataDirectory => _demo.DataDirectory;
        public bool Portable => _demo.Portable;
        public SettingsView Settings => _demo.Settings;
        public event Action<DashboardView>? Changed { add => _demo.Changed += value; remove => _demo.Changed -= value; }
        public Task InitializeAsync() => _demo.InitializeAsync();
        public Task RefreshAsync(string? accountKey = null) => _demo.RefreshAsync(accountKey);
        public Task RefreshAccountAsync(string accountKey) => _demo.RefreshAccountAsync(accountKey);
        public Task SaveSettingsAsync(SettingsView settings) => _demo.SaveSettingsAsync(settings);
        public Task SaveAccountAsync(string key, string displayName, string thresholds, decimal? spendIncrementUsd = null,
            bool? showPeriodEstimate = null, decimal? customBudgetUsd = null, bool updateCustomBudget = false) =>
            _demo.SaveAccountAsync(key, displayName, thresholds, spendIncrementUsd, showPeriodEstimate, customBudgetUsd, updateCustomBudget);
        public (string DisplayName, string Thresholds, decimal? SpendIncrementUsd, bool ShowPeriodEstimate) AccountSettings(string key) =>
            _demo.AccountSettings(key);
        public Task RemoveAsync(string key) => _demo.RemoveAsync(key);
        public Task AddAsync(string host, bool offlineAccess, string? reconnectKey, Action<DevicePrompt> prompt,
            Action authorized, CancellationToken cancellationToken, string? clientId = null) =>
            Attempt?.RunAsync(prompt, authorized, cancellationToken) ?? Task.Delay(Timeout.Infinite, cancellationToken);
        public string? AccountClientId(string key) => _demo.AccountClientId(key);
        public string ResolveHostDescription(string host) => _demo.ResolveHostDescription(host);
        public Task ResumeAsync() => _demo.ResumeAsync();
        public void SetNotificationHandler(Func<NotificationView, Task<bool>> handler) => _demo.SetNotificationHandler(handler);
        public void Dispose() => _demo.Dispose();
    }
}
