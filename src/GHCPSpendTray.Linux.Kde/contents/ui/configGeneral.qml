import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

ColumnLayout {
    property alias cfg_showConsumption: consumption.checked
    CheckBox {
        id: consumption
        text: "Show consumption amount instead of percentage in the panel"
    }
    Label {
        text: "Synthetic demo only. Real GitHub accounts and authentication are disabled."
        wrapMode: Text.Wrap
        Layout.fillWidth: true
    }
    Item { Layout.fillHeight: true }
}
