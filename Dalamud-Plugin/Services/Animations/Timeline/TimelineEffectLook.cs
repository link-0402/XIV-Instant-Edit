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
/// <param name="Turn">Turns the whole effect (RvRx, RvRy, RvRz), in radians (shown in degrees).</param>
/// <param name="Colors">The colours its parts use, most used first.</param>
/// <param name="Glow">Whether its particles or lights have brightness for Glow to scale.</param>
/// <param name="Opacity">Whether its particles have alpha for Opacity to scale.</param>
/// <param name="LightRange">Whether it has a point light whose range Light range scales.</param>
/// <param name="Bound">Whether it is attached to a binder: a timeline (BnNo) or one of its items (BdNo) names one.</param>
/// <param name="BinderPositions">The positions of its binders' property blocks, in file order.</param>
internal sealed record TimelineEffectLook(Vector3? Tint, Vector3? Size, Vector3? Offset, Vector3? Turn,
    ImmutableArray<TimelineEffectColor> Colors, bool Glow, bool Opacity, bool LightRange, bool Bound,
    ImmutableArray<TimelineEffectBinderPos> BinderPositions);

/// <summary>
/// The position (Pos) of one property block of a binder.
/// </summary>
/// <param name="Binder">The binder's index among the file's Bind blocks.</param>
/// <param name="Block">The property block: PrpS, Prp1, Prp2 or PrpG.</param>
/// <param name="Constant">The position when it has one key (<paramref name="Keys"/> is 1): an axis without a key is 0. Null otherwise.</param>
/// <param name="Keys">The most keys any of its X, Y and Z curves has; files leave out an axis that stays 0.</param>
internal sealed record TimelineEffectBinderPos(int Binder, string Block, Vector3? Constant, int Keys);

/// <summary>
/// The effect editor's changes to one effect. The tint replaces the file's (null keeps it); the others
/// scale or shift what the file has, so after a save they start from the saved copy. Saving or
/// previewing writes them into a copy of the effect (<see cref="TimelineEffectLooks.Bake"/>).
/// </summary>
internal sealed record TimelineEffectEdit
{
    public const float MaxTint = 4, MinSize = .01f, MaxSize = 20, MaxMove = 100, MaxTurn = 2 * MathF.PI;
    public const float MaxSaturation = 2, MaxGlow = 4, MaxOpacity = 2, MaxLightRange = 4;

    public Vector3? Tint { get; init; }

    /// <summary>Scales the whole effect by a factor per axis (1 keeps it). An attached effect can't be scaled.</summary>
    public Vector3? Size { get; init; }

    /// <summary>
    /// Moves the whole effect, in metres, from where the file puts it: an attached effect on its
    /// attach point (its binder's positions), another by its root offset.
    /// </summary>
    public Vector3? Move { get; init; }

    /// <summary>Turns the whole effect further, in radians (shown in degrees). An attached effect can't be turned.</summary>
    public Vector3? Turn { get; init; }

    /// <summary>The move, turn and size when they change something: not null, a zero move or turn, or a size of 1.</summary>
    internal Vector3? Moves => Move is { } move && move != Vector3.Zero ? move : null;

    internal Vector3? Turns => Turn is { } turn && turn != Vector3.Zero ? turn : null;

    internal Vector3? Sizes => Size is { } size && size != Vector3.One ? size : null;

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

    public bool IsEmpty => Tint == null && Sizes == null && Moves == null && Turns == null && !Recolors &&
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
            Tint == other.Tint && Size == other.Size && Move == other.Move && Turn == other.Turn && Hue == other.Hue &&
            Saturation == other.Saturation && Glow == other.Glow && Opacity == other.Opacity && LightRange == other.LightRange &&
            Colors.SequenceEqual(other.Colors));

    public override int GetHashCode() => HashCode.Combine(Tint, Size, Move, Turn, Hue, Saturation, Glow, HashCode.Combine(Opacity, LightRange, Colors.Length));
}

