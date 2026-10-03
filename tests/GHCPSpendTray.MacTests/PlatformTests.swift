import Foundation
import Security

@main
enum PlatformTests {
    @MainActor static func main() async throws {
        try ModelTests.run()
        try TrayIconRendererTests.run()
        try await NotificationTests.run()
        func check(_ condition: Bool, _ message: String) throws {
            if !condition { throw AppError.message(message) }
        }
        try check(money(nil) == "Unavailable", "Missing consumption must not display zero.")
        try check(money(Decimal(string: "26.25")) == "$26.25", "Exact USD display.")
        try check(try parseAmount("12.50") == Decimal(string: "12.5"), "Exact cent parsing.")
        try check(try parseAmount("") == nil, "Empty amount inherits.")
        for value in ["-1", "1.001", "NaN", "1,50", "1e2", "12oops"] {
            do {
                _ = try parseAmount(value)
                throw AppError.message("Invalid amount accepted: \(value)")
            } catch AppError.message(let message) {
                if message.hasPrefix("Invalid amount accepted") { throw AppError.message(message) }
            }
        }
        try check(dateValue("2026-09-27T12:00:00.1234567+00:00") != nil, "C# date parsing.")
        let target = "GHCPSpendTray/synthetic-test/" + UUID().uuidString
        let other = target + "-other-host"
        var cleanupError: Error?
        do {
            let tokens = Tokens(version: 1, accessToken: "synthetic-test-token", refreshToken: "synthetic-refresh",
                                expiresAtUtc: nil, refreshExpiresAtUtc: nil, scope: "read:user")
            try check(try KeychainStore.read(target) == nil, "Synthetic credential starts missing.")
            try KeychainStore.write(target, tokens: tokens)
            try check(try KeychainStore.read(target)?.accessToken == tokens.accessToken, "Keychain round-trip.")
            try check(try KeychainStore.read(other) == nil, "Keychain targets are isolated.")
            let rotated = Tokens(version: 1, accessToken: "synthetic-rotated", refreshToken: nil,
                                 expiresAtUtc: nil, refreshExpiresAtUtc: nil, scope: nil)
            try KeychainStore.write(target, tokens: rotated)
            try check(try KeychainStore.read(target)?.accessToken == rotated.accessToken, "Keychain replacement.")
            try KeychainStore.delete(target)
            try check(try KeychainStore.read(target) == nil, "Keychain deletion.")
            try KeychainStore.delete(target)
        } catch {
            do { try KeychainStore.delete(target) } catch { cleanupError = error }
            if let cleanupError { throw cleanupError }
            throw error
        }
        print("PASS: macOS formatting, date parsing and synthetic Keychain create/read/update/delete/isolation.")
    }
}
