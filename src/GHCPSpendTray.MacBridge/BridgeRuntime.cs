using System.Collections.Concurrent;
using System.Globalization;
using GHCPSpendTray.Core;
using GHCPSpendTray.Shared;

namespace GHCPSpendTray.MacBridge;

public sealed class BridgeRuntime : IDisposable
{
    private readonly ConcurrentQueue<BridgeEvent> _events = new();
    private readonly NativePlatform _platform;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateGate = new();
    private IApplicationController? _controller;
    private DashboardView? _dashboard;
    private bool _stateChanged;
    private CancellationTokenSource? _signIn;
    private Task? _work;
    private int _busy;
    private bool _disposed;
    private string? _directory;

    public BridgeRuntime() => _platform = new(_events.Enqueue);

    public Receipt Send(Command command)
    {
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (command.Method == "platform.reply")
            {
                _platform.Complete(Required(command.TargetId), command.Reply ?? throw new ArgumentException("Missing platform reply."));
                return new();
            }
            if (command.Method == "signin.cancel")
            {
                lock (_stateGate) _signIn?.Cancel();
                return new();
            }
            if (command.Method == "tray.preview")
            {
                var settings = command.Settings ?? throw new ArgumentException("Missing preview settings.");
                TrayPresentation tray;
                lock (_stateGate)
                {
                    var states = _dashboard?.TrayStates ?? [];
                    tray = TrayUsage.Create(new()
                    {
                        PollIntervalMinutes = Controller.Settings.PollMinutes,
                        TrayStyle = settings.TrayStyle, TrayMode = settings.TrayMode,
                        Accounts = states.Select(s => s.Account with
                        {
                            ExcludeFromTray = settings.ExcludedTrayAccounts?.Contains(s.Account.Key, StringComparer.Ordinal) == true
                        }).ToArray()
                    }, states, DateTimeOffset.UtcNow);
                }
                _events.Enqueue(new() { Kind = "completed", Id = command.Id, Tray = tray });
                return new();
            }
            if (command.Method == "host.describe")
            {
                string text = Controller.ResolveHostDescription(Required(command.Host));
                _events.Enqueue(new() { Kind = "completed", Id = command.Id, Text = text });
                return new();
            }
            if (command.Method == "account.preferences")
            {
                string key = Required(command.Key);
                var preferences = Controller.AccountSettings(key);
                _events.Enqueue(new()
                {
                    Kind = "completed", Id = command.Id,
                    Preferences = new(preferences.DisplayName, preferences.Thresholds, preferences.SpendIncrementUsd,
                        Controller.AccountClientId(key), preferences.ShowPeriodEstimate,
                        _dashboard?.Accounts.FirstOrDefault(a => a.Key == key)?.CustomBudgetUsd)
                });
                return new();
            }
            if (command.Method == "account.budget.preview")
            {
                UsageBudget.Validate(command.CustomBudgetUsd);
                AccountView account;
                lock (_stateGate)
                    account = _dashboard?.Accounts.FirstOrDefault(a => a.Key == Required(command.Key))
                        ?? throw new AppOperationException("That account is no longer configured.");
                decimal? target = command.CustomBudgetUsd;
                string text = target is null ? "API allocation: " + (account.Details.Unlimited ? "Unlimited" :
                    account.Details.ObservedAllocationUsd is { } allocation
                        ? $"${allocation.ToString("0.00", CultureInfo.InvariantCulture)}" : "Unavailable") :
                    account.Freshness != "Fresh" || account.ConsumptionUsd is not { } consumption
                    ? "Consumption unavailable; no percentage preview." :
                    $"${consumption.ToString("0.00", CultureInfo.InvariantCulture)} consumed = " +
                    $"{UsageBudget.Percentage(consumption, target.Value).ToString("0.##", CultureInfo.InvariantCulture)}% of " +
                    $"${target.Value.ToString("0.00", CultureInfo.InvariantCulture)} custom budget";
                _events.Enqueue(new() { Kind = "completed", Id = command.Id, Text = text });
                return new();
            }
            if (string.IsNullOrEmpty(command.Id)) throw new ArgumentException("Missing command identifier.");
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                throw new AppOperationException("Another operation is still running.");
            if (command.Method == "signin")
                lock (_stateGate) _signIn = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _work = Task.Run(() => ExecuteAsync(command));
            return new();
        }
        catch (Exception ex) { return new(SafeMessage(ex)); }
    }

    private IApplicationController Controller => _controller ?? throw new AppOperationException("The application is not initialized.");

    private async Task ExecuteAsync(Command command)
    {
        string? error = null;
        bool cancelled = false;
        try
        {
            switch (command.Method)
            {
                case "initialize":
                    if (_controller is not null) throw new AppOperationException("The application is already initialized.");
                    _directory = Path.GetFullPath(Required(command.Directory));
                    _controller = command.Demo ? new DemoController(_directory, command.Empty, unlimited: command.Unlimited) :
                        new ApplicationController(_directory, false, _platform,
                            createStartup: _platform.InitializeAsync, recordDiagnostic: RecordDiagnostic);
                    _controller.Changed += OnDashboard;
                    _controller.SetNotificationHandler(_platform.NotifyAsync);
                    await _controller.InitializeAsync();
                    break;
                case "refresh":
                    await Controller.RefreshAsync(command.Key);
                    break;
                case "account.refresh":
                    await Controller.RefreshAccountAsync(Required(command.Key));
                    break;
                case "resume":
                    await Controller.ResumeAsync();
                    if (!Controller.Portable) await _platform.InitializeAsync();
                    break;
                case "settings.save":
                    await Controller.SaveSettingsAsync(command.Settings ?? throw new ArgumentException("Missing settings."));
                    break;
                case "account.save":
                    await Controller.SaveAccountAsync(Required(command.Key), command.DisplayName ?? "",
                        command.Thresholds ?? "", command.SpendIncrementUsd, command.ShowPeriodEstimate,
                        command.CustomBudgetUsd, command.UpdateCustomBudget);
                    break;
                case "account.remove":
                    await Controller.RemoveAsync(Required(command.Key));
                    break;
                case "demo.account.add":
                    if (Controller is not DemoController demo)
                        throw new AppOperationException("Example accounts are only available in demonstration mode.");
                    await demo.AddExampleAccountAsync();
                    break;
                case "signin":
                    CancellationTokenSource signIn;
                    lock (_stateGate) signIn = _signIn!;
                    try
                    {
                        await Controller.AddAsync(Required(command.Host), command.OfflineAccess, command.Key,
                            prompt => _events.Enqueue(new() { Kind = "prompt", Id = command.Id, Prompt = prompt }),
                            () => _events.Enqueue(new() { Kind = "authorized", Id = command.Id }),
                            signIn.Token, command.ClientId);
                    }
                    finally { lock (_stateGate) { _signIn = null; signIn.Dispose(); } }
                    break;
                default:
                    throw new AppOperationException("Unknown application command.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || command.Method == "signin")
        {
            cancelled = true;
        }
        catch (Exception ex) { error = SafeMessage(ex); }
        finally
        {
            lock (_stateGate) _stateChanged = true;
            Interlocked.Exchange(ref _busy, 0);
            _events.Enqueue(new() { Kind = "completed", Id = command.Id, Error = error, Cancelled = cancelled, Settings = _controller?.Settings });
        }
    }

    private void OnDashboard(DashboardView dashboard)
    {
        lock (_stateGate) { _dashboard = dashboard; _stateChanged = true; }
    }

    public BridgeEvent[] Poll()
    {
        var result = new List<BridgeEvent>();
        while (_events.TryDequeue(out var message)) result.Add(message);
        lock (_stateGate)
        {
            if (_stateChanged && _controller is not null)
            {
                _stateChanged = false;
                result.Add(new() { Kind = "state", Dashboard = _dashboard is null ? null : _dashboard with { TrayStates = null }, Settings = _controller.Settings });
            }
        }
        return result.ToArray();
    }

    private void RecordDiagnostic(string category)
    {
        lock (_stateGate)
        {
            try
            {
                string directory = Path.Combine(_directory!, "logs");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "diagnostics.log");
                if (File.Exists(path) && new FileInfo(path).Length > 128 * 1024)
                    File.Move(path, Path.Combine(directory, "diagnostics.previous.log"), true);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {category}\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _events.Enqueue(new() { Kind = "error", Error = "Cannot write the diagnostic log. Check data-folder permissions and free space." });
            }
        }
    }

    private static string Required(string? text) => string.IsNullOrWhiteSpace(text) ?
        throw new ArgumentException("A required value is missing.") : text;

    private string SafeMessage(Exception ex)
    {
        if (_directory is not null) RecordDiagnostic($"Application operation failed ({ex.GetType().Name}).");
        return ex is AppOperationException or PlatformOperationException ? ex.Message :
            "The operation failed. Check the supplied values, connectivity, permissions, and local diagnostic log.";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _platform.Dispose();
        _work?.GetAwaiter().GetResult();
        if (_controller is not null)
        {
            _controller.Changed -= OnDashboard;
            _controller.Dispose();
        }
    }
}
