# XIV Instant Edit

XIV Instant Edit is a focused Blender extension for the XIV Instant Edit Dalamud
plugin. It contains only the model I/O and scene tools needed for the bridge,
plus a File Import / Export panel for working without the plugin.

## Features

- Receives secure, versioned `.mdl` import requests from XIV Instant Edit.
- Creates isolated import collections and preserves the authorized Penumbra
  export destination.
- Supports generated import armatures or a named existing scene armature.
  A generated armature has the game's skeleton, which the plugin sends with the
  import: the rest pose and hierarchy of the model's race skeleton, with your
  skeleton mods and the face, hair, headgear or top skeleton the model uses (an
  on-screen character's own skeleton when it can). Bones point along the game
  bone's Y axis, as in TexTools FBX imports and devkits. Model bones the skeleton
  lacks are placed at their weighted vertices and reported. **Import Model File**
  asks the running plugin for the skeleton when the file is named like a game
  model (`c0201e0123_top.mdl`); otherwise the bones sit at the origin.
- Receives animations from the plugin's **Animations** tab, either a recording
  of a character's live pose (including the game's bone physics) or a sampled
  animation file, and keys them as a new action on the named scene armature
  (else the active or only armature). Bones match by name and receive the game
  bone's movement in their own rest orientation, so glTF and FBX rigs work.
  Keys land on every scene frame from the start frame, at the scene frame rate,
  and the scene's end frame follows the action. Scale is keyed only on request.
- Displays and assigns the FFXIV material path for every visible mesh group,
  with quick selectors for the materials used by the model's other mesh groups.
- Highlights only the mesh-part rows whose normalized export material is
  missing or differs from the first part used by their mesh group.
- Optionally builds import-local, packed Principled BSDF previews from the
  effective game/Penumbra MTRL and TEX resources supplied by the plugin.
- Supports adding new visible mesh parts and groups anywhere in the scene using
  YAA-compatible `group.part Name` object names.
- Includes a **Tools** action to convert suffix-form mesh IDs from Textools/FBX
  scenes into the prefix naming convention.
- Combines two armatures into a new rest rig through **Tools > Combine
  Armatures**. Choose the base rig and an additional rig; shared bone names use
  the base definition. Both originals are kept and hidden in the current view
  layer, and only visible meshes using them are reassigned. Existing vertex
  groups and weights stay unchanged; create groups for newly available bones
  as needed. Pose, animation, and rig controls remain on the originals. Undo
  restores the original visibility and mesh bindings.
- Quick Export back to the original source mod, including variants and optional
  Penumbra setup.
- Saves a single-context export as a new Penumbra mod, bundling material and
  texture files owned by participating mods.
- Creates multi-context mashups as a new group in the active mod or as a new
  Penumbra mod. The creation popup can optionally bundle eligible materials and
  textures from non-participating mods. Vanilla resources and shared body/skin,
  pube, and piercing materials remain pass-through dependencies; any remaining
  required external mods are listed in the result and mod description.
- Offsets incoming MDL mesh-group IDs away from visible conflicts without
  changing group assignment based on materials.
- Persists import authorization in the Dalamud plugin and reconnects saved scene
  contexts after Blender or plugin restarts.
- Recovers export receipts after network timeouts without submitting a second
  write, and durably queues context revocations while the plugin is offline.
- Redraws the local player and their currently spawned summons, minions, and
  mounts after Quick Export.
- File Export to MDL, FBX, or glTF for the meshes chosen by **Export Parts**
  (all visible meshes, all except one mesh, or the Context collection); Quick
  Export uses the same setting.
- File Import from MDL, FBX, or glTF (.gltf/.glb) files; the format follows the
  file extension.
- Export-time vertex data fixes: UV2 (keep, copy UV1, or clear), vertex color 1
  (keep, clear alpha, or clear), and vertex color 2 and flow-data cleanup.
- Optional armature scaling reset (**Options > Export > Reset Armature
  Scaling**); armature rest-pose neutralization and complete state restoration
  remain automatic.
