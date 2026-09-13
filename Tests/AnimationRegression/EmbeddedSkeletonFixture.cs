using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using FFXIVClientStructs.Havok.Animation;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Serialize.Util;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

internal static unsafe class EmbeddedSkeletonFixture
{
    private const string GamePath = "chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        using var arena = new AnimationNative.Arena();
        var rest = new BoneTransform(Vector3.Zero, Quaternion.Identity, Vector3.One);
        var source = new SkeletonDescription("skeleton", "", Enumerable.Range(0, 167)
            .Select(i => new SkeletonBone($"bone{i}", (short)(i == 0 ? -1 : 0), 0, rest)).ToImmutableArray(), [], [], []);
        var target = source with { Bones = source.Bones.Add(new("n_hara_noanim_trans", 0, 0, rest)) };
        var main = AnimationSkeleton.Materialize(target, arena);
        var embedded = AnimationSkeleton.Materialize(source, arena);
        var alternative = AnimationSkeleton.Materialize(source with
        { Bones = source.Bones.SetItem(1, source.Bones[1] with { Reference = rest with { Position = Vector3.UnitX } }) }, arena);
        var invalid = arena.Alloc<hkaSkeleton>();
        var container = arena.Alloc<hkaAnimationContainer>();
        container->Skeletons = arena.Array<FFXIVClientStructs.Havok.Common.Base.Types.hkRefPtr<hkaSkeleton>>(1);
        container->Skeletons.Data[0].ptr = main;
        var root = arena.Alloc<hkRootLevelContainer>();
        root->NamedVariants = arena.Array<hkRootLevelContainer.NamedVariant>(5);
        Mapper(root, 0, embedded, main, arena);
        Mapper(root, 1, embedded, main, arena); // Repeated native pointers.
        Mapper(root, 2, AnimationSkeleton.Materialize(source, arena), main, arena); // Repeated content.
        Mapper(root, 3, alternative, main, arena);
        Mapper(root, 4, invalid, main, arena);
        var variants = AnimationSkeleton.DescribeSources(root, container);
        check(variants.Length == 3 && variants[0].Name == "" && variants.Count(v => v.Skeleton.Bones.Length == 167) == 2,
            "mapper source skeletons are discovered, deduplicated by content, and isolated from malformed siblings");
        var channels = new AnimationChannels("skeleton", Enumerable.Range(1, 145).Select(i => (short)i).ToImmutableArray(), [], [], 167, 0);
        var candidates = Candidates(variants);
        var resolution = AnimationSkeletonIndex.Rank(channels, candidates, AnimationSkeleton.Describe(main), GamePath);
        check(resolution.State == SkeletonResolutionState.Matched && resolution.Selected?.Skeleton.Fingerprint == AnimationRuntime.SkeletonFingerprint(embedded),
            "167-bone predictive startup automatically resolves its embedded source instead of the 168-bone main skeleton");
        var ambiguity = AnimationSkeletonIndex.Rank(channels, candidates, null, GamePath);
        check(ambiguity.State == SkeletonResolutionState.Ambiguous && ambiguity.Candidates.Length == 2,
            "equally plausible embedded reference poses require a source choice");
        var other = candidates.Single(c => c.Skeleton.Fingerprint == AnimationRuntime.SkeletonFingerprint(alternative));
        check(AnimationSkeletonIndex.Rank(channels, candidates, null, GamePath, AnimationSkeletonIndex.SelectionId(other)).Selected?.Skeleton.Fingerprint == other.Skeleton.Fingerprint,
            "manual variant selection distinguishes two source poses within one SKLB file");
        var selected = AnimationSkeleton.SelectSource(variants, resolution.Selected!.Skeleton.Fingerprint);
        var processing = AnimationSkeleton.Materialize(selected, arena);
        var binding = Predictive(channels, arena);
        reject(() => AnimationNative.Sampler.Validate(main, binding), "main skeleton still fails the predictive decoder guard");
        AnimationNative.Sampler.Validate(processing, binding);
        check(true, "the selected embedded reference pose passes the unchanged native decoder guard");
        var pose = selected.Bones.Select(b => b.Reference).ToArray();
        pose[1] = pose[1] with { Position = new(0, 2, 0) };
        var mapped = new AnimationRetarget(selected, AnimationSkeleton.Describe(main), channels).Map(pose);
        check(mapped.Length == 168 && mapped[1].Position == new Vector3(0, 2, 0) && mapped[167] == rest,
            "decoded source motion retargets to 168 bones while the additional destination helper retains its reference pose");
        check(AnimationSkeletonIndex.Rank(channels with { ReferenceBones = null, ReferenceFloats = null }, candidates,
            AnimationSkeleton.Describe(main), GamePath).Selected?.Skeleton.Bones.Length == 168,
            "the uncompressed loop keeps its main skeleton after embedded sources are indexed");
        reject(() => AnimationSkeleton.SelectSource(variants, "stale-fingerprint"), "a changed embedded source cannot silently fall back to the main skeleton");
        check(selected.Bones[1].Reference.Position == Vector3.Zero && main->Bones.Length == 168 && embedded->Bones.Length == 167,
            "processing does not resize or mutate the source SKLB graph");
        var restored = System.Text.Json.JsonSerializer.Deserialize<SkeletonCandidate>(System.Text.Json.JsonSerializer.Serialize(resolution.Selected,
            new System.Text.Json.JsonSerializerOptions { IncludeFields = true }), new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
        check(restored?.Source.Variant == resolution.Selected.Source.Variant && restored.Skeleton.Fingerprint == selected.Fingerprint,
            "embedded variant identity survives capture and journal serialization");
        root->NamedVariants.Data = null;
        reject(() => AnimationSkeleton.DescribeSources(root, container), "missing root variant buffers are rejected before native traversal");
    }

