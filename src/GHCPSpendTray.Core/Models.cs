using System.Globalization;
using System.Text.Json.Serialization;

namespace GHCPSpendTray.Core;

public sealed record Account
{
    public string Host { get; init; } = "github.com";
    public string UserId { get; init; } = "";
    public string Login { get; init; } = "";
    public string? OAuthClientId { get; init; }
    public string? DisplayName { get; init; }
    public string? AvatarUrl { get; init; }
    public decimal[]? ThresholdOverrides { get; init; }
    public decimal? SpendIncrementUsd { get; init; }
    public string Key => HostResolver.Resolve(Host).Host + ":" + UserId;

    public void Validate()
    {
        _ = HostResolver.Resolve(Host);
        if (OAuthClientId is not null)
            _ = GitHubOAuth.ResolveClientId(Host, OAuthClientId);
        if (string.IsNullOrWhiteSpace(UserId) || UserId.StartsWith('0') || UserId.Any(c => !char.IsAsciiDigit(c)) ||
            !UserId.Any(c => c != '0') || string.IsNullOrWhiteSpace(Login))
            throw new ArgumentException("A verified numeric user ID and login are required.");
        if (ThresholdOverrides is not null)
            AppSettings.ValidateThresholds(ThresholdOverrides);
        AppSettings.ValidateSpendIncrement(SpendIncrementUsd);
    }
}

public static class AccountAvatar
{
    public static string? Resolve(Account account)
    {
        ResolvedHost host = HostResolver.Resolve(account.Host);
        return Validate(host, account.AvatarUrl) ??
            (host.Kind == HostKind.GitHub && account.UserId.Length is > 0 and <= 20 &&
             account.UserId.All(char.IsAsciiDigit) && account.UserId.Any(c => c != '0')
                ? "https://avatars.githubusercontent.com/u/" + account.UserId
                : null);
    }

    public static string? Validate(ResolvedHost host, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
            return null;
        bool sameHost = uri.IdnHost.Equals(host.WebBaseUri.IdnHost, StringComparison.OrdinalIgnoreCase) &&
            uri.Port == host.WebBaseUri.Port;
        bool sameApi = uri.IdnHost.Equals(host.ApiBaseUri.IdnHost, StringComparison.OrdinalIgnoreCase) &&
            uri.Port == host.ApiBaseUri.Port;
        bool githubAvatars = host.Kind == HostKind.GitHub &&
            uri.IdnHost.Equals("avatars.githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
            uri.IsDefaultPort;
        bool enterpriseAvatars = host.Kind == HostKind.EnterpriseCloud &&
            uri.IdnHost.Equals("avatars." + host.WebBaseUri.IdnHost, StringComparison.OrdinalIgnoreCase) &&
            uri.IsDefaultPort;
        return sameHost || sameApi || githubAvatars || enterpriseAvatars ? uri.AbsoluteUri : null;
    }
}

public sealed record AppSettings
{
    // Setters preserve omitted-field defaults with .NET 10's generated JSON reader.
    // MonitorService takes detached copies at its settings boundary.
    [JsonRequired] public int Version { get; set; } = 1;
    public int PollIntervalMinutes { get; set; } = 60;
    public bool NotificationsEnabled { get; set; } = true;
    public decimal[] AlertThresholds { get; set; } = [50m, 80m, 100m];
    public decimal? SpendIncrementUsd { get; set; }
    public Account[] Accounts { get; set; } = [];
    public bool RequestOfflineAccess { get; set; }
    public int HistoryRetentionDays { get; set; } = 90;

    public void Validate()
    {
        if (Version != 1) throw new ArgumentException("Unsupported settings version.");
        if (PollIntervalMinutes is < 5 or > 1440)
            throw new ArgumentException("Polling interval must be between 5 and 1440 minutes.");
        if (HistoryRetentionDays is < 1 or > 3650)
            throw new ArgumentException("History retention must be between 1 and 3650 days.");
        ValidateThresholds(AlertThresholds);
        ValidateSpendIncrement(SpendIncrementUsd);
        ArgumentNullException.ThrowIfNull(Accounts);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Account account in Accounts)
        {
            ArgumentNullException.ThrowIfNull(account);
            account.Validate();
            if (!keys.Add(account.Key)) throw new ArgumentException("Duplicate account identity.");
        }
    }

    public static void ValidateThresholds(decimal[] thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        if (thresholds.Length > 100 || thresholds.Any(t => t <= 0) ||
            !thresholds.SequenceEqual(thresholds.Distinct().Order()))
            throw new ArgumentException("Thresholds must be sorted, distinct positive percentages (at most 100).");
    }

    public static void ValidateSpendIncrement(decimal? increment)
    {
        if (increment is < 0 || increment is { } amount && decimal.Round(amount, 2) != amount)
            throw new ArgumentException("Spend increments must be nonnegative USD amounts with at most two decimal places. Zero disables the alert.");
    }
}

