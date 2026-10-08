import AppKit
import SwiftUI

@MainActor
protocol ApplicationBridge {
    func request(_ fields: [String: Any]) throws -> Receipt
    func poll() throws -> [BridgeEvent]
    func shutdown()
}

@MainActor
final class NativeApplicationBridge: ApplicationBridge {
    func request(_ fields: [String: Any]) throws -> Receipt {
        let data = try JSONSerialization.data(withJSONObject: fields, options: [.sortedKeys])
        guard let text = String(data: data, encoding: .utf8) else { throw AppError.message("Cannot encode the application request.") }
        return try text.withCString { pointer in
            guard let response = ghcp_request(pointer) else { throw AppError.message("The shared engine returned no response.") }
            defer { ghcp_free(response) }
            return try JSONDecoder().decode(Receipt.self, from: Data(String(cString: response).utf8))
        }
    }

    func poll() throws -> [BridgeEvent] {
        guard let response = ghcp_poll() else { throw AppError.message("The shared engine stopped responding.") }
        defer { ghcp_free(response) }
        return try JSONDecoder().decode([BridgeEvent].self, from: Data(String(cString: response).utf8))
    }

    func shutdown() { ghcp_shutdown() }
}

@MainActor
final class AppModel: ObservableObject {
    @Published var dashboard: Dashboard?
    @Published var settings: SettingsData?
    @Published var initialized = false
    @Published var busy = false
    @Published var error: String?
    @Published var notice: String?
    @Published var prompt: DevicePrompt?
    @Published var signingIn = false
    @Published var connectingAccount = false
    @Published var editingHost = false
    @Published var codeCopied = false
    @Published var clipboardError: String?
    @Published var signInHost = "github.com"
    @Published var signInClientId = ""
    @Published var offlineAccess = false
    @Published var hostDescription = ""
    @Published var showingSignIn = false
    @Published var reconnect: AccountData?
    @Published var reconnectClientId: String?
    @Published var page: SettingsPage = .usage
    @Published var selectedAccount: String?
    @Published var notificationPermission: NotificationPermission?
    @Published var notificationBusy = false
    let directory: URL
    let demo: Bool
    var showSettings: (() -> Void)?
    var dashboardChanged: ((Dashboard) -> Void)?
    private var timer: Timer?
    private var callbacks: [String: (BridgeEvent) -> Void] = [:]
    private var signInId: String?
    private var cancellingSignIn = false
    private var closed = false
    private let bridge: any ApplicationBridge
    private let notifications: any NotificationService
    private var permissionRevision = 0
    var copyToClipboard: (String) -> Bool = { value in
        NSPasteboard.general.clearContents()
        return NSPasteboard.general.setString(value, forType: .string)
    }

    init(directory: URL, demo: Bool, bridge: any ApplicationBridge = NativeApplicationBridge(),
         notifications: any NotificationService = NativeNotifications()) {
        self.directory = directory
        self.demo = demo
        self.bridge = bridge
        self.notifications = notifications
    }

