using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Services.TextureCompression;
using InstantEdit.Studio.Models.Resources;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Backups;
using InstantEdit.Studio.Services.CleanUp;
using InstantEdit.Studio.Windows.UI;
using Penumbra.Api.Enums;

namespace InstantEdit.Studio.Windows.Studio;

/// <summary>The Check view's Clean up tab: the mod's textures, resized and compressed.</summary>
internal sealed partial class CheckView
{
    private static readonly int[] SizeCaps = { 0, 4096, 2048, 1024, 512, 256 };
    private static readonly string[] SizeCapLabels = { "Keep size", "4096", "2048", "1024", "512", "256" };
    private static readonly TextureFormatChoice[] Formats = Enum.GetValues<TextureFormatChoice>();
    private static readonly string[] FormatLabels = { "Auto", "Keep format", "BC1", "BC3", "BC4", "BC5", "BC7" };

    private Task<IReadOnlyList<TextureCandidate>>? _textureScan;
    private IReadOnlyList<TextureCandidate>? _textures;
    private readonly Dictionary<string, (bool Ticked, int Format, int Cap)> _textureChoices = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The texture picked in Clean up, for the inspector.</summary>
    internal TextureCandidate? SelectedTexture { get; private set; }

    partial void DrawMoreTabs()
    {
        if (ImGui.BeginTabItem("Clean up###cleanup"))
        {
            DrawTextures();
            ImGui.Spacing();
            DrawLocations();
            ImGui.Spacing();
            DrawDuplicates();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Backups###backups"))
        {
            DrawBackups();
            ImGui.EndTabItem();
        }
    }

    // ── Duplicate files ──────────────────────────────────────────────────────

    private Task<IReadOnlyList<DuplicateSet>>? _duplicateScan;
    private IReadOnlyList<DuplicateSet>? _duplicates;

    private void DrawDuplicates()
    {
        if (_duplicateScan is { IsCompleted: true } scan)
        {
            _duplicateScan = null;
            _duplicates = scan.IsCompletedSuccessfully ? scan.Result : Array.Empty<DuplicateSet>();
        }
        if (!ImGui.CollapsingHeader("Duplicate files###duplicates"))
            return;
        Gui.HintWrapped("Files the mod carries more than once. Each copy's redirections are pointed at one of them, and the other copies are deleted after a backup.");
        using (Gui.Disabled(_duplicateScan != null || Check.Busy || _session.Project == null))
        {
            if (Gui.IconTextButton(FontAwesomeIcon.Search, _duplicates == null ? "Find duplicates" : "Look again", "Compare the mod's files by size, hash and bytes."))
                StartDuplicateScan();
        }
        if (_duplicateScan != null)
        {
            ImGui.SameLine();
            Gui.Hint("Comparing files");
        }
        if (_duplicates is not { } sets)
            return;
        if (sets.Count == 0)
        {
            Gui.Hint("No file is in the mod twice.");
            return;
        }
        var bytes = sets.Sum(s => s.BytesFreed);
        ImGui.TextUnformatted($"{sets.Sum(s => s.Others.Count)} duplicate file{(sets.Sum(s => s.Others.Count) == 1 ? string.Empty : "s")}, {Size(bytes)}.");
        foreach (var set in sets.Take(8))
        {
            ImGui.TextColored(Theme.Muted, set.Keeper);
            Gui.Tooltip("Kept. Also used for:\n" + string.Join("\n", set.Others));
        }
        if (sets.Count > 8)
            Gui.Hint($"and {sets.Count - 8} more");

        var why = Check.Busy ? "Wait until the change under way is done."
            : _editor.Sync.Busy ? Services.Check.RepairApplier.BusyReason
            : !_host.PenumbraIpc.IsAvailable ? "Penumbra is not available."
            : null;
        using (Gui.Disabled(why != null))
        {
            if (Gui.PrimaryButton(FontAwesomeIcon.Clone, "Remove the duplicates", Theme.S(200), why ?? "Point every redirection at one copy, then delete the others."))
                StartDuplicates(sets);
        }
        if (why != null)
            Gui.Tooltip(why);
    }

    private void StartDuplicateScan()
    {
        if (_session.Project is not { } project)
            return;
        var meta = project.Meta.Clone();
        var folder = project.FolderPath;
        var ledger = project.Sidecar.Ledger.Files.Select(f => Services.Project.PathNormalizer.RelKey(f.RelPath)).ToHashSet(StringComparer.Ordinal);
        _duplicateScan = Task.Run(() => DuplicateFiles.Scan(meta, folder, ledger, System.Threading.CancellationToken.None));
    }

    private void StartDuplicates(IReadOnlyList<DuplicateSet> sets)
    {
        if (_session.Project is not { } project)
            return;
        _editor.Flush();
        var host = _host;
        _duplicates = null;
        Check.StartOperation("Removing duplicates", async token =>
        {
            if (BackupFolder.TryResolve(() => host.CacheRoot, out var problem) is not { } cacheRoot)
                return (false, problem);
            var result = await DuplicateFiles.ApplyAsync(project, sets, host.Backups, cacheRoot, host.Reloader, host.Configuration, token, OnFramework)
                .ConfigureAwait(false);
            if (result.Problems.Count > 0)
                return (false, result.Problems[0]);
            var message = $"Removed {result.Changed} duplicate file{(result.Changed == 1 ? string.Empty : "s")}, {Size(result.BytesSaved)} freed";
            if (result.Pending > 0)
                message += $"; {result.Pending} locked file{(result.Pending == 1 ? " is" : "s are")} removed later";
            return (true, message + ".");
        }, checkAfter: true);
    }

    // ── Backups ──────────────────────────────────────────────────────────────

    private void DrawBackups()
    {
        if (_session.Project is not { } project)
            return;
        Gui.HintWrapped("What fixes and clean-up changed in this mod, for a week. Restoring puts back the files that still hold what the change left; files changed since are left alone.");
        if (_host.Backups.LoadError is { Length: > 0 } error)
            ImGui.TextColored(Theme.Warn, error);
        var entries = _host.Backups.For(project.FolderPath);
        if (entries.Count == 0)
        {
            Gui.Hint("Nothing was changed here yet.");
            return;
        }
        foreach (var entry in entries)
        {
            ImGui.PushID(entry.Id.ToString());
            using (Gui.Disabled(Check.Busy || _editor.Sync.Busy))
            {
                if (Gui.IconButton(FontAwesomeIcon.Undo, "restore", $"Restore what \"{entry.Label}\" changed"))
                    StartRestore(entry.Id);
            }
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(entry.Label);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Faint, $"{entry.At.ToLocalTime():ddd HH:mm}, {entry.Files.Count} file{(entry.Files.Count == 1 ? string.Empty : "s")}"
                                         + (entry.RestoredAt is { } restored ? $", restored {restored.ToLocalTime():HH:mm}" : string.Empty));
            Gui.Tooltip(string.Join("\n", entry.Files.Take(20).Select(f => f.RelPath)) + (entry.Files.Count > 20 ? $"\nand {entry.Files.Count - 20} more" : string.Empty));
            ImGui.PopID();
        }
    }

