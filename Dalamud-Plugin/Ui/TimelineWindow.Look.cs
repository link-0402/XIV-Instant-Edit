using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// Effect looks and sound changes shown in game while Preview or Live runs, without building the clip
/// again (see <see cref="EffectLookEditor"/> for the editor itself).
/// </summary>
public sealed partial class TimelineWindow
{
    // Looks shown in game while Preview or Live runs: each effect's own path points at an edited copy in
    // the preview's temporary mod (see TimelineEffectLooks.Redirectable). The game asks for the path again
    // whenever the effect starts, so a changed look shows then, without building the clip again.
    // The preview's files without the looks, and the previewed document without the looks it redirects.
    private ImmutableDictionary<string, byte[]>? previewBaseFiles;
    private TimelineDocument? previewRest;
    private ImmutableDictionary<string, TimelineEffectEdit> shownLooks = ImmutableDictionary<string, TimelineEffectEdit>.Empty;
    // The same for sound changes: each one-sound file's own path points at the edited file (TimelineSoundEdits.Redirectable).
    private ImmutableDictionary<string, byte[]> shownSounds = ImmutableDictionary<string, byte[]>.Empty;
    // Live's temporary mod with looks or sounds is up (Live itself plays the clip's own file and needs none).
    private bool liveLooks;
    private Task? lookSync;
    private (ImmutableDictionary<string, TimelineEffectEdit> Looks, ImmutableDictionary<string, byte[]> Sounds, bool Live, int Generation)? lookSyncing;
    private int lookGeneration;
    // The document last compared with the preview, and the one whose looks were last shown or found unshowable.
    private TimelineDocument? staleChecked;
    private TimelineDocument? lookChecked;
    private bool previewStale;

    /// <summary> Forgets the looks shown with a preview that ended or is replaced; a running update is ignored. </summary>
    private void ForgetPreviewLooks()
    {
        lookGeneration++;
        previewBaseFiles = null;
        previewRest = null;
        shownLooks = ImmutableDictionary<string, TimelineEffectEdit>.Empty;
        shownSounds = ImmutableDictionary<string, byte[]>.Empty;
        liveLooks = false;
        staleChecked = null;
        lookChecked = null;
        previewStale = false;
    }

    /// <summary> Ends Live's temporary mod with looks, when it has one. </summary>
    private void StopLiveLooks()
    {
        if (!liveLooks && lookSyncing is not { Live: true })
            return;
        ForgetPreviewLooks();
        StopTemporaryMod();
    }

    /// <summary>
    /// Every frame: while Preview or Live runs, puts the document's effect looks into the temporary mod
    /// when they changed, one update at a time. A preview whose document changed otherwise is stale
    /// and needs Preview again; Live shows looks over the clip's own file.
    /// </summary>
    private void SyncLooks(TimelineEditor current)
    {
        if (lookSync != null)
        {
            if (!lookSync.IsCompleted)
                return;
            var task = lookSync;
            lookSync = null;
            if (lookSyncing is { } done && done.Generation == lookGeneration)
            {
                if (task.IsCompletedSuccessfully)
                {
                    shownLooks = done.Looks;
                    shownSounds = done.Sounds;
                    liveLooks = done.Live && (done.Looks.Count > 0 || done.Sounds.Count > 0);
                }
                else if (!task.IsCanceled)
                    previewError = "The effect looks and sound changes could not be shown. " + TimelineSession.ErrorText(task.Exception);
            }
            lookSyncing = null;
            lookChecked = null;
        }
        var live = liveWanted && liveStarted;
        var previewing = !liveWanted && previewFingerprint != null && !previewJob && previewBaseFiles != null && previewRest != null;
        if (!live && !previewing)
            return;
        var doc = current.Document;
        if (previewing && !ReferenceEquals(doc, staleChecked) && soundFiles.SoundMissing(doc) == null)
        {
            staleChecked = doc;
            // Only looks or redirected sound changes changed: the preview shows the document once they are in.
            try { previewStale = !TimelineEdits.Same(soundFiles.PreviewRest(doc).Document, previewRest!); }
            catch (Exception e) when (e is InvalidDataException or ArgumentException) { previewStale = true; }
            if (!previewStale)
                previewDocument = doc;
        }
        if (previewing && previewStale || ReferenceEquals(doc, lookChecked) || previewStop is { IsCompleted: false } || preview.Busy ||
            previewActor is not { } actor || effectLooks.LookMissing(doc) != null || soundFiles.SoundMissing(doc) != null)
            return;
        lookChecked = doc;
        var looks = TimelineEffectLooks.Redirectable(doc).Looks;
        ImmutableDictionary<string, byte[]> sounds;
        try { sounds = soundFiles.PreviewRest(doc).Sounds; }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
        {
            previewError = e.Message;
            return;
        }
        if (TimelineEffectLooks.SameLooks(looks, shownLooks) && SameFiles(sounds, shownSounds))
            return;
        ImmutableDictionary<string, byte[]> files;
        try
        {
            files = (previewing ? previewBaseFiles! : ImmutableDictionary<string, byte[]>.Empty)
                .SetItems(TimelineEffectLooks.Redirected(looks, effectLooks.EffectBytes)).SetItems(sounds);
        }
        catch (InvalidDataException e)
        {
            previewError = e.Message;
            return;
        }
        lookSyncing = (looks, sounds, live, lookGeneration);
        // Live without looks needs no temporary mod.
        lookSync = files.Count == 0 ? preview.StopAsync() : preview.ShowAsync(actor, files, CancellationToken.None);
    }

    /// <summary> Whether two sets of files by game path hold the same bytes. </summary>
    private static bool SameFiles(IReadOnlyDictionary<string, byte[]> a, IReadOnlyDictionary<string, byte[]> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && (ReferenceEquals(pair.Value, other) || pair.Value.AsSpan().SequenceEqual(other)));
}
