using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;
using Newtonsoft.Json;

namespace InstantEdit.Studio.Services.Check;

/// <summary>What no chain reaches: redirections nothing asks for, metadata for items the mod doesn't change, and files no option uses.</summary>
internal sealed partial class ModCheck
{
    private static readonly HashSet<string> GameAssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mdl", ".mtrl", ".tex", ".atex", ".avfx", ".pap", ".tmb", ".sklb", ".skp", ".phyb", ".eid", ".shpk", ".scd", ".imc", ".eqdp",
    };

    private readonly HashSet<GameRoot> _shippedRoots = new();
    private readonly HashSet<GameRoot> _approximateRoots = new();
    private readonly HashSet<GameRoot> _ownRoots = new();
    private readonly List<Orphan> _orphans = new();

    private sealed record Orphan(string RelPath, long Size, DateTime LastWriteUtc, bool GameAsset, GameRoot? Root);

    /// <summary>The items whose models the mod ships, as far as the check could follow them.</summary>
    private void CollectShippedRoots()
    {
        foreach (var walk in _walks)
        {
            var root = ResourceNaming.RootOf(walk.Path);
            if (walk.Info is not null && walk.Layout is not null && !walk.Approximate) _shippedRoots.Add(root);
            else _approximateRoots.Add(root);
        }
        _shippedRoots.ExceptWith(_approximateRoots);
    }

    /// <summary>Materials first, so the ones no model asks for are still checked on their own.</summary>
    private void ClassifyMaterialsAndValidateStandalone()
    {
        foreach (var mapping in _model.Mappings.Where(m => !m.Shadowed && m.Key.EndsWith(".mtrl", StringComparison.Ordinal)))
        {
            mapping.Class = Classify(mapping);
            if (mapping.Class is MappingClass.VanillaReplacement or MappingClass.NotAnalyzed)
                ValidateMaterial(mapping.Container, mapping.Key,
                    new PathResolution(ResolutionSource.ModFile, mapping.Key, mapping.RelPath, mapping.Container.Option));
        }
    }

    private void ClassifyRemaining()
    {
        foreach (var mapping in _model.Mappings.Where(m => !m.Key.EndsWith(".mtrl", StringComparison.Ordinal) || m.Shadowed))
            mapping.Class = mapping.Shadowed ? MappingClass.NotAnalyzed : Classify(mapping);

        foreach (var mapping in _model.Mappings.Where(m => m.Class is MappingClass.Used or MappingClass.VanillaReplacement or MappingClass.NotAnalyzed))
            if (ResourceNaming.HasValidRoot(mapping.Key))
                _ownRoots.Add(mapping.Root);
        _ownRoots.UnionWith(_shippedRoots);
        _ownRoots.UnionWith(_approximateRoots);

        // A texture of an item this mod doesn't otherwise touch is foreign rather than a dead part of its own item.
        foreach (var mapping in _model.Mappings.Where(m => m.Class == MappingClass.Foreign && _ownRoots.Contains(m.Root)))
            mapping.Class = MappingClass.DeadOwn;
    }

    private MappingClass Classify(CheckMapping mapping)
    {
        var key = mapping.Key;
        if (!ResourceNaming.HasValidRoot(key)) return MappingClass.JunkKey;
        if (_requested.Contains(key)) return MappingClass.Used;
        if (_game.Exists(key)) return MappingClass.VanillaReplacement;
        var extension = Path.GetExtension(key);
        var root = mapping.Root;
        if (extension is not (".mtrl" or ".tex") || root.IsShared || _approximateRoots.Contains(root)) return MappingClass.NotAnalyzed;
        if (_shippedRoots.Contains(root)) return MappingClass.DeadOwn;
        // Materials of items this mod has no model for may belong to another mod's model (recolour packs, for example).
        if (extension == ".mtrl" || _shippedRoots.Count == 0) return MappingClass.NotAnalyzed;
        return MappingClass.Foreign;
    }

    private void FindOrphans()
    {
        var referenced = new HashSet<string>(_model.Mappings.Select(m => m.RelPath), StringComparer.OrdinalIgnoreCase);
        referenced.UnionWith(_model.SkippedMappings.Select(s => s.RelPath));
        referenced.UnionWith(_model.AuxiliaryPaths);
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_input.Folder, "*", SearchOption.AllDirectories).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notes.Add($"The mod's folder could not be listed ({ex.Message}), so files nothing uses were not looked for.");
            return;
        }
        foreach (var file in files)
        {
            _token.ThrowIfCancellationRequested();
            var relPath = Path.GetRelativePath(_input.Folder, file).Replace('\\', '/');
            var name = Path.GetFileName(relPath);
            var extension = Path.GetExtension(relPath);
            if (referenced.Contains(relPath)
             || (!relPath.Contains('/') && extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
             || IsHousekeeping(relPath)
             || _input.LedgerRelKeys.Contains(PathNormalizer.RelKey(relPath))
             || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
             || name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase))
                continue;
            FileInfo info;
            try { info = new FileInfo(file); }
            catch (IOException) { continue; }
            var gamePath = ResourceNaming.HasValidRoot(relPath) ? relPath : ResourceNaming.IsMangledKey(relPath, out var real) ? real : null;
            _orphans.Add(new Orphan(relPath, info.Length, info.LastWriteTimeUtc, GameAssetExtensions.Contains(extension),
                gamePath is null ? null : ResourceNaming.RootOf(gamePath)));
        }
    }

    private void LeftoverFindings()
    {
        var referencedFiles = _model.Mappings.GroupBy(m => m.RelPath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        long FreedBy(CheckMapping mapping)
            => referencedFiles.GetValueOrDefault(mapping.RelPath) <= 1 && !_model.AuxiliaryPaths.Contains(mapping.RelPath)
                ? State(mapping.RelPath) is { Exists: true } state ? state.Size : 0
                : 0;

        foreach (var mapping in _model.Mappings.Where(m => m.Class is MappingClass.DeadOwn or MappingClass.Foreign))
        {
            var foreign = mapping.Class == MappingClass.Foreign;
            var superseding = _supersededBy.GetValueOrDefault(mapping);
            var code = superseding is not null ? "L001" : foreign ? "L003" : "L002";
            var key = $"{code}:{mapping.Container.Option.Serialize()}:{mapping.Key}";
            var cluster = foreign ? $"From another mod: {mapping.Root.Label}" : $"Unused parts of {mapping.Root.Label}";
            AddFinding(key, code, CheckCategory.Leftovers, Severity.Info,
                superseding is not null ? "Redirection under a wrong name" : foreign ? "Redirection for an item this mod doesn't change" : "Redirection nothing asks for",
                superseding is not null
                    ? $"{mapping.Key} is never asked for; its file is what {ShortName(superseding[(superseding.LastIndexOf(':') + 1)..])} should be. It is removed once that is redirected properly."
                    : foreign
                        ? $"{mapping.Key} goes to {mapping.RelPath}: no material in this mod uses it, and it is not a game file. It looks like a leftover from another mod, and would replace that mod's file if this mod had priority."
                        : $"{mapping.Key} goes to {mapping.RelPath}: none of this mod's models or materials ask for it, and it is not a game file.",
                mapping.Key, mapping.Container, cluster: cluster);
            AddRepair($"remove:{mapping.Container.Option.Serialize()}:{mapping.Key}", key, CheckCategory.Leftovers, $"Remove {mapping.Key}",
                "Remove the redirection. Penumbra never uses it.", superseding is null ? Array.Empty<string>() : new[] { superseding },
                new RepairEdit[] { new RemoveFileEdit(mapping.Container.Option, mapping.Key, mapping.RelPath) }, preselected: true, cluster: cluster, bytesFreed: FreedBy(mapping));
        }

        foreach (var mapping in _model.Mappings.Where(m => m.Class == MappingClass.JunkKey))
        {
            var suffix = ResourceNaming.IsMangledKey(mapping.Key, out var real) ? real : null;
            var mappedElsewhere = suffix is not null && _providers.ContainsKey(suffix);
            var key = $"S005:{mapping.Container.Option.Serialize()}:{mapping.Key}";
            const string cluster = "Game paths that aren't ones";
            AddFinding(key, "S005", CheckCategory.Leftovers, Severity.Info, "Redirection with a game path that isn't one",
                $"{mapping.Key} doesn't start with a game folder, so the game never asks for it."
              + (suffix is null ? string.Empty : mappedElsewhere ? $" The real path {suffix} is already redirected elsewhere in this mod." : $" It looks like {suffix} with folder names in front."),
                mapping.Key, mapping.Container, cluster: cluster);
            var remove = new RemoveFileEdit(mapping.Container.Option, mapping.Key, mapping.RelPath);
            if (mappedElsewhere || suffix is null)
            {
                AddRepair($"remove:{mapping.Container.Option.Serialize()}:{mapping.Key}", key, CheckCategory.Leftovers, $"Remove {mapping.Key}",
                    "Remove the redirection; the game can never ask for it.", Array.Empty<string>(), new RepairEdit[] { remove },
                    preselected: mappedElsewhere, cluster: cluster, bytesFreed: FreedBy(mapping));
            }
            else
            {
                var exclusive = $"junk:{mapping.Container.Option.Serialize()}:{mapping.Key}";
                AddRepair($"rekey:{mapping.Container.Option.Serialize()}:{mapping.Key}", key, CheckCategory.Leftovers, $"Change it to {suffix}",
                    "Keep the redirection under the game path it holds. This makes it active.", Array.Empty<string>(),
                    new RepairEdit[] { new RekeyFileEdit(mapping.Container.Option, mapping.Key, suffix!) }, forceOptIn: true, exclusiveGroup: exclusive, cluster: cluster);
                AddRepair($"remove:{mapping.Container.Option.Serialize()}:{mapping.Key}", key, CheckCategory.Leftovers, $"Remove {mapping.Key}",
                    "Remove the redirection; the game can never ask for it.", Array.Empty<string>(), new RepairEdit[] { remove },
                    forceOptIn: true, exclusiveGroup: exclusive, cluster: cluster, bytesFreed: FreedBy(mapping));
            }
        }

        var deadRoots = _model.Mappings.Where(m => m.Class is MappingClass.Foreign or MappingClass.DeadOwn).Select(m => m.Root)
            .Concat(_orphans.Where(o => o.Root is not null).Select(o => o.Root!.Value)).ToHashSet();
        foreach (var manipulation in _model.Manipulations.Where(m => m.Parsed.Root is { } root && !_ownRoots.Contains(root)))
        {
            var root = manipulation.Parsed.Root!.Value;
            var evidence = deadRoots.Contains(root);
            var cluster = $"From another mod: {root.Label}";
            var key = $"L004:{manipulation.Container.Option.Serialize()}:{manipulation.Parsed.Identity}";
            AddFinding(key, "L004", CheckCategory.Leftovers, Severity.Info, "Metadata for an item this mod doesn't change",
                $"{manipulation.Parsed.Description} changes {root.Label}, which this mod has no files for.{(evidence ? " The mod also holds unused files of that item, so this is most likely left over from another mod." : string.Empty)}",
                container: manipulation.Container, cluster: cluster);
            AddRepair($"meta:{manipulation.Container.Option.Serialize()}:{manipulation.Parsed.Identity}", key, CheckCategory.Leftovers, $"Remove {manipulation.Parsed.Description}",
                "Remove the manipulation from this mod.", Array.Empty<string>(),
                new RepairEdit[] { new RemoveManipulationEdit(manipulation.Container.Option, manipulation.Parsed.Identity, manipulation.Json.ToString(Formatting.None), manipulation.Parsed.Description) },
                preselected: evidence, forceOptIn: !evidence, cluster: cluster);
        }

        var clean = _model.Issues.Count == 0;
        foreach (var orphan in _orphans)
        {
            var cluster = orphan.Root is { } root && !_ownRoots.Contains(root) ? $"From another mod: {root.Label}" : "Unused files";
            var key = $"L005:{orphan.RelPath}";
            AddFinding(key, "L005", CheckCategory.Leftovers, Severity.Info, "File nothing uses",
                $"{orphan.RelPath} ({FormatSize(orphan.Size)}) is not redirected by any option.", cluster: cluster);
            AddRepair($"delete:{orphan.RelPath}", key, CheckCategory.Leftovers, $"Delete {orphan.RelPath}",
                "Delete the file. Penumbra never loads it.", Array.Empty<string>(), Array.Empty<RepairEdit>(),
                preselected: orphan.GameAsset && clean, forceOptIn: !orphan.GameAsset || !clean,
                deletions: new[] { new FileDeletion(orphan.RelPath, orphan.Size, orphan.LastWriteUtc) }, cluster: cluster, bytesFreed: orphan.Size);
        }
    }

    private static string FormatSize(long bytes)
        => bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.#} MiB" : bytes >= 1 << 10 ? $"{bytes / 1024.0:0.#} KiB" : $"{bytes} B";
}
