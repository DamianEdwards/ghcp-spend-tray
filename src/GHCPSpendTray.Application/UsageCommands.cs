using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GHCPSpendTray.Core;

namespace GHCPSpendTray.Shared;

// This is an app-owned export, not a credential store or another usage monitor.
public sealed record SavedUsage
{
    [JsonRequired] public int Version { get; init; } = 1;
    public required DateTimeOffset PublishedAtUtc { get; init; }
    public required int PollMinutes { get; init; }
    public required SavedAccountUsage[] Accounts { get; init; }
}

public sealed record SavedAccountUsage
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required string UserId { get; init; }
    public required bool Selected { get; init; }
    public decimal? CustomBudgetUsd { get; init; }
    public required AccountStatus Status { get; init; }
    public required bool Refreshing { get; init; }
    public UsageSnapshot? Observation { get; init; }
    public PeriodEstimate? Estimate { get; init; }
}

public sealed record AccountUsageResult(string Key, string Name, string Host, bool Selected,
    string Status, bool IsFresh, bool IsCurrentPeriod, bool IsRefreshing,
    DateTimeOffset? ObservedAtUtc, DateTimeOffset? SourceTimestampUtc, DateTimeOffset? ResetAtUtc,
    decimal? ConsumptionUsd, decimal? ServiceAllocationUsd, decimal? CustomBudgetUsd,
    decimal? EffectiveBudgetUsd, decimal? PercentConsumed, decimal? EstimatedConsumptionUsd,
    string ForecastState);

public sealed record UsageResult(int Version, DateTimeOffset QueriedAtUtc, DateTimeOffset? PublishedAtUtc,
    int? PollMinutes, string Status, bool IsComplete, bool IsRefreshing, int IncludedAccounts, int SelectedAccounts,
    decimal? ConsumptionUsd, decimal? EffectiveBudgetUsd, decimal? PercentConsumed,
    decimal? EstimatedConsumptionUsd, string ForecastState, AccountUsageResult[] Accounts);

public sealed record UsageCommandResult(int ExitCode, string Output, string? Error = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SavedUsage))]
[JsonSerializable(typeof(UsageResult))]
public partial class UsageJsonContext : JsonSerializerContext;

public static class AppUsageStore
{
    public const string FileName = "shell-usage-v1.json";

    public static SavedUsage Create(AppSettings settings, IReadOnlyList<AccountState> states,
        IReadOnlyList<AccountView> views, DateTimeOffset now)
    {
        var byKey = states.ToDictionary(state => state.Account.Key, StringComparer.Ordinal);
        var estimates = views.ToDictionary(view => view.Key, StringComparer.Ordinal);
        return new()
        {
            PublishedAtUtc = now, PollMinutes = settings.PollIntervalMinutes,
            Accounts = settings.Accounts.Select(account =>
            {
                byKey.TryGetValue(account.Key, out var state);
                estimates.TryGetValue(account.Key, out var view);
                return new SavedAccountUsage
                {
                    Key = account.Key, Name = account.DisplayName ?? account.Login,
                    Host = account.Host, UserId = account.UserId, Selected = !account.ExcludeFromTray,
                    CustomBudgetUsd = account.CustomBudgetUsd, Status = state?.Status ?? AccountStatus.Pending,
                    Refreshing = state?.IsRefreshing == true,
                    Observation = state?.Snapshot, Estimate = account.ShowPeriodEstimate ? view?.PeriodEstimate : null
                };
            }).ToArray()
        };
    }

    public static void Save(string directory, SavedUsage snapshot)
    {
        Validate(snapshot);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName);
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(staging, options))
            {
                JsonSerializer.Serialize(stream, snapshot, UsageJsonContext.Default.SavedUsage);
                stream.Flush(flushToDisk: true);
            }
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(staging, path, overwrite: true); break; }
                catch (IOException error) when (OperatingSystem.IsWindows() && attempt < 4 &&
                    (error.HResult & 0xffff) is 32 or 33) { Thread.Sleep(2); }
                catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows() && attempt < 4)
                { Thread.Sleep(2); }
            }
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    public static SavedUsage? Read(string directory)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(Path.Combine(directory, FileName), FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Saved shell usage exceeds its size limit.");
                var snapshot = JsonSerializer.Deserialize(stream, UsageJsonContext.Default.SavedUsage) ??
                    throw new InvalidDataException("Saved shell usage is empty.");
                Validate(snapshot);
                return snapshot;
            }
            catch (FileNotFoundException) when (attempt < 2) { Thread.Sleep(1); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
    }

    public static void Validate(SavedUsage snapshot)
    {
        if (snapshot.Version != 1 || snapshot.PublishedAtUtc == default || snapshot.PollMinutes is < 5 or > 1440 ||
            snapshot.Accounts is null || snapshot.Accounts.Length > 1000)
            throw new InvalidDataException("Invalid saved shell usage.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var saved in snapshot.Accounts)
        {
            if (saved is null) throw new InvalidDataException("Missing saved account.");
            var account = new Account
            {
                Host = saved.Host, UserId = saved.UserId, Login = "query",
                CustomBudgetUsd = saved.CustomBudgetUsd
            };
            account.Validate();
            if (string.IsNullOrWhiteSpace(saved.Name) || saved.Name.Length > 256 ||
                account.Key != saved.Key || !keys.Add(saved.Key) || !Enum.IsDefined(saved.Status))
                throw new InvalidDataException("Invalid saved account identity.");
            if (saved.Observation is { } observation)
            {
                observation.Validate();
                if (observation.AccountKey != saved.Key || observation.FetchedAtUtc > snapshot.PublishedAtUtc)
                    throw new InvalidDataException("Invalid saved account observation.");
            }
            if (saved.Estimate is { } estimate &&
                (estimate.EstimatedConsumptionUsd < 0 || estimate.AverageDailyConsumptionUsd < 0))
                throw new InvalidDataException("Invalid saved forecast.");
        }
    }
}

