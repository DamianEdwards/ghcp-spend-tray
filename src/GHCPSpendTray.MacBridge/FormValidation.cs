using System.Globalization;
using GHCPSpendTray.Core;
using GHCPSpendTray.Shared;

namespace GHCPSpendTray.MacBridge;

internal static class FormValidation
{
    internal static Receipt Validate(FormInput input)
    {
        var errors = new Dictionary<string, string>();
        decimal? increment = null, budget = null;
        int? minutes = null;
        if (input.Page == "account")
        {
            string name = input.DisplayName.Trim();
            if (name.Length > 128 || name.Any(char.IsControl))
                errors["name"] = "Use at most 128 characters with no control characters.";
            Thresholds(input.Thresholds, inherit: true);
            if (!input.InheritIncrement) increment = Amount("increment", input.Increment, budget: false);
            if (input.UseCustomBudget) budget = Amount("budget", input.CustomBudget, budget: true);
        }
        else if (input.Page is "general" or "notifications")
        {
            if (input.Page == "general")
            {
                if (int.TryParse(input.PollMinutes, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) &&
                    value is >= 5 and <= 1440) minutes = value;
                else errors["minutes"] = "Enter a refresh interval from 5 through 1440 minutes.";
            }
            else
            {
                Thresholds(input.Thresholds, inherit: false);
                increment = Amount("increment", input.Increment, budget: false);
            }
        }
        else throw new AppOperationException("Unknown preferences form.");
        return new(FieldErrors: errors, IncrementUsd: increment, CustomBudgetUsd: budget, PollMinutes: minutes);

        void Thresholds(string text, bool inherit)
        {
            if (inherit && string.IsNullOrWhiteSpace(text)) return;
            try { _ = ApplicationController.ParseThresholds(text); }
            catch (Exception ex) when (ex is AppOperationException or ArgumentException)
            {
                errors["thresholds"] = ex.Message;
            }
        }
        decimal? Amount(string field, string text, bool budget)
        {
            if (!budget && string.IsNullOrWhiteSpace(text)) return null;
            if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal amount) &&
                (budget ? amount > 0 : amount >= 0) && decimal.Round(amount, 2) == amount) return amount;
            errors[field] = budget
                ? "Enter a budget greater than $0 with at most two decimal places, such as 500 or 12.50."
                : "Enter a USD increment with at most two decimal places, such as 50 or 12.50. Use 0 to disable.";
            return null;
        }
    }
}
