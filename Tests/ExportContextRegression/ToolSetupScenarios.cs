using System.Text.RegularExpressions;
using InstantEdit.Services.Painter;
using InstantEdit.Services.Setup;
using static InstantEdit.TestSupport.Assertions;

/// <summary> Setup's tool detection and installers: the parsing and file edits they rely on, and the scripts they ship. </summary>
internal static class ToolSetupScenarios
{
    public static void Run(string testRoot)
    {
        SteamLibrariesParse();
        CommandLinesAndVersions();
        VersionRangesAreEnforced(testRoot);
        PainterLogNamesItsFolders();
        BlenderCopiesAndResults(Path.Combine(testRoot, "BlenderExtensions"));
        BlenderScriptShipsWithTheAssembly();
        KritaConfigEdits();
        EditorScriptsShipWithTheAssembly();
    }

    private static void SteamLibrariesParse()
    {
        const string vdf = """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"C:\\Program Files (x86)\\Steam"
            		"label"		""
            		"apps"
            		{
            			"228980"		"418962468"
            		}
            	}
            	"1"
            	{
            		"path"		"D:\\SteamLibrary"
            	}
            }
            """;
        var libraries = InstalledSoftware.ParseSteamLibraryFolders(vdf);
        Require(libraries.SequenceEqual([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"]),
            "Steam's libraryfolders.vdf yields every library path, unescaped");
    }

    private static void CommandLinesAndVersions()
    {
        Require(InstalledSoftware.CommandExecutable("\"C:\\Program Files\\Blender Foundation\\Blender 4.5\\blender-launcher.exe\" \"%1\"")
                == @"C:\Program Files\Blender Foundation\Blender 4.5\blender-launcher.exe",
            "a quoted open command yields its program");
        Require(InstalledSoftware.CommandExecutable(@"C:\Tools\Blender\blender.exe %1") == @"C:\Tools\Blender\blender.exe",
            "an unquoted open command yields everything up to .exe");
        Require(InstalledSoftware.ExecutableFromIcon("\"C:\\Program Files\\Krita (x64)\\bin\\krita.exe\",0") == @"C:\Program Files\Krita (x64)\bin\krita.exe" &&
                InstalledSoftware.ExecutableFromIcon(@"C:\Program Files\Adobe\ps_installpkg.ico") == "",
            "DisplayIcon values yield executables, not icons");
        Require(InstalledSoftware.MajorMinor("4.5.13 LTS") == "4.5" && InstalledSoftware.MajorMinor("26.8.1.8") == "26.8" &&
                InstalledSoftware.MajorMinor("5.2") == "5.2" && InstalledSoftware.MajorMinor("Blender") == "",
            "versions reduce to major.minor");
        Require(InstalledSoftware.ParseVersion("11.0.0") == new Version(11, 0, 0) &&
                InstalledSoftware.ParseVersion("10.0") == new Version(10, 0, 0) &&
                InstalledSoftware.ParseVersion("26.8 (20250624.r.8 5e0d05d)") == new Version(26, 8, 0) &&
                InstalledSoftware.ParseVersion("5") == new Version(5, 0, 0) &&
                InstalledSoftware.ParseVersion("") is null,
            "version numbers parse with missing parts as 0");
    }

    /// <summary> Setup offers the add-on only to the Blenders its manifest supports, and only offers scripts and plugins to supported editors. </summary>
    private static void VersionRangesAreEnforced(string testRoot)
    {
        var manifest = File.ReadAllText(Path.Combine(RepositoryRoot(), "Blender-Addon", "blender_manifest.toml"));
        var minimum = InstalledSoftware.ParseVersion(BlenderSetup.ManifestValue(manifest, "blender_version_min"))!;
        var limit = InstalledSoftware.ParseVersion(BlenderSetup.ManifestValue(manifest, "blender_version_max"))!;
        Require(BlenderSetup.MinimumVersion == new Version(minimum.Major, minimum.Minor) &&
                BlenderSetup.VersionLimit == new Version(limit.Major, limit.Minor),
            "setup's Blender range is the add-on manifest's");

        static BlenderInstall Blender(string version) => new(@"C:\nowhere\blender.exe", version, "");
        var oldest = $"{BlenderSetup.MinimumVersion.Major}.{BlenderSetup.MinimumVersion.Minor}";
        var first = $"{BlenderSetup.VersionLimit!.Major}.{BlenderSetup.VersionLimit.Minor}";
        Require(Blender("4.4").TooOld && !Blender("4.4").Supported && Blender(oldest).Supported && Blender("5.2").Supported,
            "Blender below the manifest's minimum is too old, the minimum itself is supported");
        Require(Blender(first).TooNew && Blender("9.0").TooNew && !Blender(first).Supported,
            "Blender at or past the manifest's exclusive maximum is too new");
        Require(BlenderSetup.UnsupportedReason(Blender("4.4")).StartsWith("Too old") &&
                BlenderSetup.UnsupportedReason(Blender(first)).StartsWith("Too new") &&
                BlenderSetup.UnsupportedReason(Blender("5.2")) == "" &&
                BlenderSetup.UnsupportedReason(Blender("unknown")).Length > 0,
            "unsupported Blenders say why");
        var refused = BlenderSetup.InstallAsync(Blender("4.4"), replace: false).GetAwaiter().GetResult();
        Require(!refused.Ok && refused.Error.StartsWith("Too old"), "installing into an unsupported Blender is refused before Blender starts");

        var folder = Path.Combine(testRoot, "HandAddedBlender");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "blender.exe"), "not a program");
        File.WriteAllText(Path.Combine(folder, "notes.exe"), "not a program");
        Require(BlenderSetup.FromExecutable(Path.Combine(folder, "missing", "blender.exe"), true).Error.StartsWith("Choose blender.exe") &&
                BlenderSetup.FromExecutable(Path.Combine(folder, "notes.exe"), true).Error.StartsWith("Choose blender.exe") &&
                BlenderSetup.FromExecutable(Path.Combine(folder, "blender.exe"), true).Error.Contains("no version"),
            "a hand-picked program must be a blender.exe that carries its version");
        Require(!BlenderSetup.Detect([Path.Combine(folder, "blender.exe")]).Any(install => install.AddedByHand),
            "hand-added programs that aren't a usable Blender stay off the list");

        Require(PainterInstallation.TooOld("9.1.2") && PainterInstallation.TooOld("10.0.0") && PainterInstallation.TooOld("10.0") &&
                !PainterInstallation.TooOld("10.0.1") && !PainterInstallation.TooOld("11.0.0") && !PainterInstallation.TooOld(""),
            "Painter below 10.0.1 is too old; an unreadable version passes");

        Require(TextureEditorSetup.UnsupportedReason(TextureEditorKind.Gimp, "2.8").Length > 0 &&
                TextureEditorSetup.UnsupportedReason(TextureEditorKind.Gimp, "2.10") == "" &&
                TextureEditorSetup.UnsupportedReason(TextureEditorKind.Gimp, "3.0") == "" &&
                TextureEditorSetup.UnsupportedReason(TextureEditorKind.Gimp, "") == "",
            "GIMP below 2.10 gets no save script");
        Require(TextureEditorSetup.UnsupportedReason(TextureEditorKind.Krita, "4.4").Length > 0 &&
                TextureEditorSetup.UnsupportedReason(TextureEditorKind.Krita, "5.2") == "" &&
                TextureEditorSetup.UnsupportedReason(TextureEditorKind.Krita, "6.0") == "" &&
                TextureEditorSetup.UnsupportedReason(TextureEditorKind.Photoshop, "20.0") == "",
            "Krita below 5 gets no save plugin; other editors have no floor");
        var oldGimp = new TextureEditor(TextureEditorKind.Gimp, "GIMP 2.8", "gimp-2.8.exe", Path.Combine(testRoot, "OldGimp", "scripts"),
            TextureEditorSetup.UnsupportedReason(TextureEditorKind.Gimp, "2.8"));
        Reject(() => TextureEditorSetup.InstallScriptsAsync(oldGimp).GetAwaiter().GetResult(), "scripts aren't written into an unsupported editor");
        Require(!Directory.Exists(oldGimp.ScriptFolder), "the refused install leaves no folder behind");
    }