public static class UsageCommands
{
    public static bool Handles(string[] arguments) => arguments.FirstOrDefault() is "usage" or "prompt" or "init" or "integration";

    public static UsageCommandResult Run(string[] arguments, string defaultDirectory, DateTimeOffset? now = null,
        string? executable = null)
    {
        try
        {
            if (!Handles(arguments)) throw new ArgumentException("Expected usage, prompt, init or integration.");
            if (arguments[0] == "init")
                return new(0, ShellIntegration.Initialize(arguments, executable ?? Environment.ProcessPath ?? ""));
            if (arguments[0] == "integration")
            {
                if (arguments.Length != 3 || arguments[1] != "oh-my-posh" || arguments[2] != "segment")
                    throw new ArgumentException("Use integration oh-my-posh segment.");
                return new(0, ShellIntegration.Segment());
            }
            string directory = defaultDirectory;
            string? account = null;
            string format = arguments[0] == "prompt" ? "prompt" : "json";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 1; index < arguments.Length; index += 2)
            {
                if (index + 1 >= arguments.Length || !seen.Add(arguments[index]))
                    throw new ArgumentException("Missing or repeated usage option.");
                switch (arguments[index])
                {
                    case "--account": account = arguments[index + 1]; break;
                    case "--data-dir":
                        if (!Path.IsPathFullyQualified(arguments[index + 1])) throw new ArgumentException("Use an absolute data directory.");
                        directory = Path.GetFullPath(arguments[index + 1]); break;
                    case "--format" when arguments[0] == "usage" && arguments[index + 1] == "json": break;
                    default: throw new ArgumentException("Supported options: --account KEY, --format json (usage only), --data-dir DIRECTORY.");
                }
            }
            var result = Query(AppUsageStore.Read(directory), now ?? DateTimeOffset.UtcNow, account);
            return new(0, format == "prompt" ? Prompt(result) :
                JsonSerializer.Serialize(result, UsageJsonContext.Default.UsageResult));
        }
        catch (ArgumentException error) { return new(2, "", error.Message); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or
            UnauthorizedAccessException or OverflowException)
        {
            return new(1, "", "Saved app usage could not be read. Open GHCPSpendTray and check its data directory.");
        }
    }

    public static UsageResult Query(SavedUsage? snapshot, DateTimeOffset now, string? accountKey = null)
    {
        if (snapshot is not null) AppUsageStore.Validate(snapshot);
        var all = snapshot?.Accounts ?? [];
        if (accountKey is not null && !all.Any(account => account.Key == accountKey))
            throw new ArgumentException("The account key is not in the saved app state.");
        TimeSpan freshness = TimeSpan.FromMinutes(snapshot?.PollMinutes ?? 10);
        bool publicationValid = snapshot is not null && snapshot.PublishedAtUtc <= now;
        var accounts = all.Select(saved =>
        {
            var observation = saved.Observation;
            bool current = observation is not null && publicationValid && BillingPeriods.IsCurrent(observation, now);
            bool fresh = current && saved.Status == AccountStatus.Fresh && now - observation!.FetchedAtUtc <= freshness;
            var account = new Account { Host = saved.Host, UserId = saved.UserId, Login = "query", CustomBudgetUsd = saved.CustomBudgetUsd };
            decimal? budget = observation is null ? saved.CustomBudgetUsd : UsageBudget.Allocation(account, observation);
            decimal? percent = fresh ? UsageBudget.Percentage(account, observation!) : null;
            var estimate = saved.Estimate;
            bool estimateValid = fresh && estimate is { UnavailableReason: null, EstimatedConsumptionUsd: not null,
                ObservedAtUtc: not null, ResetAtUtc: not null, PeriodStartUtc: not null } &&
                estimate.PeriodStartUtc.Value.Day == 1 && estimate.PeriodStartUtc.Value.TimeOfDay == TimeSpan.Zero &&
                estimate.PeriodStartUtc.Value.Offset == TimeSpan.Zero &&
                estimate.ResetAtUtc == estimate.PeriodStartUtc.Value.AddMonths(1) &&
                estimate.ObservedAtUtc >= estimate.PeriodStartUtc && estimate.ObservedAtUtc <= observation!.FetchedAtUtc &&
                (observation.ResetAtUtc is null || observation.ResetAtUtc == estimate.ResetAtUtc) &&
                estimate.ObservedAtUtc <= now && now - estimate.ObservedAtUtc <= freshness && now < estimate.ResetAtUtc;
            decimal? projected = estimateValid ? estimate!.EstimatedConsumptionUsd : null;
            string state = Band(observation?.ConsumptionUsd, budget, estimateValid ? estimate : null);
            bool refreshing = publicationValid && saved.Refreshing && now - snapshot!.PublishedAtUtc < TimeSpan.FromSeconds(95);
            string status = !publicationValid ? "unavailable" : observation is null ? saved.Status.ToString() :
                !current ? "expired" : saved.Status != AccountStatus.Fresh ? saved.Status.ToString() : fresh ? "fresh" : "stale";
            return new AccountUsageResult(saved.Key, saved.Name, saved.Host, saved.Selected, status, fresh,
                current, refreshing, observation?.FetchedAtUtc, observation?.SourceTimestampUtc, observation?.ResetAtUtc,
                observation?.ConsumptionUsd, observation?.AllocationUsd, saved.CustomBudgetUsd, budget,
                percent, projected, state);
        }).ToArray();
        var selected = accounts.Where(account => accountKey is null ? account.Selected : account.Key == accountKey).ToArray();
        var included = selected.Where(account => account.IsFresh).ToArray();
        bool complete = selected.Length > 0 && included.Length == selected.Length;
        decimal? used = included.Length == 0 ? null : included.Sum(account => account.ConsumptionUsd!.Value);
        // Never pair dollars from one set of accounts with a percentage from a different set.
        decimal? effectiveBudget = included.Length > 0 && included.All(account => account.EffectiveBudgetUsd is > 0)
            ? included.Sum(account => account.EffectiveBudgetUsd!.Value) : null;
        decimal? totalPercent = used is { } amount && effectiveBudget is > 0 ? UsageBudget.Percentage(amount, effectiveBudget.Value) : null;
        decimal? projectedTotal = complete && selected.All(account => account.EstimatedConsumptionUsd is not null)
            ? selected.Sum(account => account.EstimatedConsumptionUsd!.Value) : null;
        var estimateTotal = projectedTotal is { } total ? new PeriodEstimate(EstimatedConsumptionUsd: total) : null;
        string statusTotal = snapshot is null ? "unavailable" : selected.Length == 0 ? "no-accounts" :
            complete ? "fresh" : included.Length > 0 ? "partial" : "unavailable";
        return new(1, now, snapshot?.PublishedAtUtc, snapshot?.PollMinutes, statusTotal, complete, selected.Any(account => account.IsRefreshing),
            included.Length, selected.Length, used, effectiveBudget, totalPercent, projectedTotal,
            Band(used, effectiveBudget, estimateTotal), accountKey is null ? accounts : selected);
    }

    public static string Prompt(UsageResult result)
    {
        string spend = result.ConsumptionUsd is { } used ? CompactMoney(used) : "unavailable";
        if (result.ConsumptionUsd is not null && !result.IsComplete) spend = "!" + spend;
        if (result.PercentConsumed is { } percent)
            spend += " (~" + decimal.Round(percent, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%)";
        string forecast = result.EstimatedConsumptionUsd is { } projected ? CompactMoney(projected) : "?";
        string connection = result.IsRefreshing ? "in_progress" : result.IsComplete ? "connected" : "not_connected";
        return string.Join('\t', "GHCP-SPEND/1", spend, forecast, result.ForecastState, connection);
    }

    private static string Band(decimal? actual, decimal? budget, PeriodEstimate? estimate) =>
        UsageForecast.Create(actual, budget, estimate)?.Band switch
        {
            ForecastBand.Under => "green", ForecastBand.Near => "yellow", ForecastBand.Over => "red", _ => "unknown"
        };

    private static string CompactMoney(decimal value) =>
        decimal.Round(value, 0, MidpointRounding.AwayFromZero) >= 1000
            ? "$" + decimal.Round(value / 1000, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture) + "K"
            : "$" + decimal.Round(value, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
}
