# SaveFlattenedTGA.jsx

Overwrites the currently open `.tga` with a flattened, 32-bit (8bpc RGBA) copy,
without prompting for a save path and without flattening your working
document's layers. Solves the problem where Photoshop's Actions panel only
records a *static* Save As path/filename.

## Why a script instead of an Action

Photoshop Actions can't read "what file is this document, and where did it
come from" dynamically — a recorded Save As step always targets the exact
path you had open while recording. A script can query `activeDocument`
at run time, so it always saves back to wherever the current document
actually lives.

## One-off use (no install)

`File > Scripts > Browse...` → select `SaveFlattenedTGA.jsx`. Runs immediately
against the active document.

## Permanent menu entry + optional keyboard shortcut

1. Copy `SaveFlattenedTGA.jsx` into Photoshop's Scripts folder:
   - Windows: `C:\Program Files\Adobe\Adobe Photoshop <version>\Presets\Scripts\`
   - macOS: `/Applications/Adobe Photoshop <version>/Presets/Scripts/`
2. Restart Photoshop. It now appears as
   `File > Scripts > SaveFlattenedTGA`.
3. (Optional) `Edit > Keyboard Shortcuts...` → App Menus → `File > Scripts` →
   find the entry → assign a shortcut, e.g. `Ctrl+Alt+S` / `Cmd+Opt+S`.

This is the simplest one-click setup and needs no Action at all.

## Wiring it into an Action (e.g. to bind an F-key via the Actions panel, or bundle with other steps)

1. Actions panel → New Action → Record.
2. `File > Scripts > Browse...` → select `SaveFlattenedTGA.jsx` (or, if
   installed per above, `File > Scripts > SaveFlattenedTGA`).
3. Stop recording.

The action step stores a reference to the script file's path, not the script's
logic, so if you export the `.atn` to share it, send `SaveFlattenedTGA.jsx`
alongside it and keep them at the same relative location the action was
recorded with (or have the recipient install the script per-machine and
re-record the one step). The script itself is the portable part — the Action
is just a convenience trigger.
