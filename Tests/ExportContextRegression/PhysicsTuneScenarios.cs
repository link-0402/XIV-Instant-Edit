using System.Buffers.Binary;
using System.Numerics;
using InstantEdit.Services.Physics;
using InstantEdit.Ui;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Tuning the body to the body it's on: what each slider reads and writes, the still pose a body is
/// measured in (Customize+ and the bust size, nothing else), and how far a part can swing before it
/// sinks into the torso.
/// </summary>
internal static class PhysicsTuneScenarios
{
    public static void Run()
    {
        CheckKnobs();
        CheckBust();
        CheckProfile();
        CheckReferencePose();
        CheckClearance();
        CheckFit();
        CheckSliderScale();
    }

    private static PhybChain Chain(string bone, float length = 0.1f)
        => new()
        {
            LastBoneOffset = new Vector3(length, 0, 0),
            Nodes = [new PhybNode { Bone = PhybName.From(bone), ConeMaxAngle = 1f, ConeAxisOffset = new Vector3(0.1f, 0, 0) }, new PhybNode { ConeMaxAngle = MathF.PI }],
        };

    private static void CheckKnobs()
    {
        var simulator = new PhybSimulator();
        var chain = Chain("j_mune_l");
        chain.Nodes.Insert(1, new PhybNode { Bone = PhybName.From("middle"), Radius = 0.01f, GravityScale = 0.25f, ConeMaxAngle = 0.5f });
        chain.Nodes[^1].Radius = 0.02f;
        chain.Nodes[^1].GravityScale = 0.5f;
        simulator.Chains.Add(chain);
        simulator.Attracts.Add(new PhybAttract { Bone = PhybName.From("j_sebo_b"), Chain = 0, Node = 2, Stiffness = 0.03f });
        simulator.Attracts.Add(new PhybAttract { Bone = PhybName.From("j_sebo_b"), Chain = 0, Node = 2, Stiffness = 0.001f });

        PhysicsKnobs.Write(simulator, 0, PhysicsKnob.Size, 4);
        PhysicsKnobs.Write(simulator, 0, PhysicsKnob.Weight, 1);
        PhysicsKnobs.Write(simulator, 0, PhysicsKnob.Swing, 30);
        PhysicsKnobs.Write(simulator, 0, PhysicsKnob.Length, 20);
        PhysicsKnobs.Write(simulator, 0, PhysicsKnob.Firmness, 0.05f);
        Require(MathF.Abs(chain.Nodes[^1].Radius - 0.04f) < 1e-6f && MathF.Abs(chain.Nodes[1].Radius - 0.02f) < 1e-6f &&
                MathF.Abs(chain.Nodes[1].GravityScale - 0.5f) < 1e-6f,
            "physics knobs: size and weight go to the tip, the points between keeping their share of it");
        Require(MathF.Abs(PhysicsKnobs.Read(simulator, 0, PhysicsKnob.Swing)!.Value - 30) < 1e-3f &&
                MathF.Abs(chain.Nodes[1].ConeMaxAngle - 0.5f * PhysicsTemplates.Radians(30)) < 1e-5f &&
                Vector3.Distance(chain.LastBoneOffset, new Vector3(0.2f, 0, 0)) < 1e-6f,
            "physics knobs: swing is the bone's in degrees and length the tip's distance in centimetres, along the same direction");
        Require(simulator.Attracts[0].Stiffness == 0.05f && simulator.Attracts[1].Stiffness == 0.001f &&
                PhysicsKnobs.Read(simulator, 0, PhysicsKnob.Firmness) == 0.05f && PhysicsKnobs.Read(new PhybSimulator { Chains = [Chain("j_mune_r")] }, 0,
                    PhysicsKnob.Firmness) is null,
            "physics knobs: firmness is the strongest spring on the tip, and none without a spring");
        Require(PhysicsKnobs.Label(PhysicsKnob.Damping) == "Settle" && PhysicsKnobs.Label(PhysicsKnob.Swing) == "Swing limit" &&
                PhysicsKnobs.Label(PhysicsKnob.Follow) == "Follow pose" && PhysicsKnobs.Label(PhysicsKnob.Length) == "Tip length" &&
                PhysicsKnobs.Label(PhysicsKnob.Size) == "Tip size" && Enum.GetValues<PhysicsKnob>().Select(PhysicsKnobs.Label).Distinct().Count() == 12,
            "physics knobs: every slider has a plain name of its own");
        CheckSpringAim();
    }

