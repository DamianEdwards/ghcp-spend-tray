using System.Text;
using System.Text.Json;
using GHCPSpendTray.Core;
using GHCPSpendTray.Shared;
using Tmds.DBus.Protocol;

namespace GHCPSpendTray.Linux;

internal sealed class DemoBus(LinuxSession session) : IPathMethodHandler, IDisposable
{
    internal const string Name = "io.github.ghcpspendtray.LinuxDemo";
    internal const string Interface = Name + "4";
    public string Path => "/io/github/ghcpspendtray/LinuxDemo";
    public bool HandlesChildPaths => false;
    private readonly DBusConnection _connection = new(DBusAddress.Session
        ?? throw new InvalidOperationException("A desktop session D-Bus is required."));
    private bool _ownsName;
    private static readonly byte[] Introspection = Encoding.UTF8.GetBytes($"""
        <interface name="{Interface}">
          <method name="GetSnapshot"><arg type="s" direction="out"/></method>
          <method name="GetSignIn"><arg type="s" direction="out"/></method>
          <method name="Execute"><arg name="request" type="s" direction="in"/></method>
          <method name="Preview"><arg name="request" type="s" direction="in"/><arg type="s" direction="out"/></method>
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
        if (_ownsName)
        {
            session.Changed += OnChanged;
            session.SetNotificationHandler(NotifyAsync);
        }
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
            session.ReportError("Unable to publish the updated snapshot to the desktop bus.");
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([Introspection]);
            return default;
        }
        // Cached desktop code cannot consume the current account contract.
        if (context.Request.InterfaceAsString is Name + "2" or Name + "3")
        {
            if (context.Request.MemberAsString == "Quit" && context.Request.Signature.IsEmpty)
            {
                Reply(context);
                session.Quit();
            }
            else
            {
                context.ReplyError(Name + ".UpgradeRequired",
                    "Log out of your desktop and back in to load the updated panel. " +
                    "The running desktop integration is outdated. Your saved accounts are unchanged.");
            }
            return default;
        }
        if (context.Request.InterfaceAsString != Interface)
        {
            context.ReplyUnknownMethodError();
            return default;
        }
        string method = context.Request.MemberAsString!;
        if (method == "Preview")
        {
            if (context.Request.SignatureAsString != "s") return Invalid(context, "Preview requires a JSON string.");
            return ReplyPreviewAsync(context, context.Request.GetBodyReader().ReadString());
        }
        if (method == "Execute")
        {
            if (context.Request.SignatureAsString != "s")
                return Invalid(context, "Execute requires a JSON string.");
            string request = context.Request.GetBodyReader().ReadString();
            return RunActionAsync(context, () => session.ExecuteAsync(request));
        }

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
            case "GetSignIn":
                return ReplyStateAsync(context, method == "GetSignIn");
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

    private async ValueTask ReplyPreviewAsync(MethodContext context, string json)
    {
        try
        {
            await session.StartAsync();
            string preview = session.PreviewJson(json);
            using var reply = context.CreateReplyWriter("s");
            reply.WriteString(preview);
            context.Reply(reply.CreateMessage());
        }
        catch (Exception ex) { ReplyFailure(context, ex); }
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
            await session.StartAsync();
            await action();
            Reply(context);
        }
        catch (Exception ex) { ReplyFailure(context, ex); }
    }

    private async ValueTask ReplyStateAsync(MethodContext context, bool signIn)
    {
        try
        {
            await session.StartAsync();
            using var reply = context.CreateReplyWriter("s");
            reply.WriteString(signIn ? session.SignInJson() : session.Snapshot.ToJson());
            context.Reply(reply.CreateMessage());
        }
        catch (Exception ex) { ReplyFailure(context, ex); }
    }

    private void ReplyFailure(MethodContext context, Exception ex)
    {
        string message = ex switch
        {
            AppOperationException or PlatformOperationException or ArgumentException => ex.Message,
            JsonException => "Invalid account request.",
            _ => "The operation failed. Check the helper diagnostic log and try again."
        };
        session.ReportError($"Desktop action failed ({ex.GetType().Name}).");
        context.ReplyError(Interface + ".ActionFailed", message);
    }

    private async Task<bool> NotifyAsync(NotificationView notification)
    {
        try
        {
            await _connection.CallMethodAsync(Notification(notification),
                static (message, _) => message.GetBodyReader().ReadUInt32()).WaitAsync(TimeSpan.FromSeconds(10));
            return true;
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or TimeoutException)
        {
            session.ReportError($"Desktop notification unavailable ({ex.GetType().Name}); alert was not acknowledged.");
            return false;
        }
    }

    private MessageBuffer Notification(NotificationView notification)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.Notifications",
            path: "/org/freedesktop/Notifications", @interface: "org.freedesktop.Notifications",
            member: "Notify", signature: "susssasa{sv}i");
        writer.WriteString("GHCPSpendTray");
        writer.WriteUInt32(0);
        writer.WriteString("utilities-system-monitor");
        writer.WriteString(notification.Title);
        writer.WriteString(System.Security.SecurityElement.Escape(notification.Message));
        writer.WriteArray(Array.Empty<string>());
        var hints = writer.WriteArrayStart(DBusType.Struct);
        writer.WriteArrayEnd(hints);
        writer.WriteInt32(-1);
        return writer.CreateMessage();
    }

    internal Task<Exception?> DisconnectedAsync() => _connection.DisconnectedAsync();

    public void Dispose()
    {
        if (_ownsName) session.Changed -= OnChanged;
        _connection.Dispose();
    }
}
