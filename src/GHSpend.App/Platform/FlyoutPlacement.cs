namespace GHSpend.App.Platform;

internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    internal int Right => X + Width;
    internal int Bottom => Y + Height;
}

internal static class FlyoutPlacement
{
    internal static PixelRect AboveIcon(PixelRect icon, PixelRect work, int width, int height, int gap)
    {
        if (work.Width <= 0 || work.Height <= 0) throw new ArgumentException("The monitor work area must have positive dimensions.", nameof(work));
        gap = Math.Clamp(gap, 0, (Math.Min(work.Width, work.Height) - 1) / 2);
        width = Math.Clamp(width, 1, Math.Max(1, work.Width - gap * 2));
        height = Math.Clamp(height, 1, Math.Max(1, work.Height - gap * 2));
        int left = Math.Clamp(icon.X + icon.Width / 2 - width / 2, work.X + gap, work.Right - width - gap);
        int top = icon.Y - height - gap;
        if (top < work.Y + gap) top = icon.Bottom + gap;
        top = Math.Clamp(top, work.Y + gap, work.Bottom - height - gap);
        return new(left, top, width, height);
    }
}
