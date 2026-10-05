using GHCPSpendTray.Core;
using GHCPSpendTray.Shared;

namespace GHCPSpendTray.Linux;

public sealed class DemoSession : IDisposable
{
    private readonly DemoController _controller;
    private readonly SemaphoreSlim _mutations = new(1, 1);
    private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    public DashboardView Dashboard { get; private set; } = new("", "", "", []);
    public SettingsView Settings => _controller.Settings;
    public Task<int> Completion => _completion.Task;
    public DemoSnapshot Snapshot { get; private set; } = DemoSnapshot.Empty;
    public event Action? Changed;
    public event Action<string>? Error;

    public DemoSession(bool empty = false)
    {
        // DemoController never reads or writes this directory.
        _controller = new DemoController("", empty);
        _controller.Changed += OnChanged;
    }

    public Task StartAsync() => _controller.InitializeAsync();

    private void OnChanged(DashboardView dashboard)
    {
        Dashboard = dashboard;
        Snapshot = DemoSnapshot.Create(dashboard, Settings.TrayStyle, Snapshot.Revision + 1);
        Changed?.Invoke();
    }

    public async Task RefreshAsync()
    {
        await _mutations.WaitAsync();
        try { await _controller.RefreshAsync(); }
        finally { _mutations.Release(); }
    }

    public async Task AddExampleAsync()
    {
        await _mutations.WaitAsync();
        try
        {
            if (Dashboard.Accounts.Count >= 100)
                throw new InvalidOperationException("The prototype supports at most 100 synthetic accounts.");
            await _controller.AddExampleAccountAsync();
        }
        finally { _mutations.Release(); }
    }

    public async Task SetStyleAsync(TrayIconStyle style)
    {
        await _mutations.WaitAsync();
        try { await _controller.SaveSettingsAsync(Settings with { TrayStyle = style }); }
        finally { _mutations.Release(); }
    }

    public void Quit() => _completion.TrySetResult(0);

    public void ReportError(string message)
    {
        Console.Error.WriteLine(message);
        Error?.Invoke(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.Changed -= OnChanged;
        _controller.Dispose();
    }
}
