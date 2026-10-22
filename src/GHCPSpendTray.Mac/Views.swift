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

struct AccountIdentityView: View {
    let account: AccountData
    var body: some View {
        HStack {
            AccountAvatar(account: account)
            VStack(alignment: .leading) {
                Text(account.name).font(.headline)
                Text("\(account.login) @ \(account.host)").font(.caption).foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

struct AccountRow: View {
    let account: AccountData
    var manage: (() -> Void)? = nil
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                AccountIdentityView(account: account)
                VStack(alignment: .trailing, spacing: 6) {
                    if account.actualUsage == nil {
                        Text(money(account.consumptionUsd)).font(.title3).monospacedDigit()
                    }
                    if let manage {
                        Button("Manage account", action: manage)
                            .accessibilityLabel("Manage account \(account.name) on \(account.host)")
                            .accessibilityIdentifier(account.key + "_UsageManageAccount")
                            .anchorPreference(key: PreferenceBounds.self, value: .bounds) {
                                [account.key + "_UsageManageAccount": $0]
                            }
                    }
                }
            }
            if let percent = account.percent {
                if let actual = account.actualUsage {
                    AccountUsageTrack(account: account, actual: actual)
                } else {
                    ProgressView(value: min(max(NSDecimalNumber(decimal: percent).doubleValue, 0), 100), total: 100)
                        .tint(percent >= 100 ? .red : .accentColor)
                        .accessibilityLabel("\(account.targetLabel) consumed")
                        .accessibilityValue("\(decimalText(percent)) percent")
                }
                Text(account.usageCaption)
                    .font(.caption).foregroundStyle(.secondary)
            } else {
                Text(account.allocationSummary)
                    .font(.caption).foregroundStyle(.secondary)
            }
            if let estimate = account.periodEstimate {
                PeriodEstimateRow(estimate: estimate, customBudget: account.customBudgetUsd != nil, showAmount: account.forecast == nil)
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
    var showAmount = true
    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            if showAmount {
                HStack(alignment: .firstTextBaseline) {
                    Text("Estimated at reset")
                    Spacer()
                    Text(estimatedMoney(estimate.estimatedConsumptionUsd)).monospacedDigit()
                        .accessibilityLabel("Estimated consumption: \(estimatedMoney(estimate.estimatedConsumptionUsd))")
                }
            }

            if let reason = estimate.unavailableReason {
                Text(reason).fixedSize(horizontal: false, vertical: true)
            } else {
                Text(estimate.summary(customBudget: customBudget)).fixedSize(horizontal: false, vertical: true)
                    .foregroundStyle(.secondary)
            }
        }
        .font(.caption).foregroundStyle(.secondary)
        .help(estimate.method)
        .accessibilityIdentifier("PeriodEstimate")
    }
}

private enum TrackingTargetCenter: AlignmentID {
    static func defaultValue(in context: ViewDimensions) -> CGFloat { context[VerticalAlignment.center] }
}

private extension VerticalAlignment {
    static let trackingTargetCenter = VerticalAlignment(TrackingTargetCenter.self)
}

private struct UsageTrackLayout: Layout {
    let actualFraction: Double
    let forecastFraction: Double?

    private func sizes(width: CGFloat, subviews: Subviews) -> (target: CGSize, actual: CGSize, forecast: CGSize, plot: CGFloat, lineY: CGFloat) {
        func fitted(_ index: Int, maximum: CGFloat) -> CGSize {
            let ideal = subviews[index].sizeThatFits(.unspecified)
            return subviews[index].sizeThatFits(ProposedViewSize(width: min(ideal.width, maximum), height: nil))
        }
        let target = fitted(3, maximum: min(140, width * 0.4))
        let plot = max(0, width - target.width - 12)
        let actual = fitted(1, maximum: plot)
        let forecast = fitted(2, maximum: plot)
        return (target, actual, forecast, plot, max(54, actual.height + 11))
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width ?? 352
        let size = sizes(width: width, subviews: subviews)
        return CGSize(width: width, height: size.lineY + max(28, forecastFraction == nil ? 0 : size.forecast.height + 21))
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        // Subviews are the drawing, current amount, optional forecast, and fixed target.
        let size = sizes(width: bounds.width, subviews: subviews)
        func place(_ index: Int, x: CGFloat, y: CGFloat, size: CGSize) {
            subviews[index].place(at: CGPoint(x: bounds.minX + x, y: bounds.minY + y),
                                 anchor: .topLeading, proposal: ProposedViewSize(size))
        }
        func left(_ fraction: Double, width: CGFloat) -> CGFloat {
            let marker = 2 + CGFloat(fraction) * max(0, size.plot - 4)
            return min(max(0, marker - width / 2), max(0, size.plot - width))
        }
        place(0, x: 0, y: size.lineY - 9, size: CGSize(width: size.plot, height: 23))
        place(1, x: left(actualFraction, width: size.actual.width), y: size.lineY - 11 - size.actual.height, size: size.actual)
        place(2, x: left(forecastFraction ?? 0, width: size.forecast.width), y: size.lineY + 17, size: size.forecast)
        let targetCenter = subviews[3].dimensions(in: ProposedViewSize(size.target))[VerticalAlignment.trackingTargetCenter]
        place(3, x: size.plot + 12, y: size.lineY - targetCenter, size: size.target)
    }
}

struct AccountUsageTrack: View {
    let account: AccountData
    let actual: UsagePositionData
    private var tint: Color {
        switch account.forecast?.band ?? .under {
        case .under: .accentColor
        case .near: .orange
        case .over: .red
        }
    }
    private var description: String {
        guard let forecast = account.forecast, let estimate = account.periodEstimate else { return account.allocationSummary }
        let status = forecast.band == .under ? "Under target" : forecast.band == .near ? "Near target" : "At or over target"
        return "\(account.allocationSummary). Estimated at reset: \(estimatedMoney(estimate.estimatedConsumptionUsd)) (\(percentageText(forecast.percent)) of \(account.targetLabel)). \(status). \(estimate.method)"
    }
    var body: some View {
        UsageTrackLayout(actualFraction: actual.fraction, forecastFraction: account.forecast?.forecastFraction) {
            Canvas { context, size in
                let width = max(0, size.width - 4)
                let actualX = 2 + CGFloat(actual.fraction) * width
                func line(_ from: CGPoint, _ to: CGPoint, color: Color, style: StrokeStyle) {
                    var path = Path()
                    path.move(to: from)
                    path.addLine(to: to)
                    context.stroke(path, with: .color(color), style: style)
                }
                line(CGPoint(x: 2, y: 9), CGPoint(x: size.width - 2, y: 9),
                     color: .secondary.opacity(0.4), style: StrokeStyle(lineWidth: 1))
                line(CGPoint(x: 2, y: 9), CGPoint(x: actualX, y: 9),
                     color: .accentColor, style: StrokeStyle(lineWidth: 4))
                if let forecast = account.forecast {
                    let endX = 2 + CGFloat(forecast.forecastFraction) * width
                    line(CGPoint(x: actualX, y: 9), CGPoint(x: endX, y: 9),
                         color: tint, style: StrokeStyle(lineWidth: 4, dash: [12, 4]))
                    line(CGPoint(x: endX, y: 4), CGPoint(x: endX, y: 23),
                         color: tint, style: StrokeStyle(lineWidth: 1.5))
                }
                line(CGPoint(x: actualX, y: 0), CGPoint(x: actualX, y: 16),
                     color: .secondary, style: StrokeStyle(lineWidth: 1.5))
            }
            .anchorPreference(key: PreferenceBounds.self, value: .bounds) { ["UsageDrawing": $0] }
            .accessibilityHidden(true)
            VStack(alignment: .center, spacing: 0) {
                HStack(spacing: 6) {
                    if actual.band != .under {
                        Image(systemName: actual.band == .near ? "exclamationmark.triangle.fill" : "xmark.octagon.fill")
                            .foregroundStyle(actual.band == .near ? Color.orange : Color.red)
                            .accessibilityLabel(actual.band == .near ? "Current usage is near the target" : "Current usage is at or over the target")
                            .anchorPreference(key: PreferenceBounds.self, value: .bounds) { ["ActualUsageWarning": $0] }
                    }
                    Text(money(account.consumptionUsd)).font(.title2).bold().monospacedDigit()
                        .frame(minWidth: 0, maxWidth: .infinity)
                }
                .transformAnchorPreference(key: PreferenceBounds.self, value: .bounds) { $0["ActualUsageContent"] = $1 }
                Text("used").font(.system(size: 10)).foregroundStyle(.secondary)
                    .anchorPreference(key: PreferenceBounds.self, value: .bounds) { ["ActualUsageLabel": $0] }
            }
            .lineLimit(1)
            .minimumScaleFactor(0.5)
            .transformAnchorPreference(key: PreferenceBounds.self, value: .bounds) { $0["ActualUsageCallout"] = $1 }
            .accessibilityElement(children: .combine)
            .help("Current usage: \(money(account.consumptionUsd)). \(account.allocationSummary)")
            if let forecast = account.forecast, let estimate = account.periodEstimate {
                VStack(alignment: .center, spacing: 0) {
                    Text(estimatedMoney(estimate.estimatedConsumptionUsd)).font(.callout).bold().monospacedDigit()
                        .foregroundStyle(.primary)
                        .frame(minWidth: 0, maxWidth: .infinity)
                        .anchorPreference(key: PreferenceBounds.self, value: .bounds) { ["ForecastAmount": $0] }
                    Text("est. total").font(.system(size: 11)).foregroundStyle(.secondary)
                        .anchorPreference(key: PreferenceBounds.self, value: .bounds) { ["ForecastLabel": $0] }
                    if forecast.band == .over {
                        Text("(\(percentageText(forecast.percent)))").font(.system(size: 10)).foregroundStyle(.secondary)
                            .frame(minWidth: 0, maxWidth: .infinity)
                    }
                }
                .lineLimit(1)
                .minimumScaleFactor(0.75)
                .transformAnchorPreference(key: PreferenceBounds.self, value: .bounds) { $0["ForecastCallout"] = $1 }
                .accessibilityElement(children: .ignore)
                .accessibilityLabel(description)
            } else {
                Color.clear.frame(width: 0, height: 0).accessibilityHidden(true)
            }
            VStack(alignment: .center, spacing: 0) {
                Text(trackingTargetMoney(account.customBudgetUsd ?? account.allocationUsd))
                    .font(.system(size: 16, weight: .semibold)).monospacedDigit()
                    .foregroundStyle(.primary)
                    .frame(minWidth: 0, maxWidth: .infinity)
                    .anchorPreference(key: PreferenceBounds.self, value: .bounds) { ["TargetAmount": $0] }
                    .alignmentGuide(.trackingTargetCenter) { $0[VerticalAlignment.center] }
                Text(account.trackingTargetLabel).font(.system(size: 11)).foregroundStyle(.secondary)
                    .anchorPreference(key: PreferenceBounds.self, value: .bounds) { ["TargetLabel": $0] }
            }
            .lineLimit(1)
            .minimumScaleFactor(0.6)
            .transformAnchorPreference(key: PreferenceBounds.self, value: .bounds) { $0["TrackingTarget"] = $1 }
            .accessibilityElement(children: .combine)
            .accessibilityLabel("\(money(account.customBudgetUsd ?? account.allocationUsd)) \(account.targetLabel)")
            .accessibilityIdentifier("TrackingTarget")
            .help("\(money(account.customBudgetUsd ?? account.allocationUsd)) \(account.targetLabel)")
        }
        .accessibilityElement(children: .contain)
        .help(description)
        .accessibilityIdentifier("ForecastTrack")
        .transformAnchorPreference(key: PreferenceBounds.self, value: .bounds) { $0["ForecastTrack"] = $1 }
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
                Text("Copilot usage").font(.headline)
                Spacer()
                Button { model.openSettings() } label: { Image(systemName: "gearshape") }
                    .help("Open Settings").accessibilityLabel("Open Settings")
            }
            if noAccounts {
                StatusMessage(model: model)
                AccountSetupPrompt(model: model, alignment: .leading).padding(.vertical, 8)
            } else {
                if (model.dashboard?.accounts.count ?? 0) > 1 {
                    HStack(alignment: .firstTextBaseline) {
                        Text(money(model.dashboard?.consumptionUsd)).font(.largeTitle).monospacedDigit()
                        Text("month to date").foregroundStyle(.secondary)
                    }
                }
                if let dashboard = model.dashboard, !dashboard.isComplete {
                    Text(dashboard.isLastKnown ? "Last-known / partial consumption" : "Partial / unavailable consumption")
                        .font(.caption).foregroundStyle(.orange)
                }
                if (model.dashboard?.accounts.count ?? 0) > 1, let tray = model.dashboard?.tray {
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
                                model.viewUsage(account.key)
                            } label: { AccountRow(account: account) }
                                .buttonStyle(.plain)
                                .help("View usage for \(account.name)")
                                .accessibilityIdentifier(account.key + "_ViewUsage")
                            DailyConsumptionChartView(account: account, compact: true)
                            Divider()
                        }
                    }
                    .transformAnchorPreference(key: PreferenceBounds.self, value: .bounds) { $0["FlyoutAccountsContent"] = $1 }
                }
                .frame(maxHeight: model.dashboard?.accounts.count == 1 ? 430 : 330)
                .transformAnchorPreference(key: PreferenceBounds.self, value: .bounds) { $0["FlyoutAccountsViewport"] = $1 }
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
            List(SettingsPage.allCases, selection: Binding(get: { model.page }, set: { model.selectPage($0) })) { page in
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
        ScrollViewReader { proxy in
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
                                AccountRow(account: account) { model.selectAccount(account.key) }
                                DailyConsumptionChartView(account: account)
                                AccountDiagnosticsView(account: account)
                            }.padding(6).frame(maxWidth: .infinity, alignment: .leading)
                        }.id(account.key)
                    }
                    Text("USD consumption is AI credits used divided by 100, not an invoice or total GitHub spend. Locally sampled history is not a verified daily breakdown.")
                        .font(.caption).foregroundStyle(.secondary)
                }.padding(24).frame(maxWidth: .infinity, alignment: .leading)
            }
            .onAppear {
                if let key = model.usageAccount { proxy.scrollTo(key, anchor: .top) }
            }
            .onChange(of: model.usageNavigationRevision) { _, _ in
                if let key = model.usageAccount { proxy.scrollTo(key, anchor: .top) }
            }
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
                row("Limit (observed)", account.details.unlimited ? "Unlimited" : money(account.details.observedAllocationUsd))
                if let budget = account.customBudgetUsd { row("Custom tracking budget", trackingTargetMoney(budget)) }
                row("Percent (observed)", account.details.observedPercentConsumed.map { percentageText($0) } ??
                    (account.details.unlimited ? "Not applicable (unlimited)" : "Unavailable"))
                row("Current billing period", account.details.isCurrentPeriod ? "Yes" : "No")
                row("Last fetched", dateText(account.updatedAt))
                row("Source timestamp", dateText(account.details.sourceTimestampUtc))
                row("Resets", dateText(account.details.resetAtUtc))
                row("Next refresh", dateText(account.details.nextRefreshUtc))
                row("Period", account.details.periodId ?? "Unavailable")
                if let estimate = account.periodEstimate {
                    row("Estimate method", estimate.method)
                    if let first = estimate.averageStartDate, let last = estimate.averageEndDate {
                        row("Averaging window", "\(dailyDateText(first)) - \(dailyDateText(last)) (UTC)")
                    }
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
                Button("All Accounts") { model.selectAccount(nil) }.disabled(model.saving || model.pendingNavigation)
                    .padding(.horizontal, 24)
                AccountEditor(model: model, account: account).id(account.key)
            } else {
                List(model.dashboard?.accounts ?? []) { account in
                    AccountManagementRow(account: account) { model.selectAccount(account.key) }
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
                    AccountIdentityView(account: account)
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

struct AccountEditor: View {
    @ObservedObject var model: AppModel
    let account: AccountData
    @State private var removing = false
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
                    }.disabled(model.busy || !model.formLoaded)
                }
                Section("Usage budget") {
                    Picker("Tracking target", selection: $model.accountDraft.useCustomBudget) {
                        Text("Use API limit").tag(false)
                        Text("Use a custom budget").tag(true)
                    }.pickerStyle(.radioGroup).accessibilityIdentifier("BudgetMode")
                    if model.accountDraft.useCustomBudget {
                        PreferenceTextField("Custom budget (USD)", text: $model.accountDraft.budget,
                                            error: model.fieldErrors["budget"], identifier: "CustomBudget")
                        Text(model.budgetPreview ?? "Preparing budget preview...")
                            .font(.caption).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                            .accessibilityIdentifier("BudgetPreview")
                    }
                    Text("Budgets are tracking targets, not spending caps. Consumption and percentage alerts use this target for the API billing period, with a calendar-month fallback. Choose Use API limit and Save to remove the custom target.")
                        .font(.caption).foregroundStyle(.secondary)
                    Text("API limit: \(account.details.unlimited ? "Unlimited" : money(account.details.observedAllocationUsd))")
                        .font(.caption).foregroundStyle(.secondary)
                }
                CorporateUsageSetup(model: model, account: account)
                Section("Account Preferences") {
                    PreferenceTextField("Display name", text: $model.accountDraft.name,
                                        error: model.fieldErrors["name"], identifier: "DisplayName")
                    PreferenceTextField("Percentage alerts", text: $model.accountDraft.thresholds,
                                        error: model.fieldErrors["thresholds"], identifier: "AccountThresholds",
                                        prompt: "Inherit global thresholds")
                    Toggle("Inherit global USD increment", isOn: $model.accountDraft.inheritIncrement)
                    if !model.accountDraft.inheritIncrement {
                        PreferenceTextField("USD increment (0 disables)", text: $model.accountDraft.increment,
                                            error: model.fieldErrors["increment"], identifier: "AccountIncrement")
                    }
                    Toggle("Show estimated period consumption", isOn: $model.accountDraft.showPeriodEstimate)
                        .accessibilityIdentifier("ShowPeriodEstimate")
                    Text("Estimate usage at reset using up to 7 recent reported days when available, otherwise the period-to-date pace. Actual usage may differ.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Section {
                    Button("Remove Account...", role: .destructive) { removing = true }.disabled(model.busy)
                }
            }.formStyle(.grouped).disabled(!model.formLoaded || model.saving || model.pendingNavigation)
            FormActions(model: model)
        }
        .onAppear { model.loadAccountDraft(account.key) }
        .confirmationDialog("Remove \(account.name) from this Mac?", isPresented: $removing) {
            Button("Remove Account", role: .destructive) {
                let remove = {
                    model.perform("account.remove", fields: ["key": account.key]) { event in
                        if event.error == nil { model.selectAccount(nil); model.notice = "Account removed from this Mac." }
                    }
                }
                if model.requestLeaving(remove) { remove() }
            }
        } message: {
            Text("This deletes its local credentials, alert state, and cached avatar. Retained history and recovery copies may remain. It does not revoke the OAuth grant on GitHub.")
        }
    }
}

