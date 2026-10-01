using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using GHCPSpendTray.App.Native;
using GHCPSpendTray.Core;
using Microsoft.UI.Reactor.Wrappers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GHCPSpendTray.App.UI;

// The built-in ImageElement requires a URI; this image owns an in-memory source.
[GenerateReactorWrapper(typeof(Image), AutoDiscover = false)]
internal partial record TrayPreviewElement;

// Owns only a WinUI pixel buffer, never an HICON, GDI bitmap, or Shell registration.
internal sealed class TrayPreviewImage
{
    private readonly Image _image;
    private TrayIndicator _indicator;
    private TrayIconStyle _style;
    private WriteableBitmap? _bitmap;
    private XamlRoot? _root;
    private (TrayIndicator Indicator, TrayIconStyle Style, int Size, TrayPalette Palette)? _rendered;

    private TrayPreviewImage(Image image, TrayIndicator indicator, TrayIconStyle style)
    {
        _image = image; _indicator = indicator; _style = style;
        image.Loaded += Loaded;
        image.Unloaded += Unloaded;
    }

    internal static void Apply(Image image, TrayIndicator indicator, TrayIconStyle style)
    {
        if (image.Tag is not TrayPreviewImage preview)
            image.Tag = preview = new(image, indicator, style);
        preview._indicator = indicator;
        preview._style = style;
        preview.Render();
    }

    private void Loaded(object sender, RoutedEventArgs args)
    {
        if (_root is not null) _root.Changed -= RootChanged;
        _root = _image.XamlRoot;
        if (_root is not null) _root.Changed += RootChanged;
        Render();
    }
    private void Unloaded(object sender, RoutedEventArgs args)
    {
        if (_root is not null) _root.Changed -= RootChanged;
        _root = null;
        _image.Source = null;
        _bitmap = null;
        _rendered = null;
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Render();

    private void Render()
    {
        try
        {
            uint dpi = (uint)Math.Round(96 * (_image.XamlRoot?.RasterizationScale ?? 1));
            int size = TrayIconRenderer.SizeForDpi(dpi);
            var palette = TrayIconRenderer.SystemPalette();
            // A taskbar-colored swatch keeps transparent icons readable when app and taskbar themes differ.
            if (_image.Parent is Border frame)
                frame.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255,
                    (byte)(palette.Background >> 16), (byte)(palette.Background >> 8), (byte)palette.Background));
            var next = (_indicator, _style, size, palette);
            if (_rendered == next)
            {
                _image.Source = _bitmap;
                return;
            }
            uint[] pixels = TrayIconRenderer.Pixels(_indicator, _style, size, palette);
            if (_bitmap is null || _bitmap.PixelWidth != size)
                _bitmap = new(size, size);
            using (var stream = _bitmap.PixelBuffer.AsStream())
                stream.Write(MemoryMarshal.AsBytes(pixels.AsSpan()));
            _bitmap.Invalidate();
            _image.Source = _bitmap;
            _rendered = next;
        }
        catch
        {
            _image.Source = null;
            _bitmap = null;
            _rendered = null;
            throw;
        }
    }
}
