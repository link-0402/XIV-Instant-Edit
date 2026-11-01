# Texture editing acceptance checks

Automated coverage runs with `Tests/BlenderStatusRegression`. Its simulated
backend checks orchestration, not real Photoshop saves or Penumbra codecs.
The following checks require the updated plugin, Penumbra, Blender addon, and an
installed TGA editor. Use a disposable test mod/collection with recoverable source
files. These interactive checks have not been performed by the implementation
agent.

## Channel and format round trips

- Open representative BC1/3/4/5/7 and BGRA32 2D textures. Verify each initial TGA
  is 32-bit with an 8-bit alpha channel and matches the decoded original pixels.
- Include packed normal/mask textures, alpha values 0/1/127/254/255, and nonzero
  RGB underneath zero alpha. In Photoshop, open and save without editing and
  compare decoded RGB and alpha byte-for-byte. An unchanged image must cause no
  TEX replacement or redraw. Test uncompressed and RLE TGA saves.
- Edit each channel independently, including RGB beneath zero alpha, and save
  in place. Confirm the original TEX format is retained, dimensions are unchanged,
  and the game renders the intended change. Compression artifacts are expected
  for BC formats; gamma changes, premultiplication and channel swaps are not.
- Resize the image (for example 2048 → 1024, and a non-square size) and save.
  The TEX must take the new size with a regenerated mip chain, keep its format,
  and render correctly in game. The session must show the new size.
- Save a compressed texture with fine detail the compression check rejects (a
  sharp cut-out edge in alpha, for example). The destination must become
  uncompressed BGRA32 at the same size, and the session status must say
  "saved uncompressed" with the reason. An ordinary edit must keep the original format.
- Save at a size that isn't a power of two on each side (1000 × 1000, or 6 × 4): the
  save is refused with why, the file stays, and the next power-of-two save goes through.
- Confirm existing non-mipmapped textures remain single-level. Mipmapped inputs
  should regenerate the full size-appropriate chain, capped at 13 levels.
- Open a mod texture whose header lists mip offsets written for uncompressed data,
  such as Bibo+ `chara/bibo_viera_base.tex` (BC7), both with the brush and from a
  Substance Painter project. The session must open with a TGA matching what the
  game draws, and saves must keep BC7. Restore backup, and a Painter send of the
  untouched image after a change, must write the same pixels back with a header
  whose offsets describe its data; the game must still draw it the same.
- Confirm 24-bit/missing-alpha saves, BC saves whose size is not divisible by 4,
  truncated saves and unsupported source layouts/formats produce actionable
  status without replacing the destination. Fix the working file and verify the
  next save is applied.

## Sessions and live integration

- Configure an editor path containing spaces; open multiple textures and reopen
  an existing session. Its TGA must not be re-exported over your edits.
- Set the cache directory and automatic cache cleanup in the in-game plugin,
  connect Blender once to synchronize them, then close Blender. Save a texture
  with the game UI closed; it must still update. Change the cache directory in
  the plugin and reconnect Blender, then verify only new sessions use the new
  path. With cleanup enabled, stale model jobs and paused texture sessions older
  than 24 hours should be removed, while active sessions and sessions with
  unsaved TGA changes remain. With cleanup disabled, both kinds of cache should
  be retained.
- Save rapidly while a BC7 conversion is running. Also exercise the editor's
  temp-file/rename save behavior. Only the latest stable image may commit.
- Pause while encoding; verify the pending conversion cannot replace the TEX.
  Resume and verify the pending working image is processed. Reload the plugin
  (or restart the game) with sessions open: watching sessions must keep watching
  with their original paths, and a save made right after must apply without
  pressing Resume. A session you paused stays paused, and one whose files haven't
  changed for a day comes back paused. Choose **Edit texture** on a paused one
  (and **Open in editor** on another): each must resume, and a save made while
  paused must then be applied.
- Rewrite the mod's metadata without touching the texture: rename an option in
  Penumbra, export its model from Blender with attribute groups or EST changes,
  and save a variant from another session in the same mod. Saves must keep
  applying, also from Substance Painter.
