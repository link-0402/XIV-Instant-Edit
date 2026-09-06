# Regression suite

Run from the repository root. Use Blender 4.5.3 or newer and .NET 10 with the
local Dalamud development assemblies installed. CI runs the Blender cases on
4.5.3 and 5.2.0; standalone Python tests do not require Blender.

```powershell
blender --background --factory-startup --python-exit-code 1 --python Blender-Addon/testing/bridge_regression.py
blender --background --factory-startup --python-exit-code 1 --python Blender-Addon/testing/smoke_export.py
blender --background --factory-startup --python-exit-code 1 --python Blender-Addon/testing/correctness_regression.py
python Blender-Addon/testing/cache_regression.py
python Blender-Addon/testing/diagnostics_regression.py
python Blender-Addon/testing/server_diagnostics_regression.py
dotnet run --project Tests/ExportContextRegression -c Release -p:SkipDistributionPackage=true
dotnet run --project Tests/BlenderStatusRegression -c Release -p:SkipDistributionPackage=true
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
  handling, Mesh Studio operations, and actual MDL export/round-trip workflows.
- `correctness_regression.py`: injected transparency, backface, and shape-key
  preparation failures; scheduled and active workers across file loads; durable
  revocation retries and stale-result rejection.
- `ExportContextRegression`: named session-store and variant-export scenarios,
  plus authorization, backups, resource bundling, migration, and mod metadata.
- `BlenderStatusRegression`: grouped connection states and bridge response,
  import handoff, and diagnostic behavior.
- Standalone Python suites: cache ownership and cleanup, diagnostic sanitation
  and limits, import validation, and asynchronous failure reporting.

## Removed or consolidated checks

| Previous check | Retained behavior coverage |
| --- | --- |
| Exact Dalamud version-mismatch sentence | Matching/mismatching versions, missing/invalid version data, failed connections, and diagnostic fields remain covered. |
| Four exact bridge target tooltips: In-place, New Group, existing group, existing option | Target selection, authenticated payloads, destination display, option overwrite, and post-export selection remain covered. |
| Exact Save to new mod tooltip | Single-context eligibility, dependency failures, selection, and submitted destination/contributors remain covered. |
| Exact Create Mashup tooltip under a coverage warning | The test still checks that this target does not receive the missing-coverage warning; other targets still must receive it. |
| Full Context-sentinel label/help tuple | The permanent sentinel's identity and selector behavior remain covered without freezing its display text. |
| Absence of removed material-collapse helper and operator | Symbol absence has no current behavior contract. Mesh-group conflict handling and per-group material assignment remain covered. |
| Separate missing-version, invalid-version-type, and malformed-document probe blocks | All three inputs remain as named cases in one table, with the same reachability and mismatch assertions. |

Geometry, authorization, path safety, backup protection, compatibility, and
failure-restoration tests are retained. The suite has no test-count reduction
target. Live FFXIV/Penumbra integration remains a separate manual check.
