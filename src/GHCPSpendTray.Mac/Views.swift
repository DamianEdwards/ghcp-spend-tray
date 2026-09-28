import AppKit
import ServiceManagement
import SwiftUI

enum SettingsPage: String, CaseIterable, Identifiable {
    case usage = "Usage", accounts = "Accounts", general = "General", notifications = "Notifications", about = "About"
    var id: String { rawValue }
    var icon: String {
        switch self {
        case .usage: return "chart.bar"
        case .accounts: return "person.crop.circle"
        case .general: return "gear"
        case .notifications: return "bell"
        case .about: return "info.circle"
        }
    }
}

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
                    .accessibilityLabel("Allocation consumed")
                    .accessibilityValue("\(decimalText(percent)) percent")
                Text("\(decimalText(percent))% of \(money(account.allocationUsd)) allocation")
                    .font(.caption).foregroundStyle(.secondary)
            } else {
                Text(account.details.unlimited ? "Unlimited allocation" : "Allocation unavailable")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Text(account.freshness).font(.caption)
                .foregroundStyle(account.freshness == "Fresh" ? Color.secondary : Color.orange)
            if let message = account.details.message { Text(message).font(.caption).foregroundStyle(.orange) }
        }
        .padding(.vertical, 5)
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

struct FlyoutView: View {
    @ObservedObject var model: AppModel
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Image(systemName: "gauge.with.dots.needle.67percent")
                Text("Copilot consumption").font(.headline)
                Spacer()
                Button { model.openSettings() } label: { Image(systemName: "gearshape") }
                    .help("Open Settings").accessibilityLabel("Open Settings")
            }
            HStack(alignment: .firstTextBaseline) {
                Text(money(model.dashboard?.consumptionUsd)).font(.largeTitle).monospacedDigit()
                Text("month to date").foregroundStyle(.secondary)
            }
            if let dashboard = model.dashboard, !dashboard.isComplete {
                Text(dashboard.isLastKnown ? "Last-known / partial consumption" : "Partial / unavailable consumption")
                    .font(.caption).foregroundStyle(.orange)
            }
            StatusMessage(model: model)
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    ForEach(model.dashboard?.accounts ?? []) { account in
                        Button {
                            model.selectedAccount = account.key
                            model.openSettings(.accounts)
                        } label: { AccountRow(account: account) }
                            .buttonStyle(.plain)
                        Divider()
                    }
                }
            }
            .frame(maxHeight: 330)
            if model.dashboard?.accounts.isEmpty == true {
                Text("Connect a GitHub account to monitor AI-credit consumption.").foregroundStyle(.secondary)
                Button("Connect Account...") { model.addAccount() }.buttonStyle(.borderedProminent)
                    .disabled(!model.initialized || model.busy)
            }
            HStack {
                Button("Refresh Now") { model.perform("refresh") }.disabled(!model.initialized || model.busy)
                if model.busy { ProgressView().controlSize(.small) }
                Spacer()
                Button("Quit") { NSApplication.shared.terminate(nil) }.keyboardShortcut("q")
            }
        }
        .padding(18).frame(width: 400)
    }
}

struct SettingsView: View {
    @ObservedObject var model: AppModel
    var body: some View {
        NavigationSplitView {
            List(SettingsPage.allCases, selection: $model.page) { page in
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
                    case .general: PreferencesView(model: model, notifications: false)
                    case .notifications: PreferencesView(model: model, notifications: true)
                    case .about: AboutView(model: model)
                    }
                }
            }.frame(minWidth: 500)
        }
        .frame(minWidth: 730, minHeight: 550)
        .sheet(isPresented: $model.showingSignIn, onDismiss: { model.cancelSignIn() }) {
            SignInView(model: model)
        }
    }
}

