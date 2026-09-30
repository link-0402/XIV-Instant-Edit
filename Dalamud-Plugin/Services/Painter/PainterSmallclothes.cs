using System.Buffers.Binary;
using System.Text.RegularExpressions;
using InstantEdit.Services.Previews;

namespace InstantEdit.Services.Painter;

/// <summary> A body slot the smallclothes (equipment set e0000) cover, as the game numbers and names it. </summary>
/// <param name="Index">The character's model slot, which is also the slot's part in equipment IMC files.</param>
/// <param name="Suffix">The model file suffix: top, glv, dwn or sho.</param>
/// <param name="Part">What the dialog calls the slot.</param>
/// <param name="VariantCode">The letter of the slot's variant attributes (atr_tv_a on the torso, atr_gv_a on the hands).</param>
/// <param name="EquipSlot">Penumbra's EquipSlot value for the slot, as its meta manipulations store it.</param>
internal sealed record PainterBodySlot(int Index, string Suffix, string Part, char VariantCode, byte EquipSlot)
{
    public static readonly PainterBodySlot Body = new(1, "top", "Torso", 't', 4);
    public static readonly PainterBodySlot Hands = new(2, "glv", "Hands", 'g', 5);
    public static readonly PainterBodySlot Legs = new(3, "dwn", "Legs", 'd', 7);
    public static readonly PainterBodySlot Feet = new(4, "sho", "Feet", 's', 8);
    public static readonly IReadOnlyList<PainterBodySlot> All = [Body, Hands, Legs, Feet];

    /// <summary> The slot's EQDP bits: the material flag, then the model flag (Penumbra.GameData EqdpEntry). </summary>
    public int EqdpShift => 2 * Index;

    /// <summary> Penumbra's name for the slot in decoded IMC manipulations. </summary>
    public string EquipSlotName => Index switch { 1 => "Body", 2 => "Hands", 3 => "Legs", _ => "Feet" };

    public string ModelPath(int genderRace) => $"chara/equipment/e0000/model/c{genderRace:D4}e0000_{Suffix}.mdl";
}

/// <summary> The skin material folder the character's body skin comes from, such as chara/human/c0201/obj/body/b0001/material/v0001. </summary>
internal sealed record PainterSkinFolder(string Directory, int Race, int Body);

/// <summary>
/// What the character would wear in the body slots with nothing equipped: the smallclothes, set
/// e0000. Penumbra's rules (ResolveContext.PathResolution, ShapeAttributeManager) pick the model's
/// race by EQDP, the IMC entry's attribute mask turns variant parts on or off, and connector shapes
/// turn on where neighbouring parts both have them. Dalamud-free; the files and manipulations come in.
/// </summary>
internal static partial class PainterSmallclothes
{
    public const string ImcPath = "chara/equipment/e0000/e0000.imc";
    public const int MidlanderMale = 101;

    public static string EqdpPath(int genderRace) => $"chara/xls/charadb/equipmentdeformerparameter/c{genderRace:D4}.eqdp";

    /// <summary> The race whose models a gender-race falls back to (Penumbra.GameData GenderRace.Fallback). </summary>
    public static int Fallback(int genderRace) => genderRace switch
    {
        104 or 201 => MidlanderMale,
        904 or 1501 => 901,
        1104 or 1201 or 1204 => 1101,
        1304 or 1404 => 1301,
        _ when genderRace % 10 == 4 => 104,
        _ when (genderRace / 100 & 1) == 0 => 201,
        _ => MidlanderMale,
    };

    /// <summary>
    /// The race of the smallclothes model a character of <paramref name="genderRace"/> loads in a slot:
    /// its own when its EQDP entry for set 0 has the slot's model flag, else its fallback's when that
    /// has it, else Midlander Male's (Penumbra's ResolveEqdpRaceCode).
    /// </summary>
    /// <param name="entry">The EQDP entry for set 0 of a race, with the collection's manipulations applied.</param>
    public static int ModelRace(int genderRace, PainterBodySlot slot, Func<int, ushort> entry)
    {
        if (genderRace == MidlanderMale)
            return MidlanderMale;
        if (HasModel(entry(genderRace), slot))
            return genderRace;
        var fallback = Fallback(genderRace);
        if (fallback == MidlanderMale)
            return MidlanderMale;
        return HasModel(entry(fallback), slot) ? fallback : MidlanderMale;
    }

    private static bool HasModel(ushort entry, PainterBodySlot slot) => (entry >> (slot.EqdpShift + 1) & 1) != 0;

