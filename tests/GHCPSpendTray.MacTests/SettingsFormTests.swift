import AppKit
import Carbon
import SwiftUI

@MainActor
enum SettingsFormTests {
    static func run() throws {
        func check(_ condition: Bool, _ message: String) throws {
            if !condition { throw AppError.message(message) }
        }
        let bridge = FixtureBridge()
        let model = AppModel(directory: URL(fileURLWithPath: "/synthetic-forms-unused"), demo: true, bridge: bridge)
        defer { model.shutdown() }
        model.initialized = true
        model.settings = try JSONDecoder().decode(SettingsData.self, from: Data("""
        {"pollMinutes":10,"thresholds":"50, 80, 100","notifications":true,"startup":false,
         "canChangeStartup":false,"startupDescription":"Synthetic","trayStyle":0,"trayMode":0}
        """.utf8))
        model.dashboard = try JSONDecoder().decode(Dashboard.self, from: Data("""
        {"total":"Synthetic","status":"Synthetic","tooltip":"Synthetic","isComplete":true,"isLastKnown":false,
         "accounts":[{"key":"github.com:1","name":"Synthetic","login":"fixture","host":"github.com",
          "details":{"unlimited":true,"isCurrentPeriod":true},"freshness":"Fresh","consumptionUsd":16.5,
          "customBudgetUsd":50,"allocationUsd":50,"percent":33}]}
        """.utf8))
        let account = model.dashboard!.accounts[0]
        try check(account.allocationSummary == "33% of $50.00 custom budget",
                  "Finite custom budgets must label usage even with unlimited API allocation.")
        let unknown = try JSONDecoder().decode(AccountData.self, from: Data("""
        {"key":"github.com:2","name":"Synthetic","login":"fixture","host":"github.com",
         "details":{"unlimited":false,"isCurrentPeriod":true},"freshness":"Fresh",
         "consumptionUsd":16.5,"customBudgetUsd":50,"allocationUsd":50,"percent":33}
        """.utf8))
        try check(unknown.allocationSummary == account.allocationSummary,
                  "Unknown API allocation does not hide a finite custom target.")
        model.selectAccount(account.key)
        model.loadAccountDraft(account.key)
        let load = bridge.lastRequest
        try bridge.enqueue("completed", id: load["id"], values: ["preferences": [
            "displayName": "Synthetic", "thresholds": "", "showPeriodEstimate": false, "customBudgetUsd": 50
        ]])
        model.poll()
        try check(model.formLoaded && !model.hasUnsavedChanges && model.accountDraft.budget == "50",
                  "Account preferences establish a saved budget baseline.")
        model.accountDraft.showPeriodEstimate = true
        try check(model.hasUnsavedChanges, "Toggles participate in dirty tracking.")
        model.accountDraft.showPeriodEstimate = false
        try check(!model.hasUnsavedChanges, "Returning toggles to the original value makes the form clean.")
        model.accountDraft.name = String(repeating: "x", count: 129)
        model.accountDraft.thresholds = "0, test"
        model.accountDraft.inheritIncrement = false
        model.accountDraft.increment = "invalid"
        model.accountDraft.budget = "test"
        let invalidDraft = model.accountDraft
        model.saveChanges()
        try check(model.fieldErrors.count == 4 && !model.busy && model.accountDraft == invalidDraft &&
                  !bridge.requests.contains { $0["method"] as? String == "account.save" },
                  "Invalid Save marks every field, retains the exact draft and does not persist.")
        model.accountDraft.name = "Synthetic"
        model.accountDraft.thresholds = "100, 50, 80, 50"
        model.accountDraft.inheritIncrement = true
        model.accountDraft.useCustomBudget = false
        try check(model.fieldErrors.isEmpty && model.formMessage == nil,
                  "Corrected values and disabled overrides clear inline validation.")
        model.cancelChanges()
        try check(model.accountDraft.budget == "50" && !model.hasUnsavedChanges,
                  "Cancel restores both the target mode and the saved values.")

        for action in [
            { model.selectAccount(nil) },
            { model.selectPage(.about) },
            { model.selectAccount("github.com:2") },
            { model.addAccount() },
            { model.reconnectAccount(account) }
        ] {
            model.accountDraft.name = "Dirty"
            action()
            try check(model.pendingNavigation && model.selectedAccount == account.key && model.page == .accounts &&
                      !model.showingSignIn, "Back, sidebar, account switches, Add and Reconnect must guard the active draft.")
            model.resolveUnsavedChanges(.keepEditing)
            try check(model.accountDraft.name == "Dirty" && model.hasUnsavedChanges && !model.pendingNavigation,
                      "Keep editing preserves the selected page and exact draft.")
            model.cancelChanges()
        }
        model.accountDraft.budget = "invalid"
        model.selectPage(.general)
        model.resolveUnsavedChanges(.save)
        try check(model.page == .accounts && model.accountDraft.budget == "invalid" &&
                  model.fieldErrors["budget"] != nil && !model.pendingNavigation,
                  "Invalid Save cancels the requested navigation.")
        model.accountDraft.budget = "75.00"
        model.selectPage(.general)
        model.resolveUnsavedChanges(.save)
        let saving = bridge.requests.last { $0["method"] as? String == "account.save" }!
        try check(model.saving && saving["updateCustomBudget"] as? Bool == true &&
                  (saving["customBudgetUsd"] as? NSDecimalNumber)?.decimalValue == 75,
                  "An explicit edit sends the additive budget update arguments.")
        var closes = 0
        try check(!model.requestLeaving({ closes += 1 }) && closes == 0, "Closing/Quit is blocked while Save is in flight.")
        model.selectAccount(nil)
        try check(model.selectedAccount == account.key, "Back remains blocked during Save.")
        try bridge.enqueue("completed", id: saving["id"], values: ["error": "Synthetic save failed."])
        model.poll()
        try check(model.page == .accounts && model.accountDraft.budget == "75.00" && model.hasUnsavedChanges &&
                  model.formMessage == "Synthetic save failed.", "Failed persistence retains the draft and cancels navigation.")
        model.selectPage(.general)
        model.resolveUnsavedChanges(.save)
        try bridge.enqueue("completed", id: bridge.lastRequest["id"])
        model.poll()
        try check(model.page == .general && !model.hasUnsavedChanges, "Successful Save continues the original action.")
        model.prepareGlobalDraft()
        let global = model.globalDraft
        model.globalDraft.minutes = "test"
        model.saveChanges()
        try check(model.fieldErrors["minutes"] != nil && model.globalDraft.minutes == "test" &&
                  model.settings?.pollMinutes == 10, "General validation does not mutate persisted preferences.")
        model.globalDraft.minutes = "10"
        try check(!model.hasUnsavedChanges && model.fieldErrors.isEmpty, "Returning text inputs to their baseline is clean.")
        model.globalDraft.startup = true
        model.globalDraft.trayStyle = .percentage
        model.globalDraft.excludedAccounts.insert(account.key)
        try bridge.enqueue("state", id: nil, values: ["settings": try jsonObject(model.settings!)])
        model.poll()
        try check(model.globalDraft.startup && model.globalDraft.trayStyle == .percentage && model.hasUnsavedChanges,
                  "Activation/background settings refresh cannot overwrite dirty toggles and selections.")
        var cancelled = 0
        try check(!model.requestLeaving({ closes += 1 }, cancelled: { cancelled += 1 }), "Closing/Quit protects global drafts.")
        model.resolveUnsavedChanges(.keepEditing)
        try check(closes == 0 && cancelled == 1 && model.globalDraft.startup, "Keep editing cancels termination/close.")
        _ = model.requestLeaving({ closes += 1 })
        model.resolveUnsavedChanges(.discard)
        try check(closes == 1 && model.globalDraft == global && !model.hasUnsavedChanges,
                  "Discard restores saved preferences before completing the close.")
        model.selectPage(.notifications)
        model.prepareGlobalDraft()
        model.globalDraft.notifications = false
        model.globalDraft.thresholds = "50, nope"
        model.globalDraft.increment = "-1"
        model.saveChanges()
        try check(model.fieldErrors.count == 2 && model.settings?.notifications == true,
                  "Notifications validate thresholds/increment together and retain toggle drafts.")
        model.cancelChanges()
        try check(!model.hasUnsavedChanges && model.globalDraft.notifications, "Notifications Cancel restores saved toggles.")
        try check(isSystemTermination(UInt32(kAEShutDown)) && isSystemTermination(UInt32(kAERestart)) &&
                  isSystemTermination(UInt32(kAEReallyLogOut)) && !isSystemTermination(0),
                  "Noninteractive shutdown/restart/logout does not require a modal draft decision.")
        try check(UnsavedChangesAlert.make().buttons.map(\.title) == ["Save", "Discard", "Keep Editing"] &&
                  UnsavedChangesAlert.choice(.alertThirdButtonReturn) == .keepEditing,
                  "The native sheet offers Save/Discard/Keep editing and Escape safely keeps editing.")
        try editingAndLayout(model: model, bridge: bridge, account: account)
        print("PASS: native budget labels, shared inline validation, exact drafts, guarded navigation and fixed form actions.")
    }

