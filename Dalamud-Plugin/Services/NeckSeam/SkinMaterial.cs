using System.Buffers.Binary;

namespace InstantEdit.Services.NeckSeam;

/// <summary>
/// A skin.shpk material's shader section: shader package, keys, constants and samplers, plus the
/// texture each sampler reads. Constants can be changed or added; the rest of the file is kept
/// byte for byte. Layout as in MaterialPreviewBundleBuilder.ReadMaterialMetadata.
/// </summary>
internal sealed class SkinMaterial
{
    public const string SkinShader = "skin.shpk";
    public const uint SkinTypeKey = 0x380CAED0;
    public const uint SkinTypeFace = 0xF5673524;
    public const uint SkinTypeBody = 0x2BDB45F1;
    public const uint SkinTypeBodyHrothgar = 0x57FF3B64;
    public const uint SkinTypeFaceEmissive = 0x72E697CD;

    public const uint DiffuseSampler = 0x115306BE;
    public const uint NormalSampler = 0x0C5EC1F1;
    public const uint MaskSampler = 0x8A4E82B6;

    public const uint DiffuseColor = 0x2C2A34DD;
    public const uint SsaoMask = 0xB7FA33E2;
    public const uint TileIndex = 0x4255F2F4;
    public const uint TileScale = 0x2E60B071;
    public const uint TileAlpha = 0x12C6AC9F;
    public const uint NormalScale = 0xB5545FBB;
    public const uint SheenRate = 0x800EE35F;
    public const uint SheenTintRate = 0x1F264897;
    public const uint SheenAperture = 0xF490F76E;
    public const uint TextureMipBias = 0x39551220;
    public const uint TileMipBiasOffset = 0x6421DD30;

    /// <summary> skin.shpk's defaults for the constants the seam compares (read from the shader package). </summary>
    public static readonly IReadOnlyDictionary<uint, float[]> Defaults = new Dictionary<uint, float[]>
    {
        [DiffuseColor] = [1, 1, 1], [SsaoMask] = [1], [TileIndex] = [0], [TileScale] = [16, 16], [TileAlpha] = [1],
        [NormalScale] = [1], [SheenRate] = [0], [SheenTintRate] = [0], [SheenAperture] = [1], [TextureMipBias] = [0],
        [TileMipBiasOffset] = [0],
    };

    public static readonly IReadOnlyDictionary<uint, string> Names = new Dictionary<uint, string>
    {
        [DiffuseColor] = "g_DiffuseColor", [SsaoMask] = "g_SSAOMask", [TileIndex] = "g_TileIndex", [TileScale] = "g_TileScale",
        [TileAlpha] = "g_TileAlpha", [NormalScale] = "g_NormalScale", [SheenRate] = "g_SheenRate", [SheenTintRate] = "g_SheenTintRate",
        [SheenAperture] = "g_SheenAperture", [TextureMipBias] = "g_TextureMipBias", [TileMipBiasOffset] = "g_TileMipBiasOffset",
    };

    private readonly byte[] _bytes;
    private readonly int _shaderHeader;
    private readonly (uint Id, ushort Offset, ushort Size)[] _constants;
    private readonly int _values;
    private readonly int _valueSize;

    public string ShaderPackage { get; }
    public IReadOnlyDictionary<uint, uint> Keys { get; }
    /// <summary> Sampler id → index into the material's texture table. </summary>
    public IReadOnlyDictionary<uint, int> Samplers { get; }
    /// <summary> Sampler id → its flags, which hold how it addresses UVs outside 0..1 (see <see cref="SeamAddress"/>). </summary>
    public IReadOnlyDictionary<uint, uint> SamplerFlags { get; }
    /// <summary> The stored texture paths, in texture-table order. </summary>
    public IReadOnlyList<string> Textures { get; }
    public IReadOnlyList<ushort> TextureFlags { get; }

    public uint SkinType => Keys.TryGetValue(SkinTypeKey, out var value) ? value : SkinTypeFace;
    public bool IsSkin => string.Equals(ShaderPackage, SkinShader, StringComparison.OrdinalIgnoreCase);
    public bool IsFaceSkin => IsSkin && SkinType is SkinTypeFace or SkinTypeFaceEmissive;
    public bool IsBodySkin => IsSkin && SkinType is SkinTypeBody or SkinTypeBodyHrothgar;

