using System.Collections.Immutable;
using System.Text.Json;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

namespace InstantEdit.Services.Skeletons;

/// <summary>A model's game skeleton, or why there is none.</summary>
internal sealed record ModelSkeletonResult(ModelSkeleton? Skeleton, string? Problem);

/// <summary>
/// Finds the game skeleton a model is bound to, for the armature Blender builds: the live skeleton
/// of the on-screen character the model belongs to, when the character is of the model's race, else
/// the skeleton files the game would load for it: the race's body and the face, hair, headgear or top
/// skeleton its EST table picks, each resolved through the character's Penumbra collection, so
/// skeleton mods apply.
/// </summary>
internal sealed class ModelSkeletonResolver(PenumbraService penumbra, IDataManager data, IFramework framework,
    IObjectTable objects, IPluginLog log)
{
    private const int MaximumPartials = 16;
    private const int MaximumCachedSkeletons = 64;
    private readonly object sync = new();
    private readonly Dictionary<string, SkeletonDescription> descriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<EstSlot, EstTable> estTables = [];

    /// <summary>
    /// The skeleton of the model at <paramref name="modelPath"/> (a game path or file name), for the
    /// game object at <paramref name="objectIndex"/>, whose collection and EST edits apply. With the
    /// object's address, its live skeleton is used when it can be.
    /// </summary>
    public async Task<ModelSkeletonResult> ResolveAsync(string modelPath, int objectIndex, nint characterAddress,
        CancellationToken token)
    {
        var key = ModelSkeletonPaths.Parse(modelPath);
        if (key == null) return new(null, "No game skeleton is known for this kind of model.");
        var warnings = new List<string>();
        try
        {
            if (characterAddress != 0 && key.Human)
            {
                var live = await framework.RunOnFrameworkThread(() => ReadCharacter(key, objectIndex, characterAddress, warnings));
                if (live != null) return new(live, null);
                warnings.Clear();
            }
            return new(await ReadFilesAsync(key, objectIndex, warnings, token), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            log.Warning(e, $"Could not read the game skeleton of {modelPath}.");
            return new(null, $"The game skeleton could not be read: {e.Message}");
        }
    }

    /// <summary>The skeleton of a model file for the local player's collection, from the skeleton files.</summary>
    public async Task<ModelSkeletonResult> ResolveForPlayerAsync(string modelPath, CancellationToken token)
    {
        var objectIndex = await framework.RunOnFrameworkThread(() => objects.LocalPlayer is { } player ? player.ObjectIndex : -1);
        return await ResolveAsync(modelPath, objectIndex, 0, token);
    }

    /// <summary>
    /// The live skeleton of a character of the model's race: all its partial skeletons, with each
    /// Havok skeleton's reference pose (the bind pose, not the animated pose). Null when the object
    /// changed, isn't a character of that race, or has no readable skeleton.
    /// </summary>
    private unsafe ModelSkeleton? ReadCharacter(ModelSkeletonKey key, int objectIndex, nint address, List<string> warnings)
    {
        if (objectIndex is < 0 or > ushort.MaxValue || objects[objectIndex] is not ICharacter character ||
            character.Address != address || address == 0)
            return null;
        var drawObject = ((Character*)address)->GetCharacterBase();
        if (drawObject == null || drawObject->GetModelType() != CharacterBase.ModelType.Human ||
            ((Human*)drawObject)->RaceSexId != key.GenderRace)
            return null;
        var skeleton = drawObject->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletons == null || skeleton->PartialSkeletonCount is 0 or > MaximumPartials)
            return null;
        var parts = new List<SkeletonPart>();
        for (var p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var partial = &skeleton->PartialSkeletons[p];
            var pose = partial->GetHavokPose(0);
            hkaSkeleton* havok = pose != null ? pose->Skeleton : null;
            if (havok == null && partial->SkeletonResourceHandle != null)
                havok = partial->SkeletonResourceHandle->HavokSkeleton;
            var path = partial->SkeletonResourceHandle != null
                ? GamePathOf(partial->SkeletonResourceHandle->FileName.ToString())
                : $"partial skeleton {p}";
            if (havok == null)
            {
                if (p == 0) return null;
                continue;
            }
            SkeletonDescription description;
            try { description = AnimationSkeleton.Describe(havok); }
            catch (InvalidDataException e)
            {
                if (p == 0) return null;
                warnings.Add($"{Path.GetFileName(path)} was left out: {e.Message}");
                continue;
            }
            parts.Add(new SkeletonPart(path, description, p == 0 ? -1 : partial->ConnectedParentBoneIndex));
        }
        return new ModelSkeleton(ModelSkeleton.CharacterSource, key.Id, parts.Select(part => part.Path).ToImmutableArray(),
            ModelSkeletonMerge.Merge(parts, warnings), warnings.ToImmutableArray());
    }

    // Penumbra loads redirected files under their mod path; keep the game path part for display.
    private static string GamePathOf(string resource)
    {
        var normalized = resource.Replace('\\', '/');
        var index = normalized.IndexOf("chara/", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? normalized[index..] : Path.GetFileName(normalized);
    }

    private async Task<ModelSkeleton> ReadFilesAsync(ModelSkeletonKey key, int objectIndex, List<string> warnings,
        CancellationToken token)
    {
        var validObject = objectIndex is >= 0 and <= ushort.MaxValue;
        var collection = validObject ? await penumbra.GetCollectionTargetAsync(objectIndex) : null;
        var extra = ModelSkeletonPaths.Extra(key);
        var entry = extra != null ? await EstEntryAsync(extra, key.GenderRace, validObject ? objectIndex : -1, token) : 0;
        var files = ModelSkeletonPaths.Files(key, (ushort)entry);
        warnings.AddRange(files.Warnings);
        var parts = new List<SkeletonPart>();
        foreach (var path in files.Files)
        {
            token.ThrowIfCancellationRequested();
            byte[] bytes;
            SkeletonDescription description;
            try
            {
                bytes = await ReadAsync(collection?.Id, path, token);
                description = await DescribeAsync(bytes, token);
            }
            catch (Exception e) when (parts.Count > 0 && e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                warnings.Add($"{Path.GetFileName(path)} was left out: {e.Message}");
                continue;
            }
            parts.Add(new SkeletonPart(path, description, parts.Count == 0 ? -1 : SkeletonFileHeader.ConnectBone(bytes) ?? -1));
        }
        return new ModelSkeleton(ModelSkeleton.FilesSource, key.Id, parts.Select(part => part.Path).ToImmutableArray(),
            ModelSkeletonMerge.Merge(parts, warnings), warnings.ToImmutableArray());
    }

    private async Task<int> EstEntryAsync(EstRequest request, ushort genderRace, int objectIndex, CancellationToken token)
    {
        if (objectIndex >= 0 && await penumbra.MetaManipulationsAsync(objectIndex) is { } encoded)
        {
            try
            {
                if (EstTable.Override(AnimationMetadata.Decode(encoded), request.Slot, genderRace, request.Set) is { } entry)
                    return entry;
            }
            catch (Exception e) when (e is InvalidDataException or FormatException or JsonException or IOException)
            {
                log.Debug(e, "Could not read the collection's EST edits; using the game's tables.");
            }
        }
        EstTable? table;
        lock (sync) estTables.TryGetValue(request.Slot, out table);
        if (table == null)
        {
            var path = EstTable.GamePath(request.Slot);
            var bytes = await Task.Run(() => data.GetFile(path)?.Data ?? throw new FileNotFoundException($"Missing game file: {path}"), token);
            table = EstTable.Parse(bytes);
            lock (sync) estTables[request.Slot] = table;
        }
        return table[genderRace, request.Set];
    }

    private async Task<byte[]> ReadAsync(Guid? collection, string gamePath, CancellationToken token)
    {
        var resolved = gamePath;
        if (collection is { } id && id != Guid.Empty)
        {
            try { resolved = await penumbra.ResolveAnimationPathAsync(id, gamePath); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.Debug(e, $"Penumbra could not resolve {gamePath}; reading the game's file.");
            }
        }
        if (Path.IsPathRooted(resolved))
        {
            TextureFiles.EnsureLocalPath(resolved);
            var info = new FileInfo(resolved);
            if (!info.Exists) throw new FileNotFoundException($"{gamePath} is redirected to a missing file.");
            if (info.Length > AnimationPap.MaxFileSize) throw new InvalidDataException($"{gamePath} exceeds 256 MiB.");
            return await File.ReadAllBytesAsync(resolved, token);
        }
        if (!AnimationDependencies.SafeGamePath(resolved)) throw new InvalidDataException($"Invalid file swap for {gamePath}.");
        return await Task.Run(() => data.GetFile(resolved)?.Data ?? throw new FileNotFoundException($"Missing game file: {resolved}"), token);
    }

    // Havok reads skeleton files on the framework thread only; descriptions are kept by content.
    private async Task<SkeletonDescription> DescribeAsync(byte[] bytes, CancellationToken token)
    {
        var hash = AnimationPap.Hash(bytes);
        lock (sync)
            if (descriptions.TryGetValue(hash, out var cached)) return cached;
        var variants = await framework.RunOnTick(() =>
        {
            token.ThrowIfCancellationRequested();
            return AnimationSkeleton.InspectSources(bytes);
        }, delayTicks: 1, cancellationToken: token);
        var main = variants.FirstOrDefault(variant => variant.Name.Length == 0)?.Skeleton ?? variants[0].Skeleton;
        lock (sync)
        {
            if (descriptions.Count >= MaximumCachedSkeletons) descriptions.Clear();
            descriptions[hash] = main;
        }
        return main;
    }
}
