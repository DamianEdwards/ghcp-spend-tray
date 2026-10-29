import AppKit
import Foundation
import SwiftUI

@MainActor
enum DailyConsumptionTests {
    static func run() throws {
        let bridge = FixtureBridge()
        let model = AppModel(directory: URL(fileURLWithPath: "/synthetic-daily-unused"), demo: true, bridge: bridge)
        defer { model.shutdown() }
        model.initialized = true
        for state in [EnterpriseSetupState.ready, .cliMissing, .signInRequired, .confirmIdentity, .connected, .error] {
            let account = try fixture(state: state)
            try check(account.enterpriseConsumption?.state == state, "Every shared setup state decodes natively.")
            if state == .confirmIdentity {
                try check(account.enterpriseConsumption?.pendingIdentity?.username == "work@example.invalid",
                          "Confirmation retains the recognizable work identity.")
            }
            let host = NSHostingView(rootView: AccountEditor(model: model, account: account).frame(width: 650, height: 900))
            try snapshot(host, name: "CorporateSetup\(state.rawValue)")
        }
        for confirm in [false, true] {
            model.perform("consumption.connect", fields: ["key": "github.com:1", "confirm": confirm])
            try check(bridge.lastRequest["method"] as? String == "consumption.connect" &&
                      bridge.lastRequest["key"] as? String == "github.com:1" &&
                      bridge.lastRequest["confirm"] as? Bool == confirm && model.busy,
                      "Setup preserves the per-account command and explicit identity confirmation.")
            try bridge.enqueue("completed", id: bridge.lastRequest["id"])
            model.poll()
            try check(!model.busy && model.error == nil, "Setup completion releases the native busy state.")
        }

        let previousZone = NSTimeZone.default
        NSTimeZone.default = TimeZone(identifier: "America/Los_Angeles")!
        defer { NSTimeZone.default = previousZone }
        let formatter = DateFormatter()
        formatter.locale = .current
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateStyle = .short
        let firstDay = formatter.string(from: dateValue("2026-10-01T00:00:00Z")!)
        try check(dailyDateText("2026-10-01") == firstDay, "Regional daily dates do not shift to the previous local day.")
        let account = try fixture(bars: [
            ["date": "2026-10-01", "amountUsd": 1.25, "estimated": false, "derived": false],
            ["date": "2026-10-02", "amountUsd": 2.5, "estimated": true, "derived": false]
        ])
        let bars = account.enterpriseConsumption!.bars!
        try check(bars[0].tooltip == "\(firstDay): $1.25 USD - Reported" && bars[1].kind == "Estimated",
                  "Tooltips retain exact USD, regional UTC dates and reported/estimated labels.")
        for compact in [false, true] {
            for (name, appearance) in [("Light", NSAppearance.Name.aqua), ("Dark", .darkAqua)] {
                let width: CGFloat = compact ? 352 : 650
                let host = NSHostingView(rootView: DailyConsumptionChartView(account: account, compact: compact)
                    .accentColor(.blue).frame(width: width).padding(12).background(Color(nsColor: .windowBackgroundColor)))
                host.appearance = NSAppearance(named: appearance)
                try check(host.fittingSize.width == width + 24 && host.fittingSize.height > 100,
                          "Daily charts fit their native popup and Usage widths.")
                let bitmap = try snapshot(host, name: (compact ? "CompactDailyConsumption" : "DailyConsumption") + name)
                try checkChart(bitmap, width: width, compact: compact)
            }
        }
        let derived = try fixture(bars: [
            ["date": "2026-10-01", "amountUsd": 1.25, "estimated": false, "derived": true]
        ], message: "Today's consumption is the current usage less all reported prior days.")
        try check(derived.enterpriseConsumption?.bars?.first?.kind == "Today - from current usage",
                  "Today's uniquely derived remainder is distinct from estimates.")
        try snapshot(NSHostingView(rootView: DailyConsumptionChartView(account: derived)
            .frame(width: 650).padding(12).background(Color(nsColor: .windowBackgroundColor))),
                     name: "DerivedDailyConsumption")
        for loading in [false, true] {
            let status = try fixture(message: "No daily reports are available.", loading: loading)
            try check(status.enterpriseConsumption?.isLoading == loading && status.enterpriseConsumption?.bars?.isEmpty == true,
                      "Connected-but-loading and connected-without-reports remain distinct from zero usage.")
            let host = NSHostingView(rootView: DailyConsumptionChartView(account: status)
                .frame(width: 650).padding(12).background(Color(nsColor: .windowBackgroundColor)))
            try check(host.fittingSize.height > 30, "Connected accounts without bars render an explanatory status.")
            try snapshot(host, name: loading ? "DailyConsumptionLoading" : "DailyConsumptionUnavailable")
        }
        let hidden = try fixture(eligible: false)
        let hiddenChart = NSHostingView(rootView: DailyConsumptionChartView(account: hidden).frame(width: 352))
        try check(hidden.enterpriseConsumption == nil && hiddenChart.fittingSize.height == 0,
                  "Unconfigured accounts retain no corporate chart or invented zero.")
        print("PASS: Mac corporate setup states/bridge commands, chart rendering, regional UTC tooltips and loading/no-data states.")
    }

