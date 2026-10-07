using System.Diagnostics;
using System.Text.Json;
using GHCPSpendTray.Core;

namespace GHCPSpendTray.Prompt;

public sealed class PromptService(PromptOptions options, TimeProvider? timeProvider = null,
    Func<PromptOptions, string>? contextKey = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private string Key() => contextKey is null ? CredentialContext.Key(options) : contextKey(options);

    public PromptState ReadPrompt(Action<string> diagnostic, Action<string, string>? startWorker = null)
    {
        string key = Key();
        var store = new PromptStore(options, key);
        DateTimeOffset now = _time.GetUtcNow();
        PromptCache? cache = ReadCache(store, out bool invalid);
        RefreshLease? lease = ReadLease(store, out bool invalidLease);
        bool refreshing = lease?.IsActive(key, now) == true;
        PromptDiagnostic code = invalid || invalidLease ? PromptDiagnostic.Storage : cache?.Diagnostic ?? PromptDiagnostic.None;
        if ((cache is null || cache.NextAttemptUtc <= now) && !refreshing)
        {
            using RefreshGate? gate = store.TryLock();
            if (gate is null) refreshing = true;
            else
            {
                cache = ReadCache(store, out invalid);
                lease = ReadLease(store, out invalidLease);
                refreshing = lease?.IsActive(key, now) == true;
                if ((cache is null || cache.NextAttemptUtc <= now) && !refreshing)
                {
                    string nonce = Guid.NewGuid().ToString("N");
                    store.SaveLease(new(key, nonce, now, now + options.WorkerLifetime));
                    try
                    {
                        (startWorker ?? StartWorker)(key, nonce);
                        refreshing = true;
                    }
                    catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        code = PromptDiagnostic.Launch;
                        cache = Failure(key, now, now.AddMinutes(5), AccountStatus.StorageError, code);
                        store.Save(cache);
                        store.ClearLease(nonce);
                    }
                }
            }
        }
        if (!refreshing)
        {
            code = cache?.Diagnostic ?? code;
            store.Report(code, diagnostic);
        }
        return PromptState.Create(cache, now, options.Freshness, refreshing);
    }

    public void Refresh(string expectedContext, string nonce,
        Func<PromptOptions, CancellationToken, Task<TokenSet>>? credentials = null,
        Func<HttpClient>? createHttp = null)
    {
        var store = new PromptStore(options, expectedContext);
        using RefreshGate? gate = store.TryLock(TimeSpan.FromSeconds(5));
        if (gate is null) return;
        DateTimeOffset now = _time.GetUtcNow();
        PromptCache? existing = ReadCache(store, out _);
        RefreshLease? lease = ReadLease(store, out _);
        if (lease?.Nonce != nonce || lease.ContextKey != expectedContext) return;
        try
        {
            if (existing?.NextAttemptUtc > now) return;
            using var lifetime = new CancellationTokenSource(options.WorkerLifetime, _time);
            PromptCache cache;
            try
            {
                if (Key() != expectedContext)
                    throw new CredentialChangedException();
                TokenSet token;
                using (var discovery = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    discovery.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
                    token = (credentials ?? CredentialContext.ResolveAsync)(options, discovery.Token).GetAwaiter().GetResult();
                }
                using HttpClient http = createHttp?.Invoke() ?? CreateHttp();
                http.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
                GitHubIdentity identity = new GitHubIdentityClient(http, _time)
                    .GetAsync(HostResolver.Resolve(options.Hostname), token, lifetime.Token).GetAwaiter().GetResult();
                var account = new Account { Host = options.Hostname, UserId = identity.UserId, Login = identity.Login };
                UsageSnapshot snapshot = new CopilotUsageClient(http, _time)
                    .FetchAsync(account, token, lifetime.Token).GetAwaiter().GetResult();
                if (!BillingPeriods.IsCurrent(snapshot, _time.GetUtcNow()) ||
                    snapshot.SourceTimestampUtc > snapshot.FetchedAtUtc)
                    throw new ServiceException(AccountStatus.InvalidData, "The usage observation is invalid or expired.");
                if (Key() != expectedContext)
                    throw new CredentialChangedException();
                cache = new()
                {
                    ContextKey = expectedContext, Hostname = options.Hostname, AccountId = identity.UserId,
                    Status = AccountStatus.Fresh, Snapshot = snapshot,
                    AttemptedAtUtc = now, NextAttemptUtc = _time.GetUtcNow() + options.Freshness
                };
            }
            catch (Exception error) when (error is ServiceException or OperationCanceledException or HttpRequestException or
                IOException or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException or CredentialChangedException)
            {
                AccountStatus status = error is ServiceException service ? service.Status : AccountStatus.NetworkError;
                PromptDiagnostic code = error switch
                {
                    CredentialChangedException => PromptDiagnostic.CredentialsChanged,
                    OperationCanceledException => PromptDiagnostic.Timeout,
                    ServiceException serviceError => serviceError.Status switch
                    {
                        AccountStatus.SignInRequired => PromptDiagnostic.Authentication,
                        AccountStatus.Forbidden => PromptDiagnostic.AccessDenied,
                        AccountStatus.RateLimited => PromptDiagnostic.RateLimited,
                        AccountStatus.Unsupported => PromptDiagnostic.Unsupported,
                        AccountStatus.InvalidData => PromptDiagnostic.InvalidData,
                        _ => PromptDiagnostic.Network
                    },
                    _ => PromptDiagnostic.Network
                };
                DateTimeOffset retry = _time.GetUtcNow().AddMinutes(5);
                if (error is ServiceException { RetryAtUtc: { } serverRetry } && serverRetry > retry) retry = serverRetry;
                cache = Failure(expectedContext, now, retry, status, code);
            }
            store.Save(cache);
        }
        finally { store.ClearLease(nonce); }
    }

    private HttpClient CreateHttp() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds)
    });

    private void StartWorker(string key, string nonce)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("No helper executable path.");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add("refresh");
        info.ArgumentList.Add(key);
        info.ArgumentList.Add(nonce);
        foreach (string argument in options.Arguments()) info.ArgumentList.Add(argument);
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start background helper.");
        process.StandardInput.Close();
        // The worker publishes diagnostics to its cache, not inherited shell pipes.
        // Redirected streams keep command substitution from waiting for worker EOF.
    }

    private PromptCache Failure(string key, DateTimeOffset now, DateTimeOffset retry,
        AccountStatus status, PromptDiagnostic code) => new()
    {
        ContextKey = key, Hostname = options.Hostname, Status = status, Diagnostic = code,
        AttemptedAtUtc = now, NextAttemptUtc = retry
    };

    private static PromptCache? ReadCache(PromptStore store, out bool invalid)
    {
        invalid = false;
        try { return store.Read(); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException or OverflowException)
        { invalid = true; return null; }
    }

    private static RefreshLease? ReadLease(PromptStore store, out bool invalid)
    {
        invalid = false;
        try { return store.ReadLease(); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException)
        { invalid = true; return null; }
    }

    private sealed class CredentialChangedException : Exception;
}
