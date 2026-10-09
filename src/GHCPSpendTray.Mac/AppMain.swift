import AppKit
import Darwin
import SwiftUI
import UserNotifications

#if !arch(arm64)
#error("GHCPSpendTray for macOS supports only Apple silicon (arm64).")
#endif

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSMenuItemValidation, UNUserNotificationCenterDelegate {
    private var model: AppModel?
    private var statusItems: [String: NSStatusItem] = [:]
    private let popover = NSPopover()
    private var settingsWindow: NSWindow?
    private var instanceLock: Int32 = -1
    private var wakeObserver: NSObjectProtocol?
    private var activationObserver: NSObjectProtocol?
    private var smoke = false
    private var empty = false
    #if UPDATE_REHEARSAL
    private var rehearsal: UpdateRehearsal?
    #endif
    private let activationName = Notification.Name("com.damianedwards.GHCPSpendTray.activate")

    func applicationDidFinishLaunching(_ notification: Notification) {
        do {
            let arguments = Array(CommandLine.arguments.dropFirst())
            smoke = arguments.contains("--smoke-test")
            empty = arguments.contains("--demo-empty")
            var demo = smoke || empty || arguments.contains("--demo")
            let allowed = ["--smoke-test", "--demo", "--demo-empty", "--data-dir"]
            var dataPath: String?
            var index = 0
            while index < arguments.count {
                guard allowed.contains(arguments[index]) else { throw AppError.message("Unknown launch argument.") }
                if arguments[index] == "--data-dir" {
                    index += 1
                    guard index < arguments.count, dataPath == nil else { throw AppError.message("Supply one isolated data directory.") }
                    dataPath = arguments[index]
                }
                index += 1
            }
            #if UPDATE_REHEARSAL
            rehearsal = try UpdateRehearsal.load()
            if let rehearsal {
                guard arguments.isEmpty else { throw AppError.message("Rehearsal launches do not accept arguments.") }
                demo = true
                dataPath = rehearsal.directory.path
            }
            #endif
            let support = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
            let standard = support.appendingPathComponent("GHCPSpendTray", isDirectory: true)
            let directory: URL
            if demo {
                guard let dataPath, dataPath.hasPrefix("/") else { throw AppError.message("Demo and smoke modes require --data-dir <absolute isolated directory>.") }
                directory = URL(fileURLWithPath: dataPath, isDirectory: true).standardizedFileURL
                guard directory.resolvingSymlinksInPath() != standard.resolvingSymlinksInPath(),
                      directory.pathComponents.count > 3 else {
                    throw AppError.message("Demonstration data must be isolated from normal application data.")
                }
            } else {
                guard dataPath == nil else { throw AppError.message("A data-directory override is only supported in isolated demo mode.") }
                directory = standard
            }
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
            instanceLock = Darwin.open(directory.appendingPathComponent("instance.lock").path, O_CREAT | O_RDWR | O_CLOEXEC | O_NOFOLLOW, 0o600)
            guard instanceLock >= 0 else { throw AppError.message("Cannot open the application instance lock. Check data-folder permissions.") }
            if flock(instanceLock, LOCK_EX | LOCK_NB) != 0 {
                guard errno == EWOULDBLOCK else { throw AppError.message("Cannot lock the application data directory.") }
                if !demo { DistributedNotificationCenter.default().postNotificationName(activationName, object: nil, userInfo: nil, deliverImmediately: true) }
                NSApplication.shared.terminate(nil)
                return
            }
            var updates = AppUpdates()
            #if UPDATE_REHEARSAL
            if let rehearsal { updates = AppUpdates.rehearsal(rehearsal) }
            #endif
            let model = AppModel(directory: directory, demo: demo, updates: updates)
            self.model = model
            model.showSettings = { [weak self] in self?.openSettings() }
            model.dashboardChanged = { [weak self] dashboard in self?.updateMenuBar(dashboard.tray) }
            setupMenuBar(model)
            UNUserNotificationCenter.current().delegate = self
            wakeObserver = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak model] _ in
                MainActor.assumeIsolated { if model?.initialized == true { model?.perform("resume") } }
            }
            if !demo {
                activationObserver = DistributedNotificationCenter.default().addObserver(forName: activationName, object: nil, queue: .main) { [weak self] _ in
                    MainActor.assumeIsolated { self?.openSettings() }
                }
            }
            model.start(empty: empty)
            model.updates.start(channel: Bundle.main.object(forInfoDictionaryKey: "GHCPReleaseChannel") as? String ?? "Development",
                                isolated: demo && !isUpdateRehearsal)
            #if UPDATE_REHEARSAL
            rehearsal?.run(model)
            #endif
            if smoke { Task { await runSmoke(model) } }
        } catch {
            #if UPDATE_REHEARSAL
            if Bundle.main.object(forInfoDictionaryKey: "GHCPUpdateRehearsal") != nil {
                fputs("FAIL: update rehearsal startup: \(error.localizedDescription)\n", stderr)
                exit(1)
            }
            #endif
            if smoke { fputs("FAIL: macOS startup smoke test.\n", stderr); exit(1) }
            let alert = NSAlert()
            alert.messageText = "GHCPSpendTray could not start"
            alert.informativeText = (error as? AppError)?.errorDescription ?? "Check local data-directory permissions and available disk space."
            alert.alertStyle = .critical
            alert.runModal()
            NSApplication.shared.terminate(nil)
        }
    }

    private var isUpdateRehearsal: Bool {
        #if UPDATE_REHEARSAL
        return rehearsal != nil && rehearsal?.configuration.scenario != "cleanup"
        #else
        return false
        #endif
    }

    private func setupMenuBar(_ model: AppModel) {
        updateMenuBar(nil)
        popover.behavior = .transient
        let controller = NSHostingController(rootView: FlyoutView(model: model))
        controller.sizingOptions.insert(.preferredContentSize)
        popover.contentViewController = controller
        let mainMenu = NSMenu()
        let applicationItem = NSMenuItem()
        mainMenu.addItem(applicationItem)
        let applicationMenu = NSMenu()
        applicationMenu.addItem(withTitle: "Settings...", action: #selector(settingsAction), keyEquivalent: ",").target = self
        applicationMenu.addItem(withTitle: "Check for Updates...", action: #selector(updateAction), keyEquivalent: "").target = self
        applicationMenu.addItem(.separator())
        applicationMenu.addItem(withTitle: "Quit GHCPSpendTray", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        applicationItem.submenu = applicationMenu
        let editItem = NSMenuItem()
        let editMenu = NSMenu(title: "Edit")
        editMenu.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        editMenu.addItem(.separator())
        editMenu.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        editMenu.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = editMenu
        mainMenu.addItem(editItem)
        NSApplication.shared.mainMenu = mainMenu
    }

    private func updateMenuBar(_ presentation: TrayPresentation?) {
        let icons = presentation?.icons ?? [.unavailable]
        let keys = Set(icons.map(\.id))
        for key in Array(statusItems.keys) where !keys.contains(key) {
            if popover.isShown { popover.performClose(nil) }
            if let item = statusItems.removeValue(forKey: key) { NSStatusBar.system.removeStatusItem(item) }
        }
        for icon in icons {
            let item: NSStatusItem
            if let existing = statusItems[icon.id] { item = existing }
            else {
                item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
                statusItems[icon.id] = item
            }
            guard let button = item.button else {
                model?.error = "macOS could not create a menu-bar indicator. Reopen the app to access Settings."
                continue
            }
            button.image = TrayIconRenderer.image(icon, style: presentation?.style ?? .pie)
            button.toolTip = icon.tooltip
            button.setAccessibilityLabel(icon.details)
            button.target = self
            button.action = #selector(togglePopover(_:))
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
        }
    }

    @objc private func togglePopover(_ button: NSStatusBarButton) {
        guard let item = statusItems.values.first(where: { $0.button === button }) else { return }
        if NSApplication.shared.currentEvent?.type == .rightMouseUp {
            popover.performClose(nil)
            let menu = NSMenu()
            menu.addItem(withTitle: "Open", action: #selector(showUsage), keyEquivalent: "").target = self
            let refresh = menu.addItem(withTitle: "Refresh Now", action: #selector(refreshAction), keyEquivalent: "")
            refresh.target = self
            refresh.isEnabled = model?.initialized == true && model?.busy == false
            menu.addItem(withTitle: "Settings...", action: #selector(settingsAction), keyEquivalent: ",").target = self
            let update = menu.addItem(withTitle: model?.updates.actionTitle ?? "Check for Updates...",
                                      action: #selector(updateAction), keyEquivalent: "")
            update.target = self
            update.isEnabled = model?.updates.enabled == true && model?.updates.state.canCheck == true
            menu.addItem(.separator())
            menu.addItem(withTitle: "Quit GHCPSpendTray", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
            menu.autoenablesItems = false
            item.menu = menu
            button.performClick(nil)
            item.menu = nil
        } else if let key = statusItems.first(where: { $0.value === item })?.key, key != "rollup" {
            model?.selectedAccount = key
            model?.openSettings(.accounts)
        } else if popover.isShown {
            popover.performClose(nil)
        } else {
            guard let view = popover.contentViewController?.view else {
                model?.error = "The menu-bar popup could not be created. Reopen the app to access Settings."
                return
            }
            view.layoutSubtreeIfNeeded()
            popover.contentSize = view.fittingSize
            NSApplication.shared.activate()
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
            guard popover.isShown, let window = view.window else {
                model?.error = "The menu-bar popup could not be shown. Reopen the app to access Settings."
                return
            }
            window.makeKeyAndOrderFront(nil)
        }
    }

    @objc private func showUsage() { model?.openSettings(.usage) }
    @objc private func settingsAction() { openSettings() }
    @objc private func refreshAction() { model?.perform("refresh") }
    @objc private func updateAction() {
        popover.performClose(nil)
        model?.updates.checkForUpdates()
    }

    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        if menuItem.action == #selector(updateAction) {
            menuItem.title = model?.updates.actionTitle ?? "Check for Updates..."
            return model?.updates.enabled == true && model?.updates.state.canCheck == true
        }
        return true
    }

    private func openSettings() {
        guard let model else { return }
        popover.performClose(nil)
        if settingsWindow == nil {
            let window = NSWindow(contentViewController: NSHostingController(rootView: SettingsView(model: model)))
            window.title = "GHCPSpendTray"
            window.styleMask = [.titled, .closable, .miniaturizable, .resizable]
            window.setContentSize(NSSize(width: 850, height: 660))
            window.minSize = NSSize(width: 730, height: 550)
            if !model.demo { window.setFrameAutosaveName("GHCPSpendTray.Settings") }
            window.isReleasedWhenClosed = false
            window.delegate = self
            window.center()
            settingsWindow = window
        }
        NSApplication.shared.activate()
        settingsWindow?.makeKeyAndOrderFront(nil)
        if model.initialized && !model.busy && !model.showingSignIn { model.perform("resume") }
    }

    func windowWillClose(_ notification: Notification) {
        model?.cancelSignIn()
        model?.showingSignIn = false
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        openSettings()
        return false
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        model?.cancelSignIn()
        return .terminateNow
    }

    func applicationWillTerminate(_ notification: Notification) {
        model?.shutdown()
        if let wakeObserver { NSWorkspace.shared.notificationCenter.removeObserver(wakeObserver) }
        if let activationObserver { DistributedNotificationCenter.default().removeObserver(activationObserver) }
        if instanceLock >= 0 { Darwin.close(instanceLock) }
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter,
        willPresent notification: UNNotification, withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .sound, .list])
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter,
        didReceive response: UNNotificationResponse, withCompletionHandler completionHandler: @escaping () -> Void) {
        let key = response.notification.request.content.userInfo["accountKey"] as? String
        Task { @MainActor [weak self] in
            self?.model?.selectedAccount = key
            self?.model?.openSettings(key == nil ? .usage : .accounts)
        }
        completionHandler()
    }

    private func runSmoke(_ model: AppModel) async {
        func progress(_ phase: String) {
            FileHandle.standardError.write(Data("SMOKE: \(phase)\n".utf8))
        }
        func savePopupSnapshot(_ name: String) throws {
            guard let view = popover.contentViewController?.view,
                  let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else {
                throw AppError.message("Menu-bar popup did not render.")
            }
            view.cacheDisplay(in: view.bounds, to: bitmap)
            guard let png = bitmap.representation(using: .png, properties: [:]) else {
                throw AppError.message("Menu-bar popup render was empty.")
            }
            try png.write(to: model.directory.appendingPathComponent("\(name).png"))
        }
        func saveSettingsSnapshot(_ name: String) throws {
            guard let view = settingsWindow?.contentView,
                  let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else {
                throw AppError.message("Settings view did not render.")
            }
            view.cacheDisplay(in: view.bounds, to: bitmap)
            guard bitmap.pixelsWide >= 730, let png = bitmap.representation(using: .png, properties: [:]) else {
                throw AppError.message("Settings render was empty.")
            }
            try png.write(to: model.directory.appendingPathComponent("\(name).png"))
        }
        do {
            progress("waiting for shared initialization")
            try await waitUntil { model.initialized && !model.busy }
            guard !model.updates.enabled, model.updates.state.availableVersion == nil,
                  model.updates.state.error == nil else {
                throw AppError.message("Isolated smoke mode must not start the updater.")
            }
            // Read-only native callback coverage: no permission prompt, alert, or account access.
            progress("reading native notification settings")
            _ = await NativeNotifications().status()
            print("PASS: native notification settings callback.")
            guard model.error == nil, let dashboard = model.dashboard,
                  dashboard.accounts.count == (empty ? 0 : 2),
                  dashboard.consumptionUsd == (empty ? nil : Decimal(string: "42.75")),
                  money(dashboard.consumptionUsd) == (empty ? "Unavailable" : "$42.75"),
                  model.settings?.startup == false else { throw AppError.message("Synthetic dashboard contract failed.") }
            guard statusItems.count == 1, statusItems["rollup"]?.button?.image?.isTemplate == true else {
                throw AppError.message("Adaptive menu bar indicator was not created.")
            }
            progress("checking menu-bar popup")
            guard let button = statusItems["rollup"]?.button else {
                throw AppError.message("Roll-up menu-bar item missing.")
            }
            try await waitForPopupAnchor(button, phase: "initial menu-bar placement", presented: false)
            togglePopover(button)
            try await waitForPopupAnchor(button, phase: "\(empty ? "empty" : "populated") initial presentation") { bounds in
                bounds.width == 400 && (!self.empty || bounds.height < 300)
            }
            guard let flyout = popover.contentViewController?.view else {
                throw AppError.message("Menu-bar popup did not render.")
            }
            try savePopupSnapshot("Flyout")
            progress("checking popup growth and shrinkage")
            let height = flyout.bounds.height
            model.notice = "Synthetic popup height change.\nA second line exercises content growth."
            try await waitForPopupAnchor(button, phase: "notice growth beyond \(height) points") { bounds in
                bounds.width == 400 && bounds.height > height + 1
            }
            model.notice = nil
            try await waitForPopupAnchor(button, phase: "notice shrinkage to \(height) points") { bounds in
                bounds.width == 400 && abs(bounds.height - height) <= 1
            }
            popover.performClose(nil)
            openSettings()
            progress("opening settings")
            try await waitUntil { !model.busy }
            for page in SettingsPage.allCases {
                progress("rendering \(page.rawValue)")
                model.page = page
                try await Task.sleep(for: .milliseconds(250))
                try saveSettingsSnapshot(page.rawValue)
            }
            if var settings = model.settings {
                progress("saving and previewing menu-bar settings")
                settings.pollMinutes = 15
                settings.spendIncrementUsd = 50
                model.perform("settings.save", fields: ["settings": try jsonObject(settings)])
                try await waitUntil { !model.busy }
                guard model.error == nil, model.settings?.pollMinutes == 15, model.settings?.spendIncrementUsd == 50 else {
                    throw AppError.message("Settings did not round-trip across the native bridge.")
                }
                settings.trayStyle = .percentage
                settings.trayMode = .perAccount
                var preview: TrayPresentation?
                model.send("tray.preview", fields: ["settings": try jsonObject(settings)]) { preview = $0.tray }
                try await waitUntil { preview != nil }
                guard self.statusItems.count == 1, model.settings?.trayStyle == .pie else {
                    throw AppError.message("Draft tray preview changed the real menu bar.")
                }
                model.perform("settings.save", fields: ["settings": try jsonObject(settings)])
                try await waitUntil { !model.busy && self.statusItems.count == (self.empty ? 1 : 2) }
                guard model.error == nil, model.settings?.trayStyle == .percentage,
                      model.dashboard?.tray?.rollUp.percent == (empty ? nil : 34.2) else {
                    throw AppError.message("Saved menu-bar options did not update native indicators.")
                }
                if !empty {
                    guard let button = statusItems["github.com:1"]?.button else {
                        throw AppError.message("Account menu-bar item missing.")
                    }
                    togglePopover(button)
                    try await waitUntil { !model.busy }
                    guard model.selectedAccount == "github.com:1", model.page == .accounts else {
                        throw AppError.message("Per-account indicator did not navigate to the account.")
                    }
                    settings.excludedTrayAccounts = ["github.com:1"]
                    model.perform("settings.save", fields: ["settings": try jsonObject(settings)])
                    try await waitUntil { !model.busy && self.statusItems.count == 1 }
                    guard statusItems["example.ghe.com:2"] != nil && model.dashboard?.consumptionUsd == Decimal(string: "42.75") else {
                        throw AppError.message("Account selection changed dollar totals or failed to remove its indicator.")
                    }
                }
                settings.excludedTrayAccounts = model.dashboard?.accounts.map(\.key)
                model.perform("settings.save", fields: ["settings": try jsonObject(settings)])
                try await waitUntil { !model.busy && self.statusItems["rollup"] != nil }
                guard statusItems.count == 1, model.dashboard?.tray?.rollUp.percent == nil else {
                    throw AppError.message("Unavailable access indicator did not replace excluded accounts.")
                }
                settings.trayStyle = .pie
                settings.trayMode = .rollUp
                settings.excludedTrayAccounts = []
                model.perform("settings.save", fields: ["settings": try jsonObject(settings)])
                try await waitUntil { !model.busy }
            }
            progress("adding an example account to the open popup")
            model.notice = nil
            guard let sampleButton = statusItems["rollup"]?.button else {
                throw AppError.message("Sample popup menu-bar item missing.")
            }
            try await waitForPopupAnchor(sampleButton, phase: "example menu-bar placement", presented: false)
            togglePopover(sampleButton)
            try await waitForPopupAnchor(sampleButton, phase: "presentation before example insertion")
            let previousHeight = flyout.bounds.height
            let previousCount = model.dashboard?.accounts.count ?? 0
            let previousConsumption = model.dashboard?.consumptionUsd ?? 0
            model.addExampleAccount()
            try await waitUntil { !model.busy }
            try await waitForPopupAnchor(sampleButton, phase: "example insertion beyond \(previousHeight) points") { bounds in
                bounds.width == 400 && (!self.empty || bounds.height > previousHeight + 1)
            }
            guard model.error == nil, popover.isShown, !model.showingSignIn,
                  model.dashboard?.accounts.count == previousCount + 1,
                  model.dashboard?.consumptionUsd == previousConsumption + 12.5,
                  model.dashboard?.accounts.last?.percent == 25,
                  !empty || flyout.bounds.height > previousHeight else {
                throw AppError.message("Example account did not update and resize the open popup without sign-in.")
            }
            try savePopupSnapshot("FlyoutWithExample")
            progress("checking per-account estimate opt-in")
            guard let example = model.dashboard?.accounts.last, example.periodEstimate == nil else {
                throw AppError.message("New sample account must default its estimate off.")
            }
            let actual = model.dashboard?.consumptionUsd
            let percentage = model.dashboard?.tray?.rollUp.percent
            model.perform("account.save", fields: ["key": example.key, "displayName": example.name,
                                                   "showPeriodEstimate": true])
            try await waitUntil { !model.busy }
            try await waitForPopupAnchor(sampleButton, phase: "account estimate opt-in")
            guard model.error == nil, model.dashboard?.accounts.last?.periodEstimate != nil,
                  model.dashboard?.accounts.dropLast().allSatisfy({ $0.periodEstimate == nil }) == true,
                  model.dashboard?.consumptionUsd == actual, model.dashboard?.tray?.rollUp.percent == percentage else {
                throw AppError.message("Account estimate changed actual totals, other accounts or allocation indicators.")
            }
            try savePopupSnapshot("FlyoutWithEstimate")
            var preferences: AccountPreferences?
            model.send("account.preferences", fields: ["key": example.key]) { preferences = $0.preferences }
            try await waitUntil { preferences != nil }
            guard preferences?.showPeriodEstimate == true else {
                throw AppError.message("Account estimate preference did not round-trip.")
            }
            popover.performClose(nil)
            model.selectedAccount = example.key
            model.openSettings(.accounts)
            try await Task.sleep(for: .milliseconds(500))
            guard model.error == nil else { throw AppError.message("Account estimate settings did not load.") }
            try saveSettingsSnapshot("AccountWithEstimate")
            model.perform("account.save", fields: ["key": example.key, "displayName": example.name,
                                                   "showPeriodEstimate": false])
            try await waitUntil { !model.busy }
            guard model.error == nil, model.dashboard?.accounts.last?.periodEstimate == nil else {
                throw AppError.message("Disabling an account estimate must remove the forecast.")
            }
            model.addAccount()
            progress("opening onboarding")
            try await Task.sleep(for: .milliseconds(250))
            guard model.showingSignIn, model.page == .accounts else {
                throw AppError.message("Account onboarding did not open.")
            }
            model.showingSignIn = false
            try await waitUntil { self.settingsWindow?.attachedSheet == nil }
            try "PASS: native menu bar, five settings pages, synthetic dashboard and settings bridge.\n"
                .write(to: model.directory.appendingPathComponent("smoke-result.txt"), atomically: true, encoding: .utf8)
            print("PASS: macOS arm64 \(empty ? "empty" : "populated") Native AOT / SwiftUI smoke test.")
            progress("terminating")
            NSApplication.shared.terminate(nil)
        } catch {
            fputs("FAIL: macOS synthetic smoke test: \(error.localizedDescription)\n", stderr)
            model.shutdown()
            exit(1)
        }
    }

    private func waitForPopupAnchor(_ button: NSStatusBarButton, phase: String, presented: Bool = true,
                                    contentReady: (NSRect) -> Bool = { $0.width == 400 }) async throws {
        let clock = ContinuousClock()
        let start = clock.now
        var readiness = PopupSmokeReadiness()
        var firstDiagnostic: String?
        var samples = 0
        while true {
            let anchorWindow = button.window
            let controller = popover.contentViewController
            let view = controller?.view
            let popupWindow = view?.window
            let screen = anchorWindow?.screen
            let anchor = anchorWindow?.convertToScreen(button.convert(button.bounds, to: nil))
            var frames: [NSRect] = []
            var failure: String?
            if let anchor, let screen, let anchorWindow {
                frames = [anchor, button.bounds, button.frame, anchorWindow.frame, screen.frame, screen.visibleFrame]
                if !anchorWindow.isVisible || button.isHiddenOrHasHiddenAncestor || button.visibleRect.isEmpty {
                    failure = "Menu-bar button is not visible."
                } else {
                    failure = PopupSmokeGeometry.anchorFailure(anchor, screen: screen.frame)
                }
                if presented {
                    if let popupWindow, let windowContentView = popupWindow.contentView, let view, let controller {
                        // Preferred-size updates can resize the real window without updating NSPopover.contentSize.
                        let geometry = PopupSmokeGeometry(anchor: anchor, popup: popupWindow.frame,
                            screen: screen.frame, visibleScreen: screen.visibleFrame, contentBounds: view.bounds,
                            windowContentBounds: windowContentView.bounds, preferredContentSize: controller.preferredContentSize)
                        frames += geometry.stabilityFrames + [popover.positioningRect, view.frame]
                        if failure == nil {
                            if !popover.isShown || !popupWindow.isVisible || view.isHiddenOrHasHiddenAncestor {
                                failure = "Popup is not shown and visible."
                            } else if !NSApplication.shared.isActive || !popupWindow.isKeyWindow {
                                failure = "Popup has not become the active key window."
                            } else if popover.isDetached {
                                failure = "Popup is detached from its positioning view."
                            } else if popupWindow.screen != screen {
                                failure = "Popup and menu-bar anchor are on different screens."
                            } else if let geometryFailure = geometry.failure {
                                failure = geometryFailure
                            } else if !contentReady(view.bounds) {
                                failure = "Expected content size for \(phase) has not appeared."
                            }
                        }
                    } else {
                        failure = failure ?? "Popup window, window content or hosting view is missing."
                    }
                }
            } else {
                failure = "Menu-bar anchor window or screen is missing."
            }
            samples += 1
            let diagnostic = popupSmokeDiagnostic(button, anchor: anchor)
            if firstDiagnostic == nil { firstDiagnostic = "readiness=\(failure ?? "geometry candidate"); \(diagnostic)" }
            let elapsed = start.duration(to: clock.now)
            switch readiness.observe(frames: frames, failure: failure, elapsed: elapsed) {
            case .ready:
                let attachment: String
                if presented, let anchor, let popupWindow {
                    attachment = "; gap=\(anchor.minY - popupWindow.frame.maxY) points"
                } else {
                    attachment = "; visible menu-bar button"
                }
                FileHandle.standardError.write(Data("SMOKE: stable \(phase) after \(elapsed), \(samples) samples\(attachment).\n".utf8))
                return
            case .timedOut:
                throw AppError.message("Timed out waiting for \(phase) after \(elapsed), \(samples) samples: \(readiness.reason)\nFirst geometry: \(firstDiagnostic ?? "missing")\nLatest geometry: \(diagnostic)")
            case .waiting:
                try await Task.sleep(for: .milliseconds(50))
            }
        }
    }

    private func popupSmokeDiagnostic(_ button: NSStatusBarButton, anchor: NSRect?) -> String {
        let controller = popover.contentViewController
        let view = controller?.view
        let anchorWindow = button.window
        let popupWindow = view?.window
        let positioningRect = popover.positioningRect.isEmpty ? button.bounds : popover.positioningRect
        let positioningScreenRect = anchorWindow?.convertToScreen(button.convert(positioningRect, to: nil))
        let screens = NSScreen.screens.map {
            "frame=\($0.frame), visibleFrame=\($0.visibleFrame), scale=\($0.backingScaleFactor)"
        }.joined(separator: "; ")
        return """
        positioningRect=\(popover.positioningRect), positioningScreenRect=\(String(describing: positioningScreenRect)), preferredEdge=minY, shown=\(popover.isShown), detached=\(popover.isDetached), animated=\(popover.animates), appActive=\(NSApplication.shared.isActive);
        button frame=\(button.frame), bounds=\(button.bounds), visibleRect=\(button.visibleRect), hidden=\(button.isHiddenOrHasHiddenAncestor), screenRect=\(String(describing: anchor));
        anchorWindow frame=\(String(describing: anchorWindow?.frame)), contentBounds=\(String(describing: anchorWindow?.contentView?.bounds)), visible=\(String(describing: anchorWindow?.isVisible)), screen=\(String(describing: anchorWindow?.screen?.frame));
        popupWindow frame=\(String(describing: popupWindow?.frame)), contentBounds=\(String(describing: popupWindow?.contentView?.bounds)), visible=\(String(describing: popupWindow?.isVisible)), key=\(String(describing: popupWindow?.isKeyWindow)), canBecomeKey=\(String(describing: popupWindow?.canBecomeKey)), screen=\(String(describing: popupWindow?.screen?.frame));
        content frame=\(String(describing: view?.frame)), bounds=\(String(describing: view?.bounds)), hidden=\(String(describing: view?.isHiddenOrHasHiddenAncestor)), contentSize=\(popover.contentSize), preferredContentSize=\(String(describing: controller?.preferredContentSize));
        screens=[\(screens)], macOS=\(ProcessInfo.processInfo.operatingSystemVersionString)
        """
    }

    private func waitUntil(_ condition: () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(20)
        while !condition() {
            if Date() >= deadline { throw AppError.message("Timed out waiting for the application.") }
            try await Task.sleep(for: .milliseconds(100))
        }
    }

}

@main
enum Main {
    @MainActor static func main() {
        let application = NSApplication.shared
        let delegate = AppDelegate()
        application.delegate = delegate
        application.setActivationPolicy(.accessory)
        withExtendedLifetime(delegate) { application.run() }
    }
}
