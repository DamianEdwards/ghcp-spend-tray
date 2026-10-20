using GHCPSpendTray.Core;

namespace GHCPSpendTray.Tests;

internal static partial class Program
{
    private static async Task UsageForecastTests()
    {
        foreach (var (amount, band) in new[]
        {
            (0m, ForecastBand.Under), (94.999m, ForecastBand.Under),
            (95m, ForecastBand.Near), (99.999m, ForecastBand.Near),
            (100m, ForecastBand.Over), (125m, ForecastBand.Over)
        })
            await Test($"forecast track classifies exact thresholds at {amount}", () =>
            {
                var forecast = UsageForecast.Create(0, 100, new(EstimatedConsumptionUsd: amount))!;
                Equal(band, forecast.Band);
                Equal(amount, forecast.Percent);
                Equal((double)(Math.Min(100, amount) / 100), forecast.ForecastFraction);
                Equal(0d, forecast.ActualFraction);
            });
        await Test("forecast extension abuts actual usage and clamps both markers", () =>
        {
            var forecast = UsageForecast.Create(63.34m, 100, new(EstimatedConsumptionUsd: 78.4m))!;
            Equal(0.6334d, forecast.ActualFraction);
            Equal(0.784d, forecast.ForecastFraction);
            var exceeded = UsageForecast.Create(120, 100, new(EstimatedConsumptionUsd: 150))!;
            Equal(1d, exceeded.ActualFraction);
            Equal(1d, exceeded.ForecastFraction);
            Equal(150m, exceeded.Percent);
            True(UsageForecast.Create(0, 0.01m, new(EstimatedConsumptionUsd: decimal.MaxValue))!.Percent == decimal.MaxValue);
        });
        await Test("forecast line falls back for missing target, unavailable estimate, or inconsistent data", () =>
        {
            Equal(null, UsageForecast.Create(10, null, new(EstimatedConsumptionUsd: 20)));
            Equal(null, UsageForecast.Create(10, 0, new(EstimatedConsumptionUsd: 20)));
            Equal(null, UsageForecast.Create(null, 100, new(EstimatedConsumptionUsd: 20)));
            Equal(null, UsageForecast.Create(10, 100, null));
            Equal(null, UsageForecast.Create(10, 100, new(EstimatedConsumptionUsd: 9)));
            Equal(null, UsageForecast.Create(10, 100, new(EstimatedConsumptionUsd: 20, UnavailableReason: "stale")));
        });
    }
}
