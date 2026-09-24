using System.Text.Json.Nodes;
using InstantEdit.Models;

namespace InstantEdit.Services;

/// <summary>
/// Texture variants: TGAs saved beside a session's working image under another name. Each one
/// is written next to the session's TEX and published as an option of one Single group per
/// session. The group's first option maps nothing, so selecting it shows the edited texture.
/// </summary>
public sealed partial class PenumbraService
{
    internal const string OriginalTextureOptionName = "Original";

    async Task<TextureVariantCommit> ITextureEditBackend.CommitVariantAsync(TextureEditSession session, TextureVariant variant,
        byte[] tex, Func<bool> stillCurrent, CancellationToken token)
    {
        TextureFiles.ValidateCommit(tex, session);
        if (session.NeedsMod) throw new IOException("The texture mod has not been created yet.");
        if (_backups is null) throw new IOException("Texture backup storage is unavailable.");
        await _exportGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            await ValidateTextureDestinationAsync(session.ModDirectory, session.ModRoot, session.RelativePath, session.TargetFile, token).ConfigureAwait(false);
            return CommitTextureVariantFiles(session, variant, tex, _backups, stillCurrent, token);
        }
        finally { _exportGate.Release(); }
    }

    /// <summary>
    /// Writes the variant TEX, then maps it in the session's variant group. A TEX created by this
    /// call is removed again when the mapping cannot be written, so a retry does not orphan it.
    /// </summary>
    internal static TextureVariantCommit CommitTextureVariantFiles(TextureEditSession session, TextureVariant variant,
        byte[] tex, ModelBackupStore backups, Func<bool> stillCurrent, CancellationToken token)
    {
        if (!PathRules.IsSafeVariantName(variant.Name))
            throw new IOException($"\"{variant.Name}\" cannot be used as a Penumbra option name.");
        if (string.Equals(variant.Name, OriginalTextureOptionName, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"\"{OriginalTextureOptionName}\" is reserved for the edited texture. Save the variant under another name.");
        if (TextureMappingFingerprint(session.ModRoot) != session.MappingFingerprint)
            throw new TextureConflictException("The mod's options or mappings changed. Reopen the texture from the browser.");

        var owned = variant.LastCommittedHash.Length > 0 && variant.RelativePath.Length > 0;
        var relative = owned ? variant.RelativePath : ChooseTextureVariantPath(session, variant.Name);
        var target = Path.Combine(session.ModRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        TextureFiles.EnsureLocalPath(target);
        var backup = "";
        var created = false;
        token.ThrowIfCancellationRequested();
        if (owned && File.Exists(target))
            backup = TextureFiles.Replace(target, session.ModRoot, relative, session.ModDirectory, tex,
                variant.LastCommittedHash, backups, stillCurrent, token);
        else
        {
            if (!stillCurrent()) throw new OperationCanceledException("A newer save is pending.");
            WriteBytesAtomic(session.ModRoot, relative, tex);
            created = true;
        }

        try
        {
            var (groupId, originalId, optionId) = WriteTextureVariantGroup(session, variant, relative);
            return new TextureVariantCommit(TextureFiles.Hash(tex), backup, relative, groupId, originalId, optionId,
                TextureMappingFingerprint(session.ModRoot));
        }
        catch
        {
            if (created && File.Exists(target)) File.Delete(target);
            throw;
        }
    }

    /// <summary>
    /// A path beside the session's TEX: <c>&lt;texture&gt;_&lt;variant&gt;.tex</c>, numbered when that file
    /// already exists, since it then belongs to the mod rather than to this variant.
    /// </summary>
    private static string ChooseTextureVariantPath(TextureEditSession session, string name)
    {
        var separator = session.RelativePath.LastIndexOf('/');
        var folder = separator < 0 ? "" : session.RelativePath[..(separator + 1)];
        var stem = Path.GetFileNameWithoutExtension(session.RelativePath);
        var taken = session.Variants.Select(v => v.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var attempt = 1; attempt < 1000; attempt++)
        {
            var candidate = attempt == 1 ? $"{folder}{stem}_{name}.tex" : $"{folder}{stem}_{name} ({attempt}).tex";
            if (!IsSafeGameResourcePath(candidate, ".tex"))
                throw new IOException($"\"{name}\" does not make a valid texture file name.");
            if (!taken.Contains(candidate) &&
                !File.Exists(Path.Combine(session.ModRoot, candidate.Replace('/', Path.DirectorySeparatorChar))))
                return candidate;
        }
        throw new IOException($"No free file name is left for the variant \"{name}\".");
    }

    /// <summary>
    /// Maps the session's game path to <paramref name="relative"/> in the variant's option,
    /// creating the group, its Original option and the variant option as needed. The group and
    /// options are found by identity first, so renaming them in Penumbra keeps them in use.
    /// </summary>
    private static (Guid GroupId, Guid OriginalId, Guid OptionId) WriteTextureVariantGroup(
        TextureEditSession session, TextureVariant variant, string relative)
    {
        var meta = LoadV4ModMetadata(session.ModRoot);
        if (meta["Groups"] is not JsonArray groups)
            meta["Groups"] = groups = new JsonArray();

        var group = groups.OfType<JsonObject>().FirstOrDefault(candidate =>
            session.VariantGroupId is { } id && ReadGuid(candidate["Id"]) == id &&
            string.Equals(JsonString(candidate["Type"]), "Single", StringComparison.OrdinalIgnoreCase));
        if (group is null)
        {
            var highest = groups.OfType<JsonObject>().Select(candidate => JsonInt(candidate["Priority"])).DefaultIfEmpty(0).Max();
            if (highest == int.MaxValue) throw new IOException("penumbra_group_priority_exhausted");
            var name = UniqueGroupName(groups, Path.GetFileNameWithoutExtension(session.GamePath) + " variants");
            group = new JsonObject
            {
                ["Type"] = "Single",
                ["Id"] = Guid.NewGuid().ToString("D"),
                ["Name"] = name,
                ["Description"] = VariantGroupDescriptionPrefix + name + " -> " + session.GamePath,
                ["Priority"] = highest + 1,
                // Other collections keep showing the edited texture until a variant is chosen.
                ["DefaultSettings"] = 0,
                ["Options"] = new JsonArray { new JsonObject { ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = OriginalTextureOptionName } },
            };
            groups.Add(group);
        }
        if (group["Options"] is not JsonArray options)
            group["Options"] = options = new JsonArray();

        var original = options.OfType<JsonObject>().FirstOrDefault(option =>
                           session.OriginalOptionId is { } id && ReadGuid(option["Id"]) == id)
                       ?? options.OfType<JsonObject>().FirstOrDefault(option => !MapsGamePath(option, session.GamePath));
        if (original is null)
        {
            // Appended, not inserted: Single-group settings are option indices.
            original = new JsonObject { ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = OriginalTextureOptionName };
            options.Add(original);
        }

        var target = options.OfType<JsonObject>().FirstOrDefault(option =>
                         variant.OptionId is { } id && ReadGuid(option["Id"]) == id && option != original)
                     ?? options.OfType<JsonObject>().FirstOrDefault(option => option != original &&
                         string.Equals(JsonString(option["Name"]), variant.Name, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            target = new JsonObject { ["Id"] = Guid.NewGuid().ToString("D"), ["Name"] = variant.Name };
            options.Add(target);
        }
        if (target["Files"] is not JsonObject files)
            target["Files"] = files = new JsonObject();
        foreach (var key in files.Select(pair => pair.Key).Where(key => SameGamePath(key, session.GamePath)).ToArray())
            files.Remove(key);
        files[session.GamePath] = relative;

        TouchV4ModMetadata(meta);
        WriteJsonAtomic(Path.Combine(session.ModRoot, "meta.json"), meta);
        return (ReadGuid(group["Id"])!.Value, EnsureOptionId(original), EnsureOptionId(target));

        static Guid EnsureOptionId(JsonObject option)
            => ReadGuid(option["Id"]) ?? throw new InvalidDataException("A Penumbra option has no identifier.");
    }

    private static bool MapsGamePath(JsonObject option, string gamePath)
        => option["Files"] is JsonObject files && files.Any(pair => SameGamePath(pair.Key, gamePath));

    private static string UniqueGroupName(JsonArray groups, string name)
    {
        var used = groups.OfType<JsonObject>().Select(group => JsonString(group["Name"]) ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = name;
        for (var number = 2; used.Contains(candidate); number++)
            candidate = $"{name} {number}";
        return candidate;
    }

    /// <summary>The current group and option names for a variant-group option, or null when either is gone.</summary>
    private static (string Group, string Option)? ReadTextureVariantSelection(string modRoot, Guid groupId, Guid optionId)
    {
        try
        {
            var meta = LoadV4ModMetadata(modRoot);
            var group = (meta["Groups"] as JsonArray ?? []).OfType<JsonObject>()
                .FirstOrDefault(candidate => ReadGuid(candidate["Id"]) == groupId);
            var option = (group?["Options"] as JsonArray ?? []).OfType<JsonObject>()
                .FirstOrDefault(candidate => ReadGuid(candidate["Id"]) == optionId);
            var groupName = JsonString(group?["Name"]);
            var optionName = JsonString(option?["Name"]);
            return groupName is null || optionName is null ? null : (groupName, optionName);
        }
        catch
        {
            return null;
        }
    }

    internal static (string Group, string Option)? ReadTextureVariantSelectionForRegression(string modRoot, Guid groupId, Guid optionId)
        => ReadTextureVariantSelection(modRoot, groupId, optionId);
}
