using System.Globalization;

namespace GHCPSpendTray.Core;

public enum TrayIconStyle { Pie, Percentage }
public enum TrayDisplayMode { RollUp, PerAccount }

public sealed record TrayAccountUsage(string Key, string Name, string Host, double? Percent, string? Exclusion,
    bool IsUnlimited = false);

public sealed record TrayIndicator(string? AccountKey, string Name, double? Percent,
    int IncludedAccounts, int SelectedAccounts, string Details, string Tooltip, bool IsUnlimited = false)
{
    public bool IsPartial => IncludedAccounts > 0 && IncludedAccounts < SelectedAccounts;
    public bool IsOverAllocation => Percent > 100;
    public string ValueText => IsUnlimited ? "Unlimited allocation" :
        Percent is { } value ? FormatPercent(value) : "Unavailable";
    public string NumericText => IsUnlimited ? "\u221e" : Percent switch
    {
        null => "?",
        > 0 and < 1 => "<1",
        > 999 => "999",
        { } value => Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)
    };

    public static string FormatPercent(double value) => value switch
    {
        > 0 and < .01 => "<0.01%",
        >= 1e12 => value.ToString("G17", CultureInfo.InvariantCulture) + "%",
        _ => value.ToString("0.##", CultureInfo.InvariantCulture) + "%"
    };
}

public sealed record TrayPresentation(TrayIconStyle Style, TrayIndicator RollUp,
    IReadOnlyList<TrayIndicator> Icons, IReadOnlyList<TrayAccountUsage> Accounts)
{
    public static TrayPresentation Unavailable { get; } = TrayUsage.Create(new(), [], DateTimeOffset.UtcNow);
}

// This intentionally does not use UsageAggregation.Total: last-known dollar values
// remain useful, but are not eligible for a current allocation percentage.
public static class TrayUsage
{
    public static TrayPresentation Create(AppSettings settings, IEnumerable<AccountState> states, DateTimeOffset now)
    {
        var byKey = states.ToDictionary(s => s.Account.Key, StringComparer.Ordinal);
        var selected = settings.Accounts.Where(a => !a.ExcludeFromTray).ToArray();
        var accounts = new List<TrayAccountUsage>();
        double used = 0, allocation = 0;
        decimal exactUsed = 0, exactAllocation = 0;
        bool exactTotals = true;
        foreach (var account in selected)
        {
            byKey.TryGetValue(account.Key, out var state);
            string? exclusion = Exclusion(state, now, TimeSpan.FromMinutes(settings.PollIntervalMinutes));
            bool isUnlimited = exclusion is null && state!.Snapshot!.Unlimited;
            double? percent = null;
            if (exclusion is null && !isUnlimited)
            {
                var snapshot = state!.Snapshot!;
                // Keep normal accounting exact (e.g. 0.1 + 0.2 == 0.3), while
                // retaining finite display values if aggregate decimal sums overflow.
                double accountAllocation = (double)snapshot.AllocationUsd!.Value;
                double accountUsed = (double)snapshot.ConsumptionUsd;
                used += accountUsed;
                allocation += accountAllocation;
                if (exactTotals)
                {
                    try
                    {
                        exactUsed += snapshot.ConsumptionUsd;
                        exactAllocation += snapshot.AllocationUsd.Value;
                    }
                    catch (OverflowException) { exactTotals = false; }
                }
                percent = Percentage(snapshot.ConsumptionUsd, snapshot.AllocationUsd.Value);
            }
            accounts.Add(new(account.Key, account.DisplayName ?? account.Login, account.Host, percent,
                isUnlimited ? "unlimited allocation" : exclusion, isUnlimited));
        }
        int included = accounts.Count(a => a.Percent is not null);
        int unlimited = accounts.Count(a => a.IsUnlimited);
        bool unlimitedRollUp = included == 0 && unlimited > 0;
        string reasons = string.Join("; ", accounts.Where(a => a.Exclusion is not null &&
                !(unlimitedRollUp && a.IsUnlimited))
            .GroupBy(a => a.Exclusion).Select(g => $"{g.Count()} {g.Key}"));
        string details = selected.Length == 0
            ? settings.Accounts.Length == 0 ? "No connected accounts." : "No accounts selected."
            : string.Join("\n", accounts.Select(a =>
                $"{a.Name} ({a.Host}): {AccountValue(a)}"));
        var rollUp = Indicator(null, "GHCPSpendTray", included == 0 ? null :
            exactTotals ? Percentage(exactUsed, exactAllocation) : used / allocation * 100,
            unlimitedRollUp ? unlimited : included, selected.Length, details, reasons,
            unlimited: unlimitedRollUp);
        var icons = settings.TrayMode == TrayDisplayMode.PerAccount && accounts.Count > 0
            ? accounts.Select(a => Indicator(a.Key, a.Name, a.Percent, a.Percent is not null || a.IsUnlimited ? 1 : 0, 1,
                $"{a.Name} ({a.Host}): {AccountValue(a)}",
                a.IsUnlimited ? "" : a.Exclusion ?? "", a.Host, a.IsUnlimited)).ToArray()
            : [rollUp];
        return new(settings.TrayStyle, rollUp, icons, accounts);
    }

