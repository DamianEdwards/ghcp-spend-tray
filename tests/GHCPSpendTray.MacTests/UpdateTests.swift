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
        fixture.state.phase = .available
        fixture.publish()
        try check(updates.actionTitle == "Update Available: 0.3.0...", "Scheduled updates have an in-app reminder.")
        try check(updates.statusTitle == "Update 0.3.0 available to download" &&
                  updates.statusDetail.contains("not been downloaded"),
                  "An available update explicitly distinguishes discovery from a completed download.")
        fixture.state.phase = .downloading
        fixture.publish()
        try check(updates.statusTitle == "Downloading update 0.3.0" &&
                  updates.actionTitle == "Downloading Update..." && !updates.state.readyToInstall,
                  "An active download must not offer installation.")
        fixture.state.phase = .preparing
        fixture.publish()
        try check(updates.statusTitle == "Update 0.3.0 downloaded; preparing to install" &&
                  updates.actionTitle == "Preparing Update..." && !updates.state.readyToInstall,
                  "A completed download is not ready while it is still being prepared.")
        fixture.state.phase = .ready
        fixture.publish()
        try check(updates.actionTitle == "Install and Relaunch...", "Downloaded updates have a relaunch action.")
        try check(updates.statusTitle == "Update 0.3.0 downloaded and ready to install" &&
                  updates.statusDetail.contains("Installation on quit has not been scheduled"),
                  "An enabled automatic-download preference alone cannot promise installation on quit.")
        fixture.state.installsOnQuit = true
        fixture.publish()
        try check(updates.statusDetail.contains("will install when you quit GHCPSpendTray") &&
                  updates.statusDetail.contains("not when you close this window"),
                  "A scheduled update explains both immediate installation and what quitting means.")
        updates.setAutomaticallyDownloads(false)
        try check(updates.statusDetail.contains("will install when you quit GHCPSpendTray"),
                  "Changing the download preference cannot hide an already scheduled installation.")
        fixture.state.phase = .installing
        fixture.publish()
        try check(updates.statusTitle == "Installing update 0.3.0" &&
                  updates.actionTitle == "Installing Update...", "Active installation is distinct from readiness.")
        fixture.state.canCheck = false
        fixture.publish()
        updates.checkForUpdates()
        try check(fixture.checks == 1, "An in-flight update disables repeated checks.")
        fixture.state = UpdateState(canCheck: true, error: "Synthetic download failure")
        fixture.publish()
        try check(updates.state.availableVersion == nil && updates.state.error != nil,
                  "Session completion clears reminders while keeping real errors visible.")
        try check(updates.actionTitle == "Check for Updates...", "Completed sessions restore the manual action.")
        try check(updates.statusTitle == "Update could not complete", "Errors must not look like successful checks.")
        fixture.state = UpdateState(canCheck: false, phase: .checking)
        fixture.publish()
        try check(updates.statusTitle == "Checking for updates" && updates.actionTitle == "Checking for Updates...",
                  "Checking is distinct from download or installation.")
        fixture.state = UpdateState(canCheck: true, lastChecked: Date())
        fixture.publish()
        try check(updates.statusTitle == "No update pending" && updates.statusDetail.contains("Check for updates"),
                  "A previous check alone cannot assert that the installed version is the latest.")

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
        try check(availability.phase == .available && !availability.installsOnQuit, "Discovery does not imply a download.")
        availability.progress(version: "0.3.0", phase: .downloading)
        try check(!availability.readyToInstall, "Downloads in progress are not ready.")
        availability.progress(version: "0.3.0", phase: .preparing)
        try check(!availability.readyToInstall, "Preparation is not installation readiness.")
        availability.progress(version: "0.3.0", phase: .ready)
        try check(availability.readyToInstall && !availability.installsOnQuit,
                  "Readiness alone does not imply automatic installation on quit.")
        availability.finished()
        try check(availability.version == nil, "Dismissed scheduled reminders do not linger.")
        availability.scheduledOnQuit(version: "0.3.0")
        try check(availability.installsOnQuit, "Sparkle scheduling confirms installation on quit.")
        availability.offered(version: "0.4.0", downloaded: false)
        try check(!availability.installsOnQuit, "A different offered version must not inherit an older update's schedule.")
        availability.finished()
        try check(availability.version == "0.3.0" && availability.readyToInstall && availability.installsOnQuit,
                  "Finishing the alert must not hide an update that is still waiting for this long-running app to quit.")
        availability.cancelled()
        try check(availability.version == nil && !availability.readyToInstall && !availability.installsOnQuit,
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
