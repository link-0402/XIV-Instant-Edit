using System.Numerics;
using InstantEdit.Services.Physics;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// What undo and redo say they change, in the controls' words and values, the names of the groin's
/// IVCS areas, and areas a file has twice.
/// </summary>
internal static class PhysicsHistoryScenarios
{
    public static void Run()
    {
        CheckLabels();
        CheckChanges();
        CheckSteps();
    }

    private static PhybSimulator Area(params string[] bones)
    {
        var simulator = new PhybSimulator { Group = PhysicsAreas.ChestGroup };
        foreach (var bone in bones)
        {
            simulator.Attracts.Add(new PhybAttract { Bone = PhybName.From(bone), Chain = (short)simulator.Chains.Count, Node = 2, Stiffness = 0.02f });
            simulator.Chains.Add(new PhybChain
            {
                Dampening = 0.1f,
                LastBoneOffset = new Vector3(0.12f, 0, 0),
                Nodes =
                [
                    new PhybNode { Bone = PhybName.From(bone), ConeMaxAngle = PhysicsTemplates.Radians(8) },
                    new PhybNode { Bone = PhybName.From(bone + "_mid"), Radius = 0.01f, ConeMaxAngle = PhysicsTemplates.Radians(4) },
                    new PhybNode { Radius = 0.04f },
                ],
            });
        }
        return simulator;
    }

    private static void CheckLabels()
    {
        var file = new PhybFile();
        file.Simulators.Add(Area("iv_ochinko_b", "iv_ochinko_c"));
        file.Simulators.Add(Area("iv_kougan_l", "iv_kougan_r"));
        file.Simulators.Add(Area("iv_funyachin_phy_a"));
        file.Simulators.Add(Area("iv_kintama_phys_l", "iv_kintama_phys_r"));
        file.Simulators.Add(Area("iv_kuritto"));
        file.Simulators.Add(Area("j_mune_l", "j_mune_r"));
        file.Simulators.Add(Area("j_mune_l", "j_mune_r"));
        Require(PhysicsAreas.Labels(file).SequenceEqual(["Penis (IVCS)", "Testicles (IVCS)", "Penis, flaccid (IVCS)", "Testicles, jiggle (IVCS)",
                    "Clitoris (IVCS)", "Chest", "Chest (2)"]),
            "physics history: the groin's IVCS areas are named by part, and an area a file has twice is numbered");
        Require(file.Simulators.Take(5).All(simulator => PhysicsAreas.Classify(simulator) is { Part: PhysicsPart.Groin, Body: true }),
            "physics history: the groin's parts are body areas of the groin");
    }

    private static PhybFile Chest()
    {
        var file = new PhybFile();
        file.Collision.Capsules.Add(new PhybCapsule
        {
            Name = PhybName.From("asi_l_a"), StartBone = PhybName.From("j_asi_a_l"), EndBone = PhybName.From("j_asi_a_l"), Radius = 0.02f,
        });
        file.Simulators.Add(Area("j_mune_l", "j_mune_r"));
        return file;
    }

