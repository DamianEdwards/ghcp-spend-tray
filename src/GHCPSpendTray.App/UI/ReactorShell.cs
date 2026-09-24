using GHCPSpendTray.App.Native;
using GHCPSpendTray.App.Platform;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.System;

namespace GHCPSpendTray.App.UI;

internal sealed class ReactorShell : IDisposable
{
    private readonly TrayHost _tray;
    private readonly AppSession _session;
    private ReactorWindow? _flyout, _settings;
    private bool _exiting;
    private long _flyoutPresentation;
    private string? _notificationAccount;
    internal AppSession Session => _session;
    internal ReactorWindow? Flyout => _flyout;
    internal ReactorWindow? SettingsWindow => _settings;
    internal nint TrayHandle => _tray.Handle;

    internal ReactorShell(IApplicationController controller)
    {
        _tray = new TrayHost(controller.Portable ? controller.DataDirectory : null);
        _session = new AppSession(controller, action =>
        {
            if (ReactorApp.UIDispatcher?.TryEnqueue(() => action()) != true)
                Diagnostics.Record("Reactor UI dispatcher rejected an operation.");
        });
        _session.OpenSettings = ShowSettings;
        _session.HideFlyout = () => _flyout?.Hide();
        _session.TestNotification = () => _tray.Icon.Notify("GHCPSpendTray test", "Windows accepted this test notification request.");
        _tray.OpenRequested += () => _session.Post(ShowFlyout);
        _tray.SettingsRequested += () => ShowSettings(SettingsPage.General);
        _tray.RefreshRequested += () => _session.Refresh();
        _tray.ExitRequested += Exit;
        _tray.ResumeRequested += () => _session.Run(controller.ResumeAsync);
        _tray.NotificationClicked += () =>
        {
            if (_notificationAccount is { } key) _session.EditAccount(key);
            else ShowFlyout();
        };
        _session.Changed += () => _tray.Update(_session.Dashboard.Tooltip);
        controller.SetNotificationHandler((key, title, text) =>
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _session.Post(() =>
            {
                _notificationAccount = key;
                completion.TrySetResult(_tray.Icon.Notify(title, text));
            });
            return completion.Task;
        });
    }
    internal void Start(bool show)
    {
        _session.Initialize();
        if (show) ShowFlyout();
    }
    internal void ShowFlyout()
    {
        if (_exiting) return;
        _flyoutPresentation++;
        if (_flyout is null)
        {
            _flyout = ReactorApp.OpenWindow(new WindowSpec
            {
                Title = "GHCPSpendTray", Width = 400, Height = 590, Style = WindowStyle.None,
                ResizeMode = WindowResizeMode.NoResize, ShowInTaskbar = false, ShowInSwitcher = false,
                CornerStyle = WindowCornerStyle.Rounded, ActivateOnOpen = false,
                Backdrop = BackdropChoice.Of(BackdropKind.DesktopAcrylic),
                Icon = WindowIcon.FromPath(Path.Combine(AppContext.BaseDirectory, "Assets", "GHCPSpendTray.ico"))
            }, () => new FlyoutComponent(_session));
            _flyout.Deactivated += (_, _) => DismissAfterDeactivation();
            _flyout.Closing += (_, e) =>
            {
                if (!_exiting) { e.Cancel = true; _flyout?.Hide(); }
            };
            if (_flyout.NativeWindow.Content is UIElement root)
                root.KeyDown += (_, e) =>
                {
                    if (e.Key == VirtualKey.Escape) { _flyout?.Hide(); e.Handled = true; }
                };
        }
        var (anchor, work, dpi) = _tray.Placement();
        double scale = dpi / 96d;
        int height = _session.Initialized && _session.Dashboard.Accounts.Count == 0 ? 410 : 590;
        var placement = FlyoutPlacement.AboveIcon(anchor, work, (int)(400 * scale), (int)(height * scale), (int)(10 * scale));
        _flyout.AppWindow.MoveAndResize(new RectInt32(placement.X, placement.Y, placement.Width, placement.Height));
        _flyout.Show(); _flyout.Activate();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_flyout.NativeWindow);
        if (Win32.SetForegroundWindow(hwnd) == 0 && Win32.GetForegroundWindow() != hwnd)
            Diagnostics.Record("Windows declined foreground activation of the tray flyout.");
        // Invalidate dismissals raised before or reentrantly during this presentation.
        _flyoutPresentation++;
    }
    private void DismissAfterDeactivation()
    {
        var window = _flyout;
        var presentation = _flyoutPresentation;
        // Showing/activating and Shell focus changes can reenter deactivation. Check the
        // settled native foreground window, not Reactor's cached activation/visibility flags.
        _session.Post(() =>
        {
            if (_exiting || window is null || !ReferenceEquals(window, _flyout) ||
                presentation != _flyoutPresentation) return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window.NativeWindow);
            if (Win32.GetForegroundWindow() != hwnd) window.Hide();
        });
    }
    internal void ShowSettings(SettingsPage page)
    {
        _flyout?.Hide();
        if (_session.Page != page) _session.Navigate(page);
        if (_settings is null)
        {
            _settings = ReactorApp.OpenWindow(new WindowSpec
            {
                Title = "GHCPSpendTray Settings", Width = 980, Height = 740, MinWidth = 640, MinHeight = 540,
                StartPosition = WindowStartPosition.CenterOnCurrent,
                Backdrop = BackdropChoice.Of(BackdropKind.Mica),
                CornerStyle = WindowCornerStyle.Rounded,
                Icon = WindowIcon.FromPath(Path.Combine(AppContext.BaseDirectory, "Assets", "GHCPSpendTray.ico"))
            }, () => new SettingsComponent(_session));
            _settings.Closed += (_, _) => { _settings = null; _session.CloseSettings(); };
            _settings.NativeWindow.Activated += (_, e) =>
            {
                if (e.WindowActivationState != WindowActivationState.Deactivated && !_session.Busy)
                {
                    _session.Startup = _session.Controller.Settings.Startup;
                    _session.Notify();
                }
            };
        }
        _settings.Show(); _settings.Activate();
    }
    internal void Exit()
    {
        _exiting = true;
        _session.CancelSignIn();
        ReactorApp.Exit();
    }
    public void Dispose()
    {
        _exiting = true;
        _session.Dispose();
        _settings?.Dispose(); _flyout?.Dispose(); _tray.Dispose();
    }
}
