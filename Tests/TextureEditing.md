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
- Turn off **Recompress saved textures** under Options and save again without
  changing pixels. The destination must become uncompressed BGRA32 at the same
  size, and a size that is not divisible by 4 must now be accepted. Turn the
  option back on and confirm the next save restores the original compression.
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
  Resume and verify the pending working image is processed. Restart the plugin
  with sessions open; confirm they restore paused with their original paths.
  Choose **Edit texture** on one of them (and **Open in editor** on another):
  each must resume without pressing Resume, and a save made while paused must
  then be applied.
- Edit the destination externally, change its option mapping, disable/remove/move
  its mod, or change the actor's active texture mapping. The session must pause
  on a conflict rather than overwrite a different source. Reopening from the
  browser must retain the old conflicted session's working files.
- Edit a vanilla texture. No mod should exist until the first changed save; then
  verify its TEX mapping and enabled captured collection. Subsequent saves should
  reuse that destination. A taken mod name must never be overwritten.
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

## Implementation validation environment

The local environment uses Blender 5.2.1 LTS. The model export smoke suite fails
at its existing group-material-suggestion assertion (line 749); an untouched
`HEAD` snapshot reproduces the same failure. The bridge and correctness Blender
suites, standalone Python suites, and both .NET regression programs are separate
checks; inspect their individual results rather than treating the smoke failure
as a successful run.
