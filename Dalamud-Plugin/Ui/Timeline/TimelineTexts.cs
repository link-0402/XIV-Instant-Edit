using System.Collections.Immutable;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// What the window shows about the document, made again only when the document changes: the events'
/// labels, the length, the header's note, the save's warnings and the effects that stop too soon.
/// The canvas, the header, the status line's notes, the properties panel and the save footer read them.
/// </summary>
internal sealed class TimelineTexts(TimelineSession session)
{
    private TimelineDocument? labelDocument;
    // The rebuild the texts were made with: the layer bones warning names its skeleton's bones.
    private RaceRebuild? labelRebuild;

    /// <summary>Each event's label, in the document's order.</summary>
    public string[] Labels { get; private set; } = [];
    public string LengthText { get; private set; } = string.Empty;
    public string? HeaderNote { get; private set; }
    public string? PastEndWarning { get; private set; }
    /// <summary>The note that a pose plays its events once (<see cref="TimelineWindowViews.PoseLoopNote"/>).</summary>
    public string? PoseLoopNote { get; private set; }
    public string? LayerBonesWarning { get; private set; }
    public ImmutableArray<TimelineEffectCheck.Problem> EffectProblems { get; private set; } = [];
    public string? EffectWarning { get; private set; }
    /// <summary>The copies keeping the effects going swaps in.</summary>
    public ImmutableArray<TimelineImport> EffectCopies { get; private set; } = [];
    /// <summary>How many events keeping the effects going changes.</summary>
    public int EffectEvents { get; private set; }

    // The link line, for the document and the link states it was made for: states change without the document changing.
    private (TimelineDocument? Document, IReadOnlyDictionary<string, LinkState>? States, string? Text) linkOff;

    /// <summary>
    /// The save footer's line when a link of the open document is off now, so the save leaves that partner's edits out
    /// (<see cref="TimelineWindowViews.LinkOffWarning"/>); null when none is.
    /// </summary>
    public string? LinkOffWarning
    {
        get
        {
            if (session.Editor?.Document is not { } doc)
                return null;
            var states = session.LinkStates;
            if (!ReferenceEquals(linkOff.Document, doc) || !ReferenceEquals(linkOff.States, states))
                linkOff = (doc, states, TimelineWindowViews.LinkOffWarning(doc, states, session.PartnerName));
            return linkOff.Text;
        }
    }

    // The bake-in line, for the document and the partners it was made for: other workspaces' links change without this document.
    private (TimelineDocument? Document, IReadOnlyList<string>? Linked, string? Text) linkBake;

    /// <summary>
    /// The save footer's line when the save bakes the layers in (<paramref name="bakesIn"/>) and so ends a link of the open document, or
    /// another workspace's link to it (<see cref="TimelineWindowViews.LinkBakeWarning"/>); null when none ends. Asks for the other
    /// workspaces' links only while the save bakes in.
    /// </summary>
    public string? LinkBakeWarning(bool bakesIn)
    {
        if (!bakesIn || session.Editor?.Document is not { } doc)
            return null;
        var linked = session.LinkedBy();
        if (!ReferenceEquals(linkBake.Document, doc) || linkBake.Linked is not { } was || !was.SequenceEqual(linked, StringComparer.Ordinal))
            linkBake = (doc, linked, TimelineWindowViews.LinkBakeWarning(true, TimelineWindowViews.LinkBakeNames(doc, session.PartnerName, linked)));
        return linkBake.Text;
    }

    /// <summary> Makes the texts again for the next document drawn, as after a clip is read. </summary>
    public void Forget() => labelDocument = null;

    /// <summary> Labels, the past-end warning, the length texts and the effect problems, rebuilt only when the document or the session's rebuild changes. </summary>
    public void Refresh(TimelineDocument doc)
    {
        if (ReferenceEquals(labelDocument, doc) && labelRebuild == session.Rebuild)
            return;
        var events = doc.Events.IsDefault ? [] : doc.Events;
        Labels = new string[events.Length];
        for (var index = 0; index < events.Length; index++)
            Labels[index] = TimelineWindowViews.EventLabel(events[index]);
        LengthText = TimelineWindowViews.LengthText(doc);
        HeaderNote = TimelineWindowViews.HeaderNote(doc);
        PastEndWarning = TimelineWindowViews.PastEndWarning(doc);
        PoseLoopNote = session.Source is { } clip ? TimelineWindowViews.PoseLoopNote(doc, clip.Clip.GamePath) : null;
        LayerBonesWarning = session.Source is not { } source ? null : TimelineWindowViews.LayerBonesWarning(doc, session.Fresh(source).Clip,
            session.EditingSkeleton(source), session.Rebuild?.Model);
        EffectProblems = session.Source?.EffectProblems(doc) ?? [];
        EffectWarning = TimelineWindowViews.EffectLoopWarning(EffectProblems);
        EffectCopies = TimelineEffectCheck.Copies(EffectProblems);
        EffectEvents = EffectProblems.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count();
        labelDocument = doc;
        labelRebuild = session.Rebuild;
    }

    /// <summary> <paramref name="ev"/>'s label: the cached one while <paramref name="doc"/> is the one the labels were made for. </summary>
    public string LabelOf(TimelineDocument doc, TimelineEvent ev)
    {
        if (ReferenceEquals(labelDocument, doc) && TimelineEdits.IndexOf(doc, ev.Key) is var index and >= 0 && index < Labels.Length)
            return Labels[index];
        return TimelineWindowViews.EventLabel(ev);
    }
}
