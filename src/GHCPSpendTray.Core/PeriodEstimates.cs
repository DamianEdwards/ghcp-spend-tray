namespace GHCPSpendTray.Core;

public sealed record PeriodEstimate(decimal? EstimatedConsumptionUsd = null,
    decimal? AverageDailyConsumptionUsd = null, decimal? OverAllocationUsd = null,
    DateTimeOffset? PeriodStartUtc = null, DateTimeOffset? ResetAtUtc = null,
    DateTimeOffset? ObservedAtUtc = null, bool IsEarly = false, string? UnavailableReason = null,
    string? AverageStartDate = null, string? AverageEndDate = null, int ReportedDayCount = 0);

public static class PeriodEstimates
{
    private static readonly TimeSpan SourceClockSkewTolerance = TimeSpan.FromMinutes(1);

    public static PeriodEstimate? Create(AccountState state, DateTimeOffset now, TimeSpan freshness,
        DailyConsumption? dailyConsumption = null)
    {
        if (!state.Account.ShowPeriodEstimate) return null;
        if (state.Snapshot is not { } snapshot)
            return new(UnavailableReason: "Awaiting consumption data.");
        try { snapshot.Validate(); }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            return new(UnavailableReason: "Consumption data is invalid.");
        }
        if (snapshot.AccountKey != state.Account.Key)
            return new(UnavailableReason: "Consumption belongs to a different account.");
        if (!BillingPeriods.IsCurrent(snapshot, now))
            return new(UnavailableReason: "Awaiting current-period consumption.");
        if (state.Status != AccountStatus.Fresh)
            return new(UnavailableReason: state.Status == AccountStatus.Stale
                ? "Consumption observation is stale."
                : "Current consumption is unavailable. Refresh this account.");
        if (now - snapshot.FetchedAtUtc > freshness)
            return new(UnavailableReason: "Consumption observation is stale.");

        var fetched = snapshot.FetchedAtUtc.UtcDateTime;
        var start = new DateTimeOffset(fetched.Year, fetched.Month, 1, 0, 0, 0, TimeSpan.Zero);
        if (start.Year == 9999 && start.Month == 12)
            return new(UnavailableReason: "Billing period exceeds the supported date range.");
        var reset = start.AddMonths(1);
        var estimate = new PeriodEstimate(PeriodStartUtc: start, ResetAtUtc: reset);
        if (snapshot.ResetAtUtc is { } supplied && supplied != reset)
            return estimate with { UnavailableReason = "Billing period differs from a UTC calendar month." };

        var observed = snapshot.SourceTimestampUtc ?? snapshot.FetchedAtUtc;
        // Tolerate minor server/client clock skew without projecting from a future observation.
        if (observed > snapshot.FetchedAtUtc &&
            observed - snapshot.FetchedAtUtc <= SourceClockSkewTolerance)
            observed = snapshot.FetchedAtUtc;
        estimate = estimate with { ObservedAtUtc = observed.ToUniversalTime() };
        if (observed < start || observed > snapshot.FetchedAtUtc)
            return estimate with { UnavailableReason = "Usage observation time is outside the supported period." };
        if (now - observed > freshness)
            return estimate with { UnavailableReason = "Consumption observation is stale." };
        long elapsedTicks = (observed - start).Ticks;
        if (elapsedTicks < TimeSpan.TicksPerDay)
            return estimate with { UnavailableReason = "At least 24 hours of this period must elapse." };
        try
        {
            decimal elapsedDays = (decimal)elapsedTicks / TimeSpan.TicksPerDay;
            decimal periodDays = (decimal)(reset - start).Ticks / TimeSpan.TicksPerDay;
            decimal daily = snapshot.ConsumptionUsd / elapsedDays;
            decimal projected;
            if (RecentAverage(dailyConsumption, snapshot, start, observed, now, freshness) is { } recent)
            {
                daily = recent.Average;
                decimal remainingDays = (decimal)(reset - observed).Ticks / TimeSpan.TicksPerDay;
                projected = snapshot.ConsumptionUsd + daily * remainingDays;
                estimate = estimate with
                {
                    AverageStartDate = recent.Start, AverageEndDate = recent.End, ReportedDayCount = recent.Count
                };
            }
            else projected = snapshot.ConsumptionUsd * periodDays / elapsedDays;
            decimal? allocation = UsageBudget.Allocation(state.Account, snapshot);
            decimal? over = allocation is > 0
                ? Math.Max(0, projected - allocation.Value) : null;
            return estimate with
            {
                EstimatedConsumptionUsd = projected, AverageDailyConsumptionUsd = daily,
                OverAllocationUsd = over, IsEarly = elapsedTicks < 3 * TimeSpan.TicksPerDay
            };
        }
        catch (OverflowException)
        {
            return estimate with { UnavailableReason = "Estimate exceeds the supported amount range." };
        }
    }

    private static (decimal Average, string Start, string End, int Count)? RecentAverage(
        DailyConsumption? data, UsageSnapshot snapshot, DateTimeOffset periodStart, DateTimeOffset observed,
        DateTimeOffset now, TimeSpan freshness)
    {
        if (data is null || !DailyConsumptionChart.HasCompleteEnterpriseScope(snapshot) ||
            data.AsOf > now.AddMinutes(5) || now - data.AsOf > freshness + TimeSpan.FromMinutes(15))
            return null;
        var start = DateOnly.FromDateTime(periodStart.UtcDateTime);
        var today = DateOnly.FromDateTime(observed.UtcDateTime);
        if (!DateOnly.TryParseExact(data.StartDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var responseStart) || responseStart != start ||
            !DateOnly.TryParseExact(data.EndDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var responseEnd) || responseEnd > today || responseEnd <= start)
            return null;
        var days = new Dictionary<DateOnly, decimal?>();
        foreach (var day in data.Days)
        {
            if (!DateOnly.TryParseExact(day.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var date) ||
                date < start || date >= responseEnd || day.AmountUsd < 0 || !days.TryAdd(date, day.AmountUsd))
                return null;
        }
        var reported = days.Where(d => d.Value is not null).ToArray();
        if (reported.Length == 0) return null;
        var last = reported.Max(d => d.Key);
        // Daily reports may lag; do not move the averaging window arbitrarily far back.
        if (today.DayNumber - last.DayNumber > 3) return null;
        var first = last.AddDays(-Math.Min(6, last.DayNumber - start.DayNumber));
        int count = last.DayNumber - first.DayNumber + 1;
        decimal sum = 0;
        try
        {
            if (reported.Sum(d => d.Value!.Value) > snapshot.ConsumptionUsd) return null;
            for (var date = first; date <= last; date = date.AddDays(1))
            {
                if (!days.TryGetValue(date, out var amount) || amount is null) return null;
                sum += amount.Value;
            }
        }
        catch (OverflowException) { return null; }
        return (sum / count, first.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            last.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), count);
    }
}
