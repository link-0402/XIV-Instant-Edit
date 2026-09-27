using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace InstantEdit.Services.Setup;

/// <summary> A Blender found on this machine, or added by hand. <see cref="Version"/> is <c>major.minor</c>. </summary>
internal sealed record BlenderInstall(string Executable, string Version, string UserDirectory, bool AddedByHand = false)
{
    private System.Version? Parsed => System.Version.TryParse(Version, out var version) ? new System.Version(version.Major, version.Minor) : null;

    public bool TooOld => Parsed is { } version && version < BlenderSetup.MinimumVersion;

    public bool TooNew => Parsed is { } version && BlenderSetup.VersionLimit is { } limit && version >= limit;

    /// <summary> Whether the add-on runs in this Blender: its manifest's version range includes it. </summary>
    public bool Supported => Parsed is not null && !TooOld && !TooNew;
}

/// <summary> An installed copy of the add-on: the extension repository folder it sits in, and its version. </summary>
internal sealed record BlenderAddonCopy(string Repository, string Version)
{
    /// <summary> Copies in Blender's local repositories were installed from a file and never update themselves. </summary>
    public bool FromFile => Repository is "user_default" or "system";
}

internal sealed record BlenderSetupResult(bool Ok, string Error, string InstalledVersion, string Warning, IReadOnlyList<string> OtherCopies);

/// <summary>
/// Finds Blender installs and sets up the add-on in them from the GitHub extension repository,
/// with Blender's update check at startup, by running <c>install_blender_addon.py</c> in a
/// windowless Blender against the user's own preferences.
/// </summary>
internal static class BlenderSetup
{
    public const string AddonPackage = "xiv_instant_edit";
    public const string RepositoryUrl = "https://raw.githubusercontent.com/link-0402/XIV-Instant-Edit/main/Blender-Addon/blender_repo/index.json";
    private const string ScriptResource = "InstantEdit.Setup.install_blender_addon.py";
    private const string ManifestResource = "InstantEdit.Setup.blender_manifest.toml";
    private static readonly Lazy<(Version Minimum, Version? Limit)> Range = new(ReadRange);

    /// <summary> The oldest Blender (major.minor) the add-on runs in: its manifest's <c>blender_version_min</c>. </summary>
    public static Version MinimumVersion => Range.Value.Minimum;

    /// <summary>
    /// The first Blender (major.minor) the add-on no longer runs in: its manifest's
    /// <c>blender_version_max</c>, which Blender treats as exclusive. Null when there is none.
    /// </summary>
    public static Version? VersionLimit => Range.Value.Limit;

    /// <summary>
    /// The add-on's version range, from the manifest this build embeds. Installs are known by
    /// major.minor only, so a bound with a patch number counts from its major.minor.
    /// </summary>
    private static (Version Minimum, Version? Limit) ReadRange()
    {
        using var stream = typeof(BlenderSetup).Assembly.GetManifestResourceStream(ManifestResource)
                           ?? throw new InvalidOperationException("This build doesn't include the add-on's manifest.");
        var manifest = new StreamReader(stream).ReadToEnd();
        return (MajorMinorVersion(ManifestValue(manifest, "blender_version_min")) ?? new Version(4, 5),
            MajorMinorVersion(ManifestValue(manifest, "blender_version_max")));

        static Version? MajorMinorVersion(string value)
            => InstalledSoftware.ParseVersion(value) is { } version ? new Version(version.Major, version.Minor) : null;
    }

    /// <summary> Why the add-on can't be installed into this Blender, or empty when it can. </summary>
    public static string UnsupportedReason(BlenderInstall install)
        => install.TooOld ? $"Too old: the add-on needs Blender {MinimumVersion} or newer."
            : install.TooNew ? $"Too new: this add-on version supports Blender below {VersionLimit}. A later add-on version will add it."
            : install.Supported ? ""
            : "Its version couldn't be read, so the add-on can't be checked against it.";

