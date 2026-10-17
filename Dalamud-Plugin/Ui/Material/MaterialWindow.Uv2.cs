using Dalamud.Bindings.ImGui;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Materials;
using InstantEdit.Services.PreviewMods;

namespace InstantEdit.Ui.Material;

/// <summary>
/// The second UV map the scroll shader needs: the models the material is drawn with, whether their
/// meshes with it have one, and the Scrolling pattern option (off by default) that adds one copied from
/// the first. The change rides with the material: previewed with it, saved with it (backed up), and put
/// back by Undo last save.
/// </summary>
internal sealed partial class MaterialWindow
{
    private sealed record ModelUv2State(PreviewSource Model, byte[] Bytes, Uv2Check Check);

    private IReadOnlyList<PreviewSource> models = [];
    private Task<List<ModelUv2State>>? uv2Task;
    private List<ModelUv2State>? uv2;
    private bool addUv2;

    /// <summary>The models in the actor's tree that list the row's material, as save targets (hash filled in when read).</summary>
    private static IReadOnlyList<PreviewSource> ModelsFor(ResourceView row, ActorView actor)
    {
        var gamePath = PathRules.NormalizeGamePath(row.GamePath);
        return actor.Roots.SelectMany(root => root.Flattened)
            .Where(node => node.IsModel && node.Children.Any(child => string.Equals(PathRules.NormalizeGamePath(child.GamePath), gamePath, StringComparison.OrdinalIgnoreCase)))
            .Select(node => new PreviewSource
            {
                GamePath = PathRules.NormalizeGamePath(node.GamePath),
                ActualPath = node.ActualPath,
                State = node.SourceState,
                ModName = node.SourceState == ResourceSourceState.GameData ? string.Empty : node.SourceModName,
                ModDirectory = node.SourceState == ResourceSourceState.GameData ? string.Empty : node.SourceModDirectory,
                ModRootPath = node.SourceState == ResourceSourceState.GameData ? string.Empty : node.SourceModRootPath,
                RelativePath = node.SourceState == ResourceSourceState.LoadedMod ? node.SourceRelativePath.Replace('\\', '/') : string.Empty,
                ModStableId = node.SourceModStableId,
                Sha256 = string.Empty,
            })
            // One mod file at two game paths is one model to read, change and save.
            .DistinctBy(model => model.IsModFile ? model.ActualPath.ToLowerInvariant() : model.GamePath)
            .ToList();
    }

    /// <summary>Reads the models and checks their second UV map for this material, once per opened material.</summary>
    private void CheckModels(MaterialSession current)
    {
        // Ticked while a second UV map waits for the save, so applying the pattern again keeps it unless unticked.
        addUv2 = current.ModelChangesFor(current.Current).Count > 0;
        if (uv2Task != null || uv2 != null || models.Count == 0)
            return;
        var targets = models;
        var material = current.Source.FileName;
        uv2Task = Task.Run(async () =>
        {
            var states = new List<ModelUv2State>();
            foreach (var model in targets)
            {
                try
                {
                    var bytes = model.IsModFile
                        ? await File.ReadAllBytesAsync(model.ActualPath, life.Token).ConfigureAwait(false)
                        : (await data.GetFileAsync<Lumina.Data.FileResource>(model.GamePath, life.Token).ConfigureAwait(false))?.Data;
                    if (bytes != null)
                        states.Add(new ModelUv2State(model with { Sha256 = PreviewSource.Hash(bytes) }, bytes, ModelUv2.Check(bytes, material)));
                }
                // A damaged model is left out; the others still get checked.
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                {
                    log.Debug(e, "Could not read the model {Model} for the second UV map check.", model.ActualPath);
                }
            }
            return states;
        });
    }

    private void PollModels()
    {
        if (uv2Task is not { IsCompleted: true } done)
            return;
        uv2Task = null;
        uv2 = done.IsCompletedSuccessfully ? done.Result : [];
    }

