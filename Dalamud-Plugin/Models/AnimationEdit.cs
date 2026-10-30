using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Models;

[Flags]
internal enum PoseComponents { None = 0, Position = 1, Rotation = 2, Scale = 4, All = 7 }
internal enum AnimationDestination { NewMod, InPlace }
// Journals store operations by number. BakeOffsets (LivePose bakes) and 2-5 belonged to
// operations that were removed (startup transitions, slot swaps, facial expressions,
// retiming) and stay unused so those older journals still load and undo.
internal enum AnimationOperation { BakeOffsets = 0, RepairSkeleton = 1, ExcludeBones = 6, EditTimeline = 7 }
internal enum SkeletonResolutionState { Searching, Matched, Ambiguous, Incompatible }
internal enum SkeletonSourceKind { Collection, Game, Mod }
// The shared body skeleton layouts, each extending the one before it: the game's own,
// then IVCS's iv_ bones, then YAS's ya_ bones. Animations bind bones by index, and
// these are the indices other players' skeletons and sync plugins expect.
internal enum SkeletonStandard { Vanilla, Ivcs, IvcsYas }
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
// The main skeleton of one skeleton file, or with LeadingBones only its first bones: a
// predictive animation made before a skeleton mod appended bones needs exactly those.
// Journals saved before only main skeletons were read also name a Variant, the mapper
// skeleton they meant; it is ignored on load.
internal sealed record SkeletonSource(SkeletonSourceKind Kind, AnimationResource Resource,
    ImmutableArray<string> MappedGamePaths = default, string? CanonicalModel = null, string MappingFingerprint = "",
    int LeadingBones = 0)
{
    public ImmutableArray<string> MappedGamePaths { get; init; } = MappedGamePaths.IsDefault ? [] : MappedGamePaths;
}
internal sealed record SkeletonCandidate(SkeletonSource Source, SkeletonDescription Skeleton, long Rank, string Rationale);
/// <summary>A standard layout's skeleton for the clip's model, and where it was found.</summary>
internal sealed record StandardSkeleton(SkeletonStandard Standard, SkeletonDescription Skeleton, string Origin);
internal sealed record SkeletonResolution(SkeletonResolutionState State, ImmutableArray<SkeletonCandidate> Candidates,
    SkeletonCandidate? Selected = null, string? Reason = null,
    // The source-skeleton bone each of the clip's transform tracks drives, in track
    // order. Resolutions recorded before this was kept load with none.
    ImmutableArray<short> TrackBones = default,
    // The standard skeletons found for the clip's model, smallest first; skeleton
    // repair retargets onto one of them. Older resolutions load with none.
    ImmutableArray<StandardSkeleton> Standards = default,
    // The character's race when the clip is another race's (e.g. "c0801"), and that race's
    // standard skeletons, smallest first. Older resolutions load with none.
    string? ActorModel = null, ImmutableArray<StandardSkeleton> ActorStandards = default)
{
    public ImmutableArray<short> TrackBones { get; init; } = TrackBones.IsDefault ? [] : TrackBones;
    public ImmutableArray<StandardSkeleton> Standards { get; init; } = Standards.IsDefault ? [] : Standards;
    public ImmutableArray<StandardSkeleton> ActorStandards { get; init; } = ActorStandards.IsDefault ? [] : ActorStandards;
}
/// <summary>
/// The race a borrowed clip is rebuilt for and the standard skeleton it is rebuilt onto. Without a
/// <paramref name="Model"/> it is a repair onto a standard of the clip's own race, edited on that standard as well.
/// </summary>
internal sealed record RaceRebuild(string? Model, StandardSkeleton Target);
// A bone's adjustments in a .livepose file: its stacks in order, each with whether it is an IK stack.
internal sealed record PoseBoneId(string Name, int Partial, int Slot = 0);
internal sealed record PoseStack(Vector3 Position, Quaternion Rotation, Vector3 Scale, PoseComponents Propagate, bool Ik);
internal sealed record PoseBone(PoseBoneId Id, ImmutableArray<PoseStack> Stacks);
internal sealed record AnimationResource(string GamePath, string ResolvedPath, string Hash, string? ModDirectory = null,
    string? ModRoot = null, string? RelativePath = null, string? ModName = null);
internal sealed record AnimationClip(string GamePath, string Name, int BindingIndex, int Partial,
    ushort Timeline, string SkeletonPath, string SkeletonFingerprint = "", SkeletonDescription? TargetSkeleton = null,
    SkeletonResolution? Resolution = null, string BindingFingerprint = "", string SourceContext = "", string? LastOperationError = null,
    AnimationSourceIdentity? SourceIdentity = null, float Duration = 0, bool IsLoop = false);
