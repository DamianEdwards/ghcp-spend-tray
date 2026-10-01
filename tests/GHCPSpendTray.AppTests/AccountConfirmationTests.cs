using System.Collections.Concurrent;
using GHCPSpendTray.App;
using GHCPSpendTray.App.UI;
using GHCPSpendTray.Core;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml.Controls;

internal static class AccountConfirmationTests
{
    internal static async Task<int> RunAsync(string root)
    {
        var handler = new FixtureHttp();
        using var http = new HttpClient(handler);
        var credentials = new GatedCredentials();
        using var app = new ApplicationController(root, true, http, credentials);
        var dispatch = new ConcurrentQueue<Action>();
        using var session = new AppSession(app, dispatch.Enqueue);
        var store = new JsonStore(root);
        int assertions = 0;
        session.Initialize();
        await Until(() => !session.Busy);
        Check(session.Initialized && session.Error is null, "confirmation fixture initialized");

        session.AddAccount();
        session.StartSignIn();
        await Until(() => session.Identity is not null);
        Check(session.SigningIn && session.ShowAddForm && !session.ConnectingAccount &&
            session.Notice is null && !session.NoticeIsSuccess, "browser authorization is not connection success");
        Check(credentials.Writes == 0 && (await store.LoadSettingsAsync()).Value.Accounts.Length == 0,
            "identity is never saved before explicit confirmation");
        var panel = UI.IdentityConfirmation(session, session.Identity!);
        Check(panel is { Severity: InfoBarSeverity.Warning, IsClosable: false } &&
            panel.Title?.Contains("Action required") == true && panel.Message?.Contains("setup is not") == true &&
            panel.Message.Contains("Connect this account"), "final step has a distinct warning and explicit required action");
        var body = (StackElement)panel.Content!;
        Check(Descendants(body).OfType<TextBlockElement>().Any(text => text.Content == "test-1 on github.com") &&
            Descendants(body).OfType<TextBlockElement>().Any(text => text.Content.Contains("Immutable user ID: 1")),
            "confirmation retains host, login and immutable identity");
        Check(body.Padding is { Bottom: 16, Right: 16 }, "confirmation content reserves bottom and right inset");
        var actions = body.Children.OfType<FlexElement>().Single();
        Check(actions.Direction == FlexDirection.Row && actions.Wrap == FlexWrap.Wrap &&
            actions.ColumnGap == 10 && actions.RowGap == 8 &&
            actions.Children.OfType<ButtonElement>().Select(button => button.Label)
                .SequenceEqual(["Connect this account", "Wrong account"]),
            "primary action precedes secondary in a spaced wrapping horizontal row");
        string preview = session.Identity!.AvatarPath!;
        Check(File.Exists(preview) && preview.Contains("pending-avatars") &&
            Descendants(body).OfType<PersonPictureElement>().Single() is { DisplayName: "test-1", ProfilePicture: var image } &&
            image == preview, "verified identity avatar uses isolated local cache and shared person picture");
        Check(!Directory.Exists(Path.Combine(root, "avatars")) &&
            !Directory.GetFiles(Path.Combine(root, "pending-avatars"), "*", SearchOption.AllDirectories)
                .Any(path => File.ReadAllText(path).Contains("synthetic-avatar-token")),
            "preview neither registers a durable avatar nor persists signed URLs");
        var connect = Buttons(panel).Single(button => button.Label == "Connect this account");
        Check(connect.Modifiers?.IsEnabled == true, "connect action is available at the final step");
        connect.OnClick!();
        await Until(() => credentials.Writes == 1);
        Check(session.SigningIn && session.ConnectingAccount && session.ShowAddForm &&
            session.Notice is null && !session.NoticeIsSuccess, "acceptance shows pending save, not completion");
        var saving = UI.IdentityConfirmation(session, session.Identity!);
        Check(saving.Severity == InfoBarSeverity.Informational && saving.Title == "Connecting account..." &&
            Buttons(saving).All(button => button.Modifiers?.IsEnabled == false),
            "pending save disables repeated identity decisions");
        session.ConfirmIdentity(false);
        Check(session.ConnectingAccount, "a second decision cannot replace accepted confirmation");
        Check((await store.LoadSettingsAsync()).Value.Accounts.Length == 0,
            "delayed credential save has not persisted account settings");
        credentials.Release.TrySetResult();
        await Until(() => !session.SigningIn);
        Check(credentials.Values.ContainsKey("github.com:1") &&
            (await store.LoadSettingsAsync()).Value.Accounts.Single().Key == "github.com:1",
            "successful connection persists credentials and account settings");
        Check(!session.ShowAddForm && session.Identity is null && !session.ConnectingAccount &&
            session.Notice == "Account connected." && session.NoticeIsSuccess,
            "completion appears only after successful save and clears onboarding state");
        Check(!File.Exists(preview) && PreviewsClean(), "successful save cleans transient avatar");
        Check(UI.Feedback(session) is InfoBarElement { Title: "Complete!", Severity: InfoBarSeverity.Success },
            "completion has a native success indicator");
        Check(UI.Feedback(session, showNotice: false) is null, "connection notices remain settings-only");
        session.SaveGlobal();
        await Until(() => !session.Busy);
        Check(UI.Feedback(session) is InfoBarElement { Severity: InfoBarSeverity.Informational },
            "later unrelated notices do not inherit connection success styling");

        handler.NextIdentity = "2";
        session.AddAccount();
        session.StartSignIn();
        await Until(() => session.Identity is not null);
        Buttons(UI.IdentityConfirmation(session, session.Identity!))
            .Single(button => button.Label == "Wrong account").OnClick!();
        await Until(() => !session.SigningIn);
        Check(session.ShowAddForm && session.Identity is null && session.Notice is null &&
            !session.NoticeIsSuccess && credentials.Writes == 1, "wrong-account rejection never saves or reports success");
        Check(PreviewsClean(), "wrong-account rejection cleans transient avatar");

        session.StartSignIn();
        await Until(() => session.Identity is not null);
        session.CancelSignIn();
        Check(!session.SigningIn && session.ShowAddForm && session.Identity is null &&
            session.Prompt is null && session.Notice is null, "cancelling confirmation retains the retryable form");

        credentials.FailWrite = true;
        session.StartSignIn();
        await Until(() => session.Identity is not null);
        session.ConfirmIdentity(true);
        await Until(() => !session.SigningIn);
        Check(session.ShowAddForm && session.Error is not null && session.Notice is null &&
            !session.NoticeIsSuccess && !session.ConnectingAccount,
            "save failure remains visible and retryable without a completion indicator");
        Check(!credentials.Values.ContainsKey("github.com:2") &&
            (await store.LoadSettingsAsync()).Value.Accounts.Length == 1, "failed save does not add an account");
        credentials.FailWrite = false;
        using (File.Open(Path.Combine(root, "config.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            session.StartSignIn();
            await Until(() => session.Identity is not null);
            session.ConfirmIdentity(true);
            await Until(() => !session.SigningIn);
            Check(session.ShowAddForm && session.Error is not null && session.Notice is null &&
                !session.NoticeIsSuccess && !credentials.Values.ContainsKey("github.com:2"),
                "settings save failure rolls back credentials and never reports completion");
        }
        Check((await store.LoadSettingsAsync()).Value.Accounts.Length == 1,
            "settings save failure preserves existing accounts");
        session.StartSignIn();
        await Until(() => session.Identity is not null);
        session.ConfirmIdentity(true);
        await Until(() => !session.SigningIn);
        Check(session.Error is null && session.NoticeIsSuccess &&
            (await store.LoadSettingsAsync()).Value.Accounts.Length == 2, "retry after save failure can complete");

        var account = session.Dashboard.Accounts.Single(a => a.Key == "github.com:1");
        session.Reconnect(account);
        Check(session.Notice is null && !session.NoticeIsSuccess,
            "opening reconnect clears previous completion feedback");
        session.StartSignIn();
        await Until(() => !session.SigningIn);
        Check(session.Error?.Contains("different account") == true && session.Identity is null &&
            session.Notice is null && !session.NoticeIsSuccess &&
            credentials.Values["github.com:1"].AccessToken == "fixture-1",
            "wrong-identity reconnect is rejected before confirmation and preserves credentials");
        handler.NextIdentity = "1";
        session.StartSignIn();
        await Until(() => session.Identity is not null);
        Check(session.ReconnectKey == "github.com:1" && session.Notice is null,
            "correct reconnect still requires the final identity confirmation");
        session.ConfirmIdentity(true);
        await Until(() => !session.SigningIn);
        Check(session.Notice == "Account reconnected." && session.NoticeIsSuccess &&
            (await store.LoadSettingsAsync()).Value.Accounts.Length == 2,
            "reconnect reports completion only after saving without duplicating accounts");

        handler.NextIdentity = "4";
        session.AddAccount();
        credentials.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int previousWrites = credentials.Writes;
        session.StartSignIn();
        await Until(() => session.Identity is not null);
        session.ConfirmIdentity(true);
        await Until(() => credentials.Writes > previousWrites);
        session.CancelSignIn();
        credentials.Release.TrySetResult();
        await Until(() => credentials.CancelledWrites == 1);
        Check(!session.SigningIn && !session.ConnectingAccount && session.ShowAddForm &&
            session.Notice is null && !session.NoticeIsSuccess &&
            !credentials.Values.ContainsKey("github.com:4") &&
            (await store.LoadSettingsAsync()).Value.Accounts.Length == 2,
            "cancelling a pending save neither persists nor displays stale completion");

        await Until(PreviewsClean);
        foreach (string failure in new[] { "http", "invalid", "oversized", "redirect", "timeout" })
        {
            handler.FailAvatar = failure == "http";
            handler.InvalidAvatar = failure == "invalid";
            handler.OversizedAvatar = failure == "oversized";
            handler.RedirectAvatar = failure == "redirect";
            handler.AvatarGate = failure == "timeout" ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
            session.StartSignIn();
            await Until(() => session.Identity is not null);
            Check(session.Identity!.AvatarPath is null && session.Error is null &&
                Descendants(UI.IdentityConfirmation(session, session.Identity).Content!)
                    .OfType<PersonPictureElement>().Single() is { ProfilePicture: null, DisplayName: "test-4" },
                $"avatar {failure} uses initials without failing identity confirmation");
            session.ConfirmIdentity(false);
            await Until(() => !session.SigningIn);
            Check(PreviewsClean(), $"avatar {failure} leaves no transient files");
        }
        handler.FailAvatar = handler.InvalidAvatar = handler.OversizedAvatar = handler.RedirectAvatar = false;
        handler.AvatarGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = handler.AvatarRequests;
        session.StartSignIn();
        await Until(() => handler.AvatarRequests > requests);
        Check(session.Identity is null && session.SigningIn, "avatar loading never offers an unverified or stale identity");
        var previousGate = handler.AvatarGate;
        session.CancelSignIn();
        handler.AvatarGate = null;
        handler.NextIdentity = "6";
        session.StartSignIn();
        await Until(() => session.Identity is not null);
        previousGate.TrySetResult();
        Check(session.Identity!.Login == "test-6" && File.Exists(session.Identity.AvatarPath) &&
            session.Error is null, "cancelled avatar fetch cannot replace a newer sign-in identity");
        session.ConfirmIdentity(false);
        await Until(() => !session.SigningIn && PreviewsClean());
        Check(credentials.Values.Count == 2, "avatar loading and cancelled sign-ins never register credentials");

        handler.NextIdentity = "1";
        string savedAvatar = new AvatarCache(http, root).GetPath(new Account { Host = "github.com", UserId = "1", Login = "test-1" })!;
        session.Reconnect(account);
        session.StartSignIn();
        await Until(() => session.Identity is not null);
        Check(session.Identity!.AvatarPath != savedAvatar && File.Exists(savedAvatar),
            "reconnect preview is isolated from the registered avatar");
        session.ConfirmIdentity(false);
        await Until(() => !session.SigningIn);
        Check(File.Exists(savedAvatar) && PreviewsClean(), "rejected reconnect preserves the original cached avatar");
        return assertions;

        bool PreviewsClean() => !Directory.Exists(Path.Combine(root, "pending-avatars")) ||
            !Directory.EnumerateFileSystemEntries(Path.Combine(root, "pending-avatars")).Any();
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
            throw new TimeoutException("Account confirmation state did not arrive.");
        }
    }

    private static IEnumerable<ButtonElement> Buttons(InfoBarElement panel) =>
        Descendants(panel.Content!).OfType<ButtonElement>();

    private static IEnumerable<Element> Descendants(Element element)
    {
        yield return element;
        Element[] children = element switch
        {
            StackElement stack => stack.Children,
            GridElement grid => grid.Children,
            FlexElement flex => flex.Children,
            _ => []
        };
        foreach (var child in children)
            foreach (var descendant in Descendants(child)) yield return descendant;
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
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { Interlocked.Increment(ref _cancelledWrites); throw; }
            if (FailWrite) throw new AppOperationException("Synthetic credential save failed.");
            await _inner.WriteAsync(account, tokens, cancellationToken);
        }
        public Task DeleteAsync(Account account, CancellationToken cancellationToken = default) =>
            _inner.DeleteAsync(account, cancellationToken);
    }
}
