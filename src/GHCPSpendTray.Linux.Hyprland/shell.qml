import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import Quickshell
import Quickshell.Io
import Quickshell.Wayland
import Quickshell.Hyprland
import "snapshot.js" as Snapshot
import "components"

Scope {
    id: root
    property bool opened: false
    property bool settingsOpen: false
    property var snapshot: null
    property string failure: ""
    readonly property string helper: (Quickshell.env("XDG_DATA_HOME") ||
        Quickshell.env("HOME") + "/.local/share") + "/ghcp-spend-tray-demo/GHCPSpendTray.Linux"

    function request(method) {
        if (!fetch.running) {
            fetch.command = [helper, "--command", method]
            fetch.running = true
        }
    }

    IpcHandler {
        target: "usage"
        function ready(): bool { return true }
        function toggle(): void {
            if (!root.opened) {
                const monitor = Hyprland.focusedMonitor
                popup.screen = Quickshell.screens.find(screen => monitor && screen.name === monitor.name) ||
                    Quickshell.screens[0]
                root.opened = true
                root.request("GetSnapshot")
            } else {
                root.opened = false
            }
        }
    }

    Process {
        id: fetch
        stdout: StdioCollector {
            onStreamFinished: {
                if (!text.length) {
                    root.snapshot = null
                    root.failure = "Helper returned no presentation data. Try Refresh."
                    console.error(root.failure)
                    return
                }
                try {
                    root.snapshot = Snapshot.parseSnapshot(text)
                    root.failure = ""
                } catch (error) {
                    root.snapshot = null
                    root.failure = String(error)
                    console.error(root.failure)
                }
            }
        }
        stderr: StdioCollector {
            onStreamFinished: if (text.length) console.error(text)
        }
        onExited: (exitCode, exitStatus) => {
            if (exitCode !== 0 || exitStatus !== 0) {
                root.snapshot = null
                root.failure = "Helper unavailable. Check the installation and try Refresh."
            }
        }
    }
    Timer {
        interval: 10000
        running: root.opened
        repeat: true
        onTriggered: root.request("GetSnapshot")
    }
    HyprlandFocusGrab {
        windows: [popup]
        active: root.opened
        onCleared: root.opened = false
    }

    PanelWindow {
        id: popup
        visible: root.opened
        anchors.top: true
        anchors.right: true
        margins.top: 36
        margins.right: 8
        implicitWidth: Math.min(400, screen ? screen.width - 16 : 400)
        implicitHeight: Math.min(580, screen ? screen.height - 52 : 580)
        exclusionMode: ExclusionMode.Ignore
        WlrLayershell.namespace: "ghcp-spend-tray"
        WlrLayershell.layer: WlrLayer.Overlay
        WlrLayershell.keyboardFocus: WlrKeyboardFocus.Exclusive
        onVisibleChanged: if (visible) content.forceActiveFocus()
        color: palette.window
        SystemPalette { id: palette }
        ColumnLayout {
            id: content
            anchors.fill: parent
            anchors.margins: 16
            focus: true
            Keys.onEscapePressed: root.opened = false
            RowLayout {
                Label { text: "GHCPSpendTray"; font.pixelSize: 20; font.bold: true; Layout.fillWidth: true }
                Button { text: root.settingsOpen ? "Back" : "Settings"; onClicked: root.settingsOpen = !root.settingsOpen }
            }
            Label { text: root.failure; visible: root.failure.length > 0; wrapMode: Text.Wrap; Layout.fillWidth: true }
            UsageView {
                visible: !root.settingsOpen
                snapshot: root.snapshot
                Layout.fillWidth: true
                Layout.fillHeight: true
            }
            ColumnLayout {
                visible: root.settingsOpen
                Layout.fillHeight: true
                Layout.fillWidth: true
                Label { text: "Synthetic accounts only. Authentication is disabled."; wrapMode: Text.Wrap; Layout.fillWidth: true }
                Button {
                    text: "Add demo account"
                    enabled: !fetch.running && root.snapshot !== null && root.snapshot.accounts.length < 100
                    onClicked: root.request("AddDemoAccount")
                }
                Item { Layout.fillHeight: true }
            }
            RowLayout {
                Label { text: "DEMO"; Layout.fillWidth: true }
                Button { text: "Refresh"; enabled: !fetch.running; onClicked: root.request("Refresh") }
                Button { text: "Close"; onClicked: root.opened = false }
            }
        }
    }
}
