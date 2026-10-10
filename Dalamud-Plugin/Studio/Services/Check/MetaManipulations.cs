using System;
using System.Globalization;
using System.Linq;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;
using Newtonsoft.Json.Linq;

namespace InstantEdit.Studio.Services.Check;

/// <summary>Lenient readers for Penumbra's JSON, which may store numbers as strings.</summary>
internal static class JsonNumbers
{
    public static bool TryGetInt(JToken? token, out int value)
    {
        value = 0;
        switch (token?.Type)
        {
            case JTokenType.Integer:
                var number = token.Value<long>();
                if (number is < int.MinValue or > int.MaxValue) return false;
                value = (int)number;
                return true;
            case JTokenType.Float:
                var real = token.Value<double>();
                if (real != Math.Floor(real) || real is < int.MinValue or > int.MaxValue) return false;
                value = (int)real;
                return true;
            case JTokenType.String:
                return int.TryParse(token.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            default:
                return false;
        }
    }

    public static int GetInt(JToken? token, int fallback) => TryGetInt(token, out var value) ? value : fallback;
}

internal enum MetaType
{
    Unknown,
    Imc,
    Eqdp,
    Eqp,
    Est,
    Gmp,
    Rsp,
    GlobalEqp,
    Atch,
    Shp,
    Atr,
}

/// <summary>An EQDP manipulation: one race's entry for a set, of which the slot's two bits matter.</summary>
internal sealed record EqdpMeta(ushort Race, ushort SetId, EquipSlot Slot, ushort Entry)
{
    /// <summary>The slot's two bits (material, model), shifted down.</summary>
    public int SlotBits => (Entry >> SlotInfo.EqdpShift(Slot)) & 3;

    public bool HasModel => (SlotBits & 2) != 0;
}

/// <summary>An IMC manipulation or IMC group identifier: which model's variant it sets, and to what.</summary>
internal sealed record ImcMeta(string ObjectType, ushort PrimaryId, ushort SecondaryId, int Variant, EquipSlot? Slot, string? BodySlot, ImcEntry Entry);

/// <summary>
/// One manipulation of an option. <see cref="Root"/> is the item it changes, or null for kinds not
/// tied to one item (racial scaling, global EQP, attachments, shapes, attributes), which are never
/// taken for leftovers.
/// </summary>
internal sealed record ParsedManipulation(MetaType Type, string TypeName, JObject Json, string Identity, GameRoot? Root, EqdpMeta? Eqdp, ImcMeta? Imc,
    string Description);

/// <summary>Reads and writes Penumbra's manipulation JSON for the check (Mod Optimizer's MetaManipulations).</summary>
internal static class MetaManipulations
{
    public static ParsedManipulation Parse(JObject json)
    {
        var typeName = json.Value<string>("Type") ?? string.Empty;
        var type = Enum.TryParse<MetaType>(typeName, true, out var parsed) ? parsed : MetaType.Unknown;
        var body = json["Manipulation"] as JObject;
        var identity = PenumbraModMeta.IdentityOf(json);
        if (body is null)
            return new ParsedManipulation(MetaType.Unknown, typeName, json, identity, null, null, null, typeName);

        EqdpMeta? eqdp = null;
        ImcMeta? imc = null;
        GameRoot? root = null;
        var setId = (ushort)JsonNumbers.GetInt(body["SetId"], 0);
        switch (type)
        {
            case MetaType.Eqdp when SlotInfo.TryFromPenumbra(body.Value<string>("Slot"), out var slot)
                                 && RaceInfo.TryFromPenumbra(body.Value<string>("Race"), body.Value<string>("Gender"), out var race):
                eqdp = new EqdpMeta(race, setId, slot, (ushort)JsonNumbers.GetInt(body["Entry"], 0));
                root = Gear(SlotInfo.IsAccessory(slot), setId);
                break;
            case MetaType.Eqp when SlotInfo.TryFromPenumbra(body.Value<string>("Slot"), out var slot):
                root = Gear(SlotInfo.IsAccessory(slot), setId);
                break;
            case MetaType.Gmp:
                root = Gear(false, setId);
                break;
            case MetaType.Est:
                root = body.Value<string>("Slot")?.ToLowerInvariant() switch
                {
                    "hair"           => new GameRoot(GameRootKind.Hair, setId, 0, string.Empty),
                    "face"           => new GameRoot(GameRootKind.Face, setId, 0, string.Empty),
                    "body" or "head" => Gear(false, setId),
                    _                => null,
                };
                break;
            case MetaType.Imc:
                imc = ParseImc(body);
                root = imc is null ? null : RootOf(imc);
                break;
        }
        return new ParsedManipulation(type, typeName, json, identity, root, eqdp, imc, Describe(typeName, body));
    }