    private static void PainterLogNamesItsFolders()
    {
        const string log = """
            [INFO] <Python> Python environment:
            Directory prefix    : C:/Program Files/Adobe/Adobe Substance 3D Painter/resources/pythonsdk
            [INFO] <Python> Plugins:
            Plugins search path :['C:/Users/someone/OneDrive/Documents/Adobe/Adobe Substance 3D Painter/python', 'C:/Program Files/Adobe/Adobe Substance 3D Painter/resources/python']
            """;
        var install = PainterInstallation.LoggedInstallDirectory(log);
        Require(install == @"C:\Program Files\Adobe\Adobe Substance 3D Painter", "Painter's log names its install folder");
        Require(PainterInstallation.LoggedUserPluginsDirectory(log, install) ==
                @"C:\Users\someone\OneDrive\Documents\Adobe\Adobe Substance 3D Painter\python\plugins",
            "Painter's log names the user plugin folder it searches");

        const string extraPath = """
            Directory prefix    : D:/Steam/steamapps/common/Substance 3D Painter 2024/resources/pythonsdk
            Plugins search path :['E:/dev/painter-plugins', 'C:/Users/someone/Documents/Adobe/Adobe Substance 3D Painter/python', 'D:/Steam/steamapps/common/Substance 3D Painter 2024/resources/python']
            """;
        Require(PainterInstallation.LoggedUserPluginsDirectory(extraPath, PainterInstallation.LoggedInstallDirectory(extraPath)) ==
                @"C:\Users\someone\Documents\Adobe\Adobe Substance 3D Painter\python\plugins",
            "folders from SUBSTANCE_PAINTER_PLUGINS_PATH and the install folder are skipped");
        Require(PainterInstallation.LoggedUserPluginsDirectory("[INFO] nothing here", "") == "",
            "a log without the search path names no folder");
    }

