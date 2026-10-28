using System.Diagnostics;
using System.Text.Json;
using GHCPSpendTray.Core;
using GHCPSpendTray.Shared;

if (UsageCommands.Handles(args))
{
    var command = UsageCommands.Run(args, "");
    if (command.Output.Length > 0) Console.WriteLine(command.Output);
    if (command.Error is not null) Console.Error.WriteLine(command.Error);
    return command.ExitCode;
}
if (args.FirstOrDefault() == "--prepare-cache")
{
    var state = Fixture(DateTimeOffset.UtcNow);
    if (args.Length > 2)
    {
        var account = state.Accounts[0];
        account = args[2] switch
        {
            "green" => account with { Estimate = account.Estimate! with { EstimatedConsumptionUsd = 45m } },
            "yellow" => account with { Estimate = account.Estimate! with { EstimatedConsumptionUsd = 48m } },
            "red" => account,
            "in_progress" => account with { Refreshing = true },
            "not_connected" => account with { Status = AccountStatus.SignInRequired },
            _ => throw new ArgumentException("Unknown synthetic scenario.")
        };
        state = state with { Accounts = [account] };
    }
    AppUsageStore.Save(args[1], state);
    return 0;
}
int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + label);
    checks++;
}
void Equal<T>(T actual, T expected, string label) => Check(EqualityComparer<T>.Default.Equals(actual, expected), label);
DateTimeOffset now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
string root = Path.Combine(Path.GetTempPath(), "GHCPAppUsage-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var saved = Fixture(now);
    string initializationDirectory = Path.Combine(root, "not-created ' $()");
    string quotedApp = Path.Combine(root, "app ' $()\\GHCPSpendTray.exe");
    foreach (string shell in new[] { "pwsh", "bash", "zsh" })
    {
        var initialization = UsageCommands.Run(["init", shell, "--data-dir", initializationDirectory,
            "--account", "github.com:123"], initializationDirectory, now, quotedApp);
        Equal(initialization.ExitCode, 0, "embedded initialization available");
        string script = initialization.Output;
        if (shell == "pwsh")
        {
            Equal(initialization.Output.TrimEnd().Split('\n').Length, 1, "PowerShell init is one pipeline expression");
            script = DecodePowerShellInitialization(initialization.Output);
        }
        Check(script.Contains(shell == "pwsh" ? "function Initialize-CopilotPrompt" : "initialize_copilot_prompt()",
            StringComparison.Ordinal), "embedded canonical shell function");
        Check(script.TrimEnd().EndsWith(shell == "pwsh" ? "-InstallHook" : "--install-hook", StringComparison.Ordinal),
            "initialization includes hook installation");
        Check(script.Contains(shell == "pwsh" ? "app '' $()" : "app '\"'\"' $()", StringComparison.Ordinal),
            "app path is quoted for the selected shell");
        Check(!Directory.Exists(initializationDirectory), "init does not read or create app data");
    }
    var segmentExport = UsageCommands.Run(["integration", "oh-my-posh", "segment"], initializationDirectory);
    Equal(segmentExport.ExitCode, 0, "embedded segment available without app state");
    using (var segment = JsonDocument.Parse(segmentExport.Output))
        Equal(segment.RootElement.GetProperty("type").GetString(), "text", "canonical text segment exported");
    Equal(UsageCommands.Run(["init", "fish"], root).ExitCode, 2, "unknown shell rejected");
    Equal(UsageCommands.Run(["init", "pwsh", "--account"], root).ExitCode, 2, "missing init option rejected");
    Equal(UsageCommands.Run(["init", "bash", "--account", "bad\nvalue"], root).ExitCode, 2, "control character initialization rejected");
    Equal(UsageCommands.Run(["init", "pwsh", "--data-dir", "relative"], root).ExitCode, 2, "relative initialization directory rejected");
    Equal(UsageCommands.Run(["integration", "oh-my-posh", "unknown"], root).ExitCode, 2, "unknown integration export rejected");
    AppUsageStore.Save(root, saved);
    var writer = Task.Run(() =>
    {
        for (int index = 0; index < 25; index++) AppUsageStore.Save(root, saved);
    });
    for (int index = 0; index < 25; index++)
        Check(AppUsageStore.Read(root) is not null, "concurrent reader sees complete snapshot");
    writer.GetAwaiter().GetResult();
    var result = UsageCommands.Query(AppUsageStore.Read(root), now);
    Equal(result.ConsumptionUsd, 40m, "selected consumption");
    Equal(result.EffectiveBudgetUsd, 50m, "custom budget overrides allocation");
    Equal(result.PercentConsumed, 80m, "custom budget percentage");
    Equal(result.ForecastState, "red", "custom budget forecast");
    Equal(UsageCommands.Prompt(result), "GHCP-SPEND/1\t$40 (~80%)\t$80\tred\tconnected", "prompt TSV");
    Equal(UsageCommands.Query(saved, now.AddMinutes(10)).Status, "fresh", "exact freshness boundary agrees with app");
    foreach (var pair in new[] { (94.99m, "green"), (95m, "yellow"), (99.99m, "yellow"), (100m, "red"), (101m, "red") })
    {
        var account = saved.Accounts[0] with
        {
            CustomBudgetUsd = 100,
            Estimate = saved.Accounts[0].Estimate! with { EstimatedConsumptionUsd = pair.Item1 }
        };
        Equal(UsageCommands.Query(saved with { Accounts = [account] }, now).ForecastState, pair.Item2, "shared 95/100 forecast bands");
    }
    var excluded = saved.Accounts[0] with
    {
        Key = "github.com:456", UserId = "456", Name = "excluded", Selected = false,
        Observation = saved.Accounts[0].Observation! with { AccountKey = "github.com:456" }
    };
    saved = saved with { Accounts = [saved.Accounts[0], excluded] };
    Equal(UsageCommands.Query(saved, now).SelectedAccounts, 1, "tray selection default");
    Equal(UsageCommands.Query(saved, now, excluded.Key).SelectedAccounts, 1, "account override selects excluded");
    Equal(UsageCommands.Query(saved, now, excluded.Key).Accounts[0].Key, excluded.Key, "immutable key override");
    var excludedRefreshing = saved with { Accounts = [excluded with { Refreshing = true }] };
    Check(!UsageCommands.Query(excludedRefreshing, now).IsRefreshing, "excluded refresh does not affect default command");
    Check(UsageCommands.Query(excludedRefreshing, now, excluded.Key).IsRefreshing, "explicit excluded account can show its refresh");
    Check(UsageCommands.Run(["usage","--account","github.com:999"], root, now).ExitCode != 0, "unknown account error");
    var stale = UsageCommands.Query(saved, now.AddMinutes(11));
    Equal(stale.ConsumptionUsd, null, "stale total unavailable");
    Equal(stale.Accounts[0].ConsumptionUsd, 40m, "JSON retains explicit last-known dollars");
    Equal(stale.Accounts[0].Status, "stale", "JSON identifies stale");
    Equal(stale.Accounts[0].PercentConsumed, null, "stale percentage unavailable");
    Equal(stale.EstimatedConsumptionUsd, null, "stale forecast unavailable");
    Equal(UsageCommands.Query(saved, now.AddSeconds(-1)).Status, "unavailable", "future publication unavailable");
    var monthEnd = saved with { PublishedAtUtc = new(2026,10,31,23,59,0,TimeSpan.Zero), Accounts = [
        saved.Accounts[0] with { Observation = saved.Accounts[0].Observation! with
        { FetchedAtUtc = new(2026,10,31,23,59,0,TimeSpan.Zero) } }] };
    Equal(UsageCommands.Query(monthEnd, new(2026,11,1,0,0,0,TimeSpan.Zero)).Accounts[0].Status, "expired", "billing rollover");
    var unlimited = saved.Accounts[0] with
    {
        Observation = saved.Accounts[0].Observation! with { Unlimited = true, PercentConsumed = null }
    };
    Equal(UsageCommands.Query(saved with { Accounts = [unlimited] }, now).PercentConsumed, 80m, "custom budget applies to unlimited");
    unlimited = unlimited with { CustomBudgetUsd = null };
    var noBudget = UsageCommands.Query(saved with { Accounts = [unlimited] }, now);
    Equal(noBudget.ConsumptionUsd, 40m, "unlimited dollars retained");
    Equal(noBudget.PercentConsumed, null, "unlimited without budget no percentage");
    Equal(noBudget.ForecastState, "unknown", "unlimited without budget no risk color");
    var failed = saved.Accounts[0] with { Status = AccountStatus.SignInRequired };
    var partial = UsageCommands.Query(saved with { Accounts = [saved.Accounts[0], excluded with { Selected = true, Status = AccountStatus.NetworkError }] }, now);
    Check(!partial.IsComplete && partial.Status == "partial", "partial selection explicit");
    Check(UsageCommands.Prompt(partial).Contains("!$40", StringComparison.Ordinal), "partial prompt marker");
    Equal(partial.EstimatedConsumptionUsd, null, "partial forecast never invented");
    Equal(UsageCommands.Query(saved with { Accounts = [failed] }, now).ConsumptionUsd, null, "auth failure not zero");
    var off = saved.Accounts[0] with { Estimate = null };
    Equal(UsageCommands.Query(saved with { Accounts = [off] }, now).ForecastState, "unknown", "estimate setting off respected");
    var oldSource = saved.Accounts[0] with
    { Estimate = saved.Accounts[0].Estimate! with { ObservedAtUtc = now.AddMinutes(-11) } };
    Equal(UsageCommands.Query(saved with { Accounts = [oldSource] }, now).EstimatedConsumptionUsd, null, "saved stale-source forecast unavailable");
    var invalidPeriod = saved.Accounts[0] with
    { Estimate = saved.Accounts[0].Estimate! with { ResetAtUtc = now.AddDays(1) } };
    Equal(UsageCommands.Query(saved with { Accounts = [invalidPeriod] }, now).EstimatedConsumptionUsd, null, "saved non-calendar forecast unavailable");
    var daily = saved.Accounts[0] with
    { Estimate = saved.Accounts[0].Estimate! with { EstimatedConsumptionUsd = 47m, ReportedDayCount = 7 } };
    Equal(UsageCommands.Query(saved with { Accounts = [daily] }, now).EstimatedConsumptionUsd, 47m, "app daily-derived estimate is not replaced");
    Equal(UsageCommands.Query(null, now).Status, "unavailable", "no app snapshot unavailable");
    var empty = Fixture(now) with { Accounts = [] };
    Equal(UsageCommands.Query(empty, now).Status, "no-accounts", "no selected accounts explicit");
    var refreshing = Fixture(now) with { Accounts = [Fixture(now).Accounts[0] with { Refreshing = true }] };
    Check(UsageCommands.Prompt(UsageCommands.Query(refreshing, now)).EndsWith("\tin_progress", StringComparison.Ordinal), "recent refresh state");
    Check(!UsageCommands.Prompt(UsageCommands.Query(refreshing, now.AddSeconds(96))).EndsWith("\tin_progress", StringComparison.Ordinal), "stopped worker marker expires");
    string before = File.ReadAllText(Path.Combine(root, AppUsageStore.FileName));
    var json = UsageCommands.Run(["usage","--format","json"], root, now);
    Equal(json.ExitCode, 0, "JSON command success");
    using (var document = JsonDocument.Parse(json.Output))
    {
        Equal(document.RootElement.GetProperty("accounts")[0].GetProperty("customBudgetUsd").GetDecimal(), 50m, "numeric JSON budget");
        Equal(document.RootElement.GetProperty("version").GetInt32(), 1, "versioned JSON output");
    }
    Equal(File.ReadAllText(Path.Combine(root, AppUsageStore.FileName)), before, "query never writes state");
    Equal(UsageCommands.Run(["usage","--format","shell"], root, now).ExitCode, 2, "invalid format error");
    Equal(UsageCommands.Run(["usage","--data-dir","relative"], root, now).ExitCode, 2, "relative path rejected");
    var missing = Path.Combine(root, "does-not-exist");
    Equal(UsageCommands.Run(["usage"], missing, now).ExitCode, 0, "missing saved state is queryable");
    Check(!Directory.Exists(missing), "query does not create directory");
    File.WriteAllText(Path.Combine(root, AppUsageStore.FileName), "{");
    Equal(UsageCommands.Run(["usage"], root, now).ExitCode, 1, "corrupt state error");
    AppUsageStore.Save(root, Fixture(now));
    File.WriteAllText(Path.Combine(root, AppUsageStore.FileName), before.Replace("\"version\":1,", "", StringComparison.Ordinal));
    Equal(UsageCommands.Run(["usage"], root, now).ExitCode, 1, "missing version error");
    AppUsageStore.Save(root, Fixture(DateTimeOffset.UtcNow));
    if (args.Length >= 2 && args[0] == "--app")
    {
        string executable = Path.GetFullPath(args[1]);
        var appInit = Execute(executable, ["init", "pwsh", "--data-dir", root]);
        Equal(appInit.Code, 0, "actual app initialization exit");
        Check(DecodePowerShellInitialization(appInit.Output).Contains("Initialize-CopilotPrompt -AppExecutable", StringComparison.Ordinal), "actual app embedded PowerShell init");
        var appSegment = Execute(executable, ["integration", "oh-my-posh", "segment"]);
        Equal(appSegment.Code, 0, "actual app embedded segment exit");
        Equal(appSegment.Output.Trim(), segmentExport.Output.Trim(), "actual executable ships canonical segment");
        Equal(Execute(executable, ["init", "unknown"]).Code, 2, "actual executable rejects unsupported init shell");
        var samples = new List<double>();
        for (int index = 0; index < 15; index++)
        {
            var timer = Stopwatch.StartNew();
            var child = Execute(executable, ["prompt", "--data-dir", root]);
            Equal(child.Code, 0, "actual app CLI exit");
            Check(child.Output.Trim().StartsWith("GHCP-SPEND/1\t$40 (~80%)\t", StringComparison.Ordinal), "actual app piped TSV");
            samples.Add(timer.Elapsed.TotalMilliseconds);
        }
        var childJson = Execute(executable, ["usage", "--data-dir", root, "--format", "json"]);
        Equal(childJson.Code, 0, "actual app JSON exit");
        using var document = JsonDocument.Parse(childJson.Output);
        Equal(document.RootElement.GetProperty("effectiveBudgetUsd").GetDecimal(), 50m, "actual app JSON budget");
        var invalid = Execute(executable, ["usage", "--data-dir", root, "--account", "missing"]);
        Equal(invalid.Code, 2, "actual app propagates usage exit codes");
        Check(invalid.Error.Length > 0 && invalid.Output.Length == 0, "actual app diagnostics on stderr");
        samples.Sort();
        Console.WriteLine($"App cached query median {samples[7]:F2} ms, p95 {samples[14]:F2} ms; no UI/network.");
    }
    Console.WriteLine($"PASS: {checks} app usage/query assertions; synthetic saved state only.");
    return 0;
}
finally
{
    foreach (string file in Directory.EnumerateFiles(root)) File.Delete(file);
    Directory.Delete(root);
}

