import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import "../snapshot.js" as Snapshot

ScrollView {
    id: view
    property var snapshot: null
    property string selectedKey: ""
    property var expandedKeys: []
    signal manageAccount(string key)
    signal clearSelection()
    clip: true
    contentWidth: availableWidth
    ColumnLayout {
        width: view.availableWidth
        spacing: 12
        Label { text: "This month's consumption" }
        Label {
            objectName: "consumption"
            text: view.snapshot ? view.snapshot.consumption : "Unavailable"
            font.pixelSize: 40
            font.bold: true
        }
        Label {
            text: view.snapshot ? view.snapshot.accounts.length + " connected account(s)" : "No current observations"
        }
        Label {
            text: view.snapshot ? (view.snapshot.isComplete ? "" :
                view.snapshot.isLastKnown ? "Last-known / partial total" : "Partial total - some accounts unavailable") : ""
            wrapMode: Text.Wrap; Layout.fillWidth: true
        }
        Label { text: view.snapshot ? view.snapshot.status : ""; textFormat: Text.PlainText; wrapMode: Text.Wrap; Layout.fillWidth: true }
        Label {
            text: view.snapshot && view.snapshot.tray ? view.snapshot.tray.rollUp.details : ""
            textFormat: Text.PlainText; wrapMode: Text.Wrap; Layout.fillWidth: true
        }
        Button { visible: view.selectedKey.length > 0; text: "All accounts"; onClicked: view.clearSelection() }
        Label {
            visible: view.snapshot !== null && view.snapshot.accounts.length === 0
            objectName: "empty-hint"
            text: "Sign in to an account in Settings."
        }
        Repeater {
            model: view.snapshot ? view.snapshot.accounts.filter(account => !view.selectedKey || account.key === view.selectedKey) : []
            delegate: Frame {
                id: card
                objectName: "account-" + modelData.key
                required property var modelData
                readonly property bool detailsOpen: view.expandedKeys.indexOf(modelData.key) >= 0
                Layout.fillWidth: true
                ColumnLayout {
                    anchors.fill: parent
                    spacing: 10
                    RowLayout {
                        Rectangle {
                            implicitWidth: 32
                            implicitHeight: 32
                            radius: 16
                            color: view.palette.midlight
                            Label {
                                visible: !card.modelData.avatarUri || avatar.status === Image.Error
                                anchors.centerIn: parent
                                text: card.modelData.name.slice(0, 2).toUpperCase()
                                font.bold: true
                            }
                            Image {
                                id: avatar
                                anchors.fill: parent
                                source: card.modelData.avatarUri || ""
                                fillMode: Image.PreserveAspectFit
                                onStatusChanged: if (status === Image.Error) console.error("Cached avatar unavailable; showing initials.")
                            }
                        }
                        ColumnLayout {
                            Layout.fillWidth: true
                            Label { text: card.modelData.name; textFormat: Text.PlainText; font.bold: true; wrapMode: Text.Wrap; Layout.fillWidth: true }
                            Label { text: card.modelData.host; wrapMode: Text.Wrap; Layout.fillWidth: true }
                        }
                        Label { text: card.modelData.consumption; font.bold: true; font.pixelSize: 22 }
                    }
                    ProgressBar {
                        objectName: "allocation-progress"
                        visible: card.modelData.percent !== null
                        value: Math.min(100, card.modelData.percent || 0)
                        from: 0
                        to: 100
                        Layout.fillWidth: true
                        Accessible.name: card.modelData.name + " allocation consumed"
                    }
                    Label {
                        objectName: "allocation-label"
                        text: card.modelData.percent === null ? "Allocation percentage unavailable" :
                            card.modelData.percent + "% of " + card.modelData.allocation + " allocation"
                        wrapMode: Text.Wrap
                        Layout.fillWidth: true
                    }
                    Label {
                        visible: card.modelData.periodEstimate !== null && card.modelData.periodEstimate !== undefined
                        text: Snapshot.estimateText(card.modelData.periodEstimate)
                        textFormat: Text.PlainText; wrapMode: Text.Wrap; Layout.fillWidth: true
                    }
                    RowLayout {
                        Label {
                            text: card.modelData.freshness + (card.modelData.percent > 100 ? " - Over allocation" : "")
                            wrapMode: Text.Wrap
                            Layout.fillWidth: true
                        }
                        Button {
                            objectName: "details-button"
                            text: card.detailsOpen ? "Hide" : "Details"
                            onClicked: view.expandedKeys = card.detailsOpen ?
                                view.expandedKeys.filter(key => key !== card.modelData.key) :
                                view.expandedKeys.concat([card.modelData.key])
                        }
                        Button { text: "Manage"; onClicked: view.manageAccount(card.modelData.key) }
                    }
                    Label {
                        objectName: "account-details"
                        visible: card.detailsOpen
                        text: card.modelData.login + " @ " + card.modelData.host + "\n" +
                            (card.modelData.updatedAt ? "Updated " + new Date(card.modelData.updatedAt).toLocaleString() : "No observations yet") +
                            "\n" + Snapshot.diagnosticsText(card.modelData)
                        textFormat: Text.PlainText
                        wrapMode: Text.Wrap
                        Layout.fillWidth: true
                    }
                }
            }
        }
        Label { text: "AI-credit consumption value, not an invoice."; wrapMode: Text.Wrap; Layout.fillWidth: true }
    }
}
