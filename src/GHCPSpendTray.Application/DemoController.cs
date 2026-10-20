using System.Globalization;
using GHCPSpendTray.Core;
using Microsoft.Extensions.Logging;

namespace GHCPSpendTray.Shared;

public sealed class DemoController(string directory, bool empty = false, TimeProvider? timeProvider = null,
    bool unlimited = false, TelemetryConfiguration? telemetry = null, bool usageStates = false) : IApplicationController
{
    private readonly AppTelemetry? _telemetry = telemetry is null ? null :
        new(telemetry with { TrackedAllowed = telemetry.TrackedAllowed && telemetry.SyntheticVerification },
            message => System.Diagnostics.Trace.TraceWarning(message));
    private readonly JsonStore? _verificationStore = telemetry?.SyntheticVerification == true
        ? new(Path.Combine(directory, "telemetry-verification")) : null;
    private Task? _verificationInitialization;
    private AppSettings _verificationSettings = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly List<AccountView> _examples = usageStates
        ? UsageStateExamples((timeProvider ?? TimeProvider.System).GetUtcNow()) : [];
    private readonly Dictionary<string, (string DisplayName, string Thresholds, decimal? SpendIncrementUsd,
        bool ShowPeriodEstimate)> _preferences = [];
    private readonly Dictionary<string, decimal?> _budgets = [];
    public string DataDirectory => directory;
    public bool Portable => true;
    public event Action<DashboardView>? Changed;
    public SettingsView Settings { get; private set; } = new(10, "50, 80, 100", true, false);
    public Task InitializeAsync() => RefreshAsync();
    private Task InitializeVerificationAsync() => _verificationInitialization ??= LoadVerificationAsync();
    private async Task LoadVerificationAsync()
    {
        if (_verificationStore is null) return;
        var loaded = await _verificationStore.LoadSettingsAsync();
        if (loaded.Diagnostics.Length > 0)
            throw new AppOperationException(string.Join(" ", loaded.Diagnostics));
        _verificationSettings = loaded.Value;
        if (_verificationSettings.TelemetryId is null || _verificationSettings.TelemetryId == Guid.Empty)
        {
            _verificationSettings = _verificationSettings with { TelemetryId = Guid.NewGuid() };
            await _verificationStore.SaveSettingsAsync(_verificationSettings);
        }
        Settings = Settings with { TrackedTelemetryEnabled = _verificationSettings.TrackedTelemetryEnabled };
        _telemetry?.Configure(Settings.TrackedTelemetryEnabled, _verificationSettings.TelemetryId.Value);
        _telemetry?.Started(usageStates ? _examples.Count : empty ? 0 : unlimited ? 1 : 2, "unknown");
    }
    public async Task RefreshAsync(string? accountKey = null)
    {
        await InitializeVerificationAsync();
        using var activity = AppInstrumentation.Activities.StartActivity("demo.refresh");
        _telemetry?.Logger.LogInformation("Refreshing synthetic demo data");
        var now = _time.GetUtcNow();
        if (empty && _examples.Count == 0)
        {
            Changed?.Invoke(new("No accounts", "Synthetic demonstration only.", "GHCPSpendTray DEMO | No accounts", [],
                Tray: TrayUsage.Create(new() { TrayStyle = Settings.TrayStyle, TrayMode = Settings.TrayMode }, [], now)));
            return;
        }
        List<AccountView> views = [];
        if (!empty && !usageStates && unlimited)
            views.Add(new("github.com:1", "Unlimited (demo)", "demo-unlimited", "github.com",
                new(2625m, 26.25m, null, null, true, now, null, now.AddHours(1), "synthetic", true, null),
                null, 26.25m, null, "Fresh", now));
        else if (!empty && !usageStates) views.AddRange([
            new("github.com:1", "Personal (demo)", "demo-user", "github.com",
                new(2625m, 26.25m, 25m, 105m, false, now, null, now.AddHours(1), "synthetic", true, null), 105m,
                26.25m, 25m, "Fresh", now),
            new("example.ghe.com:2", "Work (demo)", "demo-work", "example.ghe.com",
                new(1650m, 16.5m, 100m, 16.5m, false, now, null, now.AddHours(1), "synthetic", true, null),
                16.5m, 16.5m, 100m, "Fresh", now)
        ]);
        views.AddRange(_examples.Select(account => account with
        {
            UpdatedAt = now,
            Details = account.Details with { SourceTimestampUtc = now, NextRefreshUtc = now.AddHours(1) }
        }));
        views = views.Select(account => _preferences.TryGetValue(account.Key, out var preferences)
            ? account with { Name = string.IsNullOrWhiteSpace(preferences.DisplayName) ? account.Name : preferences.DisplayName }
            : account).ToList();
        decimal total = views.Sum(account => account.ConsumptionUsd!.Value);
        string amount = total.ToString("0.00", CultureInfo.InvariantCulture);
        var dashboard = new DashboardView($"DEMO - MTD consumption: ${amount} | {views.Count}/{views.Count} accounts",
            _verificationStore is null
                ? "Synthetic demonstration only. No network, real account data, installation, or startup changes."
                : "Synthetic telemetry verification. Demo events are sent to Azure; no real account data is accessed.",
            $"GHCPSpendTray DEMO | MTD ${amount} | {views.Count} accounts", views, total, true);
        var accounts = dashboard.Accounts.Select((a, i) => new Account
        {
            Host = a.Host, UserId = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Login = a.Login, DisplayName = a.Name,
            ShowPeriodEstimate = _preferences.TryGetValue(a.Key, out var preferences)
                ? preferences.ShowPeriodEstimate : usageStates && a.PeriodEstimate is not null,
            CustomBudgetUsd = _budgets.TryGetValue(a.Key, out var budget) ? budget : a.CustomBudgetUsd,
            ExcludeFromTray = Settings.ExcludedTrayAccounts?.Contains(a.Key, StringComparer.Ordinal) == true
        }).ToArray();
        var states = accounts.Select((a, i) => new AccountState
        {
            Account = a, Status = AccountStatus.Fresh, Snapshot = new()
            {
                AccountKey = a.Key, FetchedAtUtc = now, SourceTimestampUtc = now, PeriodId = BillingPeriods.Resolve(now, null),
                CreditsUsed = dashboard.Accounts[i].Details.CreditsUsed!.Value,
                Unlimited = dashboard.Accounts[i].Details.Unlimited,
                Entitlement = dashboard.Accounts[i].AllocationUsd * 100,
                ConsumptionUsd = dashboard.Accounts[i].ConsumptionUsd!.Value,
                AllocationUsd = dashboard.Accounts[i].AllocationUsd, PercentConsumed = dashboard.Accounts[i].Percent
            }
        }).ToArray();
        Changed?.Invoke(dashboard with
        {
            Accounts = dashboard.Accounts.Select((account, i) => account with
            {
                PeriodEstimate = usageStates && accounts[i].ShowPeriodEstimate && account.PeriodEstimate is { } example
                    ? example with
                    {
                        OverAllocationUsd = example.EstimatedConsumptionUsd is { } projected &&
                            (accounts[i].CustomBudgetUsd ?? account.AllocationUsd) is { } target
                            ? Math.Max(0, projected - target) : null
                    }
                    : PeriodEstimates.Create(states[i], now, TimeSpan.FromMinutes(Settings.PollMinutes)),
                EnterpriseConsumption = i == 0 ? DemoConsumption(now, account.ConsumptionUsd!.Value) : i == 1
                    ? new(true, "Daily usage connected", Connected: true, Bars: [],
                        ChartMessage: "Daily usage is connected, but no daily reports are available for this account yet.",
                        State: EnterpriseSetupState.Connected) : null,
                CustomBudgetUsd = accounts[i].CustomBudgetUsd,
                AllocationUsd = accounts[i].CustomBudgetUsd ?? account.AllocationUsd,
                Percent = UsageBudget.Percentage(accounts[i], states[i].Snapshot!)
            }).ToArray(),
            Tray = TrayUsage.Create(new()
            {
                Accounts = accounts, TrayStyle = Settings.TrayStyle, TrayMode = Settings.TrayMode
            }, states, now),
            TrayStates = states
        });
        foreach (var state in states.Where(s => accountKey is null || s.Account.Key == accountKey))
            _telemetry?.Refreshed(states.Length, "unknown", AccountStatus.Fresh);
    }
    public Task AddExampleAccountAsync()
    {
        var now = _time.GetUtcNow();
        string id = (_examples.Count + (empty || usageStates ? 1 : unlimited ? 2 : 3)).ToString(CultureInfo.InvariantCulture);
        _examples.Add(new($"github.com:{id}", $"Example {id} (demo)", $"demo-example-{id}", "github.com",
            new(1250m, 12.5m, 50m, 25m, false, now, null, now.AddHours(1), "synthetic", true, null),
            25m, 12.5m, 50m, "Fresh", now));
        return RefreshAsync();
    }
    public Task RefreshAccountAsync(string accountKey) => RefreshAsync(accountKey);
    private static List<AccountView> UsageStateExamples(DateTimeOffset now)
    {
        var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var reset = start.AddMonths(1);
        (string Name, decimal Used, decimal? Projected, bool CustomBudget, bool Unlimited, bool Unavailable)[] examples =
        [
            ("Under budget", 40.25m, 75, true, false, false),
            ("Forecast near budget", 65.50m, 97, true, false, false),
            ("Forecast over budget", 70, 125, true, false, false),
            ("Usage near limit", 96, 130, false, false, false),
            ("Usage over budget", 110.50m, 160, true, false, false),
            ("Estimate off", 35, null, false, false, false),
            ("Estimate unavailable", 20, null, true, false, true),
            ("Unlimited", 12.50m, null, false, true, false)
        ];
        return examples.Select((example, index) =>
        {
            string id = (index + 1).ToString(CultureInfo.InvariantCulture);
            decimal? target = example.Unlimited ? null : 100m;
            decimal? percent = target is { } limit ? example.Used / limit * 100 : null;
            PeriodEstimate? estimate = example.Projected is { } projected
                ? new(EstimatedConsumptionUsd: projected, PeriodStartUtc: start, ResetAtUtc: reset, ObservedAtUtc: now)
                : example.Unavailable ? new(UnavailableReason: "Billing period differs from a UTC calendar month.") : null;
            return new AccountView($"github.com:{id}", example.Name + " (sample)", $"demo-state-{id}", "github.com",
                new(example.Used * 100, example.Used, target, percent, example.Unlimited, now, reset,
                    now.AddHours(1), BillingPeriods.Resolve(now, null), true, null),
                percent, example.Used, target, "Fresh", now, PeriodEstimate: estimate,
                CustomBudgetUsd: example.CustomBudget ? 100 : null);
        }).ToList();
    }
    private static EnterpriseConsumptionView DemoConsumption(DateTimeOffset now, decimal consumption = 26.25m)
    {
        var start = new DateOnly(now.Year, now.Month, 1);
        int days = Math.Min(now.Day, 17);
        var weights = Enumerable.Range(0, days).Select(i => i switch { 0 => 12.50m, 1 => 8.25m, 2 => 10m, _ => 1.25m }).ToArray();
        decimal total = weights.Sum();
        var bars = Enumerable.Range(0, days).Select(i => new DailySpend(
            start.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            i == days - 1 ? consumption - weights.Take(i).Sum(w => consumption * w / total) : consumption * weights[i] / total,
            days > 3 && i >= days - 3)).ToArray();
        return new(true, "Daily consumption connected", Connected: true, Bars: bars,
            State: EnterpriseSetupState.Connected, ConnectedUsername: "work@example.invalid",
            AxisTicks: DailyChartPresentation.Ticks(bars));
    }
    public async Task SaveSettingsAsync(SettingsView settings)
    {
        await InitializeVerificationAsync();
        if (_verificationStore is not null)
        {
            var next = _verificationSettings with { TrackedTelemetryEnabled = settings.TrackedTelemetryEnabled };
            await _verificationStore.SaveSettingsAsync(next);
            _verificationSettings = next;
            _telemetry?.Configure(next.TrackedTelemetryEnabled, next.TelemetryId!.Value);
        }
        Settings = settings with { Startup = false };
        await RefreshAsync();
    }
    public Task SaveAccountAsync(string key, string displayName, string thresholds, decimal? spendIncrementUsd = null,
        bool? showPeriodEstimate = null, decimal? customBudgetUsd = null, bool updateCustomBudget = false)
    {
        var previous = AccountSettings(key);
        displayName = displayName.Trim();
        if (displayName.Length > 128 || displayName.Any(char.IsControl))
            throw new AppOperationException("Display names must be at most 128 characters and contain no control characters.");
        AppSettings.ValidateSpendIncrement(spendIncrementUsd);
        UsageBudget.Validate(customBudgetUsd);
        _preferences[key] = (displayName, thresholds, spendIncrementUsd, showPeriodEstimate ?? previous.ShowPeriodEstimate);
        if (updateCustomBudget) _budgets[key] = customBudgetUsd;
        return RefreshAsync();
    }
    public (string DisplayName, string Thresholds, decimal? SpendIncrementUsd, bool ShowPeriodEstimate) AccountSettings(string key)
    {
        if (_preferences.TryGetValue(key, out var preferences)) return preferences;
        if (key is "github.com:1" && !empty && !usageStates) return (unlimited ? "Unlimited (demo)" : "Personal (demo)", "", null, false);
        if (key is "example.ghe.com:2" && !empty && !unlimited && !usageStates) return ("Work (demo)", "", null, false);
        var example = _examples.SingleOrDefault(account => account.Key == key)
            ?? throw new AppOperationException("That account is no longer configured.");
        return (example.Name, "", null, usageStates && example.PeriodEstimate is not null);
    }
    public Task RemoveAsync(string key) => throw new AppOperationException("Synthetic accounts cannot be removed in demonstration mode.");
    public Task AddAsync(string host, bool offlineAccess, string? reconnectKey,
        Action<DevicePrompt> prompt, Action authorized, CancellationToken cancellationToken,
        string? clientId = null) =>
        throw new AppOperationException("Authentication is disabled in demonstration mode. Start normal portable mode to sign in.");
    public string? AccountClientId(string key)
    {
        _ = AccountSettings(key);
        return null;
    }
    public string ResolveHostDescription(string host)
    {
        try
        {
            var value = Core.HostResolver.Resolve(host);
            return $"{value.Kind}: auth {value.WebBaseUri}\r\nAPI {value.ApiBaseUri}";
        }
        catch (ArgumentException) { throw new AppOperationException("Enter a valid HTTPS host."); }
    }
    public Task ResumeAsync() => RefreshAsync();
    public void SetNotificationHandler(Func<NotificationView, Task<bool>> handler) { }
    public void Dispose() => _telemetry?.Dispose();
}
