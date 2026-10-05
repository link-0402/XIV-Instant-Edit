using System.Collections.Immutable;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>How a field's value is stored in an entry's fixed bytes.</summary>
internal enum TmbStorage : byte { Int32, Int16, UInt16, Byte, SByte, Single, Bit0, Vector }

/// <summary>Where a <see cref="TimelineField"/> lives: its offset and storage, and for a vector its component and size.</summary>
internal sealed record TmbFieldBinding(TimelineField Field, int Offset, TmbStorage Storage, int Component = 0, int VectorSize = 0);

/// <summary>One TMB entry kind as the editor sees it.</summary>
/// <param name="HasDuration">Whether +12 holds how many frames the entry lasts (VFXEditor's "Duration" or "Fade Time").</param>
/// <param name="StringOffset">Where the kind keeps its string pointer, or -1.</param>
/// <param name="Lane">The lane of the kind; C009/C010 pick theirs from the motion they play.</param>
/// <param name="Access">The access of the kind; a C009/C010 body motion is always Locked.</param>
internal sealed record TmbEntryKind(string Magic, string Name, int Size, bool HasDuration, int StringOffset,
    TimelineLane Lane, TimelineAccess Access, string? LockReason, ImmutableArray<TmbFieldBinding> Bindings)
{
    public ImmutableArray<TimelineField> Fields { get; } = [.. Bindings.Select(b => b.Field)];
}

/// <summary>
/// Every C-entry kind VFXEditor knows (same sizes as TmbLayouts.json), with the fields the editor
/// understands well enough to expose. Every other byte of an entry is preserved untouched.
/// </summary>
internal static class TmbEntrySchema
{
    public const string GameplayReason = "Affects gameplay";
    public const string MotionReason = "The animation itself";
    public const string MovableReason = "Can be moved or deleted";

    /// <summary>C010's time control bit: on, the motion plays its frame range over the duration; off, the game ignores both.</summary>
    public const string TimeControlField = "TimeControl";

    /// <summary>Kinds that move the player, lock actions, change targeting or drive cameras: never changed.</summary>
    private static readonly ImmutableHashSet<string> Gameplay =
        ["C013", "C083", "C084", "C117", "C124", "C125", "C139", "C168", "C176", "C177", "C178", "C192"];

    /// <summary>The kinds a prop Add creates (see <see cref="TimelineTemplates"/>); other prop kinds can only be moved.</summary>
    internal static readonly ImmutableHashSet<string> PropRecipe = ["C173", "C198", "C031", "C094"];

    private static readonly ImmutableHashSet<string> PropKinds =
        ["C043", "C198", "C031", "C094", "C187", "C014", "C015", "C100", "C174", "C175", "C199", "C203"];

    private static readonly (string Label, double Value)[] BindOrigins = [("Off", -1), ("Default", 0), ("Caster", 1), ("Target", 2)];
    private static readonly (string Label, double Value)[] BindTypes = [("Off", -1), ("Character", 0), ("Weapon", 1), ("Off hand", 2), ("Summon", 3)];

    public static IReadOnlyDictionary<string, TmbEntryKind> Kinds { get; } = Build();

    public static TmbEntryKind? Find(string magic) => Kinds.GetValueOrDefault(magic);

    public static TmbEntryKind Get(string magic) =>
        Find(magic) ?? throw new InvalidDataException($"The timeline entry kind {magic} is unknown.");

    /// <summary>
    /// The lane, access and reason of an entry. C009/C010 play a motion: "cfxl_" ones are lip sync,
    /// other "cfx" ones (faces, blinks) expressions, anything else the clip's own body motion.
    /// </summary>
    public static (TimelineLane Lane, TimelineAccess Access, string? Reason) Classify(string magic, string path)
    {
        var kind = Get(magic);
        if (magic is not ("C009" or "C010")) return (kind.Lane, kind.Access, kind.LockReason);
        if (path.StartsWith("cfxl_", StringComparison.Ordinal)) return (TimelineLane.LipSync, TimelineAccess.Editable, null);
        if (path.StartsWith("cfx", StringComparison.Ordinal)) return (TimelineLane.Expression, TimelineAccess.Editable, null);
        return (TimelineLane.Motion, TimelineAccess.Locked, MotionReason);
    }

