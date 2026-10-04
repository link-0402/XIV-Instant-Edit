using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using InstantEdit.Models;
using Lumina.Data.Files;

namespace InstantEdit.Services.Animations;

internal sealed record AnimationReference(string Path, string Kind);
internal sealed record AnimationReferences(ImmutableArray<AnimationReference> References, ImmutableArray<string> Problems);
/// <summary>
/// One entry of a timeline (TMLB) as the walker found it: which of the file's timelines holds it,
/// its kind, where it starts and its size, and the bounds of its timeline, which its pointers stay inside.
/// </summary>
internal readonly record struct AnimationTimelineEntry(int Timeline, string Magic, int Position, int Size, int Start, int End);
/// <summary>The textures and sounds an AVFX names, each once, in file order.</summary>
internal sealed record AnimationVfxPaths(ImmutableArray<string> Textures, ImmutableArray<string> Sounds, ImmutableArray<string> Problems);

/// <summary>Bounded, format-aware readers for the files an animation's timelines name.</summary>
internal static class AnimationDependencies
{
    private sealed record Layout(int Size, string Name);
    private static readonly Dictionary<string, Layout> Layouts = LoadLayouts();
    private static Dictionary<string, Layout> LoadLayouts()
    {
        using var stream = typeof(AnimationDependencies).Assembly.GetManifestResourceStream("InstantEdit.Services.Animations.TmbLayouts.json")
            ?? throw new InvalidOperationException("Animation timeline layouts are missing.");
        return JsonSerializer.Deserialize<Dictionary<string, Layout>>(stream)!;
    }
    public static bool SafeGamePath(string path) => path.Length is > 0 and < 512 && !Path.IsPathRooted(path) &&
        !path.Contains('\\') && !path.Contains(':') && !path.Contains('\0') &&
        path.Split('/').All(p => p is not ("" or "." or "..")) &&
        path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '.');

    public static AnimationReferences Read(string gamePath, byte[] bytes)
    {
        if (bytes.Length > AnimationPap.MaxFileSize) throw new InvalidDataException("Dependency exceeds the file size limit.");
        var references = new List<AnimationReference>(); var problems = new List<string>();
        switch (Path.GetExtension(gamePath).ToLowerInvariant())
        {
            case ".pap": Walk(true, bytes, references, problems, null); break;
            case ".tmb": Walk(false, bytes, references, problems, null); break;
            case ".avfx": ReadVfx(bytes, (path, _) => references.Add(new AnimationReference(path, "resource")), problems); break;
            case ".mtrl":
                var material = MaterialPreviewBundleBuilder.LooseLuminaFile.Load<MtrlFile>(bytes);
                foreach (var texture in material.TextureOffsets)
                    references.Add(new AnimationReference(PathRules.Dx11TexturePath(
                        PathRules.ReadNullTerminated(material.Strings, texture.Offset), texture.Flags), "resource"));
                break;
            case ".mdl":
                foreach (var materialName in MaterialPreviewBundleBuilder.ReadModelMaterials(bytes))
                    references.Add(new AnimationReference(materialName, "material"));
                break;
            case ".tex": case ".scd": case ".sklb": case ".skp": case ".eid": case ".phyb": break;
            default: problems.Add($"No dependency reader exists for {Path.GetExtension(gamePath)}."); break;
        }
        return new AnimationReferences(references.Distinct().ToImmutableArray(), problems.Distinct().ToImmutableArray());
    }

    /// <summary>
    /// Every entry of every timeline in a PAP (its embedded timelines, in clip order) or a TMB,
    /// walked with the same bounds checks as <see cref="Read"/>. Malformed timelines throw.
    /// </summary>
    internal static ImmutableArray<AnimationTimelineEntry> TimelineEntries(string gamePath, byte[] bytes)
    {
        if (bytes.Length > AnimationPap.MaxFileSize) throw new InvalidDataException("Dependency exceeds the file size limit.");
        var entries = new List<AnimationTimelineEntry>();
        switch (Path.GetExtension(gamePath).ToLowerInvariant())
        {
            case ".pap": Walk(true, bytes, [], [], entries); break;
            case ".tmb": Walk(false, bytes, [], [], entries); break;
            default: throw new InvalidDataException("Only PAP and TMB files hold timelines.");
        }
        return [.. entries];
    }

    /// <summary>
    /// The string an entry's field points at; pointers count from the entry's start + 8. Null when
    /// the pointer is zero or the field lies outside the entry.
    /// </summary>
    internal static string? EntryString(byte[] bytes, AnimationTimelineEntry entry, int field)
    {
        if (field < 8 || field > entry.Size - 4) return null;
        var displacement = AnimationPap.ReadInt(bytes, entry.Position + field);
        if (displacement == 0) return null;
        var position = (long)entry.Position + 8 + displacement;
        if (position < entry.Start || position >= entry.End) throw new InvalidDataException($"Invalid {entry.Magic} string offset.");
        return ReadString(bytes, (int)position, entry.End);
    }

    private static void Walk(bool pap, byte[] bytes, List<AnimationReference> references, List<string> problems,
        List<AnimationTimelineEntry>? entries)
    {
        if (!pap)
        {
            if (ReadTimeline(bytes, 0, 0, references, problems, entries) != bytes.Length) problems.Add("Unrecognized bytes after the timeline.");
            return;
        }
        var file = new AnimationPap(bytes);
        var offset = file.TimelineOffset;
        for (var i = 0; i < file.Entries.Length; i++)
        {
            var count = ReadTimeline(bytes, offset, i, references, problems, entries);
            offset = checked(offset + count);
            if (i + 1 < file.Entries.Length) offset += (file.TimelineOffset - offset) & 3;
        }
        if (offset != bytes.Length) problems.Add("Unrecognized bytes after PAP timelines.");
    }

    private static int ReadTimeline(byte[] bytes, int start, int timeline, List<AnimationReference> references, List<string> problems,
        List<AnimationTimelineEntry>? found)
    {
        if (start < 0 || start > bytes.Length - 12 || !bytes.AsSpan(start, 4).SequenceEqual("TMLB"u8))
            throw new InvalidDataException("Invalid animation timeline header.");
        var length = AnimationPap.ReadInt(bytes, start + 4); var entries = AnimationPap.ReadInt(bytes, start + 8);
        if (length < 12 || length > bytes.Length - start || entries is < 0 or > 65536) throw new InvalidDataException("Invalid animation timeline size.");
        var end = start + length; var cursor = start + 12;
        for (var i = 0; i < entries; i++)
        {
            if (cursor > end - 8) throw new InvalidDataException("Truncated animation timeline entry.");
            var magic = Encoding.ASCII.GetString(bytes, cursor, 4); var size = AnimationPap.ReadInt(bytes, cursor + 4);
            if (size < 8 || size > end - cursor) throw new InvalidDataException($"Invalid {magic} timeline entry size.");
            // Only the entries whose path fields are read below name a file; VFXEditor's
            // TMB definitions agree that no other entry carries a string. Everything else -
            // footsteps, voice lines, BGM, terrain effects, summons - has the game pick a
            // resource by numeric ID at runtime. There is no dependency to follow, and every
            // edit carries those IDs over untouched, so they select exactly what they did
            // before. A new mod holds only the baked clips, never these resources.
            if (Layouts.TryGetValue(magic, out var layout))
            {
                if (size != layout.Size) problems.Add($"Unsupported {magic} ({layout.Name}) entry size {size}.");
            }
            else if (magic is not ("TMDH" or "TMPP" or "TMAL" or "TMAC" or "TMTR" or "TMFC"))
                problems.Add($"Unknown timeline entry {magic}.");
            var entry = new AnimationTimelineEntry(timeline, magic, cursor, size, start, end);
            found?.Add(entry);
            if (magic == "TMPP")
            {
                // The face library a timeline loads (nonresident/{name}.pap of the player's face). It is
                // reported only; a broken one is a problem, not a reason to stop reading the timeline.
                try
                {
                    if (EntryString(bytes, entry, 8) is { Length: > 0 } pack) references.Add(new AnimationReference(pack, "facepack"));
                }
                catch (InvalidDataException) { problems.Add("Invalid TMPP face library name."); }
            }
            var field = magic switch { "C002" => 24, "C009" => 20, "C010" => 32, "C012" or "C063" or "C173" => 20, _ => -1 };
            if (field >= 0 && EntryString(bytes, entry, field) is { Length: > 0 } path)
                references.Add(new AnimationReference(path,
                    magic == "C002" ? "timeline" : magic is "C009" or "C010" ? "animation" : "resource"));
            cursor += size;
        }
        return length;
    }
    private static string ReadString(byte[] bytes, int start, int end)
    {
        var span = bytes.AsSpan(start, Math.Min(512, end - start)); var zero = span.IndexOf((byte)0);
        if (zero < 0) throw new InvalidDataException("Unterminated animation dependency path.");
        return Encoding.UTF8.GetString(span[..zero]);
    }
    /// <summary>
    /// The texture (<c>Tex</c>) and emitter sound (<c>Emit/SdNm</c>) paths an AVFX names, for
    /// packaging an effect with what it needs. Malformed files throw; unknown chunks are problems.
    /// </summary>
    internal static AnimationVfxPaths ReadVfxPaths(byte[] bytes)
    {
        if (bytes.Length > AnimationPap.MaxFileSize) throw new InvalidDataException("Dependency exceeds the file size limit.");
        var textures = new List<string>(); var sounds = new List<string>(); var problems = new List<string>();
        ReadVfx(bytes, (path, sound) => { if (path.Length > 0) (sound ? sounds : textures).Add(path); }, problems);
        return new AnimationVfxPaths([.. textures.Distinct(StringComparer.OrdinalIgnoreCase)],
            [.. sounds.Distinct(StringComparer.OrdinalIgnoreCase)], [.. problems.Distinct()]);
    }

    /// <summary>Walks an AVFX, reporting each texture path (sound false) and emitter sound path (sound true) in file order.</summary>
    private static void ReadVfx(byte[] bytes, Action<string, bool> found, List<string> problems)
    {
        if (bytes.Length < 8 || FourCc(bytes, 0) != "AVFX") throw new InvalidDataException("Invalid AVFX header.");
        var length = AnimationPap.ReadInt(bytes, 4);
        if (length < 0 || length != bytes.Length - 8) throw new InvalidDataException("Invalid AVFX length.");
        var rootFields = new HashSet<string>(("Ver bDFP bFG bTS bASH bCBC bCul CBPx CBPy CBPz CBSx CBSy CBSz ZBMs ZBMd bCmS bFEL bOSE bOSt " +
            "NCB NCE FCB FCE SPFR SKO DwLy DwOT DLST PL1S PL2S RvPx RvPy RvPz RvRx RvRy RvRz RvSx RvSy RvSz RvR RvG RvB " +
            "AFXe AFXi AFXo AFYe AFYi AFYo AFZe AFZi AFZo bGFE GFIM bLTS bAGS APri DPri bSAB bSBV SBVa bSSV SSVa SPHP " +
            "ScCn TlCn EmCn PrCn EfCn BdCn TxCn MdCn").Split(' '), StringComparer.Ordinal);
        foreach (var (name, start, size) in Chunks(bytes, 8, 8 + length))
        {
            if (name == "Tex") found(ReadString(bytes, start, start + size), false);
            else if (name == "Emit")
            {
                foreach (var (field, position, count) in Chunks(bytes, start, start + size))
                    if (field == "SdNm")
                    {
                        var path = ReadString(bytes, position, position + count);
                        if (path.Length > 0) found(path, true);
                    }
            }
            else if (name is not ("Schd" or "TmLn" or "Ptcl" or "Efct" or "Bind" or "Modl") && !rootFields.Contains(name))
                problems.Add($"Unknown AVFX root chunk '{name}' prevents establishing dependency completeness.");
        }
    }
    // Names are stored reversed; the game pads three-letter names with a NUL after the reversed
    // letters ("xeT\0" for Tex), so the NUL comes first once reversed.
    private static string FourCc(byte[] bytes, int offset) => new string(Encoding.ASCII.GetString(bytes, offset, 4).Reverse().ToArray()).Trim('\0');
    private static IEnumerable<(string Name, int Start, int Size)> Chunks(byte[] bytes, int start, int end)
    {
        for (var p = start; p < end;)
        {
            if (p > end - 8) throw new InvalidDataException("Truncated VFX chunk.");
            var size = AnimationPap.ReadInt(bytes, p + 4);
            if (size < 0 || size > end - p - 8) throw new InvalidDataException("Invalid VFX chunk size.");
            yield return (FourCc(bytes, p), p + 8, size);
            p = checked(p + 8 + size + ((-size) & 3));
            if (p > end) throw new InvalidDataException("Invalid VFX padding.");
        }
    }
}
