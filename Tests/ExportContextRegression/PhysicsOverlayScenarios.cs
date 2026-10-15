using System.Numerics;
using InstantEdit.Services.Physics;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// What the builder draws on the character: a chain's bones and tip, the balls on its moving points,
/// the bone's swing cone, where its spring pulls, and the collision shapes lit when the chain bumps
/// into them, each lit by the control under the mouse.
/// </summary>
internal static class PhysicsOverlayScenarios
{
    public static void Run()
    {
        CheckChain();
        CheckShapes();
        CheckMissing();
    }

    /// <summary> A chest bone 10 cm up a spine bone, pointing along +X, with a 14 cm tip and a spring on the spine. </summary>
    private static (PhysicsSkeleton Skeleton, PhybFile File, Dictionary<string, PhysicsFrame> Frames) Chest()
    {
        var skeleton = new PhysicsSkeleton(
        [
            new PhysicsBone("n_root", "", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("j_sebo_b", "n_root", new Vector3(0, 1, 0), Quaternion.Identity),
            new PhysicsBone("j_mune_l", "j_sebo_b", new Vector3(0.05f, 0.1f, 0), Quaternion.Identity),
            new PhysicsBone("j_asi_a_l", "n_root", new Vector3(0.1f, 0.8f, 0), Quaternion.Identity),
            new PhysicsBone("j_asi_b_l", "j_asi_a_l", new Vector3(0, -0.4f, 0), Quaternion.Identity),
        ]);
        var chain = new PhybChain
        {
            LastBoneOffset = new Vector3(0.14f, 0, 0),
            Nodes =
            [
                new PhybNode { Bone = PhybName.From("j_mune_l"), ConeMaxAngle = PhysicsTemplates.Radians(30), ConeAxisOffset = new Vector3(0.1f, 0, 0) },
                new PhybNode { Radius = 0.04f, ConeMaxAngle = MathF.PI, CollisionFlags = 1 },
            ],
        };
        var simulator = new PhybSimulator { Chains = [chain] };
        simulator.Attracts.Add(new PhybAttract { Bone = PhybName.From("j_sebo_b"), Offset = new Vector3(0.05f + 0.14f * 1.34f, 0.1f, 0), Chain = 0, Node = 1, Stiffness = 0.05f });
        simulator.Collisions.Add(new PhybCollisionRef { Name = PhybName.From("thigh_l"), Type = PhybCollisionType.Outside });
        var file = new PhybFile { Simulators = [simulator] };
        file.Collision.Capsules.Add(new PhybCapsule { Name = PhybName.From("thigh_l"), StartBone = PhybName.From("j_asi_a_l"), EndBone = PhybName.From("j_asi_b_l"), Radius = 0.06f });
        file.Collision.Spheres.Add(new PhybSphere { Name = PhybName.From("hand_r"), Bone = PhybName.From("n_root"), Offset = new Vector3(0.3f, 0.9f, 0), Thickness = 0.03f });
        return (skeleton, file, PhysicsBody.RestPose(skeleton));
    }

    private static void CheckChain()
    {
        var (skeleton, file, frames) = Chest();
        PhysicsFrame? Frame(string bone) => frames.TryGetValue(bone, out var frame) ? frame : null;
        var shapes = PhysicsOverlayModel.Build(file, [(0, 0)], false, PhysicsOverlayHover.None, skeleton, Frame);
        var bone = frames["j_mune_l"].Position;
        var tip = bone + new Vector3(0.14f, 0, 0);

        var chain = shapes.Segments.Where(segment => segment.Part == PhysicsOverlayPart.Chain).ToList();
        Require(chain.Count == 1 && Vector3.Distance(chain[0].From, bone) < 1e-5f && Vector3.Distance(chain[0].To, tip) < 1e-5f &&
                shapes.Marks.Any(mark => mark.Part == PhysicsOverlayPart.Chain && Vector3.Distance(mark.At, tip) < 1e-5f && mark.Label == "14.0 cm"),
            "physics overlay: a chain runs from its bone to the tip at the chain's offset, labelled with its length");
        var ball = shapes.Circles.SingleOrDefault(circle => circle.Part == PhysicsOverlayPart.Tip);
        Require(ball is { Normal: null } && Vector3.Distance(ball.Center, tip) < 1e-5f && MathF.Abs(ball.Radius - 0.04f) < 1e-6f &&
                !shapes.Circles.Any(circle => circle.Part == PhysicsOverlayPart.Tip && Vector3.Distance(circle.Center, bone) < 1e-5f),
            "physics overlay: the tip's ball faces the camera at the tip's radius, and the bone itself has none");

        var rim = shapes.Circles.SingleOrDefault(circle => circle.Part == PhysicsOverlayPart.Cone);
        var angle = PhysicsTemplates.Radians(30);
        Require(rim is { Normal: not null } && Vector3.Distance(rim.Normal.Value, Vector3.UnitX) < 1e-5f &&
                Vector3.Distance(rim.Center, bone + new Vector3(0.14f * MathF.Cos(angle), 0, 0)) < 1e-5f && MathF.Abs(rim.Radius - 0.14f * MathF.Sin(angle)) < 1e-6f,
            "physics overlay: the swing cone's rim sits on the cone's axis as far as the tip, as wide as the swing limit");
        var lines = shapes.Segments.Where(segment => segment.Part == PhysicsOverlayPart.Cone).ToList();
        Require(lines.Count == PhysicsOverlayModel.ConeLines && lines.All(line => Vector3.Distance(line.From, bone) < 1e-5f &&
                    MathF.Abs(Vector3.Distance(line.To, rim!.Center) - rim.Radius) < 1e-5f) &&
                shapes.Marks.Any(mark => mark.Part == PhysicsOverlayPart.Cone && mark.Label == "30°"),
            "physics overlay: lines run from the bone to the rim, and the rim says the angle");

        var anchor = frames["j_sebo_b"].Place(file.Simulators[0].Attracts[0].Offset);
        var spring = shapes.Segments.SingleOrDefault(segment => segment.Part == PhysicsOverlayPart.Spring);
        Require(spring is { Dashed: true } && Vector3.Distance(spring.From, tip) < 1e-5f && Vector3.Distance(spring.To, anchor) < 1e-5f &&
                shapes.Marks.Any(mark => mark.Part == PhysicsOverlayPart.Spring && Vector3.Distance(mark.At, anchor) < 1e-5f && mark.Label == "Firmness"),
            "physics overlay: a dashed line runs from the tip to where its spring pulls, named for what the spring does");

        Require(shapes.Segments.All(segment => !segment.Lit) && shapes.Circles.All(circle => !circle.Lit) && shapes.Marks.All(mark => !mark.Lit),
            "physics overlay: nothing is lit while no control is hovered");
        var cone = PhysicsOverlayModel.Build(file, [(0, 0)], false, new PhysicsOverlayHover(PhysicsOverlayPart.Cone), skeleton, Frame);
        Require(cone.Circles.Where(circle => circle.Part == PhysicsOverlayPart.Cone).All(circle => circle.Lit) &&
                cone.Segments.Where(segment => segment.Part == PhysicsOverlayPart.Cone).All(segment => segment.Lit) &&
                cone.Segments.Where(segment => segment.Part != PhysicsOverlayPart.Cone).All(segment => !segment.Lit) &&
                cone.Circles.Where(circle => circle.Part != PhysicsOverlayPart.Cone).All(circle => !circle.Lit) && cone.Capsules.All(capsule => !capsule.Lit),
            "physics overlay: hovering the swing limit lights the cone and nothing else");
        var tipLit = PhysicsOverlayModel.Build(file, [(0, 0)], false, new PhysicsOverlayHover(PhysicsOverlayPart.Tip), skeleton, Frame);
        Require(tipLit.Circles.Single(circle => circle.Part == PhysicsOverlayPart.Tip).Lit && tipLit.Marks.Any(mark => mark is { Part: PhysicsOverlayPart.Tip, Lit: true, Label: "4.0 cm" }),
            "physics overlay: hovering the tip size lights the ball and says its size");
    }

    private static void CheckShapes()
    {
        var (skeleton, file, frames) = Chest();
        PhysicsFrame? Frame(string bone) => frames.TryGetValue(bone, out var frame) ? frame : null;
        var shapes = PhysicsOverlayModel.Build(file, [(0, 0)], false, PhysicsOverlayHover.None, skeleton, Frame);
        var thigh = shapes.Capsules.Single();
        var hand = shapes.Spheres.Single();
        Require(thigh is { Name: "thigh_l", Part: PhysicsOverlayPart.Shape, Lit: false } && Vector3.Distance(thigh.A, frames["j_asi_a_l"].Position) < 1e-5f &&
                Vector3.Distance(thigh.B, frames["j_asi_b_l"].Position) < 1e-5f && MathF.Abs(thigh.Radius - 0.06f) < 1e-6f,
            "physics overlay: a capsule the chain bumps into runs between its bones and counts as a shape");
        Require(hand is { Name: "hand_r", Part: PhysicsOverlayPart.OtherShape, Lit: false } && Vector3.Distance(hand.Center, new Vector3(0.3f, 0.9f, 0)) < 1e-5f &&
                MathF.Abs(hand.Radius - 0.03f) < 1e-6f,
            "physics overlay: a sphere the chain doesn't bump into is drawn dim at its bone's offset");

        var hovered = PhysicsOverlayModel.Build(file, [(0, 0)], false, new PhysicsOverlayHover(PhysicsOverlayPart.Shape, "hand_r"), skeleton, Frame);
        Require(hovered.Spheres.Single() is { Part: PhysicsOverlayPart.Shape, Lit: true } && hovered.Capsules.Single() is { Part: PhysicsOverlayPart.Shape, Lit: false },
            "physics overlay: hovering a shape's row lights that shape, even one the chain doesn't bump into");
        var view = PhysicsOverlayModel.Build(file, [(0, 0)], true, new PhysicsOverlayHover(PhysicsOverlayPart.Shape, "thigh_l"), skeleton, Frame);
        Require(view.Spheres.Single() is { Part: PhysicsOverlayPart.Shape, Lit: false } && view.Capsules.Single() is { Part: PhysicsOverlayPart.Shape, Lit: true },
            "physics overlay: the shapes view shows every shape as a shape, the hovered one lit");

        var byDefault = PhysicsOverlayModel.Build(file, [(0, 0)], false, PhysicsOverlayHover.None, skeleton, Frame, PhysicsOverlayShown.Default);
        Require(byDefault.Spheres.Count == 0 && byDefault.Capsules.Count == 1 && byDefault.Circles.Count == 2 && byDefault.Segments.Count > 0,
            "physics overlay: by default the file's other shapes stay hidden, and the area, its balls, cone, spring and used shapes show");
        var onlyCone = PhysicsOverlayModel.Build(file, [(0, 0)], false, PhysicsOverlayHover.None, skeleton, Frame, PhysicsOverlayShown.Cone);
        Require(onlyCone.Capsules.Count == 0 && onlyCone.Circles.All(circle => circle.Part == PhysicsOverlayPart.Cone) &&
                onlyCone.Segments.All(segment => segment.Part == PhysicsOverlayPart.Cone) && onlyCone.Marks.All(mark => mark.Part == PhysicsOverlayPart.Cone),
            "physics overlay: unticked parts stay out");
        var hoveredHidden = PhysicsOverlayModel.Build(file, [(0, 0)], false, new PhysicsOverlayHover(PhysicsOverlayPart.Shape, "hand_r"), skeleton, Frame,
            PhysicsOverlayShown.Chain);
        Require(hoveredHidden.Spheres.Single() is { Lit: true } && hoveredHidden.Capsules.Count == 0,
            "physics overlay: a hovered shape shows even while shapes are unticked");
    }

    private static void CheckMissing()
    {
        var (skeleton, file, frames) = Chest();
        PhysicsFrame? Frame(string bone) => frames.TryGetValue(bone, out var frame) ? frame : null;
        file.Simulators[0].Chains[0].Nodes[0].Bone = PhybName.From("j_missing");
        var missing = PhysicsOverlayModel.Build(file, [(0, 0)], false, PhysicsOverlayHover.None, skeleton, Frame);
        Require(!missing.Segments.Any(segment => segment.Part is PhysicsOverlayPart.Chain or PhysicsOverlayPart.Cone or PhysicsOverlayPart.Spring) &&
                !missing.Circles.Any() && missing.Capsules.Count == 1,
            "physics overlay: a chain on a bone the character lacks draws neither chain, cone nor spring, and the shapes still draw");
        file.Simulators[0].Chains[0].Nodes[0].Bone = PhybName.From("j_mune_l");
        file.Simulators[0].Excluded = true;
        var excluded = PhysicsOverlayModel.Build(file, [(0, 0)], false, PhysicsOverlayHover.None, skeleton, Frame);
        Require(excluded.Segments.Count == 0 && excluded.Circles.Count == 0 && excluded.Marks.Count == 0 &&
                excluded.Capsules.Single().Part == PhysicsOverlayPart.OtherShape,
            "physics overlay: an area turned off draws nothing, and its shapes go dim");
        Require(PhysicsOverlayModel.Build(file, [(3, 0), (0, 9)], false, PhysicsOverlayHover.None, skeleton, Frame).Segments.Count == 0,
            "physics overlay: chains that aren't there are skipped");
        var (u, v) = PhysicsOverlayModel.Basis(Vector3.UnitY);
        Require(MathF.Abs(Vector3.Dot(u, Vector3.UnitY)) < 1e-6f && MathF.Abs(Vector3.Dot(v, Vector3.UnitY)) < 1e-6f && MathF.Abs(Vector3.Dot(u, v)) < 1e-6f &&
                MathF.Abs(u.Length() - 1) < 1e-6f && MathF.Abs(v.Length() - 1) < 1e-6f,
            "physics overlay: a ring's basis is two unit directions at right angles to its axis and each other");
    }
}
