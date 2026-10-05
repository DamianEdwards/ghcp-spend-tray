using Tmds.DBus.Protocol;

namespace GHCPSpendTray.Linux;

internal static class DemoCommand
{
    internal static async Task<string> ExecuteAsync(string method, string? argument = null)
    {
        if (method is not ("GetSnapshot" or "Refresh" or "AddDemoAccount" or "SetStyle") ||
            method == "SetStyle" && argument is not ("Pie" or "Percentage") ||
            method != "SetStyle" && argument is not null)
            throw new ArgumentException("Invalid demo command or argument.");
        using var client = new DBusConnection(DBusAddress.Session
            ?? throw new InvalidOperationException("A desktop session bus is required."));
        await client.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        if (method != "GetSnapshot")
            await client.CallMethodAsync(Call(client, method, argument)).WaitAsync(TimeSpan.FromSeconds(10));
        return await client.CallMethodAsync(Call(client, "GetSnapshot", null),
            static (message, _) => message.GetBodyReader().ReadString()).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static MessageBuffer Call(DBusConnection client, string method, string? argument)
    {
        using var writer = client.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: DemoBus.Name, path: "/io/github/ghcpspendtray/LinuxDemo",
            @interface: DemoBus.Interface, member: method, signature: argument is null ? null : "s");
        if (argument is not null) writer.WriteString(argument);
        return writer.CreateMessage();
    }
}
