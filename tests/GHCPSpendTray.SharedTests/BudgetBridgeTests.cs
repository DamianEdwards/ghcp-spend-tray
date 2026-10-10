using System.Text.Json;
using GHCPSpendTray.MacBridge;
using GHCPSpendTray.Shared;

internal static class BudgetBridgeTests
{
    internal static async Task<int> RunAsync(string directory)
    {
        int assertions = 0;
        using var bridge = new BridgeRuntime();
        DashboardView? dashboard = null;
        await Send(new() { Id = "budget-init", Method = "initialize", Directory = directory, Demo = true });
        Check(dashboard?.Accounts[1].CustomBudgetUsd is null, "Omitted budgets default to API allocation.");
        var command = JsonSerializer.Deserialize("""
            {"id":"budget-set","method":"account.save","key":"example.ghe.com:2","displayName":"Work (demo)",
             "customBudgetUsd":50.00,"updateCustomBudget":true,"showPeriodEstimate":true}
            """, BridgeJsonContext.Default.Command)!;
        Check(JsonSerializer.Deserialize(JsonSerializer.Serialize(command, BridgeJsonContext.Default.Command),
            BridgeJsonContext.Default.Command) == command, "Budget command retains cents and explicit update in generated JSON.");
        await Send(command);
        Check(dashboard!.Accounts[1] is { CustomBudgetUsd: 50m, AllocationUsd: 50m, Percent: 33m } &&
            dashboard.Accounts[1].Details is { ObservedAllocationUsd: 100m, ObservedPercentConsumed: 16.5m },
            "Budget adjusts usage without modifying raw API diagnostics.");
        Check(dashboard.Tray!.RollUp.Percent == 57 && dashboard.Accounts[1].PeriodEstimate is not null,
            "Budget denominator reaches menu-bar weighting and estimates.");
        var preferences = await Send(new() { Id = "budget-prefs", Method = "account.preferences", Key = "example.ghe.com:2" });
        Check(preferences.Preferences is { CustomBudgetUsd: 50m, ShowPeriodEstimate: true },
            "Saved budget is exposed by the additive preferences contract.");
        var validation = bridge.Send(new()
        {
            Method = "form.validate", Key = "example.ghe.com:2",
            Form = new() { Page = "account", InheritIncrement = true, UseCustomBudget = true, CustomBudget = "50.00" }
        });
        Check(validation.Error is null && validation.Text ==
            "Preview: $16.50 consumed = 33% of $50.00 custom budget", "Draft preview uses shared budget arithmetic.");
        Check(dashboard.Accounts[1].CustomBudgetUsd == 50m, "Validation/preview does not mutate settings.");
        foreach (var (id, json) in new[]
        {
            ("omit", """{"method":"account.save","key":"example.ghe.com:2","displayName":"Renamed"}"""),
            ("null", """{"method":"account.save","key":"example.ghe.com:2","customBudgetUsd":null}"""),
            ("false", """{"method":"account.save","key":"example.ghe.com:2","customBudgetUsd":75,"updateCustomBudget":false}""")
        })
        {
            await Send(JsonSerializer.Deserialize(json, BridgeJsonContext.Default.Command)! with { Id = id });
            Check(dashboard.Accounts[1].CustomBudgetUsd == 50m, "Omitted/null/false unrelated saves preserve the override.");
        }
        await Send(new() { Id = "global", Method = "settings.save", Settings = new(15, "50, 80, 100", false, false) });
        Check(dashboard.Accounts[1].CustomBudgetUsd == 50m, "Global preferences preserve account budgets.");
        var rejected = await Send(new() { Id = "invalid", Method = "account.save", Key = "example.ghe.com:2",
            CustomBudgetUsd = -1, UpdateCustomBudget = true }, allowError: true);
        Check(rejected.Error is not null && dashboard.Accounts[1].CustomBudgetUsd == 50m, "Invalid budget leaves saved data intact.");
        await Send(JsonSerializer.Deserialize("""
            {"id":"clear","method":"account.save","key":"example.ghe.com:2","customBudgetUsd":null,"updateCustomBudget":true}
            """, BridgeJsonContext.Default.Command)!);
        Check(dashboard.Accounts[1] is { CustomBudgetUsd: null, AllocationUsd: 100m, Percent: 16.5m },
            "Explicit null plus true clears the target.");
        var invalid = bridge.Send(new() { Method = "form.validate", Form = new()
        {
            Page = "account", DisplayName = new string('x', 129), Thresholds = "0, test", Increment = "-1",
            UseCustomBudget = true, CustomBudget = "1.001"
        } });
        Check(invalid.FieldErrors?.Count == 4, "Validation identifies every invalid field.");
        var allowed = bridge.Send(new() { Method = "form.validate", Form = new()
        {
            Page = "account", Thresholds = "100, 50, 80, 50", Increment = ".50",
            UseCustomBudget = true, CustomBudget = "50.000"
        } });
        Check(allowed.FieldErrors?.Count == 0 && allowed.CustomBudgetUsd == 50m && allowed.IncrementUsd == .5m,
            "Validation matches Windows/shared normalization and decimal allowed values.");
        var disabled = bridge.Send(new() { Method = "form.validate", Form = new()
        {
            Page = "account", InheritIncrement = true, Increment = "invalid", CustomBudget = "invalid"
        } });
        Check(disabled.FieldErrors?.Count == 0, "Disabled overrides ignore inactive drafts.");
        return assertions;

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
        async Task<BridgeEvent> Send(Command command, bool allowError = false)
        {
            Check(bridge.Send(command).Error is null, "Command accepted.");
            for (int attempt = 0; attempt < 250; attempt++)
            {
                var events = JsonSerializer.Deserialize(
                    JsonSerializer.Serialize(bridge.Poll(), BridgeJsonContext.Default.BridgeEventArray),
                    BridgeJsonContext.Default.BridgeEventArray)!;
                foreach (var e in events) if (e.Dashboard is not null) dashboard = e.Dashboard;
                if (events.SingleOrDefault(e => e.Kind == "completed" && e.Id == command.Id) is { } result)
                {
                    if (!allowError) Check(result.Error is null, "Command completed successfully.");
                    return result;
                }
                await Task.Delay(20);
            }
            throw new TimeoutException("Budget bridge command did not settle.");
        }
    }
}
