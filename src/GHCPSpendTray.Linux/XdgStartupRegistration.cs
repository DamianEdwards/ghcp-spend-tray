using GHCPSpendTray.Shared;

namespace GHCPSpendTray.Linux;

internal sealed class XdgStartupRegistration(string configHome, string? executable, bool statusNotifier = false) : IStartupRegistration
{
    internal const string Marker = "# GHCPSpendTray startup v1\n";
    internal string FilePath => Path.Combine(configHome, "autostart", "ghcp-spend-tray.desktop");
    private string Content => Marker + "[Desktop Entry]\nType=Application\nName=GHCPSpendTray\n" +
        $"Exec={Quote(executable!)}{(statusNotifier ? " --status-notifier" : "")}\nTerminal=false\nX-GNOME-Autostart-enabled=true\n";
    private bool Owned => !File.Exists(FilePath) || File.ReadAllText(FilePath) == Content;
    public bool CanChange => executable is not null && Path.IsPathFullyQualified(configHome) &&
        new FileInfo(FilePath).LinkTarget is null &&
        new DirectoryInfo(Path.GetDirectoryName(FilePath)!).LinkTarget is null && Owned;
    public bool Enabled => executable is not null && new FileInfo(FilePath).LinkTarget is null &&
        File.Exists(FilePath) && File.ReadAllText(FilePath) == Content;
    public string Description => CanChange
        ? "Start the monitoring helper at desktop login using XDG Autostart. Your desktop controls when its panel loads."
        : "Startup is unavailable: run the installed native helper, or resolve an unowned/modified autostart entry.";

    public Task SetEnabledAsync(bool enabled)
    {
        if (!CanChange) throw new PlatformOperationException(Description);
        if (!enabled)
        {
            File.Delete(FilePath);
            return Task.CompletedTask;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, Content);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (!CanChange) throw new PlatformOperationException("The startup entry changed. Nothing was overwritten.");
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { File.Delete(temporary); }
        return Task.CompletedTask;
    }

    internal static string Quote(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
            throw new PlatformOperationException("The helper executable path is invalid for login startup.");
        string argument = path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`")
            .Replace("$", "\\$").Replace("%", "%%");
        return "\"" + argument.Replace("\\", "\\\\") + "\"";
    }
}
