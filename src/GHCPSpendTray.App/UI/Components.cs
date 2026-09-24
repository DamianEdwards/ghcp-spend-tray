using System.Globalization;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Microsoft.UI.Reactor.Factories;

namespace GHCPSpendTray.App.UI;

// A cost-first tray surface and task-oriented settings, using the user's Windows theme and native Fluent controls.
internal static class UI
{
    internal static string Money(decimal? amount) => amount is { } value ?
        "$" + value.ToString("N2", CultureInfo.GetCultureInfo("en-US")) : "Unavailable";
    internal static TextBlockElement Copy(string text) => TextBlock(text).TextWrapping().Foreground(Theme.SecondaryText);
    internal static Element Logo(double size) => Image(Path.Combine(AppContext.BaseDirectory, "Assets", "ghcpspendtray-logo.png"))
        .Width(size).Height(size).AutomationName("GHCPSpendTray");
    internal static ButtonElement Glyph(string glyph, string label, Action action) =>
        Button(Icon(FontIcon(glyph, "Segoe Fluent Icons", 18)), action)
            .Width(40).Height(40).Padding(0).AutomationName(label).ToolTip(label);
    internal static Element Section(string title, string description, Element control) =>
        Card(Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
            VStack(5, TextBlock(title).SemiBold(), Copy(description)).Grid(column: 0).Margin(0, 0, 24, 0),
            control.Grid(column: 1).VAlign(VerticalAlignment.Center)));
    internal static Element? Feedback(AppSession state) => state.Error is { } error
        ? InfoBar("Unable to complete", error).Error().IsClosable(false)
        : state.Notice is { } notice ? InfoBar("", notice).Informational().IsClosable(false) : null;
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
    internal static Element Graph(AccountView account)
    {
        var now = DateTimeOffset.UtcNow;
        var start = now.AddHours(-24);
        var points = account.Graph.Where(p => p.Time >= start).ToArray();
        var maximum = Math.Max(.01m, points.Where(p => p.Rate.HasValue).Select(p => p.Rate!.Value).DefaultIfEmpty().Max());
        var lines = new List<Element>();
        GraphPoint? previous = null;
        var brush = new SolidColorBrush(Microsoft.UI.Colors.SeaGreen);
        foreach (var point in points)
        {
            if (point.Rate is null) { previous = null; continue; }
            double x = (point.Time - start).TotalHours / 24 * 320;
            double y = 50 - (double)(point.Rate.Value / maximum) * 44;
            var segment = previous is { Rate: { } prior } ? Line(
                (previous.Time - start).TotalHours / 24 * 320, 50 - (double)(prior / maximum) * 44, x, y) :
                Line(x - 2, y, x + 2, y);
            lines.Add(segment.Stroke(brush).StrokeThickness(2).WithKey(lines.Count.ToString(CultureInfo.InvariantCulture)));
            previous = point;
        }
        return Viewbox(Canvas(lines.ToArray()).Width(320).Height(56)).Height(56)
            .AutomationName("Observed consumption rate in USD per hour; textual summary follows");
    }
}

