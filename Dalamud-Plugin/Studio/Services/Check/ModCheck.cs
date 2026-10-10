using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;

namespace InstantEdit.Studio.Services.Check;

/// <summary>
/// Checks why a mod's items might not show up: traces every model → material → texture chain per
/// option, the IMC material sets and EQDP race models that decide which files the game asks for,
/// and sorts out what no chain reaches. <see cref="Run"/> returns the findings with repairs that
/// only edit meta.json. Mod Optimizer's ModAnalyzer, rebuilt on Studio's models.
///
/// Runs on a worker. Reads the mod's files from disk and the game's through <see cref="IVanillaFiles"/>,
/// and writes nothing.
/// </summary>
internal sealed partial class ModCheck
{
    private readonly CheckInput _input;
    private readonly CheckModel _model;
    private readonly IVanillaFiles _game;
    private readonly CancellationToken _token;
    private readonly CheckContainer _default;
    private readonly Dictionary<CheckContainer, Dictionary<string, CheckMapping>> _files = new();
    private readonly Dictionary<string, List<CheckMapping>> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _notes = new();

    private ModCheck(CheckInput input, CancellationToken token)
    {
        _input   = input;
        _model   = CheckModel.Build(input.Meta, input.Folder);
        _game    = input.Game;
        _token   = token;
        _default = _model.Default;
    }

    public static ModCheckReport Run(CheckInput input, IProgress<CheckProgress>? progress, CancellationToken token)
    {
        var fingerprint = MetaFingerprint.Of(input.Folder);
        progress?.Report(new CheckProgress("Reading the mod's files", 0, 5));
        var check = new ModCheck(input, token);
        check.Analyze(progress);
        progress?.Report(new CheckProgress("Planning repairs", 5, 5));
        return check.BuildReport(fingerprint);
    }

    private void Analyze(IProgress<CheckProgress>? progress)
    {
        BuildIndexes();
        foreach (var mapping in _model.Mappings)
        {
            _token.ThrowIfCancellationRequested();
            State(mapping.RelPath);
        }

        progress?.Report(new CheckProgress("Tracing models, materials and textures", 1, 5));
        WalkModels();
        CollectShippedRoots();
        progress?.Report(new CheckProgress("Checking materials no model uses", 2, 5));
        ClassifyMaterialsAndValidateStandalone();
        progress?.Report(new CheckProgress("Checking race models", 3, 5));
        BuildRaceTables();
        progress?.Report(new CheckProgress("Checking your Penumbra collection", 4, 5));
        ResolveExternal();
        ClassifyRemaining();
        FindOrphans();
    }

    // ── Index ────────────────────────────────────────────────────────────────

    private void BuildIndexes()
    {
        foreach (var container in _model.Containers)
            _files[container] = new Dictionary<string, CheckMapping>(StringComparer.Ordinal);
        foreach (var mapping in _model.Mappings)
        {
            if (mapping.Shadowed || !_files[mapping.Container].TryAdd(mapping.Key, mapping))
                continue;
            if (!_providers.TryGetValue(mapping.Key, out var list))
                _providers[mapping.Key] = list = new List<CheckMapping>();
            list.Add(mapping);
        }
    }

    /// <summary>What Penumbra applies for a container: itself, with the default files under it.</summary>
    private IReadOnlyList<CheckContainer> Active(CheckContainer root)
        => root.IsDefault ? new[] { root } : new[] { root, _default };

    // ── Files ────────────────────────────────────────────────────────────────

    private sealed record FileState(string RelPath, bool Exists, FileKind Kind, long Size, DateTime LastWriteUtc, string? Error);

    private FileState State(string relPath)
    {
        if (_states.TryGetValue(relPath, out var state))
            return state;
        try
        {
            var info = new FileInfo(Full(relPath));
            state = info.Exists
                ? new FileState(relPath, true, FileSniffer.SniffFile(info.FullName), info.Length, info.LastWriteTimeUtc, null)
                : new FileState(relPath, false, FileKind.Unknown, 0, default, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            state = new FileState(relPath, false, FileKind.Unknown, 0, default, ex.Message);
        }
        return _states[relPath] = state;
    }

    private string Full(string relPath) => PathNormalizer.ToDiskPath(_input.Folder, relPath);

    // ── Resolution ───────────────────────────────────────────────────────────

    /// <summary>Where the game gets <paramref name="path"/> from when <paramref name="root"/> asks for it.</summary>
    private PathResolution Resolve(string path, CheckContainer root, int depth = 0)
    {
        path = PathNormalizer.GamePath(path);
        var active = Active(root);
        foreach (var container in active)
        {
            if (_files[container].TryGetValue(path, out var mapping))
                return new PathResolution(ResolutionSource.ModFile, path, mapping.RelPath, container.Option,
                    container.IsDefault ? null : container.DisplayName);
            if (depth < 4 && _model.Swaps[container].TryGetValue(path, out var target))
            {
                var swapped = Resolve(target, root, depth + 1);
                return swapped with
                {
                    RequestedPath = path,
                    Detail = $"file swap to {target}" + (swapped.Detail is null ? string.Empty : $"; {swapped.Detail}"),
                };
            }
        }

        _providers.TryGetValue(path, out var providers);
        if (providers is not null && AlwaysProvided(providers) is { } always)
            return new PathResolution(ResolutionSource.AlwaysProvided, path, always.RelPath, always.Container.Option, always.Container.DisplayName);
        if (_game.Exists(path))
            return new PathResolution(ResolutionSource.Vanilla, path);
        if (providers is { Count: > 0 })
        {
            var names = providers.Select(provider => provider.Container.DisplayName).Distinct().ToArray();
            var exclusive = providers.All(provider => provider.Container.IsExclusiveWith(root));
            return new PathResolution(exclusive ? ResolutionSource.Exclusive : ResolutionSource.Conditional, path, providers[0].RelPath,
                providers[0].Container.Option, Providers: names);
        }
        var searched = string.Join(" or ", active.Select(container => container.DisplayName));
        return new PathResolution(ResolutionSource.Unresolved, path, Detail: $"not redirected by {searched}, and not a game file");
    }

    /// <summary>A redirection every option of a single-choice or combining group without conditions carries is always on.</summary>
    private static CheckMapping? AlwaysProvided(IReadOnlyList<CheckMapping> providers)
    {
        foreach (var group in providers.Where(p => p.Container.GroupIndex >= 0 && !p.Container.HasCondition
                                                && p.Container.Kind is ContainerKind.SingleOption or ContainerKind.CombiningContainer)
                     .GroupBy(p => p.Container.GroupIndex))
        {
            var count = group.First().Container.SiblingCount;
            if (count > 0 && group.Select(p => p.Container.OptionIndex).Distinct().Count() == count)
                return group.First();
        }
        return null;
    }

    private static string ShortName(string path) => path[(path.LastIndexOf('/') + 1)..];
}
