import GLib from 'gi://GLib';
import GdkPixbuf from 'gi://GdkPixbuf';

export const fixture = {
    phase: 'idle',
    expires: new Date(Date.now() + 600000).toISOString(),
    requests: [],
};

export class ExtensionPreferences {
    getSettings() {
        return {get_string: () => ''};
    }
}

const snapshot = {
    demo: false,
    accounts: [{
        key: 'synthetic-account', name: 'Synthetic account', login: 'synthetic', host: 'github.com',
        displayName: '', thresholds: '', spendIncrementUsd: null, clientId: null, showPeriodEstimate: false,
    }],
    settings: {
        pollMinutes: 15, notifications: true, thresholds: '50, 80, 100', spendIncrementUsd: null,
        startup: false, trayStyle: 'Pie', trayMode: 'RollUp', excludedTrayAccounts: [],
        canChangeStartup: true, startupDescription: 'Start monitoring when you sign in.',
    },
};

export class DemoClient {
    constructor(onSnapshot) {
        this.running = true;
        this.onSnapshot = onSnapshot;
        GLib.idle_add(GLib.PRIORITY_DEFAULT, () => {
            this.refresh();
            return GLib.SOURCE_REMOVE;
        });
    }

    refresh() {
        this.onSnapshot(snapshot);
    }

    call(method, parameters, completed) {
        if (method === 'GetSignIn') {
            const waiting = fixture.phase === 'waiting';
            completed([JSON.stringify({
                phase: fixture.phase, message: null,
                code: waiting ? 'TEST-CODE' : null,
                verificationUri: waiting ? 'https://github.com/login/device' : null,
                expires: waiting ? fixture.expires : null,
            })]);
        } else if (method === 'Execute') {
            const request = JSON.parse(parameters.deep_unpack()[0]);
            fixture.requests.push(request);
            if (request.kind === 'signin') fixture.phase = 'waiting';
            if (request.kind === 'cancel') fixture.phase = 'cancelled';
            completed([]);
        } else if (method === 'Preview') {
            const image = new GdkPixbuf.Pixbuf({
                colorspace: GdkPixbuf.Colorspace.RGB, has_alpha: true, bits_per_sample: 8, width: 32, height: 32,
            });
            image.fill(0x55aaffff);
            const [saved, png] = image.save_to_bufferv('png', [], []);
            if (!saved) throw new Error('Could not create the synthetic preview.');
            completed([JSON.stringify({tray: {rollUp: {}}, icons: [{
                name: 'Synthetic', tooltip: 'Synthetic consumption preview: 50% of the selected allocation.',
                imageUri: `data:image/png;base64,${GLib.base64_encode(png)}`,
            }]})]);
        } else {
            throw new Error(`Unexpected preferences method: ${method}`);
        }
    }

    destroy() {
        this.running = false;
    }
}
