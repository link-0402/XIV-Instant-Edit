# Regression suite

Run from the repository root. Use Blender 4.5.0 or newer and .NET 10 with the
local Dalamud development assemblies installed. CI runs the Blender cases on
4.5.0 and 5.2.0; standalone Python tests do not require Blender.

```powershell
python Blender-Addon/testing/run_blender_suites.py
python Blender-Addon/testing/cache_regression.py
python Blender-Addon/testing/diagnostics_regression.py
python Blender-Addon/testing/server_diagnostics_regression.py
dotnet run --project Tests/ExportContextRegression -c Release -p:SkipDistributionPackage=true
dotnet run --project Tests/BlenderStatusRegression -c Release -p:SkipDistributionPackage=true
dotnet run --project Tests/AnimationRegression -c Release -p:SkipDistributionPackage=true
dotnet run --project Tests/ChangelogRegression -c Release -p:SkipDistributionPackage=true
dotnet build Dalamud-Plugin/InstantEdit.csproj -c Release -p:SkipDistributionPackage=true
```

PowerShell users should check `$LASTEXITCODE` after each command.

`run_blender_suites.py` runs `bridge_regression`, `smoke_export`, and
`correctness_regression`, or the suite names and script paths you pass, as
`blender --background --factory-startup --python-exit-code 1 --python <script>`.
Pass `--blender <path>` to test a Blender that is not on PATH. Each script gets
a new temporary Blender user profile. The fixtures call
`bpy.ops.wm.read_factory_settings`, which, with no extensions enabled, rewrites
`extensions/.cache/compat.dat` and deletes every wheel from `extensions/.local`.
In your own profile that removes libraries such as SciPy from the extensions
you have installed. `addon_session` therefore refuses to run unless all of
Blender's user directories are inside the `XIV_IE_TEST_PROFILE` directory.

To launch Blender directly, set `BLENDER_USER_RESOURCES` and
`XIV_IE_TEST_PROFILE` to the same new, empty directory and clear any other
`BLENDER_USER_*` variable. Keep `--python-exit-code 1` before `--python`;
without it a Python assertion can print a traceback while Blender exits
successfully.

The Blender fixtures register a test add-on with an isolated temporary cache,
point `APPDATA` into the same temporary directory so cache settings and
diagnostic reports stay out of the Dalamud plugin's configuration folder, stub
listener startup, and clean scene data after each run. HTTP contracts are
tested through explicit transport stubs. The bridge suite now lives under
`Blender-Addon/testing`, which packaging already excludes. None of these commands
updates the distribution archives or extension repository index.

## Coverage organization

- `bridge_regression.py`: manifest status, model stream options, isolated
  material previews, then the import/context/target-selection workflow.
- `smoke_export.py`: UV seams, mesh IDs and naming, isolated Simple Import folder
  handling, Mesh Studio operations, combined rest rigs and visible mesh bindings,
  and actual MDL export/round-trip workflows (including newly weighted bones).
- `correctness_regression.py`: injected transparency, backface, and shape-key
  preparation failures; armature-combination validation and rollback; scheduled
  and active workers across file loads; durable
  revocation retries and stale-result rejection.
- `ExportContextRegression`: named session-store and variant-export scenarios,
  plus authorization, backups, resource bundling, migration, mod metadata, and
  collection activation (`CollectionActivationScenarios.cs`: enabling a new mod
  must not depend on the redraw step), and the window view models
  (`UiViewModelScenarios.cs`: kind filters, search, expansion keys, status feed,
  session grouping and the mod-view builder, all without ImGui).
- `BlenderStatusRegression`: grouped connection states and bridge response,
  import handoff, diagnostic behavior, cache synchronization, and texture-session
  regressions in `TextureEditScenarios.cs`. Texture tests exercise the production
  validators, filesystem watcher, atomic replacement, and backup store with a
  simulated conversion/IPC backend; they do not test Penumbra's actual codecs.
  `PreviewScenarios.cs` covers the hover-preview cache policy (LRU, byte budget,
  invalidation, failure caching, disposal with loads in flight) and the CPU
  texture decoder, without a GPU.
- Standalone Python suites: cache ownership and cleanup, diagnostic sanitation
  and limits, import validation, and asynchronous failure reporting.
- `AnimationRegression`: PAP/SKLB envelopes, complete pose-stack shape fixtures,
  component filtering, timeline/VFX dependencies, metadata scope, durable recovery,
  and PAP backup conflict protection. Native Havok and actual IPC acceptance are
  documented in [animation editing acceptance](AnimationEditing.md).
- `ChangelogRegression`: release catalog ordering and uniqueness, version-aware
  auto-open behavior, and configuration persistence for the last-seen release.

## Test design

For local skeleton-repair diagnosis, export a predictive PAP's Havok payload and
its source SKLB payload to XML with XAT, then run:

```powershell
dotnet run --project Tests/AnimationRegression -c Release -p:SkipDistributionPackage=true -- --skeleton-repair-xml "startup.xml" "skeleton.xml"
```

This optional fixture reads the exported skeletons and mapper endpoints, runs
production discovery/ranking, validates the original predictive buffers and
binding against the selected source, and retargets its reference pose. It does
not invoke native decompression, compression, or live game playback. The XML
inputs remain local and are not bundled with the regression suite.

The automated suites check compilation, serialized contracts, validation,
state transitions, export/import results, authorization, path safety, backup
protection, and failure restoration. They intentionally avoid exact UI labels,
tooltips, display formatting, icons, and status-message wording. Live
FFXIV/Penumbra integration remains a separate manual check.

See [texture editing acceptance checks](TextureEditing.md) for the Photoshop and
live-game scenarios required before releasing texture editing.