    private static func fixture(state: EnterpriseSetupState = .connected, bars: [[String: Any]] = [],
                                message: String? = nil, loading: Bool = false, eligible: Bool = true) throws -> AccountData {
        var fields: [String: Any] = [
            "key": "github.com:1", "name": "Synthetic", "login": "fixture", "host": "github.com",
            "details": ["unlimited": false, "isCurrentPeriod": true], "freshness": "Fresh",
            "consumptionUsd": 3.75, "allocationUsd": 100, "percent": 3.75
        ]
        if eligible {
            var enterprise: [String: Any] = [
                "eligible": true, "message": "Synthetic corporate state", "state": state.rawValue,
                "connected": state == .connected, "connectedUsername": "work@example.invalid",
                "bars": bars, "axisTicks": [0, 1.25, 2.5], "isLoading": loading
            ]
            if let message { enterprise["chartMessage"] = message }
            if state == .confirmIdentity {
                enterprise["pendingIdentity"] = [
                    "alias": "work", "tenantId": "11111111-1111-1111-1111-111111111111",
                    "objectId": "22222222-2222-2222-2222-222222222222", "username": "work@example.invalid"
                ]
            }
            fields["enterpriseConsumption"] = enterprise
        }
        return try JSONDecoder().decode(AccountData.self, from: JSONSerialization.data(withJSONObject: fields))
    }

    private static func check(_ condition: Bool, _ message: String) throws {
        if !condition { throw AppError.message(message) }
    }

    private static func checkChart(_ bitmap: NSBitmapImageRep, width: CGFloat, compact: Bool) throws {
        let scale = CGFloat(bitmap.pixelsWide) / (width + 24)
        let plotLeft: CGFloat = 12 + (compact ? 35 : 55) + 6
        let plotWidth = width - (compact ? 35 : 55) - 6
        let gap: CGFloat = compact ? 2 : 4
        let firstX = Int((plotLeft + (plotWidth - gap) / 4) * scale)
        let secondX = Int((plotLeft + plotWidth * 0.75 + gap / 4) * scale)
        let gridX = Int((plotLeft + plotWidth / 2) * scale)
        func components(_ x: Int, _ y: Int) throws -> [CGFloat] {
            guard let color = bitmap.colorAt(x: x, y: y)?.usingColorSpace(.deviceRGB) else {
                throw AppError.message("Chart bitmap has no RGB color.")
            }
            return [color.redComponent, color.greenComponent, color.blueComponent]
        }
        func chroma(_ x: Int, _ y: Int) throws -> CGFloat {
            let values = try components(x, y)
            return values.max()! - values.min()!
        }
        let reported = try (0..<bitmap.pixelsHigh).filter { try chroma(firstX, $0) > 0.05 }
        let estimated = try (0..<bitmap.pixelsHigh).filter { try chroma(secondX, $0) > 0.05 }
        guard let reportedBottom = reported.last, let estimatedTop = estimated.first else {
            throw AppError.message("Both chart columns must render accent-colored content.")
        }
        let background = try components(0, 0)[0]
        let grid = try (0..<min(bitmap.pixelsHigh, reportedBottom + Int(scale * 2))).filter { y in
            let values = try components(gridX, y)
            return values.max()! - values.min()! < 0.01 && abs(values[0] - background) > 0.03
        }
        guard let gridTop = grid.first, let gridBottom = grid.last else {
            throw AppError.message("USD gridlines must render between chart columns.")
        }
        try check(abs(CGFloat(reportedBottom - gridBottom)) <= scale &&
                  abs(CGFloat(estimatedTop - gridTop)) <= scale,
                  "Reported baselines and maximum-height estimates align with the USD grid within one point.")
        let middleY = (reported.first! + reportedBottom) / 2
        try check(try chroma(secondX, middleY) < chroma(firstX, middleY) / 2,
                  "Estimated columns have a translucent fill, not the solid reported fill.")
        let edgeStart = Int((plotLeft + (plotWidth + gap) / 2 + 4) * scale)
        let edgeEnd = Int((plotLeft + plotWidth - 4) * scale)
        let edge = try (edgeStart..<edgeEnd).map { try chroma($0, estimatedTop + 1) }
        try check(edge.max()! - edge.min()! > 0.2, "Estimated outlines alternate between accent strokes and dash gaps.")
    }

    @discardableResult
    private static func snapshot(_ view: NSView, name: String) throws -> NSBitmapImageRep {
        let window = NSWindow(contentRect: NSRect(x: -10000, y: -10000, width: 650, height: 900),
                              styleMask: .borderless, backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        defer { window.close() }
        window.contentView = view
        window.setContentSize(view.fittingSize)
        window.orderBack(nil)
        view.layoutSubtreeIfNeeded()
        guard let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else {
            throw AppError.message("Daily consumption view did not render.")
        }
        view.cacheDisplay(in: view.bounds, to: bitmap)
        guard let png = bitmap.representation(using: .png, properties: [:]), png.count > 1000 else {
            throw AppError.message("Daily consumption render was empty.")
        }
        try png.write(to: URL(fileURLWithPath: "artifacts/macos/\(name).png"))
        return bitmap
    }
}
