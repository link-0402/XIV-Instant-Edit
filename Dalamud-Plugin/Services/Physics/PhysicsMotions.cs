namespace InstantEdit.Services.Physics;

/// <summary>
/// The loops the builder plays on your posed character in GPose so you can watch the physics move:
/// rows of the game's ActionTimeline sheet, played in place on the base slot as Brio does.
/// </summary>
internal static class PhysicsMotions
{
    /// <summary> ActionTimeline normal/walk. </summary>
    public const ushort Walk = 13;

    /// <summary> ActionTimeline normal/run. </summary>
    public const ushort Run = 22;

    /// <summary> The ActionTimeline key each loop has, for checking the rows against the game's sheet. </summary>
    public static readonly IReadOnlyList<(ushort Timeline, string Key)> Keys = [(Walk, "normal/walk"), (Run, "normal/run")];
}
