import AppKit
import ServiceManagement
import SwiftUI

struct AccountAvatar: View {
    let account: AccountData
    var body: some View {
        Group {
            // Only display the shared cache; never make a second, unvalidated avatar request.
            if let path = account.avatarUrl, path.hasPrefix("/"), let image = NSImage(contentsOfFile: path) {
                Image(nsImage: image).resizable().scaledToFill()
            } else {
                Text(String(account.name.prefix(2)).uppercased())
                    .font(.headline).frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(Color.accentColor.opacity(0.15))
            }
        }
        .frame(width: 36, height: 36).clipShape(Circle())
        .accessibilityLabel("Avatar for \(account.name)")
    }
}

struct AccountRow: View {
    let account: AccountData
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                AccountAvatar(account: account)
                VStack(alignment: .leading) {
                    Text(account.name).font(.headline)
                    Text("\(account.login) @ \(account.host)").font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Text(money(account.consumptionUsd)).font(.title3).monospacedDigit()
            }
            if let percent = account.percent {
                ProgressView(value: min(max(NSDecimalNumber(decimal: percent).doubleValue, 0), 100), total: 100)
                    .tint(percent >= 100 ? .red : .accentColor)
                    .accessibilityLabel("\(account.targetLabel) consumed")
                    .accessibilityValue("\(decimalText(percent)) percent")
            }
            Text(account.usageSummary).font(.caption).foregroundStyle(.secondary)
            if let estimate = account.periodEstimate {
                PeriodEstimateRow(estimate: estimate, customBudget: account.customBudgetUsd != nil)
            }
            Text(account.freshness).font(.caption)
                .foregroundStyle(account.freshness == "Fresh" ? Color.secondary : Color.orange)
            if let message = account.details.message { Text(message).font(.caption).foregroundStyle(.orange) }
        }
        .padding(.vertical, 5)
    }
}

struct PeriodEstimateRow: View {
    let estimate: PeriodEstimateData
    var customBudget = false
    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .firstTextBaseline) {
                Text("Estimated at reset")
                Spacer()
                Text(estimatedMoney(estimate.estimatedConsumptionUsd)).monospacedDigit()
                    .accessibilityLabel("Estimated consumption: \(estimatedMoney(estimate.estimatedConsumptionUsd))")
            }
            if let reason = estimate.unavailableReason {
                Text(reason).fixedSize(horizontal: false, vertical: true)
            } else {
                Text(estimate.summary(customBudget: customBudget)).fixedSize(horizontal: false, vertical: true)
                    .foregroundStyle((estimate.overAllocationUsd ?? 0) > 0 ? Color.orange : Color.secondary)
            }
        }
        .font(.caption).foregroundStyle(.secondary)
        .help("Based on your average consumption so far this UTC calendar month. Assumes the same pace continues; not an invoice.")
        .accessibilityIdentifier("PeriodEstimate")
    }
}

struct StatusMessage: View {
    @ObservedObject var model: AppModel
    var body: some View {
        if let error = model.error {
            HStack(alignment: .top) {
                Image(systemName: "exclamationmark.triangle.fill")
                Text(error).textSelection(.enabled)
                Spacer()
                Button { model.error = nil } label: { Image(systemName: "xmark") }
                    .buttonStyle(.plain).accessibilityLabel("Dismiss error")
            }
            .foregroundStyle(.red).padding(10).background(Color.red.opacity(0.08))
        } else if let notice = model.notice {
            Text(notice).foregroundStyle(.secondary).padding(8)
        }
    }
}

struct ExampleAccountButton: View {
    @ObservedObject var model: AppModel
    var body: some View {
        if model.demo {
            Button("Add Example Account") { model.addExampleAccount() }
                .disabled(!model.initialized || model.busy)
                .help("Add synthetic usage for this session only, without signing in or saving account data.")
        }
    }
}

