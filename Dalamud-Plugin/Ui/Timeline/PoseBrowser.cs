using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Pose;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The pose browser's content, drawn for <see cref="PoseBrowserWindow"/>: your pose folders as tiles with their
/// pictures, browsed like Brio's library. A click on a pose keys it in the Timeline window's selected pose layer
/// at the playhead, as <see cref="PoseKeyOptions"/> says, and the next click there replaces it within the same
/// undo step (<see cref="PoseTrial"/>). Folders and poses are read on tasks, so the draw path never reads files.
/// </summary>
internal sealed class PoseBrowser(TimelineSession session, MotionDecode decode, BonePosing posing, PoseKeyOptions options, PoseThumbnails thumbnails)
{
    private const int SearchLimit = 500;

    /// <summary>Where a pick keys: the selected pose layer at the playhead.</summary>
    private readonly record struct Target(TimelineEditor Editor, AnimationTimelineSource Loaded, MotionLayer Layer, SkeletonDescription Skeleton, int Frame);

    private Configuration? config;
    private Action? save;

    // The folder shown; null for the library's own folders.
    private string? folder;
    private string filter = string.Empty;
    private bool resetScroll;
    // What the tiles show or are being read for: the folder, the filter and the library's version.
    private (string? Folder, string Filter, int Roots)? listed;
    private int rootsVersion;
    private Task<List<PoseLibraryEntry>>? listing;
    private CancellationTokenSource? listCancel;
    private List<PoseLibraryEntry> entries = [];
    private string? listError;
    // A pose being moved to the Recycle Bin.
    private (PoseLibraryEntry Entry, Task Moving)? deleting;

    // The pose clicked last, while its file is read or the motion it keys over is.
    private (string Path, string Name, Task<PoseFile> Reading, CancellationTokenSource Cancel)? picking;
    // The pose keyed last, as it was made local: keyed again when a choice changes while it can still be replaced.
    private (string Path, string Name, PoseFile File, PoseImport Import)? picked;
    // Tile names broken into lines, by name and width.
    private readonly Dictionary<(string Name, float Width), List<string>> nameLines = new();
    private readonly PoseTrial trial = new();
    private string? note;
    private bool noteWarns;
    private float footerHeight;
    // Save pose here: the name typed, the file of that name waiting for Replace it?, and the pose read at the click,
    // then written on a task.
    private string saveName = string.Empty;
    private bool focusSave;
    private string? confirmReplace;
    private (string Path, string Name, AnimationTimelineSource Loaded, TimelineDocument Doc, int Frame)? saveWaiting;
    private (string Name, Task Writing)? saveWriting;

    /// <summary>The settings the folders and the last folder shown are kept in.</summary>
    public void Attach(Configuration configuration, Action saveConfiguration)
    {
        config = configuration;
        save = saveConfiguration;
        var last = configuration.PoseBrowserFolder;
        folder = last.Length > 0 && RootOf(last) != null && Directory.Exists(last) ? last : null;
    }

    /// <summary>Stops reading and lets go of the pictures, when the window closes.</summary>
    public void Closed()
    {
        listCancel?.Cancel();
        picking?.Cancel.Cancel();
        picking = null;
        listing = null;
        listed = null;
        thumbnails.Clear();
    }

    /// <summary>Forgets the last pick, so the next one keys anew: the timeline's clip went or changed.</summary>
    public void Forget()
    {
        trial.Forget();
        picked = null;
        note = null;
        // A save still waiting for the clip's motion goes with it; one being written finishes.
        saveWaiting = null;
    }

    public void Draw()
    {
        if (config == null)
            return;
        PollList();
        PollDelete();
        PollSave();
        var target = FindTarget(out var blocked);
        PollPick(target, blocked);
        DrawBar();
        var height = Math.Max(Theme.Scaled(120), ImGui.GetContentRegionAvail().Y - footerHeight - ImGui.GetStyle().ItemSpacing.Y);
        using (var grid = ImRaii.Child("##pose-browser-grid", new Vector2(0, height), false))
        {
            if (grid.Success)
                DrawGrid(target, blocked);
        }
        var start = ImGui.GetCursorPosY();
        DrawFooter(target, blocked);
        footerHeight = Math.Max(1, ImGui.GetCursorPosY() - start);
    }

