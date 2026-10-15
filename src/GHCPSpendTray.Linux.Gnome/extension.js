import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import St from 'gi://St';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import {DemoClient} from './client.js';
import {formatPercent, updatedText, diagnosticsText, estimateText} from './snapshot.js';

function column(styleClass = '') {
    return new St.BoxLayout({orientation: Clutter.Orientation.VERTICAL, style_class: styleClass});
}

function label(text, styleClass = '') {
    return new St.Label({text, style_class: styleClass, y_align: Clutter.ActorAlign.CENTER});
}

function action(text, callback, styleClass = 'ghcp-demo-button') {
    const button = new St.Button({label: text, style_class: styleClass, can_focus: true, accessible_name: text});
    button.connect('clicked', callback);
    return button;
}

export default class GHCPSpendTrayExtension extends Extension {
    enable() {
        this._expanded = new Set();
        this._snapshot = null;
        this._selectedKey = '';
        this._button = new PanelMenu.Button(0.5, 'GHCPSpendTray');
        this._indicators = new St.BoxLayout();
        this._button.add_child(this._indicators);
        const content = column('ghcp-demo-popup');
        const header = new St.BoxLayout({style_class: 'ghcp-demo-header'});
        header.add_child(new St.Icon({
            gicon: new Gio.FileIcon({file: Gio.File.new_for_path(`${this.path}/icons/logo.svg`)}),
            icon_size: 24,
        }));
        const title = label('GHCPSpendTray', 'ghcp-demo-title');
        title.x_expand = true;
        header.add_child(title);
        const settings = new St.Button({
            child: new St.Icon({icon_name: 'emblem-system-symbolic', icon_size: 20}),
            style_class: 'ghcp-demo-button', can_focus: true, accessible_name: 'Settings',
        });
        settings.connect('clicked', () => {
            this._button.menu.close();
            this.getSettings().set_string('selected-account', '');
            this.openPreferences();
        });
        header.add_child(settings);
        content.add_child(header);
        content.add_child(label("This month's consumption", 'ghcp-demo-caption'));
        this._total = label('Unavailable', 'ghcp-demo-total');
        content.add_child(this._total);
        this._count = label('Connecting to the local helper...', 'ghcp-demo-caption');
        content.add_child(this._count);
        this._errorLabel = label('', 'ghcp-demo-error');
        this._errorLabel.clutter_text.line_wrap = true;
        this._errorLabel.visible = false;
        content.add_child(this._errorLabel);
        this._accounts = column('ghcp-demo-accounts');
        const scroll = new St.ScrollView({
            style_class: 'ghcp-demo-scroll',
            hscrollbar_policy: St.PolicyType.NEVER,
            vscrollbar_policy: St.PolicyType.AUTOMATIC,
        });
        scroll.set_child(this._accounts);
        content.add_child(scroll);
        const footer = new St.BoxLayout({style_class: 'ghcp-demo-footer'});
        this._updated = label('No observations yet', 'ghcp-demo-caption');
        this._updated.x_expand = true;
        footer.add_child(this._updated);
        footer.add_child(action('Refresh', () => this._client.call('Refresh', null, () => this._client.refresh())));
        footer.add_child(action('Quit', () => this._client.call('Quit')));
        content.add_child(footer);
        content.add_child(label('AI-credit consumption value, not an invoice.', 'ghcp-demo-caption'));
        this._demoLabel = label('DEMO - synthetic accounts only', 'ghcp-demo-caption');
        this._demoLabel.visible = false;
        content.add_child(this._demoLabel);
        this._button.menu.box.add_child(content);
        Main.panel.addToStatusArea(this.uuid, this._button);
        this._client = new DemoClient(snapshot => this._render(snapshot), message => this._showError(message));
        this._updateIndicator();
    }

    _updateIndicator() {
        const snapshot = this._snapshot;
        const signature = JSON.stringify(snapshot?.icons ?? []);
        if (signature === this._indicatorSignature) return;
        this._indicatorSignature = signature;
        this._indicators.destroy_all_children();
        if (!snapshot) this._indicators.add_child(label('?', 'ghcp-demo-indicator'));
        for (const icon of snapshot?.icons ?? []) {
            const button = new St.Button({can_focus: true, accessible_name: icon.tooltip,
                child: new St.Icon({icon_size: 22, gicon: new Gio.BytesIcon({
                    bytes: new GLib.Bytes(GLib.base64_decode(icon.imageUri.split(',')[1])),
                })})});
            button.connect('button-press-event', () => {
                this._activateIcon(icon);
                return Clutter.EVENT_STOP;
            });
            button.connect('key-press-event', (_actor, event) => {
                if (![Clutter.KEY_Return, Clutter.KEY_space].includes(event.get_key_symbol()))
                    return Clutter.EVENT_PROPAGATE;
                this._activateIcon(icon);
                return Clutter.EVENT_STOP;
            });
            this._indicators.add_child(button);
        }
        this._button.accessible_name = `GHCPSpendTray: ${snapshot?.consumption ?? 'Unavailable'}, ${formatPercent(snapshot?.percent ?? null)}`;
    }

    _activateIcon(icon) {
        const open = !this._button.menu.isOpen || this._selectedKey !== (icon.key ?? '');
        this._selectedKey = icon.key ?? '';
        this._render(this._snapshot);
        if (open) this._button.menu.open();
        else this._button.menu.close();
    }

