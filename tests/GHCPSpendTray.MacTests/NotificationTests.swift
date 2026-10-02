import Foundation

@MainActor
enum NotificationTests {
    static func run() async throws {
        func check(_ condition: Bool, _ message: String) throws {
            if !condition { throw AppError.message(message) }
        }
        let service = FixtureNotifications()
        let bridge = FixtureBridge()
        let model = AppModel(directory: URL(fileURLWithPath: "/synthetic-unused"), demo: false,
                             bridge: bridge, notifications: service)
        defer { model.shutdown() }
        await model.refreshNotificationPermission()
        try check(model.notificationPermission == .notDetermined && service.requests == 0,
                  "Reading status never prompts for permission.")
        try check(model.notificationPermission?.actionTitle == "Enable Notifications",
                  "First-use UI offers a permission request.")

        await model.configureNotifications()
        try check(service.requests == 1 && model.notificationPermission == .authorized &&
                  service.sends == 0 && service.settingsOpened == 0,
                  "Explicit setup requests permission without sending or opening settings.")
        await model.testNotification()
        try check(service.sends == 1 && service.requests == 1 && model.notice?.contains("Submitted") == true,
                  "Authorized test submits without requesting again.")

        service.permission = .denied
        model.error = "Previous notification error"
        await model.refreshNotificationPermission()
        try check(model.notificationPermission == .denied &&
                  model.notificationPermission?.actionTitle == "Open Notification Settings",
                  "External permission revocation changes the available action.")
        await model.testNotification()
        try check(service.sends == 1 && service.requests == 1 && service.settingsOpened == 0 && model.error == nil,
                  "Denied test shows permission guidance, not another error or permission prompt.")
        await model.configureNotifications()
        try check(service.settingsOpened == 1 && service.requests == 1,
                  "Denied setup opens settings without trying to bypass consent.")

        service.permission = .authorized
        await model.refreshNotificationPermission()
        try check(model.notificationPermission == .authorized, "Returning from Settings refreshes permission.")
        service.permission = .quiet
        await model.testNotification()
        try check(service.sends == 2 && model.notice?.contains("Notification Center") == true,
                  "Quiet permissions explain Notification Center rather than promising a banner.")

        service.permission = .notDetermined
        service.requestResult = .denied
        await model.testNotification()
        try check(service.requests == 2 && service.sends == 2 && model.error == nil &&
                  model.notificationPermission == .denied, "Declining the initial test prompt is not an error.")
        service.permission = .notDetermined
        service.requestResult = .authorized
        await model.testNotification()
        try check(service.requests == 3 && service.sends == 3, "Allowing initial test permission sends the test.")

        service.failSend = true
        await model.testNotification()
        try check(model.error?.contains("rejected") == true, "Actual submission failures remain explicit.")
        service.failSend = false
        service.failOpen = true
        await model.configureNotifications()
        try check(model.error?.contains("Synthetic settings failure") == true, "Settings launch errors remain actionable.")
        service.failOpen = false
        service.permission = .notDetermined
        service.failRequest = true
        await model.configureNotifications()
        try check(model.error != nil && !model.notificationBusy, "Permission API failure restores retry controls.")
        service.failRequest = false

        service.permission = .denied
        let requestsBeforeBackground = service.requests
        let opensBeforeBackground = service.settingsOpened
        try bridge.enqueue("platform", id: "background-alert", values: [
            "operation": "notification", "title": "Synthetic alert", "message": "Synthetic message"
        ])
        model.poll()
        for _ in 0..<100 where bridge.requests.last?["method"] as? String != "platform.reply" {
            try await Task.sleep(for: .milliseconds(10))
        }
        let reply = bridge.requests.last?["reply"] as? [String: Any]
        try check(reply?["accepted"] as? Bool == false && service.requests == requestsBeforeBackground &&
                  service.settingsOpened == opensBeforeBackground, "Background alerts never prompt or open settings.")

        service.permission = .notDetermined
        service.pauseRequest = true
        let configure = Task { await model.configureNotifications() }
        for _ in 0..<100 where service.pendingRequest == nil {
            try await Task.sleep(for: .milliseconds(10))
        }
        try check(model.notificationBusy && service.pendingRequest != nil, "Permission prompt is tracked as an in-flight operation.")
        let requestsInFlight = service.requests
        await model.configureNotifications()
        await model.testNotification()
        await model.refreshNotificationPermission()
        try check(service.requests == requestsInFlight, "Repeated clicks and activation do not duplicate the permission request.")
        service.permission = .authorized
        service.pendingRequest?.resume()
        service.pendingRequest = nil
        await configure.value
        try check(!model.notificationBusy && model.notificationPermission == .authorized, "Permission completion refreshes UI.")

        let demoService = FixtureNotifications()
        let demo = AppModel(directory: URL(fileURLWithPath: "/synthetic-unused"), demo: true,
                            bridge: FixtureBridge(), notifications: demoService)
        defer { demo.shutdown() }
        await demo.refreshNotificationPermission()
        await demo.configureNotifications()
        await demo.testNotification()
        try check(demoService.reads == 0 && demoService.requests == 0 && demoService.sends == 0 &&
                  demoService.settingsOpened == 0, "Demo mode never touches native permission or delivery.")
        print("PASS: notification permission setup, denial/settings recovery, quiet delivery, failures and prompt deduplication.")
    }
}

@MainActor
private final class FixtureNotifications: NotificationService {
    var permission: NotificationPermission = .notDetermined
    var requestResult: NotificationPermission = .authorized
    var reads = 0, requests = 0, sends = 0, settingsOpened = 0
    var failRequest = false, failOpen = false, failSend = false, pauseRequest = false
    var pendingRequest: CheckedContinuation<Void, Never>?

    func status() async -> NotificationPermission {
        reads += 1
        return permission
    }

    func requestAuthorization() async throws {
        requests += 1
        if failRequest { throw AppError.message("Synthetic permission failure.") }
        if pauseRequest { await withCheckedContinuation { pendingRequest = $0 } }
        else { permission = requestResult }
    }

    func send(title: String, message: String, key: String?) async throws -> Bool {
        guard permission.canSend else { return false }
        if failSend { throw AppError.message("Synthetic delivery failure.") }
        sends += 1
        return true
    }

    func openSettings() throws {
        if failOpen { throw AppError.message("Synthetic settings failure.") }
        settingsOpened += 1
    }
}