    private static void CheckChanges()
    {
        var from = Chest();
        var to = from.Clone();
        PhysicsKnobs.Write(to.Simulators[0], 0, PhysicsKnob.Firmness, 0.035f);
        PhysicsKnobs.Write(to.Simulators[0], 1, PhysicsKnob.Firmness, 0.035f);
        Require(PhysicsChanges.Describe(from, to).SequenceEqual(["Chest: Firmness 0.0200 to 0.0350"]) &&
                PhysicsChanges.Describe(to, from).SequenceEqual(["Chest: Firmness 0.0350 to 0.0200"]),
            "physics history: a slider's change reads as its setting and values, once for both sides");

        to = from.Clone();
        PhysicsKnobs.Write(to.Simulators[0], 0, PhysicsKnob.Swing, 30);
        Require(PhysicsChanges.Describe(from, to).SequenceEqual(["Chest (j_mune_l): Swing 8° to 30°"]),
            "physics history: a change to one side names its chain");

        to = from.Clone();
        PhysicsKnobs.Write(to.Simulators[0], 0, PhysicsKnob.Firmness, 0.02001f);
        PhysicsKnobs.Write(to.Simulators[0], 1, PhysicsKnob.Firmness, 0.02001f);
        Require(PhysicsChanges.Describe(from, to).SequenceEqual(["Chest: Firmness 0.02000 to 0.02001"]),
            "physics history: values the slider rounds alike get more decimals");

        to = from.Clone();
        to.Simulators[0].Chains[0].Nodes[1].Radius = 0.02f;
        to.Simulators[0].Chains[1].Nodes[1].Radius = 0.02f;
        Require(PhysicsChanges.Describe(from, to).SequenceEqual(["Chest, j_mune_l_mid and j_mune_r_mid: Size 1.0 cm to 2.0 cm"]),
            "physics history: a point the sliders don't show is named by its bone");

        to = from.Clone();
        to.Simulators[0].Attracts.Clear();
        Require(PhysicsChanges.Describe(from, to).SequenceEqual(["Chest: Firmness 0.0200 to none"]),
            "physics history: firmness without a spring reads as none");

        to = from.Clone();
        to.Simulators.Add(Area("iv_kyokin_phys_l", "iv_kyokin_phys_r"));
        to.Simulators[0].Excluded = true;
        Require(PhysicsChanges.Describe(from, to).SequenceEqual(["Chest: turned off", "Adds Pecs (IVCS)"]) &&
                PhysicsChanges.Describe(to, from).SequenceEqual(["Chest: turned on", "Deletes Pecs (IVCS)"]),
            "physics history: areas added, deleted, turned off and on");

        to = from.Clone();
        to.Simulators.Insert(0, Area("ya_shiri_phys_l", "ya_shiri_phys_r"));
        PhysicsKnobs.Write(to.Simulators[1], 0, PhysicsKnob.Damping, 0.2f);
        PhysicsKnobs.Write(to.Simulators[1], 1, PhysicsKnob.Damping, 0.2f);
        Require(PhysicsChanges.Describe(from, to).SequenceEqual(["Adds Butt (YAS)", "Chest: Damping 0.100 to 0.200"]),
            "physics history: an area added before another still pairs the other with itself");

        to = from.Clone();
        to.Collision.Capsules[0].Radius = 0.054f;
        Require(PhysicsChanges.Describe(from, to).SequenceEqual(["Shape asi_l_a: Radius 2.0 cm to 5.4 cm"]),
            "physics history: a collision shape's size");

        to = from.Clone();
        Require(PhysicsCollisions.Add(to, [(0, 0), (0, 1)], "asi_l_a") &&
                PhysicsChanges.Describe(from, to).SequenceEqual(["Chest: collides with Left thigh (asi_l_a)"]) &&
                PhysicsChanges.Describe(to, from).SequenceEqual(["Chest: no longer collides with Left thigh (asi_l_a)"]),
            "physics history: shapes an area starts or stops colliding with, named by the body part they cover");

        to = from.Clone();
        foreach (var knob in new[] { PhysicsKnob.Damping, PhysicsKnob.Swing, PhysicsKnob.Weight, PhysicsKnob.MaxSpeed })
            for (var c = 0; c < 2; c++)
                PhysicsKnobs.Write(to.Simulators[0], c, knob, 0.5f);
        Require(PhysicsChanges.Describe(from, to, most: 3) is { Count: 3 } capped && capped[^1] == "and 2 more" &&
                PhysicsChanges.Describe(from, from.Clone()).Count == 0,
            "physics history: a long list stops with how many more, and no change says nothing");
    }

    private static void CheckSteps()
    {
        var document = new PhysicsDocument(Chest(), new PhysicsSource(PhysicsSourceKind.File, "Chest", "chest.phyb"), 801);
        Require(document.NextUndo() is null && document.NextRedo() is null, "physics history: a new document has nothing to undo or redo");
        var history = document.History;
        document.Checkpoint("Fit to my body");
        PhysicsKnobs.Write(document.File.Simulators[0], 0, PhysicsKnob.Damping, 0.3f);
        document.Changed();
        // A slider clicked without moving is a step that changes nothing.
        document.Checkpoint();
        Require(document.NextUndo() is { Name: "Fit to my body" } step && PhysicsKnobs.Read(step.File.Simulators[0], 0, PhysicsKnob.Damping) == 0.1f &&
                document.History > history,
            "physics history: undo names the step it goes back over, past steps that changed nothing");
        var undone = document.Undo();
        var redo = document.NextRedo();
        Require(undone && redo is { Name: "Fit to my body" } && PhysicsKnobs.Read(redo.File.Simulators[0], 0, PhysicsKnob.Damping) == 0.3f &&
                document.NextUndo() is null,
            "physics history: the step's name moves to redo, with the state redo brings back");
        Require(redo != null && PhysicsChanges.Describe(document.File, redo.File).SequenceEqual(["Chest (j_mune_l): Damping 0.100 to 0.300"]),
            "physics history: redo's change reads from the state now to the state it brings back");
        document.Redo();
        document.MarkSaved(new PhysicsSource(PhysicsSourceKind.Mine, "Mine", "mine.phyb"), document.OutputBytes());
        Require(PhysicsKnobs.Read(document.Start.Simulators[0], 0, PhysicsKnob.Damping) == 0.1f &&
                PhysicsKnobs.Read(document.File.Simulators[0], 0, PhysicsKnob.Damping) == 0.3f,
            "physics history: the sliders' starting values stay those of the file as opened, through edits and saves");
    }
}
