using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using GHCPSpendTray.Core;
using GHCPSpendTray.Linux;
using GHCPSpendTray.Shared;
using Tmds.DBus.Protocol;

internal static class BackendTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }

    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "ghcp-linux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var credentials = new MemoryCredentials();
            var handler = new FixtureHttp();
            using (var session = new LinuxSession(new ApplicationController(root, false, credentials, new HttpClient(handler))))
            using (var bus = new DemoBus(session))
            using (var client = new DBusConnection(DBusAddress.Session!))
            {
                Check(await bus.StartAsync(), "Real backend owns the isolated desktop bus");
                await session.StartAsync();
                await client.ConnectAsync();
                Check(!session.Snapshot.Demo && session.Snapshot.Accounts.Length == 0 &&
                    session.Snapshot.Consumption == "Unavailable", "Real mode starts empty, never as demo or zero");
                MessageBuffer Call(string method, string? argument = null)
                {
                    using var writer = client.GetMessageWriter();
                    writer.WriteMethodCallHeader(destination: DemoBus.Name, path: bus.Path,
                        @interface: DemoBus.Interface, member: method, signature: argument is null ? null : "s");
                    if (argument is not null) writer.WriteString(argument);
                    return writer.CreateMessage();
                }
                Task Execute(string json) => client.CallMethodAsync(Call("Execute", json));
                async Task<string> State()
                {
                    string json = await client.CallMethodAsync(Call("GetSignIn"),
                        static (message, _) => message.GetBodyReader().ReadString());
                    if (json.Contains("synthetic-token")) throw new InvalidOperationException("Token in sign-in reply.");
                    using var doc = JsonDocument.Parse(json);
                    return doc.RootElement.GetProperty("phase").GetString()!;
                }
                async Task WaitFor(string phase)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    while (await State() != phase) await Task.Delay(50, timeout.Token);
                }
                async Task Reject(Func<Task> action)
                {
                    bool failed = false;
                    try { await action(); }
                    catch (DBusErrorReplyException) { failed = true; }
                    Check(failed, "Invalid or unavailable desktop action is rejected");
                }
                await Reject(() => client.CallMethodAsync(Call("AddDemoAccount")));
                await Reject(() => Execute("""{"kind":"signin","host":"http://github.com"}"""));
                await Reject(() => Execute("""{"kind":"signin","host":"enterprise.example","clientId":null}"""));
                await Reject(() => Execute("""{"kind":"save","unexpected":true}"""));
                await Execute("""{"kind":"signin","host":"github.com","offlineAccess":true}""");
                await WaitFor("waiting");
                Check(!session.Snapshot.ToJson().Contains("SYNTHETIC-CODE") &&
                    !session.Snapshot.ToJson().Contains("synthetic-token"), "Broadcast snapshot excludes device codes and tokens");
                await Reject(() => Execute("""{"kind":"signin"}"""));
                handler.AllowToken.TrySetResult();
                await WaitFor("complete");
                Check(session.Snapshot.Accounts.Single().Key == "github.com:1" &&
                    session.Snapshot.Consumption == "$26.25", "Linux sign-in uses shared identity and consumption verification");
                Check(credentials.Values.Count == 1, "Verified tokens go only to the injected credential store");
                await Execute("""{"kind":"settings","settings":{"pollMinutes":15,"thresholds":"50, 80","notifications":false,"startup":false,"trayStyle":"Percentage","trayMode":"PerAccount","excludedTrayAccounts":[]}}""");
                Check(session.Settings is { PollMinutes: 15, Notifications: false, TrayMode: TrayDisplayMode.PerAccount } &&
                    session.Snapshot.Icons?.Single().Key == "github.com:1",
                    "Global settings persist and drive independent account indicators");
                string draft = await client.CallMethodAsync(Call("Preview",
                    """{"kind":"preview","settings":{"pollMinutes":15,"thresholds":"50","notifications":false,"startup":false,"trayStyle":"Pie","trayMode":"RollUp","excludedTrayAccounts":["github.com:1"]}}"""),
                    static (message, _) => message.GetBodyReader().ReadString());
                using (var preview = JsonDocument.Parse(draft))
                    Check(preview.RootElement.GetProperty("tray").GetProperty("rollUp").GetProperty("percent").ValueKind == JsonValueKind.Null &&
                        preview.RootElement.GetProperty("icons")[0].GetProperty("imageUri").GetString()!.StartsWith("data:image/png;base64,") &&
                        session.Settings.TrayMode == TrayDisplayMode.PerAccount,
                        "Draft preview uses shared exclusions without changing saved settings");
                await Reject(() => Execute("""{"kind":"settings","settings":{"pollMinutes":1,"thresholds":"50","notifications":false,"startup":false}}"""));
                bool notified = false;
                session.SetNotificationHandler(_ => { notified = true; return Task.FromResult(true); });
                await Execute("""{"kind":"testNotification"}""");
                Check(notified, "Notification test bypasses alert thresholds and explicitly submits to the platform");
                session.SetNotificationHandler(_ => Task.FromResult(false));
                await Reject(() => Execute("""{"kind":"testNotification"}"""));
                await Execute("""{"kind":"save","key":"github.com:1","displayName":"Work","thresholds":"60, 90","spendIncrementUsd":0}""");
                Check(session.Snapshot.Accounts[0] is { Name: "Work", Thresholds: "60, 90", SpendIncrementUsd: 0 },
                    "Linux editing persists account preferences");
                await Execute("""{"kind":"save","key":"github.com:1","displayName":"Work","thresholds":"60, 90","spendIncrementUsd":0,"showPeriodEstimate":true}""");
                Check(session.Snapshot.Accounts[0].PeriodEstimate is not null &&
                    session.Snapshot.Accounts[0].Details?.CreditsUsed == 2625,
                    "Estimate opt-in exposes shared projection or unavailable reason and full diagnostics");
                await Execute("""{"kind":"signin","key":"github.com:1"}""");
                await WaitFor("complete");
                Check(session.Snapshot.Accounts[0].Name == "Work", "Reconnect preserves account preferences");
                handler.UserId = "2";
                await Execute("""{"kind":"signin","key":"github.com:1"}""");
                await WaitFor("error");
                Check(credentials.Values.Count == 1, "Wrong-identity reconnect never overwrites credentials");
                handler.AllowToken = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await Execute("""{"kind":"signin"}""");
                await WaitFor("waiting");
                await Execute("""{"kind":"cancel"}""");
                await WaitFor("cancelled");
                Check(!session.SignInJson().Contains("SYNTHETIC-CODE"), "Cancellation removes the device prompt");
                credentials.FailDelete = true;
                await Reject(() => Execute("""{"kind":"remove","key":"github.com:1"}"""));
                Check(session.Snapshot.Accounts.Length == 1, "Failed credential deletion retains the account for retry");
                credentials.FailDelete = false;
                await Execute("""{"kind":"remove","key":"github.com:1"}""");
                Check(session.Snapshot.Accounts.Length == 0 && credentials.Values.IsEmpty,
                    "Removing an account deletes its credential and configuration");
                foreach (string file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
                    Check(!File.ReadAllText(file).Contains("synthetic-token"), "No token appears in persisted JSON");
                handler.AllowToken.TrySetResult();
                credentials.FailWrite = true;
                await Execute("""{"kind":"signin"}""");
                await WaitFor("error");
                Check(session.Snapshot.Accounts.Length == 0 && credentials.Values.IsEmpty,
                    "Failed credential save never adds an account");
                credentials.FailWrite = false;
                await Execute("""{"kind":"signin"}""");
                await WaitFor("complete");
                await Execute("""{"kind":"save","key":"github.com:2","displayName":"Restored","thresholds":"75"}""");
            }
            using (var restored = new LinuxSession(new ApplicationController(root, false, credentials, new HttpClient(handler))))
            {
                await restored.StartAsync();
                Check(restored.Snapshot.Accounts.Single() is { Name: "Restored", Thresholds: "75" },
                    "Real accounts and preferences survive helper restart");
                Check(restored.Settings is { PollMinutes: 15, Notifications: false, TrayMode: TrayDisplayMode.PerAccount },
                    "Global preferences survive helper restart");
                await restored.ExecuteAsync("""{"kind":"remove","key":"github.com:2"}""");
                Check(restored.Snapshot.Accounts.Length == 0, "Restored accounts can be removed");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static async Task CredentialsAsync(string mode)
    {
        if (Environment.GetEnvironmentVariable("GHCP_ISOLATED_KEYRING") != "1")
            throw new InvalidOperationException("Credential integration tests require the isolated keyring runner.");
        var store = new SecretServiceCredentials();
        var account = new Account { Host = "github.com", UserId = "987654321", Login = "synthetic-test" };
        var enterprise = account with { Host = "test.ghe.com", OAuthClientId = "synthetic-client" };
        var otherClient = enterprise with { OAuthClientId = "other-client" };
        Check(SecretServiceCredentials.Target(account) != SecretServiceCredentials.Target(enterprise) &&
            SecretServiceCredentials.Target(enterprise) != SecretServiceCredentials.Target(otherClient),
            "Credential identity is host-, user- and OAuth-client-specific");
        if (mode == "--no-keyring")
        {
            bool rejected = false;
            try { await store.ReadAsync(account); }
            catch (ServiceException ex) when (ex.Status == AccountStatus.StorageError) { rejected = true; }
            Check(rejected, "Missing Secret Service is an explicit failure, not missing credentials");
            return;
        }
        if (mode == "--keyring-seed")
        {
            await store.WriteAsync(account, new() { AccessToken = "synthetic-locked-token" });
            return;
        }
        if (mode == "--keyring-locked")
        {
            foreach (Func<CancellationToken, Task> action in new Func<CancellationToken, Task>[]
            {
                async token => { _ = await store.ReadAsync(account, token); },
                token => store.WriteAsync(account, new() { AccessToken = "must-not-be-written" }, token),
                token => store.DeleteAsync(account, token)
            })
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                bool rejected = false;
                try { await action(timeout.Token); }
                catch (ServiceException ex) when (ex.Status == AccountStatus.StorageError) { rejected = true; }
                catch (OperationCanceledException) { rejected = true; }
                Check(rejected, "Locked keyring never reports successful read, write or deletion");
            }
            return;
        }
        if (mode == "--keyring-cleanup")
        {
            Check((await store.ReadAsync(account))?.AccessToken == "synthetic-locked-token",
                "Locked-keyring failures preserve the original credential");
            await store.DeleteAsync(account);
            return;
        }
        if (mode != "--keyring") throw new ArgumentException("Unknown credential test mode.");
        try
        {
            Check(await store.ReadAsync(account) is null, "Missing credential is distinguished from service failure");
            await store.WriteAsync(account, new() { AccessToken = "synthetic-token", RefreshToken = "synthetic-refresh" });
            Check((await store.ReadAsync(account))?.RefreshToken == "synthetic-refresh", "libsecret stores and reads complete token sets");
            Check(await store.ReadAsync(enterprise) is null, "Host isolation is enforced in the real keyring");
            await store.WriteAsync(enterprise, new() { AccessToken = "synthetic-enterprise" });
            Check(await store.ReadAsync(otherClient) is null, "OAuth-client isolation is enforced in the real keyring");
            await store.WriteAsync(account, new() { AccessToken = "synthetic-replacement" });
            Check((await store.ReadAsync(account))?.AccessToken == "synthetic-replacement", "Credential updates replace the exact identity");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            bool stopped = false;
            try { await store.DeleteAsync(account, cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && await store.ReadAsync(account) is not null, "Cancelled deletion preserves stored tokens");
            SecretService.Access(SecretServiceCredentials.Target(account), "invalid-json", false, default);
            bool corrupt = false;
            try { await store.ReadAsync(account); }
            catch (ServiceException ex) when (ex.Status == AccountStatus.SignInRequired) { corrupt = true; }
            Check(corrupt, "Corrupt keyring payload requires reconnect instead of becoming missing credentials");
        }
        finally
        {
            await store.DeleteAsync(account);
            await store.DeleteAsync(enterprise);
        }
        Check(await store.ReadAsync(account) is null && await store.ReadAsync(enterprise) is null,
            "Credential deletion leaves no matching item");
        await store.DeleteAsync(account);
    }

    private sealed class MemoryCredentials : ICredentialStore
    {
        internal ConcurrentDictionary<string, TokenSet> Values { get; } = new();
        internal bool FailDelete { get; set; }
        internal bool FailWrite { get; set; }
        public Task<TokenSet?> ReadAsync(Account account, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.GetValueOrDefault(account.Key));
        public Task WriteAsync(Account account, TokenSet tokens, CancellationToken cancellationToken = default)
        {
            if (FailWrite) throw new PlatformOperationException("Synthetic keyring locked.");
            Values[account.Key] = tokens;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Account account, CancellationToken cancellationToken = default)
        {
            if (FailDelete) throw new PlatformOperationException("Synthetic deletion refusal.");
            Values.TryRemove(account.Key, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class FixtureHttp : HttpMessageHandler
    {
        internal string UserId { get; set; } = "1";
        internal TaskCompletionSource AllowToken { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/login/oauth/access_token")
                await AllowToken.Task.WaitAsync(cancellationToken);
            string json = request.RequestUri.AbsolutePath switch
            {
                "/login/device/code" => """{"device_code":"synthetic-device","user_code":"SYNTHETIC-CODE","verification_uri":"https://github.com/login/device","expires_in":60,"interval":1}""",
                "/login/oauth/access_token" => $$"""{"access_token":"synthetic-token-{{UserId}}","token_type":"bearer","scope":"read:user"}""",
                "/user" => $$"""{"id":{{UserId}},"login":"synthetic-user"}""",
                "/copilot_internal/user" => """{"quota_snapshots":{"premium_interactions":{"token_based_billing":true,"credits_used":2625,"entitlement":2500,"has_quota":true,"unlimited":false}}}""",
                _ => throw new InvalidOperationException("Unexpected synthetic request.")
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
