import Foundation

@main
enum RehearsalServer {
    @MainActor static func main() async throws {
        guard CommandLine.arguments.count == 2 else { throw AppError.message("Supply the isolated rehearsal directory.") }
        let directory = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        let server = try LoopbackAppcastServer(directory: directory) {
            FileHandle.standardError.write(Data(($0 + "\n").utf8))
        }
        let url = try await server.start()
        FileHandle.standardOutput.write(Data((url.deletingLastPathComponent().absoluteString + "\n").utf8))
        let keepAlive = CallbackWait<Void>()
        try await keepAlive.value(timeout: .seconds(3600), failure: "The rehearsal server exceeded its one-hour safety limit.")
        server.stop()
    }
}
