using System.Collections.Immutable;
using System.Text;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>A face, blink or lip shape in a PAP: its name, its entry, and its binding's fingerprint.</summary>
internal sealed record FaceCopyEntry(string Name, int EntryIndex, string Fingerprint, byte[] Pap);

/// <summary>
/// A face to copy into a library: where it comes from, and the name it gets there. An <paramref name="Edited"/> face is a
/// face session's bake under a name of its own, not a face the source plays.
/// </summary>
internal sealed record FaceCopy(FaceCopyEntry Source, string Name, bool Edited = false);

/// <summary>
/// What copying faces into a library does. <paramref name="Build"/> is the library's PAP in entry order (its own entries
/// it keeps, then the copies), null when nothing is written. Names are the copies' names in the library.
/// <paramref name="Asks"/> are the names the library has for another motion: none is written until the copy may replace them.
/// </summary>
internal sealed record FaceCopyPlan(IReadOnlyList<(byte[] Pap, int EntryIndex, string Name)>? Build,
    ImmutableArray<string> Added, ImmutableArray<string> Already, ImmutableArray<string> Replaced, ImmutableArray<string> Asks);

/// <summary>
/// Copying faces into an Instant Edit library unchanged. A copy keeps its source's motion, so its binding fingerprints
/// as its source's: the library having that name with the same fingerprint already holds it.
/// </summary>
internal static class FaceLibraryCopy
{
    // A PAP entry's name field is 32 bytes with its terminator.
    private const int MaxNameBytes = 31;

    /// <summary>
    /// The library's PAP after copying <paramref name="copies"/> into its <paramref name="library"/> entries for one face
    /// folder (none when it has no file there yet). A name the library has with the same motion is already there; with
    /// another motion it is asked about, and replaced when <paramref name="replace"/>. Throws with the reason for a face
    /// the face always has loaded (<paramref name="resident"/>; an edited face only by its new name), a name that breaks the
    /// rules, or two copies of one name with different motions.
    /// </summary>
    public static FaceCopyPlan Plan(IReadOnlyList<FaceCopyEntry> library, IReadOnlyList<FaceCopy> copies, IReadOnlySet<string> resident, bool replace)
    {
        if (copies.Count == 0) throw new InvalidDataException("Choose a face to copy.");
        var wanted = new List<FaceCopy>();
        foreach (var copy in copies)
        {
            if (NameProblem(copy.Source.Name, copy.Name) is { } problem) throw new InvalidDataException(problem);
            if (resident.Contains(copy.Name) || !copy.Edited && resident.Contains(copy.Source.Name))
                throw new InvalidDataException($"{copy.Name} always plays, so it needs no library.");
            if (wanted.FirstOrDefault(w => w.Name == copy.Name) is { } twin)
            {
                if (twin.Source.Fingerprint != copy.Source.Fingerprint) throw new InvalidDataException($"Two faces are named {copy.Name}. Rename one.");
                continue;
            }
            wanted.Add(copy);
        }
        var added = ImmutableArray.CreateBuilder<string>();
        var already = ImmutableArray.CreateBuilder<string>();
        var replaced = ImmutableArray.CreateBuilder<string>();
        var asks = ImmutableArray.CreateBuilder<string>();
        var copied = new List<FaceCopy>();
        foreach (var copy in wanted)
        {
            if (library.FirstOrDefault(e => e.Name == copy.Name) is not { } held) { added.Add(copy.Name); copied.Add(copy); }
            else if (held.Fingerprint == copy.Source.Fingerprint) already.Add(copy.Name);
            else if (replace) { replaced.Add(copy.Name); copied.Add(copy); }
            else asks.Add(copy.Name);
        }
        if (asks.Count > 0 || copied.Count == 0)
            return new(null, added.ToImmutable(), already.ToImmutable(), replaced.ToImmutable(), asks.ToImmutable());
        var replacing = replaced.ToHashSet(StringComparer.Ordinal);
        var build = library.Where(e => !replacing.Contains(e.Name)).Select(e => (e.Pap, e.EntryIndex, e.Name))
            .Concat(copied.Select(c => (c.Source.Pap, c.Source.EntryIndex, c.Name))).ToList();
        return new(build, added.ToImmutable(), already.ToImmutable(), replaced.ToImmutable(), []);
    }

    /// <summary>
    /// Why a copy of <paramref name="source"/> can't be named <paramref name="name"/>, or null: it keeps its cfxf_, cfxb_ or
    /// cfxl_ kind, and a new name has only a-z, 0-9 and _ after it, within 31 bytes.
    /// </summary>
    internal static string? NameProblem(string source, string name)
    {
        if (name == source) return Encoding.UTF8.GetByteCount(name) <= MaxNameBytes && name.Length > 0 ? null : $"{name} is not a name a library can hold.";
        if (source.Length < 5 || !name.StartsWith(source[..5], StringComparison.Ordinal) || name.Length == 5 ||
            name.Length > MaxNameBytes || name[5..].Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            return "Use letters or numbers in the name.";
        return null;
    }
}
