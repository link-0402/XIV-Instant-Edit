using InstantEdit.Services;
using InstantEdit.Services.Setup;
using static InstantEdit.TestSupport.Assertions;

/// <summary> The oldest Blender add-on and Painter plugin this build works with, and how installed versions are compared to them. </summary>
internal static class ComponentVersionScenarios
{
    public static void Run()
    {
        MinimumsAreDeclared();
        MinimumsAreShipped();
        VersionsAreCompared();
        PainterVersionIsParsed();
        AddonButtonOffersUpdatesOnlyWhenNewer();
    }

    private static void AddonButtonOffersUpdatesOnlyWhenNewer()
    {
        Require(ComponentVersions.IsOlder("2.1.0", ComponentVersions.BlenderAddonLatest), "the shipped add-on is newer than 2.1.0");
        Require(BlenderAddonLabels.ButtonLabel("2.1.0") == "Update add-on", "an add-on older than the shipped one offers an update");
        Require(BlenderAddonLabels.ButtonLabel(ComponentVersions.BlenderAddonLatest) == "Reinstall add-on" &&
                BlenderAddonLabels.ButtonLabel("99.0.0") == "Reinstall add-on",
            "the shipped add-on, or a newer one, offers a reinstall");
    }

    private static void MinimumsAreDeclared()
        => Require(ComponentVersions.BlenderAddonMinimum == "3.0.0" && ComponentVersions.PainterPluginMinimum == "3.0.0",
            "both minimums are declared");

    private static void MinimumsAreShipped()
    {
        var repo = RepositoryRoot();
        Require(ComponentVersions.BlenderAddonLatest ==
                BlenderSetup.ManifestValue(File.ReadAllText(Path.Combine(repo, "Blender-Addon", "blender_manifest.toml")), "version"),
            "the embedded manifest is the source one");
        Require(!ComponentVersions.IsOlder(ComponentVersions.BlenderAddonLatest, ComponentVersions.BlenderAddonMinimum),
            "the add-on minimum is not above the shipped add-on");
        Require(!ComponentVersions.IsOlder(ComponentVersions.PainterPluginLatest, ComponentVersions.PainterPluginMinimum),
            "the Painter minimum is not above the shipped Painter plugin");
    }

    private static void VersionsAreCompared()
    {
        Require(ComponentVersions.Satisfies("2.2.0", "2.2.0") && ComponentVersions.Satisfies("2.3.1", "2.2.0") &&
                ComponentVersions.Satisfies("2.2.0.1", "2.2.0") && ComponentVersions.Satisfies("10.0.0", "2.2.0"),
            "equal, newer, four-part and numerically larger versions satisfy");
        Require(!ComponentVersions.Satisfies("2.1.9", "2.2.0") && !ComponentVersions.Satisfies(null, "2.2.0") &&
                !ComponentVersions.Satisfies("", "2.2.0") && !ComponentVersions.Satisfies("abc", "2.2.0") &&
                !ComponentVersions.Satisfies("2.2.0", ""),
            "older, missing, unparseable, or no minimum fails");
        Require(ComponentVersions.IsOlder("2.1.0", "2.2.0") && !ComponentVersions.IsOlder("2.2.0", "2.2.0") &&
                !ComponentVersions.IsOlder("2.3.0", "2.2.0") && !ComponentVersions.IsOlder("", "2.2.0"),
            "IsOlder is true only for a readable older version");
    }

    private static void PainterVersionIsParsed()
        => Require(ComponentVersions.ParsePainterPluginVersion("\"\"\"doc\"\"\"\nPLUGIN_VERSION = \"2.1.0\"\n") == "2.1.0" &&
                   ComponentVersions.ParsePainterPluginVersion("x = 1") == "",
            "PLUGIN_VERSION is parsed, absent gives empty");

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Blender-Addon", "blender_manifest.toml")))
            directory = directory.Parent;
        Require(directory is not null, "the repository is found from the test output");
        return directory!.FullName;
    }
}
