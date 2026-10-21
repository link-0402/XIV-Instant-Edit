// Custom hair skeletons: HairSkeletonWriter's sklb for a 3-bone rig, loaded by the game as the player's
// hair skeleton through a test mod with a hair EST entry. Needs a Debug build of Instant Edit loaded as
// the dev plugin. The plan's check is c0801; the rig is built for the player's own race (c0801 for the
// test character) so the EST entry and the skeleton path match what the game loads.
// xiv_exec file=Tools/HairSkeleton/verify_writer.csx, then xiv_exec_cleanup (deletes the test mod and its settings).
using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Services.Skeletons;

const ushort SkeletonId = 9001;
const float FileTolerance = 1e-5f, LiveTolerance = 1e-4f;
var failures = new List<string>();

// The player's race and the current hair's set ID, both from Penumbra's resource tree: the tree's race
// code, and the set in the hair model's game path (chara/human/cRRRR/obj/hair/hNNNN/model/...).
var (genderRace, hairSet) = await OnFramework<(ushort, ushort?)>(() =>
{
    var trees = new global::Penumbra.Api.IpcSubscribers.GetGameObjectResourceTrees(PluginInterface).Invoke(false, 0);
    if (trees.Length == 0 || trees[0] is not { } tree) return ((ushort)0, (ushort?)null);
    var stack = new Stack<global::Penumbra.Api.Helpers.ResourceNodeDto>(tree.Nodes);
    while (stack.Count > 0)
    {
        var node = stack.Pop();
        if (node.GamePath is { } path && Regex.Match(path, @"^chara/human/c\d{4}/obj/hair/h(\d{4})/model/", RegexOptions.IgnoreCase) is { Success: true } match)
            return (tree.RaceCode, (ushort?)ushort.Parse(match.Groups[1].Value));
        foreach (var child in node.Children) stack.Push(child);
    }
    return (tree.RaceCode, (ushort?)null);
});
var race = $"c{genderRace:D4}";
if (EstEntryService.EstRaceCode(race) is null) return new { ok = false, failures = new[] { $"the player's race {race} isn't a player race" } };
if (hairSet is not ushort set) return new { ok = false, failures = new[] { "no hair model in the player's resource tree" } };

// The rig, in game model space: _a 8 cm behind and 5 cm below j_kao, _b and _c 6 cm below each other.
var est = EstTable.Parse(Data.GetFile(EstTable.GamePath(EstSlot.Hair))!.Data);
if (HairSkeletonIds.TemplateId(est, genderRace, Data.FileExists) is not ushort template)
    return new { ok = false, failures = new[] { $"{race} has no vanilla hair skeleton to start from" } };
var templatePath = ModelSkeletonPaths.PartialPath(race, EstSlot.Hair, template);
var templateBytes = Data.GetFile(templatePath)!.Data;
var kao = await OnFramework(() => HairSkeletonWriter.TemplateKao(templateBytes));
static float[] Model(Vector3 p) => [p.X, p.Y, p.Z, 0, 0, 0, 1, 1, 1, 1];
var a = kao.Reference.Position + new Vector3(0, -0.05f, -0.08f);
var b = a + new Vector3(0, -0.06f, 0);
var c = b + new Vector3(0, -0.06f, 0);
var request = new HairSkeletonRequest
{
    Race = race,
    Bones =
    [
        new() { Name = "j_ex_hair_01_a", Parent = "j_kao", Model = Model(a) },
        new() { Name = "j_ex_hair_01_b", Parent = "j_ex_hair_01_a", Model = Model(b) },
        new() { Name = "j_ex_hair_01_c", Parent = "j_ex_hair_01_b", Model = Model(c) },
    ],
};
if (HairSkeletonRequest.Validate(request) is { } invalid) return new { ok = false, failures = new[] { $"the test rig is invalid: {invalid}" } };
var bones = HairSkeletonLayout.Bones(request, kao);
var sklb = await OnFramework(() => HairSkeletonWriter.Write(templateBytes, bones));

