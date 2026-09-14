using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Reflection;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Animations;

var passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("[PASS] " + name); passed++; }
void Reject(Action action, string name)
{
    try { action(); } catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException)
    { Check(true, name); return; }
    throw new Exception("Expected rejection: " + name);
}
static void Int(byte[] bytes, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), value);
SkeletonRepairFixture.Run(Check, Reject);
ChartSkeletonFixture.Run(Check);
EmbeddedSkeletonFixture.Run(Check, Reject);
if (args is ["--skeleton-repair-xml", var animationXml, var skeletonXml])
    EmbeddedSkeletonFixture.InspectXml(animationXml, skeletonXml, Check);
static byte[] Timeline(string code, string? path = null, int field = 20, int size = 24)
{
    var text = Encoding.UTF8.GetBytes((path ?? "") + "\0");
    var bytes = new byte[12 + size + text.Length];
    "TMLB"u8.CopyTo(bytes); Int(bytes, 4, bytes.Length); Int(bytes, 8, 1);
    Encoding.ASCII.GetBytes(code).CopyTo(bytes, 12); Int(bytes, 16, size);
    if (path != null) Int(bytes, 12 + field, size - 8);
    text.CopyTo(bytes, 12 + size); return bytes;
}
static byte[] Pap(int infoPadding = 0)
{
    var a = Timeline("C009", "loop"); var b = Timeline("C009", "start");
    var padding = (-a.Length) & 3;
    // Write the packed fields sequentially, as VFXEditor does. Do not share
    // header constants with the parser: the previous fixture hid its alignment bug.
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write("pap "u8); writer.Write(0x20001); writer.Write((short)2);
    writer.Write((short)0x1234); writer.Write((byte)1); writer.Write((byte)2); // model ID, type, variant
    var offsets = stream.Position;
    writer.Write(0); writer.Write(0); writer.Write(0);
    writer.Write(new byte[infoPadding]);
    var info = (int)stream.Position;
    foreach (var (name, type, binding, face) in new[] { ("loop", (short)7, (short)0, 0), ("start", (short)3, (short)1, 1) })
    {
        var text = Encoding.UTF8.GetBytes(name);
        writer.Write(text); writer.Write(new byte[32 - text.Length]);
        writer.Write(type); writer.Write(binding); writer.Write(face);
    }
    var havok = (int)stream.Position;
    writer.Write(new byte[] { 0x57, 0xE0, 0xE0, 0x57, 0x10, 0xC0, 0xC0, 0x10 });
    var timeline = (int)stream.Position;
    writer.Write(a); writer.Write(new byte[padding]); writer.Write(b);
    stream.Position = offsets;
    writer.Write(info); writer.Write(havok); writer.Write(timeline);
    return stream.ToArray();
}
var papBytes = Pap(); var pap = new AnimationPap(papBytes);
Check(pap.Entries.Length == 2 && pap.Entries[1].Binding == 1, "PAP keeps multiple named bindings");
Check(pap.HavokOffset == 106 && pap.TimelineOffset == 114 &&
      pap.Entries[0] == new AnimationPap.Entry("loop", 7, 0, 0) &&
      pap.Entries[1] == new AnimationPap.Entry("start", 3, 1, 1),
    "packed 26-byte PAP headers decode offsets, names, types, bindings, and face flags");
