#!/usr/bin/env python3
"""Graphical, per-user AppImage setup. No privileged installation or account access."""

import argparse
import contextlib
import ctypes.util
import fcntl
import io
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import traceback

import install

DESKTOPS = {"gnome": "GNOME", "kde": "KDE Plasma", "hyprland": "Hyprland / Waybar"}
GUIDANCE = {
    "gnome": "The native GNOME popup and preferences will be installed. Setup will attempt to enable "
             "the extension. GNOME may require a logout/login to discover it; updates require one "
             "to reload its code. No general tray extension will be changed.",
    "kde": "The native Plasma widget will be installed. After setup, right-click your panel, choose "
           "Add Widgets, and add GHCPSpendTray. Log out/in after updates to reload its code.",
    "hyprland": "Setup will reuse Waybar's tray, or add it to your selected bar with your consent, preserving "
                "comments and includes. Owned legacy custom indicators are migrated. Monitoring starts now; "
                "login startup is opt-in in General settings. Reload Waybar yourself, then click a native "
                "icon for the Quickshell popup. Setup never starts or restarts Waybar.",
}
COMPLETION_GUIDANCE = {
    "gnome": "Log out of your desktop and back in to load the installed GNOME panel. "
             "Re-enabling the extension does not reload its code. Your saved accounts are unchanged. "
             "Then enable GHCPSpendTray in Extensions if it is not already enabled.",
    "kde": "Log out of your desktop and back in to load the installed widget code. "
           "Then add GHCPSpendTray from your panel's Add Widgets menu if it is not already present.",
    "hyprland": "Reload Waybar, then click your new indicator.",
}


host_environment = install.host_environment


def detected_desktop():
    desktop = os.environ.get("XDG_CURRENT_DESKTOP", "").lower()
    if "hyprland" in desktop:
        return "hyprland"
    if "kde" in desktop or "plasma" in desktop:
        return "kde"
    if "gnome" in desktop:
        return "gnome"
    return None


def data_home():
    if platform.system() != "Linux" or os.getuid() == 0:
        raise RuntimeError("Run GHCPSpendTray as your normal desktop user, not root.")
    data = Path(os.environ.get("XDG_DATA_HOME", str(Path.home() / ".local/share")))
    if not data.is_absolute():
        raise RuntimeError("XDG_DATA_HOME must be an absolute path.")
    return data.resolve()


def missing_dependencies(desktop):
    required = {"gnome": ["gdbus", "gnome-extensions"], "kde": ["gdbus", "plasmashell"],
                "hyprland": ["gdbus", "waybar", "quickshell"]}
    missing = [command for command in required[desktop] if shutil.which(command) is None]
    if ctypes.util.find_library("secret-1") is None:
        missing.append("libsecret-1")
    return missing


def perform(action, desktop, config=None, bar=None, files_only=False):
    data = data_home()
    data.mkdir(parents=True, exist_ok=True)
    # Serialize all setup windows, including ones opened from different downloaded versions.
    with (data / ".ghcp-spend-tray-setup.lock").open("a") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise RuntimeError("Another GHCPSpendTray setup operation is running.") from error
        if action == "remove":
            install.uninstall(data, files_only)
            return
        missing = missing_dependencies(desktop) if not files_only else []
        if missing:
            raise RuntimeError("Missing desktop components: " + ", ".join(missing) +
                               ". Install these with your distribution's software manager, then retry.")
        appdir = Path(os.environ.get("GHCP_APPDIR", ""))
        appimage = Path(os.environ["APPIMAGE"]) if os.environ.get("APPIMAGE") else None
        if not (appdir / "payload/architecture").is_file():
            raise RuntimeError("Open the downloaded GHCPSpendTray AppImage to install or update.")
        if (appdir / "payload/architecture").read_text().strip() != platform.machine():
            raise RuntimeError("This AppImage was built for a different CPU architecture.")
        install.install(appdir / "payload", data, files_only, desktop, config, bar, appdir, appimage)