// Read back independently of Write's own check: bones, the template's header and float slots, and
// the partition rule (one over all bones, named as the template's first, or none).
static bool Near(Vector3 x, Vector3 y, float eps) => MathF.Max(MathF.Abs(x.X - y.X), MathF.Max(MathF.Abs(x.Y - y.Y), MathF.Abs(x.Z - y.Z))) <= eps;
static bool NearQ(Quaternion x, Quaternion y, float eps) => MathF.Abs(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(x), Quaternion.Normalize(y))) - 1) <= eps;
var (before, after) = await OnFramework(() => (AnimationSkeleton.Inspect(templateBytes), AnimationSkeleton.Inspect(sklb)));
var readBack = after.Bones.Length == bones.Length;
for (var i = 0; readBack && i < bones.Length; i++)
    readBack = after.Bones[i].Name == bones[i].Name && after.Bones[i].Parent == bones[i].Parent &&
        Near(after.Bones[i].Reference.Position, bones[i].Reference.Position, FileTolerance) &&
        NearQ(after.Bones[i].Reference.Rotation, bones[i].Reference.Rotation, FileTolerance) &&
        Near(after.Bones[i].Reference.Scale, bones[i].Reference.Scale, FileTolerance);
if (!readBack) failures.Add("the written bones don't read back as given");
var offset = templateBytes.Length - AnimationPap.SkeletonHavok(templateBytes).Length;
if (!sklb.AsSpan(0, offset).SequenceEqual(templateBytes.AsSpan(0, offset))) failures.Add("header bytes differ from the template's");
if (!after.FloatNames.SequenceEqual(before.FloatNames) || !after.ReferenceFloats.SequenceEqual(before.ReferenceFloats)) failures.Add("float slots changed");
var partitionsOk = before.Partitions.IsEmpty
    ? after.Partitions.IsEmpty
    : after.Partitions is [var only] && only.Name == before.Partitions[0].Name && only.Start == 0 && only.Count == bones.Length;
if (!partitionsOk) failures.Add($"partitions [{string.Join(", ", after.Partitions.Select(p => $"{p.Name} {p.Start}+{p.Count}"))}] break the rule");

// The rig hangs from bone 0, so the template's j_kao must be the body's j_kao in model space: the race's
// base skeleton composed down its parents as HairSkeletonLayout.ModelSpace does.
var basePath = $"chara/human/{race}/skeleton/base/b0001/skl_{race}b0001.sklb";
var baseBytes = Data.GetFile(basePath)?.Data;
var baseSkeleton = baseBytes is null ? null : await OnFramework(() => AnimationSkeleton.Inspect(baseBytes));
var baseKaoIndex = baseSkeleton is null ? -1 : baseSkeleton.Bones.Select(x => x.Name).ToList().IndexOf("j_kao");
var templateKao = before.Bones[0];
var kaoFrameMatches = false;
var kaoFrame = "";
if (baseSkeleton is null) failures.Add($"{basePath} could not be read");
else if (baseKaoIndex < 0) failures.Add($"{basePath} has no j_kao");
else
{
    var baseKao = HairSkeletonLayout.ModelSpace(baseSkeleton.Bones)[baseKaoIndex];
    kaoFrameMatches = templateKao.Name == "j_kao" && templateKao.Parent < 0 &&
        Near(templateKao.Reference.Position, baseKao.Position, LiveTolerance) &&
        NearQ(templateKao.Reference.Rotation, baseKao.Rotation, LiveTolerance);
    kaoFrame = $"template {templateKao.Name} t {templateKao.Reference.Position} r {templateKao.Reference.Rotation} vs base t {baseKao.Position} r {baseKao.Rotation}";
    if (!kaoFrameMatches) failures.Add("the template's j_kao is not the base skeleton's j_kao in model space");
}

