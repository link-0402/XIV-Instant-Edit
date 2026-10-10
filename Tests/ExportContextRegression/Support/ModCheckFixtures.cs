using System.Buffers.Binary;
using System.Text;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services.Check;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;

/// <summary>The game's files for a mod check scenario: only what a test adds exists.</summary>
internal sealed class FakeVanillaFiles : IVanillaFiles
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public FakeVanillaFiles Add(string gamePath, byte[]? content = null)
    {
        _files[PathNormalizer.GamePath(gamePath)] = content ?? [0];
        return this;
    }

    /// <summary>Gives every playable race an EQDP file; races without an entry have no model of their own for the set.</summary>
    public FakeVanillaFiles WithEqdp(ushort setId, IReadOnlyDictionary<ushort, ushort> entries, bool accessory = false)
    {
        foreach (var race in RaceInfo.PlayableCodes)
            Add(ModelNaming.EqdpPath(race, accessory), ModCheckFixtures.Eqdp(new Dictionary<ushort, ushort> { [setId] = entries.GetValueOrDefault(race) }));
        return this;
    }

    public bool Exists(string gamePath) => _files.ContainsKey(PathNormalizer.GamePath(gamePath));

    public byte[]? TryReadBytes(string gamePath) => _files.GetValueOrDefault(PathNormalizer.GamePath(gamePath));
}

/// <summary>Item names by model and variant.</summary>
internal sealed class FakeItemVariants : IItemVariants
{
    private readonly Dictionary<(GameModelKind, ushort, EquipSlot?, int), List<string>> _items = new();

    public FakeItemVariants Add(GameModelKind kind, ushort set, EquipSlot slot, int variant, string name)
    {
        if (!_items.TryGetValue((kind, set, slot, variant), out var list))
            _items[(kind, set, slot, variant)] = list = [];
        list.Add(name);
        return this;
    }

    public IReadOnlyList<string> Names(GameModelKind kind, ushort primaryId, ushort secondaryId, EquipSlot? slot, int variant)
        => _items.GetValueOrDefault((kind, primaryId, slot, variant)) ?? (IReadOnlyList<string>)[];
}

/// <summary>A player's collection: the game paths other mods provide, under X:\Penumbra\&lt;mod&gt;\.</summary>
internal sealed class FakeCollectionView(Dictionary<string, string> provided, bool? enabled = true) : ICollectionView
{
    public CollectionContext Context { get; } = new("Test Collection", enabled, 0, null);

    public IReadOnlyDictionary<string, string?> Resolve(IReadOnlyList<string> gamePaths)
        => gamePaths.ToDictionary(path => path, path => provided.GetValueOrDefault(path));

