namespace GHCPSpendTray.App;

internal sealed class DemoController(string directory, bool empty = false) : IApplicationController
{
    public string DataDirectory => directory;
    public bool Portable => true;
    public event Action<DashboardView>? Changed;
    public SettingsView Settings { get; private set; } = new(60, "50, 80, 100", true, false);
    public Task InitializeAsync() => RefreshAsync();
    public Task RefreshAsync(string? accountKey = null)
    {
        var now = DateTimeOffset.UtcNow;
        if (empty)
        {
            Changed?.Invoke(new("No accounts", "Synthetic demonstration only.", "GHCPSpendTray DEMO | No accounts", []));
            return Task.CompletedTask;
        }
        var points = Enumerable.Range(0, 24).Select(i =>
            new GraphPoint(now.AddHours(i - 23), i is 8 or 9 ? null : (decimal?)(1m + i % 5 * .75m))).ToArray();
        Changed?.Invoke(new("DEMO - MTD consumption: $42.75 | 2/2 accounts",
            "Synthetic demonstration only. No network, real account data, installation, or startup changes.",
            "GHCPSpendTray DEMO | MTD $42.75 | 2 accounts", [
            new("github.com:1", "Personal (demo)", "demo-user", "github.com",
                new(2625m, 26.25m, 25m, 105m, false, now, null, now.AddHours(1), "synthetic", true, null), 105m,
                "Observed +$12.40 over the last 24 hours (synthetic). Gaps are shown.", points, 26.25m, 25m, "Fresh", now),
            new("example.ghe.com:2", "Work (demo)", "demo-work", "example.ghe.com",
                new(1650m, 16.5m, 100m, 16.5m, false, now, null, now.AddHours(1), "synthetic", true, null),
                16.5m, "Collecting history", [], 16.5m, 100m, "Fresh", now)
        ], 42.75m, true));
        return Task.CompletedTask;
    }
    public Task SaveSettingsAsync(SettingsView settings)
    {
        Settings = settings with { Startup = false };
        return RefreshAsync();
    }
    public Task SaveAccountAsync(string key, string displayName, string thresholds, decimal? spendIncrementUsd = null) => Task.CompletedTask;
    public (string DisplayName, string Thresholds, decimal? SpendIncrementUsd) AccountSettings(string key) => ("Demonstration", "", null);
    public Task RemoveAsync(string key) => throw new AppOperationException("Synthetic accounts cannot be removed in demonstration mode.");
    public Task AddAsync(string host, bool offlineAccess, string? reconnectKey,
        Action<DevicePrompt> prompt, Func<PendingIdentity, Task<bool>> confirm, CancellationToken cancellationToken) =>
        throw new AppOperationException("Authentication is disabled in demonstration mode. Start normal portable mode to sign in.");
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
    public void SetNotificationHandler(Func<string, string, string, Task<bool>> handler) { }
    public void Dispose() { }
}
