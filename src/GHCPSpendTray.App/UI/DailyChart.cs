using System.Globalization;
using GHCPSpendTray.Core;
using Microsoft.UI.Reactor.Core.V1Protocol;
using Microsoft.UI.Reactor.Core.V1Protocol.Descriptor;
using Microsoft.UI.Reactor.Wrappers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.ViewManagement;

namespace GHCPSpendTray.App.UI;

// The chart owns its drawing children; Reactor must not reconcile them as an empty panel.
[GenerateReactorWrapper(typeof(Canvas), AutoDiscover = false)]
[WrapManual("Children")]
internal partial record DailyChartElement
{
    private static partial ControlDescriptor<DailyChartElement, Canvas> Customize(ControlDescriptor<DailyChartElement, Canvas> d)
    {
        d.Children = new None<DailyChartElement, Canvas>();
        return d;
    }
}

internal sealed class DailyChart
{
    private readonly Canvas _canvas;
    private readonly UISettings _colors = new();
    private DailySpend[] _bars = [];
    private bool _compact;
    private bool _loaded;

    private DailyChart(Canvas canvas)
    {
        _canvas = canvas;
        canvas.SizeChanged += (_, _) => Render();
        canvas.ActualThemeChanged += (_, _) => Render();
        canvas.Loaded += (_, _) =>
        {
            if (!_loaded) _colors.ColorValuesChanged += ColorsChanged;
            _loaded = true;
            Render();
        };
        canvas.Unloaded += (_, _) =>
        {
            if (_loaded) _colors.ColorValuesChanged -= ColorsChanged;
            _loaded = false;
        };
    }

    internal static void Apply(Canvas canvas, DailySpend[] bars, bool compact)
    {
        if (canvas.Tag is not DailyChart chart) canvas.Tag = chart = new(canvas);
        chart._bars = bars;
        chart._compact = compact;
        chart.Render();
    }

    private void ColorsChanged(UISettings sender, object args) => _canvas.DispatcherQueue.TryEnqueue(Render);

    private void Render()
    {
        double width = _canvas.ActualWidth, height = _canvas.Height;
        if (width < 100 || !double.IsFinite(height) || _bars.Length == 0) return;
        _canvas.Children.Clear();
        double left = _compact ? 42 : 64, top = 18, bottom = height - 20;
        double plotHeight = bottom - top, plotWidth = width - left - 4;
        var ticks = DailyChartPresentation.Ticks(_bars);
        decimal maximum = ticks[^1];
        var ink = new SolidColorBrush(_colors.GetColorValue(UIColorType.Foreground));
        var accent = new SolidColorBrush(_colors.GetColorValue(UIColorType.Accent));
        AddText("USD", 0, 0, left - 4, ink);
        foreach (decimal value in ticks)
        {
            double y = bottom - (double)(value / maximum) * plotHeight;
            _canvas.Children.Add(new Line
            {
                X1 = left, X2 = width - 4, Y1 = y, Y2 = y,
                Stroke = ink, StrokeThickness = 1, Opacity = value == 0 ? 0.4 : 0.15
            });
            AddText(value.ToString(maximum < 1 ? "0.00" : "#,0.##", CultureInfo.CurrentCulture),
                0, y - 7, left - 8, ink);
        }
        double cell = plotWidth / _bars.Length;
        for (int i = 0; i < _bars.Length; i++)
        {
            var bar = _bars[i];
            double barHeight = (double)(bar.AmountUsd / maximum) * plotHeight;
            double x = left + i * cell + Math.Min(3, cell * 0.15);
            double barWidth = Math.Max(1, cell - Math.Min(6, cell * 0.3));
            var fill = new Rectangle
            {
                Width = barWidth, Height = Math.Max(barHeight, 1), Fill = accent,
                Opacity = bar.Estimated ? 0.2 : 1
            };
            Place(fill, x, bottom - Math.Max(barHeight, 1));
            if (bar.Estimated)
            {
                var outline = new Rectangle
                {
                    Width = barWidth, Height = Math.Max(barHeight, 2), Stroke = accent,
                    StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false
                };
                Place(outline, x, bottom - Math.Max(barHeight, 2));
            }
            var hitTarget = new Border
            {
                Width = cell, Height = plotHeight, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
            };
            string tooltip = DailyChartPresentation.Tooltip(bar);
            ToolTipService.SetToolTip(hitTarget, tooltip);
            AutomationProperties.SetName(hitTarget, tooltip);
            Place(hitTarget, left + i * cell, top);
            int stride = Math.Max(1, (int)Math.Ceiling(_bars.Length / (_compact ? 6d : 16d)));
            if (i % stride == 0 || i == _bars.Length - 1)
                AddText(DateOnly.ParseExact(bar.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture).Day.ToString(CultureInfo.CurrentCulture),
                    left + i * cell, bottom + 3, cell, ink);
        }
    }

    private void Place(FrameworkElement element, double x, double y)
    {
        Canvas.SetLeft(element, x); Canvas.SetTop(element, y); _canvas.Children.Add(element);
    }

    private void AddText(string text, double x, double y, double width, Brush ink)
    {
        var label = new TextBlock
        {
            Text = text, FontSize = _compact ? 10 : 11, Width = width, Foreground = ink,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTipService.SetToolTip(label, text);
        Place(label, x, y);
    }
}
