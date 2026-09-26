"""The "XIV Instant Edit" dock and the plugins-toolbar button."""

from PySide6 import QtCore, QtWidgets

import substance_painter.ui


class Panel:
    def __init__(self, on_send):
        self.widget = QtWidgets.QWidget()
        self.widget.setObjectName("XivInstantEditPanel")
        self.widget.setWindowTitle("XIV Instant Edit")
        layout = QtWidgets.QVBoxLayout(self.widget)
        self.title = QtWidgets.QLabel("No Instant Edit project open")
        self.title.setWordWrap(True)
        font = self.title.font()
        font.setBold(True)
        self.title.setFont(font)
        self.status = QtWidgets.QLabel("")
        self.status.setWordWrap(True)
        self.send_button = QtWidgets.QPushButton("Send to game")
        self.send_button.setToolTip("Export this project's textures and apply them in game through Instant Edit.")
        self.send_button.clicked.connect(on_send)
        self.log = QtWidgets.QPlainTextEdit()
        self.log.setReadOnly(True)
        self.log.setMaximumBlockCount(300)
        layout.addWidget(self.title)
        layout.addWidget(self.status)
        layout.addWidget(self.send_button)
        layout.addWidget(self.log, 1)
        self.dock = substance_painter.ui.add_dock_widget(self.widget)

        self.toolbar_button = QtWidgets.QToolButton()
        self.toolbar_button.setText("XIV")
        self.toolbar_button.setToolTip("Send textures to the game (XIV Instant Edit)")
        self.toolbar_button.clicked.connect(on_send)
        substance_painter.ui.add_plugins_toolbar_widget(self.toolbar_button)
        self.set_job(None)

    def set_job(self, title) -> None:
        self.title.setText(title or "No Instant Edit project open")
        enabled = title is not None
        self.send_button.setEnabled(enabled)
        self.toolbar_button.setEnabled(enabled)

    def set_busy(self, busy: bool) -> None:
        self.send_button.setEnabled(not busy and self.title.text() != "No Instant Edit project open")
        self.toolbar_button.setEnabled(self.send_button.isEnabled())

    def set_status(self, text: str) -> None:
        self.status.setText(text)

    def add_log(self, text: str) -> None:
        stamp = QtCore.QTime.currentTime().toString("HH:mm:ss")
        for line in text.splitlines() or [""]:
            self.log.appendPlainText(f"{stamp}  {line}")

    def show(self) -> None:
        if self.dock is not None:
            self.dock.show()
            self.dock.raise_()

    def confirm(self, title: str, text: str) -> bool:
        answer = QtWidgets.QMessageBox.question(substance_painter.ui.get_main_window(), title, text,
                                                QtWidgets.QMessageBox.Yes | QtWidgets.QMessageBox.No,
                                                QtWidgets.QMessageBox.No)
        return answer == QtWidgets.QMessageBox.Yes

    def close(self) -> None:
        for element in (self.toolbar_button, self.dock):
            try:
                substance_painter.ui.delete_ui_element(element)
            except Exception:
                pass
