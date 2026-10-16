using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;

namespace InstantEdit.Studio.Services;

/// <summary>What happened to one file of an update.</summary>
internal enum WriteOutcome
{
    /// <summary>Written into the mod.</summary>
    Ok,

    /// <summary>Not written: nothing to make it from.</summary>
    Skipped,

    /// <summary>Could not be made or written; the mod keeps what it had there.</summary>
    Failed,

    /// <summary>Changed elsewhere since it was written here, so this change was not written; waiting for the user to choose.</summary>
    Kept,

    /// <summary>Written onto a copy that was changed elsewhere, keeping those changes (a material's textures carried over).</summary>
    Merged,
}

/// <summary>What to do with a file that both a replacement here and something elsewhere changed.</summary>
internal enum ConflictChoice
{
    /// <summary>Write what is set up here, over the change made elsewhere.</summary>
    UseMine,

    /// <summary>Keep the file as it was changed elsewhere; what is set up here leaves it alone until it changes again.</summary>
    KeepTheirs,
}

/// <summary>One file (or one attempted file) of an update.</summary>
internal sealed record WriteEntry(
    WriteOutcome Outcome,
    string       Label,
    string?      GamePath,
    string?      Detail,
    Target?      Source);

/// <summary>
/// A file this plugin wrote that was changed elsewhere since, while a replacement here now asks for
/// something else in it. It is left as it is until the user picks a <see cref="ConflictChoice"/>.
/// </summary>
internal sealed record WriteConflict(string Key, string GamePath, OptionRef Option, string RelPath, string Label, Target? Source, string Reason)
{
    /// <summary>The key of a file in the default files or an option.</summary>
    public static string KeyFor(OptionRef option, string gamePath) => $"{option.Serialize().ToLowerInvariant()}|{PathNormalizer.GamePath(gamePath)}";
}

/// <summary>
/// Converts one image into a .tex at <paramref name="outputPath"/>, returning null on success or why
/// it failed. Builds and updates are handed one of these so they can use Penumbra's native
/// converter without knowing anything about IPC; when there is none, they encode the texture
/// themselves.
/// </summary>
internal delegate string? TextureConversion(string sourcePath, TextureCompression compression, string outputPath,
    System.Threading.CancellationToken ct);

/// <summary>
/// Everything an update of the open mod needs, deep-copied from the session on the render thread:
/// every replaced resource, and the mod's meta.json as it was then (for laying out where files go —
/// the update reads meta.json again right before it writes it).
/// </summary>
internal sealed class WriteRequest
{
    /// <summary>The mod folder written into.</summary>
    public required string ModPath         { get; init; }

    /// <summary>Its folder name under Penumbra's mod directory, which is how Penumbra knows it.</summary>
    public required string ModFolderName   { get; init; }

    /// <summary>A copy of the mod's meta.json as it was when the update was asked for.</summary>
    public required PenumbraModMeta Meta   { get; init; }

    /// <summary>What the mod was last updated with, as the session knew it; the update reads the sidecar's own copy first.</summary>
    public required WriteLedger PreviousLedger { get; init; }

    /// <summary>The sidecar as it was, written whole only when the one on disk cannot be read.</summary>
    public required StudioProject Sidecar     { get; init; }

    /// <summary>Whether this plugin made the folder; otherwise meta.json is backed up before its first rewrite.</summary>
    public bool CreatedHere { get; init; }

    /// <summary>Resources replaced or added from local files.</summary>
    public List<ResourceReplacement> Replacements { get; init; } = new();

    /// <summary>
    /// The game's own files, for what starts from them: a material made from the game's, a game
    /// model whose materials are reassigned. Read on the worker, the way the resource tree does.
    /// </summary>
    public IVanillaFiles? Vanilla { get; init; }

    public required string PluginDirectory { get; init; }

    /// <summary>
    /// Where converted textures are kept between updates, so the same images are not compressed
    /// again every time (see <see cref="TextureCache"/>). Empty disables the cache.
    /// </summary>
    public string CacheDirectory { get; init; } = string.Empty;

    /// <summary>
    /// Who converts the textures. Null falls back to the encoder bundled with this plugin, which is
    /// managed code and far slower at BC7 than the native one Penumbra offers.
    /// </summary>
    public TextureConversion? ConvertTexture { get; init; }

    /// <summary>Penumbra's conversion for Auto, which encodes and checks a texture before it picks the format. Null leaves Auto textures uncompressed.</summary>
    public InstantEdit.Services.TextureCompression.TexConvert? CheckTexture { get; init; }

    /// <summary>The user's answers for files changed both here and elsewhere, by <see cref="WriteConflict.Key"/>.</summary>
    public IReadOnlyDictionary<string, ConflictChoice> Resolutions { get; init; } = new Dictionary<string, ConflictChoice>();

    /// <summary>The set-up revision this was taken at, so the session knows what the mod has afterwards.</summary>
    public int Revision { get; init; }

    /// <summary>Every local file the replacements are made from, to fingerprint (see <see cref="SourceFingerprint"/>).</summary>
    public IReadOnlyList<string> SourcePaths { get; init; } = Array.Empty<string>();
}

