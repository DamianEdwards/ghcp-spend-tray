using Tmds.DBus.Protocol;

namespace GHCPSpendTray.Linux;

internal sealed class ResumeObserver(LinuxSession session, string? address = null) : IAsyncDisposable
{
    private DBusConnection? _connection;
    private readonly object _gate = new();
    private IDisposable? _subscription;
    private Task _refresh = Task.CompletedTask;
    private bool _disposed;

    internal async Task StartAsync()
    {
        try
        {
            _connection = new(address ?? DBusAddress.System ??
                throw new InvalidOperationException("No system bus is available."));
            await _connection.ConnectAsync();
            _subscription = await _connection.AddMatchAsync(new MatchRule
            {
                Type = MessageType.Signal, Sender = "org.freedesktop.login1",
                Path = "/org/freedesktop/login1", Interface = "org.freedesktop.login1.Manager",
                Member = "PrepareForSleep"
            }, static (message, _) => message.GetBodyReader().ReadBool(),
                notification =>
                {
                    if (!notification.HasValue)
                    {
                        session.ReportError("Resume detection disconnected. Restart the helper to reconnect; polling remains active.");
                        return;
                    }
                    lock (_gate)
                    {
                        if (!notification.Value && !_disposed && _refresh.IsCompleted)
                            _refresh = RefreshAsync();
                    }
                }, emitOnCapturedContext: false);
        }
        catch (Exception ex)
        {
            session.ReportError($"Login service resume detection unavailable ({ex.GetType().Name}); polling remains active.");
        }
    }

    private async Task RefreshAsync()
    {
        try { await session.ExecuteAsync("""{"kind":"resume"}"""); }
        catch (Exception ex) { session.ReportError($"Resume refresh failed ({ex.GetType().Name}). Use Refresh to retry."); }
    }

    public async ValueTask DisposeAsync()
    {
        Task pending;
        lock (_gate) { _disposed = true; pending = _refresh; }
        _subscription?.Dispose();
        _connection?.Dispose();
        await pending;
    }
}
