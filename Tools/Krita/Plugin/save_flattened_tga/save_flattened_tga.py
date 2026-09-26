from krita import Krita, Extension, InfoObject
import os

# Krita 5 ships PyQt5 and Krita 6 PyQt6 (and refuses to import PyQt5).
try:
    from PyQt5.QtWidgets import QInputDialog, QMessageBox
except ImportError:
    from PyQt6.QtWidgets import QInputDialog, QMessageBox

TITLE = "Save Flattened TGA"
VARIANT_TITLE = "Save Flattened TGA As Variant"
# kritarc group and key that remember the last variant name entered.
SETTINGS_GROUP = "save_flattened_tga"
VARIANT_NAME_KEY = "variant_name"


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

        action = window.createAction(
            "save_flattened_tga_variant",
            "Save Flattened TGA As Variant...",
            "tools/scripts",
        )
        action.triggered.connect(self.save_flattened_tga_variant)

    def save_flattened_tga(self):
        doc = Krita.instance().activeDocument()

        if doc is None:
            QMessageBox.warning(None, TITLE, "No document is open.")
            return

        filename = doc.fileName()
        if not filename:
            QMessageBox.warning(None, TITLE,
                                 "This document has never been saved/exported to a file.")
            return

        if not filename.lower().endswith(".tga"):
            QMessageBox.warning(None, TITLE,
                                 "\"%s\" is not a .tga file." % os.path.basename(filename))
            return

        export_flattened_tga(doc, filename, TITLE)

    def save_flattened_tga_variant(self):
        """Asks for a name and saves <name>.tga next to the document's file.

        Instant Edit adds every extra TGA saved in a texture's working folder
        as a variant: a Penumbra option named after the file. Saving under the
        same name again updates that variant. The document stays linked to its
        own file, so the in-place action keeps saving over that one.
        """
        app = Krita.instance()
        doc = app.activeDocument()

        if doc is None:
            QMessageBox.warning(None, VARIANT_TITLE, "No document is open.")
            return

        filename = doc.fileName()
        if not filename:
            QMessageBox.warning(None, VARIANT_TITLE,
                                 "This document has never been saved/exported to a file.")
            return

        window = app.activeWindow()
        parent = window.qwindow() if window is not None else None
        name = app.readSetting(SETTINGS_GROUP, VARIANT_NAME_KEY, "")
        problem = ""
        while True:
            label = "Variant name (saved as <name>.tga next to %s):" % os.path.basename(filename)
            name, ok = QInputDialog.getText(parent, VARIANT_TITLE,
                                            problem + "\n\n" + label if problem else label,
                                            text=name)
            if not ok:
                return
            name = clean_variant_name(name)
            problem = variant_name_problem(name)
            if not problem:
                break

        if export_flattened_tga(doc, os.path.join(os.path.dirname(filename), name + ".tga"), VARIANT_TITLE):
            app.writeSetting(SETTINGS_GROUP, VARIANT_NAME_KEY, name)


def export_flattened_tga(doc, filename, title):
    """Writes a flattened 8-bit copy of doc to filename, leaving doc untouched."""
    clone = doc.clone()
    try:
        clone.setBatchmode(True)
        clone.flatten()

        if clone.colorDepth() != "U8":
            clone.setColorSpace(clone.colorModel(), "U8", clone.colorProfile())

        exported = clone.exportImage(filename, InfoObject())
    finally:
        clone.close()

    if not exported:
        QMessageBox.warning(None, title, "Could not write \"%s\"." % filename)
    return exported


def clean_variant_name(name):
    """Drops surrounding spaces and a typed ".tga", which the save adds anyway."""
    name = name.strip()
    if name.lower().endswith(".tga"):
        name = name[:-4].strip()
    return name


def variant_name_problem(name):
    """Why Instant Edit can't use name for a variant, or "" when it can."""
    if not name:
        return "Enter a name for the variant."
    if len(name) > 120:
        return "Keep the name to 120 characters or fewer."
    if name in (".", "..") or any(c in '\\/:*?"<>|' or ord(c) < 32 for c in name):
        return "\"%s\" isn't a valid file name. Leave out \\ / : * ? \" < > |" % name
    lower = name.lower()
    if lower == "original":
        return "\"Original\" is reserved for the edited texture. Pick another name."
    if lower in ("texture", "snapshot"):
        return "Instant Edit uses %s.tga itself. Pick another name." % lower
    return ""