struct CorporateUsageSetup: View {
    @ObservedObject var model: AppModel
    let account: AccountData
    var body: some View {
        if let consumption = account.enterpriseConsumption, consumption.eligible {
            Section("Corporate Daily Usage") {
                Text(consumption.message)
                switch consumption.state {
                case .cliMissing:
                    Link("Install Azure CLI", destination: URL(string: "https://aka.ms/installazurecli")!)
                    Button("Check Again") { model.perform("consumption.connect", fields: ["key": account.key]) }.disabled(model.busy)
                case .confirmIdentity:
                    Text(consumption.pendingIdentity?.username ?? consumption.pendingIdentity?.alias ?? "")
                        .font(.headline).textSelection(.enabled)
                    Button("Use This Account") {
                        model.perform("consumption.connect", fields: ["key": account.key, "confirm": true])
                    }.buttonStyle(.borderedProminent).disabled(model.busy)
                    Text("Using the wrong account? Run az login in a terminal, then check again.").font(.caption)
                    Button("Check Again") { model.perform("consumption.connect", fields: ["key": account.key]) }.disabled(model.busy)
                case .signInRequired:
                    Text("az login --scope \"2efaf292-00a0-426c-ba7d-f5d2b214b8fc/.default\"").textSelection(.enabled)
                    Button("I've Signed In - Try Again") {
                        model.perform("consumption.connect", fields: ["key": account.key])
                    }.disabled(model.busy)
                case .connected:
                    HStack {
                        Text(consumption.connectedUsername ?? "Work account connected").foregroundStyle(.secondary)
                        Spacer()
                        Button("Change Account") { model.perform("consumption.connect", fields: ["key": account.key]) }.disabled(model.busy)
                    }
                case .ready, .error:
                    Button(consumption.state == .error ? "Try Again" : "Connect Work Account") {
                        model.perform("consumption.connect", fields: ["key": account.key])
                    }.buttonStyle(.borderedProminent).disabled(model.busy)
                }
            }
        }
    }
}