    private void StartRestore(Guid entryId)
    {
        if (_session.Project is not { } project)
            return;
        _editor.Flush();
        var host = _host;
        var folder = project.FolderPath;
        Check.StartOperation("Restoring", _ =>
        {
            if (BackupFolder.TryResolve(() => host.CacheRoot, out var problem) is not { } cacheRoot)
                return Task.FromResult((false, problem));
            var result = host.Backups.Restore(entryId, folder, cacheRoot, host.Reloader);
            var message = $"Restored {result.Restored} file{(result.Restored == 1 ? string.Empty : "s")}"
                        + (result.Skipped.Count > 0 ? $"; {result.Skipped.Count} changed since and left alone:\n{string.Join("\n", result.Skipped)}" : ".");
            return Task.FromResult((result.Skipped.Count == 0, message));
        }, checkAfter: true);
    }

    // ── File locations ───────────────────────────────────────────────────────

    private Task<LocationPlan>? _locationScan;
    private LocationPlan? _locations;

    private void DrawLocations()
    {
        if (_locationScan is { IsCompleted: true } scan)
        {
            _locationScan = null;
            _locations = scan.IsCompletedSuccessfully ? scan.Result : new LocationPlan(Array.Empty<LocationMove>(), Array.Empty<string>());
        }
        if (!ImGui.CollapsingHeader("File locations###locations"))
            return;
        Gui.HintWrapped("Move the mod's files to where Penumbra keeps them: the default files at their game path, an option's under its group and option. "
                      + "Images and files Instant Edit wrote stay where they are. Old files are backed up before they go.");
        using (Gui.Disabled(_locationScan != null || Check.Busy || _session.Project == null))
        {
            if (Gui.IconTextButton(FontAwesomeIcon.Search, _locations == null ? "See what would move" : "Look again", "List the moves without changing anything."))
                StartLocationScan();
        }
        if (_locations is not { } plan)
            return;
        if (plan.Moves.Count == 0 && plan.Obsolete.Count == 0)
        {
            Gui.Hint("Everything is where Penumbra keeps it.");
            return;
        }
        ImGui.TextUnformatted($"{plan.Moves.Count} file{(plan.Moves.Count == 1 ? string.Empty : "s")} move, {plan.Obsolete.Count} unused file{(plan.Obsolete.Count == 1 ? string.Empty : "s")} go.");
        foreach (var move in plan.Moves.Take(8))
            ImGui.TextColored(Theme.Faint, $"{move.FromRel}  to  {move.ToRel}");
        if (plan.Moves.Count > 8)
            Gui.Hint($"and {plan.Moves.Count - 8} more");

        var why = Check.Busy ? "Wait until the change under way is done."
            : _editor.Sync.Busy ? Services.Check.RepairApplier.BusyReason
            : !_host.PenumbraIpc.IsAvailable ? "Penumbra is not available."
            : null;
        using (Gui.Disabled(why != null))
        {
            if (Gui.PrimaryButton(FontAwesomeIcon.FolderOpen, "Move the files", Theme.S(200), why ?? "Copy, change the mappings, then delete the old files."))
                StartLocations(plan);
        }
        if (why != null)
            Gui.Tooltip(why);
    }

