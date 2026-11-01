# Animation editing acceptance

Status: development implementation. Automated regressions and compilation do
not validate Havok ABI compatibility or actual Penumbra/SimpleHeels behavior.
Do not regenerate distribution archives until the native, IK, dependency,
activation, and recovery checks below have passed in game.

Automated validation on 2026-09-10: all 42 animation checks, both existing .NET
regression projects, and the Release build with `SkipDistributionPackage=true`
passed. The Release build had no warnings or errors. The existing regression
projects reported NU1900 because NuGet vulnerability metadata was unreachable.
The live-game checks below were not run then; XIV Bridge ran most of them from 2026-10-10.

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

Reload the development build before these checks (run through XIV Bridge from 2026-10-10):

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
- [x] Open the walk's timeline; on the Source page verify the mod, file, live skeleton
  and Made for. Exercise a tied candidate choice through the **Made for** combo (the
  Bones window follows it).
  Partly checked (XIV Bridge tests/instant-edit/animations/102, 2026-10-10): mod, file, live skeleton (455 bones) and
  Made for (a tie of 65 candidates) are right, and two tied choices each reach the Bones window. Not checked: a face
  clip at the same time; the GPose actor dropped the face when the walk started.
  In game 2026-10-29 (with the stale Toast in GPose): Smile then Walk, the face goes neutral when the walk starts.
  That is the plugin, not the stale emote: every loader play stops the one before, and a body play clears the face slot
  (AnimationPlayback.Play). So a face beside the walk can't pass until the loader keeps a face playing; open decision.
  Ruled 2026-10-29: the loader doesn't keep a face playing beside a body clip, so the face part is dropped.
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
`GetEntity<T>(EntityId)` to exercise overload resolution (both removed 2026-10-30 with the LivePose integration). Predictive-compressed
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
- [x] Verify exact duration and both endpoints. Test a short clip and a source with more
  than 30 samples/second. Confirm higher source density survives.
  (XIV Bridge tests/instant-edit/animations/113, 2026-10-10: Jump Start (0.067 s), Idle pose 3 made 1.5x slower and
  2x faster, and a 60-samples/second source keep their exact durations, endpoints and every sample. The zero-duration
  clip was dropped, 2026-10-29: no game file has one.)
- [x] Compare the edited PAP's sampled bone transforms against BlenderLiveposer
  for root translation, stacked rotations in different orders, parent and child
  offsets together, additive scale, and quaternion sign continuity. Use matching
  samples, source skeleton, and the layer's Turn/Move/Scale. The native output validator
  permits 0.002 model units for translation/scale and 0.002 in quaternion-dot
  error after compression; visually inspect hands and feet as well.
  Partly checked (XIV Bridge tests/instant-edit/animations/114, 2026-10-10): every case matches hand-computed
  expectations within the compression's noise (0.52°, 0.002; hands and feet within 0.004 in model space), and the two
  rotation orders really differ (20°). BlenderLiveposer itself wasn't used. In game 2026-10-29, by eye: Idle pose 3
  with all of 114's cases stacked (root 0.3 forward, j_sebo_b X 20 then Y 30, j_ude_a_l 25 and j_ude_b_l 35, j_ude_a_l
  scaled 1.1), previewed: hands and feet look right.
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

Dropped 2026-10-30: Instant Edit no longer talks to the LivePose plugin. The Face page bake, Add > Offset layer
from LivePose, "Clear them in LivePose" / "Put them back", an Edits row's Restore offsets and the native CCD and
two-joint solvers are gone, and with them the checks that were here. Old LivePose bakes still load on the Edits page
and undo their files. Only .livepose files remain (Timeline > LivePose files below).

- [x] All 56 .livepose files read into the stacks their JSON holds, and IK bones are left out of the layer as the
  note counts.
  (XIV Bridge tests/instant-edit/animations/120, 2026-10-10: 558 bones, 573 stacks: names, partials, slots, order,
  transforms, propagation, IK on/off; a disabled-IK stack with tuning reads as a plain stack, an identity stack with
  active IK keeps IK on.)

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
  Collection inheritance checked (XIV Bridge animations-pending/466, Release 2026-10-29 21:23): MCPTest inheriting the
  player's own collection, a clip whose PAP comes from a mod enabled only through that inheritance saves into a new mod
  holding only the edited file; the provider, the parent collection and the inherited setting are unchanged, and the
  new mod ranks above the provider. Left: nested external TMBs and texture providers (no such mod found yet).
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
  Partly checked (XIV Bridge tests/instant-edit/animations/141, 2026-10-29): an output path another operation created
  leaves an Edits row needing attention whose tooltip gives the precise error, and Undo clears it with the mod's files
  as before; a new-mod folder that can't be created fails clearly and registers nothing, and Undo of its "Not saved"
  row now just finishes it (fixed tonight). Compression, serialization, add-mod, reload and redraw failures can't be
  injected from a script; the resolved-output check was timing-dependent and not reached (the hook never saw the job
  Committed).
- [ ] Interrupt the plugin/game after the first file replacement, after activation,
  and halfway through undo. Restart with the
  same player and collection; the Edits tab marks the edit and its Undo resumes recovery.
  Never rely on a persisted native address after restarting the game.
  Partly checked (XIV Bridge tests/instant-edit/animations/142, 2026-10-10): records left Activated, Undoing and
  Committing (after an in-place replacement), read back from disk as a restart does and with a stale native address,
  are marked on the Edits tab, and their Undo finishes the recovery. Not checked: a real interruption and restart.
- [ ] Change a PAP or new-mod metadata after a successful edit. Undo must refuse
  conflicting changes. Test moved/renamed mods, expired
  backups, a different player, and a different collection.
  Partly checked (XIV Bridge tests/instant-edit/animations/143, 2026-10-29): Undo refuses, naming why and changing
  nothing, for a changed PAP, changed new-mod metadata, a renamed mod ("ownership marker is missing"), a changed
  in-place output and an expired backup; once the conflict is undone it goes through. A different player and a different collection are no longer tested: since commit 04e93b0
  (2026-10-19) Undo of a timeline save needs no captured character and doesn't check the player or collection, on
  purpose.
- [x] Test multiple successive edits and undo them in reverse order. Preserve the
  recovery records across plugin restarts. PAP backups share the existing 7-day
  managed retention; expired backups must produce a clear recovery error.
  Partly checked (XIV Bridge tests/instant-edit/animations/144, 2026-10-10): three in-place edits undo only newest
  first (the oldest refuses with "Undo that edit first."), each undo restores the file and mapping before it, the
  records and their states are on disk, and an 8-day-old record is dropped. The expired-backup error is covered by
  test 143 above. Not checked: the records across a real plugin restart (they were read back from disk).
  (XIV Bridge animations-pending/460, Release 2026-10-29: across a real reload of Instant Edit the three edits are
  listed, Completed and newest first; the oldest still refuses first, and undoing newest first restores each.)

General keyframe editing, movement/combat animations, other race variants, and
editing separate companion/weapon rigs are outside this release. Unsupported
dependency constructs are a packaging boundary, not permission to omit assets.

## Animated bones live acceptance (pending)

**Animated Bones** lists the bones the selected clip's transform tracks drive,
named by its selected source skeleton. Unticked bones get no track in the rebaked
clip, so nothing in the animation drives them. Offline fixtures cover the list,
the refusals, and the rewrite of tracks and expected samples. Whether the game's
physics then takes over an untracked bone is the in-game claim still to verify.

- [x] Untick hair, skirt, tail or chest bones on the **Bones** tab for an emote that
  animates them, then Save into a new mod and in place. Confirm those bones follow
  physics again and every other bone animates exactly as before.
  Partly checked (XIV Bridge tests/instant-edit/animations/145, 2026-10-10, Idle pose 3): 29 skirt, chest, hair and
  tail bones unticked, saved into a new mod and then more in place: the files have no track for them (they sample at
  their reference pose), every kept bone matches within 0.56° and 0.7 mm, and in game the other bones move as before
  (2.0° at most, the idle's sway). Not checked: that the bones then follow physics (watch a tail or skirt; hair is
  already driven by physics in game, test 16).
  (XIV Bridge visual/145s, Release 2026-10-30: saved into a new mod and replayed, the unticked bones aren't frozen. A bone
  with no track and no physics sits exactly at its reference pose (0.00 deg); the skirt bones sit 0.3-11 deg off it and
  wander, the chest sits 0.94 deg off it (as it did with its track: physics drives the chest either way), and the tail
  wanders 0.17-2.1 deg past its anchor. The "chest 0 of 2, tail 1 of 5" of 2026-10-29 came from thresholds sized for a
  swinging skirt, not from frozen bones.)
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

