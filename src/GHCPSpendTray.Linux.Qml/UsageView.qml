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
        Frame {
            Layout.fillWidth: true
            ColumnLayout {
                anchors.fill: parent
                spacing: 6
                RowLayout {
                    Layout.fillWidth: true
                    ColumnLayout {
                        Label { text: "This month's usage" }
                        Label {
                            objectName: "consumption"
                            text: view.snapshot ? view.snapshot.consumption : "Unavailable"
                            font.pixelSize: 40
                            font.bold: true
                        }
                    }
                    Item { Layout.fillWidth: true }
                    Label {
                        text: view.snapshot ? view.snapshot.accounts.length + " connected account(s)" : "No current observations"
                    }
                }
                Label {
                    text: view.snapshot ? (view.snapshot.isComplete ? "" :
                        view.snapshot.isLastKnown ? "Last-known / partial total" : "Partial total - some accounts unavailable") : ""
                    wrapMode: Text.Wrap; Layout.fillWidth: true
                }
                Label {
                    text: view.snapshot ? view.snapshot.status : ""
                    textFormat: Text.PlainText; wrapMode: Text.Wrap; Layout.fillWidth: true
                }
            }
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
                Layout.maximumWidth: view.availableWidth
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
                        Button { text: "Manage account"; onClicked: view.manageAccount(card.modelData.key) }
                    }
                    Item {
                        id: forecastTrack
                        objectName: "forecast-track"
                        visible: card.modelData.actualUsage !== null && card.modelData.actualUsage !== undefined
                        Layout.fillWidth: true
                        implicitHeight: card.modelData.forecast ? 88 : 64
                        clip: true
                        readonly property real meterWidth: Math.max(0, width - target.width - 18)
                        readonly property real markerLimit: Math.max(0, meterWidth - 7)
                        Accessible.name: card.modelData.forecast ? Snapshot.forecastText(card.modelData) :
                            card.modelData.name + " current usage " + Snapshot.formatPercent(card.modelData.actualUsage.percent)
                        Rectangle {
                            x: 2; width: forecastTrack.meterWidth
                            y: 42
                            height: 4; radius: 2; color: view.palette.mid
                        }
                        Rectangle {
                            x: 2; y: 42
                            height: 4; radius: 2; color: "#ff7a2f"
                            width: forecastTrack.meterWidth * card.modelData.actualUsage.fraction
                        }
                        Item {
                            visible: card.modelData.forecast !== null && card.modelData.forecast !== undefined
                            x: 2 + forecastTrack.meterWidth * card.modelData.forecast.actualFraction
                            y: 39
                            width: Math.max(0, forecastTrack.meterWidth *
                                (card.modelData.forecast.forecastFraction - card.modelData.forecast.actualFraction))
                            height: parent.height
                            clip: true
                            Row {
                                anchors.fill: parent; spacing: 3
                                Repeater {
                                    model: Math.ceil(parent.width / 7)
                                    Rectangle {
                                        required property int index
                                        y: 3
                                        width: 4
                                        height: 4; radius: 2
                                        color: card.modelData.forecast.band === "Over" ? "#e01b24" :
                                            card.modelData.forecast.band === "Near" ? "#e5a50a" : "#3584e4"
                                    }
                                }
                            }
                        }
                        Rectangle {
                            x: Math.min(forecastTrack.markerLimit, Math.max(0,
                                2 + forecastTrack.meterWidth * card.modelData.actualUsage.fraction - width / 2))
                            y: 34; width: 2; height: 20; color: view.palette.text
                        }
                        Rectangle {
                            visible: card.modelData.forecast !== null && card.modelData.forecast !== undefined
                            x: Math.min(forecastTrack.markerLimit, Math.max(0,
                                2 + forecastTrack.meterWidth * card.modelData.forecast.forecastFraction - width / 2))
                            y: 38; width: 2; height: 22
                            color: card.modelData.forecast && card.modelData.forecast.band === "Over" ? "#e01b24" :
                                card.modelData.forecast && card.modelData.forecast.band === "Near" ? "#e5a50a" : "#3584e4"
                        }
                        Column {
                            id: current
                            x: Math.min(Math.max(0, forecastTrack.meterWidth - width - 2), Math.max(0,
                                2 + forecastTrack.meterWidth * card.modelData.actualUsage.fraction - width / 2))
                            y: 0
                            Label {
                                objectName: "current-amount"
                                text: card.modelData.consumption; font.bold: true; font.pixelSize: 22
                            }
                            Label {
                                objectName: "current-caption"
                                text: "used"; anchors.horizontalCenter: parent.horizontalCenter
                            }
                        }
                        Column {
                            id: target
                            anchors.right: parent.right
                            y: 27
                            Label {
                                objectName: "target-amount"
                                text: card.modelData.trackingTarget; font.bold: true; font.pixelSize: 18
                            }
                            Label {
                                objectName: "target-caption"
                                text: "budget"
                                anchors.horizontalCenter: parent.horizontalCenter
                            }
                        }
                        Column {
                            visible: card.modelData.forecast !== null && card.modelData.forecast !== undefined
                            x: Math.min(Math.max(0, forecastTrack.meterWidth - width - 2), Math.max(0,
                                2 + forecastTrack.meterWidth * card.modelData.forecast.forecastFraction - width / 2))
                            y: 58
                            Label {
                                objectName: "forecast-amount"
                                text: card.modelData.estimateAmount || ""; font.bold: true; font.pixelSize: 16
                            }
                            Label {
                                objectName: "forecast-caption"
                                text: "est. total"; anchors.horizontalCenter: parent.horizontalCenter
                            }
                        }
                    }
                    Label {
                        objectName: "allocation-label"
                        text: card.modelData.actualUsage === null || card.modelData.actualUsage === undefined ?
                            "Tracking target unavailable" : card.modelData.usageCaption
                        wrapMode: Text.Wrap
                        Layout.fillWidth: true
                        Layout.maximumWidth: view.availableWidth - 32
                    }
                    Label {
                        objectName: "forecast-label"
                        visible: card.modelData.forecast !== null && card.modelData.forecast !== undefined
                        text: [card.modelData.estimateCaption, Snapshot.forecastStatus(card.modelData)]
                            .filter(Boolean).join(" · ")
                        textFormat: Text.PlainText; wrapMode: Text.Wrap; Layout.fillWidth: true
                        Layout.maximumWidth: view.availableWidth - 32
                    }
                    RowLayout {
                        Label {
                            text: card.modelData.freshness +
                                (card.modelData.actualUsage && card.modelData.actualUsage.band === "Over" ?
                                    " - At or over " + card.modelData.targetLabel : "")
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
