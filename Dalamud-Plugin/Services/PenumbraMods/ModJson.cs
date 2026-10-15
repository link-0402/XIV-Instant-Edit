using System.Text.Json.Nodes;

namespace InstantEdit.Services.PenumbraMods;

/// <summary> Lenient reads of the values in a mod's meta.json: a wrong type reads as nothing. </summary>
internal static class ModJson
{
    internal static string? JsonString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static int JsonInt(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

    internal static Guid? ReadGuid(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var guid)
            ? guid
            : null;

    internal static int JsonIntFlexible(JsonNode? node)
    {
        if (node is not JsonValue value)
            return 0;
        if (value.TryGetValue<int>(out var number))
            return number;
        return value.TryGetValue<string>(out var text) && int.TryParse(text, out number) ? number : 0;
    }
}
