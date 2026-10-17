using InstantEdit.Models;
using InstantEdit.Services.PreviewMods;

namespace InstantEdit.Services.Materials;

/// <summary>How an edit reaches the character: written into the loaded material at once, or through a redraw.</summary>
internal enum EditClass
{
    Live,
    Structural,
}

/// <summary>What one live edit has to write into the loaded material.</summary>
internal sealed record LiveDiff(uint[] Constants, uint[] Keys, bool Flags, uint[] Samplers, bool Table)
{
    public bool Empty => Constants.Length == 0 && Keys.Length == 0 && !Flags && Samplers.Length == 0 && !Table;
}

/// <summary>Shows edited files on a character through a temporary mod and a redraw (the structural preview).</summary>
internal interface IMaterialPreview
{
    /// <summary>Shows <paramref name="files"/> (game path to bytes) on the object's collection and redraws it; a warning, or null.</summary>
    Task<string?> ShowAsync(int objectIndex, IReadOnlyDictionary<string, byte[]> files, CancellationToken token);

    /// <summary>Removes the preview; with an object index, redraws it without the preview.</summary>
    Task StopAsync(int? redrawObject);
}

/// <summary>
/// One open material: where it came from, its undo history, and the rules for what an edit needs:
/// value edits are shown live, edits that change what the game loads need a redraw. Saving goes
/// through the preview-mod flow (hash check, backups, a new mod for game files). No ImGui or
/// Dalamud dependencies.
/// </summary>
internal sealed class MaterialSession
{
    public const string FallbackModName = "Material Edit";
    private const uint IndexSampler = 0x565F8FD8;

    public const string ScrollShader = "characterscroll.shpk";

    private readonly Func<MaterialDocument, EditClass, Task> onEdit;
    private readonly Dictionary<string, byte[]> newFiles = new(StringComparer.OrdinalIgnoreCase);
    // With: the document the change came with; undoing past it takes the change out of the save.
    private readonly Dictionary<string, (PreviewSource Model, byte[] Bytes, MaterialDocument With)> modelChanges = new(StringComparer.OrdinalIgnoreCase);

    public MaterialSession(MaterialSource source, MaterialDocument initial, Func<MaterialDocument, EditClass, Task> onEdit)
    {
        Source = source;
        History = new MaterialHistory(initial);
        this.onEdit = onEdit;
    }

    public static MaterialSession CreateForRegression(MaterialSource source, MaterialDocument initial, Func<MaterialDocument, EditClass, Task> onEdit)
        => new(source, initial, onEdit);

    /// <summary>The file the session reads and saves; after saving a game file it is the new mod's file.</summary>
    public MaterialSource Source { get; private set; }

    public MaterialHistory History { get; }

    public MaterialDocument Current => History.Current;

    /// <summary>A structural preview (temporary mod) is shown for this material.</summary>
    public bool PreviewShown { get; private set; }

    /// <summary>Textures a preset added, by game path; previewed and saved while a slot of the material reads them.</summary>
    public IReadOnlyDictionary<string, byte[]> NewFiles => newFiles;

    public void AddNewFile(string gamePath, byte[] bytes) => newFiles[PathRules.NormalizeGamePath(gamePath)] = bytes;

    /// <summary>The new files the document reads.</summary>
    public IReadOnlyList<(string GamePath, byte[] Bytes)> NewFilesFor(MaterialDocument doc)
        => newFiles.Where(file => doc.TexturePaths.Contains(file.Key, StringComparer.OrdinalIgnoreCase)).Select(file => (file.Key, file.Value)).ToList();

    /// <summary>
    /// A model the material is drawn with, changed for it: <paramref name="model"/> is the file as read
    /// (its hash guards the save), <paramref name="bytes"/> the changed file. Only a second UV map for the
    /// scroll shader is added this way, so the change goes with the material while it is on that shader.
    /// </summary>
    public void AddModelChange(PreviewSource model, byte[] bytes) => AddModelChanges([(model, bytes)]);

