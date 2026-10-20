using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Materials;
using InstantEdit.Studio.Services.Project;
using Penumbra.Api.Enums;

namespace InstantEdit.Ui.Material;

/// <summary>
/// Save as variant: the edited material goes into its mod as a new option of a Penumbra group made
/// for it (see <see cref="MaterialVariants"/>), and the character's collection is set to show it. The
/// file it was opened from stays as it is, and Save writes the variant from then on.
/// </summary>
internal sealed partial class MaterialWindow
{
    private const string VariantPopup = "Save as variant##material-variant";

    private string variantName = string.Empty;
    private string variantGroup = string.Empty;
    private bool variantNewGroup;
    private PenumbraModMeta? variantMeta;
    private bool openVariant;

    /// <summary>The dots menu's item: for any material of a mod, wherever it was opened from.</summary>
    private void DrawVariantMenuItem(MaterialSession current)
    {
        var block = current.Source.State != ResourceSourceState.LoadedMod || current.Source.ModRootPath.Length == 0
            ? "Save the material into a mod first. Then it can have variants."
            : copies?.Block;
        using (ImRaii.Disabled(Busy || block != null))
        {
            if (ImGui.MenuItem("Save as variant"))
                StartVariant(current);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(block ?? "Saves the edits as a new option of a Penumbra group for this material. The original stays as it is.");
    }

    private void StartVariant(MaterialSession current)
    {
        try
        {
            var meta = PenumbraModMeta.Load(current.Source.ModRootPath);
            var group = MaterialVariants.FindGroup(meta, current.Source.GamePath);
            variantMeta = meta;
            variantNewGroup = group == null;
            variantGroup = group?.Name ?? MaterialVariants.NewGroupName(meta, current.Source.GamePath);
            variantName = MaterialVariants.SuggestName(meta, current.Source.GamePath);
            openVariant = true;
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not read the mod of the material for a variant.");
            Say(FeedbackSeverity.Error, $"The mod's meta.json could not be read: {e.Message}");
        }
    }

    private void DrawVariantPopup(MaterialSession current)
    {
        if (openVariant)
        {
            openVariant = false;
            ImGui.OpenPopup(VariantPopup);
        }
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(440, 0), Theme.Scaled(640, 400));
        if (!ImGui.BeginPopupModal(VariantPopup, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        if (variantMeta is not { } meta)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        Widgets.SectionHeader("Save as a variant");
        Widgets.HintWrapped(variantNewGroup
            ? $"Makes the group \"{variantGroup}\" in {current.Source.ModName}, with \"{MaterialVariants.OriginalOption}\" for the material as it is and this variant beside it."
            : $"Adds this variant to the group \"{variantGroup}\" in {current.Source.ModName}.");
        Widgets.HintWrapped("The original file stays as it is. Your character is set to show the variant.");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        var enter = ImGui.InputTextWithHint("##material-variant-name", "Variant name", ref variantName, 120, ImGuiInputTextFlags.EnterReturnsTrue);
        var problem = MaterialVariants.NameProblem(meta, current.Source.GamePath, variantName);
        if (problem != null && variantName.Trim().Length > 0)
            ImGui.TextColored(Theme.Warning, problem);
        var block = copies?.Block;
        if (block != null)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Warning, block);
        }
        ImGui.Spacing();
        var ready = problem == null && block == null && !Busy;
        using (ImRaii.Disabled(!ready))
        {
            if ((ImGui.Button("Save variant##material-variant-save") || enter) && ready)
            {
                ImGui.CloseCurrentPopup();
                variantMeta = null;
                SaveVariant(current, variantName.Trim());
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##material-variant-cancel"))
        {
            variantMeta = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    /// <summary>
    /// Writes the edited material, with the textures a preset made for it, as a new variant: through
    /// the mod window it came from, or straight into the mod. Then shows it on the character.
    /// </summary>
    private void SaveVariant(MaterialSession current, string name)
    {
        var doc = current.Current;
        if (current.ModelChangesFor(doc).Count > 0)
        {
            Say(FeedbackSeverity.Warning, "A variant can't carry the model's new second UV map. Save writes both, or apply Scrolling pattern again without it.");
            return;
        }
        // Saves and restores of other windows may be writing the same mod's files right now.
        if (!ModWrites.Wait(0))
        {
            Say(FeedbackSeverity.Warning, "Another Material window is saving. Try again in a moment.");
            return;
        }
        MaterialVariants.Written written;
        byte[] bytes;
        try
        {
            bytes = doc.Write();
            List<(string GamePath, byte[] Bytes)> files = [(current.Source.GamePath, bytes), .. current.NewFilesFor(doc)];
            if (copies is { } window)
            {
                if (window.SaveVariant(name, files, out var error) is not { } through)
                {
                    Say(FeedbackSeverity.Error, error);
                    return;
                }
                written = through;
            }
            else
                written = MaterialVariants.WriteInto(current.Source.ModRootPath, name, files);
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not save the material as a variant.");
            Say(FeedbackSeverity.Error, $"The variant could not be saved: {e.Message}");
            return;
        }
        finally { ModWrites.Release(); }

        var material = written.Files[0];
        current.SavedAsCopy(doc, material.DiskPath, material.RelPath, bytes);
        loadedDoc = current.History.Saved;
        shownDoc = null;
        previewDue = 0;
        var source = current.Source;
        var shownIn = collection;
        Run(async () =>
        {
            // The variant's file is what the character loads next; the temporary preview comes down without a redraw of its own.
            await current.DiscardAsync(preview, redraw: false).ConfigureAwait(false);
            return await ShowVariantAsync(source, shownIn, written).ConfigureAwait(false);
        }, warning =>
        {
            var where = written.NewGroup ? $"the new group \"{written.GroupName}\"" : $"\"{written.GroupName}\"";
            return warning ?? $"Saved \"{written.OptionName}\" into {where} and picked it for your character. The original is as it was. Save now writes the variant.";
        });
    }

    /// <summary>Lets Penumbra read the mod again, picks the variant in the character's collection and redraws; returns what didn't work.</summary>
    private Task<string?> ShowVariantAsync(MaterialSource source, Guid? target, MaterialVariants.Written written)
        => framework.RunOnFrameworkThread(() =>
        {
            var lead = $"Saved \"{written.OptionName}\" into \"{written.GroupName}\", but";
            try
            {
                var reload = penumbra.ReloadMod.Invoke(source.ModDirectory);
                if (reload is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
                    return $"{lead} Penumbra did not reload the mod ({reload}). Reload it in Penumbra, then pick the variant there.";
                if (target is not { } id)
                    return $"{lead} your character's collection is unknown. Pick the variant in Penumbra.";
                var picked = penumbra.TrySetModSetting.Invoke(id, source.ModDirectory, written.GroupName, written.OptionName);
                if (picked is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
                    return $"{lead} it could not be picked for your character ({picked}). Pick it in Penumbra.";
            }
            catch (Exception e)
            {
                log.Warning(e, "Could not show the material variant through Penumbra.");
                return $"{lead} Penumbra could not be reached: {e.Message}";
            }
            return penumbra.RedrawObjectOnFramework(source.ObjectIndex);
        });
}
