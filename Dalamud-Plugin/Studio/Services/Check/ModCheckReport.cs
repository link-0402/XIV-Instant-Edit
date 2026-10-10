using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using InstantEdit.Studio.Services.Project;
using Newtonsoft.Json.Linq;

namespace InstantEdit.Studio.Services.Check;

internal enum CheckCategory
{
    /// <summary>The model, material and texture chains: what the game fails to load.</summary>
    Materials,
    Variants,
    Races,
    Dependencies,
    Structure,
    Leftovers,
}

internal enum RepairConfidence
{
    High,
    Medium,
    Low,
}

/// <summary>Where a requested game path is served from, for the option that asks for it.</summary>
internal enum ResolutionSource
{
    /// <summary>A file of this mod, in the option or the default files (or through one of their file swaps).</summary>
    ModFile,

    /// <summary>Every option of a single-choice group redirects it, so it is always there.</summary>
    AlwaysProvided,
    Vanilla,

    /// <summary>Not in this mod or the game, but another mod in the player's collection provides it.</summary>
    External,

    /// <summary>Only there while another option of this mod is on.</summary>
    Conditional,

    /// <summary>Only in options that can never be on together with the one asking.</summary>
    Exclusive,
    Unresolved,
}

internal sealed record PathResolution(ResolutionSource Source, string RequestedPath, string? RelPath = null, OptionRef? Option = null,
    string? Detail = null, IReadOnlyList<string>? Providers = null)
{
    public bool Works => Source is ResolutionSource.ModFile or ResolutionSource.AlwaysProvided or ResolutionSource.Vanilla or ResolutionSource.External;

    public bool IsModFile => RelPath is not null && Source is ResolutionSource.ModFile or ResolutionSource.AlwaysProvided;

    /// <summary>How bad it is when this resolution is what the game gets; null when it works.</summary>
    public Severity? Severity => Source switch
    {
        ResolutionSource.Conditional => Services.Severity.Warning,
        ResolutionSource.Exclusive or ResolutionSource.Unresolved => Services.Severity.Error,
        _ => null,
    };

    public string Describe() => Source switch
    {
        ResolutionSource.ModFile        => $"This mod: {RelPath}{Suffix()}",
        ResolutionSource.AlwaysProvided => $"Every option of a group provides it (for example {RelPath}){Suffix()}",
        ResolutionSource.Vanilla        => $"Game file{Suffix()}",
        ResolutionSource.External       => $"Another mod in your collection: {Detail}",
        ResolutionSource.Conditional    => $"Only while on: {string.Join(", ", Providers ?? Array.Empty<string>())}",
        ResolutionSource.Exclusive      => $"Only in options that can't be on together: {string.Join(", ", Providers ?? Array.Empty<string>())}",
        _                               => $"Missing: {Detail ?? "not in this mod or the game"}",
    };

    private string Suffix() => Detail is null ? string.Empty : $" ({Detail})";
}

/// <summary>
/// One thing the check found. <see cref="GamePath"/> and <see cref="Option"/> say which file it is
/// about, when it is about one; the view finds the Resources node from them.
/// </summary>
internal sealed record CheckFinding(
    string Key,
    string Code,
    CheckCategory Category,
    Severity Severity,
    string Title,
    string Detail,
    string? GamePath = null,
    OptionRef? Option = null,
    string? Where = null,
    string? Manual = null,
    string? Cluster = null)
{
    /// <summary>The repairs offered for it, filled in by the planner.</summary>
    public List<string> RepairIds { get; } = new();
}

// ── Repairs ──────────────────────────────────────────────────────────────────

/// <summary>One change to the mod's meta.json, in one option (or the default files).</summary>
internal abstract record RepairEdit(OptionRef Option)
{
    public abstract string Describe();
}

internal sealed record AddFileEdit(OptionRef Option, string GamePath, string RelPath) : RepairEdit(Option)
{
    public override string Describe() => $"Redirect {GamePath} to {RelPath}";
}

internal sealed record RemoveFileEdit(OptionRef Option, string GamePath, string ExpectedRelPath) : RepairEdit(Option)
{
    public override string Describe() => $"Remove the redirection of {GamePath}";
}

internal sealed record RekeyFileEdit(OptionRef Option, string OldGamePath, string NewGamePath) : RepairEdit(Option)
{
    public override string Describe() => $"Change {OldGamePath} to {NewGamePath}";
}

