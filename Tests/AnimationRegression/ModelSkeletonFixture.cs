using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Skeletons;

/// <summary>
/// The game skeletons sent with imports, for the armature Blender builds: which skeleton files a
/// model is bound to, EST tables and their collection overrides, the connect bone of a partial
/// skeleton file, merging a character's skeletons into one hierarchy, and the JSON the add-on reads
/// (Blender-Addon/testing/skeleton_regression.py builds armatures from the same shape).
/// </summary>
internal static class ModelSkeletonFixture
{
    private static BoneTransform T(float x, float y, float z, Quaternion? rotation = null) =>
        new(new(x, y, z), rotation ?? Quaternion.Identity, Vector3.One);

    private static Quaternion Q(float x, float y, float z, float degrees) =>
        Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(x, y, z)), degrees * MathF.PI / 180);

    private static SkeletonDescription Skeleton(string name, params SkeletonBone[] bones) => new(name, name, [.. bones], [], [], []);

    private static SkeletonBone B(string name, int parent, BoneTransform reference) => new(name, (short)parent, 0, reference);

    // A body: the root, hips turned about Z, a spine, the neck and head, and the jaw.
    public static SkeletonDescription Body() => Skeleton("body",
        B("n_root", -1, T(0, 0, 0)),
        B("j_kosi", 0, T(0, 1.02f, 0.03f, Q(0, 0, 1, 12))),
        B("j_sebo_a", 1, T(0.08f, 0.04f, 0, Q(1, 0, 0, 21))),
        B("j_sebo_c", 2, T(0.2f, 0.01f, 0.02f, Q(0.3f, 1, 0.2f, 35))),
        B("j_kao", 3, T(0.1f, 0.3f, -0.02f, Q(0, 1, 0, -90))),
        B("j_ago", 4, T(0.02f, -0.01f, 0.03f, Q(1, 0, 0, 8))));

    private static Matrix4x4 Local(BoneTransform t) =>
        Matrix4x4.CreateFromQuaternion(t.Rotation) * Matrix4x4.CreateTranslation(t.Position);

    /// <summary>Model-space matrices (row-vector convention) from rotations and positions.</summary>
    private static Matrix4x4[] Model<T>(IReadOnlyList<T> bones, Func<T, int> parent, Func<T, BoneTransform> reference)
    {
        var result = new Matrix4x4[bones.Count];
        for (var i = 0; i < bones.Count; i++)
            result[i] = parent(bones[i]) < 0 ? Local(reference(bones[i])) : Local(reference(bones[i])) * result[parent(bones[i])];
        return result;
    }

    private static bool Near(Matrix4x4 a, Matrix4x4 b, float tolerance = 2e-6f)
    {
        for (var row = 0; row < 4; row++)
            for (var column = 0; column < 4; column++)
                if (MathF.Abs(a[row, column] - b[row, column]) > tolerance) return false;
        return true;
    }

    public static ModelSkeleton Sample()
    {
        var body = Body();
        var bodyModel = Model(body.Bones, b => b.Parent, b => b.Reference);
        Matrix4x4.Decompose(bodyModel[4], out _, out var kaoRotation, out var kaoPosition);
        Matrix4x4.Decompose(bodyModel[3], out _, out var seboRotation, out var seboPosition);
        // A face skeleton rooted at the head where the body puts it, with its own jaw.
        var face = Skeleton("face",
            B("j_kao", -1, new BoneTransform(kaoPosition, kaoRotation, Vector3.One)),
            B("j_ago", 0, T(0.03f, -0.02f, 0.04f, Q(1, 0, 0, 5))),
            B("j_f_face", 0, T(0.01f, 0, 0.02f, Q(0, 0, 1, 90))),
            B("j_f_eye_l", 2, T(0.03f, 0.05f, 0.06f, Q(0.2f, 1, 0, 14))));
        // A vanilla top skeleton: its root has no body counterpart and hangs from the header's bone.
        var top = Skeleton("top",
            B("n_ex_top", -1, new BoneTransform(seboPosition + new Vector3(0, 0.01f, 0), seboRotation, Vector3.One)),
            B("j_ex_top_a", 0, T(0, -0.2f, 0.1f, Q(1, 0, 0, 30))));
        var warnings = new List<string>();
        var bones = ModelSkeletonMerge.Merge([
            new("chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb", body),
            new("chara/human/c0101/skeleton/face/f0003/skl_c0101f0003.sklb", face, 4),
            new("chara/human/c0101/skeleton/top/t0100/skl_c0101t0100.sklb", top, 3),
        ], warnings);
        return new ModelSkeleton(ModelSkeleton.FilesSource, "c0101",
            ["chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb"], bones, [.. warnings]);
    }

    private static byte[] Est(params (ushort Set, ushort GenderRace, ushort Skeleton)[] entries)
    {
        var bytes = new byte[4 + entries.Length * 6];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4 + i * 4), entries[i].Set);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6 + i * 4), entries[i].GenderRace);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4 + entries.Length * 4 + i * 2), entries[i].Skeleton);
        }
        return bytes;
    }

    private static byte[] SklbHeader(string version, ushort connect, params ushort[] connectList)
    {
        var bytes = new byte[0x40];
        "blks"u8.CopyTo(bytes);
        Encoding.ASCII.GetBytes(version).CopyTo(bytes, 4);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x10), connect);
        for (var i = 0; i < 4; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x28 + 2 * i), i < connectList.Length ? connectList[i] : ushort.MaxValue);
        return bytes;
    }

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        // Which skeletons a model is bound to.
        var top = ModelSkeletonPaths.Parse("chara/equipment/e6001/model/c0201e6001_top.mdl");
        check(top is { Id: "c0201", Kind: 'e', Set: 6001, Slot: "top", Human: true, GenderRace: 201 } &&
              ModelSkeletonPaths.BasePath(top) == "chara/human/c0201/skeleton/base/b0001/skl_c0201b0001.sklb" &&
              ModelSkeletonPaths.Extra(top) == new EstRequest(EstSlot.Body, 6001),
            "a top's skeleton is its race's body and the top skeleton EST picks for its set");
        var face = ModelSkeletonPaths.Parse(@"C:\Mods\Faces\c1801f0001_fac.mdl");
        check(face is { Id: "c1801", Kind: 'f', Set: 1 } && ModelSkeletonPaths.Extra(face) == new EstRequest(EstSlot.Face, 1) &&
              ModelSkeletonPaths.PartialPath(face.Id, EstSlot.Face, 2) == "chara/human/c1801/skeleton/face/f0002/skl_c1801f0002.sklb",
            "a face model file on disk is read by its name and picks its face skeleton through EST");
        check(ModelSkeletonPaths.Parse("chara/human/c0101/obj/hair/h0113/model/c0101h0113_hir.mdl") is { } hair &&
              ModelSkeletonPaths.Extra(hair) == new EstRequest(EstSlot.Hair, 113) &&
              ModelSkeletonPaths.PartialPath("c0101", EstSlot.Hair, 114) == "chara/human/c0101/skeleton/hair/h0114/skl_c0101h0114.sklb",
            "a hairstyle's skeleton comes from the hair EST table, not the hairstyle's number");
        check(ModelSkeletonPaths.Parse("chara/equipment/e0005/model/c0101e0005_met.mdl") is { } met &&
              ModelSkeletonPaths.Extra(met) == new EstRequest(EstSlot.Head, 5) &&
              ModelSkeletonPaths.Parse("chara/equipment/e0005/model/c0101e0005_glv.mdl") is { } gloves &&
              ModelSkeletonPaths.Extra(gloves) == null &&
              ModelSkeletonPaths.Parse("chara/accessory/a0001/model/c0201a0001_ear.mdl") is { } ear && ModelSkeletonPaths.Extra(ear) == null,
            "headgear picks a met skeleton; gloves and accessories use the body alone");
        check(ModelSkeletonPaths.Parse("chara/human/c1801/obj/zear/z0001/model/c1801z0001_zer.mdl") is { } zear &&
              ModelSkeletonPaths.Extra(zear) is { Slot: EstSlot.Face, Set: 1, Note: not null },
            "Viera ears use a face skeleton, and say which");
        check(ModelSkeletonPaths.Parse("chara/weapon/w0101/obj/body/b0001/model/w0101b0001.mdl") is { } weapon &&
              ModelSkeletonPaths.BasePath(weapon) == "chara/weapon/w0101/skeleton/base/b0001/skl_w0101b0001.sklb" &&
              ModelSkeletonPaths.Parse("chara/monster/m0001/obj/body/b0001/model/m0001b0001.mdl") is { } monster &&
              ModelSkeletonPaths.BasePath(monster) == "chara/monster/m0001/skeleton/base/b0001/skl_m0001b0001.sklb" &&
              ModelSkeletonPaths.Parse("chara/demihuman/d1001/obj/equipment/e0001/model/d1001e0001_top.mdl") is { } demihuman &&
              ModelSkeletonPaths.BasePath(demihuman) == "chara/demihuman/d1001/skeleton/base/b0001/skl_d1001b0001.sklb" &&
              ModelSkeletonPaths.Extra(demihuman) == null,
            "weapons, monsters and demihumans use their own base skeleton");
        check(ModelSkeletonPaths.Parse("bg/ffxiv/wil_w1/twn/w1t1/bgparts/w1t1_a1_hous1.mdl") == null &&
              ModelSkeletonPaths.Parse("chara/monster/m0001/obj/body/b0001/model/m0001e0001.mdl") == null &&
              ModelSkeletonPaths.Parse("my edit.mdl") == null && ModelSkeletonPaths.Parse(null) == null &&
              ModelSkeletonPaths.Parse("c0101e0001_top.mdl.bak") == null,
            "other models and files not named like the game's have no known skeleton");

        // EST tables and a collection's edits.
        var table = EstTable.Parse(Est((1, 101, 3), (2, 101, 2), (1, 201, 2), (113, 101, 114)));
        check(table.Count == 4 && table[101, 1] == 3 && table[201, 1] == 2 && table[101, 113] == 114 && table[101, 7] == 0 &&
              table[101, -1] == 0 && table[101, 70000] == 0,
            "an EST table maps a race and set to a skeleton, and to none when absent");
        reject(() => EstTable.Parse(new byte[3]), "a truncated EST table is refused");
        reject(() => EstTable.Parse(Est((1, 101, 3)).AsSpan(0, 9)), "an EST table shorter than its count is refused");
        var metadata = JsonNode.Parse("""
            [
              {"Type": "Imc", "Manipulation": {"ObjectType": "Equipment"}},
              {"Type": "Est", "Manipulation": {"Gender": "Male", "Race": "Midlander", "SetId": 1, "Slot": "Face", "Entry": 7}},
              {"Type": "Est", "Manipulation": {"Gender": "Female", "Race": "Midlander", "SetId": 1, "Slot": "Face", "Entry": 9}},
              {"Type": "Est", "Manipulation": {"Gender": "Male", "Race": "Midlander", "SetId": 1, "Slot": "Hair", "Entry": 0}},
              {"Type": "Est", "Manipulation": {"Gender": 3, "Race": "Midlander", "SetId": 2, "Slot": "Face", "Entry": 1}}
            ]
            """)!.AsArray();
        check(EstTable.Override(metadata, EstSlot.Face, 101, 1) == 7 && EstTable.Override(metadata, EstSlot.Face, 201, 1) == 9 &&
              EstTable.Override(metadata, EstSlot.Hair, 101, 1) == 0 && EstTable.Override(metadata, EstSlot.Face, 101, 2) == null &&
              EstTable.Override(metadata, EstSlot.Head, 101, 1) == null,
            "a collection's EST edits override the game's table for their race, set and slot only");

        // The bone a partial skeleton file hangs from.
        check(SkeletonFileHeader.ConnectBone(SklbHeader("0031", 46)) == 46 &&
              SkeletonFileHeader.ConnectBone(SklbHeader("0031", ushort.MaxValue)) == null &&
              SkeletonFileHeader.ConnectBone(SklbHeader("1031", 0, 46, 51)) == 46 &&
              SkeletonFileHeader.ConnectBone(SklbHeader("1031", 0)) == null &&
              SkeletonFileHeader.ConnectBone(SklbHeader("0021", 46)) == null &&
              SkeletonFileHeader.ConnectBone(new byte[16]) == null,
            "skeleton file headers name the body bone a partial skeleton connects to");

        // Merging a character's skeletons.
        var sample = Sample();
        var names = sample.Bones.Select(b => b.Name).ToArray();
        check(names.SequenceEqual(["n_root", "j_kosi", "j_sebo_a", "j_sebo_c", "j_kao", "j_ago", "j_f_face", "j_f_eye_l", "n_ex_top", "j_ex_top_a"]),
            "the body's bones come first, and a partial skeleton adds only bones the body lacks");
        var byName = names.Select((name, index) => (name, index)).ToDictionary(p => p.name, p => p.index);
        check(sample.Bones[byName["j_f_face"]].Parent == byName["j_kao"] && sample.Bones[byName["j_f_eye_l"]].Parent == byName["j_f_face"] &&
              sample.Bones[byName["n_ex_top"]].Parent == byName["j_sebo_c"] && sample.Bones[byName["j_ex_top_a"]].Parent == byName["n_ex_top"],
            "a partial skeleton hangs from the body bone of its root's name, else from the bone its file names");
        check(sample.Bones.Take(6).Select(b => b.Reference).SequenceEqual(Body().Bones.Select(b => b.Reference)) &&
              sample.Bones[byName["j_f_eye_l"]].Reference == T(0.03f, 0.05f, 0.06f, Q(0.2f, 1, 0, 14)),
            "reference transforms within one skeleton are kept exactly");
        var merged = Model(sample.Bones, b => b.Parent, b => b.Reference);
        var bodyModel = Model(Body().Bones, b => b.Parent, b => b.Reference);
        Matrix4x4.Decompose(bodyModel[3], out _, out var seboRotation, out var seboPosition);
        var expectedTop = Local(new BoneTransform(seboPosition + new Vector3(0, 0.01f, 0), seboRotation, Vector3.One));
        check(Near(merged[byName["j_f_face"]], Local(T(0.01f, 0, 0.02f, Q(0, 0, 1, 90))) * bodyModel[4]) &&
              Near(merged[byName["n_ex_top"]], expectedTop) &&
              Near(merged[byName["j_ex_top_a"]], Local(T(0, -0.2f, 0.1f, Q(1, 0, 0, 30))) * expectedTop) &&
              Near(merged[byName["j_ago"]], bodyModel[5]),
            "every merged bone keeps its place in its own skeleton's model space; a shared bone is the body's");
        check(sample.Warnings.IsEmpty, "a fully connected character merges without warnings");

        var loose = new List<string>();
        var looseBones = ModelSkeletonMerge.Merge([
            new("body.sklb", Body()),
            new("chara/human/c0101/skeleton/met/m0999/skl_c0101m0999.sklb",
                Skeleton("met", B("j_unknown", -1, T(0, 1.5f, 0)), B("j_ex_met_a", 0, T(0, 0.1f, 0)))),
        ], loose);
        check(looseBones[^2] is { Name: "j_unknown", Parent: -1 } && looseBones[^2].Reference == T(0, 1.5f, 0) &&
              loose.Count == 1 && loose[0].Contains("skl_c0101m0999.sklb"),
            "a partial root no body bone connects stays a root at its own place, with a warning");
        // Mod top skeletons with several roots named like body bones.
        var roots = ModelSkeletonMerge.Merge([
            new("body.sklb", Body()),
            new("top.sklb", Skeleton("top", B("j_sebo_a", -1, T(0, 1.1f, 0)), B("j_kao", -1, T(0, 1.6f, 0)),
                B("j_ex_top_b", 0, T(0.1f, 0, 0)), B("j_ex_top_c", 1, T(0.2f, 0, 0))), 3),
        ], new List<string>());
        check(roots[^2] is { Name: "j_ex_top_b", Parent: 2 } && roots[^1] is { Name: "j_ex_top_c", Parent: 4 },
            "every root of a partial skeleton merges with the body bone of its name");
        reject(() => ModelSkeletonMerge.Merge([], new List<string>()), "a skeleton needs a body");

        // What the add-on reads.
        var json = JsonSerializer.Serialize(new { skeleton = sample.ToPayload() });
        using var document = JsonDocument.Parse(json);
        var payload = document.RootElement.GetProperty("skeleton");
        var first = payload.GetProperty("bones")[1];
        check(payload.GetProperty("schema").GetString() == "instant-edit.skeleton" && payload.GetProperty("version").GetInt32() == 1 &&
              payload.GetProperty("source").GetString() == "files" && payload.GetProperty("race").GetString() == "c0101" &&
              payload.GetProperty("bones").GetArrayLength() == 10 && first.GetProperty("name").GetString() == "j_kosi" &&
              first.GetProperty("parent").GetInt32() == 0 && first.GetProperty("reference").GetArrayLength() == AnimationTake.Stride &&
              Math.Abs(first.GetProperty("reference")[1].GetSingle() - 1.02f) < 1e-6,
            "the skeleton reaches Blender as bones with parent indices and reference transforms");
    }

    /// <summary>The sample skeleton as the import request carries it.</summary>
    public static string SamplePayload() =>
        JsonSerializer.Serialize(Sample().ToPayload(), new JsonSerializerOptions { WriteIndented = true });
}
