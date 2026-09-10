using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InstantEdit.Services.Animations;

/// <summary>Penumbra's version-prefixed GZip JSON IPC format, as used by VFXEditor.</summary>
internal static partial class AnimationMetadata
{
    public static JsonArray Decode(string encoded)
    {
        if (encoded.Length > 8 * 1024 * 1024) throw new InvalidDataException("The collection metadata snapshot is too large.");
        using var compressed = new MemoryStream(Convert.FromBase64String(encoded));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = gzip.Read(buffer)) > 0)
        {
            if (output.Length + read > 8 * 1024 * 1024) throw new InvalidDataException("The collection metadata expands beyond 8 MiB.");
            output.Write(buffer, 0, read);
        }
        var bytes = output.ToArray();
        if (bytes.Length < 3 || bytes[0] != 0) throw new InvalidDataException("Unsupported Penumbra metadata serialization version.");
        return JsonNode.Parse(bytes.AsSpan(1)) as JsonArray ?? throw new InvalidDataException("Unsupported Penumbra metadata shape.");
    }

    public static JsonArray Applicable(JsonArray all, IEnumerable<string> paths)
    {
        var selected = paths.ToArray();
        var result = new JsonArray();
        foreach (var node in all)
        {
            if (node is not JsonObject item || item["Type"] == null || item["Manipulation"] is not JsonObject payload)
                throw new InvalidDataException("An effective metadata entry is malformed.");
            var type = item["Type"]!.GetValue<string>();
            var applicable = false;
            if (type == "Est")
            {
                // EST is relevant only when it selects one of this player's packaged partial skeletons.
                var entry = payload["Entry"]?.GetValue<int>() ?? throw new InvalidDataException("Invalid EST entry.");
                var slot = payload["Slot"]?.GetValue<string>() ?? throw new InvalidDataException("Invalid EST slot.");
                var part = slot switch { "Body" => "base", "Head" => "met", "Hair" => "hair", "Face" => "face", _ => "" };
                foreach (var path in selected)
                {
                    var match = SkeletonPath().Match(path);
                    if (!match.Success || part != match.Groups[2].Value || entry != int.Parse(match.Groups[3].Value)) continue;
                    var code = int.Parse(match.Groups[1].Value) / 100;
                    var race = (code + 1) / 2;
                    string[] raceNames = ["", "Midlander", "Highlander", "Elezen", "Lalafell", "Miqote", "Roegadyn", "AuRa", "Hrothgar", "Viera"];
                    if (race < 1 || race >= raceNames.Length) throw new InvalidDataException("Unsupported player race for EST metadata.");
                    applicable |= payload["Race"]?.GetValue<string>() == raceNames[race] &&
                        payload["Gender"]?.GetValue<string>() == (code % 2 == 1 ? "Male" : "Female");
                }
            }
            else if (type == "Imc")
            {
                var primary = payload["PrimaryId"]?.GetValue<int>() ?? throw new InvalidDataException("Invalid IMC primary ID.");
                var kind = payload["ObjectType"]?.GetValue<string>();
                var prefix = kind switch { "Equipment" => $"chara/equipment/e{primary:D4}/", "Accessory" => $"chara/accessory/a{primary:D4}/",
                    "Weapon" => $"chara/weapon/w{primary:D4}/", "Monster" => $"chara/monster/m{primary:D4}/", "DemiHuman" => $"chara/demihuman/d{primary:D4}/", _ => null };
                applicable = prefix != null && selected.Any(p => p.StartsWith(prefix, StringComparison.Ordinal));
            }
            if (applicable) result.Add(item.DeepClone());
        }
        return result;
    }
    [GeneratedRegex(@"^chara/human/c(\d{4})/skeleton/(base|met|hair|face)/[a-z](\d{4})/.*\.sklb$", RegexOptions.CultureInvariant)]
    private static partial Regex SkeletonPath();
}
