using System.Collections.Concurrent;
using System.Net;
using GHCPSpendTray.App;
using GHCPSpendTray.App.UI;
using GHCPSpendTray.Core;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml.Controls;

internal static class AccountSignInTests
{
    internal static async Task<int> RunAsync(string root)
    {
        var fixture = new FixtureHttp();
        var handler = new SignInHttp(fixture);
        using var http = new HttpClient(handler);
        var credentials = new GatedCredentials();
        using var app = new ApplicationController(root, true, http, credentials);
        app.SetNotificationHandler((_, _, _) => Task.FromResult(true));
        Diagnostics.Initialize(root);
        var dispatch = new ConcurrentQueue<Action>();
        using var session = new AppSession(app, dispatch.Enqueue);
        var store = new JsonStore(root);
        var copies = new List<string>();
        session.CopyToClipboard = copies.Add;
        int assertions = 0;
        session.Initialize();
        await Until(() => session.Initialized && !session.Busy);

        session.AddAccount();
        Check(session.SigningIn && session.ShowAddForm && !session.EditingHost && session.Host == "github.com",
            "Add account starts github.com authorization immediately without an extra click");
        await Until(() => session.Prompt is not null);
        Check(copies.SequenceEqual(["TEST-CODE"]) && session.CodeCopied && session.ClipboardError is null,
            "the generated code is automatically copied once with successful feedback");
        var panel = UI.DeviceSignIn(session, session.Prompt!, 0);
        Check(Descendants(panel).OfType<TextBlockElement>().Any(text => text.Content == "TEST-CODE") &&
            Descendants(panel).OfType<InfoBarElement>().Any(bar =>
                bar.Message == "Code copied to clipboard." && bar.Severity == InfoBarSeverity.Success),
            "device code and accurate clipboard feedback are displayed together");
        var actions = Descendants(panel).OfType<FlexElement>().Single();
        Check(actions.Children.OfType<ButtonElement>().Select(button => button.Label)
            .SequenceEqual(["Open browser", "Copy code"]), "browser is the leading action beside manual Copy");
        Check(credentials.Writes == 0 && (await store.LoadSettingsAsync()).Value.Accounts.Length == 0,
            "code generation alone cannot create an account");
        Check(!session.Prompt!.ToString().Contains("TEST-CODE"), "device prompt string representation redacts the code");
        handler.TokenGate.TrySetResult();
        await Until(() => credentials.Writes == 1 && session.ConnectingAccount);
        Check(session.Prompt is null && !session.CodeCopied && session.ClipboardError is null &&
            session.Notice is null && !session.NoticeIsSuccess, "authorized flow automatically saves without confirmation");
        Check((await store.LoadSettingsAsync()).Value.Accounts.Length == 0, "success waits for durable persistence");
        credentials.Release.TrySetResult();
        await Until(() => !session.SigningIn);
        Check(credentials.Values.ContainsKey("github.com:1") &&
            (await store.LoadSettingsAsync()).Value.Accounts.Single().Key == "github.com:1" &&
            !session.ShowAddForm && session.NoticeIsSuccess && session.Notice == "Account connected.",
            "browser authorization completes account registration with no second decision");
        Check(UI.Feedback(session) is InfoBarElement { Title: "Complete!", Severity: InfoBarSeverity.Success },
            "successful automatic registration retains completion feedback");
        Check(!Directory.Exists(Path.Combine(root, "pending-avatars")) &&
            !(await File.ReadAllTextAsync(Path.Combine(root, "config.json"))).Contains("synthetic-avatar-token"),
            "confirmation-only avatar staging is gone and signed URLs remain unpersisted");

        handler.Reset();
        fixture.NextIdentity = "2";
        session.CopyToClipboard = _ => throw new InvalidOperationException("secret-clipboard-message");
        session.AddAccount();
        await Until(() => session.Prompt is not null);
        Check(!session.CodeCopied && session.ClipboardError is not null && session.Error is null && session.SigningIn,
            "clipboard contention does not stop login or falsely claim the code was copied");
        Check(Descendants(UI.DeviceSignIn(session, session.Prompt!, 0)).OfType<InfoBarElement>()
            .Single().Severity == InfoBarSeverity.Warning, "clipboard failure has a visible retry hint");
        session.CopyToClipboard = copies.Add;
        session.CopyCode();
        Check(session.CodeCopied && session.ClipboardError is null && copies.Count == 2,
            "manual copy retry clears the warning only after success");
        session.ChangeHost();
        Check(!session.SigningIn && session.EditingHost && session.Prompt is null && !session.CodeCopied,
            "Change host cancels authorization and clears the old code and copy feedback");
        session.SelectHost(true);
        session.SetHost("team.ghe.com");
        session.SetClientId("team-registration");
        fixture.NextIdentity = "5";
        handler.Reset();
        session.StartSignIn();
        await Until(() => session.Prompt is not null);
        Check(session.Prompt!.VerificationUri.Host == "team.ghe.com" &&
            fixture.OAuthClientIds.Last() == "team-registration" && session.CodeCopied,
            "custom-host sign-in uses its own registration and automatically copies its new code");
        handler.TokenGate.TrySetResult();
        await Until(() => !session.SigningIn);
        Check(session.NoticeIsSuccess && (await store.LoadSettingsAsync()).Value.Accounts.Length == 2,
            "custom-host authorization also adds the account automatically");

        handler.Reset();
        fixture.NextIdentity = "1";
        var account = session.Dashboard.Accounts.Single(a => a.Key == "github.com:1");
        session.Reconnect(account);
        Check(session.SigningIn && !session.EditingHost && session.ReconnectKey == account.Key,
            "Reconnect immediately starts the original host flow");
        await Until(() => session.Prompt is not null);
        handler.TokenGate.TrySetResult();
        await Until(() => !session.SigningIn);
        Check(session.Notice == "Account reconnected." && session.NoticeIsSuccess &&
            (await store.LoadSettingsAsync()).Value.Accounts.Length == 2, "reconnect completes without duplicating an account");

        handler.Reset();
        fixture.NextIdentity = "2";
        session.Reconnect(account);
        await Until(() => session.Prompt is not null);
        handler.TokenGate.TrySetResult();
        await Until(() => !session.SigningIn);
        Check(session.Error?.Contains("different account") == true &&
            credentials.Values["github.com:1"].AccessToken == "fixture-1" &&
            !session.NoticeIsSuccess, "wrong-identity reconnect still protects the original credentials");

        handler.Reset();
        fixture.NextIdentity = "1";
        session.AddAccount();
        await Until(() => session.Prompt is not null);
        handler.TokenGate.TrySetResult();
        await Until(() => !session.SigningIn);
        Check(session.Error?.Contains("already monitored") == true &&
            (await store.LoadSettingsAsync()).Value.Accounts.Length == 2, "duplicate sign-in fails without overwriting accounts");

        foreach (string failure in new[] { "code", "poll", "denied", "expired", "timeout", "identity", "usage" })
        {
            handler.Reset();
            handler.Failure = failure;
            fixture.NextIdentity = "2";
            session.AddAccount();
            handler.TokenGate.TrySetResult();
            await Until(() => !session.SigningIn);
            Check(session.Error is not null && session.ShowAddForm && !session.NoticeIsSuccess &&
                session.Prompt is null && !credentials.Values.ContainsKey("github.com:2"),
                $"{failure} failure is retryable without false completion");
        }
        handler.Failure = null;
        handler.Reset();
        credentials.FailWrite = true;
        session.StartSignIn();
        handler.TokenGate.TrySetResult();
        await Until(() => !session.SigningIn);
        Check(session.Error is not null && !session.NoticeIsSuccess &&
            !credentials.Values.ContainsKey("github.com:2"), "credential failure never reports completion");
        credentials.FailWrite = false;
        using (File.Open(Path.Combine(root, "config.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            handler.Reset();
            session.StartSignIn();
            handler.TokenGate.TrySetResult();
            await Until(() => !session.SigningIn);
            Check(session.Error is not null && !credentials.Values.ContainsKey("github.com:2"),
                "settings failure rolls back newly written credentials");
        }
        handler.Reset();
        session.StartSignIn();
        handler.TokenGate.TrySetResult();
        await Until(() => !session.SigningIn);
        Check(session.NoticeIsSuccess && (await store.LoadSettingsAsync()).Value.Accounts.Length == 3,
            "retry succeeds without another confirmation step");

        fixture.NextIdentity = "4";
        handler.Reset();
        credentials.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int writes = credentials.Writes;
        session.AddAccount();
        handler.TokenGate.TrySetResult();
        await Until(() => credentials.Writes > writes);
        session.CancelSignIn();
        await Until(() => credentials.CancelledWrites == 1);
        Check(!session.SigningIn && !session.ConnectingAccount && !session.NoticeIsSuccess &&
            !credentials.Values.ContainsKey("github.com:4"), "cancelling pending persistence does not add an account");
        credentials.Release.TrySetResult();

        handler.Reset();
        session.AddAccount();
        await Until(() => session.Prompt is not null);
        int oldCopies = copies.Count;
        session.ChangeHost();
        session.SelectHost(true);
        session.SetHost("unregistered.ghe.com");
        session.StartSignIn();
        Check(session.Error is not null && !session.SigningIn && session.EditingHost &&
            copies.Count == oldCopies, "invalid host registration is logged and leaves an editable retry form");
        session.AddAccount();
        await Until(() => session.Prompt is not null);
        Check(session.Host == "github.com" && !session.CustomHost,
            "a new Add account resets to github.com rather than reusing the previous custom host");
        session.CloseSettings();
        Check(!session.SigningIn && !session.ShowAddForm && session.Prompt is null &&
            !session.CodeCopied && session.ClipboardError is null, "closing settings clears all transient sign-in state");

        string logs = await File.ReadAllTextAsync(Path.Combine(root, "logs", "diagnostics.log"));
        foreach (string stage in new[] { "device code request", "browser authorization", "identity verification",
            "consumption access", "credential save", "settings save", "host validation" })
            Check(logs.Contains(stage), $"diagnostics identify the failing {stage} phase");
        Check(logs.Contains("code copy failed") && logs.Contains("cancelled"),
            "clipboard failures and interrupted login attempts are logged");
        Check(logs.Contains("Account sign-in failed during settings save") &&
            !logs.Contains("Account sign-in failed during credential rollback"),
            "successful rollback does not mislabel the original settings-save failure");
        foreach (string secret in new[] { "TEST-CODE", "fixture-", "synthetic-device-", "synthetic-avatar-token", "secret-" })
            Check(!logs.Contains(secret), "login diagnostics never contain codes, tokens, signed URLs or exception messages");
        return assertions;

        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + description);
            assertions++;
        }
        async Task Until(Func<bool> predicate)
        {
            for (int i = 0; i < 500; i++)
            {
                while (dispatch.TryDequeue(out var action)) action();
                if (predicate()) return;
                await Task.Delay(20);
            }
            throw new TimeoutException("Account sign-in state did not arrive.");
        }
    }

