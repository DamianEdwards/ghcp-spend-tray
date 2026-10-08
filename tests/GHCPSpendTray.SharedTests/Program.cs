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
    using (var demo = new DemoController(Path.Combine(root, "unlimited-demo"), unlimited: true))
    {
        DashboardView? preview = null;
        demo.Changed += value => preview = value;
        await demo.InitializeAsync();
        Check(preview!.Accounts.Count == 1 && preview.Accounts[0].Details.Unlimited &&
            preview.Accounts[0].Percent is null && preview.Tray!.RollUp.IsUnlimited &&
            preview.ConsumptionUsd == 26.25m, "Unlimited demo starts with one synthetic unlimited account.");
        preview.TrayStates!.Single().Snapshot!.Validate();
        Check(demo.AccountSettings("github.com:1").DisplayName == "Unlimited (demo)",
            "Unlimited demo account preferences match the displayed account.");
        await demo.SaveSettingsAsync(demo.Settings with { TrayStyle = TrayIconStyle.Percentage });
        Check(preview!.Tray!.Style == TrayIconStyle.Percentage && preview.Tray.RollUp.NumericText == "\u221e",
            "Unlimited demo supports a live percentage-style infinity preview.");
        await demo.RefreshAsync();
        Check(preview!.Tray!.RollUp.IsUnlimited, "Refreshing synthetic unlimited data retains infinity.");
        await demo.AddExampleAccountAsync();
        Check(preview!.Accounts.Count == 2 && preview.Accounts[1].Key == "github.com:2" &&
            preview.Tray!.RollUp is { IsUnlimited: false, Percent: 25, IsPartial: true },
            "Additional finite examples have unique identities and produce a truthful mixed roll-up.");
    }
    using (var controller = new ApplicationController(root, false, credentials, http, () => Task.FromResult<IStartupRegistration>(startup)))
    {
        DashboardView? dashboard = null;
        controller.Changed += value => Volatile.Write(ref dashboard, value);
        int notifications = 0;
        NotificationView? notification = null;
        controller.SetNotificationHandler(value =>
        {
            notification = value;
            Interlocked.Increment(ref notifications);
            return Task.FromResult(true);
        });
        await controller.InitializeAsync();
        Check(dashboard?.ConsumptionUsd is null, "Empty consumption is unavailable, not zero.");
        Check(controller.Settings.PollMinutes == 10 && controller.Settings.CanChangeStartup, "Shared defaults and startup adapter.");
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
        Check(dashboard!.Accounts[0].PeriodEstimate is null && !controller.AccountSettings("github.com:1").ShowPeriodEstimate,
            "New accounts have no estimate until explicitly enabled.");
        Check(notifications == 1, "Percentage and dollar milestones coalesce.");
        Check(notification is { AccountKey: "github.com:1", PercentConsumed: 105m, SpendMilestoneUsd: 20m } &&
            notification.Message.Contains("$26.25") && notification.Title == "GHCPSpendTray consumption alert",
            "Notification carries the alert account's actual snapshot percentage and milestone, not the crossed threshold or roll-up.");
        await controller.RefreshAsync();
        Check(notifications == 1, "Alert ledger prevents duplicate notifications.");
        var sample = dashboard!.TrayStates![0].Snapshot!;
        var account = dashboard.TrayStates[0].Account;
        await controller.SubmitAsync(new(account, sample with
        {
            Unlimited = true, PercentConsumed = null
        }, 0, [], 20m));
        Check(notification is { PercentConsumed: null, SpendMilestoneUsd: 20m } &&
            notification.Message.Contains("$20.00") && !notification.Message.Contains("%"),
            "Dollar-only notification preserves the milestone without inventing an allocation percentage.");
        await controller.SubmitAsync(new(account, sample with
        {
            CreditsUsed = 2187.5m, ConsumptionUsd = 21.875m, PercentConsumed = 87.5m
        }, 80m, [80m]));
        Check(notification is { PercentConsumed: 87.5m, SpendMilestoneUsd: null },
            "Artwork data uses current consumption rather than rounding down to the crossed allocation threshold.");
        Check(credentials.Values.Count == 1, "Credentials are persisted through injected platform store.");
        Check(dashboard?.Tray?.RollUp.Percent == 105 && dashboard.Tray.RollUp.IsOverAllocation, "Shared dashboard includes truthful tray allocation.");
        handler.Unlimited = true;
        await controller.RefreshAsync();
        Check(dashboard!.IsComplete && dashboard.ConsumptionUsd == 26.25m &&
            dashboard.Accounts[0].Details.Unlimited && dashboard.Accounts[0].Percent is null &&
            dashboard.Tray!.RollUp is { IsUnlimited: true, NumericText: "\u221e", IsPartial: false },
            "Successful unlimited refresh retains observed dollars and exposes infinity rather than unavailable usage.");
        var unlimitedEvents = new[] { new BridgeEvent { Kind = "changed", Dashboard = dashboard, Tray = dashboard.Tray } };
        var unlimitedJson = JsonSerializer.Serialize(unlimitedEvents, BridgeJsonContext.Default.BridgeEventArray);
        var unlimitedRoundTrip = JsonSerializer.Deserialize(unlimitedJson, BridgeJsonContext.Default.BridgeEventArray)!.Single();
        Check(unlimitedRoundTrip.Tray!.RollUp is { IsUnlimited: true, Percent: null, NumericText: "\u221e" } &&
            unlimitedRoundTrip.Dashboard!.Tray!.Icons[0].IsUnlimited &&
            unlimitedRoundTrip.Tray.Accounts[0].IsUnlimited,
            "Generated bridge JSON preserves unlimited state and infinity in standalone and dashboard tray payloads.");
        handler.Unlimited = false;
        await controller.RefreshAsync();
        await controller.SaveAccountAsync("github.com:1", "", "", showPeriodEstimate: true);
        Check(controller.AccountSettings("github.com:1").ShowPeriodEstimate && dashboard!.Accounts[0].PeriodEstimate is not null,
            "Enabling an account estimate updates the shared dashboard.");
        Check((await new JsonStore(root).LoadSettingsAsync()).Value.Accounts[0].ShowPeriodEstimate,
            "Account estimate preference persists.");
        Check(dashboard!.Accounts[0].PeriodEstimate == PeriodEstimates.Create(dashboard.TrayStates![0],
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(controller.Settings.PollMinutes)),
            "Controller exposes the shared calculation rather than frontend-specific arithmetic.");
        await Reject<AppOperationException>(() => controller.SaveAccountAsync("github.com:1", "Bad\nname", "", showPeriodEstimate: false));
        Check(controller.AccountSettings("github.com:1").ShowPeriodEstimate, "Invalid account saves preserve the estimate preference.");
        await controller.SaveSettingsAsync(controller.Settings with
        {
            TrayStyle = TrayIconStyle.Percentage, TrayMode = TrayDisplayMode.PerAccount, ExcludedTrayAccounts = ["github.com:1"]
        });
        Check(dashboard?.Tray?.Icons.Count == 1 && dashboard.Tray.RollUp.Percent is null, "Excluding all accounts retains unavailable roll-up.");
        Check((await new JsonStore(root).LoadSettingsAsync()).Value.Accounts[0].ExcludeFromTray, "Tray exclusions persist.");
        Check(controller.AccountSettings("github.com:1").ShowPeriodEstimate, "Global settings saves preserve account estimates.");
        await controller.AddAsync("github.com", false, "github.com:1", _ => { }, () => { }, default);
        Check(controller.Settings.ExcludedTrayAccounts?.SequenceEqual(["github.com:1"]) == true, "Reconnect preserves tray exclusion.");
        Check(controller.AccountSettings("github.com:1").ShowPeriodEstimate &&
            (await new JsonStore(root).LoadSettingsAsync()).Value.Accounts[0].ShowPeriodEstimate,
            "Reconnect preserves and persists account estimate opt-in.");
        await Reject<AppOperationException>(() => controller.AddAsync("github.com", false, null, _ => { }, () => { }, default));
        handler.UserId = "2";
        await Reject<AppOperationException>(() => controller.AddAsync("github.com", false, "github.com:1", _ => { }, () => { }, default));
        Check(credentials.Values["github.com:1"].AccessToken == "synthetic-token-1", "Wrong identity reconnect preserves credential.");
        using (var cancel = new CancellationTokenSource())
            await Reject<OperationCanceledException>(() => controller.AddAsync("github.com", false, null, _ => cancel.Cancel(), () => { }, cancel.Token));
        Check(credentials.Values.Count == 1, "Cancelled sign-in is not saved.");
        await controller.SaveAccountAsync("github.com:1", "Example", "60, 90", 0m);
        Check(controller.AccountSettings("github.com:1") == ("Example", "60, 90", 0m, true),
            "Account saves that omit the estimate argument preserve opt-in.");
        await controller.SaveAccountAsync("github.com:1", "Example", "60, 90", 0m, showPeriodEstimate: false);
        Check(!controller.AccountSettings("github.com:1").ShowPeriodEstimate && dashboard!.Accounts[0].PeriodEstimate is null &&
            !(await new JsonStore(root).LoadSettingsAsync()).Value.Accounts[0].ShowPeriodEstimate,
            "Explicit disable persists and completely removes the estimate.");
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
    BridgeEvent? notificationRequest = null;
    using (var platform = new NativePlatform(request =>
    {
        notificationRequest = request;
        Check(request is { Kind: "platform", Operation: "notification", Key: "github.com:1",
            Title: "Synthetic alert", Message: "Synthetic message" },
            "macOS adapter preserves the existing native notification request shape and text");
    }))
    {
        var pending = platform.NotifyAsync(new("github.com:1", "Synthetic alert", "Synthetic message", 87.5m, 100m));
        platform.Complete(notificationRequest!.Id!, new() { Accepted = true });
        Check(await pending, "macOS notification acceptance still reaches the shared alert ledger.");
    }
    using (var bridge = new BridgeRuntime())
    {
        DashboardView? estimateDashboard = null;
        void ObserveEstimate(BridgeEvent e) { if (e.Dashboard is not null) estimateDashboard = e.Dashboard; }
        var init = new Command { Id = "init", Method = "initialize", Directory = Path.Combine(root, "demo"), Demo = true };
        var encoded = JsonSerializer.Serialize(init, BridgeJsonContext.Default.Command);
        Check(JsonSerializer.Deserialize(encoded, BridgeJsonContext.Default.Command) == init, "Source-generated command round-trip.");
        Check(bridge.Send(init).Error is null, "Bridge accepts initialization.");
        var initialized = await Complete(bridge, "init", ObserveEstimate);
        Check(initialized.Error is null && initialized.Settings?.PollMinutes == 10, "Bridge reports asynchronous initialization.");
        Check(estimateDashboard!.Accounts.All(a => a.PeriodEstimate is null), "Demo also defaults estimates off.");
        bridge.Send(new() { Id = "estimate-default", Method = "account.preferences", Key = "github.com:1" });
        Check((await Complete(bridge, "estimate-default")).Preferences?.ShowPeriodEstimate == false,
            "Bridge preferences expose explicit default-off opt-in.");
        var estimateCommand = new Command { Id = "estimate-on", Method = "account.save", Key = "github.com:1",
            DisplayName = "Personal (demo)", ShowPeriodEstimate = true };
        Check(JsonSerializer.Deserialize(JsonSerializer.Serialize(estimateCommand, BridgeJsonContext.Default.Command),
            BridgeJsonContext.Default.Command) == estimateCommand, "Source-generated command retains the nullable estimate preference.");
        bridge.Send(estimateCommand);
        Check((await Complete(bridge, "estimate-on", ObserveEstimate)).Error is null &&
            estimateDashboard!.Accounts[0].PeriodEstimate is not null && estimateDashboard.Accounts[1].PeriodEstimate is null,
            "Native AOT bridge serializes per-account results without fabricating an aggregate.");
        Check(estimateDashboard!.ConsumptionUsd == 42.75m && estimateDashboard.Tray!.RollUp.Percent == 34.2,
            "Enabling estimates leaves actual consumption and menu-bar allocation unchanged.");
        bridge.Send(new() { Id = "estimate-omit", Method = "account.save", Key = "github.com:1", DisplayName = "Personal (demo)" });
        await Complete(bridge, "estimate-omit", ObserveEstimate);
        Check(estimateDashboard!.Accounts[0].PeriodEstimate is not null, "Omitted bridge argument does not disable an existing estimate.");
        bridge.Send(new() { Id = "estimate-preferences", Method = "account.preferences", Key = "github.com:1" });
        Check((await Complete(bridge, "estimate-preferences")).Preferences?.ShowPeriodEstimate == true,
            "Enabled preference round-trips through generated bridge JSON.");
        bridge.Send(new() { Id = "estimate-off", Method = "account.save", Key = "github.com:1",
            DisplayName = "Personal (demo)", ShowPeriodEstimate = false });
        await Complete(bridge, "estimate-off", ObserveEstimate);
        Check(estimateDashboard!.Accounts.All(a => a.PeriodEstimate is null), "Explicit bridge disable removes the result.");
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
        DashboardView? exampleDashboard = null;
        bridge.Send(new() { Id = "populated-example", Method = "demo.account.add" });
        var example = await Complete(bridge, "populated-example", e =>
        {
            if (e.Dashboard is not null) exampleDashboard = e.Dashboard;
        });
        Check(example.Error is null && exampleDashboard?.Accounts.Count == 3 &&
            exampleDashboard.ConsumptionUsd == 55.25m, "Example account appends to existing sample data.");
        Check(exampleDashboard!.Accounts.Select(a => a.Key).Distinct().Count() == 3 &&
            exampleDashboard.Tray!.Icons.Any(i => i.AccountKey == "github.com:3"),
            "Sample accounts have unique identities and newly added accounts join saved tray settings.");
    }
    string exampleDirectory = Path.Combine(root, "empty-demo-examples");
    using (var bridge = new BridgeRuntime())
    {
        DashboardView? dashboard = null;
        void Observe(BridgeEvent e) { if (e.Dashboard is not null) dashboard = e.Dashboard; }
        bridge.Send(new() { Id = "empty-demo", Method = "initialize", Directory = exampleDirectory, Demo = true, Empty = true });
        Check((await Complete(bridge, "empty-demo", Observe)).Error is null &&
            dashboard?.Accounts.Count == 0 && dashboard.ConsumptionUsd is null, "Empty sample mode has no fabricated zero usage.");
        bridge.Send(new() { Id = "first-example", Method = "demo.account.add" });
        Check((await Complete(bridge, "first-example", Observe)).Error is null &&
            dashboard?.Accounts.Count == 1 && dashboard.ConsumptionUsd == 12.5m,
            "One example populates the empty sample dashboard with exact synthetic consumption.");
        Check(dashboard!.Accounts[0].Name == "Example 1 (demo)" && dashboard.Accounts[0].Percent == 25m &&
            dashboard.Tray!.RollUp.Percent == 25 && dashboard.Tray.RollUp.IncludedAccounts == 1,
            "Example usage and tray allocation are coherent.");
        bridge.Send(new() { Id = "second-example", Method = "demo.account.add" });
        Check((await Complete(bridge, "second-example", Observe)).Error is null &&
            dashboard?.Accounts.Count == 2 && dashboard.ConsumptionUsd == 25m &&
            dashboard.Accounts.Select(a => a.Key).Distinct().Count() == 2,
            "Repeated example additions append unique accounts instead of replacing or duplicating identities.");
        bridge.Send(new() { Id = "refresh-examples", Method = "refresh" });
        Check((await Complete(bridge, "refresh-examples", Observe)).Error is null &&
            dashboard?.Accounts.Count == 2 && dashboard.Tray!.RollUp.Percent == 25,
            "Refresh retains in-memory examples with fresh allocation data.");
        Check(!Directory.Exists(exampleDirectory) || !Directory.EnumerateFiles(exampleDirectory, "*", SearchOption.AllDirectories).Any(),
            "Example additions do not write account data or credentials.");
    }
    using (var bridge = new BridgeRuntime())
    {
        DashboardView? dashboard = null;
        bridge.Send(new() { Id = "restart-examples", Method = "initialize", Directory = exampleDirectory, Demo = true, Empty = true });
        await Complete(bridge, "restart-examples", e => { if (e.Dashboard is not null) dashboard = e.Dashboard; });
        Check(dashboard?.Accounts.Count == 0, "Temporary example accounts do not survive a new isolated session.");
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
        bridge.Send(new() { Id = "forbidden-example", Method = "demo.account.add", Demo = true });
        Check((await Complete(bridge, "forbidden-example")).Error == "Example accounts are only available in demonstration mode.",
            "Normal mode rejects example accounts even when a command spoofs the demo flag.");
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
static async Task<BridgeEvent> Complete(BridgeRuntime bridge, string id, Action<BridgeEvent>? observe = null)
{
    BridgeEvent? result = null;
    await Until(() =>
    {
        var events = bridge.Poll();
        // Exercise generated event serialization used by the Native AOT ABI.
        var json = JsonSerializer.Serialize(events, BridgeJsonContext.Default.BridgeEventArray);
        var decoded = JsonSerializer.Deserialize(json, BridgeJsonContext.Default.BridgeEventArray)!;
        foreach (var e in decoded) observe?.Invoke(e);
        result = decoded.FirstOrDefault(e => e.Kind == "completed" && e.Id == id);
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
    public bool Unlimited { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string json = request.RequestUri!.AbsolutePath switch
        {
            "/login/device/code" => """{"device_code":"synthetic-device","user_code":"SYNTHETIC","verification_uri":"https://github.com/login/device","expires_in":60,"interval":1}""",
            "/login/oauth/access_token" => $$"""{"access_token":"synthetic-token-{{UserId}}","token_type":"bearer","scope":"read:user"}""",
            "/user" => $$"""{"id":{{UserId}},"login":"synthetic-user"}""",
            "/copilot_internal/user" => """{"quota_snapshots":{"premium_interactions":{"token_based_billing":true,"credits_used":2625,"entitlement":2500,"has_quota":true,"unlimited":""" +
                Unlimited.ToString().ToLowerInvariant() + "}}}",
            _ => throw new InvalidOperationException("Unexpected synthetic request.")
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
