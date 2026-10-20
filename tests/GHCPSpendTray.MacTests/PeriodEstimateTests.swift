import AppKit
import Foundation
import SwiftUI

@MainActor
enum PeriodEstimateTests {
    static func run() throws {
        func check(_ condition: Bool, _ message: String) throws {
            if !condition { throw AppError.message(message) }
        }
        try check(estimatedMoney(nil) == "Unavailable", "Unavailable estimate must not be zero.")
        try check(estimatedMoney(0) == "~$0", "Observed zero has a numerical estimate.")
        try check(estimatedMoney(Decimal(string: "132.525")) == "~$133", "Forecast display rounds to whole USD.")
        try check(estimatedMoney(Decimal(string: "0.49")) == "<$1", "Small nonzero forecasts do not look like zero.")
        let preferences = try JSONDecoder().decode(AccountPreferences.self, from: Data("""
        {"displayName":"Synthetic","thresholds":"","showPeriodEstimate":true}
        """.utf8))
        try check(preferences.showPeriodEstimate, "Shared account preference decodes as opt-in.")

        let on = try account(estimate: [
            "estimatedConsumptionUsd": NSDecimalNumber(string: "132.525"),
            "averageDailyConsumptionUsd": NSDecimalNumber(string: "4.275"),
            "overAllocationUsd": NSDecimalNumber(string: "32.525"),
            "periodStartUtc": "2026-10-01T00:00:00.0000000+00:00",
            "resetAtUtc": "2026-11-01T00:00:00.0000000+00:00",
            "observedAtUtc": "2026-10-11T00:00:00.0000000+00:00", "isEarly": false
        ])
        let estimate = on.periodEstimate!
        try check(estimate.estimatedConsumptionUsd == Decimal(string: "132.525") &&
                  estimatedMoney(estimate.estimatedConsumptionUsd) == "~$133",
                  "Shared decimal results retain their cents before display rounding.")
        try check(estimate.summary.contains("About $33 over limit") && estimate.summary.hasSuffix("UTC"),
                  "Projected exceedance and UTC reset are labeled.")
        try check(utcDateText(estimate.resetAtUtc).contains("2026") &&
                  utcTimestampText(estimate.periodStartUtc).hasSuffix("UTC"),
                  "Estimate boundaries have explicit UTC labels.")
        let early = try account(estimate: [
            "estimatedConsumptionUsd": 15, "overAllocationUsd": NSDecimalNumber(string: "0.2"),
            "isEarly": true, "resetAtUtc": "2026-11-01T00:00:00Z"
        ]).periodEstimate!
        try check(early.summary.contains("Early estimate") && early.summary.contains("Less than $1 over limit"),
                  "Early projections and fractional projected exceedances are truthful.")
        let recent = try account(estimate: [
            "estimatedConsumptionUsd": 125, "averageDailyConsumptionUsd": 5, "overAllocationUsd": 25,
            "reportedDayCount": 7, "averageStartDate": "2026-10-04", "averageEndDate": "2026-10-10",
            "isEarly": false
        ]).periodEstimate!
        try check(recent.reportedDayCount == 7 && recent.summary.contains("Latest 7 reported days") &&
                  recent.method.contains("time remaining") && recent.averageStartDate == "2026-10-04" &&
                  recent.averageEndDate == "2026-10-10" && estimate.reportedDayCount == 0 &&
                  estimate.method.contains("Period-to-date"),
                  "Native forecasts distinguish rolling reported-day averages from the period-to-date fallback.")
        try check(recent.summary(customBudget: true).contains("About $25 over custom budget") &&
                  recent.summary.contains("About $25 over limit"),
                  "Reported-day forecasts label custom-budget comparisons without changing the forecast amount.")
        for (value, band) in [(94.99, ForecastBand.under), (95.0, .near), (99.99, .near), (100.0, .over), (150.0, .over)] {
            let fixture = try account(estimate: ["estimatedConsumptionUsd": value, "isEarly": false])
            try check(fixture.forecast?.band == band && fixture.forecast?.forecastFraction == min(1, value / 100),
                      "Native forecast track decodes threshold and clamped endpoint.")
            let narrow = NSHostingView(rootView: AccountRow(account: fixture).frame(width: 260))
            narrow.setFrameSize(narrow.fittingSize)
            narrow.layoutSubtreeIfNeeded()
            try check(narrow.fittingSize.width == 260 && narrow.fittingSize.height.isFinite,
                      "Projected callouts fit the compact native account width.")
        }

        let off = try account()
        let unavailable = try account(estimate: [
            "isEarly": false, "unavailableReason": "Billing period differs from a UTC calendar month."
        ])
        try check(off.periodEstimate == nil && unavailable.periodEstimate?.estimatedConsumptionUsd == nil &&
                  unavailable.periodEstimate?.unavailableReason != nil,
                  "Disabled forecasts are distinct from enabled-but-unavailable forecasts.")
        let offView = NSHostingView(rootView: AccountRow(account: off).frame(width: 352))
        let onView = NSHostingView(rootView: AccountRow(account: on).frame(width: 352))
        let unavailableView = NSHostingView(rootView: AccountRow(account: unavailable).frame(width: 352))
        let offSize = offView.fittingSize
        let onSize = onView.fittingSize
        let unavailableSize = unavailableView.fittingSize
        try check(offSize.width == 352 && onSize.width == 352 && unavailableSize.width == 352 &&
                  onSize.height > offSize.height && unavailableSize.height > offSize.height,
                  "Native account rows add forecast content only when enabled and fit the popup width.")
        for (name, view) in [("EstimateOn", onView), ("EstimateUnavailable", unavailableView)] {
            view.setFrameSize(view.fittingSize)
            view.layoutSubtreeIfNeeded()
            guard let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else {
                throw AppError.message("Forecast row did not render.")
            }
            view.cacheDisplay(in: view.bounds, to: bitmap)
            guard let png = bitmap.representation(using: .png, properties: [:]), png.count > 1000 else {
                throw AppError.message("Forecast row render was empty.")
            }
            try png.write(to: URL(fileURLWithPath: "artifacts/macos/\(name).png"))
        }
        print("PASS: Mac period-estimate preferences, decimal/UTC display, opt-in and unavailable native account rows.")
    }

    private static func account(estimate: [String: Any]? = nil) throws -> AccountData {
        var fields: [String: Any] = [
            "key": "github.com:1", "name": "Synthetic", "login": "fixture-user", "host": "github.com",
            "details": ["unlimited": false, "isCurrentPeriod": true],
            "freshness": "Fresh", "consumptionUsd": 42.75, "allocationUsd": 100, "percent": 42.75
        ]
        if let estimate {
            var value = estimate
            if value["reportedDayCount"] == nil { value["reportedDayCount"] = 0 }
            fields["periodEstimate"] = value
            if let amount = estimate["estimatedConsumptionUsd"] as? NSNumber, amount.doubleValue >= 42.75,
               estimate["unavailableReason"] == nil {
                fields["forecast"] = [
                    "actualFraction": 0.4275, "forecastFraction": min(1, amount.doubleValue / 100),
                    "percent": amount, "band": amount.doubleValue >= 100 ? 2 : amount.doubleValue >= 95 ? 1 : 0
                ]
            }
        }
        return try JSONDecoder().decode(AccountData.self, from: JSONSerialization.data(withJSONObject: fields))
    }
}
