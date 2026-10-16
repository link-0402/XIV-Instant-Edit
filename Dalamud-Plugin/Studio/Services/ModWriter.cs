using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using InstantEdit.Services.TextureCompression;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Models.Resources;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;

namespace InstantEdit.Studio.Services;

/// <summary>
/// One running (or finished) update of the mod. The worker only ever writes <see cref="Progress"/>
/// (an immutable snapshot swapped atomically), <see cref="Committing"/> and the task result; the
/// render thread only reads them. Nothing else is shared, so there is no field for the two threads
/// to race on.
/// </summary>
internal sealed class WriteJob
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly CancellationTokenSource _stop = new();
    private volatile WriteProgress _progress = new(0, 0, "Starting");
    private volatile bool _committing;

    public WriteJob(WriteRequest request)
    {
        Request = request;
        Task = System.Threading.Tasks.Task.Run(() => new ModWriter(this).Run(_cancel.Token, _stop.Token));
    }

    public WriteRequest       Request  { get; }
    public Task<WriteReport>  Task     { get; }
    public WriteProgress      Progress => _progress;
    public bool               IsDone   => Task.IsCompleted;

    /// <summary>
    /// Whether the update has started writing into the mod. Until then it has only read and
    /// converted, and stopping it leaves the mod exactly as it was; from then on it runs to the end.
    /// </summary>
    public bool Committing => _committing;

    /// <summary>Stops the update before it writes anything (a newer change is waiting). A conversion under way finishes first, and is kept for next time.</summary>
    public void Cancel() => _cancel.Cancel();

    /// <summary>Stops it as soon as it can, not waiting on Penumbra either: the plugin is going away.</summary>
    public void Stop()
    {
        _cancel.Cancel();
        _stop.Cancel();
    }

    internal void Report(WriteProgress progress) => _progress = progress;
    internal void BeginCommit() => _committing = true;
}

/// <summary>
/// Updates a mod open in Edit mode from the replacements set up for it: images converted to the
/// game's texture format (a .tex as it is), materials made here generated, every other file taken
/// unchanged — folded into the mod's meta.json, leaving everything else in the mod as it was.
///
/// It works in two steps. <b>Preparing</b> works out every file the replacements make and compares
/// each with what the last update wrote (the sidecar's ledger): a file asked for the same way is
/// left alone, whatever happened to it since — a material edited in Penumbra survives any number of
/// unrelated changes here — and only what changed is made again (converted, generated, read).
/// Nothing in the mod folder is touched while preparing, so an update can be stopped then.
/// <b>Committing</b> writes what changed: over this plugin's own file while it is still as written;
/// onto a material changed elsewhere by carrying over only the texture changes made here; and not
/// at all over anything else changed elsewhere, which waits for the user (see
/// <see cref="WriteConflict"/>). Then meta.json is read again, merged and written, and the new
/// ledger saved into the sidecar.
///
/// A file that cannot be made this time (its source went missing, a conversion failed) is not taken
/// out of the mod: what the mod has from before stays until it can be made again. Only what no
/// replacement asks for any more is taken back out.
///
/// A file only ever goes where nothing of the mod's own lives: if the place a file would naturally
/// go already holds a file this plugin did not write, it is written under "instant_edit\" instead and the
/// entry is pointed there, so the mod's own file survives (and comes back if the replacement goes).
///
/// Runs on a worker thread against a <see cref="WriteRequest"/> snapshot. Telling Penumbra to read
/// the mod again happens afterwards on the render thread.
/// </summary>
internal sealed class ModWriter
{
    /// <summary>An intent nothing produces: for a file that failed before its intent was known.</summary>
    private const string NoIntent = "?";

    private enum PlanState
    {
        /// <summary>Has to be written (made, if it has no content yet).</summary>
        Write,

        /// <summary>Asked for the way it was asked last time: the mod keeps it as it is.</summary>
        Unchanged,

        /// <summary>Could not be made this time: what the last update wrote stays.</summary>
        KeepPrevious,
    }

    /// <summary>One file an update puts into the mod: decided while preparing, written (or not) when committing.</summary>
    private sealed class Planned
    {
        public required string GamePath { get; init; }
        public required OptionRef Option { get; init; }
        public required string RelPath { get; init; }
        public required string Intent { get; init; }
        public LedgerFile? Previous { get; init; }

        /// <summary>The file the last update wrote is gone from the folder, while the mod still points at it.</summary>
        public bool Missing { get; init; }

        public PlanState State { get; set; }
        public Content? Content { get; set; }
        public MaterialRecipe? Material { get; set; }

        // What the report says about it.
        public required string Label { get; init; }
        public Target? Source { get; init; }
        public string? Detail { get; set; }

        public bool NeedsContent => State == PlanState.Write && Content == null;
    }

    /// <summary>What a file is made of: bytes in memory, or a file to copy (a converted texture in the cache, a source taken as it is).</summary>
    private sealed class Content
    {
        private string? _sha;

        private Content(byte[]? bytes, string? file)
        {
            Bytes = bytes;
            File  = file;
        }

        public byte[]? Bytes { get; }
        public string? File { get; }

        public static Content Of(byte[] bytes) => new(bytes, null);
        public static Content FromFile(string path) => new(null, path);

        public string Sha256 => _sha ??= Bytes != null ? Convert.ToHexString(SHA256.HashData(Bytes)) : HashFile(File!);
    }

    /// <summary>A material as it is made here: its shader package and the texture of each role — what is carried over onto a copy changed elsewhere.</summary>
    private sealed record MaterialRecipe(string Shader, IReadOnlyDictionary<TextureType, string> Textures)
    {
        public LedgerMaterial ToLedger() => new()
        {
            Shader   = Shader,
            Textures = Textures.ToDictionary(t => t.Key.ToString(), t => t.Value),
        };
    }

    private readonly WriteJob _job;
    private readonly WriteRequest _req;
    private readonly TextureCache _textures;
    private readonly WriteOutput _output = new();
    private readonly List<Planned> _files = new();

    /// <summary>What the mod was last updated with.</summary>
    private WriteLedger _previous = new();

    /// <summary>Its files, by <see cref="WriteConflict.KeyFor"/>.</summary>
    private readonly Dictionary<string, LedgerFile> _previousFiles = new(StringComparer.Ordinal);

    /// <summary>Every place this update writes to (relative-path keys), and the entry (<see cref="WriteConflict.KeyFor"/>) writing it.</summary>
    private readonly Dictionary<string, string> _claimed = new(StringComparer.Ordinal);

    /// <summary>Places the last update wrote to, and the entry each was written for; only that entry may write there again.</summary>
    private Dictionary<string, string> _owned = new(StringComparer.Ordinal);

    /// <summary>Every mod-relative path meta.json pointed at when the update was asked for.</summary>
    private HashSet<string> _inUse = new(StringComparer.Ordinal);

    private WriteReport _report = null!;
    private string _modPath = string.Empty;
    private int _done;
    private int _total;

