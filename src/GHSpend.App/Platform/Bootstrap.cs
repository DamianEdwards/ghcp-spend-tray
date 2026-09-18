using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace GHSpend.App.Platform;

public static class Bootstrap
{
    /// <summary>Non-fatal installer warnings. Subscribe before Start; callbacks run on its calling thread.</summary>
    public static event Action<string>? Warning;

    internal static void ReportWarning(string message)
    {
        Trace.TraceWarning(message);
        var handlers = Warning;
        if (handlers is null)
            return;
        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try { handler(message); }
            catch
            {
                Trace.TraceError("GHSpend could not deliver an installer warning to a diagnostic subscriber.");
            }
        }
    }

    /// <summary>
    /// Starts an installed or explicitly portable instance. Null means another instance accepted
    /// activation or the installed child became ready. Call and dispose on the native UI thread.
    /// </summary>
    public static BootstrapRuntime? Start(string[] args)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("GHSpend requires Windows.");
        var options = BootstrapOptions.Parse(args);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
            throw new IOException("Windows did not supply the current user's profile directory.");
        var installDirectory = InstallationPaths.NormalizeAbsolute(Path.Combine(profile, ".ghspend"));
        var installedExecutable = Path.Combine(installDirectory, "ghspend.exe");
        var source = InstallationPaths.NormalizeAbsolute(Environment.ProcessPath ??
            throw new IOException("Windows did not supply the running executable's path."));
        var dataDirectory = options.Portable ? options.DataDirectory! : installDirectory;

        if (options.Portable)
        {
            if (InstallationPaths.IsWithin(dataDirectory, installDirectory) ||
                InstallationPaths.SamePath(dataDirectory, profile) ||
                InstallationPaths.SamePath(dataDirectory, Path.GetPathRoot(dataDirectory)!))
                throw new ArgumentException("Portable mode requires its own data directory, not the installed directory, user profile, or drive root.");
            InstallationPaths.EnsurePrivateDirectory(dataDirectory);
            return StartRuntime(dataDirectory, portable: true, handoff: null);
        }

        if (InstallationPaths.SamePath(source, installedExecutable))
        {
            InstallationPaths.EnsurePrivateDirectory(installDirectory);
            return StartRuntime(dataDirectory, portable: false, options.Handoff);
        }

        if (options.Handoff is not null)
            throw new ArgumentException("A readiness handoff is valid only for the installed executable.");
        if (RuntimeFeature.IsDynamicCodeSupported)
            throw new InvalidOperationException("Self-installation requires a published Native AOT executable. Use --portable --data-dir <absolute-directory> for development.");

        var scope = InstanceCoordinator.GetScope(null);
        using var installLock = InstanceCoordinator.CreateMutex(@"Global\GHSpend.Install." + scope);
        bool acquired;
        try { acquired = installLock.WaitOne(TimeSpan.FromSeconds(35)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
            throw new IOException("Another GHSpend installation is in progress. Wait for it to finish, then try again.");

        try
        {
            InstallationPaths.EnsurePrivateDirectory(installDirectory);
            InstallationPaths.RejectReparseAncestors(installedExecutable);
            var files = new WindowsInstallFiles();
            var exists = files.Exists(installedExecutable);
            var sameBinary = exists && CryptographicOperations.FixedTimeEquals(files.Hash(source), files.Hash(installedExecutable));
            if (InstanceCoordinator.IsRunning(scope))
            {
                if (!sameBinary)
                    throw new IOException("A different GHSpend version is running. Exit GHSpend from its tray menu before replacing it.");
                InstanceCoordinator.Activate(scope);
                return null;
            }

            if (sameBinary)
            {
                ReadinessHandoff.LaunchAndWait(installedExecutable);
                return null;
            }

            if (exists)
                RejectDowngrade(source, installedExecutable);
            var registration = new StartupRegistration(installedExecutable);
            new InstallationTransaction(files, registration).Execute(source, installedExecutable,
                ReadinessHandoff.LaunchAndWait, enableStartupOnFirstInstall: !options.Startup);
            return null;
        }
        finally
        {
            installLock.ReleaseMutex();
        }
    }

    private static BootstrapRuntime? StartRuntime(string directory, bool portable, string? handoff)
    {
        var instance = InstanceCoordinator.Start(InstanceCoordinator.GetScope(portable ? directory : null));
        return instance is null ? null : new BootstrapRuntime(directory, portable, handoff, instance);
    }

    private static void RejectDowngrade(string source, string installed)
    {
        var candidate = FileVersionInfo.GetVersionInfo(source);
        var existing = FileVersionInfo.GetVersionInfo(installed);
        var incomingVersion = VersionOf(candidate);
        var installedVersion = VersionOf(existing);
        if (incomingVersion is null || installedVersion is null)
            throw new IOException("GHSpend cannot safely compare executable versions. Remove or move the old executable after exiting the app before installing this build.");
        if (incomingVersion < installedVersion)
            throw new IOException($"GHSpend will not replace version {installedVersion} with older version {incomingVersion}.");
    }

    private static Version? VersionOf(FileVersionInfo info) =>
        string.IsNullOrWhiteSpace(info.FileVersion) ? null :
            new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
}

public sealed class BootstrapRuntime : IDisposable
{
    private readonly InstanceCoordinator _instance;
    private readonly string? _handoff;
    private bool _ready;
    private bool _disposed;

    internal BootstrapRuntime(string directory, bool portable, string? handoff, InstanceCoordinator instance)
    {
        DataDirectory = directory;
        IsPortable = portable;
        _handoff = handoff;
        _instance = instance;
    }

    public string DataDirectory { get; }
    public bool IsPortable { get; }

    public event Action<Exception>? Diagnostic
    {
        add => _instance.Diagnostic += value;
        remove => _instance.Diagnostic -= value;
    }

    /// <summary>Callbacks run on a worker thread; post a message to the native window thread.</summary>
    public void RegisterActivationCallback(Action callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _instance.RegisterActivationCallback(callback);
    }

    /// <summary>Call only after both the native window and the tray icon have initialized successfully.</summary>
    public void SignalReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ready)
            return;
        if (_handoff is not null)
            ReadinessHandoff.Signal(_handoff);
        _ready = true;
        _instance.MarkReady();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _instance.Dispose();
    }
}

internal sealed record BootstrapOptions(bool Portable, string? DataDirectory, string? Handoff, bool Startup)
{
    public static BootstrapOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool portable = false, startup = false;
        string? directory = null, handoff = null;
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
                case "--handoff" when handoff is null && index + 1 < args.Length:
                    handoff = args[++index];
                    if (!Guid.TryParseExact(handoff, "N", out _))
                        throw new ArgumentException("The readiness identifier is invalid.");
                    break;
                default:
                    throw new ArgumentException("Unrecognized, repeated, or incomplete GHSpend command-line option.");
            }
        }
        if (portable != (directory is not null) || (portable && (startup || handoff is not null)))
            throw new ArgumentException("Use --portable together with --data-dir <absolute-directory>, without startup or handoff options.");
        return new BootstrapOptions(portable, directory, handoff, startup);
    }
}