struct DailyConsumptionChartView: View {
    let account: AccountData
    var compact = false

    var body: some View {
        if let view = account.enterpriseConsumption, let bars = view.bars, !bars.isEmpty,
           let ticks = view.axisTicks, let maximum = ticks.last, maximum > 0 {
            let maxAmount = NSDecimalNumber(decimal: maximum).doubleValue
            let plotHeight: CGFloat = compact ? 64 : 150
            let stride = max(1, Int(ceil(Double(bars.count) / (compact ? 6 : 16))))
            VStack(alignment: .leading, spacing: 8) {
                Text("Daily Usage - \(dailyDateText(bars[0].date, month: true))\(compact ? "" : " (UTC)")")
                    .font(compact ? .caption : .headline)
                Text("USD").font(.caption2).foregroundStyle(.secondary)
                HStack(alignment: .top, spacing: 6) {
                    GeometryReader { geometry in
                        ForEach(Array(ticks.enumerated()), id: \.offset) { _, tick in
                            Text(decimalText(tick))
                                .font(.caption2).foregroundStyle(.secondary)
                                .position(x: geometry.size.width / 2,
                                          y: plotHeight * CGFloat(1 - NSDecimalNumber(decimal: tick).doubleValue / maxAmount))
                        }
                    }.frame(width: compact ? 35 : 55, height: plotHeight)
                    ZStack(alignment: .bottom) {
                        GeometryReader { geometry in
                            ForEach(Array(ticks.enumerated()), id: \.offset) { _, tick in
                                Path { path in
                                    let y = plotHeight * CGFloat(1 - NSDecimalNumber(decimal: tick).doubleValue / maxAmount)
                                    path.move(to: CGPoint(x: 0, y: y))
                                    path.addLine(to: CGPoint(x: geometry.size.width, y: y))
                                }.stroke(Color.secondary.opacity(0.18), lineWidth: 1)
                            }
                        }.frame(height: plotHeight).padding(.bottom, 18)
                        HStack(alignment: .bottom, spacing: compact ? 2 : 4) {
                            ForEach(Array(bars.enumerated()), id: \.element.id) { index, bar in
                                VStack(spacing: 4) {
                                    Rectangle()
                                        .fill(Color.accentColor.opacity(bar.estimated ? 0.2 : 1))
                                        .overlay {
                                            if bar.estimated {
                                                Rectangle().strokeBorder(Color.accentColor, style: StrokeStyle(lineWidth: 1.5, dash: [3, 2]))
                                            }
                                        }
                                        .frame(height: max(1, CGFloat(NSDecimalNumber(decimal: bar.amountUsd).doubleValue / maxAmount) * plotHeight))
                                        .frame(height: plotHeight, alignment: .bottom)
                                    Text(index % stride == 0 || index == bars.count - 1 ? String(Int(bar.date.suffix(2)) ?? 0) : " ")
                                        .font(.caption2).frame(height: 14)
                                }
                                .frame(maxWidth: .infinity)
                                .contentShape(Rectangle())
                                .help(bar.tooltip)
                                .accessibilityLabel(bar.tooltip)
                            }
                        }.frame(height: plotHeight + 18)
                    }
                }
                if !compact, !bars.contains(where: { $0.derived }), let message = view.chartMessage {
                    Text(message).font(.caption).foregroundStyle(.secondary)
                }
            }.accessibilityIdentifier(account.key + (compact ? "_CompactDailyConsumption" : "_DailyConsumption"))
        } else if !compact, let view = account.enterpriseConsumption, view.connected {
            VStack(alignment: .leading, spacing: 8) {
                Text("Daily Usage").font(.headline)
                Text(view.isLoading ? "Connected. Loading daily usage..." : view.chartMessage ?? view.message)
                    .foregroundStyle(.secondary)
            }.accessibilityIdentifier(account.key + "_DailyConsumptionStatus")
        }
    }
}

