namespace GHSpend.App;

internal sealed class AppOperationException(string message) : Exception(message);
internal sealed record GraphPoint(DateTimeOffset Time, decimal? Rate);
internal sealed record AccountDiagnostics(decimal? CreditsUsed, decimal? ObservedConsumptionUsd,
    decimal? ObservedAllocationUsd, decimal? ObservedPercentConsumed, bool Unlimited, DateTimeOffset? SourceTimestampUtc,
    DateTimeOffset? ResetAtUtc, DateTimeOffset? NextRefreshUtc, string? PeriodId,
    bool IsCurrentPeriod, string? Message);
internal sealed record AccountView(string Key, string Name, string Login, string Host, AccountDiagnostics Details,
    decimal? Percent, string HistoryText, IReadOnlyList<GraphPoint> Graph,
    decimal? ConsumptionUsd = null, decimal? AllocationUsd = null, string Freshness = "", DateTimeOffset? UpdatedAt = null);
internal sealed record DashboardView(string Total, string Status, string Tooltip, IReadOnlyList<AccountView> Accounts,
    decimal? ConsumptionUsd = null, bool IsComplete = false, bool IsLastKnown = false);
internal sealed record SettingsView(int PollMinutes, string Thresholds, bool Notifications, bool Startup,
    decimal? SpendIncrementUsd = null);
internal sealed record DevicePrompt(string Code, Uri VerificationUri, DateTimeOffset Expires);
internal sealed record PendingIdentity(string Host, long UserId, string Login);

internal interface IApplicationController : IDisposable
{
    string DataDirectory { get; }
    bool Portable { get; }
    event Action<DashboardView>? Changed;
    SettingsView Settings { get; }
    Task InitializeAsync();
    Task RefreshAsync(string? accountKey = null);
    Task SaveSettingsAsync(SettingsView settings);
    Task SaveAccountAsync(string key, string displayName, string thresholds, decimal? spendIncrementUsd = null);
    (string DisplayName, string Thresholds, decimal? SpendIncrementUsd) AccountSettings(string key);
    Task RemoveAsync(string key);
    Task AddAsync(string host, bool offlineAccess, string? reconnectKey,
        Action<DevicePrompt> prompt, Func<PendingIdentity, Task<bool>> confirm, CancellationToken cancellationToken);
    string ResolveHostDescription(string host);
    Task ResumeAsync();
    void SetNotificationHandler(Func<string, string, string, Task<bool>> handler);
}