    private static string AccountValue(TrayAccountUsage account) => account.IsUnlimited
        ? "Unlimited allocation" : account.Exclusion ?? TrayIndicator.FormatPercent(account.Percent!.Value);

    private static double Percentage(decimal used, decimal allocation)
    {
        try
        {
            decimal percentage = used / allocation * 100m;
            return percentage != 0 || used == 0 ? (double)percentage : (double)used / (double)allocation * 100;
        }
        catch (OverflowException) { return (double)used / (double)allocation * 100; }
    }

    private static string? Exclusion(AccountState? state, DateTimeOffset now, TimeSpan freshness)
    {
        if (state is null) return "awaiting data";
        if (state.Status != AccountStatus.Fresh)
            return state.Status switch
            {
                AccountStatus.Pending => "awaiting data",
                AccountStatus.Stale => "stale",
                AccountStatus.SignInRequired => "sign-in required",
                AccountStatus.Unsupported => "unsupported",
                AccountStatus.InvalidData => "invalid data",
                AccountStatus.Forbidden => "access denied",
                AccountStatus.RateLimited => "rate limited",
                AccountStatus.StorageError => "storage error",
                _ => "refresh failed"
            };
        if (state.Snapshot is not { } snapshot) return "awaiting data";
        try { snapshot.Validate(); }
        catch (Exception ex) when (ex is ArgumentException or OverflowException) { return "invalid data"; }
        if (snapshot.AccountKey != state.Account.Key) return "invalid identity";
        if (!BillingPeriods.IsCurrent(snapshot, now)) return "outside current period";
        if (now - snapshot.FetchedAtUtc > freshness) return "stale";
        if (snapshot.Unlimited) return null;
        if (snapshot.AllocationUsd is null) return "unknown allocation";
        if (snapshot.AllocationUsd <= 0) return "zero allocation";
        return null;
    }

    private static TrayIndicator Indicator(string? key, string name, double? percent, int included,
        int selected, string details, string reasons, string? host = null, bool unlimited = false)
    {
        string value = unlimited ? "Unlimited allocation" :
            percent is { } p ? TrayIndicator.FormatPercent(p) : "Unavailable";
        string qualifier = included > 0 && included < selected ? "Partial ! | " : "";
        string over = percent > 100 ? " | Over allocation" : "";
        string counts = $"{included}/{selected} included";
        // Put truth qualifiers before potentially long user-controlled account names.
        string tooltip = $"{qualifier}{value}{over} | {counts}";
        if (reasons.Length > 0) tooltip += "\n" + reasons;
        tooltip += "\n" + name + (host is null ? "" : " (" + host + ")");
        if (selected == 0) tooltip += "\n" + details;
        return new(key, name, percent, included, selected,
            $"{qualifier}{value}{over} | {counts}\n{details}", tooltip, unlimited);
    }
}