    /// <summary>The entry's duration in frames when its kind has one, else 0.</summary>
    public static int Duration(TmbFile.Entry entry) => Get(entry.Magic).HasDuration ? entry.Int32(12) : 0;

    public static ImmutableDictionary<string, double> ReadValues(TmbFile.Entry entry) =>
        Get(entry.Magic).Bindings.ToImmutableDictionary(b => b.Field.Name, b => Read(entry, b));

    public static double Read(TmbFile.Entry entry, TmbFieldBinding binding) => binding.Storage switch
    {
        TmbStorage.Int32 => entry.Int32(binding.Offset),
        TmbStorage.Int16 => entry.Int16(binding.Offset),
        TmbStorage.UInt16 => entry.UInt16(binding.Offset),
        TmbStorage.Byte => entry.Byte(binding.Offset),
        TmbStorage.SByte => entry.SByte(binding.Offset),
        TmbStorage.Single => entry.Single(binding.Offset),
        TmbStorage.Bit0 => entry.Int32(binding.Offset) & 1,
        // VFXEditor reads a vector of the wrong size as zeros, and writes the right size back.
        _ => entry.Vector(binding.Offset) is { } v && v.Length == binding.VectorSize ? v[binding.Component] : 0,
    };

    /// <summary>The value as storage keeps it: floats lose precision, everything else is exact.</summary>
    public static double Normalize(TmbFieldBinding binding, double value) =>
        binding.Storage is TmbStorage.Single or TmbStorage.Vector ? (float)value : value;

    /// <summary>Writes one field, refusing a value its field does not allow. Other bits and bytes stay as they are.</summary>
    public static void Write(TmbFile file, short id, TmbFieldBinding binding, double value)
    {
        var field = binding.Field;
        var whole = field.Kind is not TimelineFieldKind.Float;
        if (!double.IsFinite(value) || value < field.Min || value > field.Max || whole && value != Math.Round(value) ||
            field.Kind == TimelineFieldKind.Choice && field.Options.All(o => o.Value != value))
            throw new InvalidDataException($"{value} is not a valid {field.Label}.");
        var entry = file.Find(id) ?? throw new InvalidDataException($"The timeline has no entry {id}.");
        switch (binding.Storage)
        {
            case TmbStorage.Int32: file.SetInt32(id, binding.Offset, (int)value); break;
            case TmbStorage.Int16: file.SetInt16(id, binding.Offset, (short)value); break;
            case TmbStorage.UInt16: file.SetUInt16(id, binding.Offset, (ushort)value); break;
            case TmbStorage.Byte: file.SetByte(id, binding.Offset, (byte)value); break;
            case TmbStorage.SByte: file.SetSByte(id, binding.Offset, (sbyte)value); break;
            case TmbStorage.Single: file.SetSingle(id, binding.Offset, (float)value); break;
            case TmbStorage.Bit0: file.SetInt32(id, binding.Offset, entry.Int32(binding.Offset) & ~1 | (int)value); break;
            default:
                var current = entry.Vector(binding.Offset);
                var vector = current.Length == binding.VectorSize ? current.ToArray() : new float[binding.VectorSize];
                vector[binding.Component] = (float)value;
                file.SetVector(id, binding.Offset, vector);
                break;
        }
    }

