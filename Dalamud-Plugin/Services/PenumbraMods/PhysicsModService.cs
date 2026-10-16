using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;
using static InstantEdit.Services.PenumbraMods.FrameworkThread;
using static InstantEdit.Services.PenumbraMods.ModFiles;
using static InstantEdit.Services.PenumbraMods.ModJson;
using static InstantEdit.Services.PenumbraMods.ModMetadata;
using static InstantEdit.Services.PenumbraMods.ModPaths;

namespace InstantEdit.Services.PenumbraMods;

/// <summary> The physics builder's mod after a save: where it is, the file now mapped, and anything to report. </summary>
internal sealed record PhysicsModSaved(string Directory, Guid Identifier, string RelativePath, bool Created, string CollectionName,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The physics builder's own mods, one per name you save under, whose default option maps each race's
/// body physics file. Every save writes a new file name (the game never reads a path it already loaded
/// again) and removes the mod's older ones; the mod is enabled at a high priority in the character's collection.
/// </summary>
internal sealed class PhysicsModService
{
    private readonly PenumbraIpc _ipc;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly SemaphoreSlim _exportGate;
    private readonly ModActivation _activation;
    private readonly TrySetMod _trySetMod;

    public PhysicsModService(PenumbraIpc ipc, IFramework framework, IPluginLog log, SemaphoreSlim exportGate, ModActivation activation)
    {
        _ipc = ipc;
        _framework = framework;
        _log = log;
        _exportGate = exportGate;
        _activation = activation;
        _trySetMod = ipc.TrySetMod;
    }

    internal const string PhysicsFolder = "physics";
    private const int PreviewModPriority = PreviewModWriter.PreviewModPriority;
    private static readonly Regex PhysicsFileVersion = new(@"\.(?<n>\d{1,9})\.phyb$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Writes <paramref name="bytes"/> as the file for <paramref name="gamePath"/> into the physics mod
    /// at <paramref name="directory"/> when it is still the mod with <paramref name="identifier"/>, or
    /// into a new mod named <paramref name="name"/> otherwise (refused when another mod has that name). Then reloads or adds the mod and enables
    /// it at <see cref="PreviewModPriority"/> in the collection of the object at <paramref name="objectIndex"/>.
    /// Doesn't redraw.
    /// </summary>
    internal async Task<PhysicsModSaved> SavePhysicsModAsync(string? directory, Guid identifier, string name, string description,
        string gamePath, byte[] bytes, int objectIndex, CancellationToken token)
    {
        gamePath = NormalizeGamePath(gamePath);
        if (!IsSafeGameResourcePath(gamePath, ".phyb") || bytes.Length == 0)
            throw new InvalidDataException("The physics file to save is invalid.");
        var collection = await _ipc.GetCollectionTargetAsync(objectIndex).ConfigureAwait(false)
            ?? throw new IOException("Your character's Penumbra collection is unavailable. Try again once your character is loaded.");

        string? root;
        string modDirectory;
        string relative;
        bool created;
        await _exportGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            (root, var listed) = await _framework.RunOnFrameworkThread(() => (_ipc.GetModDirectory(), _ipc.TryGetModList(out var mods) ? mods : null)).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                throw new IOException("Penumbra's mod directory is unavailable.");
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Penumbra's mod directory is an unsupported linked folder.");
            await LeaveFrameworkThread();

            // The mod renamed in Penumbra keeps its identifier, so saves follow it to its new folder.
            if (FindPhysicsModFolder(root, directory, identifier, listed?.Keys) is { } existing)
            {
                modDirectory = existing;
                relative = UpdatePhysicsModFiles(Path.Combine(root, existing), gamePath, bytes);
                created = false;
            }
            else
            {
                var modList = listed ?? throw new IOException("Could not retrieve the Penumbra mod list.");
                if (!IsSafeNewModName(name))
                    throw new InvalidDataException($"\"{name}\" can't be a mod name.");
                // The name you typed is the mod's: one another mod has is refused rather than numbered.
                if (modList.Keys.Any(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) ||
                    modList.Values.Any(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase)) ||
                    Directory.Exists(Path.Combine(root, name)) || File.Exists(Path.Combine(root, name)))
                    throw new IOException($"Another mod is already called {name}. Pick another name.");
                modDirectory = name;
                var target = Path.GetFullPath(Path.Combine(root, modDirectory));
                if (!IsPathWithin(target, root))
                    throw new IOException("The new mod's folder is outside Penumbra's mod directory.");
                var staging = Path.Combine(root, $".instant-edit-physics-{Guid.NewGuid():N}.tmp");
                try
                {
                    Directory.CreateDirectory(staging);
                    relative = CreatePhysicsModFiles(staging, modDirectory, description, gamePath, bytes);
                    token.ThrowIfCancellationRequested();
                    Directory.Move(staging, target);
                }
                finally
                {
                    if (Directory.Exists(staging))
                        TryDeleteMashupNamespace(root, staging);
                }
                created = true;
            }
        }
        finally
        {
            _exportGate.Release();
        }

        // Read here on the thread pool: an await on the framework thread would continue there.
        var newIdentifier = ReadModStableIdentifier(Path.Combine(root, modDirectory)) ?? Guid.Empty;
        var warnings = new List<string>();
        if (created)
        {
            if (await _activation.AddNewModAsync(modDirectory).ConfigureAwait(false) is { } added)
                warnings.Add(added.Message);
        }
        else if (await _framework.RunOnFrameworkThread(() => _activation.ReloadModOnFramework(modDirectory)).ConfigureAwait(false) is { } reload)
            warnings.Add(reload.Message);
        var configure = await _framework.RunOnFrameworkThread(() => _activation.ConfigureModForCollectionOnFramework(
            modDirectory, collection.Id, collection.Name, setPriority: true, priority: PreviewModPriority, redraw: false)).ConfigureAwait(false);
        if (!configure.Success)
            warnings.Add(configure.Message);
        warnings.AddRange(configure.WarningList);
        return new PhysicsModSaved(modDirectory, newIdentifier, relative, created, collection.Name, warnings);
    }

    /// <summary> Writes a new physics mod's file and metadata into an empty folder; returns the file's relative path. </summary>
    internal static string CreatePhysicsModFiles(string folder, string name, string description, string gamePath, byte[] bytes)
    {
        var relative = PhysicsRelativePath(gamePath, 1);
        WritePhysicsFile(folder, relative, bytes);
        WriteJsonAtomic(Path.Combine(folder, "meta.json"), CreateV4ModMetadata(name, "XIV Instant Edit", description, "",
            new JsonObject
            {
                ["Files"] = new JsonObject { [gamePath] = relative.Replace('/', '\\') },
                ["FileSwaps"] = new JsonObject(),
                ["Manipulations"] = new JsonArray(),
            }));
        return relative;
    }

    /// <summary>
    /// Maps <paramref name="gamePath"/> to a new file in the mod's default option, numbered after the
    /// mod's files for it, and deletes the older physics files no entry maps any more.
    /// </summary>
    internal static string UpdatePhysicsModFiles(string folder, string gamePath, byte[] bytes)
    {
        var meta = LoadV4ModMetadata(folder);
        if (meta["DefaultData"] is not JsonObject defaults)
            meta["DefaultData"] = defaults = new JsonObject { ["FileSwaps"] = new JsonObject(), ["Manipulations"] = new JsonArray() };
        if (defaults["Files"] is not JsonObject files)
            defaults["Files"] = files = new JsonObject();

        var physicsFolder = Path.Combine(folder, PhysicsFolder);
        var stem = Path.GetFileNameWithoutExtension(gamePath);
        var used = Directory.Exists(physicsFolder)
            ? Directory.EnumerateFiles(physicsFolder, stem + ".*.phyb").Select(file => PhysicsFileVersion.Match(Path.GetFileName(file)))
                .Where(match => match.Success).Select(match => int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max()
            : 0;
        var relative = PhysicsRelativePath(gamePath, used + 1);
        WritePhysicsFile(folder, relative, bytes);

        var previous = files.Where(pair => SameGamePath(pair.Key, gamePath)).Select(pair => (pair.Key, Value: JsonString(pair.Value))).ToList();
        foreach (var (key, _) in previous)
            files.Remove(key);
        files[gamePath] = relative.Replace('/', '\\');
        TouchV4ModMetadata(meta);
        WriteJsonAtomic(Path.Combine(folder, "meta.json"), meta);

        // Older versions of this file that nothing maps any more.
        var mapped = MappedRelativePaths(meta);
        foreach (var (_, value) in previous)
        {
            if (value is null)
                continue;
            var old = value.Replace('\\', '/').TrimStart('/');
            if (mapped.Contains(old) || !old.StartsWith(PhysicsFolder + "/", StringComparison.OrdinalIgnoreCase) ||
                !old.EndsWith(".phyb", StringComparison.OrdinalIgnoreCase))
                continue;
            var file = Path.GetFullPath(Path.Combine(folder, old.Replace('/', Path.DirectorySeparatorChar)));
            try
            {
                if (IsPathWithin(file, folder) && File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                    File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The game may still read it; the next save tries again.
            }
        }
        return relative;
    }

    /// <summary> The physics file the mod's default option maps for a game path, or null. </summary>
    internal static string? PhysicsModFile(string folder, string gamePath)
    {
        var meta = LoadV4ModMetadata(folder);
        if (meta["DefaultData"] is not JsonObject defaults || defaults["Files"] is not JsonObject files)
            return null;
        var value = files.Where(pair => SameGamePath(pair.Key, gamePath)).Select(pair => JsonString(pair.Value)).FirstOrDefault();
        if (value is null)
            return null;
        var file = Path.GetFullPath(Path.Combine(folder, value.Replace('\\', '/').TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        return IsPathWithin(file, folder) && File.Exists(file) ? file : null;
    }

    private static HashSet<string> MappedRelativePaths(JsonObject meta)
    {
        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(JsonNode? node)
        {
            if (node is JsonObject entry && entry["Files"] is JsonObject map)
                foreach (var (_, value) in map)
                    if (JsonString(value) is { } text)
                        mapped.Add(text.Replace('\\', '/').TrimStart('/'));
        }
        Visit(meta["DefaultData"]);
        if (meta["Groups"] is JsonArray groups)
            foreach (var group in groups.OfType<JsonObject>())
                foreach (var key in new[] { "Options", "Containers" })
                    if (group[key] is JsonArray list)
                        foreach (var entry in list)
                            Visit(entry);
        return mapped;
    }

    /// <summary> "physics/phy_c0801b0001.3.phyb": the file's name with a version number. </summary>
    internal static string PhysicsRelativePath(string gamePath, int version)
        => $"{PhysicsFolder}/{Path.GetFileNameWithoutExtension(gamePath)}.{version.ToString(CultureInfo.InvariantCulture)}.phyb";

    private static void WritePhysicsFile(string folder, string relative, byte[] bytes)
    {
        var target = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
        var parent = Path.GetDirectoryName(target) ?? throw new InvalidDataException("The physics file has no folder.");
        if (!IsPathWithin(target, folder) || HasReparsePointInPath(folder, parent) ||
            File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The physics file would leave the mod's folder.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".instant-edit-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    /// <summary> The physics mod's folder now: <paramref name="directory"/>, the folder it was renamed to, or null when it's gone. </summary>
    internal async Task<string?> FindPhysicsModAsync(string directory, Guid identifier)
    {
        var root = await _framework.RunOnFrameworkThread(_ipc.GetModDirectory).ConfigureAwait(false);
        await LeaveFrameworkThread();
        if (string.IsNullOrWhiteSpace(root))
            return null;
        if (FindPhysicsModFolder(root, directory, identifier, null) is { } same)
            return same;
        var mods = await _framework.RunOnFrameworkThread(() => _ipc.TryGetModList(out var list) ? list : null).ConfigureAwait(false);
        await LeaveFrameworkThread();
        return FindPhysicsModFolder(root, directory, identifier, mods?.Keys);
    }

    /// <summary>
    /// The folder in <paramref name="root"/> of the mod with <paramref name="identifier"/>: <paramref name="directory"/>
    /// while it still is that mod, else the one of <paramref name="folders"/> that is (Penumbra keeps a mod's
    /// identifier when it renames its folder). Null when none is, or more than one.
    /// </summary>
    internal static string? FindPhysicsModFolder(string root, string? directory, Guid identifier, IEnumerable<string>? folders)
    {
        if (identifier == Guid.Empty)
            return null;
        bool Is(string name) => IsSafeModName(name) && Path.Combine(root, name) is var folder && Directory.Exists(folder) &&
                                ReadModStableIdentifier(folder) == identifier;
        if (directory != null && Is(directory))
            return directory;
        var found = (folders ?? []).Where(name => !string.Equals(name, directory, StringComparison.OrdinalIgnoreCase) && Is(name)).Take(2).ToList();
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>
    /// Turns the physics mods that map <paramref name="gamePath"/> off in the object's collection;
    /// returns the ones that were on. Mods that are gone or already off are left as they are.
    /// </summary>
    internal async Task<IReadOnlyList<string>> TurnOffPhysicsModsAsync(IReadOnlyList<string> directories, int objectIndex, string gamePath)
    {
        if (directories.Count == 0)
            return [];
        // Only the mods that replace the same file: another race's physics mod stays on.
        var root = await _framework.RunOnFrameworkThread(() => _ipc.GetModDirectory()).ConfigureAwait(false);
        await LeaveFrameworkThread();
        if (!string.IsNullOrWhiteSpace(root))
            directories = directories.Where(directory => !IsSafeModName(directory) || ModMapsGamePath(Path.Combine(root, directory), gamePath)).ToList();
        if (directories.Count == 0)
            return [];
        var collection = await _ipc.GetCollectionTargetAsync(objectIndex).ConfigureAwait(false)
            ?? throw new IOException("Your character's Penumbra collection is unavailable.");
        return await _framework.RunOnFrameworkThread(() =>
        {
            var off = new List<string>();
            foreach (var directory in directories.Where(IsSafeModName))
            {
                var result = _trySetMod.Invoke(collection.Id, directory, false, directory);
                if (result == PenumbraApiEc.Success)
                    off.Add(directory);
                else if (result is not (PenumbraApiEc.NothingChanged or PenumbraApiEc.ModMissing))
                    _log.Warning($"Penumbra could not turn {directory} off ({result}).");
            }
            return (IReadOnlyList<string>)off;
        }).ConfigureAwait(false);
    }

    /// <summary> The mod folder and display name a file lies in, when it is inside Penumbra's mod directory. </summary>
    internal async Task<(string Directory, string Name)?> ModOfFileAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return null;
        var (root, mods) = await _framework.RunOnFrameworkThread(() => (_ipc.GetModDirectory(), _ipc.TryGetModList(out var list) ? list : null)).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(root) || !IsPathWithin(Path.GetFullPath(path), root))
            return null;
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        var directory = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return (directory, mods?.GetValueOrDefault(directory) is { Length: > 0 } name ? name : directory);
    }
}
