using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// A pose to key into a pose layer, on the clip's skeleton: each bone's local transform, the bones the
/// pose moves away from where the body it was made on puts them, and whether that body had other
/// proportions, so that its positions would reshape this one.
/// </summary>
/// <param name="Missing">Bones of the pose the clip's skeleton lacks, left out.</param>
internal sealed record PoseImport(ImmutableDictionary<string, MotionTransform> Local, ImmutableHashSet<string> Moved, bool OtherProportions,
    int Missing);

/// <summary>
/// Makes poses from posing tools, the live character and other clips into poses on the clip's
/// skeleton. Rotations carry over by bone name. A bone counts as moved when its offset from its parent
/// is away from the body's own; when most bones are, the pose was made on another body, whose
/// positions are best left out (Pose2Idle judges the same way).
/// </summary>
internal static class PoseImports
{
    // A bone sits elsewhere when its offset from its parent is this far from the body's own, in metres,
    // or this share of the offset's length.
    private const float Tolerance = .002f, Share = .03f;
    // A body has other proportions when more than this share of its measured bones sit elsewhere.
    private const float OtherShare = .5f;
    // Bones this near their parent say nothing about proportions.
    private const float ShortBone = .01f;

    /// <summary>
    /// A .pose file's model-space bones on <paramref name="skeleton"/>, made local through its hierarchy.
    /// A bone the file lacks takes the clip's own local transform (<paramref name="fill"/>), so the bones
    /// below it still find their parent. Moved bones and proportions are judged against the skeleton's
    /// rest pose, since the file doesn't say which body it was made on. Only the body's own bones count
    /// as moved (see <see cref="Positional"/>): the others sit elsewhere on other skeleton versions and
    /// with other gear even when nobody moved them.
    /// </summary>
    public static PoseImport FromModel(SkeletonDescription skeleton, IReadOnlyDictionary<string, MotionTransform> model,
        ReadOnlySpan<MotionTransform> fill)
    {
        var count = skeleton.Bones.Length;
        if (fill.Length != count)
            throw new InvalidDataException("The pose does not match the skeleton.");
        var models = new MotionTransform[count];
        var given = new bool[count];
        for (var b = 0; b < count; b++)
        {
            var parent = skeleton.Bones[b].Parent;
            if (model.TryGetValue(skeleton.Bones[b].Name, out var value))
            {
                models[b] = value;
                given[b] = true;
            }
            else
                models[b] = parent < 0 ? fill[b] : MotionEvaluator.Compose(models[parent], fill[b]);
        }
        var local = ImmutableDictionary.CreateBuilder<string, MotionTransform>(StringComparer.Ordinal);
        var moved = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        int measured = 0, elsewhere = 0;
        for (var b = 0; b < count; b++)
        {
            if (!given[b])
                continue;
            var bone = skeleton.Bones[b];
            var value = bone.Parent < 0 ? models[b] : MotionEvaluator.Relative(models[bone.Parent], models[b]);
            local[bone.Name] = value;
            var away = Away(value.Position, bone.Reference.Position);
            if (away && Positional(bone.Name))
                moved.Add(bone.Name);
            if (Measures(bone))
            {
                measured++;
                if (away)
                    elsewhere++;
            }
        }
        var names = MotionEvaluator.Index(skeleton);
        return new PoseImport(local.ToImmutable(), moved.ToImmutable(), Other(measured, elsewhere), model.Keys.Count(name => !names.ContainsKey(name)));
    }

    /// <summary>
    /// Local transforms of another skeleton's bones (a frame of another clip, the GPose character's live
    /// pose) on <paramref name="skeleton"/>, matched by name; <c>RestOffset</c> is each bone's offset from its
    /// parent at rest on that skeleton. Moved bones are judged against that rest pose, the proportions
    /// by comparing it with this skeleton's.
    /// </summary>
    public static PoseImport FromLocal(SkeletonDescription skeleton, IEnumerable<(string Name, MotionTransform Local, Vector3 RestOffset)> bones)
    {
        var index = MotionEvaluator.Index(skeleton);
        var local = ImmutableDictionary.CreateBuilder<string, MotionTransform>(StringComparer.Ordinal);
        var moved = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        int measured = 0, elsewhere = 0, missing = 0;
        foreach (var (name, value, rest) in bones)
        {
            if (!index.TryGetValue(name, out var at))
            {
                missing++;
                continue;
            }
            if (local.ContainsKey(name))
                continue;
            local[name] = value;
            if (Away(value.Position, rest))
                moved.Add(name);
            var bone = skeleton.Bones[at];
            if (Measures(bone))
            {
                measured++;
                if (Away(rest, bone.Reference.Position))
                    elsewhere++;
            }
        }
        return new PoseImport(local.ToImmutable(), moved.ToImmutable(), Other(measured, elsewhere), missing);
    }

    /// <summary>
    /// Whether a pose file's position for a bone can be trusted as a move: the body's place (n_hara) and
    /// the body's j_ bones, but not weapon holsters, skirts, hair, extras, the face and the ears. A survey
    /// of 8,897 poses (2026-10-06) found those, the IVCS bones and the other nodes sitting elsewhere in
    /// nearly every file made on another skeleton version or with other gear.
    /// </summary>
    public static bool Positional(string bone) => bone == "n_hara" || bone.StartsWith("j_", StringComparison.Ordinal) &&
        !(bone.StartsWith("j_buki", StringComparison.Ordinal) || bone.StartsWith("j_sk_", StringComparison.Ordinal) ||
          bone.StartsWith("j_kami", StringComparison.Ordinal) || bone.StartsWith("j_ex_", StringComparison.Ordinal) ||
          bone.StartsWith("j_f_", StringComparison.Ordinal) || bone.StartsWith("j_mimi", StringComparison.Ordinal) ||
          bone.StartsWith("j_zera", StringComparison.Ordinal));

    private static bool Away(Vector3 position, Vector3 rest) => Vector3.Distance(position, rest) > MathF.Max(Tolerance, Share * rest.Length());

    /// <summary>
    /// Whether a bone's offset tells the body's proportions: a long enough one, but not the body's own
    /// place (n_hara), which poses move freely, nor the skeleton's other root nodes.
    /// </summary>
    private static bool Measures(SkeletonBone bone) => bone.Parent >= 0 && bone.Reference.Position.Length() >= ShortBone &&
                                                        bone.Name is not ("n_hara" or "n_throw" or "n_hara_noanim_trans");

    private static bool Other(int measured, int elsewhere) => measured > 0 && elsewhere > measured * OtherShare;
}
