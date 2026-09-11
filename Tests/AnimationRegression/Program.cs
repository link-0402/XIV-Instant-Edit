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
static byte[] Timeline(string code, string? path = null, int field = 20, int size = 24)
{
    var text = Encoding.UTF8.GetBytes((path ?? "") + "\0");
    var bytes = new byte[12 + size + text.Length];
    "TMLB"u8.CopyTo(bytes); Int(bytes, 4, bytes.Length); Int(bytes, 8, 1);
    Encoding.ASCII.GetBytes(code).CopyTo(bytes, 12); Int(bytes, 16, size);
    if (path != null) Int(bytes, 12 + field, size - 8);
    text.CopyTo(bytes, 12 + size); return bytes;
}
static byte[] Pap()
{
    var a = Timeline("C009", "loop"); var b = Timeline("C009", "start");
    var padding = (-a.Length) & 3;
    var bytes = new byte[116 + a.Length + padding + b.Length];
    "pap "u8.CopyTo(bytes); Int(bytes, 4, 0x20001); bytes[8] = 2;
    Int(bytes, 16, 28); Int(bytes, 20, 108); Int(bytes, 24, 116);
    "loop"u8.CopyTo(bytes.AsSpan(28)); "start"u8.CopyTo(bytes.AsSpan(68)); bytes[102] = 1;
    a.CopyTo(bytes, 116); b.CopyTo(bytes, 116 + a.Length + padding); return bytes;
}
var papBytes = Pap(); var pap = new AnimationPap(papBytes);
Check(pap.Entries.Length == 2 && pap.Entries[1].Binding == 1, "PAP keeps multiple named bindings");
var rebuilt = new AnimationPap(pap.ReplaceHavok(new byte[21]));
Check(rebuilt.Entries.SequenceEqual(pap.Entries) && rebuilt.Timelines.SequenceEqual(pap.Timelines), "replacing Havok preserves every clip entry and embedded timeline byte");
var bad = papBytes.ToArray(); Int(bad, 20, int.MaxValue);
Reject(() => new AnimationPap(bad), "invalid PAP offsets fail before native loading");
bad = papBytes.ToArray(); Int(bad, 4, 123);
Reject(() => new AnimationPap(bad), "unknown PAP versions fail closed");
var sklb = new byte[32]; Int(sklb, 0, 0x736b6c62); sklb[6] = 0x33; sklb[7] = 0x31; Int(sklb, 12, 16);
Check(AnimationPap.SkeletonHavok(sklb).Length == 16, "new-format SKLB locates its Havok payload");
sklb[6] = 0x32; sklb[10] = 16;
Check(AnimationPap.SkeletonHavok(sklb).Length == 16, "legacy SKLB locates its Havok payload");
Check(AnimationPoseRules.SampleCount(1.25f, 12) == 39 && AnimationPoseRules.SampleCount(1, 121) == 121 &&
      AnimationPoseRules.SampleCount(0, 0) == 2, "sampling includes endpoints and retains higher source density");
Reject(() => AnimationPoseRules.SampleCount(float.NaN, 0), "non-finite duration cannot reach native sampling");

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

var adapterType = typeof(LivePoseAdapter);
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
var meta = JsonNode.Parse("""
[{"Type":"Est","Manipulation":{"Race":"Midlander","Gender":"Male","SetId":1,"Slot":"Body","Entry":2}},
 {"Type":"Est","Manipulation":{"Race":"Midlander","Gender":"Female","SetId":1,"Slot":"Body","Entry":2}},
 {"Type":"Imc","Manipulation":{"ObjectType":"Weapon","PrimaryId":10,"Entry":{"MaterialId":2}}}]
""")!.AsArray();
Check(AnimationMetadata.Decode(Metadata(meta)).Count == 3, "effective metadata decoding preserves all fields");
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
    var journal = new AnimationEditJournal { Id = request.Id, Request = request, PoseBefore = pose, PoseAfter = cleared, State = "ClearingOffsets" };
    store.Save(journal); var loaded = new AnimationJournalStore(temp).Load().Single();
    Check(loaded.State == "ClearingOffsets" && AnimationPoseRules.Same(loaded.PoseBefore!, pose) && loaded.Request.SelectedBones.SetEquals(request.SelectedBones) &&
        loaded.PoseBefore!.Bones[0].Stacks[0].Ik.EnforceConstraints, "restart recovery retains complete stacks, IK constraints, filters, and clearing intent");
    journal.State = "Completed"; store.Save(journal);
    Check(store.Load().Single().State == "Completed", "journal transitions replace the previous durable state atomically");
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
