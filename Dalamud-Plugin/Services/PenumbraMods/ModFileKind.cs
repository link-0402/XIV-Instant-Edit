using InstantEdit.Services.Physics;

namespace InstantEdit.Services.PenumbraMods;

/// <summary>
/// A kind of single game file a builder saves into its own mod: the mod folder its numbered copies go in
/// (<c>physics/phy_c0801b0001.3.phyb</c>), its extension, the words messages name it with, and its largest size.
/// </summary>
internal sealed record ModFileKind(string Folder, string Extension, string Noun, int MaxBytes)
{
    public static ModFileKind Physics { get; } = new(PhysicsModService.PhysicsFolder, ".phyb", "physics", PhybFile.MaxFileBytes);

    /// <summary> A body skeleton's parameters (<c>.skp</c>): head tracking. </summary>
    public static ModFileKind SkeletonParams { get; } = new("skeleton", ".skp", "skeleton parameters", 64 * 1024);
}

/// <summary> What a temporary preview of one <see cref="ModFileKind"/> file is called in Penumbra and where it keeps its files and record. </summary>
internal sealed record FilePreviewKind(string Tag, string CacheFolder, string RecordFile, ModFileKind File)
{
    public static FilePreviewKind Physics { get; } = new(PhysicsPreviewService.Tag, PhysicsPreviewService.CacheFolder, "PhysicsPreview.json", ModFileKind.Physics);

    public static FilePreviewKind HeadTracking { get; } =
        new("XIV Instant Edit head tracking preview", "head-tracking-preview", "HeadTrackingPreview.json", ModFileKind.SkeletonParams);
}
