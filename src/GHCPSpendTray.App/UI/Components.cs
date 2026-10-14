using System.Globalization;
using System.Reflection;
using GHCPSpendTray.App.Native;
using GHCPSpendTray.Core;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace GHCPSpendTray.App.UI;

// A cost-first tray surface and task-oriented settings, using the user's Windows theme and native Fluent controls.
internal static class UI
{
    internal static string Version => typeof(UI).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+', 2)[0] ?? throw new InvalidOperationException("App version is missing.");
    internal static string Money(decimal? amount) => amount is { } value ?
        "$" + value.ToString("N2", CultureInfo.GetCultureInfo("en-US")) : "Unavailable";
    internal static string UsagePercentage(AccountView account) =>
        $"{account.Percent:0.##}% of {Money(account.AllocationUsd)} " +
        (account.CustomBudgetUsd is not null ? "custom budget" : "allocation");
    internal static TextBlockElement Copy(string text) => TextBlock(text).TextWrapping().Foreground(Theme.SecondaryText);
    internal static Element SettingsInput(AppSession session, SettingsField field, string value, string label,
        string? placeholder = null, double? width = null, bool enabled = true)
    {
        string? error = session.FieldError(field);
        var input = TextBox(value, text => session.SetInput(field, text), placeholder)
            .AutomationName(label).AutomationId(field.ToString()).IsEnabled(enabled && !session.Busy)
            .HelpText(error ?? "");
        var content = VStack(4,
            Border(input).CornerRadius(4).BorderThickness(error is null ? 0 : 2)
                .BorderBrush(Theme.Ref("SystemFillColorCriticalBrush")).AutomationId(field + "ValidationBorder"),
            error is not null
                ? TextBlock(error).TextWrapping().FontSize(12).Foreground(Theme.Ref("SystemFillColorCriticalBrush"))
                    .AutomationId(field + "Error").AutomationName(label + " error: " + error)
                : null);
        return width is { } size ? content.Width(size).HAlign(HorizontalAlignment.Left) : content;
    }
    internal static Element? ValidationFeedback(AppSession session) => session.HasFieldErrors
        ? InfoBar("Check your inputs", "Correct the highlighted fields before saving.").Error().IsClosable(false)
            .AutomationId("SettingsValidationFeedback")
        : null;
    internal static Element Logo(double size) => Image(Path.Combine(AppContext.BaseDirectory, "Assets", "ghcpspendtray-logo.png"))
        .Width(size).Height(size).AutomationName("GHCPSpendTray");
    internal static Element AccountPicture(AccountView account, double size) =>
        AccountPicture(account.Login, account.AvatarUrl, account.Key, size);
    internal static Element AccountPicture(string login, string? avatarPath, string key, double size) =>
        (PersonPicture().DisplayName(login) with { ProfilePicture = avatarPath })
            .Width(size).Height(size).AutomationName($"Account {login}")
            .AutomationId("AccountAvatar-" + key);
    internal static ButtonElement Glyph(string glyph, string label, Action action) =>
        Button(Icon(FontIcon(glyph, "Segoe Fluent Icons", 18)), action)
            .Width(40).Height(40).Padding(0).AutomationName(label).ToolTip(label);
    internal static Element Section(string title, string description, Element control) =>
        Card(Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            VStack(5, TextBlock(title).SemiBold(), Copy(description)).Grid(column: 0).Margin(0, 0, 24, 0),
            control.Grid(column: 1).VAlign(VerticalAlignment.Center)));
    internal static Element? Feedback(AppSession state, bool showNotice = true) => state.HasValidationError ? null : state.Error is { } error
        ? InfoBar("Unable to complete", error).Error().IsClosable(false)
        : showNotice && state.Notice is { } notice
            ? state.NoticeIsSuccess
                ? InfoBar("Complete!", notice).Success().IsClosable(false).AutomationId("AccountConnectionComplete")
                : InfoBar("", notice).Informational().IsClosable(false)
            : null;
    internal static Element? UpdateNotice(AppSession state, bool onAbout = false) => state.StoreUpdates is { HasUpdate: true }
        && !onAbout
        ? Card(VStack(8, Copy("A GHCPSpendTray update is available."),
            Button("View update", () => state.OpenSettings?.Invoke(SettingsPage.About))
                .AutomationId("ViewStoreUpdate").HAlign(HorizontalAlignment.Left)))
        : null;
    internal static Element StoreUpdateCard(StoreUpdateSession updates, nint owner) => Card(VStack(12,
        TextBlock("Microsoft Store updates").SemiBold(),
        Copy(updates.Status).AutomationId("StoreUpdateStatus"),
        updates.Error is { } error ? InfoBar("Unable to complete update", error).Error().IsClosable(false) : null,
        updates.Updating ? Progress(updates.Progress).AutomationName("App update progress") : null,
        updates.HasUpdate
            ? VStack(8,
                Copy("Windows will ask permission to download and install. The app may close and restart."),
                Button(updates.RestartRequired ? "Restart" : "Update", () => updates.Install(owner))
                    .AccentButton().AutomationName(updates.RestartRequired ? "Restart app" : "Install app update")
                    .AutomationId("InstallStoreUpdate").HAlign(HorizontalAlignment.Left)
                    .IsEnabled(!updates.Checking && !updates.Updating))
            : updates.Error is not null
                ? Button("Try again", () => updates.Check(force: true)).HAlign(HorizontalAlignment.Left)
                    .IsEnabled(!updates.Checking)
                : null
    )).AutomationId("StoreUpdateCard");
    internal static Element DeviceSignIn(AppSession state, DevicePrompt device, nint owner)
    {
        int remaining = Math.Max(0, (int)(device.Expires - DateTimeOffset.UtcNow).TotalSeconds);
        return Card(VStack(16,
            FlexRow(
                TextBlock($"Signing in to {device.VerificationUri.Host}").FontSize(16).SemiBold()
                    .TextWrapping().AutomationId("ActiveSignInHost").Flex(shrink: 1),
                state.ReconnectKey is null
                    ? Button("Change host", state.ChangeHost).AutomationId("ChangeSignInHost").Flex(shrink: 0)
                    : null) with
            {
                Wrap = FlexWrap.Wrap, ColumnGap = 12, RowGap = 8, AlignItems = FlexAlign.Center
            },
            TextBlock("Enter this code in your browser").FontSize(20).SemiBold().TextWrapping(),
            TextBlock(device.Code).FontSize(32).SemiBold().IsTextSelectionEnabled()
                .AutomationName("Device authorization code").AutomationId("DeviceCode"),
            state.ClipboardError is { } error
                ? InfoBar("Copy the code", error).Warning().IsClosable(false).AutomationId("CodeCopyFeedback")
                : state.CodeCopied
                    ? InfoBar("", "Code copied to clipboard.").Success().IsClosable(false).AutomationId("CodeCopyFeedback")
                    : null,
            FlexRow(
                Button("Open browser", () => state.OpenSignInBrowser(owner))
                    .AccentButton().AutomationId("OpenSignInBrowser").Flex(shrink: 0),
                Button("Copy code", state.CopyCode).AutomationId("CopySignInCode").Flex(shrink: 0)) with
            {
                Wrap = FlexWrap.Wrap, ColumnGap = 10, RowGap = 8, AlignItems = FlexAlign.FlexStart
            },
            Copy($"Waiting for authorization. Code expires in {remaining} seconds.").FontSize(12)
        )).AutomationId("DeviceSignIn");
    }
    internal static string? AccountWarning(AccountView account)
    {
        if (account.Freshness == "Fresh") return account.Details.Message;
        return string.IsNullOrWhiteSpace(account.Details.Message) ? account.Freshness :
            $"{account.Freshness}: {account.Details.Message}";
    }
    internal static Element DetailRow(string label, string value, string id) =>
        Grid([GridSize.Px(140), GridSize.Star()], [GridSize.Auto],
            Copy(label).Grid(column: 0).Margin(0, 0, 16, 0).AutomationId(id + "Label"),
            TextBlock(value).TextWrapping().IsTextSelectionEnabled().Grid(column: 1)
                .AutomationId(id).LabeledBy(id + "Label"));
    internal static string Timestamp(DateTimeOffset? value, string missing = "Not available") =>
        value?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? missing;
    internal const string EstimateDescription =
        "Estimate consumption at the end of the UTC calendar month using your average pace so far. Actual consumption may differ.";
    internal const string EstimateMethod =
        "Period-to-date consumption divided by exact elapsed UTC time, scaled to the whole UTC calendar month. " +
        "Uses the source observation time, or fetch time when not supplied, not when this account was connected. " +
        "Available after 24 hours; qualified as an Early estimate before 72 hours. " +
        "AI-credit consumption value, not an invoice.";
    internal static string ApproximateMoney(decimal value) =>
        value is > 0 and < 1 ? "<$1" :
            "~$" + decimal.Round(value, 0, MidpointRounding.AwayFromZero).ToString("N0", CultureInfo.GetCultureInfo("en-US"));
    internal static string UtcTimestamp(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("MMM d, yyyy HH:mm:ss 'UTC'", CultureInfo.CurrentCulture) ?? "Not available";
    internal static string EstimateAmount(PeriodEstimate estimate) =>
        estimate.EstimatedConsumptionUsd is { } amount ? ApproximateMoney(amount) : "Unavailable";
    internal static string EstimateContext(PeriodEstimate estimate, bool customBudget = false)
    {
        if (estimate.UnavailableReason is { } reason) return reason;
        var parts = new List<string>();
        if (estimate.IsEarly) parts.Add("Early estimate");
        if (estimate.OverAllocationUsd is > 0 and < 1)
            parts.Add("Less than $1 over " + (customBudget ? "custom budget" : "allocation"));
        else if (estimate.OverAllocationUsd is { } over && over > 0)
            parts.Add($"About ${decimal.Round(over, 0, MidpointRounding.AwayFromZero).ToString("N0", CultureInfo.GetCultureInfo("en-US"))} over " +
                (customBudget ? "custom budget" : "allocation"));
        if (estimate.ResetAtUtc is { } boundary)
            parts.Add($"Resets {boundary.ToUniversalTime().ToString("MMM d, yyyy 'UTC'", CultureInfo.CurrentCulture)}");
        return string.Join(" \u00B7 ", parts);
    }
    internal static Element? EstimateRow(AccountView account, string idPrefix, bool rightAlignAmount = false) =>
        account.PeriodEstimate is { } estimate
            ? VStack(3,
                Grid([rightAlignAmount ? GridSize.Star() : GridSize.Auto, GridSize.Auto], [GridSize.Auto],
                    Copy("Estimated at reset").FontSize(12).Grid(column: 0).Margin(0, 0, 12, 0)
                        .AutomationId(idPrefix + "PeriodEstimateLabel"),
                    Copy(EstimateAmount(estimate)).FontSize(12).Grid(column: 1).HAlign(HorizontalAlignment.Right)
                        .AutomationId(idPrefix + "PeriodEstimateAmount").LabeledBy(idPrefix + "PeriodEstimateLabel")),
                Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                    estimate.OverAllocationUsd is > 0
                        ? Icon(FontIcon("\uE7BA", "Segoe Fluent Icons", 12))
                            .AutomationName(account.CustomBudgetUsd is not null
                                ? "Projected over custom budget, not observed usage" : "Projected over allocation, not observed usage")
                            .AutomationId(idPrefix + "PeriodEstimateWarning").Grid(column: 0).Margin(0, 0, 6, 0) : null,
                    Copy(EstimateContext(estimate, account.CustomBudgetUsd is not null)).FontSize(12)
                        .Foreground(estimate.OverAllocationUsd is > 0
                            ? Theme.Ref("SystemFillColorCautionBrush") : Theme.SecondaryText)
                        .AutomationId(idPrefix + "PeriodEstimateContext").Grid(column: 1))
            ).ToolTip(EstimateDescription + " " + EstimateMethod).AutomationId(idPrefix + "PeriodEstimate")
            : null;
    internal static Element? EstimateDetails(AccountView account, string idPrefix = "") => account.PeriodEstimate is { } estimate
        ? VStack(12,
            DetailRow("Estimate method", EstimateMethod, idPrefix + "PeriodEstimateMethod"),
            DetailRow("Estimated at reset", EstimateAmount(estimate), idPrefix + "PeriodEstimateDetailAmount"),
            DetailRow("Average per day", estimate.AverageDailyConsumptionUsd is { } daily
                ? ApproximateMoney(daily) : "Unavailable", idPrefix + "PeriodEstimateDaily"),
            DetailRow("Period starts", UtcTimestamp(estimate.PeriodStartUtc), idPrefix + "PeriodEstimateStart"),
            DetailRow("Period resets", UtcTimestamp(estimate.ResetAtUtc), idPrefix + "PeriodEstimateReset"),
            DetailRow("Observation time", UtcTimestamp(estimate.ObservedAtUtc), idPrefix + "PeriodEstimateObserved"),
            estimate.UnavailableReason is { } reason
                ? DetailRow("Estimate unavailable", reason, idPrefix + "PeriodEstimateUnavailable") : null
        ).AutomationId(idPrefix + "PeriodEstimateDisclosure")
        : null;
}

