using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using GHCPSpendTray.Core;
using GHCPSpendTray.App.Platform;
using Microsoft.Win32.SafeHandles;

namespace GHCPSpendTray.App.Native;

internal readonly record struct TrayPalette(uint Background, uint Foreground);

internal sealed class TrayImage : SafeHandleZeroOrMinusOneIsInvalid
{
    internal TrayImage(nint value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() => Win32.DestroyIcon(handle) != 0;
}

internal static class TrayIconRenderer
{
    internal static int SizeForDpi(uint dpi) => Math.Clamp(Win32.GetSystemMetricsForDpi(49, dpi), 16, 256);
    internal static int NotificationSizeForDpi(uint dpi) => Math.Clamp(Win32.GetSystemMetricsForDpi(11, dpi), 16, 256);

    internal static unsafe TrayPalette SystemPalette()
    {
        var contrast = new Win32.HIGHCONTRAST { cbSize = (uint)sizeof(Win32.HIGHCONTRAST) };
        if (Win32.SystemParametersInfo(0x42, contrast.cbSize, ref contrast, 0) == 0)
            throw new Win32Exception("Cannot read Windows contrast settings.");
        if ((contrast.dwFlags & 1) != 0)
            return new(Argb(Win32.GetSysColor(5)), Argb(Win32.GetSysColor(8)));
        return TrayAppearance.IsLightTheme() ? new(0xFFF3F3F3, 0xFF161616) : new(0xFF202020, 0xFFF5F5F5);
    }
    private static uint Argb(uint colorRef) =>
        0xFF000000 | ((colorRef & 0xFF) << 16) | (colorRef & 0xFF00) | ((colorRef >> 16) & 0xFF);

    internal static TrayImage Create(TrayIndicator indicator, TrayIconStyle style, int size, TrayPalette palette) =>
        Create(Pixels(indicator, style, size, palette), size);

    internal static TrayImage CreateNotification(NotificationView notification, int size, TrayPalette palette) =>
        Create(NotificationPixels(notification, size, palette), size);

    internal static uint[] NotificationPixels(NotificationView notification, int size, TrayPalette palette)
    {
        double? percent = notification.PercentConsumed is { } value ? (double)value : null;
        var indicator = new TrayIndicator(notification.AccountKey, notification.Title, percent,
            percent is null ? 0 : 1, 1, "", "");
        string? text = percent is null && notification.SpendMilestoneUsd is { } milestone
            ? "$" + milestone.ToString("0.##", CultureInfo.InvariantCulture) : null;
        return Pixels(indicator, TrayIconStyle.Pie, size, palette, text);
    }

    private static unsafe TrayImage Create(uint[] pixels, int size)
    {
        var info = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Win32.BITMAPINFOHEADER), biWidth = size, biHeight = -size,
            biPlanes = 1, biBitCount = 32
        };
        nint bitmap = 0, mask = 0;
        try
        {
            bitmap = Win32.CreateDIBSection(0, ref info, 0, out var bits, 0, 0);
            if (bitmap == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot allocate tray pixels.");
            pixels.CopyTo(new Span<uint>((void*)bits, pixels.Length));
            // Windows composites the 32-bit premultiplied alpha; the AND mask stays clear.
            byte[] maskBits = new byte[((size + 15) / 16) * 2 * size];
            fixed (byte* data = maskBits) mask = Win32.CreateBitmap(size, size, 1, 1, data);
            if (mask == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot allocate tray mask.");
            var iconInfo = new Win32.ICONINFO { fIcon = 1, hbmColor = bitmap, hbmMask = mask };
            nint icon = Win32.CreateIconIndirect(ref iconInfo);
            if (icon == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot create the usage icon.");
            return new(icon);
        }
        finally
        {
            if (mask != 0) Win32.DeleteObject(mask);
            if (bitmap != 0) Win32.DeleteObject(bitmap);
        }
    }

    internal static uint[] Pixels(TrayIndicator indicator, TrayIconStyle style, int size, TrayPalette palette) =>
        Pixels(indicator, style, size, palette, null);

    private static unsafe uint[] Pixels(TrayIndicator indicator, TrayIconStyle style, int size, TrayPalette palette, string? text)
    {
        if (size is < 16 or > 256) throw new ArgumentOutOfRangeException(nameof(size));
        const int samples = 4;
        int resolution = size * samples;
        double scale = resolution / 16d;
        var info = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Win32.BITMAPINFOHEADER), biWidth = resolution, biHeight = -resolution,
            biPlanes = 1, biBitCount = 32
        };
        nint dc = 0, bitmap = 0, previous = 0, bits = 0;
        try
        {
            dc = Win32.CreateCompatibleDC(0);
            if (dc == 0) throw new Win32Exception("Cannot allocate the tray drawing context.");
            bitmap = Win32.CreateDIBSection(dc, ref info, 0, out bits, 0, 0);
            if (bitmap == 0) throw new Win32Exception("Cannot allocate tray coverage pixels.");
            previous = Win32.SelectObject(dc, bitmap);
            if (previous == 0 || previous == -1) throw new Win32Exception("Cannot select tray coverage pixels.");
            var coverage = new Span<uint>((void*)bits, resolution * resolution);
            coverage.Clear();
            if (Win32.SetBkMode(dc, 1) == 0 || Win32.SetTextColor(dc, 0xFFFFFF) == uint.MaxValue)
                throw new Win32Exception("Cannot configure tray text rendering.");

            if (text is not null) Text(text, 16, 18);
            else if (indicator.Percent is null) Text("?", 16, 18);
            else
            {
                if (style == TrayIconStyle.Percentage)
                {
                    bool badges = indicator.IsPartial || indicator.IsOverAllocation;
                    Text(indicator.NumericText, badges ? 10 : 16, badges ? 13 : 18);
                }
                else
                {
                    double fraction = Math.Clamp(indicator.Percent.Value / 100, 0, 1);
                    for (int y = 0; y < resolution; y++)
                        for (int x = 0; x < resolution; x++)
                        {
                            double dx = (x + .5) / scale - 8, dy = (y + .5) / scale - 8;
                            double radius = Math.Sqrt(dx * dx + dy * dy);
                            double angle = (Math.Atan2(dx, -dy) + Math.Tau) % Math.Tau;
                            if (radius <= 7 && (radius >= 5.7 || angle < fraction * Math.Tau))
                                coverage[y * resolution + x] = 0xFFFFFF;
                        }
                }
                if (indicator.IsOverAllocation) Badge(1, false);
                if (indicator.IsPartial) Badge(11, true);
            }

            // Grayscale coverage, not ClearType, works on any Explorer surface.
            // Both the HICON and WriteableBitmap consume premultiplied BGRA.
            var pixels = new uint[size * size];
            uint foreground = palette.Foreground;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    uint total = 0;
                    for (int sy = 0; sy < samples; sy++)
                        for (int sx = 0; sx < samples; sx++)
                            total += coverage[(y * samples + sy) * resolution + x * samples + sx] & 255;
                    uint alpha = (total + samples * samples / 2) / (samples * samples);
                    pixels[y * size + x] = alpha << 24 |
                        (((foreground >> 16 & 255) * alpha + 127) / 255) << 16 |
                        (((foreground >> 8 & 255) * alpha + 127) / 255) << 8 |
                        ((foreground & 255) * alpha + 127) / 255;
                }
            return pixels;
        }
        finally
        {
            if (previous != 0 && previous != -1) Win32.SelectObject(dc, previous);
            if (bitmap != 0) Win32.DeleteObject(bitmap);
            if (dc != 0) Win32.DeleteDC(dc);
        }