    /// <summary>
    /// Adds model changes together (see <see cref="AddModelChange"/>), going with the current document: undoing
    /// past it drops them from the save and redoing brings them back. One model file is changed once, whatever
    /// game paths load it.
    /// </summary>
    public void AddModelChanges(IReadOnlyList<(PreviewSource Model, byte[] Bytes)> changes)
    {
        foreach (var (model, bytes) in changes.DistinctBy(change => change.Model.IsModFile
                     ? change.Model.ActualPath.ToLowerInvariant() : PathRules.NormalizeGamePath(change.Model.GamePath)))
            modelChanges[PathRules.NormalizeGamePath(model.GamePath)] = (model, bytes, History.Current);
    }

    /// <summary>Drops every model change waiting for the save (Scrolling pattern applied again without the second UV map).</summary>
    public bool RemoveModelChanges()
    {
        var had = modelChanges.Count > 0;
        modelChanges.Clear();
        return had;
    }

    /// <summary>A change to the model at <paramref name="gamePath"/> waits for the save (and wasn't undone).</summary>
    public bool HasModelChange(string gamePath)
        => modelChanges.TryGetValue(PathRules.NormalizeGamePath(gamePath), out var change) && !History.Undone(change.With);

    /// <summary>The model changes the document needs: those for the scroll shader, while it is on it, whose step wasn't undone.</summary>
    public IReadOnlyList<(PreviewSource Model, byte[] Bytes)> ModelChangesFor(MaterialDocument doc)
        => doc.ShaderPackage.Equals(ScrollShader, StringComparison.OrdinalIgnoreCase)
            ? modelChanges.Values.Where(change => !History.Undone(change.With)).Select(change => (change.Model, change.Bytes)).ToList()
            : [];

    /// <summary>Applies an edit as one undo step (or part of the running gesture) and passes it on to the preview.</summary>
    public Task Apply(Func<MaterialDocument, MaterialDocument> edit, string label)
    {
        var before = History.Current;
        var after = edit(before);
        if (ReferenceEquals(after, before))
            return Task.CompletedTask;
        History.Push(after, label);
        return onEdit(after, Classify(before, after));
    }

    public Task Undo() => Step(History.Undo);

    public Task Redo() => Step(History.Redo);

    private Task Step(Func<bool> move)
    {
        var before = History.Current;
        return move() ? onEdit(History.Current, Classify(before, History.Current)) : Task.CompletedTask;
    }

    /// <summary>
    /// Writes the material over the mod's file (backed up, after checking the file is still the one
    /// opened), or into a new mod named <paramref name="newModName"/> for a game file; then redraws.
    /// </summary>
    public async Task<PreviewApplyResult> SaveAsync(PreviewModService previews, string newModName)
    {
        var saved = Current;
        var bytes = saved.Write();
        var added = NewFilesFor(saved);
        var models = ModelChangesFor(saved);
        var result = await previews.WriteAsync(SaveWrites(Source, bytes, added, models), Source.ObjectIndex, newModName, "Material written by XIV Instant Edit.",
            FallbackModName, "Open the material again").ConfigureAwait(false);
        if (Source.IsGameData && result.FixMod is { } mod && await previews.ModRootAsync().ConfigureAwait(false) is { Length: > 0 } root)
            Source = Source.SavedInto(root, mod, PreviewSource.Hash(bytes));
        else if (!Source.IsGameData)
            Source = Source with { Sha256 = PreviewSource.Hash(bytes) };
        foreach (var file in added)
            newFiles.Remove(file.GamePath);
        foreach (var (model, _) in models)
            modelChanges.Remove(PathRules.NormalizeGamePath(model.GamePath));
        if (ReferenceEquals(History.Current, saved))
            History.MarkSaved();
        return result;
    }

