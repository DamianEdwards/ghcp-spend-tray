using GHCPSpendTray.Linux;
using GHCPSpendTray.Shared;
using Tmds.DBus.Protocol;

internal static class PlatformTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }

    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux platform tests require Linux.");
        string root = Path.Combine(Path.GetTempPath(), "ghcp-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var registration = new XdgStartupRegistration(root, Path.Combine(root, "helper with spaces"), true);
            Check(registration.CanChange && !registration.Enabled, "XDG startup is opt-in");
            await registration.SetEnabledAsync(true);
            string original = File.ReadAllText(registration.FilePath);
            Check(registration.Enabled && original.Contains("\" --status-notifier\n") &&
                (File.GetUnixFileMode(registration.FilePath) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0,
                "Startup preserves the native tray mode and has owner-only permissions");
            await registration.SetEnabledAsync(true);
            Check(File.ReadAllText(registration.FilePath) == original, "Startup enable is idempotent");
            File.AppendAllText(registration.FilePath, "# user change\n");
            Check(!registration.CanChange && !registration.Enabled, "Modified startup entries are not owned");
            bool refused = false;
            try { await registration.SetEnabledAsync(false); }
            catch (PlatformOperationException) { refused = true; }
            Check(refused && File.Exists(registration.FilePath), "Disabling never deletes modified startup entries");
            File.WriteAllText(registration.FilePath, original);
            await registration.SetEnabledAsync(false);
            Check(!File.Exists(registration.FilePath), "Owned startup entries can be removed");
            string target = Path.Combine(root, "user-file");
            File.WriteAllText(target, "preserve");
            File.CreateSymbolicLink(registration.FilePath, target);
            Check(!registration.CanChange && !registration.Enabled, "Symlink startup entries are rejected");
            File.Delete(registration.FilePath);
            Check(!new XdgStartupRegistration(root, null).CanChange, "Managed-host execution cannot register dotnet as startup");
            Check(XdgStartupRegistration.Quote("/tmp/a%$`\"\\b").Contains("%%"),
                "Desktop Exec arguments escape field codes and shell metacharacters");
            using var session = new LinuxSession(new DemoController("", empty: true), demo: true);
            await session.StartAsync();
            using var login = new DBusConnection(DBusAddress.Session!);
            await login.ConnectAsync();
            MessageBuffer OwnLogin()
            {
                using var writer = login.GetMessageWriter();
                writer.WriteMethodCallHeader(destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus",
                    @interface: "org.freedesktop.DBus", member: "RequestName", signature: "su");
                writer.WriteString("org.freedesktop.login1"); writer.WriteUInt32(4);
                return writer.CreateMessage();
            }
            Check(await login.CallMethodAsync(OwnLogin(), static (message, _) => message.GetBodyReader().ReadUInt32()) == 1,
                "Synthetic login service owns only the private test bus");
            await using var observer = new ResumeObserver(session, DBusAddress.Session);
            await observer.StartAsync();
            var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Changed += () => resumed.TrySetResult();
            {
                using var writer = login.GetMessageWriter();
                writer.WriteSignalHeader(path: "/org/freedesktop/login1", @interface: "org.freedesktop.login1.Manager",
                    member: "PrepareForSleep", signature: "b");
                writer.WriteBool(false);
                Check(login.TrySendMessage(writer.CreateMessage()), "Synthetic resume signal is sent");
            }
            await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(session.Snapshot.Revision == 2, "System resume refreshes through the shared controller");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
