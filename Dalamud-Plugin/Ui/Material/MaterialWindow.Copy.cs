using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Materials;
using InstantEdit.Studio;

namespace InstantEdit.Ui.Material;

/// <summary>
/// Save as copy into an option: a material opened from a mod window goes, as edited, into an option
/// of that mod you pick, as new files written by the mod window. The file it was opened from stays
/// as it is, and Save writes the copy from then on.
/// </summary>
internal sealed partial class MaterialWindow
{
    private const string CopyPopup = "Save a copy##material-copy";

    // The mod window the open material came from; null when it was opened from On Screen or Game Files.
    private IModFileCopies? copies;
    private IReadOnlyList<ModHandoffOption> copyOptions = [];
    private string? copyChoice;
    private bool openCopy;

    /// <summary>The dots menu's item, only for a mod file opened from a mod window.</summary>
    private void DrawCopyMenuItem(MaterialSession current)
    {
        if (copies is not { } target || current.Source.State != ResourceSourceState.LoadedMod)
            return;
        var block = target.Block;
        using (ImRaii.Disabled(Busy || block != null))
        {
            if (ImGui.MenuItem("Save as copy into an option"))
                StartCopy(current, target);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(block ?? $"Saves the material into an option of {target.ModName} as a new file. The original stays as it is.");
    }

    private void StartCopy(MaterialSession current, IModFileCopies target)
    {
        List<string> paths = [current.Source.GamePath, .. current.NewFilesFor(current.Current).Select(file => file.GamePath)];
        copyOptions = target.Options(paths, current.Source.RelativePath);
        copyChoice = null;
        openCopy = true;
    }

    private void DrawCopyPopup(MaterialSession current)
    {
        if (openCopy)
        {
            openCopy = false;
            ImGui.OpenPopup(CopyPopup);
        }
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(440, 0), Theme.Scaled(640, 560));
        if (!ImGui.BeginPopupModal(CopyPopup, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        if (copies is not { } target)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        Widgets.SectionHeader("Save a copy into an option");
        Widgets.HintWrapped($"The edited material goes into the option you pick as a new file of {target.ModName}. The original file stays as it is.");
        ImGui.Spacing();
        DrawCopyOptions();
        var chosen = copyOptions.FirstOrDefault(option => option.Key == copyChoice);
        if (chosen != null)
            Widgets.HintWrapped(chosen.HasFile ? "This option uses the copy from now on. The file it used stays in the mod." : "Adds the material to this option.");
        var block = target.Block;
        if (block != null)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Warning, block);
        }
        ImGui.Spacing();
        using (ImRaii.Disabled(chosen == null || block != null || Busy))
        {
            if (ImGui.Button("Save copy##material-copy-save") && chosen != null && block == null && !Busy)
            {
                ImGui.CloseCurrentPopup();
                SaveCopy(current, target, chosen.Key);
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##material-copy-cancel"))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    /// <summary>One row per place in the mod; one that can't take the copy is greyed with the reason.</summary>
    private void DrawCopyOptions()
    {
        var rows = Math.Clamp(copyOptions.Count, 1, 10);
        var height = (ImGui.GetTextLineHeight() + ImGui.GetStyle().CellPadding.Y * 2) * rows + ImGui.GetStyle().CellPadding.Y;
        using var table = ImRaii.Table("##material-copy-options", 2, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter,
            new Vector2(Theme.Scaled(420), height));
        if (!table.Success)
            return;
        ImGui.TableSetupColumn("Option", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Note", ImGuiTableColumnFlags.WidthFixed);
        for (var i = 0; i < copyOptions.Count; i++)
        {
            var option = copyOptions[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            using (ImRaii.Disabled(option.Block != null))
            {
                if (ImGui.Selectable($"{option.Label}##material-copy-{i}", copyChoice == option.Key, ImGuiSelectableFlags.SpanAllColumns) && option.Block == null)
                    copyChoice = option.Key;
            }
            if (option.Block != null)
                Tip(option.Block);
            ImGui.TableNextColumn();
            if (option.UsesSource || option.HasFile)
                ImGui.TextColored(Theme.Muted, option.UsesSource ? "uses this file" : "has its own");
        }
        if (copyOptions.Count == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.Muted, "The mod has no options to show.");
        }
    }

    /// <summary>Writes the edited material, with the textures a preset made for it, into the option through the mod window.</summary>
    private void SaveCopy(MaterialSession current, IModFileCopies target, string optionKey)
    {
        var doc = current.Current;
        if (current.ModelChangesFor(doc).Count > 0)
        {
            Say(FeedbackSeverity.Warning, "A copy can't carry the model's new second UV map. Save writes both, or apply Scrolling pattern again without it.");
            return;
        }
        // Saves and restores of other windows may be writing the same mod's option files right now.
        if (!ModWrites.Wait(0))
        {
            Say(FeedbackSeverity.Warning, "Another Material window is saving. Try again in a moment.");
            return;
        }
        try
        {
            var bytes = doc.Write();
            List<(string GamePath, byte[] Bytes)> files = [(current.Source.GamePath, bytes), .. current.NewFilesFor(doc)];
            var result = target.SaveCopy(optionKey, files);
            if (result.Error is { } error)
            {
                Say(FeedbackSeverity.Error, error);
                return;
            }
            var material = result.Files.First(file => string.Equals(file.GamePath, current.Source.GamePath, StringComparison.OrdinalIgnoreCase));
            current.SavedAsCopy(doc, material.DiskPath, material.RelPath, bytes);
            Say(FeedbackSeverity.Success, $"Saved a copy into {result.OptionLabel}. The original is as it was. Save now writes the copy.");
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not save a copy of the material into an option.");
            Say(FeedbackSeverity.Error, $"The copy could not be saved: {e.Message}");
        }
        finally { ModWrites.Release(); }
    }
}
