using System.Globalization;

namespace GHCPSpendTray.Core;

public static class DailyChartPresentation
{
    public const string LoginCommand = "az login --scope \"2efaf292-00a0-426c-ba7d-f5d2b214b8fc/.default\"";

    public static decimal[] Ticks(IEnumerable<DailySpend> bars)
    {
        decimal max = Math.Max(0.01m, bars.Select(b => b.AmountUsd).DefaultIfEmpty(0).Max());
        decimal step = 0.01m;
        while (step < max / 4)
        {
            decimal next = step * 2;
            if (next >= max / 4) { step = next; break; }
            next = step * 5;
            if (next >= max / 4) { step = next; break; }
            step *= 10;
        }
        int count = (int)decimal.Ceiling(max / step);
        return Enumerable.Range(0, count + 1).Select(i => step >= 1 && i > decimal.MaxValue / step ? max : i * step).ToArray();
    }

    public static string DateLabel(string date, CultureInfo? culture = null) =>
        DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("d", culture ?? CultureInfo.CurrentCulture);

    public static string Kind(DailySpend bar) => bar.Estimated ? "Estimated" : bar.Derived ? "Today - from current quota" : "Reported";
    public static string Tooltip(DailySpend bar) =>
        $"{DateLabel(bar.Date)}: {bar.AmountUsd.ToString("N2", CultureInfo.CurrentCulture)} USD - {Kind(bar)}";
}
