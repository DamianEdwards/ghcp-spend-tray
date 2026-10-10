import AppKit
import Carbon
import Foundation
import SwiftUI

@MainActor
enum PreferencesTests {
    static func run() throws {
        func check(_ condition: Bool, _ message: String) throws {
            if !condition { throw AppError.message(message) }
        }
        let bridge = FixtureBridge()
        let model = AppModel(directory: URL(fileURLWithPath: "/synthetic-preferences-unused"), demo: true, bridge: bridge)
        defer { model.shutdown() }
        model.initialized = true
        model.settings = try JSONDecoder().decode(SettingsData.self, from: Data("""
            {"pollMinutes":10,"thresholds":"50, 80, 100","notifications":true,"startup":false,
             "canChangeStartup":false,"startupDescription":"Synthetic","trayStyle":0,"trayMode":0}
            """.utf8))
        model.page = .accounts
        model.selectedAccount = "github.com:1"
        model.beginPreferences(accountKey: "github.com:1")
        try bridge.enqueue("completed", id: bridge.lastRequest["id"], values: ["preferences": [
            "displayName": "Synthetic", "thresholds": "", "showPeriodEstimate": false, "customBudgetUsd": 50
        ]])
        model.poll()
        let form = model.preferences
        try check(form.loaded && !form.dirty && form.account.useCustomBudget && form.account.budget == "50",
                  "Saved budget loads from the additive generated preference contract.")
        form.account.name = "Changed"
        form.account.name = "Synthetic"
        form.account.inherit = false
        form.account.increment = "invalid hidden draft"
        form.account.inherit = true
        try check(!form.dirty, "Returning names and inherited overrides to the baseline is clean.")
        form.account.showPeriodEstimate = true
        try check(form.dirty, "Estimate toggle participates in dirty comparison.")
        model.cancelPreferences()
        try check(!form.dirty && !form.account.showPeriodEstimate, "Cancel restores toggles and saved values.")

        form.account.name = String(repeating: "x", count: 129)
        form.account.thresholds = "50,,80"
        form.account.inherit = false
        form.account.increment = "1.001"
        form.account.budget = "0"
        let invalidDraft = form.account
        let beforeInvalid = bridge.requests.count
        model.savePreferences()
        try check(bridge.requests.count == beforeInvalid && form.account == invalidDraft &&
                  form.errors.count == 4 && !form.saving,
                  "Invalid saves highlight every account field, retain the exact draft, and never reach persistence.")
        form.account.name = "Synthetic"
        form.account.thresholds = "80, 50, 50"
        form.account.inherit = true
        form.account.useCustomBudget = false
        try check(form.errors.isEmpty, "Corrected fields and disabled overrides clear inline errors immediately.")
        model.cancelPreferences()
        form.account.name = "Renamed"
        model.savePreferences()
        try check(bridge.lastRequest["method"] as? String == "account.save" &&
                  bridge.lastRequest["updateCustomBudget"] == nil && bridge.lastRequest["customBudgetUsd"] == nil,
                  "Unrelated account saves omit budget arguments and preserve the target.")
        try bridge.enqueue("completed", id: bridge.lastRequest["id"])
        model.poll()
        try check(!form.dirty && !form.saving, "Only successful saves advance the baseline.")
        form.account.budget = "12.50"
        model.savePreferences()
        try check(bridge.lastRequest["updateCustomBudget"] as? Bool == true &&
                  (bridge.lastRequest["customBudgetUsd"] as? NSDecimalNumber)?.decimalValue == Decimal(string: "12.50"),
                  "Explicit budget edits send exact cents plus the update flag.")
        let failedDraft = form.account
        try bridge.enqueue("completed", id: bridge.lastRequest["id"], values: ["error": "Synthetic disk failure."])
        model.poll()
        try check(form.dirty && form.account == failedDraft && !form.saving && model.error == "Synthetic disk failure.",
                  "Asynchronous failures retain the draft and saved baseline.")
        bridge.requestError = "Synthetic receipt failure."
        model.savePreferences()
        try check(form.account == failedDraft && form.dirty && !form.saving && !model.busy,
                  "Immediate bridge failure also preserves the draft and releases saving.")
        bridge.requestError = nil
        model.cancelPreferences()
        form.account.useCustomBudget = false
        model.savePreferences()
        try check(bridge.lastRequest["updateCustomBudget"] as? Bool == true &&
                  bridge.lastRequest["customBudgetUsd"] is NSNull,
                  "Explicit API reset sends null plus true, not an omitted argument.")
        try bridge.enqueue("completed", id: bridge.lastRequest["id"])
        model.poll()

        var prompts = 0
        model.askUnsavedChanges = { prompts += 1 }
        form.account.name = "Dirty"
        model.navigate(.general)
        try check(prompts == 1 && model.page == .accounts && model.selectedAccount == "github.com:1",
                  "Sidebar navigation prompts without changing selected page or account.")
        model.resolveUnsavedChanges(.keepEditing)
        try check(form.account.name == "Dirty" && model.page == .accounts, "Keep editing preserves the draft and page.")
        model.editAccount("github.com:2")
        model.resolveUnsavedChanges(.discard)
        try check(model.selectedAccount == "github.com:2" && !form.dirty && form.account.name == "Renamed",
                  "Discard restores saved values before account-switch continuation.")
        model.selectedAccount = "github.com:1"
        form.account.budget = "0"
        form.account.useCustomBudget = true
        var continued = 0
        var cancelled = 0
        model.leaveForm({ continued += 1 }, cancelled: { cancelled += 1 })
        model.resolveUnsavedChanges(.save)
        try check(continued == 0 && cancelled == 1 && form.dirty && form.errors[.budget] != nil,
                  "Invalid Save cancels close/quit/navigation continuations.")
        form.account.budget = "50"
        model.leaveForm({ continued += 1 }, cancelled: { cancelled += 1 })
        model.resolveUnsavedChanges(.save)
        let pendingSave = bridge.lastRequest
        model.navigate(.notifications)
        model.editAccount(nil)
        model.addAccount()
        model.reconnectAccount(try account())
        try check(form.saving && model.page == .accounts && model.selectedAccount == "github.com:1" &&
                  !model.showingSignIn && continued == 0,
                  "In-flight save blocks sidebar, Back, account switches, Add and Reconnect.")
        try bridge.enqueue("completed", id: pendingSave["id"], values: ["error": "Synthetic failure."])
        model.poll()
        try check(continued == 0 && cancelled == 2 && form.dirty, "Failed Save cancels the original leave action.")
        model.leaveForm({ continued += 1 }, cancelled: { cancelled += 1 })
        model.resolveUnsavedChanges(.save)
        try bridge.enqueue("completed", id: bridge.lastRequest["id"])
        model.poll()
        try check(continued == 1 && !form.dirty, "Save continues the original action exactly once after persistence.")
        form.account.name = "Leave test"
        let reconnectAccount = try account()
        for action in [ { model.editAccount(nil) }, { model.addAccount() }, { model.reconnectAccount(reconnectAccount) } ] {
            action()
            model.resolveUnsavedChanges(.keepEditing)
            try check(form.account.name == "Leave test" && model.selectedAccount == "github.com:1" &&
                      !model.showingSignIn, "Back, Add and Reconnect Keep editing preserve the account form.")
        }
        model.cancelPreferences()
        model.navigate(.general)
        model.beginPreferences()
        form.global.minutes = "4"
        model.savePreferences()
        try check(form.errors[.minutes] != nil && form.global.minutes == "4", "General interval has inline validation.")
        form.global.minutes = "1440"
        try check(form.errors[.minutes] == nil, "Interval correction clears its error.")
        let savedSettings = model.settings!
        try bridge.enqueue("state", id: nil, values: ["settings": try jsonObject(savedSettings)])
        model.poll()
        try check(form.global.minutes == "1440" && form.dirty, "Activation/background settings events cannot replace dirty text.")
        form.global.minutes = "10"
        form.global.trayStyle = .percentage
        form.global.trayStyle = .pie
        form.global.excludedAccounts.insert("github.com:1")
        form.global.excludedAccounts.remove("github.com:1")
        try check(!form.dirty, "Tray enums and selected-account sets return clean at the baseline.")
        model.navigate(.notifications)
        model.beginPreferences()
        form.global.thresholds = "0"
        form.global.increment = "-1"
        form.global.enabled = false
        model.savePreferences()
        try check(form.errors[.thresholds] != nil && form.errors[.increment] != nil && !form.global.enabled,
                  "Notification invalid save retains toggle and both fields.")
        model.cancelPreferences()
        try check(form.global.enabled && !form.dirty && form.errors.isEmpty, "Notification Cancel restores the baseline.")
        for reason in [kAEReallyLogOut, kAEShutDown, kAERestart] {
            let event = NSAppleEventDescriptor.appleEvent(withEventClass: AEEventClass(kCoreEventClass),
                eventID: AEEventID(kAEQuitApplication), targetDescriptor: nil, returnID: AEReturnID(kAutoGenerateReturnID),
                transactionID: AETransactionID(kAnyTransactionID))
            event.setParam(NSAppleEventDescriptor(enumCode: OSType(reason)), forKeyword: AEKeyword(kAEQuitReason))
            try check(TerminationPolicy.isNoninteractive(event), "Noninteractive OS shutdown must not wait for a draft prompt.")
        }
        try check(!TerminationPolicy.isNoninteractive(nil), "Ordinary app Quit remains interactive.")

        for text in ["50", "12.50", ".50", "50.", "12.500"] {
            try check(try parseUSD(text, budget: true) != nil, "Native amount parser accepts the shared allowed values.")
        }
        for text in ["", "0", "-1", "1.001", "1e2", " 50 ", "79228162514264337593543950336"] {
            do {
                _ = try parseUSD(text, budget: true)
                throw AppError.message("Invalid budget accepted: \(text)")
            } catch AppError.message(let message) {
                if message.hasPrefix("Invalid budget accepted") { throw AppError.message(message) }
            }
        }
        let custom = try account(custom: true, unlimited: true)
        try check(custom.usageSummary == "33% of $50.00 custom budget" && custom.details.unlimited &&
                  custom.details.observedAllocationUsd == nil,
                  "Finite custom target is labeled independently of unlimited raw API diagnostics.")
        try check(try account(custom: true).usageSummary == "33% of $50.00 custom budget",
                  "Unknown API allocation still permits a clearly labeled finite custom target.")
        try check(try account(custom: false, unlimited: true).usageSummary == "Unlimited API allocation",
                  "Unlimited API without a target never looks like zero.")
        try check(try account(custom: true, unavailable: true).usageSummary.contains("consumption unavailable"),
                  "Saved budget never fabricates unavailable consumption.")
        try renderForms(model)
        print("PASS: Mac budget contracts/labels, inline validation, draft baselines and guarded Save/Discard/Keep editing.")
    }

