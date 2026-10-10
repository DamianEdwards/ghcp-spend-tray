import AppKit
import Combine
import Sparkle

// This file is linked only by the explicitly opted-in local rehearsal build.
@MainActor
final class UpdateRehearsal: NSObject, SPUUserDriver {
    struct Configuration: Decodable {
        let root: String
        let identifier: String
        let installedApp: String
        let scenario: String
        let feed: String
        let downgradeFeed: String
    }

    #if UPDATE_REHEARSAL_B
    static let marker = "rehearsal-B"
    #else
    static let marker = "rehearsal-A"
    #endif
    let configuration: Configuration
    let directory: URL
    let feedURL: URL
    let downgradeURL: URL
    var updater: SPUUpdater?
    private weak var model: AppModel?
    private var initialization: AnyCancellable?
    private var scheduledAction: AnyCancellable?
    private var started = false
    private var finished = false
    private var automaticInstallation = false
    private let root: URL
    private var credentialTarget: String { "GHCPSpendTray/sparkle-rehearsal/" + configuration.identifier }
    private var syntheticToken: String { "synthetic-rehearsal-token" }

    static func load() throws -> UpdateRehearsal? {
        guard let path = Bundle.main.object(forInfoDictionaryKey: "GHCPUpdateRehearsal") as? String else { return nil }
        let configuration = try JSONDecoder().decode(Configuration.self, from: Data(contentsOf: URL(fileURLWithPath: path)))
        return try UpdateRehearsal(configuration)
    }

    private init(_ configuration: Configuration) throws {
        self.configuration = configuration
        root = URL(fileURLWithPath: configuration.root, isDirectory: true).standardizedFileURL
        directory = root.appendingPathComponent("data", isDirectory: true)
        guard configuration.identifier.range(of: #"^com\.damianedwards\.GHCPSpendTray\.rehearsal\.[a-f0-9]{32}$"#,
                                              options: .regularExpression) != nil,
              Bundle.main.bundleIdentifier == configuration.identifier,
              root.path.contains("/artifacts/macos-rehearsal/"),
              root.resolvingSymlinksInPath() == root,
              Bundle.main.bundleURL.standardizedFileURL.path == configuration.installedApp,
              configuration.installedApp.hasPrefix(root.path + "/"),
              let feedURL = URL(string: configuration.feed),
              feedURL.scheme == "http", feedURL.host == "127.0.0.1", feedURL.port != nil,
              feedURL.user == nil, feedURL.password == nil,
              let downgradeURL = URL(string: configuration.downgradeFeed),
              downgradeURL.scheme == feedURL.scheme, downgradeURL.host == feedURL.host,
              downgradeURL.port == feedURL.port,
              ["manual", "background-relaunch", "on-quit", "cancel-download", "cancel-install",
               "corrupt-archive", "wrong-signature", "interrupted-download", "cleanup"].contains(configuration.scenario) else {
            throw AppError.message("Unsafe or invalid isolated update rehearsal configuration.")
        }
        self.feedURL = feedURL
        self.downgradeURL = downgradeURL
        super.init()
    }

    func record(_ event: String, fields: [String: Any] = [:]) {
        do {
            var values = fields
            values["event"] = event
            values["marker"] = Self.marker
            values["pid"] = ProcessInfo.processInfo.processIdentifier
            values["scenario"] = configuration.scenario
            values["time"] = Date().ISO8601Format()
            var data = try JSONSerialization.data(withJSONObject: values, options: [.sortedKeys])
            data.append(10)
            let path = root.appendingPathComponent("events.jsonl")
            if !FileManager.default.fileExists(atPath: path.path) { try Data().write(to: path) }
            let file = try FileHandle(forWritingTo: path)
            try file.seekToEnd()
            try file.write(contentsOf: data)
            try file.close()
            FileHandle.standardError.write(data)
        } catch {
            FileHandle.standardError.write(Data("FAIL: cannot record update rehearsal evidence: \(error.localizedDescription)\n".utf8))
            exit(1)
        }
    }

