import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import "../snapshot.js" as Snapshot

ScrollView {
    id: view
    property var snapshot: null
    property var signIn: null
    property bool busy: false
    property string selectedKey: ""
    property string selectedHost: ""
    property string validation: ""
    readonly property bool active: signIn !== null && ["starting", "waiting", "saving"].indexOf(signIn.phase) >= 0
    readonly property bool waiting: signIn !== null && signIn.phase === "waiting"
    signal request(string method, var arguments)
    clip: true
    contentWidth: availableWidth
    onSnapshotChanged: {
        if (snapshot && selectedKey && !snapshot.accounts.some(account => account.key === selectedKey))
            selectedKey = ""
    }

    function execute(value) { request("Execute", [JSON.stringify(value)]) }
    function selectAccount(account) {
        selectedKey = account.key
        selectedHost = account.host
        displayName.text = account.displayName
        thresholds.text = account.thresholds
        spend.text = account.spendIncrementUsd === null ? "" : String(account.spendIncrementUsd)
        budget.text = account.customBudgetUsd === null ? "" : String(account.customBudgetUsd)
        budgetMode.currentIndex = account.customBudgetUsd === null ? 0 : 1
        clientId.text = account.clientId || ""
        estimate.checked = account.showPeriodEstimate
        confirmRemoval.checked = false
        validation = ""
    }

    ColumnLayout {
        width: view.availableWidth
        spacing: 10
        Label {
            text: "Credentials are saved in Secret Service (GNOME Keyring or KWallet). Start and unlock your keyring before signing in."
            wrapMode: Text.Wrap
            Layout.fillWidth: true
        }
        Label {
            visible: view.snapshot !== null && view.snapshot.demo
            text: "Explicit synthetic demo mode: real sign-in is disabled."
            wrapMode: Text.Wrap
            Layout.fillWidth: true
        }
        Button {
            visible: view.snapshot !== null && view.snapshot.demo
            text: "Add demo account"
            enabled: !view.busy && view.snapshot !== null && view.snapshot.accounts.length < 100
            onClicked: view.request("AddDemoAccount", [])
        }
        TextField { id: host; objectName: "signin-host"; text: "github.com"; placeholderText: "HTTPS host"; Layout.fillWidth: true }
        TextField { id: clientId; placeholderText: "Enterprise OAuth client ID (if required)"; Layout.fillWidth: true }
        CheckBox { id: offline; text: "Request offline access" }
        Button {
            objectName: "signin-button"
            text: "Sign in to a new account"
            enabled: !view.busy && !view.active && view.snapshot !== null && !view.snapshot.demo
            onClicked: view.execute({kind: "signin", host: host.text, clientId: clientId.text || null, offlineAccess: offline.checked})
        }
        Label {
            text: view.signIn ? (view.signIn.message || view.signIn.phase) : ""
            textFormat: Text.PlainText
            wrapMode: Text.Wrap
            Layout.fillWidth: true
        }
        TextEdit {
            id: code
            objectName: "device-code"
            visible: view.waiting
            text: view.waiting ? view.signIn.code : ""
            readOnly: true
            selectByMouse: true
            color: view.palette.text
            font.pixelSize: 24
            Layout.fillWidth: true
        }
        Label {
            visible: view.waiting
            text: view.waiting ? view.signIn.verificationUri + "\nExpires " + new Date(view.signIn.expires).toLocaleTimeString() : ""
            textFormat: Text.PlainText
            wrapMode: Text.Wrap
            Layout.fillWidth: true
        }
        Button {
            visible: view.waiting
            text: "Copy code"
            onClicked: { code.selectAll(); code.copy(); code.deselect() }
        }
        Button {
            visible: view.waiting
            text: "Open verification page"
            onClicked: {
                if (new Date(view.signIn.expires).getTime() <= Date.now())
                    view.validation = "Device code expired. Start sign-in again."
                else if (!Qt.openUrlExternally(view.signIn.verificationUri))
                    view.validation = "Could not open a browser. Open the displayed HTTPS address manually."
            }
        }
        Button {
            text: "Cancel sign-in"
            visible: view.active
            enabled: !view.busy
            onClicked: view.execute({kind: "cancel"})
        }
        Label { text: "Manage accounts"; font.bold: true }
        Repeater {
            model: view.snapshot ? view.snapshot.accounts : []
            delegate: Button {
                required property var modelData
                text: modelData.name + " @ " + modelData.host
                enabled: !view.active && !view.busy
                Layout.fillWidth: true
                onClicked: view.selectAccount(modelData)
            }
        }
        ColumnLayout {
            visible: view.selectedKey.length > 0
            enabled: !view.active && !view.busy
            Layout.fillWidth: true
            Label { text: view.selectedKey; textFormat: Text.PlainText }
            TextField { id: displayName; objectName: "account-name"; placeholderText: "Display name"; Layout.fillWidth: true }
            TextField { id: thresholds; placeholderText: "Alert percentages (blank inherits)"; Layout.fillWidth: true }
            TextField { id: spend; placeholderText: "USD alert increment (blank inherits; 0 disables)"; Layout.fillWidth: true }
            Label { text: "Usage budget"; font.bold: true }
            Label {
                text: "Choose what this account's usage percentage is measured against."
                wrapMode: Text.Wrap; Layout.fillWidth: true
            }
            ComboBox {
                id: budgetMode
                objectName: "budget-mode"
                model: ["Use API limit", "Use a custom budget"]
                Layout.fillWidth: true
            }
            TextField {
                id: budget
                objectName: "custom-budget"
                placeholderText: "Custom budget (USD)"
                enabled: budgetMode.currentIndex === 1
                Layout.fillWidth: true
            }
            Label {
                objectName: "budget-preview"
                readonly property var account: view.snapshot ?
                    view.snapshot.accounts.find(value => value.key === view.selectedKey) : null
                readonly property var target: budgetMode.currentIndex === 0 ? (account ? account.apiLimitUsd : null) :
                    (budget.text.trim() === "" ? null : Number(budget.text))
                text: target > 0 && account && account.consumptionUsd !== null ?
                    "Preview: " + account.consumption + " consumed = " +
                    Snapshot.formatPercent(account.consumptionUsd / target * 100) + " of " +
                    (budgetMode.currentIndex === 0 ? account.apiLimit : Snapshot.amount(target)) + " " +
                    (budgetMode.currentIndex === 0 ? "API limit" : "custom budget") :
                    "Enter a valid target to preview the usage percentage."
                wrapMode: Text.Wrap; Layout.fillWidth: true
            }
            Label {
                text: "Applies to this account's current billing period, usage meter, tray percentage, estimates, and percentage alerts. It does not cap spending or change your GitHub limit."
                wrapMode: Text.Wrap; Layout.fillWidth: true
            }
            CheckBox { id: estimate; objectName: "show-estimate"; text: "Show estimated period consumption" }
            Label {
                text: "Average pace so far this UTC calendar month. Assumes the same pace continues; not an invoice."
                wrapMode: Text.Wrap; Layout.fillWidth: true
            }
            RowLayout {
                Button {
                    objectName: "save-account"
                    text: "Save"
                    onClicked: {
                        const amount = spend.text.trim() === "" ? null : Number(spend.text)
                        if (amount !== null && (!Number.isFinite(amount) || amount < 0)) {
                            view.validation = "Enter a nonnegative USD amount or leave it blank."
                            return
                        }
                        const customBudget = budgetMode.currentIndex === 0 ? null :
                            (budget.text.trim() === "" ? null : Number(budget.text))
                        if (budgetMode.currentIndex === 1 && (customBudget === null ||
                                !Number.isFinite(customBudget) || customBudget <= 0 ||
                                Math.abs(Math.round(customBudget * 100) - customBudget * 100) > 0.00000001)) {
                            view.validation = "Custom budgets must be positive USD amounts with at most two decimal places."
                            return
                        }
                        view.validation = ""
                        view.execute({kind: "save", key: view.selectedKey, displayName: displayName.text,
                            thresholds: thresholds.text, spendIncrementUsd: amount, showPeriodEstimate: estimate.checked,
                            customBudgetUsd: customBudget, updateCustomBudget: true})
                    }
                }
                Button { text: "Refresh"; onClicked: view.execute({kind: "refresh", key: view.selectedKey}) }
                Button {
                    text: "Reconnect"
                    onClicked: view.execute({kind: "signin", key: view.selectedKey,
                        clientId: clientId.text || null, offlineAccess: offline.checked})
                }
            }
            Button {
                text: "Manage OAuth grants"
                onClicked: if (!Qt.openUrlExternally("https://" + view.selectedHost + "/settings/applications"))
                    view.validation = "Could not open this host's OAuth settings."
            }
            Label {
                text: "Removal deletes the local credential, alert state and cached avatar, not the OAuth grant. Retained history and recovery copies may remain."
                wrapMode: Text.Wrap; Layout.fillWidth: true
            }
            CheckBox { id: confirmRemoval; text: "Confirm removal and credential deletion" }
            Button {
                text: "Remove account"
                enabled: confirmRemoval.checked
                onClicked: {
                    view.execute({kind: "remove", key: view.selectedKey})
                    confirmRemoval.checked = false
                }
            }
        }
        Label { text: view.validation; textFormat: Text.PlainText; wrapMode: Text.Wrap; Layout.fillWidth: true }
    }
}