var rebuilt = new AnimationPap(pap.ReplaceHavok(new byte[21]));
Check(rebuilt.Entries.SequenceEqual(pap.Entries) && rebuilt.Timelines.SequenceEqual(pap.Timelines), "replacing Havok preserves every clip entry and embedded timeline byte");
foreach (var infoPadding in new[] { 0, 2, 5 })
{
    var originalPap = Pap(infoPadding);
    var original = new AnimationPap(originalPap);
    var motion = Enumerable.Range(0, 21).Select(i => (byte)(i + 1)).ToArray();
    var replaced = original.ReplaceHavok(motion);
    // Decode the saved header independently of AnimationPap.
    using var reader = new BinaryReader(new MemoryStream(replaced));
    reader.BaseStream.Position = 14;
    var infoOffset = reader.ReadInt32(); var havokOffset = reader.ReadInt32(); var timelineOffset = reader.ReadInt32();
    Check(infoOffset == 26 + infoPadding && havokOffset == original.HavokOffset &&
          replaced.AsSpan(0, 22).SequenceEqual(originalPap.AsSpan(0, 22)) &&
          replaced.AsSpan(26, havokOffset - 26).SequenceEqual(originalPap.AsSpan(26, havokOffset - 26)) &&
          replaced.AsSpan(havokOffset, motion.Length).SequenceEqual(motion) &&
          replaced.AsSpan(timelineOffset).SequenceEqual(original.Timelines) &&
          timelineOffset % 4 == original.TimelineOffset % 4,
        $"PAP replacement updates only the packed footer offset and motion, preserving metadata and alignment (info padding {infoPadding})");
}
var bad = papBytes.ToArray(); Int(bad, 18, int.MaxValue);
Reject(() => new AnimationPap(bad), "invalid PAP offsets fail before native loading");
bad = papBytes.ToArray(); Int(bad, 14, 25);
Reject(() => new AnimationPap(bad), "PAP entry tables cannot overlap the packed header");
bad = papBytes.ToArray(); Int(bad, 18, 105);
Reject(() => new AnimationPap(bad), "PAP entry tables cannot overlap Havok data");
bad = papBytes.ToArray(); Int(bad, 22, 105);
Reject(() => new AnimationPap(bad), "PAP timelines cannot precede Havok data");
bad = papBytes.ToArray(); Int(bad, 22, bad.Length + 1);
Reject(() => new AnimationPap(bad), "PAP timelines cannot point beyond the file");
bad = papBytes.ToArray(); BinaryPrimitives.WriteInt16LittleEndian(bad.AsSpan(8), -1);
Reject(() => new AnimationPap(bad), "negative PAP animation counts remain invalid");
Reject(() => new AnimationPap(papBytes[..25]), "truncated packed PAP headers are rejected");
bad = papBytes.ToArray(); Int(bad, 4, 123);
Reject(() => new AnimationPap(bad), "unknown PAP versions fail closed");
var sklb = new byte[32]; Int(sklb, 0, 0x736b6c62); sklb[6] = 0x33; sklb[7] = 0x31; Int(sklb, 12, 16);
Check(AnimationPap.SkeletonHavok(sklb).Length == 16, "new-format SKLB locates its Havok payload");
sklb[6] = 0x32; sklb[10] = 16;
Check(AnimationPap.SkeletonHavok(sklb).Length == 16, "legacy SKLB locates its Havok payload");
Check(AnimationPoseRules.SampleCount(1.25f, 12) == 45 && AnimationPoseRules.SampleCount(1, 121) == 121 &&
      AnimationPoseRules.SampleCount(0, 0) == 2, "sampling includes endpoints and retains higher source density");
Reject(() => AnimationPoseRules.SampleCount(float.NaN, 0), "non-finite duration cannot reach native sampling");
Check(AnimationPoseRules.ValidStartupDuration(0) && AnimationPoseRules.ValidStartupDuration(2) &&
      !AnimationPoseRules.ValidStartupDuration(-0.001f) && !AnimationPoseRules.ValidStartupDuration(2.001f) &&
      !AnimationPoseRules.ValidStartupDuration(float.NaN), "startup duration validation is inclusive and rejects invalid values");
Check(AnimationPoseRules.StartupSampleCount(0) == 2 && AnimationPoseRules.StartupSampleCount(1) == 31,
    "zero-duration startup transitions use a repeated immediate endpoint while positive durations use a 30 FPS inclusive grid");
var startupFrom = new BoneTransform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 170 / 180), Vector3.One);
var startupTo = new BoneTransform(new(10, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI * 170 / 180), new(3));
var startupMid = AnimationPoseRules.Blend(startupFrom, startupTo, 0.5f);
Check(startupMid.Position == new Vector3(5, 0, 0) && startupMid.Scale == new Vector3(2) &&
      Math.Abs(1 - Math.Abs(Quaternion.Dot(startupMid.Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI)))) < 0.001f &&
      AnimationPoseRules.BlendFloat(1, 5, 0.5f) == 3 &&
      AnimationPoseRules.SmoothStep(0) == 0 && AnimationPoseRules.SmoothStep(1) == 1,
    "startup blending uses smoothstep translation, scale, float, and shortest-path quaternion interpolation");