- [x] Open the Shiver loop's timeline: the Source page shows "Uses only its first 167
  bones". Repair onto IVCS into a new mod and confirm it plays like the original.
  (XIV Bridge tests/instant-edit/animations-pending/264, 2026-10-29: the Source page says it; the repair onto IVCS keeps
  all 145 tracks, the file moves like the original within 0.001 units and 0.16 deg, and the game plays it.)

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
- [x] Add a sound (template: play once, 3D), an effect (caster to target), a resident
  expression (Angry) and a library expression (Smile, sets the face pack) to a PAP
  timeline that had no face pack. Each plays at its frame; the face holds for its
  length.
  Partly checked (XIV Bridge tests/instant-edit/animations/161, 2026-10-10, Idle pose 3): the saved timeline holds all
  four with the templates' values (play once, 3D; caster to target) and Smile sets the face pack; in game Angry
  (6.3°) and Smile (10.7°) show at their frames and the face falls back after Angry. In game 2026-10-29 (161 rerun):
  the sound and the effect play at their frames, and Angry and Smile show and hold for their length. But all four
  play only on the first loop, in the GPose loader and with /cpose (see the visual checks doc's failures).
  Explained 2026-10-29: the game plays an idle or sitting pose's timeline once and holds it at its end while the
  motion loops (Idle pose 3's timeline stayed at frame 70 while its motion kept cycling), so every event there plays on
  the first loop only, the clip's own too. Emote loops do play theirs again. The Timeline now says so on these poses:
  "This pose plays its events once. Later loops repeat only the motion." (XIV Bridge
  tests/instant-edit/animations-pending/453, 2026-10-29: with a VFX event added Idle pose 3 shows the note, muted, and
  Undo takes it away; Dance with the same event shows none.)
- [x] Add lip sync (cfxl_lip_nor1). It plays at its own pace.
  (XIV Bridge tests/instant-edit/animations/162, 2026-10-10: saved with no time control and the range 0..40; in game
  the mouth moves for 38 frames from frame 11 and then rests.)
- [x] Add a harvested prop (Toast: model 1962, WeaponTimeline 388 on c0101, 389 on
  c0801) and a still prop (Read). The prop appears without a T-pose flash, animates,
  and disappears when it ends.
  (XIV Bridge visual/163s, Release 2026-10-30, Preview paused on the first pass against the same paused frame without the
  prop: Toast at 26 changes 7,827 px around the hands, Read at 20 14,991 px, and after Toast's end, at 67, nothing (0 px);
  two paused captures without props differ by 28 px. No T-pose flash and the animating: by eye on 2026-10-29, above.)
  Partly checked (XIV Bridge tests/instant-edit/animations/163, 2026-10-10): Toast adds the sync effect, model 1962,
  WeaponTimeline 389 on c0801 five frames after the spawn and the hide-then-fade visibility steps; Read adds the sync
  effect and the model; the save holds them. Not checked: seeing the prop (no T-pose flash, animating, gone at the end).
  In game 2026-10-29 (Idle pose 3, Toast at 4, Read at 40): only Read showed. Both took summon 0, and Toast alone shows
  with no T-pose flash and animates (first loop only, see above). A prop added while another is out now takes summon 1,
  and its animation plays on summon 1 (it was always summon 0). The fades still act on every summon, so Read hides with
  Toast's fade-out from frame 59.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/455, 2026-10-29): Toast takes summon 0 (its
  animation targets it), Read takes summon 1, and in the Preview both models are loaded at once in two summon slots,
  frames 14 to 64. Not checked: both on screen (by eye; the bridge's draw check reads Toast as hidden even alone).
  Found by 455: an Add moves a prop earlier so its last event starts inside the clip (Read asked for at 40 lands at
  14 on this 70-frame clip), but the summon is picked for the frame asked for. A prop asked for at 66 gets summon 0
  and lands at 14, while Toast holds summon 0 until 64. Fixed 2026-10-29: the summon is the one free where the prop
  lands (TimelineEdits.LandedSummon; TmbFileFixture.Props). (XIV Bridge 455, 2026-10-29, build with the fix: asked for
  at 66, the prop lands at 14 on summon 1, its animation targets summon 1, and Toast keeps summon 0.)
- [x] On a looping emote, add a prop: it starts at frame 1, lasts to the clip's end with no fade-out or hold, and still
  shows on the second and later loops. Stretch an older prop to the clip's end: its fade-out and hold go. Shorten one
  whose hold ran to the clip's end: the hold still ends there. (2026-10-30: a prop faded out at the clip's end stayed
  hidden after the first loop, and stretching moved the hold past the end.)
  (XIV Bridge animations-pending/585, Release 2026-10-31 22:25, Dance: a prop added as the picker adds it starts at frame
  1, ends at the clip's end with no fade-out or hold, and its draw object shows on at least 90 % of every loop's frames
  over 3.3 loops; an older prop stretched to the end loses its fade-out and hold and shows on later loops too; shortened,
  its hold still ends at the clip's end.)
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
  In game 2026-10-29: right on the first loop, then the smile holds the whole loop from the second; 169 failed too
  (8.7° late in the loop). Not fixed: the header says 70 frames but the motion lasts 100, and the run had the stale
  Toast in GPose. Pending: rerun after sitting down once outside GPose.
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
- [x] Open the dance: the window says "4 effects stop: ...". Keep them looping,
  preview in GPose, then save in place. Dance for over a minute: the effects never
  stop and the music plays in full and starts over without a gap. The mod gets
  instant_edit/ copies mapped in the Music, Props, Sparkles and Floor Cloud Thingy
  options. Turning a toggle off removes that effect from the dance too. The mode A
  walk is unchanged. Undo removes the copies and their mappings.
  Partly checked (XIV Bridge tests/instant-edit/animations/173, 2026-10-10): the window says "4 effects stop: redcarpet,
  redcarpetbgm, redcarpetcloud, redcarpetsparkle."; Keep them looping makes copies that differ only in the kill item's
  enable byte (13-14 bytes) and a sound copy looping over all of it (0..1827659); Preview plays; the in-place save maps
  the copies in Music, Props, Sparkles and Floor Cloud Thingy; Music off no longer resolves its copy; the mode A walk is
  unchanged; Undo removes the copies and mappings. In game 2026-10-29 (the user's own in-place save, on through temporary
  settings): over a minute of dancing no effect stops, the music plays in full and restarts with no gap, and a toggled-off
  option's effect leaves the dance.
- [x] Check whether -1 on the bgm effect (it doesn't loop, it was 909) changes
  anything, and whether the copies' effects double up when the dance loops.
  (XIV Bridge tests/instant-edit/animations/190, 2026-10-10, by the files' live reference counts in the game's
  resource map: over three dance loops no copy's count grows, so nothing doubles up; saved in place, the music's
  looping copy plays and is still playing at 35 s with -1 and with 909 alike: the value changes nothing.)
  In game 2026-10-29 (190 rerun, by eye and ear): the Preview, the save at -1 and the save at 909 sound and look the
  same, and nothing plays doubled.
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
2026-10-07 on Angel Dust's dustsmoke, Live off). A playing timeline keeps the effects it
loaded at its start through every loop (2026-10-29), so a look change redraws the
character and plays the clip again, 0.4 s after the edit settles.
- [ ] Preview the smoking emote with a recoloured dustsmoke: it shows the new colour.
  Change the colour while the preview plays: the next puff (each loop restarts it)
  has it, with no Preview again (the character redraws and the clip plays again). The
  hint says a look change redraws the character.
  Partly checked (XIV Bridge tests/instant-edit/animations/174, 2026-10-10, Angel Dust): with Preview, dustsmoke's game
  path resolves to an edited copy with the red tint; a blue tint while it plays reaches a new revision of that path
  with no new build and no replay; the hint is shown. Not checked: seeing the smoke change colour.
  In game 2026-10-29: fails, the smoke stays red until Preview is pressed again. The playing timeline never loads the
  new revision. A look change now redraws the character and plays the clip again (no new Preview build); a paused
  preview pauses again at its frame. The hint now says "Changing an effect look redraws your character." (XIV Bridge
  tests/instant-edit/animations/174, 2026-10-29: the blue tint reaches a new revision of the path, and the clip plays
  again after the redraw with no new Preview build. The game crashed about two minutes later; see the XIV Bridge
  handoff.) Pending in game: the next puff is blue.
- [ ] Turn Live on and change a look: the same happens over the clip's own file. Turn
  Live off: the effect goes back to its own look on its next start.
  Partly checked (XIV Bridge tests/instant-edit/animations/175, 2026-10-10): with Live, the effect's path resolves to an
  edited copy while the clip's own file still plays, a new tint reaches a new revision, and Live off resolves the
  effect to its own file again. Not checked: seeing it.
  In game 2026-10-29: fails, the smoke keeps the last Preview's tint (same cause as above). With Live a look change now
  goes back to the clip's own motion for the redraw and comes back after. (XIV Bridge 175, rerun 2026-10-29: the new
  tint reaches a new revision of the path and Live starts again after the redraw; Live off resolves the effect to its
  own file.) Pending in game: the next puff takes the new look, and Live off gives the effect's own look. Unsure: the
  clip's own PAP keeps its path, so the game may reuse its loaded copy.
- [x] Check whether the rest of a Preview shows (2026-10-07): recolours showed, but a
  removed expression kept playing. A replay never re-reads the clip's file even at a new
  revision path, so Preview now redraws the GPose character after showing a revision,
  as the in-place edits redraw the player (see Preview below).
- [x] Tint an effect red and preview in GPose: the whole effect turns red, lights and
  model particles too. Above 1 it gets brighter.
  Partly checked (XIV Bridge tests/instant-edit/animations/176, 2026-10-10): the tint writes only RvR/RvG/RvB (red reads
  back 1,0,0; 2.5 is kept). In game 2026-10-29 (vfx/common/eff/dk01pr_fir0t.avfx added to Angel Dust's clip, Preview
  after each change): the tint shows on the whole effect.
- [ ] Size 2 doubles it, Move Y 1 lifts it, Turn Y 90 turns it a quarter (Turn is
  radians in the file, degrees shown; if it turns about 57x as far, that is wrong). An effect attached to a bone
  can only be moved.
  Partly checked (XIV Bridge tests/instant-edit/animations/176, 2026-10-10): size 2,2,2, offset 0,1,0 and turn 0,90,0
  are written as typed and nothing else changes. Evidence on the unit: of 1,501 effects read, the largest Turn value
  is 0.052 and none is above 2π, which suggests radians. Not checked: how far it turns in game.
  In game 2026-10-29 (dk01pr_fir0t as a C012, Preview): Move and Turn work, Size 2 shows no change. That effect is
  attached (its binder), so Size on a C012 of an attached effect is now greyed out with "Size can't change an attached
  effect". (XIV Bridge tests/instant-edit/animations-pending/454, 2026-10-29: on a C012 of an attached effect Size is
  greyed out with that tooltip; on one that isn't attached it is enabled, "Scales the effect.") Pending in game: Size 2
  on a C012 of an effect that isn't attached.
- [x] Click a swatch, pick another colour: only that part changes. Hue 180 turns
  every colour to its opposite; Saturation 0 makes it grey.
  Partly checked (XIV Bridge tests/instant-edit/animations/176, 2026-10-10): a swatch recolours its 4 keys and leaves
  the other 4; Hue 180 turns every colour key by 180° keeping its brightness; Saturation 0 makes every key grey.
  In game 2026-10-29 (dk01pr_fir0t, Preview after each change): the swatch changes only its part and Hue 180 turns
  the colours. Saturation 0 looks clearly less saturated but not quite grey, and doesn't match the swatch preview.
- [ ] Glow 200% brightens, Opacity 50% fades the particles. Light range 200% on an
  effect with a point light (a fire or glow) lights a wider area.
  Partly checked (XIV Bridge tests/instant-edit/animations/176, 2026-10-10): Glow doubles every brightness key, Opacity
  halves the 14 alpha keys and Light range doubles the point light's range key, each changing nothing else. Not
  checked: how it looks.
  In game 2026-10-29 (dk01pr_fir0t): Glow and Opacity work; Light range worked backwards. A point light's DstS is a
  distance scale (bigger reaches less far), so Light range now divides it. (XIV Bridge 176, rerun 2026-10-29: 200%
  halves every point light distance scale key and changes nothing else.) Pending in game: 200% lights a wider area.
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
  In game 2026-10-29: an event with a changed look shows the amber dot and "Look changed." in its tooltip.

Place and length (2026-10-27):
Selecting an effect shows a Place section (Move in metres, Turn in degrees, Size) above Look.
A C173 event also has Stops at, which bakes a KILL into a copy of the effect.
- [x] Turn Y 90 on an effect turns it a quarter, not 57 times as far.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/431, 2026-10-29): Turn Y 90 is kept as pi/2 radians
  and the saved copy turns a quarter further (+pi/2), not 90. In game 2026-10-29 (vfx/common/eff/m0096sp_06c2s.avfx
  added as a C173 to Angel Dust's clip, Preview): Turn Y 90 turns it a quarter, and Size 2 doubles it.
- [ ] Move 0,1,0 on an effect attached to a bone lifts it.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/431, 2026-10-29): Move is allowed on an attached
  effect (Turn and size are refused) and every fixed binder position is 1 higher in the saved copy. Not checked:
  seeing it lift in game (by eye).
  In game 2026-10-29 (dustsmoke and breathsmoke, C173 on a bone): it moves along the bone's own axes, not straight up.
  Move on an attached effect now says "Moves along the bone's axes, in metres." (XIV Bridge
  tests/instant-edit/animations-pending/454, 2026-10-29: that tooltip shows on an attached C173's Move; on one that
  isn't attached it says "Moves the whole effect, in metres.")
- [x] A C012 Position edit moves that event only, not the other uses of the effect.
  (XIV Bridge tests/instant-edit/animations-pending/431, 2026-10-29: Move 0,1,0 sets that event's Position.Y to 1 and
  the other use stays at 0; the saved timeline keeps both C012s on the game's effect, no copy.)
- [x] Stops at 60 on a looping effect ends it at 2 s. Keep looping and Stops at clear each other.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/432, 2026-10-29): Stops at 60 puts the copy's KILL at
  frame 60 and sets Loop/wait back to 1; Keep looping (-1) clears Stops at and Stops at clears Keep looping; the
  preview plays. Not checked: whether the effect really ends 2 s after it starts. Reference counts in game can't tell
  it (the stopped effect's are those of the one kept looping). In game 2026-10-29 (m0096sp_06c2s as a C173 on Angel
  Dust's clip, Stops at 60, Preview): the effect ends about 2 s after it starts.

Prop place and length (2026-10-27):
Selecting a prop shows a Place section (Attach, Bone, Move, Turn, Size, Only this animation). The save writes an Atch
override into the mod's meta.json for each race the file plays on, and the attach state into the TMB.
- [x] Preview Toast. The log (Debug level) says "Prop 1962 attaches with" a 3-letter code; Place then enables and saving no longer
  says "Preview once". Navigate's second prop (frame 173) is learned too.
  (XIV Bridge tests/instant-edit/animations-pending/433, 2026-10-29: before the Preview saving says "Preview once to
  place 1962"; the Preview learns wdm and the next save builds its overrides; Navigate's two props are both learned.)
- [x] Move a prop 0,0.1,0 and Preview: it shows moved. A look or sound change during the preview keeps it there.
  In Toast, selecting its first or its second summon shows the same place.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/433, 2026-10-29): the prop sits 0.10 m from where it
  was unplaced, a look change keeps it there (0.002 m), and every summon and prop control of Toast's group shows the
  same place. In game 2026-10-29 (Toast alone on Idle pose 3): it sits moved, and stays there through a look change on
  its sync effect and a sound change. The tint on the sync effect never shows, even after Preview (it may draw nothing).
- [x] Save it, then open the mod in Penumbra: the Atch entry is there, and the prop sits moved in a normal /emote.
  Undo the save: the entry is gone (or the one it replaced is back).
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/434, 2026-10-29): the save writes one Atch entry for
  the race the file plays on, the game's entry moved 0,0.1,0. In game 2026-10-29 (Toast placed 0,0.1,0 on Idle pose 3,
  saved into a new test mod): the toast sits moved in Idle pose 3 played with /cpose. Undo of that save turns the new
  mod off, as for any new-mod save: the entry no longer applies, while the mod's own editor still lists it.
- [x] Reopen the saved file: Place shows the saved move (Only this animation ticked if it was; Attach then shows In
  use, as the state it started from isn't kept). Nudge it and save: the entry moves on from there. Reset place and
  save: the entry is gone; Undo brings it back.
  (XIV Bridge tests/instant-edit/animations-pending/434, 2026-10-29: Place shows the saved move; nudged to 0.15 with
  Only this animation, the entry moves to 0.15, and reopened it shows Only this animation ticked and Attach In use;
  after Reset place and save the entry is gone, and Undo brings back the 0.15 entry.)
- [x] Change a prop's Atch entry in Penumbra's own editor, then reopen the file: Place shows that change (Penumbra's
  field names are read as Instant Edit writes them; not yet checked against Penumbra's writer).
  (XIV Bridge animations-pending/480, Release 2026-10-29: Toast placed and saved into a new mod; each Atch Offset Y
  raised 0.05 through Penumbra's own ModMetaEditor (Load, Update, Apply, as its Meta tab does); Instant Edit reads
  Penumbra's entries, and after a reopen Place shows 0.15 at the same state.)
- [x] Without Only this animation, a vanilla emote with the same kind of prop moves too. With it on, Toast's prop
  moves and the vanilla mug emote's stays put.
  (XIV Bridge tests/instant-edit/animations-pending/442, 2026-10-29, after the fix below: without Only this animation
  the idle's Toast and the game's own Toast emote (sp19) both sit 10.0 cm off, and back with the mod off; with it,
  Toast moves 10.0 cm and sp19's prop stays put.) In game 2026-10-29, by eye (Idle pose 3 with /cpose and the Toast
  emote, each save in a new test mod): without Only this animation both toasts sit moved; with it, only the idle's does.
  Fixed 2026-10-29: a summon that leaves its attach state unset (a C043, or a C198 at AtchState Default, with no
  C014/C174) shows in state 1 (Stowed), not 0, so a shared place was written where no vanilla emote looked.
  TimelinePropPlaces.CurrentState now falls back to 1. Probes: all 17 vanilla prop emotes leave the state unset; on
  Toast, Drink Tea, Navigate and Magic Trick a place saved at state 1 moves the vanilla prop 10.0 cm, one at state 0
  doesn't. (Their state 0 and 1 entries are the same, so the wrong state didn't show until a place was saved.)
- [x] Resize sp50 by +30 frames: the fade-out stays at the end and the prop is gone when the clip ends.
  (XIV Bridge tests/instant-edit/animations-pending/435, 2026-10-29: the summon grows 81 to 111 frames, ends by the
  clip's end (111 of 124), the fade-out keeps its distance to it (72+9 to 102+9), and in a Preview the prop is gone
  when the clip ends.)
- [x] A file other races also play (a c0101 fallback) gets an Atch entry for each of them.
  (XIV Bridge tests/instant-edit/animations-pending/435, 2026-10-29: Cheer Jump: Indigo, a file c0101 also plays, gets
  14 entries, one per race; sp50 alone gets c0801's.)

XIV Bridge plan for these: dump the four weapon slots (DrawObjectData, three at +0x10, one at +0x160) during Toast and
read the code at +0x30 of each (+0x20 is a pointer; expect one such as tsl or wdm; summon 0 is the third slot, seen
2026-10-28; note which slot summon 1 uses). Then Toast
with Only this animation beside a vanilla mug emote, both on the GPose actor. Then sp50 resized by +30 into the
"MCPTest" collection.

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
  (XIV Bridge tests/instant-edit/visual/183v, 2026-10-29: Penumbra's own temporary-mod list, which its Temporary tab
  draws, holds "XIV Instant Edit animation preview" once at 2147483647 in MCPTest, above the only other one (99), and
  drops it on Stop.)
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
  Ruled 2026-10-29: a pose loaded through Brio or Ktisis needn't survive the preview's redraws; that part is dropped.
  (XIV Bridge animations-pending/472, Release 2026-10-29, for the record: a pose loaded with Brio's LoadPoseFromFile
  doesn't survive Stop's redraw, the arm 31 deg off it.) Left: Face Camera turned off by hand (no script reaches it).
- [x] Pause freezes body and face; scrubbing moves the pose without drift; Resume and
  speed work; sounds and effects fire only while playing. Check alongside Brio and
  Ktisis, which hook the same speed function.
  Partly checked (XIV Bridge tests/instant-edit/animations/185, 2026-10-10): paused, body and face move 0.04°; the clip's
  frame follows the playhead and scrubbing back to a frame gives the same pose (0.00°); it runs at 30.0 frames/s,
  15.0 at 50% and 60.0 at 200%. Not checked: sounds and effects while paused, and Brio or Ktisis loaded.
  Checked with Brio (XIV Bridge animations-pending/473, Release 2026-10-29): with Brio loaded, a sound at frame 5 and an
  effect at 10 fire when the Preview starts the clip, nothing fires while paused or scrubbing over their frames, the
  body stays still paused, 50% runs at half rate, and Brio's own speed for the actor is as before after Stop. Events fire
  once per Preview start, not on each loop of an idle (as the Timeline's note says). Ktisis isn't installed.
- [ ] Stop, closing the window, leaving GPose and unloading the plugin each end the
  preview: the temporary mod is gone, the actor is not left locked, and the
  animation-preview cache folder is cleaned. After a forced crash the next load
  removes them.
  Partly checked (XIV Bridge tests/instant-edit/animations/186, 2026-10-10): Stop, closing the window and leaving GPose
  each remove the temporary mod and the session's cache folder, and nothing keeps playing the preview; after Stop and
  closing the window the actor's lock mode is 1 again (8 while the loader held it). Not checked: unloading the plugin
  and a forced crash.
  Unload checked (XIV Bridge animations-pending/461, Release 2026-10-29): unloading Instant Edit mid-preview takes the
  clip off the preview mod, deletes the session folder, clears the crash record and leaves the actor unlocked. Left:
  a forced crash (by hand).
- [ ] With a shared collection the window warns that others see the preview too.
  Lightless/PlayerSync do not upload preview files.
  Partly checked (XIV Bridge animations-pending/586, Release 2026-10-31: the Preview's shared flag follows Penumbra's
  answer for the player and the GPose actor, and shared, the status line warns "Others using this collection see the
  preview too." until Stop; the shared case forced in memory). Not checked: Lightless/PlayerSync uploads (by hand, needs
  a second client). Note: with Live on the warning is not shown (TimelinePreview.cs:189-198 returns before it), also over
  a Preview.

Window:
- [ ] Edges, bodies, stacked markers and the length handle are easy to grab at normal
  and zoomed-out levels. Ctrl+wheel zooms at the cursor without scrolling the window.
  Partly checked (XIV Bridge tests/instant-edit/animations/187, 2026-10-10): 32 hit tests on the real layout find every
  event where it is drawn at 1, 3, 9 and 30 px per frame; 16 zoom steps keep the frame under the cursor (0.000 frames
  off) and stop at the limits; the window has NoScrollbar and NoScrollWithMouse. Not checked: whether it feels easy
  by hand.
- [x] With the window focused, Delete, arrows and Ctrl+Z/Y/C/V/D/A don't reach the game.
  Partly checked (XIV Bridge tests/instant-edit/animations/188, 2026-10-10): with the window focused ImGui wants the
  keyboard, and each key acts on the timeline. Feeding keys to ImGui can't show what the game receives: press them in
  a GPose scene and watch the camera and the character.
  Fixed 2026-10-29: only the keys a shortcut uses were taken from the game, so Up and Down (no shortcut) would still
  tilt the GPose camera. Ruled: all four arrows stay with the Timeline; ShortcutKeys.Hold takes Up and Down too.
  Retest: XIV Bridge animations-pending/465 (real key presses, camera and actor read before and after; run with
  --arg waitSeconds=60 and click into the game).
  (XIV Bridge animations-pending/465, Release 2026-10-29 21:23 with the Up/Down fix, game in front: real presses of
  Delete, all four arrows and Ctrl+Z/Y/C/V/D/A with the Timeline focused act on the timeline and leave the GPose camera
  and actor still; the same arrow with no window focused turns the camera.)
- [x] Every field edit in the properties panel undoes in one step. The window stays
  visible in GPose and the On Screen/Animations tabs keep working.
  (XIV Bridge tests/instant-edit/animations/189, 2026-10-10: 38 field edits of sound, effect, face, lip and prop events
  each undo and redo in one step; in GPose the window stays drawn while the main window shows On Screen and Animations.)

## Props from other mods (2026-10-28, pending)

The prop picker's "From a mod" tab lists the models of the mods in your collection. The animation keeps a private copy
of the model it takes, so the other mod can be off.
- [ ] From a mod lists a mod's models. A model weighted to body bones shows disabled with "Bends with bones a prop
  doesn't have".
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/436, 2026-10-29): the Hanzo phone, the Telephone and
  the Vibrator list their models as props that can be picked. Not checked: a model weighted to body bones shows
  disabled; none of the mods read has a monster or demihuman model weighted to body bones, pick one by hand.
- [x] Preview the Hanzo phone and the Telephone on swipe left: both draw with materials, and the log names `gdt`.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/437, 2026-10-29): both props show in their summon
  slots and the Preview learns `gdt`. Not checked: that both draw with materials (by eye: the phone case and the pink
  telephone in the paused preview). Rerun 2026-10-29 evening with the stricter draw check (NPropDrawNow): it reads both
  slots as hidden with the visible flag off, but it reads Toast that way too when Toast is alone in a Preview (455), so
  it can't tell what is on screen; still by eye.
  Found and fixed 2026-10-29: the Preview refused prop copies from mods. AnimationPreviewStore now takes a private prop
  body's .mdl/.mtrl/.tex and counts them apart.
  In game 2026-10-29: fails, neither prop shows although both summon slots hold draw objects. Likely cause: a private
  body (b9000..b9999) has no IMC, Penumbra can't create one, and a weapon without one doesn't draw (the log shows the
  summons' .imc and .mdl failing at their bare game paths). Open: a slot dump and the same prop on a shipped w9001 body
  decide the fix.
  Found and fixed 2026-10-29 (XIV Bridge tools/diagnostics/prop-imc, prop-look, prop-anim, prop-set): not the IMC. A
  summon on weapon set w9001 never turns visible, on a private or a shipped body (b0200, which has an IMC), with or
  without a C094 show or a C031 weapon timeline; the model, material and textures all load. The same phone copy on w9901
  draws, textured, in the right hand, on a private body with no IMC too (as Eat Rice Ball, w9901 b0026). Penumbra
  ignores a redirect of an .imc, so shipping one wasn't possible anyway. PropModelSources.TargetSet now sends w9001,
  monster and demihuman models to w9901; other weapons keep their set (w0528 shows). Fixture: PropModelSourcesFixture.
  Prop timelines saved before the fix still hold w9001 summons and stay invisible until the prop is picked again; no
  migration (ruled 2026-10-29: no such saves exist).
  Retest after a Release build: 436, 437, 443, 445, 446 (437 needs NUsePropTypes(9901, 528), and lib/n.csx's
  NPropDrawNow should judge by the visible flag alone: the slot's hidden state is set on visible summons too).
  In game 2026-10-29 (Release 21:23): the phone case now draws on w9901 (Summon 0); the Telephone on Summon 1 doesn't.
  Diagnosed 22:05 (XIV Bridge tools/diagnostics/prop-telephone.csx): a prop on Summon 1 on w9901 never loads; the game
  asks for none of its files and the slot stays built for b0000. The same Telephone copy alone on Summon 0 loads and
  is built for its private body, and the phone case alone on Summon 1 fails the same way: not the monster model.
  Next (fresh game): phases E-G of that script (vanilla Eat Rice Ball, a w9001 copy and a w19xx prop on Summon 1).
  Phases E-G (22:11, restarted game): on Summon 1 the game's own bodies load and draw (Eat Rice Ball w9901 b0026,
  vanilla w1965 b0001), a private body doesn't (the Telephone's copy on w9001 b9119: built b0000, no model). So a
  private body (b9000..b9999, no IMC file; Penumbra ignores .imc redirects) loads on Summon 0 only. Fix needs a
  decision: which summon or body a prop from a mod gets when Summon 0 is taken.
  Ruled 2026-10-29: a prop from a mod always takes Summon 0; a vanilla prop overlapping it moves to Summon 1; two props
  from mods at the same time are refused with a short reason.
  (XIV Bridge visual/437s and tools/diagnostics/prop-phone-now, Release 2026-10-30 01:48: the Hanzo phone case on Summon 0
  of Idle pose 3 draws textured in the Preview, the purple case with its camera light beside the right hand, 1,121 px
  around the hands against 666 of noise, brightness spread 41; the visible flag is on. With the Telephone refused
  beside it (one prop from a mod at a time) that half of the item no longer applies. Where it sits in the hand: by eye.
  The attach code the Preview learns is `fdr` since props from mods moved to w9901; `gdt` was w9001's.)
  Ticked 2026-10-30.
- [x] A mod with a colour option (Telephone, Moogle Pink) copies the textures of the option you have on.
  (XIV Bridge tests/instant-edit/animations-pending/436, 2026-10-29: with Moogle Pink on, the copy's diffuse texture is
  the Moogle Pink file, differing from the default option's, and the built copy holds its bytes.)
- [ ] The Vibrator under w0528 sits in the hand like the equipped lance.
  Not checked (XIV Bridge tests/instant-edit/animations-pending/443, 2026-10-29): it keeps set 528 on a private body and
  the Preview learns 2sp, but in attach state 0 it sits a steady 40 cm from n_buki_l, not at a hand. The lance
  comparison needs a lance drawn in GPose (the test doesn't equip gear). Not checked in game 2026-10-29: props picked
  from mods draw nothing (the phone case and Telephone are invisible in the Preview and in the saved idle).
  Run 2026-10-29 22:27 (XIV Bridge animations-pending/474, Release 21:23, Dragoon with the lance drawn before GPose): the
  Vibrator attaches at 2sp, the lance read at 2bk (the back point: in GPose the actor seems to hold it sheathed), 9.9 cm
  apart on the bone; the Vibrator's model loads but its material doesn't. Left: a look in GPose with the lance in hand,
  and why the material of a w0528 private body doesn't load.
- [ ] A prop from a mod takes Summon 0; a vanilla prop overlapping it moves to Summon 1 and both show; one undo puts both
  back. A second prop from a mod at the same time is refused: "Only one prop from a mod can show at a time."
  (XIV Bridge tests/instant-edit/animations-pending/437, 2026-10-29: the phone case is on Summon 0 and the telephone
  on Summon 1, both drawn.) In game 2026-10-29 neither is visible, although both slots hold draw objects.
  Changed 2026-10-29 (ruled): a private body loads only on Summon 0, so props from mods always take it
  (TimelineEdits.ModPropSlots, applied on add, paste, duplicate, drag, nudge and the properties panel; fixture
  ModPropSlots). A prop from a mod over the clip's own summoned weapon (C043, always Summon 0) is refused: "The clip's
  own prop holds Summon 0 here." Retest after a Release build: 437 now checks this item (Toast moves to Summon 1 under
  the phone case, one undo, the Telephone refused); 437s, 445 and 475 use one mod prop at a time.
  Ruled 2026-10-29 (accepted as built): a mod prop over a C043 is refused; a drag that would overlap stops at the last
  allowed spot; a vanilla prop moved to Summon 1 stays there when the mod prop goes. Not covered: length changes with
  scaled events and cuts, which move props without the rule.
  (XIV Bridge animations-pending/437, Release 2026-10-30 01:48: the phone case picked over Toast takes Summon 0 and moves
  Toast to Summon 1, one undo puts both back, redo, and the Telephone over the phone case is refused with that text; in
  the Preview paused at 30 both slots hold their models with the visible flag on (tools/diagnostics/prop-phone-now: Toast
  shows; at that frame Toast's keg sits in front of the phone case). Left: both on screen at a frame where neither hides
  the other, by eye.)
  Found by 437 (2026-10-30): pausing the Preview before the props' fade-in (C094 at 9 to 16) and seeking past it leaves
  them hidden, and a pause before a hide at 64 seeking to 67 left Toast showing (163s): a seek doesn't apply the visibility
  events it skips. See the XIV Bridge handoff.
- [x] The prop panel lists "Missing: <path>" for a texture the source lacks.
  (XIV Bridge tests/instant-edit/animations-pending/436, 2026-10-29: the phone case's copy lists
  chara/weapon/w9001/obj/body/b9000/texture/v01_unknown_d.tex as missing, and only it.)
- [x] Save, turn the source mod off: the prop still draws.
  (XIV Bridge visual/437s, Release 2026-10-30 01:48: saved into a new mod with the source mod off, the replayed idle draws
  the phone case textured, 980 px around the hands against 577 of noise, brightness spread 38.)
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/437, 2026-10-29): with the source mods off the
  private model resolves into the new mod. Not checked: that the prop still draws (by eye: play the saved idle with the
  source mods off). In game 2026-10-29: fails, the saved props draw nothing at all (see the visual checks doc). Same
  cause as in the Preview (no IMC for a private body); pending the body choice.
  Fixed 2026-10-29: the cause was the w9001 set, see the Preview item above. Retest after a Release build.
- [x] Save in place, then Undo: the mod's meta.json is as before and the private files are gone.
  (XIV Bridge tests/instant-edit/animations-pending/437, 2026-10-29: the in-place save writes the vibrator's private
  files into the mod; after Undo its JSON files are as before and the files the save added are gone.)
- [x] Save into a new mod: its DefaultData maps the private files and 256 material folders.
  (XIV Bridge tests/instant-edit/animations-pending/437, 2026-10-29: DefaultData maps both props' private models and
  textures, and each material in 256 material folders.)
- [x] A prop on a GPose actor that hides weapons: does hiding hide summons, and what shows them.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/444, 2026-10-29, "Display weapons when sheathed"
  off): yes, the summon is in its slot with a draw object but not visible (a screenshot shows no toast). What shows it
  again: not checked (the script calls HideWeapons only after it is disassembled).
  In game 2026-10-29, by eye: no. With "Display weapons when sheathed" off before entering GPose, Toast added to Idle
  pose 3 shows in the Preview. Props picked from mods show with the setting neither off nor on, so weapon hiding isn't
  why they're invisible.
- [ ] Two new mods, each with a prop from a mod, both on: each emote shows its own prop.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/445, 2026-10-29): the two phone cases get bodies
  b9893 and b9894, each idle draws only its own bodies (the telephone too) and each resolves into its own mod. Not
  checked: that they are visible (the character hides weapons, which hides summons). Not checked in game 2026-10-29:
  props picked from mods draw nothing, even with "Display weapons when sheathed" on.
  (XIV Bridge animations-pending/475, Release 2026-10-30 01:48, the weapon shown through Glamourer's temporary state: each
  idle played on the GPose actor shows its own props, not hidden, model, materials and textures loaded, and none of the
  other mod's. On screen: by eye.)
- [ ] In place, a prop on a second animation of the same mod gets its own body and stays when the first's option is off.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/446, 2026-10-29): the second gets b9894 (the first
  b9893), each is mapped only in its own option, and with option A off the second still resolves into the mod and is
  drawn. Not checked: that it is visible (the character hides weapons). Not checked in game 2026-10-29: props picked
  from mods draw nothing, even with "Display weapons when sheathed" on.
  (XIV Bridge animations-pending/476, Release 2026-10-30 01:48, the weapon shown through Glamourer: with option A off,
  Idle pose 2's phone case (b9894) resolves into the mod and shows on the GPose actor, not hidden, its files loaded. On
  screen: by eye.)

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
- [x] Untick a face bone under Animated bones on the Face page, then **Save the face
  without them**: that bone stops moving.
  (XIV Bridge tests/instant-edit/animations/201, 2026-10-10, after the fixes that send the matched clip and bake
  deprecated-additive faces the way the game applies them.)
- [x] Send an expression to Blender with the pen.
  Partly checked (XIV Bridge animations-pending/481, Release 2026-10-29): Smile sent with its row's pen to a headless
  Blender: the send dialog takes it and Blender keys it onto the armature. Not checked: that the motion in Blender is
  the face's (the script's own decode of the face PAP failed: "Invalid PAP header or size."); compare in Blender.
  (XIV Bridge animations-pending/481, Release 2026-10-30: the face sampled as the pen samples it (AnimationClipExport) and
  Blender's action read back agree on all 80 bones at every sample, 60 of them posed off their rest, 0.00 mm and 0.00 deg;
  Smile is a held pose, 2 frames.)
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
  In game 2026-10-29 (with the stale Toast in GPose): the body plays Toast and Stop doesn't bring the face back to
  neutral. Stop restores what was held before the preview (here the stale Toast), so this should pass once the stale
  emote is cleared. With a held expression picked in GPose, Stop brings that back rather than neutral. Pending: rerun
  after sitting down once outside GPose.
- [ ] Tick Keep on /smile's face and save in place. In game the smile stays after the
  expression ends, until another expression replaces it. Undo last save restores it.
  Partly checked (XIV Bridge tests/instant-edit/animations/203, 2026-10-10), saved into a new mod rather than in place
  (/smile comes from the game's files): the kept smile is still 2.00° at 8.5 s where the plain one is back at 0.01°;
  /sad replaces it; after Undo it ends again. In-place expression saves failed then; since the fix that backs up
  the expression's .tmb they work (205, 206). Not checked: a kept smile saved in place (needs a mod's smile).
  In game 2026-10-29, by eye, saved into a new test mod: /smile stays after it would end until /sad or /angry
  replaces it, and after Undo /smile ends normally again.
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

## Face editing (pending)

An expression's timeline now opens its face as a clip of the face's own PAP: bone layers key face bones, Live shows
them in the face slot, and Save writes the face's file (and the expression's timeline when its events or the face's
timing changed). Edit face on a face event opens that face on its own; Back returns. Faces are Still (one shape) or
Moving (their own length). Offline: the face model, its timing in the timeline, sources, saves, Edit again by binding,
holds, the window's gates, the carrier and Live's layers are in the regression suite (FaceEditingFixture, TmbFileFixture,
EditAgainFixture). Every vanilla expression times its face (0 to 1 over 150 frames, time control on).

- [x] An unedited face bake of Smile (a layer at Amount 0) passes the bake's validation and samples as the original (0.00°).
  (XIV Bridge animations-pending/690, Release 2026-10-31 22:25: a layer at Amount 0 is no change, so nothing bakes; with
  an offset layer keyed at 0 deg the face bakes, and the baked file samples as the original within 0.1 deg. The item
  should say "a layer with a 0 deg key".)
- [ ] Live on Smile's face: a jaw key shows on the face only (slot 2), the held-expression refill is paused, Stop
  brings back the held expression or the neutral face.
- [x] Gizmo dots on face bones line up with the face (open check: the face partial's model space against the body's).
  (XIV Bridge animations-pending/692, Release 2026-10-31: in the overlay's own model space the face bones sit within 20
  cm of j_kao and the jaw's dot within 150 px of the head on screen.)
- [x] A jaw key on Smile saved into a new mod shows in /smile; Undo last save takes it away.
  (XIV Bridge animations-pending/693, Release 2026-10-31: the face file is written, not the TMB; /smile on the player
  shows the jaw at 25 deg, and after Undo last save the mod is off.)
- [ ] A mod's smile saved in place; Undo last save restores the mod's file.
- [ ] Smile made Moving (300 frames) and saved: /smile plays the whole face (the face event is retimed 0 to 300).
- [ ] Keep on a moving face of 330 frames: it loops at 330 frames and ends neutral. (Changed 2026-10-31, the user's call:
  Keep writes at most 100 copies, so a face loops only from 328 frames on; the 300-frame face of the item above can't
  loop by design, TmbFileFixture.)
- [x] Neutral face on Smile, keyed and saved: the new face shows, additive like the game's.
  (XIV Bridge animations-pending/697, Release 2026-10-31: the blend hint is kept and the other face bones stay within 2
  deg of the idle face.)
- [x] Edit face from a body timeline's face event; Back returns with the body's unsaved edits.
  (XIV Bridge animations-pending/698, Release 2026-10-31, a real click: Back returns with the body's added event and neck
  layer, still unsaved.)
- [ ] Two faces saved one after the other into resident face.pap: reopening the first brings its layers back.
- [x] A face opened on its own plays through the carrier (facial/pose/f_umm) in Preview, and Live runs over it.
  (XIV Bridge animations-pending/700, Release 2026-10-31: the Preview plays the carrier's timeline and Live's jaw key
  shows over it.)
- [ ] An expression's face PAP is not size-checked against the character before Preview (PlayProblem runs on the
  .tmb only): try a face whose skeleton has more bones than the character's and note what the game does.
  Partly (XIV Bridge animations-pending/701, Release 2026-10-31, read-only: it lists the other races' Smile face files
  that don't fit the character's face skeleton, and PlayProblem returns nothing for /smile, as the item says). Playing
  the oversized face (PlayOversizedFace = true) is left to run alone with the corruption probe: a crash risk.
- [x] A body timeline holding Smile without time control, after Smile was made moving: note what the held face does
  once the moving face has run out.
  (XIV Bridge animations-pending/702, Release 2026-10-31, an observation on a copy of Leena Grind: the jaw printed each
  second for 25 s after the moving Smile ran out, with no error.)

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
- [x] A clip captured in GPose: save its timeline in GPose: it works. Leave GPose and save
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
  whose bones it lacks (iv_ bones) have none; closing the Timeline window closes it. In game 2026-10-29 (210 rerun, by eye): on Idle pose 3 the dots sit on their
  joints on the body, armor and hand pictures.)
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
  "knee" narrows it to 4; the All bones page draws its list. In game 2026-10-29 (by eye, Idle pose 3): the tree's
  colours match the dots.)
