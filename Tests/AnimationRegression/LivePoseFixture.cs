// Deliberately small shape fixture for the reflection boundary. It does not replace live-module acceptance.
using System.Numerics;
using Newtonsoft.Json;

namespace LivePose.Core
{
    [Flags] public enum TransformComponents { Position = 1, Rotation = 2, Scale = 4 }
    public struct Transform { public Vector3 Position; public Quaternion Rotation; public Vector3 Scale; }
}
namespace LivePose.Game.Posing
{
    public enum PoseInfoSlot { Character, MainHand }
    public record struct BonePoseInfoId(string BoneName, int Partial, PoseInfoSlot Slot);
    public record struct BonePoseTransformInfo(Core.TransformComponents PropagateComponents, BoneIKInfo IKInfo, Core.Transform Transform);
    public struct BoneIKInfo
    {
        public bool Enabled;
        public bool EnforceConstraints;
        public SolverChoice SolverOptions;
        public struct CCDOptions { public int Depth; public int Iterations; }
        public struct TwoJointOptions { public int FirstBone; public int SecondBone; public int EndBone; public Vector3 RotationAxis; }
    }
    public struct SolverChoice
    {
        public int Index { get; private set; }
        public BoneIKInfo.CCDOptions AsT0 { get; private set; }
        public BoneIKInfo.TwoJointOptions AsT1 { get; private set; }
        public static SolverChoice FromT0(BoneIKInfo.CCDOptions value) => new() { Index = 0, AsT0 = value };
        public static SolverChoice FromT1(BoneIKInfo.TwoJointOptions value) => new() { Index = 1, AsT1 = value };
    }
    public sealed class BonePoseInfo
    {
        private readonly List<BonePoseTransformInfo> _stacks = [];
        public IReadOnlyList<BonePoseTransformInfo> Stacks => _stacks;
        public BonePoseInfo Clone() { var result = new BonePoseInfo(); result._stacks.AddRange(_stacks); return result; }
    }
    public sealed class PoseInfo
    {
        private readonly Dictionary<BonePoseInfoId, BonePoseInfo> _poses = [];
        public BonePoseInfo GetPoseInfo(BonePoseInfoId id)
        { if (!_poses.TryGetValue(id, out var info)) _poses[id] = info = new(); return info; }
        public PoseInfo Clone(Predicate<BonePoseInfoId>? filter = null)
        {
            var result = new PoseInfo();
            foreach (var (id, info) in _poses) if (filter == null || filter(id)) result._poses[id] = info.Clone();
            return result;
        }
    }
}
namespace LivePose
{
    public static class LivePose
    {
        public static Config.ConfigurationService? Configuration;
        public static bool TryGetService<T>(out T? service) where T : class
        { service = Configuration as T; return service != null; }
    }
    public sealed class BonePoseData
    {
        public Core.Transform Transform;
        public Core.TransformComponents Propogate;
        public bool IK_Enabled, IK_EnforceConstraints;
        public int IK_Type, IK_Arg0, IK_Arg1, IK_Arg2;
        public Vector3 IK_RotationAxis;
    }
    public sealed class LivePoseBoneEntry
    {
        public Game.Posing.BonePoseInfoId BonePoseInfoId;
        public List<BonePoseData> Stacks = [];
        [JsonExtensionData] public Dictionary<string, Newtonsoft.Json.Linq.JToken>? Extra;
    }
    public sealed class LivePoseCacheEntry
    {
        public ushort TimelineId, SecondaryTimelineId;
        public List<LivePoseBoneEntry> Pose = [];
    }
    public sealed class FixtureCapability
    {
        public Config.CharacterConfiguration CharacterConfiguration { get; } = new() { ContentId = 42 };
        // Nullable is intentional: a regression previously looked up a non-nullable overload.
        public FixtureBone GetBone(Game.Posing.BonePoseInfoId? id) => new(id!.Value.BoneName, id.Value.Partial == 1);
    }
    public sealed class FixtureBone(string name, bool face = false)
    {
        public string Name => name;
        public bool IsFaceBone => face;
        public List<FixtureBone> GetBonesToDepth(int depth, bool hidden, int? partial, List<FixtureBone>? bones) =>
            name == "hand" ? [this, new("forearm"), new("arm")] : [this];
    }
}
namespace LivePose.Config
{
    public sealed class CharacterConfiguration { public ulong ContentId { get; set; } }
    public sealed class ConfigurationService
    {
        public bool Fail;
        public int Saves;
        public ulong SavedContentId;
        public void SaveCharacterConfiguration(ulong id, CharacterConfiguration config)
        {
            if (Fail) throw new IOException("Fixture persistence failure");
            Saves++; SavedContentId = id;
        }
    }
}

namespace LivePose.Entities.Core
{
    public record struct EntityId(string Unique);
    public class Entity;
}
namespace LivePose.Entities
{
    public sealed class EntityManager
    {
        public Core.Entity Player { get; } = new();
        public Core.Entity? GetEntity(Core.EntityId id) => id.Unique == "actor_123" ? Player : null;
        public T? GetEntity<T>(Core.EntityId id) where T : Core.Entity =>
            throw new Exception("The reflection adapter must use the non-generic entity lookup.");
        public Core.Entity? GetEntity(string id) => throw new Exception("Wrong entity ID overload.");
    }
}
