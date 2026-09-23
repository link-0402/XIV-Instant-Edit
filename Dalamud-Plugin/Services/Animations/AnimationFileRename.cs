namespace InstantEdit.Services.Animations;

/// <summary>
/// The game and Penumbra cannot refresh an animation resource from an unchanged
/// physical path once it has been loaded, so an in-place rebake with LivePose or
/// Repair skeleton must land its output at a fresh path every time. A short
/// marker keeps repeated edits from growing the file name without bound: a
/// rebake replaces its own marker instead of stacking another one.
/// </summary>
internal static class AnimationFileRename
{
    private const string Marker = ".ie";

    /// <summary>The relative path an in-place rebake should write to next, replacing any marker this tool already added.</summary>
    public static string NextRelativePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var directory = slash < 0 ? "" : normalized[..(slash + 1)];
        var fileName = slash < 0 ? normalized : normalized[(slash + 1)..];
        var extension = Path.GetExtension(fileName);
        var stem = fileName[..^extension.Length];
        var markerIndex = stem.LastIndexOf(Marker, StringComparison.Ordinal);
        if (markerIndex >= 0 && IsMarkerToken(stem[(markerIndex + Marker.Length)..]))
            stem = stem[..markerIndex];
        var token = Guid.NewGuid().ToString("N")[..8];
        return $"{directory}{stem}{Marker}{token}{extension}";
    }

    private static bool IsMarkerToken(string value) =>
        value.Length == 8 && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