    /// <summary>
    /// Where a save writes: a mod material over its file (unless it already holds these bytes) with its
    /// new textures added to the same mod; a game material and its new textures into a new mod. A
    /// changed model goes over its own mod's file (backed up), or with a game material into its new mod.
    /// </summary>
    internal static PreviewWrites SaveWrites(MaterialSource source, byte[] bytes, IReadOnlyList<(string GamePath, byte[] Bytes)> added,
        IReadOnlyList<(PreviewSource Model, byte[] Bytes)>? models = null)
    {
        models ??= [];
        var modModels = models.Where(m => m.Model.IsModFile).Select(m => (m.Model, m.Bytes)).ToList();
        var gameModels = models.Where(m => !m.Model.IsModFile).ToList();
        if (gameModels.Count > 0 && !source.IsGameData)
            throw new InvalidOperationException("A game model can only change together with a game material, which goes into a new mod.");
        if (source.IsGameData)
            return new PreviewWrites(modModels, [], [PreviewModEntry.At(source.GamePath, bytes), .. added.Select(file => PreviewModEntry.At(file.GamePath, file.Bytes)),
                .. gameModels.Select(m => PreviewModEntry.At(m.Model.GamePath, m.Bytes))]);
        var target = source.ToPreviewSource();
        var unchanged = string.Equals(PreviewSource.Hash(bytes), target.Sha256, StringComparison.OrdinalIgnoreCase);
        return new PreviewWrites([.. unchanged ? [] : new[] { (target, bytes) }, .. modModels],
            added.Select(file => (target, PathRules.NormalizeGamePath(file.GamePath), file.Bytes)).ToList(), []);
    }

    /// <summary>
    /// A copy of <paramref name="saved"/> (<paramref name="bytes"/>, with the new textures it reads) went
    /// into another file of the same mod: Save as copy into an option. That file is the one the session
    /// saves from now on; the file it was opened from stays as it is.
    /// </summary>
    public void SavedAsCopy(MaterialDocument saved, string actualPath, string relativePath, byte[] bytes)
    {
        Source = Source with { ActualPath = actualPath, RelativePath = relativePath.Replace('\\', '/'), Sha256 = PreviewSource.Hash(bytes) };
        foreach (var file in NewFilesFor(saved))
            newFiles.Remove(file.GamePath);
        if (ReferenceEquals(History.Current, saved))
            History.MarkSaved();
    }

    /// <summary>Shows the current document through the temporary-mod preview and redraws the object.</summary>
    public async Task<string?> PreviewStructuralAsync(IMaterialPreview preview, CancellationToken token)
    {
        var doc = Current;
        var files = new Dictionary<string, byte[]> { [Source.GamePath] = doc.Write() };
        foreach (var (gamePath, bytes) in NewFilesFor(doc))
            files[gamePath] = bytes;
        foreach (var (model, bytes) in ModelChangesFor(doc))
            files[PathRules.NormalizeGamePath(model.GamePath)] = bytes;
        var warning = await preview.ShowAsync(Source.ObjectIndex, files, token).ConfigureAwait(false);
        PreviewShown = true;
        return warning;
    }

    /// <summary>Takes the temporary-mod preview down and, unless told not to, redraws the object as it is without it.</summary>
    public async Task DiscardAsync(IMaterialPreview preview, bool redraw = true)
    {
        if (!PreviewShown)
            return;
        await preview.StopAsync(redraw ? Source.ObjectIndex : null).ConfigureAwait(false);
        PreviewShown = false;
    }

    /// <summary>Puts the document back to how it was last saved or opened, as one undo step.</summary>
    public Task Revert() => Apply(_ => History.Saved, "Discard changes");

    /// <summary>Starts over from a document read again from the file, with no undo steps.</summary>
    public void Reload(MaterialSource source, MaterialDocument doc)
    {
        Source = source;
        History.Reset(doc);
    }

