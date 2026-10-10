using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Build;

/// <summary>
/// The structure of the mod Build mode makes: an option (or the default files) in the centre with
/// everything put into it — items, files on their own, variant files — or a group with its options;
/// and in the inspector, their names, descriptions, how a group's options are picked and their order.
/// Also the popups that make and take out groups and options.
/// </summary>
internal sealed class PackStructureView
{
    private const string NewGroupPopupId     = "New group##StudioPackNewGroup";
    private const string NewOptionPopupId    = "New option##StudioPackNewOption";
    private const string RemoveGroupPopupId  = "Remove the group?##StudioPackRemoveGroup";
    private const string RemoveOptionPopupId = "Remove the option?##StudioPackRemoveOption";

    private readonly StudioHost _host;
    private readonly PortSession _session;
    private readonly TextureThumbnailCache _thumbs;

    private string _newName = string.Empty;
    private bool _newMulti;
    private PackGroup? _newOptionGroup;
    private PackGroup? _removeGroup;
    private (PackGroup Group, PackOption Option)? _removeOption;
    private string? _openPopup;

    public PackStructureView(StudioHost host, PortSession session, TextureThumbnailCache thumbs)
    {
        _host  = host;
        _session = session;
        _thumbs  = thumbs;
    }

    /// <summary>Whether the selection is part of the structure (an option, the default files, or a group), which this view shows.</summary>
    public bool Shows(Target? selection) => selection is { Kind: TargetKind.Option or TargetKind.Group };

    // ─────────────────────────────────────────────────────────────────────────
    // Actions other views start
    // ─────────────────────────────────────────────────────────────────────────

    public void BeginNewGroup()
    {
        _newName = string.Empty;
        _newMulti = false;
        _openPopup = NewGroupPopupId;
    }

    public void BeginNewOption(PackGroup group)
    {
        if (group.Multi && group.Options.Count >= PortSession.MaxMultiOptions)
        {
            _session.Notify($"A group players pick any number of can have at most {PortSession.MaxMultiOptions} options.", Severity.Warning);
            return;
        }
        _newOptionGroup = group;
        _newName = string.Empty;
        _openPopup = NewOptionPopupId;
    }

    public void AskRemove(PackGroup group)
    {
        _removeGroup = group;
        _openPopup = RemoveGroupPopupId;
    }

    public void AskRemove(PackGroup group, PackOption option)
    {
        _removeOption = (group, option);
        _openPopup = RemoveOptionPopupId;
    }

