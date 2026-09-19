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

Krita's scripting API (`krita` module) is well-documented and stable across
4.x/5.x, so this is more confidently correct than the GIMP version — but I
still haven't run it against a live Krita install, so test it on a spare
texture copy first.

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
