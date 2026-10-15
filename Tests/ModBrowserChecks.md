Uninstall the Mod Optimizer dev plugin first.

# Mod Browser: checks, clean-up and library filters

In-game checks for the Check view, the clean-up tools and the library filters (from Mod Optimizer's
acceptance list, rewritten for the Mod Browser). Use copies of real mods where a step changes files.

## Library filters

1. Open the Mod Browser. In "Show", pick "Not enabled anywhere": a "Checking N mods" line with Cancel
   shows, then only mods that no collection enables (inheritance counted) are listed, with an Unused
   badge. Enable one in Penumbra: it leaves the list within a moment.
2. A mod only another plugin turned on temporarily still counts as unused.
3. "Show: Duplicates" with two installs of one mod (import the same .pmp twice): both tiles show a
   Duplicate badge whose tooltip names the other folder, next to each other in the list. A copy with
   one file changed is not listed.
4. "Search in": Item with "e6016" or an item name, Emote with an emote name, Face with "au ra 101",
   Hair with a race and hair number, Standing idle / Groundsit / Chair sit with a race or pose number.
   Each lists only mods that change that thing, and the tile tooltip's "Changes:" list marks what
   matched with "> ". Face lists no hair, tail, ear or face paint mods; Hair lists hairstyles.
5. Cancel during a scan: the filters go back to "Name" and "All mods" (and the sort to "Name" from "Size").
6. Right-click a tile: Open, Open the mod's folder (Explorer opens it) and Select in Penumbra
   (Penumbra's mod tab opens with the mod selected).
6a. "Search in" Name, Author and Tag each search only that field. Folder searches where Penumbra
    files the mod ("Gear/Shoes"); the tile tooltip's "In Penumbra:" line shows it.
6b. "Show: Active" lists the mods enabled in your character's collection (the combo's tooltip names
    it), and those tiles carry an Active badge in every view. "Disabled" lists all the others.
    Toggle a mod in Penumbra: its badge and the lists follow within a moment. Assign your character
    another collection: both follow within a couple of seconds.
6c. Sort by "Size": the largest mods come first, and each tile's detail line starts with its size.
    Sort by "Date added": the newest imports come first, with "Added <date>" on each tile, matching
    Penumbra's import dates.
6d. Disk cache: after a Size sort or a changes search, the cache folder holds library-scans.json.
    Reload the plugin and repeat: the "Checking N mods" line passes in moments, since only new or
    changed mods are read. Ctrl+click the refresh button: every mod is read again (the slow pass).
6e. When Penumbra gives no answer for your collection, "Show: Active" says "Penumbra did not say which
    mods are enabled in <collection>." and lists nothing, not the previous collection's mods.
    (Checked 2026-10-14 through XIV Bridge, with the settings read made to return nothing: the hint
    named Lucina. 92f245e.)

## Check and repair

7. Open a gear mod that is invisible on some item variants. Check: the Items tab names the broken
   items. Apply the recommended repairs: every variant shows in game after a redraw.
8. The Races tab highlights your character's race.
9. Tick "Use my collection" and check a mod that needs a body mod's skin: the dependency names the
   body mod that provides it. Disable that body mod and check again: a warning shows.
10. Check a body or texture-only mod: Leftovers offers nothing to remove.

## Clean up

11. Textures: resize and compress a mod's textures. The textures reload in game without a redraw
    problem, and the sizes in the list drop.
12. File locations: hold one of the mod's old files open in another program (open it in an image
    viewer that locks it), then tidy the locations. The mappings move and reload; the held file is
    listed as pending clean-up. Close the other program: the file goes the next time clean-up runs
    or the plugin starts.
13. Duplicate files: a mod with two equal files keeps one and maps both to it. Files used by a ui/
    path are never offered.
14. Backups: Restore the last clean-up. The old files and mappings come back and Penumbra shows the
    mod as it was. Backups older than 7 days are gone after a restart.
