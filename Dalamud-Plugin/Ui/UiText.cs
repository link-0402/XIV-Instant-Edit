namespace InstantEdit.Ui;

/// <summary> Text helpers shared by the windows and their view models. </summary>
internal static class UiText
{
    /// <summary> The value, or the fallback when it is null or whitespace. </summary>
    public static string Safe(string? value, string fallback = "")
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary> An ImGui id fragment: never empty and free of embedded nulls. </summary>
    public static string SafeId(string? value)
    {
        var id = Safe(value, "resource");
        return id.Replace("\0", string.Empty, StringComparison.Ordinal);
    }

    /// <summary> A file-name-safe version of a display name. </summary>
    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var value = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return value.Length == 0 ? "Object" : value;
    }
}
