using InstantEdit.Services.Materials;
using static InstantEdit.TestSupport.Assertions;

/// <summary>Every material setting the editor shows has a plain label.</summary>
internal static partial class MaterialEditorScenarios
{
    // Read by some package, but nobody knows their names; their labels may say so.
    private static readonly HashSet<uint> UnnamedReadConstants = [0xAD94E254, 0xB61D7498, 0xAE4F649C, 0x2B5EB116, 0x5C598180, 0x6C159E95, 0x1A60F60E];

    public static void RunLabels()
    {
        CheckEveryReadConstantHasLabel();
        CheckEveryKeyValueHasLabel();
        CheckNoEllipsis();
        CheckKinds();
    }

    private static void CheckEveryReadConstantHasLabel()
    {
        var missing = ShaderPackages.All
            .SelectMany(package => package.ReadConstants.Select(id => (package.Name, Id: id)))
            .Where(item => ShaderLabels.Constant(item.Id).Label.StartsWith("Unknown", StringComparison.Ordinal) && !UnnamedReadConstants.Contains(item.Id))
            .Select(item => $"{item.Name} 0x{item.Id:X8}")
            .ToList();
        Require(missing.Count == 0, "every constant a package reads has a label" + (missing.Count > 0 ? ": missing " + string.Join(", ", missing) : ""));
        Require(ShaderPackages.Params.All(param => ShaderLabels.Constant(param.Id).Id == param.Id && !string.IsNullOrWhiteSpace(ShaderLabels.Constant(param.Id).Help)),
            "every shared parameter has a label entry with help text");
        Require(ShaderLabels.Constant(0x12345678) is { Label: "Unknown value 0x12345678", Kind: ValueKind.Float, Group: "Other" },
            "an unknown constant gets a hex label instead of throwing");
    }

    private static void CheckEveryKeyValueHasLabel()
    {
        var missing = new List<string>();
        foreach (var package in ShaderPackages.All)
            foreach (var key in package.Keys)
            {
                var label = ShaderLabels.Key(key.Id);
                if (label.Label.StartsWith("Unknown", StringComparison.Ordinal))
                    missing.Add($"{package.Name} key 0x{key.Id:X8}");
                foreach (var value in key.Values)
                    if (!label.Values.TryGetValue(value, out var name) || string.IsNullOrWhiteSpace(name))
                        missing.Add($"{package.Name} key 0x{key.Id:X8} value 0x{value:X8}");
            }
        Require(missing.Count == 0, "every key and key value of every package has a label" + (missing.Count > 0 ? ": missing " + string.Join(", ", missing) : ""));
        Require(ShaderLabels.KeyValue(0xF52CCF05, 0xA7D2FF60).Contains("0xA7D2FF60", StringComparison.Ordinal),
            "unnamed key values carry their id in the label");
        Require(ShaderLabels.Key(0x11111111).Label == "Unknown key 0x11111111" && ShaderLabels.KeyValue(0x11111111, 0x22222222) == "0x22222222",
            "unknown keys and values fall back to hex");
        Require(ShaderPackages.All.SelectMany(package => package.Samplers).All(sampler => !ShaderLabels.Sampler(sampler.Id).StartsWith("Unknown", StringComparison.Ordinal)),
            "every texture slot a package declares has a label");
    }

    private static void CheckNoEllipsis()
    {
        var texts = ShaderLabels.KnownConstants.SelectMany(label => new[] { label.Label, label.Help })
            .Concat(ShaderLabels.KnownKeys.SelectMany(label => label.Values.Values.Append(label.Label).Append(label.Help)))
            .Concat(ShaderLabels.KnownSamplers)
            .Concat(Enumerable.Range(0, 32).Select(ShaderLabels.RowField))
            .Concat(ShaderLabels.Flags.SelectMany(flag => new[] { flag.Label, flag.Help }))
            .Concat(ShaderLabels.WrapModes)
            .ToList();
        Require(texts.Count > 200 && texts.All(text => !text.Contains('…') && !text.Contains("...", StringComparison.Ordinal)),
            "no label or help text uses an ellipsis");
    }

    private static void CheckKinds()
    {
        Require(ShaderLabels.Constant(0x2C2A34DD).Kind == ValueKind.Color, "the diffuse colour is a colour");
        Require(ShaderLabels.Constant(0x29AC0223) is { Kind: ValueKind.Percent or ValueKind.Float, Min: 0f, Max: 1f, Group: "Alpha" },
            "the alpha cutoff runs from 0 to 1 in the Alpha group");
        Require(ShaderLabels.Constant(0x4255F2F4).Kind == ValueKind.TileIndex && ShaderLabels.Constant(0x59BDA0B1).Kind == ValueKind.ShaderId,
            "tile index and shader id get their own pickers");
        uint[] scrollIds = [0x43345395, 0x4172EDCC, 0x738A241C, 0x71CC9A45, 0xDA3D022F, 0xD87BBC76, 0xEA8375A6, 0xE8C5CBFF];
        Require(scrollIds.All(id => ShaderLabels.Constant(id).Group == "Scroll"), "the scroll constants are in the Scroll group");
        Require(ShaderLabels.Constant(0x285F72D2).Kind == ValueKind.Int && ShaderLabels.Constant(0x00A680BC).Kind == ValueKind.Int,
            "constants whose default is an integer are edited as integers");
        Require(ShaderLabels.Flags.Length == 32 && ShaderLabels.Flags[0].Label == "Hide backfaces" && ShaderLabels.Flags[4].Label == "Translucent",
            "shader flags 0x01 and 0x10 are named");
    }
}
