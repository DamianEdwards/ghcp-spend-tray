using System.Collections.Concurrent;
using GHCPSpendTray.App.Native;
using GHCPSpendTray.App.Platform;
using GHCPSpendTray.App.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Windows.Services.Store;

internal static class StoreUpdateTests
{
    internal static async Task<int> RunAsync()
    {
        int assertions = 0;
        var queue = new ConcurrentQueue<Action>();
        var service = new FakeStore();
        var clock = new UpdateClock();
        using var session = new StoreUpdateSession(service, queue.Enqueue, clock);
        int notices = 0, changes = 0;
        session.Available += () => notices++;
        session.Changed += () => changes++;
        Check(StoreUpdates.Create(false) is null && StoreUpdates.Create(true) is null,
            "unpackaged development runs never use Store APIs");
        Check(UpdateRestart.RemainingDelay(TimeSpan.Zero) == TimeSpan.FromSeconds(61) &&
            UpdateRestart.RemainingDelay(TimeSpan.FromSeconds(60)) == TimeSpan.FromSeconds(1) &&
            UpdateRestart.RemainingDelay(TimeSpan.FromSeconds(61)) == TimeSpan.Zero &&
            UpdateRestart.RemainingDelay(TimeSpan.FromDays(1)) == TimeSpan.Zero,
            "installation respects Restart Manager's 60-second minimum process runtime");
        UpdateRestart.Register();
        UpdateRestart.Unregister();
        Check(true, "source-generated native restart registration and cleanup execute");
        Check(StoreUpdates.FailureMessage(StorePackageUpdateState.ErrorLowBattery).Contains("Charge") &&
            StoreUpdates.FailureMessage(StorePackageUpdateState.ErrorWiFiRequired).Contains("Wi-Fi") &&
            StoreUpdates.FailureMessage(StorePackageUpdateState.OtherError).Contains("Microsoft Store"),
            "Store battery, network and unknown failures have actionable messages");

        service.CheckGate = new();
        session.Start();
        session.Start();
        session.Check(force: true);
        session.Install(123);
        Check(session.Checking && service.Checks == 1 && service.Installs == 0,
            "startup starts one check and prevents overlapping checks/installations");
        service.CheckGate.SetResult(false);
        await Until(() => !session.Checking);
        Check(!session.HasUpdate && session.Status == "You're running the latest version." && notices == 0,
            "successful empty query reports the latest version without notifications");
        Check(!Descendants(UI.StoreUpdateCard(session, 123)).OfType<ButtonElement>().Any(),
            "latest-version About card has no button");
        session.Check();
        Drain();
        Check(service.Checks == 1, "repeated About activation is throttled");

        service.CheckGate = null; service.HasUpdate = true;
        clock.Advance(StoreUpdateSession.CheckInterval - TimeSpan.FromTicks(1));
        Drain();
        Check(service.Checks == 1, "periodic check does not run before six hours");
        clock.Advance(TimeSpan.FromTicks(1));
        await Until(() => !session.Checking && session.HasUpdate);
        Check(service.Checks == 2 && notices == 1 && session.Status == "An update is available.",
            "six-hour check detects updates and emits one notification");
        Check(Descendants(UI.StoreUpdateCard(session, 123)).OfType<ButtonElement>()
            .Single().Label == "Update", "available update renders exactly one Update action");
        session.Check(force: true);
        await Until(() => !session.Checking);
        Check(notices == 1, "unchanged availability does not repeat notifications");

        service.CheckError = new InvalidOperationException("synthetic-private-payload");
        session.Check(force: true);
        await Until(() => !session.Checking);
        Check(session.HasUpdate && session.Error is not null && !session.Error.Contains("private-payload") &&
            session.Status == "An update is available.", "failed checks preserve known updates and hide exception payloads");
        service.CheckError = null;
        service.HasUpdate = false;
        session.Check(force: true);
        await Until(() => !session.Checking);
        Check(!session.HasUpdate && session.Error is null, "external Store update clears the available notice");
        service.CheckError = new InvalidOperationException();
        session.Check(force: true);
        await Until(() => !session.Checking);
        Check(!session.HasUpdate && session.Status == "Update status unavailable." && session.Error is not null,
            "failed empty-state query is unavailable, never latest");
        Check(Descendants(UI.StoreUpdateCard(session, 123)).OfType<ButtonElement>().Single().Label == "Try again",
            "failed check offers a retry instead of an install button");
        service.CheckError = null; service.HasUpdate = true;
        session.Check(force: true);
        await Until(() => !session.Checking);
        Check(notices == 2, "a new availability episode emits a new notification");

        service.InstallGate = new();
        session.Install(123);
        session.Install(123);
        session.Check(force: true);
        Check(session.Updating && service.Installs == 1 && service.Owner == 123,
            "Update passes the window owner and blocks concurrent work");
        service.Progress!(new(.25, false));
        Drain();
        Check(session.Progress == 25 && session.Status == "Downloading update... 25%",
            "download progress reaches the About status");
        Check(Descendants(UI.StoreUpdateCard(session, 123)).OfType<ButtonElement>().Single().Modifiers?.IsEnabled == false,
            "Update action is disabled while installation is running");
        service.Progress(new(1.5, true));
        Drain();
        Check(session.Progress == 100 && session.Status.Contains("Installing"),
            "installation progress is clamped and distinguished from download");
        service.InstallGate.SetResult(StoreInstallResult.Canceled);
        await Until(() => !session.Updating);
        Check(session.HasUpdate && session.Status.Contains("canceled") && session.Error is null && service.Restarts == 0,
            "declining Store consent leaves a retryable update without restarting");
        service.Progress(new(.75, false));
        Drain();
        Check(session.Status.Contains("canceled"), "late progress cannot overwrite a terminal canceled result");

        service.InstallGate = null;
        service.InstallError = new AppOperationException(StoreUpdates.FailureMessage(StorePackageUpdateState.ErrorLowBattery));
        session.Install(123);
        await Until(() => !session.Updating);
        Check(session.HasUpdate && session.Error!.Contains("Charge") && service.Restarts == 0,
            "installation errors retain the Update action and actionable Store explanation");
        service.InstallError = new InvalidOperationException("synthetic-private-payload");
        session.Install(123);
        await Until(() => !session.Updating);
        Check(!session.Error!.Contains("private-payload"), "unexpected installation errors never expose payloads");

        service.InstallError = null; service.Result = StoreInstallResult.Completed;
        service.RestartError = new InvalidOperationException();
        session.Install(123);
        await Until(() => !session.Updating);
        Check(session.RestartRequired && session.HasUpdate && service.Restarts == 1 && session.Error is not null &&
            session.Status == "Update installed. Restart required.",
            "completion requests a new process and reports failed restart rather than claiming current process is latest");
        Check(Descendants(UI.StoreUpdateCard(session, 123)).OfType<ButtonElement>().Single().Label == "Restart",
            "failed relaunch offers Restart, not a second download");
        int installs = service.Installs, checks = service.Checks;
        session.Check(force: true);
        clock.Advance(StoreUpdateSession.CheckInterval);
        Drain();
        Check(service.Checks == checks, "pending restart cannot be overwritten with latest-version status");
        session.Install(123);
        await Until(() => !session.Updating);
        Check(service.Installs == installs && service.Restarts == 2, "Restart retries only relaunch");
        int beforeDispose = changes;
        session.Dispose();
        clock.Advance(StoreUpdateSession.CheckInterval);
        service.Progress!(new(.5, false));
        Drain();
        Check(changes == beforeDispose && service.Checks == checks, "disposal stops polling and ignores late progress");

        var late = new FakeStore { CheckGate = new() };
        using var disposed = new StoreUpdateSession(late, queue.Enqueue, clock);
        int lateNotices = 0;
        disposed.Available += () => lateNotices++;
        disposed.Start();
        disposed.Dispose();
        Check(late.CheckToken.IsCancellationRequested, "shutdown cancels outstanding Store work");
        late.CheckGate.SetResult(true);
        await Task.Delay(20);
        Drain();
        Check(lateNotices == 0 && !disposed.HasUpdate, "completed checks cannot notify after shutdown");
        return assertions;

        void Drain() { while (queue.TryDequeue(out var action)) action(); }
        async Task Until(Func<bool> predicate)
        {
            for (int i = 0; i < 250; i++)
            {
                Drain();
                if (predicate()) return;
                await Task.Delay(20);
            }
            throw new TimeoutException("Store update state did not arrive.");
        }
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + description);
            assertions++;
        }
    }

    private static IEnumerable<Element> Descendants(Element element)
    {
        yield return element;
        var children = element switch
        {
            StackElement stack => stack.Children,
            FlexElement flex => flex.Children,
            BorderElement { Child: { } child } => [child],
            _ => Array.Empty<Element>()
        };
        foreach (var child in children)
            foreach (var item in Descendants(child)) yield return item;
    }

    private sealed class FakeStore : IStoreUpdates
    {
        internal int Checks, Installs, Restarts;
        internal nint Owner;
        internal bool HasUpdate;
        internal CancellationToken CheckToken;
        internal TaskCompletionSource<bool>? CheckGate;
        internal TaskCompletionSource<StoreInstallResult>? InstallGate;
        internal Exception? CheckError, InstallError, RestartError;
        internal Action<StoreUpdateProgress>? Progress;
        internal StoreInstallResult Result = StoreInstallResult.Canceled;
        public Task<bool> CheckAsync(CancellationToken cancellationToken)
        {
            Checks++; CheckToken = cancellationToken;
            if (CheckError is { } error) throw error;
            return CheckGate?.Task ?? Task.FromResult(HasUpdate);
        }
        public Task<StoreInstallResult> InstallAsync(nint owner, Action<StoreUpdateProgress> progress,
            CancellationToken cancellationToken)
        {
            Installs++; Owner = owner; Progress = progress;
            if (InstallError is { } error) throw error;
            return InstallGate?.Task ?? Task.FromResult(Result);
        }
        public void Restart()
        {
            Restarts++;
            if (RestartError is { } error) throw error;
        }
    }

    private sealed class UpdateClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly List<UpdateTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new UpdateTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        internal void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            foreach (var timer in _timers.ToArray()) timer.Fire();
        }
        private sealed class UpdateTimer(UpdateClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset _due;
            private TimeSpan _period;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _due = clock._now + dueTime; _period = period;
                return !_disposed;
            }
            internal void Fire()
            {
                if (_disposed || clock._now < _due) return;
                _due = clock._now + _period;
                callback(state);
            }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
