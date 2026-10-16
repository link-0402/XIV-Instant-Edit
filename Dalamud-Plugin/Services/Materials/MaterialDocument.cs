using System.Runtime.InteropServices;
using InstantEdit.Studio.Services;

namespace InstantEdit.Services.Materials;

/// <summary>A shader constant with its floats (integer constants keep their raw bits in the floats).</summary>
public sealed record MaterialConstant(uint Id, float[] Values);

/// <summary>
/// One texture slot: the sampler (shader input) and the texture it reads. Sampler settings hold
/// the wrap mode across (bits 0-1) and down (bits 2-3), a signed LoD bias in 64ths (bits 10-19)
/// and the smallest mip (bits 20-23).
/// </summary>
public sealed record MaterialSlot(uint SamplerId, string Path, bool Dx11, uint SamplerSettings)
{
    public int WrapU => (int)(SamplerSettings & 0x3);
    public int WrapV => (int)((SamplerSettings >> 2) & 0x3);
    public float LodBias => ((int)(SamplerSettings << 12) >> 22) / 64f;
    public int MinLod => (int)((SamplerSettings >> 20) & 0xF);

    public MaterialSlot WithWrap(int u, int v)
        => this with { SamplerSettings = (SamplerSettings & ~0xFu) | (uint)(u & 0x3) | (uint)(v & 0x3) << 2 };

    public MaterialSlot WithLodBias(float bias)
    {
        var steps = Math.Clamp((int)MathF.Round(bias * 64f), -512, 511);
        return this with { SamplerSettings = (SamplerSettings & ~(0x3FFu << 10)) | ((uint)steps & 0x3FF) << 10 };
    }

    public MaterialSlot WithMinLod(int lod) => this with { SamplerSettings = (SamplerSettings & ~(0xFu << 20)) | (uint)(lod & 0xF) << 20 };
}

/// <summary>
/// An editable material. Every change returns a new document and leaves this one alone, so undo
/// can keep snapshots. A document writes back byte for byte until something in it changes.
/// </summary>
public sealed class MaterialDocument
{
    public const uint NormalSampler = 0x0C5EC1F1;
    public const uint DefaultSamplerSettings = 0x000F8340;

    // This snapshot's own copy; never changed after construction.
    private readonly MtrlInfo info;

    private MaterialDocument(MtrlInfo source, MtrlInfo info, ColorTable? table)
    {
        Source = source;
        this.info = info;
        Table = table;
        Keys = info.ShaderKeys.Select(key => (key.Id, key.Value)).ToArray();
        Constants = info.Constants.Select(constant => new MaterialConstant(constant.Id, ReadValues(info, constant))).ToArray();
        Slots = info.Samplers.Select(sampler =>
        {
            var texture = sampler.HasTexture && sampler.TextureIndex < info.TextureOffsets.Length ? info.TextureOffsets[sampler.TextureIndex] : (MtrlTextureOffset?)null;
            return new MaterialSlot(sampler.Id, texture?.Path ?? string.Empty, texture?.Dx11 ?? false, sampler.Settings);
        }).ToArray();
    }

    /// <summary>The material as it was opened.</summary>
    public MtrlInfo Source { get; }

    /// <summary>False when the shader section could not be read; only texture paths can change then.</summary>
    public bool Editable => info.ShaderDataParsed;

    public string ShaderPackage => info.ShaderPackageName;

    /// <summary>The two shader flag words as one value (first word low).</summary>
    public uint ShaderFlags => info.MaterialFlags1 | (uint)info.MaterialFlags2 << 16;

    public IReadOnlyList<(uint Id, uint Value)> Keys { get; }
    public IReadOnlyList<MaterialConstant> Constants { get; }
    public IReadOnlyList<MaterialSlot> Slots { get; }
    public ColorTable? Table { get; }

    /// <summary>All texture paths of the file, including ones no sampler reads.</summary>
    public IReadOnlyList<string> TexturePaths => info.TextureOffsets.Select(texture => texture.Path).ToArray();

    public static MaterialDocument From(MtrlInfo material) => new(material, material.Clone(), ColorTable.Read(material));

    public byte[] Write() => info.ShaderDataParsed ? MaterialPatcher.Write(info) : MaterialPatcher.WritePathsOnly(info);

    public static bool Same(MaterialDocument a, MaterialDocument b) => a.Write().AsSpan().SequenceEqual(b.Write());

    public uint? KeyValue(uint id) => Keys.Where(key => key.Id == id).Select(key => (uint?)key.Value).FirstOrDefault();

    public MaterialConstant? Constant(uint id) => Constants.FirstOrDefault(constant => constant.Id == id);

    // ── Edits ────────────────────────────────────────────────────────────────

    public MaterialDocument WithKey(uint id, uint value) => Edit(m =>
    {
        var key = m.ShaderKeys.FirstOrDefault(key => key.Id == id);
        if (key != null)
            key.Value = value;
        else
            m.ShaderKeys.Add(new MtrlShaderKey { Id = id, Value = value });
    });

    public MaterialDocument WithoutKey(uint id) => Edit(m => m.ShaderKeys.RemoveAll(key => key.Id == id));

    /// <summary>Sets a constant's floats, in place when the size matches, else at the end of the value block (padded with the package defaults).</summary>
    public MaterialDocument WithConstant(uint id, float[] values) => Edit(m =>
    {
        var param = ShaderPackages.Param(id);
        if (param != null && values.Length < param.FloatCount)
            values = [.. values, .. param.Default.Skip(values.Length)];
        var bytes = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        var existing = m.Constants.FirstOrDefault(constant => constant.Id == id);
        if (existing != null && existing.Size == bytes.Length)
        {
            bytes.CopyTo(m.ShaderValues, existing.Offset);
            return;
        }
        if (existing != null)
            RemoveConstant(m, existing);
        m.Constants.Add(new MtrlConstant { Id = id, Offset = (ushort)m.ShaderValues.Length, Size = (ushort)bytes.Length });
        m.ShaderValues = [.. m.ShaderValues, .. bytes];
    });