struct AccountSetupPrompt: View {
    @ObservedObject var model: AppModel
    var alignment: HorizontalAlignment = .center
    var body: some View {
        VStack(alignment: alignment, spacing: 12) {
            Text("Add an account to see your usage").font(.title2)
                .fixedSize(horizontal: false, vertical: true)
            Text("Connect your GitHub account to start tracking Copilot consumption.")
                .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Button("Add Account...") { model.addAccount() }.buttonStyle(.borderedProminent)
                .disabled(!model.initialized || model.busy)
            ExampleAccountButton(model: model)
        }
        .multilineTextAlignment(alignment == .leading ? .leading : .center)
    }
}

struct FlyoutView: View {
    @ObservedObject var model: AppModel
    var body: some View {
        let noAccounts = model.dashboard?.accounts.isEmpty == true
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Image(nsImage: NSApplication.shared.applicationIconImage).resizable().frame(width: 24, height: 24)
                Text("Copilot consumption").font(.headline)
                Spacer()
                Button { model.openSettings() } label: { Image(systemName: "gearshape") }
                    .help("Open Settings").accessibilityLabel("Open Settings")
            }
            if noAccounts {
                StatusMessage(model: model)
                AccountSetupPrompt(model: model, alignment: .leading).padding(.vertical, 8)
            } else {
                HStack(alignment: .firstTextBaseline) {
                    Text(money(model.dashboard?.consumptionUsd)).font(.largeTitle).monospacedDigit()
                    Text("month to date").foregroundStyle(.secondary)
                }
                if let dashboard = model.dashboard, !dashboard.isComplete {
                    Text(dashboard.isLastKnown ? "Last-known / partial consumption" : "Partial / unavailable consumption")
                        .font(.caption).foregroundStyle(.orange)
                }
                if let tray = model.dashboard?.tray {
                    Text(tray.rollUp.details).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                }
                if !model.demo && model.settings?.notifications == true,
                   let permission = model.notificationPermission, !permission.canSend {
                    Button("Set Up Notifications...") { model.openSettings(.notifications) }
                        .font(.caption)
                }
                StatusMessage(model: model)
                ScrollView {
                    VStack(alignment: .leading, spacing: 12) {
                        ForEach(model.dashboard?.accounts ?? []) { account in
                            Button {
                                model.editAccount(account.key)
                            } label: { AccountRow(account: account) }
                                .buttonStyle(.plain)
                            Divider()
                        }
                    }
                }
                .frame(maxHeight: 330)
                ExampleAccountButton(model: model)
            }
            HStack {
                if !noAccounts {
                    Button("Refresh Now") { model.perform("refresh") }.disabled(!model.initialized || model.busy)
                }
                if model.busy { ProgressView().controlSize(.small) }
                Spacer()
                Button("Quit") { NSApplication.shared.terminate(nil) }.keyboardShortcut("q")
            }
            UpdateReminder(updates: model.updates)
        }
        .padding(18).frame(width: 400)
    }
}

struct SettingsView: View {
    @ObservedObject var model: AppModel
    var body: some View {
        NavigationSplitView {
            List(SettingsPage.allCases, selection: Binding(
                get: { model.page }, set: { model.navigate($0) })) { page in
                Label(page.rawValue, systemImage: page.icon).tag(page)
            }.navigationSplitViewColumnWidth(min: 150, ideal: 165, max: 200)
        } detail: {
            VStack(spacing: 0) {
                StatusMessage(model: model)
                if !model.initialized {
                    VStack(spacing: 16) {
                        Text("Loading GHCPSpendTray...")
                        if model.busy { ProgressView() }
                        else { Text("Restart the app after correcting the error above.").foregroundStyle(.secondary) }
                    }.frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    switch model.page {
                    case .usage: UsageView(model: model)
                    case .accounts: AccountsView(model: model)
                    case .general: PreferencesView(model: model, form: model.preferences, notifications: false)
                    case .notifications: PreferencesView(model: model, form: model.preferences, notifications: true)
                    case .about: AboutView(model: model)
                    }
                }
            }.frame(minWidth: 500)
        }
        .frame(minWidth: 730, minHeight: 550)
        .sheet(isPresented: $model.showingSignIn, onDismiss: { model.cancelSignIn() }) {
            SignInView(model: model)
        }
        .onReceive(model.preferences.$errors) { errors in
            if errors.isEmpty && model.error == "Correct the highlighted fields before saving." { model.error = nil }
        }
    }
}

