namespace InstantEdit.Services.Setup;

/// <summary> Wording for the Blender add-on's install button. </summary>
internal static class BlenderAddonLabels
{
    /// <summary> "Update add-on" while the installed add-on is older than the one this build knows of, else "Reinstall add-on". </summary>
    public static string ButtonLabel(string installedVersion)
        => ComponentVersions.IsOlder(installedVersion, ComponentVersions.BlenderAddonLatest) ? "Update add-on" : "Reinstall add-on";
}