- SimpleHeels offsets: add `heels_offset=0.15` as a custom attribute to a mesh
  part, or enable **Options > Export > Calculate Heels Offset** to measure how
  far the LOD 0 geometry reaches below the floor and write that value on the
  first mesh part at export. The calculated value replaces manual heels
  attributes; when nothing is below the floor, no offset is added and manual
  attributes are kept.

Meshes in one export may resolve to different Blender armatures. Each mesh uses
its parent armature when present, otherwise its first valid Armature modifier.
Every resolved rig is evaluated in rest pose. The resulting FFXIV MDL merges the
used bone names into one bone list; it does not preserve separate Blender
armature identities.

Material previews are display-only and intentionally approximate. They do not
include actor colors or dye baking, do not add material/texture editing, and
are never included in Quick Export. Missing preview resources fall back to the
existing colored placeholder without blocking geometry import. Gear using
`character.shpk`-family colorsets and index textures bakes the colorset lookup
(base color, roughness, metalness, emission) at the index texture's size and
combines it in the node tree with the diffuse, mask, and normal textures at
their own sizes. Materials flagged translucent render with Blender's Blended
method; other materials cut out at their alpha threshold. File Import does not
resolve FFXIV resources.

When the Dalamud **Exclude body and general materials** sub-option is enabled,
body skin, body-piercing, and pube slots intentionally retain their colored
placeholders without producing missing-preview warnings.

## Sidebar

The **IE - Instant Edit** tab of the 3D Viewport sidebar holds these panels:

- **Instant Edit**: the models sent from the game (Contexts). With several
  Contexts, each row has a visibility toggle and a radio button choosing the
  one Quick Export writes back. Below it are the export target (the imported
  file in place, an existing option, a new option or group, a new mod, or a
  mashup), any problems that block or weaken the export, and the Quick Export
  button. The link icon in the header shows whether Blender is listening for
  the plugin.
- **Mesh Groups**: one box per mesh group with its vertex count, parts,
  attributes, and material, followed by the triangle count. Click a part's name
  to select it (Shift-click adds or removes it), **+** to add an attribute, and
  an attribute to remove it. To move a part or a whole group, click its grip,
  move the pointer to where it should go (the list shows the new order under the
  pointer, including in other groups), and click again to drop it. Right-click a
  part's name for **Generate Duplicate with Backfaces**: it copies the part as
  the next part number, moves any part in the way up by one, and flips the
  copy's normals like Edit Mode's **Normals > Flip**.
- **File Import / Export**: import model files and export to a folder without
  the plugin.
- **Options**: import, export, and vertex data settings shared by Quick Export
  and File Export.
- **Backups**: the header checkbox keeps a backup before a model is replaced;
  the panel lists the backups of the current target, which can be imported or
  restored.
- **Tools**: Combine Armatures, mesh naming helpers, clearing all Contexts, and
  the cache and diagnostics folders.

## Installation

Build or install the extension ZIP through Blender's Extensions preferences.
The source directory itself can also be used for development with Blender 4.5.0
or newer.

Do not enable this extension at the same time as a custom Yet Another Addon
build that also contains the XIV Instant Edit listener. Both would attempt to own
the same local port (42424 by default). The unmodified upstream Yet Another
Addon can coexist because it does not provide that listener.

The connection ports can be changed in the extension preferences. The cache
directory and automatic cleanup are configured in the in-game plugin settings.
All controls are in the **IE - Instant Edit** tab of the 3D Viewport sidebar
(see [Sidebar](#sidebar)).

## Attribution and license

This extension is derived from
[Yet Another Addon](https://github.com/Arrenval/Yet-Another-Addon) by Arrenval
and vendors the relevant portions of
[XIVPy](https://github.com/Arrenval/XIVPy).

It is distributed under the GNU General Public License version 3 or later.
See `LICENSE`, `xivpy/LICENSE`, and `THIRD_PARTY_NOTICES.md`.
