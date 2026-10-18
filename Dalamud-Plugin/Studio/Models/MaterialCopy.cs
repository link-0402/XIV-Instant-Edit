using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantEdit.Studio.Models;

/// <summary>
/// Copies of a material made for another item: one pasted from the clipboard, and the ones the build
/// writes for mesh parts set to another item's material (see <see cref="SharedMaterialLink"/>).
/// </summary>
internal static class MaterialCopy
{
    /// <summary>
    /// A copy of <paramref name="source"/> for <paramref name="to"/>, renamed to its naming stem
    /// (mt_c0201e0039_top_b → mt_c0201e0039_dwn_b) and unique among <paramref name="taken"/>. With
    /// <paramref name="shareTextures"/>, every texture points at the game path the original writes, so
    /// both share one set of texture files; otherwise they are written again from the same sources.
    /// </summary>
    public static MaterialSetup For(MaterialSetup source, PortSubject from, PortSubject to, IEnumerable<string> taken, bool shareTextures)
    {
        var copy = source.Clone();
        copy.Id   = Guid.NewGuid();
        copy.Name = UniqueName(taken.ToList(), RetargetName(source.Name, from, to));
        foreach (var tex in copy.Textures)
        {
            tex.SharedGamePath = !shareTextures ? string.Empty
                : tex.IsShared ? tex.SharedGamePath   // already shared: keep pointing at the original
                : from.TextureGamePath(MaterialNaming.ComposeTextureName(source.Name, tex.Postfix));
        }
        return copy;
    }

    /// <summary>A material the build added to an item for its shared links, and what it was copied from.</summary>
    public sealed record SharedCopy(MaterialSetup Copy, PortSubject From, MaterialSetup Source);

    /// <summary>
    /// Turns the shared links of an item's models into materials of its own, for the build: one copy
    /// per material linked to (however many mesh parts use it), appended to <paramref name="materials"/>
    /// and going into <paramref name="place"/>, with each link set to its copy's index. A link whose
    /// material is gone (its item left the pack, or it was removed) keeps the file's own name.
    /// <paramref name="models"/> and <paramref name="materials"/> are the build's own copies: both change.
    /// </summary>
    public static List<SharedCopy> AddShared(PortSubject subject, List<MaterialSetup> materials, IEnumerable<RaceModelEntry> models,
        Func<uint, Guid, (PortSubject Subject, MaterialSetup Material)?> find, string? place)
    {
        var copies  = new List<SharedCopy>();
        var indexOf = new Dictionary<(uint Item, Guid Material), int>();
        foreach (var model in models)
        {
            foreach (var link in model.SharedLinks)
            {
                var links = link.Variant < 0 ? model.MaterialLinks : model.EditVariantLinks(link.Variant);
                if (ModelMaterialLinks.Stored(links, link.Slot) != ModelMaterialLinks.Shared)
                    continue;   // the slot was set to something else since

                var key = (link.Item, link.Material);
                if (!indexOf.TryGetValue(key, out var index))
                {
                    index = ModelMaterialLinks.Keep;
                    if (find(link.Item, link.Material) is { } found)
                    {
                        var copy = For(found.Material, found.Subject, subject, materials.Select(m => m.Name), shareTextures: true);
                        copy.Option = place;
                        materials.Add(copy);
                        index = materials.Count - 1;
                        copies.Add(new SharedCopy(copy, found.Subject, found.Material));
                    }
                    indexOf[key] = index;
                }
                ModelMaterialLinks.Store(links, link.Slot, index);
            }
            model.SharedLinks.Clear();
        }
        return copies;
    }

    /// <summary>Swaps the source subject's naming stem for the target's, when the name follows it.</summary>
    public static string RetargetName(string name, PortSubject from, PortSubject to)
    {
        static string Stem(PortSubject s, RaceGender? race)
        {
            var n = s.DefaultMaterialName(1, race);   // "mt_c0201h0005_hir_a"
            int cut = n.LastIndexOf('_');
            return cut > 0 ? n[..cut] : n;            // "mt_c0201h0005_hir"
        }

        // Gear is named per gender, so a copied name can follow either of the two stems.
        foreach (var race in new RaceGender?[] { null, RaceInfo.BaseFor(PlayerGender.Female) })
        {
            var fromStem = Stem(from, race);
            if (name.StartsWith(fromStem, StringComparison.OrdinalIgnoreCase))
                return Stem(to, race) + name[fromStem.Length..];
        }
        return name;
    }

    /// <summary>Makes a name unique among <paramref name="taken"/>, by bumping a trailing "_a" letter (or adding one).</summary>
    public static string UniqueName(IReadOnlyCollection<string> taken, string name)
    {
        bool Taken(string n) => taken.Any(t => string.Equals(t, n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(name)) return name;

        bool lettered = name.Length > 2 && name[^2] == '_' && char.IsAsciiLetterLower(name[^1]);
        var stem = lettered ? name[..^2] : name;
        for (char c = lettered ? (char)(name[^1] + 1) : 'b'; c <= 'z'; c++)
            if (!Taken($"{stem}_{c}"))
                return $"{stem}_{c}";
        for (int i = 2; ; i++)
            if (!Taken($"{name}_{i}"))
                return $"{name}_{i}";
    }
}