    /// <summary>The selected pose layer at the playhead, and why a pose can't be keyed there now; null without one.</summary>
    private Target? FindTarget(out string? blocked)
    {
        blocked = null;
        if (session.Source is not { Document: not null } loaded || session.Editor is not { } current)
            blocked = "Open an animation in the Timeline window.";
        else if (loaded.Standalone || current.Document.Face != null)
            blocked = "A face takes no body pose.";
        else if (session.EditingSkeleton(loaded) is not { } skeleton)
            blocked = "Choose the skeleton this animation was made for under Source first.";
        else if (current.SelectedLayerValue is not { Kind: MotionLayerKind.Pose } layer)
            blocked = "Select a pose layer in the timeline.";
        else
        {
            var frame = current.Playhead;
            blocked = session.ReadOnly ? "Wait for the save to finish."
                : frame < layer.Start || frame > layer.End ? "Move the playhead into the layer."
                : null;
            return new Target(current, loaded, layer, skeleton, frame);
        }
        return null;
    }

    // ── Folders ──────────────────────────────────────────────────────────────

    /// <summary>The library folder <paramref name="path"/> is in, or null.</summary>
    private string? RootOf(string path) =>
        config?.PoseBrowserFolders.Where(root => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
                                                 path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .MaxBy(root => root.Length);

    private void Open(string? next)
    {
        folder = next;
        filter = string.Empty;
        resetScroll = true;
        config!.PoseBrowserFolder = next ?? string.Empty;
        save!();
    }

    private void AddFolder()
    {
        var start = folder ?? config!.PoseBrowserFolders.LastOrDefault();
        session.FileDialog.OpenFolderDialog("Choose a pose folder", (success, path) =>
        {
            if (!success || string.IsNullOrEmpty(path))
                return;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!config!.PoseBrowserFolders.Contains(full, StringComparer.OrdinalIgnoreCase))
                config.PoseBrowserFolders.Add(full);
            rootsVersion++;
            Open(full);
        }, start is { } at && Directory.Exists(at) ? at : null, false);
    }

    private void RemoveFolder(string root)
    {
        config!.PoseBrowserFolders.RemoveAll(path => string.Equals(path, root, StringComparison.OrdinalIgnoreCase));
        rootsVersion++;
        if (folder != null && RootOf(folder) == null)
            Open(null);
        else
            save!();
    }

    /// <summary>Takes a pose moved to the Recycle Bin off the tiles, or says why it is still there.</summary>
    private void PollDelete()
    {
        if (deleting is not { Moving.IsCompleted: true } done)
            return;
        deleting = null;
        if (done.Moving.IsCompletedSuccessfully)
        {
            entries = [.. entries.Where(entry => !string.Equals(entry.Path, done.Entry.Path, StringComparison.OrdinalIgnoreCase))];
            Note($"{done.Entry.Name} is in the Recycle Bin.", false);
        }
        else
            Note($"{done.Entry.Name}: {TimelineSession.ErrorText(done.Moving.Exception)}", true);
    }

    /// <summary>Reads the folder shown, or what the filter finds in and below it, when either changed.</summary>
    private void PollList()
    {
        var key = (folder, filter.Trim(), rootsVersion);
        if (!listed.Equals(key))
        {
            listed = key;
            listCancel?.Cancel();
            listCancel?.Dispose();
            listCancel = new CancellationTokenSource();
            var token = listCancel.Token;
            var (at, text, _) = key;
            var roots = config!.PoseBrowserFolders.ToArray();
            listing = Task.Run(() => at == null
                ? text.Length == 0
                    ? PoseLibrary.Roots(roots, token)
                    : roots.Where(Directory.Exists).SelectMany(root => PoseLibrary.Search(root, text, SearchLimit, token)).Take(SearchLimit).ToList()
                : text.Length == 0 ? PoseLibrary.List(at, token) : PoseLibrary.Search(at, text, SearchLimit, token), token);
        }
        if (listing is not { IsCompleted: true } task)
            return;
        listing = null;
        if (task.IsCanceled)
            return;
        if (task.IsFaulted)
        {
            entries = [];
            listError = task.Exception?.InnerException is DirectoryNotFoundException ? "The folder is gone." : TimelineSession.ErrorText(task.Exception);
            return;
        }
        entries = PoseLibrary.Sorted(task.Result, config!.PoseBrowserNewestFirst);
        listError = null;
    }

    // ── Picking ──────────────────────────────────────────────────────────────

