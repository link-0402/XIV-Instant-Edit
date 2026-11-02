using System.Numerics;
using InstantEdit.Services.NeckSeam;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The seam checks bucket every triangle of the character's models, user mods included. A vertex at
/// infinity or far out must not make that never end.
/// </summary>
internal static class SeamSpaceScenarios
{
    public static void Run()
    {
        // The left half of a triangle below zero, one corner at +infinity: the cell span wrapped around as an int.
        Vector3[] positions = [new(-0.05f, 0, 0), new(0.05f, 0, 0), new(float.PositiveInfinity, 0.02f, 0.02f),
                               new(0, 0, 0), new(0.02f, 0, 0), new(0, 0.02f, 0)];
        int[] triangles = [0, 1, 2, 3, 4, 5];

        var build = Task.Run(() => new SeamSpace(positions, triangles));
        var built = build.Wait(TimeSpan.FromSeconds(10));
        Require(built, "a triangle with a corner at infinity is left out instead of bucketed forever");
        if (!built)
            return;
        var space = build.Result;

        var query = Task.Run(() => (Near: space.Nearest(new Vector3(0.005f, 0.005f, 0), 0.05f),
                                    Lost: space.Nearest(new Vector3(float.PositiveInfinity, 0, 0), 0.05f),
                                    Ray: space.Hits(new Vector3(0.005f, 0.005f, 1), new Vector3(0, 0, -1), 2f),
                                    BadRay: space.Hits(new Vector3(float.PositiveInfinity, 0, 0), new Vector3(0, 0, -1), 2f)));
        var answered = query.Wait(TimeSpan.FromSeconds(10));
        Require(answered, "queries from a point at infinity answer at once");
        if (!answered)
            return;
        Require(query.Result.Near is { Triangle: 1 } && query.Result.Lost is null && query.Result.Ray && !query.Result.BadRay,
            "the finite triangle next to it is still found, and a point at infinity finds nothing");
    }
}
