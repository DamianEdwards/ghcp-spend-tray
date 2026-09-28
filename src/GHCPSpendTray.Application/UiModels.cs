namespace GHCPSpendTray.Shared;

public sealed class AppOperationException(string message) : Exception(message);
public class PlatformOperationException(string message) : Exception(message);
public sealed record AccountDiagnostics(decimal? CreditsUsed, decimal? ObservedConsumptionUsd,
    decimal? ObservedAllocationUsd, decimal? ObservedPercentConsumed, bool Unlimited, DateTimeOffset? SourceTimestampUtc,
    DateTimeOffset? ResetAtUtc, DateTimeOffset? NextRefreshUtc, string? PeriodId,
    bool IsCurrentPeriod, string? Message);
public sealed record AccountView(string Key, string Name, string Login, string Host, AccountDiagnostics Details,
    decimal? Percent,
    decimal? ConsumptionUsd = null, decimal? AllocationUsd = null, string Freshness = "", DateTimeOffset? UpdatedAt = null,
    string? AvatarUrl = null);
public sealed record DashboardView(string Total, string Status, string Tooltip, IReadOnlyList<AccountView> Accounts,
    decimal? ConsumptionUsd = null, bool IsComplete = false, bool IsLastKnown = false);
public sealed record SettingsView(int PollMinutes, string Thresholds, bool Notifications, bool Startup,
    decimal? SpendIncrementUsd = null, bool CanChangeStartup = false,
    string StartupDescription = "Unavailable in isolated portable mode.");
public sealed record DevicePrompt(string Code, Uri VerificationUri, DateTimeOffset Expires);
public sealed record PendingIdentity(string Host, long UserId, string Login);

public interface IStartupRegistration
{
    bool Enabled { get; }
    bool CanChange { get; }
    string Description { get; }
    Task SetEnabledAsync(bool enabled);
}

public interface IApplicationController : IDisposable
{
    string DataDirectory { get; }
    bool Portable { get; }
    event Action<DashboardView>? Changed;
    SettingsView Settings { get; }
    Task InitializeAsync();
    Task RefreshAsync(string? accountKey = null);
    Task RefreshAccountAsync(string accountKey);
    Task SaveSettingsAsync(SettingsView settings);
    Task SaveAccountAsync(string key, string displayName, string thresholds, decimal? spendIncrementUsd = null);
    (string DisplayName, string Thresholds, decimal? SpendIncrementUsd) AccountSettings(string key);
    Task RemoveAsync(string key);
    Task AddAsync(string host, bool offlineAccess, string? reconnectKey,
        Action<DevicePrompt> prompt, Func<PendingIdentity, Task<bool>> confirm, CancellationToken cancellationToken,
        string? clientId = null);
    string? AccountClientId(string key);
    string ResolveHostDescription(string host);
    Task ResumeAsync();
    void SetNotificationHandler(Func<string, string, string, Task<bool>> handler);
}
