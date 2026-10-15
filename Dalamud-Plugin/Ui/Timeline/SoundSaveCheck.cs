using Dalamud.Bindings.ImGui;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// Sound changes in a save: which mod files a save in place writes over, and the question when other
/// animations of the mod play one of them too. The answer holds for that save.
/// </summary>
internal sealed class SoundSaveCheck(TimelineSession session, SoundFiles soundFiles)
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

    /// <summary>The mod is being checked for other animations playing the sounds.</summary>
    public bool Checking => soundCheck != null;

    /// <summary>The question about the sounds waits for an answer.</summary>
    public bool Asking => soundAsk != null;

    /// <summary> Forgets a check or a question, which belongs to the clip it was for. </summary>
    public void Forget()
    {
        soundCheck = null;
        soundCheckDocument = null;
        soundAsk = null;
        soundInPlace = null;
    }

    /// <summary> The save started: the sound files it writes over in place are its own now. </summary>
    public void SaveStarted() => soundInPlace = null;

    /// <summary>
    /// Save: in place, edited sounds of the clip's own mod are written over unless other animations of
    /// the mod (or of the game, for a game sound the mod replaces) play them too, which asks first;
    /// everything else saves at once (<paramref name="startSave"/>).
    /// </summary>
    public void Begin(TimelineEditor current, AnimationTimelineSource loaded, AnimationDestination destination, Action startSave)
    {
        soundAsk = null;
        var candidates = destination == AnimationDestination.InPlace ? InPlaceSounds(current.Document, loaded) : [];
        soundInPlace = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
        if (candidates.Count == 0 || loaded.Resource.ModRoot is not { } root)
        {
            soundInPlace.Clear();
            startSave();
            return;
        }
        var exclude = loaded.Resource.ResolvedPath;
        var entry = loaded.EntryIndex;
        soundCheckDocument = current.Document;
        var data = session.Data;
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
                soundFiles.Resource(key.Path) is not { ModDirectory: { } mod } resource ||
                mod != loaded.Resource.ModDirectory || !AnimationResources.CanReplace(resource) ||
                soundFiles.ShownSound(document, key, loaded) is not { } shown || ReferenceEquals(shown.Bytes, shown.Original))
                continue;
            paths.Add((key.Path, resource.ResolvedPath));
        }
        var shared = TimelineSoundUsage.SharedFiles(paths);
        return [.. paths.Where(p => !shared.Contains(p.GamePath)).Select(p => p.GamePath)];
    }

    /// <summary> Where a save puts each edited sound: over the mod's file when <see cref="Begin"/> found it can, else a copy. </summary>
    public Func<TimelineSoundKey, TimelineSoundRoute> Route(AnimationDestination place)
    {
        var inPlace = place == AnimationDestination.InPlace && soundInPlace is { } paths
            ? new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase)
            : [];
        return key => inPlace.Contains(key.Path) ? TimelineSoundRoute.InPlace : TimelineSoundRoute.Copy;
    }

    /// <summary>
    /// After Save's check of the mod: saves when no other animation plays the sounds, else asks whether
    /// to change each one everywhere (the mod's file) or only here (a copy). <paramref name="fail"/> says
    /// why the save can't go on.
    /// </summary>
    public void Draw(TimelineEditor current, Action startSave, Action<string> fail)
    {
        if (soundCheck is { IsCompleted: true } task)
        {
            soundCheck = null;
            var checkedDocument = soundCheckDocument;
            soundCheckDocument = null;
            if (!task.IsCompletedSuccessfully)
            {
                fail("The mod could not be checked for other animations playing its sounds. " + TimelineSession.ErrorText(task.Exception));
                soundInPlace = null;
                return;
            }
            if (!ReferenceEquals(checkedDocument, current.Document))
            {
                fail("The timeline changed while the mod was checked. Save again.");
                soundInPlace = null;
                return;
            }
            var shared = task.Result.Where(s => s.Users > 0 || s.Game).ToList();
            if (shared.Count == 0)
                startSave();
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
                startSave();
            }
            TimelineUi.Tip("Change the mod's own file, for every animation that plays it.");
            ImGui.SameLine();
            if (ImGui.Button("Only here"))
            {
                foreach (var sound in asking)
                    soundInPlace?.Remove(sound.Path);
                soundAsk = null;
                ImGui.CloseCurrentPopup();
                startSave();
            }
            TimelineUi.Tip("Save a copy that only this animation plays.");
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
}
