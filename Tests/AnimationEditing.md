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

XIV Bridge rerun on 2026-10-15, after the TimelineWindow and PenumbraService splits (build
6297dbaa, main 7b74c4f): the whole `tests/instant-edit/animations` suite, each failure run again on
its own. The notes below dated 2026-10-10 still hold, except for two failures: 244 (no "barely
applies" tooltip) and 141 (a "Not saved" row offers an Undo that fails). The face tests (05, 162,
201, 243, 249 to 251) failed until the test sandbox waited for its redraw: a face played before the
character's redraw lands is ended by the game, which then sets the face overlays back to default.

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

- [x] After reloading the plugin, open the supplied startup's timeline and verify Made for
  on the Source page resolves to a 167-bone embedded mapper skeleton. Repair it onto a
  standard with the footer's **Skeleton** combo, then compare startup motion and the
  transition into its loop on the live target. Repair the loop from its own timeline too;
  verify the output is sampled and validated before activation, without requiring Shift.
  (XIV Bridge tests/instant-edit/animations/100, 2026-10-10, after the fix that ranks the character's own skeleton
  file above other files of the same skeleton name: Made for resolves to the 167-bone mapper skeleton; the repair
  saves sampled and validated, keeps its motion, endpoints and the join into its loop; the loop repairs too.)
- [x] With **Another Move Cycle** and the custom c0801 skeleton enabled, play the
  walk. It shows under Playing now at once, with an hourglass while its source is
  found; unresolved and rejected clips do not stay under Played recently.
  (XIV Bridge tests/instant-edit/animations/101, 2026-10-10: the walk is listed 0.2 s after Play with the hourglass
  ("Finding a compatible source skeleton"), which gives way to the tie warning; Searching and Incompatible clips leave
  Played recently, an Ambiguous one stays.)
- [ ] Open the walk's timeline; on the Source page verify the mod, file, live skeleton
  and Made for. Exercise a tied candidate choice through the **Made for** combo (the
  Bones window follows it). Keep a simultaneous face/upper-body clip playing to verify
  independent discovery and resolution.
  Partly checked (XIV Bridge tests/instant-edit/animations/102, 2026-10-10): mod, file, live skeleton (455 bones) and
  Made for (a tie of 65 candidates) are right, and two tied choices each reach the Bones window. Not checked: a face
  clip at the same time; the GPose actor dropped the face when the walk started.
