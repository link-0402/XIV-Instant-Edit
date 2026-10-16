using InstantEdit.Models;
using InstantEdit.Services.PreviewMods;

namespace InstantEdit.Services.Materials;

/// <summary>How an edit reaches the character: written into the loaded material at once, or through a redraw.</summary>
internal enum EditClass
{
    Live,
    Structural,
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

    private readonly Func<MaterialDocument, EditClass, Task> onEdit;

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
        var entry = PreviewModEntry.At(Source.GamePath, bytes);
        var created = await previews.CreateAsync($"Material Save - {Source.FileName}", "Material written by XIV Instant Edit.",
            [entry], Source.ObjectIndex, FallbackModName, CancellationToken.None).ConfigureAwait(false);
        var preview = new PreviewMod
        {
            Id = Guid.NewGuid(), ModDirectory = created.ModDirectory, ModIdentifier = created.Identifier, CollectionId = created.CollectionId,
            CollectionName = created.CollectionName, ActorName = Source.ActorName, ObjectIndex = (ushort)Source.ObjectIndex, Created = DateTimeOffset.UtcNow,
            Files =
            [
                new PreviewModFile
                {
                    Kind = "material", GamePath = Source.GamePath, PreviewRelativePath = entry.RelativePath, PreviewSha256 = PreviewSource.Hash(bytes),
                    Source = Source.ToPreviewSource(),
                },
            ],
        };
        var result = await previews.ApplyAsync(preview, newModName, "Material written by XIV Instant Edit.", FallbackModName, "Open the material again")
            .ConfigureAwait(false);
        if (Source.IsGameData && result.FixMod is { } mod && await previews.ModRootAsync().ConfigureAwait(false) is { Length: > 0 } root)
            Source = Source.SavedInto(root, mod, PreviewSource.Hash(bytes));
        else if (!Source.IsGameData)
            Source = Source with { Sha256 = PreviewSource.Hash(bytes) };
        if (ReferenceEquals(History.Current, saved))
            History.MarkSaved();
        return result;
    }

    /// <summary>Shows the current document through the temporary-mod preview and redraws the object.</summary>
    public async Task<string?> PreviewStructuralAsync(IMaterialPreview preview, CancellationToken token)
    {
        var warning = await preview.ShowAsync(Source.ObjectIndex, new Dictionary<string, byte[]> { [Source.GamePath] = Current.Write() }, token)
            .ConfigureAwait(false);
        PreviewShown = true;
        return warning;
    }

    /// <summary>Takes the temporary-mod preview down and redraws the object as it is without it.</summary>
    public async Task DiscardAsync(IMaterialPreview preview)
    {
        if (!PreviewShown)
            return;
        await preview.StopAsync(Source.ObjectIndex).ConfigureAwait(false);
        PreviewShown = false;
    }

    // ── Rules ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Structural when the game would load something else: another shader, other texture files or
    /// slots, a table of another size, or a key some texture slot depends on. Everything else
    /// (constants, other keys, flags, sampler settings, rows) is written into the loaded material.
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
            if ((before.KeyValue(id) ?? fallback) == (after.KeyValue(id) ?? fallback))
                continue;
            if (facts == null || facts.SamplerOnlyWhen.Values.Any(conditions => conditions.Any(condition => condition.Key == id)))
                return EditClass.Structural;
        }
        return EditClass.Live;
    }

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
