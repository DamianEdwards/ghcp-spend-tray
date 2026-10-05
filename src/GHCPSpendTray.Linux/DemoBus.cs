using System.Text;
using GHCPSpendTray.Core;
using Tmds.DBus.Protocol;

namespace GHCPSpendTray.Linux;

internal sealed class DemoBus(DemoSession session) : IPathMethodHandler, IDisposable
{
    internal const string Name = "io.github.ghcpspendtray.LinuxDemo";
    internal const string Interface = Name + "2";
    public string Path => "/io/github/ghcpspendtray/LinuxDemo";
    public bool HandlesChildPaths => false;
    private readonly DBusConnection _connection = new(DBusAddress.Session
        ?? throw new InvalidOperationException("A desktop session D-Bus is required."));
    private bool _ownsName;
    private static readonly byte[] Introspection = Encoding.UTF8.GetBytes($"""
        <interface name="{Interface}">
          <method name="GetSnapshot"><arg type="s" direction="out"/></method>
          <method name="Refresh"/>
          <method name="AddDemoAccount"/>
          <method name="SetStyle"><arg name="style" type="s" direction="in"/></method>
          <method name="Quit"/>
          <signal name="Changed"><arg type="s"/></signal>
        </interface>
        """);

    internal async Task<bool> StartAsync()
    {
        await _connection.ConnectAsync();
        _connection.AddMethodHandler(this);
        uint result = await _connection.CallMethodAsync(RequestName(),
            static (message, _) => message.GetBodyReader().ReadUInt32());
        if (result is not (1 or 3)) throw new InvalidOperationException($"Unexpected D-Bus ownership result {result}.");
        _ownsName = result == 1;
        if (_ownsName) session.Changed += OnChanged;
        return _ownsName;
    }

    private MessageBuffer RequestName()
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus",
            @interface: "org.freedesktop.DBus", member: "RequestName", signature: "su");
        writer.WriteString(Name);
        writer.WriteUInt32(4);
        return writer.CreateMessage();
    }

    private void OnChanged()
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteSignalHeader(path: Path, @interface: Interface, member: "Changed", signature: "s");
        writer.WriteString(session.Snapshot.ToJson());
        if (!_connection.TrySendMessage(writer.CreateMessage()))
            session.ReportError("Unable to publish the updated demo snapshot to the desktop bus.");
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([Introspection]);
            return default;
        }
        if (context.Request.InterfaceAsString != Interface)
        {
            context.ReplyUnknownMethodError();
            return default;
        }
        string method = context.Request.MemberAsString!;
        if (method == "SetStyle")
        {
            if (context.Request.SignatureAsString != "s")
                return Invalid(context, "SetStyle requires a string.");
            string value = context.Request.GetBodyReader().ReadString();
            if (value is not ("Pie" or "Percentage")) return Invalid(context, "Style must be Pie or Percentage.");
            return RunActionAsync(context, () => session.SetStyleAsync(
                value == "Pie" ? TrayIconStyle.Pie : TrayIconStyle.Percentage));
        }
        if (!context.Request.Signature.IsEmpty) return Invalid(context, "This method takes no arguments.");
        switch (method)
        {
            case "GetSnapshot":
                using (var reply = context.CreateReplyWriter("s"))
                {
                    reply.WriteString(session.Snapshot.ToJson());
                    context.Reply(reply.CreateMessage());
                }
                return default;
            case "Refresh": return RunActionAsync(context, session.RefreshAsync);
            case "AddDemoAccount": return RunActionAsync(context, session.AddExampleAsync);
            case "Quit":
                Reply(context);
                session.Quit();
                return default;
            default:
                context.ReplyUnknownMethodError();
                return default;
        }
    }

    private static ValueTask Invalid(MethodContext context, string message)
    {
        context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", message);
        return default;
    }

    private static void Reply(MethodContext context)
    {
        using var writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }

    private async ValueTask RunActionAsync(MethodContext context, Func<Task> action)
    {
        try
        {
            await action();
            Reply(context);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            session.ReportError(ex.Message);
            context.ReplyError(Interface + ".ActionFailed", ex.Message);
        }
    }

    internal Task<Exception?> DisconnectedAsync() => _connection.DisconnectedAsync();

    public void Dispose()
    {
        if (_ownsName) session.Changed -= OnChanged;
        _connection.Dispose();
    }
}
