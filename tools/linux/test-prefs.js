import Adw from 'gi://Adw?version=1';
import Gtk from 'gi://Gtk?version=4.0';
import Gdk from 'gi://Gdk?version=4.0';
import GLib from 'gi://GLib';
import System from 'system';
import Preferences from './prefs.js';
import {fixture} from './prefs-fixture.js';

Adw.init();
const [width, fontSize, output] = ARGV;
Gtk.Settings.get_default().gtk_font_name = `Sans ${fontSize}`;
Adw.StyleManager.get_default().color_scheme = Adw.ColorScheme.FORCE_DARK;
const window = new Adw.PreferencesWindow({title: 'GHCPSpendTray'});
new Preferences().fillPreferencesWindow(window);
window.set_default_size(Number(width), 800);

function assert(condition, message) {
    if (!condition) throw new Error(message);
}

function* descendants(widget) {
    yield widget;
    for (let child = widget.get_first_child(); child; child = child.get_next_sibling())
        yield* descendants(child);
}

function find(type, predicate) {
    const widget = [...descendants(window)].find(w => w instanceof type && predicate(w));
    assert(widget, `Missing ${type.name}`);
    return widget;
}

function button(label) {
    return find(Gtk.Button, w => w.label === label);
}

function click(label) {
    const widget = button(label);
    widget.grab_focus();
    widget.emit('clicked');
}

function waitFor(predicate) {
    const deadline = GLib.get_monotonic_time() + 4000000;
    return new Promise((resolve, reject) => {
        GLib.timeout_add(GLib.PRIORITY_DEFAULT, 50, () => {
            if (predicate()) { resolve(); return GLib.SOURCE_REMOVE; }
            if (GLib.get_monotonic_time() > deadline) {
                reject(new Error('Preferences did not reach the expected state.'));
                return GLib.SOURCE_REMOVE;
            }
            return GLib.SOURCE_CONTINUE;
        });
    });
}

async function settle() {
    await new Promise(resolve => GLib.timeout_add(GLib.PRIORITY_DEFAULT, 200, () => {
        resolve();
        return GLib.SOURCE_REMOVE;
    }));
}

function checkLayout() {
    assert(window.get_width() <= Number(width), 'Controls forced the window wider than requested.');
    for (const widget of descendants(window)) {
        if (widget instanceof Adw.ActionRow)
            assert(widget.title !== 'Action failed' || !widget.visible, 'A preferences callback failed.');
        if (!widget.get_mapped() || !(widget instanceof Gtk.Label || widget instanceof Gtk.Button)) continue;
        const [valid, bounds] = widget.compute_bounds(window);
        assert(valid && bounds.origin.x >= -1 && bounds.origin.x + bounds.size.width <= window.get_width() + 1,
            `Horizontal overflow: ${widget.get_name()}`);
        if (widget instanceof Gtk.Label && widget.label.length > 0) {
            const layout = widget.get_layout();
            if (widget.get_ancestor(Gtk.Button)) {
                assert(!layout.is_ellipsized(), `Truncated button: ${widget.label}`);
                assert(layout.get_pixel_size()[0] <= widget.get_width() + 1, `Clipped button: ${widget.label}`);
            }
            if (widget.label.length < 30)
                assert(layout.get_line_count() <= 3, `Label collapsed to a narrow column: ${widget.label}`);
        }
    }
}

async function scrollToBottom() {
    const adjustment = find(Gtk.ScrolledWindow, w => w.get_mapped()).get_vadjustment();
    adjustment.value = adjustment.upper - adjustment.page_size;
    await settle();
}

function screenshot(name) {
    const paintable = new Gtk.WidgetPaintable({widget: window});
    const snapshot = new Gtk.Snapshot();
    paintable.snapshot(snapshot, window.get_width(), window.get_height());
    const texture = window.get_renderer().render_texture(snapshot.to_node(), null);
    assert(texture.save_to_png(`${output}/${name}-${width}-${fontSize}.png`), 'Screenshot was not saved.');
}

async function run() {
    window.present();
    await waitFor(() => window.get_mapped());
    await settle();
    const icons = Gtk.IconTheme.get_for_display(Gdk.Display.get_default());
    for (const name of ['avatar-default-symbolic', 'adw-external-link-symbolic', 'user-trash-symbolic'])
        assert(icons.has_icon(name), `Missing preferences icon: ${name}`);
    assert(button('Sign in').visible && !button('Cancel sign-in').visible, 'Idle actions are incorrect.');
    checkLayout();

    click('Sign in');
    await waitFor(() => [...descendants(window)].some(w => w instanceof Adw.ActionRow && w.title === 'TEST-CODE'));
    await settle();
    const code = find(Adw.ActionRow, w => w.title === 'TEST-CODE');
    const status = find(Adw.ActionRow, w => w.title === 'Enter this code on GitHub');
    assert(code.get_ancestor(Gtk.ListBox) === status.get_ancestor(Gtk.ListBox),
        'Device code must stay inside the sign-in card, not below its actions.');
    assert(code.title_selectable && code.visible, 'Device code must be visible and selectable.');
    assert(!button('Sign in').get_parent().visible && button('Cancel sign-in').visible,
        'Active sign-in must replace the idle action without an empty flow cell.');
    assert(button('Copy code').sensitive && button('Open verification page').sensitive, 'Prompt actions are disabled.');
    checkLayout();
    screenshot('sign-in');

    click('Copy code');
    const clipboard = Gdk.Display.get_default().get_clipboard();
    const copied = await new Promise((resolve, reject) => clipboard.read_text_async(null, (source, result) => {
        try { resolve(source.read_text_finish(result)); } catch (error) { reject(error); }
    }));
    assert(copied === 'TEST-CODE', 'Copy code must put only the synthetic code on the isolated clipboard.');
    fixture.expires = new Date(Date.now() - 1000).toISOString();
    await waitFor(() => !button('Copy code').sensitive && !button('Open verification page').sensitive);
    click('Cancel sign-in');
    await waitFor(() => !code.visible && button('Sign in').visible);
    assert(fixture.requests.some(r => r.kind === 'signin' && r.host === 'github.com') &&
        fixture.requests.some(r => r.kind === 'cancel'), 'Sign-in and cancel commands must remain wired.');

    find(Adw.ExpanderRow, w => w.title === 'Synthetic account').expanded = true;
    await settle();
    checkLayout();
    click('Save');
    assert(fixture.requests.some(r => r.kind === 'save' && r.key === 'synthetic-account'), 'Account editing lost its command.');
    await scrollToBottom();
    screenshot('accounts');

    window.set_visible_page(find(Adw.PreferencesPage, w => w.title === 'General'));
    await settle();
    checkLayout();
    await scrollToBottom();
    screenshot('general');
    print(`PASS: GNOME preferences at ${width}px / ${fontSize}pt: sign-in, copy, expiry, cancellation, account and general controls`);
}

const loop = new GLib.MainLoop(null, false);
let failed = false;
run().catch(error => {
    printerr(`${error}\n${error.stack}`);
    failed = true;
    try { screenshot('failure'); } catch (captureError) { printerr(`Screenshot failed: ${captureError}`); }
}).finally(() => {
    window.close();
    loop.quit();
});
loop.run();
if (failed) System.exit(1);
