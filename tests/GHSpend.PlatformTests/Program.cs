using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using GHSpend.App.Platform;
using Microsoft.Win32;

// This harness never invokes the installer, real startup storage, or Credential Manager.
// All real filesystem/IPC operations are isolated to a unique project-local directory/scope.
var root = Path.Combine(Directory.GetCurrentDirectory(), "tests", "GHSpend.PlatformTests", ".artifacts", Guid.NewGuid().ToString("N"));
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
        Throws<ArgumentException>(() => BootstrapOptions.Parse(["--handoff", "invalid"]));
        Throws<ArgumentException>(() => BootstrapOptions.Parse(["--portable", "--data-dir", "relative"]));
        Throws<ArgumentException>(() => BootstrapOptions.Parse(["--unknown"]));
    }),
    ("Paths normalize Unicode and reject device/ADS/ambiguous paths", () =>
    {
        var path = Path.Combine(root, "日本語 folder", "ghspend.exe");
        Check(InstallationPaths.SamePath(path, path.ToUpperInvariant()), "Path comparison must ignore casing.");
        Check(InstallationPaths.NormalizeAbsolute(Path.Combine(root, ".", "a")) == Path.Combine(root, "a"), "Dot segment not normalized.");
        Check(InstallationPaths.IsWithin(Path.Combine(root, "a"), root), "Descendant path not recognized.");
        Check(!InstallationPaths.IsWithin(root + "-sibling", root), "Sibling mistaken for descendant.");
        Throws<ArgumentException>(() => InstallationPaths.NormalizeAbsolute(@"C:relative"));
        Throws<ArgumentException>(() => InstallationPaths.NormalizeAbsolute(@"\\server\share\a"));
        Throws<ArgumentException>(() => InstallationPaths.NormalizeAbsolute(@"\\?\C:\a"));
        Throws<ArgumentException>(() => InstallationPaths.NormalizeAbsolute(@"C:\a:b"));
        Throws<ArgumentException>(() => InstallationPaths.NormalizeAbsolute(@"C:\space \a"));
        Throws<ArgumentException>(() => InstallationPaths.NormalizeAbsolute("C:\\quote\"\\a"));
        Throws<ArgumentException>(() => InstallationPaths.NormalizeAbsolute(@"C:\NUL.txt"));
        Throws<ArgumentException>(() => InstallationPaths.NormalizeAbsolute(@"C:\folder?\a"));
        Throws<ArgumentException>(() => InstallationPaths.NormalizeAbsolute(@"C:\COM¹"));
    }),
    ("Startup quoting, verification and restoration use only abstraction", () =>
    {
        var store = new FakeStartupStore();
        var executable = Path.Combine(root, "日本語 folder", "ghspend.exe");
        var registration = new StartupRegistration(executable, store);
        Check(!registration.Enabled, "Empty startup should be disabled.");
        registration.SetEnabled(true);
        Check((string)store.Value!.Value == $"\"{executable}\" --startup", "Startup command not quoted correctly.");
        Check(registration.Enabled, "Enabled setting not recognized.");
        registration.SetEnabled(false);
        Check(store.Value is null, "Disable did not delete only app value.");
        var previous = new StartupValue("%USERPROFILE%\\old.exe", RegistryValueKind.ExpandString);
        registration.Restore(previous);
        Check(store.Value == previous, "Original registry value/kind not restored.");
        store.IgnoreWrites = true;
        Throws<IOException>(() => registration.SetEnabled(true));
    }),
    ("Successful install stages, verifies, registers and awaits readiness", () =>
    {
        var files = new FakeFiles();
        files.Data["source"] = [1, 2, 3];
        var store = new FakeStartupStore();
        var dest = Path.Combine(root, "ghspend.exe");
        var ready = false;
        new InstallationTransaction(files, new StartupRegistration(dest, store)).Execute("source", dest, path =>
        {
            Check(files.Data[path].SequenceEqual(new byte[] { 1, 2, 3 }), "Child launched before promotion.");
            Check(store.Value is not null, "Child launched before startup registration.");
            ready = true;
        });
        Check(ready && files.Data.Count == 2, "Install did not complete/clean staging.");
    }),
    ("Failed first launch removes executable and restores prior startup", () =>
    {
        var files = new FakeFiles();
        files.Data["source"] = [1, 2, 3];
        var old = new StartupValue("old command", RegistryValueKind.String);
        var store = new FakeStartupStore { Value = old };
        var dest = Path.Combine(root, "ghspend.exe");
        Throws<IOException>(() => new InstallationTransaction(files, new StartupRegistration(dest, store))
            .Execute("source", dest, _ => throw new IOException("Readiness failed.")));
        Check(!files.Data.ContainsKey(dest), "Failed first install retained executable.");
        Check(store.Value == old && files.Data.Count == 1, "Startup rollback/cleanup failed.");
    }),
    ("Failed upgrade restores executable without re-enabling disabled startup", () =>
    {
        var files = new FakeFiles();
        var dest = Path.Combine(root, "ghspend.exe");
        files.Data["source"] = [2];
        files.Data[dest] = [1];
        var store = new FakeStartupStore();
        Throws<IOException>(() => new InstallationTransaction(files, new StartupRegistration(dest, store))
            .Execute("source", dest, _ => throw new IOException("Readiness failed.")));
        Check(files.Data[dest].SequenceEqual(new byte[] { 1 }), "Upgrade did not restore original executable.");
        Check(store.Writes == 0 && store.Deletes == 0, "Upgrade changed startup preference.");
        Check(files.Data.Count == 2, "Backup/staging remained after successful rollback.");
    }),
    ("Successful upgrade preserves disabled startup", () =>
    {
        var files = new FakeFiles();
        var dest = Path.Combine(root, "ghspend.exe");
        files.Data["source"] = [2];
        files.Data[dest] = [1];
        var store = new FakeStartupStore();
        new InstallationTransaction(files, new StartupRegistration(dest, store)).Execute("source", dest, _ => { });
        Check(files.Data[dest].SequenceEqual(new byte[] { 2 }) && files.Data.Count == 2, "Upgrade/cleanup failed.");
        Check(store.Writes == 0 && store.Deletes == 0, "Upgrade re-enabled startup.");
    }),
    ("Startup launch preserves preference even when executable is missing", () =>
    {
        var files = new FakeFiles();
        var dest = Path.Combine(root, "ghspend.exe");
        files.Data["source"] = [1];
        var store = new FakeStartupStore();
        new InstallationTransaction(files, new StartupRegistration(dest, store))
            .Execute("source", dest, _ => { }, enableStartupOnFirstInstall: false);
        Check(store.Writes == 0 && store.Deletes == 0, "Startup launch changed startup preference.");
    }),
    ("Corrupt staging never promotes or changes startup", () =>
    {
        var files = new FakeFiles { CorruptCopy = true };
        var dest = Path.Combine(root, "ghspend.exe");
        files.Data["source"] = [1];
        var store = new FakeStartupStore();
        Throws<IOException>(() => new InstallationTransaction(files, new StartupRegistration(dest, store))
            .Execute("source", dest, _ => throw new Exception("Must not launch.")));
        Check(!files.Data.ContainsKey(dest) && store.Writes == 0, "Corrupt staging was promoted.");
        Check(files.Data.Count == 1, "Failed stage was not removed.");
    }),
    ("Startup failure after mutation rolls back both writes", () =>
    {
        var files = new FakeFiles();
        var dest = Path.Combine(root, "ghspend.exe");
        files.Data["source"] = [1];
        var store = new FakeStartupStore { FailNextWriteAfterMutation = true };
        Throws<IOException>(() => new InstallationTransaction(files, new StartupRegistration(dest, store))
            .Execute("source", dest, _ => throw new Exception("Must not launch.")));
        Check(!files.Data.ContainsKey(dest) && store.Value is null, "Partial startup write not rolled back.");
    }),
    ("Rollback failure retains executable recovery backup", () =>
    {
        var files = new FakeFiles { FailRollback = true };
        var dest = Path.Combine(root, "ghspend.exe");
        files.Data["source"] = [2];
        files.Data[dest] = [1];
        var store = new FakeStartupStore();
        Throws<AggregateException>(() => new InstallationTransaction(files, new StartupRegistration(dest, store))
            .Execute("source", dest, _ => throw new IOException("Readiness failed.")));
        Check(files.Data.Keys.Any(path => path.EndsWith(".backup", StringComparison.Ordinal)), "Recovery backup was removed.");
    }),
    ("Unstoppable failed child never rolls back its running executable", () =>
    {
        var files = new FakeFiles();
        var dest = Path.Combine(root, "ghspend.exe");
        files.Data["source"] = [2];
        files.Data[dest] = [1];
        var store = new FakeStartupStore();
        Throws<HandoffChildStillRunningException>(() => new InstallationTransaction(files, new StartupRegistration(dest, store))
            .Execute("source", dest, _ => throw new HandoffChildStillRunningException(new IOException(), new IOException())));
        Check(files.Data[dest].SequenceEqual(new byte[] { 2 }), "Running executable was rolled back.");
        Check(files.Data.Keys.Any(path => path.EndsWith(".backup", StringComparison.Ordinal)), "Recovery backup was removed.");
    }),
    ("Failed backup cleanup warns without rolling back successful handoff", () =>
    {
        var files = new FakeFiles { FailCleanupSuffix = ".backup" };
        var dest = Path.Combine(root, "ghspend.exe");
        files.Data["source"] = [2];
        files.Data[dest] = [1];
        var warnings = new List<string>();
        var store = new FakeStartupStore();
        new InstallationTransaction(files, new StartupRegistration(dest, store), warnings.Add)
            .Execute("source", dest, _ => { });
        Check(files.Data[dest].SequenceEqual(new byte[] { 2 }), "Cleanup failure rolled back a successful handoff.");
        Check(files.Data.Keys.Any(path => path.EndsWith(".backup", StringComparison.Ordinal)), "Expected retained backup.");
        Check(warnings.Count == 1 && warnings[0].Contains(".backup", StringComparison.Ordinal), "Cleanup warning not surfaced.");
        Check(!warnings[0].Contains("injected-secret", StringComparison.Ordinal), "Cleanup warning included exception content.");
    }),
    ("Failed staging cleanup preserves original error and emits fixed warning", () =>
    {
        var files = new FakeFiles { CorruptCopy = true, FailCleanupSuffix = ".stage", CleanupAccessDenied = true };
        var dest = Path.Combine(root, "ghspend.exe");
        files.Data["source"] = [1];
        var warnings = new List<string>();
        var store = new FakeStartupStore();
        Throws<IOException>(() => new InstallationTransaction(files, new StartupRegistration(dest, store), warnings.Add)
            .Execute("source", dest, _ => throw new Exception("Must not launch.")));
        Check(warnings.Count == 1 && warnings[0].Contains(".stage", StringComparison.Ordinal), "Staging warning not surfaced.");
        Check(!warnings[0].Contains("injected-secret", StringComparison.Ordinal), "Staging warning included exception content.");
        Check(!files.Data.ContainsKey(dest) && store.Writes == 0, "Cleanup warning changed rollback behavior.");
    }),
    ("Bootstrap warning subscriber failure cannot invalidate successful handoff", () =>
    {
        var files = new FakeFiles { FailCleanupSuffix = ".backup" };
        var dest = Path.Combine(root, "ghspend.exe");
        files.Data["source"] = [2];
        files.Data[dest] = [1];
        var warnings = new List<string>();
        Action<string> failing = _ => throw new InvalidOperationException("injected-secret");
        Action<string> recording = warnings.Add;
        Bootstrap.Warning += failing;
        Bootstrap.Warning += recording;
        try
        {
            new InstallationTransaction(files, new StartupRegistration(dest, new FakeStartupStore()))
                .Execute("source", dest, _ => { });
        }
        finally
        {
            Bootstrap.Warning -= failing;
            Bootstrap.Warning -= recording;
        }
        Check(warnings.Count == 1, "Later warning subscriber was skipped.");
        Check(files.Data[dest].SequenceEqual(new byte[] { 2 }), "Warning handler failure rolled back handoff.");
    }),
    ("Native copy/hash/promote preserves Mark of the Web", () =>
    {
        var directory = Path.Combine(root, "copy");
        InstallationPaths.EnsurePrivateDirectory(directory);
        var source = Path.Combine(directory, "source.exe");
        var stage = Path.Combine(directory, "stage.exe");
        var destination = Path.Combine(directory, "ghspend.exe");
        var backup = Path.Combine(directory, "old.backup");
        File.WriteAllText(source, "inert fixture - not executable");
        File.WriteAllText(source + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        File.WriteAllText(destination, "old inert fixture");
        var files = new WindowsInstallFiles();
        files.Copy(source, stage);
        Check(files.Hash(source).SequenceEqual(files.Hash(stage)), "Native copy changed file hash.");
        Check(File.ReadAllText(stage + ":Zone.Identifier").Contains("ZoneId=3", StringComparison.Ordinal), "Native copy lost MOTW.");
        files.Promote(stage, destination, backup);
        Check(File.ReadAllText(destination + ":Zone.Identifier").Contains("ZoneId=3", StringComparison.Ordinal), "Promotion lost MOTW.");
        files.Rollback(destination, backup);
        Check(File.ReadAllText(destination) == "old inert fixture", "Native rollback failed.");
        var security = new DirectoryInfo(directory).GetAccessControl();
        Check(security.AreAccessRulesProtected, "Data directory ACL inherits broad permissions.");
        var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
        Check(rules.Count == 2, "Data directory ACL is not limited to the current user and SYSTEM.");
    }),
    ("Reject junction installation ancestor", () =>
    {
        var target = Path.Combine(root, "junction-target");
        var link = Path.Combine(root, "junction-link");
        Directory.CreateDirectory(target);
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec")!,
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        })!;
        process.WaitForExit();
        Check(process.ExitCode == 0, "Could not create isolated junction fixture.");
        try { Throws<IOException>(() => InstallationPaths.RejectReparseAncestors(Path.Combine(link, "child", "ghspend.exe"))); }
        finally { Directory.Delete(link); }
    }),
    ("Singleton activation queues until explicit UI readiness", () =>
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
        Check(!activated.IsSet, "Activation occurred before the UI was ready.");
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
    ("Readiness sends a byte only on explicit signal", () =>
    {
        var id = Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream("GHSpend.Ready." + id, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ready = ReadinessHandoff.WaitForReadyAsync(server, Environment.ProcessId, timeout.Token);
        Check(!ready.IsCompleted, "Readiness signaled spontaneously.");
        var signal = Task.Run(() => ReadinessHandoff.Signal(id));
        ready.GetAwaiter().GetResult();
        signal.GetAwaiter().GetResult();
    }),
    ("Readiness rejects a response from the wrong process", () =>
    {
        var id = Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream("GHSpend.Ready." + id, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ready = ReadinessHandoff.WaitForReadyAsync(server, -1, timeout.Token);
        var signal = Task.Run(() => ReadinessHandoff.Signal(id));
        Throws<IOException>(() => ready.GetAwaiter().GetResult());
        server.Dispose();
        try { signal.GetAwaiter().GetResult(); }
        catch (IOException) { }
    }),
    ("Portable runtime never installs and exposes isolated data path", () =>
    {
        var path = Path.Combine(root, "portable");
        using var runtime = Bootstrap.Start(["--portable", "--data-dir", path]);
        Check(runtime is { IsPortable: true } && runtime.DataDirectory == path, "Portable runtime has wrong state.");
        runtime!.SignalReady();
        runtime.SignalReady();
        Check(!File.Exists(Path.Combine(path, "ghspend.exe")), "Portable mode installed executable.");
    }),
    ("Portable mode rejects production tree and profile/volume roots before writes", () =>
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Throws<ArgumentException>(() => Bootstrap.Start(["--portable", "--data-dir", profile]));
        Throws<ArgumentException>(() => Bootstrap.Start(["--portable", "--data-dir", Path.GetPathRoot(root)!]));
        Throws<ArgumentException>(() => Bootstrap.Start(["--portable", "--data-dir", Path.Combine(profile, ".ghspend")]));
        Throws<ArgumentException>(() => Bootstrap.Start(["--portable", "--data-dir", Path.Combine(profile, ".ghspend", "child")]));
    }),
    ("Credential validation rejects non-app targets without calling Windows", () =>
    {
        var vault = new CredentialVault();
        Throws<ArgumentException>(() => vault.Read("other-app"));
        Throws<ArgumentException>(() => vault.Delete("other-app"));
        Throws<ArgumentException>(() => vault.Write("GHSpend/test", new string('x', 1281)));
    })
};

try
{
    foreach (var (name, run) in tests)
    {
        try
        {
            run();
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            failures++;
            Console.Error.WriteLine("FAIL " + name + Environment.NewLine + ex);
        }
    }
}
finally
{
    Directory.Delete(root, recursive: true);
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} platform tests passed.");
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

sealed class FakeStartupStore : IStartupValueStore
{
    public StartupValue? Value { get; set; }
    public bool IgnoreWrites { get; set; }
    public bool FailNextWriteAfterMutation { get; set; }
    public int Writes { get; private set; }
    public int Deletes { get; private set; }
    public StartupValue? Read() => Value;
    public void Write(StartupValue value)
    {
        Writes++;
        if (!IgnoreWrites)
            Value = value;
        if (FailNextWriteAfterMutation)
        {
            FailNextWriteAfterMutation = false;
            throw new IOException("Injected startup write failure.");
        }
    }
    public void Delete()
    {
        Deletes++;
        Value = null;
    }
}

sealed class FakeFiles : IInstallFiles
{
    public Dictionary<string, byte[]> Data { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool CorruptCopy { get; set; }
    public bool FailRollback { get; set; }
    public string? FailCleanupSuffix { get; set; }
    public bool CleanupAccessDenied { get; set; }
    public bool Exists(string path) => Data.ContainsKey(path);
    public void Copy(string source, string staging) => Data[staging] = CorruptCopy ? [255] : Data[source].ToArray();
    public byte[] Hash(string path) => SHA256.HashData(Data[path]);
    public void Promote(string staging, string destination, string? backup)
    {
        if (backup is not null)
            Data[backup] = Data[destination];
        Data[destination] = Data[staging];
        Data.Remove(staging);
    }
    public void Rollback(string destination, string? backup)
    {
        if (FailRollback)
            throw new IOException("Injected rollback failure.");
        if (backup is null)
            Data.Remove(destination);
        else
        {
            Data[destination] = Data[backup];
            Data.Remove(backup);
        }
    }
    public void Delete(string path)
    {
        if (FailCleanupSuffix is not null && path.EndsWith(FailCleanupSuffix, StringComparison.Ordinal))
        {
            if (CleanupAccessDenied)
                throw new UnauthorizedAccessException("injected-secret");
            throw new IOException("injected-secret");
        }
        Data.Remove(path);
    }
}
