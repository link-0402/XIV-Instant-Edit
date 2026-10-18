using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services;
using InstantEdit.Services.Materials;
using InstantEdit.Services.Materials.Presets;
using InstantEdit.Services.Previews;

namespace InstantEdit.Ui.Material;

/// <summary>The Presets popup: one card per effect, with the rows it should change, and what each one did and still needs.</summary>
internal sealed partial class MaterialWindow
{
    private const string PresetsPopup = "##material-presets-popup";
    private static readonly string[] TargetLabels = ["All rows", "Used rows", "Selected pair", "Top row", "Bottom row"];

    private RowTarget presetTarget = RowTarget.Used;
    private bool adjustMask = true;
    private Task<DecodedTexture?>? maskTask;
    private string? maskFor;
    private DecodedTexture? mask;

    private void OpenPresets(MaterialSession current)
    {
        ReadMask(current.Current);
        CheckModels(current);
        ImGui.OpenPopup(PresetsPopup);
    }

    /// <summary>Decodes the mask texture for the Metal check, once per mask file.</summary>
    private void ReadMask(MaterialDocument doc)
    {
        var path = doc.Slots.FirstOrDefault(slot => slot.SamplerId == MaskSampler && slot.Path.Length > 0)?.Path;
        var file = path != null && resolvedTextures.TryGetValue(path, out var resolved) ? resolved : null;
        if (file == maskFor || maskTask != null)
            return;
        maskFor = file;
        mask = null;
        if (file == null)
            return;
        maskTask = Task.Run(async () =>
        {
            var bytes = Path.IsPathRooted(file)
                ? await File.ReadAllBytesAsync(file, life.Token).ConfigureAwait(false)
                : (await data.GetFileAsync<Lumina.Data.FileResource>(file, life.Token).ConfigureAwait(false))?.Data;
            return bytes == null ? null : TextureDecoder.Decode(bytes, 256);
        });
    }