struct UsageView: View {
    @ObservedObject var model: AppModel
    var body: some View {
        if model.dashboard?.accounts.isEmpty == true {
            VStack(spacing: 16) {
                Image(nsImage: NSApplication.shared.applicationIconImage).resizable().frame(width: 72, height: 72)
                AccountSetupPrompt(model: model)
            }.padding(24).frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            usage
        }
    }

    private var usage: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                Text("Usage").font(.largeTitle)
                Text(model.dashboard?.total ?? "Consumption unavailable").font(.title2)
                Text(model.dashboard?.status ?? "").foregroundStyle(.secondary)
                if let tray = model.dashboard?.tray {
                    Text(tray.rollUp.details).font(.caption).textSelection(.enabled)
                }
                HStack {
                    Button("Refresh Now") { model.perform("refresh") }.disabled(model.busy)
                    Button("Add Account...") { model.addAccount() }.disabled(model.busy)
                    if model.busy { ProgressView().controlSize(.small) }
                }
                ForEach(model.dashboard?.accounts ?? []) { account in
                    GroupBox {
                        VStack(alignment: .leading, spacing: 12) {
                            AccountRow(account: account)
                            AccountDiagnosticsView(account: account)
                        }.padding(6).frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
                Text("USD consumption is AI credits used divided by 100, not an invoice or total GitHub spend. Locally sampled history is not a verified daily breakdown.")
                    .font(.caption).foregroundStyle(.secondary)
            }.padding(24).frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}

struct FullWidthDisclosureGroupStyle: DisclosureGroupStyle {
    var accessibilityIdentifier = "AdvancedDetailsToggle"

    func makeBody(configuration: Configuration) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Button {
                withAnimation { configuration.isExpanded.toggle() }
            } label: {
                HStack(spacing: 6) {
                    Image(systemName: configuration.isExpanded ? "chevron.down" : "chevron.right")
                        .font(.caption).frame(width: 10).accessibilityHidden(true)
                    configuration.label
                    Spacer()
                }
                .frame(maxWidth: .infinity, alignment: .leading).padding(.vertical, 4)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityValue(configuration.isExpanded ? "Expanded" : "Collapsed")
            .accessibilityIdentifier(accessibilityIdentifier)
            .help(configuration.isExpanded ? "Hide advanced details" : "Show advanced details")
            if configuration.isExpanded { configuration.content }
        }
    }
}

struct AccountDiagnosticsView: View {
    let account: AccountData
    var body: some View {
        DisclosureGroup("Advanced Details") {
            Grid(alignment: .leading, horizontalSpacing: 24, verticalSpacing: 8) {
                row("Credits used (observed)", decimalText(account.details.creditsUsed).isEmpty ? "Unavailable" : decimalText(account.details.creditsUsed))
                row("Consumption (observed)", money(account.details.observedConsumptionUsd))
                row("Allocation (observed)", account.details.unlimited ? "Unlimited" : money(account.details.observedAllocationUsd))
                row("Percent (observed)", account.details.observedPercentConsumed.map { "\(decimalText($0))%" } ??
                    (account.details.unlimited ? "Not applicable (unlimited allocation)" : "Unavailable"))
                row("Current billing period", account.details.isCurrentPeriod ? "Yes" : "No")
                row("Last fetched", dateText(account.updatedAt))
                row("Source timestamp", dateText(account.details.sourceTimestampUtc))
                row("Resets", dateText(account.details.resetAtUtc))
                row("Next refresh", dateText(account.details.nextRefreshUtc))
                row("Period", account.details.periodId ?? "Unavailable")
                if let estimate = account.periodEstimate {
                    row("Estimate method", "Average pace so far this UTC calendar month. Assumes the same pace continues; not an invoice.")
                    row("Estimated at reset", estimatedMoney(estimate.estimatedConsumptionUsd))
                    row("Average per day (estimated)", estimatedMoney(estimate.averageDailyConsumptionUsd))
                    row("Estimate period starts", utcTimestampText(estimate.periodStartUtc))
                    row("Estimate resets", utcTimestampText(estimate.resetAtUtc))
                    row("Estimate observed at", utcTimestampText(estimate.observedAtUtc))
                    if let reason = estimate.unavailableReason { row("Estimate unavailable", reason) }
                }
            }
            .font(.caption).textSelection(.enabled).padding(.top, 8)
        }
        .disclosureGroupStyle(FullWidthDisclosureGroupStyle(accessibilityIdentifier: account.key + "_AdvancedDetailsToggle"))
    }

