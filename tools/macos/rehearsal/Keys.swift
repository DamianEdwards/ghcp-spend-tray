import CryptoKit
import Foundation

@main
enum RehearsalKeys {
    static func main() throws {
        guard CommandLine.arguments.count == 3 else {
            throw NSError(domain: "RehearsalKeys", code: 1)
        }
        let key = Curve25519.Signing.PrivateKey()
        let seed = URL(fileURLWithPath: CommandLine.arguments[1])
        try Data(key.rawRepresentation.base64EncodedString().utf8).write(to: seed)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: seed.path)
        try Data(key.publicKey.rawRepresentation.base64EncodedString().utf8)
            .write(to: URL(fileURLWithPath: CommandLine.arguments[2]))
    }
}
