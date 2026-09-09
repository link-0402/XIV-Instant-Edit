# XIV Instant Edit

Instant Edit is a combination of in-game Dalamud plugin and Blender addon that allows for an easy and instant exchange of game models between the two (.mdl files through Penumbra as well as vanilla assets).

## Requirements

- [XIVLauncher](https://goatcorp.github.io/) with Dalamud enabled
- [Penumbra](https://github.com/xivdev/Penumbra)
- [Blender](https://www.blender.org/) 4.5.0+

## Installation

### Install the Blender add-on

1. Open **Edit > Preferences > Get Extensions** in Blender.
2. Click **Repositories**, click **+**, and choose **Add Remote Repository**.
3. Add this repository URL:

   `https://raw.githubusercontent.com/link-0402/XIV-Instant-Edit/main/Blender-Addon/blender_repo/index.json`
4. *Recommended* to tick "Check for Updates on Startup" to receive automatic updates.
5. Find **XIV Instant Edit** and install it if it hasn't already.

You can also download [XIV-Instant-Edit.zip](https://raw.githubusercontent.com/link-0402/XIV-Instant-Edit/main/Blender-Addon/blender_repo/XIV-Instant-Edit.zip)
and use **Install from Disk** in the same Blender preferences window.
Note that you will not receive automatic feature and compatibility updates this way and will have to update the add-on manually.

### Install the Dalamud plugin

1. In FFXIV, run `/xlsettings` and open **Experimental**.
2. Add this URL under **Custom Plugin Repositories**:

   `https://raw.githubusercontent.com/link-0402/XIV-Instant-Edit/main/Dalamud-Plugin/repo.json`

3. Enable the repository, save your settings, and open `/xlplugins`.
4. Find and install **XIV Instant Edit** under **All Plugins**.

## How to use it

1. Type /ie to open the plugin interface ingame. Click Refresh character list.
2. Start Blender. Verify The plugin shows Blender as "Online".
3. Verify import options, then click "Edit" on the model you want to import to Blender.
4. Do whatever you wanna do with the model in Blender.
5. Pick an export context from the list
   - In-place overwrites the exact model that you imported.
   - New Group sets up a new option group in the mod you imported from and automatically configures the paths for you.
   - (Existing group) creates a new option in an existing option group.
   - (Option in existing group) overwrites the model file that is mapped to this option.
   - Create Mashup creates a new mod or group in an existing mod containing the combined model + all required textures and materials. Only visible with 2+ mods imported into the scene, otherwise switches to "Create as new mod".
   The context dropdown controls which mod structure is being shown.
6. Hit export. Immediately see the result ingame.
Creating new options on existing mods requires you to refresh the view in Penumbra by navigating to a different mod and back.

## Additional notes

### Texture editing

1. Open XIV Instant Edit Settings and enter the full path to your texture editor
   executable, such as `Photoshop.exe`.
2. Set the shared cache base directory in XIV Instant Edit Settings. Open `/ie`
   with the updated Blender addon running once so the plugin can synchronize that
   directory to Blender; Blender can then be closed for texture editing.
3. Select **Textures** in **On Screen** or **Mod Browser**, then **Edit texture**.
   For vanilla textures, enable **Include Vanilla** and enter a new mod name.
4. Edit the opened TGA, which keeps the original texture filename (for example,
   `c0101e0001_top_d.tga`), and save that same file as **32-bit TGA with an 8-bit
   alpha channel**. Uncompressed and RLE TGA saves are supported. Keep the original
   dimensions. Layered documents need a flattened TGA copy saved over the working
   file.
5. After the save settles, Instant Edit converts it to the original TEX format,
   replaces the mod file with a backup, reloads the mod, and redraws the selected
   actor and the local player/owned entities. A vanilla override is created and
   enabled in the captured collection on the first changed save.

The **Texture Edits** tab shows each working path, destination, and save status,
with controls to open the editor/folder, pause/resume, retry, restore the previous
backup, or discard the session. Restoration pauses the session and retains your
working image. Restarted sessions begin paused. A source-file or mapping conflict
also pauses the session: retain your working image and reopen the texture from
the browser to start from its current source. **Discard** deletes the session's
entire working directory, including any editing documents you saved there.

Texture working directories live under `texture-edits/<session-id>` in the
synchronized cache. **Automatic cache cleanup** is controlled from the in-game
plugin settings and applies to model cache jobs and paused texture sessions older
than 24 hours. Sessions with unsaved TGA changes are retained. Changing the cache
directory in XIV Instant Edit Settings affects new sessions; existing sessions
retain their paths. Managed TEX backups use the plugin's existing backup storage
and 30-day retention.

Supported originals are ordinary 2D TEX textures in BC1, BC3, BC4, BC5, BC7, or
BGRA32, up to 8192 × 8192. The plugin uses Penumbra's conversion API and explicitly
selects the original format; saving a TGA cannot silently turn a BC7 texture into
an uncompressed TEX. BC recompression is lossy, but the working TGA remains
uncompressed or losslessly RLE-compressed, and unchanged pixels skip encoding.
Mipmapped textures regenerate a complete chain capped at 13 levels; textures
without mipmaps remain single-level. Unusual formats, arrays, cubes, volumes,
resizing, DDS/PNG working images, and existing-option destinations for vanilla
textures are not supported in this first version.

Textures can be shared: all references to the overwritten mod file change.
Channel data is passed through without intentional color correction,
premultiplication, or normal-map reconstruction. Preserve all channels in your
editor, including RGB beneath transparent pixels. See the [manual texture
acceptance checks](Tests/TextureEditing.md) for Photoshop and live-game validation.

The Blender plugin manages it's mappings to mods via the automatically created "Instant Edit [context_id]" collections. To ensure the addon works properly, do not move, rename, delete or otherwise edit them until you exported the model you were working on. You can easily remove an old context by removing the collection along with it's context afterwards, should you wish to continue other work in the same scene.

### Main Features
Main Features
- One-click import of models through an on-screen browser or simplified mod file browser.
- Easy export context selection. Export in-place, pick any existing mod option or easily create a new one. The plugin sets up everything for you automatically.
- Instant creation of mashups. The plugin automatically sets up all required textures, materials and paths for you.
- Seamlessly integrates into any existing Blender scene, independent of body, devkit, etc.
- Simple Importer / Exporter for general FBX and MDL files with various QoL functions and automations optimized for FFXIV workflows

### Material preview

The optional texture import uses the effective MTRL and TEX files resolved for the
selected model and packs the generated images into the Blender file. It is a
practical Principled BSDF approximation, not an exact reproduction of FFXIV's
shader pipeline. Character gear without a diffuse map is composed from its
colorset, index, normal, and mask textures. Missing or unsupported
resources keep the existing colored placeholder and produce a warning without
blocking model import. Note that the texture import is for preview purposes only.
Making changes to them in Blender will not affect the exported model.

## Contributing

Bug reports and feature idea submissions are welcome.
If you'd like to contribute anything, see [CONTRIBUTING.md](CONTRIBUTING.md) for setup, testing, and submission guidance.

## License

This is an unofficial community tool and is not affiliated with or endorsed by
Square Enix, Dalamud, Penumbra, or Blender. The project is licensed under the
[GNU GPL-3.0-or-later](LICENSE).
