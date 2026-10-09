using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text.RegularExpressions;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>A colour an effect uses, as one swatch: its value rounded (<see cref="TimelineEffectLooks.Swatch"/>) and how many keys use it.</summary>
internal sealed record TimelineEffectColor(Vector3 Rgb, int Uses);

/// <summary>
/// What the effect editor shows of an effect (AVFX) file. The whole-effect values are the file's root
/// "revised" fields, which every effect in the mod library has; each is null when the file lacks it.
/// </summary>
/// <param name="Tint">Multiplies the colour of the whole effect (RvR, RvG, RvB).</param>
/// <param name="Size">Scales the whole effect (RvSx, RvSy, RvSz).</param>
/// <param name="Offset">Moves the whole effect (RvPx, RvPy, RvPz).</param>
/// <param name="Turn">Turns the whole effect (RvRx, RvRy, RvRz), in degrees.</param>
/// <param name="Colors">The colours its parts use, most used first.</param>
/// <param name="Glow">Whether its particles or lights have brightness for Glow to scale.</param>
/// <param name="Opacity">Whether its particles have alpha for Opacity to scale.</param>
/// <param name="LightRange">Whether it has a point light whose range Light range scales.</param>
internal sealed record TimelineEffectLook(Vector3? Tint, Vector3? Size, Vector3? Offset, Vector3? Turn,
    ImmutableArray<TimelineEffectColor> Colors, bool Glow, bool Opacity, bool LightRange);

/// <summary>
/// The effect editor's changes to one effect. The whole-effect values replace the file's (null keeps
/// it); the others scale or shift what the file has, so after a save they start from the saved copy.
/// Saving or previewing writes them into a copy of the effect (<see cref="TimelineEffectLooks.Bake"/>).
/// </summary>
internal sealed record TimelineEffectEdit
{
    public const float MaxTint = 4, MinSize = .01f, MaxSize = 20, MaxOffset = 100, MaxTurn = 360;
    public const float MaxSaturation = 2, MaxGlow = 4, MaxOpacity = 2, MaxLightRange = 4;

    public Vector3? Tint { get; init; }
    public Vector3? Size { get; init; }
    public Vector3? Offset { get; init; }
    public Vector3? Turn { get; init; }

    /// <summary>Turns every colour's hue, in degrees.</summary>
    public float Hue { get; init; }

    /// <summary>Scales every colour's saturation.</summary>
    public float Saturation { get; init; } = 1;

    /// <summary>Colours swapped for others, by swatch. A swapped colour ignores Hue and Saturation.</summary>
    public ImmutableArray<(Vector3 From, Vector3 To)> Colors { get => colors; init => colors = value.IsDefault ? [] : value; }

    private readonly ImmutableArray<(Vector3 From, Vector3 To)> colors = [];

    /// <summary>Scales the brightness of the particles and lights.</summary>
    public float Glow { get; init; } = 1;

    /// <summary>Scales the particles' alpha.</summary>
    public float Opacity { get; init; } = 1;

    /// <summary>Scales how far the point lights reach.</summary>
    public float LightRange { get; init; } = 1;

    public bool IsEmpty => Tint == null && Size == null && Offset == null && Turn == null && !Recolors &&
                           Glow == 1 && Opacity == 1 && LightRange == 1;

    /// <summary>Whether the colours of the parts change.</summary>
    public bool Recolors => Hue != 0 || Saturation != 1 || !Colors.IsEmpty;

    /// <summary>The colour the swatch <paramref name="swatch"/> was swapped for; null when it wasn't.</summary>
    public Vector3? Swapped(Vector3 swatch)
    {
        foreach (var (from, to) in Colors)
            if (from == swatch)
                return to;
        return null;
    }

    /// <summary>This edit with the swatch <paramref name="from"/> swapped for <paramref name="to"/>, or back to its own with null.</summary>
    public TimelineEffectEdit WithSwap(Vector3 from, Vector3? to)
    {
        var kept = Colors.Where(c => c.From != from);
        return this with { Colors = to is { } colour ? [.. kept, (from, colour)] : [.. kept] };
    }

    /// <summary>What <paramref name="rgb"/>, a colour of the file, becomes: its swap, else its hue and saturation changed.</summary>
    public Vector3 Recolor(Vector3 rgb) =>
        Swapped(TimelineEffectLooks.Swatch(rgb)) ?? TimelineEffectLooks.Shift(rgb, Hue, Saturation);