    /// <summary>An IMC identifier with its entry (an IMC manipulation's body, or an IMC group's identifier and default entry).</summary>
    public static ImcMeta? ParseImc(JObject identifier, JToken? entry = null)
    {
        var objectType = identifier.Value<string>("ObjectType");
        var parsedEntry = ImcEntryOf(entry ?? identifier["Entry"]);
        if (objectType is null || parsedEntry is null) return null;
        EquipSlot? slot = SlotInfo.TryFromPenumbra(identifier.Value<string>("EquipSlot"), out var equipSlot) ? equipSlot : null;
        return new ImcMeta(objectType, (ushort)JsonNumbers.GetInt(identifier["PrimaryId"], 0), (ushort)JsonNumbers.GetInt(identifier["SecondaryId"], 0),
            JsonNumbers.GetInt(identifier["Variant"], 0), slot, identifier.Value<string>("BodySlot"), parsedEntry.Value);
    }

    /// <summary>An IMC entry as Penumbra writes it in JSON.</summary>
    public static ImcEntry? ImcEntryOf(JToken? token)
    {
        if (token is not JObject obj) return null;
        return new ImcEntry(
            (byte)JsonNumbers.GetInt(obj["MaterialId"], 0),
            (byte)JsonNumbers.GetInt(obj["DecalId"], 0),
            (ushort)(JsonNumbers.GetInt(obj["AttributeMask"], 0) & 0x3FF),
            (byte)(JsonNumbers.GetInt(obj["SoundId"], 0) & 0x3F),
            (byte)JsonNumbers.GetInt(obj["VfxId"], 0),
            (byte)JsonNumbers.GetInt(obj["MaterialAnimationId"], 0));
    }

    public static GameRoot? RootOf(ImcMeta imc) => imc.ObjectType.ToLowerInvariant() switch
    {
        "equipment" => Gear(false, imc.PrimaryId),
        "accessory" => Gear(true, imc.PrimaryId),
        "weapon"    => new GameRoot(GameRootKind.Weapon, imc.PrimaryId, imc.SecondaryId, string.Empty),
        "monster"   => new GameRoot(GameRootKind.Monster, imc.PrimaryId, imc.SecondaryId, string.Empty),
        "demihuman" => new GameRoot(GameRootKind.Demihuman, imc.PrimaryId, imc.SecondaryId, string.Empty),
        _           => null,
    };

    /// <summary>Whether an IMC identifier is about the given model (and, for gear, its slot).</summary>
    public static bool Targets(ImcMeta imc, ModelPathInfo model)
    {
        var expected = model.Kind switch
        {
            GameModelKind.Equipment => "Equipment",
            GameModelKind.Accessory => "Accessory",
            GameModelKind.Weapon    => "Weapon",
            GameModelKind.Monster   => "Monster",
            GameModelKind.Demihuman => "DemiHuman",
            _                       => null,
        };
        if (expected is null || !imc.ObjectType.Equals(expected, StringComparison.OrdinalIgnoreCase) || imc.PrimaryId != model.PrimaryId)
            return false;
        return model.Kind switch
        {
            GameModelKind.Equipment or GameModelKind.Accessory => imc.Slot == model.Slot,
            GameModelKind.Demihuman => imc.SecondaryId == model.SecondaryId && imc.Slot == model.Slot,
            _ => imc.SecondaryId == model.SecondaryId,
        };
    }

    /// <summary>An EQDP manipulation giving one race the slot bits (material, model) for a set.</summary>
    public static JObject Eqdp(ushort race, ushort setId, EquipSlot slot, int slotBits)
    {
        var who = RaceInfo.FromCode(race) ?? throw new ArgumentOutOfRangeException(nameof(race), race, "Not a playable race code.");
        return new JObject
        {
            ["Type"] = "Eqdp",
            ["Manipulation"] = new JObject
            {
                ["Gender"] = who.PenumbraGender,
                ["Race"]   = who.PenumbraRace,
                ["SetId"]  = setId,
                ["Slot"]   = SlotInfo.LabelMap[slot],
                ["Entry"]  = (slotBits & 3) << SlotInfo.EqdpShift(slot),
            },
        };
    }

    private static GameRoot Gear(bool accessory, ushort setId)
        => new(accessory ? GameRootKind.Accessory : GameRootKind.Equipment, setId, 0, string.Empty);

    private static string Describe(string type, JObject body)
    {
        var fields = new[] { "ObjectType", "Slot", "EquipSlot", "BodySlot", "SetId", "PrimaryId", "SecondaryId", "Variant", "Race", "Gender", "SubRace", "Attribute" }
            .Where(body.ContainsKey)
            .Select(key => $"{key} {body[key]}");
        return $"{type} ({string.Join(", ", fields)})";
    }
}