    // ── Rules ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Structural when the game would load something else: another shader, other texture files or
    /// slots, a table of another size, or any key (writing a loaded material's key values showed
    /// nothing in game). Everything else (constants, flags, sampler settings, rows) is written into
    /// the loaded material.
    /// </summary>
    public static EditClass Classify(MaterialDocument before, MaterialDocument after)
    {
        if (!string.Equals(before.ShaderPackage, after.ShaderPackage, StringComparison.OrdinalIgnoreCase)
            || before.Slots.Count != after.Slots.Count
            || !before.TexturePaths.SequenceEqual(after.TexturePaths, StringComparer.Ordinal)
            || TableShape(before.Table) != TableShape(after.Table))
            return EditClass.Structural;
        for (var i = 0; i < before.Slots.Count; i++)
            if (before.Slots[i].SamplerId != after.Slots[i].SamplerId || before.Slots[i].Path != after.Slots[i].Path || before.Slots[i].Dx11 != after.Slots[i].Dx11)
                return EditClass.Structural;

        var facts = ShaderPackages.Get(after.ShaderPackage);
        foreach (var id in before.Keys.Concat(after.Keys).Select(key => key.Id).Distinct())
        {
            var fallback = facts?.Key(id)?.Default;
            if ((before.KeyValue(id) ?? fallback) != (after.KeyValue(id) ?? fallback))
                return EditClass.Structural;
        }
        return EditClass.Live;
    }

    /// <summary>
    /// What a live edit writes into the loaded material: the constants whose values changed (a
    /// removed one goes back to the package default), the keys whose values changed, whether the
    /// shader flags changed, the samplers whose settings changed, and whether the colour table did.
    /// </summary>
    public static LiveDiff LiveDiff(MaterialDocument before, MaterialDocument after)
    {
        var constants = before.Constants.Select(constant => constant.Id).Union(after.Constants.Select(constant => constant.Id))
            .Where(id => !SameValues(before.Constant(id)?.Values, after.Constant(id)?.Values))
            .ToArray();
        var facts = ShaderPackages.Get(after.ShaderPackage);
        var keys = before.Keys.Select(key => key.Id).Union(after.Keys.Select(key => key.Id))
            .Where(id => (before.KeyValue(id) ?? facts?.Key(id)?.Default) != (after.KeyValue(id) ?? facts?.Key(id)?.Default))
            .ToArray();
        var samplers = before.Slots.Zip(after.Slots)
            .Where(pair => pair.First.SamplerId == pair.Second.SamplerId && pair.First.SamplerSettings != pair.Second.SamplerSettings)
            .Select(pair => pair.Second.SamplerId)
            .Distinct()
            .ToArray();
        var table = !ReferenceEquals(before.Table, after.Table) && (before.Table == null) == (after.Table == null)
                    && !before.Table!.WriteDataSet().AsSpan().SequenceEqual(after.Table!.WriteDataSet());
        return new LiveDiff(constants, keys, before.ShaderFlags != after.ShaderFlags, samplers, table);
    }

    private static bool SameValues(float[]? a, float[]? b)
        => a != null && b != null && MemoryMarshalBits(a).SequenceEqual(MemoryMarshalBits(b));

