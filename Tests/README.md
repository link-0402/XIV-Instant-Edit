# Regression suite

Run from the repository root. Use Blender 4.5.0 or newer and .NET 10 with the
local Dalamud development assemblies installed. CI runs the Blender cases on
4.5.0 and 5.2.0; standalone Python tests do not require Blender.

```powershell
blender --background --factory-startup --python-exit-code 1 --python Blender-Addon/testing/bridge_regression.py
blender --background --factory-startup --python-exit-code 1 --python Blender-Addon/testing/smoke_export.py
blender --background --factory-startup --python-exit-code 1 --python Blender-Addon/testing/correctness_regression.py
python Blender-Addon/testing/cache_regression.py
python Blender-Addon/testing/diagnostics_regression.py
python Blender-Addon/testing/server_diagnostics_regression.py
dotnet run --project Tests/ExportContextRegression -c Release -p:SkipDistributionPackage=true
dotnet run --project Tests/BlenderStatusRegression -c Release -p:SkipDistributionPackage=true
dotnet run --project Tests/AnimationRegression -c Release -p:SkipDistributionPackage=true
dotnet build Dalamud-Plugin/InstantEdit.csproj -c Release -p:SkipDistributionPackage=true
```

PowerShell users should check `$LASTEXITCODE` after each command. Blender's
`--python-exit-code 1` must precede `--python`; without it a Python assertion can
print a traceback while Blender exits successfully.

The Blender fixtures register a test add-on with an isolated temporary cache,
stub listener startup, and clean scene data after each run. HTTP contracts are
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
  plus authorization, backups, resource bundling, migration, and mod metadata.
- `BlenderStatusRegression`: grouped connection states and bridge response,
  import handoff, diagnostic behavior, cache synchronization, and texture-session
  regressions in `TextureEditScenarios.cs`. Texture tests exercise the production
  validators, filesystem watcher, atomic replacement, and backup store with a
  simulated conversion/IPC backend; they do not test Penumbra's actual codecs.
- Standalone Python suites: cache ownership and cleanup, diagnostic sanitation
  and limits, import validation, and asynchronous failure reporting.
- `AnimationRegression`: PAP/SKLB envelopes, complete pose-stack shape fixtures,
  component filtering, timeline/VFX dependencies, metadata scope, durable recovery,
  and PAP backup conflict protection. Native Havok and actual IPC acceptance are
  documented in [animation editing acceptance](AnimationEditing.md).

## Test design

The automated suites check compilation, serialized contracts, validation,
state transitions, export/import results, authorization, path safety, backup
protection, and failure restoration. They intentionally avoid exact UI labels,
tooltips, display formatting, icons, and status-message wording. Live
FFXIV/Penumbra integration remains a separate manual check.

See [texture editing acceptance checks](TextureEditing.md) for the Photoshop and
live-game scenarios required before releasing texture editing.