- [x] Select an offset layer and the neck: the rotation ball's rings follow the camera as you
  orbit it. Drag a ring with Live on: the head turns about that axis, a key appears at the
  playhead, and Ctrl+Z undoes the whole drag. The wheel over a ring turns it in steps, Shift
  finer, and a right click locks the ring. Character axes turn it about up, forward and to
  the side whatever its own axes are.
  Partly checked (XIV Bridge tests/instant-edit/animations/212, 2026-10-10), on the thigh rather than the neck (in GPose
  the spine, neck and head follow the camera, which skews the readings): a 30° drag on the Y ring with Live on and
  paused turns the bone 30.0° about its own Y axis and it stays; a key appears at the playhead; Ctrl+Z undoes the whole
  drag; with Character axes the X, Y and Z rings turn it exactly about the character's side, up and forward. Not
  checked: the rings following the camera, the wheel, Shift and the right-click lock (they need the mouse). In game
  2026-10-29 (by hand, Idle pose 3, the neck on an offset layer, Live on and paused): the rings follow the camera, a
  ring drag turns the head and keys the playhead, the wheel turns in steps (finer with Shift), and a right click locks
  a ring.
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
keying are covered offline in PoseFileFixture; all 8,897 poses in D:\Mods\Poses parse, 60%
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
- [x] After reloading the plugin, Edit again still works.
  Partly checked (XIV Bridge tests/instant-edit/animations/222, 2026-10-10): a new journal store reads the record and
  the kept motion back from disk as a reload does, and Edit again opens the clip from the record's own capture with
  its layer and in place. The plugin itself wasn't reloaded.
  (XIV Bridge animations-pending/462, Release 2026-10-29: after a real reload, Edit again opens the clip with its saved
  layer from the saved mod, offers saving in place, and the in-place save works.)
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
- [x] Select an offset layer's key: under the keys the panel shows "To the next key" with the
  layer's way and a curve. Pick Hold: the key turns into a square and with Live on the bone
  stays put until the next key, then jumps. Slow start and Slow end visibly lag or lead.
  Partly checked (XIV Bridge tests/instant-edit/animations/230, 2026-10-10, a 0° to 40° neck layer with Live paused):
  a selected key draws the ease combo and curve, none without a key; Live matches the evaluator at every frame
  (0.001°) for Smooth, Hold, Slow start, Slow end and Linear; Hold stays at 0.0° until frame 59 and is 40.0° at 60;
  Slow start lags (7.4° against 13.3° at frame 20) and Slow end leads (16.3°). Not checked: the square key marker.
  In game 2026-10-29 (by eye, the same neck layer): after Hold the key is drawn as a square.
- [x] Drag a handle of the curve: the key turns into a circle, the ease becomes Custom and Live
  follows while dragging; the drag is one undo step. A handle pulled above the box makes the
  bone overshoot the next key's value and settle back.
  Partly checked (XIV Bridge tests/instant-edit/animations/230, 2026-10-10): the ease becomes Custom, Live follows the
  drag (28.1°, 36.8°, 45.5° at frame 20), one Undo takes it back, and an overshooting curve reaches 60.0° and settles at
  40.0° on the key. Not checked: the circle key marker. In game 2026-10-29 (by hand, the same neck layer): a handle
  drag turns the key into a circle, and a handle above the box makes the neck overshoot and settle back.
