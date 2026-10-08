import Foundation

enum SettingsPage: String, CaseIterable, Identifiable {
    case usage = "Usage", accounts = "Accounts", general = "General", notifications = "Notifications", about = "About"
    var id: String { rawValue }
    var icon: String {
        switch self {
        case .usage: return "chart.bar"
        case .accounts: return "person.crop.circle"
        case .general: return "gear"
        case .notifications: return "bell"
        case .about: return "info.circle"
        }
    }
}

struct SettingsData: Codable {
    var pollMinutes: Int
    var thresholds: String
    var notifications: Bool
    var startup: Bool
    var spendIncrementUsd: Decimal?
    var canChangeStartup: Bool
    var startupDescription: String
    var trayStyle: TrayIconStyle = .pie
    var trayMode: TrayDisplayMode = .rollUp
    var excludedTrayAccounts: [String]?
}

struct Dashboard: Decodable {
    let total: String
    let status: String
    let tooltip: String
    let accounts: [AccountData]
    let consumptionUsd: Decimal?
    let isComplete: Bool
    let isLastKnown: Bool
    let tray: TrayPresentation?
}

struct AccountData: Decodable, Identifiable {
    var id: String { key }
    let key: String
    let name: String
    let login: String
    let host: String
    let details: AccountDetails
    let percent: Decimal?
    let consumptionUsd: Decimal?
    let allocationUsd: Decimal?
    let freshness: String
    let updatedAt: String?
    let avatarUrl: String?
    let periodEstimate: PeriodEstimateData?
}

struct PeriodEstimateData: Decodable {
    let estimatedConsumptionUsd: Decimal?
    let averageDailyConsumptionUsd: Decimal?
    let overAllocationUsd: Decimal?
    let periodStartUtc: String?
    let resetAtUtc: String?
    let observedAtUtc: String?
    let isEarly: Bool
    let unavailableReason: String?

    var summary: String {
        var parts: [String] = []
        if isEarly { parts.append("Early estimate") }
        if let over = overAllocationUsd, over > 0 {
            parts.append(over < 1 ? "Less than $1 over allocation" : "About \(wholeMoney(over)) over allocation")
        }
        if let reset = resetAtUtc { parts.append("Resets \(utcDateText(reset))") }
        return parts.joined(separator: " · ")
    }
}

struct AccountDetails: Decodable {
    let creditsUsed: Decimal?
    let observedConsumptionUsd: Decimal?
    let observedAllocationUsd: Decimal?
    let observedPercentConsumed: Decimal?
    let unlimited: Bool
    let sourceTimestampUtc: String?
    let resetAtUtc: String?
    let nextRefreshUtc: String?
    let periodId: String?
    let isCurrentPeriod: Bool
    let message: String?
}

struct AccountPreferences: Decodable {
    let displayName: String
    let thresholds: String
    let spendIncrementUsd: Decimal?
    let clientId: String?
    let showPeriodEstimate: Bool
}

struct DevicePrompt: Decodable {
    let code: String
    let verificationUri: String
    let expires: String
}

struct Tokens: Codable {
    let version: Int
    let accessToken: String
    let refreshToken: String?
    let expiresAtUtc: String?
    let refreshExpiresAtUtc: String?
    let scope: String?
}

struct BridgeEvent: Decodable {
    let kind: String
    let id: String?
    let error: String?
    let text: String?
    let dashboard: Dashboard?
    let settings: SettingsData?
    let preferences: AccountPreferences?
    let tray: TrayPresentation?
    let prompt: DevicePrompt?
    let cancelled: Bool
    let operation: String?
    let target: String?
    let tokens: Tokens?
    let enabled: Bool
    let title: String?
    let message: String?
    let key: String?
}

struct Receipt: Decodable { let error: String? }

enum TrayIconStyle: Int, Codable, CaseIterable {
    case pie, percentage
}

enum TrayDisplayMode: Int, Codable, CaseIterable {
    case rollUp, perAccount
}

