# XIV Instant Edit

Instant Edit is Final Fantasy XIV Dalamud plugin that serves as a method of instantaneously moving vanilla game or mod resources into editing software and replacing those edited assets back into the game without requiring additional manual user import, export or setup work.
Model editing uses a Blender add-on to manage model life cycle and allows for exporting of both vanilla as well as modded Penumbra mod model files.
Additionally, it supports full software-independent texture editing as well as animation editing.

## Requirements

- [XIVLauncher](https://goatcorp.github.io/) with Dalamud enabled
- [Penumbra](https://github.com/xivdev/Penumbra) 1.7.1.0+
- [Blender](https://www.blender.org/) 4.5.0+ for model editing
- (optional) an Image Editing Software of your choice (with TGA format support)
- (optional) the LivePose Dalamud plugin for animation editing (baking pose adjustments)

## Installation

### Install the Dalamud plugin

1. In FFXIV, run `/xlsettings` and open **Experimental**.
2. Add this URL under **Custom Plugin Repositories**:

   `https://raw.githubusercontent.com/link-0402/DalamudPlugins/main/repo.json`

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

1. Type /ie to open the plugin interface ingame. The on-screen list loads on its own; the refresh button in the toolbar reloads it.
2. Start Blender. Verify the toolbar shows Blender as "Online".
3. Verify the model options both in-game (the sliders button in the toolbar) and inside the add-on, then use the pencil action (or right-click, "Edit model in Blender") on the model you want to import. Hover a model for a summary and thumbnail before importing it.
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

1. Type /ie to open the plugin interface ingame.
2. Choose the **Textures** chip in **On Screen** or **Mod Browser**, then use the brush action
   (or right-click, "Edit texture") on a texture. Hovering a texture shows a preview first.
   For vanilla textures, enable **Include vanilla** and enter a new mod name.
3. Edit the opened TGA, and save that same file as **32-bit TGA with an 8-bit
   alpha channel**. Usually, simply hitting the save shortcut (for example Ctrl+S) is sufficient.
   Uncompressed and RLE TGA saves are supported. You can change the resolution
   (up to 8192 × 8192); compressed formats need a width and height divisible by 4.
   Layered documents need a flattened TGA copy saved over the working file.
4. After the save settles, Instant Edit converts it to the original TEX format,
   replaces the mod file with a backup, reloads the mod, and redraws the selected
   actor and the local player/owned entities. A vanilla override is created and
   enabled in the captured collection on the first changed save. To save
   uncompressed instead, turn off **Recompress saved textures** in the toolbar's
   options popover.
5. To make variants, save a copy of the TGA under another name in the same folder
   (for example `Red.tga`). Instant Edit writes it next to the original TEX, adds it as
   an option named after the file to a `<texture> variants` group in the mod, and
   selects that option in the captured collection. The group's **Original** option shows
   the edited texture; saving the main TGA switches back to it. Saving a variant again
   updates it.
6. Sessions pause when the game or plugin restarts. Opening the texture again
   (or the open action on its card in **Sessions**) resumes it.

## How to: Animation Editing

Animation editing needs the LivePose plugin. It rebakes a character animation so that
LivePose adjustments become part of the animation itself, repairs animations whose
skeleton no longer matches the character, or removes bones from an animation to hand
them back to physics.

1. Open the **Animations** tab and play the animation (an emote, idle or movement)
   on your character. Ready animations appear in the list on the left.
2. Select it. The **Source** card shows the file, the live skeleton and the source
   skeleton the animation was made for; pick one if several candidates match, or use
   **Rescan skeletons** after installing new skeleton mods.
3. Under **Current LivePose Adjustments**, tick the bones and components to bake.
   **Animated Bones** lets you untick bones the animation should stop driving.
4. Under **Bake**, choose a new mod or an in-place replacement and press
   **Rebake with LivePose**, **Repair skeleton** or **Rebake without unticked bones**.
5. Every edit is journaled for a week. **Undo last edit** reverts it and restores the
   live offsets it cleared; the **Recent edits** card lists older edits and reopens on
   its own when an interrupted edit needs attention.

## Additional notes

The plugin window has a toolbar with the tabs, Penumbra and Blender status dots, a
refresh button and an options popover; the gear in the title bar opens Settings, where
automatic refresh, notifications and model thumbnails can be turned off. The status
strip at the bottom keeps the latest message per tab and a history of recent messages.

The Blender plugin manages it's mappings to mods via the automatically created "Instant Edit [context_id]" collections. To ensure the addon works properly, do not move, rename, delete or otherwise edit these collections until you exported the model you were working on. Hiding them from the viewport removes the context temporarily as well. You can then easily remove an old context by simply removing it's collection, should you wish to continue other work in the same scene.

### Main Features
- One-click import of models through an on-screen browser or simplified mod file browser.
- Hover previews for textures (with alpha), materials (their texture slots) and models (mesh, material and attribute summary with a rendered thumbnail).
- The on-screen list follows Penumbra: it refreshes when mod settings change or characters are redrawn, and rows offer copy, open folder, Open in Penumbra and Show in Mod Browser.
- Easy export context selection. Export in-place, pick any existing mod option or easily create a new one. The plugin sets up everything for you automatically.
- Instant creation of mashups. The plugin automatically sets up all required textures, materials and paths for you.
- Seamlessly integrates into any existing Blender scene, independent of body, devkit, etc.
- Simple Importer / Exporter for general FBX and MDL files with various QoL functions and automations optimized for FFXIV workflows
- One-click import and export for textures
- Animation editing: bake LivePose adjustments, repair skeletons, exclude bones, with undo and recovery

### Material preview

The optional texture import uses the effective MTRL and TEX files resolved for the
selected model and packs the generated images into the Blender file. It is a
practical Principled BSDF approximation, not an exact reproduction of FFXIV's
shader pipeline. Character gear is composed from its colorset, index, normal,
mask, and (in compatibility mode) diffuse textures, including colorset
roughness, metalness, and emission. Transparency follows each material's own
settings: translucent materials such as sheer fabric blend, while others cut
out at the material's alpha threshold. Missing or unsupported
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
