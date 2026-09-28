import AppKit
import Security
import ServiceManagement
import UserNotifications

enum KeychainStore {
    static let service = "com.damianedwards.GHCPSpendTray"

    static func query(_ target: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: target,
            kSecAttrSynchronizable as String: false
        ]
    }

    static func check(_ status: OSStatus) throws {
        guard status == errSecSuccess else {
            // Never include a credential, target, or system response in diagnostics.
            throw AppError.message("Keychain operation failed (status \(status)). Unlock your login keychain and check access permissions, then retry.")
        }
    }

    static func read(_ target: String) throws -> Tokens? {
        var query = query(target)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        try check(status)
        guard let data = result as? Data else {
            throw AppError.message("The saved Keychain credential is invalid. Reconnect this account.")
        }
        do { return try JSONDecoder().decode(Tokens.self, from: data) }
        catch { throw AppError.message("The saved Keychain credential is invalid. Reconnect this account.") }
    }

    static func write(_ target: String, tokens: Tokens) throws {
        let data = try JSONEncoder().encode(tokens)
        let changes = [kSecValueData as String: data]
        let status = SecItemUpdate(query(target) as CFDictionary, changes as CFDictionary)
        if status == errSecItemNotFound {
            var item = query(target)
            item[kSecValueData as String] = data
            item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            item[kSecAttrLabel as String] = "GHCPSpendTray OAuth credential"
            try check(SecItemAdd(item as CFDictionary, nil))
        } else {
            try check(status)
        }
    }

    static func delete(_ target: String) throws {
        let status = SecItemDelete(query(target) as CFDictionary)
        if status != errSecItemNotFound { try check(status) }
    }
}

@MainActor
enum Platform {
    static func startupStatus() -> [String: Any] {
        let status = SMAppService.mainApp.status
        let description: String
        switch status {
        case .enabled: description = "Launch quietly in the menu bar when you log in."
        case .notRegistered: description = "Launch quietly in the menu bar when you log in."
        case .requiresApproval: description = "Allow GHCPSpendTray in System Settings > General > Login Items."
        case .notFound: description = "Move GHCPSpendTray to Applications, then reopen it to configure login startup."
        @unknown default: description = "Check System Settings > General > Login Items."
        }
        return [
            "enabled": status == .enabled,
            "canChange": status == .enabled || status == .notRegistered,
            "description": description
        ]
    }

    static func notify(title: String, message: String, key: String?) async throws -> Bool {
        let center = UNUserNotificationCenter.current()
        var authorization = await authorizationStatus(center)
        if authorization == .notDetermined {
            let allowed: Bool = try await withCheckedThrowingContinuation { continuation in
                center.requestAuthorization(options: [.alert, .sound]) { allowed, error in
                    if let error { continuation.resume(throwing: error) }
                    else { continuation.resume(returning: allowed) }
                }
            }
            if !allowed { return false }
            authorization = await authorizationStatus(center)
        }
        guard authorization == .authorized || authorization == .provisional else {
            return false
        }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = message
        content.sound = .default
        if let key { content.userInfo = ["accountKey": key] }
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            center.add(request) { error in
                if let error { continuation.resume(throwing: error) }
                else { continuation.resume() }
            }
        }
        return true
    }

    private static func authorizationStatus(_ center: UNUserNotificationCenter) async -> UNAuthorizationStatus {
        // Older SDKs do not make UNNotificationSettings Sendable; transfer only the enum.
        await withCheckedContinuation { continuation in
            center.getNotificationSettings { settings in
                continuation.resume(returning: settings.authorizationStatus)
            }
        }
    }

    static func handle(_ event: BridgeEvent) async throws -> [String: Any] {
        switch event.operation {
        case "startup.read":
            return startupStatus()
        case "startup.write":
            do {
                if event.enabled { try SMAppService.mainApp.register() }
                else { try await SMAppService.mainApp.unregister() }
            } catch {
                throw AppError.message("macOS could not change login startup. Move the app to Applications and check System Settings > General > Login Items.")
            }
            return startupStatus()
        case "credential.read", "credential.write", "credential.delete":
            guard let target = event.target, target.hasPrefix("GHCPSpendTray/v1/") else {
                throw AppError.message("Invalid credential-store request.")
            }
            switch event.operation {
            case "credential.read":
                if let tokens = try KeychainStore.read(target) { return ["tokens": try jsonObject(tokens)] }
            case "credential.write":
                guard let tokens = event.tokens else { throw AppError.message("Missing credential data.") }
                try KeychainStore.write(target, tokens: tokens)
            default: try KeychainStore.delete(target)
            }
            return [:]
        case "notification":
            do {
                let accepted = try await notify(title: event.title ?? "GHCPSpendTray", message: event.message ?? "", key: event.key)
                return ["accepted": accepted]
            } catch {
                throw AppError.message("macOS rejected the notification. Check System Settings > Notifications > GHCPSpendTray.")
            }
        default:
            throw AppError.message("Unknown macOS platform request.")
        }
    }
}