    public bool Equals(TimelineEffectEdit? other) =>
        other is not null && (ReferenceEquals(this, other) ||
            Tint == other.Tint && Size == other.Size && Offset == other.Offset && Turn == other.Turn && Hue == other.Hue &&
            Saturation == other.Saturation && Glow == other.Glow && Opacity == other.Opacity && LightRange == other.LightRange &&
            Colors.SequenceEqual(other.Colors));

    public override int GetHashCode() => HashCode.Combine(Tint, Size, Offset, Turn, Hue, Saturation, Glow, HashCode.Combine(Opacity, LightRange, Colors.Length));
}

/// <summary>
/// Reads and changes the look of an effect (AVFX) file in place: the whole-effect tint, size, offset
/// and turn (root fields), and the colours, brightness and alpha of its parts (curve keys). Only
/// numbers the file already has change, so the file keeps its layout byte for byte. Layouts follow
/// VFXEditor: a curve's keys are 16 bytes (frame, type, then three floats; a colour key's are its
/// RGB, a single value is the third). Surveyed on the 5,095 effects of the mod library (2026-10-07):
/// every one has the root fields; 4,424 colour their particles; 4,058 have particle brightness.
/// </summary>
internal static class TimelineEffectLooks
{
    private static readonly string[] TintFields = ["RvR", "RvG", "RvB"];
    private static readonly string[] SizeFields = ["RvSx", "RvSy", "RvSz"];
    private static readonly string[] OffsetFields = ["RvPx", "RvPy", "RvPz"];
    private static readonly string[] TurnFields = ["RvRx", "RvRy", "RvRz"];
    private static readonly HashSet<string> RootFields = [.. TintFields, .. SizeFields, .. OffsetFields, .. TurnFields];

    /// <summary>The colour curves of a particle's type data: model, line, polyline and disc colours.</summary>
    private static readonly HashSet<string> DataColors = ["ColB", "ColE", "ColC", "CoEB", "CoEC", "CoEE", "CEI", "CEO"];

    /// <summary>A dissolve particle's colour curves, inside its EdC.</summary>
    private static readonly HashSet<string> DissolveColors = ["StrC", "MidC", "EndC"];

    private const int KeySize = 16;
    private const int PointLight = 0, DirectionalLight = 1;
    private static readonly Regex HashSuffix = new("_[0-9a-f]{8}$", RegexOptions.CultureInvariant);

    private enum Part : byte
    {
        /// <summary>A colour key's RGB floats.</summary>
        Rgb,
        /// <summary>A simple animation colour's RGB bytes.</summary>
        ByteRgb,
        /// <summary>A particle colour's alpha key.</summary>
        Alpha,
        /// <summary>A particle's or light's brightness key.</summary>
        Glow,
        /// <summary>A point light's range key.</summary>
        LightRange,
    }

    private readonly record struct Spot(Part Part, int At);

    private sealed record Layout(Dictionary<string, int> Root, List<Spot> Spots);

    /// <summary>A colour as a swatch: each channel rounded to three decimals, so copies of one colour count as one.</summary>
    public static Vector3 Swatch(Vector3 rgb) =>
        new((float)Math.Round(rgb.X, 3), (float)Math.Round(rgb.Y, 3), (float)Math.Round(rgb.Z, 3));

    /// <summary>
    /// <paramref name="rgb"/> with its hue turned by <paramref name="hue"/> degrees and its saturation
    /// scaled, keeping its brightness. Values above 1 (a glow) keep their strength.
    /// </summary>
    public static Vector3 Shift(Vector3 rgb, float hue, float saturation)
    {
        if (hue == 0 && saturation == 1)
            return rgb;
        var value = Math.Max(rgb.X, Math.Max(rgb.Y, rgb.Z));
        if (!(value > 0))
            return rgb;
        var minimum = Math.Min(rgb.X, Math.Min(rgb.Y, rgb.Z));
        var chroma = value - minimum;
        var s = chroma / value;
        float h = 0;
        if (chroma > 0)
        {
            h = value == rgb.X ? (rgb.Y - rgb.Z) / chroma % 6 : value == rgb.Y ? (rgb.Z - rgb.X) / chroma + 2 : (rgb.X - rgb.Y) / chroma + 4;
            h *= 60;
        }
        h = ((h + hue) % 360 + 360) % 360;
        s = Math.Clamp(s * saturation, 0, 1);
        var c = value * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return new Vector3(r + m, g + m, b + m);
    }

