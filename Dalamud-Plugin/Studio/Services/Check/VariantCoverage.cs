using System;
using System.Linq;
using InstantEdit.Studio.Models;

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
        var pick = provided.OrderBy(p => p.MaterialSet == preferred ? 0 : 1).ThenBy(p => p.MaterialSet).First();
        var agree = provided.Select(p => p.Resolution.RelPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        return (pick.Resolution.Option ?? walk.Container.Option, pick.Resolution.RelPath!, agree);
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
                Severity? status = broken > 0 ? Severity.Error : hidden || conditional > 0 ? Severity.Warning : null;
                var summary = broken > 0 ? $"{broken} material{(broken == 1 ? string.Empty : "s")} missing"
                    : hidden ? "The attribute mask hides every part"
                    : conditional > 0 ? $"{conditional} material{(conditional == 1 ? string.Empty : "s")} depend on another option"
                    : "OK";
                return new VariantRow(row.Variant, row.Items, row.Entry.MaterialId, row.Entry.AttributeMask, status, summary);
            }).ToArray(), walk.RowNote)).ToArray();
}
