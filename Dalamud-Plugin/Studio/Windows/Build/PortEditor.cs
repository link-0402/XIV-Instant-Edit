using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Build;

/// <summary>
/// Editing an item of the modpack, shared by Build mode's panes: dropping model files on a race, the
/// menus that add races, materials and texture slots, the material context menu, and the
/// confirmations those actions ask for (drawn at the window root, so any pane can open them).
/// </summary>
internal sealed class PortEditor
{
    private const string ConfirmRemoveItemId     = "Remove from the modpack?##StudioConfirmRemoveItem";
    private const string ConfirmRemoveMaterialId = "Remove material?##StudioConfirmRemoveMat";
    private const string ConfirmMatchId          = "Match vanilla materials?##StudioConfirmMatch";
    private const string ConfirmPresetId         = "Apply preset?##StudioConfirmPreset";
    private const string ConfirmPasteId          = "Paste material##StudioConfirmPaste";

    private readonly StudioHost _host;
    private readonly PortSession _session;

    private uint _askRemoveItem;
    private (uint Item, int Material)? _askRemoveMaterial;
    private uint _askMatch;
    private (uint Item, int Material, int Preset)? _askPreset;
    private (uint Item, int After)? _askPaste;
    private string? _openPopup;

    /// <summary>Suggested preset per subject, looked up once (tails and ears read their vanilla material).</summary>
    private readonly Dictionary<uint, int> _suggestedPreset = new();

