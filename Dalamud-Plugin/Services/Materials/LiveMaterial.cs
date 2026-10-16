using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using KernelShaderPackage = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.ShaderPackage;
using RenderMaterial = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Material;

namespace InstantEdit.Services.Materials;

/// <summary>
/// The in-memory preview of a material's values: writes constants, keys, shader flags and sampler
/// settings straight into the materials the game has loaded for a file, so the character changes
/// the same frame. Every place the file is loaded on the character (several models can share one
/// material) is bound by indices; pointers are found again and compared before every write, and a
/// change (a redraw, GPose, a zone change) drops the binding instead of writing. What was there at
/// bind time is put back on Restore. Framework thread only.
/// </summary>
internal sealed unsafe class LiveMaterial : IDisposable
{
    private const int MaximumModels = 64;
    private const uint SamplerFlagsKeep = 0xFFFFFDFF;
    private const uint SamplerFlagsSet = 0x1C0;

    private readonly IObjectTable objects;
    private readonly IPluginLog log;
    private readonly List<Target> targets = [];
    private readonly Dictionary<nint, Snapshot> snapshots = [];
    private bool warned;

    /// <summary>One bound place, with the pointers seen at bind time.</summary>
    private sealed record Target(MaterialBinding Binding, nint CharacterBase, nint Material, nint Handle, nint ShaderPackage);

    /// <summary>What a material held at bind time, put back on Restore.</summary>
    private sealed record Snapshot(float[] Constants, uint Flags, uint[] SamplerFlags, uint[] Keys);

    public LiveMaterial(IObjectTable objects, IPluginLog log)
    {
        this.objects = objects;
        this.log = log;
    }

    public bool Bound => targets.Count > 0;

    public IReadOnlyList<MaterialBinding> Bindings => targets.Select(target => target.Binding).ToArray();

    /// <summary>Bytes of constant buffer kept for restoring, over all bound materials.</summary>
    public int SnapshotBytes => snapshots.Values.Sum(snapshot => snapshot.Constants.Length * 4);

    public string LastWrite { get; private set; } = string.Empty;

    /// <summary>Every place on the object (its body and weapons) where a material loaded from <paramref name="gamePath"/> sits.</summary>
    public static IReadOnlyList<MaterialBinding> Find(IObjectTable objects, int objectIndex, string gamePath)
    {
        var found = new List<MaterialBinding>();
        foreach (var kind in Enum.GetValues<DrawObjectKind>())
        {
            var characterBase = DrawObject(objects, objectIndex, kind);
            if (characterBase == null || characterBase->Models == null)
                continue;
            var count = Math.Min(characterBase->SlotCount, MaximumModels);
            for (var slot = 0; slot < count; slot++)
            {
                var model = characterBase->Models[slot];
                if (model == null || model->Materials == null)
                    continue;
                for (var index = 0; index < Math.Min(model->MaterialCount, CharacterBase.MaterialsPerSlot); index++)
                {
                    var material = model->Materials[index];
                    if (material != null && material->MaterialResourceHandle != null
                        && MaterialPathMatch.Matches(material->MaterialResourceHandle->FileName.Utf8(), gamePath))
                        found.Add(new MaterialBinding(objectIndex, kind, slot, index));
                }
            }
        }
        return found;
    }

    /// <summary>
    /// Keeps the bound places that still resolve, forgets the stale ones, and binds the new ones,
    /// keeping what each newly bound material holds now. Returns how many places were added.
    /// </summary>
    public int Bind(IReadOnlyList<MaterialBinding> bindings)
    {
        targets.RemoveAll(target => Resolve(target.Binding) != target);
        var kept = targets.Select(target => target.Material).ToHashSet();
        foreach (var stale in snapshots.Keys.Where(address => !kept.Contains(address)).ToList())
            snapshots.Remove(stale);
        var added = 0;
        foreach (var binding in bindings)
        {
            if (targets.Any(target => target.Binding == binding) || Resolve(binding) is not { } target)
                continue;
            targets.Add(target);
            added++;
            if (snapshots.ContainsKey(target.Material))
                continue;
            var material = (RenderMaterial*)target.Material;
            var constants = material->MaterialParameterCBuffer == null ? [] : material->MaterialParameterCBuffer->TryGetBuffer().ToArray();
            var samplers = new uint[material->TextureCount];
            for (var i = 0; i < samplers.Length; i++)
                samplers[i] = material->Textures[i].SamplerFlags;
            var keys = material->ShaderKeyValues == null ? [] : material->ShaderKeyValuesSpan.ToArray();
            snapshots[target.Material] = new Snapshot(constants, material->ShaderFlags, samplers, keys);
        }
        if (added > 0)
            warned = false;
        return added;
    }