    /// <summary>
    /// Every Blender found, newest first, plus the ones the user added by hand (their blender.exe
    /// paths). Microsoft Store installs are left out: their files can't be run directly.
    /// </summary>
    public static IReadOnlyList<BlenderInstall> Detect(IEnumerable<string> addedByHand)
    {
        var candidates = new List<string>();
        candidates.AddRange(InstalledSoftware.RunningExecutables("blender"));
        foreach (var program in InstalledSoftware.Programs())
            if (program.Name.Equals("Blender", StringComparison.OrdinalIgnoreCase) ||
                program.Name.StartsWith("Blender ", StringComparison.OrdinalIgnoreCase))
            {
                if (program.InstallLocation.Length > 0)
                    candidates.Add(Path.Combine(program.InstallLocation, "blender.exe"));
                candidates.Add(InstalledSoftware.ExecutableFromIcon(program.DisplayIcon));
            }
        foreach (var programFiles in InstalledSoftware.ProgramFilesDirectories())
            candidates.AddRange(Subdirectories(Path.Combine(programFiles, "Blender Foundation"), "*")
                .Select(folder => Path.Combine(folder, "blender.exe")));
        candidates.AddRange(InstalledSoftware.SteamGameDirectories("Blender").Select(folder => Path.Combine(folder, "blender.exe")));
        candidates.Add(InstalledSoftware.FileTypeExecutable("blendfile"));

        var found = InstalledSoftware.Existing(candidates)
            .Where(path => Path.GetFileName(path).Equals("blender.exe", StringComparison.OrdinalIgnoreCase))
            .Select(path => FromExecutable(path, false).Install)
            .OfType<BlenderInstall>()
            .ToList();
        foreach (var path in addedByHand)
            if (FromExecutable(path, true).Install is { } install &&
                !found.Any(known => InstalledSoftware.SamePath(known.Executable, install.Executable)))
                found.Add(install);
        return found
            .OrderByDescending(install => System.Version.Parse(install.Version))
            .ThenBy(install => install.Executable, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The Blender a program belongs to: blender.exe itself, or the blender-launcher.exe beside
    /// it. Returns why not when it isn't a usable Blender.
    /// </summary>
    internal static (BlenderInstall? Install, string Error) FromExecutable(string path, bool addedByHand)
    {
        string executable;
        try
        {
            executable = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, "That isn't a valid path.");
        }
        if (Path.GetFileName(executable).Equals("blender-launcher.exe", StringComparison.OrdinalIgnoreCase))
            executable = Path.Combine(Path.GetDirectoryName(executable)!, "blender.exe");
        if (!Path.GetFileName(executable).Equals("blender.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            return (null, "Choose blender.exe in Blender's install folder.");
        if (executable.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            return (null, "Microsoft Store installs of Blender can't be set up from here. Add the repository in Blender instead.");
        var version = InstalledSoftware.MajorMinor(InstalledSoftware.FileProductVersion(executable));
        if (version.Length == 0)
            return (null, "That blender.exe carries no version, so the add-on can't be checked against it.");
        return (new BlenderInstall(executable, version, UserDirectoryFor(executable, version), addedByHand), "");
    }

    /// <summary> Blender's user folder: <c>portable</c> beside a portable install, else the per-version AppData folder. </summary>
    internal static string UserDirectoryFor(string executable, string version)
    {
        var portable = Path.Combine(Path.GetDirectoryName(executable) ?? "", "portable");
        return Directory.Exists(portable)
            ? portable
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Blender Foundation", "Blender", version);
    }

    /// <summary> The add-on copies in an install's extension repositories. </summary>
    public static IReadOnlyList<BlenderAddonCopy> InstalledCopies(BlenderInstall install)
        => InstalledCopies(Path.Combine(install.UserDirectory, "extensions"));

    internal static IReadOnlyList<BlenderAddonCopy> InstalledCopies(string extensionsDirectory)
    {
        var copies = new List<BlenderAddonCopy>();
        foreach (var repository in Subdirectories(extensionsDirectory, "*"))
        {
            var name = Path.GetFileName(repository);
            if (name.StartsWith('.'))
                continue;
            var manifest = Path.Combine(repository, AddonPackage, "blender_manifest.toml");
            try
            {
                if (File.Exists(manifest))
                    copies.Add(new BlenderAddonCopy(name, ManifestValue(File.ReadAllText(manifest), "version")));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        return copies;
    }

    /// <summary> A top-level <c>key = "…"</c> of a <c>blender_manifest.toml</c>, or empty. </summary>
    internal static string ManifestValue(string manifest, string key)
    {
        foreach (var line in manifest.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
                break;
            var equals = trimmed.IndexOf('=');
            if (equals > 0 && trimmed[..equals].Trim() == key)
                return trimmed[(equals + 1)..].Trim().Trim('"', '\'');
        }
        return "";
    }

    public static bool IsRunning(BlenderInstall install) => InstalledSoftware.IsRunning("blender", install.Executable);

    /// <summary>
    /// Adds the GitHub repository with its update check to the install's preferences, turns on
    /// online access, and installs and enables the add-on from it. <paramref name="replace"/>
    /// removes copies installed from elsewhere first; without it they are reported instead.
    /// </summary>
    public static async Task<BlenderSetupResult> InstallAsync(BlenderInstall install, bool replace, CancellationToken cancellationToken = default)
    {
        if (UnsupportedReason(install) is { Length: > 0 } unsupported)
            return Failed(unsupported);
        if (IsRunning(install))
            return Failed($"Close Blender {install.Version} first. It saves its preferences when it quits, which would undo the setup.");

        var work = Path.Combine(Path.GetTempPath(), "XIV Instant Edit setup " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var script = Path.Combine(work, "install_blender_addon.py");
            var resultPath = Path.Combine(work, "result.json");
            await using (var resource = typeof(BlenderSetup).Assembly.GetManifestResourceStream(ScriptResource)
                               ?? throw new IOException("This build doesn't include the Blender setup script."))
            await using (var file = File.Create(script))
                await resource.CopyToAsync(file, cancellationToken).ConfigureAwait(false);

            var start = new ProcessStartInfo(install.Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = work,
            };
            foreach (var argument in new[] { "--background", "--online-mode", "--python", script, "--", resultPath })
                start.ArgumentList.Add(argument);
            if (replace)
                start.ArgumentList.Add("--replace");

            var output = new StringBuilder();
            using var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, line) => { if (line.Data is { } data) lock (output) output.AppendLine(data); };
            process.ErrorDataReceived += (_, line) => { if (line.Data is { } data) lock (output) output.AppendLine(data); };
            try
            {
                process.Start();
            }
            catch (Win32Exception error)
            {
                return Failed($"Blender could not be started: {error.Message}");
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
                return Failed("Blender took longer than three minutes. Check your internet connection and try again.");
            }

            if (!File.Exists(resultPath))
            {
                string log;
                lock (output) log = output.ToString().Trim();
                return Failed("Blender quit without finishing the setup." + (log.Length > 0 ? " Its last output: " + LastLines(log, 3) : ""));
            }
            return ParseResult(await File.ReadAllTextAsync(resultPath, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            try { Directory.Delete(work, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static BlenderSetupResult ParseResult(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var others = root.TryGetProperty("otherCopies", out var copies) && copies.ValueKind == JsonValueKind.Array
                ? copies.EnumerateArray()
                    .Select(copy => copy.TryGetProperty("repository", out var name) ? name.GetString() ?? "" : "")
                    .Where(name => name.Length > 0)
                    .ToList()
                : [];
            return new BlenderSetupResult(
                root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                Text(root, "error"),
                Text(root, "installedVersion"),
                Text(root, "enableWarning"),
                others);
        }
        catch (JsonException)
        {
            return Failed("Blender's setup report could not be read.");
        }

        static string Text(JsonElement root, string name)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
    }

    /// <summary> Opens Blender normally, for the user. </summary>
    public static void Start(BlenderInstall install)
        => Process.Start(new ProcessStartInfo(install.Executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(install.Executable) })?.Dispose();

    private static BlenderSetupResult Failed(string error) => new(false, error, "", "", []);

    private static string LastLines(string text, int count)
        => string.Join(" ", text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).TakeLast(count));

    private static IEnumerable<string> Subdirectories(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetDirectories(folder, pattern) : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
