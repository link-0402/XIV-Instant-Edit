using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InstantEdit.Services;
using InstantEdit.Services.NeckSeam;
using InstantEdit.Services.TextureCompression;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Models.Resources;
using InstantEdit.Studio.Services.Backups;
using InstantEdit.Studio.Services.Check;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Ui;
using StudioResourceKind = InstantEdit.Studio.Models.Resources.ResourceKind;
using TextureRole = InstantEdit.Ui.TextureRole;

namespace InstantEdit.Studio.Services.CleanUp;

internal enum TextureFormatChoice
{
    /// <summary>BC5 for index maps, BC7 for the rest; a texture of one colour may become 32 × 32.</summary>
    Auto,

    /// <summary>The format it has (only resized).</summary>
    Keep,
    Bc1,
    Bc3,
    Bc4,
    Bc5,
    Bc7,
}

/// <summary>One texture file of the mod, with every material use the mod's materials give it.</summary>
internal sealed record TextureCandidate(string RelPath, string DiskPath, TexInfo Info, IReadOnlyList<TextureUse> Uses, bool Selectable, bool Preselected,
    string? Note, IReadOnlyList<string> GamePaths);

internal sealed record TextureJob(TextureCandidate Candidate, TextureFormatChoice Format, int MaxSide);

internal sealed record CleanUpResult(int Changed, int Kept, IReadOnlyList<string> Problems, int Pending, long BytesSaved);

/// <summary>
/// A mod's textures, resized and compressed: what can be changed and what starts ticked, and the
/// changes themselves, each original backed up and every result checked before it replaces the file.
/// Mod Optimizer's texture tools, on Instant Edit's encode-and-check (<see cref="VerifiedEncode"/>).
/// Dalamud-free; Penumbra comes in through <see cref="TexConvert"/> and <see cref="IModReloader"/>.
/// </summary>
internal static class TextureCleanUp
{
    /// <summary>The smallest side at which a texture starts ticked.</summary>
    public const int PreselectSide = 4096;

    /// <summary>Why a texture can't be picked, or null when it can.</summary>
    public static string? WhyNotSelectable(TexInfo info)
    {
        if (!info.Is2D)
            return "Only flat 2D textures can be changed (not arrays, cubes or volumes).";
        if (info.Format == TextureCost.Bc6H || ((info.Format >> 12) & 0xF) == 2)
            return "HDR and floating-point textures can't be changed.";
        if (TextureCost.BitsPerPixel(info.Format) is null)
            return $"Its format ({TextureCost.FormatName(info.Format)}) is unknown.";
        if (Math.Max(info.Width, info.Height) > TextureFiles.MaxDimension)
            return "It is larger than textures can be.";
        return null;
    }

