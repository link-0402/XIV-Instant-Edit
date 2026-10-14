using System.Collections.Immutable;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>
/// A stretch of a sound's audio a cut takes out, in seconds: from <see cref="At"/> in the audio as the
/// spans before it left it (the output's time from the sound's start), <see cref="Seconds"/> long;
/// <see cref="double.PositiveInfinity"/> cuts to the end.
/// </summary>
internal readonly record struct TimelineAudioSpan(double At, double Seconds);

/// <summary>
/// Which sounds play across a cut of the motion, and what of their audio the cut takes out so they
/// stay in step with it. No ImGui or Dalamud dependencies.
/// </summary>
internal static class TimelineSoundCuts
{
    /// <summary>
    /// The spans of a sound starting at output frame <paramref name="eventStart"/> whose audio lasts
    /// <paramref name="audioSeconds"/>: at each join after its start that its audio reaches, the frames
    /// the cut removed there as they played; and when the motion's end was trimmed and the audio runs
    /// past the new end, everything from there on. Empty when it crosses no cut.
    /// </summary>
    public static ImmutableArray<TimelineAudioSpan> Spans(TimelineDocument doc, int eventStart, double audioSeconds)
    {
        if (doc.Cuts is not { Whole: false } cuts || cuts.Kept <= 0 || doc.Length <= 0 || !(audioSeconds > 0))
            return [];
        var spans = ImmutableArray.CreateBuilder<TimelineAudioSpan>();
        var speed = (double)doc.Length / cuts.Kept;
        double removed = 0;
        for (var i = 0; i < cuts.Joins.Length; i++)
        {
            var at = cuts.PartEnd(i, doc.Length);
            var frames = cuts.Parts[i + 1].From - cuts.Parts[i].To;
            if (at <= eventStart || frames <= 0)
                continue;
            var start = (at - eventStart) / TimelineDocument.Fps;
            if (start >= audioSeconds - removed)
                break;
            var seconds = frames * speed / TimelineDocument.Fps;
            spans.Add(new TimelineAudioSpan(start, seconds));
            removed += seconds;
        }
        if (cuts.Source > cuts.Parts[^1].To && doc.Length > eventStart)
        {
            var end = (double)(doc.Length - eventStart) / TimelineDocument.Fps;
            if (audioSeconds - removed > end)
                spans.Add(new TimelineAudioSpan(end, double.PositiveInfinity));
        }
        return spans.ToImmutable();
    }

    /// <summary>Whether <paramref name="ev"/>, playing audio <paramref name="audioSeconds"/> long, plays across a cut.</summary>
    public static bool Crosses(TimelineDocument doc, TimelineEvent ev, double audioSeconds) => !Spans(doc, ev.Start, audioSeconds).IsEmpty;
}
