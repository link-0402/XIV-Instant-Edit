using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;

namespace InstantEdit.Studio.Services.Check;

/// <summary>The report: findings, and the repairs offered for them. Only unambiguous repairs start ticked.</summary>
internal sealed partial class ModCheck
{
    private readonly List<CheckFinding> _findings = new();
    private readonly List<Repair> _repairs = new();
    private readonly HashSet<Request> _coveredByVariantFix = new();
    private readonly Dictionary<CheckMapping, string> _supersededBy = new(ReferenceEqualityComparer.Instance);
    private List<string>? _allFiles;
    private HashSet<string>? _allFileSet;

    private static readonly string[][] SuffixSynonyms =
    {
        new[] { "n", "norm", "normal" },
        new[] { "d", "diff", "diffuse", "base" },
        new[] { "m", "mask", "multi" },
        new[] { "s", "spec", "specular" },
        new[] { "id", "index" },
    };

    private ModCheckReport BuildReport(MetaFingerprint? fingerprint)
    {
        StructureFindings();
        FileFindings();
        ModelFindings();
        VariantFindings();
        RequestFindings();
        MaterialProblemFindings();
        RaceFindings();
        LiveFindings();
        LeftoverFindings();

        foreach (var repair in _repairs)
            _findings.FirstOrDefault(finding => finding.Key == repair.FindingKey)?.RepairIds.Add(repair.Id);

        var findings = _findings.OrderByDescending(finding => finding.Severity).ThenBy(finding => finding.Category).ToArray();
        return new ModCheckReport(_input.Folder, fingerprint, DateTime.UtcNow, findings, _repairs, BuildVariantTables(),
            RaceTableViews(), _input.Collection?.Context, _input.PlayerRace, _notes);
    }

    // ── Structure and files ──────────────────────────────────────────────────

    private void StructureFindings()
    {
        foreach (var issue in _model.Issues)
        {
            var (code, title) = issue.Kind switch
            {
                MetaIssueKind.NonStringMapping => ("S002", "Redirection doesn't point at a file"),
                MetaIssueKind.EscapingPath     => ("S003", "Redirection points outside the mod folder"),
                MetaIssueKind.InvalidGamePath  => ("S004", "Redirection has a game path that isn't one"),
                MetaIssueKind.BadGroup         => ("S007", "Option group is broken"),
                _                              => ("S008", "Manipulation is broken"),
            };
            AddFinding($"{code}:{issue.Location}", code, CheckCategory.Structure, Severity.Error, title,
                $"{issue.Message} ({PenumbraModMeta.FileName}{(issue.Location.Length > 0 ? $", {issue.Location}" : string.Empty)})",
                issue.GamePath, manual: "Penumbra skips this entry. Fix or remove it in Penumbra's Edit Mod tab.");
        }
    }

    private void FileFindings()
    {
        foreach (var group in _model.Mappings.Where(m => !m.Shadowed && ResourceNaming.HasValidRoot(m.Key)).GroupBy(m => m.RelPath, StringComparer.OrdinalIgnoreCase))
        {
            var state = State(group.Key);
            var paths = string.Join(", ", group.Select(m => m.Key).Distinct().Take(4));
            if (!state.Exists)
            {
                foreach (var mapping in group)
                {
                    var key = $"S006:{mapping.Container.Option.Serialize()}:{mapping.Key}";
                    AddFinding(key, "S006", CheckCategory.Structure, Severity.Error, "Redirected file is missing",
                        $"{mapping.Key} points at {mapping.RelPath}, which doesn't exist{(state.Error is null ? "." : $" ({state.Error}).")} The game fails to load it.",
                        mapping.Key, mapping.Container);
                    var sameName = SameNameFiles(mapping.RelPath);
                    var candidates = sameName
                        .Select(file => new RepairCandidate(file, sameName.Count == 1 ? RepairConfidence.High : RepairConfidence.Low, "A file with the same name elsewhere in the mod",
                            new RepairEdit[] { new RetargetFileEdit(mapping.Container.Option, mapping.Key, mapping.RelPath, file) }))
                        .ToList();
                    if (_game.Exists(mapping.Key))
                        candidates.Add(new RepairCandidate("Remove the redirection (use the game's file)", RepairConfidence.Medium, "The path exists in the game",
                            new RepairEdit[] { new RemoveFileEdit(mapping.Container.Option, mapping.Key, mapping.RelPath) }));
                    if (candidates.Count > 0)
                        AddRepair($"retarget:{mapping.Container.Option.Serialize()}:{mapping.Key}", key, CheckCategory.Structure, $"Fix the missing file for {ShortName(mapping.Key)}",
                            "Point the redirection at a file that exists.", candidates);
                }
                continue;
            }

            var mismatched = group.Where(m => FileSniffer.ExpectedKind(m.Key) is { } expected && expected != state.Kind).ToArray();
            if (mismatched.Length == 0)
                continue;
            var wanted = FileSniffer.ExpectedKind(mismatched[0].Key)!.Value;
            AddFinding($"F001:{group.Key}", "F001", CheckCategory.Structure, Severity.Error, $"File is not {FileSniffer.Describe(wanted)}",
                $"{group.Key} is {FileSniffer.Describe(state.Kind)}, but it is used as {paths}. The game can't load it.",
                mismatched[0].Key, manual: wanted == FileKind.Tex
                    ? "Turn the image into a .tex file, with Penumbra's texture import in Advanced Editing for example."
                    : "Replace the file with a valid one exported from Penumbra, TexTools or a model editor.");
        }
    }