    private static func editingAndLayout(model: AppModel, bridge: FixtureBridge, account: AccountData) throws {
        model.selectAccount(account.key)
        model.loadAccountDraft(account.key)
        try bridge.enqueue("completed", id: bridge.lastRequest["id"], values: ["preferences": [
            "displayName": "Synthetic", "thresholds": "", "showPeriodEstimate": false, "customBudgetUsd": 50
        ]])
        model.poll()
        let view = NSHostingView(rootView: AccountEditor(model: model, account: account).frame(width: 500, height: 400))
        let window = NSWindow(contentRect: NSRect(x: -10000, y: -10000, width: 500, height: 400),
                              styleMask: .borderless, backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = view
        window.orderBack(nil)
        defer { window.close() }
        func settle() {
            for _ in 0..<20 { view.layoutSubtreeIfNeeded(); RunLoop.current.run(until: Date().addingTimeInterval(0.01)) }
        }
        settle()
        func fields(_ root: NSView) -> [NSTextField] {
            (root as? NSTextField).map { [$0] } ?? root.subviews.flatMap { fields($0) }
        }
        guard let field = fields(view).first(where: { $0.stringValue == "50" && $0.isEditable }) else {
            throw AppError.message("The constrained native account form did not expose its budget text editor.")
        }
        field.scrollToVisible(field.bounds)
        window.makeFirstResponder(field)
        guard let editor = field.currentEditor() else { throw AppError.message("The budget field could not begin native editing.") }
        editor.string = "invalid"
        field.stringValue = "invalid"
        field.sendAction(field.action, to: field.target)
        model.accountDraft.budget = "invalid"
        model.saveChanges()
        settle()
        guard fields(view).contains(where: { $0 === field }), field.currentEditor() === editor,
              model.accountDraft.budget == "invalid", model.fieldErrors["budget"] != nil else {
            throw AppError.message("Inline validation replaced the active native editor or lost its focus/draft.")
        }
        editor.string = "50"
        field.stringValue = "50"
        field.sendAction(field.action, to: field.target)
        model.accountDraft.budget = "50"
        settle()
        guard fields(view).contains(where: { $0 === field }), field.currentEditor() === editor,
              model.fieldErrors["budget"] == nil else {
            throw AppError.message("Correcting inline validation replaced the native editor.")
        }
        window.makeFirstResponder(nil)
        model.accountDraft.budget = "75"
        settle()
        try NavigationInteractionTests.click(window, view: view, x: 452, fromTop: 370)
        guard model.saving, bridge.lastRequest["method"] as? String == "account.save" else {
            throw AppError.message("Save is not clickable in the fixed footer of a scrolled 500 x 400 account form.")
        }
        try bridge.enqueue("completed", id: bridge.lastRequest["id"])
        model.poll()
        model.accountDraft.budget = "80"
        settle()
        try NavigationInteractionTests.click(window, view: view, x: 385, fromTop: 370)
        guard model.accountDraft.budget == "75", !model.hasUnsavedChanges else {
            throw AppError.message("Cancel is not clickable in the fixed constrained account footer.")
        }
        for page in [SettingsPage.general, .notifications] {
            model.selectPage(page)
            model.prepareGlobalDraft()
            let preferences = NSHostingView(rootView: PreferencesView(model: model, notifications: page == .notifications)
                .frame(width: 500, height: 400))
            window.contentView = preferences
            for _ in 0..<20 { preferences.layoutSubtreeIfNeeded(); RunLoop.current.run(until: Date().addingTimeInterval(0.01)) }
            if page == .general { model.globalDraft.minutes = "15" }
            else { model.globalDraft.notifications = false }
            for _ in 0..<20 { preferences.layoutSubtreeIfNeeded(); RunLoop.current.run(until: Date().addingTimeInterval(0.01)) }
            try NavigationInteractionTests.click(window, view: preferences, x: 452, fromTop: 370)
            guard model.saving, bridge.lastRequest["method"] as? String == "settings.save" else {
                throw AppError.message("Save is not clickable in the constrained \(page.rawValue) footer.")
            }
            var saved = model.settings!
            if page == .general { saved.pollMinutes = 15 }
            else { saved.notifications = false }
            try bridge.enqueue("completed", id: bridge.lastRequest["id"], values: ["settings": try jsonObject(saved)])
            model.poll()
            if page == .general { model.globalDraft.minutes = "20" }
            else { model.globalDraft.notifications = true }
            for _ in 0..<20 { preferences.layoutSubtreeIfNeeded(); RunLoop.current.run(until: Date().addingTimeInterval(0.01)) }
            try NavigationInteractionTests.click(window, view: preferences, x: 385, fromTop: 370)
            guard !model.hasUnsavedChanges else {
                throw AppError.message("Cancel is not clickable in the constrained \(page.rawValue) footer.")
            }
        }
        var alert: NSAlert?
        model.unsavedChangesRequested = {
            let sheet = UnsavedChangesAlert.make()
            alert = sheet
            sheet.beginSheetModal(for: window) { response in
                model.resolveUnsavedChanges(UnsavedChangesAlert.choice(response))
            }
        }
        defer { model.unsavedChangesRequested = nil }
        func choose(_ index: Int) throws {
            guard let alert, window.attachedSheet != nil else { throw AppError.message("Native unsaved-changes sheet did not attach.") }
            alert.buttons[index].performClick(nil)
            for _ in 0..<20 { RunLoop.current.run(until: Date().addingTimeInterval(0.01)) }
        }
        model.globalDraft.increment = "invalid"
        model.selectPage(.about)
        try choose(2)
        guard model.page == .notifications, model.globalDraft.increment == "invalid" else {
            throw AppError.message("Native Keep Editing lost the selected page or draft.")
        }
        model.selectPage(.about)
        try choose(0)
        guard model.page == .notifications, model.fieldErrors["increment"] != nil, !model.saving else {
            throw AppError.message("Native Save navigated after invalid input.")
        }
        model.selectPage(.about)
        try choose(1)
        guard model.page == .about, !model.hasUnsavedChanges else {
            throw AppError.message("Native Discard did not restore the draft before navigation.")
        }
        model.selectPage(.notifications)
        model.prepareGlobalDraft()
        model.globalDraft.increment = "50"
        model.selectPage(.about)
        try choose(0)
        guard model.saving else { throw AppError.message("Native Save did not start persistence.") }
        try bridge.enqueue("completed", id: bridge.lastRequest["id"], values: ["error": "Synthetic save failed."])
        model.poll()
        guard model.page == .notifications, model.globalDraft.increment == "50", model.hasUnsavedChanges else {
            throw AppError.message("Native Save failure lost the draft or continued navigation.")
        }
        model.cancelChanges()
    }
}
