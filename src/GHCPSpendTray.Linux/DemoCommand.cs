using Tmds.DBus.Protocol;

namespace GHCPSpendTray.Linux;

internal static class DemoCommand
{
    internal static async Task<string> ExecuteAsync(string method, string? argument = null)
    {
        if (method is not ("GetSnapshot" or "GetSignIn" or "Refresh" or "AddDemoAccount" or "SetStyle" or "Execute" or "Preview" or "Quit") ||
            method == "SetStyle" && argument is not ("Pie" or "Percentage") ||
            method is "Execute" or "Preview" && argument is null ||
            method is not ("SetStyle" or "Execute" or "Preview") && argument is not null)
            throw new ArgumentException("Invalid desktop command or argument.");
        using var client = new DBusConnection(DBusAddress.Session
            ?? throw new InvalidOperationException("A desktop session bus is required."));
        await client.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        if (method == "Preview")
            return await client.CallMethodAsync(Call(client, method, argument),
                static (message, _) => message.GetBodyReader().ReadString()).WaitAsync(TimeSpan.FromSeconds(10));
        if (method == "Quit")
        {
            await client.CallMethodAsync(Call(client, method, null)).WaitAsync(TimeSpan.FromSeconds(10));
            return "{}";
        }
        if (method is not ("GetSnapshot" or "GetSignIn"))
            await client.CallMethodAsync(Call(client, method, argument)).WaitAsync(TimeSpan.FromSeconds(130));
        return await client.CallMethodAsync(Call(client, method == "GetSignIn" ? method : "GetSnapshot", null),
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
