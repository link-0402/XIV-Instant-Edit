using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Windows.Build;
using InstantEdit.Studio.Windows.UI;
using ComboWheel = InstantEdit.Ui.ComboWheel;

namespace InstantEdit.Studio.Windows.Panels;

/// <summary>
/// Build mode's inspector for an item of the modpack: everything about the tile that is selected,
/// including the settings too detailed for an inline row — game paths, variant lists, texture
/// postfixes and placeholder sizes, and what the vanilla game ships.
/// </summary>
internal sealed class ModelInspector
{
    private readonly StudioHost _host;
    private readonly PortSession _session;
    private readonly TextureSlotEditor _textures;
    private readonly PortEditor _tree;

    private int _presetIndex;

    // Game-data facts per subject, read once: dye variant count, vanilla material names.
    private readonly Dictionary<uint, int> _dyeVariants = new();
    private readonly Dictionary<uint, IReadOnlyList<string>> _vanillaMaterials = new();

    public ModelInspector(StudioHost host, PortSession session, TextureSlotEditor textures, PortEditor tree)
    {
        _host   = host;
        _session  = session;
        _textures = textures;
        _tree     = tree;
    }

    public void Draw()
    {
        if (_session.Selection is not { } sel || _session.FindItem(sel.Item) is not { } item)
        {
            Gui.Hint(_session.Items.Count == 0 ? "Add gear or a feature from the game first." : "Pick a tile to see it here.");
            return;
        }

        switch (sel.Kind)
        {
            case TargetKind.Model when sel.Index >= 0 && sel.Index < item.Models.Count:
                DrawModel(item, sel.Index);
                break;
            case TargetKind.Material when sel.Index >= 0 && sel.Index < item.Materials.Count:
                DrawMaterial(item, sel.Index);
                break;
            case TargetKind.Texture when sel.Index >= 0 && sel.Index < item.Materials.Count
                                      && sel.SubIndex >= 0 && sel.SubIndex < item.Materials[sel.Index].Textures.Count:
                DrawTexture(item, sel.Index, sel.SubIndex);
                break;
            default:
                DrawItem(item);
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Item
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawItem(PortItem item)
    {
        var subject = item.Subject;
        Gui.SectionHeader(subject.DisplayName);

        if (subject is GearSubject gear)
        {
            Gui.LabeledValue("Slot", SlotInfo.DisplayLabelMap[gear.Item.Slot]);
            Gui.LabeledValue("Model", gear.Item.ModelIdDisplay, "Model set id and variant.");
        }
        else if (subject is FeatureSubject feature)
        {
            Gui.LabeledValue("Race", feature.Race.DisplayName);
            Gui.LabeledValue("Id", feature.IdDisplay);
        }
        else if (subject is WeaponSubject weapon)
        {
            Gui.LabeledValue("Class", weapon.Model.Category.Length > 0 ? weapon.Model.Category : "Weapon");
            Gui.LabeledValue("Model", $"{weapon.IdDisplay}-{weapon.Model.Variant}{(weapon.Model.OffHand ? ", off hand" : "")}", "Weapon id, body and variant.");
        }
        Gui.LabeledValue("Materials", subject.MaterialFolder(subject.BaseRace), "Every .mtrl this item writes goes here.");
        Gui.LabeledValue("Textures", subject.TextureFolder);
        PackPlaceUi.Row(_session, "##itemplace", item.Meta.Option ?? PackPlace.Default, null, p => _session.SetPlace(item, p ?? PackPlace.Default),
            "Where the item goes in the mod: its files, unless one of them is put elsewhere, and its metadata. What is in an option is only used while players have it picked.");

        ImGui.Spacing();
        Gui.Label("Set up");
        ImGui.TextUnformatted($"{item.Models.Count} {(subject.HasRaces ? "race model(s)" : "model(s)")}, {item.Materials.Count} material(s), {item.Materials.Sum(m => m.Textures.Count)} texture(s)");

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
        if (Gui.IconTextButton(FontAwesomeIcon.Trash, "Remove from modpack", "Its set-up is kept — adding it again from the browser brings it back."))
            _tree.AskRemoveItem(item);
        ImGui.PopStyleColor();
        if (_session.CopiedMaterial is { } copied)
        {
            ImGui.SameLine();
            if (Gui.IconTextButton(FontAwesomeIcon.Paste, "Paste material", $"Paste \"{copied.Name}\" onto this model."))
                _tree.RequestPaste(item, -1);
        }

        ImGui.Spacing();
        ImGui.Spacing();
        Gui.SectionHeader("In the game", "What the vanilla game already ships.");

        if (subject is GearSubject or WeaponSubject)
        {
            if (!_dyeVariants.TryGetValue(item.Key, out var dyes))
                _dyeVariants[item.Key] = dyes = (subject is GearSubject g
                    ? _host.GameData.GetImcOverridesForcingV1(g.Item.Slot, g.Item.ModelId)
                    : _host.GameData.GetWeaponImcOverridesForcingV1(((WeaponSubject)subject).Model))?.Count ?? 0;
            Gui.Label("Dye variants");
            ImGui.TextUnformatted(dyes > 0 ? dyes.ToString() : "—");
            Gui.Tooltip("Each variant gets an Imc override pointing it at your material, so the mod shows whichever one is equipped.");
        }

        DrawVanillaMaterials(item);

        // Single-race features only ever apply to their own race, so the table only helps gear and hair.
        if (!subject.MultiRace)
            return;

        ImGui.Spacing();
        var nativeRaces = _host.GameData.GetNativeRaces(subject);
        ImGui.TextColored(Theme.Muted, subject is GearSubject ? "Races with their own model" : $"Races that have {subject.KindLabel.ToLowerInvariant()} {((FeatureSubject)subject).Id}");
        Gui.Tooltip(subject is GearSubject
            ? "Races not listed fall back to another race's model. Adding a model for one of them adds an Eqdp override automatically."
            : "Adding a race that does not ship this id may not work: the game might never ask for the file.");

        if (ImGui.BeginTable("##NativeRaces", 2, ImGuiTableFlags.SizingStretchSame))
        {
            foreach (var rg in RaceInfo.AllRaces)
            {
                ImGui.TableNextColumn();
                bool native = nativeRaces.Contains(rg);
                Gui.Icon(native ? FontAwesomeIcon.Check : FontAwesomeIcon.Minus, native ? Theme.Good : Theme.Faint);
                ImGui.SameLine();
                ImGui.TextColored(native ? Theme.Muted : Theme.Faint, rg.DisplayName);
            }
            ImGui.EndTable();
        }
    }

    /// <summary>
    /// The material names the vanilla model references — what a feature port's model must use,
    /// and what "Match vanilla" sets up.
    /// </summary>
    private void DrawVanillaMaterials(PortItem item)
    {
        var subject = item.Subject;
        if (subject.BaseRace is not { } race)
        {
            if (Gui.IconTextButton(FontAwesomeIcon.Magic, "Match vanilla materials",
                    "Replace this item's materials with the vanilla model's own: their names, shaders and texture slots."))
                _tree.AskMatchVanilla(item);
            return;
        }

        if (!_vanillaMaterials.TryGetValue(item.Key, out var names))
            _vanillaMaterials[item.Key] = names = _host.GameData.GetVanillaMaterialNames(subject, race);

        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "Vanilla materials");
        Gui.Tooltip("The material names the game's own model uses. Your model's mesh parts should reference the same names.");
        if (names.Count == 0)
        {
            ImGui.TextColored(Theme.Warn, "No vanilla model found.");
            return;
        }

        foreach (var name in names)
        {
            ImGui.Bullet();
            ImGui.TextUnformatted(name.TrimStart('/'));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click to copy.");
            if (ImGui.IsItemClicked()) ImGui.SetClipboardText(name);
        }

        ImGui.Spacing();
        if (Gui.IconTextButton(FontAwesomeIcon.Magic, "Match vanilla materials",
                "Replace this item's materials with these, with their shaders and texture slots."))
            _tree.AskMatchVanilla(item);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Race model
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawModel(PortItem item, int index)
    {
        var subject = item.Subject;
        var model = item.Models[index];
        Gui.SectionHeader(subject.HasRaces ? $"{subject.DisplayName} · {model.RaceGender.DisplayName}" : $"{subject.DisplayName} · model");

        Gui.LabeledValue("Game path", subject.ModelGamePath(model.RaceGender), "Where this model is placed in the mod.");

        bool native = _host.GameData.GetNativeRaces(subject).Contains(model.RaceGender);
        Gui.Label("Vanilla model");
        if (native)
            ImGui.TextColored(Theme.Muted, "yes — replaced directly");
        else if (subject is GearSubject)
            ImGui.TextColored(Theme.Accent, "no — Eqdp override added");
        else
            ImGui.TextColored(Theme.Warn, "no — the game may not load this");
        if (!model.UseVariants || model.UseDummy)
            PackPlaceUi.Row(_session, "##modelplace", model.Option, $"With the item ({_session.Label(_session.PlaceOf(item))})", p => _session.SetPlace(model, p),
                "Where this race's model goes in the mod.");

        ImGui.Spacing();
        Gui.Label("Source", "Where this race's .mdl comes from.");
        var mode = model.UseDummy ? 2 : model.UseVariants ? 1 : 0;
        for (int m = 0; m < 3; m++)
        {
            if (m > 0) ImGui.SameLine();
            if (ImGui.RadioButton(m switch { 0 => "File##modelmode", 1 => "Variants##modelmode", _ => "Dummy##modelmode" }, mode == m))
            {
                model.UseVariants = m == 1;
                model.UseDummy    = m == 2;
                mode = m;
                _session.MarkDirty();
            }
            Gui.Tooltip(m switch
            {
                0 => "One .mdl file, copied in as the default.",
                1 => "Several .mdl files \u2014 each becomes one option of a group in the mod, named after the file.",
                _ => "No file of your own: the build generates this race's model from its vanilla skeleton, "
                   + "with one empty mesh part per material. Enough to load the mod in game, and a starting point to model over.",
            });
        }

        var drop = _tree.ModelDropRule(item, model);
        if (mode == 2)
            DrawDummySource(item, model);
        else if (mode == 1)
            DrawModelVariants(item, model, drop);
        else
            DrawFileSource(item, model, drop);

        if (!subject.MultiRace || PortSession.IsBaseRace(item, model.RaceGender))
            return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
        if (Gui.IconTextButton(FontAwesomeIcon.Trash, "Remove race", "Removes this race from the mod. The file on disk is not touched."))
            _session.RemoveModel(item, index);
        ImGui.PopStyleColor();
    }

    /// <summary>File mode: one .mdl copied into the mod, with its mesh parts pointed at this item's materials.</summary>
    private void DrawFileSource(PortItem item, RaceModelEntry model, FileDropRule drop)
    {
        Gui.Label("File");
        string src = model.SourcePath;
        if (Gui.PathPicker("ModelSrc", ref src, "path to a .mdl file", PathKind.File, "Model files{.mdl}",
                picked => { model.SourcePath = picked; model.IsVanillaDummy = false; _session.MarkDirty(); },
                "Local .mdl copied into the mod at the game path above.", drop: drop))
        {
            model.SourcePath = src;
            model.IsVanillaDummy = false;
            _session.MarkDirty();
        }

        if (model.IsVanillaDummy)
        {
            Gui.Icon(FontAwesomeIcon.ExclamationTriangle, Theme.Warn);
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
            ImGui.TextWrapped("This file is an empty dummy exported earlier \u2014 model it in Blender before releasing the mod.");
            ImGui.PopStyleColor();
        }

        if (string.IsNullOrWhiteSpace(model.SourcePath))
            return;

        ImGui.Spacing();
        DrawMaterialWiring(item, model, model.SourcePath, model.MaterialLinks,
            (slot, value) => ModelMaterialLinks.Store(model.MaterialLinks, slot, value), "file");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Material wiring
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The material slots of one .mdl and which of the item's materials each is switched to. A model
    /// made anywhere else references the names it was exported with, so without this the .mtrl files
    /// the build writes would sit in the mod unused.
    /// </summary>
    private void DrawMaterialWiring(PortItem item, RaceModelEntry model, string path,
        IReadOnlyList<int>? stored, Action<int, int> onSet, string id)
    {
        var facts = _host.ModelFiles.Read(path);
        Gui.Label("Materials in this file", "One row per material slot of the file \u2014 the mesh parts reference these by name.");

        if (!facts.Ok)
        {
            ImGui.TextColored(Theme.Warn, facts.Error);
            Gui.Tooltip("Its materials cannot be read, so the build copies the file through as it is.");
            return;
        }

        if (facts.Materials.Count == 0)
        {
            Gui.Hint("(this file has no material slots)");
            return;
        }

        var written = ModelMaterialLinks.WrittenNames(item.Subject, item.Materials,
            _host.GameData.MaterialRaceFor(item.Subject, model.RaceGender));
        if (written.Count == 0)
            Gui.HintWrapped("This item has no materials yet \u2014 add some and they can be assigned here.");

        ImGui.PushID(id);
        if (ImGui.BeginTable("##wiring", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.PadOuterX))
        {
            ImGui.TableSetupColumn("In the file", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Uses",        ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableHeadersRow();

            for (int slot = 0; slot < facts.Materials.Count; slot++)
            {
                var referenced = facts.Materials[slot];
                int link = ModelMaterialLinks.Stored(stored, slot);
                int resolved = ModelMaterialLinks.Resolve(link, slot, facts.Materials, written);

                ImGui.TableNextRow();
                ImGui.PushID(slot);

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                bool sharedFeature = ModelMaterialLinks.IsSharedFeature(referenced);
                bool kept = resolved < 0 && !sharedFeature;
                ImGui.TextColored(kept && written.Count > 0 ? Theme.Warn : Theme.Muted, Short(referenced));
                Gui.Tooltip($"Mesh part {slot + 1} references {referenced}."
                         + (kept && written.Count > 0 ? "\n\nLeft as it is, so this mod does not supply its material."
                          : sharedFeature ? "\n\nA shared body material (bibo / bibopube / piercings, etc.) — left alone on purpose."
                          : string.Empty));

                // The options read as what happens, so "Automatic" says which material it lands on.
                int auto = ModelMaterialLinks.Resolve(ModelMaterialLinks.Auto, slot, facts.Materials, written);
                var options = new string[written.Count + 2];
                options[0] = auto >= 0 ? $"Automatic \u2192 {Short(written[auto])}" : "Automatic \u2014 keep";
                options[1] = "Keep the file's own";
                for (int m = 0; m < written.Count; m++)
                    options[m + 2] = Short(written[m]);

                int choice = link == ModelMaterialLinks.Auto ? 0
                           : link == ModelMaterialLinks.Keep || link >= written.Count ? 1
                           : link + 2;

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                if (ComboWheel.Combo("##uses", ref choice, options))
                {
                    onSet(slot, choice switch
                    {
                        0 => ModelMaterialLinks.Auto,
                        1 => ModelMaterialLinks.Keep,
                        _ => choice - 2,
                    });
                    _session.MarkDirty();
                }
                Gui.Tooltip(resolved >= 0
                    ? $"The build writes {written[resolved]} into this mesh part."
                    : "This mesh part keeps the name the file was made with.");

                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        ImGui.PopID();

        var plan = ModelMaterialLinks.Plan(facts.Materials, stored, written);
        int changes = ModelMaterialLinks.CountChanges(facts.Materials, plan);
        ImGui.TextColored(changes > 0 ? Theme.Muted : Theme.Faint,
            changes > 0
                ? $"{changes} of {facts.Materials.Count} mesh part(s) repointed on build"
                : "the file is copied with its own material names");
        Gui.Tooltip("The file on disk is never modified \u2014 the copy written into the mod is.");
    }

    /// <summary>A material reference as the rows show it: no leading slash, no extension.</summary>
    private static string Short(string reference)
    {
        var name = reference.TrimStart('/');
        return name.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
    }

    /// <summary>Dummy mode: the model is generated when the mod is built, from the configured materials.</summary>
    private void DrawDummySource(PortItem item, RaceModelEntry model)
    {
        Gui.Label("Generated");
        if (item.Materials.Count == 0)
        {
            ImGui.TextColored(Theme.Bad, "add a material first \u2014 each one becomes a mesh part");
        }
        else
        {
            ImGui.TextColored(Theme.Muted, $"vanilla skeleton, {item.Materials.Count} empty mesh part(s)");
            Gui.Tooltip(string.Join("\n", item.Materials.Select((mat, i) => $"part {i + 1}: {mat.Name}.mtrl")));
        }

        // For hair, the bones come from whichever vanilla hair the mod's skeleton entry points at,
        // so the generated model already fits the skeleton the game will load.
        var (basePath, fromHair) = _session.DummyBase(item, model.RaceGender);
        Gui.Label("Skeleton");
        if (fromHair != null)
        {
            ImGui.TextColored(Theme.Accent, $"from hair {fromHair}");
            Gui.Tooltip($"This race's skeleton entry points at the skeleton hair {fromHair} uses, so the generated model is built " +
                       $"on that hair's bones.\n\n{basePath}");
        }
        else
        {
            ImGui.TextColored(Theme.Muted, "this model's own");
            Gui.Tooltip(basePath);
        }

        ImGui.Spacing();
        Gui.HintWrapped("The build writes this model itself, so there is no file to keep in sync: change the materials and the "
                     + "next build follows. Export a copy to model it in Blender via InstantEdit, then switch this race to File.");
        ImGui.Spacing();
        using (Gui.Disabled(item.Materials.Count == 0))
        {
            if (Gui.IconTextButton(FontAwesomeIcon.FileExport, "Export a copy",
                    item.Materials.Count == 0 ? "Add a material first." : "Writes the generated model to a temp folder and copies its path."))
                _session.ExportDummyModel(item, model);
        }
    }

    /// <summary>
    /// The variant-mode file list: one expandable row per .mdl, each with its own material wiring —
    /// every variant starts on the same automatic match and can then differ — plus add-files (which
    /// also takes drops).
    /// </summary>
    private void DrawModelVariants(PortItem item, RaceModelEntry model, FileDropRule drop)
    {
        if (model.VariantPaths.Count == 0)
            Gui.Hint("(no files yet — add or drop at least two: each becomes an option)");
        else
            PackPlaceUi.VariantGroupRow(_session, "##modelvariantgroup", _session.VariantGroupOf(model), g => _session.SetVariantGroup(model, g));

        int remove = -1;
        for (int i = 0; i < model.VariantPaths.Count; i++)
        {
            var path = model.VariantPaths[i];
            ImGui.PushID(i);
            if (Gui.IconButton(FontAwesomeIcon.Trash, "Remove", "Remove this file", danger: true))
                remove = i;
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, File.Exists(path) ? Theme.Muted : Theme.Bad);
            bool open = ImGui.TreeNodeEx($"{Path.GetFileName(path)}##variant", ImGuiTreeNodeFlags.SpanAvailWidth);
            ImGui.PopStyleColor();
            Gui.Tooltip($"{path}\n\nOpen to choose which materials this variant's mesh parts use.");

            if (open)
            {
                int index = i;
                DrawMaterialWiring(item, model, path, model.VariantLinks(index),
                    (slot, value) => ModelMaterialLinks.Store(model.EditVariantLinks(index), slot, value), "variant");
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
        if (remove >= 0)
        {
            model.RemoveVariant(remove);
            _session.MarkDirty();
        }

        ImGui.Spacing();
        var startFrom = model.VariantPaths.Count > 0 ? model.VariantPaths[^1] : model.SourcePath;
        Gui.PickMultipleFiles("AddModelVariants", "Add file(s)", "Model files{.mdl}", picked =>
        {
            model.VariantPaths.AddRange(picked.Where(p => !model.VariantPaths.Contains(p, StringComparer.OrdinalIgnoreCase)));
            model.IsVanillaDummy = false;
            _session.MarkDirty();
        }, startFrom);
        Gui.Tooltip("Or drop .mdl files / a folder of them on this button — dropping replaces the list.");
        if (FileDrop.Target(drop.Reject, out var dropped))
            drop.OnDrop(dropped);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Material
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawMaterial(PortItem item, int index)
    {
        var subject = item.Subject;
        var mat = item.Materials[index];
        Gui.SectionHeader(string.IsNullOrWhiteSpace(mat.Name) ? "(unnamed material)" : mat.Name,
            $"Mesh slot {index + 1} of {subject.DisplayName}.");

        string name = mat.Name;
        if (Gui.LabeledInput("Name", "##MatName", ref name, 128, subject.DefaultMaterialName(index + 1, _session.NamingRace(item)),
                "The .mtrl name — also the prefix of every texture name in this material."))
        {
            mat.Name = name;
            _session.MarkDirty();
        }

        int shaderIdx = (int)mat.ShaderType;
        if (Gui.LabeledCombo("Shader", "##MatShader", ref shaderIdx, ShaderInfo.Labels,
                $"Shader pack: {ShaderInfo.ShaderPackName(mat.ShaderType)}"))
        {
            mat.ShaderType = (ShaderType)shaderIdx;
            _session.MarkDirty();
        }

        // Where the .mtrl will come from, so a shader with no template is obvious now, not at build time.
        Gui.Label("Template");
        var resolved = _host.MaterialTemplates.Resolve(item, mat);
        if (resolved == null)
        {
            ImGui.TextColored(Theme.Bad, "no bundled preset and no vanilla material to clone");
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, resolved.IsPreset ? Theme.Muted : Theme.Warn);
            ImGui.TextWrapped(resolved.Source);
            ImGui.PopStyleColor();

            // What building does to the template, so a shader key change or a dropped texture is visible now.
            var plan = MaterialPatcher.Plan(resolved.Template, mat.Textures.Select(t => t.Type), resolved.IsPreset);
            var changes = plan.Describe();
            if (changes.Length > 0)
            {
                Gui.Label("On build", "What the build changes on the template for this material's textures.");
                ImGui.PushStyleColor(ImGuiCol.Text, plan.NotWired.Any() ? Theme.Warn : Theme.Muted);
                ImGui.TextWrapped(changes);
                ImGui.PopStyleColor();
            }
        }

        PackPlaceUi.Row(_session, "##materialplace", mat.Option, $"With the item ({_session.Label(_session.PlaceOf(item))})", p => _session.SetPlace(mat, p),
            "Where its .mtrl files go in the mod — and its textures, unless one of them is put elsewhere.");

        Gui.Label("Mesh slot", "The model's material slots are the materials in tree order. Reorder with the buttons below.");
        ImGui.TextUnformatted($"{index + 1}");

        ImGui.Spacing();
        var races = subject.MaterialRaces(item.Models);
        if (races.Count == 1)
        {
            var raceName = subject.MaterialNameFor(mat.Name, races[0]);
            Gui.LabeledValue("Written as", subject.MaterialGamePath(raceName, races[0]), "The .mtrl this material produces.");
            Gui.LabeledValue("In the model", $"/{MaterialNaming.SanitizeFileName(raceName)}.mtrl",
                "The material path to set on this mesh part in Blender / TexTools.");
        }
        else
        {
            // Hair ported to several races, or gear with models of both genders: one copy each,
            // and every model references the one its own race resolves to.
            ImGui.TextColored(Theme.Muted, item.Subject is GearSubject
                ? "Written once per gender — each model references the one for its own:"
                : "Written once per race — each race's model references its own name:");
            if (ImGui.BeginTable("##PerRace", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                foreach (var race in races)
                {
                    var raceName = subject.MaterialNameFor(mat.Name, race);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.Muted, race?.DisplayName ?? "");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"/{MaterialNaming.SanitizeFileName(raceName)}.mtrl");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{subject.MaterialGamePath(raceName, race)}\n\nClick to copy.");
                    if (ImGui.IsItemClicked()) ImGui.SetClipboardText($"/{MaterialNaming.SanitizeFileName(raceName)}.mtrl");
                }
                ImGui.EndTable();
            }
        }

        ImGui.Spacing();
        Gui.Label("Preset");
        float applyW = Dalamud.Interface.Components.ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.Redo, "Apply");
        ImGui.SetNextItemWidth(-(applyW + ImGui.GetStyle().ItemSpacing.X));
        ComboWheel.Combo("##ApplyPreset", ref _presetIndex, MaterialPresets.Names);
        ImGui.SameLine();
        if (Gui.IconTextButton(FontAwesomeIcon.Redo, "Apply", "Replaces this material's shader and texture slots with the preset's."))
            _tree.AskPreset(item, index, _presetIndex);

        ImGui.Spacing();
        ImGui.Spacing();
        using (Gui.Disabled(index == 0))
            if (Gui.IconButton(FontAwesomeIcon.ArrowUp, "up", "Move up — one mesh slot earlier"))
                _session.MoveMaterial(item, index, -1);
        ImGui.SameLine();
        using (Gui.Disabled(index >= item.Materials.Count - 1))
            if (Gui.IconButton(FontAwesomeIcon.ArrowDown, "down", "Move down — one mesh slot later"))
                _session.MoveMaterial(item, index, +1);
        ImGui.SameLine();
        if (Gui.IconTextButton(FontAwesomeIcon.Copy, "Copy", "Copy this material, to paste it onto this or another model."))
            _session.CopyMaterial(item, index);
        ImGui.SameLine();
        using (Gui.Disabled(_session.CopiedMaterial == null))
        {
            if (Gui.IconTextButton(FontAwesomeIcon.Paste, "Paste below",
                    _session.CopiedMaterial is { } c ? $"Paste \"{c.Name}\" as the next mesh slot." : "Copy a material first."))
                _tree.RequestPaste(item, index);
        }
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
        if (Gui.IconTextButton(FontAwesomeIcon.Trash, "Remove"))
            _tree.AskRemoveMaterial(item, index);
        ImGui.PopStyleColor();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Texture
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawTexture(PortItem item, int materialIndex, int index)
    {
        var mat = item.Materials[materialIndex];
        var tex = mat.Textures[index];
        Gui.SectionHeader($"{TextureTypeInfo.DisplayName(tex.Type)} · {mat.Name}");

        if (_textures.DrawFull(item.Subject, mat, tex, _host.Configuration.ShowThumbnails))
            _session.MarkDirty();

        // A shared slot writes no file of its own, so it goes nowhere.
        if (!tex.IsShared && tex.UseVariants && !tex.UseWhiteDummy)
        {
            ImGui.Spacing();
            PackPlaceUi.VariantGroupRow(_session, "##texvariantgroup", _session.VariantGroupOf(tex), g => _session.SetVariantGroup(tex, g));
        }
        else if (!tex.IsShared)
        {
            ImGui.Spacing();
            PackPlaceUi.Row(_session, "##texplace", tex.Option, $"With its material ({_session.Label(_session.PlaceOf(item, mat))})", p => _session.SetPlace(tex, p),
                "Where this texture goes in the mod.");
        }

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
        if (Gui.IconTextButton(FontAwesomeIcon.Trash, "Remove texture slot"))
            _session.RemoveTexture(item, materialIndex, index);
        ImGui.PopStyleColor();
    }
}