    /// <summary>Every bound place still holds the material it was bound to; false drops nothing by itself.</summary>
    public bool IsValid()
    {
        if (targets.Count == 0)
            return false;
        foreach (var target in targets)
            if (Resolve(target.Binding) != target)
                return false;
        return true;
    }

    /// <summary>Writes a constant at the offset the shader package gives it; the package lacking it is logged once.</summary>
    public void SetConstant(uint crc, ReadOnlySpan<float> values)
    {
        foreach (var address in Materials())
        {
            var material = (RenderMaterial*)address;
            var package = (KernelShaderPackage*)TargetOf(address).ShaderPackage;
            var element = Element(package, crc);
            if (element is not { } found || material->MaterialParameterCBuffer == null)
            {
                Warn($"The loaded shader has no value 0x{crc:X8}.");
                continue;
            }
            var buffer = material->MaterialParameterCBuffer->TryGetBuffer();
            var start = found.Offset / 4;
            var count = Math.Min(values.Length, found.Size / 4);
            if (start < 0 || start + count > buffer.Length)
                continue;
            values[..count].CopyTo(buffer.Slice(start, count));
        }
        LastWrite = $"value 0x{crc:X8} = {string.Join(", ", values.ToArray())}";
    }

    public void SetFlags(uint flags)
    {
        foreach (var address in Materials())
            ((RenderMaterial*)address)->ShaderFlags = flags;
        LastWrite = $"flags 0x{flags:X8}";
    }

    public void SetSamplerFlags(uint samplerCrc, uint settings)
    {
        foreach (var address in Materials())
        {
            var material = (RenderMaterial*)address;
            for (var i = 0; i < material->TextureCount; i++)
                if (material->Textures[i].Id == samplerCrc)
                    material->Textures[i].SamplerFlags = (settings & SamplerFlagsKeep) | SamplerFlagsSet;
        }
        LastWrite = $"sampler 0x{samplerCrc:X8} = 0x{settings:X8}";
    }

    /// <summary>Sets a material key's value in the loaded material, in the shader package's key order.</summary>
    public void SetKey(uint key, uint value)
    {
        foreach (var address in Materials())
        {
            var material = (RenderMaterial*)address;
            var package = (KernelShaderPackage*)TargetOf(address).ShaderPackage;
            if (package == null || material->ShaderKeyValues == null)
                continue;
            var keys = package->MaterialKeysSpan;
            var count = Math.Min(keys.Length, material->ShaderKeyCount);
            for (var i = 0; i < count; i++)
                if (keys[i] == key)
                    material->ShaderKeyValues[i] = value;
        }
        LastWrite = $"key 0x{key:X8} = 0x{value:X8}";
    }

    /// <summary>A constant's floats as the loaded material holds them now; null when it can't be read.</summary>
    public float[]? ReadConstant(uint crc)
    {
        var addresses = Materials();
        if (addresses.Count == 0)
            return null;
        var material = (RenderMaterial*)addresses[0];
        if (material->MaterialParameterCBuffer == null)
            return null;
        if (Element((KernelShaderPackage*)TargetOf(addresses[0]).ShaderPackage, crc) is not { } found)
            return null;
        var buffer = material->MaterialParameterCBuffer->TryGetBuffer();
        var start = found.Offset / 4;
        var count = found.Size / 4;
        return start + count <= buffer.Length ? buffer.Slice(start, count).ToArray() : null;
    }

