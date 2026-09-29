using System.Globalization;

namespace InstantEdit.Models;

/// <summary>What Blender reported after receiving an animation.</summary>
/// <param name="AlignedBoneCount">Armature bones Blender set to inherit scale Aligned, so that they scale the game's way.</param>
public sealed record BlenderAnimationResult(bool Applied, string Action = "", string Armature = "", int Frames = 0,
    double FrameRate = 0, int FrameStart = 0, int FrameEnd = 0, int MatchedBones = 0, int MissingBoneCount = 0,
    IReadOnlyList<string>? MissingBones = null, int AlignedBoneCount = 0)
{
    public string Describe()
    {
        if (!Applied)
            return "Blender received the animation and keys it as soon as it is idle.";
        var text = $"Keyed \"{Action}\" on \"{Armature}\" in Blender: {MatchedBones} bones, frames {FrameStart}–{FrameEnd} at " +
                   $"{FrameRate.ToString("0.##", CultureInfo.InvariantCulture)} fps.";
        if (MissingBoneCount > 0)
        {
            var names = MissingBones ?? [];
            var shown = string.Join(", ", names.Take(4));
            text += $" {MissingBoneCount} bone{(MissingBoneCount == 1 ? " is" : "s are")} not in the armature" +
                    (shown.Length == 0 ? "." : $" ({shown}{(MissingBoneCount > 4 ? ", and more" : "")}).");
        }
        if (AlignedBoneCount > 0)
            text += $" {AlignedBoneCount} bone{(AlignedBoneCount == 1 ? " now inherits" : "s now inherit")} scale Aligned, " +
                    "so they scale the game's way.";
        return text;
    }
}
