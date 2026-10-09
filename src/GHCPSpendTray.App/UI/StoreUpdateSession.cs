using GHCPSpendTray.App.Platform;

namespace GHCPSpendTray.App.UI;

internal sealed class StoreUpdateSession : IDisposable
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private readonly IStoreUpdates _service;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lifetime = new();
    private ITimer? _timer;
    private bool _disposed, _notified;
    private int _installAttempt;
    private DateTimeOffset? _lastCheck;
    internal event Action? Changed, Available;
    internal bool Checking { get; private set; }
    internal bool Updating { get; private set; }
    internal bool HasUpdate { get; private set; }
    internal bool RestartRequired { get; private set; }
    internal string? Error { get; private set; }
    internal string Status { get; private set; } = "Checking for updates...";
    internal double Progress { get; private set; }

    internal StoreUpdateSession(IStoreUpdates service, Action<Action> dispatch, TimeProvider? time = null)
    {
        _service = service; _dispatch = dispatch; _time = time ?? TimeProvider.System;
    }

    internal void Start()
    {
        if (_disposed || _timer is not null) return;
        _timer = _time.CreateTimer(_ => Post(() => Check()), null, CheckInterval, CheckInterval);
        Check();
    }

    internal async void Check(bool force = false)
    {
        if (_disposed || Checking || Updating || RestartRequired ||
            !force && _lastCheck is { } last && _time.GetUtcNow() - last < TimeSpan.FromMinutes(1)) return;
        Checking = true; Error = null;
        if (!HasUpdate) Status = "Checking for updates...";
        Changed?.Invoke();
        try
        {
            bool available = await _service.CheckAsync(_lifetime.Token);
            Post(() =>
            {
                HasUpdate = available;
                Status = available ? "An update is available." : "You're running the latest version.";
                _lastCheck = _time.GetUtcNow();
                if (!available) _notified = false;
                else if (!_notified) { _notified = true; Available?.Invoke(); }
            });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Diagnostics.RecordFailure("Microsoft Store update check failed", ex);
            Post(() => { Status = HasUpdate ? "An update is available." : "Update status unavailable."; Error =
                "Could not check Microsoft Store for updates. Check your connection and try again."; });
        }
        finally { Post(() => { Checking = false; Changed?.Invoke(); }); }
    }

    internal async void Install(nint owner)
    {
        if (_disposed || Checking || Updating || !HasUpdate) return;
        Updating = true; Error = null; Progress = 0;
        int attempt = ++_installAttempt;
        bool finished = false;
        Status = RestartRequired ? "Restarting..." : "Preparing update. Windows may close and restart the app...";
        Changed?.Invoke();
        try
        {
            if (RestartRequired) { Restart(); return; }
            var result = await _service.InstallAsync(owner, progress => Post(() =>
            {
                if (finished || !Updating || attempt != _installAttempt) return;
                Progress = Math.Clamp(progress.Fraction * 100, 0, 100);
                Status = progress.Installing ? "Installing update. The app will restart..." :
                    Progress > 0 ? $"Downloading update... {Progress:0}%" :
                    "Preparing update. Windows may close and restart the app...";
                Changed?.Invoke();
            }), _lifetime.Token);
            finished = true;
            if (_disposed) return;
            if (result == StoreInstallResult.Canceled)
            {
                Post(() => Status = "Update canceled. You can try again.");
                return;
            }
            Post(() => { RestartRequired = true; Restart(); });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Diagnostics.RecordFailure("Microsoft Store update installation or restart failed", ex);
            Post(() =>
            {
                Status = RestartRequired ? "Update installed. Restart required." : "An update is available.";
                Error = RestartRequired ? "Windows could not restart the app. Try Restart, or exit and reopen GHCPSpendTray." :
                    ex is AppOperationException ? ex.Message :
                    "The update could not be installed. Check Microsoft Store and try again.";
            });
        }
        finally { finished = true; Post(() => { Updating = false; Changed?.Invoke(); }); }
    }

    private void Restart()
    {
        try { _service.Restart(); }
        catch (Exception ex)
        {
            Diagnostics.RecordFailure("Microsoft Store update restart failed", ex);
            Status = "Update installed. Restart required.";
            Error = "Windows could not restart the app. Try Restart, or exit and reopen GHCPSpendTray.";
            Changed?.Invoke();
        }
    }

    private void Post(Action action)
    {
        if (!_disposed) _dispatch(() => { if (!_disposed) action(); });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
