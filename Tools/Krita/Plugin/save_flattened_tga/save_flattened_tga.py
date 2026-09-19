from krita import Krita, Extension, InfoObject
from PyQt5.QtWidgets import QMessageBox
import os


class SaveFlattenedTGAExtension(Extension):
    def __init__(self, parent):
        super().__init__(parent)

    def setup(self):
        pass

    def createActions(self, window):
        action = window.createAction(
            "save_flattened_tga",
            "Save Flattened TGA (In Place)",
            "tools/scripts",
        )
        action.triggered.connect(self.save_flattened_tga)

    def save_flattened_tga(self):
        app = Krita.instance()
        doc = app.activeDocument()

        if doc is None:
            QMessageBox.warning(None, "Save Flattened TGA", "No document is open.")
            return

        filename = doc.fileName()
        if not filename:
            QMessageBox.warning(None, "Save Flattened TGA",
                                 "This document has never been saved/exported to a file.")
            return

        if not filename.lower().endswith(".tga"):
            QMessageBox.warning(None, "Save Flattened TGA",
                                 "\"%s\" is not a .tga file." % os.path.basename(filename))
            return

        clone = doc.clone()
        clone.setBatchmode(True)
        clone.flatten()

        if clone.colorDepth() != "U8":
            clone.setColorSpace(clone.colorModel(), "U8", clone.colorProfile())

        clone.exportImage(filename, InfoObject())
        clone.close()
