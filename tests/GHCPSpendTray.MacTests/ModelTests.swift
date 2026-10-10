import AppKit
import Foundation

@MainActor
enum ModelTests {
    static func run() throws {
        func check(_ condition: Bool, _ message: String) throws {
            if !condition { throw AppError.message(message) }
        }
        let unlimitedBridge = FixtureBridge()
        let unlimitedModel = AppModel(directory: URL(fileURLWithPath: "/synthetic-unlimited-unused"),
                                      demo: true, bridge: unlimitedBridge)
        defer { unlimitedModel.shutdown() }
        unlimitedModel.start(empty: false, unlimited: true)
        try check(unlimitedBridge.lastRequest["method"] as? String == "initialize" &&
                  unlimitedBridge.lastRequest["demo"] as? Bool == true &&
                  unlimitedBridge.lastRequest["empty"] as? Bool == false &&
                  unlimitedBridge.lastRequest["unlimited"] as? Bool == true,
                  "Unlimited sample mode reaches the shared initializer without real account access.")
        let bridge = FixtureBridge()
        let model = AppModel(directory: URL(fileURLWithPath: "/synthetic-unused"), demo: true, bridge: bridge)
        var copies: [String] = []
        model.copyToClipboard = { copies.append($0); return true }
        defer { model.shutdown() }
        model.addExampleAccount()
        try check(bridge.requests.isEmpty && model.error != nil, "Examples wait for initialization.")
        model.initialized = true
        model.addExampleAccount()
        let example = bridge.lastRequest
        try check(example["method"] as? String == "demo.account.add" && model.busy &&
                  !model.showingSignIn && !model.signingIn, "Examples use the shared synthetic command without opening sign-in.")
        model.addExampleAccount()
        try check(bridge.requests.count == 1, "Example additions serialize while the bridge is busy.")
        try bridge.enqueue("completed", id: example["id"])
        model.poll()
        try check(!model.busy && model.error == nil, "Example completion clears busy state.")
        model.addExampleAccount()
        try bridge.enqueue("completed", id: bridge.lastRequest["id"], values: ["error": "Synthetic example failure."])
        model.poll()
        try check(model.error == "Synthetic example failure." && !model.busy, "Example errors remain visible and retryable.")
        let normalBridge = FixtureBridge()
        let normal = AppModel(directory: URL(fileURLWithPath: "/synthetic-normal-unused"), demo: false, bridge: normalBridge)
        defer { normal.shutdown() }
        normal.initialized = true
        normal.addExampleAccount()
        try check(normalBridge.requests.isEmpty && normal.error != nil, "Normal mode cannot request synthetic accounts.")
        model.addAccount()
        let initial = bridge.lastRequest
        try check(initial["method"] as? String == "signin" && initial["host"] as? String == "github.com",
                  "Add Account starts github.com sign-in immediately.")
        try check(model.signingIn && model.showingSignIn && !model.editingHost, "Immediate sign-in sheet.")
        try bridge.enqueue("prompt", id: initial["id"], values: ["prompt": [
            "code": "SYNTHETIC", "verificationUri": "https://github.com/login/device",
            "expires": "2099-01-01T00:00:00Z"
        ]])
        model.poll()
        try check(model.codeCopied && copies == ["SYNTHETIC"], "Device code is copied automatically.")
        model.copyToClipboard = { _ in false }
        model.copyCode()
        try check(!model.codeCopied && model.clipboardError != nil && model.prompt != nil, "Clipboard failure remains recoverable.")
        try bridge.enqueue("authorized", id: initial["id"])
        model.poll()
        try check(model.connectingAccount && model.prompt == nil && model.clipboardError == nil,
                  "Authorization clears device code and shows connection progress.")
        try bridge.enqueue("prompt", id: initial["id"], values: ["prompt": [
            "code": "OBSOLETE", "verificationUri": "https://github.com/login/device", "expires": "2099-01-01T00:00:00Z"
        ]])
        model.poll()
        try check(model.prompt == nil && copies == ["SYNTHETIC"], "Late prompt after authorization cannot restore secrets.")
        try bridge.enqueue("completed", id: initial["id"])
        model.poll()
        try check(!model.signingIn && !model.busy && !model.showingSignIn && model.notice == "Account connected.",
                  "Successful sign-in completes without another confirmation.")
        try check(!bridge.requests.contains { $0["method"] as? String == "signin.confirm" }, "No obsolete identity confirmation command.")

        model.addAccount()
        let cancelled = bridge.lastRequest
        model.changeHost()
        try check(model.editingHost && model.busy && model.prompt == nil, "Changing host cancels and drains the active sign-in.")
        try bridge.enqueue("prompt", id: cancelled["id"], values: ["prompt": [
            "code": "CANCELLED", "verificationUri": "https://github.com/login/device", "expires": "2099-01-01T00:00:00Z"
        ]])
        try bridge.enqueue("authorized", id: cancelled["id"])
        model.poll()
        try check(model.prompt == nil && !model.connectingAccount && copies.count == 1, "Cancelled callbacks do not replace the host editor.")
        let beforeRestart = bridge.requests.count
        model.startSignIn()
        try check(bridge.requests.count == beforeRestart, "A new operation waits for cancellation completion.")
        try bridge.enqueue("completed", id: cancelled["id"], values: ["cancelled": true])
        model.poll()
        try check(!model.busy && model.error == nil && model.editingHost, "Cancellation returns quietly to host selection.")

        model.setHost("example.ghe.com")
        model.signInClientId = "example-registration"
        model.offlineAccess = true
        model.startSignIn()
        let enterprise = bridge.lastRequest
        try check(enterprise["host"] as? String == "example.ghe.com" &&
                  enterprise["clientId"] as? String == "example-registration" &&
                  enterprise["offlineAccess"] as? Bool == true, "Host-scoped enterprise registration and offline access.")
        try bridge.enqueue("completed", id: enterprise["id"], values: ["error": "Synthetic connection failure."])
        model.poll()
        try check(model.error == "Synthetic connection failure." && model.showingSignIn && !model.busy,
                  "Connection failure remains visible and retryable.")
        model.changeHost()
        model.setHost("another.ghe.com")
        try check(model.signInClientId.isEmpty, "Changing host clears the previous registration.")
        model.startSignIn()
        let retry = bridge.lastRequest
        try bridge.enqueue("completed", id: retry["id"])
        model.poll()

        let account = try JSONDecoder().decode(AccountData.self, from: Data("""
        {"key":"example.ghe.com:1","name":"Synthetic","login":"fixture","host":"example.ghe.com",
         "details":{"unlimited":false,"isCurrentPeriod":true},"freshness":"Fresh"}
        """.utf8))
        model.reconnectAccount(account)
        let preferences = bridge.lastRequest
        try bridge.enqueue("completed", id: preferences["id"], values: ["preferences": [
            "displayName": "Synthetic", "thresholds": "", "clientId": "original-registration", "showPeriodEstimate": true
        ]])
        model.poll()
        let reconnect = bridge.lastRequest
        try check(reconnect["key"] as? String == account.key && reconnect["clientId"] as? String == "original-registration",
                  "Reconnect retains immutable identity and original registration.")
        model.changeHost()
        try check(!model.editingHost, "Reconnect cannot change host.")
        try bridge.enqueue("completed", id: reconnect["id"])
        model.poll()
        try check(model.notice == "Account reconnected.", "Reconnect success feedback.")
        model.reconnectAccount(account)
        let legacy = bridge.lastRequest
        try bridge.enqueue("completed", id: legacy["id"], values: ["preferences": [
            "displayName": "Synthetic", "thresholds": "", "showPeriodEstimate": false
        ]])
        model.poll()
        try check(model.editingHost && !model.signingIn, "Legacy custom-host reconnect requires its missing client ID.")

        let settings = try JSONDecoder().decode(SettingsData.self, from: Data("""
        {"pollMinutes":60,"thresholds":"50, 80, 100","notifications":true,"startup":false,"canChangeStartup":false,
         "startupDescription":"Synthetic","trayStyle":1,"trayMode":1,"excludedTrayAccounts":["github.com:1"]}
        """.utf8))
        let roundTrip = try JSONDecoder().decode(SettingsData.self, from: JSONEncoder().encode(settings))
        try check(roundTrip.trayStyle == .percentage && roundTrip.trayMode == .perAccount &&
                  roundTrip.excludedTrayAccounts == ["github.com:1"], "Tray options retain the generated C# enum contract.")
        let unlimited = try JSONDecoder().decode(TrayIndicator.self, from: Data("""
        {"name":"Synthetic","includedAccounts":1,"selectedAccounts":1,"details":"Unlimited allocation",
         "tooltip":"Unlimited allocation | 1/1 included","isPartial":false,"isOverAllocation":false,
         "valueText":"Unlimited allocation","numericText":"\\u221e","isUnlimited":true}
        """.utf8))
        try check(unlimited.isUnlimited && unlimited.percent == nil && unlimited.numericText == "\u{221e}" &&
                  unlimited.valueText == "Unlimited allocation" && !unlimited.isPartial,
                  "Generated bridge unlimited state decodes without a percentage or warning.")

        print("PASS: Mac automatic sign-in, clipboard recovery, cancellation, reconnect and menu-bar models.")
    }
}

@MainActor
final class FixtureBridge: ApplicationBridge {
    var requests: [[String: Any]] = []
    var events: [BridgeEvent] = []
    var lastRequest: [String: Any] { requests.last! }

    func request(_ fields: [String: Any]) throws -> Receipt {
        requests.append(fields)
        if fields["method"] as? String == "form.validate" {
            return try NativeApplicationBridge().request(fields)
        }
        return Receipt(error: nil)
    }

    func poll() throws -> [BridgeEvent] {
        let result = events
        events.removeAll()
        return result
    }

    func enqueue(_ kind: String, id: Any?, values: [String: Any] = [:]) throws {
        var fields = values
        fields["kind"] = kind
        fields["id"] = id
        fields["enabled"] = false
        if fields["cancelled"] == nil { fields["cancelled"] = false }
        events.append(try JSONDecoder().decode(BridgeEvent.self, from: JSONSerialization.data(withJSONObject: fields)))
    }

    func shutdown() {}
}
