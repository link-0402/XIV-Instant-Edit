# Code review of the `testing` branch

A review of the overall state of the code on `testing` as of commit `b99337f` (not of any single commit). Reviewers read the source in parallel, one area each; every finding below was traced to a concrete failing scenario by its reviewer and then re-checked here. Where the problem could be confirmed without the game, it was fixed together with a regression test. What could not be confirmed is listed for verification in the game or for a decision.

## Summary

- **81 findings** from 13 of 24 areas (0 high, 22 medium, 59 low), plus 2 more found while setting up the review and reading the reports.
- **Fixed: 45** (one of them only partly), each with a regression test unless the entry says why not.
- **Needs verification in the game or in Blender: 11.**
- **Needs a maintainer decision: 24.**
- **Confirmed but not fixed: 3.**
- No high-severity finding was reported: nothing found damages a user's files in an ordinary session. The most serious confirmed defects (all fixed): saving a material after removing or adding a texture slot on a read-only material wrote a material whose samplers point at the wrong textures (S14-1); a model-rewrite loop that never ends for some third-party `.mdl` files (S07-1); restoring refit hair could put the model back but leave the cropped texture (S16-1); and several ways a crafted or damaged file makes the plugin allocate gigabytes or spin (S09-1, S18-1, S18-2, S16-2). The medium findings that remain open are S08-1, S08-2, S08-3, S09-2, S13-1, S13-3, S21-3: decisions about behaviour, or a check that needs the game.

### Test status

