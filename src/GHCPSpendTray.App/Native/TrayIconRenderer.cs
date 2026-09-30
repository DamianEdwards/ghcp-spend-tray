using System.ComponentModel;
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

    internal static unsafe TrayImage Create(TrayIndicator indicator, TrayIconStyle style, int size, TrayPalette palette)
    {
        uint[] pixels = Pixels(indicator, style, size, palette);
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
            // An explicit zero AND mask makes the fully opaque, high-contrast pixels
            // independent of the Shell's compositing background.
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

    internal static uint[] Pixels(TrayIndicator indicator, TrayIconStyle style, int size, TrayPalette palette)
    {
        if (size is < 16 or > 256) throw new ArgumentOutOfRangeException(nameof(size));
        var pixels = new uint[size * size];
        Array.Fill(pixels, palette.Background | 0xFF000000);
        if (indicator.Percent is null)
        {
            Text("?", 5, 1, 2, 2);
            return pixels;
        }
        if (style == TrayIconStyle.Percentage)
        {
            string text = indicator.NumericText;
            int scale = text.Length <= 2 ? 2 : 1;
            int width = (text.Length * 4 - 1) * scale;
            Text(text, (16 - width) / 2, 1, scale, 2);
            Text("%", 1, 11);
            if (indicator.IsOverAllocation) Text("+", 6, 11);
        }
        else
        {
            double fraction = Math.Clamp(indicator.Percent.Value / 100, 0, 1);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    double dx = (x + .5) * 16 / size - 8, dy = (y + .5) * 16 / size - 8;
                    double radius = Math.Sqrt(dx * dx + dy * dy);
                    double angle = (Math.Atan2(dx, -dy) + Math.Tau) % Math.Tau;
                    if (radius <= 7 && (radius >= 5.8 || angle < fraction * Math.Tau))
                        pixels[y * size + x] = palette.Foreground | 0xFF000000;
                }
            if (indicator.IsOverAllocation) Badge("+", 1);
        }
        if (indicator.IsPartial) Badge("!", 11);
        return pixels;

        void Badge(string text, int x)
        {
            Rect(x - 1, 10, 5, 6, palette.Background);
            Text(text, x, 11);
        }
        void Text(string text, int left, int top, int sx = 1, int sy = 1)
        {
            foreach (char character in text)
            {
                string glyph = Glyph(character);
                for (int row = 0; row < 5; row++)
                    for (int col = 0; col < 3; col++)
                        if (glyph[row * 3 + col] == '1')
                            Rect(left + col * sx, top + row * sy, sx, sy, palette.Foreground);
                left += 4 * sx;
            }
        }
        void Rect(int x, int y, int width, int height, uint color)
        {
            int left = x * size / 16, right = (x + width) * size / 16;
            int top = y * size / 16, bottom = (y + height) * size / 16;
            for (int py = top; py < bottom; py++)
                for (int px = left; px < right; px++)
                    pixels[py * size + px] = color | 0xFF000000;
        }
    }

    // A three-column pixel face has predictable bounds even at the actual 16px
    // Shell size. No font fallback, ClearType fringe, or shrinking of large values.
    private static string Glyph(char value) => value switch
    {
        '0' => "111101101101111", '1' => "010110010010111",
        '2' => "111001111100111", '3' => "111001111001111",
        '4' => "101101111001001", '5' => "111100111001111",
        '6' => "111100111101111", '7' => "111001010010010",
        '8' => "111101111101111", '9' => "111101111001111",
        '?' => "111001010000010", '!' => "010010010000010",
        '<' => "001010100010001", '+' => "000010111010000",
        '%' => "101001010100101",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
