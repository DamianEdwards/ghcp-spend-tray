import Foundation
import Carbon

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
    var trackedTelemetryEnabled: Bool = true
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
    let enterpriseConsumption: EnterpriseConsumptionData?
    let forecast: UsageForecastData?
    let actualUsage: UsagePositionData?
    var customBudgetUsd: Decimal? = nil

    var targetLabel: String { customBudgetUsd == nil ? "API limit" : "custom budget" }
    var allocationSummary: String {
        if let percent {
            return "\(decimalText(percent))% of \(money(customBudgetUsd ?? allocationUsd)) \(targetLabel)"
        }

        struct UsageForecastData: Decodable {
            let actualFraction: Double
            let forecastFraction: Double
            let percent: Decimal
            let band: ForecastBand
        }

        struct UsagePositionData: Decodable {
            let fraction: Double
            let percent: Decimal
            let band: ForecastBand
        }

        enum ForecastBand: Int, Decodable {
            case under, near, over
        }
        if let customBudgetUsd { return "\(money(customBudgetUsd)) custom budget · Consumption unavailable" }
        return details.unlimited ? "Unlimited API limit" : "API limit unavailable"
    }
}

struct EnterpriseConsumptionData: Decodable {
    let eligible: Bool
    let message: String
    let pendingIdentity: CorporateIdentityData?
    let connected: Bool
    let bars: [DailySpendData]?
    let chartMessage: String?
    let state: EnterpriseSetupState
    let connectedUsername: String?
    let axisTicks: [Decimal]?
    let isLoading: Bool
}
enum EnterpriseSetupState: Int, Decodable {
    case ready, cliMissing, signInRequired, confirmIdentity, connected, error
}
struct CorporateIdentityData: Decodable {
    let alias: String
    let tenantId: String
    let objectId: String
    let username: String?
}
struct DailySpendData: Decodable, Identifiable {
    var id: String { date }
    let date: String
    let amountUsd: Decimal
    let estimated: Bool
    let derived: Bool

    var kind: String { estimated ? "Estimated" : derived ? "Today - from current usage" : "Reported" }
    var tooltip: String { "\(dailyDateText(date)): \(money(amountUsd)) USD - \(kind)" }
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
    let averageStartDate: String?
    let averageEndDate: String?
    let reportedDayCount: Int

    var method: String {
        reportedDayCount > 0
            ? "Current usage plus the average of the latest \(reportedDayCount) complete reported UTC days multiplied by the time remaining until reset. Excludes today's partial usage and estimated bars. Reports may lag by up to 3 days; not an invoice."
            : "Period-to-date usage divided by exact elapsed UTC time, scaled to the whole UTC calendar month. Uses the source observation time, or fetch time when absent; not an invoice."
    }

    var summary: String {
        summary(customBudget: false)
    }

    func summary(customBudget: Bool) -> String {
        var parts: [String] = []
        if isEarly { parts.append("Early estimate") }
        if reportedDayCount > 0 { parts.append("Latest \(reportedDayCount) reported days") }
        if let over = overAllocationUsd, over > 0 {
            let target = customBudget ? "custom budget" : "limit"
            parts.append(over < 1 ? "Less than $1 over \(target)" : "About \(wholeMoney(over)) over \(target)")
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
    var customBudgetUsd: Decimal? = nil
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

struct Receipt: Decodable {
    var error: String? = nil
    var fieldErrors: [String: String]? = nil
    var incrementUsd: Decimal? = nil
    var customBudgetUsd: Decimal? = nil
    var pollMinutes: Int? = nil
    var text: String? = nil
}

struct AccountDraft: Equatable {
    var name = ""
    var thresholds = ""
    var increment = ""
    var inheritIncrement = true
    var showPeriodEstimate = false
    var useCustomBudget = false
    var budget = ""

    init() {}
    init(_ preferences: AccountPreferences) {
        name = preferences.displayName
        thresholds = preferences.thresholds
        increment = decimalText(preferences.spendIncrementUsd)
        inheritIncrement = preferences.spendIncrementUsd == nil
        showPeriodEstimate = preferences.showPeriodEstimate
        useCustomBudget = preferences.customBudgetUsd != nil
        budget = decimalText(preferences.customBudgetUsd)
    }

    var activeValues: Self {
        var value = self
        if inheritIncrement { value.increment = "" }
        if !useCustomBudget { value.budget = "" }
        return value
    }
    var form: [String: Any] {
        ["page": "account", "displayName": name, "thresholds": thresholds, "increment": increment,
         "inheritIncrement": inheritIncrement, "useCustomBudget": useCustomBudget, "customBudget": budget]
    }
}

struct GlobalDraft: Equatable {
    var minutes = ""
    var thresholds = ""
    var increment = ""
    var notifications = true
    var startup = false
    var trackedTelemetryEnabled = true
    var trayStyle: TrayIconStyle = .pie
    var trayMode: TrayDisplayMode = .rollUp
    var excludedAccounts = Set<String>()

    init() {}
    init(_ settings: SettingsData) {
        minutes = String(settings.pollMinutes)
        thresholds = settings.thresholds
        increment = decimalText(settings.spendIncrementUsd)
        notifications = settings.notifications
        startup = settings.startup
        trackedTelemetryEnabled = settings.trackedTelemetryEnabled
        trayStyle = settings.trayStyle
        trayMode = settings.trayMode
        excludedAccounts = Set(settings.excludedTrayAccounts ?? [])
    }
}

enum UnsavedChangesChoice { case save, discard, keepEditing }

func isSystemTermination(_ reason: UInt32) -> Bool {
    [UInt32(kAEQuitAll), UInt32(kAEShutDown), UInt32(kAERestart), UInt32(kAEReallyLogOut)].contains(reason)
}

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

func dailyDateText(_ text: String, month: Bool = false) -> String {
    let parser = DateFormatter()
    parser.locale = Locale(identifier: "en_US_POSIX")
    parser.timeZone = TimeZone(secondsFromGMT: 0)
    parser.dateFormat = "yyyy-MM-dd"
    guard let date = parser.date(from: text) else { return text }
    let formatter = DateFormatter()
    formatter.locale = .current
    formatter.timeZone = TimeZone(secondsFromGMT: 0)
    if month { formatter.setLocalizedDateFormatFromTemplate("MMMM yyyy") }
    else { formatter.dateStyle = .short }
    return formatter.string(from: date)
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
