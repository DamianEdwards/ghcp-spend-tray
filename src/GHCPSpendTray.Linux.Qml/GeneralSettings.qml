import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

ScrollView {
    id: view
    property var snapshot: null
    property var preview: null
    property bool busy: false
    property bool loaded: false
    property var excluded: []
    property string failure: ""
    signal request(string method, var arguments)
    clip: true
    contentWidth: availableWidth
    onSnapshotChanged: if (!loaded && snapshot && snapshot.settings) resetDraft()
    function resetDraft() {
        const settings = snapshot.settings
        loaded = false
        minutes.text = String(settings.pollMinutes)
        notifications.checked = settings.notifications
        thresholds.text = settings.thresholds
        increment.text = settings.spendIncrementUsd === null ? "" : String(settings.spendIncrementUsd)
        startup.checked = settings.startup
        style.currentIndex = settings.trayStyle === "Pie" ? 0 : 1
        mode.currentIndex = settings.trayMode === "RollUp" ? 0 : 1
        excluded = (settings.excludedTrayAccounts || []).slice()
        loaded = true
        previewTimer.restart()
    }
    function draft() {
        const poll = Number(minutes.text)
        const spend = increment.text.trim() === "" ? null : Number(increment.text)
        if (!Number.isInteger(poll) || poll < 5 || poll > 1440 ||
            (spend !== null && (!Number.isFinite(spend) || spend < 0))) {
            failure = "Refresh must be 5 to 1440 whole minutes. USD increments must be nonnegative."
            return null
        }
        failure = ""
        return {pollMinutes: poll, thresholds: thresholds.text, notifications: notifications.checked,
            startup: startup.checked, spendIncrementUsd: spend, trayStyle: style.currentIndex === 0 ? "Pie" : "Percentage",
            trayMode: mode.currentIndex === 0 ? "RollUp" : "PerAccount", excludedTrayAccounts: excluded}
    }
    function showPreview() {
        if (!loaded) return
        const settings = draft()
        if (settings) request("Preview", [JSON.stringify({kind: "preview", settings: settings})])
    }
    function open(uri) {
        if (uri && !Qt.openUrlExternally(uri)) failure = "The desktop could not open this location."
    }
    Timer { id: previewTimer; interval: 300; onTriggered: view.showPreview() }
    ColumnLayout {
        width: view.availableWidth
        spacing: 10
        Label { text: "General"; font.bold: true }
        Label { text: "Refresh interval (5 to 1440 minutes)" }
        TextField { id: minutes; objectName: "poll-minutes"; Layout.fillWidth: true; onTextEdited: previewTimer.restart() }
        CheckBox {
            id: startup
            text: "Start monitoring at login"
            enabled: view.snapshot !== null && view.snapshot.settings.canChangeStartup && !view.busy
        }
        Label {
            text: view.snapshot ? view.snapshot.settings.startupDescription : ""
            wrapMode: Text.Wrap; Layout.fillWidth: true
        }
        RowLayout {
            Button { text: "Open data folder"; enabled: view.snapshot !== null && !view.snapshot.demo; onClicked: view.open(view.snapshot.dataUri) }
            Button { text: "Login startup folder"; enabled: view.snapshot !== null && !view.snapshot.demo; onClicked: view.open(view.snapshot.startupUri) }
        }
        Label { text: "Notifications"; font.bold: true }
        CheckBox { id: notifications; objectName: "notifications-enabled"; text: "Enable consumption alerts" }
        TextField { id: thresholds; placeholderText: "Percentages, e.g. 50, 80, 100"; Layout.fillWidth: true }
        TextField { id: increment; placeholderText: "USD increment (blank or 0 disables)"; Layout.fillWidth: true }
        Button {
            text: "Send test notification"
            enabled: !view.busy && view.snapshot !== null && !view.snapshot.demo
            onClicked: view.request("Execute", [JSON.stringify({kind: "testNotification"})])
        }
        Label {
            text: "Each account has independent alerts. Desktop notification settings and Do Not Disturb can suppress delivery."
            wrapMode: Text.Wrap; Layout.fillWidth: true
        }
        Label { text: "Panel indicators"; font.bold: true }
        ComboBox {
            id: style; objectName: "tray-style"; model: ["Pie chart", "Percentage number"]; Layout.fillWidth: true
            onActivated: previewTimer.restart()
        }
        ComboBox {
            id: mode; objectName: "tray-mode"; model: ["One weighted roll-up", "One icon per selected account"]; Layout.fillWidth: true
            onActivated: previewTimer.restart()
        }
        Repeater {
            model: view.snapshot ? view.snapshot.accounts : []
            delegate: CheckBox {
                required property var modelData
                text: modelData.name + " (" + modelData.host + ")"
                checked: view.excluded.indexOf(modelData.key) < 0
                onToggled: {
                    let values = view.excluded.filter(key => key !== modelData.key)
                    if (!checked) values.push(modelData.key)
                    view.excluded = values
                    previewTimer.restart()
                }
            }
        }
        Label { text: "Live draft preview"; font.bold: true }
        Repeater {
            model: view.preview ? view.preview.icons : []
            delegate: RowLayout {
                required property var modelData
                Image { source: modelData.imageUri; sourceSize.width: 32; sourceSize.height: 32; Layout.preferredWidth: 32; Layout.preferredHeight: 32 }
                Label { text: modelData.tooltip; textFormat: Text.PlainText; wrapMode: Text.Wrap; Layout.fillWidth: true }
            }
        }
        Label {
            text: "! means partial or over allocation; ? means unavailable. Only fresh, current-period, selected accounts contribute to allocation indicators."
            wrapMode: Text.Wrap; Layout.fillWidth: true
        }
        RowLayout {
            Button {
                objectName: "save-settings"
                text: "Save settings"; enabled: view.loaded && !view.busy
                onClicked: {
                    const settings = view.draft()
                    if (settings) view.request("Execute", [JSON.stringify({kind: "settings", settings: settings})])
                }
            }
            Button { text: "Discard draft"; enabled: view.loaded && !view.busy; onClicked: view.resetDraft() }
        }
        Label { text: view.failure; textFormat: Text.PlainText; wrapMode: Text.Wrap; Layout.fillWidth: true }
        Label { text: "About GHCPSpendTray"; font.bold: true }
        Label {
            text: "Independent Copilot AI-credit monitoring. Consumption is credits divided by 100, not an invoice. History is locally sampled, not a verified daily breakdown. Linux development build; verify the distribution signature before installing an update."
            wrapMode: Text.Wrap; Layout.fillWidth: true
        }
        Button { text: "Project and releases"; onClicked: view.open("https://github.com/DamianEdwards/ghcp-spend-tray/releases") }
        Button { text: "Privacy policy"; onClicked: view.open("https://github.com/DamianEdwards/ghcp-spend-tray/blob/main/PRIVACY.md") }
        Button { text: "Report an issue"; onClicked: view.open("https://github.com/DamianEdwards/ghcp-spend-tray/issues") }
        Button { text: "Quit monitoring"; enabled: !view.busy; onClicked: view.request("Quit", []) }
    }
}