struct TrayIndicator: Decodable, Identifiable, Sendable {
    var id: String { accountKey ?? "rollup" }
    let accountKey: String?
    let name: String
    let percent: Double?
    let includedAccounts: Int
    let selectedAccounts: Int
    let details: String
    let tooltip: String
    let isPartial: Bool
    let isOverAllocation: Bool
    let valueText: String
    let numericText: String
    let isUnlimited: Bool

    static let unavailable = TrayIndicator(accountKey: nil, name: "GHCPSpendTray", percent: nil,
        includedAccounts: 0, selectedAccounts: 0, details: "Consumption unavailable.",
        tooltip: "GHCPSpendTray | Consumption unavailable", isPartial: false, isOverAllocation: false,
        valueText: "Unavailable", numericText: "?", isUnlimited: false)
}

struct TrayPresentation: Decodable {
    let style: TrayIconStyle
    let rollUp: TrayIndicator
    let icons: [TrayIndicator]
}

enum AppError: LocalizedError {
    case message(String)
    var errorDescription: String? {
        switch self { case .message(let message): return message }
    }
}

func jsonObject<T: Encodable>(_ value: T) throws -> Any {
    try JSONSerialization.jsonObject(with: JSONEncoder().encode(value))
}

func money(_ value: Decimal?) -> String {
    guard let value else { return "Unavailable" }
    let format = NumberFormatter()
    format.locale = Locale(identifier: "en_US")
    format.numberStyle = .currency
    return format.string(from: value as NSDecimalNumber) ?? "\(value) USD"
}

func wholeMoney(_ value: Decimal) -> String {
    var input = value
    var rounded = Decimal()
    NSDecimalRound(&rounded, &input, 0, .plain)
    let format = NumberFormatter()
    format.locale = Locale(identifier: "en_US")
    format.numberStyle = .currency
    format.minimumFractionDigits = 0
    format.maximumFractionDigits = 0
    return format.string(from: rounded as NSDecimalNumber) ?? "\(rounded) USD"
}

func estimatedMoney(_ value: Decimal?) -> String {
    guard let value else { return "Unavailable" }
    if value > 0 && value < 1 { return "<$1" }
    return "~\(wholeMoney(value))"
}

func decimalText(_ value: Decimal?) -> String {
    value.map { NSDecimalNumber(decimal: $0).stringValue } ?? ""
}

func parseAmount(_ text: String) throws -> Decimal? {
    let value = text.trimmingCharacters(in: .whitespacesAndNewlines)
    if value.isEmpty { return nil }
    guard value.range(of: #"^\d+(\.\d{1,2})?$"#, options: .regularExpression) != nil,
          let amount = Decimal(string: value, locale: Locale(identifier: "en_US_POSIX")),
          amount >= 0 else {
        throw AppError.message("Enter a nonnegative USD increment with at most two decimal places. Use 0 to disable.")
    }
    return amount
}

func dateValue(_ text: String?) -> Date? {
    guard let text else { return nil }
    let format = ISO8601DateFormatter()
    format.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
    if let date = format.date(from: text) { return date }
    format.formatOptions = [.withInternetDateTime]
    return format.date(from: text)
}

func dateText(_ text: String?) -> String {
    dateValue(text)?.formatted(date: .abbreviated, time: .shortened) ?? "Unavailable"
}

func utcDateText(_ text: String?) -> String {
    guard let date = dateValue(text) else { return "Unavailable" }
    let format = DateFormatter()
    format.locale = Locale.current
    format.timeZone = TimeZone(secondsFromGMT: 0)
    format.dateStyle = .medium
    format.timeStyle = .none
    return "\(format.string(from: date)) UTC"
}

func utcTimestampText(_ text: String?) -> String {
    guard let date = dateValue(text) else { return "Unavailable" }
    let format = DateFormatter()
    format.locale = Locale.current
    format.timeZone = TimeZone(secondsFromGMT: 0)
    format.dateStyle = .medium
    format.timeStyle = .short
    return "\(format.string(from: date)) UTC"
}
