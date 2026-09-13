using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InstantEdit.Services.Animations;

/// <summary>Penumbra's version-prefixed GZip metadata IPC format, as used by VFXEditor.</summary>
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
        if (bytes.Length < 2) throw new InvalidDataException("Unsupported Penumbra metadata serialization version.");
        return bytes[0] switch
        {
            0 => JsonNode.Parse(bytes.AsSpan(1)) as JsonArray
                ?? throw new InvalidDataException("Unsupported Penumbra metadata shape."),
            1 => DecodeV1(bytes.AsSpan(1)),
            _ => throw new InvalidDataException($"Unsupported Penumbra metadata serialization version {bytes[0]}.")
        };
    }

    // Penumbra 5.15 switched the metadata IPC payload from JSON (v0) to the
    // compact META0001 binary format. Animation packaging only needs IMC and
    // EST records; the sections between them are still consumed and
    // bounds-checked so a malformed payload cannot shift the records we use.
    private static JsonArray DecodeV1(ReadOnlySpan<byte> data)
    {
        if (!data.StartsWith("META0001"u8))
            throw new InvalidDataException("Unsupported Penumbra metadata serialization shape.");

        var reader = new MetadataReader(data[8..]);
        var result = new JsonArray();
        ReadImc(ref reader, reader.ReadCount("IMC"), result);
        reader.SkipRecords(reader.ReadCount("EQP"), 12, "EQP");
        reader.SkipRecords(reader.ReadCount("EQDP"), 8, "EQDP");
        ReadEst(ref reader, reader.ReadCount("EST"), result);
        return result;
    }

    private static void ReadImc(ref MetadataReader reader, int count, JsonArray result)
    {
        for (var i = 0; i < count; ++i)
        {
            var primary = reader.ReadUInt16();
            var variant = reader.ReadByte();
            var objectType = reader.ReadByte();
            var secondary = reader.ReadUInt16();
            var equipSlot = reader.ReadByte();
            var bodySlot = reader.ReadByte();
            var entry = new JsonObject
            {
                ["MaterialId"] = (int)reader.ReadByte(),
                ["DecalId"] = (int)reader.ReadByte(),
            };
            var attributes = reader.ReadUInt16();
            entry["VfxId"] = (int)reader.ReadByte();
            entry["MaterialAnimationId"] = (int)reader.ReadByte();
            entry["AttributeMask"] = attributes & 0x03FF;
            entry["SoundId"] = attributes >> 10;

            var manipulation = new JsonObject
            {
                ["ObjectType"] = ObjectTypeName(objectType),
                // JsonValue<byte/ushort> cannot be read with GetValue<int>() by Applicable.
                ["PrimaryId"] = (int)primary,
                ["Variant"] = (int)variant,
                ["Entry"] = entry,
            };
            switch (objectType)
            {
                case 2: // DemiHuman
                    manipulation["SecondaryId"] = (int)secondary;
                    manipulation["EquipSlot"] = EquipSlotName(equipSlot);
                    break;
                case 3: // Accessory
                case 11: // Equipment
                    manipulation["EquipSlot"] = EquipSlotName(equipSlot);
                    break;
                case 6: // Monster
                case 13: // Weapon
                    manipulation["SecondaryId"] = (int)secondary;
                    manipulation["BodySlot"] = BodySlotName(bodySlot);
                    break;
                default:
                    throw new InvalidDataException("Invalid IMC object type in Penumbra metadata.");
            }
            result.Add(new JsonObject { ["Type"] = "Imc", ["Manipulation"] = manipulation });
        }
    }

    private static void ReadEst(ref MetadataReader reader, int count, JsonArray result)
    {
        for (var i = 0; i < count; ++i)
        {
            var setId = reader.ReadUInt16();
            var slot = reader.ReadByte();
            reader.ReadByte(); // padding in EstIdentifier
            var genderRace = reader.ReadUInt16();
            var entry = reader.ReadUInt16();
            var (gender, race) = GenderRaceNames(genderRace);
            var manipulation = new JsonObject
            {
                ["Gender"] = gender,
                ["Race"] = race,
                ["SetId"] = (int)setId,
                ["Slot"] = slot switch
                {
                    // EstType uses CharacterUtility MetaIndex values, not ordinal slot indices.
                    // Penumbra/Meta/Manipulations/Est.cs and Penumbra/Interop/Structs/MetaIndex.cs.
                    72 => "Face",
                    73 => "Hair",
                    74 => "Head",
                    75 => "Body",
                    _ => throw new InvalidDataException($"Invalid EST slot in Penumbra metadata: {slot}."),
                },
                ["Entry"] = (int)entry,
            };
            result.Add(new JsonObject { ["Type"] = "Est", ["Manipulation"] = manipulation });
        }
    }

    private static string ObjectTypeName(byte value) => value switch
    {
        2 => "DemiHuman",
        3 => "Accessory",
        6 => "Monster",
        11 => "Equipment",
        13 => "Weapon",
        _ => throw new InvalidDataException("Invalid IMC object type in Penumbra metadata."),
    };

    private static string EquipSlotName(byte value) => value switch
    {
        1 => "MainHand",
        2 => "OffHand",
        3 => "Head",
        4 => "Body",
        5 => "Hands",
        6 => "Belt",
        7 => "Legs",
        8 => "Feet",
        9 => "Ears",
        10 => "Neck",
        11 => "Wrists",
        12 => "RFinger",
        13 => "BothHand",
        14 => "LFinger",
        15 => "HeadBody",
        16 => "BodyHandsLegsFeet",
        17 => "SoulCrystal",
        18 => "LegsFeet",
        19 => "FullBody",
        20 => "BodyHands",
        21 => "BodyLegsFeet",
        22 => "ChestHands",
        23 => "ChestLegs",
        24 => "Nothing",
        25 => "All",
        _ => throw new InvalidDataException("Invalid equipment slot in Penumbra metadata."),
    };

    private static string BodySlotName(byte value) => value switch
    {
        0 => "Unknown",
        1 => "Hair",
        2 => "Face",
        3 => "Tail",
        4 => "Body",
        5 => "Ear",
        6 => "Head",
        _ => throw new InvalidDataException("Invalid body slot in Penumbra metadata."),
    };

    private static (string Gender, string Race) GenderRaceNames(ushort value)
    {
        var code = value / 100;
        var suffix = value % 100;
        if (code is 91 or 92)
            return (code == 91 ? "MaleNpc" : "FemaleNpc", "Unknown");
        if (code is < 1 or > 18)
            throw new InvalidDataException("Invalid gender-race code in Penumbra metadata.");

        string[] races = ["", "Midlander", "Highlander", "Elezen", "Lalafell", "Miqote", "Roegadyn", "AuRa", "Hrothgar", "Viera"];
        var race = races[(code + 1) / 2];
        var gender = suffix switch
        {
            1 => (code & 1) is 1 ? "Male" : "Female",
            2 or 3 => "Unknown",
            4 => (code & 1) is 1 ? "MaleNpc" : "FemaleNpc",
            _ => throw new InvalidDataException("Invalid gender-race code in Penumbra metadata."),
        };
        return (gender, race);
    }

    private ref struct MetadataReader
    {
        private readonly ReadOnlySpan<byte> data;
        private int position;

        public MetadataReader(ReadOnlySpan<byte> data) => this.data = data;

        public int ReadCount(string section)
        {
            var count = ReadInt32();
            if (count < 0) throw new InvalidDataException($"Invalid {section} count in Penumbra metadata.");
            return count;
        }

        public byte ReadByte() => ReadSpan(1)[0];

        public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(ReadSpan(sizeof(ushort)));

        private int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(ReadSpan(sizeof(int)));

        public void SkipRecords(int count, int recordSize, string section)
        {
            try { Skip(checked((long)count * recordSize)); }
            catch (OverflowException) { throw new InvalidDataException($"Invalid {section} data in Penumbra metadata."); }
        }

        private void Skip(long count)
        {
            if (count < 0 || count > Remaining) throw new InvalidDataException("Truncated Penumbra metadata payload.");
            position += checked((int)count);
        }

        private ReadOnlySpan<byte> ReadSpan(int count)
        {
            if (count < 0 || count > Remaining) throw new InvalidDataException("Truncated Penumbra metadata payload.");
            var value = data.Slice(position, count);
            position += count;
            return value;
        }

        private int Remaining => data.Length - position;
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
