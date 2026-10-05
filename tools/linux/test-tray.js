import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import System from 'system';
import GdkPixbuf from 'gi://GdkPixbuf';

const bus = Gio.DBus.session;
const name = 'io.github.ghcpspendtray.LinuxDemo';
const path = '/io/github/ghcpspendtray/LinuxDemo';
const watcherName = 'org.kde.StatusNotifierWatcher';
const itemInterface = 'org.kde.StatusNotifierItem';
const loop = new GLib.MainLoop(null, false);
let helper;
let failed = false;
const registrations = [];
const delay = () => new Promise(resolve => GLib.timeout_add(GLib.PRIORITY_DEFAULT, 100, () => {
    resolve(); return GLib.SOURCE_REMOVE;
}));
const call = (connection, destination, objectPath, iface, method, parameters = null) =>
    new Promise((resolve, reject) => connection.call(destination, objectPath, iface, method, parameters, null,
        Gio.DBusCallFlags.NONE, 5000, null, (source, result) => {
            try { resolve(source.call_finish(result).deep_unpack()); } catch (error) { reject(error); }
        }));
const execute = request => call(bus, name, path, name + '4', 'Execute',
    new GLib.Variant('(s)', [JSON.stringify(request)]));
function check(value, message) {
    if (!value) throw new Error(message);
    print('PASS: ' + message);
}
async function waitFor(predicate) {
    for (let i = 0; i < 100; i++) {
        if (await predicate()) return;
        await delay();
    }
    throw new Error('Timed out waiting for tray reconciliation');
}
async function watcher() {
    const connection = Gio.DBusConnection.new_for_address_sync(GLib.getenv('DBUS_SESSION_BUS_ADDRESS'),
        Gio.DBusConnectionFlags.AUTHENTICATION_CLIENT | Gio.DBusConnectionFlags.MESSAGE_BUS_CONNECTION, null, null);
    const exported = Gio.DBusExportedObject.wrapJSObject(
        '<node><interface name="org.kde.StatusNotifierWatcher"><method name="RegisterStatusNotifierItem">' +
        '<arg type="s" direction="in"/></method></interface></node>', {
            RegisterStatusNotifierItemAsync(args, invocation) {
                registrations.push({owner: invocation.get_sender(), path: args[0]});
                invocation.return_value(null);
            },
        });
    exported.export(connection, '/StatusNotifierWatcher');
    await call(connection, 'org.freedesktop.DBus', '/org/freedesktop/DBus', 'org.freedesktop.DBus', 'RequestName',
        new GLib.Variant('(su)', [watcherName, 4]));
    return {connection, exported};
}
async function properties(item) {
    const [values] = await call(bus, item.owner, item.path, 'org.freedesktop.DBus.Properties', 'GetAll',
        new GLib.Variant('(s)', [itemInterface]));
    return Object.fromEntries(Object.entries(values).map(([key, value]) => [key, value.deep_unpack()]));
}
async function run() {
    let host = await watcher();
    helper = Gio.Subprocess.new([ARGV[0], '--demo', '--status-notifier'], Gio.SubprocessFlags.NONE);
    await waitFor(() => registrations.length === 1);
    const rollup = registrations[0];
    const initial = await properties(rollup);
    const pixels = initial.IconPixmap[0];
    check(pixels[0] === 32 && pixels[1] === 32 && pixels[2].length === 4096, 'Native tray provides exact ARGB pixmap dimensions');
    const [json] = await call(bus, name, path, name + '4', 'GetSnapshot');
    const image = JSON.parse(json).icons[0].imageUri;
    const loader = new GdkPixbuf.PixbufLoader();
    loader.write(GLib.base64_decode(image.split(',')[1]));
    loader.close();
    const pixbuf = loader.get_pixbuf();
    const rgba = pixbuf.get_pixels();
    check(pixbuf.width === 32 && pixbuf.height === 32 && pixbuf.has_alpha &&
        rgba.every((value, index) => value === pixels[2][Math.floor(index / 4) * 4 + (index % 4 + 1) % 4]),
        'Decoded frontend PNG is pixel-identical to the native tray pixmap');
    check(initial.ToolTip[3].includes('34.2%') && initial.Status === 'Active', 'Tray tooltip uses the shared weighted total');
    await execute({kind: 'settings', settings: {pollMinutes: 60, thresholds: '50, 80', notifications: false,
        startup: false, trayStyle: 'Percentage', trayMode: 'PerAccount'}});
    await waitFor(() => registrations.length === 3);
    const first = registrations[1];
    const numeric = await properties(first);
    check(numeric.ToolTip[3].includes('105%') && numeric.ToolTip[3].includes('Over allocation'),
        'Per-account indicators retain over-allocation qualifiers');
    check(JSON.stringify(numeric.IconPixmap) !== JSON.stringify(initial.IconPixmap), 'Percentage and pie render differently');
    const [exists] = await call(bus, 'org.freedesktop.DBus', '/org/freedesktop/DBus', 'org.freedesktop.DBus',
        'NameHasOwner', new GLib.Variant('(s)', [rollup.owner]));
    check(!exists, 'Removed roll-up connection unregisters its tray item');
    await call(bus, first.owner, first.path, itemInterface, 'Activate', new GLib.Variant('(ii)', [0, 0]));
    await call(bus, first.owner, first.path, itemInterface, 'ContextMenu', new GLib.Variant('(ii)', [0, 0]));
    const [, bytes] = GLib.file_get_contents(ARGV[1] + '/activation');
    const activated = new TextDecoder().decode(bytes);
    check(activated.includes('--panel\nopen\n--account\ngithub.com:1\n') && activated.includes('--settings\n'),
        'Independent activation selects the account; context action opens settings');
    host.exported.unexport();
    host.connection.close_sync(null);
    host = await watcher();
    await waitFor(() => registrations.length === 5);
    check(registrations[3].owner === first.owner, 'Existing items re-register after watcher restart');
    await execute({kind: 'settings', settings: {pollMinutes: 60, thresholds: '50', notifications: false,
        startup: false, trayStyle: 'Pie', trayMode: 'PerAccount',
        excludedTrayAccounts: ['github.com:1', 'example.ghe.com:2']}});
    await waitFor(() => registrations.length === 6);
    check((await properties(registrations[5])).ToolTip[3].includes('No accounts selected'),
        'Excluding every account leaves an explicit unavailable indicator');
    await call(bus, name, path, name + '4', 'Quit');
    host.exported.unexport();
    host.connection.close_sync(null);
}
run().catch(error => { failed = true; printerr(String(error)); }).finally(() => {
    helper?.force_exit();
    loop.quit();
});
loop.run();
System.exit(failed ? 1 : 0);