struct PreferencesView: View {
    @ObservedObject var model: AppModel
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
                    Toggle("Enable consumption alerts", isOn: $model.globalDraft.notifications)
                    PreferenceTextField("Percentage thresholds", text: $model.globalDraft.thresholds,
                                        error: model.fieldErrors["thresholds"], identifier: "Thresholds")
                    Text("Separate positive percentages with commas. Defaults: 50, 80, 100.").font(.caption).foregroundStyle(.secondary)
                    PreferenceTextField("USD increment", text: $model.globalDraft.increment,
                                        error: model.fieldErrors["increment"], identifier: "Increment", prompt: "Disabled")
                    Text("For example, 50 alerts at $50, $100, and so on. Per-account preferences can override or disable these alerts.")
                        .font(.caption).foregroundStyle(.secondary)
                    Button("Send Test Notification") { Task { await model.testNotification() } }
                        .disabled(model.demo || model.notificationBusy || model.notificationPermission == nil ||
                                  model.notificationPermission == .denied || model.notificationPermission == .unavailable)
                    Text("macOS permission and Focus settings determine whether notifications appear.").font(.caption).foregroundStyle(.secondary)
                }
            } else {
                Section("General") {
                    PreferenceTextField("Refresh interval (minutes)", text: $model.globalDraft.minutes,
                                        error: model.fieldErrors["minutes"], identifier: "PollMinutes")
                    Text("From 5 to 1440 minutes; default 10.").font(.caption).foregroundStyle(.secondary)
                    Toggle("Launch at login", isOn: $model.globalDraft.startup).disabled(model.settings?.canChangeStartup != true || model.demo)
                    Text(model.settings?.startupDescription ?? "").font(.caption).foregroundStyle(.secondary)
                    Button("Open Login Items Settings") { SMAppService.openSystemSettingsLoginItems() }.disabled(model.demo)
                    Button("Open Data Folder") { model.openDataFolder() }
                    Toggle("Share basic app telemetry", isOn: $model.globalDraft.trackedTelemetryEnabled)
                        .accessibilityIdentifier("TrackedTelemetryEnabled")
                    Text("Share telemetry including app usage data and error reports to help the developer improve this application.")
                        .font(.caption).foregroundStyle(.secondary)
                    Link("Privacy Policy", destination: URL(string: "https://github.com/DamianEdwards/ghcp-spend-tray/blob/main/PRIVACY.md")!)
                }
                Section("Menu Bar") {
                    Text("Show fresh usage against custom budgets or API limits independently of dollar totals. New accounts are included by default.")
                        .font(.caption).foregroundStyle(.secondary)
                    Picker("Icon style", selection: $model.globalDraft.trayStyle) {
                        Text("Pie chart").tag(TrayIconStyle.pie)
                        Text("Percentage number").tag(TrayIconStyle.percentage)
                    }
                    Picker("Icons to show", selection: $model.globalDraft.trayMode) {
                        Text("One roll-up icon").tag(TrayDisplayMode.rollUp)
                        Text("One icon per selected account").tag(TrayDisplayMode.perAccount)
                    }
                    ForEach(model.dashboard?.accounts ?? []) { account in
                        Toggle("\(account.name) (\(account.host))", isOn: Binding(
                            get: { !model.globalDraft.excludedAccounts.contains(account.key) },
                            set: { include in
                                if include { model.globalDraft.excludedAccounts.remove(account.key) }
                                else { model.globalDraft.excludedAccounts.insert(account.key) }
                            }))
                    }
                    Text("Live Preview").font(.headline)
                    if let preview = trayPreview {
                        ForEach(preview.icons) { icon in
                            HStack {
                                Image(nsImage: TrayIconRenderer.image(icon, style: preview.style))
                                    .help(icon.details).accessibilityLabel(icon.details)
                                Text("\(icon.accountKey == nil ? "Roll-up" : icon.name): \(icon.valueText)" +
                                     (icon.isPartial ? " (partial)" : "") + (icon.isOverAllocation ? " (over limit)" : ""))
                                    .font(.caption)
                            }
                        }
                    }
                    Text("Preview changes apply to the menu bar only after Save. \u{221e} means unlimited; mixed roll-ups show only finite limits. ! marks partial usage or an unavailable pie; ? means unavailable in percentage mode; + means over limit. An empty pie with ! is unavailable, not 0%. Numbers are rounded; hover for details.")
                        .font(.caption).foregroundStyle(.secondary)
                    Text("A neutral icon remains when nothing is selected. macOS may hide icons on a crowded menu bar; reopen GHCPSpendTray to access Settings.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
          }.formStyle(.grouped).disabled(!model.formLoaded || model.saving || model.pendingNavigation)
          FormActions(model: model)
        }.onAppear { model.prepareGlobalDraft(); preview() }
        .onChange(of: notifications) { _, _ in model.prepareGlobalDraft(); preview() }
        .onChange(of: model.globalDraft) { _, _ in preview() }
        .onReceive(model.$dashboard) { _ in preview() }
        .task(id: notifications) { if notifications { await model.refreshNotificationPermission() } }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            if notifications { Task { await model.refreshNotificationPermission() } }
        }
    }

    private func preview() {
        guard model.formLoaded && !notifications, var value = model.settings else { return }
        value.trayStyle = model.globalDraft.trayStyle
        value.trayMode = model.globalDraft.trayMode
        value.excludedTrayAccounts = model.globalDraft.excludedAccounts.sorted()
        previewRevision += 1
        let revision = previewRevision
        do {
            model.send("tray.preview", fields: ["settings": try jsonObject(value)]) { event in
                if revision == previewRevision { trayPreview = event.tray }
            }
        } catch { model.error = "Could not prepare the menu-bar preview." }
    }

}

