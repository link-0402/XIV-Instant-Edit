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
    private readonly Dictionary<string, PhysicsBone> otherRest = new(StringComparer.Ordinal);

    /// <param name="otherParents"> Parents of the other skeletons' bones, by bone name; their names count as bones too. </param>
    /// <param name="others"> The other skeletons' bones with their rest places; the first one of a name counts. </param>
    public PhysicsSkeleton(IReadOnlyList<PhysicsBone> body, IEnumerable<string>? otherBones = null, IReadOnlyDictionary<string, string>? otherParents = null,
        IEnumerable<PhysicsBone>? others = null)
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
        foreach (var bone in others ?? [])
            if (otherRest.TryAdd(bone.Name, bone))
            {
                allNames.Add(bone.Name);
                this.otherParents.TryAdd(bone.Name, bone.Parent);
            }
        Family = FamilyOf(byName.Keys);
    }

    /// <summary> Every bone name of the character's skeletons. </summary>
    public IEnumerable<string> Names => allNames;

    /// <summary> A bone of any of the character's skeletons with its rest place, the body's first; null when its rest place isn't known. </summary>
    public PhysicsBone? Rest(string bone) => byName.GetValueOrDefault(bone) ?? otherRest.GetValueOrDefault(bone);

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
    public static PhysicsSkeleton From(SkeletonDescription body, IEnumerable<string>? otherBones = null, IReadOnlyDictionary<string, string>? otherParents = null,
        IEnumerable<PhysicsBone>? others = null)
        => new(Bones(body), otherBones, otherParents, others);

    /// <summary> A skeleton's bones with their parents' names and rest places. </summary>
    public static List<PhysicsBone> Bones(SkeletonDescription skeleton)
    {
        var bones = new List<PhysicsBone>(skeleton.Bones.Length);
        foreach (var bone in skeleton.Bones)
            bones.Add(new PhysicsBone(bone.Name, ParentName(skeleton, bone.Parent), bone.Reference.Position, bone.Reference.Rotation));
        return bones;
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
