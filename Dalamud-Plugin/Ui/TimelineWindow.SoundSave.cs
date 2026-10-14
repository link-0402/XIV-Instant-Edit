using System.Collections.Immutable;
using Dalamud.Bindings.ImGui;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// Sound changes in a save and a preview: what must be read first, which mod files a save writes
/// over in place, and the question when other animations of the mod play one of them too.
/// </summary>
public sealed partial class TimelineWindow
{
    private const string SoundAskPopup = "Change the sound everywhere?###timeline-sound-ask";

    /// <summary>
    /// A sound file a save would write over in place, how many other animations of the mod name it,
    /// whether the mod maps it to several files, and whether it replaces a game sound (the game's own
    /// animations play it then).
    /// </summary>
    private sealed record SharedSound(string Path, int Users, bool Several, bool Game);

    // Save checks the mod for other animations that play the sounds it would write over in place,
    // for this document; the timeline is read-only meanwhile.
    private Task<List<SharedSound>>? soundCheck;
    private TimelineDocument? soundCheckDocument;
    // The sounds the question asks about, and the sound files the coming save writes over in place.
    private List<SharedSound>? soundAsk;
    private HashSet<string>? soundInPlace;

    /// <summary>
    /// Why the sound changes can't be saved or previewed yet: a file with changes or a pin still being
    /// read, one that couldn't be, or changes that can't be written.
    /// </summary>
    private string? SoundMissing(TimelineDocument document)
    {
        if (source is not { } loaded || document.Events.IsDefault)
            return null;
        foreach (var ev in document.Events)
        {
            if (TimelineSoundKey.Of(ev) is not { } key || !document.SoundEdits.ContainsKey(key) && ev.Pin == null)
                continue;
            var file = Path.GetFileName(key.Path);
            var read = SoundFor(key.Path, loaded);
            if (read.Bytes == null)
                return read.Error != null ? $"{file} could not be read, so its changes can't be used." : "Reading the sound files.";
            if (ShownSound(document, key, loaded) is not { } shown)
                continue;
            if (shown.Error is { } error)
                return $"{file}: {error}";
            if (TimelineWindowViews.PinGone(ev.Pin, shown.Ids, file) is { } gone)
                return gone;
        }
        return null;
    }

    /// <summary>
    /// <paramref name="document"/> with its sound changes and pins written into files (see
    /// <see cref="TimelineSoundEdits.Bake"/>): the copies to package and the mod files written over in place.
    /// </summary>
    private TimelineSoundBake WithSounds(TimelineDocument document, Func<TimelineSoundKey, TimelineSoundRoute> route, Func<string, byte[]?>? sound = null) =>
        TimelineSoundEdits.Bake(document, sound ?? SoundBytes, ImportBytes, route);

    /// <summary>
    /// Save: in place, edited sounds of the clip's own mod are written over unless other animations of
    /// the mod (or of the game, for a game sound the mod replaces) play them too, which asks first;
    /// everything else saves at once.
    /// </summary>
    private void BeginSave(TimelineEditor current, AnimationTimelineSource loaded)
    {
        soundAsk = null;
        var candidates = destination == AnimationDestination.InPlace ? InPlaceSounds(current.Document, loaded) : [];
        soundInPlace = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
        if (candidates.Count == 0 || loaded.Resource.ModRoot is not { } root)
        {
            soundInPlace.Clear();
            StartSave(current, loaded);
            return;
        }
        var exclude = loaded.Resource.ResolvedPath;
        var entry = loaded.EntryIndex;
        soundCheckDocument = current.Document;
        // The clip's own PAP counts with its other entries; a game sound the mod replaces is played by the game's own animations too.
        soundCheck = Task.Run(() => candidates.Select(path => new SharedSound(path,
            TimelineSoundUsage.OtherUsers(root, path, exclude, entry).Count, PenumbraService.AnimationMappedFiles(root, path).Count > 1,
            data.FileExists(path))).ToList());
    }