    /// <summary>What the effect editor shows of <paramref name="avfx"/>; throws <see cref="InvalidDataException"/> for a file it can't read.</summary>
    public static TimelineEffectLook Read(byte[] avfx)
    {
        var layout = Scan(avfx);
        var uses = new Dictionary<Vector3, int>();
        var order = new List<Vector3>();
        foreach (var spot in layout.Spots)
        {
            Vector3? colour = spot.Part switch
            {
                Part.Rgb => ReadRgb(avfx, spot.At),
                Part.ByteRgb => new Vector3(avfx[spot.At], avfx[spot.At + 1], avfx[spot.At + 2]) / 255f,
                _ => null,
            };
            if (colour is not { } rgb || !float.IsFinite(rgb.X) || !float.IsFinite(rgb.Y) || !float.IsFinite(rgb.Z))
                continue;
            var swatch = Swatch(rgb);
            if (uses.TryGetValue(swatch, out var count))
                uses[swatch] = count + 1;
            else
            {
                uses[swatch] = 1;
                order.Add(swatch);
            }
        }
        var colours = order.Select((rgb, index) => (Colour: new TimelineEffectColor(rgb, uses[rgb]), Index: index))
            .OrderByDescending(c => c.Colour.Uses).ThenBy(c => c.Index).Select(c => c.Colour).ToImmutableArray();
        return new TimelineEffectLook(Root(avfx, layout, TintFields), Root(avfx, layout, SizeFields), Root(avfx, layout, OffsetFields),
            Root(avfx, layout, TurnFields), colours,
            layout.Spots.Any(s => s.Part == Part.Glow), layout.Spots.Any(s => s.Part == Part.Alpha),
            layout.Spots.Any(s => s.Part == Part.LightRange));
    }

    /// <summary>
    /// <paramref name="avfx"/> with <paramref name="edit"/> written in: the same bytes but for the numbers
    /// it changes. Values out of the editor's ranges are clamped.
    /// </summary>
    public static byte[] Apply(byte[] avfx, TimelineEffectEdit edit)
    {
        var layout = Scan(avfx);
        var output = (byte[])avfx.Clone();
        WriteRoot(output, layout, TintFields, edit.Tint, 0, TimelineEffectEdit.MaxTint);
        WriteRoot(output, layout, SizeFields, edit.Size, TimelineEffectEdit.MinSize, TimelineEffectEdit.MaxSize);
        WriteRoot(output, layout, OffsetFields, edit.Offset, -TimelineEffectEdit.MaxOffset, TimelineEffectEdit.MaxOffset);
        WriteRoot(output, layout, TurnFields, edit.Turn, -TimelineEffectEdit.MaxTurn, TimelineEffectEdit.MaxTurn);
        var safe = edit with
        {
            Hue = float.IsFinite(edit.Hue) ? Math.Clamp(edit.Hue, -180, 180) : 0,
            Saturation = Bounded(edit.Saturation, TimelineEffectEdit.MaxSaturation),
            Colors = [.. edit.Colors.Select(c => (c.From, Vector3.Clamp(Finite(c.To), Vector3.Zero, new Vector3(TimelineEffectEdit.MaxTint))))],
        };
        var glow = Bounded(edit.Glow, TimelineEffectEdit.MaxGlow);
        var opacity = Bounded(edit.Opacity, TimelineEffectEdit.MaxOpacity);
        var range = Bounded(edit.LightRange, TimelineEffectEdit.MaxLightRange);
        foreach (var (part, at) in layout.Spots)
            switch (part)
            {
                case Part.Rgb when safe.Recolors:
                    var rgb = ReadRgb(avfx, at);
                    if (float.IsFinite(rgb.X) && float.IsFinite(rgb.Y) && float.IsFinite(rgb.Z) && safe.Recolor(rgb) is var changed && changed != rgb &&
                        float.IsFinite(changed.X) && float.IsFinite(changed.Y) && float.IsFinite(changed.Z))
                        WriteRgb(output, at, changed);
                    break;
                case Part.ByteRgb when safe.Recolors:
                    var original = new Vector3(avfx[at], avfx[at + 1], avfx[at + 2]) / 255f;
                    var recoloured = Vector3.Clamp(safe.Recolor(original), Vector3.Zero, Vector3.One) * 255f;
                    output[at] = (byte)Math.Round(recoloured.X);
                    output[at + 1] = (byte)Math.Round(recoloured.Y);
                    output[at + 2] = (byte)Math.Round(recoloured.Z);
                    break;
                case Part.Alpha when opacity != 1:
                    Scale(avfx, output, at, opacity);
                    break;
                case Part.Glow when glow != 1:
                    Scale(avfx, output, at, glow);
                    break;
                case Part.LightRange when range != 1:
                    Scale(avfx, output, at, range);
                    break;
            }
        return output;
    }

