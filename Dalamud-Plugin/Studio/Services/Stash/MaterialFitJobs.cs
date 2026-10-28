using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>A model file's write time and length: when either changes, the file was written again.</summary>
internal readonly record struct ModelStamp(DateTime WrittenUtc, long Length)
{
    /// <summary>The file's stamp now, or null when it isn't there or can't be read.</summary>
    public static ModelStamp? Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new ModelStamp(info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>A fit asked for on the render thread, with what it needs of the item read there.</summary>
/// <param name="Item">The item's key.</param>
/// <param name="ModelPath">The model file the materials are fitted to.</param>
/// <param name="MaterialsAtRequest">
/// The item's materials when the fit was asked for, as a copy of the list: none for a model just dropped or
/// picked, the ones "Fit to model" replaces. The fit goes in only while the item still has these very ones, in
/// this order: matching vanilla, pasting, or removing one and adding another meanwhile all make it stale.
/// </param>
/// <param name="OtherPreset">The preset + Material suggests for a subject other than gear or a weapon, or null.</param>
/// <param name="OnlyFromStash">
/// Fit only a model of a stash folder (a model dropped or picked): one from anywhere else keeps its own material paths.
/// </param>
internal sealed record MaterialFitRequest(uint Item, string ModelPath, IReadOnlyList<MaterialSetup> MaterialsAtRequest, PortSubject Subject,
    RaceGender? NamingRace, MaterialPreset? OtherPreset, bool OnlyFromStash = false);

/// <summary>
/// What a fit found, or why it found nothing; and the model file's stamp from before it was read. A fit asked
/// for only from the stash, of a model in no stash folder, is <paramref name="NotInStash"/>: nothing changes, and nothing is said.
/// </summary>
internal sealed record MaterialFitOutcome(MaterialFitResult? Fit, string? Problem, ModelStamp? Stamp, bool NotInStash = false);

/// <summary>A fit that ended: what it found, or the exception it stopped with. Both are null for one that was cancelled.</summary>
internal sealed record FinishedFit(MaterialFitRequest Request, MaterialFitOutcome? Outcome, Exception? Error);

/// <summary>
/// Materials fitted to a model dropped or picked on a Build item (see <see cref="MaterialFit"/>), worked out off
/// the render thread: reading the model, its stash folder and that folder's images takes a moment. One fit per
/// item, the latest asked for: an earlier one is cancelled and never comes back. The render thread takes finished
/// fits back and puts them in while their item still wants them (see <see cref="StillWanted"/>). Render thread only,
/// but for the work itself.
/// </summary>
internal sealed class MaterialFitJobs
{
    private sealed record Running(MaterialFitRequest Request, Task<MaterialFitOutcome> Work, CancellationTokenSource Cancel);

    private readonly Dictionary<uint, Running> _running = new();

    /// <summary>Starts <paramref name="work"/> for the request's item, in place of the fit under way for it.</summary>
    public void Start(MaterialFitRequest request, Func<CancellationToken, MaterialFitOutcome> work)
    {
        if (_running.Remove(request.Item, out var earlier))
        {
            earlier.Cancel.Cancel();
            earlier.Work.ContinueWith(t =>
            {
                _ = t.Exception;   // nobody looks at it now
                earlier.Cancel.Dispose();
            }, TaskScheduler.Default);
        }
        var cancel = new CancellationTokenSource();
        var token = cancel.Token;
        _running[request.Item] = new Running(request, Task.Run(() => work(token), token), cancel);
    }

    /// <summary>A fit that has ended, taken out of the list; false when none has. Called every frame: cheap while none runs.</summary>
    public bool TakeFinished([NotNullWhen(true)] out FinishedFit? finished)
    {
        finished = null;
        if (_running.Count == 0)
            return false;
        foreach (var (item, running) in _running)
        {
            if (!running.Work.IsCompleted)
                continue;
            _running.Remove(item);
            running.Cancel.Dispose();
            var work = running.Work;
            finished = new FinishedFit(running.Request, work.IsCompletedSuccessfully ? work.Result : null, work.Exception?.InnerException);
            return true;
        }
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // What is fitted, and when it goes in
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The file a model set on <paramref name="item"/> fits its materials to: of several, the first; none when it has materials.</summary>
    public static string? FileToFit(PortItem item, IReadOnlyList<string> files)
        => item.Materials.Count == 0 && item.Animation == null && files.Count > 0 ? files[0] : null;

    /// <summary>The model file "Fit to model" uses: the first race's that builds from a file, of variants the first; null when none does.</summary>
    public static string? ModelFile(PortItem item)
    {
        foreach (var model in item.Models)
        {
            if (model.UseDummy)
                continue;
            var file = model.UseVariants ? model.VariantPaths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) : model.SourcePath;
            if (!string.IsNullOrWhiteSpace(file))
                return file;
        }
        return null;
    }

    /// <summary>
    /// Whether a finished fit still belongs on <paramref name="item"/>: the item is still in the pack, with the very
    /// materials it had when the fit was asked for, and one of its races still builds from the model.
    /// </summary>
    public static bool StillWanted(MaterialFitRequest request, [NotNullWhen(true)] PortItem? item)
        => item != null && item.Key == request.Item && item.Animation == null
        && SameMaterials(item.Materials, request.MaterialsAtRequest)
        && item.Models.Exists(m => BuildsFrom(m, request.ModelPath));

    /// <summary>Whether a fit that went in selects its first material: only while the selection is still on its item, so a late one never pulls it back.</summary>
    public static bool TakesSelection(MaterialFitRequest request, Target? selection)
        => selection is { } at && at.IsWithin(new Target(TargetKind.Item, request.Item));

    private static bool SameMaterials(IReadOnlyList<MaterialSetup> now, IReadOnlyList<MaterialSetup> then)
    {
        if (now.Count != then.Count)
            return false;
        for (int i = 0; i < now.Count; i++)
            if (!ReferenceEquals(now[i], then[i]))
                return false;
        return true;
    }

    private static bool BuildsFrom(RaceModelEntry model, string path)
        => !model.UseDummy && (model.UseVariants
            ? model.VariantPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
            : string.Equals(model.SourcePath, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Why a wanted fit doesn't go in, as a short warning, or null when it does: the model couldn't be read, it
    /// was written again since (<paramref name="now"/> is its stamp as it is now), or it names no materials.
    /// </summary>
    public static string? Problem(MaterialFitOutcome outcome, string modelPath, ModelStamp? now)
    {
        if (outcome.Problem != null)
            return outcome.Problem;
        if (outcome.Stamp != now)
            return BeingWritten(modelPath);
        return outcome.Fit is { Materials.Count: > 0 } ? null : $"No materials found in {Path.GetFileName(modelPath)}.";
    }

    /// <summary>What the status bar says once a fit went in: "Set up 3 materials from Top.mdl, 2 with diffuse."</summary>
    public static string Message(MaterialFitResult fit, string modelPath)
    {
        int count = fit.Materials.Count;
        var diffuse = fit.WithDiffuse == 0 ? "none" : fit.WithDiffuse.ToString(CultureInfo.InvariantCulture);
        return $"Set up {count} material{(count == 1 ? string.Empty : "s")} from {Path.GetFileName(modelPath)}, {diffuse} with diffuse.";
    }

    /// <summary>What the status bar says while a fit runs.</summary>
    public static string Starting(string modelPath) => $"Setting up materials from {Path.GetFileName(modelPath)}.";

    private static string BeingWritten(string modelPath) => $"{Path.GetFileName(modelPath)} is still being written. Try again in a moment.";

    /// <summary>
    /// The fit itself, on the background task: the materials the model's meshes name, with their textures from
    /// the stash (see <see cref="ModelTextureLookup"/>). The model is read with a cache of its own, since the shared
    /// one is the render thread's. A model being written isn't fitted: another program holding it open fails the
    /// read (and is said so in short), and one written again meanwhile changes its stamp. Asked for only from
    /// the stash, a model in no stash folder isn't read at all.
    /// </summary>
    /// <param name="stashRoots">The folders extractions sit in (see <see cref="PackageImportPaths.StashRoots"/>).</param>
    /// <param name="openedFolders">The folders of loose files the user opened in the stash that are still there.</param>
    public static MaterialFitOutcome Run(MaterialFitRequest request, IReadOnlyList<string> stashRoots, IReadOnlyList<string> openedFolders,
        CancellationToken cancel)
    {
        var path = request.ModelPath;
        var stamp = ModelStamp.Of(path);
        var lookup = ModelTextureLookup.Open(path, stashRoots, openedFolders, cancel);
        if (request.OnlyFromStash && lookup.Folder == null)
            return new MaterialFitOutcome(null, null, stamp, NotInStash: true);
        var facts = new ModelFileCache().Read(path);
        if (!facts.Ok)
            return new MaterialFitOutcome(null, HeldOpen(path) ? BeingWritten(path) : $"Could not read {Path.GetFileName(path)} ({facts.Error?.TrimEnd('.')}).", stamp);

        var fit = MaterialFit.Fit(facts.Materials, lookup.TexturesFor, request.Subject, request.NamingRace, request.OtherPreset);
        cancel.ThrowIfCancellationRequested();
        return ModelStamp.Of(path) == stamp
            ? new MaterialFitOutcome(fit, null, stamp)
            : new MaterialFitOutcome(null, BeingWritten(path), stamp);
    }

    /// <summary>Whether another program holds the file open to write it (Blender saving it, say), so it can't be read.</summary>
    private static bool HeldOpen(string path)
    {
        const int SharingViolation = 32, LockViolation = 33;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is SharingViolation or LockViolation)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
