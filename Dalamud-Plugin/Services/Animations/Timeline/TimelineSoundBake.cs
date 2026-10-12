using System.Collections.Immutable;
using System.Text.RegularExpressions;
using InstantEdit.Services.Audio;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>Where an edited sound file goes when saving: a copy beside it, or over the mod's own file.</summary>
internal enum TimelineSoundRoute { Copy, InPlace }

/// <summary>A mod's sound file written over in place: the hash of the bytes the edit was made on, and the edited bytes.</summary>
internal sealed record TimelineSoundReplacement(string BaseHash, byte[] Bytes);

/// <summary>
/// A document with its sound edits and pins written into files: the events name the files they now
/// play, <see cref="Copies"/> are the new files to add, <see cref="InPlace"/> the mod files to
/// overwrite, by game path.
/// </summary>
internal sealed record TimelineSoundBake(TimelineDocument Document, ImmutableArray<TimelineImport> Copies,
    ImmutableDictionary<string, TimelineSoundReplacement> InPlace);

internal static partial class TimelineSoundEdits
{
    private static readonly Regex HashSuffix = new("_[0-9a-f]{8}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The document with every sound edit and pin written into files, ready for
    /// <see cref="TimelineConvert.Apply"/>. A file's edited sounds are written into one file; by its
    /// <paramref name="route"/> that file is a copy at sound/instant_edit/name_hash.scd, mapped beside
    /// the original, which every unpinned event of the file now plays, or replaces the original in
    /// place. A pinned event plays a copy of the edited file holding only its sound and alternative
    /// (name_n_hash.scd, n counting from 1), as sound 0. An edit that changes nothing keeps the file.
    /// <paramref name="sound"/> gives a file by game path; one that can't be read is refused only when
    /// it has an edit or a pin. <paramref name="import"/> gives the files added sounds come from.
    /// </summary>
    public static TimelineSoundBake Bake(TimelineDocument doc, Func<string, byte[]?> sound, Func<string, byte[]?> import,
        Func<TimelineSoundKey, TimelineSoundRoute> route)
    {
        var events = doc.Events.IsDefault ? [] : doc.Events;
        var none = ImmutableDictionary<string, TimelineSoundReplacement>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
        if (doc.SoundEdits.IsEmpty && events.All(e => e.Pin == null))
            return new TimelineSoundBake(doc, [], none);
        var copies = new Dictionary<string, TimelineImport>(StringComparer.OrdinalIgnoreCase);
        var inPlace = none.ToBuilder();
        var updated = events.ToBuilder();
        var touched = Enumerable.Range(0, events.Length)
            .Where(i => TimelineSoundKey.Of(events[i]) is { } key && (doc.SoundEdits.ContainsKey(key) || events[i].Pin != null))
            .GroupBy(i => events[i].Path, StringComparer.OrdinalIgnoreCase);
        foreach (var group in touched)
        {
            var path = group.Key;
            var original = sound(path) ?? throw Unreadable(path);
            var keys = group.Select(i => TimelineSoundKey.Of(events[i])!.Value).Distinct().OrderBy(k => k.Sound).ToList();
            var edits = keys.Where(doc.SoundEdits.ContainsKey).Select(k => (k.Sound, Edit: doc.SoundEdits[k])).ToArray();
            var (edited, ids) = Written(original, keys.Select(k => k.Sound), doc.SoundEdits.GetValueOrDefault, path, import);
            string? copyPath = null;
            var rewritten = false;
            if (!edited.AsSpan().SequenceEqual(original))
            {
                rewritten = route(edits.Length > 0 ? keys.First(doc.SoundEdits.ContainsKey) : keys[0]) == TimelineSoundRoute.InPlace;
                if (rewritten)
                    inPlace[path] = new TimelineSoundReplacement(AnimationPap.Hash(original), edited);
                else
                {
                    copyPath = CopyPath(BaseName(path), edited);
                    copies[copyPath] = Copy(copyPath, edited, path, other => Rewritten(other, edits, import));
                }
            }
            foreach (var i in group)
            {
                var ev = events[i];
                if (ev.Pin is not { } pin)
                {
                    if (copyPath != null)
                        updated[i] = ev with { Path = copyPath };
                    continue;
                }
                var index = TimelineSoundKey.Of(ev)!.Value.Sound;
                var entry = ids[index].IndexOf(pin);
                if (entry < 0)
                    throw new InvalidDataException($"{Path.GetFileName(path)} doesn't have the sound an event always plays any more.");
                var only = ScdEdits.KeepOnly(ScdModel.Read(edited), index, entry).Write();
                var pinPath = CopyPath($"{BaseName(path)}_{entry + 1}", only);
                // A file written over in place can't have copies mapped beside it; they go with the animation.
                copies.TryAdd(pinPath, rewritten ? Copy(pinPath, only, null, null)
                    : Copy(pinPath, only, path, other => ScdEdits.KeepOnly(ScdModel.Read(Rewritten(other, edits, import)), index, entry).Write()));
                updated[i] = ev with { Path = pinPath, Pin = null, Values = (ev.Values ?? ImmutableDictionary<string, double>.Empty).SetItem(TimelineSoundKey.SoundIndexField, 0) };
            }
        }
        return new TimelineSoundBake(doc with { Events = updated.ToImmutable(), SoundEdits = doc.SoundEdits.Clear() }, [.. copies.Values], inPlace.ToImmutable());
    }

    /// <summary>
    /// The edits a preview shows by pointing their file's own game path at the edited file: those on
    /// a file with one sound that some event plays. The game keeps a playing clip's files loaded, so
    /// the file's own path, asked for again whenever the sound starts, hears later edits without a
    /// rebuild. Returns the document with those edits taken off and every pin on them naming its
    /// alternative's place in the edited file ("o" + its index), for a <see cref="Bake"/> that reads
    /// the returned files first. Edits no event plays are dropped; others stay for <see cref="Bake"/>.
    /// </summary>
    /// <param name="apply">Writes an edit into a file as <see cref="Apply"/> does; a caller that already wrote it can hand back its result.</param>
    public static (TimelineDocument Document, ImmutableDictionary<string, byte[]> Files) Redirectable(TimelineDocument doc,
        Func<string, byte[]?> sound, Func<string, byte[]?> import,
        Func<byte[], int, TimelineSoundEdit, (byte[] Bytes, ImmutableArray<string> Ids)>? apply = null)
    {
        apply ??= (original, index, edit) => Apply(original, index, edit, import);
        var files = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (doc.SoundEdits.IsEmpty)
            return (doc, files.ToImmutable());
        var events = doc.Events.IsDefault ? [] : doc.Events;
        var updated = events.ToBuilder();
        var edits = doc.SoundEdits;
        foreach (var (key, edit) in doc.SoundEdits)
        {
            var playing = Enumerable.Range(0, events.Length).Where(i => TimelineSoundKey.Of(events[i]) == key).ToArray();
            if (playing.Length == 0)
            {
                edits = edits.Remove(key);
                continue;
            }
            var original = key.Sound == 0 ? sound(key.Path) ?? throw Unreadable(key.Path) : null;
            if (original == null || ScdFile.Read(original).Sounds.Length != 1)
                continue;
            var (bytes, ids) = apply(original, 0, edit);
            if (!ReferenceEquals(bytes, original))
                files[key.Path] = bytes;
            edits = edits.Remove(key);
            foreach (var i in playing)
                if (events[i].Pin is { } pin)
                    updated[i] = events[i] with
                    {
                        Pin = ids.IndexOf(pin) is >= 0 and var at ? $"{TimelineSoundAlternative.OwnPrefix}{at}"
                            : throw new InvalidDataException($"{Path.GetFileName(key.Path)} doesn't have the sound an event always plays any more."),
                    };
        }
        return (doc with { SoundEdits = edits, Events = updated.ToImmutable() }, files.ToImmutable());
    }

    /// <summary>The file with the edits of <paramref name="sounds"/> written in, one after another, and each sound's alternative ids in written order.</summary>
    private static (byte[] Bytes, Dictionary<int, ImmutableArray<string>> Ids) Written(byte[] original, IEnumerable<int> sounds,
        Func<TimelineSoundKey, TimelineSoundEdit?> editOf, string path, Func<string, byte[]?> import)
    {
        var bytes = original;
        var ids = new Dictionary<int, ImmutableArray<string>>();
        foreach (var index in sounds)
        {
            var edit = editOf(new TimelineSoundKey(path, index)) ?? Initial(ScdModel.Read(bytes), index);
            (bytes, ids[index]) = Apply(bytes, index, edit, import);
        }
        return (bytes, ids);
    }

    /// <summary>Another file of the original (another option of the mod) with the same edits, for <see cref="TimelineImport.Rebuild"/>.</summary>
    private static byte[] Rewritten(byte[] other, (int Sound, TimelineSoundEdit Edit)[] edits, Func<string, byte[]?> import)
    {
        foreach (var (index, edit) in edits)
            other = Apply(other, index, edit, import).Bytes;
        return other;
    }

    /// <summary>A copy mapped beside <paramref name="original"/>, made again from its other files by <paramref name="rebuild"/>; without one it is mapped with the animation.</summary>
    private static TimelineImport Copy(string gamePath, byte[] bytes, string? original, Func<byte[], byte[]>? rebuild) =>
        new(gamePath, ImmutableDictionary<string, byte[]>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase).Add(gamePath, bytes),
            ScdFile.TrySeconds(bytes) ?? 0,
            original == null ? null : ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase).Add(gamePath, original),
            rebuild == null ? null : ImmutableDictionary<string, Func<byte[], byte[]>>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase).Add(gamePath, rebuild));

    private static string CopyPath(string name, byte[] bytes)
    {
        var gamePath = TimelineImports.SoundPath(name + ".scd", bytes);
        return AnimationDependencies.SafeGamePath(gamePath) ? gamePath : throw new InvalidDataException("The sound's name cannot be used in a game path.");
    }

    /// <summary>The name a copy of <paramref name="gamePath"/> starts with: an earlier copy's name without its content hash.</summary>
    private static string BaseName(string gamePath)
    {
        var name = Path.GetFileNameWithoutExtension(gamePath);
        return gamePath.StartsWith("sound/instant_edit/", StringComparison.OrdinalIgnoreCase) ? HashSuffix.Replace(name, string.Empty) : name;
    }

    private static InvalidDataException Unreadable(string path) => new($"{Path.GetFileName(path)} could not be read, so its changes can't be used.");
}
