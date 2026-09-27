using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace InstantEdit.Services.Setup;

internal enum TextureEditorKind
{
    Photoshop,
    Gimp,
    Krita,
    PaintDotNet,
}

/// <summary>
/// A texture editor found on this machine. <see cref="ScriptFolder"/> is where its Save Flattened
/// TGA scripts go, or empty for an editor that saves a flattened TGA on its own.
/// <see cref="Unsupported"/> says why the scripts can't go into this version, or is empty.
/// </summary>
internal sealed record TextureEditor(TextureEditorKind Kind, string Name, string Executable, string ScriptFolder, string Unsupported = "")
{
    public bool NeedsScripts => ScriptFolder.Length > 0;
}

internal enum SaveScriptState
{
    NotNeeded,
    NotInstalled,
    Outdated,
    Installed,
}

/// <summary>
/// Finds Photoshop, GIMP, Krita and Paint.NET, and installs the Save Flattened TGA scripts that
/// ship inside this assembly (from <c>Tools/</c>) into the first three.
/// </summary>
internal static class TextureEditorSetup
{
    private const string ResourcePrefix = "InstantEdit.EditorScripts.";
    private const string KritaPlugin = "save_flattened_tga";
    private static readonly Version GimpMinimum = new(2, 10, 0);
    private static readonly Version KritaMinimum = new(5, 0, 0);

    /// <summary>
    /// Why the save scripts can't be installed into this version of an editor, or empty. GIMP 2.8
    /// and older keep their scripts outside %APPDATA%\GIMP, and the Krita plugin needs Krita 5's
    /// or 6's Python. An unreadable version passes.
    /// </summary>
    internal static string UnsupportedReason(TextureEditorKind kind, string version)
    {
        var parsed = InstalledSoftware.ParseVersion(version);
        return kind switch
        {
            TextureEditorKind.Gimp when parsed is { } gimp && gimp < GimpMinimum =>
                "Too old for the save script: it needs GIMP 2.10 or newer, and older GIMPs keep scripts elsewhere.",
            TextureEditorKind.Krita when parsed is { } krita && krita < KritaMinimum =>
                "Too old for the save plugin: it needs Krita 5 or newer.",
            _ => "",
        };
    }

