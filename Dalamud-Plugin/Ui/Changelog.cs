namespace InstantEdit.Ui;

internal enum ChangelogEntryKind
{
    Normal,
    Highlight,
    Important,
}

internal sealed record ChangelogEntry(
    string Text,
    ChangelogEntryKind Kind = ChangelogEntryKind.Normal,
    int Indent = 0);

internal sealed record ChangelogRelease(
    string Version,
    IReadOnlyList<ChangelogEntry> Entries);

internal static class ChangelogCatalog
{
    public const string CurrentVersion = "2.1.0";

    public static IReadOnlyList<ChangelogRelease> Releases { get; } =
    [
        Release("2.1.0",
            Highlight("Quick Actions: Character weight totals what your character costs other players in texture memory (VRAM) and triangles, counted the way Lightless, PlayerSync and other Mare-based sync plugins count them, next to their default warning and auto-pause limits."),
            Entry("It ranks every texture and model your character renders by what it costs, counting each file once, and flags uncompressed textures, textures over 4096 pixels and textures without mipmaps.", 1),
            Entry("Tick textures to compress them (BC7, or BC5 for index maps, the same rule as Mod Optimizer) or halve their size; uncompressed ones come ticked. The planned totals update as you choose.", 1),
            Entry("The smaller textures go into a preview mod first. Apply them to your mods (backups are kept for a week) or discard them; changed game files go into a new mod. A file several options share changes for all of them.", 1),
            Highlight("Quick Actions: Fix neck seam is now Fix skin seams. Besides the neck, it checks where the top meets the gloves at the wrists and the legs at the waist, and where the legs meet the shoes at the ankles."),
            Entry("It compares only the skin the game draws, with the shape keys body mods use to join their parts, and finds gaps between the edges, vertex normals that differ, different skin materials, and textures that don't continue across the seam.", 1),
            Entry("Gaps of up to 5 mm can be closed, normals matched, skin settings brought together and textures blended across the seam, with a slider for which part changes. Parts made for different bodies, whose edges lie further apart, are named without a fix.", 1),
            Entry("All fixes go into one preview mod first, as for the neck. Applying a changed model changes it for every character and outfit that uses it.", 1),
            Highlight("Quick Actions: Paint my skin opens every part of your character that shows skin in Substance Painter as one project. The body, hands, legs and feet share the body skin's textures, so tattoos, freckles and body paint can cross the wrists, waist and ankles."),
            Entry("Tick the face in the dialog to paint across the neck too. It joins with its own texture set.", 1),
            Entry("Body parts made for another race are always shaped for yours here, so they meet your face at the neck.", 1),
            Entry("Skin your gear or customization turns off stays out of Painter and keeps what the texture has there.", 1),
            Entry("Fixed the neck check reading vanilla body textures at their edge instead of at the neck, since vanilla body UVs run from 1 to 2. It reported a normal difference of about 11 degrees on vanilla bodies, and its texture blend moved the face towards the wrong values."),
            Entry("Fixed texture editing and Substance Painter refusing many older mod textures whose header lists the wrong mip offsets, such as Bibo+ skin textures. The game draws them normally, and now they can be edited, painted and sent back. Restoring one writes it with a corrected header, and the skin seam fix keeps their compression instead of saving them uncompressed."),
            Entry("Substance Painter: sending back a texture that other models or materials use too, such as the body skin, now only changes it where the project's models draw it. Before, the rest of the texture could be overwritten.")),
        Release("2.0.0",
            Highlight("Animation editing is now available in the new Animations tab (requires the LivePose plugin):"),
            Entry("The tab lists the animations your character plays with the mod each one comes from, and sorts the tools for the selected animation into tabs.", 1),
            Entry("Bake your LivePose adjustments into an animation, so the pose becomes part of the animation itself.", 1),
            Entry("Repair animations made for another skeleton by moving them onto a standard one: the game's own, IVCS or IVCS + YAS. Only the bones the animation moves are written, at the indices other players' skeletons and sync plugins expect, however many bones your own skeleton mod adds.", 1),
            Entry("Remove bones from an animation to let physics move them again, or keep only the game's, IVCS or IVCS + YAS bones with one click.", 1),
            Entry("Save the result as a new mod or replace the original. Edits from the past week can be undone, and interrupted edits can be recovered.", 1),
            Entry("The library of skeletons that animations are matched against holds each full skeleton in your mods once. It is built once and kept in the cache folder. Rebuild it in Settings after installing, updating or removing skeleton mods.", 1),
            Highlight("Send animations to Blender as an action on your skeleton, from the Animations tab:"),
            Entry("Record your character's live pose for a few seconds, including the game's bone physics and LivePose, for example to check clothing for clipping while walking. Works in GPose too, on you or your GPose target. Customize+ is paused on the character while it records, so its template can be applied in Blender instead, for example with MagicFit.", 1),
            Entry("Send any animation in the list with its pen button, sampled from its file on the skeleton it was made for.", 1),
            Entry("Send a mod's animation files from the Mod Browser's new Animations filter, choosing the clip and skeleton, without playing them.", 1),
            Entry("With Key bone scale on, bones scale in Blender the way they do in the game. If something the recorder can't pause, such as Customize+ from Brio or Mare, still scales the body while it records, the result says so.", 1),
            Highlight("Paint textures in Substance Painter (turn it on in Settings, then install the Painter plugin from there):"),
            Entry("Use the paint roller on a model in On Screen. Painter opens it with the exact textures your character uses: one texture set per material, with the current textures as the bottom layer.", 1),
            Entry("Painter shows the model as your character wears it: parts turned off by gear or mod options stay out, lashes, brows and other see-through materials are transparent, non-square textures keep their proportions, and hair and colorset gear show their colors.", 1),
            Entry("Other models that share its materials, such as the body parts sharing a skin texture, can join the same project.", 1),
            Entry("Press Send to game in Painter's XIV Instant Edit panel. Changed textures are saved with backups and their original compression, then the game reloads and redraws once. Textures you didn't change stay as they are.", 1),
            Entry("Vanilla textures from one Painter project go into a single new mod.", 1),
            Highlight("Browse the game's own models in the new Game Files tab:"),
            Entry("Every hairstyle, face, tail, Viera ear and body of every race, and all equipment, accessories and weapons, named after their items and hairstyle unlocks. Filter by race and slot, or show only the hairstyles players can pick. Player races show only the faces players can pick.", 1),
            Entry("Open a model to see its materials, textures and skeleton files, with the same previews as elsewhere. Edit models in Blender and textures in your editor; the first change to a game file goes into a new mod.", 1),
            Entry("Tick models, or every shown one, and export their files to a folder, each at its game path: the models, and as you choose their materials, textures, skeleton files and decoded skeletons, with a manifest listing what each model uses. Exports go to the cache folder unless you choose another, and automatic cache cleanup removes them there after a week.", 1),
            Highlight("The first-time setup now finds your tools and sets them up for you (run it again from Settings):"),
            Entry("Blender: installs the add-on from the GitHub extension repository with Check for Updates on Startup turned on, so Blender offers every new version. An add-on installed from a ZIP can be switched over. Settings shows each Blender's add-on version and can update it.", 1),
            Entry("Texture editors: lists the Photoshop, GIMP, Krita and Paint.NET installs it finds to pick from, and installs the Save Flattened TGA scripts into Photoshop, GIMP and Krita (Photoshop asks for administrator permission).", 1),
            Entry("Substance Painter: finds Painter wherever it is installed, Steam included, and installs the Painter plugin into the folder Painter reads plugins from.", 1),
            Entry("It also checks for Penumbra, Glamourer and LivePose, and can pick a default cache folder.", 1),
            Highlight("The plugin window has been redesigned:"),
            Entry("Tabs, Penumbra and Blender status and quick options now sit in one toolbar. Settings open from the gear icon in the title bar or with /iesettings, and group their options into Editors, Cache, Interface and Advanced tabs.", 1),
            Entry("Search the On Screen list with Ctrl+F and filter it by models, textures or materials. It now shows everything by default, color coded by type, with textures colored as base, normal, mask or index maps, and lines connecting each item to its materials and textures.", 1),
            Entry("Right-click a file to copy its path, open its folder, open its mod in Penumbra or find it in the Mod Browser. Clicking a path copies it.", 1),
            Entry("The Mod Browser shows the mod list and the selected mod side by side, and can open the mod in Penumbra.", 1),
            Entry("Messages appear in a status bar at the bottom, with a history of recent ones. Warnings, errors and Blender import and export results can also pop up as notifications.", 1),
            Highlight("Hover to preview: textures show the image (hold Shift for a larger view with transparency), materials show their textures, and models show their details, with a small 3D preview for Dawntrail models."),
            Highlight("Texture variants: save a copy of your texture under a new name, and it becomes an option in Penumbra that you can switch to."),
            Highlight("Quick Actions: a new tab for tasks that work on a whole character. The first one fixes neck seams: it compares your character's face and body where they meet, the way the game's skin shader draws them."),
            Entry("It checks the face model's neck connection data, the skin detail tile and skin settings, and the colour, masks and normal maps on both sides of the seam.", 1),
            Entry("A slider picks where the face's and body's skin settings meet: change only the body, only the face, or both part of the way.", 1),
            Entry("The fix goes into a new preview mod first. If you like the result, apply it to your mods (backups are kept for a week) or discard it.", 1),
            Highlight("Apply racial scaling for model import (in the model import options): models made for another race, like the c0201 gear most female races wear, go to Blender and Substance Painter shaped for your character's race, as the game shows them on it, so you can check them against your race's face and hair."),
            Entry("Blender gets your race's skeleton with them. Scaled models are for preview only: each scaled mesh carries an xiv_racial_scaling custom property, Quick Export and mashups refuse them, and Simple Export asks first and warns that the file keeps the scaled shape.", 1),
            Entry("Browsed mods and game files are scaled for the first character in On Screen.", 1),
            Entry("Texture editing: you can now change a texture's resolution (up to 8192 × 8192) or save it without compression. Edit sessions are shown as cards with a thumbnail and quick actions, and Clean up all sessions discards every one at once."),
            Entry("The On Screen list now refreshes on its own when you change mods in Penumbra or your appearance in Glamourer. You can turn this off in Settings."),
            Entry("On Screen starts with the Other section, which holds body connectors and similar parts, collapsed."),
            Entry("Blender add-on:"),
            Entry("Models sent to Blender get an armature with the game's skeleton: its rest pose and bone hierarchy, with your skeleton mods, instead of bones stacked at the origin. Posing, animations and Customize+ templates now move imported models the way the game does. Import Model File asks the plugin for the skeleton when the file is named like the game's. Models sent from the plugin arrive with the armature hidden, so its bones stay out of the way.", 1),
            Entry("SimpleHeels support: add a heels offset to a mesh part yourself, or let the add-on calculate it from how far the model reaches below the floor.", 1),
            Entry("Hair weighted to a hair skeleton with Magic Fit's Hair Weights gets the matching EST entry in Penumbra on Quick Export, in every option that uses the model. Exporting it without one takes back only the entry Quick Export set, and restoring a backup restores the entry too.", 1),
            Entry("The material dialog lists the materials already used by the model's other mesh groups, so you can reuse one without typing its path.", 1),
            Entry("The Add Attribute dialog asks for a category first (Body Parts, Head, Earring, Face and so on), then lists only that category's attributes.", 1),
            Entry("Create Penumbra Attribute Group now accepts the game's own attributes, such as those on vanilla hairstyles, faces and gear, instead of refusing the export. Face toggles (atr_fv_) never get an option group, since Glamourer and character creation control them. Part variants _i and _j now get options too.", 1),
            Entry("Reordering mesh parts: click a part's grip, move the pointer and click again to drop it. The list previews the new order under the pointer, and can now separate parts that share the same number.", 1),
            Entry("Material previews now show roughness, metalness and glow, and see-through materials such as sheer fabric display correctly.", 1),
            Entry("The new Pose section shows your armature in any of its actions or in its rest pose, for checking clipping or painting weights in a pose. Its trash button deletes an action from the file. Exports always use the rest pose.", 1),
            Entry("The vertex data fixes moved from the export options to Tools > Vertex Data, like TexTools' Modify Model Vertices: clear or copy UV2, clear vertex colors or alpha, and clear hair flow. They change the selected meshes once, so you can see the result, instead of every export.", 1),
            Entry("Remove Hidden Vertices deletes what no camera angle can see, such as skin under a top: from Tools for the selected meshes, or from a part name's right-click menu for that part. It lets see-through materials, faces drawn from both sides and shp_ shape keys show what they would in the game, and keeps a margin of hidden vertices at the edges so clothes moving in animations don't open gaps.", 1),
            Entry("Quick Export and switching to the rest pose can have keyboard shortcuts, set next to them or in the add-on preferences.", 1),
            Entry("Every export now checks that the meshes are triangulated and that each vertex has a bone weight, and names the meshes to fix. The Check Triangulation, Create Backfaces and YAS Groups export options are gone.", 1),
            Entry("When another Blender holds the connection port, Blender now connects by itself once the port is free.", 1),
            Entry("File Import / Export is called Simple Export / Import again and has Import and Export tabs.", 1),
            Entry("The globe icon in the Instant Edit panel's header links to Luci_xiv's mods on XIV Mod Archive, the GitHub page, Bluesky and Ko-fi.", 1),
            Entry("Fixed the vertex data fixes: Copy UV1 to UV2 did nothing on meshes with one UV map, clearing vertex color 1 also reset its alpha, and clearing hair flow set a fixed direction instead of none.", 1),
            Entry("Fixed hair flow changing when a vanilla model was imported and exported again, vertex alpha changing on export for colors imported from FBX, and exports sometimes keeping vertex groups that are no bones.", 1),
            Entry("Exporting no longer deselects the exported meshes.", 1),
            Important("Fixed new mods created from vanilla models and textures not being turned on in your collection."),
            Important("Fixed the game freezing for several seconds when refreshing the On Screen list with a large mod library."),
            Important("Fixed the plugin failing to load after a cleanup tool removed some of its files from the Windows temp folder."),
            Entry("Fixed some models failing to open in Blender with textures and materials because of unusual material values."),
            Entry("Fixed vanilla hairstyles, tails and off-hand weapons that share another model's material opening in Blender without its preview."),
            Entry("Fixed the atrx_ part toggles Quick Export makes in Penumbra for hairstyles and faces doing nothing in game. Export the model again to repair toggles made by earlier versions."),
            Entry("Hair variant tags (atr_hv_) no longer get a Penumbra option group, which Penumbra can't make for hairstyles. They could make the export fail or leave out the atrx_ toggles.")),
        Release("1.2.4",
            Entry("Fixed a model import issue that could distort unusually dense meshes."),
            Entry("Fixed vertex weights not combining correctly in some cases."),
            Entry("Added face toggle attributes to the mesh part preset list."),
            Highlight("Added fast texture save scripts for Photoshop, GIMP and Krita (see Github page).")),
        Release("1.2.3",
            Entry("Improved material previews for hair, eyebrows, and eyelashes in the Blender add-on."),
            Entry("Fixed a stale warning that could appear after creating a Mashup."),
            Entry("The Toolbox now highlights the currently selected mesh part.")),
        Release("1.2.2",
            Entry("Added glTF (.gltf/.glb) support to Simple Import in the Blender add-on."),
            Entry("Fixed the Blender add-on's sidebar tab label colliding with Yet Another Addon's."),
            Entry("Improved performance of on-screen resource source lookups for large mod lists."),
            Entry("Streamlined the On Screen resource tree to only show items Instant Edit can actually edit."),
            Entry("Fixed a mesh-renaming bug in the Toolbox where hidden objects could block valid renames."),
            Entry("Fixed a Quick Export validation edge case involving stray Penumbra mod identities."),
            Entry("Reduced managed Blender cache backup retention from 30 to 7 days."),
            Entry("Simplified Quick Export's automatic Penumbra attribute-group toggle."),
            Entry("Various internal cleanup and refactoring.")),
        Release("1.2.1",
            Entry("Improved Blender status message display and fixed a performance issue with Blender UI draw calls; hidden part tags are now suppressed."),
            Entry("Various internal cleanup and refactoring.")),
        Release("1.2.0",
            Highlight("Added texture editing feature with compatability with various photo editing software (requires 32-bit TGA support)."),
            Highlight("Added armature-combine support in the Toolbox."),
            Highlight("Adjusted Instant Edit for full Penumbra 1.7 compatability."),
            Entry("Improved export handoff, diagnostics, path handling, and validation across the plugin and Blender add-on. Export won't block anymore, but give explicit warnings such as for missing materials."),
            Entry("Added a toggle to automatically create Penumbra attribute groups from the model's enabled part attributes during Quick Export."),
            Entry("Added a Changelog viewer on the Dalamud plugin."),
            Entry("Added a one time configuration setup on the Dalamud plugin."),
            Entry("Various other bugfixes and improvements to the plugin and Blender add-on.")),
        Release("1.1.7",
            Highlight("Improved material and shader-aware model export for more faithful Blender previews."),
            Entry("Expanded backup and recovery handling around model exports and Penumbra updates."),
            Entry("Improved resource path handling and release validation across supported platforms.")),
        Release("1.1.6",
            Highlight("Added recovery support for interrupted Blender export and import operations."),
            Entry("Improved add-on startup metadata and recovery cleanup.")),
        Release("1.1.5",
            Highlight("Expanded model materials, mesh data, and export controls in Blender."),
            Entry("Improved import/export validation, diagnostics, and regression coverage."),
            Entry("Added clearer Blender-side controls for the expanded material workflow.")),
        Release("1.1.4",
            Highlight("Added drag-and-drop ordering and UI improvements for model and mesh workflows."),
            Entry("Added export metadata and clearer Mod Browser warnings for ambiguous resources."),
            Entry("Improved material previews and export-context persistence.")),
        Release("1.1.3",
            Highlight("Improved the Blender extension repository and add-on update metadata."),
            Entry("Updated release packaging and compatibility metadata.")),
        Release("1.1.2",
            Highlight("Added support for displaying mount and minion resources in the On Screen tab.")),
        Release("1.1.1",
            Highlight("Added an explicit in-place export target for overwriting the exact imported model."),
            Entry("Clarified export target selection in the Blender workflow.")),
        Release("1.1.0",
            Highlight("Renamed the project to XIV Instant Edit and expanded the export-context workflow."),
            Entry("Improved on-screen resource discovery and Blender context handling."),
            Entry("Fixed resource display issues after refresh and improved path recovery.")),
        Release("1.0.8",
            Highlight("Improved model operations, cache behavior, and Blender-side recovery.")),
        Release("1.0.7",
            Highlight("Added the first broader Blender-side editing and validation improvements."),
            Entry("Improved Blender UI controls and regression coverage for model workflows.")),
        Release("1.0.6",
            Highlight("Added variant export UI and expanded the export-context model."),
            Entry("Improved Mod Browser quick export, On Screen refresh, shape-key export, and mod-path recovery."),
            Entry("Raised the supported Blender version to 4.5.")),
        Release("1.0.5",
            Highlight("Improved cache directories, cleanup, and export-server reliability."),
            Entry("Added safer handling for stale model jobs and cache data.")),
        Release("1.0.4",
            Highlight("Added texture export and material preview support for model workflows."),
            Entry("Improved the Blender model preview and export handoff.")),
        Release("1.0.1",
            Highlight("Improved the initial Instant Edit workflow with persistent export contexts."),
            Entry("Added drag-and-drop mesh ordering and improved Blender mesh/material controls."),
            Entry("Improved Penumbra variant handling and release packaging.")),
        Release("1.0.0",
            Highlight("Initial release of the XIV Instant Edit Dalamud plugin and Blender add-on."),
            Entry("Send Penumbra model files to Blender, edit them, and export them back into the game."),
            Entry("Browse resources from the current screen and create or update model exports.")),
    ];

