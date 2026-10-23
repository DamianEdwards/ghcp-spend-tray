using System.Globalization;

namespace GHCPSpendTray.Core;

public static class UsageFormatting
{
    public static string Percentage(decimal value) =>
        decimal.Round(value, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";

    public static string Percentage(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";

    public static string BudgetMoney(decimal value) =>
        "$" + value.ToString(value == decimal.Truncate(value) ? "N0" : "N2", CultureInfo.GetCultureInfo("en-US"));
}
