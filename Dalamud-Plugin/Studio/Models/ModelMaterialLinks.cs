using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantEdit.Studio.Models;

/// <summary>
/// How the material slots of a source .mdl map onto the item's configured materials.
///
/// A model made anywhere else still references the material names it was exported with, so the
/// .mtrl files this tool writes would sit in the mod unused. The build rewrites those references
/// (see <c>ModelMaterialRewriter</c>); this is the mapping it follows, kept per race model — and
/// per variant file — as one entry per material slot of that file: <see cref="Auto"/>,
/// <see cref="Keep"/>, or an index into the item's Materials.
///
/// Slots the user never touched are simply absent from the stored list, which reads as
/// <see cref="Auto"/>, so a model dropped in and left alone still comes out wired.
/// </summary>
internal static class ModelMaterialLinks
{
    /// <summary>Match the file's own name against this item's materials, else take the material in the same slot order.</summary>
    public const int Auto = -2;

    /// <summary>Leave the file's own reference alone — a mesh part that keeps a vanilla material, say.</summary>
    public const int Keep = -1;

    /// <summary>
    /// The name each of the item's materials is written under for <paramref name="race"/>, in the
    /// form a model references it ("/mt_c0101e0025_top_a.mtrl"). Hair writes one copy per race, so
    /// which race is asking decides the name; gear ignores it.
    /// </summary>
    public static List<string> WrittenNames(PortSubject subject, IReadOnlyList<MaterialSetup> materials, RaceGender race)
        => materials.Select(m => Reference(subject.MaterialNameFor(m.Name, race))).ToList();

    /// <summary>A material name as a model references it: leading slash, lower case, ".mtrl".</summary>
    public static string Reference(string materialName)
        => $"/{MaterialNaming.SanitizeFileName(materialName)}.mtrl";

    /// <summary>What is stored for a slot; slots past the end of the list have never been decided.</summary>
    public static int Stored(IReadOnlyList<int>? links, int slot)
        => links != null && slot >= 0 && slot < links.Count ? links[slot] : Auto;

    /// <summary>Stores a slot's choice, padding the list with <see cref="Auto"/> up to it.</summary>
    public static void Store(List<int> links, int slot, int value)
    {
        while (links.Count <= slot)
            links.Add(Auto);
        links[slot] = value;
    }

    /// <summary>
    /// Keeps every model's explicit picks (stored indices into the item's materials) on the same
    /// materials after the materials list changed: <paramref name="map"/> takes each old index to its
    /// new one, or to <see cref="Keep"/> / <see cref="Auto"/>. Covers the race models' own links and
    /// every variant file's. <see cref="Auto"/> and <see cref="Keep"/> entries are not indices and stay.
    /// </summary>
    public static void Remap(IEnumerable<RaceModelEntry> models, Func<int, int> map)
    {
        foreach (var model in models)
        {
            RemapList(model.MaterialLinks, map);
            foreach (var links in model.VariantMaterialLinks)
                RemapList(links, map);
        }
    }

    private static void RemapList(List<int> links, Func<int, int> map)
    {
        for (int i = 0; i < links.Count; i++)
            if (links[i] >= 0)
                links[i] = map(links[i]);
    }

    /// <summary>The material at <paramref name="index"/> was removed: picks of it keep the file's own name, later ones move up.</summary>
    public static Func<int, int> Removed(int index) => i => i == index ? Keep : i > index ? i - 1 : i;

    /// <summary>A material was inserted at <paramref name="index"/>: picks at or after it move down.</summary>
    public static Func<int, int> Inserted(int index) => i => i >= index ? i + 1 : i;

    /// <summary>Two materials swapped places.</summary>
    public static Func<int, int> Swapped(int a, int b) => i => i == a ? b : i == b ? a : i;

    /// <summary>The materials were replaced as a whole: every pick goes back to the automatic match.</summary>
    public static Func<int, int> Reset => _ => Auto;

    /// <summary>
    /// Material names that name a shared body / feature group — a common base body mod's own body
    /// ("bibo"), its pubic overlay ("bibopube"), or its piercings — rather than anything an item
    /// ported here would supply. These sit in some mesh material slots right alongside the slots an
    /// item actually replaces. Automatic assignment must never guess at them by position: whatever
    /// shared body mod the wearer has installed already supplies them, and repointing one at this
    /// item's own material would silently break it for everyone using that shared group.
    /// </summary>
    private static readonly string[] SharedFeatureNameParts = { "bibo", "pube", "pierc" };

    /// <summary>Whether a mesh part's own material name names one of the shared groups above.</summary>
    public static bool IsSharedFeature(string referenced)
    {
        var name = Normalize(referenced);
        foreach (var part in SharedFeatureNameParts)
            if (name.Contains(part, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// The material a slot ends up on: an index into the item's materials, or <see cref="Keep"/>.
    /// A stored index whose material has since been removed falls back to keeping the file's own
    /// reference rather than silently pointing at a different material. <paramref name="referenced"/>
    /// is every mesh part's own material name, in slot order, so the position fallback below can
    /// skip shared groups rather than count them.
    /// </summary>
    public static int Resolve(int stored, int slot, IReadOnlyList<string> referenced, IReadOnlyList<string> written)
    {
        if (stored >= 0)
            return stored < written.Count ? stored : Keep;
        if (stored != Auto)
            return Keep;

        var name = referenced[slot];
        int byName = IndexOfName(written, name);
        if (byName >= 0)
            return byName;

        if (IsSharedFeature(name))
            return Keep;

        // Position among only the slots that are not themselves a shared group, so those slots
        // don't consume a position that belongs to one of the item's own materials.
        int position = 0;
        for (int i = 0; i < slot; i++)
            if (!IsSharedFeature(referenced[i]))
                position++;

        return position < written.Count ? position : Keep;
    }

    /// <summary>
    /// The new name for every material slot of the file, in slot order — null where the file keeps
    /// its own. This is what the build hands the rewriter.
    /// </summary>
    public static string?[] Plan(IReadOnlyList<string> referenced, IReadOnlyList<int>? links, IReadOnlyList<string> written)
    {
        var plan = new string?[referenced.Count];
        for (int i = 0; i < referenced.Count; i++)
        {
            int material = Resolve(Stored(links, i), i, referenced, written);
            plan[i] = material >= 0 ? written[material] : null;
        }
        return plan;
    }

    /// <summary>How many slots the build would actually rewrite — for the report and the editor's summary.</summary>
    public static int CountChanges(IReadOnlyList<string> referenced, string?[] plan)
    {
        int changed = 0;
        for (int i = 0; i < plan.Length && i < referenced.Count; i++)
            if (plan[i] != null && !SameName(plan[i]!, referenced[i]))
                changed++;
        return changed;
    }

    /// <summary>Index of the material written under this name, or -1. Models differ in case and leading slash.</summary>
    public static int IndexOfName(IReadOnlyList<string> written, string referenced)
    {
        for (int i = 0; i < written.Count; i++)
            if (SameName(written[i], referenced))
                return i;
        return -1;
    }

    private static bool SameName(string a, string b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string name)
    {
        name = name.Trim().TrimStart('/');
        return name.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
    }
}