var ik = new PoseIk(true, 1, true, 0, 0, 2, 1, 0, Vector3.UnitX);
var first = new PoseStack(new(1, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .6f), new(.1f), PoseComponents.Position, ik);
var second = first with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .9f), Ik = ik with { Enabled = false } };
var bone = new PoseBone(new("hand", 0), [first, second], IkChain: ["hand", "forearm", "arm"]);
var other = new PoseBone(new("face", 1), [second], true, ["face"]);
var pose = new PoseSnapshot(42, 0, 5, false, [bone, other], DateTime.UtcNow, "fixture-v1");
var resource = new AnimationResource("chara/test.pap", "chara/test.pap", AnimationPap.Hash(papBytes));
var capture = new AnimationCapture("clip", 42, 123, Guid.NewGuid(), "Test", "Emote", new(resource.GamePath, "loop", 0, 0, 42, "chara/test.sklb"), null,
    [resource.GamePath], [resource], pose, pose.CapturedUtc, true);
var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Animation regression", false, ImmutableHashSet.Create(bone.Id), PoseComponents.Position);
var filtered = AnimationPoseRules.Filter(first, PoseComponents.Rotation | PoseComponents.Scale);
Check(!filtered.Ik.Enabled && filtered.Position == Vector3.Zero && filtered.Rotation == first.Rotation && filtered.Scale == first.Scale,
    "excluding Position disables its IK operation and preserves other components");
var cleared = AnimationPoseRules.RemoveBaked(pose, request);
Check(cleared.Bones[0].Stacks.Length == 2 && cleared.Bones[0].Stacks[1].Rotation == second.Rotation && cleared.Bones[1] == other &&
    cleared.Bones[0].Stacks.All(s => s.Position == Vector3.Zero && !s.Ik.Enabled), "scoped clearing preserves stack order, excluded components, and other partials");
Check(!AnimationPoseRules.Same(pose, pose with { Bones = [bone with { Stacks = [second, first] }, other] }), "reordered stacks count as a newer pose edit");
Check(AnimationPoseRules.Same(pose, pose with { CapturedUtc = pose.CapturedUtc.AddHours(1) }), "capture time alone does not cause a pose conflict");
Reject(() => AnimationPoseRules.Validate(first with { Position = new(float.NaN) }), "invalid pose components are rejected");
var presentationSkeleton = new SkeletonDescription("test", "presentation", [], [], [], []);
var presentationCandidate = new SkeletonCandidate(new(SkeletonSourceKind.Game, resource), presentationSkeleton, 0, "fixture");
var matched = new SkeletonResolution(SkeletonResolutionState.Matched, [presentationCandidate], presentationCandidate);
var standing = capture with
{
    DisplayName = "Idle", Playing = true,
    Clip = capture.Clip with { GamePath = "chara/human/c0801/animation/a0001/bt_common/resident/idle.pap", Timeline = 3,
        SkeletonPath = "chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb", Resolution = matched },
    Startup = capture.Clip with { GamePath = "chara/human/c0801/animation/a0001/bt_common/emote/j_pose01_start.pap", Timeline = 642, Resolution = matched },
};
var recentReady = standing with { Id = "recent", Playing = false, Startup = null, Clip = standing.Clip with
{ GamePath = "chara/human/c0801/animation/a0001/bt_common/resident/move_a.pap", Timeline = 13 } };
var unavailable = standing with { Id = "unavailable", Playing = false, Clip = standing.Clip with { Resolution = new(SkeletonResolutionState.Searching, []) } };
var resolvingCurrent = unavailable with { Id = "resolving", Playing = true, Startup = null };
var listItems = AnimationPresentation.ListItems([standing, resolvingCurrent, recentReady, unavailable]);
Check(listItems.Length == 4 && !listItems[0].Startup && listItems[1].Startup && listItems[2].Capture.Id == "resolving" && listItems[3].SeparatorBefore,
    "animation list shows active rows immediately while keeping unresolved recent clips hidden");
var waitingStartup = standing with { Startup = standing.Startup! with { Resolution = new(SkeletonResolutionState.Searching, []) } };
Check(AnimationPresentation.ListItems([waitingStartup]) is [{ Startup: false }, { Startup: true }],
    "a detected startup remains visible with its active loop while its skeleton is prepared");
