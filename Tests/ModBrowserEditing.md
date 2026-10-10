# Mod Browser acceptance checks

Automated coverage runs with `Tests/ExportContextRegression` (`ModHandoffScenarios`):
a mod window's files become hand-off rows whose option memberships match what
Instant Edit reads from meta.json, and each option resolves on its own. Nothing in
the windows themselves, Penumbra, Blender or the game is exercised there.

The following checks need the plugin built from this tree, Penumbra, the Blender
add-on and a texture editor. Uninstall or disable the XIV Port Studio dev plugin
first: it watches the same mod folders. Use disposable test mods. These
interactive checks have not been performed by the implementation agent.

## Library

- Open the Mod Browser tab. It shows the Build, Import and Open folder tiles, the
  recent mods once there are some, and every Penumbra mod as a tile with its
  picture, author and tags. Search, sort and the refresh button work.
- Hover a tile: its tooltip lists the items Penumbra says the mod changes.
- Import a `.pmp`: Penumbra installs it, and the mod opens in a window.
- Drop a mod folder on Open a mod folder; it opens in a window.
- Turn off mod images in Settings > Interface > Mod Browser. Tiles lose their pictures.

## Mod windows

- Click two mods. Each opens in its own window, cascaded, titled with the mod's
  name. Clicking an open mod brings its window forward, and its tile shows Open.
- Replace a texture by dropping an image on its tile. The status bar goes from
  waiting to writing to up to date, Penumbra reloads the mod and the game shows
  the change after a redraw. Undo puts the mod's own file back.
- Make a group and an option, add files to it, rename it and remove it. meta.json
  follows each step, and Penumbra's mod settings show the same groups.
- Change a file of the open mod in Penumbra's editor. The window picks it up.
- Close a window right after a change. The change is still written into the mod.
  Opening the mod again while that write runs says to try again in a moment.
- Rename the mod's folder in Penumbra: its window follows. Delete it in Penumbra:
  its window closes.
- Duplicate the mod from the window's menu: the copy opens in a window of its own.

## Instant Edit hand-offs

- In a mod whose two options carry the same model file, pick the model under one
  option and use Edit in Blender (tile pen, inspector button or right-click menu).
  Quick Export writes back into that option's file, with no ambiguity warning.
  Repeat from the other option.
- A model in a combining group's container exports back into that container.
- Edit a texture from a mod window. The TGA opens in the editor, saving updates the
  mod, and the window shows the new file. The window's status bar shows the
  hand-off's result.
- Send an animation (.pap) from a mod window while the main window is closed. The
  send dialog opens in the mod window, and the clip arrives in Blender.
- A file replaced by one of your own, or changed elsewhere, offers no hand-off and
  says why. Game files, added files, missing files and swaps offer none.
- On Screen: right-click a mod's file and choose Open mod. The mod opens in a window.

## Build window

- Click Build a new mod. The build window opens. Add gear from the game, set up
  its models and materials from your own files, and build. The mod appears in
  Penumbra, and Edit the built mod opens it in a mod window.
- Close and reopen the plugin: the modpack is still there (Continue building).
- Import a Sims 4 package from the build window, apply it to a material and build.
- Material presets and placeholder textures (Flat normal, Metal) are found in the
  installed plugin: a material made from a preset builds without a missing-file error.