    private static IEnumerable<Element> Descendants(Element element)
    {
        yield return element;
        Element[] children = element switch
        {
            StackElement stack => stack.Children,
            GridElement grid => grid.Children,
            FlexElement flex => flex.Children,
            BorderElement border => border.Child is { } child ? [child] : [],
            _ => []
        };
        foreach (var child in children)
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private sealed class SignInHttp(FixtureHttp inner) : DelegatingHandler(inner)
    {
        internal TaskCompletionSource TokenGate { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? Failure { get; set; }
        internal void Reset() => TokenGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/login/oauth/access_token") await TokenGate.Task.WaitAsync(cancellationToken);
            if (Failure == "timeout" && path == "/login/oauth/access_token")
                throw new TaskCanceledException("secret-timeout-message");
            if (Failure is "denied" or "expired" && path == "/login/oauth/access_token")
                return new(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(Failure == "denied"
                        ? """{"error":"access_denied"}"""
                        : """{"error":"expired_token"}""")
                };
            if (Failure switch
                {
                    "code" => path == "/login/device/code", "poll" => path == "/login/oauth/access_token",
                    "identity" => path == "/user", "usage" => path == "/copilot_internal/user", _ => false
                })
                throw new HttpRequestException("secret-server-message");
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class GatedCredentials : ICredentialStore
    {
        private readonly MemoryCredentials _inner = new();
        private int _writes, _cancelledWrites;
        internal ConcurrentDictionary<string, TokenSet> Values => _inner.Values;
        internal int Writes => Volatile.Read(ref _writes);
        internal int CancelledWrites => Volatile.Read(ref _cancelledWrites);
        internal bool FailWrite { get; set; }
        internal TaskCompletionSource Release { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TokenSet?> ReadAsync(Account account, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(account, cancellationToken);
        public async Task WriteAsync(Account account, TokenSet tokens, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writes);
            try { await Release.Task.WaitAsync(cancellationToken); cancellationToken.ThrowIfCancellationRequested(); }
            catch (OperationCanceledException) { Interlocked.Increment(ref _cancelledWrites); throw; }
            if (FailWrite) throw new IOException("secret-storage-message");
            await _inner.WriteAsync(account, tokens, cancellationToken);
        }
        public Task DeleteAsync(Account account, CancellationToken cancellationToken = default) =>
            _inner.DeleteAsync(account, cancellationToken);
    }
}
