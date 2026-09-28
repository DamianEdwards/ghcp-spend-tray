import Foundation

struct SettingsData: Codable {
    var pollMinutes: Int
    var thresholds: String
    var notifications: Bool
    var startup: Bool
    var spendIncrementUsd: Decimal?
    var canChangeStartup: Bool
    var startupDescription: String
}

struct Dashboard: Decodable {
    let total: String
    let status: String
    let tooltip: String
    let accounts: [AccountData]
    let consumptionUsd: Decimal?
    let isComplete: Bool
    let isLastKnown: Bool
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
}

struct DevicePrompt: Decodable {
    let code: String
    let verificationUri: String
    let expires: String
}

struct PendingIdentity: Decodable {
    let host: String
    let userId: Int64
    let login: String
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
    let prompt: DevicePrompt?
    let identity: PendingIdentity?
    let operation: String?
    let target: String?
    let tokens: Tokens?
    let enabled: Bool
    let title: String?
    let message: String?
    let key: String?
}

struct Receipt: Decodable { let error: String? }

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
