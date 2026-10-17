using System;
using System.Collections.Generic;
using System.Linq;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services.Resources;

namespace InstantEdit.Studio.Services.Check;

/// <summary>Item variants: the material sets the IMC data picks for them, and the parts their attribute masks hide.</summary>
internal sealed partial class ModCheck
{
    private void VariantFindings()
    {
        foreach (var walk in _walks.Where(w => w.Info is { UsesImc: true } && w.Layout is not null))
        {
            var model = walk.Info!;
            var folderUses = walk.Materials.Where(m => m.Resolved is not null).GroupBy(m => m.MaterialSet).ToDictionary(g => g.Key, g => g.ToArray());
            foreach (var (folder, uses) in folderUses)
            {
                var missing = uses.Where(u => u.Resolved!.Resolution.Source is ResolutionSource.Unresolved or ResolutionSource.Exclusive).ToArray();
                if (missing.Length == 0)
                    continue;
                // Materials this model's other material sets provide can be redirected into this one as well.
                var sources = missing.Select(use => (use, source: SourceFolder(walk, use.Name, folder))).ToArray();
                var fixable = sources.Where(pair => pair.source is not null).ToArray();
                if (fixable.Length == 0)
                    continue;
                foreach (var (use, _) in fixable)
                    _coveredByVariantFix.Add(use.Resolved!);

                var rows = walk.Rows.Where(row => row.Entry.MaterialId == folder).ToArray();
                var items = rows.SelectMany(row => row.Items).Distinct().ToArray();
                var severity = items.Length > 0 || _input.Items is null
                    ? Severity.Error
                    : rows.All(row => row.Variant == 0) ? Severity.Info : Severity.Warning;
                var who = items.Length > 0 ? string.Join(", ", items) : $"Variant {string.Join(", ", rows.Select(row => row.Variant))}";
                var provided = walk.Folders.Where(f => f != folder && folderUses.GetValueOrDefault(f)?.Any(u => u.Resolved!.Resolution.IsModFile) == true)
                    .Select(f => $"v{f:D4}").ToArray();
                var key = $"V001:{walk.Container.Option.Serialize()}:{walk.Path}:{folder}";
                AddFinding(key, "V001", CheckCategory.Variants, severity, $"Material set v{folder:D4} is not provided",
                    $"{who}: IMC material set v{folder:D4} of {model.Directory}. This mod redirects the model for every variant, but its materials only in {string.Join(", ", provided)}, "
                  + $"so {missing.Length} of the materials asked for in v{folder:D4} don't exist and the item is invisible.",
                    $"{model.Directory}/material/v{folder:D4}/", walk.Container);
                var edits = fixable.Select(pair => (RepairEdit)new AddFileEdit(pair.source!.Value.Container, pair.use.Resolved!.Path, pair.source.Value.RelPath)).ToArray();
                var agree = fixable.All(pair => pair.source!.Value.Agree);
                AddRepair($"variant:{walk.Container.Option.Serialize()}:{walk.Path}:{folder}", key, CheckCategory.Variants,
                    $"Use the same materials for v{folder:D4}{(items.Length > 0 ? $" ({string.Join(", ", items.Take(2))}{(items.Length > 2 ? " and more" : string.Empty)})" : string.Empty)}",
                    $"Redirect {edits.Length} material{(edits.Length == 1 ? string.Empty : "s")} in v{folder:D4} to the files this mod already uses for its other material set.",
                    Array.Empty<string>(), edits, preselected: true, confidence: agree ? RepairConfidence.High : RepairConfidence.Medium);
            }

            PlaceholderSetFindings(walk);

            if (walk.Layout!.Structured && model.Slot is { } slot)
                foreach (var row in walk.Rows.Where(row => !AnySubmeshVisible(walk.Layout, slot, row.Entry.AttributeMask)))
                    AddFinding($"V003:{walk.Path}:{row.Variant}", "V003", CheckCategory.Variants, row.Items.Count > 0 ? Severity.Warning : Severity.Info,
                        "Variant hides the whole model",
                        $"{(row.Items.Count > 0 ? string.Join(", ", row.Items) : $"Variant {row.Variant}")}: the IMC attribute mask 0x{row.Entry.AttributeMask:X3} hides every part of {walk.Path}.",
                        walk.Path, walk.Container,
                        "Turn on the model's variant attributes for this variant (an IMC edit in Penumbra), or take them off the meshes.");
        }
    }