    /// <summary>
    /// The document with every effect look written into a copy of its effect: the events use the
    /// copies' game paths and carry no look any more, ready for <see cref="TimelineConvert.Apply"/>.
    /// Each copy gets its own content-named path under vfx/instant_edit/ and is mapped beside the
    /// effect it copies (see <see cref="TimelineImport.Beside"/>). A copy of an import takes the
    /// import's other files along, and is mapped beside what that import copies. A look that changes
    /// nothing keeps the effect. <paramref name="effect"/> gives an effect's bytes by game path, null
    /// when it wasn't read; such an effect is refused rather than saved without its look.
    /// </summary>
    public static (TimelineDocument Document, ImmutableArray<TimelineImport> Copies) Bake(TimelineDocument doc,
        Func<string, byte[]?> effect, IReadOnlyDictionary<string, TimelineImport> imports)
    {
        if (doc.Events.IsDefault || doc.Events.All(e => e.Look == null))
            return (doc, []);
        var copies = new Dictionary<string, TimelineImport>(StringComparer.OrdinalIgnoreCase);
        var events = doc.Events.Select(ev =>
        {
            if (ev.Look is not { } look)
                return ev;
            if (look.IsEmpty)
                return ev with { Look = null };
            var bytes = effect(ev.Path) ?? throw new InvalidDataException($"{Path.GetFileName(ev.Path)} could not be read, so its look can't be used.");
            var edited = Apply(bytes, look);
            if (edited.AsSpan().SequenceEqual(bytes))
                return ev with { Look = null };
            var gamePath = TimelineImports.EffectPath(BaseName(ev.Path) + ".avfx", edited);
            if (!AnimationDependencies.SafeGamePath(gamePath))
                throw new InvalidDataException("The effect's name cannot be used in a game path.");
            if (!copies.ContainsKey(gamePath))
                copies[gamePath] = Copy(ev.Path, gamePath, edited, imports.GetValueOrDefault(ev.Path));
            return ev with { Path = gamePath, Look = null };
        }).ToImmutableArray();
        return (doc with { Events = events }, [.. copies.Values]);
    }

