import Foundation

@MainActor
enum UpdateTests {
    static func run() throws {
        func check(_ condition: Bool, _ message: String) throws {
            if !condition { throw AppError.message(message) }
        }
        var created = 0
        let fixture = FixtureUpdater()
        func makeUpdates() -> AppUpdates {
            AppUpdates { created += 1; return fixture }
        }
        for channel in ["Development", "Stable", "Preview"] {
            let updates = makeUpdates()
            updates.start(channel: channel, isolated: true)
            updates.checkForUpdates()
            updates.setAutomaticallyChecks(true)
            updates.setAutomaticallyDownloads(true)
            try check(!updates.enabled && created == 0 && fixture.starts == 0,
                      "Isolated modes must never create an updater, touch preferences or access the network.")
        }
        let development = makeUpdates()
        development.start(channel: "Development", isolated: false)
        try check(created == 0 && !development.enabled, "Development builds have no update side effects.")
        let invalid = makeUpdates()
        invalid.start(channel: "Unknown", isolated: false)
        try check(invalid.state.error != nil && created == 0, "Unknown channels fail explicitly.")

        let updates = makeUpdates()
        updates.start(channel: "Stable", isolated: false)
        try check(updates.enabled && updates.state.canCheck && fixture.starts == 1,
                  "Production startup enables the service once.")
        updates.start(channel: "Stable", isolated: false)
        try check(created == 1 && fixture.starts == 1, "Repeated startup cannot create duplicate schedulers.")
        updates.checkForUpdates()
        try check(fixture.checks == 1, "The manual action reaches the updater.")
        updates.setAutomaticallyChecks(true)
        updates.setAutomaticallyDownloads(true)
        try check(updates.state.automaticallyChecks && updates.state.automaticallyDownloads,
                  "Preferences come from the service, not a second defaults store.")
        fixture.state.availableVersion = "0.3.0"
        fixture.publish()
        try check(updates.actionTitle == "Update Available: 0.3.0...", "Scheduled updates have an in-app reminder.")
        fixture.state.readyToInstall = true
        fixture.publish()
        try check(updates.actionTitle == "Install and Relaunch...", "Downloaded updates have a relaunch action.")
        fixture.state.canCheck = false
        fixture.publish()
        updates.checkForUpdates()
        try check(fixture.checks == 1, "An in-flight update disables repeated checks.")
        fixture.state = UpdateState(canCheck: true, error: "Synthetic download failure")
        fixture.publish()
        try check(updates.state.availableVersion == nil && updates.state.error != nil,
                  "Session completion clears reminders while keeping real errors visible.")
        try check(updates.actionTitle == "Check for Updates...", "Completed sessions restore the manual action.")

        let failing = FixtureUpdater()
        failing.failStart = true
        let failed = AppUpdates { failing }
        failed.start(channel: "Stable", isolated: false)
        try check(!failed.enabled && failed.state.error?.contains("Synthetic startup failure") == true,
                  "Startup failures remain explicit without preventing the rest of the app from running.")
        failed.checkForUpdates()
        try check(failing.checks == 0 && failing.stateChanged == nil, "Failed startup cannot leave active controls or callbacks.")
        var availability = UpdateAvailability()
        availability.offered(version: "0.3.0", downloaded: false)
        availability.finished()
        try check(availability.version == nil, "Dismissed scheduled reminders do not linger.")
        availability.scheduledOnQuit(version: "0.3.0")
        availability.offered(version: "0.4.0", downloaded: false)
        availability.finished()
        try check(availability.version == "0.3.0" && availability.readyToInstall,
                  "Finishing the alert must not hide an update that is still waiting for this long-running app to quit.")
        availability.cancelled()
        try check(availability.version == nil && !availability.readyToInstall,
                  "Skipped updates and real failures clear pending relaunch reminders.")
        print("PASS: updater isolation, consent preferences, reminders, relaunch action and error recovery.")
    }
}

@MainActor
private final class FixtureUpdater: UpdateService {
    var stateChanged: ((UpdateState) -> Void)?
    var state = UpdateState(canCheck: true)
    var starts = 0, checks = 0
    var failStart = false
    func start() throws {
        starts += 1
        if failStart { throw AppError.message("Synthetic startup failure.") }
        publish()
    }
    func publish() { stateChanged?(state) }
    func checkForUpdates() { checks += 1 }
    func setAutomaticallyChecks(_ enabled: Bool) { state.automaticallyChecks = enabled; publish() }
    func setAutomaticallyDownloads(_ enabled: Bool) { state.automaticallyDownloads = enabled; publish() }
}
