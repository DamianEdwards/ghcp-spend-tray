using System.Text;
using GHCPSpendTray.Core;

namespace GHCPSpendTray.Tests;

internal static partial class Program
{
    private static async Task EnterpriseConsumptionTests()
    {
        await Test("Consumption endpoint configuration validates HTTPS and normalizes the base route", () =>
        {
            Equal(null, EnterpriseConsumptionEndpoint.ParseBaseUri(null));
            Equal(null, EnterpriseConsumptionEndpoint.ParseBaseUri(""));
            Equal("https://usage.example.invalid/base/", EnterpriseConsumptionEndpoint.ParseBaseUri("https://usage.example.invalid/base")!.AbsoluteUri);
            foreach (var invalid in new[] { "http://usage.example.invalid/", "https://user:password@usage.example.invalid/",
                "https://usage.example.invalid/?q=value", "https://usage.example.invalid/#fragment", "https://usage.example.invalid:8443/" })
                Throws<ArgumentException>(() => EnterpriseConsumptionEndpoint.ParseBaseUri(invalid));
        });
        await Test("Consumption URL is embedded at build time, not read from the runtime environment", () =>
        {
            var configured = EnterpriseConsumptionEndpoint.BuildTimeBaseUri;
            var expected = Environment.GetEnvironmentVariable("GHCP_TEST_EXPECTED_API_URL");
            if (expected is not null)
                Equal(expected == "disabled" ? null : EnterpriseConsumptionEndpoint.ParseBaseUri(expected), configured);
            var previous = Environment.GetEnvironmentVariable("GHCP_CONSUMPTION_API_URL");
            try
            {
                Environment.SetEnvironmentVariable("GHCP_CONSUMPTION_API_URL", "https://runtime.example.invalid/ignored/");
                Equal(configured, EnterpriseConsumptionEndpoint.BuildTimeBaseUri);
            }
            finally { Environment.SetEnvironmentVariable("GHCP_CONSUMPTION_API_URL", previous); }
        });
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var quota = new UsageSnapshot
        {
            AccountKey = "github.com:42", FetchedAtUtc = now, SourceTimestampUtc = now,
            PeriodId = "calendar:2026-10", CreditsUsed = 1000, ConsumptionUsd = 10,
            EnterpriseIds = [1578, 134482]
        };
        var data = new DailyConsumption("2026-10-01", "2026-10-05", now,
            [new("2026-10-01", 1), new("2026-10-02", 2)]);
        await Test("Enterprise eligibility is allowlisted and deduplicated", () =>
        {
            Equal(2, DailyConsumptionChart.EnterpriseNames(quota with { EnterpriseIds = [1578, 1578, 134482, 999] }).Length);
            Equal(0, DailyConsumptionChart.EnterpriseNames(quota with { EnterpriseIds = [] }).Length);
            Equal("emu:microsoft", DailyConsumptionChart.EnterpriseNames(quota with { EnterpriseIds = [13213] }).Single());
            Equal(3, DailyConsumptionChart.EnterpriseNames(quota with { EnterpriseIds = [1578, 134482, 13213] }).Length);
            Equal("proxima:msft.ghe.com", DailyConsumptionChart.EnterpriseNames(quota with
            { AccountKey = "msft.ghe.com:42", EnterpriseIds = [999] }).Single());
            Equal(0, DailyConsumptionChart.EnterpriseNames(quota with
            { AccountKey = "msft.ghe.com.attacker.example:42", EnterpriseIds = [999] }).Length);
            Equal(0, DailyConsumptionChart.EnterpriseNames(quota with
            { AccountKey = "msft.ghe.com:42", EnterpriseIds = [] }).Length);
            Equal(0, DailyConsumptionChart.EnterpriseNames(quota with
            { AccountKey = "other.example:42", EnterpriseIds = [1578] }).Length);
        });
        await Test("Daily chart distributes only the trailing remainder", () =>
        {
            var chart = DailyConsumptionChart.Create(data, quota, now, true);
            Equal(5, chart.Bars.Length);
            Equal(10m, chart.Bars.Sum(b => b.AmountUsd));
            Equal(3, chart.Bars.Count(b => b.Estimated));
            Equal(7m / 3, chart.Bars[2].AmountUsd);
        });
        await Test("Daily chart never fills internal missing days or negative remainders", () =>
        {
            var missing = data with { Days = [new("2026-10-01", 1), new("2026-10-02", null), new("2026-10-03", 2)] };
            True(DailyConsumptionChart.Create(missing, quota, now, true).Bars.All(b => !b.Estimated));
            True(DailyConsumptionChart.Create(data, quota with { ConsumptionUsd = 2 }, now, true).Bars.All(b => !b.Estimated));
            True(DailyConsumptionChart.Create(data, quota with { EnterpriseIds = [1578, 999] }, now, true).Bars.All(b => !b.Estimated));
        });
        await Test("Only today's missing amount is derived, not estimated", () =>
        {
            var priorDays = data with { Days = [new("2026-10-01", 1), new("2026-10-02", 2),
                new("2026-10-03", 1), new("2026-10-04", 2)] };
            var chart = DailyConsumptionChart.Create(priorDays, quota, now, true);
            Equal(5, chart.Bars.Length);
            Equal(4m, chart.Bars[^1].AmountUsd);
            True(chart.Bars[^1].Derived && !chart.Bars[^1].Estimated);
            Equal(10m, chart.Bars.Sum(b => b.AmountUsd));
            var previousDayQuota = quota with { SourceTimestampUtc = now.AddDays(-1) };
            var earlier = data with { Days = [new("2026-10-01", 1), new("2026-10-02", 2), new("2026-10-03", 1)] };
            True(DailyConsumptionChart.Create(earlier, previousDayQuota, now, true).Bars[^1].Estimated);
        });
        await Test("Chart axes use a zero baseline, nice ticks, and bounded large values", () =>
        {
            foreach (decimal max in new[] { 0m, 0.03m, 1m, 273.18m, 10000m, decimal.MaxValue })
            {
                var ticks = DailyChartPresentation.Ticks([new("2026-10-01", max, false)]);
                Equal(0m, ticks[0]);
                True(ticks[^1] >= max && ticks.Length is >= 2 and <= 6);
                True(ticks.Zip(ticks.Skip(1)).All(pair => pair.Second > pair.First));
            }
        });
        await Test("Chart dates use regional formatting without converting the UTC day", () =>
        {
            Equal("10/3/2026", DailyChartPresentation.DateLabel("2026-10-03", System.Globalization.CultureInfo.GetCultureInfo("en-US")));
            Equal("03/10/2026", DailyChartPresentation.DateLabel("2026-10-03", System.Globalization.CultureInfo.GetCultureInfo("en-GB")));
            True(DailyChartPresentation.Tooltip(new("2026-10-03", 1, false, true)).Contains("USD - Today"));
        });
        await Test("Daily chart rejects stale and mismatched periods", () =>
        {
            Equal(0, DailyConsumptionChart.Create(data, quota, now, false).Bars.Length);
            Equal(0, DailyConsumptionChart.Create(data with { StartDate = "2026-09-01" }, quota, now, true).Bars.Length);
            Equal(0, DailyConsumptionChart.Create(data, quota with { ResetAtUtc = now.AddDays(20) }, now, true).Bars.Length);
        });
        const string json = """
        {"user":{"aadAlias":"fixture"},"groupBy":"day","startDate":"2026-10-01","endDate":"2026-10-05",
        "asOf":"2026-10-05T12:00:00Z","coverage":{"measure":"daily-ai-credit-report-usage","timezone":"UTC","currency":"USD"},
        "days":[{"date":"2026-10-01","results":[
          {"enterpriseId":"dotcom:microsoftopensource","status":"available","usage":{"grossAmountUsd":3}},
          {"enterpriseId":"proxima:example.ghe.com","status":"available","usage":{"grossAmountUsd":999}}]}]}
        """;
        await Test("Daily parser scopes totals rather than using cross-enterprise aggregate", () =>
        {
            var parsed = EnterpriseConsumptionClient.Parse(Encoding.UTF8.GetBytes(json), "fixture",
                ["dotcom:microsoftopensource"], "2026-10-01", "2026-10-05");
            Equal(3m, parsed.Days[0].AmountUsd);
            var incomplete = EnterpriseConsumptionClient.Parse(Encoding.UTF8.GetBytes(json), "fixture",
                ["dotcom:microsoftopensource", "dotcom:microsoftcopilot"], "2026-10-01", "2026-10-05");
            Equal(null, incomplete.Days[0].AmountUsd);
            Throws<EnterpriseAccessException>(() => EnterpriseConsumptionClient.Parse(Encoding.UTF8.GetBytes(json),
                "wrong-user", ["dotcom:microsoftopensource"], "2026-10-01", "2026-10-05"));
        });
        await Test("EMU daily consumption is scoped separately from dotcom", () =>
        {
            var emuJson = json.Replace("dotcom:microsoftopensource", "emu:microsoft");
            var parsed = EnterpriseConsumptionClient.Parse(Encoding.UTF8.GetBytes(emuJson), "fixture",
                DailyConsumptionChart.EnterpriseNames(quota with { EnterpriseIds = [13213] }), "2026-10-01", "2026-10-05");
            Equal(3m, parsed.Days[0].AmountUsd);
            True(DailyConsumptionChart.Create(data, quota with { EnterpriseIds = [13213] }, now, true).Bars.Any(b => b.Estimated));
        });
        await Test("ESME daily consumption requires the exact host and enterprise membership", () =>
        {
            var esme = quota with { AccountKey = "msft.ghe.com:42", EnterpriseIds = [999] };
            var esmeJson = json.Replace("dotcom:microsoftopensource", "proxima:msft.ghe.com");
            var parsed = EnterpriseConsumptionClient.Parse(Encoding.UTF8.GetBytes(esmeJson), "fixture",
                DailyConsumptionChart.EnterpriseNames(esme), "2026-10-01", "2026-10-05");
            Equal(3m, parsed.Days[0].AmountUsd);
            True(DailyConsumptionChart.Create(data, esme, now, true).Bars.Any(b => b.Estimated));
        });
        await Test("Daily parser rejects invalid dates, duplicate enterprises, and wrong measures", () =>
        {
            foreach (string invalid in new[] { json.Replace("2026-10-01\",\"results", "2026-09-30\",\"results"),
                json.Replace("\"grossAmountUsd\":3", "\"grossAmountUsd\":-3"), json.Replace("\"USD\"", "\"EUR\""),
                json.Replace("daily-ai-credit-report-usage", "other-measure") })
                Throws<EnterpriseAccessException>(() => EnterpriseConsumptionClient.Parse(Encoding.UTF8.GetBytes(invalid),
                    "fixture", ["dotcom:microsoftopensource"], "2026-10-01", "2026-10-05"));
        });
    }
}
