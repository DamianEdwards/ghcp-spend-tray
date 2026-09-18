using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GHSpend.App.Platform;

internal static partial class ReadinessHandoff
{
    private const string Prefix = "GHSpend.Ready.";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static void LaunchAndWait(string executable)
    {
        var id = Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(Prefix + id, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(Timeout);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "--handoff " + id,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            // Keep shell security/SmartScreen behavior for downloaded executables.
            UseShellExecute = true
        }) ?? throw new IOException("Windows did not return a process for the installed GHSpend executable.");

        try
        {
            var ready = WaitForReadyAsync(server, process.Id, timeout.Token);
            var exited = process.WaitForExitAsync(timeout.Token);
            var completed = Task.WhenAny(ready, exited).GetAwaiter().GetResult();
            if (completed == exited)
            {
                exited.GetAwaiter().GetResult();
                throw new IOException($"The installed GHSpend process exited before its window and tray were ready (exit code {process.ExitCode}).");
            }
            ready.GetAwaiter().GetResult();
            if (process.HasExited)
                throw new IOException("The installed GHSpend process exited during readiness handoff.");
        }
        catch (Exception original)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    if (!process.WaitForExit(5000))
                        throw new IOException("The failed installed GHSpend process did not stop.");
                }
            }
            catch (Exception stopError)
            {
                throw new HandoffChildStillRunningException(original, stopError);
            }
            if (original is OperationCanceledException)
                throw new IOException("The installed GHSpend window and tray did not become ready within 30 seconds.", original);
            throw;
        }
        finally
        {
            timeout.Cancel();
        }
    }

    internal static async Task WaitForReadyAsync(NamedPipeServerStream server, int expectedProcess, CancellationToken token)
    {
        await server.WaitForConnectionAsync(token).ConfigureAwait(false);
        if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var processId))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not verify the GHSpend readiness client.");
        if (processId != (uint)expectedProcess)
            throw new IOException("The readiness response did not come from the installed GHSpend process.");
        var response = new byte[1];
        await server.ReadExactlyAsync(response, token).ConfigureAwait(false);
        if (response[0] != 1)
            throw new IOException("The installed GHSpend process sent an invalid readiness response.");
    }

    public static void Signal(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("Invalid GHSpend readiness identifier.", nameof(id));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new NamedPipeClientStream(".", Prefix + id, PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            client.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            client.WriteAsync(new byte[] { 1 }, timeout.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex)
        {
            throw new IOException("The GHSpend installer did not accept the readiness handoff.", ex);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}

internal sealed class HandoffChildStillRunningException(Exception original, Exception stopError) : IOException(
    "Readiness handoff failed, and the child could not be stopped. The executable was not rolled back; any previous executable backup was retained. Exit GHSpend before recovering it.",
    new AggregateException(original, stopError));
