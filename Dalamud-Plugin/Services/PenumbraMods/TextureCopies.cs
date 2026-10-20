using System.Text.Json.Nodes;
using InstantEdit.Models;
using static InstantEdit.Services.PenumbraMods.ModFiles;
using static InstantEdit.Services.PenumbraMods.ModJson;
using static InstantEdit.Services.PenumbraMods.ModMetadata;
using static InstantEdit.Services.PenumbraMods.ModPaths;

namespace InstantEdit.Services.PenumbraMods;

/// <summary>
/// A texture copy that only one material uses: the copy gets its own game path in the material's mod, mapped
/// wherever the mod maps the material, and the material names that path instead. Every other material keeps
/// the original texture.
/// </summary>
internal static class TextureCopies
{
    /// <summary>
    /// Plans the copy of the texture the material names as <paramref name="textureGamePath"/> (stored or in its
    /// DirectX 11 form): a game path in a folder of its own beside the original, keeping the file name, and a mod
    /// file beside the material. Returns the plan, the session's game path and the copy's mod-relative file.
    /// </summary>
    internal static (TextureCopyPlan Plan, string GamePath, string RelativePath) Plan(byte[] material, string materialGamePath,
        string materialRelativePath, string textureGamePath, string sourceFile, Guid id)
    {
        if (!IsSafeGameResourcePath(materialRelativePath, ".mtrl")) throw new IOException("The material's mod file is invalid.");
        var reference = MaterialPreviewBundleBuilder.ReadMaterialTextures(material).FirstOrDefault(texture =>
                            SameGamePath(texture.StoredPath, textureGamePath)) ??
                        MaterialPreviewBundleBuilder.ReadMaterialTextures(material).FirstOrDefault(texture =>
                            SameGamePath(texture.Dx11Path, textureGamePath))
                        ?? throw new IOException("The material no longer names this texture. Refresh On Screen and try again.");
        var stored = reference.StoredPath;
        var separator = stored.LastIndexOf('/');
        var tag = "ie_" + id.ToString("N")[..8];
        var copy = separator < 0 ? $"{tag}/{stored}" : $"{stored[..separator]}/{tag}/{stored[(separator + 1)..]}";
        // The DirectX 11 flag stays on the material's entry, so the game asks for the copy in the same form.
        var dx11 = !SameGamePath(reference.Dx11Path, stored);
        var copyDx11 = dx11 ? PathRules.Dx11TexturePath(copy, 0x8000) : copy;
        var materialFolder = materialRelativePath[..(materialRelativePath.LastIndexOf('/') + 1)];
        var relative = $"{materialFolder}{tag}_{Path.GetFileName(stored)}";
        if (!IsSafeGameResourcePath(copy, ".tex") || !IsSafeGameResourcePath(relative, ".tex"))
            throw new IOException("The texture's path can't be copied.");
        var plan = new TextureCopyPlan
        {
            SourceGamePath = textureGamePath, SourceFile = sourceFile,
            MaterialGamePath = materialGamePath, MaterialRelativePath = materialRelativePath,
            MaterialHash = TextureFiles.Hash(material),
            MaterialTexture = stored, CopyTexture = copy,
            CopyGamePaths = dx11 ? [copy, copyDx11] : [copy],
        };
        var gamePath = dx11 && SameGamePath(reference.Dx11Path, textureGamePath) ? copyDx11 : copy;
        return (plan, gamePath, relative);
    }

    /// <summary>Whether a restored plan names only paths a copy may write.</summary>
    internal static bool IsSafe(TextureCopyPlan plan)
        => IsSafeGameResourcePath(plan.SourceGamePath, ".tex") && IsSafeGameResourcePath(plan.MaterialRelativePath, ".mtrl") &&
           IsSafeGameResourcePath(plan.CopyTexture, ".tex") && plan.CopyGamePaths.Count > 0 &&
           plan.CopyGamePaths.All(path => IsSafeGameResourcePath(path, ".tex"));

