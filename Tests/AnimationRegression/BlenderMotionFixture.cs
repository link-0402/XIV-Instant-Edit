using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

/// <summary>
/// Motion from Blender: the read request and reply the plugin and the add-on exchange. Building the
/// PAP needs the game's Havok and is checked in game.
/// </summary>
internal static class BlenderMotionFixture
{
    private const string Root = "n_root", Arm = "j_ude_a_l", Hand = "j_te_l", Tail = "n_sippo_a";
    private const string GamePath = "chara/human/c0101/animation/a0001/bt_common/emote/regression.pap";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Format(check, reject);
        Compose(check);
        Tracks(check);
        Texts(check);
    }

    private static void Texts(Action<bool, string> check)
    {
        var playing = Playing();
        var take = AnimationTakes.FromMotion(playing, "Dance (cbem_regression)", new Dictionary<string, string> { ["timeline"] = "1" });
        var arm = AnimationTake.Read(take.Samples.Slice((10 * 4 + 1) * AnimationTake.Stride, AnimationTake.Stride));
        check(take.Kind == AnimationTake.AnimationKind && take.Bones.Length == 4 && take.Bones.Select(b => b.Parent).SequenceEqual([-1, 0, 1, 0]) &&
              take.Bones[3].Reference == Rig().Bones[3].Reference && take.FrameCount == 31 && take.Times[^1] == 1 &&
              Near(MotionTransform.From(arm), playing.Get(10, 1), 1e-6f) && take.Source["timeline"] == "1",
            "the timeline's motion becomes a take for Blender");

        MotionReplacement R(int kept, bool other) => new([], "p", "h", 120, "Walk", "Body", new BlenderMotionReport(142, 10, kept, other));
        var plain = InstantEdit.Ui.TimelineWindowViews.BlenderMotionLine(R(0, false));
        var full = InstantEdit.Ui.TimelineWindowViews.BlenderMotionLine(R(3, true));
        var one = InstantEdit.Ui.TimelineWindowViews.BlenderMotionLine(R(1, false));
        check(plain == "Motion from Blender: \"Walk\" on \"Body\", 142 bones, 120 frames." &&
              full == plain + " 3 bones kept the clip's motion. Made from another animation." &&
              one == plain + " 1 bone kept the clip's motion." && !(plain + full + one).Contains('…'),
            "the status line names the action, its bones and length, and what it kept");
        var scaled = InstantEdit.Ui.TimelineWindowViews.BlenderMotionLine(
            new([], "p", "h", 120, "Walk", "Body", new BlenderMotionReport(142, 10, 0, false, ScaleIgnored: true)));
        check(scaled == plain + " Its scale was left out: Key bone scale was off when it was sent.",
            "the status line says when Blender's scale was left out");
        check(InstantEdit.Ui.TimelineWindowViews.BlenderUnchangedLine(false) == "Blender's motion matches the clip's. Nothing changed." &&
              InstantEdit.Ui.TimelineWindowViews.BlenderUnchangedLine(true) ==
              "Blender's motion matches the clip's. Nothing changed. Its scale was left out: Key bone scale was off when it was sent.",
            "a read that changes nothing says so, and says when Blender's scale was left out");
    }

    /// <summary>A clip's timeline as the Timeline window loads it: a 30-frame document over a three-byte stand-in PAP.</summary>
    internal static AnimationTimelineSource Source()
    {
        var clip = new AnimationClip(GamePath, "cbem_regression", 0, 0, 42, "chara/test.sklb");
        var capture = new AnimationCapture("a", 7, 1, Guid.NewGuid(), "Test", "Dance", clip, null, [clip.GamePath],
            [new AnimationResource(clip.GamePath, "dance.pap", "hash")], new PoseSnapshot(0, 0, 0, false, [], DateTime.UtcNow, ""),
            DateTime.UtcNow, true);
        return new AnimationTimelineSource(capture, false, clip, new AnimationResource(GamePath, "dance.pap", "file"), [1, 2, 3], 0, [],
            new TimelineDocument(30, 30, null, [], 1), null) { FileFingerprint = "file-print" };
    }

    private static void Tracks(Action<bool, string> check)
    {
        MotionClip Clip(bool tailMoves) => MotionClip.FromPoses(Rig(), 1, Enumerable.Range(0, 31).Select(i => new[]
        {
            T(new Vector3(0, 0, i / 30f)),
            T(Vector3.UnitX),
            T(Vector3.UnitX),
            T(-Vector3.UnitY + (tailMoves ? new Vector3(0, 0, i / 300f) : Vector3.Zero)),
        }).ToArray());
        check(AnimationBakeRules.ReplacementTracks([2, 0], Clip(true)).SequenceEqual(new short[] { 2, 0, 3 }),
            "the clip's tracks stay in order and moved bones are added");
        check(AnimationBakeRules.ReplacementTracks([2, 0], Clip(false)).SequenceEqual(new short[] { 2, 0 }),
            "a bone at its reference pose gets no track");
        check(AnimationBakeRules.ReplacementDuration(1 + 5e-7f, 1) == 1 && AnimationBakeRules.ReplacementDuration(1 - 9e-7f, 1) == 1 &&
              AnimationBakeRules.ReplacementDuration(1 + 2e-6f, 1) == 1 + 2e-6f && AnimationBakeRules.ReplacementDuration(1.5f, 1) == 1.5f,
            "a motion within a microsecond of the clip's length keeps that length exactly");
        check(!AnimationBakeRules.AdditiveWithFloats(0, 3) && !AnimationBakeRules.AdditiveWithFloats(1, 0) &&
              !AnimationBakeRules.AdditiveWithFloats(2, 0) && AnimationBakeRules.AdditiveWithFloats(1, 2) &&
              AnimationBakeRules.AdditiveWithFloats(2, 1),
            "an additive clip with float tracks is the one whose motion can't come from Blender");

        var source = Source();
        var replacement = new MotionReplacement([4, 5, 6], "blender-print", "blender-hash", 45, "Walk", "Body", new BlenderMotionReport(4, 1, 0, false));
        var replaced = source.WithReplacement(replacement);
        check(ReferenceEquals(replaced.MotionSource, replacement.Pap) && replaced.MotionClip.BindingFingerprint == "blender-print" &&
              replaced.BaseHash == "blender-hash" && ReferenceEquals(source.WithReplacement(null), source),
            "a replacement becomes the timeline's base motion");
    }

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static float Degrees(Quaternion a, Quaternion b)
    {
        var d = Quaternion.Conjugate(Quaternion.Normalize(a)) * Quaternion.Normalize(b);
        return 2 * MathF.Atan2(new Vector3(d.X, d.Y, d.Z).Length(), Math.Abs(d.W)) * 180 / MathF.PI;
    }

    private static bool Near(MotionTransform a, MotionTransform b, float tolerance = 1e-5f) =>
        Vector3.Distance(a.Position, b.Position) < tolerance && Degrees(a.Rotation, b.Rotation) < tolerance * 180 / MathF.PI &&
        Vector3.Distance(a.Scale, b.Scale) < tolerance;

    /// <summary>
    /// The timeline's motion: 31 samples over one second, the arm turning from 0 to 90 degrees about Z
    /// and the root sliding along Z, while the hand and the tail hold their reference poses.
    /// </summary>
    internal static MotionClip Playing(int samples = 31, float duration = 1)
    {
        var poses = Enumerable.Range(0, samples).Select(i => new[]
        {
            T(new Vector3(0, 0, i / (samples - 1f))),
            T(Vector3.UnitX, Q(Vector3.UnitZ, 90 * i / (samples - 1f))),
            T(Vector3.UnitX),
            T(-Vector3.UnitY),
        }).ToArray();
        return MotionClip.FromPoses(Rig(), duration, poses);
    }

    /// <summary>
    /// A reply over <paramref name="clip"/>'s times holding the given bones (index, ancestor), each the clip's
    /// own relation to its ancestor unless <paramref name="value"/> gives another.
    /// </summary>
    private static AnimationRead ReadOf(MotionClip clip, (int Index, int Ancestor)[] bones, Func<int, int, MotionTransform, MotionTransform>? value = null,
        bool keyedScale = true, Dictionary<string, string>? source = null)
    {
        var samples = new float[clip.Samples * bones.Length * AnimationTake.Stride];
        for (var t = 0; t < clip.Samples; t++)
        {
            var local = Enumerable.Range(0, clip.Bones).Select(b => clip.Get(t, b)).ToArray();
            var model = MotionEvaluator.ModelSpace(clip.Skeleton, local);
            for (var slot = 0; slot < bones.Length; slot++)
            {
                var (index, ancestor) = bones[slot];
                var own = ancestor < 0 ? model[index] : MotionEvaluator.Relative(model[ancestor], model[index]);
                var written = value?.Invoke(t, index, own) ?? own;
                AnimationTake.Write(written.ToBone(), samples.AsSpan((t * bones.Length + slot) * AnimationTake.Stride, AnimationTake.Stride));
            }
        }
        var times = Enumerable.Range(0, clip.Samples).Select(i => (double)clip.Time(i)).ToArray();
        return new AnimationRead("Walk", "Body", 1, clip.Samples, 30, clip.Duration, keyedScale, "keys",
            source ?? new Dictionary<string, string> { ["gamePath"] = GamePath, ["clip"] = "cbem_regression" },
            [.. bones.Select(b => new AnimationReadBone(b.Index, b.Ancestor))], times, samples, clip.Bones - bones.Length, []);
    }

    private static readonly (int, int)[] Everyone = [(0, -1), (1, 0), (2, 1), (3, 0)];

    private static void Compose(Action<bool, string> check)
    {
        var playing = Playing();
        var (same, unchanged) = BlenderMotion.Build(BlenderMotion.Fill(playing, ReadOf(playing, Everyone)), ReadOf(playing, Everyone), GamePath, "cbem_regression");
        var identical = Enumerable.Range(0, playing.Samples).All(t => Enumerable.Range(0, playing.Bones).All(b => same.Get(t, b) == playing.Get(t, b)));
        check(unchanged == new BlenderMotionReport(4, 0, 0, false) && identical, "an unedited reply keeps the clip's motion exactly");

        var turned = ReadOf(playing, Everyone, (t, b, own) => b == 1 ? own with { Rotation = own.Rotation * Q(Vector3.UnitZ, 30) } : own);
        var (motion, report) = BlenderMotion.Build(BlenderMotion.Fill(playing, turned), turned, GamePath, "cbem_regression");
        check(report.Changed == 1 && Enumerable.Range(0, playing.Samples).All(t =>
                  Near(motion.Get(t, 1), turned.Sample(t, 1)) && motion.Get(t, 2) == playing.Get(t, 2)),
            "a turned bone takes Blender's motion and its children follow");

        // The armature lacks the arm: the hand comes relative to the root and is placed through the clip's own arm.
        var shifted = ReadOf(playing, [(0, -1), (2, 0), (3, 0)],
            (t, b, own) => b == 2 ? own with { Position = own.Position + new Vector3(0, 0.2f, 0) } : own);
        var (placed, missing) = BlenderMotion.Build(BlenderMotion.Fill(playing, shifted), shifted, GamePath, "cbem_regression");
        var throughArm = Enumerable.Range(0, playing.Samples).All(t =>
        {
            var root = shifted.Sample(t, 0);
            var arm = MotionEvaluator.Compose(root, playing.Get(t, 1));
            var hand = MotionEvaluator.Relative(arm, MotionEvaluator.Compose(root, shifted.Sample(t, 1)));
            return Near(placed.Get(t, 2), hand) && placed.Get(t, 1) == playing.Get(t, 1);
        });
        check(throughArm && missing.Kept == 1 && missing.FromBlender == 3,
            "a bone under a missing bone is placed through the clip's own motion of that bone");

        var noisy = ReadOf(playing, Everyone, (t, b, own) => b == 3
            ? own with { Position = own.Position + new Vector3(5e-5f, 0, 0), Rotation = own.Rotation * Q(Vector3.UnitX, 0.004f) }
            : own);
        var (kept, keptReport) = BlenderMotion.Build(BlenderMotion.Fill(playing, noisy), noisy, GamePath, "cbem_regression");
        check(keptReport.Changed == 0 && Enumerable.Range(0, playing.Samples).All(t => kept.Get(t, 3) == playing.Get(t, 3)),
            "changes within the tolerance keep the clip's motion, so physics bones get no track");

        var grown = ReadOf(playing, Everyone, (t, b, own) => b == 1
            ? own with { Rotation = own.Rotation * Q(Vector3.UnitZ, 30), Scale = new Vector3(2) }
            : own, keyedScale: false);
        var (unscaled, _) = BlenderMotion.Build(BlenderMotion.Fill(playing, grown), grown, GamePath, "cbem_regression");
        check(Enumerable.Range(0, playing.Samples).All(t => unscaled.Get(t, 1).Scale == playing.Get(t, 1).Scale),
            "without keyed scale the clip keeps its own scale");
        var leftOut = grown with { ScaleIgnored = true };
        check(BlenderMotion.Build(BlenderMotion.Fill(playing, leftOut), leftOut, GamePath, "cbem_regression").Report.ScaleIgnored &&
              !unchanged.ScaleIgnored, "the report says when Blender's scale was left out");

        var other = ReadOf(playing, Everyone, source: new() { ["gamePath"] = "chara/human/c0101/animation/a0001/bt_common/emote/other.pap" });
        var unknown = ReadOf(playing, Everyone, source: []);
        check(BlenderMotion.Build(BlenderMotion.Fill(playing, other), other, GamePath, "cbem_regression").Report.OtherClip &&
              !BlenderMotion.Build(BlenderMotion.Fill(playing, unknown), unknown, GamePath, "cbem_regression").Report.OtherClip,
            "an action made from another animation is noticed");

        var flipped = ReadOf(playing, Everyone, (t, b, own) =>
        {
            if (b != 1) return own;
            var q = own.Rotation * Q(Vector3.UnitZ, 30);
            return own with { Rotation = t % 2 == 1 ? -q : q };
        });
        var (continuous, _) = BlenderMotion.Build(BlenderMotion.Fill(playing, flipped), flipped, GamePath, "cbem_regression");
        check(Enumerable.Range(1, playing.Samples - 1).All(t => Quaternion.Dot(continuous.Get(t - 1, 1).Rotation, continuous.Get(t, 1).Rotation) >= 0),
            "the motion is made continuous before it is built");

        var longer = ReadOf(Playing(61, 2), Everyone);
        var stretched = BlenderMotion.Fill(playing, longer);
        check(stretched.Samples == 61 && stretched.Duration == 2 &&
              Enumerable.Range(0, playing.Bones).All(b => Near(stretched.Get(30, b), playing.Get(15, b), 1e-6f)),
            "the clip's motion is stretched to Blender's length");
    }

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);

    internal static SkeletonDescription Rig() => new("rig", "rig-fingerprint",
        [new(Root, -1, 0, T(Vector3.Zero)), new(Arm, 0, 0, T(Vector3.UnitX)), new(Hand, 1, 0, T(Vector3.UnitX)), new(Tail, 0, 0, T(-Vector3.UnitY))],
        [], [], []);

    private static AnimationReadRequest Request() => new(30, GamePath, "cbem_regression", Rig());

    /// <summary>One transform per bone per time, as floats in the take layout.</summary>
    private static float[] Samples(int times, int bones, Func<int, int, BoneTransform> value)
    {
        var samples = new float[times * bones * AnimationTake.Stride];
        for (var t = 0; t < times; t++)
            for (var b = 0; b < bones; b++)
                AnimationTake.Write(value(t, b), samples.AsSpan((t * bones + b) * AnimationTake.Stride, AnimationTake.Stride));
        return samples;
    }

    /// <summary>A reply's header as the add-on writes it: every rig bone relative to its parent over two times.</summary>
    private static JsonObject Header() => new()
    {
        ["schema"] = "instant-edit.animation-read",
        ["version"] = 1,
        ["kind"] = "blender",
        ["action"] = "Walk",
        ["armature"] = "Body",
        ["frameStart"] = 1,
        ["frameEnd"] = 2,
        ["sceneRate"] = 30.0,
        ["duration"] = 1 / 30.0,
        ["keyedScale"] = false,
        ["evaluated"] = "keys",
        ["source"] = new JsonObject { ["gamePath"] = GamePath, ["clip"] = "cbem_regression" },
        ["times"] = new JsonArray(0.0, 1 / 30.0),
        ["bones"] = new JsonArray(Bone(0, -1), Bone(1, 0), Bone(2, 1), Bone(3, 0)),
        ["missingBoneCount"] = 0,
        ["missingBones"] = new JsonArray(),
    };

    private static JsonObject Bone(int index, int ancestor) => new() { ["index"] = index, ["ancestor"] = ancestor };

    private static float[] DefaultSamples() =>
        Samples(2, 4, (t, b) => T(new Vector3(t + b, 0.5f * b, -t), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.1f * (t + b))));

    /// <summary>The XIEA bytes of a reply.</summary>
    internal static byte[] Reply(JsonObject header, float[] samples, int version = 1, string magic = "XIEA")
    {
        var json = Encoding.UTF8.GetBytes(header.ToJsonString());
        var start = 12 + json.Length;
        var padding = (4 - start % 4) % 4;
        var bytes = new byte[start + padding + samples.Length * sizeof(float)];
        Encoding.ASCII.GetBytes(magic).CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), version);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), json.Length);
        json.CopyTo(bytes, 12);
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(start + padding + i * sizeof(float)), samples[i]);
        return bytes;
    }

    private static JsonObject With(Action<JsonObject> change)
    {
        var header = Header();
        change(header);
        return header;
    }

    private static void Format(Action<bool, string> check, Action<Action, string> reject)
    {
        var written = JsonNode.Parse(AnimationReadFormat.WriteRequest(Request(), "Body", null, "2.2.0"))!.AsObject();
        var bones = written["bones"]!.AsArray();
        check(written["schema"]!.GetValue<string>() == "instant-edit.animation-read-request" && written["version"]!.GetValue<int>() == 1 &&
              written["rate"]!.GetValue<double>() == 30 && written["targetObject"]!.GetValue<string>() == "Body" &&
              written["clip"]!["gamePath"]!.GetValue<string>() == GamePath && written["clip"]!["clip"]!.GetValue<string>() == "cbem_regression" &&
              bones.Count == 4 && bones[2]!["name"]!.GetValue<string>() == Hand && bones[2]!["parent"]!.GetValue<int>() == 1 &&
              bones[3]!["reference"]!.AsArray().Count == AnimationTake.Stride && bones[3]!["reference"]![1]!.GetValue<float>() == -1,
            "a read request names the armature, rate, clip and skeleton");

        var samples = DefaultSamples();
        var read = AnimationReadFormat.Parse(Reply(Header(), samples), Request());
        var expected = AnimationTake.Read(samples.AsSpan(4 * AnimationTake.Stride, AnimationTake.Stride));
        var sample = read.Sample(1, 0);
        check(read.Action == "Walk" && read.Armature == "Body" && read.Bones.Length == 4 && read.Bones[2] == new AnimationReadBone(2, 1) &&
              read.Times.Length == 2 && Math.Abs(read.Duration - 1 / 30f) < 1e-7 && !read.KeyedScale &&
              read.Source["gamePath"] == GamePath && sample.Position == expected.Position &&
              Quaternion.Dot(sample.Rotation, expected.Rotation) > 0.999999f,
            "a reply's samples are read per bone and time");

        check(!read.ScaleIgnored && AnimationReadFormat.Parse(Reply(With(h => h["scaleIgnored"] = true), samples), Request()).ScaleIgnored,
            "a reply says when Blender left a scale out");

        void Refused(byte[] body, string message) => reject(() => AnimationReadFormat.Parse(body, Request()), message);
        Refused(Reply(Header(), samples, magic: "XIEB"), "a reply without the take magic is refused");
        Refused(Reply(Header(), samples, version: 2), "a reply in another container version is refused");
        Refused(Reply(With(h => h["schema"] = "instant-edit.animation"), samples), "a reply with another schema is refused");
        Refused(Reply(With(h => h["bones"] = new JsonArray(Bone(0, -1), Bone(1, 0), Bone(2, 1), Bone(4, 0))), samples),
            "a reply naming a bone the skeleton lacks is refused");
        Refused(Reply(With(h => h["bones"] = new JsonArray(Bone(1, -1), Bone(0, -1), Bone(2, 1), Bone(3, 0))), samples),
            "a reply whose bones are out of order is refused");
        Refused(Reply(With(h => h["bones"] = new JsonArray(Bone(0, -1), Bone(1, 0), Bone(2, 1), Bone(3, 1))), samples),
            "a reply placing a bone under a bone that is not its ancestor is refused");
        Refused(Reply(Header(), samples[..^1]), "a reply one float short is refused");
        Refused(Reply(With(h => h["times"] = new JsonArray(0.1, 1 / 30.0)), samples), "a reply whose first time isn't zero is refused");
        Refused(Reply(With(h => h["times"] = new JsonArray(0.0, 0.0)), samples), "a reply whose times don't increase is refused");
        Refused(Reply(With(h => h["duration"] = 0.5), samples), "a reply whose last time isn't its duration is refused");
        var stretched = samples.ToArray();
        stretched[3] = 3;
        Refused(Reply(Header(), stretched), "a reply with a rotation that is not a unit quaternion is refused");
        var broken = samples.ToArray();
        broken[12] = float.NaN;
        Refused(Reply(Header(), broken), "a reply with a value that is not finite is refused");

        // Three times over two frames, the middle one where the add-on puts it (duration * i / (n - 1)) or off that grid.
        JsonObject Grid(double middle) => With(h =>
        {
            h["frameEnd"] = 3;
            h["duration"] = 2 / 30.0;
            h["times"] = new JsonArray(0.0, middle, 2 / 30.0);
        });
        var three = Samples(3, 4, (t, b) => T(new Vector3(t + b, 0, 0)));
        check(AnimationReadFormat.Parse(Reply(Grid(2 / 30.0 * 1 / 2), three), Request()).Times.Length == 3,
            "a reply whose times are uniform is read");
        Refused(Reply(Grid(0.02), three), "a reply whose times are not uniform is refused");
    }
}
