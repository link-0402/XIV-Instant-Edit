using System.Numerics;
using System.Text.RegularExpressions;
using InstantEdit.Services.Physics;
using InstantEdit.Services.Previews;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Fitting collision to a measured body: bone frames as Havok composes them, skin posed through the
/// rest and live pose, area-weighted measures, and the sizes the fit gives limbs, skirt shapes and body
/// area tips on a synthetic body of known sizes. With the game installed, the game's own smallclothes
/// are read with their skin weights and posed.
/// </summary>
internal static class PhysicsFitScenarios
{
    public static void Run()
    {
        CheckFrames();
        CheckPercentile();
        CheckDrawnModels();
        CheckFit();
        CheckFitApply();
        CheckTipPlace();
        CheckMirror();
    }

    private static void CheckDrawnModels()
    {
        Require(PhysicsPaths.IsBareBody("chara/equipment/e0000/model/c0201e0000_top.mdl") && PhysicsPaths.IsBareBody("chara/equipment/e0279/model/c0201e0279_dwn.mdl") &&
                !PhysicsPaths.IsBareBody("chara/equipment/e6016/model/c0201e6016_top.mdl"),
            "physics fit: smallclothes and the Emperor's New gear count as bare body, other gear doesn't");
        const string modFile = @"G:\Penumbra\Some Body\chara\equipment\e0000\model\c0201e0000_top.mdl";
        var resolved = new Dictionary<string, HashSet<string>>
        {
            [modFile] = ["chara/equipment/e0000/model/c0201e0000_top.mdl", "chara/equipment/e0279/model/c0201e0279_top.mdl"],
        };
        var drawn = InstantEdit.Services.Painter.PainterVisibility.NormalizePath(modFile);
        Require(PhysicsPaths.ResolveDrawn(resolved, drawn, 1) is (modFile, "chara/equipment/e0000/model/c0201e0000_top.mdl"),
            "physics fit: a drawn model is read from the file Penumbra loads, with its slot's game path");
        Require(PhysicsPaths.ResolveDrawn(resolved, "chara/equipment/e0000/model/c0201e0000_glv.mdl", 2) is
                    ("chara/equipment/e0000/model/c0201e0000_glv.mdl", "chara/equipment/e0000/model/c0201e0000_glv.mdl") &&
                PhysicsPaths.ResolveDrawn(resolved, "g:/penumbra/elsewhere/c0201e0000_sho.mdl", 4) is null,
            "physics fit: a game file is its own game path, and a mod file Penumbra doesn't list isn't guessed");
    }