    /// <summary> The material's shader flags: 0x10 blends transparency instead of cutting it out, 0x01 hides back faces. </summary>
    public uint MaterialFlags => U32(_bytes, _shaderHeader + 8);

    private SkinMaterial(byte[] bytes, int shaderHeader, (uint, ushort, ushort)[] constants, int values, int valueSize, string shader,
        IReadOnlyDictionary<uint, uint> keys, IReadOnlyDictionary<uint, int> samplers, IReadOnlyDictionary<uint, uint> samplerFlags,
        IReadOnlyList<string> textures, IReadOnlyList<ushort> flags)
    {
        _bytes = bytes;
        _shaderHeader = shaderHeader;
        _constants = constants;
        _values = values;
        _valueSize = valueSize;
        ShaderPackage = shader;
        Keys = keys;
        Samplers = samplers;
        SamplerFlags = samplerFlags;
        Textures = textures;
        TextureFlags = flags;
    }

    public static SkinMaterial Read(byte[] bytes)
    {
        if (bytes.Length < 16)
            throw new InvalidDataException("The material is truncated.");
        int dataSetSize = U16(bytes, 6), stringSize = U16(bytes, 8), shaderName = U16(bytes, 10);
        int textureCount = bytes[12], uvCount = bytes[13], colorSetCount = bytes[14], additional = bytes[15];
        var strings = 16 + 4 * (textureCount + uvCount + colorSetCount);
        var shaderHeader = strings + stringSize + additional + dataSetSize;
        if (shaderHeader + 12 > bytes.Length)
            throw new InvalidDataException("The material's shader section is outside the file.");
        string ReadString(int offset)
        {
            if (offset >= stringSize)
                throw new InvalidDataException("A material string points outside the string table.");
            var end = Array.IndexOf(bytes, (byte)0, strings + offset, stringSize - offset);
            return System.Text.Encoding.UTF8.GetString(bytes, strings + offset, (end < 0 ? strings + stringSize : end) - strings - offset);
        }

        var textures = new string[textureCount];
        var flags = new ushort[textureCount];
        for (var i = 0; i < textureCount; i++)
        {
            textures[i] = PathRules.NormalizeGamePath(ReadString(U16(bytes, 16 + i * 4)));
            flags[i] = U16(bytes, 18 + i * 4);
        }

        int valueSize = U16(bytes, shaderHeader), keyCount = U16(bytes, shaderHeader + 2);
        int constantCount = U16(bytes, shaderHeader + 4), samplerCount = U16(bytes, shaderHeader + 6);
        var cursor = shaderHeader + 12;
        var values = cursor + keyCount * 8 + constantCount * 8 + samplerCount * 12;
        if (valueSize % 4 != 0 || values + valueSize > bytes.Length)
            throw new InvalidDataException("The material's shader section is truncated.");
        var keys = new Dictionary<uint, uint>();
        for (var i = 0; i < keyCount; i++, cursor += 8)
            keys[U32(bytes, cursor)] = U32(bytes, cursor + 4);
        var constants = new (uint, ushort, ushort)[constantCount];
        for (var i = 0; i < constantCount; i++, cursor += 8)
        {
            constants[i] = (U32(bytes, cursor), U16(bytes, cursor + 4), U16(bytes, cursor + 6));
            if (constants[i].Item2 % 4 != 0 || constants[i].Item3 % 4 != 0 || constants[i].Item2 + constants[i].Item3 > valueSize)
                throw new InvalidDataException("A material constant points outside the value data.");
        }
        var samplers = new Dictionary<uint, int>();
        var samplerFlags = new Dictionary<uint, uint>();
        for (var i = 0; i < samplerCount; i++, cursor += 12)
            if (bytes[cursor + 8] < textureCount)
            {
                samplers[U32(bytes, cursor)] = bytes[cursor + 8];
                samplerFlags[U32(bytes, cursor)] = U32(bytes, cursor + 4);
            }

        return new SkinMaterial(bytes, shaderHeader, constants, values, valueSize, ReadString(shaderName), keys, samplers, samplerFlags, textures, flags);
    }

