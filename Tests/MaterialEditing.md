# Material editor acceptance

The Material window (a material row's menu or palette button in On Screen, Game Files or a mod
window) is covered offline by `Tests/ExportContextRegression` (MaterialEditorScenarios: colour
rows, shader package facts, labels, the document's byte-for-byte round trip over the bundled
presets and, with `IE_MTRL_LIBRARY` set, a whole mod library, and the session's undo, edit kinds
and shader switches). Nothing below has been tried in game yet. Use a test collection and a gear
mod you can restore.

## Window, labels, preview and save (Task 6, pending)

Opening:
- [ ] On Screen: a gear material row has a palette button and "Edit material" in its menu. Both
  open the Material window with the file name, its mod (or "Game file") and no "Unsaved" badge.
- [ ] Every key, value and texture slot shows a label, not a hex id (except the few unnamed ones,
  which say "Unknown value 0x..." with a help text). Values the shader ignores sit in the
  collapsed "Ignored by this shader" group with a Remove button.
- [ ] Game Files: a vanilla material row opens the same window ("Game file").
- [ ] Mod Browser: a mod window's material inspector shows "Open in the Material editor", which
  opens the same window.
- [ ] Opening another material while this one has unsaved changes asks Save first / Discard /
  Cancel.

Preview:
- [ ] Change Alpha cutoff and let go: within a second the character redraws with the change and
  the toolbar reads "Shown on the character". Nothing is written into the mod yet.
- [ ] Switch the shader to Character (Scroll): the character redraws, the keys now include
  "Reflection source", and the message lists what changed.
- [ ] Close the window: the character redraws with its own material again.

Saving:
- [ ] Save on a mod material: the message says it wrote 1 file into the mod (backups kept for N
  days), the badge goes, and the backup shows in the window's "Undo last save" (dots menu).
- [ ] Undo last save: the file from before the save is back, and the window reloads it.
- [ ] Save on a vanilla material: the "New material mod" popup asks for a name; the new mod
  appears in Penumbra, enabled in the character's collection, and the character wears it.
- [ ] Save again after that: it writes into the new mod (with a backup), not a second new mod.
- [ ] Discard changes (dots menu) puts the values back as last saved.

## Colour rows (Task 7, pending)

- [ ] A gear material on character.shpk shows a "Colour rows" tab with 16 pairs of swatches (base,
  shine, glow); clicking a pair shows its two rows side by side with named fields.
- [ ] Change row 1's base colour and let go: the character redraws with the new colour where the
  index texture picks pair 1.
- [ ] Detail scale, rotation and shear change the pattern; Advanced shows the stored matrix.
- [ ] Copy a row and paste it onto another (Everything, Colours only, Surface only): only the
  chosen fields change, and Undo takes it back in one step.
- [ ] A legacy material (characterlegacy.shpk) shows the 16-row table; the dots menu's "Convert to
  Dawntrail" moves it to character.shpk with 32 rows, and the character still looks the same.
- [ ] A character.shpk material with an old 16-row table shows the warning and Convert button.

## Live preview in memory (Task 8, pending; needs XIV Bridge with automation on)

The `material` Bridge tool shows the window's state, the live binding and, with
`{"constant": "0x29AC0223"}`, that value as the loaded material holds it.
- [ ] Open a gear material from On Screen: the toolbar reads "Live"; `material` lists a binding
  (Character model N material M) and a snapshot of 416 bytes or so.
- [ ] Drag Alpha cutoff: the model changes while dragging, with no redraw; `material` with the
  constant shows the loaded value equal to the document's.
- [ ] Change Translucent, Hide backfaces and a texture's wrap mode: each shows at once.
- [ ] Change "Face decal" on a material with a decal: does it show at once? If not, keys need a
  redraw; note it here.
- [ ] Close the window: the character looks as before; reopening and checking `material` shows
  matchesSnapshot true right after binding.
- [ ] Redraw the character (Penumbra) while bound: no crash; the toolbar shows Live again within a
  second and the edits are back on the character.
- [ ] Enter and leave GPose, and change zones, while bound: no crash; Live comes back.
- [ ] Reload the plugin while bound with unsaved edits: no crash; the character shows its own
  material after the reload.

## Colour rows live, and the pair highlight (Task 9, pending; XIV Bridge)

- [ ] Change a row's base colour or roughness: the character changes the same frame, with no
  redraw; `material` shows "tableSlots" with the bound slots showing edited rows.
- [ ] Hover a pair's swatch: its rows pulse on the character (top yellow, bottom blue); moving off
  puts the edited rows back.
- [ ] Close the window: the character's own colours are back (the game's table texture is in the
  slot again; no crash).
- [ ] On dyed gear: the edited rows show undyed until preview dyes exist (Task 12); saving and
  redrawing shows the dye again. Change the dye in Glamourer while bound: no crash, and the live
  rows come back within a second.
- [ ] A redraw, GPose enter/leave and a zone change while rows are edited: no crash; the edited
  rows come back.
