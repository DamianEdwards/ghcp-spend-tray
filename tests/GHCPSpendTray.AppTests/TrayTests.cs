using System.ComponentModel;
using GHCPSpendTray.App.Native;
using GHCPSpendTray.Core;

internal static class TrayTests
{
    internal static void WriteSamples(string path, int size = 16)
    {
        if (size is < 16 or > 64) throw new ArgumentOutOfRangeException(nameof(size));
        int cellWidth = Math.Max(60, size * 2 + 16), cellHeight = size * 3 + 16;
        double?[] values = [null, 0, .1, 50, 100, 105, 1000, 50, null, null];
        int width = cellWidth * values.Length, height = cellHeight * 4;
        var sheet = new uint[width * height];
        Array.Fill(sheet, 0xFF808080u);
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < values.Length; col++)
            {
                var palette = row < 2 ? new TrayPalette(0xFFF3F3F3, 0xFF161616) : new TrayPalette(0xFF202020, 0xFFF5F5F5);
                var indicator = new TrayIndicator(null, "Synthetic", values[col], 1, col is 7 or 9 ? 2 : 1,
                    "", "", IsUnlimited: col >= 8);
                var pixels = TrayIconRenderer.Pixels(indicator, row % 2 == 0 ? TrayIconStyle.Pie : TrayIconStyle.Percentage, size, palette);
                for (int y = 0; y < cellHeight; y++)
                    Array.Fill(sheet, palette.Background, (row * cellHeight + y) * width + col * cellWidth, cellWidth);
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        uint pixel = Composite(pixels[y * size + x], palette.Background);
                        sheet[(row * cellHeight + 4 + y) * width + col * cellWidth + (cellWidth - size) / 2 + x] = pixel;
                        for (int sy = 0; sy < 2; sy++)
                            for (int sx = 0; sx < 2; sx++)
                                sheet[(row * cellHeight + size + 10 + y * 2 + sy) * width + col * cellWidth +
                                    (cellWidth - size * 2) / 2 + x * 2 + sx] = pixel;
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

    private static uint Composite(uint pixel, uint background)
    {
        uint inverse = 255 - (pixel >> 24);
        return 0xFF000000 |
            (((pixel >> 16 & 255) + (background >> 16 & 255) * inverse / 255) << 16) |
            (((pixel >> 8 & 255) + (background >> 8 & 255) * inverse / 255) << 8) |
            ((pixel & 255) + (background & 255) * inverse / 255);
    }

    internal static unsafe void Run(Action<bool, string> check, string root)
    {
        var palette = new TrayPalette(0xFF000000, 0xFFFFFFFF);
        TrayIndicator Indicator(double? value, string? key = null, bool partial = false) =>
            new(key, key ?? "Synthetic", value, value is null ? 0 : 1, partial ? 2 : 1, "Synthetic details", "Synthetic tip");
        TrayPresentation Presentation(params TrayIndicator[] icons) => new(TrayIconStyle.Pie, icons[0], icons, []);
        foreach (uint dpi in new uint[] { 96, 120, 144, 192 })
            check(TrayIconRenderer.NotificationSizeForDpi(dpi) == Win32.GetSystemMetricsForDpi(11, dpi),
                "large notification icons use the monitor's large-icon metric rather than its small tray metric");
        foreach (int size in new[] { 32, 40, 48, 64 })
        {
            foreach (decimal value in new decimal[] { 0, .1m, 50, 87.5m, 100, 105 })
            {
                var notification = new NotificationView("github.com:1", "Synthetic", "Synthetic alert", value, 100m);
                var pixels = TrayIconRenderer.NotificationPixels(notification, size, palette);
                check(pixels.SequenceEqual(TrayIconRenderer.Pixels(Indicator((double)value, "github.com:1"),
                    TrayIconStyle.Pie, size, palette)),
                    "allocation notifications use the actual account percentage and over-allocation badge even with a dollar milestone");
                using var image = TrayIconRenderer.CreateNotification(notification, size, palette);
                check(!image.IsInvalid, "large allocation notification HICON created");
            }
            var unavailable = new NotificationView("github.com:1", "Synthetic", "Synthetic alert");
            var unknownPixels = TrayIconRenderer.NotificationPixels(unavailable, size, palette);
            check(unknownPixels.SequenceEqual(TrayIconRenderer.Pixels(Indicator(null), TrayIconStyle.Pie, size, palette)),
                "missing allocation renders unavailable, never a zero-percent pie");
            var update = new NotificationView("store-update", "Update available", "Synthetic update");
            var updatePixels = TrayIconRenderer.NotificationPixels(update, size, palette, updateAvailable: true);
            check(updatePixels.Length == size * size && updatePixels.Any(p => p >> 24 > 128) &&
                updatePixels.Any(p => p >> 24 is > 0 and < 255) && updatePixels[0] == 0 &&
                !updatePixels.SequenceEqual(unknownPixels),
                "update notifications use a distinct antialiased download glyph, not unavailable-consumption artwork");
            var tintedUpdate = TrayIconRenderer.NotificationPixels(update, size,
                new(0xFF202020, 0xFF19AAE6), updateAvailable: true);
            check(tintedUpdate.Select(p => p >> 24).SequenceEqual(updatePixels.Select(p => p >> 24)) &&
                tintedUpdate.All(p => (p >> 16 & 255) == (0x19 * (p >> 24) + 127) / 255 &&
                    (p >> 8 & 255) == (0xAA * (p >> 24) + 127) / 255 &&
                    (p & 255) == (0xE6 * (p >> 24) + 127) / 255),
                "update notification glyph preserves theme and high-contrast tinting at every notification DPI");
            using (var image = TrayIconRenderer.CreateNotification(update, size, palette, updateAvailable: true))
                check(!image.IsInvalid, "native update notification HICON created");
            foreach (decimal milestone in new decimal[] { .01m, 50, 100, 1234.56m, 1e28m })
            {
                var notification = unavailable with { SpendMilestoneUsd = milestone };
                var pixels = TrayIconRenderer.NotificationPixels(notification, size, palette);
                check(pixels.Length == size * size && pixels.Any(p => p >> 24 > 128) &&
                    pixels.Any(p => p >> 24 is > 0 and < 255) && pixels[0] == 0 &&
                    !pixels.SequenceEqual(unknownPixels),
                    "dollar-only alerts have distinct, antialiased milestone artwork at every notification DPI");
                using var image = TrayIconRenderer.CreateNotification(notification, size, palette);
                check(!image.IsInvalid, "dollar-only notification HICON created without requiring an allocation");
            }
        }
        foreach (int size in new[] { 16, 20, 24, 32, 48, 64 })
            foreach (var style in Enum.GetValues<TrayIconStyle>())
                foreach (double? value in new double?[] { null, 0, .1, 1, 50, 99.9, 100, 105, 999, 1000, 1e28 })
                {
                    var pixels = TrayIconRenderer.Pixels(Indicator(value), style, size, palette);
                    check(pixels.Length == size * size && pixels[0] == 0 && pixels[size - 1] == 0 &&
                        pixels[^size] == 0 && pixels[^1] == 0,
                        "tray artwork has transparent corners and exact native pixel dimensions");
                    check(pixels.Any(p => p >> 24 > 128) && pixels.Any(p => p >> 24 is > 0 and < 255),
                        "native glyphs and pie contours have strong ink with antialiased edge coverage");
                    check(pixels.All(p => (p & 255) == p >> 24 && (p >> 8 & 255) == p >> 24 &&
                        (p >> 16 & 255) == p >> 24), "white icon coverage is premultiplied without ClearType color fringes");
                    using var image = TrayIconRenderer.Create(Indicator(value), style, size, palette);
                    check(!image.IsInvalid, "native usage HICON created for every boundary and DPI");
                }
        foreach (var style in Enum.GetValues<TrayIconStyle>())
        {
            foreach (int size in new[] { 16, 20, 24, 32, 48, 64 })
            {
                var unlimited = Indicator(null) with { IsUnlimited = true, IncludedAccounts = 1 };
                var pixels = TrayIconRenderer.Pixels(unlimited, style, size, palette);
                check(pixels.Length == size * size && pixels[0] == 0 && pixels[^1] == 0 &&
                    pixels.Any(p => p >> 24 > 128) && pixels.Any(p => p >> 24 is > 0 and < 255),
                    "infinity has visible antialiased ink and transparent corners at every tray DPI");
                check(!pixels.SequenceEqual(TrayIconRenderer.Pixels(Indicator(null), style, size, palette)) &&
                    !pixels.SequenceEqual(TrayIconRenderer.Pixels(Indicator(0), style, size, palette)),
                    "unlimited is distinct from unavailable and zero allocation usage");
                check(pixels.SequenceEqual(TrayIconRenderer.Pixels(unlimited, TrayIconStyle.Pie, size, palette)) &&
                    !pixels.SequenceEqual(TrayIconRenderer.Pixels(unlimited with { SelectedAccounts = 2 }, style, size, palette)),
                    "both styles render infinity and partial unlimited roll-ups retain their warning badge");
                using var image = TrayIconRenderer.Create(unlimited, style, size, palette);
                check(!image.IsInvalid, "native infinity HICON created at every tray DPI");
            }
            var full = TrayIconRenderer.Pixels(Indicator(50), style, 16, palette);
            var partial = TrayIconRenderer.Pixels(Indicator(50, partial: true), style, 16, palette);
            var missing = TrayIconRenderer.Pixels(Indicator(null), style, 16, palette);
            var zero = TrayIconRenderer.Pixels(Indicator(0), style, 16, palette);
            check(!full.SequenceEqual(partial) && !zero.SequenceEqual(missing),
                "partial and unavailable have distinct monochrome geometry, not just color");
            var opaqueWhite = TrayIconRenderer.Pixels(Indicator(50), style, 24, palette);
            var color = new TrayPalette(0xFFB02090, 0xFF19AAE6);
            var tinted = TrayIconRenderer.Pixels(Indicator(50), style, 24, color);
            check(tinted.Select(p => p >> 24).SequenceEqual(opaqueWhite.Select(p => p >> 24)) &&
                tinted.All(p => (p >> 16 & 255) == (0x19 * (p >> 24) + 127) / 255 &&
                    (p >> 8 & 255) == (0xAA * (p >> 24) + 127) / 255 &&
                    (p & 255) == (0xE6 * (p >> 24) + 127) / 255),
                "theme and high-contrast colors tint the same transparent antialiased coverage correctly");
            check(tinted.SequenceEqual(TrayIconRenderer.Pixels(Indicator(50), style, 24,
                color with { Background = 0xFF00FFFF })), "tray artwork never bakes in a taskbar background rectangle");
        }
        var selected = Presentation(Indicator(25, "github.com:1"), Indicator(null, "example.ghe.com:2"));
        var rollUp = Presentation(Indicator(25, partial: true));
        var callbackShell = new FakeShell();
        using (var host = new TrayHost(root, callbackShell.Call))
        {
            List<string?> opened = [];
            int settings = 0, notifications = 0;
            string? notificationAccount = null;
            host.OpenRequested += opened.Add;
            host.SettingsRequested += () => settings++;
            host.NotificationClicked += key => { notifications++; notificationAccount = key; };
            void Send(int action, uint id = 1) =>
                Win32.SendMessage(host.Handle, Win32.WM_TRAY, 0, (nint)((id << 16) | (uint)action));

            Send(0x201);
            Send(Win32.NIN_SELECT);
            check(opened.SequenceEqual(new string?[] { null }), "mouse selection is delivered immediately without a timer");
            Send(0x202);
            check(opened.Count == 1, "raw button-down/up callbacks do not double-handle one physical selection");
            Send(Win32.NIN_KEYSELECT);
            check(opened.Count == 2, "keyboard activation uses the same immediate selection path");
            opened.Clear();
            Send(Win32.NIN_SELECT);
            Send(0x203);
            Send(Win32.NIN_SELECT);
            Win32.SendMessage(host.Handle, 0x113, 1, 0);
            check(opened.Count == 2 && settings == 0,
                "double-click sequence produces ordinary selections, never settings or a deferred timer selection");
            opened.Clear();
            Send(Win32.NIN_SELECT);
            Send(Win32.NIN_SELECT);
            check(opened.Count == 2 && settings == 0, "rapid selections are not arbitrated as a settings shortcut");
            host.Update(selected);
            opened.Clear();
            foreach (var icon in host.Icons)
            {
                Send(Win32.NIN_SELECT, icon.Id);
                Send(Win32.NIN_KEYSELECT, icon.Id);
            }
            check(opened.SequenceEqual(selected.Icons.SelectMany(icon => new[] { icon.AccountKey, icon.AccountKey })),
                "mouse and keyboard callbacks preserve each host-specific account identity");
            uint retired = host.Icons.First().Id;
            check(host.Notify(new("example.ghe.com:2", "Synthetic", "Synthetic click", 50m)),
                "host accepts a notification for a selected account");
            Send(Win32.NIN_BALLOONUSERCLICK, host.Icons.Last().Id);
            check(notifications == 1 && notificationAccount == "example.ghe.com:2",
                "native notification callback preserves its accepted account");
            check(host.Notify(new("store-update", "Update available", "Synthetic update"), updateAvailable: true),
                "host accepts an update notification with dedicated download artwork");
            Send(Win32.NIN_BALLOONUSERCLICK, host.Icons.First().Id);
            check(notifications == 2 && notificationAccount == "store-update",
                "dedicated update artwork preserves notification routing to About");
            host.Update(rollUp);
            opened.Clear();
            Send(Win32.NIN_SELECT, retired);
            Send(Win32.NIN_KEYSELECT, retired);
            Send(0x203, retired);
            Send(Win32.NIN_BALLOONUSERCLICK, retired);
            check(opened.Count == 0 && notifications == 2 && settings == 0,
                "all retired native callback forms are ignored");
            Win32.SendMessage(host.Handle, Win32.RegisterWindowMessage("TaskbarCreated"), 0, 0);
            Send(Win32.NIN_SELECT);
            check(opened.Count == 1 && callbackShell.Versions.Values.All(version => version == 4),
                "taskbar recovery retains immediate version-four selection semantics");
            int refreshes = 0, exits = 0;
            host.RefreshRequested += () => refreshes++;
            host.ExitRequested += () => exits++;
            host.ExecuteMenuCommand(0);
            host.ExecuteMenuCommand(3);
            check(settings == 1 && opened.Count == 1, "context-menu Settings remains the explicit settings shortcut");
            host.ExecuteMenuCommand(1);
            host.ExecuteMenuCommand(2);
            host.ExecuteMenuCommand(4);
            check(opened.Count == 2 && opened[^1] is null && refreshes == 1 && exits == 1,
                "context-menu Open, Refresh and Exit retain their routing");
            bool ending = false;
            host.SessionEnding += value => ending = value;
            check(Win32.SendMessage(host.Handle, Win32.WM_QUERYENDSESSION, 0, 1) == 1 && ending,
                "Restart Manager can close the tray process for package replacement");
            Win32.SendMessage(host.Handle, Win32.WM_ENDSESSION, 0, 1);
            check(exits == 1 && !ending, "canceled Restart Manager shutdown leaves the tray running");
            Win32.SendMessage(host.Handle, Win32.WM_ENDSESSION, 1, 1);
            check(exits == 2 && ending, "confirmed Restart Manager shutdown routes through application exit");
        }
        check(callbackShell.Registered.Count == 0, "native callback host disposal releases its icons");
        var recoveryShell = new FakeShell();
        using (var host = new TrayHost(root, recoveryShell.Call))
        {
            int failures = 0, appearances = 0;
            host.UpdateFailed += () => { failures++; host.Update(rollUp); };
            host.AppearanceChanged += () => appearances++;
            void Retry() => Win32.SendMessage(host.Handle, Win32.WM_TIMER, TrayHost.RetryTimerId, 0);
            uint taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
            recoveryShell.FailAdd = true;
            Win32.SendMessage(host.Handle, taskbarCreated, 0, 0);
            check(failures == 0 && recoveryShell.Registered.Count == 0,
                "temporary TaskbarCreated failure schedules recovery without a modal Shell callback error");
            int calls = recoveryShell.AddCalls;
            host.Update(selected);
            Win32.SendMessage(host.Handle, 0x7E, 0, 0);
            check(recoveryShell.AddCalls == calls && appearances == 1,
                "dashboard and display changes coalesce without restarting the pending recovery");
            recoveryShell.FailAdd = false;
            Retry();
            check(failures == 0 && recoveryShell.Registered.Count == 2 &&
                host.Icons.Select(icon => icon.AccountKey).SequenceEqual(selected.Icons.Select(icon => icon.AccountKey)) &&
                recoveryShell.Versions.Values.All(version => version == 4),
                "timer recovery installs the latest presentation and accessible account callbacks");
            calls = recoveryShell.AddCalls;
            Retry();
            check(recoveryShell.AddCalls == calls, "stale timer callbacks do not repeat a completed recovery");
            recoveryShell.Registered.Clear();
            recoveryShell.FailAdd = true;
            host.Update(Presentation(Indicator(75, "github.com:1"), Indicator(50, "example.ghe.com:2")));
            host.Update(selected);
            recoveryShell.FailAdd = false;
            Retry();
            check(failures == 0 && recoveryShell.Registered.Count == 2,
                "failed modify and re-add cannot cache a missing icon when the presentation reverts");
            recoveryShell.FailAdd = true;
            Win32.SendMessage(host.Handle, taskbarCreated, 0, 0);
            calls = recoveryShell.AddCalls;
            for (int attempt = 0; attempt < TrayHost.RetryLimit; attempt++)
            {
                host.Update(selected);
                Retry();
                check(failures == (attempt == TrayHost.RetryLimit - 1 ? 1 : 0),
                    "persistent Shell failures are surfaced once only after the bounded retries");
            }
            check(recoveryShell.AddCalls == calls + TrayHost.RetryLimit,
                "repeated dashboards do not replenish the five-attempt timer retry budget");
            calls = recoveryShell.AddCalls;
            Retry();
            check(recoveryShell.AddCalls == calls && failures == 1,
                "exhaustion stops the timer and reporting does not recursively restart recovery");
            recoveryShell.FailAdd = false;
            host.Update(selected);
            check(recoveryShell.Registered.Count == 2 && failures == 1,
                "an explicit later dashboard update can recover after retry exhaustion");
            recoveryShell.FailAdd = true;
            Win32.SendMessage(host.Handle, taskbarCreated, 0, 0);
            host.Dispose();
            check(recoveryShell.Registered.Count == 0, "disposal cancels pending recovery and removes owned icons");
        }
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
        fake = new FakeShell();
        using (var icon = new TrayIcon(0, 1, root, Indicator(25), TrayIconStyle.Percentage, 16, palette, fake.Call))
        {
            nint trayImage = icon.ImageHandle;
            var notification = new NotificationView("github.com:2", "Synthetic allocation alert", "Synthetic message", 87.5m);
            check(icon.Notify(notification, 48, palette), "custom notification accepted through an existing roll-up icon");
            var data = fake.LastNotification!.Value;
            nint balloonImage = data.hBalloonIcon;
            check(data.uFlags == (Win32.NIF_INFO | Win32.NIF_GUID) &&
                data.dwInfoFlags == (Win32.NIIF_USER | Win32.NIIF_LARGE_ICON | Win32.NIIF_RESPECT_QUIET_TIME) &&
                balloonImage != 0 && balloonImage != trayImage && data.hIcon == trayImage,
                "Shell receives a separate large custom balloon HICON and still respects quiet time");
            check(new string(data.szInfoTitle) == notification.Title && new string(data.szInfo) == notification.Message &&
                icon.NotificationAccount == notification.AccountKey, "custom artwork preserves notification text and click-account routing");
            icon.Update(Indicator(30), TrayIconStyle.Pie, 24, palette);
            icon.Restore();
            check(fake.NotificationCalls == 1 && icon.NotificationAccount == notification.AccountKey,
                "dashboard updates and Shell restart recovery do not resubmit notifications or change click routing");
            fake.FailModify = true;
            check(!icon.Notify(notification with { AccountKey = "github.com:3" }, 32, palette) &&
                icon.NotificationAccount == "github.com:2",
                "rejected notification does not replace the accepted notification's account");
            fake.FailModify = false;
            fake.ThrowNotification = true;
            try
            {
                icon.Notify(notification with { AccountKey = "github.com:3" }, 32, palette);
                throw new InvalidOperationException("Expected notification submission exception.");
            }
            catch (Win32Exception)
            {
                check(icon.NotificationAccount == "github.com:2", "submission exception preserves prior notification routing");
            }
        }
        check(fake.Registered.Count == 0, "disposing a notified tray icon removes its Shell registration");
        uint gdi = Win32.GetGuiResources(Win32.GetCurrentProcess(), 0);
        uint user = Win32.GetGuiResources(Win32.GetCurrentProcess(), 1);
        for (int iteration = 0; iteration < 250; iteration++)
        {
            using var icons = new TrayIconSet(0, root, new FakeShell().Call);
            icons.Update(selected, _ => 16, palette);
            icons.Primary.Notify(new("github.com:1", "Synthetic", "Synthetic allocation", 50m), 48, palette);
            icons.Primary.Notify(new("github.com:1", "Synthetic", "Synthetic milestone", SpendMilestoneUsd: 100m), 32, palette);
            icons.Update(rollUp, _ => 32, palette);
            icons.Restore();
        }
        check(Win32.GetGuiResources(Win32.GetCurrentProcess(), 0) <= gdi + 2 &&
            Win32.GetGuiResources(Win32.GetCurrentProcess(), 1) <= user + 2,
            "tray and notification icon lifetimes and replacements do not accumulate GDI or USER handles");
    }

    private sealed class FakeShell
    {
        internal Dictionary<uint, Guid> Registered { get; } = [];
        internal Dictionary<uint, uint> Versions { get; } = [];
        internal bool FailVersion, FailModify, FailAdd;
        internal bool ThrowNotification;
        internal Win32.NOTIFYICONDATA? LastNotification { get; private set; }
        internal int NotificationCalls { get; private set; }
        internal int AddCalls { get; private set; }
        internal int MinimumRegisteredAfterFirstAdd { get; private set; } = int.MaxValue;
        internal int Call(uint message, ref Win32.NOTIFYICONDATA data)
        {
            switch (message)
            {
                case Win32.NIM_ADD:
                    AddCalls++;
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
                    if ((data.uFlags & Win32.NIF_INFO) != 0)
                    {
                        NotificationCalls++;
                        LastNotification = data;
                        if (ThrowNotification) throw new Win32Exception("Synthetic notification failure.");
                    }
                    else if (data.hBalloonIcon != 0)
                        throw new InvalidOperationException("Notification artwork leaked into a tray update.");
                    if (FailModify) return 0;
                    if (!Registered.ContainsKey(data.uID)) return 0;
                    break;
            }
            MinimumRegisteredAfterFirstAdd = Math.Min(MinimumRegisteredAfterFirstAdd, Registered.Count);
            return 1;
        }
    }
}