/// <summary>
/// Reads and changes the look of an effect (AVFX) file: the whole-effect tint, size, offset and turn
/// (root fields), the colours, brightness and alpha of its parts (curve keys), and where an attached
/// effect sits on its attach point (its binders' Pos curves). Only numbers the file already has
/// change, so the file keeps its layout byte for byte, except that a move on an attached effect adds
/// the position curves or keys it lacks. Layouts follow VFXEditor: a curve's keys are 16 bytes
/// (frame, type, then three floats; a colour key's are its RGB, a single value is the third).
/// Surveyed on the 5,095 effects of the mod library (2026-10-07): every one has the root fields;
/// 4,424 colour their particles; 4,058 have particle brightness.
/// </summary>
internal static class TimelineEffectLooks
{
    public const string AttachedRefusal = "Turn and size can't change an attached effect";
    public const string NoPositionRefusal = "This effect can't be moved: its attach point has no position.";

    /// <summary>The curves of a binder position in the order VFXEditor writes them: axis links, the axes, their random parts.</summary>
    private static readonly string[] PositionOrder = ["ACT", "ACTR", "X", "Y", "Z", "XR", "YR", "ZR"];
    private const short LinearKey = 1;

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

    private sealed record Layout(Dictionary<string, int> Root, List<Spot> Spots, bool Bound, List<TimelineEffectBinderPos> Binders);

