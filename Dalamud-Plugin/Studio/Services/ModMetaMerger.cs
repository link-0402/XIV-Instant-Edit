using System;
using System.Collections.Generic;
using System.Linq;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services.Project;

namespace InstantEdit.Studio.Services;

/// <summary>One file of an update — written now, or carried over as it was — and what the ledger records for it.</summary>
internal sealed class OutputFile
{
    public required string GamePath { get; init; }
    public required string RelPath { get; init; }
    public required OptionRef Option { get; init; }

    /// <summary>What the new ledger records for it. The merge fills in what its entry pointed at before.</summary>
    public required LedgerFile Ledger { get; init; }

    /// <summary>
    /// Whether the option's entry is pointed at it: for a file written now. A file carried over as
    /// it was keeps whatever entry the mod has for it — removed or repointed in Penumbra, it stays so.
    /// </summary>
    public bool PointEntry { get; init; }
}

/// <summary>Everything one update produced, before it is folded into the mod's meta.json.</summary>
internal sealed class WriteOutput
{
    public List<OutputFile> Files { get; } = new();
}

/// <summary>The result of folding an update into meta.json.</summary>
internal sealed class MergeResult
{
    public required WriteLedger Ledger { get; init; }

    /// <summary>Files an earlier update wrote that nothing uses any more: deleted if they still have the hash written then.</summary>
    public List<LedgerFile> StaleFiles { get; } = new();

    public int Retracted { get; set; }
}

/// <summary>
/// Folds an update into a mod's meta.json without disturbing anything else in it. What an earlier
/// update wrote (its ledger) and no replacement asks for any more is taken back out: an entry it
/// repointed goes back to what it pointed at before, an entry it added is removed — unless it was
/// changed elsewhere since, which makes it the mod's own. Then what this update wrote goes in,
/// replacing in place where an entry exists. Every other file entry, group, option and
/// manipulation stays exactly as it was.
/// </summary>
internal static class ModMetaMerger
{
    public static MergeResult Apply(PenumbraModMeta meta, WriteOutput output, WriteLedger previous)
    {
        var result = new MergeResult { Ledger = new WriteLedger { UpdatedAt = DateTime.UtcNow } };

        static string Key(OptionRef option, string gamePath) => WriteConflict.KeyFor(option, gamePath);

        var produced = output.Files.Select(f => Key(f.Option, f.GamePath)).ToHashSet(StringComparer.Ordinal);

        // ── Take back what no replacement asks for any more ──────────────────
        foreach (var old in previous.Files)
        {
            var option = OptionRef.Parse(old.Option);
            if (produced.Contains(Key(option, old.GamePath)) || !meta.OptionExists(option))
                continue;
            if (!meta.TryGetFile(option, old.GamePath, out var current) || !PathNormalizer.SameRel(current, old.RelPath))
                continue;   // changed since, by someone else: theirs now

            if (old.OriginalRelPath != null) meta.SetFile(option, old.GamePath, old.OriginalRelPath);
            else meta.RemoveFile(option, old.GamePath);
            result.Retracted++;
        }

        // ── Put in what this update wrote ────────────────────────────────────
        var previousByKey = previous.Files
            .GroupBy(f => Key(OptionRef.Parse(f.Option), f.GamePath))
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        foreach (var file in output.Files)
        {
            var entry = file.Ledger;
            if (file.PointEntry)
            {
                // What the entry pointed at before this plugin first took it over, carried from update
                // to update; or, the first time, whatever it points at now — unless that was changed
                // since the last update, which makes the new value the one to go back to.
                string? original = null;
                bool had = meta.TryGetFile(file.Option, file.GamePath, out var current);
                if (previousByKey.TryGetValue(Key(file.Option, file.GamePath), out var before)
                    && (!had || PathNormalizer.SameRel(current, before.RelPath)))
                    original = before.OriginalRelPath;
                else if (had && !PathNormalizer.SameRel(current, file.RelPath))
                    original = current;

                entry.OriginalRelPath = original;
                meta.SetFile(file.Option, file.GamePath, file.RelPath);
            }
            result.Ledger.Files.Add(entry);
        }

        // ── Files nothing uses any more ──────────────────────────────────────
        var kept = result.Ledger.Files.Select(f => PathNormalizer.RelKey(f.RelPath)).ToHashSet(StringComparer.Ordinal);
        var referenced = meta.ReferencedRelKeys();
        foreach (var old in previous.Files.GroupBy(f => PathNormalizer.RelKey(f.RelPath)).Select(g => g.First()))
        {
            var key = PathNormalizer.RelKey(old.RelPath);
            if (!kept.Contains(key) && !referenced.Contains(key) && old.Sha256.Length > 0 && !old.EditedElsewhere)
                result.StaleFiles.Add(old);
        }

        return result;
    }
}
