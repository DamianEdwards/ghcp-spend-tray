using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GHCPSpendTray.Core;
using Tmds.DBus.Protocol;

namespace GHCPSpendTray.Linux;

internal sealed class StatusNotifier(LinuxSession session, Func<string?, bool, Task> open) : IAsyncDisposable
{
    internal const string Watcher = "org.kde.StatusNotifierWatcher";
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);
    private Task? _loop;

    internal void Start() => _loop = RunAsync();
    private async Task RunAsync()
    {
        using var connection = new DBusConnection(DBusAddress.Session!);
        try
        {
            await connection.ConnectAsync();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            string? lastFailure = null;
            do
            {
                try
                {
                    string owner = await connection.CallMethodAsync(OwnerRequest(connection),
                        static (message, _) => message.GetBodyReader().ReadString()).WaitAsync(_stop.Token);
                    var snapshot = session.Snapshot;
                    var icons = (snapshot.Tray ?? TrayPresentation.Unavailable).Icons;
                    var keys = icons.Select(icon => icon.AccountKey ?? "").ToHashSet(StringComparer.Ordinal);
                    foreach (string key in _items.Keys.Where(key => !keys.Contains(key)).ToArray())
                    { _items[key].Dispose(); _items.Remove(key); }
                    foreach (var icon in icons)
                    {
                        string key = icon.AccountKey ?? "";
                        if (!_items.TryGetValue(key, out var item))
                        {
                            item = new Item(icon, snapshot.Style == "Percentage" ? TrayIconStyle.Percentage : TrayIconStyle.Pie, open);
                            try { await item.ConnectAsync(); }
                            catch { item.Dispose(); throw; }
                            _items.Add(key, item);
                        }
                        item.Update(icon, snapshot.Style == "Percentage" ? TrayIconStyle.Percentage : TrayIconStyle.Pie);
                        await item.RegisterAsync(owner).WaitAsync(_stop.Token);
                    }
                    lastFailure = null;
                }
                catch (Exception ex) when (ex is DBusErrorReplyException or TimeoutException)
                {
                    string failure = ex.GetType().Name;
                    if (lastFailure != failure)
                        session.ReportError("Native tray unavailable. Enable/reload Waybar's tray module; registration will retry.");
                    lastFailure = failure;
                }
            } while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { session.ReportError($"Native tray stopped ({ex.GetType().Name}). Restart the helper to reconnect."); }
        finally
        {
            foreach (var item in _items.Values) item.Dispose();
            _items.Clear();
        }
    }

    private static MessageBuffer OwnerRequest(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus",
            @interface: "org.freedesktop.DBus", member: "GetNameOwner", signature: "s");
        writer.WriteString(Watcher);
        return writer.CreateMessage();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null) await _loop;
        _stop.Dispose();
    }

    internal static async Task OpenPopupAsync(string? key, bool settings)
    {
        string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        string launcher = Path.Combine(data, "ghcp-spend-tray-desktop", "ghcp-spend-tray-setup");
        var start = new ProcessStartInfo(launcher) { UseShellExecute = false };
        start.ArgumentList.Add("--panel"); start.ArgumentList.Add("open");
        if (key is not null) { start.ArgumentList.Add("--account"); start.ArgumentList.Add(key); }
        if (settings) start.ArgumentList.Add("--settings");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the installed popup.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException("The installed popup failed to open.");
    }

    internal sealed class Item(TrayIndicator indicator, TrayIconStyle style, Func<string?, bool, Task> open) : IPathMethodHandler, IDisposable
    {
        internal const string Interface = "org.kde.StatusNotifierItem";
        public string Path => "/StatusNotifierItem";
        public bool HandlesChildPaths => false;
        private readonly DBusConnection _connection = new(DBusAddress.Session!);
        private TrayIndicator _icon = indicator;
        private TrayIconStyle _style = style;
        private string? _owner;
        private byte[] _pixels = TrayPixels.Render(indicator, style);
        internal string Id { get; } = "ghcp-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(indicator.AccountKey ?? "rollup")))[..16];
        private static readonly string[] Properties = ["Category", "Id", "Title", "Status", "WindowId", "IconName",
            "IconPixmap", "ToolTip", "ItemIsMenu"];

        internal async Task ConnectAsync()
        {
            await _connection.ConnectAsync();
            _connection.AddMethodHandler(this);
        }
        internal async Task RegisterAsync(string owner)
        {
            if (_owner == owner) return;
            await _connection.CallMethodAsync(Registration()).WaitAsync(TimeSpan.FromSeconds(10));
            _owner = owner;
        }
        private MessageBuffer Registration()
        {
            using var writer = _connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: Watcher, path: "/StatusNotifierWatcher",
                @interface: Watcher, member: "RegisterStatusNotifierItem", signature: "s");
            writer.WriteString(Path);
            return writer.CreateMessage();
        }
        internal void Update(TrayIndicator icon, TrayIconStyle newStyle)
        {
            if (_icon == icon && _style == newStyle) return;
            _icon = icon; _style = newStyle;
            _pixels = TrayPixels.Render(icon, newStyle);
            foreach (string signal in new[] { "NewIcon", "NewToolTip", "NewTitle" })
            {
                using var writer = _connection.GetMessageWriter();
                writer.WriteSignalHeader(path: Path, @interface: Interface, member: signal);
                if (!_connection.TrySendMessage(writer.CreateMessage()))
                    throw new InvalidOperationException("Native tray update could not be sent.");
            }
        }
        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.IsDBusIntrospectRequest)
            {
                context.ReplyIntrospectXml([Encoding.UTF8.GetBytes("""
                    <interface name="org.kde.StatusNotifierItem">
                    <method name="Activate"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
                    <method name="SecondaryActivate"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
                    <method name="ContextMenu"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
                    <property name="Category" type="s" access="read"/><property name="Id" type="s" access="read"/>
                    <property name="Title" type="s" access="read"/><property name="Status" type="s" access="read"/>
                    <property name="WindowId" type="u" access="read"/><property name="IconName" type="s" access="read"/>
                    <property name="IconPixmap" type="a(iiay)" access="read"/><property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
                    <property name="ItemIsMenu" type="b" access="read"/>
                    <signal name="NewIcon"/><signal name="NewToolTip"/><signal name="NewTitle"/>
                    </interface>
                    """)]);
                return default;
            }
            string method = context.Request.MemberAsString!;
            if (context.Request.InterfaceAsString == "org.freedesktop.DBus.Properties" &&
                (method == "GetAll" && context.Request.SignatureAsString == "s" ||
                 method == "Get" && context.Request.SignatureAsString == "ss"))
            {
                var reader = context.Request.GetBodyReader();
                if (reader.ReadString() != Interface) { context.ReplyUnknownMethodError(); return default; }
                string? name = method == "Get" ? reader.ReadString() : null;
                if (name is not null && !Properties.Contains(name))
                { context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", "Unknown tray property."); return default; }
                var reply = context.CreateReplyWriter(name is null ? "a{sv}" : "v");
                try
                {
                    if (name is null)
                    {
                        var array = reply.WriteArrayStart(DBusType.Struct);
                        foreach (string property in Properties)
                        {
                            reply.WriteStructureStart(); reply.WriteString(property); WriteProperty(ref reply, property);
                        }
                        reply.WriteArrayEnd(array);
                    }
                    else WriteProperty(ref reply, name);
                    context.Reply(reply.CreateMessage());
                }
                finally { reply.Dispose(); }
                return default;
            }
            if (context.Request.InterfaceAsString == Interface &&
                method is "Activate" or "SecondaryActivate" or "ContextMenu" &&
                context.Request.SignatureAsString == "ii")
                return ActivateAsync(context, method == "ContextMenu");
            context.ReplyUnknownMethodError();
            return default;
        }
        private async ValueTask ActivateAsync(MethodContext context, bool settings)
        {
            try
            {
                await open(_icon.AccountKey, settings);
                using var reply = context.CreateReplyWriter(null);
                context.Reply(reply.CreateMessage());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Native popup launch failed ({ex.GetType().Name}).");
                context.ReplyError(Interface + ".LaunchFailed", "Could not open the installed Quickshell popup. Check the installation.");
            }
        }
        private void WriteProperty(ref MessageWriter writer, string name)
        {
            if (name == "IconPixmap")
            {
                writer.WriteSignature("a(iiay)");
                WritePixels(ref writer);
            }
            else if (name == "ToolTip")
            {
                writer.WriteSignature("(sa(iiay)ss)"); writer.WriteStructureStart(); writer.WriteString("");
                WritePixels(ref writer);
                writer.WriteString(_icon.Name); writer.WriteString(System.Security.SecurityElement.Escape(_icon.Tooltip));
            }
            else if (name == "WindowId") { writer.WriteSignature("u"); writer.WriteUInt32(0); }
            else if (name == "ItemIsMenu") { writer.WriteSignature("b"); writer.WriteBool(false); }
            else
            {
                writer.WriteSignature("s");
                writer.WriteString(name switch
                {
                    "Category" => "ApplicationStatus", "Id" => Id, "Title" => _icon.Name,
                    "Status" => "Active", _ => ""
                });
            }
        }
        private void WritePixels(ref MessageWriter writer)
        {
            var array = writer.WriteArrayStart(DBusType.Struct);
            writer.WriteStructureStart(); writer.WriteInt32(32); writer.WriteInt32(32); writer.WriteArray(_pixels);
            writer.WriteArrayEnd(array);
        }
        public void Dispose() => _connection.Dispose();
    }
}