    /// <summary>Drops a constant and closes the gap its values leave in the value block.</summary>
    public MaterialDocument WithoutConstant(uint id) => Edit(m =>
    {
        foreach (var constant in m.Constants.Where(constant => constant.Id == id).ToList())
            RemoveConstant(m, constant);
    });

    public MaterialDocument WithFlags(uint flags) => Edit(m =>
    {
        m.MaterialFlags1 = (ushort)flags;
        m.MaterialFlags2 = (ushort)(flags >> 16);
    });

    /// <summary>Changes slot <paramref name="index"/>'s sampler, settings, texture path and DX11 flag.</summary>
    public MaterialDocument WithSlot(int index, MaterialSlot slot) => Edit(m =>
    {
        var sampler = m.Samplers[index];
        sampler.Id = slot.SamplerId;
        sampler.Settings = slot.SamplerSettings;
        if (sampler.HasTexture && sampler.TextureIndex < m.TextureOffsets.Length)
        {
            var old = m.TextureOffsets[sampler.TextureIndex];
            var flags = (ushort)(slot.Dx11 ? old.Flags | 0x8000 : old.Flags & ~0x8000);
            m.TextureOffsets[sampler.TextureIndex] = new MtrlTextureOffset(slot.Path, flags);
        }
        else if (slot.Path.Length > 0)
        {
            m.TextureOffsets = [.. m.TextureOffsets, new MtrlTextureOffset(slot.Path, (ushort)(slot.Dx11 ? 0x8000 : 0))];
            sampler.TextureIndex = (byte)(m.TextureOffsets.Length - 1);
        }
        m.TextureCount = (byte)m.TextureOffsets.Length;
    });

    /// <summary>Adds a texture and a sampler reading it, with the normal map's sampler settings when there is one.</summary>
    public MaterialDocument WithAddedSlot(uint samplerId, string path) => Edit(m =>
    {
        var settings = m.Samplers.FirstOrDefault(sampler => sampler.Id == NormalSampler)?.Settings ?? DefaultSamplerSettings;
        m.TextureOffsets = [.. m.TextureOffsets, new MtrlTextureOffset(path, 0)];
        m.TextureCount = (byte)m.TextureOffsets.Length;
        m.Samplers.Add(new MtrlSampler { Id = samplerId, Settings = settings, TextureIndex = (byte)(m.TextureOffsets.Length - 1) });
    });

    /// <summary>Removes slot <paramref name="index"/>; its texture goes too unless another sampler reads it.</summary>
    public MaterialDocument WithoutSlot(int index) => Edit(m =>
    {
        var sampler = m.Samplers[index];
        m.Samplers.RemoveAt(index);
        var texture = sampler.TextureIndex;
        if (!sampler.HasTexture || texture >= m.TextureOffsets.Length || m.Samplers.Any(other => other.TextureIndex == texture))
            return;
        m.TextureOffsets = m.TextureOffsets.Where((_, i) => i != texture).ToArray();
        m.TextureCount = (byte)m.TextureOffsets.Length;
        foreach (var other in m.Samplers)
            if (other.HasTexture && other.TextureIndex > texture)
                other.TextureIndex--;
    });

    /// <summary>Replaces the colour table (null removes it) and sets the table flags to match.</summary>
    public MaterialDocument WithTable(ColorTable? table)
    {
        var copy = info.Clone();
        var flags = TableFlags.Read(copy.AdditionalData);
        if (table == null)
        {
            copy.AdditionalData = flags.WithTable(false).WithDyeTable(false).Write(copy.AdditionalData);
            copy.DataSet = [];
        }
        else
        {
            copy.AdditionalData = table.ApplyTo(flags).Write(copy.AdditionalData);
            copy.DataSet = table.WriteDataSet();
        }
        copy.AdditionalDataSize = (byte)copy.AdditionalData.Length;
        copy.DataSetSize = (ushort)copy.DataSet.Length;
        return new MaterialDocument(Source, copy, table);
    }

    /// <summary>Renames the shader package only; reconciling keys and slots is the session's job.</summary>
    public MaterialDocument WithShaderPackage(string name) => Edit(m => m.ShaderPackageName = name);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private MaterialDocument Edit(Action<MtrlInfo> change)
    {
        var copy = info.Clone();
        change(copy);
        return new MaterialDocument(Source, copy, Table);
    }

    private static float[] ReadValues(MtrlInfo m, MtrlConstant constant)
    {
        if (constant.Offset + constant.Size > m.ShaderValues.Length)
            return [];
        return MemoryMarshal.Cast<byte, float>(m.ShaderValues.AsSpan(constant.Offset, constant.Size / 4 * 4)).ToArray();
    }

    private static void RemoveConstant(MtrlInfo m, MtrlConstant constant)
    {
        m.Constants.Remove(constant);
        var end = constant.Offset + constant.Size;
        var shared = m.Constants.Any(other => other.Offset < end && constant.Offset < other.Offset + other.Size);
        if (shared || constant.Size == 0 || end > m.ShaderValues.Length)
            return;
        m.ShaderValues = [.. m.ShaderValues.AsSpan(0, constant.Offset), .. m.ShaderValues.AsSpan(end)];
        foreach (var other in m.Constants)
            if (other.Offset >= end)
                other.Offset -= constant.Size;
    }
}
