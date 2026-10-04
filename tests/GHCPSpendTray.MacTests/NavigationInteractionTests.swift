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
        }.frame(width: 352))
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
        try waitForLayout(accounts, phase: "account-management card") { $0.width == 352 && $0.height > 100 }
        try check(accounts.fittingSize.width == 352 && accounts.fittingSize.height > 100,
                  "Account management is a full-width shaded group with its management action.")
        try click(window, view: accounts, x: 90, fromTop: 20)
        try waitForLayout(accounts, phase: "account-information navigation") { _ in model.selectedAccount == account.key }
        try check(model.selectedAccount == account.key, "Clicking account information opens its management page.")
        model.selectedAccount = nil
        try click(window, view: accounts, x: 300, fromTop: accounts.bounds.height - 14)
        try waitForLayout(accounts, phase: "explicit management navigation") { _ in model.selectedAccount == account.key }
        try check(model.selectedAccount == account.key, "The visible Manage account action also opens account management.")
        print("PASS: title/whole-row/chevron disclosure clicks and explicit/full-row account management clicks.")
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

    private static func click(_ window: NSWindow, view: NSView, x: CGFloat, fromTop: CGFloat) throws {
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
