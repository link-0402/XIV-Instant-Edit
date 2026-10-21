using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Physics;
using InstantEdit.Services.Skeletons;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Custom hair physics: the rig's chains made into simulators by kind, nodes and cones from the
/// vanilla presets, and collision shapes copied from the race's h0182 physics.
/// </summary>
internal static class HairPhysicsScenarios
{
    public static void Run()
    {
        PathsAndPresets();
        GroupsAndOrder();
        NodesFollowTheRig();
        LongChainsBendLess();
        CollisionsPerKind();
        MissingDonor();
        NoChains();
        BytesRoundTrip();
        TunedChainsAreKept();
        MovedBoneKeepsTuning();
        NewRefsGetFreshCollisionFlags();
        KindChangeTakesPreset();
        ShapesKeepTuning();
        CoincidentBonesGetAnAxis();
    }

    // j_kao turned 90 degrees about Y, so parent-space axes differ from model space.
    private static readonly Quaternion KaoRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);

    private static readonly SkeletonBone Kao = new("j_kao", -1, 0,
        new BoneTransform(new(0, 1.5f, 0), KaoRotation, Vector3.One));

    private static readonly string[] Named =
    [
        "backhair_nplane", "hair_capsule_sako_l", "hair_capsule_sako_r",
        "sidehair_nplane", "hair_sphere_kao_l", "sidehair_capsule_sako_l",
        "hair_sphere_kao_r", "sidehair_capsule_sako_r",
    ];

    private static float[] Model(Vector3 position, Quaternion rotation)
        => [position.X, position.Y, position.Z, rotation.X, rotation.Y, rotation.Z, rotation.W, 1, 1, 1];

    /// <summary>A chain of <paramref name="count"/> bones named j_ex_hair_NN_a onwards, from <paramref name="start"/> in steps of <paramref name="step"/>.</summary>
    private static (List<HairSkeletonBoneRequest> Bones, HairSkeletonChainRequest Chain) Chain(
        int number, string kind, Vector3 start, Vector3 step, int count, float tipLength)
    {
        var bones = new List<HairSkeletonBoneRequest>();
        var names = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var name = $"j_ex_hair_{number:D2}_{(char)('a' + i)}";
            var rotation = Quaternion.CreateFromYawPitchRoll(0.3f * (i + 1), -0.4f + i * 0.5f, 0.2f);
            bones.Add(new() { Name = name, Parent = i == 0 ? "j_kao" : names[^1], Model = Model(start + step * i, rotation) });
            names.Add(name);
        }
        var tip = start + step * (count - 1) + Vector3.Normalize(step) * tipLength;
        return (bones, new() { Kind = kind, Bones = names, Tip = [tip.X, tip.Y, tip.Z] });
    }

    private static HairSkeletonRequest Rig(params (List<HairSkeletonBoneRequest> Bones, HairSkeletonChainRequest Chain)[] chains) => new()
    {
        Race = "c0801",
        Bones = chains.SelectMany(chain => chain.Bones).ToList(),
        Chains = chains.Select(chain => chain.Chain).ToList(),
    };

    private static readonly Vector3 Down = new(0, -0.06f, 0);

    /// <summary>Bangs, a side chain at X = +0.06, one at X = -0.06 and a back chain, two bones each.</summary>
    private static HairSkeletonRequest FourKinds() => Rig(
        Chain(1, "bangs", new(0, 1.62f, 0.08f), Down, 2, 0.06f),
        Chain(2, "side", new(0.06f, 1.6f, 0), Down, 2, 0.06f),
        Chain(3, "side", new(-0.06f, 1.6f, 0), Down, 2, 0.06f),
        Chain(4, "back", new(0, 1.6f, -0.08f), Down, 2, 0.06f));

    /// <summary>Every named shape and one the hair doesn't use, each with its own values.</summary>
    private static PhybFile Donor(params string[] without)
    {
        var donor = new PhybFile();
        var radius = 0.01f;
        foreach (var name in Named.Append("kubi_extra").Where(name => !without.Contains(name)))
        {
            radius += 0.01f;
            if (name.Contains("nplane"))
                donor.Collision.NormalPlanes.Add(new() { Name = PhybName.From(name), Bone = PhybName.From("j_kao"), Normal = Vector3.UnitZ, Thickness = radius });
            else if (name.Contains("capsule"))
                donor.Collision.Capsules.Add(new()
                {
                    Name = PhybName.From(name), StartBone = PhybName.From("j_sebo_c"), EndBone = PhybName.From("j_kubi"),
                    EndOffset = Vector3.UnitY, Radius = radius,
                });
            else
                donor.Collision.Spheres.Add(new() { Name = PhybName.From(name), Bone = PhybName.From("j_kao"), Thickness = radius });
        }
        return donor;
    }

    private static PhybFile Build(HairSkeletonRequest rig, PhybFile? donor, List<string>? notes = null, PhybFile? previous = null,
        IReadOnlyDictionary<string, string>? kinds = null)
        => HairPhysicsBuilder.Build(rig, HairSkeletonLayout.Bones(rig, Kao), donor, previous, notes ?? [], kinds);

    /// <summary>The file as the next export reads it from the mod.</summary>
    private static PhybFile Reread(PhybFile file) => PhybFile.Parse(file.ToBytes());

    private static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-5f;

    private static bool Near(float a, float b) => MathF.Abs(a - b) < 1e-5f;

    private static float Radians(float degrees) => degrees * MathF.PI / 180f;

    private static void PathsAndPresets()
    {
        Require(HairPhysicsBuilder.PhysicsPath(801, 4321) == "chara/human/c0801/skeleton/hair/h4321/phy_c0801h4321.phyb" &&
                HairPhysicsPresets.DonorPath(1401) == "chara/human/c1401/skeleton/hair/h0182/phy_c1401h0182.phyb",
            "Hair physics: the file and donor paths");
        Require(HairPhysicsPresets.For("ponytail") is { Dampening: 0.04f, ReferenceLength: 0.40f, Root.Radius: 0.02f, Tip.GravityScale: 0.65f },
            "Hair physics: the ponytail preset is the table's");
        Require(HairPhysicsPresets.Group("bangs", true) == 3 && HairPhysicsPresets.Group("ponytail", false) == 23 &&
                HairPhysicsPresets.Group("side", true) == 24 && HairPhysicsPresets.Group("side", false) == 25,
            "Hair physics: groups by kind and side");
        Require(HairPhysicsPresets.Shapes("bangs", true).Count == 0 &&
                HairPhysicsPresets.Shapes("side", false).SequenceEqual(["sidehair_nplane", "hair_sphere_kao_r", "sidehair_capsule_sako_r"]),
            "Hair physics: shapes by kind and side");
        Reject(() => HairPhysicsPresets.For("tail"), "Hair physics: an unknown kind has no preset");
    }

    private static void GroupsAndOrder()
    {
        var file = Build(FourKinds(), Donor());
        Require(file.Simulators.Select(s => (int)s.Group).SequenceEqual([3, 23, 24, 25]) &&
                file.Simulators.All(s => s.Chains.Count == 1),
            "Hair physics: bangs, back, left and right side simulators in that order, one chain each");
        Require(file.Simulators[2].Chains[0].Nodes[0].Bone.Text == "j_ex_hair_02_a" &&
                file.Simulators[3].Chains[0].Nodes[0].Bone.Text == "j_ex_hair_03_a",
            "Hair physics: the side chain at +X is left, the one at -X right");
        var defaults = new PhybSimulator();
        Require(file.Simulators.All(s => s.Gravity == defaults.Gravity && s.Flags == defaults.Flags &&
                                         s.ConstraintLoop == defaults.ConstraintLoop && s.CollisionLoop == defaults.CollisionLoop),
            "Hair physics: simulators keep the vanilla scalars");
    }

    private static void NodesFollowTheRig()
    {
        var rig = Rig(Chain(1, "back", new(0, 1.6f, -0.08f), Down, 3, 0.06f));
        var bones = HairSkeletonLayout.Bones(rig, Kao);
        var model = HairSkeletonLayout.ModelSpace(bones);
        var chain = HairPhysicsBuilder.Build(rig, bones, Donor(), null, new List<string>()).Simulators.Single().Chains.Single();
        Require(chain.Nodes.Select(n => n.Bone.Text).SequenceEqual(["j_ex_hair_01_a", "j_ex_hair_01_b", "j_ex_hair_01_c", ""]),
            "Hair physics: one node per bone, then an unnamed tip");
        Require(Near(chain.Nodes[0].ConeAxisOffset, Vector3.Transform(-Vector3.UnitY, Quaternion.Conjugate(KaoRotation))),
            "Hair physics: the root's axis points to the next bone in j_kao's space");
        Require(Near(chain.Nodes[1].ConeAxisOffset, Vector3.Transform(-Vector3.UnitY, Quaternion.Conjugate(model[1].Rotation))),
            "Hair physics: a middle node's axis points to the next bone in its parent's space");
        Require(Near(chain.Nodes[2].ConeAxisOffset, Vector3.Transform(-Vector3.UnitY, Quaternion.Conjugate(model[2].Rotation))),
            "Hair physics: the last bone's axis points to the tip");
        Require(Near(chain.LastBoneOffset.Length(), 0.06f) &&
                Near(chain.LastBoneOffset, Vector3.Transform(Down, Quaternion.Conjugate(model[3].Rotation))),
            "Hair physics: the tip is 6 cm off the last bone, in its space");
        Require(chain.Nodes[3].ConeAxisOffset == Vector3.UnitX && chain.Nodes[3].ConeMaxAngle == MathF.PI,
            "Hair physics: the tip's axis is +X with a 180 degree cone");
        Require(chain.Dampening == 0.05f && Near(chain.Nodes[0].ConeMaxAngle, Radians(40)) && chain.Nodes[1].Radius == 0.02f &&
                chain.MaxSpeed == 4 && chain.Friction == 0 && chain.CollisionDampening == 0 && chain.RepulsionStrength == 1 && chain.Type == 0,
            "Hair physics: the back preset's values");
    }

    private static void LongChainsBendLess()
    {
        var steep = Build(Rig(Chain(1, "back", new(0, 1.6f, -0.08f), new(0, -0.2f, 0), 2, 0.4f)), Donor());
        var nodes = steep.Simulators[0].Chains[0].Nodes;
        Require(Near(nodes[0].ConeMaxAngle, Radians(20)) && Near(nodes[1].ConeMaxAngle, Radians(20)) && nodes[2].ConeMaxAngle == MathF.PI,
            "Hair physics: a 0.6 m back chain has half the cones (20 degrees)");
        var shortChain = Build(Rig(Chain(1, "back", new(0, 1.6f, -0.08f), new(0, -0.1f, 0), 2, 0.1f)), Donor());
        Require(Near(shortChain.Simulators[0].Chains[0].Nodes[0].ConeMaxAngle, Radians(40)),
            "Hair physics: a 0.2 m back chain keeps 40 degrees");
    }

    private static void CollisionsPerKind()
    {
        var donor = Donor();
        var file = Build(FourKinds(), donor);
        var left = file.Simulators[2];
        Require(left.Collisions.Select(c => c.Name.Text).SequenceEqual(["sidehair_nplane", "hair_sphere_kao_l", "sidehair_capsule_sako_l"]) &&
                left.Collisions.All(c => c.Type == PhybCollisionType.Outside),
            "Hair physics: the left side collides with its own three shapes, from outside");
        Require(left.Chains[0].Nodes.All(n => n.CollisionFlags == 7 && n.ContinuousCollisionFlags == 0),
            "Hair physics: every left side node collides with all three");
        Require(file.Simulators[0].Collisions.Count == 0 && file.Simulators[0].Chains[0].Nodes.All(n => n.CollisionFlags == 0),
            "Hair physics: bangs collide with nothing");
        var names = file.Collision.Names.Select(n => n.Text).ToList();
        Require(names.Count == Named.Length && names.ToHashSet().SetEquals(Named) && !names.Contains("kubi_extra"),
            "Hair physics: the file holds each named shape once and nothing else");
        Require(file.Collision.Capsules.All(c => donor.Collision.Capsules.Any(d => d.Name.Equals(c.Name) && d.Radius == c.Radius && !ReferenceEquals(d, c))),
            "Hair physics: shapes are copies with the donor's values");
        Require(file.DataType == 3 && file.Version == PhybFile.CurrentVersion && file.Extension is null,
            "Hair physics: a file with shapes has data type 3");
    }

    private static void MissingDonor()
    {
        var notes = new List<string>();
        var file = Build(FourKinds(), null, notes);
        Require(file.Simulators.All(s => s.Collisions.Count == 0 && s.Chains.All(c => c.Nodes.All(n => n.CollisionFlags == 0))) &&
                file.Collision.IsEmpty && file.DataType == 2 && notes.Count == 0,
            "Hair physics: without a donor nothing collides, data type 2, no note");
        file = Build(FourKinds(), Donor("hair_sphere_kao_l"), notes);
        var left = file.Simulators[2];
        Require(left.Collisions.Select(c => c.Name.Text).SequenceEqual(["sidehair_nplane", "sidehair_capsule_sako_l"]) &&
                left.Chains[0].Nodes.All(n => n.CollisionFlags == 3),
            "Hair physics: a shape the donor lacks is left out with its ref");
        Require(notes is [var note] && note.Contains("hair_sphere_kao_l", StringComparison.Ordinal),
            "Hair physics: the missing shape is noted once");
    }

    private static void NoChains()
    {
        var rig = Rig(Chain(1, "back", new(0, 1.6f, -0.08f), Down, 2, 0.06f));
        rig.Chains = null;
        var none = Build(rig, Donor());
        rig.Chains = [];
        var empty = Build(rig, Donor());
        Require(none is { Version: 0, DataType: 0, Simulators.Count: 0 } && empty is { Version: 0, DataType: 0 },
            "Hair physics: a rig without chains turns physics off");
    }

    private static void BytesRoundTrip()
    {
        var file = Build(FourKinds(), Donor());
        var parsed = PhybFile.Parse(file.ToBytes());
        static IEnumerable<string> Names(PhybFile f) => f.Simulators.SelectMany(s => s.Chains).SelectMany(c => c.Nodes).Select(n => n.Bone.Text);
        static IEnumerable<uint> Flags(PhybFile f) => f.Simulators.SelectMany(s => s.Chains).SelectMany(c => c.Nodes).Select(n => n.CollisionFlags);
        Require(parsed.Simulators.Count == 4 && parsed.Simulators.Select(s => s.Group).SequenceEqual(file.Simulators.Select(s => s.Group)) &&
                Names(parsed).SequenceEqual(Names(file)) && Flags(parsed).SequenceEqual(Flags(file)) && parsed.DataType == 3,
            "Hair physics: the file reads back with its simulators, nodes and flags");
    }

    private static void TunedChainsAreKept()
    {
        var back = Chain(4, "back", new(0, 1.6f, -0.08f), Down, 2, 0.06f);
        var first = Build(Rig(back), Donor());
        var tuned = first.Simulators.Single();
        tuned.Chains[0].Dampening = 0.3f;
        tuned.Chains[0].Nodes[^1].Radius = 0.05f;
        tuned.Gravity = new(0, -5, 0);
        tuned.Chains[0].Nodes[0].CollisionFlags = 5;
        var notes = new List<string>();
        var file = Build(Rig(back, Chain(2, "side", new(0.06f, 1.6f, 0), Down, 2, 0.06f)), Donor(), notes, Reread(first));
        var kept = file.Simulators[0];
        Require(kept.Group == 23 && kept.Chains[0].Dampening == 0.3f && kept.Chains[0].Nodes[^1].Radius == 0.05f &&
                kept.Gravity == new Vector3(0, -5, 0),
            "Hair physics: a re-export keeps the back chain's dampening, tip radius and gravity");
        Require(kept.Chains[0].Nodes[0].CollisionFlags == 5,
            "Hair physics: collision bits are kept when the shapes are the same");
        var added = file.Simulators[1];
        Require(added.Group == 24 && added.Chains[0].Dampening == 0.1f && added.Chains[0].Nodes[0].Radius == 0.01f &&
                added.Gravity == new PhybSimulator().Gravity,
            "Hair physics: a new side chain gets its preset");
        Require(notes is ["Kept your physics settings for 1 of 2 chains."],
            "Hair physics: the re-export notes how many chains kept their settings");
    }

    private static void MovedBoneKeepsTuning()
    {
        var back = Chain(4, "back", new(0, 1.6f, -0.08f), Down, 2, 0.06f);
        var first = Build(Rig(back), Donor());
        first.Simulators[0].Chains[0].Dampening = 0.3f;
        var oldAxis = first.Simulators[0].Chains[0].Nodes[0].ConeAxisOffset;
        back.Bones[1].Model![0] += 0.02f;
        var chain = Build(Rig(back), Donor(), null, Reread(first)).Simulators[0].Chains[0];
        Require(chain.Dampening == 0.3f && Vector3.Dot(chain.Nodes[0].ConeAxisOffset, oldAxis) < 0.9999f,
            "Hair physics: a moved bone keeps the chain's tuning but gets a new axis");
        var fresh = Build(Rig(back), Donor()).Simulators[0].Chains[0];
        Require(Near(chain.LastBoneOffset, fresh.LastBoneOffset),
            "Hair physics: a moved bone gets the rig's tip offset");
    }

    private static void NewRefsGetFreshCollisionFlags()
    {
        var rig = Rig(Chain(4, "back", new(0, 1.6f, -0.08f), Down, 2, 0.06f));
        var first = Build(rig, null);
        first.Simulators[0].Chains[0].Dampening = 0.3f;
        var chain = Build(rig, Donor(), null, Reread(first)).Simulators[0].Chains[0];
        Require(chain.Dampening == 0.3f && chain.Nodes.All(n => n.CollisionFlags == 7 && n.ContinuousCollisionFlags == 0),
            "Hair physics: a re-export with new shapes keeps the tuning but collides with all three");
    }

    private static void KindChangeTakesPreset()
    {
        var hair = Chain(1, "bangs", new(0.06f, 1.6f, 0), Down, 2, 0.06f);
        var first = Build(Rig(hair), Donor());
        first.Simulators[0].Chains[0].Dampening = 0.3f;
        hair.Chain.Kind = "side";
        var notes = new List<string>();
        var file = Build(Rig(hair), Donor(), notes, Reread(first));
        Require(file.Simulators.Single() is { Group: 24 } side && side.Chains[0].Dampening == 0.1f && notes.Count == 0,
            "Hair physics: bangs made side hair get the side preset, without a note");

        // Back and ponytail share group 23, so only the recorded kinds tell them apart.
        var back = Chain(4, "back", new(0, 1.6f, -0.08f), Down, 2, 0.06f);
        var tuned = Build(Rig(back), Donor());
        tuned.Simulators[0].Chains[0].Dampening = 0.3f;
        var kinds = new Dictionary<string, string> { ["j_ex_hair_04_a"] = "back" };
        back.Chain.Kind = "ponytail";
        notes.Clear();
        var ponytail = Build(Rig(back), Donor(), notes, Reread(tuned), kinds).Simulators.Single();
        Require(ponytail.Group == 23 && ponytail.Chains[0].Dampening == 0.04f && notes.Count == 0,
            "Hair physics: a back chain made a ponytail gets the ponytail preset, without a note");
        Require(Build(Rig(back), Donor(), null, Reread(tuned)).Simulators[0].Chains[0].Dampening == 0.3f,
            "Hair physics: without recorded kinds a chain in the same group keeps its tuning");
        back.Chain.Kind = "back";
        Require(Build(Rig(back), Donor(), null, Reread(tuned), kinds).Simulators[0].Chains[0].Dampening == 0.3f,
            "Hair physics: a back chain recorded as back keeps its tuning");
    }

    private static void ShapesKeepTuning()
    {
        var rig = Rig(Chain(4, "back", new(0, 1.6f, -0.08f), Down, 2, 0.06f));
        var first = Build(rig, Donor());
        first.Collision.Capsules.Single(c => c.Name.Text == "hair_capsule_sako_l").Radius = 0.2f;
        var right = first.Collision.Capsules.Single(c => c.Name.Text == "hair_capsule_sako_r");
        first.Collision.Capsules.Remove(right);
        first.Collision.Spheres.Add(new() { Name = right.Name, Bone = PhybName.From("j_kao"), Thickness = 0.3f });
        var file = Build(rig, Donor(), null, Reread(first));
        Require(file.Collision.Capsules.Single(c => c.Name.Text == "hair_capsule_sako_l").Radius == 0.2f,
            "Hair physics: a re-export keeps a tuned shape");
        Require(file.Collision.Capsules.Single(c => c.Name.Text == "hair_capsule_sako_r").Radius == right.Radius &&
                file.Collision.Spheres.Count == 0,
            "Hair physics: a previous shape of another type doesn't replace the donor's");
    }

    private static void CoincidentBonesGetAnAxis()
    {
        // Validation refuses these rigs; the builder still never writes a NaN or arbitrary axis.
        // On j_kao's own position the two bones' model positions are exactly equal; 0.1 µm apart they are not.
        foreach (var (offset, label) in new[] { (0f, "on"), (1e-7f, "0.1 µm from") })
        {
            var start = Kao.Reference.Position;
            var rig = Rig(Chain(4, "back", start, Down, 2, 0.06f));
            rig.Bones![1].Model = Model(start + new Vector3(0, offset, 0), Quaternion.Identity);
            var nodes = Build(rig, Donor()).Simulators[0].Chains[0].Nodes;
            Require(nodes[0].ConeAxisOffset == Vector3.UnitX && nodes.All(n => float.IsFinite(n.ConeAxisOffset.Length())),
                $"Hair physics: a bone {label} the one before gets the +X axis");
        }
    }
}