internal abstract class SessionComponent(AppSession session) : Component
{
    protected AppSession Session { get; } = session;
    internal int RenderedRevision { get; private set; } = -1;
    protected void UseSession()
    {
        var (_, setRevision) = UseState(Session.Revision);
        UseEffect(() =>
        {
            void Changed() => setRevision(Session.Revision);
            Session.Changed += Changed;
            return () => Session.Changed -= Changed;
        }, Session);
        int revision = Session.Revision;
        // Reactor flushes effects before reconciling children. Publish the marker on
        // the next dispatcher turn, after that reconciliation has committed.
        UseEffect(() => Session.Post(() => RenderedRevision = revision), revision);
    }
}

internal sealed class FlyoutComponent(AppSession session) : SessionComponent(session)
{
    public override Element Render()
    {
        UseSession();
        var model = Session.Dashboard;
        Element body;
        if (!Session.Initialized)
            body = Session.Error is null
                ? VStack(20, ProgressRing(), UI.Copy("Loading your accounts...")).Padding(32)
                : Button("Try loading again", Session.Initialize).IsEnabled(!Session.Busy);
        else if (model.Accounts.Count == 0)
            body = VStack(18,
                Button(VStack(12, UI.Logo(76), TextBlock("Connect your GitHub account").FontSize(20).SemiBold()),
                    Session.AddAccount).Padding(24).AutomationName("Add your first GitHub account")
                    .AutomationId("AddFirstAccount").HAlign(HorizontalAlignment.Center),
                UI.Copy("Your Copilot consumption, one glance away. Connect an account to start tracking.")
                    .TextAlignment(TextAlignment.Center)).Padding(8, 36);
        else
            body = VStack(18,
                VStack(3,
                    UI.Copy("This month's consumption"),
                    TextBlock(UI.Money(model.ConsumptionUsd)).FontSize(46).SemiBold().AutomationId("TotalConsumption"),
                    UI.Copy(model.IsComplete ? $"{model.Accounts.Count} connected account(s)" :
                        model.IsLastKnown ? "Last-known / partial total" : "Partial total - some accounts unavailable")
                ),
                VStack(10, model.Accounts.Select(AccountCard).ToArray())
            ).Padding(0, 8);
        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Star(), GridSize.Auto],
            Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                UI.Logo(28).Grid(column: 0).Margin(0, 0, 10, 0),
                TextBlock("GHCPSpendTray").FontSize(18).SemiBold().VAlign(VerticalAlignment.Center).Grid(column: 1),
                UI.Glyph("\uE713", "Settings", () => Session.OpenSettings?.Invoke(SettingsPage.Usage))
                    .AutomationId("OpenSettings").Grid(column: 2)
            ).Grid(row: 0).Padding(20, 14),
            ScrollView(VStack(12, UI.Feedback(Session, showNotice: false), UI.UpdateNotice(Session), body))
                .Grid(row: 1).Padding(20, 0),
            Border(VStack(10,
                Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                    UI.Copy(Session.Busy ? "Refreshing..." : LastUpdated(model)).FontSize(12).Grid(column: 0).VAlign(VerticalAlignment.Center),
                    UI.Glyph("\uE72C", "Refresh consumption", () => Session.Refresh()).Grid(column: 1)
                        .IsEnabled(Session.Initialized && !Session.Busy)),
                UI.Copy("AI-credit consumption value, not an invoice.").FontSize(12)
            )).Background(Theme.CardBackground).Padding(20, 12).Grid(row: 2)
        ).Background(Theme.SolidBackground).AutomationId("SpendFlyout");
    }
    private Element AccountCard(AccountView account) =>
        Card(VStack(10,
            Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                UI.AccountPicture(account, 36).Grid(column: 0).Margin(0, 0, 10, 0),
                VStack(2, TextBlock(account.Name).SemiBold(), UI.Copy(account.Host).FontSize(12)).Grid(column: 1),
                TextBlock(UI.Money(account.ConsumptionUsd)).FontSize(21).SemiBold()
                    .VAlign(VerticalAlignment.Center).Grid(column: 2)),
            account.Percent is { } percent
                ? VStack(5, Progress((double)Math.Clamp(percent, 0, 100)).AutomationName(UI.UsagePercentage(account)),
                    UI.Copy(UI.UsagePercentage(account)).FontSize(12))
                : UI.Copy(account.Details.Unlimited ? "Unlimited allocation" : "Allocation percentage not available").FontSize(12),
            UI.EstimateRow(account, account.Key + "_Flyout_", rightAlignAmount: true),
            Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                UI.Copy(account.Freshness).FontSize(12).VAlign(VerticalAlignment.Center).Grid(column: 0),
                Button("Details", () => Session.EditAccount(account.Key)).AutomationName($"Details for {account.Login}")
                    .Grid(column: 1))
        )).WithKey(account.Key);
    private static string LastUpdated(DashboardView model)
    {
        var updates = model.Accounts.Where(a => a.UpdatedAt.HasValue).Select(a => a.UpdatedAt!.Value).ToArray();
        return updates.Length == 0 ? "No observations yet" : $"Oldest update {updates.Min().ToLocalTime():t}";
    }
}

