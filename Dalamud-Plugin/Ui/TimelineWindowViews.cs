using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// The Timeline window's words and choices: lane names and colours, event labels, why a save is
/// blocked, file dialog filters, the catalog key and the files a save packages. No ImGui or
/// Dalamud dependencies, so the regression suite can check them.
/// </summary>
internal static class TimelineWindowViews
{
    /// <summary>The file dialog filter for sound imports.</summary>
    public const string SoundFilter = "Sounds{.wav,.ogg,.scd}";

    /// <summary>The file dialog filter for effect imports.</summary>
    public const string EffectFilter = "Effects{.avfx}";

    /// <summary>The face folder used when the player's face skeleton is unknown.</summary>
    public const string DefaultFace = "f0001";

    public const string NoWritableSource = "This animation has no writable Penumbra source. Choose Create new mod.";

    /// <summary>What an Add can create, in menu order.</summary>
    public static readonly ImmutableArray<TimelineAddKind> AddKinds =
        [TimelineAddKind.Expression, TimelineAddKind.LipSync, TimelineAddKind.Sound, TimelineAddKind.Effect, TimelineAddKind.Prop];

    public static string LaneName(TimelineLane lane) => lane switch
    {
        TimelineLane.Motion => "Motion",
        TimelineLane.Expression => "Expressions",
        TimelineLane.LipSync => "Lip sync",
        TimelineLane.Sound => "Sounds",
        TimelineLane.Effect => "Effects",
        TimelineLane.Prop => "Props",
        _ => "Other",
    };

    public static Vector4 LaneColour(TimelineLane lane) => lane switch
    {
        TimelineLane.Motion => Theme.TrackMotion,
        TimelineLane.Expression => Theme.TrackExpression,
        TimelineLane.LipSync => Theme.TrackLipSync,
        TimelineLane.Sound => Theme.TrackSound,
        TimelineLane.Effect => Theme.TrackEffect,
        TimelineLane.Prop => Theme.TrackProp,
        _ => Theme.TrackOther,
    };

    /// <summary>What a double click or "Add here" on a lane creates; null for lanes nothing is added to.</summary>
    public static TimelineAddKind? AddKind(TimelineLane lane) => lane switch
    {
        TimelineLane.Expression => TimelineAddKind.Expression,
        TimelineLane.LipSync => TimelineAddKind.LipSync,
        TimelineLane.Sound => TimelineAddKind.Sound,
        TimelineLane.Effect => TimelineAddKind.Effect,
        TimelineLane.Prop => TimelineAddKind.Prop,
        _ => null,
    };

    public static string AddKindName(TimelineAddKind kind) => kind switch
    {
        TimelineAddKind.Expression => "Expression",
        TimelineAddKind.LipSync => "Lip sync",
        TimelineAddKind.Sound => "Sound",
        TimelineAddKind.Effect => "Effect",
        _ => "Prop",
    };

    /// <summary>The picker that chooses a new path or model for <paramref name="ev"/>; null when it has none.</summary>
    public static TimelineAddKind? PickerFor(TimelineEvent ev) => ev.Magic switch
    {
        "C063" => TimelineAddKind.Sound,
        "C012" or "C173" => TimelineAddKind.Effect,
        "C198" => TimelineAddKind.Prop,
        "C009" or "C010" when ev.Lane == TimelineLane.Expression => TimelineAddKind.Expression,
        "C009" or "C010" when ev.Lane == TimelineLane.LipSync => TimelineAddKind.LipSync,
        _ => null,
    };

    /// <summary>The kind's name, such as "Sound", or the magic for a kind the schema lacks.</summary>
    public static string KindName(string magic) => TmbEntrySchema.Find(magic)?.Name ?? magic;

