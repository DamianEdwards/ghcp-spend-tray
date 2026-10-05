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
    property var signIn: null
    property var preview: null
    property var queuedPreview: null
    property string selectedKey: ""
    property string pendingManageKey: ""
    property bool stopped: false
    property string pendingMethod: ""
    property string failure: ""
    readonly property string helper: (Quickshell.env("XDG_DATA_HOME") ||
        Quickshell.env("HOME") + "/.local/share") + "/ghcp-spend-tray-demo/GHCPSpendTray.Linux"

    function request(method, args) {
        if (stopped && method !== "Refresh") return
        if (method === "Refresh") stopped = false
        if (fetch.running && method === "Preview") queuedPreview = args
        if (!fetch.running) {
            pendingMethod = method
            fetch.command = [helper, "--command", method].concat(args || [])
            if (method !== "GetSignIn") failure = ""
            fetch.running = true
        }

        function openPanel(key, settings) {
            stopped = false
            const monitor = Hyprland.focusedMonitor
            popup.screen = Quickshell.screens.find(screen => monitor && screen.name === monitor.name) || Quickshell.screens[0]
            selectedKey = key
            pendingManageKey = settings ? key : ""
            if (settings) settingsTabs.currentIndex = key ? 0 : 1
            settingsOpen = settings
            opened = true
            request("GetSnapshot")
        }
    }

    IpcHandler {
        target: "usage"
        function ready(): bool { return true }
        function open(key: string, settings: bool): void { root.openPanel(key, settings) }
        function toggle(): void {
            if (!root.opened) {
                root.openPanel("", false)
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
                    if (root.pendingMethod === "GetSignIn")
                        root.signIn = Snapshot.parseSignIn(text)
                    else if (root.pendingMethod === "Preview")
                        root.preview = Snapshot.parsePreview(text)
                    else if (root.pendingMethod === "Quit") {
                        root.stopped = true
                        root.snapshot = null
                        root.failure = "Monitoring stopped. Refresh to start again."
                    } else {
                        root.snapshot = Snapshot.parseSnapshot(text)
                        if (root.selectedKey && !root.snapshot.accounts.some(account => account.key === root.selectedKey))
                            root.selectedKey = ""
                        if (root.pendingManageKey) {
                            const account = root.snapshot.accounts.find(account => account.key === root.pendingManageKey)
                            if (account) manager.selectAccount(account)
                            root.pendingManageKey = ""
                        }
                    }
                    if (root.pendingMethod !== "GetSignIn" && root.pendingMethod !== "Quit") root.failure = ""
                } catch (error) {
                    root.snapshot = null
                    root.failure = String(error)
                    console.error(root.failure)
                }
            }
        }
        stderr: StdioCollector {
            onStreamFinished: if (text.length) root.failure = text.trim()
        }
        onExited: (exitCode, exitStatus) => {
            if (exitCode !== 0 || exitStatus !== 0) {
                root.snapshot = null
                if (!root.failure.length)
                    root.failure = "Helper unavailable. Check the installation and try Refresh."
            }
            if (root.queuedPreview && !root.stopped) {
                const args = root.queuedPreview
                root.queuedPreview = null
                Qt.callLater(() => root.request("Preview", args))
            }
        }
    }
    Timer {
        interval: 10000
        running: root.opened
        repeat: true
        onTriggered: root.request("GetSnapshot")
    }
    Timer {
        interval: 1000
        running: root.opened && root.settingsOpen
        repeat: true
        onTriggered: root.request("GetSignIn")
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
            TabBar {
                id: settingsTabs
                visible: root.settingsOpen
                Layout.fillWidth: true
                TabButton { text: "Accounts" }
                TabButton { text: "General" }
            }
            AccountManager {
                id: manager
                visible: root.settingsOpen && settingsTabs.currentIndex === 0
                Layout.fillHeight: true
                Layout.fillWidth: true
                snapshot: root.snapshot
                signIn: root.signIn
                busy: fetch.running
                onRequest: (method, args) => root.request(method, args)
            }
            GeneralSettings {
                visible: root.settingsOpen && settingsTabs.currentIndex === 1
                Layout.fillWidth: true; Layout.fillHeight: true
                snapshot: root.snapshot; preview: root.preview; busy: fetch.running
                onRequest: (method, args) => root.request(method, args)
            }
            RowLayout {
                Label { text: root.snapshot && root.snapshot.demo ? "DEMO" : ""; Layout.fillWidth: true }
                Button { text: "Refresh"; enabled: !fetch.running; onClicked: root.request("Refresh") }
                Button { text: "Close"; onClicked: root.opened = false }
            }
        }
    }
}