    private func row(_ label: String, _ value: String) -> some View {
        GridRow { Text(label).foregroundStyle(.secondary); Text(value) }
    }
}

struct AccountsView: View {
    @ObservedObject var model: AppModel
    var body: some View {
        VStack(alignment: .leading) {
            HStack {
                Text("Accounts").font(.largeTitle)
                Spacer()
                Button("Add Account...") { model.addAccount() }.disabled(model.busy)
            }.padding([.horizontal, .top], 24)
            if let account = model.dashboard?.accounts.first(where: { $0.key == model.selectedAccount }) {
                Button("All Accounts") { model.editAccount(nil) }.padding(.horizontal, 24)
                AccountEditor(model: model, form: model.preferences, account: account).id(account.key)
            } else {
                List(model.dashboard?.accounts ?? []) { account in
                    AccountManagementRow(account: account) { model.editAccount(account.key) }
                        .listRowSeparator(.hidden).listRowBackground(Color.clear)
                }
                if model.dashboard?.accounts.isEmpty == true {
                    Text("No connected accounts.").foregroundStyle(.secondary).padding(24)
                }
            }
        }
    }
}

struct AccountManagementRow: View {
    let account: AccountData
    let manage: () -> Void

    var body: some View {
        Button(action: manage) {
            GroupBox {
                VStack(alignment: .leading, spacing: 6) {
                    AccountRow(account: account)
                    HStack(spacing: 4) {
                        Spacer()
                        Text("Manage account")
                        Image(systemName: "chevron.right").accessibilityHidden(true)
                    }
                    .font(.caption).foregroundStyle(Color.accentColor)
                }
                .padding(6).frame(maxWidth: .infinity, alignment: .leading)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help("Manage \(account.name) on \(account.host)")
        .accessibilityLabel("Manage account \(account.name) on \(account.host)")
        .accessibilityHint("Open account preferences and diagnostics")
        .accessibilityIdentifier(account.key + "_ManageAccount")
    }
}

struct PreferenceBounds: PreferenceKey {
    static var defaultValue: [String: Anchor<CGRect>] { [:] }
    static func reduce(value: inout [String: Anchor<CGRect>], nextValue: () -> [String: Anchor<CGRect>]) {
        value.merge(nextValue(), uniquingKeysWith: { _, latest in latest })
    }
}

struct PreferenceInput: View {
    let title: String
    @Binding var text: String
    let error: String?
    let identifier: String
    var prompt: String = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            TextField(title, text: $text, prompt: Text(prompt))
                .textFieldStyle(.roundedBorder)
                .overlay(RoundedRectangle(cornerRadius: 5).stroke(error == nil ? Color.clear : Color.red, lineWidth: 1))
                .accessibilityIdentifier(identifier)
                .accessibilityHint(error ?? "")
            if let error {
                Label(error, systemImage: "exclamationmark.circle.fill")
                    .font(.caption).foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier(identifier + "Error")
                    .anchorPreference(key: PreferenceBounds.self, value: .bounds) { [identifier + "Error": $0] }
            }
        }
    }
}

