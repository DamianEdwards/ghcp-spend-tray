using System.Globalization;
using GHCPSpendTray.Core;

namespace GHCPSpendTray.Shared;

public sealed class DemoController(string directory, bool empty = false) : IApplicationController
{
    private readonly List<AccountView> _examples = [];
    private readonly Dictionary<string, (string DisplayName, string Thresholds, decimal? SpendIncrementUsd,
        bool ShowPeriodEstimate)> _preferences = [];
    public string DataDirectory => directory;
    public bool Portable => true;
    public event Action<DashboardView>? Changed;
    public SettingsView Settings { get; private set; } = new(60, "50, 80, 100", true, false);
    public Task InitializeAsync() => RefreshAsync();
    public Task RefreshAsync(string? accountKey = null)
    {
        var now = DateTimeOffset.UtcNow;
        if (empty && _examples.Count == 0)
        {
            Changed?.Invoke(new("No accounts", "Synthetic demonstration only.", "GHCPSpendTray DEMO | No accounts", [],
                Tray: TrayUsage.Create(new() { TrayStyle = Settings.TrayStyle, TrayMode = Settings.TrayMode }, [], now)));
            return Task.CompletedTask;
        }
        List<AccountView> views = [];
        if (!empty) views.AddRange([
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
            "Synthetic demonstration only. No network, real account data, installation, or startup changes.",
            $"GHCPSpendTray DEMO | MTD ${amount} | {views.Count} accounts", views, total, true);
        var accounts = dashboard.Accounts.Select((a, i) => new Account
        {
            Host = a.Host, UserId = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Login = a.Login, DisplayName = a.Name,
            ShowPeriodEstimate = _preferences.GetValueOrDefault(a.Key).ShowPeriodEstimate,
            ExcludeFromTray = Settings.ExcludedTrayAccounts?.Contains(a.Key, StringComparer.Ordinal) == true
        }).ToArray();
        var states = accounts.Select((a, i) => new AccountState
        {
            Account = a, Status = AccountStatus.Fresh, Snapshot = new()
            {
                AccountKey = a.Key, FetchedAtUtc = now, SourceTimestampUtc = now, PeriodId = BillingPeriods.Resolve(now, null),
                CreditsUsed = dashboard.Accounts[i].Details.CreditsUsed!.Value,
                Entitlement = dashboard.Accounts[i].AllocationUsd * 100,
                ConsumptionUsd = dashboard.Accounts[i].ConsumptionUsd!.Value,
                AllocationUsd = dashboard.Accounts[i].AllocationUsd, PercentConsumed = dashboard.Accounts[i].Percent
            }
        }).ToArray();
        Changed?.Invoke(dashboard with
        {
            Accounts = dashboard.Accounts.Select((account, i) => account with
            {
                PeriodEstimate = PeriodEstimates.Create(states[i], now, TimeSpan.FromMinutes(Settings.PollMinutes))
            }).ToArray(),
            Tray = TrayUsage.Create(new()
            {
                Accounts = accounts, TrayStyle = Settings.TrayStyle, TrayMode = Settings.TrayMode
            }, states, now),
            TrayStates = states
        });
        return Task.CompletedTask;
    }
    public Task AddExampleAccountAsync()
    {
        var now = DateTimeOffset.UtcNow;
        string id = (_examples.Count + (empty ? 1 : 3)).ToString(CultureInfo.InvariantCulture);
        _examples.Add(new($"github.com:{id}", $"Example {id} (demo)", $"demo-example-{id}", "github.com",
            new(1250m, 12.5m, 50m, 25m, false, now, null, now.AddHours(1), "synthetic", true, null),
            25m, 12.5m, 50m, "Fresh", now));
        return RefreshAsync();
    }
    public Task RefreshAccountAsync(string accountKey) => RefreshAsync(accountKey);
    public Task SaveSettingsAsync(SettingsView settings)
    {
        Settings = settings with { Startup = false };
        return RefreshAsync();
    }
    public Task SaveAccountAsync(string key, string displayName, string thresholds, decimal? spendIncrementUsd = null,
        bool? showPeriodEstimate = null)
    {
        var previous = AccountSettings(key);
        displayName = displayName.Trim();
        if (displayName.Length > 128 || displayName.Any(char.IsControl))
            throw new AppOperationException("Display names must be at most 128 characters and contain no control characters.");
        AppSettings.ValidateSpendIncrement(spendIncrementUsd);
        _preferences[key] = (displayName, thresholds, spendIncrementUsd, showPeriodEstimate ?? previous.ShowPeriodEstimate);
        return RefreshAsync();
    }
    public (string DisplayName, string Thresholds, decimal? SpendIncrementUsd, bool ShowPeriodEstimate) AccountSettings(string key)
    {
        if (_preferences.TryGetValue(key, out var preferences)) return preferences;
        if (key is "github.com:1" && !empty) return ("Personal (demo)", "", null, false);
        if (key is "example.ghe.com:2" && !empty) return ("Work (demo)", "", null, false);
        var example = _examples.SingleOrDefault(account => account.Key == key)
            ?? throw new AppOperationException("That account is no longer configured.");
        return (example.Name, "", null, false);
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
    public void Dispose() { }
}
