using InstantEdit.Services.Materials;
using static InstantEdit.TestSupport.Assertions;

/// <summary>The per-package shader facts generated from the game's character shader packages.</summary>
internal static partial class MaterialEditorScenarios
{
    public static void RunPackages()
    {
        CheckCharacterFacts();
        CheckScrollFacts();
        CheckSharedParams();
    }

    private static void CheckCharacterFacts()
    {
        var character = ShaderPackages.Get("character.shpk");
        uint[] expected = [0x29AC0223, 0xD925FF32, 0xB7FA33E2, 0xB5545FBB, 0x641E0F22, 0xD26FF0AE, 0xD62BF368, 0xD07A6A65, 0xAD94E254, 0x39551220, 0xB61D7498, 0x5351646E, 0x6421DD30];
        Require(character != null && character.ReadConstants.SetEquals(expected), "character.shpk reads exactly its 13 constants");
        var textureMode = character?.Keys.FirstOrDefault(key => key.Id == 0xB616DC5A);
        Require(textureMode != null && textureMode.Default == 0x5CC605B5 && textureMode.Values.Order().SequenceEqual([0x5CC605B5u, 0x600EF9DFu]),
            "character.shpk's texture mode key has MultiMaterial (default) and Compatibility");
        Require(character is { SamplesColorTable: true }, "character.shpk samples the colour table");
        Require(character != null && character.SamplerOnlyWhen.TryGetValue(0x115306BE, out var diffuse)
                && diffuse.Single() is { Key: 0xB616DC5A } only && only.Values.SequenceEqual([0x600EF9DFu]),
            "character.shpk samples the diffuse only in Compatibility mode");
        Require(character != null && character.Samplers.Select(sampler => sampler.Id).ToHashSet().SetEquals([0x0C5EC1F1u, 0x8A4E82B6u, 0x565F8FD8u, 0x115306BEu, 0xA7E197F6u]),
            "character.shpk's material slots are normal, mask, index, diffuse and flow");
        Require(ShaderPackages.Get("CHARACTER.SHPK") == character && ShaderPackages.Get("shader/sm5/shpk/character.shpk") == character
                && ShaderPackages.Get("bg.shpk") == null,
            "packages are found by file name in any case, and unknown ones are not");
    }

    private static void CheckScrollFacts()
    {
        var scroll = ShaderPackages.Get("characterscroll.shpk");
        var source = scroll?.Keys.FirstOrDefault(key => key.Id == 0xF886E10E);
        Require(source != null && source.Default == 0x69EB4AE0 && source.Values.Order().SequenceEqual([0x69EB4AE0u, 0x9A8A46F5u, 0xD21B9BEDu]),
            "characterscroll.shpk's reflection source key has sphere map (default), catchlight and both");
        uint[] scrollIds = [0x43345395, 0x4172EDCC, 0x738A241C, 0x71CC9A45, 0xDA3D022F, 0xD87BBC76, 0xEA8375A6, 0xE8C5CBFF];
        Require(scroll != null && scrollIds.All(scroll.ReadConstants.Contains), "characterscroll.shpk reads the eight scroll constants");
        Require(scroll != null && scroll.SamplerOnlyWhen.TryGetValue(0xFEA0F3D2, out var catchlight)
                && catchlight.Single().Values.Order().SequenceEqual([0x9A8A46F5u, 0xD21B9BEDu]),
            "characterscroll.shpk samples the catchlight only when the reflection source uses it");
        Require(ShaderPackages.Get("hair.shpk") is { SamplesColorTable: false } && ShaderPackages.Get("skin.shpk") is { SamplesColorTable: false },
            "hair and skin have no colour table");
        Require(ShaderPackages.Get("skin.shpk") is { } skin && skin.ConstantsReadOnlyWhen.TryGetValue(0x38A64362, out var emissive)
                && emissive.Single().Values.SequenceEqual([0x72E697CDu]),
            "skin.shpk reads the emissive colour only for FaceEmissive");
        Require(ShaderPackages.All.Count == 13, "all 13 character packages have facts");
    }

    private static void CheckSharedParams()
    {
        var ordered = ShaderPackages.Params.OrderBy(param => param.ByteOffset).ToArray();
        Require(ordered.Length == 81 && ShaderPackages.ParamBlockSize == 416 && ordered.Sum(param => param.ByteSize) == 408
                && ordered.Zip(ordered.Skip(1)).All(pair => pair.First.ByteOffset + pair.First.ByteSize <= pair.Second.ByteOffset)
                && ordered[^1].ByteOffset + ordered[^1].ByteSize <= ShaderPackages.ParamBlockSize,
            "the shared parameter block has 81 entries, 408 bytes of values in a 416-byte block, none overlapping");
        var alpha = ShaderPackages.Params.FirstOrDefault(param => param.Id == 0x29AC0223);
        Require(alpha is { ByteOffset: 12, ByteSize: 4 } && alpha.Default.SequenceEqual([0f]), "g_AlphaThreshold sits at byte 12, one float, default 0");
        Require(ShaderPackages.Param(0x2E60B071) is { ByteSize: 8 } tile && tile.Default.SequenceEqual([16f, 16f]) && ShaderPackages.Param(0x12345678) == null,
            "params are found by id: g_TileScale is two floats of 16");
    }
}
