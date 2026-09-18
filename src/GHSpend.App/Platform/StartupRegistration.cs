using Microsoft.Win32;

namespace GHSpend.App.Platform;

public sealed class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GHSpend";
    private readonly IStartupValueStore _store;
    private readonly string _command;

    public StartupRegistration(string installedExecutablePath)
        : this(installedExecutablePath, new RegistryStartupValueStore()) { }

    internal StartupRegistration(string installedExecutablePath, IStartupValueStore store)
    {
        _command = BuildCommand(installedExecutablePath);
        _store = store;
    }

    /// <summary>The app's Run registration, not Windows StartupApproved policy.</summary>
    public bool Enabled => _store.Read() is { Kind: RegistryValueKind.String, Value: string value } &&
        string.Equals(value, _command, StringComparison.OrdinalIgnoreCase);

    public void SetEnabled(bool enabled)
    {
        if (enabled)
            _store.Write(new StartupValue(_command, RegistryValueKind.String));
        else
            _store.Delete();
        if (enabled ? !Enabled : _store.Read() is not null)
            throw new IOException("Windows did not retain the requested GHSpend startup setting.");
    }

    internal StartupValue? Capture() => _store.Read();

    internal void Restore(StartupValue? value)
    {
        if (value is null)
            _store.Delete();
        else
            _store.Write(value);
        var actual = _store.Read();
        if (actual?.Kind != value?.Kind ||
            !System.Collections.StructuralComparisons.StructuralEqualityComparer.Equals(actual?.Value, value?.Value))
            throw new IOException("Could not restore the previous GHSpend startup setting.");
    }

    internal static string BuildCommand(string executable)
    {
        executable = InstallationPaths.NormalizeAbsolute(executable);
        if (executable.Contains('"'))
            throw new ArgumentException("Executable paths cannot contain quotation marks.", nameof(executable));
        return $"\"{executable}\" --startup";
    }

    private sealed class RegistryStartupValueStore : IStartupValueStore
    {
        public StartupValue? Read()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value is null ? null : new StartupValue(value, key!.GetValueKind(ValueName));
        }

        public void Write(StartupValue value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key.SetValue(ValueName, value.Value, value.Kind);
        }

        public void Delete()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

internal sealed record StartupValue(object Value, RegistryValueKind Kind);

internal interface IStartupValueStore
{
    StartupValue? Read();
    void Write(StartupValue value);
    void Delete();
}
