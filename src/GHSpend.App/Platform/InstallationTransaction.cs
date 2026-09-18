using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace GHSpend.App.Platform;

internal sealed class InstallationTransaction(IInstallFiles files, StartupRegistration startup, Action<string>? warning = null)
{
    public void Execute(string source, string destination, Action<string> launchAndAwaitReady, bool enableStartupOnFirstInstall = true)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var staging = destination + "." + suffix + ".stage";
        var backup = destination + "." + suffix + ".backup";
        var existed = files.Exists(destination);
        var previousStartup = startup.Capture();
        var promoted = false;
        var startupTouched = false;
        var successful = false;
        try
        {
            files.Copy(source, staging);
            if (!CryptographicOperations.FixedTimeEquals(files.Hash(source), files.Hash(staging)))
                throw new IOException("The staged GHSpend executable did not match the downloaded executable.");
            files.Promote(staging, destination, existed ? backup : null);
            promoted = true;
            // An ordinary relaunch or upgrade must not re-enable startup disabled by the user.
            if (!existed && enableStartupOnFirstInstall)
            {
                startupTouched = true;
                startup.SetEnabled(true);
            }
            launchAndAwaitReady(destination);
            successful = true;
        }
        catch (Exception original)
        {
            var failures = new List<Exception> { original };
            if (startupTouched)
            {
                try { startup.Restore(previousStartup); }
                catch (Exception rollbackError) { failures.Add(rollbackError); }
            }
            if (promoted && original is not HandoffChildStillRunningException)
            {
                try { files.Rollback(destination, existed ? backup : null); }
                catch (Exception rollbackError) { failures.Add(rollbackError); }
            }
            if (failures.Count > 1)
                throw new AggregateException("GHSpend installation failed and rollback was incomplete. Previous executable backups were retained.", failures);
            throw;
        }
        finally
        {
            // Cleanup must not turn a completed handoff into a rollback of a running executable.
            TryDelete(staging, "GHSpend could not remove an installer staging file. Check the app data directory for leftover .stage files.");
            if (successful)
                TryDelete(backup, "GHSpend started successfully, but an old executable backup could not be removed. Check the app data directory for leftover .backup files.");
        }
    }

    private void TryDelete(string path, string diagnostic)
    {
        try { files.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (warning is null)
                Bootstrap.ReportWarning(diagnostic);
            else
            {
                try { warning(diagnostic); }
                catch
                {
                    Bootstrap.ReportWarning(diagnostic);
                    Bootstrap.ReportWarning("GHSpend could not deliver an installer cleanup warning to a diagnostic subscriber.");
                }
            }
        }
    }
}

internal interface IInstallFiles
{
    bool Exists(string path);
    void Copy(string source, string staging);
    byte[] Hash(string path);
    void Promote(string staging, string destination, string? backup);
    void Rollback(string destination, string? backup);
    void Delete(string path);
}

internal sealed partial class WindowsInstallFiles : IInstallFiles
{
    public bool Exists(string path) => File.Exists(path);

    public void Copy(string source, string staging)
    {
        InstallationPaths.RejectReparseAncestors(staging);
        // CopyFile preserves alternate data streams, including Zone.Identifier (Mark of the Web).
        // Do not use a byte-stream copy or Unblock-File here.
        if (!CopyFile(source, staging, failIfExists: true))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not stage the GHSpend executable.");
        InstallationPaths.ProtectStagedFile(staging);
    }

    public byte[] Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(stream);
    }

    public void Promote(string staging, string destination, string? backup)
    {
        InstallationPaths.RejectReparseAncestors(staging);
        InstallationPaths.RejectReparseAncestors(destination);
        if (backup is null)
            File.Move(staging, destination, overwrite: false);
        else
            File.Replace(staging, destination, backup, ignoreMetadataErrors: false);
    }

    public void Rollback(string destination, string? backup)
    {
        InstallationPaths.RejectReparseAncestors(destination);
        if (backup is null)
            File.Delete(destination);
        else
        {
            InstallationPaths.RejectReparseAncestors(backup);
            File.Move(backup, destination, overwrite: true);
        }
    }

    public void Delete(string path)
    {
        InstallationPaths.RejectReparseAncestors(path);
        File.Delete(path);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CopyFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CopyFile(string source, string destination, [MarshalAs(UnmanagedType.Bool)] bool failIfExists);
}
