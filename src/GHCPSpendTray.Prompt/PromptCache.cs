using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GHCPSpendTray.Core;

namespace GHCPSpendTray.Prompt;

public enum PromptDiagnostic
{
    None, Authentication, AccessDenied, RateLimited, Unsupported, InvalidData,
    Network, Timeout, CredentialsChanged, Storage, Launch
}

public sealed record PromptCache
{
    [JsonRequired] public int Version { get; init; } = 1;
    public required string ContextKey { get; init; }
    public required string Hostname { get; init; }
    public string? AccountId { get; init; }
    public AccountStatus Status { get; init; }
    public UsageSnapshot? Snapshot { get; init; }
    public required DateTimeOffset AttemptedAtUtc { get; init; }
    public required DateTimeOffset NextAttemptUtc { get; init; }
    public PromptDiagnostic Diagnostic { get; init; }

    public void Validate(string context, string host)
    {
        if (Version != 1 || ContextKey != context || Hostname != host ||
            !Enum.IsDefined(Status) || !Enum.IsDefined(Diagnostic) ||
            AttemptedAtUtc == default || NextAttemptUtc < AttemptedAtUtc)
            throw new InvalidDataException("Invalid prompt cache identity or schedule.");
        if (Status == AccountStatus.Fresh)
        {
            var account = new Account { Host = host, UserId = AccountId ?? "", Login = "prompt" };
            account.Validate();
            if (Snapshot is null || Snapshot.AccountKey != account.Key || Diagnostic != PromptDiagnostic.None)
                throw new InvalidDataException("Invalid prompt cache observation.");
            Snapshot.Validate();
        }
        else if (Snapshot is not null || Diagnostic == PromptDiagnostic.None)
            throw new InvalidDataException("Failed prompt caches must not publish consumption.");
    }
}

public sealed record RefreshLease(string ContextKey, string Nonce, DateTimeOffset StartedAtUtc, DateTimeOffset ExpiresAtUtc)
{
    public bool IsActive(string key, DateTimeOffset now) =>
        ContextKey == key && Nonce is { Length: 32 } && Nonce.All(char.IsAsciiHexDigit) &&
        StartedAtUtc <= now && now < ExpiresAtUtc && ExpiresAtUtc - StartedAtUtc <= TimeSpan.FromMinutes(4);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PromptCache))]
[JsonSerializable(typeof(RefreshLease))]
public partial class PromptJsonContext : JsonSerializerContext;

public sealed class PromptStore(PromptOptions options, string contextKey)
{
    public string CachePath { get; } = Path.Combine(options.CacheDirectory, contextKey + ".json");
    public string LeasePath => CachePath + ".refresh";
    private string DiagnosticPath => CachePath + ".diagnostic";

    public RefreshGate? TryLock(TimeSpan? wait = null) => RefreshGate.TryAcquire(options.CacheDirectory, contextKey, wait);

    public PromptCache? Read()
    {
        byte[]? bytes = ReadBounded(CachePath);
        if (bytes is null) return null;
        var cache = JsonSerializer.Deserialize(bytes, PromptJsonContext.Default.PromptCache) ??
            throw new InvalidDataException("Empty prompt cache.");
        cache.Validate(contextKey, options.Hostname);
        return cache;
    }

    public RefreshLease? ReadLease()
    {
        byte[]? bytes = ReadBounded(LeasePath, 2048);
        return bytes is null ? null : JsonSerializer.Deserialize(bytes, PromptJsonContext.Default.RefreshLease);
    }

    public void Save(PromptCache cache)
    {
        cache.Validate(contextKey, options.Hostname);
        WriteAtomic(CachePath, JsonSerializer.SerializeToUtf8Bytes(cache, PromptJsonContext.Default.PromptCache));
    }

    public void SaveLease(RefreshLease lease) =>
        WriteAtomic(LeasePath, JsonSerializer.SerializeToUtf8Bytes(lease, PromptJsonContext.Default.RefreshLease));

    public void ClearLease(string nonce)
    {
        if (ReadLease()?.Nonce == nonce) File.Delete(LeasePath);
    }

    public void Report(PromptDiagnostic diagnostic, Action<string> output)
    {
        using RefreshGate? gate = TryLock();
        if (gate is null) return;
        if (diagnostic == PromptDiagnostic.None)
        {
            if (File.Exists(DiagnosticPath)) File.Delete(DiagnosticPath);
            return;
        }
        string marker = diagnostic.ToString();
        byte[]? previous = ReadBounded(DiagnosticPath, 128);
        if (previous is not null && Encoding.UTF8.GetString(previous) == marker) return;
        WriteAtomic(DiagnosticPath, Encoding.UTF8.GetBytes(marker));
        output(Message(diagnostic));
    }

    public static string Message(PromptDiagnostic diagnostic) => diagnostic switch
    {
        PromptDiagnostic.Authentication => "Copilot: existing gh credentials could not authenticate. Check gh in this shell and host.",
        PromptDiagnostic.AccessDenied => "Copilot: access denied. Check account permissions, application approval and SSO.",
        PromptDiagnostic.RateLimited => "Copilot: GitHub rate limit reached; refresh is deferred.",
        PromptDiagnostic.Unsupported => "Copilot: this account, host or billing mode does not support token-based consumption.",
        PromptDiagnostic.InvalidData => "Copilot: invalid, expired or incompatible quota data was rejected.",
        PromptDiagnostic.Network => "Copilot: the GitHub request failed. A bounded retry is scheduled.",
        PromptDiagnostic.Timeout => "Copilot: credential discovery or the HTTP request timed out.",
        PromptDiagnostic.CredentialsChanged => "Copilot: credential context changed during refresh; the result was discarded.",
        PromptDiagnostic.Storage => "Copilot: cache data could not be read or written. Check the dedicated cache directory.",
        PromptDiagnostic.Launch => "Copilot: the background helper could not start. Check its installation.",
        _ => ""
    };

    private static byte[]? ReadBounded(string path, int limit = 65536)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > limit) throw new InvalidDataException("Prompt cache exceeds its size limit.");
                byte[] bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
                return bytes;
            }
            catch (FileNotFoundException) when (attempt < 2) { Thread.Sleep(1); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var creation = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) creation.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(staging, creation))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(staging, path, overwrite: true);
                    break;
                }
                catch (IOException error) when (OperatingSystem.IsWindows() && attempt < 4 && (error.HResult & 0xffff) is 32 or 33)
                { Thread.Sleep(2); }
                catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows() && attempt < 4)
                { Thread.Sleep(2); }
            }
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}

public sealed class RefreshGate : IDisposable
{
    private readonly FileStream _stream;
    private RefreshGate(FileStream stream) => _stream = stream;

    public static RefreshGate? TryAcquire(string directory, string context, TimeSpan? wait = null)
    {
        string root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
            else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var creation = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) creation.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                return new RefreshGate(new FileStream(Path.Combine(root, context + ".lock"), creation));
            }
            // Unix FileShare contention reports raw EWOULDBLOCK: 35 on macOS, 11 on Linux.
            catch (IOException error) when (OperatingSystem.IsWindows()
                ? (error.HResult & 0xffff) is 32 or 33
                : error.HResult == (OperatingSystem.IsMacOS() ? 35 : 11))
            {
                if (timer.Elapsed >= (wait ?? TimeSpan.Zero)) return null;
                Thread.Sleep(10);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