    /// <summary>Whether every bound material holds what it held at bind time.</summary>
    public bool MatchesSnapshot()
    {
        foreach (var address in Materials())
        {
            var material = (RenderMaterial*)address;
            var snapshot = snapshots[address];
            var constants = material->MaterialParameterCBuffer == null ? [] : material->MaterialParameterCBuffer->TryGetBuffer();
            if (!constants.SequenceEqual(snapshot.Constants) || material->ShaderFlags != snapshot.Flags)
                return false;
        }
        return true;
    }

    /// <summary>Puts back what the bound materials held at bind time (where they are still loaded) and drops the binding.</summary>
    public void Restore()
    {
        try
        {
            foreach (var address in Materials())
            {
                var material = (RenderMaterial*)address;
                if (!snapshots.TryGetValue(address, out var snapshot))
                    continue;
                if (material->MaterialParameterCBuffer != null)
                {
                    var buffer = material->MaterialParameterCBuffer->TryGetBuffer();
                    if (buffer.Length == snapshot.Constants.Length)
                        snapshot.Constants.CopyTo(buffer);
                }
                material->ShaderFlags = snapshot.Flags;
                for (var i = 0; i < Math.Min(material->TextureCount, snapshot.SamplerFlags.Length); i++)
                    material->Textures[i].SamplerFlags = snapshot.SamplerFlags[i];
                if (material->ShaderKeyValues != null)
                    for (var i = 0; i < Math.Min(material->ShaderKeyCount, snapshot.Keys.Length); i++)
                        material->ShaderKeyValues[i] = snapshot.Keys[i];
            }
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not put the material's own values back.");
        }
        targets.Clear();
        snapshots.Clear();
    }

    public void Dispose() => Restore();

    // ---- Resolving ----

    /// <summary>The addresses of the distinct bound materials that still resolve to what was bound; stale places are skipped.</summary>
    private List<nint> Materials()
    {
        var seen = new List<nint>();
        foreach (var target in targets)
            if (!seen.Contains(target.Material) && Resolve(target.Binding) == target)
                seen.Add(target.Material);
        return seen;
    }

    private Target TargetOf(nint material) => targets.First(target => target.Material == material);

    private Target? Resolve(MaterialBinding binding)
    {
        var characterBase = DrawObject(objects, binding.ObjectIndex, binding.Kind);
        if (characterBase == null || characterBase->Models == null || binding.ModelSlot < 0 || binding.ModelSlot >= Math.Min(characterBase->SlotCount, MaximumModels))
            return null;
        var model = characterBase->Models[binding.ModelSlot];
        if (model == null || model->Materials == null || binding.MaterialSlot < 0 || binding.MaterialSlot >= model->MaterialCount)
            return null;
        var material = model->Materials[binding.MaterialSlot];
        if (material == null || material->MaterialResourceHandle == null)
            return null;
        var handle = material->MaterialResourceHandle;
        var package = handle->ShaderPackageResourceHandle == null ? null : handle->ShaderPackageResourceHandle->ShaderPackage;
        return new Target(binding, (nint)characterBase, (nint)material, (nint)handle, (nint)package);
    }

    internal static CharacterBase* DrawObject(IObjectTable objects, int objectIndex, DrawObjectKind kind)
    {
        if (objectIndex < 0 || objectIndex >= objects.Length || objects[objectIndex] is not ICharacter character || character.Address == 0)
            return null;
        var native = (Character*)character.Address;
        if (kind == DrawObjectKind.Character)
            return native->GetCharacterBase();
        var slot = kind == DrawObjectKind.MainHand ? DrawDataContainer.WeaponSlot.MainHand : DrawDataContainer.WeaponSlot.OffHand;
        var drawObject = native->DrawData.Weapon(slot).DrawData.DrawObject;
        return drawObject == null || drawObject->Object.GetObjectType() != ObjectType.CharacterBase ? null : (CharacterBase*)drawObject;
    }

    private static KernelShaderPackage.MaterialElement? Element(KernelShaderPackage* package, uint crc)
    {
        if (package == null || package->MaterialElements == null)
            return null;
        foreach (var element in package->MaterialElementsSpan)
            if (element.CRC == crc)
                return element;
        return null;
    }

    private void Warn(string message)
    {
        if (warned)
            return;
        warned = true;
        log.Information($"Material live preview: {message}");
    }
}
