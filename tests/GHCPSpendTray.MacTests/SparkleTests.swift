import AppKit
import CryptoKit
import Sparkle

@MainActor
enum SparkleTests {
    static func run() async throws {
        for scenario in ["valid", "tampered", "unsigned", "incompatible"] {
            let diagnostics = try SparkleDiagnostics(scenario: scenario)
            diagnostics.record("Starting scenario; macOS \(ProcessInfo.processInfo.operatingSystemVersionString).")
            let directory = FileManager.default.temporaryDirectory.appendingPathComponent("ghcp-sparkle-" + UUID().uuidString)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let identifier = "com.damianedwards.GHCPSpendTray.synthetic." + UUID().uuidString
            defer {
                UserDefaults.standard.removePersistentDomain(forName: identifier)
                do { try FileManager.default.removeItem(at: directory) }
                catch { FileHandle.standardError.write(Data("Could not clean up the synthetic Sparkle fixture: \(error.localizedDescription)\n".utf8)) }
            }
            let key = try Curve25519.Signing.PrivateKey(rawRepresentation: Data(repeating: 7, count: 32))
            let signature = try key.signature(for: Data("synthetic archive".utf8)).base64EncodedString()
            let minimum = scenario == "incompatible" ? "99.0.0" : "15.0"
            let xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle"><channel>
            <title>Synthetic local update</title><item><sparkle:version>0.3.0</sparkle:version>
            <sparkle:minimumSystemVersion>\(minimum)</sparkle:minimumSystemVersion>
            <sparkle:hardwareRequirements>arm64</sparkle:hardwareRequirements>
            <enclosure url="https://example.invalid/synthetic.dmg" length="17" type="application/octet-stream"
            sparkle:edSignature="\(signature)"/></item></channel></rss>
            """
            let data = Data(xml.utf8)
            let feedSignature = try key.signature(for: data).base64EncodedString()
            var feed = xml
            if scenario != "unsigned" {
                feed += "<!-- sparkle-signatures:\nedSignature: \(feedSignature)\nlength: \(data.count)\n-->"
            }
            if scenario == "tampered" { feed = feed.replacingOccurrences(of: "0.3.0", with: "0.4.0") }
            let feedData = Data(feed.utf8)
            let server = try LoopbackAppcastServer(feed: feedData, report: diagnostics.record)
            defer { server.stop() }
            let feedURL = try await server.start()
            let session = URLSession(configuration: .ephemeral)
            defer { session.invalidateAndCancel() }
            var request = URLRequest(url: feedURL, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 10)
            request.httpMethod = "GET"
            let (responseData, response) = try await session.data(for: request)
            guard (response as? HTTPURLResponse)?.statusCode == 200, responseData == feedData else {
                throw AppError.message("The native appcast listener failed its exact-byte HTTP readiness check.")
            }
            diagnostics.record("Listener responsive; exact-byte HTTP check passed.")
            let app = directory.appendingPathComponent("Synthetic.app")
            let contents = app.appendingPathComponent("Contents")
            let macOS = contents.appendingPathComponent("MacOS")
            try FileManager.default.createDirectory(at: macOS, withIntermediateDirectories: true)
            guard let executable = Bundle.main.executableURL else { throw AppError.message("Synthetic updater executable missing.") }
            try FileManager.default.copyItem(at: executable, to: macOS.appendingPathComponent("Synthetic"))
            let info: [String: Any] = [
                "CFBundleIdentifier": identifier, "CFBundleName": "Synthetic",
                "CFBundleExecutable": "Synthetic", "CFBundlePackageType": "APPL",
                "CFBundleVersion": "0.1.0", "CFBundleShortVersionString": "0.1.0",
                "SUFeedURL": feedURL.absoluteString, "SUPublicEDKey": key.publicKey.rawRepresentation.base64EncodedString(),
                "SUEnableAutomaticChecks": false, "SUEnableSystemProfiling": false, "SUDefaultsDomain": identifier,
                "SURequireSignedFeed": true, "SUVerifyUpdateBeforeExtraction": true, "SUEnableDownloaderService": false
            ]
            try PropertyListSerialization.data(fromPropertyList: info, format: .xml, options: 0)
                .write(to: contents.appendingPathComponent("Info.plist"))
            let sign = Process()
            sign.executableURL = URL(fileURLWithPath: "/usr/bin/codesign")
            sign.arguments = ["--force", "--sign", "-", app.path]
            try sign.run()
            sign.waitUntilExit()
            diagnostics.record("Synthetic bundle signing exit status: \(sign.terminationStatus).")
            guard sign.terminationStatus == 0, let bundle = Bundle(url: app) else {
                throw AppError.message("Could not sign the synthetic updater fixture.")
            }
            let probe = SparkleProbe(report: diagnostics.record)
            server.failed = { [weak probe] error in probe?.completion.complete(.failure(error)) }
            let driver = SPUStandardUserDriver(hostBundle: bundle, delegate: nil)
            let updater = SPUUpdater(hostBundle: bundle, applicationBundle: bundle, userDriver: driver, delegate: probe)
            try updater.start()
            diagnostics.record("Updater started; requesting update information.")
            updater.checkForUpdateInformation()
            try await probe.completion.value(failure: "Timed out awaiting Sparkle's \(scenario) update-cycle callback.")
            try diagnostics.check()
            guard probe.loadedAppcast == (scenario == "valid" || scenario == "incompatible") else {
                throw AppError.message("Sparkle's \(scenario) appcast verification outcome was unexpected.")
            }
            if scenario == "valid" {
                guard probe.foundVersion == "0.3.0", probe.error == nil else {
                    throw AppError.message("Sparkle did not accept the signed loopback appcast: \((probe.error as NSError?)?.userInfo.description ?? "no valid update").")
                }
            } else {
                guard probe.foundVersion == nil, let error = probe.error as NSError?,
                      error.domain == SUSparkleErrorDomain else {
                    throw AppError.message("Sparkle accepted the \(scenario) synthetic appcast.")
                }
                if scenario == "incompatible" {
                    guard error.code == Int(SUError.noUpdateError.rawValue),
                          (error.userInfo[SPUNoUpdateFoundReasonKey] as? NSNumber)?.intValue ==
                            Int(SPUNoUpdateFoundReason.systemIsTooOld.rawValue) else {
                        throw AppError.message("Sparkle did not reject the fixture for the required OS version.")
                    }
                } else {
                    let underlying = error.userInfo[NSUnderlyingErrorKey] as? NSError
                    guard error.code == Int(SUError.appcastParseError.rawValue),
                          underlying?.domain == SUSparkleErrorDomain,
                          underlying?.code == Int(SUError.validationError.rawValue) else {
                        throw AppError.message("Sparkle's \(scenario) failure was not appcast signature rejection.")
                    }
                }
            }
            diagnostics.record("PASS: \(scenario); version=\(probe.foundVersion ?? "none"); completed HTTP responses=\(server.servedFeeds).")
        }
        print("PASS: real Sparkle signed appcast discovery, tampering/unsigned-feed rejection and OS filtering without installation or external network access.")
    }
}

@MainActor
private final class SparkleProbe: NSObject, SPUUpdaterDelegate {
    let completion = CallbackWait<Void>()
    private let report: (String) -> Void
    var foundVersion: String?
    var loadedAppcast = false
    var error: Error?
    init(report: @escaping (String) -> Void) { self.report = report }
    func updater(_ updater: SPUUpdater, didFinishLoading appcast: SUAppcast) {
        loadedAppcast = true
        report("Sparkle finished loading the verified appcast.")
    }
    func updater(_ updater: SPUUpdater, didFindValidUpdate item: SUAppcastItem) {
        foundVersion = item.versionString
        report("Sparkle found valid update \(item.versionString).")
    }
    func updater(_ updater: SPUUpdater, didFinishUpdateCycleFor updateCheck: SPUUpdateCheck, error: Error?) {
        self.error = error
        report("Sparkle update-cycle callback: \((error as NSError?)?.description ?? "success").")
        completion.complete(.success(()))
    }
}

@MainActor
private final class SparkleDiagnostics {
    private let path: URL
    private let scenario: String
    private var lines = ""
    private var error: Error?

    init(scenario: String) throws {
        self.scenario = scenario
        let directory = URL(fileURLWithPath: "artifacts/macos-test-diagnostics", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        path = directory.appendingPathComponent("sparkle-\(scenario).log")
        try Data().write(to: path)
    }

    func record(_ message: String) {
        let line = "SPARKLE [\(scenario)] \(Date().ISO8601Format()): \(message)\n"
        FileHandle.standardError.write(Data(line.utf8))
        lines += line
        do {
            try lines.write(to: path, atomically: true, encoding: .utf8)
        } catch {
            self.error = error
            FileHandle.standardError.write(Data("Could not retain synthetic Sparkle diagnostics: \(error.localizedDescription)\n".utf8))
        }
    }

    func check() throws {
        if let error { throw error }
    }
}