- Edit the destination externally, or map its game path to another file. The
  session must stop on a conflict rather than overwrite a different source, and
  Painter's send must report it. **Reconnect** on the session card must take the
  session back (Painter's project included): after an external edit, the next
  save goes over it with a backup first; while the mod doesn't use the file,
  Reconnect is refused. Removing or moving the mod still conflicts. Reopening
  from the browser must retain the old conflicted session's working files.
- Switch the actor's option to another file for the texture, then save: the
  session must show **Waiting** without encoding, and the save must apply after
  switching back. Save while the character is being redrawn or between areas,
  and while Penumbra reloads: none of these may conflict.
- Edit a vanilla texture. No mod should exist until the first changed save; then
  verify its TEX mapping and enabled captured collection. Subsequent saves should
  reuse that destination. A taken mod name must never be overwritten.
- Find a material that uses a blank game texture such as
  `chara/common/texture/white.tex`. Its brush button and Edit texture item must be
  disabled, with the tooltip naming the file; the same for a vanilla eye texture.
  In Substance Painter it must stay unticked with that reason, and a send must
  never write it. A mod's own eye texture must still open.
- Verify a modded texture outside a conventional `Files` folder works when its
  source belongs to the registered mod root. Check shared file references too.
- Verify backups precede replacements and Restore backup restores bytes exactly
  (except the corrected header of a texture with mismatched mip offsets),
  pauses the session, and retains the working TGA. Test a locked destination;
  the prior file must survive. Discard must delete only the selected working
  directory, retaining the mod and managed backups.
- Despawn/switch the selected actor before saving. Never redraw a different
  object that reused its index. Preserve the local player and owned-entity redraw
  behavior. When reload/redraw fails after a commit, show that the texture was
  saved and allow Retry to refresh it without re-encoding unchanged pixels.

## Variants

- Save a copy of the working TGA as `Red.tga` in the session folder. Confirm
  `<texture>_Red.tex` appears beside the destination, the mod gains a Single group
  `<texture> variants` with **Original** and **Red**, Red is selected in the captured
  collection and the actor shows it. Other collections using the mod keep Original.
- Save `Red.tga` again: the same TEX updates (with a backup) and no option is added.
  Save `Blue.tga`: it joins the same group. `Original.tga` and names Penumbra cannot
  use report a status instead of committing.
- With Red selected, save the main TGA. It must commit without a mapping conflict and
  switch the group back to Original.
- Rename the group and the Red option in Penumbra, reload, and save Red again: the
  renamed option is updated and selected, not duplicated.
- On a vanilla texture, save a variant before the main TGA. The mod is created with the
  unchanged texture, registered, enabled, and the variant is added and selected.
- With a variant showing, use the brush action on that texture in On Screen: the
  existing session reopens and the variant's TGA opens in the editor.

## Unlisted mod pause (2026-10-10, pending)

Ticked items were verified in game on 2026-10-10 by the XIV Bridge in-game tests
(`XIV-Bridge/tests/instant-edit/textures`, run with `python -I tools/run_tests.py tests/instant-edit/textures`):
a texture session of the plugin's own service code, on Instant Edit's Penumbra backend, for a texture
in a Penumbra test mod, with its session list and cache in a temporary folder.
- [x] Open a texture session, then point Penumbra's mod directory at another folder (keep the old
  one) and save the texture. The session waits at first; after 10 minutes the card says "Paused:
  Penumbra doesn't list the texture's mod. Resume once it does, or open the texture again."
  Test 01: Penumbra stops listing the mod (its mod manager's RemoveMod; the folder and file stay),
  and the wait is shortened to 8 s. The save shows "Save waiting: Penumbra doesn't list the
  texture's mod right now." and leaves the mod's file alone; after the wait the status is the exact
  paused text above.
- [x] Point Penumbra back and press Resume: the texture saves.
  Test 01: once Penumbra lists the mod again, the paused session doesn't save by itself; Resume
  (the call the card's button makes) saves it: "Texture saved and redrawn", and the mod's file
  is the committed texture.
- [x] Disabling and re-enabling the Penumbra plugin while a save waits never pauses the session.
  Test 02 unloads and loads Penumbra through Dalamud's plugin manager (gone about 20 s) with the
  real 10-minute wait. The session never pauses, and the save goes through once Penumbra is back.
  While Penumbra is off the card reads "Save waiting: Penumbra isn't available right now." (2026-10-10,
  after the fix that makes the TGA decode wait rather than fail when Penumbra's IPC is missing).

## Dropping an image on On Screen (2026-10-10, pending)

`Tests/BlenderStatusRegression` covers the service side (decode, commit, undo, restart, refusals).
These checks need the game and Explorer (not scripted: the drag from Explorer, the fill and the
outline, and the Include-vanilla dialog):
- [ ] Drag a PNG from Explorer over a mod texture row in On Screen. The row fills from the
  left over about a second while the tooltip says "Hold here to replace <texture>", then lights
  up with an accent outline and "Let go to replace <texture> with <file>". Let go: the actor
  redraws with the image, the status says "Replaced <texture> with <file>. Undo is in the row's
  menu.", and the Textures tab lists the texture's session.
  Partly checked (XIV Bridge textures/11, Release 2026-10-30, an image dragged from a test window
  that hands On Screen the same payload an Explorer drag does): the mod row fills from the left while
  the tooltip says "Hold here to replace v01_c0201e0079_top_n.tex", lights up with "Let go to replace
  ... with drop.png"; let go, the file changes, the character draws it, the status says "Replaced ...
  with drop.png. Undo is in the row's menu." and the session is listed. Left: the drag from Explorer
  itself and the look, by hand.
- [x] Let go before the row lights up: nothing is replaced and the status asks to hold the
  image until the row lights up. Moving onto another row starts its fill over.
  (XIV Bridge textures/11, Release 2026-10-30: held on one row it filled to 968 px, carried onto the
  next row it started over (62 px) and, let go there at once, nothing was replaced and the status said
  "Hold the image on the texture until its row lights up, then let go.")
- [x] Drag over a model, a material, white.tex or a row with a folder or two files: no fill;
  texture rows that refuse the drag are outlined red and say why.
  Partly checked (XIV Bridge textures/11, Release 2026-10-30): over a model row and a material row,
  no fill and no outline. Not checked: white.tex, a folder or two files.
  (XIV Bridge textures/12, Release 2026-10-30 01:48: a folder and two images held over a mod texture's row draw no fill, a red
  outline and "Drop one image.", and let go there replace nothing, while one image over the same row fills; an image over
  the blank id_16.tex row the character draws (white.tex's kin; this character draws no white.tex) is outlined red and
  says "id_16.tex is a blank texture the game shares between many materials; editing it would change all of them.")
- [x] The row's menu (dots button or right-click) shows **Undo replace**. It puts the old
  texture back, the actor redraws, and the item is gone. After another drop and an editor
  save of the texture's TGA, the item is gone too.
  Partly checked (XIV Bridge tests/instant-edit/textures/03, 2026-10-29): after another drop Undo
  replace is offered again, and after an editor save of the TGA it is gone. Undo puts the texture
  from before the drop back. Rerun 2026-10-29 after the power-of-two ruling: Undo replace is offered
  after a drop, puts the old texture back and is then gone. Not checked: the actor's redraw (by eye).
  (XIV Bridge visual/t03s, Release 2026-10-30, on a gear mask rather than a ring's: on screenshots the
  drop changes the part on the character and Undo replace puts its colour back.)
- [x] Drop a JPEG, a TGA, a DDS and a mod's .tex: each replaces the texture. An image whose
  size isn't a power of two on each side is refused with why, and the file stays (ruled
  2026-10-29: every texture Instant Edit writes is a power of two, 4 to 8192).
  (XIV Bridge tests/instant-edit/textures/03, 2026-10-29: a PNG, a JPEG, a TGA, a DDS and a mod's
  .tex each replace the texture and change the mod's file; a 65 × 66 drop on the 64 × 64 texture is
  refused with "Texture sides must be a power of two from 4 to 8192; this one is 65 × 66." and the
  file stays.)
- [x] With "Include vanilla" on, drop on a Game Data texture: the "Replace a vanilla texture"
  dialog asks for the mod name, the new mod shows the image, and Undo puts the game's texture
  back into that mod.
  (XIV Bridge textures/09, Release 2026-10-30, the image from a test window: the row charges with "Hold
  here to replace v01_c0201e0079_top_id.tex", lights with "Let go to replace ... with drop.png"; the
  dialog says "Replace a vanilla texture" and "drop.png goes into a new mod with this name"; the new mod
  is made under that name, on in the collection, the character draws it, its colour is the image's in
  the channels the BC5 index map holds (red and green), and Undo replace from the row's menu puts the
  game's texture back into that mod.)
- [x] Reload the plugin after a drop: Undo replace is still offered and works.
  Partly checked (XIV Bridge tests/instant-edit/textures/03, 2026-10-29): a second texture service
  started on the same folder stands for the reload; Undo replace is still offered there and puts the
  texture from before the drop back. The real plugin reload: by hand.
  (XIV Bridge textures/07, Release 2026-10-29: after a real reload of Instant Edit, Undo replace is
  still offered for an image dropped before it and puts the texture from before the drop back.)

## Resize texture and Replace with dummy texture in On Screen (2026-10-31, pending)

`Tests/BlenderStatusRegression` covers the service side (ResizedAndDummyTextureAsync). These need the game:
- [x] A mod texture's menu (dots button or right-click) shows **Resize texture**. The dialog says
  "Now <w> × <h>", the current size is greyed out with "(now)", and a size that isn't a power of two
  is greyed out with why. Pick a smaller size and Resize: the file is that size, the character draws
  it, and **Undo replace** puts the old one back.
  (XIV Bridge textures/13, Release 2026-10-31 19:42, right-clicks and clicks on the drawn menu and dialog: on
  v01_c0101e0502_top_n.tex the dialog says "Now 512 × 1024", "512 × 1024 (now)" is greyed out; on a 768 × 1024 copy every
  size is greyed out and 3072 × 4096 says "Texture sides must be a power of two from 4 to 8192; this one is 3072 × 4096.";
  256 × 512 writes a 256 × 512 file the character draws, and Undo replace puts the old bytes back. Note: the resized BC1
  map was saved uncompressed (A8R8G8B8) while its status said only "Texture saved and redrawn", without the "saved
  uncompressed" note.)
- [x] An index texture resized down keeps its colour rows (no blended rows on the character).
  (XIV Bridge textures/13, Release 2026-10-31: v01_c0101e0502_top_id.tex 512 × 1024 to 256 × 512: of 3,451 pixels on an
  edge between rows, 3,443 took one of the four they stand for and 5 fell between; 26 of 131,072 pixels match none (BC5
  noise). The look on the character: by eye.)
- [x] **Replace with dummy texture** on a normal map makes it a flat 16 × 16 map; on a mask it opens
  White and Metal; on a diffuse it makes it white. Undo replace puts each back.
  (XIV Bridge textures/13, Release 2026-10-31: the normal map becomes 16 × 16 (132,130,255); the mask's submenu lists White
  and Metal, and Metal makes it 16 × 16 (255,0,255); an earring's base becomes 16 × 16 white; each keeps its format, the
  character draws it, and Undo replace puts the old bytes back.)
- [x] Both items on a Game Data texture (Include vanilla on) ask for the new mod's name first.
  (XIV Bridge textures/13, Release 2026-10-31, on v01_c0101e0504_glv_n.tex: the dialog says "Replace a vanilla texture"
  with "A 256 × 256 version goes into a new mod" and "A flat (no bump) dummy goes into a new mod"; Cancel makes none.)
- [x] Both items are greyed out on white.tex and other shared blanks, with why.
  (XIV Bridge textures/13, Release 2026-10-31: on id_16.tex both are greyed out and say "id_16.tex is a blank texture the
  game shares between many materials; editing it would change all of them.")

## Copying a texture onto another row in On Screen (2026-10-17, pending)

`Tests/BlenderStatusRegression` covers the service side (CopiedTextureAsync). These need the game
(not scripted: the drag between rows, its texts and outlines):
- [x] Drag a mod texture's name onto a texture row of another material or model. The drag says
  "<texture> from <mod>" and "Hold it on another texture's row to copy it there". The target row
  fills and lights up as for an image, with "Let go to copy <texture> from <mod> onto <target>".
  Let go: the actor redraws with the copy and the status says "Replaced <target> with ...".
  (XIV Bridge textures/08, Release 2026-10-30, real held drags: both drag texts, the fill, the lit
  outline, "Let go to copy ... onto ...", the status, the target's file changed and the character draws
  it; the look by eye. 08 failed 2026-10-29 because the test's line reader split "_dwn_norm" at the
  underscore.)
- [x] Undo replace in the target row's menu puts its old texture back.
  (XIV Bridge tests/instant-edit/textures/04, 2026-10-29, on the plugin's texture service: Undo
  replace is offered on the target and puts its old texture back, at its old 64 x 64 size.)
- [x] The row being dragged shows no outline. Another row that shows the same file is outlined
  red: "This row already shows <texture>".
  (XIV Bridge textures/08 and 09, Release 2026-10-30: the dragged row draws no outline; the other row
  showing c0801f0002_etc_norm.tex is outlined red with "This row already shows c0801f0002_etc_norm.tex."
  and neither fills nor lights; letting go there opens nothing.)
- [x] With "Include vanilla" on, drag a Game Data texture onto a mod texture: the game's texture
  is copied. Drag one onto another Game Data texture: the new mod dialog says "the game's
  <texture> goes into a new mod".
  (XIV Bridge textures/09, Release 2026-10-30: the drag says "the game's v01_c0201e0079_top_m.tex", the
  mod row lights, the mod's file becomes the game's texture (same mean colour); onto another Game Data
  row the dialog says "the game's v01_c0201e0079_top_m.tex goes into a new mod" and Cancel makes none.)
- [x] Copy onto a texture of a different size (e.g. 1024 onto 2048): the target takes the
  copy's size and keeps its own compression.
  (XIV Bridge tests/instant-edit/textures/04, 2026-10-29: white.tex, 32 x 32, copied onto a 64 x 64
  target: the target becomes 32 x 32 and keeps its format (0x3420), and the session shows the new
  size.)
- [x] Let go too soon: nothing changes and the status says to hold the texture until the row
  lights up.
  (XIV Bridge textures/08, Release 2026-10-30: held 0.3 s and let go, the target's file is unchanged, no
  copy starts, and the status says "Hold the texture on the other row until it lights up, then let go.")
- [x] Dragging a texture row over a Mod Browser tile: the tile refuses it in red.
  (XIV Bridge textures/10, 2026-10-29 and Release 2026-10-30: the tile of a Mod Browser mod window refuses
  the row in red with why, and letting go there changes nothing.)

## Editing a texture as a separate copy in On Screen (2026-10-20, pending)

`Tests/ExportContextRegression` covers the plan, the write and session reuse (TextureCopyScenarios). These need the game:
- [ ] Pick a mod texture that two materials share. ⋯ > Edit as a separate copy opens the TGA; the session
  says "Watching; the first save makes the copy". Save: the actor redraws, only that material shows the
  edit, and the row now shows `ie_<id>_<name>.tex` from the material's mod. The other material is unchanged.
  Partly checked (XIV Bridge tests/instant-edit/textures/05, 2026-10-29): the session says "Watching;
  the first save makes the copy"; the first save puts the copy beside the material, the material now
  names the copy's game path and not the original, and the original file is untouched. Not checked:
  a second material sharing the texture, the redraw and the row's new name (by eye).
  (XIV Bridge visual/t05v, Release 2026-10-30: after the first save the character draws the copy and On
  Screen's row shows ie_<id>_v01_c0101a0073_ear_m.tex whole, from the material's mod. Left: a second
  material sharing the texture staying unchanged.)
- [x] In Penumbra the material's mod maps `<folder>/ie_<id>/<name>.tex` in every option that maps the material.
  (XIV Bridge tests/instant-edit/textures/05, 2026-10-29: the default files and the option that maps
  the material map the copy; the option without the material doesn't.)
- [x] Save again: the copy changes, the original file stays as it was. Restore puts back the copied original.
  (XIV Bridge tests/instant-edit/textures/05, 2026-10-29: the second save changes the copy, the
  original keeps its hash; Restore goes back to the copy before the last save, and Restoring the
  original puts back the copied original.)
- [x] Copy white.tex (or another Game Data texture) under a mod material: it works, and no new mod is made.
  (XIV Bridge tests/instant-edit/textures/06, 2026-10-29: "Texture copy saved", the copy is in the
  material's mod and the material names it; no new mod is asked for.)
- [x] Under a Game Data material the item is greyed out with "Its material comes from the game".
  (XIV Bridge tests/instant-edit/textures/05, 2026-10-29: "Its material comes from the game. Save
  the material into a mod first.")
- [x] Choosing the item again before the first save reopens the same session.
  (XIV Bridge tests/instant-edit/textures/05, 2026-10-29: the same session id.)

## Implementation validation environment

The local environment uses Blender 5.2.1 LTS. The model export smoke suite fails
at its existing group-material-suggestion assertion (line 749); an untouched
`HEAD` snapshot reproduces the same failure. The bridge and correctness Blender
suites, standalone Python suites, and both .NET regression programs are separate
checks; inspect their individual results rather than treating the smoke failure
as a successful run.
