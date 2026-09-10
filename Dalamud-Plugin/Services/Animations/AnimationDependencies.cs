using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using InstantEdit.Models;
using Lumina.Data.Files;

namespace InstantEdit.Services.Animations;

internal sealed record AnimationReference(string Path, string Kind);
internal sealed record AnimationReferences(ImmutableArray<AnimationReference> References, ImmutableArray<string> Problems);

/// <summary>Bounded, format-aware readers. Unknown timeline constructs cannot silently produce an incomplete mod.</summary>
internal static class AnimationDependencies
{
    private sealed record Layout(int Size, string Name, bool Dynamic);
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
            case ".pap":
                var pap = new AnimationPap(bytes);
                var offset = pap.TimelineOffset;
                for (var i = 0; i < pap.Entries.Length; i++)
                {
                    var count = ReadTimeline(bytes, offset, references, problems);
                    offset = checked(offset + count);
                    if (i + 1 < pap.Entries.Length) offset += (pap.TimelineOffset - offset) & 3;
                }
                if (offset != bytes.Length) problems.Add("Unrecognized bytes after PAP timelines.");
                break;
            case ".tmb":
                if (ReadTimeline(bytes, 0, references, problems) != bytes.Length) problems.Add("Unrecognized bytes after the timeline.");
                break;
            case ".avfx": ReadVfx(bytes, references, problems); break;
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

    private static int ReadTimeline(byte[] bytes, int start, List<AnimationReference> references, List<string> problems)
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
            if (Layouts.TryGetValue(magic, out var layout))
            {
                if (size != layout.Size) problems.Add($"Unsupported {magic} ({layout.Name}) entry size {size}.");
                if (layout.Dynamic || string.IsNullOrEmpty(layout.Name) || magic is "C013" or "C042" or "C053" or "C075" or "C143" or "C197" or "C198" or "C204" or "C230")
                    problems.Add($"{magic} ({layout.Name}) has unresolved dynamic resource selection; complete packaging cannot be established.");
            }
            else if (magic is not ("TMDH" or "TMPP" or "TMAL" or "TMAC" or "TMTR" or "TMFC"))
                problems.Add($"Unknown timeline entry {magic}.");
            var field = magic switch { "C002" => 24, "C009" => 20, "C010" => 32, "C012" or "C063" or "C173" => 20, _ => -1 };
            if (field >= 0 && field <= size - 4)
            {
                var displacement = AnimationPap.ReadInt(bytes, cursor + field);
                if (displacement != 0)
                {
                    var position = (long)cursor + 8 + displacement;
                    if (position < start || position >= end) throw new InvalidDataException($"Invalid {magic} string offset.");
                    var path = ReadString(bytes, (int)position, end);
                    if (path.Length > 0) references.Add(new AnimationReference(path,
                        magic == "C002" ? "timeline" : magic is "C009" or "C010" ? "animation" : "resource"));
                }
            }
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
    private static void ReadVfx(byte[] bytes, List<AnimationReference> references, List<string> problems)
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
            if (name == "Tex") references.Add(new AnimationReference(ReadString(bytes, start, start + size), "resource"));
            else if (name == "Emit")
            {
                foreach (var (field, position, count) in Chunks(bytes, start, start + size))
                    if (field == "SdNm")
                    {
                        var path = ReadString(bytes, position, position + count);
                        if (path.Length > 0) references.Add(new AnimationReference(path, "resource"));
                    }
            }
            else if (name is not ("Schd" or "TmLn" or "Ptcl" or "Efct" or "Bind" or "Modl") && !rootFields.Contains(name))
                problems.Add($"Unknown AVFX root chunk '{name}' prevents establishing dependency completeness.");
        }
    }
    private static string FourCc(byte[] bytes, int offset) => new string(Encoding.ASCII.GetString(bytes, offset, 4).Reverse().ToArray()).TrimEnd('\0');
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

    public static async Task<AnimationDependencyManifest> BuildAsync(IEnumerable<string> roots,
        Func<string, Task<(AnimationResource Resource, byte[] Bytes)>> read,
        Func<string, AnimationReference, Task<IReadOnlyList<string>>> resolveReference, CancellationToken token)
    {
        var pending = new Queue<string>(roots.Distinct(StringComparer.OrdinalIgnoreCase));
        var files = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var resources = ImmutableArray.CreateBuilder<AnimationResource>();
        long total = 0;
        while (pending.TryDequeue(out var path))
        {
            token.ThrowIfCancellationRequested();
            if (!SafeGamePath(path)) throw new InvalidDataException($"Invalid animation dependency path: {path}");
            if (files.ContainsKey(path)) continue;
            if (files.Count >= 4096) throw new InvalidDataException("The animation dependency graph exceeds 4096 resources.");
            var source = await read(path);
            total += source.Bytes.Length;
            if (total > 512L * 1024 * 1024) throw new InvalidDataException("The animation dependency bundle exceeds 512 MiB.");
            var parsed = Read(path, source.Bytes);
            if (!parsed.Problems.IsEmpty) throw new InvalidDataException($"Cannot package {path}: {string.Join(" ", parsed.Problems)}");
            files[path] = source.Bytes; resources.Add(source.Resource);
            foreach (var reference in parsed.References)
                foreach (var dependency in await resolveReference(path, reference)) pending.Enqueue(dependency);
        }
        return new AnimationDependencyManifest(resources.ToImmutable(), files.ToImmutable());
    }
}
