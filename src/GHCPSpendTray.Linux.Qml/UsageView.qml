import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

ScrollView {
    id: view
    property var snapshot: null
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
            visible: view.snapshot !== null && view.snapshot.accounts.length === 0
            objectName: "empty-hint"
            text: "Add a demo account in Settings."
        }
        Repeater {
            model: view.snapshot ? view.snapshot.accounts : []
            delegate: Frame {
                id: card
                objectName: "account-" + modelData.key
                required property var modelData
                property bool detailsOpen: false
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
                                anchors.centerIn: parent
                                text: card.modelData.name.slice(0, 2).toUpperCase()
                                font.bold: true
                            }
                        }
                        ColumnLayout {
                            Layout.fillWidth: true
                            Label { text: card.modelData.name; font.bold: true; wrapMode: Text.Wrap; Layout.fillWidth: true }
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
                    RowLayout {
                        Label {
                            text: card.modelData.freshness + (card.modelData.percent > 100 ? " - Over allocation" : "")
                            wrapMode: Text.Wrap
                            Layout.fillWidth: true
                        }
                        Button {
                            objectName: "details-button"
                            text: card.detailsOpen ? "Hide" : "Details"
                            onClicked: card.detailsOpen = !card.detailsOpen
                        }
                    }
                    Label {
                        objectName: "account-details"
                        visible: card.detailsOpen
                        text: card.modelData.login + " @ " + card.modelData.host + "\n" +
                            (card.modelData.updatedAt ? "Updated " + new Date(card.modelData.updatedAt).toLocaleString() : "No observations yet")
                        wrapMode: Text.Wrap
                        Layout.fillWidth: true
                    }
                }
            }
        }
        Label { text: "AI-credit consumption value, not an invoice."; wrapMode: Text.Wrap; Layout.fillWidth: true }
    }
}
