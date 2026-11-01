using System.Buffers.Binary;
using System.Text;

namespace InstantEdit.Services.Skeletons;

/// <summary>
/// How a character turns toward what it looks at: whether the back turns from below the chest (so chest, shoulders and
/// arms move together), and the strength of each look-at group, 0 (off) to 1 (the game's).
/// </summary>
internal sealed record HeadTrackingSettings(bool LowerBack = false, float Back = 1, float NeckHead = 1, float Eyes = 1)
{
    private const float Tolerance = .001f;

    public static HeadTrackingSettings Game { get; } = new();

    public bool IsGame => !LowerBack && Near(Back, 1) && Near(NeckHead, 1) && Near(Eyes, 1);

    public HeadTrackingSettings Clamped() => this with { Back = Clamp(Back), NeckHead = Clamp(NeckHead), Eyes = Clamp(Eyes) };

    private static bool Near(float value, float target) => Math.Abs(value - target) < Tolerance;

    private static float Clamp(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1;
}

/// <summary>
/// Edits a body skeleton parameters file for <see cref="HeadTrackingSettings"/>. The game turns three groups: backbone
/// (j_sebo_c, sideways only), base (neck and head) and eyes. The chest bones hang off j_sebo_b and the arms off j_sebo_c,
/// so the game's back turn swings the arms past the chest; Lower back turns j_sebo_b instead. Strengths scale the gains
/// of each group's parameters. Bytes are patched in place: the file keeps its size and every other byte.
/// </summary>
internal static class HeadTrackingEdit
{
    public const string Backbone = "backbone", Base = "base", Eyes = "eyes";
    private const string GameBack = "j_sebo_c", GameBackParent = "j_sebo_b", LowerBack = "j_sebo_b", LowerBackParent = "j_sebo_a";

    /// <summary> <c>chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.skp</c> for 801. </summary>
    public static string GamePath(ushort race) => $"chara/human/c{race:D4}/skeleton/base/b0001/skl_c{race:D4}b0001.skp";

    public static byte[] Apply(byte[] vanilla, HeadTrackingSettings settings)
    {
        settings = settings.Clamped();
        var layout = SkeletonParams.ReadLookAt(vanilla);
        var groups = new[] { Backbone, Base, Eyes }.Select(id => layout.Groups.FirstOrDefault(g => g.Id == id)).ToArray();
        if (groups.Any(g => g is null || g.Elements.Count == 0))
            throw new InvalidDataException("This skeleton has no head tracking groups IE knows.");
        var shared = layout.Groups.SelectMany(g => g.Elements.Select(e => (g.Id, e.ParamIndex))).Distinct()
            .GroupBy(pair => pair.ParamIndex).Any(owners => owners.Count() > 1);
        if (shared)
            throw new InvalidDataException("This skeleton's head tracking groups share settings, so they can't be changed apart.");
        var output = (byte[])vanilla.Clone();
        if (settings.IsGame)
            return output;

        if (settings.LowerBack)
        {
            if (groups[0]!.Elements is not [{ Bone: GameBack, Parent: GameBackParent } element])
                throw new InvalidDataException("The back turn is already changed in this file.");
            WriteName(output, element.BoneOffset, LowerBack);
            WriteName(output, element.ParentOffset, LowerBackParent);
        }
        Scale(output, layout, groups[0]!, settings.Back);
        Scale(output, layout, groups[1]!, settings.NeckHead);
        Scale(output, layout, groups[2]!, settings.Eyes);
        return output;
    }

    private static void Scale(byte[] output, LookAtLayout layout, LookAtGroup group, float strength)
    {
        if (strength == 1)
            return;
        foreach (var index in group.Elements.Select(e => e.ParamIndex).Distinct())
        {
            var param = layout.Params[index];
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(param.GainOffset), param.Gain * strength);
        }
    }

    private static void WriteName(byte[] output, int offset, string name)
    {
        var field = output.AsSpan(offset, SkeletonParams.NameBytes);
        field.Clear();
        Encoding.ASCII.GetBytes(name, field);
    }
}
