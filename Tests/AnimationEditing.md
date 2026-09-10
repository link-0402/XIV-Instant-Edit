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

## Automated checks

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
The fixture is not a complete SimpleHeels integration test. The native functions
must be invoked inside the game process.

## Setup and evidence

Use a development plugin installation and a test Penumbra collection. Record
FFXIV, Dalamud, FFXIVClientStructs, Penumbra, SimpleHeels, and Instant Edit build
versions. Keep the source mod and original PAP/SKLB available for comparisons.
Record game paths, binding indices, partial indices, capture times, and job IDs.
The job journal and staged PAPs are under the plugin configuration directory's
`AnimationEdits/<job-id>` directory; managed PAP backups are under `Backups`.

## Native round trips and transform parity

- [ ] Load the development build with Blender, VFXEditor, and XAT closed. Observe
  an emote and an idle; changing playback does not change the selected capture.
- [ ] Establish the no-change Havok round trip for spline and interleaved PAPs.
  Each job performs this before writing outputs; confirm it passes and does not
  alter any unselected binding, annotation, float mapping, or extracted motion.
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
- [ ] Package a graph whose PAP, VFX, SFX, textures, and skeleton come from different
  mods. Include nested external TMBs, embedded timelines, shared dependencies,
  file swaps to other game files, and collection inheritance.
- [ ] Verify a new mod is registered, enabled in the captured collection, and
  prioritized above enabled providers. Existing providers' settings must remain
  unchanged. A maximum-priority conflict must produce an actionable error.
- [ ] Disable the source animation/effect mods manually after successful creation.
  Replay startup, loop, and exit; verify visual and sound effects and skeleton
  behavior still match. This is the required standalone-package acceptance test.
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
- [ ] **Restore live offsets** restores compatible cleared components while the
  baked animation stays active. **Undo edit** restores original PAP files or
  disables the created mod, then restores compatible live offsets.
- [ ] Change a PAP, new-mod metadata, or offsets after a successful edit. Undo and
  restoration must refuse conflicting changes. Test moved/renamed mods, expired
  backups, a different player, and a different collection.
- [ ] Test multiple successive edits and undo them in reverse order. Preserve the
  recovery records across plugin restarts. PAP backups share the existing 30-day
  managed retention; expired backups must produce a clear recovery error.

General keyframe editing, movement/combat animations, other race variants, and
editing separate companion/weapon rigs are outside this release. Unsupported
dependency constructs are a packaging boundary, not permission to omit assets.
