using System.Text.Json;
using System.Text.Json.Nodes;

namespace InstantEdit.Services.Painter;

/// <summary>
/// Finds Substance Painter and installs the bundled XIV Instant Edit plugin into Painter's user
/// plugin folder. The plugin's Python files ship inside this assembly, so the installed copy
/// always matches this plugin version.
/// </summary>
public static class PainterInstallation
{
    public const string PackageName = "xiv_instant_edit";
    private const string ResourcePrefix = "InstantEdit.PainterPlugin.";

    /// <summary> Painter's user plugin folder (Documents\Adobe\Adobe Substance 3D Painter\python\plugins). </summary>
    public static string PluginsDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Adobe", "Adobe Substance 3D Painter", "python", "plugins");

    public static string PackageDirectory => Path.Combine(PluginsDirectory, PackageName);

    /// <summary> The Painter executable in the usual Adobe or Steam locations, or empty. </summary>
    public static string DetectExecutable()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Adobe", "Adobe Substance 3D Painter", "Adobe Substance 3D Painter.exe"),
        };
        foreach (var steam in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steamapps", "common"),
                 })
        {
            try
            {
                if (Directory.Exists(steam))
                    candidates.AddRange(Directory.EnumerateDirectories(steam, "Substance 3D Painter*")
                        .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                        .Select(path => Path.Combine(path, "Adobe Substance 3D Painter.exe")));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // An unreadable Steam library just isn't searched.
            }
        }
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    /// <summary> The version written by the last install, or empty when the plugin isn't installed. </summary>
    public static string InstalledVersion()
    {
        try
        {
            var config = Path.Combine(PackageDirectory, "config.json");
            if (!File.Exists(config) || !File.Exists(Path.Combine(PackageDirectory, "__init__.py")))
                return "";
            return JsonNode.Parse(File.ReadAllText(config))?["version"]?.GetValue<string>() ?? "";
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary> Writes the bundled plugin and its port settings; returns the folder it installed to. </summary>
    public static string Install(int painterPort, int pluginPort, string version)
    {
        var target = PackageDirectory;
        Directory.CreateDirectory(target);
        var assembly = typeof(PainterInstallation).Assembly;
        var files = assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)).ToList();
        if (files.Count == 0)
            throw new IOException("This build doesn't include the Painter plugin.");
        foreach (var resource in files)
        {
            var name = resource[ResourcePrefix.Length..];
            if (name != Path.GetFileName(name) || !name.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                continue;
            using var stream = assembly.GetManifestResourceStream(resource) ?? throw new IOException($"Missing plugin file {name}.");
            using var file = File.Create(Path.Combine(target, name));
            stream.CopyTo(file);
        }
        File.WriteAllText(Path.Combine(target, "config.json"), JsonSerializer.Serialize(new
        {
            painterPort,
            pluginPort,
            version,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return target;
    }

    /// <summary> Updates only the ports of an installed plugin, after they change in Settings. </summary>
    public static void UpdatePorts(int painterPort, int pluginPort)
    {
        var version = InstalledVersion();
        if (version.Length == 0)
            return;
        File.WriteAllText(Path.Combine(PackageDirectory, "config.json"), JsonSerializer.Serialize(new
        {
            painterPort,
            pluginPort,
            version,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