    /// <summary>Another material set of the same model whose copy of a material is a file of this mod; the default row's set wins.</summary>
    private (Project.OptionRef Container, string RelPath, bool Agree)? SourceFolder(ModelWalk walk, string name, int folder)
    {
        var provided = walk.Materials.Where(m => m.Name == name && m.MaterialSet != folder && m.Resolved is { Resolution.IsModFile: true })
            .Select(m => (m.MaterialSet, m.Resolved!.Resolution)).ToArray();
        if (provided.Length == 0)
            return null;
        var preferred = walk.Rows.FirstOrDefault()?.Entry.MaterialId ?? 1;
        // A set with only placeholder textures is the last choice: copying it spreads the placeholders.
        var pick = provided.OrderBy(p => IsPlaceholderSet(walk, p.MaterialSet) ? 1 : 0).ThenBy(p => p.MaterialSet == preferred ? 0 : 1).ThenBy(p => p.MaterialSet).First();
        var agree = provided.Select(p => p.Resolution.RelPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        return (pick.Resolution.Option ?? walk.Container.Option, pick.Resolution.RelPath!, agree);
    }

    // ── Placeholder material sets ────────────────────────────────────────────

    /// <summary>A texture no larger than this on either side is a placeholder (TexTools ships 16×16 ones for sets it never got real textures for).</summary>
    private const int PlaceholderSide = 32;

    /// <summary>A material with a texture this large on one side has real textures; smaller ones are often the game's own, copied.</summary>
    private const int DetailedSide = 512;

    /// <summary>A model's set with real textures, the materials that have them there, and the sets whose copies of all those materials are placeholders.</summary>
    private sealed record PlaceholderSets(int Source, IReadOnlyList<string> Names, IReadOnlyList<int> Sets);

    private readonly Dictionary<(ModelWalk Walk, int Set, string Name), int?> _largestTextures = new();
    private readonly Dictionary<ModelWalk, PlaceholderSets?> _placeholderSets = new();

    /// <summary>
    /// Material sets whose materials only use placeholder textures where another set of the same model has real ones: the
    /// items on those sets load, but look wrong. Offers (unticked) to point the set's materials at the detailed set's files.
    /// </summary>
    private void PlaceholderSetFindings(ModelWalk walk)
    {
        if (FindPlaceholderSets(walk) is not { } found)
            return;
        var source = found.Source;
        var names = string.Join(", ", found.Names.Select(name => name.TrimStart('/')));
        foreach (var set in found.Sets)
        {
            var rows = walk.Rows.Where(row => row.Entry.MaterialId == set).ToArray();
            var items = rows.SelectMany(row => row.Items).Distinct().ToArray();
            var who = items.Length > 0 ? string.Join(", ", items) : $"Variant {string.Join(", ", rows.Select(row => row.Variant))}";
            var key = $"V004:{walk.Container.Option.Serialize()}:{walk.Path}:{set}";
            AddFinding(key, "V004", CheckCategory.Variants, Severity.Warning, $"Material set v{set:D4} has placeholder textures",
                $"{who}: in v{set:D4} of {walk.Info!.Directory}, {names} only use{(found.Names.Count == 1 ? "s" : string.Empty)} textures of "
              + $"{PlaceholderSide}×{PlaceholderSide} or smaller, while v{source:D4} has real ones. These items show the placeholders instead of the mod's textures.",
                $"{walk.Info.Directory}/material/v{set:D4}/", walk.Container,
                $"Point this set's materials at the files of v{source:D4}, or give the set real textures.");

            var edits = new List<RepairEdit>();
            foreach (var use in walk.Materials.Where(m => m.MaterialSet == set && m.Resolved is { Resolution.Source: ResolutionSource.ModFile }))
            {
                var from = walk.Materials.FirstOrDefault(m => m.MaterialSet == source && m.Name == use.Name)?.Resolved?.Resolution;
                var current = use.Resolved!.Resolution;
                if (from is not { IsModFile: true } || string.Equals(from.RelPath, current.RelPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                edits.Add(new RetargetFileEdit(current.Option ?? walk.Container.Option, use.Resolved.Path, current.RelPath!, from.RelPath!));
            }
            if (edits.Count > 0)
                AddRepair($"placeholder-set:{walk.Container.Option.Serialize()}:{walk.Path}:{set}", key, CheckCategory.Variants,
                    $"Use the materials of v{source:D4} for v{set:D4}{(items.Length > 0 ? $" ({string.Join(", ", items.Take(2))}{(items.Length > 2 ? " and more" : string.Empty)})" : string.Empty)}",
                    $"Point {edits.Count} material{(edits.Count == 1 ? string.Empty : "s")} in v{set:D4} at the files v{source:D4} uses, so these items look like it.",
                    Array.Empty<string>(), edits, preselected: false, confidence: RepairConfidence.Medium);
        }
    }

    private bool IsPlaceholderSet(ModelWalk walk, int set) => FindPlaceholderSets(walk)?.Sets.Contains(set) == true;

    /// <summary>
    /// The set whose materials have the most real textures (then the largest), and the other sets in which every one of those
    /// materials only has placeholders; null when no set has real textures or none is a placeholder set.
    /// </summary>
    private PlaceholderSets? FindPlaceholderSets(ModelWalk walk)
    {
        if (_placeholderSets.TryGetValue(walk, out var cached))
            return cached;
        var names = walk.Materials.Where(m => m.Resolved is not null).Select(m => m.Name).Distinct(StringComparer.Ordinal).ToArray();
        var detailed = walk.Folders.Select(set => (set, names: names.Where(name => LargestTexture(walk, set, name) >= DetailedSide).ToArray()))
            .Where(p => p.names.Length > 0)
            .OrderByDescending(p => p.names.Length).ThenByDescending(p => p.names.Max(name => LargestTexture(walk, p.set, name))).ThenBy(p => p.set)
            .FirstOrDefault();
        PlaceholderSets? found = null;
        if (detailed.names is { Length: > 0 })
        {
            var sets = walk.Folders.Where(set => set != detailed.set && detailed.names.All(name => LargestTexture(walk, set, name) <= PlaceholderSide)).ToArray();
            if (sets.Length > 0)
                found = new PlaceholderSets(detailed.set, detailed.names, sets);
        }
        return _placeholderSets[walk] = found;
    }

    /// <summary>
    /// The longest side of any texture a material set's copy of a material uses, read from the mod's files; null when the material
    /// or one of its textures is not a readable file of this mod (a game file is real, so it is not judged).
    /// </summary>
    private int? LargestTexture(ModelWalk walk, int set, string name)
    {
        if (_largestTextures.TryGetValue((walk, set, name), out var cached))
            return cached;
        int? largest = null;
        if (walk.Materials.FirstOrDefault(m => m.MaterialSet == set && m.Name == name)?.Resolved is { Resolution: { IsModFile: true } resolution } use
            && _materials.Values.FirstOrDefault(c => c.Path == use.Path && string.Equals(c.Source.RelPath, resolution.RelPath, StringComparison.OrdinalIgnoreCase))
                is { Mtrl: not null, Textures.Count: > 0 } check)
        {
            var sides = check.Textures.Select(texture => texture.Resolution.IsModFile && TexHeader.TryRead(Full(texture.Resolution.RelPath!), out var info)
                ? Math.Max(info.Width, info.Height) : (int?)null).ToArray();
            if (sides.All(side => side is not null))
                largest = sides.Max();
        }
        return _largestTextures[(walk, set, name)] = largest;
    }

    /// <summary>Whether a mask leaves at least one submesh visible: a submesh hides when one of its variant attributes is off.</summary>
    private static bool AnySubmeshVisible(ModelLayout layout, EquipSlot slot, ushort mask)
    {
        var prefix = SlotInfo.VariantAttributePrefix(slot);
        foreach (var mesh in layout.Lod0Meshes)
        for (var s = mesh.SubmeshStart; s < mesh.SubmeshStart + mesh.SubmeshCount && s < layout.SubmeshAttributeMasks.Count; ++s)
        {
            var bits = layout.SubmeshAttributeMasks[s];
            var visible = true;
            for (var a = 0; a < layout.Attributes.Count && a < 32; ++a)
            {
                if ((bits & (1u << a)) == 0)
                    continue;
                var name = layout.Attributes[a];
                if (name.Length != prefix.Length + 1 || !name.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                var letter = name[^1] - 'a';
                if (letter is < 0 or > 9 || (mask & (1 << letter)) != 0)
                    continue;
                visible = false;
                break;
            }
            if (visible)
                return true;
        }
        return layout.Lod0Meshes.Count == 0;
    }

    private VariantTable[] BuildVariantTables()
        => _walks.Where(w => w.Info is { UsesImc: true } && w.Layout is not null).Select(walk => new VariantTable(walk.Path, walk.Container.Option,
            walk.Container.DisplayName, walk.Rows.Select(row =>
            {
                var uses = walk.Materials.Where(m => m.MaterialSet == row.Entry.MaterialId && m.Resolved is not null).ToArray();
                var broken = uses.Count(m => m.Resolved!.Resolution.Source is ResolutionSource.Unresolved or ResolutionSource.Exclusive);
                var conditional = uses.Count(m => m.Resolved!.Resolution.Source is ResolutionSource.Conditional);
                var hidden = walk.Layout!.Structured && walk.Info!.Slot is { } slot && !AnySubmeshVisible(walk.Layout, slot, row.Entry.AttributeMask);
                var placeholder = broken == 0 && IsPlaceholderSet(walk, row.Entry.MaterialId);
                Severity? status = broken > 0 ? Severity.Error : hidden || conditional > 0 || placeholder ? Severity.Warning : null;
                var summary = broken > 0 ? $"{broken} material{(broken == 1 ? string.Empty : "s")} missing"
                    : hidden ? "The attribute mask hides every part"
                    : conditional > 0 ? $"{conditional} material{(conditional == 1 ? string.Empty : "s")} depend on another option"
                    : placeholder ? "Placeholder textures"
                    : "OK";
                return new VariantRow(row.Variant, row.Items, row.Entry.MaterialId, row.Entry.AttributeMask, status, summary);
            }).ToArray(), walk.RowNote)).ToArray();
}
