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
    private bool _compact;
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

    internal static double Height(AccountView account, bool compact) =>
        (compact ? 54 : 72) + (account.Forecast is { } projected ? projected.Band == ForecastBand.Over ? 65 : 53 : 28);

    internal static void Apply(Canvas canvas, AccountView account, string prefix, bool compact = false)
    {
        if (canvas.Tag is not ForecastTrack track) canvas.Tag = track = new(canvas);
        track._account = account;
        track._prefix = prefix;
        track._compact = compact;
        track.Render();
    }

    private void ColorsChanged(UISettings sender, object args) => _canvas.DispatcherQueue.TryEnqueue(Render);

    private void Render()
    {
        if (_account?.ActualUsage is not { } actual || _canvas.ActualWidth < 60) return;
        _canvas.Children.Clear();
        double lineY = _compact ? 54 : 72;
        var accent = new SolidColorBrush(_colors.GetColorValue(UIColorType.Accent));
        var foreground = new SolidColorBrush(_colors.GetColorValue(UIColorType.Foreground));
        decimal target = (_account.CustomBudgetUsd ?? _account.AllocationUsd)!.Value;
        string targetName = _account.CustomBudgetUsd is not null ? "budget" : "API limit";
        double targetMaxWidth = Math.Min(140, _canvas.ActualWidth * 0.4);
        var targetLabel = new TextBlock
        {
            Text = targetName, FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var targetAmount = new TextBlock
        {
            Text = UI.TrackingTargetAmount(target), FontSize = _compact ? 16 : 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = foreground,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        AutomationProperties.SetAutomationId(targetAmount, _prefix + "TrackingTargetAmount");
        AutomationProperties.SetAutomationId(targetLabel, _prefix + "TrackingTargetLabel");
        AutomationProperties.SetLabeledBy(targetAmount, targetLabel);
        var targetColumn = new StackPanel { MaxWidth = targetMaxWidth };
        targetColumn.Children.Add(targetAmount);
        targetColumn.Children.Add(targetLabel);
        targetColumn.Measure(new Windows.Foundation.Size(targetMaxWidth, double.PositiveInfinity));
        double width = _canvas.ActualWidth - targetColumn.DesiredSize.Width - 12;
        double trackWidth = Math.Max(0, width - 4), actualX = 2 + actual.Fraction * trackWidth;
        Canvas.SetLeft(targetColumn, width + 12);
        Canvas.SetTop(targetColumn, lineY - targetAmount.DesiredSize.Height / 2);
        ToolTipService.SetToolTip(targetColumn, UI.Money(target) + " " + targetName);
        AutomationProperties.SetName(targetColumn, UI.Money(target) + " " + targetName);
        Add(targetColumn, _prefix + "TrackingTarget");
        Add(new Line { X1 = 2, X2 = width - 2, Y1 = lineY, Y2 = lineY, Stroke = foreground, StrokeThickness = 1, Opacity = 0.4 },
            _prefix + "UsageBaseline");
        Add(new Line { X1 = 2, X2 = actualX, Y1 = lineY, Y2 = lineY, Stroke = accent, StrokeThickness = 4 },
            _prefix + "ActualUsageLine");
        var actualMarker = new Line
        {
            X1 = actualX, X2 = actualX, Y1 = lineY - 9, Y2 = lineY + 7,
            Stroke = foreground, StrokeThickness = 1.5
        };
        Canvas.SetZIndex(actualMarker, 1);
        Add(actualMarker, _prefix + "ActualUsageMarker");

        var usedLabel = new TextBlock
        {
            Text = "used", FontSize = 10, Foreground = foreground, HorizontalAlignment = HorizontalAlignment.Center
        };
        AutomationProperties.SetName(usedLabel, "Current usage");
        var usedAmount = new TextBlock
        {
            Text = UI.Money(_account.ConsumptionUsd), FontSize = _compact ? 21 : 36,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = foreground
        };
        AutomationProperties.SetAutomationId(usedAmount, _prefix + "AccountConsumption");
        AutomationProperties.SetLabeledBy(usedAmount, usedLabel);
        var amountRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center
        };
        if (actual.Band != ForecastBand.Under)
        {
            var icon = new FontIcon
            {
                Glyph = actual.Band == ForecastBand.Near ? "\uE7BA" : "\uE711",
                FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = _compact ? 14 : 18,
                Foreground = (Brush)Application.Current.Resources[actual.Band == ForecastBand.Near
                    ? "SystemFillColorCautionBrush" : "SystemFillColorCriticalBrush"],
                VerticalAlignment = VerticalAlignment.Center
            };
            string warning = actual.Band == ForecastBand.Near ? "Current usage is near the target" : "Current usage is at or over the target";
            AutomationProperties.SetName(icon, warning);
            ToolTipService.SetToolTip(icon, warning);
            AutomationProperties.SetAutomationId(icon, _prefix + "ActualUsageWarning");
            amountRow.Children.Add(icon);
        }
        amountRow.Children.Add(usedAmount);
        var currentCallout = new StackPanel { MaxWidth = width };
        currentCallout.Children.Add(amountRow);
        currentCallout.Children.Add(usedLabel);
        currentCallout.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, lineY - 9) };
        ToolTipService.SetToolTip(currentCallout, UI.UsagePercentage(_account));
        AutomationProperties.SetName(currentCallout, $"Current usage: {UI.Money(_account.ConsumptionUsd)}. " + UI.UsagePercentage(_account));
        currentCallout.Measure(new Windows.Foundation.Size(width, lineY - 9));
        Canvas.SetLeft(currentCallout, Math.Clamp(actualX - currentCallout.DesiredSize.Width / 2, 0,
            Math.Max(0, width - currentCallout.DesiredSize.Width)));
        Canvas.SetTop(currentCallout, Math.Max(0, lineY - 11 - currentCallout.DesiredSize.Height));
        Add(currentCallout, _prefix + "ActualUsageCallout");
        AutomationProperties.SetName(_canvas, UI.UsagePercentage(_account));
        if (_account.Forecast is not { } forecast || _account.PeriodEstimate is not { } estimate) return;
        double endX = 2 + forecast.ForecastFraction * trackWidth;
        Brush projectedBrush = forecast.Band switch
        {
            ForecastBand.Near => (Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
            ForecastBand.Over => (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            _ => accent
        };
        Add(new Line
        {
            X1 = actualX, X2 = endX, Y1 = lineY, Y2 = lineY, Stroke = projectedBrush,
            StrokeThickness = 4, StrokeDashArray = new DoubleCollection { 3, 1 }
        }, _prefix + "ForecastExtension");
        Add(new Line { X1 = endX, X2 = endX, Y1 = lineY - 5, Y2 = lineY + 14, Stroke = projectedBrush, StrokeThickness = 1.5 },
            _prefix + "ForecastMarker");

        var label = new TextBlock
        {
            Text = "est. total", FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Center
        };
        AutomationProperties.SetAutomationId(label, _prefix + "PeriodEstimateLabel");
        AutomationProperties.SetName(label, "Estimated at reset");
        var amount = new TextBlock
        {
            Text = UI.EstimateAmount(estimate), FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = foreground, HorizontalAlignment = HorizontalAlignment.Center
        };
        AutomationProperties.SetAutomationId(amount, _prefix + "PeriodEstimateAmount");
        AutomationProperties.SetLabeledBy(amount, label);
        var callout = new StackPanel { Spacing = 0, MaxWidth = width };
        callout.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, 48) };
        callout.Children.Add(amount);
        callout.Children.Add(label);
        if (forecast.Band == ForecastBand.Over)
            callout.Children.Add(new TextBlock
            {
                Text = "(" + forecast.Percent.ToString("0.#", CultureInfo.CurrentCulture) + "%)",
                FontSize = 10, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        string status = forecast.Band switch { ForecastBand.Near => "Near target", ForecastBand.Over => "At or over target", _ => "Under target" };
        string tooltip = $"Estimated at reset: {UI.EstimateAmount(estimate)} ({forecast.Percent:0.##}% of " +
            (_account.CustomBudgetUsd is not null ? "custom budget" : "limit") + $"). {status}. " + UI.EstimateMethodFor(estimate);
        ToolTipService.SetToolTip(callout, tooltip);
        AutomationProperties.SetName(callout, tooltip);
        callout.Measure(new Windows.Foundation.Size(width, 48));
        Canvas.SetLeft(callout, Math.Clamp(endX - callout.DesiredSize.Width / 2, 0, Math.Max(0, width - callout.DesiredSize.Width)));
        Canvas.SetTop(callout, lineY + 17);
        Add(callout, _prefix + "ForecastCallout");
        AutomationProperties.SetName(_canvas, UI.UsagePercentage(_account) + ". " + tooltip);
    }

    private void Add(UIElement element, string? id = null)
    {
        if (id is not null) AutomationProperties.SetAutomationId(element, id);
        _canvas.Children.Add(element);
    }
}
