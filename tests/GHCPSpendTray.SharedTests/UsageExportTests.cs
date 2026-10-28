using GHCPSpendTray.Core;
using GHCPSpendTray.Shared;

static class UsageExportTests
{
    public static async Task RunAsync(Action<bool, string> check, string root)
    {
        string directory = Path.Combine(root, "shell-export");
        var account = new Account { UserId = "1", Login = "synthetic", CustomBudgetUsd = 50m, ShowPeriodEstimate = true };
        await new JsonStore(directory).SaveSettingsAsync(new() { Accounts = [account], NotificationsEnabled = false });
        var credentials = new MemoryCredentials();
        credentials.Values[account.Key] = new() { AccessToken = "synthetic-token" };
        using var http = new HttpClient(new FixtureHttp());
        using var controller = new ApplicationController(directory, true, credentials, http);
        await controller.InitializeAsync();
        await controller.RefreshAsync();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (AppUsageStore.Read(directory)?.Accounts[0] is not
            { Observation: not null, Status: AccountStatus.Fresh, Refreshing: false })
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("App-owned shell publication.");
            await Task.Delay(10);
        }
        var export = AppUsageStore.Read(directory)!;
        check(export.Accounts[0].CustomBudgetUsd == 50m && export.Accounts[0].Selected,
            "Controller publishes app budget and selection to shell snapshot.");
        check(!export.Accounts[0].Refreshing,
            "Completed monitoring refresh clears the exported in-progress flag.");
        await controller.SaveAccountAsync(account.Key, "Renamed synthetic", "", customBudgetUsd: 75, updateCustomBudget: true);
        export = AppUsageStore.Read(directory)!;
        check(export.Accounts[0].CustomBudgetUsd == 75 && export.Accounts[0].Name == "Renamed synthetic",
            "Saved account changes reach shell queries without a separate refresh.");
        await controller.SaveSettingsAsync(controller.Settings with { ExcludedTrayAccounts = [account.Key], PollMinutes = 20 });
        export = AppUsageStore.Read(directory)!;
        check(!export.Accounts[0].Selected && export.PollMinutes == 20,
            "Shell snapshot follows app poll interval and selected accounts.");
        string json = System.IO.File.ReadAllText(Path.Combine(directory, AppUsageStore.FileName));
        check(!json.Contains("synthetic-token", StringComparison.Ordinal) && !json.Contains("accessToken", StringComparison.Ordinal),
            "App shell export does not publish credentials.");
    }
}
