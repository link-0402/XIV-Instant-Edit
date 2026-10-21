using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using InstantEdit.Models;
using InstantEdit.Services.PenumbraMods;

namespace InstantEdit.Services.Skeletons;

/// <summary>
/// The <c>hairSkeleton</c> field of an export: a custom hair skeleton for the exported hair model.
/// <see cref="Bones"/> hang from <c>j_kao</c> or an earlier rig bone; <see cref="Chains"/> group them for
/// physics and are carried but not used yet.
/// </summary>
public sealed partial class HairSkeletonRequest
{
    public const int MaximumBones = 64;
    internal const string Root = "j_kao";
    private static readonly string[] ChainKinds = ["bangs", "side", "back", "ponytail"];

    [GeneratedRegex("^j_ex_hair_[0-9]{2}_[a-z]$", RegexOptions.CultureInvariant)]
    private static partial Regex BoneName();

    [JsonPropertyName("race")]
    public string? Race { get; set; }

    [JsonPropertyName("bones")]
    public List<HairSkeletonBoneRequest>? Bones { get; set; }

    [JsonPropertyName("chains")]
    public List<HairSkeletonChainRequest>? Chains { get; set; }

    /// <summary>
    /// Envelope check: a player race, 1-64 uniquely named rig bones each after its parent, finite
    /// model transforms with unit rotations, and chains of known kinds over distinct rig bones, each
    /// running root to tip (every bone the child of the one before and at least 1 mm from it) and ending
    /// in a tip off its last bone.
    /// </summary>
    internal static ResultCode? Validate(HairSkeletonRequest? request)
    {
        if (request is null)
            return null;
        if (EstEntryService.EstRaceCode(request.Race) is null || request.Bones is not { Count: >= 1 and <= MaximumBones })
            return ResultCode.InvalidHairSkeleton;

        var bones = new Dictionary<string, HairSkeletonBoneRequest>(StringComparer.Ordinal);
        foreach (var bone in request.Bones)
        {
            if (bone?.Name is not { } name || !BoneName().IsMatch(name) || bones.ContainsKey(name) ||
                bone.Parent is not { } parent || (parent != Root && !bones.ContainsKey(parent)) || !ValidModel(bone.Model))
                return ResultCode.InvalidHairSkeleton;
            bones.Add(name, bone);
        }

        var chained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chain in request.Chains ?? [])
        {
            if (chain is null || !ChainKinds.Contains(chain.Kind, StringComparer.Ordinal) || chain.Bones is not { Count: > 0 })
                return ResultCode.InvalidHairSkeleton;
            for (var i = 0; i < chain.Bones.Count; i++)
                if (chain.Bones[i] is not { } name || !bones.TryGetValue(name, out var bone) || !chained.Add(name) ||
                    (i > 0 && (bone.Parent != chain.Bones[i - 1] || !Apart(bone.Model!, bones[chain.Bones[i - 1]].Model!))))
                    return ResultCode.InvalidHairSkeleton;
            if (!ValidTip(chain.Tip, bones[chain.Bones[^1]].Model!))
                return ResultCode.InvalidHairSkeleton;
        }
        return null;
    }

    // Translation, rotation x/y/z/w and scale, all finite, the rotation of unit length.
    private static bool ValidModel(float[]? model)
    {
        if (model is not { Length: 10 } || model.Any(value => !float.IsFinite(value)))
            return false;
        var length = MathF.Sqrt(model[3] * model[3] + model[4] * model[4] + model[5] * model[5] + model[6] * model[6]);
        return MathF.Abs(length - 1) <= 1e-3f;
    }

    // Three finite values at least 1 mm from the last bone's model translation.
    private static bool ValidTip(float[]? tip, float[] last)
        => tip is { Length: 3 } && tip.All(float.IsFinite) && Apart(tip, last);

    // At least 1 mm between two points' first three values, so a cone axis between them has a direction.
    private static bool Apart(float[] a, float[] b)
    {
        float x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2];
        return MathF.Sqrt(x * x + y * y + z * z) >= 1e-3f;
    }
}

/// <summary>A rig bone: its name, its parent's name and its rest in game Y-up model space.</summary>
public sealed class HairSkeletonBoneRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("parent")]
    public string? Parent { get; set; }

    /// <summary>10 floats: translation, rotation x/y/z/w, scale.</summary>
    [JsonPropertyName("model")]
    public float[]? Model { get; set; }
}

/// <summary>A physics chain: its kind (bangs, side, back, ponytail), its rig bones from root to tip and its tip.</summary>
public sealed class HairSkeletonChainRequest
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("bones")]
    public List<string>? Bones { get; set; }

    /// <summary>3 floats: the end of the last bone in game model space.</summary>
    [JsonPropertyName("tip")]
    public float[]? Tip { get; set; }
}
