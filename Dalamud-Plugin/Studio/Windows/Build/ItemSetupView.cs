using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Windows.Panels;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Build;

/// <summary>
/// The centre of Build mode's Set-up view: the item picked on the left, as a tree of tiles — its race
/// models, then its materials (one per mesh slot, in order) with their textures under them — and its
/// Penumbra metadata below. Dropping files on a tile sets what it is made from; the "+" rows add races,
/// materials and texture slots; the inspector on the right shows the selected tile in full.
/// </summary>
internal sealed class ItemSetupView
{
    private const string NodeMenuId = "##portnodemenu";

    private readonly StudioHost _host;
    private readonly PortSession _session;
    private readonly TextureThumbnailCache _thumbs;
    private readonly TextureSlotEditor _textures;
    private readonly PortEditor _editor;
    private readonly ItemMetaPanel _meta;

    /// <summary>Materials folded away, by item key and material index.</summary>
    private readonly HashSet<(uint Item, int Material)> _collapsed = new();

    // Popups are opened and drawn outside the scrolling child, in one ID scope.
    private string? _openPopup;
    private Target? _menuTarget;
    private int _popupMaterial = -1;

    public ItemSetupView(StudioHost host, PortSession session, TextureThumbnailCache thumbs, TextureSlotEditor textures, PortEditor editor)
    {
        _host   = host;
        _session  = session;
        _thumbs   = thumbs;
        _textures = textures;
        _editor   = editor;
        _meta     = new ItemMetaPanel(host, session);
    }

    private bool ShowThumbs => _host.Configuration.ShowThumbnails;