// ExpressionTimeline is set for an expression (an emote such as /smile playing a face on the
// face skeleton): its own timeline file, chara/action/facial/pose/<name>.tmb, as the collection
// resolved it. The face motion is Clip; the timeline is a file of its own, not part of the PAP.
// ActorIndex is the object the animation was captured from: 0 for your own character, 200 or
// more for a GPose actor. Captures journaled before it was kept load as your own character's.
// Captures journaled before LivePose was dropped also hold a Pose; it is ignored on load.
internal sealed record AnimationCapture(string Id, ulong ActorId, long ActorAddress, Guid CollectionId,
    string CollectionName, string DisplayName, AnimationClip Clip, AnimationClip? Startup,
    ImmutableArray<string> FamilyPaths, ImmutableArray<AnimationResource> Sources,
    DateTime CapturedUtc, bool Playing, string? UnavailableReason = null,
    ImmutableArray<string> LoadedResourcePaths = default,
    ImmutableDictionary<string, ImmutableArray<string>>? ResourceAliases = null,
    AnimationResource? ExpressionTimeline = null, ushort ActorIndex = 0)
{
    public ImmutableArray<string> LoadedResourcePaths { get; init; } = LoadedResourcePaths.IsDefault ? [] : LoadedResourcePaths;
    public ImmutableDictionary<string, ImmutableArray<string>> ResourceAliases { get; init; } =
        ResourceAliases ?? ImmutableDictionary<string, ImmutableArray<string>>.Empty;
}
// LivePose bakes journaled the offsets they baked too (SelectedBones, Components); they are ignored on load.
internal sealed record AnimationBakeRequest(Guid Id, AnimationCapture Capture, AnimationDestination Destination,
    string ModName, bool IncludeStartup, AnimationOperation Operation,
    // Retained for old journal deserialization only; never bypasses source compatibility.
    bool AllowClosestSkeletonRepair = false,
    // Animated bones, by name, that a rebake leaves without a track so nothing in
    // the animation drives them any more. Older journals load with none.
    ImmutableArray<string> ExcludedBones = default,
    // The standard skeleton a repair retargets onto. Repairs journaled before this
    // retargeted onto the live skeleton and load with none.
    SkeletonStandard? RepairTarget = null,
    // What a timeline edit changes; null for every other operation.
    AnimationTimelineEdit? Timeline = null,
    // The race a timeline save rebuilds for (e.g. "c0801"), whose standard RepairTarget names;
    // null for the clip's own race, as in older journals.
    string? RepairModel = null)
{
    public ImmutableArray<string> ExcludedBones { get; init; } = ExcludedBones.IsDefault ? [] : ExcludedBones;
}
/// <summary>
/// A timeline edit of one clip: the loop, or its startup when <paramref name="Startup"/> is set.
/// The rewritten timeline itself travels in the staged file. <paramref name="EntryIndex"/> is the
/// clip's timeline inside its PAP, or -1 for an expression's timeline, a TMB file of its own.
/// <paramref name="DurationSeconds"/> is the clip's new length when it changed, which resamples
/// its motion. <paramref name="Motion"/> are the bone layers baked into the motion. With neither,
/// the motion stays byte for byte. Journals written before bone layers load with none.
/// <paramref name="MotionBaseHash"/> is the hash of the file whose motion the layers were baked onto, kept beside
/// the journal so the edit can be opened again with its layers (Edit again); null without layers,
/// with a new length, and in journals written before Edit again.
/// <paramref name="Cuts"/> is how the motion was cut (parts, joins, loop seam), applied before the
/// layers; null keeps it whole, as in journals written before cuts. With cuts,
/// <paramref name="DurationSeconds"/> always holds the output length.
/// </summary>
internal sealed record AnimationTimelineEdit(bool Startup, int EntryIndex, float? DurationSeconds,
    ImmutableArray<MotionLayer> Motion = default, string? MotionBaseHash = null, MotionCuts? Cuts = null,
    ImmutableDictionary<string, string>? CutSounds = null)
{
    public ImmutableArray<MotionLayer> Motion { get; init; } = Motion.IsDefault ? [] : Motion;
}
/// <summary>
/// One member of an animation family the player can switch between, such as the
/// standing idles or the ground-sitting set. Most are numbered files like
/// <c>emote/j_pose02_loop.pap</c>, but every family also has an unnumbered base
/// member in another directory: <c>resident/jmn.pap</c> is ground-sit 0.
/// </summary>
internal sealed record AnimationSlot(string Family, int Index, bool Startup);
internal sealed record AnimationDependencyManifest(ImmutableArray<AnimationResource> Resources,
    ImmutableDictionary<string, byte[]> Files, string ManipulationsJson = "[]");