    private static ImmutableArray<SkeletonCandidate> Candidates(ImmutableArray<AnimationSkeleton.Variant> variants) => variants.Select(v =>
        new SkeletonCandidate(new(SkeletonSourceKind.Mod, new(GamePath, "fixture.sklb", "hash"), v.Name), v.Skeleton, 0, "")).ToImmutableArray();

    private static void Mapper(hkRootLevelContainer* root, int index, hkaSkeleton* a, hkaSkeleton* b, AnimationNative.Arena arena)
    {
        // Independent x64 hkaSkeletonMapper ABI: hkReferencedObject occupies the
        // first 16 bytes; the mapping begins with skeletonA and skeletonB pointers.
        var mapper = arena.Alloc<byte>(0xE0);
        Marshal.WriteIntPtr((nint)mapper, 0x10, (nint)a);
        Marshal.WriteIntPtr((nint)mapper, 0x18, (nint)b);
        root->NamedVariants.Data[index].Name = AnimationSkeleton.String((index / 2).ToString(), arena);
        root->NamedVariants.Data[index].ClassName = AnimationSkeleton.String("hkaSkeletonMapper", arena);
        root->NamedVariants.Data[index].Variant.ptr = (FFXIVClientStructs.Havok.Common.Base.Object.hkReferencedObject*)mapper;
    }

    private static hkaAnimationBinding* Predictive(AnimationChannels channels, AnimationNative.Arena arena)
    {
        var a = arena.Alloc<AnimationNative.Predictive>();
        a->Animation.Type = hkaAnimation.AnimationType.PredictiveCompressedAnimation;
        a->Animation.Duration = 1;
        a->Animation.NumberOfTransformTracks = channels.Bones.Length;
        a->Animation.NumberOfFloatTracks = channels.Floats.Length;
        a->NumBones = channels.ReferenceBones!.Value; a->NumFloatSlots = channels.ReferenceFloats!.Value; a->NumFrames = 2;
        a->CompressedData = arena.Copy<byte>([1]); a->IntData = arena.Array<ushort>(8); a->FloatData = arena.Array<float>(8);
        var b = arena.Alloc<hkaAnimationBinding>(); b->Animation.ptr = &a->Animation;
        b->TransformTrackToBoneIndices = arena.Copy<short>(channels.Bones.AsSpan());
        b->FloatTrackToFloatSlotIndices = arena.Copy<short>(channels.Floats.AsSpan());
        return b;
    }

