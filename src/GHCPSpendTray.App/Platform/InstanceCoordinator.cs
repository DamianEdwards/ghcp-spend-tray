using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace GHCPSpendTray.App.Platform;

internal sealed class InstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _pipeName;
    private readonly object _callbackLock = new();
    private readonly Task _listener;
    private Action? _activate;
    private bool _pendingActivation;
    private bool _ready;
    private bool _disposed;

    private InstanceCoordinator(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        // Create synchronously so an immediate second launch finds the pipe.
        var server = CreateServer();
        _listener = ListenAsync(server);
    }

    public static string GetScope(string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User?.Value ?? throw new IOException("Could not identify the current Windows user.");
        var scope = user + ":" + scopeKey.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..32];
    }

    public static string MutexName(string scope) => @"Global\GHCPSpendTray.Runtime." + scope;
    public static string PipeName(string scope) => "GHCPSpendTray.Activate." + scope;

    internal static Mutex CreateMutex(string name) =>
        new(false, name, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });

    internal static bool TryAcquire(Mutex mutex)
    {
        try { return mutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }

    public static InstanceCoordinator? Start(string scope, bool activateExisting = true)
    {
        var mutex = CreateMutex(MutexName(scope));
        if (!TryAcquire(mutex))
        {
            mutex.Dispose();
            if (activateExisting) Activate(scope);
            return null;
        }
        try
        {
            return new InstanceCoordinator(mutex, PipeName(scope));
        }
        catch
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    public static bool IsRunning(string scope)
    {
        using var mutex = CreateMutex(MutexName(scope));
        if (!TryAcquire(mutex))
            return true;
        mutex.ReleaseMutex();
        return false;
    }

    public static void Activate(string scope)
    {
        using var client = new NamedPipeClientStream(".", PipeName(scope), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            client.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            client.WriteAsync(new byte[] { 1 }, timeout.Token).AsTask().GetAwaiter().GetResult();
            var response = new byte[1];
            client.ReadExactlyAsync(response, timeout.Token).AsTask().GetAwaiter().GetResult();
            if (response[0] != 1)
                throw new IOException("The running GHCPSpendTray instance did not accept activation.");
        }
        catch (OperationCanceledException ex)
        {
            throw new IOException("GHCPSpendTray is already running but did not respond to activation within five seconds.", ex);
        }
    }

    public void RegisterActivationCallback(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_callbackLock)
            _activate = callback;
        DispatchPending();
    }

    public void MarkReady()
    {
        lock (_callbackLock)
            _ready = true;
        DispatchPending();
    }

    private NamedPipeServerStream CreateServer() => new(_pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ListenAsync(NamedPipeServerStream server)
    {
        using (server)
        {
            while (!_shutdown.IsCancellationRequested)
            {
                try
                {
                    await server.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                    var command = new byte[1];
                    await server.ReadExactlyAsync(command, requestTimeout.Token).ConfigureAwait(false);
                    if (command[0] == 1)
                    {
                        lock (_callbackLock)
                            _pendingActivation = true;
                        DispatchPending();
                        await server.WriteAsync(new byte[] { 1 }, requestTimeout.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                finally
                {
                    if (server.IsConnected)
                        server.Disconnect();
                }
            }
        }
    }

    public event Action<Exception>? Diagnostic;

    private void DispatchPending()
    {
        Action? callback;
        lock (_callbackLock)
        {
            if (!_ready || !_pendingActivation || _activate is null || _disposed)
                return;
            _pendingActivation = false;
            callback = _activate;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_callbackLock)
            {
                if (_disposed)
                    return;
            }
            try { callback(); }
            catch (Exception ex)
            {
                try { Diagnostic?.Invoke(ex); }
                catch { /* A diagnostic subscriber must never terminate the IPC worker. */ }
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        lock (_callbackLock)
            _disposed = true;
        _shutdown.Cancel();
        try { _listener.GetAwaiter().GetResult(); }
        finally
        {
            _shutdown.Dispose();
            // BootstrapRuntime is created and disposed on the native window/message-loop thread.
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