def gui():
    from PySide6.QtCore import Qt, QThread, Signal, QTimer
    from PySide6.QtGui import QIcon
    from PySide6.QtWidgets import (QApplication, QButtonGroup, QCheckBox, QComboBox, QFileDialog,
                                   QFormLayout, QFrame, QHBoxLayout, QLabel, QLineEdit, QMainWindow,
                                   QMessageBox, QPlainTextEdit, QProgressBar, QPushButton,
                                   QScrollArea, QVBoxLayout, QWidget)
    from appearance import stylesheet

    class Worker(QThread):
        result = Signal(bool, str)

        def __init__(self, action, desktop, config, bar, parent):
            super().__init__(parent)
            self.arguments = action, desktop, config, bar

        def run(self):
            output = io.StringIO()
            try:
                with contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
                    perform(*self.arguments)
            except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
                self.result.emit(False, output.getvalue() + "\n" + str(error))
            except Exception:
                self.result.emit(False, output.getvalue() + "\nUnexpected setup error:\n" + traceback.format_exc())
            else:
                self.result.emit(True, output.getvalue())

    class Window(QMainWindow):
        def __init__(self):
            super().__init__()
            self.worker = None
            self.operation = None
            self.setWindowTitle("GHCPSpendTray Setup")
            self.resize(730, 740)
            self.setMinimumSize(660, 620)
            icon_path = Path(os.environ.get("GHCP_APPDIR", "")) / "ghcp-spend-tray.svg"
            self.setWindowIcon(QIcon(str(icon_path)))
            container = QWidget()
            container.setObjectName("page")
            self.setCentralWidget(container)
            outer = QVBoxLayout(container)
            outer.setContentsMargins(0, 0, 0, 0)
            outer.setSpacing(0)
            scroll = QScrollArea()
            scroll.setWidgetResizable(True)
            scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
            outer.addWidget(scroll, 1)
            content = QWidget()
            content.setObjectName("scroll-content")
            scroll.setWidget(content)
            layout = QVBoxLayout(content)
            layout.setContentsMargins(32, 28, 32, 24)
            layout.setSpacing(18)
            brand_row = QHBoxLayout()
            logo = QLabel()
            logo.setPixmap(self.windowIcon().pixmap(36, 36))
            logo.setFixedSize(36, 36)
            brand_row.addWidget(logo)
            brand = QLabel("GHCPSpendTray")
            brand.setObjectName("brand")
            brand_row.addWidget(brand)
            brand_row.addStretch()
            badge = QLabel("LINUX PREVIEW")
            badge.setObjectName("badge")
            brand_row.addWidget(badge)
            layout.addLayout(brand_row)
            layout.addSpacing(4)
            heading = QLabel("Copilot usage, right in your panel.")
            heading.setObjectName("heading")
            heading.setWordWrap(True)
            layout.addWidget(heading)
            subtitle = QLabel("A native popup for your desktop. No separate app to keep open.")
            subtitle.setObjectName("muted")
            subtitle.setWordWrap(True)
            layout.addWidget(subtitle)
            section = QLabel("CHOOSE YOUR DESKTOP")
            section.setObjectName("section-label")
            layout.addWidget(section)
            self.desktop = QComboBox()
            self.desktop.hide()
            self.desktop.addItem("Choose your desktop…", None)
            for key, name in DESKTOPS.items():
                self.desktop.addItem(name, key)
            self.desktop.setCurrentIndex(max(0, self.desktop.findData(detected_desktop())))
            choices = QHBoxLayout()
            choices.setSpacing(12)
            self.desktop_buttons = QButtonGroup(self)
            for index, (key, name) in enumerate(DESKTOPS.items(), 1):
                description = {"gnome": "Shell extension", "kde": "Plasma widget", "hyprland": "Waybar + Quickshell"}[key]
                button = QPushButton(name + "\n" + description)
                button.setObjectName("desktop-card")
                button.setAccessibleName(name + " desktop integration")
                button.setMinimumHeight(76)
                button.setCheckable(True)
                self.desktop_buttons.addButton(button, index)
                choices.addWidget(button, 1)
            self.desktop_buttons.idClicked.connect(self.desktop.setCurrentIndex)
            layout.addLayout(choices)
            integration = QFrame()
            integration.setObjectName("integration")
            integration_layout = QVBoxLayout(integration)
            integration_layout.setContentsMargins(20, 18, 20, 18)
            integration_layout.setSpacing(10)
            self.integration_title = QLabel()
            self.integration_title.setObjectName("integration-title")
            integration_layout.addWidget(self.integration_title)
            self.guidance = QLabel()
            self.guidance.setWordWrap(True)
            self.guidance.setObjectName("muted")
            integration_layout.addWidget(self.guidance)
            layout.addWidget(integration)
            self.dependencies = QLabel()
            self.dependencies.setObjectName("warning")
            self.dependencies.setWordWrap(True)
            layout.addWidget(self.dependencies)
            self.options_button = QPushButton("Installation options  ›")
            self.options_button.setObjectName("text-button")
            self.options_button.setCheckable(True)
            self.options_button.setCursor(Qt.CursorShape.PointingHandCursor)
            layout.addWidget(self.options_button, 0, Qt.AlignmentFlag.AlignLeft)
            self.options = QWidget()
            options_layout = QVBoxLayout(self.options)
            options_layout.setContentsMargins(0, 0, 0, 0)
            self.config_box = QWidget()
            config_form = QFormLayout(self.config_box)
            config_form.setContentsMargins(0, 0, 0, 0)
            self.config = QLineEdit()
            self.config.setPlaceholderText("Auto-detect existing Waybar config")
            browse = QPushButton("Browse…")
            browse.clicked.connect(self.browse)
            row = QHBoxLayout()
            row.addWidget(self.config)
            row.addWidget(browse)
            config_form.addRow("Waybar config", row)
            self.bar = QLineEdit()
            self.bar.setPlaceholderText("Required for multiple bars (first bar = 0)")
            config_form.addRow("Bar index", self.bar)
            options_layout.addWidget(self.config_box)
            self.location = QLabel()
            self.location.setObjectName("notice")
            self.location.setWordWrap(True)
            self.location.setTextInteractionFlags(Qt.TextInteractionFlag.TextSelectableByMouse)
            options_layout.addWidget(self.location)
            self.options.hide()
            self.options_button.toggled.connect(self.toggle_options)
            layout.addWidget(self.options)
            self.consent = QCheckBox("Allow installation and the desktop changes described above")
            self.consent.setObjectName("consent")
            layout.addWidget(self.consent)
            preview = QLabel("Sign in from the native controls after installation. Credentials require an unlocked "
                             "Secret Service provider: GNOME Keyring or KWallet with Secret Service enabled. "
                             "Removing the integration retains account data; remove accounts in native controls first.")
            preview.setObjectName("notice")
            preview.setWordWrap(True)
            layout.addWidget(preview)
            self.result_label = QLabel()
            self.result_label.setWordWrap(True)
            self.result_label.hide()
            layout.addWidget(self.result_label)
            self.details_button = QPushButton("Show details")
            self.details_button.setObjectName("text-button")
            self.details_button.setCheckable(True)
            self.details_button.hide()
            layout.addWidget(self.details_button, 0, Qt.AlignmentFlag.AlignLeft)
            self.status = QPlainTextEdit()
            self.status.setReadOnly(True)
            self.status.setObjectName("status")
            self.status.setMaximumHeight(150)
            self.status.hide()
            self.details_button.toggled.connect(self.status.setVisible)
            layout.addWidget(self.status)
            layout.addStretch()
            footer = QFrame()
            footer.setObjectName("footer")
            footer_layout = QVBoxLayout(footer)
            footer_layout.setContentsMargins(32, 18, 32, 18)
            self.progress = QProgressBar()
            self.progress.setRange(0, 0)
            self.progress.setTextVisible(False)
            self.progress.hide()
            footer_layout.addWidget(self.progress)
            actions = QHBoxLayout()
            self.install_button = QPushButton("Install integration")
            self.install_button.setObjectName("install-button")
            self.install_button.clicked.connect(lambda: self.start("install"))
            self.remove_button = QPushButton("Remove…")
            self.remove_button.setObjectName("text-button")
            self.remove_button.clicked.connect(self.remove)
            self.preferences = QPushButton("Open native controls")
            self.preferences.clicked.connect(self.native_controls)
            actions.addWidget(self.remove_button)
            actions.addStretch()
            actions.addWidget(self.preferences)
            actions.addWidget(self.install_button)
            footer_layout.addLayout(actions)
            outer.addWidget(footer)
            self.desktop.currentIndexChanged.connect(self.change_desktop)
            self.consent.toggled.connect(self.refresh)
            self.refresh()

        def toggle_options(self, opened):
            self.options.setVisible(opened)
            self.options_button.setText("Installation options  ⌄" if opened else "Installation options  ›")

        def change_desktop(self):
            self.consent.setChecked(False)
            self.refresh()

        def refresh(self):
            desktop = self.desktop.currentData()
            button = self.desktop_buttons.button(self.desktop.currentIndex())
            if button:
                button.setChecked(True)
            self.integration_title.setText(DESKTOPS.get(desktop, "Your desktop") + " integration")
            self.config_box.setVisible(desktop == "hyprland")
            self.guidance.setText(GUIDANCE.get(desktop, "Choose the desktop whose panel you want to use."))
            missing = missing_dependencies(desktop) if desktop else []
            self.dependencies.setVisible(bool(missing))
            self.dependencies.setText("Needs " + ", ".join(missing) +
                                      ". Add these through your software manager before continuing.")
            try:
                data = data_home()
                self.location.setText("Install location: " + str(data / "ghcp-spend-tray-desktop") +
                                      "\nUpdates: open a newer downloaded AppImage and choose Install / Update.")
                installed = (data / "ghcp-spend-tray-demo" / install.MARKER).is_file()
                self.remove_button.setVisible(installed)
                self.preferences.setVisible(installed)
                self.preferences.setEnabled(installed and desktop is not None)
                self.install_button.setText("Update integration" if installed else "Install integration")
                self.install_button.setEnabled(bool(desktop) and not missing and self.consent.isChecked())
            except RuntimeError as error:
                self.location.setText(str(error))
                self.install_button.setEnabled(False)
                self.remove_button.setEnabled(False)
                self.preferences.setEnabled(False)

        def show_result(self, summary, details=""):
            self.result_label.setText(summary)
            self.result_label.show()
            self.status.setPlainText(details)
            self.details_button.setVisible(bool(details))
            self.details_button.setChecked(False)
            self.status.hide()

        def browse(self):
            filename, _ = QFileDialog.getOpenFileName(self, "Choose existing Waybar configuration",
                                                     str(Path.home() / ".config/waybar"), "All files (*)")
            if filename:
                self.config.setText(filename)

        def remove(self):
            if QMessageBox.question(self, "Remove GHCPSpendTray?",
                                    "Remove all installed GHCPSpendTray desktop integrations, helper and "
                                    "AppImage copy? Owned Waybar changes will be reversed. Other settings "
                                    "and the original download are kept.") == QMessageBox.StandardButton.Yes:
                self.start("remove")

        def start(self, action):
            if self.worker is not None:
                return
            try:
                bar = int(self.bar.text()) if self.bar.text().strip() else None
                if bar is not None and bar < 0:
                    raise ValueError()
            except ValueError:
                self.options_button.setChecked(True)
                self.show_result("Choose a valid bar index (0 or greater).")
                return
            self.centralWidget().setEnabled(False)
            self.operation = action
            self.progress.show()
            self.show_result("Setting up your panel…" if action == "install" else "Removing the integration…")
            self.worker = Worker(action, self.desktop.currentData(),
                                 Path(self.config.text()) if self.config.text().strip() else None, bar, self)
            self.worker.result.connect(self.completed)
            self.worker.finished.connect(self.worker.deleteLater)
            self.worker.finished.connect(lambda: setattr(self, "worker", None))
            self.worker.start()

        def completed(self, success, text):
            self.centralWidget().setEnabled(True)
            self.progress.hide()
            self.consent.setChecked(False)
            self.refresh()
            if success:
                summary = ("Integration removed. Your other settings are unchanged." if self.operation == "remove" else
                           "Files installed. " + COMPLETION_GUIDANCE[self.desktop.currentData()])
            else:
                summary = "Setup couldn’t finish. " + (text.strip().splitlines()[-1] if text.strip() else "See details below.")
            self.show_result(summary, text)

        def native_controls(self):
            desktop = self.desktop.currentData()
            try:
                if desktop == "gnome":
                    subprocess.Popen(["gnome-extensions", "prefs", install.UUID], env=host_environment())
                elif desktop == "hyprland":
                    subprocess.Popen(install.panel_command(data_home() / "ghcp-spend-tray-hyprland", "toggle"),
                                     env=host_environment())
                else:
                    self.show_result("Right-click your panel and choose Add Widgets.",
                                     GUIDANCE["kde"] + "\nThe widget's Settings button opens its native configuration.")
            except OSError as error:
                self.show_result("Could not open native controls.", str(error))

        def closeEvent(self, event):
            if self.worker is not None and self.worker.isRunning():
                event.ignore()
            else:
                event.accept()

    application = QApplication(sys.argv[:1])
    application.setApplicationName("GHCPSpendTray Setup")
    checkmark = (Path(sys._MEIPASS) / "ui/check.svg" if getattr(sys, "frozen", False)
                 else Path(__file__).with_name("check.svg"))
    def apply_theme():
        dark = application.styleHints().colorScheme() == Qt.ColorScheme.Dark
        application.setStyleSheet(stylesheet(dark, checkmark))
    apply_theme()
    application.styleHints().colorSchemeChanged.connect(apply_theme)
    window = Window()
    window.show()
    if "--smoke-ui" in sys.argv:
        # Exercise the actual frozen GUI without accepting installation or touching the desktop.
        def smoke():
            if (window.consent.isChecked() or window.install_button.isEnabled() or
                    window.windowIcon().isNull() or window.windowIcon().pixmap(36, 36).isNull()):
                application.exit(1)
                return
            for index in range(1, window.desktop.count()):
                window.desktop.setCurrentIndex(index)
                if window.install_button.isEnabled() or not window.desktop_buttons.button(index).isChecked():
                    application.exit(1)
                    return
                window.consent.setChecked(True)
                if window.install_button.isEnabled() != (not missing_dependencies(window.desktop.currentData())):
                    application.exit(1)
                    return
                window.consent.setChecked(False)
            window.options_button.setChecked(True)
            if not window.options.isVisible():
                application.exit(1)
                return
            window.options_button.setChecked(False)
            window.desktop.setCurrentIndex(window.desktop.findData("gnome"))
            window.operation = "install"
            window.completed(True, "Synthetic installation result.")
            if ("Log out of your desktop and back in" not in window.result_label.text() or
                    "if needed" in window.result_label.text()):
                application.exit(1)
                return
            window.show_result("Synthetic failure preview", "Synthetic diagnostic details.")
            if not window.result_label.isVisible() or window.status.isVisible():
                application.exit(1)
                return
            window.details_button.setChecked(True)
            if not window.status.isVisible():
                application.exit(1)
                return
            window.details_button.setChecked(False)
            window.result_label.hide()
            window.details_button.hide()
            window.desktop.setCurrentIndex(max(0, window.desktop.findData(detected_desktop())))
            def finish():
                screenshot = os.environ.get("GHCP_SETUP_SCREENSHOT")
                if screenshot:
                    window.grab().save(screenshot)
                print("PASS: Graphical setup loads all desktop choices; installation requires consent.")
                application.quit()
            QTimer.singleShot(100, finish)
        QTimer.singleShot(300, smoke)
    return application.exec()


def main():
    if "--panel" in sys.argv:
        import panel
        os.environ.clear()
        os.environ.update(HOST_ENVIRONMENT)
        sys.argv = [sys.argv[0], *sys.argv[2:]]
        panel.main()
        return 0
    if "--install" in sys.argv or "--remove" in sys.argv:
        parser = argparse.ArgumentParser()
        parser.add_argument("--install", action="store_true")
        parser.add_argument("--remove", action="store_true")
        parser.add_argument("--desktop", choices=list(DESKTOPS), default="gnome")
        parser.add_argument("--files-only", action="store_true")
        parser.add_argument("--waybar-config", type=Path)
        parser.add_argument("--waybar-bar", type=int)
        args = parser.parse_args()
        os.environ.clear()
        os.environ.update(HOST_ENVIRONMENT)
        perform("remove" if args.remove else "install", args.desktop,
                args.waybar_config, args.waybar_bar, args.files_only)
        return 0
    return gui()


HOST_ENVIRONMENT = host_environment()

if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print(f"GHCPSpendTray setup: {error}", file=sys.stderr)
        sys.exit(1)
