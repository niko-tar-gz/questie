import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

ApplicationWindow {
    id: win
    visible: true
    title: "questie"
    width: 1000
    height: 700
    minimumWidth: 820
    minimumHeight: 600
    color: palette.window

    // Dark palette (applied app-wide by the Fusion style).
    palette.window: "#1e1f22"
    palette.windowText: "#e6e8eb"
    palette.base: "#2b2d31"
    palette.alternateBase: "#26282c"
    palette.text: "#e6e8eb"
    palette.placeholderText: "#9aa0a6"
    palette.button: "#33363c"
    palette.buttonText: "#e6e8eb"
    palette.highlight: "#8ab4f8"
    palette.highlightedText: "#1e1f22"
    palette.toolTipBase: "#35373c"
    palette.toolTipText: "#e6e8eb"
    palette.light: "#3c3f45"
    palette.midlight: "#33363c"
    palette.mid: "#2b2d31"
    palette.dark: "#17181b"
    palette.shadow: "#000000"

    property string fwMessage: ""
    property bool fwLoading: false
    property string pendingUrl: ""
    property string pendingIncremental: ""
    property string pendingVersion: ""
    property string pendingSha: ""
    property bool pendingIsFactory: false
    property bool pendingIsDowngrade: false

    ListModel { id: fwModel }

    Connections {
        target: Device
        function onConnectedChanged() { loadFirmware(); }
        function onCodenameChanged() { loadFirmware(); }
    }

    Component.onCompleted: {
        Device.refresh();
        loadFirmware();
    }

    function numericLess(a, b) {
        a = (a || "").trim(); b = (b || "").trim();
        if (a === "" || b === "") return false;
        if (!/^\d+$/.test(a) || !/^\d+$/.test(b)) return a < b;
        a = a.replace(/^0+/, ""); b = b.replace(/^0+/, "");
        if (a.length !== b.length) return a.length < b.length;
        return a < b;
    }

    function entryIsDowngrade(isFactory, inc) {
        if (isFactory) return true;
        var cur = (Device.firmwareIncremental || "").trim();
        if (cur === "" || (inc || "") === "") return false;
        return numericLess(inc, cur);
    }

    function selectedEntry() {
        if (fwCombo.currentIndex < 0 || fwCombo.currentIndex >= fwModel.count)
            return null;
        return fwModel.get(fwCombo.currentIndex);
    }

    function selectedIsDowngrade() {
        var e = selectedEntry();
        if (!e) return false;
        return entryIsDowngrade(e.isFactory, e.incremental);
    }

    function flashBlockedByLock() {
        return selectedIsDowngrade() && Device.lockState === "locked";
    }

    function loadFirmware() {
        // refresh fires connected + codename changes together; never run two fetches at once.
        if (fwLoading) return;
        fwModel.clear();
        fwMessage = "";
        if (!Device.connected) {
            fwMessage = "connect a headset and press refresh to load available updates.";
            return;
        }
        if (Device.codename === "monterey") {
            fwModel.append({
                incremental: "2135610041500000",
                version: "factory firmware (not an ota)",
                runtime: "",
                buildDate: "",
                fingerprint: "",
                sha256: "",
                url: "https://github.com/QuestEscape/updates/releases/download/2135610041500000/2135610041500000_2135610041500000.zip",
                isFactory: true,
                display: "factory firmware 2135610041500000"
            });
        }
        if (Device.archiveUrl === "") {
            if (fwModel.count === 0)
                fwMessage = "no firmware archive known for this headset (" + Device.codename + ").";
            return;
        }
        fwLoading = true;
        fwMessage = "loading update list from cocaine.trade...";
        var xhr = new XMLHttpRequest();
        xhr.onreadystatechange = function() {
            if (xhr.readyState === XMLHttpRequest.DONE) {
                if (xhr.status === 200) {
                    parseFirmwareHtml(xhr.responseText);
                } else {
                    try {
                        parseFirmwareHtml(Firmware.fetchPage(Device.archiveUrl));
                    } catch (e) {
                        fwMessage = "failed to load update list: " + e.message;
                    }
                }
                fwLoading = false;
            }
        };
        xhr.open("GET", Device.archiveUrl);
        xhr.send();
    }

    function parseFirmwareHtml(html) {
        var count = 0;
        // rows are separated by <tr> open tags and cells are bare <td> tags.
        // this tolerates both the old fully-closed markup and the current
        // minimized markup (attribute order changes, unquoted attrs, no </td>).
        var rows = html.split(/<tr[^>]*>/);
        // skip anything already in the model (overlapping reloads, repeated rows).
        var seen = {};
        for (var s = 0; s < fwModel.count; s++)
            seen[fwModel.get(s).incremental] = true;
        for (var i = 0; i < rows.length; i++) {
            // href may be quoted or bare, and may come before or after class.
            var link = /<a[^>]*href=(?:"([^"]+)"|([^\s>]+))[^>]*>([^<]+)<\/a>/.exec(rows[i]);
            if (!link || link[0].indexOf("fw-link") === -1) continue;
            var url = link[1] || link[2];
            var incremental = (link[3] || "").trim();
            var parts = rows[i].split(/<td[^>]*>/).slice(1);
            var cells = [];
            for (var c = 0; c < parts.length; c++) {
                var end = parts[c].search(/</);
                cells.push((end === -1 ? parts[c] : parts[c].substring(0, end)).trim());
            }
            if (cells.length < 2) continue;
            if (incremental === "" || seen[incremental]) continue;
            seen[incremental] = true;
            var version = cells[1] || "";
            fwModel.append({
                incremental: incremental,
                version: version,
                runtime: cells[2] || "",
                buildDate: cells[3] || "",
                fingerprint: cells[4] || "",
                sha256: cells[5] || "",
                url: url,
                isFactory: false,
                display: "v" + version + "  (" + incremental + ")"
            });
            count++;
        }
        // Keep the Quest 1 factory entry (if any) at the very bottom. copy its
        // values out first: get() hands back a live reference that remove()
        // invalidates, so re-appending it directly writes an all-empty row.
        for (var i = 0; i < fwModel.count; i++) {
            if (fwModel.get(i).isFactory) {
                var f = fwModel.get(i);
                var fRow = {
                    incremental: f.incremental,
                    version: f.version,
                    runtime: f.runtime,
                    buildDate: f.buildDate,
                    fingerprint: f.fingerprint,
                    sha256: f.sha256,
                    url: f.url,
                    isFactory: f.isFactory,
                    display: f.display
                };
                fwModel.remove(i);
                fwModel.append(fRow);
                break;
            }
        }
        fwMessage = count === 0
            ? "no updates found in the archive."
            : count + " otas found.";
    }

    // partitions to erase before install, as a comma separated string.
    // passed as a string rather than two booleans because bool parameters
    // crossing the C++/C# bridge have produced spurious wipe requests.
    function eraseFlags() {
        var flags = [];
        if (eraseDataCheck.checked) flags.push("userdata");
        if (eraseCacheCheck.checked) flags.push("cache");
        return flags.join(",");
    }

    function lockColor() {
        if (Device.lockState === "locked") return "#a54242";
        if (Device.lockState === "unlocked") return "#3d7a44";
        return "#5a5e64";
    }

    function askFlash() {
        var e = selectedEntry();
        if (!e) return;
        pendingUrl = e.url;
        pendingIncremental = e.incremental;
        pendingVersion = e.version;
        pendingSha = e.sha256;
        pendingIsFactory = e.isFactory;
        pendingIsDowngrade = selectedIsDowngrade();
        if (e.isFactory)
            factoryDialog.open();
        else
            otaDialog.open();
    }

    RowLayout {
        anchors.fill: parent
        anchors.margins: 16
        spacing: 12

        // left
        ColumnLayout {
            Layout.fillWidth: true
            Layout.fillHeight: true
            Layout.preferredWidth: 3
            spacing: 12

            // headset info
            GroupBox {
                title: "headset"
                Layout.fillWidth: true
                RowLayout {
                    anchors.fill: parent
                    spacing: 16
                    Image {
                        source: Device.headsetImage
                        Layout.preferredWidth: 104
                        Layout.preferredHeight: 104
                        fillMode: Image.PreserveAspectFit
                        cache: false
                    }
                    ColumnLayout {
                        Layout.fillWidth: true
                        spacing: 4
                        Label {
                            text: Device.headsetName
                            font.pixelSize: 20
                            font.bold: true
                        }
                        GridLayout {
                            columns: 2
                            columnSpacing: 12
                            rowSpacing: 2
                            Label { text: "model:"; color: palette.placeholderText }
                            Label { text: Device.codename }
                            Label { text: "firmware:"; color: palette.placeholderText }
                            Label {
                                text: Device.firmwareIncremental === ""
                                      ? "unknown"
                                      : (Device.firmwareVersion !== "" && Device.firmwareVersion !== Device.firmwareIncremental
                                         ? Device.firmwareVersion + " (" + Device.firmwareIncremental + ")"
                                         : Device.firmwareIncremental)
                                wrapMode: Text.WrapAnywhere
                                Layout.fillWidth: true
                            }
                            Label { text: "serial:"; color: palette.placeholderText }
                            Text {
                                text: Device.serial === "" ? "unknown"
                                      : (Device.serialRevealed ? Device.serial : "click to reveal")
                                color: Device.serial !== "" && !Device.serialRevealed ? palette.highlight : palette.windowText
                                font.underline: Device.serial !== "" && !Device.serialRevealed
                                MouseArea {
                                    anchors.fill: parent
                                    cursorShape: Device.serial !== "" ? Qt.PointingHandCursor : Qt.ArrowCursor
                                    onClicked: {
                                        if (Device.serial !== "")
                                            Device.serialRevealed = !Device.serialRevealed;
                                    }
                                }
                            }
                            Label { text: "bootloader:"; color: palette.placeholderText }
                            Rectangle {
                                radius: 4
                                color: lockColor()
                                implicitWidth: lockLabel.implicitWidth + 16
                                implicitHeight: lockLabel.implicitHeight + 8
                                Label {
                                    id: lockLabel
                                    anchors.centerIn: parent
                                    text: Device.lockState === "locked" ? "locked"
                                          : Device.lockState === "unlocked" ? "unlocked" : "unknown"
                                    color: "white"
                                    font.bold: true
                                }
                            }
                        }
                        Label {
                            text: Device.status
                            color: palette.placeholderText
                            font.pixelSize: 12
                            wrapMode: Text.WordWrap
                            Layout.fillWidth: true
                        }
                    }
                }
            }

            // dropdown for ota version
            GroupBox {
                title: "ota version"
                Layout.fillWidth: true
                ColumnLayout {
                    anchors.fill: parent
                    spacing: 6
                    ComboBox {
                        id: fwCombo
                        Layout.fillWidth: true
                        model: fwModel
                        textRole: "display"
                        enabled: fwModel.count > 0
                    }
                    Label {
                        text: {
                            var e = selectedEntry();
                            if (!e) return fwMessage !== "" ? fwMessage : "no version selected.";
                            if (e.isFactory) return "initial factory image";
                            var s = "incremental " + e.incremental;
                            if (e.buildDate !== "") s += "  -  " + e.buildDate;
                            if (e.runtime !== "") s += "\nruntime " + e.runtime;
                            return s;
                        }
                        color: palette.placeholderText
                        font.pixelSize: 12
                        wrapMode: Text.WrapAnywhere
                        Layout.fillWidth: true
                    }
                    Label {
                        visible: selectedEntry() && !selectedEntry().isFactory && selectedEntry().fingerprint !== ""
                        text: {
                            var e = selectedEntry();
                            if (!e || e.isFactory) return "";
                            return e.url + "\nsha-256: " + e.sha256;
                        }
                        color: palette.placeholderText
                        font.pixelSize: 11
                        wrapMode: Text.WrapAnywhere
                        Layout.fillWidth: true
                    }
                    Label {
                        visible: selectedEntry() !== null && selectedIsDowngrade() && Device.lockState === "locked"
                        text: "your bootloader is locked, so you can't downgrade."
                        color: "#e08585"
                        font.pixelSize: 12
                        wrapMode: Text.WordWrap
                        Layout.fillWidth: true
                    }
                    Label {
                        visible: selectedEntry() !== null && selectedEntry().isFactory
                        text: "flashing is not recommended: you may get stuck on a black screen after skipping nux, it is recommended to set up on a slightly newer version and downgrade to factory firmware after doing an ota skip."
                        color: "#e0a885"
                        font.pixelSize: 12
                        wrapMode: Text.WordWrap
                        Layout.fillWidth: true
                    }
                }
            }

            // sideload settings
            GroupBox {
                title: "sideload settings"
                Layout.fillWidth: true
                Layout.fillHeight: true
                ColumnLayout {
                    anchors.fill: parent
                    spacing: 8
                    CheckBox {
                        id: eraseDataCheck
                        text: "erase userdata before install (factory reset)"
                        checked: false
                    }
                    Label {
                        visible: eraseDataCheck.checked
                        text: "warning: this wipes your userdata partition. back up anything important first, EVERYTHING will be deleted."
                        color: "#e08585"
                        font.pixelSize: 12
                        wrapMode: Text.WordWrap
                        Layout.fillWidth: true
                    }
                    CheckBox {
                        id: eraseCacheCheck
                        text: "erase cache before install"
                        checked: false
                    }
                    Label {
                        text: "operation log"
                        color: palette.placeholderText
                        font.pixelSize: 12
                    }
                    ScrollView {
                        Layout.fillWidth: true
                        Layout.fillHeight: true
                        Layout.minimumHeight: 80
                        TextArea {
                            text: Firmware.log
                            readOnly: true
                            wrapMode: Text.WrapAnywhere
                            font.family: "Consolas, monospace"
                            font.pixelSize: 12
                            onTextChanged: cursorPosition = length
                        }
                    }
                }
            }
        }

        // right
        ColumnLayout {
            Layout.fillHeight: true
            Layout.preferredWidth: 2
            Layout.fillWidth: true
            Layout.maximumWidth: 340
            Layout.minimumWidth: 240
            spacing: 12

            // space for buttons
            GroupBox {
                title: "actions"
                Layout.fillWidth: true
                Layout.fillHeight: true
                Layout.preferredHeight: 3
                ColumnLayout {
                    anchors.fill: parent
                    spacing: 8
                    Button {
                        text: "refresh headset"
                        Layout.fillWidth: true
                        enabled: !Device.busy
                        onClicked: Device.refresh()
                    }
                    Button {
                        text: "refresh updates"
                        Layout.fillWidth: true
                        enabled: !fwLoading && Device.connected
                        onClicked: loadFirmware()
                    }
                    Button {
                        id: flashBtn
                        Layout.fillWidth: true
                        text: {
                            var e = selectedEntry();
                            if (!e) return "flash selected";
                            if (e.isFactory) return "flash factory firmware";
                            return selectedIsDowngrade() ? "downgrade to selected" : "flash selected";
                        }
                        enabled: selectedEntry() !== null && !Firmware.busy && Device.connected && !flashBlockedByLock()
                        onClicked: askFlash()
                    }
                    ToolSeparator { Layout.fillWidth: true }
                    Button {
                        text: "reboot to system"
                        Layout.fillWidth: true
                        enabled: Device.connected && !Device.busy && !Firmware.busy
                        onClicked: Device.reboot("system")
                    }
                    Button {
                        text: "reboot to bootloader"
                        Layout.fillWidth: true
                        enabled: Device.connected && !Device.busy && !Firmware.busy
                        onClicked: Device.reboot("bootloader")
                    }
                    Button {
                        text: "clear cache"
                        Layout.fillWidth: true
                        enabled: Device.connected && !Firmware.busy && !Device.busy
                        onClicked: Firmware.clearCache(Device.serial, Device.adbPath, Device.fastbootPath)
                    }
                    Button {
                        id: wipeBtn
                        text: "wipe device"
                        hoverEnabled: true
                        Layout.fillWidth: true
                        enabled: Device.connected && !Firmware.busy && !Device.busy
                        contentItem: Text {
                            text: wipeBtn.text
                            color: "#e04b4b"
                            font: wipeBtn.font
                            horizontalAlignment: Text.AlignHCenter
                            verticalAlignment: Text.AlignVCenter
                            elide: Text.ElideRight
                        }
                        onClicked: {
                            wipeDialog.countdown = 5;
                            wipeTimer.restart();
                            wipeDialog.open();
                        }
                    }
                    ProgressBar {
                        Layout.fillWidth: true
                        visible: Firmware.busy
                        indeterminate: Firmware.progress < 0
                        value: Firmware.progress < 0 ? 0 : Firmware.progress
                    }
                    Label {
                        text: Firmware.status
                        color: palette.placeholderText
                        font.pixelSize: 12
                        wrapMode: Text.WordWrap
                        Layout.fillWidth: true
                    }
                    Item { Layout.fillHeight: true }
                    BusyIndicator {
                        running: Device.busy || Firmware.busy || fwLoading
                        Layout.alignment: Qt.AlignHCenter
                    }
                }
            }

            // space for more buttons
            GroupBox {
                title: "device"
                Layout.fillWidth: true
                ColumnLayout {
                    anchors.fill: parent
                    spacing: 8
                    Button {
                        text: "settings"
                        Layout.fillWidth: true
                        onClicked: settingsDialog.open()
                    }
                    Button {
                        text: "about"
                        Layout.fillWidth: true
                        onClicked: aboutDialog.open()
                    }
                    Button {
                        text: "exit"
                        Layout.fillWidth: true
                        onClicked: Qt.quit()
                    }
                }
            }
        }
    }

    Dialog {
        id: wipeDialog
        title: "wipe device?"
        modal: true
        anchors.centerIn: parent
        property int countdown: 0
        onOpened: { countdown = 5; wipeTimer.restart(); }
        onRejected: wipeTimer.stop()
        ColumnLayout {
            width: 400
            spacing: 8
            Label {
                Layout.fillWidth: true
                color: "#e08585"
                font.bold: true
                wrapMode: Text.WordWrap
                text: "this erases your userdata partition. back up anything important, EVERYTHING will be deleted.."
            }
        }
        footer: RowLayout {
            Item { Layout.fillWidth: true }
            Button {
                text: wipeDialog.countdown > 0 ? "wipe (wait " + wipeDialog.countdown + "s)" : "wipe"
                enabled: !wipeDialog.countdown
                onClicked: {
                    wipeDialog.accept();
                    Firmware.wipeDevice(Device.serial, Device.adbPath, Device.fastbootPath);
                }
            }
            Button {
                text: "cancel"
                onClicked: wipeDialog.reject()
            }
        }
        Timer {
            id: wipeTimer
            interval: 1000
            repeat: true
            onTriggered: {
                wipeDialog.countdown--;
                if (wipeDialog.countdown <= 0) {
                    wipeDialog.countdown = 0;
                    stop();
                }
            }
        }
    }

    Dialog {
        id: settingsDialog
        title: "settings"
        modal: true
        anchors.centerIn: parent
        standardButtons: Dialog.Close
        ColumnLayout {
            width: 420
            spacing: 8
            Label {
                Layout.fillWidth: true
                text: "adb and fastboot executables"
                font.bold: true
            }
            Label {
                Layout.fillWidth: true
                wrapMode: Text.WordWrap
                color: palette.placeholderText
                font.pixelSize: 12
                text: "defaults to the copy bundled with questie. type just a name (like adb) to use one on your PATH."
            }
            RowLayout {
                Layout.fillWidth: true
                Label { text: "adb:"; Layout.preferredWidth: 80 }
                TextField {
                    Layout.fillWidth: true
                    text: Device.adbPath
                    placeholderText: "adb"
                    onEditingFinished: Device.adbPath = text
                }
            }
            RowLayout {
                Layout.fillWidth: true
                Label { text: "fastboot:"; Layout.preferredWidth: 80 }
                TextField {
                    Layout.fillWidth: true
                    text: Device.fastbootPath
                    placeholderText: "fastboot"
                    onEditingFinished: Device.fastbootPath = text
                }
            }
        }
    }

    Dialog {
        id: aboutDialog
        title: "about"
        modal: true
        anchors.centerIn: parent
        standardButtons: Dialog.Close
        ColumnLayout {
            width: 440
            spacing: 14

            RowLayout {
                Layout.fillWidth: true
                spacing: 16
                Image {
                    source: Device.appLogo
                    Layout.preferredWidth: 60
                    Layout.preferredHeight: 60
                    sourceSize.width: 120
                    sourceSize.height: 120
                    fillMode: Image.PreserveAspectFit
                    cache: false
                }
                ColumnLayout {
                    Layout.fillWidth: true
                    spacing: 2
                        RowLayout{
                            Label {
                            text: "questie"
                            font.pixelSize: 26
                            font.bold: true
                        }
                        Label {
                            text: "v1.0.0"
                            font.pixelSize: 16
                            color: palette.placeholderText
                        }
                    }
                    
                    Label {
                        text: "a quest multitool"
                        color: palette.placeholderText
                        font.pixelSize: 14
                    }
                }
            }

            Label {
                Layout.fillWidth: true
                wrapMode: Text.WordWrap
                color: palette.placeholderText
                font.pixelSize: 11
                text: "use at your own risk, i am not responsible for any damage that may occur to your headset."
            }
        }
    }

    Dialog {
        title: "flash update?"
        modal: true
        anchors.centerIn: parent
        standardButtons: Dialog.Ok | Dialog.Cancel
        ColumnLayout {
            width: 380
            spacing: 8
            Label {
                Layout.fillWidth: true
                wrapMode: Text.WordWrap
                text: "install v" + pendingVersion + " (incremental " + pendingIncremental + ")" + (pendingIsDowngrade ? " as a downgrade" : " as an upgrade") + "?\n\nthe headset will reboot to sideload mode. do not unplug it."
            }
            Label {
                visible: eraseDataCheck.checked
                Layout.fillWidth: true
                color: "#e08585"
                font.bold: true
                wrapMode: Text.WordWrap
                text: "warning: this wipes your userdata partition. back up anything important first, EVERTHING will be deleted.."
            }
            Label {
                visible: eraseCacheCheck.checked && !eraseDataCheck.checked
                Layout.fillWidth: true
                text: "the cache partition will be erased before installing."
            }
            Label {
                visible: pendingIsDowngrade
                Layout.fillWidth: true
                text: "this is a downgrade."
            }
        }
        onAccepted: Firmware.flashOta(pendingUrl, pendingIncremental, pendingSha, Device.serial, Device.adbPath, Device.fastbootPath, eraseFlags())
    }

    Dialog {
        id: factoryDialog
        title: "flash factory firmware?"
        modal: true
        anchors.centerIn: parent
        standardButtons: Dialog.Ok | Dialog.Cancel
        ColumnLayout {
            width: 400
            spacing: 8
            Label {
                Layout.fillWidth: true
                wrapMode: Text.WordWrap
                text: "flashing factory firmware directly is not recommended: you might get stuck on a black screen after skipping nux. it is recommended to set up on a slightly newer version and downgrade to factory firmware after doing an ota skip."
            }
            Label {
                visible: eraseDataCheck.checked
                Layout.fillWidth: true
                color: "#e08585"
                font.bold: true
                wrapMode: Text.WordWrap
                text: "warning: this wipes your userdata partition. back up anything important first. EVERYTHING will be deleted"
            }
        }
        onAccepted: Firmware.flashFactory(pendingUrl, Device.serial, Device.adbPath, Device.fastbootPath, eraseFlags())
    }
}
