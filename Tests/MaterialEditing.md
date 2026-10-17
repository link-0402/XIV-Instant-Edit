# Material editor acceptance

The Material window (a material row's menu or palette button in On Screen, Game Files or a mod
window) is covered offline by `Tests/ExportContextRegression` (MaterialEditorScenarios: colour
rows, shader package facts, labels, the document's byte-for-byte round trip over the bundled
presets and, with `IE_MTRL_LIBRARY` set, a whole mod library, and the session's undo, edit kinds
and shader switches). First in-game run 2026-10-17 (XIV Bridge, Critique gown, vanilla Crakows in
the test collection); fixes 678fa15 and 0a2c58d. Use a test collection and a gear mod you can
restore.

## Window, labels, preview and save (Task 6, mostly done)

Opening:
- [x] On Screen: a gear material row has a palette button and "Edit material" in its menu. Both
  open the Material window with the file name, its mod (or "Game file") and no "Unsaved" badge.
  (2026-10-17: palette button on mod and vanilla rows; the menu item not tried.)
- [x] Every key, value and texture slot shows a label, not a hex id (except the few unnamed ones,
  which say "Unknown value 0x..." with a help text). Values the shader ignores sit in the
  collapsed "Ignored by this shader" group with a Remove button. (Critique dress: two "Unknown
  value" rows; it has no ignored values, so the group wasn't seen.)
- [ ] Game Files: a vanilla material row opens the same window ("Game file").
- [ ] Mod Browser: a mod window's material inspector shows "Open in the Material editor", which
  opens the same window.
- [x] Opening another material while this one has unsaved changes asks Save first / Discard /
  Cancel. (Cancel keeps the edits.)

Preview:
- [x] Change Alpha cutoff and let go: within a second the character redraws with the change and
  the toolbar reads "Shown on the character". Nothing is written into the mod yet. (Value edits are
  live since Task 8; a value that can't land live redraws through the preview, checked with a
  Mask wrap change.)
- [x] Switch the shader to Character (Scroll): the character redraws, the keys now include
  "Reflection source", and the message lists what changed.
- [x] Close the window: the character redraws with its own material again.

Saving:
- [x] Save on a mod material: the message says it wrote 1 file into the mod (backups kept for N
  days), the badge goes, and the backup shows in the window's "Undo last save" (dots menu).
  (Checked on the mod the vanilla save made.)
- [x] Undo last save: the file from before the save is back, and the window reloads it.
- [x] Save on a vanilla material: the "New material mod" popup asks for a name; the new mod
  appears in Penumbra, enabled in the character's collection, and the character wears it.
- [x] Save again after that: it writes into the new mod (with a backup), not a second new mod.
- [x] Discard changes (dots menu) puts the values back as last saved.

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
  (Vanilla e6015 Crakows are characterlegacy.shpk with a 32-row Dawntrail table and show 16 pairs.)
- [ ] A character.shpk material with an old 16-row table shows the warning and Convert button.

## Live preview in memory (Task 8, mostly done; needs XIV Bridge with automation on)

The `material` Bridge tool shows the window's state, the live binding and, with
`{"constant": "0x29AC0223"}`, that value as the loaded material holds it.
- [x] Open a gear material from On Screen: the toolbar reads "Live"; `material` lists a binding
  (Character model N material M) and a snapshot of 416 bytes or so. (Failed at first: a mod's
  material handle is named after its mod file, not the game path; fixed in 678fa15.)
- [x] Drag Alpha cutoff: the model changes while dragging, with no redraw; `material` with the
  constant shows the loaded value equal to the document's. (Loaded = document; no visible change
  on the Critique dress, and none after a real reload with the same values either.)
- [x] Change Translucent, Hide backfaces and a texture's wrap mode: each shows at once. (Flags and
  wrap land in memory; wrap writes never matched before 0a2c58d, which maps the sampler CRC to the
  package's id. Mask and Diffuse have no package sampler on character.shpk, so their wrap
  redraws.)
- [x] Change "Face decal" on a material with a decal: does it show at once? If not, keys need a
  redraw; note it here. (Key values written into memory showed nothing on three keys, and
  Penumbra never writes them live: every key edit redraws since 0a2c58d.)
- [x] Close the window: the character looks as before; reopening and checking `material` shows
  matchesSnapshot true right after binding.
- [x] Redraw the character (Penumbra) while bound: no crash; the toolbar shows Live again within a
  second and the edits are back on the character.
