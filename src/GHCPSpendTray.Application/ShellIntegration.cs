namespace GHCPSpendTray.Shared;

public static class ShellIntegration
{
    public static string Segment() => ReadResource("segment");

    public static string Initialize(string[] arguments, string executable)
    {
        if (arguments.Length < 2 || arguments[1] is not ("pwsh" or "bash" or "zsh"))
            throw new ArgumentException("Use init pwsh, init bash or init zsh.");
        string shell = arguments[1];
        var options = new List<(string Name, string Value)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 2; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length || !seen.Add(arguments[index]))
                throw new ArgumentException("Missing or repeated initialization option.");
            string name = arguments[index], value = arguments[index + 1];
            if (name is not ("--account" or "--data-dir"))
                throw new ArgumentException("Initialization supports --account KEY and --data-dir DIRECTORY.");
            if (string.IsNullOrEmpty(value) || value.Any(char.IsControl))
                throw new ArgumentException("Initialization option values must not be empty or contain control characters.");
            if (name == "--data-dir" && !Path.IsPathFullyQualified(value))
                throw new ArgumentException("Use an absolute data directory.");
            options.Add((name, value));
        }
        if (string.IsNullOrEmpty(executable) || executable.Any(char.IsControl))
            throw new ArgumentException("The app executable could not be resolved.");

        string code = ReadResource(shell).TrimStart('\uFEFF').TrimEnd() + "\n\n";
        if (shell == "pwsh")
        {
            code += "Initialize-CopilotPrompt -AppExecutable " + QuotePowerShell(executable);
            foreach ((string name, string value) in options)
                code += (name == "--account" ? " -Account " : " -DataDirectory ") + QuotePowerShell(value);
            return code + " -InstallHook\n";
        }
        code += "initialize_copilot_prompt --app-executable " + QuotePosix(executable);
        foreach ((string name, string value) in options) code += " " + name + " " + QuotePosix(value);
        return code + " --install-hook\n";
    }

    private static string QuotePowerShell(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string QuotePosix(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string ReadResource(string resource)
    {
        using var stream = typeof(ShellIntegration).Assembly.GetManifestResourceStream("GHCPSpendTray.ShellIntegration." + resource) ??
            throw new InvalidOperationException("The bundled shell integration is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
