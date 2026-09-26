# XIV Instant Edit for Substance 3D Painter

A Python plugin for Adobe Substance 3D Painter (10.0.1 or newer). Instant Edit's
On Screen browser sends a model and the exact textures your character uses to Painter,
and this plugin's **Send to game** button sends the painted textures back.

## Install

The easy way: in Instant Edit's Settings, tick **Paint textures in Substance Painter** and
press **Install Painter plugin**. It copies this folder, plus a `config.json` with the ports,
to `Documents\Adobe\Adobe Substance 3D Painter\python\plugins\xiv_instant_edit`. The
installed copy always matches your Instant Edit version. After an Instant Edit update, press
**Update Painter plugin** and restart Painter.

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

The plugin never exports on save and doesn't change Painter's own export dialog.

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
