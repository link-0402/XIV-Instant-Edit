using System.Collections.Immutable;
using System.Numerics;

namespace InstantEdit.Models;

[Flags]
internal enum PoseComponents { None = 0, Position = 1, Rotation = 2, Scale = 4, All = 7 }
internal enum AnimationDestination { NewMod, InPlace }
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
    string? ModRoot = null, string? RelativePath = null);
internal sealed record AnimationClip(string GamePath, string Name, int BindingIndex, int Partial,
    ushort Timeline, string SkeletonPath, string SkeletonFingerprint = "");
internal sealed record AnimationCapture(string Id, ulong ActorId, long ActorAddress, Guid CollectionId,
    string CollectionName, string DisplayName, AnimationClip Clip, AnimationClip? Startup,
    ImmutableArray<string> FamilyPaths, ImmutableArray<AnimationResource> Sources, PoseSnapshot Pose,
    DateTime CapturedUtc, bool Playing, string? UnavailableReason = null, string? PackagingError = null,
    ImmutableArray<string> LoadedResourcePaths = default)
{
    public ImmutableArray<string> LoadedResourcePaths { get; init; } = LoadedResourcePaths.IsDefault ? [] : LoadedResourcePaths;
}
internal sealed record AnimationBakeRequest(Guid Id, AnimationCapture Capture, AnimationDestination Destination,
    string ModName, bool IncludeStartup, ImmutableHashSet<PoseBoneId> SelectedBones, PoseComponents Components);
internal sealed record AnimationDependencyManifest(ImmutableArray<AnimationResource> Resources,
    ImmutableDictionary<string, byte[]> Files, string ManipulationsJson = "[]");
internal sealed record AnimationEditResult(Guid Id, bool Success, string Message, string? ModDirectory = null);

/// <summary>Durable transaction state. File and pose recovery both use compare-before-write.</summary>
internal sealed class AnimationEditJournal
{
    public int Version { get; set; } = 1;
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
    string RelativePath, string BeforeHash, string AfterHash, string Backup, string Staged);
