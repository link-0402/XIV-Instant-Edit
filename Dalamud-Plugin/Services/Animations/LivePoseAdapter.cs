using System.Collections;
using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InstantEdit.Services.Animations;

/// <summary>
/// A shape-checked adapter to the loaded LivePose module, not a second LivePose host.
/// All access is on Dalamud's framework thread. Never cache foreign instances across calls.
/// </summary>
internal sealed class LivePoseAdapter(IDalamudPluginInterface pi, IObjectTable objects)
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private sealed record Host(Assembly Assembly, object Capability, string Identity);
    public string? LastError { get; private set; }

    public PoseSnapshot Capture()
    {
        var host = GetHost();
        var cap = host.Capability;
        var active = ((ushort, ushort))Get(cap, "ActiveBodyTimelines");
        return Read(host, active.Item1, active.Item2, Convert.ToUInt16(Get(cap, "ActiveFaceTimeline")), (bool)Get(cap, "CursedMode"));
    }

    public PoseSnapshot ReadMatching(PoseSnapshot scope)
    {
        var host = GetHost();
        if (host.Identity != scope.AdapterIdentity) throw new InvalidOperationException("The LivePose module changed. Capture the pose again.");
        return Read(host, scope.MainTimeline, scope.UpperTimeline, scope.FaceTimeline, scope.Global);
    }
    public void CheckModule(PoseSnapshot snapshot)
    {
        if (GetHost().Identity != snapshot.AdapterIdentity) throw new InvalidOperationException("The loaded LivePose module changed during baking.");
    }

    public void ValidateRoundTrip(PoseSnapshot snapshot)
    {
        var host = GetHost();
        if (host.Identity != snapshot.AdapterIdentity) throw new InvalidOperationException("LivePose changed. Refresh the capture.");
        var empty = Activator.CreateInstance(host.Assembly.GetType("LivePose.Game.Posing.PoseInfo", true)!)!;
        var rebuilt = BuildPose(host, empty, snapshot with { Bones = [] }, snapshot, null);
        if (!AnimationPoseRules.Same(snapshot, snapshot with { Bones = ReadBones(host, rebuilt) }))
            throw new InvalidDataException("The complete LivePose capture/update round trip failed. Animation edits are disabled for this module.");
        if (!snapshot.Global) _ = PersistentSave(host);
    }

    public bool CompareAndSet(PoseSnapshot before, PoseSnapshot after)
    {
        var host = GetHost();
        var actual = ReadMatching(before);
        if (!AnimationPoseRules.Same(actual, before)) return false;
        var cap = host.Capability;
        var bodyCache = (IDictionary)Get(cap, "BodyPoses");
        var faceCache = (IDictionary)Get(cap, "FacePoses");
        var key = (before.MainTimeline, before.UpperTimeline);
        var active = ((ushort, ushort))Get(cap, "ActiveBodyTimelines");
        var activeFace = Convert.ToUInt16(Get(cap, "ActiveFaceTimeline"));
        var originalActive = Get(cap, "PoseInfo");
        var originalBody = bodyCache.Contains(key) ? bodyCache[key] : null;
        var originalFace = faceCache.Contains(before.FaceTimeline) ? faceCache[before.FaceTimeline] : null;
        var config = Get(cap, "CharacterConfiguration");
        var save = before.Global ? null : PersistentSave(host);
        var configBody = Get(config, "BodyPoses");
        var configFace = Get(config, "FacePoses");
        var poseType = host.Assembly.GetType("LivePose.Game.Posing.PoseInfo", true)!;
        var nextBody = BuildPose(host, originalBody ?? Activator.CreateInstance(poseType)!, before, after, false);
        var nextFace = BuildPose(host, originalFace ?? Activator.CreateInstance(poseType)!, before, after, true);
        var nextActive = BuildPose(host, originalActive, before, after, null);
        // Rebuild and compare all ordered stacks before touching the live module.
        var check = before with { Bones = ReadBones(host, nextActive) };
        if (before.Global || (active == key && activeFace == before.FaceTimeline))
            if (!AnimationPoseRules.Same(check, after)) throw new InvalidDataException("LivePose pose round-trip validation failed.");
        var nextConfigBody = CloneConfigEntries(host, configBody, before, after, false);
        var nextConfigFace = CloneConfigEntries(host, configFace, before, after, true);
        try
        {
            if (before.Global) Set(cap, "PoseInfo", nextActive);
            else
            {
                bodyCache[key] = nextBody;
                faceCache[before.FaceTimeline] = nextFace;
                if (active == key || activeFace == before.FaceTimeline) Call(cap, "ApplyTimelinePose");
                Set(config, "BodyPoses", nextConfigBody);
                Set(config, "FacePoses", nextConfigFace);
                save!();
            }
            if (!AnimationPoseRules.Same(ReadMatching(after), after)) throw new InvalidOperationException("LivePose did not retain the updated offsets.");
            return true;
        }
        catch
        {
            if (originalBody == null) bodyCache.Remove(key); else bodyCache[key] = originalBody;
            if (originalFace == null) faceCache.Remove(before.FaceTimeline); else faceCache[before.FaceTimeline] = originalFace;
            Set(cap, "PoseInfo", originalActive);
            Set(config, "BodyPoses", configBody); Set(config, "FacePoses", configFace);
            save?.Invoke();
            throw;
        }
    }

    private static Action PersistentSave(Host host)
    {
        var config = Get(host.Capability, "CharacterConfiguration");
        var contentId = Convert.ToUInt64(Get(config, "ContentId"));
        if (config.GetType().Name == "NoCharacterConfiguration" || contentId == 0)
            throw new InvalidOperationException("LivePose has no persistent configuration for this player.");
        var serviceType = host.Assembly.GetType("LivePose.Config.ConfigurationService", true)!;
        var configType = host.Assembly.GetType("LivePose.Config.CharacterConfiguration", true)!;
        var lookup = host.Assembly.GetType("LivePose.LivePose", true)!.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "TryGetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
        object?[] args = [null];
        if (lookup.MakeGenericMethod(serviceType).Invoke(null, args) is not true || args[0] == null)
            throw new InvalidOperationException("LivePose's configuration service is unavailable; offsets cannot be saved.");
        var save = serviceType.GetMethod("SaveCharacterConfiguration", [typeof(ulong), configType])
            ?? throw new NotSupportedException("Unsupported LivePose configuration persistence.");
        // CharacterConfiguration.Save silently returns when its service is missing. Resolve the
        // checked service before mutation and invoke the same persistence operation directly.
        var service = args[0];
        return () => save.Invoke(service, [contentId, config]);
    }

    private Host GetHost()
    {
        try
        {
            if (objects.LocalPlayer is not { } player) throw new InvalidOperationException("No local player is available.");
            var version = pi.GetIpcSubscriber<(int, int)>("LivePose.ApiVersion").InvokeFunc();
            if (version.Item1 != 1) throw new InvalidOperationException($"Unsupported LivePose IPC version {version.Item1}.{version.Item2}.");
            var matches = new List<Host>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var module = assembly.GetType("LivePose.LivePose");
                var entityManager = assembly.GetType("LivePose.Entities.EntityManager");
                if (module == null || entityManager == null) continue;
                var method = module.GetMethods(BindingFlags.Public | BindingFlags.Static).SingleOrDefault(m =>
                    m.Name == "TryGetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
                if (method == null) continue;
                object?[] args = [null];
                if (method.MakeGenericMethod(entityManager).Invoke(null, args) is not true || args[0] == null) continue;
                var idType = assembly.GetType("LivePose.Entities.Core.EntityId", true)!;
                var id = Activator.CreateInstance(idType, $"actor_{player.Address}")!;
                var entity = entityManager.GetMethod("GetEntity", [idType])?.Invoke(args[0], [id]);
                if (entity == null) continue;
                var cap = ((IEnumerable)Get(entity, "Capabilities")).Cast<object>().SingleOrDefault(c =>
                    c.GetType().FullName == "LivePose.Capabilities.Posing.SkeletonPosingCapability");
                if (cap == null) continue;
                CheckShape(assembly, cap);
                matches.Add(new Host(assembly, cap, $"livepose-v1:{assembly.ManifestModule.ModuleVersionId:N}"));
            }
            if (matches.Count != 1) throw new InvalidOperationException("A single compatible SimpleHeels LivePose module could not be identified.");
            LastError = null;
            return matches[0];
        }
        catch (Exception e)
        {
            LastError = "LivePose integration unavailable: " + (e.InnerException?.Message ?? e.Message);
            throw new InvalidOperationException(LastError, e);
        }
    }

    private static void CheckShape(Assembly assembly, object cap)
    {
        foreach (var name in new[] { "PoseInfo", "BodyPoses", "FacePoses", "ActiveBodyTimelines", "ActiveFaceTimeline", "CursedMode", "CharacterConfiguration" }) _ = Get(cap, name);
        var pose = Get(cap, "PoseInfo");
        if (Get(pose, "_poses") is not IDictionary) throw new NotSupportedException("Unsupported LivePose pose storage.");
        var components = assembly.GetType("LivePose.Core.TransformComponents", true)!;
        foreach (var (name, value) in new[] { ("Position", 1), ("Rotation", 2), ("Scale", 4) })
            if (Convert.ToInt32(Enum.Parse(components, name)) != value) throw new NotSupportedException("Unsupported LivePose transform flags.");
        if (assembly.GetType("LivePose.Game.Posing.BonePoseInfo", true)!.GetField("_stacks", Instance) == null ||
            assembly.GetType("LivePose.Game.Posing.BoneIKInfo", true)!.GetField("EnforceConstraints") == null)
            throw new NotSupportedException("Unsupported LivePose stack layout.");
        static void Fields(Type type, params string[] expected)
        {
            var actual = type.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name).Order().ToArray();
            if (!actual.SequenceEqual(expected.Order())) throw new NotSupportedException($"Unsupported LivePose state shape: {type.Name}.");
        }
        var ik = assembly.GetType("LivePose.Game.Posing.BoneIKInfo", true)!;
        Fields(ik, "Enabled", "EnforceConstraints", "SolverOptions");
        Fields(ik.GetNestedType("CCDOptions")!, "Depth", "Iterations");
        Fields(ik.GetNestedType("TwoJointOptions")!, "FirstBone", "SecondBone", "EndBone", "RotationAxis");
        Fields(assembly.GetType("LivePose.BonePoseData", true)!, "Transform", "Propogate", "IK_Enabled", "IK_EnforceConstraints",
            "IK_Type", "IK_Arg0", "IK_Arg1", "IK_Arg2", "IK_RotationAxis");
    }

    private static PoseSnapshot Read(Host host, ushort main, ushort upper, ushort face, bool global)
    {
        var cap = host.Capability;
        if ((bool)Get(cap, "CursedMode") != global) throw new InvalidOperationException("The LivePose posing mode changed.");
        ImmutableArray<PoseBone> bones;
        if (global || (((ushort, ushort))Get(cap, "ActiveBodyTimelines") == (main, upper) &&
                Convert.ToUInt16(Get(cap, "ActiveFaceTimeline")) == face)) bones = ReadBones(host, Get(cap, "PoseInfo"));
        else
        {
            var body = (IDictionary)Get(cap, "BodyPoses"); var faces = (IDictionary)Get(cap, "FacePoses");
            bones = (body.Contains((main, upper)) ? ReadBones(host, body[(main, upper)]!).Where(b => !b.Face) : [])
                .Concat(faces.Contains(face) ? ReadBones(host, faces[face]!).Where(b => b.Face) : [])
                .OrderBy(b => b.Id.Partial).ThenBy(b => b.Id.Name, StringComparer.Ordinal).ToImmutableArray();
        }
        return new PoseSnapshot(main, upper, face, global, bones, DateTime.UtcNow, host.Identity);
    }

    private static ImmutableArray<PoseBone> ReadBones(Host host, object pose)
    {
        var result = new List<PoseBone>();
        var poses = (IDictionary)Get(pose, "_poses");
        foreach (DictionaryEntry entry in poses)
        {
            var id = entry.Key;
            var slot = Convert.ToInt32(Get(id, "Slot"));
            if (slot != 0) continue;
            var name = (string)Get(id, "BoneName");
            var partial = Convert.ToInt32(Get(id, "Partial"));
            var stacks = new List<PoseStack>();
            foreach (var stack in (IEnumerable)Get(entry.Value!, "Stacks"))
            {
                var transform = Get(stack, "Transform"); var ik = Get(stack, "IKInfo");
                var options = Get(ik, "SolverOptions"); var type = Convert.ToInt32(Get(options, "Index"));
                var solver = Get(options, type == 0 ? "AsT0" : "AsT1");
                var value = new PoseStack((Vector3)Get(transform, "Position"), (Quaternion)Get(transform, "Rotation"),
                    (Vector3)Get(transform, "Scale"), (PoseComponents)Convert.ToInt32(Get(stack, "PropagateComponents")),
                    new PoseIk((bool)Get(ik, "Enabled"), type, (bool)Get(ik, "EnforceConstraints"),
                        type == 0 ? Convert.ToInt32(Get(solver, "Depth")) : 0,
                        type == 0 ? Convert.ToInt32(Get(solver, "Iterations")) : 0,
                        type == 1 ? Convert.ToInt32(Get(solver, "FirstBone")) : 0,
                        type == 1 ? Convert.ToInt32(Get(solver, "SecondBone")) : 0,
                        type == 1 ? Convert.ToInt32(Get(solver, "EndBone")) : 0,
                        type == 1 ? (Vector3)Get(solver, "RotationAxis") : Vector3.Zero));
                AnimationPoseRules.Validate(value);
                stacks.Add(value);
            }
            if (stacks.Count == 0) continue;
            var getBone = host.Capability.GetType().GetMethod("GetBone", [typeof(Nullable<>).MakeGenericType(id.GetType())])
                ?? throw new NotSupportedException("Unsupported LivePose bone lookup.");
            var bone = getBone.Invoke(host.Capability, [id]) ?? throw new InvalidOperationException($"LivePose bone {name} is unavailable.");
            var chain = (IEnumerable)bone.GetType().GetMethod("GetBonesToDepth")!.Invoke(bone, [4096, true, null, null])!;
            result.Add(new PoseBone(new PoseBoneId(name, partial, slot), stacks.ToImmutableArray(),
                (bool)Get(bone, "IsFaceBone"), chain.Cast<object>().Select(b => (string)Get(b, "Name")).ToImmutableArray()));
        }
        return result.OrderBy(b => b.Id.Partial).ThenBy(b => b.Id.Name, StringComparer.Ordinal).ToImmutableArray();
    }

    private static object BuildPose(Host host, object original, PoseSnapshot before, PoseSnapshot after, bool? face)
    {
        var clone = original.GetType().GetMethod("Clone")!.Invoke(original, [null])!;
        var map = (IDictionary)Get(clone, "_poses");
        var ids = before.Bones.Concat(after.Bones).Where(b => face == null || b.Face == face).Select(b => b.Id).Distinct();
        var idType = host.Assembly.GetType("LivePose.Game.Posing.BonePoseInfoId", true)!;
        var slotType = host.Assembly.GetType("LivePose.Game.Posing.PoseInfoSlot", true)!;
        var stackType = host.Assembly.GetType("LivePose.Game.Posing.BonePoseTransformInfo", true)!;
        var componentType = host.Assembly.GetType("LivePose.Core.TransformComponents", true)!;
        var dataType = host.Assembly.GetType("LivePose.BonePoseData", true)!;
        var ikType = host.Assembly.GetType("LivePose.Game.Posing.BoneIKInfo", true)!;
        foreach (var id in ids)
        {
            var foreignId = Activator.CreateInstance(idType, id.Name, id.Partial, Enum.ToObject(slotType, id.Slot))!;
            var replacement = after.Bones.FirstOrDefault(b => b.Id == id);
            if (replacement == null) { map.Remove(foreignId); continue; }
            var info = clone.GetType().GetMethod("GetPoseInfo", [idType])!.Invoke(clone, [foreignId])!;
            var stacks = (IList)Get(info, "_stacks"); stacks.Clear();
            foreach (var s in replacement.Stacks)
            {
                var data = JsonConvert.DeserializeObject(StackJson(s).ToString(Formatting.None), dataType)!;
                // BonePoseData.BoneIkInfo resets disabled solver options; construct the complete runtime value instead.
                var ik = Activator.CreateInstance(ikType)!;
                Set(ik, "Enabled", s.Ik.Enabled); Set(ik, "EnforceConstraints", s.Ik.EnforceConstraints);
                var solver = Activator.CreateInstance(ikType.GetNestedType(s.Ik.Type == 0 ? "CCDOptions" : "TwoJointOptions")!)!;
                if (s.Ik.Type == 0) { Set(solver, "Depth", s.Ik.Depth); Set(solver, "Iterations", s.Ik.Iterations); }
                else
                {
                    Set(solver, "FirstBone", s.Ik.First); Set(solver, "SecondBone", s.Ik.Second);
                    Set(solver, "EndBone", s.Ik.End); Set(solver, "RotationAxis", s.Ik.Axis);
                }
                var optionsType = ikType.GetField("SolverOptions")!.FieldType;
                Set(ik, "SolverOptions", optionsType.GetMethod(s.Ik.Type == 0 ? "FromT0" : "FromT1", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [solver])!);
                stacks.Add(Activator.CreateInstance(stackType, Enum.ToObject(componentType, (int)s.Propagate),
                    ik, Get(data, "Transform"))!);
            }
        }
        return clone;
    }

    private static object CloneConfigEntries(Host host, object original, PoseSnapshot before, PoseSnapshot after, bool face)
    {
        var copy = (IList)Activator.CreateInstance(original.GetType())!;
        foreach (var item in (IEnumerable)original) copy.Add(item);
        var type = host.Assembly.GetType("LivePose.LivePoseCacheEntry", true)!;
        var primary = face ? before.FaceTimeline : before.MainTimeline;
        var secondary = face ? (ushort)0 : before.UpperTimeline;
        var index = -1;
        for (var i = 0; i < copy.Count; i++)
            if (Convert.ToUInt16(Get(copy[i]!, "TimelineId")) == primary && Convert.ToUInt16(Get(copy[i]!, "SecondaryTimelineId")) == secondary) { index = i; break; }
        // Preserve entries and bone components outside this captured scope, including unknown future fields.
        var json = index >= 0 ? JObject.FromObject(copy[index]!) : new JObject { ["TimelineId"] = primary, ["SecondaryTimelineId"] = secondary, ["Pose"] = new JArray() };
        var list = (JArray)json["Pose"]!;
        var changed = before.Bones.Concat(after.Bones).Select(b => b.Id).Distinct().Where(id =>
        {
            var a = before.Bones.FirstOrDefault(b => b.Id == id);
            var b = after.Bones.FirstOrDefault(b => b.Id == id);
            return a == null || b == null || !a.Stacks.SequenceEqual(b.Stacks);
        }).ToHashSet();
        foreach (var old in before.Bones.Where(b => b.Face == face && changed.Contains(b.Id)))
            foreach (var token in list.OfType<JObject>().Where(b => (string?)b["BonePoseInfoId"]?["BoneName"] == old.Id.Name &&
                (int?)b["BonePoseInfoId"]?["Partial"] == old.Id.Partial && (int?)b["BonePoseInfoId"]?["Slot"] == old.Id.Slot).ToArray()) token.Remove();
        foreach (var b in after.Bones.Where(b => b.Face == face && changed.Contains(b.Id)))
            list.Add(new JObject { ["BonePoseInfoId"] = new JObject { ["BoneName"] = b.Id.Name, ["Partial"] = b.Id.Partial, ["Slot"] = b.Id.Slot },
                ["Stacks"] = new JArray(b.Stacks.Select(StackJson)) });
        if (index >= 0) copy.RemoveAt(index);
        if (list.Count > 0) copy.Add(json.ToObject(type)!);
        return copy;
    }
    private static JObject StackJson(PoseStack s) => new()
    {
        ["Transform"] = new JObject { ["Position"] = JToken.FromObject(s.Position), ["Rotation"] = JToken.FromObject(s.Rotation), ["Scale"] = JToken.FromObject(s.Scale) },
        ["Propogate"] = (int)s.Propagate, ["IK_Enabled"] = s.Ik.Enabled, ["IK_EnforceConstraints"] = s.Ik.EnforceConstraints,
        ["IK_Type"] = s.Ik.Type, ["IK_Arg0"] = s.Ik.Type == 0 ? s.Ik.Depth : s.Ik.First,
        ["IK_Arg1"] = s.Ik.Type == 0 ? s.Ik.Iterations : s.Ik.Second, ["IK_Arg2"] = s.Ik.End, ["IK_RotationAxis"] = JToken.FromObject(s.Ik.Axis),
    };
    private static object Get(object target, string name) => target.GetType().GetProperty(name, Instance)?.GetValue(target) ??
        target.GetType().GetField(name, Instance)?.GetValue(target) ?? throw new MissingMemberException(target.GetType().FullName, name);
    private static void Set(object target, string name, object value)
    {
        if (target.GetType().GetProperty(name, Instance) is { } p) p.SetValue(target, value);
        else if (target.GetType().GetField(name, Instance) is { } f) f.SetValue(target, value);
        else throw new MissingMemberException(target.GetType().FullName, name);
    }
    private static void Call(object target, string name) => target.GetType().GetMethod(name, Type.EmptyTypes)!.Invoke(target, null);
}
