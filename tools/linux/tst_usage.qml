import QtQuick
import QtTest
import "components"
import "snapshot.js" as Snapshot

TestCase {
    name: "NativeUsage"
    width: 400
    height: 580
    visible: true
    when: windowShown

    UsageView { id: usage; anchors.fill: parent }
    AccountManager { id: manager; anchors.fill: parent; visible: false }
    GeneralSettings { id: general; anchors.fill: parent; visible: false }
    SignalSpy { id: settingsRequests; target: general; signalName: "request" }
    SignalSpy { id: requests; target: manager; signalName: "request" }

    function sample() {
        return {
            version: 4, demo: true, revision: 1, style: "Percentage", percent: 105,
            indicator: "105%", consumption: "$26.25", status: "Demo", updatedAt: null,
            settings: {pollMinutes: 5, thresholds: "50, 80", notifications: true, startup: false,
                spendIncrementUsd: null, canChangeStartup: false, startupDescription: "Synthetic",
                trayStyle: "Percentage", trayMode: "RollUp", excludedTrayAccounts: [],
                trackedTelemetryEnabled: true},
            tray: {rollUp: {details: "105% of allocation"}},
            icons: [{key: null, name: "Roll-up", tooltip: "105%", imageUri: "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a4xkAAAAASUVORK5CYII="}],
            accounts: [{
                key: "personal", name: "Personal", login: "synthetic", host: "github.com",
                percent: 105, consumption: "$26.25", allocation: "$25",
                consumptionUsd: 26.25, apiLimitUsd: 25, apiLimit: "$25",
                freshness: "Current", updatedAt: null, displayName: "Personal", thresholds: "", spendIncrementUsd: null,
                showPeriodEstimate: true, clientId: null, message: null, customBudgetUsd: null,
                trackingTarget: "$25", targetLabel: "API limit", usageCaption: "105% used",
                estimateAmount: "$30", estimateCaption: "Resets Dec 1, 2026 UTC",
                actualUsage: {fraction: 1, percent: 105, band: "Over"},
                forecast: {actualFraction: 1, forecastFraction: 1, percent: 120, band: "Over"},
                periodEstimate: {estimatedConsumptionUsd: 30, averageDailyConsumptionUsd: 1,
                    isEarly: false, unavailableReason: null}
            }]
        }
    }

    function cleanup() {
        usage.snapshot = null
        usage.expandedKeys = []
        usage.visible = true
        manager.snapshot = null
        manager.signIn = null
        manager.visible = false
        requests.clear()
        general.visible = false
        general.loaded = false
        general.snapshot = null
        settingsRequests.clear()
    }

    function test_account_management() {
        usage.visible = false
        manager.visible = true
        const value = sample()
        value.demo = false
        manager.snapshot = value
        const signin = findChild(manager, "signin-button")
        verify(signin.enabled)
        signin.clicked()
        compare(requests.count, 1)
        compare(requests.signalArguments[0][0], "Execute")
        compare(JSON.parse(requests.signalArguments[0][1][0]).host, "github.com")
        manager.signIn = Snapshot.parseSignIn(JSON.stringify({
            phase: "waiting", code: "SYNTHETIC", verificationUri: "https://github.com/login/device",
            expires: "2099-01-01T00:00:00Z", message: null
        }))
        verify(!signin.enabled)
        compare(findChild(manager, "device-code").text, "SYNTHETIC")
        manager.signIn = {phase: "complete", message: "Account connected."}
        compare(findChild(manager, "device-code").text, "")
        manager.selectAccount(value.accounts[0])
        findChild(manager, "account-name").text = "Renamed"
        findChild(manager, "budget-mode").currentIndex = 1
        findChild(manager, "custom-budget").text = "50"
        compare(findChild(manager, "budget-preview").text,
            "Preview: $26.25 consumed = 52.5% of $50 custom budget")
        findChild(manager, "save-account").clicked()
        compare(JSON.parse(requests.signalArguments[1][1][0]).displayName, "Renamed")
        compare(JSON.parse(requests.signalArguments[1][1][0]).spendIncrementUsd, null)
        compare(JSON.parse(requests.signalArguments[1][1][0]).customBudgetUsd, 50)
        compare(JSON.parse(requests.signalArguments[1][1][0]).updateCustomBudget, true)
        manager.snapshot = {accounts: [], demo: false}
        compare(manager.selectedKey, "")
    }

    function test_signin_parser() {
        for (const uri of ["file:///etc/passwd", "http://github.com/login/device", "https://user@github.com/"]) {
            let rejected = false
            try {
                Snapshot.parseSignIn(JSON.stringify({
                    phase: "waiting", code: "SYNTHETIC", verificationUri: uri,
                    expires: "2099-01-01T00:00:00Z", message: null
                }))
            } catch (error) { rejected = true }
            verify(rejected)
        }
    }

    function test_parser() {
        compare(Snapshot.amount(1500), "$1,500")
        compare(Snapshot.amount(1500.5), "$1,500.50")
        compare(Snapshot.amount(1500.05), "$1,500.05")
        const value = sample()
        compare(Snapshot.parseSnapshot(JSON.stringify(value)).percent, 105)
        value.demo = false
        compare(Snapshot.parseSnapshot(JSON.stringify(value)).demo, false)
        value.version = 2
        let rejected = false
        try { Snapshot.parseSnapshot(JSON.stringify(value)) } catch (error) { rejected = true }
        verify(rejected)
        value.version = 4
        value.demo = true
        value.accounts[0].forecast.forecastFraction = 0.5
        rejected = false
        try { Snapshot.parseSnapshot(JSON.stringify(value)) } catch (error) { rejected = true }
        verify(rejected)
        value.accounts[0].forecast.forecastFraction = 1
        value.accounts.push(value.accounts[0])
        rejected = false
        try { Snapshot.parseSnapshot(JSON.stringify(value)) } catch (error) { rejected = true }
        verify(rejected)
    }

    function test_general_settings() {
        usage.visible = false
        general.visible = true
        general.snapshot = sample()
        findChild(general, "poll-minutes").text = "1"
        findChild(general, "save-settings").clicked()
        compare(settingsRequests.count, 0)
        verify(general.failure.length > 0)
        findChild(general, "poll-minutes").text = "15"
        findChild(general, "tray-mode").currentIndex = 1
        findChild(general, "save-settings").clicked()
        compare(settingsRequests.count, 1)
        const request = JSON.parse(settingsRequests.signalArguments[0][1][0])
        compare(request.settings.pollMinutes, 15)
        compare(request.settings.trayMode, "PerAccount")
        compare(request.settings.trackedTelemetryEnabled, true)
        general.snapshot = sample()
        compare(findChild(general, "poll-minutes").text, "15")
        general.showPreview()
        compare(settingsRequests.signalArguments[1][0], "Preview")
    }

    function test_usage_and_details() {
        usage.snapshot = Snapshot.parseSnapshot(JSON.stringify(sample()))
        tryCompare(findChild(usage, "consumption"), "text", "$26.25")
        const card = findChild(usage, "account-personal")
        verify(card !== null)
        verify(card.height > 100)
        verify(card.width <= usage.width, "card " + card.width + " usage " + usage.width)
        verify(findChild(card, "forecast-track").visible)
        compare(findChild(card, "current-amount").text, "$26.25")
        compare(findChild(card, "current-caption").text, "used")
        compare(findChild(card, "target-amount").text, "$25")
        compare(findChild(card, "target-caption").text, "API limit")
        compare(findChild(card, "forecast-amount").text, "$30")
        compare(findChild(card, "forecast-caption").text, "est. total")
        compare(findChild(card, "allocation-label").text, "105% used")
        compare(findChild(card, "forecast-label").text, "Resets Dec 1, 2026 UTC · At or over target")
        const details = findChild(card, "account-details")
        compare(details.visible, false)
        mouseClick(findChild(card, "details-button"))
        tryCompare(details, "visible", true)
        verify(details.text.indexOf("synthetic @ github.com") >= 0)
        const near = sample()
        near.accounts[0].forecast.band = "Near"
        near.accounts[0].forecast.percent = 97
        usage.snapshot = Snapshot.parseSnapshot(JSON.stringify(near))
        verify(findChild(usage, "forecast-label").text.indexOf("Near target") >= 0)
        const under = sample()
        under.accounts[0].forecast.band = "Under"
        under.accounts[0].forecast.percent = 80
        usage.snapshot = Snapshot.parseSnapshot(JSON.stringify(under))
        verify(findChild(usage, "forecast-label").text.indexOf("Under target") >= 0)
    }

    function test_unavailable_and_empty() {
        compare(findChild(usage, "consumption").text, "Unavailable")
        const value = sample()
        value.consumption = "Unavailable"
        value.percent = null
        value.accounts = []
        usage.snapshot = Snapshot.parseSnapshot(JSON.stringify(value))
        compare(findChild(usage, "consumption").text, "Unavailable")
        tryCompare(findChild(usage, "empty-hint"), "visible", true)
    }
}