    public PortEditor(StudioHost host, PortSession session)
    {
        _host  = host;
        _session = session;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Drop rules
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The drop rule for a race model: one .mdl sets File mode, several (or a folder) make a variant group.</summary>
    public FileDropRule ModelDropRule(PortItem item, RaceModelEntry model) => new(FileDrop.RejectForModel, paths =>
    {
        var files = FileDrop.ModelFiles(paths);
        if (files.Count == 1 && paths.Folders.Count == 0)
        {
            model.SourcePath  = files[0];
            model.UseVariants = false;
            model.UseDummy    = false;
        }
        else
        {
            // A dropped set replaces the list outright, so the old per-variant material
            // choices no longer belong to any file.
            model.VariantPaths = files;
            model.VariantMaterialLinks.Clear();
            model.UseVariants  = true;
            model.UseDummy     = false;
        }
        model.IsVanillaDummy = false;
        _session.MarkDirty();
        _session.Notify($"{item.Subject.DisplayName} / {model.RaceGender.DisplayName} ← {paths.Describe()}", Severity.Info);
    });

    /// <summary>Picks the .mdl a race builds from.</summary>
    public void BrowseModel(RaceModelEntry model)
    {
        Gui.Dialogs.OpenFileDialog("Model file", "Model files{.mdl}", (ok, path) =>
        {
            if (!ok) return;
            model.SourcePath = path;
            model.UseVariants = model.UseDummy = model.IsVanillaDummy = false;
            _session.MarkDirty();
        });
    }

    /// <summary>Picks the image a texture slot is made from.</summary>
    public void BrowseTexture(TextureSlot tex)
    {
        Gui.Dialogs.OpenFileDialog("Texture image", "Image files{.png,.jpg,.jpeg,.tga,.dds,.tex}", (ok, path) =>
        {
            if (!ok) return;
            tex.SourcePath = path;
            tex.UseVariants = tex.UseWhiteDummy = false;
            _session.MarkDirty();
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Add menus (opened by the tree's "+" rows)
    // ─────────────────────────────────────────────────────────────────────────

    public const string AddRacePopup     = "##addrace";
    public const string AddMaterialPopup = "##addmat";
    public const string AddTexturePopup  = "##addtex";

    /// <summary>
    /// The races a model can be added for. Gear can claim a race that has no vanilla model of its own
    /// (an Eqdp override makes the game use it); a feature is limited to races that ship this id.
    /// </summary>
    public void DrawAddRacePopup(PortItem item)
    {
        if (!ImGui.BeginPopup(AddRacePopup))
            return;

        var addable = _session.AddableRaces(item);
        if (addable.Count == 0)
        {
            Gui.Hint("No race left to add.");
            ImGui.EndPopup();
            return;
        }

        var native = _host.GameData.GetNativeRaces(item.Subject);
        bool separated = false;
        foreach (var rg in addable.OrderByDescending(rg => native.Contains(rg)).ThenBy(rg => rg.RaceCode, StringComparer.Ordinal))
        {
            if (!native.Contains(rg) && !separated)
            {
                separated = true;
                ImGui.Separator();
                ImGui.TextColored(Theme.Muted, "Needs a race override:");
            }
            if (ImGui.Selectable(rg.DisplayName))
                _session.AddModel(item, rg);
        }
        ImGui.EndPopup();
    }

    public void DrawAddMaterialPopup(PortItem item)
    {
        if (!ImGui.BeginPopup(AddMaterialPopup))
            return;

        if (_session.CopiedMaterial is { } copied)
        {
            if (ImGui.Selectable($"Paste \"{copied.Name}\""))
                RequestPaste(item, -1);
            Gui.Tooltip($"Copied from {_session.CopiedFrom?.DisplayName}.");
            ImGui.Separator();
        }

        int suggested = SuggestedPreset(item.Subject);
        ImGui.TextColored(Theme.Muted, "From a preset");
        for (int p = 0; p < MaterialPresets.All.Count; p++)
        {
            var preset = MaterialPresets.All[p];
            if (ImGui.Selectable($"{preset.Name}{(p == suggested ? "   (suggested)" : "")}##preset{p}"))
                _session.AddMaterial(item, preset);
            Gui.Tooltip($"{ShaderInfo.DisplayName(preset.Shader)}: {string.Join(", ", preset.Textures.Select(TextureTypeInfo.DisplayName))}");
        }

        ImGui.Separator();
        var emptyShader = suggested >= 0 ? MaterialPresets.All[suggested].Shader : ShaderType.Character;
        if (ImGui.Selectable($"Empty ({ShaderInfo.DisplayName(emptyShader)}, no textures)"))
            _session.AddMaterial(item, null, emptyShader);
        Gui.Tooltip("Just a shader — add the texture slots you need with + Texture slot.");

        if (ImGui.Selectable("Match vanilla materials"))
            AskMatchVanilla(item);
        Gui.Tooltip("Replace this item's materials with the vanilla model's own: their names, shaders and texture slots.");
        ImGui.EndPopup();
    }

    public void DrawAddTexturePopup(PortItem item, int materialIndex)
    {
        if (!ImGui.BeginPopup(AddTexturePopup))
            return;
        if (materialIndex < 0 || materialIndex >= item.Materials.Count)
        {
            ImGui.EndPopup();
            return;
        }

        var mat = item.Materials[materialIndex];
        var used = mat.Textures.Select(t => t.Type).ToHashSet();
        var usual = ShaderInfo.SupportedTextures(mat.ShaderType);
        var common = usual.Where(t => !used.Contains(t)).ToList();
        var unusual = Enum.GetValues<TextureType>().Where(t => !used.Contains(t) && !usual.Contains(t)).ToList();
        var shader = ShaderInfo.DisplayName(mat.ShaderType);

        if (common.Count + unusual.Count == 0)
            Gui.Hint("Every texture type is already here.");

        foreach (var type in common)
            if (ImGui.Selectable(TextureTypeInfo.DisplayName(type)))
                _session.AddTexture(item, materialIndex, type);

        if (unusual.Count > 0)
        {
            if (common.Count > 0) ImGui.Separator();
            ImGui.TextColored(Theme.Muted, $"Not normally used by {shader}");
            foreach (var type in unusual)
            {
                if (ImGui.Selectable($"{TextureTypeInfo.DisplayName(type)}##unusual"))
                    _session.AddTexture(item, materialIndex, type);
                Gui.Tooltip("Added to the material with its own sampler, for special effects — the shader may ignore it unless it is set up to read it.");
            }
        }
        ImGui.EndPopup();
    }

    /// <summary>The right-click menu of a material.</summary>
    public void DrawMaterialMenu(PortItem item, int index)
    {
        if (index < 0 || index >= item.Materials.Count)
            return;
        if (ImGui.MenuItem("Copy"))
            _session.CopyMaterial(item, index);
        PasteMenuItem(item, index);
        ImGui.Separator();
        if (ImGui.MenuItem("Move up", "", false, index > 0))
            _session.MoveMaterial(item, index, -1);
        if (ImGui.MenuItem("Move down", "", false, index < item.Materials.Count - 1))
            _session.MoveMaterial(item, index, +1);
        ImGui.Separator();
        if (ImGui.MenuItem("Remove"))
            AskRemoveMaterial(item, index);
    }

    /// <summary>Index of the preset that suits the subject (hair → Hair, face → Skin, tails and ears → their vanilla shader), or -1.</summary>
    public int SuggestedPreset(PortSubject subject)
    {
        if (_suggestedPreset.TryGetValue(subject.Key, out var cached))
            return cached;

        ShaderType? shader = subject.Kind switch
        {
            SubjectKind.Hair => ShaderType.Hair,
            SubjectKind.Face => ShaderType.Skin,
            SubjectKind.Gear or SubjectKind.Weapon => ShaderType.Character,
            _ => subject.BaseRace is { } race
                ? _host.GameData.ReadVanillaMaterialLayout(subject, race)?.FirstOrDefault()?.ShaderType
                : null,
        };

        int index = -1;
        for (int i = 0; shader != null && i < MaterialPresets.All.Count; i++)
        {
            if (MaterialPresets.All[i].Shader == shader)
            {
                index = i;
                break;
            }
        }
        _suggestedPreset[subject.Key] = index;
        return index;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Confirmations
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>A "Paste" menu entry, disabled until a material has been copied.</summary>
    private void PasteMenuItem(PortItem item, int afterIndex)
    {
        var copied = _session.CopiedMaterial;
        var label = copied == null ? "Paste material" : $"Paste \"{copied.Name}\"{(afterIndex >= 0 ? " below" : "")}";
        if (ImGui.MenuItem(label, "", false, copied != null))
            RequestPaste(item, afterIndex);
    }

    /// <summary>
    /// Pastes the copied material, first asking whether its textures should stay shared with the
    /// original or be written again for this model. A material without textures just pastes.
    /// </summary>
    public void RequestPaste(PortItem item, int afterIndex)
    {
        if (_session.CopiedMaterial is not { } copied) return;
        if (copied.Textures.Count == 0) _session.PasteMaterial(item, keepTexturePaths: false, afterIndex);
        else Ask(ref _askPaste, (item.Key, afterIndex), ConfirmPasteId);
    }

    public void AskRemoveItem(PortItem item) => Ask(ref _askRemoveItem, item.Key, ConfirmRemoveItemId);

    public void AskRemoveMaterial(PortItem item, int index) => Ask(ref _askRemoveMaterial, (item.Key, index), ConfirmRemoveMaterialId);

    /// <summary>Applies a preset, asking first when the material already has texture slots.</summary>
    public void AskPreset(PortItem item, int index, int preset)
    {
        if (index < 0 || index >= item.Materials.Count) return;
        if (item.Materials[index].Textures.Count == 0) _session.ApplyPreset(item.Materials[index], MaterialPresets.All[preset]);
        else Ask(ref _askPreset, (item.Key, index, preset), ConfirmPresetId);
    }

    /// <summary>Replaces an item's materials with the vanilla layout, asking first when it has any.</summary>
    public void AskMatchVanilla(PortItem item)
    {
        if (item.Materials.Count == 0) _session.MatchVanillaMaterials(item);
        else Ask(ref _askMatch, item.Key, ConfirmMatchId);
    }

    private void Ask<T>(ref T field, T value, string popupId)
    {
        field = value;
        _openPopup = popupId;
    }

    /// <summary>Draws the confirmations; called once per frame at the window root.</summary>
    public void DrawPopups()
    {
        if (_openPopup != null)
        {
            ImGui.OpenPopup(_openPopup);
            _openPopup = null;
        }

        Confirm.Modal(ConfirmRemoveItemId, () =>
        {
            var item = _session.FindItem(_askRemoveItem);
            ImGui.TextUnformatted($"Remove \"{item?.Subject.DisplayName}\" from the modpack?");
            ImGui.TextColored(Theme.Muted, "Its set-up is kept — adding it again from the browser brings it back.");
        }, "Remove", () => _session.RemoveItem(_askRemoveItem));

        Confirm.Modal(ConfirmRemoveMaterialId, () =>
        {
            var mat = _askRemoveMaterial is { } a ? MaterialAt(a.Item, a.Material) : null;
            ImGui.TextUnformatted($"Remove \"{mat?.Name}\" and its texture set-up?");
            ImGui.TextColored(Theme.Muted, "Files on disk are not touched.");
        }, "Remove", () =>
        {
            if (_askRemoveMaterial is { } a && _session.FindItem(a.Item) is { } item)
                _session.RemoveMaterial(item, a.Material);
        });

        Confirm.Modal(ConfirmMatchId, () =>
        {
            var item = _session.FindItem(_askMatch);
            ImGui.TextUnformatted($"Replace all {item?.Materials.Count} material(s) of \"{item?.Subject.DisplayName}\" with the vanilla model's materials?");
            ImGui.TextColored(Theme.Muted, "Texture source paths set on the current materials are cleared.");
        }, "Replace", () =>
        {
            if (_session.FindItem(_askMatch) is { } item)
                _session.MatchVanillaMaterials(item);
        });

        DrawPastePopup();

        Confirm.Modal(ConfirmPresetId, () =>
        {
            var p = _askPreset;
            var mat = p is { } x ? MaterialAt(x.Item, x.Material) : null;
            ImGui.TextUnformatted($"Replace the shader and all {mat?.Textures.Count} texture slot(s) of \"{mat?.Name}\"");
            ImGui.TextUnformatted($"with the \"{(p is { } y ? MaterialPresets.Names[y.Preset] : "")}\" preset?");
            ImGui.TextColored(Theme.Muted, "Texture source paths set on this material are cleared.");
        }, "Apply", () =>
        {
            if (_askPreset is { } x && MaterialAt(x.Item, x.Material) is { } mat)
                _session.ApplyPreset(mat, MaterialPresets.All[x.Preset]);
        });
    }

    private void DrawPastePopup()
    {
        if (!ImGui.BeginPopupModal(ConfirmPasteId, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var copied = _session.CopiedMaterial;
        var from = _session.CopiedFrom;
        var target = _askPaste is { } a ? _session.FindItem(a.Item) : null;
        if (copied == null || from == null || target == null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        int after = _askPaste!.Value.After;
        ImGui.TextUnformatted($"Paste \"{copied.Name}\" from {from.DisplayName} onto {target.Subject.DisplayName}.");
        ImGui.TextUnformatted($"What should its {copied.Textures.Count} texture(s) use?");
        ImGui.Spacing();

        float w = Theme.S(210);
        if (Gui.IconTextButton(FontAwesomeIcon.Link, "Keep texture paths", null, w))
        {
            _session.PasteMaterial(target, keepTexturePaths: true, after);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Theme.S(320));
        Gui.HintWrapped($"Shared: this material points at the textures {from.DisplayName} writes. One set of files, no duplicates.");
        ImGui.PopTextWrapPos();

        if (Gui.IconTextButton(FontAwesomeIcon.Unlink, "Retarget to this model", null, w))
        {
            _session.PasteMaterial(target, keepTexturePaths: false, after);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Theme.S(320));
        Gui.HintWrapped($"Separate: textures are written again under {target.Subject.DisplayName}'s folder — from the same source files, so they still match.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        if (ImGui.Button("Cancel", new Vector2(Theme.S(100), 0)))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private MaterialSetup? MaterialAt(uint key, int index)
    {
        var item = _session.FindItem(key);
        return item != null && index >= 0 && index < item.Materials.Count ? item.Materials[index] : null;
    }
}
