using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using GHCPSpendTray.Core;
using GHCPSpendTray.MacBridge;
using GHCPSpendTray.Shared;

string root = Path.Combine(Path.GetTempPath(), "GHCPSpendTray-SharedTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int assertions = 0;
try
{
    var credentials = new MemoryCredentials();
    var startup = new Startup();
    var handler = new FixtureHttp();
    using var http = new HttpClient(handler);
    using (var controller = new ApplicationController(root, false, credentials, http, () => Task.FromResult<IStartupRegistration>(startup)))
    {
        DashboardView? dashboard = null;
        controller.Changed += value => Volatile.Write(ref dashboard, value);
        int notifications = 0;
        controller.SetNotificationHandler((_, _, _) => { Interlocked.Increment(ref notifications); return Task.FromResult(true); });
        await controller.InitializeAsync();
        Check(dashboard?.ConsumptionUsd is null, "Empty consumption is unavailable, not zero.");
        Check(controller.Settings.PollMinutes == 60 && controller.Settings.CanChangeStartup, "Shared defaults and startup adapter.");
        await controller.SaveSettingsAsync(new(15, "100, 50, 80", true, true, 10m));
        Check(startup.Enabled && controller.Settings.Thresholds == "50, 80, 100", "Startup and global settings saved.");
        await Reject<AppOperationException>(() => controller.SaveSettingsAsync(new(4, "50", true, false)));
        Check(startup.Enabled, "Invalid settings do not change startup.");
        startup.Reject = true;
        await Reject<AppOperationException>(() => controller.SaveSettingsAsync(new(20, "50", true, false)));
        Check(controller.Settings.PollMinutes == 15, "Startup failure preserves settings.");
        startup.Reject = false;

        int authorized = 0;
        await controller.AddAsync("github.com", false, null, prompt => Check(prompt.Code == "SYNTHETIC", "Device prompt crosses shared boundary."),
            () => authorized++, default);
        Check(authorized == 1 && credentials.Values.Count == 1, "Browser approval saves the verified identity without another confirmation.");
        await controller.RefreshAsync();
        Check(dashboard?.ConsumptionUsd == 26.25m && dashboard.Accounts[0].Percent == 105m, "Account consumption and allocation.");
        Check(notifications == 1, "Percentage and dollar milestones coalesce.");
        await controller.RefreshAsync();
        Check(notifications == 1, "Alert ledger prevents duplicate notifications.");
        Check(credentials.Values.Count == 1, "Credentials are persisted through injected platform store.");
        Check(dashboard?.Tray?.RollUp.Percent == 105 && dashboard.Tray.RollUp.IsOverAllocation, "Shared dashboard includes truthful tray allocation.");
        await controller.SaveSettingsAsync(controller.Settings with
        {
            TrayStyle = TrayIconStyle.Percentage, TrayMode = TrayDisplayMode.PerAccount, ExcludedTrayAccounts = ["github.com:1"]
        });
        Check(dashboard?.Tray?.Icons.Count == 1 && dashboard.Tray.RollUp.Percent is null, "Excluding all accounts retains unavailable roll-up.");
        Check((await new JsonStore(root).LoadSettingsAsync()).Value.Accounts[0].ExcludeFromTray, "Tray exclusions persist.");
        await controller.AddAsync("github.com", false, "github.com:1", _ => { }, () => { }, default);
        Check(controller.Settings.ExcludedTrayAccounts?.SequenceEqual(["github.com:1"]) == true, "Reconnect preserves tray exclusion.");
        await Reject<AppOperationException>(() => controller.AddAsync("github.com", false, null, _ => { }, () => { }, default));
        handler.UserId = "2";
        await Reject<AppOperationException>(() => controller.AddAsync("github.com", false, "github.com:1", _ => { }, () => { }, default));
        Check(credentials.Values["github.com:1"].AccessToken == "synthetic-token-1", "Wrong identity reconnect preserves credential.");
        using (var cancel = new CancellationTokenSource())
            await Reject<OperationCanceledException>(() => controller.AddAsync("github.com", false, null, _ => cancel.Cancel(), () => { }, cancel.Token));
        Check(credentials.Values.Count == 1, "Cancelled sign-in is not saved.");
        await controller.SaveAccountAsync("github.com:1", "Example", "60, 90", 0m);
        Check(controller.AccountSettings("github.com:1") == ("Example", "60, 90", 0m), "Per-account inheritance and disable settings.");
        handler.UserId = "1";
        await controller.RefreshAccountAsync("github.com:1");
        var persisted = await new JsonStore(root).LoadSettingsAsync();
        Check(persisted.Value.Accounts[0].AvatarUrl is null, "Avatar URLs are not persisted.");
        credentials.FailDelete = true;
        await Reject<AppOperationException>(() => controller.RemoveAsync("github.com:1"));
        Check((await new JsonStore(root).LoadSettingsAsync()).Value.Accounts.Length == 1, "Failed credential deletion remains retryable.");
        credentials.FailDelete = false;
        await controller.RemoveAsync("github.com:1");
        Check(credentials.Values.IsEmpty && (await new JsonStore(root).LoadSettingsAsync()).Value.Accounts.Length == 0, "Removal deletes credentials and configuration.");
    }
    using (var bridge = new BridgeRuntime())
    {
        var init = new Command { Id = "init", Method = "initialize", Directory = Path.Combine(root, "demo"), Demo = true };
        var encoded = JsonSerializer.Serialize(init, BridgeJsonContext.Default.Command);
        Check(JsonSerializer.Deserialize(encoded, BridgeJsonContext.Default.Command) == init, "Source-generated command round-trip.");
        Check(bridge.Send(init).Error is null, "Bridge accepts initialization.");
        var initialized = await Complete(bridge, "init");
        Check(initialized.Error is null && initialized.Settings?.PollMinutes == 60, "Bridge reports asynchronous initialization.");
        Check(bridge.Send(new() { Id = "save", Method = "settings.save", Settings = new(30, "50, 80, 100", true, false, 12.50m) }).Error is null, "Bridge accepts settings.");
        var saved = await Complete(bridge, "save");
        Check(saved.Settings?.SpendIncrementUsd == 12.50m, "Decimal settings retain exact cents across bridge.");
        bridge.Send(new() { Id = "host", Method = "host.describe", Host = "team.ghe.com" });
        Check((await Complete(bridge, "host")).Text?.Contains("https://api.team.ghe.com/") == true, "Host destinations use shared resolver.");
        Check(bridge.Send(new() { Method = "host.describe", Host = "https://github.com/evil" }).Error is not null, "Bridge rejects unsafe host.");
        bridge.Send(new() { Id = "unknown", Method = "unknown" });
        Check((await Complete(bridge, "unknown")).Error is not null, "Unknown commands fail explicitly.");
        bridge.Send(new() { Id = "auth", Method = "signin", Host = "github.com" });
        Check((await Complete(bridge, "auth")).Error?.Contains("disabled") == true, "Demo never performs authentication.");
        bridge.Send(new() { Id = "obsolete", Method = "signin.confirm" });
        Check((await Complete(bridge, "obsolete")).Error is not null, "Obsolete confirmation command is rejected.");
        Check(bridge.Send(new() { Method = "platform.reply", TargetId = "expired", Reply = new() }).Error is null, "Late canceled platform reply is safe.");

        var draft = saved.Settings! with { TrayStyle = TrayIconStyle.Percentage, TrayMode = TrayDisplayMode.PerAccount,
            ExcludedTrayAccounts = ["example.ghe.com:2"] };
        bridge.Send(new() { Id = "preview", Method = "tray.preview", Settings = draft });
        var preview = (await Complete(bridge, "preview")).Tray!;
        Check(preview.Style == TrayIconStyle.Percentage && preview.Icons.Single().AccountKey == "github.com:1",
            "Preview uses draft style, mode and account selection.");
        Check(preview.RollUp.Percent == 105 && preview.RollUp.IsOverAllocation, "Preview retains over-allocation.");
        bridge.Send(new() { Id = "unchanged", Method = "refresh" });
        Check((await Complete(bridge, "unchanged")).Settings!.TrayStyle == TrayIconStyle.Pie, "Preview does not change saved settings.");
        bridge.Send(new() { Id = "apply", Method = "settings.save", Settings = draft });
        Check((await Complete(bridge, "apply")).Settings!.TrayMode == TrayDisplayMode.PerAccount, "Tray save updates settings.");
        draft = draft with { TrayMode = TrayDisplayMode.RollUp, ExcludedTrayAccounts = [] };
        bridge.Send(new() { Id = "rollup", Method = "tray.preview", Settings = draft });
        Check((await Complete(bridge, "rollup")).Tray!.RollUp.Percent == 34.2, "Roll-up uses weighted allocation, not mean percentage.");
        draft = draft with { ExcludedTrayAccounts = ["github.com:1", "example.ghe.com:2"] };
        bridge.Send(new() { Id = "none", Method = "tray.preview", Settings = draft });
        Check((await Complete(bridge, "none")).Tray!.Icons.Single().NumericText == "?", "Unavailable preview is not zero.");
    }
    using (var bridge = new BridgeRuntime())
    {
        bridge.Send(new() { Id = "real-init", Method = "initialize", Directory = Path.Combine(root, "empty-real") });
        BridgeEvent? request = null;
        await Until(() =>
        {
            request ??= bridge.Poll().FirstOrDefault(e => e.Kind == "platform" && e.Operation == "startup.read");
            return request is not null;
        });
        Check(bridge.Send(new() { Id = "busy", Method = "refresh" }).Error is not null, "Bridge serializes user mutations while waiting for platform.");
        bridge.Send(new() { Method = "platform.reply", TargetId = request!.Id, Reply = new() { CanChange = true, Description = "Synthetic startup." } });
        Check((await Complete(bridge, "real-init")).Error is null, "Platform completion unblocks shared initialization.");
        bridge.Send(new() { Id = "startup", Method = "settings.save", Settings = new(30, "50, 80, 100", true, true) });
        request = null;
        await Until(() =>
        {
            request ??= bridge.Poll().FirstOrDefault(e => e.Operation == "startup.write");
            return request is not null;
        });
        Check(request!.Enabled, "Login preference reaches native adapter.");
        bridge.Send(new() { Method = "platform.reply", TargetId = request.Id, Reply = new() { Error = "Synthetic macOS denial." } });
        Check((await Complete(bridge, "startup")).Error == "Synthetic macOS denial.", "Native errors remain actionable.");
    }
    using (var bridge = new BridgeRuntime())
    {
        bridge.Send(new() { Id = "stop", Method = "initialize", Directory = Path.Combine(root, "shutdown") });
        await Until(() => bridge.Poll().Any(e => e.Kind == "platform"));
        bridge.Dispose();
        Check(bridge.Send(new() { Id = "late", Method = "refresh" }).Error is not null, "Shutdown cancels outstanding native work.");
    }
    Console.WriteLine($"PASS: {assertions} shared application and native bridge assertions; synthetic data only.");
}
finally { Directory.Delete(root, recursive: true); }

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    assertions++;
}
async Task Reject<T>(Func<Task> operation) where T : Exception
{
    try { await operation(); }
    catch (T) { assertions++; return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
static async Task Until(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!condition()) await Task.Delay(10, timeout.Token);
}
static async Task<BridgeEvent> Complete(BridgeRuntime bridge, string id)
{
    BridgeEvent? result = null;
    await Until(() =>
    {
        var events = bridge.Poll();
        // Exercise generated event serialization used by the Native AOT ABI.
        var json = JsonSerializer.Serialize(events, BridgeJsonContext.Default.BridgeEventArray);
        result = JsonSerializer.Deserialize(json, BridgeJsonContext.Default.BridgeEventArray)!
            .FirstOrDefault(e => e.Kind == "completed" && e.Id == id);
        return result is not null;
    });
    return result!;
}

sealed class Startup : IStartupRegistration
{
    public bool Enabled { get; private set; }
    public bool CanChange => true;
    public string Description => "Synthetic startup.";
    public bool Reject { get; set; }
    public Task SetEnabledAsync(bool enabled)
    {
        if (Reject) throw new PlatformOperationException("Synthetic startup denial.");
        Enabled = enabled;
        return Task.CompletedTask;
    }
}

sealed class MemoryCredentials : ICredentialStore
{
    public ConcurrentDictionary<string, TokenSet> Values { get; } = new();
    public bool FailDelete { get; set; }
    public Task<TokenSet?> ReadAsync(Account account, CancellationToken cancellationToken = default) => Task.FromResult(Values.GetValueOrDefault(account.Key));
    public Task WriteAsync(Account account, TokenSet tokens, CancellationToken cancellationToken = default)
    { Values[account.Key] = tokens; return Task.CompletedTask; }
    public Task DeleteAsync(Account account, CancellationToken cancellationToken = default)
    {
        if (FailDelete) throw new PlatformOperationException("Synthetic credential-store denial.");
        Values.TryRemove(account.Key, out _);
        return Task.CompletedTask;
    }
}

sealed class FixtureHttp : HttpMessageHandler
{
    public string UserId { get; set; } = "1";
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string json = request.RequestUri!.AbsolutePath switch
        {
            "/login/device/code" => """{"device_code":"synthetic-device","user_code":"SYNTHETIC","verification_uri":"https://github.com/login/device","expires_in":60,"interval":1}""",
            "/login/oauth/access_token" => $$"""{"access_token":"synthetic-token-{{UserId}}","token_type":"bearer","scope":"read:user"}""",
            "/user" => $$"""{"id":{{UserId}},"login":"synthetic-user"}""",
            "/copilot_internal/user" => """{"quota_snapshots":{"premium_interactions":{"token_based_billing":true,"credits_used":2625,"entitlement":2500,"has_quota":true,"unlimited":false}}}""",
            _ => throw new InvalidOperationException("Unexpected synthetic request.")
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
