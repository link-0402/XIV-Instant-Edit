using System.Collections.Immutable;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>The row of the timeline an event is drawn in.</summary>
internal enum TimelineLane : byte { Motion, Expression, LipSync, Sound, Effect, Prop, Other }

/// <summary>What the editor lets the user do with an event.</summary>
internal enum TimelineAccess : byte
{
    /// <summary>Move, resize when <see cref="TimelineEvent.Resizable"/>, edit its fields, duplicate and delete.</summary>
    Editable,
    /// <summary>Move and delete only: a harmless kind the editor does not offer to add.</summary>
    Movable,
    /// <summary>Shown, never changed by the user: the clip's own motion, or a kind that affects gameplay.</summary>
    Locked,
}

/// <summary>
/// One entry of an animation's timeline (a TMB C-entry). Times are whole frames at
/// <see cref="TimelineDocument.Fps"/> frames per second.
/// </summary>
/// <param name="Key">"t" + the TMB entry id for a loaded entry, "n" + a number for one added in the editor, "" before it is added.</param>
/// <param name="Magic">The TMB entry kind, such as "C063".</param>
/// <param name="Start">The entry's TMB time, 0 to <see cref="TimelineDocument.MaxFrames"/>.</param>
/// <param name="Duration">How many frames the entry lasts when its kind has a duration field, else 0.</param>
/// <param name="Resizable">Whether <paramref name="Duration"/> is a length the user may change.</param>
/// <param name="Path">The entry's string: a file path or a motion name. Empty when its kind has none.</param>
/// <param name="LockReason">A short reason shown for Movable and Locked entries.</param>
/// <param name="Conditional">The entry's TMB track only plays under a condition.</param>
/// <param name="Values">The kind's other typed fields by schema name, such as "SoundIndex" or "Scale.X".</param>
/// <param name="Group">Shared by the entries one Add created together, such as a prop's parts; null when alone.</param>
/// <param name="CloneOf">For a copy of a loaded entry, that entry's "t" key: saving copies every byte of it, not only the fields shown.</param>
/// <param name="Loop">A kept moving face that loops (see <see cref="TimelineDocument.Loops"/>): saved as copies of itself back to back.</param>
/// <param name="Look">An effect's changed look (tint, size, colours): saving writes it into a copy of the effect (<see cref="TimelineEffectLooks.Bake"/>); null when unchanged.</param>
/// <param name="Pin">A sound event that always plays one alternative of its random set: that alternative's id (<see cref="TimelineSoundAlternative.Id"/>); null plays any. Saving gives it a copy of the file holding only that one.</param>
internal sealed record TimelineEvent(
    string Key,
    string Magic,
    TimelineLane Lane,
    int Start,
    int Duration,
    bool Resizable,
    string Path,
    TimelineAccess Access,
    string? LockReason,
    bool Conditional,
    ImmutableDictionary<string, double> Values,
    string? Group = null,
    string? CloneOf = null,
    bool Loop = false,
    TimelineEffectEdit? Look = null,
    string? Pin = null);

