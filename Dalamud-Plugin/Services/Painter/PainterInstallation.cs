using System.Text.Json;
using System.Text.Json.Nodes;
using InstantEdit.Services.Setup;

namespace InstantEdit.Services.Painter;

/// <summary>
/// Finds Substance Painter and installs the bundled XIV Instant Edit plugin into Painter's user
/// plugin folder. The plugin's Python files ship inside this assembly, so the installed copy
/// always matches this plugin version.
/// </summary>
public static class PainterInstallation
{
    public const string PackageName = "xiv_instant_edit";

    /// <summary> The oldest Painter the plugin supports. </summary>
    public static readonly Version MinimumVersion = new(10, 0, 1);

    /// <summary> Whether a Painter product version ("11.0.0") is below <see cref="MinimumVersion"/>; unreadable versions pass. </summary>
    public static bool TooOld(string productVersion)
        => InstalledSoftware.ParseVersion(productVersion) is { } version && version < MinimumVersion;
    private const string ResourcePrefix = "InstantEdit.PainterPlugin.";
    private static readonly string[] ExecutableNames = ["Adobe Substance 3D Painter.exe", "Substance 3D Painter.exe"];
    private static readonly object CacheLock = new();
    private static (DateTime At, string Value)? _plugins;
    private static (DateTime At, string Value)? _executable;

    /// <summary> Painter's default user plugin folder (Documents\Adobe\Adobe Substance 3D Painter\python\plugins). </summary>
    public static string DefaultPluginsDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Adobe", "Adobe Substance 3D Painter", "python", "plugins");

    /// <summary>
    /// The user plugin folder Painter searched on its last start, from its log, or the default
    /// one when Painter hasn't run yet. Cached, since Settings asks every frame.
    /// </summary>
    public static string PluginsDirectory
    {
        get
        {
            lock (CacheLock)
            {
                if (_plugins is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(30))
                    return cached.Value;
            }
            var log = ReadLogHead();
            var plugins = log.Length > 0 ? LoggedUserPluginsDirectory(log, LoggedInstallDirectory(log)) : "";
            if (plugins.Length == 0 || !Directory.Exists(Path.GetDirectoryName(plugins)))
                plugins = DefaultPluginsDirectory;
            lock (CacheLock)
                _plugins = (DateTime.UtcNow, plugins);
            return plugins;
        }
    }

    public static string PackageDirectory => Path.Combine(PluginsDirectory, PackageName);

    /// <summary>
    /// The Painter executable: running, installed (Adobe or Steam), or named in Painter's log;
    /// empty when none is found. Searches the registry and Steam libraries, so call it off the
    /// UI thread; the result is cached briefly.
    /// </summary>
    public static string DetectExecutable()
    {
        lock (CacheLock)
        {
            if (_executable is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(10))
                return cached.Value;
        }
        var log = ReadLogHead();
        var installFolder = log.Length > 0 ? LoggedInstallDirectory(log) : "";
        var candidates = new List<string>(InstalledSoftware.RunningExecutables("Adobe Substance 3D Painter"));
        foreach (var program in InstalledSoftware.Programs())
            if (program.Name.Contains("Substance 3D Painter", StringComparison.OrdinalIgnoreCase) && program.InstallLocation.Length > 0)
                candidates.AddRange(ExecutableNames.Select(name => Path.Combine(program.InstallLocation, name)));
        if (installFolder.Length > 0)
            candidates.AddRange(ExecutableNames.Select(name => Path.Combine(installFolder, name)));
        foreach (var programFiles in InstalledSoftware.ProgramFilesDirectories())
            candidates.Add(Path.Combine(programFiles, "Adobe", "Adobe Substance 3D Painter", ExecutableNames[0]));
        foreach (var steam in InstalledSoftware.SteamGameDirectories("*Substance 3D Painter*"))
            candidates.AddRange(ExecutableNames.Select(name => Path.Combine(steam, name)));
        var executable = InstalledSoftware.Existing(candidates).FirstOrDefault() ?? "";
        lock (CacheLock)
            _executable = (DateTime.UtcNow, executable);
        return executable;
    }

    /// <summary> Forgets the cached folders, after the user starts, installs or closes Painter. </summary>
    public static void Refresh()
    {
        lock (CacheLock)
        {
            _plugins = null;
            _executable = null;
        }
    }

    /// <summary>
    /// The start of Painter's log (<c>%LOCALAPPDATA%\Adobe\Adobe Substance 3D Painter\log.txt</c>),
    /// where it records its install folder and the plugin folders it searches, or empty.
    /// </summary>
    private static string ReadLogHead()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Adobe", "Adobe Substance 3D Painter");
        foreach (var file in new[] { Path.Combine(folder, "log.txt"), Path.Combine(folder, "log.back.txt") })
        {
            try
            {
                if (!File.Exists(file))
                    continue;
                // Painter keeps its log open while it runs; the lines needed come early in it.
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var head = new byte[(int)Math.Min(stream.Length, 512 * 1024)];
                stream.ReadExactly(head);
                var text = System.Text.Encoding.UTF8.GetString(head);
                if (text.Contains("Plugins search path", StringComparison.Ordinal))
                    return text;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        return "";
    }

    /// <summary> Painter's install folder from its log's <c>Directory prefix : …/resources/pythonsdk</c> line, or empty. </summary>
    internal static string LoggedInstallDirectory(string log)
    {
        foreach (var line in log.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Directory prefix", StringComparison.Ordinal))
                continue;
            var path = trimmed[(trimmed.IndexOf(':') + 1)..].Trim().Replace('/', '\\').TrimEnd('\\');
            const string suffix = @"\resources\pythonsdk";
            return path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? path[..^suffix.Length] : "";
        }
        return "";
    }

    /// <summary>
    /// The user plugin folder from the log's <c>Plugins search path :[…]</c> line: the
    /// <c>…Substance 3D Painter\python</c> entry outside the install folder, plus <c>plugins</c>.
    /// Extra folders from SUBSTANCE_PAINTER_PLUGINS_PATH don't end that way and are skipped.
    /// </summary>
    internal static string LoggedUserPluginsDirectory(string log, string installFolder)
    {
        var line = log.Split('\n').LastOrDefault(value => value.TrimStart().StartsWith("Plugins search path", StringComparison.Ordinal));
        if (line is null)
            return "";
        var start = line.IndexOf('[');
        var end = line.LastIndexOf(']');
        if (start < 0 || end <= start)
            return "";
        foreach (var entry in line[(start + 1)..end].Split(','))
        {
            var path = entry.Trim().Trim('\'', '"').Replace('/', '\\').TrimEnd('\\');
            if (path.Length == 0 || (installFolder.Length > 0 && InstalledSoftware.IsInside(path, installFolder)))
                continue;
            if (path.EndsWith(@"Substance 3D Painter\python", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(@"Substance Painter\python", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(path, "plugins");
        }
        return "";
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

    /// <summary>
    /// Whether the installed plugin files are exactly the ones this build ships. Builds of one
    /// version can differ, and a plugin that doesn't match lays out Painter projects wrongly.
    /// </summary>
    public static bool FilesMatch()
    {
        try
        {
            var assembly = typeof(PainterInstallation).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
            {
                var installed = Path.Combine(PackageDirectory, resource[ResourcePrefix.Length..]);
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is null || !File.Exists(installed))
                    return false;
                using var shipped = new MemoryStream();
                stream.CopyTo(shipped);
                if (!shipped.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(installed)))
                    return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
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
