using System.Text.Json.Serialization;

namespace GHCPSpendTray.Core;

public sealed record AlertLedger
{
    [JsonRequired] public int Version { get; init; } = 1;
    [JsonRequired] public Dictionary<string, AlertLedgerEntry> Accounts { get; init; } = new(StringComparer.Ordinal);
    public void Validate()
    {
        if (Version != 1 || Accounts is null) throw new ArgumentException("Invalid alert ledger.");
        foreach ((string key, AlertLedgerEntry entry) in Accounts)
        {
            if (string.IsNullOrWhiteSpace(key) || entry is null ||
                string.IsNullOrWhiteSpace(entry.PeriodId) || entry.LastSubmittedUtc == default)
                throw new ArgumentException("Invalid alert ledger entry.");
            AppSettings.ValidateThresholds(entry.SubmittedThresholds);
            if (entry.SubmittedSpendUsd < 0) throw new ArgumentException("Invalid spend notification ledger.");
        }
    }
}

public sealed record AlertLedgerEntry
{
    [JsonRequired] public string PeriodId { get; init; } = "";
    [JsonRequired] public decimal[] SubmittedThresholds { get; init; } = [];
    [JsonRequired] public DateTimeOffset LastSubmittedUtc { get; init; }
    public decimal SubmittedSpendUsd { get; init; }
}

public sealed record UsageAlert(Account Account, UsageSnapshot Snapshot, decimal HighestThreshold,
    decimal[] ReachedThresholds, decimal? SpendMilestoneUsd = null);

public interface INotificationSink
{
    /// <returns>True only when Windows accepts submission, not when user delivery is proven.</returns>
    Task<bool> SubmitAsync(UsageAlert alert, CancellationToken cancellationToken = default);
}

public sealed class AlertService(JsonStore store, INotificationSink notificationSink)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AlertLedger? _ledger;
    private AlertLedger? _pendingLedger;
    private readonly Dictionary<string, SubmissionRetry> _retries = new(StringComparer.Ordinal);
    public event Action<string>? DiagnosticReported;

    public async Task<bool> EvaluateAsync(Account account, UsageSnapshot snapshot, AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        settings.Validate();
        snapshot.Validate();
        if (snapshot.AccountKey != account.Key) throw new ArgumentException("Snapshot belongs to a different account.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!settings.NotificationsEnabled && _pendingLedger is null)
                return false;
            _ledger ??= (await store.LoadAlertLedgerAsync(cancellationToken).ConfigureAwait(false)).Value;
            if (_pendingLedger is { } pending)
            {
                await store.SaveAlertLedgerAsync(pending, cancellationToken).ConfigureAwait(false);
                _ledger = pending;
                _pendingLedger = null;
            }
            if (!settings.NotificationsEnabled) return false;
            _ledger.Accounts.TryGetValue(account.Key, out AlertLedgerEntry? previous);
            // Ignore out-of-order observations: an old sample cannot roll the durable period backwards.
            if (previous is not null && snapshot.FetchedAtUtc < previous.LastSubmittedUtc) return false;
            decimal[] submitted = previous?.PeriodId == snapshot.PeriodId ? previous.SubmittedThresholds : [];
            decimal[] reached = (account.ThresholdOverrides ?? settings.AlertThresholds)
                .Where(t => snapshot.PercentConsumed is { } percent && t <= percent && !submitted.Contains(t)).ToArray();
            decimal previousSpend = previous?.PeriodId == snapshot.PeriodId ? previous.SubmittedSpendUsd : 0;
            decimal? increment = account.SpendIncrementUsd ?? settings.SpendIncrementUsd;
            decimal? milestone = increment > 0
                ? decimal.Floor(snapshot.ConsumptionUsd / increment.Value) * increment.Value : null;
            if (milestone <= previousSpend) milestone = null;
            if (reached.Length == 0 && milestone is null) return false;
            _retries.TryGetValue(account.Key, out SubmissionRetry? retry);
            if (retry?.PeriodId == snapshot.PeriodId && snapshot.FetchedAtUtc < retry.NotBeforeUtc) return false;
            var alert = new UsageAlert(account with { ThresholdOverrides = account.ThresholdOverrides?.ToArray() },
                snapshot, reached.Length == 0 ? 0 : reached[^1], reached.ToArray(), milestone);
            bool accepted;
            try { accepted = await notificationSink.SubmitAsync(alert, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await RecordFailureAsync(account, snapshot, retry, cancellationToken).ConfigureAwait(false);
                throw new ServiceException(AccountStatus.StorageError, "Notification submission failed; a bounded retry is deferred to a later sample.");
            }
            if (!accepted)
            {
                await RecordFailureAsync(account, snapshot, retry, cancellationToken).ConfigureAwait(false);
                return false;
            }
            _retries.Remove(account.Key);
            var entries = new Dictionary<string, AlertLedgerEntry>(_ledger.Accounts, StringComparer.Ordinal)
            {
                [account.Key] = new()
                {
                    PeriodId = snapshot.PeriodId, LastSubmittedUtc = snapshot.FetchedAtUtc,
                    SubmittedThresholds = submitted.Concat(reached).Distinct().Order().ToArray(),
                    SubmittedSpendUsd = milestone ?? previousSpend
                }
            };
            var updated = _ledger with { Accounts = entries };
            // Persist only accepted submissions. A crash after submission and before this write can duplicate.
            // In-process disk failures retain pending state so retrying persistence does not resubmit.
            _pendingLedger = updated;
            await store.SaveAlertLedgerAsync(updated, cancellationToken).ConfigureAwait(false);
            _ledger = updated;
            _pendingLedger = null;
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAccountAsync(string accountKey, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ledger ??= (await store.LoadAlertLedgerAsync(cancellationToken).ConfigureAwait(false)).Value;
            var entries = new Dictionary<string, AlertLedgerEntry>((_pendingLedger ?? _ledger).Accounts, StringComparer.Ordinal);
            entries.Remove(accountKey);
            var updated = _ledger with { Accounts = entries };
            _pendingLedger = updated;
            await store.SaveAlertLedgerAsync(updated, cancellationToken).ConfigureAwait(false);
            _ledger = updated;
            _pendingLedger = null;
            _retries.Remove(accountKey);
        }
        finally { _gate.Release(); }
    }

    private async Task RecordFailureAsync(Account account, UsageSnapshot snapshot, SubmissionRetry? previous,
        CancellationToken cancellationToken)
    {
        int failures = previous?.PeriodId == snapshot.PeriodId ? Math.Min(previous.Failures + 1, 10) : 1;
        _retries[account.Key] = new(snapshot.PeriodId, failures,
            snapshot.FetchedAtUtc.AddSeconds(Math.Min(900, 30 * Math.Pow(2, failures - 1))));
        try { DiagnosticReported?.Invoke("Windows did not accept the notification; a bounded retry is deferred to a later sample."); }
        catch (Exception) { System.Diagnostics.Trace.TraceError("GHCPSpendTray notification diagnostic observer failed."); }
        await store.RecordDiagnosticAsync(DiagnosticCode.NotificationRejected, snapshot.FetchedAtUtc,
            account.Key, cancellationToken).ConfigureAwait(false);
    }

    private sealed record SubmissionRetry(string PeriodId, int Failures, DateTimeOffset NotBeforeUtc);
}
