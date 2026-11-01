using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Physics;

namespace InstantEdit.Services.Skeletons;

/// <summary>A physics chain of a hair rig: its kind, its rig bones from root to tip, and its tip in game model space.</summary>
internal sealed record HairRigChain(string Kind, ImmutableArray<string> Bones, Vector3 Tip);

/// <summary>
/// The hair rig of a custom hair skeleton an export made, read back from its skeleton and physics files, so
/// the armature Blender builds for the hair can be exported with it again.
/// </summary>
internal sealed record HairSkeletonRig(string Race, ImmutableArray<HairRigChain> Chains)
{
    public HairSkeletonRigPayload ToPayload() => new(Race,
        Chains.Select(chain => new HairSkeletonRigChainPayload(chain.Kind, chain.Bones, [chain.Tip.X, chain.Tip.Y, chain.Tip.Z])).ToArray());
}

/// <summary>The <c>hairRig</c> field of a skeleton payload. See Blender-Addon/instant_edit/hair_rig.py.</summary>
public sealed record HairSkeletonRigPayload(
    [property: JsonPropertyName("race")] string Race,
    [property: JsonPropertyName("chains")] IReadOnlyList<HairSkeletonRigChainPayload> Chains);

public sealed record HairSkeletonRigChainPayload(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("bones")] IReadOnlyList<string> Bones,
    [property: JsonPropertyName("tip")] float[] Tip);

internal static partial class HairSkeletonRigs
{
    [GeneratedRegex(@"^chara/human/(c[0-9]{4})/skeleton/hair/h([0-9]{4})/skl_\1h\2\.sklb$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HairSkeletonPath();

    [GeneratedRegex("^j_ex_hair_[0-9]{2}_[a-z]$", RegexOptions.CultureInvariant)]
    private static partial Regex RigBoneName();

    /// <summary>
    /// The race, ID and physics file of the custom hair skeleton among <paramref name="skeleton"/>'s files:
    /// a hair skeleton whose bones include rig bones (<c>j_ex_hair_</c>), which only exports write. Null for none.
    /// </summary>
    public static (string Race, int Id, string PhysicsPath)? Custom(ModelSkeleton skeleton)
    {
        if (!skeleton.Bones.Any(bone => RigBoneName().IsMatch(bone.Name)))
            return null;
        foreach (var path in skeleton.Skeletons)
        {
            if (HairSkeletonPath().Match(path.Replace('\\', '/')) is not { Success: true } match)
                continue;
            var race = match.Groups[1].Value.ToLowerInvariant();
            var id = int.Parse(match.Groups[2].Value);
            return (race, id, HairPhysicsBuilder.PhysicsPath(int.Parse(race.AsSpan(1)), id));
        }
        return null;
    }

    /// <summary>
    /// The rig of a custom hair skeleton for <paramref name="race"/>: every chain of its physics file over the
    /// skeleton's rig bones, with the kind the last export recorded for its first bone (<paramref name="kinds"/>),
    /// else the kind its simulator's group stands for (back for the group back hair and ponytails share), and its
    /// tip where the chain's last bone offset puts it. A chain over other bones is left out with a warning.
    /// </summary>
    public static HairSkeletonRig Read(ModelSkeleton skeleton, string race, PhybFile physics,
        IReadOnlyDictionary<string, string>? kinds, ICollection<string> warnings)
    {
        var model = RigidModelSpace(skeleton.Bones);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < skeleton.Bones.Length; i++)
            index.TryAdd(skeleton.Bones[i].Name, i);
        var chains = new List<HairRigChain>();
        var skipped = 0;
        foreach (var simulator in physics.Simulators)
            foreach (var chain in simulator.Chains)
            {
                var bones = chain.Nodes.Where(node => !node.Bone.IsEmpty).Select(node => node.Bone.Text).ToImmutableArray();
                var kind = bones.IsEmpty ? null : kinds is not null && kinds.TryGetValue(bones[0], out var recorded) ? recorded : GroupKind(simulator.Group);
                if (kind is null || bones.IsEmpty || !bones.All(name => RigBoneName().IsMatch(name) && index.ContainsKey(name)))
                {
                    skipped++;
                    continue;
                }
                var last = model[index[bones[^1]]];
                chains.Add(new HairRigChain(kind, bones, last.Position + Vector3.Transform(chain.LastBoneOffset, last.Rotation)));
            }
        if (skipped > 0)
            warnings.Add($"The hair physics has {skipped} chain{(skipped == 1 ? "" : "s")} that the hair rig can't take, " +
                         "so exporting it leaves them out.");
        return new HairSkeletonRig(race, [.. chains.OrderBy(chain => chain.Bones[0], StringComparer.Ordinal)]);
    }

    /// <summary>The chain kind a hair simulator's PhysicsGroup row stands for (see <see cref="HairPhysicsPresets.Group"/>).</summary>
    private static string? GroupKind(byte group) => group switch
    {
        3 => "bangs",
        23 => "back",
        24 or 25 => "side",
        _ => null,
    };

    // Rotations and positions alone, as the merge and Blender's rest bones compose them.
    private static (Vector3 Position, Quaternion Rotation)[] RigidModelSpace(ImmutableArray<AnimationTakeBone> bones)
    {
        var result = new (Vector3 Position, Quaternion Rotation)[bones.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            var reference = bones[i].Reference;
            var rotation = Quaternion.Normalize(reference.Rotation);
            var parent = bones[i].Parent;
            result[i] = parent < 0
                ? (reference.Position, rotation)
                : (result[parent].Position + Vector3.Transform(reference.Position, result[parent].Rotation),
                    Quaternion.Normalize(result[parent].Rotation * rotation));
        }
        return result;
    }
}