internal sealed record RetargetFileEdit(OptionRef Option, string GamePath, string OldRelPath, string NewRelPath) : RepairEdit(Option)
{
    public override string Describe() => $"Point {GamePath} at {NewRelPath}";
}

internal sealed record AddManipulationEdit(OptionRef Option, JObject Manipulation, string Description, string? ReplacesIdentity = null) : RepairEdit(Option)
{
    public override string Describe() => ReplacesIdentity is null ? $"Add {Description}" : $"Set {Description}";
}

internal sealed record RemoveManipulationEdit(OptionRef Option, string Identity, string ExpectedJson, string Description) : RepairEdit(Option)
{
    public override string Describe() => $"Remove {Description}";
}

/// <summary>A file of the mod to delete, as it was when the check saw it.</summary>
internal sealed record FileDeletion(string RelPath, long Size, DateTime LastWriteUtc);

internal sealed record RepairCandidate(string Label, RepairConfidence Confidence, string Reason, IReadOnlyList<RepairEdit> Edits);

internal sealed record Repair(
    string Id,
    string FindingKey,
    CheckCategory Category,
    string Title,
    string Explanation,
    RepairConfidence Confidence,
    bool Preselected,
    IReadOnlyList<RepairEdit> Edits,
    IReadOnlyList<FileDeletion> Deletions,
    IReadOnlyList<string> DependsOn,
    string? ExclusiveGroup = null,
    IReadOnlyList<RepairCandidate>? Candidates = null,
    long BytesFreed = 0,
    string? Cluster = null)
{
    /// <summary>The edits of the chosen candidate, or the repair's own when it has no candidates.</summary>
    public IReadOnlyList<RepairEdit> EditsFor(string? choice)
        => Candidates is { Count: > 0 } candidates
            ? (candidates.FirstOrDefault(candidate => candidate.Label == choice) ?? candidates[0]).Edits
            : Edits;
}

// ── Tables ───────────────────────────────────────────────────────────────────

/// <summary>One IMC variant of a model: the items that use it, its material set and attribute mask, and how it fares. Null status is fine.</summary>
internal sealed record VariantRow(int Variant, IReadOnlyList<string> Items, byte MaterialSet, ushort AttributeMask, Severity? Status, string Summary);

internal sealed record VariantTable(string ModelPath, OptionRef Option, string Where, IReadOnlyList<VariantRow> Rows, string? Note);

/// <summary>One playable race (Penumbra's code, 101 for c0101): the model it loads for the set, and whether that is this mod's.</summary>
internal sealed record RaceRow(ushort Race, ushort Loaded, bool UsesModModel, bool ViaFallback, bool IsPlayer, string? SkinNote, string? RepairId);

internal sealed record RaceTable(string Label, IReadOnlyList<ushort> Shipped, IReadOnlyList<RaceRow> Rows);

/// <summary>meta.json as a check read it, so a repair can tell whether the mod changed since.</summary>
internal sealed record MetaFingerprint(long Size, long LastWriteTicks, string Sha256)
{
    /// <summary>The fingerprint of a mod folder's meta.json, or null when the folder or the file is gone.</summary>
    public static MetaFingerprint? Of(string folder)
    {
        try
        {
            var path = Path.Combine(folder, PenumbraModMeta.FileName);
            if (!File.Exists(path))
                return null;
            var bytes = File.ReadAllBytes(path);
            return new MetaFingerprint(bytes.LongLength, File.GetLastWriteTimeUtc(path).Ticks, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal sealed record CheckProgress(string Phase, int Current, int Total);

internal sealed record ModCheckReport(
    string Folder,
    MetaFingerprint? Fingerprint,
    DateTime At,
    IReadOnlyList<CheckFinding> Findings,
    IReadOnlyList<Repair> Repairs,
    IReadOnlyList<VariantTable> Variants,
    IReadOnlyList<RaceTable> Races,
    CollectionContext? Collection,
    ushort? PlayerRace,
    IReadOnlyList<string> Notes)
{
    public int Count(Severity severity) => Findings.Count(finding => finding.Severity == severity);

    public Repair? Find(string id) => Repairs.FirstOrDefault(repair => repair.Id == id);
}
