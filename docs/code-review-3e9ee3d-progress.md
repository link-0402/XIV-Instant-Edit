# Code review up to `3e9ee3d`: progress (interrupted)

A second review pass covering the code as of commit `3e9ee3d` ("Ignore Tools/EarringBones"). It was stopped part-way
because the cloud session was running out of credits. This file records what was done and how to resume it locally.

## Scope

`docs/code-review-testing-branch.md` already reviewed 13 of 24 areas at `b99337f`. This pass covers what that review
left out, plus the commits it added:

| Area | Covers (paths under `Dalamud-Plugin/` unless noted) | Status |
| --- | --- | --- |
| S02 Animation preview and timeline model | Services/Animations/Preview, Services/Animations/Timeline | stopped, no report |
| S04 Timeline UI: editing core | Ui/Timeline: TimelineEditor, TimelineEdits, CutEdits, CutPanel, TimelineWorkspace(.Overlay), SaveFooter, TimelineHistory, TimelineAutosaver, TimelineLoader, TimelineReader, TimelineSession, EditedSeam, SoundSaveCheck, WorkspaceActor, WorkspaceServiceFactory, StatusLine, ShortcutKeys, RecentEditsPage, StartPage, TimelineTexts, DragHolds | stopped, no report |
| S05 Timeline UI: motion, layers, bones | Ui/Timeline: MotionEdits, LayerPanel, LayerStrip, BoneEditor, BonePosing, BonesPanel, PoseBrowser, PoseKeyOptions, PoseSourcePanel, PoseThumbnails, LoopPoseReader, PartnerLinks, PartnersPage, LinkFields, LinkPicker, LookFields, SwingFields, CurveBake, CurveView, HandShapePanel, LivePosePanel, BlenderMotionPanel, MotionDecode, KeyMarkers | stopped, no report |
| S06 Timeline UI: canvas, preview, pickers | Ui/Timeline: TimelineCanvas, TimelineClipBand, TimelineHitTest, TimelineLayout, TimelinePainter, TimelinePreview, TimelinePickers, TimelineMenus, TimelineSnap, TimelineToolbar, TimelineUi, TimelineView, PlayBar, ClipView, MarkerSearch, PropertiesPanel, SidePanel, SoundFiles, SoundInspector, EffectLookEditor, EffectPlaceEditor, PropPlaceEditor; Ui/TimelineWindow*.cs | stopped, no report |
| S10 Studio windows: main | Studio/Windows/Studio, Home, Import, ModWindow.cs, PackageImportWindow.cs | stopped, no report |
| S11 Studio windows: build, panels, UI | Studio/Windows/Build, Panels, UI, BuildWindow.cs, Studio/*.cs | stopped, no report |
| S12 Models and plugin root | Studio/Models, Models, Plugin.cs, Configuration.cs, InstantEdit.csproj | stopped, no report |
| **S15 Physics and heels** | Services/Physics, Services/Heels, Ui/PhysicsWindow*.cs, Ui/PhysicsOverlay.cs | **done: 4 findings, 1 fixed** |
| S19 Audio, skeletons, pose UI, UI root | Services/Audio, Services/Skeletons, Ui/Pose, Ui/*.cs (not Physics*/Timeline*) | stopped, no report |
| S20 Main window UI | Ui/Main | stopped, no report |
| S24 Tests and documentation | Tests, Blender-Addon/testing, docs, README, workflows | stopped, no report |
| SF The earlier review's fix commits | `6257379`, `8293122`, `a0d8ff4`, `60b6b20`, `3e9ee3d` | stopped, no report |

Every reviewer except S15 was stopped before reporting; nothing from them was kept. Those areas still need reviewing
from scratch.

## Findings so far

### S15-1 · medium · fixed: a ~50 KB .phyb file makes the parser allocate gigabytes

- **Where:** `Dalamud-Plugin/Services/Physics/PhybFile.cs` (ReadSimulators, ListStart, ReadChain).
- **Problem:** each list offset was only checked to lie inside the file. Nothing stopped many simulators pointing
  at the same chain list, or many chains at the same node list. 255 × 255 × 255 gives 16.6 M nodes (about 4 GB) from
  a 52 KB file, and the 16 MB `MaxFileBytes` cap does not bound it. The file can come from any mod's .phyb: the
  builder's "Loaded now" / "A file" / "Import from a file", `ModelSkeletonResolver.WithHairRigAsync`, and
  `HairSkeletonFiles.ParsePhysics`.
- **Fix:** the parser counts the bytes of every list it reads and refuses the file
  ("The physics file's lists repeat each other.") once they pass twice the file's size. The game's own files never
  share lists, so everything they hold fits in the file.
- **Test:** `PhysicsScenarios.CheckRejections`: a forged 40-simulator file sharing one chain list and one node list is
  refused, and a small file with its lists read once still parses. Checked here with a standalone build of
  `PhybFile.cs` (the plugin can't build in the cloud sandbox). The old parser read the forged file into 2,601,000
  nodes in 4.2 s; the new one refuses it. A 658 KB file of 30 simulators × 20 chains × 12 nodes still round-trips byte
  for byte. **The full C# suites have not been run on this change; run them locally or in CI.**

### S15-2 · low · fix here, not yet done: ticking a shape whose name isn't printable ASCII throws in Draw

- **Where:** `Dalamud-Plugin/Services/Physics/PhysicsCollisions.cs:84` (`Name = PhybName.From(shape)`), reached from
  `Ui/PhysicsWindow.Areas.cs` ("Collision with") and `PhysicsFit.Apply` (skirt-ball add).
- **Problem:** names read from a file are Latin-1 and not validated (`PhybName.Read`), but when a shape is listed in
  a simulator for the first time its name is rebuilt from its text with `PhybName.From`. That rejects anything
  outside `' '..'~'` or longer than 31 characters, so it throws an ArgumentException inside `Change(...)` after
  `Checkpoint()`, during ImGui Draw.
- **Proposed fix:** reuse the shape's existing `PhybName` (look it up in `file.Collision` by text, or pass it in from
  the caller), which also keeps the original bytes.
