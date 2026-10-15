using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GHCPSpendTray.Core;
using GHCPSpendTray.Shared;

namespace GHCPSpendTray.Linux;

public sealed record DemoAccount(string Key, string Name, string Login, string Host, decimal? Percent,
    string Consumption, string Allocation, string Freshness, string? UpdatedAt,
    string DisplayName = "", string Thresholds = "", decimal? SpendIncrementUsd = null,
    bool ShowPeriodEstimate = false, string? ClientId = null, string? Message = null,
    AccountDiagnostics? Details = null, PeriodEstimate? PeriodEstimate = null, string? AvatarUri = null);

public sealed record SignInView(string Phase, string? Code = null, string? VerificationUri = null,
    DateTimeOffset? Expires = null, string? Message = null)
{
    public override string ToString() => "SignInView [redacted]";
}

public sealed record TrayImage(string? Key, string Name, string Tooltip, string ImageUri);
public sealed record TrayPreview(TrayPresentation Tray, TrayImage[] Icons)
{
    public static TrayPreview Create(TrayPresentation tray) => new(tray,
        tray.Icons.Select(icon => new TrayImage(icon.AccountKey, icon.Name, icon.Tooltip,
            TrayPixels.DataUri(icon, tray.Style))).ToArray());
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountRequest
{
    public required string Kind { get; init; }
    public string? Key { get; init; }
    public string? Host { get; init; }
    public string? ClientId { get; init; }
    public bool OfflineAccess { get; init; }
    public string? DisplayName { get; init; }
    public string? Thresholds { get; init; }
    public decimal? SpendIncrementUsd { get; init; }
    public bool? ShowPeriodEstimate { get; init; }
    public SettingsView? Settings { get; init; }
}

public sealed record DemoSnapshot(int Version, bool Demo, long Revision, string Style,
    double? Percent, string Indicator, string Consumption, string Status, string? UpdatedAt, DemoAccount[] Accounts,
    SettingsView? Settings = null, TrayPresentation? Tray = null, bool IsComplete = false, bool IsLastKnown = false,
    string? DataUri = null, string? StartupUri = null, TrayImage[]? Icons = null)
{
    public static DemoSnapshot Empty { get; } = new(4, false, 0, "Pie", null, "?", "Unavailable", "No accounts", null, []);

    public static DemoSnapshot Create(DashboardView dashboard, TrayIconStyle style, long revision)
    {
        var tray = dashboard.Tray ?? TrayPresentation.Unavailable;
        var updates = dashboard.Accounts.Where(a => a.UpdatedAt.HasValue).Select(a => a.UpdatedAt!.Value).ToArray();
        return new(4, false, revision, style.ToString(), tray.RollUp.Percent, tray.RollUp.NumericText,
            Money(dashboard.ConsumptionUsd), dashboard.Status,
            updates.Length == 0 ? null : updates.Min().ToString("O", CultureInfo.InvariantCulture),
            dashboard.Accounts.Select(a => new DemoAccount(a.Key, a.Name, a.Login, a.Host, a.Percent,
                Money(a.ConsumptionUsd), Money(a.AllocationUsd), a.Freshness,
                a.UpdatedAt?.ToString("O", CultureInfo.InvariantCulture), Message: a.Details.Message,
                Details: a.Details, PeriodEstimate: a.PeriodEstimate,
                AvatarUri: a.AvatarUrl is { } avatar && Path.IsPathFullyQualified(avatar)
                    ? new Uri(avatar).AbsoluteUri : null)).ToArray(),
            Tray: tray, IsComplete: dashboard.IsComplete, IsLastKnown: dashboard.IsLastKnown,
            Icons: TrayPreview.Create(tray).Icons);
    }

    private static string Money(decimal? value) => value is null
        ? "Unavailable" : "$" + value.Value.ToString("0.00", CultureInfo.InvariantCulture);

    public string ToJson() => JsonSerializer.Serialize(this, DemoJsonContext.Default.DemoSnapshot);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(DemoSnapshot))]
[JsonSerializable(typeof(SignInView))]
[JsonSerializable(typeof(AccountRequest))]
[JsonSerializable(typeof(TrayPreview))]
internal partial class DemoJsonContext : JsonSerializerContext;
