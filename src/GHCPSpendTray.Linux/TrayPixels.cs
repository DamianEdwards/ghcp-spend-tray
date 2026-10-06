using GHCPSpendTray.Core;
using System.Buffers.Binary;
using System.IO.Compression;

namespace GHCPSpendTray.Linux;

internal static class TrayPixels
{
    // StatusNotifierItem pixmaps use network-order ARGB, not the machine's pixel byte order.
    internal static byte[] Render(TrayIndicator icon, TrayIconStyle style, int size = 32)
    {
        var pixels = new byte[size * size * 4];
        void Set(int x, int y, byte red, byte green, byte blue, byte alpha = 255)
        {
            if (x < 0 || y < 0 || x >= size || y >= size) return;
            int offset = (y * size + x) * 4;
            pixels[offset] = alpha; pixels[offset + 1] = red; pixels[offset + 2] = green; pixels[offset + 3] = blue;
        }
        double center = (size - 1) / 2d, radius = size * .43;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double dx = x - center, dy = y - center, distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance > radius + 1) continue;
            Set(x, y, 35, 35, 35);
            if (style != TrayIconStyle.Pie || icon.Percent is null) continue;
            double angle = (Math.Atan2(dy, dx) + Math.PI * 2.5) % (Math.PI * 2);
            bool fill = distance < radius - 3 && angle < Math.Clamp(icon.Percent.Value / 100, 0, 1) * Math.PI * 2;
            if (fill || distance >= radius - 1 && distance <= radius)
                Set(x, y, 240, 240, 240);
        }
        void Text(string text, int top, int scale, byte red = 255, byte green = 255, byte blue = 255)
        {
            int left = (size - (text.Length * 4 - 1) * scale) / 2;
            foreach (char character in text)
            {
                string glyph = character switch
                {
                    '0' => "111101101101111", '1' => "010110010010111", '2' => "111001111100111",
                    '3' => "111001111001111", '4' => "101101111001001", '5' => "111100111001111",
                    '6' => "111100111101111", '7' => "111001010010010", '8' => "111101111101111",
                    '9' => "111101111001111", '<' => "001010100010001", '!' => "010010010000010",
                    '+' => "000010111010000", _ => "111001010000010"
                };
                for (int row = 0; row < 5; row++)
                for (int col = 0; col < 3; col++)
                if (glyph[row * 3 + col] == '1')
                    for (int yy = 0; yy < scale; yy++)
                    for (int xx = 0; xx < scale; xx++)
                        Set(left + col * scale + xx, top + row * scale + yy, red, green, blue);
                left += 4 * scale;
            }
        }
        if (style == TrayIconStyle.Percentage || icon.Percent is null)
            Text(icon.NumericText, (size - 5 * Math.Max(1, size / 16)) / 2, Math.Max(1, size / 16));
        if (icon.IsPartial || icon.IsOverAllocation)
        {
            for (int y = size - 10; y < size; y++)
            for (int x = 0; x < size; x++)
                if (x >= size / 2 - 4 && x <= size / 2 + 4) Set(x, y, 35, 35, 35);
            Text(icon.Percent > 999 ? "+" : "!", size - 8, 1, 255, 190, 40);
        }
        return pixels;
    }

    internal static string DataUri(TrayIndicator icon, TrayIconStyle style)
    {
        const int size = 32;
        byte[] argb = Render(icon, style, size);
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string name, byte[] data)
        {
            Span<byte> word = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(word, data.Length); png.Write(word);
            byte[] type = System.Text.Encoding.ASCII.GetBytes(name);
            png.Write(type); png.Write(data);
            uint crc = uint.MaxValue;
            foreach (byte value in type.Concat(data))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
            }
            BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); png.Write(word);
        }
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, size);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8; header[9] = 6;
        Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (int y = 0; y < size; y++)
            {
                zlib.WriteByte(0);
                for (int x = 0; x < size; x++)
                {
                    int offset = (y * size + x) * 4;
                    zlib.Write(argb.AsSpan(offset + 1, 3)); zlib.WriteByte(argb[offset]);
                }
            }
        }
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
        return "data:image/png;base64," + Convert.ToBase64String(png.ToArray());
    }
}