- [x] Right-click a key: To the next key lists the layer's way and the presets.
  Partly checked (XIV Bridge tests/instant-edit/animations/230, 2026-10-10): the presets are Linear, Smooth, Slow start,
  Slow end and Hold, and the layer's way is Smooth. The menu itself wasn't opened (it needs a right click). In game
  2026-10-29 (by hand, the neck layer): the key's menu lists the layer's way and the five presets.
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
D:\Mods\Poses\LivePose, unticked bones as an undoable document edit, a timeline save's
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
- [x] Record records and sends to Blender as the old tab did; the countdown slider wraps below
  Length in the narrow panel.
  (XIV Bridge tests/instant-edit/animations/240, 2026-10-10: in the 304 px panel the countdown slider is a row below
  Length; Record counts down 3 s, records the GPose character (187 bones at 60 fps) and Stop ends it; the take goes to
  a receiver answering as Blender's add-on, after a status request, and "Send last recording again" sends the same
  bytes. A run against real Blender isn't needed, 2026-10-29.)

LivePose files (the live LivePose items were dropped 2026-10-30):
- [x] Add > Offset layer from a .livepose file: pick one from D:\Mods\Poses\LivePose; the
  layer is named after the file and Live shows the pose. Bones the skeleton lacks are counted.
  (XIV Bridge tests/instant-edit/animations/15, 2026-10-10: bend2.livepose on Idle pose 3, the file's result
  handed in as the dialog's callback does. Layer "bend2", one key over the clip, selected; "Added a layer
  from 4 adjusted bones. 1 bone was left out."; with Live, j_naka_a_l turns the layer's 16.5° against Live
  without the layer, which matches the game's playback within 0.4°.)
- [x] A LivePose scale adjustment kept from its children (LivePose's default) becomes a layer
  that scales only that bone: with Live on, its children keep their size, as in LivePose.
  (XIV Bridge tests/instant-edit/animations/242, 2026-10-10, from a .livepose file: j_ude_a_l x1.5; its children get
  x0.667, and with Live on the arm is 1.5x bigger while j_ude_b_l and n_hkata_l keep their size in the model (x1.000).)
- [x] On an expression: Face page > Save the face without them runs the old face rebake, into a new mod or in
  place as the footer says.
  (XIV Bridge tests/instant-edit/animations/243, 2026-10-10: Save the face without them saves Smile into a new mod
  (which then plays) and in place.)

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
  axis. The ImGuizmo widget itself wasn't dragged, so where it draws wasn't checked. In game 2026-10-29 (by hand,
  j_kubi at 50 %, Live paused at frame 20): during a 30° ring drag the gizmo stays drawn on the bone.)
- [ ] Below 5% (the first frames of a fade-in), hovering the gizmo shows "The layer barely applies
  here." and a drag changes nothing.
  (XIV Bridge tests/instant-edit/animations/18, 2026-10-10: at 3% the gizmo's step changes nothing. The tooltip needs a hover over the gizmo: not checked.)
  (XIV Bridge tests/instant-edit/animations/244, 2026-10-10: still not checked. With the injected mouse held near the
  neck's dot and the Timeline window collapsed, ImGuizmo never reported the mouse over its rings; not diagnosed.)
  Partly checked (XIV Bridge tests/instant-edit/animations/244, 2026-10-29): passes with a check that counts the tooltip
  ImGui begins: 1 at 3 % and none at full amount; a drag at 3 % changes nothing and adds no undo step. Confirm once by
  hand (GPose, a layer at 3 %, hover the rings). In game 2026-10-29 (by hand, the neck on Idle pose 3 at 3 %, Live
  paused): the drag changes nothing, but no tooltip shows on hover. ImGui begins it (244's count) yet never shows it.
  Fixed 2026-10-29 (likely cause: it was begun only on frames where ImGuizmo reports the mouse over the rings, which
  alternate, and ImGui hides a tooltip on the frame after one without it): the tooltip is held 4 frames after the
  rings were last hovered, so it's begun every frame. Retest: XIV Bridge visual/244v (its strip shows the cause).
- [x] Gizmo over dots: with a hand bone selected, a ring over a finger dot drags the ring and doesn't
  select the finger; clicking a dot away from the rings selects it.
  Partly checked (XIV Bridge tests/instant-edit/animations/245, 2026-10-10): with j_te_l selected, a click on j_sako_l
  away from the rings selects it. Not checked: a ring over a dot (ImGuizmo never reported the mouse over its rings, as
  in test 244). On a 3440x1440 screen the Timeline window's side panel covered the character until it was collapsed.
  In game 2026-10-29 (by hand, j_te_l on an offset layer, Live on and paused): a ring dragged over a finger dot turns
  the hand and doesn't select the finger; a click on a dot away from the rings selects it.
- [x] Brio's gizmo still flips its axes toward the camera after the Instant Edit gizmo was used.
  In game 2026-10-29 (by hand, GPose, after dragging the Instant Edit gizmo on j_te_l and closing the Timeline window):
  Brio's gizmo still flips its axes toward the camera.
- [x] With the Timeline window collapsed, leave GPose in the middle of a gizmo drag. Back in GPose,
  Brio's gizmo and the camera work, and the next edit is its own undo step.
  Partly checked (XIV Bridge tests/instant-edit/animations/246, 2026-10-10): a gizmo drag running when the overlay stops
  being wanted, with the Timeline window collapsed, ends within a few frames as one undo step, ImGuizmo isn't left in
  use and the next edit is its own step. Not checked: a real drag cut off by leaving GPose (the drag never started the
  gizmo, as in test 244), Brio's gizmo and the camera. In game 2026-10-29: a real drag on j_te_l (Live on and paused,
  Timeline collapsed), with GPose left by script while the button was held, ended as one undo step and the gizmo let go
  of it; back in GPose, Brio's gizmo and the camera work.
- [x] Pose layer Move with several bones selected, including n_hara or j_kosi whose keys only turn
  them: those bones move from where they are now, with no jump.
  (XIV Bridge tests/instant-edit/animations/18, 2026-10-10: j_kosi and j_sebo_a keyed with turns only, then moved 5 cm by the gizmo's step: both move
  exactly 5.00 cm from where they were.)

Timeline editor:
- [x] Moving face pace: a long animated face (Dawntrail Expression Library [illusio_vitae path]) with
  Time control off shows its own length and "own speed", not "held". Ticking Time control keeps the
  length, and in game the face plays at the same speed as before.
  (XIV Bridge tests/instant-edit/animations/247, 2026-10-10, on Panting Face's 600-frame face: it reads "20s · 600
  frames own speed", not held; Time control keeps 600 frames and the save writes 0..600 over 600 (the same speed).
  The Dawntrail library's faces on a body clip weren't checked, and that part was dropped, 2026-10-29.)
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
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/270, 2026-10-29): Goodbye plays the moan; 90/10 and
  10/90 read back as set. Not checked: the 90% sound about 18 of 20 times (by ear: run with Args {"listen": true} while
  someone counts).
- [ ] 2 What the wait does: Listen to a moan with its original short wait (amber mark), then after
  Match length. Describe the difference you hear (the end cut, nothing, or the next sound overlapping).
  This decides whether "ends before its audio" stays a soft hint or becomes a warning.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/270, 2026-10-29): the moan has a sound with a short
  wait (amber mark); after Match length no sound ends before its audio and the section marks none. Not checked: what
  the wait sounds like (by ear: run with Args {"listen": true} and compare the two plays).
- [ ] 3 One-shot to random set: on a vanilla emote's sound, Add sound (a .wav), Preview. Expect each
  play to pick either sound about half the time, and the clip on the canvas solid to the shorter
  sound and dashed to the longer one.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/271, 2026-10-29): the Water emote's one-shot becomes
  a random set of two sounds at 50/50; the canvas clip is solid to the shorter (3.50 s) and dashed to the longer
  (4.50 s); Preview plays the edited copy, not the vanilla file. Not checked: that each play picks either sound about
  half the time (by ear: run with Args {"listen": true}).
- [x] 4 Pinned event: set one moan event to "Only #3". Expect Preview (after Preview again) and the
  saved mod to play only that moan, from a moan_3_… copy, while the other moan events stay random.
  (XIV Bridge tests/instant-edit/animations-pending/272, 2026-10-29: Goodbye's moan event pinned to "Only #3": Preview
  plays a moan_3_ copy holding that one sound, the saved timeline points the event at it, the other moan events keep
  sound/moan.scd, and the saved mod plays the copy.)
- [x] 5 Everywhere or only here: on a mod with two animations that name the same SCD, save a chance
  change in place. Expect "Change the sound everywhere?" with the right count; Everywhere changes the
  mod's file (both animations hear it), Only here saves a copy for this animation only. Undo last
  save puts the mod's file back.
  (XIV Bridge tests/instant-edit/animations-pending/273, 2026-10-29: Save asks "Change the sound everywhere?" for
  sound/moan.scd with the right count; Everywhere changes the mod's file and Clap's animation still names it; Undo
  last save puts the file back; Only here saves a copy for this animation and leaves the mod's file as it was.)
- [x] 6 Keep looping: on a looping emote whose sound plays once, set Plays to Keep looping and click
  Add loop points. Expect the sound to keep going while the emote loops, in Preview and after Save.
  (XIV Bridge tests/instant-edit/animations-pending/274, 2026-10-29: Plays is Keep looping and Add loop points makes
  the file loop; the song keeps going past its own length, 17.8 s for a 3.5 s song, in Preview and after the
  in-place save.)
- [ ] 7 Listen on MS-ADPCM, Vorbis and HCA files: each row's button plays that alternative alone, the
  header button the whole set, and nothing stays served afterwards (the listen_… path no longer
  resolves a few seconds after the sound ends).
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/275, 2026-10-29): on MS-ADPCM, each row plays its
  one sound and the header the whole set (10); the played file is served while it plays and not a few seconds after it
  ends. Not checked: Vorbis and HCA (no sound with those codecs among the moan and the game's emote sounds; try a
  mod's), and hearing each sound.

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
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/280, 2026-10-29: the File row shows sound/lolo.scd with "From mod:
  ..." under it; another .scd path typed is one undo step, and the saved dance asks the game for the new sound, not the
  old one (heard: no); a path ending in .avfx is refused and the document stays as it was.)
- [x] 2 Live with another length: the game loops a swapped Live copy at its own length (XIV Bridge,
  2026-10-14: a 9.7 s copy of the 14.57 s dance wrapped every 9.7 s; pause, seek, replay and Stop
  behaved; the test tool is gone since). Still to see by hand: leaving GPose and unloading the plugin
  with a cut showing under Live.
- [x] 3 Fix loop on the TikTok dance: the clip band's end badge says Stalls; the seam panel says "The
  loop starts again faster than it ends." Fix loop eases the start over 30 frames and keeps the length
  (no trim); the badge says Loops cleanly. With Live on, try Ease 20, 30 and 45 and Side Start and End;
  then Preview and save in place: the dancer slows into the wrap and picks up again, no settle-then-jerk.
  (XIV Bridge tests/instant-edit/animations-pending/282, 2026-10-29: the badge says Stalls and the panel "The loop
  starts again faster than it ends."; Fix loop eases the start over 30 frames, keeps the length (437) and trims
  nothing, and the badge says Loops cleanly; Ease 20, 30 and 45 at Start and End show in Live; Preview and the in-place
  save play, the saved file loops Clean with no settle before the wrap or jerk after it, and the game plays it.)
  Removed 2026-10-30 (the user's call): Fix loop's seam blend pulled a loop into a bad first frame (Angel Dust's
  smoking idle, legs up to 19 deg), and Close loop does the job. The seam panel keeps the badge, the gap and the stall
  or speed note; an older edit's seam blend or ease still plays and can be changed or removed there.
- [x] 4 Remove a range from a dance with C, Q, W and Delete: no pop at the join in Preview, and the
  sounds, effects and faces after it moved with the motion.
  (XIV Bridge tests/instant-edit/animations-pending/283, 2026-10-29: C, C and Delete take out 45 frames (437 to 392),
  leaving two parts joined by a blend; every event after the range moved with the motion and every one before stayed;
  Preview plays and nine watched bones show no pop at the join (22.6 deg at most, the dance's own 22.7); Q and W remove
  from the start and to the end; Undo brings back the whole clip.)
- [x] 5 Cut a sound with the motion: remove a range from a dance with music, choose "Cut with the
  motion", Preview and save. The song stays in step after the cut.
  (XIV Bridge tests/instant-edit/animations-pending/284, 2026-10-29: saving waits for the choice; with Cut with the
  motion the save adds a cut copy of the song, shorter by what the cut took out (246.02 s for 248.02 s - 2), and the
  saved timeline plays the copy.)
- [x] 6 Live with cuts: drag a cut point, a blend length and the seam with Live on.
  (XIV Bridge tests/instant-edit/animations-pending/285, 2026-10-29: a cut point, the join's blend length and the loop
  seam's blend each change in one undo step, Live keeps running and shows the changed motion.)
- [x] 7 Edit again: reopen a cut dance with Edit again, move a cut, and save again. With Live on after
  Edit again, the cut shows on the character.
  (XIV Bridge tests/instant-edit/animations-pending/286, 2026-10-29: the cut saves in place and Edit again brings it
  back at the cut length (392), in place chosen, Live showing the cut; the moved cut saves a 382-frame file that keeps
  it. Fixed tonight for a borrowed clip rebuilt for the character's race: a rebuild save keeps the rebuilt motion from
  before.)
- [x] 8 Save twice: cut a dance with a bone layer and a song cut with the motion, save, change the
  layer, save again. After the first save the window shows the saved file with the "Saved with edits
  you can open again" line; the second save keeps the cut and the song stays in step. Edit again then
  shows the song's whole file chosen as "Cut with the motion", and saving cuts it once.
  (XIV Bridge tests/instant-edit/animations-pending/287, 2026-10-29: after the first save the window shows the saved
  file with "Saved with edits you can open again."; the second save keeps the cut length (377) and the song in step,
  cut once; Edit again chooses the song's whole file as Cut with the motion and saving cuts it once. Same fix as 7.)

## Pose matches on the clip band (pending)

Paused, the clip band marks the frames that play the playhead's pose the same way (every bone within a
mean 1 deg and worst 5 deg, over the frame and two either side), for finding a clean loop inside a
clip. Code: Services/Animations/Motion/PoseMatches.cs, Ui/Timeline/TimelineClipBand.cs.

- [x] 1 Open a dance that repeats a move (the TikTok dance): paused, green marks show on the clip band
  where the playhead's moment comes back; none while it plays or while you scrub, and they move when you
  step the playhead. No hitch when stepping with the arrow keys.
  (XIV Bridge tests/instant-edit/animations-pending/290, 2026-10-29, Cackle: paused, the pose of some frame comes back
  and every mark is within the tolerances; the marks move when the playhead steps (0.27 ms a step); none while
  scrubbing or playing.)
- [x] 2 Hover a mark: "Same pose and motion as frame N: x deg apart, worst bone y deg." Click it: the
  playhead jumps there and the marks follow.
  (XIV Bridge tests/instant-edit/animations-pending/290, 2026-10-29: the tooltip reads "Same pose and motion as frame
  0: 0.7 deg apart, worst bone 3.8 deg."; a click takes the playhead to the mark (frame 64) and the old playhead is
  now a mark.)
- [x] 3 Right-click a mark, Keep only this loop: the clip keeps the frames between the playhead and the
  mark (one undo step), and the seam badge says Loops cleanly or close. Preview: the move loops with no pop.
  (XIV Bridge tests/instant-edit/animations-pending/290, 2026-10-29: Keep only this loop is one undo step and keeps
  frames 0 to 64; the seam is Clean; in Preview nine watched bones pop 5.2 deg at most at the loop point, against the
  dance's own 4.8.)
- [ ] 4 Tolerance: on a few real dances, are marks missing where you see a repeat, or shown where the
  pose visibly differs? (PoseMatches.MeanDegrees / WorstDegrees.)
  Not answered (XIV Bridge tests/instant-edit/animations-pending/291, 2026-10-29): on Moogle Dance, Ball Dance, Bee's
  Knees and Bomb Dance the run checked 0 marks in game and listed no near misses, so it says nothing about the
  tolerance. A judgement by eye on real dances.

## Motion from Blender (pending)

Sending a clip's motion from the Timeline to Blender and bringing the edited action back as the
clip's motion. Spec: docs/superpowers/specs/2026-10-16-motion-from-blender-design.md. Needs a
Blender add-on the plugin accepts, with the listener running.

- [ ] 1 Round trip: open a body animation in the Timeline, press the arrows button next to the Bones
  button, Send motion to Blender, then Motion from Blender without editing. The line under the toolbar
  says "Blender's motion matches the clip's. Nothing changed." Then add a bone layer, send it again and
  read it back without editing: the line names the action, the layer is gone, the length is unchanged
  and Preview looks the same. Skirt and hair still swing.
  Partly checked (XIV Bridge animations/261 and 262, Release 2026-10-30): the motion sent to a headless Blender comes back
  within the unchanged-bone tolerance, builds nothing, and the line says nothing changed. Not checked: the bone-layer half
  (send with a layer, read back: the layer gone, the length kept, Preview the same) and skirt and hair swinging (by eye).
  (XIV Bridge animations-pending/365, Release 2026-10-30 01:48: with a 15 deg neck layer on Idle pose 3, the motion sent to
  a headless Blender and read back unedited builds a motion from Blender; the line reads "Motion from Blender: "Bridge layer
  trip" on "Skeleton", 106 bones, 70 frames.", the layer is gone, the length stays 70 frames, and the timeline plays what it
  played with the layer (0.52 deg, the neck within 0.07 deg); Ctrl+Z gives the layer back. Left: the Preview's look and skirt
  and hair swinging, by eye.)
- [x] 2 Turn an arm in Blender (keyed over the clip) and read it back: Live and Preview show the turn.
  Partly checked (XIV Bridge animations/261, Release 2026-10-30): j_ude_a_l turned 25 deg in Blender comes back turned 25 deg
  throughout, every other bone keeps the clip's motion, and Live shows the turn on the GPose actor. Not checked: Preview.
  (XIV Bridge animations-pending/366, Release 2026-10-30 01:48, the same 25 deg turn built without Blender as 260 does: in
  Preview the GPose actor's j_ude_a_l is 25.1 deg off a Preview of the clip's own motion; after Ctrl+Z two Previews of the
  clip's own differ by 0.2 deg.)
- [x] 3 Extend the scene End by 30 frames, key a pose there and read it back, once with Scale events
  off and once on. Sounds and faces stay where they were, or move with the new length.
  (XIV Bridge tests/instant-edit/animations-pending/300, 2026-10-29: a scene 30 frames longer than the clip builds a
  110-frame replacement (the clip has 80); j_ude_a_l ends turned 25 deg from the clip's last pose and follows the clip
  until its old end; with Scale events off sounds and faces keep their frames, on they move with the new length; Undo
  gives the clip its own motion back.)
- [x] 4 Ctrl+Z and "Use the clip's motion" both go back to the animation's own motion.
  (XIV Bridge animations/261 and 262, Release 2026-10-30: "Use the clip's motion" and Ctrl+Z each take the motion from
  Blender out and the clip plays its own motion again, Live staying on.)
- [x] 5 Save to a new mod and in place; the saved animation plays the Blender motion. Undo the save.
  Partly checked (XIV Bridge animations/260, Release 2026-10-30): saved into a new mod, the clip plays the motion from
  Blender and reads back with nothing pending. Not checked: the in-place save and undoing the save.
  (XIV Bridge animations-pending/364, Release 2026-10-30 01:48: a second motion from Blender saved in place into the test mod
  the first save made plays it (the arm turned 35.0-35.2 deg) and reads back with nothing pending; Undo last save puts the
  first save's file back byte for byte and the clip plays the first motion again (20.0 deg).)
- [x] 6 Add a bone layer on the Blender motion, save, then Edit again: the layer comes back on the
  Blender motion, not stacked on the saved file.
  (XIV Bridge tests/instant-edit/animations-pending/301, 2026-10-29: Edit again is offered and brings the layer back on
  the Blender motion, which the journal keeps as the edit's base; it plays what it played before the save, within 0.00
  deg.)
- [x] 7 Errors: Blender closed; the armature in Rest Position; an armature with no animation. Each
  shows a short message on the line under the toolbar.
  (XIV Bridge tests/instant-edit/animations-pending/302, 2026-10-29: Blender closed, the armature in Rest Position and
  an armature with no animation each end the read with a line that says why (108, 127 and 136 characters), and the
  timeline keeps the clip's motion. The line now shows only Blender's cause and remedy; it was 211-220 characters.)
- [x] 8 Press Motion from Blender, then Cancel on the progress line: it says "Reading stopped." and the
  timeline is unchanged.
  (XIV Bridge animations/262, Release 2026-10-30: with Blender holding the read up, Cancel is drawn, ends the read, the
  line says it stopped and nothing changed.)
- [x] 9 With Key bone scale off, send, scale a bone in Blender (keyed) and read it back: the line under
  the toolbar ends with "Its scale was left out: Key bone scale was off when it was sent."
  (XIV Bridge tests/instant-edit/animations-pending/303, 2026-10-29: the line ends with that text; no other bone
  changed, so nothing was built.)

## Symmetry layer (pending)

A bone layer that makes picked bones copy their twins on the other side, mirrored, on every frame.
Spec: docs/superpowers/specs/2026-10-17-symmetry-layer-design.md. Test clip: The Basics - Miqo
Idle 4, option Standing idle 4 (pose04_loop).

- [x] 1 Open Standing idle 4 in the Timeline, select the left shoulder in the Bones window and press
  Make symmetrical. A "Symmetry (Left → Right)" layer appears over the whole clip and is selected; the
  panel says how many bones it copies.
  (XIV Bridge tests/instant-edit/animations-pending/310, 2026-10-29: the left clavicle makes "Symmetry (Left -> Right)"
  over frames 0 to 150, selected; the panel says "Copies 21 bones from the left side.")
- [ ] 2 In GPose with Live on, the right hand rests on the chest like the left, and keeps moving with
  the idle. Right-arm dots in the Bones window and the overlay take the layer's colour; their tooltip
  says "Copied from the other side."
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/310, 2026-10-29: with Live the right hand mirrors the left within
  0.1 mm and keeps moving with the idle; the right hand's and upper arm's dots say "Copied from the other side.", the
  left hand's doesn't. The dots' colour was not read.)
- [x] 3 Switch Copy from to Right: the left arm now copies the right, and the layer is renamed
  "Symmetry (Right → Left)". Switch back.
  (XIV Bridge tests/instant-edit/animations-pending/310, 2026-10-29: the layer is renamed "Symmetry (Right -> Left)" and
  the left arm copies the right within 0.1 mm; switched back, the name is back.)
- [x] 4 Add an Offset layer above it and turn the right hand: the copy is adjusted. The gizmo is off
  while the Symmetry layer is selected.
  (XIV Bridge tests/instant-edit/animations-pending/310, 2026-10-29: an Offset layer above it adjusts the copy by 30.0
  deg; no gizmo shows on the Symmetry layer, and Rotate is back for the Offset layer.)
- [x] 5 Save in place: the saved file plays the same, and the loop does not pop where it starts over.
  Undo the save.
  (XIV Bridge tests/instant-edit/animations-pending/311, 2026-10-29: the saved file plays what the editor showed within
  0.0005 units and 0.07 deg; five watched bones step 0.2 deg at most where the loop starts over; the right hand
  mirrors the left in the saved file; Undo puts the file back.)
- [x] 6 Save again, then Edit again: the Symmetry layer comes back with its side, bones and blend.
  (XIV Bridge tests/instant-edit/animations-pending/311, 2026-10-29: it comes back with side Left, bones j_sako_l,
  blend 0.75 and its name.)
- [x] 7 Pick the chest with Copy from Left: the arms mirror and the breasts still move with physics.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/311, 2026-10-29): with the chest (Upper Spine) picked
  the arms are copied and mirror, and j_mune_l and j_mune_r still move (5.2 deg and 5.3 deg). Not checked: that the
  breasts are left to physics; this clip tracks j_mune_r, so the layer copies it.
  (XIV Bridge visual/311s, Release 2026-10-29: under Live, j_mune_r keeps its physics share on top of what the document
  says with the chest layer, as much as with a layer that leaves the chest alone.)

## Loop and startup into a new mod (pending)

A Timeline save to a new mod takes the other half of a loop and its startup along, unchanged, when the
collection gives both from the same mod.

- [x] 1 Open a loop whose startup comes from the same mod (Standing idle 4 with The Basics - Miqo
  Idle 4 on). Choose Create new mod: the footer says "Its startup goes into the new mod too." Save, then
  check in Penumbra that the new mod has both pose04_loop.pap and pose04_start.pap.
  (XIV Bridge tests/instant-edit/animations-pending/320, 2026-10-29: the footer says it; the save writes the loop and
  the startup, the new mod maps both, holds no other animation, and the startup is the mod's own, unchanged.)
- [x] 2 Turn the original mod off and redraw: the idle starts with the mod's own startup, then the edited
  loop.
  (XIV Bridge tests/instant-edit/animations-pending/320, 2026-10-29: with the original off, the startup and the loop
  both come from the new mod and the idle plays the edited loop.)
- [x] 3 Open the startup instead (its row under the loop) and save it to a new mod: the footer says "Its
  loop goes into the new mod too.", and the new mod has both files, the loop unchanged.
  (XIV Bridge tests/instant-edit/animations-pending/321, 2026-10-29: the footer says it; the new mod has both files,
  the loop unchanged and the startup the edited one.)
- [x] 4 A loop whose startup comes from another mod or the game (Standing idle 3 here: its startup is from
  "A2 on Idle 3"): no footer line, and the new mod has the loop only.
  (XIV Bridge tests/instant-edit/animations-pending/322, 2026-10-29, loop from "Unholy Idle": for a startup from
  another mod and for one from the game there is no footer line, and the new mod maps the loop only.)
- [x] 5 Replace in-place: no footer line, and only the edited file changes.
  (XIV Bridge tests/instant-edit/animations-pending/320, 2026-10-29: no footer line; only the loop is written and the
  startup's file is unchanged.)
- [x] 6 Undo the new-mod save from Recent edits: the new mod is gone with both files.
  (XIV Bridge tests/instant-edit/animations-pending/320, 2026-10-29: the save reads Undone, the new mod is off, and
  neither file comes from it any more.)

## Pin layer (pending)

A bone layer that holds bones at a spot, keyed or where the motion has them down, and bends the limb
to reach it. Spec: docs/superpowers/specs/2026-10-23-pin-layer-ik-design.md. Test clip: Rough Sketch
Fortitude on Idle 6, option default (pose06_loop).

- [x] 1 Open Standing idle 6 in the Timeline; Add, Pin feet. A "Pin feet" layer appears over the whole
  clip and is selected; each foot shows where it is held, such as "Held 0-119 (the whole loop)".
  (XIV Bridge tests/instant-edit/animations-pending/330, 2026-10-29: "Pin feet" over frames 0 to 119, selected, both
  feet planted; each foot reads "Held 0-119 (the whole loop)".)
- [x] 2 In GPose with Live on, camera down at the shoes: the feet no longer turn or slide, and the knees
  stay natural.
  (XIV Bridge tests/instant-edit/animations-pending/330, 2026-10-29: in Live the feet slide 5 mm and 1.6 mm and turn
  0.3 and 1.1 deg at most; the knees keep their bend side and move 1 mm at most in the evaluator.)
- [x] 3 Select the right hand in the Bones window and press Pin selected. In the new Pin layer set
  Attached to Upper Spine and press Pin here once, then move the key with the gizmo on Move: the arm
  follows and the elbow keeps its side.
  (XIV Bridge tests/instant-edit/animations-pending/331, 2026-10-29: Relative to is Upper Spine; Pin here keys the hand
  where it is; the gizmo's move takes the hand within 0.006 mm, the arm follows and the elbow keeps its side.)
- [x] 4 Add a second key later in the clip at another spot: the hand travels between them with the ease.
  (XIV Bridge tests/instant-edit/animations-pending/331, 2026-10-29: the hand starts at the first spot, ends at the
  second, goes the layer's way (Smooth) and stays near the straight path.)
- [x] 5 Save with Replace in-place: the file plays the same in game, and the loop does not pop where it starts over.
  (XIV Bridge tests/instant-edit/animations-pending/330, 2026-10-29: the saved file plays what the editor showed within
  0.002 mm and 0.07 deg; watched bones step 0.3 deg at most where the loop starts over.)
- [x] 6 Edit again: the Pin layers come back with their pins and settings.
  (XIV Bridge tests/instant-edit/animations-pending/330, 2026-10-29: the Pin feet layer comes back with its pins and
  their settings over the same range.)
- [x] 7 Click the Bones window's crosshairs switch three times: the selected bones only (dot icon), all
  bones, then none. Each tooltip matches what GPose shows.
  (XIV Bridge tests/instant-edit/animations-pending/332, 2026-10-29: the switch goes All, Off, Selected, All in three
  clicks; Off shows no bones, Selected only the selected two, All the 92 map bones.)
- [x] 8 Open Side Step (Cute Groove) and Add, Pin feet: only a few short stretches are held. In GPose the
  feet still shuffle as the dance has them, and nothing pops where a hold lets go.
  (XIV Bridge tests/instant-edit/animations-pending/338, 2026-10-24, on the NFLB + YAS skeleton: left foot "Held 117-125,
  622-626, 754-759, 772-775", right "Held 148-157, 706-712, 822-828", 2 % of the samples each; outside the holds the feet
  follow the dance exactly (0.0 mm) and the largest step is the dance's own; with Live the feet wander as far as without
  the layer (54.7 and 14.6 cm against 53.0 and 14.8) and no step is bigger.)
  The first run crashed the game. The dance's PAP binds 153 tracks to bones 1-153 (made on a c0101 skeleton with more
  bones); 338 played it on the GPose actor with the vanilla c0801 skeleton (106 bones) and LightlessSync unloaded,
  which otherwise blocks such clips ("limit=106"). The game's sampler wrote past the skeleton's arrays, and the heap
  damage surfaced in the next Havok allocation, the listener's load of the same PAP (report
  dalamud_appcrash_20261024_190627_968_26364.log). The file itself is sound: it parses offline with the same
  definitions and layout as a TikTok PAP that loads. Since then the loader and Preview refuse a clip with a track past
  the actor's skeleton ("Your character's skeleton lacks bones this animation moves. Playing it could crash the
  game."), as Live already did: XIV Bridge 346 refuses Side Step on the 106-bone skeleton without playing it and allows
  it on NFLB + YAS (455 bones). Not checked: Preview's refusal in game (it needs an edit made for a bigger skeleton
  previewed on a smaller one).
- [ ] 9 On a clip whose feet float or tip a little (Standing idle 6, or any port), Add, Pin feet, and set each foot's
  Floor to On the floor. In GPose with Live on, camera down at the shoes: both soles lie flat on the floor, neither
  floating nor sunk, and the knees stay natural. On the Kneel emote, look at a foot tipped far: toes down, the ball
  should touch the floor at the motion's angle; note where a foot lying on its top ends up (the sole model has only the
  heel and ball). Set Floor back: the feet are where they were.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/532, Release 2026-11-01, Idle 6 on NFLB + YAS): the
  motion's held soles sit 3.0 and 3.7 mm over model Y 0 (so Y 0 is the floor), tilted 0.6 and 2.8 deg. The first run
  levelled the right foot and it floated 7.7 mm: the standing leg is about straight and can't lower the ankle. Since then
  a foot levels only as far as its sole still touches; on the second run the left sole is at 2.7 mm and 0.4 deg, the right
  keeps 3.7 mm and 2.8 deg, the knees keep their side, and Live shows it in game (0.00 deg off the document, soles 2.7 and
  3.7 mm). Floor back off gives the motion Pin feet gives without it (0.00 mm; the 0.07 deg the angle reads is its noise
  floor: a tail bone the layer never touches reads it between two equal reads). Kneel (XIV Bridge 533, its own test, as
  opened after Idle 6 its loader row twice didn't open in 45 s): its held feet are flat (0-1 deg); with Floor on the soles
  are 0.1 and 2.0 mm, the same as Pin feet without Floor (a held spot must be in the leg's reach at every sample of its
  stretch), and the knees keep their side. No held foot of Kneel is tipped past 30 deg, so that case is checked only
  offline. Not looked at by eye.

## IK drag (pending)

The gizmo's Drag mode moves a bone and bends the limb above it, keying the selected layer at the
playhead. Spec: docs/superpowers/specs/2026-10-24-ik-drag-design.md.

- [x] 1 Open a dance in the Timeline, add an offset layer, select the right hand, Gizmo: Drag. In GPose with
  Live on and paused: drag the hand forward 20 cm. The elbow bends naturally, the hand goes where the
  gizmo is, and the layer gets keys on the upper arm and forearm at the playhead.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/333, 2026-10-29, Idle pose 3 for a dance): the
  gizmo's Drag step bends the elbow on its own side, puts the hand on the aim in the evaluator and in game, and keys
  the upper arm and forearm at the playhead only. Not checked: a real mouse drag (the injected one didn't start the
  gizmo again: OverlayDragging stayed false, as in 244). In game 2026-10-29 (by hand, Idle pose 3, Live paused at
  frame 20): a real 20 cm forward drag bends the elbow naturally and the hand goes where the gizmo is.
- [x] 2 Set the layer's amount to 50 %: a drag still puts the hand where the gizmo is.
  (XIV Bridge 334, 2026-10-24: in the evaluator and in game; the keys turn the arm about twice what the bones turn.)
- [x] 3 A pose layer, the left foot: drag it up; the knee bends forward, never backwards.
  (XIV Bridge 335, 2026-10-24: up 15 cm, the foot lands on the aim and the knee bends along the toes' direction.)
- [x] 4 Drag a hand far beyond the arm's reach: the arm straightens smoothly, nothing snaps or shakes. Then
  drag back: the arm stays straight until the gizmo is back within reach, and the hand ends under the gizmo.
  (XIV Bridge 336, 2026-10-24: 30 cm past reach in 2 cm steps, the hand never passes the reach or jumps and holds still
  in game; back in, the hand ends under the gizmo where it started.)
- [x] 5 Select the body (n_hara): Drag is disabled with "The body moves the whole character. Select the hips to drag them." The hips
  (j_kosi, "Hips") drag on a layer that moves bones; on a layer that only turns bones they say "This layer doesn't move bones."
  (Since hip drag, commit 7f8c6f9, the old "Hips sits right under the body" refusal is gone; see Hip drag.)
  (XIV Bridge 337, 2026-10-29: Drag is allowed on the waist and stays the chosen mode, the gizmo drags it as a move; on a
  turn-only layer it says "This layer doesn't move bones."; the body is refused with that text. XIV Bridge 354, 2026-10-28:
  the body gets no move gizmo either; the hips on the same layer drag.)
- [x] 6 Undo once after a drag: the whole drag comes back in one step.
  (XIV Bridge 333, 2026-10-24: one undo step; one Undo brings the document and the hand in game back.)

## Hip drag (pending)

Drag mode on the hips moves them; the bones ticked under Hold (both feet and both hands at first, plus any
bone selected before Ctrl+clicking the hips) stay where they are and the legs, arms and back follow. Spec:
docs/superpowers/specs/2026-10-27-hip-drag-design.md. Changed 2026-10-31 (user): Hold boxes replace choosing
the held bones by selecting them and Ctrl+clicking the hips.

- [x] 1 The seated pose (hips on the floor, heels down, hands planted behind): an offset layer, select the
  hips, Gizmo: Drag. In GPose with Live on and paused, drag the hips forward 10 cm and up 5 cm: the feet
  and hands stay where they were, the knees and elbows straighten, the torso leans back. The layer keys
  the hips (position), both thighs, knees and feet, both arms, the clavicles and the back.
  Partly checked (XIV Bridge 350, 2026-10-28, Ground sit, injected drag): the hips land on the aim, feet and hands
  hold (0.0 mm, in game too), elbows straighten (48/54 to 23/24 deg), the torso leans back (7.9 to 14.7 deg). Keyed:
  hips, thighs, knees, feet, upper arms, forearms, hands and j_sebo_a-c; **the clavicles are not keyed**: HipSolver.Back
  leaves them out and the arms bend at the upper arm and forearm only. Decide: key the clavicles, or drop them from this
  item. Knees go 135/130 to 144/140 deg (the hips moved toward the feet). Not checked: a real mouse drag.
  Changed 2026-10-28 (the user chose it): the clavicles are keyed. Each takes a third of its arm's swing, after the back
  leans, and the arm bends the rest. Retested (XIV Bridge 350, 2026-10-28): both clavicles keyed, the hips on the aim,
  feet and hands hold. Still not checked: a real mouse drag. In game 2026-10-29 (by hand, Ground sit, Live paused at
  frame 20): a real drag forward and up keeps the feet and hands in place, straightens the knees and elbows and leans
  the torso back.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/350, 2026-10-29): passes with the same results: the
  hips land on the aim (0.0 mm), feet and hands hold (0.0 mm, in game too), elbows straighten (47.7/54.1 to 29.8/23.1
  deg), the torso leans back (7.9 to 15.5 deg); hips, thighs, knees, feet, arms, clavicles and the back are keyed, at
  the playhead only; one Undo takes the drag back. Left: a real mouse drag (the test drives the gizmo's hip step).
- [x] 2 The layer's amount at 50 %: the hips still land under the gizmo and the feet and hands still hold.
  (XIV Bridge 351, 2026-10-28: within 0.0 mm in the evaluator and in game; the hips' key carries 2x the move.)
- [ ] 3 A standing dance frame, the hips alone selected: drag down 10 cm: the knees bend, the feet and
  hands hold. Untick Left hand and Right hand under Hold and drag again: the hands now ride with the body.
  Select the tail, then Ctrl+click the hips: a ticked Tail box joins the row and the tail holds; untick
  all boxes: "Nothing holds; the hips move alone." and the whole body moves with the hips.
  (Before the Hold boxes: XIV Bridge 352, 2026-10-28, Idle pose 3: knees 13/9 to 59/58 deg.)
- [x] 4 Drag the hips up past the legs' reach: the legs straighten smoothly and the feet lift, nothing
  snaps; back down, the feet are back on the floor under the gizmo.
  (XIV Bridge 353, 2026-10-28: 2 cm steps, no jump; back down the feet are where they were.)
- [x] 5 Select the body (n_hara): Drag is disabled with "The body moves the whole character. Select the
  hips to drag them."
  (XIV Bridge 354, 2026-10-28.)
- [x] 6 Undo once after a drag: the whole drag comes back in one step.
  (XIV Bridge 350, 2026-10-28: one undo step; the layer has no keys again and the body is back in game.)
- [x] 7 Save (replace) and Edit again: the clip plays the posed frame the same, and the keys are back.
  (XIV Bridge 355, 2026-10-28, into a test mod: the saved file within 5 mm of the layers; the keys come back.)

## Head drag (pending)

Drag mode on the head (j_kao) moves it with the body following: the back leans whole about its lowest
joint (j_sebo_a) toward the gizmo, the hips move by what the lean can't cover, and the head keeps
facing the way it did. The bones ticked under Hold (both feet at first; the hands are offered unticked,
plus any other selected bone that can hold) stay where they are; the rest rides with the body. On a
layer that only turns bones the head drags as before (the neck bends). Decided 2026-10-30 (user): feet
only by default, the back leans at its length with the hips taking the rest, the head keeps its facing;
the back stays straight (an even bend shortens it and lifts planted feet). Changed 2026-10-31 (user):
Hold boxes replace choosing the held bones by selecting them and Ctrl+clicking the head.

- [x] 1 A standing dance frame, an offset layer, select the head, Gizmo: Drag. In GPose with Live on and
  paused, drag the head forward and down along an arc: the torso leans over from the waist, the feet
  stay put, the head keeps looking ahead. The layer keys the head, j_sebo_a (turn and position), the
  hips (position) and the legs.
  Not passing yet (XIV Bridge animations-pending/520, Release 2026-10-31 19:42, Idle pose 3): the head lands on the aim,
  the back leans and Undo is one step, but with the arc turned 25 cm below the hips (so the hips take some of it) a foot
  moves 5 cm. Test or solver: open. The real mouse drag on the gizmo didn't start it; by hand.
  Rerun (XIV Bridge animations-pending/520, Release 2026-10-31 22:25): a 40 deg arc about j_sebo_a that sinks 6 cm leans
  the back about j_sebo_a, the head lands on the aim keeping its facing, both feet hold, and the layer keys the head,
  j_sebo_a, the hips' position and the legs. (The earlier fail was the test's arc, which put the aim beyond the back's
  reach so the feet lifted, as item 3 means them to.)
- [x] 2 Drag the head straight down 20 cm: the character crouches, knees bending, back upright, feet
  planted. Back up: it stands again.
  (XIV Bridge animations-pending/521, Release 2026-10-31: 20 cm down crouches with both knees bending more and forward,
  the back upright and the feet planted, in the evaluator and in game; a second drag back up stands it within 5 mm at the
  head and 1 cm at the hips.)
- [x] 3 Drag the head straight up past the legs' reach: the legs straighten smoothly and the feet lift,
  nothing snaps; back down, the feet are back on the floor.
  (XIV Bridge animations-pending/522, Release 2026-10-31: the legs straighten without a jump before the feet lift, and
  back down in the same drag the feet are on the floor again. As two separate drags the feet stay where the first left
  them: a drag holds its anchors at the pose it began on, HeadSolver.cs:88.)
- [ ] 4 Select the head, Gizmo: Drag. Under Hold tick Left hand and untick both feet: drag the head
  forward: the hand holds, the arm and clavicle bend; the feet now ride with the body. Select the hips
  with Drag: their boxes are their own (all four ticked).
  (Before the Hold boxes: XIV Bridge animations-pending/523, Release 2026-10-31: a selected hand holds, the arm and
  clavicle bend and the feet ride with the body.)
- [x] 5 The layer's amount at 50 %: the head still lands under the gizmo and the feet still hold.
  (XIV Bridge animations-pending/524, Release 2026-10-31.)
- [x] 6 A pose layer with Position off: Drag on the head bends the neck as before, with the old tooltip.
  (XIV Bridge animations-pending/525, Release 2026-10-31: the neck bends as before, with the old tooltip.)
- [x] 7 Undo once after a drag: the whole drag comes back in one step.
  (XIV Bridge animations-pending/520, Release 2026-10-31: one Ctrl+Z takes the whole drag back.)

## Fix layer (pending)

Fix layers repair the motion: Match startup to loop lands a startup's end on the loop frame the game shows
then, after its cross-fade, Smooth jitter calms small shakes. Spec: docs/superpowers/specs/2026-10-24-fix-layer-design.md.

- [x] 1 Open an emote startup that leads into a loop, Add > Match startup to loop: a layer appears at the
  clip's end, 8 frames long. In GPose play the startup, then the loop: no pop at the hand-off.
  (XIV Bridge exec, 2026-10-24, Ground Sitting Idle 1 startup j_pose01_start, 40 frames, loop from Caked Up
  - Groundsit: layer at 32-40; the last frame lands on the loop's frame 0 within 0.06 deg, from 90 deg;
    earlier frames unchanged. Played in GPose as the game chains emotes, loop as base and startup as a
    one-shot: no pop, but the unedited startup doesn't pop either, see 2.)
- [x] 2 Step frame by frame through the hand-off: no frame is held twice. (The spec assumes the game shows
  the loop's frame 0 right after the startup's last; if a frame holds, report it.)
  Found 2026-10-24 (XIV Bridge exec, Havok control times and weights per frame): no frame holds, but the
  assumption is wrong. The game starts the loop at its frame 0 before the startup ends and cross-fades:
  on j_pose01_start the loop starts at startup frame ~20 and fades in over 20 frames; on Red Ranger Pose A
  (177 frames) 10 frames before the end, over 10 frames. The startup's weight reaches 0 at its last frame,
  when the loop is already at frame 10-20. So Match aims at the wrong loop frame for a loop that moves at
  its start, and its 8 frames sit where the startup weighs 40% or less (in-game curves with and without
  the layer were nearly the same). Decide whether Match should target the loop frame shown at the
  startup's end and blend over the game's fade.
  Changed 2026-10-24: the user chose to target that frame. Played for real (out of GPose, mods
  on and off) the fade is 10 frames on Red Ranger Pose A and 20 on Ground sit pose 1; no file or sheet holds
  it, so the listener measures it (LoopFades.json) and Match has a Game fade slider. Retest: items 9 and 10.
  Not checked (XIV Bridge tests/instant-edit/animations-pending/340, 2026-10-29, skipped): playing the start row never
  handed off to the loop's timeline, the game doesn't chain it here, so the hand-off in game wasn't recorded.
  (XIV Bridge animations-pending/470, Release 2026-10-29: Red Ranger Pose A's startup with a 20 deg arm offset and
  Match, saved into a new mod and played for real on the player out of GPose, where the game chains startup to loop:
  frame by frame no frame is held twice at the hand-off and the arm doesn't pop, against the game's own startup.)
- [x] 3 Set Blend over to 20: the change spreads over more frames. (XIV Bridge, 2026-10-24: slider set
  to 20, the bar spans 20-40; the fastest step drops from 10.4 to 4.2 deg per half frame, still lands.)
- [x] 4 Set Blend to 50%: the end lands halfway to the loop's first frame. (XIV Bridge, 2026-10-24:
  every bone ends at half its gap, 90 to 45 deg, 89.5 to 44.77.)
- [x] 5 Edit the loop's start, save it, then Re-read loop on the startup's match: the startup follows the
  new loop start. (XIV Bridge, 2026-10-24, run the other way round: loop saved to a new mod with a 30 deg
  left forearm offset, Match added on the startup read it (30 deg off the original); the loop save undone
  from Edits, then Re-read loop: 0 deg off the original. One undo brings the read back.)
- [x] 6 Open a loop clip: Match startup to loop is disabled, and its tooltip says "Only a startup that
  leads into a loop". (XIV Bridge, 2026-10-24: greyed in the Add menu on j_pose01_loop. Tooltip not seen,
  the bridge can't hover while the real mouse is elsewhere; the code passes TimelineWindowViews.NoLoop.)
- [ ] 7 Add > Smooth jitter on a shaky port: the shakes calm and a sharp move stays sharp. At Strength 0
  nothing changes.
  Partly checked 2026-10-24 (XIV Bridge exec, decoded clip with the layer applied): on the Caked Up -
  Groundsit loop (nearly still) the summed second differences drop to 26%; at Strength 0 nothing changes
  (1e-5 deg). On Red Ranger Pose A's startup the 85-110 deg finger snaps stay within 1 deg. Found: on fast
  clean moves (hand whirls of 20-60 deg per half frame) Smooth leaves every other sample alone and moves
  the ones between by up to 4.3 deg, so even steps (35.3, 35.3) become 37.8, 32.8: a new half-frame
  ripple, 5 times the clip's own second differences where it was smooth. Not checked: a real shaky port,
  and the result by eye in GPose.
  Fixed 2026-10-24: the shake test and its share are taken over neighbours, so a key and
  the samples beside it are treated alike (fixture: a fast speed-up keeps growing steps, the old code
  failed it). Retest on Red Ranger Pose A's startup: its hand whirls change by well under a degree and
  their half steps stay even.
  Retested 2026-10-24 (XIV Bridge exec, Release build): the hand whirl is untouched (35.28/35.28,
  56.71/56.71), but the neighbour fix alone still left ripples where keys don't sit on even samples
  (n_kataarmor_l: 4 ripples in the clip became 22). Fixed: a track is smoothed on its keys
  (every sample but those halfway between their neighbours) and the samples between go back on the line.
  Now 3 ripples against the clip's own 4, roughness of smooth stretches 1.4x (was 5x); the 3 left are
  skirt and tail bones with real 60 Hz wobble. Fixture: a half-rate swing keeps its samples between keys
  halfway (the committed code failed it). Still not checked: a real shaky port, and by eye.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/345, 2026-10-29): on a made shaky port (Idle pose 5's
  loop with a +-1.5 deg shake on the right forearm and one sharp 25 deg move) the shake's noise drops to 0.02 of before,
  the sharp move stays at 27.9 of 27.9 deg, the idle's own motion moves 0.25 deg at most, and Strength 0 changes
  nothing; in Live the noise is 0.0013 against 0.2691 at Strength 0. Left: a real shaky port, and the result by eye
  in GPose.
- [x] 8 Open an idle's startup (such as Idle pose 5 start) with Live on: Live shows the startup and its
  layers, not the loop. Then, before the loop's skeleton has matched (straight after a reload), Add >
  Match startup to loop: it adds the layer instead of "Couldn't read the loop". The Pose source "Loop's
  first frame, at the end" reads the loop the same way.
  (XIV Bridge tests/instant-edit/animations-pending/430, 2026-10-29: Live on the startup and Match startup to loop
  pass, and "Loop's first frame, at the end" reads the loop and keys at the clip's end. The earlier fail was the
  script's: it picked the source in the frame it selected the pose layer, and the panel drops a pose picked on
  another layer.)
- [x] 9 With the Timeline window open, play /rangerpose1r out of GPose until it loops, then open its
  startup and Add > Match startup to loop: Game fade reads 10 frames, "Game fade measured in game." shows,
  and the blend is 10 frames. Do the same for Ground sit pose 1 (/groundsit, then /changepose): 20 frames.
  An emote never played says "Game fade not measured yet, so 10 frames."
  (XIV Bridge, 2026-10-24, Release: both measured, 10 and 20, after two fixes: the watcher
  first read 9 and 18, seeing the loop's control a frame or two late, and now takes the loop's own time
  off; and Match couldn't find the measure because these startups took their loop's timeline id, as
  FindStartup skipped start rows the sheet marks as loops. Ranger lands on the loop's frame 10 exactly,
  Ground sit on frame 20 within 0.06 deg (from 90). Red Ranger Pose B, loaded in GPose: the hint shows.)
- [x] 10 Set Game fade to 20 on Red Ranger Pose A's match: the loop is read again (one undo step), the
  blend grows to 20 and the startup's last frame lands on the loop's frame 20. Play it in game with
  Preview: the hand-off shows no pop, and the end of the fade matches the loop.
  Partly checked 2026-10-24 (XIV Bridge): the slider re-read the loop (21, then 20), the blend grew from
  10 to 21, the last frame lands on the loop's frame 20 within 0.06 deg, one undo goes back one read,
  and "Measured in game: 10 frames." offers Use it. Not checked: the Preview hand-off. Preview plays the
  startup on its own, and a script couldn't chain it into the loop with Brio open on the actor.
  (XIV Bridge animations-pending/471, Release 2026-10-29: played for real instead of in a Preview, which plays the
  startup alone: saved with Game fade 20 and played on the player out of GPose, the body's per-frame turn stays within
  its typical frame through the game's cross-fade and at the hand-off, and once the startup is gone the pose carries
  on as the loop.)

## Close loop (pending)

Close loop is a fix layer that blends a looping clip's end into its start after every other layer, so an edit that
doesn't wrap doesn't snap at the seam. Add > Close loop, only on a clip the game loops. Place is Before (the
last frames change), Centered (the last and first frames) or After (the first frames). Spec:
docs/superpowers/specs/2026-10-27-close-loop-fix-design.md.

- [x] 1 Open a dance loop and add an Offset layer that starts mid-clip and runs to the end, so the loop snaps at
  the seam. Add > Close loop: Live and the played loop are smooth, and stay smooth after a save to a new mod.
  Fix loop on the seam panel does the same when the layers make it jump.
  Partly checked (XIV Bridge 360, 2026-10-28, Idle pose 3 with a 25 deg right upper arm offset from mid-clip): Add >
  (Fix loop was removed 2026-10-30: Add > Close loop is the way now.)
  Close loop closes the seam (worst 25 to 0.04 deg; in Live the arm's largest step 25.0 to 2.5 deg, the saved file
  played from a new mod 2.5 deg). **Fix loop doesn't offer it**: the badge and the seam panel say Clean, since
  LoopSeam pops only on a mean gap of 1 deg (PopDegrees) over all tracks, and one bone's 25 deg is a 0.26 deg mean.
  Decide whether the worst bone should count.
  Changed 2026-10-28 (the user chose it): one bone 10 deg off or more pops too (LoopSeam.WorstPopDegrees). The game's own
  loops stay under it (XIV Bridge survey, 31 loops: worst 6.65 deg, Eastern Dance's skirt; most under 3).
  (XIV Bridge 360, 2026-10-28, 57 fps: the panel says Pops, Fix loop adds a selected Close loop in one undo step and the
  seam is Clean; in Live the arm's largest step 25.0 to 3.1 deg, played from a new mod 2.7. At 11 fps the blend's steps
  read 11 deg, so the test now compares against the snap, not the plain clip.)
- [x] 2 Switch Place between Before, Centered and After: Before changes only the frames before the seam, After
  only the frames after it, Centered both, as the bar shows (Centered also draws a thin piece at frame 0). Drag the
  bar's inner edge, or set Blend over: the blend gets longer or shorter. Centered's inner edge follows the pointer
  and snaps. The edge on the clip's end doesn't move and shows no resize cursor.
  (XIV Bridge 361, 2026-10-28: Before 63-70, Centered 0-3 and 67-70, After 0-7; Blend over 16 and the edge drags set
  the length. The cursor and the snap: by hand.) In game 2026-10-29 (by hand, Centered at 62-70): the inner edge
  follows and snaps, but the edge on the clip's end shows no resize cursor (it doesn't move, as meant). Unticked until
  that's decided. Ruled 2026-10-29: a fixed edge shows no resize cursor; the item is reworded to match.
- [x] 3 Pin both feet near the seam (Pin feet) and set Blend over to 8: the planted feet don't slide visibly at the
  seam. The layer panel's hint warns that it may move pinned bones.
  (XIV Bridge 362, 2026-10-28: the feet travel 0.0 mm more over the seam; in Live at most 2.6 mm between frames.)
- [x] 4 Open [yllisne] Angel Dust - smoking animation pack's idle (common/9, j_pose02_loop), whose first frame alone
  turns the legs up to 6 deg. Add > Close loop at each Place, then Smooth jitter alone: the legs no longer twitch at
  the seam, and the tail still closes. (Offline on the decoded clip, 2026-10-30: the legs' largest seam step 6.15 to
  0.04 deg with either fix, where Close loop used to swing them 14.6 deg; no other bone changed.)
  (XIV Bridge animations-pending/363, Release 2026-10-30, the pack's own common/9 file as Ground sit pose 2 in a test mod,
  each bone's seam step over 1.5x its typical step: the legs 6.15 deg plain; Close loop Before 0.15, Centered 0.08,
  After 0.00, Smooth jitter alone 0.00; no other bone's seam worse; the tail's seam, 10.6 deg plain, closes to 1.3, 0.8
  and 1.0 with Close loop (Smooth jitter leaves it, 10.7). In Live the twitching leg's largest step is 2.9 deg either way
  at 60 fps. The legs at the seam by eye: left.) Note: the pack itself now maps the game path to an in-place save
  (common/9/j_pose02_loop.iefcc3fe77.pap) whose legs are already 0.00 deg at the seam.

## Animations from scratch (pending)

Start from a still pose, stand it on the ground, and borrow breathing from an idle (Pose2Idle's idea).
Plan: C:\Users\Example\.claude\plans\i-want-to-expand-smooth-allen.md. Test clip: a standing emote, such as
/stretch, and your race's Standing idle.

- [x] 1 Load a standing emote, move the playhead to a calm frame, Add > Still pose (start from scratch).
  The line under the toolbar says "Still pose: frame N held for ... frames", a Pose layer over the whole
  clip is selected, and Live shows the character holding that frame.
  (XIV Bridge 410, 2026-10-28, Stretch: "Still pose: frame 10 held for 147 frames."; Live holds to 0.00 deg once it
  shows the layers. For its first second or two Live still plays the emote's own motion while it builds.)
- [x] 2 In that Pose layer, Key a pose > Pose file, a crossed-leg or kneeling pose from D:\Mods\Poses.
  "Feet on the ground" is ticked. Key at frame 0: the character takes the pose with the feet on the
  floor, not floating. Untick it and key again: the pose floats as before.
  (XIV Bridge 410, 2026-10-28, Multi\Occupied\Kneel.pose: the lowest foot 7.1 cm against the clip's floor 8.4 cm;
  unticked 49.6 cm.)
- [x] 3 With Live on and paused at frame 45, turn the head with the gizmo: a key appears at 45 and the
  head moves between frame 0 and 45 when played. With the playhead outside the layer, the Bones panel
  says "Move the playhead into the layer to pose it with the gizmo."
  (XIV Bridge 411, 2026-10-28, after a key at 0 as item 2 leaves: 10.4, 29.6 and 40.0 deg at 15, 30 and 45. Without a
  key at 0 the lone key at 45 holds over the whole layer.)
- [x] 4 Play your race's Standing idle once (Load), open the emote again, Add > Idle motion layer, Motion
  from: the idle. The chest breathes and the body shifts its weight; Breathing 0% stops the chest.
  Press Pin feet: the feet stay planted while the body shifts.
  (XIV Bridge 412, 2026-10-28, Standing idle from the game: the chest swings 1.83 deg, the hips 5.5 mm; Breathing
  0% 0.00 deg; Pin feet 0.0 mm.)
- [x] 5 Change the length to twice as long: the breath still loops without a jump at the end.
  (XIV Bridge 412, 2026-10-28: 294 frames, seam Clean, worst 0.000 deg.)
- [x] 6 Save to a new mod: the emote plays the new animation in game, breathing included.
  (XIV Bridge 413, 2026-10-28: the saved file within 3.0 mm and 0.29 deg of the layers; in game the chest wanders 0.98 deg.)
- [x] 7 Use the clip's motion: the emote's own motion comes back; Ctrl+Z brings the still pose back.
  (XIV Bridge 413, 2026-10-28.)
- [x] 8 On a Miqo'te or Au Ra, raise Tail to 200%: the tail swings further.
  (XIV Bridge 412, 2026-10-28: n_sippo_a 1.49 to 2.98 deg.)

## Onion skins (pending)

The Bones window's onion skins switch, beside the crosshairs, draws faint bones on the GPose character for
the pose at the selected layer's key before the playhead (blue) and after it (orange).

- [x] 1 Select an offset or pose layer with three keys, put the playhead between the first two and turn on
  the switch: blue bones show the first key's pose, orange ones the second's, under the normal dots.
  (XIV Bridge 380, 2026-10-28: the ghosts are the keys' poses to 0.000 deg; the overlay drew 92 ghost dots. Colours not read.)
  In game 2026-10-29 (by eye, neck keys at 10, 30 and 50, playhead at 20): blue is the first key's pose, orange the
  second's. The user finds the ghosts more confusing than helpful as they are: rethink how they're drawn.
- [x] 2 Put the playhead on the middle key: the ghosts are the first and the last key. Before the first key
  only orange shows; on or past the last only blue.
  (XIV Bridge 380, 2026-10-28.)
- [x] 3 Scrub across a key: the ghosts jump to the next pair of keys without a hitch.
  (XIV Bridge 380, 2026-10-28: 29 to 31 moves them from (10, 30) to (30, 50); 0.09 ms at most.)
- [x] 4 Turn a bone with the gizmo at the playhead's key: the ghosts stay put; turn one at a ghost's key
  (playhead there, then step off it): that ghost shows the new pose.
  (XIV Bridge 381, 2026-10-28.)
- [x] 5 Crosshairs on Selected: ghosts show only for the selected bones. Crosshairs Off: no ghosts.
  (XIV Bridge 381, 2026-10-28.)
- [x] 6 Select an amplitude or fix layer, or no layer: no ghosts. The switch's tooltip says amplitude and
  fix layers and planted pins are left out of the ghosts.
  Partly checked (XIV Bridge 381, 2026-10-29): no ghosts on an amplitude layer, a fix layer or no layer. The tooltip: by hand.
  In game 2026-10-29 (by eye): the switch's tooltip says amplitude and fix layers and planted pins are left out.
- [x] 7 The ghosts sit on the character as the normal dots do at those frames (step the playhead to a
  ghost's key: the dots land on it), also on a racially scaled character.
  Partly checked (XIV Bridge 382, 2026-10-29): within 0.0 mm of the bones in game (4 bones). A racially scaled character: by hand.
  In game 2026-10-29 (by eye, a Hellsguard Roegadyn at 207 cm through Glamourer): stepping to the keys at 10 and 30, the
  dots land on the ghosts.
- [x] 8 Close and reopen the Bones window: the switch keeps its state until the plugin reloads.
  (XIV Bridge 381, 2026-10-28: kept on reopening; a reload not tried.)

## Pose copy and paste (pending)

The Bones panel's Copy pose, Paste pose and Paste mirrored buttons, next to Mirror. The maths is covered
offline in PoseEditorFixture (Clipboard).

- [x] 1 Open a dance in the Timeline, add a pose layer, select the left arm group and pause on a frame. Copy
  pose: the status line says how many bones were copied.
  (XIV Bridge 370, 2026-10-28, Idle pose 3.)
- [x] 2 Move the playhead 20 frames on, Paste pose: one key holds the arm as it was copied. Undo once: the
  whole paste goes.
  (XIV Bridge 370, 2026-10-28: the pasted arm 0.0 mm from the copy in the chest's frame.)
- [x] 3 Paste mirrored at another frame: the right arm takes the mirrored pose, the left arm is untouched.
  With both arms selected when copying, they swap sides.
  Found 2026-10-28 (XIV Bridge 370, Idle pose 3): the left arm stays put, but the right arm lands 28.0 mm from the
  mirrored copy in the chest's frame, and both arms swapped 26.8 mm (the test allows 2 cm). The neck mirrors exactly
  (371). Cause (probe, 2026-10-28): PoseEdits.Paste mirrors the copy's change from the motion under the layer at the
  paste frame (as Mirror does), so the right arm becomes its own motion there plus the mirrored change; that guess
  matches the paste within 0.5 mm. Idle pose 3 is 28.1 mm (elbow) and 25.5 mm (hand) off symmetric at frame 50, which
  is the whole gap; the mirror plane and the rest skeleton are exact.
  Changed 2026-10-28 (the user chose it): on a Pose layer Paste mirrored keys the copied pose's mirror image, its change
  from the skeleton at rest mirrored onto the twin at rest, whatever the motion does at the paste frame; offsets and Mirror
  are unchanged. Checked in game with the new formula on the loaded build's statics: the right arm 0.0 mm from the
  mirrored copy, both arms swapped 0.0 mm. Retest: 370 once the new build is loaded (its swap check now reads the copy as
  it is after the first paste).
  (XIV Bridge 370, 2026-10-29, with the new build: Paste mirrored keys the right arm only, 0.0 mm from the mirrored
  copy; the left arm stays untouched; with both arms copied (8 bones) they swap sides within 0.0 mm.)
- [x] 4 On an offset layer, turn the neck to one side, Copy pose, then Paste mirrored at another frame: the
  neck turns to the other side.
  (XIV Bridge 371, 2026-10-28: 30.0 deg each way, the axis mirrored.)
- [x] 5 Open another animation, add a pose layer, Paste pose: the copy carries over. Paste into an offset
  layer: the buttons are disabled, and the tooltip says the copy is from a pose layer.
  (XIV Bridge 372, 2026-10-28: Idle pose 3 to Idle pose 5, through the "Open ... and lose them?" Discard. The tooltip
  is the rule's string, not read off the screen.)
- [x] 6 Copy bones from a skeleton with tail or IVCS bones, paste on one without them: the status line says
  how many bones were skipped, the rest are keyed.
  (XIV Bridge 440, 2026-10-29: 172 bones copied on an NFLB skeleton, pasted on the game's: "Skipped 67 bones this
  skeleton lacks.", and the rest are keyed.)
- [x] 7 Move the playhead outside the layer: both paste buttons are disabled with "Move the playhead into the
  layer."
  (XIV Bridge 372, 2026-10-28.)

## Picking several keys (pending)

Pick keys across a layer's rows and move, spread, delete, copy or paste them together. The edits are covered
offline in MotionEditsFixture (KeyPicks); these checks are the canvas, the panel and the feel.

- [x] 1 On a pose layer with four keys and its bone rows shown, click a key, Ctrl+click two more: all three get
  a ring, the panel's Keys row highlights them. Ctrl+click one again: it drops out.
  (XIV Bridge 390, 2026-10-28, items 1-3: clicks as TimelineEditor.Press/Drag/Release; the rings' look not read.)
  In game 2026-10-29 (by hand, keys at 10, 25, 40 and 55): each picked key shows a ring, and a second Ctrl+click drops it.
- [x] 2 Drag one of the picked keys: all of them move together and stop at a key that isn't picked. One
  Ctrl+Z puts them back, and Escape during the drag too. Dragging a bone's value in a picked key moves the
  group. A plain click on a picked key without dragging leaves it picked alone.
- [x] 3 Click a key, Shift+click a later one on the layer's row: every key in between is picked. On a bone's
  row, Shift+click picks only that bone's values.
- [x] 4 Press on an empty bone row and drag a box over keys on two bone rows: those bones' values are
  picked (faint rings on the layer's row). Drag it up over the layer's row: the whole keys are picked.
  Ctrl+drag on the bar adds a box to the picks; Escape during that box goes back to the picks before it.
  The box never reaches into the next layer's rows.
  (XIV Bridge 391, 2026-10-28, items 4-5.)
- [x] 5 Alt+Right nudges every picked key a frame; Delete takes them all out; the menu says
  "Delete N picked keys".
- [x] 6 With keys picked on 3 frames, the panel shows "3 picked · Spread over N frames". Type half: they
  play twice as fast in Preview, the first key stays put, and the field edit is one undo step. A number
  that would put two keys on one frame shows a reason in the toolbar and changes nothing.
  (XIV Bridge 392, 2026-10-28: "3 picked · Spread over 20"; half keeps the first key, one undo step.)
- [x] 7 Ctrl+C, move the playhead, Ctrl+V: the keys are pasted from the playhead on and picked. Paste into
  another animation's pose layer: they carry over. Into an offset layer: the toolbar says they came from a
  Pose layer. After copying an event, Ctrl+V pastes the event again, not the keys.
  Partly checked (XIV Bridge 392, 2026-10-29): all but the event, which was skipped: Idle pose 5 has no editable event.
  (XIV Bridge 392, Release 2026-10-30, with a sound added at 12 to copy: copying it drops the copied keys and Ctrl+V pastes
  the event again, not the keys.)
- [x] 8 On a pin layer with a keyed pin, pick two of its keys and drag them: the pins move in time.
  (XIV Bridge 393, 2026-10-28.)

## Hand shapes (pending)

A Fix layer that sets a hand's fingers to a shape. The maths is covered offline in HandShapeFixture; these
checks are the look in game. The built-in angles are first guesses: item 1 tunes them.
Known limit: a Symmetry layer after a hand layer skips finger pairs the clip doesn't animate; set the hand layer to Both instead.

- [x] 1 Each built-in shape on a vanilla hand (Relaxed, Flat, Fist, Point, Grip, Cupped, Spread) looks like its
  name; fingertips stay straight.
  (In game 2026-10-29, the right hand on Wave with Live: each shape looks like its name and the tips stay straight.)
- [x] 2 The same on a YAS character with a YAS-weighted hand mesh: tips bend, no tip line.
  (XIV Bridge animations-pending/482, Release 2026-10-29: NFLB + YAS skeleton, a test copy of Galánta nails (weighted to
  the IVCS tip bones) mapped over the worn hands model: the tip check finds tip weights, no fingertip line, Tips stay
  on, each built-in shape bends the tips as its recipe says, and in Live the Fist's tips bend. Looks: by eye.)
- [x] 3 Gloves without tip weights on a YAS skeleton: the line names the model, tips stay straight.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/448, 2026-10-29): with vanilla hands on IVCS + YAS
  the line reads "Fingertips: your hands' model isn't weighted to tip bones.", the layer's Tips turn off and the tips
  stay at rest in Live. The line doesn't name the model (HandTipCheck.cs:48).
  Fixed 2026-10-29: "Fingertips: your hands' model (c0801e0000_glv.mdl) isn't weighted to tip bones." Retest: XIV
  Bridge tools/diagnostics/bugfix-hand-tip-names.csx (needs the YAS+NFLB skeleton copy).
  (XIV Bridge tools/diagnostics/bugfix-hand-tip-names, Release 2026-10-29: on IVCS + YAS with vanilla hands the line
  names the hand model; on the game's skeleton it names the clip's skeleton.)
- [x] 4 A clip on the vanilla skeleton played on a YAS character: the line names the clip's skeleton.
  Partly checked (XIV Bridge 448, 2026-10-29): the line says the clip's skeleton has no tip bones, without naming it
  (HandTipCheck.cs:46).
  Fixed 2026-10-29: "Fingertips: the clip's skeleton (skl_c0801b0001.sklb from the game) has no tip bones. Repair it
  onto IVCS or YAS to bend them." Retest as item 3.
- [x] 5 Fist to Flat with a 10-frame fade opens smoothly in Preview and Live.
  (XIV Bridge tests/instant-edit/animations-pending/449, 2026-10-29: frame by frame the fingers open on the fade's
  smoothstep, never closing again or jumping, in the document, Live and Preview, paused and playing. In game
  2026-10-29, by eye on Wave: it opens smoothly in Live and in Preview.)
- [x] 6 Curl and Spread at -100% and +100% stay natural (no backward bends).
  (XIV Bridge tests/instant-edit/animations-pending/450, 2026-10-29: on all 7 shapes every finger joint stays between
  -5 and 110 deg, in the document and in Live. How it looks: by eye.) In game 2026-10-29: Curl at ±100% looks
  natural, but Spread below about -30% has the index finger clip through the middle finger, so it needs a cap.
  Spread now stops at -30%; a layer saved lower applies as -30%. (XIV Bridge
  tests/instant-edit/animations-pending/456, 2026-10-29: -50% and -100% pose the fingers as -30% does, in the
  document and in Live, and the slider of a layer saved at -50% shows -30%.) Pending in game: -30% looks natural.
  (XIV Bridge visual/450j, Release 2026-10-29, judged by Claude from close crops of the right hand on all 7 shapes:
  no finger bends backward; Curl -100 opens them straight, Spread -30 no longer clips. At Spread +100 on the curled
  shapes (Fist, Point) the bent fingers fan out and their nails overlap a little.)
- [x] 7 Keep finger motion keeps an emote's finger wiggle.
  (XIV Bridge tests/instant-edit/animations-pending/441, 2026-10-29: without it the shape holds the fingers (0 of the
  clip's travel); with it the wiggle comes through (99.97 % of the clip's travel). In game 2026-10-29 on Beckon with a
  Relaxed layer: the beckoning shows with Keep finger motion, and the fingers hold still without it.)
- [x] 8 Save hand as shape from the left hand, use it on the right: mirrored correctly; delete it, the layer
  keeps its look.
  (XIV Bridge tests/instant-edit/animations-pending/441, 2026-10-29: the saved left fist on the right hand is the
  right fist within 0.04 deg; after the shape is deleted the layer keeps its look.)
- [x] 9 IK drag on a clip with a Replace hand layer shows the shape while dragging.
  (XIV Bridge tests/instant-edit/animations-pending/451, 2026-10-29: at every step of a drag the gizmo's pose and Live
  keep the fist, also with the hand layer after the offset layer; the drag is one undo step. A real mouse drag didn't
  start the gizmo in the test. In game 2026-10-29, a real mouse drag on Beckon with a Replace Fist layer: the hand
  keeps the fist while dragging.)

## Pose browser (pending)

Pose browser beside Key a pose opens a window of your pose folders, browsed like Brio's library. A click on a
pose keys it in the selected pose layer at the playhead; the next click there replaces it in the same undo step.
Test folder: D:\Mods\Poses (Solo, and a pack folder with a picture such as Multi\[Chaele] Altitude).

- [x] 1 Add folder: D:\Mods\Poses. The breadcrumb reads Library > Poses; folders come first, then poses,
  in name order. A pack folder shows its picture; its poses show it too, a pose with its own image or a Brio
  picture inside the file shows that. Scrolling a folder of 100+ poses stays smooth.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/439, 2026-10-29, on a temp library of copies): the
  added folder shows as Poses; folders come first, then poses, in name order; the pack folder shows its picture and so
  do its poses. In game 2026-10-29 on D:\Mods\Poses: Search this folder lists 100+ poses and scrolls smoothly,
  and a pose with its own image shows it.
- [x] 2 With a Pose layer selected and the playhead inside it, click a pose: the character takes it in Live,
  the tile is outlined, the footer says how many bones were keyed. The window stays open.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/438, 2026-10-29: a click keys 65 bones at the playhead as one undo
  step, the footer says "keyed 65 bones", the window stays open and Live takes the pose within 0.04 deg. The tile's
  outline was not read.) In game 2026-10-29: the clicked tile is outlined and Live shows the pose.
- [x] 3 Click another pose: it replaces the first, nothing of the first is left (try one with hair keyed via
  Also > Hair, then one without). One Ctrl+Z goes back to before the first click.
  (XIV Bridge tests/instant-edit/animations-pending/438, 2026-10-29: the second click is the same undo step and keys
  exactly what that pose alone keys; one Ctrl+Z leaves no key.)
- [x] 4 Move the playhead, click a pose: a second key, the first stays. Edit a bone, click again: the edit stays.
  (XIV Bridge tests/instant-edit/animations-pending/438, 2026-10-29: at another frame a click makes a second key and
  the first stays; after a bone edit a click is a new undo step, and undoing it brings the edit back as it was.)
- [x] 5 Untick Feet on the ground or Move bones in the footer: the shown pose is keyed again with the change.
  (XIV Bridge tests/instant-edit/animations-pending/452, 2026-10-29: each footer change keys the shown pose again in the
  click's undo step, matching a fresh click with that choice, and Live shows it.) In game 2026-10-29: unticking either
  visibly changes the character.
- [x] 6 Search this folder finds poses in subfolders; the tooltip names their folder. Right-click a library
  folder > Remove from the list. Close and reopen the plugin: the folder list and the last folder come back.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/439, 2026-10-29): Search finds poses in subfolders
  and the tooltip names their folder; Remove from the list takes the folder off the library; the last folder and the
  sort are kept in the settings, and a reopened browser shows the last folder again. Not checked: closing and
  reopening the plugin itself (by hand).
  (XIV Bridge animations-pending/463, Release 2026-10-29: after a real reload the folder list, last folder, sort and
  picture size are what the reloaded plugin has, and its pose browser uses them.)
- [x] 7 No pose layer selected, or the playhead outside it: the footer says why and pose tiles are dimmed.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/438, 2026-10-29): with no pose layer, an offset
  layer, or the playhead outside the layer, the footer says why and a click keys nothing. In game 2026-10-29: the
  footer says why and the tiles are dimmed.
- [x] 8 Drag the picture size slider (bottom right) to the largest: pictures stay sharp, more columns as it
  shrinks. Switch By name / Newest first: a pack you added last comes first, folders stay before poses. Both
  choices come back after reopening the plugin.
  Partly checked (XIV Bridge tests/instant-edit/animations-pending/439, 2026-10-29): Newest first puts the pack added
  last first with folders before poses, and the choices are saved in the settings. In game 2026-10-29: pictures stay
  sharp at the largest size and more columns appear as they shrink. Not checked: the choices after reopening the
  plugin (by hand).
  (XIV Bridge animations-pending/463, Release 2026-10-29: the sort and the picture size come back after a real reload.)
- [x] 9 Right-click a pose > Move to the Recycle Bin: the tile goes, the footer says so, and the file is in the
  Recycle Bin (restore it from there). The game doesn't hitch.
  (XIV Bridge tests/instant-edit/animations-pending/439, 2026-10-29: the tile goes, the footer says "... is in the
  Recycle Bin.", the file is gone from the folder and in the bin; the worst frame is 38 ms, against 39 ms idle.)

## Idle motion from the game (pending)

The idle-motion layer's Motion from list adds, under "From the game", the character's race's Standing idle and
Idle pose 1-6 from the game's own files (the copy the race loads, another race's when it borrows one), beside the
animations seen playing. Offline check 2026-10-28: all 18 races list Standing idle and poses 1-6; idle.pap's
first entry is a damage flinch (cbna_add_dmg_f), so the idle (cbnm_id0) is read by name.

- [x] 1 Fresh plugin load, open a dance, add an idle-motion layer: Motion from lists Standing idle and Idle pose
  1-6 without playing any of them. Pick Idle pose 3: the layer reads it and Live shows its breathing.
  Partly checked (XIV Bridge 400, 2026-10-29, not a fresh load: the listener had heard poses 3, 5, 6 and the idle):
  the list is right, the layer reads Idle pose 3 (27 bones), in Live the chest wanders 0.60 deg. Idle pose 3's own
  chest swings 0.46 deg, and the layer applies its spine share (0.55 by default), 0.26 deg, which is what the layer
  should do. Not checked: a fresh load, and how the breathing looks (by eye).
  (XIV Bridge animations-pending/464, Release 2026-10-29: on a freshly reloaded Instant Edit whose listener has heard
  none of the game's idles, Motion from lists Standing idle and Idle pose 1-6; Idle pose 3 fills the layer and Live
  shows its breathing.)
- [x] 2 Pick Standing idle: the motion is the calm idle, not a flinch. With an idle mod enabled the game's own
  idle is still the one read.
  (XIV Bridge 401, 2026-10-28: cbnm_id0, 2.67 s; with a copy of [Noff] Smoking Idle on, the same samples.)
- [ ] 3 On a Highlander or Hellsguard character (they borrow Standing idle and poses 5-6): those three read
  without an error.
  Partly checked (XIV Bridge 402, 2026-10-29): c0301, c0401, c1101 and c1201 listed and decoded by code, from a
  Miqo'te. Not on such a character.
- [ ] 4 Make startup instant on a modded loop: the held pose is the standing idle, not a flinch.
  Not checked (XIV Bridge 403, 2026-10-29, skipped again): the TikTok copy's loop has no startup ("This animation has no
  startup."). Needs a modded loop that has one.

## Motion key markers (pending)

The clip band's key poses and the Motion keys layer: a bone edit keys the bone and fades out over the motion's own
markers. The maths is covered offline (marker finder, key poses, fade keys); these checks are the canvas and the feel.
Code: Services/Animations/Motion (markers), Ui/Timeline/TimelineClipBand.cs, the layer panel's Detail and Reach.

- [ ] 1 Open a vanilla emote and click a key pose on the clip band (muted ticks along its bottom edge; the tooltip
  says "Frame N: n bones key here. Click to edit motion keys."): a "Motion keys" Offset layer appears first in the
  stack and is selected, and the playhead is on that frame. Right-click the band > Edit motion keys does the same.
  (XIV Bridge 420, 2026-10-28, Bow, key poses 0, 35, 59, 96, 107: the tooltip text, layer and playhead as written.
  The ticks' muted look: by hand.) In game 2026-10-29 (by eye, Bow): the ticks are so faint they're near impossible to
  see without looking closely; the user had never noticed them. Unticked until they're easier to find.
  Changed 2026-10-29: the ticks are taller (7 px, Label colour, full alpha) with a small triangle at the foot and
  brighten on hover; the Motion keys rows' rings are larger and Label-coloured; Add > Motion keys does what a tick
  click does (disabled with "Needs the motion's key poses. Wait until the motion is read." until they're in).
  Retest: the ticks and rings are easy to see on Bow, and Add > Motion keys.
- [x] 2 The Motion keys layer unfolds into rows for its keyed bones and the selected bones; each row shows that
  bone's markers as small hollow circles behind the keys. Click a marker: the playhead moves there and the bone is
  selected, and no key is made.
  (XIV Bridge 420, 2026-10-28. The circles' look: by hand.) In game 2026-10-29 (by eye, Bow, Motion keys at 35, only the
  selected neck's row as nothing is keyed yet): the row shows small hollow circles; they're tiny, like the ticks above.
- [x] 3 Turn a bone at a marker (rotation ball, fields or gizmo). With Live on, the change fades out at the next
  markers on both sides, with zero keys there. Key, Reset, Mirror and a pose import fade out the same way.
  Partly checked (XIV Bridge 421, 2026-10-28, Wave): a turn, Key, Reset and the gizmo fade out at the next markers.
  Mirror and a pose import not run.
  Rerun (XIV Bridge 421, 2026-10-29): same results; Mirror and a pose import still not run.
  In game 2026-10-29 (by hand, Bow's Motion keys layer, Live on): the left upper arm turned at a marker, then Mirror,
  and a pose import at another marker: each fades out at the next markers on both sides.
- [x] 4 Paste, nudge and delete add no zero keys.
  (XIV Bridge 421, 2026-10-28.)
- [x] 5 Reach 2 widens the change to the second markers; Reach 3 to the third. An existing closer key is kept.
  (XIV Bridge 421, 2026-10-28.)
- [x] 6 Mute the layer: the original motion is back. Rename and delete it like any layer.
  (XIV Bridge 422, 2026-10-28: muted, 0.000 mm and 0.08 deg from the clip, the evaluator's own rounding.)
- [x] 7 Detail Fewer / Normal / More changes the marker counts on the bone rows; the key poses on the clip band stay
  the same.
  Changed 2026-10-29 (item 9): the key poses now always come from the Fewer markers. Retest with XIV Bridge 422.
  (XIV Bridge 422, Release 2026-10-29 21:23: Wave, j_te_r 50/68/81 markers, 1117/2238/4216 in all, key poses 3/3/3.
  422's own check still expects the key poses to change: update it.)
  (XIV Bridge 422, 2026-10-28, Wave: j_te_r 50/68/81 markers, 1117/2238/4216 in all, key poses 3/2/3.)
- [x] 8 Save and reopen (Edit again): the Motion keys layer comes back with its keys, and Detail and Reach are kept.
  (XIV Bridge 422, 2026-10-28.)
- [x] 9 Record the marker and key-pose counts per Detail on about 10 vanilla emotes (short, long, looping), read with
  `xiv_exec` from the open clip (`_timelineWindow.keyMarkers` / `editor.Markers`). If the counts look wrong, tune the
  tolerances (Fewer 2 deg / 0.005 m / 2 %, Normal 0.5 deg / 0.001 m / 0.5 %, More 0.1 deg / 0.0002 m / 0.1 %).
  Recorded 2026-10-28 (XIV Bridge 423), key poses Fewer/Normal/More: Bow 8/5/2, Cheer 5/5/2, Clap 6/3/2, Joy 5/3/2,
  Kneel 6/4/2, Laugh 7/3/2, Pray 5/5/4, Salute 7/4/3, Idle pose 3 4/4/6, Ground sit 4/4/7. Markers per bone grow about
  2x per step. On emotes More finds the fewest key poses, and Wave at Normal has them only on its first and last frame.
  To decide: whether that is the wanted tuning.
  Rerun (XIV Bridge 423, 2026-10-29): the same counts on the same 10 clips.
  Found and fixed 2026-10-29: not a tuning question. At finer Detail the markers are so dense that nearly every frame
  is a key-pose candidate, the run of candidates merges into one, and a run touching frame 0 or the end keeps only
  the ends. The key poses now come from the Fewer markers at every Detail (fixture: a shaky swing keeps 0, 5, 23, 36,
  50, 60 at all three; the old code gave 0 and 60 at More). Retest with XIV Bridge 423: each emote's count should be
  the same at all three Details, equal to its Fewer count above.
  (XIV Bridge 423, Release 2026-10-29 21:23: the same at all three Details, each equal to its old Fewer count: Bow 8,
  Cheer 5, Clap 6, Joy 5, Kneel 6, Laugh 7, Pray 5, Salute 7, Idle pose 3 4, Ground sit 4. Markers still grow about 2x
  per step.)
- [x] 10 Hips drag on the Motion keys layer fades out (after the hip-drag work is committed and wrapped).
  (XIV Bridge 424, 2026-10-28, Idle pose 3: every keyed bone fades out at its markers. Where the next marker is the
  clip's end, the zero key at frame 0 closes it: the change eases to 0 at 70 and the seam stays Clean.)

## Swing and Smooth motion (pending)

Two keyless bone layers. Swing swings its bones back and forth a whole number of times over the layer, in the
character's axes: Tilt and Twist turn, Slide moves the top bone of each chain; lower bones trail by Delay, left and
right swing opposite, a chain shares the angle. Smooth motion averages each frame with the frames round it over its
Window, so sudden moves soften. The maths is covered offline (SwingLayerFixture, SmoothMotionFixture); these checks
are the look and Live. Code: Services/Animations/Motion/MotionSwing.cs, MotionSmoothing.cs, Ui/Timeline/SwingFields.cs.

- [x] 1 On a dance, Add > Swing, limit it to the hips (j_kosi), Slide 5 cm, Tilt 5°, then Add > Pin feet after it:
  in Live the hips sway side to side and the feet stay planted.
  Fails in Live (XIV Bridge animations-pending/540, Release 2026-10-31, Flame Dance): the document slides the hips 5 cm
  and leans them 5 deg with the feet planted, but Live moves the hips 0.07 cm and the feet 7.4 cm. Flame Dance is a c0101
  clip on the c0801 character, and for another race's clip Live puts every translation but n_hara's back at rest
  (LiveConversion.ToSource, LiveConversion.cs:50), so a Slide on j_kosi is dropped while Pin feet's leg turns, solved for
  the slid hips, stay. Suspected bug; decide whether the Slide goes onto n_hara for such clips or the editor warns.
  Fixed 2026-10-31 (ruled: onto n_hara): on a clip rebuilt for another race each layer's move of j_kosi goes onto n_hara
  in the document, Live and the save, so the back goes along with the hips (SwingLayerFixture). Retest after a Release build.
  Retest after the fix (XIV Bridge animations-pending/540, Release 2026-10-31 22:03): Live now slides the hips by the
  Slide, but the feet still move 2.6 cm under Pin feet. Open.
  Rerun (XIV Bridge animations-pending/540, Release 2026-10-31 22:25): Live slides the hips by the Slide and the feet are
  where the evaluator's Pin feet holds them (within 1 cm). Pin feet holds each foot at its planted stretch's average
  spot, which the swing moves, so the feet with the swing on and off differ by about 2.6 cm on Flame Dance by design
  (Instant Edit's PinLayerFixture.SwungHips).
- [x] 2 Add > Swing on both upper arms (j_ude_a_l, j_ude_a_r), Direction 90°, Tilt 30°, Twins opposite on: the arms
  swing forward and back in turn, like walking. Untick Twins opposite: they swing together.
  (XIV Bridge animations-pending/541, Release 2026-10-31: each arm peaks at 29-31 deg; Twins opposite on they move
  opposite (correlation under -0.95), off together (over 0.95); Live shows it. Like walking: by eye.)
- [x] 3 A swing on the spine chain (j_sebo_a, j_sebo_b, j_sebo_c) with Delay 3: the upper back follows the lower back,
  and the chest leans no further than the Tilt set (Spread over chain on).
  (XIV Bridge animations-pending/542, Release 2026-10-31: each spine bone takes a third of the Tilt, j_sebo_b trails
  j_sebo_a by 3 frames and j_sebo_c by 6, and the chest leans no further than the Tilt.)
- [x] 4 A swing covering a whole looping emote: no jump where the loop starts over in game.
  (XIV Bridge animations-pending/543, Release 2026-10-31, a looping idle: no bone's seam step gets more than 0.5 deg
  worse and the hips' at most 1 mm; Live over three loops shows no jump at the wrap.)
- [x] 5 Add > Smooth motion on a snappy emote with Window 10: the sharp moves soften without slowing the emote down.
  At Strength 0% it plays as before.
  (XIV Bridge animations-pending/544, Release 2026-10-31, Clap: the sharpest arm step drops by at least 15% with the
  length and the speed peaks kept; at Strength 0% it is within 0.1 deg of the clip.)
- [x] 6 Save both layers and play the saved animation: it matches Live. Edit again brings both layers back with their
  settings.
  (XIV Bridge animations-pending/545, Release 2026-10-31: the saved file matches Live within 1 cm and 2 deg, the game
  plays it, and Edit again brings both layers back with their settings.)

## Bones in use on the map (pending)

The Bones window's map shows only the bones a model the character draws has weight on (gear other gear hides and
parts hidden by attributes left out), the bones the clip takes off their rest pose and the bones a layer that is on
moves, plus the selected ones, the ones the selected layer changes and the body's handle. The filter button beside
swap sides shows every bone again; the crosshairs' all-bones mode shows the same dots as the map. Bones without a
spot on the pictures stay in All bones. The maths is covered offline (BoneMapFixture). Code:
Services/Previews/ModelBones.cs, Services/Animations/Preview/WornBones.cs, AnimationBones.Moved,
BonePosing.MapBones, PoseMap.Shows/Extras.

- [x] 1 Measure first (XIV Bridge): on a few vanilla emotes, how many tracked bones `AnimationBones.Moved` leaves out,
  and how far the furthest of them is from rest, to confirm the tolerance sits well above what compression leaves.
  Also list the bones the user's outfit has weight on (skirt, YAS fingers).
  (XIV Bridge, 2026-10-30, GPose loader, Miqo'te female: vanilla bow, cheer, think and six modded emotes, 97-104 tracked
  bones each.) Held bones sit under 0.0016 mm and 0.004 % off rest, but vanilla clips hold j_mune_l/r 0.14-0.17 deg
  off it; the smallest real moves were 0.75 deg (j_asi_e_l, a pose mod) and 1.46 mm (n_buki_r). The rotation line went
  from 0.25 to 0.5 deg; 0.5 mm and 0.5 % stay. Vanilla emotes move the skirt bones (cheer 3.96 deg at least), so those
  show without a skirt; the weapon bones and j_buki_sebo are only held. Untracked bones are exactly at rest.
  Partly checked (XIV Bridge, 2026-10-30, outside GPose, on the live build's ModelMeshReader): the player's 12 drawn models
  all read (V6, mod files and game files), 147 bones with weight, and every bone a model lists has weight. Of the map's
  bones 70 are used; unused are the skirt, armor and weapon bones, the IVCS toes (heels cover the feet), the IVCS third
  finger joints (the glove model has none) and n_sippo_e. A seam-connector body model's attributes leave 3 of its 20
  bones drawn. The clip side still needs a clip opened in the Timeline (GPose loader).
- [ ] 2 Wear a skirt and open an emote: the armor picture shows with the skirt dots. Take the skirt off (Glamourer,
  temporary): within a second the skirt dots go, and the armor picture too when none of its bones are used.
- [ ] 3 Bones the clip only holds at rest and nothing wears have no dot (skirt bones on a vanilla emote without a skirt);
  bones it moves do, worn or not.
- [ ] 4 Select a skirt bone, then take the skirt off: the selected bone keeps its dot. Key a bone in an offset layer:
  its dot shows while the layer is selected and after, as the layer moves it.
- [ ] 5 Click the filter button: every bone shows, as before. Crosshairs on all bones in GPose: the character shows
  the same dots as the map, with the filter and without.
- [ ] 6 A body mod with IVCS toes on a vanilla clip: the toes show greyed, with their hint; without the toes in the
  body mod they don't show.
- [ ] 7 Changing gear with the Bones window open, and a long gizmo drag on a layer with a symmetry and a pin layer, make
  no hitch in dalamud.log.

## Save pose (pending)

- [ ] 1 Open a body emote, key a pose layer, open a pose folder in the pose browser and save the playhead's pose: its tile
  shows with the folder's picture. Click it on another frame: the same pose keys. With a Pin feet (planted) or Smooth
  motion layer on, the saved pose is Live's, those layers included.
- [ ] 2 Load the saved file in Brio and in Ktisis (and Anamnesis if installed) on the GPose character: the body takes the pose.
- [ ] 3 Save under a name that is there: Replace it? shows; No keeps the file, Yes replaces it. A name with ? is refused.
- [ ] 4 Bones window, Save pose: the pose browser opens at its last folder with the name focused; with no folder yet it
  shows the folder list. In a face session the button is disabled with its hint.
- [ ] 5 Saving makes no hitch in dalamud.log.

## Breakdowner (pending)

- [x] 1 Offset layer, an arm keyed at two frames: on a frame between, Tween at 50% keys it halfway, Live shows it; 0% and
  100% match the keys; 150% overshoots. One Ctrl+Z takes the key out.
  (XIV Bridge animations-pending/500, Release 2026-10-31 19:42, Idle pose 3, the arm keyed 0 and 40 deg at 10 and 50: at 30
  Tween keys 0, 20, 40 and 60 deg for 0/50/100/150% (the 50% one with a real click on Apply), Live shows each within 2 deg,
  and one Ctrl+Z takes the key out.)
- [x] 2 Key the arm on that frame and turn it further: Push / pull at 150% exaggerates it, 50% softens it, Relax at 100%
  lands it on the in-between. Without a key there, the status says Key this frame first.
  (XIV Bridge animations-pending/501, Release 2026-10-31: on keys 0 / 30 / 40 deg Push / pull 150% and 50% move the middle
  key away from and toward the in-between, Relax 100% lands it on it, Live shows each, and with no key there the status
  says "Key this frame first.")
- [x] 3 Select a bone with no key on one side: Apply says No keys either side. With other bones that have both, only those key.
  (XIV Bridge animations-pending/501, Release 2026-10-31: "No keys either side."; with a second bone keyed both sides,
  only that one keys.)
- [x] 4 Pose layer whose keys set the hips' position: Tween moves the hips too and the new key sets the position. A layer
  without Scale ticked keeps the key's scale.
  (XIV Bridge animations-pending/502, Release 2026-10-31: Tween 50% moves the hips halfway and the key sets the position;
  with Scale unticked the key keeps its scale, ticked the scale tweens.)
- [x] 5 Select an amplitude or pin layer: there is no breakdown row (it shows under offset and pose layers only, as UI
  redesign stage 5 item 14 says). Ctrl+click the slider to type a value. (Reworded 2026-10-31, the user's call: the row
  stays hidden on other kinds rather than greyed with a hint.)
  (XIV Bridge animations-pending/503, Release 2026-10-31: an offset layer draws Tween, the slider and Apply; an amplitude
  and a pin layer draw no row; a real Ctrl+click turns the slider into a field and 75 typed sets it.)

## Key timing (pending)

- [ ] 1 Pick three keys of a pose layer, put the playhead on the first and press S: moving the mouse right spreads them, left
  draws them together, and the toolbar shows the percent. A click applies it (one Ctrl+Z puts it back); Escape or a right
  click during it changes nothing. Preview plays the scaled keys.
  Partly checked (XIV Bridge animations-pending/504, Release 2026-10-31, real S, mouse moves and clicks: right spreads to
  180% (keys 20, 38, 56), left draws to 59% (20, 26, 32), the toolbar shows the percent, a click applies it as one undo
  step, Escape and a right click change nothing, and Live plays the scaled keys. Not checked: Preview.)
- [x] 2 Right-click a picked key, Scale timing, type 50 and Enter: the keys close in on the playhead. A percent that would
  put two keys on one frame shows a reason in the toolbar and changes nothing.
  (XIV Bridge animations-pending/505, Release 2026-10-31, a real right-click and typing: 20, 30, 40 become 20, 25, 30 in
  one undo step; 10% shows its reason in the toolbar and changes nothing.)
- [x] 3 Move the playhead past the keys and press Ctrl+D: copies land from the playhead on with the same spacing and are
  picked. On a key, the toolbar says why nothing happened. With no key picked, Ctrl+D still copies the layer.
  (XIV Bridge animations-pending/506, Release 2026-10-31.)
- [x] 4 Reverse timing on three keys with different eases: the motion plays backwards between the first and last key, each
  key keeping its ease.
  (XIV Bridge animations-pending/507, Release 2026-10-31: keys at 10 / 20 / 40 move to 40 / 30 / 10, each with its value
  and ease, and Live plays the values backwards at the key frames, one undo step.)
- [x] 5 S in the Timeline doesn't walk the character back, and outside the Timeline S walks as usual.
  Also with no key picked (fixed 2026-10-31, the user's call: S was taken from the game only once a key had been picked,
  so a fresh Timeline let S walk the character; TimelineCanvas now asks about S whatever is picked).
  (XIV Bridge animations-pending/508, Release 2026-10-31 22:03 with the fix, real S presses with FFXIV in front: with the
  Timeline focused and no key picked S doesn't walk the player, with a key picked it starts the scale drag, and with no
  ImGui window focused S walks the player back as usual.)

## Layer weight (pending)

The maths and edits are covered offline (LayerWeightFixture); these checks are the canvas and Live.

- [x] 1 Key an offset layer, tick Weight in the layer panel: its row shows a line at the top. Click low in the middle of
  the bar: a point lands there, the line dips, and Live shows the offset weaker around it. Undo takes the point out.
  Partly checked (XIV Bridge animations-pending/509, Release 2026-10-31, the real Weight tick and click: the line shows at
  the top along the layer, the click adds a point at about 20 % that the line passes through, and Undo takes it out). To
  decide: one point weighs the whole layer (MotionEvaluator.WeightAt holds the first point's value before it and the last
  one's after), so with one point at 22 % Live shows 22 % at every frame (8.8 of 40 deg), not "weaker around it".
  Fixed 2026-10-31 (ruled: full weight elsewhere): the line starts at 100 % on the layer's first frame and ends at 100 % on
  its last unless a point is there, so a lone point dips it around itself only (LayerWeightFixture). Retest after a Release build.
  Retest after the fix (XIV Bridge animations-pending/509, Release 2026-10-31 22:03): passes with the new rule; the
  weight is 100 % at the layer's ends and dips to the lone point.
- [ ] 2 Drag a point up, down and sideways: it follows, stops before its neighbours, snaps to keys and the playhead, and
  moves freely with Alt. Hover it: "Frame N, 60 %". Right-click it: it goes.
  Partly checked (XIV Bridge animations-pending/510, Release 2026-10-31, real drags: it follows within a pixel's worth,
  stops before both neighbours, snaps to the key at 42 and the playhead at 28, the hover says "Frame 35, 60 %" and a
  right-click deletes it. Alt: by hand, a script can't hold it through a drag.)
- [x] 3 While Weight is on, the row's keys and fade handles don't take clicks; the panel's Starts and Ends still work and
  drop points left outside. Untick Weight: the line stays faint and the keys work again. Clear weight empties it.
  (XIV Bridge animations-pending/511, Release 2026-10-31: with Weight on only the bar and its points take clicks (the
  layer's end edges still answer just outside it, for resizing); Starts 20 and Ends 50 drop the points outside; unticked
  the line stays at alpha 0.35 (0.95 while editing) and the keys work; Clear weight empties it.)
- [x] 4 An amplitude layer with a point at 0 over its first half changes nothing there in Live and the save.
  (XIV Bridge animations-pending/512, Release 2026-10-31, Idle pose 3 at 200 %: in the document, Live and the saved file
  the first half is within 0.1 deg of the unedited motion and the second half still changes.)
- [x] 5 Move the layer and duplicate it: the points go with it. Save and reopen with Edit again: the points are back.
  (XIV Bridge animations-pending/513, Release 2026-10-31.)
- [x] 6 Dragging points on a long clip makes no hitch in dalamud.log.
  (XIV Bridge animations-pending/514, Release 2026-10-31, Flame Dance with Live playing: no frame over 100 ms and no new
  "[HITCH]" line for Instant Edit while three points are dragged.)

## Motion paths (pending)

The Bones window's paths switch, beside onion skins, draws lines on the GPose character through the selected bones'
positions over the frames around the playhead. The frames and the cache are covered offline in MotionPathsFixture.

- [ ] 1 Select a hand on a dance and turn on the switch: a line follows the hand's arc, blue before the playhead and
  orange after, white at the playhead, on the hand's dot. Play or scrub: the path moves with the playhead.
- [ ] 2 Key the hand in an offset layer at two frames inside the range: those frames have bigger dots.
- [ ] 3 Range 5 and 60: the path gets shorter and longer. Near the clip's start or end it stops there; on a looping
  emote it carries on round the seam.
- [ ] 4 Select both hands and both feet: four paths. Select 10 bones: eight paths and the note beside the selection.
- [ ] 5 Drag a key with the gizmo: the path follows without blanking. Undo: it goes back.
- [ ] 6 Scrub a long dance with Range 60 and 8 bones: no hitch in dalamud.log.
- [ ] 7 Turn the crosshairs Off: no paths. Close and reopen the Bones window: the switch and the range keep their state
  until the plugin reloads.

## Noise layer (pending)

The wobble's maths is covered offline (NoiseLayerFixture); these checks are the panel and Live.

- [x] 1 Add a Noise layer over a looping idle, add the tail bones: Live shows the tail wobbling at random, and the loop's
  seam doesn't jump.
  (XIV Bridge animations-pending/546, Release 2026-10-31: each tail bone wobbles within the Tilt, differently per bone
  and over time, and the seam gets no worse; Live checked on the arm, as the tail's physics overrides it in Live. The
  tail in Live: by eye.)
- [x] 2 Raise Tilt, Twist and Slide: the wobble grows; untick Turn or Move and that part stops. Slide moves only the top
  tail bone.
  (XIV Bridge animations-pending/547, Release 2026-10-31: Tilt 5 to 15 makes the wobble about three times larger, Twist
  adds a turn about the up axis, Slide moves only the top tail bone, and unticking Turn or Move stops just that part.)
- [x] 3 Drag Speed from 0.1 to 10: the wobble goes from slow drifts to a tremble. New picks another seed and another
  wobble; Ctrl+Z puts the old one back.
  (XIV Bridge animations-pending/548, Release 2026-10-31: Speed 0.1 to 10 is one undo step and the wobble changes at
  least 4 times faster; New picks another seed and another wobble, and Ctrl+Z puts both back.)
- [x] 4 A noise over part of a clip starts and ends at rest. The Bones window shows the same pose as Live.
  (XIV Bridge animations-pending/549, Release 2026-10-31: over frames 20-50 the tail is the clip's own outside the range
  and eases in; the Bones window matches Live within 2 deg, checked on the arm.)
- [x] 5 Save and reopen with Edit again: the wobble is the same. Dragging its sliders makes no hitch in dalamud.log.
  (XIV Bridge animations-pending/550, Release 2026-10-31: real drags on Tilt and Speed make no frame over the limit and
  no new Instant Edit [HITCH] line; after a save Edit again gives the same wobble.)

## Curve view (pending)

- [x] 1 Body emote, Curves on: the strip shows under the lanes; with no bone selected it says Select a bone. Pick the
  left upper arm: Turn X, Y and Z draw in red, green and blue, keys line up with the rows above while scrolling and zooming
  (wheel and Ctrl+wheel in the strip too). Drag the splitter: the strip and the lanes resize.
  (XIV Bridge animations-pending/580, Release 2026-10-31 22:25: Curves shows the strip under the lanes, "Select a bone"
  with none, Turn X, Y and Z in red, green and blue for the left upper arm, the key ticks lining up through zoom and
  scroll, and the splitter resizing the strip and the lanes.)
- [x] 2 Motion: with no layer the faint and solid lines are one; add an offset turn on the arm: "Reading" shows briefly,
  then the solid line follows the layer and the faint one stays the clip's own. A spinning or flipping bone draws no jumps
  at 180 degrees. Move X / Y / Z switches add the moves in cm.
  (XIV Bridge animations-pending/581, Release 2026-10-31: with no layer one line per turn; an offset turn shows Reading,
  then the solid line follows the layer; a spinning arm draws no 180 deg jumps; the Move switches add the moves.)
- [ ] 3 Layer, the offset layer selected: a dot per key, eased between them as Live plays. Drag a dot up: that one value
  changes, Live and the Bones window's Turn fields follow; one Ctrl+Z undoes the whole drag. Ctrl+click a dot, type a value
  and Enter: the key takes it.
  Partly checked (XIV Bridge animations-pending/582, Release 2026-10-31: a dot per key, Live between keys as the curve, a
  dot drag changes that value and the Bones window follows, one Ctrl+Z). Ctrl+click on a dot opened no field in the run;
  open.
- [x] 4 Layer on a pose layer whose keys set the hips' position on some keys: Move dots show only on those keys. On an
  amplitude layer the strip says This layer has no keys to show.
  (XIV Bridge animations-pending/583, Release 2026-10-31: on a pose layer Move dots show only on the keys that set the
  hips' position; an amplitude layer's strip says it has no keys to show.)
- [x] 5 Click or drag on the graph: the playhead follows. Dragging a key on the canvas meanwhile holds the after curve
  until the drop. No hitch in dalamud.log while the strip is open during playback.
  (XIV Bridge animations-pending/584, Release 2026-10-31: a click and a drag on the graph move the playhead, a canvas key
  drag holds the after curve until the drop and rebuilds it after, and no hitch while the strip is open during playback.)

## IK limits and Bend (pending)

Knees and elbows bend only forward, at most 150 degrees, wherever a pin or a drag bends a limb; a Bend angle turns a
limb about the line from its upper bone to the hand or foot. The solver is covered offline in PinLayerFixture (Limits).
The offline skeleton's rest knees and elbows are bent; item 1 checks that the game's rest pose bends them forward too.

- [x] 1 An offset layer, select the left foot, Gizmo: Drag. Raise the foot to the side, above the hip, then in front
  and behind: the knee always bends forward, never backwards. The same for a hand reached behind the back: the elbow
  never bends backwards.
  Fails (XIV Bridge animations-pending/526, Release 2026-10-31): the knee always bends forward on its own hinge, but at
  one frame of the sweep from the side to behind the whole leg turns half a turn about the hip (the knee jumps 70 cm in
  one frame, twice the thigh). Suspected: PinSolver.cs:231-241 picks the bend plane, its mirror or a roll afresh every
  frame from the drag's start pose, so the side can switch in a single frame.
  Fixed 2026-10-31: each frame of a drag still solves from the start pose but bends on the plane the frame before left
  (PinSolver's LimbSide), so the knee moves round with the aim; offline the largest knee step on a 1 deg sweep fell from
  53 cm to 0.6 cm (PinLayerFixture). Retest after a Release build.
  Retest after the fix (XIV Bridge animations-pending/526, Release 2026-10-31 22:03, the drag carrying its bend side as
  Instant Edit's own drag does): no leg bone moves over 60 mm in a frame on the sweep from the side to behind, and the
  knee and the elbow bend forward on every frame.
- [x] 2 Drag the foot up toward the hip: the knee folds to about 150 degrees and the foot stops short, without a snap.
  Partly checked (XIV Bridge animations-pending/527, Release 2026-10-31): the knee folds to about 150 deg and the foot
  stops short of the aim, but the last frame steps 3.3 cm (the check allows 2 x the gizmo's step + 2 mm). Open: test or
  solver.
  Rerun (XIV Bridge animations-pending/527, Release 2026-10-31 22:25): the knee folds to about 150 deg, the foot stops at
  the folded distance, and no frame jumps (the earlier fail was the first step from a near-straight knee, which moves
  several times the gizmo's step without snapping).
- [x] 3 With the foot selected and Drag chosen, set Bend to 90 and drag: the knee points to the side and the layer keys
  that pose. Bend back to 0: the next drag bends the knee forward again. The Bend field shows only for hands and feet.
  To decide (XIV Bridge animations-pending/528, Release 2026-10-31): Bend 90 points the knee 90 deg to the side and keys
  it, and Bend goes back to 0 by itself after every drag (TimelineWindow.Overlay.cs:662-664); but the next Bend 0 drag
  keeps the knee 90 deg to the side, as Bend works from the pose the drag starts on (PinSolver.cs:177-179). Either the
  item says the knee stays where the keyed pose has it, or Bend becomes a side measured from the rest pose and stops
  resetting.
  Ruled 2026-10-31 (the user's call): Bend is relative to the pose the drag starts on, so after a Bend 90 drag the next
  drag keeps the knee where the keyed pose has it; Undo or removing the key brings it forward. Checked as ruled: Bend 90
  keys the knee 90 deg to the side, Bend goes back to 0 after the drag, and the next Bend 0 drag keeps the keyed side
  (XIV Bridge 528).
- [x] 4 A pin layer with a keyed hand pin: the pin's Bend row turns the elbow on Live; one slider drag is one undo step.
  The row shows only for hand and foot pins.
  (XIV Bridge animations-pending/529, Release 2026-10-31: the pin's Bend row turns the elbow in Live, one slider drag is
  one undo step, and the row shows only for hand and foot pins.)
- [x] 5 Hip drag on the seated pose and a head drag: the knees and elbows still bend forward, as before.
  (XIV Bridge animations-pending/530, Release 2026-10-31, Ground sit: a hip drag and a head drag keep the knees and
  elbows bending forward.)
- [x] 6 Save (replace) and Edit again: the pin's Bend is back and the clip plays the same.
  (XIV Bridge animations-pending/531, Release 2026-10-31: a hand pin with Bend 60 saved in place into a test mod comes
  back with Edit again and plays the same.)

## Look at (pending)

The turn's maths is covered offline (LookAtFixture); these checks are the panel, the overlay and Live.

- [x] 1 Add a Look at layer over an idle with Live on and paused: a dot shows in front of the face with a faint line
  from the head. Drag it with the gizmo to one side: the head, some of the neck and a little of the upper back turn
  toward it, and a key appears at the playhead. Ctrl+Z puts the dot back.
  (XIV Bridge animations-pending/551, Release 2026-10-31: the spot starts 1 m in front of the head; moved 80 cm aside it
  keys at the playhead in one undo step and the head ends within 1.5 deg of it, the neck turning less and the upper back
  least; Ctrl+Z puts it back. The dot and its line: by eye.)
- [x] 2 Key another spot later on and play: the head goes from one spot to the next, the way the layer eases.
  (XIV Bridge animations-pending/552, Release 2026-10-31.)
- [x] 3 Move the dot behind the character: the head turns as far as Limit allows, level, never over the top. Lower
  Limit: the turn gets smaller, without a snap near the limit.
  (XIV Bridge animations-pending/553, Release 2026-10-31: behind the character the head turns 72-80.5 deg at Limit 80 and
  36-40.5 deg at Limit 40, level, with no snap as the Limit and the spot sweep.)
- [x] 4 Looks at, pick Right Hand (j_te_r) on an emote that moves the hand: the keys go, the line runs to the hand and
  the head follows it. Pick Spot again: the gizmo comes back.
  (XIV Bridge animations-pending/554, Release 2026-10-31: following Right Hand the keys go and the head looks at the hand
  within 5 deg where it can reach; Spot again brings the gizmo back.)
- [x] 5 Untick Upper back and Neck: only the head turns, and the same angle. The Bones window shows the same pose as Live.
  (XIV Bridge animations-pending/555, Release 2026-10-31: the neck and upper back stay the clip's own, the head looks the
  same way, and the Bones window matches Live.)
- [x] 6 Save and reopen with Edit again: the look is the same. Moving the dot makes no hitch in dalamud.log.
  (XIV Bridge animations-pending/556, Release 2026-10-31: moving the dot makes no hitch; Edit again brings back the same
  spots, Limit, bones and motion.)

## UI redesign stage 1 (pending)

- [ ] 1 Preview shows the Video icon and the label Preview.
- [ ] 2 Live shows its label and stays filled while on.
- [ ] 3 From start replays the preview from frame 0. No button on the bar uses the Redo icon.
- [ ] 4 Speed is a narrow field showing 100%. Dragging it and Ctrl+click typing both stay between 10 and 200.
- [ ] 5 The time beside Stop reads like "6.13s · frame 184" and follows the playhead.
- [ ] 6 Disabled controls keep today's reason tooltips, such as "Press Preview first." on Play.
- [ ] 7 The Bones window shows Undo and Redo first, then the same play bar as the Timeline.
- [ ] 8 Key a bone in the Bones window, then click Undo there: the edit is undone and the Timeline's lanes follow.
  Redo puts it back.
- [ ] 9 The Bones header's own Live and Play buttons are gone. The key arrows, layer name, frame and help stay.
  (Replaced by the layer strip in stage 5.)

## UI redesign stage 2 (pending)

- [ ] 1 With nothing to say, the status line takes no height: the lanes start right under the play bar.
- [ ] 2 Outside GPose, "Enter GPose to play the preview." shows in the status line, not on the play bar.
- [ ] 3 With an effect that stops after its first loop, its warning shows there with Keep it looping (Keep them
  looping for several). The button works and is greyed out while a save runs.
- [ ] 4 With more than one note, "+N notes" opens a list of all of them, each with its colour and button.
- [ ] 5 Dismiss a note: it stays hidden while its text is the same and comes back when its text changes.
- [ ] 6 The Preview tooltip holds "Sounds and effects play only while the preview plays." and "Changing an effect
  look redraws your character."
- [ ] 7 The Live tooltip holds the Live hint, and over a preview "Live shows your bone layers over the preview."
- [ ] 8 The action bar reads Undo, Redo, Add, the play bar, the length and Bones on the right, with no menu button
  on it. The time shows once, without the word "Playhead". The status line sits under the bars, above the lanes.
  (The length moved to the clip view in stage 4.)
- [ ] 9 Add opens today's menu without Still pose; a face's Add has no Neutral face.
- [ ] 10 Bones is filled while the Bones window is open and is hidden on an expression's timeline without its face.
  Resize the Timeline to its narrowest (760 px): Bones stays visible, on a line of its own if the bar is full.
- [ ] 11 Click Start with Live on: the start page shows "Back to" the clip, and Live, the preview and the Bones
  window keep running. Back returns to the same view and playhead.
- [ ] 12 With an unfinished edit, Start reads "Start (1)" in the warning colour.
- [ ] 13 Select an event, then click the clip's name in the header: the selection clears.
- [ ] 14 Above the lanes are the menu bar (Animation, Edit, Add, View, Help), the header (Start and the clip's name) and
  the action bar (Undo, Redo, Add, the play bar, Bones), then the status line when it has a note. Snap, Scale events,
  Fit, Curves, Blender, Load, Follow and the help tip are gone from the bars. (The length moved to the clip view in
  stage 4. Reworded 2026-10-31, the user's call: the header row sits between the menu bar and the action bar.)
- [ ] 15 Animation: Start page and Load another (on Game animations) open the start page; Reload reads the clip again
  and asks "Reload it and lose them?" with unsaved edits; Open startup (or Open loop from a startup) opens the other
  clip. Send motion to Blender, Motion from Blender and Still pose work as before. After Still pose, Use the clip's
  motion goes back.
- [ ] 16 Edit: Undo and Redo work and show Ctrl+Z and Ctrl+Y. With an event selected its Duplicate, Delete, Move to
  playhead and Copy show; with a bone layer its menu (with the key items while keys are picked); with a cut or the
  loop seam the clip row's items at the playhead; with nothing "Nothing selected".
- [ ] 17 Add opens the same items as the action bar's Add. View: Fit (F), then Snap, Scale events with length and
  Follow as ticks, then Curves and Bones window as ticks. Each one works, and Help, Mouse and keys shows the help text.
- [ ] 18 While a save runs, Reload, Add, Undo and the Blender items are greyed out. The menu items give their reason as a
  tooltip ("Wait for the timeline to finish."); the action bar's Add and Undo keep their usual tooltips. (Reworded
  2026-10-31, the user's call.)
- [ ] 19 On an expression's timeline without its face: no Curves, Bones window or Blender items. On a face (Edit face):
  no Blender items, no Reload, and Neutral face instead of Still pose.
- [ ] 20 The start page has no menu bar, and neither does a timeline that can't be edited. Its banner offers
  "Start page".
- [ ] 21 At the Timeline's default 1000 px, Save to, the mod name, Skeleton, Save and Undo last save sit on one row,
  with Save and Undo last save on the right.
- [ ] 22 Resize to 760 px: the mod name shrinks first and Save stays visible. On a rebuild (the wide Skeleton choice),
  Save and Undo last save move to a line of their own, on the right.
- [ ] 23 Save to has New mod and In place. The mod name shows only for New mod and shows "Mod name" while empty.
- [ ] 24 A blocking problem (In place on a game file, a skeleton too small) shows as one warning line above the row, not
  a banner; a failed save shows its error there in red. Events past the end and layer bones show there too.
- [ ] 25 With layers and a repair, Skeleton's tooltip holds the bake-in hint, and the highest index, bones left out
  and the rebuild lines. On a loop with a startup from the same mod, Save to's tooltip says the startup goes along.
- [ ] 26 On a face (Edit face): no Skeleton. Save's tooltip names the face, and the face's in-place problem shows on
  the warning line.
- [ ] 27 Save, Undo last save and the save running keep their reasons as tooltips, on a face session and a timeline
  that is read-only too. The spinner, status and Cancel follow Undo last save while it runs.
- [ ] 28 Saving in place a sound other animations use still asks Everywhere or Only here.
- [ ] 29 On a clip borrowed from another race whose motion can't be read on the rebuild, the status line warns "Can't
  rebuild for" the race and Skeleton shows Keep its own. A clip that can't be rebuilt says why there as a note.
- [ ] 30 Repair onto a skeleton that lacks bones the animation moves: the warning line reads like "Repair onto IVCS leaves
  out 3 bones." and Skeleton's tooltip lists them.
- [ ] 31 Live on, no preview: drag Turn in the Bones window. The lanes don't jump.

## UI redesign stage 3 (pending)

- [ ] 1 On Playing now, a click on a row opens its timeline. No row stays selected.
- [ ] 2 A click on a startup row opens the startup, not its loop.
- [ ] 3 A row's pen opens Send animation to Blender over the Timeline window.
- [ ] 4 Make startup instant (in a loop's "..." menu) shows its result on the page's last line, not in the main window's
  status strip.
- [ ] 5 With no clip open, the Timeline shows the start page: a sidebar with Playing now, Game animations, Saved edits
  and Record, each with its icon, and the chosen page beside it. There is no Partners row.
- [ ] 6 Playing now's row shows how many animations play now. With an unfinished edit, Saved edits shows the count in
  the warning colour.
- [ ] 7 Game animations has the loader. Play one: "Waiting for" it shows on the page's last line with Cancel, and its
  timeline opens once it plays.
- [ ] 8 Open a clip, turn on Live and click Start: the page starts with "Back to" the clip, and Live keeps updating,
  the preview plays, autosave keeps the edits and the Bones window stays open and posable.
- [ ] 9 Back to the clip returns to the same view and playhead.
- [ ] 10 With unsaved edits, click Start and open another clip from Playing now, Game animations or Saved edits' Edit
  again: the Discard / Keep editing banner shows over the start page. Keep editing stays on the start page with the
  clip still open; Discard opens the other clip.
- [ ] 11 On the start page, click the open clip's own row in Playing now: the clip view comes back, nothing is read again.
- [ ] 12 Pick Saved edits, open a clip and click Start: the page opens on Saved edits. Animation, Load another opens it
  on Game animations. After a plugin reload it opens on Playing now again.
- [ ] 13 With animation editing unavailable, the Timeline shows its banner and the recorder.
- [ ] 14 The main window's Animations button (running icon) opens the Timeline window, or brings it to front when it is
  open. The main window stays on its tab, and there is no Animations tab or Open timeline button any more. Its tooltip
  reads "Open the Timeline window."
- [ ] 15 With an unfinished edit the button reads "Animations (1)" in the warning colour, tooltip "1 edit didn't
  finish. Undo it in the Timeline."; with three, "Animations (3)" and "3 edits didn't finish. Undo them in the Timeline."
- [ ] 16 With animation services unavailable, the Animations button still opens the Timeline, which shows its banner
  and the recorder.
- [ ] 17 Close the Timeline: the listener stops, unless Quick Actions' Send character card waits for an animation.
  With that card waiting (main window on Quick Actions), it still hears the animation you play.
- [ ] 18 Still pose, then Start before it finishes, then Back: the still pose is in, and the Bones window never stayed
  greyed.
- [ ] 19 Record a live pose from the start page: its result shows on the Record page.
- [ ] 20 Send an animation to Blender with a Playing now pen and close the dialog before it ends: the page's last line
  shows the send running with Cancel, then how it went. Undo in Saved edits shows there the same way.

## UI redesign stage 4 (pending)

- [ ] 1 Open a body clip with nothing selected: the side panel shows Source, then Length with its seconds and Scale
  events with length, then Bones kept reading like "37 of 40 kept · highest index 103" with Edit. Ticking it ticks the
  View menu's Scale events with length, and the other way round.
- [ ] 2 Type a new length and press Enter: the motion stretches and the header's length turns amber.
- [ ] 3 Edit opens the bones-kept page: Clip (back arrow), the hint, Keep only, the filter and the ticks. Untick a
  bone, go back with Clip: the line counts one fewer. Open another clip: the clip view shows, the filter is empty.
- [ ] 4 An expression's own timeline: Source shows Timeline and Timeline from when it has them, the length is greyed
  out with "An expression's timeline keeps its length.", and Bones kept's Edit opens the face's bones, without Keep only.
- [ ] 5 An expression whose face can't be read: Bones kept says why, with no Edit.
- [ ] 6 Edit face: Face shows Moves and, while it moves, its seconds. Tick and untick Moves, then change the seconds:
  each works as before. The header's length tooltip says the face's own length is in the clip view.
- [ ] 7 While a save runs, and on a timeline that is read-only, the length, Moves, the seconds, Open startup, Made for
  and the ticks are greyed out with today's tooltips.
- [ ] 8 The action bar reads Undo, Redo, Add, the play bar and Bones, with no length or Face. At 760 px Bones stays on
  the bar's right while there is room, and moves to a line of its own only when there isn't.
- [ ] 9 The side panel has no tabs. Select an event: its properties show. A prop's parts (one group): one event's
  properties. Events from different groups: the count with Duplicate and Delete. A bone layer: its settings. A cut or
  the loop seam: the cut panel. Click empty lane space: the clip view.
- [ ] 10 Select an event, then click the Motion lane's name: the selection clears and the clip view shows. The same
  with the header's clip name. A bone layer's name still selects the layer, and its fold arrow and right-click menu
  work as before.
- [ ] 11 Select a face event and click Edit face: the face opens on its own as before, and Back returns.
- [ ] 12 Add a layer from a .livepose file: its settings show in the side panel.
- [ ] 13 Saved edits and Record are on the start page only, not in the side panel. The splitter and the panel's width
  stay as before.
- [ ] 14 Select a layer of each kind: its settings show a muted line where the bone lists were ("Limited to 2 bones" or
  "Every bone" on amplitude, fix and idle, "3 bones keyed" on offset and pose, "Copies 2 bones", "3 pins", "Turns 3 bones",
  "Wobbles 1 bone", "Swings no bones", "Smooths every bone") and Pose in Bones window. A hand shape and a root reset show
  the button with no line. There is no bone picker, bone values, pin buttons, Look at bone ticks, pose sources or Bones
  button any more.
- [ ] 15 The settings keep the name, on/off, timing, amount, Weight, parts, the Swing, Smooth, Noise and Look at fields,
  each pin's settings with Bend, the keys, spread and the selected key's ease.
- [ ] 16 Pose in Bones window opens the Bones window when it is closed and brings it to front when it is open; it never
  closes it.
- [ ] 17 For each kind, the Bones window still poses it: offset and pose bones with the ball, pose sources on a pose
  layer, the bone list for amplitude, fix, idle, swing, smooth and noise, the head chain ticks for Look at, the symmetry
  bones and Pin here. The summary line follows each change.

## UI redesign stage 5 (pending)

- [ ] 1 The Bones window's title reads "Bones · " and the clip's name, and "Bones" with no clip open. Click Start in the
  Timeline: the title keeps the clip's name and posing still works.
- [ ] 2 The Bones window's top line holds Undo, Redo and the play bar; the line below holds the layer strip: the layer's
  icon, the dropdown, the previous-key arrow, the strip and the next-key arrow. There are no step buttons, layer name,
  frame or help on the top line; the help is at the end of the tools row.
- [ ] 3 Select a bone layer in the Timeline: the strip shows that layer, its span across the clip, its keys as diamonds
  and the playhead. Choose another layer in the dropdown: the Timeline selects it and its side panel shows its settings.
- [ ] 4 A clip with no bone layer: the dropdown reads "No bone layer" and is greyed out. With no layer selected the strip
  shows the playhead only, and the arrows are greyed out with "Select a bone layer first."
- [ ] 5 The arrows go to the layer's previous and next key and select it; past the first or last key they are greyed out.
  The playhead moves in both windows, and a playing preview pauses.
- [ ] 6 Click or drag on the strip: the playhead follows in both windows. A click on a key's diamond goes to that key and
  selects it in the Timeline, the diamond in the accent colour.
- [ ] 7 At the window's 620 px minimum, Undo, Redo and the whole play bar show, and the strip ends with its next-key
  arrow inside the window.
- [ ] 8 The tools row reads Map, All bones, Select, View, Clear, the selection, then Save pose on the right and the help
  tip after it. At the 620 px minimum all of them show; with a long bone name or "(paths for the first 8)" the selection
  text is cut off before Save pose, and hovering it shows the whole text. There are no swap, filter, on-character, onion
  skin or path icon buttons any more.
- [ ] 9 View opens a menu: an "On character" line with Off, Selected and All (one ticked at a time), then Every bone and
  Mirror view, then Onion skins and Paths. Ticking several items in a row keeps the menu open. On All bones, Every bone
  and Mirror view are not in the menu.
- [ ] 10 Each View item does what its old button did, with its old tooltip: On character shows no bones, the selected ones
  or all of them in GPose; Every bone shows every bone on the map; Mirror view ticked shows the left side on the left,
  unticked as seen from the front; Onion skins and Paths draw on the character.
- [ ] 11 With Paths on, its frames field sits on the Paths line, right of the label and left of the tick. Dragging it
  changes the frames (with today's limits) and letting go anywhere on the line leaves Paths on; a click on the label
  switches Paths off and the field goes.
- [ ] 12 Save pose is greyed out for a face, with "A face can't be saved as a pose." on hover. The help's first line ends
  "View > Every bone shows them all."
- [ ] 13 Select a layer of each kind in the Bones window: the kind's icon and name, Make symmetrical, then its posing
  controls. Offset and pose: the ball, Turn, Move and Scale, then Key, Delete key, Reset and Clear on one line and Copy
  pose, Paste pose, Paste mirrored and Mirror below, each with its old tooltip and greyed out as before; pose sources
  under a pose layer. Amplitude, fix, idle, swing, smooth and noise: the bone list. Look at: its bones, what the target
  follows or how to key it, and what the gizmo waits for. Symmetry: its bones and the copied-side hint. Pin: the pin
  buttons.
- [ ] 14 The breakdown row (Tween, Push / pull or Relax, the amount and Apply) shows under offset and pose layers only.
  Other kinds have no breakdown row and no "Breakdowns need an offset or pose layer." hint.
- [ ] 15 With no layer selected: the hint, then Add layer. It opens a menu with each layer kind (a face's own kinds on a
  face), then Match startup to loop, Close loop, Smooth jitter, Hand shape and Reset root turn (on a face only Smooth
  jitter), greyed out with their
  reasons as the old buttons were. Each adds over the whole clip, as before. On a read-only timeline
  every item is greyed out.

## Partners (pending)

The Timeline start page's Partners page spawns a second GPose actor from a preset that links a Penumbra collection, a
Glamourer design and a Customize+ profile. Spec: docs/superpowers/specs/2026-10-30-partner-spawner-design.md (local
only).

- [x] 1 Make a preset "Aria Rose" with a collection, a design and a C+ profile; in GPose Spawn: the partner stands on
  your spot facing your way, wearing the design with the profile's proportions and the collection's mods.
- [x] 2 While spawned, Penumbra's Individual Assignments list "Aria Rose" with that collection; after Despawn the entry
  is gone.
- [x] &#x20;
- [x] 3 Leave GPose with the partner spawned: it disappears and the Individual entry is gone.
- [x] 4 Delete the preset's design in Glamourer: the row shows it missing; Spawn still works and lists "Design <name>
  no longer exists" with the name of the design you deleted.
- [x] 5 A Glamourer automation set for "Aria Rose" doesn't change the spawned look.
- [x] 6 "Aria" alone and your own name are refused with their messages.
- [x] 7 Reload Instant Edit with a partner spawned: the partner is gone and Penumbra lists no "Aria Rose".
- [x] 8 The Partners page: right-click a row (its name or links) for Edit and Delete; Edit opens the editor with the
  preset's values; the name box takes 21 characters; delete a linked design in Glamourer and the row marks it
  (missing) within 5 seconds.
- [x] 9 In Penumbra, give "Aria Rose" its own Individual assignment to None; Spawn the preset (its collection applies
  while spawned), then Despawn: the Individual entry for "Aria Rose" is None again, not removed.

## Partner Timeline (pending)

Target a spawned partner in GPose and the Timeline window shows its own workspace: its own clip, unsaved edits, Preview
and Live, under one playhead with yours. Spec: docs/superpowers/specs/2026-10-31-partner-timeline-design.md (local
only).

- [ ] 1 Open a partner mod's two halves, one on you and one on the partner, with Live on both: pausing and stepping puts
  both on one frame, and the contact points line up.
- [ ] 2 Save the partner's half in place: the file in its preset's collection changes, and the partner is redrawn with it.
- [ ] 3 Save the partner's half as a new mod: it lands in the partner's collection.
- [ ] 4 Record the partner: the recorded bones are unscaled, and its Customize+ proportions come back afterwards.
- [ ] 5 Despawn with unsaved edits, then spawn again: the clip and edits come back; saving works.
- [ ] 6 Target the partner, then back to you with edits on both: closing the window asks once, naming both.
