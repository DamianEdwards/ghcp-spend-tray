namespace GHSpend.App.Native;

internal sealed class AddAccountWindow : NativeWindow
{
    private readonly IApplicationController _controller;
    private readonly AccountView? _reconnect;
    private readonly nint _hostLabel, _host, _offline, _destination, _instructions,
        _code, _status, _start, _copy, _browser, _cancel;
    private readonly CancellationTokenSource _cancellation = new();
    private DevicePrompt? _prompt;
    private TaskCompletionSource<bool>? _confirmation;
    internal AddAccountWindow(nint owner, IApplicationController controller, AccountView? reconnect) :
        base(reconnect is null ? "Add GHSpend account" : "Reconnect GHSpend account", 680, 505, owner)
    {
        _controller = controller; _reconnect = reconnect;
        DefaultCommand = 10;
        _hostLabel = Label("GitHub &host (HTTPS only):"); _host = Edit(reconnect?.Host ?? "github.com", 1);
        _offline = Control("BUTTON", "Request &offline_access (only if supported/approved by this host)", 4, Win32.WS_TABSTOP | 3);
        _destination = Label("");
        _instructions = Label("Sign-in uses the same OAuth application as the gh CLI. GitHub's consent screen will name GitHub CLI, not GHSpend. " +
            "No client ID or app registration is needed. GHSpend requests read:user, not the CLI's repository scopes. " +
            "Enterprise policy and consumption API availability still apply.");
        _code = Control("EDIT", "", 5, Win32.WS_TABSTOP | 0x800);
        _status = Label("Sign in in the browser, then confirm the verified identity here before saving.");
        _start = Button("&Start device sign-in", 10); _copy = Button("&Copy code", 11);
        _browser = Button("&Open browser", 12); _cancel = Button("Cancel", 13);
        Win32.EnableWindow(_copy, 0); Win32.EnableWindow(_browser, 0);
        if (reconnect is not null) Win32.EnableWindow(_host, 0);
        UpdateDestinations(); Layout();
    }
    protected override void Layout()
    {
        var (w, _) = ClientSize();
        Place(_hostLabel, 16, 12, w - 32, 22); Place(_host, 16, 38, w - 32, 27);
        Place(_offline, 16, 75, w - 32, 26);
        Place(_destination, 16, 109, w - 32, 55);
        Place(_instructions, 16, 172, w - 32, 105);
        Place(_code, 16, 282, 220, 30); Place(_copy, 246, 282, 110, 30); Place(_browser, 366, 282, 130, 30);
        Place(_status, 16, 323, w - 32, 65);
        Place(_start, 16, 398, 190, 30); Place(_cancel, 216, 398, 100, 30);
    }
    private void UpdateDestinations()
    {
        try
        {
            Win32.SetWindowText(_destination, _controller.ResolveHostDescription(Text(_host)));
        }
        catch (AppOperationException ex) { Win32.SetWindowText(_destination, ex.Message); }
    }
    protected override void Command(int id, int notification)
    {
        if (id == 1 && notification == 0x300) UpdateDestinations();
        if (id == 10)
        {
            var host = Text(_host);
            var offline = Win32.SendMessage(_offline, 0xF0, 0, 0) == 1;
            Win32.EnableWindow(_start, 0);
            Win32.EnableWindow(_host, 0); Win32.EnableWindow(_offline, 0);
            Win32.SetWindowText(_status, "Requesting device sign-in...");
            RunOperation(async () =>
            {
                try
                {
                    await _controller.AddAsync(host, offline, _reconnect?.Key, prompt =>
                        Post(() =>
                        {
                            _prompt = prompt;
                            Win32.SetWindowText(_code, prompt.Code);
                            Win32.EnableWindow(_copy, 1); Win32.EnableWindow(_browser, 1);
                            Win32.SetTimer(Handle, 1, 1000, 0);
                            Countdown();
                        }), identity =>
                        {
                            var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            _confirmation = confirmation;
                            Post(() =>
                            {
                                if (_cancellation.IsCancellationRequested) { confirmation.TrySetResult(false); return; }
                                Win32.KillTimer(Handle, 1);
                                Win32.SetWindowText(_status, "Identity verified. Confirm this is the intended account.");
                                confirmation.TrySetResult(Win32.MessageBox(Handle,
                                    $"Save verified account {identity.Login} (ID {identity.UserId}) on {identity.Host}?\n\n" +
                                    "Consumption access has been checked. Choose No if the browser selected the wrong account.",
                                    "Confirm account", Win32.MB_YESNO) == 6);
                            });
                            return confirmation.Task;
                        }, _cancellation.Token).ConfigureAwait(false);
                }
                finally
                {
                    Post(() =>
                    {
                        Win32.KillTimer(Handle, 1);
                        Win32.EnableWindow(_start, 1); Win32.EnableWindow(_offline, 1);
                        if (_reconnect is null) Win32.EnableWindow(_host, 1);
                        Win32.SetWindowText(_status, "Sign-in finished. A failure requires a new device code.");
                    });
                }
            }, Dispose);
        }
        else if (id == 11 && _prompt is not null) ShellServices.CopyText(Handle, _prompt.Code);
        else if (id == 12 && _prompt is not null) ShellServices.Open(_prompt.VerificationUri.AbsoluteUri, Handle);
        else if (id == 13) Dispose();
    }
    private void Countdown()
    {
        if (_prompt is null) return;
        var remaining = _prompt.Expires - DateTimeOffset.UtcNow;
        Win32.SetWindowText(_status, remaining > TimeSpan.Zero
            ? $"Waiting for browser authorization. Code expires in {(int)remaining.TotalMinutes}:{remaining.Seconds:00}. " +
                "Device codes are not stored or logged."
            : "Device code expired. Start again to request a new code.");
        if (remaining <= TimeSpan.Zero) Win32.KillTimer(Handle, 1);
    }
    protected override nint? Message(uint message, nuint wParam, nint lParam)
    {
        if (message == Win32.WM_TIMER) { Countdown(); return 0; }
        return null;
    }
    protected override void Closing() => Dispose();
    protected override void ReleaseResources()
    {
        _cancellation.Cancel();
        _confirmation?.TrySetResult(false);
        Win32.KillTimer(Handle, 1);
    }
}