Check(AnimationPresentation.AnimationName(standing) == "Standing Idle - Loop 1" &&
      AnimationPresentation.AnimationName(standing, true) == "Chair Sitting Idle 1 - Startup" &&
      AnimationPresentation.AnimationName(recentReady) == "Movement - Running" &&
      AnimationPresentation.AnimationName(capture with { DisplayName = "Joy" }) == "Joy" &&
      AnimationPresentation.AnimationName(standing with { DisplayName = "Change Pose", Clip = standing.Clip with
      { GamePath = "chara/human/c0801/animation/a0001/bt_common/emote/pose05_loop.pap" } }) == "Standing Idle - Loop 5",
    "animation presentation uses readable idle, movement, startup, and emote names");
Check(AnimationPresentation.ModelName(standing.Clip) == "Female Miqo'te (c0801)" &&
      AnimationPresentation.BoneName("j_te_l") == "Left Hand (j_te_l)" &&
      AnimationPresentation.BoneName("custom_bone_l") == "Custom Bone Left (custom_bone_l)",
    "animation presentation translates model and known or fallback bone identifiers");
var sourcePresentation = presentationCandidate with
{
    Source = presentationCandidate.Source with
    {
        CanonicalModel = "c0101",
        MappedGamePaths = ["chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb", standing.Clip.SkeletonPath],
    },
};
Check(AnimationPresentation.ModelName(standing.Clip) == "Female Miqo'te (c0801)" &&
      AnimationPresentation.SourceModelName(sourcePresentation) == "Male Midlander (c0101)",
    "animation presentation distinguishes the live target model from the mapped source model");
Check(AnimationPresentation.DefaultModName(standing with { Sources = [resource with { GamePath = standing.Clip.GamePath, ModName = "Paragon" }] }) == "Paragon - Instant Edit" &&
      AnimationPresentation.DefaultModName(capture with { DisplayName = "Joy" }) == "Joy - Instant Edit",
    "animation mod defaults use the source mod or readable game animation name");

var adapterType = typeof(LivePoseAdapter);
var entityManager = new LivePose.Entities.EntityManager();
Check(ReferenceEquals(LivePoseAdapter.FindEntity(entityManager, typeof(LivePose.Entities.Core.EntityId),
        new LivePose.Entities.Core.EntityId("actor_123")), entityManager.Player),
    "LivePose entity lookup selects the non-generic overload with the exact EntityId signature");
Check(LivePoseAdapter.FindEntity(entityManager, typeof(LivePose.Entities.Core.EntityId),
        new LivePose.Entities.Core.EntityId("actor_missing")) == null,
    "a missing LivePose entity remains unavailable without invoking another overload");
var hostType = adapterType.GetNestedType("Host", BindingFlags.NonPublic)!;
var host = Activator.CreateInstance(hostType, typeof(LivePose.FixtureCapability).Assembly, new LivePose.FixtureCapability(), pose.AdapterIdentity)!;
object BuildPose(PoseSnapshot before, PoseSnapshot after) => adapterType.GetMethod("BuildPose", BindingFlags.NonPublic | BindingFlags.Static)!
    .Invoke(null, [host, new LivePose.Game.Posing.PoseInfo(), before, after, null])!;
ImmutableArray<PoseBone> ReadBones(object value) => (ImmutableArray<PoseBone>)adapterType.GetMethod("ReadBones", BindingFlags.NonPublic | BindingFlags.Static)!
    .Invoke(null, [host, value])!;
var roundtrip = pose with { Bones = ReadBones(BuildPose(pose with { Bones = [] }, pose)) };
Check(AnimationPoseRules.Same(roundtrip, pose) && roundtrip.Bones[0].IkChain.SequenceEqual(bone.IkChain),
    "adapter shape round trip preserves enabled and disabled IK options, constraints, hierarchy, and stack order");
var runtimeCleared = cleared with { Bones = ReadBones(BuildPose(pose, cleared)) };
Check(AnimationPoseRules.Same(runtimeCleared, cleared), "scoped adapter rebuilding preserves disabled IK tuning after Position clearing");
Check(AnimationCatalog.IsExplicitIdle(642) && AnimationCatalog.IsExplicitIdle(643) &&
      AnimationCatalog.IsExplicitIdle(653) && AnimationCatalog.IsExplicitIdle(654) &&
    Enumerable.Range(13, 8).All(id => AnimationCatalog.IsExplicitWalk((ushort)id)) &&
      !AnimationCatalog.IsExplicitWalk(41),
    "chair/ground sit timelines and normal walk-cycle rows remain eligible without enabling battle walk rows");
