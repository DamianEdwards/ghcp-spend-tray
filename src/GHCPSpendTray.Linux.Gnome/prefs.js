import Adw from 'gi://Adw';
import Gtk from 'gi://Gtk';
import {ExtensionPreferences} from 'resource:///org/gnome/Shell/Extensions/js/extensions/prefs.js';
import {DemoClient} from './client.js';

export default class GHCPSpendTrayPreferences extends ExtensionPreferences {
    fillPreferencesWindow(window) {
        window.set_default_size(560, 460);
        const settings = this.getSettings();
        const page = new Adw.PreferencesPage({title: 'GHCPSpendTray', icon_name: 'utilities-system-monitor-symbolic'});
        const appearance = new Adw.PreferencesGroup({title: 'Panel indicator'});
        const style = new Adw.ComboRow({
            title: 'Icon style',
            model: Gtk.StringList.new(['Pie chart', 'Percentage']),
            selected: settings.get_string('indicator-style') === 'Pie' ? 0 : 1,
        });
        style.connect('notify::selected', () =>
            settings.set_string('indicator-style', style.selected === 0 ? 'Pie' : 'Percentage'));
        const changed = settings.connect('changed::indicator-style', () =>
            style.set_selected(settings.get_string('indicator-style') === 'Pie' ? 0 : 1));
        appearance.add(style);
        page.add(appearance);
        const demo = new Adw.PreferencesGroup({
            title: 'Synthetic demonstration',
            description: 'No sign-in, real account data, network requests, or saved accounts. Appearance is saved in GNOME settings.',
        });
        const status = new Adw.ActionRow({title: 'Connecting to the local helper...'});
        demo.add(status);
        const add = new Gtk.Button({label: 'Add demo account', valign: Gtk.Align.CENTER, sensitive: false});
        const addRow = new Adw.ActionRow({title: 'Explore another account'});
        addRow.add_suffix(add);
        addRow.activatable_widget = add;
        demo.add(addRow);
        page.add(demo);
        window.add(page);
        const client = new DemoClient(snapshot => {
            status.title = `${snapshot.accounts.length} demo accounts`;
            status.subtitle = `${snapshot.consumption} consumption`;
            add.sensitive = snapshot.accounts.length < 100;
        }, error => {
            status.title = 'Helper unavailable';
            status.subtitle = error;
            add.sensitive = false;
        });
        add.connect('clicked', () => client.call('AddDemoAccount'));
        window.connect('close-request', () => {
            settings.disconnect(changed);
            client.destroy();
            return false;
        });
    }
}
