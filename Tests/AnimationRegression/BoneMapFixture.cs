using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Previews;
using InstantEdit.Ui.Pose;

/// <summary>
/// Which bones the Bones window's map shows: the bones a model's skin weights move on the parts the game
/// draws, the bones a clip takes off their rest pose, and the map's filter with the pictures it leaves out.
/// </summary>
internal static class BoneMapFixture
{
    public static void Run(Action<bool, string> check)
    {
        Worn(check);
        Moved(check);
        Filter(check);
    }

    private static Quaternion Q(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitX, degrees * MathF.PI / 180);

    private static BoneTransform T(Vector3 position, Quaternion rotation, float scale = 1) => new(position, rotation, new Vector3(scale));

    /// <summary>
    /// Four vertices over two submeshes, the second behind attribute b: the hips under the first two, a front skirt
    /// bone half on the second and all of the third, a back skirt bone on the fourth only, and a bone it lists at no weight.
    /// </summary>
    private static ModelMesh Mesh(bool skinned = true)
    {
        var skin = new ModelSkin(2, [0, -1, 1, 0, 1, -1, 2, 3], [1, 0, .5f, .5f, 1, 0, 1, 0]);
        var part = new ModelMeshPart(0, 0, new Vector3[4], new Vector3[4], new Vector2[4],
            [new ModelSubmesh(0, [0, 1, 2], 0), new ModelSubmesh(1, [1, 2, 3], 0b10)]) { Skin = skinned ? skin : null };
        return new ModelMesh([part], ["material"], ["atr_a", "atr_b"]) { Bones = ["j_kosi", "j_sk_f_a_l", "j_sk_b_a_l", "j_ex_unused"] };
    }

    private static void Worn(Action<bool, string> check)
    {
        var bones = ModelBones.Of(Mesh());
        static string[] Sorted(IReadOnlySet<string> set) => [.. set.Order(StringComparer.Ordinal)];
        check(Sorted(bones.Weighted(null)).SequenceEqual(["j_kosi", "j_sk_b_a_l", "j_sk_f_a_l"]),
            "a model's bones are the ones a vertex has weight on, not every bone it lists");
        check(Sorted(bones.Weighted([0b10])).SequenceEqual(["j_kosi", "j_sk_b_a_l", "j_sk_f_a_l"]) &&
              Sorted(bones.Weighted([0b01])).SequenceEqual(["j_kosi", "j_sk_f_a_l"]),
            "a part the game hides by its attributes leaves out the bones only its own vertices have weight on");
        check(ModelBones.Of(Mesh(skinned: false)).Weighted(null).Count == 0, "a model without skin weights moves no bones");
    }

    private static void Moved(Action<bool, string> check)
    {
        var rest = T(Vector3.UnitY, Q(10));
        string[] names = ["n_root", "j_still", "j_noise", "j_flipped", "j_turned", "j_slid", "j_held", "j_scaled"];
        var skeleton = new SkeletonDescription("rig", "rig-print",
            [.. names.Select((name, i) => new SkeletonBone(name, (short)(i == 0 ? -1 : 0), 0, rest))], [], [], []);
        int Bone(string name) => Array.IndexOf(names, name);
        var poses = Enumerable.Range(0, 10).Select(_ => Enumerable.Repeat(rest, names.Length).ToArray()).ToArray();
        // As vanilla clips hold the chest bones: 0.17 degrees off rest, and a hair off its place.
        poses[4][Bone("j_noise")] = T(Vector3.UnitY + new Vector3(.0002f, 0, 0), Q(10.17f));
        poses[3][Bone("j_flipped")] = T(Vector3.UnitY, Quaternion.Negate(Q(10)));
        poses[5][Bone("j_turned")] = T(Vector3.UnitY, Q(11));
        poses[7][Bone("j_slid")] = T(Vector3.UnitY + new Vector3(0, .002f, 0), Q(10));
        foreach (var pose in poses)
            pose[Bone("j_held")] = T(Vector3.UnitY, Q(25));
        poses[2][Bone("j_scaled")] = T(Vector3.UnitY, Q(10), 1.02f);
        var moved = AnimationBones.Moved(MotionClip.FromPoses(skeleton, 1, poses));
        check(moved.Order(StringComparer.Ordinal).SequenceEqual(["j_held", "j_scaled", "j_slid", "j_turned"]),
            "a clip moves the bones it turns, moves or scales off their rest pose somewhere, or holds off it");
        check(!moved.Contains("j_still") && !moved.Contains("n_root") && !moved.Contains("j_noise") && !moved.Contains("j_flipped"),
            "a bone held at rest, held as far off it as vanilla clips hold the chest, or the same turn with the other sign, doesn't count as moved");
    }

    private static void Filter(Action<bool, string> check)
    {
        IReadOnlySet<string> used = new HashSet<string>(StringComparer.Ordinal) { "j_kosi", "n_kataarmor_r", "iv_asi_oya_a_l" };
        check(PoseMap.Shows("j_sk_f_a_l", null, false) && PoseMap.Shows("j_kosi", used, false) && !PoseMap.Shows("j_sk_f_a_l", used, false),
            "with no bones to go by every dot shows; with them only the used ones do");
        check(PoseMap.Shows("j_sk_f_a_l", used, true) && PoseMap.Shows("n_hara", used, false) && PoseMap.Handles.SetEquals(["n_hara"]),
            "a selected bone, one the layer changes and the body's handle keep their dot");
        check(PoseMap.Extras(bone => PoseMap.Shows(bone, used, false)) == (true, true, false, false),
            "a picture shows with a used dot on either side: the armor for the right shoulder's, the toes for an IVCS toe");
        IReadOnlySet<string> body = new HashSet<string>(StringComparer.Ordinal) { "j_kosi", "j_asi_e_l" };
        check(PoseMap.Extras(bone => PoseMap.Shows(bone, body, false)) == (false, false, false, false),
            "the foot alone, which the body picture shows, brings no toes picture, and no used bone of theirs no armor, groin or tail");
        check(PoseMap.Extras(_ => true) == (true, true, true, true), "with every bone shown every picture is placed");
    }
}