    public static bool ShouldAutoOpen(string? lastSeenVersion, string currentVersion)
        => !string.IsNullOrWhiteSpace(currentVersion)
            && !string.Equals(lastSeenVersion?.Trim(), currentVersion, StringComparison.OrdinalIgnoreCase);

    public static bool IsOrderedNewestFirst()
        => Releases.Zip(Releases.Skip(1), (current, next) => CompareVersions(current.Version, next.Version))
            .All(result => result > 0);

    public static bool HasUniqueVersions()
        => Releases.Select(release => release.Version)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == Releases.Count;

    private static int CompareVersions(string left, string right)
    {
        if (!Version.TryParse(left, out var leftVersion) || !Version.TryParse(right, out var rightVersion))
            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        return leftVersion.CompareTo(rightVersion);
    }

    private static ChangelogRelease Release(string version, params ChangelogEntry[] entries)
        => new(version, entries);

    private static ChangelogEntry Entry(string text, int indent = 0)
        => new(text, ChangelogEntryKind.Normal, indent);

    private static ChangelogEntry Highlight(string text, int indent = 0)
        => new(text, ChangelogEntryKind.Highlight, indent);

    private static ChangelogEntry Important(string text, int indent = 0)
        => new(text, ChangelogEntryKind.Important, indent);
}
