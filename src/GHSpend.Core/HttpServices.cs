using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace GHSpend.Core;

internal enum GitHubOperation { DeviceAuthorization, TokenExchange, TokenRefresh, Identity, Consumption }

public static class HttpTransport
{
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(20)
    }) { Timeout = TimeSpan.FromSeconds(60) };

    internal static HttpRequestMessage Request(HttpMethod method, Uri uri, string? token = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("GHSpend/1.0");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    internal static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type,
        CancellationToken cancellationToken)
    {
        const int limit = 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit)
            throw new ServiceException(AccountStatus.InvalidData, "Service response exceeded the size limit.");
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > limit)
                throw new ServiceException(AccountStatus.InvalidData, "Service response exceeded the size limit.");
            buffer.Write(chunk, 0, read);
        }
        try
        {
            return JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), type)
                ?? throw new ServiceException(AccountStatus.InvalidData, "Service returned an empty response.");
        }
        catch (JsonException)
        {
            throw new ServiceException(AccountStatus.InvalidData, "Service returned invalid or incompatible JSON.");
        }
    }

    internal static void EnsureSuccess(HttpResponseMessage response, DateTimeOffset now, GitHubOperation operation)
    {
        if (response.IsSuccessStatusCode) return;
        int code = (int)response.StatusCode;
        if (code is >= 300 and <= 399)
            throw new ServiceException(AccountStatus.Forbidden, "Redirect refused. Check the configured host.");
        DateTimeOffset? retry = response.Headers.RetryAfter?.Date;
        if (response.Headers.RetryAfter?.Delta is { } delta) retry = now + delta;
        bool exhausted = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) &&
            values.Contains("0");
        if (exhausted && response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) &&
            long.TryParse(resets.FirstOrDefault(), CultureInfo.InvariantCulture, out long seconds) &&
            seconds is >= 0 and <= 253402300799)
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (retry is null || reset > retry) retry = reset;
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests ||
            response.StatusCode == HttpStatusCode.Forbidden && (exhausted || retry is not null))
            throw new ServiceException(AccountStatus.RateLimited, "GitHub rate limit reached; refresh is deferred.",
                retry > now ? retry : now.AddMinutes(1));
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new ServiceException(AccountStatus.SignInRequired, "Sign in again with this account's approved OAuth app.");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new ServiceException(AccountStatus.Forbidden, response.Headers.Contains("X-GitHub-SSO")
                ? "Enterprise SSO authorization is required. Authorize the app with your organization."
                : "Access forbidden. Check app approval, scopes, enterprise policy, and IP restrictions.");
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
        {
            string detail = operation switch
            {
                GitHubOperation.DeviceAuthorization =>
                    "Device authorization could not start (POST /login/device/code). " +
                    "Check that this OAuth application is registered and approved on the selected host and that Device Flow is enabled. " +
                    "A github.com registration is not automatically valid on an enterprise host. " +
                    "Copilot consumption has not been checked.",
                GitHubOperation.TokenExchange =>
                    "OAuth token exchange is unavailable (POST /login/oauth/access_token). " +
                    "Check the host's OAuth registration, Device Flow support, and enterprise policy. " +
                    "Copilot consumption has not been checked.",
                GitHubOperation.TokenRefresh =>
                    "OAuth token refresh is unavailable (POST /login/oauth/access_token). " +
                    "Check the host's refresh support and OAuth application approval, then reconnect this account.",
                GitHubOperation.Identity =>
                    "Account identity could not be read (GET /user). " +
                    "Check the selected API host and account access. Copilot consumption has not been checked.",
                _ =>
                    "Copilot consumption is unavailable (GET /copilot_internal/user). " +
                    "The endpoint may be unsupported or inaccessible to this account or OAuth application."
            };
            throw new ServiceException(AccountStatus.Unsupported,
                $"HTTP {code.ToString(CultureInfo.InvariantCulture)}: {detail}");
        }
        throw new ServiceException(code >= 500 ? AccountStatus.NetworkError : AccountStatus.InvalidData,
            "GitHub request failed (HTTP " + code.ToString(CultureInfo.InvariantCulture) + ").", retry);
    }
}

public sealed record DeviceAuthorization(string DeviceCode, string UserCode, Uri VerificationUri,
    DateTimeOffset ExpiresAtUtc, int IntervalSeconds)
{
    public override string ToString() => "DeviceAuthorization [redacted]";
}
public sealed record GitHubIdentity(string UserId, string Login);