    private void StartLocationScan()
    {
        if (_session.Project is not { } project)
            return;
        var meta = project.Meta.Clone();
        var folder = project.FolderPath;
        var ledger = project.Sidecar.Ledger.Files.Select(f => Services.Project.PathNormalizer.RelKey(f.RelPath)).ToHashSet(StringComparer.Ordinal);
        _locationScan = Task.Run(() => FileLocations.Plan(meta, folder, ledger));
    }

    private void StartLocations(LocationPlan plan)
    {
        if (_session.Project is not { } project)
            return;
        _editor.Flush();
        var host = _host;
        _locations = null;
        Check.StartOperation("Moving files", async token =>
        {
            if (BackupFolder.TryResolve(() => host.CacheRoot, out var problem) is not { } cacheRoot)
                return (false, problem);
            var result = await FileLocations.ApplyAsync(project, plan, host.Backups, cacheRoot, host.Reloader, host.Configuration, token, OnFramework)
                .ConfigureAwait(false);
            if (result.Problems.Count > 0)
                return (false, result.Problems[0]);
            var message = $"Moved {result.Changed} file{(result.Changed == 1 ? string.Empty : "s")}";
            if (result.Pending > 0)
                message += $"; {result.Pending} locked file{(result.Pending == 1 ? " is" : "s are")} removed later";
            return (true, message + ".");
        }, checkAfter: true);
    }

