using System.Collections.Immutable;
using System.Numerics;

namespace InstantEdit.Models;

[Flags]
internal enum PoseComponents { None = 0, Position = 1, Rotation = 2, Scale = 4, All = 7 }
internal enum AnimationDestination { NewMod, InPlace }
internal enum AnimationOperation { BakeOffsets, RepairSkeleton, CreateStartup }
internal enum AnimationStartupPose { ReferencePose, CharacterIdle }
internal enum SkeletonResolutionState { Searching, Matched, Ambiguous, Incompatible }
internal enum SkeletonSourceKind { Collection, Game, Mod }
internal sealed record BoneTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale);
internal sealed record SkeletonBone(string Name, short Parent, byte LockTranslation, BoneTransform Reference);
internal sealed record SkeletonPartition(string Name, short Start, short Count);
internal sealed record SkeletonDescription(string Name, string Fingerprint, ImmutableArray<SkeletonBone> Bones,
    ImmutableArray<string> FloatNames, ImmutableArray<float> ReferenceFloats, ImmutableArray<SkeletonPartition> Partitions);
internal sealed record AnimationSourceIdentity(ImmutableArray<string> MappedGamePaths, string? MappedModel,
    string? PapModel, string? CanonicalModel, string MappingFingerprint)
{
    public ImmutableArray<string> MappedGamePaths { get; init; } = MappedGamePaths.IsDefault ? [] : MappedGamePaths;
}
internal sealed record SkeletonSource(SkeletonSourceKind Kind, AnimationResource Resource, string Variant = "",
    ImmutableArray<string> MappedGamePaths = default, string? CanonicalModel = null, string MappingFingerprint = "")
{
    public ImmutableArray<string> MappedGamePaths { get; init; } = MappedGamePaths.IsDefault ? [] : MappedGamePaths;
}
internal sealed record SkeletonCandidate(SkeletonSource Source, SkeletonDescription Skeleton, long Rank, string Rationale);
internal sealed record SkeletonResolution(SkeletonResolutionState State, ImmutableArray<SkeletonCandidate> Candidates,
    SkeletonCandidate? Selected = null, string? Reason = null);
internal sealed record PoseBoneId(string Name, int Partial, int Slot = 0);
internal sealed record PoseIk(bool Enabled, int Type, bool EnforceConstraints, int Depth, int Iterations,
    int First, int Second, int End, Vector3 Axis);
internal sealed record PoseStack(Vector3 Position, Quaternion Rotation, Vector3 Scale, PoseComponents Propagate, PoseIk Ik);
internal sealed record PoseBone(PoseBoneId Id, ImmutableArray<PoseStack> Stacks, bool Face = false,
    ImmutableArray<string> IkChain = default)
{
    public ImmutableArray<string> IkChain { get; init; } = IkChain.IsDefault ? [] : IkChain;
}
internal sealed record PoseSnapshot(ushort MainTimeline, ushort UpperTimeline, ushort FaceTimeline, bool Global,
    ImmutableArray<PoseBone> Bones, DateTime CapturedUtc, string AdapterIdentity);
internal sealed record AnimationResource(string GamePath, string ResolvedPath, string Hash, string? ModDirectory = null,
    string? ModRoot = null, string? RelativePath = null, string? ModName = null);
internal sealed record AnimationClip(string GamePath, string Name, int BindingIndex, int Partial,
    ushort Timeline, string SkeletonPath, string SkeletonFingerprint = "", SkeletonDescription? TargetSkeleton = null,
    SkeletonResolution? Resolution = null, string BindingFingerprint = "", string SourceContext = "", string? LastOperationError = null,
    AnimationSourceIdentity? SourceIdentity = null, float Duration = 0, bool IsLoop = false);
internal sealed record AnimationCapture(string Id, ulong ActorId, long ActorAddress, Guid CollectionId,
    string CollectionName, string DisplayName, AnimationClip Clip, AnimationClip? Startup,
    ImmutableArray<string> FamilyPaths, ImmutableArray<AnimationResource> Sources, PoseSnapshot Pose,
    DateTime CapturedUtc, bool Playing, string? UnavailableReason = null, string? PackagingError = null,
    ImmutableArray<string> LoadedResourcePaths = default, string? PoseUnavailableReason = null,
    ImmutableDictionary<string, ImmutableArray<string>>? ResourceAliases = null)
{
    public ImmutableArray<string> LoadedResourcePaths { get; init; } = LoadedResourcePaths.IsDefault ? [] : LoadedResourcePaths;
    public ImmutableDictionary<string, ImmutableArray<string>> ResourceAliases { get; init; } =
        ResourceAliases ?? ImmutableDictionary<string, ImmutableArray<string>>.Empty;
}
internal sealed record AnimationBakeRequest(Guid Id, AnimationCapture Capture, AnimationDestination Destination,
    string ModName, bool IncludeStartup, ImmutableHashSet<PoseBoneId> SelectedBones, PoseComponents Components,
    AnimationOperation Operation = AnimationOperation.BakeOffsets,
    // Retained for old journal deserialization only; never bypasses source compatibility.
    bool AllowClosestSkeletonRepair = false, AnimationStartupOptions? StartupOptions = null);
internal sealed record AnimationStartupOptions(AnimationStartupPose Pose, float DurationSeconds);
internal sealed record AnimationStartupSource(AnimationClip Clip, AnimationResource Resource, byte[] Pap, byte[] Skeleton);
internal sealed record AnimationDependencyManifest(ImmutableArray<AnimationResource> Resources,
    ImmutableDictionary<string, byte[]> Files, string ManipulationsJson = "[]");
/// <summary>
/// One produced file. An empty <paramref name="Option"/> writes into the mod's default
/// data; a named one becomes a Penumbra option, so several variants may legitimately
/// produce the same game path.
/// </summary>
internal sealed record AnimationOutput(string GamePath, string Option, byte[] Bytes);
internal sealed record AnimationEditResult(Guid Id, bool Success, string Message, string? ModDirectory = null);
/// <summary>One guarded, durable LivePose clear operation for the current player.</summary>
internal sealed record LivePoseOffsetBackup(ulong ActorId, Guid CollectionId, PoseSnapshot Before,
    PoseSnapshot Cleared, DateTime ClearedUtc);

/// <summary>Durable transaction state. File and pose recovery both use compare-before-write.</summary>
internal sealed class AnimationEditJournal
{
    public int Version { get; set; } = 2;
    public Guid Id { get; set; }
    public string State { get; set; } = "Prepared";
    public string Message { get; set; } = "";
    public AnimationBakeRequest Request { get; set; } = null!;
    public string ModDirectory { get; set; } = "";
    public string ModRoot { get; set; } = "";
    public List<AnimationFileChange> Files { get; set; } = [];
    public PoseSnapshot? PoseBefore { get; set; }
    public PoseSnapshot? PoseAfter { get; set; }
    public bool OffsetsCleared { get; set; }
    public string PoseClearOutcome { get; set; } = "NotStarted";
    public Dictionary<string, string> ModFileHashes { get; set; } = [];
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
internal sealed record AnimationFileChange(string GamePath, string Target, string ModDirectory, string ModRoot,
    string RelativePath, string BeforeHash, string AfterHash, string Backup, string Staged, string Option = "")
{
    // Journals written before option groups existed have no Option; they load as
    // default-data changes, which is what they were.
    public string Option { get; init; } = Option ?? "";
}
