using System.Collections.Immutable;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// What the window shows about the document, made again only when the document changes: the events'
/// labels, the length, the header's note, the save's warnings and the effects that stop too soon.
/// The canvas, the header, the toolbar's messages, the properties panel and the save footer read them.
/// </summary>
internal sealed class TimelineTexts(TimelineSession session)
{
    private TimelineDocument? labelDocument;

    /// <summary>Each event's label, in the document's order.</summary>
    public string[] Labels { get; private set; } = [];
    public string LengthText { get; private set; } = string.Empty;
    public string? HeaderNote { get; private set; }
    public string? PastEndWarning { get; private set; }
    public string? LayerBonesWarning { get; private set; }
    public ImmutableArray<TimelineEffectCheck.Problem> EffectProblems { get; private set; } = [];
    public string? EffectWarning { get; private set; }
    /// <summary>The copies keeping the effects going swaps in.</summary>
    public ImmutableArray<TimelineImport> EffectCopies { get; private set; } = [];
    /// <summary>How many events keeping the effects going changes.</summary>
    public int EffectEvents { get; private set; }

    /// <summary> Makes the texts again for the next document drawn, as after a clip is read. </summary>
    public void Forget() => labelDocument = null;

    /// <summary> Labels, the past-end warning, the length texts and the effect problems, rebuilt only when the document changes. </summary>
    public void Refresh(TimelineDocument doc)
    {
        if (ReferenceEquals(labelDocument, doc))
            return;
        var events = doc.Events.IsDefault ? [] : doc.Events;
        Labels = new string[events.Length];
        for (var index = 0; index < events.Length; index++)
            Labels[index] = TimelineWindowViews.EventLabel(events[index]);
        LengthText = TimelineWindowViews.LengthText(doc);
        HeaderNote = TimelineWindowViews.HeaderNote(doc);
        PastEndWarning = TimelineWindowViews.PastEndWarning(doc);
        LayerBonesWarning = session.Source is not { } source ? null : TimelineWindowViews.LayerBonesWarning(doc, session.Fresh(source).Clip);
        EffectProblems = session.Source?.EffectProblems(doc) ?? [];
        EffectWarning = TimelineWindowViews.EffectLoopWarning(EffectProblems);
        EffectCopies = TimelineEffectCheck.Copies(EffectProblems);
        EffectEvents = EffectProblems.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count();
        labelDocument = doc;
    }

    /// <summary> <paramref name="ev"/>'s label: the cached one while <paramref name="doc"/> is the one the labels were made for. </summary>
    public string LabelOf(TimelineDocument doc, TimelineEvent ev)
    {
        if (ReferenceEquals(labelDocument, doc) && TimelineEdits.IndexOf(doc, ev.Key) is var index and >= 0 && index < Labels.Length)
            return Labels[index];
        return TimelineWindowViews.EventLabel(ev);
    }
}