    private void DrawTextures()
    {
        if (_textureScan is { IsCompleted: true } scan)
        {
            _textureScan = null;
            _textures = scan.IsCompletedSuccessfully ? scan.Result : Array.Empty<TextureCandidate>();
            _textureChoices.Clear();
            foreach (var candidate in _textures)
                _textureChoices[candidate.RelPath] = (candidate.Preselected, 0, 0);
        }

        if (!ImGui.CollapsingHeader("Textures###textures", ImGuiTreeNodeFlags.DefaultOpen))
            return;
        Gui.HintWrapped("Make textures smaller and compress them. Every result is checked before it replaces a file, and every original is backed up.");
        using (Gui.Disabled(_textureScan != null || Check.Busy || ReferenceEquals(_session.Graph, ResourceGraph.Empty)))
        {
            if (Gui.IconTextButton(FontAwesomeIcon.Search, _textures == null ? "Find the textures" : "Look again", "List the mod's textures and how its materials use them."))
                StartTextureScan();
        }
        if (_textureScan != null)
        {
            ImGui.SameLine();
            Gui.Hint("Reading the textures");
        }
        if (_textures is not { } textures)
            return;
        if (textures.Count == 0)
        {
            Gui.Hint("The mod has no textures of its own.");
            return;
        }

        if (ImGui.BeginTable("##textures", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY,
                new System.Numerics.Vector2(-1, Math.Min(ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() * 2, Theme.S(26) * (textures.Count + 1) + Theme.S(8)))))
        {
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, Theme.S(24));
            ImGui.TableSetupColumn("Texture");
            ImGui.TableSetupColumn("Now", ImGuiTableColumnFlags.WidthFixed, Theme.S(110));
            ImGui.TableSetupColumn("Used as", ImGuiTableColumnFlags.WidthFixed, Theme.S(90));
            ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, Theme.S(100));
            ImGui.TableSetupColumn("Format", ImGuiTableColumnFlags.WidthFixed, Theme.S(110));
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            foreach (var candidate in textures)
            {
                ImGui.PushID(candidate.RelPath);
                var choice = _textureChoices.GetValueOrDefault(candidate.RelPath);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                using (Gui.Disabled(!candidate.Selectable))
                {
                    var ticked = choice.Ticked;
                    if (ImGui.Checkbox("##tick", ref ticked))
                        _textureChoices[candidate.RelPath] = choice with { Ticked = ticked };
                }
                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{Path.GetFileName(candidate.RelPath)}##sel", ReferenceEquals(SelectedTexture, candidate)))
                {
                    SelectedTexture = candidate;
                    SelectedFinding = null;
                }
                Gui.Tooltip(candidate.Note is { } note ? $"{candidate.RelPath}\n{note}" : candidate.RelPath);
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.Muted, $"{candidate.Info.Width}×{candidate.Info.Height} {TextureCost.FormatName(candidate.Info.Format)}");
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.Faint, string.Join(", ", candidate.Uses.Select(u => u.Role.ToString()).Distinct()));
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                var cap = choice.Cap;
                if (ImGui.Combo("##cap", ref cap, SizeCapLabels, SizeCapLabels.Length))
                    _textureChoices[candidate.RelPath] = (_textureChoices[candidate.RelPath].Ticked, choice.Format, cap);
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                var format = choice.Format;
                if (ImGui.Combo("##format", ref format, FormatLabels, FormatLabels.Length))
                    _textureChoices[candidate.RelPath] = (_textureChoices[candidate.RelPath].Ticked, format, _textureChoices[candidate.RelPath].Cap);
                ImGui.PopID();
            }
            ImGui.EndTable();
        }

        var jobs = textures.Where(c => c.Selectable && _textureChoices.GetValueOrDefault(c.RelPath).Ticked)
            .Select(c =>
            {
                var choice = _textureChoices[c.RelPath];
                var cap = SizeCaps[choice.Cap];
                return new TextureJob(c, Formats[choice.Format], cap == 0 ? int.MaxValue : cap);
            })
            .ToList();
        var why = jobs.Count == 0 ? "Tick the textures to change."
            : Check.Busy ? "Wait until the change under way is done."
            : _editor.Sync.Busy ? Services.Check.RepairApplier.BusyReason
            : !_host.PenumbraIpc.CanConvertTextures ? "Penumbra is not available to encode textures."
            : null;
        using (Gui.Disabled(why != null))
        {
            if (Gui.PrimaryButton(FontAwesomeIcon.Compress, jobs.Count == 1 ? "Change 1 texture" : $"Change {jobs.Count} textures", Theme.S(200),
                    why ?? "Back up each texture, encode it, check it, and replace it only when it passes."))
                StartTextureCleanUp(jobs);
        }
        if (why != null)
            Gui.Tooltip(why);
    }

    private void StartTextureScan()
    {
        var graph = _session.Graph;
        var game = _host.GameData.VanillaFiles;
        _textureScan = Task.Run(() => TextureCleanUp.Scan(graph, node => node.Origin == OriginKind.ModFile
            ? File.Exists(node.DiskPath) ? File.ReadAllBytes(node.DiskPath) : null
            : game.TryReadBytes(node.GamePath)));
        SelectedTexture = null;
    }

    private void StartTextureCleanUp(IReadOnlyList<TextureJob> jobs)
    {
        if (_session.Project is not { } project)
            return;
        _editor.Flush();
        var host = _host;
        _textures = null;
        SelectedTexture = null;
        var progress = Check.OperationProgress;
        Check.StartOperation("Changing textures", async token =>
        {
            if (BackupFolder.TryResolve(() => host.CacheRoot, out var problem) is not { } cacheRoot)
                return (false, problem);
            var result = await TextureCleanUp.ApplyAsync(project, jobs, PenumbraConvert(host.PenumbraIpc), host.Backups, cacheRoot, host.Reloader,
                host.Configuration, progress, token).ConfigureAwait(false);
            var message = $"Changed {result.Changed} texture{(result.Changed == 1 ? string.Empty : "s")}, {Size(result.BytesSaved)} saved"
                        + (result.Problems.Count > 0 ? $"; {result.Problems.Count} left as they were (hover for why)." : ".");
            return (result.Problems.Count == 0 || result.Changed > 0, result.Problems.Count > 0 ? $"{message}\n{string.Join("\n", result.Problems)}" : message);
        }, checkAfter: false);
    }

    /// <summary>Penumbra's texture conversion, asked on the framework thread; the conversion itself runs where Penumbra puts it.</summary>
    private static TexConvert PenumbraConvert(PenumbraIpcService ipc) => async (input, output, type, mipMaps) =>
    {
        var work = StudioServices.Framework.RunOnTick(() =>
            ipc.ConvertTextureFile(input, output, (int)type, mipMaps) ?? Task.FromException(new IOException("Penumbra could not convert the texture.")));
        if (await Task.WhenAny(work, Task.Delay(TimeSpan.FromMinutes(5))).ConfigureAwait(false) != work)
            throw new TimeoutException("Penumbra took too long to convert the texture.");
        await work.ConfigureAwait(false);
    };

    private static string Size(long bytes)
        => bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.#} MiB" : bytes >= 1 << 10 ? $"{bytes / 1024.0:0.#} KiB" : $"{Math.Max(0, bytes)} B";
}