public sealed class DeviceFlowClient(HttpClient httpClient, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<DeviceAuthorization> BeginAsync(ResolvedHost host, string clientId,
        bool requestOfflineAccess = false, CancellationToken cancellationToken = default)
    {
        OAuthValidation.ValidateClientId(clientId);
        // read:user is a conservative starting scope, not a verified minimum for the undocumented quota API.
        using var request = HttpTransport.Request(HttpMethod.Post, host.AuthUri("login/device/code"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = requestOfflineAccess ? "read:user offline_access" : "read:user"
        });
        using HttpResponseMessage response = await httpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.BadRequest)
            HttpTransport.EnsureSuccess(response, _time.GetUtcNow(), GitHubOperation.DeviceAuthorization);
        DeviceWire wire = await HttpTransport.ReadAsync(response, WireJsonContext.Default.DeviceWire, cancellationToken).ConfigureAwait(false);
        if (wire.Error is not null) throw OAuthError(wire.Error);
        HttpTransport.EnsureSuccess(response, _time.GetUtcNow(), GitHubOperation.DeviceAuthorization);
        if (string.IsNullOrWhiteSpace(wire.DeviceCode) || string.IsNullOrWhiteSpace(wire.UserCode) ||
            wire.ExpiresIn is null or <= 0 or > 86400 || wire.Interval is <= 0 or > 3600 ||
            wire.VerificationUri is null)
            throw new ServiceException(AccountStatus.InvalidData, "Invalid device authorization response.");
        return new(wire.DeviceCode, wire.UserCode, host.ValidateVerificationUri(wire.VerificationUri),
            _time.GetUtcNow().AddSeconds(wire.ExpiresIn.Value), wire.Interval ?? 5);
    }

    public async Task<TokenSet> PollAsync(ResolvedHost host, string clientId,
        DeviceAuthorization authorization, CancellationToken cancellationToken = default)
    {
        OAuthValidation.ValidateClientId(clientId);
        _ = host.ValidateVerificationUri(authorization.VerificationUri.AbsoluteUri);
        int interval = Math.Clamp(authorization.IntervalSeconds, 1, 3600);
        while (_time.GetUtcNow() < authorization.ExpiresAtUtc)
        {
            TimeSpan delay = TimeSpan.FromSeconds(interval);
            if (_time.GetUtcNow() + delay >= authorization.ExpiresAtUtc)
            {
                await Task.Delay(authorization.ExpiresAtUtc - _time.GetUtcNow(), _time, cancellationToken).ConfigureAwait(false);
                break;
            }
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            TokenWire wire = await RequestTokenAsync(host, new()
            {
                ["client_id"] = clientId, ["device_code"] = authorization.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            }, GitHubOperation.TokenExchange, cancellationToken).ConfigureAwait(false);
            if (wire.Error == "authorization_pending") continue;
            if (wire.Error == "slow_down") { interval = Math.Min(interval + 5, 3600); continue; }
            return ConvertToken(wire);
        }
        throw new ServiceException(AccountStatus.SignInRequired, "Device authorization expired. Start sign-in again.");
    }

    public async Task<GitHubIdentity> GetIdentityAsync(ResolvedHost host, TokenSet tokens,
        CancellationToken cancellationToken = default)
    {
        tokens.Validate();
        using var request = HttpTransport.Request(HttpMethod.Get, host.ApiUri("user"), tokens.AccessToken);
        using HttpResponseMessage response = await httpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        HttpTransport.EnsureSuccess(response, _time.GetUtcNow(), GitHubOperation.Identity);
        IdentityWire wire = await HttpTransport.ReadAsync(response, WireJsonContext.Default.IdentityWire, cancellationToken).ConfigureAwait(false);
        if (wire.Id is null or <= 0 || string.IsNullOrWhiteSpace(wire.Login))
            throw new ServiceException(AccountStatus.InvalidData, "GitHub did not return a valid immutable identity.");
        return new(wire.Id.Value.ToString(CultureInfo.InvariantCulture), wire.Login);
    }

    public async Task<TokenSet> RefreshAsync(ResolvedHost host, string clientId, TokenSet tokens,
        CancellationToken cancellationToken = default)
    {
        OAuthValidation.ValidateClientId(clientId);
        tokens.Validate();
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken) || tokens.RefreshExpiresAtUtc <= _time.GetUtcNow())
            throw new ServiceException(AccountStatus.SignInRequired, "Refresh is unavailable or expired. Sign in again.");
        TokenWire wire = await RequestTokenAsync(host, new()
        {
            ["client_id"] = clientId, ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.RefreshToken
        }, GitHubOperation.TokenRefresh, cancellationToken).ConfigureAwait(false);
        TokenSet refreshed = ConvertToken(wire);
        // A host may rotate both tokens or leave the existing refresh token valid.
        return refreshed.RefreshToken is null ? refreshed with
        {
            RefreshToken = tokens.RefreshToken, RefreshExpiresAtUtc = tokens.RefreshExpiresAtUtc
        } : refreshed;
    }

    private async Task<TokenWire> RequestTokenAsync(ResolvedHost host, Dictionary<string, string> form,
        GitHubOperation operation, CancellationToken cancellationToken)
    {
        using var request = HttpTransport.Request(HttpMethod.Post, host.AuthUri("login/oauth/access_token"));
        request.Content = new FormUrlEncodedContent(form);
        using HttpResponseMessage response = await httpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.BadRequest)
            HttpTransport.EnsureSuccess(response, _time.GetUtcNow(), operation);
        TokenWire wire = await HttpTransport.ReadAsync(response, WireJsonContext.Default.TokenWire, cancellationToken).ConfigureAwait(false);
        if (wire.Error is null) HttpTransport.EnsureSuccess(response, _time.GetUtcNow(), operation);
        return wire;
    }

    private TokenSet ConvertToken(TokenWire wire)
    {
        if (wire.Error is not null) throw OAuthError(wire.Error);
        if (string.IsNullOrWhiteSpace(wire.AccessToken) ||
            !string.Equals(wire.TokenType, "bearer", StringComparison.OrdinalIgnoreCase) ||
            wire.ExpiresIn is <= 0 or > 315360000 || wire.RefreshTokenExpiresIn is <= 0 or > 315360000 ||
            wire.AccessToken.Any(char.IsWhiteSpace))
            throw new ServiceException(AccountStatus.InvalidData, "Invalid OAuth token response.");
        DateTimeOffset now = _time.GetUtcNow();
        var result = new TokenSet
        {
            AccessToken = wire.AccessToken, RefreshToken = wire.RefreshToken, Scope = wire.Scope,
            ExpiresAtUtc = wire.ExpiresIn is { } expiry ? now.AddSeconds(expiry) : null,
            RefreshExpiresAtUtc = wire.RefreshTokenExpiresIn is { } refreshExpiry ? now.AddSeconds(refreshExpiry) : null
        };
        result.Validate();
        return result;
    }

    private static ServiceException OAuthError(string error) => new(AccountStatus.SignInRequired, error switch
    {
        "access_denied" => "Authorization was denied.",
        "expired_token" => "Authorization expired. Start sign-in again.",
        "incorrect_client_credentials" or "invalid_client" => "The OAuth client ID is not valid on this host.",
        "device_flow_disabled" => "Device flow is disabled for this OAuth app.",
        "bad_refresh_token" or "invalid_grant" => "The refresh grant is invalid. Sign in again.",
        "unsupported_grant_type" => "This host does not support the requested OAuth grant.",
        _ => "OAuth authorization failed. Check the app registration and enterprise policy."
    });
}

