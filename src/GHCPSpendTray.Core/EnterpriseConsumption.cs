using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;

namespace GHCPSpendTray.Core;

public sealed record EnterpriseIdentity(string Alias, string TenantId, string ObjectId, string? Username = null)
{
    public bool IsSameAccount(EnterpriseIdentity? other) => other is not null &&
        Guid.TryParse(TenantId, out var tenant) && Guid.TryParse(other.TenantId, out var otherTenant) && tenant == otherTenant &&
        Guid.TryParse(ObjectId, out var id) && Guid.TryParse(other.ObjectId, out var otherId) && id == otherId;

    public void Validate()
    {
        if (string.IsNullOrEmpty(Alias) || Alias.Length > 128 || Alias.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_') ||
            !Guid.TryParse(TenantId, out _) || !Guid.TryParse(ObjectId, out _))
            throw new ArgumentException("Invalid corporate identity.");
    }
}

public sealed record EnterpriseToken(string AccessToken, EnterpriseIdentity Identity, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "EnterpriseToken [redacted]";
}
public interface IEnterpriseTokenProvider
{
    bool IsAvailable { get; }
    Task<EnterpriseToken> GetAsync(bool forceRefresh, CancellationToken cancellationToken);
}
public sealed class EnterpriseAccessException(string message, bool authenticationFailed = false, bool cliMissing = false) : Exception(message)
{
    public bool AuthenticationFailed { get; } = authenticationFailed;
    public bool CliMissing { get; } = cliMissing;
}
public sealed record DailySpend(string Date, decimal AmountUsd, bool Estimated, bool Derived = false);
public sealed record DailyConsumption(string StartDate, string EndDate, DateTimeOffset AsOf, DailyReportedSpend[] Days);
public sealed record DailyReportedSpend(string Date, decimal? AmountUsd);
public enum EnterpriseSetupState { Ready, CliMissing, SignInRequired, ConfirmIdentity, Connected, Error }
public sealed record EnterpriseConsumptionView(bool Eligible, string Message, EnterpriseIdentity? PendingIdentity = null,
    bool Connected = false, DailySpend[]? Bars = null, string? ChartMessage = null,
    EnterpriseSetupState State = EnterpriseSetupState.Ready, string? ConnectedUsername = null, decimal[]? AxisTicks = null,
    bool IsLoading = false);

public static class DailyConsumptionChart
{
    private static bool IsEsme(UsageSnapshot snapshot) =>
        snapshot.AccountKey.StartsWith("msft.ghe.com:", StringComparison.OrdinalIgnoreCase) && snapshot.EnterpriseIds.Length > 0;

    public static string[] EnterpriseNames(UsageSnapshot snapshot) => IsEsme(snapshot) ? ["proxima:msft.ghe.com"] :
        !snapshot.AccountKey.StartsWith("github.com:", StringComparison.OrdinalIgnoreCase) ? [] : snapshot.EnterpriseIds
        .Where(id => id is 1578 or 134482 or 13213).Select(id => id switch
        {
            1578 => "dotcom:microsoftopensource",
            13213 => "emu:microsoft",
            _ => "dotcom:microsoftcopilot"
        })
        .Distinct().Order(StringComparer.Ordinal).ToArray();

    public static bool HasCompleteEnterpriseScope(UsageSnapshot snapshot) =>
        EnterpriseNames(snapshot).Length > 0 &&
        (IsEsme(snapshot) || snapshot.EnterpriseIds.All(id => id is 1578 or 134482 or 13213));

    public static (DailySpend[] Bars, string? Message) Create(DailyConsumption data, UsageSnapshot quota,
        DateTimeOffset now, bool fresh)
    {
        var observed = (quota.SourceTimestampUtc ?? quota.FetchedAtUtc).ToUniversalTime();
        var start = new DateOnly(observed.Year, observed.Month, 1);
        var today = DateOnly.FromDateTime(observed.UtcDateTime);
        if (!fresh || !BillingPeriods.IsCurrent(quota, now) || observed > now ||
            data.AsOf > now.AddMinutes(5) || DateOnly.ParseExact(data.StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture) != start ||
            quota.ResetAtUtc is { } reset && reset != new DateTimeOffset(start.AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))
            return ([], "Daily consumption cannot be reconciled with this usage observation.");
        var days = data.Days.Where(d => DateOnly.ParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture) <= today).ToArray();
        var reported = days.Where(d => d.AmountUsd is not null).ToArray();
        var bars = reported.Select(d => new DailySpend(d.Date, d.AmountUsd!.Value, false)).ToList();
        if (reported.Length == 0) return ([], "Daily usage is connected, but no daily reports are available for this account yet.");
        if (!HasCompleteEnterpriseScope(quota))
            return (bars.ToArray(), "Reported days cover only the supported enterprises; this account's broader usage cannot be used to estimate the remainder.");
        var last = DateOnly.ParseExact(reported[^1].Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        bool contiguous = Enumerable.Range(0, last.DayNumber - start.DayNumber + 1)
            .All(i => days.Any(d => d.Date == start.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) && d.AmountUsd is not null));
        var remainder = quota.ConsumptionUsd - reported.Sum(d => d.AmountUsd!.Value);
        if (!contiguous || remainder < 0)
            return (bars.ToArray(), "Reported days are shown without estimates: coverage is incomplete or totals differ.");
        int gap = today.DayNumber - last.DayNumber;
        if (gap == 0) return (bars.ToArray(), remainder == 0 ? null : "The latest reported day does not yet reconcile with current consumption.");
        if (gap == 1 && today == DateOnly.FromDateTime(now.UtcDateTime))
        {
            bars.Add(new(today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), remainder, false, true));
            return (bars.ToArray(), "Today's consumption is the current usage less all reported prior days.");
        }
        for (int i = 1; i <= gap; i++)
            bars.Add(new(last.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                i == gap ? remainder - remainder / gap * (gap - 1) : remainder / gap, true));
        return (bars.ToArray(), "Estimated bars evenly distribute the unreported remainder; they are not daily measurements.");
    }
}

