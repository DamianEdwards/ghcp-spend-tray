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
    property var signIn: null
    property var preview: null
    property var queuedPreview: null
    property string selectedKey: ""
    property bool stopped: false
    property string failure: ""
    property bool busy: false
    property bool settingsOpen: false
    property int requestId: 0
    Plasmoid.icon: "utilities-system-monitor"
    toolTipMainText: "GHCPSpendTray"
    toolTipSubText: snapshot ? snapshot.consumption : "Unavailable"
    preferredRepresentation: compactRepresentation

    function receive(json) {
        try {
            snapshot = Snapshot.parseSnapshot(json)
            if (selectedKey && !snapshot.accounts.some(account => account.key === selectedKey)) selectedKey = ""
            failure = ""
        } catch (error) {
            snapshot = null
            failure = String(error)
            console.error(failure)
        }
    }

    function call(method, args) {
        if (busy) {
            if (method === "Preview") queuedPreview = args
            return
        }
        if (stopped && method !== "Refresh") return
        if (method === "Refresh") stopped = false
        busy = true
        const currentRequest = ++requestId
        deadline.restart()
        DBus.SessionBus.asyncCall({
            service: "io.github.ghcpspendtray.LinuxDemo",
            path: "/io/github/ghcpspendtray/LinuxDemo",
            iface: "io.github.ghcpspendtray.LinuxDemo4",
            member: method,
            arguments: args || []
        }, reply => {
            if (currentRequest !== requestId)
                return
            deadline.stop()
            busy = false
            if (method === "GetSignIn") {
                try { signIn = Snapshot.parseSignIn(reply.value) }
                catch (error) { failure = "Invalid sign-in response from the helper." }
            } else if (method === "Preview") {
                try { preview = Snapshot.parsePreview(reply.value) }
                catch (error) { failure = "Invalid tray preview: " + error }
            } else if (method === "Quit") {
                stopped = true
                snapshot = null
                failure = "Monitoring stopped. Refresh to start again."
            } else if (method === "GetSnapshot")
                receive(reply.value)
            else
                call("GetSnapshot", [])
            if (queuedPreview && !busy && !stopped) {
                const args = queuedPreview
                queuedPreview = null
                call("Preview", args)
            }
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
        interval: 135000
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
    Timer {
        interval: 1000
        running: root.expanded && root.settingsOpen
        repeat: true
        onTriggered: root.call("GetSignIn", [])
    }

    compactRepresentation: RowLayout {
        spacing: 0
        Repeater {
            model: root.snapshot ? root.snapshot.icons : [{key: null, name: "GHCPSpendTray", tooltip: "Unavailable", imageUri: ""}]
            delegate: Controls.ToolButton {
                required property var modelData
                contentItem: Image {
                    source: modelData.imageUri
                    sourceSize.width: 32; sourceSize.height: 32
                    implicitWidth: 24; implicitHeight: 24
                    fillMode: Image.PreserveAspectFit
                    Controls.Label { anchors.centerIn: parent; text: "?"; visible: !modelData.imageUri }
                }
                Accessible.name: modelData.tooltip
                Controls.ToolTip.text: modelData.tooltip
                Controls.ToolTip.visible: hovered
                onClicked: {
                    const open = !root.expanded || root.selectedKey !== (modelData.key || "")
                    root.selectedKey = modelData.key || ""
                    root.settingsOpen = false
                    root.expanded = open
                }
            }
        }
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
            selectedKey: root.selectedKey
            onClearSelection: root.selectedKey = ""
            onManageAccount: key => {
                const account = root.snapshot.accounts.find(account => account.key === key)
                if (account) manager.selectAccount(account)
                settingsTabs.currentIndex = 0
                root.settingsOpen = true
            }
            Layout.fillWidth: true
            Layout.fillHeight: true
        }
        ColumnLayout {
            visible: root.settingsOpen
            Layout.fillWidth: true
            Layout.fillHeight: true
            Controls.TabBar {
                id: settingsTabs
                Layout.fillWidth: true
                Controls.TabButton { text: "Accounts" }
                Controls.TabButton { text: "General" }
            }
            AccountManager {
                id: manager
                visible: settingsTabs.currentIndex === 0
                Layout.fillWidth: true
                Layout.fillHeight: true
                snapshot: root.snapshot
                signIn: root.signIn
                busy: root.busy
                onRequest: (method, args) => root.call(method, args)
            }
            GeneralSettings {
                visible: settingsTabs.currentIndex === 1
                Layout.fillWidth: true; Layout.fillHeight: true
                snapshot: root.snapshot; preview: root.preview; busy: root.busy
                onRequest: (method, args) => root.call(method, args)
            }
        }
        RowLayout {
            Controls.Label { text: root.snapshot && root.snapshot.demo ? "DEMO" : ""; Layout.fillWidth: true }
            Controls.Button { text: "Refresh"; enabled: !root.busy; onClicked: root.call("Refresh", []) }
            Controls.Button { text: "Close"; onClicked: root.expanded = false }
        }
    }
}
