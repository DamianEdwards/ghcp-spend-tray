import AppKit
import CryptoKit
import Sparkle

@MainActor
enum SparkleTests {
    static func run() async throws {
        for scenario in ["valid", "tampered", "unsigned", "incompatible"] {
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
            let feedURL = directory.appendingPathComponent("appcast.xml")
            try Data(feed.utf8).write(to: feedURL)
            let server = Process()
            let readiness = directory.appendingPathComponent("port.txt")
            server.executableURL = URL(fileURLWithPath: "/usr/bin/env")
            server.arguments = ["python3", "-u", "-c", """
            import http.server, pathlib, sys
            class Handler(http.server.BaseHTTPRequestHandler):
                def do_GET(self):
                    if self.path != "/appcast.xml":
                        self.send_error(404)
                        return
                    data = pathlib.Path(sys.argv[1]).read_bytes()
                    self.send_response(200)
                    self.send_header("Content-Type", "application/xml")
                    self.send_header("Content-Length", str(len(data)))
                    self.end_headers()
                    self.wfile.write(data)
                def log_message(self, *args):
                    pass
            server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
            ready = pathlib.Path(sys.argv[2])
            temporary = ready.with_suffix(".pending")
            temporary.write_text(str(server.server_port))
            temporary.replace(ready)
            server.serve_forever()
            """, feedURL.path, readiness.path]
            server.standardOutput = FileHandle.nullDevice
            try server.run()
            defer {
                if server.isRunning { server.terminate() }
                server.waitUntilExit()
            }
            let startupDeadline = ContinuousClock.now.advanced(by: .seconds(10))
            while !FileManager.default.fileExists(atPath: readiness.path) && server.isRunning &&
                ContinuousClock.now < startupDeadline { try await Task.sleep(for: .milliseconds(25)) }
            guard FileManager.default.fileExists(atPath: readiness.path), server.isRunning else {
                throw AppError.message("The synthetic loopback update server did not start.")
            }
            guard let port = Int(try String(contentsOf: readiness, encoding: .utf8)), (1...65535).contains(port) else {
                throw AppError.message("The synthetic loopback server returned an invalid port.")
            }
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
                "SUFeedURL": "http://127.0.0.1:\(port)/appcast.xml", "SUPublicEDKey": key.publicKey.rawRepresentation.base64EncodedString(),
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
            guard sign.terminationStatus == 0, let bundle = Bundle(url: app) else {
                throw AppError.message("Could not sign the synthetic updater fixture.")
            }
            let probe = SparkleProbe()
            let driver = SPUStandardUserDriver(hostBundle: bundle, delegate: nil)
            let updater = SPUUpdater(hostBundle: bundle, applicationBundle: bundle, userDriver: driver, delegate: probe)
            try updater.start()
            updater.checkForUpdateInformation()
            let deadline = ContinuousClock.now.advanced(by: .seconds(10))
            while !probe.finished && ContinuousClock.now < deadline { try await Task.sleep(for: .milliseconds(25)) }
            guard probe.finished else { throw AppError.message("Timed out reading the \(scenario) synthetic appcast.") }
            if scenario == "valid" {
                guard probe.foundVersion == "0.3.0", probe.error == nil else {
                    throw AppError.message("Sparkle did not accept the signed loopback appcast: \((probe.error as NSError?)?.userInfo.description ?? "no valid update").")
                }
            } else {
                guard probe.foundVersion == nil, probe.error != nil else {
                    throw AppError.message("Sparkle accepted the \(scenario) synthetic appcast.")
                }
            }
        }
        print("PASS: real Sparkle signed appcast discovery, tampering/unsigned-feed rejection and OS filtering without installation or external network access.")
    }
}

@MainActor
private final class SparkleProbe: NSObject, SPUUpdaterDelegate {
    var foundVersion: String?
    var finished = false
    var error: Error?
    func updater(_ updater: SPUUpdater, didFindValidUpdate item: SUAppcastItem) { foundVersion = item.versionString }
    func updater(_ updater: SPUUpdater, didFinishUpdateCycleFor updateCheck: SPUUpdateCheck, error: Error?) {
        self.error = error
        finished = true
    }
}