- [x] With LivePose unavailable, repair to a new mod: Create new mod, the footer's
  **Skeleton** > Repair onto …, then Save. Confirm the output samples and plays correctly
  on the live target, or that an exact missing-bone/channel reason appears with no files
  replaced. Existing live offsets must not clear.
  (XIV Bridge tests/instant-edit/animations/103, 2026-10-10: a layer on a bone the standard lacks is refused by name
  with no files written; the plain repair saves, samples like the original, plays, and clears no live offsets; after
  the save the window reads the saved file's binding, so a further save goes through.)
- [x] Repeat with Add > Offset layer from LivePose, additive clips, and the startup in
  its own timeline. Compare duration, endpoints, hands/feet, root motion, annotations,
  and untouched PAP bindings. LivePose's own offsets stay after the save.
  (XIV Bridge tests/instant-edit/animations/104, 2026-10-10: a .livepose file layer plus a repair on a startup in its
  own timeline; every compared value comes out as the layer alone would leave it.)
- [ ] Change the animation file or its mapping, the collection, or (when the save bakes
  motion) the selected source skeleton during a save; confirm it stops before committing
  stale output (the live skeleton is not checked). Test in-place rollback and new-mod
  undo, then restart and check recovery for both repair and offset-layer saves.
  Partly checked (XIV Bridge tests/instant-edit/animations/105, 2026-10-10): the save stops when the file or its
  mapping changes while it bakes, writes nothing, and its record ends "Not saved" (FilesUndone); in-place and new-mod
  saves undo to the exact bytes / the mod off; the records reload from disk as after a restart. Not checked: the
  source skeleton changing (the clip's made-for skeleton is a mod the test can't change).
- [ ] Measure first-index and repeated-observation frame times, memory use, and
  repeated native decode/compress/dispose cycles before distribution packaging.
  Partly checked (XIV Bridge tests/instant-edit/animations/106, 2026-10-10): see "Verify sustained observation" below.
  First-index times weren't measured.

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
The job journal and staged PAPs are under the plugin's config folder, in
`pluginConfigs\InstantEdit\AnimationEdits\<job-id>`; pre-edit backups are under
`pluginConfigs\InstantEdit\Backups`.

## Native round trips and transform parity

- [x] Load the development build with Blender, VFXEditor, and XAT closed. Observe
  an emote and an idle; changing playback does not change the selected capture.
  (XIV Bridge tests/instant-edit/animations/110, 2026-10-10: Idle pose 3 selected on the Animations tab stays selected
  and listed while Flame Dance, Idle pose 2, Run and Idle pose 3 again play. Whether Blender, VFXEditor and XAT were
  closed wasn't checked.)
- [x] Establish the no-change Havok round trip for spline, interleaved, and
  predictive-compressed PAPs, including a PAP with mixed encodings.
  Each job performs this before writing outputs; confirm it passes and does not
  alter any unselected binding, annotation, float mapping, or extracted motion.
  (XIV Bridge tests/instant-edit/animations/111, 2026-10-10: Idle pose 3 (spline), Another Move Cycle's move_a.pap
  (predictive Walk Left and interleaved Jump Start in one file) and Jerking Idle 1's loop and startup: every no-change
  bake passes, nothing unselected changes, durations and float tracks are kept; the worst quaternion-dot error is
  0.00001 and the worst bone 0.52° (a hand), from compression.)
- [ ] Bake an offset layer into a predictive-compressed clip with reference-pose channels
  and floating channels. Confirm its spline output matches the original samples plus the
  layer's offsets, including both endpoints, and that other predictive clips stay intact.
  Partly checked (XIV Bridge tests/instant-edit/animations/112, 2026-10-10): Walk Left (predictive, 105 reference-pose
  channels) comes out spline with the original samples plus the layer's turn and move at every sample and both
  endpoints (0.06°), and no other binding changed. Not checked: floating channels and other predictive clips in the
  same PAP; the file has neither.
- [ ] Verify exact duration and both endpoints. Test a short clip, a zero-duration
  fixture, and a source with more than 30 samples/second. Confirm higher source
  density survives.
  Partly checked (XIV Bridge tests/instant-edit/animations/113, 2026-10-10): Jump Start (0.067 s), Idle pose 3 made
  1.5x slower and 2x faster, and a 60-samples/second source keep their exact durations, endpoints and every sample.
  Not checked: a zero-duration clip; no game file has one, so it needs a hand-made PAP.
- [ ] Compare the edited PAP's sampled bone transforms against BlenderLiveposer
  for root translation, stacked rotations in different orders, parent and child
  offsets together, additive scale, and quaternion sign continuity. Use matching
  samples, source skeleton, and the layer's Turn/Move/Scale. The native output validator
  permits 0.002 model units for translation/scale and 0.002 in quaternion-dot
  error after compression; visually inspect hands and feet as well.
  Partly checked (XIV Bridge tests/instant-edit/animations/114, 2026-10-10): every case matches hand-computed
  expectations within the compression's noise (0.52°, 0.002; hands and feet within 0.004 in model space), and the two
  rotation orders really differ (20°). BlenderLiveposer itself wasn't used.
- [ ] Compare each Position/Rotation/Scale propagation combination to LivePose,
  including non-propagating parent changes that require child track updates. A turn
  kept from the children can't be matched; the layer's note says "Some turns now carry
  the bones below them."
  Partly checked (XIV Bridge tests/instant-edit/animations/115, 2026-10-10): for all eight combinations, children keep
  their place or size when position or scale isn't propagated (they get tracks of their own), and exactly the
  combinations without Rotation get the note. Compared with LivePose's rules, not with the LivePose plugin.
- [ ] Exercise normal, additive, and deprecated-additive bindings. Check floating
  channels against the original samples. A failed reconstructed-pose comparison
  must prevent every destination write.
  Partly checked (XIV Bridge tests/instant-edit/animations/116, 2026-10-10): a normal binding (Aback) keeps its blend
  hint and samples, and a save cancelled mid-bake writes nothing. Not checked: additive clips, as the "(on the ground)"
  emotes never open from the loader (the listener hears only the base slot); no game body clip has a
  deprecated-additive binding or floating channels.
- [ ] Test multiple clips in one PAP, simultaneous body/upper/face timelines,
  identical competing bindings, body/face partials, and a modded skeleton. Verify
  ambiguous bindings or mismatched hierarchy/reference poses block editing.
  Partly checked (XIV Bridge tests/instant-edit/animations/117, 2026-10-10): editing Walk, Run and Sprint of one
  8-clip PAP leaves the others byte for byte; two files with identical motion make the clip ambiguous; a modded-rig
  clip doesn't fit the vanilla skeleton; ambiguous, incompatible and still-searching skeletons block motion edits with
  their reasons. Not checked: simultaneous body/upper/face timelines and body/face partials.
- [x] Verify sustained observation and repeated edits do not leak native memory.
  Sampling and resampling yield between small framework batches. Compression and
  Havok serialization are indivisible native calls: measure their worst frame
  time on long clips before approving release.
  (XIV Bridge tests/instant-edit/animations/106, 2026-10-10: 15 s of observation and six bake/decode/dispose cycles of
  Little Ladies' Dance (11.8 s, the longest dance that bakes): private memory stays flat (-37 MB at the end); frames
  keep coming during a cycle (34-38 per cycle); the worst frame of a cycle is 511 ms (the compression and
  serialization calls), against 351 ms for observation alone.)

## LivePose and IK

- [ ] Compare the complete stacks a Face page bake or "Clear them in LivePose" captures
  to the loaded LivePose plugin: names, partials, slots, timeline keys, stack order,
  propagation, CCD depth/iterations, two-joint indices/axis, and constraint enforcement.
  Include disabled IK stacks with saved solver tuning and identity-transform stacks with
  active IK.
  Partly checked (XIV Bridge tests/instant-edit/animations/120, 2026-10-10): all 56 .livepose files read into the
  stacks their JSON holds (558 bones, 573 stacks: names, partials, slots, order, transforms, propagation, IK on/off);
  a disabled-IK stack with tuning reads as a plain stack, an identity stack with active IK keeps IK on, and IK bones
  are left out of the layer as the note counts. Not checked: what the loaded LivePose plugin captures (timeline keys,
  CCD depth/iterations, two-joint indices/axis, constraint enforcement).
- [ ] The Face page bake validates capture/rebuild before committing. Verify both cached
  recent poses and active poses, with body and facial scopes active together.
  Partly checked (XIV Bridge tests/instant-edit/animations/121, 2026-10-10): a stack on another partial changes
  nothing in the clip, and selecting only another partial's bones is refused with a reason. Cached and active poses
  need LivePose.
- [ ] On a Face page bake, compare CCD and two-joint results against LivePose, with
  constraints on/off, reachable/unreachable targets, hidden parent boundaries, and
  constrained chains. Check every indirect solver joint. A layer from LivePose leaves IK
  bones out ("… bones were left out.").
  Partly checked (XIV Bridge tests/instant-edit/animations/121, 2026-10-10, with made-up IK stacks): CCD reaches a
  reachable target (0.8 mm), keeps bone lengths with constraints on, stops short of an unreachable one, and places the
  bone with constraints off; bones above the depth stay; two-joint keeps lengths and reaches with a fitting hinge axis.
  Test 120 covers the "bones were left out" note. Not compared with LivePose's own results.
- [ ] Verify a successful Face page bake clears only the baked face adjustments in the
  matching scope. Switch timelines, reload LivePose, and restart the game: baked offsets
  must not return. Body adjustments, unrelated timelines, weapon/minion poses,
  playback speed, freeze state, and other configuration must survive.
- [ ] Change an offset during a Face page bake. The result may activate, but automatic
  clearing must be skipped ("… so automatic clearing was skipped."). A skipped clear
  must not authorize a later automatic pose restoration.
- [ ] Unload/reload LivePose or change its public/private data shape during a Face page
  bake. Stop native work safely, retain live offsets, and keep other Instant Edit
  tabs working. Test plugin unload, logout, and partial-skeleton replacement too.

## Dependencies and activation

- [ ] Test an emote with a data-linked startup/loop/exit. A save writes only the open
  clip; the startup is saved from its own timeline (Source > Startup > Open). Other
  clips and embedded timelines remain unchanged.
  Partly checked (XIV Bridge tests/instant-edit/animations/130, 2026-10-10, Curves Idle): the loop's save writes the
  loop only; the startup opened from Source and saved writes the startup only; motion bytes and other timelines stay
  the same and the loop's mod is untouched by the startup's save. Not checked: an exit clip (a capture has no exit).
- [ ] Save a clip whose PAP, VFX, SFX, textures, and skeleton come from different
  mods. Include nested external TMBs, embedded timelines, shared dependencies, file
  swaps to other game files, and collection inheritance; verify the new mod holds only
  the edited file plus any imports or effect copies, and none of the providers' files.
  Partly checked (XIV Bridge tests/instant-edit/animations/131, 2026-10-10): with the PAP, an effect plus a file swap,
  and the skeleton from three mods, the new mod holds only the edited PAP, maps only its game path and copies no swap;
  the providers' files and settings are unchanged. Not checked: nested external TMBs, texture providers and collection
  inheritance (the test collection can't inherit without a collection write).
- [x] Verify a new mod is registered, enabled in the captured collection, and
  prioritized above enabled providers. Existing providers' settings must remain
  unchanged. A maximum-priority conflict must produce an actionable error.
  (XIV Bridge tests/instant-edit/animations/132, 2026-10-10: the new mod ranks at the highest enabled priority plus
  one, existing mods keep their settings, and a provider at the maximum priority fails the save with an actionable
  error; the rolled-back save reads "Not saved" and no longer counts as unfinished.)
- [ ] Keep the source animation/effect mods enabled after successful creation.
  Replay startup, loop, and exit; verify the generated mod overrides only the
  selected baked clips while existing providers continue supplying dependencies.
  Partly checked (XIV Bridge tests/instant-edit/animations/133, 2026-10-10): after the save Curves Idle and the skeleton
  mod stay enabled at their priorities, the new mod ranks above them, the replayed loop comes from the new mod while
  the startup still resolves to Curves Idle and the skeleton mod still supplies the NFLB bones. Not checked: an exit.
- [x] Verify applicable EST/IMC entries and the player's resolved skeleton variant.
  Inspect the output mappings and metadata, not just a visual loop playback.
  A new animation mod carries no EST entry: it doesn't package the skeleton, and the collection that selects the
  variant keeps doing so (decided 2026-10-10).
  (XIV Bridge tests/instant-edit/animations/134, 2026-10-10: the new mod carries what applies to its files, no EST
  entry and no IMC entry of a test provider; the player's variant resolves to c0801 b0001.)
- [x] Test missing files, unknown TMB records, ambiguous material variants, unknown
  AVFX root chunks, and unknown Penumbra metadata serialization versions. Only the
  metadata version blocks a new mod; an unknown TMB entry kind opens the timeline
  read-only and names the kind. The others don't block. No incomplete mod should be
  activated.
  (XIV Bridge tests/instant-edit/animations/135, 2026-10-10: metadata version 2 is refused by number (0 and 1 read);
  an unknown AVFX root chunk is only a reported problem; an unknown entry kind C999 is named and blocks Save; a sound
  whose file doesn't exist saves and the new mod's journal completes. Material variants don't apply: a timeline save
  reads no MTRL. Penumbra answering version 2 end to end wasn't tried.)
- [ ] Save an animation with footsteps (C042) and a motion change (a layer, a repair or
  unticked bones) into a new mod, then one with voice lines or other events whose sound
  the game picks by ID. These name no file, carry through the save unchanged, and must
  not block. Confirm footsteps still play on the terrain-appropriate vanilla sound with
  the generated mod enabled.
  Partly checked (XIV Bridge tests/instant-edit/animations/136, 2026-10-10): Aback's three C042 and Angry's C053, each
  saved with a layer, aren't blocked, carry through unchanged and the journal lists the clip only. Not checked: the
  footsteps' sound on each terrain (by ear).
- [x] Verify vanilla PAPs require a new mod; read-only, linked and external/unregistered
  files reject in-place writes. A file two game paths share is not refused: the save gives
  the edited path a file of its own and leaves the other path's file intact (decided 2026-10-10).
  (XIV Bridge tests/instant-edit/animations/137 and 138, 2026-10-10: a vanilla file, an external file from a
  temporary mod and a file in a folder Penumbra doesn't list are refused by the footer and the service; a read-only
  file and a file through a directory junction fail clearly and write nothing; a shared file is split as above.)

## Transactions and recovery

- [x] Change the source file's hash or effective mapping during a save. No stale output
  may overwrite new work.
  (XIV Bridge tests/instant-edit/animations/139, 2026-10-10: the save fails with "changed" and writes nothing; undoing
  the change lets the same save through; the refused job no longer counts as unfinished.)
- [x] Press the footer's Cancel during source reads, sampling, and verification. No
  files or offsets change. Once the status reads "Committing validated animation files",
  Cancel is gone: the journaled transaction finishes or rolls back rather than
  abandoning half a multi-file replacement.
  (XIV Bridge tests/instant-edit/animations/140, 2026-10-10, Little Ladies' Dance: Cancel during source reads, at 22 %
  of baking and at 32 % of verifying ends with "Cancelled before commit. Live offsets are unchanged.", no mod folder,
  no journal and the same collection settings; at "Committing validated animation files" Cancel isn't drawn and the
  save completes.)
- [ ] Cause compression, serialization, disk-write, add-mod, reload, priority, redraw,
  and resolved-output verification failures where you can; there are no fault hooks.
  Preserve offsets and check automatic file rollback, or an Edits row whose state badge
  gives the precise error in its tooltip.
  Partly checked (XIV Bridge tests/instant-edit/animations/141, 2026-10-10): an output path another operation created
  leaves an Edits row needing attention whose tooltip gives the precise error, and Undo clears it with the mod's files
  as before; a new-mod folder that can't be created fails clearly, registers nothing and undoes. Compression,
  serialization, add-mod, reload and redraw failures can't be injected from a script; the resolved-output check was
  timing-dependent and not reached.
- [ ] Interrupt the plugin/game after the first file replacement, after activation,
  during a Face page bake's offset clearing, and halfway through undo. Restart with the
  same player and collection; the Edits tab marks the edit and its Undo resumes recovery.
  Never rely on a persisted native address after restarting the game.
  Partly checked (XIV Bridge tests/instant-edit/animations/142, 2026-10-10): records left Activated, Undoing and
  Committing (after an in-place replacement), read back from disk as a restart does and with a stale native address,
  are marked on the Edits tab, and their Undo finishes the recovery. Not checked: a real interruption and restart, and
  a Face page bake's offset clearing.
- [ ] **Put them back** in the LivePose note restores the state "Clear them in LivePose"
  cleared, and an Edits row's Restore offsets the state a Face page bake cleared, keeping
  its files. Both refuse to overwrite newer LivePose changes.
- [ ] Change a PAP, new-mod metadata, or offsets after a successful edit. Undo and
  restoration must refuse conflicting changes. Test moved/renamed mods, expired
  backups, a different player, and a different collection.
  Partly checked (XIV Bridge tests/instant-edit/animations/143, 2026-10-10): Undo refuses, naming why and changing
  nothing, for a changed PAP, changed new-mod metadata, a renamed mod ("ownership marker is missing"), another
  collection, another player, a changed in-place output and an expired backup; once the conflict is undone it goes
  through. Not checked: changed offsets (LivePose is manual).
- [ ] Test multiple successive edits and undo them in reverse order. Preserve the
  recovery records across plugin restarts. PAP backups share the existing 7-day
  managed retention; expired backups must produce a clear recovery error.
  Partly checked (XIV Bridge tests/instant-edit/animations/144, 2026-10-10): three in-place edits undo only newest
  first (the oldest refuses with "Undo that edit first."), each undo restores the file and mapping before it, the
  records and their states are on disk, and an 8-day-old record is dropped. Not checked: the expired-backup error
  (test 143, which fails, above).

General keyframe editing, movement/combat animations, other race variants, and
editing separate companion/weapon rigs are outside this release. Unsupported
dependency constructs are a packaging boundary, not permission to omit assets.

## Animated bones live acceptance (pending)

**Animated Bones** lists the bones the selected clip's transform tracks drive,
named by its selected source skeleton. Unticked bones get no track in the rebaked
clip, so nothing in the animation drives them. Offline fixtures cover the list,
the refusals, and the rewrite of tracks and expected samples. Whether the game's
physics then takes over an untracked bone is the in-game claim still to verify.

- [ ] Untick hair, skirt, tail or chest bones on the **Bones** tab for an emote that
  animates them, then Save into a new mod and in place. Confirm those bones follow
  physics again and every other bone animates exactly as before.
  Partly checked (XIV Bridge tests/instant-edit/animations/145, 2026-10-10, Idle pose 3): 29 skirt, chest, hair and
  tail bones unticked, saved into a new mod and then more in place: the files have no track for them (they sample at
  their reference pose), every kept bone matches within 0.56° and 0.7 mm, and in game the other bones move as before
  (2.0° at most, the idle's sway). Not checked: that the bones then follow physics (watch a tail or skirt; hair is
  already driven by physics in game, test 16).
- [ ] Confirm validation passes on spline, interleaved and predictive sources, and
  on an additive clip: a bone without a track must sample at its reference pose.
  A pose-validation failure naming an unticked bone means that assumption is wrong
  for that encoding, and no file may be written.
  Partly checked (XIV Bridge tests/instant-edit/animations/146, 2026-10-10): with j_kami_a unticked, Idle pose 1
  (interleaved) and Idle pose 3 (spline) validate and save; the bone has no track and samples at its reference pose.
  Not checked: a predictive and an additive clip (none among the loader rows tried opened; the additive "(on the
  ground)" emotes never open).
- [x] Untick bones, then save with a layer from LivePose and with a Skeleton repair;
  both must leave the unticked bones out. Untick a bone the layer moves: Save is
  blocked with "Bone layers move …, which you unticked under Bones. Tick it again or
  take it out of the layer."
  (XIV Bridge tests/instant-edit/animations/147, 2026-10-10: the layer came from a .livepose file. Unticking
  j_hito_a_l, which it moves, blocks Save with "Bone layers move Left Index A (j_hito_a_l), which you unticked under
  Bones. …"; with j_kami_a and j_kami_f_l unticked, the layered save and a repair onto IVCS both leave them out and
  keep every other track.)
- [x] On the Bones tab, **Keep only** Vanilla, IVCS and IVCS + YAS each tick exactly
  that preset's groups and untick every other animated bone; the "highest index" shown
  above the list follows the ticks.
  Checked (XIV Bridge tests/instant-edit/animations/12, 2026-10-10) on Jerking Idle 1, an IVCS clip: Vanilla unticks its 47 iv_ bones
  (highest index 153 to 104) and Ctrl+Z ticks them again. It has no ya_ or nf_ bones, so the IVCS
  and IVCS + YAS presets had nothing to untick.

## Standard-skeleton repair (2026-09-28)

Repair used to retarget onto the live rig and pin every live bone the source never
animated. With an all-in-one rig such as NFLB + YAS (455 bones for c0801), every
repaired clip bound all 455 bones, up to index 454, and sync plugins refused it:
they reject animations binding indices beyond the receiving skeleton. The Curves
Idle `pose05_loop.pap` and `pose05_start.pap` show it: both bind bones 0-454 of the
`c0801` rig.

Repair now retargets onto a standard skeleton, never the live rig: the game's own
(read from game data), IVCS or IVCS + YAS (the best main skeleton of that layout
mapped to the live path in the skeleton library). Only the source's own tracks are
written, plus a kept bone that only moves because a left-out bone above it moves.
Bones the chosen standard lacks are left out rather than refused.

Surveyed layouts (c0801, from the saved skeleton library): the game skeleton has
106 bones, `n_hara_noanim_trans` last at 105. IVCS 2.3 keeps the first 105 and adds
62 iv_ bones at 105-166, with `n_hara_noanim_trans` at 167. YAS is IVCS without
that bone plus 5 ya_ bones at 167-171. NFLB + YAS keeps 0-153, then moves 13 IVCS
physics bones to 397-440, YAS to 447-451 and `n_hara_noanim_trans` to 454.
Trimming a rebake in the NFLB layout therefore still binds index 454; only repair
restores standard indices.

Offline, with the plugin's own code against the real library, game skeleton and the
Curves Idle track list: repair onto Vanilla writes 106 tracks up to index 105,
IVCS 168 up to 167, IVCS + YAS 172 up to 171. The rig's shared bones rest within
0.04 degrees and 0.15 mm of the standards, so the rest-relative transfer does not
visibly change motion. All 385 animation regressions pass.

- [ ] Open Curves Idle's timeline, **Keep only** Vanilla on the Bones tab, then save into
  a new mod with the footer's Skeleton > Repair onto Vanilla. Confirm the idle plays as
  before on the NFLB rig, the output binds 106 bones, and a sync plugin sends it without
  an invalid-bone warning.
  Partly checked (XIV Bridge tests/instant-edit/animations/150, 2026-10-10): the repaired save binds 106 tracks, none
  past index 105 and none an iv_, ya_ or nf_ bone, and on the NFLB rig it plays within 3.8° of the original (mean
  1.0°). Not checked: a sync plugin sending it.
- [x] Repeat onto IVCS and IVCS + YAS; confirm each Repair onto item's "N bones, from …"
  tooltip, and that the "… bones left out" tooltip ("The IVCS skeleton lacks …") names
  the nf_ bones.
  (XIV Bridge tests/instant-edit/animations/151, 2026-10-10: "168 bones, from Illusio Vitae Custom Skeletons V2_3" and
  "172 bones, from Rue+, Yet Another Body+ (2), Yet Another Skeleton and 4 more"; on Curves Idle's startup (282 nf_
  bones) "The IVCS skeleton lacks Nf Sho Asi E Left (nf_sho_j_asi_e_l), … and 279 more." The outputs bind nothing past
  index 167 / 171. The loop has no nf_ bones.)
- [x] Open a vanilla emote made for the game's skeleton and choose Repair onto Vanilla:
  Save is blocked with "The animation was already made for the Vanilla skeleton. Choose
  Keep its own." Repair a clip, then its startup from its own timeline; both must bind
  the same standard.
  (XIV Bridge tests/instant-edit/animations/152, 2026-10-10: Idle pose 3 is refused with that text; Curves Idle's loop
  and startup, each repaired from its own timeline, both use the IVCS + YAS standard.)
- [x] Undo last save (or Undo on its Edits row) and confirm the unticked bones are
  animated again.
  (XIV Bridge tests/instant-edit/animations/153, 2026-10-10, Jerking Idle 1: after a save with Keep only Vanilla (47
  iv_ bones unticked), both Undo last save and the row's Undo read the original file again; the 47 bones have their
  tracks again, nothing is unticked and the highest index is 153 again.)

## Quantized clips made on an older skeleton (2026-10-17)

`[Noff][IVCS] Idle Pussy Lick [Shiver]`'s `loop_emot18_loop.pap` is quantized and
declares 167 reference bones: IVCS before 2.3 added `n_hara_noanim_trans`. No c0801
skeleton in the library has 167 bones, and the first-bones fallback was predictive
only, so the Source page said no source was found and the clip could not be repaired.
Quantized clips now take a longer skeleton's first bones too, from their exact model
only: both compressions fill unstored channels from the source's rest pose. Among
first bones, those that name the animated bones as the skeleton the clip names come
first (NFLB has its own bones at 154-166, where IVCS has its physics bones), then the
character's own file, then the skeleton that added the fewest bones since (IVCS 2.3:
1, YAS: 5).

Offline, with the plugin's own Rank against the real library: without the live rig
the source is IVCS 2.3's first 167 bones; with the NFLB + YAS rig live, the rig's
first 167 (the clip moves nothing past 153). Repair onto IVCS writes all 145 tracks
up to index 153. The face clip `nf_idleoral1.pap` (80 bones) matched before and still
does.

- [ ] Open the Shiver loop's timeline: the Source page shows "Uses only its first 167
  bones". Repair onto IVCS into a new mod and confirm it plays like the original.

## Timeline editor live acceptance (pending)

The Timeline tab opens the selected clip in the Timeline window. The TMB reader and
writer round-trip every vanilla chara timeline (173,717) byte for byte and 53,018 of
53,032 mod timelines (the 14 refused are malformed). SCD output was re-parsed and
decoded offline. None of this was checked in game yet.

Saving:
- [x] Move a sound in an emote and save to a new mod, then in place. Only that
  timeline changes; the motion bytes are identical; Undo last save restores it.
  (XIV Bridge tests/instant-edit/animations/160, 2026-10-10, Clap: the sound moved 4 frames later into a new mod, then 2
  earlier in place; each save holds the edited events, the same entries and model and identical motion bytes; Undo
  puts the mod's mapping back and the window reads the first save's timeline again.)
- [ ] Add a sound (template: play once, 3D), an effect (caster to target), a resident
  expression (Angry) and a library expression (Smile, sets the face pack) to a PAP
  timeline that had no face pack. Each plays at its frame; the face holds for its
  length.
  Partly checked (XIV Bridge tests/instant-edit/animations/161, 2026-10-10, Idle pose 3): the saved timeline holds all
  four with the templates' values (play once, 3D; caster to target) and Smile sets the face pack; in game Angry
  (6.3°) and Smile (10.7°) show at their frames and the face falls back after Angry. Not checked: hearing the sound
  and seeing the effect at their frames.
- [x] Add lip sync (cfxl_lip_nor1). It plays at its own pace.
  (XIV Bridge tests/instant-edit/animations/162, 2026-10-10: saved with no time control and the range 0..40; in game
  the mouth moves for 38 frames from frame 11 and then rests.)
- [ ] Add a harvested prop (Toast: model 1962, WeaponTimeline 388 on c0101, 389 on
  c0801) and a still prop (Read). The prop appears without a T-pose flash, animates,
  and disappears when it ends.
  Partly checked (XIV Bridge tests/instant-edit/animations/163, 2026-10-10): Toast adds the sync effect, model 1962,
  WeaponTimeline 389 on c0801 five frames after the spawn and the hide-then-fade visibility steps; Read adds the sync
  effect and the model; the save holds them. Not checked: seeing the prop (no T-pose flash, animating, gone at the end).
- [ ] Import a 44.1 kHz and a 48 kHz wav, a stereo wav, an ogg and an scd. Each plays
  once at full length and pitch; volume 50% is quieter. Import an .avfx whose
  textures sit in a sibling folder; the textures are packaged at their game paths.
  Partly checked (XIV Bridge tests/instant-edit/animations/164 and 165, 2026-10-10): each sound becomes a one-shot SCD
  at its own sample rate (44100, 48000), channels and full length (1.501 s for 1.500 s), 50% is stored as volume 0.5,
  an scd is taken as it is, and a placed import is saved into the mod; Red Carpet's effect with 9 textures beside it
  is packaged and mapped at their game paths, and a missing texture is refused by name. Not checked: listening.
- [x] Save an edit with imports in place into a mod whose animation sits in a group
  option. Every option that maps the animation also maps the import; Undo removes
  the import and its mappings.
  (XIV Bridge tests/instant-edit/animations/166, 2026-10-10, Nhagi's Idle Pose Vol. 7 cpose 4: all 6 options that map
  the animation map the imported sound after the in-place save; Undo removes the import, its file and its mappings.
  Undo writes one mapping's path separators back as '/' where the mod had '\'; the file is the same.)
- [x] Delete the only entry of a conditional track; edit a PAP with several entries
  and one with an odd footer offset. Every entry still plays.
  (XIV Bridge tests/instant-edit/animations/167 and 168, 2026-10-10: Advent of Light without its conditional track's
  only entry saves and plays; Another Move Cycle's 8-entry move_a.pap (footer at 295035) and Nhagi's Idle pose 4
  (footer at 640453) edited in place keep every other entry's timeline byte for byte and the footer offset, and their
  entries still play.)

Expressions without time control:
Without time control the game ignores a C010's length and frame range. A face pose is
a constant two-key clip, so the face shows at once and holds. Seen in game on
2026-10-05: Leena Grind on Breathcontrol's Smile (stored 10 frames, range 0 to 0)
shows for the whole 100-frame loop. The editor now draws such a face until the next
face or the end and marks it "held". 4,912 library faces are like this, 3,434 of them
with the empty 0 to 0 range VFXEditor writes.
- [x] Open that mod: the Smile spans the whole loop, reads "held" and has no resize
  edges. Tick Time control: it keeps its span and becomes resizable. Shorten it to 30
  frames and save in place (the range becomes 0 to 1). In game the smile now ends
  after 1 s and comes back with each loop.
  (XIV Bridge tests/instant-edit/animations/169, 2026-10-10: held over all 100 frames without resize edges; with Time
  control it keeps 100 frames and is resizable; saved at 30 frames with the range 0..1; in game the smile shows for the
  first second (16.4°), the face is neutral for the rest of the loop (0.1°) and the smile came back in the next loop.)
  Unticked: since a later suite run, and after a relog, the same test fails three times running. The first loop is
  still right (smile to frame 32, neutral from 40), but over two loops the rest-of-loop samples average 8.5° (max 16.7°),
  so the smile seems to stay on in a later loop. The second loop's samples weren't seen yet; rerun 169 and look.
  Passes again on its own (2026-10-10, after the day's fixes).
- [x] Give a held face a timed face later in the clip. The editor ends the held face
  where the timed one starts and shows nothing after it ends. Check what the game
  shows after the timed face ends: the held face, or the neutral face.
  (XIV Bridge tests/instant-edit/animations/170, 2026-10-10: after a held Angry, a time-controlled Smile at frames
  30-45 shows (21.7° from neutral), and after it the game shows **the neutral face**. Also with a resident timed face
  (Battle Camera 1). The ninth session's "the Smile never shows" no longer reproduces after the fixes of 2026-10-10.)

Lip sync and blinks without time control play their own length. The window reads the
lengths from the player's resident face.pap through the game's Havok (in all 161 vanilla
resident faces lip …1 = 40 frames and …2 = 10). 23 of the library's 61 such lips
store another length.
- [x] Add Lip Nor 2 and Lip Nor 1: they read 0.33s and 1.33s with "own speed" and
  have no resize edges. (40 frames for Nor 2 means the Havok read failed.) In game
  each plays once, then the mouth rests.
  (XIV Bridge tests/instant-edit/animations/171, 2026-10-10: "0.33s · 10 frames" and "1.33s · 40 frames", own speed, no
  resize edges; saved with the ranges 0..10 and 0..40; in game each moves the mouth once and it rests between and after.)
- [x] Tick Time control on Lip Nor 1, stretch it to 80 frames and save. In game the
  lip plays at half speed (the save writes the range 0 to 40).
  (XIV Bridge tests/instant-edit/animations/172, 2026-10-10: the save writes 0 to 40 over 80 frames and the lip plays
  in game.)

Effects that stop:
Red Carpet's Popoto Step dance (a swap of its mode A walk onto dance12_loop) starts
four effects with Async VFX entries at loop/wait 909. With -1 the carpet, cloud and
sparkle kept looping but ended at frame 908 (30.3 s, two dance loops): each effect
schedules its own KILL there. The music (redcarpetbgm -> redcarpet.scd, Vorbis, 910
frames) is marked to loop but has no loop points, so it played once. Offline, the fix
writes copies that differ only in the kill item's enable byte (13-14 bytes) and in
the SCD's loop end (1,827,659, the value mode B's redcarpet2.scd already uses).
- [ ] Open the dance: the window says "4 effects stop: ...". Keep them looping,
  preview in GPose, then save in place. Dance for over a minute: the effects never
  stop and the music plays in full and starts over without a gap. The mod gets
  instant_edit/ copies mapped in the Music, Props, Sparkles and Floor Cloud Thingy
  options. Turning a toggle off removes that effect from the dance too. The mode A
  walk is unchanged. Undo removes the copies and their mappings.
  Partly checked (XIV Bridge tests/instant-edit/animations/173, 2026-10-10): the window says "4 effects stop: redcarpet,
  redcarpetbgm, redcarpetcloud, redcarpetsparkle."; Keep them looping makes copies that differ only in the kill item's
  enable byte (13-14 bytes) and a sound copy looping over all of it (0..1827659); Preview plays; the in-place save maps
  the copies in Music, Props, Sparkles and Floor Cloud Thingy; Music off no longer resolves its copy; the mode A walk is
  unchanged; Undo removes the copies and mappings. Not checked: dancing for over a minute (eyes and ears).
- [x] Check whether -1 on the bgm effect (it doesn't loop, it was 909) changes
  anything, and whether the copies' effects double up when the dance loops.
  (XIV Bridge tests/instant-edit/animations/190, 2026-10-10, by the files' live reference counts in the game's
  resource map: over three dance loops no copy's count grows, so nothing doubles up; saved in place, the music's
  looping copy plays and is still playing at 35 s with -1 and with 909 alike: the value changes nothing.)
- [x] Preview plays an effect's music. Found and fixed 2026-10-10 (XIV Bridge animations/190, tools/diagnostics/
  music-isolate.csx, music-timing.csx): a Preview played no sound at all, not even the unedited Red Carpet dance's
  music or the emote's own sound. It played on Penumbra's redraw event, when the character has its skeleton but
  doesn't show yet (it shows 65 to 120 ms later), and a timeline started then asks for none of its sounds. The
  preview now waits until the character shows (at most 2 s). 190 passes: the preview plays the looping music copy.
  Listen once in game that the music plays from the preview's start.

Effect looks (2026-10-07):
Selecting an effect (C012 or C173) shows a Look section. Tint, size, offset and turn
write the AVFX root "revised" fields (RvR/G/B, RvS*, RvP*, RvR*); the swatches, Hue and
Saturation recolour every colour key (particle, model, line, dissolve, non-white
emitter, light, simple animation colours in use); Glow and Opacity scale each
particle's main Bri/A curve and the lights' Bri; Light range scales point lights'
DstS. Offline, all 16,339 library and vanilla effects read, an empty look changes no
byte, and a full look changes only those numbers.
In Preview and Live the effect's own game path points at an edited copy in the preview's
temporary mod (a new revision file per change), because the game keeps the playing clip's
PAP loaded: a preview's rebuilt timeline, naming copies, was never read (reported
2026-10-07 on Angel Dust's dustsmoke, Live off). Penumbra hashes the resolved file, so
the next spawn of the effect loads the new look.
- [ ] Preview the smoking emote with a recoloured dustsmoke: it shows the new colour.
  Change the colour while the preview plays: the next puff (each loop restarts it)
  has it, with no Preview again and no replay. The hint says looks change when the
  effect starts again.
  Partly checked (XIV Bridge tests/instant-edit/animations/174, 2026-10-10, Angel Dust): with Preview, dustsmoke's game
  path resolves to an edited copy with the red tint; a blue tint while it plays reaches a new revision of that path
  with no new build and no replay; the hint is shown. Not checked: seeing the smoke change colour.
- [ ] Turn Live on and change a look: the same happens over the clip's own file. Turn
  Live off: the effect goes back to its own look on its next start.
  Partly checked (XIV Bridge tests/instant-edit/animations/175, 2026-10-10): with Live, the effect's path resolves to an
  edited copy while the clip's own file still plays, a new tint reaches a new revision, and Live off resolves the
  effect to its own file again. Not checked: seeing it.
- [x] Check whether the rest of a Preview shows (2026-10-07): recolours showed, but a
  removed expression kept playing. A replay never re-reads the clip's file even at a new
  revision path, so Preview now redraws the GPose character after showing a revision,
  as the in-place edits redraw the player (see Preview below).
- [ ] Tint an effect red and preview in GPose: the whole effect turns red, lights and
  model particles too. Above 1 it gets brighter.
  Partly checked (XIV Bridge tests/instant-edit/animations/176, 2026-10-10): the tint writes only RvR/RvG/RvB (red reads
  back 1,0,0; 2.5 is kept). Not checked: how it looks in game.
- [ ] Size 2 doubles it, Offset Y 1 lifts it, Turn Y 90 turns it a quarter (if it
  turns about 57x as far, the field is radians and the label must change).
  Partly checked (XIV Bridge tests/instant-edit/animations/176, 2026-10-10): size 2,2,2, offset 0,1,0 and turn 0,90,0
  are written as typed and nothing else changes. Evidence on the unit: of 1,501 effects read, the largest Turn value
  is 0.052 and none is above 2π, which suggests radians. Not checked: how far it turns in game.
- [ ] Click a swatch, pick another colour: only that part changes. Hue 180 turns
  every colour to its opposite; Saturation 0 makes it grey.
  Partly checked (XIV Bridge tests/instant-edit/animations/176, 2026-10-10): a swatch recolours its 4 keys and leaves
  the other 4; Hue 180 turns every colour key by 180° keeping its brightness; Saturation 0 makes every key grey.
  Not checked: how it looks.
- [ ] Glow 200% brightens, Opacity 50% fades the particles. Light range 200% on an
  effect with a point light (a fire or glow) lights a wider area.
  Partly checked (XIV Bridge tests/instant-edit/animations/176, 2026-10-10): Glow doubles every brightness key, Opacity
  halves the 14 alpha keys and Light range doubles the point light's range key, each changing nothing else. Not
  checked: how it looks.
- [x] Save in place: the mod gets vfx/instant_edit/<name>_<hash>.avfx mapped in the
  option the effect is in, and the timeline names it. Reopen the timeline: the Look
  shows the saved tint and colours, and a further change makes a copy without a
  second hash in its name. Undo removes the copy.
  (XIV Bridge tests/instant-edit/animations/177, 2026-10-10, Angel Dust's dustsmoke: vfx/instant_edit/dustsmoke_915896a6.avfx,
  mapped and named by the timeline; reopened, the Look shows the red tint and the swapped colour; a blue change makes
  dustsmoke_af5385fc.avfx; each Undo removes its copy and mapping. The mod has no options, so "the option the effect is
  in" is the default one.)
- [x] Each drag of a look control undoes in one step; a lane event with a changed look
  shows an amber dot and "Look changed." in its tooltip.
  (XIV Bridge tests/instant-edit/animations/178, 2026-10-10: a tint drag and a hue drag each undo and redo in one step;
  the event carries the look while it is changed and drops it on Reset look or another effect file. The dot and the
  tooltip line are drawn exactly when the event has a look (TimelineWindow.Canvas.cs:539, 796); they weren't looked at.)

Length:
- [x] Make a loop emote 1.5x longer with Scale events on. The motion, faces, sounds
  and footsteps stretch together; validation passes (the old retime always failed it).
  (XIV Bridge tests/instant-edit/animations/179, 2026-10-10, Ball Dance: 196 to 294 frames; every event's start scaled
  (a face and 12 footsteps; it has no sounds), the motion lasts 9.8 s for 6.53 s and the save validated. Many other
  emotes fail pose validation on a length change; see the ninth session's notes in XIV Bridge's HANDOFF.md.)
- [x] Make a one-shot emote longer. Check whether the shared chara/action/<key>.tmb
  cuts it off at the old length. If it does, the editor needs an option to edit that
  file too.
  (XIV Bridge tests/instant-edit/animations/180, 2026-10-10: it doesn't. Played on the GPose actor without the loader's
  hold, as the game starts it, Wave's own timeline ends 3.19 s after it starts (its motion is 3.57 s); made one second
  longer and saved into a new mod it ends at 4.20 s.)
- [ ] A clip with root motion or Havok annotations refuses a length change with a
  clear reason.
  Not checked (XIV Bridge tests/instant-edit/animations/181, 2026-10-10): no loader row's game file has root motion or
  annotations; it needs a modded clip with some.
- [ ] Open Red Carpet's Popoto Step dance (header 30 frames, motion 455). The length
  reads 15.17s, a note says the header says 1s, and 39 events are past the end.
  Make it 1.5x longer: the dance lasts about 22.8 s and isn't squeezed. Saved, the
  header scales to 45 and the motion entry to 683. Also watch whether the second
  loop's faces (frames 455 to 907) play: if they do, the past-end warning overstates.
  Partly checked (XIV Bridge tests/instant-edit/animations/182, 2026-10-10): 15.17s, the note "The file's header says
  1s, but the motion lasts 15.17s.", 1.5x longer it lasts 22.77 s, saved with the header 45 and the motion entry 683.
  But the file now has 32 events past the end, not 39, and all are footsteps (C042): no face starts past the end, so
  the second-loop faces question can't be asked of this file.

Preview (GPose):
- [x] Preview adds a temporary Penumbra mod (Penumbra's Temporary view shows it at
  maximum priority) and the GPose actor plays the edited clip, even when another mod
  replaces the same PAP. Replaying alone kept the old file (2026-10-07), so Preview
  redraws the GPose character after each new revision (Penumbra RedrawObject, waiting
  for GameObjectRedrawn), then plays.
  (XIV Bridge tests/instant-edit/animations/183, 2026-10-10: with Nhagi's mod replacing Idle pose 4, Preview's temporary
  mod is registered at priority 2147483647 and the clip resolves into the preview's folder; the actor is redrawn once
  per revision and the listener hears the previewed file; after Stop the clip resolves to the mod's file again and the
  temporary mod is gone. Penumbra's Temporary tab itself wasn't looked at.)
- [x] Remove an expression and press Preview: the character flickers once and the
  expression no longer plays. Press Preview again after another change: it shows too.
  (XIV Bridge tests/instant-edit/animations/184, 2026-10-10: the character is redrawn once per Preview and
  plays without the removed expression; the second Preview shows the next change.)
- [ ] Stop puts the character back: it is redrawn once more and plays its own file
  (the removed expression is back when the emote plays). Turning Live on after a
  preview waits for that redraw. Check whether a Brio or Ktisis pose survives the
  redraws.
  Partly checked (XIV Bridge tests/instant-edit/animations/184, 2026-10-10): Stop redraws once more and the clip's own
  file plays again with the removed expression; Live asked for right after Stop waits until the character is drawn
  again, then starts. Not checked: a Brio or Ktisis pose.
  Found with 202 (2026-10-10): after the preview's redraw the GPose character's head turns to the camera (35° on the
  neck, GPose's Face Camera) and stays so after Stop, where it looked ahead before. Check by eye whether a Face Camera
  turned off by hand survives a Preview, and decide whether the preview should keep the head as it was.
- [ ] Pause freezes body and face; scrubbing moves the pose without drift; Resume and
  speed work; sounds and effects fire only while playing. Check alongside Brio and
  Ktisis, which hook the same speed function.
  Partly checked (XIV Bridge tests/instant-edit/animations/185, 2026-10-10): paused, body and face move 0.04°; the clip's
  frame follows the playhead and scrubbing back to a frame gives the same pose (0.00°); it runs at 30.0 frames/s,
  15.0 at 50% and 60.0 at 200%. Not checked: sounds and effects while paused, and Brio or Ktisis loaded.
- [ ] Stop, closing the window, leaving GPose and unloading the plugin each end the
  preview: the temporary mod is gone, the actor is not left locked, and the
  animation-preview cache folder is cleaned. After a forced crash the next load
  removes them.
  Partly checked (XIV Bridge tests/instant-edit/animations/186, 2026-10-10): Stop, closing the window and leaving GPose
  each remove the temporary mod and the session's cache folder, and nothing keeps playing the preview; after Stop and
  closing the window the actor's lock mode is 1 again (8 while the loader held it). Not checked: unloading the plugin
  and a forced crash.
- [ ] With a shared collection the window warns that others see the preview too.
  Lightless/PlayerSync do not upload preview files.

Window:
- [ ] Edges, bodies, stacked markers and the length handle are easy to grab at normal
  and zoomed-out levels. Ctrl+wheel zooms at the cursor without scrolling the window.
  Partly checked (XIV Bridge tests/instant-edit/animations/187, 2026-10-10): 32 hit tests on the real layout find every
  event where it is drawn at 1, 3, 9 and 30 px per frame; 16 zoom steps keep the frame under the cursor (0.000 frames
  off) and stop at the limits; the window has NoScrollbar and NoScrollWithMouse. Not checked: whether it feels easy
  by hand.
- [ ] With the window focused, Delete, arrows and Ctrl+Z/Y/C/V/D/A don't reach the game.
  Partly checked (XIV Bridge tests/instant-edit/animations/188, 2026-10-10): with the window focused ImGui wants the
  keyboard, and each key acts on the timeline. Feeding keys to ImGui can't show what the game receives: press them in
  a GPose scene and watch the camera and the character.
- [x] Every field edit in the properties panel undoes in one step. The window stays
  visible in GPose and the On Screen/Animations tabs keep working.
  (XIV Bridge tests/instant-edit/animations/189, 2026-10-10: 38 field edits of sound, effect, face, lip and prop events
  each undo and redo in one step; in GPose the window stays drawn while the main window shows On Screen and Animations.)

## Expressions (pending)

Expression emotes (/smile, /taunt...) play a face on the face skeleton, timed by a TMB of
their own, chara/action/facial/pose/<name>.tmb. The listener now looks for the face in the
library that TMB names (its TMPP) and in the face's resident face.pap. Before, it only
looked in nonresident/<name>.pap, which misses Straight Face, Shut Eyes, Endure, Ouch and
Concentrate. Offline: Keep on a vanilla expression writes exactly what Permanent Facial
Expressions ships for Alert and Straight Face; its other 27 files also set the header to
-1. 312 vanilla and mod expression timelines read and save back byte for byte.

In-game facts (2026-10-05, Panting Face [Taunt], a 600-frame moving face):
- Time control on with a frame range past the face's own length (0..1,000,000,000 over
  as many frames, or 0..2^32 over -1) drops the face at once.
- Time control off plays the face once at its own length, even with an endless (-1)
  timeline header. Time control on, 0..600 over 600 with a 600-frame header, also plays
  once: the expression's timeline does not repeat.
- -1 with the clip's own range (the mod's Static Infinite) holds the face still.
- The same face three times back to back (frames 0, 600, 1200, each 0..600 over 600)
  plays for the full minute, with the header at 1800, -1 or left at 150. Entries past
  the expression's header play.
- The editor's own Keep output for Panting Face (54 plays, then the kept copy) keeps
  playing, and a short run (4 plays, then the kept copy) holds the face still after the
  last play (test pack 3, 2026-10-05). The user wants the face back to neutral at the
  end instead, so the last copy now plays normally.

So Keep on a moving face (its own length over 2 frames, from the clip the listener
matched) writes it as copies back to back on its own track, up to frame 32,767 (the
last a 16-bit entry time reaches, about 18 minutes); after the last play the face
returns to neutral. Loops saved with a kept last copy (test pack 3) still read as loops
and are rewritten without it when edited. NFLB's Cow Taming keeps 19 faces on one track; no library
timeline has more than 26 tracks on one actor, so copies never get tracks of their own.
Reading collapses such a run back into one looping face. A play keeps a mod's own speed
when its range covers the whole face (Half Speed loops at half speed), else it is the
face once at its own length.

- [x] Do /smile, /taunt and /bow (Shut Eyes, resident). Each is listed with a face icon
  and "Expression" while it plays, and stays under Played recently once its face
  skeleton is matched. The timeline's Source page shows the face's PAP and the timeline
  file with its mod. The save footer has no Skeleton choice.
  (XIV Bridge tests/instant-edit/animations/200, 2026-10-10, played from the loader: Smile, Taunt and Shut Eyes, with
  the PAPs nonresident/smile.pap, nonresident/comeon.pap and resident/face.pap and the timelines facial/pose/smile.tmb,
  comeon.tmb and bow.tmb, all from Game Data.)
- [ ] Adjust face bones in LivePose during an expression, then Face page > **Bake LivePose
  into the face**. The expression plays with the adjustment and LivePose's face pose for
  it is cleared.
- [x] Untick a face bone under Animated bones on the Face page, then **Save the face
  without them**: that bone stops moving.
  (XIV Bridge tests/instant-edit/animations/201, 2026-10-10, after the fixes that send the matched clip and bake
  deprecated-additive faces the way the game applies them.)
- [ ] Send an expression to Blender with the pen.
- [x] Open an expression's timeline. The header shows a face, the length can't change
  and Save writes only the TMB. Preview in GPose plays the expression on the face
  only: the body keeps its pose. Stop returns the face to neutral.
  (XIV Bridge tests/instant-edit/animations/202, 2026-10-10, Smile: the length is fixed and its handle only scrubs; the
  preview moves the face 2.0° while the body stays within the idle's sway and its timeline is untouched; Stop puts the
  face back (0.02°); the save writes only chara/action/facial/pose/smile.tmb.)
  Unticked: since a later suite run, and after a relog, the body moves 6.5-7.3° (the idle sways 0.9°) while the face
  preview plays, and the face is 0.58° off neutral after Stop. Not explained yet; rerun 202.
  Explained 2026-10-10: all of it is the neck (j_kubi 35°; arms, legs and spine stay within 1.3° of their idle): the
  head turns to the camera after the preview's redraw and stays so after Stop. That is GPose's Face Camera (on again at
  every GPose start; the user turns it off by hand), not the expression playing on the body. The script now leaves
  the neck out and passes. See the redraw item under Preview.
- [ ] Tick Keep on /smile's face and save in place. In game the smile stays after the
  expression ends, until another expression replaces it. Undo last save restores it.
  Partly checked (XIV Bridge tests/instant-edit/animations/203, 2026-10-10), saved into a new mod rather than in place
  (/smile comes from the game's files): the kept smile is still 2.00° at 8.5 s where the plain one is back at 0.01°;
  /sad replaces it; after Undo it ends again. In place, an expression's save fails (see Panting Face below).
- [x] Open Permanent Facial Expressions' smile: the face reads kept, a note says the
  header is endless, and an unchanged save is refused (nothing changed).
  (XIV Bridge tests/instant-edit/animations/204, 2026-10-10: kept, "The file's header gives an endless length (-1). It
  stays that way.", and Save says "Nothing changed since the last save.")
- [x] Open Panting Face's /taunt. The face reads 20s long. Tick Keep: it reads "Loops
  for about 18 min" and is drawn to the end. Save in place, /taunt and watch for over
  a minute: the face keeps panting with no visible pop between plays, and /smile
  replaces it. Reopen the timeline: one looping face, not 55.
  (XIV Bridge tests/instant-edit/animations/205, 2026-10-10, after the fix that backs up the expression's .tmb: saved
  with 55 plays, it pants as strongly after a minute; the biggest frame-to-frame steps (up to 7.7° against 5.4° in
  the first play) fall at 38-50 s, not every 20 s where plays meet; /smile replaces it; reopened it is one face.)
- [x] With Panting Face's Static Infinite option, the face reads "Holds still" and Keep
  is unticked; ticking it makes it loop. With Half Speed it loops at half speed.
  (XIV Bridge tests/instant-edit/animations/206, 2026-10-10, after the fix that backs up an expression's .tmb for an
  in-place save.)
- [ ] A loop that runs out returns the face to neutral. Check with a loop started late
  (move the face to about 31,000 frames, about 1 minute of loop) rather than waiting
  18 minutes.
  Partly checked (XIV Bridge tests/instant-edit/animations/207, 2026-10-10): moved to frame 31,000 the kept face still
  loops and reads about 1 minute, saved as three plays (31000, 31600, 32200) ending at 32800. Not checked: watching
  the face return to neutral (about 19 minutes in; run it with Args wait=true).

## Motion editor live acceptance (pending)

Bone layers (offset, amplitude, pose blend) live in the Timeline window's document and
are baked by the existing save path: `AnimationBakeService.Session.ApplyMotion` runs
`MotionEvaluator` over every sampled pose before the build. Live shows them on the GPose
actor with no hooks: `MotionLiveDisplay` swaps an uncompressed copy of the clip (the
samples a bake compresses) into the actor's Havok controls, and puts the clip's own
binding and track weights back afterwards. The evaluator, layer edits, angles, rows, hit
zones, gestures, documents and journals are covered offline (MotionLayerFixture,
MotionEditsFixture).

Spike, checked in game 2026-10-06 with a fixed test layer (the neck bent 30 degrees about
its own Z axis over the middle third, which tilts the head forward): Live showed it, with
pause, scrubbing, replay and speed; toggling the layer under Live, Stop, leaving GPose and
unloading the plugin with Live on all worked; Preview played the built file. Saving worked
but stacked: the reload after a save dropped the layer, so adding it again baked a second
tilt onto the saved result. The window now keeps the layers and the motion before its
first save (below).

Bone layers (reported good in game 2026-10-06):
- [x] Add > Offset layer: a bar covers the whole clip in the Bones rows and the panel shows
  it. Choose the neck in the panel and drag Turn with Live on: the head follows the drag
  and a key diamond appears at the playhead.
- [x] Move the playhead and turn the neck the other way: a second key. Scrubbing goes
  smoothly between them with Smooth, evenly with Linear and holds with Hold.
- [x] Drag a bar's ends, the fade handles on its top edge, the whole bar and a key. Each
  follows the pointer and snaps to edges and keys (Alt drags freely); Ctrl+Z undoes each
  drag in one step. Alt+arrows nudge the selected key or layer.
- [x] Double-click the empty Bones row: a one-second offset section appears there.
  Right-click a layer: turn it off (Live drops it), duplicate, apply earlier or later,
  delete; right-click a key: delete it.
- [x] Add > Amplitude layer on an emote with clear arm motion: at 200% the arms swing
  twice as far, at 0% they hold their average pose. Limit it to one bone: only that bone
  changes.
- [x] Add > Pose layer, choose a bone, press Key, then turn it: the bone holds that
  rotation over the layer, and Blend 50% goes half way. Tick Move to set its position too.
- [x] Save into a new mod: the layers stay in the editor, not dirty. Change a value and
  save in place: the result replaces the earlier one, the tilt doesn't double. Undo last
  save: the layers stay as unsaved changes.
- [x] Turn every layer off and save in place: the clip's original motion comes back.
- [x] Key a bone outside IVCS + YAS that the clip doesn't animate: the save footer warns
  that sync plugins can refuse the file.
- [x] Change the length with a layer present: Live asks for Preview instead; saving bakes
  the layers in for good and they leave the editor.

Listener in GPose, reported good in game 2026-10-06 (it follows the GPose target when that is a GPose actor, else the posed
copy of your character; outside GPose your own character):
- [x] In GPose, play an emote on the GPose actor (Brio, or the GPose animation controls).
  It shows under Playing now without leaving GPose, and the list's status reads
  "Listening in GPose". Target another GPose actor: its animations are listed instead.
- [x] With Live on, the clip stays listed as playing while you edit.
- [x] A clip captured in GPose: its LivePose tab says the adjustments are read from your own
  character outside GPose. Save its timeline in GPose: it works. Leave GPose and save
  again: the message says the GPose character is gone.
- [x] Leave GPose: the list follows your own character again, and what played in GPose
  stays under Played recently.

Bones window (Phase 4; the map's data, selection rules, edits for several bones and Mirror
are covered offline in PoseEditorFixture):
- [x] The Timeline toolbar's figure button opens the Bones window, also in GPose; it closes
  with the Timeline window. Brio's body, armor and hand pictures show, with toes for IVCS
  skeletons and a tail for races with one. Dots sit on their joints and bones missing from
  the clip's skeleton have none.
  (XIV Bridge tests/instant-edit/animations/210, 2026-10-10, Idle pose 3 in GPose: the button opens the window, drawn
  with its map and bone panel; the five pictures are embedded and the layout holds body, armor, hands and tail (no toes
  on the game's skeleton; IVCS and IVCS + YAS get toes); every dot is a bone of the skeleton and the 30 map spots
  whose bones it lacks (iv_ bones) have none; closing the Timeline window closes it. Where the dots sit on the pictures wasn't looked at.)
- [x] Hover a dot: its name and state show. Click selects it, Ctrl+click adds or removes,
  Shift+click selects it with everything below it, a click on empty space clears. The lines
  below the primary bone light up. Swap sides moves the left side's dots to the right.
  (XIV Bridge tests/instant-edit/animations/211, 2026-10-10: the selection's own calls; the dot texts read e.g.
  "Neck (j_kubi)\nKeyed here in this layer." and "Root (n_root)\nThe clip doesn't move it."; Shift+click on the left
  upper arm selects its 19-bone branch; the forearm's and hand's lines are lit, the clavicle's and the right arm's not;
  Swap sides puts the left arm's dot on the right.)
- [x] Select > Left fingers, Spine and the other groups select those bones; with a layer
  selected, "Bones this layer changes" selects its bones. All bones lists the skeleton as a
  tree, coloured like the dots, and a search narrows it.
  (XIV Bridge tests/instant-edit/animations/211, 2026-10-10: Spine is j_kosi and three spine bones, Left fingers 10
  bones (Ctrl adds), Tail 5; "Bones this layer changes" selects the layer's 2; the tree reaches all 106 bones once and
  "knee" narrows it to 4; the All bones page draws its list. The colours weren't looked at.)
- [ ] Select an offset layer and the neck: the rotation ball's rings follow the camera as you
  orbit it. Drag a ring with Live on: the head turns about that axis, a key appears at the
  playhead, and Ctrl+Z undoes the whole drag. The wheel over a ring turns it in steps, Shift
  finer, and a right click locks the ring. Character axes turn it about up, forward and to
  the side whatever its own axes are.
  Partly checked (XIV Bridge tests/instant-edit/animations/212, 2026-10-10), on the thigh rather than the neck (in GPose
  the spine, neck and head follow the camera, which skews the readings): a 30° drag on the Y ring with Live on and
  paused turns the bone 30.0° about its own Y axis and it stays; a key appears at the playhead; Ctrl+Z undoes the whole
  drag; with Character axes the X, Y and Z rings turn it exactly about the character's side, up and forward. Not
  checked: the rings following the camera, the wheel, Shift and the right-click lock (they need the mouse).
- [x] Select a side skirt bone and a leg: Bone axes greys out and the ball's rings are the
  character's. A drag turns both the same way about their own joints; so does a Turn field.
  Key, Delete key, Reset and Clear act on every selected bone.
  (XIV Bridge tests/instant-edit/animations/213, 2026-10-10: a skirt bone and j_asi_a_l both turn, so the
  rings are the character's. The live turns used both legs, as the skirt is physics-driven: a drag turns both 20.0°
  about up and a Turn field turns both the same way (20.0° about the same axis); Key, Delete key, Reset and Clear act
  on both.)
- [x] Shift+click the upper arm and drag a ring: the arm turns as one piece, the forearm and
  hand follow without turning further. Make the hand the primary bone (Ctrl+click it twice):
  only the hand turns.
  (XIV Bridge tests/instant-edit/animations/213, 2026-10-10: the arm turns as one piece (upper arm 20.0°, forearm
  and hand 0.0° further, the hand following 20.0° in model space); with the hand primary only j_te_l turns, 20.0°, and
  the arm's attachment bones stay.)
- [x] Key the left upper arm raised, then Mirror: the right arm raises the mirrored way,
  also when the body is turned in the clip, and the right arm's earlier keys in that layer
  are replaced.
  (XIV Bridge tests/instant-edit/animations/214, 2026-10-10: on Idle pose 3 (body turned 3.7°) and on Flame Dance at
  frame 545 (59.2°) the right arm turns the same 50.0° about the mirrored axis (0° off), and its earlier key is replaced.)
- [x] A pose layer: the ball turns the selected bones from the clip's own pose; Reset keys
  that pose back. An amplitude layer: Limit to selected adds the selected bones to its list.
  (XIV Bridge tests/instant-edit/animations/215, 2026-10-10: the key is the clip's own neck rotation turned 20.0° and
  Reset keys the own rotation (0.00°); Limit to selected adds the two selected bones, then a third, and greys out once
  they are all listed.)
- [x] Previous and next key step the playhead between the layer's keys; the arrows step it a
  frame (Shift ten) while the Bones window has focus, without moving the character.
  (XIV Bridge tests/instant-edit/animations/216, 2026-10-10, clicked and keyed: 30 to 50 and back to 30 and 10, greyed
  at the ends; Right 11, Shift+Right 21, Left 20, Shift+Left 10; the character didn't move.)
- [x] Bone names on the Bones tab and in a layer's bone picker read "Left Knee (j_asi_b_l)",
  "Left Foot (j_asi_d_l)" and "Body (n_hara)"; the Face page names face bones the same way.
  (XIV Bridge tests/instant-edit/animations/217, 2026-10-10: those three exactly, and the Face page reads e.g.
  "F Dhoho Left (j_f_dhoho_l)".)

Bones on the character (Phase 5, reported good in game 2026-10-06; the gizmo's math and moves
for several bones are covered offline in PoseEditorFixture):
- [x] In GPose with the Bones window open, dots sit on the GPose character's joints, joined to
  the bone above, coloured like the map's, also for a tall or small character and after
  turning or moving the character. They stay behind the Bones window and every other window.
  The crosshairs button in the Bones window hides and shows them; outside GPose there are none.
- [x] Hover a dot: its name and state show, and the click selects it without turning the
  camera. Ctrl and Shift work as on the map. Dragging the camera across dots selects nothing.
- [x] Live off: the Turn/Move choice says to turn on Live, and no gizmo shows. Live on and
  playing: it says to pause. The new play button beside Live pauses and resumes.
- [x] Live on, paused, an offset layer and the neck selected: the gizmo's rings sit on the
  neck. Drag a ring: the head turns that way and stays there when you let go, without the
  gizmo jumping back; a key appears at the playhead and Ctrl+Z undoes the whole drag.
- [x] Bone axes: the rings follow the neck's own axes. Character axes, or several bones
  selected: they are the character's, and every selected bone turns the same way about its
  own joint (a bone below another selected one follows it).
- [x] Gizmo Move: the arrows move the hand and it follows the mouse along each arrow; in a
  pose layer the key then sets the position (Set position ticks itself), starting from where
  the clip has the bone.
- [x] With Brio's overlay open as well, dragging one gizmo never moves the other.

Key a pose (Phase 6; .pose parsing, model-to-local conversion, moved bones, proportions and
keying are covered offline in PoseFileFixture; all 8,897 poses in E:\FFXIVmods\Poses parse, 60%
of them were made on bodies of other proportions than IVCS c0801):
- [x] Select a pose layer: Key a pose shows under the keys in the properties panel and under
  the bone panel in the Bones window. Offset and amplitude layers don't have it.
- [x] Pose file: pick an Anamnesis, a Ktisis and a Brio .pose. The name and tool show; Key at
  the playhead keys the whole body (no hair, skirt or weapon bones unless ticked) as one undo
  step, and with Live on the character takes the pose there and blends back out of it.
- [x] A lying or sitting pose made on your own race: Move bones is ticked, the body lands where
  the pose has it. A pose made on another race: Move bones is off with the proportions note,
  and the character keeps its own proportions.
- [x] Selected bones: select an arm, key a pose: only the arm takes the pose.
- [x] Character pose: in GPose, pose the character with Brio or Ktisis, then key it: the clip
  takes that pose at the playhead. Outside GPose it reads your own character.
- [x] Frame of an animation: pick another emote, drag Frame, key: the bones take that frame's
  pose. Picking this clip itself (this one) needs no reading.
- [x] Open a startup: Loop's first frame, at the end keys the loop's first pose at the
  startup's last frame; with a fade-in the startup now runs into the loop without a jump.

Edit again (Phase 7; which edits qualify, the motion a save keeps, the capture, the file check and
older journals are covered offline in EditAgainFixture):
- [x] Save a clip with a bone layer in place. On the Edits tab its row has an Edit again button
  (pen); it is greyed out, with the reason in its tooltip, for edits without bone layers, undone
  edits and edits that changed the length.
  (XIV Bridge tests/instant-edit/animations/220, 2026-10-10: offered for layered saves into a new mod and in
  place; refused with "This edit was undone.", "Only edits with bone layers can be edited again." and "This edit
  changed the length, which baked its layers in for good." After an undo the next save goes through without a new
  capture.)
- [x] Close the Timeline window, reopen it with Open timeline and nothing selected, and press Edit
  again on the start page's Edits tab: the clip opens with its layers, "The saved layers are
  back." shows and Replace in-place is chosen. With Live on the layers show once, not twice.
  (XIV Bridge tests/instant-edit/animations/221, 2026-10-10: Idle pose 3 with a 20° neck layer; Live shows about 16-20°,
  not 40.)
- [x] Change the layer and save: the motion changes only by the new amount (nothing stacks).
  Undo last save goes back to the first save, and the layers stay editable.
  (XIV Bridge tests/instant-edit/animations/221, 2026-10-10: changed to 10°, the game plays 9.7°, not 30°; after Undo
  it plays the first save's 19.7° and the layer stays as an unsaved change.)
- [x] Open the clip from the Animations tab instead: "Saved with bone layers." and Edit again
  show above the lanes, and Edit again brings the layers back.
  (XIV Bridge tests/instant-edit/animations/221, 2026-10-10.)
- [ ] After reloading the plugin, Edit again still works.
  Partly checked (XIV Bridge tests/instant-edit/animations/222, 2026-10-10): a new journal store reads the record and
  the kept motion back from disk as a reload does, and Edit again opens the clip from the record's own capture with
  its layer and in place. The plugin itself wasn't reloaded.
- [x] Save the clip again without its layers (a sound only), then press Edit again on the older
  edit: it refuses, because the animation was changed after that edit.
  (XIV Bridge tests/instant-edit/animations/223, 2026-10-10: "The animation was changed after this edit.")
- [x] An edit saved into a new mod: Edit again opens that mod's file, and saving in place
  writes into that mod.
  (XIV Bridge tests/instant-edit/animations/224, 2026-10-10: the in-place save writes a new file in the same mod; no
  second mod appears.)

Docs (Phase 8):
- [x] The changelog's 3.0.0 lists the bone layer entries under Timeline, and the Animations
  tab's hint beside the open-timeline button names bone layers.
  (XIV Bridge tests/instant-edit/animations/225, 2026-10-10: eight entries under "Timeline: bone layers change how the
  animation moves, live on your character in GPose."; the hint for a body clip reads "Events, length, bone layers,
  LivePose, animated bones, skeleton repair and a preview in GPose.")

Key eases and bone rows (2026-10-07; curves, per-key sampling, bone key moves, rows, hits and
gestures are covered offline in MotionLayerFixture and MotionEditsFixture):
- [ ] Select an offset layer's key: under the keys the panel shows "To the next key" with the
  layer's way and a curve. Pick Hold: the key turns into a square and with Live on the bone
  stays put until the next key, then jumps. Slow start and Slow end visibly lag or lead.
  Partly checked (XIV Bridge tests/instant-edit/animations/230, 2026-10-10, a 0° to 40° neck layer with Live paused):
  a selected key draws the ease combo and curve, none without a key; Live matches the evaluator at every frame
  (0.001°) for Smooth, Hold, Slow start, Slow end and Linear; Hold stays at 0.0° until frame 59 and is 40.0° at 60;
  Slow start lags (7.4° against 13.3° at frame 20) and Slow end leads (16.3°). Not checked: the square key marker.
- [ ] Drag a handle of the curve: the key turns into a circle, the ease becomes Custom and Live
  follows while dragging; the drag is one undo step. A handle pulled above the box makes the
  bone overshoot the next key's value and settle back.
  Partly checked (XIV Bridge tests/instant-edit/animations/230, 2026-10-10): the ease becomes Custom, Live follows the
  drag (28.1°, 36.8°, 45.5° at frame 20), one Undo takes it back, and an overshooting curve reaches 60.0° and settles at
  40.0° on the key. Not checked: the circle key marker.
- [ ] Right-click a key: To the next key lists the layer's way and the presets.
  Partly checked (XIV Bridge tests/instant-edit/animations/230, 2026-10-10): the presets are Linear, Smooth, Slow start,
  Slow end and Hold, and the layer's way is Smooth. The menu itself wasn't opened (it needs a right click).
- [x] The arrow at the right of a layer's name unfolds a row per keyed bone, parents first
  (an amplitude has no arrow). Folding is no undo step and stays after a save.
  (XIV Bridge tests/instant-edit/animations/231, 2026-10-10: the rows come parents first also for a clip matched after
  capture; an amplitude has no rows; folding is no undo step and stays after a save.)
- [x] Click a bone row: the bone is picked in the panel and the Bones window. Drag one of its
  keys: only that bone's value moves; dropped on another key's frame it joins that key.
  Delete and Alt+arrows act on that bone alone. The layer's own row still moves whole keys.
  (XIV Bridge tests/instant-edit/animations/232, 2026-10-10: the editor's press, drag and key calls on the real layout.)
- [x] Save and Edit again: the eases come back with the layers, and the saved motion matches
  what Live showed.
  (XIV Bridge tests/instant-edit/animations/233, 2026-10-10: Slow start and a custom curve come back with the
  layer; the saved file's neck matches the evaluator within 0.1° and what Live showed; the window decodes the saved
  file after the save.)

## Animations tab into the Timeline (2026-10-08, pending)

The Animations tab keeps only the listener and a Timeline section; every other tool moved into
the Timeline window. Covered offline (TimelineToolsFixture): LivePose stacks and .livepose files
as an offset layer, the loader's list (also run against the real sheets: 7 idles, 4 ground
sits, 5 chair sits, 3 bed poses, 16 movement tracks, 29 expressions) and all 56 files in
E:\FFXIVmods\Poses\LivePose, unticked bones as an undoable document edit, a timeline save's
request carrying unticked bones and the repair target, Edit again refusing such saves, and the
save's bone problems. Live display, playback, the bake and the layout need the game.

Animations tab:
- [x] The tab shows the list and, below it, the selected clip's name, length and mod, any
  effect loop warning and Open timeline. No tool tabs are left.
  (XIV Bridge tests/instant-edit/animations/14, 2026-10-10 and a screenshot: "Standing Idle - Loop 4 · Loop · playing", "Length 2.33s · Game Data",
  Open timeline opens the selected clip. The effect loop warning wasn't seen: the clip has no effects.)
- [x] With nothing selected, Open timeline opens the Timeline window on its loader.
  (XIV Bridge tests/instant-edit/animations/14, 2026-10-10.)
- [x] An edit that did not finish shows a banner pointing to Edits in the Timeline window.
  (XIV Bridge tests/instant-edit/animations/14, 2026-10-10: an edit record in state Prepared, in memory only.)
- [x] Switching to another tab with the Timeline window open keeps the listener running
  (the window's Source page still updates); closing both stops it.
  (XIV Bridge tests/instant-edit/animations/14, 2026-10-10: on On Screen the window follows Idle pose 2 from the loader; it stops with the Timeline
  window closed and the main window on another tab or closed.)

Loader (GPose only):
- [x] With no clip open, the window shows Load an animation, Edits and Record live pose tabs.
- [x] Outside GPose every row is greyed with "Enter GPose to play the preview."
- [x] In GPose pick Emotes > Dance: the GPose character dances, the line says it waits, and the
  timeline opens Dance by itself within a second or two. The character keeps dancing.
  (XIV Bridge tests/instant-edit/animations/02: opens in 0.5 s, the body's cbem_dance (97 bones) rather than the 6-bone
  cbep_e_dance: of the clips a timeline plays the loader opens the one moving the most bones, 2026-10-10.)
- [x] Idles > Idle pose 3, Ground sits > Ground sit pose 2, Chair sits > Chair sit pose 4 and
  Lying > Lying pose 1 each play that /changepose pose and open its loop.
- [x] Movement > Run, Sprint and Jump Start each open their own track (run_start etc. too).
- [x] Expressions > Smile plays on the face and opens the expression's timeline.
- [x] Other: search "chair talk", play it, and its timeline opens.
  (XIV Bridge tests/instant-edit/animations/06, 2026-10-10: opens in 1.4 s. The listener hears an Other timeline once
  the loader plays it, not in everyday play.)
- [x] The toolbar's folder button opens the loader as a popup while a clip is open; with unsaved
  changes the inline Discard / Keep editing question comes first.
- [x] Something that never plays (a weapon skill without the weapon) shows the warning after
  about six seconds; Cancel stops waiting. Stop in the preview bar puts the character back.

Side panel:
- [x] Tabs: Properties, Source, Bones (Face for an expression), Edits, Record. Selecting an
  event or layer jumps back to Properties.
- [x] Source shows mod, file, game path, model, startup (Open opens the startup's timeline),
  live skeleton and Made for; an ambiguous clip's skeleton can be chosen here, and the Bones
  window follows the choice. The cog opens Settings.
  (XIV Bridge tests/instant-edit/animations/10, 2026-10-10: every field shows; Open opens the startup's timeline
  without the draw failing; choosing another of the clip's skeletons moves the Bones window with it; the cog.)
- [x] Bones: untick a hair bone with Live on in GPose: the hair goes to physics at once.
  Ctrl+Z ticks it again. Keep only Vanilla unticks the iv_/ya_ bones. Save writes the clip
  without them and the saved file plays the same.
  (XIV Bridge tests/instant-edit/animations/16, 2026-10-10, Idle pose 3, j_kami_a: unticking with Live on keeps
  Live running and undo ticks it again; a save leaves out its track and the game plays the saved file within
  1.4° of the original. Hair physics drives the hair bones in the game itself: a 30° layer turn on j_kami_a
  shows as about 1° both with Live and in the saved file, so the switch to physics isn't visible. Ctrl+Z: test
  11; Keep only Vanilla: test 12.)
- [x] Unticking a bone a layer moves blocks Save and names the bone.
- [x] Edits lists saved edits with Edit again, Undo and Restore offsets. Undo of the open clip's
  save reads the timeline again; with unsaved changes it is greyed.
  (XIV Bridge tests/instant-edit/animations/13, 2026-10-10: a click on Undo with unsaved changes does nothing; without them it marks the edit
  Undone, reads the clip again from the game's files and turns the new mod off.)
- [ ] Record records and sends to Blender as the old tab did; the countdown slider wraps below
  Length in the narrow panel.
  Partly checked (XIV Bridge tests/instant-edit/animations/240, 2026-10-10): in the 304 px panel the countdown slider is
  a row below Length; Record counts down 3 s, records the GPose character (187 bones at 60 fps) and Stop ends it; the
  take goes to a receiver answering as Blender's add-on, after a status request, and "Send last recording again" sends
  the same bytes. Real Blender wasn't used.

LivePose:
- [ ] Add > Offset layer from LivePose: a layer named LivePose covering the clip with one key
  appears and is selected; with Live on the pose shows twice until "Clear them in LivePose",
  and "Put them back" restores LivePose's own.
  Partly checked (XIV Bridge tests/instant-edit/animations/241, 2026-10-10, a LivePose read handed in as the service
  hands it): a layer named LivePose over the whole clip with one key is added and selected, the note counts the bones,
  Clear them in LivePose / Put them back are offered; unavailable or empty LivePose adds nothing and says why. Not
  checked: Live with LivePose loaded, and the two buttons (they write LivePose).
- [x] Add > Offset layer from a .livepose file: pick one from E:\FFXIVmods\Poses\LivePose; the
  layer is named after the file and Live shows the pose. Bones the skeleton lacks are counted.
  (XIV Bridge tests/instant-edit/animations/15, 2026-10-10: bend2.livepose on Idle pose 3, the file's result
  handed in as the dialog's callback does. Layer "bend2", one key over the clip, selected; "Added a layer
  from 4 adjusted bones. 1 bone was left out."; with Live, j_naka_a_l turns the layer's 16.5° against Live
  without the layer, which matches the game's playback within 0.4°.)
- [x] A LivePose scale adjustment kept from its children (LivePose's default) becomes a layer
  that scales only that bone: with Live on, its children keep their size, as in LivePose.
  (XIV Bridge tests/instant-edit/animations/242, 2026-10-10, from a .livepose file: j_ude_a_l x1.5; its children get
  x0.667, and with Live on the arm is 1.5x bigger while j_ude_b_l and n_hkata_l keep their size in the model (x1.000).)
- [ ] On an expression: Face page > Bake LivePose into the face and Save the face without
  them run the old face rebakes, into a new mod or in place as the footer says.
  Partly checked (XIV Bridge tests/instant-edit/animations/243, 2026-10-10): Save the face without them saves Smile
  into a new mod (which then plays) and in place. Not checked: Bake LivePose into the face (manual: it clears
  LivePose).

Save footer:
- [x] Skeleton: Keep its own / Repair onto Vanilla, IVCS, IVCS + YAS (missing ones greyed with a
  reason). Choosing one enables Save without other changes and shows the highest index.
  (XIV Bridge tests/instant-edit/animations/13, 2026-10-10, Idle pose 3: IVCS enables Save with no other change, highest index 104. All three
  skeletons were in the library, so a greyed item wasn't seen.)
- [x] Repair onto the skeleton the clip was made for is refused. A repaired save plays in game
  and its Edits row says "timeline edit, repaired onto ...".
  (XIV Bridge tests/instant-edit/animations/13, 2026-10-10: Vanilla refused on Idle pose 3; after a redraw the GPose actor plays the loop from
  the new mod; the row says "timeline edit, repaired onto IVCS".)
- [x] A save with unticked bones or a repair bakes the layers in; Edit again on it explains why.
  (XIV Bridge tests/instant-edit/animations/13, 2026-10-10: with a repair; the layers leave the editor and Edit again says the layers were baked in for good.)

## Review fixes 2026-10-10 (pending)

Fixes from the review of 9d39ff1 back to 2.1.0, committed 313e1ad. Automated suites pass; none
of this has been tried in game. Candidates are mods from G:\Penumbra.

Live and the motion editor:
- [x] Skeleton refusal: open an NFLB animation (the NFLB lists in Eorzean-Nightlife+), pick the NFLB
  skeleton under Source, add an offset layer and key an nf_ bone. Turn Live on for a GPose actor
  without NFLB (a Brio NPC, or your character with the NFLB skeleton mod off and redrawn). Expect
  "Your character's skeleton doesn't have every bone these layers move.", Live turning itself off,
  no crash, and the actor playing the plain animation.
  A clip for it (XIV Bridge tests/instant-edit/animations/255, 2026-10-10): [NFLB] Bench Rider [F Talls][Sit2], option
  "F Miqo", Chair sit pose 2: 427 animated bones, 280 of them nf_, matched to a 455-bone c0801 NFLB skeleton.
  (XIV Bridge tests/instant-edit/animations/17, 2026-10-10, Idle pose 3 with a c0801 NFLB skeleton chosen under
  Source, an nf_ bone keyed: the choice survives Live; Live refuses with that message, turns itself off, and the actor
  stays drawn and keeps playing the plain clip.)
- [x] Control for the refusal: keying only a bone every skeleton has (j_kubi) runs Live normally.
  (XIV Bridge tests/instant-edit/animations/17, 2026-10-10: Live runs, keeps running and shows the 20° turn on j_kubi.)
- [x] Memory: on a clip of 30 s or more with Live on, drag the ball or gizmo nonstop for about 10 s
  while watching ffxiv_dx11.exe in Task Manager. Memory rises by tens of MB at most and falls back
  within a second or two after letting go; the bone follows about every 0.1 s.
  (XIV Bridge tests/instant-edit/animations/20, 2026-10-10: Flame Dance, 24 s, the longest of the game's dances;
  Live paused, 10 s of the gizmo's per-frame turn step (574 steps, one undo step): private memory +19 MB at
  most, 35 MB below the start 2 s after letting go; the bone changed every 117 ms (median, longest 134 ms).)
- [x] Loop seam: on a looping idle, an offset layer covering the whole timeline never snaps back to
  the original pose for one frame at the loop point, with Live and after saving.
  (XIV Bridge tests/instant-edit/animations/19, 2026-10-10: Idle pose 3 (2.33 s loop), a 25° offset on j_kubi over
  the whole timeline, read every frame for 7.5 s (3 loop points): the largest step between frames is 0.06°
  with Live and in the saved file, while the layer's 24.9° holds.)
- [x] Faded layers: in an offset layer at 50% amount (or partway into its fade-in), a 30° gizmo drag
  turns the bone the full 30° and the gizmo stays on the bone.
  (XIV Bridge tests/instant-edit/animations/18, 2026-10-10: Idle pose 3 paused at frame 20, the gizmo's own step (Follow, then PoseEdits.Turn with the window's
  targets and frames) on j_kubi: at 50% amount and halfway into a fade-in the bone turns 30.0° about the gizmo's
  axis. The ImGuizmo widget itself wasn't dragged, so where it draws wasn't checked.)
- [ ] Below 5% (the first frames of a fade-in), hovering the gizmo shows "The layer barely applies
  here." and a drag changes nothing.
  (XIV Bridge tests/instant-edit/animations/18, 2026-10-10: at 3% the gizmo's step changes nothing. The tooltip needs a hover over the gizmo: not checked.)
  (XIV Bridge tests/instant-edit/animations/244, 2026-10-10: still not checked. With the injected mouse held near the
  neck's dot and the Timeline window collapsed, ImGuizmo never reported the mouse over its rings; not diagnosed.)
- [ ] Gizmo over dots: with a hand bone selected, a ring over a finger dot drags the ring and doesn't
  select the finger; clicking a dot away from the rings selects it.
  Partly checked (XIV Bridge tests/instant-edit/animations/245, 2026-10-10): with j_te_l selected, a click on j_sako_l
  away from the rings selects it. Not checked: a ring over a dot (ImGuizmo never reported the mouse over its rings, as
  in test 244). On a 3440x1440 screen the Timeline window's side panel covered the character until it was collapsed.
- [ ] Brio's gizmo still flips its axes toward the camera after the Instant Edit gizmo was used.
- [ ] With the Timeline window collapsed, leave GPose in the middle of a gizmo drag. Back in GPose,
  Brio's gizmo and the camera work, and the next edit is its own undo step.
  Partly checked (XIV Bridge tests/instant-edit/animations/246, 2026-10-10): a gizmo drag running when the overlay stops
  being wanted, with the Timeline window collapsed, ends within a few frames as one undo step, ImGuizmo isn't left in
  use and the next edit is its own step. Not checked: a real drag cut off by leaving GPose (the drag never started the
  gizmo, as in test 244), Brio's gizmo and the camera.
- [x] Pose layer Move with several bones selected, including n_hara or j_kosi whose keys only turn
  them: those bones move from where they are now, with no jump.
  (XIV Bridge tests/instant-edit/animations/18, 2026-10-10: j_kosi and j_sebo_a keyed with turns only, then moved 5 cm by the gizmo's step: both move
  exactly 5.00 cm from where they were.)

Timeline editor:
- [ ] Moving face pace: a long animated face (Dawntrail Expression Library [illusio_vitae path]) with
  Time control off shows its own length and "own speed", not "held". Ticking Time control keeps the
  length, and in game the face plays at the same speed as before.
  Partly checked (XIV Bridge tests/instant-edit/animations/247, 2026-10-10) on Panting Face's 600-frame face: it reads
  "20s · 600 frames own speed", not held; Time control keeps 600 frames and the save writes 0..600 over 600 (the same
  speed). Not checked: the Dawntrail library's faces on a body clip. With the "[noff] smoking idle" copy on, its idle
  never opened; it replaces the resident idle.pap, which a redraw probably doesn't reload (a zone change with the mod
  on would).
- [x] Type a new Start in Properties, then click the ruler: toolbar Undo is enabled and one Undo
  reverts only the typed value; a drag afterwards is a separate undo step.
  (XIV Bridge tests/instant-edit/animations/248, 2026-10-10: the toolbar Undo was clicked: Start 25 back to 10, the
  sound and the playhead stay; typed again and dragged 8 frames, two separate undo steps.)
- [x] File changed: open an emote's timeline, then switch a Penumbra option (or enable a
  higher-priority mod) that changes its animation and play the emote again. The banner "This file
  changed in your mods." shows with Reload, which loads the new file; with unsaved edits it asks
  "Reload it and lose them?". No banner right after your own in-place save.
  (XIV Bridge tests/instant-edit/animations/21, 2026-10-10: Idle pose 3; the higher-priority mod is a layered edit
  saved into a new test mod, undone, then turned on again in the test collection; both characters redrawn and
  the idle played again. The game keeps a loaded animation file while any character uses it.)
- [x] A looping face swapped for a pose (cfxf_smile) saves without "length is unknown" and holds.
  (XIV Bridge tests/instant-edit/animations/249, 2026-10-10: swapped, the kept face stops looping and saves as one
  endless pose (range 0..1); in game the smile is still 16.7° at 8.5 s, where the plain expression is back at 0.0°.)
- [x] Loop limit: a moving face of about 11 s or longer can loop; a shorter one shows "This face is too
  short to loop. Use a face made to loop." in the Keep tooltip and doesn't loop.
  (XIV Bridge tests/instant-edit/animations/250, 2026-10-10: the shortest face that can loop is 325 frames (10.83 s);
  5 s and 10 s faces show that tooltip, don't loop and save as one kept face; 11, 15 and 20 s faces loop.)
- [x] A held pack face whose mod stores a range such as 0..10, given Time control and saved, still
  shows in game instead of being dropped at once.
  (XIV Bridge tests/instant-edit/animations/251, 2026-10-10, a test mod of the game's smile timeline with its
  face changed to no time control and 0..10: as authored the face doesn't show (0.0°); given Time control and saved
  in place it is written as 0..1 and shows (16.7°, as the plain smile).)
- [x] Choose > import on a sound event, save while the file dialog is open, then finish the import:
  "… was imported. Pick it from the list to place it.", never placed on another event. Finishing
  during a drag says "… was imported but could not be placed. Finish the drag first."
  (XIV Bridge tests/instant-edit/animations/252, 2026-10-10: both messages exactly; no sound changed after the save, and
  the drag's sound didn't take the import; with nothing in the way it is placed.)
- [x] "Ends itself": a non-looping emote whose effect stops after the clip ends shows no warning; a
  looping emote whose effect ends itself still warns.
  (XIV Bridge tests/instant-edit/animations/253, 2026-10-10, an effect made to end itself: on a non-looping clip it
  warns only when it ends before the clip does; on a looping clip it always warns ("ends_itself ends itself after
  6.7 s."); Begone Demon and Catch 'Em All show exactly the problems the check finds.)
- [x] Per-option effect copies: in Begone Demon (VFX Standard / Subtle / Minimal), Catch 'Em All
  (Catch Mode) or BeatzBunny (VFX), change the effect's Look or turn on Keep them looping with one
  option active and save in place. Switching options shows each option's own effect with the edit;
  instant_edit/ holds one copy per file (..._2, ..._3), and Undo last save removes them all.
  (XIV Bridge tests/instant-edit/animations/254, 2026-10-10, Begone Demon's Look: shockedcross_c377f309.avfx, _2 and _3,
  one per option, each that option's effect with the edit; Undo removes all three and their mappings. In Catch 'Em
  All, no effect of Cheer differs between the Catch Mode options.)

## Sound inspector (2026-10-12)

The Timeline's sound options in plain words and the Sound file section (alternatives, chances,
volumes, add, replace, remove, pins, Listen), commits 2ff872b..a3e168f. Automated suites pass, and
every vanilla timeline SCD and the whole mod library round-trip through the SCD model; none of this
has been tried in game. Candidates: Self Spank with Moan (sound/moan.scd, a random set of 10) and
vanilla emotes with one-shot sounds.

- [x] Listen, first: an XIV Bridge script finds the play-sound function (E8 ?? ?? ?? ?? E9 ?? ?? ??
  ?? FE C2), plays sound/battle/etc/SE_Bt_Etc_Clap.scd, then a temporary-mod path made from a keep-only
  copy of it. Expect no crash and the resource handle seen both times; note which collection resolved
  the temporary path. Nothing else here should be tried in game before this passes.
  (XIV Bridge exec, 2026-10-14: one match, the same function VFXEditor's PlaySoundPath holds; the clap
  and the path served by a temporary mod in all collections both loaded a handle, no crash or log
  error; Interface, Base and the player's collection all resolved the temporary path to the file.)
- [ ] 1 Chances: make a two-sound test file at 90/10, play it about 20 times (Listen on the header
  button), then 10/90. Expect the 90% sound about 18 of 20 times. This confirms or refutes reading the
  limits as running totals; if not, every set should show raw limits until the real rule is known.
- [ ] 2 What the wait does: Listen to a moan with its original short wait (amber mark), then after
  Match length. Describe the difference you hear (the end cut, nothing, or the next sound overlapping).
  This decides whether "ends before its audio" stays a soft hint or becomes a warning.
- [ ] 3 One-shot to random set: on a vanilla emote's sound, Add sound (a .wav), Preview. Expect each
  play to pick either sound about half the time, and the clip on the canvas solid to the shorter
  sound and dashed to the longer one.
- [ ] 4 Pinned event: set one moan event to "Only #3". Expect Preview (after Preview again) and the
  saved mod to play only that moan, from a moan_3_… copy, while the other moan events stay random.
- [ ] 5 Everywhere or only here: on a mod with two animations that name the same SCD, save a chance
  change in place. Expect "Change the sound everywhere?" with the right count; Everywhere changes the
  mod's file (both animations hear it), Only here saves a copy for this animation only. Undo last
  save puts the mod's file back.
- [ ] 6 Keep looping: on a looping emote whose sound plays once, set Plays to Keep looping and click
  Add loop points. Expect the sound to keep going while the emote loops, in Preview and after Save.
- [ ] 7 Listen on MS-ADPCM, Vorbis and HCA files: each row's button plays that alternative alone, the
  header button the whole set, and nothing stays served afterwards (the listen_… path no longer
  resolves a few seconds after the sound ends).

Review fixes 2026-10-14 (8441df6):
- [x] Listen on a looping sound plays one pass. (XIV Bridge exec, 2026-10-14: a looping copy of the
  clap went to the game with no loop flag or loop points, and stopped being served after its length
  plus 2 s. Not heard: whether it audibly stops still needs an ear.)
- [x] A Listen that fails after its temporary mod is added removes it. (XIV Bridge exec, 2026-10-14:
  cancelled between the add and the play; the path no longer resolved and the file was gone.)
- [x] Save in place over a mod's sound at a path the game has asks "Change the sound everywhere?",
  with no other user in the mod. (XIV Bridge tests/instant-edit/animations-pending/257, 2026-10-14.)

## Timeline cuts and loop seams (pending)

Cutting a clip's motion like a video editor (trim, remove ranges, blended joins), Fix loop for loops
that stall or pop, cutting sounds with the motion, and the editable game path of sounds and effects.
Spec: docs/superpowers/specs/2026-10-14-timeline-cuts-and-loop-seams-design.md. Candidate: the mod
"Tiktok Challenge Edit on Moogle Dance" (emote/dance05_loop.pap), which plays sound/lolo.scd.

- [ ] 1 Sound path: on a library dance that plays sound/lolo.scd, the sound's File row shows the path
  in a text field with "From mod: ..." or "Not in your collection. ..." under it. Type another .scd
  path and press Enter (one undo step), save, and hear the new sound. A path ending in .avfx is
  refused under the field.
- [x] 2 Live with another length: the game loops a swapped Live copy at its own length (XIV Bridge,
  2026-10-14: a 9.7 s copy of the 14.57 s dance wrapped every 9.7 s; pause, seek, replay and Stop
  behaved; the test tool is gone since). Still to see by hand: leaving GPose and unloading the plugin
  with a cut showing under Live.
- [ ] 3 Fix loop on the TikTok dance: the clip band's end badge says Stalls; the seam panel says "The
  loop starts again faster than it ends." Fix loop eases the start over 30 frames and keeps the length
  (no trim); the badge says Loops cleanly. With Live on, try Ease 20, 30 and 45 and Side Start and End;
  then Preview and save in place: the dancer slows into the wrap and picks up again, no settle-then-jerk.
- [ ] 4 Remove a range from a dance with C, Q, W and Delete: no pop at the join in Preview, and the
  sounds, effects and faces after it moved with the motion.
- [ ] 5 Cut a sound with the motion: remove a range from a dance with music, choose "Cut with the
  motion", Preview and save. The song stays in step after the cut.
- [ ] 6 Live with cuts: drag a cut point, a blend length and the seam with Live on.
- [ ] 7 Edit again: reopen a cut dance with Edit again, move a cut, and save again. With Live on after
  Edit again, the cut shows on the character.
- [ ] 8 Save twice: cut a dance with a bone layer and a song cut with the motion, save, change the
  layer, save again. After the first save the window shows the saved file with the "Saved with edits
  you can open again" line; the second save keeps the cut and the song stays in step. Edit again then
  shows the song's whole file chosen as "Cut with the motion", and saving cuts it once.

## Motion from Blender (pending)

Sending a clip's motion from the Timeline to Blender and bringing the edited action back as the
clip's motion. Spec: docs/superpowers/specs/2026-10-16-motion-from-blender-design.md. Needs a
Blender add-on the plugin accepts, with the listener running.

- [ ] 1 Round trip: open a body animation in the Timeline, press the arrows button next to the Bones
  button, Send motion to Blender, then Motion from Blender without editing. The line under the toolbar
  says "Blender's motion matches the clip's. Nothing changed." Then add a bone layer, send it again and
  read it back without editing: the line names the action, the layer is gone, the length is unchanged
  and Preview looks the same. Skirt and hair still swing.
- [ ] 2 Turn an arm in Blender (keyed over the clip) and read it back: Live and Preview show the turn.
- [ ] 3 Extend the scene End by 30 frames, key a pose there and read it back, once with Scale events
  off and once on. Sounds and faces stay where they were, or move with the new length.
- [ ] 4 Ctrl+Z and "Use the clip's motion" both go back to the animation's own motion.
- [ ] 5 Save to a new mod and in place; the saved animation plays the Blender motion. Undo the save.
- [ ] 6 Add a bone layer on the Blender motion, save, then Edit again: the layer comes back on the
  Blender motion, not stacked on the saved file.
- [ ] 7 Errors: Blender closed; the armature in Rest Position; an armature with no animation. Each
  shows a short message on the line under the toolbar.
- [ ] 8 Press Motion from Blender, then Cancel on the progress line: it says "Reading stopped." and the
  timeline is unchanged.
- [ ] 9 With Key bone scale off, send, scale a bone in Blender (keyed) and read it back: the line under
  the toolbar ends with "Its scale was left out: Key bone scale was off when it was sent."

## Symmetry layer (pending)

A bone layer that makes picked bones copy their twins on the other side, mirrored, on every frame.
Spec: docs/superpowers/specs/2026-10-17-symmetry-layer-design.md. Test clip: Unholy Idle C Adjusted -
Idle 4, option Standing idle 4 (pose04_loop).

- [ ] 1 Open Standing idle 4 in the Timeline, select the left shoulder in the Bones window and press
  Make symmetrical. A "Symmetry (Left → Right)" layer appears over the whole clip and is selected; the
  panel says how many bones it copies.
- [ ] 2 In GPose with Live on, the right hand rests on the chest like the left, and keeps moving with
  the idle. Right-arm dots in the Bones window and the overlay take the layer's colour; their tooltip
  says "Copied from the other side."
- [ ] 3 Switch Copy from to Right: the left arm now copies the right, and the layer is renamed
  "Symmetry (Right → Left)". Switch back.
- [ ] 4 Add an Offset layer above it and turn the right hand: the copy is adjusted. The gizmo is off
  while the Symmetry layer is selected.
- [ ] 5 Save in place: the saved file plays the same, and the loop does not pop where it starts over.
  Undo the save.
- [ ] 6 Save again, then Edit again: the Symmetry layer comes back with its side, bones and blend.
- [ ] 7 Pick the chest with Copy from Left: the arms mirror and the breasts still move with physics.

## Loop and startup into a new mod (pending)

A Timeline save to a new mod takes the other half of a loop and its startup along, unchanged, when the
collection gives both from the same mod.

- [ ] 1 Open a loop whose startup comes from the same mod (Standing idle 4 with Unholy Idle C Adjusted -
  Idle 4 on). Choose Create new mod: the footer says "Its startup goes into the new mod too." Save, then
  check in Penumbra that the new mod has both pose04_loop.pap and pose04_start.pap.
- [ ] 2 Turn the original mod off and redraw: the idle starts with the mod's own startup, then the edited
  loop.
- [ ] 3 Open the startup instead (its row under the loop) and save it to a new mod: the footer says "Its
  loop goes into the new mod too.", and the new mod has both files, the loop unchanged.
- [ ] 4 A loop whose startup comes from another mod or the game (Standing idle 3 here: its startup is from
  "A2 on Idle 3"): no footer line, and the new mod has the loop only.
- [ ] 5 Replace in-place: no footer line, and only the edited file changes.
- [ ] 6 Undo the new-mod save from Recent edits: the new mod is gone with both files.
