using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

/// <summary>
/// Animations sent to Blender: the take a recording or a sampled clip becomes, its binary form
/// (which Blender-Addon/testing/animation_regression.py reads back), and the resampling that
/// turns a recording's uneven frame times into a fixed rate.
/// </summary>
internal static class AnimationExportFixture
{
    private static BoneTransform T(float x, float y = 0, float z = 0, Quaternion? rotation = null) =>
        new(new(x, y, z), rotation ?? Quaternion.Identity, Vector3.One);

    private static readonly ImmutableArray<AnimationTakeBone> Bones =
    [
        new("n_root", -1, T(0)),
        new("j_kosi", 0, T(0, 1.02f, 0.03f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.2f))),
        new("j_mune_l", 1, T(0.05f, 0.11f, 0.08f)),
    ];

    private static float[] Frame(float x)
    {
        var frame = new float[Bones.Length * AnimationTake.Stride];
        for (var bone = 0; bone < Bones.Length; bone++)
            AnimationTake.Write(T(x + bone, 0, 0, Quaternion.CreateFromAxisAngle(Vector3.UnitY, x)), frame.AsSpan(bone * AnimationTake.Stride, AnimationTake.Stride));
        return frame;
    }

    private static bool Near(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 1e-6f) return false;
        return true;
    }

    public static AnimationTake SampleTake()
    {
        double[] times = [0, 1 / 30d, 2 / 30d];
        var samples = new[] { Frame(0), Frame(0.25f), Frame(0.5f) }.SelectMany(f => f).ToArray();
        return new AnimationTake(AnimationTake.RecordingKind, "Live pose 12:00:00", Bones, times, samples,
            source: new Dictionary<string, string> { ["character"] = "Regression" });
    }

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var take = SampleTake();
        check(take.FrameCount == 3 && Math.Abs(take.Duration - 2 / 30d) < 1e-12 && take.Samples.Length == 3 * 3 * AnimationTake.Stride,
            "a take holds every bone's sample for every frame");

        reject(() => _ = new AnimationTake(AnimationTake.RecordingKind, "x", [.. Bones, new("j_kosi", 0, T(0))], [0], new float[40]),
            "a take naming a bone twice is refused");
        reject(() => _ = new AnimationTake(AnimationTake.RecordingKind, "x", [new("a", 0, T(0))], [0], new float[10]),
            "a take bone listed before its parent is refused");
        reject(() => _ = new AnimationTake(AnimationTake.RecordingKind, "x", Bones, [0, 0], new float[60]),
            "take frame times must increase");
        reject(() => _ = new AnimationTake(AnimationTake.RecordingKind, "x", Bones, [0], new float[29]),
            "take samples must match the bones and frames");
        var infinite = Frame(0);
        infinite[12] = float.PositiveInfinity;
        reject(() => _ = new AnimationTake(AnimationTake.RecordingKind, "x", Bones, [0], infinite), "a non-finite sample is refused");
        var degenerate = Frame(0);
        degenerate.AsSpan(3, 4).Clear();
        reject(() => _ = new AnimationTake(AnimationTake.RecordingKind, "x", Bones, [0], degenerate), "a zero rotation is refused");
        var loose = Frame(0);
        for (var i = 3; i < 7; i++) loose[i] *= 1.004f;
        var normalized = new AnimationTake(AnimationTake.RecordingKind, "x", Bones, [0], loose);
        var q = AnimationTake.Read(normalized.Samples[..AnimationTake.Stride]).Rotation;
        check(Math.Abs(q.Length() - 1) < 1e-6f, "rotations the game keeps only nearly unit length are normalized");

        var bytes = AnimationTakeFormat.Write(take, "  Skeleton ", true, "1.2.4");
        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        var dataStart = 12 + headerLength + (4 - (12 + headerLength) % 4) % 4;
        check(Encoding.ASCII.GetString(bytes, 0, 4) == "XIEA" && BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) == 1 &&
              bytes.Length == dataStart + take.Samples.Length * 4 && dataStart % 4 == 0 &&
              bytes.AsSpan(12 + headerLength, dataStart - 12 - headerLength).IndexOfAnyExcept((byte)0) < 0,
            "the binary take is magic, version, header length, header, zero padding and the samples");
        var header = JsonNode.Parse(bytes.AsSpan(12, headerLength))!.AsObject();
        check((string?)header["schema"] == "instant-edit.animation" && (int?)header["version"] == 1 &&
              (string?)header["kind"] == "recording" && (string?)header["name"] == "Live pose 12:00:00" &&
              (string?)header["targetObject"] == "Skeleton" && (bool?)header["keyScale"] == true && (bool?)header["loop"] == false &&
              (string?)header["pluginVersion"] == "1.2.4" && (string?)header["source"]?["character"] == "Regression",
            "the take header names its schema, kind, target armature and options");
        check(!header.ContainsKey("targetCharacter"), "a take for the named armature names no character send");
        var sendBytes = AnimationTakeFormat.Write(take, "Skeleton", false, "1.2.4", new string('a', 32));
        var sendHeader = JsonNode.Parse(sendBytes.AsSpan(12, BinaryPrimitives.ReadInt32LittleEndian(sendBytes.AsSpan(8))))!.AsObject();
        check((string?)sendHeader["targetCharacter"] == new string('a', 32) && (string?)sendHeader["targetObject"] == "Skeleton",
            "a take for a character send names the send whose armature it belongs on");
        var bones = header["bones"]!.AsArray();
        check(bones.Count == 3 && (string?)bones[1]!["name"] == "j_kosi" && (int?)bones[1]!["parent"] == 0 &&
              bones[1]!["reference"]!.AsArray().Count == AnimationTake.Stride &&
              Math.Abs((float)bones[1]!["reference"]![1]! - 1.02f) < 1e-6f &&
              header["times"]!.AsArray().Select(t => (double)t!).SequenceEqual(take.Times),
            "the header carries the hierarchy, reference pose and frame times");
        var samples = new float[take.Samples.Length];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(dataStart + i * 4));
        check(samples.AsSpan().SequenceEqual(take.Samples), "the samples follow the header as little-endian floats, frame by frame");

        // A recording's frames arrive at the game's frame rate, not the take's.
        List<double> recorded = [0, 0.011, 0.02, 0.041, 0.05];
        List<float[]> frames = [Frame(0), Frame(0.1f), Frame(0.2f), Frame(0.3f), Frame(0.4f)];
        var (times, resampled) = AnimationTakeTiming.Resample(recorded, frames, Bones.Length, 60);
        check(times.Length == 4 && times.SequenceEqual([0, 1 / 60d, 2 / 60d, 3 / 60d]),
            "a recording is resampled at the take rate over its own length");
        var (poseTimes, pose) = AnimationTakeTiming.Resample([0.4], [Frame(0.1f)], Bones.Length, 60);
        check(poseTimes.SequenceEqual([0d]) && Near(pose, Frame(0.1f)),
            "a one-frame recording, a character send's current pose, stays one frame at time zero");
        var width = Bones.Length * AnimationTake.Stride;
        check(Near(resampled.AsSpan(0, width), frames[0]), "the first resampled frame is the first recorded frame");
        var middle = AnimationTake.Read(resampled.AsSpan(width + AnimationTake.Stride, AnimationTake.Stride));
        var expectedAlpha = (1 / 60d - 0.011) / (0.02 - 0.011);
        check(Math.Abs(middle.Position.X - (1.1f + 0.1f * (float)expectedAlpha)) < 1e-5f,
            "translations are interpolated between the recorded frames around each sample time");
        var halfway = new float[AnimationTake.Stride];
        var a = new float[AnimationTake.Stride];
        var b = new float[AnimationTake.Stride];
        AnimationTake.Write(T(0, 0, 0, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.2f)), a);
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f);
        AnimationTake.Write(T(2, 0, 0, new Quaternion(-turned.X, -turned.Y, -turned.Z, -turned.W)), b);
        AnimationTakeTiming.Interpolate(a, b, 0.5f, halfway);
        var interpolated = AnimationTake.Read(halfway);
        var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f);
        check(Math.Abs(interpolated.Position.X - 1) < 1e-6f && Math.Abs(Math.Abs(Quaternion.Dot(interpolated.Rotation, expected)) - 1) < 1e-6f,
            "rotations are interpolated along the shorter arc, whatever sign the game stored");
        var (single, singleSamples) = AnimationTakeTiming.Resample([0.25], [Frame(1)], Bones.Length, 60);
        check(single.SequenceEqual([0d]) && Near(singleSamples, Frame(1)), "a one-frame recording stays one frame");
        reject(() => AnimationTakeTiming.Resample([0, 0], [Frame(0), Frame(1)], Bones.Length, 60), "recorded frame times must increase");
        reject(() => AnimationTakeTiming.Resample([0], [new float[3]], Bones.Length, 60), "a recorded frame must match the skeleton");
    }

    /// <summary>
    /// Scaling a recording shows beyond the game's own, which is what remains of Customize+ when the
    /// recorder couldn't pause it (a temporary profile from Brio or Mare).
    /// </summary>
    public static void RecordingScaleCases(Action<bool, string> check)
    {
        ImmutableArray<AnimationTakeBone> bones =
        [
            new("n_root", -1, T(0)),
            new("j_kosi", 0, T(0, 1.02f)),
            new("j_asi_a_l", 1, T(0.1f)),
            new("j_mune_l", 1, T(0.05f, 0.2f, 0.08f)),
            new("iv_c_mune_l", 3, T(0.01f)),
            new("n_sippo_a", 1, T(0, 0, -0.1f)),
            new("n_sippo_b", 5, T(0, 0, -0.1f)),
            new("j_hidden", 1, new BoneTransform(Vector3.Zero, Quaternion.Identity, Vector3.Zero)),
            new("j_f_mayu_l", 0, T(0, 1.6f)),
        ];
        // Every bone but the face bone belongs to the body partial.
        var body = Enumerable.Range(0, bones.Length - 1).ToArray();

        float[] Frame(params (string Bone, Vector3 Scale)[] changes)
        {
            var frame = new float[bones.Length * AnimationTake.Stride];
            for (var i = 0; i < bones.Length; i++)
            {
                var reference = bones[i].Reference;
                var change = changes.Where(c => c.Bone == bones[i].Name).Select(c => (Vector3?)c.Scale).FirstOrDefault();
                AnimationTake.Write(reference with { Scale = change ?? reference.Scale }, frame.AsSpan(i * AnimationTake.Stride, AnimationTake.Stride));
            }
            return frame;
        }

        check(RecordingScale.ScaledBones(bones, body, Frame()).Count == 0, "a recording at the reference scale has nothing scaled");
        check(RecordingScale.ScaledBones(bones, body, Frame(("j_mune_l", new(1.2f, 1.1f, 1.1f)), ("iv_c_mune_l", new(1 / 1.2f, 1 / 1.1f, 1 / 1.1f)),
                ("n_sippo_a", new(1.3f)), ("n_sippo_b", new(0.9f)))).Count == 0,
            "the bust and the tail, which the game scales itself, are left out with the bones below them");
        var scaled = RecordingScale.ScaledBones(bones, body, Frame(("j_kosi", new(1, 1.15f, 1.1f)), ("j_asi_a_l", new(1, 1.36f / 1.15f, 1.4f / 1.1f)),
            ("n_root", new(1.005f)), ("j_hidden", new(1.5f)), ("j_f_mayu_l", new(1, 0.72f, 1))));
        check(scaled.SequenceEqual(["j_kosi", "j_asi_a_l"]),
            "Customize+-like scaling of body bones is found in take order, while float noise, hidden bones and bones outside the body are not");
        check(RecordingScale.Describe(["a", "b", "c", "d", "e", "f"]) == "a, b, c, d and 2 more" && RecordingScale.Describe(["j_kosi"]) == "j_kosi",
            "scaled bones are named briefly");
        check(RecordingScale.Warning("j_kosi", keyScale: false).EndsWith("and send it again with Key bone scale on.") &&
              RecordingScale.Warning("j_kosi", keyScale: true).EndsWith("off for this recording."),
            "the warning asks for Key bone scale only while it is off");
    }

    /// <summary>Animations tab rows for animations the listener detected, and action names for PAP files sent from the Mod Browser.</summary>
    public static void Rows(Action<bool, string> check)
    {
        const string loopPath = "chara/human/c0801/animation/a0001/bt_common/emote/dance01_loop.pap";
        const string startPath = "chara/human/c0801/animation/a0001/bt_common/emote/dance01_start.pap";
        const string idlePath = "chara/human/c0801/animation/a0001/bt_common/emote/pose04_loop.pap";
        var modded = new AnimationResource(loopPath, @"C:\mods\Dance\dance01_loop.pap", "hash", "DanceMod", @"C:\mods\Dance", "dance01_loop.pap", "Dance Mod");
        var vanilla = new AnimationResource(startPath, startPath, "hash2");
        var loop = new AnimationClip(loopPath, "cbem_dance01lp", 0, 0, 42, "chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb");
        var start = loop with { GamePath = startPath, Name = "cbem_dance01st" };
        var capture = new AnimationCapture("dance", 1, 1, Guid.NewGuid(), "Test", "Dance", loop, start, [loopPath, startPath],
            [modded, vanilla], new PoseSnapshot(0, 0, 0, false, [], DateTime.UtcNow, ""), DateTime.UtcNow, true);

        var row = InstantEdit.Ui.ResourceViews.FromAnimation(capture, false, 0);
        check(row.Name == "Dance" && row.GamePath == loopPath && row.ActualPath == modded.ResolvedPath &&
              row.SourceModName == "Dance Mod" && row.SourceModDirectory == "DanceMod" && row.SourceRelativePath == "dance01_loop.pap" &&
              row.SourceState == ResourceSourceState.LoadedMod && row.Kinds == InstantEdit.Ui.ResourceKinds.Animation,
            "a detected animation becomes an Animations tab row with its mod and file");
        var startup = InstantEdit.Ui.ResourceViews.FromAnimation(capture with { Playing = false }, true, 1);
        check(startup.GamePath == startPath && startup.SourceState == ResourceSourceState.GameData && startup.ActualPath == startPath &&
              startup.SourceLabel == "Game Data" && startup.SourceModName.Length == 0 && startup.Order == 1,
            "a startup row names its own file, and a vanilla file reads as game data");
        var unnamed = InstantEdit.Ui.ResourceViews.FromAnimation(capture with { Sources = [modded with { ModName = null }, vanilla] }, false, 0);
        check(unnamed.SourceModName == "DanceMod", "a mod without a known name is shown by its folder");

        // The list: what is playing, each with its startup, then what played recently with a matched skeleton.
        var skeleton = new SkeletonCandidate(new SkeletonSource(SkeletonSourceKind.Game, vanilla),
            new SkeletonDescription("skl_c0801b0001", "print", [], [], [], []), 0, "exact match");
        var matched = new SkeletonResolution(SkeletonResolutionState.Matched, [skeleton], skeleton);
        var idle = new AnimationCapture("idle", 1, 1, Guid.NewGuid(), "Test", "Idle",
            loop with { GamePath = idlePath, Resolution = matched }, null, [idlePath], [modded with { GamePath = idlePath }],
            new PoseSnapshot(0, 0, 0, false, [], DateTime.UtcNow, ""), DateTime.UtcNow, false);
        var unmatched = idle with { Id = "unmatched", Clip = idle.Clip with { Resolution = new(SkeletonResolutionState.Searching, []) } };
        var rows = InstantEdit.Ui.AnimationRows.Build([capture, idle, unmatched]);
        check(rows.Length == 3 &&
              rows[0] is { Startup: false, Group: InstantEdit.Ui.AnimationGroup.Playing, Playing: true } && rows[0].Clip == loop &&
              rows[1] is { Startup: true, Group: InstantEdit.Ui.AnimationGroup.Playing, Playing: false } && rows[1].Clip == start &&
              rows[2] is { Startup: false, Group: InstantEdit.Ui.AnimationGroup.Recent, Playing: false } && rows[2].Capture == idle &&
              rows.Select(r => r.Key).Distinct().Count() == 3 && rows.Select(r => r.View.Order).SequenceEqual([0, 1, 2]),
            "the playing animation comes first with its startup, then recent ones whose skeleton was matched");

        var search = new InstantEdit.Ui.ResourceSearch { Text = "dance01_start" };
        var found = InstantEdit.Ui.AnimationRows.Filter(rows, search);
        check(found.Count == 2 && found.All(r => r.Capture == capture),
            "searching for a startup keeps the animation it leads into");
        search.Text = "Dance Mod";
        check(InstantEdit.Ui.AnimationRows.Filter(rows, search).Count == 3 && InstantEdit.Ui.AnimationRows.Filter(rows, new()).Count == 3,
            "a search for the mod finds each animation from it, and no search shows them all");

        var source = new AnimationFileSource("chara/human/c0801/animation/a0001/bt_common/emote/dance01.pap", @"C:\mods\Dance\dance01.pap", "dance01.pap");
        check(AnimationFiles.ExportName(new AnimationFileClip(source, "cbem_dance01", 0, 1, false)) == "dance01 (cbem_dance01)" &&
              AnimationFiles.ExportName(new AnimationFileClip(source, "dance01", 0, 1, false)) == "dance01",
            "a Mod Browser clip is named after its file, and the clip too when they differ");
    }

    public static void Presentation(Action<bool, string> check)
    {
        var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/yes.pap", "x", "hash");
        var clip = new AnimationClip(resource.GamePath, "cbem_yes", 0, 0, 42, "chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb");
        AnimationCapture Capture(string display) => new("clip", 1, 1, Guid.NewGuid(), "Test", display, clip, null, [resource.GamePath],
            [resource], new PoseSnapshot(0, 0, 0, false, [], DateTime.UtcNow, ""), DateTime.UtcNow, true);
        check(AnimationPresentation.ExportName(Capture("Yes")) == "Yes (cbem_yes)",
            "a Blender action is named after the animation and its clip inside the PAP");
        check(AnimationPresentation.ExportName(Capture("CBEM_YES loop")) == "CBEM_YES loop",
            "the clip name is not repeated when the display name already has it");
    }
}
