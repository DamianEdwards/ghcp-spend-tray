using System.Text.Json;
using GHCPSpendTray.Core;
using GHCPSpendTray.Linux;
using GHCPSpendTray.Shared;
using Tmds.DBus.Protocol;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS: {message}");
}

if (args.Length > 0)
{
    await BackendTests.CredentialsAsync(args[0]);
    return;
}

await BackendTests.RunAsync();
await PlatformTests.RunAsync();

using var session = new LinuxSession(new DemoController(""), demo: true);
await session.StartAsync();
using var service = new DemoBus(session);
Check(await service.StartAsync(), "Own the isolated helper bus");
Check(session.Snapshot.Percent == 34.2 && session.Snapshot.Consumption == "$42.75",
    "Shared controller supplies weighted percentage and exact consumption");
Check(session.Snapshot.Accounts[0].Percent == 105, "Over-allocation is not clamped in presentation data");
using var client = new DBusConnection(DBusAddress.Session!);
await client.ConnectAsync();

MessageBuffer Call(string method, string? value = null, string interfaceName = DemoBus.Interface)
{
    using var writer = client.GetMessageWriter();
    writer.WriteMethodCallHeader(destination: DemoBus.Name, path: service.Path,
        @interface: interfaceName, member: method, signature: value is null ? null : "s");
    if (value is not null) writer.WriteString(value);
    return writer.CreateMessage();
}

var json = await client.CallMethodAsync(Call("GetSnapshot"), static (message, _) => message.GetBodyReader().ReadString());
using var snapshot = JsonDocument.Parse(json);
Check(snapshot.RootElement.GetProperty("version").GetInt32() == 4 &&
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
try { await DemoCommand.ExecuteAsync("Unknown"); }
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
revision = session.Snapshot.Revision;
foreach (string legacy in new[] { DemoBus.Name + "2", DemoBus.Name + "3" })
{
    foreach (var request in new[] {
        ("GetSnapshot", (string?)null), ("GetSignIn", null), ("Refresh", null), ("AddDemoAccount", null),
        ("SetStyle", "Pie"), ("Execute", """{"kind":"refresh"}"""), ("Quit", "unexpected")
    })
    {
        bool rejected = false;
        try { await client.CallMethodAsync(Call(request.Item1, request.Item2, legacy)); }
        catch (DBusErrorReplyException ex) when (ex.ErrorName == DemoBus.Name + ".UpgradeRequired" &&
            ex.Message.Contains("Log out of your desktop and back in", StringComparison.Ordinal))
        {
            rejected = true;
        }
        Check(rejected, $"Outdated {legacy} {request.Item1} receives actionable reload guidance");
    }
}
Check(session.Snapshot.Revision == revision && !session.Completion.IsCompleted,
    "Outdated clients cannot read account data, mutate state or shut down with invalid arguments");
using (var empty = new LinuxSession(new DemoController("", empty: true), demo: true))
{
    await empty.StartAsync();
    Check(empty.Snapshot.Percent is null && empty.Snapshot.Consumption == "Unavailable", "Empty is unavailable, not zero");
    await empty.AddExampleAsync();
    Check(empty.Snapshot.Percent == 25 && empty.Snapshot.Accounts.Length == 1, "Empty demo can add synthetic accounts");
}
foreach (string contract in new[] { DemoBus.Name + "2", DemoBus.Name + "3", DemoBus.Interface })
{
    await client.CallMethodAsync(Call("Quit", interfaceName: contract));
    await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    Check(session.Completion.IsCompletedSuccessfully, $"{contract} retains explicit helper shutdown for upgrades");
}
