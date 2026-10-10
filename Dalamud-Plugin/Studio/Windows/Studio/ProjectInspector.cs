using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Resources;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Studio;

/// <summary>
/// The inspector for the mod itself: its name, author, version, website, description and tags as
/// meta.json has them (edited in place and written shortly after), what is in it, and its folder.
/// </summary>
internal sealed class ProjectInspector
{
    private readonly StudioHost _host;
    private readonly ModEditor _editor;
    private readonly ModSession _session;
    private string _newTag = string.Empty;

    // What the summary says about meta.json, worked out again only when it changes (a mod can have
    // thousands of options).
    private PenumbraModMeta? _summaryOf;
    private int _summaryRevision = -1;
    private int _carriers;
    private List<string> _manipulations = new();

    public ProjectInspector(ModEditor editor, ModSession session)
    {
        _host  = editor.Host;
        _editor = editor;
        _session = session;
    }

    public void Draw()
    {
        var project = _session.Project;
        if (project == null)
            return;
        var meta = project.Meta;

        Gui.SectionHeader("Mod", "Written into the mod's meta.json shortly after you stop typing; Penumbra reads it again then.");

        Field("Name", "##ModName", meta.Name, 128, project.FolderName, v => m => m.Name = v, "Name",
            "The mod's name in Penumbra. Its folder keeps its own name.");
        Field("Author", "##ModAuthor", meta.Author, 128, string.Empty, v => m => m.Author = v, "Author");
        Field("Version", "##ModVersion", meta.Version, 32, "1.0", v => m => m.Version = v, "Version");
        Field("Website", "##ModWebsite", meta.Website, 256, "https://", v => m => m.Website = v, "Website");

        Gui.Label("Description");
        string description = meta.Description;
        if (ImGui.InputTextMultiline("##ModDescription", ref description, 8192,
                new Vector2(-1, ImGui.GetTextLineHeight() * 6 + ImGui.GetStyle().FramePadding.Y * 2)))
            _session.EditMeta(m => m.Description = description, "Description");

        DrawTags(meta);

        if (!string.IsNullOrWhiteSpace(meta.Image))
            Gui.LabeledValue("Image", meta.Image, "The picture Penumbra shows for the mod.");

        ImGui.Spacing();
        ImGui.Spacing();
        Gui.SectionHeader("In this mod");
        var graph = _session.Graph;
        if (_summaryOf != meta || _summaryRevision != meta.Revision)
        {
            _summaryOf       = meta;
            _summaryRevision = meta.Revision;
            _carriers        = meta.FileCarriers().Count();
            _manipulations   = meta.ManipulationCounts().GroupBy(m => m.Type).Select(g => $"{g.Sum(x => x.Count)} {g.Key}").ToList();
        }
        Row(FontAwesomeIcon.File, $"{graph.ModFileCount} file(s) across {_carriers} place(s) (default files and options)");
        foreach (var section in graph.Roots.GroupBy(r => r.Section).OrderBy(g => g.Key))
            Row(ResourceStyle.SectionIcon(section.Key), $"{section.Count()} × {ResourceKinds.SectionLabel(section.Key)}");
        if (project.Sidecar.Replacements.Count > 0)
            Row(FontAwesomeIcon.ExchangeAlt, $"{project.Sidecar.Replacements.Count} file(s) replaced or added with files of your own");

        if (_manipulations.Count > 0)
            Row(FontAwesomeIcon.SlidersH, $"Manipulations: {string.Join(", ", _manipulations)}");

        if (meta.Groups.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Spacing();
            Gui.SectionHeader($"Option groups ({meta.Groups.Count})", "Penumbra's option groups; files listed under an option are only active while it is picked.");
            foreach (var group in meta.Groups)
            {
                ImGui.PushID(group.Id);
                bool open = ImGui.TreeNodeEx($"{group.Name}##group", ImGuiTreeNodeFlags.SpanAvailWidth);
                ImGui.SameLine();
                ImGui.TextColored(Theme.Faint, $"{group.Type} · {group.Options.Count} option(s)");
                if (open)
                {
                    var settings = _editor.Settings;
                    foreach (var option in group.FileSets.Take(300))
                    {
                        ImGui.BulletText(option.Name);
                        ImGui.SameLine();
                        ImGui.TextColored(Theme.Faint, $"{graph.FilesIn(option.Ref)} file(s)");
                        if (settings.IsOn(option.Ref) is { } on)
                        {
                            ImGui.SameLine();
                            ImGui.TextColored(on ? Theme.Good : Theme.Faint, on ? "· in use" : "· off");
                        }
                    }
                    if (group.FileSets.Count > 300)
                        ImGui.TextColored(Theme.Faint, $"and {group.FileSets.Count - 300} more");
                    ImGui.TreePop();
                }
                ImGui.PopID();
            }
        }

        ImGui.Spacing();
        ImGui.Spacing();
        Gui.SectionHeader("Folder");
        Gui.LabeledValue("On disk", project.FolderPath);
        if (Gui.IconTextButton(FontAwesomeIcon.FolderOpen, "Open folder"))
            OpenFolder(project.FolderPath);
        ImGui.SameLine();
        using (Gui.Disabled(!_host.PenumbraIpc.IsAvailable))
        {
            if (Gui.IconTextButton(FontAwesomeIcon.SyncAlt, "Reload in Penumbra", "Penumbra reads the mod from disk again."))
            {
                var result = _host.PenumbraIpc.ReloadMod(project.FolderName);
                _session.Notify(result == PenumbraApiEc.Success ? "Penumbra read the mod again." : $"Penumbra said {result}.", result == PenumbraApiEc.Success ? Severity.Info : Severity.Warning);
            }
        }

        ImGui.Spacing();
        Gui.HintWrapped("Changes made here are written into the mod as you make them, and Penumbra reads it again each time. " +
                       "Changes made to it elsewhere — in Penumbra's editor, say — are picked up and kept: XIV Instant Edit only rewrites a file it wrote when its replacement here changes.");

        if (!project.CreatedHere)
        {
            ImGui.Spacing();
            Gui.HintWrapped($"This mod was not made here: the first time XIV Instant Edit writes its meta.json, the original is kept as meta.json{PenumbraModMeta.BackupSuffix}.");
        }
    }

