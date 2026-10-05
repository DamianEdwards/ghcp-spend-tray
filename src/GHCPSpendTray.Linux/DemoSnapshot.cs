using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GHCPSpendTray.Core;
using GHCPSpendTray.Shared;

namespace GHCPSpendTray.Linux;

public sealed record DemoAccount(string Key, string Name, string Login, string Host, decimal? Percent,
    string Consumption, string Allocation, string Freshness, string? UpdatedAt);

public sealed record DemoSnapshot(int Version, bool Demo, long Revision, string Style,
    double? Percent, string Indicator, string Consumption, string Status, string? UpdatedAt, DemoAccount[] Accounts)
{
    public static DemoSnapshot Empty { get; } = new(2, true, 0, "Pie", null, "?", "Unavailable", "No accounts", null, []);

    public static DemoSnapshot Create(DashboardView dashboard, TrayIconStyle style, long revision)
    {
        var tray = dashboard.Tray ?? TrayPresentation.Unavailable;
        var updates = dashboard.Accounts.Where(a => a.UpdatedAt.HasValue).Select(a => a.UpdatedAt!.Value).ToArray();
        return new(2, true, revision, style.ToString(), tray.RollUp.Percent, tray.RollUp.NumericText,
            Money(dashboard.ConsumptionUsd), dashboard.Status,
            updates.Length == 0 ? null : updates.Min().ToString("O", CultureInfo.InvariantCulture),
            dashboard.Accounts.Select(a => new DemoAccount(a.Key, a.Name, a.Login, a.Host, a.Percent,
                Money(a.ConsumptionUsd), Money(a.AllocationUsd), a.Freshness,
                a.UpdatedAt?.ToString("O", CultureInfo.InvariantCulture))).ToArray());
    }

    private static string Money(decimal? value) => value is null
        ? "Unavailable" : "$" + value.Value.ToString("0.00", CultureInfo.InvariantCulture);

    public string ToJson() => JsonSerializer.Serialize(this, DemoJsonContext.Default.DemoSnapshot);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DemoSnapshot))]
internal partial class DemoJsonContext : JsonSerializerContext;
