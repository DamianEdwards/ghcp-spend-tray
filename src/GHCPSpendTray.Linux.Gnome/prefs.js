import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Gtk from 'gi://Gtk';
import Gdk from 'gi://Gdk';
import {ExtensionPreferences} from 'resource:///org/gnome/Shell/Extensions/js/extensions/prefs.js';
import {DemoClient} from './client.js';
import {parseSignIn, parsePreview} from './snapshot.js';

function actionButtons() {
    return new Gtk.FlowBox({
        selection_mode: Gtk.SelectionMode.NONE,
        homogeneous: true,
        min_children_per_line: 1,
        max_children_per_line: 2,
        column_spacing: 8,
        row_spacing: 8,
        margin_top: 8,
        margin_bottom: 8,
    });
}

export default class GHCPSpendTrayPreferences extends ExtensionPreferences {
    fillPreferencesWindow(window) {
        window.set_default_size(620, 720);
        const page = new Adw.PreferencesPage({title: 'Accounts', icon_name: 'avatar-default-symbolic'});
        const generalPage = new Adw.PreferencesPage({title: 'General', icon_name: 'preferences-system-symbolic'});
        const general = new Adw.PreferencesGroup({title: 'Monitoring'});
        const minutes = new Adw.EntryRow({title: 'Refresh interval (5 to 1440 minutes)'});
        const notifications = new Adw.SwitchRow({title: 'Enable consumption notifications'});
        const thresholds = new Adw.EntryRow({title: 'Alert percentages, e.g. 50, 80, 100'});
        const increment = new Adw.EntryRow({title: 'USD increment (blank or 0 disables)'});
        const startup = new Adw.SwitchRow({title: 'Start monitoring at login'});
        for (const row of [minutes, notifications, thresholds, increment, startup]) general.add(row);
        generalPage.add(general);
        const appearance = new Adw.PreferencesGroup({title: 'Panel indicators',
            description: '! means partial or over allocation; ? means unavailable. Only fresh, current-period, selected accounts contribute.'});
        const style = new Adw.ComboRow({
            title: 'Icon style', use_subtitle: true, model: Gtk.StringList.new(['Pie chart', 'Percentage']),
        });
        const mode = new Adw.ComboRow({title: 'Indicator mode', use_subtitle: true,
            model: Gtk.StringList.new(['One weighted roll-up', 'One icon per selected account'])});
        appearance.add(style);
        appearance.add(mode);
        generalPage.add(appearance);
        const inclusion = new Adw.PreferencesGroup({title: 'Accounts included in indicators'});
        generalPage.add(inclusion);
        const previewGroup = new Adw.PreferencesGroup({title: 'Live draft preview'});
        const previewBox = new Gtk.Box({orientation: Gtk.Orientation.VERTICAL, spacing: 8});
        previewGroup.add(previewBox);
        generalPage.add(previewGroup);
        const actionsGroup = new Adw.PreferencesGroup({title: 'Settings and support'});
        const generalStatus = new Adw.ActionRow({title: 'Ready', use_markup: false});
        actionsGroup.add(generalStatus);
        const generalActions = actionButtons();
        actionsGroup.add(generalActions);
        generalPage.add(actionsGroup);

        const signIn = new Adw.PreferencesGroup({
            title: 'Connect a GitHub account',
            description: 'Credentials stay in your Secret Service keyring (GNOME Keyring or KWallet). A running, unlocked keyring is required.',
        });
        const host = new Adw.EntryRow({title: 'HTTPS host', text: 'github.com'});
        const clientId = new Adw.EntryRow({title: 'Enterprise OAuth client ID (if required)'});
        const offline = new Adw.SwitchRow({title: 'Request offline access'});
        const status = new Adw.ActionRow({title: 'Ready', use_markup: false});
        const failure = new Adw.ActionRow({title: 'Action failed', use_markup: false, visible: false});
        const code = new Adw.ActionRow({title: '', title_selectable: true, use_markup: false, visible: false});
        const browser = new Gtk.Button({label: 'Open verification page', sensitive: false,
            valign: Gtk.Align.CENTER, css_classes: ['suggested-action']});
        const connect = new Gtk.Button({label: 'Sign in', valign: Gtk.Align.CENTER, css_classes: ['suggested-action']});
        const cancel = new Gtk.Button({label: 'Cancel sign-in', sensitive: false, valign: Gtk.Align.CENTER});
        const copy = new Gtk.Button({label: 'Copy code', sensitive: false, valign: Gtk.Align.CENTER});
        const buttons = actionButtons();
        for (const button of [connect, browser, copy, cancel]) buttons.insert(button, -1);
        for (const row of [host, clientId, offline, status, failure, code, buttons]) signIn.add(row);
        page.add(signIn);
        const accounts = new Adw.PreferencesGroup({title: 'Accounts'});
        page.add(accounts);
        const demoGroup = new Adw.PreferencesGroup({title: 'Synthetic demonstration', visible: false});
        const addDemo = new Gtk.Button({label: 'Add demo account'});
        demoGroup.add(addDemo);
        page.add(demoGroup);
        window.add(page);
        window.add(generalPage);
        let accountRows = [];
        let accountSignature = '';
        let prompt = null;
        let busy = false;
        let active = false;
        let closed = false;
        let polling = false;
        let draftLoaded = false;
        let snapshot = null;
        let excluded = [];
        let inclusionRows = [];
        let inclusionSignature = '';
        let previewTimer = 0;
        let previewGeneration = 0;
        const updateButtons = () => {
            connect.visible = !active;
            cancel.visible = active;
            browser.visible = prompt !== null;
            copy.visible = prompt !== null;
            for (const button of [connect, browser, copy, cancel]) button.get_parent().visible = button.visible;
            connect.sensitive = !busy && !active;
            cancel.sensitive = active && !busy;
            browser.sensitive = prompt !== null && new Date(prompt.expires).getTime() > Date.now();
            copy.sensitive = browser.sensitive;
            for (const entry of [host, clientId, offline]) entry.sensitive = !busy && !active;
            accounts.sensitive = !busy && !active;
        };
        updateButtons();
        const error = message => {
            busy = false;
            polling = false;
            failure.visible = true;
            failure.subtitle = message;
            generalStatus.title = 'Action failed';
            generalStatus.subtitle = message;
            updateButtons();
        };
        const execute = request => {
            busy = true;
            failure.visible = false;
            updateButtons();
            client.call('Execute', new GLib.Variant('(s)', [JSON.stringify(request)]), () => {
                busy = false;
                status.title = 'Request accepted';
                status.subtitle = '';
                generalStatus.title = 'Request completed';
                generalStatus.subtitle = '';
                updateButtons();
                client.refresh();
                poll();
            });
        };
        const open = uri => Gio.AppInfo.launch_default_for_uri_async(uri, null, null, (_source, result) => {
            if (closed) return;
            try { Gio.AppInfo.launch_default_for_uri_finish(result); }
            catch { error('The desktop could not open this location.'); }
        });
        const draft = () => {
            const poll = Number(minutes.text);
            const spend = increment.text.trim() === '' ? null : Number(increment.text);
            if (!Number.isInteger(poll) || poll < 5 || poll > 1440 ||
                (spend !== null && (!Number.isFinite(spend) || spend < 0)))
                throw new Error('Refresh must be 5 to 1440 whole minutes. USD increments must be nonnegative.');
            return {pollMinutes: poll, notifications: notifications.active, thresholds: thresholds.text,
                spendIncrementUsd: spend, startup: startup.active, trayStyle: style.selected === 0 ? 'Pie' : 'Percentage',
                trayMode: mode.selected === 0 ? 'RollUp' : 'PerAccount', excludedTrayAccounts: excluded};
        };
        const preview = () => {
            if (!draftLoaded || closed) return;
            if (previewTimer) GLib.source_remove(previewTimer);
            const generation = ++previewGeneration;
            previewTimer = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 300, () => {
                previewTimer = 0;
                try {
                    client.call('Preview', new GLib.Variant('(s)', [JSON.stringify({kind: 'preview', settings: draft()})]), reply => {
                        if (generation !== previewGeneration) return;
                        try {
                            const value = parsePreview(reply[0]);
                            while (previewBox.get_first_child()) previewBox.remove(previewBox.get_first_child());
                            for (const icon of value.icons) {
                                const row = new Gtk.Box({spacing: 12});
                                row.append(new Gtk.Image({gicon: new Gio.BytesIcon({bytes: new GLib.Bytes(
                                    GLib.base64_decode(icon.imageUri.split(',')[1]))}), pixel_size: 32}));
                                row.append(new Gtk.Label({label: icon.tooltip, wrap: true, xalign: 0, selectable: true}));
                                previewBox.append(row);
                            }
                        } catch { error('Invalid tray preview from the helper.'); }
                    });
                } catch (ex) { error(ex.message); }
                return GLib.SOURCE_REMOVE;
            });
        };
        const renderGeneral = value => {
            snapshot = value;
            if (!draftLoaded) {
                const s = snapshot.settings;
                minutes.text = String(s.pollMinutes);
                thresholds.text = s.thresholds;
                increment.text = s.spendIncrementUsd === null ? '' : String(s.spendIncrementUsd);
                notifications.active = s.notifications;
                startup.active = s.startup;
                style.selected = s.trayStyle === 'Pie' ? 0 : 1;
                mode.selected = s.trayMode === 'RollUp' ? 0 : 1;
                excluded = (s.excludedTrayAccounts || []).slice();
                draftLoaded = true;
                inclusionSignature = '';
                preview();
            }
            startup.sensitive = snapshot.settings.canChangeStartup;
            startup.subtitle = snapshot.settings.startupDescription;
            const signature = JSON.stringify(snapshot.accounts.map(a => [a.key, a.name]));
            if (signature !== inclusionSignature) {
                inclusionSignature = signature;
                for (const row of inclusionRows) inclusion.remove(row);
                inclusionRows = snapshot.accounts.map(account => {
                    const row = new Adw.SwitchRow({title: account.name, subtitle: account.host, use_markup: false,
                        active: !excluded.includes(account.key)});
                    row.connect('notify::active', () => {
                        excluded = excluded.filter(key => key !== account.key);
                        if (!row.active) excluded.push(account.key);
                        preview();
                    });
                    inclusion.add(row);
                    return row;
                });
                preview();
            }
        };
        for (const widget of [style, mode]) widget.connect('notify::selected', preview);
        minutes.connect('changed', preview);
        const action = (label, callback) => {
            const button = new Gtk.Button({label});
            button.connect('clicked', callback);
            generalActions.insert(button, -1);
        };
        action('Save settings', () => {
            try { execute({kind: 'settings', settings: draft()}); }
            catch (ex) { error(ex.message); }
        });
        action('Discard draft', () => { if (snapshot) { draftLoaded = false; renderGeneral(snapshot); } });
        action('Send test notification', () => execute({kind: 'testNotification'}));
        action('Open data folder', () => { if (snapshot?.dataUri) open(snapshot.dataUri); });
        action('Login startup folder', () => { if (snapshot?.startupUri) open(snapshot.startupUri); });
        action('Project and releases', () => open('https://github.com/DamianEdwards/ghcp-spend-tray/releases'));
        action('Privacy policy', () => open('https://github.com/DamianEdwards/ghcp-spend-tray/blob/main/PRIVACY.md'));
        action('Report an issue', () => open('https://github.com/DamianEdwards/ghcp-spend-tray/issues'));
        action('Quit monitoring', () => client.call('Quit'));
        action('Start / refresh monitoring', () => client.refresh());
        actionsGroup.description = 'Independent Copilot AI-credit monitoring, not an invoice. Locally sampled history is not a verified daily breakdown. Linux development build; verify the distribution signature before installing an update.';
        const renderAccounts = snapshot => {
            const signature = JSON.stringify(snapshot.accounts.map(a => [
                a.key, a.name, a.displayName, a.thresholds, a.spendIncrementUsd, a.clientId, a.showPeriodEstimate,
            ]));
            if (signature === accountSignature) return;
            accountSignature = signature;
            for (const row of accountRows) accounts.remove(row);
            accountRows = [];
            for (const account of snapshot.accounts) {
                const row = new Adw.ExpanderRow({title: account.name, subtitle: `${account.login} @ ${account.host}`, use_markup: false,
                    expanded: this.getSettings().get_string('selected-account') === account.key});
                const name = new Adw.EntryRow({title: 'Display name', text: account.displayName});
                const thresholds = new Adw.EntryRow({title: 'Alert percentages (blank inherits)', text: account.thresholds});
                const spend = new Adw.EntryRow({title: 'USD alert increment (blank inherits; 0 disables)',
                    text: account.spendIncrementUsd === null ? '' : String(account.spendIncrementUsd)});
                const estimate = new Adw.SwitchRow({title: 'Show estimated period consumption',
                    subtitle: 'Average pace this UTC month; assumes the same pace continues, not an invoice.',
                    active: account.showPeriodEstimate});
                for (const entry of [name, thresholds, spend, estimate]) row.add_row(entry);
                const actions = actionButtons();
                const addAction = (label, callback) => {
                    const button = new Gtk.Button({label, valign: Gtk.Align.CENTER});
                    button.connect('clicked', callback);
                    actions.insert(button, -1);
                };
                addAction('Save', () => {
                    const amount = spend.text.trim() === '' ? null : Number(spend.text);
                    if (amount !== null && (!Number.isFinite(amount) || amount < 0)) {
                        error('Enter a nonnegative USD amount or leave it blank.');
                        return;
                    }
                    execute({kind: 'save', key: account.key, displayName: name.text,
                        thresholds: thresholds.text, spendIncrementUsd: amount, showPeriodEstimate: estimate.active});
                });
                addAction('Refresh', () => execute({kind: 'refresh', key: account.key}));
                addAction('Reconnect', () => execute({kind: 'signin', key: account.key,
                    clientId: account.clientId || clientId.text || null, offlineAccess: offline.active}));
                row.add_row(actions);
                const grants = new Adw.ActionRow({title: 'Manage OAuth grants', activatable: true,
                    subtitle: 'Local removal does not revoke the grant. History and recovery copies may remain.'});
                grants.add_suffix(new Gtk.Image({icon_name: 'adw-external-link-symbolic'}));
                grants.connect('activated', () => open(`https://${account.host}/settings/applications`));
                row.add_row(grants);
                const confirmation = new Adw.SwitchRow({title: 'Confirm removal of this account and its saved credentials'});
                row.add_row(confirmation);
                const removal = new Adw.ActionRow({title: 'Remove from monitoring'});
                const remove = new Gtk.Button({icon_name: 'user-trash-symbolic', tooltip_text: 'Remove account',
                    sensitive: false, valign: Gtk.Align.CENTER, css_classes: ['destructive-action']});
                confirmation.connect('notify::active', () => { remove.sensitive = confirmation.active; });
                remove.connect('clicked', () => execute({kind: 'remove', key: account.key}));
                removal.add_suffix(remove);
                row.add_row(removal);
                accounts.add(row);
                accountRows.push(row);
            }
        };
        const client = new DemoClient(snapshot => {
            renderGeneral(snapshot);
            renderAccounts(snapshot);
            demoGroup.visible = snapshot.demo;
            addDemo.sensitive = snapshot.accounts.length < 100;
            signIn.sensitive = !snapshot.demo;
            if (snapshot.demo) {
                status.title = 'Explicit synthetic demo mode';
            }
        }, error);
        addDemo.connect('clicked', () => client.call('AddDemoAccount'));
        const poll = () => {
            if (closed || polling || !client.running) return;
            polling = true;
            client.call('GetSignIn', null, reply => {
                polling = false;
                try {
                    const state = parseSignIn(reply[0]);
                    active = ['starting', 'waiting', 'saving'].includes(state.phase);
                    prompt = state.phase === 'waiting' ? state : null;
                    code.visible = prompt !== null;
                    code.title = prompt?.code || '';
                    code.subtitle = prompt ? `Expires ${new Date(prompt.expires).toLocaleTimeString()}` : '';
                    if (state.phase !== 'idle') {
                        status.title = state.phase === 'waiting' ? 'Enter this code on GitHub' : state.phase;
                        status.subtitle = state.message || prompt?.verificationUri || '';
                    }
                    updateButtons();
                } catch {
                    error('Invalid sign-in response from the helper.');
                }
            });
        };
        connect.connect('clicked', () => execute({kind: 'signin', host: host.text,
            clientId: clientId.text || null, offlineAccess: offline.active}));
        cancel.connect('clicked', () => execute({kind: 'cancel'}));
        copy.connect('clicked', () => { if (prompt) Gdk.Display.get_default().get_clipboard().set(prompt.code); });
        browser.connect('clicked', () => {
            if (!prompt || new Date(prompt.expires).getTime() <= Date.now()) return;
            Gio.AppInfo.launch_default_for_uri_async(prompt.verificationUri, null, null, (_source, result) => {
                if (closed) return;
                try { Gio.AppInfo.launch_default_for_uri_finish(result); }
                catch { error('Could not open a browser. Open the displayed HTTPS verification address manually.'); }
            });
        });
        const timer = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 1, () => {
            poll();
            return GLib.SOURCE_CONTINUE;
        });
        window.connect('close-request', () => {
            closed = true;
            GLib.source_remove(timer);
            if (previewTimer) GLib.source_remove(previewTimer);
            client.destroy();
            return false;
        });
    }
}
