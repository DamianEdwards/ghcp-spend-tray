using System.Diagnostics;

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
    /// Starts from the extracted application folder. Null means another instance accepted
    /// activation. Call and dispose on the native UI thread. Startup is explicitly opt-in.
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
        var dataDirectory = options.Portable ? options.DataDirectory! : installDirectory;
        if (options.Handoff is not null)
            throw new ArgumentException("ZIP builds run from their extracted folder and do not support installer handoff.");

        if (options.Portable)
        {
            if (InstallationPaths.IsWithin(dataDirectory, installDirectory) ||
                InstallationPaths.SamePath(dataDirectory, profile) ||
                InstallationPaths.SamePath(dataDirectory, Path.GetPathRoot(dataDirectory)!))
                throw new ArgumentException("Portable mode requires its own data directory, not the installed directory, user profile, or drive root.");
            InstallationPaths.EnsurePrivateDirectory(dataDirectory);
            return StartRuntime(dataDirectory, portable: true, handoff: null);
        }

        InstallationPaths.EnsurePrivateDirectory(installDirectory);
        return StartRuntime(dataDirectory, portable: false, handoff: null);
    }

    private static BootstrapRuntime? StartRuntime(string directory, bool portable, string? handoff)
    {
        var instance = InstanceCoordinator.Start(InstanceCoordinator.GetScope(portable ? directory : null));
        return instance is null ? null : new BootstrapRuntime(directory, portable, handoff, instance);
    }

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