internal sealed record AnimationEditResult(Guid Id, bool Success, string Message, string? ModDirectory = null);

/// <summary>
/// The Timeline window's unsaved edits of one clip, kept on disk so a plugin reload or a game crash doesn't
/// lose them. <paramref name="FileHash"/> and <paramref name="BaseHash"/> are the clip's file and the motion
/// its layers apply to: the edits come back only onto the same ones. The document's sound edits travel in
/// <paramref name="SoundEdits"/>, since JSON can't key a dictionary by <see cref="TimelineSoundKey"/>.
/// </summary>
internal sealed record TimelineAutosave(AnimationCapture Capture, bool Startup, string FileHash, string? BaseHash,
    TimelineDocument Document, ImmutableArray<(TimelineSoundKey Key, TimelineSoundEdit Edit)> SoundEdits,
    ImmutableArray<TimelineImport> Imports, DateTime SavedUtc)
{
    public TimelineDocument Restored => Document with { SoundEdits = SoundEdits.ToImmutableDictionary(p => p.Key, p => p.Edit) };
}

/// <summary>
/// Durable transaction state; file recovery uses compare-before-write. Journals of LivePose bakes also hold the
/// offsets they cleared (PoseBefore, PoseAfter, OffsetsCleared, PoseClearOutcome); they are ignored on load.
/// </summary>
internal sealed class AnimationEditJournal
{
    public int Version { get; set; } = 3;
    public Guid Id { get; set; }
    public string State { get; set; } = "Prepared";
    public string Message { get; set; } = "";
    public AnimationBakeRequest Request { get; set; } = null!;
    public string ModDirectory { get; set; } = "";
    public string ModRoot { get; set; } = "";
    public List<AnimationFileChange> Files { get; set; } = [];
    public Dictionary<string, string> ModFileHashes { get; set; } = [];
    // Game paths this edit's timeline uses that an earlier edit added and this one found
    // already in place: undoing that earlier edit would take them away, so it waits for this one.
    public List<string> ReusedFiles { get; set; } = [];
    // Meta manipulations the edit adds to the mod (a placed prop's attach overrides); null for none.
    public AnimationManipulationEdit? Manipulations { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The meta manipulations an edit adds (Penumbra JSON, one per entry of <see cref="Added"/>). A new mod carries them in
/// its default data. In place they go into every option of <see cref="ModRoot"/> that maps <see cref="For"/>, the
/// edited animation, and the ones there with the identifier of one of <see cref="Removed"/> go (a prop put back where
/// the game has it); <see cref="Changes"/> records each before the meta is written, so Undo can take them back.
/// </summary>
internal sealed class AnimationManipulationEdit
{
    public string For { get; set; } = "";
    public string ModDirectory { get; set; } = "";
    public string ModRoot { get; set; } = "";
    public List<string> Added { get; set; } = [];
    public List<string> Removed { get; set; } = [];
    public List<AnimationManipulationChange> Changes { get; set; } = [];
}

/// <summary>
/// One manipulation written into one option of a mod (<paramref name="Container"/>, see ModMetadata): what was
/// written, null when the edit took one out; and the one with the same identifier it replaced or took out there,
/// null when it held none.
/// </summary>
internal sealed record AnimationManipulationChange(string Container, string? Written, string? Replaced);
internal sealed record AnimationFileChange(string GamePath, string Target, string ModDirectory, string ModRoot,
    string RelativePath, string BeforeHash, string AfterHash, string Backup, string Staged,
    // Set only when an in-place rebake landed at a fresh path instead of
    // overwriting the original file. Null for journals written before this
    // existed, which is correctly read as "this file was not renamed".
    string? RenamedFromRelativePath = null,
    // Set for a file the edit added, such as an imported sound: the game path of the
    // edited animation, whose mappings in place gain one for this file too. Undo
    // removes the file and that mapping. Null for every edited file.
    string? AddedFor = null,
    // Set for an added copy whose original the mod maps to different files in different
    // options: the mod-relative file it was made from. Only the options mapping AddedFor to
    // that file map this copy; the copies of the others share its game path. Null maps it
    // wherever AddedFor is, as journals written before this existed did.
    string? AddedForFile = null,
    // Set for an added game path that shares another added file (a prop material's other
    // version folders): the game path of that file, whose Target, RelativePath and Staged
    // this entry carries. Nothing is staged or written for it; commit maps it with its
    // file and undo removes that mapping, never the file. Null for every other entry.
    string? AliasOf = null);
