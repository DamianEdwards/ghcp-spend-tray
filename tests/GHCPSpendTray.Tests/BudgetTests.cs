using GHCPSpendTray.Core;

namespace GHCPSpendTray.Tests;

internal static partial class Program
{
    private static async Task BudgetTests()
    {
        var account = Account with { CustomBudgetUsd = 500m, ShowPeriodEstimate = true };
        var snapshot = Sample(37500m, 300000000000000m);
        AccountState State(UsageSnapshot sample, AccountStatus status = AccountStatus.Fresh) =>
            new() { Account = account, Snapshot = sample, Status = status };
        await Test("custom budget measures consumption without changing the API snapshot", () =>
        {
            Equal(500m, UsageBudget.Allocation(account, snapshot));
            Equal(75m, UsageBudget.Percentage(account, snapshot));
            Equal(3000000000000m, snapshot.AllocationUsd);
            snapshot.Validate();
            var tray = TrayUsage.Create(Settings(account), [State(snapshot)], Now);
            Equal(75d, tray.RollUp.Percent);
            True(tray.RollUp.Tooltip.Contains("custom budgets"));
            var estimate = PeriodEstimates.Create(State(snapshot), Now, TimeSpan.FromHours(1))!;
            True(estimate.EstimatedConsumptionUsd > 500 && estimate.OverAllocationUsd > 0);
        });
        await Test("custom budget supports unlimited unknown and zero allocation but never stale data", () =>
        {
            foreach (var sample in new[] { Sample(37500, null, unlimited: true), Sample(37500, null), Sample(37500, 0) })
            {
                Equal(75m, UsageBudget.Percentage(account, sample));
                Equal(75d, TrayUsage.Create(Settings(account), [State(sample)], Now).RollUp.Percent);
                Equal<double?>(null, TrayUsage.Create(Settings(account), [State(sample, AccountStatus.Stale)], Now).RollUp.Percent);
                Equal<double?>(null, TrayUsage.Create(Settings(account), [State(sample)], Now.AddMonths(1)).RollUp.Percent);
            }
        });
        await Test("custom budgets persist and reject zero negative and sub-cent values", async () =>
        {
            var store = Store();
            await store.SaveSettingsAsync(Settings(account));
            Equal(500m, (await new JsonStore(store.RootPath).LoadSettingsAsync()).Value.Accounts[0].CustomBudgetUsd);
            foreach (decimal invalid in new[] { 0m, -1m, 0.001m })
                Throws<ArgumentException>(() => (account with { CustomBudgetUsd = invalid }).Validate());
            (account with { CustomBudgetUsd = null }).Validate();
            (account with { CustomBudgetUsd = decimal.MaxValue }).Validate();
            Equal(decimal.MaxValue, UsageBudget.Percentage(decimal.MaxValue, 0.01m));
        });
        await Test("roll-up weights custom budgets and API allocations together", () =>
        {
            var second = Account with { UserId = "43", Login = "second" };
            var next = new AccountState { Account = second, Status = AccountStatus.Fresh,
                Snapshot = Sample(12500, 10000, account: second) };
            True(Math.Abs(500d / 600d * 100 -
                TrayUsage.Create(Settings(account, second), [State(snapshot), next], Now).RollUp.Percent!.Value) < 0.000001);
        });
        await Test("budget alerts rearm on target changes without duplicating dollar milestones", async () =>
        {
            var store = Store();
            var sink = new Sink();
            var alerts = new AlertService(store, sink);
            var settings = Settings(account) with { SpendIncrementUsd = 100m };
            True(await alerts.EvaluateAsync(account, snapshot, settings));
            True(sink.Alerts[0].ReachedThresholds.SequenceEqual([50m]));
            Equal(300m, sink.Alerts[0].SpendMilestoneUsd);
            var restart = new AlertService(new JsonStore(store.RootPath), sink);
            True(!await restart.EvaluateAsync(account, snapshot with { FetchedAtUtc = Now.AddMinutes(1) }, settings));
            var lower = account with { CustomBudgetUsd = 300m };
            True(await restart.EvaluateAsync(lower, snapshot with { FetchedAtUtc = Now.AddMinutes(2) }, Settings(lower) with { SpendIncrementUsd = 100m }));
            True(sink.Alerts[^1].ReachedThresholds.SequenceEqual([50m, 80m, 100m]));
            Equal<decimal?>(null, sink.Alerts[^1].SpendMilestoneUsd);
            var unlimited = Sample(37500, null, at: Now.AddMonths(1), unlimited: true);
            True(await restart.EvaluateAsync(account, unlimited, settings));
            Equal(50m, sink.Alerts[^1].HighestThreshold);
        });
    }
}