    /// <summary>Forgets what was read of the models: after a save or restore their files changed.</summary>
    private void ForgetModels()
    {
        uv2 = null;
        // A check still running read the last material's models (or the files before this save): its result is dropped.
        uv2Task = null;
        addUv2 = false;
    }

    /// <summary>The models that lack the second UV map and could take one with this material's save.</summary>
    private List<ModelUv2State> Uv2Fixable(MaterialSession current)
        => Uv2Missing(current).Where(state => state.Check.CanAdd && (state.Model.IsModFile || current.Source.IsGameData)).ToList();

    /// <summary>The models whose file lacks the second UV map, those already given one for the save included (the box shows it ticked).</summary>
    private List<ModelUv2State> Uv2Missing(MaterialSession current)
        => uv2?.Where(state => state.Check.WithoutUv2 > 0).ToList() ?? [];

    /// <summary>Under the Scrolling pattern card: the second UV map option, or why the pattern will flash.</summary>
    private void DrawUv2Option(MaterialSession current)
    {
        if (uv2Task != null)
        {
            ImGui.TextColored(Theme.Muted, "Checking the model's second UV map");
            return;
        }
        if (uv2?.Any(state => current.HasModelChange(state.Model.GamePath)) == true && current.ModelChangesFor(current.Current).Count > 0)
            ImGui.TextColored(Theme.Muted, "The model has a new second UV map; Save writes it.");
        // A model whose meshes name this material otherwise (another race's skin), or that couldn't be read.
        foreach (var state in uv2?.Where(state => state.Check.Meshes == 0) ?? [])
            ImGui.TextColored(Theme.Muted, state.Check.Blocked is { } why
                ? $"{Path.GetFileName(state.Model.GamePath)}: its second UV map can't be checked. {why}"
                : $"{Path.GetFileName(state.Model.GamePath)}: no mesh names this material, so its second UV map can't be checked.");
        var missing = Uv2Missing(current);
        if (missing.Count == 0)
            return;
        var fixable = Uv2Fixable(current);
        if (fixable.Count > 0)
        {
            ImGui.Checkbox("Add a second UV map to the model##material-uv2", ref addUv2);
            Tip("The pattern follows the model's second UV map, which it doesn't have, so the part would flash instead. "
                + "This copies the first UV map into it. Save writes the model too, with a backup; Undo last save puts it back.");
        }
        foreach (var state in missing.Except(fixable))
            ImGui.TextColored(Theme.Warning, state.Check.Blocked is { } why
                ? $"No second UV map, so the pattern flashes. {why}"
                : "No second UV map, so the pattern flashes. Only a mod's model can get one here.");
    }

    /// <summary>After Scrolling pattern: adds the second UV maps when asked (a change), or says the part will flash (a warning).</summary>
    private (string? Change, string? Warning) ApplyUv2(MaterialSession current)
    {
        // Applied again without the box ticked: a second UV map added before goes too.
        var removed = !addUv2 && current.RemoveModelChanges();
        var fixable = Uv2Fixable(current);
        if (fixable.Count == 0 || !addUv2)
            return (removed ? "The model's new second UV map is left out again." : null, Uv2Missing(current).Count > 0
                ? "The model has no second UV map, so the part flashes instead of showing the pattern."
                  + (fixable.Count > 0 ? " Tick Add a second UV map and apply again." : string.Empty)
                : null);
        // Every model is changed or none: a failure on one leaves no change on the others.
        var changes = fixable.Select(state => (state.Model, ModelUv2.Add(state.Bytes, current.Source.FileName))).ToList();
        current.AddModelChanges(changes);
        var meshes = fixable.Sum(state => state.Check.WithoutUv2);
        // The change shows through the preview, even when the material itself didn't change.
        previewDue = Environment.TickCount64 + PreviewDelayMs;
        return ($"Added a second UV map to {(meshes == 1 ? "1 mesh" : $"{meshes} meshes")} of {string.Join(", ", fixable.Select(state => Path.GetFileName(state.Model.GamePath)))}. Save writes it.", null);
    }
}
