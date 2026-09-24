namespace GHCPSpendTray.App.Platform;

public static class Bootstrap
{
    public static BootstrapRuntime? Start(string[] args)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("GHCPSpendTray requires Windows.");
        var options = BootstrapOptions.Parse(args);
        string directory;
        if (options.Portable)
        {
            directory = options.DataDirectory!;
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (InstallationPaths.SamePath(directory, profile) ||
                InstallationPaths.SamePath(directory, Path.GetPathRoot(directory)!) ||
                InstallationPaths.IsWithin(directory, Path.Combine(local, "Packages")))
                throw new ArgumentException("Portable mode requires its own data directory, not a user profile, drive root, or package data folder.");
            InstallationPaths.EnsurePrivateDirectory(directory);
        }
        else
        {
            if (!PackageContext.IsPackaged)
                throw new InvalidOperationException("Install the GHCPSpendTray MSIX package, or use --portable --data-dir <absolute-directory> for development.");
            directory = PackageContext.DataDirectory;
            Directory.CreateDirectory(directory);
        }
        var instance = InstanceCoordinator.Start(InstanceCoordinator.GetScope(
            options.Portable ? "portable:" + directory : "package:" + PackageContext.FamilyName),
            activateExisting: !options.Startup);
        return instance is null ? null : new BootstrapRuntime(directory, options.Portable, options.Startup, instance);
    }
}

public sealed class BootstrapRuntime : IDisposable
{
    private readonly InstanceCoordinator _instance;

    internal BootstrapRuntime(string directory, bool portable, bool startup, InstanceCoordinator instance)
    {
        DataDirectory = directory;
        IsPortable = portable;
        IsStartup = startup;
        _instance = instance;
    }

    public string DataDirectory { get; }
    public bool IsPortable { get; }
    public bool IsStartup { get; }
    public event Action<Exception>? Diagnostic
    {
        add => _instance.Diagnostic += value;
        remove => _instance.Diagnostic -= value;
    }
    public void RegisterActivationCallback(Action callback) => _instance.RegisterActivationCallback(callback);
    public void SignalReady() => _instance.MarkReady();
    public void Dispose() => _instance.Dispose();
}

internal sealed record BootstrapOptions(bool Portable, string? DataDirectory, bool Startup)
{
    public static BootstrapOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool portable = false, startup = false;
        string? directory = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--portable" when !portable:
                    portable = true;
                    break;
                case "--startup" when !startup:
                    startup = true;
                    break;
                case "--data-dir" when directory is null && index + 1 < args.Length:
                    directory = InstallationPaths.NormalizeAbsolute(args[++index]);
                    break;
                default:
                    throw new ArgumentException("Unrecognized, repeated, or incomplete GHCPSpendTray command-line option.");
            }
        }
        if (portable != (directory is not null) || (portable && startup))
            throw new ArgumentException("Use --portable together with --data-dir <absolute-directory>, without startup options.");
        return new BootstrapOptions(portable, directory, startup);
    }
}
