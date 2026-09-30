using System.ComponentModel;
using GHCPSpendTray.App.Native;
using GHCPSpendTray.Core;

internal static class TrayTests
{
    internal static void WriteSamples(string path)
    {
        const int width = 480, height = 256;
        var sheet = new uint[width * height];
        Array.Fill(sheet, 0xFF808080u);
        double?[] values = [null, 0, .1, 50, 100, 105, 1000, 50];
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < values.Length; col++)
            {
                var palette = row < 2 ? new TrayPalette(0xFFF3F3F3, 0xFF161616) : new TrayPalette(0xFF202020, 0xFFF5F5F5);
                var indicator = new TrayIndicator(null, "Synthetic", values[col], 1, col == 7 ? 2 : 1, "", "");
                var pixels = TrayIconRenderer.Pixels(indicator, row % 2 == 0 ? TrayIconStyle.Pie : TrayIconStyle.Percentage, 16, palette);
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        sheet[(row * 64 + 4 + y) * width + col * 60 + 20 + x] = pixels[y * 16 + x];
                        for (int sy = 0; sy < 2; sy++)
                            for (int sx = 0; sx < 2; sx++)
                                sheet[(row * 64 + 27 + y * 2 + sy) * width + col * 60 + 12 + x * 2 + sx] = pixels[y * 16 + x];
                    }
            }
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0x4D42);
        writer.Write(54 + sheet.Length * 4);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(width);
        writer.Write(-height);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(sheet.Length * 4);
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        foreach (uint pixel in sheet) writer.Write(pixel);
    }

    internal static void Run(Action<bool, string> check, string root)
    {
        var palette = new TrayPalette(0xFF000000, 0xFFFFFFFF);
        TrayIndicator Indicator(double? value, string? key = null, bool partial = false) =>
            new(key, key ?? "Synthetic", value, value is null ? 0 : 1, partial ? 2 : 1, "Synthetic details", "Synthetic tip");
        TrayPresentation Presentation(params TrayIndicator[] icons) => new(TrayIconStyle.Pie, icons[0], icons, []);
        foreach (int size in new[] { 16, 20, 24, 32, 48, 64 })
            foreach (var style in Enum.GetValues<TrayIconStyle>())
                foreach (double? value in new double?[] { null, 0, .1, 1, 50, 99.9, 100, 105, 999, 1000, 1e28 })
                {
                    var pixels = TrayIconRenderer.Pixels(Indicator(value), style, size, palette);
                    check(pixels.Length == size * size && pixels.All(p => p == palette.Background || p == palette.Foreground),
                        "tray pixels stay within their exact size and use an opaque contrasting palette");
                    check(pixels.Any(p => p == palette.Foreground), "tray glyph is visible even for zero and unavailable");
                    using var image = TrayIconRenderer.Create(Indicator(value), style, size, palette);
                    check(!image.IsInvalid, "native usage HICON created for every boundary and DPI");
                }
        foreach (var style in Enum.GetValues<TrayIconStyle>())
        {
            var full = TrayIconRenderer.Pixels(Indicator(50), style, 16, palette);
            var partial = TrayIconRenderer.Pixels(Indicator(50, partial: true), style, 16, palette);
            var missing = TrayIconRenderer.Pixels(Indicator(null), style, 16, palette);
            var zero = TrayIconRenderer.Pixels(Indicator(0), style, 16, palette);
            check(!full.SequenceEqual(partial) && !zero.SequenceEqual(missing),
                "partial and unavailable have distinct monochrome geometry, not just color");
        }
        var selected = Presentation(Indicator(25, "github.com:1"), Indicator(null, "example.ghe.com:2"));
        var rollUp = Presentation(Indicator(25, partial: true));
        var fake = new FakeShell();
        using (var icons = new TrayIconSet(0, root, fake.Call))
        {
            icons.Update(rollUp, _ => 16, palette);
            check(icons.Primary.Id == 1 && fake.Registered.Count == 1, "default roll-up has stable callback ID");
            fake.FailAdd = true;
            try
            {
                icons.Update(selected, _ => 16, palette);
                throw new InvalidOperationException("Expected replacement registration failure.");
            }
            catch (Win32Exception) { check(icons.Primary.Id == 1 && fake.Registered.Count == 1,
                "rejected mode switch preserves the existing access icon"); }
            fake.FailAdd = false;
            icons.Update(selected, _ => 16, palette);
            var first = icons.ForAccount("github.com:1")!;
            uint originalId = first.Id;
            Guid originalGuid = first.Identity;
            check(fake.Registered.Count == 2 && icons.Find(1) is null, "per-account mode retires roll-up and its callbacks");
            check(first.AccountKey == "github.com:1" && icons.ForAccount("example.ghe.com:2")!.Id != first.Id,
                "host-specific accounts map to different live callback IDs");
            nint handle = first.ImageHandle;
            icons.Update(selected, _ => 16, palette);
            check(first.ImageHandle == handle, "unchanged rendering does not allocate replacement HICONs");
            icons.Update(Presentation(selected.Icons.Reverse().ToArray()), _ => 24, palette);
            check(icons.ForAccount("github.com:1")!.Id == originalId, "reordering and DPI changes preserve callbacks");
            icons.Restore();
            check(fake.Registered.Count == 2 && fake.Versions.Values.All(v => v == 4),
                "Shell recovery restores every icon with keyboard-accessible version four");
            icons.Update(rollUp, _ => 16, palette);
            check(fake.Registered.Count == 1 && icons.Find(originalId) is null,
                "retired account callbacks cannot open a different account");
            icons.Update(selected, _ => 16, palette);
            check(icons.ForAccount("github.com:1")!.Id == originalId &&
                icons.ForAccount("github.com:1")!.Identity == originalGuid, "reselection preserves stable identity");
            check(fake.MinimumRegisteredAfterFirstAdd > 0, "mode switches never remove the last access icon first");
            fake.FailAdd = true;
            try
            {
                icons.Restore();
                throw new InvalidOperationException("Expected restore failure.");
            }
            catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception)
            { check(fake.Registered.Count == 0, "failed recovery invalidates every missing registration"); }
            fake.FailAdd = false;
            icons.Update(selected, _ => 16, palette);
            check(fake.Registered.Count == 2, "next unchanged dashboard retries all failed Shell registrations");
        }
        check(fake.Registered.Count == 0, "disposing the icon set removes all owned Shell registrations");
        check(TrayIcon.StableIdentity(root, "github.com:1") == TrayIcon.StableIdentity(root.ToUpperInvariant(), "github.com:1") &&
            TrayIcon.StableIdentity(root, "github.com:1") != TrayIcon.StableIdentity(root, "example.ghe.com:1") &&
            TrayIcon.StableIdentity(root, null) != TrayIcon.StableIdentity(null, null),
            "persistent GUIDs isolate portable instances and immutable host identities");
        fake = new FakeShell { FailVersion = true };
        try
        {
            using var rejected = new TrayIcon(0, 1, root, Indicator(50), TrayIconStyle.Pie, 16, palette, fake.Call);
            throw new InvalidOperationException("Expected Shell version failure.");
        }
        catch (Win32Exception) { check(fake.Registered.Count == 0, "failed constructor removes a partially added icon"); }
        fake = new FakeShell();
        using (var icon = new TrayIcon(0, 1, root, Indicator(50), TrayIconStyle.Pie, 16, palette, fake.Call))
        {
            nint previous = icon.ImageHandle;
            fake.FailModify = fake.FailAdd = true;
            try
            {
                icon.Update(Indicator(70), TrayIconStyle.Percentage, 24, palette);
                throw new InvalidOperationException("Expected Shell update failure.");
            }
            catch (Win32Exception) { check(icon.ImageHandle == previous, "failed replacement preserves the previous owned image"); }
            fake.FailModify = fake.FailAdd = false;
            icon.Update(Indicator(70), TrayIconStyle.Percentage, 24, palette);
        }
        check(fake.Registered.Count == 0, "failed updates do not orphan a Shell icon on disposal");
        uint gdi = Win32.GetGuiResources(Win32.GetCurrentProcess(), 0);
        uint user = Win32.GetGuiResources(Win32.GetCurrentProcess(), 1);
        for (int iteration = 0; iteration < 250; iteration++)
        {
            using var icons = new TrayIconSet(0, root, new FakeShell().Call);
            icons.Update(selected, _ => 16, palette);
            icons.Update(rollUp, _ => 32, palette);
            icons.Restore();
        }
        check(Win32.GetGuiResources(Win32.GetCurrentProcess(), 0) <= gdi + 2 &&
            Win32.GetGuiResources(Win32.GetCurrentProcess(), 1) <= user + 2,
            "750 icon lifetimes and replacements do not accumulate GDI or USER handles");
    }

    private sealed class FakeShell
    {
        internal Dictionary<uint, Guid> Registered { get; } = [];
        internal Dictionary<uint, uint> Versions { get; } = [];
        internal bool FailVersion, FailModify, FailAdd;
        internal int MinimumRegisteredAfterFirstAdd { get; private set; } = int.MaxValue;
        internal int Call(uint message, ref Win32.NOTIFYICONDATA data)
        {
            switch (message)
            {
                case Win32.NIM_ADD:
                    if (FailAdd) return 0;
                    Registered.Add(data.uID, data.guidItem);
                    break;
                case Win32.NIM_DELETE:
                    Registered.Remove(data.uID);
                    break;
                case Win32.NIM_SETVERSION:
                    if (FailVersion) return 0;
                    Versions[data.uID] = data.uTimeoutOrVersion;
                    break;
                case Win32.NIM_MODIFY:
                    if (FailModify) return 0;
                    if (!Registered.ContainsKey(data.uID)) return 0;
                    break;
            }
            MinimumRegisteredAfterFirstAdd = Math.Min(MinimumRegisteredAfterFirstAdd, Registered.Count);
            return 1;
        }
    }
}