        void Text(string text, int boxHeight, int fontHeight)
        {
            int width = 0;
            int available = (int)(14 * scale);
            for (;;)
            {
                nint font = Win32.CreateFont(-(int)(fontHeight * scale), width, 0, 0, 600,
                    0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
                if (font == 0) throw new Win32Exception("Cannot create tray text font.");
                nint oldFont = Win32.SelectObject(dc, font);
                try
                {
                    if (oldFont == 0 || oldFont == -1) throw new Win32Exception("Cannot select tray text font.");
                    var measured = new Win32.RECT();
                    if (Win32.DrawText(dc, text, text.Length, ref measured, 0x400 | 0x20 | 0x800) == 0)
                        throw new Win32Exception("Cannot measure tray text.");
                    if (measured.right > available)
                    {
                        if (width == 1) throw new InvalidOperationException("Tray text cannot fit the icon.");
                        width = width == 0 ? Math.Max(1, available / text.Length) :
                            Math.Max(1, Math.Min(width - 1, width * available / measured.right));
                        continue;
                    }
                    var bounds = new Win32.RECT
                        { left = (int)scale, top = 0, right = resolution - (int)scale, bottom = (int)(boxHeight * scale) };
                    if (Win32.DrawText(dc, text, text.Length, ref bounds, 1 | 4 | 0x20 | 0x800) == 0 ||
                        Win32.GdiFlush() == 0)
                        throw new Win32Exception("Cannot draw tray text.");
                    return;
                }
                finally
                {
                    if (oldFont != 0 && oldFont != -1) Win32.SelectObject(dc, oldFont);
                    Win32.DeleteObject(font);
                }
            }
        }

        void Badge(int x, bool partial)
        {
            Rect(x - 1, 10, 7, 6, 0);
            if (partial)
            {
                Rect(x + 2, 10.5, 1.2, 3.1, 0xFFFFFF);
                Rect(x + 2, 14.4, 1.2, 1.2, 0xFFFFFF);
            }
            else
            {
                Rect(x + 2, 11, 1.2, 4.5, 0xFFFFFF);
                Rect(x + .3, 12.7, 4.6, 1.2, 0xFFFFFF);
            }
        }
        void Rect(double x, double y, double width, double height, uint color)
        {
            int left = Math.Max(0, (int)Math.Round(x * scale)), right = Math.Min(resolution, (int)Math.Round((x + width) * scale));
            int top = Math.Max(0, (int)Math.Round(y * scale)), bottom = Math.Min(resolution, (int)Math.Round((y + height) * scale));
            var coverage = new Span<uint>((void*)bits, resolution * resolution);
            for (int py = top; py < bottom; py++)
                for (int px = left; px < right; px++)
                    coverage[py * resolution + px] = color;
        }
    }
}