    private void Pick(PoseLibraryEntry entry)
    {
        picking?.Cancel.Cancel();
        var cancel = new CancellationTokenSource();
        var path = entry.Path;
        picking = (path, entry.Name, Task.Run(() =>
        {
            if (new FileInfo(path).Length > PoseFiles.MaximumBytes)
                throw new InvalidDataException("The file is too large for a pose.");
            return PoseFiles.Parse(File.ReadAllBytes(path));
        }, cancel.Token), cancel);
        note = null;
        // Another pose: whether positions are keyed follows it again until chosen, as in the pose panel.
        options.NewPose();
    }

    /// <summary>Keys the pose clicked once its file is read and the motion it keys over is.</summary>
    private void PollPick(Target? target, string? blocked)
    {
        if (picking is not { Reading: { IsCompleted: true } task } pick)
            return;
        if (task.IsCanceled)
        {
            picking = null;
            return;
        }
        if (task.IsFaulted)
        {
            picking = null;
            Note($"{pick.Name}: {TimelineSession.ErrorText(task.Exception)}", true);
            return;
        }
        if (Key(target, blocked, pick.Path, pick.Name, task.Result))
            picking = null;
    }

    /// <summary>
    /// Keys <paramref name="file"/> at <paramref name="target"/>, in place of the last pick when it can still be
    /// replaced. False while the motion it keys over is read; true once keyed or refused, with a note saying which.
    /// </summary>
    private bool Key(Target? target, string? blocked, string path, string name, PoseFile file)
    {
        if (target is not { } at || blocked != null)
        {
            Note(blocked ?? "Select a pose layer in the timeline.", true);
            return true;
        }
        var current = at.Editor;
        if (posing.FramesAt(at.Loaded, current.Document, at.Layer, at.Frame)?.Before is not { } under)
        {
            if (!decode.FailedFor(at.Loaded))
                return false;
            Note("The motion could not be read: " + decode.Error, true);
            return true;
        }
        PoseImport import;
        try { import = PoseImports.FromModel(at.Skeleton, file.Bones, under); }
        catch (InvalidDataException e)
        {
            Note($"{name}: {e.Message}", true);
            return true;
        }
        var choice = options.For(import, at.Skeleton, current.Document);
        if (choice.Bones.Count == 0)
        {
            Note(choice.SelectedOnly ? $"{name} has none of the selected bones." : $"{name} has none of this skeleton's bones.", true);
            return true;
        }
        var before = trial.Base(current.Document, at.Layer.Key, at.Frame, out var replaces);
        var next = current.WithReach(before, options.Key(before, at.Layer.Key, at.Frame, import, choice, at.Skeleton, under));
        if (!(replaces ? current.Amend(next) : current.Commit(next)))
        {
            Note("The timeline is busy. Try again.", true);
            return true;
        }
        trial.Keyed(before, current.Document, at.Layer.Key, at.Frame, path);
        picked = (path, name, file, import);
        current.SelectLayer(at.Layer.Key, at.Frame);
        Note($"{name}: keyed {(choice.Bones.Count == 1 ? "1 bone" : $"{choice.Bones.Count} bones")} at {TimelineWindowViews.FrameText(at.Frame)}.", false);
        return true;
    }

    private void Note(string text, bool warns)
    {
        note = text;
        noteWarns = warns;
    }

    // ── Saving ───────────────────────────────────────────────────────────────

    /// <summary>Puts the cursor in Save pose here's name, the next time a folder shows (the Bones window's Save pose).</summary>
    public void FocusSave() => focusSave = true;

    /// <summary>Why the pose at the playhead can't be saved now; null when it can.</summary>
    private string? SaveBlocked()
    {
        if (session.Source is not { Document: not null } loaded || session.Editor is not { } current)
            return "Open an animation in the Timeline window.";
        if (loaded.Standalone || current.Document.Face != null)
            return "A face can't be saved as a pose.";
        return session.EditingSkeleton(loaded) == null ? "Choose the skeleton this animation was made for under Source first." : null;
    }

    /// <summary>Saves the pose at the playhead in the folder shown, under the name typed; asks first when the file is there.</summary>
    private void StartSave(bool replace)
    {
        var name = saveName.Trim();
        if (PoseFiles.NameProblem(name) is { } problem)
        {
            Note(problem, true);
            return;
        }
        var path = PoseFiles.PathFor(folder!, name);
        if (!replace && File.Exists(path))
        {
            confirmReplace = path;
            return;
        }
        confirmReplace = null;
        note = null;
        saveWaiting = (path, name, session.Source!, session.Editor!.Document, session.Editor.Playhead);
    }