var config = new List<LivePose.LivePoseCacheEntry>
{
    new() { TimelineId = 42, Pose = [new() { BonePoseInfoId = new("hand", 0, 0), Stacks = [] },
        new() { BonePoseInfoId = new("unrelated", 0, 0), Extra = new() { ["FutureField"] = "retained" } }] },
    new() { TimelineId = 99, Pose = [] },
};
var updatedConfig = (List<LivePose.LivePoseCacheEntry>)adapterType.GetMethod("CloneConfigEntries", BindingFlags.NonPublic | BindingFlags.Static)!
    .Invoke(null, [host, config, pose, cleared, false])!;
Check(ReferenceEquals(updatedConfig.Single(e => e.TimelineId == 99), config[1]) &&
    (string?)updatedConfig.Single(e => e.TimelineId == 42).Pose.Single(b => b.BonePoseInfoId.BoneName == "unrelated").Extra!["FutureField"] == "retained",
    "scoped persistence preserves other timeline entries and unknown fields on unrelated bones");

var persistentSave = adapterType.GetMethod("PersistentSave", BindingFlags.NonPublic | BindingFlags.Static)!;
try
{
    persistentSave.Invoke(null, [host]);
    throw new Exception("Expected unavailable persistence rejection");
}
catch (TargetInvocationException e) when (e.GetBaseException() is InvalidOperationException)
{ Check(true, "missing LivePose persistence service blocks clearing before mutation"); }
var persistence = new LivePose.Config.ConfigurationService();
LivePose.LivePose.Configuration = persistence;
var save = (Action)persistentSave.Invoke(null, [host])!;
save();
Check(persistence.Saves == 1 && persistence.SavedContentId == 42, "checked persistence targets the captured character configuration");
persistence.Fail = true;
try { save(); throw new Exception("Expected persistence failure"); }
catch (TargetInvocationException e) when (e.GetBaseException() is IOException)
{ Check(true, "LivePose persistence errors propagate to recovery"); }
NativeValidationFixture.Run(Check, Reject);
ObservationPerformanceFixture.Run(Check);
AnimationMatchingFixture.Run(Check);
await MotionDependencyFixture.Run(Check, Reject);
await AnimationActivationFixture.Run(Check, Reject);

var refs = AnimationDependencies.Read(resource.GamePath, papBytes);
Check(refs.Problems.IsEmpty && refs.References.Select(r => r.Path).SequenceEqual(new[] { "loop", "start" }), "PAP dependency reader visits every embedded timeline");
refs = AnimationDependencies.Read("chara/action/test.tmb", Timeline("C002", "emote/start", 24, 28));
Check(refs.References.Single() == new AnimationReference("emote/start", "timeline"), "TMB string offsets use entry-relative addressing");
Check(!AnimationDependencies.Read("chara/action/test.tmb", Timeline("C999")).Problems.IsEmpty, "unknown timeline records block incomplete packaging");
Check(!AnimationDependencies.Read("chara/action/test.tmb", Timeline("C053", size: 28)).Problems.IsEmpty, "unresolved voice banks block incomplete packaging");
var invalidTimeline = Timeline("C009", "bad"); Int(invalidTimeline, 32, int.MaxValue);
Reject(() => AnimationDependencies.Read("chara/action/test.tmb", invalidTimeline), "invalid dependency string offsets are rejected");
Check(!AnimationDependencies.SafeGamePath("../escape.pap") && !AnimationDependencies.SafeGamePath("C:/escape.pap") &&
    !AnimationDependencies.SafeGamePath("chara//x.pap") && AnimationDependencies.SafeGamePath("chara/human/c0101/test.pap"), "dependency paths stay inside the game namespace");

static byte[] VfxChunk(string key, byte[] payload)
{
    var result = new byte[8 + payload.Length + ((-payload.Length) & 3)];
    Encoding.ASCII.GetBytes(new string(key.PadRight(4, '\0').Reverse().ToArray())).CopyTo(result, 0);
    Int(result, 4, payload.Length); payload.CopyTo(result, 8); return result;
}
var vfx = VfxChunk("AVFX", VfxChunk("Tex", "vfx/texture/test.tex\0"u8.ToArray())
    .Concat(VfxChunk("Emit", VfxChunk("SdNm", "sound/test.scd\0"u8.ToArray()).Concat(VfxChunk("SdNo", new byte[4])).ToArray())).ToArray());
