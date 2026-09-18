namespace GHSpend.App;

internal sealed class AppOperationException(string message) : Exception(message);
internal sealed record GraphPoint(DateTimeOffset Time, decimal? Rate);
internal sealed record AccountView(string Key, string Name, string Login, string Host, string Details,
    decimal? Percent, string HistoryText, IReadOnlyList<GraphPoint> Graph);
internal sealed record DashboardView(string Total, string Status, string Tooltip, IReadOnlyList<AccountView> Accounts);
internal sealed record SettingsView(int PollMinutes, string Thresholds, bool Notifications, bool Startup);
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
    Task SaveAccountAsync(string key, string displayName, string thresholds);
    (string DisplayName, string Thresholds) AccountSettings(string key);
    Task RemoveAsync(string key);
    Task AddAsync(string host, bool offlineAccess, string? reconnectKey,
        Action<DevicePrompt> prompt, Func<PendingIdentity, Task<bool>> confirm, CancellationToken cancellationToken);
    string ResolveHostDescription(string host);
    Task ResumeAsync();
    void SetNotificationHandler(Func<string, string, string, Task<bool>> handler);
}
