using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Resources;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;

namespace InstantEdit.Studio.Services;

/// <summary>
/// Checks the resources being replaced or added for what keeps them out of the mod: a missing
/// source file, a file that cannot stand in for what it replaces, an option that is gone, two
/// replacements for the same file, and the size of images (the same advice Build mode's texture
/// slots get).
/// </summary>
internal static class ReplacementValidator
{
    public static List<PortIssue> Validate(ModSession session, ImageFileCache images)
    {
        var issues = new List<PortIssue>();
        var project = session.Project;
        if (project == null)
            return issues;

        var meta = project.Meta;
        var seen = new HashSet<(string Option, string Path)>();

        foreach (var replacement in project.Sidecar.Replacements)
        {
            var option = OptionRef.Parse(replacement.Option);
            var path = PathNormalizer.GamePath(replacement.GamePath);
            var target = session.Graph.Find(path, option)?.Target ?? Target.ForResource(string.Empty, path, replacement.Option);
            var who = Path.GetFileName(path);

            if (!PathNormalizer.LooksLikeGamePath(path))
            {
                issues.Add(new(Severity.Error, target, $"{replacement.GamePath}: not a game path, so the game would never load it."));
                continue;
            }

            if (!meta.OptionExists(option))
            {
                issues.Add(new(Severity.Error, target, $"{who}: the option it was meant for is no longer in the mod — pick it again, or drop it."));
                continue;
            }

            if (!seen.Add((option.Serialize().ToLowerInvariant(), path)))
                issues.Add(new(Severity.Warning, target, $"{who}: replaced twice in {meta.OptionLabel(option)} — only one of them is written."));

            if (replacement.Authored is { } material)
            {
                ValidateAuthored(replacement, material, path, target, who, images, issues);
                continue;
            }

            if (replacement.ReassignsMaterials)
            {
                ValidateReassigned(session, replacement, path, option, target, who, issues);
                if (string.IsNullOrWhiteSpace(replacement.SourcePath))
                    continue;
            }

            var source = replacement.SourcePath;
            if (string.IsNullOrWhiteSpace(source))
            {
                issues.Add(new(Severity.Warning, target, $"{who}: no file set — it is skipped."));
                continue;
            }
            if (!File.Exists(source))
            {
                issues.Add(new(Severity.Error, target, $"{who}: {Path.GetFileName(source)} does not exist any more."));
                continue;
            }
            if (!ResourceKinds.Accepts(path, source))
            {
                issues.Add(new(Severity.Error, target, $"{who}: a {Path.GetExtension(source)} file cannot stand in for a {Path.GetExtension(path)} file."));
                continue;
            }
            if (IsInside(source, project.FolderPath))
                issues.Add(new(Severity.Info, target, $"{who}: its file is already inside the mod folder — it is copied to its own place there."));

            if (!ResourceKinds.IsTexturePath(path))
                continue;

            var ext = Path.GetExtension(source).ToLowerInvariant();
            if (ext is ".tex" or ".atex")
            {
                if (!TexHeader.TryRead(source, out _))
                    issues.Add(new(Severity.Error, target, $"{who}: {Path.GetFileName(source)} is not a texture the game can read."));
                continue;
            }

            var facts = images.Read(source);
            if (!facts.Ok)
            {
                issues.Add(new(Severity.Error, target, $"{who}: {Path.GetFileName(source)} could not be read as an image ({facts.Error})."));
                continue;
            }
            if (!IsPowerOfTwo(facts.Width) || !IsPowerOfTwo(facts.Height))
                issues.Add(new(Severity.Info, target, $"{who}: {facts.Size} is not a power of two on each side — the game's own textures always are, and smaller mipmaps may look off."));
            if (replacement.Compression == TextureCompression.None && facts.Pixels >= 1024 * 1024)
                issues.Add(new(Severity.Warning, target,
                    $"{who}: {facts.Size} written uncompressed is {ImageFacts.Mib(facts.UncompressedBytes)}, which the game then carries in memory — a block format keeps it to a quarter of that."));
        }
        return issues;
    }