    /// <summary> The texture a sampler reads, as stored in the material; null when the sampler is absent. </summary>
    public string? TextureFor(uint sampler) => Samplers.TryGetValue(sampler, out var index) ? Textures[index] : null;

    /// <summary> A sampler's flags; 0 (wrap) when the sampler is absent. </summary>
    public uint FlagsFor(uint sampler) => SamplerFlags.GetValueOrDefault(sampler);

    /// <summary> The path the game requests for a sampler's texture (the DX11 "--" file when flagged). </summary>
    public string? RequestedTextureFor(uint sampler)
        => Samplers.TryGetValue(sampler, out var index) ? PathRules.Dx11TexturePath(Textures[index], TextureFlags[index]) : null;

    public bool HasConstant(uint id) => _constants.Any(c => c.Id == id);

    /// <summary> A constant's stored values, or skin.shpk's default when the material leaves it out. </summary>
    public float[] Constant(uint id)
    {
        foreach (var (constantId, offset, size) in _constants)
            if (constantId == id)
            {
                var values = new float[size / 4];
                for (var i = 0; i < values.Length; i++)
                    values[i] = BinaryPrimitives.ReadSingleLittleEndian(_bytes.AsSpan(_values + offset + i * 4));
                return values;
            }
        return Defaults.TryGetValue(id, out var fallback) ? (float[])fallback.Clone() : [];
    }

    /// <summary>
    /// The material with <paramref name="changes"/> written: existing constants of the same size are
    /// overwritten in place, others are appended to the constant table and value data.
    /// </summary>
    public byte[] WithConstants(IReadOnlyDictionary<uint, float[]> changes)
    {
        var constants = _constants.ToList();
        var values = new List<byte>(_bytes.AsSpan(_values, _valueSize).ToArray());
        foreach (var (id, newValues) in changes)
        {
            var index = constants.FindIndex(c => c.Id == id);
            if (index >= 0 && constants[index].Size == newValues.Length * 4)
            {
                for (var i = 0; i < newValues.Length; i++)
                {
                    var raw = BitConverter.GetBytes(newValues[i]);
                    for (var b = 0; b < 4; b++)
                        values[constants[index].Offset + i * 4 + b] = raw[b];
                }
                continue;
            }
            var offset = values.Count;
            foreach (var value in newValues)
                values.AddRange(BitConverter.GetBytes(value));
            var entry = (id, (ushort)offset, (ushort)(newValues.Length * 4));
            if (index >= 0) constants[index] = entry;
            else constants.Add(entry);
        }
        if (values.Count > ushort.MaxValue || constants.Count > 256)
            throw new InvalidDataException("The material's constants would exceed the format's limits.");

        int keyCount = U16(_bytes, _shaderHeader + 2), oldConstants = U16(_bytes, _shaderHeader + 4), samplerCount = U16(_bytes, _shaderHeader + 6);
        var keysStart = _shaderHeader + 12;
        var samplersStart = keysStart + keyCount * 8 + oldConstants * 8;
        using var output = new MemoryStream(_bytes.Length + 64);
        output.Write(_bytes, 0, _shaderHeader);
        Span<byte> header = stackalloc byte[12];
        _bytes.AsSpan(_shaderHeader, 12).CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)values.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)constants.Count);
        output.Write(header);
        output.Write(_bytes, keysStart, keyCount * 8);
        Span<byte> entryBytes = stackalloc byte[8];
        foreach (var (id, offset, size) in constants)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(entryBytes, id);
            BinaryPrimitives.WriteUInt16LittleEndian(entryBytes[4..], offset);
            BinaryPrimitives.WriteUInt16LittleEndian(entryBytes[6..], size);
            output.Write(entryBytes);
        }
        output.Write(_bytes, samplersStart, samplerCount * 12);
        output.Write(values.ToArray());
        var tail = _values + _valueSize;
        output.Write(_bytes, tail, _bytes.Length - tail);
        var result = output.ToArray();
        if (result.Length > ushort.MaxValue)
            throw new InvalidDataException("The material would exceed the format's size limit.");
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), (ushort)result.Length);
        return result;
    }

    private static ushort U16(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at, 2));
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
}