    private static void BlenderCopiesAndResults(string extensions)
    {
        Directory.CreateDirectory(Path.Combine(extensions, "user_default", "xiv_instant_edit"));
        File.WriteAllText(Path.Combine(extensions, "user_default", "xiv_instant_edit", "blender_manifest.toml"),
            "schema_version = \"1.0.0\"\nid = \"xiv_instant_edit\"\nversion = \"1.2.4\"\n\n[permissions]\nversion = \"x\"\n");
        Directory.CreateDirectory(Path.Combine(extensions, "raw_githubusercontent_com", "xiv_instant_edit"));
        File.WriteAllText(Path.Combine(extensions, "raw_githubusercontent_com", "xiv_instant_edit", "blender_manifest.toml"),
            "id = \"xiv_instant_edit\"\r\nversion = '1.3.0'\r\n");
        Directory.CreateDirectory(Path.Combine(extensions, ".cache", "xiv_instant_edit"));
        Directory.CreateDirectory(Path.Combine(extensions, "blender_org", "other_addon"));

        var copies = BlenderSetup.InstalledCopies(extensions).OrderBy(copy => copy.Repository, StringComparer.Ordinal).ToList();
        Require(copies.Count == 2 &&
                copies[0] == new BlenderAddonCopy("raw_githubusercontent_com", "1.3.0") &&
                copies[1] == new BlenderAddonCopy("user_default", "1.2.4"),
            "the add-on's copies are found per repository with their manifest versions");
        Require(copies[1].FromFile && !copies[0].FromFile, "a copy in User Default came from a file and doesn't update itself");

        var ok = BlenderSetup.ParseResult("""{"ok": true, "installedVersion": "1.3.0", "enableWarning": "loads next start", "otherCopies": []}""");
        Require(ok is { Ok: true, InstalledVersion: "1.3.0", Warning: "loads next start", Error: "" } && ok.OtherCopies.Count == 0,
            "a successful setup report is read");
        var clash = BlenderSetup.ParseResult("""{"ok": false, "error": "Another copy", "otherCopies": [{"repository": "User Default", "linked": false}]}""");
        Require(clash is { Ok: false, Error: "Another copy" } && clash.OtherCopies.SequenceEqual(["User Default"]),
            "a report of another copy names its repository");
        Require(!BlenderSetup.ParseResult("not json").Ok, "an unreadable report is a failure");
    }

    private static void BlenderScriptShipsWithTheAssembly()
    {
        using var stream = typeof(BlenderSetup).Assembly.GetManifestResourceStream("InstantEdit.Setup.install_blender_addon.py");
        Require(stream is not null, "the Blender setup script is embedded");
        var script = new StreamReader(stream!).ReadToEnd();
        var url = Regex.Match(script, "REPOSITORY_URL = \"([^\"]+)\"");
        Require(url.Success && url.Groups[1].Value == BlenderSetup.RepositoryUrl, "the script installs from the repository Settings shows");
        Require(Regex.IsMatch(script, $"PACKAGE = \"{BlenderSetup.AddonPackage}\""), "the script installs the add-on's package");

        var readme = File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md"));
        Require(readme.Contains(BlenderSetup.RepositoryUrl, StringComparison.Ordinal), "the README documents the same repository URL");
    }