    /// <summary>Puts gear dragged from the browser into a place: added to the modpack if it is not in it yet.</summary>
    public void DropItemInto(PortSubject subject, string place)
    {
        var item = _session.AddItem(subject);
        _session.SetPlace(item, place);
        _session.Notify($"{subject.DisplayName} goes into {_session.Label(place)}.", Severity.Info);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Canvas
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawCanvas()
    {
        switch (_session.Selection)
        {
            case { Kind: TargetKind.Group } g when Guid.TryParse(g.Root, out var id) && _session.FindGroup(id) is { } group:
                DrawGroupCanvas(group);
                break;
            case { Kind: TargetKind.Option } o when _session.Exists(o.Option):
                DrawOptionCanvas(o.Option);
                break;
            default:
                Gui.Hint("This is no longer in the mod.");
                break;
        }
    }

    private void DrawOptionCanvas(string place)
    {
        var found = _session.Find(place);
        var contents = _session.ContentsOf(place);
        int items = contents.Count(c => c.Kind == PlacedKind.Item);
        int files = contents.Count - items;

        var actions = new List<TileAction>();
        if (found is { } f)
        {
            actions.Add(new TileAction(FontAwesomeIcon.ArrowUp, "Move up", () => _session.MoveOption(f.Group, f.Group.Options.IndexOf(f.Option), -1)));
            actions.Add(new TileAction(FontAwesomeIcon.ArrowDown, "Move down", () => _session.MoveOption(f.Group, f.Group.Options.IndexOf(f.Option), +1)));
            actions.Add(new TileAction(FontAwesomeIcon.Trash, "Take the option out", () => AskRemove(f.Group, f.Option), Theme.Bad));
        }

        Tile.Draw(new TileSpec
        {
            Id        = "optionheader",
            Title     = found?.Option.Name ?? "Default files",
            Subtitle  = found is { } g ? $"{g.Group.Name} · {(g.Group.Multi ? "pick any" : "pick one")}" : "Always in use while the mod is enabled",
            Detail    = $"{items} item(s) · {files} file(s) on their own",
            Icon      = found == null ? FontAwesomeIcon.CheckCircle : found.Value.Group.Multi ? FontAwesomeIcon.CheckSquare : FontAwesomeIcon.DotCircle,
            IconColor = Theme.Accent,
            Actions   = actions,
            Large     = true,
            Selected  = true,
            Tooltip   = "Drop gear from the item browser here to put it into this option.",
        }, new Vector2(ImGui.GetContentRegionAvail().X, Tile.Height(large: true)));
        if (SubjectDrop.Target() is { } subject)
            DropItemInto(subject, place);

        ImGui.Spacing();
        if (contents.Count == 0)
        {
            Gui.HintWrapped(found == null
                ? "Nothing goes into the default files yet."
                : "Nothing goes into this option yet. Put an item here from its inspector (Goes into), or a single model, material or texture of one — " +
                  "or drop gear from the item browser onto this page. Variant files go into the option named after them.");
            if (found != null)
                DrawEmptyHint();
            return;
        }

        if (!ImGui.BeginChild("##placed", new Vector2(-1, -1), false))
        {
            ImGui.EndChild();
            return;
        }
        float width = ImGui.GetContentRegionAvail().X;
        var tileH = Tile.Height(detail: true);
        int index = 0;
        foreach (var kind in new[] { PlacedKind.Item, PlacedKind.Model, PlacedKind.Material, PlacedKind.Texture, PlacedKind.Variant })
        {
            var ofKind = contents.Where(c => c.Kind == kind).ToList();
            if (ofKind.Count == 0)
                continue;
            ImGui.Spacing();
            ImGui.TextColored(Theme.Accent, kind switch
            {
                PlacedKind.Item     => $"Items ({ofKind.Count}) — with their files, unless one is put elsewhere",
                PlacedKind.Model    => $"Models on their own ({ofKind.Count})",
                PlacedKind.Material => $"Materials on their own ({ofKind.Count}) — with their textures",
                PlacedKind.Texture  => $"Textures on their own ({ofKind.Count})",
                _                   => $"Variant files ({ofKind.Count})",
            });
            foreach (var placed in ofKind)
                DrawPlaced(placed, index++, new Vector2(width, tileH));
        }
        ImGui.EndChild();
    }

    private void DrawEmptyHint()
    {
        ImGui.Spacing();
        var zone = DropZone.Draw("dropgear", "Drop gear from the item browser here", FontAwesomeIcon.Tshirt,
            new Vector2(ImGui.GetContentRegionAvail().X, Theme.T(56)), null);
        if (zone.Clicked)
            _session.GoToStage(StageId.Browse);
        if (SubjectDrop.Target() is { } subject && _session.Selection is { Kind: TargetKind.Option } o)
            DropItemInto(subject, o.Option);
    }

    private void DrawPlaced(Placed placed, int index, Vector2 size)
    {
        var item = placed.Item;
        uint icon = placed.Kind == PlacedKind.Item ? GameIcons.Of(item.Subject) : 0;
        Action<float>? thumb = null;
        if (icon != 0)
            thumb = side => GameIcons.Draw(icon, side);
        else if (placed.File != null && FileDrop.IsImage(placed.File) && _host.Configuration.ShowThumbnails)
            thumb = side => _thumbs.DrawFile(placed.File, side);

        var result = Tile.Draw(new TileSpec
        {
            Id        = $"placed{index}",
            Title     = placed.Label,
            Subtitle  = placed.Detail,
            Detail    = placed.File,
            Icon      = placed.Kind switch
            {
                PlacedKind.Item     => item.Subject.Kind == SubjectKind.Gear ? FontAwesomeIcon.Tshirt : FontAwesomeIcon.User,
                PlacedKind.Model    => FontAwesomeIcon.Cube,
                PlacedKind.Material => FontAwesomeIcon.Palette,
                _                   => placed.Target.Kind == TargetKind.Model ? FontAwesomeIcon.Cube : FontAwesomeIcon.Image,
            },
            IconColor = Theme.Accent,
            DrawThumb = thumb,
            Tooltip   = "Click to set it up.",
        }, size);
        if (result.Clicked)
            _session.Focus(placed.Target);
    }

    private void DrawGroupCanvas(PackGroup group)
    {
        var actions = new List<TileAction>
        {
            new(FontAwesomeIcon.Plus, "Add an option", () => BeginNewOption(group), Enabled: !group.Multi || group.Options.Count < PortSession.MaxMultiOptions),
            new(FontAwesomeIcon.ArrowUp, "Move the group up", () => _session.MoveGroup(group, -1)),
            new(FontAwesomeIcon.ArrowDown, "Move the group down", () => _session.MoveGroup(group, +1)),
            new(FontAwesomeIcon.Trash, "Take the group out", () => AskRemove(group), Theme.Bad),
        };
        Tile.Draw(new TileSpec
        {
            Id        = "groupheader",
            Title     = group.Name,
            Subtitle  = group.Multi ? "Players pick any number of its options" : "Players pick one of its options",
            Detail    = $"{group.Options.Count} option(s)",
            Icon      = group.Multi ? FontAwesomeIcon.CheckSquare : FontAwesomeIcon.DotCircle,
            IconColor = Theme.Accent,
            Actions   = actions,
            Large     = true,
            Selected  = true,
        }, new Vector2(ImGui.GetContentRegionAvail().X, Tile.Height(large: true)));

        ImGui.Spacing();
        if (!ImGui.BeginChild("##options", new Vector2(-1, -1), false))
        {
            ImGui.EndChild();
            return;
        }
        float width = ImGui.GetContentRegionAvail().X;
        float tileH = Tile.Height(detail: true);
        for (int i = 0; i < group.Options.Count; i++)
        {
            var option = group.Options[i];
            var place = PackPlace.Of(group.Id, option.Id);
            var contents = _session.ContentsOf(place);
            var image = contents.FirstOrDefault(c => c.File != null && FileDrop.IsImage(c.File))?.File;
            int index = i;
            var result = Tile.Draw(new TileSpec
            {
                Id        = $"opt{option.Id}",
                Title     = option.Name,
                Subtitle  = contents.Count == 0 ? "nothing goes into it yet" : Describe(contents),
                SubtitleColor = contents.Count == 0 ? Theme.Faint : null,
                Detail    = option.Description.Length > 0 ? option.Description.Replace('\n', ' ') : null,
                Icon      = group.Multi ? FontAwesomeIcon.CheckSquare : FontAwesomeIcon.DotCircle,
                IconColor = Theme.Muted,
                DrawThumb = image != null && _host.Configuration.ShowThumbnails ? side => _thumbs.DrawFile(image, side) : null,
                Actions   = new[]
                {
                    new TileAction(FontAwesomeIcon.ArrowUp, "Move up", () => _session.MoveOption(group, index, -1), Enabled: index > 0),
                    new TileAction(FontAwesomeIcon.ArrowDown, "Move down", () => _session.MoveOption(group, index, +1), Enabled: index < group.Options.Count - 1),
                },
                Tooltip   = "Click to see what goes into it. Drop gear from the item browser here to put it into this option.",
            }, new Vector2(width, tileH));
            if (result.Clicked)
                _session.Focus(Target.ForPackOption(place));
            if (SubjectDrop.Target() is { } subject)
                DropItemInto(subject, place);
        }

        var takes = !group.Multi || group.Options.Count < PortSession.MaxMultiOptions;
        if (DropZone.Draw("+option", takes ? "Add an option" : $"At most {PortSession.MaxMultiOptions} options", FontAwesomeIcon.Plus,
                new Vector2(width, Theme.T(34)), null).Clicked && takes)
            BeginNewOption(group);
        ImGui.EndChild();
    }

    private static string Describe(List<Placed> contents)
    {
        var parts = new List<string>();
        int items = contents.Count(c => c.Kind == PlacedKind.Item);
        int variants = contents.Count(c => c.Kind == PlacedKind.Variant);
        int own = contents.Count - items - variants;
        if (items > 0) parts.Add($"{items} item(s)");
        if (own > 0) parts.Add($"{own} file(s) on their own");
        if (variants > 0) parts.Add(string.Join(", ", contents.Where(c => c.Kind == PlacedKind.Variant).Select(c => c.Detail).Take(3)) + (variants > 3 ? " and more" : string.Empty));
        return string.Join(" · ", parts);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawInspector()
    {
        switch (_session.Selection)
        {
            case { Kind: TargetKind.Group } g when Guid.TryParse(g.Root, out var id) && _session.FindGroup(id) is { } group:
                DrawGroupInspector(group);
                break;
            case { Kind: TargetKind.Option } o when _session.Find(o.Option) is { } found:
                DrawOptionInspector(found.Group, found.Option);
                break;
            case { Kind: TargetKind.Option }:
                Gui.SectionHeader("Default files");
                Gui.HintWrapped("Always in use while the mod is enabled. Every item goes here unless it is put into an option, and its files with it unless one of them is put elsewhere.");
                break;
        }
    }

    private void DrawOptionInspector(PackGroup group, PackOption option)
    {
        Gui.SectionHeader(option.Name, "Written into the mod's meta.json with the next build.");

        string name = option.Name;
        if (Gui.LabeledInput("Name", "##packoptname", ref name, 128) && name.Trim().Length > 0)
            _session.Rename(option, name);
        Gui.Label("Description", "What Penumbra shows for the option when it is hovered.");
        string description = option.Description;
        if (ImGui.InputTextMultiline("##packoptdesc", ref description, 4096, new Vector2(-1, ImGui.GetTextLineHeight() * 4 + ImGui.GetStyle().FramePadding.Y * 2)))
        {
            option.Description = description;
            _session.MarkDirty();
        }
        Gui.Label("Group");
        if (ImGui.Selectable($"{group.Name}##packoptgroup", false))
            _session.Focus(Target.ForPackGroup(group.Id));
        Gui.Tooltip("Click to see all of its options.");
        if (option.Key.Length > 0)
            Gui.LabeledValue("Made for", option.Key, "Variant files of this name go into this option, whatever it is called.");

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
        if (Gui.IconTextButton(FontAwesomeIcon.Trash, "Take the option out"))
            AskRemove(group, option);
        ImGui.PopStyleColor();
    }

    private void DrawGroupInspector(PackGroup group)
    {
        Gui.SectionHeader(group.Name, "Written into the mod's meta.json with the next build.");

        string name = group.Name;
        if (Gui.LabeledInput("Name", "##packgroupname", ref name, 128) && name.Trim().Length > 0)
            _session.Rename(group, name);
        Gui.Label("Description");
        string description = group.Description;
        if (ImGui.InputTextMultiline("##packgroupdesc", ref description, 4096, new Vector2(-1, ImGui.GetTextLineHeight() * 3 + ImGui.GetStyle().FramePadding.Y * 2)))
        {
            group.Description = description;
            _session.MarkDirty();
        }

        Gui.Label("Players pick", "Single: exactly one option at a time. Multi: any number of them (at most 32 options).");
        if (ImGui.RadioButton("one option##packsingle", !group.Multi))
            _session.SetMulti(group, false);
        ImGui.SameLine();
        var whyNot = PortSession.WhyNotMulti(group);
        using (Gui.Disabled(whyNot != null))
        {
            if (ImGui.RadioButton("any number##packmulti", group.Multi))
                _session.SetMulti(group, true);
        }
        Gui.Tooltip(whyNot);

        var slots = _session.VariantSlotsOf(group);
        if (slots.Count > 0)
        {
            ImGui.Spacing();
            Gui.SectionHeader("Filled by variants", "Each file of these slots goes into the option made for its name; a file with no option yet gets one.");
            foreach (var (target, label) in slots)
            {
                if (ImGui.Selectable($"{label}##{target}", false))
                    _session.Focus(target);
            }
        }
        if (group.LeftOut.Count > 0)
        {
            ImGui.Spacing();
            Gui.SectionHeader("Left out", "Variant files whose option was taken out. They stay out of the mod until brought back.");
            foreach (var key in group.LeftOut.ToList())
            {
                ImGui.PushID(key);
                if (ImGui.SmallButton("Bring back"))
                    _session.BringBack(group, key);
                ImGui.SameLine();
                ImGui.TextColored(Theme.Muted, key);
                ImGui.PopID();
            }
        }

        ImGui.Spacing();
        Gui.SectionHeader($"Options ({group.Options.Count})");
        for (int i = 0; i < group.Options.Count; i++)
        {
            var option = group.Options[i];
            ImGui.PushID(option.Id.ToString());
            using (Gui.Disabled(i == 0))
                if (Gui.IconButton(FontAwesomeIcon.ArrowUp, "up", "Move up"))
                    _session.MoveOption(group, i, -1);
            ImGui.SameLine();
            using (Gui.Disabled(i == group.Options.Count - 1))
                if (Gui.IconButton(FontAwesomeIcon.ArrowDown, "down", "Move down"))
                    _session.MoveOption(group, i, +1);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            if (ImGui.Selectable(option.Name, false))
                _session.Focus(Target.ForPackOption(PackPlace.Of(group.Id, option.Id)));
            ImGui.PopID();
        }
        if (Gui.IconTextButton(FontAwesomeIcon.Plus, "Add an option"))
            BeginNewOption(group);

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
        if (Gui.IconTextButton(FontAwesomeIcon.Trash, "Take the group out"))
            AskRemove(group);
        ImGui.PopStyleColor();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Popups
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawPopups()
    {
        if (_openPopup != null)
        {
            ImGui.OpenPopup(_openPopup);
            _openPopup = null;
        }

        if (ImGui.BeginPopupModal(NewGroupPopupId, ImGuiWindowFlags.AlwaysAutoResize))
        {
            Gui.HintWrapped("A group of options players pick between in Penumbra. Put items or files into its options afterwards.");
            Gui.Label("Name");
            ImGui.SetNextItemWidth(Theme.S(320));
            if (ImGui.IsWindowAppearing())
                ImGui.SetKeyboardFocusHere();
            bool enter = ImGui.InputTextWithHint("##packnewgroup", "Colour, Style, Outfit", ref _newName, 128, ImGuiInputTextFlags.EnterReturnsTrue);
            Gui.Label("Players pick");
            if (ImGui.RadioButton("one option##newsingle", !_newMulti)) _newMulti = false;
            ImGui.SameLine();
            if (ImGui.RadioButton("any number##newmulti", _newMulti)) _newMulti = true;

            var trimmed = _newName.Trim();
            var problem = trimmed.Length == 0 ? "Give it a name."
                        : _session.Groups.Any(g => string.Equals(g.Name, trimmed, StringComparison.OrdinalIgnoreCase)) ? "There is a group with that name already." : null;
            if (problem != null)
                ImGui.TextColored(Theme.Warn, problem);
            ImGui.Spacing();
            using (Gui.Disabled(problem != null))
            {
                if ((Gui.PrimaryButton(FontAwesomeIcon.FolderPlus, "Make it", Theme.S(140)) || enter) && problem == null)
                {
                    var group = _session.AddGroup(trimmed, _newMulti);
                    _session.Focus(Target.ForPackGroup(group.Id));
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(Theme.S(100), 0)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopupModal(NewOptionPopupId, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var group = _newOptionGroup;
            if (group == null || !_session.Groups.Contains(group))
            {
                ImGui.CloseCurrentPopup();
            }
            else
            {
                ImGui.TextUnformatted($"A new option of {group.Name}");
                Gui.Label("Name");
                ImGui.SetNextItemWidth(Theme.S(320));
                if (ImGui.IsWindowAppearing())
                    ImGui.SetKeyboardFocusHere();
                bool enter = ImGui.InputTextWithHint("##packnewoption", "Red, Short, Matte", ref _newName, 128, ImGuiInputTextFlags.EnterReturnsTrue);
                var trimmed = _newName.Trim();
                var problem = trimmed.Length == 0 ? "Give it a name."
                            : group.Options.Any(o => string.Equals(o.Name, trimmed, StringComparison.OrdinalIgnoreCase)) ? $"{group.Name} has an option with that name already." : null;
                if (problem != null)
                    ImGui.TextColored(Theme.Warn, problem);
                ImGui.Spacing();
                using (Gui.Disabled(problem != null))
                {
                    if ((Gui.PrimaryButton(FontAwesomeIcon.Plus, "Add it", Theme.S(140)) || enter) && problem == null)
                    {
                        if (_session.AddOption(group, trimmed) is { } option)
                            _session.Focus(Target.ForPackOption(PackPlace.Of(group.Id, option.Id)));
                        ImGui.CloseCurrentPopup();
                    }
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel", new Vector2(Theme.S(100), 0)))
                    ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        Confirm.Modal(RemoveGroupPopupId, () =>
        {
            ImGui.TextUnformatted($"Take the group {_removeGroup?.Name} and its {_removeGroup?.Options.Count} option(s) out of the mod?");
            ImGui.TextColored(Theme.Muted, "Whatever was put into its options goes back with what it belongs to (items to the default files).");
            if (_removeGroup != null && _session.VariantSlotsOf(_removeGroup).Count > 0)
                ImGui.TextColored(Theme.Muted, "Slots of variants that filled it get a group of their own again.");
        }, "Remove", () =>
        {
            if (_removeGroup != null)
                _session.RemoveGroup(_removeGroup);
            _removeGroup = null;
        }, danger: true);

        Confirm.Modal(RemoveOptionPopupId, () =>
        {
            ImGui.TextUnformatted($"Take {_removeOption?.Option.Name} out of {_removeOption?.Group.Name}?");
            ImGui.TextColored(Theme.Muted, "Whatever was put into it goes back with what it belongs to (items to the default files).");
            if (_removeOption is { Option.Key.Length: > 0 } r)
                ImGui.TextColored(Theme.Muted, $"Variant files named {r.Option.Key} are left out of the mod (the group's inspector can bring them back).");
        }, "Remove", () =>
        {
            if (_removeOption is { } r)
                _session.RemoveOption(r.Group, r.Option);
            _removeOption = null;
        }, danger: true);
    }
}
