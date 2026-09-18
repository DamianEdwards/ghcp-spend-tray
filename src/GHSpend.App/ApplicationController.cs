using System.Collections.Concurrent;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GHSpend.App.Platform;
using GHSpend.Core;

namespace GHSpend.App;

internal sealed class ApplicationController : IApplicationController, INotificationSink
{
    private readonly JsonStore _store;
    private readonly HttpClient _http;
    private readonly ICredentialStore _credentials;
    private readonly DeviceFlowClient _auth;
    private readonly CopilotUsageProvider _usage;
    private readonly AlertService _alerts;
    private readonly StartupRegistration? _startup;
    private readonly SemaphoreSlim _mutations = new(1, 1);
    private readonly SemaphoreSlim _render = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset? Sample, UsageSnapshot[] History)> _history = new();
    private readonly object _initializationLock = new();
    private AppSettings _settings = new();
    private MonitorService? _monitor;
    private Task? _initialization, _clock;
    private Func<string, string, string, Task<bool>>? _notify;
    private string? _storageDiagnostic;
    private bool _disposed;
    public string DataDirectory { get; }
    public bool Portable { get; }
    public event Action<DashboardView>? Changed;

    internal ApplicationController(string directory, bool portable, HttpClient? http = null, ICredentialStore? credentials = null)
    {
        DataDirectory = directory; Portable = portable;
        _store = new JsonStore(directory);
        _store.DiagnosticReported += StoreDiagnostic;
        _http = http ?? HttpTransport.CreateClient();
        _credentials = credentials ?? new VaultCredentials(portable ? directory : null);
        _auth = new DeviceFlowClient(_http);
        _usage = new CopilotUsageProvider(_http, new TokenManager(_credentials, _auth));
        _alerts = new AlertService(_store, this);
        if (!portable) _startup = new StartupRegistration(Path.Combine(directory, "ghspend.exe"));
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
    }

    public SettingsView Settings => new(_settings.PollIntervalMinutes,
        FormatThresholds(_settings.AlertThresholds), _settings.NotificationsEnabled, _startup?.Enabled ?? false);

    public Task InitializeAsync()
    {
        lock (_initializationLock) return _initialization ??= InitializeCoreAsync();
    }
    private async Task InitializeCoreAsync()
    {
        try
        {
            var result = await _store.LoadSettingsAsync(_stop.Token).ConfigureAwait(false);
            _settings = result.Value;
            if (result.Diagnostics.Length > 0) _storageDiagnostic = string.Join(" ", result.Diagnostics);
            _stop.Token.ThrowIfCancellationRequested();
            _monitor = new MonitorService(_usage, _store, _alerts, _settings);
            _monitor.StateChanged += StateChanged;
            _monitor.DiagnosticReported += StoreDiagnostic;
            await PublishAsync().ConfigureAwait(false);
            await _monitor.StartAsync(_stop.Token).ConfigureAwait(false);
            _clock = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
                try
                {
                    while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                        await PublishAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw SafeError(ex);
        }
    }

    public async Task RefreshAsync(string? accountKey = null)
    {
        await InitializeAsync().ConfigureAwait(false);
        await _monitor!.RefreshAsync(accountKey, _stop.Token).ConfigureAwait(false);
    }
    public async Task ResumeAsync()
    {
        await InitializeAsync().ConfigureAwait(false);
        _monitor!.NotifyResume();
        await PublishAsync().ConfigureAwait(false);
    }
    public async Task SaveSettingsAsync(SettingsView settings)
    {
        await InitializeAsync().ConfigureAwait(false);
        await _mutations.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            var next = _settings with
            {
                PollIntervalMinutes = settings.PollMinutes,
                AlertThresholds = ParseThresholds(settings.Thresholds),
                NotificationsEnabled = settings.Notifications,
                StartWithWindows = Portable ? _settings.StartWithWindows : settings.Startup
            };
            next.Validate();
            var previousStartup = _startup?.Capture();
            bool changedStartup = _startup is not null && _startup.Enabled != settings.Startup;
            if (changedStartup) _startup!.SetEnabled(settings.Startup);
            try { await _store.SaveSettingsAsync(next, _stop.Token).ConfigureAwait(false); }
            catch
            {
                if (changedStartup) _startup!.Restore(previousStartup);
                throw;
            }
            _settings = next;
            _monitor!.UpdateSettings(next);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw SafeError(ex); }
        finally { _mutations.Release(); }
        await PublishAsync().ConfigureAwait(false);
    }
    public (string DisplayName, string Thresholds) AccountSettings(string key)
    {
        var account = _settings.Accounts.SingleOrDefault(a => a.Key == key)
            ?? throw new AppOperationException("That account is no longer configured.");
        return (account.DisplayName ?? "", account.ThresholdOverrides is null ? "" : FormatThresholds(account.ThresholdOverrides));
    }
    public async Task SaveAccountAsync(string key, string displayName, string thresholds)
    {
        await InitializeAsync().ConfigureAwait(false);
        await _mutations.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            displayName = displayName.Trim();
            if (displayName.Length > 128 || displayName.Any(char.IsControl))
                throw new AppOperationException("Display names must be at most 128 characters and contain no control characters.");
            var overrides = string.IsNullOrWhiteSpace(thresholds) ? null : ParseThresholds(thresholds);
            if (!_settings.Accounts.Any(a => a.Key == key)) throw new AppOperationException("That account is no longer configured.");
            var next = _settings with
            {
                Accounts = _settings.Accounts.Select(a => a.Key == key ? a with
                    { DisplayName = displayName.Length == 0 ? null : displayName, ThresholdOverrides = overrides } : a).ToArray()
            };
            await _store.SaveSettingsAsync(next, _stop.Token).ConfigureAwait(false);
            _settings = next;
            _monitor!.UpdateSettings(next);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw SafeError(ex); }
        finally { _mutations.Release(); }
        await PublishAsync().ConfigureAwait(false);
    }
    public async Task RemoveAsync(string key)
    {
        await InitializeAsync().ConfigureAwait(false);
        await _mutations.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            var account = _settings.Accounts.SingleOrDefault(a => a.Key == key)
                ?? throw new AppOperationException("That account is no longer configured.");
            await _monitor!.PauseAccountAsync(key, _stop.Token).ConfigureAwait(false);
            try
            {
                // Keep configuration when credential deletion fails, so removal can be retried visibly.
                await _credentials.DeleteAsync(account, _stop.Token).ConfigureAwait(false);
                var next = _settings with { Accounts = _settings.Accounts.Where(a => a.Key != key).ToArray() };
                await _store.SaveSettingsAsync(next, _stop.Token).ConfigureAwait(false);
                _settings = next;
                await _alerts.RemoveAccountAsync(key, _stop.Token).ConfigureAwait(false);
                _history.TryRemove(key, out _);
            }
            finally { _monitor!.UpdateSettings(_settings); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw SafeError(ex); }
        finally { _mutations.Release(); }
        await PublishAsync().ConfigureAwait(false);
    }
    public string ResolveHostDescription(string host)
    {
        try
        {
            var resolved = HostResolver.Resolve(host);
            return $"{resolved.Kind}: auth {resolved.WebBaseUri}\r\nAPI {resolved.ApiBaseUri}";
        }
        catch (ArgumentException)
        {
            throw new AppOperationException("Enter an HTTPS host only, without a path, user information, query or fragment. Custom ports are GHES-only.");
        }
    }
    public async Task AddAsync(string host, bool offlineAccess, string? reconnectKey,
        Action<DevicePrompt> prompt, Func<PendingIdentity, Task<bool>> confirm, CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var token = linked.Token;
        try
        {
            var resolved = HostResolver.Resolve(host);
            var authorization = await _auth.BeginAsync(resolved, GitHubOAuth.ClientId, offlineAccess, token).ConfigureAwait(false);
            prompt(new(authorization.UserCode, authorization.VerificationUri, authorization.ExpiresAtUtc));
            var tokens = await _auth.PollAsync(resolved, GitHubOAuth.ClientId, authorization, token).ConfigureAwait(false);
            var identity = await _auth.GetIdentityAsync(resolved, tokens, token).ConfigureAwait(false);
            var account = new Account { Host = resolved.Host, UserId = identity.UserId, Login = identity.Login };
            if (reconnectKey is not null && account.Key != reconnectKey)
                throw new AppOperationException("The browser selected a different account. Nothing was saved; select the original identity and reconnect again.");
            if (reconnectKey is null && _settings.Accounts.Any(a => a.Key == account.Key))
                throw new AppOperationException("This account is already monitored. Select it in the overview and choose Reconnect instead.");
            _ = await _usage.FetchWithTokenAsync(account, tokens, token).ConfigureAwait(false);
            if (!await confirm(new(account.Host, long.Parse(identity.UserId, CultureInfo.InvariantCulture), identity.Login))
                    .WaitAsync(token).ConfigureAwait(false))
                throw new OperationCanceledException(token);
            await _mutations.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var previous = _settings.Accounts.SingleOrDefault(a => a.Key == account.Key);
                if (previous is not null && reconnectKey is null)
                    throw new AppOperationException("This identity was added in another window. Use Reconnect.");
                if (previous is null && reconnectKey is not null)
                    throw new AppOperationException("This account was removed while sign-in was in progress. Add it again.");
                if (previous is not null)
                {
                    account = account with { DisplayName = previous.DisplayName, ThresholdOverrides = previous.ThresholdOverrides };
                    await _monitor!.PauseAccountAsync(account.Key, token).ConfigureAwait(false);
                }
                TokenSet? oldTokens = null;
                bool wroteCredential = false;
                try
                {
                    oldTokens = await _credentials.ReadAsync(account, token).ConfigureAwait(false);
                    var next = _settings with
                    {
                        Accounts = _settings.Accounts.Where(a => a.Key != account.Key).Append(account).ToArray()
                    };
                    await _credentials.WriteAsync(account, tokens, token).ConfigureAwait(false);
                    wroteCredential = true;
                    await _store.SaveSettingsAsync(next, token).ConfigureAwait(false);
                    _settings = next;
                }
                catch
                {
                    if (wroteCredential)
                    {
                        if (oldTokens is null) await _credentials.DeleteAsync(account, CancellationToken.None).ConfigureAwait(false);
                        else await _credentials.WriteAsync(account, oldTokens, CancellationToken.None).ConfigureAwait(false);
                    }
                    throw;
                }
                finally { _monitor!.UpdateSettings(_settings); }
            }
            finally { _mutations.Release(); }
            await _monitor!.RefreshAsync(account.Key, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw SafeError(ex); }
    }

    public void SetNotificationHandler(Func<string, string, string, Task<bool>> handler) => _notify = handler;
    public async Task<bool> SubmitAsync(UsageAlert alert, CancellationToken cancellationToken = default)
    {
        var notify = _notify ?? throw new AppOperationException("The notification surface is not initialized.");
        return await notify(alert.Account.Key, "GHSpend allocation alert",
            $"{alert.Account.DisplayName ?? alert.Account.Login}: {Money(alert.Snapshot.ConsumptionUsd)} consumed of " +
            $"{Money(alert.Snapshot.AllocationUsd!.Value)}; {alert.HighestThreshold:0.##}% threshold reached.")
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    private void StateChanged(AccountState state) => _ = PublishAsync();
    private void StoreDiagnostic(string message)
    {
        _storageDiagnostic = message;
        Diagnostics.Record("Local storage recovery or maintenance diagnostic; inspect the overview.");
    }
    private async void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable) return;
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            _monitor!.NotifyNetworkRecovery();
            await PublishAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Record($"Network recovery failed ({ex.GetType().Name})."); }
    }
    private async Task PublishAsync()
    {
        try
        {
            await _render.WaitAsync(_stop.Token).ConfigureAwait(false);
            try
            {
                var states = _monitor?.States ?? [];
                var now = DateTimeOffset.UtcNow;
                var total = UsageAggregation.Total(states, now, TimeSpan.FromMinutes(_settings.PollIntervalMinutes));
                var accountViews = new List<AccountView>();
                foreach (var state in states)
                {
                    var snapshot = state.Snapshot;
                    if (!_history.TryGetValue(state.Account.Key, out var cache) || cache.Sample != snapshot?.FetchedAtUtc)
                    {
                        try
                        {
                            var loaded = await _store.LoadHistoryAsync(state.Account.Key, now.AddHours(-48), _stop.Token).ConfigureAwait(false);
                            cache = (snapshot?.FetchedAtUtc, loaded.Value);
                            _history[state.Account.Key] = cache;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                        {
                            _storageDiagnostic = "History could not be loaded. Check storage permissions or corruption before continuing.";
                            cache = (snapshot?.FetchedAtUtc, []);
                        }
                    }
                    var graph = HistoryAnalysis.Build(cache.History, now);
                    string history = graph.CollectingHistory ? "Collecting history" :
                        $"Observed +{Money(graph.ObservedIncreaseUsd)} over the last 24 hours (sampled intervals). Graph: USD/hour.";
                    int gaps = graph.Points.Count(p => p.Kind != RatePointKind.Segment);
                    if (gaps > 0) history += $" {gaps} gap/reset/correction interval(s).";
                    bool current = snapshot is not null && BillingPeriods.IsCurrent(snapshot, now);
                    var visibleStatus = snapshot is not null && !current ? "Previous billing period - awaiting current data" :
                        state.Status == AccountStatus.Fresh && snapshot is not null &&
                        now - snapshot.FetchedAtUtc > TimeSpan.FromMinutes(_settings.PollIntervalMinutes)
                            ? "Stale - last-known observation" : state.Status.ToString();
                    var details = new StringBuilder()
                        .AppendLine($"{state.Account.Login} | {state.Account.Host}")
                        .AppendLine($"Status: {visibleStatus}" + (state.Diagnostic is null ? "" : " - " + state.Diagnostic));
                    if (snapshot is not null)
                    {
                        details.AppendLine($"Consumption{(current ? "" : " (previous period, excluded from total)")}: " +
                            $"{Money(snapshot.ConsumptionUsd)} | {snapshot.CreditsUsed:0.####} AI credits")
                            .AppendLine("Allocation: " + (snapshot.Unlimited ? "Unlimited" :
                                snapshot.AllocationUsd is { } allocation ? Money(allocation) : "N/A") +
                                " | consumed: " + (snapshot.PercentConsumed is { } percentage ? $"{percentage:0.##}%" : "N/A"))
                            .AppendLine($"Last fetched: {snapshot.FetchedAtUtc.ToLocalTime():g} | source: " +
                                (snapshot.SourceTimestampUtc?.ToLocalTime().ToString("g") ?? "not supplied"))
                            .AppendLine("Billing reset: " + (snapshot.ResetAtUtc?.ToLocalTime().ToString("g") ?? "calendar-month fallback"));
                    }
                    else details.AppendLine("Consumption unavailable (not zero).");
                    details.Append("Next refresh: " + (state.NextRefreshUtc?.ToLocalTime().ToString("g") ?? "pending"));
                    accountViews.Add(new(state.Account.Key, state.Account.DisplayName ?? state.Account.Login,
                        state.Account.Login, state.Account.Host, details.ToString().ReplaceLineEndings("\r\n"),
                        current ? snapshot?.PercentConsumed : null, history,
                        graph.Points.Select(p => new GraphPoint(p.ToUtc, p.UsdPerHour)).ToArray()));
                }
                var qualification = total.IsComplete ? "" : total.IsLastKnown ? "Last-known / partial " : "Partial ";
                var amount = total.IncludedAccounts == 0 ? "unavailable" : Money(total.ConsumptionUsd);
                var title = $"{qualification}MTD consumption: {amount} | {total.IncludedAccounts}/{total.TotalAccounts} accounts";
                var status = _storageDiagnostic ?? (states.Count == 0 ? "Add an account to get started. Sign-in uses the GitHub CLI OAuth application." :
                    $"Poll interval: {_settings.PollIntervalMinutes} minutes. Account freshness and errors are shown below.");
                var tooltip = $"GHSpend | {qualification}MTD {amount} | {total.IncludedAccounts}/{total.TotalAccounts} accounts";
                var last = states.Where(s => s.Snapshot is not null).Select(s => s.Snapshot!.FetchedAtUtc).DefaultIfEmpty().Min();
                if (last != default) tooltip += $"\nOldest update {last.ToLocalTime():HH:mm}";
                Changed?.Invoke(new(title, status, tooltip, accountViews));
            }
            finally { _render.Release(); }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Diagnostics.Record($"Dashboard update failed ({ex.GetType().Name}).");
            Changed?.Invoke(new("Consumption unavailable", "Dashboard could not update. Check storage and account errors.",
                "GHSpend | Consumption unavailable", []));
        }
    }
    private static decimal[] ParseThresholds(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(p => p.Length == 0))
            throw new AppOperationException("Enter one or more positive percentages separated by commas.");
        var result = new List<decimal>();
        foreach (var part in parts)
        {
            if (!decimal.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var percentage) || percentage <= 0)
                throw new AppOperationException("Thresholds must be positive percentages. Use '.' for decimals and ',' between values.");
            result.Add(percentage);
        }
        var sorted = result.Distinct().Order().ToArray();
        AppSettings.ValidateThresholds(sorted);
        return sorted;
    }
    private static string FormatThresholds(decimal[] values) => string.Join(", ", values.Select(v => v.ToString("0.##", CultureInfo.InvariantCulture)));
    private static string Money(decimal value) => "$" + value.ToString("N2", CultureInfo.GetCultureInfo("en-US"));
    private static AppOperationException SafeError(Exception ex) => ex switch
    {
        AppOperationException known => known,
        ServiceException service => new(service.Message),
        ArgumentException => new("Invalid settings or host. Check the displayed values and supported ranges."),
        IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception =>
            new("Local storage or Credential Manager could not complete the operation. Check permissions and free space; existing configuration was preserved where possible."),
        _ => new("The request failed or timed out. Check connectivity and the host's OAuth app approval.")
    };
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        _stop.Cancel();
        if (_monitor is not null)
        {
            _monitor.StateChanged -= StateChanged;
            _monitor.DiagnosticReported -= StoreDiagnostic;
            _monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        _http.Dispose();
    }
}

internal sealed class VaultCredentials(string? portableDirectory) : ICredentialStore
{
    private readonly CredentialVault _vault = new();
    private readonly string _prefix = portableDirectory is null ? "GHSpend/v1/" :
        "GHSpend/portable/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(portableDirectory).ToUpperInvariant()))) + "/";
    private string Target(Account account) => _prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.Key)));
    public Task<TokenSet?> ReadAsync(Account account, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = _vault.Read(Target(account));
        if (payload is null) return Task.FromResult<TokenSet?>(null);
        var value = JsonSerializer.Deserialize(payload, CoreJsonContext.Default.TokenSet);
        if (value is null || value.Version != 1 || string.IsNullOrWhiteSpace(value.AccessToken))
            throw new InvalidDataException("The stored GHSpend credential is invalid.");
        return Task.FromResult<TokenSet?>(value);
    }
    public Task WriteAsync(Account account, TokenSet tokens, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _vault.Write(Target(account), JsonSerializer.Serialize(tokens, CoreJsonContext.Default.TokenSet));
        return Task.CompletedTask;
    }
    public Task DeleteAsync(Account account, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _vault.Delete(Target(account));
        return Task.CompletedTask;
    }
}
