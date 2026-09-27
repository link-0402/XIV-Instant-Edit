# XIV Instant Edit for Substance 3D Painter

A Python plugin for Adobe Substance 3D Painter (10.0.1 or newer). Instant Edit's
On Screen browser sends a model and the exact textures your character uses to Painter,
and this plugin's **Send to game** button sends the painted textures back.

## Install

The easy way: in Instant Edit's Settings, tick **Paint textures in Substance Painter** and
press **Install Painter plugin**. It copies this folder, plus a `config.json` with the ports,
to `Documents\Adobe\Adobe Substance 3D Painter\python\plugins\xiv_instant_edit`. The
installed copy always matches your Instant Edit build. When Settings offers **Update Painter
plugin** (after an Instant Edit update, or when the installed files differ from the build's),
press it and restart Painter.

By hand: copy the `xiv_instant_edit` folder into that `plugins` folder yourself.

Then start Painter and enable the plugin once under **Python > xiv_instant_edit**. Painter
remembers it. The plugin adds an **XIV Instant Edit** panel and an **XIV** button to the
plugins toolbar.

## Use

1. In the game, use the paint-roller action on a model in Instant Edit's On Screen list, tick
   the textures to send back, and press **Send to Painter**. Painter must have no other project
   open.
2. Painter creates the project: one texture set per material, sized for its textures, with the
   current textures as the bottom **XIV original** layer. Paint on layers above it, and keep
   that layer.
3. Press **Send to game** (panel or toolbar). The plugin exports every texture in FFXIV's
   channel layout and hands the files to Instant Edit, which applies the changed ones and
   redraws. The panel lists the result for each texture.
4. Save the project wherever you like. Reopening it later links it to Instant Edit again.
   Until the first save Painter calls it "Untitled (Read only)"; saving works normally.

The plugin never exports on save and doesn't change Painter's own export dialog.

## What Painter shows

The project is set up to look like the character does in game:

- **Only what the character draws.** Parts of the model it has turned off (by gear,
  customization or mod options) stay out, and their texels keep what the texture has. Eye
  occlusion shading, which the game draws over the eyes, stays out too.
- **Transparency and back faces.** Each texture set gets a shader instance of Painter's Adobe
  Standard Material that follows its material: alpha blending for translucent materials (lashes,
  brows), alpha testing at the material's threshold for the others, and double-sided drawing
  where the game shows back faces. Brows, lashes and hair modelled as a front and a reversed back
  copy keep only one copy, drawn from both sides, so the two don't flicker against each other.
- **True texture proportions.** Painter shows every texture set as a square, so a set for
  non-square textures (a 1024×2048 face, say) is made square, and the textures fill its top-left
  corner at their own proportions. Exports are cropped back to the texture's size.
- **Color where the game doesn't use a texture.** Hair shaders (hair, brows, lashes) get the
  character's hair and highlight colors in base color; gear that colors by colorset gets the
  colorset colors its index texture picks. That base color is only for viewing: nothing sends
  it back, so edit the channels that are (normal, mask, index) to change those materials.

Mirrored parts, such as left and right brows or teeth that share one area of the texture,
overlap in the 2D view; switch it to a single channel to see the flat texture.

## Channels

Each FFXIV texture channel has one Painter channel:

| Shader | diffuse | normal | mask |
|---|---|---|---|
| gear (character, legacy, glass, …) | RGB base color, A user | RG normal, B opacity, A user | R specular level, G roughness (legacy: glossiness), B AO, A user |
| skin | RGB base color, A opacity | RG normal, B/A user | R specular level, G roughness, B scattering, A user |
| hair | RGB base color, A user | RG normal, B user, A opacity | R specular level, G roughness, B scattering, A AO |

User channels are labelled with their source, for example `normal.a`. Normals use OpenGL
(Y+) orientation, as FFXIV does. Channels the texture's format doesn't store (for example B/A
of a BC5 normal map) are not added.

## Ports

The plugin listens on `127.0.0.1:42426` and calls Instant Edit on `42428`. Change both in
Instant Edit's Settings; an installed plugin's `config.json` follows.

## Tests

From the repository root:

```bash
python -m unittest discover Tools/SubstancePainter/tests
```