internal abstract class SessionComponent(AppSession session) : Component
{
    protected AppSession Session { get; } = session;
    protected void UseSession()
    {
        var (_, setRevision) = UseState(Session.Revision);
        UseEffect(() =>
        {
            void Changed() => setRevision(Session.Revision);
            Session.Changed += Changed;
            return () => Session.Changed -= Changed;
        }, Session);
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
                UI.Glyph("\uE713", "Settings", () => Session.OpenSettings?.Invoke(SettingsPage.General))
                    .AutomationId("OpenSettings").Grid(column: 2)
            ).Grid(row: 0).Padding(20, 14),
            ScrollView(VStack(12, UI.Feedback(Session), body)).Grid(row: 1).Padding(20, 0),
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
            Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                VStack(2, TextBlock(account.Name).SemiBold(), UI.Copy(account.Host).FontSize(12)).Grid(column: 0),
                TextBlock(UI.Money(account.ConsumptionUsd)).FontSize(21).SemiBold()
                    .VAlign(VerticalAlignment.Center).Grid(column: 1)),
            account.Percent is { } percent
                ? VStack(5, Progress((double)Math.Clamp(percent, 0, 100)).AutomationName($"{percent:0.##}% of allocation consumed"),
                    UI.Copy($"{percent:0.##}% of {UI.Money(account.AllocationUsd)} allocation").FontSize(12))
                : UI.Copy("Allocation percentage not available").FontSize(12),
            account.Graph.Count > 1 ? UI.Graph(account) : null,
            UI.Copy(account.HistoryText).FontSize(12),
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
            SettingsPage.Accounts => Accounts(hwnd),
            SettingsPage.Notifications => Notifications(),
            SettingsPage.About => About(),
            _ => General(hwnd)
        };
        var navigation = NavigationView(
            [
                NavItem("General", "Setting", "General"),
                NavItem("Accounts", "Contact", "Accounts"),
                NavItem("Notifications", "Message", "Notifications"),
                NavItem("About", "Help", "About")
            ], ScrollView(VStack(22,
                TextBlock(Session.Page.ToString()).FontSize(30).SemiBold(),
                UI.Feedback(Session),
                content
            ).Padding(30, 24)))
            with { SelectedTag = Session.Page.ToString(), IsSettingsVisible = false };
        return Grid([GridSize.Star()], [GridSize.Auto, GridSize.Star()],
            TitleBar("GHCPSpendTray Settings").Grid(row: 0),
            navigation.SelectedTagChanged(tag =>
            {
                if (Enum.TryParse<SettingsPage>(tag, out var page) && page != Session.Page) Session.Navigate(page);
            }).OpenPaneLength(230).PaneDisplayMode(NavigationViewPaneDisplayMode.Auto)
                .BackButtonVisible(false).Grid(row: 1)
        ).AutomationId("SettingsRoot");
    }
    private Element General(nint owner) => VStack(18,
        UI.Section("Start with Windows", Session.Controller.Settings.StartupDescription,
            ToggleSwitch(Session.Startup, value => { Session.Startup = value; Session.Notify(); })
                .AutomationName("Start with Windows").IsEnabled(Session.Controller.Settings.CanChangeStartup && !Session.Busy)),
        Button("Open Windows startup settings", () => Session.OpenLink("ms-settings:startupapps", owner))
            .IsEnabled(!Session.Controller.Portable),
        Card(VStack(10, TextBlock("Refresh interval").SemiBold(),
            UI.Copy("Check each account every 5 to 1440 minutes. The default is one hour."),
            TextBox(Session.PollMinutes, value => Session.PollMinutes = value).Width(180)
                .HAlign(HorizontalAlignment.Left).AutomationName("Refresh interval in minutes"))),
        HStack(10, Button("Save changes", Session.SaveGlobal).IsEnabled(Session.Initialized && !Session.Busy),
            Button("Open data folder", () => Session.OpenLink(Session.Controller.DataDirectory, owner))),
        UI.Copy("Windows manages installation and removal. Install a newer signed package to update; GitHub builds do not check for updates.")
    );
    private Element Notifications() => VStack(18,
        UI.Section("Windows notifications", "Alerts apply independently to each account.",
            ToggleSwitch(Session.Notifications, value => { Session.Notifications = value; Session.Notify(); })
                .AutomationName("Enable Windows notifications")),
        Card(VStack(10, TextBlock("Allocation thresholds").SemiBold(),
            UI.Copy("Notify at these percentages of each account's allocation. Values above 100 are supported."),
            TextBox(Session.Thresholds, value => Session.Thresholds = value).AutomationName("Default allocation thresholds"))),
        Card(VStack(10, TextBlock("Spending increments").SemiBold(),
            UI.Copy("Notify whenever an account crosses another USD increment this billing period. For example, 50 alerts at $50, $100, $150..."),
            TextBox(Session.Increment, value => Session.Increment = value, "Off (e.g. 50)")
                .Width(200).HAlign(HorizontalAlignment.Left).AutomationName("Default USD spending increment"),
            UI.Copy("Blank or 0 turns this off. If several milestones are crossed between refreshes, one notification reports the highest. Account settings can override this default.").FontSize(12))),
        HStack(10, Button("Save changes", Session.SaveGlobal).IsEnabled(Session.Initialized && !Session.Busy),
            Button("Test notification", Session.SendTest)),
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
                    Card(Grid([GridSize.Star(), GridSize.Auto], [GridSize.Auto],
                        VStack(4, TextBlock(account.Name).SemiBold(), UI.Copy($"{account.Login} - {account.Host}"),
                            UI.Copy(account.Freshness).FontSize(12)).Grid(column: 0),
                        Button("Manage", () => Session.EditAccount(account.Key))
                            .AutomationName($"Manage {account.Login} on {account.Host}")
                            .VAlign(VerticalAlignment.Center).Grid(column: 1))).WithKey(account.Key)).ToArray())
        );
    }
    private Element AddForm(nint owner)
    {
        string remaining = Session.Prompt is { } prompt
            ? Math.Max(0, (int)(prompt.Expires - DateTimeOffset.UtcNow).TotalSeconds).ToString(CultureInfo.InvariantCulture) : "";
        return VStack(18,
            TextBlock(Session.ReconnectKey is null ? "Connect an account" : "Reconnect account").FontSize(22).SemiBold(),
            UI.Copy("Authorize our GHCPSpendTray application in your browser. No password or token needs to be pasted here."),
            TextBox(Session.Host, value => { Session.Host = value; Session.Notify(); }, "github.com")
                .AutomationName("GitHub host").AutomationId("AccountHost")
                .IsEnabled(!Session.SigningIn && Session.ReconnectKey is null),
            UI.Copy(Session.HostDescription()).FontSize(12),
            CheckBox(Session.OfflineAccess, value => { Session.OfflineAccess = value; Session.Notify(); },
                "Request offline_access where supported").IsEnabled(!Session.SigningIn),
            UI.Copy("Sign-in requests basic identity access. Enterprise registrations and application policies are host-specific."),
            Session.Identity is { } identity
                ? Card(VStack(12, TextBlock("Confirm this account").FontSize(20).SemiBold(),
                    TextBlock($"{identity.Login} on {identity.Host}").TextWrapping(),
                    UI.Copy($"Immutable user ID: {identity.UserId}. Consumption access was checked."),
                    HStack(10, Button("Connect this account", () => Session.ConfirmIdentity(true)),
                        Button("Wrong account", () => Session.ConfirmIdentity(false)))))
                : Session.Prompt is { } device
                    ? Card(VStack(12,
                        UI.Copy("Enter this code on GitHub"),
                        TextBlock(device.Code).FontSize(32).SemiBold().IsTextSelectionEnabled().AutomationName("Device authorization code"),
                        HStack(10, Button("Copy code", () => Session.CopyCode(owner)),
                            Button("Open browser", () => Session.OpenLink(device.VerificationUri.AbsoluteUri, owner))),
                        UI.Copy($"Expires in {remaining} seconds. Waiting for authorization...")))
                    : Session.SigningIn ? HStack(12, ProgressRing().Width(24).Height(24), UI.Copy("Requesting device sign-in...")) : null,
            HStack(10,
                Button("Start device sign-in", Session.StartSignIn).IsEnabled(!Session.SigningIn && !Session.Busy),
                Button(Session.SigningIn ? "Cancel sign-in" : "Back to accounts", () =>
                {
                    if (Session.SigningIn) Session.CancelSignIn();
                    else { Session.CloseSettings(); Session.Navigate(SettingsPage.Accounts); }
                }).AutomationName(Session.SigningIn ? "Cancel sign-in" : "Back to accounts"))
        ).AutomationId("AccountOnboarding");
    }
    private Element AccountDetails(AccountView account, nint owner) => VStack(18,
        HStack(10, Button("Back", () => { Session.CloseSettings(); Session.Navigate(SettingsPage.Accounts); }),
            VStack(2, TextBlock(account.Name).FontSize(22).SemiBold(), UI.Copy(account.Host).FontSize(12))
                .VAlign(VerticalAlignment.Center)),
        UI.AccountWarning(account) is { Length: > 0 } warning
            ? InfoBar("Account needs attention", warning).Warning().IsClosable(false).AutomationId("AccountWarning") : null,
        Card(VStack(12,
            VStack(2, UI.Copy("This month's consumption"),
                TextBlock(UI.Money(account.ConsumptionUsd)).FontSize(36).SemiBold().AutomationId("AccountConsumption")),
            account.Percent is { } percent
                ? VStack(6,
                    Progress((double)Math.Clamp(percent, 0, 100)).AutomationName($"{percent:0.##}% of allocation consumed"),
                    UI.Copy($"{percent:0.##}% of {UI.Money(account.AllocationUsd)} allocation"))
                : UI.Copy(account.Details.Unlimited ? "Unlimited allocation" : "Allocation percentage not available"),
            UI.Copy(account.UpdatedAt is { } updated ? $"Updated {updated.ToLocalTime():g}" : "No observations yet").FontSize(12)
        )).AutomationId("AccountSummary"),
        Expander("Spending history", Session.ShowAccountHistory
                ? VStack(12, UI.Graph(account), UI.Copy(account.HistoryText),
                    UI.Copy("Observed USD/hour between refreshes. Gaps and corrections are not treated as zero.").FontSize(12))
                : VStack(),
            Session.ShowAccountHistory, expanded => { Session.ShowAccountHistory = expanded; Session.Notify(); })
            .HAlign(HorizontalAlignment.Stretch).AutomationId("AccountHistory"),
        Expander("Advanced details", Session.ShowAdvancedDetails ? AdvancedDetails(account) : VStack(),
            Session.ShowAdvancedDetails, expanded => { Session.ShowAdvancedDetails = expanded; Session.Notify(); })
            .HAlign(HorizontalAlignment.Stretch).AutomationId("AdvancedAccountDetails"),
        Card(VStack(10, TextBlock("Display name").SemiBold(),
            TextBox(Session.DisplayName, value => Session.DisplayName = value).AutomationName("Account display name"),
            TextBlock("Allocation threshold overrides").SemiBold(),
            TextBox(Session.AccountThresholds, value => Session.AccountThresholds = value, "Use global defaults")
                .AutomationName("Account allocation threshold overrides"),
            CheckBox(Session.InheritIncrement, value => { Session.InheritIncrement = value; Session.Notify(); },
                "Use default spending increment"),
            TextBox(Session.AccountIncrement, value => Session.AccountIncrement = value, "0 = off; e.g. 50")
                .IsEnabled(!Session.InheritIncrement).AutomationName("Account USD spending increment"))),
        HStack(10, Button("Save account", Session.SaveAccount).IsEnabled(!Session.Busy),
            Button("Refresh", () => Session.Refresh(account.Key)).IsEnabled(!Session.Busy),
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
    private static Element AdvancedDetails(AccountView account) => VStack(12,
        UI.DetailRow("GitHub login", account.Login, "DetailLogin"),
        UI.DetailRow("Host", account.Host, "DetailHost"),
        UI.DetailRow("Status", account.Freshness, "DetailStatus"),
        UI.DetailRow("AI credits", account.Details.CreditsUsed?.ToString("#,0.############################",
            CultureInfo.CurrentCulture) ?? "Not available", "DetailCredits"),
        UI.DetailRow("Recorded consumption", UI.Money(account.Details.ObservedConsumptionUsd), "DetailRecordedConsumption"),
        UI.DetailRow("Recorded allocation", account.Details.Unlimited ? "Unlimited" :
            UI.Money(account.Details.ObservedAllocationUsd), "DetailRecordedAllocation"),
        UI.DetailRow("Allocation consumed", account.Details.ObservedPercentConsumed is { } percent ?
            $"{percent:0.####}%" : "Not available", "DetailRecordedPercent"),
        UI.DetailRow("Last fetched", UI.Timestamp(account.UpdatedAt), "DetailFetched"),
        UI.DetailRow("Source timestamp", UI.Timestamp(account.Details.SourceTimestampUtc, "Not supplied"), "DetailSource"),
        UI.DetailRow("Billing reset", UI.Timestamp(account.Details.ResetAtUtc, "Calendar-month fallback"), "DetailReset"),
        UI.DetailRow("Next refresh", UI.Timestamp(account.Details.NextRefreshUtc, "Pending"), "DetailNextRefresh"),
        UI.Copy("Times are shown in your local time zone.").FontSize(12),
        !account.Details.IsCurrentPeriod && account.Details.CreditsUsed is not null
            ? UI.Copy("This observation is from a previous billing period and is excluded from current consumption.") : null
    ).AutomationId("AccountDiagnosticsTable");

    private static Element About() => VStack(16, UI.Logo(64).HAlign(HorizontalAlignment.Left),
        TextBlock("GHCPSpendTray").FontSize(28).SemiBold(),
        UI.Copy("GitHub Copilot consumption, at a glance."),
        UI.Copy("Consumption is the USD value of AI credits used, not an invoice, internal finance budget, or all-product spend."),
        UI.Copy("Built with Microsoft UI Reactor, WinUI 3, and .NET Native AOT. The consumption endpoint is undocumented and may change."),
        HyperlinkButton("Project and source code", new Uri("https://github.com/DamianEdwards/ghcp-spend-tray")),
        UI.Copy("MIT licensed. Independent project; not endorsed by GitHub."));
}