    _showError(message) {
        this._snapshot = null;
        this._total.text = 'Unavailable';
        this._count.text = 'Helper unavailable';
        this._updated.text = 'No current observations';
        this._accounts.destroy_all_children();
        this._errorLabel.text = message;
        this._errorLabel.visible = true;
        this._updateIndicator();
    }

    _render(snapshot) {
        this._snapshot = snapshot;
        if (this._selectedKey && !snapshot.accounts.some(account => account.key === this._selectedKey))
            this._selectedKey = '';
        this._demoLabel.visible = snapshot.demo;
        this._errorLabel.visible = false;
        this._total.text = snapshot.consumption;
        this._count.text = `${snapshot.accounts.length} connected account(s)` +
            (snapshot.isComplete ? '' : snapshot.isLastKnown ? ' - Last-known / partial total' : ' - Partial total');
        this._updated.text = updatedText(snapshot.updatedAt);
        this._accounts.destroy_all_children();
        const summary = label(snapshot.status + '\n' + snapshot.tray.rollUp.details, 'ghcp-demo-caption');
        summary.clutter_text.line_wrap = true;
        this._accounts.add_child(summary);
        if (this._selectedKey)
            this._accounts.add_child(action('All accounts', () => { this._selectedKey = ''; this._render(this._snapshot); }));
        if (snapshot.accounts.length === 0) {
            this._accounts.add_child(label('No accounts yet.', 'ghcp-demo-caption'));
            this._accounts.add_child(action('Sign in', () => {
                this._button.menu.close();
                this.openPreferences();
            }));
        }
        for (const account of snapshot.accounts.filter(account => !this._selectedKey || account.key === this._selectedKey))
            this._accounts.add_child(this._accountCard(account));
        this._updateIndicator();
    }

    _accountCard(account) {
        const card = column('ghcp-demo-card');
        const heading = new St.BoxLayout({style_class: 'ghcp-demo-header'});
        if (account.avatarUri && Gio.File.new_for_uri(account.avatarUri).query_exists(null))
            heading.add_child(new St.Icon({gicon: new Gio.FileIcon({file: Gio.File.new_for_uri(account.avatarUri)}), icon_size: 32}));
        else
            heading.add_child(label(account.login.slice(0, 1).toUpperCase(), 'ghcp-demo-avatar'));
        const identity = column();
        identity.x_expand = true;
        identity.add_child(label(account.name, 'ghcp-demo-account-name'));
        identity.add_child(label(account.host, 'ghcp-demo-caption'));
        heading.add_child(identity);
        heading.add_child(label(account.consumption, 'ghcp-demo-amount'));
        card.add_child(heading);
        if (account.percent !== null) {
            const track = new St.Widget({style_class: 'ghcp-demo-track', height: 4, x_expand: true});
            const fill = new St.Widget({style_class: 'ghcp-demo-fill', height: 4});
            track.add_child(fill);
            track.connect('notify::width', () => fill.set_width(track.width * Math.min(account.percent / 100, 1)));
            card.add_child(track);
        }
        card.add_child(label(account.percent === null ? 'Allocation percentage not available' :
            `${formatPercent(account.percent)} of ${account.allocation} allocation`, 'ghcp-demo-caption'));
        if (account.periodEstimate) {
            const estimate = label(estimateText(account.periodEstimate), 'ghcp-demo-caption');
            estimate.clutter_text.line_wrap = true;
            card.add_child(estimate);
        }
        const status = new St.BoxLayout();
        const freshness = label(account.percent > 100 ? `${account.freshness} - Over allocation` : account.freshness,
            account.percent > 100 ? 'ghcp-demo-warning' : 'ghcp-demo-caption');
        freshness.x_expand = true;
        status.add_child(freshness);
        const details = column('ghcp-demo-details');
        details.add_child(label(`${account.login} @ ${account.host}`, 'ghcp-demo-caption'));
        details.add_child(label(account.updatedAt ? `Updated ${new Date(account.updatedAt).toLocaleString()}` :
            'No observations yet', 'ghcp-demo-caption'));
        const diagnostic = label(diagnosticsText(account), 'ghcp-demo-caption');
        diagnostic.clutter_text.line_wrap = true;
        details.add_child(diagnostic);
        details.add_child(action('Manage account', () => {
            this._button.menu.close();
            this.getSettings().set_string('selected-account', account.key);
            this.openPreferences();
        }));
        details.visible = this._expanded.has(account.key);
        const toggle = action(details.visible ? 'Hide details' : 'Details', () => {
            details.visible = !details.visible;
            toggle.label = details.visible ? 'Hide details' : 'Details';
            if (details.visible)
                this._expanded.add(account.key);
            else
                this._expanded.delete(account.key);
        });
        status.add_child(toggle);
        card.add_child(status);
        card.add_child(details);
        return card;
    }

    disable() {
        this._client?.destroy();
        this._client = null;
        this._button?.destroy();
        this._button = null;
        this._snapshot = null;
        this._expanded = null;
        this._indicators = null;
        this._indicatorSignature = null;
        this._total = null;
        this._count = null;
        this._updated = null;
        this._accounts = null;
        this._errorLabel = null;
    }
}
