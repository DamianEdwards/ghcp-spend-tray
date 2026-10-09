using System.ComponentModel;
using GHCPSpendTray.Core;

namespace GHCPSpendTray.App.Native;

internal sealed class TrayIconSet(nint owner, string? portableDirectory, TrayIcon.ShellCall? shell = null) : IDisposable
{
    private readonly Dictionary<string, TrayIcon> _icons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _ids = new(StringComparer.Ordinal) { [""] = 1 };
    private uint _nextId = 2;
    internal IReadOnlyCollection<TrayIcon> Icons => _icons.Values;
    internal TrayIcon Primary => _icons.Values.First();
    internal TrayIcon? Find(uint id) => _icons.Values.FirstOrDefault(icon => icon.Id == id);
    internal TrayIcon? ForAccount(string? key) => _icons.GetValueOrDefault(key ?? "");

    internal void Update(TrayPresentation presentation, Func<TrayIcon?, int> size, TrayPalette palette)
    {
        if (presentation.Icons.Count == 0) throw new ArgumentException("A tray access icon is required.", nameof(presentation));
        var desired = presentation.Icons.Select(i => i.AccountKey ?? "").ToHashSet(StringComparer.Ordinal);
        foreach (var indicator in presentation.Icons)
        {
            string key = indicator.AccountKey ?? "";
            if (_icons.TryGetValue(key, out var icon))
                icon.Update(indicator, presentation.Style, size(icon), palette);
            else
            {
                if (!_ids.TryGetValue(key, out uint id))
                {
                    // NOTIFYICON_VERSION_4 packs the callback ID in a 16-bit word.
                    if (_nextId > ushort.MaxValue) throw new InvalidOperationException("Tray identity limit reached. Restart the app.");
                    _ids.Add(key, id = _nextId++);
                }
                _icons.Add(key, new(owner, id, portableDirectory, indicator, presentation.Style, size(null), palette, shell));
            }
        }
        // Install replacements first so mode switches and empty selections never
        // leave the running app without an access point.
        foreach (string key in _icons.Keys.Where(key => !desired.Contains(key)).ToArray())
        {
            _icons[key].Dispose();
            _icons.Remove(key);
        }
    }

    internal void Invalidate()
    {
        foreach (var icon in _icons.Values) icon.Invalidate();
    }

    internal void Restore()
    {
        Win32Exception? failure = null;
        foreach (var icon in _icons.Values)
        {
            try { icon.Restore(); }
            catch (Win32Exception ex) { failure ??= ex; }
        }
        if (failure is not null) throw new InvalidOperationException("Windows could not restore all tray icons.", failure);
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values) icon.Dispose();
        _icons.Clear();
    }
}
