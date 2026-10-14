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
            if (_findings.FirstOrDefault(finding => finding.Key == repair.FindingKey) is { } owner && !owner.RepairIds.Contains(repair.Id))
                owner.RepairIds.Add(repair.Id);

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
        // One unit per asking option and model option, judged by its worst request: a model's option can redirect what its
        // material asks for, so the same material can be fine with one model and broken with another.
        var units = _requests.Where(r => !_coveredByVariantFix.Contains(r))
            .GroupBy(r => (r.Kind, r.Path, Target: r.Target.Option, Via: r.Via?.Option))
            .Select(group => new RequestUnit(group.MinBy(r => Badness(r.Resolution.Source))!, group.ToArray()))
            .ToList();
        var byTarget = units.ToLookup(unit => (unit.Worst.Kind, unit.Worst.Path, unit.Worst.Target.Option));

        // Units that fare alike and ask from the same file share a finding: options shipping one material, or one material under several models.
        foreach (var group in units.Where(unit => Badness(unit.Worst.Resolution.Source) < 3)
                     .GroupBy(unit => (unit.Worst.Kind, unit.Worst.Path, From: unit.Worst.From?.ToLowerInvariant(), Signature(unit.Worst.Resolution))))
        {
            var members = group.ToArray();
            var request = members[0].Worst;
            var resolution = request.Resolution;
            var what = request.Kind switch { RequestKind.Material => "Material", RequestKind.Texture => "Texture", _ => "Shader" };
            // Name the model's option only when the same material fares differently with another model.
            var split = members.Any(member => byTarget[(member.Worst.Kind, member.Worst.Path, member.Worst.Target.Option)].Any(unit => !members.Contains(unit)));
            var vias = members.Select(member => member.Worst.Via).OfType<CheckContainer>().Select(via => via.DisplayName).Distinct().ToArray();
            var requesters = Requesters(members.SelectMany(member => member.All))
                + (split && vias.Length > 0 ? $", with the model from {string.Join(", ", vias)}," : string.Empty);
            var key = split
                ? $"{request.Kind}:{request.Target.Option.Serialize()}:{request.Via?.Option.Serialize() ?? "-"}:{request.Path}"
                : $"{request.Kind}:{request.Target.Option.Serialize()}:{request.Path}";
            var askers = members.Select(member => member.Worst).DistinctBy(r => r.Target.Option).ToArray();
            var roots = string.Join(" or ", members.Select(member => member.Worst.Root.DisplayName).Distinct());
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
                    var details = members.Select(member => member.Worst.Resolution.Detail).Distinct().ToArray();
                    var detail = details.Length == 1
                        ? details[0]
                        : $"not redirected by {string.Join(" or ", members.SelectMany(m => Active(m.Worst.Root, m.Worst.Via)).Distinct().Select(c => c.DisplayName))}, and not a game file";
                    var finding = AddFinding(key, request.Kind == RequestKind.Material ? "C001" : "C002", CheckCategory.Materials, Severity.Error,
                        exclusive ? $"{what} only exists in an option that can't be on" : $"{what} is missing",
                        exclusive
                            ? $"{requesters} needs {request.Path}, but only {string.Join(", ", providers)} provide{(providers.Count == 1 ? "s" : string.Empty)} it, and those options are never active together with {roots}."
                            : $"{requesters} needs {request.Path}, which is {detail}{(_input.Collection is null ? string.Empty : " and no mod in your collection provides it")}. The {(request.Kind == RequestKind.Material ? "model" : "material")} fails to load, so the item is invisible.",
                        request.Path, request.Root,
                        request.Kind == RequestKind.Texture
                            ? "Add the texture to the mod, or change the path in Penumbra's material editor."
                            : "Add the material to the mod, or change the material name in the model.");
                    foreach (var asker in askers)
                    {
                        var into = Into(asker);
                        OfferRepair(finding, $"fix:{into.Option.Serialize()}:{asker.Path}",
                            askers.Length > 1 || !ReferenceEquals(into, asker.Target) ? $"Provide {ShortName(asker.Path)} in {into.DisplayName}" : $"Provide {ShortName(asker.Path)}",
                            $"Redirect {asker.Path} in {into.DisplayName}.", Candidates(asker, into));
                    }
                    break;
                }
                case ResolutionSource.Conditional:
                {
                    var asking = request.Kind == RequestKind.Material ? "model" : "material";
                    var (gapGroup, missing) = GroupGap(OtherProviders(request));
                    var finding = AddFinding(key, "C006", CheckCategory.Materials, Severity.Warning, $"{what} depends on another option",
                        missing is null
                            ? $"{requesters} needs {request.Path}, which only {string.Join(", ", providers)} provide{(providers.Count == 1 ? "s" : string.Empty)}. Without {(providers.Count == 1 ? "that option" : "one of those options")} the {asking} fails to load and the item is invisible."
                            : $"{requesters} needs {request.Path}, which every {gapGroup} option provides except {string.Join(", ", missing.Select(m => $"\"{m}\""))}. With {(missing.Count == 1 ? "that one" : "one of those")} picked the {asking} fails to load and the item is invisible.",
                        request.Path, request.Root);
                    foreach (var asker in askers)
                    {
                        var into = Into(asker);
                        var shown = asker.Resolution.Providers ?? Array.Empty<string>();
                        var (askerGap, askerMissing) = GroupGap(OtherProviders(asker));
                        OfferRepair(finding, $"fix:{into.Option.Serialize()}:{asker.Path}",
                            $"Also provide {ShortName(asker.Path)} in {into.DisplayName}",
                            "Copy the redirection so it no longer depends on the other option. "
                            + (askerMissing is null
                                ? $"{string.Join(", ", shown)} still win{(shown.Count == 1 ? "s" : string.Empty)} when on."
                                : $"The {askerGap} options that provide it still win when picked."),
                            Candidates(asker, into).Select(c => c with { Confidence = RepairConfidence.Medium }).ToList(), forceOptIn: true);
                    }
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

    /// <summary>The requests of one asking option and model option, and the one that fares worst.</summary>
    private sealed record RequestUnit(Request Worst, Request[] All);

    private static int Badness(ResolutionSource source) => source switch
    {
        ResolutionSource.Unresolved or ResolutionSource.Exclusive => 0,
        ResolutionSource.Conditional => 1,
        ResolutionSource.External => 2,
        _ => 3,
    };

    /// <summary>What makes two units' findings the same: how the path resolves, and who provides it.</summary>
    private static string Signature(PathResolution resolution) => resolution.Source switch
    {
        ResolutionSource.Unresolved => "unresolved",
        ResolutionSource.External => $"external|{resolution.Detail}",
        _ => $"{resolution.Source}|{string.Join("|", resolution.Providers ?? Array.Empty<string>())}",
    };

    /// <summary>Who asks, with the options that ship the same material named together: "x.mtrl in A, B (mask)".</summary>
    private static string Requesters(IEnumerable<Request> requests)
        => string.Join("; ", requests
            .GroupBy(r => r.Material is null ? r.Requester : $"{r.Material}|{r.SlotLabel}")
            .Select(g => g.First() is { Material: { } material } first
                ? material + (g.Any(r => r.NamesTarget) ? $" in {string.Join(", ", g.Select(r => r.Target.DisplayName).Distinct())}" : string.Empty)
                  + (first.SlotLabel is { } slot ? $" ({slot})" : string.Empty)
                : g.Key)
            .Take(3));

    /// <summary>
    /// Where a repair adds the missing redirection: the asking option, or the default files when that option would win over
    /// another option that provides the path (also one only a different model's option brings), which must still win when on.
    /// </summary>
    private CheckContainer Into(Request request)
        => _providers.TryGetValue(request.Path, out var providers)
        && providers.Any(p => !ReferenceEquals(p.Container, request.Target) && !p.Container.IsExclusiveWith(request.Root) && Outranks(request.Target, p.Container))
            ? _default
            : request.Target;

    /// <summary>Adds a repair, or offers the one another finding already has when it makes the same edit, so two never write one redirection.</summary>
    private void OfferRepair(CheckFinding finding, string id, string title, string explanation, IReadOnlyList<RepairCandidate> candidates, bool forceOptIn = false)
    {
        if (candidates.Count == 0)
            return;
        if (_repairs.All(repair => repair.Id != id))
            AddRepair(id, finding.Key, CheckCategory.Materials, title, explanation, candidates, forceOptIn);
        if (!finding.RepairIds.Contains(id))
            finding.RepairIds.Add(id);
    }

    /// <summary>The mod's other redirections of a request's path: those that can be on together with it, or all when none can.</summary>
    private IReadOnlyList<CheckMapping> OtherProviders(Request request)
    {
        if (!_providers.TryGetValue(request.Path, out var providers))
            return Array.Empty<CheckMapping>();
        var possible = providers.Where(p => !ExclusiveWith(p.Container, request.Root, request.Via)).ToArray();
        return possible.Length > 0 ? possible : providers;
    }

    /// <summary>When the providers are all but a few options of one single-choice group: that group's name and the options without it.</summary>
    private (string? Group, IReadOnlyList<string>? Missing) GroupGap(IReadOnlyList<CheckMapping> providers)
    {
        var first = providers.FirstOrDefault()?.Container;
        if (first is not { Kind: ContainerKind.SingleOption } || providers.Any(p => p.Container.GroupIndex != first.GroupIndex))
            return (null, null);
        var siblings = _model.Containers.Where(c => c.GroupIndex == first.GroupIndex).ToArray();
        var missing = siblings.Where(c => !providers.Any(p => ReferenceEquals(p.Container, c))).Select(OptionName).ToArray();
        if (missing.Length == 0 || siblings.Length - missing.Length < 3 || missing.Length >= siblings.Length - missing.Length)
            return (null, null);
        var label = first.DisplayName;
        var separator = label.LastIndexOf(" › ", StringComparison.Ordinal);
        return (separator > 0 ? label[..separator] : label, missing);
    }

    private static string OptionName(CheckContainer container)
    {
        var label = container.DisplayName;
        var separator = label.LastIndexOf(" › ", StringComparison.Ordinal);
        return separator >= 0 ? label[(separator + 3)..] : label;
    }

    /// <summary>Whether two options' names share a word, such as "Metal Weathered" and "Metal - 2".</summary>
    private static bool NamesMatch(CheckContainer a, CheckContainer b)
    {
        static IEnumerable<string> Words(CheckContainer container)
            => System.Text.RegularExpressions.Regex.Split(OptionName(container), "[^\\p{L}]+").Where(word => word.Length >= 3);
        return !a.IsDefault && !b.IsDefault && Words(a).Intersect(Words(b), StringComparer.OrdinalIgnoreCase).Any();
    }

    /// <summary>Files that could serve a missing request, best first, redirected in <paramref name="into"/> (the request's target by default).</summary>
    private List<RepairCandidate> Candidates(Request request, CheckContainer? into = null)
    {
        var path = request.Path;
        into ??= request.Target;
        var found = new List<(string RelPath, RepairConfidence Confidence, string Reason, CheckMapping? Supersedes)>();

        var providers = OtherProviders(request);
        // Among several, the file of an option named like the one asking comes first: Metal's material gets "Metal - 1"'s texture.
        var alike = new HashSet<string>(providers.Where(p => NamesMatch(p.Container, request.Target)).Select(p => p.RelPath), StringComparer.OrdinalIgnoreCase);
        if (providers.Count > 0)
        {
            var files = providers.Select(p => p.RelPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var provider in providers.DistinctBy(p => p.RelPath, StringComparer.OrdinalIgnoreCase))
                found.Add((provider.RelPath, files.Length == 1 ? RepairConfidence.High : RepairConfidence.Medium, $"The file {provider.Container.DisplayName} uses", null));
        }

        var twin = ResourceNaming.Dx11Twin(path);
        if (Resolve(twin, request.Root, request.Via) is { IsModFile: true } twinResolution)
            found.Add((twinResolution.RelPath!, RepairConfidence.High, $"Redirected as {ShortName(twin)}, which differs only by the DX11 '--' prefix", null));

        if (SynonymKey(path) is { } synonym)
            foreach (var container in Active(request.Root, request.Via))
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
            .OrderBy(c => c.Confidence).ThenByDescending(c => alike.Contains(c.RelPath)).ThenBy(c => c.RelPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new List<RepairCandidate>();
        foreach (var candidate in merged)
        {
            result.Add(new RepairCandidate(candidate.RelPath, candidate.Confidence, candidate.Reason,
                new RepairEdit[] { new AddFileEdit(into.Option, path, candidate.RelPath) }));
            if (candidate.Supersedes is { } dead && candidate.Confidence == RepairConfidence.High
             && request.Resolution.Source is ResolutionSource.Unresolved or ResolutionSource.Exclusive)
                _supersededBy.TryAdd(dead, $"fix:{into.Option.Serialize()}:{path}");
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