    /// <summary>Stops the update between steps, before it writes anything.</summary>
    private CancellationToken _cancel;

    /// <summary>Also stops waiting on Penumbra: the plugin is going away.</summary>
    private CancellationToken _stop;

    public ModWriter(WriteJob job)
    {
        _job = job;
        _req = job.Request;
        _textures = new TextureCache(_req.CacheDirectory);
    }

    public WriteReport Run(CancellationToken cancel, CancellationToken stop)
    {
        _cancel = cancel;
        _stop = stop;
        _modPath = _req.ModPath;
        var snapshot = _req.Meta;
        var report = _report = new WriteReport
        {
            ModName    = string.IsNullOrWhiteSpace(snapshot.Name) ? _req.ModFolderName : snapshot.Name,
            ModPath    = _modPath,
            FolderName = _req.ModFolderName,
            Revision   = _req.Revision,
        };
        var clock = Stopwatch.StartNew();

        try
        {
            if (!Directory.Exists(_modPath))
                throw new DirectoryNotFoundException("the mod's folder is gone");

            report.SourceFingerprint = SourceFingerprint.Of(_req.SourcePaths);
            LoadPrevious();
            _total = 1 + _req.Replacements.Count;   // meta.json, replacements

            // ── Prepare: nothing in the mod folder changes ──────────────────
            PrepareReplacements(snapshot);

            // ── Commit: from here on, it runs to the end ────────────────────
            cancel.ThrowIfCancellationRequested();
            Commit();
        }
        catch (OperationCanceledException) when (!_job.Committing)
        {
            report.Cancelled = true;
        }
        catch (Exception ex)
        {
            StudioServices.Log.Warning(ex, "[Studio] Updating {0} stopped", _req.ModFolderName);
            report.FatalError = ex.Message;
        }

        _textures.Prune();
        report.Duration   = clock.Elapsed;
        report.FinishedAt = DateTime.UtcNow;
        _job.Report(new WriteProgress(_total, _total, report.Cancelled ? "Stopped" : "Done"));

        if (report.ChangedMod || report.FatalError != null || report.Conflicts.Count > 0)
            StudioServices.Log.Information("[Studio] Updated {0}: {1}", _req.ModFolderName, report.Summary());
        return report;
    }

    /// <summary>
    /// What the mod was last updated with: the sidecar's own copy (an update of this mod may have
    /// finished after the request was taken), else the one the session had.
    /// </summary>
    private void LoadPrevious()
    {
        _previous = StudioProjectStore.LoadLedger(_modPath) ?? _req.PreviousLedger;
        foreach (var file in _previous.Files)
            _previousFiles[WriteConflict.KeyFor(OptionRef.Parse(file.Option), file.GamePath)] = file;
        _owned = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in _previous.Files)
            _owned.TryAdd(PathNormalizer.RelKey(file.RelPath), WriteConflict.KeyFor(OptionRef.Parse(file.Option), file.GamePath));
        _inUse = _req.Meta.ReferencedRelKeys();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Planning
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plans one file: where it goes in the mod folder, and whether this update has anything to
    /// write for it — <see cref="Planned.NeedsContent"/> says whether the caller should make it.
    /// A later plan for the same entry replaces an earlier one.
    /// </summary>
    private Planned Plan(string gamePath, OptionRef option, string preferredRel, string intent, string label, Target? source)
    {
        var entry = WriteConflict.KeyFor(option, gamePath);
        _previousFiles.TryGetValue(entry, out var previous);
        string rel;
        if (previous != null && PathNormalizer.IsSafeRel(previous.RelPath))
        {
            // Where it was written before, so it stays put however the mod's own files moved.
            rel = previous.RelPath;
            _claimed.TryAdd(PathNormalizer.RelKey(rel), entry);
        }
        else
        {
            rel = Place(preferredRel, entry);
        }

        _files.RemoveAll(p => p.Option == option && PathNormalizer.SameGamePath(p.GamePath, gamePath));

        bool missing = previous != null && !File.Exists(Disk(previous.RelPath)) && _inUse.Contains(PathNormalizer.RelKey(previous.RelPath));
        bool same = previous?.Intent != null && previous.Intent == intent;
        var plan = new Planned
        {
            GamePath = gamePath,
            Option   = option,
            RelPath  = rel,
            Intent   = intent,
            Previous = previous,
            Missing  = missing,
            State    = same && !missing ? PlanState.Unchanged : PlanState.Write,
            Label    = label,
            Source   = source,
        };
        _files.Add(plan);
        return plan;
    }

    /// <summary>
    /// Hands a planned file what it is made of. A file last written by a version that kept no intent
    /// is compared by its bytes instead: the same bytes as were written then mean nothing changed.
    /// </summary>
    private void Provide(Planned plan, Content content, string? detail = null)
    {
        plan.Content = content;
        plan.Detail  = detail;

        if (plan.Previous is { Intent: null } previous && !plan.Missing && previous.Sha256.Length > 0
            && SameHash(previous.Sha256, content.Sha256))
        {
            plan.State   = PlanState.Unchanged;
            plan.Content = null;
        }
    }

    /// <summary>
    /// The file could not be made this time. Whatever the mod has there from the last update stays,
    /// rather than being taken out. Returns whether there is such a thing.
    /// </summary>
    private bool Fail(Planned plan, string error)
    {
        bool keeps = plan.Previous != null && !plan.Missing;
        _report.Add(WriteOutcome.Failed, plan.Label, plan.GamePath, keeps ? $"{error} — the mod keeps the one it has" : error, plan.Source);

        if (keeps)
        {
            plan.State   = PlanState.KeepPrevious;
            plan.Content = null;
        }
        else
        {
            _files.Remove(plan);
        }
        return keeps;
    }

    /// <summary>What a file made from <paramref name="source"/> is asked for as: the file as it is now (path, size, timestamp) and the settings that shape the result.</summary>
    private static string Intent(string kind, string source, params object[] settings)
        => $"{kind}:{TextureCache.KeyFor(source, settings)}";

    /// <summary>What a generated file is asked for as: the bytes it comes out as.</summary>
    private static string Generated(string kind, byte[] bytes)
        => $"{kind}:{Convert.ToHexString(SHA256.HashData(bytes))[..32].ToLowerInvariant()}";

    /// <summary>A texture made from an image (or a .tex taken as it is). Auto's result also depends on how the texture is read.</summary>
    private static string TextureIntent(string source, TextureCompression compression, IReadOnlyList<TextureUse> uses)
        => TextureSources.IsTex(source) ? Intent("copy", source)
            : compression == TextureCompression.Auto ? Intent("tex", source, compression, AutoCompression.KeyOf(uses))
            : Intent("tex", source, compression);