    func run(_ model: AppModel) {
        self.model = model
        initialization = model.$initialized.filter { $0 }.prefix(1).sink { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                do { try self.initialized(model) }
                catch { self.record("failure", fields: ["message": error.localizedDescription]); self.finish() }
            }
        }
    }

    func prepare(_ updater: SPUUpdater) {
        if Self.marker == "rehearsal-B" || configuration.scenario == "cleanup" {
            record("update-preferences", fields: ["checks": updater.automaticallyChecksForUpdates,
                                                 "downloads": updater.automaticallyDownloadsUpdates])
            // The replacement's only remaining operation is a controlled downgrade probe.
            updater.automaticallyChecksForUpdates = false
            updater.automaticallyDownloadsUpdates = false
        }
    }

    private func initialized(_ model: AppModel) throws {
        guard !started, model.demo, model.directory == directory,
              model.updates.enabled || configuration.scenario == "cleanup" else {
            throw AppError.message("The rehearsal did not initialize the isolated real app and updater.")
        }
        started = true
        record("started", fields: ["bundle": Bundle.main.bundleURL.path,
            "executable": Bundle.main.executableURL?.path ?? "", "version": Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") ?? ""])
        if configuration.scenario == "cleanup" {
            try KeychainStore.delete(credentialTarget)
            UserDefaults.standard.removePersistentDomain(forName: configuration.identifier)
            record("cleaned")
            finish()
            return
        }
        let config = directory.appendingPathComponent("config.json")
        let history = directory.appendingPathComponent("history/synthetic.jsonl")
        if Self.marker == "rehearsal-A" {
            try FileManager.default.createDirectory(at: history.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data(#"{"synthetic":true,"pollMinutes":15,"thresholds":"50,80,100"}"#.utf8).write(to: config)
            try Data("{\"synthetic\":true,\"consumptionUsd\":12.5}\n".utf8).write(to: history)
            try KeychainStore.write(credentialTarget, tokens: Tokens(version: 1, accessToken: syntheticToken,
                refreshToken: nil, expiresAtUtc: nil, refreshExpiresAtUtc: nil, scope: "synthetic"))
            model.updates.setAutomaticallyChecks(configuration.scenario == "background-relaunch" || configuration.scenario == "on-quit")
            model.updates.setAutomaticallyDownloads(configuration.scenario == "background-relaunch" || configuration.scenario == "on-quit")
        } else {
            guard try Data(contentsOf: config) == Data(#"{"synthetic":true,"pollMinutes":15,"thresholds":"50,80,100"}"#.utf8),
                  try Data(contentsOf: history) == Data("{\"synthetic\":true,\"consumptionUsd\":12.5}\n".utf8),
                  try KeychainStore.read(credentialTarget, allowInteraction: false)?.accessToken == syntheticToken else {
                throw AppError.message("Synthetic settings, history or Keychain data changed during replacement.")
            }
            record("preserved", fields: ["settings": true, "history": true, "keychain": true])
        }
        if Self.marker == "rehearsal-A" && (configuration.scenario == "background-relaunch" || configuration.scenario == "on-quit") {
            if updater?.sessionInProgress == false { updater?.checkForUpdatesInBackground() }
        } else {
            guard model.updates.state.canCheck else { throw AppError.message("The real updater manual action is not available.") }
            model.updates.checkForUpdates()
        }
    }

    func scheduledOnQuit() {
        guard !automaticInstallation else { return }
        automaticInstallation = true
        record("scheduled-on-quit", fields: ["action": model?.updates.actionTitle ?? "missing"])
        if configuration.scenario == "on-quit" {
            Task { @MainActor in NSApplication.shared.terminate(nil) }
        } else if let model {
            scheduledAction = model.updates.$state.filter { $0.canCheck && $0.readyToInstall }.sink { _ in
                Task { @MainActor in
                    guard self.scheduledAction != nil, model.updates.state.canCheck else { return }
                    self.scheduledAction = nil
                    self.record("resume-background-install", fields: ["canCheck": model.updates.state.canCheck])
                    model.updates.checkForUpdates()
                }
            }
        }
    }

    private func finish() {
        guard !finished else { return }
        finished = true
        Task { @MainActor in NSApplication.shared.terminate(nil) }
    }

    func show(_ request: SPUUpdatePermissionRequest, reply: @escaping (SUUpdatePermissionResponse) -> Void) {
        reply(SUUpdatePermissionResponse(automaticUpdateChecks: false, sendSystemProfile: false))
    }
    func showUserInitiatedUpdateCheck(cancellation: @escaping () -> Void) { record("checking") }
    func showUpdateFound(with appcastItem: SUAppcastItem, state: SPUUserUpdateState, reply: @escaping (SPUUserUpdateChoice) -> Void) {
        record("offered", fields: ["version": appcastItem.versionString, "stage": state.stage.rawValue, "userInitiated": state.userInitiated])
        reply(.install)
    }
    func showUpdateReleaseNotes(with downloadData: SPUDownloadData) { record("release-notes") }
    func showUpdateReleaseNotesFailedToDownloadWithError(_ error: Error) { record("release-notes-error"); finish() }
    func showUpdateNotFoundWithError(_ error: Error, acknowledgement: @escaping () -> Void) {
        let error = error as NSError
        record("not-found", fields: ["domain": error.domain, "code": error.code,
            "reason": (error.userInfo[SPUNoUpdateFoundReasonKey] as? NSNumber)?.intValue ?? -1])
        acknowledgement()
        finish()
    }
    func showUpdaterError(_ error: Error, acknowledgement: @escaping () -> Void) {
        let error = error as NSError
        var errors: [[String: Any]] = []
        var underlying: NSError? = error
        while let current = underlying, errors.count < 8 {
            errors.append(["domain": current.domain, "code": current.code])
            underlying = current.userInfo[NSUnderlyingErrorKey] as? NSError
        }
        record("updater-error", fields: ["domain": error.domain, "code": error.code,
                                        "errors": errors, "message": error.localizedDescription])
        acknowledgement()
        finish()
    }
    func showDownloadInitiated(cancellation: @escaping () -> Void) {
        record("downloading")
        if configuration.scenario == "cancel-download" {
            record("cancelled-download")
            cancellation()
        }
    }
    func showDownloadDidReceiveExpectedContentLength(_ expectedContentLength: UInt64) {
        record("download-length", fields: ["bytes": expectedContentLength])
    }
    func showDownloadDidReceiveData(ofLength length: UInt64) {}
    func showDownloadDidStartExtractingUpdate() { record("extracting") }
    func showExtractionReceivedProgress(_ progress: Double) {}
    func showReady(toInstallAndRelaunch reply: @escaping (SPUUserUpdateChoice) -> Void) {
        record("ready-to-install")
        if configuration.scenario == "cancel-install" {
            record("cancelled-install")
            reply(.skip)
        } else {
            reply(.install)
        }
    }
    func showInstallingUpdate(withApplicationTerminated applicationTerminated: Bool, retryTerminatingApplication: @escaping () -> Void) {
        record("installing", fields: ["applicationTerminated": applicationTerminated])
    }
    func showUpdateInstalledAndRelaunched(_ relaunched: Bool, acknowledgement: @escaping () -> Void) {
        record("installed", fields: ["relaunched": relaunched])
        acknowledgement()
    }
    func dismissUpdateInstallation() {
        record("dismissed")
        if configuration.scenario == "cancel-download" || configuration.scenario == "cancel-install" { finish() }
    }
}