    /// <summary>
    /// The sound files a save in place would write over: edited files of the clip's own mod that some
    /// event plays. Game paths the mod maps to one file go into copies, as one file can't be written over twice.
    /// </summary>
    private List<string> InPlaceSounds(TimelineDocument document, AnimationTimelineSource loaded)
    {
        var paths = new List<(string GamePath, string Resolved)>();
        var events = document.Events.IsDefault ? [] : document.Events;
        foreach (var key in document.SoundEdits.Keys)
        {
            if (paths.Any(p => string.Equals(p.GamePath, key.Path, StringComparison.OrdinalIgnoreCase)) || !events.Any(ev => TimelineSoundKey.Of(ev) == key) ||
                !soundReads.TryGetValue(key.Path, out var read) || read.Resource is not { ModDirectory: { } mod } resource ||
                mod != loaded.Resource.ModDirectory || !AnimationResources.CanReplace(resource) ||
                ShownSound(document, key, loaded) is not { } shown || ReferenceEquals(shown.Bytes, shown.Original))
                continue;
            paths.Add((key.Path, resource.ResolvedPath));
        }
        var shared = TimelineSoundUsage.SharedFiles(paths);
        return [.. paths.Where(p => !shared.Contains(p.GamePath)).Select(p => p.GamePath)];
    }

    /// <summary> Where a save puts each edited sound: over the mod's file when <see cref="BeginSave"/> found it can, else a copy. </summary>
    private Func<TimelineSoundKey, TimelineSoundRoute> SoundRoute(AnimationDestination place)
    {
        var inPlace = place == AnimationDestination.InPlace && soundInPlace is { } paths
            ? new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase)
            : [];
        return key => inPlace.Contains(key.Path) ? TimelineSoundRoute.InPlace : TimelineSoundRoute.Copy;
    }

    /// <summary>
    /// After Save's check of the mod: saves when no other animation plays the sounds, else asks whether
    /// to change each one everywhere (the mod's file) or only here (a copy). The answer holds for that save.
    /// </summary>
    private void DrawSoundAsk(TimelineEditor current, AnimationTimelineSource loaded)
    {
        if (soundCheck is { IsCompleted: true } task)
        {
            soundCheck = null;
            var checkedDocument = soundCheckDocument;
            soundCheckDocument = null;
            if (!task.IsCompletedSuccessfully)
            {
                saveError = "The mod could not be checked for other animations playing its sounds. " + ErrorText(task.Exception);
                soundInPlace = null;
                return;
            }
            if (!ReferenceEquals(checkedDocument, current.Document))
            {
                saveError = "The timeline changed while the mod was checked. Save again.";
                soundInPlace = null;
                return;
            }
            var shared = task.Result.Where(s => s.Users > 0 || s.Game).ToList();
            if (shared.Count == 0)
                StartSave(current, loaded);
            else
                soundAsk = shared;
        }
        if (soundAsk is not { } asking)
            return;
        if (!ImGui.IsPopupOpen(SoundAskPopup))
            ImGui.OpenPopup(SoundAskPopup);
        if (!ImGui.BeginPopupModal(SoundAskPopup, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        try
        {
            foreach (var sound in asking)
                ImGui.TextUnformatted(TimelineWindowViews.SoundAsk(Path.GetFileName(sound.Path), sound.Users, sound.Several, sound.Game));
            ImGui.Spacing();
            if (ImGui.Button("Everywhere"))
            {
                soundAsk = null;
                ImGui.CloseCurrentPopup();
                StartSave(current, loaded);
            }
            Tip("Change the mod's own file, for every animation that plays it.");
            ImGui.SameLine();
            if (ImGui.Button("Only here"))
            {
                foreach (var sound in asking)
                    soundInPlace?.Remove(sound.Path);
                soundAsk = null;
                ImGui.CloseCurrentPopup();
                StartSave(current, loaded);
            }
            Tip("Save a copy that only this animation plays.");
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                soundAsk = null;
                soundInPlace = null;
                ImGui.CloseCurrentPopup();
            }
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    /// <summary>
    /// The document a preview builds once looks and sound changes it shows by redirecting are taken
    /// off, with the sound files that redirect (see <see cref="TimelineSoundEdits.Redirectable"/>).
    /// </summary>
    private (TimelineDocument Document, ImmutableDictionary<string, byte[]> Sounds) PreviewRest(TimelineDocument document)
    {
        if (previewRestCache is { } cached && ReferenceEquals(cached.For, document))
            return cached.Result;
        var rest = TimelineSoundEdits.Redirectable(TimelineEffectLooks.Redirectable(document).Document, SoundBytes, ImportBytes, AppliedSound);
        previewRestCache = (document, rest);
        return rest;
    }

    // The last document PreviewRest was worked out for: the stale check and the sync ask for the same one.
    private (TimelineDocument For, (TimelineDocument Document, ImmutableDictionary<string, byte[]> Sounds) Result)? previewRestCache;
}
