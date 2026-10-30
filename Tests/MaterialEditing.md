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
- [x] Game Files: a vanilla material row opens the same window ("Game file"). (Hairstyle 1; "Not
  on screen", as it isn't worn.)
- [x] Mod Browser: a mod window's material inspector shows "Open in the Material editor", which
  opens the same window. (Strawberry Halo copy.)
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

## Colour rows (Task 7, mostly done)

- [x] A gear material on character.shpk shows a "Colour rows" tab with 16 pairs of swatches (base,
  shine, glow); clicking a pair shows its two rows side by side with named fields.
- [x] Change row 1's base colour and let go: the character redraws with the new colour where the
  index texture picks pair 1. (Live since Task 9; checked on pair 16 of the Critique dress.)
- [x] Detail scale, rotation and shear change the pattern; Advanced shows the stored matrix. (Not
  judged: the change doesn't show at screenshot size.)
  Partly checked (XIV Bridge tests/instant-edit/materials/10, 2026-10-29): dragging Detail scale,
  rotation and shear each changes the stored matrix to what the shown values compose, one undo step
  each; a scale of 250 stays 250 after the scale is touched (no clamp to 64). How the pattern looks:
  by eye. Test 10 fails tonight at "Advanced shows the Detail matrix field": the field isn't found by
  its ID. A test problem, not a product bug.
  The look, by screenshots (XIV Bridge tests/instant-edit/visual/m10s, 2026-10-29, smallclothes top
  row 16 at scale 8): scale x3, rotation +45 and shear +60 each visibly change the pattern on the
  character (edge directions or strength move 4x to 6x more than the idle sway does). Only the
  Advanced matrix field is left.
  (XIV Bridge materials/10, Release 2026-10-30: Advanced shows the Detail matrix field, and the drags
  change the stored matrix to what the shown values compose. Run after physics tests in one batch its
  drags once changed nothing; alone it passes.)
- [x] Copy a row and paste it onto another (Everything, Colours only, Surface only): only the
  chosen fields change, and Undo takes it back in one step. (Colours only, through the buttons.)
- [ ] A legacy material (characterlegacy.shpk) shows the 16-row table; the dots menu's "Convert to
  Dawntrail" moves it to character.shpk with 32 rows, and the character still looks the same.
  (No characterlegacy.shpk material with a 16-row table under 60 MB in the library; vanilla e6015
  Crakows are characterlegacy.shpk with a 32-row Dawntrail table and show 16 pairs.)
  Partly checked (XIV Bridge materials/18, Release 2026-10-29, on a 16-row legacy material built in a
  test mod): the window shows the 16-row table; Convert to Dawntrail moves it to character.shpk with
  32 rows, each old row filling both rows of its pair as the legacy shader drew it, and says "Moved
  to character.shpk". Left: the character looks the same (the made material isn't worn).
- [x] A character.shpk material with an old 16-row table shows the warning and Convert button.
  (Strawberry Halo copy: 32 rows, each old row in both rows of its pair, one undo step. Open: the
  material keeps no index texture after converting, so the rows can't map like the old ones did.)

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
- [x] Enter and leave GPose, and change zones, while bound: no crash; Live comes back. (After a zone
  change the game handed the same material, still edited, to a new draw object; closing then left
  the edits in memory. Fixed in 042d85b: snapshots outlive stale bindings; retested, cutoff back to
  0.5.)
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
  Not scripted: changing the dye in Glamourer would change your Glamourer state.
  Scripted since (XIV Bridge materials/19, Glamourer's temporary state, put back on cleanup), but it skips
  (2026-10-29, 2026-10-30): the character wears no dyeable gear with Dawntrail colour rows. Putting one
  on through Glamourer (SetItem) was tried 2026-10-30: each call said Success, the character never drew
  the item. Wear a dyeable Dawntrail piece and run 19 again.
- [x] A redraw, GPose enter/leave and a zone change while rows are edited: no crash; the edited
  rows come back.

## Index usage (Task 10, pending)

- [x] A Stellar Blade-style mod with a 32 x 32 one-colour index texture: two pairs at most are
  bright, the rest greyed; hovering a greyed pair says "Not used by the index texture".
  (Critique dress, a one-colour index: only pair 16 bright, "Used by 100% of the index texture".
  XIV Bridge tests/instant-edit/materials/11, 2026-10-29, a test mod with a 32 x 32 BC7 index: only
  pair 4 bright, "Used by 100% of the index texture."; the other 15 greyed, "Not used by the index
  texture.")
- [x] A vanilla gear material: the pairs its index uses are bright, with their share in the tooltip.
  (e6015 Crakows: pairs 7 and 8.)
- [x] "Show where" draws the index texture with the selected pair's texels lit (top yellow, bottom
  blue), and follows when another pair is picked. (Critique harness, pairs 1 and 2.)
- [x] A material whose index slot points at a missing file greys nothing and says so. ("The index
  texture could not be read."; with no index slot the reason was missing after opening another
  material, fixed in a140030.)

## Presets (Task 11, pending)

- [x] Presets button: ten cards; ones that don't fit the material are disabled with the reason.
  (All ten fit the harness; See-through now also refuses skin, d3caa97.)
- [x] Metal on a plain gear material (Used rows): the used rows turn metallic the same frame; the
  message names the rows changed and warns if the mask's green channel is rough. (Message and
  rows checked; the harness faces away, so the look is unjudged.)
- [ ] Scrolling pattern: the character redraws on the scroll shader and the pattern visibly moves;
  the new `_fx.tex` sits beside the material after Save (in the mod, or in the new mod). (Redraw on
  characterscroll.shpk with the `_fx.tex` loaded from the preview; movement not checked.)
  Partly checked (XIV Bridge tests/instant-edit/materials/07, 2026-10-29): Save in a mod writes the
  material on characterscroll.shpk and the new `_top_a_fx.tex` beside it, and Penumbra maps the
  texture's game path to that file. That the pattern visibly moves: by eye.
  Failed by screenshots (XIV Bridge tests/instant-edit/visual/m07s, 2026-10-29): on the pale
  smallclothes top the preview glows flat white-blue with no veins, and the part moves less after
  the preset (2% in 0.5 s) than before it (5%). Picture: XIV-Bridge tests/results/visual/
  "2026-10-29_200416_085 m07s after the preset.png".
  Failing by eye too (2026-10-29): the user applied Scrolling pattern by hand to other gear and the
  glow fails on everything, and the card showed no option to add or fix the second UV map. Needs
  another look: the UV2 check and option (MaterialWindow.Uv2.cs) and the glow itself.
  Fixed 2026-10-29 (two causes). The scroll pattern shows only at the row's reflection strength (half 21),
  which the preset left at 0: every scrolling row of five real characterscroll mods has 1 or more, so
  scrolling rows now get 1. And nearly every gear mesh has a four-value UV whose second map is one point,
  which counted as having a second map: such a map now counts as missing, and Add copies the first map
  over it in place. Retest: XIV Bridge visual/m07s (the smallclothes top should be listed as missing a
  second UV map).
  Still failing (XIV Bridge visual/m07s, Release 2026-10-30 01:48, which has both fixes): on the
  smallclothes bottom (mt_c0201e0000_dwn_a, row 16) the preset says "1 row show layer 1 at full
  reflection strength and glow" and the redraw shows a flat white-blue glow with no veins, as before;
  "models without a second UV map: none; 0 can take one here". Pictures: XIV-Bridge tests/results/visual/
  "2026-10-30_150142_902 m07s before the preset.png" and "2026-10-30_150203_487 m07s after the preset.png".
  The idle sway alone changes the part 21-25% in 0.5 s, so m07s's motion measure can't see a pattern
  either way; the picture shows no pattern. Needs diagnosis (the generated `_fx.tex`, the scroll
  constants, the UV2 check on this mesh).
- [x] Glass, See-through and Matcap each look right after their redraw; Undo takes each back in one
  step. (All three apply, redraw and undo; Matcap names everything it removed. Detail pattern and
  Velvet too. Looks not judged on the back-facing harness.)
- [x] Dyeable adds a dye table (the rows' Dye section appears); Clean up's hover lists what it
  removes. (On a material that already has one, Dyeable sets 3 rows to channel 1; adding a table
  is covered by Dyeable on/off in Task 12. XIV Bridge tests/instant-edit/materials/06, 2026-10-29:
  on a material with no dye table, Dyeable adds one with every row on dye channel 1, one undo step,
  and Undo takes it away; Clean up lists "Removes Velvet: this shader ignores it." and Cancel
  changes nothing.)
- [x] Rows to change offers Pair N, Pair N/1 and Pair N/2; Metal on Pair N/1 changes only the top row
  (row 2N - 1), and only the top half of that pair turns metallic on the character. (2026-10-18,
  Pentagram Boots sho_b, Pair 7/1: only row 13 changed; the pentagram and rivets took the white shine.)
- [x] Metal with Adjust mask ticked (default): the message names a new `…_mask_ie.tex`, the mask slot
  reads it, and only the rows' area of that mask is red 255 / green 128 (open it in the Textures
  thumbnail or after Save). The rest of the model looks as before; Undo puts the old mask back in one
  step, and Save writes the new mask beside the material. (2026-10-18, same boots: slot moved to
  `…_s_1813c566_ie.tex`; 7/1 texels went from green 255 to 129 on average, 8.8k blended 7/2 texels moved
  slightly, 6/x and 8/x unchanged; one Undo restored mask and rows. The preview's "Shown on the
  character" replaces the preset's message, as with every redraw. XIV Bridge
  tests/instant-edit/materials/08, 2026-10-29: the slot reads the new `…_top_mask_ie.tex`, 512 x 1024
  like the old one; 2012 texels changed, all red 255 / green 128, and 522276 stayed as they were;
  one Undo puts the old mask back and Redo the new one; Save writes the new mask into the mod's
  folder and Penumbra maps its game path to it.)
  The test paints the least-used pair (pair 6, 0.4% of the index) to show that only part of the mask
  changes: on a gear index every texel maps to a used row, so "Used rows" paints the whole mask.
- [x] Adjust mask is greyed out with its reason on a material without a mask or index texture.
  (XIV Bridge tests/instant-edit/materials/06, 2026-10-29: "Needs a mask texture." without a mask,
  "Needs an index texture, which says where the rows show." without an index.)

## Dye rows and preview dyes (Task 12, pending)

The dye template reader is checked offline against the game's own stainingtemplate_gud.stm and
stainingtemplate.stm (43 templates each).
- [x] On a dyeable gear material, pick a preview dye for Dye 1: the swatches and the character take
  it the same frame. (Critique dress, Celeste Green: the red dress turned teal at once.)
- [x] Compare one row's dyed colour with Penumbra's dye preview for the same dye: they match. (Dye
  field 3 dyes half 11, the base brightness; note here if Penumbra shows it elsewhere.) (Compared with
  Penumbra.GameData's ColorTableRow.ApplyDye instead of its window: Dawntrail rows take the
  Exposure flag into Exposure (half 11), as here; only its legacy path writes Scalar3.)
- [x] A row's Dye section: change its template, channel and fields; Undo takes each back.
- [x] Bake: the rows hold the dyed values, and with "No dye" the character still looks dyed.
- [x] Untick Dyeable: the dye rows go (the character redraws); tick it again: empty dye rows.

## Save as copy into an option (pending)

The places, file names and the meta.json write are checked offline (ModHandoffScenarios.RunCopies).
Checked in game 2026-10-29 by XIV Bridge tests/instant-edit/materials/13 to 17, on a test mod with options.
- [x] Open a material from a mod window, edit a value, dots menu > Save as copy into an option: the
  list shows Default files and every option; the options using the opened file say "uses this file".
- [x] Pick another option and Save copy: the message names it; in Penumbra that option now points
  at a new file in its own folder, and the original file and the other options are unchanged.
- [x] Turn that option on and redraw: the character shows the copy. Save again: it writes the copy,
  not the original. (The copy is the file the character loads; how it looks: by eye.)
- [x] After Scrolling pattern, Save as copy: the new `_fx.tex` goes into the same option.
- [x] Opened from On Screen or Game Files, the menu has no such item; with the mod window closed it
  is greyed with the reason. (The reason and that nothing is written are checked; the grey: by eye.)

## Final review fixes (2026-10-17, need an in-game check)

- [x] Save on a mod material whose file changed since it was opened (edit it elsewhere first): the
  window says it changed, and no "Material Save" mod is left in Penumbra (232c815: Save writes
  straight into the mod now). (Also a normal save: written with a backup, no leftover mod.)
- [x] Change a key, then another, and close the window (or open another material and Discard)
  while the second redraw runs: the preview comes down and the character shows its own material
  (93e61fc). (Both ways.)
- [x] Edit colour rows on the top, then change the top in Glamourer: the new item shows its own
  colours, not the edited rows (2be2c23). (Gown to Stellar Blade e0079: both bindings let go.)
- [x] Convert to Dawntrail on a legacy material: a new `_id.tex` from the normal map is listed as a
  new file, the character keeps its old colour layout, and Save adds the texture (14b6004). (First
  try built it from the collection's normal map, all pair 16; bb5d366 reads the mod's own, which
  gave pairs 8-16. Save added the texture to the material's option. The halo isn't worn, so the
  look isn't judged.)

## Several windows and the one-row pulse (2026-10-17, need an in-game check)

- [x] Open a material, then another from a different row: a second window opens beside the first,
  and both keep their edits. Opening a material that a window already holds brings that window up.
  (XIV Bridge tests/instant-edit/materials/01, 2026-10-29: the second window opens cascaded beside
  the first, both keep their edits; opening the first material again brings its window to the front
  and opens no new window.)
- [x] Two windows with structural edits (a key or a preset) on the same character: both previews
  show at once (Penumbra lists "XIV Instant Edit material preview" and "... preview 2"); closing
  one takes only its preview down.
  (XIV Bridge tests/instant-edit/materials/02, 2026-10-29: Penumbra lists both previews, each from
  its own file, in the test collection; closing the second sends its material back to the game's
  file and the first stays on its preview.)
- [x] Close a window with no unsaved changes, then open a third material: it reuses that window
  instead of making a new one. A closed window with unsaved changes keeps them for its material.
  (XIV Bridge tests/instant-edit/materials/01, 2026-10-29: the third material takes the freed
  window; a window closed with an edit isn't reused and brings the edit back when its material is
  opened again.)
- [x] Copy a row in one window and paste it in another.
  (XIV Bridge tests/instant-edit/materials/03, 2026-10-29: row 1 pasted onto row 2 of the other
  window, one undo step, Undo takes it back, the first window is unchanged.)
- [x] Save in two windows at once into the same mod: both files land, meta.json intact.
  (XIV Bridge tests/instant-edit/materials/09, 2026-10-29: both Save buttons pressed in the same
  frame; both materials and both new `_fx.tex` files land and are mapped, meta.json parses and keeps
  the mod's name.)
- [x] Hover the top half of a pair swatch: only that row pulses (yellow) and the bottom row keeps
  its colours; the bottom half pulses only its row (blue). The tooltip names the row and its share
  of the pair; a sub row the index never picks is dimmed in its half.
  Partly checked (XIV Bridge tests/instant-edit/materials/04, 2026-10-29): hovering a pair's top
  half pulses its top row only, the bottom half its bottom row only, and off the swatches nothing
  pulses. The colours, the tooltip and the dimmed sub row: by eye.
  The colours, by screenshots (XIV Bridge tests/instant-edit/visual/m04s, 2026-10-29): the bottom half
  turns its row's part blue on the character (shift 94), the other half doesn't. The tooltip and the
  dimmed sub row are left for visual/m04v (not run yet).
  (XIV Bridge visual/m04v, Release 2026-10-29: the top half's tooltip names its row, its share and
  "pulses yellow", the bottom half's the other row and "pulses blue"; a sub row the index never
  picks is dimmed in its half and the used half isn't.)
- [x] Hover a row's name, copy, paste or discard button below: that row pulses; it keeps pulsing
  while the paste menu is open. `material` shows "highlightedRow".
  Partly checked (XIV Bridge tests/instant-edit/materials/04, 2026-10-29): hovering a row's copy or
  paste button pulses that row, it keeps pulsing while the paste menu is open, and `material` shows
  "highlightedRow". The row's name and the discard button weren't hovered.
  (XIV Bridge tests/instant-edit/visual/m04s, 2026-10-29: hovering "Row 16" and its discard button
  highlights row 16 and turns its part blue on the character, by screenshots; nothing pulses after.)
- [x] Unload the plugin with two previews shown: both temporary mods go and the character redraws.
  (XIV Bridge materials/20, Release 2026-10-29: with two Material windows' previews shown, unloading
  Instant Edit takes both temporary mods away, redraws the player, and Penumbra shows both materials
  from the game again.)

## Legacy shader to character.shpk (2026-10-18)

Side by side through a temporary Penumbra mod with files made by `LegacyConversion` offline (no Release
build): Leonhart Bottoms (`mt_c0201e0436_dwn_a.mtrl`, characterlegacy.shpk, Compatibility, white shine
map), Limsa inn.

- [x] The converted material matches the original on surfaces facing the camera (mean difference 1.6
  levels against 1.1 between two captures of the same material).
- [x] Copying the rows onto character.shpk as they are (exposure 0) reproduces the report: the skirt
  goes flat black.
- [x] Shader id 12 and 0 look the same here; 12 is kept (legacy writes lighting type 44 = 32 + 12).
- [~] Silhouette: legacy reflects the room at the shine colour, character.shpk at 4%, so the edges came
  out darker (8.5 / 6.6 levels). A white sheen at 25% of the row's shine (aperture 4) halves that
  (3.5 / 3.2) and is now part of the conversion; 50% overshoots. Roughness from the reflection blur
  (Penumbra's formula) helped the centre a little and the edges not at all, so the G-buffer-exact
  2^(-gloss/15) stays.
- [x] Through the editor (Release with cd68f73 + 03f077c, a Bridge copy of the zelda mod, 2026-10-18):
  before converting, the rows show Shine strength and Gloss with the legacy dye fields, and no exposure,
  sheen or metal. Convert to Dawntrail is one undo step: character.shpk, rows at base brightness 1,
  roughness 40%, sheen 25% (spread 4), shader id 12, the mask slot on `…_dwn_s_ie.tex`. Undo gives the
  legacy material back with nothing new to save; Redo brings it back; Save wrote the material and added
  the mask to the same option of the copy, and the game then loaded both from it. The convert message
  is replaced at once by "Shown on the character. Nothing is saved yet." (minor). Penumbra drops a test
  copy's temporary settings when a save reloads the mod, so set them again before looking.
- [ ] Pasting a row copied on characterlegacy.shpk onto a character.shpk row keeps its base colour
  (covered by the regression scenarios; not clicked in game). The legacy dye grid now has two columns,
  as "Shine strength" was cut off in three (not seen in game yet).
  Partly checked (XIV Bridge tests/instant-edit/materials/12, 2026-10-29): a row copied on a legacy
  material and pasted onto a character.shpk row keeps its base colour (<1, 1, 1> both), base
  brightness stays 1, one undo step. The two-column dye grid: by eye.

## Half 11 is the base brightness (2026-10-18)

The composite passes of all six packages that read the table (character, characterscroll, characterinc,
characterstockings, characterglass, charactertransparency) multiply the base colour by half 11; the glow
is the glow colour times the scene's glow scale only.
- [x] A character.shpk row's "Base brightness" at 0 turns that part black, 2 makes it brighter; the
  glow doesn't change. The base swatch darkens and brightens with it; the glow swatch doesn't.
  Partly checked (XIV Bridge tests/instant-edit/materials/05, 2026-10-29): at 0 the base swatch is
  black and at 2 it is twice the base colour, the base colour itself and the glow don't change, and
  the glow swatch is the same at 0, 1 and 2. How the character looks: by eye.
  (XIV Bridge tests/instant-edit/visual/m05s, 2026-10-29, by screenshots: the smallclothes bottom's
  row 16 reads brightness 122.7 at 1, 29.3 at 0 (black but for lighting), 170.5 at 2, and 122.8 after
  Revert. That row has no glow, so the glow stays with 05's data.)
- [x] The Glow preset lights the rows without making the base colour brighter.
  (XIV Bridge tests/instant-edit/materials/05, 2026-10-29: 32 rows get a glow at twice their base
  colour; no row's base colour or base brightness changes, so no base swatch gets brighter; one undo
  step.)

## Finish (Task 13, pending)

- [x] Dots menu > Clean up ignored settings lists what it would remove first; Clean up applies it as
  one undo step; on a clean material it says there is nothing to clean up. (DOUX Maeve hair: Shadow
  cutoff. The list says "Removed ..." under the heading "Clean up removes"; past tense reads oddly.)
- [x] No crash and no stuck preview after: closing the window with unsaved edits, opening another
  material, reloading the plugin, entering and leaving GPose, and a zone change. (Zone change fixed
  in 042d85b.)
