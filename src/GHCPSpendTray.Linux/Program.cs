namespace GHCPSpendTray.Linux;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine("GHCPSpendTray Linux demo helper [--demo-empty]");
            Console.WriteLine("--command GetSnapshot|Refresh|AddDemoAccount|SetStyle [Pie|Percentage]");
            Console.WriteLine("Synthetic data only. Activated automatically by the native desktop integration.");
            return 0;
        }
        bool command = args is ["--command", _] or ["--command", _, _];
        if (!command && args.Any(arg => arg != "--demo-empty"))
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
            using var session = new DemoSession(args.Contains("--demo-empty"));
            await session.StartAsync();
            using var bus = new DemoBus(session);
            if (!await bus.StartAsync()) return 0;
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
            Console.Error.WriteLine($"Linux demo helper failed: {ex.Message}");
            return 1;
        }
    }
}
