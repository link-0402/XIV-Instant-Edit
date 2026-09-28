using System.Collections.Immutable;

namespace InstantEdit.Services.Animations;

/// <summary>
/// Finds bones a recording shows scaled beyond what the game scales by itself. The recorder pauses a
/// character's own Customize+ profile, but Customize+ doesn't report temporary profiles (Brio's
/// actors, Mare's synced players), and its template editor or a pose tool can scale bones as well.
/// Their scaling still shows up here, so the user can tell MagicFit's Customize+ would add it again.
/// </summary>
internal static class RecordingScale
{
    /// <summary>How far a scale may differ from the reference pose before it counts; Customize+ edits are larger.</summary>
    public const float Tolerance = 0.01f;

    /// <summary>
    /// Names of the <paramref name="candidates"/> whose scale in <paramref name="frame"/>, one recorded
    /// frame of local transforms, differs from their reference pose, in take order. The breasts and
    /// the tail (bones at or below j_mune_l, j_mune_r and n_sippo_*) are left out, since the game
    /// scales them itself for the bust size and tail length.
    /// </summary>
    public static IReadOnlyList<string> ScaledBones(ImmutableArray<AnimationTakeBone> bones, IEnumerable<int> candidates,
        ReadOnlySpan<float> frame)
    {
        if (frame.Length != bones.Length * AnimationTake.Stride)
            throw new ArgumentException("The frame does not match the take's bones.", nameof(frame));
        // Parents come before their bones, so each bone can look at its parent's answer.
        var gameScaled = new bool[bones.Length];
        for (var i = 0; i < bones.Length; i++)
            gameScaled[i] = ScaledByGame(bones[i].Name) || bones[i].Parent >= 0 && gameScaled[bones[i].Parent];
        var scaled = new List<string>();
        foreach (var bone in candidates.Distinct().Order())
        {
            if (bone < 0 || bone >= bones.Length || gameScaled[bone]) continue;
            var reference = bones[bone].Reference.Scale;
            var scale = frame.Slice(bone * AnimationTake.Stride + 7, 3);
            if (Differs(scale[0], reference.X) || Differs(scale[1], reference.Y) || Differs(scale[2], reference.Z))
                scaled.Add(bones[bone].Name);
        }
        return scaled;
    }

    /// <summary>The first few of <paramref name="names"/> for a message, and how many more there are.</summary>
    public static string Describe(IReadOnlyList<string> names, int shown = 4) =>
        string.Join(", ", names.Take(shown)) + (names.Count > shown ? $" and {names.Count - shown} more" : "");

    /// <summary>What to tell the user about the <paramref name="bones"/> a recording shows scaled, as <see cref="Describe"/> wrote them.</summary>
    public static string Warning(string bones, bool keyScale) =>
        $"Something the recorder can't pause scaled {bones}: Customize+ from Brio or Mare, the Customize+ editor, " +
        "or a pose tool. If it's Customize+, turn MagicFit's Customize+ off for this recording" +
        (keyScale ? "." : " and send it again with Key bone scale on.");

    private static bool ScaledByGame(string name) =>
        name is "j_mune_l" or "j_mune_r" || name.StartsWith("n_sippo_", StringComparison.Ordinal);

    // Mod skeletons hide bones by scaling their reference to nothing; there is nothing to compare with.
    private static bool Differs(float scale, float reference) =>
        Math.Abs(reference) > 1e-6f && Math.Abs(scale / reference - 1) > Tolerance;
}
