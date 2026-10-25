using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Previews;

namespace InstantEdit.Services.Animations;

/// <summary>
/// What the live character has for fingertips: whether its skeleton holds each hand's four tip bones and
/// whether the drawn hand model is weighted to them. <paramref name="Problem"/> is set when it couldn't be read.
/// </summary>
internal sealed record HandTipFacts(bool LiveLeft, bool LiveRight, bool MeshLeft, bool MeshRight, string? Problem);

/// <summary>
/// The fingertip bones (iv_hito, iv_naka, iv_kusu and iv_ko _c) exist only on IVCS and YAS skeletons. A hand shape
/// layer may bend them only when the clip's skeleton, the live skeleton and the drawn hand model all have them.
/// </summary>
internal static class HandTipCheck
{
    private static readonly string[] TipNames = ["iv_hito_c", "iv_naka_c", "iv_kusu_c", "iv_ko_c"];

    private static string[] Tips(MotionSide side) => TipNames.Select(name => name + (side == MotionSide.Left ? "_l" : "_r")).ToArray();

    /// <summary>Whether all four tip bones of a hand are among <paramref name="bones"/>.</summary>
    public static bool SkeletonHas(IEnumerable<string> bones, MotionSide side)
    {
        var have = bones.ToHashSet(StringComparer.Ordinal);
        return Tips(side).All(have.Contains);
    }

    /// <summary>Whether some vertex of the model has more than 1% weight on each of a hand's four tip bones.</summary>
    public static bool MeshWeighted(ModelMesh mesh, MotionSide side)
    {
        var tips = Tips(side);
        var weighted = new bool[tips.Length];
        var ids = tips.Select(tip => mesh.Bones.ToList().IndexOf(tip)).ToArray();
        if (ids.Any(id => id < 0))
            return false;
        foreach (var skin in mesh.Meshes.Select(part => part.Skin).OfType<ModelSkin>())
            for (var slot = 0; slot < skin.Bones.Length && slot < skin.Weights.Length; slot++)
                if (skin.Weights[slot] > .01f && Array.IndexOf(ids, skin.Bones[slot]) is var tip and >= 0)
                    weighted[tip] = true;
        return weighted.All(w => w);
    }

    /// <summary>The line saying which check failed first (clip, live skeleton, hand model), or null when all pass.</summary>
    public static string? Reason(bool clipHasTips, bool live, bool mesh) =>
        !clipHasTips ? "Fingertips: the clip's skeleton has no tip bones. Repair it onto IVCS or YAS to bend them." :
        !live ? "Fingertips: your character's skeleton has no tip bones." :
        !mesh ? "Fingertips: your hands' model isn't weighted to tip bones." : null;
}
