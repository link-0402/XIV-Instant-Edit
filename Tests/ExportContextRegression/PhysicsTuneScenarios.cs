using System.Buffers.Binary;
using System.Numerics;
using InstantEdit.Services.Physics;
using InstantEdit.Ui;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Tuning the body to the body it's on: what each slider reads and writes, the ranges most presets
/// use, intensity levels from presets, breast size and the firmness that suits it, the still pose a
/// body is measured in (Customize+ and the bust size, nothing else), and how far a part can swing
/// before it sinks into the torso.
/// </summary>
internal static class PhysicsTuneScenarios
{
    public static void Run()
    {
        CheckKnobs();
        CheckRanges();
        CheckLevels();
        CheckSize();
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
    }

    private static readonly Dictionary<PhysicsKnob, (float Min, float Max)> Sliders = new()
    {
        [PhysicsKnob.Firmness] = (0, 0.2f), [PhysicsKnob.Damping] = (0, 1), [PhysicsKnob.Swing] = (0, 180), [PhysicsKnob.Weight] = (0, 2),
        [PhysicsKnob.Follow] = (0, 1), [PhysicsKnob.Length] = (0.5f, 50), [PhysicsKnob.MaxSpeed] = (0, 10), [PhysicsKnob.Wind] = (0, 0.2f),
        [PhysicsKnob.Size] = (0, 20), [PhysicsKnob.Friction] = (0, 1), [PhysicsKnob.BumpDamping] = (0, 1), [PhysicsKnob.PushBack] = (0, 2),
    };

    private static void CheckRanges()
    {
        var bands = PhysicsRanges.Areas.SelectMany(area => Enum.GetValues<PhysicsKnob>().Select(knob => (area, knob, PhysicsRanges.Band(area, knob))))
            .Where(entry => entry.Item3 is not null).ToList();
        Require(bands.Count > 100 && bands.All(entry => entry.Item3!.Value.Low <= entry.Item3.Value.High &&
                                                        entry.Item3.Value.Low >= Sliders[entry.knob].Min && entry.Item3.Value.High <= Sliders[entry.knob].Max),
            "physics ranges: every range lies within its slider, low before high");
        Require(PhysicsRanges.Band("chest", PhysicsKnob.Firmness) is { } firmness && firmness.Contains(0.035f) && firmness.Contains(0.075f) &&
                PhysicsRanges.Band("butt-ivcs", PhysicsKnob.Damping) is not null && PhysicsRanges.Band("ears", PhysicsKnob.Damping) is null &&
                PhysicsLevels.For("chest").All(level => PhysicsRanges.Band("chest", PhysicsKnob.Damping)!.Value.Contains(level.Damping)),
            "physics ranges: the chest's range holds the game's and the presets' firmness and every chest level's damping, and areas few presets have get none");
    }

    /// <summary> A skeleton with every body area's bones, each a child of the hips or a thigh. </summary>
    private static PhysicsSkeleton AreaSkeleton()
    {
        var bones = new List<PhysicsBone>
        {
            new("n_root", "", Vector3.Zero, Quaternion.Identity),
            new("j_kosi", "n_root", new Vector3(0, 1, 0), Quaternion.Identity),
            new("j_sebo_a", "j_kosi", new Vector3(0, 0.1f, 0), Quaternion.Identity),
            new("j_sebo_b", "j_sebo_a", new Vector3(0, 0.1f, 0), Quaternion.Identity),
            new("j_asi_a_l", "j_kosi", new Vector3(0.08f, -0.1f, 0), Quaternion.Identity),
            new("j_asi_a_r", "j_kosi", new Vector3(-0.08f, -0.1f, 0), Quaternion.Identity),
        };
        foreach (var kind in PhysicsAreas.Kinds.Where(kind => PhysicsLevels.For(kind.Key).Count > 0))
            foreach (var bone in kind.Bones)
                bones.Add(new PhysicsBone(bone, bone.Contains("daitai") ? bone.EndsWith("_r") ? "j_asi_a_r" : "j_asi_a_l" : bone.Contains("mune") ? "j_sebo_b" : "j_kosi",
                    new Vector3(0.05f, 0, 0.05f), Quaternion.Identity));
        return new PhysicsSkeleton(bones, []);
    }

