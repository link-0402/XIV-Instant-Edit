namespace InstantEdit.Services.Materials;

/// <summary>A material key a package declares: its default and every value the package has shaders for.</summary>
public sealed record PackageKey(uint Id, uint Default, uint[] Values);

/// <summary>A texture slot a material can fill for this package (a slot-2 resource of the package).</summary>
public sealed record PackageSampler(uint Id, string ShpkName);

/// <summary>"Only while key <see cref="Key"/> is one of <see cref="Values"/>."</summary>
public sealed record KeyCondition(uint Key, uint[] Values);

/// <summary>One entry of the material parameter block: where its floats sit and what the package uses when a material leaves it out.</summary>
public sealed record MaterialParam(uint Id, ushort ByteOffset, ushort ByteSize, float[] Default)
{
    public int FloatCount => ByteSize / 4;
}

/// <summary>
/// What one character shader package does with a material: which keys it has, which constants its
/// shaders read (some only under certain key values), which texture slots it samples and when, and
/// whether it reads the colour table.
/// </summary>
public sealed record ShaderPackageFacts(
    string Name,
    PackageKey[] Keys,
    HashSet<uint> ReadConstants,
    Dictionary<uint, KeyCondition[]> ConstantsReadOnlyWhen,
    PackageSampler[] Samplers,
    Dictionary<uint, KeyCondition[]> SamplerOnlyWhen,
    bool SamplesColorTable)
{
    public PackageKey? Key(uint id) => Keys.FirstOrDefault(key => key.Id == id);

    public bool DeclaresSampler(uint id) => Samplers.Any(sampler => sampler.Id == id);

    /// <summary>Whether the shaders the game picks for these key values read the constant. Missing keys count as their default.</summary>
    public bool Reads(uint constantId, IEnumerable<(uint Id, uint Value)> keys)
        => ReadConstants.Contains(constantId) && Holds(ConstantsReadOnlyWhen.GetValueOrDefault(constantId), keys);

    /// <summary>Whether the shaders the game picks for these key values sample the slot.</summary>
    public bool Samples(uint samplerId, IEnumerable<(uint Id, uint Value)> keys)
        => DeclaresSampler(samplerId) && Holds(SamplerOnlyWhen.GetValueOrDefault(samplerId), keys);

    private bool Holds(KeyCondition[]? conditions, IEnumerable<(uint Id, uint Value)> keys)
    {
        if (conditions == null)
            return true;
        var set = keys.ToList();
        return conditions.All(condition =>
        {
            var value = set.Where(key => key.Id == condition.Key).Select(key => (uint?)key.Value).FirstOrDefault()
                        ?? Key(condition.Key)?.Default;
            return value is { } v && condition.Values.Contains(v);
        });
    }
}

/// <summary>The facts for the 13 character-family shader packages, generated from the game's files.</summary>
public static partial class ShaderPackages
{
    private static readonly ShaderPackageFacts[] Packages = GeneratedPackages();

    private static readonly Dictionary<string, ShaderPackageFacts> ByName =
        Packages.ToDictionary(package => package.Name, StringComparer.OrdinalIgnoreCase);

    // Params lives in the generated part; static initialisers of two partial files run in no set order.
    private static Dictionary<uint, MaterialParam>? paramsById;

    public static IReadOnlyList<ShaderPackageFacts> All => Packages;

    /// <summary>The package by its file name ("character.shpk") or game path; null for packages outside the character family.</summary>
    public static ShaderPackageFacts? Get(string shpkName)
    {
        var name = shpkName[(shpkName.LastIndexOfAny(['/', '\\']) + 1)..];
        return ByName.GetValueOrDefault(name);
    }

    public static MaterialParam? Param(uint id) => (paramsById ??= Params.ToDictionary(param => param.Id)).GetValueOrDefault(id);
}
