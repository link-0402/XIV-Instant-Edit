using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// The Timeline window's words and choices: lane names and colours, event labels, why a save is
/// blocked, file dialog filters, the catalog key and the files a save packages. No ImGui or
/// Dalamud dependencies, so the regression suite can check them.
/// </summary>
internal static partial class TimelineWindowViews
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

    /// <summary>What the Add menu's bone layer items create, in menu order.</summary>
    public static readonly ImmutableArray<MotionLayerKind> LayerKinds = [MotionLayerKind.Offset, MotionLayerKind.Amplitude, MotionLayerKind.Pose];

    /// <summary>The bone layer rows' legend when there is no layer yet.</summary>
    public const string BonesLaneName = "Bones";

    public static Vector4 LayerColour(MotionLayerKind kind) => kind switch
    {
        MotionLayerKind.Offset => Theme.Info,
        MotionLayerKind.Amplitude => Theme.Watching,
        _ => Theme.Important,
    };

    /// <summary>What a layer kind does, for the Add menu and tooltips.</summary>
    public static string LayerKindHint(MotionLayerKind kind) => kind switch
    {
        MotionLayerKind.Offset => "Turn, move or scale bones over a section, by keys you set.",
        MotionLayerKind.Amplitude => "Make bones move more or less around their average pose.",
        _ => "Blend bones toward poses you key.",
    };

    /// <summary>The amount as the panel and bar show it: "150%".</summary>
    public static string AmountText(MotionLayer layer) =>
        Math.Round(layer.Amount * 100, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>A layer's bar label: its name, its amount when it isn't full strength, "off" when turned off.</summary>
    public static string LayerLabel(MotionLayer layer) =>
        !layer.Enabled ? layer.Name + " · off"
        : Math.Abs(layer.Amount - 1) > 1e-4f || layer.Kind == MotionLayerKind.Amplitude ? $"{layer.Name} · {AmountText(layer)}"
        : layer.Name;

    /// <summary>
    /// A warning when the active bone layers move bones outside the IVCS + YAS layout that the clip
    /// doesn't animate already: saving gives them tracks at indices other players' skeletons may lack,
    /// and sync plugins refuse such files. Null when none do, or before the game's skeleton is known.
    /// </summary>
    public static string? LayerBonesWarning(TimelineDocument doc, AnimationClip clip)
    {
        if (!doc.MotionChanged || AnimationBones.Standard(clip, SkeletonStandard.Vanilla) is not { } game)
            return null;
        var vanilla = game.Skeleton.Bones.Select(bone => bone.Name).ToHashSet(StringComparer.Ordinal);
        var animated = AnimationBones.Animated(clip).ToHashSet(StringComparer.Ordinal);
        var moved = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var layer in doc.Motion.Where(MotionEvaluator.Active))
        {
            // An amplitude only scales motion that is there, so it never adds a track.
            if (layer.Kind == MotionLayerKind.Amplitude)
                continue;
            foreach (var key in layer.Keys)
                foreach (var bone in key.Bones.Keys)
                    if (layer.Bones.IsEmpty || layer.Bones.Contains(bone))
                        moved.Add(bone);
        }
        var outside = moved.Where(bone => !animated.Contains(bone) && !AnimationBones.InStandard(bone, SkeletonStandard.IvcsYas, vanilla)).ToArray();
        if (outside.Length == 0)
            return null;
        var names = string.Join(", ", outside.Take(3).Select(AnimationPresentation.BoneName)) + (outside.Length > 3 ? $" and {outside.Length - 3} more" : "");
        return $"Bone layers move {names}, which other players' skeletons may not have. Sync plugins can refuse the saved file.";
    }

    /// <summary>How many keys and bones a layer has, for its tooltip; an amplitude has no keys, only the bones it is limited to.</summary>
    public static string LayerContent(MotionLayer layer)
    {
        var bones = layer.Kind == MotionLayerKind.Amplitude
            ? layer.Bones.Length
            : layer.Keys.SelectMany(key => key.Bones.Keys).Distinct(StringComparer.Ordinal).Count();
        if (layer.Kind == MotionLayerKind.Amplitude)
            return bones == 0 ? "Every bone" : bones == 1 ? "1 bone" : $"{Whole(bones)} bones";
        var keys = layer.Keys.Length;
        return (keys == 1 ? "1 key" : $"{Whole(keys)} keys") + " · " + (bones == 1 ? "1 bone" : $"{Whole(bones)} bones");
    }

    /// <summary>The ways a layer can take its keys to the next one, in menu order. A key can also have its own curve.</summary>
    public static readonly ImmutableArray<MotionInterpolation> LayerEases =
        [MotionInterpolation.Linear, MotionInterpolation.Smooth, MotionInterpolation.EaseIn, MotionInterpolation.EaseOut, MotionInterpolation.Step];

    public static string EaseName(MotionInterpolation mode) => mode switch
    {
        MotionInterpolation.Linear => "Linear",
        MotionInterpolation.Smooth => "Smooth",
        MotionInterpolation.EaseIn => "Slow start",
        MotionInterpolation.EaseOut => "Slow end",
        MotionInterpolation.Step => "Hold",
        _ => "Custom",
    };

    public static string EaseHint(MotionInterpolation mode) => mode switch
    {
        MotionInterpolation.Linear => "Same speed all the way.",
        MotionInterpolation.Smooth => "Starts slow and ends slow.",
        MotionInterpolation.EaseIn => "Starts slow, ends at full speed.",
        MotionInterpolation.EaseOut => "Starts at full speed, ends slow.",
        MotionInterpolation.Step => "Stays put, then jumps at the next key.",
        _ => "Your own curve. Drag its two handles.",
    };

    /// <summary>How a key goes to the next one, for its tooltip: its own way, or the layer's.</summary>
    public static string KeyEaseText(MotionLayer layer, MotionKey key) =>
        key.Ease is { } own ? "To the next key: " + EaseName(own) : $"To the next key: {EaseName(layer.Interpolation)} (the layer's)";

    /// <summary>A bone's row tooltip: how many of the layer's keys hold it.</summary>
    public static string BoneKeysText(MotionLayer layer, string bone)
    {
        var keys = layer.Keys.Count(key => key.Bones.ContainsKey(bone));
        return keys == 1 ? "1 key in this layer" : $"{Whole(keys)} keys in this layer";
    }

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
    /// timeline; anywhere else, or when <paramref name="tooShort"/> to loop, it holds still once kept.
    /// </summary>
    public static string KeepTip(TimelineEvent ev, bool moves, bool loops, bool tooShort = false) =>
        loops ? $"Keep this face on after the expression ends. It loops for about {LoopMinutes(ev)} minutes, then returns to neutral. Another expression replaces it sooner."
        : tooShort ? "Keep this face on after the timeline ends, until another expression replaces it. It holds still once kept. " + TimelineDocument.TooShortToLoop
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
        SkeletonResolution? resolution, bool motionChanged = false)
    {
        if (!editable)
            return readOnlyReason ?? "This timeline can't be edited.";
        if (inPlace && !canReplace)
            return NoWritableSource;
        // A new length and bone layers both bake the motion, on the skeleton the clip was made for.
        if ((lengthChanged || motionChanged) && resolution is not { State: SkeletonResolutionState.Matched, Selected: not null })
            return resolution?.Reason ?? (lengthChanged
                ? "Choose the skeleton this animation was made for before changing its length."
                : "Choose the skeleton this animation was made for before changing its motion.");
        return null;
    }

    /// <summary>
    /// What stops saving with <paramref name="doc"/>'s bones left out and a repair onto <paramref name="repair"/>,
    /// as the old rebakes refused them; null when nothing does. Bone layers on a bone left out would be lost,
    /// and a repair onto the skeleton the clip was already made for changes nothing.
    /// </summary>
    public static string? BonesProblem(TimelineDocument doc, AnimationClip clip, SkeletonStandard? repair)
    {
        var excluded = doc.ExcludedBones;
        if (AnimationBones.Problem(clip, excluded) is { } exclusion)
            return exclusion;
        if (!excluded.IsEmpty)
        {
            var left = excluded.ToHashSet(StringComparer.Ordinal);
            var moved = doc.Motion.Where(MotionEvaluator.Active)
                .SelectMany(layer => layer.Kind == MotionLayerKind.Amplitude ? layer.Bones.AsEnumerable() : layer.Keys.SelectMany(key => key.Bones.Keys))
                .Where(left.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (moved.Length > 0)
                return $"Bone layers move {string.Join(", ", moved.Take(3).Select(AnimationPresentation.BoneName))}" +
                       (moved.Length > 3 ? $" and {moved.Length - 3} more" : "") + ", which you unticked under Bones. Tick it again or take it out of the layer.";
        }
        if (repair is not { } target)
            return null;
        var name = AnimationBones.StandardName(target);
        if (AnimationBones.Plan(clip, target, excluded) is not { } plan)
            return $"No {name} skeleton was found for this animation's model.";
        if (plan.Kept.IsEmpty)
            return $"None of the bones this animation moves are on the {name} skeleton.";
        if (excluded.IsEmpty && clip.Resolution?.Selected?.Skeleton.Fingerprint == plan.Target.Skeleton.Fingerprint)
            return $"The animation was already made for the {name} skeleton. Choose Keep its own.";
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

    /// <summary>
    /// The newest capture of <paramref name="current"/>'s clip in <paramref name="history"/> (newest first) when it heard a
    /// file other than the one read and the one opened: the file changed in the mods since. Null when unknown or unchanged.
    /// Captures that heard a preview's temporary file are skipped: after a preview stops, the character goes back to its
    /// idle, so the capture heard during the preview stays the clip's newest (seen in game 2026-10-14).
    /// </summary>
    public static AnimationCapture? ChangedCapture(IEnumerable<AnimationCapture> history, AnimationTimelineSource current)
    {
        var newest = history.FirstOrDefault(capture => capture.Id == current.Capture.Id &&
                                                       !AnimationPreviewRules.IsPreviewFile(SourceOf(current, capture)?.ResolvedPath));
        return newest != null && SourceOf(current, newest)?.Hash is { Length: > 0 } hash &&
               hash != current.Resource.Hash && hash != SourceOf(current, current.Capture)?.Hash
            ? newest
            : null;
    }

    /// <summary>The file <paramref name="capture"/> heard for the open clip: an expression's own timeline, else the clip's animation.</summary>
    private static AnimationResource? SourceOf(AnimationTimelineSource current, AnimationCapture capture) =>
        current.Standalone
            ? capture.ExpressionTimeline is { } timeline && timeline.GamePath == current.Resource.GamePath ? timeline : null
            : capture.Sources.FirstOrDefault(resource => resource.GamePath == current.Clip.GamePath);

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

    /// <summary>
    /// How each added copy the save packages is built again from its original as the save finds it
    /// (<see cref="TimelineImport.Rebuild"/>), by game path, for the imports <paramref name="doc"/> uses.
    /// </summary>
    public static ImmutableDictionary<string, Func<byte[], byte[]>> AddedRebuild(TimelineDocument doc, IEnumerable<TimelineImport> imports)
    {
        var rebuild = ImmutableDictionary.CreateBuilder<string, Func<byte[], byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var import in Used(doc, imports))
            foreach (var (gamePath, build) in import.Rebuild ?? ImmutableDictionary<string, Func<byte[], byte[]>>.Empty)
                if (import.Files.ContainsKey(gamePath))
                    rebuild[gamePath] = build;
        return rebuild.ToImmutable();
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

    /// <summary>The clip band's legend.</summary>
    public const string ClipLegend = "Clip";

    /// <summary>The clip band's right-click menu: cut at the pointer, remove to the part's end or from its start, remove the part.</summary>
    public static readonly ImmutableArray<string> ClipMenu = ["Cut here", "Remove from here to the end", "Remove from the start to here", "Remove part"];

    /// <summary>A part's label on the clip band: the motion's own frames it keeps.</summary>
    public static string PartLabel(MotionPart part) => $"{part.From} - {part.To}";

    /// <summary>The ruler's mark where frames were cut out: how many.</summary>
    public static string RemovedMarker(int frames) => $"-{frames}";

    /// <summary>What a cut took with it, for the status line; null when it took nothing.</summary>
    public static string? RemovalStatus(CutRemoval removed)
    {
        var events = removed.Events == 1 ? "1 event" : $"{removed.Events} events";
        var keys = removed.Keys == 1 ? "1 key" : $"{removed.Keys} keys";
        return (removed.Events, removed.Keys) switch
        {
            (0, 0) => null,
            (_, 0) => $"Removed {events} with the cut.",
            (0, _) => $"Removed {keys} with the cut.",
            _ => $"Removed {events} and {keys} with the cut.",
        };
    }

    /// <summary>The loop seam's badge at the end of the clip band.</summary>
    public static string SeamBadge(SeamState state) => state switch
    {
        SeamState.Pops => "Pops",
        SeamState.Stalls => "Stalls",
        _ => "Loops cleanly",
    };

    /// <summary>The jump a join or the seam blends away: the pose gap and the speeds on both sides.</summary>
    public static string GapText(JoinGap gap) => string.Create(CultureInfo.InvariantCulture,
        $"Pose gap {gap.MeanAngle:0.0} deg{(gap.WorstBone.Length > 0 ? string.Create(CultureInfo.InvariantCulture, $", worst {gap.WorstBone} {gap.WorstAngle:0.0} deg") : "")}. " +
        $"Speed in {gap.SpeedIn:0.00}, out {gap.SpeedOut:0.00} deg per frame.");

    /// <summary>
    /// Under the preview bar while Live is on. Live plays the clip's own timeline, so with
    /// <paramref name="retimed"/> motion (cut or stretched) its sounds and effects keep their old frames.
    /// </summary>
    public static string LiveHint(bool retimed) => retimed
        ? "Live shows the motion. Sounds and effects keep their old timing until Preview."
        : "Live shows bone layers and effect looks. Preview shows sounds, effects and looks that differ per event too.";

    /// <summary>A loop that slows to a stop at its end: over how many frames.</summary>
    public static string StallText(int frames) => $"The motion holds still before the loop starts again (last {frames} frames).";

    /// <summary>Which side of a join its blend changes.</summary>
    public static string PlacementName(JoinPlacement placement) => placement switch
    {
        JoinPlacement.Before => "Before",
        JoinPlacement.After => "After",
        _ => "Centered",
    };

    /// <summary>Under the seam's place when it isn't Before: the loop's own start changes.</summary>
    public const string SeamPlacementNote = "This changes the loop's first frames, which the startup leads into.";

    /// <summary>The Fix loop button's tooltip.</summary>
    public const string FixLoopTip = "Trims an end that holds still, blends a jump in pose and eases a jump in speed.";

    /// <summary>A loop whose end and start move at different speeds: which way the speed jumps.</summary>
    public static string JumpText(float speedIn, float speedOut) => speedIn < speedOut
        ? "The loop starts again faster than it ends."
        : "The loop starts again slower than it ends.";

    /// <summary>The loop seam's ease field in the side panel, its sides and its tooltip.</summary>
    public const string EaseLegend = "Ease";

    public static readonly string[] EaseSides = ["Start", "End"];

    public const string EaseTip = "The faster side plays at the slower side's speed and catches up within these frames. The length stays.";

    /// <summary>Fix loop's tooltip while there is nothing to fix.</summary>
    public const string LoopCleanTip = "The loop already runs cleanly.";

    /// <summary>A part's frames: the motion's own it keeps, and where it plays now.</summary>
    public static string PartRange(MotionPart part, double start, double end) => string.Create(CultureInfo.InvariantCulture,
        $"Source frames {part.From} to {part.To}. Plays at {start:0} to {end:0}.");

    /// <summary>The warning mark's tooltip on a sound that plays across a cut.</summary>
    public const string CrossesCutTip = "This sound plays across a cut.";

    /// <summary>What a sound across a cut does: its audio is cut with the motion, or it plays whole.</summary>
    public static readonly ImmutableArray<string> SoundCutChoices = ["Cut with the motion", "Leave whole"];

    /// <summary>Under a sound across a cut that plays whole.</summary>
    public const string LeaveWholeNote = "Plays whole. After the cut it is out of step with the motion.";

    /// <summary>Why saving waits: sounds across a cut without a choice.</summary>
    public static string SoundCutsFooter(int sounds) => sounds == 1
        ? "1 sound plays across a cut. Choose before saving."
        : $"{sounds} sounds play across a cut. Choose for each one before saving.";

    /// <summary>A blend longer than the parts around it can take: the frames it really uses.</summary>
    public static string ShortenedText(double used) => string.Create(CultureInfo.InvariantCulture, $"Uses {used:0} frames: the parts around it are shorter.");

    /// <summary>Where a sound's or effect's game path comes from, under its field.</summary>
    public static string PathOriginText(TimelinePathOrigin origin, string? modName) => origin switch
    {
        TimelinePathOrigin.Game => "Game file",
        TimelinePathOrigin.Mod => $"From mod: {modName}",
        _ => "Not in your collection. It plays only for people who have a mod with this file, such as a music library.",
    };

    /// <summary>
    /// <paramref name="ev"/> playing the game path typed in its File field; the same event when the
    /// path doesn't change, null with the reason <see cref="GamePath"/> gives when it isn't one.
    /// </summary>
    public static TimelineEvent? WithPath(TimelineEvent ev, string text, out string? error)
    {
        // A kind without a game path (an expression, a prop) gets GamePath's own refusal.
        var path = GamePath(text, PickerFor(ev) ?? TimelineAddKind.Prop, out error);
        if (path == null)
            return null;
        return string.Equals(path, ev.Path, StringComparison.Ordinal) ? ev : ev with { Path = path };
    }

    /// <summary>
    /// The expression library the timeline uses after choosing an item that needs
    /// <paramref name="itemPack"/>; a timeline loads only one, so a different one is refused while
    /// a face still plays from it (<paramref name="documentPackInUse"/>).
    /// </summary>
    public static string? PackAfter(string? documentPack, string? itemPack, out string? error, bool documentPackInUse = true)
    {
        error = null;
        if (string.IsNullOrEmpty(itemPack) || string.Equals(documentPack, itemPack, StringComparison.OrdinalIgnoreCase))
            return documentPack;
        if (string.IsNullOrEmpty(documentPack) || !documentPackInUse)
            return itemPack;
        error = TimelineEdits.PackInUse(documentPack);
        return documentPack;
    }

    /// <summary>
    /// The library to place a face from, of the ones that hold it (<paramref name="packs"/>, the
    /// preferred first): none for a face always loaded, the timeline's own when it holds the face,
    /// else the preferred one.
    /// </summary>
    public static string? PackFor(ImmutableArray<string> packs, string? documentPack) =>
        packs.IsDefaultOrEmpty ? null
        : !string.IsNullOrEmpty(documentPack) && packs.Contains(documentPack, StringComparer.OrdinalIgnoreCase) ? documentPack
        : packs[0];

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
