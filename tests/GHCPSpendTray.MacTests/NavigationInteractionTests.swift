import AppKit
import Foundation
import SwiftUI

@MainActor
enum NavigationInteractionTests {
    private static var eventNumber = 0

    static func run() throws {
        func check(_ condition: Bool, _ message: String) throws {
            if !condition { throw AppError.message(message) }
        }
        let model = AppModel(directory: URL(fileURLWithPath: "/synthetic-navigation-unused"), demo: true,
                             bridge: FixtureBridge())
        defer { model.shutdown() }
        model.dashboard = try JSONDecoder().decode(Dashboard.self, from: Data("""
        {"total":"Synthetic consumption","status":"Synthetic","tooltip":"Synthetic","consumptionUsd":12.5,
         "isComplete":true,"isLastKnown":false,
         "accounts":[{"key":"github.com:1","name":"Synthetic","login":"fixture-user","host":"github.com",
          "details":{"unlimited":false,"isCurrentPeriod":true},"freshness":"Fresh","consumptionUsd":12.5}]}
        """.utf8))
        let account = model.dashboard!.accounts[0]
        let details = NSHostingView(rootView: AccountDiagnosticsView(account: account).frame(width: 352))
        let accounts = NSHostingView(rootView: AccountManagementRow(account: account) {
            model.selectedAccount = account.key
        }.frame(width: 352).background(Color(nsColor: .windowBackgroundColor)))
        let window = NSWindow(contentRect: NSRect(x: -10000, y: -10000, width: 352, height: 300),
                              styleMask: .borderless, backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        defer { window.close() }
        window.contentView = details
        window.setContentSize(details.fittingSize)
        window.orderBack(nil)
        try waitForLayout(details, phase: "initial collapsed heading") { $0.width == 352 && $0.height < 40 }
        let collapsedHeight = details.fittingSize.height
        try check(details.fittingSize.width == 352 && collapsedHeight < 40,
                  "The shared Usage/account disclosure starts as a full-width collapsed heading.")
        try click(window, view: details, x: 90, fromTop: 12)
        try waitForLayout(details, phase: "heading-text expansion") { $0.height > collapsedHeight + 100 }
        try check(details.fittingSize.height > collapsedHeight + 100,
                  "Clicking the heading text, away from the chevron, expands the details.")
        window.setContentSize(details.fittingSize)
        try waitForLayout(details, phase: "expanded heading before whitespace click") { $0.height > collapsedHeight + 100 }
        try click(window, view: details, x: 330, fromTop: 12)
        try waitForLayout(details, phase: "heading-whitespace collapse") { abs($0.height - collapsedHeight) < 1 }
        try check(abs(details.fittingSize.height - collapsedHeight) < 1,
                  "Clicking the far end of the heading row collapses the details.")
        window.setContentSize(details.fittingSize)
        try waitForLayout(details, phase: "collapsed heading before chevron click") { abs($0.height - collapsedHeight) < 1 }
        try click(window, view: details, x: 5, fromTop: 12)
        try waitForLayout(details, phase: "chevron expansion") { $0.height > collapsedHeight + 100 }
        try check(details.fittingSize.height > collapsedHeight + 100,
                  "The existing chevron remains a working disclosure target.")

        window.contentView = accounts
        window.setContentSize(accounts.fittingSize)
        try waitForLayout(accounts, phase: "account-management card") { $0.width == 352 && $0.height > 60 && $0.height < 100 }
        let identitySize = accounts.fittingSize
        let identityImage = try snapshot(accounts)
        var richAccount = try PeriodEstimateTests.account(estimate: ["estimatedConsumptionUsd": 200, "isEarly": false], used: 99)
        richAccount.customBudgetUsd = 1000
        let richManagement = NSHostingView(rootView: AccountManagementRow(account: richAccount) {}
            .frame(width: 352).background(Color(nsColor: .windowBackgroundColor)))
        window.contentView = richManagement
        window.setContentSize(richManagement.fittingSize)
        try waitForLayout(richManagement, phase: "usage-independent account-management card") { $0 == identitySize }
        try check(try snapshot(richManagement) == identityImage,
                  "Accounts list cards render only identity and management: amounts, percentages, budgets and forecasts cannot change their pixels.")
        try identityImage.write(to: URL(fileURLWithPath: "artifacts/macos-test-diagnostics/AccountManagement.png"))
        window.contentView = accounts
        window.setContentSize(identitySize)
        try waitForLayout(accounts, phase: "identity-only management navigation") { $0 == identitySize }
        try click(window, view: accounts, x: 90, fromTop: 20)
        try waitForLayout(accounts, phase: "account-information navigation") { _ in model.selectedAccount == account.key }
        try check(model.selectedAccount == account.key, "Clicking account information opens its management page.")
        model.selectedAccount = nil
        try click(window, view: accounts, x: 300, fromTop: accounts.bounds.height - 14)
        try waitForLayout(accounts, phase: "explicit management navigation") { _ in model.selectedAccount == account.key }
        try check(model.selectedAccount == account.key, "The visible Manage account action also opens account management.")
        let popup = NSHostingView(rootView: FlyoutView(model: model))
        window.contentView = popup
        window.setContentSize(popup.fittingSize)
        try waitForLayout(popup, phase: "popup Usage navigation") { $0.width == 400 && $0.height > 150 }
        var opened = 0
        model.showSettings = { opened += 1 }
        for _ in 0..<2 {
            model.page = .accounts
            model.selectedAccount = account.key
            let revision = model.usageNavigationRevision
            try click(window, view: popup, x: 100, fromTop: 75)
            try waitForLayout(popup, phase: "popup account Usage action") { _ in model.usageNavigationRevision > revision }
            try check(model.page == .usage && model.selectedAccount == nil && model.usageAccount == account.key &&
                      model.usageNavigationRevision == revision + 1,
                      "Popup account clicks open Usage and repeat its scroll request rather than opening management.")
        }
        try check(opened == 2, "Every popup account click opens the settings window.")
        model.viewUsage("github.com:removed")
        try check(opened == 2 && model.error != nil, "Removed accounts fail explicitly instead of navigating.")
        model.error = nil
        try usageManagement(model, window: window, first: account)
        print("PASS: identity-only Accounts cards, per-account Usage management clicks, disclosures and repeated popup-to-Usage navigation.")
    }

    private static func usageManagement(_ model: AppModel, window: NSWindow, first: AccountData) throws {
        let second = try JSONDecoder().decode(AccountData.self, from: Data("""
        {"key":"example.ghe.com:2","name":"Other synthetic","login":"other-fixture","host":"example.ghe.com",
         "details":{"unlimited":false,"isCurrentPeriod":true},"freshness":"Fresh","consumptionUsd":25}
        """.utf8))
        model.dashboard = Dashboard(total: "Synthetic", status: "Synthetic", tooltip: "Synthetic",
                                    accounts: [first, second], consumptionUsd: 37.5,
                                    isComplete: true, isLastKnown: false, tray: nil)
        model.selectPage(.usage)
        model.usageAccount = nil
        let probe = ManagementLayout()
        let usage = NSHostingView(rootView: UsageView(model: model).frame(width: 650, height: 900)
            .background(Color(nsColor: .windowBackgroundColor))
            .overlayPreferenceValue(PreferenceBounds.self) { anchors in
                GeometryReader { geometry in
                    let frames = anchors.mapValues { geometry[$0] }
                    Color.clear.onAppear { probe.frames = frames }
                        .onChange(of: frames) { _, value in probe.frames = value }
                }.allowsHitTesting(false)
            })
        window.contentView = usage
        window.setContentSize(usage.fittingSize)
        try waitForLayout(usage, phase: "per-account Usage management actions") { _ in
            [first, second].allSatisfy { probe.frames[$0.key + "_UsageManageAccount"] != nil }
        }
        try snapshot(usage).write(to: URL(fileURLWithPath: "artifacts/macos-test-diagnostics/UsageManagement.png"))
        for account in [second, first] {
            model.selectPage(.usage)
            guard let bounds = probe.frames[account.key + "_UsageManageAccount"],
                  usage.bounds.contains(bounds) else {
                throw AppError.message("The account's Manage action is missing or outside the Usage viewport.")
            }
            try click(window, view: usage, x: bounds.midX, fromTop: bounds.midY)
            try waitForLayout(usage, phase: "Usage-to-account-management navigation") { _ in
                model.page == .accounts && model.selectedAccount == account.key
            }
        }
    }

    private static func snapshot(_ view: NSView) throws -> Data {
        view.layoutSubtreeIfNeeded()
        guard let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else {
            throw AppError.message("Account navigation view did not render.")
        }
        view.cacheDisplay(in: view.bounds, to: bitmap)
        guard let data = bitmap.representation(using: .png, properties: [:]), data.count > 1000 else {
            throw AppError.message("Account navigation render was empty.")
        }
        return data
    }

    private final class ManagementLayout {
        var frames: [String: CGRect] = [:]
    }

    private static func waitForLayout(_ view: NSView, phase: String, ready: (NSSize) -> Bool) throws {
        let clock = ContinuousClock()
        let start = clock.now
        var readiness = PopupSmokeReadiness()
        var firstSize: NSSize?
        var samples = 0
        while true {
            view.layoutSubtreeIfNeeded()
            let size = view.fittingSize
            firstSize = firstSize ?? size
            samples += 1
            let elapsed = start.duration(to: clock.now)
            switch readiness.observe(frames: [NSRect(origin: .zero, size: size), view.bounds],
                                     failure: ready(size) ? nil : "Expected layout or navigation state has not appeared.",
                                     elapsed: elapsed) {
            case .ready:
                return
            case .timedOut:
                throw AppError.message("Timed out waiting for \(phase) after \(elapsed), \(samples) samples: \(readiness.reason). First size: \(String(describing: firstSize)); latest size: \(size); bounds: \(view.bounds); window: \(String(describing: view.window?.frame)); macOS: \(ProcessInfo.processInfo.operatingSystemVersionString).")
            case .waiting:
                RunLoop.current.run(until: Date().addingTimeInterval(0.01))
            }
        }
    }

    static func click(_ window: NSWindow, view: NSView, x: CGFloat, fromTop: CGFloat) throws {
        let y = view.isFlipped ? fromTop : view.bounds.height - fromTop
        let point = view.convert(NSPoint(x: x, y: y), to: nil)
        let timestamp = ProcessInfo.processInfo.systemUptime
        eventNumber += 2
        guard let down = NSEvent.mouseEvent(with: .leftMouseDown, location: point, modifierFlags: [],
                                           timestamp: timestamp, windowNumber: window.windowNumber, context: nil,
                                           eventNumber: eventNumber - 1, clickCount: 1, pressure: 1),
              let up = NSEvent.mouseEvent(with: .leftMouseUp, location: point, modifierFlags: [],
                                         timestamp: timestamp + 0.001, windowNumber: window.windowNumber, context: nil,
                                         eventNumber: eventNumber, clickCount: 1, pressure: 0) else {
            throw AppError.message("Cannot construct the synthetic navigation click.")
        }
        NSApplication.shared.postEvent(up, atStart: true)
        window.sendEvent(down)
        if let queuedUp = NSApplication.shared.nextEvent(matching: .leftMouseUp, until: Date(), inMode: .default, dequeue: true) {
            window.sendEvent(queuedUp)
        }
    }
}
