import AppKit
import Foundation
import SwiftUI

@MainActor
enum NavigationInteractionTests {
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
        settle(details)
        let collapsedHeight = details.fittingSize.height
        try check(details.fittingSize.width == 352 && collapsedHeight < 40,
                  "The shared Usage/account disclosure starts as a full-width collapsed heading.")
        try click(window, view: details, x: 90, fromTop: 12)
        try check(details.fittingSize.height > collapsedHeight + 100,
                  "Clicking the heading text, away from the chevron, expands the details.")
        window.setContentSize(details.fittingSize)
        settle(details)
        try click(window, view: details, x: 330, fromTop: 12)
        try check(abs(details.fittingSize.height - collapsedHeight) < 1,
                  "Clicking the far end of the heading row collapses the details.")
        window.setContentSize(details.fittingSize)
        settle(details)
        try click(window, view: details, x: 5, fromTop: 12)
        try check(details.fittingSize.height > collapsedHeight + 100,
                  "The existing chevron remains a working disclosure target.")

        window.contentView = accounts
        window.setContentSize(accounts.fittingSize)
        settle(accounts)
        try check(accounts.fittingSize.width == 352 && accounts.fittingSize.height > 100,
                  "Account management is a full-width shaded group with its management action.")
        try click(window, view: accounts, x: 90, fromTop: 20)
        try check(model.selectedAccount == account.key, "Clicking account information opens its management page.")
        model.selectedAccount = nil
        try click(window, view: accounts, x: 300, fromTop: accounts.bounds.height - 14)
        try check(model.selectedAccount == account.key, "The visible Manage account action also opens account management.")
        print("PASS: title/whole-row/chevron disclosure clicks and explicit/full-row account management clicks.")
    }

    private static func settle(_ view: NSView) {
        view.layoutSubtreeIfNeeded()
        RunLoop.current.run(until: Date().addingTimeInterval(0.25))
        view.layoutSubtreeIfNeeded()
    }

    private static func click(_ window: NSWindow, view: NSView, x: CGFloat, fromTop: CGFloat) throws {
        let y = view.isFlipped ? fromTop : view.bounds.height - fromTop
        let point = view.convert(NSPoint(x: x, y: y), to: nil)
        guard let down = NSEvent.mouseEvent(with: .leftMouseDown, location: point, modifierFlags: [],
                                           timestamp: 0, windowNumber: window.windowNumber, context: nil,
                                           eventNumber: 1, clickCount: 1, pressure: 1),
              let up = NSEvent.mouseEvent(with: .leftMouseUp, location: point, modifierFlags: [],
                                         timestamp: 0.1, windowNumber: window.windowNumber, context: nil,
                                         eventNumber: 2, clickCount: 1, pressure: 0) else {
            throw AppError.message("Cannot construct the synthetic navigation click.")
        }
        NSApplication.shared.postEvent(up, atStart: true)
        window.sendEvent(down)
        _ = NSApplication.shared.nextEvent(matching: .leftMouseUp, until: Date(), inMode: .default, dequeue: true)
        window.sendEvent(up)
        settle(view)
    }
}