    /// <summary>
    /// The looks a preview shows by pointing their effect's own game path at an edited copy: one per
    /// path whose events all have that same look. The game keeps a playing clip's file loaded, so a
    /// preview's rebuilt timeline, naming copies, may never be read; the effect's own path is asked
    /// for again whenever the effect starts. Returns the document with those looks taken off; looks
    /// that differ between events of one effect stay on for <see cref="Bake"/>.
    /// </summary>
    public static (TimelineDocument Document, ImmutableDictionary<string, TimelineEffectEdit> Looks) Redirectable(TimelineDocument doc)
    {
        var none = ImmutableDictionary<string, TimelineEffectEdit>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
        if (doc.Events.IsDefault || doc.Events.All(e => e.Look == null))
            return (doc, none);
        var looks = none.ToBuilder();
        foreach (var group in doc.Events.Where(e => e.Path.Length > 0).GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
            if (group.First().Look is { IsEmpty: false } look && group.All(e => Equals(e.Look, look)))
                looks[group.Key] = look;
        if (looks.Count == 0)
            return (doc, none);
        var events = doc.Events.Select(e => e.Look != null && looks.ContainsKey(e.Path) ? e with { Look = null } : e).ToImmutableArray();
        return (doc with { Events = events }, looks.ToImmutable());
    }

    /// <summary>
    /// The files that show <paramref name="looks"/> (see <see cref="Redirectable"/>): each effect's own
    /// game path with its look written in. A look that changes nothing is left out.
    /// </summary>
    public static ImmutableDictionary<string, byte[]> Redirected(IReadOnlyDictionary<string, TimelineEffectEdit> looks, Func<string, byte[]?> effect)
    {
        var files = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, look) in looks)
        {
            var bytes = effect(path) ?? throw new InvalidDataException($"{Path.GetFileName(path)} could not be read, so its look can't be used.");
            var edited = Apply(bytes, look);
            if (!edited.AsSpan().SequenceEqual(bytes))
                files[path] = edited;
        }
        return files.ToImmutable();
    }

    /// <summary>Whether two sets of looks by game path are the same.</summary>
    public static bool SameLooks(IReadOnlyDictionary<string, TimelineEffectEdit> a, IReadOnlyDictionary<string, TimelineEffectEdit> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && pair.Value.Equals(other));

    /// <summary>The name a copy of <paramref name="gamePath"/> starts with: an earlier copy's name without its content hash.</summary>
    internal static string BaseName(string gamePath)
    {
        var name = Path.GetFileNameWithoutExtension(gamePath);
        return gamePath.StartsWith("vfx/instant_edit/", StringComparison.OrdinalIgnoreCase) ? HashSuffix.Replace(name, string.Empty) : name;
    }

    private static TimelineImport Copy(string original, string gamePath, byte[] edited, TimelineImport? import)
    {
        var files = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var beside = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        if (import != null)
        {
            foreach (var (path, bytes) in import.Files)
                if (!path.Equals(original, StringComparison.OrdinalIgnoreCase))
                    files[path] = bytes;
            foreach (var (path, of) in import.Beside ?? ImmutableDictionary<string, string>.Empty)
                if (!path.Equals(original, StringComparison.OrdinalIgnoreCase) && files.ContainsKey(path))
                    beside[path] = of;
        }
        files[gamePath] = edited;
        beside[gamePath] = import?.Beside?.GetValueOrDefault(original) ?? original;
        return new TimelineImport(gamePath, files.ToImmutable(), 0, beside.ToImmutable());
    }

    /// <summary>Where the numbers the editor reads and changes are.</summary>
    private static Layout Scan(byte[] avfx)
    {
        var length = RootLength(avfx);
        var root = new Dictionary<string, int>(StringComparer.Ordinal);
        var spots = new List<Spot>();
        foreach (var (name, start, size) in AnimationDependencies.Chunks(avfx, 8, 8 + length))
            switch (name)
            {
                case "Ptcl":
                    Particle(avfx, start, size, spots);
                    break;
                case "Emit":
                    foreach (var (field, at, count) in AnimationDependencies.Chunks(avfx, start, start + size))
                        if (field == "Col")
                            Curve(avfx, at, count, spots, emitter: true);
                    break;
                case "Efct":
                    Effector(avfx, start, size, spots);
                    break;
                default:
                    if (size == 4 && RootFields.Contains(name))
                        root.TryAdd(name, start);
                    break;
            }
        return new Layout(root, spots);
    }

    private static void Particle(byte[] avfx, int start, int size, List<Spot> spots)
    {
        var simple = false;
        int? simpleColours = null;
        foreach (var (name, at, count) in AnimationDependencies.Chunks(avfx, start, start + size))
            switch (name)
            {
                case "bSCt" when count >= 1:
                    simple = avfx[at] != 0;
                    break;
                case "Col":
                    Curve(avfx, at, count, spots);
                    break;
                case "Data":
                    foreach (var (field, from, bytes) in AnimationDependencies.Chunks(avfx, at, at + count))
                        if (DataColors.Contains(field))
                            Curve(avfx, from, bytes, spots, main: false);
                        else if (field == "EdC")
                            foreach (var (part, inner, length) in AnimationDependencies.Chunks(avfx, from, from + bytes))
                                if (DissolveColors.Contains(part))
                                    Curve(avfx, inner, length, spots, main: false);
                    break;
                case "Smpl":
                    foreach (var (field, from, bytes) in AnimationDependencies.Chunks(avfx, at, at + count))
                        if (field == "Cols" && bytes >= 16)
                            simpleColours = from;
                    break;
            }
        // Simple animation colours count only when the particle uses its simple animation.
        if (simple && simpleColours is { } colours)
            for (var i = 0; i < 4; i++)
                spots.Add(new Spot(Part.ByteRgb, colours + 4 * i));
    }

    private static void Effector(byte[] avfx, int start, int size, List<Spot> spots)
    {
        var type = -1;
        (int At, int Size)? data = null;
        foreach (var (name, at, count) in AnimationDependencies.Chunks(avfx, start, start + size))
            if (name == "EfVT" && count >= 4) type = BinaryPrimitives.ReadInt32LittleEndian(avfx.AsSpan(at));
            else if (name == "Data") data = (at, count);
        if (data is not { } found || type is not (PointLight or DirectionalLight))
            return;
        foreach (var (name, at, count) in AnimationDependencies.Chunks(avfx, found.At, found.At + found.Size))
            if (name is "Col" or "Amb") Curve(avfx, at, count, spots, light: true);
            else if (name == "DstS" && type == PointLight)
                foreach (var key in Keys(avfx, at, count))
                    spots.Add(new Spot(Part.LightRange, key + 12));
    }

    /// <summary>
    /// A colour curve's keys: its RGB always (an emitter's only when not white: a white emitter colour
    /// leaves its particles as they are), its brightness for a particle's main colour or a light, its
    /// alpha for a particle's main colour. A particle's colours multiply, so scaling only the main
    /// curve's brightness and alpha changes each particle once.
    /// </summary>
    private static void Curve(byte[] avfx, int start, int size, List<Spot> spots, bool emitter = false, bool light = false, bool main = true)
    {
        foreach (var (name, at, count) in AnimationDependencies.Chunks(avfx, start, start + size))
            switch (name)
            {
                case "RGB":
                    foreach (var key in Keys(avfx, at, count))
                        if (!emitter || ReadRgb(avfx, key + 4) != Vector3.One)
                            spots.Add(new Spot(Part.Rgb, key + 4));
                    break;
                case "A" when main && !emitter && !light:
                    foreach (var key in Keys(avfx, at, count))
                        spots.Add(new Spot(Part.Alpha, key + 12));
                    break;
                case "Bri" when main && !emitter:
                    foreach (var key in Keys(avfx, at, count))
                        spots.Add(new Spot(Part.Glow, key + 12));
                    break;
            }
    }

    /// <summary>Where each key of a curve starts; as many as both its count (KeyC) and its key data allow.</summary>
    private static IEnumerable<int> Keys(byte[] avfx, int start, int size)
    {
        var count = int.MaxValue;
        (int At, int Size)? keys = null;
        foreach (var (name, at, bytes) in AnimationDependencies.Chunks(avfx, start, start + size))
            if (name == "KeyC" && bytes >= 4) count = BinaryPrimitives.ReadInt32LittleEndian(avfx.AsSpan(at));
            else if (name == "Keys") keys = (at, bytes);
        if (keys is not { } found)
            yield break;
        var total = Math.Min(found.Size / KeySize, Math.Max(0, count));
        for (var i = 0; i < total; i++)
            yield return found.At + i * KeySize;
    }

    private static Vector3? Root(byte[] avfx, Layout layout, string[] fields)
    {
        var values = new float[3];
        for (var i = 0; i < 3; i++)
        {
            if (!layout.Root.TryGetValue(fields[i], out var at))
                return null;
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(avfx.AsSpan(at));
        }
        return new Vector3(values[0], values[1], values[2]);
    }

    private static void WriteRoot(byte[] output, Layout layout, string[] fields, Vector3? value, float min, float max)
    {
        if (value is not { } wanted || Root(output, layout, fields) is null)
            return;
        var clamped = Vector3.Clamp(Finite(wanted), new Vector3(min), new Vector3(max));
        for (var i = 0; i < 3; i++)
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(layout.Root[fields[i]]), clamped[i]);
    }

    private static Vector3 ReadRgb(byte[] avfx, int at) => new(BinaryPrimitives.ReadSingleLittleEndian(avfx.AsSpan(at)),
        BinaryPrimitives.ReadSingleLittleEndian(avfx.AsSpan(at + 4)), BinaryPrimitives.ReadSingleLittleEndian(avfx.AsSpan(at + 8)));

    private static void WriteRgb(byte[] output, int at, Vector3 rgb)
    {
        BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(at), rgb.X);
        BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(at + 4), rgb.Y);
        BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(at + 8), rgb.Z);
    }

    private static void Scale(byte[] avfx, byte[] output, int at, float factor)
    {
        var value = BinaryPrimitives.ReadSingleLittleEndian(avfx.AsSpan(at));
        if (float.IsFinite(value * factor))
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(at), value * factor);
    }

    private static float Bounded(float value, float max) => float.IsFinite(value) ? Math.Clamp(value, 0, max) : 1;

    private static Vector3 Finite(Vector3 value) =>
        new(float.IsFinite(value.X) ? value.X : 0, float.IsFinite(value.Y) ? value.Y : 0, float.IsFinite(value.Z) ? value.Z : 0);

    private static int RootLength(byte[] avfx)
    {
        if (avfx.Length > AnimationPap.MaxFileSize) throw new InvalidDataException("The effect exceeds the file size limit.");
        if (avfx.Length < 8 || !avfx.AsSpan(0, 4).SequenceEqual("XFVA"u8)) throw new InvalidDataException("This is not an effect file.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(avfx.AsSpan(4));
        if (length < 0 || length != avfx.Length - 8) throw new InvalidDataException("The effect file is damaged.");
        return length;
    }
}