    /// <summary>The mod's textures in a resource graph, each file once, with the uses its materials give it.</summary>
    public static IReadOnlyList<TextureCandidate> Scan(ResourceGraph graph, Func<ResourceNode, byte[]?> readFile)
    {
        var materials = new Dictionary<string, SkinMaterial?>(StringComparer.OrdinalIgnoreCase);
        SkinMaterial? Material(ResourceNode node)
        {
            if (materials.TryGetValue(node.Id, out var known))
                return known;
            SkinMaterial? material = null;
            try
            {
                if (readFile(node) is { } bytes)
                    material = SkinMaterial.Read(bytes);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException or EndOfStreamException) { }
            return materials[node.Id] = material;
        }

        var result = new List<TextureCandidate>();
        foreach (var group in graph.Nodes.Values
                     .Where(n => n.Kind == StudioResourceKind.Texture && n.Origin == OriginKind.ModFile && n.DiskPath.Length > 0)
                     .GroupBy(n => n.DiskPath, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            if (ReadInfo(first.DiskPath) is not { } info)
                continue;
            var uses = new List<TextureUse>();
            foreach (var node in group)
            {
                var parents = graph.ParentsOf(node).Where(p => p.Kind == StudioResourceKind.Material).ToArray();
                var fallback = () => TextureRoleClassifier.Classify(InstantEdit.Ui.ResourceKinds.Texture, null, node.GamePath, node.DiskPath);
                if (parents.Length == 0)
                    uses.Add(new TextureUse { Role = fallback() });
                foreach (var parent in parents)
                    foreach (var use in CompressionCapture.UsesIn(Material(parent), parent.GamePath, node.GamePath, fallback))
                        if (!uses.Contains(use))
                            uses.Add(use);
            }

            var why = WhyNotSelectable(info);
            var normal = uses.Any(u => u.Role == TextureRole.Normal);
            var index = uses.Any(u => u.Role == TextureRole.Index);
            var note = why
                ?? (normal ? "A normal map: compressing it can change cut-outs, so it starts unticked."
                : index ? "An index map: it starts unticked."
                : info.IsBlockCompressed ? "Already compressed: it starts unticked."
                : Math.Max(info.Width, info.Height) < PreselectSide ? $"Smaller than {PreselectSide}, so it starts unticked."
                : null);
            var preselected = why is null && !normal && !index && TextureCost.IsCompressibleColour(info.Format) && Math.Max(info.Width, info.Height) >= PreselectSide;
            result.Add(new TextureCandidate(first.RelPath.Replace('\\', '/'), first.DiskPath, info, uses, why is null, preselected, note,
                group.Select(n => n.GamePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        }
        return result.OrderBy(c => c.RelPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static TexInfo? ReadInfo(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[TextureCost.HeaderSize];
            return stream.Read(head, 0, head.Length) == head.Length ? TextureCost.Read(head) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Resizes and compresses the given textures, one after another: each original is backed up, the
    /// result checked, and only a passing result replaces the file. Cancelling keeps what is done.
    /// Penumbra reads the mod again once at the end.
    /// </summary>
    public static async Task<CleanUpResult> ApplyAsync(ModProject project, IReadOnlyList<TextureJob> jobs, TexConvert convert, ModBackupStore backups,
        string cacheRoot, IModReloader penumbra, StudioSettings settings, IProgress<CheckProgress>? progress, CancellationToken token)
    {
        var folder = project.FolderPath;
        int changed = 0, kept = 0;
        long saved = 0;
        var problems = new List<string>();
        using var operation = backups.Begin(folder, project.Meta.Identifier ?? Guid.Empty, "Textures", cacheRoot);
        var work = Path.Combine(Path.GetTempPath(), "InstantEdit", "texture-cleanup", Guid.NewGuid().ToString("N"));
        try
        {
            for (var i = 0; i < jobs.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var job = jobs[i];
                var relPath = job.Candidate.RelPath;
                var name = Path.GetFileName(relPath);
                progress?.Report(new CheckProgress($"Encoding {name}", i, jobs.Count));
                var info = job.Candidate.Info;
                var maxSide = job.MaxSide < Math.Max(info.Width, info.Height) ? job.MaxSide : (int?)null;
                if (job.Format == TextureFormatChoice.Keep && maxSide is null)
                    continue;
                uint? target = job.Format switch
                {
                    TextureFormatChoice.Keep => info.Format,
                    TextureFormatChoice.Bc1  => TextureCost.Bc1,
                    TextureFormatChoice.Bc3  => TextureCost.Bc3,
                    TextureFormatChoice.Bc4  => TextureCost.Bc4,
                    TextureFormatChoice.Bc5  => TextureCost.Bc5,
                    TextureFormatChoice.Bc7  => TextureCost.Bc7,
                    _                        => null,
                };

                string full;
                byte[] original;
                try
                {
                    full = ModPaths.Resolve(folder, relPath);
                    original = await File.ReadAllBytesAsync(full, token).ConfigureAwait(false);
                    operation.Keep(relPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    problems.Add($"{name}: left as it is, it couldn't be backed up ({ex.Message}).");
                    continue;
                }

                var jobWork = Path.Combine(work, i.ToString());
                Directory.CreateDirectory(jobWork);
                VerifiedEncodeResult result;
                try
                {
                    result = await VerifiedEncode.RunAsync(new VerifiedEncodeRequest(TextureFiles.NormalizeMipOffsets(original), info, job.Candidate.Uses, target,
                        maxSide, AllowSingleColor: job.Format == TextureFormatChoice.Auto), convert, jobWork, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    operation.Forget(relPath);
                    throw;
                }
                catch (Exception ex)
                {
                    operation.Forget(relPath);
                    problems.Add($"{name}: left as it is, Penumbra couldn't encode it ({ex.Message}).");
                    continue;
                }

                if (result.Outcome != VerifiedEncodeOutcome.Encoded)
                {
                    operation.Forget(relPath);
                    if (result.Outcome == VerifiedEncodeOutcome.FailedCheck)
                    {
                        kept++;
                        problems.Add($"{name}: left as it is, {result.Check!.Problem}.");
                    }
                    continue;
                }

                var encoded = result.Encoded!;
                lock (ModFiles.Gate)
                {
                    var temp = full + ".instant-edit-tmp";
                    File.WriteAllBytes(temp, encoded);
                    File.Move(temp, full, overwrite: true);
                }
                changed++;
                saved += original.LongLength - encoded.LongLength;
            }
        }
        finally
        {
            operation.Entry.Label = changed == 1 ? "Changed 1 texture" : $"Changed {changed} textures";
            operation.Commit();
            if (changed > 0)
                penumbra.Reload(project.FolderName);
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new CleanUpResult(changed, kept, problems, 0, saved);
    }
}
