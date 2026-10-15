using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using InstantEdit.Services;
using InstantEdit.Services.Physics;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The physics builder's pure parts: reading and writing physics files in the game's layout (with
/// Dawntrail's extension block), areas and mirrored chains, simulators built from a skeleton's rest
/// pose, the checks that refuse files the game would misread, the editable document, and the files of
/// the builder's own mod. With the game installed, every vanilla body physics file must round-trip.
/// </summary>
internal static class PhysicsScenarios
{
    public static void Run(string testRoot)
    {
        CheckNames();
        CheckRoundTrip();
        CheckExtension();
        CheckDisabledFiles();
        CheckRejections();
        CheckAreas();
        CheckTemplates();
        CheckValidation();
        CheckDocument();
        CheckModFiles(testRoot);
        CheckLoadedLabels();
        CheckPosed();
        CheckVanillaFiles();
        PhysicsFitScenarios.Run();
        PhysicsBumpScenarios.Run();
        PhysicsTuneScenarios.Run();
        PhysicsHistoryScenarios.Run();
        PhysicsOverlayScenarios.Run();
    }

    private static void CheckNames()
    {
        var name = PhybName.From("j_mune_l");
        var raw = new byte[PhybName.Size];
        name.CopyTo(raw);
        Require(raw[8] == 0 && raw[9..].All(b => b == 0xFE) && PhybName.Empty.IsEmpty,
            "physics: names are null-terminated and padded with 0xFE, like the game's files");
        var empty = new byte[PhybName.Size];
        PhybName.Empty.CopyTo(empty);
        Require(empty.All(b => b == 0), "physics: an empty name is all zeros");
        Reject(() => PhybName.From(new string('a', 32)), "physics: names longer than 31 characters are refused");
        Reject(() => PhybName.From("boneé"), "physics: names outside printable ASCII are refused");
        raw[20] = 0x41;
        Require(PhybName.Read(raw).Text == "j_mune_l", "physics: bytes after the terminator don't belong to the name");
    }

    /// <summary> A file with every kind of list, as the game lays them out. </summary>
    internal static PhybFile Sample()
    {
        var file = new PhybFile();
        file.Collision.Capsules.Add(new PhybCapsule
        {
            Name = PhybName.From("asi_l_a"), StartBone = PhybName.From("j_asi_a_l"), EndBone = PhybName.From("j_asi_a_l"),
            EndOffset = new Vector3(0.3561f, 0, 0), Radius = 0.02f,
        });
        file.Collision.Spheres.Add(new PhybSphere { Name = PhybName.From("inside_l"), Bone = PhybName.From("j_sebo_b"), Offset = new Vector3(0.2f, 0.19f, 0.04f), Thickness = 0.145f });
        file.Collision.Ellipsoids.Add(new PhybEllipsoid { Name = PhybName.From("egg"), Bone = PhybName.From("j_kosi"), Offset2 = Vector3.One, Radius = 0.1f });
        PhybChain Chain(string bone, float tip) => new()
        {
            Dampening = 0.1f, MaxSpeed = 4, RepulsionStrength = 1, LastBoneOffset = new Vector3(tip, 0, 0),
            Nodes =
            [
                new PhybNode { Bone = PhybName.From(bone), GravityScale = 1, ConeMaxAngle = 0.14f, ConeAxisOffset = new Vector3(0.01f, 0.097f, 0.022f) },
                new PhybNode { Radius = 0.01f, GravityScale = 0.1f, ConeMaxAngle = MathF.PI, ConeAxisOffset = Vector3.UnitX, CollisionFlags = 3 },
            ],
        };
        var chest = new PhybSimulator { Group = 7 };
        chest.Chains.Add(Chain("j_mune_l", 0.145f));
        chest.Chains.Add(Chain("j_mune_r", 0.145f));
        chest.Chains[1].Collisions.Add(new PhybCollisionRef { Name = PhybName.From("inside_l"), Type = PhybCollisionType.Inside });
        chest.Collisions.Add(new PhybCollisionRef { Name = PhybName.From("asi_l_a") });
        chest.CollisionConnectors.Add(new PhybCollisionRef { Name = PhybName.From("egg"), Type = PhybCollisionType.Both });
        chest.Attracts.Add(new PhybAttract { Bone = PhybName.From("j_sebo_b"), Offset = new Vector3(0.075f, 0.1929f, 0.077f), Chain = 0, Node = 1, Stiffness = 0.075f });
        chest.Connectors.Add(new PhybConnector { Chain1 = 0, Chain2 = 1, Node1 = 1, Node2 = 1, CollisionRadius = 0.04f, Repulsion = 1, CollisionFlags = 12 });
        chest.Springs.Add(new PhybSpring { Chain1 = 0, Chain2 = 1, Node1 = 1, Node2 = 1, StretchStiffness = 0.5f, CompressStiffness = 0.25f });
        chest.Pins.Add(new PhybPin { Bone = PhybName.From("j_kosi"), Offset = Vector3.UnitY, Chain = 1, Node = 1 });
        chest.PostAlignments.Add(new PhybPostAlignment { CollisionName = PhybName.From("asi_l_a"), Chain = 0, Node = 0 });
        var tail = new PhybSimulator { Group = 14 };
        tail.Chains.Add(new PhybChain
        {
            Dampening = 0.15f, LastBoneOffset = new Vector3(0.15f, 0, 0),
            Nodes = [new PhybNode { Bone = PhybName.From("n_sippo_b") }, new PhybNode { Bone = PhybName.From("n_sippo_c") }, new PhybNode()],
        });
        file.Simulators.Add(chest);
        file.Simulators.Add(tail);
        file.Simulators.Add(new PhybSimulator { Group = 9 });
        return file;
    }

    private static void CheckRoundTrip()
    {
        var bytes = Sample().ToBytes();
        var read = PhybFile.Parse(bytes);
        Require(read.ToBytes().AsSpan().SequenceEqual(bytes), "physics: a file read back writes the same bytes");
        var chest = read.Simulators[0];
        Require(read.Simulators.Count == 3 && chest.Chains.Count == 2 && chest.Chains[0].Nodes[0].Bone.Text == "j_mune_l" &&
                chest.Chains[0].Nodes[1].Bone.IsEmpty && chest.Attracts.Single().Stiffness == 0.075f && chest.Group == 7 &&
                chest.Connectors.Single().CollisionFlags == 12 && chest.Springs.Single().CompressStiffness == 0.25f &&
                chest.Pins.Single().Chain == 1 && chest.PostAlignments.Single().CollisionName.Text == "asi_l_a" &&
                chest.Chains[1].Collisions.Single().Type == PhybCollisionType.Inside &&
                chest.CollisionConnectors.Single().Name.Text == "egg" && read.Simulators[1].Chains[0].Nodes.Count == 3 &&
                read.Simulators[2].Chains.Count == 0,
            "physics: every list of every simulator reads back as written");
        Require(read.Collision.Capsules.Single().EndOffset.X == 0.3561f && read.Collision.Spheres.Single().Thickness == 0.145f &&
                read.Collision.Ellipsoids.Single().Offset2 == Vector3.One,
            "physics: collision shapes read back as written");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(bytes) == PhybFile.CurrentVersion && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) == 3 &&
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) == 16,
            "physics: the header has the game's version, data type 3 and the shapes right after it");
        // Empty post alignment lists say 0xCCCCCCCC, other empty lists 0, as the game writes them.
        var simulators = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        var tailHeader = simulators + 4 + 72;
        Require(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(tailHeader + 40 + 7 * 4)) == 0xCCCCCCCC &&
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(tailHeader + 40 + 4 * 4)) == 0,
            "physics: empty lists get the game's offsets");
        Require(bytes[tailHeader + 38] == 0xCC && bytes[tailHeader + 39] == 0xCC, "physics: the simulator padding is 0xCCCC like the game's");
        var withoutShapes = new PhybFile { DataType = 2 };
        withoutShapes.Simulators.Add(Sample().Simulators[1]);
        var bare = withoutShapes.ToBytes();
        Require(BinaryPrimitives.ReadUInt32LittleEndian(bare.AsSpan(8)) == BinaryPrimitives.ReadUInt32LittleEndian(bare.AsSpan(12)) &&
                PhybFile.Parse(bare).Collision.IsEmpty,
            "physics: a file without shapes points both sections at the simulators");
        var emptyList = PhybFile.Parse(new PhybFile { DataType = 2, EmptySimulatorList = true }.ToBytes());
        Require(emptyList.EmptySimulatorList && emptyList.ToBytes().Length == 20, "physics: an empty simulator list keeps its count");
    }

    private static void CheckExtension()
    {
        var file = Sample();
        file.Extension = new PhybExtension { Payload = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray() };
        var bytes = file.ToBytes();
        var withoutBytes = Sample().ToBytes().Length;
        var start = (withoutBytes + 7) & ~7;
        var total = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bytes.Length - 8));
        Require(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(start)) == 0x42485045 && (ulong)(bytes.Length - withoutBytes) == total,
            "physics: the extension block starts at the next multiple of 8 and its size counts the padding, as in the game's files");
        var read = PhybFile.Parse(bytes);
        Require(read.Extension!.Payload.SequenceEqual(file.Extension.Payload) && read.ToBytes().AsSpan().SequenceEqual(bytes) &&
                read.Simulators.Count == 3,
            "physics: the extension block is kept as read");

        // Some tools align the block to 16 bytes and leave the padding out of the size.
        var sixteen = (withoutBytes + 15) & ~15;
        var other = new byte[sixteen + 16 + 40 + 24];
        Sample().ToBytes().CopyTo(other, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(other.AsSpan(sixteen), 0x42485045);
        BinaryPrimitives.WriteUInt16LittleEndian(other.AsSpan(sixteen + 4), 1);
        BinaryPrimitives.WriteInt64LittleEndian(other.AsSpan(sixteen + 8), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(other.AsSpan(other.Length - 24), 0x4B434150);
        BinaryPrimitives.WriteUInt64LittleEndian(other.AsSpan(other.Length - 8), 16 + 40 + 24);
        var tolerated = PhybFile.Parse(other);
        Require(tolerated.Extension?.Payload.Length == 40 && tolerated.Simulators.Count == 3,
            "physics: a block aligned to 16 bytes without its padding in the size is read too");
    }

    private static void CheckDisabledFiles()
    {
        var off = PhybFile.Parse(new byte[] { 0, 0, 0, 0, 12, 0, 0, 0, 12, 0, 0, 0 });
        Require(off.Version == 0 && off.Simulators.Count == 0 && off.ToBytes().SequenceEqual(new byte[] { 0, 0, 0, 0, 12, 0, 0, 0, 12, 0, 0, 0 }),
            "physics: a file that turns physics off reads and writes as the game's 12 bytes");
        var noData = new PhybFile { DataType = 0 }.ToBytes();
        Require(noData.Length == 16 && PhybFile.Parse(noData).DataType == 0, "physics: data type 0 has no sections");
    }

    private static void CheckRejections()
    {
        var bytes = Sample().ToBytes();
        Reject(() => PhybFile.Parse(bytes.AsSpan(0, 10)), "physics: a truncated file is refused");
        var badOffset = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badOffset.AsSpan(12), (uint)bytes.Length + 100);
        Reject(() => PhybFile.Parse(badOffset), "physics: a simulator offset past the end is refused");
        var badList = (byte[])bytes.Clone();
        var simulators = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(badList.AsSpan(simulators + 4 + 40 + 2 * 4), 0x7FFFFFF0);
        Reject(() => PhybFile.Parse(badList), "physics: a chain list outside the file is refused");
        var badCount = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badCount.AsSpan(simulators), 9999);
        Reject(() => PhybFile.Parse(badCount), "physics: an impossible simulator count is refused");
        var unterminated = (byte[])bytes.Clone();
        unterminated.AsSpan(24, 32).Fill(0x41);
        Reject(() => PhybFile.Parse(unterminated), "physics: a name without its terminator is refused");
        var tooMany = new PhybFile();
        tooMany.Simulators.Add(new PhybSimulator { Chains = Enumerable.Range(0, 256).Select(_ => new PhybChain()).ToList() });
        Reject(() => tooMany.ToBytes(), "physics: more than 255 chains in a simulator can't be written");
    }

    private static void CheckAreas()
    {
        var file = Sample();
        Require(PhysicsAreas.Classify(file.Simulators[0]) is { Label: "Chest", Part: PhysicsPart.Chest, Body: true } &&
                PhysicsAreas.Classify(file.Simulators[1]) is { Label: "Tail", Body: false } &&
                PhysicsAreas.Classify(file.Simulators[2]).Label == "Empty",
            "physics: simulators are named by the body area their chains move");
        var mixed = new PhybSimulator();
        foreach (var bone in new[] { "iv_shiri_l", "ya_shiri_phys_r", "iv_daitai_phys_l", "mystery_bone" })
            mixed.Chains.Add(new PhybChain { Nodes = [new PhybNode { Bone = PhybName.From(bone) }, new PhybNode()] });
        var info = PhysicsAreas.Classify(mixed);
        Require(info.Label == "Butt (IVCS, YAS), Thighs (IVCS), mystery_bone" && info.Part is null && !info.Body,
            "physics: a simulator mixing areas and unknown bones lists them all");
        Require(PhysicsAreas.Mirror("j_mune_l") == "j_mune_r" && PhysicsAreas.Mirror("n_sippo_a") is null &&
                PhysicsAreas.MirrorChain(file, 0, 0) == (0, 1),
            "physics: left and right chains are paired by their bones");
        var split = new PhybFile();
        foreach (var bone in new[] { "iv_shiri_r", "iv_shiri_l" })
            split.Simulators.Add(new PhybSimulator { Chains = [new PhybChain { Nodes = [new PhybNode { Bone = PhybName.From(bone) }, new PhybNode()] }] });
        Require(PhysicsAreas.MirrorChain(split, 0, 0) == (1, 0), "physics: mirrored chains are found in other simulators too");
        Require(PhysicsSkeleton.FamilyOf(["j_kosi", "iv_shiri_l", "ya_shiri_phys_l", "nf_bulge_a"]) == "NFLB + YAS" &&
                PhysicsSkeleton.FamilyOf(["j_kosi", "iv_shiri_l", "ya_fukubu_phys"]) == "YAS" &&
                PhysicsSkeleton.FamilyOf(["j_kosi", "iv_shiri_l"]) == "IVCS" && PhysicsSkeleton.FamilyOf(["j_kosi"]) == "Vanilla",
            "physics: skeletons are named by the bones skeleton mods add");
    }

    /// <summary> A small body: j_kosi with two butt bones, j_sebo_b with breasts turned like the game's. </summary>
    internal static PhysicsSkeleton Skeleton()
    {
        var turn = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.5f));
        return new PhysicsSkeleton(
        [
            new PhysicsBone("n_root", "", Vector3.Zero, Quaternion.Identity),
            new PhysicsBone("j_kosi", "n_root", new Vector3(0, 1, 0), Quaternion.Identity),
            new PhysicsBone("j_sebo_b", "j_kosi", new Vector3(0, 0.2f, 0), Quaternion.Identity),
            new PhysicsBone("j_mune_l", "j_sebo_b", new Vector3(0.0573f, 0.0045f, 0.0332f), turn),
            new PhysicsBone("j_mune_r", "j_sebo_b", new Vector3(0.0573f, 0.0045f, -0.0332f), turn),
            new PhysicsBone("iv_shiri_l", "j_kosi", new Vector3(0.106f, 0.062f, 0.066f), Quaternion.Identity),
            new PhysicsBone("iv_shiri_r", "j_kosi", new Vector3(0.106f, 0.062f, -0.066f), Quaternion.Identity),
        ], ["j_kao"]);
    }

    /// <summary> Where c0801's own body physics anchors the left breast's spring, on j_sebo_b. </summary>
    private static readonly Vector3 GameChestSpring = new(0.075f, 0.1929f, 0.077f);

    /// <summary> c0801's breast bones in their bind pose, from the game's base skeleton. </summary>
    private static PhysicsSkeleton GameChest() => new(
    [
        new PhysicsBone("j_sebo_b", "j_sebo_a", new Vector3(0.12164748f, 0, 0), new Quaternion(0, 0, 0.040669296f, 0.9991726f)),
        new PhysicsBone("j_mune_l", "j_sebo_b", new Vector3(0.05731833f, 0.004540924f, 0.033198476f), new Quaternion(-0.72570795f, -0.6785303f, -0.07961082f, 0.08128255f)),
        new PhysicsBone("j_mune_r", "j_sebo_b", new Vector3(0.05731833f, 0.004540962f, -0.033198595f), new Quaternion(-0.7257079f, -0.6785302f, 0.07961077f, -0.081282414f)),
    ]);

    private static void CheckTemplates()
    {
        var skeleton = Skeleton();
        var chest = PhysicsTemplates.Build(PhysicsAreas.Find("chest")!, skeleton)!;
        var tip = Vector3.Transform(new Vector3(0.145f, 0, 0), skeleton.Get("j_mune_l")!.Rotation);
        Require(chest.Group == PhysicsAreas.ChestGroup && chest.Chains.Count == 2 && chest.Chains.All(c => c.Nodes.Count == 2 && c.Nodes[1].Bone.IsEmpty) &&
                chest.Attracts.Count == 2 && chest.Attracts[0].Bone.Text == "j_sebo_b" && chest.Attracts[0].Node == 1 &&
                Vector3.Distance(chest.Attracts[0].Offset, skeleton.Get("j_mune_l")!.Position + tip * PhysicsTemplates.SpringReach) < 1e-5f,
            "physics: a new area's springs pull each tip toward a point past its rest place, in the parent bone's space, in the chest group");
        var game = PhysicsTemplates.Build(PhysicsAreas.Find("chest")!, GameChest())!;
        var off = Vector3.Distance(game.Attracts[0].Offset, GameChestSpring);
        Require(off < 0.006f && Vector3.Distance(game.Attracts[1].Offset, GameChestSpring with { Z = -GameChestSpring.Z }) < 0.006f,
            $"physics: a new chest on c0801's bind pose springs {off * 1000:F1} mm from the game's own spring point");
        Require(Vector3.Distance(Vector3.Normalize(chest.Chains[0].Nodes[0].ConeAxisOffset), Vector3.Normalize(tip)) < 1e-5f &&
                MathF.Abs(chest.Chains[0].Nodes[0].ConeMaxAngle - PhysicsTemplates.Radians(8)) < 1e-6f,
            "physics: a new area's swing is measured from where its tip rests, like the game's breasts");
        Require(PhysicsTemplates.Build(PhysicsAreas.Find("butt-yas")!, skeleton) is null &&
                PhysicsTemplates.Build(PhysicsAreas.Find("skirt")!, skeleton) is null,
            "physics: areas the skeleton has no bones for, or that have no template, can't be added");
        var file = Sample();
        var addable = PhysicsTemplates.Addable(file, skeleton).Select(kind => kind.Key).ToList();
        Require(addable.SequenceEqual(["butt-ivcs"]), "physics: only areas whose bones nothing moves yet are offered");
        file.Simulators[0].Excluded = true;
        Require(PhysicsTemplates.Addable(file, skeleton).Any(kind => kind.Key == "chest"), "physics: bones of an area that's off can be given a new one");
        var bare = new PhybChain { LastBoneOffset = new Vector3(0.1f, 0, 0), Nodes = [new PhybNode { Bone = PhybName.From("iv_shiri_l") }, new PhybNode()] };
        Require(PhysicsTemplates.RestSpring(bare, 3, skeleton, 0.04f) is { Chain: 3, Node: 1, Stiffness: 0.04f } spring &&
                spring.Bone.Text == "j_kosi" && Vector3.Distance(spring.Offset, new Vector3(0.106f + 0.1f * PhysicsTemplates.SpringReach, 0.062f, 0.066f)) < 1e-5f,
            "physics: a chain without a spring can get one anchored past where its tip rests, as the game's breasts");
    }

    private static void CheckValidation()
    {
        var skeleton = Skeleton();
        var file = Sample();
        file.Simulators.RemoveAt(1);
        Require(!PhysicsValidation.Check(file, null).Any(issue => issue.Level == PhysicsIssueLevel.Error), "physics: a correct file has no errors");

        var broken = Sample();
        broken.Simulators[0].Chains[0].Nodes.Insert(1, new PhybNode());
        broken.Simulators[0].Attracts.Add(new PhybAttract { Bone = PhybName.From("j_kosi"), Chain = 5, Node = 1 });
        broken.Simulators[0].Collisions.Add(new PhybCollisionRef { Name = PhybName.From("forearm_l") });
        broken.Simulators[0].Chains[1].Nodes[1].Radius = float.NaN;
        broken.Simulators[1].Chains.Add(new PhybChain { Nodes = [new PhybNode { Bone = PhybName.From("n_sippo_d") }] });
        var found = PhysicsValidation.Check(broken, null);
        var errors = found.Where(issue => issue.Level == PhysicsIssueLevel.Error).Select(issue => issue.Text).ToList();
        Require(errors.Count == 2 && errors.Any(text => text.Contains("isn't a number")) && errors.Any(text => text.Contains("needs its bone and a tip")),
            "physics: values that aren't numbers and chains without a tip are errors");
        Require(found.Any(issue => issue.Level == PhysicsIssueLevel.Warning && issue.Text.Contains("point 2 has no bone")) &&
                found.Single(issue => issue.Fix == PhysicsFix.RemoveBroken) is { Level: PhysicsIssueLevel.Warning } links &&
                links.Text.StartsWith("2 links", StringComparison.Ordinal) && links.Text.Contains("forearm_l"),
            "physics: links past a chain's end, missing shapes and a point without a bone before the tip are warnings, as presets ship them");

        // Removing broken links keeps every remaining shape's collision bit on the nodes.
        var links2 = Sample();
        var chestSim = links2.Simulators[0];
        chestSim.Collisions.Insert(0, new PhybCollisionRef { Name = PhybName.From("forearm_l") });
        chestSim.Chains[0].Nodes[1].CollisionFlags = 0b10;
        chestSim.Connectors.Add(new PhybConnector { Chain1 = 0, Chain2 = 1, Node1 = 2, Node2 = 1 });
        PhysicsValidation.Apply(links2, PhysicsFix.RemoveBroken, 0);
        Require(chestSim.Collisions.Single().Name.Text == "asi_l_a" && chestSim.Chains[0].Nodes[1].CollisionFlags == 0b1 &&
                chestSim.Connectors.Count == 1 && !PhysicsValidation.Check(links2, null).Any(issue => issue.Fix == PhysicsFix.RemoveBroken),
            "physics: removing broken links drops them and moves the kept shapes' collision bits along");

        var warned = Sample();
        warned.Simulators[0].Group = 4;
        warned.Simulators[0].Chains[0].Nodes[0].ConeMaxAngle = 13.09f;
        var issues = PhysicsValidation.Check(warned, skeleton);
        Require(issues.Any(issue => issue.Fix == PhysicsFix.ChestGroup && issue.Text.Contains("hair group")) &&
                issues.Any(issue => issue.Fix == PhysicsFix.ClampSwing) &&
                issues.Any(issue => issue.Level == PhysicsIssueLevel.Warning && issue.Text.Contains("n_sippo_b")) &&
                issues.Any(issue => issue.Simulator == -1 && issue.Text.Contains("j_asi_a_l")),
            "physics: body areas outside the chest group, swing limits past 180° and bones the skeleton lacks are warnings");
        PhysicsValidation.Apply(warned, PhysicsFix.ChestGroup, 0);
        PhysicsValidation.Apply(warned, PhysicsFix.ClampSwing, 0);
        Require(warned.Simulators[0].Group == PhysicsAreas.ChestGroup && warned.Simulators[0].Chains[0].Nodes[0].ConeMaxAngle == MathF.PI,
            "physics: the fixes move the area to the chest group and limit the swing to 180°");
        warned.Simulators[0].Excluded = true;
        Require(!PhysicsValidation.Check(warned, skeleton).Any(issue => issue.Simulator == 0), "physics: areas that are off aren't checked");
    }

    private static void CheckDocument()
    {
        var source = Sample();
        source.Extension = new PhybExtension { Payload = [1, 2, 3, 4] };
        var document = new PhysicsDocument(PhybFile.Parse(source.ToBytes()), new PhysicsSource(PhysicsSourceKind.File, "Sample", "sample.phyb"), 801);
        Require(!document.Dirty && !document.CanUndo, "physics document: a new document has nothing to save or undo");

        document.Checkpoint();
        document.File.Simulators[0].Chains[0].Dampening = 0.5f;
        document.Changed();
        Require(document.Dirty && document.Output().Extension != null,
            "physics document: changed values keep the extension block, which still matches the simulators");
        Require(document.Undo() && !document.Dirty && document.File.Simulators[0].Chains[0].Dampening == 0.1f && document.Redo() &&
                document.File.Simulators[0].Chains[0].Dampening == 0.5f,
            "physics document: undo and redo step through the changes");
        document.Checkpoint();
        document.Checkpoint();
        Require(document.Undo() && document.File.Simulators[0].Chains[0].Dampening == 0.1f && document.Redo(),
            "physics document: undo skips steps that changed nothing, like a slider clicked without moving it");

        document.Checkpoint();
        document.File.Simulators[1].Excluded = true;
        document.Changed();
        var output = document.Output();
        Require(output.Simulators.Count == 2 && output.Simulators.All(s => !s.Excluded) && output.Extension is null && document.File.Simulators.Count == 3,
            "physics document: areas that are off are left out of the file, and the extension block with them");
        var saving = document.OutputBytes();
        document.Checkpoint();
        document.File.Simulators[0].Chains[0].Friction = 0.25f;
        document.Changed();
        document.MarkSaved(new PhysicsSource(PhysicsSourceKind.Mine, "Mine", "IE Physics"), saving);
        Require(document.Dirty && document.Source.Kind == PhysicsSourceKind.Mine,
            "physics document: a change made while saving still counts as unsaved");
        document.Undo();
        Require(!document.Dirty, "physics document: saving sets what changes are counted from");

        var skeleton = Skeleton();
        document.Checkpoint();
        Require(document.AddArea(PhysicsAreas.Find("butt-ivcs")!, skeleton) == 3 && document.File.Simulators[3].Group == PhysicsAreas.ChestGroup,
            "physics document: adding an area builds it on the skeleton");

        var other = new PhybFile();
        other.Collision.Capsules.Add(new PhybCapsule { Name = PhybName.From("asi_l_a"), StartBone = PhybName.From("j_asi_b_l"), EndBone = PhybName.From("j_asi_b_l"), Radius = 0.5f });
        other.Collision.Spheres.Add(new PhybSphere { Name = PhybName.From("inside_l"), Bone = PhybName.From("j_sebo_b"), Offset = new Vector3(0.2f, 0.19f, 0.04f), Thickness = 0.145f });
        var imported = new PhybSimulator { Group = 1 };
        imported.Chains.Add(new PhybChain { Nodes = [new PhybNode { Bone = PhybName.From("iv_shiri_l") }, new PhybNode()] });
        imported.Collisions.Add(new PhybCollisionRef { Name = PhybName.From("asi_l_a") });
        imported.Collisions.Add(new PhybCollisionRef { Name = PhybName.From("inside_l") });
        other.Simulators.Add(imported);
        document.Checkpoint();
        var index = document.ImportArea(other, 0);
        var copy = document.File.Simulators[index];
        Require(copy.Collisions[0].Name.Text == "asi_l_a_2" && copy.Collisions[1].Name.Text == "inside_l" &&
                document.File.Collision.Capsules.Count(c => c.Name.Text.StartsWith("asi_l_a", StringComparison.Ordinal)) == 2 &&
                document.File.Collision.Spheres.Count == 1 && !ReferenceEquals(copy, imported),
            "physics document: an imported area brings its shapes, renaming ones that differ from this file's and reusing the same ones");
        Require(!PhysicsValidation.Check(document.Output(), null).Any(issue => issue.Level == PhysicsIssueLevel.Error),
            "physics document: the result is a valid file");

        var off = new PhysicsDocument(new PhybFile { Version = 0, DataType = 0 }, new PhysicsSource(PhysicsSourceKind.Vanilla, "Off", ""), 801);
        off.Checkpoint();
        off.AddArea(PhysicsAreas.Find("chest")!, skeleton);
        var turnedOn = PhybFile.Parse(off.OutputBytes());
        Require(turnedOn.Version == PhybFile.CurrentVersion && turnedOn.DataType == 2 && turnedOn.Simulators.Count == 1,
            "physics document: adding an area to a file that turned physics off turns it on");

        // The game's files say 3 with shapes and 2 without; shapes imported into a file without any count.
        var bare = new PhysicsDocument(PhybFile.Parse(turnedOn.ToBytes()), new PhysicsSource(PhysicsSourceKind.File, "Bare", "bare.phyb"), 801);
        bare.Checkpoint();
        bare.ImportArea(other, 0);
        var cleared = new PhysicsDocument(PhybFile.Parse(Sample().ToBytes()), new PhysicsSource(PhysicsSourceKind.File, "Sample", "sample.phyb"), 801);
        cleared.Checkpoint();
        cleared.File.Collision = new PhybCollision();
        cleared.Changed();
        Require(bare.Output().DataType == 3 && cleared.Output().DataType == 2,
            "physics document: the data type follows whether the file has shapes");
        cleared.File.Simulators.ForEach(simulator => simulator.Excluded = true);
        cleared.Changed();
        Require(PhybFile.Parse(cleared.OutputBytes()).EmptySimulatorList,
            "physics document: with every area off the file still lists its simulators, none, instead of ending early");
    }

    private static void CheckModFiles(string testRoot)
    {
        var folder = Path.Combine(testRoot, "PhysicsMod");
        Directory.CreateDirectory(folder);
        const string gamePath = "chara/human/c0801/skeleton/base/b0001/phy_c0801b0001.phyb";
        var first = PenumbraService.CreatePhysicsModFiles(folder, "IE Physics", "test", gamePath, [1, 2, 3]);
        Require(first == "physics/phy_c0801b0001.1.phyb" && File.ReadAllBytes(Path.Combine(folder, "physics", "phy_c0801b0001.1.phyb")).SequenceEqual(new byte[] { 1, 2, 3 }) &&
                PenumbraService.PhysicsModFile(folder, gamePath) is { } mapped && mapped.EndsWith("phy_c0801b0001.1.phyb", StringComparison.Ordinal),
            "physics mod: a new mod maps the body physics to its first file");

        // A second race in the same mod, and an option the user added, stay as they are.
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "meta.json")))!.AsObject();
        meta["DefaultData"]!["Files"]!["chara/human/c1801/skeleton/base/b0001/phy_c1801b0001.phyb"] = "physics\\phy_c1801b0001.1.phyb";
        File.WriteAllText(Path.Combine(folder, "meta.json"), meta.ToJsonString());
        File.WriteAllBytes(Path.Combine(folder, "physics", "phy_c1801b0001.1.phyb"), [9]);

        var second = PenumbraService.UpdatePhysicsModFiles(folder, gamePath, [4, 5]);
        var files = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "meta.json")))!["DefaultData"]!["Files"]!.AsObject();
        Require(second == "physics/phy_c0801b0001.2.phyb" && (string?)files[gamePath] == "physics\\phy_c0801b0001.2.phyb" &&
                !File.Exists(Path.Combine(folder, "physics", "phy_c0801b0001.1.phyb")) &&
                File.Exists(Path.Combine(folder, "physics", "phy_c1801b0001.1.phyb")) && files.Count == 2,
            "physics mod: each save maps a new file name and deletes the old one, leaving other races alone");
        var third = PenumbraService.UpdatePhysicsModFiles(folder, gamePath.ToUpperInvariant(), [6]);
        files = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "meta.json")))!["DefaultData"]!["Files"]!.AsObject();
        Require(third.EndsWith(".3.phyb", StringComparison.Ordinal) && files.Count == 2, "physics mod: game paths match whatever their case");

        // Renamed in Penumbra, the mod keeps its identifier, and saves follow it.
        var identifier = PenumbraService.ReadModStableIdentifierForRegression(folder)!.Value;
        Require(PenumbraService.FindPhysicsModFolder(testRoot, "PhysicsMod", identifier, null) == "PhysicsMod" &&
                PenumbraService.FindPhysicsModFolder(testRoot, "PhysicsMod", Guid.NewGuid(), ["PhysicsMod"]) is null,
            "physics mod: the mod is found in its folder while that folder has its identifier");
        Directory.Move(folder, Path.Combine(testRoot, "PhysicsMod Renamed"));
        Require(PenumbraService.FindPhysicsModFolder(testRoot, "PhysicsMod", identifier, ["Another Mod", "PhysicsMod Renamed"]) == "PhysicsMod Renamed" &&
                PenumbraService.FindPhysicsModFolder(testRoot, "PhysicsMod", identifier, ["Another Mod"]) is null,
            "physics mod: a mod renamed in Penumbra is found by its identifier");
    }

    /// <summary> "Loaded now" names a game file a file swap picks by its race. </summary>
    private static void CheckLoadedLabels()
    {
        var own = PhysicsPaths.BodyPhysics(801);
        Require(PhysicsPaths.GameFileLabel(own, own.ToUpperInvariant(), PhysicsPaths.BodyPhysics) == "the game's own file" &&
                PhysicsPaths.GameFileLabel(own, null, PhysicsPaths.BodyPhysics) == "the game's own file" &&
                PhysicsPaths.GameFileLabel(own, PhysicsPaths.BodyPhysics(1401), PhysicsPaths.BodyPhysics) == "the game's file for Female Au Ra" &&
                PhysicsPaths.GameFileLabel(own, "chara/equipment/e0001/phy_e0001.phyb", PhysicsPaths.BodyPhysics) == "another game file" &&
                PhysicsPaths.GameFileLabel(PhysicsPaths.BodySkeleton(801), PhysicsPaths.BodySkeleton(201), PhysicsPaths.BodySkeleton) == "the game's file for Female Midlander",
            "physics: a swapped game file is named by the race it belongs to, or as another game file");
    }

    /// <summary> In GPose the builder reads your posed copy: the first GPose actor with your name, as Penumbra finds it to redraw. </summary>
    private static void CheckPosed()
    {
        string?[] slots = ["Brio One", "Lucina Zero", "Lucina Zero", null, "After Gap"];
        int? Find(string name) => PhysicsLiveReader.PosedSlot(slot => slot < slots.Length ? slots[slot] : null, name);
        Require(Find("Lucina Zero") == PhysicsLiveReader.FirstPosed + 1 && Find("Brio One") == PhysicsLiveReader.FirstPosed,
            "physics: in GPose your posed copy is the first GPose actor with your name");
        Require(Find("After Gap") is null && Find("Someone Else") is null && PhysicsLiveReader.PosedSlot(_ => null, "Lucina Zero") is null,
            "physics: the search ends at the first empty GPose slot, as Penumbra's does");
    }

    /// <summary> The walk and run the builder plays in GPose are the game's own loops. </summary>
    private static void CheckMotions(Lumina.GameData game)
    {
        var sheet = game.Excel.GetSheet<Lumina.Excel.RawRow>(name: "ActionTimeline");
        Require(PhysicsMotions.Keys.All(motion => sheet.GetRow(motion.Timeline).ReadStringColumn(0).ExtractText() == motion.Key),
            "physics: the walk and run played in GPose are the game's normal/walk and normal/run");
    }

    /// <summary> Every vanilla body physics file, read from the installed game when there is one. </summary>
    private static void CheckVanillaFiles()
    {
        var sqpack = GameSqpack();
        if (sqpack is null)
        {
            Console.WriteLine("[SKIP] physics: the game isn't installed where XIVLauncher says, so vanilla files aren't checked");
            return;
        }
        using var game = new Lumina.GameData(sqpack, new Lumina.LuminaOptions { PanicOnSheetChecksumMismatch = false });
        var checkedFiles = 0;
        var skeleton = Skeleton();
        foreach (var race in InstantEdit.Services.GameFiles.GameRaces.All)
        {
            var path = PhysicsPaths.BodyPhysics(race.Code);
            if (game.GetFile(path)?.Data is not { } bytes)
                continue;
            var file = PhybFile.Parse(bytes);
            var document = new PhysicsDocument(file, new PhysicsSource(PhysicsSourceKind.Vanilla, "vanilla", path), race.Code);
            if (!file.ToBytes().AsSpan().SequenceEqual(bytes) || !document.OutputBytes().AsSpan().SequenceEqual(bytes) ||
                PhysicsValidation.Check(file, null).Any(issue => issue.Level == PhysicsIssueLevel.Error))
                throw new InvalidOperationException($"physics: the game's {path} doesn't round-trip or has errors");
            checkedFiles++;
        }
        Require(checkedFiles >= 20, $"physics: all {checkedFiles} of the game's body physics files round-trip byte for byte and pass the checks");
        var c0801 = PhybFile.Parse(game.GetFile(PhysicsPaths.BodyPhysics(801))!.Data);
        Require(c0801.Simulators.Any(s => PhysicsAreas.Classify(s).Part == PhysicsPart.Chest && s.Group == PhysicsAreas.ChestGroup) &&
                c0801.Simulators.Select(s => PhysicsAreas.Classify(s).Label).Contains("Skirt"),
            "physics: the game's Miqo'te file has the chest in group 7 and the skirt");
        var chest = c0801.Simulators.First(s => PhysicsAreas.Classify(s).Part == PhysicsPart.Chest);
        var left = chest.Chains.FindIndex(chain => chain.Nodes[0].Bone.Text == "j_mune_l");
        Require(chest.Attracts.Any(a => a.Chain == left && a.Node == 1 && a.Bone.Text == "j_sebo_b" && Vector3.Distance(a.Offset, GameChestSpring) < 1e-4f) &&
                MathF.Abs(chest.Chains[left].LastBoneOffset.Length() - 0.145f) < 1e-3f,
            "physics: the game's Miqo'te breasts pull a 14.5 cm tip toward the spring point new chests are checked against");
        _ = skeleton;
        PhysicsFitScenarios.CheckGameBody(game);
        PhysicsTuneScenarios.CheckGame(game);
        CheckMotions(game);
    }

    private static string? GameSqpack()
    {
        try
        {
            var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "launcherConfigV3.json");
            if (!File.Exists(config))
                return null;
            var game = JsonNode.Parse(File.ReadAllText(config))?["GamePath"]?.GetValue<string>();
            var sqpack = game is null ? null : Path.Combine(game, "game", "sqpack");
            return sqpack is not null && Directory.Exists(sqpack) ? sqpack : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
