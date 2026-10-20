namespace GHCPSpendTray.Core;

public enum ForecastBand { Under, Near, Over }

public sealed record UsageForecast(double ActualFraction, double ForecastFraction, decimal Percent, ForecastBand Band)
{
    public static UsageForecast? Create(decimal? actual, decimal? target, PeriodEstimate? estimate)
    {
        if (actual is null or < 0 || target is null or <= 0 ||
            estimate is not { UnavailableReason: null, EstimatedConsumptionUsd: { } projected } || projected < actual)
            return null;
        decimal percentage = UsageBudget.Percentage(projected, target.Value);
        return new(Fraction(actual.Value, target.Value), Fraction(projected, target.Value), percentage,
            projected >= target ? ForecastBand.Over : projected >= target * 0.95m ? ForecastBand.Near : ForecastBand.Under);
    }

    private static double Fraction(decimal value, decimal target) => value >= target ? 1 : (double)(value / target);
}