    /// <summary>
    /// A short readable name for an event: the file name for a path, a readable motion name for an
    /// expression or lip sync, the model for a prop and the kind's name otherwise.
    /// </summary>
    public static string EventLabel(TimelineEvent ev)
    {
        var values = ev.Values ?? ImmutableDictionary<string, double>.Empty;
        switch (ev.Magic)
        {
            case "C009" or "C010" when ev.Path.Length > 0:
                return ev.Lane is TimelineLane.Expression or TimelineLane.LipSync ? TimelineCatalogBuilder.Readable(ev.Path) : ev.Path;
            case "C198" or "C043":
                return PropModel(values.GetValueOrDefault("Model"), values.GetValueOrDefault("Body"), values.GetValueOrDefault("Variant"));
            case "C031":
                return "Prop animation " + Whole(values.GetValueOrDefault("WeaponTimeline"));
            case "C094":
                var from = values.GetValueOrDefault("StartVisibility");
                var to = values.GetValueOrDefault("EndVisibility");
                return to > from ? "Fade in" : to < from ? "Fade out" : "Hide";
        }
        if (ev.Path.Length > 0)
        {
            var name = Path.GetFileNameWithoutExtension(ev.Path.Replace('\\', '/').Split('/')[^1]);
            return name.Length > 0 ? name : ev.Path;
        }
        return KindName(ev.Magic);
    }

    /// <summary>A prop model the way the catalog lists them: "w1962 b0001 v1".</summary>
    public static string PropModel(double model, double body, double variant) =>
        $"w{Whole(model).PadLeft(4, '0')} b{Whole(body).PadLeft(4, '0')} v{Whole(variant)}";

    /// <summary>"1.2s · frame 36".</summary>
    public static string FrameText(int frames) => $"{TimelineText.Seconds(frames)} · frame {Whole(frames)}";

    /// <summary>"1s · 30 frames"; durations past the TMB range mean "until the end" in mods.</summary>
    public static string DurationText(int frames) => frames > TimelineDocument.MaxFrames
        ? "until the end"
        : $"{TimelineText.Seconds(frames)} · {Whole(frames)} {(frames == 1 ? "frame" : "frames")}";

    /// <summary>How long a face without time control shows (see <see cref="TimelineDocument.Holds"/>).</summary>
    public static string HoldText(TimelineDocument doc, TimelineEvent ev) =>
        doc.HeldEnd(ev) < doc.Length ? "Holds until the next expression" : "Holds to the end";

    /// <summary>How long a kept face shows (see <see cref="TimelineDocument.Kept"/>).</summary>
    public const string KeptText = "Stays after the timeline, until another expression replaces it";

    /// <summary>How long a looping face loops (see <see cref="TimelineDocument.Loops"/>), then what it does.</summary>
    public static string LoopText(TimelineEvent ev) =>
        $"Loops for about {LoopMinutes(ev)} minutes, then returns to neutral";

    /// <summary>The minutes a face starting at <paramref name="ev"/>'s start loops: up to the last frame a timeline reaches.</summary>
    public static int LoopMinutes(TimelineEvent ev) =>
        (int)Math.Max(1, Math.Round((TimelineDocument.MaxFrames - ev.Start) / (double)TimelineDocument.Fps / 60));

    /// <summary>
    /// The Keep switch's tooltip. A moving face (a mod's animated one) loops on an expression's
    /// timeline; anywhere else it holds still once kept.
    /// </summary>
    public static string KeepTip(TimelineEvent ev, bool moves, bool loops) =>
        loops ? $"Keep this face on after the expression ends. It loops for about {LoopMinutes(ev)} minutes, then returns to neutral. Another expression replaces it sooner."
        : moves ? "Keep this face on after the timeline ends, until another expression replaces it. It holds still once kept."
        : "Keep this face on after the timeline ends, until another expression replaces it.";

    /// <summary>"2.5s · 75 frames", with the old length when it changed.</summary>
    public static string LengthText(TimelineDocument doc)
    {
        var text = $"{TimelineText.Seconds(doc.Length)} · {Whole(doc.Length)} frames";
        return doc.LengthChanged ? $"{text} · was {TimelineText.Seconds(doc.SourceLength)}" : text;
    }

    /// <summary>
    /// A note when the file's header gives another length than the motion's (mods only), or null.
    /// It hides while the length is changed, when the motion's loaded length no longer holds.
    /// </summary>
    public static string? HeaderNote(TimelineDocument doc) =>
        doc.LengthChanged || doc.HeaderLength == doc.SourceLength
            ? null
            : doc.HeaderLength < 0
                ? "The file's header gives an endless length (-1). It stays that way."
                : $"The file's header says {TimelineText.Seconds(doc.HeaderLength)}, but the motion lasts {TimelineText.Seconds(doc.SourceLength)}.";