/// <summary>
/// One timeline being edited. Immutable: every edit makes a new document, so undo keeps
/// whole snapshots and a no-op edit returns the same instance.
/// </summary>
/// <param name="Length">The clip's length in frames: its motion lasts this long. Changing it resamples the motion.</param>
/// <param name="SourceLength">The length when the timeline was loaded.</param>
/// <param name="FacePack">The expression library the timeline loads (TMB TMPP), such as "emot/laugh"; null for none.</param>
/// <param name="Events">The entries in TMB item order; added entries go at the end.</param>
/// <param name="NextKey">The number the next added entry's "n" key uses.</param>
internal sealed record TimelineDocument(
    int Length,
    int SourceLength,
    string? FacePack,
    ImmutableArray<TimelineEvent> Events,
    int NextKey)
{
    public const int Fps = 30;
    public const int MaxFrames = short.MaxValue;
    /// <summary>The shortest length the editor allows, in frames.</summary>
    public const int MinLength = 2;
    /// <summary>The longest length the editor allows, in frames (10 minutes).</summary>
    public const int MaxLength = 18000;
    public bool LengthChanged => Length != SourceLength;

    /// <summary>
    /// How the clip's motion is cut (see <see cref="MotionCuts"/>): the kept parts of the motion in its
    /// own frames, their joins and the loop seam's blend. Null keeps the motion whole.
    /// </summary>
    public MotionCuts? Cuts { get; init; }

    /// <summary>The length of the motion the cuts are made in, in frames: the loaded length unless the cuts say otherwise.</summary>
    public int MotionLength => Cuts?.Source ?? SourceLength;

    /// <summary>How many of the motion's frames the cuts keep; they play over <see cref="Length"/>.</summary>
    public int KeptFrames => Cuts?.Kept ?? MotionLength;

    /// <summary>Whether the motion is cut: a part removed or trimmed, or a loop seam blend set.</summary>
    public bool Cut => Cuts is { Whole: false };

    /// <summary>Whether the kept motion plays faster or slower than it was made: <see cref="Length"/> isn't <see cref="KeptFrames"/>.</summary>
    public bool Stretched => Length != KeptFrames;

    /// <summary>
    /// For sounds that play across a cut (see <see cref="TimelineSoundCuts"/>), by event key: true cuts the
    /// sound's audio with the motion, false leaves it whole. A crossing sound without a choice holds back
    /// saving and previewing.
    /// </summary>
    public ImmutableDictionary<string, bool> SoundCuts
    {
        get => soundCuts;
        init => soundCuts = value ?? ImmutableDictionary<string, bool>.Empty.WithComparers(StringComparer.Ordinal);
    }

    private readonly ImmutableDictionary<string, bool> soundCuts = ImmutableDictionary<string, bool>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>
    /// The bone layers that change the clip's motion, applied in this order (see
    /// <see cref="MotionEvaluator"/>). Saving bakes them into the motion; empty when the motion is not edited.
    /// </summary>
    public ImmutableArray<MotionLayer> Motion { get => motion; init => motion = value.IsDefault ? [] : value; }

    private readonly ImmutableArray<MotionLayer> motion = [];

    /// <summary>The number the next added layer's "m" key uses.</summary>
    public int NextMotionKey { get; init; } = 1;

    /// <summary>Whether saving changes the motion through its bone layers.</summary>
    public bool MotionChanged => MotionEvaluator.Changes(Motion);

    /// <summary>
    /// Bones the clip animates that saving leaves without a track, by name, in order: nothing in the
    /// animation drives them any more, so physics or the skeleton's rest takes them back. Empty for none.
    /// </summary>
    public ImmutableArray<string> ExcludedBones { get => excluded; init => excluded = value.IsDefault ? [] : value; }

    private readonly ImmutableArray<string> excluded = [];

    /// <summary>
    /// Changes to the sounds the events play, by file and sound index (paths in any case). Saving,
    /// previewing and listening write them into the files (<see cref="TimelineSoundEdits"/>).
    /// </summary>
    public ImmutableDictionary<TimelineSoundKey, TimelineSoundEdit> SoundEdits
    {
        get => soundEdits;
        init => soundEdits = value ?? ImmutableDictionary<TimelineSoundKey, TimelineSoundEdit>.Empty;
    }

    private readonly ImmutableDictionary<TimelineSoundKey, TimelineSoundEdit> soundEdits = ImmutableDictionary<TimelineSoundKey, TimelineSoundEdit>.Empty;

    /// <summary>
    /// An expression's own timeline (chara/action/facial/pose/smile.tmb), not a clip's: it has no
    /// motion, so its length can't change, and a kept moving face on it loops (<see cref="Loops"/>).
    /// </summary>
    public bool Expression { get; init; }

    /// <summary>
    /// The length the file's header (TMB TMDH) gave when loaded. Vanilla's matches the motion's, but
    /// mods often keep one copied from another timeline: a 15 s dance whose header says 30 frames
    /// plays fine in game. See <see cref="SavedHeaderLength"/>.
    /// </summary>
    public int HeaderLength { get; init; } = SourceLength;

    /// <summary>
    /// The header length a save writes. It stays as loaded unless the length changes. Then a header
    /// that matched the motion (every vanilla one), or said nothing (0), becomes the new length, and
    /// a mod's own scales with the motion: one laid out longer than its motion, with events up to
    /// its end, stays that much longer, in case the game times the events by it.
    /// </summary>
    public int SavedHeaderLength =>
        !LengthChanged ? HeaderLength
        : HeaderLength == SourceLength || HeaderLength <= 0 || SourceLength <= 0 ? Length
        : (int)Math.Clamp(Math.Round(HeaderLength * (double)Length / SourceLength, MidpointRounding.AwayFromZero), 1, MaxFrames);

    /// <summary>
    /// Whether a motion entry lasting <paramref name="frames"/> lasts the whole clip. Files often count
    /// the clip one frame longer than its motion (a few vanilla ones too, many mods).
    /// </summary>
    public bool LastsWhole(int frames) => SameLength(frames, Length);

    /// <summary>Lengths one frame apart are the same clip counted differently.</summary>
    public static bool SameLength(int a, int b) => b > 0 && Math.Abs((long)a - b) <= 1;

    /// <summary>
    /// Whether the game plays <paramref name="ev"/> at its own speed: a face, blink or lip motion
    /// (C010) without time control. The game then ignores the length and frame range it stores.
    /// </summary>
    public static bool OwnPace(TimelineEvent ev) =>
        ev.Magic == "C010" && ev.Lane is TimelineLane.Expression or TimelineLane.LipSync && ev.Values is { } values &&
        values.TryGetValue(TmbEntrySchema.TimeControlField, out var timed) && timed == 0;

    /// <summary>
    /// Whether <paramref name="ev"/> is a face (C010 "cfxf_") at its own speed (<see cref="OwnPace"/>).
    /// It shows at once (a pose is a constant clip) and stays until another face takes over or the
    /// clip ends, see <see cref="HeldEnd"/>.
    /// </summary>
    public static bool Holds(TimelineEvent ev) => OwnPace(ev) && IsFace(ev);

    /// <summary>
    /// How many frames the game plays <paramref name="ev"/> for: a holding pose until
    /// <see cref="HeldEnd"/>, a moving face (<see cref="Moves"/>) or another motion at its own speed
    /// its own length when <paramref name="motionFrames"/> (the face's animation files, by motion
    /// name) knows it, and anything else the length it stores. A moving face plays once at its own
    /// length, so giving it time control with this length keeps its pace.
    /// </summary>
    public int PlayedFrames(TimelineEvent ev, IReadOnlyDictionary<string, int>? motionFrames = null) =>
        Kept(ev) || Holds(ev) && Moves(ev, motionFrames) == null ? HeldEnd(ev) - ev.Start
        : OwnPace(ev) && motionFrames != null && motionFrames.TryGetValue(ev.Path, out var frames) && frames > 0 ? frames
        : ev.Duration;

    /// <summary>
    /// Where a holding face (<see cref="Holds"/>) stops showing: the start of the next face, else the
    /// end of the clip, and never before the frame after its own start. A kept face
    /// (<see cref="Kept"/>) is drawn to the same place, though it outlasts the clip.
    /// </summary>
    public int HeldEnd(TimelineEvent ev)
    {
        var end = Length;
        if (!Events.IsDefault)
            foreach (var other in Events)
                if (other.Start > ev.Start && other.Start < end && IsFace(other))
                    end = other.Start;
        return Math.Max(end, ev.Start + 1);
    }

    private static bool IsFace(TimelineEvent ev) => ev.Lane == TimelineLane.Expression && ev.Path.StartsWith("cfxf_", StringComparison.Ordinal);

    /// <summary>The length of the game's expression timelines (facial/pose), in frames.</summary>
    public const int ExpressionLength = 150;

    /// <summary>
    /// The length an expression's timeline is edited at: its header's, or for an endless header
    /// (-1, which mods that keep expressions on write) the game's own expression length.
    /// </summary>
    public static int ExpressionFrames(int header) => header > 0 ? header : ExpressionLength;

    /// <summary>The length a kept face stores (<see cref="Kept"/>): -1, which the game reads as endless.</summary>
    public const int KeptDuration = -1;

    /// <summary>
    /// Whether <paramref name="ev"/> is a kept face that loops: a moving face on an expression's
    /// timeline, saved as copies of itself back to back on its track up to the last frame a
    /// timeline can reach (<see cref="MaxFrames"/>, about 18 minutes). The game plays every copy,
    /// even past the timeline's length (seen in game 2026-10-05). After the last play the face
    /// returns to neutral. One face entry can't loop: a range past the face's own length drops it
    /// at once, and the expression's timeline never repeats.
    /// </summary>
    public static bool Loops(TimelineEvent ev) => ev.Loop && Kept(ev);

    /// <summary>The most copies a looping face is saved as after its first play: a short face would need thousands.</summary>
    public const int MaxLoopCopies = 100;

    /// <summary>Why a moving face needing more than <see cref="MaxLoopCopies"/> copies doesn't loop.</summary>
    public const string TooShortToLoop = "This face is too short to loop. Use a face made to loop.";

    /// <summary>
    /// How many copies follow the first play of a face that loops every <paramref name="length"/>
    /// frames from <paramref name="start"/>, as a save writes them: back to back up to <see cref="MaxFrames"/>.
    /// </summary>
    public static long LoopCopies(int start, int length) => length <= 0 ? long.MaxValue : Math.Max(0, ((long)MaxFrames - start) / length);

    /// <summary>The longest a pose is: every vanilla face is a 2-frame clip whose frames are equal.</summary>
    public const int PoseFrames = 2;

    /// <summary>
    /// The own length in frames of a face that moves (a mod's animated face, such as a 600-frame
    /// panting loop), by its motion name in <paramref name="motionFrames"/>; null for a pose or a
    /// face whose length is unknown.
    /// </summary>
    public static int? Moves(TimelineEvent ev, IReadOnlyDictionary<string, int>? motionFrames) =>
        motionFrames != null && motionFrames.TryGetValue(ev.Path, out var frames) && frames > PoseFrames ? frames : null;

    /// <summary>
    /// Whether <paramref name="ev"/> is a face that can be kept on: an editable C010 face.
    /// </summary>
    public static bool CanKeep(TimelineEvent ev) => ev.Magic == "C010" && ev.Access == TimelineAccess.Editable && IsFace(ev);

    /// <summary>
    /// Whether <paramref name="ev"/> is a kept face: time control on and an endless length (-1),
    /// what "Permanent Facial Expressions" writes. The game keeps showing it after its timeline
    /// ends, until another face replaces it. It is drawn until the next face or the end.
    /// </summary>
    public static bool Kept(TimelineEvent ev) =>
        ev.Duration < 0 && ev.Magic == "C010" && IsFace(ev) && ev.Values is { } values &&
        values.TryGetValue(TmbEntrySchema.TimeControlField, out var timed) && timed != 0;

    /// <summary>A motion's length in seconds as whole frames; null when unknown or out of the timeline's range.</summary>
    public static int? MotionFrames(float seconds) =>
        float.IsFinite(seconds) && Math.Round(seconds * (double)Fps, MidpointRounding.AwayFromZero) is var frames and >= 1 and <= MaxFrames
            ? (int)frames
            : null;
}

