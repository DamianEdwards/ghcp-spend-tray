import AppKit
import Darwin
import SwiftUI
import UserNotifications

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, UNUserNotificationCenterDelegate {
    private var model: AppModel?
    private var statusItem: NSStatusItem?
    private let popover = NSPopover()
    private var settingsWindow: NSWindow?
    private var instanceLock: Int32 = -1
    private var wakeObserver: NSObjectProtocol?
    private var activationObserver: NSObjectProtocol?
    private var smoke = false
    private var empty = false
    private let activationName = Notification.Name("com.damianedwards.GHCPSpendTray.activate")

    func applicationDidFinishLaunching(_ notification: Notification) {
        do {
            let arguments = Array(CommandLine.arguments.dropFirst())
            smoke = arguments.contains("--smoke-test")
            empty = arguments.contains("--demo-empty")
            let demo = smoke || empty || arguments.contains("--demo")
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
            let model = AppModel(directory: directory, demo: demo)
            self.model = model
            model.showSettings = { [weak self] in self?.openSettings() }
            model.dashboardChanged = { [weak self] dashboard in self?.statusItem?.button?.toolTip = dashboard.tooltip }
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
            if smoke { Task { await runSmoke(model) } }
        } catch {
            if smoke { fputs("FAIL: macOS startup smoke test.\n", stderr); exit(1) }
            let alert = NSAlert()
            alert.messageText = "GHCPSpendTray could not start"
            alert.informativeText = (error as? AppError)?.errorDescription ?? "Check local data-directory permissions and available disk space."
            alert.alertStyle = .critical
            alert.runModal()
            NSApplication.shared.terminate(nil)
        }
    }

    private func setupMenuBar(_ model: AppModel) {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem = item
        if let button = item.button {
            button.image = NSImage(systemSymbolName: "gauge.with.dots.needle.67percent", accessibilityDescription: "GHCPSpendTray")
            button.image?.isTemplate = true
            button.toolTip = "GHCPSpendTray | Loading"
            button.target = self
            button.action = #selector(togglePopover)
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
        }
        popover.behavior = .transient
        popover.contentViewController = NSHostingController(rootView: FlyoutView(model: model))
        let mainMenu = NSMenu()
        let applicationItem = NSMenuItem()
        mainMenu.addItem(applicationItem)
        let applicationMenu = NSMenu()
        applicationMenu.addItem(withTitle: "Settings...", action: #selector(settingsAction), keyEquivalent: ",").target = self
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

    @objc private func togglePopover() {
        guard let button = statusItem?.button else { return }
        if NSApplication.shared.currentEvent?.type == .rightMouseUp {
            popover.performClose(nil)
            let menu = NSMenu()
            menu.addItem(withTitle: "Open", action: #selector(showUsage), keyEquivalent: "").target = self
            let refresh = menu.addItem(withTitle: "Refresh Now", action: #selector(refreshAction), keyEquivalent: "")
            refresh.target = self
            refresh.isEnabled = model?.initialized == true && model?.busy == false
            menu.addItem(withTitle: "Settings...", action: #selector(settingsAction), keyEquivalent: ",").target = self
            menu.addItem(.separator())
            menu.addItem(withTitle: "Quit GHCPSpendTray", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
            menu.autoenablesItems = false
            statusItem?.menu = menu
            button.performClick(nil)
            statusItem?.menu = nil
        } else if popover.isShown {
            popover.performClose(nil)
        } else {
            NSApplication.shared.activate()
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        }
    }

    @objc private func showUsage() { model?.openSettings(.usage) }
    @objc private func settingsAction() { openSettings() }
    @objc private func refreshAction() { model?.perform("refresh") }

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
        if model.initialized && !model.busy { model.perform("resume") }
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
        do {
            try await waitUntil { model.initialized && !model.busy }
            guard model.error == nil, let dashboard = model.dashboard,
                  dashboard.accounts.count == (empty ? 0 : 2),
                  dashboard.consumptionUsd == (empty ? nil : Decimal(string: "42.75")),
                  money(dashboard.consumptionUsd) == (empty ? "Unavailable" : "$42.75"),
                  model.settings?.startup == false else { throw AppError.message("Synthetic dashboard contract failed.") }
            guard statusItem?.button != nil else { throw AppError.message("Menu bar item was not created.") }
            openSettings()
            try await waitUntil { !model.busy }
            for page in SettingsPage.allCases {
                model.page = page
                try await Task.sleep(for: .milliseconds(250))
                guard let view = settingsWindow?.contentView, let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else {
                    throw AppError.message("Settings view did not render.")
                }
                view.cacheDisplay(in: view.bounds, to: bitmap)
                guard bitmap.pixelsWide >= 730, let png = bitmap.representation(using: .png, properties: [:]) else {
                    throw AppError.message("Settings render was empty.")
                }
                try png.write(to: model.directory.appendingPathComponent("\(page.rawValue).png"))
            }
            if var settings = model.settings {
                settings.pollMinutes = 15
                settings.spendIncrementUsd = 50
                model.perform("settings.save", fields: ["settings": try jsonObject(settings)])
                try await waitUntil { !model.busy }
                guard model.error == nil, model.settings?.pollMinutes == 15, model.settings?.spendIncrementUsd == 50 else {
                    throw AppError.message("Settings did not round-trip across the native bridge.")
                }
            }
            model.addAccount()
            try await Task.sleep(for: .milliseconds(250))
            guard model.showingSignIn else { throw AppError.message("Account onboarding did not open.") }
            model.showingSignIn = false
            try await waitUntil { self.settingsWindow?.attachedSheet == nil }
            try "PASS: native menu bar, five settings pages, synthetic dashboard and settings bridge.\n"
                .write(to: model.directory.appendingPathComponent("smoke-result.txt"), atomically: true, encoding: .utf8)
            print("PASS: macOS \(empty ? "empty" : "populated") Native AOT / SwiftUI smoke test.")
            NSApplication.shared.terminate(nil)
        } catch {
            fputs("FAIL: macOS synthetic smoke test: \(error.localizedDescription)\n", stderr)
            model.shutdown()
            exit(1)
        }
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