    /// <summary> Every editor found, grouped by kind, newest version first within a kind. </summary>
    public static IReadOnlyList<TextureEditor> Detect()
    {
        var editors = new List<TextureEditor>();
        var programs = InstalledSoftware.Programs();

        // Photoshop only reads scripts from Presets\Scripts in its own install folder.
        var photoshop = new List<string>(InstalledSoftware.RunningExecutables("Photoshop"));
        foreach (var program in programs.Where(IsPhotoshop))
            if (program.InstallLocation.Length > 0)
                photoshop.Add(Path.Combine(program.InstallLocation, "Photoshop.exe"));
        foreach (var programFiles in InstalledSoftware.ProgramFilesDirectories())
            photoshop.AddRange(Subdirectories(Path.Combine(programFiles, "Adobe"), "Adobe Photoshop*")
                .Select(folder => Path.Combine(folder, "Photoshop.exe")));
        editors.AddRange(InstalledSoftware.Existing(photoshop)
            .Select(path => new TextureEditor(TextureEditorKind.Photoshop, Path.GetFileName(Path.GetDirectoryName(path)!),
                path, Path.Combine(Path.GetDirectoryName(path)!, "Presets", "Scripts")))
            .OrderByDescending(editor => editor.Name, StringComparer.OrdinalIgnoreCase));

        // GIMP reads scripts from the per-version user folder, %APPDATA%\GIMP\<major.minor>\scripts.
        var gimp = new List<(string Path, string Version)>();
        foreach (var program in programs.Where(program => program.Name.StartsWith("GIMP", StringComparison.OrdinalIgnoreCase)))
        {
            var folder = program.InstallLocation.Length > 0
                ? program.InstallLocation
                : Path.GetDirectoryName(Path.GetDirectoryName(InstalledSoftware.ExecutableFromIcon(program.DisplayIcon)) ?? "") ?? "";
            if (GimpExecutable(folder) is { Length: > 0 } executable)
                gimp.Add((executable, InstalledSoftware.MajorMinor(program.Version)));
        }
        foreach (var programFiles in InstalledSoftware.ProgramFilesDirectories())
            foreach (var folder in Subdirectories(programFiles, "GIMP*"))
                if (GimpExecutable(folder) is { Length: > 0 } executable)
                    gimp.Add((executable, ""));
        foreach (var running in InstalledSoftware.RunningExecutables("gimp-3").Concat(InstalledSoftware.RunningExecutables("gimp-2.10")))
            gimp.Add((running, ""));
        foreach (var group in gimp.GroupBy(found => Path.GetFullPath(found.Path), StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(group.Key))
                continue;
            var version = group.Select(found => found.Version).FirstOrDefault(value => value.Length > 0)
                          ?? GimpVersion(group.Key);
            if (version.Length == 0)
                continue;
            editors.Add(new TextureEditor(TextureEditorKind.Gimp, "GIMP " + version, group.Key,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GIMP", version, "scripts"),
                UnsupportedReason(TextureEditorKind.Gimp, version)));
        }

        // Krita loads Python plugins from pykrita in its resource folder once they're enabled in kritarc.
        var krita = new List<string>(InstalledSoftware.RunningExecutables("krita"));
        foreach (var program in programs.Where(program => program.Name.StartsWith("Krita", StringComparison.OrdinalIgnoreCase)))
        {
            if (program.InstallLocation.Length > 0)
                krita.Add(Path.Combine(program.InstallLocation, "bin", "krita.exe"));
            krita.Add(InstalledSoftware.ExecutableFromIcon(program.DisplayIcon));
        }
        foreach (var programFiles in InstalledSoftware.ProgramFilesDirectories())
            krita.AddRange(Subdirectories(programFiles, "Krita*").Select(folder => Path.Combine(folder, "bin", "krita.exe")));
        foreach (var steam in InstalledSoftware.SteamGameDirectories("Krita"))
        {
            krita.Add(Path.Combine(steam, "bin", "krita.exe"));
            krita.Add(Path.Combine(steam, "krita", "bin", "krita.exe"));
        }
        var kritaFolder = Path.Combine(KritaResourceDirectory(), "pykrita");
        foreach (var path in InstalledSoftware.Existing(krita)
                     .Where(path => Path.GetFileName(path).Equals("krita.exe", StringComparison.OrdinalIgnoreCase)))
        {
            var version = InstalledSoftware.MajorMinor(InstalledSoftware.FileProductVersion(path));
            editors.Add(new TextureEditor(TextureEditorKind.Krita, version.Length > 0 ? "Krita " + version : "Krita", path, kritaFolder,
                UnsupportedReason(TextureEditorKind.Krita, version)));
        }

        // Paint.NET flattens when it saves a TGA, so it needs no script.
        var paintDotNet = new List<string>(InstalledSoftware.RunningExecutables("paintdotnet"));
        foreach (var program in programs.Where(program => program.Name.Equals("paint.net", StringComparison.OrdinalIgnoreCase)))
            if (program.InstallLocation.Length > 0)
                paintDotNet.Add(Path.Combine(program.InstallLocation, "paintdotnet.exe"));
        foreach (var programFiles in InstalledSoftware.ProgramFilesDirectories())
            paintDotNet.Add(Path.Combine(programFiles, "paint.net", "paintdotnet.exe"));
        editors.AddRange(InstalledSoftware.Existing(paintDotNet)
            .Where(path => !path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            .Select(path => new TextureEditor(TextureEditorKind.PaintDotNet, "Paint.NET", path, "")));

        return editors
            .GroupBy(editor => editor.Executable, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static bool IsPhotoshop(InstalledProgram program)
        => program.Name.StartsWith("Adobe Photoshop", StringComparison.OrdinalIgnoreCase) &&
           !program.Name.Contains("Elements", StringComparison.OrdinalIgnoreCase) &&
           !program.Name.Contains("Express", StringComparison.OrdinalIgnoreCase) &&
           !program.Name.Contains("Lightroom", StringComparison.OrdinalIgnoreCase);

    /// <summary> GIMP's main program in an install folder (<c>bin\gimp-3.exe</c>, <c>bin\gimp-2.10.exe</c>), or empty. </summary>
    internal static string GimpExecutable(string installFolder)
    {
        if (installFolder.Length == 0)
            return "";
        var bin = Path.Combine(installFolder, "bin");
        return Files(bin, "gimp-*.exe")
            .Where(path => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"^gimp-\d+(\.\d+)?\.exe$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? "";
    }

    private static string GimpVersion(string executable)
    {
        var version = InstalledSoftware.MajorMinor(InstalledSoftware.FileProductVersion(executable));
        return version.Length > 0 ? version : InstalledSoftware.MajorMinor(Path.GetFileNameWithoutExtension(executable) + ".0");
    }

    /// <summary> Krita's config file, where the resource folder and enabled Python plugins are kept. </summary>
    public static string KritaConfigPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kritarc");

    /// <summary> Krita's resource folder: kritarc's <c>ResourceDirectory</c>, or <c>%APPDATA%\krita</c>. </summary>
    public static string KritaResourceDirectory()
    {
        try
        {
            if (File.Exists(KritaConfigPath) &&
                KritaResourceDirectory(File.ReadAllText(KritaConfigPath)) is { Length: > 0 } configured)
                return configured;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "krita");
    }

    /// <summary> The <c>ResourceDirectory</c> of kritarc's top-level group (before the first <c>[group]</c>), or empty. </summary>
    internal static string KritaResourceDirectory(string kritarc)
    {
        foreach (var line in kritarc.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
                break;
            if (trimmed.StartsWith("ResourceDirectory=", StringComparison.Ordinal))
                return trimmed["ResourceDirectory=".Length..].Trim().Replace('/', '\\');
        }
        return "";
    }

    /// <summary> kritarc with <c>enable_save_flattened_tga=true</c> in its <c>[python]</c> group, everything else unchanged. </summary>
    internal static string EnableKritaPlugin(string kritarc)
    {
        var newline = kritarc.Contains("\r\n") ? "\r\n" : "\n";
        var key = $"enable_{KritaPlugin}";
        var lines = kritarc.Length == 0 ? new List<string>() : kritarc.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        var section = lines.FindIndex(line => line.Trim() == "[python]");
        if (section < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                lines.Add("");
            lines.Add("[python]");
            lines.Add($"{key}=true");
        }
        else
        {
            var end = lines.FindIndex(section + 1, line => line.TrimStart().StartsWith('['));
            if (end < 0)
                end = lines.Count;
            var existing = lines.FindIndex(section + 1, end - section - 1, line => line.Trim().StartsWith(key + "=", StringComparison.Ordinal));
            if (existing >= 0)
                lines[existing] = $"{key}=true";
            else
            {
                // After the group's last entry, before any blank lines separating it from the next group.
                var insert = end;
                while (insert > section + 1 && lines[insert - 1].Trim().Length == 0)
                    insert--;
                lines.Insert(insert, $"{key}=true");
            }
        }
        return string.Join(newline, lines) + newline;
    }

    internal static bool KritaPluginEnabled(string kritarc)
    {
        var inPython = false;
        foreach (var line in kritarc.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
                inPython = trimmed == "[python]";
            else if (inPython && trimmed == $"enable_{KritaPlugin}=true")
                return true;
        }
        return false;
    }

    /// <summary> The scripts an editor gets: resource name → path relative to its script folder. </summary>
    internal static IReadOnlyList<(string Resource, string RelativePath)> ScriptFiles(TextureEditorKind kind)
    {
        var prefix = ResourcePrefix + kind switch
        {
            TextureEditorKind.Photoshop => "Photoshop.",
            TextureEditorKind.Gimp => "GIMP.",
            TextureEditorKind.Krita => "Krita.",
            _ => "",
        };
        if (kind == TextureEditorKind.PaintDotNet)
            return [];
        return typeof(TextureEditorSetup).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            // Krita's package files are embedded as "Krita.<package>/<file>".
            .Select(name => (name, name[prefix.Length..].Replace('/', Path.DirectorySeparatorChar)))
            .OrderBy(file => file.Item2, StringComparer.Ordinal)
            .ToList();
    }

    public static SaveScriptState ScriptState(TextureEditor editor)
    {
        if (!editor.NeedsScripts)
            return SaveScriptState.NotNeeded;
        try
        {
            var files = ScriptFiles(editor.Kind);
            var present = 0;
            var matching = 0;
            foreach (var (resource, relative) in files)
            {
                var installed = Path.Combine(editor.ScriptFolder, relative);
                if (!File.Exists(installed))
                    continue;
                present++;
                if (Shipped(resource).AsSpan().SequenceEqual(File.ReadAllBytes(installed)))
                    matching++;
            }
            if (present == 0)
                return SaveScriptState.NotInstalled;
            if (matching < files.Count)
                return SaveScriptState.Outdated;
            if (editor.Kind == TextureEditorKind.Krita &&
                !(File.Exists(KritaConfigPath) && KritaPluginEnabled(File.ReadAllText(KritaConfigPath))))
                return SaveScriptState.Outdated;
            return SaveScriptState.Installed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return SaveScriptState.NotInstalled;
        }
    }

    /// <summary>
    /// Writes the editor's scripts and returns what to do next in the editor. Photoshop's script
    /// folder is in Program Files, so unless it is writable, Windows asks for administrator
    /// permission to copy the files there.
    /// </summary>
    public static async Task<string> InstallScriptsAsync(TextureEditor editor, CancellationToken cancellationToken = default)
    {
        if (editor.Unsupported.Length > 0)
            throw new IOException(editor.Unsupported);
        var files = ScriptFiles(editor.Kind);
        if (files.Count == 0)
            throw new IOException("This build doesn't include the save scripts for " + editor.Name + ".");

        if (editor.Kind == TextureEditorKind.Photoshop)
        {
            try
            {
                WriteFiles(editor.ScriptFolder, files);
            }
            catch (UnauthorizedAccessException)
            {
                await CopyAsAdministratorAsync(editor.ScriptFolder, files, cancellationToken).ConfigureAwait(false);
            }
            return $"Installed to {editor.ScriptFolder}. Restart Photoshop; the scripts are under File > Scripts. " +
                   "Edit > Keyboard Shortcuts can give SaveFlattenedTGA a shortcut.";
        }

        WriteFiles(editor.ScriptFolder, files);
        if (editor.Kind == TextureEditorKind.Krita)
        {
            var config = KritaConfigPath;
            var current = File.Exists(config) ? await File.ReadAllTextAsync(config, cancellationToken).ConfigureAwait(false) : "";
            if (!KritaPluginEnabled(current))
                await File.WriteAllTextAsync(config, EnableKritaPlugin(current), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return $"Installed to {editor.ScriptFolder} and enabled. Restart Krita; the commands are under Tools > Scripts. " +
                   "Settings > Configure Krita > Keyboard Shortcuts can give them shortcuts.";
        }
        return $"Installed to {editor.ScriptFolder}. Restart GIMP; the commands are under File > Export. " +
               "Edit > Keyboard Shortcuts can give them shortcuts.";
    }

    private static void WriteFiles(string folder, IReadOnlyList<(string Resource, string RelativePath)> files)
    {
        foreach (var (resource, relative) in files)
        {
            var target = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, Shipped(resource));
        }
    }

    /// <summary> Copies the files into a folder only administrators may write, through a Windows permission prompt. </summary>
    private static async Task CopyAsAdministratorAsync(string folder, IReadOnlyList<(string Resource, string RelativePath)> files, CancellationToken cancellationToken)
    {
        var staging = Path.Combine(Path.GetTempPath(), "XIV Instant Edit scripts " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            WriteFiles(staging, files);
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "robocopy.exe"),
                RobocopyArguments(staging, folder, files.Select(file => file.RelativePath)))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process? process;
            try
            {
                process = Process.Start(start);
            }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223)
            {
                throw new UnauthorizedAccessException("Windows' administrator prompt was cancelled, so nothing was copied.");
            }
            catch (Win32Exception error)
            {
                throw new IOException("The copy could not be started: " + error.Message);
            }
            if (process is null)
                throw new IOException("The copy could not be started.");
            using (process)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                // robocopy's exit codes below 8 all mean the files are in place.
                if (process.ExitCode >= 8)
                    throw new IOException($"Copying to {folder} failed (robocopy exit code {process.ExitCode}).");
            }
            foreach (var (resource, relative) in files)
                if (!File.Exists(Path.Combine(folder, relative)) ||
                    !Shipped(resource).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(folder, relative))))
                    throw new IOException($"{relative} did not arrive in {folder}.");
        }
        finally
        {
            try { Directory.Delete(staging, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// robocopy (it ships with Windows) copying the named files, which must sit directly in
    /// <paramref name="source"/>, over any existing ones. A runas start can't take an argument
    /// list, and a quoted path must not end in a backslash, which would escape its closing quote.
    /// </summary>
    internal static string RobocopyArguments(string source, string target, IEnumerable<string> fileNames)
    {
        var arguments = new StringBuilder($"\"{source.TrimEnd('\\')}\" \"{target.TrimEnd('\\')}\"");
        foreach (var name in fileNames)
            arguments.Append($" \"{name}\"");
        return arguments.Append(" /IS /IT /R:0 /W:0 /NJH /NJS /NP").ToString();
    }

    private static byte[] Shipped(string resource)
    {
        using var stream = typeof(TextureEditorSetup).Assembly.GetManifestResourceStream(resource)
                           ?? throw new IOException($"Missing script {resource}.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

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

    private static IEnumerable<string> Files(string folder, string pattern)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
