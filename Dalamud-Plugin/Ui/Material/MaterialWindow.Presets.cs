using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Materials;
using InstantEdit.Services.Materials.Presets;
using InstantEdit.Services.Previews;

namespace InstantEdit.Ui.Material;

/// <summary>The Presets popup: one card per effect, with the rows it should change, and what each one did and still needs.</summary>
internal sealed partial class MaterialWindow
{
    private const string PresetsPopup = "##material-presets-popup";
    private static readonly string[] TargetLabels = ["All rows", "Used rows", "Selected pair"];

    private RowTarget presetTarget = RowTarget.Used;
    private Task<DecodedTexture?>? maskTask;
    private string? maskFor;
    private DecodedTexture? mask;

    private void OpenPresets(MaterialSession current)
    {
        ReadMask(current.Current);
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
            var labels = TargetLabels.Select((label, i) => i == 2 ? $"Pair {selectedPair + 1}" : label).ToArray();
            if (ImGui.Combo("##preset-target", ref target, labels, labels.Length))
                presetTarget = (RowTarget)target;
            Tip(usage == null ? "Used rows needs the index texture; without it every row changes." : "Used rows are the ones the index texture picks.");
        }
        foreach (var preset in EffectPresets.All)
        {
            using var id = ImRaii.PushId(preset.Name);
            ImGui.Separator();
            var applies = preset.Applies(context);
            ImGui.TextColored(applies ? Theme.Text : Theme.Muted, preset.Name);
            ImGui.SameLine();
            ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - Theme.Scaled(70)));
            using (ImRaii.Disabled(!applies || working != null))
            {
                if (ImGui.Button("Apply", Theme.Scaled(70, 0)))
                {
                    ApplyPreset(current, preset, context);
                    ImGui.CloseCurrentPopup();
                }
            }
            if (!applies)
                Tip(preset.Requirement);
            else if (preset.Name == "Clean up" && ImGui.IsItemHovered())
                Tip(string.Join("\n", preset.Apply(context, presetTarget).Changes));
            ImGui.TextColored(Theme.Hint, preset.Description);
        }
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
        Widgets.SectionHeader(nothing ? "Nothing to clean up" : "Clean up removes");
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
        _ = current.Apply(_ => result.Doc, preset.Name);
        var text = $"{preset.Name}: " + string.Join(" ", result.Changes);
        if (result.Warnings.Count > 0)
            text += " " + string.Join(" ", result.Warnings);
        Say(result.Warnings.Count > 0 ? FeedbackSeverity.Warning : FeedbackSeverity.Success, text);
    }
}