    /// <summary>The material's file, with a conflict when it is gone or something else changed it since the session opened.</summary>
    internal static byte[] ReadMaterial(string modRoot, TextureCopyPlan plan)
    {
        var file = Path.Combine(modRoot, plan.MaterialRelativePath.Replace('/', Path.DirectorySeparatorChar));
        TextureFiles.EnsureLocalPath(file);
        if (!File.Exists(file))
            throw new TextureConflictException("The texture's material is gone from its mod. Open the texture again.");
        var bytes = TextureFiles.Read(file);
        if (TextureFiles.Hash(bytes) != plan.MaterialHash)
            throw new TextureConflictException("Something else changed the texture's material. Discard this session and make the copy again.");
        return bytes;
    }

    /// <summary>
    /// Writes the copy: the TEX beside the material, its game paths in every option that maps the material, then
    /// the material naming it (after a backup). Something that fails puts the mod back as it was. Returns the
    /// material's backup.
    /// </summary>
    internal static string Write(string modRoot, string modDirectory, string relativePath, TextureCopyPlan plan, byte[] tex,
        ModelBackupStore backups, Func<bool> stillCurrent)
    {
        _ = TextureFiles.ReadTex(tex);
        var material = ReadMaterial(modRoot, plan);
        var rewritten = MashupPlanning.RewriteMaterialTexturePaths(material,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [plan.MaterialTexture] = plan.CopyTexture });
        var metaFile = Path.Combine(modRoot, "meta.json");
        var metaText = File.ReadAllText(metaFile);
        var meta = LoadV4ModMetadata(modRoot);
        var mapped = 0;
        foreach (var files in MaterialContainers(meta, plan.MaterialRelativePath))
        {
            foreach (var gamePath in plan.CopyGamePaths)
                files[gamePath] = relativePath;
            mapped++;
        }
        if (mapped == 0)
            throw new TextureConflictException("The mod no longer uses the texture's material. Open the texture again.");
        TouchV4ModMetadata(meta);
        if (!stillCurrent()) throw new OperationCanceledException("A newer save is pending.");

        var target = Path.Combine(modRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var materialFile = Path.Combine(modRoot, plan.MaterialRelativePath.Replace('/', Path.DirectorySeparatorChar));
        TextureFiles.EnsureLocalPath(target);
        WriteBytesAtomic(modRoot, relativePath, tex);
        var metaWritten = false;
        var temp = materialFile + $".{Guid.NewGuid():N}.tmp";
        try
        {
            WriteJsonAtomic(metaFile, meta);
            metaWritten = true;
            File.WriteAllBytes(temp, rewritten);
            var backup = backups.Create(materialFile, modDirectory, plan.MaterialRelativePath);
            if (TextureFiles.Hash(TextureFiles.Read(materialFile)) != plan.MaterialHash)
                throw new TextureConflictException("Something else changed the texture's material during the save. Discard this session and make the copy again.");
            File.Replace(temp, materialFile, null);
            return backup;
        }
        catch
        {
            if (metaWritten) File.WriteAllText(metaFile, metaText);
            if (File.Exists(target)) File.Delete(target);
            throw;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>The Files maps of the mod's default data and every option that maps the material's file.</summary>
    private static List<JsonObject> MaterialContainers(JsonObject meta, string materialRelativePath)
    {
        var result = new List<JsonObject>();
        var key = RelativeKey(materialRelativePath);
        void Add(JsonNode? container)
        {
            if (container is JsonObject value && value["Files"] is JsonObject files &&
                files.Any(pair => JsonString(pair.Value) is { } path && string.Equals(RelativeKey(path), key, StringComparison.OrdinalIgnoreCase)))
                result.Add(files);
        }
        Add(meta["DefaultData"]);
        foreach (var group in (meta["Groups"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var name in new[] { "Options", "Containers" })
                foreach (var container in (group[name] as JsonArray ?? []).OfType<JsonObject>())
                    Add(container);
        return result;
    }

    private static string RelativeKey(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.TrimStart('/');
    }
}