internal sealed class SettingsComponent(AppSession session) : SessionComponent(session)
{
    public override Element Render()
    {
        UseSession();
        var owner = UseWindow();
        nint hwnd = owner is null ? 0 : WinRT.Interop.WindowNative.GetWindowHandle(owner.NativeWindow);
        Element content = Session.Page switch
        {
            SettingsPage.Usage => Usage(),
            SettingsPage.Accounts => Accounts(hwnd),
            SettingsPage.Notifications => Notifications(),
            SettingsPage.About => About(hwnd),
            _ => General(hwnd)
        };
        var navigation = NavigationView(
            [
                NavItem("Usage", "Home", "Usage"),
                NavItem("General", "Setting", "General"),
                NavItem("Accounts", "Contact", "Accounts"),
                NavItem("Notifications", "Message", "Notifications"),
                NavItem("About", "Help", "About")
            ], Grid([GridSize.Star()], [GridSize.Star(), GridSize.Auto],
                ScrollView(VStack(22,
                    TextBlock(Session.Page.ToString()).FontSize(30).SemiBold(),
                    Session.HasEditableForm ? null : UI.Feedback(Session),
                    UI.UpdateNotice(Session, onAbout: Session.Page == SettingsPage.About),
                    content.WithKey("SettingsPage-" + Session.Page)
                ).Padding(30, 24)).AutomationId("SettingsScroll").Grid(row: 0),
                Session.HasEditableForm ? FormActions().Grid(row: 1) : null))
            with { SelectedTag = Session.Page.ToString(), IsSettingsVisible = false };
        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Star()],
            TitleBar("GHCPSpendTray Settings").Grid(row: 0),
            navigation.SelectedTagChanged(tag =>
            {
                if (Enum.TryParse<SettingsPage>(tag, out var page) && page != Session.Page) Session.Navigate(page);
            }).OpenPaneLength(230).PaneDisplayMode(NavigationViewPaneDisplayMode.Auto)
                .BackButtonVisible(false).Grid(row: 1),
            (ContentDialog("Save your changes?",
                UI.Copy("You have unsaved settings. Save them before leaving, discard them, or keep editing."), "Save") with
            {
                IsOpen = Session.HasPendingNavigation,
                SecondaryButtonText = "Discard",
                CloseButtonText = "Keep editing",
                DefaultButton = ContentDialogButton.Close
            }).IsPrimaryButtonEnabled(!Session.Busy)
                .Closed(result => Session.ResolveUnsavedChanges(result switch
                {
                    ContentDialogResult.Primary => UnsavedChangesChoice.Save,
                    ContentDialogResult.Secondary => UnsavedChangesChoice.Discard,
                    _ => UnsavedChangesChoice.KeepEditing
                })).AutomationId("UnsavedChangesDialog").Grid(row: 1)
        ).AutomationId("SettingsRoot");
    }
    private Element FormActions() => Border(VStack(8,
        UI.ValidationFeedback(Session),
        UI.Feedback(Session),
        Grid([GridSize.Star(), GridSize.Auto, GridSize.Auto], [GridSize.Auto],
            UI.Copy(Session.Saving ? "Saving changes..." : Session.HasUnsavedChanges ? "Unsaved changes" : "All changes saved")
                .VAlign(VerticalAlignment.Center).AutomationId("SettingsDraftStatus").Grid(column: 0).Margin(0, 0, 12, 0),
            Button(Session.Page == SettingsPage.Accounts ? "Save account" : "Save changes",
                    () => { if (Session.Page == SettingsPage.Accounts) Session.SaveAccount(); else Session.SaveGlobal(); })
                .AutomationName(Session.Page == SettingsPage.Accounts ? "Save account" : "Save changes")
                .AccentButton().AutomationId(Session.Page == SettingsPage.Accounts ? "SaveAccount" :
                    Session.Page == SettingsPage.General ? "SaveGeneralSettings" : "SaveNotificationSettings")
                .IsEnabled(!Session.Busy && Session.HasUnsavedChanges).Grid(column: 1).Margin(0, 0, 10, 0),
            Button("Cancel", Session.CancelChanges).AutomationId("CancelSettingsChanges")
                .IsEnabled(!Session.Busy && Session.HasUnsavedChanges).Grid(column: 2))
    ).Padding(30, 14)).Background(Theme.Ref("LayerFillColorDefaultBrush"))
        .BorderBrush(Theme.Ref("DividerStrokeColorDefaultBrush")).BorderThickness(0, 1, 0, 0)
        .AutomationId("SettingsFormActions");
    private Element Usage()
    {
        var model = Session.Dashboard;
        if (!Session.Initialized)
            return VStack(16,
                Session.Error is null
                    ? HStack(12, ProgressRing().Width(24).Height(24), UI.Copy("Loading your accounts..."))
                    : Button("Try loading again", Session.Initialize).HAlign(HorizontalAlignment.Left)
            ).AutomationId("UsagePage");
        if (model.Accounts.Count == 0)
            return VStack(16,
                TextBlock("Add an account to see your usage").FontSize(22).SemiBold().TextWrapping()
                    .AutomationId("UsageEmptyMessage"),
                UI.Copy("Connect your GitHub account to start tracking Copilot consumption."),
                Button(HStack(8, Icon("Add"), TextBlock("Add account")), Session.AddAccount)
                    .AccentButton().AutomationName("Add account").AutomationId("UsageAddAccount")
                    .HAlign(HorizontalAlignment.Left)
            ).Padding(0, 16).AutomationId("UsagePage");
        return VStack(18,
            Card(VStack(10,
                UI.Copy("This month's consumption"),
                TextBlock(UI.Money(model.ConsumptionUsd)).FontSize(36).SemiBold().AutomationId("UsageTotal"),
                UI.Copy(model.IsComplete ? $"{model.Accounts.Count} connected account(s)" :
                    model.IsLastKnown ? "Last-known / partial total" : "Partial total - some accounts unavailable"),
                UI.Copy(model.Status).FontSize(12),
                UI.Copy((model.Tray ?? TrayPresentation.Unavailable).RollUp.Details)
                    .AutomationId("TrayUsageDetails"),
                UI.Copy("AI-credit consumption value, not an invoice.").FontSize(12),
                Button(Session.Busy ? "Refreshing..." : "Refresh consumption", () => Session.Refresh())
                    .AutomationName("Refresh consumption").AutomationId("RefreshUsage").HAlign(HorizontalAlignment.Left)
                    .IsEnabled(Session.Initialized && !Session.Busy))),
            VStack(16, model.Accounts.Select(UsageAccount).ToArray())
        ).AutomationId("UsagePage");
    }
    private Element UsageAccount(AccountView account) => Card(VStack(12,
        Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            UI.AccountPicture(account, 44).Grid(column: 0).Margin(0, 0, 12, 0),
            VStack(2, TextBlock(account.Name).FontSize(20).SemiBold(),
                UI.Copy($"{account.Login} - {account.Host}").FontSize(12)).Grid(column: 1),
            Button("Manage account", () => Session.EditAccount(account.Key))
                .AutomationName($"Manage {account.Login} on {account.Host}")
                .Grid(column: 2).VAlign(VerticalAlignment.Center)),
        UI.AccountWarning(account) is { Length: > 0 } warning
            ? InfoBar("Account needs attention", warning).Warning().IsClosable(false) : null,
        UsageSummary(account, account.Key + "_"),
        UsageAdvancedDetails(account)
    )).WithKey(account.Key);
    private Element UsageAdvancedDetails(AccountView account)
    {
        bool expanded = Session.ExpandedUsageAccounts.Contains(account.Key);
        return Expander("Advanced information", expanded ? AdvancedDetails(account, account.Key + "_") : VStack(),
            expanded, value =>
            {
                if (value) Session.ExpandedUsageAccounts.Add(account.Key);
                else Session.ExpandedUsageAccounts.Remove(account.Key);
                Session.Notify();
            }).HAlign(HorizontalAlignment.Stretch).AutomationId(account.Key + "_AdvancedAccountDetails");
    }
    private Element General(nint owner) => VStack(18,
        UI.Section("Start with Windows", Session.Controller.Settings.StartupDescription,
            ToggleSwitch(Session.Startup, value => { Session.Startup = value; Session.Notify(); })
                .AutomationName("Start with Windows").IsEnabled(Session.Controller.Settings.CanChangeStartup && !Session.Busy)),
        Button("Open Windows startup settings", () => Session.OpenLink("ms-settings:startupapps", owner))
            .IsEnabled(!Session.Controller.Portable),
        Card(VStack(10, TextBlock("Refresh interval").SemiBold(),
            UI.Copy("Check each account every 5 to 1440 minutes. The default is 10 minutes."),
            UI.SettingsInput(Session, SettingsField.PollMinutes, Session.PollMinutes, "Refresh interval in minutes", width: 180))),
        Card(VStack(12,
            TextBlock("System tray").SemiBold(),
            UI.Copy("Show fresh usage against each account's custom budget or API allocation, independently of the dollar totals. New accounts are included by default."),
            TextBlock("Icon style"),
            ComboBox(["Pie chart", "Percentage number"], (int)Session.TrayStyle,
                index => { if (index >= 0) { Session.TrayStyle = (TrayIconStyle)index; Session.Notify(); } })
                .AutomationName("Tray icon style").AutomationId("TrayStyle").IsEnabled(!Session.Busy),
            TextBlock("Icons to show"),
            ComboBox(["One roll-up icon", "One icon per selected account"], (int)Session.TrayMode,
                index => { if (index >= 0) { Session.TrayMode = (TrayDisplayMode)index; Session.Notify(); } })
                .AutomationName("Tray display mode").AutomationId("TrayMode").IsEnabled(!Session.Busy),
            TextBlock("Included accounts"),
            Session.Dashboard.Accounts.Count == 0 ? UI.Copy("Connect an account to show its usage.") :
                VStack(8, Session.Dashboard.Accounts.Select(account =>
                    CheckBox(!Session.ExcludedTrayAccounts.Contains(account.Key),
                        value =>
                        {
                            if (value == true) Session.ExcludedTrayAccounts.Remove(account.Key);
                            else Session.ExcludedTrayAccounts.Add(account.Key);
                            Session.Notify();
                        }, $"{account.Name} ({account.Host})").AutomationName($"Include {account.Login} on {account.Host} in tray")
                        .AutomationId("TrayAccount-" + account.Key).IsEnabled(!Session.Busy).WithKey(account.Key)).ToArray()),
            TrayPreview(),
            UI.Copy("\u221e means unlimited allocation; ! means a partial roll-up; ? means unavailable. Mixed roll-ups show only finite allocations. Numbers are rounded; <1 means below 1% and 999+ means above 999%. Hover for details.").FontSize(12),
            UI.Copy("A neutral icon remains when nothing is selected. Windows controls which icons appear in the notification area or its overflow.").FontSize(12)
        )),
        Button("Open data folder", () => Session.OpenLink(Session.Controller.DataDirectory, owner))
            .HAlign(HorizontalAlignment.Left),
        UI.Copy("Windows manages installation and removal. Install a newer signed package to update; GitHub builds do not check for updates.")
    );
    private Element TrayPreview()
    {
        var preview = Session.PreviewTray();
        string background = $"#{TrayIconRenderer.SystemPalette().Background:X8}";
        return VStack(8,
            TextBlock("Live preview").SemiBold(),
            UI.Copy("Draft choices using current usage. Icons are shown at tray size; apply with Save changes.").FontSize(12),
            VStack(8, preview.Icons.Select(icon =>
            {
                var account = preview.Accounts.FirstOrDefault(a => a.Key == icon.AccountKey);
                string label = account is null ? "Roll-up" : $"{account.Name} ({account.Host})";
                return Grid([GridSize.Auto, GridSize.Star()], [GridSize.Auto],
                    Border(new TrayPreviewElement().Set(image => TrayPreviewImage.Apply(image, icon, preview.Style))
                        .Width(16).Height(16)
                        .AutomationId("TrayPreviewIcon-" + (icon.AccountKey ?? "rollup"))
                        .AutomationName($"{label}: {icon.Details}").ToolTip(icon.Details))
                        .Background(background)
                        .Padding(2).VAlign(VerticalAlignment.Center).Margin(0, 0, 10, 0).Grid(column: 0),
                    TextBlock($"{label} - {icon.ValueText}" +
                        (icon.IsPartial ? " (partial)" : "") + (icon.IsOverAllocation ? " (over allocation)" : ""))
                        .TextWrapping().FontSize(12).VAlign(VerticalAlignment.Center).Grid(column: 1))
                    .WithKey(icon.AccountKey ?? "rollup");
            }).ToArray())
        ).AutomationId("TrayLivePreview");
    }
    private Element Notifications() => VStack(18,
        UI.Section("Windows notifications", "Alerts apply independently to each account.",
            ToggleSwitch(Session.Notifications, value => { Session.Notifications = value; Session.Notify(); })
                .AutomationName("Enable Windows notifications").IsEnabled(!Session.Busy)),
        Card(VStack(10, TextBlock("Allocation thresholds").SemiBold(),
            UI.Copy("Notify at these percentages of each account's custom budget or API allocation. Values above 100 are supported."),
            UI.SettingsInput(Session, SettingsField.Thresholds, Session.Thresholds, "Default allocation thresholds"))),
        Card(VStack(10, TextBlock("Spending increments").SemiBold(),
            UI.Copy("Notify whenever an account crosses another USD increment this billing period. For example, 50 alerts at $50, $100, $150..."),
            UI.SettingsInput(Session, SettingsField.Increment, Session.Increment, "Default USD spending increment",
                "Off (e.g. 50)", width: 200),
            UI.Copy("Blank or 0 turns this off. If several milestones are crossed between refreshes, one notification reports the highest. Account settings can override this default.").FontSize(12))),
        Button("Test notification", Session.SendTest).HAlign(HorizontalAlignment.Left),
        UI.Copy("A successful submission does not guarantee delivery. Windows notification settings and Do Not Disturb can suppress alerts.")
    );
    private Element Accounts(nint owner)
    {
        if (Session.ShowAddForm) return AddForm(owner);
        if (Session.SelectedAccount is { } key && Session.Dashboard.Accounts.FirstOrDefault(a => a.Key == key) is { } account)
            return AccountDetails(account, owner);
        return VStack(16,
            UI.Copy("Connect personal and enterprise identities. Credentials stay in Windows Credential Manager."),
            Button(HStack(8, Icon("Add"), TextBlock("Add account")), Session.AddAccount)
                .AutomationName("Add account").AutomationId("AddAccount").HAlign(HorizontalAlignment.Left).IsEnabled(Session.Initialized),
            Session.Dashboard.Accounts.Count == 0 ? UI.Copy("No accounts connected yet.") :
                VStack(10, Session.Dashboard.Accounts.Select(account =>
                    Card(Grid([GridSize.Auto, GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                        UI.AccountPicture(account, 40).Grid(column: 0).Margin(0, 0, 12, 0),
                        VStack(4, TextBlock(account.Name).SemiBold(), UI.Copy($"{account.Login} - {account.Host}"),
                            UI.Copy(account.Freshness).FontSize(12)).Grid(column: 1),
                        Button("Manage", () => Session.EditAccount(account.Key))
                            .AutomationName($"Manage {account.Login} on {account.Host}")
                            .VAlign(VerticalAlignment.Center).Grid(column: 2))).WithKey(account.Key)).ToArray())
        );
    }
    private Element AddForm(nint owner)
    {
        return VStack(18,
            TextBlock(Session.ReconnectKey is null ? "Connect an account" : "Reconnect account").FontSize(22).SemiBold(),
            Session.EditingHost
                ? VStack(18,
                    UI.Copy("Choose the GitHub host to sign in to."),
                    TextBlock("GitHub host").SemiBold(),
                    ComboBox(["github.com", "Custom..."], Session.CustomHost ? 1 : 0,
                        index => Session.SelectHost(index == 1))
                        .AutomationName("GitHub host").AutomationId("AccountHostSelection")
                        .IsEnabled(!Session.SigningIn && Session.ReconnectKey is null),
                    Session.CustomHost
                        ? VStack(8,
                            TextBox(Session.Host, Session.SetHost, "sample.ghe.com")
                                .AutomationName("Custom GitHub hostname").AutomationId("AccountHost")
                                .IsEnabled(!Session.SigningIn && Session.ReconnectKey is null),
                            TextBlock("OAuth Client ID").SemiBold(),
                            TextBox(Session.ClientId, Session.SetClientId, "Host-specific OAuth Client ID")
                                .AutomationName("Host-specific OAuth Client ID").AutomationId("AccountClientId")
                                .IsEnabled(!Session.SigningIn))
                        : null,
                    UI.Copy(Session.HostDescription()).FontSize(12),
                    CheckBox(Session.OfflineAccess, value => { Session.OfflineAccess = value; Session.Notify(); },
                        "Request offline_access where supported").IsEnabled(!Session.SigningIn),
                    UI.Copy("Sign-in requests basic identity access. Enterprise registrations and application policies are host-specific."))
                : Session.Prompt is { } device
                    ? UI.DeviceSignIn(Session, device, owner)
                    : Session.SigningIn
                        ? HStack(12, ProgressRing().Width(24).Height(24),
                            UI.Copy(Session.ConnectingAccount ? "Finishing your connection..." : $"Generating a sign-in code for {Session.Host}..."))
                            .AutomationId("SignInProgress")
                        : UI.Copy("Start sign-in to generate a new code."),
            FlexRow(
                !Session.SigningIn
                    ? Button(Session.EditingHost ? "Start sign-in" : "Try again", Session.StartSignIn)
                        .AccentButton().IsEnabled(!Session.Busy).AutomationId("StartSignIn")
                        .AutomationName(Session.EditingHost ? "Start sign-in" : "Try again").Flex(shrink: 0)
                    : null,
                Session.ReconnectKey is null && Session.Prompt is null && !Session.EditingHost && !Session.ConnectingAccount
                    ? Button("Change host", Session.ChangeHost).AutomationId("ChangeSignInHost").Flex(shrink: 0)
                    : null,
                Button(Session.SigningIn ? "Cancel sign-in" : "Back to accounts", () => Session.TryGoBack())
                    .AutomationName(Session.SigningIn ? "Cancel sign-in" : "Back to accounts").AutomationId("AccountBack").Flex(shrink: 0)) with
            {
                Wrap = FlexWrap.Wrap, ColumnGap = 10, RowGap = 8, AlignItems = FlexAlign.FlexStart
            }
        ).AutomationId("AccountOnboarding");
    }
    private Element AccountDetails(AccountView account, nint owner) => VStack(18,
        HStack(10, Button("Back", () => Session.TryGoBack()).AutomationId("AccountBack"),
            UI.AccountPicture(account, 48),
            VStack(2, TextBlock(account.Name).FontSize(22).SemiBold(), UI.Copy(account.Host).FontSize(12))
                .VAlign(VerticalAlignment.Center)),
        UI.AccountWarning(account) is { Length: > 0 } warning
            ? InfoBar("Account needs attention", warning).Warning().IsClosable(false).AutomationId("AccountWarning") : null,
        UsageSummary(account),
        BudgetSettings(account),
        Expander("Advanced details", Session.ShowAdvancedDetails ? AdvancedDetails(account) : VStack(),
            Session.ShowAdvancedDetails, expanded => { Session.ShowAdvancedDetails = expanded; Session.Notify(); })
            .HAlign(HorizontalAlignment.Stretch).AutomationId("AdvancedAccountDetails"),
        Card(VStack(10, TextBlock("Display name").SemiBold(),
            UI.SettingsInput(Session, SettingsField.DisplayName, Session.DisplayName, "Account display name"),
            TextBlock("Allocation threshold overrides").SemiBold(),
            UI.SettingsInput(Session, SettingsField.AccountThresholds, Session.AccountThresholds,
                "Account allocation threshold overrides", "Use global defaults"),
            CheckBox(Session.InheritIncrement, value => Session.SetInheritIncrement(value),
                "Use default spending increment").IsEnabled(!Session.Busy),
            UI.SettingsInput(Session, SettingsField.AccountIncrement, Session.AccountIncrement,
                "Account USD spending increment", "0 = off; e.g. 50", enabled: !Session.InheritIncrement),
            CheckBox(Session.ShowPeriodEstimate, value => { Session.ShowPeriodEstimate = value == true; Session.Notify(); },
                "Show estimated period consumption").AutomationId("ShowPeriodEstimate").IsEnabled(!Session.Busy),
            UI.Copy(UI.EstimateDescription).FontSize(12))),
        HStack(10, Button("Refresh", () => Session.RefreshAccount(account.Key)).IsEnabled(!Session.Busy),
            Button("Reconnect", () => Session.Reconnect(account)).IsEnabled(!Session.Busy)),
        UI.Copy("Removing an account deletes its local credential, not the OAuth grant. History follows your retention policy."),
        Button("Manage OAuth grants", () => Session.OpenLink($"https://{account.Host}/settings/applications", owner))
            .HAlign(HorizontalAlignment.Left),
        Session.ConfirmRemove
            ? Card(VStack(10, TextBlock("Remove this account from GHCPSpendTray?").SemiBold(),
                HStack(10, Button("Remove account", Session.RemoveAccount).IsEnabled(!Session.Busy),
                    Button("Cancel", () => { Session.ConfirmRemove = false; Session.Notify(); }))))
            : Button("Remove account...", () => { Session.ConfirmRemove = true; Session.Notify(); })
                .HAlign(HorizontalAlignment.Left)
    );
    private Element BudgetSettings(AccountView account) => Card(VStack(10,
        TextBlock("Usage budget").SemiBold(),
        UI.Copy("Choose what this account's usage percentage is measured against."),
        ComboBox(["Use API allocation", "Use a custom budget"], Session.UseCustomBudget ? 1 : 0,
            index => Session.SetCustomBudgetEnabled(index == 1))
            .AutomationName("Usage budget basis").AutomationId("BudgetBasis").IsEnabled(!Session.Busy),
        Session.UseCustomBudget
            ? VStack(8,
                TextBlock("Custom budget (USD)").SemiBold(),
                UI.SettingsInput(Session, SettingsField.CustomBudget, Session.CustomBudget, "Custom budget in USD",
                    "e.g. 500", enabled: !Session.Busy),
                UI.Copy(BudgetPreview(account)).FontSize(12).AutomationId("BudgetPreview"))
            : UI.Copy("API allocation: " + (account.Details.Unlimited ? "Unlimited" :
                UI.Money(account.Details.ObservedAllocationUsd))).FontSize(12),
        UI.Copy("Applies to this account's current billing period, usage meter, tray percentage, estimates, and percentage alerts. " +
            "It does not cap spending or change your GitHub allocation. Choose Save account to apply.").FontSize(12)
    )).AutomationId("UsageBudgetSettings");

    private string BudgetPreview(AccountView account)
    {
        if (!decimal.TryParse(Session.CustomBudget, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var budget) ||
            budget <= 0 || decimal.Round(budget, 2) != budget)
            return "Enter a positive USD amount with up to two decimal places.";
        return account.ConsumptionUsd is { } used
            ? $"Preview: {UI.Money(used)} consumed = {UsageBudget.Percentage(used, budget):0.##}% of {UI.Money(budget)} custom budget"
            : "Preview unavailable until current-period consumption is available.";
    }

    private static Element UsageSummary(AccountView account, string idPrefix = "") => Card(VStack(12,
        VStack(2, UI.Copy("This month's consumption"),
            TextBlock(UI.Money(account.ConsumptionUsd)).FontSize(36).SemiBold().AutomationId(idPrefix + "AccountConsumption")),
        account.Percent is { } percent
            ? VStack(6,
                Progress((double)Math.Clamp(percent, 0, 100)).AutomationName(UI.UsagePercentage(account)),
                UI.Copy(UI.UsagePercentage(account)))
            : UI.Copy(account.Details.Unlimited ? "Unlimited allocation" : "Allocation percentage not available"),
        UI.EstimateRow(account, idPrefix),
        UI.Copy(account.UpdatedAt is { } updated ? $"Updated {updated.ToLocalTime():g}" : "No observations yet").FontSize(12)
    )).AutomationId(idPrefix + "AccountSummary");

    private static Element AdvancedDetails(AccountView account, string idPrefix = "") => VStack(12,
        UI.DetailRow("GitHub login", account.Login, idPrefix + "DetailLogin"),
        UI.DetailRow("Host", account.Host, idPrefix + "DetailHost"),
        UI.DetailRow("Status", account.Freshness, idPrefix + "DetailStatus"),
        UI.DetailRow("AI credits", account.Details.CreditsUsed?.ToString("#,0.############################",
            CultureInfo.CurrentCulture) ?? "Not available", idPrefix + "DetailCredits"),
        UI.DetailRow("Recorded consumption", UI.Money(account.Details.ObservedConsumptionUsd), idPrefix + "DetailRecordedConsumption"),
        UI.DetailRow("Recorded allocation", account.Details.Unlimited ? "Unlimited" :
            UI.Money(account.Details.ObservedAllocationUsd), idPrefix + "DetailRecordedAllocation"),
        UI.DetailRow("Allocation consumed", account.Details.ObservedPercentConsumed is { } percent ?
            $"{percent:0.####}%" : account.Details.Unlimited ? "Not applicable (unlimited allocation)" :
            "Not available", idPrefix + "DetailRecordedPercent"),
        UI.DetailRow("Last fetched", UI.Timestamp(account.UpdatedAt), idPrefix + "DetailFetched"),
        UI.DetailRow("Source timestamp", UI.Timestamp(account.Details.SourceTimestampUtc, "Not supplied"), idPrefix + "DetailSource"),
        UI.DetailRow("Billing reset", UI.Timestamp(account.Details.ResetAtUtc, "Calendar-month fallback"), idPrefix + "DetailReset"),
        UI.DetailRow("Next refresh", UI.Timestamp(account.Details.NextRefreshUtc, "Pending"), idPrefix + "DetailNextRefresh"),
        UI.Copy("Times are shown in your local time zone.").FontSize(12),
        UI.EstimateDetails(account, idPrefix),
        !account.Details.IsCurrentPeriod && account.Details.CreditsUsed is not null
            ? UI.Copy("This observation is from a previous billing period and is excluded from current consumption.") : null
    ).AutomationId(idPrefix + "AccountDiagnosticsTable");

    private Element About(nint owner) => VStack(16, UI.Logo(64).HAlign(HorizontalAlignment.Left),
        TextBlock("GHCPSpendTray").FontSize(28).SemiBold(),
        UI.Copy($"Version {UI.Version}"),
        Session.StoreUpdates is { } updates ? UI.StoreUpdateCard(updates, owner) : null,
        UI.Copy("GitHub Copilot consumption, at a glance."),
        UI.Copy("Consumption is the USD value of AI credits used, not an invoice, internal finance budget, or all-product spend."),
        UI.Copy("Built with Microsoft UI Reactor, WinUI 3, and .NET Native AOT. The consumption endpoint is undocumented and may change."),
        HyperlinkButton("Project and source code", new Uri("https://github.com/DamianEdwards/ghcp-spend-tray")),
        UI.Copy("MIT licensed. Independent project; not endorsed by GitHub."));
}
