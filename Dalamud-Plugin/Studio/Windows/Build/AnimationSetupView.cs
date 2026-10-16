using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Animation;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Build;

/// <summary>
/// An animation item's set-up: the races its files are written for, then one row per timeline of
/// the emote with a tile for its .pap and one for its .tmb (drop a file or pick one). The
/// inspector half lists where every file goes, race by race, and which races borrow another's.
/// </summary>
internal sealed class AnimationSetupView
{
    private readonly PortSession _session;
    private readonly PortEditor _editor;

    public AnimationSetupView(PortSession session, PortEditor editor)
    {
        _session = session;
        _editor  = editor;
    }

    public void Draw(PortItem item)
    {
        if (item.Animation is not { } animation || item.Subject is not AnimationSubject subject)
            return;

        DrawHeader(item, subject, animation);
        ImGui.Spacing();

        Gui.SectionHeader("Races", "The .pap files are written for these races. The game sends some races to another race's file, so " +
                                   "unticked races sharing that file change too; the inspector lists them.");
        if (DrawRaces("item", animation.Races))
            _session.MarkDirty();
        ImGui.Spacing();

        Gui.SectionHeader("Animations", "Drop a .pap on a row, and a .tmb if the timing, sounds or effects change too.");
        float width = ImGui.GetContentRegionAvail().X;
        for (int i = 0; i < animation.Timelines.Count; i++)
        {
            ImGui.PushID(i);
            DrawTimeline(animation, animation.Timelines[i], width);
            ImGui.PopID();
            ImGui.Spacing();
        }

        var locked = subject.Target.Timelines.Where(t => !t.Editable).ToList();
        if (locked.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.Muted, "Not replaceable here");
            foreach (var timeline in locked)
            {
                Gui.Icon(FontAwesomeIcon.Minus, Theme.Faint);
                ImGui.SameLine();
                ImGui.TextColored(Theme.Faint, $"{timeline.Key}: {timeline.Reason}");
            }
        }
    }

    private void DrawHeader(PortItem item, AnimationSubject subject, AnimationSetup animation)
    {
        var target = new Target(TargetKind.Item, item.Key);
        int files = animation.Timelines.Sum(t => (string.IsNullOrWhiteSpace(t.PapSource) ? 0 : 1) + (string.IsNullOrWhiteSpace(t.TmbSource) ? 0 : 1));
        var badges = new List<Badge>();
        if (!item.HasWork)
            badges.Add(new Badge("not set up", Theme.Faint, "Nothing is set for it yet: a build leaves it out."));
        badges.With(PackPlaceUi.PlaceBadge(_session, _session.PlaceOf(item), PackPlace.Default));

        var worst = PortValidator.WorstUnder(_session.Issues, target);
        uint icon = subject.Target.Icon;
        var result = Tile.Draw(new TileSpec
        {
            Id        = "header",
            Title     = subject.DisplayName,
            Subtitle  = $"Animation · {subject.IdDisplay}",
            Detail    = $"{animation.Timelines.Count} animation(s) · {files} file(s)",
            Icon      = FontAwesomeIcon.Running,
            IconColor = Theme.Accent,
            DrawThumb = icon != 0 ? side => GameIcons.Draw(icon, side) : null,
            Badges    = badges,
            Actions   = new[] { new TileAction(FontAwesomeIcon.Trash, "Remove from the modpack (its set-up is kept)", () => _editor.AskRemoveItem(item), Theme.Bad) },
            Large     = true,
            Selected  = _session.Selection == target,
            Issue     = worst,
            IssueText = worst != null ? string.Join("\n", _session.Issues.Where(i => i.Target == target).Select(i => i.Message)) : null,
        }, new Vector2(ImGui.GetContentRegionAvail().X, Tile.Height(large: true)));
        if (result.Clicked)
            _session.Selection = target;
    }

    /// <summary>"Every race", or a checkbox per race. Empty <paramref name="races"/> means every race. The last ticked race can't be unticked.</summary>
    private static bool DrawRaces(string id, List<string> races)
    {
        bool changed = false;
        bool all = races.Count == 0;
        if (ImGui.Checkbox($"Every race##{id}all", ref all))
        {
            races.Clear();
            if (!all)
                races.AddRange(PortSession.AllRaceCodes);
            changed = true;
        }
        if (all)
            return changed;

        if (ImGui.BeginTable($"##{id}races", 2, ImGuiTableFlags.SizingStretchSame))
        {
            foreach (var race in RaceInfo.AllRaces)
            {
                ImGui.TableNextColumn();
                bool on = races.Contains(race.RaceCode);
                using (Gui.Disabled(on && races.Count == 1))
                {
                    if (ImGui.Checkbox($"{race.DisplayName}##{id}{race.RaceCode}", ref on))
                    {
                        if (on) races.Add(race.RaceCode);
                        else    races.Remove(race.RaceCode);
                        changed = true;
                    }
                }
            }
            ImGui.EndTable();
        }
        return changed;
    }

    private void DrawTimeline(AnimationSetup animation, AnimationTimelineSetup timeline, float width)
    {
        using (Fonts.Title())
            ImGui.TextUnformatted(timeline.Label);
        ImGui.SameLine();
        Gui.Hint(timeline.Key);

        float half = (width - ImGui.GetStyle().ItemSpacing.X) / 2;
        var size = new Vector2(half, Tile.Height(detail: true));
        DrawFile(timeline, tmb: false, size);
        ImGui.SameLine();
        DrawFile(timeline, tmb: true, size);

        bool own = timeline.Races != null;
        if (ImGui.Checkbox("Races of its own##own", ref own))
        {
            timeline.Races = own ? (animation.Races.Count == 0 ? PortSession.AllRaceCodes.ToList() : animation.Races.ToList()) : null;
            _session.MarkDirty();
        }
        Gui.Tooltip("Write this animation for other races than the rest of the item.");
        if (timeline.Races != null && DrawRaces("own", timeline.Races))
            _session.MarkDirty();
    }

    private void DrawFile(AnimationTimelineSetup timeline, bool tmb, Vector2 size)
    {
        var path = tmb ? timeline.TmbSource : timeline.PapSource;
        var kind = tmb ? ".tmb" : ".pap";
        bool set = !string.IsNullOrWhiteSpace(path);
        var problem = set ? Check(path, tmb) : null;

        var actions = new List<TileAction> { new(FontAwesomeIcon.FolderOpen, $"Pick the {kind} file", () => Browse(timeline, tmb)) };
        if (set)
            actions.Add(new TileAction(FontAwesomeIcon.Times, "Clear (the file on disk is not touched)", () => Set(timeline, tmb, string.Empty), Theme.Bad));

        var drop = new FileDropRule(
            p => p.Files.Count == 1 && p.Folders.Count == 0 && Path.GetExtension(p.Files[0]).Equals(kind, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"Drop one {kind} file.",
            p => Set(timeline, tmb, p.Files[0]));

        var result = Tile.Draw(new TileSpec
        {
            Id            = tmb ? "tmb" : "pap",
            Title         = set ? Path.GetFileName(path) : tmb ? "No .tmb (optional)" : "No .pap yet",
            TitleColor    = set ? null : Theme.Faint,
            Subtitle      = problem ?? (tmb ? "Timeline: timing, sounds, effects" : "Motion"),
            SubtitleColor = problem != null ? Theme.Bad : Theme.Muted,
            Detail        = set ? path : $"Drop a {kind} file here",
            Icon          = tmb ? FontAwesomeIcon.Stream : FontAwesomeIcon.Running,
            IconColor     = set ? Theme.Accent : Theme.Faint,
            Actions       = actions,
            Drop          = drop,
            Tooltip       = set ? path : null,
        }, size);
        if (result.Dropped is { } dropped)
            drop.OnDrop(dropped);
    }

    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(2);
    private readonly Dictionary<(string Path, bool Tmb), (DateTime At, string? Problem)> _checks = new();

    /// <summary>The file check, read again from disk at most every couple of seconds, not every frame.</summary>
    private string? Check(string path, bool tmb)
    {
        var now = DateTime.UtcNow;
        if (_checks.TryGetValue((path, tmb), out var known) && now - known.At < CheckEvery)
            return known.Problem;
        var problem = AnimationFileCheck.Problem(path, tmb);
        _checks[(path, tmb)] = (now, problem);
        return problem;
    }

    private void Browse(AnimationTimelineSetup timeline, bool tmb)
    {
        Gui.Dialogs.OpenFileDialog(tmb ? "Timeline file" : "Animation file", tmb ? "Timeline files{.tmb}" : "Animation files{.pap}", (ok, path) =>
        {
            if (ok) Set(timeline, tmb, path);
        });
    }

    private void Set(AnimationTimelineSetup timeline, bool tmb, string path)
    {
        if (tmb) timeline.TmbSource = path;
        else     timeline.PapSource = path;
        _session.MarkDirty();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Where the item goes, and every file it writes, with the races each path serves.</summary>
    public void DrawInspector(PortItem item)
    {
        if (item.Animation is not { } animation)
            return;
        Gui.SectionHeader(item.Subject.DisplayName);
        if (item.Subject is AnimationSubject { Target.Command.Length: > 0 } subject)
            Gui.LabeledValue("Command", subject.Target.Command);
        PackPlaceUi.Row(_session, "##itemplace", item.Meta.Option ?? PackPlace.Default, null, p => _session.SetPlace(item, p ?? PackPlace.Default),
            "Where the item goes in the mod. What is in an option is only used while players have it picked.");

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
        if (Gui.IconTextButton(FontAwesomeIcon.Trash, "Remove from modpack", "Its set-up is kept: adding it again from the browser brings it back."))
            _editor.AskRemoveItem(item);
        ImGui.PopStyleColor();

        ImGui.Spacing();
        Gui.SectionHeader("Written", "Each file at its game path. The game's PAP load table sends some races to another race's copy, so those share it.");
        var plans = _session.AnimationPlan(animation);
        if (plans.Count == 0)
        {
            Gui.HintWrapped("Nothing yet: drop a .pap on an animation.");
            return;
        }
        foreach (var plan in plans)
        {
            ImGui.TextUnformatted(plan.GamePath);
            Gui.Tooltip(plan.Source);
            var owner = OwnerOf(plan.GamePath);
            var borrowers = plan.Races.Where(r => owner != null && r != owner).ToList();
            ImGui.TextColored(Theme.Muted, plan.GamePath.EndsWith(".tmb", StringComparison.Ordinal)
                ? $"  every race · {Path.GetFileName(plan.Source)}"
                : $"  {string.Join(", ", plan.Races.Select(r => $"c{r}"))} · {Path.GetFileName(plan.Source)}");
            if (borrowers.Count > 0)
                ImGui.TextColored(Theme.Faint, $"  {string.Join(", ", borrowers.Select(r => $"c{r}"))} play c{owner}'s file");
            if (plan.AlsoServes.Count > 0)
                ImGui.TextColored(Theme.Warn, $"  also changes {string.Join(", ", plan.AlsoServes.Select(r => $"c{r}"))}: not ticked, but the game loads this file for them too");
            ImGui.Spacing();
        }
    }

    /// <summary>The race whose folder a body PAP path is in ("0101" for chara/human/c0101/...), or null.</summary>
    private static string? OwnerOf(string gamePath)
    {
        var parts = gamePath.Split('/');
        return parts.Length > 2 && parts[0] == "chara" && parts[1] == "human" && parts[2].Length == 5 ? parts[2][1..] : null;
    }
}