    private static void CheckFrames()
    {
        var frame = new PhysicsFrame(new Vector3(0.1f, 1, -0.2f), Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.4f, -0.3f, 1.1f)),
            new Vector3(1.3f, 0.7f, 2f));
        var point = new Vector3(0.3f, -0.05f, 0.12f);
        Require(Vector3.Distance(frame.ToModel(frame.ToLocal(point)), point) < 1e-5f,
            "physics fit: a point taken into a scaled, turned bone's space and back stays put");
        Require(Vector3.Distance(frame.Place(Vector3.UnitX), frame.Position + Vector3.Transform(Vector3.UnitX, frame.Rotation)) < 1e-6f,
            "physics fit: physics offsets turn and move with the bone but never scale, as physics runs before Customize+");

        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var skeleton = new PhysicsSkeleton(
        [
            new PhysicsBone("n_root", "", new Vector3(0, 1, 0), turn),
            new PhysicsBone("j_kosi", "n_root", new Vector3(0.5f, 0, 0), Quaternion.Identity),
        ]);
        var rest = PhysicsBody.RestPose(skeleton);
        Require(Vector3.Distance(rest["j_kosi"].Position, new Vector3(0, 1.5f, 0)) < 1e-5f && rest["j_kosi"].Scale == Vector3.One,
            "physics fit: rest places compose like Havok's, the parent turning and moving its child");
    }

    private static void CheckPercentile()
    {
        var values = new List<(float Value, float Area)> { (2, 3), (1, 1) };
        Require(PhysicsFit.Percentile(values, 0.5f) == 2 && PhysicsFit.Percentile(values, 0.2f) == 1 && PhysicsFit.Percentile([], 0.5f) is null,
            "physics fit: measures count skin by area, not by how densely a mesh is made");
    }

    /// <summary>
    /// A body with a thigh 7 cm thick, a forearm 3 cm thick and a breast dome 6 cm wide in front of its bone,
    /// with bones laid out in model space as a live pose would have them.
    /// </summary>
    internal static (PhysicsBody Body, PhysicsSkeleton Skeleton) SyntheticBody()
    {
        var skeleton = new PhysicsSkeleton(
        [
            new PhysicsBone("n_root", "", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("j_kosi", "n_root", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("j_asi_a_l", "j_kosi", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("ya_daitai_phys_l", "j_asi_a_l", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("j_asi_b_l", "j_asi_a_l", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("j_sk_f_a_l", "j_kosi", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("j_sebo_b", "j_kosi", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("j_mune_l", "j_sebo_b", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("iv_c_mune_l", "j_mune_l", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("j_ude_b_l", "j_sebo_b", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("n_hhiji_l", "j_ude_b_l", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("j_te_l", "j_ude_b_l", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("j_kami_a", "j_sebo_b", Vector3.Zero, Quaternion.Identity),
        ]);
        // The breast bone's X axis points forward (+Z), as j_mune points out of the chest.
        var forward = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2);
        var pose = new Dictionary<string, PhysicsFrame>(StringComparer.Ordinal)
        {
            ["n_root"] = PhysicsFrame.Rest(Vector3.Zero, Quaternion.Identity),
            ["j_kosi"] = PhysicsFrame.Rest(new Vector3(0, 1, 0), Quaternion.Identity),
            ["j_asi_a_l"] = PhysicsFrame.Rest(new Vector3(0.1f, 0.9f, 0), Quaternion.Identity),
            ["ya_daitai_phys_l"] = PhysicsFrame.Rest(new Vector3(0.1f, 0.8f, 0.03f), Quaternion.Identity),
            ["j_asi_b_l"] = PhysicsFrame.Rest(new Vector3(0.1f, 0.5f, 0), Quaternion.Identity),
            ["j_sk_f_a_l"] = PhysicsFrame.Rest(new Vector3(0.1f, 0.95f, 0.08f), Quaternion.Identity),
            ["j_sebo_b"] = PhysicsFrame.Rest(new Vector3(0, 1.2f, 0), Quaternion.Identity),
            ["j_mune_l"] = PhysicsFrame.Rest(new Vector3(0.08f, 1.2f, 0), forward),
            ["iv_c_mune_l"] = PhysicsFrame.Rest(new Vector3(0.08f, 1.2f, 0.05f), forward),
            ["j_ude_b_l"] = PhysicsFrame.Rest(new Vector3(0.4f, 1.2f, 0), Quaternion.Identity),
            ["n_hhiji_l"] = PhysicsFrame.Rest(new Vector3(0.4f, 1.2f, 0), Quaternion.Identity),
            ["j_te_l"] = PhysicsFrame.Rest(new Vector3(0.65f, 1.2f, 0), Quaternion.Identity),
            ["j_kami_a"] = PhysicsFrame.Rest(new Vector3(0, 1.5f, 0), Quaternion.Identity),
        };
        string[] bones = ["j_asi_a_l", "j_ude_b_l", "j_mune_l", "j_sebo_b", "ya_daitai_phys_l"];
        var points = new List<Vector3>();
        var pointBones = new List<int>();
        var pointWeights = new List<float>();
        void Add(Vector3 point, params (int Bone, float Weight)[] weights)
        {
            points.Add(point);
            for (var i = 0; i < PhysicsBody.Influences; i++)
            {
                pointBones.Add(i < weights.Length ? weights[i].Bone : -1);
                pointWeights.Add(i < weights.Length ? weights[i].Weight : 0);
            }
        }
        // Thigh: 7 cm around the hip to knee, with a jiggle bone sharing the front.
        for (var y = 0.52f; y <= 0.881f; y += 0.02f)
            for (var a = 0; a < 24; a++)
            {
                var angle = a * MathF.Tau / 24;
                var point = new Vector3(0.1f + 0.07f * MathF.Cos(angle), y, 0.07f * MathF.Sin(angle));
                Add(point, point.Z > 0.03f ? [(0, 0.7f), (4, 0.3f)] : [(0, 1f)]);
            }
        // Forearm: 3 cm around the elbow to the hand.
        for (var x = 0.4f; x <= 0.651f; x += 0.01f)
            for (var a = 0; a < 16; a++)
            {
                var angle = a * MathF.Tau / 16;
                Add(new Vector3(x, 1.2f + 0.03f * MathF.Cos(angle), 0.03f * MathF.Sin(angle)), (1, 1f));
            }
        // Breast: a dome 6 cm wide, from 10 to 16 cm in front of its bone, carried 60% by the breast and 40% by the spine.
        for (var ring = 0; ring <= 12; ring++)
        {
            var polar = ring * (MathF.PI / 2) / 12;
            var count = ring == 0 ? 1 : 24;
            for (var a = 0; a < count; a++)
            {
                var angle = a * MathF.Tau / count;
                var point = new Vector3(0.08f + 0.06f * MathF.Sin(polar) * MathF.Cos(angle), 1.2f + 0.06f * MathF.Sin(polar) * MathF.Sin(angle),
                    0.1f + 0.06f * MathF.Cos(polar));
                Add(point, (2, 0.6f), (3, 0.4f));
            }
        }
        var body = PhysicsBody.Of(bones, points.ToArray(), pointBones.ToArray(), pointWeights.ToArray(), Enumerable.Repeat(1e-4f, points.Count).ToArray(),
            pose);
        return (body, skeleton);
    }

    /// <summary> A file whose skirt bumps into the thigh, whose chest bumps into the arm, and whose hair has a shape of its own. </summary>
    internal static PhybFile SyntheticFile(float breastTip)
    {
        var file = new PhybFile();
        PhybCapsule Capsule(string name, string from, string to, Vector3 end, float radius) => new()
        {
            Name = PhybName.From(name), StartBone = PhybName.From(from), EndBone = PhybName.From(to), EndOffset = end, Radius = radius,
        };
        file.Collision.Capsules.Add(Capsule("thigh_l", "j_asi_a_l", "j_asi_b_l", Vector3.Zero, 0.02f));
        file.Collision.Capsules.Add(Capsule("forearm_l", "j_te_l", "j_ude_b_l", Vector3.Zero, 0.05f));
        file.Collision.Capsules.Add(Capsule("elbow_l", "n_hhiji_l", "n_hhiji_l", new Vector3(0.02f, 0, 0), 0.05f));
        file.Collision.Capsules.Add(Capsule("neck", "j_sebo_b", "j_sebo_b", new Vector3(0, 0.1f, 0), 0.05f));
        PhybSimulator Simulator(byte group, string[] shapes, params PhybChain[] chains)
        {
            var simulator = new PhybSimulator { Group = group };
            foreach (var shape in shapes)
                simulator.Collisions.Add(new PhybCollisionRef { Name = PhybName.From(shape), Type = PhybCollisionType.Outside });
            simulator.Chains.AddRange(chains);
            return simulator;
        }
        PhybChain Chain(string bone, Vector3 tip, float radius, uint flags) => new()
        {
            LastBoneOffset = tip,
            Nodes = [new PhybNode { Bone = PhybName.From(bone) }, new PhybNode { Radius = radius, CollisionFlags = flags }],
        };
        file.Simulators.Add(Simulator(2, ["thigh_l"], Chain("j_sk_f_a_l", new Vector3(0.4f, 0, 0), 0.04f, 1)));
        file.Simulators.Add(Simulator(PhysicsAreas.ChestGroup, ["forearm_l", "elbow_l"], Chain("j_mune_l", new Vector3(breastTip, 0, 0), 0.043f, 3)));
        file.Simulators.Add(Simulator(4, ["neck"], Chain("j_kami_a", new Vector3(0.1f, 0, 0), 0.035f, 1)));
        return file;
    }

    private static void CheckFit()
    {
        var (body, skeleton) = SyntheticBody();
        var fit = PhysicsFit.Measure(SyntheticFile(0.12f), body, skeleton);
        PhysicsFitItem? Item(string name) => fit.Items.FirstOrDefault(item => item.Name == name && item.Target != PhysicsFitTarget.Place);
        Require(Item("forearm_l") is { Target: PhysicsFitTarget.Capsule } forearm && MathF.Abs(forearm.Fitted - 0.03f) <= 0.0015f &&
                forearm.Label == "Left forearm (forearm_l)",
            "physics fit: a limb's shape becomes the limb's own thickness");
        Require(Item("thigh_l") is { } thigh && MathF.Abs(thigh.Fitted - 0.03f) <= 0.0015f && thigh.Detail.Contains("skirt", StringComparison.Ordinal),
            "physics fit: a shape the skirt bumps into leaves room for the skirt points' own size, as the game's thighs and skirts do");
        Require(Item("elbow_l") is { } elbow && MathF.Abs(elbow.Fitted - 0.03f) <= 0.002f,
            "physics fit: a helper bone without skin of its own, like the elbow's, is measured on the limb it sits on");
        Require(Item("neck") is null, "physics fit: shapes only hair bumps into keep the size the game gave them");
        Require(Item("j_mune_l") is { Target: PhysicsFitTarget.Chain, Simulator: 1, Chain: 0 } tip && tip.Fitted is > 0.05f and <= 0.061f &&
                tip.Label == "Chest, left: tip size",
            "physics fit: a breast tip fills the breast around its chain, as wide as the breast");

        var close = PhysicsFit.Measure(SyntheticFile(0.155f), body, skeleton);
        Require(close.Items.FirstOrDefault(item => item.Target == PhysicsFitTarget.Chain) is { } near && near.Fitted < 0.05f,
            "physics fit: a tip near the front of the skin reaches past it only a little");
        var outside = PhysicsFit.Measure(SyntheticFile(0.25f), body, skeleton);
        Require(outside.Items.FirstOrDefault(item => item.Target == PhysicsFitTarget.Chain) is { } kept && kept.Fitted == kept.Current &&
                kept.FittedAtPlace is > 0 && outside.Notes.All(note => !note.Contains("outside your skin", StringComparison.Ordinal)),
            "physics fit: a tip that sits outside the skin keeps the size its preset gave it, and only gets one with its tip place");

        var thick = SyntheticFile(0.12f);
        thick.Collision.Capsules[0].Radius = 0.05f;
        Require(PhysicsFit.Measure(thick, body, skeleton).Items.First(item => item.Name == "thigh_l").Fitted == 0.05f,
            "physics fit: skirt shapes never shrink, so skirts stay as far off thin legs as the file keeps them");

        var off = SyntheticFile(0.12f);
        off.Simulators[1].Excluded = true;
        Require(PhysicsFit.Measure(off, body, skeleton).Items.All(item => item.Name is not ("forearm_l" or "j_mune_l")),
            "physics fit: areas left out of the file don't count");

        var strange = SyntheticFile(0.12f);
        strange.Collision.Capsules.Add(new PhybCapsule
        {
            Name = PhybName.From("hair_l"), StartBone = PhybName.From("j_kami_a"), EndBone = PhybName.From("j_kami_a"), EndOffset = new Vector3(0.1f, 0, 0),
            Radius = 0.03f,
        });
        strange.Collision.Capsules.Add(new PhybCapsule { Name = PhybName.From("arm_r"), StartBone = PhybName.From("j_te_r"), EndBone = PhybName.From("j_ude_b_r"), Radius = 0.03f });
        strange.Simulators[1].Collisions.Add(new PhybCollisionRef { Name = PhybName.From("hair_l"), Type = PhybCollisionType.Outside });
        strange.Simulators[1].Collisions.Add(new PhybCollisionRef { Name = PhybName.From("arm_r"), Type = PhybCollisionType.Outside });
        strange.Simulators[1].Chains[0].Nodes[1].CollisionFlags = 15;
        var unmeasured = PhysicsFit.Measure(strange, body, skeleton);
        Require(unmeasured.Items.All(item => item.Name is not ("hair_l" or "arm_r")) &&
                unmeasured.Notes.Any(note => note.StartsWith("hair_l: none of your skin", StringComparison.Ordinal)) &&
                unmeasured.Notes.Any(note => note.StartsWith("arm_r: your skeleton lacks", StringComparison.Ordinal)),
            "physics fit: shapes whose bones carry no skin, or that the skeleton lacks, are reported, not guessed");
    }

    private static void CheckFitApply()
    {
        var (body, skeleton) = SyntheticBody();
        // A middle point on the breast's inner bone, and the tip 6 cm past it.
        var file = SyntheticFile(0.06f);
        file.Simulators[1].Chains[0].Nodes.Insert(1, new PhybNode { Bone = PhybName.From("iv_c_mune_l"), Radius = 0.0215f, CollisionFlags = 3 });
        var fit = PhysicsFit.Measure(file, body, skeleton);
        PhysicsFit.Apply(file, fit.Items);
        var tip = fit.Items.First(item => item.Target == PhysicsFitTarget.Chain).FittedWith(true);
        var chain = file.Simulators[1].Chains[0];
        Require(MathF.Abs(file.Collision.Capsules[1].Radius - fit.Items.First(item => item.Name == "forearm_l").Fitted) < 1e-6f &&
                MathF.Abs(chain.Nodes[^1].Radius - tip) < 1e-6f && MathF.Abs(chain.Nodes[1].Radius - tip / 2) < 1e-4f &&
                file.Collision.Capsules[3].Radius == 0.05f,
            "physics fit: applying sets the shapes and tips, middle points keep their share, and the rest is left alone");
        var bytes = file.ToBytes();
        Require(PhybFile.Parse(bytes).ToBytes().AsSpan().SequenceEqual(bytes), "physics fit: a fitted file still writes and reads back the same");
    }

    private static void CheckTipPlace()
    {
        var (body, skeleton) = SyntheticBody();
        // The dome's front is 16 cm in front of the breast bone; this tip sits 12 cm out and 3 cm up.
        var file = SyntheticFile(0.12f);
        var simulator = file.Simulators[1];
        var chain = simulator.Chains[0];
        chain.LastBoneOffset = new Vector3(0.12f, 0.03f, 0);
        chain.Nodes[0].ConeAxisOffset = Vector3.Normalize(chain.LastBoneOffset) * 0.1f;
        simulator.Attracts.Add(new PhybAttract { Bone = PhybName.From("j_sebo_b"), Offset = chain.LastBoneOffset * 1.34f, Chain = 0, Node = 1, Stiffness = 0.05f });
        simulator.Attracts.Add(new PhybAttract { Bone = PhybName.From("n_root"), Offset = new Vector3(0, 1, 0), Chain = 0, Node = 1, Stiffness = -0.001f });
        var fit = PhysicsFit.Measure(file, body, skeleton);
        var place = fit.Items.FirstOrDefault(item => item.Target == PhysicsFitTarget.Place);
        Require(place is { Changes: true, Label: "Chest, left: tip place", Move: { } move } && MathF.Abs(move.Offset.X - 0.16f) <= 0.003f &&
                MathF.Abs(move.Offset.Y) <= 0.002f && MathF.Abs(move.Offset.Z) <= 0.002f,
            "physics fit: a breast's tip moves onto the front of the breast, along the breast's own centre line");
        var size = fit.Items.First(item => item.Target == PhysicsFitTarget.Chain);
        Require(size.FittedAtPlace is > 0.035f and <= 0.041f && !PhysicsFit.MovesPlace([size], size) && PhysicsFit.MovesPlace([size, place!], size) &&
                size.FittedWith(true) == size.FittedAtPlace && size.FittedWith(false) == size.Fitted,
            "physics fit: a tip's size has a second value for its new place, used only while the place is chosen");

        var untouched = SyntheticFile(0.12f);
        PhysicsFit.Apply(untouched, [size]);
        Require(untouched.Simulators[1].Chains[0].LastBoneOffset == new Vector3(0.12f, 0, 0) &&
                MathF.Abs(untouched.Simulators[1].Chains[0].Nodes[1].Radius - size.Fitted) < 1e-6f,
            "physics fit: without its place, a tip stays where it is and takes the size for there");

        PhysicsFit.Apply(file, fit.Items);
        var newDirection = Vector3.Normalize(chain.LastBoneOffset);
        Require(chain.LastBoneOffset == place!.Move!.Offset && MathF.Abs(chain.Nodes[^1].Radius - size.FittedAtPlace!.Value) < 1e-6f,
            "physics fit: applying a tip place moves the tip and sizes it for its new place");
        Require(Vector3.Distance(Vector3.Normalize(chain.Nodes[0].ConeAxisOffset), newDirection) < 1e-3f &&
                MathF.Abs(chain.Nodes[0].ConeAxisOffset.Length() - 0.1f) < 1e-4f,
            "physics fit: a swing cone aimed at the old tip turns to the new one");
        Require(Vector3.Distance(simulator.Attracts[0].Offset, chain.LastBoneOffset * 1.34f) < 1e-3f,
            "physics fit: a spring aimed at the old tip turns to the new one, keeping its reach past it");
        Require(simulator.Attracts[1].Offset == new Vector3(0, 1, 0),
            "physics fit: a spring aimed somewhere else stays where it is");

        var near = PhysicsFit.Measure(SyntheticFile(0.158f), body, skeleton);
        Require(near.Items.FirstOrDefault(item => item.Target == PhysicsFitTarget.Place) is { Changes: false },
            "physics fit: a tip already on the front of the breast stays");
        var bones = SyntheticFile(0.12f);
        bones.Simulators[1].Chains[0].Nodes[1].Bone = PhybName.From("iv_c_mune_l");
        Require(PhysicsFit.Measure(bones, body, skeleton).Items.All(item => item.Target != PhysicsFitTarget.Place),
            "physics fit: a tip on a bone of its own isn't moved");
        Require(fit.BreastTips && !PhysicsFit.Measure(bones, body, skeleton).BreastTips,
            "physics fit: the result says when it has breast tips to place");

        // The dome is a half ball of 6 cm around a point 10 cm in front of the bone.
        var middleFile = SyntheticFile(0.16f);
        var middle = PhysicsFit.Measure(middleFile, body, skeleton, PhysicsTipAt.Middle);
        var middlePlace = middle.Items.FirstOrDefault(item => item.Target == PhysicsFitTarget.Place);
        Require(middlePlace is { Changes: true, Move: { } inside } && MathF.Abs(inside.Offset.X - 0.10f) <= 0.002f &&
                MathF.Abs(inside.Offset.Y) <= 0.002f && MathF.Abs(inside.Offset.Z) <= 0.002f,
            "physics fit: in the middle, a breast's tip moves to the centre of the ball its skin follows");
        var middleSize = middle.Items.First(item => item.Target == PhysicsFitTarget.Chain);
        Require(middleSize.FittedAtPlace is > 0.058f and < 0.062f,
            "physics fit: in the middle, a tip's size fills the breast");
        // The middle is searched in 2 mm steps, so it lands within a millimetre of 10 cm.
        Require(Regex.IsMatch(middlePlace!.Detail, @"Tip length 16\.0 to 10\.[01] cm, so the same settings swing the breast about 1\.6x as much\."),
            $"physics fit: moving a tip says how much more the same settings swing the breast ({middlePlace.Detail})");
        PhysicsFit.Apply(middleFile, middle.Items);
        var settled = PhysicsFit.Measure(middleFile, body, skeleton, PhysicsTipAt.Middle);
        Require(settled.Items.FirstOrDefault(item => item.Target == PhysicsFitTarget.Place) is { Changes: false } &&
                settled.Items.First(item => item.Target == PhysicsFitTarget.Chain) is { Changes: false },
            "physics fit: a tip already in the middle keeps its place and the middle's size");
        Require(PhysicsFit.LeverText(0.17f, 0.168f).StartsWith("Tip length barely changes", StringComparison.Ordinal),
            "physics fit: a tip that barely moves along says the jiggle stays the same");
    }

    private static void CheckMirror()
    {
        // A left breast bone turned out and down, the right one its mirror image, under a middle spine bone.
        var left = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.3f, 0.2f, 0.1f));
        var right = new Quaternion(left.X, -left.Y, -left.Z, left.W);
        var skeleton = new PhysicsSkeleton(
        [
            new PhysicsBone("j_sebo_b", "", new Vector3(0, 1.2f, 0), Quaternion.Identity),
            new PhysicsBone("j_mune_l", "j_sebo_b", new Vector3(0.03f, 0, 0), left),
            new PhysicsBone("j_mune_r", "j_sebo_b", new Vector3(-0.03f, 0, 0), right),
        ]);
        var rest = PhysicsBody.RestPose(skeleton);
        var offset = new Vector3(0.17f, 0.02f, -0.01f);
        var mirrored = PhysicsAreas.MirrorOffset(offset, "j_mune_l", "j_mune_r", rest);
        var leftPlace = rest["j_mune_l"].Place(offset);
        var rightPlace = rest["j_mune_r"].Place(mirrored);
        Require(Vector3.Distance(rightPlace, leftPlace with { X = -leftPlace.X }) < 1e-5f &&
                Vector3.Distance(PhysicsAreas.MirrorOffset(mirrored, "j_mune_r", "j_mune_l", rest), offset) < 1e-5f,
            "physics mirror: a tip place mirrored to the other side lands on the mirror image of this side's, and back again");
        Require(PhysicsAreas.MirrorOffset(new Vector3(0.1f, 0.2f, 0.3f), "j_sebo_b", "j_sebo_b", rest) == new Vector3(-0.1f, 0.2f, 0.3f) &&
                PhysicsAreas.MirrorOffset(offset, "j_mune_l", "missing", rest) == offset,
            "physics mirror: a middle bone's offset flips left to right, and a missing bone leaves it as it is");
        Require(PhysicsAreas.Opposite("j_mune_l", "j_mune_r") && PhysicsAreas.Opposite("ya_shiri_phys_r", "ya_shiri_phys_l") &&
                !PhysicsAreas.Opposite("j_mune_l", "j_mune_l") && !PhysicsAreas.Opposite("j_sebo_b", "j_mune_r"),
            "physics mirror: only a left and a right bone are opposite");
    }

    /// <summary> The game's own smallclothes, read with their skin weights and posed through a plain and a scaled skeleton. </summary>
    public static void CheckGameBody(Lumina.GameData game)
    {
        var bytes = game.GetFile("chara/equipment/e0000/model/c0201e0000_top.mdl")?.Data;
        if (bytes is null)
        {
            Console.WriteLine("[SKIP] physics fit: the game's smallclothes top isn't readable");
            return;
        }
        var mesh = ModelMeshReader.Read(bytes, skin: true);
        var skinned = mesh.Meshes.Where(part => part.Skin is not null).ToList();
        Require(mesh.Bones.Count > 10 && skinned.Count == mesh.Meshes.Count &&
                skinned.All(part => part.Skin!.Bones.All(bone => bone < mesh.Bones.Count) &&
                                    Enumerable.Range(0, part.Positions.Length).All(v =>
                                        MathF.Abs(Enumerable.Range(0, part.Skin.Influences).Sum(i => part.Skin.Weights[v * part.Skin.Influences + i]) - 1) < 0.02f)),
            "physics fit: the game's smallclothes read with every vertex's bones and weights adding up to one");
        Require(ModelMeshReader.Read(bytes).Meshes.Zip(mesh.Meshes).All(pair => pair.First.Positions.AsSpan().SequenceEqual(pair.Second.Positions)),
            "physics fit: reading skin weights doesn't change the rest of the mesh");

        var still = mesh.Bones.ToDictionary(bone => bone, _ => PhysicsFrame.Rest(Vector3.Zero, Quaternion.Identity), StringComparer.Ordinal);
        var body = PhysicsBody.Measure([new PhysicsBodyModel("top", bytes, null, null, 0)], still, still);
        var drawn = mesh.Meshes.SelectMany(part => part.Submeshes.SelectMany(submesh => submesh.Indices).Distinct().Select(v => part.Positions[v])).ToList();
        Require(body.Points.Length == drawn.Count && body.Areas.All(area => area >= 0) && body.Areas.Sum() > 0.05f &&
                body.Points.Zip(drawn).All(pair => Vector3.Distance(pair.First, pair.Second) < 1e-5f),
            "physics fit: a skeleton in its rest pose leaves the skin where the model has it, each point standing for its share of skin");
        var doubled = still.ToDictionary(pair => pair.Key, pair => pair.Value with { Scale = new Vector3(2) }, StringComparer.Ordinal);
        var grown = PhysicsBody.Measure([new PhysicsBodyModel("top", bytes, null, null, 0)], still, doubled);
        Require(grown.Points.Zip(body.Points).All(pair => Vector3.Distance(pair.First, pair.Second * 2) < 1e-4f),
            "physics fit: bones scaled like Customize+ scales them grow the skin they carry");
        // With no attributes enabled, only parts without any are drawn.
        var hidden = PhysicsBody.Measure([new PhysicsBodyModel("top", bytes, null, 0, 0)], still, still);
        var plain = mesh.Meshes.SelectMany(part => part.Submeshes.Where(submesh => submesh.AttributeMask == 0).SelectMany(submesh => submesh.Indices)
            .Distinct().Select(v => part.Positions[v])).Count();
        Require(hidden.Points.Length == plain, $"physics fit: parts the character's gear turns off aren't measured ({body.Points.Length - plain} of the top's points)");
    }
}
