import AppKit
import Combine
import Sparkle

struct UpdateState: Equatable {
    var canCheck = false
    var automaticallyChecks = false
    var automaticallyDownloads = false
    var lastChecked: Date?
    var availableVersion: String?
    var readyToInstall = false
    var error: String?
}

struct UpdateAvailability {
    private(set) var version: String?
    private(set) var readyToInstall = false
    private var onQuitVersion: String?

    mutating func offered(version: String, downloaded: Bool) {
        self.version = version
        readyToInstall = downloaded
    }

    mutating func scheduledOnQuit(version: String) {
        self.version = version
        readyToInstall = true
        onQuitVersion = version
    }

    mutating func finished() {
        if let onQuitVersion {
            version = onQuitVersion
            readyToInstall = true
        } else {
            self = UpdateAvailability()
        }
    }

    mutating func cancelled() { self = UpdateAvailability() }
}

@MainActor
protocol UpdateService: AnyObject {
    var stateChanged: ((UpdateState) -> Void)? { get set }
    func start() throws
    func checkForUpdates()
    func setAutomaticallyChecks(_ enabled: Bool)
    func setAutomaticallyDownloads(_ enabled: Bool)
}

@MainActor
final class AppUpdates: ObservableObject {
    @Published private(set) var state = UpdateState()
    @Published private(set) var enabled = false
    @Published private(set) var explanation = "Updates are disabled in development and demonstration builds."
    private var service: (any UpdateService)?
    private let makeService: () -> any UpdateService

    var actionTitle: String {
        if state.readyToInstall { return "Install and Relaunch..." }
        if let version = state.availableVersion { return "Update Available: \(version)..." }
        return "Check for Updates..."
    }

    init(makeService: @escaping () -> any UpdateService = { SparkleUpdateService() }) {
        self.makeService = makeService
    }

    #if UPDATE_REHEARSAL
    static func rehearsal(_ rehearsal: UpdateRehearsal) -> AppUpdates {
        AppUpdates { SparkleUpdateService(rehearsal: rehearsal) }
    }
    #endif

    func start(channel: String, isolated: Bool) {
        guard !enabled, service == nil else { return }
        guard !isolated && channel != "Development" else { return }
        guard channel == "Stable" || channel == "Preview" else {
            state.error = "The application has an invalid update channel. Download a new macOS release."
            explanation = "Automatic updates could not start."
            return
        }
        let service = makeService()
        service.stateChanged = { [weak self] in self?.state = $0 }
        do {
            try service.start()
            self.service = service
            enabled = true
            explanation = "Updates come from stable macOS releases. Settings, history and sign-ins stay on this Mac."
        } catch {
            service.stateChanged = nil
            state.error = "Automatic updates could not start: \(error.localizedDescription)"
            explanation = "Download a new macOS release or try again after restarting the app."
        }
    }

    func checkForUpdates() {
        guard enabled, state.canCheck else { return }
        service?.checkForUpdates()
    }

    func setAutomaticallyChecks(_ enabled: Bool) {
        guard self.enabled else { return }
        service?.setAutomaticallyChecks(enabled)
    }

    func setAutomaticallyDownloads(_ enabled: Bool) {
        guard self.enabled else { return }
        service?.setAutomaticallyDownloads(enabled)
    }
}

