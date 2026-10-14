using System.Collections.Concurrent;
using GHCPSpendTray.App.UI;
using GHCPSpendTray.Core;
using GHCPSpendTray.Shared;

internal static class PeriodEstimateTests
{
    internal static async Task<int> RunAsync(string directory)
    {
        int assertions = 0;
        var time = new EstimateClock(new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        using var controller = new DemoController(directory, timeProvider: time);
        var pending = new ConcurrentQueue<Action>();
        using var session = new AppSession(controller, pending.Enqueue);
        session.Initialize();
        await Until(() => session.Initialized && !session.Busy);
        var account = session.Dashboard.Accounts[0];
        var noDaily = account with
        {
            EnterpriseConsumption = new(true, "Daily usage connected", Connected: true, Bars: [],
                ChartMessage: "Daily usage is connected, but no daily reports are available for this account yet.")
        };
        Check(UI.DailyConsumption(noDaily) is not null,
            "Connected accounts without daily reports retain an explicit Usage status.");
        Check(UI.DailyConsumption(noDaily, compact: true) is null,
            "Unavailable daily-report status does not expand the compact popup.");
        Check(UI.DailyConsumption(account with { EnterpriseConsumption = new(true, "Connect work account") }) is null,
            "Unconnected daily usage does not add a Usage status.");
        Check(UI.DailyConsumption(noDaily with
        {
            EnterpriseConsumption = noDaily.EnterpriseConsumption! with { IsLoading = true }
        }) is not null, "Connected daily usage shows an in-flight report status.");
        var before = session.Dashboard;
        Check(account.PeriodEstimate is null && UI.EstimateRow(account, "") is null && UI.EstimateDetails(account) is null,
            "Disabled forecast omits all Windows estimate surfaces.");
        session.EditAccount(account.Key);
        Check(!session.ShowPeriodEstimate, "Windows account draft initializes from off-by-default preferences.");
        session.ShowPeriodEstimate = true;
        Check(controller.AccountSettings(account.Key).ShowPeriodEstimate == false &&
            session.Dashboard.Accounts[0].PeriodEstimate is null, "Draft opt-in does not change the saved dashboard.");
        session.SaveAccount();
        await Until(() => !session.Busy);
        account = session.Dashboard.Accounts[0];
        Check(session.Error is null && controller.AccountSettings(account.Key).ShowPeriodEstimate &&
            account.PeriodEstimate == PeriodEstimates.Create(session.Dashboard.TrayStates![0], time.Now, TimeSpan.FromHours(1)),
            "Windows Save passes the explicit preference and uses exactly main's shared result.");
        Check(UI.EstimateRow(account, "") is not null && UI.EstimateDetails(account) is not null,
            "Opt-in renders the compact row and visible method disclosure.");
        Check(session.Dashboard.ConsumptionUsd == before.ConsumptionUsd && account.ConsumptionUsd == 26.25m &&
            account.Percent == 105m && session.Dashboard.Tray!.RollUp == before.Tray!.RollUp,
            "Actual cents, total, percentage and tray remain observed, not projected.");
        Check(UI.EstimateAmount(account.PeriodEstimate!) == "~$56" && UI.Money(account.ConsumptionUsd) == "$26.25",
            "Estimated dollars are approximate and rounded; actual dollars retain cents.");
        Check(UI.EstimateContext(account.PeriodEstimate!).Contains("About $31 over limit") &&
            UI.EstimateContext(account.PeriodEstimate!).Contains("Nov 1, 2026 UTC"),
            "Projected excess uses whole dollars and reset dates explicitly use UTC.");
        Check(UI.UtcTimestamp(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).ToOffset(TimeSpan.FromHours(-7)))
                .Contains("00:00:00 UTC") && UI.ApproximateMoney(12.5m) == "~$13",
            "Windows estimate formatting cannot shift UTC boundaries to local dates.");
        Check(UI.ApproximateMoney(0) == "~$0" && UI.ApproximateMoney(0.01m) == "<$1" &&
            UI.ApproximateMoney(0.99m) == "<$1" && UI.ApproximateMoney(1) == "~$1",
            "Windows matches macOS whole/sub-dollar formatting without turning nonzero forecasts into zero.");
        Check(UI.EstimateContext(new(OverAllocationUsd: 0.01m)) == "Less than $1 over limit" &&
            UI.EstimateContext(new(OverAllocationUsd: 0.99m)) == "Less than $1 over limit" &&
            UI.EstimateContext(new(OverAllocationUsd: 1.5m)) == "About $2 over limit",
            "Windows matches macOS's sub-dollar projected-excess warning.");
        Check(UI.EstimateContext(new(IsEarly: true, OverAllocationUsd: 33m,
            ResetAtUtc: new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero))) ==
            "Early estimate \u00B7 About $33 over limit \u00B7 Resets Nov 1, 2026 UTC",
            "Compact estimate context matches the macOS information order and separators.");
        session.Navigate(SettingsPage.Usage);
        session.EditAccount(account.Key);
        Check(session.ShowPeriodEstimate, "Returning to account settings reloads saved opt-in.");
        session.ShowPeriodEstimate = false;
        session.EditAccount(session.Dashboard.Accounts[1].Key);
        Check(!session.ShowPeriodEstimate, "Opening another account does not leak the previous account's draft.");
        session.EditAccount(account.Key);
        Check(session.ShowPeriodEstimate, "Abandoning a draft leaves the saved preference intact.");
        session.SaveGlobal();
        await Until(() => !session.Busy);
        Check(controller.AccountSettings(account.Key).ShowPeriodEstimate, "Windows global Save preserves account opt-in.");
        time.Now = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        session.Refresh();
        await Until(() => !session.Busy);
        account = session.Dashboard.Accounts[0];
        Check(UI.EstimateContext(account.PeriodEstimate!).StartsWith("Early estimate \u00B7 "),
            "Early estimates are visibly qualified below 72 hours.");
        time.Now = time.Now.AddHours(-1);
        session.Refresh();
        await Until(() => !session.Busy);
        account = session.Dashboard.Accounts[0];
        Check(account.PeriodEstimate is { EstimatedConsumptionUsd: null } &&
            UI.EstimateRow(account, "") is not null && UI.EstimateDetails(account) is not null &&
            UI.EstimateAmount(account.PeriodEstimate) == "Unavailable" &&
            UI.EstimateContext(account.PeriodEstimate).Contains("24 hours"),
            "Enabled-but-unavailable Windows estimates retain a row, reason and method.");
        session.ShowPeriodEstimate = false;
        session.SaveAccount();
        await Until(() => !session.Busy);
        Check(!controller.AccountSettings(account.Key).ShowPeriodEstimate &&
            session.Dashboard.Accounts[0].PeriodEstimate is null, "Windows Save explicitly disables the forecast.");
        Check(!Directory.Exists(directory), "Synthetic forecast preferences never write real data.");
        return assertions;

        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            assertions++;
        }
        async Task Until(Func<bool> condition)
        {
            for (int attempt = 0; attempt < 250; attempt++)
            {
                while (pending.TryDequeue(out var action)) action();
                if (condition()) return;
                await Task.Delay(20);
            }
            throw new TimeoutException("Windows period estimate state did not settle.");
        }
    }

    private sealed class EstimateClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