var vfxReferences = AnimationDependencies.Read("vfx/test.avfx", vfx);
Check(vfxReferences.Problems.IsEmpty && vfxReferences.References.Select(r => r.Path).SequenceEqual(new[] { "vfx/texture/test.tex", "sound/test.scd" }),
    "AVFX parses aligned texture strings and nested emitter sound references");
Check(!AnimationDependencies.Read("vfx/test.avfx", VfxChunk("AVFX", VfxChunk("NEW!", new byte[4]))).Problems.IsEmpty,
    "unknown VFX root constructs block incomplete packaging");

var reads = 0;
var graph = await AnimationDependencies.BuildAsync(["chara/a.tmb", "chara/a.tmb"], path =>
{
    reads++; var bytes = Timeline("C002", path == "chara/a.tmb" ? "chara/b.tmb" : "chara/a.tmb", 24, 28);
    return Task.FromResult((new AnimationResource(path, path, AnimationPap.Hash(bytes)), bytes));
}, (_, r) => Task.FromResult<IReadOnlyList<string>>([r.Path]), CancellationToken.None);
Check(reads == 2 && graph.Files.Count == 2, "recursive dependency cycles and shared assets are deduplicated");
var packagedRoot = "chara/human/c0801/animation/a0001/bt_common/emote/pose01_loop.pap";
var packagedDependency = "chara/human/c0801/skeleton/face/f0002/skl_c0801f0002.sklb";
var packagedManifest = new AnimationDependencyManifest(
    [new AnimationResource(packagedRoot, packagedRoot, "root-hash"),
     new AnimationResource(packagedDependency, packagedDependency, "dependency-hash")],
    ImmutableDictionary<string, byte[]>.Empty
        .Add(packagedRoot, [1])
        .Add(packagedDependency, [2]));
var packagedOutputs = ImmutableDictionary<string, byte[]>.Empty.Add(packagedRoot, [3]);
var selectedModFiles = AnimationCommitService.SelectNewModFiles(packagedManifest, packagedOutputs);
Check(selectedModFiles.Count == 1 && selectedModFiles.ContainsKey(packagedRoot) &&
      !selectedModFiles.ContainsKey(packagedDependency),
    "new animation mods copy only baked clips, not transitively discovered dependencies");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try
    {
        await AnimationDependencies.BuildAsync(["chara/a.tex"], _ => throw new Exception("Must not read"), (_, _) => throw new Exception("Must not resolve"), cancelled.Token);
        throw new Exception("Expected cancellation");
    }
    catch (OperationCanceledException) { Check(true, "cancelled dependency jobs perform no resource reads"); }
}

static string Metadata(JsonArray array, byte version = 0)
{
    using var output = new MemoryStream();
    using (var gzip = new GZipStream(output, CompressionMode.Compress, true))
    { gzip.WriteByte(version); gzip.Write(Encoding.UTF8.GetBytes(array.ToJsonString())); }
    return Convert.ToBase64String(output.ToArray());
}
static string BinaryMetadataV1(byte estSlot = 75)
{
    using var output = new MemoryStream();
    using (var gzip = new GZipStream(output, CompressionMode.Compress, true))
    using (var writer = new BinaryWriter(gzip, Encoding.UTF8, true))
    {
        writer.Write((byte)1); writer.Write("META0001"u8.ToArray());
        writer.Write(1); // IMC count
        writer.Write((ushort)10); writer.Write((byte)2); writer.Write((byte)13); // primary, variant, weapon
        writer.Write((ushort)20); writer.Write((byte)0); writer.Write((byte)4); // secondary, equip slot, body slot
        writer.Write((byte)3); writer.Write((byte)4); writer.Write((ushort)((7 << 10) | 0x15)); writer.Write((byte)5); writer.Write((byte)6);
        writer.Write(1); // EQP count: 4-byte identifier, 8-byte entry
        writer.Write(Convert.FromHexString("341204008877665544332211"));
        writer.Write(1); // EQDP count: 6-byte identifier, 2-byte entry
        writer.Write(Convert.FromHexString("7856040065000300"));
        writer.Write(1); // EST count
        // Penumbra MetaIndex values, not zero-based ordinals (Est.cs / MetaIndex.cs).
        writer.Write((ushort)2); writer.Write(estSlot); writer.Write((byte)0); writer.Write((ushort)101); writer.Write((ushort)3);
        writer.Write(0); // RSP count
        writer.Write(0); // GMP count
        writer.Write(0); // global EQP count
    }
    return Convert.ToBase64String(output.ToArray());
}
var meta = JsonNode.Parse("""
[{"Type":"Est","Manipulation":{"Race":"Midlander","Gender":"Male","SetId":1,"Slot":"Body","Entry":2}},
 {"Type":"Est","Manipulation":{"Race":"Midlander","Gender":"Female","SetId":1,"Slot":"Body","Entry":2}},
 {"Type":"Imc","Manipulation":{"ObjectType":"Weapon","PrimaryId":10,"Entry":{"MaterialId":2}}}]
""")!.AsArray();
Check(AnimationMetadata.Decode(Metadata(meta)).Count == 3, "effective metadata decoding preserves all fields");
var binaryMeta = AnimationMetadata.Decode(BinaryMetadataV1());
Check(binaryMeta.Count == 2 && binaryMeta[0]!["Manipulation"]!["ObjectType"]!.GetValue<string>() == "Weapon" &&
      binaryMeta[0]!["Manipulation"]!["Entry"]!["AttributeMask"]!.GetValue<int>() == 0x15 &&
      binaryMeta[0]!["Manipulation"]!["Entry"]!["SoundId"]!.GetValue<int>() == 7 &&
      binaryMeta[1]!["Manipulation"]!["Race"]!.GetValue<string>() == "Midlander" &&
      binaryMeta[1]!["Manipulation"]!["Gender"]!.GetValue<string>() == "Male" &&
      binaryMeta[1]!["Manipulation"]!["Slot"]!.GetValue<string>() == "Body",
    "Penumbra v1 binary metadata decodes IMC and EST records for animation packaging");