    private CheckFinding AddFinding(string key, string code, CheckCategory category, Severity severity, string title, string detail,
        string? gamePath = null, CheckContainer? container = null, string? manual = null, string? cluster = null)
    {
        var existing = _findings.FirstOrDefault(finding => finding.Key == key);
        if (existing is not null)
            return existing;
        var finding = new CheckFinding(key, code, category, severity, title, detail, gamePath, container?.Option, container?.DisplayName, manual, cluster);
        _findings.Add(finding);
        return finding;
    }

    /// <summary>A repair choosing between candidates: ticked only when exactly one candidate is certain and it comes first.</summary>
    private void AddRepair(string id, string findingKey, CheckCategory category, string title, string explanation,
        IReadOnlyList<RepairCandidate> candidates, bool forceOptIn = false)
    {
        var high = candidates.Where(c => c.Confidence == RepairConfidence.High).Select(c => c.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var preselected = !forceOptIn && high == 1 && candidates[0].Confidence == RepairConfidence.High;
        _repairs.Add(new Repair(id, findingKey, category, title, explanation, candidates[0].Confidence, preselected, candidates[0].Edits,
            Array.Empty<FileDeletion>(), Array.Empty<string>(), Candidates: candidates));
    }

    private void AddRepair(string id, string findingKey, CheckCategory category, string title, string explanation, IReadOnlyList<string> dependsOn,
        IReadOnlyList<RepairEdit> edits, bool preselected = false, bool forceOptIn = false, RepairConfidence confidence = RepairConfidence.High,
        string? exclusiveGroup = null, string? cluster = null, long bytesFreed = 0, IReadOnlyList<FileDeletion>? deletions = null)
    {
        if (_repairs.Any(repair => repair.Id == id))
            return;
        _repairs.Add(new Repair(id, findingKey, category, title, explanation, confidence, preselected && !forceOptIn, edits,
            deletions ?? Array.Empty<FileDeletion>(), dependsOn, exclusiveGroup, null, bytesFreed, cluster));
    }

    // ── Requests: materials, textures, shaders ───────────────────────────────

    private void RequestFindings()
    {
        foreach (var group in _requests.Where(r => !_coveredByVariantFix.Contains(r)).GroupBy(r => (r.Kind, r.Path, r.Target.Option)))
        {
            var request = group.First();
            var resolution = request.Resolution;
            var what = request.Kind switch { RequestKind.Material => "Material", RequestKind.Texture => "Texture", _ => "Shader" };
            var requesters = string.Join("; ", group.Select(r => r.Requester).Distinct().Take(3));
            var key = $"{request.Kind}:{request.Target.Option.Serialize()}:{request.Path}";
            var providers = resolution.Providers ?? Array.Empty<string>();
            switch (resolution.Source)
            {
                case ResolutionSource.Unresolved or ResolutionSource.Exclusive when request.Kind == RequestKind.Shader:
                    AddFinding(key, "C005", CheckCategory.Materials, Severity.Error, "Material uses a shader that doesn't exist",
                        $"{requesters} uses {request.Path}, which is neither a game shader nor part of this mod. The material fails to load.",
                        request.Path, request.Root, "Pick a shader package that exists in Penumbra's material editor.");
                    break;
                case ResolutionSource.Unresolved or ResolutionSource.Exclusive:
                {
                    var exclusive = resolution.Source == ResolutionSource.Exclusive;
                    AddFinding(key, request.Kind == RequestKind.Material ? "C001" : "C002", CheckCategory.Materials, Severity.Error,
                        exclusive ? $"{what} only exists in an option that can't be on" : $"{what} is missing",
                        exclusive
                            ? $"{requesters} needs {request.Path}, but only {string.Join(", ", providers)} provide{(providers.Count == 1 ? "s" : string.Empty)} it, and those options are never active together with {request.Root.DisplayName}."
                            : $"{requesters} needs {request.Path}, which is {resolution.Detail}{(_input.Collection is null ? string.Empty : " and no mod in your collection provides it")}. The {(request.Kind == RequestKind.Material ? "model" : "material")} fails to load, so the item is invisible.",
                        request.Path, request.Root,
                        request.Kind == RequestKind.Texture
                            ? "Add the texture to the mod, or change the path in Penumbra's material editor."
                            : "Add the material to the mod, or change the material name in the model.");
                    var candidates = Candidates(request);
                    if (candidates.Count > 0)
                        AddRepair($"fix:{request.Target.Option.Serialize()}:{request.Path}", key, CheckCategory.Materials, $"Provide {ShortName(request.Path)}",
                            $"Redirect {request.Path} in {request.Target.DisplayName}.", candidates);
                    break;
                }
                case ResolutionSource.Conditional:
                {
                    AddFinding(key, "C006", CheckCategory.Materials, Severity.Warning, $"{what} depends on another option",
                        $"{requesters} needs {request.Path}, which only {string.Join(", ", providers)} provide{(providers.Count == 1 ? "s" : string.Empty)}. Without {(providers.Count == 1 ? "that option" : "one of those options")} the item is invisible.",
                        request.Path, request.Root);
                    var copies = Candidates(request).Select(c => c with { Confidence = RepairConfidence.Medium }).ToList();
                    if (copies.Count > 0)
                        AddRepair($"fix:{request.Target.Option.Serialize()}:{request.Path}", key, CheckCategory.Materials,
                            $"Also provide {ShortName(request.Path)} in {request.Target.DisplayName}",
                            "Copy the redirection so it no longer depends on the other option.", copies, forceOptIn: true);
                    break;
                }
                case ResolutionSource.External:
                    AddFinding(key, "X002", CheckCategory.Dependencies, Severity.Info, $"{what} comes from another mod",
                        $"{request.Path} is not part of this mod, but {resolution.Detail} provides it in your collection. Others need that mod too.",
                        request.Path, request.Root);
                    break;
            }
        }
    }

    /// <summary>Files that could serve a missing request, best first.</summary>
    private List<RepairCandidate> Candidates(Request request)
    {
        var path = request.Path;
        var found = new List<(string RelPath, RepairConfidence Confidence, string Reason, CheckMapping? Supersedes)>();

        if (_providers.TryGetValue(path, out var providers))
        {
            var files = providers.Select(p => p.RelPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var provider in providers.DistinctBy(p => p.RelPath, StringComparer.OrdinalIgnoreCase))
                found.Add((provider.RelPath, files.Length == 1 ? RepairConfidence.High : RepairConfidence.Medium, $"The file {provider.Container.DisplayName} uses", null));
        }

        var twin = ResourceNaming.Dx11Twin(path);
        if (Resolve(twin, request.Root) is { IsModFile: true } twinResolution)
            found.Add((twinResolution.RelPath!, RepairConfidence.High, $"Redirected as {ShortName(twin)}, which differs only by the DX11 '--' prefix", null));

        if (SynonymKey(path) is { } synonym)
            foreach (var container in Active(request.Root))
            foreach (var mapping in _files[container].Values.Where(m => m.Class is MappingClass.DeadOwn or MappingClass.Foreign && SynonymKey(m.Key) == synonym))
                found.Add((mapping.RelPath, RepairConfidence.High, $"Redirected under the wrong name {ShortName(mapping.Key)}", mapping));

        foreach (var prefix in Prefixes(request.Target))
            if (FileAt(prefix + path) is { } exact)
                found.Add((exact, RepairConfidence.High, "The file already sits at the requested path", null));

        var sameName = SameNameFiles(path);
        foreach (var file in sameName)
            found.Add((file, sameName.Count == 1 ? RepairConfidence.Medium : RepairConfidence.Low, "A file with the same name", null));

        var extension = Path.GetExtension(path);
        foreach (var file in AllFiles().Where(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase)).Take(60))
            found.Add((file, RepairConfidence.Low, "Pick it by hand", null));

        var merged = found.GroupBy(c => c.RelPath, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var best = g.Min(c => c.Confidence);
                return (RelPath: g.Key, Confidence: best,
                    Reason: string.Join("; ", g.Where(c => c.Confidence == best).Select(c => c.Reason).Distinct()),
                    Supersedes: g.Select(c => c.Supersedes).FirstOrDefault(s => s is not null));
            })
            .OrderBy(c => c.Confidence).ThenBy(c => c.RelPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new List<RepairCandidate>();
        foreach (var candidate in merged)
        {
            result.Add(new RepairCandidate(candidate.RelPath, candidate.Confidence, candidate.Reason,
                new RepairEdit[] { new AddFileEdit(request.Target.Option, path, candidate.RelPath) }));
            if (candidate.Supersedes is { } dead && candidate.Confidence == RepairConfidence.High
             && request.Resolution.Source is ResolutionSource.Unresolved or ResolutionSource.Exclusive)
                _supersededBy.TryAdd(dead, $"fix:{request.Target.Option.Serialize()}:{path}");
        }
        return result;
    }

