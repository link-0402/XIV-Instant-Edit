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
    public const string CurrentVersion = "1.2.0";

    public static IReadOnlyList<ChangelogRelease> Releases { get; } =
    [
        Release("1.2.0",
            Highlight("Added texture editing with in-game selection, managed working files, TGA editor handoff, and safe write-back to Penumbra mods."),
            Entry("Texture sessions now keep working copies, backups, cache ownership, and recovery information so an interrupted edit can be resumed safely.", 1),
            Highlight("Added animation capture and editing workflows for inspecting recent clips and applying supported pose changes."),
            Entry("Animation capture keeps a bounded history and exposes source paths, skeleton information, and captured offsets in the plugin UI.", 1),
            Highlight("Added armature-combine support for importing model parts into an existing Blender skeleton."),
            Entry("Improved export handoff, diagnostics, path handling, and validation across the plugin and Blender add-on.", 0),
            Important("Update both the Dalamud plugin and the XIV Instant Edit Blender add-on together when using the new texture or animation features.")),
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
            Highlight("Added support for displaying mount and minion resources in the On Screen tab."),
            Entry("Added a Ko-fi support button to the main window.")),
        Release("1.1.1",
            Highlight("Added an explicit in-place export target for overwriting the exact imported model."),
            Entry("Clarified export target selection in the Blender workflow.")),
        Release("1.1.0",
            Highlight("Renamed the project to XIV Instant Edit and expanded the export-context workflow."),
            Entry("Improved on-screen resource discovery and Blender context handling."),
            Entry("Fixed resource display issues after refresh and improved path recovery.")),
        Release("1.0.8",
            Highlight("Improved model operations, cache behavior, and Blender-side recovery."),
            Entry("Removed obsolete modification documentation and tightened release packaging.")),
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