    public string? ModOf(string resolvedPath) => resolvedPath.StartsWith(@"X:\Penumbra\", StringComparison.OrdinalIgnoreCase)
        ? resolvedPath[@"X:\Penumbra\".Length..].Split('\\')[0]
        : null;
}

/// <summary>Penumbra reloading a mod: succeeds unless told otherwise, and counts the reloads.</summary>
internal sealed class FakeReloader : IModReloader
{
    public bool ReloadSucceeds { get; init; } = true;
    public int ReloadCalls { get; private set; }

    public bool Reload(string folderName)
    {
        ReloadCalls++;
        return ReloadSucceeds;
    }

    public bool IsPresent(string folderName) => true;
}

/// <summary>A mod folder in the test root, written file by file.</summary>
internal sealed class TestModFolder : IDisposable
{
    public TestModFolder(string? name = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "InstantEditModCheck", name ?? Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public TestModFolder File(string relative, byte[] content)
    {
        var full = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, content);
        return this;
    }

    public TestModFolder Meta(string json)
    {
        System.IO.File.WriteAllText(System.IO.Path.Combine(Path, "meta.json"), json);
        return this;
    }

    public bool Exists(string relative) => System.IO.File.Exists(System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    public string ReadMeta() => System.IO.File.ReadAllText(System.IO.Path.Combine(Path, "meta.json"));

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>
/// Minimal but well-formed game files for the mod check scenarios, laid out as Mod Optimizer's
/// GameFileBuilders wrote them, so its tests port one to one.
/// </summary>
internal static class ModCheckFixtures
{
    public const uint ModelVersion5 = 0x01000005;
    public const uint ModelVersion6 = 0x01000006;
    public const uint MaterialSignature = 0x01030000;

    public const uint SamplerNormal = 0x0C5EC1F1;
    public const uint SamplerMask = 0x8A4E82B6;
    public const uint SamplerIndex = 0x565F8FD8;
    public const uint SamplerDiffuse = 0x115306BE;

    public sealed record Submesh(params string[] Attributes);

    public sealed record Mesh(int MaterialIndex, params Submesh[] Submeshes);

    public sealed record Sampler(uint Id, byte TextureIndex);

    /// <summary>An MDL with the given materials, attributes and LOD0 meshes; v5 and v6 share this layout.</summary>
    public static byte[] Mdl(IReadOnlyList<string> materials, IReadOnlyList<Mesh>? meshes = null, IReadOnlyList<string>? bones = null,
        uint version = ModelVersion6, bool extraLods = false)
    {
        meshes ??= materials.Select((_, i) => new Mesh(i, new Submesh())).ToArray();
        bones ??= ["j_kosi", "j_sebo_a"];
        var attributes = meshes.SelectMany(m => m.Submeshes).SelectMany(s => s.Attributes).Distinct().ToArray();

        var strings = new MemoryStream();
        var offsets = new Dictionary<string, uint>();
        foreach (var value in attributes.Concat(bones).Concat(materials).Distinct())
        {
            offsets[value] = (uint)strings.Position;
            strings.Write(Encoding.UTF8.GetBytes(value));
            strings.WriteByte(0);
        }
        while (strings.Length % 4 != 0) strings.WriteByte(0);

        var body = new MemoryStream();
        var writer = new BinaryWriter(body);
        writer.Write((ushort)offsets.Count);
        writer.Write((ushort)0);
        writer.Write((uint)strings.Length);
        writer.Write(strings.ToArray());
        var submeshCount = meshes.Sum(m => m.Submeshes.Length);
        var header = new byte[56];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)meshes.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)attributes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)submeshCount);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)materials.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), (ushort)bones.Count);
        header[22] = 1;
        header[27] = (byte)(extraLods ? 0x10 : 0);
        writer.Write(header);
        for (var lod = 0; lod < 3; ++lod)
        {
            var entry = new byte[60];
            if (lod == 0) BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2), (ushort)meshes.Count);
            writer.Write(entry);
        }
        if (extraLods) writer.Write(new byte[3 * 40]);
        var submeshIndex = 0;
        foreach (var mesh in meshes)
        {
            var entry = new byte[36];
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(8), (ushort)mesh.MaterialIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(10), (ushort)submeshIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(12), (ushort)mesh.Submeshes.Length);
            submeshIndex += mesh.Submeshes.Length;
            writer.Write(entry);
        }
        foreach (var attribute in attributes) writer.Write(offsets[attribute]);
        foreach (var submesh in meshes.SelectMany(m => m.Submeshes))
        {
            var mask = submesh.Attributes.Aggregate(0u, (current, name) => current | 1u << Array.IndexOf(attributes, name));
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(mask);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
        }
        foreach (var material in materials) writer.Write(offsets[material]);
        foreach (var bone in bones) writer.Write(offsets[bone]);
        writer.Write(new byte[64]);
        writer.Flush();

        var runtime = body.ToArray();
        var file = new byte[0x44 + runtime.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(file, version);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), (uint)runtime.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(14), (ushort)materials.Count);
        file[64] = 1;
        runtime.CopyTo(file, 0x44);
        return file;
    }

    /// <summary>An MTRL with the given textures; each texture gets a sampler unless samplers are given.</summary>
    public static byte[] Mtrl(IReadOnlyList<string> textures, string shader = "character.shpk", IReadOnlyList<bool>? dx11 = null,
        IReadOnlyList<Sampler>? samplers = null, int colorTableSize = 2176)
    {
        samplers ??= textures.Select((_, i) => new Sampler(i switch { 0 => SamplerNormal, 1 => SamplerMask, 2 => SamplerIndex, _ => SamplerDiffuse }, (byte)i)).ToArray();
        var strings = new MemoryStream();
        var textureOffsets = new List<ushort>();
        foreach (var texture in textures)
        {
            textureOffsets.Add((ushort)strings.Position);
            strings.Write(Encoding.UTF8.GetBytes(texture));
            strings.WriteByte(0);
        }
        var uvOffset = (ushort)strings.Position;
        strings.Write("map1\0"u8);
        var shaderOffset = (ushort)strings.Position;
        strings.Write(Encoding.UTF8.GetBytes(shader));
        strings.WriteByte(0);
        while (strings.Length % 4 != 0) strings.WriteByte(0);

        var file = new MemoryStream();
        var writer = new BinaryWriter(file);
        writer.Write(MaterialSignature);
        writer.Write((ushort)0);
        writer.Write((ushort)colorTableSize);
        writer.Write((ushort)strings.Length);
        writer.Write(shaderOffset);
        writer.Write((byte)textures.Count);
        writer.Write((byte)1);
        writer.Write((byte)0);
        writer.Write((byte)0);
        for (var i = 0; i < textures.Count; ++i)
        {
            writer.Write(textureOffsets[i]);
            writer.Write((ushort)(dx11 is not null && dx11[i] ? 0x8000 : 0));
        }
        writer.Write(uvOffset);
        writer.Write((ushort)0);
        writer.Write(strings.ToArray());
        writer.Write(new byte[colorTableSize]);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)samplers.Count);
        writer.Write(0u);
        foreach (var sampler in samplers)
        {
            writer.Write(sampler.Id);
            writer.Write(0u);
            writer.Write(sampler.TextureIndex);
            writer.Write(new byte[3]);
        }
        writer.Flush();
        var bytes = file.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)Math.Min(bytes.Length, ushort.MaxValue));
        return bytes;
    }

    /// <summary>A B8G8R8A8 texture of the given size.</summary>
    public static byte[] Tex(int width = 4, int height = 4)
    {
        var bytes = new byte[80 + width * height * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x00800000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x1450);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1);
        bytes[14] = 1;
        bytes[15] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 80);
        return bytes;
    }

    public static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    /// <summary>An IMC file; rows[variant][part] = (material set, attribute mask). Row 0 is the default row.</summary>
    public static byte[] Imc(IReadOnlyList<(byte MaterialId, ushort Mask)[]> rows, ushort partMask = 0x1F)
    {
        var parts = System.Numerics.BitOperations.PopCount(partMask);
        var bytes = new byte[4 + rows.Count * parts * 6];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)(rows.Count - 1));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), partMask);
        for (var row = 0; row < rows.Count; ++row)
        for (var part = 0; part < parts; ++part)
        {
            var (materialId, mask) = part < rows[row].Length ? rows[row][part] : ((byte)1, (ushort)0x3FF);
            var offset = 4 + (row * parts + part) * 6;
            bytes[offset] = materialId;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 2), mask);
        }
        return bytes;
    }

    /// <summary>An EQDP file holding the given packed entries per set.</summary>
    public static byte[] Eqdp(IReadOnlyDictionary<ushort, ushort> entries)
    {
        const int blockSize = 160;
        var blockCount = entries.Count == 0 ? 1 : entries.Keys.Max() / blockSize + 1;
        var blocks = entries.Keys.Select(set => set / blockSize).Distinct().Order().ToArray();
        var header = new ushort[blockCount];
        Array.Fill(header, ushort.MaxValue);
        for (var i = 0; i < blocks.Length; ++i) header[blocks[i]] = (ushort)(i * blockSize);
        var bytes = new byte[6 + blockCount * 2 + blocks.Length * blockSize * 2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), blockSize);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)blockCount);
        for (var i = 0; i < blockCount; ++i) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6 + i * 2), header[i]);
        foreach (var (set, entry) in entries)
        {
            var offset = 6 + blockCount * 2 + (header[set / blockSize] + set % blockSize) * 2;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), entry);
        }
        return bytes;
    }
}
