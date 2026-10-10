using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Services;

namespace InstantEdit.Studio.Models;

/// <summary>What one option (or the default files) of the modpack's mod gets: an item, or one of an item's files put there on its own.</summary>
internal enum PlacedKind { Item, Model, Material, Texture, Variant }

/// <summary>One thing placed in an option, for the views that list an option's contents.</summary>
internal sealed record Placed(PlacedKind Kind, PortItem Item, string Label, string Detail, Target Target, string? File = null);

/// <summary>
/// The groups and options of the mod the modpack builds, and where each item and file of it goes.
/// An item goes into one place (the default files unless put into an option), its models and
/// materials with it unless put elsewhere, a material's textures with the material. A slot of
/// variants fills a group instead: each of its files goes into the option named after it, so two
/// slots of variants sharing a group put their files of the same name into the same option.
/// </summary>
internal sealed partial class PortSession
{
    /// <summary>The largest number of options a multi-select group can have in Penumbra.</summary>
    public const int MaxMultiOptions = 32;

    public List<PackGroup> Groups => _host.Configuration.PackGroups;

    public PackGroup? FindGroup(Guid id) => Groups.Find(g => g.Id == id);

    /// <summary>The group and option a place names, or null for the default files or a place no longer there.</summary>
    public (PackGroup Group, PackOption Option)? Find(string? place)
    {
        if (!PackPlace.TryParse(place, out var g, out var o) || FindGroup(g) is not { } group || group.Find(o) is not { } option)
            return null;
        return (group, option);
    }

    /// <summary>Whether a place can take files: the default files, or an option still there.</summary>
    public bool Exists(string? place) => string.IsNullOrEmpty(place) || Find(place) != null;

    /// <summary>"Default files", or "Group › Option".</summary>
    public string Label(string? place)
        => string.IsNullOrEmpty(place) ? "Default files" : Find(place) is { } found ? $"{found.Group.Name} › {found.Option.Name}" : "(an option no longer there)";

    /// <summary>A place as saved, or the default files when what it names is gone.</summary>
    private string Valid(string? place) => Exists(place) ? place ?? PackPlace.Default : PackPlace.Default;

    // ── Where things go ──────────────────────────────────────────────────────

    public string PlaceOf(PortItem item) => Valid(item.Meta.Option);

    public string PlaceOf(PortItem item, RaceModelEntry model) => model.Option != null ? Valid(model.Option) : PlaceOf(item);

    public string PlaceOf(PortItem item, MaterialSetup material) => material.Option != null ? Valid(material.Option) : PlaceOf(item);

    public string PlaceOf(PortItem item, MaterialSetup material, TextureSlot texture)
        => texture.Option != null ? Valid(texture.Option) : PlaceOf(item, material);

    /// <summary>Puts an item into a place (its files follow unless put elsewhere).</summary>
    public void SetPlace(PortItem item, string place)
    {
        item.Meta.Option = string.IsNullOrEmpty(place) ? null : place;
        MarkDirty();
    }

    /// <summary>Puts a model, material or texture into a place, or (null) lets it go with what it belongs to.</summary>
    public void SetPlace(RaceModelEntry model, string? place)    { model.Option = place; MarkDirty(); }
    public void SetPlace(MaterialSetup material, string? place)  { material.Option = place; MarkDirty(); }
    public void SetPlace(TextureSlot texture, string? place)     { texture.Option = place; MarkDirty(); }

    // ── Shaping the structure ────────────────────────────────────────────────

    public PackGroup AddGroup(string name, bool multi)
    {
        var group = new PackGroup { Name = name.Trim(), Multi = multi };
        Groups.Add(group);
        MarkDirty();
        return group;
    }

    /// <summary>Adds an option to a group, or null when a multi-select group is full.</summary>
    public PackOption? AddOption(PackGroup group, string name, string key = "")
    {
        if (group.Multi && group.Options.Count >= MaxMultiOptions)
            return null;
        var option = new PackOption { Name = name.Trim(), Key = key };
        group.Options.Add(option);
        MarkDirty();
        return option;
    }

