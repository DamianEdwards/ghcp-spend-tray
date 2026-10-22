import AppKit
import Foundation
import SwiftUI

@MainActor
enum UsageTrackTests {
    static func run() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ghcp-usage-ui-" + UUID().uuidString)
        let model = AppModel(directory: directory, demo: true)
        defer { model.shutdown() }
        model.start(empty: false, usageStates: true)
        try await waitForState(model)
        guard let accounts = model.dashboard?.accounts, accounts.count == 8,
              accounts.prefix(3).map({ $0.forecast?.band }) == [.under, .near, .over],
              accounts[3].actualUsage?.band == .near, accounts[4].actualUsage?.band == .over,
              accounts[5].forecast == nil, accounts[5].actualUsage != nil,
              accounts[6].periodEstimate?.unavailableReason != nil, accounts[6].forecast == nil,
              accounts[7].details.unlimited, accounts[7].actualUsage == nil else {
            throw AppError.message("The native showcase must decode all eight shared usage/budget/forecast states.")
        }
        let output = URL(fileURLWithPath: "artifacts/macos-test-diagnostics/usage-ui", isDirectory: true)
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        for width: CGFloat in [260, 352, 650] {
            for (theme, scheme) in [("light", ColorScheme.light), ("dark", .dark)] {
                for group in 0..<2 {
                    let rows = Array(accounts[(group * 4)..<(group * 4 + 4)])
                    let view = NSHostingView(rootView:
                        VStack(alignment: .leading, spacing: 20) {
                            ForEach(rows) { account in
                                AccountRow(account: account).frame(width: width)
                                Divider()
                            }
                        }
                        .padding(12).frame(width: width + 24)
                        .background(Color(nsColor: .windowBackgroundColor))
                        .environment(\.colorScheme, scheme))
                    try snapshot(view, to: output.appendingPathComponent("\(Int(width))-\(theme)-\(group).png"))
                }
                for account in accounts { try measure(account, width: width, scheme: scheme) }
            }
        }
        try measurePopup(model)
        for used in [0.0, 100.0] {
            let equal = try PeriodEstimateTests.account(estimate: ["estimatedConsumptionUsd": used, "isEarly": false], used: used)
            for scheme in [ColorScheme.light, .dark] { try measure(equal, width: 260, scheme: scheme) }
        }
        for budget: Decimal in [125.50, 50, 0.01] {
            model.perform("account.save", fields: [
                "key": accounts[0].key, "displayName": accounts[0].name, "showPeriodEstimate": true,
                "customBudgetUsd": NSDecimalNumber(decimal: budget), "updateCustomBudget": true
            ])
            try await waitForState(model)
            guard let edited = model.dashboard?.accounts.first,
                  edited.customBudgetUsd == budget, edited.consumptionUsd == 40.25,
                  edited.periodEstimate?.estimatedConsumptionUsd == 75 else {
                throw AppError.message("Native budget edits must retain actual and estimated usage amounts.")
            }
            try measure(edited, width: 260, scheme: .light)
            try measurePopup(model)
            if budget == 125.50 {
                let expected = Decimal(string: "32.07")!.formatted(.number.grouping(.never))
                try check(edited.usageCaption == "\(expected)% used",
                          "Repeating budget percentages round to two regional decimal places instead of displaying decimal tails.")
            }
        }
        let large = try JSONDecoder().decode(AccountData.self, from: Data("""
        {"key":"github.com:99","name":"Large amounts (sample)","login":"demo-large","host":"github.com",
         "details":{"unlimited":false,"isCurrentPeriod":true},"freshness":"Fresh",
         "consumptionUsd":123456789.12,"allocationUsd":100000000,"customBudgetUsd":100000000,"percent":123.45678912,
         "actualUsage":{"fraction":1,"percent":123.45678912,"band":2},
         "forecast":{"actualFraction":1,"forecastFraction":1,"percent":234.56789012,"band":2},
         "periodEstimate":{"estimatedConsumptionUsd":234567890.12,"isEarly":false,"reportedDayCount":0}}
        """.utf8))
        for width: CGFloat in [260, 352, 650] { try measure(large, width: width, scheme: .light) }
        try check(!FileManager.default.fileExists(atPath: directory.path),
                  "The native usage showcase must not persist account data or create real-account storage.")
        print("PASS: Native AOT usage showcase, compact/wide light/dark callout bounds, target centers, independent risk states and budget edits.")
    }

    private static func waitForState(_ model: AppModel) async throws {
        let deadline = ContinuousClock.now.advanced(by: .seconds(10))
        while ContinuousClock.now < deadline {
            model.poll()
            if let error = model.error { throw AppError.message(error) }
            if model.initialized && !model.busy { return }
            try await Task.sleep(for: .milliseconds(10))
        }
        throw AppError.message("The native usage showcase did not complete its bridge operation.")
    }

    private static func measure(_ account: AccountData, width: CGFloat, scheme: ColorScheme) throws {
        let probe = LayoutProbe()
        let host = NSHostingView(rootView:
            AccountRow(account: account).frame(width: width).padding(12)
                .background(Color(nsColor: .windowBackgroundColor)).environment(\.colorScheme, scheme)
                .overlayPreferenceValue(PreferenceBounds.self) { anchors in
                    GeometryReader { geometry in
                        let frames = anchors.mapValues { geometry[$0] }
                        Color.clear.onAppear { probe.frames = frames }
                            .onChange(of: frames) { _, value in probe.frames = value }
                    }.allowsHitTesting(false)
                })
        let window = makeWindow(host)
        defer { window.close() }
        do {
            try settle(host, probe: probe, hasTrack: account.actualUsage != nil)
        } catch {
            throw AppError.message("\(error.localizedDescription) Account: \(account.name); width: \(width); bounds: \(host.bounds); keys: \(probe.frames.keys.sorted()).")
        }
        guard account.actualUsage != nil else {
            try check(probe.frames["ForecastTrack"] == nil, "Unlimited usage retains the amount fallback instead of inventing a finite track.")
            return
        }
        func frame(_ name: String) throws -> CGRect {
            guard let frame = probe.frames[name] else { throw AppError.message("Missing native usage geometry: \(name).") }
            return frame
        }
        let track = try frame("ForecastTrack")
        let actual = try frame("ActualUsageCallout")
        let content = try frame("ActualUsageContent")
        let used = try frame("ActualUsageLabel")
        let target = try frame("TrackingTarget")
        let amount = try frame("TargetAmount")
        let label = try frame("TargetLabel")
        let drawing = try frame("UsageDrawing")
        let lineY = drawing.minY + 9
        let context = "\(account.name), width \(width), \(scheme)"
        for rect in [track, actual, content, used, target, amount, label] {
            try check(host.bounds.insetBy(dx: -1, dy: -1).contains(rect), "Usage content is clipped: \(context), \(rect).")
        }
        try check(actual.insetBy(dx: -1, dy: -1).contains(content) &&
                  actual.insetBy(dx: -1, dy: -1).contains(used),
                  "The current amount or used label exceeds its callout: \(context); callout \(actual), amount row \(content), label \(used).")
        try check(abs(content.midX - used.midX) <= 1 &&
                  abs(amount.midX - label.midX) <= 1 && target.insetBy(dx: -1, dy: -1).contains(amount),
                  "Amount/subtitle centers and target containment must agree: \(context).")
        try check(actual.maxY <= lineY - 9 && actual.maxX <= target.minX - 11 &&
                  abs(amount.midY - lineY) <= 1,
                  "Used amounts stay above the marker and separate from the centered target: \(context); actual \(actual), target \(target), target amount \(amount), line \(lineY).")
        func calloutX(fraction: Double, width: CGFloat) -> CGFloat {
            let marker = drawing.minX + 2 + CGFloat(fraction) * max(0, drawing.width - 4)
            return min(max(drawing.minX, marker - width / 2), drawing.maxX - width)
        }
        try check(abs(actual.minX - calloutX(fraction: account.actualUsage!.fraction, width: actual.width)) <= 1 &&
                  (probe.frames["ActualUsageWarning"] != nil) == (account.actualUsage?.band != .under),
                  "Current amounts remain marker-centered with edge clamping and actual-only risk icons: \(context).")
        if account.forecast != nil {
            let forecast = try frame("ForecastCallout")
            let forecastAmount = try frame("ForecastAmount")
            let forecastLabel = try frame("ForecastLabel")
            try check(track.insetBy(dx: -1, dy: -1).contains(forecast) &&
                      forecast.insetBy(dx: -1, dy: -1).contains(forecastAmount) &&
                      forecast.insetBy(dx: -1, dy: -1).contains(forecastLabel) &&
                      abs(forecastAmount.midX - forecastLabel.midX) <= 1 &&
                      abs(forecast.minX - calloutX(fraction: account.forecast!.forecastFraction, width: forecast.width)) <= 1 &&
                      forecast.minY >= lineY + 17 && forecast.maxX <= target.minX - 11,
                      "Estimated totals stay centered below the line without target overlap or clipping: \(context).")
        } else {
            try check(probe.frames["ForecastCallout"] == nil, "Disabled or unavailable estimates retain only the actual-usage track.")
        }
    }

    private static func measurePopup(_ model: AppModel) throws {
        let dashboard = model.dashboard!
        defer { model.dashboard = dashboard }
        model.dashboard = Dashboard(total: "Synthetic", status: "Synthetic", tooltip: "Synthetic",
                                    accounts: [dashboard.accounts[0]], consumptionUsd: dashboard.accounts[0].consumptionUsd,
                                    isComplete: true, isLastKnown: false, tray: nil)
        let probe = LayoutProbe()
        let host = NSHostingView(rootView: FlyoutView(model: model)
            .overlayPreferenceValue(PreferenceBounds.self) { anchors in
                GeometryReader { geometry in
                    let frames = anchors.mapValues { geometry[$0] }
                    Color.clear.onAppear { probe.frames = frames }
                        .onChange(of: frames) { _, value in probe.frames = value }
                }.allowsHitTesting(false)
            })
        let window = makeWindow(host)
        defer { window.close() }
        try settle(host, probe: probe, hasTrack: true)
        guard let content = probe.frames["FlyoutAccountsContent"], let viewport = probe.frames["FlyoutAccountsViewport"] else {
            throw AppError.message("The single-account popup did not expose its content and viewport bounds.")
        }
        try check(content.height <= viewport.height + 1,
                  "A single account with its forecast and daily chart should fit without scrolling: content \(content.height), viewport \(viewport.height).")
    }

    private static func settle(_ view: NSView, probe: LayoutProbe? = nil, hasTrack: Bool = false) throws {
        let clock = ContinuousClock()
        let start = clock.now
        var readiness = PopupSmokeReadiness()
        while true {
            view.layoutSubtreeIfNeeded()
            let frames = probe?.frames ?? [:]
            let missing = hasTrack && frames["TrackingTarget"] == nil
            switch readiness.observe(frames: [view.bounds] + frames.keys.sorted().compactMap { frames[$0] },
                                     failure: missing ? "Usage track preferences have not rendered." : nil,
                                     elapsed: start.duration(to: clock.now)) {
            case .ready: return
            case .timedOut: throw AppError.message("Native usage layout did not settle: \(readiness.reason)")
            case .waiting: RunLoop.current.run(until: Date().addingTimeInterval(0.01))
            }
        }
    }

    private static func makeWindow(_ view: NSView) -> NSWindow {
        let window = NSWindow(contentRect: NSRect(x: -10000, y: -10000, width: 700, height: 1000),
                              styleMask: .borderless, backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = view
        window.setContentSize(view.fittingSize)
        window.orderBack(nil)
        return window
    }

    private static func snapshot(_ view: NSView, to url: URL) throws {
        let window = makeWindow(view)
        defer { window.close() }
        try settle(view)
        guard let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else {
            throw AppError.message("Usage-state showcase did not render.")
        }
        view.cacheDisplay(in: view.bounds, to: bitmap)
        guard let png = bitmap.representation(using: .png, properties: [:]), png.count > 1000 else {
            throw AppError.message("Usage-state showcase render was empty.")
        }
        try png.write(to: url)
    }

    private static func check(_ condition: Bool, _ message: String) throws {
        if !condition { throw AppError.message(message) }
    }

    private final class LayoutProbe {
        var frames: [String: CGRect] = [:]
    }
}