struct UsageView: View {
    @ObservedObject var model: AppModel
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                Text("Usage").font(.largeTitle)
                Text(model.dashboard?.total ?? "Consumption unavailable").font(.title2)
                Text(model.dashboard?.status ?? "").foregroundStyle(.secondary)
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

struct AccountDiagnosticsView: View {
    let account: AccountData
    var body: some View {
        DisclosureGroup("Advanced Details") {
            Grid(alignment: .leading, horizontalSpacing: 24, verticalSpacing: 8) {
                row("Credits used (observed)", decimalText(account.details.creditsUsed).isEmpty ? "Unavailable" : decimalText(account.details.creditsUsed))
                row("Consumption (observed)", money(account.details.observedConsumptionUsd))
                row("Allocation (observed)", account.details.unlimited ? "Unlimited" : money(account.details.observedAllocationUsd))
                row("Percent (observed)", account.details.observedPercentConsumed.map { "\(decimalText($0))%" } ?? "Unavailable")
                row("Current billing period", account.details.isCurrentPeriod ? "Yes" : "No")
                row("Last fetched", dateText(account.updatedAt))
                row("Source timestamp", dateText(account.details.sourceTimestampUtc))
                row("Resets", dateText(account.details.resetAtUtc))
                row("Next refresh", dateText(account.details.nextRefreshUtc))
                row("Period", account.details.periodId ?? "Unavailable")
            }
            .font(.caption).textSelection(.enabled).padding(.top, 8)
        }
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
                Button("All Accounts") { model.selectedAccount = nil }.padding(.horizontal, 24)
                AccountEditor(model: model, account: account).id(account.key)
            } else {
                List(model.dashboard?.accounts ?? []) { account in
                    Button { model.selectedAccount = account.key } label: { AccountRow(account: account) }
                        .buttonStyle(.plain).padding(.vertical, 6)
                }
                if model.dashboard?.accounts.isEmpty == true {
                    Text("No connected accounts.").foregroundStyle(.secondary).padding(24)
                }
            }
        }
    }
}

struct AccountEditor: View {
    @ObservedObject var model: AppModel
    let account: AccountData
    @State private var name = ""
    @State private var thresholds = ""
    @State private var increment = ""
    @State private var inherit = true
    @State private var loaded = false
    @State private var removing = false
    var body: some View {
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
                TextField("Display name", text: $name)
                TextField("Percentage alerts", text: $thresholds, prompt: Text("Inherit global thresholds"))
                Toggle("Inherit global USD increment", isOn: $inherit)
                if !inherit { TextField("USD increment (0 disables)", text: $increment) }
                HStack {
                    Button("Save") {
                        do {
                            var fields: [String: Any] = ["key": account.key, "displayName": name, "thresholds": thresholds]
                            if !inherit { fields["spendIncrementUsd"] = NSDecimalNumber(decimal: try parseAmount(increment) ?? 0) }
                            model.perform("account.save", fields: fields)
                        } catch { model.error = error.localizedDescription }
                    }.disabled(model.busy || !loaded)
                    Spacer()
                    Button("Remove Account...", role: .destructive) { removing = true }.disabled(model.busy)
                }
            }
        }.formStyle(.grouped)
        .onAppear {
            model.send("account.preferences", fields: ["key": account.key]) { event in
                guard let value = event.preferences else { return }
                name = value.displayName
                thresholds = value.thresholds
                increment = decimalText(value.spendIncrementUsd)
                inherit = value.spendIncrementUsd == nil
                loaded = true
            }
        }
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
}

struct PreferencesView: View {
    @ObservedObject var model: AppModel
    let notifications: Bool
    @State private var minutes = "60"
    @State private var thresholds = "50, 80, 100"
    @State private var increment = ""
    @State private var enabled = true
    @State private var startup = false
    var body: some View {
        Form {
            if notifications {
                Section("Notifications") {
                    Toggle("Enable consumption alerts", isOn: $enabled)
                    TextField("Percentage thresholds", text: $thresholds)
                    Text("Separate positive percentages with commas. Defaults: 50, 80, 100.").font(.caption).foregroundStyle(.secondary)
                    TextField("USD increment", text: $increment, prompt: Text("Disabled"))
                    Text("For example, 50 alerts at $50, $100, and so on. Per-account preferences can override or disable these alerts.")
                        .font(.caption).foregroundStyle(.secondary)
                    Button("Send Test Notification") { model.testNotification() }
                    Text("macOS permission and Focus settings determine whether notifications appear.").font(.caption).foregroundStyle(.secondary)
                }
            } else {
                Section("General") {
                    TextField("Refresh interval (minutes)", text: $minutes)
                    Text("From 5 to 1440 minutes; default 60.").font(.caption).foregroundStyle(.secondary)
                    Toggle("Launch at login", isOn: $startup).disabled(model.settings?.canChangeStartup != true || model.demo)
                    Text(model.settings?.startupDescription ?? "").font(.caption).foregroundStyle(.secondary)
                    Button("Open Login Items Settings") { SMAppService.openSystemSettingsLoginItems() }.disabled(model.demo)
                    Button("Open Data Folder") { model.openDataFolder() }
                }
            }
            Button("Save") { save() }.disabled(model.busy)
        }.formStyle(.grouped).onAppear { load() }
        .onChange(of: notifications) { _, _ in load() }
    }

