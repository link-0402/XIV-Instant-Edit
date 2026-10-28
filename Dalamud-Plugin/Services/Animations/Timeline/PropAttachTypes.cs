using System.Collections.Concurrent;
using System.Text.Json;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>
/// Which attach point (the 3-character code of the character's attach table, see <see cref="AtchTable"/>) each prop
/// model is held with. No file says it, so it is learned from a prop seen being held and kept in the config folder
/// (<c>prop-attach-types.json</c>, model number to code, <c>{ "1962": "tsl" }</c>).
/// </summary>
internal sealed class PropAttachTypes
{
    private const string FileName = "prop-attach-types.json";
    private readonly string? path;
    private readonly Action<string>? warn;
    private readonly ConcurrentDictionary<ushort, string> types = new();

    /// <param name="configFolder">Where the types are kept; null keeps them in memory only.</param>
    public PropAttachTypes(string? configFolder, Action<string>? warn = null)
    {
        this.warn = warn;
        if (configFolder == null)
            return;
        path = Path.Combine(configFolder, FileName);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path)) is { } saved)
                foreach (var (model, code) in saved)
                    if (ushort.TryParse(model, out var id) && Valid(id, code))
                        types[id] = code;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            warn?.Invoke("The prop attach types could not be read: " + e.Message);
        }
    }

    /// <summary>Every prop model learned and its attach point code.</summary>
    public IReadOnlyDictionary<ushort, string> All => types;

    /// <summary>The attach point code of a prop model; null when it was never learned.</summary>
    public string? CodeOf(ushort model) => types.TryGetValue(model, out var code) ? code : null;

    /// <summary>Keeps the attach point a prop model is held with, saved when it changed. A model 0 or a code that isn't 3 characters is dropped.</summary>
    public void Learn(ushort model, string code)
    {
        if (!Valid(model, code) || types.TryGetValue(model, out var known) && known == code)
            return;
        types[model] = code;
        if (path == null)
            return;
        try
        {
            var saved = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (id, text) in types)
                saved[id.ToString()] = text;
            AnimationJournalStore.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(saved));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke("A prop attach type could not be saved: " + e.Message);
        }
    }

    /// <summary>
    /// The attach point code a held prop's draw object names (4 bytes at +32 of its <c>DrawObjectData</c>): 3 printable
    /// ASCII characters stored reversed, then a NUL. Null for anything else, as for a slot that holds no prop.
    /// </summary>
    public static string? CodeFrom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[3] != 0)
            return null;
        Span<char> code = stackalloc char[3];
        for (var i = 0; i < 3; i++)
        {
            if (bytes[i] is < (byte)'!' or > (byte)'~')
                return null;
            code[2 - i] = (char)bytes[i];
        }
        return new string(code);
    }

    private static bool Valid(ushort model, string code) => model != 0 && code is { Length: 3 };
}
