using GHCPSpendTray.Core;

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
    decimal? ConsumptionUsd = null, bool IsComplete = false, bool IsLastKnown = false,
    TrayPresentation? Tray = null, IReadOnlyList<AccountState>? TrayStates = null);
public sealed record SettingsView(int PollMinutes, string Thresholds, bool Notifications, bool Startup,
    decimal? SpendIncrementUsd = null, bool CanChangeStartup = false,
    string StartupDescription = "Unavailable in isolated portable mode.",
    TrayIconStyle TrayStyle = TrayIconStyle.Pie, TrayDisplayMode TrayMode = TrayDisplayMode.RollUp,
    string[]? ExcludedTrayAccounts = null);
public sealed record DevicePrompt(string Code, Uri VerificationUri, DateTimeOffset Expires)
{
    public override string ToString() => "DevicePrompt [redacted]";
}

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
        Action<DevicePrompt> prompt, Action authorized, CancellationToken cancellationToken,
        string? clientId = null);
    string? AccountClientId(string key);
    string ResolveHostDescription(string host);
    Task ResumeAsync();
    void SetNotificationHandler(Func<string, string, string, Task<bool>> handler);
}
