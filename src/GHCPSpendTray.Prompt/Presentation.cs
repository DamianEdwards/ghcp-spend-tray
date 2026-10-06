using System.Globalization;
using GHCPSpendTray.Core;

namespace GHCPSpendTray.Prompt;

public sealed record PromptState(string Spend, string Forecast, string ForecastState, string ConnectionState)
{
    public static PromptState Unavailable { get; } = new("unavailable", "?", "unknown", "not_connected");

    public string ToProtocol()
    {
        string[] fields = ["GHCP-SPEND/1", Spend, Forecast, ForecastState, ConnectionState];
        if (fields.Any(field => string.IsNullOrEmpty(field) || field.Length > 256 || field.Any(char.IsControl)))
            throw new InvalidDataException("Invalid prompt protocol field.");
        return string.Join('\t', fields);
    }

    public static PromptState Create(PromptCache? cache, DateTimeOffset now, TimeSpan freshness, bool refreshing)
    {
        var result = Unavailable;
        if (cache is { Status: AccountStatus.Fresh, Snapshot: { } snapshot } &&
            BillingPeriods.IsCurrent(snapshot, now) && now - snapshot.FetchedAtUtc < freshness)
        {
            snapshot.Validate();
            string spend = FormatUsd(snapshot.ConsumptionUsd);
            if (snapshot.PercentConsumed is { } percent)
                spend += " (~" + decimal.Round(percent, 0, MidpointRounding.AwayFromZero)
                    .ToString("0", CultureInfo.InvariantCulture) + "%)";
            var account = new Account { Host = cache.Hostname, UserId = cache.AccountId!, Login = "prompt", ShowPeriodEstimate = true };
            var estimate = PeriodEstimates.Create(new AccountState
            {
                Account = account, Status = AccountStatus.Fresh, Snapshot = snapshot
            }, now, freshness);
            string forecast = "?", state = "unknown";
            if (!snapshot.Unlimited && snapshot.AllocationUsd is > 0 && estimate?.EstimatedConsumptionUsd is { } projected)
            {
                forecast = FormatUsd(projected);
                state = projected <= snapshot.AllocationUsd.Value * .8m ? "green" :
                    projected <= snapshot.AllocationUsd.Value ? "yellow" : "red";
            }
            result = new(spend, forecast, state, "connected");
        }
        return refreshing ? result with { ConnectionState = "in_progress" } : result;
    }

    public static string FormatUsd(decimal amount)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        decimal whole = decimal.Round(amount, 0, MidpointRounding.AwayFromZero);
        return whole >= 1000
            ? "$" + decimal.Round(amount / 1000, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture) + "K"
            : "$" + whole.ToString("0", CultureInfo.InvariantCulture);
    }

    public static PromptState Demo(string state)
    {
        if (state is "in_progress" or "not_connected")
            return Unavailable with { ConnectionState = state };
        decimal estimate = state switch
        {
            "green" => 6000, "yellow" => 9000, "red" => 12000,
            _ => throw new ArgumentException("Demo state must be green, yellow, red, in_progress or not_connected.")
        };
        DateTimeOffset now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var snapshot = CopilotUsageClient.ParseJson(
            """{"quota_snapshots":{"premium_interactions":{"token_based_billing":true,"has_quota":true,"unlimited":false,"credits_used":0,"entitlement":1000000}},"quota_reset_date":"2026-11-01"}""",
            "github.com:123", now);
        decimal credits = estimate * 50;
        snapshot = snapshot with
        {
            CreditsUsed = credits, ConsumptionUsd = CreditPolicy.ToUsd(credits),
            PercentConsumed = CreditPolicy.Percentage(credits, snapshot.Entitlement, false)
        };
        return Create(new PromptCache
        {
            ContextKey = new string('a', 64), Hostname = "github.com", AccountId = "123",
            Status = AccountStatus.Fresh, Snapshot = snapshot, AttemptedAtUtc = now, NextAttemptUtc = now.AddHours(1)
        }, now, TimeSpan.FromHours(1), false);
    }
}