    /// <summary> c0801's breast bones in their bind pose, from the game's base skeleton, under a root at the floor. </summary>
    private static PhysicsSkeleton GameChest() => new(
    [
        new PhysicsBone("n_root", "", Vector3.Zero, Quaternion.Identity),
        new PhysicsBone("j_sebo_b", "n_root", new Vector3(0, 1.05f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)),
        new PhysicsBone("j_mune_l", "j_sebo_b", new Vector3(0.05731833f, 0.004540924f, 0.033198476f), new Quaternion(-0.72570795f, -0.6785303f, -0.07961082f, 0.08128255f)),
    ]);

    private static void CheckSpringAim()
    {
        var skeleton = GameChest();
        var chest = new PhybSimulator { Chains = [Chain("j_mune_l", 0.1449f)] };
        // The game's own spring, on j_sebo_b past where the tip rests.
        var game = new PhybAttract { Bone = PhybName.From("j_sebo_b"), Offset = new Vector3(0.075f, 0.1929f, 0.077f), Chain = 0, Node = 1, Stiffness = 0.075f };
        // A preset's spring toward a point a metre above the floor, in the middle of the body.
        var preset = new PhybAttract { Bone = PhybName.From("n_root"), Offset = new Vector3(0, 1, 0), Chain = 0, Node = 1, Stiffness = 0.0005f };
        var rest = PhysicsTemplates.RestSpring(chest.Chains[0], 0, skeleton, 0.04f)!;
        Require(PhysicsKnobs.SpringAim(chest, game, skeleton) is < PhysicsKnobs.SpringAimLimit && PhysicsKnobs.SpringAim(chest, rest, skeleton) is < 0.01f,
            $"physics knobs: the game's chest spring and a new rest spring pull the tip toward where it rests ({PhysicsKnobs.SpringAim(chest, game, skeleton):F1}°)");
        Require(PhysicsKnobs.SpringAim(chest, preset, skeleton) is > 60,
            $"physics knobs: a spring toward the middle of the body pulls the tip far from where it rests ({PhysicsKnobs.SpringAim(chest, preset, skeleton):F0}°)");
        var pushed = new PhybAttract { Bone = game.Bone, Offset = game.Offset, Chain = 0, Node = 1, Stiffness = -0.075f };
        Require(PhysicsKnobs.SpringAim(chest, pushed, skeleton) is > 165,
            "physics knobs: a negative spring pushes the tip away from its point, so aimed past the rest place it pushes the tip off it");

        // A left breast in the game's model space (+Y up, +Z forward), its bone pointing forward.
        var body = new PhysicsSkeleton(
        [
            new PhysicsBone("n_root", "", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("j_mune_l", "n_root", new Vector3(0.05f, 1.18f, 0.03f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2)),
        ]);
        var breast = new PhybSimulator { Chains = [Chain("j_mune_l", 0.145f)] };
        PhybAttract Toward(Vector3 point, float stiffness) => new() { Bone = PhybName.From("n_root"), Offset = point, Chain = 0, Node = 1, Stiffness = stiffness };
        var down = Toward(new Vector3(0, 1, 0), 0.0005f);
        var lift = Toward(new Vector3(0, 1, 0), -0.001f);
        var atRest = Toward(new Vector3(0.05f, 1.18f, 0.3f), 0.05f);
        var inward = Toward(new Vector3(-0.3f, 1.18f, 0.1f), 0.05f);
        var behind = Toward(new Vector3(0.05f, 1.18f, -0.5f), 0.05f);
        Require(PhysicsKnobs.SpringName(breast, down, body) == "Pull down" && PhysicsKnobs.SpringName(breast, lift, body) == "Lift" &&
                PhysicsKnobs.SpringName(breast, atRest, body) == "Firmness" && PhysicsKnobs.SpringName(breast, inward, body) == "Pull together" &&
                PhysicsKnobs.SpringName(breast, behind, body) == "Spring" &&
                PhysicsKnobs.SpringName(breast, new PhybAttract { Bone = PhybName.From("missing"), Chain = 0, Node = 1, Stiffness = 1 }, body) == "Spring",
            "physics knobs: a spring is named for what it does: firmness toward the rest place, a lift or a pull elsewhere, and plain spring when it can't tell");

        // Tiramisu's lift: a negative spring the slider must not turn into a pull.
        chest.Attracts.Add(new PhybAttract { Bone = PhybName.From("n_root"), Offset = new Vector3(0, 1, 0), Chain = 0, Node = 1, Stiffness = -0.001f });
        PhysicsKnobs.Write(chest, 0, PhysicsKnob.Firmness, 0.005f);
        Require(chest.Attracts[0].Stiffness == -0.005f && PhysicsKnobs.Read(chest, 0, PhysicsKnob.Firmness) == 0.005f,
            "physics knobs: firmness sets a negative spring's strength and keeps it pushing");
        Require(PhysicsKnobs.SpringAim(chest, new PhybAttract { Bone = PhybName.From("missing"), Chain = 0, Node = 1 }, skeleton) is null &&
                PhysicsKnobs.SpringAim(chest, new PhybAttract { Bone = PhybName.From("n_root"), Chain = 1, Node = 1 }, skeleton) is null,
            "physics knobs: a spring on a bone or chain that isn't there has no aim");
    }

    private static void CheckBust()
    {
        var cmp = new byte[186752];
        var at = 2 * 9216 + 32 * 5120 + (7 >> 1) * 10 * 56 + (7 & 1) * 56 + 8 * 4;
        float[] range = [0.9f, 0.8f, 0.7f, 1.1f, 1.2f, 1.3f];
        for (var i = 0; i < range.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(cmp.AsSpan(at + i * 4), range[i]);
        Require(Vector3.Distance(PhysicsPose.Bust(cmp, 8, true, 1, 50), Vector3.One) < 1e-5f &&
                Vector3.Distance(PhysicsPose.Bust(cmp, 8, true, 1, 100), new Vector3(1.1f, 1.2f, 1.3f)) < 1e-5f &&
                Vector3.Distance(PhysicsPose.Bust(cmp, 8, true, 0, 0), new Vector3(0.9f, 0.8f, 0.7f)) < 1e-5f,
            "physics pose: the bust's scale runs from the clan's smallest to its largest with the bust size, as the game reads human.cmp");
        Require(PhysicsPose.Bust(cmp, 8, false, 1, 100) == Vector3.One && PhysicsPose.Bust(cmp, 8, true, 2, 100) == Vector3.One &&
                PhysicsPose.Bust(cmp.AsSpan(0, 1000), 8, true, 1, 100) == Vector3.One && PhysicsPose.Bust(cmp, 7, true, 1, 100) == Vector3.One,
            "physics pose: men, other body types, a short file and a clan without a range keep the breasts' own size");
    }

    private static void CheckProfile()
    {
        const string json = """
            {"Bones":{"j_mune_l":{"Scaling":{"X":1.05,"Y":1.45,"Z":1.3}},
                      "iv_c_mune_l":{"Translation":{"Y":0.032},"Rotation":{"Z":-4.0},"Scaling":{"X":1.0,"Y":1.0,"Z":1.0}},
                      "j_asi_a_l":{"Translation":{"X":0.03},"Scaling":{"X":1.0,"Y":1.0,"Z":1.0},"PropagateTranslation":true},
                      "n_hide":{}}}
            """;
        var bones = PhysicsPose.ParsePlus(json);
        Require(bones.Count == 4 && bones["j_mune_l"].Scaling == new Vector3(1.05f, 1.45f, 1.3f) && bones["j_mune_l"].Translation == Vector3.Zero &&
                bones["iv_c_mune_l"].Translation == new Vector3(0, 0.032f, 0) && bones["iv_c_mune_l"].Rotation == new Vector3(0, 0, -4) &&
                bones["j_asi_a_l"].PropagateTranslation && !bones["j_asi_a_l"].PropagateScale && bones["n_hide"].Scaling == Vector3.Zero &&
                PhysicsPose.ParsePlus("{}").Count == 0,
            "physics pose: a Customize+ profile reads with Customize+'s defaults, a missing scale being zero as its IPC leaves it out");
    }

    private static void CheckReferencePose()
    {
        var skeleton = new PhysicsSkeleton(
        [
            new PhysicsBone("n_root", "", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("j_kosi", "n_root", new Vector3(0, 1, 0), Quaternion.Identity),
            new PhysicsBone("j_asi_a_l", "j_kosi", new Vector3(0.1f, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2)),
            new PhysicsBone("j_asi_b_l", "j_asi_a_l", new Vector3(0.4f, 0, 0), Quaternion.Identity),
            new PhysicsBone("j_sebo_b", "j_kosi", new Vector3(0, 0.2f, 0), Quaternion.Identity),
            new PhysicsBone("j_mune_l", "j_sebo_b", new Vector3(0.05f, 0, 0.05f), Quaternion.Identity),
            new PhysicsBone("iv_c_mune_l", "j_mune_l", new Vector3(0.05f, 0, 0), Quaternion.Identity),
        ], []);
        var rest = PhysicsBody.RestPose(skeleton);
        Require(PhysicsPose.Reference(skeleton, null, Vector3.One).All(pair => pair.Value == rest[pair.Key]),
            "physics pose: without Customize+ and at the middle bust size, the body stands in its rest pose");

        var moved = PhysicsPose.Reference(skeleton, new Dictionary<string, PhysicsPlusBone>
        {
            ["j_asi_a_l"] = new(new Vector3(0.03f, 0, 0), Vector3.Zero, Vector3.One, Vector3.One, false, false, false, false),
        }, Vector3.One);
        var carried = PhysicsPose.Reference(skeleton, new Dictionary<string, PhysicsPlusBone>
        {
            ["j_asi_a_l"] = new(new Vector3(0.03f, 0, 0), Vector3.Zero, Vector3.One, Vector3.One, false, true, false, false),
        }, Vector3.One);
        // The thigh's X points down the leg, so moving it 3 cm along X moves it 3 cm down.
        Require(Vector3.Distance(moved["j_asi_a_l"].Position, rest["j_asi_a_l"].Position + new Vector3(0, -0.03f, 0)) < 1e-5f &&
                moved["j_asi_b_l"] == rest["j_asi_b_l"] &&
                Vector3.Distance(carried["j_asi_b_l"].Position, rest["j_asi_b_l"].Position + new Vector3(0, -0.03f, 0)) < 1e-5f,
            "physics pose: a Customize+ move goes along the bone's own axes and takes the bones under it only when it carries");

        var scaled = PhysicsPose.Reference(skeleton, new Dictionary<string, PhysicsPlusBone>
        {
            ["j_mune_l"] = new(Vector3.Zero, Vector3.Zero, new Vector3(1.05f, 1.45f, 1.3f), Vector3.One, false, false, false, false),
            ["iv_c_mune_l"] = new(Vector3.Zero, new Vector3(0, 0, 90), Vector3.One, Vector3.One, false, false, false, false),
        }, new Vector3(1.1f, 1.2f, 1.3f));
        var spread = PhysicsPose.Reference(skeleton, new Dictionary<string, PhysicsPlusBone>
        {
            ["j_mune_l"] = new(Vector3.Zero, Vector3.Zero, new Vector3(2, 2, 2), Vector3.One, false, false, false, true),
        }, Vector3.One);
        Require(Vector3.Distance(scaled["j_mune_l"].Scale, new Vector3(1.05f * 1.1f, 1.45f * 1.2f, 1.3f * 1.3f)) < 1e-5f &&
                scaled["iv_c_mune_l"].Scale == Vector3.One &&
                Quaternion.Dot(scaled["iv_c_mune_l"].Rotation, Quaternion.CreateFromYawPitchRoll(0, 0, MathF.PI / 2)) > 0.99999f,
            "physics pose: Customize+ scales a bone without its children, turns it by its angles as yaw, pitch and roll, and the bust scales the breasts after it");
        Require(Vector3.Distance(spread["iv_c_mune_l"].Scale, new Vector3(2)) < 1e-5f &&
                Vector3.Distance(spread["iv_c_mune_l"].Position - spread["j_mune_l"].Position, new Vector3(0.1f, 0, 0)) < 1e-5f,
            "physics pose: a scale that carries grows the bones under it and their distance from the bone");
    }

    /// <summary>
    /// A torso wall facing +Z at z = 0 and a breast in front of it, turning about a bone 3 cm behind
    /// the wall: the bone can swing until the breast's skin near the wall sinks into it.
    /// </summary>
    private static (PhysicsBody Body, PhysicsSkeleton Skeleton, PhybSimulator Simulator) Wall(float front)
    {
        var skeleton = new PhysicsSkeleton(
        [
            new PhysicsBone("j_kosi", "", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("j_mune_l", "j_kosi", new Vector3(0, 0, -0.03f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2)),
        ], []);
        var points = new List<Vector3>();
        var normals = new List<Vector3>();
        var owners = new List<int>();
        for (var x = -0.2f; x <= 0.2001f; x += 0.01f)
            for (var y = -0.2f; y <= 0.2001f; y += 0.01f)
            {
                points.Add(new Vector3(x, y, 0));
                normals.Add(Vector3.UnitZ);
                owners.Add(0);
            }
        for (var x = -0.04f; x <= 0.0401f; x += 0.01f)
            for (var y = -0.04f; y <= 0.0401f; y += 0.01f)
                for (var z = front; z <= front + 0.1001f; z += 0.01f)
                {
                    points.Add(new Vector3(x, y, z));
                    normals.Add(Vector3.UnitZ);
                    owners.Add(1);
                }
        var bones = new int[points.Count * PhysicsBody.Influences];
        var weights = new float[points.Count * PhysicsBody.Influences];
        Array.Fill(bones, -1);
        for (var i = 0; i < points.Count; i++)
        {
            bones[i * PhysicsBody.Influences] = owners[i];
            weights[i * PhysicsBody.Influences] = 1;
        }
        var pose = PhysicsBody.RestPose(skeleton);
        var body = PhysicsBody.Of(["j_kosi", "j_mune_l"], points.ToArray(), bones, weights, Enumerable.Repeat(1e-4f, points.Count).ToArray(), pose,
            normals.ToArray());
        var chain = Chain("j_mune_l");
        chain.Nodes[0].ConeAxisOffset = new Vector3(0, 0, 0.1f);
        chain.Nodes[0].ConeMaxAngle = MathF.PI;
        return (body, skeleton, new PhybSimulator { Group = PhysicsAreas.ChestGroup, Chains = [chain] });
    }

    private static void CheckClearance()
    {
        var (body, skeleton, simulator) = Wall(0.02f);
        var torso = PhysicsClearance.TorsoOf(body, skeleton);
        var clear = PhysicsClearance.Degrees(body, skeleton, torso, simulator, 0);
        // The corner of the breast nearest the wall, 6 cm from the bone, sinks 1.5 cm past it at about 40 degrees; 3% of it a few degrees later.
        Require(torso.Count == 41 * 41 && clear is > 30 and < 60, $"physics clearance: a breast swings until its skin sinks into the torso ({clear}°)");
        var (far, farSkeleton, farSimulator) = Wall(0.3f);
        Require(PhysicsClearance.Degrees(far, farSkeleton, PhysicsClearance.TorsoOf(far, farSkeleton), farSimulator, 0) == 180,
            "physics clearance: a part far from the torso needs no limit");
        simulator.Chains[0].Nodes[0].ConeAxisOffset = Vector3.Transform(new Vector3(0, 0, 0.1f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.2f));
        Require(PhysicsClearance.Degrees(body, skeleton, torso, simulator, 0) >= 71,
            "physics clearance: the limit always holds the bone's rest place, however far its cone's axis points from it");
    }

    private static void CheckFit()
    {
        var (body, skeleton, simulator) = Wall(0.02f);
        PhysicsKnobs.Write(simulator, 0, PhysicsKnob.Swing, 180);
        var file = new PhybFile { Simulators = [simulator] };
        var items = PhysicsFit.Measure(file, body, skeleton).Items;
        var swing = items.SingleOrDefault(item => item.Target == PhysicsFitTarget.Swing);
        Require(swing is { Current: 180, Changes: true } && swing.Fitted < 60,
            "physics fit: a swing that would sink into the body gets a lower limit");
        // With everything else the fit found, such as the tip's place: fitting again settles.
        PhysicsFit.Apply(file, items.Where(item => item.Changes));
        Require(MathF.Abs(PhysicsKnobs.Read(simulator, 0, PhysicsKnob.Swing)!.Value - swing!.Fitted) < 0.01f &&
                !PhysicsFit.Measure(file, body, skeleton).Items.Any(item => item.Changes),
            "physics fit: applied, the swing fits, and fitting again finds nothing to change");
        PhysicsKnobs.Write(simulator, 0, PhysicsKnob.Swing, 20);
        Require(!PhysicsFit.Measure(file, body, skeleton).Items.Any(item => item.Target == PhysicsFitTarget.Swing),
            "physics fit: a swing already inside the clear range stays");
    }

    private static void CheckSliderScale()
    {
        Require(SliderScale.Ratio(0.05f, 0, 0.1f, false, 3) == 0.5f && SliderScale.Ratio(-1, 0, 0.1f, false, 3) == 0 &&
                MathF.Abs(SliderScale.Ratio(0.001f, 0, 0.2f, true, 4) - MathF.Log(10) / MathF.Log(2000)) < 1e-5f &&
                SliderScale.Ratio(0, 0, 0.2f, true, 4) == 0 && SliderScale.Ratio(0.2f, 0, 0.2f, true, 4) == 1 &&
                MathF.Abs(SliderScale.X(0.5f, 0, 100, 10) - 50) < 1e-4f,
            "slider scale: values sit along a slider where ImGui puts its grab, logarithmic sliders from their smallest shown value");
        Require(SliderScale.Decimals("%.4f") == 4 && SliderScale.Decimals("%.0f°") == 0 && SliderScale.Decimals("%d") == 3 &&
                SliderScale.Text(0.075f, "%.4f") == "0.0750" && SliderScale.Text(14.5f, "%.1f cm") == "14.5 cm" && SliderScale.Text(8, "%.0f°") == "8°",
            "slider scale: values read as their slider shows them");
    }

    /// <summary> The game's own bust ranges and skin normals. </summary>
    public static void CheckGame(Lumina.GameData game)
    {
        if (game.GetFile(PhysicsPose.CharaMakeFile)?.Data is not { } cmp)
        {
            Console.WriteLine("[SKIP] physics pose: the game's human.cmp isn't readable");
            return;
        }
        Require(Vector3.Distance(PhysicsPose.Bust(cmp, 7, true, 1, 50), Vector3.One) < 1e-4f &&
                Vector3.Distance(PhysicsPose.Bust(cmp, 7, true, 1, 100), new Vector3(1.08f, 1.2f, 1.184f)) < 1e-4f &&
                Vector3.Distance(PhysicsPose.Bust(cmp, 5, true, 1, 0), Vector3.One) < 1e-4f,
            "physics pose: in the game's human.cmp a Miqo'te's bust at 50 keeps its size and at 100 grows to (1.08, 1.2, 1.184); Lalafell never shrink");

        var bytes = game.GetFile("chara/equipment/e0000/model/c0201e0000_top.mdl")!.Data;
        var mesh = InstantEdit.Services.Previews.ModelMeshReader.Read(bytes, skin: true);
        var still = mesh.Bones.ToDictionary(bone => bone, _ => PhysicsFrame.Rest(Vector3.Zero, Quaternion.Identity), StringComparer.Ordinal);
        var body = PhysicsBody.Measure([new PhysicsBodyModel("top", bytes, null, null, 0)], still, still);
        // The torso's skin (not the arms') faces away from the spine, a vertical line through the body's middle.
        var torso = Enumerable.Range(0, body.Points.Length).Where(i => MathF.Abs(body.Points[i].X) < 0.12f).ToList();
        var middle = torso.Aggregate(Vector3.Zero, (sum, i) => sum + body.Points[i]) / torso.Count;
        var outward = torso.Count(i =>
        {
            var away = body.Points[i] - middle;
            away.Y = 0;
            return Vector3.Dot(body.Normals[i], away) > 0;
        });
        Require(body.Normals.All(normal => normal == Vector3.Zero || MathF.Abs(normal.Length() - 1) < 1e-3f) && outward > torso.Count * 0.8f,
            $"physics body: the game's smallclothes top faces outward at {outward * 100 / Math.Max(1, torso.Count)}% of its torso's points");
    }
}
