using System.Numerics;
using System.Text;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Pose;

/// <summary>
/// Key pose sources without the game: .pose files as Anamnesis, Ktisis and Brio write them, poses made
/// local on the clip's skeleton with their moved bones and proportions, and keying them into a pose layer.
/// </summary>
internal static class PoseFileFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Files(check, reject);
        Model(check);
        Local(check);
        Keys(check);
    }

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;

    private static bool Near(Vector3 a, Vector3 b, float tolerance = 1e-4f) => Vector3.Distance(a, b) < tolerance;

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);

    private static byte[] Json(string text, bool bom = false) => [.. bom ? new byte[] { 0xEF, 0xBB, 0xBF } : [], .. Encoding.UTF8.GetBytes(text)];

    /// <summary>A small body: the root, the body's place at hip height, a spine, an arm with its hand and a tail, with turned rests.</summary>
    private static SkeletonDescription Body(float proportions = 1) => new("body", "body-print",
    [
        new("n_root", -1, 0, T(Vector3.Zero)),
        new("n_hara", 0, 0, T(new Vector3(0, 1, 0))),
        new("j_kosi", 1, 0, T(new Vector3(0, .05f, 0) * proportions, Q(Vector3.UnitZ, 90))),
        new("j_sebo_a", 2, 0, T(new Vector3(.1f, 0, 0) * proportions, Q(Vector3.UnitX, 10))),
        new("j_ude_a_l", 3, 0, T(new Vector3(.2f, .15f, 0) * proportions, Q(Vector3.UnitY, -40))),
        new("j_te_l", 4, 0, T(new Vector3(.25f, 0, 0) * proportions, Q(Vector3.UnitZ, 15))),
        new("n_sippo_a", 2, 0, T(new Vector3(-.1f, 0, -.05f) * proportions)),
    ], [], [], []);

    private static MotionTransform[] Rest(SkeletonDescription skeleton) => [.. skeleton.Bones.Select(bone => MotionTransform.From(bone.Reference))];

    private static void Files(Action<bool, string> check, Action<Action, string> reject)
    {
        const string anamnesis = """
            {
              "FileExtension": ".pose",
              "TypeName": "Anamnesis Pose",
              "Position": "26.257, 7.87, 159.815",
              "Bones": {
                "n_root": { "Position": "0, 0, 0", "Rotation": "0, 0, 0, 0.99999994", "Scale": "1, 1, 1" },
                "n_hara": { "Position": "0.032729417, 0.9073141, 0.038458284", "Rotation": "0, 0, 0, 0.9833072", "Scale": "0.9922484, 0.9922484, 0.9922484" },
                "j_kosi": { "Position": "-1.18869714E-07, 0.97, 1.6264205E-07", "Rotation": "0, 0, 0, 0" },
                "j_short": { "Position": "1, 2", "Rotation": "0, 0, 0, 1" },
                "j_nan": { "Position": "NaN, 0, 0", "Rotation": "0, 0, 0, 1" },
                "j_object": { "Position": { "X": 1, "Y": 2, "Z": 3 }, "Rotation": { "X": 0, "Y": 0, "Z": 0, "W": 2 } }
              },
              "MainHand": { "n_root": { "Position": "9, 9, 9", "Rotation": "0, 0, 0, 1", "Scale": "1, 1, 1" } }
            }
            """;
        var file = PoseFiles.Parse(Json(anamnesis, bom: true));
        check(file.Tool == "Anamnesis" && file.Bones.Count == 4 && !file.Bones.ContainsKey("j_short") && !file.Bones.ContainsKey("j_nan"),
            "a .pose file's readable bones are read, byte order mark or not, and the weapons left out");
        check(Math.Abs(file.Bones["n_hara"].Rotation.Length() - 1) < 1e-5f && Degrees(file.Bones["n_hara"].Rotation, Quaternion.Identity) < 1e-3f &&
              file.Bones["j_kosi"].Rotation == Quaternion.Identity && file.Bones["j_kosi"].Scale == Vector3.One &&
              file.Bones["n_hara"].Scale == new Vector3(.9922484f),
            "rotations are normalised, one of no length is none, and a missing scale is one");
        check(Near(file.Bones["n_hara"].Position, new Vector3(.032729417f, .9073141f, .038458284f), 1e-6f) &&
              Near(file.Bones["j_kosi"].Position, new Vector3(0, .97f, 0), 1e-6f) && file.Bones["j_object"].Position == new Vector3(1, 2, 3),
            "numbers are read whatever the system's language, as strings or as objects");
        check(PoseFiles.Parse(Json("""{"TypeName":"Ktisis Pose","Bones":{"j_kao":{"Position":"0, 1, 0","Rotation":"0, 0, 0, 1"}}}""")).Tool == "Ktisis" &&
              PoseFiles.Parse(Json("""{"Bones":{"j_kao":{"Position":"0, 1, 0","Rotation":"0, 0, 0, 1"}}}""")).Tool == "",
            "the tool that wrote the pose is named when the file says");
        reject(() => PoseFiles.Parse(Json("not a pose")), "a file that isn't JSON is refused");
        reject(() => PoseFiles.Parse(Json("""{"TypeName":"Brio Pose"}""")), "a file without bones is refused");
        reject(() => PoseFiles.Parse(Json("""{"Bones":{"j_kao":{"Position":"x"}}}""")), "a file with no readable bone is refused");
    }

    /// <summary>A pose on <see cref="Body"/>: the spine bent, the arm raised, the body lowered and moved forward.</summary>
    private static MotionTransform[] Posed(SkeletonDescription skeleton)
    {
        var local = Rest(skeleton);
        local[1] = local[1] with { Position = new Vector3(0, .4f, .2f), Rotation = Q(Vector3.UnitX, -80) };
        local[2] = local[2] with { Rotation = local[2].Rotation * Q(Vector3.UnitY, 25) };
        local[4] = local[4] with { Rotation = local[4].Rotation * Q(Vector3.UnitZ, 70) };
        local[5] = local[5] with { Rotation = local[5].Rotation * Q(Vector3.UnitX, -30) };
        return local;
    }

    private static Dictionary<string, MotionTransform> File(SkeletonDescription skeleton, MotionTransform[] local)
    {
        var model = MotionEvaluator.ModelSpace(skeleton, local);
        return skeleton.Bones.Select((bone, i) => (bone.Name, model[i])).ToDictionary(pair => pair.Name, pair => pair.Item2);
    }

    private static void Model(Action<bool, string> check)
    {
        var body = Body();
        var posed = Posed(body);
        var file = File(body, posed);
        var pose = PoseImports.FromModel(body, file, Rest(body));
        check(body.Bones.Select((bone, i) => (bone.Name, i)).All(pair => pose.Local[pair.Name] is var local &&
                  Degrees(local.Rotation, posed[pair.i].Rotation) < 1e-2f && Near(local.Position, posed[pair.i].Position)),
            "a pose saved in model space comes back as the same local pose on the skeleton");
        check(pose.Moved.SetEquals(["n_hara"]) && !pose.OtherProportions && pose.Missing == 0,
            "only the bone the pose moved counts as moved, on a body of the same proportions");

        // The file lacks the spine: it takes the clip's own pose, and the arm below still turns as saved.
        file.Remove("j_sebo_a");
        file["j_extra"] = MotionTransform.Identity;
        var partial = PoseImports.FromModel(body, file, posed);
        check(!partial.Local.ContainsKey("j_sebo_a") && Degrees(partial.Local["j_ude_a_l"].Rotation, posed[4].Rotation) < 1e-2f && partial.Missing == 1,
            "a bone the file lacks is filled in from the clip, and bones the skeleton lacks are counted");

        // A moved hand counts; a tail sitting elsewhere doesn't, since other skeletons put tails elsewhere anyway.
        var reached = posed.ToArray();
        reached[5] = reached[5] with { Position = reached[5].Position + new Vector3(0, .02f, 0) };
        reached[6] = reached[6] with { Position = reached[6].Position + new Vector3(.05f, 0, 0) };
        check(PoseImports.FromModel(body, File(body, reached), Rest(body)).Moved.SetEquals(["n_hara", "j_te_l"]),
            "a moved body bone counts as moved, a tail node elsewhere doesn't");

        // Saved on a body a quarter larger: every bone sits elsewhere, so the pose's positions don't fit.
        var larger = Body(1.25f);
        check(PoseImports.FromModel(body, File(larger, Posed(larger)), Rest(body)).OtherProportions &&
              !PoseImports.FromModel(larger, File(larger, Posed(larger)), Rest(larger)).OtherProportions,
            "a pose made on a body of other proportions is told apart");
    }

    private static void Local(Action<bool, string> check)
    {
        var body = Body();
        var posed = Posed(body);
        var same = PoseImports.FromLocal(body, body.Bones.Select((bone, i) => (bone.Name, posed[i], bone.Reference.Position)).Append(("iv_extra", MotionTransform.Identity, Vector3.Zero)));
        check(same.Moved.SetEquals(["n_hara"]) && !same.OtherProportions && same.Missing == 1 && Degrees(same.Local["j_te_l"].Rotation, posed[5].Rotation) < 1e-3f,
            "another clip's frame keeps its rotations, and its moves are judged against its own rest pose");
        var larger = Body(1.25f);
        var rest = Rest(larger);
        var other = PoseImports.FromLocal(body, larger.Bones.Select((bone, i) => (bone.Name, rest[i], bone.Reference.Position)));
        check(other.Moved.Count == 0 && other.OtherProportions, "a frame of a clip on another body moves nothing at rest, and its proportions differ");
    }

    private static void Keys(Action<bool, string> check)
    {
        var body = Body();
        var pose = PoseImports.FromModel(body, File(body, Posed(body)), Rest(body));
        var doc = new TimelineDocument(30, 30, null, [], 1)
        {
            Motion = [new("m1", MotionLayerKind.Pose, "P", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear, [], [])],
            NextMotionKey = 2,
        };
        var rest = Rest(body);
        var index = MotionEvaluator.Index(body);
        MotionTransform? Own(string bone) => index.TryGetValue(bone, out var at) ? rest[at] with { Scale = new Vector3(1.1f) } : null;
        var keyed = PoseEdits.KeyPose(doc, "m1", 12, pose, ["n_hara", "j_ude_a_l", "j_nowhere"], true, Own);
        var key = keyed.Motion[0].Keys.Single();
        check(key.Frame == 12 && key.Bones.Count == 2 && key.Moved.SequenceEqual(["n_hara"]) &&
              Near(key.Bones["n_hara"].Position, pose.Local["n_hara"].Position) && Near(key.Bones["j_ude_a_l"].Position, rest[4].Position) &&
              Degrees(key.Bones["j_ude_a_l"].Rotation, pose.Local["j_ude_a_l"].Rotation) < 1e-3f && key.Bones["j_ude_a_l"].Scale == new Vector3(1.1f),
            "a pose keys its rotations, its positions where it moves bones, and leaves scale to the clip");
        var turned = PoseEdits.KeyPose(doc, "m1", 12, pose, ["n_hara"], false, Own).Motion[0].Keys.Single();
        check(turned.Moved.IsEmpty && Near(turned.Bones["n_hara"].Position, rest[1].Position), "without positions a pose only turns bones");

        string[] available = ["n_root", "n_hara", "j_kosi", "j_f_mayu_l", "j_sk_f_a_l", "j_kami_a", "j_te_l"];
        var leftOut = PoseMap.Groups.Where(group => group.Name is "Hair" or "Skirt");
        check(PoseEdits.PoseBones(available, null, leftOut).SequenceEqual(["n_hara", "j_kosi", "j_te_l"]) &&
              PoseEdits.PoseBones(available, ["j_te_l", "j_sk_f_a_l", "j_gone"], leftOut).SequenceEqual(["j_sk_f_a_l", "j_te_l"]),
            "the whole body leaves out the root nodes, the face and the chosen groups; selected bones are keyed as chosen");
    }
}
