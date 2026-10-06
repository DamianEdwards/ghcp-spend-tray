import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import System from 'system';

const directory = ARGV[0];
const {DemoClient, BUS_NAME, BUS_PATH, BUS_INTERFACE} =
    await import(GLib.filename_to_uri(`${directory}/client.js`, null));
const {parseSnapshot, formatPercent} = await import(GLib.filename_to_uri(`${directory}/snapshot.js`, null));
const loop = new GLib.MainLoop(null, false);
let failed = false;
let phase = 0;
let client;
let timeout;

function check(condition, message) {
    if (!condition)
        throw new Error(message);
    print(`PASS: ${message}`);
}

function finish(error = null) {
    if (timeout) {
        GLib.source_remove(timeout);
        timeout = null;
    }
    client?.destroy();
    if (error) {
        printerr(String(error));
        failed = true;
    }
    loop.quit();
}

check(formatPercent(null) === 'Unavailable' && formatPercent(0) === '0%', 'GJS distinguishes unavailable from zero');
for (const json of ['null', '{}', '{"version":1,"demo":true}', '{"version":2,"demo":false}']) {
    let rejected = false;
    try {
        parseSnapshot(json);
    } catch {
        rejected = true;
    }
    check(rejected, 'GJS rejects incompatible snapshots');
}

timeout = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 15, () => {
    timeout = null;
    finish(new Error('Timed out waiting for native helper signals'));
    return GLib.SOURCE_REMOVE;
});
client = new DemoClient(snapshot => {
    try {
        if (phase === 0) {
            check(snapshot.percent === 34.2 && snapshot.consumption === '$42.75', 'D-Bus activation returns shared demo data');
            check(snapshot.accounts[0].percent === 105, 'Native UI receives over-allocation unchanged');
            const invalid = JSON.parse(JSON.stringify(snapshot));
            invalid.accounts.push(invalid.accounts[0]);
            let rejected = false;
            try {
                parseSnapshot(JSON.stringify(invalid));
            } catch {
                rejected = true;
            }
            check(rejected, 'Duplicate account identities are rejected');
            phase = 1;
            client.call('AddDemoAccount');
        } else if (phase === 1 && snapshot.accounts.length === 3) {
            check(snapshot.consumption === '$55.25', 'Changed signal updates the native UI after Add');
            phase = 2;
            client.setStyle('Percentage');
        } else if (phase === 2 && snapshot.style === 'Percentage') {
            check(snapshot.indicator.length > 0, 'Native preferences update the shared indicator');
            phase = 3;
            client.destroy();
            Gio.DBus.session.call(BUS_NAME, BUS_PATH, BUS_INTERFACE, 'Quit', null, null,
                Gio.DBusCallFlags.NONE, 5000, null, (connection, result) => {
                    try {
                        connection.call_finish(result);
                        finish();
                    } catch (error) {
                        finish(error);
                    }
                });
        }
    } catch (error) {
        finish(error);
    }
}, error => finish(new Error(error)));
loop.run();
System.exit(failed ? 1 : 0);