    private static void KritaConfigEdits()
    {
        Require(TextureEditorSetup.KritaResourceDirectory("ResourceDirectory=D:/Art/krita/\n[General]\nResourceDirectory=nope\n") == @"D:\Art\krita\",
            "kritarc's top-level ResourceDirectory is the resource folder");
        Require(TextureEditorSetup.KritaResourceDirectory("[General]\nResourceDirectory=D:/nope\n") == "",
            "a ResourceDirectory inside a group isn't the resource folder");

        var empty = TextureEditorSetup.EnableKritaPlugin("");
        Require(empty == "[python]\nenable_save_flattened_tga=true\n" && TextureEditorSetup.KritaPluginEnabled(empty),
            "an empty kritarc gets a python group enabling the plugin");

        const string existing = "ResourceDirectory=C:/k/\r\n\r\n[python]\r\nenable_ten_brushes=true\r\n\r\n[theme]\r\nTheme=Krita dark\r\n";
        var enabled = TextureEditorSetup.EnableKritaPlugin(existing);
        Require(enabled == "ResourceDirectory=C:/k/\r\n\r\n[python]\r\nenable_ten_brushes=true\r\nenable_save_flattened_tga=true\r\n\r\n[theme]\r\nTheme=Krita dark\r\n",
            "the plugin joins an existing python group, keeping CRLF and every other entry");
        Require(!TextureEditorSetup.KritaPluginEnabled(existing) && TextureEditorSetup.KritaPluginEnabled(enabled),
            "the enabled state is read from the python group");

        const string disabled = "[python]\nenable_save_flattened_tga=false\n[General]\nx=1\n";
        Require(TextureEditorSetup.EnableKritaPlugin(disabled) == "[python]\nenable_save_flattened_tga=true\n[General]\nx=1\n",
            "a disabled plugin is switched on in place");
        Require(!TextureEditorSetup.KritaPluginEnabled("[other]\nenable_save_flattened_tga=true\n"),
            "the key only counts inside the python group");
        Require(TextureEditorSetup.EnableKritaPlugin(enabled) == enabled, "enabling twice changes nothing");
    }

    private static void EditorScriptsShipWithTheAssembly()
    {
        var tools = Path.Combine(RepositoryRoot(), "Tools");
        var expected = new[]
        {
            (TextureEditorKind.Photoshop, Directory.GetFiles(Path.Combine(tools, "Photoshop"), "*.jsx").Select(Path.GetFileName)),
            (TextureEditorKind.Gimp, Directory.GetFiles(Path.Combine(tools, "GIMP"), "*.scm").Select(Path.GetFileName)),
            (TextureEditorKind.Krita, new[] { "save_flattened_tga.desktop" }.Concat(
                Directory.GetFiles(Path.Combine(tools, "Krita", "Plugin", "save_flattened_tga"), "*.py")
                    .Select(file => Path.Combine("save_flattened_tga", Path.GetFileName(file))))),
        };
        foreach (var (kind, files) in expected)
        {
            var shipped = TextureEditorSetup.ScriptFiles(kind).Select(file => file.RelativePath).OrderBy(path => path, StringComparer.Ordinal);
            Require(shipped.SequenceEqual(files.OrderBy(path => path, StringComparer.Ordinal)!),
                $"{kind}'s save scripts are embedded at their install paths");
        }
        Require(TextureEditorSetup.ScriptFiles(TextureEditorKind.PaintDotNet).Count == 0, "Paint.NET gets no scripts");

        var source = Path.Combine(tools, "Krita", "Plugin", "save_flattened_tga", "__init__.py");
        var resource = TextureEditorSetup.ScriptFiles(TextureEditorKind.Krita).Single(file => file.RelativePath.EndsWith("__init__.py")).Resource;
        using var stream = typeof(TextureEditorSetup).Assembly.GetManifestResourceStream(resource)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        Require(memory.ToArray().SequenceEqual(File.ReadAllBytes(source)), "an embedded script is byte-identical to its source");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dalamud-Plugin", "InstantEdit.csproj")))
            directory = directory.Parent;
        Require(directory is not null, "the repository is found from the test output");
        return directory!.FullName;
    }
}
