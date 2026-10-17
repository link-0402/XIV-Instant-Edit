using System.Reflection;
using System.Text.RegularExpressions;
using InstantEdit.Services.Setup;

namespace InstantEdit.Services;

/// <summary>
/// The Blender add-on and the Painter plugin have their own versions. This build works with any
/// version at or above its minimum (InstantEdit.csproj), so a plugin release only asks for a
/// component update when it needs that component's newer code. Versions compare by
/// major.minor.build; a fourth part is ignored.
/// </summary>
public static partial class ComponentVersions
{
    private static readonly Lazy<string> AddonMinimum = new(() => Metadata("BlenderAddonMinimumVersion"));
    private static readonly Lazy<string> PainterMinimum = new(() => Metadata("PainterPluginMinimumVersion"));
    private static readonly Lazy<string> AddonLatest = new(() => Normalize(BlenderSetup.ManifestValue(Resource("InstantEdit.Setup.blender_manifest.toml"), "version")));
    private static readonly Lazy<string> PainterLatest = new(() => ParsePainterPluginVersion(Resource("InstantEdit.PainterPlugin.__init__.py")));

    /// <summary> The oldest Blender add-on this build works with; empty when the build doesn't declare one. </summary>
    public static string BlenderAddonMinimum => AddonMinimum.Value;

    /// <summary> The oldest Painter plugin this build works with; empty when the build doesn't declare one. </summary>
    public static string PainterPluginMinimum => PainterMinimum.Value;

    /// <summary> The add-on version in the manifest this build embeds: the newest one it knows of. </summary>
    public static string BlenderAddonLatest => AddonLatest.Value;

    /// <summary> The version of the Painter plugin this build embeds and installs. </summary>
    public static string PainterPluginLatest => PainterLatest.Value;

    /// <summary> Whether <paramref name="actual"/> is at or above <paramref name="minimum"/>. Missing or unreadable versions fail. </summary>
    public static bool Satisfies(string? actual, string? minimum)
        => Parse(actual) is { } version && Parse(minimum) is { } floor && version >= floor;

    /// <summary> Whether <paramref name="actual"/> is readable and below <paramref name="other"/>. </summary>
    public static bool IsOlder(string? actual, string? other)
        => Parse(actual) is { } version && Parse(other) is { } than && version < than;

    /// <summary> The <c>PLUGIN_VERSION</c> in a Painter plugin's <c>__init__.py</c>, or empty. </summary>
    public static string ParsePainterPluginVersion(string initSource)
    {
        var match = PluginVersionPattern().Match(initSource);
        return match.Success ? Normalize(match.Groups[1].Value) : "";
    }

    private static Version? Parse(string? version)
        => !string.IsNullOrWhiteSpace(version) && Version.TryParse(version.Trim(), out var parsed) && parsed.Build >= 0
            ? new Version(parsed.Major, parsed.Minor, parsed.Build)
            : null;

    private static string Normalize(string? version) => Parse(version)?.ToString() ?? "";

    private static string Metadata(string key)
        => Normalize(typeof(ComponentVersions).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)?.Value);

    private static string Resource(string name)
    {
        using var stream = typeof(ComponentVersions).Assembly.GetManifestResourceStream(name);
        return stream is null ? "" : new StreamReader(stream).ReadToEnd();
    }

    [GeneratedRegex("^PLUGIN_VERSION\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.Multiline)]
    private static partial Regex PluginVersionPattern();
}