Check(AnimationMetadata.Applicable(binaryMeta, ["chara/weapon/w0010/obj/body/b0020/model/test.mdl"]).Count == 1 &&
      AnimationMetadata.Applicable(binaryMeta, ["chara/weapon/w0011/obj/body/b0020/model/test.mdl"]).Count == 0 &&
      JsonNode.DeepEquals(AnimationMetadata.Applicable(binaryMeta,
              ["chara/human/c0101/skeleton/base/b0003/skl_c0101b0003.sklb"]),
          AnimationMetadata.Applicable(AnimationMetadata.Decode(Metadata(binaryMeta)),
              ["chara/human/c0101/skeleton/base/b0003/skl_c0101b0003.sklb"])),
    "binary IMC and EST numeric fields support filtering with the same results as v0 JSON");
foreach (var (slot, name, part, prefix) in new[]
         { (72, "Face", "face", "f"), (73, "Hair", "hair", "h"), (74, "Head", "met", "m"), (75, "Body", "base", "b") })
{
    var decoded = AnimationMetadata.Decode(BinaryMetadataV1((byte)slot));
    var est = decoded[1]!["Manipulation"]!;
    Check(est["Slot"]!.GetValue<string>() == name && est["SetId"]!.GetValue<int>() == 2 &&
          est["Entry"]!.GetValue<int>() == 3 && est["Gender"]!.GetValue<string>() == "Male" &&
          est["Race"]!.GetValue<string>() == "Midlander",
        $"Penumbra EST slot {slot} decodes as {name} after nonempty EQP and EQDP sections");
    Check(AnimationMetadata.Applicable(decoded,
              [$"chara/human/c0101/skeleton/{part}/{prefix}0003/skl_c0101{prefix}0003.sklb"]).Count == 1 &&
          AnimationMetadata.Applicable(decoded,
              [$"chara/human/c0201/skeleton/{part}/{prefix}0003/skl_c0201{prefix}0003.sklb"]).Count == 0,
        $"decoded {name} EST metadata selects only the matching player skeleton");
}
foreach (var slot in new byte[] { 0, 1, 2, 3, 71, 76, 255 })
    Reject(() => AnimationMetadata.Decode(BinaryMetadataV1(slot)), $"invalid EST wire slot {slot} is rejected");
Check(AnimationMetadata.Applicable(meta, ["chara/human/c0101/skeleton/base/b0002/skl_c0101b0002.sklb"]).Count == 1,
    "EST packaging is scoped to the player's skeleton, race, and gender");
Reject(() => AnimationMetadata.Decode(Metadata(meta, 99)), "unknown metadata protocol versions cannot silently lose dependencies");
var animationRelative = "files/" + resource.GamePath;
var animationMetadata = AnimationCommitService.CreateNewModMetadata(
    "Animation regression",
    [new AnimationFileChange(resource.GamePath, "target", "Animation regression", "root",
        animationRelative, "", resource.Hash, "", "staged")],
    meta.ToJsonString());