    /// <summary>Writes the pose once the motion it is made of is read, then reads the folder again so its tile shows.</summary>
    private void PollSave()
    {
        // Another clip opened before the motion was read: waiting would read the old clip's motion against the new one's.
        if (saveWaiting is { } gone && (session.Source is not { } open || TimelineSession.ClipKey(open) != TimelineSession.ClipKey(gone.Loaded)))
        {
            saveWaiting = null;
            Note($"{gone.Name} wasn't saved: another animation opened first.", true);
        }
        if (saveWaiting is { } wait)
        {
            if (posing.WholePose(wait.Loaded, wait.Doc, wait.Frame) is { } pose)
            {
                saveWaiting = null;
                var path = wait.Path;
                saveWriting = (wait.Name, Task.Run(() =>
                    PoseFiles.Save(path, PoseFiles.Write(pose.Skeleton.Bones.Select((bone, i) => (bone.Name, pose.Model[i]))))));
            }
            else if (decode.FailedFor(wait.Loaded))
            {
                saveWaiting = null;
                Note("The motion could not be read: " + decode.Error, true);
            }
        }
        if (saveWriting is not { Writing.IsCompleted: true } done)
            return;
        saveWriting = null;
        if (done.Writing.IsCompletedSuccessfully)
        {
            listed = null;
            saveName = string.Empty;
            Note($"Saved {done.Name}.", false);
        }
        else
            Note($"{done.Name}: {TimelineSession.ErrorText(done.Writing.Exception)}", true);
    }

