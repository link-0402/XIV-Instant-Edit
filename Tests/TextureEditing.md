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
- Confirm existing non-mipmapped textures remain single-level. Mipmapped inputs
  should regenerate the full size-appropriate chain, capped at 13 levels.
- Confirm 24-bit/missing-alpha saves, resized images, truncated saves and
  unsupported source layouts/formats produce actionable status without replacing
  the destination. Fix the working file and verify the next save is applied.

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
- Edit the destination externally, change its option mapping, disable/remove/move
  its mod, or change the actor's active texture mapping. The session must pause
  on a conflict rather than overwrite a different source. Reopening from the
  browser must retain the old conflicted session's working files.
- Edit a vanilla texture. No mod should exist until the first changed save; then
  verify its TEX mapping and enabled captured collection. Subsequent saves should
  reuse that destination. A taken mod name must never be overwritten.
- Verify a modded texture outside a conventional `Files` folder works when its
  source belongs to the registered mod root. Check shared file references too.
- Verify backups precede replacements and Restore backup restores bytes exactly,
  pauses the session, and retains the working TGA. Test a locked destination;
  the prior file must survive. Discard must delete only the selected working
  directory, retaining the mod and managed backups.
- Despawn/switch the selected actor before saving. Never redraw a different
  object that reused its index. Preserve the local player and owned-entity redraw
  behavior. When reload/redraw fails after a commit, show that the texture was
  saved and allow Retry to refresh it without re-encoding unchanged pixels.

## Implementation validation environment

The local environment uses Blender 5.2.1 LTS. The model export smoke suite fails
at its existing group-material-suggestion assertion (line 749); an untouched
`HEAD` snapshot reproduces the same failure. The bridge and correctness Blender
suites, standalone Python suites, and both .NET regression programs are separate
checks; inspect their individual results rather than treating the smoke failure
as a successful run.
