namespace InstantEdit.Services.Physics;

/// <summary> The game paths of a race's body skeleton and body physics. </summary>
internal static class PhysicsPaths
{
    /// <summary> <c>chara/human/c0801/skeleton/base/b0001/phy_c0801b0001.phyb</c> for 801. </summary>
    public static string BodyPhysics(ushort race) => $"chara/human/c{race:D4}/skeleton/base/b0001/phy_c{race:D4}b0001.phyb";

    /// <summary> <c>chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb</c> for 801. </summary>
    public static string BodySkeleton(ushort race) => $"chara/human/c{race:D4}/skeleton/base/b0001/skl_c{race:D4}b0001.sklb";
}
