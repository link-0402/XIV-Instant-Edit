using System.Buffers.Binary;
using System.Text.RegularExpressions;
using InstantEdit.Models;
using InstantEdit.Services.Painter;
using InstantEdit.Services.Previews;

namespace InstantEdit.Services.CharacterSend;

/// <summary> A model a send takes, and what the game draws of it now. </summary>
/// <param name="AttributeMasks">The enabled attributes of each copy of the file the character draws; empty when unknown, and then every part counts as drawn.</param>
/// <param name="Shapes">The shape keys the game has on, in the model's shape order; null when unknown.</param>
internal sealed record CharacterDrawnModel(CharacterSendModel Model, IReadOnlyList<uint> AttributeMasks, uint? Shapes);

/// <summary> A model of the character's list that a send leaves out, and why. </summary>
internal sealed record CharacterLeftOutModel(string FileName, string Reason);

/// <summary> What a send takes of the character as the game draws it now. </summary>
/// <param name="Missing">Files the character draws in its own slots that the On Screen list lacks, because it changed since the list was made.</param>
/// <param name="Known">Whether the draw state was read; without it every model and part of the list goes.</param>
internal sealed record CharacterDrawPlan(IReadOnlyList<CharacterDrawnModel> Models, IReadOnlyList<CharacterLeftOutModel> LeftOut,
    IReadOnlyList<string> Missing, bool Known)
{
    /// <summary> Files the character draws in its own slots from outside the installed mods, which the On Screen list leaves out and a send can't take. </summary>
    public IReadOnlyList<string> External { get; init; } = [];
}

/// <summary>
/// Narrows a send to what the game draws on the character right now: the models its draw object
/// holds, each with the attributes and shape keys the game has on. Of what the draw object holds,
/// the game never shows its low-poly whole body (<c>chara/human/c####/obj/body/b0003</c>), and
/// gear in a body slot carries its own body, so smallclothes of that slot loaded next to it aren't
/// shown either. Dalamud-free, so it can be tested without the game.
/// </summary>
internal static class CharacterDrawState
{
    public const string LowPolyReason = "the low-poly body the game never shows";
    public const string SmallclothesReason = "smallclothes under the gear in its slot";
    public const string NotDrawnReason = "not drawn any more";
    public const string NoPartReason = "every part is turned off";