Check(animationMetadata["FileVersion"]!.GetValue<int>() == 4 &&
      Guid.TryParse(animationMetadata["Identifier"]!.GetValue<string>(), out _) &&
      animationMetadata["LastWrite"] is not null &&
      animationMetadata["Groups"]!.AsArray().Count == 0 &&
      animationMetadata["DefaultData"]!["Files"]![resource.GamePath]!.GetValue<string>() == animationRelative &&
      JsonNode.DeepEquals(animationMetadata["DefaultData"]!["Manipulations"], meta),
    "new animation mods embed mappings and manipulations in v4 meta.json data");
Reject(() => AnimationCommitService.CreateNewModMetadata("Invalid", [], "{}"),
    "animation metadata rejects non-array manipulations");

var temp = Path.Combine(Path.GetTempPath(), "ie-animation-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var store = new AnimationJournalStore(temp);
    var offsetBackup = new LivePoseOffsetBackup(capture.ActorId, capture.CollectionId, pose, AnimationPoseRules.ClearAll(pose), DateTime.UtcNow);
    store.SaveOffsetBackup(offsetBackup);
    var persistedOffsets = new AnimationJournalStore(temp).LoadOffsetBackup();
    Check(persistedOffsets != null && AnimationPoseRules.Same(persistedOffsets.Before, pose) && persistedOffsets.Cleared.Bones.IsEmpty &&
          !AnimationPoseRules.Same(persistedOffsets.Cleared, pose with { Bones = [bone] }),
        "LivePose full-clear backup survives restart and detects a changed pose before reapply");
    store.ClearOffsetBackup();
    Check(store.LoadOffsetBackup() == null, "reapplied LivePose backups are consumed");
    var journal = new AnimationEditJournal { Id = request.Id, Request = request, PoseBefore = pose, PoseAfter = cleared, State = "ClearingOffsets" };
    store.Save(journal); var loaded = new AnimationJournalStore(temp).Load().Single();
    Check(loaded.State == "ClearingOffsets" && AnimationPoseRules.Same(loaded.PoseBefore!, pose) && loaded.Request.SelectedBones.SetEquals(request.SelectedBones) &&
        loaded.PoseBefore!.Bones[0].Stacks[0].Ik.EnforceConstraints, "restart recovery retains complete stacks, IK constraints, filters, and clearing intent");
    journal.State = "Completed"; store.Save(journal);
    Check(store.Load().Single().State == "Completed", "journal transitions replace the previous durable state atomically");
    journal.Version = 1; store.Save(journal);
    Check(store.Load().Single().Request.Operation == AnimationOperation.BakeOffsets, "version-one journals retain offset-bake recovery semantics");
    journal.Version = 2; journal.Request = request with { Operation = AnimationOperation.RepairSkeleton, SelectedBones = ImmutableHashSet<PoseBoneId>.Empty, Components = PoseComponents.None };
    journal.PoseBefore = journal.PoseAfter = null; journal.PoseClearOutcome = "NotApplicable"; store.Save(journal);
    var repairJournal = store.Load().Single();
    Check(repairJournal.Request.Operation == AnimationOperation.RepairSkeleton && repairJournal.Request.SelectedBones.IsEmpty &&
        repairJournal.PoseBefore == null && repairJournal.PoseAfter == null && repairJournal.PoseClearOutcome == "NotApplicable",
        "repair-only recovery needs no offsets and cannot authorize pose restoration");
    var file = Path.Combine(temp, "test.pap"); File.WriteAllBytes(file, papBytes);
    var backups = new ModelBackupStore(temp); var backup = backups.Create(file, "test", "files/test.pap");
    var target = backups.Describe("test", "files/test.pap");
    Check(backups.Resolve(target.Id, Path.GetFileName(backup)) == backup, "managed PAP backups can be resolved after restart");
    File.WriteAllText(file, "newer user work");
    Reject(() => AnimationCommitService.RequireHash(file, resource.Hash), "conflict protection refuses to overwrite newer PAP work");
    Check(File.ReadAllText(file) == "newer user work", "failed conflict checks leave later work intact");
}
finally { Directory.Delete(temp, true); }
Console.WriteLine($"Animation regressions: {passed} passed. Native/game acceptance must be run inside FFXIV.");