    func start(empty: Bool, unlimited: Bool = false) {
        timer = Timer.scheduledTimer(withTimeInterval: 0.2, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.poll() }
        }
        perform("initialize", fields: ["directory": directory.path, "demo": demo, "empty": empty,
                                       "unlimited": unlimited]) { [weak self] event in
            self?.initialized = event.error == nil
            Task { await self?.refreshNotificationPermission() }
        }
    }

    @discardableResult
    func send(_ method: String, fields: [String: Any] = [:],
              completion: ((BridgeEvent) -> Void)? = nil) -> String? {
        guard !closed else { return nil }
        let id = UUID().uuidString
        var request = fields
        request["id"] = id
        request["method"] = method
        do {
            let receipt = try bridge.request(request)
            if let error = receipt.error { throw AppError.message(error) }
            if let completion { callbacks[id] = completion }
            return id
        } catch {
            self.error = (error as? AppError)?.errorDescription ?? "The native application bridge failed. Restart GHCPSpendTray."
            return nil
        }
    }

    func perform(_ method: String, fields: [String: Any] = [:], completion: ((BridgeEvent) -> Void)? = nil) {
        guard !busy else { return }
        busy = true
        error = nil
        notice = nil
        if send(method, fields: fields, completion: { [weak self] event in
            guard let self else { return }
            self.busy = false
            if let error = event.error { self.error = error }
            if event.error == nil && method.hasSuffix(".save") { self.notice = "Settings saved." }
            completion?(event)
        }) == nil { busy = false }
    }

    func poll() {
        do {
            let events = try bridge.poll()
            for event in events {
                if let settings = event.settings { self.settings = settings }
                switch event.kind {
                case "state":
                    if let dashboard = event.dashboard {
                        self.dashboard = dashboard
                        dashboardChanged?(dashboard)
                    }
                case "completed":
                    if let id = event.id { callbacks.removeValue(forKey: id)?(event) }
                case "prompt":
                    if event.id == signInId && !cancellingSignIn && !connectingAccount {
                        prompt = event.prompt
                        copyCode()
                    }
                case "authorized":
                    if event.id == signInId && !cancellingSignIn {
                        connectingAccount = true
                        clearDevicePrompt()
                    }
                case "error":
                    error = event.error
                case "platform":
                    Task { [weak self] in
                        guard let self, let id = event.id else { return }
                        var reply: [String: Any]
                        do {
                            guard !self.demo else { throw AppError.message("Platform side effects are disabled in demonstration mode.") }
                            if event.operation == "notification" {
                                let accepted = try await self.notifications.send(
                                    title: event.title ?? "GHCPSpendTray", message: event.message ?? "", key: event.key)
                                reply = ["accepted": accepted]
                                await self.refreshNotificationPermission()
                            } else {
                                reply = try await Platform.handle(event)
                            }
                        } catch {
                            reply = ["error": (error as? AppError)?.errorDescription ?? "The macOS operation failed. Check system permissions."]
                        }
                        self.send("platform.reply", fields: ["targetId": id, "reply": reply])
                    }
                default: error = "The shared engine returned an unknown event."
                }
            }
        } catch {
            self.error = "The shared engine returned invalid data. Restart GHCPSpendTray."
        }
    }

    func openSettings(_ page: SettingsPage = .usage) {
        self.page = page
        showSettings?()
    }

    func addAccount() {
        guard !busy else { return }
        reconnect = nil
        reconnectClientId = nil
        signInHost = "github.com"
        signInClientId = ""
        editingHost = false
        showingSignIn = true
        openSettings(.accounts)
        startSignIn()
    }

    func addExampleAccount() {
        guard demo else {
            error = "Example accounts are only available in demonstration mode."
            return
        }
        guard initialized else {
            error = "Wait for the app to finish loading before adding an example account."
            return
        }
        perform("demo.account.add")
    }

    func reconnectAccount(_ account: AccountData) {
        send("account.preferences", fields: ["key": account.key]) { [weak self] event in
            guard let self, let preferences = event.preferences else { return }
            self.reconnect = account
            self.reconnectClientId = preferences.clientId
            self.signInHost = account.host
            self.signInClientId = preferences.clientId ?? ""
            self.editingHost = preferences.clientId == nil && account.host != "github.com"
            self.showingSignIn = true
            if self.editingHost { self.describeHost() }
            else { self.startSignIn() }
        }
    }

    func startSignIn() {
        guard !busy else { return }
        error = nil
        notice = nil
        clearDevicePrompt()
        connectingAccount = false
        cancellingSignIn = false
        editingHost = false
        signingIn = true
        busy = true
        var fields: [String: Any] = ["host": signInHost, "offlineAccess": offlineAccess]
        if !signInClientId.isEmpty { fields["clientId"] = signInClientId }
        if let reconnect { fields["key"] = reconnect.key }
        signInId = send("signin", fields: fields) { [weak self] event in
            guard let self else { return }
            self.busy = false
            self.signingIn = false
            self.connectingAccount = false
            self.clearDevicePrompt()
            self.signInId = nil
            let wasCancelled = self.cancellingSignIn || event.cancelled
            self.cancellingSignIn = false
            if !wasCancelled {
                if let error = event.error { self.error = error }
                else {
                    self.notice = self.reconnect == nil ? "Account connected." : "Account reconnected."
                    self.showingSignIn = false
                }
            }
        }
        if signInId == nil { busy = false; signingIn = false }
    }

    func cancelSignIn() {
        if signingIn { cancellingSignIn = true; send("signin.cancel") }
        clearDevicePrompt()
    }

    private func clearDevicePrompt() {
        prompt = nil
        codeCopied = false
        clipboardError = nil
    }

    func changeHost() {
        guard reconnect == nil && !connectingAccount else { return }
        cancelSignIn()
        editingHost = true
        error = nil
        notice = nil
        describeHost()
    }

    func setHost(_ host: String) {
        guard reconnect == nil && signInHost != host else { return }
        signInHost = host
        signInClientId = ""
        describeHost()
    }

    func describeHost() {
        hostDescription = ""
        let host = signInHost
        guard !host.isEmpty else { return }
        send("host.describe", fields: ["host": host]) { [weak self] event in
            if self?.signInHost == host { self?.hostDescription = event.text ?? "" }
        }
    }

    func copyCode() {
        guard let prompt else { return }
        codeCopied = copyToClipboard(prompt.code)
        clipboardError = codeCopied ? nil : "Could not copy the code. Select it and copy manually, or try Copy Code again."
    }

    func openURL(_ value: String) {
        guard let url = URL(string: value), url.scheme == "https", url.user == nil, url.password == nil,
              NSWorkspace.shared.open(url) else {
            error = "Could not open the HTTPS destination in your browser."
            return
        }
    }

    func openDataFolder() {
        if !NSWorkspace.shared.open(directory) { error = "Could not open the local data folder." }
    }

    func refreshNotificationPermission() async {
        guard !demo && !closed && !notificationBusy else { return }
        permissionRevision += 1
        let revision = permissionRevision
        let permission = await notifications.status()
        guard !closed && revision == permissionRevision else { return }
        notificationPermission = permission
    }

    func configureNotifications() async {
        guard !demo && !closed && !notificationBusy else { return }
        notificationBusy = true
        permissionRevision += 1
        error = nil
        notice = nil
        defer { notificationBusy = false }
        do {
            let permission = await notifications.status()
            notificationPermission = permission
            if permission == .notDetermined {
                try await notifications.requestAuthorization()
                notificationPermission = await notifications.status()
            } else {
                try notifications.openSettings()
            }
        } catch {
            self.error = (error as? AppError)?.errorDescription ?? "macOS could not request notification permission. Try again or open Notification Settings."
        }
    }

    func testNotification() async {
        guard !demo && !closed && !notificationBusy else { return }
        notificationBusy = true
        permissionRevision += 1
        error = nil
        notice = nil
        defer { notificationBusy = false }
        do {
            var permission = await notifications.status()
            if permission == .notDetermined {
                try await notifications.requestAuthorization()
                permission = await notifications.status()
            }
            notificationPermission = permission
            guard permission.canSend else { return }
            if try await notifications.send(title: "GHCPSpendTray test", message: "Your test notification arrived.", key: nil) {
                notice = permission == .quiet
                    ? "Sent to Notification Center. Enable banners in Notification Settings to see pop-ups."
                    : "Submitted to macOS. Focus settings may suppress display."
            } else {
                notificationPermission = await notifications.status()
            }
        } catch {
            self.error = "macOS rejected the test notification. Try again or open Notification Settings."
        }
    }

    func shutdown() {
        closed = true
        timer?.invalidate()
        timer = nil
        bridge.shutdown()
    }
}