struct PreferenceBounds: PreferenceKey {
    static var defaultValue: [String: Anchor<CGRect>] { [:] }
    static func reduce(value: inout [String: Anchor<CGRect>], nextValue: () -> [String: Anchor<CGRect>]) {
        value.merge(nextValue(), uniquingKeysWith: { _, latest in latest })
    }
}

struct PreferenceTextField: View {
    let title: String
    @Binding var text: String
    let error: String?
    let identifier: String
    var prompt: String = ""

    init(_ title: String, text: Binding<String>, error: String?, identifier: String, prompt: String = "") {
        self.title = title
        _text = text
        self.error = error
        self.identifier = identifier
        self.prompt = prompt
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            TextField(title, text: $text, prompt: Text(prompt))
                .textFieldStyle(.roundedBorder)
                .overlay(RoundedRectangle(cornerRadius: 5).stroke(error == nil ? Color.clear : Color.red, lineWidth: 2))
                .accessibilityIdentifier(identifier)
                .accessibilityHint(error ?? prompt)
            if let error {
                Label(error, systemImage: "exclamationmark.circle.fill")
                    .font(.caption).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier(identifier + "Error")
                    .anchorPreference(key: PreferenceBounds.self, value: .bounds) { [identifier + "Error": $0] }
            }
        }
    }
}

