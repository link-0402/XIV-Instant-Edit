using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Pose;
using InstantEdit.Ui.Timeline;

/// <summary>
/// The Bones window's breakdowns: Tween, Push / pull and Relax at the playhead, from the selected layer's own keys
/// either side (<see cref="MotionBreakdown"/>), written by <see cref="PoseEdits.Breakdown"/>.
/// </summary>
internal static class BreakdownFixture
{
    private const string Arm = "j_ude_a_l", Hand = "j_te_l", Tail = "n_sippo_a";
    private const int At = 5;

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Tweens(check);
        Pushes(check);
        Eases(check);
        Skips(check);
        Components(check);
        Moved(check);
        Writes(check);
    }

    private static Quaternion Q(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180);

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;

    private static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-4f;

    private static BoneTransform T(float degrees, Vector3 position, Vector3 scale) => new(position, Q(degrees), scale);

    private static MotionKey Key(int frame, params (string Bone, BoneTransform Value)[] bones) =>
        new(frame, bones.ToImmutableDictionary(b => b.Bone, b => b.Value, StringComparer.Ordinal));

    /// <summary>The arm at 0 degrees, at the origin and at 100% at frame 0; at 90 degrees, 2 along X and 200% at frame 20.</summary>
    private static readonly BoneTransform Start = T(0, Vector3.Zero, Vector3.One), Finish = T(90, new Vector3(2, 0, 0), new Vector3(2));

    private static TimelineDocument Doc(MotionLayerKind kind = MotionLayerKind.Offset, PoseComponents parts = PoseComponents.All,
        MotionInterpolation ease = MotionInterpolation.Linear, params MotionKey[] keys) =>
        new TimelineDocument(30, 30, null, [], 1) with
        {
            Motion = [new MotionLayer("m1", kind, "Layer", 0, 30, 0, 0, 1, parts, ease, [], [.. keys])],
        };

    private static TimelineDocument Spanned(params MotionKey[] extra) =>
        Doc(keys: [.. new[] { Key(0, (Arm, Start)), Key(20, (Arm, Finish)) }.Concat(extra).OrderBy(key => key.Frame)]);

    private static MotionTransform? Value(TimelineDocument doc, string bone, int frame = At) =>
        doc.Motion[0].Keys.FirstOrDefault(key => key.Frame == frame)?.Bones.TryGetValue(bone, out var value) == true ? MotionTransform.From(value) : null;

    private static TimelineDocument Run(TimelineDocument doc, BreakdownMode mode, float amount, out string? status, params string[] bones) =>
        PoseEdits.Breakdown(doc, "m1", At, bones.Length == 0 ? [Arm] : bones, mode, amount, out status);

    private static bool Is(MotionTransform? value, float degrees, float x, float scale) =>
        value is { } v && Degrees(v.Rotation, Q(degrees)) < .01f && Near(v.Position, new Vector3(x, 0, 0)) && Near(v.Scale, new Vector3(scale));

    private static void Tweens(Action<bool, string> check)
    {
        var doc = Spanned();
        check(Is(Value(Run(doc, BreakdownMode.Tween, 0, out _), Arm), 0, 0, 1), "a tween at 0% keys the key before");
        check(Is(Value(Run(doc, BreakdownMode.Tween, .5f, out _), Arm), 45, 1, 1.5f), "a tween at 50% keys halfway, rotation, position and scale");
        check(Is(Value(Run(doc, BreakdownMode.Tween, 1, out _), Arm), 90, 2, 2), "a tween at 100% keys the key after");
        check(Is(Value(Run(doc, BreakdownMode.Tween, 1.5f, out _), Arm), 135, 3, 2.5f), "a tween at 150% overshoots past the key after");
        check(Is(Value(Run(doc, BreakdownMode.Tween, -.5f, out _), Arm), -45, -1, .5f), "a tween at -50% overshoots before the key before");
        var tiny = Doc(keys: [Key(0, (Arm, T(0, Vector3.Zero, new Vector3(.01f)))), Key(20, (Arm, Finish))]);
        check(Value(Run(tiny, BreakdownMode.Tween, -.5f, out _), Arm) is { } low && low.Scale.X >= MotionEvaluator.MinScale,
            "an overshoot keeps the scale within what a key holds");
        check(Is(Value(Run(Spanned(Key(At, (Arm, T(10, Vector3.One, Vector3.One)))), BreakdownMode.Tween, .5f, out _), Arm), 45, 1, 1.5f),
            "a tween replaces the bone's key at the playhead, from the keys either side alone");
    }

    private static void Pushes(Action<bool, string> check)
    {
        // Linear: what the keys give at frame 5 is a quarter of the way, 22.5 degrees, .5 along and 125%.
        var doc = Spanned(Key(At, (Arm, T(40, new Vector3(1, 0, 0), new Vector3(1.5f)))));
        check(Is(Value(Run(doc, BreakdownMode.Push, 1, out _), Arm), 40, 1, 1.5f), "a push at 100% changes nothing");
        check(Is(Value(Run(doc, BreakdownMode.Push, 2, out _), Arm), 57.5f, 1.5f, 1.75f), "a push at 200% doubles the difference from the keys either side");
        check(Is(Value(Run(doc, BreakdownMode.Push, 0, out _), Arm), 22.5f, .5f, 1.25f), "a push at 0% lands on what the keys either side give");
        check(Is(Value(Run(doc, BreakdownMode.Push, .5f, out _), Arm), 31.25f, .75f, 1.375f), "a push below 100% softens");
        check(Is(Value(Run(doc, BreakdownMode.Relax, 1, out _), Arm), 22.5f, .5f, 1.25f), "a relax at 100% lands on what the keys either side give");
        check(Is(Value(Run(doc, BreakdownMode.Relax, .5f, out _), Arm), 31.25f, .75f, 1.375f), "a relax at 50% goes halfway there");
        check(Is(Value(Run(doc, BreakdownMode.Relax, 0, out _), Arm), 40, 1, 1.5f), "a relax at 0% changes nothing");
        // The short way: a key at the playhead stored on the other hemisphere pushes the same.
        var flipped = Spanned(Key(At, (Arm, new BoneTransform(new Vector3(1, 0, 0), -Q(40), new Vector3(1.5f)))));
        check(Is(Value(Run(flipped, BreakdownMode.Push, 2, out _), Arm), 57.5f, 1.5f, 1.75f), "a push goes the short way round");
    }

    private static void Eases(Action<bool, string> check)
    {
        var smooth = Doc(ease: MotionInterpolation.Smooth, keys: [Key(0, (Arm, Start)), Key(20, (Arm, Finish))]);
        var span = MotionBreakdown.Span(smooth.Motion[0], Arm, At)!.Value;
        // A smoothstep a quarter of the way: .15625.
        check(Degrees(MotionBreakdown.Inter(span, At).Rotation, Q(90 * .15625f)) < .01f, "the keys either side ease as the layer does: a smoothstep");
        var held = Doc(keys: [Key(0, (Arm, Start)) with { Ease = MotionInterpolation.Step }, Key(20, (Arm, Finish))]);
        check(Is(MotionBreakdown.Inter(MotionBreakdown.Span(held.Motion[0], Arm, At)!.Value, At), 0, 0, 1), "a hold on the key before holds it");
        var relaxed = Run(held with { Motion = [held.Motion[0] with { Keys = held.Motion[0].Keys.Insert(1, Key(At, (Arm, Finish))) }] },
            BreakdownMode.Relax, 1, out _);
        check(Is(Value(relaxed, Arm), 0, 0, 1), "a relax onto a hold lands on the key before");
        var after = Doc(keys: [Key(0, (Arm, Start)), Key(20, (Arm, Finish)) with { Ease = MotionInterpolation.Step }]);
        check(Is(MotionBreakdown.Inter(MotionBreakdown.Span(after.Motion[0], Arm, At)!.Value, At), 22.5f, .5f, 1.25f),
            "the key after's own ease doesn't count");
    }

    private static void Skips(Action<bool, string> check)
    {
        var doc = Spanned() with { Motion = [Spanned().Motion[0] with { Keys = [Key(0, (Arm, Start), (Hand, Start)), Key(20, (Arm, Finish))] }] };
        var some = Run(doc, BreakdownMode.Tween, .5f, out var status, Arm, Hand, Tail);
        check(Value(some, Arm) != null && Value(some, Hand) == null && Value(some, Tail) == null && status == null,
            "bones without a key either side are skipped, the others keyed");
        var none = Run(doc, BreakdownMode.Tween, .5f, out status, Hand, Tail);
        check(ReferenceEquals(none, doc) && status == "No keys either side.", "with no bone keyed either side nothing changes and the status says so");
        var unkeyed = Run(doc, BreakdownMode.Push, 2, out status);
        check(ReferenceEquals(unkeyed, doc) && status == "Key this frame first.", "a push without a key at the playhead changes nothing and asks for one");
        Run(doc, BreakdownMode.Relax, .5f, out status);
        check(status == "Key this frame first.", "so does a relax");
        var other = Doc(MotionLayerKind.Amplitude);
        check(ReferenceEquals(PoseEdits.Breakdown(other, "m1", At, [Arm], BreakdownMode.Tween, .5f, out _), other), "other layer kinds are left alone");
        var edge = PoseEdits.Breakdown(doc, "m1", 0, [Arm], BreakdownMode.Tween, .5f, out status);
        check(ReferenceEquals(edge, doc) && status == "No keys either side.", "a key on the playhead itself isn't a neighbour");
    }

    private static void Components(Action<bool, string> check)
    {
        var keyed = Doc(parts: PoseComponents.Rotation,
            keys: [Key(0, (Arm, Start)), Key(At, (Arm, T(10, new Vector3(9, 0, 0), new Vector3(3)))), Key(20, (Arm, Finish))]);
        check(Is(Value(Run(keyed, BreakdownMode.Tween, .5f, out _), Arm), 45, 9, 3), "a layer that only turns keeps the key's move and scale");
        var fresh = Doc(parts: PoseComponents.Rotation | PoseComponents.Scale, keys: [Key(0, (Arm, Start)), Key(20, (Arm, Finish))]);
        check(Is(Value(Run(fresh, BreakdownMode.Tween, 1, out _), Arm), 90, .5f, 2), "a new key takes what the keys give for the parts the layer doesn't change");
    }

    private static void Moved(Action<bool, string> check)
    {
        static MotionKey M(MotionKey key) => key with { Moved = [Arm] };
        var moving = Doc(MotionLayerKind.Pose, keys: [M(Key(0, (Arm, Start))), M(Key(20, (Arm, Finish)))]);
        var tweened = Run(moving, BreakdownMode.Tween, .5f, out _);
        check(Is(Value(tweened, Arm), 45, 1, 1.5f) && tweened.Motion[0].Keys[1].Moved.Contains(Arm), "a pose tween between keys that move the bone moves it too");
        var turning = Doc(MotionLayerKind.Pose, keys: [M(Key(0, (Arm, Start))), Key(20, (Arm, Finish))]);
        var turned = Run(turning, BreakdownMode.Tween, 1, out _);
        check(Value(turned, Arm) is { } only && Degrees(only.Rotation, Q(90)) < .01f && Near(only.Position, new Vector3(.5f, 0, 0)) &&
              turned.Motion[0].Keys[1].Moved.IsEmpty, "a pose tween leaves the position to the motion when a key either side does");
        var pushed = Run(moving with { Motion = [moving.Motion[0] with { Keys = moving.Motion[0].Keys.Insert(1, M(Key(At, (Arm, T(40, new Vector3(1, 0, 0), Vector3.One))))) }] },
            BreakdownMode.Push, 2, out _);
        check(pushed.Motion[0].Keys[1].Moved.Contains(Arm) && Near(Value(pushed, Arm)!.Value.Position, new Vector3(1.5f, 0, 0)),
            "a pose push keeps the key's Moved and pushes its position");
        var kept = Run(moving with { Motion = [moving.Motion[0] with { Keys = moving.Motion[0].Keys.Insert(1, Key(At, (Arm, T(40, new Vector3(7, 0, 0), Vector3.One)))) }] },
            BreakdownMode.Push, 2, out _);
        check(kept.Motion[0].Keys[1].Moved.IsEmpty && Near(Value(kept, Arm)!.Value.Position, new Vector3(7, 0, 0)),
            "a pose push leaves a key that only turns the bone turning it");
    }

    private static void Writes(Action<bool, string> check)
    {
        var doc = Spanned(Key(At, (Arm, Start), (Hand, Finish)) with { Ease = MotionInterpolation.EaseIn });
        var replaced = Run(doc, BreakdownMode.Tween, .5f, out _);
        var key = replaced.Motion[0].Keys[1];
        check(replaced.Motion[0].Keys.Length == 3 && key.Ease == MotionInterpolation.EaseIn && key.Bones[Hand] == Finish && Is(Value(replaced, Arm), 45, 1, 1.5f),
            "a breakdown replaces the bone in the key at the playhead, keeping its ease and other bones");
        var added = Run(Spanned(), BreakdownMode.Tween, .5f, out _);
        check(added.Motion[0].Keys.Length == 3 && added.Motion[0].Keys[1] is { Frame: At, Ease: null } && Is(Value(added, Arm), 45, 1, 1.5f),
            "without a key at the playhead it adds one with the layer's ease");
        MotionEvaluator.Validate(added.Motion);
        check(true, "the written layer stays valid");
    }
}
