# Animation edit crash investigation

The reported edit produced a false activation failure and a native access
violation. The investigation found three defects: native ownership of temporary
animation objects, physical path comparison, and rollback's registered-root lookup.

## Evidence

The 2026-09-12 21:54:25 Dalamud crash log records an access violation at
`ffxiv_dx11.exe+0x1F6FA61`. Offline disassembly of the installed executable places
this in allocator free-list traversal. RCX/R8 contain the invalid pointer
`0x6D0075006E0065`. This establishes damaged allocator state, but the dump does
not identify the earlier write that damaged it.

Offline inspection of the animation-control constructor at RVA `0x1F260B0`
and destructor at RVA `0x1F26160` establishes the relevant ownership contract:

- The low 16 bits at object offset `+0x8` hold the reference count.
- The high 16 bits at `+0xA` gate automatic reference management. Zero bypasses it.
- A control retains its binding, then releases it on destruction. A managed
  binding reaching reference count zero is passed to its deleting destructor.

The plugin's temporary binding copies used `0x00010000`, meaning count zero and
size one. Native control construction raised the count to one; destruction
lowered it to zero and allowed Havok to delete an allocation owned by the plugin's
arena. That also risked destruction of borrowed source members, followed by a
second free from arena cleanup. This concrete lifetime defect is consistent with
the delayed allocator crash; it is not a proven reconstruction of every event
leading to the crash.

The failed journal (`4d129ed3-6f76-4f6e-9d1a-6f1488e1ae3d`) recorded the target
under `G:\Penumbra\IE Animation 20260912 215419\files/chara/...`, while Penumbra
resolved the same file using backslashes throughout. Raw string comparison
incorrectly rejected this activation. The journal remained `Undoing`, with
offset clearing not started. The saved collection still had the generated mod
enabled when inspected.

Rollback then reported that the registered directory had changed. Its lookup
used Penumbra's virtual UI folder/sort path, rather than the physical mod root.

## Changes

- Raw and baked bindings use a shared arena-copy helper that disables automatic
  Havok reference management. Arena-owned interleaved animations use the same
  ownership rule. Source objects and their reference counts remain unchanged.
- Activation compares normalized absolute Windows paths and verifies every
  effective mapping and output hash before requesting a character redraw.
- New mods receive their priority before being enabled.
- Animation recovery reads the actual registered disk root from the synchronized
  mod-list adapter. Existing journals with mixed separators are accepted when
  they refer to the same root; genuinely different roots still fail recovery.

## Validation and recovery

All 130 animation regression checks passed. The ownership fixture models the
independently inspected native retain/release branches and reproduces deletion
with the old ownership word. Activation fixtures cover equivalent paths, actual
mapping conflicts, changed output, and redraw ordering. These tests do not invoke
native game code or establish stability during live baking and cleanup.

The Release build uses `SkipDistributionPackage=true`; distribution archives were
not regenerated for this fix. No live game actions or collection changes were
performed during this investigation.

Disable the failed `IE Animation 20260912 215419` mod in Penumbra and restart the
game before testing the rebuilt plugin. The rebuilt plugin's **Recovery → Undo
edit** can retry the pending rollback using the corrected root lookup. Recovery
must still be verified in game, followed by repeated edit/cleanup and playback
checks in [the acceptance checklist](AnimationEditing.md). Do not treat an
earlier successful PAP serialization check as proof that the failed run's output
or process memory was intact.
