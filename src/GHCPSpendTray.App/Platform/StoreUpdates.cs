using System.Diagnostics;
using GHCPSpendTray.App.Native;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace GHCPSpendTray.App.Platform;

internal enum StoreInstallResult { Completed, Canceled }
internal readonly record struct StoreUpdateProgress(double Fraction, bool Installing);

internal interface IStoreUpdates
{
    Task<bool> CheckAsync(CancellationToken cancellationToken);
    Task<StoreInstallResult> InstallAsync(nint owner, Action<StoreUpdateProgress> progress,
        CancellationToken cancellationToken);
    void Restart();
}

internal sealed class StoreUpdates : IStoreUpdates
{
    private readonly DispatcherQueue _dispatcher;
    private StoreContext? _context;

    private StoreUpdates() => _dispatcher = DispatcherQueue.GetForCurrentThread() ??
        throw new InvalidOperationException("Microsoft Store updates require the UI dispatcher.");

    internal static IStoreUpdates? Create(bool enabled) =>
        enabled && PackageContext.IsPackaged && Package.Current.SignatureKind == PackageSignatureKind.Store
            ? new StoreUpdates() : null;

    private StoreContext Context => _context ??= StoreContext.GetDefault();

    private async Task<StorePackageUpdate[]> FindUpdatesAsync(CancellationToken cancellationToken)
    {
        var operation = await OnUIAsync(() => Context.GetAppAndOptionalStorePackageUpdatesAsync());
        var updates = await operation.AsTask(cancellationToken);
        // Package describes the installed package, not the incoming version.
        return updates.Any(update => update.Package.Id.FamilyName == Package.Current.Id.FamilyName)
            ? updates.ToArray() : [];
    }

    public async Task<bool> CheckAsync(CancellationToken cancellationToken) =>
        (await FindUpdatesAsync(cancellationToken)).Length > 0;

    public async Task<StoreInstallResult> InstallAsync(nint owner, Action<StoreUpdateProgress> progress,
        CancellationToken cancellationToken)
    {
        if (owner == 0) throw new AppOperationException("The update requires an open Settings window.");
        var updates = await FindUpdatesAsync(cancellationToken);
        if (updates.Length == 0)
            throw new AppOperationException("The update is no longer available. Reopen About to check for updates again.");

        using var process = Process.GetCurrentProcess();
        var delay = UpdateRestart.RemainingDelay(DateTime.UtcNow - process.StartTime.ToUniversalTime());
        if (delay > TimeSpan.Zero)
        {
            progress(new(0, false));
            await Task.Delay(delay, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        UpdateRestart.Register();
        bool preserveRegistration = false;
        try
        {
            // Store prompts must originate on the UI thread with a live owner HWND.
            var operation = await OnUIAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                WinRT.Interop.InitializeWithWindow.Initialize(Context, owner);
                var request = Context.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
                request.Progress = (_, status) => progress(new(status.TotalDownloadProgress,
                    status.PackageUpdateState == StorePackageUpdateState.Deploying));
                return request;
            });
            var result = await operation.AsTask(cancellationToken);
            if (result.OverallState == StorePackageUpdateState.Canceled) return StoreInstallResult.Canceled;
            if (result.OverallState != StorePackageUpdateState.Completed)
                throw new AppOperationException(FailureMessage(result.OverallState));
            preserveRegistration = true;
            return StoreInstallResult.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            preserveRegistration = true;
            throw;
        }
        finally
        {
            // Successful deployment can terminate us before the await returns. Do not
            // unregister during shutdown: Restart Manager still needs the registration.
            if (!preserveRegistration) UpdateRestart.Unregister();
        }
    }

    internal static string FailureMessage(StorePackageUpdateState state) => state switch
    {
        StorePackageUpdateState.ErrorLowBattery => "Charge your device, then try the update again.",
        StorePackageUpdateState.ErrorWiFiRecommended or StorePackageUpdateState.ErrorWiFiRequired =>
            "Connect to Wi-Fi, then try the update again.",
        _ => $"The Microsoft Store could not install the update ({state}). Try again or open Microsoft Store."
    };

    public void Restart()
    {
        var reason = Microsoft.Windows.AppLifecycle.AppInstance.Restart("--startup");
        throw new AppOperationException($"Windows could not restart the app ({reason}). Exit and reopen GHCPSpendTray.");
    }

    private Task<T> OnUIAsync<T>(Func<T> action)
    {
        if (_dispatcher.HasThreadAccess) return Task.FromResult(action());
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }))
            completion.TrySetException(new InvalidOperationException("The UI dispatcher rejected a Store operation."));
        return completion.Task;
    }
}
