using System.Text.Json;
using GHCPSpendTray.Core;

namespace GHCPSpendTray.Tests;

internal static partial class Program
{
    private static async Task PeriodEstimateTests()
    {
        var enabled = Account with { ShowPeriodEstimate = true };
        var freshness = TimeSpan.FromHours(1);
        AccountState State(DateTimeOffset at, decimal credits = 2000m, decimal? entitlement = 10000m,
            DateTimeOffset? source = null, DateTimeOffset? reset = null, bool unlimited = false) =>
            new()
            {
                Account = enabled, Status = AccountStatus.Fresh,
                Snapshot = Sample(credits, entitlement, at, enabled, unlimited) with
                {
                    SourceTimestampUtc = source, ResetAtUtc = reset, PeriodId = BillingPeriods.Resolve(at, reset)
                }
            };
        PeriodEstimate Estimate(AccountState state, DateTimeOffset? now = null) =>
            PeriodEstimates.Create(state, now ?? state.Snapshot!.FetchedAtUtc, freshness)!;
        void Unavailable(AccountState state, string reason, DateTimeOffset? now = null)
        {
            var value = Estimate(state, now);
            Equal(null, value.EstimatedConsumptionUsd);
            Equal(null, value.AverageDailyConsumptionUsd);
            Equal(null, value.OverAllocationUsd);
            Equal(reason, value.UnavailableReason);
        }

        await Test("period estimates default off and old configuration remains off", async () =>
        {
            var legacy = JsonSerializer.Deserialize(
                """{"version":1,"accounts":[{"host":"github.com","userId":"42","login":"fixture-user"}]}""",
                CoreJsonContext.Default.AppSettings)!;
            True(!legacy.Accounts[0].ShowPeriodEstimate);
            Equal(null, PeriodEstimates.Create(State(Now) with { Account = Account }, Now, freshness));
            var store = Store();
            await store.SaveSettingsAsync(Settings(enabled));
            True((await store.LoadSettingsAsync()).Value.Accounts.Single().ShowPeriodEstimate);
        });
        foreach (var (year, month, days) in new[] { (2026, 2, 28), (2024, 2, 29), (2026, 9, 30), (2026, 10, 31) })
        {
            await Test($"period estimate uses the exact {days}-day UTC calendar month", () =>
            {
                var start = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
                var at = start.AddDays(10);
                var state = State(at, reset: start.AddMonths(1));
                var value = Estimate(state);
                Equal(20m * days / 10, value.EstimatedConsumptionUsd);
                Equal(2m, value.AverageDailyConsumptionUsd);
                Equal(0m, value.OverAllocationUsd);
                Equal(start, value.PeriodStartUtc);
                Equal(start.AddMonths(1), value.ResetAtUtc);
                Equal(at, value.ObservedAtUtc);
                Equal(null, value.UnavailableReason);
                True(!value.IsEarly);
            });
        }
        await Test("period estimate uses UTC boundaries for offset timestamps and missing reset", () =>
        {
            var at = new DateTimeOffset(2026, 9, 30, 23, 0, 0, TimeSpan.FromHours(-7));
            var value = Estimate(State(at));
            Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), value.PeriodStartUtc);
            Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), value.ResetAtUtc);
            Equal(null, value.EstimatedConsumptionUsd);
            True(value.UnavailableReason!.Contains("24 hours"));
        });
        await Test("period estimate honors exact 24-hour and 72-hour boundaries", () =>
        {
            var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            Unavailable(State(start), "At least 24 hours of this period must elapse.");
            Unavailable(State(start.AddDays(1).AddTicks(-1)), "At least 24 hours of this period must elapse.");
            var value = Estimate(State(start.AddDays(1)));
            Equal(600m, value.EstimatedConsumptionUsd);
            True(value.IsEarly);
            True(Estimate(State(start.AddDays(3).AddTicks(-1))).IsEarly);
            True(!Estimate(State(start.AddDays(3))).IsEarly);
        });
        await Test("period estimate uses source time and remains stable between observations", () =>
        {
            var source = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
            var state = State(source.AddMinutes(10), source: source);
            var value = Estimate(state);
            Equal(40m, value.EstimatedConsumptionUsd);
            Equal(source, value.ObservedAtUtc);
            Equal(value, Estimate(state, source.AddMinutes(45)));
            Equal(state.Snapshot!.FetchedAtUtc, Estimate(state with
            {
                Snapshot = state.Snapshot with { SourceTimestampUtc = null }
            }).ObservedAtUtc);
        });
        await Test("period estimate tolerates up to one minute of positive source clock skew", () =>
        {
            var expected = Estimate(State(Now));
            foreach (var skew in new[]
            {
                TimeSpan.FromTicks(1), TimeSpan.FromMilliseconds(5),
                TimeSpan.FromMinutes(1).Subtract(TimeSpan.FromTicks(1)), TimeSpan.FromMinutes(1)
            })
            {
                var source = Now.Add(skew).ToOffset(TimeSpan.FromHours(-7));
                var state = State(Now, source: source);
                Equal(expected, Estimate(state));
                Equal(Now, Estimate(state).ObservedAtUtc);
                Equal(expected, Estimate(state, Now.AddMinutes(30)));
                Equal(source, state.Snapshot!.SourceTimestampUtc);
            }
        });
        await Test("period estimate clock skew cannot advance elapsed-time or freshness boundaries", () =>
        {
            var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            var beforeDay = start.AddDays(1).AddTicks(-1);
            Unavailable(State(beforeDay, source: beforeDay.AddMinutes(1)),
                "At least 24 hours of this period must elapse.");
            var beforeEarlyEnd = start.AddDays(3).AddTicks(-1);
            True(Estimate(State(beforeEarlyEnd, source: beforeEarlyEnd.AddMinutes(1))).IsEarly);
            var state = State(Now, source: Now.AddMinutes(1));
            True(Estimate(state, Now.Add(freshness)).EstimatedConsumptionUsd is not null);
            Unavailable(state, "Consumption observation is stale.", Now.Add(freshness).AddTicks(1));
        });
        await Test("period estimate clamps positive clock skew across reset without extending the period", () =>
        {
            var reset = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
            var fetched = reset.AddSeconds(-30);
            var state = State(fetched, source: reset.AddSeconds(30), reset: reset);
            Equal(Estimate(State(fetched, reset: reset)), Estimate(state));
            Equal(fetched, Estimate(state).ObservedAtUtc);
            Unavailable(state, "Awaiting current-period consumption.", reset);
        });
        await Test("period estimate rejects previous-period sources and clock skew over one minute", () =>
        {
            Unavailable(State(Now, source: new DateTimeOffset(2026, 8, 31, 23, 59, 59, TimeSpan.Zero)),
                "Usage observation time is outside the supported period.");
            var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            Unavailable(State(start.AddSeconds(10), source: start.AddTicks(-1)),
                "Usage observation time is outside the supported period.");
            Unavailable(State(Now, source: Now.AddMinutes(1).AddTicks(1)),
                "Usage observation time is outside the supported period.");
            Unavailable(State(Now, source: Now.AddDays(1)),
                "Usage observation time is outside the supported period.");
        });
        await Test("period estimate requires fresh current observations at exact freshness threshold", () =>
        {
            var state = State(Now);
            True(Estimate(state, Now.Add(freshness)).EstimatedConsumptionUsd is not null);
            Unavailable(state, "Consumption observation is stale.", Now.Add(freshness).AddTicks(1));
            Unavailable(State(Now, source: Now.Add(freshness.Negate()).AddTicks(-1)), "Consumption observation is stale.");
            Unavailable(state with { Status = AccountStatus.Stale }, "Consumption observation is stale.");
            foreach (var status in Enum.GetValues<AccountStatus>().Where(s => s is not (AccountStatus.Fresh or AccountStatus.Stale)))
                Unavailable(state with { Status = status }, "Current consumption is unavailable. Refresh this account.");
            Unavailable(state with { Snapshot = null }, "Awaiting consumption data.", Now);
        });
        await Test("period estimate disappears at rollover and does not fabricate noncalendar periods", () =>
        {
            var at = new DateTimeOffset(2026, 9, 30, 23, 45, 0, TimeSpan.Zero);
            var reset = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
            Unavailable(State(at, reset: reset), "Awaiting current-period consumption.", reset);
            Unavailable(State(at), "Awaiting current-period consumption.", reset);
            Unavailable(State(Now, reset: new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero)),
                "Billing period differs from a UTC calendar month.");
            var offsetReset = reset.ToOffset(TimeSpan.FromHours(-7));
            True(Estimate(State(Now, reset: offsetReset)).EstimatedConsumptionUsd is not null);
        });
        await Test("period estimate distinguishes zero usage and projects without allocation", () =>
        {
            var at = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
            Equal(0m, Estimate(State(at, credits: 0)).EstimatedConsumptionUsd);
            foreach (var allocation in new decimal?[] { null, 0 })
            {
                var value = Estimate(State(at, entitlement: allocation));
                Equal(40m, value.EstimatedConsumptionUsd);
                Equal(null, value.OverAllocationUsd);
            }
            var unlimited = Estimate(State(at, unlimited: true));
            Equal(40m, unlimited.EstimatedConsumptionUsd);
            Equal(null, unlimited.OverAllocationUsd);
        });
        await Test("period estimate is not capped to allocation or rounded during calculation", () =>
        {
            var at = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
            var value = Estimate(State(at, credits: 2625m, entitlement: 2500m));
            Equal(52.50m, value.EstimatedConsumptionUsd);
            Equal(1.75m, value.AverageDailyConsumptionUsd);
            Equal(27.50m, value.OverAllocationUsd);
            var fraction = Estimate(State(at.AddHours(12), credits: 1234.56m));
            Equal(12.3456m * 30m / 15.5m, fraction.EstimatedConsumptionUsd);
        });
        await Test("period estimate exposes invalid identity and unsupported amounts rather than failing the dashboard", () =>
        {
            var state = State(Now);
            Unavailable(state with { Snapshot = state.Snapshot! with { ConsumptionUsd = -1 } }, "Consumption data is invalid.");
            Unavailable(state with { Snapshot = state.Snapshot! with { AccountKey = "github.com:43" } },
                "Consumption belongs to a different account.");
            var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            var huge = State(start.AddDays(1), credits: decimal.MaxValue, entitlement: null);
            True(Estimate(huge).EstimatedConsumptionUsd > 0);
            Unavailable(huge with
            {
                Snapshot = huge.Snapshot! with { Entitlement = 1, AllocationUsd = 0.01m }
            }, "Consumption data is invalid.");
            Unavailable(State(new DateTimeOffset(9999, 12, 16, 0, 0, 0, TimeSpan.Zero)),
                "Billing period exceeds the supported date range.");
        });
        await Test("period estimate does not change actual aggregation, tray eligibility or alerts", async () =>
        {
            var at = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
            var state = State(at, credits: 4000m, entitlement: 5000m);
            var original = state with { Account = Account };
            Equal(80m, Estimate(state).EstimatedConsumptionUsd);
            Equal(UsageAggregation.Total([original], at, freshness), UsageAggregation.Total([state], at, freshness));
            Equal(TrayUsage.Create(Settings(Account), [original], at).RollUp.Percent,
                TrayUsage.Create(Settings(enabled), [state], at).RollUp.Percent);
            var sink = new Sink();
            var alerts = new AlertService(Store(), sink);
            await alerts.EvaluateAsync(enabled, state.Snapshot!, Settings(enabled));
            Equal(80m, sink.Alerts.Single().HighestThreshold);
        });
    }
}
