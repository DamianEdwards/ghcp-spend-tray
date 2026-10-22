using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GHCPSpendTray.Core;

public sealed class AzureCliTokenProvider : IEnterpriseTokenProvider
{
    private const string Audience = "2efaf292-00a0-426c-ba7d-f5d2b214b8fc";
    private readonly SemaphoreSlim _gate = new(1);
    private EnterpriseToken? _cached;
    public bool IsAvailable => FindCli() is not null;

    public async Task<EnterpriseToken> GetAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _cached is { } cached && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
                return cached;
            _cached = null;
            string? cli = FindCli();
            if (cli is null)
                throw new EnterpriseAccessException("Azure CLI is needed to connect your work account.", cliMissing: true);
            var info = new ProcessStartInfo
            {
                FileName = cli, UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            const string arguments = "account get-access-token --scope " + Audience + "/.default --output json --only-show-errors";
            if (cli.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
                info.Arguments = "/d /s /c \"\"" + cli + "\" " + arguments + "\"";
            }
            else
            {
                foreach (string argument in arguments.Split(' ')) info.ArgumentList.Add(argument);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var process = new Process { StartInfo = info };
            bool started = false;
            try
            {
                process.Start();
                started = true;
                var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
                var error = ReadBoundedAsync(process.StandardError, timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                string json = await output.ConfigureAwait(false);
                await error.ConfigureAwait(false);
                if (process.ExitCode != 0)
                    throw new EnterpriseAccessException("Sign in to Azure CLI with your work account, then try again.", authenticationFailed: true);
                using var document = JsonDocument.Parse(json);
                string token = document.RootElement.GetProperty("accessToken").GetString() ?? throw new JsonException();
                _cached = ParseToken(token);
                return _cached;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new EnterpriseAccessException("Azure CLI timed out. Sign in in a terminal, then try again.", authenticationFailed: true); }
            catch (Exception ex) when (ex is Win32Exception or IOException or JsonException or FormatException or KeyNotFoundException or ArgumentException or InvalidOperationException)
            { throw new EnterpriseAccessException("Azure CLI could not provide a work-account token. Sign in in a terminal, then try again.", authenticationFailed: true); }
            finally
            {
                if (started && !process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        finally { _gate.Release(); }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        char[] chunk = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (text.Length + count > 65536) throw new IOException("Azure CLI output exceeded the size limit.");
            text.Append(chunk, 0, count);
        }
        return text.ToString();
    }

    private static string? FindCli()
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).ToList();
        if (!OperatingSystem.IsWindows()) paths.AddRange(["/opt/homebrew/bin", "/usr/local/bin"]);
        foreach (string directory in paths.Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p.Trim('"'))))
            foreach (string name in OperatingSystem.IsWindows() ? new[] { "az.cmd", "az.exe" } : new[] { "az" })
            {
                string path = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(path)) return path;
            }
        return null;
    }

    public static EnterpriseToken ParseToken(string token)
    {
        if (token.Length > 32768) throw new FormatException();
        var parts = token.Split('.');
        if (parts.Length != 3) throw new FormatException();
        string payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
        var claims = document.RootElement;
        if (claims.GetProperty("aud").GetString() is not (Audience or "api://" + Audience))
            throw new FormatException();
        string? username = claims.TryGetProperty("upn", out var upn) ? upn.GetString() :
            claims.GetProperty("preferred_username").GetString();
        if (username is null || username.Split('@').Length != 2) throw new FormatException();
        var identity = new EnterpriseIdentity(username.Split('@')[0].ToLowerInvariant(),
            claims.GetProperty("tid").GetString() ?? "", claims.GetProperty("oid").GetString() ?? "", username);
        identity.Validate();
        var expiry = DateTimeOffset.FromUnixTimeSeconds(claims.GetProperty("exp").GetInt64());
        if (expiry <= DateTimeOffset.UtcNow.AddMinutes(1) ||
            claims.TryGetProperty("nbf", out var nbf) && nbf.GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            throw new FormatException();
        // These claims bind the UI confirmation; the production API validates the token's signature.
        return new(token, identity, expiry);
    }
}
