using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Physics;

/// <summary> A body bone: its parent's name (empty for a root) and its rest place relative to the parent. </summary>
internal sealed record PhysicsBone(string Name, string Parent, Vector3 Position, Quaternion Rotation);

/// <summary>
/// The body skeleton a physics file is built for: the bones of the character's body skeleton with
/// their rest poses, and the names of every bone the character has (face, hair and gear skeletons
/// too), since a physics file may name those. Pure data, so it can be built from a live character or
/// from skeleton files and tested without the game.
/// </summary>
internal sealed class PhysicsSkeleton
{
    private readonly Dictionary<string, PhysicsBone> byName;
    private readonly HashSet<string> allNames;
    private readonly Dictionary<string, string> otherParents;

    /// <param name="otherParents"> Parents of the other skeletons' bones, by bone name; their names count as bones too. </param>
    public PhysicsSkeleton(IReadOnlyList<PhysicsBone> body, IEnumerable<string>? otherBones = null, IReadOnlyDictionary<string, string>? otherParents = null)
    {
        Body = body.ToImmutableArray();
        byName = new Dictionary<string, PhysicsBone>(StringComparer.Ordinal);
        foreach (var bone in Body)
            byName.TryAdd(bone.Name, bone);
        allNames = new HashSet<string>(byName.Keys, StringComparer.Ordinal);
        if (otherBones != null)
            allNames.UnionWith(otherBones);
        this.otherParents = otherParents != null ? new Dictionary<string, string>(otherParents, StringComparer.Ordinal) : new(StringComparer.Ordinal);
        allNames.UnionWith(this.otherParents.Keys);
        Family = FamilyOf(byName.Keys);
    }

    public ImmutableArray<PhysicsBone> Body { get; }

    /// <summary> Vanilla, IVCS, YAS, NFLB or NFLB + YAS, from the bones skeleton mods add. </summary>
    public string Family { get; }

    public int Count => Body.Length;

    /// <summary> The body skeleton has this bone. </summary>
    public bool HasBody(string bone) => byName.ContainsKey(bone);

    /// <summary> Any of the character's skeletons has this bone. </summary>
    public bool Has(string bone) => allNames.Contains(bone);

    public PhysicsBone? Get(string bone) => byName.GetValueOrDefault(bone);

    /// <summary> The parent's name of a bone of any of the character's skeletons, the body's first; empty for a root or a bone it lacks. </summary>
    public string ParentOf(string bone)
        => byName.TryGetValue(bone, out var known) ? known.Parent : otherParents.GetValueOrDefault(bone) ?? string.Empty;

    /// <summary> A body skeleton read from a skeleton file or a live character. </summary>
    public static PhysicsSkeleton From(SkeletonDescription body, IEnumerable<string>? otherBones = null, IReadOnlyDictionary<string, string>? otherParents = null)
    {
        var bones = new List<PhysicsBone>(body.Bones.Length);
        foreach (var bone in body.Bones)
            bones.Add(new PhysicsBone(bone.Name, ParentName(body, bone.Parent), bone.Reference.Position, bone.Reference.Rotation));
        return new PhysicsSkeleton(bones, otherBones, otherParents);
    }

    /// <summary> The name of the bone at <paramref name="parent"/>, or empty for a root. </summary>
    internal static string ParentName(SkeletonDescription skeleton, int parent)
        => parent >= 0 && parent < skeleton.Bones.Length ? skeleton.Bones[parent].Name : string.Empty;

    /// <summary> Names a skeleton by the bones skeleton mods add: iv_ (IVCS), ya_ (YAS) and nf_ (NFLB). </summary>
    internal static string FamilyOf(IEnumerable<string> bones)
    {
        bool ivcs = false, yas = false, nflb = false;
        foreach (var bone in bones)
        {
            ivcs |= bone.StartsWith("iv_", StringComparison.Ordinal);
            yas |= bone.StartsWith("ya_", StringComparison.Ordinal);
            nflb |= bone.StartsWith("nf_", StringComparison.Ordinal);
        }
        return (nflb, yas, ivcs) switch
        {
            (true, true, _) => "NFLB + YAS",
            (true, false, _) => "NFLB",
            (false, true, _) => "YAS",
            (false, false, true) => "IVCS",
            _ => "Vanilla",
        };
    }
}
