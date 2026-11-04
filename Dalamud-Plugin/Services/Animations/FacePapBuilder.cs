using Dalamud.Plugin.Services;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations;

/// <summary>
/// Builds a face library's PAP from faces taken out of other PAPs: each face's entry and timeline as its source has
/// them, under the name given, over one Havok container merged from the sources' bindings.
/// </summary>
internal static class FacePapBuilder
{
    /// <summary>
    /// The library PAP for <paramref name="modelId"/>/<paramref name="modelType"/> holding <paramref name="sources"/> in order.
    /// The Havok merge runs in one framework-thread hop.
    /// </summary>
    public static async Task<byte[]> BuildAsync(IFramework framework, ushort modelId, byte modelType,
        IReadOnlyList<(byte[] Pap, int EntryIndex, string Name)> sources, string workDirectory, CancellationToken token)
    {
        var (entries, merge) = Plan(sources);
        token.ThrowIfCancellationRequested();
        var havok = await framework.RunOnFrameworkThread(() =>
        {
            token.ThrowIfCancellationRequested();
            return AnimationNative.MergeBindings(merge, workDirectory);
        });
        return FacePapLayout.Build(modelId, modelType, entries, havok);
    }

    /// <summary>
    /// The library's entries (each source entry's type, face flag and timeline under its new name) and what to merge (the
    /// source PAP's Havok and the entry's binding). Each PAP is read once and its faces share one Havok buffer.
    /// Duplicate names are refused here, before any Havok work.
    /// </summary>
    internal static (IReadOnlyList<FacePapLayout.Entry> Entries, IReadOnlyList<(byte[] Havok, int Binding)> Merge) Plan(
        IReadOnlyList<(byte[] Pap, int EntryIndex, string Name)> sources)
    {
        if (sources.Count == 0) throw new InvalidDataException("A face library needs at least one face.");
        if (sources.Count > 4096) throw new InvalidDataException("A face library holds at most 4096 faces.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
            if (!names.Add(source.Name)) throw new InvalidDataException($"The face name '{source.Name}' is used twice.");
        var read = new Dictionary<byte[], (AnimationPap Pap, byte[] Havok)>(ReferenceEqualityComparer.Instance);
        var entries = new List<FacePapLayout.Entry>(sources.Count);
        var merge = new List<(byte[] Havok, int Binding)>(sources.Count);
        foreach (var (bytes, index, name) in sources)
        {
            if (!read.TryGetValue(bytes, out var file))
            {
                var pap = new AnimationPap(bytes);
                read[bytes] = file = (pap, pap.Havok);
            }
            if (index < 0 || index >= file.Pap.Entries.Length)
                throw new InvalidDataException($"The face '{name}' comes from an entry its file doesn't have.");
            var entry = file.Pap.Entries[index];
            entries.Add(new FacePapLayout.Entry(name, entry.Type, entry.Face, file.Pap.TimelineBytes(index)));
            merge.Add((file.Havok, entry.Binding));
        }
        return (entries, merge);
    }
}
