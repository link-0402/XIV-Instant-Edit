using System.Collections.Immutable;
using System.Globalization;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>Why an effect stops although the animation goes on.</summary>
internal enum TimelineEffectIssue
{
    /// <summary>Loop/wait 1 at frame 0: the effect plays its loop once.</summary>
    LoopsOnce,
    /// <summary>Loop/wait above 1: the effect plays its loop once, then pauses that many frames.</summary>
    Pauses,
    /// <summary>The effect's own schedule kills it at a frame (<see cref="TimelineEffectInfo.EndsAt"/>).</summary>
    EndsItself,
    /// <summary>The effect's sound is marked to loop but has no loop points (<see cref="TimelineEffectInfo.SoundSeconds"/>).</summary>
    SoundStops,
}

/// <summary>
/// Finds effects that stop although they should keep going. An Async VFX entry (C173) keeps a
/// loop/wait value: -1 keeps a looping effect going, 0 turns the entry off and 1 plays one loop
/// (both only when it starts at frame 0), and a higher value plays one loop, then waits that many
/// frames before playing it again (the community TMB notes, tested in game). Vanilla never starts a
/// looping effect with C173; mods that do and keep 1 or a wait see the effect stop after its first
/// loop. Even at -1, an effect whose own schedule kills it, or whose sound plays once, stops; those
/// get a copy that keeps going (<see cref="TimelineEffectInfo.Looping"/>), which Red Carpet's dance
/// needed: its effects kill themselves at frame 908 and its music has no loop points.
/// </summary>
internal static class TimelineEffectCheck
{
    /// <summary>The C173 loop/wait field in <see cref="TimelineEvent.Values"/>.</summary>
    public const string LoopField = "Loop";
    public const int KeepLooping = -1;

    /// <summary>
    /// An effect that stops: <paramref name="Value"/> is its loop/wait, <paramref name="Frames"/> its
    /// longest loop, the frame it ends itself at, or its sound's length. <paramref name="Copy"/> is
    /// the copy that keeps it going, for the issues the timeline alone can't fix.
    /// </summary>
    internal sealed record Problem(string Key, string Path, TimelineEffectIssue Issue, int Value, int Frames, TimelineImport? Copy = null)
    {
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

        /// <summary>The effect stops after its first loop, which loop/wait alone decides.</summary>
        public bool FirstLoop => Issue is TimelineEffectIssue.LoopsOnce or TimelineEffectIssue.Pauses;

        public string Text => Issue switch
        {
            TimelineEffectIssue.LoopsOnce => $"{Name} plays its loop once, then stops.",
            TimelineEffectIssue.Pauses => $"{Name} plays its loop once, then pauses for {Seconds(Value)} s.",
            TimelineEffectIssue.EndsItself => $"{Name} ends itself after {Seconds(Frames)} s.",
            _ => $"{Name}'s sound stops after {Seconds(Frames)} s.",
        };
    }

    /// <summary>
    /// The C173 events whose effect stops. <paramref name="effects"/> holds what was read of each
    /// effect by game path; effects missing from it are not judged, and an effect that ends itself or
    /// whose sound stops counts only when a copy that keeps it going could be made, and an event with a
    /// Stops at (<see cref="TimelineEffectEdit.Stop"/>) isn't judged. An effect ends
    /// itself too soon only when the stop can cut it short: <paramref name="clipLoops"/>, or the stop
    /// falls inside the clip.
    /// </summary>
    public static ImmutableArray<Problem> Find(TimelineDocument document, IReadOnlyDictionary<string, TimelineEffectInfo> effects,
        bool clipLoops = true)
    {
        var problems = ImmutableArray.CreateBuilder<Problem>();
        foreach (var ev in document.Events.IsDefault ? [] : document.Events)
        {
            // An effect with a Stops at stops where the user chose, so keeping it going would undo that.
            if (ev.Magic != "C173" || ev.Values is null || !ev.Values.TryGetValue(LoopField, out var raw) ||
                !effects.TryGetValue(ev.Path, out var effect) || ev.Look?.Stop != null)
                continue;
            var value = (int)raw;
            // 0 turns the entry off on purpose, and 1 at a later frame waits one frame, then plays it again.
            if (value == 0 || value == 1 && ev.Start != 0)
                continue;
            if (!effect.Loops.IsDefaultOrEmpty && value != KeepLooping)
                problems.Add(new Problem(ev.Key, ev.Path, value == 1 ? TimelineEffectIssue.LoopsOnce : TimelineEffectIssue.Pauses, value,
                    effect.Loops.Max(r => r.End - r.Start)));
            if (effect.Looping is not { } copy)
                continue;
            if (effect.EndsAt is { } end && (clipLoops || (long)ev.Start + end < document.Length))
                problems.Add(new Problem(ev.Key, ev.Path, TimelineEffectIssue.EndsItself, value, end, copy));
            if (effect.SoundSeconds is { } seconds)
                problems.Add(new Problem(ev.Key, ev.Path, TimelineEffectIssue.SoundStops, value,
                    (int)Math.Round(seconds * TimelineDocument.Fps), copy));
        }
        return problems.ToImmutable();
    }