    /// <summary> A slot's EQDP bits from a manipulation replace the file's (Penumbra's EqdpCache). </summary>
    public static ushort ApplyEqdp(ushort entry, PainterBodySlot slot, ushort manipulation)
    {
        var mask = (ushort)(3 << slot.EqdpShift);
        return (ushort)((entry & ~mask) | (manipulation & mask));
    }

    /// <summary>
    /// A set's entry in a vanilla EQDP file: [id][block size][block count], each block's offset in
    /// entries (collapsed blocks are 0xFFFF and all zero), then the stored entries (Penumbra's ExpandedEqdpFile).
    /// </summary>
    public static ushort EqdpEntry(ReadOnlySpan<byte> file, int setId)
    {
        if (file.Length < 6)
            throw new InvalidDataException("The EQDP file is truncated.");
        int blockSize = BinaryPrimitives.ReadUInt16LittleEndian(file[2..]), blockCount = BinaryPrimitives.ReadUInt16LittleEndian(file[4..]);
        if (blockSize == 0 || setId / blockSize >= blockCount)
            return 0;
        if (6 + blockCount * 2 > file.Length)
            throw new InvalidDataException("The EQDP file is truncated.");
        var block = BinaryPrimitives.ReadUInt16LittleEndian(file[(6 + setId / blockSize * 2)..]);
        if (block == ushort.MaxValue)
            return 0;
        var at = 6 + blockCount * 2 + (block + setId % blockSize) * 2;
        if (at + 2 > file.Length)
            throw new InvalidDataException("The EQDP file is truncated.");
        return BinaryPrimitives.ReadUInt16LittleEndian(file[at..]);
    }

    /// <summary>
    /// The attribute mask of a variant's IMC entry for a part: [variant count][part mask], then six
    /// bytes per part and variant, variant 0 first (material, decal, attributes and sound, vfx, animation).
    /// </summary>
    public static ushort ImcAttributes(ReadOnlySpan<byte> file, int variant, int part)
    {
        if (file.Length < 4)
            throw new InvalidDataException("The IMC file is truncated.");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(file), partMask = BinaryPrimitives.ReadUInt16LittleEndian(file[2..]);
        if (variant > count || (partMask & 1 << part) == 0)
            throw new InvalidDataException($"The IMC file has no variant {variant} of part {part}.");
        var parts = System.Numerics.BitOperations.PopCount((uint)partMask);
        var index = System.Numerics.BitOperations.PopCount((uint)(partMask & ((1 << part) - 1)));
        var at = 4 + (variant * parts + index) * 6;
        if (at + 6 > file.Length)
            throw new InvalidDataException("The IMC file is truncated.");
        return (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(file[(at + 2)..]) & 0x3FF);
    }

    /// <summary>
    /// The attributes the game enables on a smallclothes model with nothing worn over it: all of them,
    /// except the slot's variant attributes (atr_gv_a..j on the hands) whose bit is off in the IMC
    /// entry's attribute mask. Custom atrx_ attributes stay on, as Penumbra leaves them by default.
    /// </summary>
    public static uint EnabledAttributes(IReadOnlyList<string> attributes, PainterBodySlot slot, ushort imcAttributes)
    {
        var mask = uint.MaxValue;
        var prefix = $"atr_{slot.VariantCode}v_";
        for (var i = 0; i < attributes.Count && i < 32; i++)
        {
            var name = attributes[i];
            if (name.Length == prefix.Length + 1 && name.StartsWith(prefix, StringComparison.Ordinal) && name[^1] is >= 'a' and <= 'j'
                && (imcAttributes & 1 << (name[^1] - 'a')) == 0)
                mask &= ~(1u << i);
        }
        return mask;
    }

    /// <summary>
    /// The connector shapes Penumbra turns on between neighbouring parts that both have them:
    /// shpx_wr_ between the torso and hands, shpx_wa_ between the torso and legs, and shpx_an_
    /// between the legs and feet. Other custom shapes stay off, as Penumbra leaves them by default.
    /// </summary>
    /// <param name="shapes">Each slot's shape names in its model's shape order, in <see cref="PainterBodySlot.All"/> order.</param>
    /// <returns> The enabled shape mask of each slot. </returns>
    public static uint[] ConnectorShapes(IReadOnlyList<IReadOnlyList<string>> shapes)
    {
        var masks = new uint[shapes.Count];
        void Connect(int first, int second, string center)
        {
            if (first >= shapes.Count || second >= shapes.Count)
                return;
            for (var i = 0; i < shapes[first].Count && i < 32; i++)
            {
                var name = shapes[first][i];
                if (name.Length <= 8 || !name.StartsWith("shpx_" + center + "_", StringComparison.Ordinal))
                    continue;
                var other = shapes[second].Take(32).ToList().IndexOf(name);
                if (other < 0)
                    continue;
                masks[first] |= 1u << i;
                masks[second] |= 1u << other;
            }
        }
        Connect(0, 1, "wr");
        Connect(0, 2, "wa");
        Connect(2, 3, "an");
        return masks;
    }