- [ ] Enter and leave GPose, and change zones, while bound: no crash; Live comes back.
- [x] Reload the plugin while bound with unsaved edits: no crash; the character shows its own
  material after the reload.

## Colour rows live, and the pair highlight (Task 9, mostly done; XIV Bridge)

- [x] Change a row's base colour or roughness: the character changes the same frame, with no
  redraw; `material` shows "tableSlots" with the bound slots showing edited rows.
- [x] Hover a pair's swatch: its rows pulse on the character (top yellow, bottom blue); moving off
  puts the edited rows back. (Checked by the user.)
- [x] Close the window: the character's own colours are back (the game's table texture is in the
  slot again; no crash). (Also after a plugin reload.)
- [ ] On dyed gear: the edited rows show undyed until preview dyes exist (Task 12); saving and
  redrawing shows the dye again. Change the dye in Glamourer while bound: no crash, and the live
  rows come back within a second. (The dyed Critique dress shows its undyed rows while edited, as
  ruled.)
- [ ] A redraw, GPose enter/leave and a zone change while rows are edited: no crash; the edited
  rows come back. (Redraw passed.)

## Index usage (Task 10, pending)

- [ ] A Stellar Blade-style mod with a 32 x 32 one-colour index texture: two pairs at most are
  bright, the rest greyed; hovering a greyed pair says "Not used by the index texture".
  (Critique dress, a one-colour index: only pair 16 bright, "Used by 100% of the index texture".)
- [x] A vanilla gear material: the pairs its index uses are bright, with their share in the tooltip.
  (e6015 Crakows: pairs 7 and 8.)
- [ ] "Show where" draws the index texture with the selected pair's texels lit (top yellow, bottom
  blue), and follows when another pair is picked.
- [ ] A material whose index slot points at a missing file greys nothing and says so.

## Presets (Task 11, pending)

- [ ] Presets button: ten cards; ones that don't fit the material are disabled with the reason.
- [ ] Metal on a plain gear material (Used rows): the used rows turn metallic the same frame; the
  message names the rows changed and warns if the mask's green channel is rough.
- [ ] Scrolling pattern: the character redraws on the scroll shader and the pattern visibly moves;
  the new `_fx.tex` sits beside the material after Save (in the mod, or in the new mod).
- [ ] Glass, See-through and Matcap each look right after their redraw; Undo takes each back in one
  step.
- [ ] Dyeable adds a dye table (the rows' Dye section appears); Clean up's hover lists what it
  removes.

## Dye rows and preview dyes (Task 12, pending)

The dye template reader is checked offline against the game's own stainingtemplate_gud.stm and
stainingtemplate.stm (43 templates each).
- [ ] On a dyeable gear material, pick a preview dye for Dye 1: the swatches and the character take
  it the same frame.
- [ ] Compare one row's dyed colour with Penumbra's dye preview for the same dye: they match. (Dye
  field 3 is taken as glow exposure; note here if Penumbra shows it elsewhere.)
- [ ] A row's Dye section: change its template, channel and fields; Undo takes each back.
- [ ] Bake: the rows hold the dyed values, and with "No dye" the character still looks dyed.
- [ ] Untick Dyeable: the dye rows go (the character redraws); tick it again: empty dye rows.

## Save as copy into an option (pending)

The places, file names and the meta.json write are checked offline (ModHandoffScenarios.RunCopies).
- [ ] Open a material from a mod window, edit a value, dots menu > Save as copy into an option: the
  list shows Default files and every option; the options using the opened file say "uses this file".
- [ ] Pick another option and Save copy: the message names it; in Penumbra that option now points
  at a new file in its own folder, and the original file and the other options are unchanged.
- [ ] Turn that option on and redraw: the character shows the copy. Save again: it writes the copy,
  not the original.
- [ ] After Scrolling pattern, Save as copy: the new `_fx.tex` goes into the same option.
- [ ] Opened from On Screen or Game Files, the menu has no such item; with the mod window closed it
  is greyed with the reason.

## Finish (Task 13, pending)

- [ ] Dots menu > Clean up ignored settings lists what it would remove first; Clean up applies it as
  one undo step; on a clean material it says there is nothing to clean up.
- [ ] No crash and no stuck preview after: closing the window with unsaved edits, opening another
  material, reloading the plugin, entering and leaving GPose, and a zone change. (Closing with
  edits, opening another and the plugin reload passed.)
