using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace InstantEdit.Services.Setup;

/// <summary> One entry of Windows' installed-apps list. </summary>
internal sealed record InstalledProgram(string Name, string Version, string InstallLocation, string DisplayIcon);

/// <summary>
/// Where the tools Instant Edit works with are installed: Windows' installed-apps list, Steam
/// libraries, file associations and running processes. Every lookup tolerates missing or
/// unreadable keys and folders, since any of them can be absent on a given machine.
/// </summary>
internal static partial class InstalledSoftware
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly object CacheLock = new();
    private static (DateTime At, IReadOnlyList<InstalledProgram> Programs)? _programs;

    /// <summary> Machine-wide (64- and 32-bit) and per-user installed apps, cached for a few seconds. </summary>
    public static IReadOnlyList<InstalledProgram> Programs()
    {
        lock (CacheLock)
        {
            if (_programs is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(10))
                return cached.Programs;
        }
        var programs = new List<InstalledProgram>();
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default),
                 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallKey);
                if (uninstall is null)
                    continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is not string displayName || displayName.Trim().Length == 0)
                        continue;
                    programs.Add(new InstalledProgram(displayName.Trim(),
                        (entry.GetValue("DisplayVersion") as string ?? "").Trim(),
                        (entry.GetValue("InstallLocation") as string ?? "").Trim().Trim('"'),
                        (entry.GetValue("DisplayIcon") as string ?? "").Trim()));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // An unreadable hive just isn't searched.
            }
        }
        lock (CacheLock)
            _programs = (DateTime.UtcNow, programs);
        return programs;
    }

    /// <summary> The executable a DisplayIcon value names (<c>"C:\x\app.exe",0</c>), or empty. </summary>
    public static string ExecutableFromIcon(string displayIcon)
    {
        var value = displayIcon.Trim();
        var comma = value.LastIndexOf(',');
        if (comma > 0 && int.TryParse(value[(comma + 1)..].Trim(), out _))
            value = value[..comma];
        value = value.Trim().Trim('"');
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value : "";
    }

    /// <summary> Program Files folders (64-bit, then 32-bit), without duplicates. </summary>
    public static IEnumerable<string> ProgramFilesDirectories()
        => new[]
            {
                Environment.GetEnvironmentVariable("ProgramW6432") ?? "",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            }
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary> <c>steamapps\common</c> of every Steam library on this machine. </summary>
    public static IReadOnlyList<string> SteamCommonDirectories()
    {
        var steamRoots = new List<string>();
        foreach (var (hive, key) in new[]
                 {
                     (RegistryHive.CurrentUser, @"Software\Valve\Steam"),
                     (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
                     (RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam"),
                 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var steam = root.OpenSubKey(key);
                foreach (var name in new[] { "SteamPath", "InstallPath" })
                    if (steam?.GetValue(name) is string path && path.Trim().Length > 0)
                        steamRoots.Add(path.Trim().Replace('/', '\\'));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }
        steamRoots.AddRange(ProgramFilesDirectories().Select(folder => Path.Combine(folder, "Steam")));

        var libraries = new List<string>();
        foreach (var root in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            libraries.Add(root);
            try
            {
                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    libraries.AddRange(ParseSteamLibraryFolders(File.ReadAllText(vdf)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        return libraries
            .Select(library => Path.Combine(library, "steamapps", "common"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .ToList();
    }

    /// <summary> The library paths listed in Steam's <c>libraryfolders.vdf</c>. </summary>
    internal static IReadOnlyList<string> ParseSteamLibraryFolders(string vdf)
        => SteamLibraryPath().Matches(vdf)
            .Select(match => match.Groups[1].Value.Replace(@"\\", @"\"))
            .Where(path => path.Length > 0)
            .ToList();

    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamLibraryPath();

    /// <summary> Folders under the Steam libraries whose name matches <paramref name="pattern"/>. </summary>
    public static IEnumerable<string> SteamGameDirectories(string pattern)
    {
        foreach (var common in SteamCommonDirectories())
        {
            string[] found;
            try { found = Directory.GetDirectories(common, pattern); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var directory in found.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                yield return directory;
        }
    }

    /// <summary> Executables of running processes named <paramref name="processName"/> that this process may inspect. </summary>
    public static IReadOnlyList<string> RunningExecutables(string processName)
    {
        var paths = new List<string>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { Length: > 0 } path)
                        paths.Add(path);
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Elevated or exited processes can't be inspected.
                }
            }
        }
        return paths;
    }

    /// <summary> Whether a process named <paramref name="processName"/> runs <paramref name="executable"/>, or can't be told apart from it. </summary>
    public static bool IsRunning(string processName, string executable)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.MainModule?.FileName is not { Length: > 0 } path || SamePath(path, executable))
                        return true;
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary> The executable the open command of a file type (a ProgID such as <c>blendfile</c>) runs, or empty. </summary>
    public static string FileTypeExecutable(string progId)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var command = root.OpenSubKey($@"Software\Classes\{progId}\shell\open\command");
                if (command?.GetValue(null) is string line && CommandExecutable(line) is { Length: > 0 } executable)
                    return executable;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }
        return "";
    }

    /// <summary> The program a command line starts: its quoted first part, or everything up to ".exe". </summary>
    internal static string CommandExecutable(string commandLine)
    {
        var line = commandLine.Trim();
        if (line.StartsWith('"'))
        {
            var end = line.IndexOf('"', 1);
            return end > 1 ? line[1..end] : "";
        }
        var exe = line.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? line[..(exe + 4)] : "";
    }

    /// <summary> <c>major.minor</c> of a version string such as "4.5.13" or "26.8.1.8"; empty when it has none. </summary>
    internal static string MajorMinor(string version)
    {
        var match = MajorMinorPattern().Match(version);
        return match.Success ? $"{int.Parse(match.Groups[1].Value)}.{int.Parse(match.Groups[2].Value)}" : "";
    }

    [GeneratedRegex(@"(\d+)\.(\d+)")]
    private static partial Regex MajorMinorPattern();

    /// <summary>
    /// The first version number in <paramref name="text"/> ("11.0.0", "2.10", "26.8 (2025…)"),
    /// with missing minor and patch parts as 0, so "10.0" compares below "10.0.1"; null when it has none.
    /// </summary>
    internal static Version? ParseVersion(string text)
    {
        var match = VersionPattern().Match(text);
        if (!match.Success)
            return null;
        return new Version(int.Parse(match.Groups[1].Value),
            match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0,
            match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0);
    }

    [GeneratedRegex(@"(\d{1,9})(?:\.(\d{1,9}))?(?:\.(\d{1,9}))?")]
    private static partial Regex VersionPattern();

    /// <summary> The product version the file's version resource carries, or empty. </summary>
    public static string FileProductVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return (info.ProductVersion ?? info.FileVersion ?? "").Trim();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "";
        }
    }

    public static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary> Whether <paramref name="path"/> is <paramref name="folder"/> or inside it. </summary>
    public static bool IsInside(string path, string folder)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\') + "\\";
            var root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary> Files that exist, in order, without duplicates. </summary>
    public static IEnumerable<string> Existing(IEnumerable<string> candidates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            string full;
            try { full = Path.GetFullPath(candidate); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (seen.Add(full) && File.Exists(full))
                yield return full;
        }
    }
}
