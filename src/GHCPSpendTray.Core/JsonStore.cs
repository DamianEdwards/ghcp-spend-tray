using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace GHCPSpendTray.Core;

public enum DiagnosticCode
{
    NetworkFailure, RateLimit, AuthenticationRequired, AccessDenied, UnsupportedCapability,
    InvalidUsage, StorageFailure, HistoryCorruption, NotificationRejected, RecoveryApplied
}

/// <summary>Versioned, application-owned storage. Tokens must only be persisted by ICredentialStore.</summary>
public sealed class JsonStore
{
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public event Action<string>? DiagnosticReported;
    public string RootPath => _root;

    public JsonStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async Task<StoreLoadResult<AppSettings>> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadAsync("config.json", CoreJsonContext.Default.AppSettings, () => new AppSettings(),
                s => s.Validate(), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveAsync("config.json", settings, CoreJsonContext.Default.AppSettings,
                s => s.Validate(), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<StoreLoadResult<AlertLedger>> LoadAlertLedgerAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadAsync("state.json", CoreJsonContext.Default.AlertLedger, () => new AlertLedger(),
                l => l.Validate(), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAlertLedgerAsync(AlertLedger ledger, CancellationToken cancellationToken = default)
    {
        ledger.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveAsync("state.json", ledger, CoreJsonContext.Default.AlertLedger,
                l => l.Validate(), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public string HistoryDirectory(string accountKey) =>
        Path.Combine(_root, "history", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountKey))).ToLowerInvariant());

    public async Task AppendHistoryAsync(UsageSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        snapshot.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string directory = HistoryDirectory(snapshot.AccountKey);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, snapshot.FetchedAtUtc.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl");
            await RepairTailAsync(path, cancellationToken).ConfigureAwait(false);
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, CoreJsonContext.Default.UsageSnapshot) + "\n");
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        finally { _gate.Release(); }
    }