/// <summary>What an Add creates. Each kind makes one or more events (a prop makes several).</summary>
internal enum TimelineAddKind : byte { Expression, LipSync, Sound, Effect, Prop }

/// <summary>A weapon or prop model for a prop event, and optionally the WeaponTimeline row that animates it.</summary>
internal sealed record TimelinePropModel(ushort Model, ushort Body, ushort Variant, ushort? WeaponTimeline);

/// <summary>
/// Everything an Add needs. <paramref name="Path"/> is the motion name for expressions and lip
/// sync ("cfxf_smile", "cfxl_lip_nor1") and the game path for sounds and effects.
/// <paramref name="Pack"/> is the expression library a non-resident expression needs, else null.
/// </summary>
internal sealed record TimelineAddRequest(TimelineAddKind Kind, int Start, string Path, int Duration = 0,
    string? Pack = null, TimelinePropModel? Prop = null);

internal enum TimelineFieldKind : byte { Int, Float, Bool, Choice, Flags }

/// <summary>One editable field of an entry kind, for the properties panel.</summary>
/// <param name="Name">The key in <see cref="TimelineEvent.Values"/>.</param>
/// <param name="Label">Short UI label.</param>
/// <param name="Options">For Choice and Flags: each option's label and value (a bit for Flags).</param>
internal sealed record TimelineField(string Name, string Label, TimelineFieldKind Kind, double Min, double Max,
    ImmutableArray<(string Label, double Value)> Options = default)
{
    public ImmutableArray<(string Label, double Value)> Options { get; init; } = Options.IsDefault ? [] : Options;
}