    [GeneratedRegex(@"^chara/human/c(?<race>\d{4})/obj/body/b(?<body>\d{4})/material/v\d{4}/mt_c\k<race>b\k<body>_[^/]+\.mtrl$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BodySkinPath();

    [GeneratedRegex(@"^/?mt_c\d{4}b\d{4}_(?<suffix>[^/]+\.mtrl)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BodyMaterialName();

    /// <summary>
    /// The folder the character's body skin materials load from, taken from the ones it loads now:
    /// every model in a body slot loads the slot's skin material from it. The most common one wins.
    /// </summary>
    public static PainterSkinFolder? SkinFolder(IEnumerable<string> loadedGamePaths)
        => loadedGamePaths.Select(PathRules.NormalizeGamePath)
            .Select(path => (Path: path, Match: BodySkinPath().Match(path)))
            .Where(item => item.Match.Success)
            .GroupBy(item => item.Path[..item.Path.LastIndexOf('/')], StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => new PainterSkinFolder(group.Key, int.Parse(group.First().Match.Groups["race"].Value),
                int.Parse(group.First().Match.Groups["body"].Value)))
            .FirstOrDefault();

    /// <summary>
    /// The path the game loads for a model's body material name (/mt_c0201b0001_bibo.mtrl): the name's
    /// suffix in the character's skin folder, with the folder's race and body. Null for other names.
    /// </summary>
    public static string? SkinMaterialPath(string materialName, PainterSkinFolder folder)
        => BodyMaterialName().Match(materialName) is { Success: true } match
            ? $"{folder.Directory}/mt_c{folder.Race:D4}b{folder.Body:D4}_{match.Groups["suffix"].Value}".ToLowerInvariant()
            : null;

    /// <summary> The folder a body material name itself names, for when the character loads no body skin to take it from. </summary>
    public static PainterSkinFolder? FolderOfName(string materialName)
    {
        var match = Regex.Match(materialName, @"^/?mt_c(?<race>\d{4})b(?<body>\d{4})_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return null;
        var race = int.Parse(match.Groups["race"].Value);
        var body = int.Parse(match.Groups["body"].Value);
        return new PainterSkinFolder($"chara/human/c{race:D4}/obj/body/b{body:D4}/material/v0001", race, body);
    }

    /// <summary>
    /// The mesh with the enabled shapes applied: each shape swaps some of a mesh's indices for its
    /// own vertices, later shapes winning, as the game applies them. The UVs stay the same.
    /// </summary>
    public static ModelMesh WithShapes(ModelMesh mesh, uint enabled)
    {
        if (enabled == 0 || mesh.Shapes.Count == 0)
            return mesh;
        var swaps = new Dictionary<int, Dictionary<int, int>>();
        for (var s = 0; s < mesh.Shapes.Count && s < 32; s++)
        {
            if ((enabled & 1u << s) == 0)
                continue;
            foreach (var shapeMesh in mesh.Shapes[s].Meshes)
            {
                if (!swaps.TryGetValue(shapeMesh.MeshIndex, out var swap))
                    swaps[shapeMesh.MeshIndex] = swap = [];
                for (var i = 0; i < shapeMesh.Indices.Length; i++)
                    swap[shapeMesh.Indices[i]] = shapeMesh.Vertices[i];
            }
        }
        if (swaps.Count == 0)
            return mesh;
        var parts = mesh.Meshes.Select(part =>
        {
            if (!swaps.TryGetValue(part.MeshIndex, out var swap))
                return part;
            var submeshes = part.Submeshes.Select(submesh =>
            {
                var indices = (int[])submesh.Indices.Clone();
                for (var i = 0; i < indices.Length; i++)
                    if (swap.TryGetValue(submesh.MeshIndexStart + i, out var vertex) && vertex < part.Positions.Length)
                        indices[i] = vertex;
                return submesh with { Indices = indices };
            }).ToList();
            return part with { Submeshes = submeshes };
        }).ToList();
        return mesh with { Meshes = parts };
    }
}