public sealed record TokenSet
{
    [JsonRequired] public int Version { get; init; } = 1;
    [JsonRequired] public string AccessToken { get; init; } = "";
    public string? RefreshToken { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public DateTimeOffset? RefreshExpiresAtUtc { get; init; }
    public string? Scope { get; init; }
    public override string ToString() => "TokenSet [redacted]";
    public void Validate()
    {
        if (Version != 1 || string.IsNullOrWhiteSpace(AccessToken) || AccessToken.Length > 16384 ||
            AccessToken.Any(c => c < 33 || c > 126) ||
            RefreshToken is { } refresh && (refresh.Length is 0 or > 16384 || refresh.Any(c => c < 33 || c > 126)))
            throw new ServiceException(AccountStatus.SignInRequired, "Saved credentials are invalid. Sign in again.");
    }
}

public interface ICredentialStore
{
    Task<TokenSet?> ReadAsync(Account account, CancellationToken cancellationToken = default);
    Task WriteAsync(Account account, TokenSet tokens, CancellationToken cancellationToken = default);
    Task DeleteAsync(Account account, CancellationToken cancellationToken = default);
}

public sealed record UsageSnapshot
{
    [JsonRequired] public string AccountKey { get; init; } = "";
    [JsonRequired] public DateTimeOffset FetchedAtUtc { get; init; }
    public DateTimeOffset? SourceTimestampUtc { get; init; }
    [JsonRequired] public string PeriodId { get; init; } = "";
    public DateTimeOffset? ResetAtUtc { get; init; }
    [JsonRequired] public decimal CreditsUsed { get; init; }
    public decimal? Entitlement { get; init; }
    [JsonRequired] public bool Unlimited { get; init; }
    [JsonRequired] public decimal ConsumptionUsd { get; init; }
    public decimal? AllocationUsd { get; init; }
    public decimal? PercentConsumed { get; init; }
    [JsonRequired] public int ConversionPolicyVersion { get; init; } = 1;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AccountKey) || string.IsNullOrWhiteSpace(PeriodId) ||
            FetchedAtUtc == default || ConversionPolicyVersion != CreditPolicy.Version ||
            CreditsUsed < 0 || Entitlement < 0 || ConsumptionUsd != CreditPolicy.ToUsd(CreditsUsed) ||
            AllocationUsd != (Entitlement is { } e ? CreditPolicy.ToUsd(e) : null) ||
            PercentConsumed != CreditPolicy.Percentage(CreditsUsed, Entitlement, Unlimited))
            throw new ArgumentException("Invalid persisted usage snapshot.");
        if (ResetAtUtc is { } reset && (reset <= FetchedAtUtc ||
            PeriodId != BillingPeriods.Resolve(FetchedAtUtc, reset)))
            throw new ArgumentException("Invalid billing reset.");
        if (ResetAtUtc is null && PeriodId != BillingPeriods.Resolve(FetchedAtUtc, null))
            throw new ArgumentException("Invalid calendar billing period.");
    }
}

/// <summary>
/// Provider policy v1: 100 token-based AI credits equal USD 1 of consumption value.
/// This is not a promise about invoiced charges; premium request counts are never converted.
/// </summary>
public static class CreditPolicy
{
    public const int Version = 1;
    public const decimal CreditsPerUsd = 100m;
    public static decimal ToUsd(decimal credits) => credits >= 0 ? credits / CreditsPerUsd :
        throw new ArgumentOutOfRangeException(nameof(credits));
    public static decimal? Percentage(decimal credits, decimal? entitlement, bool unlimited)
    {
        if (credits < 0 || entitlement < 0) throw new ArgumentOutOfRangeException(nameof(credits));
        return !unlimited && entitlement > 0 ? checked(credits / entitlement.Value * 100m) : null;
    }
}

public static class BillingPeriods
{
    public static string Resolve(DateTimeOffset fetchedAtUtc, DateTimeOffset? resetAtUtc) =>
        resetAtUtc is { } reset
            ? "reset:" + reset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : "calendar:" + fetchedAtUtc.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static bool IsCurrent(UsageSnapshot snapshot, DateTimeOffset now) =>
        snapshot.FetchedAtUtc <= now && (snapshot.ResetAtUtc is { } reset
            ? now < reset
            : snapshot.PeriodId == Resolve(now, null));
}

public enum AccountStatus
{
    Pending, Fresh, Stale, SignInRequired, Forbidden, RateLimited, Unsupported,
    InvalidData, NetworkError, StorageError
}

public sealed record AccountState
{
    public required Account Account { get; init; }
    public AccountStatus Status { get; init; }
    public UsageSnapshot? Snapshot { get; init; }
    public string? Diagnostic { get; init; }
    public DateTimeOffset? NextRefreshUtc { get; init; }
    public DateTimeOffset? LastAttemptUtc { get; init; }
}

public sealed class ServiceException : Exception
{
    public AccountStatus Status { get; }
    public DateTimeOffset? RetryAtUtc { get; }
    public ServiceException(AccountStatus status, string safeMessage, DateTimeOffset? retryAtUtc = null)
        : base(safeMessage) { Status = status; RetryAtUtc = retryAtUtc; }
}

public sealed record StoreLoadResult<T>(T Value, string[] Diagnostics);

public sealed record UsageTotal(decimal ConsumptionUsd, int IncludedAccounts, int TotalAccounts,
    bool IsComplete, bool IsLastKnown);

public static class UsageAggregation
{
    public static UsageTotal Total(IEnumerable<AccountState> states, DateTimeOffset now,
        TimeSpan freshness)
    {
        AccountState[] all = states.ToArray();
        AccountState[] included = all.Where(s => s.Snapshot is { } v && BillingPeriods.IsCurrent(v, now)).ToArray();
        bool stale = included.Any(s => s.Status != AccountStatus.Fresh ||
            now - s.Snapshot!.FetchedAtUtc > freshness);
        return new(included.Sum(s => s.Snapshot!.ConsumptionUsd), included.Length, all.Length,
            included.Length == all.Length && !stale, stale);
    }
}
