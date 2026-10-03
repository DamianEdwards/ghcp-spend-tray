import AppKit
import UserNotifications

enum NotificationPermission: Sendable {
    case notDetermined, denied, authorized, quiet, unavailable

    var canSend: Bool { self == .authorized || self == .quiet }

    var title: String {
        switch self {
        case .notDetermined: return "Enable macOS Notifications"
        case .denied: return "Notifications Are Off in macOS"
        case .authorized: return "Notifications Are Allowed"
        case .quiet: return "Notifications Are Delivered Quietly"
        case .unavailable: return "Check macOS Notification Settings"
        }
    }

    var explanation: String {
        switch self {
        case .notDetermined:
            return "Allow GHCPSpendTray to show consumption alerts. Choose Enable Notifications, then allow them in the macOS prompt."
        case .denied:
            return "macOS requires you to turn Allow Notifications back on. Open Notification Settings below; if the app is not selected, choose GHCPSpendTray. This page updates when you return."
        case .authorized:
            return "macOS allows notifications. Focus and notification preview settings may still suppress banners."
        case .quiet:
            return "Notifications can reach Notification Center, but banners may be disabled. Open Notification Settings to choose how they appear."
        case .unavailable:
            return "macOS returned an unrecognized permission state. Open Notification Settings to review access."
        }
    }

    var actionTitle: String {
        self == .notDetermined ? "Enable Notifications" : "Open Notification Settings"
    }
}

@MainActor
protocol NotificationService {
    func status() async -> NotificationPermission
    func requestAuthorization() async throws
    func send(title: String, message: String, key: String?) async throws -> Bool
    func openSettings() throws
}

@MainActor
final class NativeNotifications: NotificationService {
    func status() async -> NotificationPermission {
        // Older SDKs omit Sendable on these callbacks; macOS invokes them off the main actor.
        await withCheckedContinuation { continuation in
            UNUserNotificationCenter.current().getNotificationSettings { @Sendable settings in
                let permission: NotificationPermission
                switch settings.authorizationStatus {
                case .notDetermined: permission = .notDetermined
                case .denied: permission = .denied
                case .authorized: permission = settings.alertSetting == .enabled ? .authorized : .quiet
                case .provisional: permission = .quiet
                @unknown default: permission = .unavailable
                }
                continuation.resume(returning: permission)
            }
        }
    }

    func requestAuthorization() async throws {
        let _: Bool = try await withCheckedThrowingContinuation { continuation in
            UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { @Sendable allowed, error in
                if let error { continuation.resume(throwing: error) }
                else { continuation.resume(returning: allowed) }
            }
        }
    }

    func send(title: String, message: String, key: String?) async throws -> Bool {
        // Scheduled refreshes never initiate a permission prompt or open System Settings.
        guard await status().canSend else { return false }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = message
        content.sound = .default
        if let key { content.userInfo = ["accountKey": key] }
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            UNUserNotificationCenter.current().add(request) { @Sendable error in
                if let error { continuation.resume(throwing: error) }
                else { continuation.resume() }
            }
        }
        return true
    }

    func openSettings() throws {
        guard let identifier = Bundle.main.bundleIdentifier else {
            throw AppError.message("The application bundle has no identifier. Reinstall GHCPSpendTray.")
        }
        var destination = URLComponents()
        destination.scheme = "x-apple.systempreferences"
        destination.path = "com.apple.preference.notifications"
        destination.queryItems = [URLQueryItem(name: "id", value: identifier)]
        guard let url = destination.url, NSWorkspace.shared.open(url) else {
            throw AppError.message("Could not open Notification Settings. Open System Settings > Notifications > GHCPSpendTray.")
        }
    }
}