    public async Task<StoreLoadResult<UsageSnapshot[]>> LoadHistoryAsync(string accountKey,
        DateTimeOffset sinceUtc, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var diagnostics = new List<string>();
            var snapshots = new List<UsageSnapshot>();
            string directory = HistoryDirectory(accountKey);
            if (!Directory.Exists(directory)) return new([], []);
            foreach (string file in Directory.EnumerateFiles(directory, "*.jsonl").Order(StringComparer.Ordinal))
            {
                if (!TryMonth(file, out DateTimeOffset month))
                {
                    AddDiagnostic(diagnostics, "Unrecognized history filename was not loaded.");
                    continue;
                }
                if (month.AddMonths(1) <= sinceUtc) continue;
                StoreLoadResult<UsageSnapshot[]> loaded = await ReadHistoryFileAsync(file, accountKey, cancellationToken).ConfigureAwait(false);
                snapshots.AddRange(loaded.Value.Where(s => s.FetchedAtUtc >= sinceUtc));
                foreach (string diagnostic in loaded.Diagnostics) AddDiagnostic(diagnostics, diagnostic);
            }
            return new(snapshots.OrderBy(s => s.FetchedAtUtc).ToArray(), diagnostics.ToArray());
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAccountHistoryAsync(string accountKey, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string directory = HistoryDirectory(accountKey);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Only fixed codes and hashed identity are logged; arbitrary service messages are never persisted.</summary>
    public async Task RecordDiagnosticAsync(DiagnosticCode code, DateTimeOffset atUtc,
        string? accountKey = null, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(code)) throw new ArgumentOutOfRangeException(nameof(code));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string directory = Path.Combine(_root, "logs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "diagnostics.log");
            if (File.Exists(path) && new FileInfo(path).Length >= 128 * 1024)
                File.Move(path, path + ".1", overwrite: true);
            string identity = accountKey is null ? "-" :
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountKey)))[..16];
            string line = atUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + " " +
                code + " " + identity + "\n";
            await File.AppendAllTextAsync(path, line, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Call at scheduled maintenance, not on each UI refresh. Corrupt boundary files are preserved.</summary>
    public async Task<string[]> MaintainHistoryAsync(DateTimeOffset now, int retentionDays = 90,
        CancellationToken cancellationToken = default)
    {
        if (retentionDays is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string root = Path.Combine(_root, "history");
            if (!Directory.Exists(root)) return [];
            var diagnostics = new List<string>();
            DateTimeOffset cutoff = now.AddDays(-retentionDays);
            foreach (string file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryMonth(file, out DateTimeOffset month)) continue;
                if (month.AddMonths(1) <= cutoff) { File.Delete(file); continue; }
                if (month > cutoff) continue;
                StoreLoadResult<UsageSnapshot[]> loaded = await ReadHistoryFileAsync(file, null, cancellationToken).ConfigureAwait(false);
                if (loaded.Diagnostics.Length != 0)
                {
                    AddDiagnostic(diagnostics, "Retention preserved a corrupt boundary-month file for recovery.");
                    continue;
                }
                UsageSnapshot[] retained = loaded.Value.Where(s => s.FetchedAtUtc >= cutoff).ToArray();
                if (retained.Length == loaded.Value.Length) continue;
                string staging = file + ".maintenance";
                try
                {
                    await using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None,
                        4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        foreach (UsageSnapshot snapshot in retained)
                        {
                            byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, CoreJsonContext.Default.UsageSnapshot) + "\n");
                            await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
                        }
                        stream.Flush(true);
                    }
                    File.Move(staging, file, overwrite: true);
                }
                finally { if (File.Exists(staging)) File.Delete(staging); }
            }
            return diagnostics.ToArray();
        }
        finally { _gate.Release(); }
    }

    private async Task<StoreLoadResult<T>> LoadAsync<T>(string name, JsonTypeInfo<T> type, Func<T> defaults,
        Action<T> validate, CancellationToken cancellationToken)
    {
        string path = Path.Combine(_root, name), backup = path + ".bak";
        if (!File.Exists(path) && !File.Exists(backup)) return new(defaults(), []);
        try
        {
            T value = await ReadDocumentAsync(path, type, validate, cancellationToken).ConfigureAwait(false);
            return new(value, []);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException or FileNotFoundException)
        {
            T recovered;
            try { recovered = await ReadDocumentAsync(backup, type, validate, cancellationToken).ConfigureAwait(false); }
            catch (Exception backupError) when (backupError is JsonException or ArgumentException or IOException)
            { throw new InvalidDataException("The primary " + name + " and its recovery copy are unavailable or invalid. Existing files were preserved."); }
            if (File.Exists(path)) File.Copy(path, path + ".corrupt", overwrite: true);
            await WriteAtomicAsync(path, recovered, type, makeBackup: false, cancellationToken).ConfigureAwait(false);
            string diagnostic = "Recovered " + name + " from its recovery copy; damaged data was preserved.";
            Report(diagnostic);
            return new(recovered, [diagnostic]);
        }
    }

    private static async Task<T> ReadDocumentAsync<T>(string path, JsonTypeInfo<T> type, Action<T> validate,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Document is too large.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        T value = await JsonSerializer.DeserializeAsync(stream, type, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Document was empty.");
        validate(value);
        return value;
    }

    private async Task SaveAsync<T>(string name, T value, JsonTypeInfo<T> type, Action<T> validate,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(_root, name);
        if (!File.Exists(path) && File.Exists(path + ".bak"))
        {
            T recovered = await ReadDocumentAsync(path + ".bak", type, validate, cancellationToken).ConfigureAwait(false);
            await WriteAtomicAsync(path, recovered, type, makeBackup: false, cancellationToken).ConfigureAwait(false);
        }
        // Never replace a valid recovery copy with an unchecked damaged primary.
        if (File.Exists(path))
            _ = await ReadDocumentAsync(path, type, validate, cancellationToken).ConfigureAwait(false);
        await WriteAtomicAsync(path, value, type, makeBackup: true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAtomicAsync<T>(string path, T value, JsonTypeInfo<T> type,
        bool makeBackup, CancellationToken cancellationToken)
    {
        string staging = path + ".new";
        try
        {
            await using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, type, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path) && makeBackup) File.Replace(staging, path, path + ".bak");
            else
            {
                File.Move(staging, path, overwrite: true);
                if (makeBackup) File.Copy(path, path + ".bak", overwrite: true);
            }
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private static async Task<StoreLoadResult<UsageSnapshot[]>> ReadHistoryFileAsync(string path,
        string? accountKey, CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        var snapshots = new List<UsageSnapshot>();
        if (new FileInfo(path).Length > 32 * 1024 * 1024)
            return new([], ["History file exceeds the 32 MiB safety limit; it was preserved but not loaded."]);
        using var reader = new StreamReader(path, new UTF8Encoding(false, true));
        int number = 0;
        try
        {
            string? pending = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            while (pending is { } line)
            {
                pending = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                number++;
                try
                {
                    if (line.Length > 65536) throw new InvalidDataException();
                    UsageSnapshot snapshot = JsonSerializer.Deserialize(line, CoreJsonContext.Default.UsageSnapshot)
                        ?? throw new InvalidDataException();
                    snapshot.Validate();
                    if (accountKey is not null && snapshot.AccountKey != accountKey) throw new InvalidDataException();
                    if (!TryMonth(path, out DateTimeOffset month) ||
                        snapshot.FetchedAtUtc < month || snapshot.FetchedAtUtc >= month.AddMonths(1))
                        throw new InvalidDataException();
                    snapshots.Add(snapshot);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException or OverflowException)
                {
                    AddDiagnostic(diagnostics, pending is null && !EndsWithNewline(path)
                        ? "Crash-truncated final history record detected; it will be quarantined before the next append."
                        : "History corruption at line " + number.ToString(CultureInfo.InvariantCulture) + "; record preserved and not loaded.");
                }
            }
        }
        catch (DecoderFallbackException)
        {
            AddDiagnostic(diagnostics, "History contains invalid UTF-8. The file was preserved; unread records were not loaded.");
        }
        return new(snapshots.ToArray(), diagnostics.ToArray());
    }

    private async Task RepairTailAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0 || EndsWithNewline(path)) return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        long end = stream.Length, start = end;
        while (start > 0)
        {
            stream.Position = --start;
            if (stream.ReadByte() == '\n') { start++; break; }
            if (end - start > 65536)
                throw new InvalidDataException("History tail exceeds the record safety limit; manual recovery is required.");
        }
        byte[] tail = new byte[checked((int)(end - start))];
        stream.Position = start;
        stream.ReadExactly(tail);
        bool valid;
        try
        {
            UsageSnapshot sample = JsonSerializer.Deserialize(tail, CoreJsonContext.Default.UsageSnapshot)
                ?? throw new JsonException();
            sample.Validate();
            valid = true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or OverflowException) { valid = false; }
        if (valid)
        {
            stream.Position = end;
            stream.WriteByte((byte)'\n');
        }
        else
        {
            await File.WriteAllBytesAsync(path + ".truncated", tail, cancellationToken).ConfigureAwait(false);
            stream.SetLength(start);
            Report("A crash-truncated final history record was quarantined before appending.");
        }
        stream.Flush(true);
    }

    private static bool EndsWithNewline(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0) return true;
        stream.Position = stream.Length - 1;
        return stream.ReadByte() == '\n';
    }

    private static bool TryMonth(string path, out DateTimeOffset month) =>
        DateTimeOffset.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out month) &&
        (month.Year != 9999 || month.Month != 12);

    private static void AddDiagnostic(List<string> diagnostics, string message)
    {
        if (diagnostics.Count < 100) diagnostics.Add(message);
        else if (diagnostics.Count == 100) diagnostics.Add("Additional corruption diagnostics were suppressed.");
    }

    private void Report(string message)
    {
        try { DiagnosticReported?.Invoke(message); }
        catch (Exception) { System.Diagnostics.Trace.TraceError("GHCPSpendTray storage diagnostic observer failed."); }
    }
}
