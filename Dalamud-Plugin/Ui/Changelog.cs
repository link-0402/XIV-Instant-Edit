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
    public const string CurrentVersion = "1.2.3";

    public static IReadOnlyList<ChangelogRelease> Releases { get; } =
    [
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
