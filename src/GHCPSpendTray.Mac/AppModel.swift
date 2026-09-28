import AppKit
import SwiftUI

@MainActor
final class AppModel: ObservableObject {
    @Published var dashboard: Dashboard?
    @Published var settings: SettingsData?
    @Published var initialized = false
    @Published var busy = false
    @Published var error: String?
    @Published var notice: String?
    @Published var prompt: DevicePrompt?
    @Published var identity: PendingIdentity?
    @Published var signingIn = false
    @Published var showingSignIn = false
    @Published var reconnect: AccountData?
    @Published var reconnectClientId: String?
    @Published var page: SettingsPage = .usage
    @Published var selectedAccount: String?
    let directory: URL
    let demo: Bool
    var showSettings: (() -> Void)?
    var dashboardChanged: ((Dashboard) -> Void)?
    private var timer: Timer?
    private var callbacks: [String: (BridgeEvent) -> Void] = [:]
    private var signInId: String?
    private var closed = false

    init(directory: URL, demo: Bool) {
        self.directory = directory
        self.demo = demo
    }

    func start(empty: Bool) {
        timer = Timer.scheduledTimer(withTimeInterval: 0.2, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.poll() }
        }
        perform("initialize", fields: ["directory": directory.path, "demo": demo, "empty": empty]) { [weak self] event in
            self?.initialized = event.error == nil
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
            let data = try JSONSerialization.data(withJSONObject: request, options: [.sortedKeys])
            guard let text = String(data: data, encoding: .utf8) else { throw AppError.message("Cannot encode the application request.") }
            let receipt: Receipt = try text.withCString { pointer in
                guard let response = ghcp_request(pointer) else { throw AppError.message("The shared engine returned no response.") }
                defer { ghcp_free(response) }
                return try JSONDecoder().decode(Receipt.self, from: Data(String(cString: response).utf8))
            }
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

    private func poll() {
        guard let response = ghcp_poll() else { error = "The shared engine stopped responding."; return }
        defer { ghcp_free(response) }
        do {
            let events = try JSONDecoder().decode([BridgeEvent].self, from: Data(String(cString: response).utf8))
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
                    if event.id == signInId { prompt = event.prompt; identity = nil }
                case "identity":
                    if event.id == signInId { identity = event.identity; prompt = nil }
                case "error":
                    error = event.error
                case "platform":
                    Task { [weak self] in
                        guard let self, let id = event.id else { return }
                        var reply: [String: Any]
                        do {
                            guard !self.demo else { throw AppError.message("Platform side effects are disabled in demonstration mode.") }
                            reply = try await Platform.handle(event)
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
        reconnect = nil
        reconnectClientId = nil
        openSettings(.accounts)
        showingSignIn = true
    }

    func reconnectAccount(_ account: AccountData) {
        send("account.preferences", fields: ["key": account.key]) { [weak self] event in
            guard let self, let preferences = event.preferences else { return }
            self.reconnect = account
            self.reconnectClientId = preferences.clientId
            self.showingSignIn = true
        }
    }

    func startSignIn(host: String, clientId: String, offline: Bool) {
        guard !busy else { return }
        error = nil
        prompt = nil
        identity = nil
        signingIn = true
        busy = true
        var fields: [String: Any] = ["host": host, "offlineAccess": offline]
        if !clientId.isEmpty { fields["clientId"] = clientId }
        if let reconnect { fields["key"] = reconnect.key }
        signInId = send("signin", fields: fields) { [weak self] event in
            guard let self else { return }
            self.busy = false
            self.signingIn = false
            self.prompt = nil
            self.identity = nil
            self.signInId = nil
            if let error = event.error { self.error = error }
            else { self.notice = "Account connected."; self.showingSignIn = false }
        }
        if signInId == nil { busy = false; signingIn = false }
    }

    func cancelSignIn() {
        if signingIn { send("signin.cancel") }
        prompt = nil
        identity = nil
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

    func testNotification() {
        guard !demo else { error = "Notifications are disabled in demonstration mode."; return }
        Task {
            do {
                if try await Platform.notify(title: "GHCPSpendTray test", message: "Consumption alerts are enabled.", key: nil) {
                    notice = "Submitted to macOS. Focus settings may suppress display."
                } else { error = "Notifications are not allowed. Enable them in System Settings > Notifications > GHCPSpendTray." }
            } catch { self.error = "macOS rejected the test notification. Check notification permissions." }
        }
    }

    func shutdown() {
        closed = true
        timer?.invalidate()
        timer = nil
        ghcp_shutdown()
    }
}
