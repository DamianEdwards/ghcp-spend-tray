using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using GHCPSpendTray.Core;
using GHCPSpendTray.Prompt;

int assertions = 0;
if (args.FirstOrDefault() == "auth")
{
    if (Environment.GetEnvironmentVariable("GHCP_TEST_GH") == "timeout") Thread.Sleep(10000);
    return 1;
}
if (args.FirstOrDefault() == "--hold-gate")
{
    using RefreshGate? gate = RefreshGate.TryAcquire(args[1], args[2]);
    if (gate is null) return 2;
    File.WriteAllText(args[3], "ready");
    Thread.Sleep(30000);
    return 0;
}
if (args.FirstOrDefault() == "--prepare-cache")
{
    var prepared = new PromptOptions { CacheDirectory = args[1], GhExecutable = args[2] }.Validate();
    DateTimeOffset instant = DateTimeOffset.UtcNow;
    string preparedKey = CredentialContext.Key(prepared);
    var observation = CopilotUsageClient.ParseJson(
        """{"quota_snapshots":{"premium_interactions":{"token_based_billing":true,"has_quota":true,"unlimited":false,"credits_used":4000,"entitlement":10000}}}""",
        "github.com:123", instant);
    new PromptStore(prepared, preparedKey).Save(new()
    {
        ContextKey = preparedKey, Hostname = "github.com", AccountId = "123", Status = AccountStatus.Fresh,
        Snapshot = observation, AttemptedAtUtc = instant, NextAttemptUtc = instant.AddHours(1)
    });
    return 0;
}
string root = Path.Combine(Path.GetTempPath(), "GHCPPrompt-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
DateTimeOffset now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
var clock = new Clock(now);
string key = new('a', 64);
var options = new PromptOptions { CacheDirectory = root }.Validate();
var store = new PromptStore(options, key);
var service = new PromptService(options, clock, _ => key);
string quota = """{"quota_snapshots":{"premium_interactions":{"token_based_billing":true,"has_quota":true,"unlimited":false,"credits_used":4000,"entitlement":10000}},"quota_reset_date":"2026-11-01"}""";
var snapshot = CopilotUsageClient.ParseJson(quota, "github.com:123", now);
PromptCache Sample(decimal projected = 80)
{
    decimal used = projected / 2, credits = used * 100;
    return new()
    {
        ContextKey = key, Hostname = "github.com", AccountId = "123", Status = AccountStatus.Fresh,
        AttemptedAtUtc = now, NextAttemptUtc = now.AddHours(1),
        Snapshot = snapshot with
        {
            CreditsUsed = credits, ConsumptionUsd = used,
            PercentConsumed = CreditPolicy.Percentage(credits, snapshot.Entitlement, false)
        }
    };
}
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + label);
    assertions++;
}
void Equal<T>(T actual, T expected, string label) => Check(EqualityComparer<T>.Default.Equals(actual, expected), label);
void Throws(Action action, string label)
{
    bool thrown = false;
    try { action(); } catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or JsonException or OverflowException or ServiceException) { thrown = true; }
    Check(thrown, label);
}
try
{
    foreach (var sample in new[] { (0m, "$0"), (.49m, "$0"), (.5m, "$1"), (246.55m, "$247"),
        (999.5m, "$1K"), (1000m, "$1K"), (1050m, "$1.1K"), (4426m, "$4.4K"), (10000m, "$10K") })
        Equal(PromptState.FormatUsd(sample.Item1), sample.Item2, "compact dollars");
    foreach (var sample in new[] { (79.99m, "green"), (80m, "green"), (80.01m, "yellow"), (100m, "yellow"), (100.01m, "red") })
        Equal(PromptState.Create(Sample(sample.Item1), now, options.Freshness, false).ForecastState, sample.Item2, "unrounded threshold");
    Equal(PromptState.Create(Sample(), now, options.Freshness, false).Spend, "$40 (~40%)", "spend-first protocol");
    Equal(PromptState.Create(Sample(), now.AddHours(1), options.Freshness, false).Spend, "unavailable", "exact expiry");
    Equal(PromptState.Create(Sample(), now.AddSeconds(-1), options.Freshness, false).Spend, "unavailable", "future fetch");
    Equal(PromptState.Create(null, now, options.Freshness, true).ConnectionState, "in_progress", "initial fetch");
    var unknown = Sample() with { Snapshot = snapshot with { Entitlement = null, AllocationUsd = null, PercentConsumed = null } };
    Equal(PromptState.Create(unknown, now, options.Freshness, false).Spend, "$40", "unknown allocation retains dollars");
    Equal(PromptState.Create(unknown, now, options.Freshness, false).Forecast, "?", "unknown allocation no estimate");
    var unlimited = Sample() with { Snapshot = snapshot with { Unlimited = true, PercentConsumed = null } };
    Equal(PromptState.Create(unlimited, now, options.Freshness, false).ForecastState, "unknown", "unlimited no forecast");
    var staleSource = Sample() with { Snapshot = snapshot with { SourceTimestampUtc = now.AddMinutes(-61) } };
    Equal(PromptState.Create(staleSource, now, options.Freshness, false).Forecast, "?", "stale source");
    var nonCalendar = Sample() with { Snapshot = snapshot with
    {
        ResetAtUtc = now.AddDays(2), PeriodId = BillingPeriods.Resolve(now, now.AddDays(2))
    } };
    Equal(PromptState.Create(nonCalendar, now, options.Freshness, false).Forecast, "?", "non-calendar period");
    DateTimeOffset firstDay = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    var early = Sample() with { Snapshot = snapshot with { FetchedAtUtc = firstDay } };
    Equal(PromptState.Create(early, firstDay, options.Freshness, false).Forecast, "?", "first 24 hours");
    var rollover = Sample() with { Snapshot = snapshot with { FetchedAtUtc = new(2026, 10, 31, 23, 59, 0, TimeSpan.Zero) } };
    Equal(PromptState.Create(rollover, new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), options.Freshness, false).Spend, "unavailable", "billing rollover");
    Throws(() => CopilotUsageClient.ParseJson(quota.Replace("4000", "-1"), "github.com:123", now), "negative quota rejected");
    bool unsupported = false;
    try { CopilotUsageClient.ParseJson(quota.Replace("\"token_based_billing\":true", "\"token_based_billing\":false"), "github.com:123", now); }
    catch (ServiceException error) { unsupported = error.Status == AccountStatus.Unsupported; }
    Check(unsupported, "request billing unsupported");
    Throws(() => new PromptOptions { CacheDirectory = Path.GetPathRoot(root)! }.Validate(), "dedicated directory");
    Throws(() => PromptOptions.Parse(["prompt", "--refresh-minutes", "0"]), "interval validation");
    Throws(() => PromptOptions.Parse(["prompt", "--hostname", "https://evil.test/path"]), "host validation");
    Throws(() => (PromptState.Unavailable with { Spend = "$(touch sentinel)\nextra" }).ToProtocol(), "data protocol controls rejected");

    var environment = new Dictionary<string, string?> { ["GH_CONFIG_DIR"] = root, ["GH_TOKEN"] = "synthetic-a" };
    string contextA = CredentialContext.Key(options, name => environment.GetValueOrDefault(name));
    environment["GH_TOKEN"] = "synthetic-b";
    Check(CredentialContext.Key(options, name => environment.GetValueOrDefault(name)) != contextA, "credential isolation");
    Check(CredentialContext.Key(options with { Hostname = "tenant.ghe.com" }, name => environment.GetValueOrDefault(name)) != contextA, "host isolation");
    environment["GH_TOKEN"] = null;
    environment["GITHUB_TOKEN"] = "cloud";
    environment["GH_ENTERPRISE_TOKEN"] = "server";
    Equal(CredentialContext.EnvironmentToken("tenant.ghe.com", name => environment.GetValueOrDefault(name)), "cloud", "cloud precedence");
    Equal(CredentialContext.EnvironmentToken("enterprise.example", name => environment.GetValueOrDefault(name)), "server", "server precedence");
    string metadataKey = CredentialContext.Key(options, name => environment.GetValueOrDefault(name));
    File.WriteAllText(Path.Combine(root, "hosts.yml"), "synthetic metadata");
    Check(CredentialContext.Key(options, name => environment.GetValueOrDefault(name)) != metadataKey, "config metadata isolation");
    store.Save(Sample());
    Equal(store.Read()!.Snapshot!.ConsumptionUsd, 40m, "validated cache roundtrip");
    Throws(() => store.Save(Sample() with { Hostname = "wrong.test" }), "cache host verification");
    Throws(() => store.Save(Sample() with { AccountId = "456" }), "cache account verification");
    File.WriteAllText(store.CachePath, "{");
    Throws(() => store.Read(), "truncated cache rejected");
    string missingVersion = JsonSerializer.Serialize(Sample(), PromptJsonContext.Default.PromptCache)
        .Replace("\"version\":1,", "", StringComparison.Ordinal);
    File.WriteAllText(store.CachePath, missingVersion);
    Throws(() => store.Read(), "missing native schema version rejected");
    File.WriteAllText(store.CachePath, new string('x', 65537));
    Throws(() => store.Read(), "oversized cache rejected");
    store.Save(Sample());
    int launches = 0;
    Equal(service.ReadPrompt(_ => { }, (_, _) => launches++).Spend, "$40 (~40%)", "cache-only prompt");
    Equal(launches, 0, "fresh prompt has no worker/discovery/HTTP");
    clock.Now = now.AddHours(1);
    Equal(service.ReadPrompt(_ => { }, (_, _) => launches++).ConnectionState, "in_progress", "due prompt schedules");
    Equal(launches, 1, "single scheduled worker");
    service.ReadPrompt(_ => { }, (_, _) => launches++);
    Equal(launches, 1, "active lease excludes second scheduling");
    var lease = store.ReadLease()!;
    Check(lease.IsActive(key, clock.Now), "lease active");
    Check(!lease.IsActive(key, clock.Now + options.WorkerLifetime), "abandoned lease expires");
    Check(!lease.IsActive(new string('b', 64), clock.Now), "lease scope");
    store.ClearLease(lease.Nonce);
    clock.Now = now;
    File.Delete(store.CachePath);
    string nonce = Guid.NewGuid().ToString("N");
    store.SaveLease(new(key, nonce, now, now + options.WorkerLifetime));
    int httpCalls = 0, discoveries = 0;
    service.Refresh(key, nonce, (_, _) => { discoveries++; return Task.FromResult(new TokenSet { AccessToken = "synthetic-token" }); },
        () => new HttpClient(new Handler(request =>
        {
            httpCalls++;
            Equal(request.Headers.Authorization!.Parameter, "synthetic-token", "captured credential boundary");
            return new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath == "/user"
                ? """{"id":123,"login":"synthetic-account"}""" : quota) };
        })));
    Equal(httpCalls, 2, "identity and quota through .NET HTTP");
    Equal(discoveries, 1, "background-only discovery");
    Equal(store.Read()!.Status, AccountStatus.Fresh, "refresh cache fresh");
    Check(store.ReadLease() is null, "lease released");
    Check(!File.ReadAllText(store.CachePath).Contains("synthetic-token", StringComparison.Ordinal), "token never persisted");
    Check(!File.ReadAllText(store.CachePath).Contains("synthetic-account", StringComparison.Ordinal), "login never persisted");
    clock.Now = now.AddHours(1);
    store.SaveLease(new(key, nonce, clock.Now, clock.Now + options.WorkerLifetime));
    service.Refresh(key, nonce, (_, _) => Task.FromResult(new TokenSet { AccessToken = "synthetic-token" }),
        () => new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("SYNTHETIC-PRIVATE-BODY") };
            response.Headers.RetryAfter = new(System.TimeSpan.FromHours(2));
            return response;
        })));
    var failed = store.Read()!;
    Equal(failed.Diagnostic, PromptDiagnostic.RateLimited, "rate-limit diagnostic");
    Equal(failed.NextAttemptUtc, clock.Now.AddHours(2), "server deadline persisted");
    Check(failed.Snapshot is null, "failure not stale/zero observation");
    Check(!File.ReadAllText(store.CachePath).Contains("SYNTHETIC-PRIVATE", StringComparison.Ordinal), "raw error body not persisted");
    launches = 0;
    service.ReadPrompt(_ => { }, (_, _) => launches++);
    service.ReadPrompt(_ => { }, (_, _) => launches++);
    Equal(launches, 0, "cooldown no worker");
    store.Report(PromptDiagnostic.None, _ => { });
    int warnings = 0;
    store.Report(PromptDiagnostic.Authentication, _ => warnings++);
    store.Report(PromptDiagnostic.Authentication, _ => warnings++);
    Equal(warnings, 1, "safe diagnostic change reporting");
    store.Save(Sample());
    var writer = Task.Run(() =>
    {
        for (int index = 0; index < 50; index++) store.Save(Sample(index % 2 == 0 ? 80 : 100));
    });
    for (int index = 0; index < 50; index++)
    {
        var observation = store.Read()!;
        Check(observation.Snapshot is not null && observation.Status == AccountStatus.Fresh, "atomic publication cannot expose partial JSON");
    }
    writer.GetAwaiter().GetResult();
    File.Delete(store.CachePath);
    store.SaveLease(new(key, nonce, clock.Now, clock.Now + options.WorkerLifetime));
    string activeKey = key;
    new PromptService(options, clock, _ => activeKey).Refresh(key, nonce,
        (_, _) => Task.FromResult(new TokenSet { AccessToken = "synthetic-token" }),
        () => new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/user") activeKey = new string('b', 64);
            return new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.AbsolutePath == "/user"
                ? """{"id":123,"login":"synthetic-account"}""" : quota) };
        })));
    Equal(store.Read()!.Diagnostic, PromptDiagnostic.CredentialsChanged, "changed credential context rejects successful HTTP result");
    Check(store.Read()!.Snapshot is null, "changed context publishes no consumption");

    if (args.Length >= 2 && args[0] == "--helper")
    {
        string helper = Path.GetFullPath(args[1]);
        var childEnvironment = new Dictionary<string, string?>
        {
            ["GH_CONFIG_DIR"] = root, ["GH_TOKEN"] = null, ["GITHUB_TOKEN"] = null,
            ["GH_ENTERPRISE_TOKEN"] = null, ["GITHUB_ENTERPRISE_TOKEN"] = null
        };
        string rootProcess = Environment.ProcessPath!;
        var nativeOptions = options with { GhExecutable = rootProcess, RequestTimeoutSeconds = 1 };
        string nativeKey = CredentialContext.Key(nativeOptions, name => childEnvironment.GetValueOrDefault(name));
        var nativeStore = new PromptStore(nativeOptions, nativeKey);
        DateTimeOffset current = DateTimeOffset.UtcNow;
        var currentSnapshot = snapshot with
        {
            FetchedAtUtc = current, ResetAtUtc = null, PeriodId = BillingPeriods.Resolve(current, null)
        };
        nativeStore.Save(new()
        {
            ContextKey = nativeKey, Hostname = nativeOptions.Hostname, AccountId = "123", Status = AccountStatus.Fresh,
            AttemptedAtUtc = current, NextAttemptUtc = current.AddHours(1), Snapshot = currentSnapshot
        });
        var first = Stopwatch.StartNew();
        var data = Run(helper, ["prompt", .. nativeOptions.Arguments()], childEnvironment);
        first.Stop();
        Equal(data.ExitCode, 0, "published cache-only exit");
        Check(data.Output.StartsWith("GHCP-SPEND/1\t$40 (~40%)\t", StringComparison.Ordinal), "published snapshot protocol");
        Check(nativeStore.ReadLease() is null, "published cached process launches no gh");
        foreach (string state in new[] { "green", "yellow", "red", "in_progress", "not_connected" })
            Equal(Run(helper, ["demo", state], childEnvironment).Output.Trim(), PromptState.Demo(state).ToProtocol(), "published synthetic demo");
        string ready = Path.Combine(root, "holder-ready");
        using (var holder = Start(rootProcess, ["--hold-gate", root, nativeKey, ready], childEnvironment))
        {
            WaitUntil(() => File.Exists(ready), "mutex holder readiness");
            var due = nativeStore.Read()! with { NextAttemptUtc = current.AddSeconds(-1), AttemptedAtUtc = current.AddMinutes(-1) };
            nativeStore.Save(due);
            var locked = Run(helper, ["prompt", .. nativeOptions.Arguments()], childEnvironment);
            Check(locked.Output.Trim().EndsWith("\tin_progress", StringComparison.Ordinal), "portable cross-process exclusion");
            Check(nativeStore.ReadLease() is null, "held native mutex must not schedule another worker");
            Equal(nativeStore.Read()!.Status, AccountStatus.Fresh, "held native mutex cannot change observation");
            holder.Kill(entireProcessTree: true);
            holder.WaitForExit();
        }
        using (RefreshGate? recovered = nativeStore.TryLock())
            Check(recovered is not null, "abandoned native mutex recovers");
        nativeStore.SaveLease(new(nativeKey, nonce, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(-1)));
        var launched = Run(helper, ["prompt", .. nativeOptions.Arguments()], childEnvironment);
        Check(launched.Output.Contains("\tin_progress", StringComparison.Ordinal),
            "published process schedules bounded worker: " + launched.Output.Trim() + " / " + launched.Error.Trim());
        WaitUntil(() => nativeStore.Read()?.Status != AccountStatus.Fresh, "published credential failure cache");
        Equal(nativeStore.Read()!.Diagnostic, PromptDiagnostic.Authentication, "native gh exit failure, no HTTP");
        int errorReports = 0;
        nativeStore.Report(PromptDiagnostic.Authentication, _ => errorReports++);
        var nativeFailure = Run(helper, ["prompt", .. nativeOptions.Arguments()], childEnvironment);
        Check(nativeFailure.Output.Trim().EndsWith("\tnot_connected", StringComparison.Ordinal), "published failed state");
        Check(nativeStore.ReadLease() is null, "published failure cooldown has no lease");
        nativeStore.Save(nativeStore.Read()! with
        {
            AttemptedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(-1)
        });
        childEnvironment["GHCP_TEST_GH"] = "timeout";
        var timeoutLaunch = Run(helper, ["prompt", .. nativeOptions.Arguments()], childEnvironment);
        Check(timeoutLaunch.Output.Contains("\tin_progress", StringComparison.Ordinal), "credential timeout runs only in background");
        WaitUntil(() => nativeStore.Read()?.Diagnostic == PromptDiagnostic.Timeout, "native gh timeout");
        Equal(nativeStore.Read()!.Diagnostic, PromptDiagnostic.Timeout, "bounded credential-discovery timeout is persisted");
        Check(nativeStore.ReadLease() is null, "timed-out worker releases marker");
        childEnvironment.Remove("GHCP_TEST_GH");

        var samples = new List<double>();
        nativeStore.Save(new()
        {
            ContextKey = nativeKey, Hostname = nativeOptions.Hostname, AccountId = "123", Status = AccountStatus.Fresh,
            AttemptedAtUtc = current, NextAttemptUtc = current.AddHours(1), Snapshot = currentSnapshot
        });
        for (int index = 0; index < 30; index++)
        {
            var timer = Stopwatch.StartNew();
            Run(helper, ["prompt", .. nativeOptions.Arguments()], childEnvironment);
            samples.Add(timer.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        Console.WriteLine($"Native process: first cached read {first.Elapsed.TotalMilliseconds:F3} ms; warm median {samples[14]:F3} ms, p95 {samples[28]:F3} ms (30 processes, rendering excluded).");
    }
    Console.WriteLine($"PASS: {assertions} shared native prompt assertions; synthetic HTTP/credentials only.");
    return 0;
}
finally
{
    foreach (string file in Directory.EnumerateFiles(root)) File.Delete(file);
    Directory.Delete(root);
}

static Process Start(string executable, IEnumerable<string> arguments, Dictionary<string, string?> environment)
{
    var info = new ProcessStartInfo(executable)
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
    };
    foreach (string argument in arguments) info.ArgumentList.Add(argument);
    foreach ((string name, string? value) in environment)
    {
        if (value is null) info.Environment.Remove(name);
        else info.Environment[name] = value;
    }
    foreach (string name in new[] { "GH_DEBUG", "DEBUG" }) info.Environment.Remove(name);
    return Process.Start(info) ?? throw new InvalidOperationException("Could not launch synthetic child.");
}
static (int ExitCode, string Output, string Error) Run(string executable, IEnumerable<string> arguments,
    Dictionary<string, string?> environment)
{
    using var process = Start(executable, arguments, environment);
    Task<string> output = process.StandardOutput.ReadToEndAsync();
    Task<string> error = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(15000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("Synthetic helper timed out."); }
    return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
}
static void WaitUntil(Func<bool> condition, string label)
{
    var timer = Stopwatch.StartNew();
    while (!condition())
    {
        if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(label);
        Thread.Sleep(20);
    }
}
sealed class Clock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(response(request));
}