    /// <summary>
    /// A model whose material slots are pointed elsewhere: only a model has slots, each name has to
    /// be a material, the slot has to exist, and what it names should be somewhere.
    /// </summary>
    private static void ValidateReassigned(ModSession session, Models.Project.ResourceReplacement replacement, string path, OptionRef option,
        Target target, string who, List<PortIssue> issues)
    {
        if (ResourceKinds.FromGamePath(path) != ResourceKind.Model)
        {
            issues.Add(new(Severity.Warning, target, $"{who}: only a model has material slots — the reassignment is ignored."));
            return;
        }

        var model = session.Graph.Find(path, option);
        for (int slot = 0; slot < replacement.MaterialNames.Count; slot++)
        {
            var name = replacement.MaterialNames[slot];
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (!name.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new(Severity.Error, target, $"{who}: slot {slot + 1} names {name}, which is not a material (.mtrl)."));
                continue;
            }
            if (model == null || model.MaterialSlots.Count == 0)
                continue;
            if (slot >= model.MaterialSlots.Count)
            {
                issues.Add(new(Severity.Warning, target, $"{who}: has {model.MaterialSlots.Count} material slot(s), so slot {slot + 1} ({name}) is ignored."));
                continue;
            }

            var file = Path.GetFileName(name.Replace('\\', '/'));
            var loads = model.Children.Select(c => c.Node)
                .FirstOrDefault(n => n.Kind == ResourceKind.Material && string.Equals(n.FileName, file, StringComparison.OrdinalIgnoreCase));
            if (loads is { Origin: OriginKind.Missing })
                issues.Add(new(Severity.Warning, target, $"{who}: slot {slot + 1} loads {file}, which neither the mod nor the game has — make it, or pick another."));
        }
    }

    /// <summary>A material made here: something to make it from, and an image (or a placeholder) for each slot.</summary>
    private static void ValidateAuthored(Models.Project.ResourceReplacement replacement, MaterialSetup material, string path,
        Target target, string who, ImageFileCache images, List<PortIssue> issues)
    {
        if (!replacement.CloneGameMaterial && !replacement.CloneModMaterial
            && MaterialPresetLibrary.FindPresetPath(material.ShaderType, material.Textures.Select(t => t.Type).ToHashSet()) == null)
            issues.Add(new(Severity.Error, target,
                $"{who}: there is no bundled {ShaderInfo.DisplayName(material.ShaderType)} preset with these texture slots to make it from."));

        if (material.Textures.Count == 0)
            issues.Add(new(Severity.Warning, target, $"{who}: has no texture slots — it is not written."));

        foreach (var tex in material.Textures)
        {
            var slot = $"{who} / {TextureTypeInfo.DisplayName(tex.Type)}";
            if (tex.IsShared && MaterialPatcher.IsMatcapLibraryTexture(tex.SharedGamePath))
                issues.Add(new(Severity.Info, target,
                    $"{slot}: reads {tex.SharedGamePath} from the MatCap Library mod, which players need installed. Pick an image to ship your own instead."));
            if (tex.UseWhiteDummy || tex.IsShared)
                continue;
            if (!replacement.CloneGameMaterial && !replacement.CloneModMaterial && PortValidator.UsesMatcapLibrary(material, tex))
                issues.Add(new(Severity.Info, target, $"{slot}: {PortValidator.MatcapLibraryNote}"));
            else if (tex.UseVariants)
                issues.Add(new(Severity.Warning, target, $"{slot}: a folder of variants is not supported for a material made here — pick one image."));
            else if (string.IsNullOrWhiteSpace(tex.SourcePath))
                issues.Add(new(Severity.Warning, target, $"{slot}: no image set — the slot is left out."));
            else if (!File.Exists(tex.SourcePath))
                issues.Add(new(Severity.Error, target, $"{slot}: {Path.GetFileName(tex.SourcePath)} does not exist."));
            else if (!ResourceKinds.Accepts("x/x.tex", tex.SourcePath))
                issues.Add(new(Severity.Error, target, $"{slot}: {Path.GetFileName(tex.SourcePath)} is not an image."));
            else if (Path.GetExtension(tex.SourcePath).ToLowerInvariant() is not (".tex" or ".atex") && !images.Read(tex.SourcePath).Ok)
                issues.Add(new(Severity.Error, target, $"{slot}: {Path.GetFileName(tex.SourcePath)} could not be read as an image."));
        }
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private static bool IsInside(string path, string folder)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
