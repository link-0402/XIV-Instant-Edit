using System.Collections.Immutable;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>A face or lip motion a timeline plays from a set, by its motion name; a null <paramref name="Set"/> is in no set the timeline names.</summary>
internal sealed record MixedFace(string Motion, string? Set);

/// <summary>
/// What a timeline's faces need (<see cref="MixedFaces.Needs"/>). <paramref name="Known"/> is false while the motions
/// the face always has loaded aren't known: then nothing is decided and nothing is listed.
/// </summary>
/// <param name="Mixed">The faces come from more than one set: a TMB loads only one, so they must be copied into one.</param>
/// <param name="Faces">Every face the timeline plays that isn't resident, once per motion and set, in the order first used.</param>
internal sealed record MixedFaceNeeds(bool Known, bool Mixed, ImmutableArray<MixedFace> Faces);

/// <summary>
/// Which sets a timeline's faces come from. A TMB names one face set (TMPP), so a timeline whose faces come from
/// several needs them copied into one library on save (spec section 5).
/// </summary>
internal static class MixedFaces
{
    /// <summary>
    /// The non-resident faces on <paramref name="doc"/>'s expression and lip sync lanes, each with its set: the event's
    /// own <see cref="TimelineEvent.Pack"/>, else the timeline's <see cref="TimelineDocument.FacePack"/>. Sets compare in
    /// any case. A face with neither is listed with a null set (it can't be found) and doesn't count as a set of its own.
    /// Without <paramref name="resident"/> (the catalog isn't ready) nothing is known, so resident faces are never copied.
    /// </summary>
    public static MixedFaceNeeds Needs(TimelineDocument doc, IReadOnlySet<string>? resident)
    {
        if (resident == null)
            return new(false, false, []);
        var faces = ImmutableArray.CreateBuilder<MixedFace>();
        var seen = new HashSet<(string Motion, string Set)>();
        var sets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!doc.Events.IsDefault)
            foreach (var ev in doc.Events)
            {
                if (ev.Lane is not (TimelineLane.Expression or TimelineLane.LipSync) || ev.Path.Length == 0 || resident.Contains(ev.Path))
                    continue;
                var set = !string.IsNullOrEmpty(ev.Pack) ? ev.Pack : !string.IsNullOrEmpty(doc.FacePack) ? doc.FacePack : null;
                if (!seen.Add((ev.Path, set?.ToLowerInvariant() ?? "")))
                    continue;
                faces.Add(new(ev.Path, set));
                if (set != null)
                    sets.Add(set);
            }
        return new(true, sets.Count > 1, faces.ToImmutable());
    }
}
