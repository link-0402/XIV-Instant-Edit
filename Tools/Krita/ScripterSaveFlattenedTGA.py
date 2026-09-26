# Save Flattened TGA (In Place) - one-off version
#
# Paste into Krita's Tools > Scripting > Scripter, then press "Run".
# Exports the active document as a flattened 32-bit Targa (RGBA, 8 bits/
# channel), overwriting the file it was opened from. Runs on a clone of the
# document, so your open document's layers are left untouched.
#
# For a permanent Tools > Scripts menu entry + keyboard shortcut instead of
# pasting this in each time, see Plugin/ in this folder. The plugin also has a
# second action that saves a copy under a name you enter, for Instant Edit's
# texture variants.

from krita import Krita, InfoObject
import os

# Krita 5 ships PyQt5 and Krita 6 PyQt6 (and refuses to import PyQt5).
try:
    from PyQt5.QtWidgets import QMessageBox
except ImportError:
    from PyQt6.QtWidgets import QMessageBox


def save_flattened_tga():
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


save_flattened_tga()
