using System.Text.Json;
using GHCPSpendTray.Core;
using GHCPSpendTray.Shared;

namespace GHCPSpendTray.Linux;

public sealed class LinuxSession : IDisposable
{
    private readonly IApplicationController _controller;
    private readonly bool _demo;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _mutations = new(1, 1);
    private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _signInCancellation;
    private Task _signInTask = Task.CompletedTask;
    private Task? _initialization;
    private SignInView _signIn = new("idle");
    private DashboardView _dashboard = new("", "", "", []);
    private Func<NotificationView, Task<bool>>? _notify;
    public SettingsView Settings => _controller.Settings;
    public Task<int> Completion => _completion.Task;
    public DemoSnapshot Snapshot { get; private set; } = DemoSnapshot.Empty;
    public event Action? Changed;

    public LinuxSession(IApplicationController controller, bool demo = false)
    {
        _controller = controller;
        _demo = demo;
        Snapshot = DemoSnapshot.Empty with { Demo = demo };
        _controller.Changed += OnChanged;
    }

    internal static LinuxSession CreateReal(bool statusNotifier = false)
    {
        string root = Environment.GetEnvironmentVariable("XDG_STATE_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        if (!Path.IsPathFullyQualified(root))
            throw new PlatformOperationException("XDG_STATE_HOME must be an absolute path.");
        string directory = Path.Combine(root, "ghcp-spend-tray");
        if (new DirectoryInfo(directory).LinkTarget is not null)
            throw new PlatformOperationException("The account data directory must not be a symbolic link.");
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        if (!Path.IsPathFullyQualified(config)) throw new PlatformOperationException("XDG_CONFIG_HOME must be an absolute path.");
        string? executable = Environment.ProcessPath;
        if (Path.GetFileName(executable) != "GHCPSpendTray.Linux") executable = null;
        return new(new ApplicationController(directory, false, new SecretServiceCredentials(),
            createStartup: () => Task.FromResult<IStartupRegistration>(new XdgStartupRegistration(config, executable, statusNotifier)),
            recordDiagnostic: message => Console.Error.WriteLine(message)));
    }

    public Task StartAsync()
    {
        lock (_gate) return _initialization ??= _controller.InitializeAsync();
    }
    internal void SetNotificationHandler(Func<NotificationView, Task<bool>> handler)
    {
        _notify = handler;
        _controller.SetNotificationHandler(handler);
    }
    private void OnChanged(DashboardView dashboard)
    {
        lock (_gate)
        {
            _dashboard = dashboard;
            var snapshot = DemoSnapshot.Create(dashboard, Settings.TrayStyle, Snapshot.Revision + 1);
            Snapshot = snapshot with
            {
                Demo = _demo,
                Settings = Settings,
                DataUri = _demo ? null : new Uri(_controller.DataDirectory + Path.DirectorySeparatorChar).AbsoluteUri,
                StartupUri = _demo ? null : new Uri(Path.Combine(
                    Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ??
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
                    "autostart") + Path.DirectorySeparatorChar).AbsoluteUri,
                Accounts = snapshot.Accounts.Select(account =>
                {
                    var settings = _controller.AccountSettings(account.Key);
                    return account with { DisplayName = settings.DisplayName, Thresholds = settings.Thresholds,
                        SpendIncrementUsd = settings.SpendIncrementUsd, ShowPeriodEstimate = settings.ShowPeriodEstimate,
                        ClientId = _controller.AccountClientId(account.Key) };
                }).ToArray()
            };
        }
        Changed?.Invoke();
    }

    public Task RefreshAsync() => _controller.RefreshAsync();
    public Task SetStyleAsync(TrayIconStyle style) => MutateAsync(() => _controller.SaveSettingsAsync(Settings with { TrayStyle = style }));
    public Task AddExampleAsync() => MutateAsync(async () =>
    {
        if (_controller is not DemoController demo)
            throw new AppOperationException("Synthetic accounts are available only in explicit demo mode.");
        if (Snapshot.Accounts.Length >= 100)
            throw new AppOperationException("At most 100 accounts can be monitored.");
        await demo.AddExampleAccountAsync();
    });

    internal string SignInJson()
    {
        lock (_gate) return JsonSerializer.Serialize(_signIn, DemoJsonContext.Default.SignInView);
    }

    internal Task ExecuteAsync(string json)
    {
        if (json.Length > 16384) throw new ArgumentException("Account request is too large.");
        var request = JsonSerializer.Deserialize(json, DemoJsonContext.Default.AccountRequest)
            ?? throw new ArgumentException("An account request is required.");
        return request.Kind switch
        {
            "signin" => BeginSignIn(request),
            "cancel" => CancelSignIn(),
            "remove" => MutateAsync(() => _controller.RemoveAsync(RequireKey(request))),
            "refresh" => MutateAsync(() => _controller.RefreshAccountAsync(RequireKey(request))),
            "save" => MutateAsync(() => _controller.SaveAccountAsync(RequireKey(request),
                request.DisplayName ?? "", request.Thresholds ?? "", request.SpendIncrementUsd, request.ShowPeriodEstimate)),
            "settings" => MutateAsync(() => _controller.SaveSettingsAsync(request.Settings ??
                throw new ArgumentException("Settings are required."))),
            "testNotification" => TestNotificationAsync(),
            "resume" => _controller.ResumeAsync(),
            _ => throw new ArgumentException("Unknown account action.")
        };
    }

    internal string PreviewJson(string json)
    {
        if (json.Length > 16384) throw new ArgumentException("Preview request is too large.");
        var request = JsonSerializer.Deserialize(json, DemoJsonContext.Default.AccountRequest);
        var settings = request?.Settings ?? throw new ArgumentException("Settings are required.");
        lock (_gate)
        {
            var state = _dashboard.TrayStates ?? [];
            var draft = new AppSettings
            {
                PollIntervalMinutes = settings.PollMinutes,
                TrayStyle = settings.TrayStyle, TrayMode = settings.TrayMode,
                Accounts = state.Select(s => s.Account with
                    { ExcludeFromTray = settings.ExcludedTrayAccounts?.Contains(s.Account.Key, StringComparer.Ordinal) == true }).ToArray()
            };
            draft.Validate();
            return JsonSerializer.Serialize(TrayPreview.Create(TrayUsage.Create(draft, state, DateTimeOffset.UtcNow)),
                DemoJsonContext.Default.TrayPreview);
        }
    }

    private async Task TestNotificationAsync()
    {
        if (_demo) throw new AppOperationException("Desktop notifications are disabled in demonstration mode.");
        if (_notify is null || !await _notify(new(null, "GHCPSpendTray test notification",
            "Notifications are connected. Desktop settings and Do Not Disturb can suppress delivery.")))
            throw new AppOperationException("The notification service did not accept the test. Check your desktop notification settings.");
    }

    private static string RequireKey(AccountRequest request) => !string.IsNullOrWhiteSpace(request.Key)
        ? request.Key : throw new ArgumentException("Select an account.");

    private Task BeginSignIn(AccountRequest request)
    {
        if (_demo) throw new AppOperationException("Real sign-in is unavailable in explicit demo mode.");
        lock (_gate)
        {
            if (!_signInTask.IsCompleted) throw new AppOperationException("Sign-in is already in progress. Finish or cancel it first.");
            if (request.Key is null && Snapshot.Accounts.Length >= 100)
                throw new AppOperationException("At most 100 accounts can be monitored.");
            string host = request.Host ?? "github.com";
            string? clientId = request.ClientId;
            if (request.Key is not null)
            {
                var account = Snapshot.Accounts.SingleOrDefault(a => a.Key == request.Key)
                    ?? throw new AppOperationException("That account is no longer configured.");
                host = account.Host;
                clientId = _controller.AccountClientId(request.Key) ?? clientId;
            }
            _ = _controller.ResolveHostDescription(host);
            _ = GitHubOAuth.ResolveClientId(host, clientId);
            _signInCancellation?.Dispose();
            _signInCancellation = new();
            _signIn = new("starting");
            _signInTask = Task.Run(() => SignInAsync(host, clientId, request, _signInCancellation.Token));
        }
        return Task.CompletedTask;
    }

    private async Task SignInAsync(string host, string? clientId, AccountRequest request, CancellationToken token)
    {
        try
        {
            await _controller.AddAsync(host, request.OfflineAccess, request.Key,
                prompt => SetSignIn(new("waiting", prompt.Code, prompt.VerificationUri.AbsoluteUri, prompt.Expires)),
                () => SetSignIn(new("saving")), token, clientId);
            SetSignIn(new("complete", Message: "Account connected."));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { SetSignIn(new("cancelled", Message: "Sign-in cancelled. Refresh the overview to see any account already saved.")); }
        catch (AppOperationException ex) { SetSignIn(new("error", Message: ex.Message)); }
        catch (Exception ex)
        {
            ReportError($"Sign-in failed ({ex.GetType().Name}).");
            SetSignIn(new("error", Message: "Sign-in failed. Check the helper diagnostic log."));
        }
    }

    private void SetSignIn(SignInView state) { lock (_gate) _signIn = state; }
    private Task CancelSignIn()
    {
        lock (_gate) _signInCancellation?.Cancel();
        return Task.CompletedTask;
    }
    private async Task MutateAsync(Func<Task> action)
    {
        await _mutations.WaitAsync();
        try { await action(); }
        finally { _mutations.Release(); }
    }
    public void Quit() => _completion.TrySetResult(0);
    public void ReportError(string message) => Console.Error.WriteLine(message);
    public void Dispose()
    {
        _signInCancellation?.Cancel();
        _signInTask.GetAwaiter().GetResult();
        _signInCancellation?.Dispose();
        _controller.Changed -= OnChanged;
        _controller.Dispose();
    }
}
