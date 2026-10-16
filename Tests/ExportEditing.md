# Model export acceptance

Exports into Penumbra mods are covered by `Tests/ExportContextRegression` (RaceExportScenarios,
SizesExportScenarios, VariantExportScenarios, attribute group scenarios) and the Blender suites
(`run_blender_suites.py`). Nothing below has been tried in game yet. Work on copies of mods you
care about, or keep Instant Edit's model backups on. Candidates are mods from G:\Penumbra; the
character is assumed to be c0801.

## Review fixes 2026-10-10 (pending)

Ticked items were verified in game on 2026-10-10 by the XIV Bridge in-game tests
(`XIV-Bridge/tests/instant-edit/exports`, run with `python -I tools/run_tests.py tests/instant-edit/exports`):
requests to a copy of the plugin's export receiver on its real Penumbra backend, as Blender sends them, on
copies of the named mods and test mods, and checks on the mods' files, options and what the character loads.
The Blender items ran headless with the add-on and Magic Fit from their repositories.

Committed 313e1ad. The Blender items need the add-on from the repository: the add-on zip and its
repository index haven't been rebuilt for them yet.

Race export (Save as c0801 Model):
- [x] Shared file: kitty headphones ("tamamo headband / regular headphones" maps
  common\3\c0201e5035_met.mdl at both the c0201 and c0401 paths) or Goo Girls ("Tail on Earring",
  c0201 and c1801). Wear it on the c0801 character so it loads the c0201 path, import it and Save as
  c0801 Model. A new c0801e5035_met.mdl appears; the c0401 (or c1801) mapping still points at the
  old file, unchanged in date and size; other races look as before.
- [x] Put an unmapped file named c0801e5035_met.mdl in the mod's folder first and repeat: the
  export writes "c0801e5035_met 2.mdl" and leaves the stray file alone.
- [x] Restore: with backups on, export as above, then Restore the backup listed for that import in
  Blender. In Penumbra > Edit Mod the c0801 mapping and the EQDP entry the export added are gone,
  the new c0801 file is deleted (if unchanged), and the character looks as before the export.
  Note: the real kitty headphones also map a c0801 file of their own, and the game gives c0801 its own
  e5035 model, so the tests' copy drops that mapping and turns the c0801 EQDP model flag off. The
  character doesn't wear the headphones; XIV Bridge test 04 repeats the export and Restore on shoes the
  character wears (loaded from the c0201 path) and checks what it draws.

Magic Fit sizes:
- [x] Sizes into an existing group whose default is option 1: the default stays on option 1.
- [x] A size whose option file another group also maps gets a new file; the other group keeps its
  file.
- [x] More than 64 sizes: Blender's readiness box shows "Pick at most 64 sizes." before exporting.
  (XIV Bridge test 10, 2026-10-10, with Magic Fit itself: the box says so before exporting, Quick Export refuses
  and sends nothing; the plugin refuses 65 sizes itself (invalid_sizes).)
- [x] A size that fails to write partway (for example a locked file) says which sizes' models were
  already replaced.

Single variant export:
- [x] Exporting a variant into an existing group keeps that group's default option.

Part toggles:
- [x] Export a model with part-toggle tags in place into a mod with per-variant IMC entries (for
  example 2B Top or Bean Top). Expect a warning about mixed variant entries, the atrx_ toggle group
  written, no Instant Edit IMC group left behind, and the mod's variant colours still switching.
  Note: checked on a Bean Top copy (its Bra options set variant 24 to material 1; 2B Top's entries
  match the game's, so it doesn't warn). The character doesn't wear the top, so "colours still
  switching" was checked as the mod's own IMC entries staying as they were with no IMC group.
  Without the import's metadata snapshot (resource manifest capture failed), the same export now
  writes no IMC group and warns that the import didn't read the mod's variant settings; atrx_ is
  still written (2026-10-10; offline in VariantExportScenarios, NecklaceUnknown).

Apply Racial Scaling (Blender Tools):
- [x] Duplicate a mesh as a reference, apply racial scaling to c0801, then in game toggle a skeleton
  mod that ships its own human.pbd (IVCS or YAS) and redraw. Apply back to the mesh's own race: it
  matches the reference exactly.
  Note: IVCS and YAS leave the game's bones' c0201-to-c0801 matrices as they are and add their own
  bones' matrices, so the test's mesh is weighted to both, and it toggles IVCS both ways (off to on, on
  to off). Largest difference from the reference: 1.9e-9 m.
