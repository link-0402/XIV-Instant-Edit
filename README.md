# XIV Instant Edit

Instant Edit is Final Fantasy XIV Dalamud plugin that serves as a method of instantaneously moving vanilla game or mod resources into editing software and replacing those edited assets back into the game without requiring additional manual user import, export or setup work.
Model editing uses a Blender add-on to manage model life cycle and allows for exporting of both vanilla as well as modded Penumbra mod model files.
Additionally, it supports full software-independent texture editing as well as animation editing.

Animation skeleton repair resolves the animation's source reference pose from
installed SKLB files, including source skeletons embedded in Havok mappers, and
uses the source chosen in the animation editor as its sole processing skeleton.
LivePose rebakes use that same selected source skeleton: they apply the offsets
directly to the animation and do not retarget through the character's live
skeleton. Predictive startup clips can use a different embedded source from
their uncompressed loops. If several sources fit equally well, choose the source
variant in the animation editor. A missing source reference pose cannot be
bypassed with Shift.

## Requirements

- [XIVLauncher](https://goatcorp.github.io/) with Dalamud enabled
- [Penumbra](https://github.com/xivdev/Penumbra) 1.7.1.0+
- [Blender](https://www.blender.org/) 4.5.0+ for model editing
- (optional) an Image Editing Software of your choice (with TGA format support)

## Installation

### Install the Dalamud plugin

1. In FFXIV, run `/xlsettings` and open **Experimental**.
2. Add this URL under **Custom Plugin Repositories**:

   `https://raw.githubusercontent.com/link-0402/XIV-Instant-Edit/main/Dalamud-Plugin/repo.json`

3. Enable the repository, save your settings, and open `/xlplugins`.
4. Find and install **XIV Instant Edit** under **All Plugins**.
5. When opened for the first time, run through the guided first-time setup, defining a cache location for temporary files used in exports as well as a Photo Editing software of your choice (optional).

### Install the Blender add-on

1. Open **Edit > Preferences > Get Extensions** in Blender.
2. Click **Repositories**, click **+**, and choose **Add Remote Repository**.
3. Add this repository URL:

   `https://raw.githubusercontent.com/link-0402/XIV-Instant-Edit/main/Blender-Addon/blender_repo/index.json`
4. It is **Strongly recommended** to tick **Check for Updates on Startup** to receive automatic updates. 
   An out-of-sync version of the plugin and Blender add-on may lead to unexpected behavior and is not supported.
   Blender's automatic extension updater can be unreliable, please verify the versions match and are up-to-date before submitting issues.
5. Find **XIV Instant Edit** in the list and install it.


## How to: Model Editing

1. Type /ie to open the plugin interface ingame. Click Refresh character list.
2. Start Blender. Verify the plugin shows Blender as "Online".
3. Verify import options both in-game and inside the add-on, then click "Edit" on the model you want to import to Blender.
4. Edit the model as you normally would.
5. Pick the desired export context from the list:
   - "In-place" overwrites the exact model that you imported. This is the default export location.
   - "New Group" sets up a new option group in the mod you imported from and automatically configures the paths for you. Define the new group and option names in the inputs below.
   - (Existing group) creates a new option in an existing option group. Define the new option name in the inputs below.
   - (Option within existing group) overwrites the model file that is mapped to this option.
   - "Create Mashup" creates a new mod or group in an existing mod containing the combined model + all required textures and materials. Only visible with 2+ mods imported into and visible in the scene, otherwise switches to "Create as new mod".
   The context dropdown controls which mod structure is being shown.
6. Press "Quick Export". You'll immediately see the results updated in-game (if the model is currently used on your character).

## How to: Texture Editing

1. Type /ie to open the plugin interface ingame. Click Refresh character list.
3. Select **Textures** in **On Screen** or **Mod Browser**, then **Edit texture**.
   For vanilla textures, enable **Include Vanilla** and enter a new mod name.
4. Edit the opened TGA, and save that same file as **32-bit TGA with an 8-bit
   alpha channel**. Usually, simply hitting the save shortcut (for example Ctrl+S) is sufficient.
   Uncompressed and RLE TGA saves are supported. Keep the original
   dimensions. Layered documents need a flattened TGA copy saved over the working
   file.
5. After the save settles, Instant Edit converts it to the original TEX format,
   replaces the mod file with a backup, reloads the mod, and redraws the selected
   actor and the local player/owned entities. A vanilla override is created and
   enabled in the captured collection on the first changed save.


## Additional notes

The Blender plugin manages it's mappings to mods via the automatically created "Instant Edit [context_id]" collections. To ensure the addon works properly, do not move, rename, delete or otherwise edit these collections until you exported the model you were working on. Hiding them from the viewport removes the context temporarily as well. You can then easily remove an old context by simply removing it's collection, should you wish to continue other work in the same scene.

### Main Features
Main Features
- One-click import of models through an on-screen browser or simplified mod file browser.
- Easy export context selection. Export in-place, pick any existing mod option or easily create a new one. The plugin sets up everything for you automatically.
- Instant creation of mashups. The plugin automatically sets up all required textures, materials and paths for you.
- Seamlessly integrates into any existing Blender scene, independent of body, devkit, etc.
- Simple Importer / Exporter for general FBX and MDL files with various QoL functions and automations optimized for FFXIV workflows
- One-click import and export for textures

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
If you'd like to contribute to this project, see [CONTRIBUTING.md](CONTRIBUTING.md) for setup, testing, and submission guidance.

## License

This is an unofficial community tool and is not affiliated with or endorsed by
Square Enix, Dalamud, Penumbra, or Blender. The project is licensed under the
[GNU GPL-3.0-or-later](LICENSE).
