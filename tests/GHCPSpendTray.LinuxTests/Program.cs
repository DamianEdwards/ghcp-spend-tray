using System.Text.Json;
using GHCPSpendTray.Core;
using GHCPSpendTray.Linux;
using Tmds.DBus.Protocol;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS: {message}");
}

using var session = new DemoSession();
await session.StartAsync();
using var service = new DemoBus(session);
Check(await service.StartAsync(), "Own the isolated helper bus");
Check(session.Snapshot.Percent == 34.2 && session.Snapshot.Consumption == "$42.75",
    "Shared controller supplies weighted percentage and exact consumption");
Check(session.Snapshot.Accounts[0].Percent == 105, "Over-allocation is not clamped in presentation data");
using var client = new DBusConnection(DBusAddress.Session!);
await client.ConnectAsync();

MessageBuffer Call(string method, string? value = null)
{
    using var writer = client.GetMessageWriter();
    writer.WriteMethodCallHeader(destination: DemoBus.Name, path: service.Path,
        @interface: DemoBus.Interface, member: method, signature: value is null ? null : "s");
    if (value is not null) writer.WriteString(value);
    return writer.CreateMessage();
}

var json = await client.CallMethodAsync(Call("GetSnapshot"), static (message, _) => message.GetBodyReader().ReadString());
using var snapshot = JsonDocument.Parse(json);
Check(snapshot.RootElement.GetProperty("version").GetInt32() == 2 &&
    snapshot.RootElement.GetProperty("demo").GetBoolean(), "Source-generated, versioned synthetic contract");
Check(!json.Contains("token", StringComparison.OrdinalIgnoreCase), "Presentation contract contains no credentials");
long revision = session.Snapshot.Revision;
await client.CallMethodAsync(Call("Refresh"));
Check(session.Snapshot.Revision == revision + 1, "Refresh updates the shared controller");
await client.CallMethodAsync(Call("SetStyle", "Percentage"));
Check(session.Settings.TrayStyle == TrayIconStyle.Percentage, "Native preferences can update shared presentation");
await client.CallMethodAsync(Call("AddDemoAccount"));
Check(session.Snapshot.Accounts.Length == 3 && session.Snapshot.Consumption == "$55.25",
    "Adding a demo account updates shared totals");
using (var commandSnapshot = JsonDocument.Parse(await DemoCommand.ExecuteAsync("GetSnapshot")))
    Check(commandSnapshot.RootElement.GetProperty("accounts").GetArrayLength() == 3,
        "Quickshell command adapter returns plain, versioned JSON");
bool invalidCommand = false;
try { await DemoCommand.ExecuteAsync("Quit"); }
catch (ArgumentException) { invalidCommand = true; }
Check(invalidCommand, "Presentation command adapter rejects non-UI commands");
foreach (var request in new[] { ("SetStyle", "Invalid"), ("Refresh", "unexpected") })
{
    bool rejected = false;
    try { await client.CallMethodAsync(Call(request.Item1, request.Item2)); }
    catch (DBusErrorReplyException ex) when (ex.ErrorName == "org.freedesktop.DBus.Error.InvalidArgs") { rejected = true; }
    Check(rejected, $"Invalid {request.Item1} input is explicitly rejected");
}
using (var second = new DemoBus(session))
    Check(!await second.StartAsync(), "A duplicate helper exits without replacing the owner");
using (var empty = new DemoSession(empty: true))
{
    await empty.StartAsync();
    Check(empty.Snapshot.Percent is null && empty.Snapshot.Consumption == "Unavailable", "Empty is unavailable, not zero");
    await empty.AddExampleAsync();
    Check(empty.Snapshot.Percent == 25 && empty.Snapshot.Accounts.Length == 1, "Empty demo can add synthetic accounts");
}
await client.CallMethodAsync(Call("Quit"));
Check(session.Completion.IsCompletedSuccessfully, "Explicit helper shutdown completes");