    /// <summary>A field's value as read-only text.</summary>
    public static string FieldText(TimelineField field, double value)
    {
        switch (field.Kind)
        {
            case TimelineFieldKind.Bool:
                return value != 0 ? "Yes" : "No";
            case TimelineFieldKind.Choice:
                foreach (var (label, option) in field.Options)
                    if (option == value)
                        return label;
                return Whole(value);
            case TimelineFieldKind.Flags:
                var bits = (long)value;
                var names = new List<string>();
                foreach (var (label, option) in field.Options)
                    if ((bits & (long)option) != 0)
                        names.Add(label);
                return names.Count == 0 ? "None" : string.Join(", ", names);
            case TimelineFieldKind.Float:
                return value.ToString("0.###", CultureInfo.InvariantCulture);
            default:
                return Whole(value);
        }
    }

    /// <summary>
    /// What stops a save, shown above the Save button; null when nothing does. Events past the end
    /// only warn (see <see cref="PastEndWarning"/>).
    /// </summary>
    public static string? SaveProblem(bool editable, string? readOnlyReason, bool inPlace, bool canReplace, bool lengthChanged,
        SkeletonResolution? resolution)
    {
        if (!editable)
            return readOnlyReason ?? "This timeline can't be edited.";
        if (inPlace && !canReplace)
            return NoWritableSource;
        if (lengthChanged && resolution is not { State: SkeletonResolutionState.Matched, Selected: not null })
            return resolution?.Reason ?? "Choose the skeleton this animation was made for before changing its length.";
        return null;
    }

    /// <summary>
    /// A warning about events that start at or after the motion's end, or null when none do. Vanilla
    /// never has any; whether the game still plays them is untested, so the warning doesn't promise.
    /// </summary>
    public static string? PastEndWarning(TimelineDocument doc)
    {
        var count = TimelineEdits.PastEnd(doc).Length;
        return count switch
        {
            0 => null,
            1 => "1 event starts after the end and may not play.",
            _ => $"{Whole(count)} events start after the end and may not play.",
        };
    }

    /// <summary>The warning about effects that stop although the animation goes on, or null when none do.</summary>
    public static string? EffectLoopWarning(ImmutableArray<TimelineEffectCheck.Problem> problems)
    {
        var names = problems.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count switch
        {
            0 => null,
            1 => string.Join(" ", problems.Select(p => p.Text)),
            _ => $"{Whole(names.Count)} effects stop{(problems.All(p => p.FirstLoop) ? " after their first loop" : "")}: {string.Join(", ", names)}.",
        };
    }