    private static ReadOnlySpan<byte> MemoryMarshalBits(float[] values) => System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan());

    private static (ColorTableKind, bool) TableShape(ColorTable? table)
        => table == null ? (ColorTableKind.None, false) : (table.Kind, table.Dye != null || table.LegacyDye != null);

    /// <summary>
    /// Fits a document to a shader package after a switch: drops keys the package lacks and values
    /// it has no shaders for, adds its keys at their defaults, drops texture slots it has no input
    /// for, and gives a colour-table package a table and an index slot. The notes say what changed.
    /// </summary>
    public static (MaterialDocument Doc, IReadOnlyList<string> Notes) Reconcile(MaterialDocument doc, ShaderPackageFacts facts)
    {
        var notes = new List<string>();
        var package = facts.Name;
        if ((doc.ShaderFlags & Presets.PresetChecks.Translucent) != 0 && Presets.PresetChecks.TranslucentCrashes(package))
        {
            doc = doc.WithFlags(doc.ShaderFlags & ~Presets.PresetChecks.Translucent);
            notes.Add($"Turned Translucent off: {Presets.PresetChecks.TranslucentCrashNote}");
        }
        foreach (var (id, value) in doc.Keys.ToList())
        {
            var key = facts.Key(id);
            if (key == null)
            {
                doc = doc.WithoutKey(id);
                notes.Add($"Removed {ShaderLabels.Key(id).Label}: {package} does not have it.");
            }
            else if (!key.Values.Contains(value))
            {
                doc = doc.WithKey(id, key.Default);
                notes.Add($"{ShaderLabels.Key(id).Label} is now {ShaderLabels.KeyValue(id, key.Default)}: {package} has no {ShaderLabels.KeyValue(id, value)}.");
            }
        }
        foreach (var key in facts.Keys)
            if (doc.KeyValue(key.Id) == null)
                doc = doc.WithKey(key.Id, key.Default);

        for (var i = doc.Slots.Count - 1; i >= 0; i--)
        {
            var slot = doc.Slots[i];
            if (facts.DeclaresSampler(slot.SamplerId))
                continue;
            doc = doc.WithoutSlot(i);
            notes.Add($"Removed the {ShaderLabels.Sampler(slot.SamplerId)} texture ({slot.Path}): {package} has no slot for it.");
        }

        if (!facts.SamplesColorTable)
        {
            if (doc.Table != null)
            {
                doc = doc.WithTable(null);
                notes.Add($"Removed the colour table: {package} does not use one.");
            }
            return (doc, notes);
        }

        if (doc.Table == null || doc.Table.Kind is ColorTableKind.Opaque)
        {
            doc = doc.WithTable(ColorTable.NewDawntrail(dyeable: false));
            notes.Add("Added a colour table with default rows.");
        }
        else if (doc.Table.Kind == ColorTableKind.Legacy && !package.Equals("characterlegacy.shpk", StringComparison.OrdinalIgnoreCase))
        {
            doc = doc.WithTable(doc.Table.ToDawntrail());
            notes.Add("Converted the colour table to Dawntrail rows.");
        }

        if (doc.Slots.All(slot => slot.SamplerId != IndexSampler))
        {
            var normal = doc.Slots.FirstOrDefault(slot => slot.SamplerId == MaterialDocument.NormalSampler && slot.Path.Length > 0);
            if (normal == null)
                notes.Add($"{package} needs an Index texture. Add one in Textures.");
            else
            {
                var path = IndexPathFor(normal.Path);
                doc = doc.WithAddedSlot(IndexSampler, path);
                notes.Add($"Added an Index texture slot ({path}). Make that texture if it does not exist.");
            }
        }
        return (doc, notes);
    }

    /// <summary>
    /// <paramref name="path"/>, or the first of …_ie.tex, …_ie2.tex and so on beside it, that <paramref name="taken"/>
    /// says nothing uses yet (a game file, a file the mod maps, a texture made this session).
    /// </summary>
    internal static string FreeTexturePath(string path, Func<string, bool> taken)
    {
        if (!taken(path))
            return path;
        var stem = path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ? path[..^".tex".Length] : path;
        for (var n = 1; n <= 99; n++)
        {
            var candidate = n == 1 ? stem + "_ie.tex" : $"{stem}_ie{n}.tex";
            if (!taken(candidate))
                return candidate;
        }
        throw new IOException("No free path for the new texture was found beside " + path + ".");
    }

    /// <summary>The index texture beside a normal map: …_norm.tex or …_n.tex becomes …_id.tex.</summary>
    internal static string IndexPathFor(string normalPath)
    {
        var slash = normalPath.LastIndexOf('/') + 1;
        var folder = normalPath[..slash];
        var stem = Path.GetFileNameWithoutExtension(normalPath[slash..]);
        foreach (var suffix in (string[])["_norm", "_n"])
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return folder + stem[..^suffix.Length] + "_id.tex";
        return folder + stem + "_id.tex";
    }
}
