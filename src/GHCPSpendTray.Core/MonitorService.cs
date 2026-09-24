namespace GHCPSpendTray.Core;

/// <summary>One scheduler, bounded parallel accounts, and one shared in-flight task per immutable identity.</summary>
public sealed class MonitorService : IAsyncDisposable
{
    private readonly ICopilotUsageProvider _provider;
    private readonly JsonStore _store;
    private readonly AlertService _alerts;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _parallelism;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<Task> _retired = [];
    private AppSettings _settings;
    private Task? _loop;
    private bool _disposed;
    private DateTimeOffset _maintenanceAt = DateTimeOffset.MinValue;

    public event Action<AccountState>? StateChanged;
    public event Action<string>? DiagnosticReported;

    public MonitorService(ICopilotUsageProvider provider, JsonStore store, AlertService alerts,
        AppSettings settings, TimeProvider? timeProvider = null, int maxConcurrency = 3)
    {
        if (maxConcurrency is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        settings.Validate();
        _provider = provider;
        _store = store;
        _alerts = alerts;
        _alerts.DiagnosticReported += ReportDiagnostic;
        _time = timeProvider ?? TimeProvider.System;
        _parallelism = new(maxConcurrency, maxConcurrency);
        _settings = Copy(settings);
        foreach (Account account in _settings.Accounts) _entries.Add(account.Key, new(account, _stop.Token));
    }

    public IReadOnlyList<AccountState> States
    {
        get { lock (_sync) return _entries.Values.Select(e => CopyState(e.State)).ToArray(); }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _loop ??= Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public void UpdateSettings(AppSettings settings)
    {
        settings.Validate();
        AppSettings copy = Copy(settings);
        List<AccountState> changed = [];
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var keys = copy.Accounts.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
            foreach (string key in _entries.Keys.Where(k => !keys.Contains(k)).ToArray())
            {
                Entry removed = _entries[key];
                removed.Cancellation.Cancel();
                if (removed.InFlight is not null)
                    _retired.Add(removed.InFlight.ContinueWith(_ => removed.Cancellation.Dispose(),
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
                else removed.Cancellation.Dispose();
                _entries.Remove(key);
            }
            _settings = copy;
            foreach (Account account in copy.Accounts)
            {
                if (!_entries.TryGetValue(account.Key, out Entry? entry))
                {
                    entry = new(account, _stop.Token);
                    _entries.Add(account.Key, entry);
                }
                DateTimeOffset? next = entry.State.LastAttemptUtc?.AddMinutes(copy.PollIntervalMinutes);
                if (entry.NotBeforeUtc > next || next is null && entry.NotBeforeUtc is not null)
                    next = entry.NotBeforeUtc;
                entry.State = entry.State with { Account = account, NextRefreshUtc = next };
                changed.Add(entry.State);
            }
            _retired.RemoveAll(t => t.IsCompleted);
        }
        foreach (AccountState state in changed) Publish(state);
        Wake();
    }

    /// <summary>Caller cancellation stops waiting; it does not cancel an operation shared by other callers.</summary>
    public async Task RefreshAsync(string? accountKey = null, CancellationToken cancellationToken = default)
    {
        Task[] tasks;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            tasks = _entries.Where(p => accountKey is null || p.Key == accountKey)
                .Select(p => BeginRefresh(p.Value)).ToArray();
        }
        await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cancel and join account work before deleting its credentials, preventing a late refresh write.</summary>
    public async Task RemoveAccountAsync(string accountKey, CancellationToken cancellationToken = default)
    {
        Task? inFlight;
        Entry? entry;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.Remove(accountKey, out entry)) return;
            entry.Cancellation.Cancel();
            inFlight = entry.InFlight;
            _settings = _settings with { Accounts = _settings.Accounts.Where(a => a.Key != accountKey).ToArray() };
            if (inFlight is not null)
                _retired.Add(inFlight.ContinueWith(_ => entry.Cancellation.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
            else entry.Cancellation.Dispose();
        }
        Wake();
        if (inFlight is not null) await inFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Quiesce an account before reconnecting. Does not modify persisted settings or credentials.
    /// After replacing credentials, UpdateSettings with the account present resumes monitoring.
    /// </summary>
    public Task PauseAccountAsync(string accountKey, CancellationToken cancellationToken = default) =>
        RemoveAccountAsync(accountKey, cancellationToken);

    /// <summary>Use after sleep: overdue accounts catch up once, not once per missed tick.</summary>
    public void NotifyResume() => Wake();

    /// <summary>Spread overdue network retries across 30 seconds while retaining server rate-limit deadlines.</summary>
    public void NotifyNetworkRecovery()
    {
        List<AccountState> changed = [];
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DateTimeOffset now = _time.GetUtcNow();
            foreach (Entry entry in _entries.Values)
            {
                if (entry.State.Status != AccountStatus.NetworkError ||
                    entry.InFlight is { IsCompleted: false } || entry.State.NextRefreshUtc > now)
                    continue;
                DateTimeOffset next = now.AddSeconds(Random.Shared.NextDouble() * 30);
                if (entry.NotBeforeUtc > next) next = entry.NotBeforeUtc.Value;
                entry.State = entry.State with { NextRefreshUtc = next };
                changed.Add(entry.State);
            }
        }
        foreach (AccountState state in changed) Publish(state);
        Wake();
    }

    private Task BeginRefresh(Entry entry)
    {
        if (entry.InFlight is { IsCompleted: false }) return entry.InFlight;
        if (entry.NotBeforeUtc > _time.GetUtcNow()) return Task.CompletedTask;
        return entry.InFlight = Task.Run(() => RefreshEntryAsync(entry), CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                DateTimeOffset now = _time.GetUtcNow();
                TimeSpan wait = TimeSpan.FromMinutes(1);
                int retention;
                lock (_sync)
                {
                    foreach (Entry entry in _entries.Values)
                    {
                        if (entry.InFlight is { IsCompleted: false }) continue;
                        if (entry.State.NextRefreshUtc is null || entry.State.NextRefreshUtc <= now)
                            _ = BeginRefresh(entry);
                        else
                            wait = Min(wait, entry.State.NextRefreshUtc.Value - now);
                    }
                    retention = _settings.HistoryRetentionDays;
                }
                if (now >= _maintenanceAt)
                {
                    _maintenanceAt = now.AddDays(1);
                    try
                    {
                        foreach (string diagnostic in await _store.MaintainHistoryAsync(now, retention, cancellationToken).ConfigureAwait(false))
                            ReportDiagnostic(diagnostic);
                    }
                    catch (IOException) { ReportDiagnostic("Scheduled history retention failed; existing data was preserved."); }
                    catch (UnauthorizedAccessException) { ReportDiagnostic("Scheduled history retention was denied access; existing data was preserved."); }
                }
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task signal = _wake.WaitAsync(tick.Token);
                Task timer = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1), _time, tick.Token);
                await Task.WhenAny(signal, timer).ConfigureAwait(false);
                await tick.CancelAsync().ConfigureAwait(false);
                try { await Task.WhenAll(signal, timer).ConfigureAwait(false); }
                catch (OperationCanceledException) when (tick.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RefreshEntryAsync(Entry entry)
    {
        CancellationToken removed = entry.Cancellation.Token;
        bool entered = false;
        try
        {
            await _parallelism.WaitAsync(removed).ConfigureAwait(false);
            entered = true;
            Account account;
            AppSettings settings;
            lock (_sync) { account = entry.State.Account; settings = _settings; }
            DateTimeOffset now = _time.GetUtcNow();
            SetState(entry, s => s with { LastAttemptUtc = now });
            if (!entry.HistoryLoaded)
            {
                StoreLoadResult<UsageSnapshot[]> loaded = await _store.LoadHistoryAsync(account.Key,
                    now.AddDays(-settings.HistoryRetentionDays), removed).ConfigureAwait(false);
                entry.HistoryLoaded = true;
                entry.HistoryDiagnostic = loaded.Diagnostics.Length > 0 ? string.Join(" ", loaded.Diagnostics) : null;
                if (entry.HistoryDiagnostic is not null) ReportDiagnostic(entry.HistoryDiagnostic);
                UsageSnapshot? latest = loaded.Value.LastOrDefault();
                if (latest is not null) SetState(entry, s => s with
                {
                    Snapshot = latest, Status = AccountStatus.Stale,
                    Diagnostic = entry.HistoryDiagnostic
                });
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90), _time);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(removed, timeout.Token);
            UsageSnapshot snapshot = await _provider.FetchAsync(account, operation.Token).ConfigureAwait(false);
            snapshot.Validate();
            if (snapshot.AccountKey != account.Key)
                throw new ServiceException(AccountStatus.InvalidData, "The usage response belongs to a different account.");
            if (!BillingPeriods.IsCurrent(snapshot, _time.GetUtcNow()))
                throw new ServiceException(AccountStatus.InvalidData, "The usage response is not from the current billing period.");
            removed.ThrowIfCancellationRequested();
            try { await _store.AppendHistoryAsync(snapshot, removed).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetState(entry, s => s with { Snapshot = snapshot });
                throw new ServiceException(AccountStatus.StorageError, "Current consumption is unsaved: history could not be written.");
            }
            string? diagnostic = null;
            try { await _alerts.EvaluateAsync(account, snapshot, settings, removed).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostic = "Consumption saved, but notification submission or its durable ledger failed; it will be retried.";
            }
            entry.Failures = 0;
            lock (_sync) { entry.NotBeforeUtc = null; settings = _settings; }
            SetState(entry, s => s with
            {
                Snapshot = snapshot, Status = diagnostic is null ? AccountStatus.Fresh : AccountStatus.StorageError,
                Diagnostic = diagnostic ?? entry.HistoryDiagnostic, NextRefreshUtc = _time.GetUtcNow().AddMinutes(settings.PollIntervalMinutes)
            });
        }
        catch (OperationCanceledException) when (removed.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AccountStatus status = ex is ServiceException service ? service.Status :
                ex is IOException or UnauthorizedAccessException ? AccountStatus.StorageError :
                ex is ArgumentException or OverflowException ? AccountStatus.InvalidData : AccountStatus.NetworkError;
            string message = ex is ServiceException safe ? safe.Message : status switch
            {
                AccountStatus.StorageError => "Local storage could not be read or written. Existing data was preserved.",
                AccountStatus.InvalidData => "Invalid consumption data was rejected.",
                _ => "The request failed or timed out. Check connectivity; a bounded retry is scheduled."
            };
            entry.Failures = Math.Min(entry.Failures + 1, 10);
            DateTimeOffset now = _time.GetUtcNow();
            double minutes = Math.Min(60, Math.Pow(2, entry.Failures - 1));
            DateTimeOffset retry = now.AddMinutes(minutes * (1 + Random.Shared.NextDouble() * .2));
            if (ex is ServiceException { RetryAtUtc: { } serverRetry } && serverRetry > retry) retry = serverRetry;
            if (status is AccountStatus.SignInRequired or AccountStatus.Forbidden or AccountStatus.Unsupported or AccountStatus.InvalidData)
            {
                lock (_sync) retry = now.AddMinutes(_settings.PollIntervalMinutes);
            }
            lock (_sync) entry.NotBeforeUtc = status == AccountStatus.RateLimited ? retry : null;
            SetState(entry, s => s with { Status = status, Diagnostic = message, NextRefreshUtc = retry });
            try
            {
                await _store.RecordDiagnosticAsync(status switch
                {
                    AccountStatus.SignInRequired => DiagnosticCode.AuthenticationRequired,
                    AccountStatus.Forbidden => DiagnosticCode.AccessDenied,
                    AccountStatus.RateLimited => DiagnosticCode.RateLimit,
                    AccountStatus.Unsupported => DiagnosticCode.UnsupportedCapability,
                    AccountStatus.InvalidData => DiagnosticCode.InvalidUsage,
                    AccountStatus.StorageError => DiagnosticCode.StorageFailure,
                    _ => DiagnosticCode.NetworkFailure
                }, now, entry.State.Account.Key, removed).ConfigureAwait(false);
            }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException or OperationCanceledException)
            { ReportDiagnostic("A diagnostic could not be saved; account status remains visible."); }
        }
        finally
        {
            if (entered) _parallelism.Release();
            Wake();
        }
    }

    private void SetState(Entry entry, Func<AccountState, AccountState> update)
    {
        AccountState state;
        lock (_sync)
        {
            if (!_entries.TryGetValue(entry.State.Account.Key, out Entry? current) || !ReferenceEquals(current, entry))
                return;
            state = entry.State = update(entry.State);
        }
        Publish(state);
    }

    private void Publish(AccountState state)
    {
        try { StateChanged?.Invoke(CopyState(state)); }
        catch (Exception) { ReportDiagnostic("An account-state observer failed; monitoring continues."); }
    }

    private void ReportDiagnostic(string message)
    {
        try { DiagnosticReported?.Invoke(message); }
        catch (Exception) { System.Diagnostics.Trace.TraceError("GHCPSpendTray monitor diagnostic observer failed."); }
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] operations;
        Task? loop;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            loop = _loop;
            operations = _entries.Values.Select(e => e.InFlight).OfType<Task>().Concat(_retired).ToArray();
        }
        if (loop is not null) await loop.ConfigureAwait(false);
        await Task.WhenAll(operations).ConfigureAwait(false);
        _alerts.DiagnosticReported -= ReportDiagnostic;
        foreach (Entry entry in _entries.Values) entry.Cancellation.Dispose();
        _stop.Dispose();
        _parallelism.Dispose();
        _wake.Dispose();
    }

    private static TimeSpan Min(TimeSpan x, TimeSpan y) => x < y ? x : y;
    private static AccountState CopyState(AccountState state) => state with
    {
        Account = state.Account with { ThresholdOverrides = state.Account.ThresholdOverrides?.ToArray() }
    };
    private static AppSettings Copy(AppSettings settings) => settings with
    {
        Accounts = settings.Accounts.Select(a => a with { ThresholdOverrides = a.ThresholdOverrides?.ToArray() }).ToArray(),
        AlertThresholds = settings.AlertThresholds.ToArray()
    };

    private sealed class Entry
    {
        public AccountState State;
        public readonly CancellationTokenSource Cancellation;
        public Task? InFlight;
        public bool HistoryLoaded;
        public string? HistoryDiagnostic;
        public int Failures;
        public DateTimeOffset? NotBeforeUtc;
        public Entry(Account account, CancellationToken stop)
        {
            State = new() { Account = account, Status = AccountStatus.Pending };
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop);
        }
    }
}