    /// <summary>Why a group's options cannot be picked any number at once, or null when they can.</summary>
    public static string? WhyNotMulti(PackGroup group)
        => group.Options.Count > MaxMultiOptions ? $"A group players pick any number of can have at most {MaxMultiOptions} options; this one has {group.Options.Count}." : null;

    public void Rename(PackGroup group, string name)   { group.Name = name.Trim(); MarkDirty(); }
    public void Rename(PackOption option, string name) { option.Name = name.Trim(); MarkDirty(); }

    public void SetMulti(PackGroup group, bool multi)
    {
        if (multi && WhyNotMulti(group) != null)
            return;
        group.Multi = multi;
        MarkDirty();
    }

    /// <summary>Moves an option within its group (the order players see).</summary>
    public void MoveOption(PackGroup group, int index, int delta)
    {
        int to = index + delta;
        if (index < 0 || index >= group.Options.Count || to < 0 || to >= group.Options.Count)
            return;
        (group.Options[index], group.Options[to]) = (group.Options[to], group.Options[index]);
        MarkDirty();
    }

    public void MoveGroup(PackGroup group, int delta)
    {
        int index = Groups.IndexOf(group), to = index + delta;
        if (index < 0 || to < 0 || to >= Groups.Count)
            return;
        (Groups[index], Groups[to]) = (Groups[to], Groups[index]);
        MarkDirty();
    }

    /// <summary>
    /// Takes an option out; whatever was put into it goes back with what it belongs to (an item to the
    /// default files). An option made for variant files leaves those files out of the mod.
    /// </summary>
    public void RemoveOption(PackGroup group, PackOption option)
    {
        group.Options.Remove(option);
        if (option.Key.Length > 0 && !group.IsLeftOut(option.Key))
            group.LeftOut.Add(option.Key);
        Forget(p => PackPlace.TryParse(p, out var g, out var o) && g == group.Id && o == option.Id);
        if (Selection is { Kind: TargetKind.Option } s && s.Option == PackPlace.Of(group.Id, option.Id))
            Selection = Target.ForPackGroup(group.Id);
        MarkDirty();
    }

    /// <summary>Takes a group out with its options; slots of variants that filled it are left without one (and get a new one).</summary>
    public void RemoveGroup(PackGroup group)
    {
        Groups.Remove(group);
        Forget(p => PackPlace.TryParse(p, out var g, out _) && g == group.Id);
        foreach (var item in Items)
        {
            foreach (var model in item.Models.Where(m => m.VariantGroup == group.Id))
                model.VariantGroup = null;
            foreach (var tex in item.Materials.SelectMany(m => m.Textures).Where(t => t.VariantGroup == group.Id))
                tex.VariantGroup = null;
        }
        if (Selection is { Kind: TargetKind.Group or TargetKind.Option } s
            && (s.Root == group.Id.ToString("D") || PackPlace.TryParse(s.Option, out var g, out _) && g == group.Id))
            Selection = null;
        MarkDirty();
    }

    /// <summary>Everything put into a place that is going away goes back with what it belongs to.</summary>
    private void Forget(Func<string?, bool> gone)
    {
        foreach (var item in Items)
        {
            if (gone(item.Meta.Option)) item.Meta.Option = null;
            foreach (var model in item.Models.Where(m => gone(m.Option))) model.Option = null;
            foreach (var material in item.Materials)
            {
                if (gone(material.Option)) material.Option = null;
                foreach (var tex in material.Textures.Where(t => gone(t.Option))) tex.Option = null;
            }
        }
    }

    // ── Variants ─────────────────────────────────────────────────────────────

    /// <summary>The variant files a slot was last seen to have, by the slot (refreshed by <see cref="SyncVariantGroups"/>).</summary>
    private readonly Dictionary<object, List<string>> _variantFiles = new(ReferenceEqualityComparer.Instance);

    /// <summary>The variant files of a slot as last read (images of a texture's folder, or a model's picked files).</summary>
    public IReadOnlyList<string> VariantFiles(object slot) => _variantFiles.TryGetValue(slot, out var files) ? files : Array.Empty<string>();