    // Optional local acceptance: XAT XML exports let us exercise the production
    // discovery/selection/retarget guards using real assets without game calls or
    // committing somebody else's skeleton/animation data to the test repository.
    public static void InspectXml(string animationPath, string skeletonPath, Action<bool, string> check)
    {
        using var arena = new AnimationNative.Arena();
        var xml = XDocument.Load(skeletonPath);
        var objects = xml.Root!.Elements("object").ToDictionary(e => (string)e.Attribute("id")!);
        XElement? Field(XElement e, string name) => e.Elements().FirstOrDefault(f => (string?)f.Attribute("name") == name);
        string Text(XElement e, string name, string fallback = "") => Field(e, name)?.Value ?? fallback;
        float[] Floats(XElement e) => e.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => BitConverter.Int32BitsToSingle(unchecked((int)Convert.ToUInt32(t[1..], 16)))).ToArray();
        var skeletons = new Dictionary<string, nint>();
        foreach (var (id, e) in objects.Where(p => (string?)p.Value.Attribute("type") == "hkaSkeleton"))
        {
            var names = Field(e, "bones")!.Elements("struct").ToArray();
            var parents = Text(e, "parentIndices").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(short.Parse).ToArray();
            var references = Field(e, "referencePose")!.Elements("vec12").Select(Floats).ToArray();
            var bones = names.Select((b, i) => new SkeletonBone(Text(b, "name"), parents[i], byte.Parse(Text(b, "lockTranslation", "0")),
                new(new(references[i][0], references[i][1], references[i][2]), new(references[i][4], references[i][5], references[i][6], references[i][7]),
                    new(references[i][8], references[i][9], references[i][10])))).ToImmutableArray();
            if (Field(e, "floatSlots") != null || Field(e, "partitions") != null) throw new InvalidDataException("Local XML fixture currently requires skeletons without floats/partitions.");
            skeletons[id] = (nint)AnimationSkeleton.Materialize(new(Text(e, "name"), "", bones, [], [], []), arena);
        }
        var c = objects.Values.Single(e => (string?)e.Attribute("type") == "hkaAnimationContainer");
        var main = (hkaSkeleton*)skeletons[Field(c, "skeletons")!.Element("ref")!.Value];
        var container = arena.Alloc<hkaAnimationContainer>();
        container->Skeletons = arena.Array<FFXIVClientStructs.Havok.Common.Base.Types.hkRefPtr<hkaSkeleton>>(1); container->Skeletons.Data[0].ptr = main;
        var mappers = objects.Values.Where(e => (string?)e.Attribute("type") == "hkaSkeletonMapper").ToArray();
        var root = arena.Alloc<hkRootLevelContainer>(); root->NamedVariants = arena.Array<hkRootLevelContainer.NamedVariant>(mappers.Length);
        for (var i = 0; i < mappers.Length; i++)
        {
            var mapping = Field(mappers[i], "mapping")!;
            Mapper(root, i, (hkaSkeleton*)skeletons[Text(mapping, "skeletonA")], (hkaSkeleton*)skeletons[Text(mapping, "skeletonB")], arena);
        }
        var pap = XDocument.Load(animationPath);
        var anim = pap.Root!.Elements("object").Single(e => (string?)e.Attribute("type") == "hkaPredictiveCompressedAnimation");
        var binding = pap.Root.Elements("object").Single(e => (string?)e.Attribute("type") == "hkaAnimationBinding");
        var channels = new AnimationChannels(Text(binding, "originalSkeletonName"), Text(binding, "transformTrackToBoneIndices")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(short.Parse).ToImmutableArray(), [], [], int.Parse(Text(anim, "numBones")), int.Parse(Text(anim, "numFloatSlots", "0")));
        var variants = AnimationSkeleton.DescribeSources(root, container);
        var resolution = AnimationSkeletonIndex.Rank(channels, Candidates(variants), AnimationSkeleton.Describe(main), GamePath);
        check(resolution.State == SkeletonResolutionState.Matched, "supplied predictive PAP resolves a unique embedded source in the supplied SKLB");
        var selected = AnimationSkeleton.SelectSource(variants, resolution.Selected!.Skeleton.Fingerprint);
        var nativeBinding = Predictive(channels, arena);
        var predictive = (AnimationNative.Predictive*)nativeBinding->Animation.ptr;
        predictive->Animation.Duration = Floats(Field(anim, "duration")!)[0];
        predictive->NumFrames = int.Parse(Text(anim, "numFrames"));
        predictive->FirstFloatBlockScaleAndOffsetIndex = int.Parse(Text(anim, "firstFloatBlockScaleAndOffsetIndex", "0"));
        string[] Tokens(string name) => Text(anim, name).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        predictive->CompressedData = arena.Copy<byte>(Tokens("compressedData").Select(byte.Parse).ToArray());
        predictive->IntData = arena.Copy<ushort>(Tokens("intData").Select(ushort.Parse).ToArray());
        predictive->FloatData = arena.Copy<float>(Floats(Field(anim, "floatData")!));
        var intOffsets = Tokens("intArrayOffsets").Select(int.Parse).ToArray();
        var floatOffsets = Tokens("floatArrayOffsets").Select(int.Parse).ToArray();
        if (intOffsets.Length != 9 || floatOffsets.Length != 3) throw new InvalidDataException("Unexpected predictive tables in local fixture.");
        for (var i = 0; i < 9; i++) predictive->IntArrayOffsets[i] = intOffsets[i];
        for (var i = 0; i < 3; i++) predictive->FloatArrayOffsets[i] = floatOffsets[i];
        AnimationNative.Sampler.Validate(AnimationSkeleton.Materialize(selected, arena), nativeBinding);
        check(selected.Bones.Length == channels.ReferenceBones, "supplied PAP binding indices and reference counts pass the production sampler guard");
        var pose = new AnimationRetarget(selected, AnimationSkeleton.Describe(main), channels).Map(selected.Bones.Select(b => b.Reference).ToArray());
        check(pose.Length == main->Bones.Length, "supplied source reference pose retargets to the full destination skeleton");
        Console.WriteLine($"Local asset: {variants.Length} unique skeletons; selected {resolution.Selected.Source.Variant}, {selected.Bones.Length} bones -> {main->Bones.Length} target bones.");
    }
}
