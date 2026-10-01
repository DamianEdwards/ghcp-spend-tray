using System.Globalization;
using System.Reflection;
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
    internal static TextBlockElement Copy(string text) => TextBlock(text).TextWrapping().Foreground(Theme.SecondaryText);
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
    internal static Element? Feedback(AppSession state, bool showNotice = true) => state.Error is { } error
        ? InfoBar("Unable to complete", error).Error().IsClosable(false)
        : showNotice && state.Notice is { } notice
            ? state.NoticeIsSuccess
                ? InfoBar("Complete!", notice).Success().IsClosable(false).AutomationId("AccountConnectionComplete")
                : InfoBar("", notice).Informational().IsClosable(false)
            : null;
    internal static Element DeviceSignIn(AppSession state, DevicePrompt device, nint owner)
    {
        int remaining = Math.Max(0, (int)(device.Expires - DateTimeOffset.UtcNow).TotalSeconds);
        return Card(VStack(16,
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
            Copy($"Waiting for authorization on {device.VerificationUri.Host}. Code expires in {remaining} seconds.").FontSize(12)
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
                UI.Glyph("\uE713", "Settings", () => Session.OpenSettings?.Invoke(SettingsPage.Usage))
                    .AutomationId("OpenSettings").Grid(column: 2)
            ).Grid(row: 0).Padding(20, 14),
            ScrollView(VStack(12, UI.Feedback(Session, showNotice: false), body)).Grid(row: 1).Padding(20, 0),
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
                ? VStack(5, Progress((double)Math.Clamp(percent, 0, 100)).AutomationName($"{percent:0.##}% of allocation consumed"),
                    UI.Copy($"{percent:0.##}% of {UI.Money(account.AllocationUsd)} allocation").FontSize(12))
                : UI.Copy("Allocation percentage not available").FontSize(12),
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
            SettingsPage.About => About(),
            _ => General(hwnd)
        };
        var navigation = NavigationView(
            [
                NavItem("Usage", "Home", "Usage"),
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
        AdvancedDetails(account, account.Key + "_")
    )).WithKey(account.Key);
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
        Card(VStack(12,
            TextBlock("System tray").SemiBold(),
            UI.Copy("Show fresh allocation usage, independently of the dollar totals. New accounts are included by default."),
            TextBlock("Icon style"),
            ComboBox(["Pie chart", "Percentage number"], (int)Session.TrayStyle,
                index => { if (index >= 0) { Session.TrayStyle = (TrayIconStyle)index; Session.Notify(); } })
                .AutomationName("Tray icon style").AutomationId("TrayStyle"),
            TextBlock("Icons to show"),
            ComboBox(["One roll-up icon", "One icon per selected account"], (int)Session.TrayMode,
                index => { if (index >= 0) { Session.TrayMode = (TrayDisplayMode)index; Session.Notify(); } })
                .AutomationName("Tray display mode").AutomationId("TrayMode"),
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
                        .AutomationId("TrayAccount-" + account.Key).WithKey(account.Key)).ToArray()),
            TrayPreview(),
            UI.Copy("! means a partial roll-up; ? means unavailable. Numbers are rounded; <1 means below 1% and 999+ means above 999%. Hover for the percentage; Usage has all inclusion details.").FontSize(12),
            UI.Copy("A neutral icon remains when nothing is selected. Windows controls which icons appear in the notification area or its overflow.").FontSize(12)
        )),
        HStack(10, Button("Save changes", Session.SaveGlobal).AutomationId("SaveGeneralSettings")
                .IsEnabled(Session.Initialized && !Session.Busy),
            Button("Open data folder", () => Session.OpenLink(Session.Controller.DataDirectory, owner))),
        UI.Copy("Windows manages installation and removal. Install a newer signed package to update; GitHub builds do not check for updates.")
    );
    private Element TrayPreview()
    {
        var preview = Session.PreviewTray();
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
                Session.ReconnectKey is null && !Session.EditingHost && !Session.ConnectingAccount
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
            Button("Refresh", () => Session.RefreshAccount(account.Key)).IsEnabled(!Session.Busy),
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
    private static Element UsageSummary(AccountView account, string idPrefix = "") => Card(VStack(12,
        VStack(2, UI.Copy("This month's consumption"),
            TextBlock(UI.Money(account.ConsumptionUsd)).FontSize(36).SemiBold().AutomationId(idPrefix + "AccountConsumption")),
        account.Percent is { } percent
            ? VStack(6,
                Progress((double)Math.Clamp(percent, 0, 100)).AutomationName($"{percent:0.##}% of allocation consumed"),
                UI.Copy($"{percent:0.##}% of {UI.Money(account.AllocationUsd)} allocation"))
            : UI.Copy(account.Details.Unlimited ? "Unlimited allocation" : "Allocation percentage not available"),
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
            $"{percent:0.####}%" : "Not available", idPrefix + "DetailRecordedPercent"),
        UI.DetailRow("Last fetched", UI.Timestamp(account.UpdatedAt), idPrefix + "DetailFetched"),
        UI.DetailRow("Source timestamp", UI.Timestamp(account.Details.SourceTimestampUtc, "Not supplied"), idPrefix + "DetailSource"),
        UI.DetailRow("Billing reset", UI.Timestamp(account.Details.ResetAtUtc, "Calendar-month fallback"), idPrefix + "DetailReset"),
        UI.DetailRow("Next refresh", UI.Timestamp(account.Details.NextRefreshUtc, "Pending"), idPrefix + "DetailNextRefresh"),
        UI.Copy("Times are shown in your local time zone.").FontSize(12),
        !account.Details.IsCurrentPeriod && account.Details.CreditsUsed is not null
            ? UI.Copy("This observation is from a previous billing period and is excluded from current consumption.") : null
    ).AutomationId(idPrefix + "AccountDiagnosticsTable");

    private static Element About() => VStack(16, UI.Logo(64).HAlign(HorizontalAlignment.Left),
        TextBlock("GHCPSpendTray").FontSize(28).SemiBold(),
        UI.Copy($"Version {UI.Version}"),
        UI.Copy("GitHub Copilot consumption, at a glance."),
        UI.Copy("Consumption is the USD value of AI credits used, not an invoice, internal finance budget, or all-product spend."),
        UI.Copy("Built with Microsoft UI Reactor, WinUI 3, and .NET Native AOT. The consumption endpoint is undocumented and may change."),
        HyperlinkButton("Project and source code", new Uri("https://github.com/DamianEdwards/ghcp-spend-tray")),
        UI.Copy("MIT licensed. Independent project; not endorsed by GitHub."));
}