@MainActor
private final class SparkleUpdateService: NSObject, UpdateService, SPUUpdaterDelegate,
    @preconcurrency SPUStandardUserDriverDelegate {
    var stateChanged: ((UpdateState) -> Void)?
    private var controller: SPUStandardUpdaterController?
    private var activeUpdater: SPUUpdater?
    #if UPDATE_REHEARSAL
    private var rehearsal: UpdateRehearsal?
    convenience init(rehearsal: UpdateRehearsal) {
        self.init()
        self.rehearsal = rehearsal
    }
    #endif
    private var observations = Set<AnyCancellable>()
    private var availability = UpdateAvailability()
    private var updateError: String?

    func start() throws {
        guard let feed = Bundle.main.object(forInfoDictionaryKey: "SUFeedURL") as? String,
              let url = URL(string: feed), validFeed(url), url.host != nil,
              url.user == nil, url.password == nil,
              let key = Bundle.main.object(forInfoDictionaryKey: "SUPublicEDKey") as? String,
              Data(base64Encoded: key)?.count == 32 else {
            throw AppError.message("The update feed or signing key is missing or invalid.")
        }
        let updater: SPUUpdater
        #if UPDATE_REHEARSAL
        if let rehearsal {
            updater = SPUUpdater(hostBundle: .main, applicationBundle: .main, userDriver: rehearsal, delegate: self)
            rehearsal.updater = updater
        } else {
            let controller = SPUStandardUpdaterController(startingUpdater: false, updaterDelegate: self, userDriverDelegate: self)
            self.controller = controller
            updater = controller.updater
        }
        #else
        let controller = SPUStandardUpdaterController(startingUpdater: false, updaterDelegate: self, userDriverDelegate: self)
        self.controller = controller
        updater = controller.updater
        #endif
        activeUpdater = updater
        #if UPDATE_REHEARSAL
        rehearsal?.prepare(updater)
        #endif
        try updater.start()
        updater.publisher(for: \.canCheckForUpdates).sink { [weak self] _ in
            MainActor.assumeIsolated { self?.publish() }
        }.store(in: &observations)
        updater.publisher(for: \.automaticallyChecksForUpdates).sink { [weak self] _ in
            MainActor.assumeIsolated { self?.publish() }
        }.store(in: &observations)
        updater.publisher(for: \.automaticallyDownloadsUpdates).sink { [weak self] _ in
            MainActor.assumeIsolated { self?.publish() }
        }.store(in: &observations)
        updater.publisher(for: \.lastUpdateCheckDate).sink { [weak self] _ in
            MainActor.assumeIsolated { self?.publish() }
        }.store(in: &observations)
        publish()
    }

    func checkForUpdates() {
        updateError = nil
        publish()
        NSApplication.shared.activate()
        activeUpdater?.checkForUpdates()
    }

    func setAutomaticallyChecks(_ enabled: Bool) { activeUpdater?.automaticallyChecksForUpdates = enabled }
    func setAutomaticallyDownloads(_ enabled: Bool) { activeUpdater?.automaticallyDownloadsUpdates = enabled }
    private func validFeed(_ url: URL) -> Bool {
        #if UPDATE_REHEARSAL
        if let rehearsal { return url == rehearsal.feedURL && url.scheme == "http" && url.host == "127.0.0.1" }
        #endif
        return url.scheme == "https"
    }
    func allowedChannels(for updater: SPUUpdater) -> Set<String> { [] }
    func allowedSystemProfileKeys(for updater: SPUUpdater) -> [String]? { [] }
    var supportsGentleScheduledUpdateReminders: Bool { true }

    func standardUserDriverShouldHandleShowingScheduledUpdate(_ update: SUAppcastItem, andInImmediateFocus immediateFocus: Bool) -> Bool {
        false
    }

    func standardUserDriverWillHandleShowingUpdate(_ handleShowingUpdate: Bool, forUpdate update: SUAppcastItem, state: SPUUserUpdateState) {
        availability.offered(version: update.displayVersionString, downloaded: state.stage != .notDownloaded)
        updateError = nil
        publish()
    }

    func standardUserDriverWillFinishUpdateSession() {
        availability.finished()
        publish()
    }

    func updater(_ updater: SPUUpdater, willInstallUpdateOnQuit item: SUAppcastItem,
                 immediateInstallationBlock immediateInstallHandler: @escaping () -> Void) -> Bool {
        availability.scheduledOnQuit(version: item.displayVersionString)
        publish()
        #if UPDATE_REHEARSAL
        rehearsal?.scheduledOnQuit()
        #endif
        return false
    }

    func updater(_ updater: SPUUpdater, didAbortWithError error: Error) {
        let error = error as NSError
        #if UPDATE_REHEARSAL
        rehearsal?.record("abort", fields: ["domain": error.domain, "code": error.code])
        #endif
        if error.domain == SUSparkleErrorDomain && error.code == SUError.noUpdateError.rawValue { return }
        availability.cancelled()
        if error.domain == SUSparkleErrorDomain && error.code == SUError.installationCanceledError.rawValue {
            publish()
            return
        }
        updateError = "The update could not complete. \(error.localizedDescription)"
        publish()
    }

    func updater(_ updater: SPUUpdater, userDidMake choice: SPUUserUpdateChoice, forUpdate item: SUAppcastItem, state: SPUUserUpdateState) {
        if choice == .skip { availability.cancelled(); publish() }
    }

    func updater(_ updater: SPUUpdater, didFinishUpdateCycleFor updateCheck: SPUUpdateCheck, error: Error?) {
        #if UPDATE_REHEARSAL
        rehearsal?.record("cycle-finished", fields: ["check": updateCheck.rawValue, "error": (error as NSError?)?.code ?? 0])
        #endif
        let error = error as NSError?
        if error == nil || (error?.domain == SUSparkleErrorDomain && error?.code == Int(SUError.noUpdateError.rawValue)) {
            updateError = nil
            publish()
        }
    }

    #if UPDATE_REHEARSAL
    func feedURLString(for updater: SPUUpdater) -> String? {
        guard let rehearsal else { return nil }
        return (UpdateRehearsal.marker == "rehearsal-B" ? rehearsal.downgradeURL : rehearsal.feedURL).absoluteString
    }

    func updater(_ updater: SPUUpdater, didFindValidUpdate item: SUAppcastItem) {
        rehearsal?.record("found", fields: ["version": item.versionString])
    }

    func updaterWillRelaunchApplication(_ updater: SPUUpdater) {
        rehearsal?.record("relaunching")
    }

    func updater(_ updater: SPUUpdater, failedToDownloadUpdate item: SUAppcastItem, error: Error) {
        rehearsal?.record("download-error", fields: ["code": (error as NSError).code])
    }
    #endif

    private func publish() {
        guard let updater = activeUpdater else { return }
        #if UPDATE_REHEARSAL
        rehearsal?.record("state", fields: ["canCheck": updater.canCheckForUpdates,
            "session": updater.sessionInProgress, "ready": availability.readyToInstall])
        #endif
        stateChanged?(UpdateState(canCheck: updater.canCheckForUpdates,
            automaticallyChecks: updater.automaticallyChecksForUpdates,
            automaticallyDownloads: updater.automaticallyDownloadsUpdates,
            lastChecked: updater.lastUpdateCheckDate, availableVersion: availability.version,
            readyToInstall: availability.readyToInstall, error: updateError))
    }
}