    /// <summary>Folder, stem and canonical texture suffix, so "x_norm.tex" and "x_normal.tex" compare equal.</summary>
    private static string? SynonymKey(string path)
    {
        var slash = path.LastIndexOf('/');
        var name = Path.GetFileNameWithoutExtension(path[(slash + 1)..]).TrimStart('-');
        var underscore = name.LastIndexOf('_');
        if (underscore <= 0)
            return null;
        var suffix = name[(underscore + 1)..];
        var group = Array.FindIndex(SuffixSynonyms, names => names.Contains(suffix, StringComparer.OrdinalIgnoreCase));
        return group < 0 ? null : $"{path[..(slash + 1)]}{name[..underscore]}_#{group}{Path.GetExtension(path)}";
    }

    /// <summary>Folder prefixes the container keeps its files under, such as "Group/Option/" in tidied mods.</summary>
    private IEnumerable<string> Prefixes(CheckContainer container)
    {
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.Empty };
        foreach (var mapping in _files[container].Values)
            if (mapping.RelPath.Length > mapping.Key.Length && mapping.RelPath.EndsWith(mapping.Key, StringComparison.OrdinalIgnoreCase))
                prefixes.Add(mapping.RelPath[..^mapping.Key.Length]);
        return prefixes;
    }

    /// <summary>Every file inside the mod's folders (not at its top), with forward slashes; this plugin's and backup files left out.</summary>
    private List<string> AllFiles()
    {
        if (_allFiles is not null)
            return _allFiles;
        try
        {
            _allFiles = Directory.EnumerateFiles(_input.Folder, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(_input.Folder, file).Replace('\\', '/'))
                .Where(file => file.Contains('/') && !IsHousekeeping(file))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _allFiles = new List<string>();
        }
        _allFileSet = new HashSet<string>(_allFiles, StringComparer.OrdinalIgnoreCase);
        return _allFiles;
    }

    /// <summary>Files this plugin or Penumbra keep for themselves: temporary files and backups.</summary>
    private static bool IsHousekeeping(string relPath)
        => relPath.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
        || relPath.EndsWith(".instant-edit-tmp", StringComparison.OrdinalIgnoreCase)
        || relPath.EndsWith(PenumbraModMeta.BackupSuffix, StringComparison.OrdinalIgnoreCase)
        || relPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    private string? FileAt(string relPath)
    {
        AllFiles();
        return _allFileSet!.TryGetValue(relPath, out var actual) ? actual : null;
    }

    private List<string> SameNameFiles(string path)
    {
        var name = Path.GetFileName(path.Replace('\\', '/'));
        var own = path.Replace('\\', '/');
        return AllFiles().Where(file => Path.GetFileName(file).Equals(name, StringComparison.OrdinalIgnoreCase)
                                     && !file.Equals(own, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
