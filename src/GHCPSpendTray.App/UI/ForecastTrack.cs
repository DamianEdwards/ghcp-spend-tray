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

[GenerateReactorWrapper(typeof(Canvas), AutoDiscover = false)]
[WrapManual("Children")]
internal partial record ForecastTrackElement
{
    private static partial ControlDescriptor<ForecastTrackElement, Canvas> Customize(ControlDescriptor<ForecastTrackElement, Canvas> d)
    {
        d.Children = new None<ForecastTrackElement, Canvas>();
        return d;
    }
}

internal sealed class ForecastTrack
{
    private readonly Canvas _canvas;
    private readonly UISettings _colors = new();
    private AccountView? _account;
    private string _prefix = "";
    private bool _loaded;

    private ForecastTrack(Canvas canvas)
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

    internal static void Apply(Canvas canvas, AccountView account, string prefix)
    {
        if (canvas.Tag is not ForecastTrack track) canvas.Tag = track = new(canvas);
        track._account = account;
        track._prefix = prefix;
        track.Render();
    }

    private void ColorsChanged(UISettings sender, object args) => _canvas.DispatcherQueue.TryEnqueue(Render);

    private void Render()
    {
        if (_account?.Forecast is not { } forecast || _account.PeriodEstimate is not { } estimate ||
            _canvas.ActualWidth < 60) return;
        _canvas.Children.Clear();
        double width = _canvas.ActualWidth;
        double trackWidth = width - 4, actualX = 2 + forecast.ActualFraction * trackWidth;
        double endX = 2 + forecast.ForecastFraction * trackWidth;
        var accent = new SolidColorBrush(_colors.GetColorValue(UIColorType.Accent));
        var foreground = new SolidColorBrush(_colors.GetColorValue(UIColorType.Foreground));
        Brush projectedBrush = forecast.Band switch
        {
            ForecastBand.Near => (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
            ForecastBand.Over => (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            _ => accent
        };
        Add(new Line { X1 = 2, X2 = width - 2, Y1 = 5, Y2 = 5, Stroke = foreground, StrokeThickness = 1, Opacity = 0.4 });
        Add(new Line { X1 = 2, X2 = actualX, Y1 = 5, Y2 = 5, Stroke = accent, StrokeThickness = 4 });
        Add(new Line
        {
            X1 = actualX, X2 = endX, Y1 = 5, Y2 = 5, Stroke = projectedBrush,
            StrokeThickness = 4, StrokeDashArray = new DoubleCollection { 2, 2 }
        }, _prefix + "ForecastExtension");
        Add(new Line { X1 = endX, X2 = endX, Y1 = 0, Y2 = 19, Stroke = projectedBrush, StrokeThickness = 1.5 },
            _prefix + "ForecastMarker");

        var label = new TextBlock { Text = "Est.", FontSize = 12, Foreground = projectedBrush };
        AutomationProperties.SetAutomationId(label, _prefix + "PeriodEstimateLabel");
        AutomationProperties.SetName(label, "Estimated at reset");
        var amount = new TextBlock
        {
            Text = UI.EstimateAmount(estimate), FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = projectedBrush
        };
        AutomationProperties.SetAutomationId(amount, _prefix + "PeriodEstimateAmount");
        AutomationProperties.SetLabeledBy(amount, label);
        var callout = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, MaxWidth = width };
        callout.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, 26) };
        callout.Children.Add(label);
        callout.Children.Add(amount);
        if (forecast.Band == ForecastBand.Over)
            callout.Children.Add(new TextBlock
            {
                Text = "(" + forecast.Percent.ToString("0.#", CultureInfo.CurrentCulture) + "%)",
                FontSize = 12, Foreground = projectedBrush, TextTrimming = TextTrimming.CharacterEllipsis
            });
        string status = forecast.Band switch { ForecastBand.Near => "Near target", ForecastBand.Over => "At or over target", _ => "Under target" };
        string tooltip = $"Estimated at reset: {UI.EstimateAmount(estimate)} ({forecast.Percent:0.##}% of " +
            (_account.CustomBudgetUsd is not null ? "custom budget" : "limit") + $"). {status}. " + UI.EstimateMethodFor(estimate);
        ToolTipService.SetToolTip(callout, tooltip);
        AutomationProperties.SetName(callout, tooltip);
        callout.Measure(new Windows.Foundation.Size(width, 28));
        Canvas.SetLeft(callout, Math.Clamp(endX - callout.DesiredSize.Width / 2, 0, Math.Max(0, width - callout.DesiredSize.Width)));
        Canvas.SetTop(callout, 22);
        Add(callout, _prefix + "ForecastCallout");
        AutomationProperties.SetName(_canvas, UI.UsagePercentage(_account) + ". " + tooltip);
    }

    private void Add(UIElement element, string? id = null)
    {
        if (id is not null) AutomationProperties.SetAutomationId(element, id);
        _canvas.Children.Add(element);
    }
}
