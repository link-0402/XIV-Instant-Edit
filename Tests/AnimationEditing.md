# Animation editing acceptance

Status: development implementation. Automated regressions and compilation do
not validate Havok ABI compatibility or actual Penumbra/SimpleHeels behavior.
Do not regenerate distribution archives until the native, IK, dependency,
activation, and recovery checks below have passed in game.

Automated validation on 2026-09-10: all 42 animation checks, both existing .NET
regression projects, and the Release build with `SkipDistributionPackage=true`
passed. The Release build had no warnings or errors. The existing regression
projects reported NU1900 because NuGet vulnerability metadata was unreachable.
The live-game checks below remain unexecuted.

Automated validation on 2026-09-12: all 67 animation checks, ExportContext,
BlenderStatus/texture, and Changelog regressions passed. The Release build had
no warnings or errors. These checks used an isolated copy of HEAD plus the
animation fixes because concurrent export edits temporarily prevented building
the shared workspace. Native predictive decoding, spline output, and live
LivePose integration still require the in-game checks below.

PAP follow-up validation on 2026-09-12: a sequentially written, packed 26-byte
header reproduced the reported offset/count error before the fix. Corrected
offset reads and writes now pass all 77 animation checks in the shared workspace,
including independent output-header decoding, preserved model/entry metadata,
modded info padding and timeline alignment, and malformed section bounds.

