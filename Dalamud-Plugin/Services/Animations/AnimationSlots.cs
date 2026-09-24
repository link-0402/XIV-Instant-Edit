using System.Collections.Immutable;
using System.Text.RegularExpressions;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

/// <summary>
/// Animation families the player can switch between. Each has a numbered set under
/// <c>emote/</c> and one unnumbered base member elsewhere: the standing idles are
/// joined by <c>idle.pap</c>, chair sitting by <c>sit.pap</c>, ground sitting by
/// <c>jmn.pap</c>. The base member is slot 0 of its family. The catalog models emote
/// families and has no notion of a numbered slot at all, so animations are named
/// from their paths here.
/// </summary>
internal static partial class AnimationSlots
{
    private sealed record Family(string Key, string Label, string Prefix, string BaseName);

    private static readonly ImmutableArray<Family> Families =
    [
        new("standing", "Standing Idle", "pose", "idle"),
        new("chair", "Chair Sitting Idle", "s_pose", "sit"),
        new("ground", "Ground Sitting Idle", "j_pose", "jmn"),
    ];

    [GeneratedRegex(@"^(?<root>.+)/(?<dir>[^/]+)/(?<prefix>[A-Za-z_]*pose)(?<slot>\d{2})_(?<state>loop|start)\.pap$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NumberedPath();

    [GeneratedRegex(@"^(?<root>.+)/(?<dir>[^/]+)/(?<name>[A-Za-z0-9_-]+)\.pap$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BasePath();

    public const int MaxIndex = 99;

    /// <summary>
    /// The family a slot belongs to. A prefix the table does not know still has to
    /// be named, and there the timeline disambiguates the sitting rows that report
    /// their transition instead of their loop.
    /// </summary>
    public static string Resolve(string family, ushort timeline) =>
        Families.Any(f => f.Key == family) ? family
        : timeline is 642 or 643 ? "chair" : timeline is 653 or 654 ? "ground" : "standing";

    public static string Label(string family, ushort timeline) =>
        Families.First(f => f.Key == Resolve(family, timeline)).Label;

    public static AnimationSlot? Describe(string papPath)
    {
        if (!AnimationDependencies.SafeGamePath(papPath)) return null;
        if (NumberedPath().Match(papPath) is { Success: true } numbered)
        {
            var index = int.Parse(numbered.Groups["slot"].Value);
            if (index is < 0 or > MaxIndex) return null;
            var prefix = numbered.Groups["prefix"].Value.ToLowerInvariant();
            // An unrecognised prefix keeps its own identity rather than being folded
            // into a family it may have nothing to do with.
            var family = Families.FirstOrDefault(f => f.Prefix == prefix)?.Key ?? prefix;
            return new AnimationSlot(family, index,
                numbered.Groups["state"].Value.Equals("start", StringComparison.OrdinalIgnoreCase));
        }
        if (BasePath().Match(papPath) is { Success: true } baseMatch &&
            Families.FirstOrDefault(f => f.BaseName.Equals(baseMatch.Groups["name"].Value, StringComparison.OrdinalIgnoreCase)) is { } owner)
            return new AnimationSlot(owner.Key, 0, false);
        return null;
    }
}