- **Test:** a PhybFile with a capsule named from the bytes `C4 62 63 00`; `PhysicsCollisions.Add(file, [(0,0)], "Äbc")`
  throws today. After the fix it should succeed, and `ToBytes` should keep the original name bytes.

### S15-3 · low · fix here, not yet done: the torso clearance grid has no size cap

- **Where:** `Dalamud-Plugin/Services/Physics/PhysicsClearance.cs:41-56`.
- **Problem:** `sizeX*sizeY*sizeZ` 1 cm cells come straight from the torso points' bounding box with no limit. The
  product is unchecked int arithmetic, and two arrays of that size are allocated. One stray vertex weighted to
  `j_kosi`/`j_sebo_*` 5 m away gives a 500³ grid, about 1 GB, during "Fit to my body". At about 10 m the product
  overflows.
- **Proposed fix:** `checked` arithmetic, and throw `InvalidDataException("Your body's skin reaches too far to
  measure")` above about 8 M cells. Alternatively, drop torso points more than about 2 m from the hips before
  computing the bounds.
- **Test:** `PhysicsBody.Of` with a few torso points near the origin plus one at (5,5,5) owned by `j_kosi`; expect
  `PhysicsClearance.TorsoOf` to give a bounded result or a clean InvalidDataException.

### S15-4 · low · needs the game: a posed partner's skeleton is dereferenced without checks

- **Where:** `Dalamud-Plugin/Services/Physics/PhysicsLiveReader.cs:65-74` (ReadBody).
- **Problem:** the local player's skeleton is checked in ReadLocalPlayer (non-null, PartialSkeletons non-null,
  count 1..16). When another GPose actor is chosen, `skeleton->PartialSkeletons[0].GetHavokPose(0)` runs on it with
  none of those checks. This path is reached from the timeline's hand-tip check (`Plugin.cs`, `ReadHandTipsAsync`).
  A null dereference is an access violation, which takes the game down.
- **Proposed fix:**
  `if (skeleton == null || skeleton->PartialSkeletons == null || skeleton->PartialSkeletonCount is 0 or > MaximumPartials) return (null, "The character's skeleton isn't loaded.");`
- **Why not fixed:** whether `CharacterBase.Skeleton` is ever null while the draw object exists needs the game (a
  GPose partner caught mid-redraw). The guard itself is cheap and could be added on sight.

### S15: found sound

PHYB record sizes, offsets, bounds and the EPHB extension round trip; PhysicsDocument undo/dirty/save; the builder's
save path; the preview service's lifecycle; collision-bit renumbering; the fit maths (zero-length vectors are
guarded); Customize+ child propagation (same formula as Customize+); heels units, sign and attribute parsing (same as
Simple Heels); HeelsModelTag's string table, offset shifts and hash-checked undo; ImGui balance (ImRaii throughout);
the overlay skips points behind the camera; live reads run on the framework thread.

## Resuming locally

1. Check out `testing` (the review read `3e9ee3d`; `testing` has moved on since, so check each finding against the
   tip before fixing it). `git worktree add --detach ../ie-3e9ee3d 3e9ee3d` gives a read-only copy at the reviewed
   commit.
2. Fix S15-2 and S15-3 as described above, and add the S15-4 guard if you're happy to without a game check.
3. Review the remaining areas in the table, one reviewer (or Claude Code subagent) per area. The brief each reviewer
   followed is below. With Claude Code locally, the plugin and all four C# suites can build (Dalamud's assemblies are
   on your machine), so fixes can be tested properly rather than file by file.
4. Run the C# suites and the Blender suites (`Tests/README.md`) on the PHYB change above.

### Reviewer brief

> You are one of several reviewers, each owning one area. Review the code as of commit `3e9ee3d` in a read-only
> worktree; for each finding, check whether it is still present on the tip of `testing` and give the tip's file:line.
> Don't edit either tree. `docs/code-review-testing-branch.md` is an earlier review of 13 other areas: don't
> re-report anything listed there.
>
> Look for correctness defects with a concrete failing scenario, in this order: (1) damage to the user's files or
> mods (wrong bytes, lost edits, writes outside the mod folder, a save reported but not done, undo/restore of the
> wrong thing); (2) game crashes (native pointers without null checks, game objects / Havok / ClientStructs off the
> framework thread, unhandled exceptions in framework/draw callbacks, ImGui Begin/End or Push/Pop imbalance on an early
> return, ID collisions); (3) wrong results (maths, off-by-one, frame/time conversion, NaN, stale state after an
> actor/workspace/clip switch, async races with the UI); (4) untrusted input that can crash, hang or allocate without
> bound; (5) code that doesn't do what its doc comment, changelog or Tests/*.md checklist says. Leave out style,
> refactors, performance (unless it hangs the game) and speculation. Try to refute every candidate before reporting
> it. At most ~10 findings.
>
> Classify each finding as **FIX-HERE** (confirmable without the game: by reading, or a test of pure logic),
> **NEEDS-GAME**, or **DECISION**. Report each with: id, severity, class, title, where (at 3e9ee3d and at the tip),
> problem, scenario, proposed fix, test, confidence. Also list the files read and what was checked and found sound.