- The Windows CI run for `b99337f` passed all four C# suites. This review's sandbox is Linux, where the same suites fail ~180 checks that depend on Windows path separators and COM; that failure set was saved before any change and compared after each batch of fixes: **it never changed**, and every new check passes.
- All 15 Blender/Python suites pass on Blender 5.2.0 (`bpy` module) before and after the fixes, as do the Substance Painter plugin's unit tests.
- The plugin builds with 0 errors and, after PRE-1, 0 warnings.
- Each new check was run against its fix reverted (or deliberately broken) to confirm that it fails without it, with these exceptions: S13-4 (its check follows the service's staging sequence step by step; the failure was seen with the old sequence, but it does not call `MashupService` itself), S21-2 (a constant shared between the two sides), and the fixes whose entry says there is no test. One new C# check had to be moved into a scenario of its own for this reason: on Linux the scenario class it first sat in stops early at an unrelated Windows-path failure, so it never ran there.

## Fixed in this review

Grouped by area. *Test* says what now covers the change.

### PRE-1 · low · New ImageSharp advisory not suppressed

- **Where:** Dalamud-Plugin/InstantEdit.csproj
- **Fix:** Suppressed GHSA-v76p-62qx-wwq2 (ImageSharp tiled-fax TIFF decoder) in InstantEdit.csproj next to its siblings and extended the comment; NuGet audit now reports 0 warnings.
- **Test:** Build (0 warnings). Every Image.Load/Identify in the plugin already passes StudioImages.Decoder (no TIFF module) - re-checked by grep.

### S17-OOS-painter-proxy · low · Substance Painter client went through system proxies

- **Where:** Tools/SubstancePainter/xiv_instant_edit/client.py
- **Fix:** Tools/SubstancePainter client.py sends through a ProxyHandler({}) opener like the Blender add-on and the plugin.
- **Test:** Tools/SubstancePainter/tests/test_plugin.py (test fails with the old urlopen).

### S01-1 · low · A .pose bone with a huge rotation becomes a zero quaternion, then NaN, instead of being skipped

- **Where:** Dalamud-Plugin/Services/Animations/Motion/PoseFiles.cs:63-65 (Bone), :83 (Values only checks IsFinite per component); consumed by PoseImport
- **Problem:** the class promises "a bone whose values can't be read is skipped; one of no length counts as none". A rotation whose components are finite but whose square overflows (|x| > ~1.8e19) has `LengthSquared() == +Inf`, which passes the `< 1e-12f` test; `Quaternion.Normalize` then returns (0,0,0,0). The next `Normalize` of it (in `MotionEvaluator.Relative` while making the bone local) gives NaN.
- **Fix:** PoseFiles.Bone normalises the rotation in double (a component too large to square no longer yields a zero quaternion and then NaN) and skips a bone farther than 1000 m from the origin.
- **Test:** PoseFileFixture (Rotation 1e20, bone 5000 m away).

### S01-4 · low · LivePoseLayer.ReadFile throws InvalidOperationException for valid JSON of the wrong shape, contrary to its contract

- **Where:** Dalamud-Plugin/Services/Animations/Motion/LivePoseLayer.cs:137-143 (entry.TryGetProperty, id.TryGetProperty, nameValue.GetString() on a non-
- **Problem:** the doc says "Throws InvalidDataException for anything else", but `JsonElement.TryGetProperty` throws `InvalidOperationException` when the element is not an object, and `GetString()` when it is not a string/null. Only `JsonException` is translated.
- **Fix:** LivePoseLayer.ReadFile checks the JSON value kinds and throws InvalidDataException (its documented contract) instead of InvalidOperationException.
- **Test:** TimelineToolsFixture (array entry that is a number, bone name that is a number).

### S03-1 · medium · AnimationSkeletonIndex shares unsynchronised Dictionaries between concurrent ResolveAsync callers

- **Where:** Dalamud-Plugin/Services/Animations/AnimationSkeletonIndex.cs:25-26 (descriptions, files), :502-555 (Describe), :724 (files.Clear() / loadedR
- **Problem:** `descriptions` and `files` are plain `Dictionary` instances read, written and cleared in `Describe`/`ResolveAsync` with no lock (`libraryLock` only guards `sessionLibrary`/`sessionLibraryBuild`). `ResolveAsync` has several independent entry points that run on thread-pool continuations: the observer's `MatchAsync`, a second `MatchAsync` started by `RefreshSkeletonMatches` without waiting for the first (`matching = MatchAsync(...)` there has no `IsCompleted` check, unlike line 475), the Send-to-Blender dialog (`AnimationFiles.ResolveAsync`), and `LoadFaceAsync`. A library rebuild (`BuildSessionLibraryAsync`) also calls `Describe` for thousands of files.
- **Fix:** AnimationSkeletonIndex: `descriptions` and `files` are ConcurrentDictionary (the only operations used are TryGetValue, the indexer, Count and Clear).
- **Test:** none
- **Note:** No test: the corruption is timing-dependent and the swap is mechanical. The reviewer classed it NEEDS-GAME because whether concurrent ResolveAsync calls collide in practice depends on timing in the game.

### S03-2 · low · Automatic rollback of a failed commit is skipped when the journal cannot be saved

- **Where:** Dalamud-Plugin/Services/Animations/AnimationCommitService.cs:410-416
- **Problem:** the failure handler is `journal.State = "RecoveryRequired"; journal.Message = ...; store.Save(journal);` followed by `try { await UndoFilesCoreAsync(journal); }`. `store.Save` is outside that inner try, so if it throws (disk full, antivirus/backup tool holding `journal.json`, config folder on a network drive) the exception leaves the catch block and `UndoFilesCoreAsync` never runs. The same applies to the unguarded `store.Save(journal)` inside the nested `catch (Exception rollback)`, which replaces the `IOException(journal.Message, error)` the caller expects.
- **Fix:** AnimationCommitService: the failure handler saves the journal through TrySave, so a failed save no longer skips the rollback of the files already changed or replaces the original error.
- **Test:** none
- **Note:** No test: it needs a commit that fails while the journal folder is unwritable.

### S03-4 · low · Job folders that never got a journal.json are never cleaned up

- **Where:** Dalamud-Plugin/Services/Animations/AnimationJournalStore.cs:69-72 (if (!File.Exists(path)) continue;); producers AnimationCommitService.cs:3
- **Problem:** every job creates `AnimationEdits/<guid>` early (bake output, `rebuilt-base`, staged copies) but writes `journal.json` only at the very end of `PrepareAsync`. Any failure or cancellation before that (bake validation failure, "Choose the skeleton...", actor changed, `SharesTarget`/"already exists" exceptions after staging, user Cancel) leaves a folder without `journal.json`. `Cleanup` skips such folders, and `Load` ignores them, so they are never removed. A folder with a staged sound or model can be tens of MB.
- **Fix:** AnimationJournalStore.Cleanup removes a job folder that never got its journal.json once its write time is older than the retention.
- **Test:** JournalCleanupFixture (stale folder removed, recent folder and non-job folders kept).

### S03-5 · low · RuntimeCaptureCache.BindingFingerprint dereferences Binding.Animation without a null check

- **Where:** Dalamud-Plugin/Services/Animations/AnimationRuntime.cs:81-86 (called from :279-281)
- **Problem:** `AnimationRuntime.Capture` checks `control->Binding.ptr != null` and then passes it to `cache.BindingFingerprint`, which reads `animation->Duration`, `animation->Type`, `animation->NumberOfTransformTracks` from `binding->Animation.ptr` before `AnimationNative.Fingerprint` (which does null-check) is reached. A null `Animation` is a native access violation, which .NET cannot catch: it takes the game down. `LoopFadeWatch.cs:91` shows the author knew to test `Binding.ptr->Animation.ptr == null` for the same controls.
- **Fix:** AnimationRuntime.Capture skips controls whose binding has no animation, as LoopFadeWatch already does, before BindingFingerprint dereferences it.
- **Test:** none
- **Note:** No test: native memory. The reviewer classed it NEEDS-GAME because whether such a control exists in practice needs the game; the guard is one condition.

### S03-6 · low · AnimationClipExport has no up-front memory limit, unlike the bake session

- **Where:** Dalamud-Plugin/Services/Animations/AnimationClipExport.cs:56-73 (Session constructor), :81-98 (SampleBatch), :100-107 (Build)
- **Problem:** the bake `Session` refuses clips whose samples would exceed `AnimationPap.MaxFileSize` (`AnimationBakeService.cs:339`), but the export `Session` only relies on `SampleCount` (up to 216001 samples) and then keeps a `float[bones*10]` per sample in `frames`, plus a second full copy in `Build`. `AnimationTakeFormat.Write` rejects takes over 192 MiB only after everything was sampled and copied.
- **Fix:** AnimationClipExport.Session refuses a clip whose samples would exceed Blender's 192 MiB body limit before sampling it, with the same message wording as the take writer.
- **Test:** none
- **Note:** No test: the session needs a native Havok document.

### S07-1 · medium · ModelMaterialRewriter.Write loops forever when the model's string block size is not a multiple of 4

- **Where:** Dalamud-Plugin/Studio/Services/ModelMaterialRewriter.cs:255-257 (loop), :373-382 (Parse accepts any StringSize); reached from ModBuilder.cs:
- **Problem:** `while (size % 4 != 0 || (size - StringSize + grown) % 16 != 0) size++;` needs `size == 0 (mod 4)` and `size == StringSize - grown (mod 16)` at once. `grown` is a multiple of 4, so if the original `StringSize` is not a multiple of 4 the two conditions contradict and the loop never ends. `Parse` does not check the alignment, and the loop ignores any cancellation token.
- **Fix:** ModelMaterialRewriter.Write: dropped the unsatisfiable `size % 4 == 0` loop condition; the padding now only has to make the file shift a multiple of 16 (identical result for 4-aligned blocks).
- **Test:** MeshMaterialScenarios.UnalignedStringBlockStillRewrites (10 s timeout; TestModels.TwoMeshModel gained `alignStrings`).

### S07-2 · medium · Texture cache entries are written non-atomically and trusted without validation, so a truncated .tex can be written into the user's mod

- **Where:** Dalamud-Plugin/Studio/Services/TextureCache.cs:92-108 (Store), :111-128 (Adopt), :55-89 (Get/PathOf); consumers ModWriter.cs:965-971, :868-8
- **Problem:** `Store` does `File.WriteAllBytes(path, data)` straight onto the final cache name and, on any exception (disk full, I/O error), returns null but leaves the partly written file in place. `Adopt` moves from `%TEMP%` with `File.Move`, which is a copy+delete when the cache folder is on another volume. `Get`/`PathOf` accept any existing file under the key (source path+size+mtime+settings) with no size or header check. The sibling cache `AlphaNormal.Save` already uses temp file + move for exactly this reason.
- **Fix:** TextureCache: Store/Adopt write a .part file in the cache folder and rename it into place; Get/PathOf drop an entry shorter than its own TEX header describes; Prune sweeps stale .part files.
- **Test:** TextureCacheScenarios (whole, truncated, stub, padded, adopt, failed adopt, part-file sweep).

### S07-6 · low · Uncompressed 32-bit DDS without an alpha mask is imported with whatever is in its fourth byte

- **Where:** Dalamud-Plugin/Studio/Services/TextureConverter.cs:407-414 (pfFlags, aMask read but unused), :454-466.
- **Problem:** for `fourCC == 0` and 32 bpp the reader only decides RGBA vs BGRA from `rMask`. A DDS saved as X8R8G8B8/B8G8R8X8 (`aMask == 0`, no DDPF_ALPHAPIXELS) carries unspecified data in the alpha byte; it is copied straight into the B8G8R8A8 texture. If a writer left 0 there, the texture is fully transparent in game.
- **Fix:** TextureConverter.ReadDds: a 32-bit DDS without an alpha mask (legacy aMask == 0, or DXGI B8G8R8X8) is read with alpha 255 instead of whatever its fourth byte holds.
- **Test:** DdsAlphaScenarios (X8R8G8B8, A8R8G8B8, RGB-ordered with/without mask, DXGI 87/88).
- **Note:** Behaviour follows the DDS specification; no real-world file that exhibited the problem was available.

### S08-4 · low · Cancelling "Move files" mid-copy leaves the copied files behind and records nothing, although the UI says it can be restored

- **Where:** Dalamud-Plugin/Studio/Services/CleanUp/FileLocations.cs:146 (token.ThrowIfCancellationRequested() inside the copy loop) vs the catch at :182
- **Problem:** an `OperationCanceledException` bypasses the catch, so the targets already copied (`created`) are neither deleted nor journaled (the `BackupOperation` is never committed). They stay in the mod as unreferenced copies (and show up as "File nothing uses" later). The Stop button's tooltip promises "files already changed stay changed and can be restored".
- **Fix:** FileLocations: cancelling mid-copy deletes the copies made so far before rethrowing.
- **Test:** none
- **Note:** No deterministic test: the cancellation point is inside the copy loop.

### S09-1 · medium · GEOM face list grows to 4x the resource size: a ~250 KB forged package can allocate >2 GB

- **Where:** Dalamud-Plugin/Studio/Services/Sims/SimsGeom.cs:332-365 (ReadFaces, var indices = new List<int>() at 338, only bound at 348), reached from S
- **Problem:** each face block is only checked against the bytes left in the resource (`indexCount * bytesPerIndex <= remaining`). With `bytesPerIndex = 1`, one byte becomes a 4-byte `int` in a `List<int>` that doubles its backing array, then `ToArray()` copies it again. `MaxResourceSize` (256 MB) and the zlib ratio check (1032:1) both pass, so the "forged size never allocates as-is" defence in `DbpfPackage` is bypassed here.
- **Fix:** SimsGeom.ReadFaces caps the total face-point count (6,000,000) before reading each block.
- **Test:** SimsImportScenarios (forged 7M-index GEOM refused cheaply; 300k-index GEOM still parses; the test helper now writes 1-byte indices).

### S09-3 · low · Unity YAML regexes are quadratic on runs of blank lines and cannot be cancelled

- **Where:** Dalamud-Plugin/Studio/Services/Unity/UnityYaml.cs:23 (TextureTypeLine), :26 (NameLine), :29 (ShaderLine), :33 (TextureEntry); called after t
- **Problem:** patterns start with `^\s*` / `^\s+` under `RegexOptions.Multiline`, and `\s` also matches newlines. At every line start inside a whitespace-only run the engine consumes the rest of the run and then fails, so a run of N blank lines costs O(N^2). There is no match timeout.
- **Fix:** UnityYaml line patterns start with `[ \t]*` instead of `\s*`, so a run of blank lines is no longer quadratic.
- **Test:** ImporterRobustnessScenarios (100,000 blank lines read in well under 2 s; LF and CRLF material/meta still read).

### S09-4 · low · Non-ASCII digits in a game path make `int.Parse` throw and disable the library-wide "what mods change" scan

- **Where:** Dalamud-Plugin/Studio/Services/Library/ChangesIndex.cs:27,30 (regexes with \d{4}), :157 and :165 (int.Parse(id)); the throw escapes LibraryS
- **Problem:** in .NET `\d` matches any Unicode decimal digit (full-width, Arabic-Indic...), but `int.Parse` accepts ASCII digits only, so `FormatException`.
- **Fix:** ChangesIndex regexes use [0-9] instead of \d.
- **Test:** ImporterRobustnessScenarios (full-width digits in a game path no longer throw).
- **Note:** Not done: a per-mod try/catch around Classify in LibraryScans.

### S09-5 · low · Unity extraction does not neutralise Windows reserved device names

- **Where:** Dalamud-Plugin/Studio/Services/Unity/UnityPackageReader.cs:277-291 (SafeRelative) and :137 (package folder name), both via PathNormalizer.Sa
- **Problem:** `SafeSegment` replaces invalid characters and trims dots/spaces but not `CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9` (with or without an extension). The Sims importer handles this (`MakeSafeName` + `ReservedNames`), the Unity importer does not.
- **Fix:** PathNormalizer.SafeSegment puts a '_' in front of Windows device names (also covers the S07 out-of-slice note about option/group folders).
- **Test:** PathRuleScenarios.
- **Note:** Windows behaviour with such names itself could not be observed here.

### S13-2 · medium · Cleanup keeps one full byte[] per mapping, not per distinct file (partly)

- **Where:** Dalamud-Plugin/Services/PenumbraMods/ModCleanup.cs:107-121 (File.ReadAllBytes(source) inside the per-mapping loop; every bytes is stored in 
- **Problem:** N mappings of the same large file hold N copies in memory (`sameContent.Add((mapping, bytes))`), and all distinct files of the mod are resident at once until the writes finish. Memory = sum of file sizes over mappings. The same pass also copies the entire mod into a sibling snapshot folder.
- **Fix:** ModCleanup.NormalizeAndDeduplicateMod: each physical file is read and hashed once and one byte[] is kept per distinct content (the code only ever used the first mapping's bytes), so memory no longer scales with the number of mappings.
- **Test:** ModCleanupMemoryScenarios (12 options mapping one 6 MB file allocate ~6 MB).
- **Note:** All distinct files of the mod are still resident while cleanup runs and the snapshot still copies the whole mod; streaming the writes safely needs care about overwriting a file that another mapping still reads.

### S13-4 · low · New-mod mashup of a hair/face model with legacy Unknown-slot ATR entries always fails staged validation

- **Where:** Dalamud-Plugin/Services/PenumbraMods/MashupService.cs:825-830,838-860, AttributeGroupService.cs:819-844 (ManipulationsWithAtrDefaults), Mash
- **Problem:** `outputManipulations` (legacy `Slot:"Unknown"` Atr entries rewritten to `Hair`/`Face`, defaults appended) is used both for the output manifest and as the expected value in `ValidateStagedMashupMod`, but the staged `DefaultData` is built from the raw `activeContext.ResourceManifest.Manipulations` (`CreateMashupDefaultData`, line 843-846) and `WriteAttributeGroups` leaves the legacy entries alone. The two arrays differ.
- **Fix:** MashupService (new-mod path) stages the default data from the converted manipulations it validates against (outputManipulations) instead of the source's raw ones.
- **Test:** MashupLegacyAtrScenarios (reproduced the 'Meta Manipulations are incomplete' failure before the change; follows the service's staging sequence step by step, it does not drive MashupService itself).

### S14-1 · medium · Remove/Add texture slot on a read-only (unparsed) material writes samplers that point at the wrong textures

- **Where:** Dalamud-Plugin/Ui/Material/MaterialWindow.Textures.cs:75-100 (add-slot combo) and :120-128 (trash button), not gated by doc.Editable; Dalamu
- **Problem:** when the shader section fails the "everything accounted for" check (bytes left over after the value block, or a constant outside the value block, or a truncated list), `TryParseTail` has already filled `Samplers`, so `MaterialDocument.Slots` is non-empty and the Textures column shows slot cards with Remove and the "Add a texture slot" combo (shader package is known). `DrawMain` says "Only its texture paths can change", but only the path edit is safe: Remove deletes the texture entry from the list that is written while the raw `Tail` still holds the old sampler -> texture indices, and Add appends a texture entry whose sampler is never written. Setting a path on a sampler without a texture also only changes the (unwritten) parsed copy.
- **Fix:** MaterialDocument: for a material whose shader section was not fully read, WithoutSlot and WithAddedSlot change nothing and WithSlot only changes a texture's path (the sampler table is written raw); the Textures column no longer offers Remove or Add for it.
- **Test:** MaterialReadOnlyScenarios (read-only material built from the reviewer's repro: removing/adding a slot is a no-op, a path edit works and leaves the sampler table byte for byte; a fully read material still gains and loses slots).

### S14-2 · low · Half-float packing overflows to +/-Infinity instead of clamping to 65504

- **Where:** Dalamud-Plugin/Services/Materials/ColorTableRows.cs:64-65 (HalfPacking.Set: (Half)value), fed by Dalamud-Plugin/Ui/Material/MaterialWindow.R
- **Problem:** C# float -> Half conversion of |x| > 65504 gives +/-Infinity. The colour fields use ImGui HDR (no upper clamp, typed values allowed) and store `edit * |edit|`, the raw-value and matrix fields are unbounded drags; nothing clamps before `Set`, so a typed edit of 300 (stored 90000) writes 0x7C00 into the table file.
- **Fix:** HalfPacking.Set clamps to +/-65504 and maps NaN to 0 before converting to half.
- **Test:** MaterialReadOnlyScenarios.HalfFloatsClamp.

### S16-1 · medium · Restoring a refit group is not all-or-nothing: a changed texture is kept while its model and materials go back to the original UVs

- **Where:** Dalamud-Plugin/Services/TextureCompression/TextureCompressionService.cs:1044-1090 (RestoreRefitAsync), result counted at :1013-1017
- **Problem:** `RestoreRefitAsync` decides per file. A file whose current hash is neither the original nor what the refit wrote is added to `changed`/`remaining` and skipped (`:1062-1067`), but the other files of the same group (models, materials, other textures) are still written back (`:1075`). The group record exists precisely so models, materials and textures "are put back together" (`RefitGroup` doc), and the forward direction (`ApplyRefitAsync`) is all-or-nothing with rollback; the reverse is not.
- **Fix:** TextureCompressionService.RestoreRefitAsync now plans first (new PlanRefitRestoreAsync: every file is checked for original / what the refit wrote / changed / missing / bad backup) and writes nothing when any file of the group is blocked; the group stays whole and the blocking files are reported.
- **Test:** TextureCompressionScenarios.CheckRefitRestoreIsWholeAsync (edited texture, missing backup, missing file, already-original files).
- **Note:** Behaviour change to be aware of: a hairstyle whose cropped texture the user edited is now left as it is (with the reason on the card) instead of being half restored. The alternative the reviewer mentioned is to skip only the files that depend on the blocked one.

### S16-2 · low · `SeamSpace` loops forever (and eats memory) when a triangle has a non-finite or huge coordinate

- **Where:** Dalamud-Plugin/Services/NeckSeam/SeamSpace.cs:33-48 (constructor), :66 (Key), :69-92 (Nearest), :96-141 (Hits via Key(origin))
- **Problem:** `Key` casts `MathF.Floor(p / 0.01f)` to `int`; in .NET 10 that saturates (+Inf -> `int.MaxValue`). The span check `high.X - low.X > MaxSpan` is plain `int` arithmetic and overflows when `low.X` is negative and `high.X == int.MaxValue`, giving a negative number, so the triangle is not skipped. The loops `for (x = low.X; x <= high.X; x++)` then never end (`x <= int.MaxValue` is always true) and fill `_cells` until OOM. `Nearest(p, ...)` has the same unbounded loop for a query point at +Inf.
- **Fix:** SeamSpace skips triangles with non-finite or >1000 m coordinates, computes the span in long, and Nearest/Hits answer null/false for such points.
- **Test:** SeamSpaceScenarios (triangle with a +Inf corner, queries from infinity, each under a 10 s limit).

### S17-1 · low · `//host/share/x.mdl` passes the "local absolute .mdl" guard on `filePath`

- **Where:** Dalamud-Plugin/Services/ExportServer.cs:1603-1611 (StageExportFileAsync); same raw-string test in Services/PenumbraMods/ModPaths.cs:74 (IsSa
- **Problem:** UNC is refused only with `filePath.StartsWith("\\\\")`. The check runs on the unnormalised string, but Windows (`Path.GetFullPath`) turns `//host/share/a.mdl` and `/\host\share\a.mdl` into `\\host\share\a.mdl`. Neither is caught, the extension and `.`/`..` checks pass, and the very next call, `File.Exists(fullPath)`, touches the network.
- **Fix:** New PathRules.IsNetworkPath treats / and \ alike; used by ExportServer.StageExportFileAsync, ModPaths.IsSafeLocalModelPath and TextureFiles.EnsureLocalPath (and the add-on's parse_import_files / Open Mod Folder guard).
- **Test:** PathRuleScenarios.NetworkPathsInEverySpelling; bridge_regression and smoke_export for the add-on side.

### S17-2 · low · Windows reserved device names are accepted as variant names (they become file names)

- **Where:** Dalamud-Plugin/Services/PathRules.cs:40-45 (IsSafeVariantName), reached from ExportServer.cs:1368, ModPaths.cs:89, ModelExportService.cs:97/
- **Problem:** the validator rejects invalid file-name characters, `.mdl` suffix and `.`/`..`, but not `CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9` (with or without an extension). `ModPaths.IsSafeNewModName` (ModPaths.cs:184-197) does reject exactly these, so the omission looks accidental.
- **Fix:** New PathRules.IsReservedDeviceName; PathRules.IsSafeVariantName rejects device names; ModPaths.IsSafeNewModName now shares the helper.
- **Test:** PathRuleScenarios.DeviceNamesAreNotFileNames.

### S17-4 · low · Durable state is "atomically" replaced without flushing, so a crash can leave zero-length files; one of them disables texture editing

- **Where:** Dalamud-Plugin/Services/TextureFiles.cs:465-472 (WriteJson: TextureSessions.json and every session.json), EstEntryStore.cs:64-83, HairSkelet
- **Problem:** these four write with `File.WriteAllText(temp)` and `File.Move(temp, path, true)` with no `Flush(true)`. After an OS crash or power loss shortly after a save, NTFS can keep the rename but not the data, leaving an empty file.
- **Fix:** New DurableFile.WriteAllText/WriteAllBytes (write + FileStream.Flush(true)) used before the rename in TextureFiles.WriteJson, EstEntryStore, HairSkeletonStore, ModelBackupStore sidecars, ModMetadata.WriteJsonAtomic (every meta.json write) and HairSkeletonFiles.WriteAtomic.
- **Test:** none
- **Note:** Crash durability cannot be unit-tested; existing suites cover the write paths.

### S18-1 · medium · A crafted MDL makes ModelMeshReader allocate gigabytes (no aggregate cap on submesh/vertex copies)

- **Where:** Dalamud-Plugin/Services/Previews/ModelMeshReader.cs:195-233 (per-mesh vertex arrays, one int[] per submesh); weaker twin in ModelGeometryRea
- **Problem:** each submesh is only checked as `start + count <= indexTotal` (the whole LOD-0 index buffer, line 222), and each mesh's stream only against the file length (`DecodeStream`, line 399). Submesh ranges may repeat or overlap, a mesh may list up to 65535 submeshes, and meshes may share one vertex stream, so decoded memory is not bounded by the file size or by the `MaxVertexBufferBytes` check (which only compares a header field).
- **Fix:** ModelMeshReader: ceiling on total decoded vertices (2,000,000) and on emitted submesh indices (twice the index buffer + 64Ki).
- **Test:** PainterScenarios (3000 submeshes over a 3000-index buffer refused; a partitioned mesh reads whole).

### S18-2 · low · UvCoverage's "corrupt triangle" guard overflows int, so large finite UVs hang the Painter send

- **Where:** Dalamud-Plugin/Services/Painter/UvCoverage.cs:49-59 (called from PainterJobService.cs:508).
- **Problem:** `maxX - minX > 4 * width` is evaluated in `int`. When the extent exceeds 2^31 (UV span of about +-5e5 at 2048 px, or any huge finite value that saturates the `(int)` cast), the subtraction wraps to a negative number, the guard passes, and the loops run about 4 billion times per row.
- **Fix:** UvCoverage.Rasterize judges the triangle extent on the floats and skips triangles beyond 1e7 texels, instead of on wrapped ints.
- **Test:** PainterScenarios.CoverageSurvivesAbsurdUvs.

### S18-3 · low · Blender detection discards blender-launcher.exe candidates, so the file-association fallback never finds anything

- **Where:** Dalamud-Plugin/Services/Setup/BlenderSetup.cs:95-100 (filter on blender.exe), with the candidates added at 89 and 95; FromExecutable (127-12
- **Problem:** `Detect` keeps only paths whose file name is `blender.exe` before calling `FromExecutable`, but `FileTypeExecutable("blendfile")` returns the registered open command, which for Blender is `...\blender-launcher.exe "%1"` (Tests/ExportContextRegression/ToolSetupScenarios.cs:48 asserts exactly that), and a `DisplayIcon` can name the launcher too. Those candidates are dropped, so the mapping to the sibling blender.exe in `FromExecutable` is dead code on this path.
- **Fix:** BlenderSetup.Detect keeps blender-launcher.exe candidates (FromExecutable maps them to blender.exe) and removes duplicates.
- **Test:** ToolSetupScenarios.BlenderLauncherIsACandidate (filter only; Detect itself reads the registry).

### S18-5 · low · PreviewModStore.Add/Remove throw after changing the list, and the Penumbra preview mod it records is left behind

- **Where:** Dalamud-Plugin/Services/PreviewMods/PreviewModRecords.cs:177-201 (Save has no I/O handling); caller Services/NeckSeam/NeckSeamService.cs:268
- **Problem:** the preview mod is already created and enabled in Penumbra when `Add` runs. If writing `NeckSeamPreviews.json` fails (read-only or locked config folder, disk full, AV lock on the `.tmp`), `Add` throws after `_previews.Add`; the caller does not delete the mod, so the user sees a failed preview while an enabled "Skin Seam Preview" mod stays in the collection, tracked only in memory and forgotten after the next reload.
- **Fix:** PreviewModStore.Save no longer throws: an I/O failure is reported through LoadError and the preview stays tracked in memory; the file is written with DurableFile.
- **Test:** PreviewModScenarios (list folder is a file: Add does not throw, error reported, preview kept).
- **Note:** The UI already shows LoadError; the preview mod itself is not deleted when the record cannot be saved.

### S21-1 · medium · "Open Mod Folder" hands a .blend-stored string to the OS opener without checking it is a folder

- **Where:** Blender-Addon/instant_edit/ops.py:1911-1938 (OpenContextFolder.poll/execute); value comes from context.py:633 (source_mod_root_path, a custo
- **Problem:** `bpy.ops.wm.path_open(filepath=folder)` opens any existing path with the shell's "open" verb, not just directories. The only gate is `if not ref.source_mod_root_path`.
- **Fix:** ops.mod_folder_to_open: Open Mod Folder only opens an existing local directory (not a file, not a UNC path in either slash spelling).
- **Test:** bridge_regression (mod_folder_to_open cases).

### S21-2 · medium · `/import/files` rejects more than 64 files but the plugin sends any number

- **Where:** Blender-Addon/instant_edit/server.py:51,1186-1187 (MAX_IMPORT_FILES = 64); sender Dalamud-Plugin/Ui/Main/ModelSender.cs:199-214 (SendFiles, 
- **Problem:** contract mismatch. The stash "Send N models to Blender" button posts every marked model in one request; the add-on answers 400 `invalid_files` "files must list 1 to 64 paths" and imports nothing. Nothing in the C# side limits marking or chunks the request (and chunking would break the "models with a matching armature share one" behaviour, which is per request).
- **Fix:** The plugin refuses to send more than BlenderClient.MaxImportFiles (64) models with a clear message; the limit is now one number in Tests/TestSupport/context-protocol.json checked on both sides.
- **Test:** ContextProtocolScenarios and bridge_regression (limit agreement).
- **Note:** Raising the add-on's limit instead (or chunking) remains a product choice.

### S21-5 · low · Keying an animation onto an armature in NLA tweak mode leaves an orphan fake-user action and reports a generic error

- **Where:** Blender-Addon/instant_edit/animation.py:735-746 (animation_data.action = action sits before the try: whose handler removes the new action).
- **Problem:** in tweak mode `AnimData.action` is read-only, so the assignment raises AttributeError after `bpy.data.actions.new(...)` and `action.use_fake_user = True` (and possibly `previous_action.use_fake_user = True`). The rollback block does not cover this statement, so the new action stays in the file forever, and the plugin gets the generic "unexpected error" instead of a message that says what to do.
- **Fix:** animation.apply_take refuses an armature in NLA tweak mode (code animation_target_tweak_mode, with the remedy) before creating any action.
- **Test:** animation_regression.run_failure_cases (tweak-mode armature; no action left behind).

### S21-6 · low · Hostile or malformed numbers/JSON escape the request parsers and drop the connection instead of a 400

- **Where:** Blender-Addon/instant_edit/animation.py:129-131 (_transforms -> np.asarray(values, dtype=float64)), :209 (np.asarray(times, ...)), animation
- **Problem:** a JSON integer with 400 digits makes numpy/`math.isfinite` raise OverflowError; a deeply nested body makes `json.loads` raise RecursionError. Neither is a `BridgeRequestError`, so the handler thread dies with a traceback on Blender's console and the client gets an empty reply (no diagnostic report, no structured error). `skeleton.parse_skeleton` and `character._offset` already wrap OverflowError; these do not. The `/import` branch is fine because it has a catch-all.
- **Fix:** OverflowError/RecursionError from numpy conversions, math.isfinite and json.loads are turned into the normal 400 rejections in the animation, animation-read and import-files parsers.
- **Test:** animation_regression and smoke_export (400-digit integers, 20,000-deep nesting).

### S21-7 · low · A client-supplied `skeletonPath` survives `/import` validation and is read by the queued import

- **Where:** Blender-Addon/instant_edit/server.py:976-991 ({**data, ...} keeps unknown keys), :265-269 (_stage_import_skeleton returns the other keys unc
- **Problem:** `skeletonPath` is meant to be written only by `stage_skeleton` inside the cache job, but any key in the request body reaches `_run_queued_import`. Ran `_validate_import` + `stage_import` + `_stage_import_skeleton` with `"skeletonPath": "/etc/passwd"`: it comes out unchanged.
- **Fix:** _validate_import drops a client-supplied skeletonPath; only stage_skeleton may set it.
- **Test:** bridge_regression.

### S21-10 · low · Changing the cache directory while a job is outstanding leaks that job for the whole session

- **Where:** Blender-Addon/instant_edit/cache.py:246-285 (_owned_job, finish_job), :75-107 (configure_cache), :299-320 (clean_cache skips _active_jobs).
- **Problem:** `_owned_job` requires the path to be under the current `cache_root()`. After the plugin posts a different `cacheDirectory`, `finish_job(old_job)` returns False before it discards the job from `_active_jobs`, so the directory is not removed, stays "active", and even switching back leaves `clean_cache` skipping it. Model copies (up to 512 MiB) and preview bundles (up to 1 GiB) stay on disk until Blender restarts.
- **Fix:** cache.finish_job: a job whose cache folder was changed mid-run is finished (flag cleared, removed when auto-cleanup is on) in the folder it lives in, using the same marker/UUID/kind ownership checks against that folder.
- **Test:** cache_regression (moved cache folder, auto-cleanup on and off, impostor layout left alone).

### S22-1 · medium · Simple Export with an empty Export Folder writes into Blender's working directory

- **Where:** Blender-Addon/operators.py:333-336 (XIVIE_OT_simple_export.execute); default in Blender-Addon/properties.py:73; button always enabled at Ble
- **Problem:** `Path(bpy.path.abspath("")).resolve()` is the process working directory, and `.is_dir()` is True, so the "Choose an existing export folder." guard never fires for the default empty setting (or for `//` in an unsaved .blend, which `abspath` also turns into `""`). `backups.target_folder` handles the empty value (`backups.py:51-53`), the operator does not.
- **Fix:** XIVIE_OT_simple_export: an empty Export Folder (or '//' in an unsaved file) is refused with 'Choose an existing export folder.' instead of resolving to Blender's working directory.
- **Test:** sizes_regression (empty folder, working directory left empty).

### S22-2 · medium · FBX/glTF Simple Export includes visible meshes outside the chosen "Parts" scope

- **Where:** Blender-Addon/mesh/export.py:579-601 (use_selection: False, use_visible: True for both), export.py:639-652 (FileExport.export_template calls
- **Problem:** for MDL the writer receives the explicit object list, but FBX and glTF are written by Blender's scene exporters, which take every visible object. `SceneHandler` hides only the originals of `export_objects`, so a mesh excluded by "Visible except mannequin" or "Context collection only" stays visible and is written into the file (along with any visible empties, lights, cameras, other armatures).
- **Fix:** mesh/export.py: FBX and glTF exports hide the visible meshes that are not part of the export for the duration of the write (_hidden_outside_export), then restore them.
- **Test:** smoke_export (an FBX of one mesh with two more visible holds exactly one mesh; the others are visible afterwards).
- **Note:** Only meshes are hidden. Other visible objects (lights, cameras, empties, other armatures) are still written by Blender's scene exporters; see the observation under S22.

### S22-3 · medium · Mesh Groups panel (and Material dialog) raise when any visible mesh sits in two Instant Edit collections

- **Where:** Blender-Addon/materials.py:998 (shared_base_contexts), :1036 (material_sources), :1049-1050 (same_context_objects), :1063 (material_suggesti
- **Problem:** `context_id_for_object` raises `ContextValidationError` for an object linked to more than one Instant Edit collection ("remain ambiguous and are rejected"). Other helpers deliberately guard this (`mesh_part_instance_key`, `_plan_game_hidden_parts.context_of`), but these do not.
- **Fix:** materials.py: the panel and dialog helpers use _context_of, which answers '' for an object linked to several Instant Edit collections; the export path keeps the strict check.
- **Test:** bridge_regression (object in two collections: shared_base_contexts no longer raises).

### S22-4 · medium · Simple Import failure cleanup removes nothing when the importer raises part-way

- **Where:** Blender-Addon/operators.py:545-599 (XIVIE_OT_simple_import._import_file); same pattern in operators.py:826-832 (Import Backup removes object
- **Problem:** the docstring says it "removes what it created and raises", but `imported_objects` is still `[]` when `ModelImport.from_file` raises (`imported_objects = list(created_objects or imported)` runs only after it returns), and for FBX/glTF `imported_objects = tracker.created` runs only after the `with` block exits normally. `_simple_import_remove_objects([])` is then a no-op.
- **Fix:** operators._import_file: the list the MDL importer appends to is the one the failure handler removes, and the FBX/glTF handler uses the tracker's list, so a failed import removes the objects and meshes it had made.
- **Test:** skeleton_regression (importer that fails after creating an object).
- **Note:** Import Backup (operators.py ~826) has the same pattern for meshes and armatures and was not changed.

### S23-1 · medium · `clean_material_path` turns full game material paths into "/"-prefixed (broken) ones on export

- **Where:** Blender-Addon/io/model/exp/validators.py:17-26 (used by exp/constructor.py:24-34 and materials.py:833,900,989)
- **Problem:** Any material path not starting with "/" gets "/" prepended. In an MDL a leading "/" means "relative to the model's own material folder"; full game paths (`bgcommon/...mtrl`, `chara/monster/.../mt_x.mtrl`) must stay without it. The importer stores the raw MDL string in `xiv_material` (`importer.py:340`), so an unchanged re-export rewrites the reference.
- **Fix:** clean_material_path adds the leading '/' only to a bare file name; a path with a directory in it (a full game path) is left as the model had it.
- **Test:** bridge_regression (bare names, .001 suffix, Context tag, full chara/ and bgcommon/ paths).
- **Note:** Whether the game would accept a '/'-prefixed full path was not testable; the change makes re-export keep what the model had.

### S23-3 · low · MDL is written in place, so a failed write truncates the user's existing file

- **Where:** Blender-Addon/xivpy/model/file.py:284-288 (to_file), called from io/model/exporter.py:91
- **Problem:** the comment says the data is serialised and validated first so "a failure can't leave the target truncated", but the write itself is `open(path, 'wb')` + `write`, not temp file + `os.replace`. Disk-full, a killed Blender or antivirus interference mid-write leaves a partial MDL at the target. Backups before overwrite are off by default (`properties.py` `backup_models_on_export` default False).
- **Fix:** XIVModel.to_file writes a temporary file beside the target, flushes it and swaps it in with os.replace; the temporary file is removed on failure.
- **Test:** bridge_regression (stub model: replaced file, nothing left over; failing replace leaves the old file and no temp).

### S23-4 · low · Exporter writes a zero-LOD, zero-mesh MDL without error when no mesh qualifies for LOD 0

- **Where:** Blender-Addon/io/model/exporter.py:56-72
- **Problem:** the loop `break`s when `prepare_submeshes` returns nothing for LOD 0 and nothing afterwards checks `lod_count`; `to_file` then writes a valid-looking file with `lod_count = 0`, no meshes and an all-zero bounding box.
- **Fix:** The exporter raises XIVMeshError ('No mesh could be exported ...') when no mesh qualified for LOD 0.
- **Test:** smoke_export (an object named LOD1 only).

### S23-7 · low · Painter client sends loopback calls through the system/environment HTTP proxy

- **Where:** Tools/SubstancePainter/xiv_instant_edit/client.py:101-116
- **Problem:** `urllib.request.urlopen` uses the default opener, which honours `HTTP_PROXY` and Windows proxy settings. `127.0.0.1` is only bypassed if the user's no-proxy list names it (`<local>` does not match a dotted IP in urllib's registry check).
- **Fix:** Same change as the Substance Painter proxy fix above (reported independently by S17 and S23).
- **Test:** Tools/SubstancePainter/tests/test_plugin.py.

### S23-8 · low · More than 32 distinct mesh attributes fail in `struct.pack` instead of a clear validation error

- **Where:** Blender-Addon/io/model/exp/constructor.py:385-391 (1 << idx), xivpy/model/mesh.py:172 (pack('<I', attribute_idx_mask)), exp/scene.py:57-60 (
- **Problem:** the submesh mask is a uint32; attribute 33+ produces a value above 2^32-1 and the export dies with `struct.error: 'I' format requires 0 <= number <= 4294967295` (after the whole LOD was built).
- **Fix:** The exporter refuses a model with more than 32 different attributes with a message naming the limit, instead of failing in struct.pack after the LOD was built.
- **Test:** smoke_export (33 atrx_ properties).

## Needs verification in the game or in Blender

The code reads as wrong, but whether it matters depends on FFXIV, Dalamud, Penumbra, Painter or the Blender GUI, or on real game data.

### S01-2 · low · Scale range check rejects a whole motion for a bone scale that no layer uses (Match-loop pose, keys taken from a clip)

- **Where:** MotionEvaluator.cs:282-283 (InScale applied to every bone of LoopFirst/LoopSecond) and :330 (key scale); MotionFixes.cs:25-41 (LoopPose stor
- **Problem:** `ApplyMatch` never reads scale, but `Validate` requires every bone of the stored loop pose to have each scale component within [0.001, 100]. The loop pose is the decoded loop clip as is. Likewise a pose/offset key made from the current pose keeps the clip's own scale. A clip with a bone scaled to 0 (hide-a-part mods do this) or above 100 makes `Validate` throw `InvalidDataException` for the layer, which fails `Apply`, the Live display and the save for the whole document, with a message that names the layer but not the bone.
- **Scenario:** add "Match startup to loop" on a startup whose loop clip zeroes the scale of one bone (for example a modded loop that hides a prop bone) -> `LoopPose` captures scale 0 -> layer is invalid -> the animation can no longer be baked until the layer is removed.
- **Reviewer's suggested fix:** clamp scales into `[MinScale, MaxScale]` when capturing the loop pose (`LoopPose`) and when keying from the current pose (or drop the scale check for the match pose, since it is unused); name the bone in the error.
- **To verify:** Needs real clips to see whether vanilla or modded clips carry scales outside [0.001, 100]. Suggested: clamp the scale when capturing the loop pose / keying from the current pose, or skip the scale check for the unused match pose, and name the bone in the error.

### S07-3 · low · IMC overrides take MaterialAnimationId from Lumina's property, which keeps only the low nibble

- **Where:** Dalamud-Plugin/Studio/Services/GameDataService.cs:638 and :952 (entry.MaterialAnimationId).
- **Problem:** I probed the installed Lumina 7.0 `ImcFile.ImageChangeData`: the property returns `_MaterialAnimationIdMask & 0x0F` (raw 0xFF -> 0x0F, raw 0x10 -> 0x00). Penumbra's IMC entry and this slice's own `ImcReader` (`ImcReader.cs:34`, `_data[offset + 5]`) treat the whole byte as MaterialAnimationId. The builder writes one override per variant "preserving every other field", so a variant whose byte has the high nibble set is written back with it cleared.
- **Scenario:** an item whose IMC entries carry a MaterialAnimationId >= 16 (glowing/animated gear) is built; the generated IMC manipulations change that animation id for every variant. Whether any retail item uses the high nibble needs real game data.
- **Reviewer's suggested fix:** read the entry through `ImcReader.Parse(raw bytes)` (full byte, already written and used by mod checks) instead of Lumina's masked property, or read the raw field by reflection-free offset.
- **To verify:** Needs real IMC data to see whether any retail item sets the high nibble of MaterialAnimationId. The fix (read the whole byte through ImcReader) is straightforward once that is known.

### S08-6 · low · `ModEditor.Dispose` waits up to 5 s on an operation that may need the thread it is blocking

- **Where:** Dalamud-Plugin/Studio/Services/Project/ModEditor.cs:188-193 (Check.Cancel(); Check.WaitForOperation(5 s)), called from StudioHost.Dispose (S
- **Problem:** the repair/clean-up workers hop to the framework thread (`OnOwner(...)` for `EditStructure`, `PenumbraModReloader.Reload/IsPresent` via `RunOnFrameworkThread(...).GetResult()`). If `Dispose` runs on the framework thread (plugin unload), a running operation cannot make progress during the wait, so the game freezes for 5 s per open editor, then continues against an editor whose session was torn down. Cancel only helps before meta.json is written.
- **Scenario:** plugin reload while "Applying fixes" is between `EditStructure` and `Reload`.
- **Reviewer's suggested fix:** do not block on the framework thread (wait only when not on it, or let the worker finish and dispose afterwards).
- **To verify:** Depends on which thread Dalamud calls Dispose on.

### S09-6 · low · "DST1/DST3/DST5" textures are decoded as plain DXT blocks (unverified assumption)

- **Where:** Dalamud-Plugin/Studio/Services/Sims/SimsTextureDecoder.cs:73-77 (comment "block layout is identical"), :345-351.
- **Problem:** from memory of the s4pi/Sims 4 Studio tooling, Maxis DST formats are *shuffled* DXT (colour/alpha fields stored in separate planes), which needs an unshuffle step before a BCn decode; the code treats the data as ordinary blocks. I could not verify this without real DST data, and the repo has no DST test.
- **Scenario:** a base-game or Maxis-authored `_IMG` with FourCC `DST5` decodes to noisy or blocky garbage instead of an error, and is then offered as a normal swatch/normal map. Custom content exported by Sims 4 Studio is normally real DXT and is unaffected.
- **Reviewer's suggested fix:** confirm against a real DST5 resource (compare with the same texture exported from Sims 4 Studio); if shuffled, add the unshuffle, otherwise reject DST as unsupported rather than guessing.
- **To verify:** Needs a real DST1/3/5 resource to see whether it is shuffled DXT; until then reject DST as unsupported rather than guess.

### S13-5 · low · `CaptureEffectiveManipulations` orders groups and Multi options differently from `EffectiveFiles`

- **Where:** Dalamud-Plugin/Services/PenumbraMods/ModOptionGroups.cs:566-567 (ThenBy(index)), :641-647 (options taken in settings order) vs :730-731 (The
- **Problem:** Both functions claim to follow Penumbra's precedence ("first mapping wins") but use opposite tie-breaks for equal-priority groups and ignore Multi option priority for manipulations. `EffectiveFiles` is covered by `ModEffectiveFilesFixture` ("of two groups of equal priority, the one listed later wins, as in Penumbra"); the manipulations snapshot is not.
- **Scenario:** Two enabled equal-priority groups, or two picked Multi options, each carrying an IMC/EQP/EST manipulation with the same identifier but different entries: files resolve per the later group, but the captured manipulation list (used for the mashup output manifest, `KnownManipulations`, IMC group planning) keeps the earlier one, so the IMC entry the plugin writes into its generated group can differ from what the collection actually applies.
- **Reviewer's suggested fix:** Reuse the same ordering helper for both (group priority desc, then index desc; Multi options by option priority desc, then index asc).
- **To verify:** Which tie-break Penumbra uses for manipulations with equal priority needs one check in Penumbra; the two functions should then share one ordering helper.

### S14-3 · low · LiveColorTable re-applies a stale table after a structural preview

- **Where:** Dalamud-Plugin/Services/Materials/LiveColorTable.cs:73-78 (Set keeps pending), :190-197 (Adopt sets dirty = pending != null), Dalamud-Plugin
- **Problem:** `pending` is the table of the last *live* edit. A structural edit that also changes rows (Scrolling pattern, other presets, Convert to Dawntrail, shader switch) reaches the character through the temporary-mod redraw, not through `Set`, so `pending` keeps the older rows. When the game later makes a new table texture for the same material (dye change, equipment refresh; or the 500 ms re-validation sees the redrawn character at a reused address as the same material), `Revalidate` returns Adopt and `Update` swaps the stale `pending` in over the game's texture.
- **Scenario:** drag a row colour (pending = T1), apply "Scrolling pattern" (doc rows = T2, preview redraw shows T2), change the dye in Glamourer: the character shows T1 (no scroll layer / glow) while the window shows T2, until the next live edit.
- **Reviewer's suggested fix:** when a structural preview lands (done callback in `PumpPreview`) and on `OnEdit(Structural)`, call `liveTable.Set` with the current table (or add a `Forget()` that nulls `pending`), so Adopt only replays rows that match `loadedDoc`.
- **To verify:** Needs a Glamourer dye change or redraw in the game to see the stale table. Suggested: call liveTable.Set with the current table when a structural preview lands, or add a Forget() that nulls `pending`.

### S16-4 · low · Applying a skin seam fix forgets the preview even when its mod could not be deleted

- **Where:** Dalamud-Plugin/Services/NeckSeam/NeckSeamService.cs:327-333 (Store.Remove(preview.Id) unconditional); cause Dalamud-Plugin/Services/PreviewM
- **Problem:** `DiscardAsync` removes the stored preview only `if (result.Removed)` (`NeckSeamService.cs:352-354`), but `ApplyAsync` always removes it. `PreviewModWriter.DeletePreviewModAsync` returns `(false, "Penumbra could not delete the preview mod ...")` when Penumbra's DeleteMod IPC fails; `PreviewModService.ApplyAsync` turns that into a warning string only.
- **Scenario:** Apply succeeds, Penumbra refuses/fails to delete the preview mod (IPC error, mod locked). The warning is shown once, the store forgets the preview, but the preview mod stays enabled in the character's collection. The next measurement no longer lists it in `PreviewSources` (`AnalyzeAsync` builds that from the store), so the "A skin seam preview is active" guard is bypassed and the preview mod's files are read as if they were the character's source mod files (a later Apply could write into the leftover preview).
- **Reviewer's suggested fix:** have `PreviewApplyResult` carry `Removed` (set from the tuple instead of `_`), and in `NeckSeamService.ApplyAsync` call `Store.Remove` only when the preview mod was removed (keep it so Discard can retry).
- **To verify:** The plumbing is easy (carry `Removed` from PreviewModService.ApplyAsync and only forget the preview when the mod was deleted); the failure itself needs Penumbra's DeleteMod IPC to fail.

### S16-5 · low · Refit window has no guard band: filtering at the window edge reads wrapped texels instead of the neighbouring region

- **Where:** Dalamud-Plugin/Services/TextureCompression/TextureCrop.cs:57-76 (Fit/Axis), RefitPlanner.cs:151-154
- **Problem:** the window is the smallest aligned power-of-two tile that contains the vertex UV bounding box, with no margin. A UV within half a texel (and, for lower mips, within half a texel of that mip) of the window edge is filtered in the original against texels of the adjacent region; in the cropped texture the same footprint wraps (hair/gear samplers are wrap) to the opposite edge of the window, so the strand/edge pixels can pick up the wrong colour or alpha.
- **Scenario:** hair whose UV islands end at, or just inside, 0.25/0.5 of the texture (common for atlas-style ports). The box max lies within one texel of the boundary, so the outermost fringe of the strands blends with the far side of the cropped texture. The original had padding/neighbour content there. Effect is at most a one-texel fringe, more at distance (low mips).
- **Reviewer's suggested fix:** grow `min`/`max` by one texel of the coarsest mip kept (or a fixed 2-4 texels at full size) before `Fit`, and still refuse when that leaves 0..1.
- **To verify:** Visibility of the one-texel fringe needs the game; the planner change would be a guard band of one texel of the coarsest kept mip.

### S18-4 · low · Opening a Painter job launches a second Painter whenever the plugin does not answer within 1 s

- **Where:** Dalamud-Plugin/Services/Painter/PainterJobService.cs:198-203 and 234-252.
- **Problem:** "not running" is decided only by `/status` being unreachable. A Painter that is still starting, or one whose Instant Edit plugin is not enabled yet, is indistinguishable from "not running", so `Process.Start(exe)` runs again.
- **Scenario:** user starts Painter, then clicks the paint-roller in game while Painter is loading (or the plugin is disabled): a second Painter window opens; its bridge cannot bind the exclusive port (bridge.py `SO_EXCLUSIVEADDRUSE`), and with the plugin disabled the call waits the full 2 minutes before reporting the plugin missing.
- **Reviewer's suggested fix:** before launching, check `InstalledSoftware.RunningExecutables("Adobe Substance 3D Painter")` (helper already exists); if one runs, skip the launch and just poll `/status`, with the "enable the plugin under Python" message on timeout.
- **To verify:** Needs Painter to see whether a second instance is tolerated; the check itself (InstalledSoftware.RunningExecutables) already exists.

### S21-4 · low · An export modal that is torn down by Blender (file load, window close) leaves the "export already running" guard set

- **Where:** Blender-Addon/instant_edit/steps.py:102-118,141-218 (_running, RunExportSteps has no cancel()); Blender-Addon/instant_edit/__init__.py:16-28
- **Problem:** `_running` is cleared only in `_end`, reached from `modal`. If Blender removes the modal handler without calling modal (File > Open/New or closing the window while an export waits on the plugin, which can take minutes), `_running` stays True and the generator is only closed when garbage-collected. Every later Quick Export then fails with "An export is already running." until Blender restarts.
- **Scenario:** Quick Export is waiting for Penumbra; the user opens another .blend; afterwards Quick Export is refused for the rest of the session.
- **Reviewer's suggested fix:** add `def cancel(self, context)` to `RunExportSteps` that closes `self._steps` and calls `_end`; also reset `steps._running`/`_pending` in `_prepare_for_scene_load`.
- **To verify:** Needs the Blender GUI to confirm that `modal` is skipped on file load/window close. Suggested change: add `cancel()` to RunExportSteps that closes the generator and calls `_end`, and reset steps._running/_pending in `_prepare_for_scene_load`.

### S23-2 · low · Painter plugin keeps `pending` after the project closes, so the next opened project can be set up as the job

- **Where:** Tools/SubstancePainter/xiv_instant_edit/plugin.py:183-206 (_on_edition_entered), :245-251 (_on_project_closing), :138-140
- **Problem:** `_on_project_closing` clears `job`/`send`/`sending` but not `pending`; `_on_edition_entered` takes any `ProjectEditionEntered` as "the project created for the pending job" without checking it is that project.
- **Scenario:** a send starts loading (pending set, up to 600 s), the user closes the half-built project and opens an existing .spp of the same FFXIV material; `ProjectEditionEntered` fires, `setup_texture_sets` finds the matching texture-set name, resizes it, removes/edits channels, inserts the "XIV original" fill layer, writes job metadata and runs the baseline export into the user's other project. If the creation fails asynchronously, `pending` also keeps `settingUp` true, so the bridge answers 409 "still setting up" for up to 10 minutes.
- **Reviewer's suggested fix:** clear `pending` in `_on_project_closing` (reporting the failure to Instant Edit via `_report_setup`), and in `_on_edition_entered` compare `project.file_path()`/the mesh with the pending job before touching the project.
- **To verify:** Needs Painter to confirm that ProjectEditionEntered fires for an opened .spp while a create is outstanding. Suggested: clear `pending` in _on_project_closing and compare the project with the pending job in _on_edition_entered.

## Needs a maintainer decision

Arguably a bug and arguably intended, or a trade-off with more than one reasonable answer. Each has a recommendation.

### S08-1 · medium · Duplicate removal aliases other game paths onto a file Instant Edit owns, and ModWriter later overwrites that file in place

- **Where:** Dalamud-Plugin/Studio/Services/CleanUp/DuplicateFiles.cs:69-74 (keeper = ledger file first; non-ledger copies become Others and are re-point
- **Problem:** when a non-ledger file is byte-identical to a file this plugin wrote for replacement entry E1 (game path G1), the scan keeps the ledger file and re-points the user's own mapping G2 at it. The file is now one file mapped from two game paths, but the ledger still says it belongs to E1 only. The writer has no notion of "also referenced by another mapping" (`_inUse` is only used for the "missing" test), so the next update of E1 rewrites the file in place and G2 silently changes with it.
- **Scenario:** user replaces a texture for G1 in the default files (ledger file F). Option "Alt" already has an identical hand-made copy for G2. Clean-up -> "Remove duplicates" deletes the Alt copy and points G2 at F. User later re-imports a different image for G1 and updates the mod: F is overwritten, the Alt option now shows the new image too, and the original Alt file is gone (only restorable from the 7-day backup).
- **Reviewer's suggested fix:** do not let a ledger file be the keeper of a set that has non-ledger members (drop such sets, or keep a non-ledger file as keeper and leave the ledger file out of `Others`); alternatively make `ModWriter.Place` write to a fresh path when the planned rel path is referenced by any other mapping.
- **Recommendation:** a ledger file must not be the keeper of a duplicate set that has non-ledger members, or ModWriter.Place must write to a fresh path when the planned path is referenced by another mapping.

### S08-2 · medium · Studio reads absolute / UNC paths straight from a mod's meta.json (graph builder, paste source), unlike the library tile and the checker

- **Where:** Dalamud-Plugin/Studio/Services/Project/PathNormalizer.cs:50 (ToDiskPath returns an absolute value as is) -> Dalamud-Plugin/Studio/Services/R
- **Problem:** `ModLibrary.TileImage` and `CheckModel.Build` explicitly refuse rooted/network paths ("an absolute or network path in a downloaded mod's meta.json would otherwise be looked up from the render thread"), but opening the same mod in a Studio window stats and reads whatever `Files` points at, including `\\host\share\x.tex` (Windows starts an SMB/NTLM connection on `FileInfo.Exists`) and `C:\...` files.
- **Scenario:** a downloaded mod's meta.json has `"chara/x.tex": "\\\\198.51.100.7\\s\\x.tex"` or `"C:\\Users\\me\\Documents\\secret.docx"`. Opening it in the Mod Browser stalls the graph worker on the network and leaks the NTLM hash; copying that file/option and pasting it into another mod writes the local document into the user's mod folder (and from there into anything they export).
- **Reviewer's suggested fix:** treat `IsAbsolute` entries as "outside the mod" in the graph (node with an error, no I/O) and make `ModPaste.SourceFile` require `IsSafeRel` only; keep the legacy-absolute tolerance only if there is a concrete need for it.
- **Recommendation:** treat absolute entries as outside the mod in the graph builder (no I/O) and make ModPaste.SourceFile require IsSafeRel, matching the library tile and the checker. Check that nothing relies on legacy absolute entries first.

### S08-3 · medium · The checker ignores groups of any type it does not know, so their files become "File nothing uses" (pre-ticked delete) and are swept by File locations

- **Where:** Dalamud-Plugin/Studio/Services/Check/OptionSets.cs:185-239 (switch (type) handles single/multi/combining/imc, no default, no MetaIssue); con
- **Problem:** `PenumbraModMeta.BuildGroupViews` (PenumbraModMeta.cs:331-345) treats an `Unknown`-typed group's options as file carriers (the editor lists them as used), but `CheckModel` creates no containers/mappings for them and raises no issue. Every file referenced only from such a group is therefore "unreferenced" everywhere the check/clean-up decides what may be deleted, including the post-edit re-check in `RepairApplier`, `DuplicateFiles` and `FileLocations`.
- **Scenario:** a mod uses a group type this plugin version does not know (a new Penumbra group type, or a type string it does not parse). Check -> "File nothing uses" for each of its `.tex/.mdl/.mtrl` files, pre-ticked; Apply fixes deletes them. "Move files" does the same via `Plan().Obsolete`. Recoverable from the backup only for the retention period.
- **Reviewer's suggested fix:** in `CheckModel.Build` add a `default:` branch that either treats the group like single/multi (containers over `Options`) or at least adds each `Options[].Files` value to `SkippedMappings` and a `BadGroup` issue (which also un-ticks the deletes via `clean`).
- **Recommendation:** add a `default:` branch to CheckModel that treats unknown group types as option containers or records a BadGroup issue, so their files are never offered for deletion.

### S09-2 · medium · Unity package scan and extract have no aggregate memory or disk budget (gzip bomb)

- **Where:** Dalamud-Plugin/Studio/Services/Unity/UnityPackageReader.cs:60-69 (meta text and previews kept per GUID), 75-82 (YAML kept), 201-202 (entry.D
- **Problem:** only per-entry caps exist (meta 256 KB, preview 1 MB, YAML 4 MB, pathname 64 KB). `Scan` keeps every `asset.meta` string (UTF-16, so 512 KB) and every `preview.png` byte[] for every GUID folder in dictionaries, with no total limit and no PNG validation; `BufferedAssetBytes` is only counted for a test. `Write` copies each wanted asset from the gzip stream to disk with no size, ratio or free-space check, and the tar header size is attacker-chosen.
- **Scenario:** a crafted .unitypackage with a few thousand GUID folders, each with a 256 KB-of-zeros `asset.meta` and a 1 MB-of-zeros `preview.png` (about 3 KB gzipped per folder): a ~10 MB file makes Scan hold several GB. Likewise one ticked texture entry declared as tens of GB of zeros fills the output drive during Extract (the run is then cleaned up by `Abandon`, but only after the disk was filled).
- **Reviewer's suggested fix:** give Scan a total budget (stop keeping previews/metas after e.g. 256 MB and add a warning) and keep metas as the 256 KB head only for models/textures; in Extract refuse an entry whose declared length exceeds a cap or free space, and count written bytes while copying (abort past an expansion limit).
- **Recommendation:** a total budget for the metas/previews Scan keeps and a size/ratio/free-space check while extracting an entry.

### S13-1 · medium · Mashup into an existing mod re-lays out the whole mod and orphans path-keyed state

- **Where:** Dalamud-Plugin/Services/PenumbraMods/MashupService.cs:732 (active_mod branch) calling ModCleanup.cs:31-209; consumers of old paths: Services
- **Problem:** For `destination == "active_mod"` the cleanup is not limited to the files the mashup just added: `NormalizeAndDeduplicateMod(target.Folder, ...)` collects every mapping of the user's existing mod (DefaultData, all groups/options/containers), moves each mapped file to `Files/<Group>/<Option>/<gamePath>`, merges identical files and deletes the old copies. Only the export contexts are re-keyed afterwards; everything else that addresses a file by its mod-relative path silently goes stale.
- **Scenario:** A third-party gear mod (TexTools-style layout `GroupName/OptionName/chara/.../x.mdl`) was edited earlier with "backup existing" on. The user then does a mashup into this mod. The model moves to `Files/GroupName/OptionName/chara/.../x.mdl`, so `Describe(mod, newPath).Id` differs from the id the earlier restore points are stored under: `ReadVariantTargets`/Restore list no backups for that model any more (they sit unreachable until the retention sweep deletes them). The Studio sidecar ledger still lists the old RelPaths (an update then sees them as missing/edited-elsewhere, and `OriginalRelPath` restore points at deleted files). A plugin-built hair skeleton in the same mod loses its tuned physics on the next export (`ReadPhysicsFile` looks for the file at its game-path location, which no longer exists), `Remap` rewrites the keys to new game-path files and the relocated copies stay orphaned because `DeleteUnmapped` only looks at game-path locations.
- **Reviewer's suggested fix:** For `active_mod`, run the normalisation only over the mashup namespace (`Files/xiv-instant-edit/mashups/<id>`) / the mappings of the group just written, or skip it for pre-existing mappings; if whole-mod normalisation is wanted, migrate backups (re-key `ModelBackupStore` directories from the remap) and tell the user before relocating their files.
- **Recommendation:** Confirmed by reading. Recommendation: limit the normalisation of an active-mod mashup to the files the mashup just wrote, or migrate backups/ledger entries from the remap and tell the user before relocating files.

### S13-3 · medium · Managed ATR/IMC groups are keyed by the race-specific model path, so a second race's model of the same item is blocked or gets a duplicate group

- **Where:** Dalamud-Plugin/Services/PenumbraMods/AttributeGroupService.cs:82 and :327-328 (marker = atr|<resolvedGamePath>), :584-585, :883-905 (HasUnma
- **Problem:** The generated Atr manipulations (`Slot`, `Id`, `GenderRaceCondition = 0` = any race) and the IMC group identifier (PrimaryId + slot) do not depend on race, but the managed-group marker contains the full resolved path including `cNNNN`. A group this plugin generated for `c0201e0123_top.mdl` is therefore "unmanaged" from the point of view of `c0401e0123_top.mdl`.
- **Scenario:** Mod maps `c0201e0123_top.mdl` and `c0401e0123_top.mdl`. User exports the first with custom tag `atrx_belt` (group `XIV Instant Edit c0201e0123_top Parts (ATR)` + DefaultData `Atr false`). Exporting the second with the same tag now returns `attribute_group_conflict: An existing Penumbra option already contains an unmanaged atrx_ toggle ... Remove the existing atrx_ toggles first` and refuses the whole export (ApplySourceExport/Sizes/Race/Mashup all call `ValidateAttributeGroups` first). With only standard `atr_tv_*` tags a second IMC group with the same Identifier is created instead (Penumbra keeps one IMC manipulation per identifier, so the second model's masks are ignored).
- **Reviewer's suggested fix:** Make the marker race-independent for the Equipment/Accessory/Character object (set id + slot), or treat any group whose Description starts with `AttributeGroupDescriptionPrefix + "atr|"` for the same set id and slot as managed and merge the tag sets.
- **Recommendation:** Whether one shared ATR/IMC group per item (set id + slot) is the intended product behaviour is a maintainer call; the marker could be made race-independent.

### S21-3 · medium · A new Send-my-character silently deletes the previous send's meshes (including unexported edits) and revokes their contexts, which undo cannot bring back

- **Where:** Blender-Addon/instant_edit/character.py:280-312 (replace_previous, _revoke), called from ops.py:1622-1631 (_bind_character) during InstantIm
- **Problem:** every object tagged with the old send (or in its context collections) is removed with `bpy.data.objects.remove`; only objects the user added themselves are kept. Edits made to the imported meshes (weights, sculpting, new parts parented there) are lost without a prompt. The old contexts are queued for revocation in the plugin before removal. Ctrl+Z restores the objects (the import operator has UNDO) but the plugin has already forgotten those contexts, so Quick Export of the restored meshes fails (410/stale context, reattach fails) until the meshes are moved into a new context collection or re-imported.
- **Scenario:** user sends their character, spends an hour editing the hair mesh without exporting, clicks Send my character again (e.g. to refresh the pose): hair edits are gone, and undo brings back a mesh that can no longer be exported through its context.
- **Reviewer's suggested fix:** decide the intended behaviour. Options: keep the old send but hide it, ask for confirmation when any mesh of the old send differs from its imported state, or revoke the old contexts only after the new import has been committed and not undone.
- **Recommendation:** Decide the intended behaviour: keep-and-hide the old send, confirm when a mesh differs from its imported state, or revoke only after the new import is committed.

### S01-3 · low · Symmetry layers ignore bones that a hand-shape or root-reset layer before them has changed

- **Where:** Dalamud-Plugin/Services/Animations/Motion/MotionSymmetry.cs:109-176 (Animated: no case for MotionLayerKind.Fix), used by MotionEvaluator.cs:
- **Problem:** `Animated` adds the bones an earlier layer moves "since those have motion to copy and get a track when saved" (offset/pose keys, idle, swing, noise, look, symmetry, pin, link). A Replace hand shape (finger bones) and a root reset (root bones) also change bones and the bake gives them tracks (`AnimationBakeService.ApplyMotion` adds every bone whose value changed to `affected`), but they are not counted. A pair is skipped when neither side is in `SourceTracks` or this set.
- **Scenario:** a body clip that does not track fingers; layer 1 = hand shape "fist" on the Left hand; layer 2 = Symmetry Left -> Right over the arms. Neither finger bone is "animated", so every finger pair is skipped and the right hand stays at rest instead of mirroring the fist.
- **Reviewer's suggested fix:** in `Animated`, add `case MotionLayerKind.Fix` for `HandShape` (the bones `HandRig.For(...).Chains` hold, per `Hand.Side`) and for `ResetRoot` (roots), guarded by the same `Active` test.
- **Recommendation:** Changes which pairs a symmetry layer copies for existing documents. Recommendation: count bones changed by HandShape and ResetRoot layers as animated.

### S03-3 · low · Retention cleanup deletes unfinished journals (and their staged data) purely by age

- **Where:** Dalamud-Plugin/Services/Animations/AnimationJournalStore.cs:63-84 (Cleanup, called from the constructor at :21)
- **Problem:** `Cleanup` removes every GUID folder whose `CreatedUtc` is older than the retention (7 days by default, user-configurable and as low as the Settings allow) without looking at `State`. Records in "Committing", "RecoveryRequired" or "Undoing" are the only description of a half-applied edit (which file was backed up, which mapping was switched); `ModelBackupStore` is built with the same retention, so the backups go too. Records of a newer version (`Version` not 1-3, skipped by `Load` with a warning) are deleted the same way, because `Cleanup` deserialises without a version check.
- **Scenario:** the game crashes mid-commit of a two-file in-place edit; the user comes back after a week (or after lowering the retention). At the next start the journal is deleted before `Load()`, the first file stays swapped to its `.ie` copy with the mapping switched, and there is no Undo row any more.
- **Reviewer's suggested fix:** skip folders whose state is not terminal (`Completed`, `Undone`, `FilesUndone`, or `Prepared`/`Activated` if those are meant to expire), or at least log a warning before deleting one.
- **Recommendation:** only expire journals in a terminal state (Completed, Undone, FilesUndone, and Prepared/Activated if they are meant to expire), or at least warn before deleting an unfinished one. Records of a newer version are deleted the same way today.

### S07-4 · low · The ledger is only saved once at the end of Commit, so an interrupted update leaves this plugin's own files looking "changed elsewhere"

- **Where:** Dalamud-Plugin/Studio/Services/ModWriter.cs:1083-1131 (files written in the loop, ledger saved after meta merge), ModSync.cs:398-416 (Dispos
- **Problem:** each file is replaced atomically, but the ledger entry (the SHA the next update compares against) is only persisted after every file and meta.json are done. If the game is closed/crashes in between (or the 10 s wait in `ModSync.Dispose` expires), files already overwritten no longer match their old ledger hash.
- **Scenario:** update overwrites its own texture F (ledger hash H1, new bytes H2), game exits before `SaveLedger`. Next update: `ChangedElsewhere(previous, onDisk)` is true, so F is reported as a conflict the user never caused and a material goes through `TryMerge`; "Use mine" resolves it. A new file (no ledger entry) is placed again under `instant_edit\` and the first copy is left orphaned. No user content is lost.
- **Reviewer's suggested fix:** save the ledger (or at least a journal of the paths/hashes about to be written) before the first file is replaced, or after every file.
- **Recommendation:** persist the ledger (or a journal of the paths/hashes about to be written) before the first file is replaced if the extra sidecar write per update is acceptable.

### S07-5 · low · Variant files with the same name end up as duplicate keys in one `Files` object of meta.json

- **Where:** Dalamud-Plugin/Studio/Services/ModBuilder.cs:819-834 (ProcessModelVariants), :1347-1364 (ProcessVariantFolder), ModMetaWriter.cs:271-276 (Wr
- **Problem:** the option a variant goes into is chosen by file stem (`PortSession.OptionFor`). Two model variants from different folders with the same stem (`Red/body.mdl`, `Blue/body.mdl`; `ModelInspector`/`PortEditor` only dedupe by full path) both map to the same option; each is written under its own `modelvariants/..._2` name, but both `(gamePath, rel)` pairs are added to the carrier's `List<KeyValuePair>` and `Utf8JsonWriter` writes the same property name twice. Penumbra's Newtonsoft load keeps the last one, so the other variant is silently unused while the report shows both as Ok.
- **Scenario:** user drops two folders of variants that contain files with identical names -> one variant's file is in the mod but never referenced.
- **Reviewer's suggested fix:** in `Place`/`Carrier.Files` skip or report a second entry for the same (place, gamePath) ("two variants named `body` go into the same option; the second was not used"), as `AddVariantImages` already does for textures.
- **Recommendation:** report the second variant of the same name in the build report instead of writing a duplicate JSON key (as AddVariantImages already does for textures).

### S09-7 · low · Folder scan applies its 5000-file cap only after enumerating and sorting every file

- **Where:** Dalamud-Plugin/Studio/Services/Stash/StashManifest.cs:152-168 (EnumerateVisible(folder).Order(...) then if (files.Count == maxFiles) break),
- **Problem:** `Order` buffers the whole enumeration before the cap is tested, `metas` is unbounded, and each model meta may be read up to 8 MB. Only dot-folders and drives are excluded when a folder is opened (`StashFolders.AddOpened`).
- **Scenario:** the user opens `Documents` or a game-asset tree with a few hundred thousand files in the stash: the background scan walks and sorts all of them (seconds, hundreds of MB of strings) and then lists 5000, while the UI shows the busy state with no cancel.
- **Reviewer's suggested fix:** cap during enumeration (stop at `maxFiles` plus a metas limit) and sort only the retained set, or check the cancel token in the loop.
- **Recommendation:** cap during enumeration and sort only the retained set; check the cancel token in the loop.

### S13-6 · low · Cleanup snapshots and staging folders in Penumbra's mod root are never swept

- **Where:** Dalamud-Plugin/Services/PenumbraMods/ModCleanup.cs:293-309,369-380 (comment: "A stale snapshot is harmless and can be removed on a later run
- **Problem:** A crash, kill or failed delete leaves `.instant-edit-cleanup-<guid>.tmp` (a full copy of the user's mod, including its meta.json and the same `Identifier`) or `.instant-edit-*-<guid>.tmp` (with a complete meta.json) next to the real mods. Nothing in the repo removes them later (grep for these prefixes finds only the creators), contrary to the comment, and they are exactly where Penumbra discovers mods.
- **Scenario:** Game crashes during cleanup of a 2 GB mod. Next launch / rediscover: Penumbra may list the snapshot as a second mod with an identical Identifier, and 2 GB of disk is held forever. Also `RestoreCleanupSnapshot` keeps the snapshot on failure ("the only intact copy") with no way back other than manual copying.
- **Reviewer's suggested fix:** On plugin start (or before each staging) delete `.instant-edit-*.tmp` folders in the mod root older than a day, or move snapshots under the plugin config directory instead of the Penumbra library.
- **Recommendation:** sweep `.instant-edit-*.tmp` folders older than a day from the mod root on start-up, or keep snapshots under the plugin config folder. Whether Penumbra lists dot-prefixed folders needs a look in game.

### S13-7 · low · meta.json is rewritten by about 30 call sites with no backup and fully re-serialised

- **Where:** Dalamud-Plugin/Services/PenumbraMods/ModMetadata.cs:697-715 (WriteJsonAtomic, JsonOpts), callers in ModMetadata, ModOptionGroups, AttributeG
- **Problem:** The write is atomic, but there is no copy of the previous meta.json (the Studio side has `MetaFiles` backups; these flows do not), and `System.Text.Json` with default options rewrites the whole file: non-ASCII text and `< > & ' +` become `\uXXXX`, indentation and number formatting change. A logic bug in any of the planners therefore has no recovery path for the user's group/option definitions, and mods kept in git or hand-edited get whole-file diffs.
- **Scenario:** A planner drops or mis-edits an option (see S13-3/S13-4 class of problems); the user's only copy of the previous meta.json is gone after the next export.
- **Reviewer's suggested fix:** Keep one rolling `meta.json.instant-edit.bak` (or a copy in the managed backup store) per session before the first write, and set `JsonSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping` so text is not escaped.
- **Recommendation:** meta.json writes are now flushed to disk before the rename (see S17-4), but there is still no rolling backup copy and the JSON is re-serialised with default escaping. Recommendation: one `meta.json.instant-edit.bak` per session and UnsafeRelaxedJsonEscaping.

### S14-4 · low · Reconcile replaces an unreadable ("Opaque") colour table with default rows and calls it "Added"

- **Where:** Dalamud-Plugin/Services/Materials/MaterialSession.cs:354-358.
- **Problem:** `ColorTable` promises to keep an Opaque table (unknown width/height or size) as raw bytes ("kept as it is" in the UI), but any shader switch or preset that goes through `Reconcile` on a colour-table shader replaces it with `NewDawntrail(dyeable:false)`; the note says "Added a colour table with default rows." so the user is not told that an existing table was dropped (the other branch, a shader without a table, says "Removed the colour table").
- **Scenario:** a material with a 4x32 (1024-byte) table, shown as "Unknown table", gets the Matcap/Glass/Scroll setup or a Shader combo pick; its table is silently thrown away (undo brings it back, but Save right after loses it).
- **Reviewer's suggested fix:** word the note as a replacement ("Replaced the colour table the editor can't read with default rows"), or refuse to switch/keep the raw table until the user confirms.
- **Recommendation:** Either word the note as a replacement ('Replaced the colour table the editor can't read with default rows') or ask before a shader switch or preset drops an unreadable table.

### S16-3 · low · Restore cannot follow a renamed or moved mod folder; the originals become unreachable and expire

- **Where:** Dalamud-Plugin/Services/TextureCompression/TextureBackupStore.cs:360-378 (Covering matches journal File against the character's currently lo
- **Problem:** journal entries (and refit files) are keyed by the absolute path recorded at compress time. `ModStableId` is stored but never used to relocate the entry.
- **Scenario:** a texture is optimized in mod `Foo`; the user renames the mod directory in Penumbra (or moves Penumbra's mod root). The character now loads `...\Bar\textures\x.tex`, so `Covering` selects nothing and Restore says there is nothing to restore; the journal still points at `...\Foo\...`, whose file no longer exists. After `CacheCleanupDays` (7-90) the automatic cleanup forgets the entry and deletes the only copy of the original (`Expire`). The compressed file keeps being used and cannot be undone.
- **Reviewer's suggested fix:** in `Covering`, also match an entry whose `ModStableId` + `RelativePath` equals a loaded file's mod/relative path, and let `RestoreCompressedAsync`/`RestoreRefitAsync` rebuild `File` from the current mod root (`ModFileService.ModRootAsync`). Alternatively warn on the card when an entry's file is missing.
- **Recommendation:** match journal entries by ModStableId + relative path as well as by absolute path, and rebuild File from the current mod root when restoring; warn on the card when an entry's file is missing.

### S17-3 · low · One slow or idle local connection can hold a bridge slot forever, and a full gate stops `accept`

- **Where:** Dalamud-Plugin/Services/ExportServer.cs:416-445 (accept loop waits on _clientGate before the next accept), :500-501 (10 s timeouts), :2074-2
- **Problem:** `ReadTimeout` is per `Read` call, there is no total deadline for the headers/body, and header reading is allowed up to 64 KB. A client that sends one byte every ~9 s keeps a connection (and one of the 8 gate slots) alive for days. With 8 such connections the accept loop sits in `_clientGate.WaitAsync` and legitimate Blender/Painter connections queue in the OS backlog until they time out. Only `Restart()` or unloading clears it.
- **Scenario:** any local process (not a web page: browsers send the whole request at once and get the 403) opens 8 sockets and dribbles bytes -> every import callback, `/export`, `/export/status`, `/skeleton` and Painter call fails or times out.
- **Reviewer's suggested fix:** give `ReadRequest` an absolute budget (for example a `Stopwatch` checked after every read, 15 s total, 408/close), and accept-then-reject with 503 when the gate is full instead of blocking the accept loop.
- **Recommendation:** an absolute read deadline in ReadRequest and a 503 instead of blocking accept when the gate is full.

### S17-5 · low · Export staging is fixed to `%TEMP%` and refuses any reparse point in any ancestor

- **Where:** Dalamud-Plugin/Services/ExportServer.cs:1623-1627, :1766-1777 (HasReparsePointInPath, also used for the source file's parent at :1613-1615 a
- **Problem:** staging goes to `Path.GetTempPath()\InstantEdit\plugin-exports` regardless of the cache directory the user configured, and `HasReparsePointInPath` walks every ancestor up to the drive root. A TEMP (or profile) path that crosses a junction/symlink, a common way to move %TEMP% to another drive or a RAM disk, makes every Quick Export, Sizes, Race and Mashup export fail with "plugin export staging directory is unsafe" (code `internal_error`, remedy "Retry"), with nothing the user can change in the add-on or plugin settings. The same check applied to the Blender cache path rejects a cache directory below a junction or mount point.
- **Scenario:** `TEMP=D:\Temp` where `C:\Users\me\AppData\Local\Temp` is a junction to it -> all exports fail.
- **Reviewer's suggested fix:** stage under the managed cache root (`TextureFiles.EnsureCacheRoot`, which the plugin already owns and validates), or only reject reparse points that are inside the directories the plugin creates (the last two levels), and give the failure a specific code/remedy.
- **Recommendation:** Refusing links is a deliberate hardening choice; recommendation: stage under the managed cache root or only refuse reparse points inside folders the plugin creates, with a specific error code.

### S17-6 · low · `StopListener` blocks the calling (UI/framework) thread for up to 2 s while client tasks may be waiting for that same thread

- **Where:** Dalamud-Plugin/Services/ExportServer.cs:2242-2249 (Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(2))), called from Restart() (Plugin.cs:254,
- **Problem:** `/skeleton` and `/racial-deformer` handlers await `framework.RunOnFrameworkThread(...)` (ModelSkeletonResolver.cs:97, 135). If one is in flight when the port is changed or the plugin unloads, it cannot finish while the framework thread sits in `.Wait`, so the wait always runs its full 2 s (frame hitch / slow unload). Exports that are mid-write are not waited for beyond that, and `Plugin.Dispose` has already disposed `_studio`, `_window`, `_textures`, `_painterJobs` and `_onScreen` before it stops the listener (`Plugin.cs:540-568`), so requests are served against half-torn-down services in that window.
- **Reviewer's suggested fix:** stop the listener first in `Plugin.Dispose`; make `Restart` close the listener without waiting (or wait on a worker), and give in-flight writes a chance to finish before disposing the services they use.
- **Recommendation:** stop the listener first in Plugin.Dispose and do not block the framework thread in StopListener.

### S21-8 · low · A queued import moves the user out of Edit/Sculpt/Pose mode and never puts them back; the restore code cannot work anyway

- **Where:** Blender-Addon/instant_edit/server.py:1304-1306 (forces OBJECT before the operator), ops.py:1236-1242 (_snapshot_object_state stores context.
- **Problem:** by the time `InstantImport.execute` snapshots the state the mode is always "OBJECT" (poll requires it), so the intended "restore mode" branch is dead; and `context.mode` values such as "EDIT_MESH" are not valid `mode_set` enums (checked: `TypeError: enum "EDIT_MESH" not found in ('OBJECT','EDIT','SCULPT',...)`), so it would raise if it were ever reached.
- **Scenario:** user is in Edit Mode on a mesh while the game sends a model: Blender drops to Object Mode, the model imports, the user stays in Object Mode. (Edit changes are flushed, so no data is lost.)
- **Reviewer's suggested fix:** snapshot the mode in `_run_queued_import` before forcing OBJECT, translate `context.mode` to the `mode_set` enum (`EDIT_MESH`->`EDIT`, `PAINT_WEIGHT`->`WEIGHT_PAINT`, ...), restore it after the operator, and drop the dead branch from `_restore_object_state`.
- **Recommendation:** Confirmed: the mode-restore branch in _restore_object_state can never run (mode is already OBJECT) and would raise if it did (context.mode strings are not mode_set enums). Whether to restore the user's mode after a queued import is a UX call.

### S21-9 · low · `/variant-targets` reply can exceed the add-on's 64 KiB response cap

- **Where:** Blender-Addon/instant_edit/ops.py:63,1081-1094 (MAX_PLUGIN_RESPONSE_SIZE = 64 * 1024); reply built in Dalamud-Plugin/Services/ExportServer.c
- **Problem:** one option is about 430 bytes, mostly the 64-hex id and the absolute `backupDirectory` (the directory is just `<store>/<backupTargetId>`). A group made by a Magic Fit Sizes export has up to 64 options (27 KB measured with plausible values); three such groups (82 KB) exceed the cap, `post_json` raises `PluginResponseTooLarge`, and the Penumbra target list can no longer be loaded (target refresh fails, only "New Group"/"In place" remain usable).
- **Scenario:** a mod accumulates three sizes groups for the same model; every import/refresh then warns "Penumbra targets could not refresh: ... larger than the bridge limit".
- **Reviewer's suggested fix:** drop `backupDirectory` from the option entries (derive it in Blender, or send it once per request), and/or raise the cap for this endpoint.
- **Recommendation:** drop backupDirectory from the /variant-targets option entries (derive it in Blender) or raise the response cap for that endpoint.

### S22-7 · low · Exported shape-key mix ignores a key's vertex group, while Remove Hidden Vertices honours it

- **Where:** Blender-Addon/mesh/shapes.py:8-49 (get_shape_mix, used by io/model/handler.py:_keep_shapes and mesh/export.py:495); contrast Blender-Addon/m
- **Problem:** `get_shape_mix` blends each key as `(key - relative) * value` and never applies `key.vertex_group`. Blender (and `hidden.py`) scale that offset by the group's per-vertex weight.
- **Scenario:** a `shp_` key limited by a vertex group to part of the body: the viewport shows it limited, `hidden.py` tests the limited state, but the exported shape moves every vertex. Remove Hidden Vertices can then judge a vertex covered in a shape state that the exported model does not have, and delete it.
- **Reviewer's suggested fix:** decide which meaning export should have; if Blender's, apply the group weight in `get_shape_mix` (share the helper with hidden.py), otherwise make hidden.py ignore the group.
- **Recommendation:** Decide whether export should follow Blender (a shape key's vertex group scales it, as Remove Hidden Vertices already assumes) or ignore the group; then share one helper between mesh/shapes.py and mesh/hidden.py.

### S23-5 · low · `prepare-release.ps1 -CheckOnly` passes, but a real release dies after the full build because `repo.json` still says 2.1.0

- **Where:** scripts/prepare-release.ps1:149-163,292-298,320; Dalamud-Plugin/repo.json and repo.json (AssemblyVersion 2.1.0, download links .../releases/
- **Problem:** `Update-RepoJson` throws "lists AssemblyVersion '2.1.0', but the plugin is 3.0.0" only after `dotnet build` and the asset checks; `-CheckOnly` exits before it, and `docs/release-packaging.md` (Migration step 3) claims local main's `repo.json` already points at `releases/download/3.0.0/`, which is not true on this branch.
- **Scenario:** maintainer runs `-CheckOnly` (green), then the real run: Release build, then failure at repo.json; nothing is staged for the release.
- **Reviewer's suggested fix:** bump both repo.json files with the 3.0.0 bump (checklist step 1) and move the `AssemblyVersion` == csproj `<Version>` check next to the other component checks so `-CheckOnly` covers it.
- **Recommendation:** Release process: bump both repo.json files with the version bump and move the AssemblyVersion check in prepare-release.ps1 ahead of the build so -CheckOnly covers it. docs/release-packaging.md (Migration step 3) says main's repo.json already points at 3.0.0, which is not true on this branch.

### S23-6 · low · `Assert-DalamudPackage` does not check most runtime dependencies the plugin now needs

- **Where:** scripts/prepare-release.ps1:49-56
- **Problem:** required entries are InstantEdit.dll/json/deps/pdb, Penumbra.Api.dll, NVorbis.dll. The csproj also needs BCnEncoder.dll, SixLabors.ImageSharp.dll, SharpGLTF.Core/Toolkit/Runtime.dll, CommunityToolkit.HighPerformance.dll and `Studio/Resources/MaterialPresets/**` (the Debug `bin` folder has all of them). If DalamudPackager leaves any out, the release ZIP passes the check but texture conversion, glTF export or material presets throw at runtime.
- **Scenario:** a packaging change drops a dependency; nobody notices until a user installs 3.0.0.
- **Reviewer's suggested fix:** add those entries (and at least one `Studio/Resources/MaterialPresets/` file) to `$requiredEntries`.
- **Recommendation:** Add BCnEncoder.dll, SixLabors.ImageSharp.dll, SharpGLTF.*.dll, CommunityToolkit.HighPerformance.dll and one MaterialPresets file to Assert-DalamudPackage's required entries once a Windows Release package confirms the real list.

## Confirmed, not fixed

Confirmed by reading the code; not changed because the fix needs context that is not available here.

### S08-5 · low · Restore and the rollback `PutBack` rewrite meta.json outside `ModFiles.Gate`

- **Where:** Dalamud-Plugin/Studio/Services/Backups/ModBackupStore.cs:142-197 (Restore, writes at :177), Dalamud-Plugin/Studio/Services/Backups/MetaFiles
- **Problem:** `ModFiles.Gate` exists so that the render thread's read-modify-write of meta.json/sidecar and a worker's write cannot interleave (`FileRetry` even says "under the mod files' lock, like every other write into a mod"). Restore and PutBack replace meta.json with stale bytes without taking it. The Restore buttons are disabled while `Sync.Busy`, but mod-name/description text fields stay editable during the operation, and the rollback path waits on Penumbra for as long as it likes.
- **Scenario:** user clicks Restore; while it runs they edit the mod description, ModSync (holding the gate) reads the pre-restore meta.json, Restore swaps in the backup, ModSync writes its copy back: the restore is undone but reported "Restored N files" and the entry is marked restored. Same shape for PutBack after a failed reload: a description edit made while Penumbra was reloading is overwritten by the bytes read in `Take`.
- **Reviewer's suggested fix:** take `lock (ModFiles.Gate)` around the file swaps in `Restore`/`PutBack` (and re-read meta.json bytes in `Take` under it).
- **Why not fixed:** Confirmed by reading but not changed: taking ModFiles.Gate in Restore/PutBack needs a lock-order check against the Penumbra reload path, which cannot be done without the game.

### S22-5 · low · Modify Vertex Data stops at the first mesh that raises, leaving earlier meshes edited and not updated

- **Where:** Blender-Addon/mesh/vertex_data.py:143-152 (apply_vertex_data), :52-56, :65-76; caller Blender-Addon/operators.py:2034-2039.
- **Problem:** meshes are edited one by one in `_apply`; a `ValueError` (from `get_xiv_uv_layers`: no UV map, duplicate/missing uvN, ambiguous maps; or `_add_uv2` hitting the UV map limit) for a later mesh aborts the loop. `obj.data.update()` is only called after the loop, and the operator returns CANCELLED (no undo push), so the meshes already processed keep their change.
- **Scenario:** select a body mesh and a helper mesh with no UV map, run "Clear UV2": the body's UV2 is zeroed, the helper raises "No UV map was found", the report says nothing changed. Copy UV1 to UV2 can also have renamed the first mesh's lone map to `uv0` before a later failure.
- **Reviewer's suggested fix:** collect `_uv_layers(obj)` (and any other validation) for every mesh first, or catch per-mesh `ValueError`, skip that mesh and report it with the untouched ones.
- **Why not fixed:** Confirmed by the reviewer (reproduced); not changed: the tool edits meshes in different ways per action, so a safe fix needs either a per-action validation pass before the first edit or a rollback.

### S22-6 · low · Split by Material has no rollback when a later mesh fails

- **Where:** Blender-Addon/operators.py:1352-1376 (loop, except RuntimeError, finally), :1264-1289 (_separate_by_material).
- **Problem:** each selected mesh is separated in turn (new objects, `obj.data` copied, material slots rewritten, `active_shape_key_index` set to 0). If `bpy.ops.object.mode_set`/`mesh.separate` raises `RuntimeError` for a later mesh (linked/override data, not editable), the function reports the error and returns CANCELLED, but the earlier meshes stay split under Blender's `.001` names with their old mesh numbers, the failed mesh keeps `active_shape_key_index == 0`, and nothing is renamed.
- **Scenario:** select two meshes where the second is linked from a library: the first is split into pieces named `Top.001`, `Top.002`, the operator says "Could not split the meshes". Same pattern, without rollback, in Apply Racial Scaling (`operators.py:1615-1624`, a `LinAlgError` from a singular matrix aborts after earlier meshes were moved and marked) and Remove Hidden Vertices (`:1468`).
- **Reviewer's suggested fix:** check editability of every target before the first change (or collect the new objects and remove them plus restore `obj.data` on failure); for racial scaling catch per mesh and report.
- **Why not fixed:** Confirmed; not changed: needs an editability check of every target before the first split, or collecting and removing the new objects on failure. Apply Racial Scaling and Remove Hidden Vertices share the pattern.

## Observations outside the reviewers' own areas

- (S01) `BlenderMotion.Fill` (stretched path) drops `ReferenceFollowers` and `HipsOnHara` while the clone path keeps them, and `MotionHold.Held` (hence `MotionWindUp.Build`) drops `HipsOnHara`. Harmless today because `ReplaceMotionAsync`/`HoldStillAsync` refuse any clip with `source.Rebuild != null` and `HipsOnHara` is `RepairModel != null`; it would bite if that restriction (the "shortcut" comment in `AnimationEditService.Timeline.cs`) is lifted.
- (S01) `MotionEdits.SetKey` (`Ui/Timeline/MotionEdits.cs:1114`) accepts any `BoneTransform`; scale range, finiteness and rotation length are enforced only later by `MotionEvaluator.Validate` at bake time, so a bad key surfaces as a refused save rather than at edit time.
- (S01) `MotionCuts.Validate` has no upper bound on `Parts.Length`, and `Kept` (LINQ `Sum`) would throw `OverflowException` on a crafted journal with enormous parts; unrealistic for normal use.
- (S03) `Dalamud-Plugin/Ui/Timeline/TimelineAutosaver.cs:61,69` blocks the UI thread with `writing.Wait()` in `Closed()`/`Flush()`; fine unless the autosave is large (`AnimationJournalStore.SaveAutosave` has no size cap while `LoadAutosave` refuses files over 512 MiB, so an oversized autosave is written but can never be offered back).
- (S03) `Dalamud-Plugin/Services/PenumbraMods/AnimationModService.cs:201-211` `DisableAnimationModAsync` only disables the new mod; a new-mod commit that fails after the folder was created leaves the half-written folder on disk (the rollback never deletes it), so that mod name stays "already exists" until the user removes it by hand.
- (S07) `Dalamud-Plugin/Studio/Services/Project/PathNormalizer.cs:80-88` `SafeSegment` replaces invalid characters but not Windows device names (CON, NUL, AUX, PRN, COM1-9, LPT1-9), while `PortSession.SanitizeFolderName` does. `ModBuilder.PlanFolders` and `ModWriter.ReplacementRel` build option folders from it, so an option or group named "Aux"/"Nul" makes every file write into that folder fail (Build: whole build aborts and is deleted; Edit: per-file failures).
- (S07) `Dalamud-Plugin/Services/PenumbraMods/AttributeGroupService.cs:251` uses Lumina's masked `MaterialAnimationId` too (see S07-3), while `Services/Animations/AnimationMetadata.cs:127` reads the full byte.
- (S08) `Dalamud-Plugin/Studio/Services/ModWriter.cs:276-285,1387-1416`: `Plan`/`WriteAtomically` overwrite a ledger-owned file in place without checking whether other mappings share it (see S08-1; the writer is the consumer side of that hazard).
- (S08) `Dalamud-Plugin/Studio/Windows/Studio/CheckView.CleanUp.cs:138`: the duplicate scan runs with `CancellationToken.None`, so the Stop button cannot interrupt hashing of a big mod.
- (S09) `Dalamud-Plugin/Studio/Services/Project/PathNormalizer.cs:80` `SafeSegment` lacks reserved device names; it is also used for Penumbra option/group folders (`ModBuilder.cs:246`, `ModWriter.cs:435`, `ModPaste.cs:378`), so a group named `Aux` or `Con` has the same problem there.
- (S09) `Dalamud-Plugin/Services/Previews/ModelMeshReader.cs` validates material and index ranges (checked while tracing `ModelTextureLookup.Uvs`); no issue seen.
- (S13) `Studio/Services/CleanUp/DuplicateFiles.cs` deliberately never touches files a `ui/` game path uses; `ModCleanup` (this slice) has no such exclusion and dedupes/moves them. If the exclusion exists for a game reason, ModCleanup should share it.
- (S13) `Services/Animations/AnimationCommitService.cs` / animation journals store mod-relative paths; they are also not re-keyed by `ExportServer`'s `PathRemap` handling after a whole-mod cleanup (same root cause as S13-1).
- (S13) `Ui/Timeline/TimelinePreview.cs:498` catches only `InvalidDataException`/`ArgumentException` around `PenumbraMetaEncoder.Encode`, which can also throw `OverflowException` (`checked((ushort)m.State)` in PenumbraMetaEncoder.cs:39) for an out-of-range attach state read from a mod's meta.
- (S14) `Dalamud-Plugin/Services/PenumbraMods/PreviewModWriter.cs:239-253`: `WritePreviewSourcesAsync` writes the material and a changed model file one after the other (each atomic and backed up) with no rollback of the first if the second fails, so a failed model write leaves the new material with the old model (the Scrolling pattern then flashes). Backups exist for manual undo.
- (S14) `Dalamud-Plugin/Services/MaterialPreviewBundleBuilder.cs:675` `terrainShadowMeshSize = 24`: could not verify against a real MDL (I believe the struct is 20 bytes in Lumina/Penumbra); it only moves the material-table offset used by the fallback in `ReadModelMaterials` for models that have terrain-shadow meshes (rare). `ModelUv2` refuses those models, so the two readers do not share the constant.
- (S14) `Dalamud-Plugin/Studio/Services/MtrlReader.cs:263`: any trailing byte after the value block makes the whole shader section "unparsed" (read-only editor); fine as a safe default, but it is what makes S14-1 reachable.
- (S16) `Dalamud-Plugin/Services/PenumbraMods/ModFileService.cs:53-63`: the replacement is written as `<file>.instant-edit-<guid>.tmp` inside the user's mod folder; if the process dies between the write and `File.Move` the stray `.tmp` stays in the mod (the `finally` cleanup does not run). Harmless to the mod's meta, but it accumulates and Penumbra's unused-file tools will list it.
- (S16) `Dalamud-Plugin/Services/PreviewMods/PreviewModService.cs:78`: `PreviewApplyResult` has no `Removed` flag (see S16-4).
- (S17) `Tools/SubstancePainter/xiv_instant_edit/client.py:42` calls the default `urllib.request.urlopen`, which honours `HTTP_PROXY`/Windows proxy settings (127.0.0.1 is not bypassed), so Painter's `jobId` + capability would be sent to a configured proxy. The Blender add-on avoids this explicitly with `ProxyHandler({})` (`plugin_http.py:22`) and the plugin with `UseProxy = false`.
- (S17) `Services/PenumbraMods/ModPaths.cs:74` has the same `//host/share` gap as S17-1 and gates all stored target paths.
- (S17) `Blender-Addon/instant_edit/cache.py:36-38` sweeps only `.mdl|.fbx` backups and `.est.json` sidecars; `.race.json`/`.hairskel.json` sidecars of an expired model backup stay until the plugin's own `ModelBackupStore.Cleanup` removes them (harmless, they are cleaned as orphans).
- (S17) `GlamourerIpc.Outcome` (4 = design not found) and `CustomizePlusProfiles.Outcome` (3 = profile not found) rely on the external APIs' error numbering; I could not check them without those packages.
- (S18) Other `ModelMeshReader.Read` callers inherit S18-1: `Services/Physics/PhysicsBuilderService.cs:388,454`, `Services/Physics/PhysicsBody.cs:162`, `Services/Animations/Preview/WornBones.cs:94`, `Services/Heels/HeelsMeasurement.cs:97`, `Studio/Services/Stash/ModelTextureLookup.cs:221`.
- (S18) `Tools/SubstancePainter/xiv_instant_edit/settings.py:35-40`: a `version` key left in an old `config.json` overrides `PLUGIN_VERSION` in `/status`, so a plugin whose files were updated by hand can keep reporting the old version (C# Install/UpdatePorts rewrite the file without it, so only until the next port change or reinstall).
- (S18) `Services/TextureFiles.cs:432-443` (`EnsureLocalPath`) fails for any path with a reparse-point ancestor; `PreviewService.ReadBytesAsync` calls it for every mod-file hover preview, so a Penumbra root reached through a junction/symlink shows "Linked files and directories are not supported" on all previews. Looks deliberate, but worth a product decision.
- (S21) `Dalamud-Plugin/Ui/Main/BlenderConnection.cs:69` posts `/settings/cache` on every status check (about every 5 s while the UI is visible) and before each import; each POST makes the add-on rewrite `cache-settings.json` and run a full `clean_cache(STALE_SECONDS)` (scan of imports, exports, every backup folder and the diagnostics folder) on a handler thread. Correct but wasteful; consider posting only when the directory/retention changed.
- (S21) `cache.py:340-344` removes only the `.est.json` sidecar with a swept backup; `ModelBackupStore` also writes `.race.json` and `.hairskel.json` sidecars. They are removed later by the plugin's `Cleanup()`, so harmless.
- (S21) `Dalamud-Plugin/Services/ExportServer.cs:1593-1640` (`StageExportFileAsync`) accepts any local rooted `.mdl` path, not only files under the add-on's cache; fine given the capability check, just noting that the add-on's `filePath` is not confined.
- (S22) `Blender-Addon/io/model/importer.py:_read_xiv_mesh` calls `create_stream_arrays` and `_verify_attributes` outside the per-submesh `try/except XIVMeshError`, so one bad mesh aborts the whole import after earlier meshes were created (feeds S22-4).
- (S22) FBX/glTF export with `use_visible=True` also writes non-mesh visible objects (lights, cameras, empties, other armatures) regardless of Parts (related to S22-2).
- (S22) `instant_edit.unregister()` is called unguarded from `__init__.unregister()`; an exception in `stop_server()` would skip property removal and class unregistration, and the next `register()` would hit "already registered" (speculative, not traced).
- (S23) `Blender-Addon/properties.py:155` `get_model_flags()` returns constant defaults; key `edge_geometry_disabled` has no matching `ModelFlags2` member (`edge_geometry_enabled`), and `use_lods` (hidden) is never assigned anywhere, so LOD1/LOD2 objects are never exported.
- (S23) `Tools/SubstancePainter` Python tests and the Blender suites `character_regression`, `split_by_material_menu_regression`, `armature_regression` are not run by either workflow (the C# workflow runs only the four dotnet suites).
- (S23) `Dalamud-Plugin/Services/Painter/PainterInstallation.cs:Install` only copies the embedded `.py` files; modules removed in a later plugin version stay in the Painter plugin folder.
- (S23) Six `Tools/**/*.csx` scripts hard-code `D:\Mods\VSC Projects\Instant Edit\Tools\EarringBones` (dev-only, fail with FileNotFound elsewhere); `Tools/GIMP/README.md` still describes `gimp-image-get-filename` and a `gimp-image-convert-precision` call the script no longer makes.

## Coverage

| Area | Covers | Status |
| --- | --- | --- |
| S01 Animation motion model | Services/Animations/Motion | reviewed, 4 findings |
| S02 Animation preview and timeline model | Services/Animations/Preview, Timeline | **not reviewed** |
| S03 Animation services | Services/Animations/*.cs | reviewed, 6 findings |
| S04 Timeline UI: editing core | Ui/Timeline (editor, edits, cuts, workspace, save footer) | **not reviewed** |
| S05 Timeline UI: motion, layers, bones | Ui/Timeline (layers, bones, poses, partners, curves) | **not reviewed** |
| S06 Timeline UI: canvas, preview, pickers | Ui/Timeline (canvas, preview, pickers, sound, effects), Ui/TimelineWindow* | **not reviewed** |
| S07 Studio services: mod writer and builder | Studio/Services/*.cs | reviewed, 6 findings |
| S08 Studio services: checker, project, clean-up | Studio/Services/Check, Project, Resources, Backups, CleanUp, Replacements, Animation | reviewed, 6 findings |
| S09 Studio importers | Studio/Services/Sims, Stash, Library, Unity | reviewed, 7 findings |
| S10 Studio windows: main | Studio/Windows/Studio, Home, Import and top level | **not reviewed** |
| S11 Studio windows: build, panels, UI | Studio/Windows/Build, Panels, UI, Studio/*.cs | **not reviewed** |
| S12 Studio models, plugin root | Studio/Models, Models, Plugin.cs, Configuration.cs, csproj | **not reviewed** |
| S13 Penumbra mod services | Services/PenumbraMods | reviewed, 7 findings |
| S14 Materials | Services/Materials, Ui/Material, material preview bundle | reviewed, 4 findings |
| S15 Physics and heels | Services/Physics, Heels, Ui/PhysicsWindow* | **not reviewed** |
| S16 Neck seam and texture compression | Services/NeckSeam, TextureCompression | reviewed, 5 findings |
| S17 HTTP bridge and core services | Services/*.cs (ExportServer, BlenderClient, registries, texture editing) | reviewed, 6 findings |
| S18 Painter, setup, previews, game files, misc services | Services/Painter, Setup, GameFiles, Actors, CharacterSend, PreviewMods, Previews | reviewed, 5 findings |
| S19 Audio, skeletons, pose UI, UI root | Services/Audio, Skeletons, Ui/Pose, Ui/*.cs | **not reviewed** |
| S20 Main window UI | Ui/Main | **not reviewed** |
| S21 Blender add-on: bridge (instant_edit) | Blender-Addon/instant_edit | reviewed, 10 findings |
| S22 Blender add-on: operators, UI, mesh | Blender-Addon root modules, mesh/ | reviewed, 7 findings |
| S23 Blender io/xivpy, tools, scripts, CI, packaging | Blender-Addon/io, xivpy, Tools, scripts, workflows, manifests | reviewed, 8 findings |
| S24 Tests and documentation | Tests, Blender-Addon/testing, docs, README | **not reviewed** |

What each reviewer read, and what they found clean:

- **S01** — read: read fully (all 36 files, 9.0k lines): MotionEvaluator, MotionClip, MotionLayer, MotionFixes, MotionCutter, MotionCuts, PinSolver, PinChains, PinPlanting, MotionSymmetry, MotionIdle, MotionSwing, MotionNoise, MotionSmoothing, MotionLook, MotionLinks, HipSolver, HeadSolver, HandRig, HandShapes, LoopSeam, MotionKeyMarkers, MotionCurves, MotionPaths, MotionBreakdown, MotionWindUp, MotionHold, MotionAngles, MotionMirror, KeyTiming, BlenderMotion, LivePoseLayer, PoseFiles, PoseImport, PoseLibrary, PoseMatches. Callers read for context: AnimationReadFormat, AnimationBakeService.ApplyMotion/Decode, PoseBrowser, LivePosePanel, PoseEdits, MotionEdits.SetKey, and the add-on's animation_read.py (duration echo).  
  Clean: quaternion order and handedness (Hamilton product, parent*local, left deltas, Exp/Log/Pow/Unbend), slerp/Align sign handling and Continuity, Euler round trip (200k random rotations, worst 1-|dot| = 6e-6), Rise/RiseSpeed/FallSpeed boundary derivatives, EaseTime monotonicity, interval/wrap maths in Sample/WeightAt/Fade/ReachesEnd, tent running-sum indices in MotionSmoothing, cut time map and sample counts, planted-stretch wrap logic, preview (ApplyAt) vs save (Apply) layer order including the hips-on-n_hara carry (the differences are the documented ones: amplitudes, planted pins, smooth, match/close), PoseFiles name checks and atomic temp-file + move write, hip/head/pin solvers (guards for zero-length bones and divisions are in place). No culture-dependent formatting or parsing found in the slice; pose numbers use InvariantCulture both ways. No static mutable state or threading hazards (MotionPathCache is lock-guarded).
- **S03** — read: read fully - AnimationCommitService, AnimationJournalStore, AnimationEditService (all 5 partials), AnimationObserver, AnimationRuntime, AnimationNative, AnimationBakeService, AnimationRecorder, AnimationSkeletonIndex, AnimationSkeleton, AnimationPap, AnimationMetadata, AnimationDependencies, AnimationResources, AnimationFiles, AnimationClipExport, AnimationTake, AnimationReadFormat, PapLoadTable, AnimationCatalog, AnimationLibrary, AnimationBakeRules, AnimationBones, AnimationRetarget, AnimationReferenceChannels, CustomizePlusPause, LoopFades, LoopFadeWatch, HandShapeLibrary, CaptureHistories, RecordingScale, AnimationSlots, AnimationPoseRules, AnimationMatching, HandTipCheck, AnimationFileRename; AnimationPresentation read, not scrutinised line by line (label tables). Context read: AnimationModService (activate/disable/reload), TimelineAutosaver, AnimationEditJournal model.  
  Clean: AnimationCommitService is sound on the focus questions. In-place edits write to a fresh `.ie<token>` path, never overwrite a user file without a hash-verified backup, and every written path passes IsPathWithin + EnsureLocalPath; staged files are hash-checked; added files refuse an existing target; Undo preflights every file before restoring any and is resumable from any journal state (Committing/RecoveryRequired/Undoing). Havok/ClientStructs access is on the framework thread throughout (RunOnFrameworkThread/RunOnTick; only managed MotionClip work runs on workers). The recorder is bounded (30 s cap x frame spacing >= half a 60 Hz sample), pairs Update +=/-= on every exit, and resumes Customize+ on all paths. Binary parsers (PAP, TMB walker, AVFX chunks, META0001, PapLoadTable, XIEA reply) bounds-check every length. Native array validation and arena sizing are consistent.
- **S07** — read: read fully - ModWriter, ModBuilder, ModMetaMerger, ModMetaWriter, ModSync, BuildController, GameDataService, TextureConverter, TextureCache, TextureSources, MtrlReader, MaterialPatcher, ModelMaterialRewriter, VanillaModelReader, ModelLayoutReader, DummyModelBuilder, Eqdp/Eqp/Est/Imc readers, AlphaNormal/GameNormal/NormalGreen/DiffuseNormal/GameMask, AutoCompression, PenumbraTextureConverter, PenumbraIpcService, ImageFileCache, ModelFileCache, SourceFingerprint, PartToggleGroups, Material*Library/Templates, Build/WriteReport, DummyTextureLibrary, StudioImages; PortValidator read in full but only skimmed for message wording.  
  Clean: ModWriter plan/commit/conflict/ledger logic and path safety (IsSafeRel guards on every write and delete), ModBuilder new-folder-only guard and leftover cleanup, MtrlReader/MaterialPatcher round-trip layout, MDL header/LOD/mesh offsets in ModelMaterialRewriter.Write (apart from S07-1), DummyModelBuilder layout, DDS/.tex header and mip-size maths in TextureConverter, EQP/EQDP/EST/IMC table indexing and bounds, Penumbra IPC labels and TextureType ints (verified against Penumbra.Api 5.15.1), Lumina IMC SoundId shift (verified against Lumina.dll).
- **S08** — read: read fully - Backups/*, CleanUp/*, Check/* (all 19 files), Project/* (all 10), Replacements/ReplacementValidator, Animation/*, Resources/{FileSniffer,TexHeader,SharedMaterials,PathStringScanner,ModFileFactsCache,ResourceGraphService,ResourceNaming}; ResourceGraphBuilder read in full but only skimmed for graph-linking details (no file writes there). Cross-read ModProject.EditStructure, ModWriter.Plan/Place/WriteAtomically, CheckView.CleanUp for callers.  
  Clean: ModPaths/PathNormalizer.IsSafeRel traversal handling, backup journal atomic writes and "restore only what still holds the left-behind bytes" logic, PenumbraModMeta round-trip/edit helpers (RemoveOption DefaultSettings maths, Moves remapping, legacy merge), every delete path (RepairApplier, DuplicateFiles, FileLocations, PendingCleanup) re-checks a fresh meta.json before deleting, ModPaste (never overwrites, rolls back written files), ModFolderWatcher, ModLibrary, Animation planner/writer, TexHeader/FileSniffer.
- **S09** — read: read fully: all 33 files of the slice (Sims: DbpfPackage, RefPack, CasPartReader, SimsGeom, SimsMeshExporter, SimsTextureDecoder, SimsPackageInspector, SimsHairDefaults; Unity: all 3; Stash: all 15; Library: all 7). Two small experiments (regex timing, Unicode digits) under scratchpad/work-S09/rx.  
  Clean: DBPF index/size/compression-ratio guards, RefPack decoder (opcodes, overlap copy, bounds), RLE2/DDS block decoding bounds, CASP key-table/LOD search bounds, zlib inflate caps, DBPF/Sims/Unity path-traversal guards (SafeRelative + prefix check, MakeSafeName), ExtractionFolder move-aside/restore protocol, StashManifest/LibraryScanCache atomic write + corrupt-file recovery, StashFolders.Remove containment, LibraryScans lock ordering, MaterialFitJobs cancellation/staleness logic, TextureItems/TexturePurposes grouping logic.
- **S13** — read: all 30 files under Dalamud-Plugin/Services/PenumbraMods read fully (plus callers/callees for context: Studio/Services/Project/ModFiles.cs, Services/PathRules.cs, Services/ModelBackupStore.cs, Services/ExportContextRegistry.RemapModPaths, ExportServer mashup branch, Studio WriteLedger/DuplicateFiles, Animations/AnimationCommitService mapping callers).  
  Clean: path-containment checks before every write (IsPathWithin + reparse-point walks) are consistent; temp-file + File.Move(overwrite) is used for every in-place write (meta.json, MDL, MTRL, TEX, PHYB, SKLB); all meta.json read-modify-write cycles take Studio ModFiles.Gate; FrameworkThread hops (LeaveFrameworkThread) are placed correctly before file work; no async void / .Result on the framework thread; ImGui is not used in this slice; Penumbra v4 option-group handling (Single/Multi/Imc/Combining Containers, DefaultSettings, priority ordering, FileSwaps, Manipulations) preserves unknown fields by round-tripping JsonNode and non-v4 mods are refused rather than rewritten.
- **S14** — read: read fully - MaterialDocument, ColorTableRows, ColorTablePacking, LegacyConversion, MaterialSession, MaterialHistory, MaterialSource, MaterialBinding, MaterialVariants, IndexUsage, MaskPainting, TileTransform, LiveColorTable, LiveMaterial, MaterialPreviewService, StainTemplates, ModelUv2, PresetChecks, EffectPresets, MaterialEffectPreset, ShaderPackages, MaterialPreviewBundleBuilder (+TexturePlan), every Ui/Material file (window, Rows, Dye, Textures, Shader, Values, Presets, Copy, Variant, Usage, Uv2, Live, TextureArraySlices, MaterialWindows). Skimmed/checked by script - ShaderLabels (no duplicate ids/values), ShaderPackages.Generated (no duplicate ids, sizes match float counts, no overlaps). Also read, for context, Studio/Services/MtrlReader.cs, MaterialPatcher.cs, PreviewModWriter.WritePreviewSourcesAsync and the Blender material_preview.py manifest validator. One experiment (scratchpad work-S14/unparsed) for S14-1.  
  Clean: ImGui Push/Pop balance in Ui/Material (all Begin/Push pairs are ImRaii or have no early return between Begin and End; manual BeginPopupModal sites call EndPopup on every path; ids are unique per row/slot/key); MTRL header/texture/UV/colour-set/shader-section layout and string-block reuse; Dawntrail 32-row (64-byte rows, 128-byte dye) and legacy 16-row (32-byte rows, 32-byte dye) sizes, flag bits, row halves 0..31 and dye bit maths (0/15/31 edges checked in Pack/Light, pair/row maths, IndexUsage, MaskPainting, LegacyIndexTexture); tile (index+0.5)/64 packing; TileTransform round trip (100k random matrices); LiveColorTable/LiveMaterial refcount and pointer re-validation; MaterialPreviewService record/cleanup/ownership checks; bundle builder manifest vs Blender validator (limits, field names, colourSet 1024-value cap, texture/decode caps); ModelUv2 MDL V6 offsets (header 68, declaration 136, mesh 36, LOD 60 with +44/+52/+56 patched, extra LOD 40); atomic save path (new files first, temp+move, backup, hash check) in PreviewModWriter.
- **S16** — read: read fully: TextureBackupStore, TextureCompressionService, TextureCrop, RefitModel, RefitMaterial, RefitPlanner, ModFileMap, TextureCost, SingleColor, VerifiedEncode, CompressionCheck, CompressionCapture, NeckSeamService, NeckSeamFixer, NeckSeamCapture, SkinSeamPreviewPlan, SkinMaterial, NeckSeamMaterialMatch, SkinModel, SeamGeometry, SeamSpace, SeamSurroundings, SkinMeshDeformation, BodySeamFixer, SkinToneFixer, SkinToneComparison, SkinToneAnalyzer, NeckSeamAnalyzer; BodySeamAnalyzer read fully except the Miss/Unmet tail details (skimmed). Context read: ModFileService.ReplaceModFileAsync, FileStamp, ModPaths safety checks, PreviewModService.ApplyAsync/DiscardAsync, PreviewModWriter.DeletePreviewModAsync, OnScreenService.CaptureLocalPlayer.  
  Clean: backup/journal write path (hash-verified temp+move backups, journal written before the file is replaced, replace-in-one-move with expected-hash check, rollback of a failed refit group); compress/restore ordering for refit-then-compress and compress-then-refit; TextureCrop/UvWindow maths (power-of-two windows, mip alignment, block alignment, NPOT sizes, exact-UV round trip); RefitModel/RefitMaterial layouts and tile-matrix column scaling; CompressionCheck channel order (BGRA byte indices) and tile maths; SingleColor/VerifiedEncode; SkinModel.WithNeckMorphs offset shifting; SkinMaterial.WithConstants; BodySeamFixer weld/meet maths; SeamImage addressing; culture-dependent formatting only in display strings.
- **S17** — read: all 29 files read fully (ExportServer.cs + .Race/.Sizes/.Skeletons/.RacialDeformer, ExportContextRegistry, ExportContextSessionStore, BlenderClient, ModelBackupStore, TextureEditService, TextureFiles, PathRules, OnScreenService and the small helpers). Cross-read for contract: Blender-Addon/instant_edit/server.py, plugin_http.py, recovery.py, revocation.py, cache.py, the payload builders in ops.py, Tests/TestSupport/context-protocol.json, Models/ExportContext.cs, PenumbraMods/ModelBackupService.cs.  
  Clean: HTTP request handling is well hardened (Origin/Sec-Fetch-Site/Host refusal, size limits, UTF-8/JSON strictness, per-endpoint capability check with fixed-time compare, hash+size verified on the staged copy rather than the source); context registry locking and persist ordering; export idempotency/receipts; backup naming/Resolve/Clear containment (`..`/separators cannot get through the regex); TextureFiles.Replace (temp + File.Replace + hash checks around the backup); TEX/TGA header validation; context-protocol.json, schemas, versions, field names and status codes agree between the plugin and the add-on for every endpoint I compared. No JSON/enum/unit mismatch found.
- **S18** — read: read fully: all of Painter/ (client, installation, job service/store, project builder, channel map, TGA, UV coverage, OBJ, skin/smallclothes, live reader), Setup/ (BlenderSetup, InstalledSoftware, TextureEditorSetup, install_blender_addon.py), PreviewMods/, Actors/, CharacterSend/, Previews/ (all decoders, cache, service), GameFiles/ (all), Testing/BridgeTools.cs; compared against Tools/SubstancePainter/xiv_instant_edit/{bridge,client,manifest,painter_job,plugin,settings}.py (protocol, field names, limits, version gate all agree).  
  Clean: Painter job protocol and capability handling (FixedTimeEquals, path/key/sendId validation, 64 KB/256 KB body caps, Origin/Host refusal on the Python side), Painter manifest contract (sizes, uvScale set, name regex, channel names), Dalamud object access (every game-object read in Actors, PainterLive, CharacterWeapons goes through RunOnFrameworkThread/framework Update), process launching (argument lists, no shell, robocopy quoting), TGA reader bounds, TextureDecoder (mip/format/dimension checks), game export path containment and cache cleanup, partner spawn/despawn bookkeeping.
- **S21** — read: all 21 files in Blender-Addon/instant_edit/ read in full (server.py, cache.py, context.py, ops.py, steps.py, revocation.py, recovery.py, animation.py, animation_read.py, skeleton.py, character.py, racial_scaling.py, sizes.py, stash.py, props.py, __init__.py, diagnostics.py, plugin_http.py, validation.py, hair_rig.py, material_preview.py); cross-read BlenderClient.cs, ExportServer*.cs, ModelBackupStore.cs, MaterialPreviewBundleBuilder.cs, ModelSender.cs, StashView.cs, context-protocol.json. Ran bpy 5.2 experiments against the real server/cache/animation modules (scratch in work-S21/).  
  Clean: HTTP listener trust model (Host/Origin/Sec-Fetch-Site checks, per-endpoint Content-Type, Content-Length/size caps, loopback-only bind, exclusive bind on Windows) behaves as documented when probed over a real socket; bpy is never touched from worker/handler threads (all worker code paths are pure I/O); cache job ownership checks (UUID names, marker, symlink refusal) and the backup sweep; revocation/recovery generation handling; material-preview path containment; JSON field names/limits of every endpoint I compared against the C# request classes (import, export, race, sizes, mashup, variant-targets, backup, skeleton, character, preview manifest) agree.
- **S22** — read: read fully - __init__.py, backups.py, keymaps.py, materials.py, mesh/* (armatures, export, face_order, heels, hidden, objects, racial_scaling, shapes, transforms, vertex_data, weights), mesh_list.py, operators.py, pose.py, preferences.py, properties.py; skimmed ui.py (draw code; read its mesh-group, pose, backup, session and file-IO panels). Ran bpy 5.2 experiments (bmesh delete with shape keys/custom normals, apply_transforms with custom normals, FBX/glTF operator kwargs, FBX export of visible objects, ambiguous-context draw) and fuzzed the pure-Python reorder/untangle/split planners (moved_part, untangled, split_mesh_plan: no invariant violations in 20k-30k random cases).  
  Clean: mesh_list.py planners, mesh/hidden.py (bmesh round trip keeps shape keys, custom normals, UVs, colours, vertex groups), mesh/transforms.py, mesh/racial_scaling.py weighting, backups.py (atomic replace, backup before overwrite, path containment), keymaps.py, register/unregister symmetry in __init__.py, pose.py (4.5/5.x action API handled), mesh/heels.py, armatures.py rollback logic.
- **S23** — read: read fully: xivpy/model/*.py + utils.py, io/model/{exporter,importer,handler}.py, exp/*.py, imp/*.py, com/*.py, all of Tools/SubstancePainter/xiv_instant_edit, scripts/*.ps1, both workflows, manifest, index.json, both repo.json, Photoshop/Krita/GIMP scripts; skimmed: *.csx tools, READMEs/notices, CONTRIBUTING, .gitignore/.editorconfig. Experiments: synthetic MDL write->read round trip (bone tables with 0/1/2/3 entries, 0 tables), 3000-case byte-flip fuzz of `XIVModel.from_bytes`, Painter unit tests (21 pass), committed zip hash vs index.json (matches).  
  Clean: MDL struct sizes/offsets/padding (header 0x44, mesh header 56, LOD 60, mesh 36, submesh 16, bbox 32), string-offset order, bone-table offset arithmetic and the 16-byte-aligned data pad all round-trip correctly; reader bounds are exception-based (EOF/ValueError/IndexError, no hangs or huge allocations); committed index.json size/hash match the committed 2.1.0 zip; manifest permission strings/tags/tagline within Blender limits; Painter bridge (Host/Origin/Sec-Fetch checks, body cap) and manifest path confinement are sound; .csx tools guard their output with `StartsWith(Mod + "\\")` and fail closed.

**Not reviewed:** S02 Animation preview and timeline model; S04 Timeline UI: editing core; S05 Timeline UI: motion, layers, bones; S06 Timeline UI: canvas, preview, pickers; S10 Studio windows: main; S11 Studio windows: build, panels, UI; S12 Studio models, plugin root; S15 Physics and heels; S19 Audio, skeletons, pose UI, UI root; S20 Main window UI; S24 Tests and documentation. These were left out to keep the cost of the review down: the UI layout code (Timeline, Studio windows, main window) is the most game-dependent and least likely to hold data-handling defects, and the rest (animation preview/timeline model, physics, the plugin root and models, the test projects and documentation) can be reviewed with the same brief and slice lists if wanted.

### Method and limits

- The game, Penumbra, Dalamud at runtime, Substance Painter and the Blender GUI were not available. Dalamud's development assemblies and `bpy` 5.2.0 were, so the plugin was compiled and the C# and Python suites were run.
- Reviewers were told to try to refute each candidate before reporting it, and to leave style and speculation out. A clean area in the lists above means the reviewer looked and found nothing, not that nothing could be there.
- Findings marked fixed were re-checked against the code here before being changed. Fixes follow the code's own conventions and comments; none changes a file format or a wire protocol (the one new shared constant, the `/import/files` limit, was already 64 on the add-on side).