// The test mod: the skeleton at its game path, and a hair EST entry pointing the current hair at it.
var gamePath = $"chara/human/{race}/skeleton/hair/h{SkeletonId:D4}/skl_{race}h{SkeletonId:D4}.sklb";
var (gender, raceName) = AnimationMetadata.GenderRaceNames(genderRace);
var manipulations = new JsonArray(new JsonObject
{
    ["Type"] = "Est",
    ["Manipulation"] = new JsonObject
    {
        ["Gender"] = gender, ["Race"] = raceName, ["SetId"] = (int)set, ["Slot"] = "Hair", ["Entry"] = (int)SkeletonId,
    },
}).ToJsonString();
var temp = Path.Combine(Path.GetTempPath(), $"verify_hair_skeleton_{Guid.NewGuid():N}.sklb");
string mod;
try
{
    File.WriteAllBytes(temp, sklb);
    mod = Penumbra.CreateMod("HairSkelWriter", new Dictionary<string, string> { [gamePath] = temp }, null, manipulations);
}
finally { if (File.Exists(temp)) File.Delete(temp); }
Penumbra.SetTemporary(mod, true, 2000000000);
Penumbra.Redraw(0);
await Delay(3);

// The live hair bones in parent space: unanimated and without a phyb, they sit at the written reference.
var live = await OnFramework(() => Bones(0, "j_ex_hair", true));
var liveBones = live.SelectMany(partial => partial!["bones"]!.AsArray()).Select(n => n!).ToList();
// Bones() reports its numbers as doubles.
static float F(JsonNode n) => (float)n.GetValue<double>();
static Vector3 V3(JsonNode n) => new(F(n[0]!), F(n[1]!), F(n[2]!));
var liveLocalsMatch = true;
var liveReport = new List<string>();
foreach (var bone in bones.Skip(1))
{
    var found = liveBones.FirstOrDefault(n => n["name"]!.GetValue<string>() == bone.Name);
    if (found is null) { liveLocalsMatch = false; liveReport.Add($"{bone.Name}: not in the live skeleton"); continue; }
    var r = found["r"]!;
    var rotation = new Quaternion(F(r[0]!), F(r[1]!), F(r[2]!), F(r[3]!));
    var match = Near(V3(found["t"]!), bone.Reference.Position, LiveTolerance) && NearQ(rotation, bone.Reference.Rotation, LiveTolerance) &&
        Near(V3(found["s"]!), bone.Reference.Scale, LiveTolerance);
    liveLocalsMatch &= match;
    liveReport.Add($"{bone.Name}: t {found["t"]!.ToJsonString()} r {r.ToJsonString()} vs written t {bone.Reference.Position} r {bone.Reference.Rotation}{(match ? "" : " MISMATCH")}");
}
if (!liveLocalsMatch) failures.Add("the live hair bones don't sit at the written reference");

// Which hair skeleton the player loads now (expected: h9001 from the test mod).
var hairSkeletons = await OnFramework(() =>
{
    var found = new List<string>();
    var trees = new global::Penumbra.Api.IpcSubscribers.GetGameObjectResourceTrees(PluginInterface).Invoke(false, 0);
    if (trees.Length == 0 || trees[0] is not { } tree) return found;
    var stack = new Stack<global::Penumbra.Api.Helpers.ResourceNodeDto>(tree.Nodes);
    while (stack.Count > 0)
    {
        var node = stack.Pop();
        if (node.GamePath?.Contains("skeleton/hair/", StringComparison.OrdinalIgnoreCase) == true) found.Add($"{node.GamePath} -> {node.ActualPath}");
        foreach (var child in node.Children) stack.Push(child);
    }
    return found;
});
if (!hairSkeletons.Any(s => s.Contains($"h{SkeletonId:D4}", StringComparison.OrdinalIgnoreCase))) failures.Add($"the resource tree has no h{SkeletonId:D4} hair skeleton");

new
{
    ok = failures.Count == 0 && readBack && liveLocalsMatch && kaoFrameMatches, readBack, liveLocalsMatch, kaoFrameMatches, failures,
    race, hairSet = (int)set, template = templatePath, mod, gamePath, bytes = sklb.Length, kaoFrame,
    written = bones.Select(x => $"{x.Name} <- {x.Parent} t {x.Reference.Position} r {x.Reference.Rotation}"),
    live = liveReport, hairSkeletons,
}