    /// <summary>
    /// Keeps every problem's effect going: loop/wait -1 (which clears a Stops at), and the effect's copy
    /// in place of the effect when it ends itself or its sound stops. The copies it uses must be packaged with the edit
    /// (<see cref="Copies"/>). Events that can't be edited stay as they are.
    /// </summary>
    public static TimelineDocument Fix(TimelineDocument document, IEnumerable<Problem> problems)
    {
        var byKey = problems.GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        if (byKey.Count == 0 || document.Events.IsDefault)
            return document;
        var events = document.Events.Select(ev =>
        {
            if (!byKey.TryGetValue(ev.Key, out var found) || ev.Access != TimelineAccess.Editable || ev.Values == null)
                return ev;
            var kept = WithLoop(ev, KeepLooping);
            return found.Select(p => p.Copy).FirstOrDefault(c => c != null) is { } copy ? kept with { Path = copy.GamePath } : kept;
        }).ToImmutableArray();
        return events.SequenceEqual(document.Events) ? document : document with { Events = events };
    }

    /// <summary>
    /// The event with Stops at (<see cref="TimelineEffectEdit.Stop"/>) set to <paramref name="stop"/> frames,
    /// at least 1, or cleared with null. Stops at replaces Keep looping, since removing the effect's kills
    /// and adding one fight over the same scheduler item: setting it puts Loop/wait -1 back to 1.
    /// </summary>
    public static TimelineEvent WithStop(TimelineEvent ev, int? stop)
    {
        var look = (ev.Look ?? new TimelineEffectEdit()) with { Stop = stop is { } frames ? Math.Clamp(frames, 1, TimelineDocument.MaxFrames) : null };
        var values = stop != null && ev.Values?.GetValueOrDefault(LoopField) == KeepLooping ? ev.Values.SetItem(LoopField, 1) : ev.Values;
        return ev with { Look = look, Values = values! };
    }

    /// <summary>The event with Loop/wait set to <paramref name="value"/>; Keep looping (-1) clears Stops at (see <see cref="WithStop"/>).</summary>
    public static TimelineEvent WithLoop(TimelineEvent ev, double value)
    {
        var looped = ev with { Values = (ev.Values ?? ImmutableDictionary<string, double>.Empty).SetItem(LoopField, value) };
        return value == KeepLooping && ev.Look is { Stop: not null } look ? looped with { Look = look with { Stop = null } } : looped;
    }

    /// <summary>The copies <see cref="Fix"/> puts in place, once each.</summary>
    public static ImmutableArray<TimelineImport> Copies(IEnumerable<Problem> problems) =>
        [.. problems.Select(p => p.Copy).OfType<TimelineImport>().DistinctBy(c => c.GamePath, StringComparer.OrdinalIgnoreCase)];

    private static string Seconds(int frames) =>
        (frames / (double)TimelineDocument.Fps).ToString("0.#", CultureInfo.InvariantCulture);
}