    public void Draw()
    {
        if (!_session.Loaded)
        {
            Gui.Hint("Reading the game's item list");
            return;
        }

        var item = _session.CurrentItem;
        if (item == null)
        {
            Gui.Icon(FontAwesomeIcon.ArrowLeft, Theme.Muted);
            ImGui.SameLine();
            Gui.HintWrapped(_session.Items.Count == 0
                ? "Nothing in the modpack yet. Add gear or a character feature from the game."
                : "Pick one of the modpack's items on the left to set it up.");
            return;
        }

        DrawHeader(item);
        ImGui.Spacing();

        var cfg = _host.Configuration;
        float splitter = Theme.SplitterWidth;
        float total    = ImGui.GetContentRegionAvail().Y;
        float minPane  = Theme.S(90);
        float metaH    = Math.Clamp(Theme.S(cfg.MetaPaneHeight), minPane, Math.Max(minPane, total - minPane - splitter));

        if (ImGui.BeginChild("##tree", new Vector2(-1, total - metaH - splitter), false))
            DrawTree(item);
        ImGui.EndChild();
        // The tree takes gear dragged out of the item browser.
        if (SubjectDrop.Target() is { } subject)
            _session.AddItem(subject);

        bool released = Gui.HorizontalSplitter("##SplitMeta", ref metaH, minPane, total - minPane - splitter,
            ImGui.GetContentRegionAvail().X, invert: true);
        if (ImGui.IsItemActive() || released)
            cfg.MetaPaneHeight = metaH / Theme.S(1);
        if (released)
            cfg.Save();

        if (ImGui.BeginChild("##meta", new Vector2(-1, -1), false))
            _meta.Draw(item);
        ImGui.EndChild();

        DrawPopups(item);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Header
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawHeader(PortItem item)
    {
        var subject = item.Subject;
        var target  = new Target(TargetKind.Item, item.Key);

        var badges = new List<Badge>();
        if (!item.HasWork)
            badges.Add(new Badge("not set up", Theme.Faint, "Nothing is set for it yet: a build leaves it out."));
        badges.With(PackPlaceUi.PlaceBadge(_session, _session.PlaceOf(item), PackPlace.Default));

        var worst = PortValidator.WorstUnder(_session.Issues, target);
        uint icon = GameIcons.Of(subject);
        var result = Tile.Draw(new TileSpec
        {
            Id        = "header",
            Title     = subject.DisplayName,
            Subtitle  = $"{subject.KindLabel} · {subject.IdDisplay}",
            Detail    = item.Summary(withTextures: true),
            Icon      = GameIcons.Glyph(subject),
            IconColor = Theme.Accent,
            DrawThumb = icon != 0 ? side => GameIcons.Draw(icon, side) : null,
            Badges    = badges,
            Actions   = new[] { new TileAction(FontAwesomeIcon.Trash, "Remove from the modpack (its set-up is kept)", () => _editor.AskRemoveItem(item), Theme.Bad) },
            Large     = true,
            Selected  = _session.Selection == target,
            Issue     = worst,
            IssueText = worst != null ? IssuesText(target) : null,
            Tooltip   = $"Materials: {subject.MaterialFolder(subject.BaseRace)}\nTextures: {subject.TextureFolder}",
        }, new Vector2(ImGui.GetContentRegionAvail().X, Tile.Height(large: true)));

        if (result.Clicked)
            _session.Selection = target;
        if (SubjectDrop.Target() is { } dropped)
            _session.AddItem(dropped);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tree
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawTree(PortItem item)
    {
        float width = ImGui.GetContentRegionAvail().X;
        float indent = Theme.T(22);
        float tileH = Tile.Height(detail: true);
        var zone = new Vector2(width, Theme.T(34));

        ImGui.PushID((int)item.Key);
        for (int i = 0; i < item.Models.Count; i++)
            DrawModel(item, i, new Vector2(width, tileH));

        if (item.Subject.MultiRace)
        {
            var addable = _session.AddableRaces(item);
            var hint = addable.Count > 0 ? "Race model — or drop .mdl files on a race's tile" : "Every race that can have this already has a model";
            if (DropZone.Draw("+model", hint, FontAwesomeIcon.Plus, zone, null).Clicked && addable.Count > 0)
                _openPopup = PortEditor.AddRacePopup;
            if (item.Models.Count == 0)
                Gui.HintWrapped("No model files yet — without one, only materials and textures are replaced.");
        }

        ImGui.Spacing();
        for (int m = 0; m < item.Materials.Count; m++)
        {
            bool open = DrawMaterial(item, m, new Vector2(width, tileH));
            if (!open)
                continue;

            for (int t = 0; t < item.Materials[m].Textures.Count; t++)
                DrawTexture(item, m, t, new Vector2(width, tileH), indent);

            if (DropZone.Draw($"+tex{m}", "Texture slot", FontAwesomeIcon.Plus, zone, null, indent).Clicked)
            {
                _popupMaterial = m;
                _openPopup = PortEditor.AddTexturePopup;
            }
        }

        if (DropZone.Draw("+material", "Material — one more mesh slot", FontAwesomeIcon.Plus, zone, null).Clicked)
            _openPopup = PortEditor.AddMaterialPopup;
        ImGui.PopID();
    }

    /// <summary>Scrolls the tile about to be drawn into view when a "go to" asked for it.</summary>
    private void RevealHere(Target target)
    {
        if (_session.TakeScrollRequest(target))
            ImGui.SetScrollHereY(0.3f);
    }

    private void DrawModel(PortItem item, int index, Vector2 size)
    {
        var subject = item.Subject;
        var model   = item.Models[index];
        var target  = new Target(TargetKind.Model, item.Key, index);
        var (text, color) = Describe(item, model);

        var badges = new List<Badge>();
        if (!_host.GameData.GetNativeRaces(subject).Contains(model.RaceGender))
            badges.Add(subject is GearSubject
                ? new Badge("override", Theme.Accent, "This race has no model by default — an Eqdp override is added so the game uses this one.")
                : new Badge("no vanilla", Theme.Warn, $"This race does not ship this {subject.KindLabel.ToLowerInvariant()}, so the game may never ask for the file."));
        if (model.UseVariants && !model.UseDummy)
            badges.With(_session.VariantGroupOf(model) is { } group ? new Badge(group.Name, Theme.Accent, $"Each file becomes an option of {group.Name}.") : null);
        else
            badges.With(PackPlaceUi.PlaceBadge(_session, _session.PlaceOf(item, model), _session.PlaceOf(item)));

        var actions = new List<TileAction>
        {
            new(FontAwesomeIcon.FolderOpen, subject.HasRaces ? "Pick the .mdl this race is made from" : "Pick the .mdl", () => _editor.BrowseModel(model)),
        };
        if (subject.MultiRace && !PortSession.IsBaseRace(item, model.RaceGender))
            actions.Add(new TileAction(FontAwesomeIcon.Trash, "Remove this race (the file on disk is not touched)",
                () => _session.RemoveModel(item, index), Theme.Bad));

        RevealHere(target);
        var worst = PortValidator.WorstUnder(_session.Issues, target);
        var drop = _editor.ModelDropRule(item, model);
        var result = Tile.Draw(new TileSpec
        {
            Id            = $"model{index}",
            Title         = subject.HasRaces ? model.RaceGender.DisplayName : "Model",
            Subtitle      = text,
            SubtitleColor = color,
            Detail        = model.UseDummy || model.UseVariants || string.IsNullOrWhiteSpace(model.SourcePath)
                ? subject.ModelGamePath(model.RaceGender)
                : model.SourcePath,
            Icon          = FontAwesomeIcon.Cube,
            IconColor     = Theme.Accent,
            Badges        = badges,
            Actions       = actions,
            Selected      = _session.Selection == target,
            Issue         = worst,
            IssueText     = worst != null ? IssuesText(target) : null,
            Drop          = drop,
            Tooltip       = subject.ModelGamePath(model.RaceGender),
        }, size);

        if (result.Clicked)
            _session.Selection = target;
        if (result.RightClicked)
            OpenMenu(target);
        if (result.Dropped is { } dropped)
        {
            drop.OnDrop(dropped);
            _session.Selection = target;
        }
    }

    /// <summary>What a race tile says under its name: where its model comes from.</summary>
    private static (string Text, Vector4? Color) Describe(PortItem item, RaceModelEntry model)
    {
        if (model.UseDummy)
            return ($"Generated dummy · {item.Materials.Count} mesh part(s)", Theme.Accent);
        if (model.UseVariants)
            return model.VariantPaths.Count == 0
                ? ("Variants · no files set", Theme.Faint)
                : ($"Variants · {model.VariantPaths.Count} file(s): {string.Join(", ", model.VariantPaths.Select(Path.GetFileName))}", null);
        if (string.IsNullOrWhiteSpace(model.SourcePath))
            return ("No model file set — drop a .mdl here", Theme.Faint);
        return ($"Model · {Path.GetFileName(model.SourcePath)}", null);
    }

    /// <summary>Draws a material's tile; returns whether its textures are shown under it.</summary>
    private bool DrawMaterial(PortItem item, int index, Vector2 size)
    {
        var mat    = item.Materials[index];
        var target = new Target(TargetKind.Material, item.Key, index);
        var key    = (item.Key, index);

        if (_session.WantsOpen(target))
            _collapsed.Remove(key);
        bool open = !_collapsed.Contains(key);

        RevealHere(target);
        var worst = PortValidator.WorstUnder(_session.Issues, target);
        var result = Tile.Draw(new TileSpec
        {
            Id        = $"mat{index}",
            Expanded  = open,
            Title     = mat.Name,
            Subtitle  = $"Material · {ShaderInfo.DisplayName(mat.ShaderType)} · {mat.Textures.Count} texture(s)",
            Detail    = item.Subject.MaterialGamePath(mat.Name, _session.NamingRace(item)),
            Icon      = FontAwesomeIcon.Palette,
            IconColor = Theme.Accent,
            Badges    = new List<Badge> { new($"slot {index + 1}", Theme.Muted, $"Mesh slot {index + 1}: the model's material slot {index + 1} references this material by its name.") }
                .With(PackPlaceUi.PlaceBadge(_session, _session.PlaceOf(item, mat), _session.PlaceOf(item))),
            Actions   = new[] { new TileAction(FontAwesomeIcon.Trash, "Remove this material", () => _editor.AskRemoveMaterial(item, index), Theme.Bad) },
            Selected  = _session.Selection == target,
            Issue     = worst,
            IssueText = worst != null ? IssuesText(target) : null,
            Tooltip   = $"Shader pack: {ShaderInfo.ShaderPackName(mat.ShaderType)}",
        }, size);

        if (result.Toggled || result.DoubleClicked)
        {
            if (!_collapsed.Add(key))
                _collapsed.Remove(key);
        }
        else if (result.Clicked)
        {
            _session.Selection = target;
        }
        if (result.RightClicked)
            OpenMenu(target);
        return open;
    }

    private void DrawTexture(PortItem item, int materialIndex, int index, Vector2 size, float indent)
    {
        var mat    = item.Materials[materialIndex];
        var tex    = mat.Textures[index];
        var target = new Target(TargetKind.Texture, item.Key, materialIndex, index);
        var gamePath = item.Subject.TextureGamePath(MaterialNaming.ComposeTextureName(mat.Name, tex.Postfix));
        var (text, color) = Describe(tex);

        var badges = new List<Badge>();
        if (tex.IsShared)
            badges.Add(new Badge("shared", Theme.Accent, $"Uses {tex.SharedGamePath}, which the material it was copied from writes. No texture of its own is written."));
        if (!ShaderInfo.SupportedTextures(mat.ShaderType).Contains(tex.Type))
            badges.Add(new Badge("unusual", Theme.Warn, $"Not normally used by {ShaderInfo.DisplayName(mat.ShaderType)} — the shader may ignore it."));
        if (tex.UseVariants && !tex.UseWhiteDummy && !tex.IsShared)
            badges.With(_session.VariantGroupOf(tex) is { } group ? new Badge(group.Name, Theme.Accent, $"Each image becomes an option of {group.Name}.") : null);
        else if (!tex.IsShared)
            badges.With(PackPlaceUi.PlaceBadge(_session, _session.PlaceOf(item, mat, tex), _session.PlaceOf(item, mat)));

        var actions = new List<TileAction>();
        if (!tex.IsShared)
            actions.Add(new TileAction(FontAwesomeIcon.FolderOpen, "Pick the image this texture is made from", () => _editor.BrowseTexture(tex)));
        actions.Add(new TileAction(FontAwesomeIcon.Trash, "Remove this texture slot", () => _session.RemoveTexture(item, materialIndex, index), Theme.Bad));

        RevealHere(target);
        var worst = PortValidator.WorstUnder(_session.Issues, target);
        var drop = tex.IsShared ? null : _textures.DropRule(tex);
        var result = Tile.Draw(new TileSpec
        {
            Id            = $"tex{materialIndex}.{index}",
            Indent        = indent,
            Title         = TextureTypeInfo.DisplayName(tex.Type),
            Subtitle      = text,
            SubtitleColor = color,
            Detail        = !tex.IsShared && !tex.UseWhiteDummy && !string.IsNullOrWhiteSpace(tex.SourcePath) ? tex.SourcePath : tex.IsShared ? tex.SharedGamePath : gamePath,
            Icon          = FontAwesomeIcon.Image,
            IconColor     = Theme.Accent,
            DrawThumb     = ShowThumbs ? side => _thumbs.Draw(tex, side) : null,
            Badges        = badges,
            Actions       = actions,
            Selected      = _session.Selection == target,
            Issue         = worst,
            IssueText     = worst != null ? IssuesText(target) : null,
            Drop          = drop,
            Tooltip       = gamePath,
        }, size);

        if (result.Clicked)
            _session.Selection = target;
        if (result.RightClicked)
            OpenMenu(target);
        if (result.Dropped is { } dropped && drop != null)
        {
            drop.OnDrop(dropped);
            _session.Selection = target;
        }
    }

    /// <summary>What a texture tile says under its role: where its image comes from.</summary>
    private static (string Text, Vector4? Color) Describe(TextureSlot tex)
    {
        if (tex.IsShared)
            return ($"Shared · {Path.GetFileName(tex.SharedGamePath)}", Theme.Accent);
        if (tex.UseWhiteDummy)
            return ($"Placeholder · {DummyTextureLibrary.Resolve(tex.Type, tex.DummyPreset).Label}", Theme.Accent);
        if (string.IsNullOrWhiteSpace(tex.SourcePath))
            return ("No image set — drop one here", Theme.Faint);
        if (tex.UseVariants)
            return ($"Variants · {Path.GetFileName(tex.SourcePath.TrimEnd('\\', '/'))}", null);
        return ($"Texture · {Path.GetFileName(tex.SourcePath)}", null);
    }

    private string IssuesText(Target target)
        => string.Join("\n", _session.Issues.Where(i => i.Target.IsWithin(target)).Select(i => i.Message));

    // ─────────────────────────────────────────────────────────────────────────
    // Popups
    // ─────────────────────────────────────────────────────────────────────────

    private void OpenMenu(Target target)
    {
        _session.Selection = target;
        _menuTarget = target;
        _openPopup = NodeMenuId;
    }

    private void DrawPopups(PortItem item)
    {
        if (_openPopup != null)
        {
            ImGui.OpenPopup(_openPopup);
            _openPopup = null;
        }

        _editor.DrawAddRacePopup(item);
        _editor.DrawAddMaterialPopup(item);
        _editor.DrawAddTexturePopup(item, _popupMaterial);

        if (!ImGui.BeginPopup(NodeMenuId))
            return;

        if (_menuTarget is not { } t || t.Item != item.Key)
        {
            ImGui.EndPopup();
            return;
        }

        switch (t.Kind)
        {
            case TargetKind.Material:
                _editor.DrawMaterialMenu(item, t.Index);
                break;

            case TargetKind.Texture when t.Index >= 0 && t.Index < item.Materials.Count
                                      && t.SubIndex >= 0 && t.SubIndex < item.Materials[t.Index].Textures.Count:
                if (ImGui.MenuItem("Copy game path"))
                    ImGui.SetClipboardText(item.Subject.TextureGamePath(
                        MaterialNaming.ComposeTextureName(item.Materials[t.Index].Name, item.Materials[t.Index].Textures[t.SubIndex].Postfix)));
                ImGui.Separator();
                if (ImGui.MenuItem("Remove this texture slot"))
                    _session.RemoveTexture(item, t.Index, t.SubIndex);
                break;

            case TargetKind.Model when t.Index >= 0 && t.Index < item.Models.Count:
                var model = item.Models[t.Index];
                if (ImGui.MenuItem("Copy game path"))
                    ImGui.SetClipboardText(item.Subject.ModelGamePath(model.RaceGender));
                if (item.Subject.MultiRace && !PortSession.IsBaseRace(item, model.RaceGender))
                {
                    ImGui.Separator();
                    if (ImGui.MenuItem("Remove this race"))
                        _session.RemoveModel(item, t.Index);
                }
                break;

            default:
                ImGui.CloseCurrentPopup();
                break;
        }
        ImGui.EndPopup();
    }
}