    private static func account(custom: Bool = false, unlimited: Bool = false, unavailable: Bool = false) throws -> AccountData {
        var fields: [String: Any] = [
            "key": "github.com:1", "name": "Synthetic", "login": "fixture", "host": "github.com",
            "details": ["unlimited": unlimited, "isCurrentPeriod": !unavailable], "freshness": unavailable ? "Stale" : "Fresh"
        ]
        if custom { fields["customBudgetUsd"] = 50; fields["allocationUsd"] = 50 }
        if !unavailable { fields["consumptionUsd"] = 16.5; if custom { fields["percent"] = 33 } }
        return try JSONDecoder().decode(AccountData.self, from: JSONSerialization.data(withJSONObject: fields))
    }

    private static func renderForms(_ model: AppModel) throws {
        let window = NSWindow(contentRect: NSRect(x: -10000, y: -10000, width: 500, height: 400),
                              styleMask: .borderless, backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        defer { window.close() }
        let account = try account(custom: true)
        for (name, view) in [
            ("AccountPreferences", AnyView(AccountEditor(model: model, form: model.preferences, account: account))),
            ("GeneralPreferences", AnyView(PreferencesView(model: model, form: model.preferences, notifications: false))),
            ("NotificationPreferences", AnyView(PreferencesView(model: model, form: model.preferences, notifications: true)))
        ] {
            if name == "AccountPreferences" {
                model.page = .accounts
                model.selectedAccount = account.key
                model.preferences.begin("account:\(account.key)")
                model.preferences.load(try JSONDecoder().decode(AccountPreferences.self, from: Data("""
                    {"displayName":"Synthetic","thresholds":"","showPeriodEstimate":false,"customBudgetUsd":50}
                    """.utf8)), context: "account:\(account.key)")
            } else {
                model.page = name == "GeneralPreferences" ? .general : .notifications
                model.beginPreferences()
            }
            let layout = PreferenceLayoutProbe()
            let host = NSHostingView(rootView: view.frame(width: 500, height: 400)
                .overlayPreferenceValue(PreferenceBounds.self) { anchors in
                    GeometryReader { geometry in
                        let frames = anchors.mapValues { geometry[$0] }
                        Color.clear.onAppear { layout.frames = frames }
                            .onChange(of: frames) { _, value in layout.frames = value }
                    }.allowsHitTesting(false)
                })
            window.contentView = host
            window.orderBack(nil)
            try waitForUI("fixed actions in \(name)") {
                host.layoutSubtreeIfNeeded()
                return layout.frames["SavePreferences"] != nil && layout.frames["CancelPreferences"] != nil
            }
            guard let saveFrame = layout.frames["SavePreferences"], let cancelFrame = layout.frames["CancelPreferences"] else {
                throw AppError.message("Fixed preference actions did not lay out.")
            }
            guard host.bounds.contains(saveFrame), host.bounds.contains(cancelFrame),
                  saveFrame.maxY > 330, cancelFrame.maxY > 330 else {
                throw AppError.message("Save and Cancel must remain visible at the bottom of the constrained form.")
            }
            if name == "GeneralPreferences" {
                try stableEditor(model, host: host, window: window, layout: layout)
            }
            guard let bitmap = host.bitmapImageRepForCachingDisplay(in: host.bounds) else {
                throw AppError.message("Constrained preference form did not render.")
            }
            host.cacheDisplay(in: host.bounds, to: bitmap)
            guard let png = bitmap.representation(using: .png, properties: [:]), png.count > 1000 else {
                throw AppError.message("Constrained preference form render was empty.")
            }
            try png.write(to: URL(fileURLWithPath: "artifacts/macos/\(name)Constrained.png"))
        }
    }

    private static func stableEditor(_ model: AppModel, host: NSView, window: NSWindow,
                                     layout: PreferenceLayoutProbe) throws {
        guard let input = textField(in: host) else {
            throw AppError.message("Refresh interval editor missing.")
        }
        input.selectText(nil)
        try waitForUI("focused interval field editor") { window.firstResponder is NSTextView }
        guard let editor = window.firstResponder as? NSTextView else {
            throw AppError.message("Refresh interval did not create a field editor.")
        }
        editor.selectAll(nil)
        editor.insertText("4", replacementRange: editor.selectedRange())
        let originalEditor = editor
        let selection = editor.selectedRange()
        let originalActions = ["SavePreferences": layout.frames["SavePreferences"],
                               "CancelPreferences": layout.frames["CancelPreferences"]]
        model.savePreferences()
        try waitForUI("inline interval validation message") { layout.frames["PollMinutesError"] != nil }
        guard model.preferences.global.minutes == "4", model.preferences.errors[.minutes] != nil,
              window.firstResponder === originalEditor, editor.selectedRange() == selection,
              (layout.frames["PollMinutesError"]?.height ?? 0) > 0,
              layout.frames["SavePreferences"] == originalActions["SavePreferences"]!,
              layout.frames["CancelPreferences"] == originalActions["CancelPreferences"]! else {
            throw AppError.message("Inline validation must preserve the actual field editor, selection and draft.")
        }
        editor.selectAll(nil)
        editor.insertText("15", replacementRange: editor.selectedRange())
        try waitForUI("corrected interval validation") { model.preferences.errors[.minutes] == nil }
        guard model.preferences.global.minutes == "15", window.firstResponder === originalEditor else {
            throw AppError.message("Correcting validation must not replace the focused editor.")
        }
        model.cancelPreferences()
    }

    private static func textField(in view: NSView) -> NSTextField? {
        if let field = view as? NSTextField, field.isEditable { return field }
        for child in view.subviews {
            if let field = textField(in: child) { return field }
        }

        return nil
    }

    private static func waitForUI(_ phase: String, _ ready: () -> Bool) throws {
        let deadline = Date().addingTimeInterval(5)
        while !ready() {
            guard Date() < deadline else { throw AppError.message("Timed out waiting for \(phase).") }
            RunLoop.current.run(until: Date().addingTimeInterval(0.01))
        }
    }

    @MainActor
    private final class PreferenceLayoutProbe {
        var frames: [String: CGRect] = [:]
    }
}