struct PreferenceActions: View {
    @ObservedObject var model: AppModel
    @ObservedObject var form: PreferencesDraft
    var body: some View {
        HStack {
            if form.saving { ProgressView().controlSize(.small) }
            Text(form.saving ? "Saving..." : form.dirty ? "Unsaved changes" : "No unsaved changes")
                .font(.caption).foregroundStyle(.secondary)
                .accessibilityIdentifier("PreferencesStatus")
            Spacer()
            Button("Cancel") { model.cancelPreferences() }
                .disabled(!form.loaded || !form.dirty || form.saving)
                .accessibilityIdentifier("CancelPreferences")
                .anchorPreference(key: PreferenceBounds.self, value: .bounds) { ["CancelPreferences": $0] }
            Button("Save") { model.savePreferences() }
                .buttonStyle(.borderedProminent)
                .disabled(!form.loaded || !form.dirty || model.busy || form.saving)
                .accessibilityIdentifier("SavePreferences")
                .anchorPreference(key: PreferenceBounds.self, value: .bounds) { ["SavePreferences": $0] }
        }
        .padding(16).background(.bar)
    }
}

struct AccountEditor: View {
    @ObservedObject var model: AppModel
    @ObservedObject var form: PreferencesDraft
    let account: AccountData
    @State private var removing = false
    @State private var budgetPreview = ""
    @State private var previewRevision = 0
    var body: some View {
        VStack(spacing: 0) {
          Form {
            Section {
                AccountRow(account: account)
                AccountDiagnosticsView(account: account)
                HStack {
                    Button("Refresh") { model.perform("account.refresh", fields: ["key": account.key]) }
                    Button("Reconnect...") { model.reconnectAccount(account) }
                    Button("Manage OAuth Grants") { model.openURL("https://\(account.host)/settings/applications") }
                }.disabled(model.busy)
            }
            Section("Account Preferences") {
                PreferenceInput(title: "Display name", text: $form.account.name, error: form.errors[.name],
                                identifier: "AccountDisplayName")
                PreferenceInput(title: "Percentage alerts", text: $form.account.thresholds,
                                error: form.errors[.accountThresholds], identifier: "AccountThresholds",
                                prompt: "Inherit global thresholds")
                Toggle("Inherit global USD increment", isOn: $form.account.inherit)
                if !form.account.inherit {
                    PreferenceInput(title: "USD increment (0 disables)", text: $form.account.increment,
                                    error: form.errors[.accountIncrement], identifier: "AccountIncrement")
                }
                Toggle("Show estimated period consumption", isOn: $form.account.showPeriodEstimate)
                    .accessibilityIdentifier("ShowPeriodEstimate")
                Text("Estimate consumption at the end of the UTC calendar month using your average pace so far. Actual consumption may differ.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Usage Budget") {
                Picker("Tracking target", selection: $form.account.useCustomBudget) {
                    Text("Use API allocation").tag(false)
                    Text("Use a custom budget").tag(true)
                }
                .pickerStyle(.radioGroup).accessibilityIdentifier("UsageBudgetMode")
                if form.account.useCustomBudget {
                    PreferenceInput(title: "Custom budget (USD)", text: $form.account.budget,
                                    error: form.errors[.budget], identifier: "CustomBudgetUsd", prompt: "50.00")
                    Text(budgetPreview).font(.caption).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true).accessibilityIdentifier("BudgetPreview")
                }
                Text("Budgets are tracking targets, not spending caps or invoices. Usage, menu-bar percentages, estimates, and percentage alerts use the saved target. Billing-period rules and the UTC calendar fallback are unchanged.")
                    .font(.caption).foregroundStyle(.secondary)
                Text("API allocation: \(account.details.unlimited ? "Unlimited" : money(account.details.observedAllocationUsd))")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section {
                Button("Remove Account...", role: .destructive) {
                    model.leaveForm { removing = true }
                }.disabled(model.busy)
            }
          }.formStyle(.grouped).disabled(!form.loaded || form.saving)
          Divider()
          PreferenceActions(model: model, form: form)
        }
        .onAppear {
            model.beginPreferences(accountKey: account.key)
            preview()
        }
        .onChange(of: form.account.budget) { _, _ in preview() }
        .onChange(of: form.account.useCustomBudget) { _, _ in preview() }
        .onChange(of: form.loaded) { _, _ in preview() }
        .onReceive(model.$dashboard) { _ in preview() }
        .confirmationDialog("Remove \(account.name) from this Mac?", isPresented: $removing) {
            Button("Remove Account", role: .destructive) {
                model.perform("account.remove", fields: ["key": account.key]) { event in
                    if event.error == nil { model.selectedAccount = nil; model.notice = "Account removed from this Mac." }
                }
            }
        } message: {
            Text("This deletes its local credentials, alert state, and cached avatar. Retained history and recovery copies may remain. It does not revoke the OAuth grant on GitHub.")
        }
    }

    private func preview() {
        previewRevision += 1
        let revision = previewRevision
        guard form.loaded else { budgetPreview = "Loading preferences..."; return }
        guard form.account.useCustomBudget else { budgetPreview = ""; return }
        var fields: [String: Any] = ["key": account.key]
        if form.account.useCustomBudget {
            guard let amount = try? parseUSD(form.account.budget, budget: true) else {
                budgetPreview = "Enter a valid custom budget to preview usage."
                return
            }
            fields["customBudgetUsd"] = NSDecimalNumber(decimal: amount)
        }
        budgetPreview = "Updating preview..."
        model.send("account.budget.preview", fields: fields) { event in
            if revision == previewRevision { budgetPreview = event.text ?? event.error ?? "Preview unavailable." }
        }
    }
}

struct PreferencesView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var form: PreferencesDraft
    let notifications: Bool
    @State private var trayPreview: TrayPresentation?
    @State private var previewRevision = 0
    var body: some View {
        VStack(spacing: 0) {
          Form {
            if notifications {
                Section("macOS Permission") {
                    if model.demo {
                        Text("Notification permissions and delivery are disabled in demonstration mode.")
                            .foregroundStyle(.secondary)
                    } else if let permission = model.notificationPermission {
                        Label(permission.title, systemImage: permission.canSend ? "bell.badge" : "bell.slash")
                            .font(.headline)
                        Text(permission.explanation).font(.caption).foregroundStyle(.secondary)
                        HStack {
                            Button(permission.actionTitle) { Task { await model.configureNotifications() } }
                                .disabled(model.notificationBusy)
                            if model.notificationBusy { ProgressView().controlSize(.small) }
                        }
                    } else {
                        ProgressView("Checking notification permission...").controlSize(.small)
                    }
                }
                Section("Notifications") {
                    Toggle("Enable consumption alerts", isOn: $form.global.enabled)
                    PreferenceInput(title: "Percentage thresholds", text: $form.global.thresholds,
                                    error: form.errors[.thresholds], identifier: "GlobalThresholds")
                    Text("Separate positive percentages with commas. Defaults: 50, 80, 100. Alerts use each account's custom budget or API allocation; values above 100 are supported.").font(.caption).foregroundStyle(.secondary)
                    PreferenceInput(title: "USD increment", text: $form.global.increment,
                                    error: form.errors[.increment], identifier: "GlobalIncrement", prompt: "Disabled")
                    Text("For example, 50 alerts at $50, $100, and so on. Per-account preferences can override or disable these alerts.")
                        .font(.caption).foregroundStyle(.secondary)
                    Button("Send Test Notification") { Task { await model.testNotification() } }
                        .disabled(model.demo || model.notificationBusy || model.notificationPermission == nil ||
                                  model.notificationPermission == .denied || model.notificationPermission == .unavailable)
                    Text("macOS permission and Focus settings determine whether notifications appear.").font(.caption).foregroundStyle(.secondary)
                }
            } else {
                Section("General") {
                    PreferenceInput(title: "Refresh interval (minutes)", text: $form.global.minutes,
                                    error: form.errors[.minutes], identifier: "PollMinutes")
                    Text("From 5 to 1440 minutes; default 10.").font(.caption).foregroundStyle(.secondary)
                    Toggle("Launch at login", isOn: $form.global.startup).disabled(model.settings?.canChangeStartup != true || model.demo)
                    Text(model.settings?.startupDescription ?? "").font(.caption).foregroundStyle(.secondary)
                    Button("Open Login Items Settings") { SMAppService.openSystemSettingsLoginItems() }.disabled(model.demo)
                    Button("Open Data Folder") { model.openDataFolder() }
                }
                Section("Menu Bar") {
                    Text("Show fresh API allocation or custom budget usage independently of dollar totals. New accounts are included by default.")
                        .font(.caption).foregroundStyle(.secondary)
                    Picker("Icon style", selection: $form.global.trayStyle) {
                        Text("Pie chart").tag(TrayIconStyle.pie)
                        Text("Percentage number").tag(TrayIconStyle.percentage)
                    }
                    Picker("Icons to show", selection: $form.global.trayMode) {
                        Text("One roll-up icon").tag(TrayDisplayMode.rollUp)
                        Text("One icon per selected account").tag(TrayDisplayMode.perAccount)
                    }
                    ForEach(model.dashboard?.accounts ?? []) { account in
                        Toggle("\(account.name) (\(account.host))", isOn: Binding(
                            get: { !form.global.excludedAccounts.contains(account.key) },
                            set: { include in
                                if include { form.global.excludedAccounts.remove(account.key) }
                                else { form.global.excludedAccounts.insert(account.key) }
                            }))
                    }
                    Text("Live Preview").font(.headline)
                    if let preview = trayPreview {
                        ForEach(preview.icons) { icon in
                            HStack {
                                Image(nsImage: TrayIconRenderer.image(icon, style: preview.style))
                                    .help(icon.details).accessibilityLabel(icon.details)
                                Text("\(icon.accountKey == nil ? "Roll-up" : icon.name): \(icon.valueText)" +
                                     (icon.isPartial ? " (partial)" : "") + (icon.isOverAllocation ? " (over target)" : ""))
                                    .font(.caption)
                            }
                        }
                    }
                    Text("Preview changes apply to the menu bar only after Save. Custom budgets provide finite targets even when API allocation is unlimited or unknown. \u{221e} means unlimited allocation without a custom target; mixed roll-ups show only finite targets. ! marks partial usage or an unavailable pie; ? means unavailable in percentage mode; + means over target. An empty pie with ! is unavailable, not 0%. Numbers are rounded; hover for details.")
                        .font(.caption).foregroundStyle(.secondary)
                    Text("A neutral icon remains when nothing is selected. macOS may hide icons on a crowded menu bar; reopen GHCPSpendTray to access Settings.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
          }.formStyle(.grouped).disabled(!form.loaded || form.saving)
          Divider()
          PreferenceActions(model: model, form: form)
        }.onAppear { load() }
        .onChange(of: notifications) { _, _ in load() }
        .onChange(of: form.global.trayStyle) { _, _ in preview() }
        .onChange(of: form.global.trayMode) { _, _ in preview() }
        .onChange(of: form.global.excludedAccounts) { _, _ in preview() }
        .onReceive(model.$dashboard) { _ in preview() }
        .task(id: notifications) { if notifications { await model.refreshNotificationPermission() } }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            if notifications { Task { await model.refreshNotificationPermission() } }
        }
    }

    private func load() {
        model.beginPreferences()
        preview()
    }

    private func preview() {
        guard form.loaded && !notifications, var value = model.settings else { return }
        value.trayStyle = form.global.trayStyle
        value.trayMode = form.global.trayMode
        value.excludedTrayAccounts = form.global.excludedAccounts.sorted()
        previewRevision += 1
        let revision = previewRevision
        do {
            model.send("tray.preview", fields: ["settings": try jsonObject(value)]) { event in
                if revision == previewRevision { trayPreview = event.tray }
            }
        } catch { model.error = "Could not prepare the menu-bar preview." }
    }

}

struct SignInView: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text(model.reconnect == nil ? "Connect Account" : "Reconnect Account").font(.title)
            StatusMessage(model: model)
            if model.editingHost {
                Form {
                    TextField("GitHub host", text: Binding(get: { model.signInHost }, set: { model.setHost($0) }))
                        .textFieldStyle(.roundedBorder)
                        .disabled(model.signingIn || model.reconnect != nil)
                    TextField("OAuth client ID", text: $model.signInClientId, prompt: Text("Built in for github.com and msft.ghe.com"))
                        .textFieldStyle(.roundedBorder)
                        .disabled(model.signingIn || model.reconnectClientId != nil)
                    Toggle("Request offline access (refresh tokens)", isOn: $model.offlineAccess).disabled(model.signingIn)
                }
                Text(model.hostDescription).font(.caption).textSelection(.enabled)
                Text("For other enterprise hosts, use an approved host-specific OAuth app with Device Flow enabled. No client secret is needed.")
                    .font(.caption).foregroundStyle(.secondary)
                Button("Start Sign-In") { model.startSignIn() }.buttonStyle(.borderedProminent)
                    .disabled(model.busy || model.signInHost.isEmpty)
            } else {
                HStack {
                    Text("Signing in to \(model.signInHost)").font(.headline)
                    Spacer()
                    if model.reconnect == nil && !model.connectingAccount {
                        Button("Change Host") { model.changeHost() }
                    }
                }
            }
            if let prompt = model.prompt {
                Text("Enter this code on the authorization page:").font(.headline)
                HStack {
                    Text(prompt.code).font(.system(.title, design: .monospaced)).textSelection(.enabled)
                    Button(model.codeCopied ? "Copy Again" : "Copy Code") { model.copyCode() }
                }
                if model.codeCopied { Text("Code copied to clipboard.").font(.caption).foregroundStyle(.secondary) }
                if let error = model.clipboardError { Text(error).font(.caption).foregroundStyle(.orange) }
                Text(prompt.verificationUri).textSelection(.enabled).font(.caption)
                Button("Open Authorization Page") { model.openURL(prompt.verificationUri) }
                TimelineView(.periodic(from: .now, by: 1)) { context in
                    let seconds = max(0, Int((dateValue(prompt.expires) ?? context.date).timeIntervalSince(context.date)))
                    Text("Code expires in \(seconds / 60)m \(seconds % 60)s. Verify the OAuth app before approving.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            } else if model.signingIn && !model.editingHost {
                HStack {
                    ProgressView().controlSize(.small)
                    Text(model.connectingAccount ? "Finishing your connection..." : "Generating a sign-in code...")
                }
            } else if !model.editingHost {
                Button("Try Again") { model.startSignIn() }.buttonStyle(.borderedProminent).disabled(model.busy)
            }
            HStack {
                Spacer()
                Button(model.signingIn ? "Cancel Sign-In" : "Close") { model.cancelSignIn(); dismiss() }
                    .keyboardShortcut(.cancelAction)
            }
        }
        .padding(24).frame(width: 540)
        .interactiveDismissDisabled(model.signingIn)
    }
}

struct AboutView: View {
    @ObservedObject var model: AppModel
    var body: some View {
        ScrollView {
            VStack(spacing: 16) {
                Image(nsImage: NSApplication.shared.applicationIconImage).resizable().frame(width: 96, height: 96)
                Text("GHCPSpendTray").font(.largeTitle)
                Text("macOS \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "Development")")
                Text(Bundle.main.object(forInfoDictionaryKey: "GHCPReleaseChannel") as? String ?? "Development")
                    .foregroundStyle(.secondary)
                Text("An independent GitHub Copilot AI-credit consumption monitor.\nNot affiliated with or endorsed by GitHub.")
                    .multilineTextAlignment(.center)
                Text("C# / .NET Native AOT shared engine. Native SwiftUI frontend.").font(.caption).foregroundStyle(.secondary)
                HStack {
                    Button("GitHub Releases") { model.openURL("https://github.com/DamianEdwards/ghcp-spend-tray/releases") }
                    Button("Privacy Policy") { model.openURL("https://github.com/DamianEdwards/ghcp-spend-tray/blob/main/PRIVACY.md") }
                    Button("Report an Issue") { model.openURL("https://github.com/DamianEdwards/ghcp-spend-tray/issues") }
                }
                UpdatePreferences(updates: model.updates)
                Text("MIT License. Copyright (c) 2026 Damian Edwards.").font(.caption)
            }.padding(28).frame(maxWidth: .infinity)
        }
    }
}
