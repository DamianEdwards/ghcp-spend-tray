using Windows.ApplicationModel;

namespace GHCPSpendTray.App.Platform;

internal sealed class StartupRegistration(IStartupTaskStore store)
{
    internal const string TaskId = "GHCPSpendTrayStartup";
    internal bool Enabled => store.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
    internal bool CanChange => store.State is StartupTaskState.Disabled or StartupTaskState.Enabled;
    internal string Description => store.State switch
    {
        StartupTaskState.DisabledByUser => "Disabled in Windows. Enable GHCPSpendTray in Settings > Apps > Startup.",
        StartupTaskState.DisabledByPolicy => "Startup is disabled by your organization's policy.",
        StartupTaskState.EnabledByPolicy => "Startup is enabled by your organization's policy.",
        _ => "Launch quietly in the tray when you sign in."
    };

    internal static async Task<StartupRegistration> CreateAsync()
    {
        try { return new(new WindowsStartupTaskStore(await StartupTask.GetAsync(TaskId))); }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            System.Diagnostics.Trace.TraceError($"Windows startup task lookup failed (0x{ex.HResult:X8}).");
            throw new StartupRegistrationException("Windows could not load the app's startup task. Repair or reinstall the package.");
        }
    }

    internal async Task SetEnabledAsync(bool enabled)
    {
        if (enabled == Enabled) return;
        if (!CanChange) throw new StartupRegistrationException(Description);
        try
        {
            if (enabled)
                await store.RequestEnableAsync();
            else
                store.Disable();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            System.Diagnostics.Trace.TraceError($"Windows startup task update failed (0x{ex.HResult:X8}).");
            throw new StartupRegistrationException("Windows could not change startup. Check Settings > Apps > Startup and your organization's policy.");
        }
        if (enabled != Enabled)
            throw new StartupRegistrationException(CanChange
                ? "Windows did not retain the requested startup setting. Try again in Settings > Apps > Startup."
                : Description);
    }

    private sealed class WindowsStartupTaskStore(StartupTask task) : IStartupTaskStore
    {
        public StartupTaskState State => task.State;
        public async Task RequestEnableAsync() => await task.RequestEnableAsync();
        public void Disable() => task.Disable();
    }
}

internal sealed class StartupRegistrationException(string message) : Exception(message);

internal interface IStartupTaskStore
{
    StartupTaskState State { get; }
    Task RequestEnableAsync();
    void Disable();
}
