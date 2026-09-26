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
    public const string CurrentVersion = "1.3.0";

    public static IReadOnlyList<ChangelogRelease> Releases { get; } =
    [
        Release("1.3.0",
            Highlight("Animation editing is now available in the new Animations tab (requires the LivePose plugin):"),
            Entry("Bake your LivePose adjustments into an animation, so the pose becomes part of the animation itself.", 1),
            Entry("Repair animations that don't match your character's skeleton.", 1),
            Entry("Remove bones from an animation to let physics move them again.", 1),
            Entry("Save the result as a new mod or replace the original. Edits from the past week can be undone, and interrupted edits can be recovered.", 1),
            Entry("The library of skeletons that animations are matched against is built once and kept in the cache folder. Rebuild it in Settings after installing, updating or removing skeleton mods.", 1),
            Highlight("Send animations to Blender as an action on your skeleton, from the Animations tab:"),
            Entry("Record your character's live pose for a few seconds, including the game's bone physics, Customize+ and LivePose, for example to check clothing for clipping while walking. Works in GPose too, on you or your GPose target.", 1),
            Entry("Send any animation in the list, sampled from its file on the skeleton it was made for.", 1),
            Entry("Send a mod's animation files from the Mod Browser's new Animations filter, choosing the clip and skeleton, without playing them. On Screen's Animations filter lists what your character plays.", 1),
            Highlight("Paint textures in Substance Painter (turn it on in Settings, then install the Painter plugin from there):"),
            Entry("Use the paint roller on a model in On Screen. Painter opens it with the exact textures your character uses: one texture set per material, with the current textures as the bottom layer.", 1),
            Entry("Other models that share its materials, such as the body parts sharing a skin texture, can join the same project.", 1),
            Entry("Press Send to game in Painter's XIV Instant Edit panel. Changed textures are saved with backups and their original compression, then the game reloads and redraws once. Textures you didn't change stay as they are.", 1),
            Entry("Vanilla textures from one Painter project go into a single new mod.", 1),
            Highlight("The plugin window has been redesigned:"),
            Entry("Tabs, Penumbra and Blender status and quick options now sit in one toolbar. Settings open from the gear icon in the title bar.", 1),
            Entry("Search the On Screen list with Ctrl+F and filter it by models, textures or materials. It now shows everything by default, color coded by type, with textures colored as base, normal, mask or index maps, and lines connecting each item to its materials and textures.", 1),
            Entry("Right-click a file to copy its path, open its folder, open its mod in Penumbra or find it in the Mod Browser. Clicking a path copies it.", 1),
            Entry("The Mod Browser shows the mod list and the selected mod side by side, and can open the mod in Penumbra.", 1),
            Entry("Messages appear in a status bar at the bottom, with a history of recent ones. Warnings, errors and Blender import and export results can also pop up as notifications.", 1),
            Highlight("Hover to preview: textures show the image (hold Shift for a larger view with transparency), materials show their textures, and models show their details, with a small 3D preview for Dawntrail models."),
            Highlight("Texture variants: save a copy of your texture under a new name, and it becomes an option in Penumbra that you can switch to."),
            Entry("Texture editing: you can now change a texture's resolution (up to 8192 × 8192) or save it without compression. Edit sessions are shown as cards with a thumbnail and quick actions."),
            Entry("The On Screen list now refreshes on its own when you change mods in Penumbra or your appearance in Glamourer. You can turn this off in Settings."),
            Entry("Blender add-on:"),
            Entry("SimpleHeels support: add a heels offset to a mesh part yourself, or let the add-on calculate it from how far the model reaches below the floor.", 1),
            Entry("The material dialog lists the materials already used by the model's other mesh groups, so you can reuse one without typing its path.", 1),
            Entry("Dragging mesh parts to reorder them previews the new order and applies it when you let go, and can now separate parts that share the same number.", 1),
            Entry("Material previews now show roughness, metalness and glow, and see-through materials such as sheer fabric display correctly.", 1),
            Important("Fixed new mods created from vanilla models and textures not being turned on in your collection."),
            Important("Fixed the game freezing for several seconds when refreshing the On Screen list with a large mod library."),
            Important("Fixed the plugin failing to load after a cleanup tool removed some of its files from the Windows temp folder."),
            Entry("Fixed some models failing to open in Blender with textures and materials because of unusual material values."),
            Entry("The changelog can now show only what's new since the version you last used.")),
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