    /// <summary>A binder's property blocks, each of which may place the effect.</summary>
    private static readonly HashSet<string> BinderBlocks = ["PrpS", "Prp1", "Prp2", "PrpG"];

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
            layout.Spots.Any(s => s.Part == Part.LightRange), layout.Bound, [.. layout.Binders]);
    }

    /// <summary>
    /// Why <see cref="Apply"/> would refuse <paramref name="edit"/> on the effect <paramref name="look"/>
    /// was read from, for the editor to say first; null when it wouldn't. An attached effect can't be
    /// turned or scaled (its binder has no rotation or scale), and moves only on a binder position.
    /// </summary>
    public static string? MoveRefusal(TimelineEffectLook look, TimelineEffectEdit edit) =>
        Refusal(look.Bound, !look.BinderPositions.IsEmpty, edit);

    private static string? Refusal(bool bound, bool positions, TimelineEffectEdit edit) =>
        !bound ? null
        : edit.Turns != null || edit.Sizes != null ? AttachedRefusal
        : edit.Moves != null && !positions ? NoPositionRefusal
        : null;

    /// <summary>
    /// <paramref name="avfx"/> with <paramref name="edit"/> written in: the same bytes but for the numbers
    /// it changes, and the position curves a move on an attached effect adds. Values out of the
    /// editor's ranges are clamped. Throws <see cref="InvalidDataException"/> with <see cref="MoveRefusal"/>'s
    /// text for an edit it can't write.
    /// </summary>
    public static byte[] Apply(byte[] avfx, TimelineEffectEdit edit)
    {
        var layout = Scan(avfx);
        if (Refusal(layout.Bound, layout.Binders.Count > 0, edit) is { } refusal)
            throw new InvalidDataException(refusal);
        var output = (byte[])avfx.Clone();
        WriteRoot(output, layout, TintFields, edit.Tint, 0, TimelineEffectEdit.MaxTint);
        if (!layout.Bound)
        {
            ChangeRoot(output, layout, SizeFields, edit.Sizes, scale: true, TimelineEffectEdit.MinSize, TimelineEffectEdit.MaxSize);
            ChangeRoot(output, layout, OffsetFields, edit.Moves, scale: false, -TimelineEffectEdit.MaxMove, TimelineEffectEdit.MaxMove);
            ChangeRoot(output, layout, TurnFields, edit.Turns, scale: false, -TimelineEffectEdit.MaxTurn, TimelineEffectEdit.MaxTurn);
        }
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
        return layout.Bound && edit.Moves is { } move
            ? MoveBinders(output, Vector3.Clamp(Finite(move), new Vector3(-TimelineEffectEdit.MaxMove), new Vector3(TimelineEffectEdit.MaxMove)))
            : output;
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
                copies[gamePath] = Copy(ev.Path, gamePath, edited, look, imports.GetValueOrDefault(ev.Path));
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

    /// <summary>
    /// The copy of <paramref name="original"/> with <paramref name="look"/>. It can be made the same way
    /// from another file of the effect it is mapped beside (see <see cref="TimelineImport.Rebuild"/>): a
    /// copy of a copy, such as a looping one, makes that copy first, and can't be made when that can't.
    /// </summary>
    private static TimelineImport Copy(string original, string gamePath, byte[] edited, TimelineEffectEdit look, TimelineImport? import)
    {
        var files = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var beside = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        var rebuild = ImmutableDictionary.CreateBuilder<string, Func<byte[], byte[]>>(StringComparer.OrdinalIgnoreCase);
        if (import != null)
        {
            foreach (var (path, bytes) in import.Files)
                if (!path.Equals(original, StringComparison.OrdinalIgnoreCase))
                    files[path] = bytes;
            foreach (var (path, of) in import.Beside ?? ImmutableDictionary<string, string>.Empty)
                if (!path.Equals(original, StringComparison.OrdinalIgnoreCase) && files.ContainsKey(path))
                    beside[path] = of;
            foreach (var (path, build) in import.Rebuild ?? ImmutableDictionary<string, Func<byte[], byte[]>>.Empty)
                if (beside.ContainsKey(path))
                    rebuild[path] = build;
        }
        files[gamePath] = edited;
        beside[gamePath] = import?.Beside?.GetValueOrDefault(original) ?? original;
        if (import?.Beside?.ContainsKey(original) == true)
        {
            if (import.Rebuild?.GetValueOrDefault(original) is { } first)
                rebuild[gamePath] = other => Apply(first(other), look);
        }
        else
            rebuild[gamePath] = other => Apply(other, look);
        return new TimelineImport(gamePath, files.ToImmutable(), 0, beside.ToImmutable(), rebuild.ToImmutable());
    }

    /// <summary>Where the numbers the editor reads and changes are.</summary>
    private static Layout Scan(byte[] avfx)
    {
        var length = RootLength(avfx);
        var root = new Dictionary<string, int>(StringComparer.Ordinal);
        var spots = new List<Spot>();
        var bound = false;
        var binders = new List<TimelineEffectBinderPos>();
        var binder = 0;
        foreach (var (name, start, size) in AnimationDependencies.Chunks(avfx, 8, 8 + length))
            switch (name)
            {
                case "TmLn":
                    bound |= Timeline(avfx, start, size);
                    break;
                case "Bind":
                    Binder(avfx, start, size, binder++, binders);
                    break;
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
        return new Layout(root, spots, bound, binders);
    }

    /// <summary>
    /// Whether a timeline names a binder: its own (BnNo) or one of its items' (BdNo); -1 is none. Each
    /// Item holds the items before it too, so the last one lists them all.
    /// </summary>
    private static bool Timeline(byte[] avfx, int start, int size)
    {
        var bound = false;
        (int At, int Size)? items = null;
        foreach (var (name, at, count) in AnimationDependencies.Chunks(avfx, start, start + size))
            if (name == "BnNo" && count >= 4) bound |= BinaryPrimitives.ReadInt32LittleEndian(avfx.AsSpan(at)) != -1;
            else if (name == "Item") items = (at, count);
        if (items is { } last)
            foreach (var (name, at, count) in AnimationDependencies.Chunks(avfx, last.At, last.At + last.Size))
                if (name == "BdNo" && count >= 4) bound |= BinaryPrimitives.ReadInt32LittleEndian(avfx.AsSpan(at)) != -1;
        return bound;
    }

    /// <summary>The positions of a binder's property blocks that have one (Pos, a curve per axis).</summary>
    private static void Binder(byte[] avfx, int start, int size, int binder, List<TimelineEffectBinderPos> positions)
    {
        foreach (var (block, at, count) in AnimationDependencies.Chunks(avfx, start, start + size))
            if (BinderBlocks.Contains(block))
                foreach (var (field, from, bytes) in AnimationDependencies.Chunks(avfx, at, at + count))
                    if (field == "Pos")
                        positions.Add(Position(avfx, from, bytes, binder, block));
    }

    /// <summary>
    /// A Pos block's key count and, when no axis has more than one key, its value (a single value is a
    /// key's third float). Files leave out the curves of axes that stay 0, so those read 0.
    /// </summary>
    private static TimelineEffectBinderPos Position(byte[] avfx, int start, int size, int binder, string block)
    {
        var values = new float[3];
        var keys = 0;
        foreach (var (axis, at, count) in AnimationDependencies.Chunks(avfx, start, start + size))
            if (axis is "X" or "Y" or "Z")
            {
                var found = Keys(avfx, at, count).ToList();
                keys = Math.Max(keys, found.Count);
                if (found.Count == 1)
                    values[axis[0] - 'X'] = BinaryPrimitives.ReadSingleLittleEndian(avfx.AsSpan(found[0] + 12));
            }
        return new TimelineEffectBinderPos(binder, block, keys == 1 ? new Vector3(values[0], values[1], values[2]) : null, keys);
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

    /// <summary>Bytes to add at <paramref name="At"/>, and the size fields of the blocks they go into.</summary>
    private readonly record struct Addition(int At, byte[] Bytes, int[] Sizes);

    /// <summary>
    /// <paramref name="avfx"/> with <paramref name="move"/> added to the position (Pos) of every binder
    /// property block: to each key of an axis that has keys, and as one new key, or a new curve, for an
    /// axis that has none, written as VFXEditor writes them. An axis the move leaves at 0 is not touched.
    /// Keys change in place, then the new bytes go in and every block around them grows.
    /// </summary>
    private static byte[] MoveBinders(byte[] avfx, Vector3 move)
    {
        var added = new List<Addition>();
        foreach (var (name, start, size) in AnimationDependencies.Chunks(avfx, 8, avfx.Length))
            if (name == "Bind")
                foreach (var (block, at, count) in AnimationDependencies.Chunks(avfx, start, start + size))
                    if (BinderBlocks.Contains(block))
                        foreach (var (field, from, bytes) in AnimationDependencies.Chunks(avfx, at, at + count))
                            if (field == "Pos")
                                MovePosition(avfx, from, bytes, move, [4, start - 4, at - 4, from - 4], added);
        return Grown(avfx, added);
    }

    /// <summary>A Pos block moved: each axis curve the move changes moved, or a new one put where VFXEditor writes it.</summary>
    private static void MovePosition(byte[] avfx, int start, int size, Vector3 move, int[] sizes, List<Addition> added)
    {
        var curves = AnimationDependencies.Chunks(avfx, start, start + size).ToList();
        for (var axis = 0; axis < 3; axis++)
        {
            var delta = move[axis];
            if (delta == 0)
                continue;
            var name = PositionOrder[2 + axis];
            var found = false;
            foreach (var (curve, at, count) in curves)
                if (curve == name)
                {
                    found = true;
                    MoveCurve(avfx, at, count, delta, [.. sizes, at - 4], added);
                }
            if (found)
                continue;
            // Before the first curve that comes after it, else last.
            var place = Array.IndexOf(PositionOrder, name);
            var next = curves.FindIndex(c => Array.IndexOf(PositionOrder, c.Name) > place);
            added.Add(new Addition(next < 0 ? start + size : curves[next].Start - 8, Block(name, NewCurve(delta)), sizes));
        }
    }

    /// <summary>A curve moved: <paramref name="delta"/> added to each key's value; a curve without keys gets one holding it.</summary>
    private static void MoveCurve(byte[] avfx, int start, int size, float delta, int[] sizes, List<Addition> added)
    {
        var keys = Keys(avfx, start, size).ToList();
        foreach (var key in keys)
        {
            var value = BinaryPrimitives.ReadSingleLittleEndian(avfx.AsSpan(key + 12)) + delta;
            if (float.IsFinite(value))
                BinaryPrimitives.WriteSingleLittleEndian(avfx.AsSpan(key + 12), value);
        }
        if (keys.Count > 0)
            return;
        // The new key goes in front of any data its count leaves out, as the one key the count names.
        (int At, int Size)? found = null;
        foreach (var (name, at, bytes) in AnimationDependencies.Chunks(avfx, start, start + size))
            if (name == "KeyC" && bytes >= 4) BinaryPrimitives.WriteInt32LittleEndian(avfx.AsSpan(at), 1);
            else if (name == "Keys") found = (at, bytes);
        added.Add(found is { } block
            ? new Addition(block.At, NewKey(delta), [.. sizes, block.At - 4])
            : new Addition(start + size, Block("Keys", NewKey(delta)), sizes));
    }

    /// <summary><paramref name="avfx"/> with <paramref name="added"/> put in and the size fields around each grown by it.</summary>
    private static byte[] Grown(byte[] avfx, List<Addition> added)
    {
        if (added.Count == 0)
            return avfx;
        foreach (var (_, bytes, sizes) in added)
            foreach (var field in sizes)
                BinaryPrimitives.WriteInt32LittleEndian(avfx.AsSpan(field), BinaryPrimitives.ReadInt32LittleEndian(avfx.AsSpan(field)) + bytes.Length);
        var grown = new byte[avfx.Length + added.Sum(a => a.Bytes.Length)];
        if (grown.Length > AnimationPap.MaxFileSize)
            throw new InvalidDataException("The effect exceeds the file size limit.");
        int from = 0, to = 0;
        foreach (var (at, bytes, _) in added.OrderBy(a => a.At))
        {
            avfx.AsSpan(from, at - from).CopyTo(grown.AsSpan(to));
            to += at - from;
            bytes.CopyTo(grown, to);
            to += bytes.Length;
            from = at;
        }
        avfx.AsSpan(from).CopyTo(grown.AsSpan(to));
        return grown;
    }

    /// <summary>A block as VFXEditor writes one: its name reversed and zero-padded to four bytes, its size, its payload padded to four.</summary>
    private static byte[] Block(string name, byte[] payload)
    {
        var block = new byte[8 + payload.Length + (-payload.Length & 3)];
        for (var i = 0; i < name.Length; i++)
            block[i] = (byte)name[name.Length - 1 - i];
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(4), payload.Length);
        payload.CopyTo(block, 8);
        return block;
    }

    private static byte[] Leaf(string name, int value)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, value);
        return Block(name, payload);
    }

    /// <summary>
    /// A curve's contents as VFXEditor writes a new one with a key: the key count, constant pre and post
    /// behaviour, random type 0, then the key. The library's own binder position curves read the same.
    /// </summary>
    private static byte[] NewCurve(float value) =>
        [.. Leaf("KeyC", 1), .. Leaf("BvPr", 0), .. Leaf("BvPo", 0), .. Leaf("RanT", 0), .. Block("Keys", NewKey(value))];

    /// <summary>A key as VFXEditor adds one: frame 0, linear, then 1, 1 and the value.</summary>
    private static byte[] NewKey(float value)
    {
        var key = new byte[KeySize];
        BinaryPrimitives.WriteInt16LittleEndian(key.AsSpan(2), LinearKey);
        BinaryPrimitives.WriteSingleLittleEndian(key.AsSpan(4), 1);
        BinaryPrimitives.WriteSingleLittleEndian(key.AsSpan(8), 1);
        BinaryPrimitives.WriteSingleLittleEndian(key.AsSpan(12), value);
        return key;
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

    /// <summary>
    /// The root fields with <paramref name="change"/> added (or, to <paramref name="scale"/>, multiplied)
    /// on each axis it changes, held to <paramref name="min"/>..<paramref name="max"/>; other axes keep their bytes.
    /// </summary>
    private static void ChangeRoot(byte[] output, Layout layout, string[] fields, Vector3? change, bool scale, float min, float max)
    {
        if (change is not { } by || Root(output, layout, fields) is not { } value)
            return;
        for (var i = 0; i < 3; i++)
        {
            if (!float.IsFinite(by[i]) || by[i] == (scale ? 1 : 0))
                continue;
            var result = scale ? value[i] * by[i] : value[i] + by[i];
            if (float.IsFinite(result))
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(layout.Root[fields[i]]), Math.Clamp(result, min, max));
        }
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