    /// <summary>Save pose here: a name, then Save, or Replace it? when a pose of that name is in the folder.</summary>
    private void DrawSave()
    {
        var blocked = SaveBlocked();
        var busy = saveWaiting != null || saveWriting != null;
        Widgets.Icon(FontAwesomeIcon.Save, Theme.Muted);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, "Save pose here");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Theme.Scaled(220));
        if (focusSave)
        {
            ImGui.SetKeyboardFocusHere();
            focusSave = false;
        }
        var enter = ImGui.InputTextWithHint("##pose-save-name", "Name", ref saveName, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        if (ImGui.IsItemEdited())
            confirmReplace = null;
        ImGui.SameLine();
        if (confirmReplace != null)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Warning, "Replace it?");
            ImGui.SameLine();
            using (ImRaii.Disabled(blocked != null || busy))
            {
                if (ImGui.Button("Yes##pose-save-replace"))
                    StartSave(true);
            }
            ImGui.SameLine(0, Theme.Scaled(2));
            if (ImGui.Button("No##pose-save-keep"))
                confirmReplace = null;
        }
        else
        {
            var ready = blocked == null && !busy && saveName.Trim().Length > 0;
            using (ImRaii.Disabled(!ready))
            {
                if (ImGui.Button("Save##pose-save") || enter && ready)
                    StartSave(false);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(blocked ?? "Save the pose at the playhead as a .pose file in this folder.");
        }
        if (busy)
        {
            ImGui.SameLine(0, Theme.Gap);
            Widgets.Spinner();
        }
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    /// <summary>Up, the path from the library to the folder shown, and the filter.</summary>
    private void DrawBar()
    {
        if (Widgets.IconButton("##pose-browser-up", FontAwesomeIcon.ArrowUp, "Up one folder", folder != null) && folder != null)
            Open(RootOf(folder) is { } root && string.Equals(root, folder, StringComparison.OrdinalIgnoreCase) ? null : Path.GetDirectoryName(folder));
        ImGui.SameLine(0, Theme.Gap);
        if (Crumb("Library", "library", folder == null))
            Open(null);
        if (folder != null && RootOf(folder) is { } at)
        {
            var path = at;
            Separator();
            if (Crumb(PoseLibrary.RootName(at), "root", string.Equals(at, folder, StringComparison.OrdinalIgnoreCase)))
                Open(at);
            var parts = Path.GetRelativePath(at, folder).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i] == ".")
                    continue;
                path = Path.Combine(path, parts[i]);
                Separator();
                if (Crumb(parts[i], $"part{i}", i == parts.Length - 1))
                    Open(path);
            }
        }

        var searchWidth = Theme.Scaled(220);
        var after = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + Theme.Scaled(16);
        var right = ImGui.GetContentRegionMax().X - searchWidth - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X;
        ImGui.SameLine(Math.Max(after, right));
        if (listing != null)
            Widgets.Spinner();
        else if (Widgets.IconButton("##pose-browser-refresh", FontAwesomeIcon.Sync, "Read the folder again"))
            listed = null;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(searchWidth);
        if (ImGui.InputTextWithHint("##pose-browser-filter", folder == null ? "Search every folder" : "Search this folder", ref filter, 128))
            resetScroll = true;
    }

    private static bool Crumb(string label, string id, bool current)
    {
        using var colour = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero)
            .Push(ImGuiCol.Text, current ? Theme.Text : Theme.Muted);
        ImGui.SameLine(0, Theme.Scaled(2));
        return ImGui.Button($"{label}##crumb-{id}") && !current;
    }

    private static void Separator()
    {
        ImGui.SameLine(0, Theme.Scaled(2));
        Widgets.Icon(FontAwesomeIcon.CaretRight, Theme.Inactive);
    }

    private void DrawGrid(Target? target, string? blocked)
    {
        if (resetScroll)
        {
            ImGui.SetScrollY(0);
            resetScroll = false;
        }
        if (config!.PoseBrowserFolders.Count == 0)
        {
            Widgets.EmptyState(FontAwesomeIcon.FolderOpen, "No pose folders", "Add a folder of .pose files from Anamnesis, Ktisis or Brio.");
            return;
        }
        if (listError != null)
        {
            Widgets.EmptyState(FontAwesomeIcon.ExclamationTriangle, "This folder can't be read", listError);
            return;
        }
        if (entries.Count == 0)
        {
            if (listing == null)
                Widgets.EmptyState(filter.Length > 0 ? FontAwesomeIcon.Search : FontAwesomeIcon.FolderOpen,
                    filter.Length > 0 ? "Nothing found" : "No poses here");
            return;
        }

        var picture = Theme.Scaled(Math.Clamp(config.PoseBrowserTile, Configuration.MinPoseBrowserTile, Configuration.MaxPoseBrowserTile));
        var tile = new Vector2(picture + Theme.Scaled(8), picture + Theme.Scaled(10) + ImGui.GetTextLineHeight() * 2);
        var spacing = ImGui.GetStyle().ItemSpacing;
        var columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing.X) / (tile.X + spacing.X)));
        var rows = (entries.Count + columns - 1) / columns;
        var replacing = target is { } at ? trial.Replacing(at.Editor.Document, at.Layer.Key, at.Frame) : null;
        var canKey = target != null && blocked == null;
        var clipper = ImGui.ImGuiListClipper();
        try
        {
            clipper.Begin(rows, tile.Y + spacing.Y);
            while (clipper.Step())
                for (var row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                    for (var column = 0; column < columns && row * columns + column < entries.Count; column++)
                    {
                        if (column > 0)
                            ImGui.SameLine();
                        var index = row * columns + column;
                        var entry = entries[index];
                        DrawTile(entry, index, tile, entry.Folder || canKey,
                            string.Equals(entry.Path, replacing, StringComparison.OrdinalIgnoreCase),
                            string.Equals(entry.Path, picking?.Path, StringComparison.OrdinalIgnoreCase));
                    }
            clipper.End();
        }
        finally
        {
            clipper.Destroy();
        }
    }

    /// <summary>A folder or pose: its picture or an icon, and its name in up to two lines.</summary>
    private void DrawTile(PoseLibraryEntry entry, int index, Vector2 size, bool enabled, bool applied, bool reading)
    {
        using var id = ImRaii.PushId(index);
        var min = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton("##tile", size);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var rounding = Theme.Scaled(4);
        if (applied)
            draw.AddRectFilled(min, min + size, ImGui.GetColorU32(Theme.Selection), rounding);
        else if (hovered && enabled)
            draw.AddRectFilled(min, min + size, ImGui.GetColorU32(Theme.WithAlpha(Theme.Selection, .5f)), rounding);
        if (applied)
            draw.AddRect(min, min + size, ImGui.GetColorU32(Theme.Accent), rounding, ImDrawFlags.None, Theme.Scaled(2));

        var pad = Theme.Scaled(4);
        var box = new Vector2(size.X - pad * 2);
        var boxMin = min + new Vector2(pad);
        var alpha = enabled ? 1f : .4f;
        if (thumbnails.Get(entry) is { } wrap)
        {
            var scale = Math.Min(box.X / Math.Max(1, wrap.Width), box.Y / Math.Max(1, wrap.Height));
            var shown = new Vector2(wrap.Width * scale, wrap.Height * scale);
            var at = boxMin + (box - shown) / 2;
            draw.AddImage(wrap.Handle, at, at + shown, Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1, 1, 1, alpha)));
            if (entry.Folder)
                DrawGlyph(draw, FontAwesomeIcon.Folder, boxMin + new Vector2(pad), new Vector2(Theme.Scaled(18)), Theme.WithAlpha(Theme.Label, alpha));
        }
        else
            DrawGlyph(draw, entry.Folder ? FontAwesomeIcon.Folder : FontAwesomeIcon.Running, boxMin, box,
                Theme.WithAlpha(entry.Folder ? Theme.AnimationKind : Theme.Label, alpha * .7f), box.X * .45f);
        if (reading)
        {
            var centre = boxMin + box / 2;
            var start = (float)(ImGui.GetTime() * 5.0 % (Math.PI * 2));
            draw.PathArcTo(centre, Theme.Scaled(14), start, start + MathF.PI * 1.5f, 20);
            draw.PathStroke(ImGui.GetColorU32(Theme.Accent), ImDrawFlags.None, Theme.Scaled(3));
        }

        // The name under the picture, in up to two lines; the tooltip has all of it.
        var textTop = boxMin.Y + box.Y + Theme.Scaled(4);
        var colour = ImGui.GetColorU32(Theme.WithAlpha(applied ? Theme.Text : Theme.Label, enabled ? 1 : .6f));
        draw.PushClipRect(new Vector2(min.X, textTop), min + size, true);
        if (!nameLines.TryGetValue((entry.Name, size.X), out var lines))
        {
            if (nameLines.Count >= 4096)
                nameLines.Clear();
            nameLines[(entry.Name, size.X)] = lines = Lines(entry.Name, size.X - pad, 2);
        }
        foreach (var line in lines)
        {
            var width = ImGui.CalcTextSize(line).X;
            draw.AddText(new Vector2(min.X + Math.Max(0, (size.X - width) / 2), textTop), colour, line);
            textTop += ImGui.GetTextLineHeight();
        }
        draw.PopClipRect();

        if (hovered)
            ImGui.SetTooltip(TileTooltip(entry, enabled));
        if (folder == null && filter.Trim().Length == 0 && entry.Folder)
        {
            using var menu = ImRaii.ContextPopupItem("##pose-root-menu");
            if (menu.Success && ImGui.MenuItem("Remove from the list"))
                RemoveFolder(entry.Path);
        }
        else if (!entry.Folder)
        {
            using var menu = ImRaii.ContextPopupItem("##pose-menu");
            if (menu.Success && ImGui.MenuItem("Move to the Recycle Bin", "", false, deleting == null))
                deleting = (entry, RecycleBin.MoveAsync(entry.Path));
        }
        if (!clicked || !enabled)
            return;
        if (entry.Folder)
            Open(entry.Path);
        else
            Pick(entry);
    }

    private string TileTooltip(PoseLibraryEntry entry, bool enabled)
    {
        var text = entry.Name;
        // A search finds poses in folders below; say which.
        if (filter.Trim().Length > 0 && Path.GetDirectoryName(entry.Path) is { } parent &&
            (folder ?? RootOf(entry.Path)) is { } from && !string.Equals(parent, from, StringComparison.OrdinalIgnoreCase))
            text += "\n" + Path.GetRelativePath(from, parent);
        if (entry.Folder)
            return folder == null && filter.Trim().Length == 0 ? $"{text}\n{entry.Path}\nRight-click to remove it from the list." : text;
        return (enabled ? $"{text}\nClick to key it at the playhead." : text) + "\nRight-click to delete it.";
    }

    private static void DrawGlyph(ImDrawListPtr draw, FontAwesomeIcon icon, Vector2 at, Vector2 box, Vector4 colour, float? fontSize = null)
    {
        var glyph = icon.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var size = ImGui.CalcTextSize(glyph);
        var baseSize = ImGui.GetFontSize();
        ImGui.PopFont();
        var wanted = fontSize ?? baseSize;
        draw.AddText(UiBuilder.IconFont, wanted, at + (box - size * (wanted / baseSize)) / 2, ImGui.GetColorU32(colour), glyph);
    }

    /// <summary>
    /// <paramref name="text"/> broken into at most <paramref name="count"/> lines of <paramref name="width"/>: at a space
    /// when there is one in the line, else where it fills. What doesn't fit is cut off.
    /// </summary>
    private static List<string> Lines(string text, float width, int count)
    {
        var lines = new List<string>(count);
        var rest = text.Trim();
        while (rest.Length > 0 && lines.Count < count)
        {
            var fits = 1;
            while (fits < rest.Length && ImGui.CalcTextSize(rest[..(fits + 1)]).X <= width)
                fits++;
            if (fits < rest.Length && lines.Count < count - 1 && rest.LastIndexOf(' ', fits) is var space and > 0)
                fits = space;
            lines.Add(lines.Count == count - 1 ? rest : rest[..fits].TrimEnd());
            rest = rest[Math.Min(rest.Length, fits)..].TrimStart();
        }
        return lines;
    }

    /// <summary>The order the tiles are in and their size, kept in the settings.</summary>
    private void DrawView()
    {
        var sortWidth = Theme.Scaled(120);
        var sizeWidth = Theme.Scaled(160);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var after = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + Theme.Scaled(16);
        var right = ImGui.GetContentRegionMax().X - sortWidth - sizeWidth - ImGui.GetFrameHeight() - spacing * 2;
        ImGui.SameLine(Math.Max(after, right));
        ImGui.SetNextItemWidth(sortWidth);
        var newest = config!.PoseBrowserNewestFirst;
        using (var combo = ImRaii.Combo("##pose-browser-sort", newest ? "Newest first" : "By name"))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable("By name", !newest))
                    Sort(false);
                if (ImGui.Selectable("Newest first", newest))
                    Sort(true);
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Folders come first either way. Newest is when it was added to its folder.");
        ImGui.SameLine();
        Widgets.Icon(FontAwesomeIcon.Image, Theme.Muted);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(sizeWidth);
        var size = Math.Clamp(config.PoseBrowserTile, Configuration.MinPoseBrowserTile, Configuration.MaxPoseBrowserTile);
        if (ImGui.SliderInt("##pose-browser-size", ref size, Configuration.MinPoseBrowserTile, Configuration.MaxPoseBrowserTile, ""))
            config.PoseBrowserTile = size;
        if (ImGui.IsItemDeactivatedAfterEdit())
            save!();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Picture size");
    }

    private void Sort(bool newest)
    {
        if (config!.PoseBrowserNewestFirst == newest)
            return;
        config.PoseBrowserNewestFirst = newest;
        save!();
        entries = PoseLibrary.Sorted(entries, newest);
        resetScroll = true;
    }

    /// <summary>Where a click keys and why it can't, the last pick's note, the choices, Add folder and the count.</summary>
    private void DrawFooter(Target? target, string? blocked)
    {
        ImGui.Separator();
        if (target is { } at)
        {
            Widgets.Icon(FontAwesomeIcon.Running, Theme.AnimationKind);
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Label, $"{at.Layer.Name} at {TimelineWindowViews.FrameText(at.Frame)}");
        }
        if (blocked != null)
        {
            if (target != null)
                ImGui.SameLine(0, Theme.Scaled(12));
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Warning, blocked);
        }
        if (picking is { } pick)
        {
            ImGui.SameLine(0, Theme.Scaled(12));
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, pick.Reading.IsCompleted ? decode.Status : $"Reading {pick.Name}");
        }
        else if (note != null)
        {
            ImGui.SameLine(0, Theme.Scaled(12));
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(noteWarns ? Theme.Warning : Theme.Muted, note);
        }

        if (target is { } shown)
        {
            var again = picked is { } last && trial.Replacing(shown.Editor.Document, shown.Layer.Key, shown.Frame) == last.Path ? picked : null;
            // A changed choice keys the pose shown again, in its place.
            if (options.Draw(again?.Import, shown.Skeleton, shown.Editor.Document, true) && again is { } redo && picking == null)
                Key(target, blocked, redo.Path, redo.Name, redo.File);
        }

        if (folder != null)
            DrawSave();

        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.FolderPlus, "Add folder", null, null, null, null))
            AddFolder();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Add a folder of .pose files to the list.");
        var count = entries.Count >= SearchLimit && filter.Trim().Length > 0 ? $"The first {SearchLimit} found" : entries.Count == 1 ? "1 item" : $"{entries.Count} items";
        ImGui.SameLine(0, Theme.Scaled(16));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Hint, count);
        DrawView();
    }
}
