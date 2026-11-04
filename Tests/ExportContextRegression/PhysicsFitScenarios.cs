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
        CheckTipNotes();
        CheckAccessories();
        CheckPartBalls();
        CheckGenitals();
        CheckClearance();
        CheckMirror();
        CheckSkirtShapes();
        CheckSkirtButtAndWaist();
    }

    /// <summary> The same skin and weights standing in another pose, like the body without Customize+. </summary>
    private static PhysicsBody InPose(PhysicsBody body, IReadOnlyDictionary<string, PhysicsFrame> pose)
        => PhysicsBody.Of(body.Bones, body.Points, body.PointBones, body.PointWeights, body.Areas, pose);

    private static void CheckSkirtShapes()
    {
        var (body, skeleton) = SyntheticBody();
        // At rest the thigh bones sit 3 cm further in; Customize+ moves them out to the middle of the thigh's skin.
        var rest = body.Pose.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var bone in new[] { "j_asi_a_l", "j_asi_b_l" })
            rest[bone] = rest[bone] with { Position = rest[bone].Position - new Vector3(0.03f, 0, 0) };
        var unshaped = InPose(body, rest);
        var file = SyntheticFile(0.12f);
        var thigh = PhysicsFit.Measure(file, body, skeleton, unshaped: unshaped).Items.First(item => item.Name == "thigh_l");
        Require(thigh.Shape is { NewOn: null } move && Vector3.Distance(move.Start, new Vector3(0.03f, 0, 0)) < 1e-4f &&
                Vector3.Distance(move.End, new Vector3(0.03f, 0, 0)) < 1e-4f && MathF.Abs(move.Distance - 0.03f) < 1e-4f &&
                MathF.Abs(thigh.Fitted - 0.03f) <= 0.0015f && thigh.Changes && !thigh.Optional,
            "physics fit: a skirt shape moves to where Customize+ puts its bones, as offsets from where they rest, and keeps the size it has there");
        var before = file.Clone();
        PhysicsFit.Apply(file, [thigh]);
        var capsule = file.Collision.Capsules[0];
        var middle = (rest["j_asi_a_l"].Place(capsule.StartOffset) + rest["j_asi_b_l"].Place(capsule.EndOffset)) / 2;
        Require(Vector3.Distance(middle, new Vector3(0.1f, 0.7f, 0)) < 1e-4f && MathF.Abs(capsule.Radius - thigh.Fitted) < 1e-6f,
            "physics fit: physics then places the skirt's thigh shape in the middle of the thigh's skin, where Customize+ draws it");
        // At rest the thigh bones sit 3 cm higher instead: Customize+ moves them only along the thigh.
        var higher = body.Pose.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var bone in new[] { "j_asi_a_l", "j_asi_b_l" })
            higher[bone] = higher[bone] with { Position = higher[bone].Position + new Vector3(0, 0.03f, 0) };
        Require(PhysicsFit.Measure(SyntheticFile(0.12f), body, skeleton, unshaped: InPose(body, higher)).Items.First(item => item.Name == "thigh_l").Shape is { } along &&
                Vector3.Distance(along.Start, new Vector3(0, -0.03f, 0)) < 1e-4f && MathF.Abs(along.Distance - 0.03f) < 1e-4f,
            "physics fit: a Customize+ move only along the shape, like a lowered knee, moves it too");
        Require(PhysicsFit.Measure(SyntheticFile(0.12f), body, skeleton).Items.First(item => item.Name == "thigh_l").Shape is null,
            "physics fit: measured without the body at rest, skirt shapes stay where they are");

        var root = Path.Combine(Path.GetTempPath(), "ie-physics-fit-" + Guid.NewGuid().ToString("N"));
        try
        {
            const ushort race = 201;
            new PhysicsFitStore(Path.Combine(root, "config")).Record(before, race, [thigh], null);
            // A new store reads what the first one saved.
            var store = new PhysicsFitStore(Path.Combine(root, "config"));
            var originals = store.Originals(file, race);
            Require(originals.TryGetValue((PhysicsFitTarget.Capsule, "thigh_l"), out var original) && original == (Vector3.Zero, Vector3.Zero) &&
                    store.Originals(file, 101).Count == 0 && store.Originals(before, race).Count == 0,
                "physics fit: applying remembers the skirt shape's offsets before the fit, for that race's file while it holds what the fit wrote");
            var refit = PhysicsFit.Measure(file, body, skeleton, unshaped: unshaped, originals: originals).Items.First(item => item.Name == "thigh_l");
            Require(refit.Shape is null && !refit.Changes && refit.Fitted == refit.Current,
                "physics fit: fitting a fitted file again with the same body changes nothing, without adding the Customize+ move twice");
            Require(PhysicsFit.Measure(file, body, skeleton, originals: originals).Items.First(item => item.Name == "thigh_l").Shape is null,
                "physics fit: without the body at rest, a fitted skirt shape stays where it is");

            // Customize+ now moves the thigh bones 5 cm out instead of 3: the shape goes where a first fit with that shape puts it.
            var further = body.Pose.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            foreach (var bone in new[] { "j_asi_a_l", "j_asi_b_l" })
                further[bone] = further[bone] with { Position = further[bone].Position - new Vector3(0.05f, 0, 0) };
            var first = PhysicsFit.Measure(SyntheticFile(0.12f), body, skeleton, unshaped: InPose(body, further)).Items.First(item => item.Name == "thigh_l");
            var changed = PhysicsFit.Measure(file, body, skeleton, unshaped: InPose(body, further), originals: originals).Items.First(item => item.Name == "thigh_l");
            Require(changed.Shape is { } moved && first.Shape is { } fresh && Vector3.Distance(moved.Start, fresh.Start) < 1e-4f &&
                    Vector3.Distance(moved.End, fresh.End) < 1e-4f && MathF.Abs(moved.Distance - 0.02f) < 1e-4f && changed.Changes,
                "physics fit: a changed Customize+ shape moves a fitted skirt shape from where it was before the first fit, not from the moved place");
            var refitted = file.Clone();
            PhysicsFit.Apply(refitted, [changed]);
            store.Record(file, race, [changed], originals);
            Require(store.Originals(refitted, race).TryGetValue((PhysicsFitTarget.Capsule, "thigh_l"), out var kept) && kept == (Vector3.Zero, Vector3.Zero) &&
                    store.Originals(file, race).Count == 1,
                "physics fit: a fit from a remembered original remembers that original again, and the earlier record stays for undo");

            // Moved by hand since the fit: it fits as new, from where it is now.
            var edited = file.Clone();
            edited.Collision.Capsules[0].StartOffset += new Vector3(0.001f, 0, 0);
            var editedOriginals = store.Originals(edited, race);
            Require(editedOriginals.Count == 0 &&
                    PhysicsFit.Measure(edited, body, skeleton, unshaped: unshaped, originals: editedOriginals).Items.First(item => item.Name == "thigh_l").Shape is { } anew &&
                    Vector3.Distance(anew.Start, edited.Collision.Capsules[0].StartOffset + new Vector3(0.03f, 0, 0)) < 1e-4f,
                "physics fit: a skirt shape changed since the fit wrote it fits as a first fit, from where it is");

            File.WriteAllText(Path.Combine(root, "config", "physics-fit.json"), "{ not json");
            Require(store.Originals(file, race).Count == 0, "physics fit: an unreadable record file counts as no records");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }

        var shared = SyntheticFile(0.12f);
        shared.Simulators[1].Collisions.Add(new PhybCollisionRef { Name = PhybName.From("thigh_l"), Type = PhybCollisionType.Outside });
        shared.Simulators[1].Chains[0].Nodes[1].CollisionFlags |= 4;
        var sharedFit = PhysicsFit.Measure(shared, body, skeleton, unshaped: unshaped);
        Require(sharedFit.Items.First(item => item.Name == "thigh_l").Shape is null &&
                sharedFit.Notes.Any(note => note.StartsWith("thigh_l: Customize+ moves its bones 3.0 cm", StringComparison.Ordinal)),
            "physics fit: a shape body areas bump into too stays on its bones, and the fit says why");
    }

    /// <summary>
    /// Hips with skin 9 cm around at rest and <paramref name="hips"/> with Customize+, a butt cheek on each side
    /// (a half ball 6 cm wide, behind its bone, a third carried by it), and a skirt bone starting 10 cm out.
    /// </summary>
    private static (PhysicsBody Body, PhysicsSkeleton Skeleton) HipsBody(float hips, Vector3 skirtStart, float lift = 0)
    {
        var skeleton = new PhysicsSkeleton(
        [
            new PhysicsBone("n_root", "", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("j_kosi", "n_root", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("iv_shiri_l", "j_kosi", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("iv_shiri_r", "j_kosi", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("j_sk_f_a_l", "j_kosi", Vector3.Zero, Quaternion.Identity),
        ]);
        var pose = new Dictionary<string, PhysicsFrame>(StringComparer.Ordinal)
        {
            ["n_root"] = PhysicsFrame.Rest(Vector3.Zero, Quaternion.Identity),
            ["j_kosi"] = PhysicsFrame.Rest(new Vector3(0, 1, 0), Quaternion.Identity),
            ["iv_shiri_l"] = PhysicsFrame.Rest(new Vector3(0.07f, 0.95f, 0), Quaternion.Identity),
            ["iv_shiri_r"] = PhysicsFrame.Rest(new Vector3(-0.07f, 0.95f, 0), Quaternion.Identity),
            ["j_sk_f_a_l"] = PhysicsFrame.Rest(skirtStart, Quaternion.Identity),
        };
        // Customize+ raising the whole pelvis.
        foreach (var bone in pose.Keys.ToList())
            pose[bone] = pose[bone] with { Position = pose[bone].Position + new Vector3(0, lift, 0) };
        string[] bones = ["j_kosi", "iv_shiri_l", "iv_shiri_r"];
        var points = new List<Vector3>();
        var pointBones = new List<int>();
        var pointWeights = new List<float>();
        void Add(Vector3 point, params (int Bone, float Weight)[] weights)
        {
            points.Add(point + new Vector3(0, lift, 0));
            for (var i = 0; i < PhysicsBody.Influences; i++)
            {
                pointBones.Add(i < weights.Length ? weights[i].Bone : -1);
                pointWeights.Add(i < weights.Length ? weights[i].Weight : 0);
            }
        }
        for (var y = 0.98f; y <= 1.021f; y += 0.01f)
            for (var a = 0; a < 36; a++)
                Add(new Vector3(hips * MathF.Cos(a * MathF.Tau / 36), y, hips * MathF.Sin(a * MathF.Tau / 36)), (0, 1f));
        foreach (var (x, bone) in new[] { (0.07f, 1), (-0.07f, 2) })
            for (var ring = 0; ring <= 12; ring++)
            {
                var polar = ring * (MathF.PI / 2) / 12;
                var count = ring == 0 ? 1 : 24;
                for (var a = 0; a < count; a++)
                {
                    var angle = a * MathF.Tau / count;
                    Add(new Vector3(x + 0.06f * MathF.Sin(polar) * MathF.Cos(angle), 0.95f + 0.06f * MathF.Sin(polar) * MathF.Sin(angle),
                        -0.08f - 0.06f * MathF.Cos(polar)), (0, 0.7f), (bone, 0.3f));
                }
            }
        return (PhysicsBody.Of(bones, points.ToArray(), pointBones.ToArray(), pointWeights.ToArray(), Enumerable.Repeat(1e-4f, points.Count).ToArray(), pose),
            skeleton);
    }

    private static PhybFile SkirtFile()
    {
        var file = new PhybFile();
        var simulator = new PhybSimulator { Group = 2 };
        simulator.Chains.Add(new PhybChain
        {
            LastBoneOffset = new Vector3(0.3f, 0, 0),
            Nodes = [new PhybNode { Bone = PhybName.From("j_sk_f_a_l") }, new PhybNode { Radius = 0.04f }],
        });
        file.Simulators.Add(simulator);
        return file;
    }

    /// <summary> The waistband moves as a Customize+ template: read back as Customize+ reads a clipboard import. </summary>
    private static void CheckWaistTemplate()
    {
        var profile = new Dictionary<string, PhysicsPlusBone>(StringComparer.Ordinal)
        {
            ["j_sk_f_a_l"] = new(new Vector3(0.01f, 0.02f, 0), new Vector3(5, 0, 0), new Vector3(1.1f, 1, 1), new Vector3(0.9f, 1, 1), true, true, false, true),
            ["j_kosi"] = new(new Vector3(0, 0.05f, 0), Vector3.Zero, Vector3.One, Vector3.One, false, false, false, false),
        };
        PhysicsWaistMove[] moves = [new("j_sk_f_a_l", new Vector3(0.03f, 0, -0.01f), 0.032f), new("j_sk_b_a_r", new Vector3(0, 0.02f, 0.04f), 0.045f)];
        var base64 = PhysicsPlusTemplate.Encode(moves, profile);
        using var zip = new System.IO.Compression.GZipStream(new MemoryStream(Convert.FromBase64String(base64)), System.IO.Compression.CompressionMode.Decompress);
        using var unpacked = new MemoryStream();
        zip.CopyTo(unpacked);
        var bytes = unpacked.ToArray();
        using var json = System.Text.Json.JsonDocument.Parse(bytes.AsMemory(1));
        var bones = json.RootElement.GetProperty("Bones");
        Vector3 Read(string bone, string name)
        {
            var v = bones.GetProperty(bone).GetProperty(name);
            return new Vector3(v.GetProperty("X").GetSingle(), v.GetProperty("Y").GetSingle(), v.GetProperty("Z").GetSingle());
        }
        bool Flag(string bone, string name) => bones.GetProperty(bone).GetProperty(name).GetBoolean();
        Require(bytes[0] == PhysicsPlusTemplate.Version && bones.EnumerateObject().Select(bone => bone.Name).SequenceEqual(["j_sk_f_a_l", "j_sk_b_a_r"]),
            "physics fit: the waistband template is a versioned Customize+ template holding the skirt bones only");
        Require(Vector3.Distance(Read("j_sk_f_a_l", "Translation"), new Vector3(0.04f, 0.02f, -0.01f)) < 1e-6f &&
                Read("j_sk_f_a_l", "Rotation") == new Vector3(5, 0, 0) && Read("j_sk_f_a_l", "Scaling") == new Vector3(1.1f, 1, 1) &&
                Read("j_sk_f_a_l", "ChildScaling") == new Vector3(0.9f, 1, 1) && Flag("j_sk_f_a_l", "ChildScalingIndependent") &&
                Flag("j_sk_f_a_l", "PropagateTranslation") && !Flag("j_sk_f_a_l", "PropagateRotation") && Flag("j_sk_f_a_l", "PropagateScale"),
            "physics fit: a skirt bone the profile already has keeps its values, with the move added to its position");
        Require(Read("j_sk_b_a_r", "Translation") == new Vector3(0, 0.02f, 0.04f) && Read("j_sk_b_a_r", "Rotation") == Vector3.Zero &&
                Read("j_sk_b_a_r", "Scaling") == Vector3.One && Read("j_sk_b_a_r", "ChildScaling") == Vector3.One &&
                !Flag("j_sk_b_a_r", "PropagateTranslation") && !Flag("j_sk_b_a_r", "ChildScalingIndependent") &&
                PhysicsPlusTemplate.Encode(moves, null) is { Length: > 0 },
            "physics fit: a skirt bone the profile lacks gets just the move, and a template is made without a profile too");
    }

    private static void CheckSkirtButtAndWaist()
    {
        var start = new Vector3(0.1f, 1, 0);
        var (body, skeleton) = HipsBody(0.12f, start);
        var (unshaped, _) = HipsBody(0.09f, start);
        var file = SkirtFile();
        var fit = PhysicsFit.Measure(file, body, skeleton, unshaped: unshaped);
        var butts = fit.Items.Where(item => item.Name.StartsWith(PhysicsFit.SkirtButtPrefix, StringComparison.Ordinal)).ToList();
        Require(butts.Count == 2 && butts.All(item => item is { Target: PhysicsFitTarget.Sphere, Optional: true, Current: 0, Changes: true, Shape.NewOn: "j_kosi" } &&
                                                      MathF.Abs(item.Fitted - 0.02f) <= 0.003f) &&
                butts.First(item => item.Name.EndsWith("_l", StringComparison.Ordinal)).Shape is { } left &&
                Vector3.Distance(left.Start, new Vector3(0.07f, -0.05f, -0.08f)) < 0.005f,
            "physics fit: a skirt without butt collision is offered an unticked ball in each butt cheek, on the hips, as big as the cheek less the skirt's size");
        Require(fit.Waist is [{ Bone: "j_sk_f_a_l" } waist] && Vector3.Distance(waist.Translation, new Vector3(0.03f, 0, 0)) < 0.002f,
            "physics fit: the bone a skirt starts from gets a Customize+ move out as far as the hips there grew");
        CheckWaistTemplate();

        PhysicsFit.Apply(file, butts);
        Require(file.Collision.Spheres.Count == 2 && file.Collision.Spheres.All(sphere => sphere.Bone.Text == "j_kosi") &&
                PhysicsCollisions.State(file, [(0, 0)], PhysicsFit.SkirtButtPrefix + "l") == PhysicsBumps.All &&
                PhysicsCollisions.State(file, [(0, 0)], PhysicsFit.SkirtButtPrefix + "r") == PhysicsBumps.All,
            "physics fit: applying adds the balls and the skirt bumps into them");
        var bytes = file.ToBytes();
        Require(PhybFile.Parse(bytes).ToBytes().AsSpan().SequenceEqual(bytes), "physics fit: a file with the skirt's butt balls writes and reads back the same");
        var again = PhysicsFit.Measure(file, body, skeleton, unshaped: unshaped);
        Require(again.Items.Count(item => item.Name.StartsWith(PhysicsFit.SkirtButtPrefix, StringComparison.Ordinal)) == 2 &&
                again.Items.All(item => !item.Changes && !item.Optional),
            "physics fit: fitting again measures the balls it added, which already fit");

        var own = SkirtFile();
        own.Collision.Spheres.Add(new PhybSphere { Name = PhybName.From("butt_l"), Bone = PhybName.From("iv_shiri_l"), Thickness = 0.05f });
        own.Simulators[0].Collisions.Add(new PhybCollisionRef { Name = PhybName.From("butt_l"), Type = PhybCollisionType.Outside });
        own.Simulators[0].Chains[0].Nodes[1].CollisionFlags = 1;
        Require(PhysicsFit.Measure(own, body, skeleton, unshaped: unshaped).Items.All(item => !item.Name.StartsWith(PhysicsFit.SkirtButtPrefix, StringComparison.Ordinal)),
            "physics fit: a skirt that already bumps into the butt gets no balls of the fit's");

        var (moved, _) = HipsBody(0.12f, start + new Vector3(0.03f, 0, 0));
        Require(PhysicsFit.Measure(SkirtFile(), moved, skeleton, unshaped: unshaped).Waist.Count == 0 &&
                PhysicsFit.Measure(SkirtFile(), body, skeleton).Waist.Count == 0,
            "physics fit: no Customize+ move for a skirt bone Customize+ already moves out, or without the body at rest");
        var (raised, _) = HipsBody(0.12f, start, lift: 0.05f);
        Require(PhysicsFit.Measure(SkirtFile(), raised, skeleton, unshaped: unshaped).Waist is [{ Bone: "j_sk_f_a_l" } up] &&
                Vector3.Distance(up.Translation, new Vector3(0.03f, 0, 0)) < 0.002f,
            "physics fit: with the pelvis raised by Customize+, the hips are measured at the skirt bone's new height");

        // Room in the skirt for one more shape: only one ball is offered, and applying both adds only the one.
        var crowded = SkirtFile();
        for (var i = 0; i < PhysicsCollisions.MaxShapes - 1; i++)
            crowded.Simulators[0].Collisions.Add(new PhybCollisionRef { Name = PhybName.From($"other_{i}"), Type = PhybCollisionType.Outside });
        var room = PhysicsFit.Measure(crowded, body, skeleton, unshaped: unshaped);
        Require(room.Items.Count(item => item.Name.StartsWith(PhysicsFit.SkirtButtPrefix, StringComparison.Ordinal)) == 1 &&
                room.Notes.Any(note => note.StartsWith("Right butt for the skirt: the skirt already bumps into", StringComparison.Ordinal)),
            "physics fit: with room for one more shape, the skirt is offered one butt ball and told why not the other");
        PhysicsFit.Apply(crowded, butts);
        Require(crowded.Collision.Spheres.Count == 1 && PhysicsCollisions.State(crowded, [(0, 0)], crowded.Collision.Spheres[0].Name.Text) == PhysicsBumps.All,
            "physics fit: a ball the skirt has no bit left for isn't added");
    }

    private static void CheckDrawnModels()
    {
        Require(PhysicsPaths.IsBareBody("chara/equipment/e0000/model/c0201e0000_top.mdl") && PhysicsPaths.IsBareBody("chara/equipment/e0279/model/c0201e0279_dwn.mdl") &&
                !PhysicsPaths.IsBareBody("chara/equipment/e6016/model/c0201e6016_top.mdl"),
            "physics fit: smallclothes and the Emperor's New gear count as bare body, other gear doesn't");
        const string modFile = @"D:\Mods\Some Body\chara\equipment\e0000\model\c0201e0000_top.mdl";
        var resolved = new Dictionary<string, HashSet<string>>
        {
            [modFile] = ["chara/equipment/e0000/model/c0201e0000_top.mdl", "chara/equipment/e0279/model/c0201e0279_top.mdl"],
        };
        var drawn = InstantEdit.Services.Painter.PainterVisibility.NormalizePath(modFile);
        Require(PhysicsPaths.ResolveDrawn(resolved, drawn, 1) is (modFile, "chara/equipment/e0000/model/c0201e0000_top.mdl"),
            "physics fit: a drawn model is read from the file Penumbra loads, with its slot's game path");
        Require(PhysicsPaths.ResolveDrawn(resolved, "chara/equipment/e0000/model/c0201e0000_glv.mdl", 2) is
                    ("chara/equipment/e0000/model/c0201e0000_glv.mdl", "chara/equipment/e0000/model/c0201e0000_glv.mdl") &&
                PhysicsPaths.ResolveDrawn(resolved, "d:/mods/elsewhere/c0201e0000_sho.mdl", 4) is null,
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

    /// <summary> Notes name the bone that is really missing, and a tip resting on its own bone says there's nothing to measure. </summary>
    private static void CheckTipNotes()
    {
        var (body, skeleton) = SyntheticBody();
        // The player's skeleton has no iv_c_mune_l, the last point of a chest chain [j_mune_l, tip, iv_c_mune_l].
        var lacking = new PhysicsSkeleton(skeleton.Body.Where(bone => bone.Name != "iv_c_mune_l").ToList());
        var bare = InPose(body, body.Pose.Where(pair => pair.Key != "iv_c_mune_l").ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        var extended = SyntheticFile(0.12f);
        extended.Simulators[1].Chains[0].Nodes.Add(new PhybNode { Bone = PhybName.From("iv_c_mune_l"), Radius = 0.05f, CollisionFlags = 3 });
        var fit = PhysicsFit.Measure(extended, bare, lacking);
        Require(fit.Notes.All(note => !note.Contains("lacks", StringComparison.Ordinal)) &&
                fit.Items.Any(item => item is { Target: PhysicsFitTarget.Place, Name: "j_mune_l" }),
            "physics fit: a chest whose tip has no bone still places its tip when the bone of its last point is missing");

        // Bad Decisions' skeleton: iv_c_mune_l rests exactly on j_mune_l.
        var onBone = InPose(body, body.Pose.ToDictionary(pair => pair.Key, pair => pair.Key == "iv_c_mune_l" ? body.Pose["j_mune_l"] : pair.Value,
            StringComparer.Ordinal));
        var own = SyntheticFile(0.12f);
        own.Simulators[1].Chains[0].Nodes[1].Bone = PhybName.From("iv_c_mune_l");
        var sits = PhysicsFit.Measure(own, onBone, skeleton);
        Require(sits.Notes.Contains("Chest, left: its tip sits on its bone, so there's nothing to measure.") &&
                sits.Notes.All(note => !note.Contains("none of your skin moves with j_mune_l", StringComparison.Ordinal)) &&
                !sits.Items.Any(item => item is { Name: "j_mune_l", Target: PhysicsFitTarget.Chain or PhysicsFitTarget.Place }),
            "physics fit: a tip that sits on its own bone says there's nothing to measure");

        var otherSide = SyntheticFile(0.12f);
        otherSide.Simulators[1].Chains[0].Nodes[0].Bone = PhybName.From("j_mune_r");
        var gap = SyntheticFile(0.12f);
        gap.Simulators[1].Chains[0].Nodes.Insert(1, new PhybNode { Bone = PhybName.From("j_missing"), CollisionFlags = 3 });
        Require(PhysicsFit.Measure(otherSide, body, skeleton).Notes.Contains("Chest, right: your skeleton lacks j_mune_r.") &&
                PhysicsFit.Measure(gap, body, skeleton).Notes.Contains("Chest, left: your skeleton lacks j_missing."),
            "physics fit: a missing bone note names the bone that is missing, the chain's own or the one before its tip");
    }

    /// <summary>
    /// <paramref name="body"/> with more skin, standing in <paramref name="pose"/> (the body's own when null): each point with its
    /// bones and weights, the skin it stands for and the accessory slot it comes from.
    /// </summary>
    private static PhysicsBody With(PhysicsBody body, IReadOnlyDictionary<string, PhysicsFrame>? pose,
        params (Vector3 Point, (string Bone, float Weight)[] Weights, float Area, string? Accessory)[] added)
    {
        var bones = body.Bones.ToList();
        var points = body.Points.ToList();
        var pointBones = body.PointBones.ToList();
        var pointWeights = body.PointWeights.ToList();
        var areas = body.Areas.ToList();
        var accessory = new List<string?>(new string?[body.Points.Length]);
        foreach (var (point, weights, area, slot) in added)
        {
            points.Add(point);
            areas.Add(area);
            accessory.Add(slot);
            for (var i = 0; i < PhysicsBody.Influences; i++)
            {
                if (i >= weights.Length)
                {
                    pointBones.Add(-1);
                    pointWeights.Add(0);
                    continue;
                }
                var index = bones.IndexOf(weights[i].Bone);
                if (index < 0)
                {
                    index = bones.Count;
                    bones.Add(weights[i].Bone);
                }
                pointBones.Add(index);
                pointWeights.Add(weights[i].Weight);
            }
        }
        return PhysicsBody.Of(bones, points.ToArray(), pointBones.ToArray(), pointWeights.ToArray(), areas.ToArray(), pose ?? body.Pose,
            accessory: accessory.ToArray());
    }

    /// <summary> Models on accessory slots (5 to 9) count only where an area measures its own part, never for a limb's or shape's thickness. </summary>
    private static void CheckAccessories()
    {
        Require(PhysicsPaths.IsAccessory(5) && PhysicsPaths.IsAccessory(9) && !PhysicsPaths.IsAccessory(4) && !PhysicsPaths.IsAccessory(10) &&
                PhysicsPaths.SlotName(5) == "ears" && PhysicsPaths.SlotName(6) == "neck" && PhysicsPaths.SlotName(7) == "wrists" &&
                PhysicsPaths.SlotName(8) == "right ring" && PhysicsPaths.SlotName(9) == "left ring",
            "physics fit: slots 5 to 9 are the accessories, named as the game names them");
        const string ring = @"D:\Mods\Some Ring\chara\accessory\a0001\model\c0201a0001_rir.mdl";
        var rings = new Dictionary<string, HashSet<string>>
        {
            [ring] = ["chara/accessory/a0001/model/c0201a0001_rir.mdl", "chara/accessory/a0001/model/c0201a0001_ril.mdl"],
        };
        var drawnRing = InstantEdit.Services.Painter.PainterVisibility.NormalizePath(ring);
        Require(PhysicsPaths.ResolveDrawn(rings, drawnRing, 8) is (ring, "chara/accessory/a0001/model/c0201a0001_rir.mdl") &&
                PhysicsPaths.ResolveDrawn(rings, drawnRing, 9) is (ring, "chara/accessory/a0001/model/c0201a0001_ril.mdl"),
            "physics fit: a ring model serving both hands is read with its own slot's game path");

        var (body, skeleton) = SyntheticBody();
        var file = SyntheticFile(0.12f);
        var plain = PhysicsFit.Measure(file, body, skeleton);
        static float Fitted(PhysicsFitResult fit, string name) => fit.Items.FirstOrDefault(item => item.Name == name)?.Fitted ?? float.NaN;

        // A ring 5 cm up the forearm from the hand bone, all of it on the hand bone, with more skin than the forearm.
        var ringPoints = Enumerable.Range(0, 16)
            .Select(a => (new Vector3(0.6f, 1.2f + 0.01f * MathF.Cos(a * MathF.Tau / 16), 0.01f * MathF.Sin(a * MathF.Tau / 16)), new[] { ("j_te_l", 1f) }, 0.01f,
                (string?)"left ring"))
            .ToArray();
        var ringed = PhysicsFit.Measure(file, With(body, null, ringPoints), skeleton);
        Require(Fitted(ringed, "forearm_l") == Fitted(plain, "forearm_l") && Fitted(ringed, "elbow_l") == Fitted(plain, "elbow_l"),
            "physics fit: a ring weighted to the hand changes neither the forearm nor the elbow, bit for bit");

        // A member on the right ring slot: a 2 cm cylinder 15 cm long on iv_ochinko_a, which sits under the hips with its X axis forward.
        var member = new PhysicsSkeleton([..skeleton.Body, new PhysicsBone("iv_ochinko_a", "j_kosi", Vector3.Zero, Quaternion.Identity)]);
        var pose = new Dictionary<string, PhysicsFrame>(body.Pose, StringComparer.Ordinal)
        {
            ["iv_ochinko_a"] = PhysicsFrame.Rest(new Vector3(0, 0.95f, 0.08f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2)),
        };
        var cylinder = new List<(Vector3, (string, float)[], float, string?)>();
        for (var z = 0.08f; z <= 0.2301f; z += 0.01f)
            for (var a = 0; a < 16; a++)
                cylinder.Add((new Vector3(0.02f * MathF.Cos(a * MathF.Tau / 16), 0.95f + 0.02f * MathF.Sin(a * MathF.Tau / 16), z), [("iv_ochinko_a", 1f)], 1e-4f,
                    "right ring"));
        var groin = SyntheticFile(0.12f);
        groin.Simulators.Add(new PhybSimulator
        {
            Group = PhysicsAreas.ChestGroup,
            Collisions = [new PhybCollisionRef { Name = PhybName.From("thigh_l"), Type = PhybCollisionType.Outside }],
            Chains =
            [
                new PhybChain
                {
                    LastBoneOffset = new Vector3(0.35f, 0, 0),
                    Nodes = [new PhybNode { Bone = PhybName.From("iv_ochinko_a") }, new PhybNode { Radius = 0.02f, CollisionFlags = 1 }],
                },
            ],
        });
        var measured = PhysicsFit.Measure(groin, With(body, pose, cylinder.ToArray()), member);
        Require(measured.Notes.Contains("Your right ring's model moves with iv_ochinko_a, so it was measured for the penis.") &&
                measured.Notes.All(note => !note.Contains("none of your skin moves with iv_ochinko_a", StringComparison.Ordinal)),
            "physics fit: a member on a ring slot is measured for the penis, and the notes name the slot");

        // A piercing on the neck slot, half on the hips and half on the thigh's jiggle bone, beside the thigh with more skin than it.
        var pierced = PhysicsFit.Measure(file, With(body, null, (new Vector3(0.11f, 0.7f, 0), [("j_kosi", 0.5f), ("ya_daitai_phys_l", 0.5f)], 1f, "neck")), skeleton);
        Require(plain.Items.Where(item => item.Target is PhysicsFitTarget.Capsule or PhysicsFitTarget.Sphere)
                    .All(item => pierced.Items.FirstOrDefault(other => other.Name == item.Name && other.Target == item.Target)?.Fitted == item.Fitted),
            "physics fit: an accessory weighted to the hips and a thigh's jiggle bone changes no shape's size");

        // A belly piercing, half on the hips and half on the belly's jiggle bone: measured for the belly, never for the hips' thickness.
        var belly = new PhysicsSkeleton([..skeleton.Body, new PhysicsBone("iv_fukubu_phys", "j_kosi", Vector3.Zero, Quaternion.Identity)]);
        var bellyPose = new Dictionary<string, PhysicsFrame>(body.Pose, StringComparer.Ordinal)
        {
            ["iv_fukubu_phys"] = PhysicsFrame.Rest(new Vector3(0, 1.05f, 0.08f), Quaternion.Identity),
        };
        var hipsRing = Enumerable.Range(0, 24)
            .Select(a => (new Vector3(0.09f * MathF.Cos(a * MathF.Tau / 24), 1f, 0.09f * MathF.Sin(a * MathF.Tau / 24)), new[] { ("j_kosi", 1f) }, 1e-4f, (string?)null))
            .ToArray();
        var hipsBody = With(body, bellyPose, hipsRing);
        var bellyFile = SyntheticFile(0.12f);
        bellyFile.Collision.Capsules.Add(new PhybCapsule
        {
            Name = PhybName.From("hips"), StartBone = PhybName.From("j_kosi"), EndBone = PhybName.From("j_kosi"), Radius = 0.05f,
        });
        bellyFile.Simulators.Add(new PhybSimulator
        {
            Group = PhysicsAreas.ChestGroup,
            Collisions = [new PhybCollisionRef { Name = PhybName.From("hips"), Type = PhybCollisionType.Outside }],
            Chains =
            [
                new PhybChain
                {
                    LastBoneOffset = new Vector3(0, 0, 0.04f),
                    Nodes = [new PhybNode { Bone = PhybName.From("iv_fukubu_phys") }, new PhybNode { Radius = 0.02f, CollisionFlags = 1 }],
                },
            ],
        });
        var bellyPlain = PhysicsFit.Measure(bellyFile, hipsBody, belly);
        var bellyPierced = PhysicsFit.Measure(bellyFile, With(hipsBody, null, (new Vector3(0, 1f, 0.01f), [("j_kosi", 0.5f), ("iv_fukubu_phys", 0.5f)], 1f, "neck")), belly);
        Require(bellyPierced.Notes.Contains("Your neck's model moves with iv_fukubu_phys, so it was measured for the belly.") &&
                Fitted(bellyPierced, "hips") == Fitted(bellyPlain, "hips") && !float.IsNaN(Fitted(bellyPlain, "hips")),
            "physics fit: a belly piercing weighted to the hips too counts for the belly's part, never for the hips' thickness");

        // An accessory model the reader can't read, like an old earring, is left out of the measure rather than ending it.
        string? stopped = null;
        try
        {
            PhysicsBody.Measure([new PhysicsBodyModel("ears", new byte[256], null, null, 0, "ears")], body.Pose, body.Pose);
        }
        catch (Exception e)
        {
            stopped = e.Message;
        }
        Require(stopped == "None of your body's skin is drawn, so there is nothing to measure.",
            $"physics fit: an accessory model that can't be read is left out of the measure ({stopped})");
    }

    /// <summary> A file whose right chest chain bumps into a ball on the left breast's bone, as some chest mods do: a zero-length capsule, or a sphere. </summary>
    private static PhybFile PartBallFile(Vector3 end = default, bool sphere = false, string ballBone = "j_mune_l", string chainBone = "j_mune_r")
    {
        var file = new PhybFile();
        if (sphere)
            file.Collision.Spheres.Add(new PhybSphere { Name = PhybName.From("left_breast"), Bone = PhybName.From(ballBone), Thickness = 0.025f });
        else
            file.Collision.Capsules.Add(new PhybCapsule
            {
                Name = PhybName.From("left_breast"), StartBone = PhybName.From("j_mune_l"), EndBone = PhybName.From("j_mune_l"), EndOffset = end, Radius = 0.025f,
            });
        file.Simulators.Add(new PhybSimulator
        {
            Group = PhysicsAreas.ChestGroup,
            Collisions = [new PhybCollisionRef { Name = PhybName.From("left_breast"), Type = PhybCollisionType.Outside }],
            Chains =
            [
                new PhybChain
                {
                    LastBoneOffset = new Vector3(0.12f, 0, 0),
                    Nodes = [new PhybNode { Bone = PhybName.From(chainBone) }, new PhybNode { Radius = 0.04f, CollisionFlags = 1 }],
                },
            ],
        });
        return file;
    }

    /// <summary> <paramref name="body"/> with every point <paramref name="from"/> carries moved wholly onto <paramref name="to"/>. </summary>
    private static PhysicsBody Reweighted(PhysicsBody body, string from, string to)
    {
        var bones = body.Bones.Append(to).ToList();
        var source = body.BoneIndex(from);
        var pointBones = (int[])body.PointBones.Clone();
        var pointWeights = (float[])body.PointWeights.Clone();
        for (var p = 0; p < body.Points.Length; p++)
        {
            if (Enumerable.Range(0, PhysicsBody.Influences).All(i => pointBones[p * PhysicsBody.Influences + i] != source))
                continue;
            for (var i = 0; i < PhysicsBody.Influences; i++)
            {
                pointBones[p * PhysicsBody.Influences + i] = i == 0 ? bones.Count - 1 : -1;
                pointWeights[p * PhysicsBody.Influences + i] = i == 0 ? 1 : 0;
            }
        }
        return PhysicsBody.Of(bones, body.Points, pointBones, pointWeights, body.Areas, body.Pose);
    }

    /// <summary> <paramref name="body"/> without the points <paramref name="bone"/> carries. </summary>
    private static PhysicsBody Without(PhysicsBody body, string bone)
    {
        var index = body.BoneIndex(bone);
        var keep = Enumerable.Range(0, body.Points.Length)
            .Where(p => Enumerable.Range(0, PhysicsBody.Influences).All(i => body.PointBones[p * PhysicsBody.Influences + i] != index)).ToList();
        return PhysicsBody.Of(body.Bones, keep.Select(p => body.Points[p]).ToArray(),
            keep.SelectMany(p => body.PointBones.Skip(p * PhysicsBody.Influences).Take(PhysicsBody.Influences)).ToArray(),
            keep.SelectMany(p => body.PointWeights.Skip(p * PhysicsBody.Influences).Take(PhysicsBody.Influences)).ToArray(),
            keep.Select(p => body.Areas[p]).ToArray(), body.Pose);
    }

    /// <summary> Balls on a breast's own bone fit the breast you see, not all the skin around the bone at its root. </summary>
    private static void CheckPartBalls()
    {
        var (body, skeleton) = SyntheticBody();
        // The dome is a half ball of 6 cm around this point.
        var middle = new Vector3(0.08f, 1.2f, 0.10f);
        bool Fits(PhysicsFitResult fit, PhysicsFitTarget target)
            => fit.Items.FirstOrDefault(item => item.Name == "left_breast") is { Shape: { } move } ball && ball.Target == target &&
               Vector3.Distance(body.Pose["j_mune_l"].Place(move.Start), middle) < 0.005f && move.Start == move.End && MathF.Abs(ball.Fitted - 0.06f) < 0.005f &&
               ball.Detail.Contains("middle of your left breast", StringComparison.Ordinal);
        Require(Fits(PhysicsFit.Measure(PartBallFile(), body, skeleton), PhysicsFitTarget.Capsule) &&
                Fits(PhysicsFit.Measure(PartBallFile(sphere: true), body, skeleton), PhysicsFitTarget.Sphere),
            "physics fit: a ball on a breast bone that another area bumps into moves to the middle of the breast and takes its size");

        var inner = PhysicsFit.Measure(PartBallFile(sphere: true, ballBone: "iv_c_mune_l"), body, skeleton).Items.FirstOrDefault(item => item.Name == "left_breast");
        Require(inner is { Shape: { } fromInner } && Vector3.Distance(body.Pose["iv_c_mune_l"].Place(fromInner.Start), middle) < 0.005f &&
                MathF.Abs(inner.Fitted - 0.06f) < 0.005f,
            "physics fit: a ball on iv_c_mune_l, under the breast bone, fits the breast it belongs to");

        // Only the left breast's own chain bumps into its ball: moved into the breast, it would sit on that chain's tip.
        var own = PhysicsFit.Measure(PartBallFile(chainBone: "j_mune_l"), body, skeleton).Items.FirstOrDefault(item => item.Name == "left_breast");
        Require(own is { Shape: null, Detail: "Your skin's thickness around it." },
            "physics fit: a ball only its own breast's chain bumps into keeps the thickness rule and stays on its bone");

        var puffy = PhysicsFit.Measure(PartBallFile(), Reweighted(body, "j_mune_l", "iv_c_mune_l"), skeleton).Items.FirstOrDefault(item => item.Name == "left_breast");
        Require(puffy is { Shape: { } nips } && Vector3.Distance(body.Pose["j_mune_l"].Place(nips.Start), middle) < 0.005f && MathF.Abs(puffy.Fitted - 0.06f) < 0.005f,
            "physics fit: breast skin carried only by iv_c_mune_l, as Puffy Nips weights it, still places and sizes the breast's ball");

        var bare = PhysicsFit.Measure(PartBallFile(), Without(body, "j_mune_l"), skeleton);
        Require(bare.Items.All(item => item.Name != "left_breast") && bare.Notes.Contains("left_breast: none of your skin moves with j_mune_l, so it stays as it is."),
            "physics fit: a breast ball without breast skin stays as it is, and the fit says why");

        // A butt ball the other cheek bumps into: the cheek's bone carries a third of its skin, as butt bones do.
        var (hips, hipsSkeleton) = HipsBody(0.09f, new Vector3(0.1f, 1, 0));
        var butt = new PhybFile();
        butt.Collision.Spheres.Add(new PhybSphere { Name = PhybName.From("butt_l"), Bone = PhybName.From("iv_shiri_l"), Thickness = 0.03f });
        butt.Simulators.Add(new PhybSimulator
        {
            Group = PhysicsAreas.ChestGroup,
            Collisions = [new PhybCollisionRef { Name = PhybName.From("butt_l"), Type = PhybCollisionType.Outside }],
            Chains =
            [
                new PhybChain
                {
                    LastBoneOffset = new Vector3(0.06f, 0, 0),
                    Nodes = [new PhybNode { Bone = PhybName.From("iv_shiri_r") }, new PhybNode { Radius = 0.03f, CollisionFlags = 1 }],
                },
            ],
        });
        var cheek = PhysicsFit.Measure(butt, hips, hipsSkeleton).Items.FirstOrDefault(item => item.Name == "butt_l");
        Require(cheek is { Shape: { } toCheek } && Vector3.Distance(hips.Pose["iv_shiri_l"].Place(toCheek.Start), new Vector3(0.07f, 0.95f, -0.08f)) < 0.01f &&
                MathF.Abs(cheek.Fitted - 0.06f) < 0.01f && cheek.Detail.Contains("middle of your left butt cheek", StringComparison.Ordinal),
            "physics fit: a ball on a butt bone, which carries only a third of the cheek, moves to the middle of the cheek and takes its size");

        // 16 cm along the bone, so the dome lies beside it.
        var along = PhysicsFit.Measure(PartBallFile(new Vector3(0.16f, 0, 0)), body, skeleton).Items.FirstOrDefault(item => item.Name == "left_breast");
        Require(along is { Shape: null, Detail: "Your skin's thickness around it." },
            "physics fit: a capsule along the breast bone keeps the thickness rule and stays where it is");
    }

    /// <summary>
    /// <see cref="SyntheticBody"/> with a groin: a member 2 cm thick and 15 cm long on iv_ochinko_a, pointing forward; a flaccid one
    /// on iv_funyachin_phy_a to _d, a cone from 2.5 to 1.5 cm over 16 cm; and a testicle ball 2.5 cm wide, 4 cm below iv_kougan_l.
    /// </summary>
    private static (PhysicsBody Body, PhysicsSkeleton Skeleton) GenitalBody()
    {
        var (body, skeleton) = SyntheticBody();
        var forward = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2);
        var down = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2);
        (string Name, string Parent, Vector3 At, Quaternion Turn)[] bones =
        [
            ("iv_ochinko_a", "j_kosi", new Vector3(0, 0.95f, 0.08f), forward),
            ("iv_funyachin_phy_a", "j_kosi", new Vector3(-0.1f, 0.95f, 0.08f), forward),
            ("iv_funyachin_phy_b", "iv_funyachin_phy_a", new Vector3(-0.1f, 0.95f, 0.12f), forward),
            ("iv_funyachin_phy_c", "iv_funyachin_phy_b", new Vector3(-0.1f, 0.95f, 0.16f), forward),
            ("iv_funyachin_phy_d", "iv_funyachin_phy_c", new Vector3(-0.1f, 0.95f, 0.20f), forward),
            ("iv_kougan_l", "j_kosi", new Vector3(0.03f, 0.92f, 0.06f), down),
        ];
        var groin = new PhysicsSkeleton([..skeleton.Body, ..bones.Select(bone => new PhysicsBone(bone.Name, bone.Parent, Vector3.Zero, Quaternion.Identity))]);
        var pose = new Dictionary<string, PhysicsFrame>(body.Pose, StringComparer.Ordinal);
        foreach (var bone in bones)
            pose[bone.Name] = PhysicsFrame.Rest(bone.At, bone.Turn);
        var skin = new List<(Vector3, (string, float)[], float, string?)>();
        for (var z = 0.08f; z <= 0.2301f; z += 0.01f)
            for (var a = 0; a < 16; a++)
                skin.Add((new Vector3(0.02f * MathF.Cos(a * MathF.Tau / 16), 0.95f + 0.02f * MathF.Sin(a * MathF.Tau / 16), z), [("iv_ochinko_a", 1f)], 1e-4f, null));
        // Each ring of the cone on the bone it lies along.
        string[] soft = ["iv_funyachin_phy_a", "iv_funyachin_phy_b", "iv_funyachin_phy_c", "iv_funyachin_phy_d"];
        for (var s = 0f; s <= 0.1601f; s += 0.005f)
        {
            var radius = 0.025f - 0.01f * s / 0.16f;
            var bone = soft[Math.Min(3, (int)(s / 0.04f))];
            for (var a = 0; a < 16; a++)
                skin.Add((new Vector3(-0.1f + radius * MathF.Cos(a * MathF.Tau / 16), 0.95f + radius * MathF.Sin(a * MathF.Tau / 16), 0.08f + s), [(bone, 1f)], 1e-4f,
                    null));
        }
        for (var ring = 0; ring <= 12; ring++)
        {
            var polar = ring * MathF.PI / 12;
            var count = ring is 0 or 12 ? 1 : 24;
            for (var a = 0; a < count; a++)
            {
                var angle = a * MathF.Tau / count;
                skin.Add((new Vector3(0.03f + 0.025f * MathF.Sin(polar) * MathF.Cos(angle), 0.88f + 0.025f * MathF.Cos(polar), 0.06f + 0.025f * MathF.Sin(polar) * MathF.Sin(angle)),
                    [("iv_kougan_l", 1f)], 1e-4f, null));
            }
        }
        return (With(body, pose, skin.ToArray()), groin);
    }

    /// <summary> <see cref="SyntheticFile"/> with a groin area: a penis, a flaccid penis of four bones and a testicle, each tip bumping into the thigh unless told not to. </summary>
    private static PhybFile GenitalFile(uint flags = 1)
    {
        var file = SyntheticFile(0.12f);
        PhybChain Chain(Vector3 tip, params string[] bones) => new()
        {
            LastBoneOffset = tip,
            Nodes =
            [
                new PhybNode { Bone = PhybName.From(bones[0]) },
                ..bones.Skip(1).Select(bone => new PhybNode { Bone = PhybName.From(bone), Radius = 0.01f, CollisionFlags = flags }),
                new PhybNode { Radius = 0.01f, CollisionFlags = flags },
            ],
        };
        file.Simulators.Add(new PhybSimulator
        {
            Group = PhysicsAreas.ChestGroup,
            Collisions = [new PhybCollisionRef { Name = PhybName.From("thigh_l"), Type = PhybCollisionType.Outside }],
            Chains =
            [
                Chain(new Vector3(0.35f, 0, 0), "iv_ochinko_a"),
                Chain(new Vector3(0.02f, 0, 0), "iv_funyachin_phy_a", "iv_funyachin_phy_b", "iv_funyachin_phy_c", "iv_funyachin_phy_d"),
                Chain(new Vector3(0.2f, 0, 0), "iv_kougan_l"),
            ],
        });
        return file;
    }

    /// <summary> Genital areas: tips move to the end of the member or the middle of a testicle, and a longer chain's points take the part's thickness. </summary>
    private static void CheckGenitals()
    {
        var (body, skeleton) = GenitalBody();
        var fit = PhysicsFit.Measure(GenitalFile(), body, skeleton);
        PhysicsFitItem? Find(string root, PhysicsFitTarget target) => fit.Items.FirstOrDefault(item => item.Name == root && item.Target == target);
        Vector3? Moved(PhysicsFitItem? place, string before) => place?.Move is { } move ? body.Pose[before].Place(move.Offset) : null;

        var penis = Find("iv_ochinko_a", PhysicsFitTarget.Place);
        Require(Moved(penis, "iv_ochinko_a") is { } end && Vector3.Distance(end, new Vector3(0, 0.95f, 0.23f)) < 0.005f &&
                penis!.Detail.Contains("swing the penis about", StringComparison.Ordinal) &&
                Find("iv_ochinko_a", PhysicsFitTarget.Chain) is { FittedAtPlace: { } size } && MathF.Abs(size - 0.01f) <= 0.002f,
            "physics fit: a penis's tip moves to the end of the member, sized to reach 1 cm past it, and the tooltip says how its swing changes");

        var testicle = Find("iv_kougan_l", PhysicsFitTarget.Place);
        Require(Moved(testicle, "iv_kougan_l") is { } centre && Vector3.Distance(centre, new Vector3(0.03f, 0.88f, 0.06f)) < 0.005f &&
                Find("iv_kougan_l", PhysicsFitTarget.Chain) is { } ball && MathF.Abs(ball.FittedWith(true) - 0.025f) <= 0.003f,
            "physics fit: a testicle's tip moves to the middle of the testicle and fills it");

        var soft = Find("iv_funyachin_phy_a", PhysicsFitTarget.Points);
        static float Cone(float along) => 0.025f - 0.01f * along / 0.16f;
        Require(soft is { Points: [var b, var c, var d, var tip], Label: "Penis, flaccid: point sizes" } && MathF.Abs(b - Cone(0.04f)) <= 0.003f &&
                MathF.Abs(c - Cone(0.08f)) <= 0.003f && MathF.Abs(d - Cone(0.12f)) <= 0.003f && tip == soft.Fitted &&
                soft.Detail.Contains("point 2: 1.0 to ", StringComparison.Ordinal) &&
                !fit.Items.Any(item => item is { Name: "iv_funyachin_phy_a", Target: PhysicsFitTarget.Chain }),
            "physics fit: a flaccid chain gets one row of point sizes that follow its thickness, the tip's last");
        var applied = GenitalFile();
        PhysicsFit.Apply(applied, fit.Items);
        var nodes = applied.Simulators[3].Chains[1].Nodes;
        Require(soft is { Points: { Count: 4 } sizes } && nodes[0].Radius == 0 && nodes[1].Radius == sizes[0] && nodes[2].Radius == sizes[1] &&
                nodes[3].Radius == sizes[2] && nodes[4].Radius == soft.FittedWith(true),
            "physics fit: applying point sizes sets each moving point and leaves the bone");

        var loose = PhysicsFit.Measure(GenitalFile(flags: 0), body, skeleton);
        Require(loose.Items.Any(item => item is { Name: "iv_ochinko_a", Target: PhysicsFitTarget.Place }) &&
                !loose.Items.Any(item => item is { Name: "iv_ochinko_a", Target: PhysicsFitTarget.Chain or PhysicsFitTarget.Points }),
            "physics fit: a penis chain that bumps into nothing still gets its tip place, but no size");
        var noBreasts = GenitalFile();
        noBreasts.Simulators[1].Excluded = true;
        Require(!PhysicsFit.Measure(noBreasts, body, skeleton).BreastTips, "physics fit: genital tips aren't breast tips, so the breast tip choice stays hidden");
    }

    /// <summary> Two breast bones 6.6 cm apart under the spine, X axis forward, with real rest places, as a real skeleton has them. </summary>
    private static PhysicsSkeleton ChestSkeleton()
    {
        var forward = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2);
        return new PhysicsSkeleton(
        [
            new PhysicsBone("n_root", "", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("j_kosi", "n_root", new Vector3(0, 1, 0), Quaternion.Identity),
            new PhysicsBone("j_sebo_b", "j_kosi", new Vector3(0, 0.18f, 0), Quaternion.Identity),
            new PhysicsBone("j_mune_l", "j_sebo_b", new Vector3(0.033f, 0, 0), forward),
            new PhysicsBone("j_mune_r", "j_sebo_b", new Vector3(-0.033f, 0, 0), forward),
        ]);
    }

    /// <summary> Both chest chains, each bumping into a ball on the other breast's bone, as some chest mods do, their tips 14 cm out. </summary>
    private static PhybFile ChestFile(float ball = 0.025f)
    {
        var file = new PhybFile();
        foreach (var (name, bone) in new[] { ("left_breast", "j_mune_l"), ("right_breast", "j_mune_r") })
            file.Collision.Capsules.Add(new PhybCapsule { Name = PhybName.From(name), StartBone = PhybName.From(bone), EndBone = PhybName.From(bone), Radius = ball });
        var simulator = new PhybSimulator { Group = PhysicsAreas.ChestGroup };
        simulator.Collisions.Add(new PhybCollisionRef { Name = PhybName.From("left_breast"), Type = PhybCollisionType.Outside });
        simulator.Collisions.Add(new PhybCollisionRef { Name = PhybName.From("right_breast"), Type = PhybCollisionType.Outside });
        // The left breast bumps into the right one's ball (bit 1), the right into the left's (bit 0).
        foreach (var (bone, bit) in new[] { ("j_mune_l", 2u), ("j_mune_r", 1u) })
            simulator.Chains.Add(new PhybChain
            {
                LastBoneOffset = new Vector3(0.14f, 0, 0),
                Nodes = [new PhybNode { Bone = PhybName.From(bone) }, new PhybNode { Radius = 0.04f, CollisionFlags = bit }],
            });
        file.Simulators.Add(simulator);
        return file;
    }

    /// <summary> The fit's rows for <see cref="ChestFile"/>: both balls to <paramref name="ball"/>, then each tip's place and its size. </summary>
    private static List<PhysicsFitItem> ChestItems(PhysicsSkeleton skeleton, float ball, Vector3 left, Vector3 right, float size)
    {
        var items = new List<PhysicsFitItem>();
        foreach (var name in new[] { "left_breast", "right_breast" })
            items.Add(new PhysicsFitItem(PhysicsFitTarget.Capsule, name, -1, -1, name, "Moves it to the middle of the breast.", 0.025f, ball));
        for (var c = 0; c < 2; c++)
        {
            var bone = skeleton.Get(c == 0 ? "j_mune_l" : "j_mune_r")!;
            var tip = c == 0 ? left : right;
            items.Add(new PhysicsFitItem(PhysicsFitTarget.Place, bone.Name, 0, c, "tip place", "Moves the tip.", 0.14f, tip.Length(),
                new PhysicsTipMove(tip, Vector3.Distance(tip, new Vector3(0.14f, 0, 0)), bone.Parent, bone.Position, Quaternion.Normalize(bone.Rotation))));
            items.Add(new PhysicsFitItem(PhysicsFitTarget.Chain, bone.Name, 0, c, "tip size", "Fills the part around the tip.", 0.04f, size, FittedAtPlace: size));
        }
        return items;
    }

    /// <summary> Every moving point's surface gap to each capsule it stays out of, where physics runs: in the rest pose. </summary>
    private static List<float> Gaps(PhybFile file, PhysicsSkeleton skeleton)
    {
        var rest = PhysicsBody.RestPose(skeleton);
        var gaps = new List<float>();
        foreach (var simulator in file.Simulators.Where(simulator => !simulator.Excluded))
            foreach (var chain in simulator.Chains)
                for (var n = 1; n < chain.Nodes.Count; n++)
                {
                    var node = chain.Nodes[n];
                    Vector3? at = !node.Bone.IsEmpty ? rest.TryGetValue(node.Bone.Text, out var own) ? own.Position : null
                        : rest.TryGetValue(chain.Nodes[n - 1].Bone.Text, out var before) ? before.Place(chain.LastBoneOffset) : null;
                    if (at is not { } point)
                        continue;
                    foreach (var shape in PhysicsCollisions.Shapes(simulator, chain, node).Where(shape => shape.Type != PhybCollisionType.Inside))
                        foreach (var capsule in file.Collision.Capsules.Where(capsule => capsule.Name.Text == shape.Name.Text))
                        {
                            var a = rest[capsule.StartBone.Text].Place(capsule.StartOffset);
                            var b = rest[capsule.EndBone.Text].Place(capsule.EndOffset);
                            var t = (b - a).LengthSquared() < 1e-12f ? 0 : Math.Clamp(Vector3.Dot(point - a, b - a) / (b - a).LengthSquared(), 0, 1);
                            gaps.Add(Vector3.Distance(point, a + t * (b - a)) - node.Radius - capsule.Radius);
                        }
                }
        return gaps;
    }

    /// <summary>
    /// Two breast domes like <see cref="SyntheticBody"/>'s, 6 cm wide and 10 to 16 cm in front of their bones on <see cref="ChestSkeleton"/>,
    /// grown <paramref name="scale"/> times about their bones in the measuring pose, as Customize+ grows them.
    /// </summary>
    private static PhysicsBody BreastsBody(float scale)
    {
        var pose = PhysicsBody.RestPose(ChestSkeleton());
        string[] bones = ["j_mune_l", "j_mune_r", "j_sebo_b"];
        var points = new List<Vector3>();
        var pointBones = new List<int>();
        var pointWeights = new List<float>();
        foreach (var (bone, side) in new[] { (0, "j_mune_l"), (1, "j_mune_r") })
        {
            var root = pose[side].Position;
            for (var ring = 0; ring <= 12; ring++)
            {
                var polar = ring * (MathF.PI / 2) / 12;
                var count = ring == 0 ? 1 : 24;
                for (var a = 0; a < count; a++)
                {
                    var angle = a * MathF.Tau / count;
                    var point = root + new Vector3(0.06f * MathF.Sin(polar) * MathF.Cos(angle), 0.06f * MathF.Sin(polar) * MathF.Sin(angle), 0.1f + 0.06f * MathF.Cos(polar));
                    points.Add(root + (point - root) * scale);
                    pointBones.AddRange([bone, 2, -1, -1]);
                    pointWeights.AddRange([0.6f, 0.4f, 0, 0]);
                }
            }
        }
        return PhysicsBody.Of(bones, points.ToArray(), pointBones.ToArray(), pointWeights.ToArray(), Enumerable.Repeat(1e-4f, points.Count).ToArray(), pose);
    }

    /// <summary> Fitted sizes keep 1 cm clear where physics runs, the shape giving way first, settling in one pass. </summary>
    private static void CheckClearance()
    {
        var skeleton = ChestSkeleton();
        var file = ChestFile();
        // Both balls at 6 cm on their bones, both tips 11 cm forward at 6 cm: each tip comes within 0.8 cm of the other side's ball.
        var items = ChestItems(skeleton, 0.06f, new Vector3(0.11f, 0, 0), new Vector3(0.11f, 0, 0), 0.06f);
        var all = Enumerable.Range(0, items.Count).ToHashSet();
        var held = PhysicsFitClearance.Hold(file, items, all, skeleton);
        var applied = file.Clone();
        PhysicsFit.Apply(applied, all.Order().Select(i => held[i]));
        Require(held.Count == items.Count && Gaps(applied, skeleton) is { Count: 2 } gaps && gaps.All(gap => gap >= PhysicsFitClearance.Margin - 1e-4f),
            "physics fit: after clearance no point comes within 1 cm of a shape it bumps into, at rest");
        Require(Enumerable.Range(0, held.Count).Where(i => held[i].Target == PhysicsFitTarget.Chain).All(i => held[i].FittedWith(true) == items[i].FittedWith(true)) &&
                held[0].Fitted < 0.06f,
            "physics fit: the shape gives way before the tip");
        Require(PhysicsFitClearance.Hold(file, held, all, skeleton).SequenceEqual(held), "physics fit: clearance settles: a second pass changes nothing");
        Require(held.First(item => item.Name == "right_breast").Detail.EndsWith("Kept 1 cm off the left breast's tip, which it would push all the time.", StringComparison.Ordinal),
            "physics fit: a held-back size says why");

        // The left chain also bumps into its own breast's ball: that pair is held too, or the ball would push its own tip all the time.
        var both = ChestFile();
        both.Simulators[0].Chains[0].Nodes[1].CollisionFlags = 3;
        var ownHeld = PhysicsFitClearance.Hold(both, items, all, skeleton);
        var ownApplied = both.Clone();
        PhysicsFit.Apply(ownApplied, all.Order().Select(i => ownHeld[i]));
        Require(Gaps(ownApplied, skeleton) is { Count: 3 } ownGaps && ownGaps.All(gap => gap >= PhysicsFitClearance.Margin - 1e-4f),
            "physics fit: a chain bumping into a ball on its own breast keeps 1 cm from it too");

        // A flaccid chain's middle point beside a ball on the hips: the ball goes down to 0.5 cm, then that point gives way.
        var forward = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2);
        var groin = new PhysicsSkeleton(
        [
            new PhysicsBone("n_root", "", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("j_kosi", "n_root", new Vector3(0, 1, 0), Quaternion.Identity),
            new PhysicsBone("iv_funyachin_phy_a", "j_kosi", new Vector3(0, -0.05f, 0.08f), forward),
            new PhysicsBone("iv_funyachin_phy_b", "iv_funyachin_phy_a", new Vector3(0.04f, 0, 0), Quaternion.Identity),
        ]);
        var soft = new PhybFile();
        soft.Collision.Capsules.Add(new PhybCapsule
        {
            Name = PhybName.From("hip_ball"), StartBone = PhybName.From("j_kosi"), EndBone = PhybName.From("j_kosi"),
            StartOffset = new Vector3(0.03f, -0.05f, 0.12f), EndOffset = new Vector3(0.03f, -0.05f, 0.12f), Radius = 0.02f,
        });
        soft.Simulators.Add(new PhybSimulator
        {
            Group = PhysicsAreas.ChestGroup,
            Collisions = [new PhybCollisionRef { Name = PhybName.From("hip_ball"), Type = PhybCollisionType.Outside }],
            Chains =
            [
                new PhybChain
                {
                    LastBoneOffset = new Vector3(0.04f, 0, 0),
                    Nodes =
                    [
                        new PhybNode { Bone = PhybName.From("iv_funyachin_phy_a") },
                        new PhybNode { Bone = PhybName.From("iv_funyachin_phy_b"), Radius = 0.01f, CollisionFlags = 1 },
                        new PhybNode { Radius = 0.01f, CollisionFlags = 1 },
                    ],
                },
            ],
        });
        List<PhysicsFitItem> softItems =
        [
            new(PhysicsFitTarget.Capsule, "hip_ball", -1, -1, "Hip ball (hip_ball)", "Your skin's thickness around it.", 0.02f, 0.02f),
            new(PhysicsFitTarget.Points, "iv_funyachin_phy_a", 0, 0, "Penis, flaccid: point sizes", "Fills the part around each point.", 0.01f, 0.02f,
                Points: [0.02f, 0.02f], PointsNow: [0.01f, 0.01f]),
        ];
        var softHeld = PhysicsFitClearance.Hold(soft, softItems, new HashSet<int> { 0, 1 }, groin);
        var softApplied = soft.Clone();
        PhysicsFit.Apply(softApplied, softHeld);
        Require(MathF.Abs(softHeld[0].Fitted - 0.005f) < 1e-5f && softHeld[1] is { Points: [var softMiddle, var softTip] } softRow &&
                MathF.Abs(softMiddle - 0.015f) < 1e-4f && softTip == softRow.Fitted && softRow.Fitted == 0.02f &&
                softRow.Detail.Contains("hip_ball", StringComparison.Ordinal) && softRow.Detail.EndsWith("which would push it all the time.", StringComparison.Ordinal) &&
                Gaps(softApplied, groin).All(gap => gap >= PhysicsFitClearance.Margin - 1e-4f),
            "physics fit: a point-sizes row gives way at the point a shape at its smallest still sits too close to");

        // Customize+ grows both breasts 1.7 times: the fit measures what you see, and clearance keeps it apart where physics runs.
        var body = BreastsBody(1.7f);
        var fit = PhysicsFit.Measure(ChestFile(), body, skeleton);
        var ticked = Enumerable.Range(0, fit.Items.Count).Where(i => fit.Items[i].Changes && !fit.Items[i].Optional).ToHashSet();
        var grown = PhysicsFitClearance.Hold(ChestFile(), fit.Items, ticked, skeleton);
        var grownFile = ChestFile();
        PhysicsFit.Apply(grownFile, ticked.Order().Select(i => grown[i]));
        Require(fit.Items.Any(item => item.Name == "left_breast") && Enumerable.Range(0, grown.Count).Any(i => grown[i].Fitted < fit.Items[i].Fitted) &&
                Gaps(grownFile, skeleton).All(gap => gap >= PhysicsFitClearance.Margin - 1e-4f),
            "physics fit: breasts Customize+ grows 1.7 times keep 1 cm apart where physics runs");

        // The tip places unticked: the tips stay 14 cm out, clear of the balls, which keep their size.
        var unplaced = all.Where(i => items[i].Target != PhysicsFitTarget.Place).ToHashSet();
        var kept = PhysicsFitClearance.Hold(file, items, unplaced, skeleton);
        Require(ReferenceEquals(kept[0], items[0]) && ReferenceEquals(kept[1], items[1]) && kept[0].Fitted == 0.06f,
            "physics fit: with the tip place unticked, clearance is measured from the tip where it is");

        // The balls unticked but 10 cm in the file: they stay, and the tips give way instead.
        var thick = ChestFile(0.1f);
        var unballed = all.Where(i => items[i].Target != PhysicsFitTarget.Capsule).ToHashSet();
        var tipsOnly = PhysicsFitClearance.Hold(thick, items, unballed, skeleton);
        var thickApplied = thick.Clone();
        PhysicsFit.Apply(thickApplied, unballed.Order().Select(i => tipsOnly[i]));
        Require(ReferenceEquals(tipsOnly[0], items[0]) && thickApplied.Collision.Capsules.All(capsule => capsule.Radius == 0.1f) &&
                tipsOnly.Where(item => item.Target == PhysicsFitTarget.Chain).All(item => item.FittedWith(true) < 0.06f) &&
                Gaps(thickApplied, skeleton).All(gap => gap >= PhysicsFitClearance.Margin - 1e-4f),
            "physics fit: a shape the fit doesn't change is never touched, even when it overlaps");

        // Tips moved in to the middle between the breasts: the balls go down to 0.5 cm, the rest comes off the tips.
        var close = ChestItems(skeleton, 0.06f, new Vector3(0.02f, 0, 0.033f), new Vector3(0.02f, 0, -0.033f), 0.06f);
        var floor = PhysicsFitClearance.Hold(file, close, all, skeleton);
        var floorApplied = file.Clone();
        PhysicsFit.Apply(floorApplied, all.Order().Select(i => floor[i]));
        Require(floor.Where(item => item.Target == PhysicsFitTarget.Capsule).All(item => MathF.Abs(item.Fitted - 0.005f) < 1e-5f) &&
                floor.Where(item => item.Target == PhysicsFitTarget.Chain).All(item => item.FittedWith(true) < 0.06f) &&
                Gaps(floorApplied, skeleton).All(gap => gap >= PhysicsFitClearance.Margin - 1e-4f),
            "physics fit: a shape stops at the smallest size, and the rest lowers the tip");
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
