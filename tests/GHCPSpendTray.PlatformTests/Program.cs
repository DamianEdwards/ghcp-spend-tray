using GHCPSpendTray.App.Platform;
using Windows.ApplicationModel;

// Real filesystem and IPC operations use a unique directory; startup uses an in-memory store.
var root = Path.Combine(Directory.GetCurrentDirectory(), "tests", "GHCPSpendTray.PlatformTests", ".artifacts", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var failures = 0;
var tests = new (string Name, Action Run)[]
{
    ("Options require explicit isolated portable mode", () =>
    {
        var result = BootstrapOptions.Parse(["--portable", "--data-dir", root]);
        Check(result.Portable && result.DataDirectory == root, "Portable arguments not retained.");
        Check(BootstrapOptions.Parse(["--startup"]).Startup, "Startup switch lost.");
        Throws<ArgumentException>(() => BootstrapOptions.Parse(["--portable"]));
        Throws<ArgumentException>(() => BootstrapOptions.Parse(["--data-dir", root]));
        Throws<ArgumentException>(() => BootstrapOptions.Parse(["--portable", "--data-dir", root, "--startup"]));
        Throws<ArgumentException>(() => BootstrapOptions.Parse(["--handoff", Guid.NewGuid().ToString("N")]));
        Throws<ArgumentException>(() => BootstrapOptions.Parse(["--portable", "--data-dir", "relative"]));
        Throws<ArgumentException>(() => BootstrapOptions.Parse(["--startup", "--startup"]));
        Throws<ArgumentException>(() => BootstrapOptions.Parse(["--unknown"]));
    }),
    ("Paths normalize and reject device, ADS and ambiguous paths", () =>
    {
        var path = Path.Combine(root, "folder", "GHCPSpendTray.exe");
        Check(InstallationPaths.SamePath(path, path.ToUpperInvariant()), "Path comparison must ignore casing.");
        Check(InstallationPaths.NormalizeAbsolute(Path.Combine(root, ".", "a")) == Path.Combine(root, "a"), "Dot segment not normalized.");
        Check(InstallationPaths.IsWithin(Path.Combine(root, "a"), root), "Descendant path not recognized.");
        Check(!InstallationPaths.IsWithin(root + "-sibling", root), "Sibling mistaken for descendant.");
        foreach (var invalid in new[] { @"C:relative", @"\\server\share\a", @"\\?\C:\a",
            @"C:\a:b", @"C:\space \a", "C:\\quote\"\\a", @"C:\NUL.txt", @"C:\folder?\a" })
            Throws<ArgumentException>(() => InstallationPaths.NormalizeAbsolute(invalid));
    }),
    ("Startup is opt-in and follows Windows state", () =>
    {
        var store = new FakeStartupStore();
        var registration = new StartupRegistration(store);
        Check(!registration.Enabled && registration.CanChange && store.Requests == 0, "Startup enabled implicitly.");
        registration.SetEnabledAsync(true).GetAwaiter().GetResult();
        Check(registration.Enabled && store.Requests == 1, "Enable not requested.");
        registration.SetEnabledAsync(true).GetAwaiter().GetResult();
        Check(store.Requests == 1, "Unchanged preference caused an OS write.");
        registration.SetEnabledAsync(false).GetAwaiter().GetResult();
        Check(!registration.Enabled && store.Disables == 1, "Disable not applied.");
        store.State = StartupTaskState.DisabledByUser;
        Check(!registration.CanChange && registration.Description.Contains("Settings > Apps > Startup"), "External disable not reflected.");
        Throws<StartupRegistrationException>(() => registration.SetEnabledAsync(true).GetAwaiter().GetResult());
        Check(store.Requests == 1, "External user choice overridden.");
    }),
    ("Startup honors both policy states", () =>
    {
        var store = new FakeStartupStore { State = StartupTaskState.DisabledByPolicy };
        var registration = new StartupRegistration(store);
        Check(!registration.Enabled && !registration.CanChange, "Disabled policy ignored.");
        Throws<StartupRegistrationException>(() => registration.SetEnabledAsync(true).GetAwaiter().GetResult());
        store.State = StartupTaskState.EnabledByPolicy;
        Check(registration.Enabled && !registration.CanChange, "Enabled policy ignored.");
        Throws<StartupRegistrationException>(() => registration.SetEnabledAsync(false).GetAwaiter().GetResult());
        Check(store.Requests == 0 && store.Disables == 0, "Policy changed.");
    }),
    ("Startup verifies declined and racing OS changes", () =>
    {
        var store = new FakeStartupStore { EnableResult = StartupTaskState.DisabledByUser };
        var registration = new StartupRegistration(store);
        Throws<StartupRegistrationException>(() => registration.SetEnabledAsync(true).GetAwaiter().GetResult());
        Check(!registration.Enabled && !registration.CanChange, "Declined enable reported as success.");
        store.State = StartupTaskState.Enabled;
        store.IgnoreDisable = true;
        Throws<StartupRegistrationException>(() => registration.SetEnabledAsync(false).GetAwaiter().GetResult());
    }),
    ("Data directory is private", () =>
    {
        var directory = Path.Combine(root, "private");
        InstallationPaths.EnsurePrivateDirectory(directory);
        var security = new DirectoryInfo(directory).GetAccessControl();
        Check(security.AreAccessRulesProtected, "Data directory ACL inherits broad permissions.");
        var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
        Check(rules.Count == 2, "Portable ACL is not limited to the current user and SYSTEM.");
    }),
    ("Reject junction data ancestor", () =>
    {
        var target = Path.Combine(root, "junction-target");
        var link = Path.Combine(root, "junction-link");
        Directory.CreateDirectory(target);
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec")!,
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        })!;
        process.WaitForExit();
        Check(process.ExitCode == 0, "Could not create isolated junction fixture.");
        try { Throws<IOException>(() => InstallationPaths.RejectReparseAncestors(Path.Combine(link, "child"))); }
        finally { Directory.Delete(link); }
    }),
    ("Singleton activation queues until UI readiness", () =>
    {
        var scope = InstanceCoordinator.GetScope(Path.Combine(root, "singleton"));
        using var owner = InstanceCoordinator.Start(scope) ?? throw new Exception("Failed to acquire isolated singleton.");
        using var activated = new ManualResetEventSlim();
        owner.RegisterActivationCallback(activated.Set);
        Task.Run(() =>
        {
            using var duplicate = InstanceCoordinator.Start(scope);
            Check(duplicate is null, "Second singleton accepted.");
        }).GetAwaiter().GetResult();
        Check(!activated.IsSet, "Activation occurred before UI readiness.");
        owner.MarkReady();
        Check(activated.Wait(TimeSpan.FromSeconds(5)), "Activation callback not dispatched.");
    }),
    ("Activation exceptions are diagnosed without crashing IPC", () =>
    {
        var scope = InstanceCoordinator.GetScope(Path.Combine(root, "callback-error"));
        using var owner = InstanceCoordinator.Start(scope)!;
        using var diagnosed = new ManualResetEventSlim();
        owner.Diagnostic += _ => diagnosed.Set();
        owner.RegisterActivationCallback(() => throw new InvalidOperationException("inert callback failure"));
        owner.MarkReady();
        Task.Run(() => InstanceCoordinator.Activate(scope)).GetAwaiter().GetResult();
        Check(diagnosed.Wait(TimeSpan.FromSeconds(5)), "Callback failure not diagnosed.");
    }),
    ("Repeated login startup never opens an existing flyout", () =>
    {
        var scope = InstanceCoordinator.GetScope(Path.Combine(root, "quiet-startup"));
        using var owner = InstanceCoordinator.Start(scope)!;
        var activations = 0;
        owner.RegisterActivationCallback(() => Interlocked.Increment(ref activations));
        owner.MarkReady();
        Task.Run(() =>
        {
            using var duplicate = InstanceCoordinator.Start(scope, activateExisting: false);
            Check(duplicate is null, "Duplicate startup acquired the singleton.");
        }).GetAwaiter().GetResult();
        Check(activations == 0, "Login startup activated the running app.");
        Check(InstanceCoordinator.GetScope("package:test-a") != InstanceCoordinator.GetScope("package:test-b"),
            "Different package families share an instance.");
    }),
    ("Portable runtime exposes isolated data without installation", () =>
    {
        var path = Path.Combine(root, "portable");
        using var runtime = Bootstrap.Start(["--portable", "--data-dir", path]);
        Check(runtime is { IsPortable: true, IsStartup: false } && runtime.DataDirectory == path, "Wrong portable state.");
        runtime!.SignalReady();
        Check(!File.Exists(Path.Combine(path, "GHCPSpendTray.exe")), "Portable mode installed executable.");
    }),
    ("Portable mode rejects profile, volume and package data before writes", () =>
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        foreach (var path in new[] { profile, Path.GetPathRoot(root)!, packages, Path.Combine(packages, "test", "LocalState") })
            Throws<ArgumentException>(() => Bootstrap.Start(["--portable", "--data-dir", path]));
    }),
    ("Unpackaged normal launch requires installation", () =>
    {
        Check(!PackageContext.IsPackaged, "Harness unexpectedly has package identity.");
        Throws<InvalidOperationException>(() => Bootstrap.Start([]));
    }),
    ("Credential validation rejects non-app targets without calling Windows", () =>
    {
        var vault = new CredentialVault();
        Throws<ArgumentException>(() => vault.Read("other-app"));
        Throws<ArgumentException>(() => vault.Delete("other-app"));
        Throws<ArgumentException>(() => vault.Write("GHCPSpendTray/test", new string('x', 1281)));
    })
};
try
{
    foreach (var (name, run) in tests)
    {
        try { run(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + Environment.NewLine + ex); }
    }
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"{tests.Length - failures}/{tests.Length} platform tests passed.");
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
sealed class FakeStartupStore : IStartupTaskStore
{
    public StartupTaskState State { get; set; } = StartupTaskState.Disabled;
    public StartupTaskState EnableResult { get; set; } = StartupTaskState.Enabled;
    public bool IgnoreDisable { get; set; }
    public int Requests { get; private set; }
    public int Disables { get; private set; }
    public Task RequestEnableAsync() { Requests++; State = EnableResult; return Task.CompletedTask; }
    public void Disable() { Disables++; if (!IgnoreDisable) State = StartupTaskState.Disabled; }
}