static SavedUsage Fixture(DateTimeOffset now)
{
    var reset = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
    var observation = new UsageSnapshot
    {
        AccountKey = "github.com:123", FetchedAtUtc = now, SourceTimestampUtc = now,
        PeriodId = BillingPeriods.Resolve(now, reset), ResetAtUtc = reset,
        CreditsUsed = 4000, Entitlement = 10000, ConsumptionUsd = 40,
        AllocationUsd = 100, PercentConsumed = 40
    };
    return new SavedUsage
    {
        PublishedAtUtc = now, PollMinutes = 10, Accounts = [new()
        {
            Key = "github.com:123", Host = "github.com", UserId = "123", Name = "Synthetic account",
            Selected = true, CustomBudgetUsd = 50, Status = AccountStatus.Fresh, Refreshing = false,
            Observation = observation, Estimate = new(EstimatedConsumptionUsd: 80, ObservedAtUtc: now,
                ResetAtUtc: reset, PeriodStartUtc: reset.AddMonths(-1))
        }]
    };
}
static (int Code, string Output, string Error) Execute(string executable, string[] arguments)
{
    var info = new ProcessStartInfo(executable)
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not launch app CLI.");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(10000))
    {
        process.Kill(entireProcessTree: true);
        throw new TimeoutException("App CLI started UI or did not exit.");
    }

    return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
}

static string DecodePowerShellInitialization(string output)
{
    const string prefix = "Invoke-Expression ([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('";
    const string suffix = "')))";
    string expression = output.TrimEnd();
    if (!expression.StartsWith(prefix, StringComparison.Ordinal) || !expression.EndsWith(suffix, StringComparison.Ordinal))
        throw new InvalidDataException("Unexpected PowerShell initialization expression.");
    return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(expression[prefix.Length..^suffix.Length]));
}
