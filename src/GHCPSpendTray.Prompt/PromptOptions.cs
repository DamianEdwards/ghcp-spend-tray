using System.Globalization;
using GHCPSpendTray.Core;

namespace GHCPSpendTray.Prompt;

public sealed record PromptOptions
{
    public string Hostname { get; init; } = "github.com";
    public string CacheDirectory { get; init; } = DefaultCacheDirectory();
    public int RefreshMinutes { get; init; } = 60;
    public int RequestTimeoutSeconds { get; init; } = 15;
    public string GhExecutable { get; init; } = "gh";
    public TimeSpan Freshness => TimeSpan.FromMinutes(RefreshMinutes);
    public TimeSpan WorkerLifetime => TimeSpan.FromSeconds(3 * RequestTimeoutSeconds + 15);

    public PromptOptions Validate()
    {
        string host = HostResolver.Resolve(Hostname).Host;
        string directory = Path.GetFullPath(CacheDirectory);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (RefreshMinutes is < 1 or > 1440 || RequestTimeoutSeconds is < 1 or > 60 ||
            string.IsNullOrWhiteSpace(GhExecutable) || directory == Path.GetPathRoot(directory) ||
            Path.TrimEndingDirectorySeparator(directory).Equals(Path.TrimEndingDirectorySeparator(home),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Use a dedicated cache directory, interval 1-1440 minutes, timeout 1-60 seconds and a gh executable.");
        return this with { Hostname = host, CacheDirectory = directory };
    }

    public static PromptOptions Parse(string[] arguments, int start = 1)
    {
        var options = new PromptOptions();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = start; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length || !seen.Add(arguments[index]))
                throw new ArgumentException("Missing or repeated helper option.");
            string value = arguments[index + 1];
            options = arguments[index] switch
            {
                "--hostname" => options with { Hostname = value },
                "--cache-dir" => options with { CacheDirectory = value },
                "--refresh-minutes" => options with { RefreshMinutes = ParseInteger(value) },
                "--request-timeout" => options with { RequestTimeoutSeconds = ParseInteger(value) },
                "--gh-executable" => options with { GhExecutable = value },
                _ => throw new ArgumentException("Unknown helper option.")
            };
        }
        return options.Validate();
    }

    public IEnumerable<string> Arguments()
    {
        return ["--hostname", Hostname, "--cache-dir", CacheDirectory,
            "--refresh-minutes", RefreshMinutes.ToString(CultureInfo.InvariantCulture),
            "--request-timeout", RequestTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            "--gh-executable", GhExecutable];
    }

    private static int ParseInteger(string value) =>
        int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

    private static string DefaultCacheDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GHCPSpendPrompt", "native-v1");
        string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        string root = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache") : xdg;
        return Path.Combine(root, "GHCPSpendPrompt", "native-v1");
    }
}
