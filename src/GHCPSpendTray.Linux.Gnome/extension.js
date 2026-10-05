import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import St from 'gi://St';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import {DemoClient} from './client.js';
import {formatPercent, updatedText} from './snapshot.js';

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
        this._settings = this.getSettings();
        this._button = new PanelMenu.Button(0.5, 'GHCPSpendTray demo');
        const indicator = new St.BoxLayout();
        this._pie = new St.DrawingArea({width: 22, height: 22, y_align: Clutter.ActorAlign.CENTER});
        this._pie.connect('repaint', area => this._paintPie(area));
        this._number = label('?', 'ghcp-demo-indicator');
        indicator.add_child(this._pie);
        indicator.add_child(this._number);
        this._button.add_child(indicator);
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
        content.add_child(footer);
        content.add_child(label('AI-credit consumption value, not an invoice.', 'ghcp-demo-caption'));
        content.add_child(label('DEMO - synthetic accounts only', 'ghcp-demo-caption'));
        this._button.menu.box.add_child(content);
        Main.panel.addToStatusArea(this.uuid, this._button);
        this._client = new DemoClient(snapshot => this._render(snapshot), message => this._showError(message),
            this._settings.get_string('indicator-style'));
        this._settingsChanged = this._settings.connect('changed::indicator-style', () =>
            this._client.setStyle(this._settings.get_string('indicator-style')));
        this._updateIndicator();
    }

    _paintPie(area) {
        const context = area.get_context();
        const [width, height] = area.get_surface_size();
        const radius = Math.min(width, height) / 2 - 2;
        const color = area.get_theme_node().get_foreground_color();
        context.setSourceRGBA(color.red / 255, color.green / 255, color.blue / 255, 1);
        context.setLineWidth(1.5);
        context.arc(width / 2, height / 2, radius, 0, Math.PI * 2);
        context.stroke();
        const fraction = Math.min(1, Math.max(0, (this._snapshot?.percent ?? 0) / 100));
        if (fraction > 0) {
            context.moveTo(width / 2, height / 2);
            context.arc(width / 2, height / 2, radius - 2, -Math.PI / 2, fraction * Math.PI * 2 - Math.PI / 2);
            context.closePath();
            context.fill();
        }
        context.$dispose();
    }

    _updateIndicator() {
        const snapshot = this._snapshot;
        const pie = snapshot !== null && snapshot.percent !== null &&
            this._settings.get_string('indicator-style') === 'Pie';
        this._pie.visible = pie;
        this._number.visible = !pie;
        this._number.text = snapshot?.indicator ?? '?';
        this._button.accessible_name = `GHCPSpendTray demo: ${snapshot?.consumption ?? 'Unavailable'}, ${formatPercent(snapshot?.percent ?? null)}`;
        this._pie.queue_repaint();
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
        this._errorLabel.visible = false;
        this._total.text = snapshot.consumption;
        this._count.text = `${snapshot.accounts.length} connected account(s) - ${formatPercent(snapshot.percent)}`;
        this._updated.text = updatedText(snapshot.updatedAt);
        this._accounts.destroy_all_children();
        if (snapshot.accounts.length === 0) {
            this._accounts.add_child(label('No accounts yet.', 'ghcp-demo-caption'));
            this._accounts.add_child(action('Add demo account', () => this._client.call('AddDemoAccount')));
        }
        for (const account of snapshot.accounts)
            this._accounts.add_child(this._accountCard(account));
        this._updateIndicator();
    }

    _accountCard(account) {
        const card = column('ghcp-demo-card');
        const heading = new St.BoxLayout({style_class: 'ghcp-demo-header'});
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
        const status = new St.BoxLayout();
        const freshness = label(account.percent > 100 ? `${account.freshness} - Over allocation` : account.freshness,
            account.percent > 100 ? 'ghcp-demo-warning' : 'ghcp-demo-caption');
        freshness.x_expand = true;
        status.add_child(freshness);
        const details = column('ghcp-demo-details');
        details.add_child(label(`${account.login} @ ${account.host}`, 'ghcp-demo-caption'));
        details.add_child(label(account.updatedAt ? `Updated ${new Date(account.updatedAt).toLocaleString()}` :
            'No observations yet', 'ghcp-demo-caption'));
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
        if (this._settingsChanged)
            this._settings.disconnect(this._settingsChanged);
        this._settingsChanged = null;
        this._settings = null;
        this._button?.destroy();
        this._button = null;
        this._snapshot = null;
        this._expanded = null;
        this._pie = null;
        this._number = null;
        this._total = null;
        this._count = null;
        this._updated = null;
        this._accounts = null;
        this._errorLabel = null;
    }
}
