# SaveFlattenedTGA.scm (GIMP)

Script-Fu equivalent of `Tools/Photoshop/SaveFlattenedTGA.jsx`: overwrites the
currently open `.tga` with a flattened, 32-bit (8bpc RGBA) copy, without a
static output path and without touching your working image's layers.

## How it works

GIMP has no direct "flatten a copy on export, leave the real image alone"
option like Photoshop's Save As dialog. The script gets the same effect by:

1. Duplicating the active image (`gimp-image-duplicate`) — the original stays
   open, untouched, with all its layers.
2. Merging the duplicate's visible layers into one
   (`gimp-image-merge-visible-layers`, not `gimp-image-flatten` — flatten
   would discard the alpha channel, which a 32-bit TGA needs).
3. Forcing 8 bits/channel and making sure the result has an alpha channel.
4. Exporting over the original file path (read from the image itself via
   `gimp-image-get-filename`, not hardcoded).
5. Deleting the duplicate.

## Install

1. Copy `SaveFlattenedTGA.scm` into GIMP's scripts folder:
   - Windows: `%APPDATA%\GIMP\2.10\scripts\`
   - macOS/Linux: `~/.config/GIMP/2.10/scripts/`
2. `Filters > Script-Fu > Refresh Scripts` (or restart GIMP).
3. It now appears as `File > Export > Save Flattened TGA (In Place)`.
4. (Optional) `Edit > Keyboard Shortcuts...` → search "Save Flattened" →
   assign a shortcut.

## Before you rely on it

I don't have GIMP installed on this machine, so this hasn't been run against
a live PDB — verify it the same way you'd verify any native-side assumption:
open `Filters > Script-Fu > Console` and run the script once on a spare
texture copy. The one line most likely to need adjusting for your GIMP
version is the `PRECISION-U8-NON-LINEAR` constant on the
`gimp-image-convert-precision` call — the script's header comment explains
how to look up the correct name via the Procedure Browser if it errors.

GIMP 3.0 changed significant parts of the scripting API; this targets 2.10.x.