    private func load() {
        guard let value = model.settings else { return }
        minutes = String(value.pollMinutes)
        thresholds = value.thresholds
        increment = decimalText(value.spendIncrementUsd)
        enabled = value.notifications
        startup = value.startup
    }

    private func save() {
        guard var value = model.settings else { return }
        do {
            if notifications {
                value.notifications = enabled
                value.thresholds = thresholds
                value.spendIncrementUsd = try parseAmount(increment)
            } else {
                guard let interval = Int(minutes), (5...1440).contains(interval) else {
                    throw AppError.message("Enter a refresh interval from 5 through 1440 minutes.")
                }
                value.pollMinutes = interval
                value.startup = startup
            }
            model.perform("settings.save", fields: ["settings": try jsonObject(value)]) { _ in load() }
        } catch { model.error = error.localizedDescription }
    }
}

struct SignInView: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var host = "github.com"
    @State private var clientId = ""
    @State private var offline = false
    @State private var checkedHost = ""
    @State private var destinations = ""
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text(model.reconnect == nil ? "Connect Account" : "Reconnect Account").font(.title)
            StatusMessage(model: model)
            Form {
                TextField("GitHub host", text: $host).disabled(model.signingIn || model.reconnect != nil)
                TextField("OAuth client ID", text: $clientId, prompt: Text("Built in for github.com and msft.ghe.com"))
                    .disabled(model.signingIn || model.reconnectClientId != nil)
                Toggle("Request offline access (refresh tokens)", isOn: $offline).disabled(model.signingIn)
            }
            Text("For other enterprise hosts, use an approved host-specific OAuth app with Device Flow enabled. No client secret is needed.")
                .font(.caption).foregroundStyle(.secondary)
            Button("Check Destinations") {
                let candidate = host
                model.send("host.describe", fields: ["host": candidate]) { event in
                    if let text = event.text { destinations = text; checkedHost = candidate }
                }
            }.disabled(model.signingIn)
            if !destinations.isEmpty && checkedHost == host {
                Text(destinations).font(.system(.body, design: .monospaced)).textSelection(.enabled)
            }
            if let prompt = model.prompt {
                Text("Enter this code on the authorization page:").font(.headline)
                HStack {
                    Text(prompt.code).font(.system(.title, design: .monospaced)).textSelection(.enabled)
                    Button("Copy Code") {
                        NSPasteboard.general.clearContents()
                        if !NSPasteboard.general.setString(prompt.code, forType: .string) { model.error = "Could not copy the device code." }
                    }
                }
                Text(prompt.verificationUri).textSelection(.enabled).font(.caption)
                Button("Open Authorization Page") { model.openURL(prompt.verificationUri) }
                TimelineView(.periodic(from: .now, by: 1)) { context in
                    let seconds = max(0, Int((dateValue(prompt.expires) ?? context.date).timeIntervalSince(context.date)))
                    Text("Code expires in \(seconds / 60)m \(seconds % 60)s. Verify the OAuth app before approving.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            } else if let identity = model.identity {
                Text("Confirm this account before saving:").font(.headline)
                Text("\(identity.login) @ \(identity.host)\nGitHub user ID: \(identity.userId)").textSelection(.enabled)
                Button("Confirm and Save Account") { model.send("signin.confirm", fields: ["accepted": true]) }
                    .buttonStyle(.borderedProminent)
            } else if model.signingIn {
                HStack { ProgressView().controlSize(.small); Text("Waiting for GitHub...") }
            } else {
                Button("Start Device Sign-In") { model.startSignIn(host: host, clientId: clientId, offline: offline) }
                    .buttonStyle(.borderedProminent).disabled(checkedHost != host || checkedHost.isEmpty || model.busy)
            }
            HStack {
                Spacer()
                Button(model.signingIn ? "Cancel Sign-In" : "Close") { model.cancelSignIn(); dismiss() }
                    .keyboardShortcut(.cancelAction)
            }
        }
        .padding(24).frame(width: 540)
        .onAppear { host = model.reconnect?.host ?? "github.com"; clientId = model.reconnectClientId ?? "" }
        .onChange(of: host) { _, _ in
            if model.reconnect == nil { clientId = "" }
            checkedHost = ""
            destinations = ""
        }
        .interactiveDismissDisabled(model.signingIn)
    }
}

struct AboutView: View {
    @ObservedObject var model: AppModel
    var body: some View {
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
            Text("Updates are manual: quit the app, then replace it with a newer macOS release. Settings and history remain on this Mac.")
                .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
            Text("MIT License. Copyright (c) 2026 Damian Edwards.").font(.caption)
        }.padding(28).frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
