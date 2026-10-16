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
