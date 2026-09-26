// Save Flattened TGA As Variant
//
// Asks for a name and exports the active document as a flattened 32-bit Targa
// (RGBA, 8 bits/channel) called <name>.tga, in the folder of the file it was
// opened from. The working document's layers are left intact and it stays
// linked to its own file, so SaveFlattenedTGA.jsx keeps saving over that one.
//
// Instant Edit adds every extra TGA saved in a texture's working folder as a
// variant: a Penumbra option named after the file. Saving under the same name
// again updates that variant.
//
// Usage: File > Scripts > Browse... and select this file, or install it
// permanently (see README.md in this folder).

#target photoshop

(function () {
    // Set to true if your game/engine reads RLE-compressed TGAs fine and you
    // want smaller files. Left false by default for maximum compatibility.
    var USE_RLE_COMPRESSION = false;

    var TITLE = "Save Flattened TGA As Variant";
    // Photoshop remembers the last name entered under this key.
    var SETTINGS_ID = "instantEditSaveFlattenedTGAVariant";
    var NAME_KEY = stringIDToTypeID("variantName");

    if (app.documents.length === 0) {
        alert(TITLE + ": no document is open.");
        return;
    }

    var doc = app.activeDocument;

    var sourceFile;
    try {
        sourceFile = doc.fullName;
    } catch (e) {
        alert(TITLE + ":\n\n\"" + doc.name + "\" has never been saved to disk.\n" +
              "Save it once so the variant has a folder to go to.");
        return;
    }

    if (doc.bitsPerChannel !== BitsPerChannelType.EIGHT) {
        alert(TITLE + ":\n\n\"" + doc.name + "\" is not 8 bits/channel.\n" +
              "Convert it via Image > Mode > 8 Bits/Channel first, then run this again.");
        return;
    }

    var name = loadLastName();
    var problem = "";
    for (;;) {
        var input = prompt((problem ? problem + "\n\n" : "") + "Variant name (saved as <name>.tga next to " +
                           sourceFile.displayName + "):", name, TITLE);
        if (input === null) return;
        name = cleanName(input);
        problem = nameProblem(name);
        if (!problem) break;
    }

    var tgaOptions = new TargaSaveOptions();
    tgaOptions.resolution = TargaBitsPerPixels.THIRTYTWO; // 8 bits x RGBA
    tgaOptions.alphaChannels = true;
    tgaOptions.rleCompression = USE_RLE_COMPRESSION;

    // asCopy = true: writes a flattened export without altering the open
    // document (layers stay editable, it stays linked to its own file).
    // The name is URI-encoded because File paths use URI notation.
    doc.saveAs(new File(sourceFile.path + "/" + encodeURIComponent(name) + ".tga"),
               tgaOptions, true, Extension.LOWERCASE);
    saveLastName(name);

    // Drops surrounding spaces and a typed ".tga", which the save adds anyway.
    function cleanName(value) {
        return trim(trim(value).replace(/\.tga$/i, ""));
    }

    // The names Instant Edit accepts for a variant, or why this one isn't.
    function nameProblem(value) {
        if (value.length === 0)
            return "Enter a name for the variant.";
        if (value.length > 120)
            return "Keep the name to 120 characters or fewer.";
        if (value === "." || value === ".." || /[\\\/:*?"<>|\x00-\x1f]/.test(value))
            return "\"" + value + "\" isn't a valid file name. Leave out \\ / : * ? \" < > |";
        var lower = value.toLowerCase();
        if (lower === "original")
            return "\"Original\" is reserved for the edited texture. Pick another name.";
        if (lower === "texture" || lower === "snapshot")
            return "Instant Edit uses " + lower + ".tga itself. Pick another name.";
        return "";
    }

    function trim(value) {
        return value.replace(/^\s+|\s+$/g, "");
    }

    function loadLastName() {
        try {
            return app.getCustomOptions(SETTINGS_ID).getString(NAME_KEY);
        } catch (e) {
            return ""; // Nothing saved yet.
        }
    }

    function saveLastName(value) {
        var settings = new ActionDescriptor();
        settings.putString(NAME_KEY, value);
        app.putCustomOptions(SETTINGS_ID, settings, true);
    }
})();