public static class EnterpriseConsumptionEndpoint
{
    public static Uri? BuildTimeBaseUri => ParseBaseUri(typeof(EnterpriseConsumptionEndpoint).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .SingleOrDefault(a => a.Key == "EnterpriseConsumptionApiUrl")?.Value);

    public static Uri? ParseBaseUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("The build-time consumption API URL must be an absolute HTTPS base URL without credentials, query, or fragment.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
}

public sealed class EnterpriseConsumptionClient
{
    private readonly HttpClient _http;
    private readonly Uri? _baseUri;
    public bool IsConfigured => _baseUri is not null;

    public EnterpriseConsumptionClient(HttpClient http, Uri? baseUri = null)
    {
        _http = http;
        _baseUri = baseUri is null ? EnterpriseConsumptionEndpoint.BuildTimeBaseUri :
            EnterpriseConsumptionEndpoint.ParseBaseUri(baseUri.AbsoluteUri);
    }

    public async Task<DailyConsumption> GetAsync(EnterpriseToken token, UsageSnapshot quota, CancellationToken cancellationToken)
    {
        if (_baseUri is null) throw new EnterpriseAccessException("Corporate consumption is not configured in this build.");
        token.Identity.Validate();
        var now = (quota.SourceTimestampUtc ?? quota.FetchedAtUtc).ToUniversalTime();
        string start = new DateOnly(now.Year, now.Month, 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string end = DateOnly.FromDateTime(now.UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (start == end) throw new EnterpriseAccessException("Daily reports are available after the first UTC day of the month.");
        var uri = new Uri(_baseUri,
            Uri.EscapeDataString(token.Identity.Alias) + "/consumption?api-version=2019-10-01&group-by=day&start-date=" + start + "&end-date=" + end);
        using var request = HttpTransport.Request(HttpMethod.Get, uri, token.AccessToken);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new EnterpriseAccessException("Corporate authentication failed. Run az login and reconnect daily consumption.", true);
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new EnterpriseAccessException("This corporate identity does not have consumption-read access. Contact the portal administrator.");
        if (!response.IsSuccessStatusCode)
            throw new EnterpriseAccessException($"Daily consumption request failed (HTTP {(int)response.StatusCode}). Retry from account management.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > 1024 * 1024) throw new EnterpriseAccessException("Daily consumption response exceeded the size limit.");
            buffer.Write(chunk, 0, read);
        }
        return Parse(buffer.ToArray(), token.Identity.Alias, DailyConsumptionChart.EnterpriseNames(quota), start, end);
    }

    public static DailyConsumption Parse(byte[] json, string alias, string[] enterprises, string start, string end)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var coverage = root.GetProperty("coverage");
            if (enterprises.Length == 0 || root.GetProperty("user").GetProperty("aadAlias").GetString() != alias ||
                root.GetProperty("groupBy").GetString() != "day" || root.GetProperty("startDate").GetString() != start ||
                root.GetProperty("endDate").GetString() != end || coverage.GetProperty("currency").GetString() != "USD" ||
                coverage.GetProperty("timezone").GetString() != "UTC" ||
                coverage.GetProperty("measure").GetString() != "daily-ai-credit-report-usage")
                throw new EnterpriseAccessException("Daily consumption returned an incompatible identity, period, or measure.");
            var days = new List<DailyReportedSpend>();
            var first = DateOnly.ParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var limit = DateOnly.ParseExact(end, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (limit <= first || limit.DayNumber - first.DayNumber > 32) throw new FormatException();
            foreach (var day in root.GetProperty("days").EnumerateArray())
            {
                string date = day.GetProperty("date").GetString() ?? throw new FormatException();
                var parsed = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (parsed < first || parsed >= limit || days.Any(d => d.Date == date)) throw new FormatException();
                decimal total = 0;
                bool complete = true;
                var results = day.GetProperty("results").EnumerateArray().ToArray();
                foreach (string enterprise in enterprises)
                {
                    var matches = results.Where(r => r.GetProperty("enterpriseId").GetString() == enterprise).ToArray();
                    if (matches.Length > 1) throw new FormatException();
                    if (matches.Length == 0 || matches[0].GetProperty("status").GetString() != "available" ||
                        matches[0].GetProperty("usage").ValueKind == JsonValueKind.Null)
                    { complete = false; continue; }
                    decimal value = matches[0].GetProperty("usage").GetProperty("grossAmountUsd").GetDecimal();
                    if (value < 0) throw new FormatException();
                    total = checked(total + value);
                }
                days.Add(new(date, complete ? total : null));
            }
            return new(start, end, root.GetProperty("asOf").GetDateTimeOffset(), days.OrderBy(d => d.Date, StringComparer.Ordinal).ToArray());
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw new EnterpriseAccessException("Daily consumption response is invalid or incompatible."); }
    }
}