    /// <summary>A texture slot of a material made here: its placeholder, or its image.</summary>
    private string SlotIntent(TextureSlot tex, IReadOnlyList<TextureUse> uses)
    {
        if (!tex.UseWhiteDummy)
            return TextureIntent(tex.SourcePath, tex.Compression, uses);

        var option = DummyTextureLibrary.Resolve(tex.Type, tex.DummyPreset);
        return option.IsGenerated
            ? $"dummy:{TextureCache.KeyForGenerated("white", tex.WhiteDummySize, TextureCompressionInfo.ForPlaceholder(tex.Compression))}"
            : Intent("bundled", Path.Combine(_req.PluginDirectory, option.ResourcePath!));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Where files go
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The relative path a file goes to: <paramref name="preferredRel"/>, unless a file this plugin
    /// did not write already lives there — then under "instant_edit\", so the mod's own file is left alone.
    /// A place this plugin writes (or wrote) for another entry is passed over too: two options whose
    /// names come out the same as folders ("Red" and "red", "A/B" and "A_B") get places of their own.
    /// </summary>
    private string Place(string preferredRel, string entry)
    {
        var rel = PathNormalizer.RelPathForMeta(preferredRel);
        if (!PathNormalizer.IsSafeRel(rel))
            throw new InvalidOperationException($"refusing to write outside the mod folder ({preferredRel})");

        for (int attempt = 0; ; attempt++)
        {
            string candidate;
            if (attempt == 0)
                candidate = rel;
            else if (attempt == 1)
                candidate = $"instant_edit\\{rel}";
            else
            {
                var dir  = Path.GetDirectoryName(rel) ?? string.Empty;
                var stem = Path.GetFileNameWithoutExtension(rel);
                var ext  = Path.GetExtension(rel);
                candidate = dir.Length > 0 ? $"instant_edit\\{dir}\\{stem}_{attempt}{ext}" : $"instant_edit\\{stem}_{attempt}{ext}";
            }

            var key = PathNormalizer.RelKey(candidate);
            bool free = _claimed.TryGetValue(key, out var claimedBy)
                ? claimedBy == entry
                : _owned.TryGetValue(key, out var ownedBy) ? ownedBy == entry : !File.Exists(Disk(candidate));
            if (free)
            {
                _claimed[key] = entry;
                return candidate;
            }
        }
    }

    private string Disk(string rel) => PathNormalizer.ToDiskPath(_modPath, rel);

    /// <summary>Where a replacement naturally goes: at its game path for the default files, in a folder per option otherwise.</summary>
    private static string ReplacementRel(PenumbraModMeta meta, string gamePath, OptionRef option)
    {
        var path = PathNormalizer.GamePath(gamePath).Replace('/', '\\');
        if (option.IsDefault)
            return path;
        var group = meta.FindGroupView(option.Group)?.Name ?? "group";
        return $"{PathNormalizer.SafeSegment(group)}\\{PathNormalizer.SafeSegment(meta.OptionName(option))}\\{path}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Replaced and added resources
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every replacement: images converted to the game's texture format (a .tex as it is), every
    /// other file taken unchanged. Each goes to its own place in the mod and the option's entry is
    /// pointed at it; the file it replaces stays where it was.
    /// </summary>
    private void PrepareReplacements(PenumbraModMeta meta)
    {
        foreach (var replacement in _req.Replacements)
        {
            Checkpoint();
            var option   = OptionRef.Parse(replacement.Option);
            var gamePath = replacement.GamePath;
            var label    = Path.GetFileName(gamePath);
            var target   = new Target(TargetKind.Resource, Path: gamePath, Option: replacement.Option);
            Step(label);

            if (!meta.OptionExists(option))
            {
                _report.Add(WriteOutcome.Failed, label, gamePath, "the option it was meant for is no longer in the mod", target);
                continue;
            }
            // A game path with ".." in it would be written outside the mod folder (Place refuses it):
            // only this replacement fails, the rest of the update goes ahead.
            if (!PathNormalizer.IsSafeRel(PathNormalizer.GamePath(gamePath)))
            {
                _report.Add(WriteOutcome.Failed, label, gamePath, "not a valid game path: it would write outside the mod folder", target);
                continue;
            }
            if (replacement.Authored is { } authored)
            {
                PrepareAuthoredMaterial(meta, replacement, authored, option, gamePath, target);
                continue;
            }
            if (replacement.ReassignsMaterials && ResourceKinds.FromGamePath(gamePath) == ResourceKind.Model)
            {
                PrepareReassignedModel(meta, replacement, option, gamePath, target);
                continue;
            }

            var source = replacement.SourcePath;
            if (string.IsNullOrWhiteSpace(source))
            {
                _report.Add(WriteOutcome.Skipped, label, gamePath, "no file set", target);
                continue;
            }

            bool texture = ResourceKinds.IsTexturePath(gamePath);
            var uses = texture && replacement.Compression == TextureCompression.Auto ? AutoCompression.UsesFor(gamePath, source) : Array.Empty<TextureUse>();
            var plan = Plan(gamePath, option, ReplacementRel(meta, gamePath, option),
                texture ? TextureIntent(source, replacement.Compression, uses) : Intent("copy", source), label, target);
            if (!plan.NeedsContent)
                continue;

            if (!File.Exists(source))
            {
                Fail(plan, $"file not found ({source})");
                continue;
            }

            var how   = replacement.IsNew ? "added" : "replaced";
            var where = option.IsDefault ? string.Empty : $" · {meta.OptionLabel(option)}";
            if (texture)
            {
                if (!ConvertTexture(source, replacement.Compression, uses, label, out var content, out var error, out var detail))
                {
                    Fail(plan, error);
                    continue;
                }
                Provide(plan, content, $"{how} from {detail} → {plan.RelPath}{where}");
            }
            else
            {
                Provide(plan, Content.FromFile(source), $"{how} from {Path.GetFileName(source)}, copied as it is → {plan.RelPath}{where}");
            }
        }
    }

    /// <summary>
    /// A model whose material slots are pointed at other materials: the file of the user's own when
    /// one is set, else the mod's own model at this path, else the game's — with those slots' names
    /// rewritten, and everything else of the file kept byte for byte.
    /// </summary>
    private void PrepareReassignedModel(PenumbraModMeta meta, ResourceReplacement replacement, OptionRef option, string gamePath, Target target)
    {
        var label = Path.GetFileName(gamePath);
        var names = replacement.MaterialNames.Select(n => string.IsNullOrWhiteSpace(n) ? null : n.Trim()).ToList();
        var namesKey = string.Join(",", names.Select(n => n ?? string.Empty));

        string? file = null;
        string from;
        if (!string.IsNullOrWhiteSpace(replacement.SourcePath))
        {
            file = replacement.SourcePath;
            from = Path.GetFileName(file);
        }
        else if (OwnFile(meta, option, gamePath) is { } own)
        {
            file = own;
            from = "the mod's own model";
        }
        else
        {
            from = "the game's own model";
        }

        var intent = file != null
            ? Intent("mdl", file, namesKey)
            : $"mdl:{TextureCache.KeyForGenerated("game", PathNormalizer.GamePath(gamePath), namesKey)}";
        var plan = Plan(gamePath, option, ReplacementRel(meta, gamePath, option), intent, label, target);
        if (!plan.NeedsContent)
            return;

        byte[]? bytes;
        if (file != null)
        {
            if (!File.Exists(file))
            {
                Fail(plan, $"file not found ({file})");
                return;
            }
            bytes = File.ReadAllBytes(file);
        }
        else
        {
            bytes = _req.Vanilla?.TryReadBytes(PathNormalizer.GamePath(gamePath));
        }
        if (bytes == null)
        {
            Fail(plan, "neither the mod nor the game has this model to reassign the materials of");
            return;
        }

        byte[] rewritten;
        try
        {
            rewritten = ModelMaterialRewriter.Rewrite(bytes, names);
            var loads = ModelMaterialRewriter.ReadMaterialsStrict(rewritten);
            for (int i = 0; i < names.Count && i < loads.Count; i++)
            {
                if (names[i] != null && !string.Equals(loads[i], names[i], StringComparison.Ordinal))
                {
                    Fail(plan, $"material slot {i + 1} would not load {names[i]} in the rewritten model");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Fail(plan, $"its materials could not be reassigned ({ex.Message})");
            return;
        }

        int changed = names.Count(n => n != null);
        var how   = replacement.IsNew ? "added" : "replaced";
        var where = option.IsDefault ? string.Empty : $" · {meta.OptionLabel(option)}";
        Provide(plan, Content.Of(rewritten), $"{how} from {from}, {changed} material slot(s) reassigned → {plan.RelPath}{where}");
    }

    /// <summary>
    /// The mod's own file at a game path in an option, on disk: what the entry pointed at before this
    /// plugin repointed it, or what it points at now when this plugin never wrote there. Null when the
    /// entry is this plugin's own addition, or the option has nothing there.
    /// </summary>
    private string? OwnFile(PenumbraModMeta meta, OptionRef option, string gamePath)
    {
        string? rel;
        if (_previousFiles.TryGetValue(WriteConflict.KeyFor(option, gamePath), out var previous))
            rel = previous.OriginalRelPath;
        else if (!meta.TryGetFile(option, gamePath, out rel) || _owned.ContainsKey(PathNormalizer.RelKey(rel)))
            rel = null;

        if (string.IsNullOrEmpty(rel) || !PathNormalizer.IsSafeRel(rel))
            return null;
        var full = Disk(rel);
        return File.Exists(full) ? full : null;
    }

    /// <summary>The file the mod serves at a game path now, in an option or else its default files — whoever wrote it.</summary>
    private string? CurrentFile(PenumbraModMeta meta, OptionRef option, string gamePath)
    {
        foreach (var where in option.IsDefault ? new[] { option } : new[] { option, OptionRef.Default })
        {
            if (meta.TryGetFile(where, gamePath, out var rel) && !string.IsNullOrEmpty(rel) && PathNormalizer.IsSafeRel(rel) && File.Exists(Disk(rel)))
                return Disk(rel);
        }
        return null;
    }

    /// <summary>
    /// What a material cloned here starts from: the mod's own material (when that is asked for), else
    /// the game's. Its own path is read from the mod's own file — never from what this plugin wrote
    /// there, so edits do not pile up on each other; another path from whatever the mod serves there.
    /// </summary>
    private (MtrlInfo? Template, string From, string? Error) CloneTemplate(PenumbraModMeta meta, ResourceReplacement replacement, OptionRef option, string gamePath)
    {
        var path = PathNormalizer.GamePath(replacement.TemplateGamePath);
        bool own = PathNormalizer.SameGamePath(path, gamePath);
        var name = own ? "own material" : Path.GetFileName(path);

        byte[]? bytes = null;
        string from = string.Empty;
        if (replacement.CloneModMaterial && (own ? OwnFile(meta, option, gamePath) : CurrentFile(meta, option, path)) is { } file)
        {
            bytes = File.ReadAllBytes(file);
            from = $"the mod's {name}";
        }
        else if (_req.Vanilla?.TryReadBytes(path) is { } game)
        {
            bytes = game;
            from = $"the game's {name}";
        }

        if (bytes == null)
            return (null, from, own ? "neither the mod nor the game has a material at this path to start from" : $"neither the mod nor the game has {path} to start from");
        try
        {
            return (MtrlReader.Read(bytes), from, null);
        }
        catch (Exception ex)
        {
            return (null, from, $"{from} could not be read ({ex.Message})");
        }
    }

    /// <summary>
    /// A material made here: each texture slot's image converted beside it (into the model set's
    /// texture folder), then a bundled preset — or the material it is cloned from — rewired to those
    /// textures and written at the material's game path. A slot kept as the material had it points at
    /// the same texture again. Everything goes into the option the material is in.
    /// </summary>
    private void PrepareAuthoredMaterial(PenumbraModMeta meta, ResourceReplacement replacement, MaterialSetup mat, OptionRef option,
        string gamePath, Target target)
    {
        var label = Path.GetFileName(gamePath);
        var textures = new Dictionary<TextureType, string>();

        // Found before the textures are made: Auto checks each one against how this material reads it.
        MtrlInfo? template = null;
        bool isPreset = false;
        string from = string.Empty;
        string? templateError = null;
        bool clones = replacement.CloneGameMaterial || replacement.CloneModMaterial;
        if (clones)
        {
            (template, from, templateError) = CloneTemplate(meta, replacement, option, gamePath);
        }
        else
        {
            var presetPath = MaterialPresetLibrary.FindPresetPath(mat.ShaderType, mat.Textures.Select(t => t.Type).ToHashSet());
            if (presetPath != null && File.Exists(Path.Combine(_req.PluginDirectory, presetPath)))
            {
                template = MtrlReader.Read(File.ReadAllBytes(Path.Combine(_req.PluginDirectory, presetPath)));
                isPreset = true;
                from = $"preset ({ShaderInfo.DisplayName(mat.ShaderType)})";
            }
            else
            {
                templateError = $"no bundled preset for {ShaderInfo.DisplayName(mat.ShaderType)} with these textures";
            }
        }

        foreach (var tex in mat.Textures)
        {
            Checkpoint();
            if (tex.IsShared)
            {
                textures[tex.Type] = PathNormalizer.GamePath(tex.SharedGamePath);
                continue;
            }

            var texPath = ResourceNaming.SlotTexturePath(gamePath, tex);
            var texLabel = Path.GetFileName(texPath);

            if (tex.UseVariants)
            {
                _report.Add(WriteOutcome.Skipped, texLabel, texPath,
                    "a folder of variants is not supported for a material made here — pick one image", target);
                continue;
            }
            if (!tex.UseWhiteDummy && string.IsNullOrWhiteSpace(tex.SourcePath))
            {
                _report.Add(WriteOutcome.Skipped, texLabel, texPath, "no image set", target);
                continue;
            }

            var uses = tex.Compression == TextureCompression.Auto ? AutoCompression.UsesFor(tex.Type, mat.ShaderType, template) : Array.Empty<TextureUse>();
            var plan = Plan(texPath, option, ReplacementRel(meta, texPath, option), SlotIntent(tex, uses), texLabel, target);
            if (MakeTexture(plan, tex, uses, $"{TextureTypeInfo.DisplayName(tex.Type)} for {label}, from "))
                textures[tex.Type] = texPath;
        }

        if (textures.Count == 0)
        {
            _report.Add(WriteOutcome.Skipped, label, gamePath, "none of its textures are in the mod yet", target);
            return;
        }

        var rel = ReplacementRel(meta, gamePath, option);
        if (template == null)
        {
            Fail(Plan(gamePath, option, rel, NoIntent, label, target), templateError ?? "there is nothing to make it from");
            return;
        }

        // Cloned from its own path with every texture kept as it was: nothing to write yet, and the
        // mod keeps its own file (or the game's) until something is changed.
        if (clones && PathNormalizer.SameGamePath(replacement.TemplateGamePath, gamePath) && mat.Textures.All(t => t.IsShared)
            && SameTextures(template, textures))
        {
            _report.Add(WriteOutcome.Skipped, label, gamePath, "nothing changed from the material it starts from yet", target);
            return;
        }

        if (!MaterialPatcher.TryBuildPatchedMaterial(template, textures, isPreset, out var mtrlData, out var patchPlan, out var patchError))
        {
            Fail(Plan(gamePath, option, rel, NoIntent, label, target), patchError ?? "it could not be written");
            return;
        }

        var materialPlan = Plan(gamePath, option, rel, Generated("mtrl", mtrlData), label, target);
        materialPlan.Material = new MaterialRecipe(template.ShaderPackageName, textures);
        if (materialPlan.NeedsContent)
        {
            var changes = patchPlan.Describe();
            Provide(materialPlan, Content.Of(mtrlData), $"made here from {from}{(changes.Length > 0 ? $"; {changes}" : string.Empty)} → {materialPlan.RelPath}");
        }
    }

    /// <summary>Whether a material already uses exactly these textures, role for role.</summary>
    private static bool SameTextures(MtrlInfo material, IReadOnlyDictionary<TextureType, string> textures)
    {
        var has = new Dictionary<TextureType, string>();
        for (int i = 0; i < material.TextureOffsets.Length; i++)
            if (MaterialPatcher.TypeOf(material, i) is { } type && !has.ContainsKey(type))
                has[type] = material.TextureOffsets[i].Path;
        return has.Count == textures.Count
            && textures.All(t => has.TryGetValue(t.Key, out var path) && PathNormalizer.SameGamePath(path, t.Value));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Textures
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Makes a planned texture from its slot, if the plan needs it: the placeholder, or the image
    /// converted. Returns whether the mod has the texture afterwards.
    /// </summary>
    private bool MakeTexture(Planned plan, TextureSlot tex, IReadOnlyList<TextureUse> uses, string prefix = "")
    {
        if (!plan.NeedsContent)
            return true;

        Content content;
        string detail;
        if (tex.UseWhiteDummy)
        {
            if (!DummyTexture(tex, out content, out var error, out detail))
                return Fail(plan, error);
        }
        else
        {
            if (!File.Exists(tex.SourcePath))
                return Fail(plan, $"file not found ({tex.SourcePath})");
            if (!ConvertTexture(tex.SourcePath, tex.Compression, uses, plan.Label, out content, out var error, out detail))
                return Fail(plan, error);
        }

        Provide(plan, content, prefix + detail);
        return true;
    }

    /// <summary>Converts one image as <see cref="ConvertTexture(string, TextureCompression, string, out Content, out string, out bool)"/> does, or picks its format first for Auto; <paramref name="detail"/> is its report line.</summary>
    private bool ConvertTexture(string sourcePath, TextureCompression compression, IReadOnlyList<TextureUse> uses, string label,
        out Content content, out string error, out string detail)
    {
        if (compression == TextureCompression.Auto && !TextureSources.IsTex(sourcePath))
            return AutoTexture(sourcePath, uses, label, out content, out error, out detail);

        bool ok = ConvertTexture(sourcePath, compression, label, out content, out error, out bool cached);
        detail = ok ? DescribeTexture(sourcePath, compression, cached) : string.Empty;
        return ok;
    }

    /// <summary>
    /// Auto: Penumbra encodes the image and the check Optimize textures uses decides whether the
    /// compressed texture is written, or the uncompressed one (see <see cref="AutoCompression"/>).
    /// Cached like any conversion, under how the texture is read as well. Without Penumbra, or when it
    /// can't convert the image, the texture is written uncompressed, which loses nothing.
    /// </summary>
    private bool AutoTexture(string sourcePath, IReadOnlyList<TextureUse> uses, string label, out Content content, out string error, out string detail)
    {
        var name = Path.GetFileName(sourcePath);
        string Uncompressed(string why, out Content plain, out string failure)
        {
            if (!ConvertTexture(sourcePath, TextureCompression.None, label, out plain, out failure, out bool reused))
                return string.Empty;
            return $"{name}, uncompressed (Auto: {why}){(reused ? " (converted before, reused)" : string.Empty)}";
        }

        if (_req.CheckTexture is not { } convert)
        {
            detail = Uncompressed("Penumbra isn't there to check compressing it", out content, out error);
            return detail.Length > 0;
        }

        var key = TextureCache.KeyFor(sourcePath, TextureCompression.Auto, AutoCompression.KeyOf(uses));
        if (_textures.PathOf(key) is { } hit)
        {
            content = Content.FromFile(hit);
            error   = string.Empty;
            detail  = AutoCompression.Describe(name, AutoCompression.FormatOf(ReadHead(hit)), string.Empty, " (converted before, reused)");
            return true;
        }

        string? decoded = null;
        try
        {
            var input = sourcePath;
            if (TextureSources.NeedsDecoding(sourcePath))
            {
                decoded = TextureSources.DecodeToPng(sourcePath, out error);
                if (decoded == null)
                {
                    content = null!;
                    detail  = string.Empty;
                    return false;
                }
                input = decoded;
            }

            _job.Report(new WriteProgress(_done - 1, _total, $"{label} — compressing and checking"));
            var result = AutoCompression.Encode(input, uses, convert, _stop);
            content = Keep(key, result.Tex);
            error   = string.Empty;
            detail  = AutoCompression.Describe(name, result.Format, result.Kept, string.Empty);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StudioServices.Log.Information("[Studio] Penumbra did not encode {0} for Auto ({1}); writing it uncompressed", sourcePath, ex.Message);
            detail = Uncompressed($"Penumbra couldn't encode it, {ex.Message}", out content, out error);
            return detail.Length > 0;
        }
        finally
        {
            if (decoded != null)
            {
                try { File.Delete(decoded); } catch (Exception) { /* harmless */ }
            }
        }
    }

    /// <summary>The first bytes of a .tex, enough for its header.</summary>
    private static byte[] ReadHead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[TextureCost.HeaderSize];
            return stream.Read(head, 0, head.Length) == head.Length ? head : Array.Empty<byte>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Converts one image, reusing an earlier conversion where the file and the settings are
    /// unchanged. Compressing is the slowest thing an update does — BC7 on a 4096×4096 image runs the
    /// better part of a minute across every core — so the progress line carries a percentage while it
    /// works, and the cache means only new or edited images pay for it again. The result is handed
    /// back as the cache's file, so a large texture is not held in memory until it is written.
    ///
    /// A .tex (or .atex) is already in the game's format and is taken as it is. A TGA is decoded to a
    /// PNG first, since neither converter reads TGA itself.
    /// </summary>
    private bool ConvertTexture(string sourcePath, TextureCompression compression, string label,
        out Content content, out string error, out bool cached)
    {
        cached = false;
        content = null!;
        if (TextureSources.IsTex(sourcePath))
        {
            if (!TexHeader.TryRead(sourcePath, out _))
            {
                error = "not a texture the game can read (its header is not a .tex header)";
                return false;
            }
            content = Content.FromFile(sourcePath);
            error = string.Empty;
            return true;
        }

        // Penumbra and this plugin's own encoder do not produce the same bytes, so which one made an
        // entry is part of what identifies it.
        bool native = _req.ConvertTexture != null && compression != TextureCompression.None;
        var key = TextureCache.KeyFor(sourcePath, compression, native ? "penumbra" : "managed");
        if (_textures.PathOf(key) is { } hit)
        {
            content = Content.FromFile(hit);
            error   = string.Empty;
            cached  = true;
            return true;
        }

        string? decoded = null;
        try
        {
            var input = sourcePath;
            if (TextureSources.NeedsDecoding(sourcePath))
            {
                decoded = TextureSources.DecodeToPng(sourcePath, out error);
                if (decoded == null)
                    return false;
                input = decoded;
            }

            if (native)
            {
                _job.Report(new WriteProgress(_done - 1, _total, $"{label} — compressing"));
                var scratch = Path.Combine(Path.GetTempPath(), $"instant-edit-{Guid.NewGuid():N}.tex");
                try
                {
                    // Not cut short when a newer change comes along: the conversion is kept, and
                    // the next update most likely needs it too.
                    var failure = _req.ConvertTexture!(input, compression, scratch, _stop);
                    if (failure == null)
                    {
                        content = Keep(key, scratch);
                        error   = string.Empty;
                        return true;
                    }

                    // Not fatal: the encoder bundled here can do the same job, only slower.
                    StudioServices.Log.Information("[Studio] Penumbra did not convert {0} ({1}); compressing it here instead", sourcePath, failure);
                }
                finally
                {
                    try { File.Delete(scratch); } catch (Exception) { /* a temp file that outlives us is harmless */ }
                }
            }

            var progress = new Progress<float>(fraction =>
                _job.Report(new WriteProgress(_done - 1, _total, $"{label} — compressing {fraction:P0}")));

            if (!TextureConverter.Convert(input, compression, out var data, out error, compression == TextureCompression.None ? null : progress))
                return false;

            content = Keep(key, data);
            return true;
        }
        finally
        {
            if (decoded != null)
            {
                try { File.Delete(decoded); } catch (Exception) { /* harmless */ }
            }
        }
    }

    /// <summary>Puts a conversion into the cache, handing back its file there (or the bytes, with no cache to keep them in).</summary>
    private Content Keep(string key, byte[] data)
        => _textures.Store(key, data) is { } path ? Content.FromFile(path) : Content.Of(data);

    /// <summary>Moves a converted file into the cache, handing back its file there (or its bytes, when it cannot be moved).</summary>
    private Content Keep(string key, string file)
        => _textures.Adopt(key, file) is { } path ? Content.FromFile(path) : Content.Of(File.ReadAllBytes(file));

    /// <summary>The report line for a converted texture: where it came from, how it was written.</summary>
    private static string DescribeTexture(string sourcePath, TextureCompression compression, bool cached)
    {
        var sourceName = Path.GetFileName(sourcePath);
        if (TextureSources.IsTex(sourcePath))
            return $"{sourceName}, copied as it is";

        var format = compression switch
        {
            TextureCompression.Bc7 => ", BC7",
            TextureCompression.Bc3 => ", BC3",
            TextureCompression.Bc5 => ", BC5",
            _                      => string.Empty,
        };
        return $"{sourceName}{format}{(cached ? " (converted before, reused)" : string.Empty)}";
    }

    /// <summary>
    /// A slot's placeholder: the generated white square, or a bundled ready-made .tex taken as it
    /// is — see <see cref="DummyTextureLibrary"/>.
    /// </summary>
    private bool DummyTexture(TextureSlot tex, out Content content, out string error, out string detail)
    {
        var option = DummyTextureLibrary.Resolve(tex.Type, tex.DummyPreset);
        error = string.Empty;
        content = null!;

        if (option.IsGenerated)
        {
            var compression = TextureCompressionInfo.ForPlaceholder(tex.Compression);
            var dummyKey = TextureCache.KeyForGenerated("white", tex.WhiteDummySize, compression);
            var cached = _textures.Get(dummyKey);
            if (cached == null && TextureConverter.CreateWhiteDummy(tex.WhiteDummySize, compression, out cached, out error))
                _textures.Store(dummyKey, cached);

            detail = $"white {tex.WhiteDummySize}x{tex.WhiteDummySize} dummy";
            if (cached == null)
                return false;
            content = Content.Of(cached);
            return true;
        }

        var fullPath = Path.Combine(_req.PluginDirectory, option.ResourcePath!);
        detail = $"{option.Label} (bundled dummy)";
        if (!File.Exists(fullPath))
        {
            error = $"bundled dummy texture missing ({fullPath})";
            return false;
        }

        content = Content.FromFile(fullPath);
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Committing
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes what changed into the mod, then folds it into meta.json — read again first, since
    /// Penumbra (or this plugin's own window) may have changed it while the files were made — and
    /// saves the new ledger into the sidecar. Not cancellable: once the first file is written, the
    /// ledger has to say so.
    /// </summary>
    private void Commit()
    {
        _job.BeginCommit();
        _total = _done + _files.Count(p => p.State == PlanState.Write) + 1;

        foreach (var plan in _files)
        {
            if (plan.State == PlanState.Write)
                Step($"Writing {plan.Label}");
            if (CommitFile(plan) is { } output)
                _output.Files.Add(output);
        }

        Step("meta.json");
        MergeResult merge;
        lock (ModFiles.Gate)
        {
            // Renamed or deleted in Penumbra while the files were written: never write a meta.json
            // (and so a mod folder) back where the mod no longer is.
            EnsureModFolder();
            try
            {
                var meta = PenumbraModMeta.Load(_modPath);
                merge = ModMetaMerger.Apply(meta, _output, _previous);
                if (meta.Revision > 0)
                {
                    meta.Save(_modPath, backupFirst: !_req.CreatedHere);
                    _report.MetaWritten = true;
                }
            }
            catch (Exception) when (Directory.Exists(_modPath))
            {
                // The files are written, but meta.json wasn't changed: the ledger still has to say
                // what is on disk now, or the next update takes these files for someone else's.
                SaveUnmergedLedger();
                throw;
            }

            var ledger = merge.Ledger;
            if (StudioProjectStore.SameLedger(ledger, _previous))
                ledger = _previous;
            else
                StudioProjectStore.SaveLedger(_modPath, ledger, _req.Sidecar);
            _report.NewLedger = ledger;
        }

        _report.Deleted   = DeleteStale(merge.StaleFiles);
        _report.Retracted = merge.Retracted;
    }

    /// <summary>
    /// The ledger after files were written but meta.json couldn't be merged: every file as it is on
    /// disk now, with the entries that were to be pointed at them marked as not pointed yet (the next
    /// update points them), and the last update's files no replacement asks for any more kept, so the
    /// next update still takes them back out. Under <see cref="ModFiles.Gate"/>.
    /// </summary>
    private void SaveUnmergedLedger()
    {
        try
        {
            var ledger = UnmergedLedger(_output, _previous);
            StudioProjectStore.SaveLedger(_modPath, ledger, _req.Sidecar);
            _report.NewLedger = ledger;
        }
        catch (Exception ex)
        {
            StudioServices.Log.Warning(ex, "[Studio] Could not record what was written into {0}", _req.ModFolderName);
        }
    }

    internal static WriteLedger UnmergedLedger(WriteOutput output, WriteLedger previous)
    {
        var ledger = new WriteLedger { UpdatedAt = DateTime.UtcNow };
        var produced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in output.Files)
        {
            var entry = file.Ledger.Clone();
            if (file.PointEntry)
                entry.EntryNotPointed = true;
            ledger.Files.Add(entry);
            produced.Add(WriteConflict.KeyFor(file.Option, file.GamePath));
        }
        foreach (var old in previous.Files)
            if (!produced.Contains(WriteConflict.KeyFor(OptionRef.Parse(old.Option), old.GamePath)))
                ledger.Files.Add(old.Clone());
        return ledger;
    }

    /// <summary>
    /// Writes one planned file, or decides not to: what the ledger records for it afterwards, and
    /// whether its entry in meta.json is pointed at it. Null when the mod has nothing of it.
    /// </summary>
    private OutputFile? CommitFile(Planned plan)
    {
        var previous = plan.Previous;
        switch (plan.State)
        {
            case PlanState.KeepPrevious:
                return Output(plan, previous!.Clone(), pointEntry: false);

            case PlanState.Unchanged:
            {
                _report.Unchanged++;
                var entry = previous!.Clone();
                entry.Intent   = plan.Intent;
                entry.Material = plan.Material?.ToLedger() ?? entry.Material;
                // Written last time, but meta.json couldn't be pointed at it then: it is now.
                bool point = entry.EntryNotPointed;
                entry.EntryNotPointed = false;
                return Output(plan, entry, pointEntry: point);
            }
        }

        var full = Disk(plan.RelPath);
        var content = plan.Content!;
        var key = WriteConflict.KeyFor(plan.Option, plan.GamePath);
        try
        {
            var onDisk = HashIfExists(full);
            if (onDisk is { Length: 0 })
            {
                Entry(plan, WriteOutcome.Failed, $"it could not be read to compare (is it open elsewhere?), so it was left alone{(previous != null ? " — the mod keeps the one it has" : string.Empty)}");
                return previous != null ? Output(plan, previous.Clone(), pointEntry: false) : null;
            }

            bool alreadyThere = onDisk != null && SameHash(onDisk, content.Sha256);

            if (onDisk != null && !alreadyThere && previous == null)
            {
                // Placed where nothing was, and something appeared there meanwhile: placed anew next time.
                Entry(plan, WriteOutcome.Failed, $"a file appeared at {plan.RelPath} while the mod was updated — it is written next time");
                return null;
            }

            if (onDisk != null && !alreadyThere && ChangedElsewhere(previous!, onDisk))
            {
                bool chosen = _req.Resolutions.TryGetValue(key, out var choice);
                if (!chosen || choice != ConflictChoice.UseMine)
                    return KeepChanged(plan, previous!, full, onDisk, key, chosen);
            }

            EnsureModFolder();
            if (WriteAtomically(full, content, onDisk))
                _report.FilesWritten++;
            var written = Written(plan, content.Sha256, full);
            written.OriginalRelPath = previous?.OriginalRelPath;
            Entry(plan, WriteOutcome.Ok, plan.Detail);
            return Output(plan, written, pointEntry: true);
        }
        catch (Exception ex)
        {
            TryDelete(full + ".instant-edit-tmp");
            Entry(plan, WriteOutcome.Failed, $"could not be written ({ex.Message}){(previous != null ? " — the mod keeps the one it has" : string.Empty)}");
            return previous != null ? Output(plan, previous.Clone(), pointEntry: false) : null;
        }
    }

    /// <summary>
    /// A file this plugin wrote was changed elsewhere since, and the replacement here now asks for
    /// something else in it. A material made here gets the texture changes made here carried over
    /// onto the changed copy. Anything else stays as it was changed until the user picks a side —
    /// unless they already said to keep it, which makes the replacement here leave it alone until it
    /// changes again.
    /// </summary>
    private OutputFile? KeepChanged(Planned plan, LedgerFile previous, string full, string onDisk, string key, bool keepTheirs)
    {
        if (keepTheirs)
        {
            var kept = previous.Clone();
            kept.Intent          = plan.Intent;
            kept.EditedElsewhere = true;
            kept.Material        = plan.Material?.ToLedger() ?? kept.Material;
            Entry(plan, WriteOutcome.Skipped, "left as it was changed elsewhere, as you chose");
            return Output(plan, kept, pointEntry: false);
        }

        string reason;
        if (plan.Material != null)
        {
            if (TryMerge(plan, full, previous, out var merged, out reason))
            {
                var content = Content.Of(merged);
                EnsureModFolder();
                if (WriteAtomically(full, content, onDisk))
                    _report.FilesWritten++;
                var entry = Written(plan, content.Sha256, full);
                entry.EditedElsewhere = true;
                entry.OriginalRelPath = previous.OriginalRelPath;
                Entry(plan, WriteOutcome.Merged, reason);
                return Output(plan, entry, pointEntry: true);
            }
        }
        else
        {
            reason = "changed elsewhere since it was written here, so the change made here was not written over it";
        }

        _report.Conflicts.Add(new WriteConflict(key, plan.GamePath, plan.Option, plan.RelPath, plan.Label, plan.Source, reason));
        Entry(plan, WriteOutcome.Kept, reason);
        var held = previous.Clone();
        held.EditedElsewhere = true;
        return Output(plan, held, pointEntry: false);
    }

    /// <summary>
    /// Carries the material changes made here over onto a copy changed elsewhere: only the textures
    /// whose path changed here since the material was last written, so a texture repointed in
    /// Penumbra stays repointed, and everything else of the changed copy — its colour table,
    /// constants, shader keys — stays as it is. A change of shader cannot be carried over.
    /// </summary>
    private static bool TryMerge(Planned plan, string full, LedgerFile previous, out byte[] merged, out string note)
    {
        var recipe = plan.Material!;
        merged = Array.Empty<byte>();

        MtrlInfo theirs;
        try
        {
            theirs = MtrlReader.Read(File.ReadAllBytes(full));
        }
        catch (Exception ex)
        {
            note = $"changed elsewhere, and the changed copy could not be read ({ex.Message}), so the change made here was not written over it";
            return false;
        }

        var before = previous.Material;
        var shaderBefore = before?.Shader ?? theirs.ShaderPackageName;
        if (!string.Equals(recipe.Shader, shaderBefore, StringComparison.OrdinalIgnoreCase))
        {
            note = $"changed elsewhere, and its shader was changed here ({recipe.Shader}), which cannot be carried over onto the changed copy";
            return false;
        }

        var changes = new Dictionary<TextureType, string>();
        foreach (var (type, path) in recipe.Textures)
            if (before == null || !before.Textures.TryGetValue(type.ToString(), out var was) || !PathNormalizer.SameGamePath(was, path))
                changes[type] = path;

        var dropped = before?.Textures.Keys.Where(k => !recipe.Textures.Keys.Any(t => t.ToString() == k)).ToList() ?? new List<string>();
        var droppedNote = dropped.Count > 0 ? $"; its {string.Join(", ", dropped)} texture stays in the changed copy" : string.Empty;

        if (changes.Count == 0)
        {
            merged = File.ReadAllBytes(full);
            note = $"changed elsewhere — kept as it is{droppedNote}";
            return true;
        }

        if (!MaterialPatcher.TryBuildPatchedMaterial(theirs, changes, isPreset: false, out merged, out _, out var error))
        {
            note = $"changed elsewhere, and the texture changes made here could not be carried over onto it ({error})";
            return false;
        }

        note = $"changed elsewhere — kept, with {string.Join(", ", changes.Keys)} set as here{droppedNote}";
        return true;
    }

    /// <summary>
    /// Whether this plugin's file was changed since it wrote it. A file written by a version that
    /// kept no hash is taken as this plugin's own.
    /// </summary>
    private static bool ChangedElsewhere(LedgerFile previous, string onDisk)
    {
        if (previous.EditedElsewhere)
            return true;
        if (previous.Sha256.Length == 0)
            return false;
        return !SameHash(previous.Sha256, onDisk);
    }

    /// <summary>Stops writing when the mod folder is gone (renamed or deleted in Penumbra meanwhile), rather than creating it again.</summary>
    private void EnsureModFolder()
    {
        if (!Directory.Exists(_modPath))
            throw new DirectoryNotFoundException("the mod's folder is gone (renamed or deleted in Penumbra?)");
    }

    /// <summary>What the ledger records for a file written now.</summary>
    private static LedgerFile Written(Planned plan, string sha, string full)
    {
        var info = new FileInfo(full);
        return new LedgerFile
        {
            GamePath     = plan.GamePath,
            RelPath      = plan.RelPath,
            Option       = plan.Option.Serialize(),
            Sha256       = sha,
            Intent       = plan.Intent,
            Length       = info.Exists ? info.Length : -1,
            WrittenTicks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0,
            Material     = plan.Material?.ToLedger(),
        };
    }

    private static OutputFile Output(Planned plan, LedgerFile ledger, bool pointEntry) => new()
    {
        GamePath   = plan.GamePath,
        RelPath    = ledger.RelPath,
        Option     = plan.Option,
        Ledger     = ledger,
        PointEntry = pointEntry,
    };

    private void Entry(Planned plan, WriteOutcome outcome, string? detail)
        => _report.Add(outcome, plan.Label, plan.GamePath, detail, plan.Source);

    /// <summary>
    /// Writes a file through a temporary one moved into place, so neither the game nor Penumbra ever
    /// reads it half-written. Returns false when the file already held exactly these bytes.
    /// </summary>
    private static bool WriteAtomically(string path, Content content, string? onDiskSha)
    {
        if (onDiskSha != null && SameHash(onDiskSha, content.Sha256))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".instant-edit-tmp";
        if (content.Bytes != null)
        {
            File.WriteAllBytes(tmp, content.Bytes);
        }
        else
        {
            File.Copy(content.File!, tmp, overwrite: true);
            File.SetLastWriteTimeUtc(tmp, DateTime.UtcNow);   // a copy keeps its source's timestamp; this file is new
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(40 * (attempt + 1));   // the game or Penumbra is reading it right now
            }
        }
    }

    private static string? HashIfExists(string path)
    {
        try
        {
            return File.Exists(path) ? HashFile(path) : null;
        }
        catch (Exception)
        {
            return string.Empty;   // unreadable: matches nothing, so it is never taken for this plugin's own
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool SameHash(string a, string b) => a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // Left behind; harmless, and ignored by everything that lists the mod.
        }
    }

    /// <summary>
    /// Deletes files an earlier update wrote that nothing uses now — only while they are still
    /// exactly as written. One changed elsewhere stays.
    /// </summary>
    private int DeleteStale(IEnumerable<LedgerFile> stale)
    {
        int deleted = 0;
        foreach (var file in stale)
        {
            if (!PathNormalizer.IsSafeRel(file.RelPath) || file.Sha256.Length == 0 || file.EditedElsewhere)
                continue;
            var full = Disk(file.RelPath);
            if (!File.Exists(full) || HashIfExists(full) is not { } sha || !SameHash(sha, file.Sha256))
                continue;
            try
            {
                File.Delete(full);
                deleted++;
                PruneEmptyFolders(Path.GetDirectoryName(full));
            }
            catch (Exception ex)
            {
                StudioServices.Log.Debug(ex, "[Studio] Could not delete unused {0}", full);
            }
        }
        return deleted;
    }

    private void PruneEmptyFolders(string? folder)
    {
        var root = Path.GetFullPath(_modPath).TrimEnd(Path.DirectorySeparatorChar);
        while (folder != null)
        {
            var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Length <= root.Length || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return;
            if (Directory.EnumerateFileSystemEntries(full).Any())
                return;
            Directory.Delete(full);
            folder = Path.GetDirectoryName(full);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Progress
    // ─────────────────────────────────────────────────────────────────────────

    private void Step(string current)
    {
        _job.Report(new WriteProgress(_done, _total, current));
        _done++;
    }

    /// <summary>Stops here if the update was cancelled; nothing has been written yet.</summary>
    private void Checkpoint() => _cancel.ThrowIfCancellationRequested();
}
