import CryptoKit
import Foundation

@main
enum UpdateCrypto {
    static func main() {
        do {
            try validate()
        } catch {
            FileHandle.standardError.write(Data("Sparkle signing validation failed: \(error.localizedDescription)\n".utf8))
            exit(1)
        }
    }

    private static func validate() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let privateText = environment["SPARKLE_PRIVATE_ED_KEY"],
              let privateData = Data(base64Encoded: privateText.trimmingCharacters(in: .whitespacesAndNewlines)),
              privateData.count == 32,
              let publicText = environment["SPARKLE_PUBLIC_ED_KEY"],
              let publicData = Data(base64Encoded: publicText), publicData.count == 32 else {
            throw NSError(domain: "GHCPSpendTray.Updates", code: 1,
                userInfo: [NSLocalizedDescriptionKey: "Configure a Sparkle 32-byte private seed and its 32-byte public key."])
        }
        let key = try Curve25519.Signing.PrivateKey(rawRepresentation: privateData)
        guard key.publicKey.rawRepresentation == publicData else {
            throw NSError(domain: "GHCPSpendTray.Updates", code: 2,
                userInfo: [NSLocalizedDescriptionKey: "The Sparkle private and public signing keys do not match."])
        }
        if CommandLine.arguments.count == 3 {
            guard let signature = Data(base64Encoded: CommandLine.arguments[2]),
                  key.publicKey.isValidSignature(signature, for: try Data(contentsOf: URL(fileURLWithPath: CommandLine.arguments[1]))) else {
                throw NSError(domain: "GHCPSpendTray.Updates", code: 3,
                    userInfo: [NSLocalizedDescriptionKey: "The update archive signature is invalid."])
            }
        }
    }
}
