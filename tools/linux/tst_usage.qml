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

    function sample() {
        return {
            version: 2, demo: true, revision: 1, style: "Percentage", percent: 105,
            indicator: "105%", consumption: "$26.25", status: "Demo", updatedAt: null,
            accounts: [{
                key: "personal", name: "Personal", login: "synthetic", host: "github.com",
                percent: 105, consumption: "$26.25", allocation: "$25.00",
                freshness: "Current", updatedAt: null
            }]
        }
    }

    function cleanup() { usage.snapshot = null }

    function test_parser() {
        const value = sample()
        compare(Snapshot.parseSnapshot(JSON.stringify(value)).percent, 105)
        value.demo = false
        let rejected = false
        try { Snapshot.parseSnapshot(JSON.stringify(value)) } catch (error) { rejected = true }
        verify(rejected)
        value.demo = true
        value.accounts.push(value.accounts[0])
        rejected = false
        try { Snapshot.parseSnapshot(JSON.stringify(value)) } catch (error) { rejected = true }
        verify(rejected)
    }

    function test_usage_and_details() {
        usage.snapshot = Snapshot.parseSnapshot(JSON.stringify(sample()))
        tryCompare(findChild(usage, "consumption"), "text", "$26.25")
        const card = findChild(usage, "account-personal")
        verify(card !== null)
        verify(card.height > 100)
        verify(card.width <= usage.width)
        compare(findChild(card, "allocation-progress").value, 100)
        verify(findChild(card, "allocation-label").text.indexOf("105%") >= 0)
        const details = findChild(card, "account-details")
        compare(details.visible, false)
        mouseClick(findChild(card, "details-button"))
        tryCompare(details, "visible", true)
        verify(details.text.indexOf("synthetic @ github.com") >= 0)
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