    private void Field(string label, string id, string value, int max, string hint, Func<string, Action<PenumbraModMeta>> edit, string key, string? help = null)
    {
        if (Gui.LabeledInput(label, id, ref value, max, hint, help))
            _session.EditMeta(edit(value), key);
    }

    private void DrawTags(PenumbraModMeta meta)
    {
        Gui.Label("Tags", "Penumbra's mod tags, for filtering its mod list.");
        var tags = meta.Tags;
        string? remove = null;
        float right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        for (int i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            float w = ImGui.CalcTextSize(tag).X + Theme.S(28);
            if (i > 0)
            {
                ImGui.SameLine();
                if (ImGui.GetCursorPosX() + w > right)
                {
                    ImGui.NewLine();
                    ImGui.SetCursorPosX(Theme.LabelWidth);
                }
            }
            ImGui.PushID(i);
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.Accent with { W = 0.18f });
            if (ImGui.SmallButton($"{tag}  ×"))
                remove = tag;
            ImGui.PopStyleColor();
            Gui.Tooltip("Remove this tag");
            ImGui.PopID();
        }
        if (tags.Count > 0)
        {
            ImGui.NewLine();
            ImGui.SetCursorPosX(Theme.LabelWidth);
        }
        ImGui.SetNextItemWidth(Theme.S(160));
        bool enter = ImGui.InputTextWithHint("##newtag", "new tag", ref _newTag, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((enter || ImGui.SmallButton("Add tag")) && _newTag.Trim().Length > 0)
        {
            var added = tags.Append(_newTag.Trim()).ToList();
            _session.EditMeta(m => m.Tags = added, "ModTags");
            _newTag = string.Empty;
        }

        if (remove != null)
        {
            var kept = tags.Where(t => t != remove).ToList();
            _session.EditMeta(m => m.Tags = kept, "ModTags");
        }
    }

    private static void Row(FontAwesomeIcon icon, string text)
    {
        Gui.Icon(icon, Theme.Muted);
        ImGui.SameLine(Theme.S(28));
        ImGui.TextUnformatted(text);
    }

    private void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _session.Notify($"Could not open the folder: {ex.Message}", Severity.Error);
        }
    }
}
