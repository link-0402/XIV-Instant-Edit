// Save Flattened TGA (In Place)
//
// Exports the active document as a flattened 32-bit Targa (RGBA, 8 bits/channel),
// overwriting the file it was opened from. The working document's layers are
// left intact (equivalent to checking "Save a Copy" in the Save As dialog) so
// you can keep editing after exporting.
//
// Usage: File > Scripts > Browse... and select this file, or install it
// permanently (see README.md in this folder).

#target photoshop

(function () {
    // Set to true if your game/engine reads RLE-compressed TGAs fine and you
    // want smaller files. Left false by default for maximum compatibility.
    var USE_RLE_COMPRESSION = false;

    if (app.documents.length === 0) {
        alert("Save Flattened TGA: no document is open.");
        return;
    }

    var doc = app.activeDocument;

    var targetFile;
    try {
        targetFile = doc.fullName;
    } catch (e) {
        alert("Save Flattened TGA:\n\n\"" + doc.name + "\" has never been saved to disk.\n" +
              "Save it once as a .tga before using this script.");
        return;
    }

    var ext = targetFile.name.split(".").pop().toLowerCase();
    if (ext !== "tga") {
        alert("Save Flattened TGA:\n\n\"" + targetFile.name + "\" is not a .tga file.\n" +
              "This script only overwrites Targa files it finds already saved on disk.");
        return;
    }

    if (doc.bitsPerChannel !== BitsPerChannelType.EIGHT) {
        alert("Save Flattened TGA:\n\n\"" + doc.name + "\" is not 8 bits/channel.\n" +
              "Convert it via Image > Mode > 8 Bits/Channel first, then run this again.");
        return;
    }

    var tgaOptions = new TargaSaveOptions();
    tgaOptions.resolution = TargaBitsPerPixels.THIRTYTWO; // 8 bits x RGBA
    tgaOptions.alphaChannels = true;
    tgaOptions.rleCompression = USE_RLE_COMPRESSION;

    // asCopy = true: writes a flattened export to targetFile without altering
    // the open document (layers stay editable, "unsaved changes" state is unchanged).
    doc.saveAs(targetFile, tgaOptions, true, Extension.LOWERCASE);
})();
