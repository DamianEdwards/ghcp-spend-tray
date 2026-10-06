using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GHCPSpendTray.Core;

namespace GHCPSpendTray.Prompt;

public static class CredentialContext
{
    public static string? EnvironmentToken(string hostname, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        bool cloud = HostResolver.Resolve(hostname).Kind != HostKind.EnterpriseServer;
        string? token = environment(cloud ? "GH_TOKEN" : "GH_ENTERPRISE_TOKEN");
        return string.IsNullOrEmpty(token) ? environment(cloud ? "GITHUB_TOKEN" : "GITHUB_ENTERPRISE_TOKEN") : token;
    }

    public static string Key(PromptOptions options, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? configured = environment("GH_CONFIG_DIR");
        string configuration = (!string.IsNullOrEmpty(configured) ? configured : null) ??
            (environment("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? Path.Combine(xdg, "gh") :
            OperatingSystem.IsWindows() && environment("APPDATA") is { Length: > 0 } appdata
                ? Path.Combine(appdata, "GitHub CLI") : Path.Combine(home, ".config", "gh"));
        string path = Path.GetFullPath(Path.Combine(configuration, "hosts.yml"));
        var metadata = new FileInfo(path);
        string stamp = metadata.Exists ? $"{metadata.LastWriteTimeUtc.Ticks}:{metadata.Length}" : "missing";
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join('\n', "native-v1", options.Hostname,
            ExecutableContext(options.GhExecutable, environment), options.RefreshMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            path, stamp, EnvironmentToken(options.Hostname, environment)));
        try { return Hash(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ExecutableContext(string executable, Func<string, string?> environment)
    {
        if (Path.IsPathFullyQualified(executable)) return Path.GetFullPath(executable);
        foreach (string directory in (environment("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe")) return Path.GetFullPath(candidate + ".exe");
        }
        return executable;
    }

    public static async Task<TokenSet> ResolveAsync(PromptOptions options, CancellationToken cancellationToken)
    {
        string? token = EnvironmentToken(options.Hostname);
        if (string.IsNullOrEmpty(token))
        {
            var info = new ProcessStartInfo(options.GhExecutable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
            };
            foreach (string argument in new[] { "auth", "token", "--hostname", options.Hostname }) info.ArgumentList.Add(argument);
            foreach (string name in new[] { "GH_DEBUG", "DEBUG", "GH_FORCE_TTY" }) info.Environment.Remove(name);
            info.Environment["GH_PROMPT_DISABLED"] = "1";
            info.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
            using var process = new Process { StartInfo = info };
            if (!process.Start()) throw new ServiceException(AccountStatus.SignInRequired, "Could not start gh.");
            process.StandardInput.Close();
            Task<string> output = ReadBoundedAsync(process.StandardOutput, 16386, cancellationToken);
            Task<string> error = ReadBoundedAsync(process.StandardError, 16386, cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                string text = await output.ConfigureAwait(false);
                await error.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new ServiceException(AccountStatus.SignInRequired, "gh could not read an existing credential.");
                token = text.Trim();
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        var result = new TokenSet { AccessToken = token };
        result.Validate();
        return result;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (result.Length + read > limit) throw new ServiceException(AccountStatus.InvalidData, "gh output exceeded its size limit.");
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }
}