    private static void CheckLevels()
    {
        var skeleton = AreaSkeleton();
        var every = true;
        foreach (var kind in PhysicsAreas.Kinds.Where(kind => PhysicsLevels.For(kind.Key).Count > 0))
        {
            var simulator = PhysicsTemplates.Build(kind, skeleton)!;
            var file = new PhybFile { Simulators = [simulator] };
            var levels = PhysicsLevels.For(kind.Key);
            foreach (var level in levels)
            {
                PhysicsLevels.Apply(file, Enumerable.Range(0, simulator.Chains.Count).Select(c => (0, c)), level, kind.Key, skeleton);
                every &= Enumerable.Range(0, simulator.Chains.Count).All(c => ReferenceEquals(PhysicsLevels.Match(simulator, c, kind.Key), level));
            }
        }
        Require(every, "physics levels: every level of every area, set on the area, reads back as that level");

        var butt = PhysicsTemplates.Build(PhysicsAreas.Find("butt-ivcs")!, skeleton)!;
        var buttFile = new PhybFile { Simulators = [butt] };
        var firm = PhysicsLevels.For("butt-ivcs")[0];
        var soft = PhysicsLevels.For("butt-ivcs")[2];
        PhysicsLevels.Apply(buttFile, [(0, 0)], soft, "butt-ivcs", skeleton);
        Require(firm.Damping == soft.Damping && firm.Swing == soft.Swing && ReferenceEquals(PhysicsLevels.Match(butt, 0, "butt-ivcs"), soft),
            "physics levels: levels that differ only in firmness, like Rue+'s Jiggle and But more, are told apart by it");

        var chest = PhysicsTemplates.Build(PhysicsAreas.Find("chest")!, skeleton)!;
        var chestFile = new PhybFile { Simulators = [chest] };
        var verySoft = PhysicsLevels.For("chest")[3];
        PhysicsLevels.Apply(chestFile, [(0, 0), (0, 1)], verySoft, "chest", skeleton, 0.5f);
        Require(MathF.Abs(PhysicsKnobs.Read(chest, 0, PhysicsKnob.Firmness)!.Value - verySoft.Firmness!.Value * 0.5f) < 1e-6f &&
                ReferenceEquals(PhysicsLevels.Match(chest, 1, "chest"), verySoft),
            "physics levels: a chest level takes the firmness for the measured size and still reads as its level");

        var yas = PhysicsTemplates.Build(PhysicsAreas.Find("butt-yas")!, skeleton)!;
        var yasFile = new PhybFile { Simulators = [yas] };
        PhysicsLevels.Apply(yasFile, [(0, 0)], PhysicsLevels.For("butt-yas")[3], "butt-yas", skeleton);
        var bare = new PhybSimulator { Chains = [Chain("iv_shiri_l", 0.112f)] };
        PhysicsLevels.Apply(new PhybFile { Simulators = [bare] }, [(0, 0)], firm, "butt-ivcs", skeleton);
        Require(PhysicsKnobs.Read(yas, 0, PhysicsKnob.Firmness) == 0 && bare.Attracts.Count == 1 && bare.Attracts[0].Bone.Text == "j_kosi" &&
                bare.Attracts[0].Stiffness == firm.Firmness,
            "physics levels: a level without a spring turns the spring off, and one with a spring adds it at the rest place when there is none");
    }

    private static void CheckSize()
    {
        Require(MathF.Abs(PhysicsSize.ChestFirmness(0.988f) - 0.045f) < 1e-6f && MathF.Abs(PhysicsSize.ChestFirmness(1.191f) - 0.035f) < 1e-6f &&
                MathF.Abs(PhysicsSize.ChestFirmness(1.279f) - 0.025f) < 1e-6f && MathF.Abs(PhysicsSize.ChestFirmness(1.674f) - 0.015f) < 1e-6f &&
                PhysicsSize.ChestFirmness(0.5f) == 0.045f && PhysicsSize.ChestFirmness(3f) == 0.015f &&
                MathF.Abs(PhysicsSize.ChestFirmness((1.279f + 1.674f) / 2) - 0.02f) < 1e-5f && MathF.Abs(PhysicsSize.ChestScale(PhysicsSize.MediumRatio) - 1) < 1e-6f,
            "physics size: breasts get the firmness size-based presets give their size, in a line between the sizes and held at the ends");
        Require(PhysicsSize.Label(1f) == "small" && PhysicsSize.Label(1.2f) == "medium" && PhysicsSize.Label(1.3f) == "large" &&
                PhysicsSize.Label(1.6f) == "extra large" && MathF.Abs(PhysicsSize.Growth(new Vector3(1.05f, 1.45f, 1.3f)) - 1.2563f) < 1e-3f &&
                PhysicsSize.HasBreasts(801) && !PhysicsSize.HasBreasts(701),
            "physics size: scales grow a breast by their volume's cube root, and only women's bodies are sized");
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
        simulator.Attracts.Add(new PhybAttract { Bone = PhybName.From("j_kosi"), Chain = 0, Node = 1, Stiffness = 0.1f });
        var file = new PhybFile { Simulators = [simulator] };
        PhysicsLevels.Apply(file, [(0, 0)], PhysicsLevels.For("chest")[3], "chest", skeleton);
        var result = PhysicsFit.Measure(file, body, skeleton, new PhysicsSizeMeasure(1.674f, 1.674f, 1));
        var firmness = result.Items.SingleOrDefault(item => item.Target == PhysicsFitTarget.Firmness);
        var swing = result.Items.SingleOrDefault(item => item.Target == PhysicsFitTarget.Swing);
        Require(firmness is { Changes: true } && MathF.Abs(firmness.Fitted - 0.015f) < 1e-4f && swing is { Current: 180, Changes: true } && swing.Fitted < 60,
            "physics fit: a chest on a level gets the firmness for its size, and a swing that would sink into the body a lower limit");
        PhysicsFit.Apply(file, [firmness!, swing!]);
        var again = PhysicsFit.Measure(file, body, skeleton, new PhysicsSizeMeasure(1.674f, 1.674f, 1));
        Require(MathF.Abs(PhysicsKnobs.Read(simulator, 0, PhysicsKnob.Firmness)!.Value - 0.015f) < 1e-4f &&
                MathF.Abs(PhysicsKnobs.Read(simulator, 0, PhysicsKnob.Swing)!.Value - swing!.Fitted) < 0.01f &&
                !again.Items.Any(item => item.Changes) && again.Items.Any(item => item.Target == PhysicsFitTarget.Firmness) &&
                ReferenceEquals(PhysicsLevels.Match(simulator, 0, "chest"), PhysicsLevels.For("chest")[3]),
            "physics fit: applied, the firmness and swing fit, the chest is still on its level, and fitting again finds nothing to change");
        PhysicsKnobs.Write(simulator, 0, PhysicsKnob.Swing, 20);
        Require(!PhysicsFit.Measure(file, body, skeleton).Items.Any(item => item.Target == PhysicsFitTarget.Swing) &&
                !PhysicsFit.Measure(file, body, skeleton).Items.Any(item => item.Target == PhysicsFitTarget.Firmness),
            "physics fit: a swing already inside the clear range stays, and without a measured size firmness isn't fitted");
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