    private void DrawPresets(MaterialSession current)
    {
        if (maskTask is { IsCompleted: true } decoded)
        {
            maskTask = null;
            mask = decoded.IsCompletedSuccessfully ? decoded.Result : null;
        }
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(420, 0), Theme.Scaled(560, 640));
        using var popup = ImRaii.Popup(PresetsPopup);
        if (!popup.Success)
            return;
        var doc = current.Current;
        if (ShaderPackages.Get(doc.ShaderPackage) is not { } facts)
        {
            ImGui.TextColored(Theme.Muted, "Presets need a character shader.");
            return;
        }
        var context = new PresetContext(doc, facts, usage, selectedPair, mask, current.Source.GamePath)
        {
            DyeTemplate = stains?.Templates.FirstOrDefault() ?? 0,
        };
        Widgets.SectionHeader("Presets");
        if (doc.Table is { Kind: ColorTableKind.Dawntrail })
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Label, "Rows to change");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Theme.Scaled(200));
            var target = (int)presetTarget;
            var pair = selectedPair + 1;
            var labels = TargetLabels.Select((label, i) => i switch
            {
                2 => $"Pair {pair}",
                3 => $"Pair {pair}/1",
                4 => $"Pair {pair}/2",
                _ => label,
            }).ToArray();
            if (ComboWheel.Combo("##preset-target", ref target, labels))
                presetTarget = (RowTarget)target;
            Tip((usage == null ? "Used rows needs the index texture; without it every row changes." : "Used rows are the ones the index texture picks.") +
                $"\nPair {pair}/1 is its top row (row {pair * 2 - 1}), {pair}/2 its bottom row (row {pair * 2}).");
        }
        var maskProblem = MaskAdjustProblem(doc);
        foreach (var preset in EffectPresets.All)
        {
            using var id = ImRaii.PushId(preset.Name);
            ImGui.Separator();
            var presetContext = preset.MaskEdit != null ? context with { AdjustsMask = adjustMask && maskProblem == null } : context;
            var applies = preset.Applies(presetContext);
            ImGui.TextColored(applies ? Theme.Text : Theme.Muted, preset.Name);
            ImGui.SameLine();
            ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - Theme.Scaled(70)));
            using (ImRaii.Disabled(!applies || working != null))
            {
                if (ImGui.Button("Apply", Theme.Scaled(70, 0)))
                {
                    ApplyPreset(current, preset, presetContext);
                    ImGui.CloseCurrentPopup();
                }
            }
            if (!applies)
                Tip(preset.Requirement);
            else if (preset.Name == "Clean up" && ImGui.IsItemHovered())
                Tip(string.Join("\n", preset.Apply(presetContext, presetTarget).Changes));
            ImGui.TextColored(Theme.Hint, preset.Description);
            if (preset.Name == ScrollingPattern)
                DrawUv2Option(current);
            if (preset.MaskEdit != null && doc.Table is { Kind: ColorTableKind.Dawntrail })
                DrawAdjustMask(maskProblem);
        }
    }

    private void DrawAdjustMask(string? problem)
    {
        using (ImRaii.Disabled(problem != null))
        {
            var on = adjustMask && problem == null;
            if (ImGui.Checkbox("Adjust mask", ref on))
                adjustMask = on;
        }
        Tip(problem ?? "Also paints the mask where these rows show: full shine (red) and the rows' own roughness (green at 50%).\n" +
            "The mask is copied beside the old one, which stays as it is.");
    }

    /// <summary>Why the mask can't be painted for this material, or null.</summary>
    private static string? MaskAdjustProblem(MaterialDocument doc)
        => !doc.Slots.Any(slot => slot.SamplerId == MaskSampler && slot.Path.Length > 0) ? "Needs a mask texture."
            : !doc.Slots.Any(slot => slot.SamplerId == IndexSampler && slot.Path.Length > 0) ? "Needs an index texture, which says where the rows show."
            : null;

    /// <summary>
    /// Applies a preset with its mask edit: reads the mask and the index texture at full size, paints the
    /// mask where the target rows show, and writes it to a new path the mask slot then reads, all one undo
    /// step with the row edits. A new path each time, so undo brings back the mask as it was. When the mask
    /// can't be painted the rows still change, with a warning.
    /// </summary>
    private void ApplyPresetWithMask(MaterialSession current, MaterialEffectPreset preset, PresetContext context, MaskEdit edit)
    {
        var target = presetTarget;
        var doc = context.Doc;
        var maskPath = doc.Slots.First(slot => slot.SamplerId == MaskSampler && slot.Path.Length > 0).Path;
        var indexPath = doc.Slots.First(slot => slot.SamplerId == IndexSampler && slot.Path.Length > 0).Path;
        var (rows, _) = PresetChecks.Rows(context, target);
        var source = current.Source;
        string? Resolved(string path) => OwnTextureFile(source, path) ?? (resolvedTextures.TryGetValue(path, out var found) ? found : path);
        var (maskFile, indexFile) = (Resolved(maskPath), Resolved(indexPath));
        byte[]? painted = null;
        var paintedPath = maskPath;
        Run(async () =>
        {
            var maskBytes = current.NewFiles.TryGetValue(maskPath, out var made) ? made : await ReadTextureAsync(maskFile).ConfigureAwait(false);
            if (maskBytes == null)
                return "The mask couldn't be read, so it wasn't changed.";
            var indexBytes = current.NewFiles.TryGetValue(indexPath, out var madeIndex) ? madeIndex : await ReadTextureAsync(indexFile).ConfigureAwait(false);
            if (indexBytes == null && rows.Length < ColorTable.DtRowCount)
                return "The index texture couldn't be read, so the mask wasn't changed.";
            try
            {
                var decodedMask = TextureDecoder.Decode(maskBytes, int.MaxValue);
                var (bgra, changed) = MaskPainting.Paint(decodedMask, indexBytes == null ? null : TextureDecoder.Decode(indexBytes, int.MaxValue), rows, edit);
                if (changed == 0)
                    return null;
                painted = TextureFiles.BgraTexWithMips(bgra, decodedMask.Width, decodedMask.Height);
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException)
            {
                return "The mask or index texture has a format that can't be read, so the mask wasn't changed.";
            }
            paintedPath = MaterialSession.FreeTexturePath(maskPath, path => TextureTaken(current, source, path));
            return null;
        }, warning =>
        {
            PresetResult result;
            try
            {
                result = preset.Apply(context with { Doc = current.Current }, target);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or InvalidDataException)
            {
                return $"{preset.Name} couldn't be applied: {e.Message}";
            }
            if (painted != null)
                current.AddNewFile(paintedPath, painted);
            _ = current.Apply(_ => painted == null ? result.Doc : PresetChecks.WithSamplerPath(result.Doc, MaskSampler, paintedPath), preset.Name);
            var parts = new List<string> { $"{preset.Name}:" };
            parts.AddRange(result.Changes);
            parts.Add(painted != null ? $"The mask is painted where they show ({paintedPath})."
                : warning == null ? "The mask already had these values there." : "");
            parts.AddRange(result.Warnings);
            if (warning != null)
                parts.Add(warning);
            return string.Join(" ", parts.Where(part => part.Length > 0));
        });
    }

    private const string CleanUpPopup = "Clean up##material-cleanup";
    private PresetResult? pendingCleanUp;
    private bool openCleanUp;

    /// <summary>Works out what Clean up would remove and asks first.</summary>
    private void StartCleanUp(MaterialSession current)
    {
        var doc = current.Current;
        if (ShaderPackages.Get(doc.ShaderPackage) is not { } facts)
            return;
        var context = new PresetContext(doc, facts, usage, selectedPair, mask, current.Source.GamePath);
        pendingCleanUp = EffectPresets.All.Single(preset => preset is { Name: "Clean up" }).Apply(context, RowTarget.All);
        openCleanUp = true;
    }

    private void DrawCleanUpPopup(MaterialSession current)
    {
        if (openCleanUp)
        {
            openCleanUp = false;
            ImGui.OpenPopup(CleanUpPopup);
        }
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(400, 0), Theme.Scaled(620, 520));
        if (!ImGui.BeginPopupModal(CleanUpPopup, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        var result = pendingCleanUp;
        var nothing = result == null || ReferenceEquals(result.Doc, current.Current) || MaterialDocument.Same(result.Doc, current.Current);
        Widgets.SectionHeader(nothing ? "Nothing to clean up" : "Clean up");
        if (result != null && !nothing)
            foreach (var change in result.Changes)
                ImGui.BulletText(change);
        ImGui.Spacing();
        using (ImRaii.Disabled(nothing))
        {
            if (ImGui.Button("Clean up##cleanup-apply") && result != null)
            {
                _ = current.Apply(_ => result.Doc, "Clean up");
                Say(FeedbackSeverity.Success, $"Cleaned up: {result.Changes.Count} setting{(result.Changes.Count == 1 ? "" : "s")}. Undo takes it back.");
                pendingCleanUp = null;
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button(nothing ? "Close##cleanup-cancel" : "Cancel##cleanup-cancel"))
        {
            pendingCleanUp = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void ApplyPreset(MaterialSession current, MaterialEffectPreset preset, PresetContext context)
    {
        if (context.AdjustsMask && preset.MaskEdit is { } edit)
        {
            ApplyPresetWithMask(current, preset, context, edit);
            return;
        }
        PresetResult result;
        try
        {
            result = preset.Apply(context, preset.NeedsRows ? presetTarget : RowTarget.All);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or InvalidDataException)
        {
            Say(FeedbackSeverity.Error, $"{preset.Name} couldn't be applied: {e.Message}");
            return;
        }
        foreach (var texture in result.NewTextures)
            current.AddNewFile(texture.GamePath, texture.Bytes);
        List<string> changes = [.. result.Changes];
        List<string> warnings = [.. result.Warnings];
        if (preset.Name == ScrollingPattern)
        {
            try
            {
                var (change, warning) = ApplyUv2(current);
                if (change != null) changes.Add(change);
                if (warning != null) warnings.Add(warning);
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or InvalidOperationException or OverflowException)
            {
                warnings.Add($"The second UV map couldn't be added: {e.Message}");
            }
        }
        _ = current.Apply(_ => result.Doc, preset.Name);
        var text = $"{preset.Name}: " + string.Join(" ", changes);
        if (warnings.Count > 0)
            text += " " + string.Join(" ", warnings);
        Say(warnings.Count > 0 ? FeedbackSeverity.Warning : FeedbackSeverity.Success, text);
    }

    private const string ScrollingPattern = "Scrolling pattern";
}