public sealed class TokenManager(ICredentialStore credentialStore, DeviceFlowClient deviceFlowClient,
    TimeProvider? timeProvider = null)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<TokenSet> GetAsync(Account account, bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        account.Validate();
        string clientId = GitHubOAuth.ResolveClientId(account.Host);
        SemaphoreSlim gate = _locks.GetOrAdd(account.Key, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TokenSet? tokens;
            try { tokens = await credentialStore.ReadAsync(account, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw new ServiceException(AccountStatus.StorageError, "Credential Manager could not read this account's credentials."); }
            if (tokens is null || tokens.Version != 1 || string.IsNullOrWhiteSpace(tokens.AccessToken))
                throw new ServiceException(AccountStatus.SignInRequired, "No valid saved credentials. Sign in again.");
            tokens.Validate();
            if (!forceRefresh && (tokens.ExpiresAtUtc is null || tokens.ExpiresAtUtc > _time.GetUtcNow().AddMinutes(2)))
                return tokens;
            TokenSet refreshed = await deviceFlowClient.RefreshAsync(HostResolver.Resolve(account.Host),
                clientId, tokens, cancellationToken).ConfigureAwait(false);
            try { await credentialStore.WriteAsync(account, refreshed, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw new ServiceException(AccountStatus.StorageError, "Rotated credentials could not be saved. Reconnect this account."); }
            return refreshed;
        }
        finally { gate.Release(); }
    }
}

public interface ICopilotUsageProvider
{
    Task<UsageSnapshot> FetchAsync(Account account, CancellationToken cancellationToken = default);
}

public sealed class CopilotUsageProvider(HttpClient httpClient, TokenManager tokenManager,
    TimeProvider? timeProvider = null) : ICopilotUsageProvider
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<UsageSnapshot> FetchAsync(Account account, CancellationToken cancellationToken = default)
    {
        TokenSet tokens = await tokenManager.GetAsync(account, cancellationToken: cancellationToken).ConfigureAwait(false);
        try { return await FetchWithTokenAsync(account, tokens, cancellationToken).ConfigureAwait(false); }
        catch (ServiceException ex) when (ex.Status == AccountStatus.SignInRequired)
        {
            tokens = await tokenManager.GetAsync(account, forceRefresh: true, cancellationToken).ConfigureAwait(false);
            return await FetchWithTokenAsync(account, tokens, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<UsageSnapshot> FetchWithTokenAsync(Account account, TokenSet tokens,
        CancellationToken cancellationToken = default)
    {
        account.Validate();
        tokens.Validate();
        using var request = HttpTransport.Request(HttpMethod.Get,
            HostResolver.Resolve(account.Host).ApiUri("copilot_internal/user"), tokens.AccessToken);
        using HttpResponseMessage response = await httpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        HttpTransport.EnsureSuccess(response, _time.GetUtcNow(), GitHubOperation.Consumption);
        UsageWire wire = await HttpTransport.ReadAsync(response, WireJsonContext.Default.UsageWire, cancellationToken).ConfigureAwait(false);
        return Parse(wire, account.Key, _time.GetUtcNow());
    }

    public static UsageSnapshot ParseJson(string json, string accountKey, DateTimeOffset fetchedAtUtc)
    {
        try
        {
            UsageWire wire = JsonSerializer.Deserialize(json, WireJsonContext.Default.UsageWire)
                ?? throw new ServiceException(AccountStatus.InvalidData, "Missing usage response.");
            return Parse(wire, accountKey, fetchedAtUtc);
        }
        catch (JsonException) { throw new ServiceException(AccountStatus.InvalidData, "Invalid usage JSON schema."); }
    }

    private static UsageSnapshot Parse(UsageWire wire, string accountKey, DateTimeOffset fetched)
    {
        PremiumWire? quota = wire.QuotaSnapshots?.PremiumInteractions;
        if (quota is null || quota.TokenBasedBilling is null || quota.Unlimited is null ||
            quota.HasQuota is null || quota.CreditsUsed is null || quota.CreditsUsed < 0 || quota.Entitlement < 0)
            throw new ServiceException(AccountStatus.InvalidData, "Required quota fields are missing or invalid.");
        if (!quota.TokenBasedBilling.Value)
            throw new ServiceException(AccountStatus.Unsupported, "Dollar accounting is unsupported for premium-request billing.");
        // has_quota can be false after exhaustion; validated observed consumption is still meaningful.
        DateTimeOffset? reset = Timestamp(wire.QuotaResetDateUtc, "quota reset") ??
            Timestamp(wire.QuotaResetDate, "quota reset");
        DateTimeOffset? source = Timestamp(quota.TimestampUtc, "source timestamp");
        if (reset <= fetched)
            throw new ServiceException(AccountStatus.InvalidData, "The service returned an expired billing period.");
        try
        {
            var result = new UsageSnapshot
            {
                AccountKey = accountKey, FetchedAtUtc = fetched.ToUniversalTime(),
                SourceTimestampUtc = source, ResetAtUtc = reset,
                PeriodId = BillingPeriods.Resolve(fetched, reset),
                CreditsUsed = quota.CreditsUsed.Value, Entitlement = quota.Entitlement,
                Unlimited = quota.Unlimited.Value,
                ConsumptionUsd = CreditPolicy.ToUsd(quota.CreditsUsed.Value),
                AllocationUsd = quota.Entitlement is { } e ? CreditPolicy.ToUsd(e) : null,
                PercentConsumed = CreditPolicy.Percentage(quota.CreditsUsed.Value, quota.Entitlement, quota.Unlimited.Value)
            };
            result.Validate();
            return result;
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException)
        { throw new ServiceException(AccountStatus.InvalidData, "Quota values are outside the supported range."); }
    }

    private static DateTimeOffset? Timestamp(JsonElement element, string name)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (element.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParseExact(element.GetString(),
                ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long seconds) &&
            seconds is >= 0 and <= 253402300799)
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        throw new ServiceException(AccountStatus.InvalidData, "Invalid " + name + ".");
    }
}