/// <summary>An immutable progress snapshot; the worker swaps in a new one per step.</summary>
internal sealed record WriteProgress(int Done, int Total, string Current)
{
    public float Fraction => Total <= 0 ? 0f : Math.Clamp(Done / (float)Total, 0f, 1f);
}

/// <summary>The result of one update of the mod.</summary>
internal sealed class WriteReport
{
    public required string ModName    { get; init; }
    public required string ModPath    { get; init; }
    public required string FolderName { get; init; }

    /// <summary>The set-up revision the update wrote.</summary>
    public int Revision { get; init; }

    /// <summary>The fingerprint of the source files when the update read them.</summary>
    public string SourceFingerprint { get; set; } = string.Empty;

    public DateTime FinishedAt { get; set; } = DateTime.UtcNow;

    public List<WriteEntry> Entries { get; } = new();

    /// <summary>Files changed elsewhere that this update left alone, waiting for the user.</summary>
    public List<WriteConflict> Conflicts { get; } = new();

    /// <summary>Files the replacements make that the mod already had as they make them.</summary>
    public int Unchanged      { get; set; }

    /// <summary>Files whose bytes on disk actually changed.</summary>
    public int FilesWritten   { get; set; }

    /// <summary>Whether meta.json was rewritten.</summary>
    public bool MetaWritten   { get; set; }

    /// <summary>Entries an earlier update wrote that this one took back out.</summary>
    public int Retracted      { get; set; }

    /// <summary>Files an earlier update wrote that nothing used any more, deleted.</summary>
    public int Deleted        { get; set; }

    public TimeSpan Duration  { get; set; }

    /// <summary>Stopped before anything in the mod was changed.</summary>
    public bool     Cancelled { get; set; }

    /// <summary>Set when the update stopped on an exception rather than a per-file failure.</summary>
    public string? FatalError { get; set; }

    /// <summary>What the mod was updated with, as written into the sidecar.</summary>
    public WriteLedger? NewLedger { get; set; }

    /// <summary>Filled in on the render thread afterwards: Penumbra's answer to reading the mod again.</summary>
    public PenumbraApiEc? ReloadResult { get; set; }

    public int Written => Entries.Count(e => e.Outcome is WriteOutcome.Ok or WriteOutcome.Merged);
    public int Skipped => Entries.Count(e => e.Outcome == WriteOutcome.Skipped);
    public int Failed  => Entries.Count(e => e.Outcome == WriteOutcome.Failed);

    /// <summary>Whether anything in the mod folder changed, so Penumbra should read the mod again.</summary>
    public bool ChangedMod => FilesWritten > 0 || MetaWritten || Deleted > 0;

    public bool Succeeded => FatalError == null && !Cancelled && Failed == 0 && Conflicts.Count == 0;

    public void Add(WriteOutcome outcome, string label, string? gamePath = null, string? detail = null, Target? source = null)
        => Entries.Add(new WriteEntry(outcome, label, gamePath, detail, source));

    /// <summary>One line on what the update did.</summary>
    public string Summary()
    {
        if (FatalError != null)
            return $"The mod could not be updated: {FatalError}";
        if (Cancelled)
            return "Update stopped before anything was written.";

        var parts = new List<string>();
        int written = Written;
        parts.Add(written > 0 ? $"{written} file(s) written" : MetaWritten ? "meta.json updated" : "nothing new to write");
        if (Conflicts.Count > 0) parts.Add($"{Conflicts.Count} changed elsewhere, left as they are");
        if (Failed        > 0) parts.Add($"{Failed} failed");
        if (Skipped       > 0) parts.Add($"{Skipped} skipped");
        if (Retracted     > 0) parts.Add($"{Retracted} earlier entr{(Retracted == 1 ? "y" : "ies")} taken back out");
        if (Deleted       > 0) parts.Add($"{Deleted} unused file(s) removed");
        var text = string.Join(", ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..] + $" · {Duration.TotalSeconds:0.0}s";
    }

    /// <summary>The whole report as plain text, for the copy button and the log.</summary>
    public string ToClipboardText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"XIV Instant Edit update — {ModName}");
        sb.AppendLine($"Folder: {ModPath}");
        sb.AppendLine(Summary());
        if (Unchanged > 0)
            sb.AppendLine($"{Unchanged} file(s) already as set up.");
        if (ReloadResult != null)
            sb.AppendLine(ReloadResult is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged
                ? "Penumbra read the mod again."
                : $"Penumbra did not read it again ({ReloadResult}) — reload the mod in Penumbra.");
        sb.AppendLine();

        foreach (var conflict in Conflicts)
            sb.AppendLine($"changed elsewhere  {conflict.GamePath}  — {conflict.Reason}");
        if (Conflicts.Count > 0)
            sb.AppendLine();

        foreach (var e in Entries)
        {
            var tag = e.Outcome switch
            {
                WriteOutcome.Ok      => "written",
                WriteOutcome.Merged  => "merged ",
                WriteOutcome.Kept    => "kept   ",
                WriteOutcome.Skipped => "skipped",
                _                    => "FAILED ",
            };
            sb.Append("  ").Append(tag).Append("  ").Append(e.GamePath ?? e.Label);
            if (!string.IsNullOrEmpty(e.Detail))
                sb.Append("  — ").Append(e.Detail);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
