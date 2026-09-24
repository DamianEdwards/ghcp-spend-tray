using GHSpend.App.Native;
using GHSpend.App.Platform;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.System;

namespace GHSpend.App.UI;

internal sealed class ReactorShell : IDisposable
{
    private readonly TrayHost _tray;
    private readonly AppSession _session;
    private ReactorWindow? _flyout, _settings;
    private bool _exiting;
    private string? _notificationAccount;
    internal AppSession Session => _session;
    internal ReactorWindow? Flyout => _flyout;
    internal ReactorWindow? SettingsWindow => _settings;

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
        _session.TestNotification = () => _tray.Icon.Notify("GHSpend test", "Windows accepted this test notification request.");
        _tray.OpenRequested += ToggleFlyout;
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
    private void ToggleFlyout()
    {
        if (_flyout?.IsVisible == true) _flyout.Hide();
        else ShowFlyout();
    }
    internal void ShowFlyout()
    {
        if (_exiting) return;
        if (_flyout is null)
        {
            _flyout = ReactorApp.OpenWindow(new WindowSpec
            {
                Title = "GHSpend", Width = 400, Height = 590, Style = WindowStyle.None,
                ResizeMode = WindowResizeMode.NoResize, ShowInTaskbar = false, ShowInSwitcher = false,
                CornerStyle = WindowCornerStyle.Rounded, ActivateOnOpen = false,
                Backdrop = BackdropChoice.Of(BackdropKind.DesktopAcrylic),
                Icon = WindowIcon.FromPath(Path.Combine(AppContext.BaseDirectory, "Assets", "ghspend.ico"))
            }, () => new FlyoutComponent(_session));
            _flyout.Deactivated += (_, _) => _flyout?.Hide();
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
    }
    internal void ShowSettings(SettingsPage page)
    {
        _flyout?.Hide();
        if (_session.Page != page) _session.Navigate(page);
        if (_settings is null)
        {
            _settings = ReactorApp.OpenWindow(new WindowSpec
            {
                Title = "GHSpend Settings", Width = 980, Height = 740, MinWidth = 640, MinHeight = 540,
                StartPosition = WindowStartPosition.CenterOnCurrent,
                Backdrop = BackdropChoice.Of(BackdropKind.Mica),
                CornerStyle = WindowCornerStyle.Rounded,
                Icon = WindowIcon.FromPath(Path.Combine(AppContext.BaseDirectory, "Assets", "ghspend.ico"))
            }, () => new SettingsComponent(_session));
            _settings.Closed += (_, _) => { _settings = null; _session.CloseSettings(); };
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
