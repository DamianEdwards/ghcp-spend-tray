import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import {parseSnapshot} from './snapshot.js';

export const BUS_NAME = 'io.github.ghcpspendtray.LinuxDemo';
export const BUS_PATH = '/io/github/ghcpspendtray/LinuxDemo';
export const BUS_INTERFACE = `${BUS_NAME}4`;

export class DemoClient {
    get running() { return Boolean(this._proxy?.g_name_owner); }
    constructor(onSnapshot, onError, style = null) {
        this._onSnapshot = onSnapshot;
        this._onError = onError;
        this._style = style;
        this._cancellable = new Gio.Cancellable();
        this._disposed = false;
        this._connecting = false;
        this._proxy = null;
        this._revision = -1;
        this._connect();
    }

    _error(error) {
        if (this._disposed)
            return;
        console.error('GHCPSpendTray desktop request failed.');
        this._onError(error.message);
    }

    _connect() {
        if (this._connecting || this._disposed)
            return;
        this._connecting = true;
        Gio.DBusProxy.new_for_bus(Gio.BusType.SESSION, Gio.DBusProxyFlags.DO_NOT_LOAD_PROPERTIES,
            null, BUS_NAME, BUS_PATH, BUS_INTERFACE, this._cancellable, (_source, result) => {
                this._connecting = false;
                if (this._disposed)
                    return;
                try {
                    this._proxy = Gio.DBusProxy.new_for_bus_finish(result);
                    this._signalId = this._proxy.connect('g-signal', (_proxy, _sender, name, args) => {
                        if (name === 'Changed')
                            this._receive(args.deep_unpack()[0]);
                    });
                    this._ownerId = this._proxy.connect('notify::g-name-owner', () => {
                        this._revision = -1;
                        if (this._proxy.g_name_owner)
                            this.refresh();
                        else
                            this._error(new Error('The helper stopped. Choose Refresh to restart it.'));
                    });
                    this.refresh();
                } catch (error) {
                    this._error(error);
                }
            });
    }

    _receive(json) {
        try {
            const snapshot = parseSnapshot(json);
            if (snapshot.revision < this._revision)
                return;
            this._revision = snapshot.revision;
            this._onSnapshot(snapshot);
        } catch (error) {
            this._error(error);
        }
    }

    call(method, parameters = null, completed = null) {
        if (this._disposed)
            return;
        if (!this._proxy) {
            this._error(new Error('The helper is not connected yet.'));
            this._connect();
            return;
        }
        this._proxy.call(method, parameters, Gio.DBusCallFlags.NONE, 130000, this._cancellable,
            (proxy, result) => {
                if (this._disposed)
                    return;
                try {
                    const reply = proxy.call_finish(result).deep_unpack();
                    completed?.(reply);
                } catch (error) {
                    this._error(error);
                }
            });
    }

    refresh() {
        if (!this._proxy) {
            this._connect();
            return;
        }
        if (this._style !== null)
            this.call('SetStyle', new GLib.Variant('(s)', [this._style]));
        this.call('GetSnapshot', null, reply => this._receive(reply[0]));
    }

    setStyle(style) {
        this._style = style;
        this.call('SetStyle', new GLib.Variant('(s)', [style]));
    }

    destroy() {
        this._disposed = true;
        this._cancellable.cancel();
        if (this._proxy) {
            this._proxy.disconnect(this._signalId);
            this._proxy.disconnect(this._ownerId);
            this._proxy = null;
        }
    }
}
