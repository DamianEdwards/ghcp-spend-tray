namespace GHCPSpendTray.Linux;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine("GHCPSpendTray Linux helper [--demo|--demo-empty] [--status-notifier]");
            Console.WriteLine("--command GetSnapshot|GetSignIn|Refresh|Execute|SetStyle|Preview|Quit [argument]");
            Console.WriteLine("Real accounts use Secret Service. --demo modes are isolated and synthetic.");
            return 0;
        }
        bool command = args is ["--command", _] or ["--command", _, _];
        if (!command && (args.Any(arg => arg is not ("--demo" or "--demo-empty" or "--status-notifier")) ||
            args.Distinct().Count() != args.Length || args.Contains("--demo") && args.Contains("--demo-empty")))
        {
            Console.Error.WriteLine("Unknown option. Use --help.");
            return 2;
        }
        try
        {
            if (command)
            {
                Console.WriteLine(await DemoCommand.ExecuteAsync(args[1], args.Length == 3 ? args[2] : null));
                return 0;
            }
            bool notifier = args.Contains("--status-notifier") ||
                (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':')
                    .Any(value => value.Equals("Hyprland", StringComparison.OrdinalIgnoreCase));
            using var session = !args.Contains("--demo") && !args.Contains("--demo-empty") ? LinuxSession.CreateReal(notifier) :
                new LinuxSession(new Shared.DemoController("", args.Contains("--demo-empty")), demo: true);
            using var bus = new DemoBus(session);
            if (!await bus.StartAsync()) return 0;
            await session.StartAsync();
            await using var resume = new ResumeObserver(session);
            if (!session.Snapshot.Demo) await resume.StartAsync();
            await using var tray = new StatusNotifier(session, StatusNotifier.OpenPopupAsync);
            if (notifier)
                tray.Start();
            var disconnected = bus.DisconnectedAsync();
            if (await Task.WhenAny(session.Completion, disconnected) == disconnected)
            {
                Console.Error.WriteLine($"Desktop session bus disconnected: {(await disconnected)?.Message ?? "closed"}");
                return 1;
            }
            return await session.Completion;
        }
        catch (Exception ex)
        {
            bool actionError = ex is Tmds.DBus.Protocol.DBusErrorReplyException dbus &&
                dbus.ErrorName == DemoBus.Interface + ".ActionFailed";
            Console.Error.WriteLine(actionError || ex is Shared.AppOperationException or Shared.PlatformOperationException or ArgumentException
                ? ex.Message : $"Linux helper failed ({ex.GetType().Name}).");
            return 1;
        }
    }
}