    /// <summary>
    /// The catalog key for a clip: the player's face skeleton among the loaded files, else the clip's
    /// race with face <see cref="DefaultFace"/>, so sounds, effects and props still list. Null when
    /// the clip names no human race.
    /// </summary>
    public static TimelineCatalogKey? CatalogKey(IEnumerable<string> loadedPaths, string clipGamePath, string? modRoot)
    {
        foreach (var path in loadedPaths)
            if (path.Contains("/skeleton/face/", StringComparison.OrdinalIgnoreCase) &&
                TimelineCatalogKey.FromFaceSkeleton(path, SafeRoot(modRoot)) is { } key)
                return key;
        if (RaceOf(clipGamePath) is not { } race)
            return null;
        try { return new TimelineCatalogKey(race, DefaultFace, SafeRoot(modRoot)); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>The race number in a chara/human/cXXXX/ path, or null.</summary>
    public static ushort? RaceOf(string gamePath)
    {
        var parts = gamePath.Replace('\\', '/').Split('/');
        for (var i = 0; i + 2 < parts.Length; i++)
            if (parts[i].Equals("chara", StringComparison.OrdinalIgnoreCase) && parts[i + 1].Equals("human", StringComparison.OrdinalIgnoreCase) &&
                parts[i + 2] is { Length: 5 } race && race[0] is 'c' or 'C' &&
                ushort.TryParse(race.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code is > 0 and <= 9999)
                return code;
        return null;
    }

    /// <summary>
    /// The files a save or preview packages: every file of each import whose game path an event of
    /// <paramref name="doc"/> still uses. Imports nothing uses any more are left out. Two imports
    /// needing different files at one path (effects name their textures' paths themselves) are
    /// refused rather than letting one silently replace the other.
    /// </summary>
    public static ImmutableDictionary<string, byte[]> AddedFiles(TimelineDocument doc, IEnumerable<TimelineImport> imports)
    {
        var files = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var import in Used(doc, imports))
            foreach (var (gamePath, bytes) in import.Files)
            {
                if (files.TryGetValue(gamePath, out var other) && !other.AsSpan().SequenceEqual(bytes))
                    throw new InvalidDataException($"Two imports need different files at {gamePath}. Remove one of them.");
                files[gamePath] = bytes;
            }
        return files.ToImmutable();
    }

    /// <summary>
    /// For the files <see cref="AddedFiles"/> packages that are edited copies, the game path of the
    /// file each copies (<see cref="TimelineImport.Beside"/>), so a save maps them where it is.
    /// </summary>
    public static ImmutableDictionary<string, string> AddedBeside(TimelineDocument doc, IEnumerable<TimelineImport> imports)
    {
        var beside = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var import in Used(doc, imports))
            foreach (var (gamePath, original) in import.Beside ?? ImmutableDictionary<string, string>.Empty)
                if (import.Files.ContainsKey(gamePath))
                    beside[gamePath] = original;
        return beside.ToImmutable();
    }

    /// <summary>The imports whose game path an event of <paramref name="doc"/> uses.</summary>
    private static IEnumerable<TimelineImport> Used(TimelineDocument doc, IEnumerable<TimelineImport> imports)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!doc.Events.IsDefault)
            foreach (var ev in doc.Events)
                if (ev.Path.Length > 0)
                    used.Add(ev.Path);
        return imports.Where(import => used.Contains(import.GamePath));
    }

    /// <summary>
    /// A game path typed for a sound or effect, lowercased with forward slashes; null with a short
    /// reason when it is not one the game can load or has the wrong extension.
    /// </summary>
    public static string? GamePath(string text, TimelineAddKind kind, out string? error)
    {
        error = null;
        var extension = kind switch { TimelineAddKind.Sound => ".scd", TimelineAddKind.Effect => ".avfx", _ => null };
        if (extension == null)
        {
            error = "Only sounds and effects take a game path.";
            return null;
        }
        var path = text.Trim().Trim('"').Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        if (path.Length == 0)
        {
            error = "Enter a game path first.";
            return null;
        }
        if (!path.EndsWith(extension, StringComparison.Ordinal))
        {
            error = $"A {AddKindName(kind).ToLowerInvariant()} path ends in {extension}.";
            return null;
        }
        if (!AnimationDependencies.SafeGamePath(path))
        {
            error = "The game can't load that path. Use letters, digits, / _ - and . only.";
            return null;
        }
        return path;
    }

    /// <summary>
    /// The expression library the timeline uses after choosing an item that needs
    /// <paramref name="itemPack"/>; a timeline loads only one, so a different one is refused.
    /// </summary>
    public static string? PackAfter(string? documentPack, string? itemPack, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(itemPack) || string.Equals(documentPack, itemPack, StringComparison.OrdinalIgnoreCase))
            return documentPack;
        if (string.IsNullOrEmpty(documentPack))
            return itemPack;
        error = $"This timeline already uses the {documentPack} expressions. It can only use one set.";
        return documentPack;
    }

    /// <summary>Seconds as whole frames, rounded half away from zero.</summary>
    public static int Frames(float seconds) => TimelineSnap.Round(seconds * (double)TimelineDocument.Fps);

    /// <summary>How many frames a sound of <paramref name="seconds"/> takes, rounded up; 0 when unknown.</summary>
    public static int SoundFrames(float? seconds) =>
        seconds is { } value && float.IsFinite(value) && value > 0
            ? (int)Math.Min(TimelineDocument.MaxFrames, Math.Ceiling(value * (double)TimelineDocument.Fps))
            : 0;

    /// <summary>The mod name a timeline save suggests.</summary>
    public static string DefaultModName(string clipModName) => clipModName + " Timeline";

    private static string? SafeRoot(string? modRoot)
    {
        if (string.IsNullOrWhiteSpace(modRoot))
            return null;
        try { return Path.IsPathFullyQualified(modRoot) ? modRoot : null; }
        catch (ArgumentException) { return null; }
    }

    private static string Whole(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
}
