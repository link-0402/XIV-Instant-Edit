using Dalamud.Bindings.ImGui;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// Keeps the open clip's unsaved edits on disk (<see cref="TimelineAutosave"/>) about once a minute and when the
/// plugin unloads, so a reload or a game crash doesn't lose them, and offers them back the next time. The kept
/// edits go once this window edits another clip, saves, or the edits are discarded.
/// </summary>
/// <param name="restore">Opens the kept edits' clip with them.</param>
/// <param name="key">The workspace's kept edits file: null for yours, a partner preset's Id for its own.</param>
internal sealed class TimelineAutosaver(TimelineSession session, Action<TimelineAutosave> restore, string? key = null)
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    private Task<TimelineAutosave?>? reading;
    private Task writing = Task.CompletedTask;
    // Edits an earlier run kept, offered back until restored, forgotten or written over.
    private TimelineAutosave? offer;
    // The document this run last kept; null while the file holds none of this run's.
    private TimelineDocument? kept;
    private DateTime keptAt;

    /// <summary>Why the kept edits could not come back; shown until the next restore or forget.</summary>
    public string? Error { get; set; }

    /// <summary>Every frame while the window draws: reads an earlier run's edits once, then keeps or drops this run's.</summary>
    public void Poll(TimelineEditor? editor)
    {
        if (session.Animations is not { } service || !writing.IsCompleted)
            return;
        reading ??= Task.Run(() => Read(service.Journals));
        if (!reading.IsCompleted)
            return;
        if (reading.Result is { } earlier)
        {
            offer = earlier;
            reading = Task.FromResult<TimelineAutosave?>(null);
        }
        if (Unkept(editor))
        {
            // Editing anything drops an earlier run's edits at once; this run's are kept again a minute later.
            if (kept != null && DateTime.UtcNow - keptAt < Every)
                return;
            var snapshot = Snapshot(editor!);
            kept = snapshot.Document;
            keptAt = DateTime.UtcNow;
            offer = null;
            writing = Task.Run(() => Write(service.Journals, snapshot));
        }
        else if (kept != null && !(editor is { Dirty: true } && session.Source?.Document != null))
            Forget(service.Journals);
    }

    /// <summary>The window closed, after its edits were saved or discarded: this run's kept edits go too.</summary>
    public void Closed()
    {
        writing.Wait();
        if (kept != null && session.Animations is { } service)
            Forget(service.Journals);
    }

    /// <summary>The plugin unloads: keeps the last edits now, not up to a minute late.</summary>
    public void Flush(TimelineEditor? editor)
    {
        writing.Wait();
        if (session.Animations is { } service && reading is { IsCompleted: true } && Unkept(editor))
            Write(service.Journals, Snapshot(editor!));
    }

    /// <summary>
    /// The kept edits of an earlier run, with Restore and Forget; hidden while <paramref name="hidden"/> (unsaved
    /// edits, or a clip being read). They stay offered until edits are kept over them or forgotten, so a restore
    /// that fails can be tried again.
    /// </summary>
    public void Draw(bool hidden)
    {
        if (Error is { } error)
        {
            Widgets.Banner("##timeline-autosave-error", FeedbackSeverity.Error, error);
            ImGui.Spacing();
        }
        if (offer is not { } earlier || hidden || session.Animations is not { } service)
            return;
        Widgets.Banner("##timeline-autosave", FeedbackSeverity.Info,
            $"Unsaved edits of {AnimationPresentation.AnimationName(earlier.Capture, earlier.Startup)} from {earlier.SavedUtc.ToLocalTime():g} were kept.");
        if (ImGui.Button("Restore##timeline-autosave"))
        {
            Error = null;
            restore(earlier);
        }
        ImGui.SameLine();
        if (ImGui.Button("Forget##timeline-autosave"))
        {
            offer = null;
            Error = null;
            Forget(service.Journals);
        }
        ImGui.Spacing();
    }

    // Whether the open clip has unsaved edits that aren't kept yet.
    private bool Unkept(TimelineEditor? editor) =>
        editor is { Dirty: true } && session.Source is { Document: not null } && !session.Busy && !ReferenceEquals(editor.Document, kept);

    /// <summary>The open clip's edits as kept edits: also what Edit face parks its timeline's edits as until Back.</summary>
    internal TimelineAutosave Snapshot(TimelineEditor editor) => Snapshot(session.Source!, editor.Document, session.Imports.Values);

    /// <summary>
    /// <paramref name="source"/>'s edits <paramref name="doc"/> as kept edits, with the hashes they come back onto only: an
    /// expression's face session names its face's file and base too.
    /// </summary>
    internal static TimelineAutosave Snapshot(AnimationTimelineSource source, TimelineDocument doc, IEnumerable<TimelineImport> imports) =>
        new(source.Capture, source.Startup, source.Resource.Hash, source.MotionTarget.BaseHash, doc,
            [.. doc.SoundEdits.Select(p => (p.Key, p.Value))], [.. imports], DateTime.UtcNow, TimelineAutosave.PlacesFormat, source.Face?.Resource.Hash);

    /// <summary>Whether <paramref name="kept"/> edits can come back onto <paramref name="loaded"/>: the same files and the same base.</summary>
    internal static bool Fits(AnimationTimelineSource loaded, TimelineAutosave kept) =>
        loaded.Document != null && loaded.Resource.Hash == kept.FileHash && loaded.MotionTarget.BaseHash == kept.BaseHash &&
        (kept.FaceHash == null || loaded.Face?.Resource.Hash == kept.FaceHash);

    private TimelineAutosave? Read(AnimationJournalStore store)
    {
        try { return store.LoadAutosave(key); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            session.Log.Warning(e, "Could not read the kept timeline edits.");
            return null;
        }
    }

    private void Write(AnimationJournalStore store, TimelineAutosave snapshot)
    {
        try { store.SaveAutosave(snapshot, key); }
        catch (Exception e) when (e is not OutOfMemoryException) { session.Log.Warning(e, "Could not keep the timeline's unsaved edits."); }
    }

    private void Forget(AnimationJournalStore store)
    {
        kept = null;
        try { store.ClearAutosave(key); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { session.Log.Warning(e, "Could not remove the kept timeline edits."); }
    }
}