Idle discovery follow-up on 2026-09-12: all 91 animation regressions passed.
Read-only inspection of installed game data and the supplied mod verified
timeline 7405 -> `c0801/.../emote/pose05_loop.pap` -> `cbem_pose05_2lp`.
Default standing timeline 3 uses `resident/idle.pap`, not `normal/idle.pap`.
The observer now derives candidates from active timelines and the player's
skeleton paths before resolving through the captured Penumbra collection.
[Penumbra's resource tree](https://github.com/xivdev/Penumbra/blob/testing/Penumbra/Interop/ResourceTree/ResourceTree.cs)
enumerates material PAPs but does not enumerate skeletal animation packs.
[SimpleHeels' emote identification](https://github.com/Caraxi/SimpleHeels/blob/master/EmoteIdentifier.cs)
and its existing LivePose submodule were checked as references for player state.
These checks do not establish native binding matches in the running game.

## Automated checks

Embedded-source repair follow-up on 2026-09-14: 200 standard animation checks
plus three optional real-asset XML checks pass (203 with the local inputs).
ExportContext, BlenderStatus/texture, and Changelog regressions pass; the Release
build has no warnings or errors. No distribution archives were regenerated.

The supplied `pose01_start.pap` has 145 transform tracks and declares 167
predictive reference bones. Its uncompressed loop has 145 tracks and no predictive
reference count. The installed Illusio Vitae V2_3 c0801 SKLB contains a 168-bone
main skeleton plus six mapper source references, which deduplicate to three
additional source skeletons. The main skeleton's final bone is
`n_hara_noanim_trans`. The local XML acceptance selects the compatible 167-bone
mapper source, validates the PAP's original compressed buffers and binding
indices against it, and retargets its reference pose to the 168-bone destination.
It does not execute native decompression/compression or verify game playback.

Matching now indexes both mapper endpoints and primary skeletons, identifies the
selected embedded source by fingerprint again during baking, and materializes
that reference pose independently of the live target. Version-two description
caches invalidate the former primary-only entries. Equally ranked embedded
sources have distinct picker entries, including when no source is selected yet.
Uncompressed clips continue to prefer primary skeletons. The ineffective Shift
bypass is removed: without a compatible reference pose, decoding remains blocked
with the required channel counts. Reloading the development plugin rebuilds the
source inventory when animation observation starts.

Skeleton repair implementation on 2026-09-13: all 178 animation regressions pass,
including retained playing rows for mismatched predictive bindings, malformed
candidate isolation (including `Invalid Havok animation container.`), exact-name
retargeting, reference-only helper bones, additive transforms, float/partition
mapping, cached-source invalidation, inactive Penumbra options, and v1/v2 recovery.
ExportContext, BlenderStatus/texture, and Changelog regressions also pass.
Builds use `SkipDistributionPackage=true`; the distributed archive is not updated.
These checks exercise native data layouts without calling the game sampler.

The source index reads original game skeletons, collection-resolved skeletons,
and registered mod roots, including disabled mods and inactive options. It checks
PAP model metadata and loaded skeleton paths, deduplicates identical skeletons,
and asks for a source choice when equally ranked candidates differ. Cached
descriptions are versioned and keyed by content hash; file metadata avoids
repeated parsing. Before sampling and commit, selected sources are rehashed.
The source inventory refreshes automatically when its cache expires or installed
skeleton content changes.

**Repair skeleton** works without LivePose or selected offsets. It uses only the
source skeleton selected in the dropdown and retains live offsets; the captured
live skeleton is not sampled, validated, or used as a retargeting destination.
**Rebake with LivePose** uses that same selected source skeleton. It applies the
selected offsets/IK directly in that skeleton's model space and serializes the
result against its reference pose; the actor's live skeleton is not sampled,
validated, or used as a retargeting destination. Meaningful transforms include
constant non-reference poses. Reference-only tracks can be omitted at the
existing 0.000001 change tolerance. Shear and singular reference transforms are
rejected.
The output sample grid includes every source frame at uniform subdivisions.
Only the selected PAP bindings and optional independently resolved startup change.

During a live follow-up the user reported the status alternating between
`Listening` and `Skeleton matching: Invalid Havok animation container.` The
candidate loader had caught IOException but not InvalidDataException (which
inherits SystemException). Explicit candidate isolation now handles both, and
remaining matching failures stay on their clip instead of replacing the global
listening status. The exact exception is covered by a regression fixture.

### Skeleton repair live acceptance (pending)

FFXIV was running during implementation, but the desktop tool refused access:
`Computer Use was not approved to use ffxiv_dx11`. No live repair or activation
success is claimed. Reload the development build before these checks:

- [ ] After reloading the plugin, select the supplied startup and verify the source
  resolves to a 167-bone embedded mapper skeleton. Repair normally, then compare
  startup motion and the transition into its loop on the live target. Test the
  startup alone and with **Include startup**; verify the output is sampled and
  validated before activation, without requiring Shift.
- [ ] With **Another Move Cycle** and the custom c0801 skeleton enabled, play the
  walk. It appears once a compatible source is ready; unresolved and rejected
  candidates do not appear in Character Animations.
- [ ] Select the walk; verify source provider, skeleton choice, and live target.
  Exercise a tied candidate choice through **Source variant**. Keep a simultaneous
  face/upper-body clip playing to verify independent discovery and resolution.
- [ ] With LivePose unavailable, repair to a new mod. Confirm the output samples
  and plays correctly on the live target, or that an exact missing-bone/channel
  reason appears with no files replaced. Existing live offsets must not clear.
- [ ] Repeat with offset baking, additive clips, and optional startup. Compare
  duration, endpoints, hands/feet, root motion, annotations, and untouched PAP
  bindings. Verify only selected offset components clear after successful baking.
- [ ] Change the selected source file, collection, or live skeleton during a job;
  confirm it stops before committing stale output. Test in-place rollback and
  new-mod undo, then restart and check recovery for both repair and offset edits.
- [ ] Measure first-index and repeated-observation frame times, memory use, and
  repeated native decode/compress/dispose cycles before distribution packaging.

EST metadata follow-up on 2026-09-12: a v1 fixture using Penumbra's actual
EST values reproduced `Invalid EST slot in Penumbra metadata.` before the fix.
The decoder now maps Face=72, Hair=73, Head=74, Body=75, as defined by
[EstType](https://github.com/xivdev/Penumbra/blob/testing/Penumbra/Meta/Manipulations/Est.cs)
and [MetaIndex](https://github.com/xivdev/Penumbra/blob/testing/Penumbra/Interop/Structs/MetaIndex.cs).
Binary numeric JSON fields are also normalized to integers so the packaging
filter can read them without a UInt16-to-Int32 exception. All 108 animation
regressions passed, including all four slots after nonempty EQP/EQDP sections,
invalid slots, and binary/JSON filtering equivalence. The Rebake with LivePose button
still requires an in-game retest.

Facial dependency follow-up on 2026-09-12: read-only inspection of the reported
`Jerking Idle 1` mod confirmed that its `pose01_loop.pap` contains the body
motion `cbem_pose01_2lp` and references the external facial motion `cfxf_bad`.
Installed game data links ActionTimeline 622 (`facial/pose/bad`) to that motion;
the tested c0801/f0002 variant contains it in `nonresident/bad.pap`.
Motion lookup now indexes actual TMB references, uses captured face skeletons,
and verifies candidate PAP entries after collection resolution. The supplied
mod's dependency graph completed against the installed game files, including
the face PAP and skeleton. All 120 animation regressions passed, and the Release
build had no warnings or errors. The graph is used for source validation and
baking; only the selected baked PAP clips are copied into a new mod.

New-mod file-scope follow-up on 2026-09-13: dependency manifests no longer
become copy lists. A generated mod containing the loop and optional startup
outputs maps only those baked PAP files; discovered face PAPs, skeletons, TMBs,
effects, and other provider resources remain outside the generated mod.

Crash follow-up on 2026-09-12/13: all 130 animation regressions passed, including
the reported mixed-separator activation path, verification-before-redraw ordering,
and the native binding retain/release contract. The Release build passed without
warnings or errors using `SkipDistributionPackage=true`. Offline inspection of
the installed executable found reversed ownership fields on arena bindings;
temporary bindings and interleaved animations now disable Havok reference
management. Recovery now checks the registered disk directory instead of the
Penumbra virtual UI path. See [crash findings](AnimationCrash-20260912.md) for
evidence and outstanding live validation. Managed tests do not prove native
stability, and the failed generated mod must be disabled before a fresh run.

Run from the repository root with .NET 10 and the installed Dalamud development
assemblies:

```powershell
dotnet run --project Tests/AnimationRegression -c Release -p:SkipDistributionPackage=true
dotnet run --project Tests/ExportContextRegression -c Release -p:SkipDistributionPackage=true
dotnet run --project Tests/BlenderStatusRegression -c Release -p:SkipDistributionPackage=true
dotnet build Dalamud-Plugin/InstantEdit.csproj -c Release -p:SkipDistributionPackage=true
```

Animation regressions exercise PAP envelopes and embedded timelines, malformed
offsets, both SKLB header versions, sample density, scoped pose filtering,
enabled/disabled IK capture, the reflection adapter's shape fixture, preserved
unrelated configuration fields, dependency cycles, nested emitter sound paths,
unsupported records, metadata scope, journal restart, and PAP backup conflicts.
They also check missing native buffers, inconsistent frame counts, and failure
propagation from the checked LivePose persistence service without calling Havok.
The LivePose entity fixture includes both `GetEntity(EntityId)` and
`GetEntity<T>(EntityId)` to exercise overload resolution. Predictive-compressed
fixtures cover animation identity, source sample density, buffer/offset bounds,
runtime-only fields, and reference skeleton compatibility.
Candidate discovery/matching fixtures cover absent skeletal PAPs in Penumbra's
tree, loaded race/face variants, default resident idles, timeline scoping before
uniqueness checks, and refusal to bypass live fingerprints or real ambiguity.
The fixture is not a complete SimpleHeels integration test. The native functions
must be invoked inside the game process.

## Setup and evidence

Use a development plugin installation and a test Penumbra collection. Record
FFXIV, Dalamud, FFXIVClientStructs, Penumbra, SimpleHeels, and Instant Edit build
versions. Keep the source mod and original PAP/SKLB available for comparisons.
Record game paths, binding indices, partial indices, capture times, and job IDs.
The job journal and staged PAPs are under the managed cache directory's
`AnimationEdits/<job-id>` directory; managed PAP backups are under `backups`.

## Native round trips and transform parity

- [ ] Load the development build with Blender, VFXEditor, and XAT closed. Observe
  an emote and an idle; changing playback does not change the selected capture.
- [ ] Establish the no-change Havok round trip for spline, interleaved, and
  predictive-compressed PAPs, including a PAP with mixed encodings.
  Each job performs this before writing outputs; confirm it passes and does not
  alter any unselected binding, annotation, float mapping, or extracted motion.
- [ ] Bake a predictive-compressed clip with reference-pose channels and floating
  channels. Confirm its spline output matches the original samples plus selected
  offsets, including both endpoints, and that other predictive clips stay intact.
- [ ] Verify exact duration and both endpoints. Test a short clip, a zero-duration
  fixture, and a source with more than 30 samples/second. Confirm higher source
  density survives.
- [ ] Compare the edited PAP's sampled bone transforms against BlenderLiveposer
  for root translation, stacked rotations in different orders, parent and child
  offsets together, additive scale, and quaternion sign continuity. Use matching
  samples, source skeleton, and selected components. The native output validator
  permits 0.002 model units for translation/scale and 0.002 in quaternion-dot
  error after compression; visually inspect hands and feet as well.
- [ ] Compare each Position/Rotation/Scale propagation combination to LivePose,
  including non-propagating parent changes that require child track updates.
- [ ] Exercise normal, additive, and deprecated-additive bindings. Check floating
  channels against the original samples. A failed reconstructed-pose comparison
  must prevent every destination write.
- [ ] Test multiple clips in one PAP, simultaneous body/upper/face timelines,
  identical competing bindings, body/face partials, and a modded skeleton. Verify
  ambiguous bindings or mismatched hierarchy/reference poses block editing.
- [ ] Verify sustained observation and repeated edits do not leak native memory.
  Sampling and resampling yield between small framework batches. Compression and
  Havok serialization are indivisible native calls: measure their worst frame
  time on long clips before approving release.

## SimpleHeels and IK

- [ ] Compare complete captured stacks to the loaded SimpleHeels module: names,
  partials, slots, timeline keys, stack order, propagation, CCD depth/iterations,
  two-joint indices/axis, and constraint enforcement. Include disabled IK stacks
  with saved solver tuning and identity-transform stacks with active IK.
- [ ] Validate capture/rebuild before committing. Verify both cached recent poses
  and active poses, with body and facial scopes active together.
- [ ] Compare CCD and two-joint results against LivePose, with constraints on/off,
  reachable/unreachable targets, hidden parent boundaries, and constrained chains.
  Position filtering must disable IK. Check every indirect solver joint.
- [ ] Verify successful activation clears only selected components in the matching
  scope. Switch timelines, reload SimpleHeels, and restart the game: baked offsets
  must not return. Excluded components, unrelated timelines, weapon/minion poses,
  playback speed, freeze state, and other configuration must survive.
- [ ] Change an offset during baking. The result may activate, but automatic
  clearing must be skipped. A skipped clear must not authorize a later automatic
  pose restoration.
- [ ] Unload/reload SimpleHeels or change its public/private data shape during a
  bake. Stop native work safely, retain live offsets, and keep other Instant Edit
  tabs working. Test plugin unload, logout, and partial-skeleton replacement too.

## Dependencies and activation

- [ ] Test an emote with a data-linked startup/loop/exit. Default output edits only
  the selected contained clip. With **Also edit startup**, verify exactly those
  two clips change. Other clips and embedded timelines remain unchanged.
- [ ] Validate a graph whose PAP, VFX, SFX, textures, and skeleton come from
  different mods. Include nested external TMBs, embedded timelines, shared
  dependencies, file swaps to other game files, and collection inheritance;
  verify that none of those discovered provider files are copied unless they
  are among the selected baked clips.
- [ ] Verify a new mod is registered, enabled in the captured collection, and
  prioritized above enabled providers. Existing providers' settings must remain
  unchanged. A maximum-priority conflict must produce an actionable error.
- [ ] Keep the source animation/effect mods enabled after successful creation.
  Replay startup, loop, and exit; verify the generated mod overrides only the
  selected baked clips while existing providers continue supplying dependencies.
- [ ] Verify applicable EST/IMC entries and the player's resolved skeleton variant.
  Inspect the output mappings and metadata, not just a visual loop playback.
- [ ] Test missing files, unknown TMB records, dynamic voice/footstep/model/weapon
  selections, ambiguous material variants, unknown AVFX root chunks, and unknown
  Penumbra metadata serialization versions. These currently block new-mod
  creation when complete dependencies cannot be established; report the exact
  resource/record. No incomplete mod should be activated.
- [ ] Verify vanilla PAPs require a new mod; read-only, linked, external/unregistered,
  shared-output aliases, and missing startup destinations reject in-place writes.

## Transactions and recovery

- [ ] Change a source hash or effective mapping during the job, including a second
  source in a two-PAP startup/loop edit. No stale output may overwrite new work.
- [ ] Cancel during source reads, dependency discovery, sampling, and verification.
  No files or offsets change. Once commit starts, finish or roll back the journaled
  transaction rather than abandoning half a multi-file replacement.
- [ ] Inject compression, serialization, disk-write, add-mod, reload, priority,
  redraw, and resolved-output verification failures. Preserve offsets and check
  automatic file rollback or a precise **Recovery** record.
- [ ] Interrupt the plugin/game after the first PAP replacement, after activation,
  during offset clearing, and halfway through undo. Restart with the same player
  and collection; use **Undo edit** to resume recovery. Never rely on a persisted
  native address after restarting the game.
- [ ] **Reapply offsets** restores the most recently cleared compatible LivePose
  state while the baked animation stays active, and refuses to overwrite newer
  LivePose changes.
- [ ] Change a PAP, new-mod metadata, or offsets after a successful edit. Undo and
  restoration must refuse conflicting changes. Test moved/renamed mods, expired
  backups, a different player, and a different collection.
- [ ] Test multiple successive edits and undo them in reverse order. Preserve the
  recovery records across plugin restarts. PAP backups share the existing 7-day
  managed retention; expired backups must produce a clear recovery error.

General keyframe editing, movement/combat animations, other race variants, and
editing separate companion/weapon rigs are outside this release. Clip length is no
longer fixed: retiming resamples a clip onto a new length and rescales its timeline
events to match, though clips carrying root motion are still refused. Unsupported
dependency constructs are a packaging boundary, not permission to omit assets.

## Animation length live acceptance (pending)

Retiming is the first new operation that samples motion through Havok and writes a
clip whose declared length differs from the one it read, so unlike slot swapping and
facial attachment the native checks above all apply to it.

- [ ] Retime an emote longer and shorter. The motion must play at the new speed with
  no stutter at either end, and the last sample must land exactly on the new length.
- [ ] Confirm footsteps, sounds and effects stay in step with the motion at both
  lengths. They are rescaled by the same factor as the clip; a drift means the
  timeline codec and the bake disagreed about the factor.
- [ ] Retime a clip that carries root motion and confirm it is refused with a clear
  reason rather than desynchronising its displacement.
- [ ] Retime the same clip twice in a row and confirm the second edit scales from the
  already-retimed length, not the original.
- [ ] Drag the length bar and confirm the handle lands on whole frames, matching the
  number typed into the field beside it.
- [ ] Undo and confirm both the motion and its event timing return to the original.

## Facial expression live acceptance (pending)

Attaching an expression rewrites only the animation's own reference to the facial
motion it plays. No motion data is decoded and no face file is copied into the mod,
so the expression must already exist for the character's face variant.

- [ ] On an animation that plays a face, read its expression and confirm the
  reported motion matches what the file actually names. The documented case is
  `pose01_loop.pap` naming `cfxf_bad`, which ActionTimeline 622 (`facial/pose/bad`)
  resolves to `nonresident/bad.pap` for the tested c0801/f0002 variant.
- [ ] Attach a different expression and confirm the character plays it with the body
  animation unchanged.
- [ ] Confirm an expression whose clip the character's face variant does not contain
  fails cleanly rather than playing nothing. Expression discovery reads the game's
  facial timelines and does not know which variants ship which clip; if that turns
  out to matter, the list needs filtering per variant.
- [ ] Attach an expression whose motion name is longer than the current one, which
  grows the embedded timeline, and confirm the animation still plays.
- [ ] Select an animation that plays no face and confirm the section says so rather
  than offering an attachment.
- [ ] Undo and confirm the original expression returns.

## Slot swapping live acceptance (pending)

Slot swapping is a file-level remap: the motion data is never decoded, compressed
or retargeted, so none of the native checks above apply to it. What does need
verifying is that the destination timeline resolves the moved clip at all.

- [ ] Select a numbered pose, search its group, and confirm the discovered slots
  match the ones the character can actually play. A slot the probe misses is a
  discovery bug; a slot it invents is a validation bug.
- [ ] Map one slot to another, create the variants, then enable each option in
  Penumbra in turn. Each must play the mapped animation, including its startup.
  A slot left Unchanged must be untouched by the mod.
- [ ] Confirm the generated group starts on **None** and changes nothing until an
  option is chosen.
- [ ] Verify all three rewrites landed, against the manual VFXEditor workflow this
  replaces: the file sits at the destination game path, the PAP entry name matches
  the destination's, and the C009 motion path inside the PAP's **embedded** timeline
  carries the same name. Missing the third resolves nothing in game. The standalone
  `chara/action/**.tmb` files are never written; a slot swap does not need them
  changed.
- [ ] Swap each family's unnumbered base animation both ways: `idle.pap` with a
  standing pose, `sit.pap` with a chair pose, `jmn.pap` with a ground pose. These
  are the cases where the motion name changes length, so the embedded timeline
  grows to fit it. A TMB keeps its strings at the very end, after the incremental
  sequence that TMAL, TMAC and TMTR all measure up to, so appending there leaves
  every byte count correct and only TMLB's length changes. The rewrite is also
  re-read with the dependency parser before it is accepted, so a malformed footer
  fails the edit rather than reaching the game. What live testing adds is that the
  grown file still plays and still fires its own sounds and effects.
- [ ] Confirm discovery finds each base animation. It is probed in both the resident
  and emote directories because the layout is not assumed; if neither resolves, the
  base member is simply absent from the grid and that is a discovery bug to report.
- [ ] Swap chair-sitting and ground-sitting families as well as standing poses.
  A family whose slots ship loop-only must not gain an invented startup.
- [ ] Undo the edit and confirm every destination slot returns to its original
  animation.