struct FormActions: View {
    @ObservedObject var model: AppModel
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Divider()
            if let message = model.formMessage {
                Label(message, systemImage: "exclamationmark.circle.fill")
                    .font(.caption).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true)
                    .accessibilityIdentifier("FormMessage")
            }
            HStack {
                Text(model.formLoaded ? model.formStatus : "Loading preferences...")
                    .font(.caption).foregroundStyle(.secondary).accessibilityIdentifier("FormStatus")
                if model.saving { ProgressView().controlSize(.small) }
                Spacer()
                Button("Cancel") { model.cancelChanges() }
                    .disabled(model.saving || !model.formLoaded || model.pendingNavigation)
                    .accessibilityIdentifier("CancelPreferences")
                    .anchorPreference(key: PreferenceBounds.self, value: .bounds) { ["CancelPreferences": $0] }
                Button("Save") { model.saveChanges() }.buttonStyle(.borderedProminent)
                    .disabled(model.busy || !model.formLoaded || model.pendingNavigation)
                    .accessibilityIdentifier("SavePreferences")
                    .anchorPreference(key: PreferenceBounds.self, value: .bounds) { ["SavePreferences": $0] }
            }
        }
        .padding([.horizontal, .bottom], 20).padding(.top, 4)
        .fixedSize(horizontal: false, vertical: true)
        .accessibilityIdentifier("FormActions")
    }
}

@MainActor
enum UnsavedChangesAlert {
    static func make() -> NSAlert {
        let alert = NSAlert()
        alert.messageText = "Save changes before leaving?"
        alert.informativeText = "Save your preferences, discard the draft, or keep editing. Invalid or failed saves will keep you on this page."
        alert.alertStyle = .warning
        alert.addButton(withTitle: "Save")
        alert.addButton(withTitle: "Discard")
        alert.addButton(withTitle: "Keep Editing")
        alert.buttons[2].keyEquivalent = "\u{1b}"
        return alert
    }

    static func choice(_ response: NSApplication.ModalResponse) -> UnsavedChangesChoice {
        switch response {
        case .alertFirstButtonReturn: return .save
        case .alertSecondButtonReturn: return .discard
        default: return .keepEditing
        }
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
