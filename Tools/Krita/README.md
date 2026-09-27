# Save Flattened TGA (In Place) — Krita

Krita equivalent of the Photoshop/GIMP scripts in this repo: exports the
active document as a flattened, 32-bit (8bpc RGBA) TGA, overwriting the file
it was opened from, without touching the open document's layers.

## How it works

1. `doc.clone()` — duplicates the document in memory; the real one stays
   open, untouched, with all its layers.
2. `clone.flatten()` on the clone only.
3. Forces 8 bits/channel if the document is at a higher depth.
4. `clone.exportImage(filename, InfoObject())` writes back to the exact path
   read from `doc.fileName()` — never a hardcoded path.
5. `clone.close()` discards the clone.

It supports Krita 5.x and 6.x: Krita 5 ships PyQt5 and Krita 6 PyQt6, and the
plugin imports whichever is there. Krita's scripting API (`krita` module) is
well-documented and stable across versions, so this is more confidently
correct than the GIMP version — but I still haven't run it against a live
Krita install, so test it on a spare texture copy first.

## Install from Instant Edit

The first-time setup and **Settings > Texture editing** find Krita and offer **Install save
scripts**. It copies the plugin into the `pykrita` folder of Krita's resource folder (the one
set in Krita's settings, `%APPDATA%\krita` by default) and enables it in `kritarc`, so it is
active after the next Krita start without a visit to the Python Plugin Manager.

## Option A — one-off, no install

Open `Tools > Scripting > Scripter`, paste in
`ScripterSaveFlattenedTGA.py`, click Run. Good for trying it out or for
occasional use.

## Option B — permanent menu entry + keyboard shortcut

1. Copy the whole `Plugin/` folder's contents into Krita's `pykrita` resource
   folder, so you end up with:
   - `<resource folder>/pykrita/save_flattened_tga.desktop`
   - `<resource folder>/pykrita/save_flattened_tga/__init__.py`
   - `<resource folder>/pykrita/save_flattened_tga/save_flattened_tga.py`

   Find your resource folder via `Settings > Manage Resources > Open
   Resource Folder` in Krita (Windows default is usually
   `%APPDATA%\krita\pykrita\`).
2. Restart Krita.
3. `Settings > Configure Krita > Python Plugin Manager` → enable
   "Save Flattened TGA" → restart Krita again.
4. It now appears under `Tools > Scripts > Save Flattened TGA (In Place)`.
5. `Settings > Configure Shortcuts` → search "Save Flattened TGA" → assign a
   key, e.g. `Ctrl+Alt+S`.
