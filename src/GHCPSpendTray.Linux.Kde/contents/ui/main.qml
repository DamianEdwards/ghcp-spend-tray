import QtQuick
import QtQuick.Controls as Controls
import QtQuick.Layouts
import org.kde.plasma.plasmoid
import org.kde.plasma.workspace.dbus as DBus
import "snapshot.js" as Snapshot
import "components"

PlasmoidItem {
    id: root
    property var snapshot: null
    property string failure: ""
    property bool busy: false
    property bool settingsOpen: false
    property int requestId: 0
    Plasmoid.icon: "utilities-system-monitor"
    toolTipMainText: "GHCPSpendTray (synthetic demo)"
    toolTipSubText: snapshot ? snapshot.consumption : "Unavailable"
    preferredRepresentation: compactRepresentation

    function receive(json) {
        try {
            snapshot = Snapshot.parseSnapshot(json)
            failure = ""
        } catch (error) {
            snapshot = null
            failure = String(error)
            console.error(failure)
        }
    }

    function call(method, args) {
        if (busy)
            return
        busy = true
        const currentRequest = ++requestId
        deadline.restart()
        DBus.SessionBus.asyncCall({
            service: "io.github.ghcpspendtray.LinuxDemo",
            path: "/io/github/ghcpspendtray/LinuxDemo",
            iface: "io.github.ghcpspendtray.LinuxDemo2",
            member: method,
            arguments: args || []
        }, reply => {
            if (currentRequest !== requestId)
                return
            deadline.stop()
            busy = false
            if (method === "GetSnapshot")
                receive(reply.value)
            else
                call("GetSnapshot", [])
        }, reply => {
            if (currentRequest !== requestId)
                return
            deadline.stop()
            busy = false
            snapshot = null
            failure = "Helper unavailable: " + reply.error.message
            console.error(failure)
        })
    }

    Component.onCompleted: call("GetSnapshot", [])
    onExpandedChanged: if (expanded) call("GetSnapshot", [])
    Timer {
        id: deadline
        interval: 15000
        onTriggered: {
            root.requestId++
            root.busy = false
            root.snapshot = null
            root.failure = "Helper response timed out. Try Refresh."
            console.error(root.failure)
        }
    }
    Timer {
        interval: 10000
        running: true
        repeat: true
        onTriggered: root.call("GetSnapshot", [])
    }

    compactRepresentation: Controls.ToolButton {
        text: root.snapshot ? (Plasmoid.configuration.showConsumption ?
            root.snapshot.consumption : root.snapshot.indicator) : "?"
        Accessible.name: root.toolTipMainText + ": " + root.toolTipSubText
        onClicked: root.expanded = !root.expanded
    }

    fullRepresentation: ColumnLayout {
        Layout.minimumWidth: 360
        Layout.preferredWidth: 400
        Layout.minimumHeight: 380
        Layout.preferredHeight: 580
        RowLayout {
            Layout.fillWidth: true
            Controls.Label {
                text: "GHCPSpendTray"
                font.bold: true
                font.pixelSize: 20
                Layout.fillWidth: true
            }
            Controls.Button {
                text: root.settingsOpen ? "Back" : "Settings"
                onClicked: root.settingsOpen = !root.settingsOpen
            }
        }
        Controls.Label {
            visible: root.failure.length > 0
            text: root.failure
            wrapMode: Text.Wrap
            Layout.fillWidth: true
        }
        UsageView {
            visible: !root.settingsOpen
            snapshot: root.snapshot
            Layout.fillWidth: true
            Layout.fillHeight: true
        }
        ColumnLayout {
            visible: root.settingsOpen
            Layout.fillWidth: true
            Layout.fillHeight: true
            Controls.Label { text: "Synthetic accounts only. Authentication is disabled."; wrapMode: Text.Wrap; Layout.fillWidth: true }
            Controls.CheckBox {
                text: "Show consumption amount in the panel"
                checked: Plasmoid.configuration.showConsumption
                onToggled: Plasmoid.configuration.showConsumption = checked
            }
            Controls.Button {
                text: "Add demo account"
                enabled: !root.busy && root.snapshot !== null && root.snapshot.accounts.length < 100
                onClicked: root.call("AddDemoAccount", [])
            }
            Item { Layout.fillHeight: true }
        }
        RowLayout {
            Controls.Label { text: "DEMO"; Layout.fillWidth: true }
            Controls.Button { text: "Refresh"; enabled: !root.busy; onClicked: root.call("Refresh", []) }
            Controls.Button { text: "Close"; onClicked: root.expanded = false }
        }
    }
}