    /// <summary>The name a variant file gives its option: its file name without extension.</summary>
    public static string VariantKey(string file) => Path.GetFileNameWithoutExtension(file);

    /// <summary>The option of a group a variant file goes into: the one made for its name, else one named like it.</summary>
    public static PackOption? OptionFor(PackGroup group, string file)
    {
        var key = VariantKey(file);
        return group.Options.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? group.Options.FirstOrDefault(o => o.Key.Length == 0 && string.Equals(o.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Makes sure every slot of variants has a group, and each of its files an option in it (made for
    /// its file name, so renaming the option keeps it). Options are never taken out here: one whose
    /// file is gone stays until it is removed by hand. Returns whether anything changed.
    /// </summary>
    public bool SyncVariantGroups()
    {
        bool changed = false;
        _variantFiles.Clear();
        foreach (var item in Items)
        {
            foreach (var model in item.Models.Where(m => m.UseVariants && !m.UseDummy))
            {
                var files = model.VariantPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                _variantFiles[model] = files;
                if (files.Count == 0)
                    continue;
                var group = EnsureGroup(model.VariantGroup, $"{item.Subject.DisplayName} · {model.RaceGender.DisplayName} model", g => model.VariantGroup = g);
                if (Fill(group, files))
                    changed = true;
            }

            foreach (var material in item.Materials)
            {
                foreach (var tex in material.Textures.Where(t => t.UseVariants && !t.UseWhiteDummy && !t.IsShared))
                {
                    var files = ReadFolder(tex.SourcePath);
                    _variantFiles[tex] = files;
                    if (files.Count == 0)
                        continue;
                    var group = EnsureGroup(tex.VariantGroup, $"{material.Name} · {TextureTypeInfo.DisplayName(tex.Type)}", g => tex.VariantGroup = g);
                    if (Fill(group, files))
                        changed = true;
                }
            }
        }
        if (changed)
            MarkDirty();
        return changed;

        PackGroup EnsureGroup(Guid? id, string name, Action<Guid> link)
        {
            if (id is { } existing && FindGroup(existing) is { } found)
                return found;
            var made = new PackGroup { Name = name };
            Groups.Add(made);
            link(made.Id);
            changed = true;
            return made;
        }

        bool Fill(PackGroup group, List<string> files)
        {
            bool added = false;
            foreach (var file in files)
            {
                if (OptionFor(group, file) != null || group.IsLeftOut(VariantKey(file)))
                    continue;
                if (group.Multi && group.Options.Count >= MaxMultiOptions)
                    break;
                group.Options.Add(new PackOption { Name = VariantKey(file), Key = VariantKey(file) });
                added = true;
            }
            return added;
        }
    }

    private static List<string> ReadFolder(string folder)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) ? TextureSources.EnumerateImages(folder).ToList() : new List<string>();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    /// <summary>Lets a variant file left out of a group back in: it gets its option again.</summary>
    public void BringBack(PackGroup group, string key)
    {
        group.LeftOut.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        MarkDirty();
        SyncVariantGroups();
    }

    /// <summary>The group a slot of variants fills, if it has one.</summary>
    public PackGroup? VariantGroupOf(TextureSlot tex) => tex.VariantGroup is { } id ? FindGroup(id) : null;
    public PackGroup? VariantGroupOf(RaceModelEntry model) => model.VariantGroup is { } id ? FindGroup(id) : null;

    /// <summary>Points a slot of variants at another group (null: one of its own, made with the next refresh).</summary>
    public void SetVariantGroup(TextureSlot tex, Guid? group)        { tex.VariantGroup = group; MarkDirty(); SyncVariantGroups(); }
    public void SetVariantGroup(RaceModelEntry model, Guid? group)   { model.VariantGroup = group; MarkDirty(); SyncVariantGroups(); }

    // ── What is where ────────────────────────────────────────────────────────

    /// <summary>
    /// Everything put into a place: the items it holds, then each model, material or texture of
    /// another item put there on its own, then the variant files that go into it.
    /// </summary>
    public List<Placed> ContentsOf(string place)
    {
        var result = new List<Placed>();
        foreach (var item in Items)
        {
            var itemPlace = PlaceOf(item);
            if (itemPlace == place)
                result.Add(new Placed(PlacedKind.Item, item, item.Subject.DisplayName, $"{item.Subject.KindLabel} · {item.Subject.IdDisplay}",
                    new Target(TargetKind.Item, item.Key)));

            for (int i = 0; i < item.Models.Count; i++)
            {
                var model = item.Models[i];
                if (model.UseVariants && !model.UseDummy)
                    continue;
                var modelPlace = PlaceOf(item, model);
                if (modelPlace == place && modelPlace != itemPlace)
                    result.Add(new Placed(PlacedKind.Model, item, $"{model.RaceGender.DisplayName} model", item.Subject.DisplayName,
                        new Target(TargetKind.Model, item.Key, i)));
            }

            for (int m = 0; m < item.Materials.Count; m++)
            {
                var material = item.Materials[m];
                var materialPlace = PlaceOf(item, material);
                if (materialPlace == place && materialPlace != itemPlace)
                    result.Add(new Placed(PlacedKind.Material, item, material.Name, item.Subject.DisplayName,
                        new Target(TargetKind.Material, item.Key, m)));
                for (int t = 0; t < material.Textures.Count; t++)
                {
                    var tex = material.Textures[t];
                    if (tex.UseVariants && !tex.UseWhiteDummy || tex.IsShared)
                        continue;
                    var texPlace = PlaceOf(item, material, tex);
                    if (texPlace == place && texPlace != materialPlace)
                        result.Add(new Placed(PlacedKind.Texture, item, $"{material.Name} · {TextureTypeInfo.DisplayName(tex.Type)}", item.Subject.DisplayName,
                            new Target(TargetKind.Texture, item.Key, m, t)));
                }
            }
        }

        // Variant files go into the option made for their name.
        if (Find(place) is { } found)
        {
            foreach (var item in Items)
            {
                for (int i = 0; i < item.Models.Count; i++)
                {
                    var model = item.Models[i];
                    if (model.UseVariants && model.VariantGroup == found.Group.Id)
                        foreach (var file in VariantFiles(model).Where(f => OptionFor(found.Group, f) == found.Option))
                            result.Add(new Placed(PlacedKind.Variant, item, $"{model.RaceGender.DisplayName} model", Path.GetFileName(file),
                                new Target(TargetKind.Model, item.Key, i), file));
                }
                for (int m = 0; m < item.Materials.Count; m++)
                {
                    var material = item.Materials[m];
                    for (int t = 0; t < material.Textures.Count; t++)
                    {
                        var tex = material.Textures[t];
                        if (tex.UseVariants && tex.VariantGroup == found.Group.Id)
                            foreach (var file in VariantFiles(tex).Where(f => OptionFor(found.Group, f) == found.Option))
                                result.Add(new Placed(PlacedKind.Variant, item, $"{material.Name} · {TextureTypeInfo.DisplayName(tex.Type)}", Path.GetFileName(file),
                                    new Target(TargetKind.Texture, item.Key, m, t), file));
                    }
                }
            }
        }
        return result;
    }

    /// <summary>The slots of variants that fill a group, as targets with a label each.</summary>
    public List<(Target Target, string Label)> VariantSlotsOf(PackGroup group)
    {
        var result = new List<(Target, string)>();
        foreach (var item in Items)
        {
            for (int i = 0; i < item.Models.Count; i++)
                if (item.Models[i].UseVariants && item.Models[i].VariantGroup == group.Id)
                    result.Add((new Target(TargetKind.Model, item.Key, i), $"{item.Subject.DisplayName} · {item.Models[i].RaceGender.DisplayName} model"));
            for (int m = 0; m < item.Materials.Count; m++)
                for (int t = 0; t < item.Materials[m].Textures.Count; t++)
                    if (item.Materials[m].Textures[t] is { UseVariants: true } tex && tex.VariantGroup == group.Id)
                        result.Add((new Target(TargetKind.Texture, item.Key, m, t), $"{item.Materials[m].Name} · {TextureTypeInfo.DisplayName(tex.Type)}"));
        }
        return result;
    }
}