    private static Dictionary<string, TmbEntryKind> Build()
    {
        // Magic, VFXEditor's name, size, and whether +12 is a length in frames.
        (string Magic, string Name, int Size, bool Duration)[] kinds =
        [
            ("C002", "TMB", 0x1C, true), ("C006", "Fly Text Settings", 0x18, false), ("C009", "Animation (PAP Only)", 0x18, true),
            ("C010", "Animation", 0x28, true), ("C011", "Fly Text", 0x14, false), ("C012", "VFX", 0x48, true),
            ("C013", "Model Animation", 0x1C, true), ("C014", "Weapon Position", 0x1C, false), ("C015", "Weapon Size", 0x1C, true),
            ("C021", "Unknown", 0x1C, false), ("C031", "Summon Animation", 0x18, true), ("C033", "Crafting Delay", 0x14, false),
            ("C034", "Gathering Delay", 0x14, false), ("C042", "Footstep", 0x1C, false), ("C043", "Summon Weapon", 0x20, true),
            ("C053", "Voiceline", 0x1C, false), ("C063", "Sound", 0x20, false), ("C067", "Flinch", 0x14, false),
            ("C068", "Shade Color", 0x24, true), ("C075", "Terrain VFX", 0x40, false), ("C083", "Unknown", 0x18, false),
            ("C084", "Unknown", 0x18, false), ("C088", "Animation Blending", 0x14, true), ("C089", "Unknown", 0x18, true),
            ("C093", "Color", 0x28, true), ("C094", "Invisibility", 0x20, true), ("C095", "Unknown", 0x20, false),
            ("C100", "Hide Weapon", 0x20, false), ("C107", "VFX Trigger", 0x1C, false), ("C117", "Forced Forward Movement", 0x18, true),
            ("C118", "Animation Transition", 0x18, false), ("C120", "Controller Vibration", 0x18, true), ("C124", "Targetable", 0x18, false),
            ("C125", "Animation Lock", 0x14, true), ("C131", "Animation Cancelled by Movement", 0x14, false), ("C136", "Local Wind Scale", 0x14, false),
            ("C139", "Forced Movement Cancel", 0x14, false), ("C142", "Freeze Position", 0x1C, true), ("C143", "Fishing Sound", 0x18, false),
            ("C144", "Camera and Nameplate Control", 0x2C, true), ("C161", "Blink", 0x1C, false), ("C168", "Forced Camera Control", 0x38, true),
            ("C173", "Async VFX", 0x44, false), ("C174", "Object Control", 0x28, true), ("C175", "Object Scaling", 0x28, true),
            ("C176", "Forced Vertical Movement", 0x28, true), ("C177", "Forced Backwards Movement", 0x24, true), ("C178", "Unknown", 0x24, true),
            ("C187", "Remove Part", 0x20, true), ("C188", "Invisibility", 0x14, false), ("C192", "Unknown", 0x38, false),
            ("C194", "Unknown", 0x1C, false), ("C197", "Voiceline", 0x24, true), ("C198", "Lemure", 0x28, true),
            ("C199", "Freeze Object", 0x20, false), ("C202", "Unknown", 0x24, false), ("C203", "Summon Weapon Visibility", 0x2C, true),
            ("C204", "Shroud Transform", 0x1C, true), ("C211", "Lock Facing Direction", 0x1C, true), ("C212", "Unknown", 0x20, false),
            ("C215", "Unknown", 0x1C, true), ("C216", "Subtitles", 0x30, false), ("C225", "Unknown", 0x1C, true),
            ("C230", "BGM Track", 0x28, false), ("C234", "Unknown", 0x20, false),
        ];
        var fields = new Dictionary<string, TmbFieldBinding[]>
        {
            ["C063"] =
            [
                Number("Loop", "Loop", 12, TmbStorage.Int32, -1, short.MaxValue),
                Number("SoundIndex", "Sound index", 24, TmbStorage.Int32, 0, 255),
                new(new TimelineField("PositionFlags", "Position", TimelineFieldKind.Flags, 0, 255,
                    [("3D range", 1), ("Stop with animation", 2), ("Use bind point", 4)]), 28, TmbStorage.Byte),
                Number("BindId", "Bind point", 29, TmbStorage.Byte, 0, 255),
            ],
            ["C012"] =
            [
                Choice("Bind1.Origin", "Bind 1 from", 24, TmbStorage.SByte, BindOrigins),
                Choice("Bind1.Type", "Bind 1 type", 25, TmbStorage.SByte, BindTypes),
                Number("Bind1.Id", "Bind 1 point", 26, TmbStorage.Int16, -1, short.MaxValue),
                Choice("Bind2.Origin", "Bind 2 from", 28, TmbStorage.SByte, BindOrigins),
                Choice("Bind2.Type", "Bind 2 type", 29, TmbStorage.SByte, BindTypes),
                Number("Bind2.Id", "Bind 2 point", 30, TmbStorage.Int16, -1, short.MaxValue),
                .. Vector("Scale", "Scale", 32, ["X", "Y", "Z"], -100, 100),
                .. Vector("Rotation", "Rotation", 40, ["X", "Y", "Z"], -2 * Math.PI, 2 * Math.PI),
                .. Vector("Position", "Position", 48, ["X", "Y", "Z"], -100, 100),
                .. Vector("Color", "Color", 56, ["R", "G", "B", "A"], 0, 100),
                Choice("Visibility", "Visibility", 64, TmbStorage.Int32,
                    [("Default", 0), ("Default with triggers", 1), ("Always", 2), ("Always with triggers", 3)]),
            ],
            // -1 keeps a looping effect going, 0 turns the entry off, 1 plays it once; more waits that
            // many frames after one loop before playing it again (see TimelineEffectCheck).
            ["C173"] = [Number(TimelineEffectCheck.LoopField, "Loop/wait", 12, TmbStorage.Int32, -1, short.MaxValue)],
            ["C010"] = [new(new TimelineField(TimeControlField, "Time control", TimelineFieldKind.Bool, 0, 1), 20, TmbStorage.Bit0)],
            ["C198"] =
            [
                Number("Model", "Model", 32, TmbStorage.Int16, 0, short.MaxValue),
                Number("Body", "Body", 34, TmbStorage.Int16, 0, short.MaxValue),
                Number("Variant", "Variant", 36, TmbStorage.Int32, 0, ushort.MaxValue),
            ],
            ["C043"] =
            [
                Number("Model", "Model", 24, TmbStorage.Int16, 0, short.MaxValue),
                Number("Body", "Body", 26, TmbStorage.Int16, 0, short.MaxValue),
                Number("Variant", "Variant", 28, TmbStorage.Int32, 0, ushort.MaxValue),
            ],
            ["C031"] = [Number("WeaponTimeline", "Weapon timeline", 20, TmbStorage.UInt16, 0, ushort.MaxValue)],
            ["C094"] =
            [
                new(new TimelineField("StartVisibility", "Start visibility", TimelineFieldKind.Float, 0, 1), 20, TmbStorage.Single),
                new(new TimelineField("EndVisibility", "End visibility", TimelineFieldKind.Float, 0, 1), 24, TmbStorage.Single),
            ],
        };
        var result = new Dictionary<string, TmbEntryKind>(StringComparer.Ordinal);
        foreach (var (magic, name, size, duration) in kinds)
        {
            var lane = magic switch
            {
                "C009" or "C010" => TimelineLane.Motion,
                "C063" => TimelineLane.Sound,
                "C012" or "C173" => TimelineLane.Effect,
                _ when PropKinds.Contains(magic) => TimelineLane.Prop,
                _ => TimelineLane.Other,
            };
            var (access, reason) = Gameplay.Contains(magic) ? (TimelineAccess.Locked, GameplayReason)
                : magic is "C009" or "C010" ? (TimelineAccess.Locked, MotionReason)
                : magic is "C063" or "C012" || PropRecipe.Contains(magic) ? (TimelineAccess.Editable, (string?)null)
                : (TimelineAccess.Movable, MovableReason);
            result[magic] = new TmbEntryKind(magic, name, size, duration, TmbFile.StringOffset(magic), lane, access, reason,
                [.. fields.GetValueOrDefault(magic) ?? []]);
        }
        return result;
    }

    private static TmbFieldBinding Number(string name, string label, int offset, TmbStorage storage, double min, double max) =>
        new(new TimelineField(name, label, TimelineFieldKind.Int, min, max), offset, storage);

    private static TmbFieldBinding Choice(string name, string label, int offset, TmbStorage storage, (string, double)[] options) =>
        new(new TimelineField(name, label, TimelineFieldKind.Choice, options.Min(o => o.Item2), options.Max(o => o.Item2), [.. options]),
            offset, storage);

    private static IEnumerable<TmbFieldBinding> Vector(string name, string label, int offset, string[] parts, double min, double max) =>
        parts.Select((part, i) => new TmbFieldBinding(new TimelineField($"{name}.{part}", $"{label} {part}", TimelineFieldKind.Float, min, max),
            offset, TmbStorage.Vector, i, parts.Length));
}