    private static readonly Regex LowPolyBody = new(@"^chara/human/c\d{4}/obj/body/b0003/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Equipment = new(@"^chara/equipment/e(?<set>\d{4})/model/c\d{4}e\d{4}_(?<slot>met|top|glv|dwn|sho)\.mdl$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const int FileHeaderSize = 68;
    private const int VertexDeclarationSize = 17 * 8;
    private const int StringHeaderSize = 8;
    private const int MeshHeaderSize = 56;
    private const int ElementIdSize = 32;
    private const int LodSize = 60;
    private const int ExtraLodSize = 40;
    private const int MeshSize = 36;
    private const int TerrainShadowMeshSize = 20;
    private const int SubmeshSize = 16;

    /// <param name="models">The send's models from the On Screen list (<see cref="CharacterSendPlan.Models"/>).</param>
    /// <param name="roots">The whole list, to tell which drawn models it lacks.</param>
    /// <param name="live">What the character draws now; null when it couldn't be read.</param>
    /// <param name="external">Whether a drawn file comes from outside the installed mods (a temporary mod, say), which the list leaves out.</param>
    public static CharacterDrawPlan Plan(IReadOnlyList<CharacterSendModel> models, IEnumerable<ResourceNode> roots,
        PainterLiveCharacter? live, Func<string, bool>? external = null)
    {
        if (live is null)
            return new CharacterDrawPlan(models.Select(model => new CharacterDrawnModel(model, [], null)).ToArray(), [], [], false);

        var drawn = new List<CharacterDrawnModel>();
        var leftOut = new List<CharacterLeftOutModel>();
        foreach (var model in models)
        {
            if (IsLowPolyBody(model.Node.GamePath))
            {
                leftOut.Add(new CharacterLeftOutModel(model.FileName, LowPolyReason));
                continue;
            }
            var copies = Copies(live, model.Node, model.Role).ToList();
            if (copies.Count == 0)
            {
                leftOut.Add(new CharacterLeftOutModel(model.FileName, NotDrawnReason));
                continue;
            }
            drawn.Add(new CharacterDrawnModel(model, copies.Select(copy => copy.EnabledAttributes).Distinct().ToArray(),
                copies.Aggregate(0u, (shapes, copy) => shapes | copy.EnabledShapes)));
        }

        // Every gear model draws its own body, so smallclothes of a slot that holds gear are left over.
        var gearSlots = drawn.Select(model => EquipmentSlot(model.Model.Node.GamePath))
            .Where(slot => slot is { Smallclothes: false })
            .Select(slot => slot!.Value.Slot)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shown = new List<CharacterDrawnModel>();
        foreach (var model in drawn)
        {
            if (EquipmentSlot(model.Model.Node.GamePath) is { Smallclothes: true } slot && gearSlots.Contains(slot.Slot))
                leftOut.Add(new CharacterLeftOutModel(model.Model.FileName, SmallclothesReason));
            else
                shown.Add(model);
        }

        var listed = Flatten(roots)
            .Where(node => node.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            .SelectMany(node => new[] { PainterVisibility.NormalizePath(node.ActualPath), PainterVisibility.NormalizePath(node.GamePath) })
            .ToHashSet(StringComparer.Ordinal);
        // Only the character's own slots: besides weapons, its draw object can hold others that aren't listed.
        var unlisted = live.Models.Where(copy => copy.Slot >= 0 && !listed.Contains(copy.Path)).Select(copy => copy.Path)
            .Distinct(StringComparer.Ordinal).ToLookup(path => external?.Invoke(path) == true);
        string[] Names(IEnumerable<string> paths) => paths.Select(CharacterSendPlan.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new CharacterDrawPlan(shown, leftOut, Names(unlisted[false]), true) { External = Names(unlisted[true]) };
    }

    /// <summary> The game's low-poly whole body, which characters load but the game never shows. </summary>
    public static bool IsLowPolyBody(string gamePath) => LowPolyBody.IsMatch(PathRules.NormalizeGamePath(gamePath));

    /// <summary>
    /// The attribute masks of the model's LOD-0 parts, one per part the add-on imports as an object
    /// (a submesh with indices, in a mesh with vertices); null when the file can't be read, or is a
    /// pre-Dawntrail model, which the add-on doesn't import.
    /// </summary>
    public static IReadOnlyList<uint>? PartMasks(byte[] bytes)
    {
        try
        {
            if (U32(bytes, 0) != ModelInfo.V6)
                return null;
            var declarationCount = U16(bytes, 12);
            var stringHeader = checked(FileHeaderSize + declarationCount * VertexDeclarationSize);
            var meshHeader = checked(stringHeader + StringHeaderSize + (int)U32(bytes, stringHeader + 4));
            var meshCount = U16(bytes, meshHeader + 4);
            var attributeCount = U16(bytes, meshHeader + 6);
            var submeshCount = U16(bytes, meshHeader + 8);
            var elementIdCount = U16(bytes, meshHeader + 24);
            var terrainShadowMeshCount = bytes[meshHeader + 26];
            var flags2 = bytes[meshHeader + 27];
            var lodTable = checked(meshHeader + MeshHeaderSize + elementIdCount * ElementIdSize);
            var lodMesh = U16(bytes, lodTable);
            var lodMeshCount = U16(bytes, lodTable + 2);
            var meshTable = checked(lodTable + 3 * LodSize + ((flags2 & 0x10) != 0 ? 3 * ExtraLodSize : 0));
            var submeshTable = checked(meshTable + meshCount * MeshSize + attributeCount * 4 + terrainShadowMeshCount * TerrainShadowMeshSize);
            var masks = new List<uint>();
            for (var m = lodMesh; m < Math.Min(meshCount, lodMesh + lodMeshCount); m++)
            {
                var mesh = meshTable + m * MeshSize;
                if (U16(bytes, mesh) == 0)
                    continue;
                int first = U16(bytes, mesh + 10), count = U16(bytes, mesh + 12);
                for (var s = first; s < Math.Min(submeshCount, first + count); s++)
                {
                    var submesh = submeshTable + s * SubmeshSize;
                    if (U32(bytes, submesh + 4) > 0)
                        masks.Add(U32(bytes, submesh + 8));
                }
            }
            return masks;
        }
        catch (Exception e) when (e is ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary> How many of the parts the game draws under <paramref name="masks"/> (none: all), and how many it hides. </summary>
    public static (int Drawn, int Hidden) Count(IReadOnlyList<uint> parts, IReadOnlyList<uint> masks)
    {
        var drawn = parts.Count(part => masks.Count == 0 || part == 0 || masks.Any(mask => (part & ~mask) == 0));
        return (drawn, parts.Count - drawn);
    }

    private static IEnumerable<PainterLiveModel> Copies(PainterLiveCharacter live, ResourceNode node, CharacterModelRole role)
    {
        var actual = PainterVisibility.NormalizePath(node.ActualPath);
        var game = PainterVisibility.NormalizePath(node.GamePath);
        return live.Models.Where(copy => (role == CharacterModelRole.Weapon ? copy.Slot < 0 : copy.Slot >= 0) &&
                                         (copy.Path == actual || copy.Path == game));
    }

    private static (string Slot, bool Smallclothes)? EquipmentSlot(string gamePath)
        => Equipment.Match(PathRules.NormalizeGamePath(gamePath)) is { Success: true } match
            ? (match.Groups["slot"].Value, match.Groups["set"].Value == "0000")
            : null;

    private static IEnumerable<ResourceNode> Flatten(IEnumerable<ResourceNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
                yield return child;
        }
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
}
